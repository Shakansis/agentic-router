using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class SettingsValidationEndToEndTests : ChatEndToEndTestBase<SettingsValidationEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ErrorLinksSelectTheExactProfileFocusItsFieldAndPreserveDrafts()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    await Page.Locator("#ollama-url").FillAsync(_environment.BaselineSettings.OllamaUrl + "/");
    await Page.Locator("[data-settings-target=inference]").ClickAsync();
    await Page.Locator("#inference-profile-selector").SelectOptionAsync("general-chat");
    await Page.Locator("#inference-temperature").FillAsync("3");
    await Page.Locator("#inference-profile-selector").SelectOptionAsync("documentation");
    await Page.Locator("#inference-temperature").FillAsync("4");
    // The shared visible input is valid; the API validates the other profile drafts.
    await Page.Locator("#inference-profile-selector").SelectOptionAsync("rpg-storytelling");
    await Page.Locator("#save-settings").ClickAsync();
    var first = Page.Locator("[data-settings-error-field='inferenceProfiles.general-chat.temperature']");
    var second = Page.Locator("[data-settings-error-field='inferenceProfiles.documentation.temperature']");
    await Expect(first).ToBeVisibleAsync();
    await Expect(second).ToContainTextAsync("documentation");
    await first.ClickAsync();
    await Expect(Page.Locator("#inference-temperature")).ToBeFocusedAsync();
    await Expect(Page.Locator("#inference-temperature")).ToHaveValueAsync("3");
    await Expect(Page.Locator("#inference-temperature")).ToHaveAttributeAsync("aria-invalid", "true");
    var description = await Page.Locator("#inference-temperature").GetAttributeAsync("aria-describedby");
    await Expect(Page.Locator("#" + description)).ToContainTextAsync("Temperature");
    await second.FocusAsync();
    await second.PressAsync("Enter");
    await Expect(Page.Locator("#inference-profile-selector")).ToHaveValueAsync("documentation");
    await Expect(Page.Locator("#inference-temperature")).ToBeFocusedAsync();
    await Expect(Page.Locator("#inference-temperature")).ToHaveValueAsync("4");
    await Page.Locator("#inference-temperature").FillAsync("0.4");
    await Expect(second).ToHaveCountAsync(0);
    await Expect(first).ToBeVisibleAsync();
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    await Expect(Page.Locator("#ollama-url")).ToHaveValueAsync(_environment.BaselineSettings.OllamaUrl + "/");
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RuntimeRoleErrorLinkOpensTheSectionAndMarksTheExactControl()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await Page.Locator("#runtime-role-primary-targetContextTokens").FillAsync("65536");
    await Page.Locator("#runtime-role-primary-maximumContextTokens").FillAsync("32768");
    await Page.Locator("#save-settings").ClickAsync();
    var link = Page.Locator("[data-settings-error-field^='ollamaRuntime.roleDefaults.primary']").First;
    await Expect(link).ToBeVisibleAsync();
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    await link.ClickAsync();
    await Expect(Page.Locator("#settings-dialog")).ToHaveAttributeAsync("data-section", "ollama-context");
    await Expect(Page.Locator("#runtime-role-primary-targetContextTokens")).ToHaveAttributeAsync("aria-invalid", "true");
    await Expect(Page.Locator("#runtime-role-primary-targetContextTokens")).ToBeFocusedAsync();
    await Expect(Page.Locator("#runtime-role-primary-targetContextTokens")).ToBeInViewportAsync();
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
    var output = Path.Combine(_environment.RepositoryRoot, ".artifacts", "settings-validation");
    Directory.CreateDirectory(output);
    await Page.ScreenshotAsync(new() { Path = Path.Combine(output, "runtime-role-error.png") });
  }

  private async Task SeedModelSettingsAsync(params string[] names)
  {
    var models = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/models"))!["models"]!.AsArray();
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    var overrides = settings["ollamaRuntime"]!["modelOverrides"]!.AsArray();
    foreach (var name in names)
    {
      var model = models.Single(item => item!["name"]!.GetValue<string>() == name)!;
      overrides.Add(new JsonObject
      {
        ["provider"] = "ollama-local",
        ["model"] = name,
        ["digest"] = model["digest"]!.DeepClone(),
        ["overrides"] = new JsonObject(),
        ["performance"] = new JsonObject { ["batchSize"] = 256 }
      });
      settings["modelGpuAffinities"]![name] = "auto";
    }
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    saved.EnsureSuccessStatusCode();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SavingPrunesRemovedModelsWithoutErasingInstalledModelConfiguration()
  {
    await SeedModelSettingsAsync("qwen3.8:27b-gpu0", "command-r:latest", "alpha:latest");
    _environment.FakeOllama.HideInstalledModel("qwen3.8:27b-gpu0");
    _environment.FakeOllama.HideInstalledModel("command-r:latest");
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("#ollama-url").FillAsync(_environment.BaselineSettings.OllamaUrl);
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    var remaining = settings["ollamaRuntime"]!["modelOverrides"]!.AsArray();
    Assert.HasCount(1, remaining);
    Assert.AreEqual("alpha:latest", remaining[0]!["model"]!.GetValue<string>());
    Assert.AreEqual(256, remaining[0]!["performance"]!["batchSize"]!.GetValue<int>());
    Assert.IsTrue(settings["modelGpuAffinities"]!.AsObject().ContainsKey("alpha:latest"));
    Assert.IsFalse(settings["modelGpuAffinities"]!.AsObject().ContainsKey("command-r:latest"));
    await Expect(Page.Locator("#settings-errors")).ToBeHiddenAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OfflineOllamaWithAvailableCloudDoesNotPruneLocalSettings()
  {
    await SeedModelSettingsAsync("alpha:latest");
    await ConnectFakeCloudAsync("groq", "gsk_cleanup_offline_fixture");
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    _environment.FakeOllama.ModelDiscoveryUnavailable = true;
    await Page.Locator("#ollama-url").FillAsync(_environment.BaselineSettings.OllamaUrl);
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Save failed");
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SwitchingOllamaEndpointDoesNotCountAsDeletingItsModels()
  {
    await SeedModelSettingsAsync("alpha:latest");
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    await using var other = FakeOllamaServer.Start();
    other.HideInstalledModels();
    var settings = JsonNode.Parse(before)!;
    settings["ollamaUrl"] = other.BaseUrl;
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, saved.StatusCode);
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ReplacedDigestPrunesOnlyTheObsoleteRevision()
  {
    await SeedModelSettingsAsync("alpha:latest");
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    var current = settings["ollamaRuntime"]!["modelOverrides"]![0]!;
    var digest = current["digest"]!.GetValue<string>();
    var obsolete = current.DeepClone();
    obsolete["digest"] = "obsolete-digest";
    settings["ollamaRuntime"]!["modelOverrides"]!.AsArray().Add(obsolete);
    // Existing persisted configuration from before the tag's revision changed.
    await File.WriteAllTextAsync(_environment.SettingsPath, settings.ToJsonString());
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("#ollama-url").FillAsync(_environment.BaselineSettings.OllamaUrl);
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    var saved = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    var remaining = saved["ollamaRuntime"]!["modelOverrides"]!.AsArray();
    Assert.HasCount(1, remaining);
    Assert.AreEqual(digest, remaining[0]!["digest"]!.GetValue<string>());
    Assert.IsTrue(saved["modelGpuAffinities"]!.AsObject().ContainsKey("alpha:latest"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task InstalledModelsFilteredFromRoutingAreNotTreatedAsDeleted()
  {
    var tags = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>(_environment.FakeOllama.BaseUrl + "/api/tags"))!["models"]!.AsArray();
    var model = tags.Single(item => item!["name"]!.GetValue<string>() == "functiongemma:270m")!;
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    settings["ollamaRuntime"]!["modelOverrides"]!.AsArray().Add(new JsonObject
    {
      ["provider"] = "ollama-local",
      ["model"] = "functiongemma:270m",
      ["digest"] = model["digest"]!.DeepClone(),
      ["overrides"] = new JsonObject(),
      ["performance"] = new JsonObject { ["batchSize"] = 256 }
    });
    await File.WriteAllTextAsync(_environment.SettingsPath, settings.ToJsonString());
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("#ollama-url").FillAsync(_environment.BaselineSettings.OllamaUrl);
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Save failed");
    var saved = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    Assert.AreEqual("functiongemma:270m", saved["ollamaRuntime"]!["modelOverrides"]![0]!["model"]!.GetValue<string>());
    var link = Page.Locator("[data-settings-error-field='ollamaRuntime.modelOverrides.0']");
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    await link.ClickAsync();
    await Expect(Page.Locator("#settings-override-identity-0")).ToBeFocusedAsync();
    await Expect(Page.Locator("#settings-override-identity-0")).ToHaveAttributeAsync("aria-invalid", "true");
    await Expect(Page.Locator("#settings-override-identity-0")).ToBeInViewportAsync();
    var output = Path.Combine(_environment.RepositoryRoot, ".artifacts", "settings-validation");
    Directory.CreateDirectory(output);
    await Page.ScreenshotAsync(new() { Path = Path.Combine(output, "model-identity-error.png") });
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task IndexedOverrideErrorFocusesItsContextInputInsideClosedDetails()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=ollama-context]").ClickAsync();
    await Page.Locator("#runtime-override-model").EvaluateAsync("element => element.closest('details').open = true");
    await Page.Locator("#runtime-override-model").SelectOptionAsync("alpha:latest");
    await Page.Locator("#runtime-override-role").SelectOptionAsync("primary");
    await Expect(Page.Locator("#runtime-override-source")).ToContainTextAsync("role defaults");
    await Page.Locator("#runtime-override-target").FillAsync("65536");
    await Page.Locator("#runtime-override-maximum").FillAsync("32768");
    await Page.Locator("#save-runtime-override").ClickAsync();
    await Expect(Page.Locator("#runtime-override-source")).ToContainTextAsync("Model-specific context override");
    await Page.Locator("#save-settings").ClickAsync();
    var link = Page.Locator("[data-settings-error-field='ollamaRuntime.modelOverrides.0.overrides.primary']");
    await Expect(link).ToBeVisibleAsync();
    await Page.Locator("#runtime-override-model").EvaluateAsync("element => element.closest('details').open = false");
    await Page.Locator("[data-settings-target=general]").ClickAsync();
    await link.ClickAsync();
    await Expect(Page.Locator("#runtime-override-target")).ToBeFocusedAsync();
    await Expect(Page.Locator("#runtime-override-target")).ToHaveValueAsync("65536");
    await Expect(Page.Locator("#runtime-override-target")).ToHaveAttributeAsync("aria-invalid", "true");
    await Expect(Page.Locator("#runtime-override-target")).ToBeInViewportAsync();
  }
}
