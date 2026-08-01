using System.Collections.Concurrent;

namespace AgenticRouter.Api.Execution;

public interface IApprovalCoordinator
{
  Task<ApprovalOutcome> WaitAsync(
    ValidatedLocalAction action,
    string browserSessionId,
    string executionSessionId,
    Func<ValidatedLocalAction, string, CancellationToken, Task<ApprovalRevisionValidation>> revisionValidator,
    CancellationToken cancellationToken
  );

  Task<ApprovalDecisionResult> TryDecideAsync(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    bool approved,
    string? editedText,
    CancellationToken cancellationToken
  );

  Task<ApprovalRevisionResult> TryReviseAsync(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    string editedText,
    CancellationToken cancellationToken
  );

  void InvalidateAll();
}

public sealed record ApprovalOutcome(
  bool Approved,
  ValidatedLocalAction Action,
  bool Revised
);

public sealed record ApprovalRevisionValidation(
  bool Accepted,
  ValidatedLocalAction? Action,
  string? Diagnostic = null
);

public sealed record ApprovalRevisionResult(
  bool Pending,
  bool Accepted,
  ValidatedLocalAction? Action,
  string? Diagnostic = null
);

public sealed record ApprovalDecisionResult(
  bool Pending,
  bool Accepted,
  bool Approved,
  ValidatedLocalAction? Action,
  string? Diagnostic = null
);

public sealed class ApprovalCoordinator : IApprovalCoordinator
{
  private readonly ConcurrentDictionary<string, PendingApproval> _pending = new(
    StringComparer.Ordinal
  );

  public async Task<ApprovalOutcome> WaitAsync(
    ValidatedLocalAction action,
    string browserSessionId,
    string executionSessionId,
    Func<ValidatedLocalAction, string, CancellationToken, Task<ApprovalRevisionValidation>> revisionValidator,
    CancellationToken cancellationToken
  )
  {
    var source = new TaskCompletionSource<ApprovalOutcome>(
      TaskCreationOptions.RunContinuationsAsynchronously
    );

    if (!_pending.TryAdd(
      action.ActionId,
      new PendingApproval(
        browserSessionId,
        executionSessionId,
        action,
        revisionValidator,
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
        action.ActionId,
        out _
      );
    }
  }

  public async Task<ApprovalDecisionResult> TryDecideAsync(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    bool approved,
    string? editedText,
    CancellationToken cancellationToken
  )
  {
    if (
      !_pending.TryGetValue(
        actionId,
        out var pending
      )
      || !pending.BelongsTo(
        browserSessionId,
        executionSessionId
      )
    )
    {
      return new ApprovalDecisionResult(
        false,
        false,
        approved,
        null,
        "The action is no longer pending or belongs to a different execution session."
      );
    }

    return await pending.DecideAsync(
      approved,
      editedText,
      cancellationToken
    );
  }

  public async Task<ApprovalRevisionResult> TryReviseAsync(
    string actionId,
    string browserSessionId,
    string executionSessionId,
    string editedText,
    CancellationToken cancellationToken
  )
  {
    if (
      !_pending.TryGetValue(
        actionId,
        out var pending
      )
      || !pending.BelongsTo(
        browserSessionId,
        executionSessionId
      )
    )
    {
      return new ApprovalRevisionResult(
        false,
        false,
        null,
        "The action is no longer pending or belongs to a different execution session."
      );
    }

    return await pending.ReviseAsync(
      editedText,
      cancellationToken
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

  private sealed class PendingApproval
  {
    private readonly SemaphoreSlim _gate = new(
      1,
      1
    );
    private readonly Func<ValidatedLocalAction, string, CancellationToken, Task<ApprovalRevisionValidation>> _revisionValidator;
    private ValidatedLocalAction _action;
    private int _revisionCount;

    public PendingApproval(
      string browserSessionId,
      string executionSessionId,
      ValidatedLocalAction action,
      Func<ValidatedLocalAction, string, CancellationToken, Task<ApprovalRevisionValidation>> revisionValidator,
      TaskCompletionSource<ApprovalOutcome> source
    )
    {
      BrowserSessionId = browserSessionId;
      ExecutionSessionId = executionSessionId;
      _action = action;
      _revisionValidator = revisionValidator;
      Source = source;
    }

    public string BrowserSessionId { get; }

    public string ExecutionSessionId { get; }

    public TaskCompletionSource<ApprovalOutcome> Source { get; }

    public bool BelongsTo(
      string browserSessionId,
      string executionSessionId
    )
    {
      return string.Equals(
        BrowserSessionId,
        browserSessionId,
        StringComparison.Ordinal
      ) && string.Equals(
        ExecutionSessionId,
        executionSessionId,
        StringComparison.Ordinal
      );
    }

    public async Task<ApprovalDecisionResult> DecideAsync(
      bool approved,
      string? editedText,
      CancellationToken cancellationToken
    )
    {
      await _gate.WaitAsync(
        cancellationToken
      );

      try
      {
        if (Source.Task.IsCompleted)
        {
          return new ApprovalDecisionResult(
            false,
            false,
            approved,
            null,
            "The action is no longer pending."
          );
        }

        if (approved && editedText is not null)
        {
          var validation = await _revisionValidator(
            _action,
            editedText,
            cancellationToken
          );

          if (!validation.Accepted || validation.Action is null)
          {
            return new ApprovalDecisionResult(
              true,
              false,
              true,
              null,
              validation.Diagnostic ?? "The edited command is invalid."
            );
          }

          _action = validation.Action with
          {
            ActionId = _action.ActionId
          };
          _revisionCount++;
        }

        var accepted = Source.TrySetResult(
          new ApprovalOutcome(
            approved,
            _action,
            _revisionCount > 0
          )
        );
        return new ApprovalDecisionResult(
          true,
          accepted,
          approved,
          accepted ? _action : null,
          accepted ? null : "The action is no longer pending."
        );
      }
      finally
      {
        _gate.Release();
      }
    }

    public async Task<ApprovalRevisionResult> ReviseAsync(
      string editedText,
      CancellationToken cancellationToken
    )
    {
      await _gate.WaitAsync(
        cancellationToken
      );

      try
      {
        if (Source.Task.IsCompleted)
        {
          return new ApprovalRevisionResult(
            false,
            false,
            null,
            "The action is no longer pending."
          );
        }

        var validation = await _revisionValidator(
          _action,
          editedText,
          cancellationToken
        );

        if (
          !validation.Accepted
          || validation.Action is null
        )
        {
          return new ApprovalRevisionResult(
            true,
            false,
            null,
            validation.Diagnostic ?? "The edited command is invalid."
          );
        }

        _action = validation.Action with
        {
          ActionId = _action.ActionId
        };
        _revisionCount++;
        return new ApprovalRevisionResult(
          true,
          true,
          _action
        );
      }
      finally
      {
        _gate.Release();
      }
    }
  }
}
