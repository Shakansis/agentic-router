using AgenticRouter.Api.WorkspaceProfiles;

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
  private readonly IWorkspaceProfileService _workspaceProfiles;

  public ProcessPolicyService(
    ITrustedWorkspaceService workspace,
    IWorkspaceProfileService workspaceProfiles
  )
  {
    _workspace = workspace;
    _workspaceProfiles = workspaceProfiles;
  }

  public async Task<ValidatedProcessCommand> ValidateAsync(
    string executable,
    IReadOnlyList<string> arguments,
    string? workingDirectory,
    CancellationToken cancellationToken
  )
  {
    EnsureContainsNoControlCharacters(
      executable,
      "Process executable"
    );

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

    var shellInterpreter = ShellExecutables.Contains(
      executableName
    );

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
    else
    {
      executable = ResolveExecutable(
        executable
      ) ?? executable;
    }

    if (arguments.Count > 100 || arguments.Any(
      argument => argument.Length > 2_048
    ))
    {
      throw new LocalActionException(
        "process-validation",
        "Process arguments exceed the supported safe limits."
      );
    }

    foreach (var argument in shellInterpreter
      ? []
      : arguments)
    {
      EnsureContainsNoControlCharacters(
        argument,
        "Process argument"
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

    foreach (var argument in arguments)
    {
      var pathCandidate = ExtractPathCandidate(argument);
      if (pathCandidate is null)
      {
        continue;
      }
      var absoluteCandidate = Path.GetFullPath(pathCandidate, resolvedWorkingDirectory);
      await _workspace.ResolvePathAsync(absoluteCandidate, cancellationToken);
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
    var permissionGranted = !safe && await _workspaceProfiles.HasProcessPermissionAsync(
      executable,
      arguments,
      resolvedWorkingDirectory,
      cancellationToken
    );

    return new ValidatedProcessCommand(
      executable,
      arguments,
      resolvedWorkingDirectory,
      !safe && !permissionGranted
    );
  }

  private static void EnsureContainsNoControlCharacters(
    string value,
    string label
  )
  {
    foreach (var character in value)
    {
      if (!char.IsControl(
        character
      ))
      {
        continue;
      }

      throw new LocalActionException(
        "process-validation",
        $"{label} contains control character U+{(int)character:X4}. "
          + "Use '/' for path separators in tool arguments; a JSON backslash must be escaped."
      );
    }
  }

  private static string? ExtractPathCandidate(string argument)
  {
    var candidate = argument.Trim();
    if (candidate.Length >= 2
      && candidate[0] == candidate[^1]
      && candidate[0] is '\'' or '"')
    {
      candidate = candidate[1..^1];
    }
    var assignment = candidate.LastIndexOf('=');
    if (assignment >= 0 && assignment < candidate.Length - 1)
    {
      candidate = candidate[(assignment + 1)..];
    }
    if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !uri.IsFile)
    {
      return null;
    }
    if (OperatingSystem.IsWindows()
      && candidate.StartsWith("/", StringComparison.Ordinal)
      && candidate.IndexOfAny(['/', '\\'], 1) < 0)
    {
      return null;
    }
    return Path.IsPathRooted(candidate)
      || candidate.StartsWith(".", StringComparison.Ordinal)
      || candidate.Contains(Path.DirectorySeparatorChar)
      || candidate.Contains(Path.AltDirectorySeparatorChar)
        ? candidate
        : null;
  }

  private static string? ResolveExecutable(string executable)
  {
    var fileName = Path.GetFileName(
      executable
    );
    var candidateNames = Path.HasExtension(
      fileName
    )
      ? [fileName]
      : OperatingSystem.IsWindows()
        ? new[]
        {
          fileName + ".exe",
          fileName + ".cmd",
          fileName + ".bat",
          fileName
        }
        : [fileName];
    var directories = new List<string>();
    if (OperatingSystem.IsWindows())
    {
      directories.Add(
        Environment.SystemDirectory
      );
      var windowsDirectory = Environment.GetFolderPath(
        Environment.SpecialFolder.Windows
      );
      if (!string.IsNullOrWhiteSpace(
        windowsDirectory
      ))
      {
        directories.Add(
          Path.Combine(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0"
          )
        );
      }
    }
    directories.AddRange(
      (Environment.GetEnvironmentVariable(
        "PATH"
      ) ?? string.Empty).Split(
        Path.PathSeparator,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
      ).Select(
        directory => directory.Trim(
          '"'
        )
      ).Where(
        Path.IsPathFullyQualified
      )
    );

    foreach (var directory in directories.Distinct(
      OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal
    ))
    {
      foreach (var candidateName in candidateNames)
      {
        var candidate = Path.Combine(
          directory,
          candidateName
        );
        if (File.Exists(
          candidate
        ))
        {
          return Path.GetFullPath(
            candidate
          );
        }
      }
    }

    return null;
  }
}
