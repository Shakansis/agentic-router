using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.WorkspaceProfiles;

public sealed record WorkspaceProcessPermissionData
{
  public string Id { get; init; } = string.Empty;

  public string Executable { get; init; } = string.Empty;

  public string ArgumentsDigest { get; init; } = string.Empty;

  public int ArgumentCount { get; init; }

  public string WorkingDirectory { get; init; } = ".";

  public DateTimeOffset CreatedAt { get; init; }
}

internal static class WorkspaceProcessPermissionIdentity
{
  public static string NormalizeExecutable(string executable)
  {
    var normalized = executable.Trim();
    if (Path.IsPathFullyQualified(
      normalized
    ))
    {
      normalized = Path.GetFullPath(
        normalized
      ).Replace(
        Path.AltDirectorySeparatorChar,
        Path.DirectorySeparatorChar
      );
    }
    return OperatingSystem.IsWindows()
      ? normalized.ToLowerInvariant()
      : normalized;
  }

  public static string NormalizeWorkingDirectory(
    string workspacePath,
    string workingDirectory
  )
  {
    var root = Path.TrimEndingDirectorySeparator(
      Path.GetFullPath(
        workspacePath
      )
    );
    var resolved = Path.TrimEndingDirectorySeparator(
      Path.GetFullPath(
        workingDirectory
      )
    );
    var comparison = OperatingSystem.IsWindows()
      ? StringComparison.OrdinalIgnoreCase
      : StringComparison.Ordinal;
    if (
      !string.Equals(
        root,
        resolved,
        comparison
      )
      && !resolved.StartsWith(
        root + Path.DirectorySeparatorChar,
        comparison
      )
    )
    {
      throw new WorkspaceProfileException(
        "process-permission-working-directory-outside-workspace",
        "process-permission",
        "A persistent process permission must use a working directory inside its workspace.",
        false
      );
    }

    var relative = Path.GetRelativePath(
      root,
      resolved
    ).Replace(
      Path.DirectorySeparatorChar,
      '/'
    );
    return string.IsNullOrWhiteSpace(
      relative
    )
      ? "."
      : relative;
  }

  public static bool Matches(
    WorkspaceProcessPermissionData permission,
    string executable,
    IReadOnlyList<string> arguments,
    string relativeWorkingDirectory
  )
  {
    return string.Equals(
      permission.Executable,
      NormalizeExecutable(
        executable
      ),
      StringComparison.Ordinal
    ) && string.Equals(
      permission.WorkingDirectory,
      relativeWorkingDirectory,
      StringComparison.Ordinal
    ) && string.Equals(
      permission.ArgumentsDigest,
      ComputeArgumentsDigest(
        arguments
      ),
      StringComparison.Ordinal
    ) && permission.ArgumentCount == arguments.Count;
  }

  public static string ComputeArgumentsDigest(
    IReadOnlyList<string> arguments
  )
  {
    var canonical = JsonSerializer.Serialize(
      arguments
    );
    return Convert.ToHexString(
      SHA256.HashData(
        Encoding.UTF8.GetBytes(
          canonical
        )
      )
    ).ToLowerInvariant();
  }
}
