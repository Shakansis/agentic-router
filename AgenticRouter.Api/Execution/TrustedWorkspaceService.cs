using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Platform;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Execution;

public interface ITrustedWorkspaceService
{
  Task<TrustedWorkspaceStatus> GetStatusAsync(
    CancellationToken cancellationToken
  );

  Task<TrustedWorkspaceStatus> ConfigureAsync(
    string path,
    CancellationToken cancellationToken
  );

  Task<TrustedWorkspaceStatus> ClearAsync(
    CancellationToken cancellationToken
  );

  Task<string> ResolvePathAsync(
    string? path,
    CancellationToken cancellationToken
  );

  Task<TrustedWorkspacePathResolution> ResolveCreationPathAsync(
    string? path,
    CancellationToken cancellationToken
  );
}

public sealed record TrustedWorkspacePathResolution(
  string FullPath,
  string RelativePath,
  string? OriginalPath,
  bool RebasedToWorkspace
);

public sealed class TrustedWorkspaceService : ITrustedWorkspaceService
{
  private readonly IWorkspaceProfileService _profiles;

  public TrustedWorkspaceService(
    IWorkspaceProfileService profiles
  )
  {
    _profiles = profiles;
  }

  public async Task<TrustedWorkspaceStatus> GetStatusAsync(
    CancellationToken cancellationToken
  )
  {
    var active = await _profiles.GetActiveDataAsync(
      cancellationToken
    );

    return WorkspacePathValidator.Inspect(
      active?.Path
    );
  }

  public async Task<TrustedWorkspaceStatus> ConfigureAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    var status = WorkspacePathValidator.Inspect(
      path
    );

    if (!status.Valid || status.Path is null)
    {
      return status;
    }

    var profiles = await _profiles.GetAllAsync(
      cancellationToken
    );
    var existing = profiles.Profiles.FirstOrDefault(
      profile => string.Equals(
        Path.GetFullPath(
          profile.Path
        ),
        status.Path,
        FileSystemPathSemantics.Comparison
      )
    );
    WorkspaceProfileView profile;

    if (existing is null)
    {
      profile = await _profiles.CreateAsync(
        Path.GetFileName(
          status.Path
        ),
        status.Path,
        cancellationToken
      );
    }
    else
    {
      profile = existing;
    }

    if (!profile.Active)
    {
      await _profiles.ActivateAsync(
        profile.Id,
        cancellationToken
      );
    }

