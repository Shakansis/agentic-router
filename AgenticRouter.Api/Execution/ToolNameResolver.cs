namespace AgenticRouter.Api.Execution;

public interface IToolNameResolver
{
  IReadOnlyList<string> CanonicalTools { get; }

  IReadOnlyList<string> ExecutableTools { get; }

  IReadOnlyList<string> StructuredGuidanceTools { get; }

  IReadOnlyList<ToolAliasRegistration> Aliases { get; }

  IReadOnlyList<CanonicalToolRegistration> Registrations { get; }

  ToolNameResolution Resolve(
    string proposedName,
    IEnumerable<string> offeredCanonicalNames
  );

  IReadOnlyList<string> AcceptedNamesFor(
    IEnumerable<string> offeredCanonicalNames
  );
}

public sealed record ToolAliasRegistration(
  string Alias,
  string CanonicalTool
);

public sealed record CanonicalToolRegistration(
  string CanonicalName,
  string Capability,
  IReadOnlyList<string> SupportedHarnesses,
  string RequiredPolicy,
  IReadOnlyList<string> ExactAliases,
  int SchemaVersion
);

public sealed record ToolNameResolution(
  string OriginalName,
  string CanonicalName,
  string Source
)
{
  public bool Normalized => !string.Equals(
    OriginalName,
    CanonicalName,
    StringComparison.Ordinal
  );
}

public sealed class ToolNameResolver : IToolNameResolver
{
  public const string CanonicalSource = "canonical";
  public const string CanonicalCaseSource = "canonical-case-insensitive";
  public const string CuratedAliasSource = "curated-alias";

  private static readonly string[] PlanningTools =
  [
    "create_execution_plan",
    "revise_execution_plan",
    "get_execution_plan"
  ];

  private static readonly string[] MetaTools =
  [
    LocalActionPlanner.RequestToolsetTool
  ];

