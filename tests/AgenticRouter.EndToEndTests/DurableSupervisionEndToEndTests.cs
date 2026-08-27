using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class DurableSupervisionEndToEndTests
  : ChatEndToEndTestBase<DurableSupervisionEndToEndTests>
{
  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task SupervisionDirectiveInChatAndCloudRouteFailBeforeInference()
  {
    _environment.FakeOllama.Reset();
    _environment.FakeCloud.Reset();

    using var chatResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "/supervisor build the complete local application",
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "chat",
        harness = "native",
        approvalPolicy = "auto",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    chatResponse.EnsureSuccessStatusCode();
    var chatEvents = ParseSseEvents(
      await chatResponse.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "supervision-execute-required",
      chatEvents.Single(
        item => item["type"]!.GetValue<string>() == "error"
      )["error"]!["code"]!.GetValue<string>()
    );

    using var cloudResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "Build the complete application",
        model = "groq::openai/gpt-oss-120b",
        harness = "native",
        browserSessionId = Guid.NewGuid().ToString("N"),
        approvalPolicy = "auto",
        resumePolicy = "manual"
      }
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      cloudResponse.StatusCode
    );
    var cloudError = JsonNode.Parse(
      await cloudResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    Assert.AreEqual(
      "supervision-local-model-required",
      cloudError["code"]!.GetValue<string>()
    );
    using var contradictoryAutoResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "Build the complete application",
        model = "groq::openai/gpt-oss-120b",
        harness = "native",
        autoModelHarness = true,
        browserSessionId = Guid.NewGuid().ToString("N"),
        approvalPolicy = "auto",
        resumePolicy = "manual"
      }
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      contradictoryAutoResponse.StatusCode
    );
    var contradictoryAutoError = JsonNode.Parse(
      await contradictoryAutoResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    Assert.AreEqual(
      "supervision-local-model-required",
      contradictoryAutoError["code"]!.GetValue<string>()
    );
    Assert.HasCount(
      0,
      _environment.FakeOllama.Requests
    );
    Assert.HasCount(
      0,
      _environment.FakeCloud.Requests
    );
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task SupervisedExecuteRejectsCorrectsVerifiesAndCompletesOnce()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var client = new HttpClient
    {
      BaseAddress = _environment.BaseUri,
      Timeout = TimeSpan.FromSeconds(45)
    };
    using var prepareResponse = await client.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "create file hello.txt with exact text hello world today",
        model = "qwen3-coder:30b",
        harness = "native",
        approvalPolicy = "auto",
        resumePolicy = "manual",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await client.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    JsonObject run;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
    do
    {
      await Task.Delay(100);
      run = await GetRunAsync(runId);
      if (run["terminal"]!.GetValue<bool>())
      {
        break;
      }
    } while (DateTimeOffset.UtcNow < deadline);

    Assert.IsTrue(
      run["terminal"]!.GetValue<bool>(),
      $"Last run view: {run}{Environment.NewLine}API output: {_environment.ApiOutput}"
    );
    using var eventResponse = await client.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(
      await eventResponse.Content.ReadAsStringAsync()
    );

    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.work-rejected"),
      string.Join(
        Environment.NewLine,
        events.Select(item => $"{item["type"]}: {item["message"]}")
      )
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.work-accepted")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.completed")
    );
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "hello.txt")
      )
    );

    var supervisorRequests = _environment.FakeOllama.Requests.Where(
      request => request.Messages.Any(message =>
        message.Content.Contains("SUPERVISION_", StringComparison.Ordinal)
        && !message.Content.Contains("SUPERVISION_WORKER_V1", StringComparison.Ordinal)
        && !message.Content.Contains("SUPERVISION_CORRECTION_V1", StringComparison.Ordinal)
      )
    ).ToArray();
    Assert.IsGreaterThanOrEqualTo(4, supervisorRequests.Length);
    foreach (var supervisorRequest in supervisorRequests)
    {
      Assert.DoesNotContain("create_file", supervisorRequest.AvailableTools);
      Assert.DoesNotContain("write_file", supervisorRequest.AvailableTools);
      Assert.DoesNotContain("apply_patch", supervisorRequest.AvailableTools);
      Assert.DoesNotContain("run_process", supervisorRequest.AvailableTools);
    }

    Assert.AreEqual("completed", run["state"]!.GetValue<string>());
    Assert.AreEqual(1, run["runtime"]!["completedItems"]!.GetValue<int>());
    Assert.AreEqual(1, run["runtime"]!["totalItems"]!.GetValue<int>());
    Assert.AreEqual(2, run["runtime"]!["contexts"]!.AsArray().Count);
    Assert.AreEqual(
      2,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SupervisionDirectiveExecuteStreamsOnlyAcceptedTerminalAnswer()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var client = new HttpClient
    {
      BaseAddress = _environment.BaseUri,
      Timeout = TimeSpan.FromSeconds(45)
    };
    using var response = await client.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "/supervisor create file hello.txt with exact text hello world today",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "native",
        approvalPolicy = "auto",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(
      await response.Content.ReadAsStringAsync()
    );

    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "error")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.work-rejected")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.work-accepted")
    );
    var deltas = events.Where(
      item => item["type"]!.GetValue<string>() == "response.delta"
    ).ToArray();
    Assert.HasCount(1, deltas);
    Assert.AreEqual(
      "Created hello.txt with the exact text hello world today and verified the current file contents.",
      deltas[0]["delta"]!.GetValue<string>()
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "hello.txt")
      )
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BrowserReloadReattachesDurableSupervisionAndPersistsCompletion()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    _ = await EnableHistoryAsync();
    await Page.GotoAsync("/");
    await Page.GetByRole(
      AriaRole.Button,
      new() { Name = "Execute", Exact = true }
    ).ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await Page.Locator("#message-input").FillAsync(
      "/supervisor supervision restart boundary"
    );
    await Page.Locator("#send-button").ClickAsync();

    JsonObject? activeRun = null;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    do
    {
      using var response = await _environment.HttpClient.GetAsync(
        "api/supervision/runs"
      );
      response.EnsureSuccessStatusCode();
      var runs = JsonNode.Parse(
        await response.Content.ReadAsStringAsync()
      )!["runs"]!.AsArray();
      activeRun = runs.Select(item => item!.AsObject()).FirstOrDefault(run =>
        run["objective"]!.GetValue<string>() == "supervision restart boundary"
        && run["phase"]!.GetValue<string>() == "verifying"
      );
      if (activeRun is not null)
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.IsNotNull(activeRun);
    var runId = activeRun["runId"]!.GetValue<string>();
    var conversationSessionId = activeRun["conversationSessionId"]!.GetValue<string>();
    var revisionBeforeReload = activeRun["revision"]!.GetValue<long>();

    await Page.ReloadAsync();
    var afterReload = await GetRunAsync(runId);
    Assert.AreNotEqual("cancelled", afterReload["state"]!.GetValue<string>());
    Assert.IsGreaterThanOrEqualTo(
      revisionBeforeReload,
      afterReload["revision"]!.GetValue<long>()
    );

    var sessionButton = Page.Locator(
      $".session-entry[data-session-id=\"{conversationSessionId}\"] .session-entry-content"
    );
    await Expect(sessionButton).ToBeVisibleAsync(new() { Timeout = 10_000 });
    await sessionButton.ClickAsync();
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(Page.Locator(".assistant-answer").Last).ToContainTextAsync(
      "Created hello.txt with the exact text hello world today",
      new() { Timeout = 40_000 }
    );

    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>());
    using var sessionsResponse = await _environment.HttpClient.GetAsync("api/sessions");
    sessionsResponse.EnsureSuccessStatusCode();
    var sessions = JsonNode.Parse(
      await sessionsResponse.Content.ReadAsStringAsync()
    )!["recent"]!.AsArray();
    var persisted = sessions.Select(item => item!.AsObject()).Single(session =>
      session["id"]!.GetValue<string>() == conversationSessionId
    );
    Assert.IsFalse(persisted["interrupted"]!.GetValue<bool>());
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SupervisorRejectsAcceptanceWhenEvidenceChangesDuringVerification()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "supervision stale boundary",
        model = "qwen3-coder:30b",
        harness = "native",
        browserSessionId = Guid.NewGuid().ToString("N"),
        approvalPolicy = "auto",
        resumePolicy = "manual"
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    JsonObject verifying;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    do
    {
      verifying = await GetRunAsync(runId);
      if (
        verifying["phase"]!.GetValue<string>() == "verifying"
        && verifying["runtime"]!["activeRole"]!.GetValue<string>() == "supervisor"
        && verifying["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>() == 2
      )
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.AreEqual(
      2,
      verifying["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>(),
      verifying.ToJsonString()
    );
    await File.WriteAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "hello.txt"),
      "hello world"
    );

    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(40));
    Assert.AreEqual(
      "completed",
      completed["state"]!.GetValue<string>(),
      completed.ToJsonString()
    );
    Assert.AreEqual(
      3,
      completed["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );
    using var eventsResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventsResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await eventsResponse.Content.ReadAsStringAsync());
    Assert.HasCount(
      2,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.work-rejected")
    );
    Assert.HasCount(
      1,
      events.Where(item =>
        item["message"]?.GetValue<string>().Contains(
          "Host rejected stale evidence",
          StringComparison.Ordinal
        ) == true
      )
    );
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task MalformedSupervisorDecisionBlocksOnceWithoutWorkerOrIdenticalRetry()
  {
    _environment.FakeOllama.Reset();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "malformed supervision decision",
        model = "qwen3-coder:30b",
        harness = "native",
        approvalPolicy = "auto",
        resumePolicy = "manual",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    JsonObject run;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    do
    {
      await Task.Delay(100);
      run = await GetRunAsync(runId);
      if (run["terminal"]!.GetValue<bool>())
      {
        break;
      }
    } while (DateTimeOffset.UtcNow < deadline);

    Assert.IsTrue(run["terminal"]!.GetValue<bool>(), run.ToJsonString());
    Assert.AreEqual("blocked", run["state"]!.GetValue<string>());
    StringAssert.Contains(
      run["runtime"]!["lastFailure"]!.GetValue<string>(),
      "malformed canonical JSON"
    );
    var decompositionRequests = _environment.FakeOllama.Requests.Where(
      request => request.Messages.Any(message => message.Content.Contains(
        "SUPERVISION_DECOMPOSE_V1",
        StringComparison.Ordinal
      ))
    ).ToArray();
    Assert.HasCount(1, decompositionRequests);
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(message => message.Content.Contains(
          "SUPERVISION_WORKER_V1",
          StringComparison.Ordinal
        ))
      )
    );
    using var eventResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await eventResponse.Content.ReadAsStringAsync());
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.blocked")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["terminal"]!.GetValue<bool>())
    );
  }

  [TestMethod]
  [Timeout(45_000, CooperativeCancellation = true)]
  public async Task RepeatedIdenticalEvidenceEmitsNoProgressAndStopsAtBudget()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "no progress supervision",
        model = "qwen3-coder:30b",
        harness = "native",
        approvalPolicy = "auto",
        resumePolicy = "manual",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    JsonObject run;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
    do
    {
      await Task.Delay(100);
      run = await GetRunAsync(runId);
      if (run["terminal"]!.GetValue<bool>())
      {
        break;
      }
    } while (DateTimeOffset.UtcNow < deadline);

    Assert.IsTrue(run["terminal"]!.GetValue<bool>(), run.ToJsonString());
    Assert.AreEqual("blocked", run["state"]!.GetValue<string>());
    Assert.IsGreaterThan(
      0,
      run["runtime"]!["noProgressCount"]!.GetValue<int>()
    );
    Assert.AreEqual(
      "hello world",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "hello.txt")
      )
    );
    using var eventResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await eventResponse.Content.ReadAsStringAsync());
    Assert.IsGreaterThanOrEqualTo(
      1,
      events.Count(item => item["type"]!.GetValue<string>() == "supervision.no-progress")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.blocked")
    );
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task VolatileRunSupportsBrowserObservationCursorReplayAndTerminalOnce()
  {
    _environment.FakeOllama.Reset();
    var prepared = await PrepareAsync(
      "manual"
    );
    var runId = prepared["runId"]!.GetValue<string>();
    Assert.IsFalse(
      prepared["durable"]!.GetValue<bool>()
    );

    await Page.GotoAsync(
      "/"
    );
    var browserViewJson = await Page.EvaluateAsync<string>(
      """
      async runId => {
        const response = await fetch(`/api/supervision/runs/${runId}`);
        return JSON.stringify(await response.json());
      }
      """,
      runId
    );
    var browserView = JsonNode.Parse(
      browserViewJson
    )!.AsObject();
    Assert.AreEqual(
      "prepared",
      browserView["state"]!.GetValue<string>()
    );

    using var initialEvents = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    initialEvents.EnsureSuccessStatusCode();
    var initial = ParseSseEvents(
      await initialEvents.Content.ReadAsStringAsync()
    );
    Assert.HasCount(
      1,
      initial
    );
    Assert.AreEqual(
      1L,
      initial[0]["sequence"]!.GetValue<long>()
    );

    using var cancelled = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/cancel",
      null
    );
    Assert.AreEqual(
      HttpStatusCode.Accepted,
      cancelled.StatusCode
    );
    var cancelledView = JsonNode.Parse(
      await cancelled.Content.ReadAsStringAsync()
    )!.AsObject();
    Assert.AreEqual(
      "cancelled",
      cancelledView["state"]!.GetValue<string>()
    );

    using var replayRequest = new HttpRequestMessage(
      HttpMethod.Get,
      $"api/supervision/runs/{runId}/events?after=0"
    );
    replayRequest.Headers.Add(
      "Last-Event-ID",
      "1"
    );
    using var replayResponse = await _environment.HttpClient.SendAsync(
      replayRequest
    );
    replayResponse.EnsureSuccessStatusCode();
    var replay = ParseSseEvents(
      await replayResponse.Content.ReadAsStringAsync()
    );
    CollectionAssert.AreEqual(
      new long[]
      {
        2,
        3
      },
      replay.Select(
        item => item["sequence"]!.GetValue<long>()
      ).ToArray()
    );
    Assert.AreEqual(
      1,
      replay.Count(
        item => item["terminal"]!.GetValue<bool>()
      )
    );

    using var repeatedCancel = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/cancel",
      null
    );
    Assert.AreEqual(
      HttpStatusCode.NotFound,
      repeatedCancel.StatusCode
    );
    await DiscardAsync(
      runId
    );
    Assert.HasCount(
      0,
      _environment.FakeOllama.Requests
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DurableManualRunRestoresInterruptedAndResumesToCompletion()
  {
    _environment.FakeOllama.Reset();
    var workspaceId = await EnableHistoryAsync();
    var conversationId = Guid.NewGuid().ToString(
      "N"
    );
    var prepared = await PrepareAsync(
      "manual",
      conversationId
    );
    var runId = prepared["runId"]!.GetValue<string>();
    Assert.IsTrue(
      prepared["durable"]!.GetValue<bool>()
    );
    var checkpointPath = Path.Combine(
      _environment.DataDirectory,
      "workspaces",
      workspaceId,
      "supervision",
      conversationId,
      $"{runId}.json"
    );
    Assert.IsTrue(
      File.Exists(
        checkpointPath
      )
    );
    var checkpointText = await File.ReadAllTextAsync(
      checkpointPath
    );
    using var checkpointDocument = JsonDocument.Parse(
      checkpointText
    );
    Assert.AreEqual(
      2,
      checkpointDocument.RootElement.GetProperty("schemaVersion").GetInt32()
    );
    Assert.IsTrue(checkpointDocument.RootElement.TryGetProperty("runtime", out _));
    Assert.IsTrue(checkpointDocument.RootElement.TryGetProperty("recovery", out _));
    Assert.IsFalse(
      string.IsNullOrWhiteSpace(
        checkpointDocument.RootElement.GetProperty(
          "integritySha256"
        ).GetString()
      )
    );
    Assert.IsFalse(
      checkpointText.Contains(
        "systemPrompt",
        StringComparison.OrdinalIgnoreCase
      )
    );
    Assert.IsFalse(
      checkpointText.Contains(
        "providerPayload",
        StringComparison.OrdinalIgnoreCase
      )
    );
    Assert.IsFalse(checkpointText.Contains("finalContent", StringComparison.OrdinalIgnoreCase));
    Assert.IsFalse(checkpointText.Contains("originalContent", StringComparison.OrdinalIgnoreCase));

    await _environment.RestartApplicationAsync();
    var restored = await GetRunAsync(
      runId
    );
    Assert.AreEqual(
      "interrupted-recoverable",
      restored["state"]!.GetValue<string>()
    );
    Assert.IsFalse(
      restored["autoResumeEligible"]!.GetValue<bool>()
    );
    Assert.HasCount(0, _environment.FakeOllama.Requests);

    using var resumedResponse = await _environment.HttpClient.PostAsJsonAsync(
      $"api/supervision/runs/{runId}/resume",
      new
      {
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    resumedResponse.EnsureSuccessStatusCode();
    var resumed = JsonNode.Parse(
      await resumedResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    Assert.IsTrue(
      resumed["state"]!.GetValue<string>() is "running" or "completed"
    );
    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>());
    Assert.IsGreaterThan(0, _environment.FakeOllama.Requests.Count);
    await DiscardAsync(
      runId
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoSafeRestartContinuesFromCommittedBoundary()
  {
    _environment.FakeOllama.Reset();
    _ = await EnableHistoryAsync();
    var prepared = await PrepareAsync(
      "auto-safe"
    );
    var runId = prepared["runId"]!.GetValue<string>();

    await _environment.RestartApplicationAsync();
    var restored = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", restored["state"]!.GetValue<string>());
    Assert.IsGreaterThan(0, _environment.FakeOllama.Requests.Count);
    await DiscardAsync(
      runId
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutoSafeRestartsMidGoalFromCommittedBoundaryAndFinishes()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    _ = await EnableHistoryAsync();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "supervision restart boundary",
        model = "qwen3-coder:30b",
        harness = "native",
        browserSessionId = Guid.NewGuid().ToString("N"),
        approvalPolicy = "auto",
        resumePolicy = "auto-safe"
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    JsonObject boundary;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
    do
    {
      boundary = await GetRunAsync(runId);
      var committed = boundary["recovery"]?["actions"]?.AsArray().Any(action =>
        action?["phase"]?.GetValue<string>() == "committed"
      ) == true;
      if (
        committed
        && boundary["phase"]!.GetValue<string>() == "verifying"
        && boundary["runtime"]?["activeRole"]?.GetValue<string>() == "supervisor"
      )
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.AreEqual("verifying", boundary["phase"]!.GetValue<string>(), boundary.ToJsonString());
    Assert.IsTrue(
      boundary["recovery"]!["actions"]!.AsArray().Any(action =>
        action?["phase"]?.GetValue<string>() == "committed"
      ),
      boundary.ToJsonString()
    );

    await _environment.RestartApplicationAsync();
    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(40));
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>(), completed.ToJsonString());
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );
    Assert.IsTrue(
      completed["recovery"]!["actions"]!.AsArray().Any(action =>
        action?["phase"]?.GetValue<string>() == "committed"
      )
    );
    using var eventsResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventsResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await eventsResponse.Content.ReadAsStringAsync());
    Assert.IsTrue(events.Any(item =>
      item["type"]!.GetValue<string>() == "supervision.recovery-eligible"
    ));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.completed")
    );
    await DiscardAsync(runId);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoSafeRestartWaitsForPendingApprovalWithoutNewInference()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    _ = await EnableHistoryAsync();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "create file hello.txt with exact text hello world today",
        model = "qwen3-coder:30b",
        harness = "native",
        browserSessionId = Guid.NewGuid().ToString("N"),
        approvalPolicy = "ask",
        resumePolicy = "auto-safe"
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    JsonObject pending;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
    do
    {
      pending = await GetRunAsync(runId);
      if (pending["recovery"]?["actions"]?.AsArray().Any(action =>
        action?["phase"]?.GetValue<string>() == "awaiting-approval"
      ) == true)
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.IsTrue(
      pending["recovery"]!["actions"]!.AsArray().Any(action =>
        action?["phase"]?.GetValue<string>() == "awaiting-approval"
      ),
      pending.ToJsonString()
    );
    var inferenceRequestsBeforeRestart = _environment.FakeOllama.Requests.Count(
      request => request.Messages.Count > 0
    );

    await _environment.RestartApplicationAsync();
    var restored = await GetRunAsync(runId);
    Assert.AreEqual("awaiting-user", restored["state"]!.GetValue<string>());
    Assert.AreEqual(
      "supervision-recovery-approval-pending",
      restored["waitCode"]!.GetValue<string>()
    );
    Assert.AreEqual(
      inferenceRequestsBeforeRestart,
      _environment.FakeOllama.Requests.Count(request => request.Messages.Count > 0)
    );
    Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
    await DiscardAsync(runId);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoSafeRestartReportsTrackedWorkspaceDriftAndPreservesIt()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    _ = await EnableHistoryAsync();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "supervision restart boundary",
        model = "qwen3-coder:30b",
        harness = "native",
        browserSessionId = Guid.NewGuid().ToString("N"),
        approvalPolicy = "auto",
        resumePolicy = "auto-safe"
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    JsonObject boundary;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
    do
    {
      boundary = await GetRunAsync(runId);
      if (
        boundary["phase"]!.GetValue<string>() == "verifying"
        && boundary["recovery"]?["trackedFiles"]?.AsArray().Any(file =>
          file?["relativePath"]?.GetValue<string>() == "hello.txt"
        ) == true
      )
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.AreEqual("verifying", boundary["phase"]!.GetValue<string>(), boundary.ToJsonString());
    await File.WriteAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "hello.txt"),
      "external user drift"
    );
    var requestsBeforeRestart = _environment.FakeOllama.Requests.Count;

    await _environment.RestartApplicationAsync();
    var restored = await GetRunAsync(runId);
    Assert.AreEqual("awaiting-user", restored["state"]!.GetValue<string>());
    Assert.AreEqual(
      "supervision-recovery-workspace-drift",
      restored["waitCode"]!.GetValue<string>()
    );
    Assert.HasCount(requestsBeforeRestart, _environment.FakeOllama.Requests);
    Assert.AreEqual(
      "external user drift",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );
    await DiscardAsync(runId);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoSafeRestartReportsRepositoryInstructionDrift()
  {
    _environment.FakeOllama.Reset();
    _ = await EnableHistoryAsync();
    var prepared = await PrepareAsync("auto-safe");
    var runId = prepared["runId"]!.GetValue<string>();
    await File.WriteAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "AGENTS.md"),
      "# Changed after checkpoint\n"
    );

    await _environment.RestartApplicationAsync();
    var restored = await GetRunAsync(runId);
    Assert.AreEqual("awaiting-user", restored["state"]!.GetValue<string>());
    Assert.AreEqual(
      "supervision-recovery-instructions-changed",
      restored["waitCode"]!.GetValue<string>()
    );
    Assert.HasCount(0, _environment.FakeOllama.Requests);
    using var resumeResponse = await _environment.HttpClient.PostAsJsonAsync(
      $"api/supervision/runs/{runId}/resume",
      new
      {
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    resumeResponse.EnsureSuccessStatusCode();
    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>());
    File.Delete(Path.Combine(_environment.WorkspaceDirectory, "AGENTS.md"));
    await DiscardAsync(runId);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BrowserShowsRecoverableRunAndUsesStyledDiscardConfirmation()
  {
    _environment.FakeOllama.Reset();
    _ = await EnableHistoryAsync();
    var prepared = await PrepareAsync("manual");
    var runId = prepared["runId"]!.GetValue<string>();

    await _environment.RestartApplicationAsync();
    var restored = await GetRunAsync(runId);
    Assert.AreEqual("interrupted-recoverable", restored["state"]!.GetValue<string>());
    await Page.GotoAsync("/");

    var browserRuns = await Page.EvaluateAsync<string>(
      "async () => JSON.stringify(await (await fetch('/api/supervision/runs')).json())"
    );
    Assert.IsTrue(browserRuns.Contains(runId, StringComparison.Ordinal), browserRuns);

    var recovery = Page.Locator("#supervision-recovery");
    var card = recovery.Locator($"[data-run-id=\"{runId}\"]");
    await Expect(recovery).ToBeVisibleAsync(new() { Timeout = 10_000 });
    await Expect(card).ToContainTextAsync("Build the complete local application");
    await Expect(card).ToContainTextAsync("qwen3-coder:30b × Native");
    await Expect(card.GetByRole(AriaRole.Button, new() { Name = "Resume" }))
      .ToBeVisibleAsync();

    await card.GetByRole(AriaRole.Button, new() { Name = "Discard" }).ClickAsync();
    await Expect(Page.Locator("#app-modal")).ToBeVisibleAsync();
    await Expect(Page.Locator("#app-modal-title"))
      .ToHaveTextAsync("Discard supervised recovery?");
    await Expect(Page.Locator("#app-modal-message")).ToContainTextAsync(
      "Workspace files will be preserved"
    );
    await Page.Locator("#app-modal-confirm").ClickAsync();

    await Expect(card).ToHaveCountAsync(0);
    using var missing = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}"
    );
    Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    Assert.HasCount(0, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task InvalidCheckpointDoesNotPreventStartup()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    var conversationId = Guid.NewGuid().ToString(
      "N"
    );
    var runId = Guid.NewGuid().ToString(
      "N"
    );
    var directory = Path.Combine(
      _environment.DataDirectory,
      "workspaces",
      workspaceId,
      "supervision",
      conversationId
    );
    Directory.CreateDirectory(
      directory
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        directory,
        $"{runId}.json"
      ),
      "{ invalid-json"
    );

    await _environment.RestartApplicationAsync();
    using var settings = await _environment.HttpClient.GetAsync(
      "api/settings"
    );
    settings.EnsureSuccessStatusCode();
    using var missing = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}"
    );
    Assert.AreEqual(
      HttpStatusCode.NotFound,
      missing.StatusCode
    );
    Assert.IsTrue(
      _environment.ApiOutput.Contains(
        "supervision-checkpoint-invalid",
        StringComparison.OrdinalIgnoreCase
      )
    );

    Directory.Delete(
      directory,
      true
    );
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task ConversationDeletionRemovesDurableRunAndCheckpoint()
  {
    var workspaceId = await EnableHistoryAsync();
    var conversationId = Guid.NewGuid().ToString(
      "N"
    );
    using var savedSession = await _environment.HttpClient.PutAsJsonAsync(
      "api/sessions/current",
      new
      {
        sessionId = conversationId,
        messages = new[]
        {
          new
          {
            role = "user",
            content = "Prepare durable supervised work"
          }
        },
        interactionMode = "execute",
        selectedModel = "alpha:latest",
        state = "completed"
      }
    );
    savedSession.EnsureSuccessStatusCode();
    var prepared = await PrepareAsync(
      "manual",
      conversationId
    );
    var runId = prepared["runId"]!.GetValue<string>();
    var checkpointPath = Path.Combine(
      _environment.DataDirectory,
      "workspaces",
      workspaceId,
      "supervision",
      conversationId,
      $"{runId}.json"
    );
    Assert.IsTrue(
      File.Exists(
        checkpointPath
      )
    );

    using var deleted = await _environment.HttpClient.DeleteAsync(
      $"api/sessions/{conversationId}?confirmed=true"
    );
    Assert.AreEqual(
      HttpStatusCode.NoContent,
      deleted.StatusCode
    );
    Assert.IsFalse(
      File.Exists(
        checkpointPath
      )
    );
    using var missing = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}"
    );
    Assert.AreEqual(
      HttpStatusCode.NotFound,
      missing.StatusCode
    );
  }

  private static async Task<JsonObject> PrepareAsync(
    string resumePolicy,
    string? conversationSessionId = null
  )
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "Build the complete local application in bounded steps",
        model = "qwen3-coder:30b",
        harness = "native",
        browserSessionId = Guid.NewGuid().ToString("N"),
        conversationSessionId,
        approvalPolicy = "auto",
        resumePolicy
      }
    );
    Assert.AreEqual(
      HttpStatusCode.Accepted,
      response.StatusCode,
      await response.Content.ReadAsStringAsync()
    );
    return JsonNode.Parse(
      await response.Content.ReadAsStringAsync()
    )!.AsObject();
  }

  private static async Task<JsonObject> GetRunAsync(string runId)
  {
    using var response = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}"
    );
    response.EnsureSuccessStatusCode();
    return JsonNode.Parse(
      await response.Content.ReadAsStringAsync()
    )!.AsObject();
  }

  private static async Task<JsonObject> WaitForTerminalAsync(
    string runId,
    TimeSpan timeout
  )
  {
    var deadline = DateTimeOffset.UtcNow.Add(timeout);
    JsonObject run;
    do
    {
      run = await GetRunAsync(runId);
      if (run["terminal"]!.GetValue<bool>())
      {
        return run;
      }
      await Task.Delay(100);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.Fail($"Run {runId} did not become terminal. Last view: {run}");
    return run;
  }

  private static async Task<string> EnableHistoryAsync()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using var response = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{workspaceId}/history",
      new
      {
        enabled = true
      }
    );
    response.EnsureSuccessStatusCode();
    return workspaceId;
  }

  private static async Task DiscardAsync(string runId)
  {
    using var discarded = await _environment.HttpClient.DeleteAsync(
      $"api/supervision/runs/{runId}?confirmed=true"
    );
    Assert.AreEqual(
      HttpStatusCode.NoContent,
      discarded.StatusCode
    );
  }

  private static void ResetSupervisionFixture()
  {
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    if (File.Exists(path))
    {
      File.Delete(path);
    }
  }
}
