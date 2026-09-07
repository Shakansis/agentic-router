using System.Diagnostics;
using System.Globalization;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public sealed class WindowsGpuMemoryMetricsProvider : IGpuMemoryMetricsProvider
{
  private const long Mebibyte = 1024 * 1024;

  public GpuMemoryMetricsSnapshot GetStatus()
  {
    if (!OperatingSystem.IsWindows())
    {
      return new GpuMemoryMetricsSnapshot(
        [],
        "unavailable",
        "GPU memory telemetry is available only on Windows."
      );
    }

    try
    {
      var nvidiaDevices = TryGetNvidiaStatus()?.Devices ?? [];
      IReadOnlyList<GpuMemoryStatus> dxgiDevices;
      try
      {
        dxgiDevices = GetDxgiStatus(
          includeNvidia: nvidiaDevices.Count == 0
        );
      }
      catch (Exception exception) when (nvidiaDevices.Count > 0)
      {
        return new GpuMemoryMetricsSnapshot(
          nvidiaDevices,
          "partial",
          $"NVIDIA telemetry is available, but additional Windows adapters could not be enumerated: {exception.Message}"
        );
      }
      var devices = nvidiaDevices.Concat(
        dxgiDevices
      ).ToArray();
      var partial = devices.Any(
        device => device.Status != "available"
      );

      return new GpuMemoryMetricsSnapshot(
        devices,
        partial
          ? "partial"
          : "available",
        partial
          ? "At least one Windows graphics adapter did not expose complete adapter-wide VRAM usage."
          : null
      );
    }
    catch (Exception exception)
    {
      return new GpuMemoryMetricsSnapshot(
        [],
        "unavailable",
        $"Windows GPU telemetry is unavailable: {exception.Message}"
      );
    }
  }

  private static GpuMemoryMetricsSnapshot? TryGetNvidiaStatus()
  {
    try
    {
      var devices = new List<GpuMemoryStatus>();
      using var process = new Process
      {
        StartInfo = new ProcessStartInfo
        {
          FileName = "nvidia-smi.exe",
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
        return null;
      }

      if (process.ExitCode != 0)
      {
        return null;
      }

      foreach (var line in process.StandardOutput.ReadToEnd().Split(
        '\n',
        StringSplitOptions.RemoveEmptyEntries
      ))
      {
        var values = line.Split(
          ',',
          StringSplitOptions.TrimEntries
        );

        if (
          values.Length != 5
          || !long.TryParse(
            values[3],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var totalMiB
          )
          || !long.TryParse(
            values[4],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var usedMiB
          )
          || totalMiB <= 0
          || usedMiB < 0
          || usedMiB > totalMiB
        )
        {
          return null;
        }

        var totalBytes = checked(
          totalMiB * Mebibyte
        );
        var usedBytes = checked(
          usedMiB * Mebibyte
        );
        devices.Add(
          new GpuMemoryStatus(
            values[1],
            values[2],
            totalBytes,
            usedBytes,
            Math.Clamp(
              usedBytes * 100d / totalBytes,
              0,
              100
            ),
            "available",
            "Adapter-wide dedicated-memory usage reported by NVIDIA SMI.",
            int.TryParse(
              values[0],
              NumberStyles.Integer,
              CultureInfo.InvariantCulture,
              out var ollamaIndex
            )
              ? ollamaIndex
              : null,
            "NVIDIA",
            "cuda",
            int.TryParse(
              values[0],
              NumberStyles.Integer,
              CultureInfo.InvariantCulture,
              out var backendIndex
            )
              ? backendIndex
              : null
          )
        );
      }

      return devices.Count == 0
        ? null
        : new GpuMemoryMetricsSnapshot(
          devices,
          "available",
          null
        );
    }
    catch (
      Exception exception
    ) when (
      exception is InvalidOperationException
        or System.ComponentModel.Win32Exception
        or OverflowException
    )
    {
      return null;
    }
  }

  private static IReadOnlyList<GpuMemoryStatus> GetDxgiStatus(
    bool includeNvidia
  )
  {
    var adapters = WindowsGraphicsAdapterInventory.GetAdapters();

    return adapters.Where(
      adapter => includeNvidia || adapter.Manufacturer != "NVIDIA"
    ).Select(
      adapter =>
      {
        var complete = adapter.TotalDedicatedMemoryBytes is > 0
          && adapter.UsedDedicatedMemoryBytes is >= 0
          && adapter.UsedDedicatedMemoryBytes
            <= adapter.TotalDedicatedMemoryBytes;
        return new GpuMemoryStatus(
          adapter.Id,
          adapter.Name,
          adapter.TotalDedicatedMemoryBytes,
          complete
            ? adapter.UsedDedicatedMemoryBytes
            : null,
          complete
            ? Math.Clamp(
              adapter.UsedDedicatedMemoryBytes!.Value * 100d
                / adapter.TotalDedicatedMemoryBytes!.Value,
              0,
              100
            )
            : null,
          complete
            ? "available"
            : "partial",
          complete
            ? "Adapter-wide dedicated-memory usage reported by Windows GPU performance counters."
            : adapter.TotalDedicatedMemoryBytes is > 0
              ? "Total dedicated memory was reported by DXGI; adapter-wide current usage is unavailable."
              : "DXGI did not report dedicated memory for this adapter.",
          null,
          adapter.Manufacturer,
          null,
          null
        );
      }
    ).ToArray();
  }

  private static void TryStop(
    Process process
  )
  {
    try
    {
      process.Kill(
        entireProcessTree: true
      );
    }
    catch (
      InvalidOperationException
    )
    {
    }
  }
}
