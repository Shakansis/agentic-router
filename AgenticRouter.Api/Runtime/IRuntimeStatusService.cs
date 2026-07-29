using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public interface IRuntimeStatusService
{
  Task<RuntimeStatusResponse> GetAsync(
    CancellationToken cancellationToken
  );
}
