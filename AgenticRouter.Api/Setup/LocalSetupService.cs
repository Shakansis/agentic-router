using System.Collections.Concurrent;
using System.Diagnostics;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Recovery;

namespace AgenticRouter.Api.Setup;

public interface ILocalSetupService
{
  Task<LocalSetupStatus> GetStatusAsync(CancellationToken cancellationToken);

  Task<SetupActionResult> StartInstallerAsync(
    string resourceId,
    CancellationToken cancellationToken
  );

  Task<SetupActionResult> StartModelPullAsync(
    string model,
    CancellationToken cancellationToken
  );
}

public sealed class LocalSetupService : ILocalSetupService
{
  private const long Gibibyte = 1024L * 1024L * 1024L;

  private static readonly IReadOnlyDictionary<string, InstallerDefinition>
    Installers = new Dictionary<string, InstallerDefinition>(
      StringComparer.OrdinalIgnoreCase
    )
    {
      ["ollama"] = new(
        "ollama",
        "Ollama",
        "winget install --id Ollama.Ollama --exact --source winget --accept-source-agreements --accept-package-agreements"
      ),
      [HarnessIds.Codex] = new(
        HarnessIds.Codex,
        "Codex",
        "winget install --id OpenAI.Codex --exact --source winget --accept-source-agreements --accept-package-agreements"
      ),
      [HarnessIds.ClaudeCode] = new(
        HarnessIds.ClaudeCode,
        "Claude Code",
        "winget install --id Anthropic.ClaudeCode --exact --source winget --accept-source-agreements --accept-package-agreements"
      ),
      [HarnessIds.OpenCode] = new(
        HarnessIds.OpenCode,
        "OpenCode",
        "npm install --global opencode-ai@latest"
      ),
      [HarnessIds.QwenCode] = new(
        HarnessIds.QwenCode,
        "Qwen Code",
        "npm install --global @qwen-code/qwen-code@latest"
      )
    };

  private static readonly ModelRecommendationDefinition[] ModelCatalog =
  [
    new(
      "qwen3.8:27b-q4_K_M",
      18_000_000_000L,
      22 * Gibibyte,
      "Highest-capability local coding option for 24 GB-class GPUs."
    ),
    new(
      "qwen2.5-coder:14b",
      9_000_000_000L,
      12 * Gibibyte,
      "Balanced coding model for 12 GB-class GPUs."
    ),
    new(
      "qwen2.5-coder:7b",
      4_700_000_000L,
      7 * Gibibyte,
      "Responsive coding model for 8 GB-class GPUs."
    ),
    new(
      "qwen2.5-coder:3b",
      1_900_000_000L,
      4 * Gibibyte,
      "Compact coding model for 4 GB-class GPUs."
    ),
    new(
      "qwen2.5-coder:1.5b",
      986_000_000L,
      2 * Gibibyte,
      "Low-memory fallback when GPU capacity is limited or unknown."
    )
  ];

  private readonly ISettingsStore _settingsStore;
  private readonly OllamaClient _ollama;
  private readonly IGpuDiscoveryService _gpuDiscovery;
  private readonly IHarnessRegistry _harnesses;
  private readonly SafeModeState _safeMode;
  private readonly IHostApplicationLifetime _applicationLifetime;
  private readonly ILogger<LocalSetupService> _logger;
  private readonly ConcurrentDictionary<string, SetupJobState> _jobs = new(
    StringComparer.OrdinalIgnoreCase
  );

  public LocalSetupService(
    ISettingsStore settingsStore,
    OllamaClient ollama,
    IGpuDiscoveryService gpuDiscovery,
    IHarnessRegistry harnesses,
    SafeModeState safeMode,
    IHostApplicationLifetime applicationLifetime,
    ILogger<LocalSetupService> logger
  )
  {
    _settingsStore = settingsStore;
    _ollama = ollama;
    _gpuDiscovery = gpuDiscovery;
    _harnesses = harnesses;
    _safeMode = safeMode;
    _applicationLifetime = applicationLifetime;
    _logger = logger;
  }

