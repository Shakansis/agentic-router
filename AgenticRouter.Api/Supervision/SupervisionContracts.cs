using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;

namespace AgenticRouter.Api.Supervision;

public static class SupervisionExecutionStrategies
{
  public const string Auto = "auto";
  public const string Autonomous = "autonomous";
  public const string Direct = "direct";
  public const string Supervised = "supervised";
}

public static class SupervisionResumePolicies
{
  public const string Manual = "manual";
  public const string AutoSafe = "auto-safe";
}

public static class DurableSupervisionRunStates
{
  public const string Prepared = "prepared";
  public const string Running = "running";
  public const string InterruptedRecoverable = "interrupted-recoverable";
  public const string AwaitingUser = "awaiting-user";
  public const string Cancelling = "cancelling";
  public const string Cancelled = "cancelled";
  public const string Completed = "completed";
  public const string Blocked = "blocked";

  public static bool IsTerminal(string state)
  {
    return state is Cancelled or Completed or Blocked;
  }
}

public static class SupervisionRunPhases
{
  public const string Foundation = "foundation";
  public const string Recovery = "recovery";
  public const string Decomposing = "decomposing";
  public const string Working = "working";
  public const string Verifying = "verifying";
  public const string Completing = "completing";
}

public static class SupervisionEventTypeIds
{
  public const string Prepared = "supervision.prepared";
  public const string CheckpointSaved = "supervision.checkpoint-saved";
  public const string InterruptedRecoverable = "supervision.interrupted-recoverable";
  public const string AutoResumeEligible = "supervision.auto-resume-eligible";
  public const string Resumed = "supervision.resumed";
  public const string Started = "supervision.started";
  public const string SupervisorStarted = "supervision.supervisor-started";
  public const string WorkQueued = "supervision.work-queued";
  public const string WorkerStarted = "supervision.worker-started";
  public const string TurnReasoning = "supervision.turn-reasoning";
  public const string TurnCommentary = "supervision.turn-commentary";
  public const string TurnStatus = "supervision.turn-status";
  public const string WorkerClaimed = "supervision.worker-claimed";
  public const string VerificationStarted = "supervision.verification-started";
  public const string WorkRejected = "supervision.work-rejected";
  public const string WorkAccepted = "supervision.work-accepted";
  public const string RetryStarted = "supervision.retry-started";
  public const string TurnSlowWarning = "supervision.turn-slow-warning";
  public const string TurnSlowCritical = "supervision.turn-slow-critical";
  public const string TurnWatchdogRecovery = "supervision.turn-watchdog-recovery";
  public const string TurnCanonicalRecovery = "supervision.turn-canonical-recovery";
  public const string TurnHarnessRecovery = "supervision.turn-harness-recovery";
  public const string PreflightCompleted = "supervision.preflight-completed";
  public const string NoProgress = "supervision.no-progress";
  public const string RecoveryEligible = "supervision.recovery-eligible";
  public const string ReconciliationRequired = "supervision.reconciliation-required";
  public const string ActionPrepared = "supervision.action-prepared";
  public const string ActionAwaitingApproval = "supervision.action-awaiting-approval";
  public const string ActionInFlight = "supervision.action-in-flight";
  public const string ActionCommitted = "supervision.action-committed";
  public const string ActionFailed = "supervision.action-failed";
  public const string ActionRejected = "supervision.action-rejected";
  public const string AwaitingUser = "supervision.awaiting-user";
  public const string Completed = "supervision.completed";
  public const string DeterministicCompletion = "supervision.deterministic-completion";
  public const string Blocked = "supervision.blocked";
  public const string Cancelling = "supervision.cancelling";
  public const string Cancelled = "supervision.cancelled";
}

public static class SupervisionRetryReasons
{
  public const string WorkerFailure = "worker-failure";
  public const string PlanningNoProgress = "planning-no-progress";
  public const string AcceptanceMismatch = "acceptance-mismatch";
  public const string StaleEvidenceReverification = "stale-evidence-reverification";
  public const string ValidationFailure = "validation-failure";
  public const string HarnessRecovery = "harness-recovery";
  public const string WatchdogRecovery = "watchdog-recovery";
  public const string CanonicalRecovery = "canonical-recovery";
  public const string CrashRecovery = "crash-recovery";
}

