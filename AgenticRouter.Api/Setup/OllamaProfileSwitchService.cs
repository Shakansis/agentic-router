using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Recovery;

namespace AgenticRouter.Api.Setup;

public interface IOllamaProfileSwitchService
{
  Task<OllamaProfileSwitchPlan> PrepareAsync(
    string targetProfile,
    CancellationToken cancellationToken
  );

  Task<SetupActionResult> StartAsync(
    string planId,
    CancellationToken cancellationToken
  );
}

public sealed class OllamaProfileSwitchService : IOllamaProfileSwitchService
{
  private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(
    10
  );

  private readonly string _manifestDirectory;
  private readonly string _scriptPath;
  private readonly IGpuDiscoveryService _gpuDiscovery;
  private readonly IOllamaInstallationProfileStore _profiles;
  private readonly SafeModeState _safeMode;
  private readonly ILogger<OllamaProfileSwitchService> _logger;
  private readonly ConcurrentDictionary<string, PendingSwitchPlan> _plans = new(
    StringComparer.Ordinal
  );

  public OllamaProfileSwitchService(
    string dataDirectory,
    IGpuDiscoveryService gpuDiscovery,
    IOllamaInstallationProfileStore profiles,
    SafeModeState safeMode,
    ILogger<OllamaProfileSwitchService> logger
  )
  {
    _manifestDirectory = Path.Combine(
      dataDirectory,
      "installation-manifests",
      "ollama"
    );
    _scriptPath = Path.Combine(
      AppContext.BaseDirectory,
      "scripts",
      "switch-ollama-linux-profile.sh"
    );
    _gpuDiscovery = gpuDiscovery;
    _profiles = profiles;
    _safeMode = safeMode;
    _logger = logger;
  }

  public async Task<OllamaProfileSwitchPlan> PrepareAsync(
    string targetProfile,
    CancellationToken cancellationToken
  )
  {
    EnsureLinux();
    var target = NormalizeProfile(
      targetProfile
    );
    var devices = await _gpuDiscovery.DiscoverAsync(
      cancellationToken
    );
    var options = OllamaInstallationProfiles.BuildOptions(
      devices
    );
    if (!options.Any(option => option.Id == target))
    {
      throw new SetupException(
        "profile-switch-hardware-incompatible",
        "The target Ollama profile is not compatible with the currently detected hardware."
      );
    }

    var current = await ReadCurrentProfileAsync(
      cancellationToken
    );
    if (current == target)
    {
      throw new SetupException(
        "profile-switch-not-needed",
        $"Ollama already uses the managed '{target}' package profile."
      );
    }

    var removableFiles = current == "rocm"
      ? await ReadRocmOnlyFilesAsync(
        cancellationToken
      )
      : [];
    var fingerprint = await FingerprintAsync(
      cancellationToken
    );
    var actions = new List<string>
    {
      "Stop the Ollama systemd service only while package files are replaced.",
      "Download and extract the current official Linux x64 base package to restore a coherent base installation."
    };
    if (current == "rocm")
    {
      actions.Add(
        $"Remove {removableFiles.Count} ROCm-only package files listed by the saved manifests; no recursive directory deletion is used."
      );
    }
    if (target == "rocm")
    {
      actions.Add(
        "Download and extract the current official ROCm supplemental package."
      );
    }
    actions.Add(
      target == "vulkan"
        ? "Create the Agentic Router-owned systemd override with OLLAMA_VULKAN=1."
        : "Remove only the Agentic Router-owned Vulkan override when it exists."
    );
    actions.Add(
      "Restart Ollama and independently re-check the observed backend."
    );
    actions.Add(
      "Preserve Ollama models, user data, settings, and all paths outside the recorded package manifests."
    );

    var now = DateTimeOffset.UtcNow;
    var plan = new OllamaProfileSwitchPlan(
      Guid.NewGuid().ToString("N"),
      current,
      target,
      actions,
      removableFiles.Count,
      true,
      now,
      now + PlanLifetime
    );
    _plans[plan.PlanId] = new PendingSwitchPlan(
      plan,
      fingerprint
    );
    RemoveExpiredPlans(
      now
    );
    return plan;
  }

