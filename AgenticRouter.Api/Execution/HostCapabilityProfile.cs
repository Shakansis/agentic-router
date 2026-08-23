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
    if (approvalPolicy is not "auto" and not "ask")
    {
      throw new LocalActionException(
        "approval-policy",
        "Approval policy must be ask or auto."
      );
    }
    return new HostCapabilityProfile(toolScope, approvalPolicy);
  }
}

public static class HarnessCapabilityProjection
{
  private static readonly string[] CodexNativeCommon =
  [
    "list_files",
    "read_file",
    "get_file_info",
    "search_text",
    "create_file",
    "write_file",
    "replace_text",
    "apply_patch",
    "create_directory"
  ];

  private static readonly string[] ExternalNativeCommon =
  [
    "list_files",
    "read_file",
    "search_text",
    "create_file",
    "write_file",
    "replace_text",
    "apply_patch"
  ];

  private static readonly string[] ClaudeNativeCommon =
  [
    "read_file",
    "replace_text",
    "apply_patch"
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
      return [];
    }
    var nativeCommon = NativeCommonTools(harnessId);
    return profile.ToolScope.AvailableTools
      .Except(nativeCommon, StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  public static IReadOnlyList<string> NativeCommonTools(string harnessId)
  {
    return string.Equals(harnessId, HarnessIds.Codex, StringComparison.OrdinalIgnoreCase)
      ? CodexNativeCommon
      : string.Equals(harnessId, HarnessIds.ClaudeCode, StringComparison.OrdinalIgnoreCase)
        ? ClaudeNativeCommon
        : harnessId is HarnessIds.OpenCode or HarnessIds.QwenCode
        ? ExternalNativeCommon
        : [];
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
      .Where(tool => tool is not "create_execution_plan" and not "revise_execution_plan")
      .Except(implemented, StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }
}
