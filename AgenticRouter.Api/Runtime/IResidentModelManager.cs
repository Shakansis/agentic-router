using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public interface IResidentModelManager
{
  IDisposable BeginRequest();

  ResidentModelStatus GetStatus();

  Task ChangeRouterModelAsync(
    ApplicationSettings previousSettings,
    ApplicationSettings nextSettings,
    CancellationToken cancellationToken
  );

  Task<bool> EvictForRecoveryAsync(
    string targetModel,
    CancellationToken cancellationToken
  );

  Task<bool> RestoreAfterRecoveryAsync(
    string targetModel,
    CancellationToken cancellationToken
  );
}