  public async Task<SetupActionResult> StartAsync(
    string planId,
    CancellationToken cancellationToken
  )
  {
    EnsureLinux();
    if (_safeMode.Enabled)
    {
      throw new SetupException(
        "safe-mode-read-only",
        "Ollama profile changes are disabled in safe mode."
      );
    }
    if (
      string.IsNullOrWhiteSpace(planId)
      || !_plans.TryRemove(
        planId,
        out var pending
      )
      || pending.Plan.ExpiresAt <= DateTimeOffset.UtcNow
    )
    {
      throw new SetupException(
        "profile-switch-plan-expired",
        "The Ollama profile-change plan is missing or expired. Review a new plan before continuing."
      );
    }

    var current = await ReadCurrentProfileAsync(
      cancellationToken
    );
    var fingerprint = await FingerprintAsync(
      cancellationToken
    );
    if (
      current != pending.Plan.CurrentProfile
      || !CryptographicOperations.FixedTimeEquals(
        Convert.FromHexString(fingerprint),
        Convert.FromHexString(pending.Fingerprint)
      )
    )
    {
      throw new SetupException(
        "profile-switch-plan-stale",
        "The Ollama installation manifests changed after review. Review a new plan before continuing."
      );
    }
    if (!File.Exists(_scriptPath))
    {
      throw new SetupException(
        "installer-start-failed",
        "The packaged Linux Ollama profile-change script is missing."
      );
    }

    await _profiles.SetRequestedProfileAsync(
      pending.Plan.TargetProfile,
      cancellationToken
    );
    if (!TryStartTerminal(
      pending.Plan.CurrentProfile,
      pending.Plan.TargetProfile
    ))
    {
      throw new SetupException(
        "installer-start-failed",
        "No supported graphical terminal was found for the Ollama profile change."
      );
    }

    return new SetupActionResult(
      "ollama-profile",
      true,
      "started",
      $"Ollama profile change from {pending.Plan.CurrentProfile} to {pending.Plan.TargetProfile} started in a separate terminal. Review the privileged changes there before confirming."
    );
  }

  private bool TryStartTerminal(
    string currentProfile,
    string targetProfile
  )
  {
    foreach (var terminal in TerminalCandidates(
      currentProfile,
      targetProfile
    ))
    {
      try
      {
        using var process = Process.Start(
          terminal
        );
        if (process is not null)
        {
          return true;
        }
      }
      catch (Win32Exception)
      {
      }
      catch (InvalidOperationException exception)
      {
        _logger.LogDebug(
          exception,
          "Linux terminal candidate {Terminal} could not start the Ollama profile change.",
          terminal.FileName
        );
      }
    }
    return false;
  }

  private IEnumerable<ProcessStartInfo> TerminalCandidates(
    string currentProfile,
    string targetProfile
  )
  {
    var command = new[]
    {
      "bash",
      _scriptPath,
      currentProfile,
      targetProfile,
      _manifestDirectory
    };
    yield return CreateTerminal(
      "x-terminal-emulator",
      ["-T", "Agentic Router - Change Ollama profile", "-e", .. command]
    );
    yield return CreateTerminal(
      "gnome-terminal",
      ["--title=Agentic Router - Change Ollama profile", "--", .. command]
    );
    yield return CreateTerminal(
      "konsole",
      ["--hold", "-p", "tabtitle=Agentic Router - Change Ollama profile", "-e", .. command]
    );
    yield return CreateTerminal(
      "xterm",
      ["-T", "Agentic Router - Change Ollama profile", "-e", .. command]
    );
  }

