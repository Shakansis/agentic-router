using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Runtime;

public sealed class RuntimeStatusService : IRuntimeStatusService
{
  private readonly ISystemMemoryMetricsProvider _systemMemory;
  private readonly IGpuMemoryMetricsProvider _gpuMemory;
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IOllamaGpuPlacementProvider _gpuPlacement;
  private readonly IOllamaManagedServerManager _managedServers;
  private readonly ILogger<RuntimeStatusService> _logger;

  public RuntimeStatusService(
    ISystemMemoryMetricsProvider systemMemory,
    IGpuMemoryMetricsProvider gpuMemory,
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IOllamaGpuPlacementProvider gpuPlacement,
    IOllamaManagedServerManager managedServers,
    ILogger<RuntimeStatusService> logger
  )
  {
    _systemMemory = systemMemory;
    _gpuMemory = gpuMemory;
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _gpuPlacement = gpuPlacement;
    _managedServers = managedServers;
    _logger = logger;
  }

  public async Task<RuntimeStatusResponse> GetAsync(
    CancellationToken cancellationToken
  )
  {
    var ram = _systemMemory.GetStatus();
    var gpuMemory = _gpuMemory.GetStatus();
    var resident = new ResidentModelStatus(
      string.Empty,
      null,
      "disabled",
      false,
      "disabled",
      null,
      null,
      null,
      null,
      null,
      null,
      "Resident routing is disabled; Auto uses deterministic keywords.",
      null
    );
    IReadOnlyList<LoadedModelStatus> loadedModels = [];
    var loadedModelsStatus = "available";
    string? loadedModelsDiagnostic = null;
    var warnings = new List<string>();

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var ollamaEndpoint = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      var managed = _managedServers.GetActiveServers();
      if (managed.Count == 0)
      {
        var running = await _ollamaClient.GetRunningModelsAsync(
          ollamaEndpoint,
          cancellationToken
        );
        var placement = _gpuPlacement.GetStatus(ollamaEndpoint);
        loadedModels = running.Select(
          model => MapModel(
            model,
            settings,
            gpuMemory.Devices,
            placement
          )
        ).ToArray();
      }
      else
      {
        var mapped = new List<LoadedModelStatus>();
        foreach (var server in managed)
        {
          var running = await _ollamaClient.GetRunningModelsAsync(
            server.Endpoint,
            cancellationToken
          );
          var placement = new OllamaGpuPlacementSnapshot(
            server.Backend,
            "managed",
            $"Agentic Router owns PID {server.ProcessId} at {server.Endpoint} and forced the {BackendLabel(server.Backend)} backend for '{server.Selection}'."
          );
          mapped.AddRange(
            running.Select(
              model => MapModel(
                model,
                settings,
                gpuMemory.Devices,
                placement,
                server.Selection,
                server.ContextLength
              )
            )
          );
        }
        loadedModels = mapped;
      }

      foreach (var model in loadedModels)
      {
        if (model.ProfileStatus == "context-mismatch")
        {
          warnings.Add(
            $"{model.Name} is loaded with {model.ActualContextTokens} context tokens, "
            + $"but the active runtime configuration requests {model.RequestedContextTokens}."
          );
        }

        if (model.SharedAcrossRoles)
        {
          warnings.Add(
            $"{model.Name} is shared across configured roles; one Ollama runner may use "
            + "the largest active role context."
          );
        }

        if (
          model.ConfiguredGpu is not null
          && model.ObservedBackend is not null
          && OllamaGpuSelection.ResolveTarget(
            model.ConfiguredGpu,
            settings.DefaultGpu
          ) is { } configuredTarget
          && configuredTarget.Backend != model.ObservedBackend
        )
        {
          warnings.Add(
            $"{model.Name} is configured for {BackendLabel(configuredTarget.Backend)}, "
            + $"but the local Ollama runner is observed on {BackendLabel(model.ObservedBackend)}. "
            + "The configured backend and observed runner do not match."
          );
        }

        if (
          model.TotalSizeBytes is > 0
          && model.VramSizeBytes is not null
          && model.VramSizeBytes < model.TotalSizeBytes
        )
        {
          warnings.Add(
            $"{model.Name} is partially offloaded to system memory."
          );
        }
      }

      if (
        ram.AvailableBytes is not null
        && ram.AvailableBytes < settings.OllamaRuntime.Memory.MinimumFreeSystemRamBytes
      )
      {
        warnings.Add(
          "Available system RAM is below the configured Ollama runtime headroom."
        );
      }

      foreach (var device in gpuMemory.Devices)
      {
        settings.OllamaRuntime.Memory.Devices.TryGetValue(
          device.Id,
          out var devicePolicy
        );
        var targetMaximum = devicePolicy?.TargetMaximumUsagePercent
          ?? settings.OllamaRuntime.Memory.TargetMaximumGpuUsagePercent;
        var minimumFree = devicePolicy?.MinimumFreeVramBytes
          ?? settings.OllamaRuntime.Memory.MinimumFreeVramBytes;

        if (
          device.UsedPercent is not null
          && device.UsedPercent > targetMaximum
        )
        {
          warnings.Add(
            $"{device.Name} exceeds the configured GPU usage target."
          );
        }

        if (
          device.TotalDedicatedMemoryBytes is not null
          && device.UsedDedicatedMemoryBytes is not null
          && device.TotalDedicatedMemoryBytes - device.UsedDedicatedMemoryBytes
            < minimumFree
        )
        {
          warnings.Add(
            $"{device.Name} has less free VRAM than the configured runtime headroom."
          );
        }
      }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      loadedModelsStatus = "unavailable";
      loadedModelsDiagnostic =
        $"Ollama running-model telemetry is unavailable: {exception.Message}";
      _logger.LogDebug(
        exception,
        "Loaded Ollama model telemetry is unavailable."
      );
    }

