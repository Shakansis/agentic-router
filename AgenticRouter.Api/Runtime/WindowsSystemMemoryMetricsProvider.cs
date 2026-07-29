using System.ComponentModel;
using System.Runtime.InteropServices;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public sealed class WindowsSystemMemoryMetricsProvider : ISystemMemoryMetricsProvider
{
  public SystemMemoryStatus GetStatus()
  {
    if (!OperatingSystem.IsWindows())
    {
      return Unavailable(
        "System RAM metrics are currently supported only on Windows."
      );
    }

    var status = new MemoryStatusEx
    {
      Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
    };

    if (!GlobalMemoryStatusEx(
      ref status
    ))
    {
      return Unavailable(
        new Win32Exception(
          Marshal.GetLastWin32Error()
        ).Message
      );
    }

    var total = checked((long)status.TotalPhysical);
    var available = checked((long)status.AvailablePhysical);
    var used = Math.Max(
      0,
      total - available
    );

    return new SystemMemoryStatus(
      total,
      used,
      available,
      total > 0
        ? used * 100d / total
        : null,
      "available",
      null
    );
  }

  private static SystemMemoryStatus Unavailable(
    string diagnostic
  )
  {
    return new SystemMemoryStatus(
      null,
      null,
      null,
      null,
      "unavailable",
      diagnostic
    );
  }

  [StructLayout(
    LayoutKind.Sequential,
    CharSet = CharSet.Auto
  )]
  private struct MemoryStatusEx
  {
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhysical;
    public ulong AvailablePhysical;
    public ulong TotalPageFile;
    public ulong AvailablePageFile;
    public ulong TotalVirtual;
    public ulong AvailableVirtual;
    public ulong AvailableExtendedVirtual;
  }

  [DllImport(
    "kernel32.dll",
    SetLastError = true
  )]
  [return: MarshalAs(
    UnmanagedType.Bool
  )]
  private static extern bool GlobalMemoryStatusEx(
    ref MemoryStatusEx buffer
  );
}