  public async Task<LocalSetupStatus> GetStatusAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var baseUri = new Uri(settings.OllamaUrl, UriKind.Absolute);
    string? ollamaVersion = null;
    string? ollamaDiagnostic = null;
    IReadOnlyList<InstalledModel> installedModels = [];

    try
    {
      ollamaVersion = await _ollama.GetVersionAsync(baseUri, cancellationToken);
      installedModels = await _ollama.GetModelsAsync(baseUri, cancellationToken);
    }
    catch (OllamaProviderException exception)
    {
      ollamaDiagnostic = exception.Message;
    }

    var devices = await _gpuDiscovery.DiscoverAsync(cancellationToken);
    var largestGpuBytes = devices.Devices
      .Where(device => device.Available && !device.IsAuto)
      .Select(device => device.MemoryBytes)
      .Where(memory => memory.HasValue)
      .Select(memory => memory!.Value)
      .DefaultIfEmpty()
      .Max();
    var recommendations = BuildRecommendations(
      largestGpuBytes > 0 ? largestGpuBytes : null,
      installedModels
    );
    var compatibleModelInstalled = recommendations.Any(model => model.Installed)
      || installedModels.Any(
        model => IsCompatibleInstalledModel(model, largestGpuBytes)
      );
    var harnessStatuses = await _harnesses.DiscoverAsync(cancellationToken);
    var harnesses = harnessStatuses.Select(
      status => new SetupHarnessStatus(
        status.Definition.Id,
        status.Definition.DisplayName,
        status.Definition.Id == HarnessIds.Native,
        status.Definition.Id == HarnessIds.Codex,
        status.Availability.Available,
        status.Availability.Version,
        status.Availability.Message,
        Installers.ContainsKey(status.Definition.Id),
        GetJob(status.Definition.Id),
        status.Definition,
        status.Availability
      )
    ).ToArray();
    var ollamaAvailable = ollamaVersion is not null;
    var missing = new List<string>();
    if (!ollamaAvailable)
    {
      missing.Add("Ollama");
    }
    if (ollamaAvailable && !compatibleModelInstalled)
    {
      missing.Add("a GPU-compatible Ollama model");
    }

