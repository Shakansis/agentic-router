using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public sealed class LinuxGpuMemoryMetricsProvider : IGpuMemoryMetricsProvider
{
  private const long Mebibyte = 1024 * 1024;

  public GpuMemoryMetricsSnapshot GetStatus()
  {
    if (!OperatingSystem.IsLinux())
    {
      return new GpuMemoryMetricsSnapshot(
        [],
        "unavailable",
        "Linux GPU memory telemetry is unavailable on this operating system."
      );
    }

    try
    {
      var nvidia = TryGetNvidiaStatus();
      var sysfs = GetSysfsStatus(
        includeNvidia: nvidia.Count == 0
      );
      var devices = nvidia.Concat(
        sysfs
      ).ToArray();
      if (devices.Length == 0)
      {
        return new GpuMemoryMetricsSnapshot(
          [],
          "available",
          null
        );
      }

      var partial = devices.Any(
        device => device.Status != "available"
      );
      return new GpuMemoryMetricsSnapshot(
        devices,
        partial
          ? "partial"
          : "available",
        partial
          ? "At least one Linux adapter did not expose complete adapter-wide VRAM usage."
          : null
      );
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or FormatException
        or OverflowException
        or InvalidOperationException
    )
    {
      return new GpuMemoryMetricsSnapshot(
        [],
        "unavailable",
        $"Linux GPU memory telemetry is unavailable: {exception.Message}"
      );
    }
  }

  private static IReadOnlyList<GpuMemoryStatus> TryGetNvidiaStatus()
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
      "--query-gpu=index,uuid,name,memory.total,memory.used"
    );
    process.StartInfo.ArgumentList.Add(
      "--format=csv,noheader,nounits"
    );

    try
    {
      if (
        !process.Start()
        || !process.WaitForExit(
          2_000
        )
      )
      {
        TryStop(
          process
        );
        return [];
      }
      if (process.ExitCode != 0)
      {
        return [];
      }

      var devices = new List<GpuMemoryStatus>();
      foreach (var line in process.StandardOutput.ReadToEnd().Split(
        ['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
      ))
      {
        var fields = line.Split(
          ',',
          5,
          StringSplitOptions.TrimEntries
        );
        if (
          fields.Length != 5
          || !int.TryParse(
            fields[0],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var index
          )
          || !long.TryParse(
            fields[3],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var totalMib
          )
          || !long.TryParse(
            fields[4],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var usedMib
          )
          || totalMib <= 0
          || usedMib < 0
          || usedMib > totalMib
        )
        {
          continue;
        }

        var total = checked(totalMib * Mebibyte);
        var used = checked(usedMib * Mebibyte);
        devices.Add(
          new GpuMemoryStatus(
            fields[1],
            fields[2],
            total,
            used,
            Math.Clamp(
              used * 100d / total,
              0,
              100
            ),
            "available",
            "Adapter-wide dedicated-memory usage reported by NVIDIA SMI.",
            index
          )
        );
      }

      return devices;
    }
    catch (Exception exception) when (
      exception is Win32Exception
        or InvalidOperationException
    )
    {
      return [];
    }
  }

  private static IReadOnlyList<GpuMemoryStatus> GetSysfsStatus(
    bool includeNvidia
  )
  {
    const string drmRoot = "/sys/class/drm";
    if (!Directory.Exists(drmRoot))
    {
      return [];
    }

    var devices = new List<GpuMemoryStatus>();
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
      var vendor = ReadTrimmed(
        Path.Combine(devicePath, "vendor")
      );
      var manufacturer = Manufacturer(vendor);
      if (!includeNvidia && manufacturer == "NVIDIA")
      {
        continue;
      }

      var properties = ReadKeyValues(
        Path.Combine(devicePath, "uevent")
      );
      var slot = properties.GetValueOrDefault(
        "PCI_SLOT_NAME"
      );
      var pciId = properties.GetValueOrDefault(
        "PCI_ID"
      );
      var name = ReadTrimmed(
        Path.Combine(devicePath, "product_name")
      ) ?? (string.IsNullOrWhiteSpace(pciId)
        ? $"{manufacturer ?? "Graphics adapter"} ({card})"
        : $"{manufacturer ?? "Graphics adapter"} ({pciId})");
      var total = ReadNonNegativeInt64(
        Path.Combine(devicePath, "mem_info_vram_total")
      );
      var used = ReadNonNegativeInt64(
        Path.Combine(devicePath, "mem_info_vram_used")
      );
      var complete = total is > 0
        && used is >= 0
        && used <= total;
      devices.Add(
        new GpuMemoryStatus(
          string.IsNullOrWhiteSpace(slot)
            ? $"linux-{card}"
            : $"pci-{slot}",
          name,
          total is > 0
            ? total
            : null,
          complete
            ? used
            : null,
          complete
            ? Math.Clamp(
              used!.Value * 100d / total!.Value,
              0,
              100
            )
            : null,
          complete
            ? "available"
            : "partial",
          complete
            ? "Adapter-wide VRAM usage reported by the Linux DRM driver."
            : "The Linux DRM driver did not expose complete adapter-wide VRAM usage."
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

  private static long? ReadNonNegativeInt64(
    string path
  )
  {
    return long.TryParse(
      ReadTrimmed(path),
      NumberStyles.None,
      CultureInfo.InvariantCulture,
      out var value
    ) && value >= 0
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
