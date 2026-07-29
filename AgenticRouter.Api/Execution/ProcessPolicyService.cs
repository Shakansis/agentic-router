namespace AgenticRouter.Api.Execution;

public sealed record ValidatedProcessCommand(
  string Executable,
  IReadOnlyList<string> Arguments,
  string WorkingDirectory,
  bool RequiresExplicitApproval
);

public interface IProcessPolicyService
{
  Task<ValidatedProcessCommand> ValidateAsync(
    string executable,
    IReadOnlyList<string> arguments,
    string? workingDirectory,
    CancellationToken cancellationToken
  );
}

public sealed class ProcessPolicyService : IProcessPolicyService
{
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
      "restore",
      "checkout",
      "stash",
      "commit",
      "push",
      "pull",
      "switch"
    ],
    StringComparer.OrdinalIgnoreCase
  );

  private readonly ITrustedWorkspaceService _workspace;

  public ProcessPolicyService(
    ITrustedWorkspaceService workspace
  )
  {
    _workspace = workspace;
  }

  public async Task<ValidatedProcessCommand> ValidateAsync(
    string executable,
    IReadOnlyList<string> arguments,
    string? workingDirectory,
    CancellationToken cancellationToken
  )
  {
    executable = executable.Trim();

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

    var resolvedWorkingDirectory = await _workspace.ResolvePathAsync(
      workingDirectory,
      cancellationToken
    );

    if (!Directory.Exists(
      resolvedWorkingDirectory
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

    return new ValidatedProcessCommand(
      executable,
      arguments,
      resolvedWorkingDirectory,
      !safe
    );
  }
}
