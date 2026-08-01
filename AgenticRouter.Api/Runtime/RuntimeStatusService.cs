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
  private readonly IResidentModelManager _residentModel;
  private readonly ILogger<RuntimeStatusService> _logger;

  public RuntimeStatusService(
    ISystemMemoryMetricsProvider systemMemory,
    IGpuMemoryMetricsProvider gpuMemory,
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IResidentModelManager residentModel,
    ILogger<RuntimeStatusService> logger
  )
  {
    _systemMemory = systemMemory;
    _gpuMemory = gpuMemory;
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _residentModel = residentModel;
    _logger = logger;
  }

  public async Task<RuntimeStatusResponse> GetAsync(
    CancellationToken cancellationToken
  )
  {
    var ram = _systemMemory.GetStatus();
    var gpuMemory = _gpuMemory.GetStatus();
    var resident = _residentModel.GetStatus();
    IReadOnlyList<LoadedModelStatus> loadedModels = [];
    var loadedModelsStatus = "available";
    string? loadedModelsDiagnostic = null;
    var warnings = new List<string>();

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var running = await _ollamaClient.GetRunningModelsAsync(
        new Uri(
          settings.OllamaUrl,
          UriKind.Absolute
        ),
        cancellationToken
      );
      loadedModels = running.Select(
        model => MapModel(
          model,
          settings
        )
      ).ToArray();

      foreach (var model in loadedModels)
      {
        if (model.ProfileStatus == "context-mismatch")
        {
          warnings.Add(
            $"{model.Name} is loaded with {model.ActualContextTokens} context tokens, "
            + $"but its resolved profile requests {model.RequestedContextTokens}."
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
    ApplicationSettings settings
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
    );
    var role = configuredRoles.Contains(
      OllamaRuntimeRoleIds.ResidentCoordinator,
      StringComparer.Ordinal
    )
      ? OllamaRuntimeRoleIds.ResidentCoordinator
      : configuredRoles.FirstOrDefault() ?? OllamaRuntimeRoleIds.Primary;
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

    var profileStatus = resolution is null
      ? "invalid"
      : model.ContextLength is null
        ? "unknown"
        : model.ContextLength == resolution.EffectiveContextTokens
          ? resolution.Overridden
            ? "overridden"
            : "inherited"
          : "context-mismatch";
    var residentReference = ProviderModelReference.Parse(
      settings.ActionModel
    );

    return new LoadedModelStatus(
      model.Name,
      model.Digest,
      role,
      resolution?.EffectiveContextTokens,
      model.ContextLength,
      model.SizeBytes,
      model.VramSizeBytes,
      estimatedRam,
      processor,
      model.ExpiresAt,
      string.Equals(
        model.Name,
        residentReference.ModelId,
        StringComparison.OrdinalIgnoreCase
      ),
      profileStatus,
      configuredRoles.Count > 1
    );
  }
}