    return new RuntimeStatusResponse(
      DateTimeOffset.UtcNow,
      ram,
      gpuMemory.Devices,
      gpuMemory.Status,
      gpuMemory.Diagnostic,
      loadedModels,
      loadedModelsStatus,
      loadedModelsDiagnostic,
      resident,
      warnings.Count > 0,
      warnings.Distinct(
        StringComparer.Ordinal
      ).ToArray()
    );
  }

  private static LoadedModelStatus MapModel(
    OllamaRunningModel model,
    ApplicationSettings settings,
    IReadOnlyList<GpuMemoryStatus> devices,
    OllamaGpuPlacementSnapshot placement,
    string? configuredGpuOverride = null,
    int? requestedContextOverride = null
  )
  {
    long? estimatedRam = null;

    if (model.SizeBytes is not null && model.VramSizeBytes is not null)
    {
      estimatedRam = Math.Max(
        0,
        model.SizeBytes.Value - model.VramSizeBytes.Value
      );
    }

    var processor = model.SizeBytes is null || model.VramSizeBytes is null
      ? "unknown"
      : model.VramSizeBytes == 0
        ? "cpu"
        : estimatedRam == 0
          ? "gpu"
          : "hybrid";
    var configuredRoles = OllamaRuntimeProfileResolver.ConfiguredRoles(
      settings,
      model.Name
    ).Where(role => role is not OllamaRuntimeRoleIds.Router
      and not OllamaRuntimeRoleIds.ResidentCoordinator)
      .ToArray();
    var role = configuredRoles.FirstOrDefault() ?? OllamaRuntimeRoleIds.Primary;
    OllamaContextResolution? resolution = null;

    try
    {
      resolution = OllamaRuntimeProfileResolver.Resolve(
        settings,
        model.Name,
        model.Digest,
        role,
        null,
        0,
        0
      );
    }
    catch (OllamaRuntimeProfileException)
    {
      // Status remains available even when a saved profile needs correction.
    }

    var requestedContextTokens = requestedContextOverride
      ?? resolution?.EffectiveContextTokens;
    var profileStatus = requestedContextTokens is null
      ? "invalid"
      : model.ContextLength is null
        ? "unknown"
        : model.ContextLength == requestedContextTokens
          ? requestedContextOverride is not null || resolution!.Overridden
            ? "overridden"
            : "inherited"
          : "context-mismatch";
    var configuredGpu = configuredGpuOverride ?? settings.DefaultGpu;
    var configuredGpuIndex = OllamaGpuSelection.Resolve(
      configuredGpu,
      settings.DefaultGpu
    );
    var configuredTarget = OllamaGpuSelection.ResolveTarget(
      configuredGpu,
      settings.DefaultGpu
    );
    var configuredGpuName = configuredGpuIndex is null
      ? null
      : devices.FirstOrDefault(
        device => device.BackendIndex == configuredGpuIndex
          && (configuredTarget is null
            || MatchesBackend(device, configuredTarget.Backend))
      )?.Name;
    var backendDevices = placement.Backend is null
      ? []
      : devices.Where(
        device => MatchesBackend(
          device,
          placement.Backend
        )
      ).ToArray();
    var observedDevice = configuredGpuOverride is not null
      && configuredTarget is { AllDevices: false }
        ? devices.FirstOrDefault(
          device => device.BackendIndex == configuredTarget.Index
            && MatchesBackend(device, configuredTarget.Backend)
        )
        : backendDevices.Length == 1
          ? backendDevices[0]
          : null;
    int? observedBackendIndex = observedDevice?.BackendIndex
      ?? (observedDevice is not null && placement.Backend == "rocm"
        ? 0
        : null);
    var placementStatus = processor == "cpu"
      ? "cpu"
      : observedDevice is not null
        ? placement.Status
        : placement.Backend is not null
          ? "partial"
          : placement.Status;
    var placementDiagnostic = processor == "cpu"
      ? "Ollama reports zero VRAM allocation for this model."
      : observedDevice is not null
        ? placement.Diagnostic
        : placement.Backend is not null && backendDevices.Length > 1
          ? $"{placement.Diagnostic} Multiple {BackendLabel(placement.Backend)} adapters were detected, so the exact device is not observable."
          : placement.Diagnostic;

    return new LoadedModelStatus(
      model.Name,
      model.Digest,
      role,
      requestedContextTokens,
      model.ContextLength,
      model.SizeBytes,
      model.VramSizeBytes,
      estimatedRam,
      processor,
      model.ExpiresAt,
      false,
      profileStatus,
      configuredRoles.Length > 1,
      observedBackendIndex,
      observedDevice?.Name,
      configuredGpu,
      configuredGpuIndex,
      configuredGpuName,
      observedDevice?.Id,
      placement.Backend,
      observedBackendIndex,
      placementStatus,
      placementDiagnostic
    );
  }

  private static string BackendLabel(
    string backend
  )
  {
    return backend switch
    {
      "cuda" => "CUDA",
      "rocm" => "ROCm",
      "vulkan" => "Vulkan",
      _ => backend
    };
  }

  private static bool MatchesBackend(
    GpuMemoryStatus device,
    string backend
  )
  {
    return backend switch
    {
      "cuda" => device.Manufacturer == "NVIDIA",
      "rocm" => device.Manufacturer == "AMD",
      "vulkan" => false,
      _ => false
    };
  }
}
