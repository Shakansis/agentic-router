using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Execution;

public interface ILocalActionService
{
  Task<ValidatedLocalAction> ValidateAsync(
    LocalActionProposal proposal,
    CancellationToken cancellationToken
  );

  Task<LocalActionResult> ExecuteAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  );
}

public sealed record LocalActionProposal(
  string Tool,
  JsonElement Arguments,
  string? Explanation
);

public sealed record ValidatedLocalAction(
  string ActionId,
  string Tool,
  JsonElement Arguments,
  string? TargetPath,
  string? WorkingDirectory,
  string Summary,
  string? Preview,
  bool ReadOnly,
  bool RequiresExplicitApproval
);

public sealed record LocalActionResult(
  string Output,
  string EventType
);

public sealed class LocalActionService : ILocalActionService
{
  private const int FileReadLimit = 128 * 1_024;
  private const int FileWriteLimit = 1024 * 1_024;
  private static readonly HashSet<string> Tools = new(
    [
      "list_files",
      "read_file",
      "create_file",
      "write_file",
      "replace_text",
      "apply_patch",
      "create_directory",
      "run_process"
    ],
    StringComparer.Ordinal
  );
  private static readonly HashSet<string> ShellExecutables = new(
    [
      "cmd",
      "command",
      "powershell",
      "pwsh",
      "bash",
      "sh",
      "zsh",
      "wsl",
      "cscript",
      "wscript"
    ],
    StringComparer.OrdinalIgnoreCase
  );
  private static readonly HashSet<string> SafeDotnetCommands = new(
    [
      "build",
      "test",
      "format",
      "restore",
      "--info",
      "--version"
    ],
    StringComparer.OrdinalIgnoreCase
  );
  private static readonly HashSet<string> SafeGitCommands = new(
    [
      "status",
      "diff",
      "log",
      "show",
      "branch",
      "rev-parse"
    ],
    StringComparer.OrdinalIgnoreCase
  );
  private static readonly HashSet<string> BlockedGitCommands = new(
    [
      "clean",
      "reset",
      "rm",
      "restore"
    ],
    StringComparer.OrdinalIgnoreCase
  );

  private readonly ITrustedWorkspaceService _workspace;
  private readonly IProcessExecutionService _processExecution;

  public LocalActionService(
    ITrustedWorkspaceService workspace,
    IProcessExecutionService processExecution
  )
  {
    _workspace = workspace;
    _processExecution = processExecution;
  }

  public async Task<ValidatedLocalAction> ValidateAsync(
    LocalActionProposal proposal,
    CancellationToken cancellationToken
  )
  {
    if (!Tools.Contains(
      proposal.Tool
    ))
    {
      throw new LocalActionException(
        "action-validation",
        $"Tool '{proposal.Tool}' is not available."
      );
    }

    return proposal.Tool == "run_process"
      ? await ValidateProcessAsync(
        proposal,
        cancellationToken
      )
      : await ValidateFileActionAsync(
        proposal,
        cancellationToken
      );
  }

