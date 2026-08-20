using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Devices;

public interface IGpuDiscoveryService
{
  Task<DevicesResponse> DiscoverAsync(
    CancellationToken cancellationToken
  );
}
