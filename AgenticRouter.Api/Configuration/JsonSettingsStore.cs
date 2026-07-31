using System.Text.Json;

namespace AgenticRouter.Api.Configuration;

public sealed class JsonSettingsStore : ISettingsStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
  };

  private readonly string _dataDirectory;
  private readonly string _settingsPath;
  private readonly ISettingsValidator _validator;
  private readonly ILogger<JsonSettingsStore> _logger;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public JsonSettingsStore(
    string dataDirectory,
    ISettingsValidator validator,
    ILogger<JsonSettingsStore> logger
  )
  {
    _dataDirectory = dataDirectory;
    _settingsPath = Path.Combine(
      dataDirectory,
      "settings.json"
    );
    _validator = validator;
    _logger = logger;
  }

  public async Task<ApplicationSettings> GetAsync(
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      Directory.CreateDirectory(
        _dataDirectory
      );

      if (!File.Exists(
        _settingsPath
      ))
      {
        var defaults = SettingsDefaults.Create();
        await WriteValidatedAsync(
          defaults,
          cancellationToken
        );
        return defaults;
      }

      var json = await File.ReadAllTextAsync(
        _settingsPath,
        cancellationToken
      );
      var settings = JsonSerializer.Deserialize<ApplicationSettings>(
        json,
        JsonOptions
      ) ?? throw new InvalidDataException(
        "The settings file contains no settings object."
      );
      using var document = JsonDocument.Parse(
        json
      );
      var hasCoordinatorModel = document.RootElement.TryGetProperty(
        "coordinatorModel",
        out _
      );
      var contextElement = document.RootElement.TryGetProperty(
        "context",
        out var savedContext
      )
        ? savedContext
        : default;
      var hasProviderContext = contextElement.ValueKind == JsonValueKind.Object
        && contextElement.TryGetProperty(
          "providerContextTokens",
          out _
        );
      var executionElement = document.RootElement.TryGetProperty(
        "execution",
        out var savedExecution
      )
        ? savedExecution
        : default;
      var hasMaxRecoveryAttempts = executionElement.ValueKind == JsonValueKind.Object
        && executionElement.TryGetProperty(
          "maxRecoveryAttemptsPerTurn",
          out _
        );

      if (!hasCoordinatorModel)
      {
        settings = settings with
        {
          CoordinatorModel = settings.RouterModel
        };
      }

      if (
        !hasProviderContext
        && settings.Context.DefaultContextTokens == 8_192
        && settings.Context.ReservedResponseTokens == 2_048
      )
      {
        settings = settings with
        {
          Context = settings.Context with
          {
            DefaultContextTokens = 32_768,
            ReservedResponseTokens = 4_096
          }
        };
      }

      if (!hasMaxRecoveryAttempts)
      {
        settings = settings with
        {
          Execution = settings.Execution with
          {
            MaxRecoveryAttemptsPerTurn = 5,
            ResidentCoordinatorPlanningFailuresBeforeFailure = Math.Max(
              5,
              settings.Execution.ResidentCoordinatorPlanningFailuresBeforeFailure
            ),
            MaxConsecutiveToolFailures = Math.Max(
              5,
              settings.Execution.MaxConsecutiveToolFailures
            )
          }
        };
      }

      var errors = _validator.Validate(
        settings
      );

      if (errors.Count > 0)
      {
        throw new InvalidDataException(
          "The saved settings file is invalid."
        );
      }

      var requiresRewrite = !document.RootElement.TryGetProperty(
        "runtime",
        out _
      ) || !document.RootElement.TryGetProperty(
        "context",
        out _
      ) || !document.RootElement.TryGetProperty(
        "trustedWorkspacePath",
        out _
      ) || !document.RootElement.TryGetProperty(
        "execution",
        out _
      ) || !document.RootElement.TryGetProperty(
        "projectAwareness",
        out _
      ) || !document.RootElement.TryGetProperty(
        "validationProfile",
        out _
      ) || !document.RootElement.TryGetProperty(
        "sessionHistory",
        out _
      ) || !document.RootElement.TryGetProperty(
        "gitDelivery",
        out _
      ) || !document.RootElement.TryGetProperty(
        "usage",
        out _
      ) || !document.RootElement.TryGetProperty(
        "cloudProviders",
        out _
      ) || !hasProviderContext
        || !document.RootElement.GetProperty(
          "runtime"
        ).TryGetProperty(
          "generationTimeoutSeconds",
          out _
        )
        || !document.RootElement.GetProperty(
          "execution"
        ).TryGetProperty(
          "maxToolOutputTokens",
          out _
        )
        || !hasCoordinatorModel
        || !hasMaxRecoveryAttempts;

      if (
        document.RootElement.TryGetProperty(
          "intentions",
          out var intentions
        )
        && intentions.ValueKind == JsonValueKind.Object
      )
      {
        requiresRewrite = requiresRewrite || intentions
          .EnumerateObject()
          .Any(
            intention => !intention.Value.TryGetProperty(
              "fallbackModel",
              out _
            )
          );
      }

      if (requiresRewrite)
      {
        await WriteValidatedAsync(
          settings,
          cancellationToken
        );
      }

      return settings;
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<SettingsSaveResult> SaveAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    var errors = _validator.Validate(
      settings
    );

    if (errors.Count > 0)
    {
      return new SettingsSaveResult(
        false,
        null,
        errors
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      Directory.CreateDirectory(
        _dataDirectory
      );
      await WriteValidatedAsync(
        settings,
        cancellationToken
      );

      return new SettingsSaveResult(
        true,
        settings,
        errors
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task WriteValidatedAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    var errors = _validator.Validate(
      settings
    );

    if (errors.Count > 0)
    {
      throw new InvalidOperationException(
        "Attempted to write invalid settings."
      );
    }

    var temporaryPath = Path.Combine(
      _dataDirectory,
      $".settings-{Guid.NewGuid():N}.tmp"
    );
    var json = JsonSerializer.Serialize(
      settings,
      JsonOptions
    ).Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ) + "\n";

    try
    {
      await File.WriteAllTextAsync(
        temporaryPath,
        json,
        cancellationToken
      );
      File.Move(
        temporaryPath,
        _settingsPath,
        true
      );
    }
    catch (Exception exception)
    {
      _logger.LogError(
        exception,
        "Failed to write settings safely to {SettingsPath}.",
        _settingsPath
      );
      throw;
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
}
