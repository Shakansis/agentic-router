using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Devices;

public sealed class WindowsGpuDiscoveryService : IGpuDiscoveryService
{
  private const uint PresentDevices = 0x00000002;
  private const uint DeviceDescription = 0x00000000;
  private const uint Manufacturer = 0x0000000B;
  private const uint DeviceStarted = 0x00000008;
  private static readonly Guid DisplayDeviceClass = new(
    "4d36e968-e325-11ce-bfc1-08002be10318"
  );

  private readonly ILogger<WindowsGpuDiscoveryService> _logger;

  public WindowsGpuDiscoveryService(
    ILogger<WindowsGpuDiscoveryService> logger
  )
  {
    _logger = logger;
  }

  public DevicesResponse Discover()
  {
    var auto = new GraphicsDevice(
      "auto",
      "Auto",
      null,
      null,
      true,
      true
    );

    if (!OperatingSystem.IsWindows())
    {
      return new DevicesResponse(
        [auto],
        "GPU discovery is currently supported only on Windows."
      );
    }

    try
    {
      var devices = DiscoverWindowsDevices();
      devices.Insert(
        0,
        auto
      );

      return new DevicesResponse(
        devices,
        devices.Count == 1
          ? "Windows did not report a graphics device through SetupAPI."
          : null
      );
    }
    catch (Exception exception)
    {
      _logger.LogWarning(
        exception,
        "GPU discovery failed."
      );

      return new DevicesResponse(
        [auto],
        $"GPU discovery is unavailable: {exception.Message}"
      );
    }
  }

  private static List<GraphicsDevice> DiscoverWindowsDevices()
  {
    var classGuid = DisplayDeviceClass;
    var deviceSet = SetupDiGetClassDevs(
      ref classGuid,
      null,
      IntPtr.Zero,
      PresentDevices
    );

    if (deviceSet == new IntPtr(
      -1
    ))
    {
      throw new Win32Exception(
        Marshal.GetLastWin32Error()
      );
    }

    try
    {
      var devices = new List<GraphicsDevice>();

      for (uint index = 0; ; index++)
      {
        var deviceInfo = new DeviceInfoData
        {
          Size = (uint)Marshal.SizeOf<DeviceInfoData>()
        };

        if (!SetupDiEnumDeviceInfo(
          deviceSet,
          index,
          ref deviceInfo
        ))
        {
          var error = Marshal.GetLastWin32Error();

          if (error == 259)
          {
            break;
          }

          throw new Win32Exception(
            error
          );
        }

        var id = GetDeviceInstanceId(
          deviceSet,
          ref deviceInfo
        );
        var name = GetRegistryProperty(
          deviceSet,
          ref deviceInfo,
          DeviceDescription
        ) ?? "Unknown graphics device";
        var manufacturer = GetRegistryProperty(
          deviceSet,
          ref deviceInfo,
          Manufacturer
        );
        var available = GetAvailability(
          deviceInfo.DeviceInstance
        );
        name = NormalizeDisplayValue(
          name
        );
        manufacturer = NormalizeOptionalDisplayValue(
          manufacturer
        );

        devices.Add(
          new GraphicsDevice(
            id,
            name,
            manufacturer,
            null,
            available,
            false
          )
        );
      }

      return devices
        .GroupBy(
          device => string.IsNullOrWhiteSpace(
            device.Id
          )
            ? device.Name
            : device.Id,
          StringComparer.OrdinalIgnoreCase
        )
        .Select(
          group => group.First()
        )
        .GroupBy(
          device => device.Name,
          StringComparer.OrdinalIgnoreCase
        )
        .Select(
          group => group.First()
        )
        .OrderBy(
          device => device.Name,
          StringComparer.OrdinalIgnoreCase
        )
        .ToList();
    }
    finally
    {
      SetupDiDestroyDeviceInfoList(
        deviceSet
      );
    }
  }

  private static string GetDeviceInstanceId(
    IntPtr deviceSet,
    ref DeviceInfoData deviceInfo
  )
  {
    SetupDiGetDeviceInstanceId(
      deviceSet,
      ref deviceInfo,
      null,
      0,
      out var requiredSize
    );

    if (requiredSize == 0)
    {
      return $"windows-display-{deviceInfo.DeviceInstance}";
    }

    var buffer = new StringBuilder(
      (int)requiredSize
    );

    return SetupDiGetDeviceInstanceId(
      deviceSet,
      ref deviceInfo,
      buffer,
      requiredSize,
      out _
    )
      ? buffer.ToString()
      : $"windows-display-{deviceInfo.DeviceInstance}";
  }

