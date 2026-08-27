using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;
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

  Task<DurableSupervisionRunView?> StartAsync(
    string runId,
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
    ResumeSupervisionRunRequest request,
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
      var restoredRuntime = (checkpoint.Runtime
        ?? SupervisionRuntimeView.Empty(recoverableInCurrentProcess: false)) with
      {
        RecoverableInCurrentProcess = false
      };
      var state = new LiveRunState(
        checkpoint,
        MaximumEventsPerRun,
        restoredRuntime,
        [],
        []
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
      var recoveryService = scope.ServiceProvider.GetRequiredService<ISupervisionRecoveryService>();
      SupervisionResumeEligibility eligibility;
      SupervisionReconciliationResult? reconciliation = null;
      string? recoveryFailureCode = null;
      try
      {
        eligibility = await routes.EvaluateResumeAsync(
          checkpoint,
          cancellationToken
        );
        if (eligibility.Eligible)
        {
          reconciliation = await recoveryService.ReconcileAsync(
            checkpoint,
            manualContinuation: false,
            cancellationToken
          );
        }
      }
      catch (SupervisionException exception)
      {
        recoveryFailureCode = exception.Code;
        eligibility = new SupervisionResumeEligibility(
          false,
          exception.Message
        );
        _logger.LogWarning(
          exception,
          "Supervision run {RunId} requires user reconciliation after startup recovery failed with {Code}.",
          checkpoint.RunId,
          exception.Code
        );
      }
      catch (Exception exception) when (exception is not OperationCanceledException)
      {
        recoveryFailureCode = "supervision-recovery-unavailable";
        eligibility = new SupervisionResumeEligibility(
          false,
          "The Host could not reconcile this durable run during startup."
        );
        _logger.LogError(
          exception,
          "Unexpected startup recovery failure for supervision run {RunId}.",
          checkpoint.RunId
        );
      }
      var autoSafe = string.Equals(
        checkpoint.ResumePolicy,
        SupervisionResumePolicies.AutoSafe,
        StringComparison.Ordinal
      );
      var autoSafeEligible = autoSafe
        && eligibility.Eligible
        && reconciliation?.Eligible == true;
      var stateId = autoSafeEligible
        ? DurableSupervisionRunStates.Prepared
        : autoSafe
          ? DurableSupervisionRunStates.AwaitingUser
          : DurableSupervisionRunStates.InterruptedRecoverable;
      var eventType = autoSafeEligible
        ? SupervisionEventTypeIds.RecoveryEligible
        : autoSafe
          ? SupervisionEventTypeIds.ReconciliationRequired
          : SupervisionEventTypeIds.InterruptedRecoverable;
      var waitCode = eligibility.Eligible
        ? reconciliation?.WaitCode
        : recoveryFailureCode ?? "supervision-recovery-route-ineligible";
      var message = autoSafeEligible
        ? "The durable run passed every auto-safe route, workspace, instruction, action, approval, and budget predicate."
        : eligibility.Reason
          ?? reconciliation?.Reason
          ?? "The prior Host process stopped; explicit resume is required.";
      await TransitionAsync(
        state,
        stateId,
        SupervisionRunPhases.Recovery,
        eventType,
        message,
        terminal: false,
        autoResumeEligible: autoSafeEligible,
        waitReason: autoSafeEligible
          ? null
          : message,
        browserSessionId: null,
        cancellationToken,
        runtime: autoSafeEligible ? reconciliation!.Runtime : restoredRuntime,
        recovery: autoSafeEligible ? reconciliation!.Recovery : checkpoint.Recovery,
        waitCode: autoSafeEligible
          ? null
          : autoSafe
            ? waitCode
            : "supervision-recovery-manual-required",
        captureRecovery: false
      );
      if (autoSafeEligible)
      {
        _ = await StartAsync(checkpoint.RunId, cancellationToken);
      }
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
    request = request with { ClientRunId = runId };
    var resolution = await routes.ResolveAsync(
      request,
      cancellationToken
    );
    ValidateLiveInput(request);
    _ = scope.ServiceProvider.GetRequiredService<IImageAttachmentValidator>()
      .Validate(request.Images);
    var approvalPolicy = SupervisionRequestPolicy.NormalizeApprovalPolicy(
      request.ApprovalPolicy
    );
    var resumePolicy = SupervisionRequestPolicy.NormalizeResumePolicy(
      request.ResumePolicy
    );
    var now = DateTimeOffset.UtcNow;
    var initialRuntime = SupervisionRuntimeView.Empty();
    var initialEvent = new SupervisionRunEvent(
      runId,
      1,
      SupervisionEventTypeIds.Prepared,
      now,
      DurableSupervisionRunStates.Prepared,
      resolution.HistoryEnabled
        ? "Durable supervised run prepared and checkpointed with one fixed local route."
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
      string.Empty,
      initialRuntime
    );

    if (checkpoint.Durable)
    {
      var recovery = scope.ServiceProvider.GetRequiredService<ISupervisionRecoveryService>();
      checkpoint = checkpoint with
      {
        Recovery = (await recovery.CaptureAsync(
          checkpoint,
          initialRuntime,
          [],
          cancellationToken
        )) with
        {
          ImagesPending = request.Images?.Count > 0
        }
      };
      checkpoint = await _checkpoints.WriteAsync(
        checkpoint,
        null,
        cancellationToken
      );
    }

    var state = new LiveRunState(
      checkpoint,
      MaximumEventsPerRun,
      initialRuntime,
      request.History?.ToArray() ?? [],
      request.Images?.ToArray() ?? []
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

  public async Task<DurableSupervisionRunView?> StartAsync(
    string runId,
    CancellationToken cancellationToken
  )
  {
    if (!_runs.TryGetValue(
      NormalizeExistingRunId(runId),
      out var state
    ))
    {
      return null;
    }
    if (state.CreateView().Terminal || !state.TryReserveExecution())
    {
      return state.CreateView();
    }

    try
    {
      if (!state.Runtime.RecoverableInCurrentProcess)
      {
        state.ReleaseExecutionReservation();
        return await TransitionAsync(
          state,
          DurableSupervisionRunStates.AwaitingUser,
          SupervisionRunPhases.Recovery,
          SupervisionEventTypeIds.AwaitingUser,
          "The process-recovered checkpoint has not passed Host reconciliation.",
          terminal: false,
          autoResumeEligible: false,
          waitReason: "Reconcile the checkpoint before starting execution.",
          browserSessionId: null,
          cancellationToken,
          waitCode: "supervision-recovery-not-reconciled"
        );
      }
      if (HasWorkspaceExecutionOwner(state))
      {
        state.ReleaseExecutionReservation();
        return await TransitionAsync(
          state,
          DurableSupervisionRunStates.AwaitingUser,
          SupervisionRunPhases.Recovery,
          SupervisionEventTypeIds.ReconciliationRequired,
          "Another supervised run currently owns this workspace execution slot.",
          terminal: false,
          autoResumeEligible: false,
          waitReason: "Wait for the active workspace run to finish, then resume this run.",
          browserSessionId: null,
          cancellationToken,
          waitCode: "supervision-recovery-workspace-busy"
        );
      }

      var started = await TransitionAsync(
        state,
        DurableSupervisionRunStates.Running,
        SupervisionRunPhases.Decomposing,
        SupervisionEventTypeIds.Started,
        "The Host-owned supervised execution loop started on its fixed local route.",
        terminal: false,
        autoResumeEligible: false,
        waitReason: null,
        browserSessionId: null,
        cancellationToken
      );
      state.AttachExecution(
        ExecuteOwnedAsync(state)
      );
      return started;
    }
    catch
    {
      state.ReleaseExecutionReservation();
      throw;
    }
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
    state.Cancel();
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
      CancellationToken.None
    );
  }

  public async Task<DurableSupervisionRunView?> ResumeAsync(
    string runId,
    ResumeSupervisionRunRequest request,
    CancellationToken cancellationToken
  )
  {
    var browserSessionId = request.BrowserSessionId;
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
    ValidateHistoryInput(
      request.History,
      "supervision-resume"
    );
    _ = scope.ServiceProvider.GetRequiredService<IImageAttachmentValidator>()
      .Validate(request.Images);
    if (
      state.Checkpoint.Recovery?.ImagesPending == true
      && request.Images is not { Count: > 0 }
    )
    {
      return await TransitionAsync(
        state,
        DurableSupervisionRunStates.AwaitingUser,
        SupervisionRunPhases.Recovery,
        SupervisionEventTypeIds.ReconciliationRequired,
        "The first worker turn requires image attachments that were not persisted.",
        terminal: false,
        autoResumeEligible: false,
        waitReason: "Attach the original images to Resume or discard this recovery state.",
        browserSessionId,
        cancellationToken,
        waitCode: "supervision-recovery-images-required",
        captureRecovery: false
      );
    }
    state.ReplaceInputs(request.History ?? [], request.Images ?? []);
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
        cancellationToken,
        waitCode: "supervision-recovery-route-ineligible",
        captureRecovery: false
      );
    }

    var checkpoint = state.Checkpoint;
    if (request.Images is { Count: > 0 } && checkpoint.Recovery is not null)
    {
      checkpoint = checkpoint with
      {
        Recovery = checkpoint.Recovery with { ImagesPending = false }
      };
    }
    var recoveryService = scope.ServiceProvider.GetRequiredService<ISupervisionRecoveryService>();
    var reconciliation = await recoveryService.ReconcileAsync(
      checkpoint,
      manualContinuation: true,
      cancellationToken
    );
    if (!reconciliation.Eligible)
    {
      return await TransitionAsync(
        state,
        DurableSupervisionRunStates.AwaitingUser,
        SupervisionRunPhases.Recovery,
        SupervisionEventTypeIds.ReconciliationRequired,
        reconciliation.Reason,
        terminal: false,
        autoResumeEligible: false,
        waitReason: reconciliation.Reason,
        browserSessionId,
        cancellationToken,
        runtime: reconciliation.Runtime,
        recovery: reconciliation.Recovery,
        waitCode: reconciliation.WaitCode,
        captureRecovery: false
      );
    }

    var resumed = await TransitionAsync(
      state,
      DurableSupervisionRunStates.Prepared,
      SupervisionRunPhases.Recovery,
      SupervisionEventTypeIds.Resumed,
      "The durable checkpoint was reconciled and reconstructed from current Host facts.",
      terminal: false,
      autoResumeEligible: string.Equals(
        state.Checkpoint.ResumePolicy,
        SupervisionResumePolicies.AutoSafe,
        StringComparison.Ordinal
      ),
      waitReason: null,
      browserSessionId,
      cancellationToken,
      runtime: reconciliation.Runtime,
      recovery: reconciliation.Recovery,
      waitCode: null,
      captureRecovery: false
    );
    _ = resumed;
    return await StartAsync(runId, cancellationToken);
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

  private async Task ExecuteOwnedAsync(LiveRunState state)
  {
    try
    {
      using var scope = _scopeFactory.CreateScope();
      var engine = scope.ServiceProvider.GetRequiredService<
        ISupervisionExecutionEngine
      >();
      var input = new SupervisionExecutionInput(
        state.Checkpoint,
        state.Runtime,
        state.History,
        state.Images,
        new DurableExecutionActionJournal(this, state)
      );
      await foreach (var update in engine.ExecuteAsync(
        input,
        state.ExecutionToken
      ))
      {
        await TransitionAsync(
          state,
          update.State,
          update.Phase,
          update.EventType,
          update.Message,
          update.Terminal,
          autoResumeEligible: false,
          waitReason: update.WaitReason,
          browserSessionId: null,
          cancellationToken: CancellationToken.None,
          runtime: update.Runtime,
          role: update.Role,
          contextId: update.ContextId,
          workItemId: update.WorkItemId
        );
      }
    }
    catch (OperationCanceledException) when (state.ExecutionToken.IsCancellationRequested)
    {
      await TransitionAsync(
        state,
        DurableSupervisionRunStates.Cancelled,
        state.Checkpoint.Phase,
        SupervisionEventTypeIds.Cancelled,
        "The Host-owned supervision run was cancelled.",
        terminal: true,
        autoResumeEligible: false,
        waitReason: null,
        browserSessionId: null,
        cancellationToken: CancellationToken.None
      );
    }
    catch (Exception exception)
    {
      _logger.LogError(
        exception,
        "Supervision run {RunId} failed in the Host-owned execution loop.",
        state.Checkpoint.RunId
      );
      await TransitionAsync(
        state,
        DurableSupervisionRunStates.Blocked,
        state.Checkpoint.Phase,
        SupervisionEventTypeIds.Blocked,
        "The supervised execution loop stopped after an unrecoverable Host failure.",
        terminal: true,
        autoResumeEligible: false,
        waitReason: exception.Message,
        browserSessionId: null,
        cancellationToken: CancellationToken.None,
        runtime: state.Runtime with
        {
          ActiveRole = null,
          LastFailure = exception.Message
        }
      );
    }
    finally
    {
      state.CompleteExecution();
    }
  }

  private static void ValidateLiveInput(PrepareSupervisionRunRequest request)
  {
    ValidateHistoryInput(
      request.History,
      "supervision-prepare"
    );
  }

  private static void ValidateHistoryInput(
    IReadOnlyList<ChatMessage>? input,
    string stage
  )
  {
    var history = input ?? [];
    if (
      history.Count > 100
      || history.Sum(message => message.Content?.Length ?? 0) > 262_144
    )
    {
      throw new SupervisionException(
        "supervision-history-too-large",
        stage,
        "The live supervision conversation context exceeds its bounded input limit.",
        true,
        413
      );
    }
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
    CancellationToken cancellationToken,
    SupervisionRuntimeView? runtime = null,
    string? role = null,
    string? contextId = null,
    string? workItemId = null,
    SupervisionRecoverySnapshot? recovery = null,
    string? waitCode = null,
    bool captureRecovery = true
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
          current,
          state.Runtime
        );
      }

      var nextSequence = current.Events.LastOrDefault()?.Sequence + 1 ?? 1;
      var now = DateTimeOffset.UtcNow;
      var effectiveRuntime = runtime ?? state.Runtime;
      var progressEvent = new SupervisionRunEvent(
        current.RunId,
        nextSequence,
        eventType,
        now,
        runState,
        message,
        terminal,
        role,
        contextId,
        workItemId,
        effectiveRuntime.CompletedItems,
        effectiveRuntime.TotalItems
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
        IntegritySha256 = string.Empty,
        Runtime = effectiveRuntime,
        Recovery = recovery ?? current.Recovery,
        WaitCode = waitCode
      };

      if (next.Durable)
      {
        if (captureRecovery && recovery is null)
        {
          using var scope = _scopeFactory.CreateScope();
          var recoveryService = scope.ServiceProvider.GetRequiredService<ISupervisionRecoveryService>();
          next = next with
          {
            Recovery = await recoveryService.CaptureAsync(
              next,
              effectiveRuntime,
              current.Recovery?.Actions,
              cancellationToken
            )
          };
        }
        if (
          next.Recovery is not null
          && eventType == SupervisionEventTypeIds.WorkerClaimed
        )
        {
          next = next with
          {
            Recovery = next.Recovery with { ImagesPending = false }
          };
        }
        next = await _checkpoints.WriteAsync(
          next,
          current.Revision,
          cancellationToken
        );
      }

      state.Commit(next, effectiveRuntime);
      if (terminal)
      {
        state.Cancel();
      }
      return SupervisionViewFactory.Create(
        next,
        state.Runtime
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

  private bool HasWorkspaceExecutionOwner(LiveRunState candidate)
  {
    return _runs.Values.Any(state =>
      !ReferenceEquals(state, candidate)
      && string.Equals(
        state.Checkpoint.WorkspaceId,
        candidate.Checkpoint.WorkspaceId,
        StringComparison.Ordinal
      )
      && state.CreateView().State is DurableSupervisionRunStates.Running
        or DurableSupervisionRunStates.Cancelling
    );
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

  private async Task RecordActionAsync(
    LiveRunState state,
    ValidatedLocalAction action,
    string phase,
    bool requiresApproval,
    string? result,
    CancellationToken cancellationToken
  )
  {
    var checkpoint = state.Checkpoint;
    var runtime = state.Runtime;
    var existing = checkpoint.Recovery?.Actions.FirstOrDefault(candidate => string.Equals(
      candidate.ActionId,
      action.ActionId,
      StringComparison.Ordinal
    ));
    var now = DateTimeOffset.UtcNow;
    var entry = new SupervisionActionCheckpoint(
      action.ActionId,
      runtime.Contexts.FirstOrDefault(context => context.State == SupervisionContextStates.Active)?.Id
        ?? runtime.Contexts.LastOrDefault(context => context.Role == "worker")?.Id
        ?? "worker-reconstructed",
      runtime.ActiveWorkItemId,
      action.Tool,
      phase,
      action.ReadOnly,
      requiresApproval,
      Hash(action.Arguments.GetRawText()),
      (action.PendingFileChanges
        ?? (action.PendingFileChange is null ? [] : [action.PendingFileChange])).Select(change => new SupervisionActionFileEffect(
        change.RelativePath.Replace('\\', '/'),
        change.Operation,
        change.ExistedBefore,
        change.OriginalHash,
        change.ExpectedFinalHash
      )).ToArray(),
      existing?.PreparedAt ?? now,
      now,
      string.IsNullOrEmpty(result) ? null : Hash(result),
      existing?.Reconciliation
    );
    var actions = (checkpoint.Recovery?.Actions ?? [])
      .Where(candidate => !string.Equals(candidate.ActionId, action.ActionId, StringComparison.Ordinal))
      .Append(entry)
      .TakeLast(64)
      .ToArray();
    using var scope = _scopeFactory.CreateScope();
    var recoveryService = scope.ServiceProvider.GetRequiredService<ISupervisionRecoveryService>();
    var recovery = await recoveryService.CaptureAsync(
      checkpoint,
      runtime,
      actions,
      cancellationToken
    );
    var eventType = phase switch
    {
      ExecutionActionJournalPhases.Prepared => SupervisionEventTypeIds.ActionPrepared,
      ExecutionActionJournalPhases.AwaitingApproval => SupervisionEventTypeIds.ActionAwaitingApproval,
      ExecutionActionJournalPhases.InFlight => SupervisionEventTypeIds.ActionInFlight,
      ExecutionActionJournalPhases.Committed => SupervisionEventTypeIds.ActionCommitted,
      ExecutionActionJournalPhases.Rejected => SupervisionEventTypeIds.ActionRejected,
      _ => SupervisionEventTypeIds.ActionFailed
    };
    await TransitionAsync(
      state,
      checkpoint.State,
      checkpoint.Phase,
      eventType,
      $"Host action {action.ActionId} ({action.Tool}) entered durable phase {phase}.",
      terminal: false,
      autoResumeEligible: false,
      waitReason: null,
      browserSessionId: null,
      cancellationToken,
      runtime,
      role: "worker",
      contextId: entry.ContextId,
      workItemId: entry.WorkItemId,
      recovery,
      captureRecovery: false
    );
  }

  private static string Hash(string value)
  {
    return Convert.ToHexString(
      SHA256.HashData(Encoding.UTF8.GetBytes(value))
    ).ToLowerInvariant();
  }

  private sealed class DurableExecutionActionJournal(
    DurableSupervisionRunCoordinator owner,
    LiveRunState state
  ) : IExecutionActionJournal
  {
    public Task RecordAsync(
      ValidatedLocalAction action,
      string phase,
      bool requiresApproval,
      string? result,
      CancellationToken cancellationToken
    )
    {
      return owner.RecordActionAsync(
        state,
        action,
        phase,
        requiresApproval,
        result,
        cancellationToken
      );
    }
  }

  private sealed class LiveRunState
  {
    private readonly object _gate = new();
    private readonly int _maximumEvents;
    private DurableSupervisionCheckpoint _checkpoint;
    private SupervisionRuntimeView _runtime;
    private TaskCompletionSource _changed = NewSignal();
    private readonly CancellationTokenSource _cancellation = new();
    private bool _executionReserved;
    private Task? _execution;

    public LiveRunState(
      DurableSupervisionCheckpoint checkpoint,
      int maximumEvents,
      SupervisionRuntimeView runtime,
      IReadOnlyList<ChatMessage> history,
      IReadOnlyList<ChatImageAttachment> images
    )
    {
      _checkpoint = checkpoint;
      _maximumEvents = maximumEvents;
      _runtime = runtime;
      History = history;
      Images = images;
    }

    public SemaphoreSlim TransitionGate { get; } = new(
      1,
      1
    );

    public IReadOnlyList<ChatMessage> History { get; private set; }

    public IReadOnlyList<ChatImageAttachment> Images { get; private set; }

    public CancellationToken ExecutionToken => _cancellation.Token;

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

    public SupervisionRuntimeView Runtime
    {
      get
      {
        lock (_gate)
        {
          return _runtime;
        }
      }
    }

    public DurableSupervisionRunView CreateView()
    {
      lock (_gate)
      {
        return SupervisionViewFactory.Create(
          _checkpoint,
          _runtime
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

    public void Commit(
      DurableSupervisionCheckpoint checkpoint,
      SupervisionRuntimeView? runtime = null
    )
    {
      TaskCompletionSource signal;
      lock (_gate)
      {
        _checkpoint = checkpoint;
        _runtime = runtime ?? _runtime;
        signal = _changed;
        _changed = NewSignal();
      }
      signal.TrySetResult();
    }

    public void Cancel()
    {
      _cancellation.Cancel();
    }

    public void ReplaceInputs(
      IReadOnlyList<ChatMessage> history,
      IReadOnlyList<ChatImageAttachment> images
    )
    {
      lock (_gate)
      {
        History = history.ToArray();
        Images = images.ToArray();
      }
    }

    public bool TryReserveExecution()
    {
      lock (_gate)
      {
        if (
          _executionReserved
          || _execution is { IsCompleted: false }
          || DurableSupervisionRunStates.IsTerminal(_checkpoint.State)
          || !string.Equals(
            _checkpoint.State,
            DurableSupervisionRunStates.Prepared,
            StringComparison.Ordinal
          )
        )
        {
          return false;
        }
        _executionReserved = true;
        return true;
      }
    }

    public void AttachExecution(Task execution)
    {
      lock (_gate)
      {
        _execution = execution;
        _executionReserved = false;
      }
    }

    public void ReleaseExecutionReservation()
    {
      lock (_gate)
      {
        _executionReserved = false;
      }
    }

    public void CompleteExecution()
    {
      lock (_gate)
      {
        _executionReserved = false;
      }
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