public sealed record PrepareSupervisionRunRequest(
  string Objective,
  string Model,
  string Harness,
  string BrowserSessionId,
  string? ConversationSessionId = null,
  string ApprovalPolicy = "auto",
  string ResumePolicy = SupervisionResumePolicies.Manual,
  string? ClientRunId = null,
  bool AutoModelHarness = false,
  IReadOnlyList<ChatMessage>? History = null,
  IReadOnlyList<ChatImageAttachment>? Images = null,
  SupervisionTakeoverSnapshot? Takeover = null,
  string ExecutionStrategy = SupervisionExecutionStrategies.Supervised
);

public sealed record ResumeSupervisionRunRequest(
  string BrowserSessionId,
  IReadOnlyList<ChatMessage>? History = null,
  IReadOnlyList<ChatImageAttachment>? Images = null
);

public sealed record SupervisionRunStartView(
  string RunId,
  string State,
  bool Durable,
  string EventsUrl
);

public sealed record SupervisionRunEvent(
  string RunId,
  long Sequence,
  string Type,
  DateTimeOffset Timestamp,
  string State,
  string? Message = null,
  bool Terminal = false,
  string? Role = null,
  string? ContextId = null,
  string? WorkItemId = null,
  int? CompletedItems = null,
  int? TotalItems = null,
  SlowRequestStatusView? SlowRequest = null,
  [property: JsonIgnore]
  ContextUsageView? ContextUsage = null,
  [property: JsonIgnore]
  LocalActionEvent? LocalAction = null,
  string? RejectionReason = null,
  string? RetryReason = null,
  long? DurationMilliseconds = null
);

public static class SupervisionWorkItemStates
{
  public const string Pending = "pending";
  public const string Active = "active";
  public const string Verifying = "verifying";
  public const string Completed = "completed";
  public const string Blocked = "blocked";
}

public static class SupervisionRequirementModalities
{
  public const string Must = "must";
  public const string Should = "should";
  public const string May = "may";
}

public static class SupervisionContextStates
{
  public const string Active = "active";
  public const string Suspended = "suspended";
  public const string Completed = "completed";
  public const string Abandoned = "abandoned";
}

public sealed record SupervisionWorkItemView(
  string Id,
  string Objective,
  IReadOnlyList<string> AcceptanceCriteria,
  IReadOnlyList<string> EvidencePaths,
  string Status,
  int AttemptCount,
  string? WorkerContextId,
  long EvidenceRevision,
  string? LastDiscrepancy,
  string? EvidenceSha256,
  string? RejectionReason = null,
  string? RetryReason = null
);

public sealed record SupervisionTelemetryView(
  long DecompositionDurationMilliseconds,
  long WorkerDurationMilliseconds,
  long VerificationDurationMilliseconds,
  long CorrectionDurationMilliseconds,
  long FinalCompletionDurationMilliseconds,
  int WorkerAttemptCount,
  int SupervisorTransitionCount,
  string? RejectionReason,
  string? RetryReason,
  int ActualWorkspaceMutationCount
)
{
  public static SupervisionTelemetryView Empty { get; } = new(
    0,
    0,
    0,
    0,
    0,
    0,
    0,
    null,
    null,
    0
  );
}

public sealed record SupervisionContextView(
  string Id,
  string Role,
  string? WorkItemId,
  string State,
  long Revision,
  long LastSynchronizedRunRevision,
  string? LastOutcome,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt
);

