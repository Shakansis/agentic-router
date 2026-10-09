using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class InferenceProfileExecutionEndToEndTests : ChatEndToEndTestBase<InferenceProfileExecutionEndToEndTests>
{
  [TestMethod]
  [DataRow("direct")]
  [DataRow("auto")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ManualModelExecuteSendsTheTaskProfileSavedThroughSettings(string strategy)
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("[data-settings-target=inference]").ClickAsync();
    await Page.Locator("#inference-profile-selector").SelectOptionAsync("software-development");
    await Expect(Page.Locator("#inference-profile-description")).ToContainTextAsync("Worker");
    await Page.Locator("#inference-temperature").FillAsync("0.47");
    await Page.Locator("#inference-thinking").SelectOptionAsync("low");
    await Page.Locator(".inference-advanced summary").ClickAsync();
    await Page.Locator("#inference-top-p").FillAsync("0.83");
    await Page.Locator("#inference-top-k").FillAsync("17");
    await Page.Locator("#inference-min-p").FillAsync("0.07");
    await Page.Locator("#inference-repeat-penalty").FillAsync("1.13");
    await Page.Locator("#inference-repeat-last-n").FillAsync("96");
    await Page.Locator("#inference-seed-mode").SelectOptionAsync("fixed");
    await Page.Locator("#inference-seed").FillAsync("314159");
    var before = await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings");
    Assert.AreNotEqual(0.47, before!["inferenceProfiles"]!["software-development"]!["temperature"]!.GetValue<double>());
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    _environment.FakeOllama.Reset();
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    if (File.Exists(path)) File.Delete(path);
    var events = await SendAsync("execute create file hello.txt", "gpt-oss:20b", "execute", strategy);
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"), string.Join("\n", events.Select(item => item.ToJsonString())));
    Assert.AreEqual("hello from agent", await File.ReadAllTextAsync(path));
    var profile = events.Single(item => item["type"]!.GetValue<string>() == "inference.profile-selected");
    Assert.AreEqual("software-development", profile["inference"]!["profileId"]!.GetValue<string>());
    var requests = _environment.FakeOllama.Requests.Where(request => request.Model == "gpt-oss:20b" && request.HasTools).ToArray();
    Assert.IsNotEmpty(requests);
    foreach (var request in requests)
    {
      Assert.AreEqual(0.47, request.Temperature);
      Assert.AreEqual(0.83, request.TopP);
      Assert.AreEqual(17, request.TopK);
      Assert.AreEqual(0.07, request.MinP);
      Assert.AreEqual(1.13, request.RepeatPenalty);
      Assert.AreEqual(96, request.RepeatLastN);
      Assert.AreEqual(314159, request.Seed);
      Assert.AreEqual("low", request.Think);
    }
  }

  [TestMethod]
  [DataRow("Crie um jogo completo chamado Ultimo Farol.", "software-development", 0.41)]
  [DataRow("Tell an RPG story about a lighthouse.", "rpg-storytelling", 0.73)]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ManualModelUsesTheClassifiedProfileWithoutChangingTheSelectedModel(string objective, string intent, double temperature)
  {
    await Page.GotoAsync("/");
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    settings["inferenceProfiles"]![intent]!["temperature"] = temperature;
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    saved.EnsureSuccessStatusCode();
    _environment.FakeOllama.Reset();
    var events = await SendAsync(objective, "gpt-oss:20b", "chat", "direct");
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"));
    Assert.IsTrue(events.Any(item => item["type"]!.GetValue<string>() == "inference.profile-selected"
      && item["inference"]?["profileId"]?.GetValue<string>() == intent));
    var requests = _environment.FakeOllama.Requests.Where(request => request.Messages.Any(message => message.Content == objective)).ToArray();
    Assert.IsNotEmpty(requests);
    Assert.IsTrue(requests.All(request => request.Model == "gpt-oss:20b" && request.Temperature == temperature));
  }

  [TestMethod]
  [DataRow("codex")]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  [DataRow("claude-code")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessAutoThinkingDoesNotAddPhaseEffortAndReportsUnavailableSampling(string harness)
  {
    await Page.GotoAsync("/");
    var events = await ExecuteHarnessStreamAsync(harness,
      "Create a file: global output fixture native recovery", Guid.NewGuid().ToString("N"), "qwen3.8:27b-gpu0");
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "execution-effort-requested"
      || item["type"]!.GetValue<string>().EndsWith("effort.applied", StringComparison.Ordinal)));
    var profile = events.Single(item => item["type"]!.GetValue<string>() == "inference.profile-selected");
    Assert.AreEqual("review-and-testing", profile["inference"]!["profileId"]!.GetValue<string>());
    Assert.AreEqual(0.1, profile["inference"]!["temperature"]!.GetValue<double>());
    Assert.IsTrue(profile["inference"]!["unavailableControls"]!.AsArray().Any(value => value!.GetValue<string>() == "temperature"));
    var prompts = await File.ReadAllLinesAsync(Path.Combine(_environment.DataDirectory, harness + "-runtime", "fake-output-recovery.jsonl"));
    Assert.IsNotEmpty(prompts);
    Assert.IsTrue(prompts.All(prompt => !prompt.Contains("Host effort target", StringComparison.Ordinal)));
    Assert.AreEqual("recovered incrementally", await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "output-recovered.txt")));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnsupportedTaskThinkingIsReportedWithoutAHiddenMediumOverride()
  {
    await Page.GotoAsync("/");
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    settings["inferenceProfiles"]!["software-development"]!["thinking"] = "disabled";
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    saved.EnsureSuccessStatusCode();
    _environment.FakeOllama.Reset();
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    if (File.Exists(path)) File.Delete(path);
    var events = await SendAsync("execute create file hello.txt", "gpt-oss:20b", "execute", "direct");
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"));
    var profile = events.Single(item => item["type"]!.GetValue<string>() == "inference.profile-selected");
    Assert.AreEqual("disabled", profile["inference"]!["thinking"]!.GetValue<string>());
    Assert.IsTrue(profile["inference"]!["unavailableControls"]!.AsArray().Any(value => value!.GetValue<string>() == "thinking"), profile.ToJsonString());
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "execution-effort-requested"));
    Assert.IsTrue(_environment.FakeOllama.Requests.Where(request => request.HasTools).All(request => request.Think is null));
    Assert.AreEqual("hello from agent", await File.ReadAllTextAsync(path));
  }

  private async Task<JsonObject[]> SendAsync(string message, string model, string mode, string strategy) =>
    ParseSseEvents(await Page.EvaluateAsync<string>("""
      async request => await (await fetch('/api/chat/stream', {
        method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(request),
        signal: AbortSignal.timeout(45_000)
      })).text()
      """, new
    {
      message,
      model,
      interactionMode = mode,
      executionStrategy = strategy,
      harness = "native",
      approvalPolicy = "auto",
      history = Array.Empty<object>(),
      browserSessionId = Guid.NewGuid().ToString("N")
    }));
}
