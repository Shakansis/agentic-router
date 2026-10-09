using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class ExecutionProgressEndToEndTests : ChatEndToEndTestBase<ExecutionProgressEndToEndTests>
{
  [TestMethod]
  [DataRow("opencode", false, "")]
  [DataRow("qwen-code", false, "")]
  [DataRow("claude-code", false, "")]
  [DataRow("codex", false, "")]
  [DataRow("opencode", true, "")]
  [DataRow("qwen-code", true, "")]
  [DataRow("claude-code", true, "")]
  [DataRow("codex", true, "")]
  [DataRow("codex", false, "messages protocol")]
  [DataRow("codex", false, "responses protocol")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task EveryExternalHarnessReceivesEarlyWritesAndRecoversOneOutputCutoff(
    string harness, bool repeated, string protocol)
  {
    _environment.FakeOllama.Reset();
    await Page.GotoAsync("/");
    var marker = Marker(harness);
    if (File.Exists(marker)) File.Delete(marker);
    foreach (var name in new[] { "output-before.txt", "output-recovered.txt" })
      if (File.Exists(Path.Combine(_environment.WorkspaceDirectory, name)))
        File.Delete(Path.Combine(_environment.WorkspaceDirectory, name));
    var model = harness == "claude-code" ? "qwen3-coder:30b" : "qwen3.8:27b-gpu0";
    var objective = "Create output-recovered.txt: global output fixture committed effect " + protocol
      + (repeated ? " always fail" : "") + ". Preserve USER-CONSTRAINT-KEEP and existing files.";
    var events = await SendAsync(harness, objective, model);
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(1, events.Where(item => item["type"]!.GetValue<string>() == "harness.output-limit-recovery-started"));
    var prompts = (await File.ReadAllLinesAsync(marker)).Select(line => JsonNode.Parse(line)!).ToArray();
    Assert.HasCount(2, prompts);
    var providerRequests = _environment.FakeOllama.CompatibilityRequests
      .Where(item => item.Fixture.Contains(objective, StringComparison.Ordinal)).ToArray();
    Assert.IsNotEmpty(providerRequests, "Verify the actual outgoing provider request for every harness.");
    var budgetEvent = events.First(item => item["type"]!.GetValue<string>() == "harness.output-budget.applied");
    foreach (var providerRequest in providerRequests)
    {
      Assert.AreEqual(16_384, providerRequest.Limit, budgetEvent.ToJsonString());
      Assert.AreEqual(1, providerRequest.LimitFields);
      StringAssert.Contains(providerRequest.ContentType!, "application/json");
    }
    Assert.AreEqual(prompts[0]["sessionId"]!.GetValue<string>(), prompts[1]["sessionId"]!.GetValue<string>());
    Assert.AreEqual(model, prompts[0]["model"]!.GetValue<string>());
    Assert.AreEqual(model, prompts[1]["model"]!.GetValue<string>());
    StringAssert.Contains(prompts[0]["text"]!.GetValue<string>(), "write a small valid first implementation promptly");
    var correction = prompts[1]["text"]!.GetValue<string>();
    StringAssert.Contains(correction, objective);
    StringAssert.Contains(correction, "Do not draft file content in reasoning");
    StringAssert.Contains(correction, "output-before.txt");
    Assert.AreEqual("once\n", await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "output-before.txt")));
    if (repeated)
    {
      var failure = events.Single(item => item["type"]!.GetValue<string>() == "error");
      Assert.AreEqual("harness-output-limit-exhausted", failure["error"]!["code"]!.GetValue<string>());
      Assert.IsFalse(failure["error"]!["recoverable"]!.GetValue<bool>());
      Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "output-recovered.txt")));
    }
    else
    {
      Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"));
      Assert.HasCount(1, events.Where(item => item["type"]!.GetValue<string>() == "harness.output-limit-recovery-completed"));
      Assert.AreEqual("recovered incrementally", await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "output-recovered.txt")));
    }
    var execution = events.Last(item => item["executionSession"] is not null)["executionSession"]!;
    var review = JsonNode.Parse(await _environment.HttpClient.GetStringAsync(
      $"api/execution-sessions/{execution["id"]!.GetValue<string>()}/review"))!;
    Assert.HasCount(1, review["files"]!.AsArray().Where(file => file!["relativePath"]!.GetValue<string>() == "output-before.txt"));
    Assert.IsTrue(review["files"]!.AsArray().All(file => file!["verified"]!.GetValue<bool>()));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeConsumesTheSameEarlyWriteAndCorrectionPolicy()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("execute output limit incremental recovery shared policy");
    Assert.AreEqual("recovered incrementally", await File.ReadAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "output-limit-recovered.txt")));
    var requests = _environment.FakeOllama.Requests.Where(request => request.Messages.Any(message =>
      message.Content.Contains("execute output limit incremental recovery shared policy", StringComparison.Ordinal))).ToArray();
    Assert.IsTrue(requests.Any(request => request.Messages.Any(message =>
      message.Content.Contains("write a small valid first implementation promptly", StringComparison.Ordinal))));
    Assert.IsTrue(requests.Where(request => request.HasTools).All(request =>
      request.PredictTokens == request.ContextTokens / 2), "Native inference must use the Host half-context output budget.");
    Assert.IsTrue(requests.Any(request => request.Messages.Any(message =>
      message.Content.Contains("Do not draft file content in reasoning", StringComparison.Ordinal))));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SuccessfulNativeHarnessRecoveryDoesNotTriggerDuplicateHostContinuation()
  {
    await Page.GotoAsync("/");
    var marker = Marker("codex");
    if (File.Exists(marker)) File.Delete(marker);
    var events = await SendAsync("codex", "Create output-recovered.txt: global output fixture native recovery", "qwen3.8:27b-gpu0");
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(1, await File.ReadAllLinesAsync(marker));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>().StartsWith("harness.output-limit-recovery", StringComparison.Ordinal)));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeDirectOutputCutoffIsAlsoBoundedToOneCorrection()
  {
    await Page.GotoAsync("/");
    var events = await SendAsync("native", "Create a file: execute always output limit shared policy", "qwen3-coder:30b");
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(1, events.Where(item => item["type"]!.GetValue<string>() == "action.planning-retry"));
    Assert.HasCount(1, events.Where(item => item["type"]!.GetValue<string>() == "action.output-limit-exhausted"));
    var failure = events.Single(item => item["type"]!.GetValue<string>() == "error");
    Assert.AreEqual("local-action-output-limit", failure["error"]!["code"]!.GetValue<string>());
    Assert.IsFalse(failure["error"]!["recoverable"]!.GetValue<bool>());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnresolvedNativeToolPreventsBlindOutputLimitContinuation()
  {
    await Page.GotoAsync("/");
    var marker = Marker("opencode");
    if (File.Exists(marker)) File.Delete(marker);
    var events = await SendAsync("opencode", "Create a file: global output fixture unresolved tool", "qwen3.8:27b-gpu0");
    Assert.HasCount(1, await File.ReadAllLinesAsync(marker));
    Assert.AreEqual("harness-output-limit-recovery-unavailable",
      events.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["code"]!.GetValue<string>());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BlockedGoalIsConsistentInBrowserPersistenceAndDiagnostic()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using var history = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{workspaceId}/history", new { enabled = true });
    history.EnsureSuccessStatusCode();
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("opencode");
    await Page.Locator("#message-input").FillAsync("Create a game: global output fixture no cutoff");
    await Page.Locator("#send-button").ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true");
    await Expect(Page.Locator(".message.assistant .activity > summary").Last).ToContainTextAsync("Blocked");
    var executionId = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
    var review = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/execution-sessions/{executionId}/review"))!;
    Assert.AreEqual("blocked", review["summary"]!["state"]!.GetValue<string>());
    var traceId = await Page.EvaluateAsync<string>("() => state.history.at(-1).diagnostic.traceId");
    var diagnostic = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/diagnostics/traces/{Uri.EscapeDataString(traceId)}"))!;
    Assert.AreEqual("blocked", diagnostic["status"]!.GetValue<string>());
    Assert.IsFalse(diagnostic["completed"]!.GetValue<bool>());
    var sessionId = await Page.EvaluateAsync<string>("() => state.conversationSessionId");
    var stored = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/sessions/{sessionId}?workspaceId={workspaceId}"))!;
    Assert.AreEqual("failed", stored["state"]!.GetValue<string>());
    Assert.AreEqual("blocked", stored["messages"]!.AsArray().Last()!["diagnostic"]!["terminalState"]!.GetValue<string>());
    await Page.ReloadAsync();
    await Page.Locator($".session-entry[data-session-id='{sessionId}'] .session-entry-content").ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity > summary").Last).ToContainTextAsync("Blocked");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SupervisorOutputRecoveryCannotMultiplyTheConsumedBudget()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    var events = ParseSseEvents(await Page.EvaluateAsync<string>("""
      async () => await (await fetch('/api/chat/stream', {
        method: 'POST', headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({message: 'Create a file: global output fixture always fail',
          model: 'qwen3.8:27b-gpu0', harness: 'qwen-code', history: [],
          interactionMode: 'execute', approvalPolicy: 'auto', executionStrategy: 'supervised',
          browserSessionId: 'supervisor-output-policy'})
      })).text()
      """));
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var prompts = await File.ReadAllLinesAsync(marker);
    Assert.HasCount(2, prompts);
    var correction = JsonNode.Parse(prompts[1])!["text"]!.GetValue<string>();
    StringAssert.Contains(correction, "Return the concise final response in the required format");
    Assert.IsFalse(correction.Contains("Change to bounded incremental writes now", StringComparison.Ordinal));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "supervision.turn-harness-recovery"));
    StringAssert.Contains(string.Join("\n", events.Select(item => item.ToJsonString())), "harness-output-limit-exhausted");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CancellingOutputRecoveryNeverStartsAnotherAttempt()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    var cancelled = Path.Combine(_environment.DataDirectory, "qwen-code-runtime", "fake-context-cancelled.json");
    if (File.Exists(marker)) File.Delete(marker);
    if (File.Exists(cancelled)) File.Delete(cancelled);
    await Page.EvaluateAsync("""
      () => {
        window.outputAbort = new AbortController();
        window.outputRequest = fetch('/api/chat/stream', {
          method: 'POST', headers: {'Content-Type': 'application/json'}, signal: window.outputAbort.signal,
          body: JSON.stringify({message: 'Create a file: global output fixture pause recovery',
            model: 'qwen3.8:27b-gpu0', harness: 'qwen-code', history: [],
            interactionMode: 'execute', approvalPolicy: 'auto', executionStrategy: 'direct',
            browserSessionId: 'cancel-output-policy'})
        }).then(response => response.text()).catch(error => error.name);
      }
      """);
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while ((!File.Exists(marker) || (await File.ReadAllLinesAsync(marker)).Length < 2) && DateTime.UtcNow < deadline)
      await Task.Delay(100);
    Assert.HasCount(2, await File.ReadAllLinesAsync(marker));
    await Page.EvaluateAsync("() => window.outputAbort.abort()");
    deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(cancelled) && DateTime.UtcNow < deadline) await Task.Delay(100);
    Assert.IsTrue(File.Exists(cancelled));
    Assert.HasCount(2, await File.ReadAllLinesAsync(marker));
  }

  [TestMethod]
  [DataRow("groq", "groq::openai/gpt-oss-120b", "gsk_fake_output_budget")]
  [DataRow("cerebras", "cerebras::gpt-oss-120b", "csk_fake_output_budget")]
  [DataRow("google-ai-studio", "google-ai-studio::gemini-test-flash", "AIza_fake_output_budget")]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task CloudExecuteAlsoUsesHalfContextWithoutChangingChat(string provider, string model, string key)
  {
    using var saved = await _environment.HttpClient.PutAsJsonAsync($"api/cloud-providers/{provider}/key", new { apiKey = key });
    saved.EnsureSuccessStatusCode();
    using var tested = await _environment.HttpClient.PostAsync($"api/cloud-providers/{provider}/test", null);
    tested.EnsureSuccessStatusCode();
    using var conformance = await _environment.HttpClient.PostAsJsonAsync("api/models/conformance", new
    {
      model,
      restoreResidentModel = false,
      externalProviderPermissionGranted = true
    });
    conformance.EnsureSuccessStatusCode();
    Assert.IsTrue(JsonNode.Parse(await conformance.Content.ReadAsStringAsync())!["passed"]!.GetValue<bool>());
    var inferenceSettings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    inferenceSettings["inferenceProfiles"]!["software-development"]!["temperature"] = 0.43;
    inferenceSettings["inferenceProfiles"]!["software-development"]!["topP"] = 0.82;
    using var inferenceSaved = await _environment.HttpClient.PutAsJsonAsync("api/settings", inferenceSettings);
    inferenceSaved.EnsureSuccessStatusCode();
    _environment.FakeCloud.Reset();
    await Page.GotoAsync("/");
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    if (File.Exists(path)) File.Delete(path);
    var events = await SendAsync("native", "execute create file hello.txt", model);
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"), string.Join("\n", events.Select(item => item.ToJsonString())));
    Assert.AreEqual("hello from agent", await File.ReadAllTextAsync(path));
    var inference = _environment.FakeCloud.Requests.Where(request => request.Method == "POST"
      && (request.Path.Contains("completions", StringComparison.Ordinal) || request.Path.Contains("generateContent", StringComparison.OrdinalIgnoreCase))).ToArray();
    Assert.IsNotEmpty(inference);
    foreach (var request in inference)
    {
      var body = JsonNode.Parse(request.Body)!;
      var output = provider == "google-ai-studio" ? body["generationConfig"]!["maxOutputTokens"] : body["max_tokens"];
      Assert.AreEqual(16_384, output!.GetValue<int>(), request.Path);
      var sampling = provider == "google-ai-studio" ? body["generationConfig"]! : body;
      Assert.AreEqual(0.43, sampling["temperature"]!.GetValue<double>());
      Assert.AreEqual(0.82, sampling[provider == "google-ai-studio" ? "topP" : "top_p"]!.GetValue<double>());
    }
    _environment.FakeCloud.Reset();
    await Page.Locator(".mode-option[data-mode=chat]").ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    await SendMessageAsync("Use the configured chat output budget.");
    var chatRequest = _environment.FakeCloud.Requests.Last(request => request.Method == "POST"
      && (request.Path.Contains("completions", StringComparison.Ordinal) || request.Path.Contains("generateContent", StringComparison.OrdinalIgnoreCase)));
    var chatBody = JsonNode.Parse(chatRequest.Body)!;
    var chatOutput = provider == "google-ai-studio" ? chatBody["generationConfig"]?["maxOutputTokens"] : chatBody["max_tokens"];
    Assert.IsTrue(chatOutput is null || chatOutput.GetValue<int>() != 16_384, "Chat must retain its separate configured output policy.");
  }

  private static string Marker(string harness) => Path.Combine(_environment.DataDirectory,
    harness + "-runtime", "fake-output-recovery.jsonl");

  private async Task<JsonObject[]> SendAsync(string harness, string message, string model) =>
    ParseSseEvents(await Page.EvaluateAsync<string>("""
      async request => {
        const response = await fetch('/api/chat/stream', {
          method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(request),
          signal: AbortSignal.timeout(45_000)
        });
        if (!response.ok) throw new Error(await response.text());
        return await response.text();
      }
      """, new
    {
      harness,
      message,
      model,
      history = Array.Empty<object>(),
      interactionMode = "execute",
      approvalPolicy = "auto",
      executionStrategy = "direct",
      browserSessionId = "output-policy-" + Guid.NewGuid().ToString("N")
    }));
}
