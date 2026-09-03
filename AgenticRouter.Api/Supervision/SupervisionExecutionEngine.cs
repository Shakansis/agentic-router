using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Supervision;

internal sealed record SupervisionExecutionInput(
  DurableSupervisionCheckpoint Checkpoint,
  SupervisionRuntimeView Runtime,
  IReadOnlyList<ChatMessage> History,
  IReadOnlyList<ChatImageAttachment> Images,
  IExecutionActionJournal? ActionJournal = null,
  ISupervisionTurnProgressSink? ProgressSink = null
);

internal sealed record SupervisionTurnProgress(
  string EventType,
  string Message,
  string Role,
  string ContextId,
  string? WorkItemId = null,
  SlowRequestStatusView? SlowRequest = null,
  ContextUsageView? ContextUsage = null,
  LocalActionEvent? LocalAction = null,
  bool Transient = false
);

internal interface ISupervisionTurnProgressSink
{
  Task ReportAsync(
    SupervisionTurnProgress progress,
    CancellationToken cancellationToken
  );

  bool CanRetryAfterInactivity();
}

internal sealed record SupervisionExecutionUpdate(
  string State,
  string Phase,
  string EventType,
  string Message,
  SupervisionRuntimeView Runtime,
  bool Terminal = false,
  string? WaitReason = null,
  string? Role = null,
  string? ContextId = null,
  string? WorkItemId = null,
  string? WaitCode = null
);

internal interface ISupervisionExecutionEngine
{
  IAsyncEnumerable<SupervisionExecutionUpdate> ExecuteAsync(
    SupervisionExecutionInput input,
    CancellationToken cancellationToken
  );
}

internal sealed class SupervisionExecutionEngine : ISupervisionExecutionEngine
{
  internal const string DecomposeMarker = "SUPERVISION_DECOMPOSE_V1";
  internal const string WorkerMarker = "SUPERVISION_WORKER_V1";
  internal const string CorrectionMarker = "SUPERVISION_CORRECTION_V1";
  internal const string RecoveryMarker = "SUPERVISION_RECOVERY_V1";
  internal const string HarnessRecoveryMarker = "SUPERVISION_HARNESS_RECOVERY_V1";
  internal const string VerifyMarker = "SUPERVISION_VERIFY_V1";
  internal const string VerifyWithValidationMarker =
    "SUPERVISION_VERIFY_WITH_VALIDATION_V1";
  internal const string AutonomousDecisionMarker =
    "SUPERVISION_AUTONOMOUS_DECISION_V1";
  internal const string WatchdogRecoveryMarker =
    "SUPERVISION_WATCHDOG_RECOVERY_V1";
  internal const string CanonicalRecoveryMarker =
    "SUPERVISION_CANONICAL_RECOVERY_V1";
  internal const string CompleteMarker = "SUPERVISION_COMPLETE_V1";

  private const int MaximumDecisionCharacters = 32_768;
  private const long MaximumEvidenceFileBytes = 32_768;
  private const int MaximumEvidenceCharacters = 96_000;
  private static readonly JsonSerializerOptions DecisionJson = new(
    JsonSerializerDefaults.Web
  )
  {
    PropertyNameCaseInsensitive = false
  };
  private readonly IExecutionSpecialistTurnService _turns;
  private readonly IExecutionSessionStore _sessions;
  private readonly ISettingsStore _settings;
  private readonly IWorkspaceProfileService _workspaces;
  private readonly ITrustedWorkspaceService _workspace;
  private readonly ISupervisionRouteResolver _routes;

  public SupervisionExecutionEngine(
    IExecutionSpecialistTurnService turns,
    IExecutionSessionStore sessions,
    ISettingsStore settings,
    IWorkspaceProfileService workspaces,
    ITrustedWorkspaceService workspace,
    ISupervisionRouteResolver routes
  )
  {
    _turns = turns;
    _sessions = sessions;
    _settings = settings;
    _workspaces = workspaces;
    _workspace = workspace;
    _routes = routes;
  }

