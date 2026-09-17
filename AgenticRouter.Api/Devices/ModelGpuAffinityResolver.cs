using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Devices;

public sealed record ModelGpuAffinityResolution(
  string Model,
  string ConfiguredAffinity,
  bool Explicit,
  string GpuSelection,
  string? DeviceId,
  string? DeviceName,
  string? Backend,
  bool UsesMultipleVulkanDevices
);

public sealed class ModelGpuAffinityException : Exception
{
  public ModelGpuAffinityException(string message) : base(message)
  {
  }
}

public interface IModelGpuAffinityResolver
{
  Task<ModelGpuAffinityResolution> ResolveAsync(
    ApplicationSettings settings,
    string model,
    string inheritedGpuSelection,
    CancellationToken cancellationToken
  );
}

public sealed class ModelGpuAffinityResolver : IModelGpuAffinityResolver
{
  private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromSeconds(30);
  private readonly IGpuDiscoveryService _gpuDiscovery;
  private readonly SemaphoreSlim _discoveryGate = new(1, 1);
  private DevicesResponse? _cachedDevices;
  private DateTimeOffset _cachedAt;

  public ModelGpuAffinityResolver(IGpuDiscoveryService gpuDiscovery)
  {
    _gpuDiscovery = gpuDiscovery;
  }

  public async Task<ModelGpuAffinityResolution> ResolveAsync(
    ApplicationSettings settings,
    string model,
    string inheritedGpuSelection,
    CancellationToken cancellationToken
  )
  {
    var affinity = ModelGpuAffinitySelection.GetForModel(settings, model);
    if (!ModelGpuAffinitySelection.TryGetDeviceId(affinity, out var deviceId))
    {
      // Model affinity is the only override of General Default GPU. Legacy
      // role/intent selections must not change the placement of an Auto model.
      var effectiveSelection = settings.DefaultGpu;
      var target = OllamaGpuSelection.ResolveTarget(
        effectiveSelection,
        settings.DefaultGpu
      );
      return new ModelGpuAffinityResolution(
        model,
        ModelGpuAffinitySelection.Auto,
        false,
        effectiveSelection,
        null,
        null,
        target?.Backend,
        target is { Backend: "vulkan", AllDevices: true }
      );
    }

    var devices = await GetDevicesAsync(cancellationToken);
    var device = devices.Devices.SingleOrDefault(candidate =>
      !candidate.IsAuto
      && candidate.Available
      && string.Equals(candidate.Id, deviceId, StringComparison.OrdinalIgnoreCase)
    ) ?? throw new ModelGpuAffinityException(
      $"The GPU configured for model '{model}' is no longer available."
    );
    if (
      !device.AffinitySelectable
      || string.IsNullOrWhiteSpace(device.Backend)
      || device.BackendIndex is null
    )
    {
      throw new ModelGpuAffinityException(
        $"GPU '{device.Name}' cannot guarantee explicit Ollama placement on this host."
      );
    }

    var selection = string.Equals(
      device.Backend,
      "cuda",
      StringComparison.Ordinal
    )
      ? $"{OllamaGpuSelection.RuntimePrefix}{device.BackendIndex.Value}"
      : $"{device.Backend}:{device.BackendIndex.Value}";
    return new ModelGpuAffinityResolution(
      model,
      affinity,
      true,
      selection,
      device.Id,
      device.Name,
      device.Backend,
      false
    );
  }

  private async Task<DevicesResponse> GetDevicesAsync(
    CancellationToken cancellationToken
  )
  {
    var now = DateTimeOffset.UtcNow;
    if (
      _cachedDevices is not null
      && now - _cachedAt < DiscoveryLifetime
    )
    {
      return _cachedDevices;
    }

    await _discoveryGate.WaitAsync(cancellationToken);
    try
    {
      now = DateTimeOffset.UtcNow;
      if (
        _cachedDevices is null
        || now - _cachedAt >= DiscoveryLifetime
      )
      {
        _cachedDevices = await _gpuDiscovery.DiscoverAsync(cancellationToken);
        _cachedAt = now;
      }
      return _cachedDevices;
    }
    finally
    {
      _discoveryGate.Release();
    }
  }
}
