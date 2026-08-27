using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;

namespace AgenticRouter.Api.Supervision;

public static class SupervisionExecutionStrategies
{
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
}

public static class SupervisionEventTypeIds
{
  public const string Prepared = "supervision.prepared";
  public const string CheckpointSaved = "supervision.checkpoint-saved";
  public const string InterruptedRecoverable = "supervision.interrupted-recoverable";
  public const string AutoResumeEligible = "supervision.auto-resume-eligible";
  public const string Resumed = "supervision.resumed";
  public const string Cancelling = "supervision.cancelling";
  public const string Cancelled = "supervision.cancelled";
}

public sealed record PrepareSupervisionRunRequest(
  string Objective,
  string Model,
  string Harness,
  string BrowserSessionId,
  string? ConversationSessionId = null,
  string ApprovalPolicy = "auto",
  string ResumePolicy = SupervisionResumePolicies.Manual,
  string? ClientRunId = null
);

public sealed record ResumeSupervisionRunRequest(
  string BrowserSessionId
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
  bool Terminal = false
);

public sealed record SupervisionRouteSnapshot(
  string Provider,
  string Model,
  string ModelDigest,
  string Harness,
  string HarnessVersion,
  string OllamaEndpoint,
  string WorkspacePathSha256
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
  string IntegritySha256
)
{
  public const int CurrentSchemaVersion = 1;
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
  DateTimeOffset UpdatedAt
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
  string Objective
)
{
  public bool Supervised => string.Equals(
    Strategy,
    SupervisionExecutionStrategies.Supervised,
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
  private const string Directive = "/supervisor";

  public static SupervisionRequestResolution Resolve(ChatRequest request)
  {
    var strategy = NormalizeStrategy(
      request.ExecutionStrategy
    );
    var objective = request.Message.Trim();
    var directive = HasDirective(
      objective
    );

    if (directive)
    {
      strategy = SupervisionExecutionStrategies.Supervised;
      objective = objective[Directive.Length..].TrimStart();
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
      NormalizeResumePolicy(
        request.SupervisionResumePolicy
      ),
      objective
    );
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
      ? SupervisionExecutionStrategies.Direct
      : value.Trim().ToLowerInvariant();

    if (normalized is not SupervisionExecutionStrategies.Direct
      and not SupervisionExecutionStrategies.Supervised)
    {
      throw new SupervisionException(
        "execution-strategy-invalid",
        "supervision-request",
        "Execution strategy must be direct or supervised.",
        true
      );
    }

    return normalized;
  }

  private static bool HasDirective(string message)
  {
    return message.StartsWith(
      Directive,
      StringComparison.OrdinalIgnoreCase
    ) && (
      message.Length == Directive.Length
      || char.IsWhiteSpace(
        message[Directive.Length]
      )
    );
  }
}

internal static class SupervisionViewFactory
{
  public static DurableSupervisionRunView Create(
    DurableSupervisionCheckpoint checkpoint
  )
  {
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
      checkpoint.UpdatedAt
    );
  }
}
