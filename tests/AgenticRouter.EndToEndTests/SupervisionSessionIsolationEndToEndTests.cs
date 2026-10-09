using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class SupervisionSessionIsolationEndToEndTests
  : ChatEndToEndTestBase<SupervisionSessionIsolationEndToEndTests>
{
  private readonly List<string> _runIds = [];

  [TestCleanup]
  public async Task StopFixtureRunsAsync()
  {
    foreach (var runId in _runIds)
    {
      var run = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/supervision/runs/{runId}"))!;
      if (run["terminal"]!.GetValue<bool>()) continue;
      using var cancelled = await _environment.HttpClient.PostAsJsonAsync($"api/supervision/runs/{runId}/cancel", new { });
      cancelled.EnsureSuccessStatusCode();
      var timer = Stopwatch.StartNew();
      while (timer.Elapsed < TimeSpan.FromSeconds(10))
      {
        run = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/supervision/runs/{runId}"))!;
        if (!run["executionActive"]!.GetValue<bool>()) break;
        await Task.Delay(100);
      }
    }
  }

  [TestMethod]
  [DataRow("opencode", false, false)]
  [DataRow("qwen-code", false, false)]
  [DataRow("claude-code", false, false)]
  [DataRow("codex", false, false)]
  [DataRow("opencode", true, false)]
  [DataRow("qwen-code", true, false)]
  [DataRow("claude-code", true, false)]
  [DataRow("codex", true, false)]
  [DataRow("opencode", false, true)]
  [DataRow("qwen-code", false, true)]
  [DataRow("claude-code", false, true)]
  [DataRow("codex", false, true)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task SupervisorAndWorkersKeepIndependentReusableNativeSessions(string harness, bool resume, bool correction)
  {
    await Page.GotoAsync("/");
    ResetFixture(harness);
    if (resume)
    {
      var workspaceId = await ActiveWorkspaceIdAsync();
      using var enabled = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history", new { enabled = true });
      enabled.EnsureSuccessStatusCode();
    }
    var browserId = Guid.NewGuid().ToString("N");
    var runId = await PrepareAsync(harness, browserId, resume, correction);
    await PostAsync($"/api/supervision/runs/{runId}/start", new { });
    if (resume)
    {
      await WaitForStateAsync(runId, "awaiting-user");
      // Approval authority may move to a new browser without changing the run's
      // logical contexts or their native sessions.
      browserId = Guid.NewGuid().ToString("N");
      await PostAsync($"/api/supervision/runs/{runId}/resume", new
      {
        browserSessionId = browserId
      });
    }
    var completed = await WaitForStateAsync(runId, "completed");
    Assert.IsNull(completed["runtime"]!["completionReport"]);
    Assert.AreEqual(runId, completed["runtime"]!["completionCheckId"]!.GetValue<string>());
    using var checkResponse = await _environment.HttpClient.PostAsJsonAsync($"api/completion-checks/{runId}",
      new { browserSessionId = browserId });
    checkResponse.EnsureSuccessStatusCode();
    var completionReport = JsonNode.Parse(await checkResponse.Content.ReadAsStringAsync())!;
    Assert.AreEqual("available", completionReport["status"]!.GetValue<string>());
    var reportRequests = _environment.FakeOllama.Requests.Where(request => request.Messages.Any(message =>
      message.Role == "system" && message.Content.StartsWith("EXECUTION_COMPLETION_REPORT_V1", StringComparison.Ordinal))).ToArray();
    Assert.HasCount(1, reportRequests);
    Assert.IsFalse(reportRequests[0].HasTools);
    StringAssert.Contains(reportRequests[0].Messages.Last().Content, "isolation-001.txt");
    var reread = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/supervision/runs/{runId}"))!;
    Assert.IsNull(reread["runtime"]!["completionReport"]);
    Assert.AreEqual(runId, reread["runtime"]!["completionCheckId"]!.GetValue<string>());
    var prompts = await ReadPromptsAsync(harness);
    var supervisors = prompts.Where(prompt => Phase(prompt) is "decompose" or "verify" or "complete").ToArray();
    var workers = prompts.Where(prompt => Phase(prompt) == "worker").ToArray();
    Assert.HasCount(resume || correction ? 5 : 4, supervisors, "No extra model turns should be introduced.");
    Assert.HasCount(correction ? 3 : 2, workers);
    Assert.HasCount(1, supervisors.Select(Session).Distinct());
    Assert.AreNotEqual(Session(supervisors[0]), Session(workers[0]));
    var workerOne = workers.Where(prompt => prompt["item"]!.GetValue<string>() == "work-001").ToArray();
    var workerTwo = workers.Where(prompt => prompt["item"]!.GetValue<string>() == "work-002").ToArray();
    Assert.HasCount(correction ? 2 : 1, workerOne);
    Assert.HasCount(1, workerTwo);
    Assert.HasCount(1, workerOne.Select(Session).Distinct());
    Assert.AreNotEqual(Session(supervisors[0]), Session(workerTwo[0]));
    Assert.AreNotEqual(Session(workerOne[0]), Session(workerTwo[0]));
    if (harness == "qwen-code")
    {
      Assert.HasCount(1, supervisors.Select(prompt => prompt["processId"]!.GetValue<int>()).Distinct());
      Assert.HasCount(1, workers.Select(prompt => prompt["processId"]!.GetValue<int>()).Distinct());
    }
    var contexts = completed["runtime"]!["contexts"]!.AsArray();
    CollectionAssert.AreEquivalent(new[] { "supervisor-001", "worker-001", "worker-002" },
      contexts.Select(context => context!["id"]!.GetValue<string>()).ToArray());
    Assert.IsTrue(completed["runtime"]!["workItems"]!.AsArray().All(item => item!["status"]!.GetValue<string>() == "completed"));
    foreach (var index in new[] { 1, 2 })
      Assert.AreEqual("isolated", await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, $"isolation-{index:000}.txt")));

    if (!resume)
    {
      // Reusing a conversation for another run must not alias context names.
      foreach (var index in new[] { 1, 2 })
        File.Delete(Path.Combine(_environment.WorkspaceDirectory, $"isolation-{index:000}.txt"));
      var nextRun = await PrepareAsync(harness, browserId, false);
      await PostAsync($"/api/supervision/runs/{nextRun}/start", new { });
      await WaitForStateAsync(nextRun, "completed");
      var nextPrompts = (await ReadPromptsAsync(harness)).Skip(prompts.Length).ToArray();
      Assert.HasCount(6, nextPrompts);
      Assert.IsFalse(nextPrompts.Select(Session).Intersect(prompts.Select(Session)).Any());
      if (harness == "qwen-code")
      {
        AssertProcessesStopped(prompts);
        await SendDirectAsync(harness, browserId, "conversation-after-supervision");
        AssertProcessesStopped(nextPrompts);
      }
    }
  }

  [TestMethod]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  [DataRow("claude-code")]
  [DataRow("codex")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DirectExecutionPreservesBrowserIdentityAndRequiresBrowserSession(string harness)
  {
    await Page.GotoAsync("/");
    ResetFixture(harness);
    var browserId = Guid.NewGuid().ToString("N");
    await SendDirectAsync(harness, browserId, "conversation-a");
    await SendDirectAsync(harness, browserId, "conversation-b");
    await SendDirectAsync(harness, "another-" + browserId, "conversation-b");
    await SendDirectAsync(harness, null, "conversation-b", expectedError: "execution-session");
    var prompts = await ReadPromptsAsync(harness);
    Assert.HasCount(3, prompts);
    Assert.AreEqual(Session(prompts[0]), Session(prompts[1]));
    Assert.AreNotEqual(Session(prompts[1]), Session(prompts[2]));
    StringAssert.Contains(prompts[0]["text"]!.GetValue<string>(), "DIRECT-HISTORY");
    Assert.IsFalse(prompts[1]["text"]!.GetValue<string>().Contains("DIRECT-HISTORY", StringComparison.Ordinal));
    Assert.IsFalse(prompts[1]["text"]!.GetValue<string>().Contains("Canonical Agentic Router conversation", StringComparison.Ordinal));
  }

  private async Task<string> PrepareAsync(string harness, string browserId, bool resume, bool correction = false)
  {
    var prepared = await PostAsync("/api/supervision/runs/prepare", new
    {
      objective = "session isolation fixture: create isolation-001.txt and isolation-002.txt containing isolated."
        + (resume ? " pause for resume" : "")
        + (correction ? " corrective worker" : ""),
      model = Model(harness),
      harness,
      browserSessionId = browserId,
      approvalPolicy = "auto",
      resumePolicy = "manual"
    });
    var runId = prepared["runId"]!.GetValue<string>();
    _runIds.Add(runId);
    return runId;
  }

  private async Task SendDirectAsync(string harness, string? browserId, string conversationId, string? expectedError = null)
  {
    var stream = await Page.EvaluateAsync<string>("""
      async request => await (await fetch('/api/chat/stream', {
        method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(request)
      })).text()
      """, new
    {
      message = "Inspect the workspace: session isolation fixture direct.",
      model = Model(harness),
      harness,
      browserSessionId = browserId,
      conversationSessionId = conversationId,
      history = new[]
      {
        new { role = "user", content = "DIRECT-HISTORY: preserve the existing workspace." },
        new { role = "assistant", content = "Acknowledged." }
      },
      interactionMode = "execute",
      executionStrategy = "direct",
      approvalPolicy = "auto"
    });
    var errors = ParseSseEvents(stream).Where(item => item["type"]!.GetValue<string>() == "error").ToArray();
    Assert.HasCount(expectedError is null ? 0 : 1, errors, stream);
    if (expectedError is not null) Assert.AreEqual(expectedError, errors[0]["error"]!["code"]!.GetValue<string>());
  }

  private async Task<JsonNode> PostAsync(string url, object body)
  {
    var json = await Page.EvaluateAsync<string>("""
      async ({url, body}) => {
        const response = await fetch(url, {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body)});
        if (!response.ok) throw new Error(await response.text());
        return await response.text();
      }
      """, new { url, body });
    return JsonNode.Parse(json)!;
  }

  private async Task<JsonNode> WaitForStateAsync(string runId, string expected)
  {
    var timer = Stopwatch.StartNew();
    JsonNode? run = null;
    while (timer.Elapsed < TimeSpan.FromSeconds(30))
    {
      run = JsonNode.Parse(await Page.EvaluateAsync<string>(
        "async id => await (await fetch('/api/supervision/runs/' + id)).text()", runId))!;
      var state = run["state"]!.GetValue<string>();
      if (state == expected && run["executionActive"]?.GetValue<bool>() != true) return run;
      if (state is "blocked" or "failed" or "cancelled") Assert.Fail(run.ToJsonString());
      await Task.Delay(100);
    }
    Assert.Fail(run?.ToJsonString() ?? "No supervision state was returned.");
    return null!;
  }

  private void ResetFixture(string harness)
  {
    if (File.Exists(Marker(harness))) File.Delete(Marker(harness));
    foreach (var index in new[] { 1, 2 })
    {
      var path = Path.Combine(_environment.WorkspaceDirectory, $"isolation-{index:000}.txt");
      if (File.Exists(path)) File.Delete(path);
    }
  }

  private async Task<JsonNode[]> ReadPromptsAsync(string harness) =>
    (await File.ReadAllLinesAsync(Marker(harness))).Select(line => JsonNode.Parse(line)!).ToArray();

  private static void AssertProcessesStopped(IEnumerable<JsonNode> prompts)
  {
    foreach (var processId in prompts.Select(prompt => prompt["processId"]!.GetValue<int>()).Distinct())
    {
      try
      {
        using var process = Process.GetProcessById(processId);
        Assert.IsTrue(process.HasExited, "The preceding Qwen session group's owned runtime must be stopped.");
      }
      catch (ArgumentException) { /* The owned process no longer exists. */ }
    }
  }

  private static string Marker(string harness) => Path.Combine(_environment.DataDirectory,
    harness + "-runtime", "fake-session-isolation.jsonl");
  private static string Model(string harness) => harness == "claude-code" ? "qwen3-coder:30b" : "qwen3.8:27b-gpu0";
  private static string Phase(JsonNode prompt) => prompt["phase"]!.GetValue<string>();
  private static string Session(JsonNode prompt) => prompt["sessionId"]!.GetValue<string>();
}
