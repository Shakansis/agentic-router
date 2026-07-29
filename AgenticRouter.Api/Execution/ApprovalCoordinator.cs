using System.Collections.Concurrent;

namespace AgenticRouter.Api.Execution;

public interface IApprovalCoordinator
{
  Task<bool> WaitAsync(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    CancellationToken cancellationToken
  );

  bool TryDecide(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    bool approved
  );

  void InvalidateAll();
}

public sealed class ApprovalCoordinator : IApprovalCoordinator
{
  private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(
    StringComparer.Ordinal
  );

  public async Task<bool> WaitAsync(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    CancellationToken cancellationToken
  )
  {
    var source = new TaskCompletionSource<bool>(
      TaskCreationOptions.RunContinuationsAsynchronously
    );

    if (!_pending.TryAdd(
      actionId,
      new PendingApproval(
        browserSessionId,
        executionSessionId,
        source
      )
    ))
    {
      throw new InvalidOperationException(
        "The approval action ID is already pending."
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
        actionId,
        out _
      );
    }
  }

  public bool TryDecide(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    bool approved
  )
  {
    return _pending.TryGetValue(
      actionId,
      out var pending
    ) && string.Equals(
      pending.BrowserSessionId,
      browserSessionId,
      StringComparison.Ordinal
    ) && string.Equals(
      pending.ExecutionSessionId,
      executionSessionId,
      StringComparison.Ordinal
    ) && pending.Source.TrySetResult(
      approved
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

  private sealed record PendingApproval(
    string BrowserSessionId,
    string ExecutionSessionId,
    TaskCompletionSource<bool> Source
  );
}
