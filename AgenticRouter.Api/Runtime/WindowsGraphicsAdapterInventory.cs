using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Vortice.DXGI;

namespace AgenticRouter.Api.Runtime;

internal static partial class WindowsGraphicsAdapterInventory
{
  private const uint PdhFormatLarge = 0x00000400;
  private const uint PdhMoreData = 0x800007D2;
  private const uint PdhValidData = 0x00000000;
  private const uint PdhNewData = 0x00000001;
  private static readonly string DedicatedUsageCounter =
    @"\GPU Adapter Memory(*)\Dedicated Usage";

  public static IReadOnlyList<WindowsGraphicsAdapter> GetAdapters()
  {
    if (!OperatingSystem.IsWindows())
    {
      return [];
    }

    var dedicatedUsage = ReadDedicatedUsage();
    using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
    var adapters = new List<WindowsGraphicsAdapter>();

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

        var luid = FormatLuid(
          description.Luid.HighPart,
          description.Luid.LowPart
        );
        var totalValue = description.DedicatedVideoMemory.Value.ToUInt64();
        long? total = totalValue is > 0 and <= long.MaxValue
          ? (long)totalValue
          : null;
        dedicatedUsage.TryGetValue(
          luid,
          out var used
        );
        long? boundedUsed = used >= 0
          && total is > 0
          && used <= total
            ? used
            : null;
        var manufacturer = Manufacturer(
          description.VendorId
        );

        adapters.Add(
          new WindowsGraphicsAdapter(
            $"dxgi-{luid}",
            description.Description.Trim(),
            manufacturer,
            total,
            boundedUsed
          )
        );
      }
    }

    return adapters;
  }

  private static Dictionary<string, long> ReadDedicatedUsage()
  {
    var values = new Dictionary<string, long>(
      StringComparer.OrdinalIgnoreCase
    );
    IntPtr query = IntPtr.Zero;
    IntPtr counter = IntPtr.Zero;

    try
    {
      if (PdhOpenQuery(
        null,
        UIntPtr.Zero,
        out query
      ) != 0)
      {
        return values;
      }

      if (PdhAddEnglishCounter(
        query,
        DedicatedUsageCounter,
        UIntPtr.Zero,
        out counter
      ) != 0
        || PdhCollectQueryData(
          query
        ) != 0)
      {
        return values;
      }

      uint bufferSize = 0;
      uint itemCount = 0;
      var status = PdhGetFormattedCounterArray(
        counter,
        PdhFormatLarge,
        ref bufferSize,
        ref itemCount,
        IntPtr.Zero
      );
      if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
      {
        return values;
      }

      var buffer = Marshal.AllocHGlobal(
        checked((int)bufferSize)
      );
      try
      {
        status = PdhGetFormattedCounterArray(
          counter,
          PdhFormatLarge,
          ref bufferSize,
          ref itemCount,
          buffer
        );
        if (status != 0)
        {
          return values;
        }

        var itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
        for (var index = 0; index < itemCount; index++)
        {
          var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(
            IntPtr.Add(
              buffer,
              checked((int)index * itemSize)
            )
          );
          if (
            item.Value.Status is not PdhValidData and not PdhNewData
            || item.Value.LargeValue < 0
          )
          {
            continue;
          }

          var instance = Marshal.PtrToStringUni(
            item.Name
          );
          var match = instance is null
            ? null
            : GpuAdapterInstance().Match(
              instance
            );
          if (match is null || !match.Success)
          {
            continue;
          }

          var luid = FormatLuid(
            unchecked((int)Convert.ToUInt32(
              match.Groups["high"].Value,
              16
            )),
            Convert.ToUInt32(
              match.Groups["low"].Value,
              16
            )
          );
          values[luid] = checked(
            values.GetValueOrDefault(
              luid
            ) + item.Value.LargeValue
          );
        }
      }
      finally
      {
        Marshal.FreeHGlobal(
          buffer
        );
      }
    }
    catch (Exception exception) when (
      exception is DllNotFoundException
        or EntryPointNotFoundException
        or FormatException
        or OverflowException
    )
    {
      values.Clear();
    }
    finally
    {
      if (query != IntPtr.Zero)
      {
        PdhCloseQuery(
          query
        );
      }
    }

    return values;
  }

  private static string FormatLuid(
    int highPart,
    uint lowPart
  )
  {
    return $"{unchecked((uint)highPart):x8}{lowPart:x8}";
  }

  private static string? Manufacturer(
    uint vendorId
  )
  {
    return vendorId switch
    {
      0x1002 => "AMD",
      0x10DE => "NVIDIA",
      0x8086 => "Intel",
      _ => null
    };
  }

  [GeneratedRegex(
    @"^luid_0x(?<high>[0-9a-f]+)_0x(?<low>[0-9a-f]+)_phys_\d+$",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
  )]
  private static partial Regex GpuAdapterInstance();

  [StructLayout(
    LayoutKind.Sequential
  )]
  private struct PdhFormattedCounterValueItem
  {
    public IntPtr Name;
    public PdhFormattedCounterValue Value;
  }

  [StructLayout(
    LayoutKind.Explicit
  )]
  private struct PdhFormattedCounterValue
  {
    [FieldOffset(0)]
    public uint Status;

    [FieldOffset(8)]
    public long LargeValue;
  }

  [DllImport(
    "pdh.dll",
    CharSet = CharSet.Unicode,
    EntryPoint = "PdhOpenQueryW"
  )]
  private static extern uint PdhOpenQuery(
    string? dataSource,
    UIntPtr userData,
    out IntPtr query
  );

  [DllImport(
    "pdh.dll",
    CharSet = CharSet.Unicode,
    EntryPoint = "PdhAddEnglishCounterW"
  )]
  private static extern uint PdhAddEnglishCounter(
    IntPtr query,
    string fullCounterPath,
    UIntPtr userData,
    out IntPtr counter
  );

  [DllImport(
    "pdh.dll",
    EntryPoint = "PdhCollectQueryData"
  )]
  private static extern uint PdhCollectQueryData(
    IntPtr query
  );

  [DllImport(
    "pdh.dll",
    CharSet = CharSet.Unicode,
    EntryPoint = "PdhGetFormattedCounterArrayW"
  )]
  private static extern uint PdhGetFormattedCounterArray(
    IntPtr counter,
    uint format,
    ref uint bufferSize,
    ref uint itemCount,
    IntPtr itemBuffer
  );

  [DllImport(
    "pdh.dll",
    EntryPoint = "PdhCloseQuery"
  )]
  private static extern uint PdhCloseQuery(
    IntPtr query
  );
}

internal sealed record WindowsGraphicsAdapter(
  string Id,
  string Name,
  string? Manufacturer,
  long? TotalDedicatedMemoryBytes,
  long? UsedDedicatedMemoryBytes
);
