using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Execution;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class NativeMultiToolEndToEndTests : ChatEndToEndTestBase<NativeMultiToolEndToEndTests>
{
  [TestMethod]
  [DataRow("sequential", 3, "off")]
  [DataRow("sequential", 3, "auto")]
  [DataRow("sequential", 3, "on")]
  [DataRow("invalid", 4, "off")]
  [DataRow("invalid", 4, "auto")]
  [DataRow("malformed", 4, "off")]
  [DataRow("malformed", 4, "auto")]
  [DataRow("escape", 4, "off")]
  [DataRow("escape", 4, "auto")]
  [DataRow("mixed", 4, "off")]
  [DataRow("mixed", 4, "auto")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativePreservesCallsAndCorrelatesEveryResult(string scenario, int expected, string mode)
  {
    using var saved = await _environment.PutSettingsAsync(_environment.BaselineSettings with
    {
      Execution = _environment.BaselineSettings.Execution with { ParallelToolExecution = mode, MaxConcurrentToolCalls = 2 }
    });
    saved.EnsureSuccessStatusCode();
    File.Delete(Path.Combine(_environment.WorkspaceDirectory, "multi-created.txt"));
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "multi-a.txt"), "first input");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "multi-b.txt"), "second input");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync($"NATIVE_MULTITOOL_V1 {scenario} {mode}");
    var requests = _environment.FakeOllama.Requests.Where(request =>
      request.Messages.Any(message => message.Content.Contains($"NATIVE_MULTITOOL_V1 {scenario} {mode}", StringComparison.Ordinal)))
      .ToArray();
    var completed = requests.Last(request => request.Messages.Any(message => message.ToolCallId == "read-a"));
    var assistant = completed.Messages.Single(message => message.Role == "assistant"
      && message.ToolCalls.Any(call => call.Id == "read-a"));
    Assert.HasCount(expected, assistant.ToolCalls);
    var results = completed.Messages.Where(message => message.Role == "tool" && message.ToolCallId != "grant").ToArray();
    Assert.HasCount(expected, results);
    CollectionAssert.AreEqual(assistant.ToolCalls.Select(call => call.Id).ToArray(), results.Select(result => result.ToolCallId).ToArray());
    Assert.HasCount(expected, results.Select(result => result.ToolCallId).Distinct().ToArray());
    Assert.IsTrue(results[0].Content.Contains("first input", StringComparison.Ordinal));
    if (scenario is "invalid" or "malformed")
      Assert.IsTrue(results[1].Content.Contains("rejected", StringComparison.Ordinal));
    if (scenario == "escape")
    {
      Assert.IsTrue(results[1].Content.Contains("policy-denied", StringComparison.Ordinal));
      Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "..", "native-multitool-escape.txt")));
    }
    if (scenario == "mixed")
      Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "multi-created.txt")));
    Assert.HasCount(3, requests.Where(request => request.Messages.Any(message => message.Content.Contains(LocalActionPlanner.PlannerMarker, StringComparison.Ordinal))).ToArray());
    await Expect(Page.Locator("[data-event-type=\"tools.batch-created\"]")).ToHaveCountAsync(2);
  }
  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task NativeParallelReadsActuallyOverlapAndRespectTheLimit()
  {
    using var saved = await _environment.PutSettingsAsync(_environment.BaselineSettings with
    {
      Execution = _environment.BaselineSettings.Execution with { ParallelToolExecution = "auto", MaxConcurrentToolCalls = 2 }
    });
    saved.EnsureSuccessStatusCode();
    // Real filesystem I/O, including observation hashes, keeps the reads in flight.
    var block = new byte[1024 * 1024];
    Array.Fill(block, (byte)'x');
    foreach (var name in new[] { "multi-a.txt", "multi-b.txt" })
    {
      await using var file = File.Create(Path.Combine(_environment.WorkspaceDirectory, name));
      for (var index = 0; index < 64; index++) await file.WriteAsync(block);
    }
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    var responseTask = Page.WaitForResponseAsync(response => response.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal));
    await SendMessageAsync("NATIVE_MULTITOOL_V1 overlap");
    var response = await responseTask;
    var events = ParseSseEvents(await response.TextAsync());
    var telemetry = events.Where(item => item["toolExecution"] is JsonObject)
      .Select(item => item["toolExecution"]!.AsObject()).ToArray();
    Assert.IsTrue(telemetry.Any(item => item["peak"]!.GetValue<int>() == 2), "Real read execution must overlap.");
    Assert.IsTrue(telemetry.All(item => item["peak"]!.GetValue<int>() <= 2));
    var sessionId = events.Last(item => item["executionSession"] is JsonObject)["executionSession"]!["id"]!.GetValue<string>();
    var review = await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/execution-sessions/{sessionId}/review");
    var batch = review!["nativeToolBatches"]!.AsArray().Last()!;
    Assert.AreEqual(2, batch["peakConcurrency"]!.GetValue<int>());
    var calls = batch["calls"]!.AsArray();
    Assert.HasCount(3, calls);
    Assert.IsTrue(calls.All(call => call!["status"]!.GetValue<string>() == "completed"));
    Assert.IsTrue(calls.All(call => call!["durationMilliseconds"] is not null));
    Assert.IsFalse(batch.ToJsonString().Contains("xxxx", StringComparison.Ordinal), "Checkpoint metadata must not store file contents.");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeBatchRequiresApprovalForEachMutationAndStopsOnDenial()
  {
    File.Delete(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"));
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("ask");
    await StartMessageAsync("NATIVE_MULTITOOL_V1 mutate ask");
    await Expect(Page.Locator(".action-approval").Last).ToBeVisibleAsync();
    Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
    await Page.Locator(".action-approval").Last.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
    await Expect(Page.Locator(".action-approval").Last).ToContainTextAsync("create_directory");
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
    await Page.Locator(".action-approval").Last.GetByRole(AriaRole.Button, new() { Name = "Reject", Exact = true }).ClickAsync();
    await Expect(Page.Locator("[data-event-type=\"action.rejected\"]")).ToBeAttachedAsync();
    Assert.IsFalse(Directory.Exists(Path.Combine(_environment.WorkspaceDirectory, "additional-call")));
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(request => request.Messages.Any(message =>
      message.ToolCallId == "verify" && message.Role == "tool")));
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task NativeBatchCheckpointsSurviveHostRestartWithoutReplayingCalls()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using var history = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspaceId}/history", new { enabled = true });
    history.EnsureSuccessStatusCode();
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "multi-a.txt"), "first input");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "multi-b.txt"), "second input");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    var responseTask = Page.WaitForResponseAsync(response => response.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal));
    await SendMessageAsync("NATIVE_MULTITOOL_V1 history");
    var events = ParseSseEvents(await (await responseTask).TextAsync());
    var sessionId = events.Last(item => item["executionSession"] is JsonObject)["executionSession"]!["id"]!.GetValue<string>();
    var conversationId = await Page.EvaluateAsync<string>("() => state.conversationSessionId");
    var callsBefore = _environment.FakeOllama.Requests.Count;
    await _environment.RestartApplicationAsync();
    var review = await _environment.HttpClient.GetFromJsonAsync<JsonObject>(
      $"api/execution-sessions/{sessionId}/review?conversationSessionId={conversationId}&workspaceId={workspaceId}");
    var batch = review!["nativeToolBatches"]!.AsArray().Last()!;
    Assert.HasCount(3, batch["calls"]!.AsArray());
    Assert.IsTrue(batch["calls"]!.AsArray().All(call => call!["status"]!.GetValue<string>() == "completed"));
    Assert.HasCount(callsBefore, _environment.FakeOllama.Requests, "Restoring review must not perform inference or replay tools.");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeRejectsAmbiguousDuplicateIdsBeforeEffectsAndRecovers()
  {
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "multi-a.txt"), "first input");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "multi-b.txt"), "second input");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("NATIVE_MULTITOOL_V1 duplicate");
    Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "duplicate-unsafe.txt")));
    Assert.IsFalse(Directory.Exists(Path.Combine(_environment.WorkspaceDirectory, "duplicate-unsafe-directory")));
    await Expect(Page.Locator(".message.assistant .assistant-answer").Last).ToContainTextAsync("NATIVE_MULTITOOL_OK");
    await Expect(Page.Locator("[data-event-type=\"action.planning-retry\"]")).ToHaveCountAsync(1);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeSettingsPersistThroughUiAndRejectInvalidUpdatesAtomically()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();
    await Page.Locator("#parallel-tool-execution").SelectOptionAsync("on");
    await Page.Locator("#max-concurrent-tool-calls").FillAsync("3");
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    var saved = JsonNode.Parse(await _environment.HttpClient.GetStringAsync("api/settings"))!.AsObject();
    Assert.AreEqual("on", saved["execution"]!["parallelToolExecution"]!.GetValue<string>());
    Assert.AreEqual(3, saved["execution"]!["maxConcurrentToolCalls"]!.GetValue<int>());
    var yaml = await _environment.HttpClient.GetStringAsync("api/settings/yaml");
    StringAssert.Contains(yaml, "parallel_tool_execution: \"on\"");
    StringAssert.Contains(yaml, "max_concurrent_tool_calls: 3");
    using var imported = await _environment.HttpClient.PutAsJsonAsync("api/settings/yaml", new { yaml });
    imported.EnsureSuccessStatusCode();
    saved["execution"]!["maxConcurrentToolCalls"] = 0;
    using var rejected = await _environment.HttpClient.PutAsJsonAsync("api/settings", saved);
    Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
    var unchanged = JsonNode.Parse(await _environment.HttpClient.GetStringAsync("api/settings"))!;
    Assert.AreEqual(3, unchanged["execution"]!["maxConcurrentToolCalls"]!.GetValue<int>());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CancellingNativeBatchMarksPendingCallsInterruptedAndExecutesNoMutation()
  {
    File.Delete(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"));
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("ask");
    await StartMessageAsync("NATIVE_MULTITOOL_V1 mutate cancel");
    await Expect(Page.Locator(".action-approval").Last).ToBeVisibleAsync();
    var sessionId = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
    await Page.Locator("#cancel-request").ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true");
    var review = await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/execution-sessions/{sessionId}/review");
    var calls = review!["nativeToolBatches"]!.AsArray().Last()!["calls"]!.AsArray();
    Assert.HasCount(3, calls);
    Assert.IsTrue(calls.All(call => call!["status"]!.GetValue<string>() == "interrupted"));
    Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
  }

}
