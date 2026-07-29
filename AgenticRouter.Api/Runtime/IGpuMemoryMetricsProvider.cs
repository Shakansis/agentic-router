using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public interface IGpuMemoryMetricsProvider
{
  GpuMemoryMetricsSnapshot GetStatus();
}

public sealed record GpuMemoryMetricsSnapshot(
  IReadOnlyList<GpuMemoryStatus> Devices,
  string Status,
  string? Diagnostic
);
