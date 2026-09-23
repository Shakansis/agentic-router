using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class OllamaStartupRecoveryEndToEndTests
  : ChatEndToEndTestBase<OllamaStartupRecoveryEndToEndTests>
{
  private Process? _discovery;
  private const string DiscoveryUrl = "http://127.0.0.2:11434";
  private string _executable = string.Empty;

  [TestCleanup]
  public async Task RestoreManagedConfigurationAsync()
  {
    if (_discovery is null) return;
    await _environment.SetManagedOllamaAndRestartAsync(null);
    if (!_discovery.HasExited)
    {
      _discovery.Kill(entireProcessTree: true);
      await _discovery.WaitForExitAsync();
    }
    _discovery.Dispose();
    _discovery = null;
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StartupRetriesOnceAndContinuesWithoutUserInput()
  {
    await ConfigureAsync([-1, 0]);
    await SendMessageAsync("Ollama startup recovery test");
    await AssertCompletedAsync();
    Assert.HasCount(2, StartedPids());
    Assert.IsFalse(ProcessIsAlive(StartedPids()[0]));
    await Expect(Page.Locator("#user-input-panel")).ToBeHiddenAsync();
    await Expect(Page.Locator("[data-event-type='ollama.startup-retrying']")).ToHaveCountAsync(1);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ReadyDuringRecoveryWindowContinuesWithoutQuestionOrRestart()
  {
    await ConfigureAsync([-1]);
    await StartMessageAsync("Ollama startup recovery test");
    await AwaitWarningAsync();
    var pids = StartedPids();
    var screenshot = Path.Combine(TestContext.TestResultsDirectory!, "ollama-startup-warning.png");
    await Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot });
    TestContext.AddResultFile(screenshot);
    Assert.HasCount(2, pids);
    Assert.IsFalse(ProcessIsAlive(pids[0]));
    Assert.IsTrue(ProcessIsAlive(pids[1]));
    await File.WriteAllTextAsync(Path.ChangeExtension(_executable, ".ready"), "ready");
    await AssertCompletedAsync();
    await Expect(Page.Locator("#user-input-panel")).ToBeHiddenAsync();
    Assert.HasCount(2, StartedPids());
    Assert.IsTrue(ProcessIsAlive(pids[1]));
    await Expect(Page.Locator("[data-event-type='user-input.submitted']")).ToHaveCountAsync(0);
    await Expect(Page.Locator("[data-event-type='ollama.startup-recovered']")).ToHaveCountAsync(1);
    await AssertNoPendingInputAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SlowFirstStartupWithinDeadlineDoesNotRestart()
  {
    await ConfigureAsync([1_500], attemptSeconds: 3);
    await SendMessageAsync("Ollama startup recovery test");
    await AssertCompletedAsync();
    var pids = StartedPids();
    Assert.HasCount(1, pids);
    Assert.IsTrue(ProcessIsAlive(pids[0]));
    await AssertNoPendingInputAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NoReadinessFailsOnlyAfterRecoveryWindowAndCleansUp()
  {
    await ConfigureAsync([-1], recoverySeconds: 3);
    await StartMessageAsync("Ollama startup recovery test");
    await AwaitWarningAsync();
    await Expect(Page.Locator(".assistant-answer.error")).ToContainTextAsync(
      "3 additional seconds of recovery monitoring", new() { Timeout = 15_000 }
    );
    await Expect(Page.Locator("#user-input-panel")).ToBeHiddenAsync();
    var pids = StartedPids();
    Assert.HasCount(2, pids);
    foreach (var pid in pids) Assert.IsFalse(ProcessIsAlive(pid));
    await AssertNoPendingInputAsync();
  }

  [TestMethod]
  [DataRow("chat", "native")]
  [DataRow("execute", "native")]
  [DataRow("execute", "qwen-code")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StopDuringRecoveryCancelsOnlyTheStartingProcess(string mode, string harness)
  {
    await ConfigureAsync([-1]);
    if (mode == "execute")
    {
      await SetExecuteModeAsync("auto");
      await Page.Locator("#harness-selector").SelectOptionAsync(harness);
    }
    await StartMessageAsync("Ollama startup recovery test");
    await AwaitWarningAsync();
    await Page.Locator("#cancel-request").ClickAsync();
    await Expect(Page.Locator("#user-input-panel")).ToBeHiddenAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Send");
    foreach (var pid in StartedPids())
    {
      for (var attempt = 0; attempt < 50 && ProcessIsAlive(pid); attempt++) await Task.Delay(100);
      Assert.IsFalse(ProcessIsAlive(pid));
    }
    await AssertNoPendingInputAsync();
    // The externally managed discovery fixture is still healthy.
    using var client = new HttpClient();
    using var response = await client.GetAsync($"{DiscoveryUrl}/api/version");
    response.EnsureSuccessStatusCode();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RefreshRestoresStartupWarningWithoutQuestionAndCanRecover()
  {
    await ConfigureAsync([-1]);
    await StartMessageAsync("Ollama startup recovery test");
    await AwaitWarningAsync();
    await Page.ReloadAsync();
    await AwaitWarningAsync();
    Assert.HasCount(2, StartedPids());
    await File.WriteAllTextAsync(Path.ChangeExtension(_executable, ".ready"), "ready");
    await AssertCompletedAsync();
    await Expect(Page.Locator("#user-input-panel")).ToBeHiddenAsync();
    await AssertNoPendingInputAsync();
  }

  private async Task ConfigureAsync(int[] readinessPlan, int recoverySeconds = 30, int attemptSeconds = 1)
  {
    if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Managed Ollama startup uses Windows Job Objects.");
    var fixture = Path.Combine(_environment.DataDirectory, "startup-fixtures", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixture);
    var configured = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_E2E_FAKE_OLLAMA_PATH");
    var source = string.IsNullOrWhiteSpace(configured)
      ? Path.Combine(_environment.RepositoryRoot, "tests", "FakeOllamaCli", "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0")
      : Path.GetDirectoryName(configured)!;
    foreach (var file in Directory.EnumerateFiles(source, "FakeOllamaCli*"))
      File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
    Directory.CreateDirectory(Path.Combine(fixture, "lib", "ollama", "cuda_v13"));
    _executable = Path.Combine(fixture, "FakeOllamaCli.exe");
    var discoveryDirectory = Path.Combine(fixture, "discovery");
    Directory.CreateDirectory(discoveryDirectory);
    foreach (var file in Directory.EnumerateFiles(source, "FakeOllamaCli*"))
      File.Copy(file, Path.Combine(discoveryDirectory, Path.GetFileName(file)));
    var discoveryExecutable = Path.Combine(discoveryDirectory, "FakeOllamaCli.exe");
    await File.WriteAllTextAsync(Path.ChangeExtension(discoveryExecutable, ".forward-url"), _environment.FakeOllama.BaseUrl);
    var start = new ProcessStartInfo(discoveryExecutable)
    {
      UseShellExecute = false,
      CreateNoWindow = true
    };
    start.ArgumentList.Add("serve");
    start.Environment["OLLAMA_HOST"] = DiscoveryUrl;
    start.Environment["OLLAMA_LLM_LIBRARY"] = "cuda_v13";
    _discovery = Process.Start(start)!;
    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) })
    {
      for (var attempt = 0; ; attempt++)
      {
        try
        {
          using var response = await client.GetAsync($"{DiscoveryUrl}/api/version");
          response.EnsureSuccessStatusCode();
          break;
        }
        catch (HttpRequestException) when (attempt < 20) { await Task.Delay(100); }
      }
    }
    await File.WriteAllTextAsync(Path.ChangeExtension(_executable, ".startup-plan.json"), JsonSerializer.Serialize(readinessPlan));
    await File.WriteAllTextAsync(Path.ChangeExtension(_executable, ".forward-url"), _environment.FakeOllama.BaseUrl);
    using var port = new TcpListener(IPAddress.Loopback, 0);
    port.Start();
    var managedPort = ((IPEndPoint)port.LocalEndpoint).Port;
    port.Stop();
    await _environment.SetManagedOllamaAndRestartAsync(_executable, managedPort - 11_434, attemptSeconds, recoverySeconds);
    var settings = await GetSettingsJsonAsync();
    settings["ollamaUrl"] = DiscoveryUrl;
    settings["defaultGpu"] = "ollama:0";
    settings["modelGpuAffinities"] = new JsonObject();
    using var saved = await PutSettingsJsonAsync(settings);
    saved.EnsureSuccessStatusCode();
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
  }

  private async Task AwaitWarningAsync()
  {
    var warning = Page.Locator(".ollama-startup-alert");
    await Expect(warning).ToBeVisibleAsync(new() { Timeout = 20_000 });
    await Expect(warning).ToContainTextAsync("The HTTP endpoint responded, but CUDA GPU discovery was not confirmed.");
    await Expect(warning).ToContainTextAsync("No response is required.");
    await Expect(Page.Locator("#user-input-panel")).ToBeHiddenAsync();
    await Expect(Page.Locator(".assistant-answer.error")).ToHaveCountAsync(0);
  }

  private async Task AssertCompletedAsync()
  {
    await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync(
      "data-terminal", "true", new() { Timeout = 20_000 }
    );
    await Expect(Page.Locator(".assistant-answer.error")).ToHaveCountAsync(0);
    await Expect(Page.Locator(".assistant-answer").Last).Not.ToBeEmptyAsync();
    await Expect(Page.Locator(".ollama-startup-alert")).ToHaveCountAsync(0);
  }

  private int[] StartedPids() => File.ReadAllLines(Path.ChangeExtension(_executable, ".starts"))
    .Select(line => int.Parse(line, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

  private async Task AssertNoPendingInputAsync()
  {
    var path = Path.Combine(_environment.DataDirectory, "user-input", "pending.json");
    if (File.Exists(path))
    {
      var pending = JsonNode.Parse(await File.ReadAllTextAsync(path));
      Assert.HasCount(0, pending!.AsArray());
    }
  }
}
