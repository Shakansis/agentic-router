using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class DurableSupervisionEndToEndTests
  : ChatEndToEndTestBase<DurableSupervisionEndToEndTests>
{
  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task SupervisionDirectiveAndCloudRouteFailBeforeInference()
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

    using var executeResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "/supervisor build the complete local application",
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "native",
        approvalPolicy = "auto",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    executeResponse.EnsureSuccessStatusCode();
    var executeEvents = ParseSseEvents(
      await executeResponse.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "supervision-execution-not-enabled",
      executeEvents.Single(
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
  public async Task DurableManualRunRestoresInterruptedAndResumesWithoutInference()
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
    Assert.AreEqual(
      "prepared",
      resumed["state"]!.GetValue<string>()
    );
    Assert.HasCount(
      0,
      _environment.FakeOllama.Requests
    );

    using var cancelled = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/cancel",
      null
    );
    cancelled.EnsureSuccessStatusCode();
    await DiscardAsync(
      runId
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoSafeRestartEvaluatesLocalPredicatesWithoutInference()
  {
    _environment.FakeOllama.Reset();
    _ = await EnableHistoryAsync();
    var prepared = await PrepareAsync(
      "auto-safe"
    );
    var runId = prepared["runId"]!.GetValue<string>();

    await _environment.RestartApplicationAsync();
    var restored = await GetRunAsync(
      runId
    );
    Assert.AreEqual(
      "prepared",
      restored["state"]!.GetValue<string>()
    );
    Assert.IsTrue(
      restored["autoResumeEligible"]!.GetValue<bool>()
    );
    Assert.HasCount(
      0,
      _environment.FakeOllama.Requests
    );

    using var cancelled = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/cancel",
      null
    );
    cancelled.EnsureSuccessStatusCode();
    await DiscardAsync(
      runId
    );
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
        model = "alpha:latest",
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
}
