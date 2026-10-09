using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Configuration;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class RuntimePerformanceEndToEndTests : ChatEndToEndTestBase<RuntimePerformanceEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimePerformanceUiPersistsOffAndBatchAcrossRestartAndYaml()
  {
    await OpenRuntimeAsync();
    await Page.Locator("#runtime-managed-kv-cache").SelectOptionAsync("f16");
    await Page.GetByRole(AriaRole.Button, new() { Name = "Help: KV cache precision", Exact = true }).FocusAsync();
    await Expect(Page.Locator("#settings-floating-tooltip")).ToContainTextAsync("independently of model weights");
    var help = await Page.RunAndWaitForPopupAsync(() => Page.Locator("a[href='/inference-help.html#kv-cache']").ClickAsync());
    await Expect(help.Locator("#kv-cache")).ToContainTextAsync("one quarter");
    await help.CloseAsync();
    await Expect(Page.Locator("#runtime-managed-kv-cache")).ToHaveValueAsync("f16");
    await Expect(Page.Locator("#runtime-performance-capabilities")).ToContainTextAsync("Not confirmed");
    await Page.Locator("#runtime-kv-cache").SelectOptionAsync("q8_0");
    await Page.Locator("#runtime-draft-mode").SelectOptionAsync("off");
    await Page.Locator("#runtime-batch-mode").SelectOptionAsync("custom");
    await Page.Locator("#runtime-batch-value").FillAsync("256");
    await Expect(Page.Locator("#apply-runtime-performance")).ToHaveCountAsync(0);
    Assert.AreEqual(0, (await SettingsAsync())["ollamaRuntime"]!["modelOverrides"]!.AsArray().Count);
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    await Expect(Page.Locator("#settings-dirty")).ToBeHiddenAsync();
    var saved = await SettingsAsync();
    Assert.AreEqual("f16", saved["ollamaRuntime"]!["managedKvCacheType"]!.GetValue<string>());
    Assert.AreEqual("q8_0", saved["ollamaRuntime"]!["modelOverrides"]![0]!["performance"]!["kvCacheType"]!.GetValue<string>());
    Assert.AreEqual(0, saved["ollamaRuntime"]!["modelOverrides"]![0]!["performance"]!["draftTokens"]!.GetValue<int>());
    Assert.AreEqual(256, saved["ollamaRuntime"]!["modelOverrides"]![0]!["performance"]!["batchSize"]!.GetValue<int>());
    Assert.AreEqual(0, saved["ollamaRuntime"]!["modelOverrides"]![0]!["overrides"]!.AsObject().Count);

    var yaml = await _environment.HttpClient.GetStringAsync("api/settings/yaml");
    StringAssert.Contains(yaml, "kv_cache_type: \"q8_0\"");
    StringAssert.Contains(yaml, "draft_tokens:");
    await _environment.RestartApplicationAsync();
    var reloaded = await SettingsAsync();
    Assert.IsTrue(JsonNode.DeepEquals(saved["ollamaRuntime"], reloaded["ollamaRuntime"]));

    using var imported = await _environment.HttpClient.PutAsJsonAsync("api/settings/yaml", new { yaml });
    imported.EnsureSuccessStatusCode();
    var roundTrip = await SettingsAsync();
    Assert.IsTrue(JsonNode.DeepEquals(saved["ollamaRuntime"], roundTrip["ollamaRuntime"]));
    await Page.ReloadAsync();
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await Expect(Page.Locator("#runtime-draft-mode")).ToHaveValueAsync("off");
    await Expect(Page.Locator("#runtime-managed-kv-cache")).ToHaveValueAsync("f16");
    await Expect(Page.Locator("#runtime-kv-cache")).ToHaveValueAsync("q8_0");
    await Expect(Page.Locator("#runtime-batch-value")).ToHaveValueAsync("256");
  }

  [TestMethod]
  [DataRow("bf16")]
  [DataRow("")]
  [DataRow(null)]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task InvalidManagedCacheDoesNotPartiallySaveSettings(string? cache)
  {
    await OpenRuntimeAsync();
    var before = await SettingsAsync();
    var draft = before.DeepClone();
    draft["ollamaRuntime"]!["managedKvCacheType"] = cache;
    using var response = await _environment.HttpClient.PutAsJsonAsync("api/settings", draft);
    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.IsTrue(JsonNode.DeepEquals(before, await SettingsAsync()));
    await Expect(Page.Locator("#runtime-managed-kv-cache")).ToHaveValueAsync("auto");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimePerformanceFooterSavesEditsAcrossModelsAndSections()
  {
    await OpenRuntimeAsync();
    await Page.Locator("#runtime-kv-cache").SelectOptionAsync("f16");
    await Page.Locator("#runtime-draft-mode").SelectOptionAsync("custom");
    await Page.Locator("#runtime-draft-value").FillAsync("3");
    await Page.Locator("#runtime-batch-mode").SelectOptionAsync("custom");
    await Page.Locator("#runtime-batch-value").FillAsync("128");
    await SelectRuntimeModelAsync("command-r:latest");
    await Page.Locator("#runtime-kv-cache").SelectOptionAsync("q4_0");
    await Page.Locator("#runtime-draft-mode").SelectOptionAsync("off");
    await Page.Locator("#runtime-batch-mode").SelectOptionAsync("custom");
    await Page.Locator("#runtime-batch-value").FillAsync("512");
    await SelectRuntimeModelAsync("alpha:latest");
    await Expect(Page.Locator("#runtime-kv-cache")).ToHaveValueAsync("f16");
    await Expect(Page.Locator("#runtime-draft-value")).ToHaveValueAsync("3");
    await Expect(Page.Locator("#runtime-batch-value")).ToHaveValueAsync("128");
    Assert.AreEqual(0, (await SettingsAsync())["ollamaRuntime"]!["modelOverrides"]!.AsArray().Count);
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");

    var overrides = (await SettingsAsync())["ollamaRuntime"]!["modelOverrides"]!.AsArray();
    Assert.AreEqual(2, overrides.Count);
    var alpha = overrides.Single(item => item!["model"]!.GetValue<string>() == "alpha:latest")!;
    var command = overrides.Single(item => item!["model"]!.GetValue<string>() == "command-r:latest")!;
    Assert.AreEqual("f16", alpha["performance"]!["kvCacheType"]!.GetValue<string>());
    Assert.AreEqual("q4_0", command["performance"]!["kvCacheType"]!.GetValue<string>());
    Assert.AreEqual(3, alpha["performance"]!["draftTokens"]!.GetValue<int>());
    Assert.AreEqual(128, alpha["performance"]!["batchSize"]!.GetValue<int>());
    Assert.AreEqual(0, command["performance"]!["draftTokens"]!.GetValue<int>());
    Assert.AreEqual(512, command["performance"]!["batchSize"]!.GetValue<int>());
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await SelectRuntimeModelAsync("command-r:latest");
    await Expect(Page.Locator("#runtime-draft-mode")).ToHaveValueAsync("off");
    await Expect(Page.Locator("#runtime-batch-value")).ToHaveValueAsync("512");
    await Expect(Page.Locator("#runtime-kv-cache")).ToHaveValueAsync("q4_0");
    await Page.Locator("#runtime-kv-cache").SelectOptionAsync("inherit");
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    var inherited = (await SettingsAsync())["ollamaRuntime"]!["modelOverrides"]!.AsArray()
      .Single(item => item!["model"]!.GetValue<string>() == "command-r:latest")!;
    Assert.IsNull(inherited["performance"]!["kvCacheType"]);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimePerformanceFooterRejectsHiddenInvalidEditsAndCancelDiscardsThem()
  {
    await OpenRuntimeAsync();
    var before = await SettingsAsync();
    await Page.Locator("#runtime-draft-mode").SelectOptionAsync("custom");
    await Page.Locator("#runtime-draft-value").FillAsync("0");
    await SelectRuntimeModelAsync("command-r:latest");
    await Page.Locator("#runtime-draft-mode").SelectOptionAsync("off");
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Save failed");
    await Expect(Page.Locator("#runtime-performance-model")).ToHaveValueAsync("alpha:latest");
    await Expect(Page.Locator("#runtime-draft-value")).ToHaveAttributeAsync("aria-invalid", "true");
    await Expect(Page.Locator("#settings-errors")).ToContainTextAsync("positive integer");
    Assert.IsTrue(JsonNode.DeepEquals(before, await SettingsAsync()));
    await Page.Locator("#runtime-draft-value").FillAsync("6");
    await Page.Locator("#close-settings").ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(Page.Locator("#settings-dialog")).ToBeHiddenAsync();
    Assert.IsTrue(JsonNode.DeepEquals(before, await SettingsAsync()));
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await SelectRuntimeModelAsync("alpha:latest");
    await Expect(Page.Locator("#runtime-draft-mode")).ToHaveValueAsync("auto");
    await SelectRuntimeModelAsync("command-r:latest");
    await Expect(Page.Locator("#runtime-draft-mode")).ToHaveValueAsync("auto");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimePerformanceNativeRequestsAndMeasurementUseTheExactModelSettings()
  {
    await Page.GotoAsync("/");
    await ConfigureAsync(0, 256);
    await SendMessageAsync("runtime performance request");
    var request = _environment.FakeOllama.Requests.Last(item => item.Stream && item.Model == "alpha:latest");
    Assert.AreEqual(0, request.DraftTokens);
    Assert.AreEqual(256, request.BatchSize);
    await Expect(Page.Locator("[data-event-type='runtime.performance-configured']")).ToContainTextAsync("not independently confirmed");

    using var measure = await _environment.HttpClient.PostAsJsonAsync("api/runtime/profiles/measure",
      new { model = "alpha:latest", role = "primary", contextCandidates = new[] { 8192 }, permissionGranted = true });
    measure.EnsureSuccessStatusCode();
    var first = await measure.Content.ReadFromJsonAsync<JsonObject>();
    var signature = first!["measurement"]!["runtimeSettingSignature"]!.GetValue<string>();
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(item =>
      item.Model == "alpha:latest" && item.KeepAlive != 0 && item.DraftTokens == 0 && item.BatchSize == 256));
    Assert.AreEqual(256, first["measurement"]!["performance"]!["batchSize"]!.GetValue<int>());

    await ConfigureAsync(4, 512);
    var profiles = await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/runtime/profiles");
    Assert.IsTrue(profiles!["measurements"]!.AsArray().Any(item =>
      item!["runtimeSettingSignature"]!.GetValue<string>() == signature && item["stale"]!.GetValue<bool>()));
    using var secondMeasure = await _environment.HttpClient.PostAsJsonAsync("api/runtime/profiles/measure",
      new { model = "alpha:latest", role = "primary", contextCandidates = new[] { 8192 }, permissionGranted = true });
    secondMeasure.EnsureSuccessStatusCode();
    var second = await secondMeasure.Content.ReadFromJsonAsync<JsonObject>();
    Assert.AreNotEqual(signature, second!["measurement"]!["runtimeSettingSignature"]!.GetValue<string>());

    await ConfigureAsync(null, null);
    await SendMessageAsync("runtime performance auto");
    request = _environment.FakeOllama.Requests.Last(item => item.Stream && item.Model == "alpha:latest");
    Assert.IsNull(request.DraftTokens);
    Assert.IsNull(request.BatchSize);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimePerformanceValidationIsAtomicAndDigestMismatchIsRejected()
  {
    await Page.GotoAsync("/");
    var before = await SettingsAsync();
    var invalid = before.DeepClone().AsObject();
    invalid["ollamaRuntime"]!["modelOverrides"] = new JsonArray(new JsonObject
    {
      ["provider"] = "ollama-local",
      ["model"] = "alpha:latest",
      ["digest"] = "sha256:alpha",
      ["overrides"] = new JsonObject(),
      ["performance"] = new JsonObject { ["draftTokens"] = -1, ["batchSize"] = 0 }
    });
    using var rejected = await _environment.HttpClient.PutAsJsonAsync("api/settings", invalid);
    Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
    Assert.IsTrue(JsonNode.DeepEquals(before, await SettingsAsync()));

    var configured = await SettingsAsync();
    configured["ollamaRuntime"]!["modelOverrides"] = new JsonArray(new JsonObject
    {
      ["provider"] = "ollama-local",
      ["model"] = "alpha:latest",
      ["digest"] = "sha256:old-model",
      ["overrides"] = new JsonObject(),
      ["performance"] = new JsonObject { ["draftTokens"] = 0, ["batchSize"] = 256 }
    });
    using var stale = await _environment.HttpClient.PutAsJsonAsync("api/settings", configured);
    Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
    await SendMessageAsync("runtime performance stale model");
    var request = _environment.FakeOllama.Requests.Last(item => item.Stream && item.Model == "alpha:latest");
    Assert.IsNull(request.DraftTokens);
    Assert.IsNull(request.BatchSize);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimePerformanceCapabilitiesAreHonestAndRejectionPreservesSettings()
  {
    _environment.FakeOllama.SetRuntimeModelCapabilities("alpha:latest", draft: true);
    await OpenRuntimeAsync();
    await Expect(Page.Locator("#runtime-performance-capabilities")).ToContainTextAsync("declares draft");
    await ConfigureAsync(4, 256);
    _environment.FakeOllama.RejectRuntimePerformance = true;
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream",
      new { message = "runtime option rejection", model = "alpha:latest", history = Array.Empty<object>(), interactionMode = "chat" });
    var body = await response.Content.ReadAsStringAsync();
    StringAssert.Contains(body, "runtime-performance-rejected");
    var settings = await SettingsAsync();
    Assert.AreEqual(4, settings["ollamaRuntime"]!["modelOverrides"]![0]!["performance"]!["draftTokens"]!.GetValue<int>());

    _environment.FakeOllama.SetRuntimeModelCapabilities("docs:latest", draft: false, generative: false);
    await Page.Locator("#runtime-performance-model").SelectOptionAsync("docs:latest");
    await Expect(Page.Locator("#runtime-performance-capabilities")).ToContainTextAsync("Unavailable");
    await Expect(Page.Locator("#runtime-draft-mode")).ToBeDisabledAsync();
    await Expect(Page.Locator("#runtime-batch-mode")).ToBeDisabledAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimeHelpEscapesClippingAndRemainsAccessibleAtDesktopAndMobileWidths()
  {
    await OpenRuntimeAsync();
    await Page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "ar-runtime-performance-01a0f5c0", "runtime-desktop.png") });
    var help = Page.Locator("[aria-label='Help: draft tokens']");
    await help.FocusAsync();
    var tooltip = Page.Locator("#settings-floating-tooltip");
    await Expect(tooltip).ToBeVisibleAsync();
    await Expect(tooltip).ToContainTextAsync("Off sends 0");
    Assert.IsTrue(await tooltip.EvaluateAsync<bool>("el => el.matches(':popover-open')"));
    await Expect(help).ToHaveAttributeAsync("aria-describedby", "settings-floating-tooltip");
    await help.PressAsync("Escape");
    await Expect(tooltip).ToBeHiddenAsync();
    await Expect(Page.Locator("#settings-dialog")).ToBeVisibleAsync();
    await help.HoverAsync();
    await Expect(tooltip).ToBeVisibleAsync();
    Assert.IsTrue(await tooltip.EvaluateAsync<bool>("el => { const r=el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && r.top >= 0 && r.bottom <= innerHeight; }"));
    await Page.Locator("#settings-content").EvaluateAsync("el => el.dispatchEvent(new Event('scroll'))");
    await Expect(tooltip).ToBeHiddenAsync();

    await Page.SetViewportSizeAsync(420, 800);
    await Page.Locator("#runtime-performance-model").FocusAsync();
    await help.ScrollIntoViewIfNeededAsync();
    await help.FocusAsync();
    await Expect(tooltip).ToBeVisibleAsync();
    Assert.IsTrue(await tooltip.EvaluateAsync<bool>("el => { const r=el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth; }"));
    Assert.IsTrue(await Page.Locator(".runtime-performance-panel").EvaluateAllAsync<bool>(
      "panels => panels.length > 0 && panels.every(el => el.scrollWidth <= el.clientWidth + 1)"));
    await Page.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), "ar-runtime-performance-01a0f5c0", "runtime-mobile.png") });
    await Expect(tooltip).ToBeVisibleAsync();
    await help.PressAsync("Escape");
    await Expect(Page.Locator("#settings-dialog")).ToBeVisibleAsync();
    await Page.SetViewportSizeAsync(1280, 720);
    // Force a tooltip across the sidebar to check actual painting, not z-index values.
    var oldHelp = Page.Locator("[aria-label='Help: model performance']");
    await oldHelp.ScrollIntoViewIfNeededAsync();
    await oldHelp.FocusAsync();
    await Expect(tooltip).ToBeVisibleAsync();
    await tooltip.EvaluateAsync("el => { const n=document.querySelector('#settings-navigation').getBoundingClientRect(); el.style.left=(n.left+20)+'px'; el.style.top=(n.top+20)+'px'; el.style.pointerEvents='auto'; }");
    Assert.IsTrue(await tooltip.EvaluateAsync<bool>("el => { const r=el.getBoundingClientRect(); return document.elementFromPoint(r.left+10,r.top+10) === el; }"));
    await Page.Locator("#close-settings").ClickAsync();
    await Expect(tooltip).ToBeHiddenAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimePerformanceToolsAndBenchmarkIdentityIncludeConfiguration()
  {
    await Page.GotoAsync("/");
    await ConfigureAsync(4, 256);
    using var execute = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream",
      new
      {
        message = "native create host batch files",
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        executionStrategy = "direct",
        harness = "native",
        approvalPolicy = "auto",
        browserSessionId = "runtime-performance-tools"
      });
    execute.EnsureSuccessStatusCode();
    var events = await execute.Content.ReadAsStringAsync();
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request =>
      request.Model == "alpha:latest" && !request.Stream && request.DraftTokens == 4 && request.BatchSize == 256));
    Assert.IsTrue(events.Contains("runtime.performance-configured", StringComparison.Ordinal), "Structured native activity did not report the configured runtime options.");

    await ConfigureAsync(4, 256, "command-r:latest");
    var toolEvents = await ExecuteHarnessStreamAsync("native", "execute create README", "runtime-performance-native-tools", "command-r:latest");
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request =>
      request.Model == "command-r:latest" && request.HasTools && request.DraftTokens == 4 && request.BatchSize == 256));
    Assert.IsTrue(toolEvents.Any(item => item["type"]!.GetValue<string>() == "runtime.performance-configured"));
    await ConfigureAsync(4, 256);
    var externalEvents = await ExecuteCodexStreamAsync("Report runtime configuration without modifying files.", "runtime-performance-compatibility");
    Assert.IsTrue(externalEvents.Any(item => item["type"]!.GetValue<string>() == "runtime.performance-unavailable"));

    using var client = new HttpClient { BaseAddress = _environment.BaseUri, Timeout = TimeSpan.FromSeconds(30) };
    async Task<BenchmarkSuiteRunResult> RunAsync()
    {
      using var response = await client.PostAsJsonAsync("api/benchmarks/suite-runs",
        new BenchmarkSuiteRunRequest("alpha:latest", ["codex"],
          SuiteId: BenchmarkSuiteIds.Manual, SuiteVersion: BenchmarkSuiteIds.ManualVersion,
          TimeoutSeconds: 20, ModelExecutionPermissionGranted: true, BenchmarkMode: BenchmarkModeIds.Manual,
          CustomPrompt: "Write a concise implementation report without modifying files."));
      response.EnsureSuccessStatusCode();
      return (await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>())!;
    }
    var first = await RunAsync();
    await ConfigureAsync(0, 512);
    var second = await RunAsync();
    Assert.IsNotNull(first.Configuration);
    Assert.IsNotNull(second.Configuration);
    Assert.AreNotEqual(first.Configuration.Fingerprint, second.Configuration.Fingerprint);

    var comparison = await client.GetFromJsonAsync<JsonObject>(
      $"api/benchmarks/comparisons?baselineRunId={first.RunId}&candidateRunId={second.RunId}");
    Assert.IsNotNull(comparison);
    StringAssert.Contains(comparison.ToJsonString(), "Relevant benchmark configuration differs.");
  }

  [TestMethod]
  [DataRow("auto")]
  [DataRow("bf16")]
  [DataRow("")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task InvalidModelCacheDoesNotPartiallySaveSettings(string cache)
  {
    await OpenRuntimeAsync();
    var before = await SettingsAsync();
    var draft = before.DeepClone();
    draft["ollamaRuntime"]!["modelOverrides"] = new JsonArray(new JsonObject
    {
      ["provider"] = "ollama-local",
      ["model"] = "alpha:latest",
      ["digest"] = "sha256:alpha",
      ["performance"] = new JsonObject { ["kvCacheType"] = cache },
      ["overrides"] = new JsonObject()
    });
    using var response = await _environment.HttpClient.PutAsJsonAsync("api/settings", draft);
    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    StringAssert.Contains(await response.Content.ReadAsStringAsync(), "kvCacheType");
    Assert.IsTrue(JsonNode.DeepEquals(before, await SettingsAsync()));
  }

  private async Task OpenRuntimeAsync()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await Page.Locator("#runtime-performance-model").SelectOptionAsync("alpha:latest");
    await Expect(Page.Locator("#runtime-performance-capabilities")).Not.ToHaveTextAsync("Checking model capabilities…");
  }

  private async Task SelectRuntimeModelAsync(string model)
  {
    await Page.Locator("#runtime-performance-model").SelectOptionAsync(model);
    await Expect(Page.Locator("#runtime-performance-capabilities")).Not.ToHaveTextAsync("Checking model capabilities…");
  }

  private async Task<JsonObject> SettingsAsync() =>
    (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;

  private async Task ConfigureAsync(int? draft, int? batch, string model = "alpha:latest")
  {
    var settings = await SettingsAsync();
    var models = await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/models");
    var digest = models!["models"]!.AsArray().First(item => item!["name"]!.GetValue<string>() == model)!["digest"]!.GetValue<string>();
    settings["ollamaRuntime"]!["modelOverrides"] = new JsonArray(new JsonObject
    {
      ["provider"] = "ollama-local",
      ["model"] = model,
      ["digest"] = digest,
      ["overrides"] = new JsonObject(),
      ["performance"] = new JsonObject { ["draftTokens"] = draft, ["batchSize"] = batch }
    });
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    saved.EnsureSuccessStatusCode();
  }
}
