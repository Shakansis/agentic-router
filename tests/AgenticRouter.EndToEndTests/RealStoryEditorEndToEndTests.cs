using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class RealStoryEditorEndToEndTests : PageTest
{
  [TestMethod]
  [TestCategory("RealModels")]
  [DoNotParallelize]
  [Timeout(900_000, CooperativeCancellation = true)]
  public async Task ResidentModelReviewsAndSuggestsThroughBrowserWithoutRestartingOllama()
  {
    var sourceUrl = Environment.GetEnvironmentVariable("AR_STORY_REAL_SOURCE_URL");
    var endpoint = Environment.GetEnvironmentVariable("AR_STORY_REAL_OLLAMA_URL");
    var model = Environment.GetEnvironmentVariable("AR_STORY_REAL_MODEL");
    var output = Environment.GetEnvironmentVariable("AR_STORY_REAL_OUTPUT");
    if (string.IsNullOrWhiteSpace(sourceUrl) || string.IsNullOrWhiteSpace(endpoint)
      || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(output))
      Assert.Inconclusive("Requires explicit real-model authorization, a resident endpoint/model and output directory.");

    using var source = new HttpClient { BaseAddress = new Uri(sourceUrl) };
    using var ollama = new HttpClient { BaseAddress = new Uri(endpoint) };
    var active = await source.GetFromJsonAsync<JsonArray>("api/chat/runs");
    Assert.HasCount(0, active!, "An existing user turn must finish before acceptance.");
    var before = await ollama.GetFromJsonAsync<JsonObject>("api/ps");
    var resident = before!["models"]!.AsArray().SingleOrDefault(item => item!["name"]!.GetValue<string>() == model);
    Assert.IsTrue(resident is not null || Environment.GetEnvironmentVariable("AR_STORY_REAL_ALLOW_LOAD") == "1",
      "Automatic loading requires explicit authorization via AR_STORY_REAL_ALLOW_LOAD=1.");
    var originalSettings = await source.GetFromJsonAsync<JsonObject>("api/settings");
    Directory.CreateDirectory(output);
    await using var environment = await TestEnvironment.StartAsync();
    var settings = await environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings");
    settings!["ollamaUrl"] = endpoint;
    settings["defaultModel"] = model;
    settings["coordinatorModel"] = model;
    settings["defaultGpu"] = "auto";
    settings["modelGpuAffinities"] = new JsonObject();
    foreach (var key in new[] { "ollamaRuntime", "inferenceProfiles", "context", "runtime" })
      settings[key] = originalSettings![key]!.DeepClone();
    foreach (var intention in settings["intentions"]!.AsObject())
    {
      intention.Value!["model"] = "default";
      intention.Value["gpu"] = "default";
    }
    settings["intentions"]!["rpg-storytelling"]!["systemPrompt"] = originalSettings!["intentions"]!["rpg-storytelling"]!["systemPrompt"]!.DeepClone();
    using var saved = await environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    Assert.IsTrue(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
    await Page.SetViewportSizeAsync(1600, 1000);
    await Page.GotoAsync(environment.BaseUri.ToString());
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
    await Page.Locator("#open-story-editor").ClickAsync();
    const string passage = "Clara caminhou pelo corredor antigo. O corredor antigo estava silencioso, e Clara segurava a carta junto ao peito.";
    const string prefix = "# O farol\n\n";
    const string suffix = "\n\nDo lado de fora, o mar batia nas pedras.";
    const string draft = prefix + passage + suffix;
    await Page.Locator("#story-document").FillAsync(draft);
    await SelectAsync(passage);
    await Page.Locator("#story-instruction").FillAsync("Reduza a repetição de corredor antigo. Preserve Clara, a carta, os acontecimentos e o português. Use no máximo duas frases. Não acrescente fatos.");
    var revision = await ProposeAsync("story-revise");
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync(passage);
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(draft);
    var replacement = await Page.Locator("#story-replacement").InputValueAsync();
    Assert.IsFalse(string.IsNullOrWhiteSpace(replacement));
    Assert.AreNotEqual(passage, replacement);
    StringAssert.Contains(replacement, "Clara");
    Assert.IsGreaterThan(0, await Page.Locator("#story-original del, #story-replacement-diff ins").CountAsync());
    Assert.AreEqual(replacement, await Page.Locator("#story-replacement-diff").TextContentAsync());
    await Page.ScreenshotAsync(new() { Path = Path.Combine(output, "review.png") });
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(prefix + replacement + suffix);
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(draft);

    await SelectAsync("antigo");
    await Page.Locator("#story-instruction").FillAsync("Sugira sinônimos em português para antigo que combinem com um corredor de um farol. Retorne apenas as alternativas estruturadas solicitadas.");
    var synonyms = await ProposeAsync("story-synonyms");
    var candidates = Page.Locator("#story-alternatives button");
    Assert.IsGreaterThanOrEqualTo(2, await candidates.CountAsync());
    await candidates.Nth(1).ClickAsync();
    var chosen = await Page.Locator("#story-replacement").InputValueAsync();
    await Expect(Page.Locator("#story-replacement-diff ins")).ToContainTextAsync(chosen);
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(draft);
    await Page.ScreenshotAsync(new() { Path = Path.Combine(output, "synonyms.png") });
    await Page.Locator("#story-apply").ClickAsync();
    var position = draft.IndexOf("antigo", StringComparison.Ordinal);
    var applied = draft[..position] + chosen + draft[(position + "antigo".Length)..];
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(applied);
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview h1")).ToHaveTextAsync("O farol");
    await Page.ScreenshotAsync(new() { Path = Path.Combine(output, "view.png") });
    var download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual(applied, await File.ReadAllTextAsync((await download.PathAsync())!));
    await download.SaveAsAsync(Path.Combine(output, "applied.md"));
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(draft);
    await Expect(Page.Locator("#story-preview")).ToContainTextAsync(passage);
    download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual(draft, await File.ReadAllTextAsync((await download.PathAsync())!));
    await download.SaveAsAsync(Path.Combine(output, "undone.md"));
    var after = await ollama.GetFromJsonAsync<JsonObject>("api/ps");
    var stillResident = after!["models"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == model)!;
    if (resident is not null)
    {
      Assert.AreEqual(resident["digest"]!.GetValue<string>(), stillResident["digest"]!.GetValue<string>());
      Assert.AreEqual(resident["context_length"]!.GetValue<int>(), stillResident["context_length"]!.GetValue<int>());
    }
    await File.WriteAllTextAsync(Path.Combine(output, "acceptance.json"), new JsonObject
    {
      ["model"] = model,
      ["residentBefore"] = resident?.DeepClone(),
      ["automaticLoadAuthorized"] = Environment.GetEnvironmentVariable("AR_STORY_REAL_ALLOW_LOAD") == "1",
      ["residentAfter"] = stillResident.DeepClone(),
      ["revision"] = revision,
      ["synonyms"] = synonyms,
      ["appliedSynonym"] = chosen,
      ["tokenEstimate"] = await Page.Locator("#story-token-estimate").InnerTextAsync(),
      ["checks"] = "comparison leaves draft unchanged; exact selection application; revision and synonym undo; View rendering; exact applied and undone Markdown exports"
    }.ToJsonString(new() { WriteIndented = true }));
    return;

    async Task SelectAsync(string text) => await Page.Locator("#story-document").EvaluateAsync("""
      (element, text) => { const start = element.value.indexOf(text); element.focus();
        element.setSelectionRange(start, start + text.length); element.dispatchEvent(new Event('select')); }
      """, text);

    async Task<JsonObject> ProposeAsync(string button)
    {
      var watch = Stopwatch.StartNew();
      var response = await Page.RunAndWaitForResponseAsync(() => Page.Locator("#" + button).ClickAsync(),
        response => response.Url.EndsWith("/api/story-editor/proposals", StringComparison.Ordinal), new() { Timeout = 330_000 });
      var body = await response.TextAsync();
      Assert.AreEqual(200, response.Status, body);
      var result = JsonNode.Parse(body)!.AsObject();
      Assert.AreEqual(model, result["model"]!.GetValue<string>());
      await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
      result["elapsedMilliseconds"] = watch.ElapsedMilliseconds;
      await File.WriteAllTextAsync(Path.Combine(output, button + ".json"), result.ToJsonString(new() { WriteIndented = true }));
      return result;
    }
  }
}
