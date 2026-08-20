using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

  public async Task<DevicesResponse> DiscoverAsync(
    CancellationToken cancellationToken
  )
  {
    var auto = new GraphicsDevice(
      "auto",
      "Auto",
      null,
      null,
      true,
      true,
      null
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
      var nvidiaDevices = await DiscoverNvidiaDevicesAsync(
        cancellationToken
      );

      if (nvidiaDevices.Count > 0)
      {
        nvidiaDevices.Insert(
          0,
          auto
        );
        var visibility = Environment.GetEnvironmentVariable(
          "CUDA_VISIBLE_DEVICES"
        );
        var diagnostic = string.IsNullOrWhiteSpace(
          visibility
        )
          ? "CUDA device order and UUIDs were read from nvidia-smi."
          : "CUDA device order was read from nvidia-smi, but CUDA_VISIBLE_DEVICES is set for this process. The Ollama daemon must expose every selected GPU.";

        return new DevicesResponse(
          nvidiaDevices,
          diagnostic
        );
      }

      var devices = DiscoverWindowsDevices();
      devices.Insert(
        0,
        auto
      );

      return new DevicesResponse(
        devices,
        devices.Count == 1
          ? "Windows did not report a graphics device through SetupAPI."
          : "Windows reported graphics adapters, but no authoritative Ollama CUDA index was available. Exact affinity remains on Auto."
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

  private async Task<List<GraphicsDevice>> DiscoverNvidiaDevicesAsync(
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

      var output = process.StandardOutput.ReadToEndAsync(
        cancellationToken
      );
      var error = process.StandardError.ReadToEndAsync(
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
      await process.WaitForExitAsync(
        timeout.Token
      );
      var stdout = await output;
      var stderr = await error;

      if (process.ExitCode != 0)
      {
        _logger.LogDebug(
          "nvidia-smi GPU discovery returned exit code {ExitCode}: {Diagnostic}",
          process.ExitCode,
          stderr.Trim()
        );
        return [];
      }

      return ParseNvidiaDevices(
        stdout
      );
    }
    catch (Exception exception) when (
      exception is Win32Exception
      or InvalidOperationException
      or OperationCanceledException
    )
    {
      if (cancellationToken.IsCancellationRequested)
      {
        throw;
      }

      _logger.LogDebug(
        exception,
        "nvidia-smi GPU discovery was unavailable."
      );
      return [];
    }
  }

  private static List<GraphicsDevice> ParseNvidiaDevices(
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
        || string.IsNullOrWhiteSpace(
          fields[1]
        )
      )
      {
        continue;
      }

      long? memoryBytes = long.TryParse(
        fields[3],
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var memoryMib
      ) && memoryMib > 0
        ? memoryMib * 1_048_576L
        : null;
      devices.Add(
        new GraphicsDevice(
          fields[1],
          fields[2],
          "NVIDIA",
          memoryBytes,
          true,
          false,
          index
        )
      );
    }

    return devices
      .OrderBy(
        device => device.OllamaIndex
      )
      .ToList();
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
