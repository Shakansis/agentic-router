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
  IExecutionActionJournal? ActionJournal = null
);

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
  string? WorkItemId = null
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
  internal const string VerifyMarker = "SUPERVISION_VERIFY_V1";
  internal const string VerifyWithValidationMarker =
    "SUPERVISION_VERIFY_WITH_VALIDATION_V1";
  internal const string CompleteMarker = "SUPERVISION_COMPLETE_V1";

  private const int MaximumDecisionCharacters = 32_768;
  private const int MaximumEvidenceFiles = 12;
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
        "The focused supervisor is decomposing the objective into a bounded ordered queue.",
        runtime,
        role: "supervisor",
        contextId: supervisor.Id
      );

      var decomposition = await RunTurnAsync(
        checkpoint,
        supervisor,
        CreateDecompositionPrompt(checkpoint.Objective, maximumItems),
        input.History,
        [],
        validationAvailable,
        input.ActionJournal,
        cancellationToken
      );
      if (decomposition.Failure is not null)
      {
        yield return Blocked(
          runtime,
          "Supervisor decomposition failed: " + decomposition.Failure.Message,
          supervisor.Id
        );
        yield break;
      }

      SupervisionDecision? decision = null;
      SupervisionException? decompositionError = null;
      try
      {
        decision = ParseDecision(decomposition.Answer);
        ValidateDecomposition(decision, maximumItems);
      }
      catch (SupervisionException exception)
      {
        decompositionError = exception;
      }
      if (decompositionError is not null)
      {
        yield return Blocked(runtime, decompositionError.Message, supervisor.Id);
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
          checkpoint.Objective,
          item
        );
        var workerTurn = await RunTurnAsync(
          checkpoint,
          worker,
          workerPrompt,
          input.History,
          item.AttemptCount == 1 ? input.Images : [],
          validationAvailable,
          input.ActionJournal,
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
            item.Id
          );
          yield break;
        }

        var evidenceRevision = checked(runtime.EvidenceRevision + 1);
        var evidence = await BuildEvidenceAsync(
          workerTurn.Review,
          item.EvidencePaths,
          evidenceRevision,
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
          CreateVerificationPrompt(item, workerTurn.Answer, evidence),
          input.History,
          [],
          validationAvailable,
          input.ActionJournal,
          cancellationToken
        );
        if (verification.Failure is not null)
        {
          yield return Blocked(
            runtime,
            "Supervisor verification failed: " + verification.Failure.Message,
            supervisor.Id,
            item.Id
          );
          yield break;
        }

        SupervisionDecision? verificationDecision = null;
        SupervisionException? verificationError = null;
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
                requireValidation: true
              ),
              input.History,
              [],
              validationAvailable,
              input.ActionJournal,
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
          ValidateVerification(verificationDecision, item);
        }
        catch (SupervisionException exception)
        {
          verificationError = exception;
        }
        if (verificationError is not null)
        {
          yield return Blocked(runtime, verificationError.Message, supervisor.Id, item.Id);
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
      CreateCompletionPrompt(checkpoint.Objective, runtime.WorkItems),
      input.History,
      [],
      validationAvailable,
      input.ActionJournal,
      cancellationToken
    );
    if (completion.Failure is not null)
    {
      yield return Blocked(
        runtime,
        "Final supervisor verification failed: " + completion.Failure.Message,
        finalSupervisor.Id
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
    IExecutionActionJournal? actionJournal,
    CancellationToken cancellationToken
  )
  {
    await EnsureFixedRouteAsync(checkpoint, cancellationToken);
    var supervisor = string.Equals(context.Role, "supervisor", StringComparison.Ordinal);
    var approvalPolicy = supervisor ? "ask" : checkpoint.ApprovalPolicy;
    var browserSessionId = $"sup-{checkpoint.RunId[..12]}-{context.Id}";
    var request = new ChatRequest(
      prompt,
      checkpoint.Route.Model,
      history,
      "execute",
      checkpoint.Route.Harness,
      approvalPolicy,
      browserSessionId,
      checkpoint.ConversationSessionId,
      Images: images,
      ExecutionStrategy: SupervisionExecutionStrategies.Direct
    );
    var scope = supervisor
      ? CreateSupervisorToolScope(validationAvailable)
      : null;
    string? roleResult = null;
    var invocation = new ExecutionSpecialistTurnInvocation(
      context.Id,
      supervisor ? ExecutionContextRole.Supervisor : ExecutionContextRole.Worker,
      scope,
      UseMinimalToolInventory: supervisor,
      CaptureRoleResult: value => roleResult = value,
      ActionJournal: actionJournal
    );
    var answer = new StringBuilder();
    ProviderError? failure = null;
    ExecutionSessionSummary? summary = null;
    await foreach (var streamEvent in _turns.RunAsync(
      request,
      Guid.NewGuid().ToString("N"),
      invocation,
      cancellationToken
    ))
    {
      if (streamEvent.Type == "response.delta")
      {
        answer.Append(streamEvent.Delta);
      }
      if (streamEvent.Error is not null)
      {
        failure = streamEvent.Error;
      }
      summary = streamEvent.ExecutionSession ?? summary;
    }

    var review = summary is null
      ? null
      : _sessions.GetReview(summary.Id);
    return new TurnOutcome(
      Truncate(
        (roleResult ?? answer.ToString()).Trim(),
        MaximumDecisionCharacters
      ),
      failure,
      review
    );
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
    CancellationToken cancellationToken
  )
  {
    var paths = (review?.Files.Select(file => file.RelativePath) ?? [])
      .Concat(requestedPaths)
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .Take(MaximumEvidenceFiles)
      .ToArray();
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
        completion
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
        completion
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

  private static string CreateDecompositionPrompt(string objective, int maximumItems)
  {
    return $$"""
      {{DecomposeMarker}}
      You are the focused supervisor. Decompose the original objective into the smallest ordered queue that can be independently verified. Do not mutate files.
      Original objective:
      {{objective}}

      Return JSON only:
      {"decision":"dispatch_work","items":[{"objective":"...","acceptanceCriteria":["..."],"evidencePaths":["relative/path"]}]}
      Maximum work items: {{maximumItems}}.
      Paths must be relative to the trusted workspace. Keep criteria concrete and observable.
      """;
  }

  private static string CreateWorkerPrompt(
    string originalObjective,
    SupervisionWorkItemView item
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
      Original objective:
      {{originalObjective}}

      Active work item {{item.Id}}:
      {{item.Objective}}

      Acceptance criteria:
      {{string.Join("\n", item.AcceptanceCriteria.Select((criterion, index) => $"{index + 1}. {criterion}"))}}

      {{(string.IsNullOrWhiteSpace(item.LastDiscrepancy) ? "" : "Supervisor correction:\n" + item.LastDiscrepancy)}}
      Work only on this item. Use Host-provided capabilities, preserve unrelated changes, and report a concise completion claim.
      """;
  }

  private static string CreateVerificationPrompt(
    SupervisionWorkItemView item,
    string workerClaim,
    SupervisorEvidence evidence,
    bool requireValidation = false
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
      Return JSON only. Accept:
      {"decision":"accept_work","evidenceRevision":{{evidence.Revision}},"coveredCriteria":["exact criterion text"],"summary":"..."}
      Reject:
      {"decision":"reject_work","evidenceRevision":{{evidence.Revision}},"discrepancy":"exact observed mismatch","correctiveBrief":"bounded materially different correction"}
      Other permitted decisions: request_validation, await_user, stop_blocked, or replace_pending_work. replace_pending_work must also cover the current criteria and include replacement items.
      """;
  }

  private static string CreateCompletionPrompt(
    string objective,
    IReadOnlyList<SupervisionWorkItemView> items
  )
  {
    return $$"""
      {{CompleteMarker}}
      You are the focused supervisor performing the global completion gate.
      Original objective:
      {{objective}}

      Accepted work:
      {{string.Join("\n", items.Select(item => $"- {item.Id}: {item.Objective}; evidence revision {item.EvidenceRevision}; sha256 {item.EvidenceSha256}"))}}

      All work items were accepted against current Host evidence. Return JSON only:
      {"decision":"complete_goal","finalAnswer":"one concise user-facing result grounded in the accepted evidence"}
      If completion is not justified, return {"decision":"stop_blocked","summary":"exact reason"}.
      """;
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
        "The supervisor returned malformed canonical JSON; the identical request was not retried.",
        false,
        409,
        exception
      );
    }
  }

  private static void ValidateDecomposition(
    SupervisionDecision decision,
    int maximumItems
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
      ValidateDecisionItem(item);
    }
  }

  private static void ValidateVerification(
    SupervisionDecision decision,
    SupervisionWorkItemView item
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
          ValidateDecisionItem(replacement);
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

  private static void ValidateDecisionItem(SupervisionDecisionItem item)
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
      || item.EvidencePaths.Count > MaximumEvidenceFiles
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
    string? workItemId = null
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
      workItemId
    );
  }

  private static SupervisionExecutionUpdate Blocked(
    SupervisionRuntimeView runtime,
    string reason,
    string? contextId = null,
    string? workItemId = null
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
      workItemId: workItemId
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
