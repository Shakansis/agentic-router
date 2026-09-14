using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Sessions;

public interface IPersistentSessionStore
{
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

public sealed class PersistentSessionStore : IPersistentSessionStore
{
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

      return session with
      {
        StorageBytes = new FileInfo(
          path
        ).Length
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
    var persisted = session;
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
        StorageBytes = bytes
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
        StorageBytes = 0
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
