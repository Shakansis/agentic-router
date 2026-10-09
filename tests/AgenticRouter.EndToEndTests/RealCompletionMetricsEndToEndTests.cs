using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

// Explicit opt-in: point at an isolated API with a trusted test workspace and
// installed harnesses. Never invoke real inference during the ordinary suite.
[TestClass]
public sealed class RealCompletionMetricsEndToEndTests : PageTest
{
  [TestMethod]
  [DoNotParallelize]
  [Timeout(240_000, CooperativeCancellation = true)]
  public async Task InstalledOllamaShowsRealPromptProgressAndPostLoadTtft()
  {
    var endpoint = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_METRICS_URL");
    var model = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_METRICS_MODEL");
    var readme = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_PROGRESS_README");
    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(readme))
      Assert.Inconclusive("Explicit real-inference opt-in and README path are required; use an isolated cold runtime.");
    await Page.GotoAsync(endpoint);
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    await Page.Locator("[data-mode=chat]").ClickAsync();
    await Page.Locator("#message-input").FillAsync(
      "Use exclusivamente o texto abaixo, sem ferramentas. Resuma a arquitetura e os riscos em cinco itens curtos.\n\n"
      + await File.ReadAllTextAsync(readme));
    await Page.Locator("#send-button").ClickAsync();
    var indicator = Page.Locator(".assistant-running-indicator").Last;
    await Expect(indicator).ToHaveAttributeAsync("data-inference-stage", "loading-model", new() { Timeout = 90_000 });
    var directory = Path.Combine(Path.GetDirectoryName(readme)!, ".artifacts", "real-inference-progress");
    Directory.CreateDirectory(directory);
    await Page.ScreenshotAsync(new() { Path = Path.Combine(directory, "loading.png") });
    await Expect(indicator.Locator(".assistant-inference-percent")).ToBeVisibleAsync(new() { Timeout = 120_000 });
    await Expect(indicator).ToHaveAttributeAsync("data-inference-stage", "processing-prompt");
    await Page.ScreenshotAsync(new() { Path = Path.Combine(directory, "prompt.png") });
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 120_000 });
    var footer = Page.Locator(".chat-session-footer").Last;
    await Expect(footer.Locator("[data-metric=ttft]")).ToHaveAttributeAsync("title",
      "Time to the first output token after subtracting provider-reported model loading. Includes prompt processing and reasoning's first token.");
    await Expect(footer.Locator("[data-metric=throughput]")).ToBeVisibleAsync();
    await Page.ScreenshotAsync(new() { Path = Path.Combine(directory, "completed.png") });
  }

  [TestMethod]
  [DataRow("native")]
  [DataRow("codex")]
  [DataRow("claude-code")]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  [DoNotParallelize]
  [Timeout(240_000, CooperativeCancellation = true)]
  public async Task InstalledOllamaHarnessShowsRealCompletionMetrics(string harness)
  {
    var endpoint = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_METRICS_URL");
    var model = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_METRICS_MODEL");
    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
      Assert.Inconclusive("Real inference requires explicit opt-in URL and installed model.");
    using var http = new HttpClient { BaseAddress = new Uri(endpoint) };
    await Page.GotoAsync(endpoint);
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    await Page.Locator("[data-mode=execute]").ClickAsync();
    await Page.Locator("#send-strategy-toggle").ClickAsync();
    await Page.Locator("#send-strategy-menu [data-send-strategy=direct]").ClickAsync();
    await Page.Locator("#approval-policy").SelectOptionAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);
    await Page.Locator("#message-input").FillAsync("Sem usar ferramentas e sem alterar arquivos, responda somente METRICS_OK.");
    await Page.Locator("#send-button").ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 210_000 });
    var footer = Page.Locator(".execution-session-footer").Last;
    await Expect(footer.Locator("[data-metric=tokens]")).ToBeVisibleAsync();
    await Expect(footer.Locator("[data-metric=throughput]")).ToBeVisibleAsync();
    await Expect(footer.Locator("[data-metric=ttft]")).ToBeVisibleAsync();
    await Expect(footer).ToContainTextAsync(model);
    var executionId = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
    using var review = await http.GetAsync($"api/execution-sessions/{executionId}/review");
    review.EnsureSuccessStatusCode();
    var summary = JsonNode.Parse(await review.Content.ReadAsStringAsync())!["summary"]!;
    Assert.IsTrue(summary["state"]!.GetValue<string>().StartsWith("completed", StringComparison.Ordinal));
    var metrics = summary["inferenceMetrics"]!;
    Assert.IsGreaterThan(0L, metrics["outputTokens"]!.GetValue<long>());
    Assert.IsGreaterThan(0d, metrics["generationMilliseconds"]!.GetValue<double>());
    Assert.AreEqual(metrics["outputTokens"]!.GetValue<long>() * 1000d
      / metrics["generationMilliseconds"]!.GetValue<double>(), metrics["tokensPerSecond"]!.GetValue<double>());
    Assert.IsGreaterThan(0d, metrics["timeToFirstTokenMilliseconds"]!.GetValue<double>());
    TestContext.WriteLine($"{harness}: {metrics.ToJsonString()}");
    foreach (var width in new[] { 1280, 760, 390 })
    {
      await Page.SetViewportSizeAsync(width, 800);
      await footer.ScrollIntoViewIfNeededAsync();
      Assert.IsTrue(await footer.EvaluateAsync<bool>("""
        footer => !footer.querySelector('.divider') && [...footer.querySelectorAll('.execution-footer-metric')]
          .every(badge => badge.getBoundingClientRect().right <= footer.getBoundingClientRect().right + 1)
        """));
      await footer.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"ar-real-footer-{harness}-{width}.png") });
    }
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task RecordedOllamaHarnessFootersKeepPersistedMetrics()
  {
    var endpoint = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_METRICS_URL");
    if (string.IsNullOrWhiteSpace(endpoint)) Assert.Inconclusive("Requires the isolated real-test API with recorded histories.");
    using var http = new HttpClient { BaseAddress = new Uri(endpoint) };
    var workspaces = JsonNode.Parse(await http.GetStringAsync("api/workspaces"))!;
    var workspaceId = workspaces["activeWorkspaceId"]!.GetValue<string>();
    var sessions = JsonNode.Parse(await http.GetStringAsync("api/sessions"))!["recent"]!.AsArray();
    Assert.IsGreaterThanOrEqualTo(5, sessions.Count);
    await Page.GotoAsync(endpoint);
    await Page.SetViewportSizeAsync(1280, 800);
    var harnesses = new HashSet<string>(StringComparer.Ordinal);
    foreach (var session in sessions.Take(5))
    {
      var id = session!["id"]!.GetValue<string>();
      var stored = JsonNode.Parse(await http.GetStringAsync($"api/sessions/{id}?workspaceId={workspaceId}"))!;
      harnesses.Add(stored["selectedHarness"]!.GetValue<string>());
      var summary = stored["executionReviews"]![0]!["summary"]!;
      var metrics = summary["inferenceMetrics"]!;
      var review = JsonNode.Parse(await http.GetStringAsync(
        $"api/execution-sessions/{summary["id"]!.GetValue<string>()}/review?conversationSessionId={id}&workspaceId={workspaceId}"))!;
      Assert.AreEqual(metrics.ToJsonString(), review["summary"]!["inferenceMetrics"]!.ToJsonString());
      await Page.Locator("#recent-sessions").EvaluateAsync("element => element.open = true");
      await Page.Locator($".session-entry[data-session-id='{id}'] .session-entry-content").ClickAsync();
      await Page.WaitForFunctionAsync("""
        id => state.conversationSessionId === id && !state.conversationTransitioning
          && !document.querySelector('#messages').hasAttribute('aria-busy')
        """, id);
      var footer = Page.Locator(".execution-session-footer").Last;
      await Expect(footer.Locator("[data-metric=tokens]")).ToContainTextAsync(metrics["outputTokens"]!.GetValue<long>().ToString());
      await Expect(footer.Locator("[data-metric=throughput]")).ToBeVisibleAsync();
      await Expect(footer.Locator("[data-metric=ttft]")).ToBeVisibleAsync();
    }
    CollectionAssert.AreEquivalent(new[] { "native", "codex", "claude-code", "opencode", "qwen-code" }, harnesses.ToArray());
  }
}
