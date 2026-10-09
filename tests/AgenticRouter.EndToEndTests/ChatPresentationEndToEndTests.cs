using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class ChatPresentationEndToEndTests : ChatEndToEndTestBase<ChatPresentationEndToEndTests>
{
  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ChatShowsLiveIdentityReadsCommentaryAndPersistedCompletionMetrics()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using var enabled = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{workspaceId}/history", new { enabled = true });
    enabled.EnsureSuccessStatusCode();
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"),
      "visible bounded workspace evidence");
    await Page.GotoAsync("/");
    await StartMessageAsync("chat workspace read request with mixed content chat progress fixture");
    await Expect(Page.Locator(".chat-session-header.is-live")).ToContainTextAsync("alpha:latest");
    await Expect(Page.Locator(".chat-session-footer")).ToBeHiddenAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    await Expect(Page.Locator(".chat-model-progress")).ToBeVisibleAsync();
    await Expect(Page.Locator(".chat-model-progress"))
      .ToContainTextAsync("This non-terminal preamble must never become visible.");
    await Expect(Page.Locator(".assistant-answer"))
      .Not.ToContainTextAsync("This non-terminal preamble must never become visible.");
    await Expect(Page.Locator(".work-action[data-state=completed]")).ToBeVisibleAsync();
    await Page.Locator(".work-action summary").ClickAsync();
    await Expect(Page.Locator(".work-action-preview")).ToContainTextAsync("visible bounded workspace evidence");
    var footer = Page.Locator(".chat-session-footer");
    await Expect(footer).ToBeVisibleAsync();
    await Expect(footer).ToContainTextAsync("1 read");
    await Expect(footer.Locator("[data-metric=tokens]")).ToContainTextAsync("60");
    await Expect(footer.Locator("[data-metric=throughput]")).ToContainTextAsync("50.00");
    await Expect(footer.Locator("[data-metric=ttft]")).ToBeVisibleAsync();
    await Expect(Page.Locator(".action-approval")).ToHaveCountAsync(0);
    Assert.IsNull(await Page.EvaluateAsync<string?>("() => state.latestExecutionSessionId"));
    var conversationId = await Page.EvaluateAsync<string>("() => state.conversationSessionId");
    var saved = await _environment.HttpClient.GetFromJsonAsync<JsonObject>(
      $"api/sessions/{conversationId}?workspaceId={workspaceId}");
    var terminal = saved!["messages"]!.AsArray().Last(item => item!["role"]!.GetValue<string>() == "assistant")!
      ["timeline"]!.AsArray().Single(item => item!["type"]!.GetValue<string>() == "response.completed")!;
    Assert.IsNull(terminal["executionSession"]);
    Assert.AreEqual(60L, terminal["chatSummary"]!["inferenceMetrics"]!["outputTokens"]!.GetValue<long>());
    var requests = _environment.FakeOllama.AllRequests.Count;
    await _environment.RestartApplicationAsync();
    await Page.ReloadAsync();
    await Page.Locator("#recent-sessions").EvaluateAsync("element => element.open = true");
    await Page.Locator($".session-entry[data-session-id='{conversationId}'] .session-entry-content").ClickAsync();
    await Expect(Page.Locator(".chat-session-footer [data-metric=tokens]")).ToContainTextAsync("60");
    await Expect(Page.Locator(".work-action")).ToBeVisibleAsync();
    await Expect(Page.Locator(".chat-model-progress")).ToBeVisibleAsync();
    Assert.HasCount(requests, _environment.FakeOllama.AllRequests);
    foreach (var width in new[] { 1280, 760, 390 })
    {
      await Page.SetViewportSizeAsync(width, 800);
      await Page.EvaluateAsync("() => resumeAutoFollow()");
      Assert.IsTrue(await Page.Locator(".chat-session-footer").EvaluateAsync<bool>("""
        footer => [...footer.querySelectorAll('.execution-footer-metric')].every(
          item => item.getBoundingClientRect().right <= footer.getBoundingClientRect().right + 1)
        """));
      var screenshot = Path.Combine(TestContext.TestResultsDirectory!, $"chat-footer-{width}.png");
      await Page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
      TestContext.AddResultFile(screenshot);
    }
    await Page.EvaluateAsync("""
      async json => {
        const events = JSON.parse(json).map(({ chatSummary, ...event }) => event);
        await consumeEventStream(null, appendAssistantMessage(), {
          events, historical: true, conversationVersion: state.conversationVersion
        });
      }
      """, saved["messages"]!.AsArray().Last(item => item!["role"]!.GetValue<string>() == "assistant")!["timeline"]!.ToJsonString());
    await Expect(Page.Locator(".chat-session-footer").Last).ToBeVisibleAsync();
    await Expect(Page.Locator(".chat-session-footer").Last).ToContainTextAsync("Model: alpha:latest");
    await Expect(Page.Locator(".chat-session-footer").Last.Locator(".execution-footer-metric")).ToHaveCountAsync(0);
    Assert.HasCount(requests, _environment.FakeOllama.AllRequests);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PlainChatAlsoShowsCompletionWithoutAnExecutionSession()
  {
    await Page.GotoAsync("/");
    await SendMessageAsync("hello in ordinary Chat");
    await Expect(Page.Locator(".chat-session-footer")).ToBeVisibleAsync();
    await Expect(Page.Locator(".chat-session-footer")).ToContainTextAsync("0 reads");
    await Expect(Page.Locator(".chat-session-footer")).ToContainTextAsync("completed");
    Assert.IsNull(await Page.EvaluateAsync<string?>("() => state.latestExecutionSessionId"));
  }
}
