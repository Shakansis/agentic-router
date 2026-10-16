using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Sessions;

public interface IPersistentSessionStore
{
  Task<long> CompressAsync(string workspaceId, string sessionId, CancellationToken cancellationToken,
    Action<long, string>? progress = null, Action<HostFileTiming>? completed = null);
  Task<IReadOnlyList<ConversationSessionMetadata>> ReadAllMetadataAsync(
    string workspaceId,
    CancellationToken cancellationToken
  );

  Task<string?> FindLatestChangingSessionIdAsync(
    string workspaceId,
    ConversationSessionRecord openedSession,
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<ConversationSessionRecord>> ReadAllAsync(
    string workspaceId,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord?> ReadAsync(
    string workspaceId,
    string sessionId,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord> WriteAsync(
    ConversationSessionRecord session,
    int compactionThresholdBytes,
    int compactionTargetBytes,
    CancellationToken cancellationToken
  );

  Task DeleteAsync(
    string workspaceId,
    string sessionId,
    CancellationToken cancellationToken
  );

  string GetRelativePath(
    string workspaceId,
    string sessionId
  );
}

public sealed record ConversationSessionMetadata(
  string Id,
  string WorkspaceId,
  string Title,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt,
  bool Archived,
  string State,
  string LastInteractionMode,
  string? SelectedModel,
  bool Interrupted,
  bool Pinned,
  DateTimeOffset? PinnedAt,
  bool HasSessionSummary,
  string? PreferredModelProfileId,
  string LastApprovalPolicy,
  string SelectedHarness,
  string LastExecutionStrategy,
  string? TranscriptId,
  long RecordBytes,
  long StorageBytes,
  bool HasFileChanges = false,
  bool Compressed = false
);

public sealed class PersistentSessionStore : IPersistentSessionStore
{
  private sealed record SessionMetadataFile
  {
    public int SchemaVersion { get; init; }
    public string? Id { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool Archived { get; init; }
    public string? State { get; init; }
    public string? LastInteractionMode { get; init; }
    public string? SelectedModel { get; init; }
    public bool Interrupted { get; init; }
    public bool Pinned { get; init; }
    public DateTimeOffset? PinnedAt { get; init; }
    public JsonElement? SessionSummary { get; init; }
    public string? PreferredModelProfileId { get; init; }
    public string? LastApprovalPolicy { get; init; }
    public string? SelectedHarness { get; init; }
    public string? LastExecutionStrategy { get; init; }
    public string? TranscriptId { get; init; }
    public long RecordLength { get; init; }
    public long RecordWriteTicks { get; init; }
    public bool HasFileChanges { get; init; }
  }

  private sealed record SessionChangeCandidateFile
  {
    public int SchemaVersion { get; init; }
    public string? Id { get; init; }
    public string? WorkspaceId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<SessionChangeRollbackFile>? ExecutionRollbacks { get; init; }
  }

  private sealed record SessionChangeRollbackFile
  {
    public JsonElement? Files { get; init; }
  }

  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true,
    NewLine = "\n"
  };

  private readonly string _dataDirectory;
  private readonly ILogger<PersistentSessionStore> _logger;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public PersistentSessionStore(
    IWorkspaceProfileStore workspaceStore,
    ILogger<PersistentSessionStore> logger
  )
  {
    _dataDirectory = workspaceStore.DataDirectory;
    _logger = logger;
  }

  public async Task<IReadOnlyList<ConversationSessionMetadata>> ReadAllMetadataAsync(
    string workspaceId,
    CancellationToken cancellationToken
  )
  {
    ValidateId(workspaceId);
    var directory = SessionDirectory(workspaceId);
    if (!Directory.Exists(directory)) return [];

    await _gate.WaitAsync(cancellationToken);
    try
    {
      var sessions = new List<ConversationSessionMetadata>();
      foreach (var path in RecordPaths(directory))
      {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionId = RecordId(path);
        try
        {
          ValidateId(sessionId);
          var stored = await ReadMetadataAsync(path, cancellationToken);
          if (stored is null || stored.SchemaVersion != 1
            || !string.Equals(stored.Id, sessionId, StringComparison.Ordinal)
            || !string.Equals(stored.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || stored.Title is null || stored.State is null || stored.LastInteractionMode is null)
          {
            throw new InvalidDataException("The session metadata is invalid.");
          }

          var recordBytes = new FileInfo(path).Length;
          var storageBytes = recordBytes + (File.Exists(MetadataPath(path)) ? new FileInfo(MetadataPath(path)).Length : 0);
          if (stored.TranscriptId is { } transcriptId)
          {
            storageBytes += new FileInfo(TranscriptPath(workspaceId, sessionId, transcriptId)).Length;
          }
          sessions.Add(new ConversationSessionMetadata(
            sessionId, workspaceId, stored.Title, stored.CreatedAt, stored.UpdatedAt,
            stored.Archived, stored.State, stored.LastInteractionMode, stored.SelectedModel,
            stored.Interrupted, stored.Pinned, stored.PinnedAt,
            stored.SessionSummary is { ValueKind: JsonValueKind.Object },
            stored.PreferredModelProfileId, stored.LastApprovalPolicy ?? "auto",
            stored.SelectedHarness ?? "native", stored.LastExecutionStrategy ?? "auto",
            stored.TranscriptId, recordBytes, storageBytes, stored.HasFileChanges, path.EndsWith(".gz", StringComparison.Ordinal)
          ));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
          or JsonException or InvalidDataException
          or WorkspaceProfileException { Code: "session-file-invalid" })
        {
          _logger.LogWarning(exception,
            "Skipping invalid persisted session {SessionId} in workspace {WorkspaceId}; other sessions remain available.",
            sessionId, workspaceId);
        }
      }
      return sessions;
    }
    finally { _gate.Release(); }
  }

  public async Task<string?> FindLatestChangingSessionIdAsync(
    string workspaceId,
    ConversationSessionRecord openedSession,
    CancellationToken cancellationToken
  )
  {
    ValidateId(workspaceId);
    var directory = SessionDirectory(workspaceId);
    if (!Directory.Exists(directory)) return null;

    var candidates = new List<(string Id, DateTimeOffset UpdatedAt)>();
    foreach (var path in RecordPaths(directory))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var sessionId = RecordId(path);
      try
      {
        ValidateId(sessionId);
        await using var stream = OpenRecord(path);
        var stored = await JsonSerializer.DeserializeAsync<SessionChangeCandidateFile>(
          stream, JsonOptions, cancellationToken
        );
        if (stored is null || stored.SchemaVersion != 1
          || !string.Equals(stored.Id, sessionId, StringComparison.Ordinal)
          || !string.Equals(stored.WorkspaceId, workspaceId, StringComparison.Ordinal))
        {
          throw new InvalidDataException("The session record identity is invalid.");
        }
        if (stored.ExecutionRollbacks?.Any(rollback =>
          rollback.Files is { ValueKind: JsonValueKind.Array } files
          && files.GetArrayLength() > 0) == true)
        {
          candidates.Add((sessionId, stored.UpdatedAt));
        }
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or JsonException or InvalidDataException or WorkspaceProfileException
        or InvalidOperationException)
      {
        _logger.LogWarning(exception,
          "Skipping invalid persisted session {SessionId} in workspace {WorkspaceId}; other sessions remain available.",
          sessionId, workspaceId);
      }
    }

    foreach (var candidate in candidates.OrderByDescending(item => item.UpdatedAt))
    {
      ConversationSessionRecord? session;
      try
      {
        session = string.Equals(candidate.Id, openedSession.Id, StringComparison.Ordinal)
          ? openedSession
          : await ReadAsync(workspaceId, candidate.Id, cancellationToken);
      }
      catch (WorkspaceProfileException exception) when (exception.Code == "session-file-invalid")
      {
        _logger.LogWarning(exception,
          "Skipping invalid persisted session {SessionId} in workspace {WorkspaceId}; other sessions remain available.",
          candidate.Id, workspaceId);
        continue;
      }
      if (session?.ExecutionRollbacks?.Any(rollback => rollback.Files.Count > 0) == true)
        return candidate.Id;
    }
    return null;
  }

  public async Task<IReadOnlyList<ConversationSessionRecord>> ReadAllAsync(
    string workspaceId,
    CancellationToken cancellationToken
  )
  {
    var directory = SessionDirectory(
      workspaceId
    );

    if (!Directory.Exists(
      directory
    ))
    {
      return [];
    }

    var sessions = new List<ConversationSessionRecord>();

    foreach (var path in RecordPaths(directory))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var sessionId = RecordId(path);
      ConversationSessionRecord? session;
      try
      {
        session = await ReadAsync(
          workspaceId,
          sessionId,
          cancellationToken
        );
      }
      catch (WorkspaceProfileException exception) when (
        exception.Code == "session-file-invalid"
      )
      {
        _logger.LogWarning(
          exception,
          "Skipping invalid persisted session {SessionId} in workspace {WorkspaceId}; other sessions remain available.",
          sessionId,
          workspaceId
        );
        continue;
      }

      if (session is not null)
      {
        sessions.Add(
          session
        );
      }
    }

    return sessions;
  }

  public async Task<ConversationSessionRecord?> ReadAsync(
    string workspaceId,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    ValidateId(
      workspaceId
    );
    ValidateId(
      sessionId
    );
    var path = SessionPath(
      workspaceId,
      sessionId
    );

    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (!File.Exists(path) && File.Exists(path + ".gz")) path += ".gz";
      if (!File.Exists(path)) return null;
      await using var stream = OpenRecord(path);
      var session = await JsonSerializer.DeserializeAsync<ConversationSessionRecord>(
        stream,
        JsonOptions,
        cancellationToken
      );

      if (
        session is null
        || session.Messages is null
        || session.SchemaVersion != 1
        || !string.Equals(
          session.Id,
          sessionId,
          StringComparison.Ordinal
        )
        || !string.Equals(
          session.WorkspaceId,
          workspaceId,
          StringComparison.Ordinal
        )
      )
      {
        throw new InvalidDataException(
          "The session record identity is invalid."
        );
      }

      // Review data is optional in configuration backups and older records.
      session = session with
      {
        ExecutionReviews = session.ExecutionReviews ?? [],
        Messages = session.Messages.Select(message => message with
        {
          Timeline = PersistentSessionService.CoalesceTimeline(message.Timeline)
        }).ToArray()
      };
      var storageBytes = new FileInfo(path).Length + (File.Exists(MetadataPath(path)) ? new FileInfo(MetadataPath(path)).Length : 0);
      if (session.TranscriptId is { } transcriptId)
      {
        var transcriptPath = TranscriptPath(workspaceId, sessionId, transcriptId);
        await using var transcript = File.OpenRead(transcriptPath);
        await using var decompressed = new GZipStream(transcript, CompressionMode.Decompress);
        var messages = await JsonSerializer.DeserializeAsync<ChatMessage[]>(
          decompressed, JsonOptions, cancellationToken
        ) ?? throw new InvalidDataException("The saved transcript is empty.");
        session = session with
        {
          ContextMessages = session.Messages,
          Messages = messages.Select(message => message with
          {
            Timeline = PersistentSessionService.CoalesceTimeline(message.Timeline)
          }).ToArray()
        };
        storageBytes += transcript.Length;
      }
      return session with
      {
        StorageBytes = storageBytes,
        ContextMessages = session.ContextProjectionTargetBytes > 0
          ? (await PersistentSessionCompactor.CompactAsync(session, session.ContextProjectionTargetBytes, MeasureBytesAsync, cancellationToken)).Session.Messages
          : session.ContextMessages
      };
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
      or InvalidDataException
    )
    {
      throw new WorkspaceProfileException(
        "session-file-invalid",
        "session-storage",
        "The saved session record is invalid or inaccessible.",
        false,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<ConversationSessionRecord> WriteAsync(
    ConversationSessionRecord session,
    int compactionThresholdBytes,
    int compactionTargetBytes,
    CancellationToken cancellationToken
  )
  {
    ValidateId(
      session.WorkspaceId
    );
    ValidateId(
      session.Id
    );
    // Exact history is durable. Context fitting is an inference projection only.
    var persisted = session with
    {
      TranscriptId = null,
      ContextMessages = null,
      PresentationOffset = 0,
      ContextProjectionTargetBytes = 0,
      ContextTruncated = session.ContextProjectionTargetBytes > 0 ? false : session.ContextTruncated
    };
    var bytes = await MeasureBytesAsync(persisted, cancellationToken);
    IReadOnlyList<ChatMessage>? context = null;
    long processedBytes = bytes;
    if (bytes >= compactionThresholdBytes)
    {
      context = (await PersistentSessionCompactor.CompactAsync(persisted, compactionTargetBytes, MeasureBytesAsync, cancellationToken)).Session.Messages;
      persisted = persisted with { ContextProjectionTargetBytes = compactionTargetBytes, ContextTruncated = true };
      bytes = await MeasureBytesAsync(persisted, cancellationToken);
      processedBytes = bytes;
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var directory = SessionDirectory(
        session.WorkspaceId
      );
      Directory.CreateDirectory(
        directory
      );
      var path = SessionPath(
        session.WorkspaceId,
        session.Id
      );
      var temporary = Path.Combine(
        directory,
        $".{session.Id}-{Guid.NewGuid():N}.tmp"
      );

      try
      {
        await using (var output = File.Create(temporary))
        {
          await JsonSerializer.SerializeAsync(output, CanonicalRecord(persisted), JsonOptions, cancellationToken);
          await output.WriteAsync(new byte[] { 10 }, cancellationToken);
        }
        File.Move(
          temporary,
          path,
          true
        );
        try
        {
          if (File.Exists(path + ".gz")) File.Delete(path + ".gz");
          if (bytes >= compactionThresholdBytes)
            await CompressRecordCoreAsync(path, cancellationToken, (count, _) => processedBytes += count);
          await WriteMetadataAsync(persisted, File.Exists(path) ? path : path + ".gz", cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
          // The exact canonical record already committed; compression and its index
          // must not turn a successful history write into a false persistence failure.
          _logger.LogWarning(exception, "History committed, but lossless compression or metadata indexing was unavailable.");
        }
        // Legacy transcripts are removed only after the complete canonical record commits.
        foreach (var oldTranscript in Directory.EnumerateFiles(directory, $"{session.Id}.*.history.gz"))
        {
          if (persisted.TranscriptId is null
            || !string.Equals(oldTranscript, TranscriptPath(session.WorkspaceId, session.Id, persisted.TranscriptId), StringComparison.Ordinal))
          {
            try { File.Delete(oldTranscript); }
            catch (IOException exception) { _logger.LogWarning(exception, "Could not remove an obsolete conversation transcript."); }
            catch (UnauthorizedAccessException exception) { _logger.LogWarning(exception, "Could not remove an obsolete conversation transcript."); }
          }
        }
      }
      finally
      {
        if (File.Exists(
          temporary
        ))
        {
          File.Delete(
            temporary
          );
        }
      }

      return persisted with
      {
        StorageBytes = new FileInfo(File.Exists(path) ? path : path + ".gz").Length,
        Messages = session.Messages,
        ContextMessages = context,
        LastHistoryWriteBytes = processedBytes
      };
    }
    catch (WorkspaceProfileException)
    {
      throw;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new WorkspaceProfileException(
        "session-persistence-failed",
        "session-persistence",
        "The conversation remains visible, but its local history could not be saved.",
        true,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private static ConversationSessionRecord CanonicalRecord(ConversationSessionRecord session) => session with
  { StorageBytes = 0, ContextMessages = null, PresentationOffset = 0 };

  private static async Task<long> MeasureBytesAsync(ConversationSessionRecord session, CancellationToken token)
  {
    using var output = new ByteCountStream();
    await JsonSerializer.SerializeAsync(output, CanonicalRecord(session), JsonOptions, token);
    return output.Length + 1;
  }

  // Used by the inference projection only. Count streamed UTF-8 bytes without
  // materializing a complete UTF-8 buffer and a second UTF-16 JSON document.
  private sealed class ByteCountStream : Stream
  {
    private long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public override void Write(byte[] buffer, int offset, int count) => _length += count;
    public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); _length += buffer.Length; return ValueTask.CompletedTask; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

  public async Task DeleteAsync(
    string workspaceId,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    ValidateId(
      workspaceId
    );
    ValidateId(
      sessionId
    );
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var path = SessionPath(
        workspaceId,
        sessionId
      );

      if (File.Exists(
        path
      ))
      {
        File.Delete(
          path
        );
      }
      if (File.Exists(path + ".gz")) File.Delete(path + ".gz");
      if (File.Exists(path + ".index")) File.Delete(path + ".index");
      var directory = SessionDirectory(workspaceId);
      if (Directory.Exists(directory))
      {
        foreach (var transcript in Directory.EnumerateFiles(directory, $"{sessionId}.*.history.gz"))
        {
          File.Delete(transcript);
        }
      }
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new WorkspaceProfileException(
        "session-deletion-failed",
        "session-deletion",
        "The session history record could not be deleted.",
        true,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<long> CompressAsync(string workspaceId, string sessionId, CancellationToken cancellationToken,
    Action<long, string>? progress = null, Action<HostFileTiming>? completed = null)
  {
    ValidateId(workspaceId);
    ValidateId(sessionId);
    await _gate.WaitAsync(cancellationToken);
    try
    {
      var path = SessionPath(workspaceId, sessionId);
      var metadata = File.Exists(path) ? await ReadMetadataAsync(path, cancellationToken) : null;
      var saved = await CompressRecordCoreAsync(path, cancellationToken, progress, completed);
      if (saved > 0 && metadata is not null)
      {
        var info = new FileInfo(path + ".gz");
        await HistoryContentStore.WriteJsonAsync(MetadataPath(path), metadata with
        { RecordLength = info.Length, RecordWriteTicks = info.LastWriteTimeUtc.Ticks }, cancellationToken);
      }
      return saved;
    }
    finally { _gate.Release(); }
  }

  private static async Task<long> CompressRecordCoreAsync(string path, CancellationToken token,
    Action<long, string>? progress = null, Action<HostFileTiming>? completed = null)
  {
    if (!File.Exists(path)) return 0;
    var temporary = path + $".{Guid.NewGuid():N}.tmp";
    try
    {
      var size = new FileInfo(path).Length;
      byte[] digest;
      var clock = System.Diagnostics.Stopwatch.StartNew();
      await using (var source = File.OpenRead(path))
      {
        digest = await SHA256.HashDataAsync(source, token);
        progress?.Invoke(source.Length, "Checking historical chat integrity");
        completed?.Invoke(new("Checking historical chat integrity", clock.ElapsedMilliseconds, source.Length));
        clock.Restart();
        source.Position = 0;
        await using var output = File.Create(temporary);
        await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
        await source.CopyToAsync(gzip, token);
        progress?.Invoke(source.Length, "Compacting historical chat");
      }
      completed?.Invoke(new("Compacting historical chat", clock.ElapsedMilliseconds, size));
      clock.Restart();
      await using (var source = File.OpenRead(temporary))
      await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
      {
        var restored = await SHA256.HashDataAsync(gzip, token);
        if (!digest.AsSpan().SequenceEqual(restored))
          throw new InvalidDataException("Compressed session integrity verification failed.");
      }
      progress?.Invoke(size, "Verifying compressed historical chat");
      completed?.Invoke(new("Verifying compressed historical chat", clock.ElapsedMilliseconds, size));
      var compressedSize = new FileInfo(temporary).Length;
      if (compressedSize >= size) return 0;
      File.Move(temporary, path + ".gz", true);
      try { File.Delete(path); }
      catch { File.Delete(path + ".gz"); throw; }
      return size - compressedSize;
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }

  private static IEnumerable<string> RecordPaths(string directory) =>
    Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
      .Concat(Directory.EnumerateFiles(directory, "*.json.gz", SearchOption.TopDirectoryOnly))
      .DistinctBy(RecordId);
  private static string RecordId(string path) => Path.GetFileName(path).Replace(".json.gz", "", StringComparison.Ordinal)
    .Replace(".json", "", StringComparison.Ordinal);
  private static Stream OpenRecord(string path) => path.EndsWith(".gz", StringComparison.Ordinal)
    ? new GZipStream(File.OpenRead(path), CompressionMode.Decompress) : File.OpenRead(path);

  private static string MetadataPath(string path) => (path.EndsWith(".gz", StringComparison.Ordinal) ? path[..^3] : path) + ".index";
  private async Task<SessionMetadataFile?> ReadMetadataAsync(string path, CancellationToken token)
  {
    var info = new FileInfo(path);
    var index = MetadataPath(path);
    if (File.Exists(index))
    {
      try
      {
        await using var input = File.OpenRead(index);
        var cached = await JsonSerializer.DeserializeAsync<SessionMetadataFile>(input, JsonOptions, token);
        if (cached?.RecordLength == info.Length && cached.RecordWriteTicks == info.LastWriteTimeUtc.Ticks) return cached;
      }
      catch (Exception exception) when (exception is JsonException or IOException)
      { _logger.LogWarning(exception, "History metadata cache is invalid; reading the canonical record."); }
    }
    await using var stream = OpenRecord(path);
    return await JsonSerializer.DeserializeAsync<SessionMetadataFile>(stream, JsonOptions, token);
  }

  private static Task WriteMetadataAsync(ConversationSessionRecord session, string path, CancellationToken token)
  {
    var info = new FileInfo(path);
    return HistoryContentStore.WriteJsonAsync(MetadataPath(path), new SessionMetadataFile
    {
      SchemaVersion = session.SchemaVersion,
      Id = session.Id,
      WorkspaceId = session.WorkspaceId,
      Title = session.Title,
      CreatedAt = session.CreatedAt,
      UpdatedAt = session.UpdatedAt,
      Archived = session.Archived,
      State = session.State,
      LastInteractionMode = session.LastInteractionMode,
      SelectedModel = session.SelectedModel,
      Interrupted = session.Interrupted,
      Pinned = session.Pinned,
      PinnedAt = session.PinnedAt,
      PreferredModelProfileId = session.PreferredModelProfileId,
      LastApprovalPolicy = session.LastApprovalPolicy,
      SelectedHarness = session.SelectedHarness,
      LastExecutionStrategy = session.LastExecutionStrategy,
      TranscriptId = session.TranscriptId,
      SessionSummary = session.SessionSummary is null ? null : JsonSerializer.SerializeToElement(new { available = true }),
      RecordLength = info.Length,
      RecordWriteTicks = info.LastWriteTimeUtc.Ticks,
      HasFileChanges = session.ExecutionRollbacks?.Any(item => item.Files.Count > 0) == true
    }, token);
  }

  public string GetRelativePath(
    string workspaceId,
    string sessionId
  )
  {
    ValidateId(
      workspaceId
    );
    ValidateId(
      sessionId
    );
    return Path.Combine(
      "workspaces",
      workspaceId,
      "sessions",
      $"{sessionId}.json"
    ).Replace(
      '\\',
      '/'
    );
  }

  private string SessionDirectory(
    string workspaceId
  )
  {
    return Path.Combine(
      _dataDirectory,
      "workspaces",
      workspaceId,
      "sessions"
    );
  }

  private string SessionPath(
    string workspaceId,
    string sessionId
  )
  {
    return Path.Combine(
      SessionDirectory(
        workspaceId
      ),
      $"{sessionId}.json"
    );
  }

  private string TranscriptPath(string workspaceId, string sessionId, string transcriptId)
  {
    if (transcriptId.Length != 64 || transcriptId.Any(character => !char.IsAsciiHexDigit(character)))
    {
      throw new InvalidDataException("The saved transcript identifier is invalid.");
    }
    return Path.Combine(SessionDirectory(workspaceId), $"{sessionId}.{transcriptId}.history.gz");
  }

  private static void ValidateId(
    string id
  )
  {
    if (
      id.Length is < 1 or > 64
      || id.Any(
        character => !char.IsAsciiLetterOrDigit(
          character
        ) && character is not '-' and not '_'
      )
    )
    {
      throw new WorkspaceProfileException(
        "session-file-invalid",
        "session-storage",
        "The local session identifier is invalid.",
        false
      );
    }
  }
}