public sealed record SupervisionRuntimeView(
  IReadOnlyList<SupervisionWorkItemView> WorkItems,
  IReadOnlyList<SupervisionContextView> Contexts,
  string? ActiveRole,
  string? ActiveWorkItemId,
  int CompletedItems,
  int TotalItems,
  int SupervisorTransitionCount,
  int NoProgressCount,
  long EvidenceRevision,
  string? FinalAnswer,
  string? LastFailure,
  bool RecoverableInCurrentProcess,
  SupervisionTelemetryView? Telemetry = null
)
{
  public static SupervisionRuntimeView Empty(bool recoverableInCurrentProcess = true)
  {
    return new SupervisionRuntimeView(
      [],
      [],
      null,
      null,
      0,
      0,
      0,
      0,
      0,
      null,
      null,
      recoverableInCurrentProcess,
      SupervisionTelemetryView.Empty
    );
  }
}

public sealed record SupervisionRouteSnapshot(
  string Provider,
  string Model,
  string ModelDigest,
  string Harness,
  string HarnessVersion,
  string OllamaEndpoint,
  string WorkspacePathSha256,
  string WorkerGpuSelection = OllamaGpuSelection.Auto,
  string? WorkerGpuDeviceId = null,
  string? WorkerGpuDeviceName = null,
  string? WorkerRuntime = null,
  string? SupervisorProvider = null,
  string? SupervisorModel = null,
  string? SupervisorModelDigest = null,
  string? SupervisorOllamaEndpoint = null,
  string? SupervisorGpuSelection = null,
  string? SupervisorGpuDeviceId = null,
  string? SupervisorGpuDeviceName = null,
  string? SupervisorRuntime = null,
  bool GpuPlacementConflict = false,
  string? WorkerInheritedGpuSelection = null,
  string? SupervisorInheritedGpuSelection = null
)
{
  [JsonIgnore]
  public string EffectiveSupervisorProvider => SupervisorProvider ?? Provider;

  [JsonIgnore]
  public string EffectiveSupervisorModel => SupervisorModel ?? Model;

  [JsonIgnore]
  public string EffectiveSupervisorModelDigest => SupervisorModelDigest ?? ModelDigest;

  [JsonIgnore]
  public string EffectiveSupervisorOllamaEndpoint => SupervisorOllamaEndpoint ?? OllamaEndpoint;
}

public static class SupervisionActionPhases
{
  public const string Prepared = "prepared";
  public const string AwaitingApproval = "awaiting-approval";
  public const string InFlight = "in-flight";
  public const string Committed = "committed";
  public const string Failed = "failed";
  public const string Rejected = "rejected";
  public const string Abandoned = "abandoned";
  public const string Ambiguous = "ambiguous";
}

public sealed record SupervisionActionFileEffect(
  string RelativePath,
  string Operation,
  bool ExistedBefore,
  string OriginalSha256,
  string ExpectedFinalSha256
);

public sealed record SupervisionActionCheckpoint(
  string ActionId,
  string ContextId,
  string? WorkItemId,
  string Tool,
  string Phase,
  bool ReadOnly,
  bool RequiresApproval,
  string ArgumentsSha256,
  IReadOnlyList<SupervisionActionFileEffect> FileEffects,
  DateTimeOffset PreparedAt,
  DateTimeOffset UpdatedAt,
  string? ResultSha256 = null,
  string? Reconciliation = null
);

public sealed record SupervisionTrackedFileSnapshot(
  string RelativePath,
  string State,
  string? Sha256,
  long? Bytes
);

public sealed record SupervisionBudgetSnapshot(
  int MaximumWorkItems,
  int MaximumSupervisorTransitions,
  int MaximumWorkerAttempts
);

public sealed record SupervisionRecoverySnapshot(
  string InstructionSha256,
  IReadOnlyList<string> InstructionFiles,
  IReadOnlyList<SupervisionTrackedFileSnapshot> TrackedFiles,
  IReadOnlyList<SupervisionActionCheckpoint> Actions,
  SupervisionBudgetSnapshot Budgets,
  bool TurnInFlight,
  DateTimeOffset CapturedAt,
  bool ImagesPending = false
);

public sealed record SupervisionTakeoverFileSnapshot(
  string RelativePath,
  string Operation,
  string FinalHash,
  bool Verified
);