  public async Task<LocalActionResult> ExecuteAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return action.Tool switch
      {
        "list_files" => await ListFilesAsync(
          action,
          cancellationToken
        ),
        "read_file" => await ReadFileAsync(
          action,
          cancellationToken
        ),
        "create_file" => await CreateFileAsync(
          action,
          cancellationToken
        ),
        "write_file" => await WriteFileAsync(
          action,
          cancellationToken
        ),
        "replace_text" => await ReplaceTextAsync(
          action,
          cancellationToken
        ),
        "apply_patch" => await ApplyPatchAsync(
          action,
          cancellationToken
        ),
        "create_directory" => CreateDirectory(
          action
        ),
        "run_process" => await RunProcessAsync(
          action,
          cancellationToken
        ),
        _ => throw new LocalActionException(
          "action-execution",
          $"Tool '{action.Tool}' is not available."
        )
      };
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new LocalActionException(
        "action-execution",
        exception.Message,
        exception
      );
    }
  }

  private async Task<ValidatedLocalAction> ValidateFileActionAsync(
    LocalActionProposal proposal,
    CancellationToken cancellationToken
  )
  {
    var path = GetOptionalString(
      proposal.Arguments,
      "path"
    );
    var targetPath = await _workspace.ResolvePathAsync(
      path,
      cancellationToken
    );
    var relativePath = await GetRelativePathAsync(
      targetPath,
      cancellationToken
    );
    var readOnly = proposal.Tool is "list_files" or "read_file";
    string? preview = null;

    if (proposal.Tool is "create_file" or "write_file")
    {
      preview = LimitPreview(
        GetRequiredString(
          proposal.Arguments,
          "content"
        )
      );
    }
    else if (proposal.Tool == "replace_text")
    {
      preview = $"Replace:\n{LimitPreview(GetRequiredString(proposal.Arguments, "oldText"))}"
        + $"\n\nWith:\n{LimitPreview(GetRequiredString(proposal.Arguments, "newText"))}";
    }
    else if (proposal.Tool == "apply_patch")
    {
      var replacements = GetReplacements(
        proposal.Arguments
      );
      preview = string.Join(
        "\n\n",
        replacements.Select(
          replacement => $"Replace:\n{LimitPreview(replacement.OldText)}"
            + $"\nWith:\n{LimitPreview(replacement.NewText)}"
        )
      );
    }

    return new ValidatedLocalAction(
      Guid.NewGuid().ToString(
        "N"
      ),
      proposal.Tool,
      proposal.Arguments.Clone(),
      targetPath,
      null,
      $"{proposal.Tool}: {relativePath}",
      preview,
      readOnly,
      false
    );
  }

  private async Task<ValidatedLocalAction> ValidateProcessAsync(
    LocalActionProposal proposal,
    CancellationToken cancellationToken
  )
  {
    var executable = GetRequiredString(
      proposal.Arguments,
      "executable"
    ).Trim();

    if (string.IsNullOrWhiteSpace(
      executable
    ))
    {
      throw new LocalActionException(
        "process-validation",
        "Process executable is required."
      );
    }

    var executableName = Path.GetFileNameWithoutExtension(
      executable
    );

    if (ShellExecutables.Contains(
      executableName
    ))
    {
      throw new LocalActionException(
        "process-validation",
        "Shell interpreters are not allowed. Use a structured executable and argument list."
      );
    }

    if (
      !Path.IsPathFullyQualified(
        executable
      )
      && (
        executable.Contains(
          Path.DirectorySeparatorChar
        )
        || executable.Contains(
          Path.AltDirectorySeparatorChar
        )
      )
    )
    {
      throw new LocalActionException(
        "process-validation",
        "Executable paths must be absolute or use a bare executable name."
      );
    }

    if (Path.IsPathFullyQualified(
      executable
    ))
    {
      executable = await _workspace.ResolvePathAsync(
        executable,
        cancellationToken
      );
    }

    var arguments = GetStringArray(
      proposal.Arguments,
      "arguments"
    );

    if (arguments.Count > 100 || arguments.Any(
      argument => argument.Length > 2_048 || argument.Contains(
        '\0',
        StringComparison.Ordinal
      )
    ))
    {
      throw new LocalActionException(
        "process-validation",
        "Process arguments exceed the supported safe limits."
      );
    }

    var workingDirectory = await _workspace.ResolvePathAsync(
      GetOptionalString(
        proposal.Arguments,
        "workingDirectory"
      ),
      cancellationToken
    );

    if (!Directory.Exists(
      workingDirectory
    ))
    {
      throw new LocalActionException(
        "process-validation",
        "Process working directory does not exist."
      );
    }

    var command = arguments.FirstOrDefault() ?? string.Empty;

    if (
      executableName.Equals(
        "git",
        StringComparison.OrdinalIgnoreCase
      )
      && BlockedGitCommands.Contains(
        command
      )
    )
    {
      throw new LocalActionException(
        "process-validation",
        $"The potentially destructive git command '{command}' is blocked."
      );
    }

    var safe = (
      executableName.Equals(
        "dotnet",
        StringComparison.OrdinalIgnoreCase
      )
      && SafeDotnetCommands.Contains(
        command
      )
    ) || (
      executableName.Equals(
        "git",
        StringComparison.OrdinalIgnoreCase
      )
      && SafeGitCommands.Contains(
        command
      )
    );
    var preview = $"{executable} {string.Join(" ", arguments.Select(QuoteArgument))}";

    return new ValidatedLocalAction(
      Guid.NewGuid().ToString(
        "N"
      ),
      proposal.Tool,
      proposal.Arguments.Clone(),
      executable,
      workingDirectory,
      $"run_process: {Path.GetFileName(executable)}",
      LimitPreview(
        preview
      ),
      false,
      !safe
    );
  }

  private async Task<LocalActionResult> ListFilesAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );

    if (!Directory.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The directory does not exist."
      );
    }

    var recursive = GetOptionalBoolean(
      action.Arguments,
      "recursive"
    );
    var entries = EnumerateEntries(
      target,
      recursive
    ).Take(
      500
    );
    var root = await _workspace.ResolvePathAsync(
      null,
      cancellationToken
    );
    var output = string.Join(
      "\n",
      entries.Select(
        entry => Directory.Exists(
          entry
        )
          ? $"{Path.GetRelativePath(root, entry)}/"
          : Path.GetRelativePath(
            root,
            entry
          )
      )
    );

    return new LocalActionResult(
      string.IsNullOrEmpty(
        output
      )
        ? "[directory is empty]"
        : output,
      "action.output"
    );
  }

  private static async Task<LocalActionResult> ReadFileAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var file = new FileInfo(
      target
    );

    if (!file.Exists)
    {
      throw new LocalActionException(
        "action-execution",
        "The file does not exist."
      );
    }

    if (file.Length > FileReadLimit)
    {
      throw new LocalActionException(
        "action-execution",
        "The file exceeds the 128 KiB read limit."
      );
    }

    return new LocalActionResult(
      await File.ReadAllTextAsync(
        target,
        cancellationToken
      ),
      "action.output"
    );
  }

  private static async Task<LocalActionResult> CreateFileAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var content = GetRequiredString(
      action.Arguments,
      "content"
    );
    ValidateContent(
      content
    );

    if (File.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The file already exists."
      );
    }

    EnsureParentExists(
      target
    );
    await File.WriteAllTextAsync(
      target,
      content,
      new UTF8Encoding(
        false
      ),
      cancellationToken
    );

    return new LocalActionResult(
      $"Created {Path.GetFileName(target)} ({content.Length} characters).",
      "action.edit-applied"
    );
  }

  private static async Task<LocalActionResult> WriteFileAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var content = GetRequiredString(
      action.Arguments,
      "content"
    );
    ValidateContent(
      content
    );

    if (!File.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The file does not exist. Use create_file for a new file."
      );
    }

    await WriteAtomicallyAsync(
      target,
      content,
      cancellationToken
    );

    return new LocalActionResult(
      $"Updated {Path.GetFileName(target)} ({content.Length} characters).",
      "action.edit-applied"
    );
  }

  private static async Task<LocalActionResult> ReplaceTextAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var oldText = GetRequiredString(
      action.Arguments,
      "oldText"
    );
    var newText = GetRequiredString(
      action.Arguments,
      "newText"
    );
    var replaceAll = GetOptionalBoolean(
      action.Arguments,
      "replaceAll"
    );
    var content = await ReadEditableFileAsync(
      target,
      cancellationToken
    );
    var first = content.IndexOf(
      oldText,
      StringComparison.Ordinal
    );

    if (first < 0)
    {
      throw new LocalActionException(
        "action-execution",
        "The requested text was not found."
      );
    }

    var updated = replaceAll
      ? content.Replace(
        oldText,
        newText,
        StringComparison.Ordinal
      )
      : string.Concat(
        content.AsSpan(
          0,
          first
        ),
        newText,
        content.AsSpan(
          first + oldText.Length
        )
      );
    ValidateContent(
      updated
    );
    await WriteAtomicallyAsync(
      target,
      updated,
      cancellationToken
    );

    return new LocalActionResult(
      $"Replaced text in {Path.GetFileName(target)}.",
      "action.edit-applied"
    );
  }

  private static async Task<LocalActionResult> ApplyPatchAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var content = await ReadEditableFileAsync(
      target,
      cancellationToken
    );
    var replacements = GetReplacements(
      action.Arguments
    );

    foreach (var replacement in replacements)
    {
      var index = content.IndexOf(
        replacement.OldText,
        StringComparison.Ordinal
      );

      if (index < 0)
      {
        throw new LocalActionException(
          "action-execution",
          "A patch search block was not found; no changes were written."
        );
      }

      content = string.Concat(
        content.AsSpan(
          0,
          index
        ),
        replacement.NewText,
        content.AsSpan(
          index + replacement.OldText.Length
        )
      );
    }

    ValidateContent(
      content
    );
    await WriteAtomicallyAsync(
      target,
      content,
      cancellationToken
    );

    return new LocalActionResult(
      $"Applied {replacements.Count} patch replacement(s) to {Path.GetFileName(target)}.",
      "action.edit-applied"
    );
  }

  private static LocalActionResult CreateDirectory(
    ValidatedLocalAction action
  )
  {
    var target = RequiredTarget(
      action
    );

    if (Directory.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The directory already exists."
      );
    }

    Directory.CreateDirectory(
      target
    );

    return new LocalActionResult(
      $"Created directory {Path.GetFileName(target)}.",
      "action.edit-applied"
    );
  }

  private async Task<LocalActionResult> RunProcessAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var arguments = GetStringArray(
      action.Arguments,
      "arguments"
    );
    var timeoutSeconds = GetOptionalInteger(
      action.Arguments,
      "timeoutSeconds",
      30
    );

    if (timeoutSeconds is < 1 or > 120)
    {
      throw new LocalActionException(
        "process-validation",
        "Process timeout must be between 1 and 120 seconds."
      );
    }

    var result = await _processExecution.ExecuteAsync(
      new ProcessExecutionRequest(
        action.TargetPath!,
        arguments,
        action.WorkingDirectory!,
        TimeSpan.FromSeconds(
          timeoutSeconds
        )
      ),
      cancellationToken
    );
    var output = new StringBuilder();
    output.AppendLine(
      $"Exit code: {result.ExitCode}"
    );

    if (!string.IsNullOrWhiteSpace(
      result.StandardOutput
    ))
    {
      output.AppendLine(
        "stdout:"
      );
      output.AppendLine(
        result.StandardOutput.TrimEnd()
      );
    }

    if (!string.IsNullOrWhiteSpace(
      result.StandardError
    ))
    {
      output.AppendLine(
        "stderr:"
      );
      output.AppendLine(
        result.StandardError.TrimEnd()
      );
    }

    if (result.TimedOut)
    {
      output.AppendLine(
        "Process timed out and was terminated."
      );
    }

    return new LocalActionResult(
      output.ToString().TrimEnd(),
      "action.process-output"
    );
  }

  private async Task<string> GetRelativePathAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    var root = await _workspace.ResolvePathAsync(
      null,
      cancellationToken
    );

    return Path.GetRelativePath(
      root,
      path
    );
  }

  private static string RequiredTarget(
    ValidatedLocalAction action
  )
  {
    return action.TargetPath ?? throw new LocalActionException(
      "action-validation",
      "The action has no validated target path."
    );
  }

  private static void ValidateContent(
    string content
  )
  {
    if (Encoding.UTF8.GetByteCount(
      content
    ) > FileWriteLimit)
    {
      throw new LocalActionException(
        "action-validation",
        "File content exceeds the 1 MiB write limit."
      );
    }
  }

  private static async Task<string> ReadEditableFileAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    if (!File.Exists(
      path
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The file does not exist."
      );
    }

    var content = await File.ReadAllTextAsync(
      path,
      cancellationToken
    );
    ValidateContent(
      content
    );
    return content;
  }

  private static async Task WriteAtomicallyAsync(
    string path,
    string content,
    CancellationToken cancellationToken
  )
  {
    var temporary = Path.Combine(
      Path.GetDirectoryName(
        path
      )!,
      $".agentic-router-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      await File.WriteAllTextAsync(
        temporary,
        content,
        new UTF8Encoding(
          false
        ),
        cancellationToken
      );
      File.Move(
        temporary,
        path,
        true
      );
    }
    finally
    {
      if (File.Exists(
        temporary
      ))
      {
        File.Delete(
          temporary
        );
      }
    }
  }

  private static void EnsureParentExists(
    string path
  )
  {
    var parent = Path.GetDirectoryName(
      path
    );

    if (
      parent is null
      || !Directory.Exists(
        parent
      )
    )
    {
      throw new LocalActionException(
        "action-execution",
        "The parent directory does not exist."
      );
    }
  }

  private static IEnumerable<string> EnumerateEntries(
    string root,
    bool recursive
  )
  {
    var pending = new Queue<string>();
    pending.Enqueue(
      root
    );

    while (pending.Count > 0)
    {
      var directory = pending.Dequeue();

      foreach (var entry in Directory.EnumerateFileSystemEntries(
        directory
      ))
      {
        yield return entry;

        if (
          recursive
          && Directory.Exists(
            entry
          )
          && (
            File.GetAttributes(
              entry
            )
            & FileAttributes.ReparsePoint
          ) == 0
        )
        {
          pending.Enqueue(
            entry
          );
        }
      }

      if (!recursive)
      {
        yield break;
      }
    }
  }

  private static string GetRequiredString(
    JsonElement arguments,
    string name
  )
  {
    var value = GetOptionalString(
      arguments,
      name
    );

    if (value is null)
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' is required."
      );
    }

    return value;
  }

  private static string? GetOptionalString(
    JsonElement arguments,
    string name
  )
  {
    if (
      arguments.ValueKind != JsonValueKind.Object
      || !arguments.TryGetProperty(
        name,
        out var value
      )
      || value.ValueKind == JsonValueKind.Null
    )
    {
      return null;
    }

    if (value.ValueKind != JsonValueKind.String)
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be a string."
      );
    }

    return value.GetString();
  }

  private static IReadOnlyList<string> GetStringArray(
    JsonElement arguments,
    string name
  )
  {
    if (
      !arguments.TryGetProperty(
        name,
        out var value
      )
      || value.ValueKind != JsonValueKind.Array
    )
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be an array of strings."
      );
    }

    var result = new List<string>();

    foreach (var item in value.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        throw new LocalActionException(
          "action-validation",
          $"Action argument '{name}' must contain only strings."
        );
      }

      result.Add(
        item.GetString() ?? string.Empty
      );
    }

    return result;
  }

  private static bool GetOptionalBoolean(
    JsonElement arguments,
    string name
  )
  {
    return arguments.TryGetProperty(
      name,
      out var value
    ) && value.ValueKind switch
    {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be a boolean."
      )
    };
  }

  private static int GetOptionalInteger(
    JsonElement arguments,
    string name,
    int defaultValue
  )
  {
    if (!arguments.TryGetProperty(
      name,
      out var value
    ))
    {
      return defaultValue;
    }

    if (
      value.ValueKind != JsonValueKind.Number
      || !value.TryGetInt32(
        out var number
      )
    )
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be an integer."
      );
    }

    return number;
  }

  private static IReadOnlyList<PatchReplacement> GetReplacements(
    JsonElement arguments
  )
  {
    if (
      !arguments.TryGetProperty(
        "replacements",
        out var value
      )
      || value.ValueKind != JsonValueKind.Array
    )
    {
      throw new LocalActionException(
        "action-validation",
        "Action argument 'replacements' must be an array."
      );
    }

    var replacements = value.EnumerateArray()
      .Select(
        replacement => new PatchReplacement(
          GetRequiredString(
            replacement,
            "oldText"
          ),
          GetRequiredString(
            replacement,
            "newText"
          )
        )
      )
      .ToArray();

    if (replacements.Length is < 1 or > 20)
    {
      throw new LocalActionException(
        "action-validation",
        "A patch must contain between 1 and 20 replacements."
      );
    }

    return replacements;
  }

  private static string LimitPreview(
    string value
  )
  {
    const int limit = 4_000;

    return value.Length <= limit
      ? value
      : $"{value[..limit]}\n[preview truncated]";
  }

  private static string QuoteArgument(
    string argument
  )
  {
    return argument.Any(
      char.IsWhiteSpace
    )
      ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
      : argument;
  }

  private sealed record PatchReplacement(
    string OldText,
    string NewText
  );
}
