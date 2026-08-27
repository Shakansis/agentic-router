using System.ComponentModel;
using System.Diagnostics;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public sealed class LinuxFolderLauncherService : IFolderLauncherService
{
  private readonly ILogger<LinuxFolderLauncherService> _logger;

  public LinuxFolderLauncherService(
    ILogger<LinuxFolderLauncherService> logger
  )
  {
    _logger = logger;
  }

  public async Task<FolderOpenResult> OpenAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();

    if (!OperatingSystem.IsLinux())
    {
      return Unavailable(
        "The Linux workspace launcher is unavailable on this operating system."
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
      return Unavailable(
        "The active workspace path is invalid."
      );
    }

    if (!Directory.Exists(fullPath))
    {
      return Unavailable(
        "The active workspace folder is unavailable."
      );
    }

    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "xdg-open",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        ArgumentList =
        {
          fullPath
        }
      }
    };

    try
    {
      if (!process.Start())
      {
        return Unavailable(
          "xdg-open could not be started."
        );
      }

      var errorTask = process.StandardError.ReadToEndAsync(
        cancellationToken
      );
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken
      );
      timeout.CancelAfter(
        TimeSpan.FromSeconds(
          10
        )
      );
      try
      {
        await process.WaitForExitAsync(
          timeout.Token
        );
      }
      catch
      {
        TryStop(
          process
        );
        throw;
      }

      var diagnostic = (await errorTask).Trim();
      return process.ExitCode == 0
        ? new FolderOpenResult(
          true,
          null
        )
        : Unavailable(
          string.IsNullOrWhiteSpace(diagnostic)
            ? "xdg-open could not open the active workspace folder."
            : $"xdg-open could not open the active workspace folder: {diagnostic}"
        );
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      return Unavailable(
        "xdg-open timed out while opening the active workspace folder."
      );
    }
    catch (Exception exception) when (
      exception is Win32Exception
        or InvalidOperationException
        or IOException
    )
    {
      _logger.LogWarning(
        exception,
        "xdg-open could not open the active workspace folder."
      );
      return Unavailable(
        "xdg-open is unavailable. Install xdg-utils or open the workspace path manually."
      );
    }
  }

  private static FolderOpenResult Unavailable(
    string diagnostic
  )
  {
    return new FolderOpenResult(
      false,
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
}