public sealed record SupervisionTakeoverSnapshot(
  string Trigger,
  string DirectExecutionSessionId,
  int DetectedPlanSteps,
  int MaximumDirectPlanSteps,
  bool AfterVerifiedMutation,
  ExecutionPlanView? Plan,
  IReadOnlyList<SupervisionTakeoverFileSnapshot> Files,
  string DirectCompletionStatus,
  string? ValidationStatus,
  DateTimeOffset CapturedAt
);

public sealed record DurableSupervisionCheckpoint(
  int SchemaVersion,
  string RunId,
  string WorkspaceId,
  string ConversationSessionId,
  string BrowserSessionId,
  string Objective,
  string ObjectiveSha256,
  SupervisionRouteSnapshot Route,
  string ApprovalPolicy,
  string ResumePolicy,
  string State,
  string Phase,
  long Revision,
  bool Durable,
  bool AutoResumeEligible,
  string? WaitReason,
  IReadOnlyList<SupervisionRunEvent> Events,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt,
  string IntegritySha256,
  SupervisionRuntimeView? Runtime = null,
  SupervisionRecoverySnapshot? Recovery = null,
  string? WaitCode = null,
  SupervisionTakeoverSnapshot? Takeover = null,
  string ExecutionStrategy = SupervisionExecutionStrategies.Supervised
)
{
  public const int CurrentSchemaVersion = 5;
}

public sealed record DurableSupervisionRunView(
  string RunId,
  string WorkspaceId,
  string ConversationSessionId,
  string Objective,
  string State,
  string Phase,
  long Revision,
  bool Durable,
  bool AutoResumeEligible,
  string ResumePolicy,
  string ApprovalPolicy,
  SupervisionRouteSnapshot Route,
  string? WaitReason,
  long LastSequence,
  bool Terminal,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt,
  SupervisionRuntimeView? Runtime = null,
  SupervisionRecoverySnapshot? Recovery = null,
  string? WaitCode = null,
  SupervisionTakeoverSnapshot? Takeover = null,
  string ExecutionStrategy = SupervisionExecutionStrategies.Supervised
);

public sealed record SupervisionRunListView(
  IReadOnlyList<DurableSupervisionRunView> Runs
);

public sealed record SupervisionCheckpointLoadIssue(
  string RelativePath,
  string Code,
  string Message
);

public sealed record SupervisionCheckpointLoadResult(
  IReadOnlyList<DurableSupervisionCheckpoint> Checkpoints,
  IReadOnlyList<SupervisionCheckpointLoadIssue> Issues
);

public sealed record SupervisionRouteResolution(
  string WorkspaceId,
  string ConversationSessionId,
  bool HistoryEnabled,
  SupervisionRouteSnapshot Route
);

public sealed record SupervisionResumeEligibility(
  bool Eligible,
  string? Reason
);

public sealed record SupervisionRequestResolution(
  string Strategy,
  string ResumePolicy,
  string Objective,
  string RequestedStrategy,
  string ActivationReason,
  int? EstimatedStepCount = null
)
{
  public bool Supervised => string.Equals(
    Strategy,
    SupervisionExecutionStrategies.Supervised,
    StringComparison.Ordinal
  );

  public bool Automatic => string.Equals(
    RequestedStrategy,
    SupervisionExecutionStrategies.Auto,
    StringComparison.Ordinal
  );

  public bool Autonomous => string.Equals(
    RequestedStrategy,
    SupervisionExecutionStrategies.Autonomous,
    StringComparison.Ordinal
  );
}

public sealed class SupervisionException : Exception
{
  public SupervisionException(
    string code,
    string stage,
    string message,
    bool retryable,
    int statusCode = 400,
    Exception? innerException = null
  ) : base(message, innerException)
  {
    Code = code;
    Stage = stage;
    Retryable = retryable;
    StatusCode = statusCode;
  }

  public string Code { get; }

  public string Stage { get; }

  public bool Retryable { get; }

  public int StatusCode { get; }
}

public static class SupervisionRequestPolicy
{
  private const string DirectDirective = "/direct";
  private const string SupervisorDirective = "/supervisor";

