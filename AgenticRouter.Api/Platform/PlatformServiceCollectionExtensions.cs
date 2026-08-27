using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Setup;

namespace AgenticRouter.Api.Platform;

internal static class PlatformServiceCollectionExtensions
{
  public static IServiceCollection AddHostPlatformServices(
    this IServiceCollection services,
    string dataDirectory
  )
  {
    services.AddSingleton<IOllamaInstallationProfileStore>(
      new OllamaInstallationProfileStore(
        dataDirectory
      )
    );

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
      services.AddSingleton<ISetupInstallerLauncher, WindowsSetupInstallerLauncher>();
      services.AddSingleton<IOllamaBackendEvidenceService, NoOpOllamaBackendEvidenceService>();
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
      services.AddSingleton<ISetupInstallerLauncher>(
        provider => new LinuxSetupInstallerLauncher(
          dataDirectory,
          provider.GetRequiredService<ILogger<LinuxSetupInstallerLauncher>>()
        )
      );
      services.AddSingleton<IOllamaBackendEvidenceService>(
        new LinuxOllamaBackendEvidenceService(
          dataDirectory
        )
      );
      return services;
    }

    throw new PlatformNotSupportedException(
      "Agentic Router currently supports Windows and Linux x64."
    );
  }
}
