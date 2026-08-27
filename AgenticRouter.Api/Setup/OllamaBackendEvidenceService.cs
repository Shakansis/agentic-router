using System.Globalization;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Setup;

public interface IOllamaBackendEvidenceService
{
  Task<OllamaBackendStatus?> InspectAsync(
    string? requestedProfile,
    IReadOnlyList<OllamaRunningModel> runningModels,
    string? runningModelsDiagnostic,
    CancellationToken cancellationToken
  );
}

public sealed class NoOpOllamaBackendEvidenceService : IOllamaBackendEvidenceService
{
  public Task<OllamaBackendStatus?> InspectAsync(
    string? requestedProfile,
    IReadOnlyList<OllamaRunningModel> runningModels,
    string? runningModelsDiagnostic,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<OllamaBackendStatus?>(
      null
    );
  }
}

public sealed class LinuxOllamaBackendEvidenceService : IOllamaBackendEvidenceService
{
  private readonly string _manifestPath;

  public LinuxOllamaBackendEvidenceService(
    string dataDirectory
  )
  {
    _manifestPath = Path.Combine(
      dataDirectory,
      "installation-manifests",
      "ollama",
      "install.properties"
    );
  }

  public Task<OllamaBackendStatus?> InspectAsync(
    string? requestedProfile,
    IReadOnlyList<OllamaRunningModel> runningModels,
    string? runningModelsDiagnostic,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!OperatingSystem.IsLinux())
    {
      return Task.FromResult<OllamaBackendStatus?>(
        null
      );
    }

    var manifestProfile = ReadManifestProfile();
    var observed = ObserveBackend(
      cancellationToken
    );
    if (
      observed is null
      && runningModels.Count > 0
      && runningModels.All(model => model.VramSizeBytes == 0)
    )
    {
      observed = new BackendObservation(
        "cpu",
        "Ollama /api/ps reports running models with zero VRAM allocation."
      );
    }
    else if (
      observed is null
      && runningModels.Any(model => model.VramSizeBytes is > 0)
    )
    {
      observed = new BackendObservation(
        "gpu-unknown",
        "Ollama /api/ps reports GPU allocation, but the selected backend library was not observable in /proc maps."
      );
    }

    var expected = requestedProfile ?? manifestProfile;
    var state = State(
      requestedProfile,
      manifestProfile,
      observed?.Backend,
      runningModels.Count,
      runningModelsDiagnostic
    );
    bool? fallback = state == "fallback"
      ? true
      : observed is null || expected is null
        ? null
        : false;
    var evidence = new List<string>();
    if (manifestProfile is not null)
    {
      evidence.Add(
        $"Managed installation manifest records profile '{manifestProfile}'."
      );
    }
    if (observed is not null)
    {
      evidence.Add(
        observed.Evidence
      );
    }
    if (!string.IsNullOrWhiteSpace(runningModelsDiagnostic))
    {
      evidence.Add(
        runningModelsDiagnostic
      );
    }
    if (evidence.Count == 0)
    {
      evidence.Add(
        "No managed installation manifest, running model, or selected backend library was observed."
      );
    }

