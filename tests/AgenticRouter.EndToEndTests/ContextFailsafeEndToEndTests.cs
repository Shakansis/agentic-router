using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class ContextFailsafeEndToEndTests : ChatEndToEndTestBase<ContextFailsafeEndToEndTests>
{
  [TestMethod]
  [DataRow("qwen-code", false)]
  [DataRow("opencode", false)]
  [DataRow("codex", false)]
  [DataRow("claude-code", false)]
  [DataRow("qwen-code", true)]
  [DataRow("opencode", true)]
  [DataRow("codex", true)]
  [DataRow("claude-code", true)]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ContextFailureRecoversOnceWithCanonicalConstraintsAndCommittedEffects(string harness, bool repeated)
  {
    await Page.GotoAsync("/");
    var marker = Marker(harness);
    if (File.Exists(marker)) File.Delete(marker);
    var effect = Path.Combine(_environment.WorkspaceDirectory, "context-effect.txt");
    if (File.Exists(effect)) File.Delete(effect);
    var recoveryEffect = Path.Combine(_environment.WorkspaceDirectory, "recovery-effect.txt");
    if (File.Exists(recoveryEffect)) File.Delete(recoveryEffect);
    var events = await SendAsync(harness,
      "reactive context fixture with committed effect" + (repeated ? " always fail" : ""),
      [new { role = "user", content = "USER-CONSTRAINT-KEEP: preserve the original file." },
       new { role = "assistant", content = "Prior canonical decision is retained." }]);
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(1, events.Where(item => item["type"]!.GetValue<string>() == "harness.context-recovery-started"));
    Assert.HasCount(repeated ? 1 : 0, events.Where(item => item["type"]!.GetValue<string>() == "error"));
    if (repeated)
      Assert.AreEqual("harness-context-recovery-exhausted",
        events.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["code"]!.GetValue<string>());
    var prompts = (await File.ReadAllLinesAsync(marker)).Select(line => JsonNode.Parse(line)!).ToArray();
    Assert.HasCount(2, prompts);
    Assert.AreNotEqual(prompts[0]["sessionId"]!.GetValue<string>(), prompts[1]["sessionId"]!.GetValue<string>());
    Assert.IsFalse(prompts[0]["recovering"]!.GetValue<bool>());
    Assert.IsTrue(prompts[1]["recovering"]!.GetValue<bool>());
    var packet = prompts[1]["text"]!.GetValue<string>();
    StringAssert.Contains(packet, "USER-CONSTRAINT-KEEP");
    StringAssert.Contains(packet, "Prior canonical decision is retained.");
    StringAssert.Contains(packet, "context-effect.txt");
    StringAssert.Contains(packet, "Do not repeat committed actions or commands.");
    Assert.AreEqual("committed once\n", await File.ReadAllTextAsync(effect));
    var execution = events.Last(item => item["executionSession"] is not null)["executionSession"]!;
    var reviewText = await Page.EvaluateAsync<string>("async id => await (await fetch('/api/execution-sessions/' + id + '/review')).text()",
      execution["id"]!.GetValue<string>());
    var review = JsonNode.Parse(reviewText)!;
    if (repeated)
      Assert.IsTrue(review["files"]!.AsArray().Any(file => file?["relativePath"]?.GetValue<string>() == "recovery-effect.txt"));
    Assert.HasCount(1, review["actions"]!.AsArray().Where(action =>
      action?["summary"]?.GetValue<string>() == "Host-observed harness created: context-effect.txt"));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "harness.codex-automatic-continuation-started"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SupervisorDoesNotMultiplyExhaustedContextRetries()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    var events = await SendAsync("qwen-code", "reactive context fixture always fail", [], "supervised");
    Assert.HasCount(2, await File.ReadAllLinesAsync(marker));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "supervision.turn-harness-recovery"));
    StringAssert.Contains(string.Join("\n", events.Select(item => item.ToJsonString())), "harness-context-recovery-exhausted");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CanonicalSupervisorRetryCannotResetTheConsumedContextBudget()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    // The fake's plain recovery answer is intentionally not a supervisor JSON decision.
    var events = await SendAsync("qwen-code", "reactive context fixture", [], "supervised");
    var prompts = (await File.ReadAllLinesAsync(marker)).Select(line => JsonNode.Parse(line)!).ToArray();
    Assert.HasCount(3, prompts);
    Assert.HasCount(1, prompts.Where(prompt => prompt["recovering"]!.GetValue<bool>()));
    Assert.AreEqual(prompts[1]["sessionId"]!.GetValue<string>(), prompts[2]["sessionId"]!.GetValue<string>());
    StringAssert.Contains(string.Join("\n", events.Select(item => item.ToJsonString())), "harness-context-recovery-exhausted");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FailedFreshContextSetupRetainsPreviousNativeMapping()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    var failed = await SendAsync("qwen-code", "reactive context fixture setup failure", []);
    Assert.AreEqual("harness-context-recovery-exhausted",
      failed.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["code"]!.GetValue<string>());
    var original = JsonNode.Parse((await File.ReadAllLinesAsync(marker)).Single())!["sessionId"]!.GetValue<string>();
    var next = await SendAsync("qwen-code", "Continue the existing native context.", []);
    Assert.IsFalse(next.Any(item => item["type"]!.GetValue<string>() == "error"));
    var prompt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_environment.DataDirectory, "qwen-code-runtime", "fake-qwen-prompt.json")))!;
    Assert.AreEqual(original, prompt["sessionId"]!.GetValue<string>());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RecoveryTransportFailureStillRecordsObservedEffects()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    var path = Path.Combine(_environment.WorkspaceDirectory, "recovery-effect.txt");
    if (File.Exists(path)) File.Delete(path);
    var events = await SendAsync("qwen-code", "reactive context fixture recovery transport failure", []);
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.AreEqual("harness-context-recovery-exhausted",
      events.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["code"]!.GetValue<string>());
    Assert.HasCount(2, await File.ReadAllLinesAsync(marker));
    var execution = events.Last(item => item["executionSession"] is not null)["executionSession"]!;
    var reviewText = await Page.EvaluateAsync<string>("async id => await (await fetch('/api/execution-sessions/' + id + '/review')).text()",
      execution["id"]!.GetValue<string>());
    Assert.IsTrue(JsonNode.Parse(reviewText)!["files"]!.AsArray().Any(file =>
      file?["relativePath"]?.GetValue<string>() == "recovery-effect.txt" && file["verified"]!.GetValue<bool>()));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ContextWordsInAnUnrelatedFailureDoNotActivateFailsafe()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    var events = await SendAsync("qwen-code", "reactive context fixture unrelated error", []);
    Assert.HasCount(1, await File.ReadAllLinesAsync(marker));
    Assert.AreEqual("qwen-code-turn-error",
      events.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["code"]!.GetValue<string>());
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "harness.context-recovery-started"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnresolvedNativeActionPreventsContextReplacement()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    var events = await SendAsync("qwen-code", "reactive context fixture unresolved tool", []);
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.AreEqual("harness-context-recovery-unavailable",
      events.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["code"]!.GetValue<string>());
    Assert.HasCount(1, await File.ReadAllLinesAsync(marker));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "harness.context-recovery-started"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RequiredCanonicalHistoryIsNotTruncatedToForceRecovery()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    if (File.Exists(marker)) File.Delete(marker);
    var events = await SendAsync("qwen-code", "reactive context fixture",
      [new { role = "user", content = new string('x', 600_000) }, new { role = "assistant", content = "Recorded." }]);
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.AreEqual("harness-context-recovery-unavailable",
      events.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["code"]!.GetValue<string>());
    Assert.HasCount(1, await File.ReadAllLinesAsync(marker));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ContextRecoveryDoesNotReplaceAnotherConversationOrRestartQwen()
  {
    await Page.GotoAsync("/");
    var healthy = await SendAsync("qwen-code", "Inspect this independent conversation.", [], browserSessionId: "unrelated-failsafe-context");
    Assert.IsFalse(healthy.Any(item => item["type"]!.GetValue<string>().StartsWith("harness.context-recovery", StringComparison.Ordinal)));
    var runtime = Path.Combine(_environment.DataDirectory, "qwen-code-runtime");
    var first = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-prompt.json")))!;
    var processId = await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-process-id.txt"));
    var recovered = await SendAsync("qwen-code", "reactive context fixture", []);
    Assert.HasCount(1, recovered.Where(item => item["type"]!.GetValue<string>() == "harness.context-recovery-started"));
    var continued = await SendAsync("qwen-code", "Continue this independent conversation.", [], browserSessionId: "unrelated-failsafe-context");
    Assert.IsFalse(continued.Any(item => item["type"]!.GetValue<string>().StartsWith("harness.context-recovery", StringComparison.Ordinal)));
    var last = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-prompt.json")))!;
    Assert.AreEqual(first["sessionId"]!.GetValue<string>(), last["sessionId"]!.GetValue<string>());
    Assert.AreEqual(processId, await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-process-id.txt")));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CancellingContextRecoveryStopsWithoutAnotherAttempt()
  {
    await Page.GotoAsync("/");
    var marker = Marker("qwen-code");
    var cancelled = Path.Combine(_environment.DataDirectory, "qwen-code-runtime", "fake-context-cancelled.json");
    if (File.Exists(marker)) File.Delete(marker);
    if (File.Exists(cancelled)) File.Delete(cancelled);
    await Page.EvaluateAsync("""
      () => {
        window.failsafeAbort = new AbortController();
        window.failsafeRequest = fetch('/api/chat/stream', {
          method: 'POST', headers: {'Content-Type': 'application/json'}, signal: window.failsafeAbort.signal,
          body: JSON.stringify({message: 'reactive context fixture pause recovery', history: [],
            harness: 'qwen-code', model: 'qwen3.8:27b-gpu0', browserSessionId: 'context-failsafe-e2e',
            interactionMode: 'execute', approvalPolicy: 'auto', executionStrategy: 'direct'})
        }).then(response => response.text()).catch(error => error.name);
      }
      """);
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while ((!File.Exists(marker) || (await File.ReadAllLinesAsync(marker)).Length < 2) && DateTime.UtcNow < deadline)
      await Task.Delay(100);
    Assert.HasCount(2, await File.ReadAllLinesAsync(marker));
    await Page.EvaluateAsync("() => window.failsafeAbort.abort()");
    deadline = DateTime.UtcNow.AddSeconds(10);
    while (!File.Exists(cancelled) && DateTime.UtcNow < deadline) await Task.Delay(100);
    Assert.IsTrue(File.Exists(cancelled));
    Assert.HasCount(2, await File.ReadAllLinesAsync(marker));
  }

  private static string Marker(string harness) =>
    Path.Combine(_environment.DataDirectory, harness + "-runtime", "fake-context-recovery.jsonl");

  private async Task<JsonObject[]> SendAsync(string harness, string message, object[] history,
    string executionStrategy = "direct", string browserSessionId = "context-failsafe-e2e")
  {
    return ParseSseEvents(await Page.EvaluateAsync<string>("""
      async request => {
        const response = await fetch('/api/chat/stream', {
          method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(request)
        });
        if (!response.ok) throw new Error(await response.text());
        return await response.text();
      }
      """, new
    {
      message,
      history,
      harness,
      browserSessionId,
      model = harness == "claude-code" ? "qwen3-coder:30b" : "qwen3.8:27b-gpu0",
      interactionMode = "execute",
      approvalPolicy = "auto",
      executionStrategy
    }));
  }
}
