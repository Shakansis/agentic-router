using System.Diagnostics;
using System.Globalization;
using AgenticRouter.Api.Contracts;
using Vortice.DXGI;

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

    var nvidiaSnapshot = TryGetNvidiaStatus();

    if (nvidiaSnapshot is not null)
    {
      return nvidiaSnapshot;
    }

    return GetDxgiFallbackStatus();
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
        "--query-gpu=index,name,pci.bus_id,memory.total,memory.used"
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
            $"nvidia-{values[2]}",
            values[1],
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

  private static GpuMemoryMetricsSnapshot GetDxgiFallbackStatus()
  {
    try
    {
      using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
      var devices = new List<GpuMemoryStatus>();

      for (uint index = 0; ; index++)
      {
        var result = factory.EnumAdapters1(
          index,
          out var adapter
        );

        if (result.Failure)
        {
          break;
        }

        using (adapter)
        {
          var description = adapter.Description1;

          if ((description.Flags & AdapterFlags.Software) != 0)
          {
            continue;
          }

          var id = $"{description.Luid.HighPart:x8}{description.Luid.LowPart:x8}";
          var totalValue = description.DedicatedVideoMemory.Value.ToUInt64();
          long? total = totalValue <= long.MaxValue
            ? (long)totalValue
            : null;
          var hasDedicatedTotal = total is > 0;

          devices.Add(
            new GpuMemoryStatus(
              id,
              description.Description.Trim(),
              hasDedicatedTotal
                ? total
                : null,
              null,
              null,
              "partial",
              hasDedicatedTotal
                ? "Total dedicated memory reported by DXGI; current "
                  + "adapter-wide usage is unavailable for this device."
                : "The adapter did not report total dedicated memory, and "
                  + "adapter-wide current usage is unavailable."
            )
          );
        }
      }

      return new GpuMemoryMetricsSnapshot(
        devices,
        devices.Count == 0
          ? "available"
          : "partial",
        devices.Count == 0
          ? null
          : "DXGI fallback enumerated the adapters, but could not report "
            + "adapter-wide current usage."
      );
    }
    catch (Exception exception)
    {
      return new GpuMemoryMetricsSnapshot(
        [],
        "unavailable",
        $"DXGI adapter enumeration is unavailable: {exception.Message}"
      );
    }
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
