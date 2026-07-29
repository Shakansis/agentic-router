using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.WorkspaceProfiles;

public sealed record WorkspaceProfileData
{
  public string Id { get; init; } = string.Empty;

  public string Name { get; init; } = string.Empty;

  public string Path { get; init; } = string.Empty;

  public bool Active { get; init; }

  public bool HistoryEnabled { get; init; }

  public DateTimeOffset CreatedAt { get; init; }

  public DateTimeOffset LastOpenedAt { get; init; }

  public ProjectProfile? ProjectProfile { get; init; }

  public string? DefaultModel { get; init; }

  public ValidationProfileSettings? ValidationProfile { get; init; }
}

public sealed record WorkspaceProfileDocument
{
  public int SchemaVersion { get; init; } = 1;

  public IReadOnlyList<WorkspaceProfileData> Profiles { get; init; } = [];
}

public interface IWorkspaceProfileStore
{
  string DataDirectory { get; }

  Task<WorkspaceProfileDocument> ReadAsync(
    CancellationToken cancellationToken
  );

  Task WriteAsync(
    WorkspaceProfileDocument document,
    CancellationToken cancellationToken
  );
}

public sealed class WorkspaceProfileStore : IWorkspaceProfileStore
{
  private const long MaximumFileBytes = 2_097_152;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };

  private readonly string _path;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public WorkspaceProfileStore(
    string dataDirectory
  )
  {
    DataDirectory = dataDirectory;
    _path = Path.Combine(
      dataDirectory,
      "workspaces.json"
    );
  }

  public string DataDirectory { get; }

  public async Task<WorkspaceProfileDocument> ReadAsync(
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      Directory.CreateDirectory(
        DataDirectory
      );

      if (!File.Exists(
        _path
      ))
      {
        foreach (var temporary in Directory.EnumerateFiles(
          DataDirectory,
          ".workspaces-*.tmp",
          SearchOption.TopDirectoryOnly
        ).OrderByDescending(
          File.GetLastWriteTimeUtc
        ))
        {
          try
          {
            var recoveredJson = await File.ReadAllTextAsync(
              temporary,
              cancellationToken
            );
            var recovered = JsonSerializer.Deserialize<WorkspaceProfileDocument>(
              recoveredJson,
              JsonOptions
            );

            if (recovered?.SchemaVersion == 1)
            {
              File.Move(
                temporary,
                _path,
                true
              );
              break;
            }
          }
          catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
          )
          {
          }
        }

        if (!File.Exists(
          _path
        ))
        {
          return new WorkspaceProfileDocument();
        }
      }

      var info = new FileInfo(
        _path
      );

      if (info.Length > MaximumFileBytes)
      {
        throw new WorkspaceProfileException(
          "workspace-file-too-large",
          "workspace-profile-storage",
          "The workspace profile file exceeds the safe size limit.",
          false
        );
      }

      await using var stream = File.OpenRead(
        _path
      );
      var document = await JsonSerializer.DeserializeAsync<WorkspaceProfileDocument>(
        stream,
        JsonOptions,
        cancellationToken
      ) ?? throw new InvalidDataException(
        "The workspace profile file contains no document."
      );

      if (document.SchemaVersion != 1)
      {
        throw new InvalidDataException(
          "The workspace profile schema version is not supported."
        );
      }

      if (
        document.Profiles.Count > 100
        || (
          document.Profiles.Count > 0
          && document.Profiles.Count(
            profile => profile.Active
          ) != 1
        )
      )
      {
        throw new InvalidDataException(
          "The workspace profile collection is invalid."
        );
      }

      var ids = new HashSet<string>(
        StringComparer.Ordinal
      );

      foreach (var profile in document.Profiles)
      {
        if (
          !IsSafeId(
            profile.Id
          )
          || !ids.Add(
            profile.Id
          )
          || string.IsNullOrWhiteSpace(
            profile.Name
          )
          || profile.Name.Length > 80
          || string.IsNullOrWhiteSpace(
            profile.Path
          )
        )
        {
          throw new InvalidDataException(
            "A workspace profile entry is invalid."
          );
        }
      }

      return document;
    }
    catch (WorkspaceProfileException)
    {
      throw;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
      or InvalidDataException
    )
    {
      throw new WorkspaceProfileException(
        "workspace-profile-storage-invalid",
        "workspace-profile-storage",
        "Workspace profiles could not be read safely.",
        true,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task WriteAsync(
    WorkspaceProfileDocument document,
    CancellationToken cancellationToken
  )
  {
    var json = JsonSerializer.Serialize(
      document,
      JsonOptions
    ).Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ) + "\n";

    if (json.Length > MaximumFileBytes)
    {
      throw new WorkspaceProfileException(
        "workspace-file-too-large",
        "workspace-profile-storage",
        "The workspace profile file exceeds the safe size limit.",
        false
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      Directory.CreateDirectory(
        DataDirectory
      );
      var temporaryPath = Path.Combine(
        DataDirectory,
        $".workspaces-{Guid.NewGuid():N}.tmp"
      );

      try
      {
        await File.WriteAllTextAsync(
          temporaryPath,
          json,
          cancellationToken
        );
        File.Move(
          temporaryPath,
          _path,
          true
        );
      }
      finally
      {
        if (File.Exists(
          temporaryPath
        ))
        {
          File.Delete(
            temporaryPath
          );
        }
      }
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new WorkspaceProfileException(
        "workspace-profile-persistence-failed",
        "workspace-profile-storage",
        "Workspace profiles could not be saved safely.",
        true,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private static bool IsSafeId(
    string id
  )
  {
    return id.Length is > 0 and <= 64
      && id.All(
        character => char.IsAsciiLetterOrDigit(
          character
        ) || character is '-' or '_'
      );
  }
}

public sealed class WorkspaceProfileException : Exception
{
  public WorkspaceProfileException(
    string code,
    string stage,
    string message,
    bool retryable,
    Exception? innerException = null
  )
    : base(
      message,
      innerException
    )
  {
    Code = code;
    Stage = stage;
    Retryable = retryable;
  }

  public string Code { get; }

  public string Stage { get; }

  public bool Retryable { get; }
}
