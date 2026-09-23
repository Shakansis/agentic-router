using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Sessions;

public interface IPersistentSessionStore
{
  Task<IReadOnlyList<ConversationSessionMetadata>> ReadAllMetadataAsync(
    string workspaceId,
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
  long StorageBytes
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
  }

  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
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

    var sessions = new List<ConversationSessionMetadata>();
    foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var sessionId = Path.GetFileNameWithoutExtension(path);
      try
      {
        ValidateId(sessionId);
        await using var stream = File.OpenRead(path);
        var stored = await JsonSerializer.DeserializeAsync<SessionMetadataFile>(
          stream, JsonOptions, cancellationToken
        );
        if (stored is null || stored.SchemaVersion != 1
          || !string.Equals(stored.Id, sessionId, StringComparison.Ordinal)
          || !string.Equals(stored.WorkspaceId, workspaceId, StringComparison.Ordinal)
          || stored.Title is null || stored.State is null || stored.LastInteractionMode is null)
        {
          throw new InvalidDataException("The session metadata is invalid.");
        }

        var recordBytes = stream.Length;
        var storageBytes = recordBytes;
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
          stored.TranscriptId, recordBytes, storageBytes
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

    foreach (var path in Directory.EnumerateFiles(
      directory,
      "*.json",
      SearchOption.TopDirectoryOnly
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var sessionId = Path.GetFileNameWithoutExtension(
        path
      );
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

    if (!File.Exists(
      path
    ))
    {
      return null;
    }

    await _gate.WaitAsync(cancellationToken);
    try
    {
      await using var stream = File.OpenRead(
        path
      );
      var session = await JsonSerializer.DeserializeAsync<ConversationSessionRecord>(
        stream,
        JsonOptions,
        cancellationToken
      );

      if (
        session is null
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

      var storageBytes = new FileInfo(path).Length;
      if (session.TranscriptId is { } transcriptId)
      {
        var transcriptPath = TranscriptPath(workspaceId, sessionId, transcriptId);
        await using var transcript = File.OpenRead(transcriptPath);
        await using var decompressed = new GZipStream(transcript, CompressionMode.Decompress);
        var messages = await JsonSerializer.DeserializeAsync<ChatMessage[]>(
          decompressed, JsonOptions, cancellationToken
        ) ?? throw new InvalidDataException("The saved transcript is empty.");
        session = session with { ContextMessages = session.Messages, Messages = messages };
        storageBytes += transcript.Length;
      }
      return session with
      {
        StorageBytes = storageBytes
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
    var persisted = session with { TranscriptId = null, ContextMessages = null, PresentationOffset = 0 };
    byte[]? transcriptBytes = null;
    var json = Serialize(
      persisted
    );
    var bytes = System.Text.Encoding.UTF8.GetByteCount(
      json
    );
    if (bytes >= compactionThresholdBytes)
    {
      var compaction = PersistentSessionCompactor.Compact(
        persisted,
        compactionTargetBytes,
        MeasureBytes
      );
      persisted = compaction.Session;
      transcriptBytes = JsonSerializer.SerializeToUtf8Bytes(session.Messages, JsonOptions);
      persisted = persisted with
      {
        TranscriptId = Convert.ToHexStringLower(SHA256.HashData(transcriptBytes))
      };
      json = Serialize(
        persisted
      );
      bytes = System.Text.Encoding.UTF8.GetByteCount(
        json
      );
      _logger.LogInformation(
        "Compacted persisted session {SessionId} from {BeforeBytes} to {AfterBytes} bytes at the {ThresholdBytes}-byte threshold.",
        session.Id,
        compaction.BeforeBytes,
        bytes,
        compactionThresholdBytes
      );
      if (bytes >= compactionThresholdBytes)
      {
        _logger.LogWarning(
          "Persisted session {SessionId} remains at {Bytes} bytes after semantic compaction; the write is retained because the threshold is not a hard failure limit.",
          session.Id,
          bytes
        );
      }
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
        if (transcriptBytes is not null && persisted.TranscriptId is { } transcriptId)
        {
          var transcriptPath = TranscriptPath(session.WorkspaceId, session.Id, transcriptId);
          if (!File.Exists(transcriptPath))
          {
            var transcriptTemporary = temporary + ".gz";
            try
            {
              await using (var output = File.Create(transcriptTemporary))
              {
                await using var compressed = new GZipStream(output, CompressionLevel.Optimal);
                await compressed.WriteAsync(transcriptBytes, cancellationToken);
              }
              File.Move(transcriptTemporary, transcriptPath, true);
            }
            finally
            {
              if (File.Exists(transcriptTemporary)) File.Delete(transcriptTemporary);
            }
          }
          bytes += checked((int)new FileInfo(transcriptPath).Length);
        }
        await File.WriteAllTextAsync(
          temporary,
          json,
          cancellationToken
        );
        File.Move(
          temporary,
          path,
          true
        );
        // The JSON pointer is committed only after its lossless transcript exists.
        // Retain the previous transcript on a failed write.
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
        StorageBytes = bytes,
        Messages = session.Messages,
        ContextMessages = persisted.TranscriptId is null ? null : persisted.Messages
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

  private static int MeasureBytes(
    ConversationSessionRecord session
  )
  {
    return System.Text.Encoding.UTF8.GetByteCount(
      Serialize(
        session
      )
    );
  }

  private static string Serialize(
    ConversationSessionRecord session
  )
  {
    return JsonSerializer.Serialize(
      session with
      {
        StorageBytes = 0,
        ContextMessages = null,
        PresentationOffset = 0
      },
      JsonOptions
    ).Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ) + "\n";
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