  public static SupervisionRequestResolution Resolve(
    ChatRequest request,
    int maximumDirectPlanSteps = 5
  )
  {
    var requestedStrategy = NormalizeStrategy(
      request.ExecutionStrategy
    );
    var objective = request.Message.Trim();
    var activationReason = "explicit-direct";

    if (HasDirective(objective, SupervisorDirective))
    {
      requestedStrategy = SupervisionExecutionStrategies.Supervised;
      objective = objective[SupervisorDirective.Length..].TrimStart();
      activationReason = "supervisor-directive";
    }
    else if (HasDirective(objective, DirectDirective))
    {
      requestedStrategy = SupervisionExecutionStrategies.Direct;
      objective = objective[DirectDirective.Length..].TrimStart();
      activationReason = "direct-directive";
    }

    var estimatedStepCount = EstimateStructuredStepCount(objective);
    var strategy = requestedStrategy;
    if (string.Equals(
      requestedStrategy,
      SupervisionExecutionStrategies.Autonomous,
      StringComparison.Ordinal
    ))
    {
      strategy = SupervisionExecutionStrategies.Supervised;
      activationReason = "explicit-autonomous";
    }
    else if (string.Equals(requestedStrategy, SupervisionExecutionStrategies.Auto, StringComparison.Ordinal))
    {
      strategy = string.Equals(request.InteractionMode, "execute", StringComparison.Ordinal)
        && estimatedStepCount > maximumDirectPlanSteps
          ? SupervisionExecutionStrategies.Supervised
          : SupervisionExecutionStrategies.Direct;
      activationReason = strategy == SupervisionExecutionStrategies.Supervised
        ? "automatic-objective-step-limit"
        : string.Equals(request.InteractionMode, "execute", StringComparison.Ordinal)
          ? "automatic-direct-within-step-limit"
          : "chat-direct";
    }

    if (
      string.Equals(
        strategy,
        SupervisionExecutionStrategies.Supervised,
        StringComparison.Ordinal
      )
      && !string.Equals(
        request.InteractionMode,
        "execute",
        StringComparison.Ordinal
      )
    )
    {
      throw new SupervisionException(
        "supervision-execute-required",
        "supervision-request",
        "Supervised execution is available in Execute mode only.",
        true
      );
    }

    if (
      string.Equals(request.ApprovalPolicy, "autonomous", StringComparison.Ordinal)
      && !string.Equals(
        requestedStrategy,
        SupervisionExecutionStrategies.Autonomous,
        StringComparison.Ordinal
      )
    )
    {
      throw new SupervisionException(
        "autonomous-approval-requires-supervision",
        "supervision-request",
        "Autonomous approval authority is available only through the Autonomous supervision strategy.",
        false
      );
    }

    if (
      string.Equals(
        strategy,
        SupervisionExecutionStrategies.Supervised,
        StringComparison.Ordinal
      )
      && string.IsNullOrWhiteSpace(
        objective
      )
    )
    {
      throw new SupervisionException(
        "supervision-objective-required",
        "supervision-request",
        "Enter an objective after /supervisor.",
        true
      );
    }

    return new SupervisionRequestResolution(
      strategy,
      string.Equals(
        requestedStrategy,
        SupervisionExecutionStrategies.Autonomous,
        StringComparison.Ordinal
      )
        ? SupervisionResumePolicies.Manual
        : NormalizeResumePolicy(request.SupervisionResumePolicy),
      objective,
      requestedStrategy,
      activationReason,
      estimatedStepCount == 0
        ? null
        : estimatedStepCount
    );
  }

  public static SupervisionRequestResolution Promote(
    SupervisionRequestResolution current,
    string reason,
    int detectedStepCount
  )
  {
    return current with
    {
      Strategy = SupervisionExecutionStrategies.Supervised,
      RequestedStrategy = SupervisionExecutionStrategies.Auto,
      ActivationReason = reason,
      EstimatedStepCount = detectedStepCount
    };
  }

