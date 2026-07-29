using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

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
}

public sealed class TrustedWorkspaceService : ITrustedWorkspaceService
{
  private readonly ISettingsStore _settingsStore;

  public TrustedWorkspaceService(
    ISettingsStore settingsStore
  )
  {
    _settingsStore = settingsStore;
  }

  public async Task<TrustedWorkspaceStatus> GetStatusAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );

    return Inspect(
      settings.TrustedWorkspacePath
    );
  }

  public async Task<TrustedWorkspaceStatus> ConfigureAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    var status = Inspect(
      path
    );

    if (!status.Valid || status.Path is null)
    {
      return status;
    }

    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var result = await _settingsStore.SaveAsync(
      settings with
      {
        TrustedWorkspacePath = status.Path
      },
      cancellationToken
    );

    if (!result.IsValid)
    {
      return new TrustedWorkspaceStatus(
        false,
        false,
        null,
        "Invalid",
        string.Join(
          " ",
          result.Errors.SelectMany(
            pair => pair.Value
          )
        )
      );
    }

    return status;
  }

  public async Task<TrustedWorkspaceStatus> ClearAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    await _settingsStore.SaveAsync(
      settings with
      {
        TrustedWorkspacePath = null
      },
      cancellationToken
    );

    return Inspect(
      null
    );
  }

  public async Task<string> ResolvePathAsync(
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

    if (
      Path.IsPathFullyQualified(
        relative
      )
      || string.Equals(
        relative,
        "..",
        StringComparison.Ordinal
      )
      || relative.StartsWith(
        $"..{Path.DirectorySeparatorChar}",
        StringComparison.Ordinal
      )
      || relative.StartsWith(
        $"..{Path.AltDirectorySeparatorChar}",
        StringComparison.Ordinal
      )
    )
    {
      throw new LocalActionException(
        "path-validation",
        "The requested path is outside the trusted workspace."
      );
    }

    EnsureNoReparsePoints(
      root,
      relative
    );

    return candidate;
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
    Exception? innerException = null
  )
    : base(
      message,
      innerException
    )
  {
    Stage = stage;
  }

  public string Stage { get; }
}
