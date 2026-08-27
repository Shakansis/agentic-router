using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticRouter.Api.Supervision;

public interface IDurableSupervisionRunCoordinator
{
  Task InitializeAsync(CancellationToken cancellationToken);

  Task<SupervisionRunStartView> PrepareAsync(
    PrepareSupervisionRunRequest request,
    CancellationToken cancellationToken
  );

  Task<SupervisionRunListView> ListAsync(
    CancellationToken cancellationToken
  );

  bool TryGetView(
    string runId,
    out DurableSupervisionRunView view
  );

  IAsyncEnumerable<SupervisionRunEvent> SubscribeAsync(
    string runId,
    long afterSequence,
    bool follow,
    CancellationToken cancellationToken
  );

  Task<DurableSupervisionRunView?> CancelAsync(
    string runId,
    CancellationToken cancellationToken
  );

  Task<DurableSupervisionRunView?> ResumeAsync(
    string runId,
    string browserSessionId,
    CancellationToken cancellationToken
  );

  Task<bool> DiscardAsync(
    string runId,
    CancellationToken cancellationToken
  );

  Task DiscardConversationAsync(
    string workspaceId,
    string conversationSessionId,
    CancellationToken cancellationToken
  );
}

public sealed class DurableSupervisionRunCoordinator
  : IDurableSupervisionRunCoordinator
{
  private const int MaximumRuns = 20;
  private const int MaximumEventsPerRun = 256;

  private readonly ConcurrentDictionary<string, LiveRunState> _runs = new(
    StringComparer.OrdinalIgnoreCase
  );
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ISupervisionCheckpointStore _checkpoints;
  private readonly ILogger<DurableSupervisionRunCoordinator> _logger;

  public DurableSupervisionRunCoordinator(
    IServiceScopeFactory scopeFactory,
    ISupervisionCheckpointStore checkpoints,
    ILogger<DurableSupervisionRunCoordinator> logger
  )
  {
    _scopeFactory = scopeFactory;
    _checkpoints = checkpoints;
    _logger = logger;
  }

  public async Task InitializeAsync(CancellationToken cancellationToken)
  {
    var loaded = await _checkpoints.ReadAllAsync(
      cancellationToken
    );

    foreach (var issue in loaded.Issues)
    {
      _logger.LogWarning(
        "Supervision checkpoint {RelativePath} was skipped: {Code} {Message}",
        issue.RelativePath,
        issue.Code,
        issue.Message
      );
    }

    foreach (var checkpoint in loaded.Checkpoints.OrderBy(
      item => item.CreatedAt
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var state = new LiveRunState(
        checkpoint,
        MaximumEventsPerRun
      );
      if (!_runs.TryAdd(
        checkpoint.RunId,
        state
      ))
      {
        _logger.LogWarning(
          "Duplicate supervision run id {RunId} was skipped during recovery.",
          checkpoint.RunId
        );
        continue;
      }

      if (DurableSupervisionRunStates.IsTerminal(
        checkpoint.State
      ))
      {
        continue;
      }

      using var scope = _scopeFactory.CreateScope();
      var routes = scope.ServiceProvider.GetRequiredService<ISupervisionRouteResolver>();
      var eligibility = await routes.EvaluateResumeAsync(
        checkpoint,
        cancellationToken
      );
      var autoSafe = string.Equals(
        checkpoint.ResumePolicy,
        SupervisionResumePolicies.AutoSafe,
        StringComparison.Ordinal
      );
      var stateId = autoSafe && eligibility.Eligible
        ? DurableSupervisionRunStates.Prepared
        : DurableSupervisionRunStates.InterruptedRecoverable;
      var eventType = autoSafe && eligibility.Eligible
        ? SupervisionEventTypeIds.AutoResumeEligible
        : SupervisionEventTypeIds.InterruptedRecoverable;
      var message = autoSafe && eligibility.Eligible
        ? "The durable run passed Milestone 0 auto-safe recovery predicates; no model was invoked."
        : eligibility.Reason
          ?? "The prior Host process stopped; explicit resume is required.";
      await TransitionAsync(
        state,
        stateId,
        SupervisionRunPhases.Recovery,
        eventType,
        message,
        terminal: false,
        autoResumeEligible: autoSafe && eligibility.Eligible,
        waitReason: autoSafe && eligibility.Eligible
          ? null
          : message,
        browserSessionId: null,
        cancellationToken
      );
    }

    EvictTerminalRuns();
  }

  public async Task<SupervisionRunStartView> PrepareAsync(
    PrepareSupervisionRunRequest request,
    CancellationToken cancellationToken
  )
  {
    var runId = NormalizeRunId(
      request.ClientRunId
    );
    EvictTerminalRuns();
    if (_runs.Count >= MaximumRuns)
    {
      throw new SupervisionException(
        "supervision-live-capacity",
        "supervision-prepare",
        "Too many durable supervision runs are retained.",
        true,
        409
      );
    }

    using var scope = _scopeFactory.CreateScope();
    var routes = scope.ServiceProvider.GetRequiredService<ISupervisionRouteResolver>();
    var resolution = await routes.ResolveAsync(
      request,
      cancellationToken
    );
    var approvalPolicy = SupervisionRequestPolicy.NormalizeApprovalPolicy(
      request.ApprovalPolicy
    );
    var resumePolicy = SupervisionRequestPolicy.NormalizeResumePolicy(
      request.ResumePolicy
    );
    var now = DateTimeOffset.UtcNow;
    var initialEvent = new SupervisionRunEvent(
      runId,
      1,
      SupervisionEventTypeIds.Prepared,
      now,
      DurableSupervisionRunStates.Prepared,
      resolution.HistoryEnabled
        ? "Durable supervised run prepared and checkpointed; the Milestone 0 execution engine remains inactive."
        : "Volatile supervised run prepared; local history is disabled and restart recovery is unavailable."
    );
    var checkpoint = new DurableSupervisionCheckpoint(
      DurableSupervisionCheckpoint.CurrentSchemaVersion,
      runId,
      resolution.WorkspaceId,
      resolution.ConversationSessionId,
      request.BrowserSessionId,
      request.Objective.Trim(),
      SupervisionRequestPolicy.Hash(
        request.Objective.Trim()
      ),
      resolution.Route,
      approvalPolicy,
      resumePolicy,
      DurableSupervisionRunStates.Prepared,
      SupervisionRunPhases.Foundation,
      1,
      resolution.HistoryEnabled,
      false,
      null,
      [initialEvent],
      now,
      now,
      string.Empty
    );

    if (checkpoint.Durable)
    {
      checkpoint = await _checkpoints.WriteAsync(
        checkpoint,
        null,
        cancellationToken
      );
    }

    var state = new LiveRunState(
      checkpoint,
      MaximumEventsPerRun
    );
    if (!_runs.TryAdd(
      runId,
      state
    ))
    {
      if (checkpoint.Durable)
      {
        await _checkpoints.DeleteAsync(
          checkpoint.WorkspaceId,
          checkpoint.ConversationSessionId,
          checkpoint.RunId,
          cancellationToken
        );
      }
      throw new SupervisionException(
        "supervision-run-id-conflict",
        "supervision-prepare",
        $"Supervision run '{runId}' already exists.",
        false,
        409
      );
    }

    return new SupervisionRunStartView(
      runId,
      checkpoint.State,
      checkpoint.Durable,
      $"/api/supervision/runs/{runId}/events"
    );
  }

  public async Task<SupervisionRunListView> ListAsync(
    CancellationToken cancellationToken
  )
  {
    using var scope = _scopeFactory.CreateScope();
    var workspaces = scope.ServiceProvider.GetRequiredService<
      IWorkspaceProfileService
    >();
    var active = await workspaces.GetActiveDataAsync(
      cancellationToken
    );
    var runs = active is null
      ? []
      : _runs.Values.Select(
        state => state.CreateView()
      ).Where(
        view => string.Equals(
          view.WorkspaceId,
          active.Id,
          StringComparison.Ordinal
        )
      ).OrderByDescending(
        view => view.UpdatedAt
      ).ToArray();
    return new SupervisionRunListView(
      runs
    );
  }

  public bool TryGetView(
    string runId,
    out DurableSupervisionRunView view
  )
  {
    if (!_runs.TryGetValue(
      NormalizeExistingRunId(
        runId
      ),
      out var state
    ))
    {
      view = null!;
      return false;
    }

    view = state.CreateView();
    return true;
  }

  public async IAsyncEnumerable<SupervisionRunEvent> SubscribeAsync(
    string runId,
    long afterSequence,
    bool follow,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    if (!_runs.TryGetValue(
      NormalizeExistingRunId(
        runId
      ),
      out var state
    ))
    {
      yield break;
    }

    var cursor = Math.Max(
      0,
      afterSequence
    );
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var batch = state.ReadAfter(
        cursor
      );
      foreach (var progressEvent in batch.Events)
      {
        cursor = Math.Max(
          cursor,
          progressEvent.Sequence
        );
        yield return progressEvent;
      }

      if (
        batch.Terminal
        || !follow
      )
      {
        yield break;
      }

      await batch.Signal.WaitAsync(
        cancellationToken
      );
    }
  }

  public async Task<DurableSupervisionRunView?> CancelAsync(
    string runId,
    CancellationToken cancellationToken
  )
  {
    if (!_runs.TryGetValue(
      NormalizeExistingRunId(
        runId
      ),
      out var state
    ))
    {
      return null;
    }

    if (state.CreateView().Terminal)
    {
      return null;
    }

    await TransitionAsync(
      state,
      DurableSupervisionRunStates.Cancelling,
      state.Checkpoint.Phase,
      SupervisionEventTypeIds.Cancelling,
      "Cancellation requested for the Host-owned supervision run.",
      terminal: false,
      autoResumeEligible: false,
      waitReason: null,
      browserSessionId: null,
      cancellationToken
    );
    return await TransitionAsync(
      state,
      DurableSupervisionRunStates.Cancelled,
      state.Checkpoint.Phase,
      SupervisionEventTypeIds.Cancelled,
      "The Host-owned supervision run was cancelled.",
      terminal: true,
      autoResumeEligible: false,
      waitReason: null,
      browserSessionId: null,
      cancellationToken
    );
  }

  public async Task<DurableSupervisionRunView?> ResumeAsync(
    string runId,
    string browserSessionId,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        browserSessionId
      )
      || browserSessionId.Length > 128
    )
    {
      throw new SupervisionException(
        "supervision-browser-session-invalid",
        "supervision-resume",
        "A valid browser session identifier is required.",
        true
      );
    }

    if (!_runs.TryGetValue(
      NormalizeExistingRunId(
        runId
      ),
      out var state
    ))
    {
      return null;
    }

    var view = state.CreateView();
    if (view.State is not DurableSupervisionRunStates.InterruptedRecoverable
      and not DurableSupervisionRunStates.AwaitingUser)
    {
      throw new SupervisionException(
        "supervision-resume-state-invalid",
        "supervision-resume",
        "Only an interrupted or awaiting-user supervision run can be resumed.",
        true,
        409
      );
    }

    using var scope = _scopeFactory.CreateScope();
    var routes = scope.ServiceProvider.GetRequiredService<ISupervisionRouteResolver>();
    var eligibility = await routes.EvaluateResumeAsync(
      state.Checkpoint,
      cancellationToken
    );
    if (!eligibility.Eligible)
    {
      return await TransitionAsync(
        state,
        DurableSupervisionRunStates.AwaitingUser,
        SupervisionRunPhases.Recovery,
        SupervisionEventTypeIds.InterruptedRecoverable,
        eligibility.Reason
          ?? "The supervision run cannot be resumed safely.",
        terminal: false,
        autoResumeEligible: false,
        waitReason: eligibility.Reason,
        browserSessionId,
        cancellationToken
      );
    }

    return await TransitionAsync(
      state,
      DurableSupervisionRunStates.Prepared,
      SupervisionRunPhases.Recovery,
      SupervisionEventTypeIds.Resumed,
      "The durable supervision run was reconciled and prepared; no model was invoked in Milestone 0.",
      terminal: false,
      autoResumeEligible: string.Equals(
        state.Checkpoint.ResumePolicy,
        SupervisionResumePolicies.AutoSafe,
        StringComparison.Ordinal
      ),
      waitReason: null,
      browserSessionId,
      cancellationToken
    );
  }

  public async Task<bool> DiscardAsync(
    string runId,
    CancellationToken cancellationToken
  )
  {
    if (!_runs.TryRemove(
      NormalizeExistingRunId(
        runId
      ),
      out var state
    ))
    {
      return false;
    }

    var checkpoint = state.Checkpoint;
    state.Cancel();
    if (checkpoint.Durable)
    {
      await _checkpoints.DeleteAsync(
        checkpoint.WorkspaceId,
        checkpoint.ConversationSessionId,
        checkpoint.RunId,
        cancellationToken
      );
    }

    return true;
  }

  public async Task DiscardConversationAsync(
    string workspaceId,
    string conversationSessionId,
    CancellationToken cancellationToken
  )
  {
    var matching = _runs.Values.Where(
      state => string.Equals(
        state.Checkpoint.WorkspaceId,
        workspaceId,
        StringComparison.Ordinal
      ) && string.Equals(
        state.Checkpoint.ConversationSessionId,
        conversationSessionId,
        StringComparison.Ordinal
      )
    ).Select(
      state => state.Checkpoint.RunId
    ).ToArray();

    foreach (var runId in matching)
    {
      if (_runs.TryRemove(
        runId,
        out var state
      ))
      {
        state.Cancel();
      }
    }

    await _checkpoints.DeleteConversationAsync(
      workspaceId,
      conversationSessionId,
      cancellationToken
    );
  }

  private async Task<DurableSupervisionRunView> TransitionAsync(
    LiveRunState state,
    string runState,
    string phase,
    string eventType,
    string? message,
    bool terminal,
    bool autoResumeEligible,
    string? waitReason,
    string? browserSessionId,
    CancellationToken cancellationToken
  )
  {
    await state.TransitionGate.WaitAsync(
      cancellationToken
    );

    try
    {
      var current = state.Checkpoint;
      if (DurableSupervisionRunStates.IsTerminal(
        current.State
      ))
      {
        return SupervisionViewFactory.Create(
          current
        );
      }

      var nextSequence = current.Events.LastOrDefault()?.Sequence + 1 ?? 1;
      var now = DateTimeOffset.UtcNow;
      var progressEvent = new SupervisionRunEvent(
        current.RunId,
        nextSequence,
        eventType,
        now,
        runState,
        message,
        terminal
      );
      var next = current with
      {
        BrowserSessionId = browserSessionId ?? current.BrowserSessionId,
        State = runState,
        Phase = phase,
        Revision = checked(current.Revision + 1),
        AutoResumeEligible = autoResumeEligible,
        WaitReason = waitReason,
        Events = current.Events.Append(
          progressEvent
        ).TakeLast(
          MaximumEventsPerRun
        ).ToArray(),
        UpdatedAt = now,
        IntegritySha256 = string.Empty
      };

      if (next.Durable)
      {
        next = await _checkpoints.WriteAsync(
          next,
          current.Revision,
          cancellationToken
        );
      }

      state.Commit(
        next
      );
      if (terminal)
      {
        state.Cancel();
      }
      return SupervisionViewFactory.Create(
        next
      );
    }
    finally
    {
      state.TransitionGate.Release();
    }
  }

  private void EvictTerminalRuns()
  {
    if (_runs.Count < MaximumRuns)
    {
      return;
    }

    foreach (var candidate in _runs.Values.Where(
      state => state.CreateView().Terminal
    ).OrderBy(
      state => state.Checkpoint.CreatedAt
    ).Take(
      Math.Max(
        1,
        _runs.Count - MaximumRuns + 1
      )
    ))
    {
      _runs.TryRemove(
        candidate.Checkpoint.RunId,
        out _
      );
    }
  }

  private static string NormalizeRunId(string? clientRunId)
  {
    if (string.IsNullOrWhiteSpace(
      clientRunId
    ))
    {
      return Guid.NewGuid().ToString(
        "N"
      );
    }

    if (!Guid.TryParse(
      clientRunId,
      out var parsed
    ))
    {
      throw new SupervisionException(
        "supervision-run-id-invalid",
        "supervision-prepare",
        "Client run id must be a UUID.",
        true
      );
    }

    return parsed.ToString(
      "N"
    );
  }

  private static string NormalizeExistingRunId(string runId)
  {
    if (!Guid.TryParse(
      runId,
      out var parsed
    ))
    {
      throw new SupervisionException(
        "supervision-run-id-invalid",
        "supervision-request",
        "Supervision run id must be a UUID.",
        true
      );
    }

    return parsed.ToString(
      "N"
    );
  }

  private sealed class LiveRunState
  {
    private readonly object _gate = new();
    private readonly int _maximumEvents;
    private DurableSupervisionCheckpoint _checkpoint;
    private TaskCompletionSource _changed = NewSignal();
    private readonly CancellationTokenSource _cancellation = new();

    public LiveRunState(
      DurableSupervisionCheckpoint checkpoint,
      int maximumEvents
    )
    {
      _checkpoint = checkpoint;
      _maximumEvents = maximumEvents;
    }

    public SemaphoreSlim TransitionGate { get; } = new(
      1,
      1
    );

    public DurableSupervisionCheckpoint Checkpoint
    {
      get
      {
        lock (_gate)
        {
          return _checkpoint;
        }
      }
    }

    public DurableSupervisionRunView CreateView()
    {
      lock (_gate)
      {
        return SupervisionViewFactory.Create(
          _checkpoint
        );
      }
    }

    public LiveReadBatch ReadAfter(long afterSequence)
    {
      lock (_gate)
      {
        return new LiveReadBatch(
          _checkpoint.Events.Where(
            item => item.Sequence > afterSequence
          ).TakeLast(
            _maximumEvents
          ).ToArray(),
          DurableSupervisionRunStates.IsTerminal(
            _checkpoint.State
          ),
          _changed.Task
        );
      }
    }

    public void Commit(DurableSupervisionCheckpoint checkpoint)
    {
      TaskCompletionSource signal;
      lock (_gate)
      {
        _checkpoint = checkpoint;
        signal = _changed;
        _changed = NewSignal();
      }
      signal.TrySetResult();
    }

    public void Cancel()
    {
      _cancellation.Cancel();
    }

    private static TaskCompletionSource NewSignal()
    {
      return new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously
      );
    }
  }

  private sealed record LiveReadBatch(
    IReadOnlyList<SupervisionRunEvent> Events,
    bool Terminal,
    Task Signal
  );
}
