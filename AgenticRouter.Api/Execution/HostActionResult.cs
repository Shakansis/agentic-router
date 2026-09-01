using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public static class HostActionOutcomes
{
  public const string Succeeded = "succeeded";
  public const string NoOp = "no_op";
  public const string Pending = "pending";
  public const string Recoverable = "recoverable";
  public const string Blocked = "blocked";
  public const string Failed = "failed";
  public const string Cancelled = "cancelled";
}

public static class HostActionEffectStates
{
  public const string None = "none";
  public const string Partial = "partial";
  public const string Complete = "complete";
  public const string Unknown = "unknown";
}

public static class HostPlanBindingStates
{
  public const string Explicit = "explicit";
  public const string Auto = "auto";
  public const string Corrected = "corrected";
  public const string Unbound = "unbound";
}

public static class HostActionCodes
{
  public const string ApprovalPending = "APPROVAL_PENDING";
  public const string ApprovalRejected = "APPROVAL_REJECTED";
  public const string ApprovalExpired = "APPROVAL_EXPIRED";
  public const string ProcessTimeout = "PROCESS_TIMEOUT";
  public const string HarnessStall = "HARNESS_STALL";
  public const string TurnTimeout = "TURN_TIMEOUT";
  public const string UserCancelled = "USER_CANCELLED";
}

public sealed record HostActionFact(
  string Name,
  string Value
);

public sealed record HostActionEffect(
  string State,
  bool? Changed = null,
  bool? Verified = null,
  bool? PostconditionSatisfied = null
);

public sealed record HostActionRetry(
  bool Allowed,
  bool Unchanged,
  string? Reason = null
);

public sealed record HostActionNextAction(
  string Tool,
  JsonElement Arguments,
  string Reason
);

public sealed record HostActionCause(
  string Code,
  string Message
);

public sealed record HostPlanStepProjection(
  string Id,
  string Status
);

public sealed record HostPlanDelta(
  string StepId,
  string Status
);

public sealed record HostPlanBindingCorrection(
  string State,
  string? RequestedStepId,
  string? EffectiveStepId
);

public sealed record HostActionPlanProjection(
  long PlanRevision,
  HostPlanStepProjection? CurrentStep = null,
  IReadOnlyList<HostPlanDelta>? Delta = null,
  HostPlanBindingCorrection? BindingCorrection = null,
  ExecutionPlanView? Full = null
);

public sealed record HostActionResult(
  int SchemaVersion,
  string Outcome,
  string Code,
  IReadOnlyList<HostActionFact>? Facts,
  HostActionEffect Effect,
  string WorkspaceRevision,
  HostActionRetry? Retry = null,
  IReadOnlyList<HostActionNextAction>? NextActions = null,
  HostActionPlanProjection? Plan = null,
  HostActionCause? Cause = null,
  string? EvidenceId = null
)
{
  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  public string Serialize()
  {
    return JsonSerializer.Serialize(
      this with
      {
        NextActions = NextActions?.Take(2).ToArray()
      },
      SerializerOptions
    );
  }
}

public sealed record PlanActionBindingResolution(
  string State,
  string? RequestedStepId,
  string? EffectiveStepId
)
{
  public HostPlanBindingCorrection? CreateCorrection()
  {
    return new HostPlanBindingCorrection(
      State,
      RequestedStepId,
      EffectiveStepId
    );
  }
}

public sealed record DeterministicRepeatRecord(
  string Fingerprint,
  string FailureCode,
  string EvidenceId,
  string RequiredChange,
  HostActionResult PreviousResult
);

