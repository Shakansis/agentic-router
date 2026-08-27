using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Setup;

public interface IOllamaInstallationProfileStore
{
  Task<OllamaInstallationPreference?> GetAsync(
    CancellationToken cancellationToken
  );

  Task SetRequestedProfileAsync(
    string profile,
    CancellationToken cancellationToken
  );
}

public sealed class OllamaInstallationProfileStore : IOllamaInstallationProfileStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
  };

  private readonly string _directory;
  private readonly string _path;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public OllamaInstallationProfileStore(
    string dataDirectory
  )
  {
    _directory = dataDirectory;
    _path = Path.Combine(
      dataDirectory,
      "ollama-installation.json"
    );
  }

  public async Task<OllamaInstallationPreference?> GetAsync(
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );
    try
    {
      if (!File.Exists(_path))
      {
        return null;
      }

      var preference = JsonSerializer.Deserialize<OllamaInstallationPreference>(
        await File.ReadAllTextAsync(
          _path,
          cancellationToken
        ),
        JsonOptions
      );
      return preference is
      {
        SchemaVersion: 1,
        RequestedProfile: "standard" or "vulkan" or "rocm"
      }
        ? preference
        : null;
    }
    catch (JsonException)
    {
      return null;
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task SetRequestedProfileAsync(
    string profile,
    CancellationToken cancellationToken
  )
  {
    if (profile is not ("standard" or "vulkan" or "rocm"))
    {
      throw new ArgumentException(
        "The Ollama installation profile is invalid.",
        nameof(profile)
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );
    try
    {
      Directory.CreateDirectory(
        _directory
      );
      var temporary = Path.Combine(
        _directory,
        $".ollama-installation-{Guid.NewGuid():N}.tmp"
      );
      try
      {
        await File.WriteAllTextAsync(
          temporary,
          JsonSerializer.Serialize(
            new OllamaInstallationPreference(
              1,
              profile,
              DateTimeOffset.UtcNow
            ),
            JsonOptions
          ),
          new UTF8Encoding(
            false
          ),
          cancellationToken
        );
        File.Move(
          temporary,
          _path,
          true
        );
      }
      finally
      {
        if (File.Exists(temporary))
        {
          File.Delete(temporary);
        }
      }
    }
    finally
    {
      _gate.Release();
    }
  }
}

public sealed record OllamaInstallationPreference(
  int SchemaVersion,
  string RequestedProfile,
  DateTimeOffset UpdatedAt
);
