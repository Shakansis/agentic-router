using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Runtime;

namespace AgenticRouter.Api.Platform;

internal static class PlatformServiceCollectionExtensions
{
  public static IServiceCollection AddHostPlatformServices(
    this IServiceCollection services,
    string dataDirectory
  )
  {
    if (OperatingSystem.IsWindows())
    {
      services.AddSingleton<IProtectedSecretStore>(
        new DpapiProtectedSecretStore(
          dataDirectory
        )
      );
      services.AddSingleton<IGpuDiscoveryService, WindowsGpuDiscoveryService>();
      services.AddSingleton<IFolderPickerService, WindowsFolderPickerService>();
      services.AddSingleton<IFolderLauncherService, WindowsFolderLauncherService>();
      services.AddSingleton<ISystemMemoryMetricsProvider, WindowsSystemMemoryMetricsProvider>();
      services.AddSingleton<IGpuMemoryMetricsProvider, WindowsGpuMemoryMetricsProvider>();
      return services;
    }

    if (OperatingSystem.IsLinux())
    {
      services.AddSingleton<IProtectedSecretStore, LinuxSecretServiceStore>();
      services.AddSingleton<IGpuDiscoveryService, LinuxGpuDiscoveryService>();
      services.AddSingleton<IFolderPickerService, LinuxFolderPickerService>();
      services.AddSingleton<IFolderLauncherService, LinuxFolderLauncherService>();
      services.AddSingleton<ISystemMemoryMetricsProvider, LinuxSystemMemoryMetricsProvider>();
      services.AddSingleton<IGpuMemoryMetricsProvider, LinuxGpuMemoryMetricsProvider>();
      return services;
    }

    throw new PlatformNotSupportedException(
      "Agentic Router currently supports Windows and Linux x64."
    );
  }
}
