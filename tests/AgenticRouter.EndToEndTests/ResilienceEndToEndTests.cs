using System.Net.Http.Json;
using System.Text.Json;
using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Supervision;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class ResilienceEndToEndTests : ChatEndToEndTestBase<ResilienceEndToEndTests>
{
  [TestMethod]
  [DataRow(true)]
  [DataRow(false)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task CheckpointSharingFailureRecoversWithoutCancellingTheObjective(bool transient)
  {
    _environment.FakeOllama.Reset();
    var workspace = await ActiveWorkspaceIdAsync();
    using var history = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspace}/history", new { enabled = true });
    history.EnsureSuccessStatusCode();
    using var preparation = await _environment.HttpClient.PostAsJsonAsync("api/supervision/runs/prepare",
      new PrepareSupervisionRunRequest("supervision first pass success", "qwen3-coder:30b", "native", Guid.NewGuid().ToString("N")));
    preparation.EnsureSuccessStatusCode();
    var id = (await preparation.Content.ReadFromJsonAsync<SupervisionRunStartView>())!.RunId;
    var before = await _environment.HttpClient.GetFromJsonAsync<DurableSupervisionRunView>($"api/supervision/runs/{id}");
    var path = Path.Combine(_environment.DataDirectory, "workspaces", workspace, "supervision", before!.ConversationSessionId, id + ".json");
    var saved = await File.ReadAllTextAsync(path);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
    {
      var starting = _environment.HttpClient.PostAsync($"api/supervision/runs/{id}/start", null);
      if (transient)
      {
        await Task.Delay(500);
        locked.Dispose();
        using var response = await starting;
        response.EnsureSuccessStatusCode();
      }
      else
      {
        using var response = await starting;
        Assert.IsFalse(response.IsSuccessStatusCode);
        var paused = await _environment.HttpClient.GetFromJsonAsync<DurableSupervisionRunView>($"api/supervision/runs/{id}");
        Assert.AreEqual("awaiting-user", paused!.State);
        Assert.IsFalse(paused.Terminal);
        Assert.IsFalse(paused.ExecutionActive);
        Assert.IsNotNull(paused.DurabilityError);
      }
    }
    if (!transient)
    {
      Assert.AreEqual(saved, await File.ReadAllTextAsync(path));
      await Page.GotoAsync("/");
      var status = await Page.EvaluateAsync<int>("""
        async id => (await fetch(`/api/supervision/runs/${id}/resume`, {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ browserSessionId: 'resilience-resume' }) })).status
        """, id);
      Assert.AreEqual(200, status);
    }
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    DurableSupervisionRunView? completed;
    do
    {
      completed = await _environment.HttpClient.GetFromJsonAsync<DurableSupervisionRunView>($"api/supervision/runs/{id}");
      if (completed!.Terminal && !completed.ExecutionActive) break;
      await Task.Delay(100);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.AreEqual("completed", completed!.State);
    Assert.IsFalse(completed.ExecutionActive);
    Assert.IsNull(completed.DurabilityError);
    Assert.AreEqual(1, completed.Runtime!.WorkItems.Single().AttemptCount);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BenchmarkTimeoutStopsItsPromotedSupervisorBeforePublishingResults()
  {
    _environment.FakeOllama.Reset();
    var id = Guid.NewGuid().ToString("N");
    var objective = "supervision restart boundary\n- inspect\n- create\n- update\n- check\n- validate\n- review";
    using var start = await _environment.HttpClient.PostAsJsonAsync("api/benchmarks/suite-runs/live",
      new BenchmarkSuiteRunRequest("qwen3-coder:30b", [HarnessIds.Native],
        SuiteId: BenchmarkSuiteIds.Manual, SuiteVersion: BenchmarkSuiteIds.ManualVersion,
        TimeoutSeconds: 5, ModelExecutionPermissionGranted: true, ClientRunId: id,
        BenchmarkMode: BenchmarkModeIds.Manual, CustomPrompt: objective));
    start.EnsureSuccessStatusCode();
    LiveChatRunView? active = null;
    var discoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < discoveryDeadline)
    {
      var activeRuns = await _environment.HttpClient.GetFromJsonAsync<LiveChatRunView[]>("api/chat/runs");
      active = activeRuns!.FirstOrDefault(item => item.Message == objective && item.SupervisionRunId is not null);
      if (active is not null) break;
      await Task.Delay(50);
    }
    Assert.IsNotNull(active, "The benchmark must actually promote this scenario to Supervisor.");
    var view = await WaitForBenchmarkAsync(id);
    var result = view.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunCompleted).FinalResult!;
    Assert.IsNull(result.InfrastructureError);
    Assert.AreEqual("timed-out", result.HarnessResults.Single().Tests.Single().RawResult.ExecutionStatus);
    var run = await _environment.HttpClient.GetFromJsonAsync<DurableSupervisionRunView>(
      $"api/supervision/runs/{active.SupervisionRunId}");
    Assert.AreEqual("cancelled", run!.State);
    Assert.IsFalse(run.ExecutionActive);
    await Page.GotoAsync("/");
    await Page.EvaluateAsync("id => sessionStorage.setItem('agentic-router-benchmark-live-run', id)", id);
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Expect(Page.Locator("#benchmark-status")).ToContainTextAsync("completed");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BenchmarkStorageFailureRetainsExportableEvidenceWithoutRepeatingInference()
  {
    var id = Guid.NewGuid().ToString("N");
    // A directory at the destination emulates a real filesystem write failure.
    var obstacle = Path.Combine(_environment.DataDirectory, "benchmark-results", id + ".json");
    Directory.CreateDirectory(obstacle);
    try
    {
      using var start = await _environment.HttpClient.PostAsJsonAsync("api/benchmarks/suite-runs/live",
        new BenchmarkSuiteRunRequest("alpha:latest", [HarnessIds.Codex],
          TimeoutSeconds: 20, ModelExecutionPermissionGranted: true, ClientRunId: id));
      start.EnsureSuccessStatusCode();
      var view = await WaitForBenchmarkAsync(id);
      var result = view.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunCompleted).FinalResult!;
      Assert.AreEqual("benchmark-result-not-saved", result.PersistenceError?.Code);
      Assert.IsNull(result.InfrastructureError);
      Assert.HasCount(4, result.HarnessResults.Single().Tests);
      var raw = await _environment.HttpClient.GetFromJsonAsync<BenchmarkSuiteRunResult>(
        $"api/benchmarks/suite-runs/{id}/raw");
      Assert.AreEqual(id, raw!.RunId);
      Assert.IsNotNull(raw.PersistenceError);
      var requests = _environment.FakeOllama.Requests.Count;
      await Page.GotoAsync("/");
      await Page.EvaluateAsync("id => sessionStorage.setItem('agentic-router-benchmark-live-run', id)", id);
      await Page.Locator("#open-benchmarks").ClickAsync();
      await Expect(Page.Locator("#benchmark-status")).ToContainTextAsync("could not be saved");
      await Expect(Page.Locator("#benchmark-raw-evidence-content")).ToContainTextAsync(id);
      Assert.HasCount(requests, _environment.FakeOllama.Requests);
      Assert.IsFalse(File.Exists(obstacle));
    }
    finally { Directory.Delete(obstacle); }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BenchmarkInfrastructureFailurePreservesEarlierCellsAndDoesNotStartRemainingCells()
  {
    var configured = _environment.BaselineSettings with
    {
      ModelGpuAffinities = new Dictionary<string, string>
      { ["structured:latest"] = "device:previously-installed-now-missing" }
    };
    // Persisted configuration can outlive a physically removed GPU.
    await File.WriteAllTextAsync(_environment.SettingsPath, configured.ToJson());
    await _environment.RestartApplicationAsync();
    var id = Guid.NewGuid().ToString("N");
    using var start = await _environment.HttpClient.PostAsJsonAsync("api/benchmarks/suite-runs/live",
      new BenchmarkSuiteRunRequest("alpha:latest", [HarnessIds.Codex],
        Models: ["alpha:latest", "structured:latest", "qwen3-coder:30b"],
        TimeoutSeconds: 20, ModelExecutionPermissionGranted: true, ClientRunId: id));
    start.EnsureSuccessStatusCode();
    var view = await WaitForBenchmarkAsync(id);
    var result = view.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunCompleted).FinalResult!;
    Assert.AreEqual("failed", result.TerminalState);
    Assert.IsNotNull(result.InfrastructureError);
    Assert.HasCount(4, result.Cells![0].Result!.Tests);
    Assert.AreEqual("failed", result.Cells[1].Status);
    Assert.AreEqual("not-run", result.Cells[2].Status);
    Assert.IsNull(result.Cells[2].Result);
    Assert.IsFalse(view.Events.Any(item => item.Type == BenchmarkProgressTypeIds.HarnessStarted
      && item.Model == "qwen3-coder:30b"));
    var saved = await _environment.HttpClient.GetFromJsonAsync<BenchmarkSuiteRunResult>(
      $"api/benchmarks/suite-runs/{id}/raw");
    Assert.AreEqual(result.InfrastructureError.Code, saved!.InfrastructureError!.Code);
    await Page.GotoAsync("/");
    await Page.EvaluateAsync("id => sessionStorage.setItem('agentic-router-benchmark-live-run', id)", id);
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Expect(Page.Locator("#benchmark-status")).ToContainTextAsync("infrastructure failed");
  }

  private static async Task<BenchmarkLiveRunView> WaitForBenchmarkAsync(string id)
  {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
    while (DateTimeOffset.UtcNow < deadline)
    {
      var view = await _environment.HttpClient.GetFromJsonAsync<BenchmarkLiveRunView>(
        $"api/benchmarks/suite-runs/{id}/live");
      if (view?.Terminal == true) return view;
      await Task.Delay(100);
    }
    throw new AssertFailedException("The benchmark did not settle within the test deadline.");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SimultaneousSupervisionStartsHaveOneWorkspaceOwner()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using var history = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{workspaceId}/history", new { enabled = true });
    history.EnsureSuccessStatusCode();
    var ids = new List<string>();
    try
    {
      for (var index = 0; index < 2; index++)
      {
        using var prepared = await _environment.HttpClient.PostAsJsonAsync("api/supervision/runs/prepare",
          new PrepareSupervisionRunRequest("supervision restart boundary", "qwen3-coder:30b",
            "native", Guid.NewGuid().ToString("N")));
        prepared.EnsureSuccessStatusCode();
        ids.Add((await prepared.Content.ReadFromJsonAsync<SupervisionRunStartView>())!.RunId);
      }
      await Page.GotoAsync("/");
      var statuses = await Page.EvaluateAsync<int[]>("""
        async ids => await Promise.all(ids.map(async id =>
          (await fetch(`/api/supervision/runs/${id}/start`, { method: 'POST' })).status))
        """, ids);
      CollectionAssert.AreEquivalent(new[] { 202, 409 }, statuses);
      var views = await Task.WhenAll(ids.Select(id => _environment.HttpClient
        .GetFromJsonAsync<DurableSupervisionRunView>($"api/supervision/runs/{id}")));
      Assert.HasCount(1, views.Where(view => view?.State == "awaiting-user"
        && view.WaitCode == "supervision-recovery-workspace-busy"));
    }
    finally
    {
      foreach (var id in ids)
      {
        using var cancelled = await _environment.HttpClient.PostAsync($"api/supervision/runs/{id}/cancel", null);
      }
    }
  }

  [TestMethod]
  [Timeout(180_000, CooperativeCancellation = true)]
  public async Task BenchmarkReplayGapRehydratesEveryCellFromHostSnapshot()
  {
    var id = Guid.NewGuid().ToString("N");
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs/live",
      new BenchmarkSuiteRunRequest("alpha:latest",
        [HarnessIds.Native, HarnessIds.Codex, HarnessIds.OpenCode, HarnessIds.QwenCode, HarnessIds.ClaudeCode],
        TimeoutSeconds: 20, ModelExecutionPermissionGranted: true, ClientRunId: id,
        Models: ["alpha:latest", "structured:latest"]));
    response.EnsureSuccessStatusCode();
    BenchmarkLiveRunView? view;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(140);
    do
    {
      view = await _environment.HttpClient.GetFromJsonAsync<BenchmarkLiveRunView>(
        $"api/benchmarks/suite-runs/{id}/live");
      if (view?.Terminal == true) break;
      await Task.Delay(100);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.IsNotNull(view);
    Assert.IsTrue(view.Terminal);
    Assert.IsGreaterThan(512, view.LastSequence, "The scenario must exceed the replay journal capacity.");
    Assert.HasCount(1, view.Events.Where(item => item.Type == BenchmarkProgressTypeIds.RunStarted));
    var final = view.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunCompleted).FinalResult!;
    Assert.HasCount(10, final.Cells!);
    foreach (var cell in final.Cells!)
    {
      foreach (var test in cell.Result!.Tests)
      {
        Assert.IsTrue(view.Events.Any(item => item.Model == cell.Model && item.Harness == cell.Harness
          && item.TestId == test.Run.TestId && item.TestResult is not null));
      }
    }
    var replay = await _environment.HttpClient.GetStringAsync($"api/benchmarks/suite-runs/{id}/events?after=1");
    var frames = replay.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
      .Select(line => JsonSerializer.Deserialize<BenchmarkProgressEvent>(line[6..],
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
    Assert.HasCount(1, frames);
    Assert.AreEqual(BenchmarkProgressTypeIds.Snapshot, frames[0].Type);
    Assert.AreEqual(view.LastSequence, frames[0].Sequence);
    Assert.HasCount(1, frames[0].SnapshotEvents!.Where(item => item.Type == BenchmarkProgressTypeIds.RunCompleted));

    await Page.GotoAsync("/");
    await Page.EvaluateAsync("id => sessionStorage.setItem('agentic-router-benchmark-live-run', id)", id);
    await Page.ReloadAsync();
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Expect(Page.Locator("#benchmark-status")).Not.ToContainTextAsync("reconnecting");
    await Expect(Page.Locator("#benchmark-results-body")).ToContainTextAsync("alpha:latest");
    await Expect(Page.Locator("#benchmark-results-body")).ToContainTextAsync("structured:latest");
  }
}
