using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class ToolWorkspaceEndToEndTests : ChatEndToEndTestBase<ToolWorkspaceEndToEndTests>
{
  [TestCleanup]
  public async Task CaptureFailedActivityAsync()
  {
    if (TestContext.CurrentTestOutcome != UnitTestOutcome.Failed) return;
    TestContext.WriteLine(await Page.Locator(".activity-list").Last.TextContentAsync());
  }

  [TestMethod]
  public async Task ToolsStartClosedAndShareComposerWithResizableChat()
  {
    await Page.GotoAsync("/");
    await Expect(Page.Locator("#tool-pane")).ToBeHiddenAsync();
    await Expect(Page.Locator("#composer-target-label")).ToHaveTextAsync("Send to Chat");
    await OpenEditorAsync();
    await Expect(Page.Locator("#tool-pane")).ToBeVisibleAsync();
    await Expect(Page.Locator("#composer-shell")).ToBeVisibleAsync();
    await Expect(Page.Locator("#composer-target-label")).ToHaveTextAsync("Send to Storytelling Editor");
    await Expect(Page.Locator("#tool-divider")).ToBeVisibleAsync();
    var before = (await Page.Locator("#chat-pane").BoundingBoxAsync())!.Width;
    await Page.Locator("#tool-divider").FocusAsync();
    await Page.Locator("#tool-divider").PressAsync("ArrowRight");
    Assert.IsGreaterThan(before, (await Page.Locator("#chat-pane").BoundingBoxAsync())!.Width);
    var divider = (await Page.Locator("#tool-divider").BoundingBoxAsync())!;
    await Page.Mouse.MoveAsync(divider.X + 4, divider.Y + 30);
    await Page.Mouse.DownAsync();
    await Page.Mouse.MoveAsync(divider.X - 80, divider.Y + 30);
    await Page.Mouse.UpAsync();
    Assert.IsLessThan(before, (await Page.Locator("#chat-pane").BoundingBoxAsync())!.Width);
    await Page.Locator("#jump-latest").EvaluateAsync("el => el.hidden = false");
    Assert.IsTrue(await Page.EvaluateAsync<bool>("""
      () => { const chat = document.getElementById('chat-content').getBoundingClientRect();
        const jump = document.getElementById('jump-latest').getBoundingClientRect();
        return jump.left >= chat.left && jump.right <= chat.right && jump.bottom <= chat.bottom; }
      """));
    await Page.Locator("#story-document").FillAsync("Draft survives closing.");
    await Page.Locator("#close-story-editor").ClickAsync();
    await Expect(Page.Locator("#tool-pane")).ToBeHiddenAsync();
    await Page.Locator("#open-story-editor").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Draft survives closing.");
    await CaptureAsync("split");
    await Page.Locator("#story-controls-toggle").ClickAsync();
    await Expect(Page.Locator("#story-controls")).ToBeHiddenAsync();
    await CaptureAsync("reading");
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Expect(Page.Locator("#story-fullscreen #composer-shell")).ToBeVisibleAsync();
    await Page.Keyboard.PressAsync("Escape");
    await Expect(Page.Locator("#conversation-view > #composer-shell")).ToBeVisibleAsync();
    await Page.SetViewportSizeAsync(390, 844);
    await Expect(Page.Locator("#message-input")).ToBeVisibleAsync();
    await Page.Locator("#chat-tab").ClickAsync();
    await Expect(Page.Locator("#messages")).ToBeVisibleAsync();
    await Page.Locator("#story-tool-tab").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToBeVisibleAsync();
    Assert.IsTrue(await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
    await CaptureAsync("mobile");
  }

  [TestMethod]
  public async Task SharedComposerProposesInEditorAndHostRecordsDestination()
  {
    await OpenEditorAsync();
    await Page.Locator("#story-document").FillAsync("The old mage returned.");
    await Page.Locator("#story-document").EvaluateAsync("el => { el.setSelectionRange(4, 12); el.dispatchEvent(new Event('select')); }");
    await Page.Locator("#story-instruction").FillAsync("Preserve narrative voice.");
    await SendMessageAsync("revise tool window fixture");
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request => request.Messages.Any(message => message.Role == "system"
      && message.Content.Contains("Author's revision preferences", StringComparison.Ordinal)
      && message.Content.Contains("Preserve narrative voice.", StringComparison.Ordinal))));
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("The old mage returned.");
    await Expect(Page.Locator("#story-review-proposal")).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-tool-output")).ToContainTextAsync("Tool window fixture response.");
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync("Response delivered to Storytelling Editor");
    Assert.IsTrue(await Page.EvaluateAsync<bool>("state.history.at(-1).timeline.some(e => e.type === 'tool-window.proposal' && e.message.includes('story-editor'))"));
    Assert.IsTrue(await Page.EvaluateAsync<bool>("state.history.at(-1).timeline.every(e => !e.toolWindow)"));
    await Page.Locator("#story-review-proposal").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("old mage");
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("The old mage — composer revision returned.");
    await Expect(Page.Locator("#messages")).ToContainTextAsync("Replacement applied");
  }

  [TestMethod]
  public async Task ModelCanExplicitlyReturnToChatWhileEditorStaysOpen()
  {
    await OpenEditorAsync();
    await Page.Locator("#story-document").FillAsync("Keep the draft.");
    await SendMessageAsync("chat tool window fixture");
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync("Tool window fixture response.");
    await Expect(Page.Locator("#story-tool-output")).ToBeHiddenAsync();
    await Expect(Page.Locator("#tool-pane")).ToBeVisibleAsync();
    await Expect(Page.Locator("#composer-target-label")).ToHaveTextAsync("Send to Storytelling Editor");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Keep the draft.");
    Assert.IsTrue(await Page.EvaluateAsync<bool>("state.history.at(-1).timeline.some(e => e.type === 'tool-window.target-selected' && e.message.includes('Response target: chat.'))"));
  }

  [TestMethod]
  public async Task ChangedDraftRejectsAComposerProposal()
  {
    await OpenEditorAsync();
    await Page.Locator("#story-document").FillAsync("Original draft.");
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _environment.FakeOllama.BeforeChatResponse = request =>
    {
      if (!request.Messages.Any(message => message.Content.Contains("AR_TOOL_WINDOW_V1", StringComparison.Ordinal))) return Task.CompletedTask;
      observed.TrySetResult();
      return release.Task;
    };
    try
    {
      await StartMessageAsync("revise tool window fixture");
      await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
      await Page.Locator("#story-document").FillAsync("External editing preserved.");
      release.TrySetResult();
      await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true");
      await Expect(Page.Locator("#story-review-proposal")).ToBeHiddenAsync();
      await Expect(Page.Locator("#story-status")).ToContainTextAsync("Draft changed during generation");
      await Expect(Page.Locator("#story-document")).ToHaveValueAsync("External editing preserved.");
    }
    finally { release.TrySetResult(); _environment.FakeOllama.BeforeChatResponse = null; }
  }

  [TestMethod]
  [DataRow("alpha:latest", false)]
  [DataRow("qwen3-coder:30b", false)]
  [DataRow("alpha:latest", true)]
  [DataRow("qwen3-coder:30b", true)]
  public async Task NativeExecuteUsesTheSameEditorTargetAndReviewWorkflow(string model, bool compact)
  {
    await OpenEditorAsync();
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    await Page.Locator("#story-document").FillAsync("Native draft.");
    await SetExecuteModeAsync("auto");
    if (compact) await Page.EvaluateAsync("state.compactContextNextRequest = true");
    await SendMessageAsync("revise tool window fixture");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Native draft.");
    await Expect(Page.Locator("#story-review-proposal")).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-tool-output")).ToContainTextAsync("Tool window fixture response.");
    Assert.IsTrue(await Page.EvaluateAsync<bool>("state.history.at(-1).timeline.some(e => e.type === 'tool-window.proposal')"));
    await Expect(Page.Locator(".action-approval")).ToHaveCountAsync(0);
  }

  [TestMethod]
  [DataRow("codex")]
  [DataRow("claude-code")]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  public async Task ExternalHarnessesReceiveToolContextAndDeliverToTheAuxiliaryPane(string harness)
  {
    await OpenEditorAsync();
    await Page.Locator("#story-document").FillAsync("Keep this auxiliary draft.");
    var raw = await Page.EvaluateAsync<string>("""
      async harness => {
        const response = await fetch('/api/chat/stream', { method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ message: 'Inspect the workspace and summarize.', model: 'qwen3.8:27b-gpu0', history: [],
            interactionMode: 'execute', executionStrategy: 'direct', harness, approvalPolicy: 'auto',
            browserSessionId: `tool-window-${harness}`, specialist: 'rpg-storytelling', toolWindow: captureToolWindowContext() }) });
        return await response.text();
      }
      """, harness);
    var events = ParseSseEvents(raw);
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
    Assert.HasCount(1, events.Where(item => item["type"]!.GetValue<string>() == "response.completed"));
    Assert.IsTrue(events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
      .All(item => item["responseSegmentHtml"] is null));
    Assert.IsTrue(events.Any(item => item["type"]!.GetValue<string>() == "tool-window.response"));
    Assert.IsTrue(events.Any(item => item["type"]!.GetValue<string>() == "execution-capability-profile-projected"
      && item["message"]!.GetValue<string>().Contains("set_response_target", StringComparison.Ordinal)));
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Keep this auxiliary draft.");
  }

  [TestMethod]
  public async Task SupervisedExecuteKeepsProtocolDecisionsInternalAndDeliversToTheTool()
  {
    await OpenEditorAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await Page.Locator("#story-document").FillAsync("Supervised auxiliary draft.");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("/supervisor supervision first pass success");
    await Expect(Page.Locator("#story-tool-output")).ToBeVisibleAsync();
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync("Response delivered to Storytelling Editor");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Supervised auxiliary draft.");
    Assert.IsTrue(await Page.EvaluateAsync<bool>("state.history.at(-1).timeline.some(e => e.type === 'supervision.completed')"));
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request => request.Messages.Any(message => message.Role == "system"
      && message.Content.Contains("AR_TOOL_WINDOW_CONTEXT_V1", StringComparison.Ordinal))));
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request => request.Messages.Any(message => message.Role == "system"
      && message.Content.Contains("AR_TOOL_WINDOW_V1", StringComparison.Ordinal))));
  }

  [TestMethod]
  public async Task QueuedChatKeepsItsDestinationAfterSwitchingBackToTool()
  {
    await OpenEditorAsync();
    await Page.Locator("#story-document").FillAsync("Queued draft.");
    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _environment.FakeOllama.BeforeChatResponse = request =>
    {
      if (!request.Messages.Any(message => message.Content.Contains("AR_TOOL_WINDOW_V1", StringComparison.Ordinal))) return Task.CompletedTask;
      observed.TrySetResult();
      return release.Task;
    };
    try
    {
      await StartMessageAsync("analysis tool window fixture");
      await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
      await Page.Locator("#chat-tab").ClickAsync();
      await Page.Locator("#message-input").FillAsync("Queued chat destination.");
      await Page.Locator("#send-button").ClickAsync();
      await Expect(Page.Locator(".message-buffer-item")).ToHaveCountAsync(1);
      await Page.Locator("#story-tool-tab").ClickAsync();
      release.TrySetResult();
      await Expect(Page.Locator(".message.assistant")).ToHaveCountAsync(2);
      await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true");
      Assert.IsTrue(await Page.EvaluateAsync<bool>("state.history.at(-1).timeline.every(e => !e.type.startsWith('tool-window.'))"));
      await Expect(Page.Locator("#composer-target-label")).ToHaveTextAsync("Send to Storytelling Editor");
      await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Queued draft.");
    }
    finally { release.TrySetResult(); _environment.FakeOllama.BeforeChatResponse = null; }
  }

  [TestMethod]
  public async Task HostRejectsInvalidToolContextBeforeInference()
  {
    var count = _environment.FakeOllama.Requests.Count;
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream", new
    {
      message = "Invalid context",
      model = "alpha:latest",
      history = Array.Empty<object>(),
      toolWindow = new { toolId = "unknown", document = "text", selectionStart = 0, selectionEnd = 4, revision = 0 }
    });
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.IsTrue(events.Any(item => item["error"]?["stage"]?.GetValue<string>() == "tool-window-context-invalid"));
    Assert.HasCount(count, _environment.FakeOllama.Requests);
  }

  private async Task OpenEditorAsync()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
    await Page.Locator("#open-story-editor").ClickAsync();
  }

  private async Task CaptureAsync(string name)
  {
    var directory = Path.Combine(_environment.RepositoryRoot, ".artifacts", "tool-workspace");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, name + ".png");
    await Page.ScreenshotAsync(new() { Path = path });
    TestContext.AddResultFile(path);
  }
}
