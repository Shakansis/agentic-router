using System.Collections.Concurrent;

namespace AgenticRouter.Api.Execution;

public interface IApprovalCoordinator
{
  Task<bool> WaitAsync(
    string actionId,
    CancellationToken cancellationToken
  );

  bool TryDecide(
    string actionId,
    bool approved
  );
}

public sealed class ApprovalCoordinator : IApprovalCoordinator
{
  private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(
    StringComparer.Ordinal
  );

  public async Task<bool> WaitAsync(
    string actionId,
    CancellationToken cancellationToken
  )
  {
    var source = new TaskCompletionSource<bool>(
      TaskCreationOptions.RunContinuationsAsynchronously
    );

    if (!_pending.TryAdd(
      actionId,
      source
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
    bool approved
  )
  {
    return _pending.TryGetValue(
      actionId,
      out var source
    ) && source.TrySetResult(
      approved
    );
  }
}
