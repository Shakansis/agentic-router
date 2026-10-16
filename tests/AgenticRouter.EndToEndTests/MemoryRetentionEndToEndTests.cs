using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class MemoryRetentionEndToEndTests : ChatEndToEndTestBase<MemoryRetentionEndToEndTests>
{
  [TestMethod]
  [Timeout(180_000, CooperativeCancellation = true)]
  public async Task RepeatedExecuteTurnsKeepBoundedMemoryAndRecoverExactHistoryAfterRestart()
  {
    await Page.GotoAsync("/");
    var workspace = await ActiveWorkspaceIdAsync();
    using (var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspace}/history", new { enabled = true })) enabled.EnsureSuccessStatusCode();
    var window = Guid.NewGuid().ToString("N");
    using (var decision = await _environment.HttpClient.PostAsJsonAsync("api/history-files/git-decision", new { windowId = window, choice = "context-only" })) decision.EnsureSuccessStatusCode();
    using var created = await _environment.HttpClient.PostAsJsonAsync("api/sessions/new", new { browserSessionId = window });
    created.EnsureSuccessStatusCode();
    var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["sessionId"]!.GetValue<string>();
    using var process = Process.GetProcessById(_environment.ApiProcessId);
    process.Refresh(); var baseline = process.PrivateMemorySize64; var peak = baseline;
    var exact = new List<string>();
    for (var turn = 0; turn < 30; turn++)
    {
      using var response = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream", new
      {
        message = "codex incident reasoning flood",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "codex",
        approvalPolicy = "auto",
        executionStrategy = "direct",
        chatRunId = Guid.NewGuid().ToString("N"),
        conversationSessionId = id,
        browserSessionId = window,
        historyWindowId = window
      });
      response.EnsureSuccessStatusCode();
      var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
      Assert.IsTrue(events.Any(item => item["type"]?.GetValue<string>() == "response.completed"), string.Join("\n", events.Where(item => item["type"]?.GetValue<string>() == "error").Select(item => item.ToJsonString())));
      exact.Add(string.Concat(events.Where(item => item["type"]?.GetValue<string>() == "reasoning.delta").Select(item => item["reasoningDelta"]?.GetValue<string>())));
      process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64);
    }
    TestContext.WriteLine($"Execute sequence: turns=30; baseline_private={baseline}; observed_peak_private={peak}; delta_private={peak - baseline}");
    Assert.IsLessThan(256L * 1024 * 1024, peak - baseline, "Completed work must not grow retained memory without bound in the representative execution sequence.");
    await _environment.RestartApplicationAsync();
    using var opened = await _environment.HttpClient.PostAsJsonAsync($"api/sessions/{id}/open", new { browserSessionId = window });
    opened.EnsureSuccessStatusCode();
    var session = JsonNode.Parse(await opened.Content.ReadAsStringAsync())!;
    var messages = session["messages"]!.AsArray().Skip(session["presentationOffset"]!.GetValue<int>()).ToList();
    var before = session["presentationOffset"]!.GetValue<int>();
    while (before > 0)
    {
      var page = await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/sessions/{id}/history?workspaceId={workspace}&before={before}");
      var offset = page!["startIndex"]!.GetValue<int>();
      Assert.IsLessThan(before, offset, "History pagination must advance towards the oldest turn.");
      messages.InsertRange(0, page["messages"]!.AsArray());
      before = offset;
    }
    Assert.HasCount(60, messages);
    Assert.IsTrue(exact.All(text => text.Length > 0), "Every synthetic turn must produce reasoning to exercise durable timeline recovery.");
    var restored = messages.Where(item => item!["role"]!.GetValue<string>() == "assistant")
      .Select(item => string.Concat(item!["timeline"]!.AsArray().Where(evt => evt!["type"]!.GetValue<string>() == "reasoning.delta").Select(evt => evt!["reasoningDelta"]?.GetValue<string>()))).ToArray();
    CollectionAssert.AreEqual(exact.ToArray(), restored);
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task LargeHistoryWritePreservesContentWithoutWholeDocumentCopies()
  {
    await Page.GotoAsync("/");
    var workspace = await ActiveWorkspaceIdAsync();
    using (var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspace}/history", new { enabled = true })) enabled.EnsureSuccessStatusCode();
    using var created = await _environment.HttpClient.PostAsJsonAsync("api/sessions/new", new { browserSessionId = "memory-write" });
    created.EnsureSuccessStatusCode();
    var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["sessionId"]!.GetValue<string>();
    var content = "acentuação\r\n" + new string('s', 2 * 1024 * 1024);
    var messages = new[] { new { role = "user", content = "large exact history" } }
      .Concat(Enumerable.Range(0, 8).Select(_ => new { role = "assistant", content })).ToArray();
    using var process = Process.GetProcessById(_environment.ApiProcessId);
    Process? counters = null;
    Task<string>? counterOutput = null;
    var collector = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_E2E_MEMORY_COLLECTOR");
    if (!string.IsNullOrEmpty(collector))
    {
      counters = Process.Start(new ProcessStartInfo(collector, $"{process.Id} 15")
      { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
      counterOutput = counters!.StandardOutput.ReadToEndAsync();
      await Task.Delay(1000);
    }
    process.Refresh(); var baseline = process.PrivateMemorySize64; var peak = baseline;
    using var sampling = new CancellationTokenSource();
    var monitor = Task.Run(async () => { while (!sampling.IsCancellationRequested) { process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64); await Task.Delay(20); } });
    try
    {
      using var saved = await _environment.HttpClient.PutAsJsonAsync("api/sessions/current", new { sessionId = id, messages, interactionMode = "chat", selectedModel = "alpha:latest", state = "completed" });
      saved.EnsureSuccessStatusCode();
    }
    finally { sampling.Cancel(); await monitor; }
    Directory.CreateDirectory(TestContext.TestResultsDirectory!);
    await File.WriteAllTextAsync(Path.Combine(TestContext.TestResultsDirectory!, "serialization-memory.json"), new JsonObject
    { ["pid"] = process.Id, ["baselinePrivateBytes"] = baseline, ["peakPrivateBytes"] = peak, ["deltaPrivateBytes"] = peak - baseline }.ToJsonString());
    TestContext.AddResultFile(Path.Combine(TestContext.TestResultsDirectory!, "serialization-memory.json"));
    TestContext.WriteLine($"write: Host PID={process.Id}; payload=8x2MiB; baseline_private={baseline}; peak_private={peak}; delta_private={peak - baseline}");
    if (counters is not null)
    {
      await counters.WaitForExitAsync(); Assert.AreEqual(0, counters.ExitCode, await counters.StandardError.ReadToEndAsync());
      var output = await counterOutput!;
      LogCounters(output);
      await File.WriteAllTextAsync(Path.Combine(TestContext.TestResultsDirectory!, "serialization-runtime-counters.jsonl"), output);
      TestContext.AddResultFile(Path.Combine(TestContext.TestResultsDirectory!, "serialization-runtime-counters.jsonl")); counters.Dispose();
    }
    Assert.IsLessThan(256L * 1024 * 1024, peak - baseline, "History persistence must avoid whole-document UTF-16 copies and projection buffers.");
    using var opened = await _environment.HttpClient.PostAsJsonAsync($"api/sessions/{id}/open", new { browserSessionId = "memory-write-open" });
    opened.EnsureSuccessStatusCode();
    var restored = JsonNode.Parse(await opened.Content.ReadAsStringAsync())!["messages"]!.AsArray();
    Assert.HasCount(9, restored); Assert.IsTrue(restored.Skip(1).All(message => message!["content"]!.GetValue<string>() == content));
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task PersistingNewConversationKeepsRetainedHistoryBodiesOffTheHeap()
  {
    await Page.GotoAsync("/");
    var workspace = await ActiveWorkspaceIdAsync();
    using (var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspace}/history", new { enabled = true })) enabled.EnsureSuccessStatusCode();
    using var created = await _environment.HttpClient.PostAsJsonAsync("api/sessions/new", new { browserSessionId = "memory-seed" });
    created.EnsureSuccessStatusCode();
    var seed = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["sessionId"]!.GetValue<string>();
    using (var saved = await _environment.HttpClient.PutAsJsonAsync("api/sessions/current", new { sessionId = seed, messages = new[] { new { role = "user", content = "SEED" } }, interactionMode = "chat", selectedModel = "alpha:latest", state = "completed" })) saved.EnsureSuccessStatusCode();
    var folder = Path.Combine(_environment.DataDirectory, "workspaces", workspace, "sessions");
    var template = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, seed + ".json")))!;
    var header = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, seed + ".json.index")))!;
    var content = new string('m', 8 * 1024 * 1024);
    var retained = new List<string>();
    for (var i = 0; i < 14; i++)
    {
      var id = Guid.NewGuid().ToString("N"); retained.Add(id);
      var record = template.DeepClone(); record["id"] = id;
      record["messages"] = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = content });
      var path = Path.Combine(folder, id + ".json");
      await File.WriteAllTextAsync(path, record.ToJsonString());
      var index = header.DeepClone(); index["id"] = id;
      var info = new FileInfo(path); index["recordLength"] = info.Length; index["recordWriteTicks"] = info.LastWriteTimeUtc.Ticks;
      await File.WriteAllTextAsync(path + ".index", index.ToJsonString());
    }
    using var process = Process.GetProcessById(_environment.ApiProcessId);
    Process? counters = null;
    Task<string>? counterOutput = null;
    var collector = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_E2E_MEMORY_COLLECTOR");
    if (!string.IsNullOrEmpty(collector))
    {
      counters = Process.Start(new ProcessStartInfo(collector, $"{process.Id} 15")
      { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
      counterOutput = counters!.StandardOutput.ReadToEndAsync();
      await Task.Delay(1000);
    }
    process.Refresh(); var baseline = process.PrivateMemorySize64; var peak = baseline;
    var samples = new List<long>();
    using var sampling = new CancellationTokenSource();
    var monitor = Task.Run(async () =>
    {
      while (!sampling.IsCancellationRequested)
      {
        process.Refresh(); var bytes = process.PrivateMemorySize64;
        peak = Math.Max(peak, bytes); samples.Add(bytes);
        await Task.Delay(20);
      }
    });
    try
    {
      for (var iteration = 0; iteration < 12; iteration++)
      {
        using var admission = await _environment.HttpClient.PostAsJsonAsync("api/sessions/new", new { browserSessionId = "memory-admission" });
        admission.EnsureSuccessStatusCode();
        var admitted = JsonNode.Parse(await admission.Content.ReadAsStringAsync())!["sessionId"]!.GetValue<string>();
        using var saved = await _environment.HttpClient.PutAsJsonAsync("api/sessions/current", new { sessionId = admitted, messages = new[] { new { role = "user", content = "NEW TURN" } }, interactionMode = "chat", selectedModel = "alpha:latest", state = "completed" });
        saved.EnsureSuccessStatusCode();
      }
    }
    finally { sampling.Cancel(); await monitor; }
    var delta = peak - baseline;
    TestContext.WriteLine($"Host PID={process.Id}; retained=14x8MiB; baseline_private={baseline}; peak_private={peak}; delta_private={delta}; samples={samples.Count}");
    Directory.CreateDirectory(TestContext.TestResultsDirectory!);
    await File.WriteAllTextAsync(Path.Combine(TestContext.TestResultsDirectory!, "history-admission-memory.json"), new JsonObject
    { ["pid"] = process.Id, ["baselinePrivateBytes"] = baseline, ["peakPrivateBytes"] = peak, ["deltaPrivateBytes"] = delta }.ToJsonString());
    TestContext.AddResultFile(Path.Combine(TestContext.TestResultsDirectory!, "history-admission-memory.json"));
    if (counters is not null)
    {
      await counters.WaitForExitAsync();
      TestContext.WriteLine(await counters.StandardError.ReadToEndAsync());
      Assert.AreEqual(0, counters.ExitCode);
      var output = await counterOutput!;
      LogCounters(output);
      await File.WriteAllTextAsync(Path.Combine(TestContext.TestResultsDirectory!, "runtime-counters.jsonl"), output);
      TestContext.AddResultFile(Path.Combine(TestContext.TestResultsDirectory!, "runtime-counters.jsonl"));
      counters.Dispose();
    }
    Assert.IsLessThan(128L * 1024 * 1024, delta, "Admission must inspect metadata instead of loading 112MiB of historical message bodies.");
    using var opened = await _environment.HttpClient.PostAsJsonAsync($"api/sessions/{retained[0]}/open", new { browserSessionId = "memory-reopen" });
    opened.EnsureSuccessStatusCode();
    var exact = JsonNode.Parse(await opened.Content.ReadAsStringAsync())!["messages"]![0]!["content"]!.GetValue<string>();
    Assert.AreEqual(content, exact);
    await _environment.RestartApplicationAsync();
    using var restored = await _environment.HttpClient.PostAsJsonAsync($"api/sessions/{retained[^1]}/open", new { browserSessionId = "memory-restart" });
    restored.EnsureSuccessStatusCode();
    Assert.AreEqual(content, JsonNode.Parse(await restored.Content.ReadAsStringAsync())!["messages"]![0]!["content"]!.GetValue<string>());
  }

  private void LogCounters(string output)
  {
    // Keep the measurements in TRX stdout as well as attachments: some MSTest
    // deployment cleanup paths remove attachments of successful runs.
    var counters = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Select(line => JsonNode.Parse(line))
      .Where(item => item?["kind"]?.GetValue<string>() == "runtime")
      .Select(item => item!["payload"]!["Payload"]!)
      .GroupBy(item => item["Name"]!.GetValue<string>())
      .Select(group => new
      {
        name = group.Key,
        maxMean = group.Max(item => item["Mean"]?.GetValue<double>() ?? 0),
        totalIncrement = group.Sum(item => item["Increment"]?.GetValue<double>() ?? 0),
        firstInterval = group.First()["IntervalSec"]?.GetValue<double>() ?? 0,
        firstIncrement = group.First()["Increment"]?.GetValue<double>() ?? 0
      });
    TestContext.WriteLine("Runtime counter summary: " + System.Text.Json.JsonSerializer.Serialize(counters));
  }
}
