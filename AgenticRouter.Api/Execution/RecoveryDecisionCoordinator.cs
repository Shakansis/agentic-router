using System.Collections.Concurrent;

namespace AgenticRouter.Api.Execution;

public interface IRecoveryDecisionCoordinator
{
  Task<string> WaitAsync(
    string checkpointId,
    string browserSessionId,
    string executionSessionId,
    IReadOnlySet<string> allowedOptions,
    CancellationToken cancellationToken
  );

  bool TryDecide(
    string checkpointId,
    string browserSessionId,
    string executionSessionId,
    string option
  );

  void InvalidateAll();
}

public sealed class RecoveryDecisionCoordinator : IRecoveryDecisionCoordinator
{
  private readonly ConcurrentDictionary<string, PendingDecision> _pending = new(
    StringComparer.Ordinal
  );

  public async Task<string> WaitAsync(
    string checkpointId,
    string browserSessionId,
    string executionSessionId,
    IReadOnlySet<string> allowedOptions,
    CancellationToken cancellationToken
  )
  {
    var source = new TaskCompletionSource<string>(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    var pending = new PendingDecision(
      browserSessionId,
      executionSessionId,
      allowedOptions,
      source
    );

    if (!_pending.TryAdd(
      checkpointId,
      pending
    ))
    {
      throw new InvalidOperationException(
        "The recovery checkpoint ID is already pending."
      );
    }

    try
    {
      return await source.Task.WaitAsync(
        cancellationToken
      );
    }
    finally
    {
      _pending.TryRemove(
        checkpointId,
        out _
      );
    }
  }

  public bool TryDecide(
    string checkpointId,
    string browserSessionId,
    string executionSessionId,
    string option
  )
  {
    return _pending.TryGetValue(
      checkpointId,
      out var pending
    ) && string.Equals(
      pending.BrowserSessionId,
      browserSessionId,
      StringComparison.Ordinal
    ) && string.Equals(
      pending.ExecutionSessionId,
      executionSessionId,
      StringComparison.Ordinal
    ) && pending.AllowedOptions.Contains(
      option
    ) && pending.Source.TrySetResult(
      option
    );
  }

  public void InvalidateAll()
  {
    foreach (var pending in _pending.Values)
    {
      pending.Source.TrySetCanceled();
    }

    _pending.Clear();
  }

  private sealed record PendingDecision(
    string BrowserSessionId,
    string ExecutionSessionId,
    IReadOnlySet<string> AllowedOptions,
    TaskCompletionSource<string> Source
  );
}
