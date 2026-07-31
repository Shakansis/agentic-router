using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public interface IResidentModelManager
{
  IDisposable BeginRequest();

  ResidentModelStatus GetStatus();

  Task ChangeResidentModelAsync(
    ApplicationSettings previousSettings,
    ApplicationSettings nextSettings,
    CancellationToken cancellationToken
  );

  bool HasActiveRequests { get; }

  Task<bool> EvictForRecoveryAsync(
    string targetModel,
    CancellationToken cancellationToken
  );

  Task<bool> RestoreAfterRecoveryAsync(
    string targetModel,
    CancellationToken cancellationToken
  );
}
