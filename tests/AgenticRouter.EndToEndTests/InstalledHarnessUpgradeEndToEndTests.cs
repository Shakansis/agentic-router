using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class InstalledHarnessUpgradeEndToEndTests : PageTest
{
  [TestMethod]
  [DataRow("codex")]
  [DataRow("claude-code")]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledNativeMutationPersistsOriginalBeforeApprovalAndRollsBack(string harness) =>
    VerifyAsync(harness, false, draft: 3, nativeMutation: true);

  [TestMethod]
  [DataRow(-1)]
  [DataRow(0)]
  [DataRow(3)]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledClaudeNoneOverridesHarnessThinkingOnEveryInference(int draft) =>
    VerifyAsync("claude-code", false, draft: draft >= 0 ? draft : null, disableThinking: true);

  [TestMethod]
  [DataRow("codex", 0)]
  [DataRow("codex", 3)]
  [DataRow("opencode", 0)]
  [DataRow("opencode", 3)]
  [DataRow("qwen-code", 0)]
  [DataRow("qwen-code", 3)]
  [DataRow("claude-code", 0)]
  [DataRow("claude-code", 3)]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledHarnessPreservesConfiguredNativeOptionsAndToolResults(string harness, int draft) =>
    VerifyAsync(harness, false, draft: draft);

  [TestMethod]
  [DataRow("codex")]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  [DataRow("claude-code")]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledHarnessCompletesThroughBrowserWithIsolatedProvider(string harness) =>
    VerifyAsync(harness, false);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchPreservesCrlf() => VerifyAsync("codex", true);

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexContinuesAfterFourHostReads(bool disconnect) => VerifyAsync("codex", false, reads: true, disconnect: disconnect);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexAcceptsJsonResponsesAfterHostReads() => VerifyAsync("codex", false, reads: true, json: true);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPreservesJsonProviderFailure() => VerifyAsync("codex", false, jsonError: true);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexJsonPatchRejectsWorkspaceEscape() => VerifyAsync("codex", true, true, json: true);

  [TestMethod]
  [DataRow("application/x-ndjson", false)]
  [DataRow("application/x-ndjson", true)]
  [DataRow("text/plain", false)]
  [DataRow("text/plain", true)]
  [DataRow("", false)]
  [DataRow("", true)]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexAcceptsSseWithCompatibilityHeader(string contentType, bool patch) =>
    VerifyAsync("codex", patch, outside: patch, reads: !patch, contentType: contentType);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchRejectsWorkspaceEscape() => VerifyAsync("codex", true, true);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchWaitsForHostAskApproval() => VerifyAsync("codex", true, ask: true);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchRejectsMoveDestinationEscape() => VerifyAsync("codex", true, moveOutside: true);

  private async Task VerifyAsync(string harness, bool patch, bool outside = false, bool ask = false, bool moveOutside = false, bool reads = false, bool disconnect = false, bool json = false, bool jsonError = false, string? contentType = null, int? draft = null, bool nativeMutation = false, bool disableThinking = false)
  {
    if (Environment.GetEnvironmentVariable("AGENTIC_ROUTER_INSTALLED_HARNESS_E2E") != "1")
      Assert.Inconclusive("Opt in with real executable overrides; provider remains deterministic and local.");
    await using var environment = await TestEnvironment.StartAsync();
    environment.FakeOllama.InstalledHarnessProbe = true;
    environment.FakeOllama.InstalledNativeCompatibilityProbe = draft is not null || disableThinking;
    if (disableThinking) await File.WriteAllTextAsync(Path.Combine(environment.WorkspaceDirectory, "initial.txt"), "NATIVE_COMPATIBILITY_READ_OK");
    environment.FakeOllama.InstalledNativeReadPath = Path.Combine(environment.WorkspaceDirectory, nativeMutation ? "crlf-probe.txt" : "initial.txt");
    byte[]? originalBytes = null;
    if (nativeMutation)
    {
      originalBytes = [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes("FIRST\r\nOLD_VALUE\r\nLAST\r\n")];
      var target = Path.Combine(environment.WorkspaceDirectory, "crlf-probe.txt");
      await File.WriteAllBytesAsync(target, originalBytes);
      environment.FakeOllama.InstalledNativeMutationPath = target;
      environment.FakeOllama.InstalledNativeMutationTool = harness == "claude-code" ? "Edit" : "apply_patch";
    }
    if (draft is not null)
    {
      await File.WriteAllTextAsync(Path.Combine(environment.WorkspaceDirectory, "initial.txt"), "NATIVE_COMPATIBILITY_READ_OK");
      var settings = JsonNode.Parse(await environment.HttpClient.GetStringAsync("api/settings"))!;
      settings["ollamaRuntime"]!["modelOverrides"] = new JsonArray(new JsonObject
      {
        ["provider"] = "ollama-local",
        ["model"] = "qwen3.8:27b-gpu0",
        ["digest"] = "digest-qwen3.8:27b-gpu0",
        ["overrides"] = new JsonObject(),
        ["performance"] = new JsonObject { ["draftTokens"] = draft, ["batchSize"] = 512 }
      });
      using var saved = await environment.HttpClient.PutAsJsonAsync("api/settings", settings);
      saved.EnsureSuccessStatusCode();
    }
    environment.FakeOllama.InstalledCodexPatchProbe = patch;
    environment.FakeOllama.InstalledCodexOutsidePatchProbe = outside;
    environment.FakeOllama.InstalledCodexMoveOutsideProbe = moveOutside;
    environment.FakeOllama.InstalledCodexReadProbe = reads;
    environment.FakeOllama.InstalledCodexDisconnectProbe = disconnect;
    environment.FakeOllama.InstalledCodexJsonProbe = json;
    environment.FakeOllama.InstalledCodexJsonErrorProbe = jsonError;
    environment.FakeOllama.InstalledCodexSseContentType = contentType;
    if (reads) await File.WriteAllTextAsync(Path.Combine(environment.WorkspaceDirectory, "index.html"),
      "<!doctype html><script>" + new string('x', 40_000) + "</script>");
    var patchPath = outside
      ? Path.Combine(Path.GetDirectoryName(environment.WorkspaceDirectory)!, "outside-probe.txt")
      : Path.Combine(environment.WorkspaceDirectory, "crlf-probe.txt");
    if (patch) await File.WriteAllTextAsync(patchPath, "FIRST\r\nOLD_VALUE\r\nLAST\r\n");
    try
    {
      var availability = JsonNode.Parse(await environment.HttpClient.GetStringAsync("api/harnesses"))!
        .AsArray().Single(item => item!["definition"]!["id"]!.GetValue<string>() == harness)!["availability"]!;
      var version = availability["version"]!.GetValue<string>();
      Assert.IsFalse(version.Contains("fake", StringComparison.OrdinalIgnoreCase));
      TestContext.WriteLine($"{harness}: {version}");
      await Page.GotoAsync(environment.BaseUri.ToString());
      await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
      using (var initialized = await environment.HttpClient.PostAsJsonAsync("api/history-files/git-decision",
        new { windowId = Guid.NewGuid().ToString("N"), choice = "initialize" }))
      {
        Assert.IsTrue(initialized.IsSuccessStatusCode, await initialized.Content.ReadAsStringAsync());
      }
      if (nativeMutation)
      {
        var workspaces = JsonNode.Parse(await environment.HttpClient.GetStringAsync("api/workspaces"))!;
        using var history = await environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspaces["activeWorkspaceId"]!.GetValue<string>()}/history", new { enabled = true });
        history.EnsureSuccessStatusCode();
      }
      await Page.Locator("[data-mode=execute]").ClickAsync();
      await Page.Locator("#send-strategy-toggle").ClickAsync();
      await Page.Locator("#send-strategy-menu [data-send-strategy=direct]").ClickAsync();
      await Page.Locator("#approval-policy").SelectOptionAsync(ask ? "ask" : "auto");
      await Page.Locator("#harness-selector").SelectOptionAsync(harness);
      if (disableThinking) await Page.Locator("#thinking-selector").SelectOptionAsync("none");
      await Page.Locator("#message-input").FillAsync(patch
        ? "Replace OLD_VALUE with NEW_VALUE in crlf-probe.txt using apply_patch, preserve other bytes, then respond INSTALLED_HARNESS_OK."
        : reads ? "Inspect index.html using the Host read-only tools, then respond INSTALLED_HARNESS_OK. Do not modify files."
        : "Without tools or changes, respond INSTALLED_HARNESS_OK.");
      var stream = await Page.RunAndWaitForResponseAsync(() => Page.Locator("#send-button").ClickAsync(),
        response => response.Url.Contains("/api/chat/stream", StringComparison.Ordinal));
      if (ask)
      {
        var approval = Page.Locator(".action-approval").Last;
        await Expect(approval).ToBeVisibleAsync();
        Assert.AreEqual("FIRST\r\nOLD_VALUE\r\nLAST\r\n", await File.ReadAllTextAsync(patchPath));
        await approval.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
      }
      await Expect(Page.Locator(".message.assistant .activity").Last)
        .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 90_000 });
      if (jsonError)
      {
        await Expect(Page.Locator(".message.assistant .assistant-answer").Last).Not.ToContainTextAsync("INSTALLED_HARNESS_OK");
        var failedId = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
        var failedReview = JsonNode.Parse(await environment.HttpClient.GetStringAsync($"api/execution-sessions/{failedId}/review"))!;
        Assert.AreEqual("failed", failedReview["summary"]!["state"]!.GetValue<string>());
        Assert.IsEmpty(failedReview["files"]!.AsArray());
        Assert.DoesNotContain("Harness inference transport ended", environment.ApiOutput);
        await Expect(Page.Locator(".message.assistant .activity").Last).ToContainTextAsync("JSON_PROVIDER_FAILURE");
        // Codex performs five native retries; the Host permits one continuation.
        Assert.HasCount(12, environment.FakeOllama.InstalledHarnessRequests);
        return;
      }
      await Expect(Page.Locator(".message.assistant .assistant-answer").Last)
        .ToContainTextAsync("INSTALLED_HARNESS_OK");
      var id = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
      var review = JsonNode.Parse(await environment.HttpClient.GetStringAsync($"api/execution-sessions/{id}/review"))!;
      if (!outside && !moveOutside) Assert.IsTrue(review["summary"]!["state"]!.GetValue<string>().StartsWith("completed", StringComparison.Ordinal));
      Assert.IsNotEmpty(environment.FakeOllama.InstalledHarnessRequests);
      if (disableThinking)
      {
        Assert.IsNotEmpty(environment.FakeOllama.InstalledHarnessRequests);
        Assert.IsTrue(environment.FakeOllama.InstalledHarnessRequests.All(item => item.GetProperty("think").ValueKind == System.Text.Json.JsonValueKind.False), "None must disable thinking on every model request, including tool continuations.");
      }
      if (draft is not null)
      {
        foreach (var request in environment.FakeOllama.InstalledHarnessRequests)
        {
          if (disableThinking) Assert.IsFalse(request.GetProperty("think").GetBoolean(), "Explicit None must override SDK thinking defaults on every inference.");
          Assert.AreEqual(draft.Value, request.GetProperty("options").GetProperty("draft_num_predict").GetInt32());
          Assert.AreEqual(512, request.GetProperty("options").GetProperty("num_batch").GetInt32());
        }
        Assert.IsNotEmpty(environment.FakeOllama.Requests.Where(request => request.DraftTokens == draft));
        Assert.IsTrue(environment.FakeOllama.InstalledHarnessRequests.Any(request => request.GetProperty("messages").EnumerateArray()
          .Any(message => message.GetProperty("role").GetString() == "tool" && message.GetProperty("content").GetString()!.Contains(nativeMutation ? "OLD_VALUE" : "NATIVE_COMPATIBILITY_READ_OK", StringComparison.Ordinal))), "The native tool result must return to the provider.");
        Assert.AreEqual(30d, review["summary"]!["inferenceMetrics"]!["prefillMilliseconds"]!.GetValue<double>() / environment.FakeOllama.InstalledHarnessRequests.Count);
        Assert.AreEqual(10L * environment.FakeOllama.InstalledHarnessRequests.Count,
          review["summary"]!["inferenceMetrics"]!["outputTokens"]!.GetValue<long>());
        Assert.AreEqual(250d, review["summary"]!["inferenceMetrics"]!["tokensPerSecond"]!.GetValue<double>());
        Assert.AreEqual("provider", review["summary"]!["inferenceMetrics"]!["generationTimingSource"]!.GetValue<string>());
      }
      if (nativeMutation)
      {
        var target = environment.FakeOllama.InstalledNativeMutationPath!;
        StringAssert.Contains(await File.ReadAllTextAsync(target), "NEW_VALUE");
        var events = (await stream.TextAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
          .Where(block => block.StartsWith("data: ", StringComparison.Ordinal)).Select(block => JsonNode.Parse(block[6..])!).ToArray();
        var snapshotIndex = Array.FindIndex(events, item => item["hostFileProgress"]?["action"]?.GetValue<string>() == "Creating snapshot"
          && item["hostFileProgress"]?["state"]?.GetValue<string>() == "completed");
        var approvalIndex = Array.FindIndex(events, item => item["type"]?.GetValue<string>() == "action.approved"
          && item["message"]?.GetValue<string>().Contains("approved the workspace-confined", StringComparison.Ordinal) == true);
        Assert.IsGreaterThanOrEqualTo(0, snapshotIndex);
        Assert.IsGreaterThan(snapshotIndex, approvalIndex);
        var savedFiles = JsonNode.Parse(await environment.HttpClient.GetStringAsync("api/history-files"))!.AsArray();
        var savedFile = savedFiles.First(item => item!["name"]!.GetValue<string>() == "crlf-probe.txt")!;
        using var copy = await environment.HttpClient.PostAsJsonAsync("api/history-files/download", new { fileId = savedFile["id"]!.GetValue<string>() });
        copy.EnsureSuccessStatusCode();
        CollectionAssert.AreEqual(originalBytes!, await copy.Content.ReadAsByteArrayAsync());
        await Page.Locator(".message.user .edit-message").Last.ClickAsync();
        await Expect(Page.Locator(".host-file-operation")).ToContainTextAsync("Rollback complete");
        CollectionAssert.AreEqual(originalBytes!, await File.ReadAllBytesAsync(target));
      }
      Assert.DoesNotContain("AuthorizationValid=False", environment.ApiOutput);
      if (reads)
      {
        Assert.HasCount(disconnect ? 6 : 5, environment.FakeOllama.InstalledHarnessRequests);
        if (disconnect)
        {
          StringAssert.Contains(environment.ApiOutput, "Harness inference transport ended with HttpRequestException;");
          StringAssert.Contains(environment.ApiOutput, "System.Net.Http.HttpRequestException");
        }
        var last = environment.FakeOllama.InstalledHarnessRequests.Last();
        foreach (var callId in new[] { "call_read_probe_1", "call_read_probe_2", "call_read_probe_3", "call_read_probe_4" })
          Assert.IsTrue(last.GetProperty("input").EnumerateArray().Any(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            && item.GetProperty("call_id").GetString() == callId
            && item.GetProperty("output").ToString().Contains("succeeded", StringComparison.Ordinal)));
        Assert.IsEmpty(review["files"]!.AsArray());
        Assert.AreEqual("<!doctype html><script>" + new string('x', 40_000) + "</script>",
          await File.ReadAllTextAsync(Path.Combine(environment.WorkspaceDirectory, "index.html")));
      }
      if (patch)
      {
        CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes(outside || moveOutside
          ? "FIRST\r\nOLD_VALUE\r\nLAST\r\n" : "FIRST\r\nNEW_VALUE\r\nLAST\r\n"),
          await File.ReadAllBytesAsync(patchPath));
        Assert.IsGreaterThanOrEqualTo(2, environment.FakeOllama.InstalledHarnessRequests.Count);
        foreach (var request in environment.FakeOllama.InstalledHarnessRequests)
        {
          var tool = request.GetProperty("tools").EnumerateArray().Single(item =>
            item.TryGetProperty("name", out var name) && name.GetString() == "apply_patch");
          Assert.AreEqual("function", tool.GetProperty("type").GetString());
        }
        Assert.IsTrue(environment.FakeOllama.InstalledHarnessRequests.Skip(1).Any(request =>
          request.GetProperty("input").EnumerateArray().Any(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            && item.GetProperty("call_id").GetString() == "call_patch_probe")));
        if (moveOutside) Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(environment.WorkspaceDirectory)!, "escaped-move.txt")));
        if (outside || moveOutside)
          Assert.IsTrue(environment.FakeOllama.InstalledHarnessRequests.Skip(1).Any(request =>
            request.GetProperty("input").EnumerateArray().Any(item =>
              item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
              && item.GetProperty("call_id").GetString() == "call_patch_probe"
              && item.GetProperty("output").ToString().Contains("rejected", StringComparison.OrdinalIgnoreCase))));
      }
      foreach (var request in environment.FakeOllama.InstalledHarnessRequests)
      {
        Assert.AreEqual("qwen3.8:27b-gpu0", request.GetProperty("model").GetString());
        if (harness == "opencode") Assert.IsFalse(request.TryGetProperty("textVerbosity", out _));
      }
      if (harness == "qwen-code")
      {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(environment.DataDirectory, "qwen-code-runtime", "settings.json")))!;
        Assert.IsFalse(config["agents"]!["crossSessionMessaging"]!.GetValue<bool>());
      }
    }
    catch
    {
      TestContext.WriteLine(environment.ApiOutput);
      TestContext.WriteLine(string.Join("\n", environment.FakeOllama.Errors));
      foreach (var request in environment.FakeOllama.InstalledHarnessRequests.TakeLast(2))
      {
        if (request.TryGetProperty("tools", out var tools))
        {
          TestContext.WriteLine("Offered tools: " + string.Join(", ", tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString())));
          foreach (var tool in tools.EnumerateArray().Where(tool => tool.GetProperty("function").GetProperty("name").GetString() is "tool_search" or "tool_call")) TestContext.WriteLine("Discovery schema: " + tool.GetRawText());
        }
        if (request.TryGetProperty("messages", out var messages))
          foreach (var message in messages.EnumerateArray().Where(message => message.GetProperty("role").GetString() == "tool"))
          {
            var content = message.GetProperty("content").GetString() ?? "";
            TestContext.WriteLine("Fixture tool result: " + content[..Math.Min(content.Length, 600)]);
          }
      }
      throw;
    }
  }
}
