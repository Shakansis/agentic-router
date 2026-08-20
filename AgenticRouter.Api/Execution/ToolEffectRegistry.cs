namespace AgenticRouter.Api.Execution;

using System.Text.RegularExpressions;

public static class ToolEffects
{
  public const string Inspected = "inspected";
  public const string FileCreated = "file-created";
  public const string FileChanged = "file-changed";
  public const string FileDeleted = "file-deleted";
  public const string DirectoryCreated = "directory-created";
  public const string Validated = "validated";
  public const string ProcessExecuted = "process-executed";
  public const string GitChanged = "git-changed";
}

public static class ToolEffectRegistry
{
  private static readonly Regex HostTargetMarker = new(
    @"\[target:\s*(?<path>[^\]\r\n]+)\]",
    RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
  );

  private static readonly IReadOnlyDictionary<string, string> Effects =
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["list_files"] = ToolEffects.Inspected,
      ["read_file"] = ToolEffects.Inspected,
      ["get_file_info"] = ToolEffects.Inspected,
      ["search_text"] = ToolEffects.Inspected,
      ["create_file"] = ToolEffects.FileCreated,
      ["create_files"] = ToolEffects.FileCreated,
      ["write_file"] = ToolEffects.FileChanged,
      ["replace_text"] = ToolEffects.FileChanged,
      ["apply_patch"] = ToolEffects.FileChanged,
      ["delete_paths"] = ToolEffects.FileDeleted,
      ["create_directory"] = ToolEffects.DirectoryCreated,
      ["run_process"] = ToolEffects.ProcessExecuted,
      ["run_validation_profile"] = ToolEffects.Validated,
      ["git_status"] = ToolEffects.Inspected,
      ["git_diff"] = ToolEffects.Inspected,
      ["git_log"] = ToolEffects.Inspected,
      ["git_show_commit"] = ToolEffects.Inspected,
      ["git_stage_files"] = ToolEffects.GitChanged,
      ["git_unstage_files"] = ToolEffects.GitChanged,
      ["git_create_commit"] = ToolEffects.GitChanged,
      ["git_create_annotated_tag"] = ToolEffects.GitChanged,
      ["git_push_current_branch"] = ToolEffects.GitChanged,
      ["git_push_tag"] = ToolEffects.GitChanged
    };

  public static string? ForTool(string canonicalTool)
  {
    return Effects.TryGetValue(canonicalTool, out var effect)
      ? effect
      : null;
  }

  public static bool HasCompatibleTool(
    string effect,
    IEnumerable<string> availableTools
  )
  {
    return availableTools.Any(
      tool => Effects.TryGetValue(tool, out var offeredEffect)
        && AreCompatible(effect, offeredEffect)
    );
  }

  public static bool AreCompatible(
    string expectedEffect,
    string observedEffect
  )
  {
    if (string.Equals(expectedEffect, observedEffect, StringComparison.Ordinal))
    {
      return true;
    }

    return expectedEffect is ToolEffects.FileCreated or ToolEffects.FileChanged
      && observedEffect is ToolEffects.FileCreated or ToolEffects.FileChanged;
  }

  public static string? InferExpectedEffect(string title)
  {
    var canonicalEffect = Effects
      .OrderByDescending(pair => pair.Key.Length)
      .FirstOrDefault(
        pair => title.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)
      );
    if (!string.IsNullOrWhiteSpace(canonicalEffect.Key))
    {
      return canonicalEffect.Value;
    }

    if (ContainsAny(title, "valid", "test", "build", "format", "compil"))
    {
      return ToolEffects.Validated;
    }

    if (ContainsAny(title, "git", "stage", "commit", "push", "tag"))
    {
      return ToolEffects.GitChanged;
    }

    if (ContainsAny(
      title,
      "create directory",
      "create folder",
      "create project directory",
      "create project folder",
      "directory structure",
      "folder structure",
      "criar diret",
      "criar pasta",
      "estrutura de diret",
      "estrutura de pasta"
    ))
    {
      return ToolEffects.DirectoryCreated;
    }

    return new[]
      {
        Candidate(title, ToolEffects.FileDeleted, "delete", "remove", "exclude", "excluir", "apagar", "delet"),
        Candidate(title, ToolEffects.FileCreated, "create", "add file", "new file", "criar", "adicionar arquivo"),
        Candidate(title, ToolEffects.FileChanged, "implement", "write", "change", "edit", "update", "apply", "fix", "alter", "corrig", "design", "style", "integrat", "generat"),
        Candidate(title, ToolEffects.Inspected, "inspect", "read", "review", "search", "list", "analis", "ler", "revis", "buscar", "listar"),
        Candidate(title, ToolEffects.ProcessExecuted, "run", "execute", "command", "process", "execut", "comando", "processo")
      }
      .Where(candidate => candidate.Index >= 0)
      .OrderBy(candidate => candidate.Index)
      .Select(candidate => candidate.Effect)
      .FirstOrDefault();
  }

  public static bool IsMutation(string effect)
  {
    return effect is ToolEffects.FileCreated
      or ToolEffects.FileChanged
      or ToolEffects.FileDeleted
      or ToolEffects.DirectoryCreated
      or ToolEffects.GitChanged;
  }

  public static string WithHostTarget(string title, string relativePath)
  {
    if (TryGetHostTarget(title) is not null)
    {
      return title;
    }

    var suffix = $" [target: {relativePath}]";
    var maximumBaseLength = Math.Max(1, 100 - suffix.Length);
    return title[..Math.Min(title.Length, maximumBaseLength)].TrimEnd() + suffix;
  }

  public static string? TryGetHostTarget(string title)
  {
    var match = HostTargetMarker.Match(title);
    return match.Success
      ? match.Groups["path"].Value.Trim()
      : null;
  }

  public static bool IsGenericPlanStep(string title)
  {
    return ContainsAny(
      title,
      "perform requested local action",
      "complete requested local action",
      "implement requested file changes",
      "perform requested action",
      "complete requested action",
      "executar ação solicitada",
      "concluir ação solicitada",
      "implementar alterações solicitadas"
    );
  }

  private static bool ContainsAny(string value, params string[] fragments)
  {
    return fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
  }

  private static EffectCandidate Candidate(
    string value,
    string effect,
    params string[] fragments
  )
  {
    var index = fragments.Select(
      fragment => value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase)
    ).Where(candidate => candidate >= 0).DefaultIfEmpty(-1).Min();
    return new EffectCandidate(effect, index);
  }

  private sealed record EffectCandidate(string Effect, int Index);
}
