using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public interface ISystemMemoryMetricsProvider
{
  SystemMemoryStatus GetStatus();
}
