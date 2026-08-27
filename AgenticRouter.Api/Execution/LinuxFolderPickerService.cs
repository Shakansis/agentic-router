using System.ComponentModel;
using System.Diagnostics;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public sealed class LinuxFolderPickerService : IFolderPickerService
{
  private readonly ILogger<LinuxFolderPickerService> _logger;

  public LinuxFolderPickerService(
    ILogger<LinuxFolderPickerService> logger
  )
  {
    _logger = logger;
  }

  public async Task<FolderPickerResult> PickAsync(
    string? initialPath,
    CancellationToken cancellationToken
  )
  {
    if (!OperatingSystem.IsLinux())
    {
      return Unavailable(
        "The Linux folder picker is unavailable on this operating system."
      );
    }

    var normalizedInitialPath = !string.IsNullOrWhiteSpace(initialPath)
      && Directory.Exists(initialPath)
        ? Path.GetFullPath(initialPath)
        : null;
    var zenity = await TryPickAsync(
      "zenity",
      arguments =>
      {
        arguments.Add("--file-selection");
        arguments.Add("--directory");
        arguments.Add("--title=Select trusted workspace");
        if (normalizedInitialPath is not null)
        {
          arguments.Add($"--filename={normalizedInitialPath.TrimEnd(Path.DirectorySeparatorChar)}/");
        }
      },
      cancellationToken
    );
    if (zenity.Available)
    {
      return zenity.Result!;
    }

    var kdialog = await TryPickAsync(
      "kdialog",
      arguments =>
      {
        arguments.Add("--getexistingdirectory");
        arguments.Add(normalizedInitialPath ?? Directory.GetCurrentDirectory());
        arguments.Add("--title");
        arguments.Add("Select trusted workspace");
      },
      cancellationToken
    );
    return kdialog.Available
      ? kdialog.Result!
      : Unavailable(
        "No supported graphical folder picker was found. Install zenity or kdialog, or enter the workspace path manually."
      );
  }

  private async Task<PickerAttempt> TryPickAsync(
    string executable,
    Action<ICollection<string>> configureArguments,
    CancellationToken cancellationToken
  )
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };
    configureArguments(
      process.StartInfo.ArgumentList
    );

    try
    {
      if (!process.Start())
      {
        return new PickerAttempt(
          false,
          null
        );
      }

      var outputTask = process.StandardOutput.ReadToEndAsync(
        cancellationToken
      );
      var errorTask = process.StandardError.ReadToEndAsync(
        cancellationToken
      );
      try
      {
        await process.WaitForExitAsync(
          cancellationToken
        );
      }
      catch
      {
        TryStop(
          process
        );
        throw;
      }

      var output = (await outputTask).TrimEnd(
        '\r',
        '\n'
      );
      var error = (await errorTask).Trim();
      if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
      {
        return new PickerAttempt(
          true,
          new FolderPickerResult(
            true,
            false,
            Path.GetFullPath(output),
            null
          )
        );
      }

      if (process.ExitCode == 1)
      {
        return new PickerAttempt(
          true,
          new FolderPickerResult(
            false,
            true,
            null,
            null
          )
        );
      }

      return new PickerAttempt(
        true,
        Unavailable(
          string.IsNullOrWhiteSpace(error)
            ? $"{executable} could not select a workspace folder."
            : $"{executable} could not select a workspace folder: {error}"
        )
      );
    }
    catch (Win32Exception)
    {
      return new PickerAttempt(
        false,
        null
      );
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return new PickerAttempt(
        true,
        Unavailable(
          $"{executable} timed out while selecting a workspace folder."
        )
      );
    }
    catch (Exception exception) when (
      exception is InvalidOperationException
        or IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException
    )
    {
      _logger.LogWarning(
        exception,
        "Linux folder picker {Executable} failed.",
        executable
      );
      return new PickerAttempt(
        true,
        Unavailable(
          $"{executable} could not select a workspace folder."
        )
      );
    }
  }

  private static FolderPickerResult Unavailable(
    string diagnostic
  )
  {
    return new FolderPickerResult(
      false,
      false,
      null,
      diagnostic
    );
  }

  private static void TryStop(
    Process process
  )
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(
          entireProcessTree: true
        );
      }
    }
    catch (InvalidOperationException)
    {
    }
  }

  private sealed record PickerAttempt(
    bool Available,
    FolderPickerResult? Result
  );
}
