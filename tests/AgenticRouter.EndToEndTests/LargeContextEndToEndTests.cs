using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class LargeContextEndToEndTests : ChatEndToEndTestBase<LargeContextEndToEndTests>
{
  [TestMethod]
  [DataRow(false, 131072, 262144)]
  [DataRow(false, 524288, 262144)]
  [DataRow(true, 131072, 262144)]
  [DataRow(true, 524288, 262144)]
  [DataRow(true, 10000000, 10000000)]
  [DataRow(true, 1000000000, 1000000000)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task SelectedRoleContextUsesOnlyModelCapacityWithoutChangingGlobalSettings(bool exactOverride, int modelMaximum, int requested)
  {
    _environment.FakeOllama.SetDeclaredContext("alpha:latest", modelMaximum);
    await _environment.RestartApplicationAsync();
    var before = await SettingsAsync();
    var providerCeiling = before["context"]!["providerContextTokens"]!.GetValue<int>();
    Assert.IsLessThan(65536, providerCeiling);
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    foreach (var role in new[] { "specialist", "primary", "webSearchSynthesis", "visionRequest" })
    {
      await Page.Locator($"#runtime-role-{role}-targetContextTokens").FillAsync(requested.ToString());
      await Page.Locator($"#runtime-role-{role}-maximumContextTokens").FillAsync(requested.ToString());
    }
    if (exactOverride)
    {
      await Page.Locator("#runtime-override-model").EvaluateAsync("el => el.closest('details').open = true");
      await Page.Locator("#runtime-override-model").SelectOptionAsync("alpha:latest");
      await Page.Locator("#runtime-override-role").SelectOptionAsync("primary");
      await Page.Locator("#runtime-override-minimum").FillAsync("131072");
      await Page.Locator("#runtime-override-target").FillAsync(requested.ToString());
      await Page.Locator("#runtime-override-maximum").FillAsync(requested.ToString());
      await Page.Locator("#runtime-override-output").FillAsync("4096");
      await Page.Locator("#save-runtime-override").ClickAsync();
    }
    var response = await Page.RunAndWaitForResponseAsync(() => Page.Locator("#save-settings").ClickAsync(),
      response => response.Url.EndsWith("/api/settings", StringComparison.Ordinal) && response.Request.Method == "PUT");
    Assert.AreEqual(200, response.Status, await response.TextAsync());
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    var saved = await SettingsAsync();
    Assert.IsTrue(JsonNode.DeepEquals(before["context"], saved["context"]));
    foreach (var role in new[] { "specialist", "primary", "webSearchSynthesis", "visionRequest" })
      Assert.AreEqual(requested, saved["ollamaRuntime"]!["roleDefaults"]![role]!["targetContextTokens"]!.GetValue<int>());
    var yaml = await _environment.HttpClient.GetStringAsync("api/settings/yaml");
    await _environment.RestartApplicationAsync();
    using var imported = await _environment.HttpClient.PutAsJsonAsync("api/settings/yaml", new { yaml });
    Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
    var restored = await SettingsAsync();
    Assert.IsTrue(JsonNode.DeepEquals(saved["ollamaRuntime"], restored["ollamaRuntime"]));
    Assert.IsTrue(JsonNode.DeepEquals(before["context"], restored["context"]));
    await Page.ReloadAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Hello, reply briefly.");
    var effective = Math.Min(requested, modelMaximum);
    Assert.AreEqual(effective, _environment.FakeOllama.Requests.Last(request => request.Stream && request.Model == "alpha:latest").ContextTokens);
    await Page.Locator("#context-usage-summary").ClickAsync();
    if (requested > modelMaximum)
      await Expect(Page.Locator("#context-usage-warning")).ToContainTextAsync("Requested 262,144 tokens; effective 131,072 tokens (model maximum)");
    else
      await Expect(Page.Locator("#context-usage-warning")).ToBeHiddenAsync();
    await Expect(Page.Locator("#context-usage-limit-details")).ToContainTextAsync(effective.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")));
    if (requested >= 1000000)
      await Expect(Page.Locator("#context-usage-summary")).ToContainTextAsync(requested == 1000000000 ? "1.0B" : "10.0M");
    Assert.IsTrue(await Page.Locator(".context-usage-popover").EvaluateAsync<bool>("el => el.getBoundingClientRect().top >= 12"));
    await Page.ScreenshotAsync(new() { Path = Path.Combine(_environment.RepositoryRoot, ".artifacts", "rpg-editor", "visual-review", $"selected-context-{exactOverride}-{modelMaximum}-{requested}.png") });
    var invalid = restored.DeepClone();
    var profile = exactOverride
      ? invalid["ollamaRuntime"]!["modelOverrides"]![0]!["overrides"]!["primary"]!
      : invalid["ollamaRuntime"]!["roleDefaults"]!["primary"]!;
    profile["targetContextTokens"] = 2048;
    profile["minimumContextTokens"] = 8192;
    using var rejected = await _environment.HttpClient.PutAsJsonAsync("api/settings", invalid);
    Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
    Assert.Contains("Received minimum 8192, target 2048", await rejected.Content.ReadAsStringAsync());
    Assert.IsTrue(JsonNode.DeepEquals(restored, await SettingsAsync()));
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task MillionTokenSettingsPersistAndReachOllamaWithoutBypassingModelLimits()
  {
    const string largeModel = "qwen3.8:27b-gpu0";
    _environment.FakeOllama.SetDeclaredContext(largeModel, 1_048_576);
    await _environment.RestartApplicationAsync();
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    foreach (var value in new[] { 250_000, 2_000_000, 1_048_576 })
    {
      await Page.Locator("[data-settings-target=harnesses]").ClickAsync();
      await Page.Locator("#default-context-tokens").FillAsync(value.ToString());
      await Page.Locator("#provider-context-tokens").FillAsync(value.ToString());
      await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
      foreach (var role in new[] { "specialist", "primary", "modelTest" })
      {
        await Page.Locator($"#runtime-role-{role}-targetContextTokens").FillAsync(value.ToString());
        await Page.Locator($"#runtime-role-{role}-maximumContextTokens").FillAsync(value.ToString());
        Assert.IsTrue(await Page.Locator($"#runtime-role-{role}-maximumContextTokens").EvaluateAsync<bool>("el => el.checkValidity()"));
      }
      var response = await Page.RunAndWaitForResponseAsync(() => Page.Locator("#save-settings").ClickAsync(),
        response => response.Url.EndsWith("/api/settings", StringComparison.Ordinal) && response.Request.Method == "PUT");
      Assert.AreEqual(200, response.Status, await response.TextAsync());
      await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
      var saved = await SettingsAsync();
      Assert.AreEqual(value, saved["context"]!["providerContextTokens"]!.GetValue<int>());
      Assert.AreEqual(value, saved["ollamaRuntime"]!["roleDefaults"]!["specialist"]!["maximumContextTokens"]!.GetValue<int>());
    }
    await Page.Locator("#runtime-override-model").EvaluateAsync("el => el.closest('details').open = true");
    await Page.Locator("#runtime-override-model").SelectOptionAsync(largeModel);
    await Page.Locator("#runtime-override-role").SelectOptionAsync("primary");
    await Page.Locator("#runtime-override-target").FillAsync("1048576");
    await Page.Locator("#runtime-override-maximum").FillAsync("1048576");
    await Page.Locator("#runtime-override-output").FillAsync("64000");
    await Page.Locator("#save-runtime-override").ClickAsync();
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    var configured = await SettingsAsync();
    configured["ollamaRuntime"]!["contextEscalationLadder"] = new JsonArray(4096, 32768, 131072, 262144, 524288, 1048576);
    using var ladderSaved = await _environment.HttpClient.PutAsJsonAsync("api/settings", configured);
    ladderSaved.EnsureSuccessStatusCode();
    var yaml = await _environment.HttpClient.GetStringAsync("api/settings/yaml");
    Assert.Contains("1048576", yaml);
    await _environment.RestartApplicationAsync();
    using var imported = await _environment.HttpClient.PutAsJsonAsync("api/settings/yaml", new { yaml });
    imported.EnsureSuccessStatusCode();
    var reloaded = await SettingsAsync();
    Assert.IsTrue(JsonNode.DeepEquals(configured["context"], reloaded["context"]));
    Assert.IsTrue(JsonNode.DeepEquals(configured["ollamaRuntime"], reloaded["ollamaRuntime"]));
    await Page.ReloadAsync();
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await Expect(Page.Locator("#runtime-role-specialist-maximumContextTokens")).ToHaveValueAsync("1048576");
    await Page.Locator("#runtime-role-specialist-maximumContextTokens").ScrollIntoViewIfNeededAsync();
    var screenshot = Path.Combine(_environment.RepositoryRoot, ".artifacts", "rpg-editor", "visual-review", "million-token-context.png");
    await Page.ScreenshotAsync(new() { Path = screenshot });
    await Page.Locator("#close-settings").ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync(largeModel);
    await SendMessageAsync("Hello, reply briefly.");
    Assert.AreEqual(1_048_576, _environment.FakeOllama.Requests.Last(request => request.Stream && request.Model == largeModel).ContextTokens);
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Hello again, reply briefly.");
    Assert.AreEqual(65_536, _environment.FakeOllama.Requests.Last(request => request.Stream && request.Model == "alpha:latest").ContextTokens);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LargeContextValidatesOrderingButAllowsPreferencesAboveModelCapability()
  {
    var before = await SettingsAsync();
    foreach (var field in new[] { "defaultContextTokens", "providerContextTokens" })
    {
      var invalid = before.DeepClone();
      invalid["context"]![field] = 1023;
      using var response = await _environment.HttpClient.PutAsJsonAsync("api/settings", invalid);
      Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
      Assert.Contains(field, await response.Content.ReadAsStringAsync());
    }
    var unordered = before.DeepClone();
    unordered["context"]!["providerContextTokens"] = 1_048_576;
    unordered["ollamaRuntime"]!["roleDefaults"]!["specialist"]!["targetContextTokens"] = 1_000_000;
    using var ordering = await _environment.HttpClient.PutAsJsonAsync("api/settings", unordered);
    Assert.AreEqual(HttpStatusCode.BadRequest, ordering.StatusCode);
    var invalidLadder = before.DeepClone();
    invalidLadder["ollamaRuntime"]!["contextEscalationLadder"] = new JsonArray(4096, 1024);
    using var ladder = await _environment.HttpClient.PutAsJsonAsync("api/settings", invalidLadder);
    Assert.AreEqual(HttpStatusCode.BadRequest, ladder.StatusCode);
    Assert.IsTrue(JsonNode.DeepEquals(before, await SettingsAsync()));
    var configuredModel = before.DeepClone();
    configuredModel["context"]!["providerContextTokens"] = 1_048_576;
    configuredModel["ollamaRuntime"]!["modelOverrides"] = new JsonArray(new JsonObject
    {
      ["provider"] = "ollama-local",
      ["model"] = "alpha:latest",
      ["digest"] = "digest-alpha:latest",
      ["overrides"] = new JsonObject
      {
        ["primary"] = new JsonObject
        {
          ["minimumContextTokens"] = 8192,
          ["targetContextTokens"] = 262144,
          ["maximumContextTokens"] = 1000000,
          ["outputTokenLimit"] = 4096,
          ["keepAlive"] = 300
        }
      }
    });
    using var model = await _environment.HttpClient.PutAsJsonAsync("api/settings", configuredModel);
    Assert.AreEqual(HttpStatusCode.OK, model.StatusCode, await model.Content.ReadAsStringAsync());
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Hello, reply briefly.");
    Assert.AreEqual(65_536, _environment.FakeOllama.Requests.Last(request => request.Stream && request.Model == "alpha:latest").ContextTokens);
    await Page.Locator("#context-usage-summary").ClickAsync();
    await Expect(Page.Locator("#context-usage-warning")).ToContainTextAsync("Requested 262,144 tokens; effective 65,536 tokens (model maximum)");
    await Expect(Page.Locator("#context-usage-limit-details")).ToContainTextAsync("262,144");
    await Expect(Page.Locator("#context-usage-limit-details")).ToContainTextAsync("65,536");
    Assert.IsTrue(await Page.Locator(".context-usage-popover").EvaluateAsync<bool>("el => el.getBoundingClientRect().top >= 12"));
    await Page.ScreenshotAsync(new() { Path = Path.Combine(_environment.RepositoryRoot, ".artifacts", "rpg-editor", "visual-review", "requested-effective-context.png") });
    var saved = await SettingsAsync();
    Assert.AreEqual(262144, saved["ollamaRuntime"]!["modelOverrides"]![0]!["overrides"]!["primary"]!["targetContextTokens"]!.GetValue<int>());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ModelMaximumBetweenLadderEntriesIsUsableButOversizedInputStillFails()
  {
    _environment.FakeOllama.SetDeclaredContext("alpha:latest", 12288);
    await _environment.RestartApplicationAsync();
    var configured = await SettingsAsync();
    configured["context"]!["defaultContextTokens"] = 65536;
    configured["context"]!["providerContextTokens"] = 65536;
    foreach (var role in configured["ollamaRuntime"]!["roleDefaults"]!.AsObject())
    {
      role.Value!["minimumContextTokens"] = 1024;
      role.Value["targetContextTokens"] = 8192;
      role.Value["maximumContextTokens"] = 65536;
      role.Value["outputTokenLimit"] = 1024;
    }
    configured["ollamaRuntime"]!["contextEscalationLadder"] = new JsonArray(4096, 8192, 16384, 32768, 65536);
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", configured);
    Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Please reply briefly. " + new string('x', 27000));
    Assert.AreEqual(12288, _environment.FakeOllama.Requests.Last(request => request.Stream && request.Model == "alpha:latest").ContextTokens);
    await Page.Locator("#context-usage-summary").ClickAsync();
    await Expect(Page.Locator("#context-usage-limit-details")).ToContainTextAsync("12,288");
    await Expect(Page.Locator("#context-usage-warning")).Not.ToContainTextAsync("model maximum");
    var calls = _environment.FakeOllama.Requests.Count;
    using var oversized = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream", new { model = "alpha:latest", message = new string('x', 45000) });
    var body = await oversized.Content.ReadAsStringAsync();
    Assert.Contains("request-context-does-not-fit", body);
    Assert.HasCount(calls, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SelectedRoleRetainsHistoryBeyondGlobalDefaultAndIgnoresOtherRoleTargets()
  {
    _environment.FakeOllama.SetDeclaredContext("alpha:latest", 1048576);
    await _environment.RestartApplicationAsync();
    var settings = await SettingsAsync();
    var originalContext = settings["context"]!.DeepClone();
    settings["defaultModel"] = "alpha:latest";
    foreach (var intention in settings["intentions"]!.AsObject()) intention.Value!["model"] = "alpha:latest";
    var primary = settings["ollamaRuntime"]!["roleDefaults"]!["primary"]!;
    primary["targetContextTokens"] = 262144;
    primary["maximumContextTokens"] = 262144;
    var specialist = settings["ollamaRuntime"]!["roleDefaults"]!["specialist"]!;
    specialist["targetContextTokens"] = 1048576;
    specialist["maximumContextTokens"] = 1048576;
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
    var priorUser = "RETAIN-USER " + new string('x', 70000);
    var priorAssistant = "RETAIN-ASSISTANT " + new string('y', 70000);
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream", new
    {
      model = "alpha:latest",
      message = "Continue briefly.",
      interactionMode = "chat",
      history = new[] { new { role = "user", content = priorUser }, new { role = "assistant", content = priorAssistant } }
    });
    var body = await response.Content.ReadAsStringAsync();
    Assert.Contains("response.completed", body);
    var wire = _environment.FakeOllama.Requests.Last(request => request.Stream && request.Model == "alpha:latest");
    Assert.AreEqual(262144, wire.ContextTokens);
    Assert.IsTrue(wire.Messages.Any(message => message.Content == priorUser));
    Assert.IsTrue(wire.Messages.Any(message => message.Content == priorAssistant));
    Assert.IsTrue(JsonNode.DeepEquals(originalContext, (await SettingsAsync())["context"]));
  }

  private async Task<JsonObject> SettingsAsync() => (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
}
