using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Devices;

public sealed class LinuxGpuDiscoveryService : IGpuDiscoveryService
{
  private const long Mebibyte = 1024 * 1024;
  private readonly ILogger<LinuxGpuDiscoveryService> _logger;

  public LinuxGpuDiscoveryService(
    ILogger<LinuxGpuDiscoveryService> logger
  )
  {
    _logger = logger;
  }

  public async Task<DevicesResponse> DiscoverAsync(
    CancellationToken cancellationToken
  )
  {
    var automatic = new GraphicsDevice(
      "auto",
      "Auto",
      null,
      null,
      true,
      true,
      null
    );
    if (!OperatingSystem.IsLinux())
    {
      return new DevicesResponse(
        [automatic],
        "Linux GPU discovery is unavailable on this operating system."
      );
    }

    try
    {
      var nvidia = await DiscoverNvidiaAsync(
        cancellationToken
      );
      var sysfs = DiscoverSysfsDevices(
        includeNvidia: nvidia.Count == 0
      );
      var devices = nvidia.Concat(
        sysfs
      ).ToList();
      devices.Insert(
        0,
        automatic
      );

      if (devices.Count == 1)
      {
        return new DevicesResponse(
          devices,
          "Linux did not report a supported graphics adapter through nvidia-smi or /sys/class/drm."
        );
      }

      var hasUnindexedDevices = devices.Any(
        device => !device.IsAuto && device.OllamaIndex is null
      );
      return new DevicesResponse(
        devices,
        hasUnindexedDevices
          ? "Linux graphics adapters were detected. NVIDIA Ollama indices are authoritative from nvidia-smi; AMD and Intel identities are reported without inventing a cross-backend index."
          : "Linux NVIDIA device order, UUIDs, and Ollama indices were read from nvidia-smi."
      );
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or FormatException
        or OverflowException
    )
    {
      _logger.LogWarning(
        exception,
        "Linux GPU discovery failed."
      );
      return new DevicesResponse(
        [automatic],
        $"Linux GPU discovery is unavailable: {exception.Message}"
      );
    }
  }

  private async Task<List<GraphicsDevice>> DiscoverNvidiaAsync(
    CancellationToken cancellationToken
  )
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "nvidia-smi",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };
    process.StartInfo.ArgumentList.Add(
      "--query-gpu=index,uuid,name,memory.total"
    );
    process.StartInfo.ArgumentList.Add(
      "--format=csv,noheader,nounits"
    );

    try
    {
      if (!process.Start())
      {
        return [];
      }

      var outputTask = process.StandardOutput.ReadToEndAsync(
        cancellationToken
      );
      var errorTask = process.StandardError.ReadToEndAsync(
        cancellationToken
      );
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken
      );
      timeout.CancelAfter(
        TimeSpan.FromSeconds(
          3
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

      var output = await outputTask;
      var error = await errorTask;
      if (process.ExitCode != 0)
      {
        _logger.LogDebug(
          "nvidia-smi Linux GPU discovery returned exit code {ExitCode}: {Diagnostic}",
          process.ExitCode,
          error.Trim()
        );
        return [];
      }

      return ParseNvidia(
        output
      );
    }
    catch (Win32Exception)
    {
      return [];
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      _logger.LogDebug(
        "nvidia-smi Linux GPU discovery timed out."
      );
      return [];
    }
  }

  private static List<GraphicsDevice> ParseNvidia(
    string output
  )
  {
    var devices = new List<GraphicsDevice>();
    foreach (var line in output.Split(
      ['\r', '\n'],
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    ))
    {
      var fields = line.Split(
        ',',
        4,
        StringSplitOptions.TrimEntries
      );
      if (
        fields.Length != 4
        || !int.TryParse(
          fields[0],
          NumberStyles.None,
          CultureInfo.InvariantCulture,
          out var index
        )
        || index < 0
        || string.IsNullOrWhiteSpace(fields[1])
      )
      {
        continue;
      }

      long? memory = long.TryParse(
        fields[3],
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var memoryMib
      ) && memoryMib > 0
        ? checked(memoryMib * Mebibyte)
        : null;
      devices.Add(
        new GraphicsDevice(
          fields[1],
          fields[2],
          "NVIDIA",
          memory,
          true,
          false,
          index
        )
      );
    }

    return devices.OrderBy(
      device => device.OllamaIndex
    ).ToList();
  }

  private static IReadOnlyList<GraphicsDevice> DiscoverSysfsDevices(
    bool includeNvidia
  )
  {
    const string drmRoot = "/sys/class/drm";
    if (!Directory.Exists(drmRoot))
    {
      return [];
    }

    var devices = new List<GraphicsDevice>();
    foreach (var cardPath in Directory.EnumerateDirectories(
      drmRoot,
      "card*",
      SearchOption.TopDirectoryOnly
    ))
    {
      var card = Path.GetFileName(cardPath);
      if (
        card.Length <= 4
        || !int.TryParse(
          card[4..],
          NumberStyles.None,
          CultureInfo.InvariantCulture,
          out _
        )
      )
      {
        continue;
      }

      var devicePath = Path.Combine(
        cardPath,
        "device"
      );
      var vendorId = ReadTrimmed(
        Path.Combine(devicePath, "vendor")
      );
      var manufacturer = Manufacturer(vendorId);
      if (!includeNvidia && manufacturer == "NVIDIA")
      {
        continue;
      }

      var uevent = ReadKeyValues(
        Path.Combine(devicePath, "uevent")
      );
      var pciId = uevent.GetValueOrDefault(
        "PCI_ID"
      );
      var slot = uevent.GetValueOrDefault(
        "PCI_SLOT_NAME"
      );
      var productName = ReadTrimmed(
        Path.Combine(devicePath, "product_name")
      );
      var name = !string.IsNullOrWhiteSpace(productName)
        ? productName
        : !string.IsNullOrWhiteSpace(pciId)
          ? $"{manufacturer ?? "Graphics adapter"} ({pciId})"
          : $"{manufacturer ?? "Graphics adapter"} ({card})";
      devices.Add(
        new GraphicsDevice(
          string.IsNullOrWhiteSpace(slot)
            ? $"linux-{card}"
            : $"pci-{slot}",
          name,
          manufacturer,
          ReadPositiveInt64(
            Path.Combine(devicePath, "mem_info_vram_total")
          ),
          true,
          false,
          null
        )
      );
    }

    return devices.OrderBy(
      device => device.Id,
      StringComparer.Ordinal
    ).ToArray();
  }

  private static Dictionary<string, string> ReadKeyValues(
    string path
  )
  {
    if (!File.Exists(path))
    {
      return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    return File.ReadLines(path).Select(
      line => line.Split(
        '=',
        2
      )
    ).Where(
      fields => fields.Length == 2
    ).ToDictionary(
      fields => fields[0],
      fields => fields[1],
      StringComparer.Ordinal
    );
  }

  private static string? ReadTrimmed(
    string path
  )
  {
    return File.Exists(path)
      ? File.ReadAllText(path).Trim()
      : null;
  }

  private static long? ReadPositiveInt64(
    string path
  )
  {
    return long.TryParse(
      ReadTrimmed(path),
      NumberStyles.None,
      CultureInfo.InvariantCulture,
      out var value
    ) && value > 0
      ? value
      : null;
  }

  private static string? Manufacturer(
    string? vendorId
  )
  {
    return vendorId?.ToLowerInvariant() switch
    {
      "0x1002" => "AMD",
      "0x10de" => "NVIDIA",
      "0x8086" => "Intel",
      _ => null
    };
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
