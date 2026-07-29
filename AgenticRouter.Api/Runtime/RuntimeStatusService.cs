using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
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
          settings.RouterModel
        )
      ).ToArray();
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
      resident
    );
  }

  private static LoadedModelStatus MapModel(
    OllamaRunningModel model,
    string residentModel
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

    return new LoadedModelStatus(
      model.Name,
      model.SizeBytes,
      model.VramSizeBytes,
      estimatedRam,
      processor,
      model.ExpiresAt,
      string.Equals(
        model.Name,
        residentModel,
        StringComparison.OrdinalIgnoreCase
      )
    );
  }
}
