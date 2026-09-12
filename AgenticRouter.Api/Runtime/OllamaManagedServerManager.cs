using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Runtime;

public interface IOllamaManagedServerManager
{
  Task<OllamaEndpointResolution> ResolveAsync(
    Uri configuredEndpoint,
    string? selection,
    string defaultSelection,
    CancellationToken cancellationToken
  );

  Task<OllamaEndpointResolution> ResolveAsync(
    Uri configuredEndpoint,
    string? selection,
    string defaultSelection,
    int contextLength,
    CancellationToken cancellationToken
  );

  IReadOnlyList<OllamaManagedServerStatus> GetActiveServers();
}

public sealed record OllamaEndpointResolution(
  Uri Endpoint,
  int? MainGpu,
  bool Managed,
  string? Backend,
  int? BackendIndex
);

public sealed record OllamaManagedServerStatus(
  Uri Endpoint,
  string Selection,
  string Backend,
  int? BackendIndex,
  int ProcessId,
  DateTimeOffset StartedAt,
  int? ContextLength
);

public sealed class OllamaManagedServerManager :
  IOllamaManagedServerManager,
  IHostedService,
  IAsyncDisposable
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
  };
  private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
  private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

  private readonly string _leaseDirectory;
  private readonly IHttpClientFactory _httpClients;
  private readonly ILogger<OllamaManagedServerManager> _logger;
  private readonly string? _executableOverride;
  private readonly IGpuDiscoveryService _gpuDiscovery;
  private readonly int _portOffset;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly Dictionary<string, ManagedServer> _servers = new(
    StringComparer.Ordinal
  );
  private WindowsProcessJob? _job;
  private bool _disposed;

  public OllamaManagedServerManager(
    string dataDirectory,
    IHttpClientFactory httpClients,
    ILogger<OllamaManagedServerManager> logger,
    IGpuDiscoveryService gpuDiscovery,
    string? executableOverride = null,
    int portOffset = 0
  )
  {
    _leaseDirectory = Path.Combine(dataDirectory, "ollama-managed-servers");
    _httpClients = httpClients;
    _logger = logger;
    _gpuDiscovery = gpuDiscovery;
    _executableOverride = executableOverride;
    _portOffset = portOffset;
  }

  public async Task StartAsync(CancellationToken cancellationToken)
  {
    if (!OperatingSystem.IsWindows())
    {
      return;
    }

    Directory.CreateDirectory(_leaseDirectory);
    await CollectOrphansAsync(cancellationToken);
  }

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    await StopOwnedServersAsync(cancellationToken);
  }

  public async Task<OllamaEndpointResolution> ResolveAsync(
    Uri configuredEndpoint,
    string? selection,
    string defaultSelection,
    CancellationToken cancellationToken
  )
  {
    return await ResolveCoreAsync(
      configuredEndpoint,
      selection,
      defaultSelection,
      null,
      cancellationToken
    );
  }

  public async Task<OllamaEndpointResolution> ResolveAsync(
    Uri configuredEndpoint,
    string? selection,
    string defaultSelection,
    int contextLength,
    CancellationToken cancellationToken
  )
  {
    if (contextLength <= 0)
    {
      throw new ArgumentOutOfRangeException(
        nameof(contextLength),
        contextLength,
        "Managed Ollama context length must be positive."
      );
    }
    return await ResolveCoreAsync(
      configuredEndpoint,
      selection,
      defaultSelection,
      contextLength,
      cancellationToken
    );
  }

  private async Task<OllamaEndpointResolution> ResolveCoreAsync(
    Uri configuredEndpoint,
    string? selection,
    string defaultSelection,
    int? contextLength,
    CancellationToken cancellationToken
  )
  {
    var target = OllamaGpuSelection.ResolveTarget(selection, defaultSelection);
    if (target is null)
    {
      return new OllamaEndpointResolution(
        configuredEndpoint,
        null,
        false,
        null,
        null
      );
    }

    if (!ShouldManage(configuredEndpoint))
    {
      return new OllamaEndpointResolution(
        configuredEndpoint,
        target.AllDevices ? null : target.Index,
        false,
        target.Backend,
        target.AllDevices ? null : target.Index
      );
    }

    var server = await GetOrStartAsync(
      configuredEndpoint,
      target,
      contextLength,
      cancellationToken
    );
    return new OllamaEndpointResolution(
      server.Endpoint,
      ManagedMainGpu(target),
      true,
      target.Backend,
      target.AllDevices ? null : target.Index
    );
  }

  public IReadOnlyList<OllamaManagedServerStatus> GetActiveServers()
  {
    lock (_servers)
    {
      return _servers.Values.Where(server => !server.Process.HasExited).Select(
        server => new OllamaManagedServerStatus(
          server.Endpoint,
          server.Target.Selection,
          server.Target.Backend,
          server.Target.AllDevices ? null : server.Target.Index,
          server.Process.Id,
          server.StartedAt,
          server.ContextLength
        )
      ).ToArray();
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed)
    {
      return;
    }

    _disposed = true;
    await StopOwnedServersAsync(CancellationToken.None);
    _job?.Dispose();
    _gate.Dispose();
  }

  private async Task<ManagedServer> GetOrStartAsync(
    Uri configuredEndpoint,
    OllamaGpuTarget target,
    int? contextLength,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (_disposed)
      {
        throw ManagedFailure(
          "The managed Ollama server manager is shutting down.",
          "A GPU-specific endpoint was requested after shutdown started."
        );
      }

      ManagedServer[] replaced;
      lock (_servers)
      {
        if (
          _servers.TryGetValue(target.Selection, out var existing)
          && !existing.Process.HasExited
          && (contextLength is null || existing.ContextLength == contextLength)
        )
        {
          return existing;
        }
        replaced = _servers.Values.Where(
          server => !server.Process.HasExited
        ).ToArray();
        _servers.Clear();
      }

      foreach (var server in replaced)
      {
        await StopServerAsync(server, cancellationToken);
      }

      return await StartServerAsync(
        configuredEndpoint,
        target,
        contextLength,
        cancellationToken
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task<ManagedServer> StartServerAsync(
    Uri configuredEndpoint,
    OllamaGpuTarget target,
    int? contextLength,
    CancellationToken cancellationToken
  )
  {
    var executable = ResolveOllamaExecutable();
    var library = ResolveLibrary(executable, target.Backend);
    var port = configuredEndpoint.Port + _portOffset;
    string? vulkanOrder = null;

    if (target.PreferredDevice is not null)
    {
      var probe = await StartServerProcessAsync(
        target,
        executable,
        library,
        port,
        null,
        contextLength,
        cancellationToken
      );
      try
      {
        vulkanOrder = await ResolveVulkanOrderAsync(
          probe,
          target.PreferredDevice,
          cancellationToken
        );
      }
      finally
      {
        await StopServerAsync(probe, CancellationToken.None);
      }
    }

    return await StartServerProcessAsync(
      target,
      executable,
      library,
      port,
      vulkanOrder,
      contextLength,
      cancellationToken
    );
  }

  private async Task<ManagedServer> StartServerProcessAsync(
    OllamaGpuTarget target,
    string executable,
    string library,
    int port,
    string? vulkanOrder,
    int? contextLength,
    CancellationToken cancellationToken
  )
  {
    await TakeOverPortAsync(port, executable, cancellationToken);

    var endpoint = new Uri($"http://127.0.0.1:{port}", UriKind.Absolute);
    var startInfo = new ProcessStartInfo
    {
      FileName = executable,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("serve");
    startInfo.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
    startInfo.Environment["OLLAMA_LLM_LIBRARY"] = library;
    startInfo.Environment["OLLAMA_NO_CLOUD"] = "1";
    if (contextLength is not null)
    {
      startInfo.Environment["OLLAMA_CONTEXT_LENGTH"] = contextLength.Value.ToString(
        CultureInfo.InvariantCulture
      );
    }
    ApplyDeviceEnvironment(startInfo, target, vulkanOrder);

    var process = new Process
    {
      StartInfo = startInfo,
      EnableRaisingEvents = true
    };
    try
    {
      if (!process.Start())
      {
        throw new InvalidOperationException("Process.Start returned false.");
      }
      _job ??= WindowsProcessJob.Create();
      _job.Add(process);
    }
    catch (Exception exception) when (
      exception is Win32Exception or InvalidOperationException
    )
    {
      TryTerminate(process);
      process.Dispose();
      throw ManagedFailure(
        $"The Agentic Router-owned Ollama {BackendLabel(target.Backend)} server could not start.",
        exception.Message,
        exception
      );
    }

    var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
    var leasePath = LeasePath(target.Selection);
    var output = new ConcurrentQueue<string>();
    var server = new ManagedServer(
      target,
      endpoint,
      executable,
      process,
      startedAt,
      contextLength,
      leasePath,
      output,
      DrainAsync(process.StandardOutput, target.Selection, output),
      DrainAsync(process.StandardError, target.Selection, output)
    );
    process.Exited += (_, _) => HandleExit(server);

    try
    {
      await WriteLeaseAsync(server, cancellationToken);
      await WaitUntilHealthyAsync(server, cancellationToken);
      lock (_servers)
      {
        _servers[target.Selection] = server;
      }
      _logger.LogInformation(
        "Started Agentic Router-owned Ollama server {Selection} on {Endpoint} with PID {ProcessId}.",
        target.Selection,
        endpoint,
        process.Id
      );
      return server;
    }
    catch
    {
      await StopServerAsync(server, CancellationToken.None);
      throw;
    }
  }

  private async Task<string> ResolveVulkanOrderAsync(
    ManagedServer probe,
    OllamaGpuPreference preference,
    CancellationToken cancellationToken
  )
  {
    var devices = await _gpuDiscovery.DiscoverAsync(cancellationToken);
    var preferred = devices.Devices.SingleOrDefault(
      device => device.Available
        && !device.IsAuto
        && device.AffinitySelectable
        && string.Equals(
          device.Backend,
          preference.Backend,
          StringComparison.Ordinal
        )
        && device.BackendIndex == preference.Index
    ) ?? throw ManagedFailure(
      "The preferred Vulkan GPU is no longer available.",
      $"No selectable {BackendLabel(preference.Backend)} device with index {preference.Index} was discovered."
    );
    var vulkanDevices = ParseVulkanDevices(probe.Output);
    if (vulkanDevices.Count < 2)
    {
      throw ManagedFailure(
        "The combined Vulkan server did not discover multiple GPUs.",
        $"Only {vulkanDevices.Count} distinct Vulkan device(s) were identified in the managed server evidence."
      );
    }
    var matches = vulkanDevices.Where(
      device => SameDeviceName(device.Name, preferred.Name)
    ).ToArray();
    if (matches.Length != 1)
    {
      throw ManagedFailure(
        "The preferred GPU could not be mapped unambiguously to a Vulkan device.",
        $"Physical device '{preferred.Name}' matched {matches.Length} Vulkan discovery entries."
      );
    }

    return string.Join(
      ',',
      new[] { matches[0] }.Concat(
        vulkanDevices.Where(device => device.Id != matches[0].Id)
      ).Select(device => device.Id)
    );
  }

  private static IReadOnlyList<VulkanDevice> ParseVulkanDevices(
    IEnumerable<string> lines
  )
  {
    var devices = new List<VulkanDevice>();
    foreach (var line in lines.Where(
      line => line.Contains("library=Vulkan", StringComparison.OrdinalIgnoreCase)
    ))
    {
      var id = ReadLogValue(line, "id");
      var name = ReadLogValue(line, "description");
      if (
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
        && index >= 0
        && !string.IsNullOrWhiteSpace(name)
      )
      {
        devices.Add(new VulkanDevice(index, name));
      }
    }
    return devices.DistinctBy(device => device.Id).OrderBy(
      device => device.Id
    ).ToArray();
  }

  private static string? ReadLogValue(string line, string key)
  {
    var marker = key + "=";
    var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
      return null;
    }
    start += marker.Length;
    if (start < line.Length && line[start] == '"')
    {
      start++;
      var endQuote = line.IndexOf('"', start);
      return endQuote < 0 ? null : line[start..endQuote];
    }
    var end = line.IndexOf(' ', start);
    return line[start..(end < 0 ? line.Length : end)];
  }

  private static bool SameDeviceName(string left, string right)
  {
    var normalizedLeft = NormalizeDeviceName(left);
    var normalizedRight = NormalizeDeviceName(right);
    return normalizedLeft.Length >= 8
      && normalizedRight.Length >= 8
      && (
        normalizedLeft == normalizedRight
        || normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
        || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal)
      );
  }

  private static string NormalizeDeviceName(string value)
  {
    return string.Concat(
      value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)
    );
  }

  private async Task WaitUntilHealthyAsync(
    ManagedServer server,
    CancellationToken cancellationToken
  )
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken
    );
    timeout.CancelAfter(StartupTimeout);
    var client = _httpClients.CreateClient();

    try
    {
      while (!timeout.IsCancellationRequested)
      {
        if (server.Process.HasExited)
        {
          throw ManagedFailure(
            $"The Agentic Router-owned Ollama {BackendLabel(server.Target.Backend)} server exited during startup.",
            $"PID {server.Process.Id} exited with code {server.Process.ExitCode}."
          );
        }

        try
        {
          using var response = await client.GetAsync(
            new Uri(server.Endpoint, "/api/version"),
            timeout.Token
          );
          if (
            response.IsSuccessStatusCode
            && HasBackendEvidence(server)
          )
          {
            return;
          }
        }
        catch (Exception exception) when (
          exception is HttpRequestException or TaskCanceledException
        )
        {
          if (cancellationToken.IsCancellationRequested)
          {
            throw;
          }
        }

        await Task.Delay(100, timeout.Token);
      }
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
    }

    throw ManagedFailure(
      $"The Agentic Router-owned Ollama {BackendLabel(server.Target.Backend)} server did not become ready.",
      $"A healthy /api/version response and {BackendLabel(server.Target.Backend)} discovery evidence were not both observed at {server.Endpoint} within {StartupTimeout.TotalSeconds:0} seconds."
    );
  }

  private static bool HasBackendEvidence(ManagedServer server)
  {
    var marker = $"library={BackendLabel(server.Target.Backend)}";
    var matches = server.Output.Count(
      line => line.Contains(marker, StringComparison.OrdinalIgnoreCase)
    );
    return matches >= (server.Target.AllDevices ? 2 : 1);
  }

  private async Task StopOwnedServersAsync(CancellationToken cancellationToken)
  {
    ManagedServer[] servers;
    lock (_servers)
    {
      servers = _servers.Values.ToArray();
      _servers.Clear();
    }

    foreach (var server in servers)
    {
      await StopServerAsync(server, cancellationToken);
    }
  }

  private async Task StopServerAsync(
    ManagedServer server,
    CancellationToken cancellationToken
  )
  {
    try
    {
      if (!server.Process.HasExited)
      {
        server.Process.Kill(entireProcessTree: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
          cancellationToken
        );
        timeout.CancelAfter(ShutdownTimeout);
        await server.Process.WaitForExitAsync(timeout.Token);
      }
    }
    catch (Exception exception) when (
      exception is InvalidOperationException or Win32Exception or OperationCanceledException
    )
    {
      _logger.LogWarning(
        exception,
        "Agentic Router-owned Ollama server {Selection} did not stop cleanly; closing the Job Object remains the final containment boundary.",
        server.Target.Selection
      );
    }
    finally
    {
      TryDeleteLease(server.LeasePath);
      try
      {
        await Task.WhenAll(server.StandardOutput, server.StandardError).WaitAsync(
          TimeSpan.FromSeconds(1),
          CancellationToken.None
        );
      }
      catch (Exception exception) when (
        exception is TimeoutException or IOException or ObjectDisposedException
      )
      {
        _logger.LogDebug(
          exception,
          "Managed Ollama output readers for {Selection} did not close cleanly.",
          server.Target.Selection
        );
      }
      server.Process.Dispose();
    }
  }

  private async Task CollectOrphansAsync(CancellationToken cancellationToken)
  {
    foreach (var path in Directory.EnumerateFiles(
      _leaseDirectory,
      "*.json",
      SearchOption.TopDirectoryOnly
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      ManagedServerLease? lease = null;
      try
      {
        lease = JsonSerializer.Deserialize<ManagedServerLease>(
          await File.ReadAllTextAsync(path, cancellationToken),
          JsonOptions
        );
      }
      catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or JsonException
      )
      {
        _logger.LogWarning(
          exception,
          "Ignoring an unreadable managed Ollama lease {LeasePath}.",
          path
        );
      }

      if (lease is not null)
      {
        TryCollectOrphan(lease);
      }
      TryDeleteLease(path);
    }
  }

  private void TryCollectOrphan(ManagedServerLease lease)
  {
    Process? process = null;
    try
    {
      process = Process.GetProcessById(lease.ProcessId);
      if (process.HasExited)
      {
        return;
      }
      var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime());
      var executable = process.MainModule?.FileName;
      var endpoint = new Uri(lease.Endpoint, UriKind.Absolute);
      var identityMatches = Math.Abs(
        (startedAt - lease.StartedAt).TotalSeconds
      ) < 1
        && !string.IsNullOrWhiteSpace(executable)
        && string.Equals(
          Path.GetFullPath(executable),
          Path.GetFullPath(lease.Executable),
          StringComparison.OrdinalIgnoreCase
        )
        && WindowsTcpOwner.TryGetOwnerProcessId(endpoint.Port, out var owner)
        && owner == lease.ProcessId;
      if (!identityMatches)
      {
        _logger.LogWarning(
          "A stale managed Ollama lease for PID {ProcessId} did not match executable, start time, and port ownership; the process was not touched.",
          lease.ProcessId
        );
        return;
      }

      process.Kill(entireProcessTree: true);
      process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds);
      _logger.LogInformation(
        "Collected orphaned Agentic Router-owned Ollama process {ProcessId} from {Endpoint}.",
        lease.ProcessId,
        lease.Endpoint
      );
    }
    catch (Exception exception) when (
      exception is ArgumentException
        or InvalidOperationException
        or Win32Exception
        or NotSupportedException
        or UriFormatException
    )
    {
      _logger.LogDebug(
        exception,
        "A managed Ollama lease no longer identified a collectible process."
      );
    }
    finally
    {
      process?.Dispose();
    }
  }

  private async Task WriteLeaseAsync(
    ManagedServer server,
    CancellationToken cancellationToken
  )
  {
    Directory.CreateDirectory(_leaseDirectory);
    var temporary = server.LeasePath + $".{Guid.NewGuid():N}.tmp";
    try
    {
      await File.WriteAllTextAsync(
        temporary,
        JsonSerializer.Serialize(
          new ManagedServerLease(
            1,
            server.Target.Selection,
            server.Target.Backend,
            server.Target.AllDevices ? null : server.Target.Index,
            server.Process.Id,
            server.StartedAt,
            server.Executable,
            server.Endpoint.AbsoluteUri
          ),
          JsonOptions
        ) + "\n",
        cancellationToken
      );
      File.Move(temporary, server.LeasePath, true);
    }
    finally
    {
      if (File.Exists(temporary))
      {
        File.Delete(temporary);
      }
    }
  }

  private async Task DrainAsync(
    StreamReader reader,
    string selection,
    ConcurrentQueue<string> output
  )
  {
    try
    {
      while (await reader.ReadLineAsync() is { } line)
      {
        output.Enqueue(line);
        while (output.Count > 256)
        {
          output.TryDequeue(out _);
        }
        _logger.LogDebug(
          "Managed Ollama {Selection}: {Output}",
          selection,
          line
        );
      }
    }
    catch (Exception exception) when (
      exception is IOException or ObjectDisposedException
    )
    {
      _logger.LogDebug(
        exception,
        "Managed Ollama output stream {Selection} closed.",
        selection
      );
    }
  }

  private void HandleExit(ManagedServer server)
  {
    lock (_servers)
    {
      if (
        _servers.TryGetValue(server.Target.Selection, out var current)
        && ReferenceEquals(current, server)
      )
      {
        _servers.Remove(server.Target.Selection);
      }
    }
    TryDeleteLease(server.LeasePath);
  }

  private static bool ShouldManage(Uri endpoint)
  {
    return OperatingSystem.IsWindows()
      && endpoint.IsLoopback
      && endpoint.Port == 11_434;
  }

  private static int? ManagedMainGpu(OllamaGpuTarget target)
  {
    return target.AllDevices ? null : 0;
  }

  private async Task TakeOverPortAsync(
    int port,
    string expectedExecutable,
    CancellationToken cancellationToken
  )
  {
    var listeners = IPGlobalProperties.GetIPGlobalProperties()
      .GetActiveTcpListeners();
    if (!listeners.Any(listener => listener.Port == port))
    {
      return;
    }
    if (!WindowsTcpOwner.TryGetOwnerProcessId(port, out var processId))
    {
      throw ManagedFailure(
        "The configured Ollama port is already in use.",
        $"Loopback port {port} is occupied, but its process identity could not be verified."
      );
    }
    using var process = Process.GetProcessById(processId);
    var actualExecutable = process.MainModule?.FileName;
    if (
      string.IsNullOrWhiteSpace(actualExecutable)
      || !string.Equals(
        Path.GetFullPath(actualExecutable),
        Path.GetFullPath(expectedExecutable),
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      throw ManagedFailure(
        "The configured Ollama port belongs to another process.",
        $"Loopback port {port} belongs to PID {processId}; Agentic Router refused to stop an executable other than '{expectedExecutable}'."
      );
    }

    _logger.LogInformation(
      "Stopping verified Ollama PID {ProcessId} on configured port {Port} before applying managed GPU configuration.",
      processId,
      port
    );
    process.Kill(entireProcessTree: true);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken
    );
    timeout.CancelAfter(ShutdownTimeout);
    await process.WaitForExitAsync(timeout.Token);
  }

  private string ResolveOllamaExecutable()
  {
    if (!string.IsNullOrWhiteSpace(_executableOverride))
    {
      var configured = Path.GetFullPath(_executableOverride);
      return File.Exists(configured)
        ? configured
        : throw ManagedFailure(
          "The configured managed Ollama executable does not exist.",
          $"Executable '{configured}' was not found."
        );
    }
    var candidates = new List<string>();
    var localAppData = Environment.GetFolderPath(
      Environment.SpecialFolder.LocalApplicationData
    );
    if (!string.IsNullOrWhiteSpace(localAppData))
    {
      candidates.Add(Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"));
    }
    candidates.AddRange(
      (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(
        Path.PathSeparator,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
      ).Select(directory => Path.Combine(directory, "ollama.exe"))
    );

    var executable = candidates.FirstOrDefault(File.Exists);
    return executable is null
      ? throw ManagedFailure(
        "The Agentic Router-owned Ollama server cannot start because ollama.exe was not found.",
        "Install Ollama for the current Windows user or add its directory to PATH."
      )
      : Path.GetFullPath(executable);
  }

  private static string ResolveLibrary(string executable, string backend)
  {
    var root = Path.Combine(Path.GetDirectoryName(executable)!, "lib", "ollama");
    var prefix = backend switch
    {
      "cuda" => "cuda_v",
      "rocm" => "rocm_v",
      "vulkan" => "vulkan",
      _ => backend
    };
    var candidates = Directory.Exists(root)
      ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(
        name => name is not null && (
          string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase)
          || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        )
      ).OrderByDescending(LibraryVersion).ThenByDescending(
        name => name,
        StringComparer.OrdinalIgnoreCase
      ).ToArray()
      : [];
    return candidates.FirstOrDefault() ?? throw ManagedFailure(
      $"The installed Ollama package does not contain the {BackendLabel(backend)} backend.",
      $"No '{prefix}' library directory was found under '{root}'."
    );
  }

  private static Version LibraryVersion(string? name)
  {
    var marker = name?.IndexOf("_v", StringComparison.OrdinalIgnoreCase) ?? -1;
    return marker >= 0
      && Version.TryParse(
        name![(marker + 2)..].Replace('_', '.'),
        out var version
      )
        ? version
        : new Version(0, 0);
  }

  private static void ApplyDeviceEnvironment(
    ProcessStartInfo startInfo,
    OllamaGpuTarget target,
    string? vulkanOrder
  )
  {
    if (target.Backend == "cuda")
    {
      startInfo.Environment["CUDA_VISIBLE_DEVICES"] = target.Index.ToString(
        CultureInfo.InvariantCulture
      );
      startInfo.Environment["HIP_VISIBLE_DEVICES"] = "-1";
      startInfo.Environment["ROCR_VISIBLE_DEVICES"] = "-1";
      startInfo.Environment["OLLAMA_VULKAN"] = "0";
      return;
    }
    if (target.Backend == "rocm")
    {
      var index = target.Index.ToString(CultureInfo.InvariantCulture);
      startInfo.Environment["HIP_VISIBLE_DEVICES"] = index;
      startInfo.Environment["ROCR_VISIBLE_DEVICES"] = index;
      startInfo.Environment["GPU_DEVICE_ORDINAL"] = index;
      startInfo.Environment["CUDA_VISIBLE_DEVICES"] = "-1";
      startInfo.Environment["OLLAMA_VULKAN"] = "0";
      return;
    }

    startInfo.Environment["OLLAMA_VULKAN"] = "1";
    startInfo.Environment["OLLAMA_SCHED_SPREAD"] = target.AllDevices
      ? "1"
      : "0";
    if (string.IsNullOrWhiteSpace(vulkanOrder))
    {
      startInfo.Environment.Remove("GGML_VK_VISIBLE_DEVICES");
    }
    else
    {
      startInfo.Environment["GGML_VK_VISIBLE_DEVICES"] = vulkanOrder;
    }
  }

  private string LeasePath(string selection)
  {
    var safeName = selection.Replace(':', '-');
    return Path.Combine(_leaseDirectory, $"{safeName}.json");
  }

  private static void TryTerminate(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
    catch (Exception exception) when (
      exception is InvalidOperationException or Win32Exception
    )
    {
    }
  }

  private void TryDeleteLease(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch (Exception exception) when (
      exception is IOException or UnauthorizedAccessException
    )
    {
      _logger.LogWarning(exception, "Could not remove managed Ollama lease {LeasePath}.", path);
    }
  }

  private static string BackendLabel(string backend)
  {
    return backend switch
    {
      "cuda" => "CUDA",
      "rocm" => "ROCm",
      "vulkan" => "Vulkan",
      _ => backend
    };
  }

  private static OllamaProviderException ManagedFailure(
    string message,
    string technicalMessage,
    Exception? innerException = null
  )
  {
    return new OllamaProviderException(
      "managed-ollama-start",
      message,
      technicalMessage,
      503,
      true,
      innerException
    );
  }

  private sealed record ManagedServer(
    OllamaGpuTarget Target,
    Uri Endpoint,
    string Executable,
    Process Process,
    DateTimeOffset StartedAt,
    int? ContextLength,
    string LeasePath,
    ConcurrentQueue<string> Output,
    Task StandardOutput,
    Task StandardError
  );

  private sealed record ManagedServerLease(
    int SchemaVersion,
    string Selection,
    string Backend,
    int? BackendIndex,
    int ProcessId,
    DateTimeOffset StartedAt,
    string Executable,
    string Endpoint
  );

  private sealed record VulkanDevice(int Id, string Name);
}

