using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Devices;

public interface IGpuDiscoveryService
{
  DevicesResponse Discover();
}