    return new LocalSetupStatus(
      ollamaAvailable && compatibleModelInstalled,
      _safeMode.Enabled,
      new SetupResourceStatus(
        "ollama",
        "Ollama",
        ollamaAvailable,
        ollamaVersion,
        ollamaDiagnostic,
        OperatingSystem.IsWindows(),
        GetJob("ollama")
      ),
      harnesses,
      devices.Devices,
      devices.Diagnostic,
      largestGpuBytes > 0 ? largestGpuBytes : null,
      compatibleModelInstalled,
      recommendations,
      missing
    );
  }

  public async Task<SetupActionResult> StartInstallerAsync(
    string resourceId,
    CancellationToken cancellationToken
  )
  {
    ThrowIfMutationUnavailable();
    if (!OperatingSystem.IsWindows())
    {
      throw new SetupException(
        "installer-platform-unsupported",
        "Guided installation is currently available on Windows only."
      );
    }
    if (!Installers.TryGetValue(resourceId, out var installer))
    {
      throw new SetupException(
        "installer-not-found",
        $"No guided installer is registered for '{resourceId}'."
      );
    }

    var status = await GetStatusAsync(cancellationToken);
    if (IsResourceActive(status, resourceId))
    {
      return new SetupActionResult(
        resourceId,
        false,
        "available",
        $"{installer.DisplayName} is already available."
      );
    }

    var current = GetJob(resourceId);
    if (
      current?.State == "started"
      && DateTimeOffset.UtcNow - current.UpdatedAt < TimeSpan.FromMinutes(10)
    )
    {
      return new SetupActionResult(
        resourceId,
        false,
        current.State,
        current.Message
      );
    }

    var script =
      "$Host.UI.RawUI.WindowTitle = 'Agentic Router - Install "
      + installer.DisplayName.Replace("'", "''", StringComparison.Ordinal)
      + "'; "
      + installer.Command
      + "; if ($LASTEXITCODE -ne 0) { Read-Host 'Installation failed. Press Enter to close' }";
    var startInfo = new ProcessStartInfo
    {
      FileName = "powershell.exe",
      UseShellExecute = true,
      WindowStyle = ProcessWindowStyle.Normal
    };
    startInfo.ArgumentList.Add("-NoLogo");
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-Command");
    startInfo.ArgumentList.Add(script);

    try
    {
      using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        "The installer process did not start."
      );
      var job = new SetupJobState(
        "started",
        $"{installer.DisplayName} installer started in a separate window.",
        DateTimeOffset.UtcNow,
        null,
        null
      );
      _jobs[resourceId] = job;
      return new SetupActionResult(resourceId, true, job.State, job.Message);
    }
    catch (Exception exception) when (
      exception is InvalidOperationException or System.ComponentModel.Win32Exception
    )
    {
      _logger.LogWarning(
        exception,
        "The guided installer for {ResourceId} could not be started.",
        resourceId
      );
      throw new SetupException(
        "installer-start-failed",
        $"The {installer.DisplayName} installer could not be started.",
        exception
      );
    }
  }

  public async Task<SetupActionResult> StartModelPullAsync(
    string model,
    CancellationToken cancellationToken
  )
  {
    ThrowIfMutationUnavailable();
    var status = await GetStatusAsync(cancellationToken);
    var recommendation = status.RecommendedModels.FirstOrDefault(
      candidate => string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase)
    ) ?? throw new SetupException(
      "model-not-recommended",
      "The selected model is not in the current GPU-compatible recommendation set."
    );
    if (!status.Ollama.Available)
    {
      throw new SetupException(
        "ollama-unavailable",
        "Start Ollama before downloading a model."
      );
    }
    if (recommendation.Installed)
    {
      return new SetupActionResult(model, false, "available", $"{model} is already installed.");
    }

    var key = ModelJobKey(model);
    if (GetJob(key)?.State == "downloading")
    {
      var current = GetJob(key)!;
      return new SetupActionResult(model, false, current.State, current.Message);
    }

    var initial = new SetupJobState(
      "downloading",
      $"Downloading {model}.",
      DateTimeOffset.UtcNow,
      0,
      recommendation.DownloadBytes
    );
    _jobs[key] = initial;
    _ = RunModelPullAsync(model, key);
    return new SetupActionResult(model, true, initial.State, initial.Message);
  }

  private async Task RunModelPullAsync(string model, string key)
  {
    try
    {
      var settings = await _settingsStore.GetAsync(
        _applicationLifetime.ApplicationStopping
      );
      var baseUri = new Uri(settings.OllamaUrl, UriKind.Absolute);
      await foreach (var progress in _ollama.PullModelAsync(
        baseUri,
        model,
        _applicationLifetime.ApplicationStopping
      ))
      {
        _jobs[key] = new SetupJobState(
          "downloading",
          progress.Status,
          DateTimeOffset.UtcNow,
          progress.CompletedBytes,
          progress.TotalBytes
        );
      }
      _jobs[key] = new SetupJobState(
        "completed",
        $"{model} is installed.",
        DateTimeOffset.UtcNow,
        1,
        1
      );
    }
    catch (OperationCanceledException) when (
      _applicationLifetime.ApplicationStopping.IsCancellationRequested
    )
    {
      _jobs[key] = new SetupJobState(
        "cancelled",
        $"The {model} download stopped because Agentic Router is shutting down.",
        DateTimeOffset.UtcNow,
        null,
        null
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException or InvalidOperationException
    )
    {
      _logger.LogWarning(exception, "Model pull failed for {Model}.", model);
      _jobs[key] = new SetupJobState(
        "failed",
        exception.Message,
        DateTimeOffset.UtcNow,
        null,
        null
      );
    }
  }

  private IReadOnlyList<SetupModelRecommendation> BuildRecommendations(
    long? largestGpuBytes,
    IReadOnlyList<InstalledModel> installedModels
  )
  {
    var compatible = largestGpuBytes.HasValue
      ? ModelCatalog.Where(model => model.MinimumVramBytes <= largestGpuBytes.Value).Take(3)
      : ModelCatalog.TakeLast(1);
    return compatible.Select(
      (model, index) => new SetupModelRecommendation(
        model.Model,
        model.DownloadBytes,
        model.MinimumVramBytes,
        index == 0,
        installedModels.Any(installed => string.Equals(
          installed.Name,
          model.Model,
          StringComparison.OrdinalIgnoreCase
        )),
        model.Reason,
        GetJob(ModelJobKey(model.Model))
      )
    ).ToArray();
  }

  private static bool IsCompatibleInstalledModel(
    InstalledModel model,
    long largestGpuBytes
  )
  {
    if (!model.SizeBytes.HasValue || model.SizeBytes.Value < 800_000_000L)
    {
      return false;
    }

    return largestGpuBytes <= 0 || model.SizeBytes.Value <= largestGpuBytes * 0.85;
  }

  private static bool IsResourceActive(LocalSetupStatus status, string resourceId)
  {
    if (string.Equals(resourceId, "ollama", StringComparison.OrdinalIgnoreCase))
    {
      return status.Ollama.Available;
    }
    return status.Harnesses.Any(
      harness => string.Equals(harness.Id, resourceId, StringComparison.OrdinalIgnoreCase)
        && harness.Available
    );
  }

  private SetupJobState? GetJob(string key)
  {
    return _jobs.TryGetValue(key, out var job) ? job : null;
  }

  private void ThrowIfMutationUnavailable()
  {
    if (_safeMode.Enabled)
    {
      throw new SetupException(
        "safe-mode-read-only",
        "Installation and model downloads are disabled in safe mode."
      );
    }
  }

  private static string ModelJobKey(string model) => $"model:{model}";

  private sealed record InstallerDefinition(
    string Id,
    string DisplayName,
    string Command
  );

  private sealed record ModelRecommendationDefinition(
    string Model,
    long DownloadBytes,
    long MinimumVramBytes,
    string Reason
  );
}

