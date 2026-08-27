using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Setup;

internal static class OllamaInstallationProfiles
{
  public static IReadOnlyList<OllamaInstallationProfileOption> BuildOptions(
    DevicesResponse devices
  )
  {
    var physical = devices.Devices.Where(
      device => !device.IsAuto && device.Available
    ).ToArray();
    var hasAmd = HasVendor(
      physical,
      "AMD"
    );
    var hasNvidia = HasVendor(
      physical,
      "NVIDIA"
    );
    var hasIntel = HasVendor(
      physical,
      "Intel"
    );
    var options = new List<OllamaInstallationProfileOption>();

    if (hasNvidia || physical.Length == 0)
    {
      options.Add(
        new OllamaInstallationProfileOption(
          "standard",
          hasNvidia
            ? "Standard / CUDA"
            : "Standard / CPU",
          "Official base package. Ollama selects CPU or NVIDIA CUDA automatically; no backend override is added."
        )
      );
    }
    if (hasAmd || hasIntel || physical.Length == 0)
    {
      options.Add(
        new OllamaInstallationProfileOption(
          "vulkan",
          "Vulkan",
          "Official base package with OLLAMA_VULKAN=1. Broader AMD/Intel support, currently documented by Ollama as experimental."
        )
      );
    }
    if (hasAmd || physical.Length == 0)
    {
      options.Add(
        new OllamaInstallationProfileOption(
          "rocm",
          "ROCm",
          "Official base plus ROCm supplemental package. Intended for supported AMD GPUs with a compatible ROCm v7 driver."
        )
      );
    }
    if (options.Count == 0)
    {
      options.Add(
        new OllamaInstallationProfileOption(
          "standard",
          "Standard / CPU",
          "Official base package with automatic CPU fallback and no forced GPU backend."
        )
      );
    }

    return options;
  }

  public static bool HasVendor(
    IReadOnlyList<GraphicsDevice> devices,
    string vendor
  )
  {
    return devices.Any(
      device => string.Equals(
        device.Manufacturer,
        vendor,
        StringComparison.OrdinalIgnoreCase
      )
    );
  }
}
