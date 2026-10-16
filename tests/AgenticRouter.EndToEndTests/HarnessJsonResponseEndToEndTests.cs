using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class HarnessJsonResponseEndToEndTests : ChatEndToEndTestBase<HarnessJsonResponseEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NonStreamingCodexResponseRemainsJsonAndTranslatesNativePatch()
  {
    _environment.FakeOllama.InstalledHarnessProbe = true;
    _environment.FakeOllama.InstalledCodexPatchProbe = true;
    _environment.FakeOllama.InstalledCodexJsonProbe = true;
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");
    await SendMessageAsync("codex nonstream JSON fixture");
    await Expect(Page.Locator(".message.assistant .assistant-answer").Last).ToContainTextAsync("JSON_NONSTREAM_OK");
    Assert.HasCount(1, _environment.FakeOllama.InstalledHarnessRequests);
    Assert.IsFalse(_environment.FakeOllama.InstalledHarnessRequests.Single().GetProperty("stream").GetBoolean());
    var id = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
    var review = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/execution-sessions/{id}/review"))!;
    Assert.IsEmpty(review["files"]!.AsArray());
    Assert.DoesNotContain("Harness inference transport ended", _environment.ApiOutput);
  }
}
