using System.ComponentModel;
using System.Diagnostics;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.Setup;

public interface ISetupInstallerLauncher
{
  bool TryGetDefinition(
    string resourceId,
    out SetupInstallerDefinition definition
  );

  Task<SetupInstallerLaunchResult> StartAsync(
    SetupInstallerDefinition definition,
    string? profile,
    CancellationToken cancellationToken
  );
}

public sealed record SetupInstallerDefinition(
  string Id,
  string DisplayName
);

public sealed record SetupInstallerLaunchResult(
  string Message
);

public sealed class WindowsSetupInstallerLauncher : ISetupInstallerLauncher
{
  private static readonly IReadOnlyDictionary<string, WindowsInstallerDefinition>
    Installers = new Dictionary<string, WindowsInstallerDefinition>(
      StringComparer.OrdinalIgnoreCase
    )
    {
      ["ollama"] = new(
        "ollama",
        "Ollama",
        "winget install --id Ollama.Ollama --exact --source winget --accept-source-agreements --accept-package-agreements"
      ),
      [HarnessIds.Codex] = new(
        HarnessIds.Codex,
        "Codex",
        "winget install --id OpenAI.Codex --exact --source winget --accept-source-agreements --accept-package-agreements"
      ),
      [HarnessIds.ClaudeCode] = new(
        HarnessIds.ClaudeCode,
        "Claude Code",
        "winget install --id Anthropic.ClaudeCode --exact --source winget --accept-source-agreements --accept-package-agreements"
      ),
      [HarnessIds.OpenCode] = new(
        HarnessIds.OpenCode,
        "OpenCode",
        "npm install --global opencode-ai@latest"
      ),
      [HarnessIds.QwenCode] = new(
        HarnessIds.QwenCode,
        "Qwen Code",
        "npm install --global @qwen-code/qwen-code@latest"
      )
    };

  private readonly ILogger<WindowsSetupInstallerLauncher> _logger;

  public WindowsSetupInstallerLauncher(
    ILogger<WindowsSetupInstallerLauncher> logger
  )
  {
    _logger = logger;
  }

  public bool TryGetDefinition(
    string resourceId,
    out SetupInstallerDefinition definition
  )
  {
    if (Installers.TryGetValue(
      resourceId,
      out var installer
    ))
    {
      definition = new SetupInstallerDefinition(
        installer.Id,
        installer.DisplayName
      );
      return true;
    }

    definition = null!;
    return false;
  }

  public Task<SetupInstallerLaunchResult> StartAsync(
    SetupInstallerDefinition definition,
    string? profile,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (
      !OperatingSystem.IsWindows()
      || !Installers.TryGetValue(
        definition.Id,
        out var installer
      )
    )
    {
      throw new SetupException(
        "installer-not-found",
        $"No guided installer is registered for '{definition.Id}'."
      );
    }

    var script =
      "$Host.UI.RawUI.WindowTitle = 'Agentic Router - Install "
      + installer.DisplayName.Replace("'", "''", StringComparison.Ordinal)
      + "'; "
      + installer.Command
      + "; if ($LASTEXITCODE -ne 0) { Read-Host 'Installation failed. Press Enter to close' }";
    var startInfo = new ProcessStartInfo
    {
      FileName = "powershell.exe",
      UseShellExecute = true,
      WindowStyle = ProcessWindowStyle.Normal
    };
    startInfo.ArgumentList.Add("-NoLogo");
    startInfo.ArgumentList.Add("-NoProfile");
    startInfo.ArgumentList.Add("-ExecutionPolicy");
    startInfo.ArgumentList.Add("Bypass");
    startInfo.ArgumentList.Add("-Command");
    startInfo.ArgumentList.Add(script);

    try
    {
      using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        "The installer process did not start."
      );
      return Task.FromResult(
        new SetupInstallerLaunchResult(
          $"{installer.DisplayName} installer started in a separate window."
        )
      );
    }
    catch (Exception exception) when (
      exception is InvalidOperationException or Win32Exception
    )
    {
      _logger.LogWarning(
        exception,
        "The guided installer for {ResourceId} could not be started.",
        definition.Id
      );
      throw new SetupException(
        "installer-start-failed",
        $"The {installer.DisplayName} installer could not be started.",
        exception
      );
    }
  }

  private sealed record WindowsInstallerDefinition(
    string Id,
    string DisplayName,
    string Command
  );
}

