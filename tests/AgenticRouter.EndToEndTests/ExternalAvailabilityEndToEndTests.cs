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
}
