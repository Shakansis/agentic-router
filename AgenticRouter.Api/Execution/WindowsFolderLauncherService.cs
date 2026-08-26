using System.Diagnostics;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public interface IFolderLauncherService
{
  Task<FolderOpenResult> OpenAsync(
    string path,
    CancellationToken cancellationToken
  );
}

public sealed class WindowsFolderLauncherService : IFolderLauncherService
{
  private readonly ILogger<WindowsFolderLauncherService> _logger;

  public WindowsFolderLauncherService(
    ILogger<WindowsFolderLauncherService> logger
  )
  {
    _logger = logger;
  }

  public Task<FolderOpenResult> OpenAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();

    if (!OperatingSystem.IsWindows())
    {
      return Task.FromResult(
        new FolderOpenResult(
          false,
          "Opening a workspace folder is available only on Windows."
        )
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
      return Task.FromResult(
        new FolderOpenResult(
          false,
          "The active workspace path is invalid."
        )
      );
    }

    if (!Directory.Exists(fullPath))
    {
      return Task.FromResult(
        new FolderOpenResult(
          false,
          "The active workspace folder is unavailable."
        )
      );
    }

    try
    {
      using var process = Process.Start(
        new ProcessStartInfo
        {
          FileName = "explorer.exe",
          UseShellExecute = true,
          ArgumentList =
          {
            fullPath
          }
        }
      );

      return Task.FromResult(
        process is null
          ? new FolderOpenResult(
            false,
            "Windows Explorer could not be started."
          )
          : new FolderOpenResult(
            true,
            null
          )
      );
    }
    catch (Exception exception)
    {
      _logger.LogError(
        exception,
        "Windows Explorer could not open the active workspace folder."
      );
      return Task.FromResult(
        new FolderOpenResult(
          false,
          "Windows Explorer could not open the active workspace folder."
        )
      );
    }
  }
}
