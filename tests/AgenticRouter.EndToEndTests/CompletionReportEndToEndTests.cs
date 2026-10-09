using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class CompletionReportEndToEndTests : ChatEndToEndTestBase<CompletionReportEndToEndTests>
{
  [TestMethod]
  [DataRow("Analise o desempenho desta aplicação", "general-coding")]
  [DataRow("Analyze application performance", "general-coding")]
  [DataRow("Make the application faster", "general-coding")]
  [DataRow("Validate application performance", "correctness-first")]
  [DataRow("Não priorize a velocidade da resposta; analise o desempenho", "general-coding")]
  [DataRow("Do not respond quickly; analyze performance", "general-coding")]
  [DataRow("Responda rapidamente; analise o desempenho", "efficiency-first")]
  [DataRow("Priorize a velocidade da execução; verifique testes", "efficiency-first")]
  [DataRow("Please respond quickly; validate correctness", "efficiency-first")]
  [DataRow("Use the fastest harness; validate correctness", "efficiency-first")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoSeparatesTaskPerformanceFromExecutionPreference(string objective, string category)
  {
    await Page.GotoAsync("/");
    var events = await SendAsync("native", objective, auto: true);
    var terminal = events.Single(item => item["type"]!.GetValue<string>() == "response.completed");
    var evidence = terminal["executionSession"]!["routingEvidence"]!;
    Assert.AreEqual(category, evidence["taskCategory"]!.GetValue<string>());
    Assert.AreEqual("auto-model-harness-router-v3", evidence["routerVersion"]!.GetValue<string>());
    await Page.EvaluateAsync("id => openChangeReview(id)", terminal["executionSession"]!["id"]!.GetValue<string>());
    await Expect(Page.Locator(".routing-evidence")).ToContainTextAsync(category);
  }

  [TestMethod]
  [DataRow("native")]
  [DataRow("codex")]
  [DataRow("claude-code")]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CompletedAttemptOnlyChecksOnDemand(string harness)
  {
    _environment.FakeOllama.Reset();
    await Page.GotoAsync("/");
    var objective = harness == "native" ? "run process" : "Inspect the current workspace";
    var events = await SendAsync(harness, objective);
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var terminal = events.Single(item => item["type"]!.GetValue<string>() == "response.completed");
    Assert.IsNull(terminal["completionReport"]);
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "execution.report-started"));
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(IsReport));
    await RenderAsync(events);
    var check = Page.Locator(".execution-session-footer .completion-check").Last;
    await Expect(check).ToBeVisibleAsync();
    await check.ClickAsync();
    await Expect(Page.Locator(".completion-report-text").Last).ToContainTextAsync("Missing: CSV filters");
    await check.ClickAsync();
    var review = _environment.FakeOllama.Requests.Single(IsReport);
    Assert.IsFalse(review.HasTools);
    StringAssert.Contains(review.Messages.Last().Content, objective);
    StringAssert.Contains(review.Messages.Last().Content, "Recorded Host evidence:");
    Assert.HasCount(1, _environment.FakeOllama.Requests.Where(IsReport));
    var panel = Page.Locator(".execution-completion-report").Last;
    await Expect(panel).ToBeVisibleAsync();
    Assert.IsTrue(await panel.Locator("details").EvaluateAsync<bool>("element => element.open"));
    await Expect(panel).ToContainTextAsync("Missing: CSV filters");
    await Expect(panel).ToContainTextAsync("Not verified:");
    Assert.IsFalse(await Page.EvaluateAsync<bool>("() => window.reportInjected === true"));
    await Expect(panel.Locator("script")).ToHaveCountAsync(0);
    await Expect(Page.Locator(".message.assistant .assistant-answer")).Not.ToContainTextAsync("Missing: CSV filters");
    if (harness == "native")
      await panel.ScreenshotAsync(new()
      {
        Path = Path.Combine(_environment.RepositoryRoot,
          "tests/AgenticRouter.EndToEndTests/TestResults/product-review/completion-report-expanded.png")
      });
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ReportFailureDoesNotFailOrRetryCompletedExecution()
  {
    _environment.FakeOllama.Reset();
    await Page.GotoAsync("/");
    var events = await SendAsync("native", "run process completion-report-failure-fixture");
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var terminal = events.Single(item => item["type"]!.GetValue<string>() == "response.completed");
    Assert.IsNull(terminal["completionReport"]);
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(IsReport));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"));
    await RenderAsync(events);
    await Page.Locator(".completion-check").ClickAsync();
    await Expect(Page.Locator(".execution-completion-report")).ToContainTextAsync("no retry was started");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ReportPersistsAndRestoresWithoutAnotherInference(bool restartBeforeCheck)
  {
    _environment.FakeOllama.Reset();
    var workspace = JsonNode.Parse(await _environment.HttpClient.GetStringAsync("api/workspaces"))!;
    var workspaceId = workspace["activeWorkspaceId"]!.GetValue<string>();
    using var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspaceId}/history", new { enabled = true });
    enabled.EnsureSuccessStatusCode();
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("run process");
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(IsReport));
    var conversationId = await Page.EvaluateAsync<string>("() => state.conversationSessionId");
    if (restartBeforeCheck)
    {
      await _environment.RestartApplicationAsync();
      await Page.ReloadAsync();
      await Page.Locator("#recent-sessions").EvaluateAsync("element => element.open = true");
      await Page.Locator($".session-entry[data-session-id='{conversationId}'] .session-entry-content").ClickAsync();
    }
    await Page.Locator(".completion-check").ClickAsync();
    await Expect(Page.Locator(".completion-report-text")).ToBeVisibleAsync();
    var saved = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/sessions/{conversationId}?workspaceId={workspaceId}"))!;
    var assistant = saved["messages"]!.AsArray().Last(item => item!["role"]!.GetValue<string>() == "assistant")!;
    var terminal = assistant["timeline"]!.AsArray().Single(item => item!["type"]!.GetValue<string>() == "response.completed")!;
    Assert.AreEqual("available", terminal["completionReport"]!["status"]!.GetValue<string>());
    var count = _environment.FakeOllama.AllRequests.Count(IsReport);
    await _environment.RestartApplicationAsync();
    await Page.ReloadAsync();
    await Page.Locator("#recent-sessions").EvaluateAsync("element => element.open = true");
    await Page.Locator($".session-entry[data-session-id='{conversationId}'] .session-entry-content").ClickAsync();
    await Expect(Page.Locator(".execution-completion-report")).ToBeVisibleAsync();
    Assert.IsFalse(await Page.Locator(".execution-completion-report details").EvaluateAsync<bool>("element => element.open"));
    Assert.AreEqual(count, _environment.FakeOllama.AllRequests.Count(IsReport));
  }

  private static bool IsReport(RecordedChatRequest request) => request.Messages.Any(message =>
    message.Role == "system" && message.Content.StartsWith("EXECUTION_COMPLETION_REPORT_V1", StringComparison.Ordinal));

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task VolatileConversationChecksFromItsActualFooter()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    _environment.FakeOllama.Reset();
    await SendMessageAsync("run process");
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(IsReport));
    await Page.Locator(".completion-check").ClickAsync();
    await Expect(Page.Locator(".completion-report-text")).ToContainTextAsync("Not verified:");
    Assert.HasCount(1, _environment.FakeOllama.Requests.Where(IsReport));
  }

  private async Task<JsonObject[]> SendAsync(string harness, string objective, bool auto = false)
  {
    var text = await Page.EvaluateAsync<string>("""
      async request => {
        const response = await fetch('/api/chat/stream', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request)
        });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return await response.text();
      }
      """, new
    {
      message = objective,
      model = auto ? "auto" : "alpha:latest",
      history = Array.Empty<object>(),
      interactionMode = "execute",
      harness = auto ? "auto-model-harness" : harness,
      autoModelHarness = auto,
      approvalPolicy = "auto",
      executionStrategy = "direct",
      browserSessionId = await Page.EvaluateAsync<string>("() => state.browserSessionId")
    });
    return ParseSseEvents(text);
  }

  private Task RenderAsync(JsonObject[] events) => Page.EvaluateAsync("""
    async json => {
      const assistant = appendAssistantMessage();
      await consumeEventStream(null, assistant, {
        events: JSON.parse(json), historical: true, conversationVersion: state.conversationVersion
      });
    }
    """, System.Text.Json.JsonSerializer.Serialize(events));
}