  public static string NormalizeResumePolicy(string? value)
  {
    var normalized = string.IsNullOrWhiteSpace(
      value
    )
      ? SupervisionResumePolicies.Manual
      : value.Trim().ToLowerInvariant();

    if (normalized is not SupervisionResumePolicies.Manual
      and not SupervisionResumePolicies.AutoSafe)
    {
      throw new SupervisionException(
        "supervision-resume-policy-invalid",
        "supervision-request",
        "Supervision resume policy must be manual or auto-safe.",
        true
      );
    }

    return normalized;
  }

  public static string NormalizeApprovalPolicy(string? value)
  {
    var normalized = string.IsNullOrWhiteSpace(
      value
    )
      ? "auto"
      : value.Trim().ToLowerInvariant();

    if (normalized is not "auto" and not "ask")
    {
      throw new SupervisionException(
        "supervision-approval-policy-invalid",
        "supervision-request",
        "Supervision approval policy must be auto or ask.",
        true
      );
    }

    return normalized;
  }

  public static string Hash(string value)
  {
    return Convert.ToHexString(
      SHA256.HashData(
        Encoding.UTF8.GetBytes(
          value
        )
      )
    ).ToLowerInvariant();
  }

  public static void ValidateId(string value, string field)
  {
    if (
      value.Length is < 1 or > 64
      || value.Any(
        character => !char.IsAsciiLetterOrDigit(
          character
        ) && character is not '-' and not '_'
      )
    )
    {
      throw new SupervisionException(
        "supervision-identity-invalid",
        "supervision-validation",
        $"The {field} identifier is invalid.",
        false
      );
    }
  }

  private static string NormalizeStrategy(string? value)
  {
    var normalized = string.IsNullOrWhiteSpace(
      value
    )
      ? SupervisionExecutionStrategies.Auto
      : value.Trim().ToLowerInvariant();

    if (normalized is not SupervisionExecutionStrategies.Auto
      and not SupervisionExecutionStrategies.Autonomous
      and not SupervisionExecutionStrategies.Direct
      and not SupervisionExecutionStrategies.Supervised)
    {
      throw new SupervisionException(
        "execution-strategy-invalid",
        "supervision-request",
        "Execution strategy must be auto, autonomous, direct, or supervised.",
        true
      );
    }

    return normalized;
  }

  private static bool HasDirective(string message, string directive)
  {
    return message.StartsWith(
      directive,
      StringComparison.OrdinalIgnoreCase
    ) && (
      message.Length == directive.Length
      || char.IsWhiteSpace(
        message[directive.Length]
      )
    );
  }

  private static int EstimateStructuredStepCount(string objective)
  {
    return objective.Split('\n').Count(line => IsStructuredStep(line.Trim()));
  }

  private static bool IsStructuredStep(string line)
  {
    if (line.Length < 2)
    {
      return false;
    }

    if ((line[0] is '-' or '*' or '+') && char.IsWhiteSpace(line[1]))
    {
      return true;
    }

    var index = 0;
    while (index < line.Length && char.IsAsciiDigit(line[index]))
    {
      index++;
    }
    return index > 0
      && index + 1 < line.Length
      && line[index] is '.' or ')'
      && char.IsWhiteSpace(line[index + 1]);
  }
}

public sealed class SupervisionTakeoverRequiredException : Exception
{
  public SupervisionTakeoverRequiredException(
    string executionSessionId,
    ExecutionPlanView plan,
    int maximumDirectPlanSteps,
    bool afterVerifiedMutation
  ) : base(
    $"The accepted Host plan contains {plan.Steps.Count} steps, above the configured direct limit of {maximumDirectPlanSteps}."
  )
  {
    ExecutionSessionId = executionSessionId;
    Plan = plan;
    MaximumDirectPlanSteps = maximumDirectPlanSteps;
    AfterVerifiedMutation = afterVerifiedMutation;
  }

  public string ExecutionSessionId { get; }

  public ExecutionPlanView Plan { get; }

  public int MaximumDirectPlanSteps { get; }

  public bool AfterVerifiedMutation { get; }
}

