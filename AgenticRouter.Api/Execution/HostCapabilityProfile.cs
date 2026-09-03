namespace AgenticRouter.Api.Execution;

public sealed record HostCapabilityProfile(
  ExecutionTurnToolScope ToolScope,
  string ApprovalPolicy
)
{
  public bool MutationRequiresApproval => string.Equals(
    ApprovalPolicy,
    "ask",
    StringComparison.Ordinal
  );

  public bool Allows(string canonicalTool)
  {
    return ToolScope.Allows(canonicalTool);
  }

  public string Signature => string.Join(
    '|',
    ApprovalPolicy,
    string.Join(',', ToolScope.AvailableTools.Order(StringComparer.Ordinal))
  );

  public static HostCapabilityProfile Create(
    ExecutionTurnToolScope toolScope,
    string approvalPolicy
  )
  {
    if (approvalPolicy is not "auto" and not "ask" and not "autonomous")
    {
      throw new LocalActionException(
        "approval-policy",
        "Approval policy must be ask, auto, or autonomous."
      );
    }
    return new HostCapabilityProfile(toolScope, approvalPolicy);
  }
}

public static class HarnessCapabilityProjection
{
  private static readonly HarnessToolEquivalence[] Equivalences =
  [
    new(HarnessIds.Codex, "apply_patch", "apply_patch", "codex-apply-patch-v1"),
    new(HarnessIds.OpenCode, "create_file", "create_file", "opencode-file-create-v1"),
    new(HarnessIds.OpenCode, "write_file", "write_file", "opencode-file-write-v1"),
    new(HarnessIds.OpenCode, "replace_text", "replace_text", "opencode-text-replace-v1"),
    new(HarnessIds.OpenCode, "apply_patch", "apply_patch", "opencode-apply-patch-v1"),
    new(HarnessIds.ClaudeCode, "Read", "read_file", "claude-read-v1"),
    new(HarnessIds.ClaudeCode, "Edit", "replace_text", "claude-edit-replace-v1"),
    new(HarnessIds.ClaudeCode, "Edit", "apply_patch", "claude-edit-patch-v1")
  ];

  public static IReadOnlyList<string> HostBridgeTools(
    string harnessId,
    HostCapabilityProfile profile
  )
  {
    if (string.Equals(harnessId, HarnessIds.Native, StringComparison.OrdinalIgnoreCase))
    {
      return profile.ToolScope.AvailableTools;
    }
    if (
      harnessId is not HarnessIds.Codex
        and not HarnessIds.OpenCode
        and not HarnessIds.QwenCode
        and not HarnessIds.ClaudeCode
    )
    {
      return profile.ToolScope.AvailableTools;
    }
    var nativeCommon = NativeCommonTools(harnessId);
    var projected = profile.ToolScope.AvailableTools
      .Except(nativeCommon, StringComparer.OrdinalIgnoreCase)
      .ToArray();
    var effective = projected.Concat(nativeCommon)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return effective.SetEquals(profile.ToolScope.AvailableTools)
      ? projected
      : profile.ToolScope.AvailableTools;
  }

  public static IReadOnlyList<string> NativeCommonTools(string harnessId)
  {
    return Equivalences.Where(
      equivalence => string.Equals(equivalence.HarnessId, harnessId, StringComparison.OrdinalIgnoreCase)
    ).Select(equivalence => equivalence.CanonicalTool)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  public static IReadOnlyList<HarnessToolEquivalence> FunctionalEquivalences(string harnessId)
  {
    return Equivalences.Where(
      equivalence => string.Equals(equivalence.HarnessId, harnessId, StringComparison.OrdinalIgnoreCase)
    ).ToArray();
  }

  public static IReadOnlyList<string> EffectiveCanonicalCapabilities(
    string harnessId,
    HostCapabilityProfile profile
  )
  {
    return HostBridgeTools(harnessId, profile).Concat(
      NativeCommonTools(harnessId).Where(profile.Allows)
    ).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }

  public static IReadOnlyList<string> MissingAdapterTools(
    string harnessId,
    HostCapabilityProfile profile
  )
  {
    if (
      harnessId is HarnessIds.Native
        or HarnessIds.Codex
        or HarnessIds.OpenCode
        or HarnessIds.QwenCode
        or HarnessIds.ClaudeCode
    )
    {
      return [];
    }
    var implemented = NativeCommonTools(harnessId);
    return profile.ToolScope.AvailableTools
      .Where(tool => tool is not "create_execution_plan" and not "revise_execution_plan" and not "get_execution_plan")
      .Except(implemented, StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }
}

public sealed record HarnessToolEquivalence(
  string HarnessId,
  string NativeTool,
  string CanonicalTool,
  string EquivalenceVersion
);
