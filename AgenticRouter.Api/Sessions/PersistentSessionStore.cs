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
    int maximumBytes,
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
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public PersistentSessionStore(
    IWorkspaceProfileStore workspaceStore
  )
  {
    _dataDirectory = workspaceStore.DataDirectory;
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
      var session = await ReadAsync(
        workspaceId,
        sessionId,
        cancellationToken
      );

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
    int maximumBytes,
    CancellationToken cancellationToken
  )
  {
    ValidateId(
      session.WorkspaceId
    );
    ValidateId(
      session.Id
    );
    var json = JsonSerializer.Serialize(
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
    var bytes = System.Text.Encoding.UTF8.GetByteCount(
      json
    );

    if (bytes > maximumBytes)
    {
      throw new WorkspaceProfileException(
        "session-file-too-large",
        "session-persistence",
        "The session exceeds the configured local history size limit.",
        false
      );
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

      return session with
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