public sealed record LocalSetupStatus(
  bool CoreReady,
  bool ReadOnly,
  SetupResourceStatus Ollama,
  IReadOnlyList<SetupHarnessStatus> Harnesses,
  IReadOnlyList<GraphicsDevice> Devices,
  string? DeviceDiagnostic,
  long? LargestGpuMemoryBytes,
  bool CompatibleModelInstalled,
  IReadOnlyList<SetupModelRecommendation> RecommendedModels,
  IReadOnlyList<string> MissingCoreResources
);

public sealed record SetupResourceStatus(
  string Id,
  string DisplayName,
  bool Available,
  string? Version,
  string? Diagnostic,
  bool InstallSupported,
  SetupJobState? Job
);

public sealed record SetupHarnessStatus(
  string Id,
  string DisplayName,
  bool Required,
  bool Recommended,
  bool Available,
  string? Version,
  string? Diagnostic,
  bool InstallSupported,
  SetupJobState? Job,
  HarnessDefinition Definition,
  HarnessAvailability Availability
);

public sealed record SetupModelRecommendation(
  string Model,
  long DownloadBytes,
  long MinimumVramBytes,
  bool Recommended,
  bool Installed,
  string Reason,
  SetupJobState? Job
);

public sealed record SetupJobState(
  string State,
  string Message,
  DateTimeOffset UpdatedAt,
  long? CompletedBytes,
  long? TotalBytes
);

public sealed record SetupActionResult(
  string ResourceId,
  bool Started,
  string State,
  string Message
);

public sealed class SetupException : Exception
{
  public SetupException(string code, string message, Exception? innerException = null)
    : base(message, innerException)
  {
    Code = code;
  }

  public string Code { get; }
}