  private static string? GetRegistryProperty(
    IntPtr deviceSet,
    ref DeviceInfoData deviceInfo,
    uint property
  )
  {
    var buffer = new byte[2_048];

    if (!SetupDiGetDeviceRegistryProperty(
      deviceSet,
      ref deviceInfo,
      property,
      out _,
      buffer,
      (uint)buffer.Length,
      out _
    ))
    {
      return null;
    }

    return Encoding.Unicode.GetString(
      buffer
    ).TrimEnd(
      '\0'
    );
  }

  private static bool GetAvailability(
    uint deviceInstance
  )
  {
    var result = CMGetDevNodeStatus(
      out var status,
      out var problem,
      deviceInstance,
      0
    );

    if (result != 0)
    {
      return false;
    }

    if (problem != 0)
    {
      return false;
    }

    return (status & DeviceStarted) != 0;
  }

  private static string NormalizeDisplayValue(
    string value
  )
  {
    var firstField = value.Split(
      ';',
      2,
      StringSplitOptions.TrimEntries
    )[0];
    var repaired = RepairMojibake(
      firstField
    );
    var normalized = repaired.Normalize(
      NormalizationForm.FormKC
    );
    var withoutControls = string.Concat(
      normalized.Where(
        character => !char.IsControl(
          character
        )
      )
    );
    var cleaned = Regex.Replace(
      withoutControls,
      @"\s+",
      " "
    ).Replace(
      "\uFFFD",
      string.Empty,
      StringComparison.Ordinal
    ).Trim();

    return string.IsNullOrWhiteSpace(
      cleaned
    )
      ? "Unknown graphics device"
      : cleaned;
  }

  private static string RepairMojibake(
    string value
  )
  {
    if (
      !value.Contains(
        'Ã',
        StringComparison.Ordinal
      )
      && !value.Contains(
        'Â',
        StringComparison.Ordinal
      )
      && !value.Contains(
        'â',
        StringComparison.Ordinal
      )
    )
    {
      return value;
    }

    try
    {
      return Encoding.UTF8.GetString(
        Encoding.Latin1.GetBytes(
          value
        )
      );
    }
    catch (EncoderFallbackException)
    {
      return value;
    }
  }

  private static string? NormalizeOptionalDisplayValue(
    string? value
  )
  {
    return string.IsNullOrWhiteSpace(
      value
    )
      ? null
      : NormalizeDisplayValue(
        value
      );
  }

  [StructLayout(
    LayoutKind.Sequential
  )]
  private struct DeviceInfoData
  {
    public uint Size;
    public Guid ClassGuid;
    public uint DeviceInstance;
    public IntPtr Reserved;
  }

  [DllImport(
    "setupapi.dll",
    CharSet = CharSet.Unicode,
    SetLastError = true
  )]
  private static extern IntPtr SetupDiGetClassDevs(
    ref Guid classGuid,
    string? enumerator,
    IntPtr parent,
    uint flags
  );

  [DllImport(
    "setupapi.dll",
    SetLastError = true
  )]
  [return: MarshalAs(
    UnmanagedType.Bool
  )]
  private static extern bool SetupDiEnumDeviceInfo(
    IntPtr deviceSet,
    uint memberIndex,
    ref DeviceInfoData deviceInfo
  );

  [DllImport(
    "setupapi.dll",
    CharSet = CharSet.Unicode,
    SetLastError = true
  )]
  [return: MarshalAs(
    UnmanagedType.Bool
  )]
  private static extern bool SetupDiGetDeviceInstanceId(
    IntPtr deviceSet,
    ref DeviceInfoData deviceInfo,
    StringBuilder? deviceInstanceId,
    uint deviceInstanceIdSize,
    out uint requiredSize
  );

  [DllImport(
    "setupapi.dll",
    CharSet = CharSet.Unicode,
    SetLastError = true
  )]
  [return: MarshalAs(
    UnmanagedType.Bool
  )]
  private static extern bool SetupDiGetDeviceRegistryProperty(
    IntPtr deviceSet,
    ref DeviceInfoData deviceInfo,
    uint property,
    out uint propertyRegDataType,
    byte[] propertyBuffer,
    uint propertyBufferSize,
    out uint requiredSize
  );

  [DllImport(
    "setupapi.dll",
    SetLastError = true
  )]
  [return: MarshalAs(
    UnmanagedType.Bool
  )]
  private static extern bool SetupDiDestroyDeviceInfoList(
    IntPtr deviceSet
  );

  [DllImport(
    "cfgmgr32.dll",
    EntryPoint = "CM_Get_DevNode_Status",
    SetLastError = true
  )]
  private static extern int CMGetDevNodeStatus(
    out uint status,
    out uint problemNumber,
    uint deviceInstance,
    uint flags
  );
}
