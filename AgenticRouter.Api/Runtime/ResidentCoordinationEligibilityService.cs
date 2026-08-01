using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Runtime;

public interface IResidentCoordinationEligibilityService
{
  ResidentCoordinationEligibility Evaluate(
    ApplicationSettings settings,
    InstalledModel target,
    ResidentModelStatus resident,
    bool residentConformanceApproved
  );
}

public sealed record ResidentCoordinationEligibility(
  bool ResidentEligible,
  bool RequiresResidentEviction,
  string Evidence,
  string MemoryConsequence
);

public sealed class ResidentCoordinationEligibilityService
  : IResidentCoordinationEligibilityService
{
  private readonly ISystemMemoryMetricsProvider _systemMemory;
  private readonly IGpuMemoryMetricsProvider _gpuMemory;

  public ResidentCoordinationEligibilityService(
    ISystemMemoryMetricsProvider systemMemory,
    IGpuMemoryMetricsProvider gpuMemory
  )
  {
    _systemMemory = systemMemory;
    _gpuMemory = gpuMemory;
  }

  public ResidentCoordinationEligibility Evaluate(
    ApplicationSettings settings,
    InstalledModel target,
    ResidentModelStatus resident,
    bool residentConformanceApproved
  )
  {
    var targetReference = ProviderModelReference.Parse(
      target.Name
    );
    var residentReady = !resident.Loaded
      || resident.ActualContextTokens is null
      || resident.RequestedContextTokens is null
      || resident.ActualContextTokens == resident.RequestedContextTokens;
    var residentEligible = residentConformanceApproved && residentReady;

    if (
      !targetReference.IsLocal
      || string.Equals(
        target.Name,
        settings.ActionModel,
        StringComparison.OrdinalIgnoreCase
      )
      || !resident.Loaded
    )
    {
      return new ResidentCoordinationEligibility(
        residentEligible,
        false,
        resident.Loaded
          ? "exact resident runtime status; no concurrent local target runner"
          : "resident runtime status is not loaded",
        "no resident eviction is required"
      );
    }

    var gpu = _gpuMemory.GetStatus();
    var system = _systemMemory.GetStatus();
    var knownFreeVram = gpu.Devices.Where(
      device => device.TotalDedicatedMemoryBytes is not null
        && device.UsedDedicatedMemoryBytes is not null
    ).Sum(
      device => device.TotalDedicatedMemoryBytes!.Value
        - device.UsedDedicatedMemoryBytes!.Value
    );
    var hasGpuEvidence = gpu.Devices.Any(
      device => device.TotalDedicatedMemoryBytes is not null
        && device.UsedDedicatedMemoryBytes is not null
    );
    var metadataTargetBytes = target.SizeBytes;
    var insufficientVram = metadataTargetBytes is not null
      && hasGpuEvidence
      && metadataTargetBytes
        > Math.Max(
          0,
          knownFreeVram - settings.OllamaRuntime.Memory.MinimumFreeVramBytes
        );
    var insufficientRam = metadataTargetBytes is not null
      && system.AvailableBytes is not null
      && metadataTargetBytes
        > Math.Max(
          0,
          system.AvailableBytes.Value
            - settings.OllamaRuntime.Memory.MinimumFreeSystemRamBytes
        );
    var requiresEviction = insufficientVram || insufficientRam;
    var evidence = hasGpuEvidence || system.AvailableBytes is not null
      ? "metadata-derived target size combined with observed free-memory headroom; target allocation is an estimate"
      : "memory coexistence evidence is unavailable; no exact allocation is inferred from the model name";
    var consequence = requiresEviction
      ? "the loaded resident conflicts with configured target headroom and must be evicted and restored"
      : "available observed headroom does not require proactive resident eviction; CPU offload may still change runtime use";

    return new ResidentCoordinationEligibility(
      residentEligible,
      requiresEviction,
      evidence,
      consequence
    );
  }
}
