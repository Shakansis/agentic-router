using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class ExternalAvailabilityEndToEndTests
  : ChatEndToEndTestBase<ExternalAvailabilityEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AvailabilityWatcherAddsAndRemovesKnowledgeWarningWithoutReloading()
  {
    await EnableKnowledgeAsync();
    using var unavailable = ReserveUnavailableEndpoint();
    var unavailableAddress =
      $"http://127.0.0.1:{((IPEndPoint)unavailable.LocalEndPoint!).Port}";
    using (var configured = await _environment.HttpClient.PutAsJsonAsync(
      "api/knowledge-providers/anythingllm/connection",
      new { baseUrl = unavailableAddress, apiKey = "test-knowledge-key" }
    ))
    {
      configured.EnsureSuccessStatusCode();
    }

    await Page.GotoAsync("/");
    await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
    var warning = Page.Locator("[data-external-app='anythingllm']");
    await Expect(warning).ToHaveCountAsync(1);

    using (var recovered = await _environment.HttpClient.PutAsJsonAsync(
      "api/knowledge-providers/anythingllm/connection",
      new
      {
        baseUrl = $"{_environment.FakeCloud.BaseUrl}/anythingllm/",
        apiKey = "test-knowledge-key"
      }
    ))
    {
      recovered.EnsureSuccessStatusCode();
    }
    await Page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
    await Expect(warning).ToHaveCountAsync(0);

    using (var disconnected = await _environment.HttpClient.PutAsJsonAsync(
      "api/knowledge-providers/anythingllm/connection",
      new { baseUrl = unavailableAddress, apiKey = "test-knowledge-key" }
    ))
    {
      disconnected.EnsureSuccessStatusCode();
    }
    await Page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
    await Expect(warning).ToHaveCountAsync(1);
    await Expect(warning).ToContainTextAsync("AnythingLLM could not be reached");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingKnowledgeServiceDoesNotBlockStartupAndWarningClearsAfterReconnection()
  {
    await EnableKnowledgeAsync();
    using var unavailable = ReserveUnavailableEndpoint();
    var address = $"http://127.0.0.1:{((IPEndPoint)unavailable.LocalEndPoint!).Port}";
    using var configured = await _environment.HttpClient.PutAsJsonAsync(
      "api/knowledge-providers/anythingllm/connection",
      new { baseUrl = address, apiKey = "test-knowledge-key" }
    );
    configured.EnsureSuccessStatusCode();
    var result = JsonNode.Parse(await configured.Content.ReadAsStringAsync())!;
    Assert.IsFalse(result["providers"]![0]!["availability"]!["available"]!.GetValue<bool>());

    await Page.GotoAsync("/");
    await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
    await Expect(Page.Locator("#composer")).ToBeVisibleAsync();
    await Expect(Page.Locator("#external-app-warnings")).ToBeVisibleAsync();
    await Page.Locator("#external-app-warnings-summary").ClickAsync();
    var warning = Page.Locator("[data-external-app='anythingllm']");
    await Expect(warning).ToContainTextAsync("AnythingLLM could not be reached");
    StringAssert.Contains(_environment.ApiOutput, "knowledge-unavailable: AnythingLLM could not be reached");
    Assert.DoesNotContain("HTTP request failed after", _environment.ApiOutput);
    Assert.DoesNotContain("System.Net.Http.HttpRequestException", _environment.ApiOutput);
    await Expect(Page.Locator("#external-app-warnings")).Not.ToContainTextAsync("test-knowledge-key");
    var screenshotPath = Path.Combine(TestContext.TestResultsDirectory!, "missing-knowledge-startup.png");
    await Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
    TestContext.AddResultFile(screenshotPath);
    await warning.GetByRole(AriaRole.Button).ClickAsync();
    await Expect(Page.Locator("#knowledge-base-url")).ToBeVisibleAsync();
    await Page.Locator("#knowledge-base-url").FillAsync($"{_environment.FakeCloud.BaseUrl}/anythingllm/");
    await Page.Locator("#knowledge-api-key").FillAsync("test-knowledge-key");
    await Page.Locator("#save-knowledge-connection").ClickAsync();
    await Expect(Page.Locator("#knowledge-provider-status")).ToHaveTextAsync("AnythingLLM available");
    await Expect(warning).ToHaveCountAsync(0);
    await Page.Locator("#cancel-workspace").ClickAsync();
    await Expect(Page.Locator("#message-input")).ToBeEditableAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingOllamaAndHarnessStillAllowStartupAndSettings()
  {
    using var unavailable = ReserveUnavailableEndpoint();
    var settings = await GetSettingsJsonAsync();
    settings["ollamaUrl"] = $"http://127.0.0.1:{((IPEndPoint)unavailable.LocalEndPoint!).Port}";
    using var saved = await PutSettingsJsonAsync(settings);
    saved.EnsureSuccessStatusCode();
    try
    {
      await _environment.SetOpenCodeExecutableAndRestartAsync(
        Path.Combine(_environment.DataDirectory, "missing-opencode.exe")
      );
      using var profiles = await _environment.HttpClient.GetAsync("api/runtime/profiles");
      profiles.EnsureSuccessStatusCode();
      var profileView = JsonNode.Parse(await profiles.Content.ReadAsStringAsync())!;
      Assert.IsTrue(profileView["diagnostics"]!.AsArray().Any(
        diagnostic => diagnostic!["code"]!.GetValue<string>() == "runtime-provider-unavailable"
      ));
      // Wait for the real provider checks, including refused TCP connections,
      // before asserting that startup releases the interface.
      await Page.RunAndWaitForResponseAsync(
        () => Page.GotoAsync("/"),
        response => response.Url.Contains("/api/capabilities/model?", StringComparison.Ordinal)
      );
      await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
      await Expect(Page.Locator("#composer")).ToBeVisibleAsync();
      await Page.Locator("#external-app-warnings-summary").ClickAsync();
      await Expect(Page.Locator("[data-external-app='ollama-local']")).ToContainTextAsync("Ollama");
      await Expect(Page.Locator("[data-external-app='opencode']")).ToContainTextAsync("OpenCode");
      var list = Page.Locator("#external-app-warnings-list");
      await Expect(list).ToHaveCSSAsync("scrollbar-width", "none");
      var indicator = Page.Locator("#external-app-warnings .project-scroll-indicator");
      await Expect(indicator).ToBeVisibleAsync();
      var screenshotPath = Path.Combine(TestContext.TestResultsDirectory!, "availability-project-scrolling.png");
      await Page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath });
      TestContext.AddResultFile(screenshotPath);
      await list.FocusAsync();
      await list.PressAsync("End");
      await Expect(indicator).ToBeHiddenAsync();
      Assert.IsGreaterThan(0, await list.EvaluateAsync<int>("element => Math.round(element.scrollTop)"));
      await list.PressAsync("Home");
      await Expect(indicator).ToBeVisibleAsync();
      StringAssert.Contains(_environment.ApiOutput, "ollama-unavailable at");
      Assert.DoesNotContain("HTTP request failed after", _environment.ApiOutput);
      Assert.DoesNotContain("System.Net.Http.HttpRequestException", _environment.ApiOutput);
      await Page.Locator("[data-external-app='ollama-local'] button").ClickAsync();
      await Expect(Page.Locator("#settings-dialog")).ToBeVisibleAsync();
      Assert.IsFalse(_environment.FakeOllama.Requests.Any(request => request.Stream));
    }
    finally
    {
      await _environment.SetOpenCodeExecutableAndRestartAsync(_environment.FakeOpenCodeExecutablePath);
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingConfiguredGpuIsReportedWithoutBlockingStartupOrChangingAffinity()
  {
    const string missingAffinity = "device:GPU-disconnected-fixture";
    // Simulate a previously saved GPU that is absent on the next startup.
    // The configuration API correctly rejects assigning a GPU already absent.
    await _environment.RestartApplicationAsync(async () =>
    {
      var settings = JsonNode.Parse(await File.ReadAllTextAsync(_environment.SettingsPath))!;
      settings["modelGpuAffinities"] = new JsonObject { ["alpha:latest"] = missingAffinity };
      await File.WriteAllTextAsync(_environment.SettingsPath, settings.ToJsonString());
    });
    using var response = await _environment.HttpClient.GetAsync("api/runtime/profiles");
    response.EnsureSuccessStatusCode();
    var profiles = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    Assert.IsTrue(profiles["diagnostics"]!.AsArray().Any(diagnostic =>
      diagnostic!["code"]!.GetValue<string>() == "model-gpu-unavailable"
        && diagnostic["model"]!.GetValue<string>() == "alpha:latest"
    ));
    Assert.IsTrue(profiles["recommendations"]!.AsArray().Any(recommendation =>
      recommendation!["model"]!.GetValue<string>() != "alpha:latest"
    ), "Other model profiles should remain available.");
    await Page.GotoAsync("/");
    await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
    await Page.Locator("#external-app-warnings-summary").ClickAsync();
    var warning = Page.Locator("[data-external-app='runtime:model-gpu-unavailable:alpha:latest']");
    await Expect(warning).ToContainTextAsync("Your configuration has been preserved");
    StringAssert.Contains(_environment.ApiOutput, "model-gpu-unavailable: The configured GPU for alpha:latest");
    Assert.DoesNotContain("ModelGpuAffinityException:", _environment.ApiOutput);
    await warning.GetByRole(AriaRole.Button).ClickAsync();
    await Expect(Page.Locator("#settings-dialog")).ToBeVisibleAsync();
    var after = await GetSettingsJsonAsync();
    Assert.AreEqual(missingAffinity, after["modelGpuAffinities"]!["alpha:latest"]!.GetValue<string>());
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(request => request.Stream));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NonJsonStartupFailureRemainsFriendlyAfterParallelProviderChecksFinish()
  {
    var recordsPath = Path.Combine(_environment.DataDirectory, "runtime-profiles", "ollama-model-memory.json");
    Directory.CreateDirectory(Path.GetDirectoryName(recordsPath)!);
    await File.WriteAllTextAsync(recordsPath, "{invalid-json");
    try
    {
      await Page.RunAndWaitForResponseAsync(
        () => Page.GotoAsync("/"),
        response => response.Url.EndsWith("/api/provider-health", StringComparison.Ordinal)
      );
      await Expect(Page.Locator("#app-loader-retry")).ToBeVisibleAsync();
      await Expect(Page.Locator("#app-loader")).ToHaveAttributeAsync("aria-busy", "false");
      await Expect(Page.Locator("#app-loader-detail")).ToContainTextAsync("HTTP 500");
      await Expect(Page.Locator("#app-loader-detail")).Not.ToContainTextAsync("Unexpected token");
      await Expect(Page.Locator("#app-loader-detail")).Not.ToContainTextAsync("Loading workspace");
      File.Delete(recordsPath);
      await Page.Locator("#app-loader-retry").ClickAsync();
      await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
      await Expect(Page.Locator("#composer")).ToBeVisibleAsync();
    }
    finally
    {
      File.Delete(recordsPath);
    }
  }

  private static Socket ReserveUnavailableEndpoint()
  {
    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    return socket;
  }

  private static async Task EnableKnowledgeAsync()
  {
    var id = await ActiveWorkspaceIdAsync();
    using var response = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{id}/knowledge",
      new { enabled = true, providerId = "anythingllm", libraryIds = new[] { "project-handbook" } });
    response.EnsureSuccessStatusCode();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DisabledProjectDoesNotWarnForUnavailableAnythingLlm()
  {
    var id = await ActiveWorkspaceIdAsync();
    using var selection = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{id}/knowledge",
      new { enabled = false, providerId = "anythingllm", libraryIds = new[] { "project-handbook" } });
    selection.EnsureSuccessStatusCode();
    using var unavailable = ReserveUnavailableEndpoint();
    using var configured = await _environment.HttpClient.PutAsJsonAsync(
      "api/knowledge-providers/anythingllm/connection",
      new { baseUrl = $"http://127.0.0.1:{((IPEndPoint)unavailable.LocalEndPoint!).Port}", apiKey = "test-knowledge-key" });
    configured.EnsureSuccessStatusCode();
    Assert.IsFalse(JsonNode.Parse(await configured.Content.ReadAsStringAsync())!["providers"]![0]!["availability"]!["available"]!.GetValue<bool>());
    await Page.GotoAsync("/");
    await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
    await Expect(Page.Locator("[data-external-app='anythingllm']")).ToHaveCountAsync(0);
    await Page.Locator(".project-accordion.active .project-menu-button").ClickAsync();
    await Page.Locator("#project-menu-edit").ClickAsync();
    await Page.Locator("#knowledge-section").EvaluateAsync("element => element.open = true");
    await Expect(Page.Locator("#knowledge-provider-status")).ToHaveTextAsync("Knowledge disabled for project");
    await Expect(Page.Locator("#knowledge-provider-diagnostic")).Not.ToContainTextAsync("could not be reached");
    Assert.DoesNotContain("knowledge-unavailable", _environment.ApiOutput);
    Assert.DoesNotContain("knowledge-timeout", _environment.ApiOutput);
    await Page.Locator("#knowledge-enabled").CheckAsync();
    await Expect(Page.Locator("#knowledge-provider-status")).ToHaveTextAsync("AnythingLLM unavailable");
    await Expect(Page.Locator("[data-external-app='anythingllm']")).ToHaveCountAsync(1);
    await Page.Locator("#knowledge-enabled").UncheckAsync();
    await Expect(Page.Locator("#knowledge-provider-status")).ToHaveTextAsync("Knowledge disabled for project");
    await Expect(Page.Locator("[data-external-app='anythingllm']")).ToHaveCountAsync(0);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DisabledProjectSkipsAutomaticAnythingLlmProbesDuringStartupFocusAndExecute()
  {
    using var configured = await _environment.HttpClient.PutAsJsonAsync(
      "api/knowledge-providers/anythingllm/connection",
      new { baseUrl = $"{_environment.FakeCloud.BaseUrl}/anythingllm/", apiKey = "test-knowledge-key" });
    configured.EnsureSuccessStatusCode();
    var before = _environment.FakeCloud.Requests.Count(request => request.Path.StartsWith("/anythingllm/", StringComparison.Ordinal));
    Assert.IsGreaterThan(0, before);
    await Page.GotoAsync("/");
    await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
    await Expect(Page.Locator("[data-external-app='anythingllm']")).ToHaveCountAsync(0);
    await Page.Locator(".project-accordion.active .project-menu-button").ClickAsync();
    await Page.Locator("#project-menu-edit").ClickAsync();
    await Page.Locator("#knowledge-section").EvaluateAsync("element => element.open = true");
    await Expect(Page.Locator("#knowledge-enabled")).Not.ToBeCheckedAsync();
    await Expect(Page.Locator("#knowledge-provider-status")).ToHaveTextAsync("Knowledge disabled for project");
    await Page.Locator("#cancel-workspace").ClickAsync();
    var events = await ExecuteCodexStreamAsync("harness HTTP metrics", "browser-disabled-knowledge-execute");
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
    await Page.RunAndWaitForResponseAsync(
      () => Page.EvaluateAsync("window.dispatchEvent(new Event('focus'))"),
      response => response.Url.EndsWith("/api/knowledge-providers", StringComparison.Ordinal));
    Assert.AreEqual(before, _environment.FakeCloud.Requests.Count(request => request.Path.StartsWith("/anythingllm/", StringComparison.Ordinal)));
    using var refreshed = await _environment.HttpClient.PostAsync("api/knowledge-providers/anythingllm/refresh", null);
    refreshed.EnsureSuccessStatusCode();
    var refreshedView = JsonNode.Parse(await refreshed.Content.ReadAsStringAsync())!;
    Assert.IsTrue(refreshedView["providers"]![0]!["statusChecked"]!.GetValue<bool>());
    Assert.IsTrue(refreshedView["providers"]![0]!["availability"]!["available"]!.GetValue<bool>());
    Assert.IsGreaterThan(before, _environment.FakeCloud.Requests.Count(request => request.Path.StartsWith("/anythingllm/", StringComparison.Ordinal)));
  }
}