public sealed class LinuxSetupInstallerLauncher : ISetupInstallerLauncher
{
  private readonly string _scriptPath;
  private readonly string _manifestDirectory;
  private readonly ILogger<LinuxSetupInstallerLauncher> _logger;

  public LinuxSetupInstallerLauncher(
    string dataDirectory,
    ILogger<LinuxSetupInstallerLauncher> logger
  )
  {
    _scriptPath = Path.Combine(
      AppContext.BaseDirectory,
      "scripts",
      "install-ollama-linux.sh"
    );
    _manifestDirectory = Path.Combine(
      dataDirectory,
      "installation-manifests",
      "ollama"
    );
    _logger = logger;
  }

  public bool TryGetDefinition(
    string resourceId,
    out SetupInstallerDefinition definition
  )
  {
    if (
      OperatingSystem.IsLinux()
      && string.Equals(
        resourceId,
        "ollama",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      definition = new SetupInstallerDefinition(
        "ollama",
        "Ollama"
      );
      return true;
    }

    definition = null!;
    return false;
  }

  public Task<SetupInstallerLaunchResult> StartAsync(
    SetupInstallerDefinition definition,
    string? profile,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (
      !TryGetDefinition(
        definition.Id,
        out _
      )
      || profile is not ("standard" or "vulkan" or "rocm")
    )
    {
      throw new SetupException(
        "installer-profile-invalid",
        "Select a valid Ollama acceleration profile before starting installation."
      );
    }
    if (!File.Exists(_scriptPath))
    {
      throw new SetupException(
        "installer-start-failed",
        "The packaged Linux Ollama installer script is missing."
      );
    }

    Directory.CreateDirectory(
      _manifestDirectory
    );
    foreach (var terminal in TerminalCandidates(
      profile
    ))
    {
      try
      {
        using var process = Process.Start(
          terminal
        );
        if (process is not null)
        {
          return Task.FromResult(
            new SetupInstallerLaunchResult(
              $"Ollama {profile} installer started in a separate terminal. Review the listed privileged changes before confirming."
            )
          );
        }
      }
      catch (Win32Exception)
      {
      }
      catch (InvalidOperationException exception)
      {
        _logger.LogDebug(
          exception,
          "Linux terminal candidate {Terminal} could not start.",
          terminal.FileName
        );
      }
    }

    throw new SetupException(
      "installer-start-failed",
      "No supported graphical terminal was found. Install x-terminal-emulator, GNOME Terminal, Konsole, or xterm, then retry."
    );
  }

  private IEnumerable<ProcessStartInfo> TerminalCandidates(
    string profile
  )
  {
    yield return Create(
      "x-terminal-emulator",
      "-T",
      "Agentic Router - Install Ollama",
      "-e",
      "bash",
      _scriptPath,
      profile,
      _manifestDirectory
    );
    yield return Create(
      "gnome-terminal",
      "--title=Agentic Router - Install Ollama",
      "--",
      "bash",
      _scriptPath,
      profile,
      _manifestDirectory
    );
    yield return Create(
      "konsole",
      "--hold",
      "-p",
      "tabtitle=Agentic Router - Install Ollama",
      "-e",
      "bash",
      _scriptPath,
      profile,
      _manifestDirectory
    );
    yield return Create(
      "xterm",
      "-T",
      "Agentic Router - Install Ollama",
      "-e",
      "bash",
      _scriptPath,
      profile,
      _manifestDirectory
    );
  }

  private static ProcessStartInfo Create(
    string executable,
    params string[] arguments
  )
  {
    var startInfo = new ProcessStartInfo
    {
      FileName = executable,
      UseShellExecute = false,
      CreateNoWindow = false
    };
    foreach (var argument in arguments)
    {
      startInfo.ArgumentList.Add(
        argument
      );
    }
    return startInfo;
  }
}
