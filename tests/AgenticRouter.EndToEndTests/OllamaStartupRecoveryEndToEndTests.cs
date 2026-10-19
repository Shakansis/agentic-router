using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
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

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task NativeClaudeNativePreservesTheManagedProcessAndContext()
  {
    await ConfigureAsync([0]);
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    var leasePath = Directory.GetFiles(Path.Combine(_environment.DataDirectory, "ollama-managed-servers"), "*.json").Single();
    var first = JsonNode.Parse(await File.ReadAllTextAsync(leasePath))!;
    using var client = new HttpClient();
    var firstEnvironment = JsonNode.Parse(await client.GetStringAsync(first["endpoint"]!.GetValue<string>() + "test/environment"))!;
    Assert.IsFalse(string.IsNullOrEmpty(firstEnvironment["contextLength"]?.GetValue<string>()));
    foreach (var harness in new[] { "claude-code", "native" })
    {
      await Page.Locator("#harness-selector").SelectOptionAsync(harness);
      await SendMessageAsync(harness == "native" ? "Report runtime configuration without modifying files." : "claude code deterministic turn");
      var next = JsonNode.Parse(await File.ReadAllTextAsync(leasePath))!;
      Assert.AreEqual(first["processId"]!.GetValue<int>(), next["processId"]!.GetValue<int>());
      Assert.AreEqual(first["endpoint"]!.GetValue<string>(), next["endpoint"]!.GetValue<string>());
      var environment = JsonNode.Parse(await client.GetStringAsync(next["endpoint"]!.GetValue<string>() + "test/environment"))!;
      Assert.AreEqual(firstEnvironment["contextLength"]?.GetValue<string>(), environment["contextLength"]?.GetValue<string>());
      Assert.AreEqual(firstEnvironment["kvCacheType"]?.GetValue<string>(), environment["kvCacheType"]?.GetValue<string>());
    }
    Assert.HasCount(1, StartedPids());
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(request => request.KeepAlive == 0));
    await Expect(Page.Locator(".assistant-answer.error")).ToHaveCountAsync(0);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task InferenceProgressUsesRuntimePercentAndSubtractsModelLoadFromTtft()
  {
    var settings = await GetSettingsJsonAsync();
    settings["ollamaRuntime"]!["managedKvCacheType"] = "f16";
    using (var saved = await PutSettingsJsonAsync(settings)) saved.EnsureSuccessStatusCode();
    await ConfigureAsync([0]);
    var response = await Page.RunAndWaitForResponseAsync(
      () => StartMessageAsync("inference progress fixture"),
      item => item.Url.Contains("/api/chat/stream", StringComparison.Ordinal));
    var indicator = Page.Locator(".assistant-running-indicator").Last;
    await Expect(indicator).ToHaveAttributeAsync("data-inference-stage", "loading-model");
    await Expect(indicator.Locator(".assistant-current-activity")).ToContainTextAsync("Loading model");
    await Expect(Page.Locator(".assistant-progress").Last).ToContainTextAsync("Loading model");
    await Expect(indicator.Locator(".assistant-inference-percent")).ToBeHiddenAsync();
    await Expect(indicator.Locator(".assistant-running-brain")).ToBeVisibleAsync();
    await Expect(indicator.Locator(".assistant-running-host")).ToBeHiddenAsync();
    Assert.AreEqual("rgb(255, 255, 255)", await indicator.Locator(".assistant-running-brain").EvaluateAsync<string>("e => getComputedStyle(e).stroke"));
    await Expect(indicator.Locator(".assistant-inference-percent")).ToHaveTextAsync("25%");
    await Expect(indicator.Locator(".assistant-current-activity")).ToContainTextAsync("Processing prompt");
    await Expect(Page.Locator(".assistant-progress").Last).ToContainTextAsync("Processing prompt");
    await Expect(indicator.Locator(".assistant-inference-percent")).ToHaveTextAsync("75%");
    Assert.AreEqual("75%", await indicator.EvaluateAsync<string>("e => e.style.getPropertyValue('--inference-percent')"));
    var screenshot = Path.Combine(TestContext.TestResultsDirectory!, "inference-prompt-progress.png");
    await Page.ScreenshotAsync(new() { Path = screenshot });
    TestContext.AddResultFile(screenshot);
    await AssertCompletedAsync();
    var lease = JsonNode.Parse(await File.ReadAllTextAsync(Directory.GetFiles(
      Path.Combine(_environment.DataDirectory, "ollama-managed-servers"), "*.json").Single()))!;
    using var client = new HttpClient();
    var environment = JsonNode.Parse(await client.GetStringAsync(lease["endpoint"]!.GetValue<string>().TrimEnd('/') + "/test/environment"))!;
    Assert.AreEqual("f16", environment["kvCacheType"]!.GetValue<string>());
    var events = (await response.TextAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
      .Where(block => block.StartsWith("data: ", StringComparison.Ordinal))
      .Select(block => JsonNode.Parse(block[6..])!).ToArray();
    var metrics = events.Last(item => item["chatSummary"]?["inferenceMetrics"] is not null)["chatSummary"]!["inferenceMetrics"]!;
    Assert.AreEqual(1000d, metrics["modelLoadMilliseconds"]!.GetValue<double>());
    Assert.AreEqual("after-model-load", metrics["firstTokenTimingSource"]!.GetValue<string>());
    Assert.AreEqual(metrics["dispatchToFirstTokenMilliseconds"]!.GetValue<double>() - 1000,
      metrics["timeToFirstTokenMilliseconds"]!.GetValue<double>());
    await Expect(Page.Locator(".chat-session-footer [data-metric=ttft]")).ToHaveAttributeAsync("title",
      "Time to the first output token after subtracting provider-reported model loading. Includes prompt processing and reasoning's first token.");
    await Expect(indicator).ToBeHiddenAsync();
  }

  [TestMethod]
  [DataRow("native")]
  [DataRow("codex")]
  [DataRow("claude-code")]
  [DataRow("qwen-code")]
  [DataRow("opencode")]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task ModelCacheOverridesSelectIsolatedReusableServersAcrossHarnesses(string harness)
  {
    await ConfigureAsync([0]);
    var models = await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/models");
    var digest = models!["models"]!.AsArray().First(item => item!["name"]!.GetValue<string>() == "alpha:latest")!["digest"]!.GetValue<string>();
    var settings = await GetSettingsJsonAsync();
    settings["ollamaRuntime"]!["managedKvCacheType"] = "f16";
    settings["ollamaRuntime"]!["modelOverrides"] = new JsonArray(new JsonObject
    {
      ["provider"] = "ollama-local",
      ["model"] = "alpha:latest",
      ["digest"] = digest,
      ["performance"] = new JsonObject { ["kvCacheType"] = "q8_0" },
      ["overrides"] = new JsonObject()
    });
    using (var saved = await PutSettingsJsonAsync(settings)) saved.EnsureSuccessStatusCode();
    await Page.ReloadAsync();
    if (harness != "native") await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    await Expect(Page.Locator("[data-event-type='runtime.kv-cache-configured']").Last).ToContainTextAsync("q8_0");
    var first = (await EnvironmentsAsync()).Single(item => item["kvCacheType"]?.GetValue<string>() == "q8_0");
    Assert.AreEqual("1", first["flashAttention"]!.GetValue<string>());
    var firstPid = first["processId"]!.GetValue<int>();
    await Page.Locator("#model-selector").SelectOptionAsync("command-r:latest");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    await Expect(Page.Locator("[data-event-type='runtime.kv-cache-configured']").Last).ToContainTextAsync("f16");
    var second = await EnvironmentsAsync();
    Assert.IsTrue(second.Any(item => item["kvCacheType"]?.GetValue<string>() == "f16"));
    Assert.IsTrue(second.Any(item => item["processId"]!.GetValue<int>() == firstPid), "Another cache configuration must preserve the existing server.");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    Assert.AreEqual(firstPid, (await EnvironmentsAsync()).Single(item => item["kvCacheType"]?.GetValue<string>() == "q8_0")["processId"]!.GetValue<int>());
    await Expect(Page.Locator(".assistant-answer.error")).ToHaveCountAsync(0);

    async Task<JsonNode[]> EnvironmentsAsync()
    {
      using var client = new HttpClient();
      var values = new List<JsonNode>();
      foreach (var path in Directory.GetFiles(Path.Combine(_environment.DataDirectory, "ollama-managed-servers"), "*.json"))
      {
        var lease = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        values.Add(JsonNode.Parse(await client.GetStringAsync(lease["endpoint"]!.GetValue<string>().TrimEnd('/') + "/test/environment"))!);
      }
      return values.ToArray();
    }
  }

  [TestMethod]
  [DataRow("native")]
  [DataRow("codex")]
  [DataRow("claude-code")]
  [DataRow("qwen-code")]
  [DataRow("opencode")]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task DirectSwitchUnloadsPreviousBackendAcrossHarnesses(string harness)
  {
    await ConfigureAsync([0]);
    await using var previous = FakeOllamaServer.Start();
    await using var next = FakeOllamaServer.Start();
    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_executable)!, "lib", "ollama", "vulkan"));
    Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_executable)!, "lib", "ollama", "rocm_v6"));
    await File.WriteAllTextAsync(Path.ChangeExtension(_executable, ".forward-vulkan.url"), previous.BaseUrl);
    await File.WriteAllTextAsync(Path.ChangeExtension(_executable, ".forward-rocm_v6.url"), next.BaseUrl);
    _environment.FakeOllama.SetLoadedModelContext("unused:latest", 8192);

    var settings = await GetSettingsJsonAsync();
    settings["defaultGpu"] = "vulkan:all";
    using (var saved = await PutSettingsJsonAsync(settings)) saved.EnsureSuccessStatusCode();
    await Page.ReloadAsync();
    await SetExecuteModeAsync("auto");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    Assert.Contains("alpha:latest", previous.LoadedModels);
    var ownedPids = StartedPids();

    var ordering = new System.Collections.Concurrent.ConcurrentQueue<string>();
    previous.RequestObserved = request =>
    {
      if (request.KeepAlive == 0) ordering.Enqueue("unload");
    };
    next.RequestObserved = request =>
    {
      if (request.KeepAlive != 0)
        ordering.Enqueue(previous.LoadedModels.Count == 0 ? "load-after-unload" : "overlap");
    };
    settings["defaultGpu"] = "rocm:0";
    using (var saved = await PutSettingsJsonAsync(settings)) saved.EnsureSuccessStatusCode();
    await Page.ReloadAsync();
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);
    await Page.Locator("#model-selector").SelectOptionAsync("beta:code");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    await Expect(Page.Locator(".assistant-answer.error")).ToHaveCountAsync(0);
    await Expect(Page.Locator("[data-event-type='runtime.model-unloaded']").Last)
      .ToContainTextAsync("alpha:latest");
    Assert.IsEmpty(previous.LoadedModels);
    Assert.AreEqual("unload", ordering.First());
    Assert.DoesNotContain("overlap", ordering);
    if (harness == "native") Assert.Contains("load-after-unload", ordering);
    Assert.Contains("unused:latest", _environment.FakeOllama.LoadedModels,
      "Direct must preserve unrelated models in the user's external Ollama service.");
    Assert.IsTrue(ownedPids.All(ProcessIsAlive), "Only models are unloaded; the managed servers remain reusable.");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task DirectSwitchRequiresVerifiedUnloadAndRecovers(bool acknowledgeWithoutUnloading)
  {
    await ConfigureAsync([0]);
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    Assert.Contains("alpha:latest", _environment.FakeOllama.LoadedModels);
    _environment.FakeOllama.RejectModelUnload = !acknowledgeWithoutUnloading;
    _environment.FakeOllama.IgnoreModelUnload = acknowledgeWithoutUnloading;
    await Page.Locator("#model-selector").SelectOptionAsync("beta:code");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    await Expect(Page.Locator(".assistant-answer.error").Last).ToBeVisibleAsync(new() { Timeout = 20_000 });
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(request => request.Model == "beta:code"),
      "An unload error or an unverified unload must prevent target inference.");
    _environment.FakeOllama.RejectModelUnload = false;
    _environment.FakeOllama.IgnoreModelUnload = false;
    await SendMessageAsync("Report runtime configuration without modifying files.");
    Assert.Contains("beta:code", _environment.FakeOllama.LoadedModels);
    Assert.DoesNotContain("alpha:latest", _environment.FakeOllama.LoadedModels);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DirectSwitchWaitsForActiveTurnAndReleasesCancelledWaiter(bool cancelWaitingTurn)
  {
    await ConfigureAsync([0]);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _environment.FakeOllama.BeforeChatResponse = request =>
    {
      if (request.Model == "alpha:latest" && request.Messages.Any(message => message.Content.Contains("RESIDENCY_ACTIVE_TURN", StringComparison.Ordinal)))
      {
        _environment.FakeOllama.SetLoadedModelContext("alpha:latest", 8192);
        started.TrySetResult();
        return release.Task;
      }
      return Task.CompletedTask;
    };
    try
    {
      await StartMessageAsync("RESIDENCY_ACTIVE_TURN");
      await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
      using var client = new HttpClient { BaseAddress = _environment.BaseUri, Timeout = TimeSpan.FromSeconds(30) };
      using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream")
      {
        Content = JsonContent.Create(new
        {
          model = "beta:code",
          message = "Report runtime configuration without modifying files.",
          interactionMode = "chat",
          executionStrategy = "direct",
          harness = "native",
          browserSessionId = Guid.NewGuid().ToString("N"),
          chatRunId = Guid.NewGuid().ToString("N"),
          history = Array.Empty<object>()
        })
      };
      using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
      response.EnsureSuccessStatusCode();
      using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
      using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
      JsonNode? waiting = null;
      while (await reader.ReadLineAsync(deadline.Token) is { } line)
      {
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
        var frame = JsonNode.Parse(line[6..])!;
        if (frame["type"]?.GetValue<string>() != "runtime.model-switch-waiting") continue;
        waiting = frame;
        break;
      }
      Assert.IsNotNull(waiting);
      Assert.IsFalse(_environment.FakeOllama.Requests.Any(item => item.KeepAlive == 0 || item.Model == "beta:code"),
        "Waiting for the active turn must not evict its model or start the next model.");
      if (cancelWaitingTurn)
      {
        using var cancelled = await client.PostAsync($"api/chat/runs/{waiting["chatRunId"]!.GetValue<string>()}/cancel", null);
        cancelled.EnsureSuccessStatusCode();
        StringAssert.Contains(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)), "request.cancelled");
      }
      release.TrySetResult();
      await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true");
      if (cancelWaitingTurn)
      {
        await Page.Locator("#model-selector").SelectOptionAsync("beta:code");
        await SendMessageAsync("Report runtime configuration without modifying files.");
      }
      else
        StringAssert.Contains(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)), "response.completed");
      Assert.Contains("beta:code", _environment.FakeOllama.LoadedModels);
      Assert.DoesNotContain("alpha:latest", _environment.FakeOllama.LoadedModels);
      await Expect(Page.Locator(".assistant-answer.error")).ToHaveCountAsync(0);
    }
    finally
    {
      release.TrySetResult();
      _environment.FakeOllama.BeforeChatResponse = null;
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DirectExternalSwitchPreservesUnrelatedModelsAndKeepsSelectedWarm()
  {
    _environment.FakeOllama.SetLoadedModelContext("unused:latest", 8192);
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    var beforeWarm = _environment.FakeOllama.Requests.Count(item => item.KeepAlive == 0);
    await SendMessageAsync("Report runtime configuration without modifying files.");
    Assert.AreEqual(beforeWarm, _environment.FakeOllama.Requests.Count(item => item.KeepAlive == 0));
    await Page.Locator("#model-selector").SelectOptionAsync("beta:code");
    await SendMessageAsync("Report runtime configuration without modifying files.");
    Assert.DoesNotContain("alpha:latest", _environment.FakeOllama.LoadedModels);
    Assert.Contains("beta:code", _environment.FakeOllama.LoadedModels);
    Assert.Contains("unused:latest", _environment.FakeOllama.LoadedModels);
    await Expect(Page.Locator(".assistant-answer.error")).ToHaveCountAsync(0);
  }

  [TestCleanup]
  public async Task RestoreManagedConfigurationAsync()
  {
    if (_discovery is null) return;
    if (TestContext.CurrentTestOutcome == UnitTestOutcome.Failed)
    {
      TestContext.WriteLine(_environment.ApiOutput);
      TestContext.WriteLine(string.Join("\n", _environment.FakeOllama.Errors));
      var errors = Path.ChangeExtension(_executable, ".errors");
      if (File.Exists(errors)) TestContext.WriteLine(await File.ReadAllTextAsync(errors));
      var traffic = Path.ChangeExtension(_executable, ".traffic");
      if (File.Exists(traffic)) TestContext.WriteLine(await File.ReadAllTextAsync(traffic));
    }
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
    var managedPort = FindAvailableCachePorts();
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

  private static int FindAvailableCachePorts()
  {
    // The ephemeral base alone does not prove that its derived cache ports are
    // free or outside Windows excluded ranges. Check all routes before launch.
    for (var candidate = 20_000; candidate < 30_000; candidate++)
    {
      var listeners = new List<TcpListener>();
      try
      {
        var initial = new TcpListener(IPAddress.Loopback, candidate);
        listeners.Add(initial);
        initial.Start();
        var port = ((IPEndPoint)initial.LocalEndpoint).Port;
        for (var cache = 1; cache <= 3; cache++)
        {
          var listener = new TcpListener(IPAddress.Loopback, 1024 + (port - 1024 + cache * 4096) % (65_536 - 1024));
          listeners.Add(listener);
          listener.Start();
        }
        return port;
      }
      catch (SocketException) { }
      finally
      {
        foreach (var listener in listeners) listener.Stop();
      }
    }
    throw new InvalidOperationException("No available set of managed cache ports was found.");
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