public static class HostActionResultAdapter
{
  public static HostActionResult FromLegacy(
    string output,
    bool succeeded,
    string code,
    ExecutionSession? session,
    ValidatedLocalAction? action = null,
    bool effectVerified = true,
    bool? retryUnchanged = null,
    string? outcome = null,
    HostActionCause? cause = null,
    IReadOnlyList<HostActionNextAction>? nextActions = null,
    bool includeFullPlan = false,
    string? evidenceId = null,
    string? effectState = null,
    bool? changed = null,
    bool? postconditionSatisfied = null,
    IReadOnlyList<HostActionFact>? additionalFacts = null
  )
  {
    var readOnly = action is null || action.ReadOnly;
    var effectiveOutcome = outcome ?? (
      succeeded
        ? HostActionOutcomes.Succeeded
        : HostActionOutcomes.Recoverable
    );
    var effectiveEffectState = effectState ?? (succeeded
      ? readOnly
        ? HostActionEffectStates.None
        : effectVerified
          ? HostActionEffectStates.Complete
          : HostActionEffectStates.Unknown
      : HostActionEffectStates.None);
    var retry = succeeded || retryUnchanged is null
      ? null
      : new HostActionRetry(
        true,
        retryUnchanged.Value,
        retryUnchanged.Value
          ? "The Host marked this failure as retryable without a state change."
          : "Change the arguments or relevant Host state before retrying."
      );

    return new HostActionResult(
      1,
      effectiveOutcome,
      StableCode(code),
      BuildFacts(output, additionalFacts),
      new HostActionEffect(
        effectiveEffectState,
        changed ?? (succeeded && !readOnly ? true : null),
        succeeded ? effectVerified : null,
        postconditionSatisfied
      ),
      session?.WorkspaceRevision.ToString(
        System.Globalization.CultureInfo.InvariantCulture
      ) ?? "0",
      retry,
      nextActions?.Take(2).ToArray(),
      session?.CreateAgentPlanProjection(action, includeFullPlan),
      cause,
      evidenceId
    );
  }

  private static IReadOnlyList<HostActionFact>? BuildFacts(
    string output,
    IReadOnlyList<HostActionFact>? additionalFacts
  )
  {
    var facts = new List<HostActionFact>();
    if (!string.IsNullOrWhiteSpace(output))
    {
      facts.Add(new HostActionFact("message", output));
    }
    if (additionalFacts is not null)
    {
      facts.AddRange(additionalFacts);
    }
    return facts.Count == 0 ? null : facts;
  }

  public static HostActionResult DeterministicRepeat(
    ExecutionSession session,
    DeterministicRepeatRecord previous,
    ValidatedLocalAction? action = null
  )
  {
    return new HostActionResult(
      1,
      HostActionOutcomes.Recoverable,
      "DETERMINISTIC_REPEAT",
      [
        new HostActionFact(
          "message",
          "The Host suppressed an exact deterministic repeat; the tool was not invoked again."
        ),
        new HostActionFact("previousFailureCode", previous.FailureCode),
        new HostActionFact("requiredChange", previous.RequiredChange)
      ],
      new HostActionEffect(HostActionEffectStates.None, false, true),
      session.WorkspaceRevision.ToString(
        System.Globalization.CultureInfo.InvariantCulture
      ),
      new HostActionRetry(false, false, previous.RequiredChange),
      null,
      session.CreateAgentPlanProjection(action),
      new HostActionCause(previous.FailureCode, $"Previous evidence: {previous.EvidenceId}"),
      previous.EvidenceId
    );
  }

  public static string LegacyCompatibleMessage(
    string tool,
    string status,
    HostActionResult result
  )
  {
    return $"LOCAL_ACTION_RESULT\nTool: {tool}\nStatus: {status}\nOutput:\n{result.Serialize()}";
  }

  public static bool IsSerialized(string value)
  {
    try
    {
      using var document = JsonDocument.Parse(value);
      return document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty("schemaVersion", out var version)
        && version.ValueKind == JsonValueKind.Number
        && version.GetInt32() == 1
        && document.RootElement.TryGetProperty("outcome", out _)
        && document.RootElement.TryGetProperty("code", out _);
    }
    catch (JsonException)
    {
      return false;
    }
  }

  public static string StableCode(string value)
  {
    if (string.IsNullOrWhiteSpace(value)) return "HOST_ACTION_RESULT";
    var characters = value.Trim().Select(
      character => char.IsLetterOrDigit(character)
        ? char.ToUpperInvariant(character)
        : '_'
    ).ToArray();
    return string.Join(
      "_",
      new string(characters).Split('_', StringSplitOptions.RemoveEmptyEntries)
    );
  }
}
