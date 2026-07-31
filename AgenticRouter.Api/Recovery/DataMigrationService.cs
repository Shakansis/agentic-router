using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticRouter.Api.Recovery;

public sealed record MigrationResult(
  string State,
  int InspectedStores,
  int MigratedStores,
  string? BackupDirectory,
  string? Error
);

public sealed class DataMigrationService
{
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };
  private static readonly string[] Stores =
  [
    "settings.json",
    "workspaces.json",
    "model-organization.json"
  ];
  private readonly string _dataDirectory;
  private readonly SafeModeState _safeMode;

  public DataMigrationService(
    string dataDirectory,
    SafeModeState safeMode
  )
  {
    _dataDirectory = dataDirectory;
    _safeMode = safeMode;
  }

  public async Task<MigrationResult> InitializeAsync(
    bool skipAutomatic,
    CancellationToken cancellationToken
  )
  {
    Directory.CreateDirectory(
      _dataDirectory
    );
    var failurePath = Path.Combine(
      _dataDirectory,
      "migration-failure.json"
    );

    if (File.Exists(
      failurePath
    ))
    {
      _safeMode.Activate(
        "A previous data migration failed. Automatic migration is disabled until the failure record is reviewed."
      );
      return new MigrationResult(
        "previous-failure",
        0,
        0,
        null,
        "Previous migration failure"
      );
    }

    if (skipAutomatic || _safeMode.Enabled)
    {
      return new MigrationResult(
        "skipped",
        0,
        0,
        null,
        null
      );
    }

    var candidates = new List<(
      string Path,
      JsonObject Document
    )>();
    var originals = new Dictionary<string, byte[]>(
      StringComparer.OrdinalIgnoreCase
    );
    var inspected = 0;

    try
    {
      foreach (var name in Stores)
      {
        var path = Path.Combine(
          _dataDirectory,
          name
        );

        if (!File.Exists(
          path
        ))
        {
          continue;
        }

        inspected++;
        var node = JsonNode.Parse(
          await File.ReadAllTextAsync(
            path,
            cancellationToken
          )
        )?.AsObject() ?? throw new InvalidDataException(
          $"{name} is not a JSON object."
        );
        var schema = node["schemaVersion"]?.GetValue<int?>();

        if (schema is > 1)
        {
          throw new InvalidDataException(
            $"{name} uses unsupported schema version {schema}."
          );
        }

        if (schema is null or 0)
        {
          node["schemaVersion"] = 1;
          candidates.Add(
            (
              path,
              node
            )
          );
        }
      }

      if (candidates.Count == 0)
      {
        return new MigrationResult(
          "current",
          inspected,
          0,
          null,
          null
        );
      }

      var backupDirectory = Path.Combine(
        _dataDirectory,
        "migration-backups",
        DateTimeOffset.UtcNow.ToString(
          "yyyyMMdd-HHmmssfff"
        )
      );
      Directory.CreateDirectory(
        backupDirectory
      );

      foreach (var candidate in candidates)
      {
        originals[candidate.Path] = await File.ReadAllBytesAsync(
          candidate.Path,
          cancellationToken
        );
        File.Copy(
          candidate.Path,
          Path.Combine(
            backupDirectory,
            Path.GetFileName(
              candidate.Path
            )
          ),
          false
        );
      }

      foreach (var candidate in candidates)
      {
        var json = candidate.Document.ToJsonString(
          JsonOptions
        ) + "\n";
        _ = JsonNode.Parse(
          json
        ) ?? throw new InvalidDataException(
          "Migrated JSON validation failed."
        );
        var temporary = candidate.Path + $".{Guid.NewGuid():N}.migration.tmp";

        try
        {
          await File.WriteAllTextAsync(
            temporary,
            json,
            cancellationToken
          );
          File.Move(
            temporary,
            candidate.Path,
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
      }

      var result = new MigrationResult(
        "migrated",
        inspected,
        candidates.Count,
        Path.GetRelativePath(
          _dataDirectory,
          backupDirectory
        ).Replace(
          '\\',
          '/'
        ),
        null
      );
      await File.WriteAllTextAsync(
        Path.Combine(
          _dataDirectory,
          "migration-state.json"
        ),
        JsonSerializer.Serialize(
          result,
          JsonOptions
        ) + "\n",
        cancellationToken
      );
      return result;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
      or InvalidDataException
    )
    {
      foreach (var original in originals)
      {
        try
        {
          var temporary = original.Key + $".{Guid.NewGuid():N}.rollback.tmp";
          await File.WriteAllBytesAsync(
            temporary,
            original.Value,
            CancellationToken.None
          );
          File.Move(
            temporary,
            original.Key,
            true
          );
        }
        catch (Exception rollbackException) when (
          rollbackException is IOException
          or UnauthorizedAccessException
        )
        {
        }
      }

      var failure = new
      {
        schemaVersion = 1,
        failedAt = DateTimeOffset.UtcNow,
        error = exception.Message
      };
      await File.WriteAllTextAsync(
        failurePath,
        JsonSerializer.Serialize(
          failure,
          JsonOptions
        ) + "\n",
        CancellationToken.None
      );
      _safeMode.Activate(
        "Data migration failed; original data was retained."
      );
      return new MigrationResult(
        "failed",
        inspected,
        0,
        null,
        exception.Message
      );
    }
  }
}

public sealed class SafeModeMiddleware
{
  private readonly RequestDelegate _next;

  public SafeModeMiddleware(
    RequestDelegate next
  )
  {
    _next = next;
  }

  public async Task InvokeAsync(
    HttpContext context,
    SafeModeState state
  )
  {
    if (
      !state.Enabled
      || HttpMethods.IsGet(
        context.Request.Method
      )
      || context.Request.Path.StartsWithSegments(
        "/api/recovery"
      )
      || !context.Request.Path.StartsWithSegments(
        "/api"
      )
    )
    {
      await _next(
        context
      );
      return;
    }

    await RejectAsync(
      context,
      state
    );
  }

  public static Task RejectAsync(
    HttpContext context,
    SafeModeState state
  )
  {
    context.Response.StatusCode = StatusCodes.Status423Locked;
    return context.Response.WriteAsJsonAsync(
      new
      {
        code = "safe-mode-read-only",
        stage = "safe-mode",
        message = state.Reason
          ?? "Safe mode keeps settings and runtime actions read-only.",
        retryable = false
      }
    );
  }
}
