using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class RealToolWorkspaceEndToEndTests : PageTest
{
  [TestMethod]
  [TestCategory("RealModels")]
  [DoNotParallelize]
  [Timeout(600_000, CooperativeCancellation = true)]
  public async Task LocalModelProposesThroughSharedComposerBeforeExplicitApply()
  {
    var endpoint = Environment.GetEnvironmentVariable("AR_TOOL_REAL_OLLAMA_URL");
    var model = Environment.GetEnvironmentVariable("AR_TOOL_REAL_MODEL");
    var output = Environment.GetEnvironmentVariable("AR_TOOL_REAL_OUTPUT");
    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(output))
      Assert.Inconclusive("Requires explicit local-model authorization, endpoint, installed model and output directory.");
    await using var environment = await TestEnvironment.StartAsync();
    var settings = (await environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    settings["ollamaUrl"] = endpoint;
    settings["defaultModel"] = model;
    settings["coordinatorModel"] = model;
    settings["defaultGpu"] = "auto";
    settings["modelGpuAffinities"] = new JsonObject();
    settings["runtime"]!["generationTimeoutSeconds"] = 240;
    foreach (var intention in settings["intentions"]!.AsObject())
    {
      intention.Value!["model"] = "default";
      intention.Value["fallbackModel"] = "none";
      intention.Value["gpu"] = "default";
    }
    settings["ollamaRuntime"] = new JsonObject
    {
      ["modelOverrides"] = new JsonArray(),
      ["roleDefaults"] = new JsonObject(new[] { "router", "residentCoordinator", "specialist", "primary", "fallback", "benchmark", "modelTest", "webSearchSynthesis", "visionRequest" }
        .Select(role => KeyValuePair.Create<string, JsonNode?>(role, new JsonObject
        {
          ["minimumContextTokens"] = 4096,
          ["targetContextTokens"] = 32768,
          ["maximumContextTokens"] = 32768,
          ["outputTokenLimit"] = 2048,
          ["keepAlive"] = 300
        })))
    };
    using var saved = await environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    Assert.IsTrue(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
    await Page.SetViewportSizeAsync(1600, 1000);
    await Page.GotoAsync(environment.BaseUri.ToString());
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
    await Page.Locator("#thinking-selector").SelectOptionAsync("none");
    await Page.Locator("#open-story-editor").ClickAsync();
    const string draft = "Clara abriu a porta antiga. A porta antiga rangeu.";
    await Page.Locator("#story-document").FillAsync(draft);
    await Page.Locator("#story-instruction").FillAsync("Preserve Clara e os acontecimentos. Português. Não acrescente fatos.");
    await Page.Locator("#message-input").FillAsync("Revise o rascunho inteiro para evitar a repetição de porta antiga. Use set_response_target com target story-editor e replacement contendo apenas o texto revisado. Não aplique nem salve a proposta.");
    await Page.Locator("#send-button").ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 300_000 });
    await Expect(Page.Locator("#story-review-proposal")).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(draft);
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync("Response delivered to Storytelling Editor");
    await Page.Locator("#story-review-proposal").ClickAsync();
    var replacement = await Page.Locator("#story-replacement").InputValueAsync();
    StringAssert.Contains(replacement, "Clara");
    Assert.AreNotEqual(draft, replacement);
    Directory.CreateDirectory(output);
    await Page.ScreenshotAsync(new() { Path = Path.Combine(output, "shared-composer-review.png") });
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(replacement);
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(draft);
    await File.WriteAllTextAsync(Path.Combine(output, "acceptance.json"), new JsonObject
    {
      ["model"] = model,
      ["replacement"] = replacement,
      ["explicitApplyAndUndoVerified"] = true
    }.ToJsonString(new() { WriteIndented = true }));
  }
}
