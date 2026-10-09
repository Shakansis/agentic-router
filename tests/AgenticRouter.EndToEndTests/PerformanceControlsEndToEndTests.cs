using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class PerformanceControlsEndToEndTests : ChatEndToEndTestBase<PerformanceControlsEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ModelTestUsesUnsavedDraftAndDisplaysMeasuredThroughput()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    await Page.Locator("[data-settings-target=inference]").ClickAsync();
    await Page.Locator("#inference-profile-selector").SelectOptionAsync("general-chat");
    await Page.Locator("#inference-temperature").FillAsync("0.37");
    await Page.Locator("#inference-thinking").SelectOptionAsync("disabled");
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await Page.Locator("#runtime-performance-model").SelectOptionAsync("qwen3.8:27b-gpu0");
    await Page.Locator("#runtime-draft-mode").SelectOptionAsync("custom");
    await Page.Locator("#runtime-draft-value").FillAsync("4");
    await Page.Locator("#runtime-batch-mode").SelectOptionAsync("custom");
    await Page.Locator("#runtime-batch-value").FillAsync("256");
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    await Page.Locator("#model-test-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    _environment.FakeOllama.Reset();
    _environment.FakeOllama.DelayNextModelTestResponse(TimeSpan.FromSeconds(3));
    var response = await Page.RunAndWaitForResponseAsync(
      () => Page.Locator("#test-model").ClickAsync(),
      response => response.Url.EndsWith("/api/models/test/stream", StringComparison.Ordinal));
    await Expect(Page.Locator(".model-test-stages [aria-current=step]")).ToHaveTextAsync("Loading model / processing input");
    await Expect(Page.Locator("#model-test-result")).ToContainTextAsync("Waiting for the first generated token");
    await Expect(Page.Locator("#model-test-result")).ToContainTextAsync("1 s elapsed");
    await Expect(Page.Locator("#model-test-result")).ToContainTextAsync("tok/s");
    await Expect(Page.Locator("#model-test-result")).ToContainTextAsync("Selected settings (not saved)");
    await Expect(Page.Locator(".model-test-metrics > div")).ToHaveCountAsync(8);
    var events = (await response.TextAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
      .Select(block => JsonNode.Parse(block[6..])!).ToArray();
    CollectionAssert.AreEqual(new[] { "preparing", "loading-prefill", "generating", "collecting" },
      events.Where(item => item["type"]!.GetValue<string>() == "progress")
        .Select(item => item["progress"]!["stage"]!.GetValue<string>()).Distinct().ToArray());
    Assert.AreEqual("result", events.Last()["type"]!.GetValue<string>());
    var call = _environment.FakeOllama.Requests.Single();
    Assert.AreEqual(0.37, call.Temperature);
    Assert.AreEqual("false", call.Think?.ToLowerInvariant());
    Assert.AreEqual(8192, call.PredictTokens);
    Assert.AreEqual(4, call.DraftTokens);
    Assert.AreEqual(256, call.BatchSize);
    Assert.IsNotNull(call.KeepAlive);
    Assert.IsGreaterThanOrEqualTo(14, await Page.Locator("#model-test-result").EvaluateAsync<double>("el => parseFloat(getComputedStyle(el).fontSize)"));
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
    await Expect(Page.Locator(".message")).ToHaveCountAsync(0);
    await Page.Locator("#model-test-result").ScreenshotAsync(new()
    {
      Path = Path.Combine(_environment.RepositoryRoot,
      "tests/AgenticRouter.EndToEndTests/TestResults/model-test-progress/model-test.png")
    });
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ModelTestExplainsInsufficientSelectedContextWithoutChangingIt()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await Page.Locator("#runtime-role-modelTest-targetContextTokens").FillAsync("4096");
    await Page.Locator("#runtime-role-modelTest-maximumContextTokens").FillAsync("8192");
    await Page.Locator("#runtime-role-modelTest-outputTokenLimit").FillAsync("512");
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    await Page.Locator("#model-test-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    _environment.FakeOllama.Reset();
    await Page.Locator("#test-model").ClickAsync();
    await Expect(Page.Locator("#model-test-result")).ToContainTextAsync("selected maximum is 8192");
    await Expect(Page.Locator("#model-test-result")).ToContainTextAsync("test again without saving");
    Assert.IsEmpty(_environment.FakeOllama.Requests);
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ModelTestCanBeCancelledWhileLoadingWithoutSaving()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    _environment.FakeOllama.DelayNextModelTestResponse(TimeSpan.FromSeconds(16));
    await Page.Locator("#test-model").ClickAsync();
    await Expect(Page.Locator(".model-test-stages [aria-current=step]")).ToHaveTextAsync("Loading model / processing input");
    await Page.Locator("#close-settings").ClickAsync();
    await OpenSettingsAsync();
    await Expect(Page.Locator(".model-test-stages [aria-current=step]")).ToHaveTextAsync("Loading model / processing input");
    await Page.GetByRole(AriaRole.Button, new() { Name = "Cancel test", Exact = true }).ClickAsync();
    await Expect(Page.Locator("#model-test-result")).ToHaveTextAsync("Model test cancelled.");
    await Expect(Page.Locator("#test-model")).ToBeEnabledAsync();
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ComposerControlsShareOneRowAtMaximumWidthAndWrapOnNarrowScreens()
  {
    await Page.SetViewportSizeAsync(1600, 1000);
    await Page.GotoAsync("/");
    foreach (var mode in new[] { "chat", "execute" })
    {
      await Page.Locator($"button[data-mode={mode}]").ClickAsync();
      var model = (await Page.Locator("#model-selector").BoundingBoxAsync())!;
      foreach (var selector in new[] { "#approval-policy", "#harness-selector", "#thinking-selector", "#send-button" })
      {
        var box = (await Page.Locator(selector).BoundingBoxAsync())!;
        Assert.IsLessThan(5f, Math.Abs(model.Y + model.Height / 2 - box.Y - box.Height / 2), selector);
      }
    }
    await Page.Locator(".composer-shell").ScreenshotAsync(new()
    {
      Path = Path.Combine(_environment.RepositoryRoot, "tests/AgenticRouter.EndToEndTests/TestResults/model-test-progress/composer-wide.png")
    });
    await Page.SetViewportSizeAsync(600, 900);
    await Expect(Page.Locator("#thinking-selector")).ToBeVisibleAsync();
    Assert.IsTrue(await Page.Locator(".composer").EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"));
    await Page.Locator(".composer-shell").ScreenshotAsync(new()
    {
      Path = Path.Combine(_environment.RepositoryRoot, "tests/AgenticRouter.EndToEndTests/TestResults/model-test-progress/composer-narrow.png")
    });
  }

  [TestMethod]
  [DataRow("none", "false")]
  [DataRow("low", "true")]
  [DataRow("medium", "true")]
  [DataRow("high", "true")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ComposerThinkingUsesBooleanModelControl(string effort, string sent)
  {
    _environment.FakeOllama.BooleanThinkingForQwen = true;
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await Page.Locator("#thinking-selector").SelectOptionAsync(effort);
    _environment.FakeOllama.Reset();
    await SendMessageAsync("Hello");
    var calls = _environment.FakeOllama.Requests.Where(call => call.Model == "qwen3.8:27b-gpu0" && call.Messages.Any(message => message.Role == "user" && message.Content == "Hello")).ToArray();
    Assert.IsNotEmpty(calls);
    Assert.IsTrue(calls.All(call => call.Think?.ToLowerInvariant() == sent));
    await Expect(Page.Locator("#harness-selector")).Not.ToContainTextAsync("Experimental");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NonePhaseEffortsSaveAndRoundTrip()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    foreach (var phase in new[] { "plan", "verify", "complete", "recovery" })
      await Page.Locator($"#phase-effort-{phase}").SelectOptionAsync("none");
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    foreach (var phase in new[] { "plan", "verify", "complete", "recovery" })
      Assert.AreEqual("none", settings["execution"]!["phaseEffort"]![phase]!.GetValue<string>());
  }
}