internal static class SupervisionViewFactory
{
  public static DurableSupervisionRunView Create(
    DurableSupervisionCheckpoint checkpoint,
    SupervisionRuntimeView? runtime = null
  )
  {
    var effectiveRuntime = runtime ?? checkpoint.Runtime;
    if (effectiveRuntime is not null)
    {
      var telemetry = effectiveRuntime.Telemetry ?? SupervisionTelemetryView.Empty;
      var durableEvents = checkpoint.Events;
      var decompositionDuration = SumDurations(
        durableEvents,
        SupervisionEventTypeIds.WorkQueued
      );
      var workerDuration = SumDurations(
        durableEvents,
        SupervisionEventTypeIds.WorkerClaimed,
        retry: false
      );
      var correctionDuration = SumDurations(
        durableEvents,
        SupervisionEventTypeIds.WorkerClaimed,
        retry: true
      );
      var verificationDuration = durableEvents.Where(item =>
        item.Type is SupervisionEventTypeIds.WorkAccepted
          or SupervisionEventTypeIds.WorkRejected
      ).Sum(item => item.DurationMilliseconds ?? 0);
      var completionDuration = SumDurations(
        durableEvents,
        SupervisionEventTypeIds.Completed
      );
      effectiveRuntime = effectiveRuntime with
      {
        Telemetry = telemetry with
        {
          DecompositionDurationMilliseconds = Math.Max(
            telemetry.DecompositionDurationMilliseconds,
            decompositionDuration
          ),
          WorkerDurationMilliseconds = Math.Max(
            telemetry.WorkerDurationMilliseconds,
            workerDuration
          ),
          VerificationDurationMilliseconds = Math.Max(
            telemetry.VerificationDurationMilliseconds,
            verificationDuration
          ),
          CorrectionDurationMilliseconds = Math.Max(
            telemetry.CorrectionDurationMilliseconds,
            correctionDuration
          ),
          FinalCompletionDurationMilliseconds = Math.Max(
            telemetry.FinalCompletionDurationMilliseconds,
            completionDuration
          ),
          WorkerAttemptCount = effectiveRuntime.WorkItems.Sum(item => item.AttemptCount),
          SupervisorTransitionCount = effectiveRuntime.SupervisorTransitionCount,
          RejectionReason = durableEvents.LastOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.RejectionReason)
          )?.RejectionReason ?? telemetry.RejectionReason,
          RetryReason = durableEvents.LastOrDefault(item =>
            !string.IsNullOrWhiteSpace(item.RetryReason)
          )?.RetryReason ?? telemetry.RetryReason,
          ActualWorkspaceMutationCount = checkpoint.Recovery?.Actions.Count(action =>
            !action.ReadOnly
            && action.Phase == SupervisionActionPhases.Committed
          ) ?? telemetry.ActualWorkspaceMutationCount
        }
      };
    }
    return new DurableSupervisionRunView(
      checkpoint.RunId,
      checkpoint.WorkspaceId,
      checkpoint.ConversationSessionId,
      checkpoint.Objective,
      checkpoint.State,
      checkpoint.Phase,
      checkpoint.Revision,
      checkpoint.Durable,
      checkpoint.AutoResumeEligible,
      checkpoint.ResumePolicy,
      checkpoint.ApprovalPolicy,
      checkpoint.Route,
      checkpoint.WaitReason,
      checkpoint.Events.LastOrDefault()?.Sequence ?? 0,
      DurableSupervisionRunStates.IsTerminal(
        checkpoint.State
      ),
      checkpoint.CreatedAt,
      checkpoint.UpdatedAt,
      effectiveRuntime,
      checkpoint.Recovery,
      checkpoint.WaitCode,
      checkpoint.Takeover,
      checkpoint.ExecutionStrategy
    );
  }

  private static long SumDurations(
    IReadOnlyList<SupervisionRunEvent> events,
    string type,
    bool? retry = null
  )
  {
    return events.Where(item =>
      item.Type == type
      && (
        retry is null
        || retry.Value == !string.IsNullOrWhiteSpace(item.RetryReason)
      )
    ).Sum(item => item.DurationMilliseconds ?? 0);
  }
}