  public async IAsyncEnumerable<SupervisionExecutionUpdate> ExecuteAsync(
    SupervisionExecutionInput input,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var checkpoint = input.Checkpoint;
    var runtime = input.Runtime;
    var settings = await _settings.GetAsync(cancellationToken);
    var maximumItems = checkpoint.Recovery?.Budgets.MaximumWorkItems
      ?? settings.ProjectAwareness.MaxPlanSteps;
    var maximumSupervisorTransitions = checkpoint.Recovery?.Budgets.MaximumSupervisorTransitions
      ?? checked(maximumItems * 2);
    var maximumWorkerAttempts = checkpoint.Recovery?.Budgets.MaximumWorkerAttempts
      ?? settings.Execution.MaxRecoveryAttemptsPerTurn;
    var maximumEvidencePaths = settings.Execution.MaxTrackedFilesPerSession;
    var activeWorkspace = await _workspaces.GetActiveDataAsync(cancellationToken);
    var validationAvailable = activeWorkspace?.ValidationProfile is not null
      || settings.ValidationProfile is not null;

    await EnsureFixedRouteAsync(checkpoint, cancellationToken);

    if (runtime.WorkItems.Count == 0)
    {
      var supervisor = CreateContext(
        "supervisor-001",
        "supervisor",
        null,
        checkpoint.Revision
      );
      runtime = runtime with
      {
        Contexts = [supervisor],
        ActiveRole = "supervisor",
        SupervisorTransitionCount = runtime.SupervisorTransitionCount + 1
      };
      yield return Update(
        DurableSupervisionRunStates.Running,
        SupervisionRunPhases.Decomposing,
        SupervisionEventTypeIds.SupervisorStarted,
        checkpoint.Takeover is null
          ? "The focused supervisor is decomposing the objective into a bounded ordered queue."
          : "The focused supervisor is reconciling the prior direct execution state and verified effects before replacing remaining work with bounded items.",
        runtime,
        role: "supervisor",
        contextId: supervisor.Id
      );

      var decomposition = await RunTurnAsync(
        checkpoint,
        supervisor,
        CreateDecompositionPrompt(
          checkpoint.Objective,
          maximumItems,
          maximumEvidencePaths,
          checkpoint.Takeover,
          IsAutonomous(checkpoint)
        ),
        input.History,
        [],
        validationAvailable,
        settings.Execution.PhaseEffort.Plan,
        input.ActionJournal,
        input.ProgressSink,
        cancellationToken
      );
      if (decomposition.Failure is not null)
      {
        yield return Blocked(
          runtime,
          "Supervisor decomposition failed: " + decomposition.Failure.Message,
          supervisor.Id,
          waitCode: decomposition.Failure.Code
        );
        yield break;
      }

      SupervisionDecision? decision = null;
      SupervisionException? decompositionError = null;
      try
      {
        decision = ParseDecision(decomposition.Answer);
        ValidateDecomposition(
          decision,
          maximumItems,
          maximumEvidencePaths
        );
      }
      catch (SupervisionException exception)
      {
        decompositionError = exception;
      }
      if (decompositionError is not null)
      {
        yield return Blocked(
          runtime,
          decompositionError.Message,
          supervisor.Id,
          waitCode: decompositionError.Code
        );
        yield break;
      }

      var items = decision!.Items!.Select(
        (item, index) => CreateWorkItem(item, index + 1)
      ).ToArray();
      runtime = runtime with
      {
        WorkItems = items,
        Contexts = [CompleteContext(supervisor, "Queue dispatched.", checkpoint.Revision)],
        ActiveRole = null,
        ActiveWorkItemId = null,
        TotalItems = items.Length,
        LastFailure = null
      };
      yield return Update(
        DurableSupervisionRunStates.Running,
        SupervisionRunPhases.Decomposing,
        SupervisionEventTypeIds.WorkQueued,
        $"The supervisor dispatched {items.Length} ordered work item(s).",
        runtime,
        role: "supervisor",
        contextId: supervisor.Id
      );
    }

    while (runtime.CompletedItems < runtime.TotalItems)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var itemIndex = runtime.WorkItems.ToList().FindIndex(
        item => item.Status is SupervisionWorkItemStates.Pending
          or SupervisionWorkItemStates.Active
          or SupervisionWorkItemStates.Verifying
      );
      if (itemIndex < 0)
      {
        yield return Blocked(
          runtime,
          "The ordered queue has no runnable item before global completion."
        );
        yield break;
      }

      var item = runtime.WorkItems[itemIndex];
      var workerId = item.WorkerContextId ?? $"worker-{itemIndex + 1:000}";
      var worker = runtime.Contexts.FirstOrDefault(
        context => string.Equals(context.Id, workerId, StringComparison.Ordinal)
      ) ?? CreateContext(
        workerId,
        "worker",
        item.Id,
        checkpoint.Revision
      );

      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        item = item with
        {
          Status = SupervisionWorkItemStates.Active,
          AttemptCount = item.AttemptCount + 1,
          WorkerContextId = worker.Id
        };
        worker = ActivateContext(worker, checkpoint.Revision);
        runtime = Replace(runtime, itemIndex, item, worker) with
        {
          ActiveRole = "worker",
          ActiveWorkItemId = item.Id
        };
        yield return Update(
          DurableSupervisionRunStates.Running,
          SupervisionRunPhases.Working,
          SupervisionEventTypeIds.WorkerStarted,
          $"Worker attempt {item.AttemptCount}/{maximumWorkerAttempts} started for {item.Id}.",
          runtime,
          role: "worker",
          contextId: worker.Id,
          workItemId: item.Id
        );

        var workerPrompt = CreateWorkerPrompt(
          item,
          IsAutonomous(checkpoint)
        );
        var workerTurn = await RunTurnAsync(
          checkpoint,
          worker,
          workerPrompt,
          input.History,
          item.AttemptCount == 1 ? input.Images : [],
          validationAvailable,
          string.IsNullOrWhiteSpace(item.LastDiscrepancy)
            ? settings.Execution.PhaseEffort.Work
            : settings.Execution.PhaseEffort.Recovery,
          input.ActionJournal,
          input.ProgressSink,
          cancellationToken
        );
        worker = SuspendContext(
          worker,
          workerTurn.Failure is null
            ? "Worker completion claim received."
            : workerTurn.Failure.Message,
          checkpoint.Revision
        );

        if (workerTurn.Failure is not null)
        {
          runtime = Replace(runtime, itemIndex, item, worker) with
          {
            LastFailure = workerTurn.Failure.Message,
            ActiveRole = null,
            ActiveWorkItemId = item.Id
          };
          if (
            workerTurn.Failure.Recoverable
            && item.AttemptCount < maximumWorkerAttempts
          )
          {
            item = item with
            {
              Status = SupervisionWorkItemStates.Pending,
              LastDiscrepancy = "Recoverable worker failure: "
                + workerTurn.Failure.Message
            };
            runtime = Replace(runtime, itemIndex, item, worker);
            yield return Update(
              DurableSupervisionRunStates.Running,
              SupervisionRunPhases.Working,
              SupervisionEventTypeIds.RetryStarted,
              "A typed recoverable worker failure returned with a changed recovery brief.",
              runtime,
              role: "worker",
              contextId: worker.Id,
              workItemId: item.Id
            );
            continue;
          }

          item = item with { Status = SupervisionWorkItemStates.Blocked };
          runtime = Replace(runtime, itemIndex, item, worker);
          yield return Blocked(
            runtime,
            "Worker failure exhausted its permitted recovery path: "
              + workerTurn.Failure.Message,
            worker.Id,
            item.Id,
            workerTurn.Failure.Code
          );
          yield break;
        }

        var evidenceRevision = checked(runtime.EvidenceRevision + 1);
        var evidence = await BuildEvidenceAsync(
          workerTurn.Review,
          item.EvidencePaths,
          evidenceRevision,
          maximumEvidencePaths,
          cancellationToken
        );
        var noProgress = string.Equals(
          item.EvidenceSha256,
          evidence.Sha256,
          StringComparison.Ordinal
        );
        item = item with
        {
          Status = SupervisionWorkItemStates.Verifying,
          EvidenceRevision = evidenceRevision,
          EvidenceSha256 = evidence.Sha256
        };
        runtime = Replace(runtime, itemIndex, item, worker) with
        {
          ActiveRole = "supervisor",
          ActiveWorkItemId = item.Id,
          EvidenceRevision = evidenceRevision,
          NoProgressCount = noProgress
            ? runtime.NoProgressCount + 1
            : 0,
          LastFailure = null
        };
        yield return Update(
          DurableSupervisionRunStates.Running,
          SupervisionRunPhases.Verifying,
          SupervisionEventTypeIds.WorkerClaimed,
          "The worker claim moved to verifying; Host evidence was captured from current artifacts.",
          runtime,
          role: "worker",
          contextId: worker.Id,
          workItemId: item.Id
        );
        if (noProgress)
        {
          yield return Update(
            DurableSupervisionRunStates.Running,
            SupervisionRunPhases.Verifying,
            SupervisionEventTypeIds.NoProgress,
            "The attempt produced no new Host-observed evidence.",
            runtime,
            role: "supervisor",
            workItemId: item.Id
          );
        }

        if (runtime.SupervisorTransitionCount >= maximumSupervisorTransitions)
        {
          yield return Blocked(
            runtime,
            "The bounded supervisor transition budget was exhausted.",
            workItemId: item.Id
          );
          yield break;
        }

        var supervisor = runtime.Contexts.First(
          context => string.Equals(
            context.Role,
            "supervisor",
            StringComparison.Ordinal
          )
        );
        supervisor = ActivateContext(supervisor, checkpoint.Revision);
        runtime = ReplaceContext(runtime, supervisor) with
        {
          SupervisorTransitionCount = runtime.SupervisorTransitionCount + 1
        };
        yield return Update(
          DurableSupervisionRunStates.Running,
          SupervisionRunPhases.Verifying,
          SupervisionEventTypeIds.VerificationStarted,
          $"The supervisor is verifying {item.Id} against evidence revision {evidenceRevision}.",
          runtime,
          role: "supervisor",
          contextId: supervisor.Id,
          workItemId: item.Id
        );

        var verification = await RunTurnAsync(
          checkpoint,
          supervisor,
          CreateVerificationPrompt(
            item,
            workerTurn.Answer,
            evidence,
            autonomous: IsAutonomous(checkpoint)
          ),
          input.History,
          [],
          validationAvailable,
          settings.Execution.PhaseEffort.Verify,
          input.ActionJournal,
          input.ProgressSink,
          cancellationToken
        );
        if (verification.Failure is not null)
        {
          yield return Blocked(
            runtime,
            "Supervisor verification failed: " + verification.Failure.Message,
            supervisor.Id,
            item.Id,
            verification.Failure.Code
          );
          yield break;
        }

        SupervisionDecision? verificationDecision = null;
        SupervisionException? verificationError = null;
        string? verificationErrorCode = null;
        try
        {
          verificationDecision = ParseDecision(verification.Answer);
          if (
            verificationDecision!.Decision == "request_validation"
            && validationAvailable
          )
          {
            if (runtime.SupervisorTransitionCount >= maximumSupervisorTransitions)
            {
              throw InvalidDecision(
                "The supervisor transition budget was exhausted before validation."
              );
            }
            runtime = runtime with
            {
              SupervisorTransitionCount = runtime.SupervisorTransitionCount + 1
            };
            verification = await RunTurnAsync(
              checkpoint,
              supervisor,
              CreateVerificationPrompt(
                item,
                workerTurn.Answer,
                evidence,
                requireValidation: true,
                autonomous: IsAutonomous(checkpoint)
              ),
              input.History,
              [],
              validationAvailable,
              settings.Execution.PhaseEffort.Verify,
              input.ActionJournal,
              input.ProgressSink,
              cancellationToken
            );
            if (verification.Failure is not null)
            {
              throw InvalidDecision(
                "The requested Host validation turn failed: "
                  + verification.Failure.Message
              );
            }
            verificationDecision = ParseDecision(verification.Answer);
          }
          if (
            IsAutonomous(checkpoint)
            && verificationDecision!.Decision == "await_user"
          )
          {
            if (runtime.SupervisorTransitionCount >= maximumSupervisorTransitions)
            {
              throw InvalidDecision(
                "The supervisor transition budget was exhausted before the autonomous decision correction."
              );
            }
            runtime = runtime with
            {
              SupervisorTransitionCount = runtime.SupervisorTransitionCount + 1
            };
            verification = await RunTurnAsync(
              checkpoint,
              supervisor,
              CreateAutonomousDecisionPrompt(
                item,
                workerTurn.Answer,
                evidence,
                verificationDecision
              ),
              input.History,
              [],
              validationAvailable,
              settings.Execution.PhaseEffort.Recovery,
              input.ActionJournal,
              input.ProgressSink,
              cancellationToken
            );
            if (verification.Failure is not null)
            {
              throw InvalidDecision(
                "The autonomous supervisor decision correction failed: "
                  + verification.Failure.Message
              );
            }
            verificationDecision = ParseDecision(verification.Answer);
            if (verificationDecision.Decision == "await_user")
            {
              throw InvalidDecision(
                "The autonomous supervisor repeated a forbidden user-decision request."
              );
            }
          }
          ValidateVerification(
            verificationDecision,
            item,
            maximumEvidencePaths
          );
        }
        catch (SupervisionException exception)
        {
          verificationError = exception;
          verificationErrorCode = exception.Code;
        }
        if (verificationError is not null)
        {
          yield return Blocked(
            runtime,
            verificationError.Message,
            supervisor.Id,
            item.Id,
            verificationErrorCode
          );
          yield break;
        }
        var currentDecision = verificationDecision!;

        if (currentDecision.Decision is "accept_work" or "replace_pending_work")
        {
          var latestEvidenceRevision = checked(runtime.EvidenceRevision + 1);
          var latestEvidence = await BuildEvidenceAsync(
            workerTurn.Review,
            item.EvidencePaths,
            latestEvidenceRevision,
            maximumEvidencePaths,
            cancellationToken
          );
          if (!string.Equals(
            evidence.Sha256,
            latestEvidence.Sha256,
            StringComparison.Ordinal
          ))
          {
            const string staleEvidenceMessage =
              "Host evidence changed while the supervisor was evaluating the completion claim. Inspect the current artifact and correct it before claiming completion again.";
            supervisor = SuspendContext(
              supervisor,
              staleEvidenceMessage,
              checkpoint.Revision
            );
            item = item with
            {
              Status = SupervisionWorkItemStates.Pending,
              EvidenceRevision = latestEvidenceRevision,
              EvidenceSha256 = latestEvidence.Sha256,
              LastDiscrepancy = staleEvidenceMessage
            };
            runtime = Replace(runtime, itemIndex, item, worker) with
            {
              Contexts = ReplaceContext(runtime, supervisor).Contexts,
              ActiveRole = null,
              EvidenceRevision = latestEvidenceRevision,
              NoProgressCount = 0,
              LastFailure = staleEvidenceMessage
            };
            yield return Update(
              DurableSupervisionRunStates.Running,
              SupervisionRunPhases.Verifying,
              SupervisionEventTypeIds.WorkRejected,
              $"The Host rejected stale evidence for {item.Id}; the workspace changed during supervisor verification.",
              runtime,
              role: "supervisor",
              contextId: supervisor.Id,
              workItemId: item.Id
            );
            if (item.AttemptCount >= maximumWorkerAttempts)
            {
              item = item with { Status = SupervisionWorkItemStates.Blocked };
              runtime = Replace(runtime, itemIndex, item, worker);
              yield return Blocked(
                runtime,
                "Worker correction attempts were exhausted after stale evidence invalidated supervisor acceptance.",
                supervisor.Id,
                item.Id
              );
              yield break;
            }
            continue;
          }
        }

        supervisor = SuspendContext(
          supervisor,
          currentDecision.Summary
            ?? currentDecision.Discrepancy
            ?? currentDecision.Decision,
          checkpoint.Revision
        );
        runtime = ReplaceContext(runtime, supervisor);

        if (currentDecision.Decision == "await_user")
        {
          runtime = runtime with
          {
            ActiveRole = null,
            LastFailure = currentDecision.Summary
              ?? "The supervisor requires a user decision."
          };
          yield return Update(
            DurableSupervisionRunStates.AwaitingUser,
            SupervisionRunPhases.Verifying,
            SupervisionEventTypeIds.AwaitingUser,
            runtime.LastFailure,
            runtime,
            waitReason: runtime.LastFailure,
            role: "supervisor",
            contextId: supervisor.Id,
            workItemId: item.Id
          );
          yield break;
        }

        if (currentDecision.Decision == "stop_blocked")
        {
          item = item with { Status = SupervisionWorkItemStates.Blocked };
          runtime = Replace(runtime, itemIndex, item, worker);
          yield return Blocked(
            runtime,
            currentDecision.Summary
              ?? "The supervisor determined that no permitted recovery remains.",
            supervisor.Id,
            item.Id
          );
          yield break;
        }

        if (currentDecision.Decision == "reject_work")
        {
          item = item with
          {
            Status = SupervisionWorkItemStates.Pending,
            LastDiscrepancy = currentDecision.Discrepancy
              + " Correction: "
              + currentDecision.CorrectiveBrief
          };
          runtime = Replace(runtime, itemIndex, item, worker) with
          {
            ActiveRole = null,
            LastFailure = item.LastDiscrepancy
          };
          yield return Update(
            DurableSupervisionRunStates.Running,
            SupervisionRunPhases.Verifying,
            SupervisionEventTypeIds.WorkRejected,
            $"The supervisor rejected {item.Id}: {Truncate(currentDecision.Discrepancy, 512)}",
            runtime,
            role: "supervisor",
            contextId: supervisor.Id,
            workItemId: item.Id
          );
          if (item.AttemptCount >= maximumWorkerAttempts)
          {
            item = item with { Status = SupervisionWorkItemStates.Blocked };
            runtime = Replace(runtime, itemIndex, item, worker);
            yield return Blocked(
              runtime,
              "Worker correction attempts were exhausted.",
              supervisor.Id,
              item.Id
            );
            yield break;
          }
          continue;
        }

        item = item with
        {
          Status = SupervisionWorkItemStates.Completed,
          LastDiscrepancy = null
        };
        worker = CompleteContext(
          worker,
          "Accepted by supervisor at evidence revision " + evidenceRevision + ".",
          checkpoint.Revision
        );
        runtime = Replace(runtime, itemIndex, item, worker) with
        {
          CompletedItems = runtime.CompletedItems + 1,
          ActiveRole = null,
          ActiveWorkItemId = null,
          NoProgressCount = 0,
          LastFailure = null
        };

        if (currentDecision.Decision == "replace_pending_work")
        {
          runtime = ReplacePendingSuffix(
            runtime,
            itemIndex,
            currentDecision.Items!,
            maximumItems
          );
        }

        yield return Update(
          DurableSupervisionRunStates.Running,
          SupervisionRunPhases.Verifying,
          SupervisionEventTypeIds.WorkAccepted,
          $"The supervisor accepted {item.Id} using current Host evidence.",
          runtime,
          role: "supervisor",
          contextId: supervisor.Id,
          workItemId: item.Id
        );
        break;
      }
    }

    if (runtime.SupervisorTransitionCount >= maximumSupervisorTransitions)
    {
      yield return Blocked(
        runtime,
        "The supervisor transition budget was exhausted before final completion."
      );
      yield break;
    }

    var finalSupervisor = runtime.Contexts.First(
      context => string.Equals(context.Role, "supervisor", StringComparison.Ordinal)
    );
    finalSupervisor = ActivateContext(finalSupervisor, checkpoint.Revision);
    runtime = ReplaceContext(runtime, finalSupervisor) with
    {
      ActiveRole = "supervisor",
      SupervisorTransitionCount = runtime.SupervisorTransitionCount + 1
    };
    yield return Update(
      DurableSupervisionRunStates.Running,
      SupervisionRunPhases.Completing,
      SupervisionEventTypeIds.SupervisorStarted,
      "The supervisor is evaluating global completion from accepted work and Host evidence.",
      runtime,
      role: "supervisor",
      contextId: finalSupervisor.Id
    );

    var completion = await RunTurnAsync(
      checkpoint,
      finalSupervisor,
      CreateCompletionPrompt(
        checkpoint.Objective,
        runtime.WorkItems,
        IsAutonomous(checkpoint)
      ),
      input.History,
      [],
      validationAvailable,
      settings.Execution.PhaseEffort.Complete,
      input.ActionJournal,
      input.ProgressSink,
      cancellationToken
    );
    if (completion.Failure is not null)
    {
      yield return Blocked(
        runtime,
        "Final supervisor verification failed: " + completion.Failure.Message,
        finalSupervisor.Id,
        waitCode: completion.Failure.Code
      );
      yield break;
    }

    SupervisionDecision? completionDecision = null;
    SupervisionException? completionError = null;
    try
    {
      completionDecision = ParseDecision(completion.Answer);
      if (
        completionDecision!.Decision != "complete_goal"
        || string.IsNullOrWhiteSpace(completionDecision.FinalAnswer)
      )
      {
        throw InvalidDecision(
          "The final supervisor must emit complete_goal with one bounded finalAnswer."
        );
      }
    }
    catch (SupervisionException exception)
    {
      completionError = exception;
    }
    if (completionError is not null)
    {
      yield return Blocked(runtime, completionError.Message, finalSupervisor.Id);
      yield break;
    }
    var finalDecision = completionDecision!;

    finalSupervisor = CompleteContext(
      finalSupervisor,
      "Global completion accepted.",
      checkpoint.Revision
    );
    runtime = ReplaceContext(runtime, finalSupervisor) with
    {
      ActiveRole = null,
      ActiveWorkItemId = null,
      FinalAnswer = Truncate(finalDecision.FinalAnswer, 16_384),
      LastFailure = null
    };
    yield return Update(
      DurableSupervisionRunStates.Completed,
      SupervisionRunPhases.Completing,
      SupervisionEventTypeIds.Completed,
      "The supervisor accepted global completion from current Host evidence.",
      runtime,
      terminal: true,
      role: "supervisor",
      contextId: finalSupervisor.Id
    );
  }

  private async Task<TurnOutcome> RunTurnAsync(
    DurableSupervisionCheckpoint checkpoint,
    SupervisionContextView context,
    string prompt,
    IReadOnlyList<ChatMessage> history,
    IReadOnlyList<ChatImageAttachment> images,
    bool validationAvailable,
    string requestedEffort,
    IExecutionActionJournal? actionJournal,
    ISupervisionTurnProgressSink? progressSink,
    CancellationToken cancellationToken
  )
  {
    await EnsureFixedRouteAsync(checkpoint, cancellationToken);
    var supervisor = string.Equals(context.Role, "supervisor", StringComparison.Ordinal);
    var autonomous = IsAutonomous(checkpoint);
    var approvalPolicy = autonomous
      ? "autonomous"
      : supervisor
        ? "ask"
        : checkpoint.ApprovalPolicy;
    var scope = supervisor
      ? CreateSupervisorToolScope(validationAvailable)
      : null;
    var settings = await _settings.GetAsync(cancellationToken);
    var turnStatusInterval = TimeSpan.FromSeconds(Math.Clamp(
      settings.Runtime.GenerationTimeoutSeconds / 3,
      1,
      30
    ));
    var activePrompt = prompt;
    var activeEffort = requestedEffort;
    var watchdogRecoveryAttempted = false;
    var canonicalRecoveryAttempted = false;
    var harnessRecoveryAttempted = false;

    while (true)
    {
      var request = new ChatRequest(
        activePrompt,
        checkpoint.Route.Model,
        history,
        "execute",
        checkpoint.Route.Harness,
        approvalPolicy,
        $"sup-{checkpoint.RunId[..12]}-{context.Id}"
          + (harnessRecoveryAttempted ? "-harness-recovery-1" : string.Empty),
        checkpoint.ConversationSessionId,
        Images: images,
        ExecutionStrategy: SupervisionExecutionStrategies.Direct
      );
      string? roleResult = null;
      var invocation = new ExecutionSpecialistTurnInvocation(
        context.Id,
        supervisor ? ExecutionContextRole.Supervisor : ExecutionContextRole.Worker,
        scope,
        UseMinimalToolInventory: supervisor,
        CaptureRoleResult: value => roleResult = value,
        ActionJournal: actionJournal,
        RequestedEffort: activeEffort
      );
      var answer = new StringBuilder();
      ProviderError? failure = null;
      ExecutionSessionSummary? summary = null;
      var watchdogTriggered = false;
      var watchdogRetrySafe = false;
      var pendingReasoning = new StringBuilder();
      var pendingCommentary = new StringBuilder();
      var lastReasoningUpdateAt = Stopwatch.StartNew();
      using var turnLifetime = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken
      );
      try
      {
        using var statusLifetime = CancellationTokenSource.CreateLinkedTokenSource(
          turnLifetime.Token
        );
        await using var stream = _turns.RunAsync(
          request,
          Guid.NewGuid().ToString("N"),
          invocation,
          turnLifetime.Token
        ).GetAsyncEnumerator(turnLifetime.Token);
        var moveNext = stream.MoveNextAsync().AsTask();
        var nextStatus = progressSink is null
          ? Task.Delay(Timeout.InfiniteTimeSpan, statusLifetime.Token)
          : Task.Delay(turnStatusInterval, statusLifetime.Token);
        try
        {
          while (true)
          {
            var completed = await Task.WhenAny(moveNext, nextStatus);
            if (
              ReferenceEquals(completed, nextStatus)
              && progressSink is not null
            )
            {
              await nextStatus;
              await progressSink.ReportAsync(
                new SupervisionTurnProgress(
                  SupervisionEventTypeIds.TurnStatus,
                  CreateTurnStatusMessage(context),
                  context.Role,
                  context.Id,
                  context.WorkItemId
                ),
                cancellationToken
              );
              nextStatus = Task.Delay(
                turnStatusInterval,
                statusLifetime.Token
              );
              continue;
            }
            if (!await moveNext)
            {
              break;
            }
            var streamEvent = stream.Current;
            if (streamEvent.LocalAction is not null)
            {
              await PublishCommentaryUpdateAsync(
                progressSink,
                context,
                pendingCommentary,
                cancellationToken
              );
            }
            if (
              progressSink is not null
              && ShouldForwardTurnActivity(streamEvent)
            )
            {
              await progressSink.ReportAsync(
                new SupervisionTurnProgress(
                  streamEvent.Type,
                  streamEvent.Message ?? streamEvent.LocalAction?.Summary ?? string.Empty,
                  context.Role,
                  context.Id,
                  context.WorkItemId,
                  LocalAction: streamEvent.LocalAction,
                  Transient: true
                ),
                cancellationToken
              );
            }
            if (
              streamEvent.Type == "context.usage"
              && streamEvent.ContextUsage is not null
              && progressSink is not null
            )
            {
              await progressSink.ReportAsync(
                new SupervisionTurnProgress(
                  "context.usage",
                  streamEvent.Message ?? string.Empty,
                  context.Role,
                  context.Id,
                  context.WorkItemId,
                  ContextUsage: streamEvent.ContextUsage
                ),
                cancellationToken
              );
            }
            if (
              streamEvent.Type is "request.slow-warning" or "request.slow-critical"
              && progressSink is not null
            )
            {
              await progressSink.ReportAsync(
                new SupervisionTurnProgress(
                  streamEvent.Type == "request.slow-critical"
                    ? SupervisionEventTypeIds.TurnSlowCritical
                    : SupervisionEventTypeIds.TurnSlowWarning,
                  streamEvent.Message
                    ?? "The active supervised turn has produced no meaningful Host activity.",
                  context.Role,
                  context.Id,
                  context.WorkItemId,
                  streamEvent.SlowRequest
                ),
                cancellationToken
              );
            }
            if (
              autonomous
              && streamEvent.Type == "request.slow-critical"
            )
            {
              watchdogTriggered = true;
              watchdogRetrySafe = !watchdogRecoveryAttempted
                && progressSink?.CanRetryAfterInactivity() == true;
              turnLifetime.Cancel();
            }
            if (streamEvent.Type == "response.delta")
            {
              answer.Append(streamEvent.Delta);
              pendingCommentary.Append(streamEvent.Delta);
            }
            if (
              !string.IsNullOrWhiteSpace(streamEvent.ReasoningDelta)
            )
            {
              pendingReasoning.Append(streamEvent.ReasoningDelta);
              if (
                pendingReasoning.Length >= 320
                || lastReasoningUpdateAt.ElapsedMilliseconds >= 1_500
              )
              {
                await PublishReasoningUpdateAsync(
                  progressSink,
                  context,
                  pendingReasoning,
                  cancellationToken
                );
                lastReasoningUpdateAt.Restart();
              }
            }
            if (streamEvent.Error is not null)
            {
              failure = streamEvent.Error;
            }
            summary = streamEvent.ExecutionSession ?? summary;
            moveNext = stream.MoveNextAsync().AsTask();
            if (watchdogTriggered)
            {
              try
              {
                _ = await moveNext;
              }
              catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested
              )
              {
              }
              break;
            }
          }
        }
        finally
        {
          statusLifetime.Cancel();
        }
      }
      catch (OperationCanceledException) when (
        watchdogTriggered
        && !cancellationToken.IsCancellationRequested
      )
      {
      }
      catch (HarnessException exception) when (
        !cancellationToken.IsCancellationRequested
      )
      {
        failure = CreateHarnessFailure(checkpoint, exception);
      }

      if (watchdogTriggered)
      {
        var repeated = watchdogRecoveryAttempted;
        failure = CreateWatchdogFailure(
          checkpoint,
          context,
          watchdogRetrySafe,
          repeated
        );
      }

      if (
        !watchdogTriggered
        && pendingReasoning.Length > 0
      )
      {
        await PublishReasoningUpdateAsync(
          progressSink,
          context,
          pendingReasoning,
          cancellationToken
        );
      }

      var review = summary is null
        ? null
        : _sessions.GetReview(summary.Id);
      var outcome = new TurnOutcome(
        Truncate(
          (roleResult ?? answer.ToString()).Trim(),
          MaximumDecisionCharacters
        ),
        failure,
        review
      );
      if (
        supervisor
        && outcome.Failure is null
        && !canonicalRecoveryAttempted
      )
      {
        try
        {
          _ = ParseDecision(outcome.Answer);
        }
        catch (SupervisionException exception) when (
          string.Equals(
            exception.Code,
            "supervision-decision-malformed",
            StringComparison.Ordinal
          )
        )
        {
          canonicalRecoveryAttempted = true;
          activePrompt = CreateCanonicalRecoveryPrompt(prompt);
          activeEffort = settings.Execution.PhaseEffort.Recovery;
          if (progressSink is not null)
          {
            await progressSink.ReportAsync(
              new SupervisionTurnProgress(
                SupervisionEventTypeIds.TurnCanonicalRecovery,
                "The supervisor omitted parseable final JSON. The Host is making one bounded, materially different canonical-output recovery attempt.",
                context.Role,
                context.Id,
                context.WorkItemId
              ),
              cancellationToken
            );
          }
          continue;
        }
      }
      if (
        supervisor
        && outcome.Failure?.Recoverable == true
        && !watchdogTriggered
        && !harnessRecoveryAttempted
      )
      {
        harnessRecoveryAttempted = true;
        activePrompt = CreateHarnessRecoveryPrompt(
          prompt,
          context,
          outcome.Failure
        );
        activeEffort = settings.Execution.PhaseEffort.Recovery;
        if (progressSink is not null)
        {
          await progressSink.ReportAsync(
            new SupervisionTurnProgress(
              SupervisionEventTypeIds.TurnHarnessRecovery,
              $"The supervisor turn hit recoverable harness failure {outcome.Failure.Code}; the Host reset its native session and is retrying once with a concise, materially different brief.",
              context.Role,
              context.Id,
              context.WorkItemId
            ),
            cancellationToken
          );
        }
        continue;
      }
      if (!watchdogTriggered || !watchdogRetrySafe)
      {
        return outcome;
      }

      watchdogRecoveryAttempted = true;
      activePrompt = CreateWatchdogRecoveryPrompt(
        prompt,
        context,
        failure!.Message
      );
      activeEffort = settings.Execution.PhaseEffort.Recovery;
      if (progressSink is not null)
      {
        await progressSink.ReportAsync(
          new SupervisionTurnProgress(
            SupervisionEventTypeIds.TurnWatchdogRecovery,
            $"The autonomous supervisor interrupted the inactive {context.Role} turn and is retrying once with an explicit materially different recovery brief.",
            context.Role,
            context.Id,
            context.WorkItemId
          ),
          cancellationToken
        );
      }
    }
  }

  private static bool ShouldForwardTurnActivity(ChatStreamEvent streamEvent)
  {
    if (streamEvent.LocalAction is not null)
    {
      return true;
    }
    if (string.IsNullOrWhiteSpace(streamEvent.Message))
    {
      return false;
    }
    return streamEvent.Type is not (
      "context.usage"
      or "response.delta"
      or "response.completed"
      or "reasoning.delta"
      or "error"
      or "request.slow-warning"
      or "request.slow-critical"
      or "request.cancelled"
    );
  }

  private static async Task PublishReasoningUpdateAsync(
    ISupervisionTurnProgressSink? progressSink,
    SupervisionContextView context,
    StringBuilder pending,
    CancellationToken cancellationToken
  )
  {
    if (progressSink is null || pending.Length == 0)
    {
      pending.Clear();
      return;
    }
    var update = pending.ToString();
    pending.Clear();
    if (string.IsNullOrWhiteSpace(update))
    {
      return;
    }
    await progressSink.ReportAsync(
      new SupervisionTurnProgress(
        SupervisionEventTypeIds.TurnReasoning,
        Truncate(update, 1_024),
        context.Role,
        context.Id,
        context.WorkItemId
      ),
      cancellationToken
    );
  }

  private static async Task PublishCommentaryUpdateAsync(
    ISupervisionTurnProgressSink? progressSink,
    SupervisionContextView context,
    StringBuilder pending,
    CancellationToken cancellationToken
  )
  {
    if (pending.Length == 0)
    {
      return;
    }
    var update = pending.ToString();
    pending.Clear();
    if (progressSink is null)
    {
      return;
    }
    var compact = string.Join(
      " ",
      update.Split(
        [' ', '\r', '\n', '\t'],
        StringSplitOptions.RemoveEmptyEntries
      )
    );
    if (
      string.IsNullOrWhiteSpace(compact)
      || compact.StartsWith('{')
      || compact.StartsWith('[')
      || compact.StartsWith("```json", StringComparison.OrdinalIgnoreCase)
    )
    {
      return;
    }
    await progressSink.ReportAsync(
      new SupervisionTurnProgress(
        SupervisionEventTypeIds.TurnCommentary,
        Truncate(compact, 384),
        context.Role,
        context.Id,
        context.WorkItemId,
        Transient: true
      ),
      cancellationToken
    );
  }

  private static string CreateTurnStatusMessage(
    SupervisionContextView context
  )
  {
    if (string.Equals(context.Role, "worker", StringComparison.Ordinal))
    {
      return context.WorkItemId is null
        ? "The worker is still executing its assigned work."
        : $"The worker is still executing {context.WorkItemId}; no new Host-observed result is available yet.";
    }
    return context.WorkItemId is null
      ? "The supervisor is still planning the bounded work queue; no new Host-observed result is available yet."
      : $"The supervisor is still verifying {context.WorkItemId}; no new Host-observed result is available yet.";
  }

  private static ProviderError CreateWatchdogFailure(
    DurableSupervisionCheckpoint checkpoint,
    SupervisionContextView context,
    bool recoverable,
    bool repeated
  )
  {
    var message = repeated
      ? "The autonomous turn again exceeded the configured critical inactivity limit after one watchdog recovery."
      : recoverable
        ? "The autonomous turn exceeded the configured critical inactivity limit without an ambiguous governed action."
        : "The autonomous turn exceeded the configured critical inactivity limit and cannot be replayed safely because a governed action may be unresolved.";
    return new ProviderError(
      "supervision-watchdog",
      message,
      message,
      checkpoint.RunId,
      checkpoint.Route.Provider,
      checkpoint.Route.Model,
      null,
      504,
      recoverable,
      new Dictionary<string, string?>(StringComparer.Ordinal)
      {
        ["code"] = "supervision-turn-inactivity",
        ["role"] = context.Role,
        ["contextId"] = context.Id,
        ["workItemId"] = context.WorkItemId
      },
      "supervision-turn-inactivity"
    );
  }

  private static ProviderError CreateHarnessFailure(
    DurableSupervisionCheckpoint checkpoint,
    HarnessException exception
  )
  {
    return new ProviderError(
      $"{exception.HarnessId}-harness",
      exception.Message,
      exception.TechnicalMessage,
      checkpoint.RunId,
      checkpoint.Route.Provider,
      checkpoint.Route.Model,
      null,
      400,
      exception.Recoverable,
      new Dictionary<string, string?>(StringComparer.Ordinal)
      {
        ["code"] = exception.Code,
        ["harnessId"] = exception.HarnessId
      },
      exception.Code
    );
  }

  private static string CreateHarnessRecoveryPrompt(
    string originalPrompt,
    SupervisionContextView context,
    ProviderError failure
  )
  {
    return $$"""
      {{originalPrompt}}

      {{HarnessRecoveryMarker}}
      The preceding {{context.Role}} turn ended with the typed recoverable harness failure {{failure.Code}}: {{failure.Message}}
      The Host reset the provider-native session. Preserve all committed Host effects and budgets. Do not repeat lengthy analysis or the failed generation pattern. Use the bounded facts in this prompt, call only a read-only tool if a missing fact is essential, and return the required canonical final JSON concisely.
      """;
  }

  private static string CreateWatchdogRecoveryPrompt(
    string originalPrompt,
    SupervisionContextView context,
    string failure
  )
  {
    return $$"""
      {{originalPrompt}}

      {{WatchdogRecoveryMarker}}
      The previous {{context.Role}} turn produced no meaningful Host activity before the configured critical inactivity limit.
      Exact failure: {{failure}}
      Do not repeat the same stalled strategy. Reinspect current Host/workspace facts, preserve committed effects, and use one materially different bounded approach. Never replay or bypass an unresolved governed action.
      """;
  }

  private static string CreateCanonicalRecoveryPrompt(
    string originalPrompt
  )
  {
    var excerpt = originalPrompt.Length <= 24_000
      ? originalPrompt
      : originalPrompt[..12_000]
        + "\n[bounded middle omitted]\n"
        + originalPrompt[^12_000..];
    return $$"""
      {{CanonicalRecoveryMarker}}
      The preceding supervisor turn did not place parseable canonical JSON in final assistant content. This is the single bounded recovery attempt; an identical request will not be repeated.
      Do not call tools. Do not repeat analysis. Use the decision and Host evidence already established in the immediately preceding turn, then emit exactly one JSON object in final assistant content with no prose or Markdown fence.

      Bounded original decision contract:
      {{excerpt}}
      """;
  }

  private async Task EnsureFixedRouteAsync(
    DurableSupervisionCheckpoint checkpoint,
    CancellationToken cancellationToken
  )
  {
    var eligibility = await _routes.EvaluateExecutionAsync(
      checkpoint,
      cancellationToken
    );
    if (!eligibility.Eligible)
    {
      throw new SupervisionException(
        "supervision-fixed-route-drift",
        "supervision-route",
        eligibility.Reason ?? "The fixed local route is no longer available.",
        false,
        409
      );
    }
  }

  private async Task<SupervisorEvidence> BuildEvidenceAsync(
    ExecutionSessionReview? review,
    IReadOnlyList<string> requestedPaths,
    long revision,
    int maximumEvidencePaths,
    CancellationToken cancellationToken
  )
  {
    var declaredPaths = requestedPaths
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    if (declaredPaths.Length > maximumEvidencePaths)
    {
      throw InvalidDecision(
        $"The supervisor declared {declaredPaths.Length} evidence paths, exceeding the configured limit of {maximumEvidencePaths}."
      );
    }
    var reviewPaths = (review?.Files.Select(file => file.RelativePath) ?? [])
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Where(path => !declaredPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    var remainingPathCapacity = maximumEvidencePaths - declaredPaths.Length;
    var paths = declaredPaths
      .Concat(reviewPaths.Take(remainingPathCapacity))
      .ToArray();
    var omittedReviewFileCount = Math.Max(
      0,
      reviewPaths.Length - remainingPathCapacity
    );
    var files = new List<SupervisorEvidenceFile>();
    var characters = 0;
    foreach (var path in paths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        var fullPath = await _workspace.ResolvePathAsync(path, cancellationToken);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length > MaximumEvidenceFileBytes)
        {
          files.Add(new SupervisorEvidenceFile(
            path,
            info.Exists ? "too-large" : "missing",
            null,
            info.Exists ? info.Length : null,
            null
          ));
          continue;
        }
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        if (characters + content.Length > MaximumEvidenceCharacters)
        {
          files.Add(new SupervisorEvidenceFile(path, "budget-exhausted", null, info.Length, null));
          continue;
        }
        characters += content.Length;
        files.Add(new SupervisorEvidenceFile(
          path,
          "read",
          Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
          info.Length,
          content
        ));
      }
      catch (Exception exception) when (
        exception is IOException
          or UnauthorizedAccessException
          or LocalActionException
      )
      {
        files.Add(new SupervisorEvidenceFile(path, "unavailable", null, null, exception.Message));
      }
    }

    var validation = review?.Validation is null
      ? "not-run"
      : $"{review.Validation.State}:{review.Validation.ProfileName}";
    var conflicts = review?.Conflicts?.Select(
      conflict => $"{conflict.RelativePath}:{conflict.Stage}"
    ).ToArray() ?? [];
    var completion = review?.Summary.CompletionStatus ?? "unavailable";
    var material = JsonSerializer.Serialize(
      new
      {
        files,
        validation,
        conflicts,
        completion,
        omittedReviewFileCount
      },
      DecisionJson
    );
    var serialized = JsonSerializer.Serialize(
      new
      {
        revision,
        files,
        validation,
        conflicts,
        completion,
        omittedReviewFileCount
      },
      DecisionJson
    );
    return new SupervisorEvidence(
      revision,
      serialized,
      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()
    );
  }

  private static ExecutionTurnToolScope CreateSupervisorToolScope(
    bool validationAvailable
  )
  {
    var tools = new List<string>
    {
      "list_files",
      "read_file",
      "get_file_info",
      "search_text"
    };
    if (validationAvailable)
    {
      tools.Add("run_validation_profile");
    }
    return new ExecutionTurnToolScope(
      tools,
      ProcessExecutionAllowed: false,
      ManualValidationRequested: false,
      ValidationProfileAvailable: validationAvailable,
      GitToolsAvailable: false,
      DirectoryCreationAvailable: false,
      DeletionAvailable: false
    );
  }

  private static string CreateDecompositionPrompt(
    string objective,
    int maximumItems,
    int maximumEvidencePaths,
    SupervisionTakeoverSnapshot? takeover,
    bool autonomous
  )
  {
    var takeoverContext = takeover is null
      ? string.Empty
      : $$"""

        HOST_AUTO_SUPERVISION_TAKEOVER_V1
        The Host stopped a direct execution at a verified boundary. The snapshot below contains only Host-retained plan/effect facts. Inspect current artifacts before relying on them, preserve verified useful work, and replace the broad plan with smaller independently verifiable work items. Do not repeat completed effects merely to recreate ownership.
        {{JsonSerializer.Serialize(takeover)}}
        """;
    return $$"""
      {{DecomposeMarker}}
      You are the focused supervisor. Decompose the original objective into the smallest ordered queue that can be independently verified. Do not mutate files.
      Original objective:
      {{objective}}
      {{takeoverContext}}
      {{(autonomous ? "AUTONOMOUS MODE: the user delegated every approval they could personally grant. Resolve ordinary ambiguities yourself, prefer the smallest reversible path, and never defer a permitted decision to the user. Hard Host boundaries remain non-negotiable." : "")}}

      Return JSON only:
      {"decision":"dispatch_work","items":[{"objective":"...","acceptanceCriteria":["..."],"evidencePaths":["relative/path"]}]}
      Maximum work items: {{maximumItems}}.
      Maximum declared evidence paths per work item: {{maximumEvidencePaths}}.
      Paths must be relative to the trusted workspace. Keep criteria concrete and observable.
      """;
  }

  private static string CreateWorkerPrompt(
    SupervisionWorkItemView item,
    bool autonomous
  )
  {
    var marker = item.LastDiscrepancy?.StartsWith(
      "CRASH_RECONCILIATION",
      StringComparison.Ordinal
    ) == true
      ? RecoveryMarker
      : string.IsNullOrWhiteSpace(item.LastDiscrepancy)
        ? WorkerMarker
        : CorrectionMarker;
    return $$"""
      {{marker}}
      Active work item {{item.Id}}:
      {{item.Objective}}

      Acceptance criteria:
      {{string.Join("\n", item.AcceptanceCriteria.Select((criterion, index) => $"{index + 1}. {criterion}"))}}

      {{(string.IsNullOrWhiteSpace(item.LastDiscrepancy) ? "" : "Supervisor correction:\n" + item.LastDiscrepancy)}}
      {{(autonomous ? "Autonomous mode is active. Do not ask the user to approve or choose a permitted implementation detail; act through Host-provided capabilities. Hard Host rejections must not be bypassed." : "")}}
      Work only on this item. Use Host-provided capabilities, preserve unrelated changes, and report a concise completion claim.
      """;
  }

  private static string CreateVerificationPrompt(
    SupervisionWorkItemView item,
    string workerClaim,
    SupervisorEvidence evidence,
    bool requireValidation = false,
    bool autonomous = false
  )
  {
    var marker = requireValidation ? VerifyWithValidationMarker : VerifyMarker;
    return $$"""
      {{marker}}
      You are the focused read-only supervisor. The worker response is only a claim. Evaluate the current Host evidence below against every acceptance criterion.
      Work item: {{item.Id}}
      Objective: {{item.Objective}}
      Acceptance criteria:
      {{string.Join("\n", item.AcceptanceCriteria.Select((criterion, index) => $"{index + 1}. {criterion}"))}}

      Worker claim:
      {{Truncate(workerClaim, 4_096)}}

      {{(item.LastDiscrepancy?.StartsWith("CRASH_RECONCILIATION", StringComparison.Ordinal) == true ? "Crash reconciliation envelope:\n" + item.LastDiscrepancy : "")}}

      Host evidence revision {{evidence.Revision}}:
      {{evidence.Json}}

      {{(requireValidation ? "Invoke run_validation_profile before deciding. If it is unavailable or fails, reject or stop_blocked." : "If a configured validation profile is materially required, decision may be request_validation.")}}
      {{(autonomous ? "AUTONOMOUS MODE: await_user is forbidden. Resolve permitted ambiguity yourself using current evidence and the smallest reversible choice. Use stop_blocked only for a genuine hard boundary or when no permitted recovery remains." : "")}}
      Return JSON only. Accept:
      {"decision":"accept_work","evidenceRevision":{{evidence.Revision}},"coveredCriteria":["exact criterion text"],"summary":"..."}
      Reject:
      {"decision":"reject_work","evidenceRevision":{{evidence.Revision}},"discrepancy":"exact observed mismatch","correctiveBrief":"bounded materially different correction"}
      Other permitted decisions: request_validation, {{(autonomous ? "stop_blocked" : "await_user, stop_blocked")}}, or replace_pending_work. replace_pending_work must also cover the current criteria and include replacement items.
      """;
  }

  private static string CreateCompletionPrompt(
    string objective,
    IReadOnlyList<SupervisionWorkItemView> items,
    bool autonomous
  )
  {
    return $$"""
      {{CompleteMarker}}
      You are the focused supervisor performing the global completion gate.
      Original objective:
      {{objective}}

      Accepted work:
      {{string.Join("\n", items.Select(item => $"- {item.Id}: {item.Objective}; evidence revision {item.EvidenceRevision}; sha256 {item.EvidenceSha256}"))}}

      {{(autonomous ? "AUTONOMOUS MODE: return the completed outcome. Do not ask the user how to continue, offer unfinished delivery choices, or defer a permitted decision." : "")}}
      All work items were accepted against current Host evidence. Return JSON only:
      {"decision":"complete_goal","finalAnswer":"one concise user-facing result grounded in the accepted evidence"}
      If completion is not justified, return {"decision":"stop_blocked","summary":"exact reason"}.
      """;
  }

  private static string CreateAutonomousDecisionPrompt(
    SupervisionWorkItemView item,
    string workerClaim,
    SupervisorEvidence evidence,
    SupervisionDecision previousDecision
  )
  {
    return $$"""
      {{AutonomousDecisionMarker}}
      You are the focused supervisor in Autonomous mode. The user delegated every approval they could personally grant, so await_user is forbidden.
      Resolve the prior ambiguity yourself using the smallest reversible permitted choice. Hard Host boundaries, protected paths, workspace escapes, stale conflicts, and exhausted recovery remain non-negotiable.

      Work item: {{item.Id}}
      Objective: {{item.Objective}}
      Acceptance criteria:
      {{string.Join("\n", item.AcceptanceCriteria.Select((criterion, index) => $"{index + 1}. {criterion}"))}}

      Worker claim:
      {{Truncate(workerClaim, 4_096)}}

      Prior forbidden decision:
      {{JsonSerializer.Serialize(previousDecision)}}

      Host evidence revision {{evidence.Revision}}:
      {{evidence.Json}}

      Return JSON only. Accept:
      {"decision":"accept_work","evidenceRevision":{{evidence.Revision}},"coveredCriteria":["exact criterion text"],"summary":"..."}
      Reject:
      {"decision":"reject_work","evidenceRevision":{{evidence.Revision}},"discrepancy":"exact observed mismatch","correctiveBrief":"bounded materially different correction"}
      Other permitted decisions: request_validation, replace_pending_work, or stop_blocked. Never return await_user.
      """;
  }

  private static bool IsAutonomous(DurableSupervisionCheckpoint checkpoint)
  {
    return string.Equals(
      checkpoint.ExecutionStrategy,
      SupervisionExecutionStrategies.Autonomous,
      StringComparison.Ordinal
    );
  }

  private static SupervisionDecision ParseDecision(string answer)
  {
    var json = answer.Trim();
    if (json.StartsWith("```", StringComparison.Ordinal))
    {
      var firstNewline = json.IndexOf('\n');
      var closing = json.LastIndexOf("```", StringComparison.Ordinal);
      if (firstNewline > 0 && closing > firstNewline)
      {
        json = json[(firstNewline + 1)..closing].Trim();
      }
    }
    try
    {
      return JsonSerializer.Deserialize<SupervisionDecision>(json, DecisionJson)
        ?? throw InvalidDecision("The supervisor decision was empty.");
    }
    catch (JsonException exception)
    {
      throw new SupervisionException(
        "supervision-decision-malformed",
        "supervision-decision",
        "The supervisor returned malformed canonical JSON; identical requests are never retried.",
        false,
        409,
        exception
      );
    }
  }

  private static void ValidateDecomposition(
    SupervisionDecision decision,
    int maximumItems,
    int maximumEvidencePaths
  )
  {
    if (
      decision.Decision != "dispatch_work"
      || decision.Items is null
      || decision.Items.Count is < 1
      || decision.Items.Count > maximumItems
    )
    {
      throw InvalidDecision(
        $"The supervisor must dispatch a non-empty bounded work queue. Received '{Truncate(decision.Decision, 128)}'."
      );
    }
    foreach (var item in decision.Items)
    {
      ValidateDecisionItem(item, maximumEvidencePaths);
    }
  }

  private static void ValidateVerification(
    SupervisionDecision decision,
    SupervisionWorkItemView item,
    int maximumEvidencePaths
  )
  {
    if (decision.Decision is "accept_work" or "replace_pending_work")
    {
      if (
        decision.EvidenceRevision != item.EvidenceRevision
        || decision.CoveredCriteria is null
        || item.AcceptanceCriteria.Any(
          criterion => !decision.CoveredCriteria.Contains(
            criterion,
            StringComparer.Ordinal
          )
        )
      )
      {
        throw InvalidDecision(
          "The supervisor acceptance did not cover every criterion at the current evidence revision."
        );
      }
      if (decision.Decision == "replace_pending_work")
      {
        if (decision.Items is null)
        {
          throw InvalidDecision("replace_pending_work requires a replacement suffix.");
        }
        foreach (var replacement in decision.Items)
        {
          ValidateDecisionItem(replacement, maximumEvidencePaths);
        }
      }
      return;
    }
    if (decision.Decision == "reject_work")
    {
      if (
        decision.EvidenceRevision != item.EvidenceRevision
        || string.IsNullOrWhiteSpace(decision.Discrepancy)
        || string.IsNullOrWhiteSpace(decision.CorrectiveBrief)
      )
      {
        throw InvalidDecision(
          "A supervisor rejection requires the current evidence revision, discrepancy, and corrective brief."
        );
      }
      return;
    }
    if (decision.Decision is "request_validation" or "await_user" or "stop_blocked")
    {
      return;
    }
    throw InvalidDecision($"Unsupported supervisor verification decision '{decision.Decision}'.");
  }

  private static void ValidateDecisionItem(
    SupervisionDecisionItem item,
    int maximumEvidencePaths
  )
  {
    if (
      string.IsNullOrWhiteSpace(item.Objective)
      || item.Objective.Length > 4_096
      || item.AcceptanceCriteria is null
      || item.AcceptanceCriteria.Count is < 1 or > 12
      || item.AcceptanceCriteria.Any(
        criterion => string.IsNullOrWhiteSpace(criterion) || criterion.Length > 1_024
      )
      || item.EvidencePaths is null
      || item.EvidencePaths.Count > maximumEvidencePaths
      || item.EvidencePaths.Any(
        path => string.IsNullOrWhiteSpace(path)
          || Path.IsPathFullyQualified(path)
          || path.Length > 512
      )
    )
    {
      throw InvalidDecision("A supervisor work item is invalid or exceeds its bounds.");
    }
  }

  private static SupervisionRuntimeView ReplacePendingSuffix(
    SupervisionRuntimeView runtime,
    int completedIndex,
    IReadOnlyList<SupervisionDecisionItem> replacements,
    int maximumItems
  )
  {
    var prefix = runtime.WorkItems.Take(completedIndex + 1).ToList();
    if (prefix.Count + replacements.Count > maximumItems)
    {
      throw InvalidDecision("The replacement queue exceeds the configured work-item bound.");
    }
    prefix.AddRange(replacements.Select(
      (item, index) => CreateWorkItem(item, completedIndex + index + 2)
    ));
    return runtime with
    {
      WorkItems = prefix,
      TotalItems = prefix.Count
    };
  }

  private static SupervisionWorkItemView CreateWorkItem(
    SupervisionDecisionItem item,
    int index
  )
  {
    return new SupervisionWorkItemView(
      $"work-{index:000}",
      item.Objective.Trim(),
      item.AcceptanceCriteria!.Select(value => value.Trim()).ToArray(),
      item.EvidencePaths!.Select(value => value.Trim().Replace('\\', '/')).ToArray(),
      SupervisionWorkItemStates.Pending,
      0,
      null,
      0,
      null,
      null
    );
  }

  private static SupervisionContextView CreateContext(
    string id,
    string role,
    string? workItemId,
    long runRevision
  )
  {
    var now = DateTimeOffset.UtcNow;
    return new SupervisionContextView(
      id,
      role,
      workItemId,
      SupervisionContextStates.Active,
      1,
      runRevision,
      null,
      now,
      now
    );
  }

  private static SupervisionContextView ActivateContext(
    SupervisionContextView context,
    long runRevision
  )
  {
    return context with
    {
      State = SupervisionContextStates.Active,
      Revision = context.Revision + 1,
      LastSynchronizedRunRevision = runRevision,
      UpdatedAt = DateTimeOffset.UtcNow
    };
  }

  private static SupervisionContextView SuspendContext(
    SupervisionContextView context,
    string outcome,
    long runRevision
  )
  {
    return context with
    {
      State = SupervisionContextStates.Suspended,
      Revision = context.Revision + 1,
      LastSynchronizedRunRevision = runRevision,
      LastOutcome = Truncate(outcome, 512),
      UpdatedAt = DateTimeOffset.UtcNow
    };
  }

  private static SupervisionContextView CompleteContext(
    SupervisionContextView context,
    string outcome,
    long runRevision
  )
  {
    return SuspendContext(context, outcome, runRevision) with
    {
      State = SupervisionContextStates.Completed
    };
  }

  private static SupervisionRuntimeView Replace(
    SupervisionRuntimeView runtime,
    int itemIndex,
    SupervisionWorkItemView item,
    SupervisionContextView context
  )
  {
    var items = runtime.WorkItems.ToArray();
    items[itemIndex] = item;
    return ReplaceContext(runtime with { WorkItems = items }, context);
  }

  private static SupervisionRuntimeView ReplaceContext(
    SupervisionRuntimeView runtime,
    SupervisionContextView context
  )
  {
    var contexts = runtime.Contexts.ToList();
    var index = contexts.FindIndex(
      item => string.Equals(item.Id, context.Id, StringComparison.Ordinal)
    );
    if (index < 0)
    {
      contexts.Add(context);
    }
    else
    {
      contexts[index] = context;
    }
    return runtime with { Contexts = contexts.ToArray() };
  }

  private static SupervisionExecutionUpdate Update(
    string state,
    string phase,
    string eventType,
    string message,
    SupervisionRuntimeView runtime,
    bool terminal = false,
    string? waitReason = null,
    string? role = null,
    string? contextId = null,
    string? workItemId = null,
    string? waitCode = null
  )
  {
    return new SupervisionExecutionUpdate(
      state,
      phase,
      eventType,
      Truncate(message, 1_024),
      runtime,
      terminal,
      waitReason,
      role,
      contextId,
      workItemId,
      waitCode
    );
  }

  private static SupervisionExecutionUpdate Blocked(
    SupervisionRuntimeView runtime,
    string reason,
    string? contextId = null,
    string? workItemId = null,
    string? waitCode = null
  )
  {
    runtime = runtime with
    {
      ActiveRole = null,
      LastFailure = Truncate(reason, 1_024)
    };
    return Update(
      DurableSupervisionRunStates.Blocked,
      SupervisionRunPhases.Verifying,
      SupervisionEventTypeIds.Blocked,
      runtime.LastFailure,
      runtime,
      terminal: true,
      role: "supervisor",
      contextId: contextId,
      workItemId: workItemId,
      waitCode: waitCode
    );
  }

  private static SupervisionException InvalidDecision(string message)
  {
    return new SupervisionException(
      "supervision-decision-invalid",
      "supervision-decision",
      message,
      false,
      409
    );
  }

  private static string Truncate(string? value, int maximum)
  {
    if (string.IsNullOrEmpty(value) || value.Length <= maximum)
    {
      return value ?? string.Empty;
    }
    return value[..maximum] + " [truncated]";
  }

  private sealed record TurnOutcome(
    string Answer,
    ProviderError? Failure,
    ExecutionSessionReview? Review
  );

  private sealed record SupervisorEvidence(
    long Revision,
    string Json,
    string Sha256
  );

  private sealed record SupervisorEvidenceFile(
    string Path,
    string State,
    string? Sha256,
    long? SizeBytes,
    string? Content
  );

  private sealed record SupervisionDecision(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("items")] IReadOnlyList<SupervisionDecisionItem>? Items = null,
    [property: JsonPropertyName("evidenceRevision")] long? EvidenceRevision = null,
    [property: JsonPropertyName("coveredCriteria")] IReadOnlyList<string>? CoveredCriteria = null,
    [property: JsonPropertyName("summary")] string? Summary = null,
    [property: JsonPropertyName("discrepancy")] string? Discrepancy = null,
    [property: JsonPropertyName("correctiveBrief")] string? CorrectiveBrief = null,
    [property: JsonPropertyName("finalAnswer")] string? FinalAnswer = null
  );

  private sealed record SupervisionDecisionItem(
    [property: JsonPropertyName("objective")] string Objective,
    [property: JsonPropertyName("acceptanceCriteria")] IReadOnlyList<string>? AcceptanceCriteria,
    [property: JsonPropertyName("evidencePaths")] IReadOnlyList<string>? EvidencePaths
  );
}
