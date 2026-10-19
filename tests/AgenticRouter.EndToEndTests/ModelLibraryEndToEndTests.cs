using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class ModelLibraryEndToEndTests : ChatEndToEndTestBase<ModelLibraryEndToEndTests>
{
  private async Task OpenLibraryAsync()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target='models-routing']").ClickAsync();
    await Page.Locator(".model-organization-panel").First.Locator("summary").ClickAsync();
  }

  private async Task ConfigureCliAsync()
  {
    var configured = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_E2E_FAKE_OLLAMA_PATH");
    var executable = string.IsNullOrWhiteSpace(configured)
      ? Path.Combine(_environment.RepositoryRoot, "tests", "FakeOllamaCli", "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", OperatingSystem.IsWindows() ? "FakeOllamaCli.exe" : "FakeOllamaCli")
      : configured;
    await _environment.SetManagedOllamaAndRestartAsync(executable);
  }

  private async Task InstallAsync(string name)
  {
    await Page.Locator("#model-install-name").FillAsync(name);
    await Page.Locator("#model-install-button").ClickAsync();
    await Expect(Page.Locator("#model-library-status")).ToContainTextAsync("is installed", new() { Timeout = 30_000 });
    await Expect(Page.Locator("#model-install-button")).ToBeEnabledAsync();
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task InstallOrganizeCancelAndRemoveThroughCliPreservesSettingsAndHistory()
  {
    await ConfigureCliAsync();
    var before = await _environment.HttpClient.GetStringAsync("api/settings");
    await OpenLibraryAsync();
    await InstallAsync("library-fixture");
    var card = Page.Locator("[data-model-identity='library-fixture:latest']");
    await Expect(card).ToBeVisibleAsync();
    await card.GetByLabel("Local alias", new() { Exact = false }).FillAsync("Story model");
    await card.GetByLabel("Note", new() { Exact = false }).FillAsync("Used for prose");
    await card.Locator("[data-model-organization-action='save']").ClickAsync();
    await Expect(card.Locator("strong")).ToHaveTextAsync("Story model");
    await card.Locator("[data-model-organization-action='remove']").ClickAsync();
    await Expect(Page.Locator("#app-modal-message")).ToContainTextAsync("library-fixture:latest");
    await Page.Locator("#app-modal-cancel").ClickAsync();
    await Expect(card.Locator("[data-model-organization-action='remove']")).ToBeVisibleAsync();
    await card.Locator("[data-model-organization-action='remove']").ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(Page.Locator("#model-library-status")).ToContainTextAsync("was removed", new() { Timeout = 30_000 });
    await Expect(card).ToContainTextAsync("unavailable");
    await Expect(card.Locator("[data-model-note]")).ToHaveValueAsync("Used for prose");
    await Expect(card.Locator("[data-model-organization-action='remove']")).ToHaveCountAsync(0);
    var models = await _environment.HttpClient.GetStringAsync("api/models");
    Assert.DoesNotContain("library-fixture:latest", models);
    Assert.AreEqual(before, await _environment.HttpClient.GetStringAsync("api/settings"));
    await InstallAsync("library-fixture");
    await Expect(card.Locator("strong")).ToHaveTextAsync("Story model");
    await Expect(card.Locator("[data-model-organization-action='remove']")).ToBeVisibleAsync();
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task FailedDownloadAndCliFailuresRemainActionableAndDoNotClaimSuccess()
  {
    await ConfigureCliAsync();
    await OpenLibraryAsync();
    await Page.Locator("#model-install-name").FillAsync("missing-library-model:latest");
    await Page.Locator("#model-install-button").ClickAsync();
    await Expect(Page.Locator("#model-library-status")).ToHaveAttributeAsync("data-state", "error");
    await Expect(Page.Locator("#model-install-button")).ToBeEnabledAsync();
    foreach (var name in new[] { "remove-failure:latest", "remove-unverified:latest" })
    {
      await InstallAsync(name);
      var card = Page.Locator($"[data-model-identity='{name}']");
      await card.Locator("[data-model-organization-action='remove']").ClickAsync();
      await ConfirmAppModalAsync();
      await Expect(Page.Locator("#model-library-status")).ToHaveAttributeAsync("data-state", "error");
      await Expect(card.Locator("[data-model-organization-action='remove']")).ToBeEnabledAsync();
      Assert.Contains(name, await _environment.HttpClient.GetStringAsync("api/models"));
    }
    await Expect(Page.Locator("#model-library-status")).ToContainTextAsync("still installed");
    using var invalid = await _environment.HttpClient.PostAsJsonAsync("api/setup/models/remove", new { model = "alpha:latest; whoami" });
    Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    using var invalidPull = await _environment.HttpClient.PostAsJsonAsync("api/setup/models/install", new { model = "--help" });
    Assert.AreEqual(HttpStatusCode.BadRequest, invalidPull.StatusCode);
    using var guarded = await _environment.HttpClient.PostAsJsonAsync("api/setup/models/pull", new { model = "unrecommended:latest" });
    Assert.AreEqual(HttpStatusCode.Conflict, guarded.StatusCode);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DownloadShowsProviderProgressAndContinuesWhileSettingsAreClosed()
  {
    await OpenLibraryAsync();
    await Page.Locator("#model-install-name").FillAsync("slow-library:latest");
    await Page.Locator("#model-install-button").ClickAsync();
    await Expect(Page.Locator("#model-install-progress")).ToHaveAttributeAsync("value", "500000");
    await Expect(Page.Locator("#model-install-progress")).ToHaveAttributeAsync("max", "1000000");
    await Expect(Page.Locator("#model-install-button")).ToBeDisabledAsync();
    using var overlappingRemoval = await _environment.HttpClient.PostAsJsonAsync("api/setup/models/remove", new { model = "slow-library:latest" });
    Assert.AreEqual(HttpStatusCode.Conflict, overlappingRemoval.StatusCode);
    Assert.Contains("model-operation-active", await overlappingRemoval.Content.ReadAsStringAsync());
    await Page.Locator("#close-settings").ClickAsync();
    await Expect(Page.Locator("#settings-dialog")).ToBeHiddenAsync();
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target='models-routing']").ClickAsync();
    await Expect(Page.Locator("#model-library-status")).ToContainTextAsync("is installed", new() { Timeout = 15_000 });
    await Expect(Page.Locator("[data-model-identity='slow-library:latest']")).ToBeVisibleAsync();
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task LastUseComesFromSuccessfulInferenceAndSurvivesApplicationRestart()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    var started = DateTimeOffset.UtcNow.AddSeconds(-1);
    await SendMessageAsync("Hello, reply briefly.");
    var first = JsonNode.Parse(await _environment.HttpClient.GetStringAsync("api/model-organization"))!["models"]!.AsArray()
      .Single(model => model!["qualifiedId"]!.GetValue<string>() == "alpha:latest")!;
    var used = first["lastUsedAt"]!.GetValue<DateTimeOffset>();
    Assert.IsTrue(used >= started && used <= DateTimeOffset.UtcNow);
    await _environment.RestartApplicationAsync();
    await OpenLibraryAsync();
    var card = Page.Locator("[data-model-identity='alpha:latest']");
    await Expect(card.Locator(".model-last-used")).ToHaveAttributeAsync("data-last-used-at", used.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz"));
    await Expect(Page.Locator("[data-model-identity='beta:code'] .model-last-used")).ToContainTextAsync("no recorded use");
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task LibraryFieldsFiltersAndLayoutStayUsableAtDesktopAndNarrowWidths()
  {
    await OpenLibraryAsync();
    await Page.Locator("[data-settings-target='general']").ClickAsync();
    await Page.Locator("#default-gpu").SelectOptionAsync("ollama:1");
    await Page.Locator("[data-settings-target='models-routing']").ClickAsync();
    await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Browse Ollama" })).ToHaveAttributeAsync("href", "https://ollama.com/search");
    await Page.Locator("#model-filter-search").FillAsync("alpha");
    await Expect(Page.Locator(".model-organization-card")).ToHaveCountAsync(1);
    await Page.Locator("#model-filter-favorites").CheckAsync();
    await Expect(Page.Locator("#model-organization-list")).ToContainTextAsync("No model matches");
    await Page.Locator("#model-filter-favorites").UncheckAsync();
    var card = Page.Locator("[data-model-identity='alpha:latest']");
    await card.Locator("[data-model-organization-action='favorite']").ClickAsync();
    await Expect(card.Locator("[data-model-organization-action='favorite']")).ToHaveTextAsync("Remove favorite");
    await Expect(Page.Locator("#default-gpu")).ToHaveValueAsync("ollama:1");
    await card.Locator("[data-model-gpu-affinity]").SelectOptionAsync("device:GPU-nvidia-4090-fixture");
    foreach (var width in new[] { 1600, 1280, 760, 390 })
    {
      await Page.SetViewportSizeAsync(width, 1000);
      await Page.Locator(".model-library-install").ScrollIntoViewIfNeededAsync();
      Assert.IsTrue(await Page.Locator(".model-organization-panel").First.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"), $"Panel overflow at {width}");
      foreach (var input in new[] { "#model-install-name", "#model-filter-search", "#model-filter-location", "[data-model-alias]", "[data-model-note]" })
      {
        var box = await Page.Locator(input).BoundingBoxAsync();
        Assert.IsNotNull(box);
        Assert.IsGreaterThanOrEqualTo(30, box.Height, $"Input too short: {input} at {width}");
      }
      var path = Path.Combine(_environment.RepositoryRoot, ".artifacts", "rpg-editor", "visual-review", $"model-library-{width}.png");
      await Page.ScreenshotAsync(new() { Path = path });
      await card.ScrollIntoViewIfNeededAsync();
      Assert.IsTrue(await card.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
      await Page.ScreenshotAsync(new() { Path = path.Replace("model-library-", "model-library-card-", StringComparison.Ordinal) });
    }
  }
}
