using System.Globalization;
using System.Text;

namespace AgenticRouter.Api.Execution;

public sealed record ExecutionTurnToolScope(
  IReadOnlyList<string> AvailableTools,
  bool ProcessExecutionAllowed,
  bool ManualValidationRequested,
  bool ValidationProfileAvailable,
  bool GitToolsAvailable,
  bool DirectoryCreationAvailable,
  bool DeletionAvailable
)
{
  public bool Allows(string canonicalTool)
  {
    return AvailableTools.Contains(
      canonicalTool,
      StringComparer.OrdinalIgnoreCase
    );
  }
}

public static class ExecutionTurnToolPolicy
{
  private static readonly string[] CoreTools =
  [
    "create_execution_plan",
    "revise_execution_plan",
    "get_execution_plan",
    "list_files",
    "read_file",
    "get_file_info",
    "search_text",
    "create_file",
    "create_files",
    "write_file",
    "replace_text",
    "apply_patch",
    "rename_path"
  ];

  private static readonly string[] GitTools =
  [
    "git_status",
    "git_diff",
    "git_log",
    "git_show_commit",
    "git_stage_files",
    "git_unstage_files",
    "git_create_commit",
    "git_create_annotated_tag",
    "git_push_current_branch",
    "git_push_tag"
  ];

  public static ExecutionTurnToolScope Resolve(
    string objective,
    bool validationProfileAvailable,
    bool webSearchAvailable = false,
    bool diagnosticTraceAvailable = false
  )
  {
    var normalized = Normalize(objective);
    var manualValidation = ContainsAny(
      normalized,
      "manual test",
      "test manually",
      "test it manually",
      "will test manually",
      "will test it manually",
      "i will test",
      "eu vou testar",
      "vou testar manualmente",
      "teste manual",
      "testar manualmente"
    );
    var processDenied = manualValidation || ContainsAny(
      normalized,
      "do not execute",
      "does not execute",
      "does not executes",
      "don't execute",
      "do not run",
      "don't run",
      "no execution",
      "without executing",
      "without running",
      "nao execute",
      "nao executar",
      "nao rode",
      "nao rodar",
      "sem executar",
      "sem rodar",
      "no node",
      "do not use node",
      "does not use node",
      "nao use node",
      "sem node"
    );
    var processAllowed = !processDenied;
    const bool gitToolsAvailable = true;
    const bool directoryCreationAvailable = true;
    const bool deletionAvailable = true;

    var tools = new List<string>(CoreTools);
    if (deletionAvailable) tools.Add("delete_paths");
    if (directoryCreationAvailable) tools.Add("create_directory");
    if (processAllowed) tools.Add("run_process");
    if (validationProfileAvailable && !manualValidation)
    {
      tools.Add("run_validation_profile");
    }
    if (gitToolsAvailable) tools.AddRange(GitTools);
    if (webSearchAvailable) tools.Add(WebSearchCapability.ToolName);
    if (diagnosticTraceAvailable) tools.Add(DiagnosticTraceCapability.ToolName);

    return new ExecutionTurnToolScope(
      tools,
      processAllowed,
      manualValidation,
      validationProfileAvailable,
      gitToolsAvailable,
      directoryCreationAvailable,
      deletionAvailable
    );
  }

  public static ExecutionTurnToolScope Resolve(
    IEnumerable<(string Role, string? Content)> messages
  )
  {
    var materialized = messages.ToArray();
    var objective = materialized.LastOrDefault(
      message => string.Equals(message.Role, "user", StringComparison.Ordinal)
        && !IsControlMessage(message.Content)
    ).Content ?? string.Empty;
    var projectContext = materialized.LastOrDefault(
      message => string.Equals(message.Role, "system", StringComparison.Ordinal)
        && message.Content?.StartsWith(
          "APPLICATION_OWNED_PROJECT_CONTEXT",
          StringComparison.Ordinal
        ) == true
    ).Content;
    var validationProfileAvailable = projectContext?.Contains(
      "Validation profile: configured",
      StringComparison.Ordinal
    ) == true;
    return Resolve(objective, validationProfileAvailable);
  }

  public static string Describe(ExecutionTurnToolScope scope)
  {
    var process = scope.ProcessExecutionAllowed
      ? "Structured process execution is available but optional. Availability does not mean the user requested it; propose run_process only when execution materially fulfills or validates the request."
      : "Process execution is not allowed for this turn; run_process is unavailable.";
    var validation = scope.ManualValidationRequested
      ? "The user reserved validation for manual testing; do not run a server, Node, build, test, or validation process."
      : scope.ValidationProfileAvailable
        ? "A Host validation profile is available through run_validation_profile."
        : "No Host validation profile is configured; do not invent validation commands or a development server.";
    return $"{process}\n{validation}\n"
      + "Only tools offered by the Host in the current request are valid. "
      + "The approval selector is authoritative: ask requires approval before every mutation; auto executes a requested in-scope mutation after Host validation without a duplicate prompt. "
      + "create_file and create_files create required parent directories, so do not create a directory solely as a file parent. "
      + "Use create_files for two or more independent new text files so the Host can validate and apply the batch as one action.";
  }

  private static bool IsControlMessage(string? content)
  {
    if (string.IsNullOrWhiteSpace(content)) return true;
    return content.StartsWith("LOCAL_ACTION_", StringComparison.Ordinal)
      || content.StartsWith("STRUCTURED_ACTION_", StringComparison.Ordinal)
      || content.StartsWith("TOOL_PROTOCOL_", StringComparison.Ordinal)
      || content.StartsWith("EXECUTION_", StringComparison.Ordinal)
      || content.StartsWith("COMPLETION_", StringComparison.Ordinal)
      || content.StartsWith("RECOVERY_", StringComparison.Ordinal)
      || content.StartsWith("RESIDENT_STRATEGY_", StringComparison.Ordinal)
      || content.StartsWith(
        ExpertExecutionGuidanceService.GuidanceMarker,
        StringComparison.Ordinal
      );
  }

  private static bool ContainsAny(string value, params string[] fragments)
  {
    return fragments.Any(fragment => value.Contains(fragment, StringComparison.Ordinal));
  }

  private static string Normalize(string value)
  {
    var decomposed = value.Normalize(NormalizationForm.FormD);
    var builder = new StringBuilder(decomposed.Length);
    foreach (var character in decomposed)
    {
      if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
      {
        builder.Append(char.ToLowerInvariant(character));
      }
    }
    return builder.ToString().Normalize(NormalizationForm.FormC);
  }
}
