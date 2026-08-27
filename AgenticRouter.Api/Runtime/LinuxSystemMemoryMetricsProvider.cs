using System.Globalization;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public sealed class LinuxSystemMemoryMetricsProvider : ISystemMemoryMetricsProvider
{
  private const long Kibibyte = 1024;

  public SystemMemoryStatus GetStatus()
  {
    if (!OperatingSystem.IsLinux())
    {
      return Unavailable(
        "Linux system RAM metrics are unavailable on this operating system."
      );
    }

    try
    {
      var values = File.ReadLines(
        "/proc/meminfo"
      ).Select(
        ParseLine
      ).Where(
        entry => entry is not null
      ).ToDictionary(
        entry => entry!.Value.Key,
        entry => entry!.Value.Value,
        StringComparer.Ordinal
      );

      if (!values.TryGetValue(
        "MemTotal",
        out var total
      ) || total <= 0)
      {
        return Unavailable(
          "/proc/meminfo did not report MemTotal."
        );
      }

      var available = values.GetValueOrDefault(
        "MemAvailable",
        values.GetValueOrDefault("MemFree")
          + values.GetValueOrDefault("Buffers")
          + values.GetValueOrDefault("Cached")
      );
      available = Math.Clamp(
        available,
        0,
        total
      );
      var used = total - available;

      return new SystemMemoryStatus(
        total,
        used,
        available,
        used * 100d / total,
        "available",
        null
      );
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or FormatException
        or OverflowException
    )
    {
      return Unavailable(
        $"Linux system RAM metrics are unavailable: {exception.Message}"
      );
    }
  }

  private static KeyValuePair<string, long>? ParseLine(
    string line
  )
  {
    var separator = line.IndexOf(
      ':'
    );
    if (separator <= 0)
    {
      return null;
    }

    var fields = line[(separator + 1)..].Split(
      ' ',
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    );
    if (
      fields.Length == 0
      || !long.TryParse(
        fields[0],
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var value
      )
      || value < 0
    )
    {
      return null;
    }

    var bytes = fields.Length > 1 && string.Equals(
      fields[1],
      "kB",
      StringComparison.Ordinal
    )
      ? checked(value * Kibibyte)
      : value;
    return new KeyValuePair<string, long>(
      line[..separator],
      bytes
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
}