    return status;
  }

  public async Task<TrustedWorkspaceStatus> ClearAsync(
    CancellationToken cancellationToken
  )
  {
    var active = await _profiles.GetActiveDataAsync(
      cancellationToken
    );

    if (active is not null)
    {
      await _profiles.RemoveAsync(
        active.Id,
        cancellationToken
      );
    }

    return WorkspacePathValidator.Inspect(
      null
    );
  }

  public async Task<string> ResolvePathAsync(
    string? path,
    CancellationToken cancellationToken
  )
  {
    return (await ResolvePathCoreAsync(
      path,
      cancellationToken
    )).FullPath;
  }

  public Task<TrustedWorkspacePathResolution> ResolveCreationPathAsync(
    string? path,
    CancellationToken cancellationToken
  )
  {
    return ResolvePathCoreAsync(
      path,
      cancellationToken
    );
  }

  private async Task<TrustedWorkspacePathResolution> ResolvePathCoreAsync(
    string? path,
    CancellationToken cancellationToken
  )
  {
    var status = await GetStatusAsync(
      cancellationToken
    );

    if (!status.Configured || !status.Valid || status.Path is null)
    {
      throw new LocalActionException(
        "trusted-workspace",
        "Configure a valid trusted workspace before using Execute mode."
      );
    }

    var root = status.Path;

    if (!string.IsNullOrWhiteSpace(
      path
    ))
    {
      EnsurePathContainsNoControlCharacters(
        path
      );
    }

    string candidate;

    try
    {
      candidate = string.IsNullOrWhiteSpace(
        path
      )
        ? root
        : Path.GetFullPath(
          Path.IsPathFullyQualified(
            path
          )
            ? path
            : Path.Combine(
              root,
              path
            )
        );
    }
    catch (Exception exception) when (
      exception is ArgumentException
      or NotSupportedException
      or PathTooLongException
    )
    {
      throw new LocalActionException(
        "path-validation",
        "The requested path is invalid.",
        exception
      );
    }

    var relative = Path.GetRelativePath(
      root,
      candidate
    );

    var outsideWorkspace = IsOutsideWorkspace(
      relative
    );
    if (outsideWorkspace)
    {
      throw new LocalActionException(
        "path-validation",
        "The requested path is outside the trusted workspace."
      );
    }

    if (ContainsGitMetadataSegment(relative))
    {
      throw new LocalActionException(
        "path-validation",
        "Direct filesystem access to .git metadata is not allowed. Use the structured Git tools."
      );
    }

    EnsureNoReparsePoints(
      root,
      relative
    );

    return new TrustedWorkspacePathResolution(
      candidate,
      relative,
      path,
      false
    );
  }

  private static bool ContainsGitMetadataSegment(string relative)
  {
    return relative.Split(
      [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      ],
      StringSplitOptions.RemoveEmptyEntries
    ).Any(segment => string.Equals(
      segment,
      ".git",
      FileSystemPathSemantics.Comparison
    ));
  }

  private static bool IsOutsideWorkspace(
    string relative
  )
  {
    return Path.IsPathFullyQualified(
      relative
    ) || string.Equals(
      relative,
      "..",
      StringComparison.Ordinal
    ) || relative.StartsWith(
      $"..{Path.DirectorySeparatorChar}",
      StringComparison.Ordinal
    ) || relative.StartsWith(
      $"..{Path.AltDirectorySeparatorChar}",
      StringComparison.Ordinal
    );
  }

  private static void EnsurePathContainsNoControlCharacters(
    string path
  )
  {
    foreach (var character in path)
    {
      if (!char.IsControl(
        character
      ))
      {
        continue;
      }

      throw new LocalActionException(
        "path-validation",
        $"The requested path contains control character U+{(int)character:X4}. "
          + "Use '/' as the path separator in tool arguments; a JSON backslash must be escaped."
      );
    }
  }

  private static TrustedWorkspaceStatus Inspect(
    string? path
  )
  {
    if (string.IsNullOrWhiteSpace(
      path
    ))
    {
      return new TrustedWorkspaceStatus(
        false,
        false,
        null,
        "Not configured",
        null
      );
    }

    string fullPath;

    try
    {
      fullPath = Path.GetFullPath(
        path
      );
    }
    catch (Exception exception) when (
      exception is ArgumentException
      or NotSupportedException
      or PathTooLongException
    )
    {
      return new TrustedWorkspaceStatus(
        true,
        false,
        path,
        "Invalid",
        "The configured path is invalid."
      );
    }

    if (!Directory.Exists(
      fullPath
    ))
    {
      return new TrustedWorkspaceStatus(
        true,
        false,
        fullPath,
        "Invalid",
        "The configured directory does not exist."
      );
    }

    if (IsReparsePoint(
      fullPath
    ))
    {
      return new TrustedWorkspaceStatus(
        true,
        false,
        fullPath,
        "Invalid",
        "Reparse points cannot be used as a trusted workspace."
      );
    }

    return new TrustedWorkspaceStatus(
      true,
      true,
      fullPath,
      "Configured",
      null
    );
  }

  private static void EnsureNoReparsePoints(
    string root,
    string relative
  )
  {
    if (IsReparsePoint(
      root
    ))
    {
      throw new LocalActionException(
        "path-validation",
        "The trusted workspace cannot be a reparse point."
      );
    }

    var current = root;

    foreach (var segment in relative.Split(
      [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      ],
      StringSplitOptions.RemoveEmptyEntries
    ))
    {
      current = Path.Combine(
        current,
        segment
      );

      if (
        (
          File.Exists(
            current
          )
          || Directory.Exists(
            current
          )
        )
        && IsReparsePoint(
          current
        )
      )
      {
        throw new LocalActionException(
          "path-validation",
          "Paths containing reparse points are not allowed."
        );
      }
    }
  }

  private static bool IsReparsePoint(
    string path
  )
  {
    return (
      File.GetAttributes(
        path
      )
      & FileAttributes.ReparsePoint
    ) != 0;
  }
}

public sealed class LocalActionException : Exception
{
  public LocalActionException(
    string stage,
    string message,
    Exception? innerException = null,
    string? proposedCanonicalTool = null
  )
    : base(
      message,
      innerException
    )
  {
    Stage = stage;
    ProposedCanonicalTool = proposedCanonicalTool;
  }

  public string Stage { get; }

  public string? ProposedCanonicalTool { get; }
}