    return Task.FromResult<OllamaBackendStatus?>(
      new OllamaBackendStatus(
        requestedProfile,
        manifestProfile,
        observed?.Backend,
        state,
        fallback,
        string.Join(
          " ",
          evidence
        ),
        DateTimeOffset.UtcNow
      )
    );
  }

  private string? ReadManifestProfile()
  {
    try
    {
      if (!File.Exists(_manifestPath))
      {
        return null;
      }

      var value = File.ReadLines(
        _manifestPath
      ).Select(
        line => line.Split(
          '=',
          2
        )
      ).Where(
        fields => fields.Length == 2
          && fields[0] == "requestedProfile"
      ).Select(
        fields => fields[1].Trim()
      ).FirstOrDefault();
      return value is "standard" or "vulkan" or "rocm"
        ? value
        : null;
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
    )
    {
      return null;
    }
  }

  private static BackendObservation? ObserveBackend(
    CancellationToken cancellationToken
  )
  {
    var processes = new List<ProcessMaps>();
    foreach (var processDirectory in Directory.EnumerateDirectories(
      "/proc",
      "*",
      SearchOption.TopDirectoryOnly
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!int.TryParse(
        Path.GetFileName(processDirectory),
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out _
      ))
      {
        continue;
      }

      try
      {
        var commandLine = File.ReadAllText(
          Path.Combine(processDirectory, "cmdline")
        ).Replace(
          '\0',
          ' '
        );
        if (!commandLine.Contains(
          "ollama",
          StringComparison.OrdinalIgnoreCase
        ))
        {
          continue;
        }
        processes.Add(
          new ProcessMaps(
            commandLine,
            File.ReadAllText(
              Path.Combine(processDirectory, "maps")
            )
          )
        );
      }
      catch (Exception exception) when (
        exception is IOException
          or UnauthorizedAccessException
      )
      {
      }
    }

    var runners = processes.Where(
      process => process.CommandLine.Contains(
        "runner",
        StringComparison.OrdinalIgnoreCase
      )
    ).ToArray();
    var sources = runners.Length > 0
      ? runners
      : processes.ToArray();
    var backends = new HashSet<string>(
      StringComparer.Ordinal
    );
    foreach (var source in sources)
    {
      var maps = source.Maps;
      if (maps.Contains("libggml-vulkan", StringComparison.OrdinalIgnoreCase))
      {
        backends.Add("vulkan");
      }
      if (
        maps.Contains("libggml-hip", StringComparison.OrdinalIgnoreCase)
        || maps.Contains("libamdhip64", StringComparison.OrdinalIgnoreCase)
      )
      {
        backends.Add("rocm");
      }
      if (maps.Contains("libggml-cuda", StringComparison.OrdinalIgnoreCase))
      {
        backends.Add("cuda");
      }
    }

    return backends.Count switch
    {
      0 => null,
      1 => new BackendObservation(
        backends.Single(),
        $"The active Ollama process maps contain the selected {backends.Single()} backend library."
      ),
      _ => new BackendObservation(
        "mixed",
        $"Active Ollama process maps contain multiple backend libraries: {string.Join(", ", backends.Order(StringComparer.Ordinal))}."
      )
    };
  }

  private static string State(
    string? requestedProfile,
    string? manifestProfile,
    string? observedBackend,
    int runningModelCount,
    string? runningModelsDiagnostic
  )
  {
    if (
      requestedProfile is not null
      && manifestProfile is not null
      && requestedProfile != manifestProfile
    )
    {
      return "profile-mismatch";
    }
    if (observedBackend is null)
    {
      return runningModelCount == 0
        ? "not-observed"
        : string.IsNullOrWhiteSpace(runningModelsDiagnostic)
          ? "partial"
          : "unavailable";
    }

    var expected = requestedProfile ?? manifestProfile;
    if (expected is "vulkan" or "rocm" && observedBackend == "cpu")
    {
      return "fallback";
    }
    if (expected is null)
    {
      return "observed";
    }
    if (
      observedBackend == expected
      || expected == "standard" && observedBackend is "cuda" or "cpu"
    )
    {
      return "verified";
    }
    if (observedBackend == "gpu-unknown")
    {
      return "partial";
    }
    return "backend-mismatch";
  }

  private sealed record ProcessMaps(
    string CommandLine,
    string Maps
  );

  private sealed record BackendObservation(
    string Backend,
    string Evidence
  );
}

public sealed record OllamaBackendStatus(
  string? RequestedProfile,
  string? ManifestProfile,
  string? ObservedBackend,
  string State,
  bool? FallbackObserved,
  string Evidence,
  DateTimeOffset ObservedAt
);
