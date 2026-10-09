using System.Diagnostics;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Runtime;

namespace AgenticRouter.Api.Usage;

// Measures the provider dispatch, not policy resolution, model discovery or tool time.
public sealed class InferenceObservation(Action<InferenceProgressView>? progressObserver = null) : IDisposable
{
  private readonly Stopwatch _clock = new();
  public DateTimeOffset? DispatchedAt { get; private set; }
  public double? TimeToFirstTokenMilliseconds { get; private set; }
  public Action<InferenceProgressView>? ProgressObserver { get; } = progressObserver;
  public InferenceProgressSource.Subscription? Progress { get; set; }

  public void Dispatch()
  {
    if (DispatchedAt is not null) return;
    DispatchedAt = DateTimeOffset.UtcNow;
    _clock.Start();
    Progress ??= InferenceProgressSource.Subscription.Unavailable(ProgressObserver);
  }

  public void ObserveToken()
  {
    if (DispatchedAt is not null)
      TimeToFirstTokenMilliseconds ??= _clock.Elapsed.TotalMilliseconds;
    Progress?.Generating();
  }

  public void Dispose() => Progress?.Dispose();

  public static double? AfterModelLoad(double? firstTokenMilliseconds, double? loadMilliseconds) =>
    loadMilliseconds is >= 0
      ? firstTokenMilliseconds >= loadMilliseconds ? firstTokenMilliseconds - loadMilliseconds : null
      : firstTokenMilliseconds;
}