internal sealed class WindowsProcessJob : IDisposable
{
  private const uint KillOnJobClose = 0x00002000;
  private readonly IntPtr _handle;

  private WindowsProcessJob(IntPtr handle)
  {
    _handle = handle;
  }

  public static WindowsProcessJob Create()
  {
    var handle = CreateJobObject(IntPtr.Zero, null);
    if (handle == IntPtr.Zero)
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    var information = new JobObjectExtendedLimitInformation
    {
      BasicLimitInformation = new JobObjectBasicLimitInformation
      {
        LimitFlags = KillOnJobClose
      }
    };
    var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
    var pointer = Marshal.AllocHGlobal(size);
    try
    {
      Marshal.StructureToPtr(information, pointer, false);
      if (!SetInformationJobObject(handle, 9, pointer, (uint)size))
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }
    }
    catch
    {
      CloseHandle(handle);
      throw;
    }
    finally
    {
      Marshal.FreeHGlobal(pointer);
    }
    return new WindowsProcessJob(handle);
  }

  public void Add(Process process)
  {
    if (!AssignProcessToJobObject(_handle, process.Handle))
    {
      throw new Win32Exception(Marshal.GetLastWin32Error());
    }
  }

  public void Dispose()
  {
    CloseHandle(_handle);
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetInformationJobObject(
    IntPtr job,
    int informationClass,
    IntPtr information,
    uint informationLength
  );

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

  [DllImport("kernel32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool CloseHandle(IntPtr handle);

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectBasicLimitInformation
  {
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize;
    public UIntPtr MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct IoCounters
  {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct JobObjectExtendedLimitInformation
  {
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public UIntPtr ProcessMemoryLimit;
    public UIntPtr JobMemoryLimit;
    public UIntPtr PeakProcessMemoryUsed;
    public UIntPtr PeakJobMemoryUsed;
  }
}

internal static class WindowsTcpOwner
{
  private const int InsufficientBuffer = 122;

  public static bool TryGetOwnerProcessId(int port, out int processId)
  {
    processId = 0;
    var size = 0;
    var result = GetExtendedTcpTable(
      IntPtr.Zero,
      ref size,
      false,
      2,
      5,
      0
    );
    if (result != InsufficientBuffer || size <= 0)
    {
      return false;
    }

    var buffer = Marshal.AllocHGlobal(size);
    try
    {
      result = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
      if (result != 0)
      {
        return false;
      }
      var count = Marshal.ReadInt32(buffer);
      var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
      var rowPointer = IntPtr.Add(buffer, sizeof(int));
      for (var index = 0; index < count; index++)
      {
        var row = Marshal.PtrToStructure<TcpRowOwnerPid>(
          IntPtr.Add(rowPointer, index * rowSize)
        );
        var localPort = (ushort)IPAddress.NetworkToHostOrder(
          (short)row.LocalPort
        );
        if (localPort == port && row.State == 2)
        {
          processId = checked((int)row.OwningPid);
          return true;
        }
      }
      return false;
    }
    finally
    {
      Marshal.FreeHGlobal(buffer);
    }
  }

  [DllImport("iphlpapi.dll", SetLastError = true)]
  private static extern int GetExtendedTcpTable(
    IntPtr tcpTable,
    ref int size,
    [MarshalAs(UnmanagedType.Bool)] bool order,
    int addressFamily,
    int tableClass,
    uint reserved
  );

  [StructLayout(LayoutKind.Sequential)]
  private struct TcpRowOwnerPid
  {
    public uint State;
    public uint LocalAddress;
    public uint LocalPort;
    public uint RemoteAddress;
    public uint RemotePort;
    public uint OwningPid;
  }
}