  private static readonly string[] ActionTools =
  [
    "list_files",
    "read_file",
    "get_file_info",
    "search_text",
    DiagnosticTraceCapability.ToolName,
    WebSearchCapability.ToolName,
    "create_file",
    "create_files",
    "write_file",
    "replace_text",
    "apply_patch",
    "delete_paths",
    "rename_path",
    "create_directory",
    "run_process",
    "run_validation_profile",
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

  private static readonly string[] GuidanceTools =
  [
    "list_files",
    "read_file",
    "get_file_info",
    "search_text",
    DiagnosticTraceCapability.ToolName,
    WebSearchCapability.ToolName,
    "create_file",
    "create_files",
    "write_file",
    "replace_text",
    "apply_patch",
    "delete_paths",
    "rename_path",
    "create_directory",
    "run_process",
    "run_validation_profile"
  ];

  private static readonly ToolAliasRegistration[] InitialAliases =
  [
    Alias("create-execution-plan", "create_execution_plan"),
    Alias("createexecutionplan", "create_execution_plan"),
    Alias("make_execution_plan", "create_execution_plan"),
    Alias("build_execution_plan", "create_execution_plan"),
    Alias("revise-execution-plan", "revise_execution_plan"),
    Alias("reviseexecutionplan", "revise_execution_plan"),
    Alias("update_execution_plan", "revise_execution_plan"),
    Alias("get-execution-plan", "get_execution_plan"),
    Alias("getexecutionplan", "get_execution_plan"),
    Alias("list-files", "list_files"),
    Alias("listfiles", "list_files"),
    Alias("list_directory", "list_files"),
    Alias("list-directory", "list_files"),
    Alias("list_dir", "list_files"),
    Alias("read-file", "read_file"),
    Alias("readfile", "read_file"),
    Alias("read_doc", "read_file"),
    Alias("read-doc", "read_file"),
    Alias("readdoc", "read_file"),
    Alias("read_code", "read_file"),
    Alias("read-code", "read_file"),
    Alias("readcode", "read_file"),
    Alias("get-file-info", "get_file_info"),
    Alias("getfileinfo", "get_file_info"),
    Alias("file_info", "get_file_info"),
    Alias("file-info", "get_file_info"),
    Alias("stat_file", "get_file_info"),
    Alias("file_stat", "get_file_info"),
    Alias("search-text", "search_text"),
    Alias("searchtext", "search_text"),
    Alias("grep_text", "search_text"),
    Alias("grep-text", "search_text"),
    Alias("find_text", "search_text"),
    Alias("find-text", "search_text"),
    Alias("search_in_files", "search_text"),
    Alias("search-in-files", "search_text"),
    Alias("create-file", "create_file"),
    Alias("createfile", "create_file"),
    Alias("create-files", "create_files"),
    Alias("createfiles", "create_files"),
    Alias("write-file", "write_file"),
    Alias("writefile", "write_file"),
    Alias("replace-text", "replace_text"),
    Alias("replacetext", "replace_text"),
    Alias("apply-patch", "apply_patch"),
    Alias("applypatch", "apply_patch"),
    Alias("delete_files", "delete_paths"),
    Alias("delete-files", "delete_paths"),
    Alias("deletefiles", "delete_paths"),
    Alias("delete-paths", "delete_paths"),
    Alias("deletepaths", "delete_paths"),
    Alias("remove_files", "delete_paths"),
    Alias("remove-files", "delete_paths"),
    Alias("remove_paths", "delete_paths"),
    Alias("remove-paths", "delete_paths"),
    Alias("rename-path", "rename_path"),
    Alias("renamepath", "rename_path"),
    Alias("move_file", "rename_path"),
    Alias("move-file", "rename_path"),
    Alias("create-directory", "create_directory"),
    Alias("createdirectory", "create_directory"),
    Alias("run-process", "run_process"),
    Alias("runprocess", "run_process"),
    Alias("run-validation-profile", "run_validation_profile"),
    Alias("runvalidationprofile", "run_validation_profile"),
    Alias("git-status", "git_status"),
    Alias("gitstatus", "git_status"),
    Alias("repo_status", "git_status"),
    Alias("repository_status", "git_status"),
    Alias("git-diff", "git_diff"),
    Alias("gitdiff", "git_diff"),
    Alias("repo_diff", "git_diff"),
    Alias("repository_diff", "git_diff"),
    Alias("git-log", "git_log"),
    Alias("gitlog", "git_log"),
    Alias("commit_log", "git_log"),
    Alias("git_history", "git_log"),
    Alias("git-show-commit", "git_show_commit"),
    Alias("gitshowcommit", "git_show_commit"),
    Alias("show_commit", "git_show_commit"),
    Alias("inspect_commit", "git_show_commit"),
    Alias("git-stage-files", "git_stage_files"),
    Alias("gitstagefiles", "git_stage_files"),
    Alias("git-unstage-files", "git_unstage_files"),
    Alias("gitunstagefiles", "git_unstage_files"),
    Alias("git-create-commit", "git_create_commit"),
    Alias("gitcreatecommit", "git_create_commit"),
    Alias("git-create-annotated-tag", "git_create_annotated_tag"),
    Alias("gitcreateannotatedtag", "git_create_annotated_tag"),
    Alias("git-push-current-branch", "git_push_current_branch"),
    Alias("gitpushcurrentbranch", "git_push_current_branch"),
    Alias("git-push-tag", "git_push_tag"),
    Alias("gitpushtag", "git_push_tag")
  ];

  private readonly Dictionary<string, string> _canonicalByName;
  private readonly Dictionary<string, string> _canonicalByAlias;

  public ToolNameResolver()
  {
    CanonicalTools = MetaTools.Concat(
      PlanningTools
    ).Concat(ActionTools).ToArray();
    ExecutableTools = ActionTools;
    StructuredGuidanceTools = GuidanceTools;
    Aliases = InitialAliases;
    _canonicalByName = new Dictionary<string, string>(
      StringComparer.OrdinalIgnoreCase
    );
    _canonicalByAlias = new Dictionary<string, string>(
      StringComparer.OrdinalIgnoreCase
    );

    foreach (var canonical in CanonicalTools)
    {
      if (!_canonicalByName.TryAdd(
        canonical,
        canonical
      ))
      {
        throw new InvalidOperationException(
          $"Duplicate canonical tool identifier '{canonical}'."
        );
      }
    }

    foreach (var registration in Aliases)
    {
      if (!_canonicalByName.ContainsKey(
        registration.CanonicalTool
      ))
      {
        throw new InvalidOperationException(
          $"Alias '{registration.Alias}' targets unknown canonical tool '{registration.CanonicalTool}'."
        );
      }

      if (_canonicalByName.ContainsKey(
        registration.Alias
      ))
      {
        throw new InvalidOperationException(
          $"Alias '{registration.Alias}' collides with a canonical tool identifier."
        );
      }

      if (_canonicalByAlias.TryGetValue(
        registration.Alias,
        out var existing
      ))
      {
        throw new InvalidOperationException(
          $"Alias '{registration.Alias}' has conflicting registrations for '{existing}' and '{registration.CanonicalTool}'."
        );
      }

      _canonicalByAlias.Add(
        registration.Alias,
        registration.CanonicalTool
      );
    }
    Registrations = CanonicalTools.Select(
      canonical => new CanonicalToolRegistration(
        canonical,
        CapabilityFor(canonical),
        SupportedHarnessesFor(canonical),
        RequiredPolicyFor(canonical),
        Aliases.Where(alias => alias.CanonicalTool == canonical)
          .Select(alias => alias.Alias)
          .Order(StringComparer.Ordinal)
          .ToArray(),
        1
      )
    ).ToArray();
  }

  public IReadOnlyList<string> CanonicalTools { get; }

  public IReadOnlyList<string> ExecutableTools { get; }

  public IReadOnlyList<string> StructuredGuidanceTools { get; }

  public IReadOnlyList<ToolAliasRegistration> Aliases { get; }

  public IReadOnlyList<CanonicalToolRegistration> Registrations { get; }

  public ToolNameResolution Resolve(
    string proposedName,
    IEnumerable<string> offeredCanonicalNames
  )
  {
    if (string.IsNullOrWhiteSpace(
      proposedName
    ))
    {
      throw new LocalActionException(
        "tool-name-resolution",
        "The proposed tool name cannot be empty."
      );
    }

    string canonical;
    string source;

    if (_canonicalByName.TryGetValue(
      proposedName,
      out var exactCanonical
    ))
    {
      canonical = exactCanonical;
      source = string.Equals(
        proposedName,
        canonical,
        StringComparison.Ordinal
      )
        ? CanonicalSource
        : CanonicalCaseSource;
    }
    else if (_canonicalByAlias.TryGetValue(
      proposedName,
      out var aliasCanonical
    ))
    {
      canonical = aliasCanonical;
      source = CuratedAliasSource;
    }
    else
    {
      throw new LocalActionException(
        "tool-name-resolution",
        DescribeUnknownTool(proposedName),
        proposedCanonicalTool: CanonicalSuggestion(proposedName)
      );
    }

    var offered = new HashSet<string>(
      offeredCanonicalNames,
      StringComparer.OrdinalIgnoreCase
    );

    if (!offered.Contains(
      canonical
    ))
    {
      throw new LocalActionException(
        "tool-phase-validation",
        $"Tool name '{Bounded(proposedName)}' resolves to '{canonical}', which is not offered in the current execution phase.",
        proposedCanonicalTool: canonical
      );
    }

    return new ToolNameResolution(
      proposedName,
      canonical,
      source
    );
  }

  public IReadOnlyList<string> AcceptedNamesFor(
    IEnumerable<string> offeredCanonicalNames
  )
  {
    var offered = new HashSet<string>(
      offeredCanonicalNames,
      StringComparer.OrdinalIgnoreCase
    );
    return CanonicalTools.Where(
      offered.Contains
    ).Concat(
      Aliases.Where(
        alias => offered.Contains(
          alias.CanonicalTool
        )
      ).Select(
        alias => alias.Alias
      )
    ).ToArray();
  }

  private static ToolAliasRegistration Alias(
    string alias,
    string canonicalTool
  )
  {
    return new ToolAliasRegistration(
      alias,
      canonicalTool
    );
  }

  private string DescribeUnknownTool(string proposedName)
  {
    var family = Registrations.Where(
      registration => string.Equals(
        registration.Capability,
        proposedName,
        StringComparison.OrdinalIgnoreCase
      )
    ).ToArray();
    return family.Length switch
    {
      1 => $"Unknown tool '{Bounded(proposedName)}'. Use canonical tool '{family[0].CanonicalName}'.",
      > 1 => $"Unknown tool '{Bounded(proposedName)}' names capability family '{family[0].Capability}'; request one exact canonical tool from that family.",
      _ => $"Unknown tool '{Bounded(proposedName)}'; no exact canonical name or registered alias matches."
    };
  }

  private string? CanonicalSuggestion(string proposedName)
  {
    var matches = Registrations.Where(
      registration => string.Equals(
        registration.Capability,
        proposedName,
        StringComparison.OrdinalIgnoreCase
      )
    ).Select(registration => registration.CanonicalName).ToArray();
    return matches.Length == 1 ? matches[0] : null;
  }

  private static string CapabilityFor(string canonical)
  {
    return canonical switch
    {
      "create_execution_plan" or "revise_execution_plan" or "get_execution_plan" => "plan-state",
      "request_toolset" => "capability-query",
      "list_files" or "get_file_info" => "workspace-inspection",
      "read_file" => "file-read",
      "search_text" => "file-search",
      "create_file" or "create_files" or "write_file" or "replace_text" or "apply_patch"
        or "delete_paths" or "rename_path" or "create_directory" => "file-mutation",
      "run_process" => "process-execution",
      "run_validation_profile" => "validation",
      var value when value.StartsWith("git_", StringComparison.Ordinal) => "git",
      var value when value == WebSearchCapability.ToolName => "web-search",
      var value when value == DiagnosticTraceCapability.ToolName => "diagnostic-read",
      _ => "host-capability"
    };
  }

  private static IReadOnlyList<string> SupportedHarnessesFor(string canonical)
  {
    return canonical == LocalActionPlanner.RequestToolsetTool
      ? [HarnessIds.Native]
      : [HarnessIds.Native, HarnessIds.Codex, HarnessIds.OpenCode, HarnessIds.QwenCode, HarnessIds.ClaudeCode];
  }

  private static string RequiredPolicyFor(string canonical)
  {
    return canonical switch
    {
      "run_process" => "process-policy",
      var value when value is "create_file" or "create_files" or "write_file" or "replace_text"
        or "apply_patch" or "delete_paths" or "rename_path" or "create_directory"
        || value.StartsWith("git_", StringComparison.Ordinal) => "workspace-and-approval-policy",
      _ => "current-mode-and-capability-policy"
    };
  }

  private static string Bounded(
    string value
  )
  {
    const int limit = 120;
    return value.Length <= limit
      ? value
      : value[..limit] + "...";
  }
}
