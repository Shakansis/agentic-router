using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class ManagedOllamaServerEndToEndTests
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OwnsBackendEnvironmentAndCollectsVerifiedOrphan()
  {
    if (!OperatingSystem.IsWindows())
    {
      Assert.Inconclusive("Windows Job Object and TCP-owner validation are Windows-only.");
    }

    var temporaryRoot = Path.Combine(
      Path.GetTempPath(),
      "agentic-router-managed-ollama-e2e",
      Guid.NewGuid().ToString("N")
    );
    Directory.CreateDirectory(temporaryRoot);
    var executable = CopyFakeOllama(temporaryRoot);
    var dataDirectory = Path.Combine(temporaryRoot, "data");
    var httpClients = new TestHttpClientFactory();
    OllamaManagedServerManager? first = null;
    OllamaManagedServerManager? second = null;
    try
    {
      first = CreateManager(dataDirectory, httpClients, executable);
      await first.StartAsync(CancellationToken.None);
      var cuda = await first.ResolveAsync(
        new Uri("http://127.0.0.1:11434"),
        "ollama:0",
        "ollama:0",
        CancellationToken.None
      );
      Assert.IsTrue(cuda.Managed);
      Assert.AreEqual(12_500, cuda.Endpoint.Port);
      Assert.AreEqual(0, cuda.MainGpu);
      var cudaEnvironment = await new HttpClient().GetFromJsonAsync<JsonElement>(
        new Uri(cuda.Endpoint, "/test/environment")
      );
      Assert.AreEqual("cuda_v13", cudaEnvironment.GetProperty("library").GetString());
      Assert.AreEqual("0", cudaEnvironment.GetProperty("cudaVisibleDevices").GetString());
      Assert.AreEqual("1", cudaEnvironment.GetProperty("noCloud").GetString());
      var firstPid = cudaEnvironment.GetProperty("processId").GetInt32();
      Assert.HasCount(1, Directory.GetFiles(
        Path.Combine(dataDirectory, "ollama-managed-servers"),
        "*.json"
      ));

      second = CreateManager(dataDirectory, httpClients, executable);
      await second.StartAsync(CancellationToken.None);
      await AssertProcessExitedAsync(firstPid);
      Assert.IsEmpty(Directory.GetFiles(
        Path.Combine(dataDirectory, "ollama-managed-servers"),
        "*.json"
      ));

      var rocm = await second.ResolveAsync(
        new Uri("http://localhost:11434"),
        "rocm:0",
        "rocm:0",
        CancellationToken.None
      );
      var rocmEnvironment = await new HttpClient().GetFromJsonAsync<JsonElement>(
        new Uri(rocm.Endpoint, "/test/environment")
      );
      Assert.AreEqual("rocm_v7_1", rocmEnvironment.GetProperty("library").GetString());
      Assert.AreEqual("0", rocmEnvironment.GetProperty("hipVisibleDevices").GetString());
      Assert.AreEqual("0", rocmEnvironment.GetProperty("rocrVisibleDevices").GetString());

      var vulkan = await second.ResolveAsync(
        new Uri("http://localhost:11434"),
        "vulkan:all",
        "vulkan:all",
        CancellationToken.None
      );
      var vulkanEnvironment = await new HttpClient().GetFromJsonAsync<JsonElement>(
        new Uri(vulkan.Endpoint, "/test/environment")
      );
      Assert.AreEqual("vulkan", vulkanEnvironment.GetProperty("library").GetString());
      Assert.AreEqual("1", vulkanEnvironment.GetProperty("vulkan").GetString());
      Assert.AreEqual("1", vulkanEnvironment.GetProperty("spread").GetString());

      var preferNvidia = await second.ResolveAsync(
        new Uri("http://localhost:11434"),
        "vulkan:prefer:cuda:0",
        "vulkan:prefer:cuda:0",
        CancellationToken.None
      );
      Assert.AreEqual(0, preferNvidia.MainGpu);
      var nvidiaEnvironment = await new HttpClient().GetFromJsonAsync<JsonElement>(
        new Uri(preferNvidia.Endpoint, "/test/environment")
      );
      Assert.AreEqual(
        "0,1",
        nvidiaEnvironment.GetProperty("vulkanVisibleDevices").GetString()
      );
      Assert.AreEqual("0", nvidiaEnvironment.GetProperty("spread").GetString());

      var preferAmd = await second.ResolveAsync(
        new Uri("http://localhost:11434"),
        "vulkan:prefer:rocm:0",
        "vulkan:prefer:rocm:0",
        CancellationToken.None
      );
      var amdEnvironment = await new HttpClient().GetFromJsonAsync<JsonElement>(
        new Uri(preferAmd.Endpoint, "/test/environment")
      );
      Assert.AreEqual(
        "1,0",
        amdEnvironment.GetProperty("vulkanVisibleDevices").GetString()
      );
      Assert.AreEqual("0", amdEnvironment.GetProperty("spread").GetString());
      Assert.HasCount(4, second.GetActiveServers());
    }
    finally
    {
      if (second is not null)
      {
        await second.DisposeAsync();
      }
      if (first is not null)
      {
        await first.DisposeAsync();
      }
      if (Directory.Exists(temporaryRoot))
      {
        Directory.Delete(temporaryRoot, recursive: true);
      }
    }
  }

  private static OllamaManagedServerManager CreateManager(
    string dataDirectory,
    IHttpClientFactory httpClients,
    string executable
  )
  {
    return new OllamaManagedServerManager(
      dataDirectory,
      httpClients,
      NullLogger<OllamaManagedServerManager>.Instance,
      new FakeGpuDiscoveryService(),
      executable,
      portOffset: 1_000
    );
  }

  private static string CopyFakeOllama(string temporaryRoot)
  {
    var repositoryRoot = FindRepositoryRoot();
    var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
      ?? "Debug";
    var source = Path.Combine(
      repositoryRoot,
      "tests",
      "FakeOllamaCli",
      "bin",
      configuration,
      "net10.0"
    );
    foreach (var path in Directory.EnumerateFiles(source, "FakeOllamaCli*"))
    {
      File.Copy(path, Path.Combine(temporaryRoot, Path.GetFileName(path)));
    }
    var libraryRoot = Path.Combine(temporaryRoot, "lib", "ollama");
    Directory.CreateDirectory(Path.Combine(libraryRoot, "cuda_v13"));
    Directory.CreateDirectory(Path.Combine(libraryRoot, "rocm_v7_1"));
    Directory.CreateDirectory(Path.Combine(libraryRoot, "vulkan"));
    return Path.Combine(temporaryRoot, "FakeOllamaCli.exe");
  }

  private static async Task AssertProcessExitedAsync(int processId)
  {
    for (var attempt = 0; attempt < 50; attempt++)
    {
      try
      {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
          return;
        }
      }
      catch (ArgumentException)
      {
        return;
      }
      await Task.Delay(100);
    }
    Assert.Fail($"Managed Ollama process {processId} remained alive after orphan GC.");
  }

  private static string FindRepositoryRoot()
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
      if (File.Exists(Path.Combine(current.FullName, "AgenticRouter.slnx")))
      {
        return current.FullName;
      }
      current = current.Parent;
    }
    throw new DirectoryNotFoundException("Repository root was not found.");
  }

  private sealed class TestHttpClientFactory : IHttpClientFactory
  {
    public HttpClient CreateClient(string name)
    {
      return new HttpClient
      {
        Timeout = Timeout.InfiniteTimeSpan
      };
    }
  }

  private sealed class FakeGpuDiscoveryService : IGpuDiscoveryService
  {
    public Task<DevicesResponse> DiscoverAsync(CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(
        new DevicesResponse(
          [
            new GraphicsDevice(
              "auto",
              "Auto",
              null,
              null,
              true,
              true,
              AffinitySelectable: true
            ),
            new GraphicsDevice(
              "GPU-nvidia-fixture",
              "NVIDIA GeForce RTX 4090",
              "NVIDIA",
              24L * 1024 * 1024 * 1024,
              true,
              false,
              0,
              "cuda",
              0,
              true
            ),
            new GraphicsDevice(
              "dxgi-amd-fixture",
              "AMD Radeon RX 7900 XTX",
              "AMD",
              24L * 1024 * 1024 * 1024,
              true,
              false,
              null,
              "rocm",
              0,
              true
            )
          ],
          null
        )
      );
    }
  }
}
