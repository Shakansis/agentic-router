using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public sealed record ResidentCoexistenceResult(
  bool ResidentLoaded,
  bool TargetLoaded,
  bool Reasserted,
  string Outcome
);

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

  Task<ResidentCoexistenceResult> EnsureResidentAlongsideTargetAsync(
    string targetModel,
    CancellationToken cancellationToken
  );
}