  private static ProcessStartInfo CreateTerminal(
    string executable,
    IReadOnlyList<string> arguments
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

  private async Task<string> ReadCurrentProfileAsync(
    CancellationToken cancellationToken
  )
  {
    var path = Path.Combine(
      _manifestDirectory,
      "install.properties"
    );
    if (!File.Exists(path))
    {
      throw new SetupException(
        "profile-switch-manifest-missing",
        "A managed Ollama installation manifest is required before changing profiles."
      );
    }

    var line = (await File.ReadAllLinesAsync(
      path,
      cancellationToken
    )).FirstOrDefault(
      value => value.StartsWith(
        "requestedProfile=",
        StringComparison.Ordinal
      )
    );
    return NormalizeProfile(
      line?["requestedProfile=".Length..]
    );
  }

  private async Task<IReadOnlyList<string>> ReadRocmOnlyFilesAsync(
    CancellationToken cancellationToken
  )
  {
    var baseFiles = await ReadManifestFilesAsync(
      "base.files",
      cancellationToken
    );
    var rocmFiles = await ReadManifestFilesAsync(
      "rocm.files",
      cancellationToken
    );
    var removable = rocmFiles.Except(
      baseFiles,
      StringComparer.Ordinal
    ).Order(
      StringComparer.Ordinal
    ).ToArray();
    var unsafePath = removable.FirstOrDefault(
      path => !path.StartsWith(
        "usr/lib/ollama/",
        StringComparison.Ordinal
      )
    );
    if (unsafePath is not null)
    {
      throw new SetupException(
        "profile-switch-manifest-unsafe",
        $"The ROCm-only package inventory contains a path outside /usr/lib/ollama: '{unsafePath}'."
      );
    }
    return removable;
  }

  private async Task<HashSet<string>> ReadManifestFilesAsync(
    string fileName,
    CancellationToken cancellationToken
  )
  {
    var path = Path.Combine(
      _manifestDirectory,
      fileName
    );
    if (!File.Exists(path))
    {
      throw new SetupException(
        "profile-switch-manifest-missing",
        $"The managed Ollama manifest '{fileName}' is required before changing profiles."
      );
    }

    var files = new HashSet<string>(
      StringComparer.Ordinal
    );
    foreach (var raw in await File.ReadAllLinesAsync(
      path,
      cancellationToken
    ))
    {
      var normalized = raw.Trim().Replace(
        '\\',
        '/'
      ).TrimStart(
        '.'
      ).TrimStart(
        '/'
      );
      if (
        normalized.Length == 0
        || normalized.EndsWith(
          "/",
          StringComparison.Ordinal
        )
      )
      {
        continue;
      }
      if (
        raw.TrimStart().StartsWith(
          "/",
          StringComparison.Ordinal
        )
        || raw.TrimStart().StartsWith(
          "\\",
          StringComparison.Ordinal
        )
        ||
        normalized.Contains(
          "../",
          StringComparison.Ordinal
        )
      )
      {
        throw new SetupException(
          "profile-switch-manifest-unsafe",
          $"The package manifest contains an unsafe removal path: '{raw}'."
        );
      }
      files.Add(
        normalized
      );
    }
    return files;
  }

  private async Task<string> FingerprintAsync(
    CancellationToken cancellationToken
  )
  {
    using var hash = IncrementalHash.CreateHash(
      HashAlgorithmName.SHA256
    );
    foreach (var fileName in new[]
    {
      "install.properties",
      "base.files",
      "rocm.files"
    })
    {
      var path = Path.Combine(
        _manifestDirectory,
        fileName
      );
      hash.AppendData(
        Encoding.UTF8.GetBytes(
          fileName
        )
      );
      if (File.Exists(path))
      {
        hash.AppendData(
          await File.ReadAllBytesAsync(
            path,
            cancellationToken
          )
        );
      }
    }
    return Convert.ToHexString(
      hash.GetHashAndReset()
    );
  }

  private void RemoveExpiredPlans(
    DateTimeOffset now
  )
  {
    foreach (var pair in _plans)
    {
      if (pair.Value.Plan.ExpiresAt <= now)
      {
        _plans.TryRemove(
          pair.Key,
          out _
        );
      }
    }
  }

  private static string NormalizeProfile(
    string? profile
  )
  {
    var normalized = profile?.Trim().ToLowerInvariant();
    return normalized is "standard" or "vulkan" or "rocm"
      ? normalized
      : throw new SetupException(
        "installer-profile-invalid",
        "The Ollama profile must be standard, vulkan, or rocm."
      );
  }

  private static void EnsureLinux()
  {
    if (!OperatingSystem.IsLinux())
    {
      throw new SetupException(
        "installer-platform-unsupported",
        "Ollama profile changes are available only on Linux x64."
      );
    }
  }

  private sealed record PendingSwitchPlan(
    OllamaProfileSwitchPlan Plan,
    string Fingerprint
  );
}

public sealed record OllamaProfileSwitchPlan(
  string PlanId,
  string CurrentProfile,
  string TargetProfile,
  IReadOnlyList<string> Actions,
  int RocmOnlyFilesToRemove,
  bool PreservesModelsAndData,
  DateTimeOffset CreatedAt,
  DateTimeOffset ExpiresAt
);
