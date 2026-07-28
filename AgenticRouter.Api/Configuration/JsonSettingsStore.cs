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

      await using var stream = File.OpenRead(
        _settingsPath
      );
      var settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(
        stream,
        JsonOptions,
        cancellationToken
      ) ?? throw new InvalidDataException(
        "The settings file contains no settings object."
      );
      var errors = _validator.Validate(
        settings
      );

      if (errors.Count > 0)
      {
        throw new InvalidDataException(
          "The saved settings file is invalid."
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
