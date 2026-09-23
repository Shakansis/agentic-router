using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class DurableSupervisionEndToEndTests
  : ChatEndToEndTestBase<DurableSupervisionEndToEndTests>
{
  [TestMethod]
  [DataRow(false, false)]
  [DataRow(false, true)]
  [DataRow(true, false)]
  [DataRow(true, true)]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task OversizedSupervisionHistoryRecoversAtAdmission(bool resume, bool manyMessages)
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    const string marker = "AGENTIC_ROUTER_PERSISTED_HISTORY_COMPACTION_V1";
    const string requirement = "ADMISSION-CONSTRAINT: preserve existing configuration.";
    var history = manyMessages
      ? Enumerable.Range(0, 120).Select(index => new
      {
        role = index % 2 == 0 ? "user" : "assistant",
        content = index % 2 == 0 ? requirement : "Accepted constraint."
      }).ToArray()
      : [new { role = "assistant", content = string.Concat(Enumerable.Range(0, 8).Select(index =>
          marker + "\nPreviously compacted continuity context:\n"
          + "Requirements and constraints:\n- " + requirement
          + "\n" + string.Join("\n", Enumerable.Range(0, 80).Select(item => $"- REQUIRED-{item:D2}: retain this distinct restriction."))
          + "\nDecisions and relevant facts:\n- Historical tool diagnostic " + new string('x', 40_000)
          + "\nImportant file and workspace changes:\n- Verified edit: preserved.txt"
          + "\nUnresolved work and failures:\n- Recheck outstanding validation.\n")) }];
    string runId;
    if (resume)
    {
      _ = await EnableHistoryAsync();
      var prepared = await PrepareAsync("manual");
      runId = prepared["runId"]!.GetValue<string>();
      await _environment.RestartApplicationAsync();
      await WaitForStateAsync(runId, "interrupted-recoverable", TimeSpan.FromSeconds(10));
      await Page.GotoAsync("/");
      await PostFromBrowserAsync($"/api/supervision/runs/{runId}/resume", new
      {
        browserSessionId = Guid.NewGuid().ToString("N"),
        history
      });
    }
    else
    {
      await Page.GotoAsync("/");
      var prepared = await PostFromBrowserAsync("/api/supervision/runs/prepare", new
      {
        objective = "Build the complete local application in bounded steps",
        model = "qwen3-coder:30b",
        harness = "native",
        browserSessionId = Guid.NewGuid().ToString("N"),
        approvalPolicy = "auto",
        resumePolicy = "manual",
        history
      });
      runId = prepared["runId"]!.GetValue<string>();
      Assert.HasCount(0, _environment.FakeOllama.Requests, "Admission must not invoke a summarizing model.");
      await PostFromBrowserAsync($"/api/supervision/runs/{runId}/start", new { });
    }
    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>(), completed.ToJsonString());
    var prompts = _environment.FakeOllama.Requests.SelectMany(request => request.Messages).Select(message => message.Content).ToArray();
    Assert.IsTrue(prompts.Any(content => content.Contains(requirement, StringComparison.Ordinal)));
    if (!manyMessages)
    {
      var summaries = prompts.Where(content => content.Contains(marker, StringComparison.Ordinal)).ToArray();
      Assert.IsNotEmpty(summaries);
      Assert.IsTrue(summaries.All(content => Regex.Matches(content, marker).Count == 1));
      Assert.IsTrue(summaries.Any(content => content.Contains("preserved.txt", StringComparison.Ordinal)));
      Assert.IsTrue(summaries.Any(content => content.Contains("Recheck outstanding validation.", StringComparison.Ordinal)));
      Assert.IsTrue(summaries.Any(content => content.Contains("REQUIRED-00", StringComparison.Ordinal)
        && content.Contains("REQUIRED-79", StringComparison.Ordinal)));
      var activity = await Page.EvaluateAsync<string>(
        "async url => await (await fetch(url)).text()", $"/api/supervision/runs/{runId}/events?follow=false");
      StringAssert.Contains(activity, "Host history compacted for inference");
    }
    await DiscardAsync(runId);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task OversizedRequiredSupervisionConstraintsFailWithoutInference()
  {
    _environment.FakeOllama.Reset();
    await Page.GotoAsync("/");
    var result = await Page.EvaluateAsync<string>("""
      async () => {
        const response = await fetch('/api/supervision/runs/prepare', {
          method: 'POST', headers: {'Content-Type': 'application/json'},
          body: JSON.stringify({objective: 'Keep all specified requirements.', model: 'qwen3-coder:30b',
            harness: 'native', browserSessionId: 'oversized-required-context',
            history: [{role: 'user', content: 'Mandatory constraint '.repeat(20000)}]})
        });
        return JSON.stringify({status: response.status, error: await response.json()});
      }
      """);
    var error = JsonNode.Parse(result)!;
    Assert.AreEqual(413, error["status"]!.GetValue<int>());
    Assert.AreEqual("supervision-history-too-large", error["error"]!["code"]!.GetValue<string>());
    StringAssert.Contains(error["error"]!["message"]!.GetValue<string>(), "required constraints");
    Assert.HasCount(0, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ContinuityProjectionDoesNotMoveTheNativeHistoryCursorBackwards()
  {
    await Page.GotoAsync("/");
    var history = new List<object>
    {
      new { role = "assistant", content = "AGENTIC_ROUTER_PERSISTED_HISTORY_COMPACTION_V1\nRequirements and constraints:\n- CURSOR-CONSTRAINT: preserve config.\n" }
    };
    for (var index = 0; index < 10; index++)
    {
      history.Add(new { role = "user", content = $"CURSOR-USER-{index}: inspect the existing artifact." });
      history.Add(new { role = "assistant", content = new string('x', 20_000) });
    }
    var browserId = Guid.NewGuid().ToString("N");
    async Task SendAsync(IReadOnlyList<object> messages)
    {
      var stream = await Page.EvaluateAsync<string>("""
        async request => await (await fetch('/api/chat/stream', {
          method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(request)
        })).text()
        """, new
      {
        message = "Inspect the existing workspace.",
        history = messages,
        model = "qwen3.8:27b-gpu0",
        harness = "qwen-code",
        browserSessionId = browserId,
        interactionMode = "execute",
        executionStrategy = "direct",
        approvalPolicy = "auto"
      });
      Assert.IsFalse(ParseSseEvents(stream).Any(item => item["type"]!.GetValue<string>() == "error"), stream);
    }
    await SendAsync(history);
    var promptPath = Path.Combine(_environment.DataDirectory, "qwen-code-runtime", "fake-qwen-prompt.json");
    var first = JsonNode.Parse(await File.ReadAllTextAsync(promptPath))!;
    StringAssert.Contains(first["text"]!.GetValue<string>(), "CURSOR-CONSTRAINT");
    // Same canonical positions, with previously large optional observations reduced.
    for (var index = 2; index < history.Count; index += 2)
      history[index] = new { role = "assistant", content = "Recorded observation." };
    await SendAsync(history);
    var second = JsonNode.Parse(await File.ReadAllTextAsync(promptPath))!;
    Assert.AreEqual(first["sessionId"]!.GetValue<string>(), second["sessionId"]!.GetValue<string>());
    Assert.IsFalse(second["text"]!.GetValue<string>().Contains("CURSOR-USER", StringComparison.Ordinal));
    Assert.IsFalse(second["text"]!.GetValue<string>().Contains("Canonical Agentic Router conversation", StringComparison.Ordinal));
  }

  private async Task<JsonNode> PostFromBrowserAsync(string url, object body)
  {
    var result = await Page.EvaluateAsync<string>("""
      async ({url, body}) => {
        const response = await fetch(url, {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body)});
        const text = await response.text();
        if (!response.ok) throw new Error(text);
        return text;
      }
      """, new { url, body });
    return JsonNode.Parse(result)!;
  }

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
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoKeepsFiveStructuredItemsInDirectExecution()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var client = new HttpClient
    {
      BaseAddress = _environment.BaseUri,
      Timeout = TimeSpan.FromSeconds(45)
    };
    const string fiveItemObjective = """
      inspect the project without changing files
      - inspect item one
      - inspect item two
      - inspect item three
      - inspect item four
      - inspect item five
      """;
    using var fiveItemResponse = await client.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = fiveItemObjective,
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "native",
        approvalPolicy = "auto",
        executionStrategy = "auto",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    fiveItemResponse.EnsureSuccessStatusCode();
    var fiveItemEvents = ParseSseEvents(
      await fiveItemResponse.Content.ReadAsStringAsync()
    );
    Assert.HasCount(
      0,
      fiveItemEvents.Where(item => item["type"]!.GetValue<string>().StartsWith(
        "supervision.",
        StringComparison.Ordinal
      ))
    );
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutoPromotesSixStructuredItemsWhileDirectOverrideKeepsThemDirect()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var client = new HttpClient
    {
      BaseAddress = _environment.BaseUri,
      Timeout = TimeSpan.FromSeconds(45)
    };
    const string objective = """
      create file hello.txt with exact text hello world today
      - inspect the current artifact
      - implement the first bounded component
      - implement the second bounded component
      - implement the third bounded component
      - validate the integrated result
      - review the final artifact
      """;

    HttpResponseMessage automaticResponse;
    try
    {
      automaticResponse = await client.PostAsJsonAsync(
        "api/chat/stream",
        new
        {
          message = objective,
          model = "qwen3-coder:30b",
          history = Array.Empty<object>(),
          interactionMode = "execute",
          harness = "native",
          approvalPolicy = "auto",
          executionStrategy = "auto",
          browserSessionId = Guid.NewGuid().ToString("N")
        }
      );
    }
    catch (Exception exception)
    {
      Assert.Fail($"{exception}{Environment.NewLine}API output:{Environment.NewLine}{_environment.ApiOutput}");
      throw;
    }
    using var automaticResponseOwner = automaticResponse;
    automaticResponse.EnsureSuccessStatusCode();
    var automaticEvents = ParseSseEvents(
      await automaticResponse.Content.ReadAsStringAsync()
    );
    Assert.HasCount(
      1,
      automaticEvents.Where(item => item["type"]!.GetValue<string>() == "supervision.auto-selected")
    );
    Assert.HasCount(
      1,
      automaticEvents.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );

    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    const string directObjective = """
      inspect the project without changing files
      - inspect item one
      - inspect item two
      - inspect item three
      - inspect item four
      - inspect item five
      - inspect item six
      """;
    using var directResponse = await client.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "/direct " + directObjective,
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "native",
        approvalPolicy = "auto",
        executionStrategy = "auto",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    directResponse.EnsureSuccessStatusCode();
    var directEvents = ParseSseEvents(
      await directResponse.Content.ReadAsStringAsync()
    );
    Assert.HasCount(
      0,
      directEvents.Where(item => item["type"]!.GetValue<string>().StartsWith(
        "supervision.",
        StringComparison.Ordinal
      ))
    );
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(request => request.Messages.Any(
      message => message.Content.Contains("/direct", StringComparison.OrdinalIgnoreCase)
    )));
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutoTakesOverAcceptedPlanAboveConfiguredDirectLimit()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var client = new HttpClient
    {
      BaseAddress = _environment.BaseUri,
      Timeout = TimeSpan.FromSeconds(45)
    };
    HttpResponseMessage response;
    try
    {
      response = await client.PostAsJsonAsync(
        "api/chat/stream",
        new
        {
          message = "automatic plan takeover create file hello.txt with exact text hello world today",
          model = "qwen3-coder:30b",
          history = Array.Empty<object>(),
          interactionMode = "execute",
          harness = "native",
          approvalPolicy = "auto",
          executionStrategy = "auto",
          browserSessionId = Guid.NewGuid().ToString("N")
        }
      );
    }
    catch (Exception exception)
    {
      Assert.Fail($"{exception}{Environment.NewLine}API output:{Environment.NewLine}{_environment.ApiOutput}");
      throw;
    }
    using (response)
    {
      response.EnsureSuccessStatusCode();
      var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
      var eventTypes = events.Select(item => item["type"]!.GetValue<string>()).ToArray();
      Assert.IsLessThan(
        Array.IndexOf(eventTypes, "supervision.auto-selected"),
        Array.IndexOf(eventTypes, "execution-plan-created"),
        string.Join(", ", eventTypes)
      );
      Assert.HasCount(1, eventTypes.Where(type => type == "response.completed"));
    }

    using var runsResponse = await _environment.HttpClient.GetAsync("api/supervision/runs");
    runsResponse.EnsureSuccessStatusCode();
    var run = JsonNode.Parse(
      await runsResponse.Content.ReadAsStringAsync()
    )!["runs"]!.AsArray().Select(item => item!.AsObject()).Single(item =>
      item["objective"]!.GetValue<string>().Contains(
        "automatic plan takeover",
        StringComparison.OrdinalIgnoreCase
      )
    );
    Assert.AreEqual(6, run["takeover"]!["detectedPlanSteps"]!.GetValue<int>());
    Assert.AreEqual(5, run["takeover"]!["maximumDirectPlanSteps"]!.GetValue<int>());
    Assert.AreEqual(
      "accepted-plan-step-limit",
      run["takeover"]!["trigger"]!.GetValue<string>()
    );
    Assert.AreEqual(
      "completed",
      run["state"]!.GetValue<string>(),
      run.ToJsonString()
    );
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutoTakesOverRevisedLargePlanAfterVerifiedMutation()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var client = new HttpClient
    {
      BaseAddress = _environment.BaseUri,
      Timeout = TimeSpan.FromSeconds(45)
    };
    HttpResponseMessage response;
    try
    {
      response = await client.PostAsJsonAsync(
        "api/chat/stream",
        new
        {
          message = "late automatic plan takeover create file hello.txt with exact text hello world today",
          model = "qwen3-coder:30b",
          history = Array.Empty<object>(),
          interactionMode = "execute",
          harness = "native",
          approvalPolicy = "auto",
          executionStrategy = "auto",
          browserSessionId = Guid.NewGuid().ToString("N")
        }
      );
    }
    catch (Exception exception)
    {
      Assert.Fail($"{exception}{Environment.NewLine}API output:{Environment.NewLine}{_environment.ApiOutput}");
      throw;
    }
    using var responseOwner = response;
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.auto-selected"),
      string.Join(", ", events.Select(item => item["type"]!.GetValue<string>()))
    );
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );

    using var runsResponse = await _environment.HttpClient.GetAsync("api/supervision/runs");
    runsResponse.EnsureSuccessStatusCode();
    var run = JsonNode.Parse(
      await runsResponse.Content.ReadAsStringAsync()
    )!["runs"]!.AsArray().Select(item => item!.AsObject()).Single(item =>
      item["objective"]!.GetValue<string>().Contains(
        "late automatic plan takeover",
        StringComparison.OrdinalIgnoreCase
      )
    );
    var takeover = run["takeover"]!.AsObject();
    Assert.IsTrue(takeover["afterVerifiedMutation"]!.GetValue<bool>());
    Assert.IsTrue(takeover["files"]!.AsArray().Any(file =>
      file!["relativePath"]!.GetValue<string>() == "hello.txt"
      && file["verified"]!.GetValue<bool>()
    ));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>());
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutoRecoversProviderTimeoutAfterVerifiedDirectEffectThroughSupervisor()
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
        message = "automatic resource timeout takeover create file hello.txt with exact text hello world today",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "native",
        approvalPolicy = "auto",
        executionStrategy = "auto",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.auto-selected"),
      string.Join(", ", events.Select(item => item["type"]!.GetValue<string>()))
    );
    StringAssert.Contains(
      events.Single(item => item["type"]!.GetValue<string>() == "supervision.auto-selected")["message"]!.GetValue<string>(),
      "recoverable resource failure"
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "error")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed"),
      string.Join(Environment.NewLine, events.Select(item => item.ToJsonString()))
    );

    using var runsResponse = await _environment.HttpClient.GetAsync("api/supervision/runs");
    runsResponse.EnsureSuccessStatusCode();
    var run = JsonNode.Parse(
      await runsResponse.Content.ReadAsStringAsync()
    )!["runs"]!.AsArray().Select(item => item!.AsObject()).Single(item =>
      item["objective"]!.GetValue<string>().Contains(
        "automatic resource timeout takeover",
        StringComparison.OrdinalIgnoreCase
      )
    );
    Assert.IsTrue(run["takeover"]!["afterVerifiedMutation"]!.GetValue<bool>());
    Assert.AreEqual("completed", run["state"]!.GetValue<string>());
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutonomousApprovesExplicitDeleteAndCorrectsAwaitUserWithoutPausing()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    _ = await EnableHistoryAsync();
    var obsoletePath = Path.Combine(_environment.WorkspaceDirectory, "obsolete.txt");
    await File.WriteAllTextAsync(obsoletePath, "obsolete");

    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "autonomous explicit delete obsolete.txt and resolve ordinary ambiguity yourself",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "native",
        approvalPolicy = "ask",
        executionStrategy = "autonomous",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());

    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.autonomous-selected")
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() is
        "action.awaiting-approval" or "supervision.action-awaiting-approval")
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.awaiting-user")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed"),
      string.Join(Environment.NewLine, events.Select(item => item.ToJsonString()))
    );
    Assert.IsFalse(File.Exists(obsoletePath));
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request =>
      request.Messages.Any(message => message.Content.Contains(
        "SUPERVISION_AUTONOMOUS_DECISION_V1",
        StringComparison.Ordinal
      ))
    ));

    using var runsResponse = await _environment.HttpClient.GetAsync("api/supervision/runs");
    runsResponse.EnsureSuccessStatusCode();
    var run = JsonNode.Parse(
      await runsResponse.Content.ReadAsStringAsync()
    )!["runs"]!.AsArray().Select(item => item!.AsObject()).Single(item =>
      item["objective"]!.GetValue<string>().Contains(
        "autonomous explicit delete",
        StringComparison.OrdinalIgnoreCase
      )
    );
    Assert.AreEqual("autonomous", run["executionStrategy"]!.GetValue<string>());
    Assert.AreEqual("ask", run["approvalPolicy"]!.GetValue<string>());
    Assert.AreEqual("auto-safe", run["resumePolicy"]!.GetValue<string>());
    Assert.AreEqual("completed", run["state"]!.GetValue<string>());
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutonomousCannotBypassTrustedWorkspaceBoundary()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var client = new HttpClient
    {
      BaseAddress = _environment.BaseUri,
      Timeout = TimeSpan.FromSeconds(45)
    };
    var outsidePath = Path.GetFullPath(Path.Combine(
      _environment.WorkspaceDirectory,
      "..",
      "outside-autonomous.txt"
    ));
    await File.WriteAllTextAsync(outsidePath, "must remain");
    try
    {
      using var response = await client.PostAsJsonAsync(
        "api/chat/stream",
        new
        {
          message = "autonomous hard boundary",
          model = "qwen3-coder:30b",
          history = Array.Empty<object>(),
          interactionMode = "execute",
          harness = "native",
          approvalPolicy = "auto",
          executionStrategy = "autonomous",
          browserSessionId = Guid.NewGuid().ToString("N")
        }
      );
      response.EnsureSuccessStatusCode();
      var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
      Assert.HasCount(
        1,
        events.Where(item => item["type"]!.GetValue<string>() == "supervision.blocked"),
        string.Join(Environment.NewLine, events.Select(item => item.ToJsonString()))
      );
      Assert.HasCount(
        0,
        events.Where(item => item["type"]!.GetValue<string>() is
          "action.awaiting-approval" or "supervision.action-awaiting-approval")
      );
      Assert.AreEqual("must remain", await File.ReadAllTextAsync(outsidePath));
    }
    finally
    {
      if (File.Exists(outsidePath))
      {
        File.Delete(outsidePath);
      }
    }
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task AutonomousApprovalAuthorityIsRejectedOutsideAutonomousSupervision()
  {
    _environment.FakeOllama.Reset();
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "do not execute",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "native",
        approvalPolicy = "autonomous",
        executionStrategy = "direct",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    response.EnsureSuccessStatusCode();
    var error = ParseSseEvents(await response.Content.ReadAsStringAsync())
      .Single(item => item["type"]!.GetValue<string>() == "error");
    Assert.AreEqual(
      "autonomous-approval-requires-supervision",
      error["error"]!["code"]!.GetValue<string>()
    );
    Assert.HasCount(0, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(45_000, CooperativeCancellation = true)]
  public async Task AutonomousShowsPrimaryProgressAndRecoversOneInactiveTurn()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var originalResponse = await _environment.HttpClient.GetAsync("api/settings");
    originalResponse.EnsureSuccessStatusCode();
    var original = JsonNode.Parse(
      await originalResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var updated = original.DeepClone().AsObject();
    updated["runtime"]!["generationTimeoutSeconds"] = 2;
    using var updateResponse = await _environment.HttpClient.PutAsJsonAsync(
      "api/settings",
      updated
    );
    updateResponse.EnsureSuccessStatusCode();

    try
    {
      await Page.GotoAsync("/");
      await Page.GetByRole(
        AriaRole.Button,
        new() { Name = "Execute", Exact = true }
      ).ClickAsync();
      await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
      await Page.Locator("#harness-selector").SelectOptionAsync("native");
      await Page.Locator("#send-strategy-toggle").ClickAsync();
      await Page.Locator(
        "#send-strategy-menu [data-send-strategy=\"autonomous\"]"
      ).ClickAsync();
      await Page.Locator("#message-input").FillAsync(
        "autonomous watchdog feedback compact supervisor plan title create file hello.txt with exact text hello world today"
      );
      await Page.EvaluateAsync(
        """
        () => {
          const summary = document.querySelector('#context-usage-summary-text');
          const markObserved = () => {
            if (summary.textContent?.startsWith('Context ')
              && summary.textContent !== 'Context will be calculated when sending') {
              summary.dataset.contextObserved = 'true';
            }
          };
          new MutationObserver(markObserved).observe(summary,
            { childList: true, characterData: true, subtree: true });
        }
        """
      );
      await Page.Locator("#send-button").ClickAsync();

      var assistant = Page.Locator(".message.assistant").Last;
      await Expect(
        assistant.Locator(".supervision-progress-panel")
      ).ToHaveCountAsync(0);
      await Expect(assistant.Locator(".assistant-progress")).ToContainTextAsync(
        "Autonomous"
      );
      var sessionHeader = assistant.Locator(".execution-session-header");
      await Expect(sessionHeader).Not.ToHaveAttributeAsync("hidden", string.Empty);
      await Expect(sessionHeader).ToBeVisibleAsync();
      await Expect(assistant.Locator(".activity .execution-session-header")).ToHaveCountAsync(0);
      await Expect(assistant.Locator(".assistant-current-activity")).ToContainTextAsync("Planning work");
      await Expect(assistant.Locator(".assistant-current-activity")).ToContainTextAsync(
        "autonomous watchdog feedback"
      );
      await assistant.Locator(".activity > summary").ClickAsync();
      await Expect(sessionHeader).ToBeVisibleAsync();
      await Expect(sessionHeader).ToContainTextAsync("qwen3-coder:30b");
      await Expect(sessionHeader).ToContainTextAsync("native");
      await Expect(sessionHeader).ToContainTextAsync("Autonomous");
      await Expect(sessionHeader).ToContainTextAsync("approval auto");
      await Expect(assistant.Locator(".assistant-work-narrative")).ToContainTextAsync(
        "Supervisor: Planning the work queue for:",
        new() { Timeout = 1_800 }
      );
      await Expect(assistant.Locator(".request-slow-alert")).ToBeVisibleAsync(
        new() { Timeout = 6_000 }
      );
      await Expect(
        assistant.Locator(
          "[data-event-type=\"supervision.turn-slow-warning\"]"
        )
      ).ToHaveCountAsync(1);
      Assert.IsGreaterThan(
        0,
        await assistant.Locator(
          "[data-event-type=\"supervision.turn-status\"]"
        ).CountAsync()
      );
      await Expect(
        assistant.Locator(
          "[data-event-type=\"supervision.turn-slow-critical\"]"
        )
      ).ToHaveCountAsync(1);
      var recoveryEvent = assistant.Locator(
        "[data-event-type=\"supervision.turn-watchdog-recovery\"]"
      );
      try
      {
        await Expect(recoveryEvent).ToHaveCountAsync(1);
      }
      catch (PlaywrightException)
      {
        var activity = await assistant.Locator(".activity-row").AllTextContentsAsync();
        Assert.Fail(
          "Autonomous watchdog recovery was not observed. "
            + $"Narrative: {await assistant.Locator(".assistant-work-narrative").TextContentAsync()} "
            + $"Answer: {await assistant.Locator(".assistant-response").TextContentAsync()} "
            + $"Activity: {string.Join(" | ", activity)} "
            + $"API output: {_environment.ApiOutput}"
        );
      }
      var plan = assistant.Locator(".execution-plan");
      await Expect(plan).ToBeVisibleAsync(
        new() { Timeout = 25_000 }
      );
      await Expect(plan.Locator(".execution-plan-title")).ToContainTextAsync(
        "Plan"
      );
      await Expect(plan.Locator(".plan-step")).ToHaveCountAsync(1);
      var planStepTitle = plan.Locator(".plan-step > span").Nth(1);
      await Expect(planStepTitle).ToHaveTextAsync(
        "Claude Code-3 (Jogo da Velha) · criar o jogo em hello.txt com o conteúdo…"
      );
      await Expect(planStepTitle).ToHaveAttributeAsync(
        "title",
        new System.Text.RegularExpressions.Regex("deliberately verbose implementation details")
      );
      await Expect(Page.Locator("#context-usage-summary-text")).ToHaveAttributeAsync(
        "data-context-observed",
        "true",
        new() { Timeout = 25_000 }
      );
      await plan.Locator("summary").ClickAsync();
      await Expect(plan).ToHaveAttributeAsync("open", string.Empty);
      Assert.AreEqual(
        "auto",
        await plan.Locator(".execution-plan-body").EvaluateAsync<string>(
          "element => getComputedStyle(element).overflowY"
        )
      );
      await Page.Locator(".chat-header").ClickAsync();
      await Expect(plan).Not.ToHaveAttributeAsync("open", string.Empty);
      var reasoning = assistant.Locator(".assistant-reasoning").First;
      await Expect(reasoning).ToBeVisibleAsync();
      await Expect(reasoning.Locator(".assistant-reasoning-body")).ToContainTextAsync(
        "The supervisor is reviewing"
      );
      await Expect(reasoning).Not.ToHaveAttributeAsync("open", string.Empty);
      var workerReasoning = assistant.Locator(
        ".assistant-reasoning-body",
        new() { HasText = "Reasoning segment 30" }
      );
      await Expect(workerReasoning.First).ToBeAttachedAsync(
        new() { Timeout = 25_000 }
      );
      Assert.IsGreaterThan(
        0,
        await workerReasoning.CountAsync(),
        "Worker reasoning must remain available beyond the former 24-update cutoff."
      );
      Assert.IsFalse(
        await assistant.Locator(".assistant-reasoning").EvaluateAllAsync<bool>(
          "nodes => nodes.some(node => node.open)"
        ),
        "Supervisor and worker reasoning must remain available but start collapsed."
      );
      await Expect(
        assistant.Locator(
          ".work-action[data-state=\"completed\"] .work-action-file",
          new() { HasText = "hello.txt" }
        ).First
      ).ToBeVisibleAsync();
      await Expect(assistant.Locator(".assistant-response")).ToContainTextAsync(
        "Completed and verified from current Host evidence",
        new() { Timeout = 25_000 }
      );
      await Expect(
        assistant.Locator(
          ".activity-message",
          new() { HasText = "The active supervised context usage changed." }
        )
      ).ToHaveCountAsync(0);
      Assert.IsTrue(_environment.FakeOllama.Requests.Any(request =>
        request.Messages.Any(message => message.Content.Contains(
          "SUPERVISION_WATCHDOG_RECOVERY_V1",
          StringComparison.Ordinal
        ))
      ));
    }
    finally
    {
      using var restoreResponse = await _environment.HttpClient.PutAsJsonAsync(
        "api/settings",
        original
      );
      restoreResponse.EnsureSuccessStatusCode();
    }
  }

  [TestMethod]
  [DataRow(
    "opencode",
    "opencode-supervised.txt",
    "Created opencode-supervised.txt",
    "Edit",
    null
  )]
  [DataRow(
    "qwen-code",
    "qwen-supervised.txt",
    "Created qwen-supervised.txt",
    "Create files",
    "List files"
  )]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalAutonomousHarnessShowsCommentaryActionsAndExistingProgressSurfaces(
    string harness,
    string relativePath,
    string finalAnswer,
    string mutationLabel,
    string? inspectionLabel
  )
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var targetPath = Path.Combine(_environment.WorkspaceDirectory, relativePath);
    if (File.Exists(targetPath))
    {
      File.Delete(targetPath);
    }
    await Page.GotoAsync("/");
    await Page.GetByRole(
      AriaRole.Button,
      new() { Name = "Execute", Exact = true }
    ).ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);
    await Page.Locator("#send-strategy-toggle").ClickAsync();
    await Page.Locator(
      "#send-strategy-menu [data-send-strategy=\"autonomous\"]"
    ).ClickAsync();
    await Page.Locator("#message-input").FillAsync(
      $"Use {harness} autonomous supervision and expose generic worker activity."
    );
    await Page.Locator("#send-button").ClickAsync();

    var assistant = Page.Locator(".message.assistant").Last;
    var plan = assistant.Locator(".execution-plan");
    await Expect(plan).ToBeVisibleAsync();
    await Expect(plan.Locator(".plan-step")).ToHaveCountAsync(1);
    var mutation = assistant.Locator(
      ".work-action[data-state=\"completed\"]",
      new() { HasText = relativePath }
    );
    await Expect(mutation).ToBeVisibleAsync();
    await Expect(mutation.Locator(".work-action-label")).ToHaveTextAsync(mutationLabel);
    if (inspectionLabel is not null)
    {
      var inspection = assistant.Locator(
        ".work-action[data-state=\"completed\"]",
        new() { HasText = inspectionLabel }
      );
      await Expect(inspection).ToHaveCountAsync(1);
      await Expect(inspection).ToBeVisibleAsync();
    }
    await Expect(assistant.Locator(".assistant-reasoning").First).ToBeVisibleAsync();
    await Expect(assistant.Locator(".assistant-response")).ToContainTextAsync(
      finalAnswer
    );
    await Expect(assistant.Locator(".execution-completion-summary")).ToContainTextAsync($"Created: {relativePath}");
    var commentary = assistant.Locator(
      ".activity-row[data-event-type=\"supervision.turn-commentary\"]"
    );
    Assert.IsGreaterThan(0, await commentary.CountAsync());
    await Expect(commentary.First).ToContainTextAsync("I will");
    Assert.IsGreaterThan(
      0,
      await assistant.Locator(
        $"[data-event-type=\"harness.{harness}-effects-observed\"]"
      ).CountAsync()
    );
    Assert.IsGreaterThan(
      0,
      await assistant.Locator(
        "[data-event-type=\"validation-started\"]"
      ).CountAsync()
    );
    Assert.IsTrue(File.Exists(targetPath));
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task ComposerExposesIntegratedCompactStrategySplitButtonIncludingAutonomous()
  {
    await Page.GotoAsync("/");
    await Page.GetByRole(
      AriaRole.Button,
      new() { Name = "Execute", Exact = true }
    ).ClickAsync();
    await Expect(Page.Locator("#send-strategy-toggle")).ToBeVisibleAsync();
    await Expect(Page.Locator("#send-strategy-indicator")).ToHaveTextAsync("A");
    var send = Page.Locator("#send-button");
    var toggle = Page.Locator("#send-strategy-toggle");
    Assert.AreEqual(
      await send.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"),
      await toggle.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor")
    );
    var sendBox = await send.BoundingBoxAsync();
    var toggleBox = await toggle.BoundingBoxAsync();
    Assert.IsNotNull(sendBox);
    Assert.IsNotNull(toggleBox);
    Assert.AreEqual(sendBox.X + sendBox.Width, toggleBox.X, 1);

    await toggle.ClickAsync();
    var menu = Page.Locator("#send-strategy-menu");
    await Expect(menu).ToBeVisibleAsync();
    Assert.IsLessThanOrEqualTo(260, (await menu.BoundingBoxAsync())!.Width);
    Assert.AreEqual(
      "1",
      await menu.EvaluateAsync<string>("element => getComputedStyle(element).opacity")
    );
    await menu.Locator("[data-send-strategy=\"direct\"]").ClickAsync();
    await Expect(Page.Locator("#send-strategy-indicator")).ToHaveTextAsync("D");
    await Expect(Page.Locator("#send-button")).ToHaveAttributeAsync(
      "aria-label",
      "Send message using Direct execution strategy"
    );

    await Page.Locator("#send-strategy-toggle").ClickAsync();
    await menu.Locator("[data-send-strategy=\"autonomous\"]").ClickAsync();
    await Expect(Page.Locator("#send-strategy-indicator")).ToHaveTextAsync("∞");
    await Expect(Page.Locator("#approval-policy")).ToBeDisabledAsync();
    await Expect(Page.Locator("#approval-policy")).ToHaveAttributeAsync(
      "title",
      "Autonomous supervision approves every action the user could permit; hard Host boundaries remain enforced."
    );

    await toggle.ClickAsync();
    await menu.Locator("[data-send-strategy=\"auto\"]").ClickAsync();
    await Expect(Page.Locator("#send-strategy-indicator")).ToHaveTextAsync("A");
    await Expect(Page.Locator("#approval-policy")).ToBeEnabledAsync();
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task GeneralSettingsPersistsDirectLimitAndPhaseEffortProfile()
  {
    using var originalResponse = await _environment.HttpClient.GetAsync("api/settings");
    originalResponse.EnsureSuccessStatusCode();
    var original = JsonNode.Parse(
      await originalResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    try
    {
      await Page.GotoAsync("/");
      await Page.Locator("#open-settings").ClickAsync();
      var limit = Page.Locator("#max-direct-plan-steps");
      await Expect(limit).ToHaveValueAsync("5");
      await Expect(Page.Locator("#phase-effort-plan")).ToHaveValueAsync("high");
      await Expect(Page.Locator("#phase-effort-work")).ToHaveValueAsync("medium");
      await Expect(Page.Locator("#phase-effort-verify")).ToHaveValueAsync("medium");
      await Expect(Page.Locator("#phase-effort-complete")).ToHaveValueAsync("low");
      await Expect(Page.Locator("#phase-effort-recovery")).ToHaveValueAsync("high");
      await limit.FillAsync("6");
      await Page.Locator("#phase-effort-plan").SelectOptionAsync("low");
      await Page.Locator("#phase-effort-work").SelectOptionAsync("high");
      await Page.Locator("#phase-effort-verify").SelectOptionAsync("low");
      await Page.Locator("#phase-effort-complete").SelectOptionAsync("high");
      await Page.Locator("#phase-effort-recovery").SelectOptionAsync("medium");
      await Page.Locator("#save-settings").ClickAsync();
      await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");

      using var savedResponse = await _environment.HttpClient.GetAsync("api/settings");
      savedResponse.EnsureSuccessStatusCode();
      var saved = JsonNode.Parse(
        await savedResponse.Content.ReadAsStringAsync()
      )!.AsObject();
      Assert.AreEqual(
        6,
        saved["execution"]!["maxDirectPlanSteps"]!.GetValue<int>()
      );
      var effort = saved["execution"]!["phaseEffort"]!;
      Assert.AreEqual("low", effort["plan"]!.GetValue<string>());
      Assert.AreEqual("high", effort["work"]!.GetValue<string>());
      Assert.AreEqual("low", effort["verify"]!.GetValue<string>());
      Assert.AreEqual("high", effort["complete"]!.GetValue<string>());
      Assert.AreEqual("medium", effort["recovery"]!.GetValue<string>());
    }
    finally
    {
      using var restoreResponse = await _environment.HttpClient.PutAsJsonAsync(
        "api/settings",
        original
      );
      restoreResponse.EnsureSuccessStatusCode();
    }
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task SettingsRejectsUnknownPhaseEffortAtomically()
  {
    using var originalResponse = await _environment.HttpClient.GetAsync("api/settings");
    originalResponse.EnsureSuccessStatusCode();
    var candidate = JsonNode.Parse(
      await originalResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    candidate["execution"]!["phaseEffort"]!["verify"] = "extreme";

    using var response = await _environment.HttpClient.PutAsJsonAsync(
      "api/settings",
      candidate
    );

    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    var problem = await response.Content.ReadAsStringAsync();
    StringAssert.Contains(problem, "execution.phaseEffort.verify");
    using var unchangedResponse = await _environment.HttpClient.GetAsync("api/settings");
    unchangedResponse.EnsureSuccessStatusCode();
    var unchanged = JsonNode.Parse(
      await unchangedResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    Assert.AreNotEqual(
      "extreme",
      unchanged["execution"]!["phaseEffort"]!["verify"]!.GetValue<string>()
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task SupervisionAppliesConfiguredEffortToEachPhaseAndRecovery()
  {
    using var originalResponse = await _environment.HttpClient.GetAsync("api/settings");
    originalResponse.EnsureSuccessStatusCode();
    var original = JsonNode.Parse(
      await originalResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var configured = original.DeepClone().AsObject();
    configured["execution"]!["phaseEffort"] = new JsonObject
    {
      ["plan"] = "low",
      ["work"] = "high",
      ["verify"] = "low",
      ["complete"] = "high",
      ["recovery"] = "medium"
    };

    try
    {
      using var saveResponse = await _environment.HttpClient.PutAsJsonAsync(
        "api/settings",
        configured
      );
      saveResponse.EnsureSuccessStatusCode();
      _environment.FakeOllama.Reset();
      ResetSupervisionFixture();
      using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
        "api/supervision/runs/prepare",
        new
        {
          objective = "create file hello.txt with exact text hello world today",
          model = "gpt-oss:20b",
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

      Assert.IsTrue(run["terminal"]!.GetValue<bool>(), run.ToJsonString());
      Assert.AreEqual("completed", run["state"]!.GetValue<string>());
      var requests = _environment.FakeOllama.Requests;
      AssertEffort("SUPERVISION_DECOMPOSE_V1", "low");
      AssertEffort("SUPERVISION_WORKER_V1", "high");
      AssertEffort("SUPERVISION_VERIFY_V1", "low");
      AssertEffort("SUPERVISION_CORRECTION_V1", "medium");
      Assert.IsFalse(requests.Any(request => request.Messages.Any(message =>
        message.Content.Contains("SUPERVISION_COMPLETE_V1", StringComparison.Ordinal)
      )));

      void AssertEffort(string marker, string expected)
      {
        Assert.IsTrue(
          requests.Any(request =>
            string.Equals(request.Think, expected, StringComparison.Ordinal)
            && request.Messages.Any(message => message.Content.Contains(
              marker,
              StringComparison.Ordinal
            ))),
          $"No Ollama request applied effort '{expected}' for marker '{marker}'."
        );
      }
    }
    finally
    {
      using var restoreResponse = await _environment.HttpClient.PutAsJsonAsync(
        "api/settings",
        original
      );
      restoreResponse.EnsureSuccessStatusCode();
    }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task CorrectAtomicWorkConvergesOnFirstWorkerAttempt()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync("supervision first pass success");

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(1, run["runtime"]!["totalItems"]!.GetValue<int>());
    Assert.AreEqual(
      1,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
    var telemetry = run["runtime"]!["telemetry"]!.AsObject();
    Assert.AreEqual(1, telemetry["workerAttemptCount"]!.GetValue<int>());
    Assert.AreEqual(1, telemetry["actualWorkspaceMutationCount"]!.GetValue<int>());
    Assert.IsNull(telemetry["rejectionReason"]);
    Assert.IsNull(telemetry["retryReason"]);
    Assert.IsGreaterThanOrEqualTo(0, telemetry["decompositionDurationMilliseconds"]!.GetValue<long>());
    Assert.IsGreaterThanOrEqualTo(0, telemetry["workerDurationMilliseconds"]!.GetValue<long>());
    Assert.IsGreaterThanOrEqualTo(0, telemetry["verificationDurationMilliseconds"]!.GetValue<long>());
    Assert.IsGreaterThanOrEqualTo(0, telemetry["finalCompletionDurationMilliseconds"]!.GetValue<long>());

    using var eventsResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventsResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await eventsResponse.Content.ReadAsStringAsync());
    Assert.HasCount(0, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.work-rejected"
    ));
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.worker-started"
    ));
    var preflights = events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.preflight-completed"
    ).ToArray();
    Assert.HasCount(3, preflights);
    StringAssert.Contains(preflights[0]["message"]!.GetValue<string>(), "prepared immutable");
    Assert.HasCount(2, preflights.Where(item =>
      item["message"]!.GetValue<string>().Contains("reused immutable", StringComparison.Ordinal)
    ));
    Assert.IsTrue(preflights.All(item =>
      item["durationMilliseconds"]!.GetValue<long>() >= 0
    ));
    Assert.IsNotEmpty(_environment.FakeOllama.CapabilityQueries);
    Assert.AreEqual(3, _environment.FakeOllama.TagQueryCount);
    Assert.IsEmpty(events.Where(item =>
      item["role"]?.GetValue<string>() == "supervisor"
      && item["message"]?.GetValue<string>().Contains(
        "blocked-mutation-not-performed",
        StringComparison.Ordinal
      ) == true
    ));
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.deterministic-completion"
    ));
    Assert.AreEqual(
      events.Count(item => item["type"]!.GetValue<string>() is
        "supervision.supervisor-started" or "supervision.verification-started"),
      telemetry["supervisorTransitionCount"]!.GetValue<int>()
    );
    Assert.AreEqual(
      events.Single(item =>
        item["type"]!.GetValue<string>() == "supervision.work-queued"
      )["durationMilliseconds"]!.GetValue<long>(),
      telemetry["decompositionDurationMilliseconds"]!.GetValue<long>()
    );
    Assert.AreEqual(
      events.Single(item =>
        item["type"]!.GetValue<string>() == "supervision.worker-claimed"
      )["durationMilliseconds"]!.GetValue<long>(),
      telemetry["workerDurationMilliseconds"]!.GetValue<long>()
    );
    Assert.AreEqual(
      events.Single(item =>
        item["type"]!.GetValue<string>() == "supervision.work-accepted"
      )["durationMilliseconds"]!.GetValue<long>(),
      telemetry["verificationDurationMilliseconds"]!.GetValue<long>()
    );
    Assert.AreEqual(
      events.Single(item =>
        item["type"]!.GetValue<string>() == "supervision.completed"
      )["durationMilliseconds"]!.GetValue<long>(),
      telemetry["finalCompletionDurationMilliseconds"]!.GetValue<long>()
    );
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ExplicitSupervisorKeepsRouterSelectedWorkerAndUsesEachModelGpuAffinity()
  {
    var configured = _environment.BaselineSettings with
    {
      SupervisorModel = "gpt-oss:20b",
      DefaultGpu = "ollama:1",
      ModelGpuAffinities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["docs:latest"] = "device:GPU-nvidia-4090-fixture",
        ["gpt-oss:20b"] = "device:GPU-nvidia-2070-fixture"
      }
    };
    using var saved = await _environment.PutSettingsAsync(configured);
    Assert.AreEqual(
      HttpStatusCode.OK,
      saved.StatusCode,
      await saved.Content.ReadAsStringAsync()
    );

    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "write documentation supervision first pass success",
        model = "auto",
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
    var preparedRun = await GetRunAsync(runId);
    var route = preparedRun["route"]!.AsObject();
    Assert.AreEqual("docs:latest", route["model"]!.GetValue<string>());
    Assert.AreEqual("gpt-oss:20b", route["supervisorModel"]!.GetValue<string>());
    Assert.AreEqual(
      "NVIDIA GeForce RTX 4090",
      route["workerGpuDeviceName"]!.GetValue<string>()
    );
    Assert.AreEqual(
      "NVIDIA GeForce RTX 2070 SUPER",
      route["supervisorGpuDeviceName"]!.GetValue<string>()
    );
    Assert.IsFalse(route["gpuPlacementConflict"]!.GetValue<bool>());

    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();
    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual(
      "completed",
      completed["state"]!.GetValue<string>(),
      completed.ToJsonString()
    );

    var turns = _environment.FakeOllama.Requests.Where(
      request => request.Messages.Count > 0
    ).ToArray();
    var turnPlacements = string.Join(
      ", ",
      turns.Select(request => $"{request.Model}:main_gpu={request.MainGpu?.ToString() ?? "null"}")
    );
    Assert.IsTrue(turns.Any(request =>
      request.Model == "docs:latest" && request.MainGpu == 0
    ), $"The Router-selected Worker must use its explicit model affinity. {turnPlacements}");
    Assert.IsTrue(turns.Any(request =>
      request.Model == "gpt-oss:20b" && request.MainGpu == 1
    ), $"Supervisor turns must use the configured Supervisor model and its affinity. {turnPlacements}");
    Assert.IsFalse(turns.Any(request =>
      request.Model == "gpt-oss:20b" && request.MainGpu != 1
    ));
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutomaticSupervisorAndWorkerAffinityUseGeneralDefaultDespiteLegacyRoleGpus()
  {
    var settings = JsonSerializer.SerializeToNode(_environment.BaselineSettings, TestJson.Options)!;
    settings["supervisorModel"] = "gpt-oss:20b";
    settings["defaultGpu"] = "ollama:1";
    settings["coordinatorGpu"] = "ollama:0";
    settings["intentions"]!["documentation"]!["gpu"] = "ollama:0";
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    saved.EnsureSuccessStatusCode();
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    await Page.GotoAsync("/");
    using var prepare = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "write documentation supervision first pass success",
        model = "auto",
        harness = "native",
        approvalPolicy = "auto",
        resumePolicy = "manual",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    prepare.EnsureSuccessStatusCode();
    var runId = JsonNode.Parse(await prepare.Content.ReadAsStringAsync())!["runId"]!.GetValue<string>();
    var prepared = await GetRunAsync(runId);
    Assert.AreEqual("ollama:1", prepared["route"]!["workerGpuSelection"]!.GetValue<string>());
    Assert.AreEqual("ollama:1", prepared["route"]!["supervisorGpuSelection"]!.GetValue<string>());
    using var start = await _environment.HttpClient.PostAsync($"api/supervision/runs/{runId}/start", null);
    start.EnsureSuccessStatusCode();
    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>(), completed.ToJsonString());
    var turns = _environment.FakeOllama.Requests.Where(request => request.Messages.Count > 0).ToArray();
    foreach (var model in new[] { "docs:latest", "gpt-oss:20b" })
    {
      var requests = turns.Where(request => request.Model == model).ToArray();
      Assert.IsNotEmpty(requests);
      Assert.IsTrue(requests.All(request => request.MainGpu == 1));
    }
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task VulkanAutoWorkerAndSameWorkerSupervisorDoesNotWarn()
  {
    var configured = _environment.BaselineSettings with
    {
      DefaultGpu = "vulkan:all"
    };
    using var saved = await _environment.PutSettingsAsync(configured);
    Assert.AreEqual(
      HttpStatusCode.OK,
      saved.StatusCode,
      await saved.Content.ReadAsStringAsync()
    );

    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "supervision auto affinity compatibility",
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
    var run = await GetRunAsync(runId);
    var route = run["route"]!.AsObject();
    Assert.AreEqual("qwen3-coder:30b", route["model"]!.GetValue<string>());
    Assert.AreEqual(
      "qwen3-coder:30b",
      route["supervisorModel"]!.GetValue<string>()
    );
    Assert.AreEqual("vulkan:all", route["workerGpuSelection"]!.GetValue<string>());
    Assert.IsFalse(route["gpuPlacementConflict"]!.GetValue<bool>());
    await DiscardAsync(runId);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task VulkanMultiGpuWorkerAndPinnedSupervisorWarnsOnceAndSupportsCancelOrContinue()
  {
    var configured = _environment.BaselineSettings with
    {
      SupervisorModel = "gpt-oss:20b",
      DefaultGpu = "vulkan:all",
      ModelGpuAffinities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["gpt-oss:20b"] = "device:GPU-nvidia-4090-fixture"
      }
    };
    using var saved = await _environment.PutSettingsAsync(configured);
    Assert.AreEqual(
      HttpStatusCode.OK,
      saved.StatusCode,
      await saved.Content.ReadAsStringAsync()
    );

    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    await Page.GotoAsync("/");
    await Page.GetByRole(
      AriaRole.Button,
      new() { Name = "Execute", Exact = true }
    ).ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await Page.Locator("#send-strategy-toggle").ClickAsync();
    await Page.Locator(
      "#send-strategy-menu [data-send-strategy=\"supervised\"]"
    ).ClickAsync();

    await StartMessageAsync("supervision first pass success");
    await Expect(Page.Locator("#app-modal")).ToBeVisibleAsync();
    await Expect(Page.Locator("#app-modal-title")).ToHaveTextAsync(
      "Potential GPU placement conflict"
    );
    await Expect(Page.Locator("#app-modal-message")).ToContainTextAsync(
      "multiple GPUs through Vulkan"
    );
    await Page.Locator("#app-modal-cancel").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync(
      "Send",
      new() { Timeout = 20_000 }
    );
    Assert.HasCount(
      0,
      _environment.FakeOllama.Requests.Where(request => request.Stream)
    );

    await StartMessageAsync("supervision first pass success");
    await Expect(Page.Locator("#app-modal")).ToBeVisibleAsync();
    await Page.Locator("#app-modal-confirm").ClickAsync();
    var assistant = Page.Locator(".message.assistant").Last;
    await Expect(assistant.Locator(".activity")).ToHaveAttributeAsync(
      "data-terminal",
      "true",
      new() { Timeout = 60_000 }
    );
    await Expect(assistant.Locator(
      "[data-event-type=\"supervision.gpu-placement-warning\"]"
    )).ToHaveCountAsync(1);
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request =>
      request.Stream
      && request.Model == "gpt-oss:20b"
      && request.MainGpu == 0
    ));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ReadOnlySupervisorRolesIgnoreWorkerMutationCompletionGate()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync(
      "supervision role aware completion fallback"
    );

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    using var response = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    var supervisorGates = events.Where(item =>
      item["role"]?.GetValue<string>() == "supervisor"
      && item["type"]!.GetValue<string>() == "completion-gate-evaluated"
    ).ToArray();
    Assert.IsGreaterThanOrEqualTo(2, supervisorGates.Length);
    Assert.IsEmpty(supervisorGates.Where(item =>
      item["message"]!.GetValue<string>().Contains(
        "blocked-mutation-not-performed",
        StringComparison.Ordinal
      )
    ));
    foreach (var marker in new[]
    {
      "SUPERVISION_DECOMPOSE_V1",
      "SUPERVISION_VERIFY_V1",
      "SUPERVISION_COMPLETE_V1"
    })
    {
      Assert.IsTrue(_environment.FakeOllama.Requests.Any(request =>
        request.Messages.Any(message => message.Content.Contains(
          marker,
          StringComparison.Ordinal
        ))
      ), $"Expected role marker {marker}.");
    }
    var decompositionRequest = _environment.FakeOllama.Requests.Single(request =>
      request.Messages.Any(message => message.Content.Contains(
        "SUPERVISION_DECOMPOSE_V1",
        StringComparison.Ordinal
      ))
    );
    var decompositionPrompt = decompositionRequest.Messages.Single(message =>
      message.Content.Contains(
        "SUPERVISION_DECOMPOSE_V1",
        StringComparison.Ordinal
      )
    );
    StringAssert.Contains(
      decompositionPrompt.Content,
      "Never dispatch directory-only scaffolding"
    );
    StringAssert.Contains(
      decompositionPrompt.Content,
      "Empty evidencePaths and directory paths are not valid evidence"
    );
    StringAssert.Contains(
      decompositionPrompt.Content,
      "Leave enough of the bounded output budget to return one complete, concise JSON decision"
    );
    StringAssert.Contains(
      decompositionPrompt.Content,
      "A tool call is needed only when current workspace evidence must be inspected"
    );
    Assert.IsEmpty(events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.deterministic-completion"
    ));
  }

  [TestMethod]
  [DataRow("required", "MUST:")]
  [DataRow("preference", "SHOULD:")]
  [DataRow("optional", "MAY:")]
  [DataRow("mixed", "MUST:")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DecompositionPreservesRequirementModality(
    string modality,
    string expectedPrefix
  )
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync(
      $"supervision requirement modality {modality}"
    );

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    var criteria = run["runtime"]!["workItems"]![0]!["acceptanceCriteria"]!
      .AsArray()
      .Select(item => item!.GetValue<string>())
      .ToArray();
    Assert.IsTrue(criteria.Any(criterion =>
      criterion.StartsWith(expectedPrefix, StringComparison.Ordinal)
    ));
    if (modality == "mixed")
    {
      Assert.IsTrue(criteria.Any(value => value.StartsWith("SHOULD:", StringComparison.Ordinal)));
      Assert.IsTrue(criteria.Any(value => value.StartsWith("MAY:", StringComparison.Ordinal)));
    }
    using var response = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.work-accepted"
    ));
    Assert.IsEmpty(events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.work-rejected"
    ));
    if (modality == "mixed")
    {
      Assert.AreEqual(
        "hello world today",
        await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
      );
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RepeatedInvalidWorkerProposalReturnsTypedNoProgressThenRecoversDifferently()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync(
      "supervision deterministic planning repeat"
    );

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(
      2,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
    Assert.AreEqual(
      1,
      run["runtime"]!["telemetry"]!["actualWorkspaceMutationCount"]!.GetValue<int>()
    );
    using var response = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.retry-started"
      && item["retryReason"]?.GetValue<string>() == "planning-no-progress"
    ));
    Assert.IsEmpty(events.Where(item =>
      item["type"]!.GetValue<string>() == "recovery.requested"
    ));
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RepeatedNativeOutputLimitBlocksWithoutRepeatingWorkerAttempt()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync(
      "supervision always output limit"
    );

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("blocked", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(
      "local-action-output-limit",
      run["waitCode"]!.GetValue<string>()
    );
    Assert.AreEqual(
      1,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
    StringAssert.Contains(
      run["runtime"]!["lastFailure"]!.GetValue<string>(),
      "output-token limit again"
    );
    Assert.HasCount(
      2,
      _environment.FakeOllama.Requests.Where(request =>
        request.Messages.Any(message => message.Content.Contains(
          "supervision always output limit",
          StringComparison.OrdinalIgnoreCase
        ))
        && request.AvailableTools.Contains("create_file", StringComparer.Ordinal)
      )
    );
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AtomicDecompositionCoalescesRedundantVerifyAndReviewItems()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync("atomic redundant decomposition");

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(1, run["runtime"]!["totalItems"]!.GetValue<int>());
    Assert.AreEqual(1, run["runtime"]!["completedItems"]!.GetValue<int>());
    Assert.AreEqual(
      1,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
    Assert.AreEqual(
      1,
      run["runtime"]!["telemetry"]!["actualWorkspaceMutationCount"]!.GetValue<int>()
    );
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task CorrectionChangesOnlyIncorrectContentAndPreservesCorrectEffect()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync("supervision incremental correction");

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(
      "keep this\nfixed",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );
    Assert.AreEqual(
      2,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
    Assert.AreEqual(
      2,
      run["runtime"]!["telemetry"]!["actualWorkspaceMutationCount"]!.GetValue<int>()
    );
    var committedTools = run["recovery"]!["actions"]!.AsArray()
      .Where(action => action!["phase"]!.GetValue<string>() == "committed")
      .Select(action => action!["tool"]!.GetValue<string>())
      .ToArray();
    CollectionAssert.Contains(committedTools, "create_file");
    CollectionAssert.Contains(committedTools, "replace_text");
    CollectionAssert.DoesNotContain(committedTools, "write_file");
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
    Assert.HasCount(3, supervisorRequests);
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
      "Completed and verified from current Host evidence: create file hello.txt with content hello world today.",
      deltas[0]["delta"]!.GetValue<string>()
    );
    Assert.HasCount(
      1,
      events.Where(item =>
        item["type"]!.GetValue<string>() == "supervision.deterministic-completion"
      )
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
    await Expect(Page.Locator(".assistant-answer").Last).ToContainTextAsync(
      "Completed and verified from current Host evidence",
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
    await Expect(Page.Locator("#cancel-request")).ToBeHiddenAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Send");

    var newConversationResponse = Page.WaitForResponseAsync(response =>
      response.Url.EndsWith("/api/sessions/new", StringComparison.Ordinal)
      && response.Request.Method == "POST"
    );
    await Page.Locator("#new-conversation").ClickAsync();
    await newConversationResponse;
    await Expect(Page.Locator("#new-conversation")).ToBeEnabledAsync();
    var completedOpenResponse = Page.WaitForResponseAsync(response =>
      response.Url.EndsWith(
        $"/api/sessions/{conversationSessionId}/open",
        StringComparison.Ordinal
      ) && response.Request.Method == "POST"
    );
    await Page.Locator(
      $".session-entry[data-session-id=\"{conversationSessionId}\"] .session-entry-content"
    ).ClickAsync();
    await completedOpenResponse;
    await Expect(Page.Locator("#new-conversation")).ToBeEnabledAsync();
    await Expect(Page.Locator(".assistant-answer").Last).ToContainTextAsync(
      "Completed and verified from current Host evidence"
    );
    await Expect(Page.Locator("#conversation-persistence")).ToContainTextAsync("Saved locally");
    await Expect(Page.Locator("#supervision-recovery")).ToBeHiddenAsync();
    using var completedRunsResponse = await _environment.HttpClient.GetAsync(
      "api/supervision/runs"
    );
    completedRunsResponse.EnsureSuccessStatusCode();
    var completedRuns = JsonNode.Parse(
      await completedRunsResponse.Content.ReadAsStringAsync()
    )!["runs"]!.AsArray().Select(item => item!.AsObject()).Where(run =>
      run["conversationSessionId"]!.GetValue<string>() == conversationSessionId
    ).ToArray();
    Assert.HasCount(1, completedRuns);
    Assert.AreEqual(runId, completedRuns[0]["runId"]!.GetValue<string>());
    Assert.AreEqual("completed", completedRuns[0]["state"]!.GetValue<string>());
    Assert.IsTrue(completedRuns[0]["terminal"]!.GetValue<bool>());
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BrowserReloadPreservesPendingApprovalAuthorityForActiveSupervision()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    _ = await EnableHistoryAsync();
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("ask");
    await Page.Locator("#message-input").FillAsync(
      "/supervisor supervision restart boundary"
    );
    await Page.Locator("#send-button").ClickAsync();
    await Expect(
      Page.Locator("[data-event-type=\"action.awaiting-approval\"]")
    ).ToBeVisibleAsync(new() { Timeout = 30_000 });

    using var runsResponse = await _environment.HttpClient.GetAsync(
      "api/supervision/runs"
    );
    runsResponse.EnsureSuccessStatusCode();
    var run = JsonNode.Parse(
      await runsResponse.Content.ReadAsStringAsync()
    )!["runs"]!.AsArray().Select(item => item!.AsObject()).Single(item =>
      item["objective"]!.GetValue<string>() == "supervision restart boundary"
      && item["state"]!.GetValue<string>() == "running"
    );
    var conversationSessionId = run["conversationSessionId"]!.GetValue<string>();
    var browserSessionId = await Page.EvaluateAsync<string>(
      "sessionStorage.getItem('agentic-router.browser-session-id')"
    );
    Assert.IsFalse(string.IsNullOrWhiteSpace(browserSessionId));

    await Page.ReloadAsync();
    Assert.AreEqual(
      browserSessionId,
      await Page.EvaluateAsync<string>(
        "sessionStorage.getItem('agentic-router.browser-session-id')"
      )
    );
    var sessionButton = Page.Locator(
      $".session-entry[data-session-id=\"{conversationSessionId}\"] .session-entry-content"
    );
    await Expect(sessionButton).ToBeVisibleAsync(new() { Timeout = 10_000 });
    await sessionButton.ClickAsync();

    var approval = Page.Locator(
      "[data-event-type=\"action.awaiting-approval\"]"
    ).Last;
    var reject = approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Reject",
        Exact = true
      }
    );
    await Expect(reject).ToBeEnabledAsync(new() { Timeout = 10_000 });
    var decisionResponseTask = Page.WaitForResponseAsync(response =>
      response.Url.Contains("/api/actions/", StringComparison.Ordinal)
      && response.Url.EndsWith("/decision", StringComparison.Ordinal)
      && response.Request.Method == "POST"
    );
    await reject.ClickAsync();
    var decisionResponse = await decisionResponseTask;
    Assert.AreEqual(
      HttpStatusCode.OK,
      (HttpStatusCode)decisionResponse.Status,
      await decisionResponse.TextAsync()
    );
    await Expect(Page.Locator(".app-toast[data-tone=\"error\"]"))
      .ToHaveCountAsync(0);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ExplicitCancelTerminatesAutonomousCheckpointBeforeEditedPromptRestarts()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var workspaceId = await EnableHistoryAsync();
    await Page.GotoAsync("/");
    await Page.GetByRole(
      AriaRole.Button,
      new() { Name = "Execute", Exact = true }
    ).ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await Page.Locator("#harness-selector").SelectOptionAsync("native");
    await Page.Locator("#send-strategy-toggle").ClickAsync();
    await Page.Locator(
      "#send-strategy-menu [data-send-strategy=\"autonomous\"]"
    ).ClickAsync();
    const string prompt = "supervision restart boundary";
    await Page.Locator("#message-input").FillAsync(prompt);
    await Page.Locator("#send-button").ClickAsync();

    JsonObject? firstRun = null;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    do
    {
      using var response = await _environment.HttpClient.GetAsync(
        "api/supervision/runs"
      );
      response.EnsureSuccessStatusCode();
      firstRun = JsonNode.Parse(
        await response.Content.ReadAsStringAsync()
      )!["runs"]!.AsArray().Select(item => item!.AsObject()).Where(run =>
        run["objective"]!.GetValue<string>() == prompt
        && run["executionStrategy"]!.GetValue<string>() == "autonomous"
        && !run["terminal"]!.GetValue<bool>()
      ).OrderByDescending(run => run["createdAt"]!.GetValue<DateTimeOffset>())
        .FirstOrDefault();
      if (firstRun?["phase"]!.GetValue<string>() == "verifying")
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.IsNotNull(firstRun);
    Assert.AreEqual("verifying", firstRun["phase"]!.GetValue<string>());
    var firstRunId = firstRun["runId"]!.GetValue<string>();
    var conversationSessionId = firstRun["conversationSessionId"]!.GetValue<string>();

    await Page.Locator("#cancel-request").ClickAsync();
    var cancelled = await WaitForStateAsync(
      firstRunId,
      "cancelled",
      TimeSpan.FromSeconds(10)
    );
    Assert.IsTrue(cancelled["terminal"]!.GetValue<bool>());
    await Expect(Page.Locator("#cancel-request")).ToBeHiddenAsync();
    using (var sessionResponse = await _environment.HttpClient.GetAsync(
      $"api/sessions/{conversationSessionId}?workspaceId={workspaceId}"
    ))
    {
      sessionResponse.EnsureSuccessStatusCode();
      var persisted = JsonNode.Parse(
        await sessionResponse.Content.ReadAsStringAsync()
      )!.AsObject();
      Assert.AreEqual("cancelled", persisted["state"]!.GetValue<string>());
      Assert.IsFalse(persisted["interrupted"]!.GetValue<bool>());
    }

    await Page.Locator(".message.user").First.GetByRole(
      AriaRole.Button,
      new() { Name = "Edit message", Exact = true }
    ).ClickAsync();
    await Expect(Page.Locator("#message-input")).ToHaveValueAsync(prompt);
    await Page.Locator("#message-input").PressAsync("Enter");

    JsonObject? replacement = null;
    deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    do
    {
      using var response = await _environment.HttpClient.GetAsync(
        "api/supervision/runs"
      );
      response.EnsureSuccessStatusCode();
      replacement = JsonNode.Parse(
        await response.Content.ReadAsStringAsync()
      )!["runs"]!.AsArray().Select(item => item!.AsObject()).FirstOrDefault(run =>
        run["conversationSessionId"]!.GetValue<string>() == conversationSessionId
        && run["objective"]!.GetValue<string>() == prompt
        && run["runId"]!.GetValue<string>() != firstRunId
      );
      if (replacement is not null)
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.IsNotNull(replacement);
    var replacementRunId = replacement["runId"]!.GetValue<string>();
    var completed = await WaitForTerminalAsync(
      replacementRunId,
      TimeSpan.FromSeconds(30)
    );
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>());
    Assert.AreNotEqual(
      "supervision-recovery-workspace-busy",
      completed["waitCode"]?.GetValue<string>()
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ChangedEvidenceThatIsNowCorrectIsReverifiedWithoutWorkerRetry()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync("supervision stale becomes correct");

    JsonObject verifying;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    do
    {
      verifying = await GetRunAsync(runId);
      if (
        verifying["phase"]!.GetValue<string>() == "verifying"
        && verifying["runtime"]!["activeRole"]!.GetValue<string>() == "supervisor"
        && verifying["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>() == 1
      )
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.AreEqual(
      1,
      verifying["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>(),
      verifying.ToJsonString()
    );
    await File.WriteAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "hello.txt"),
      "hello world today"
    );

    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(40));
    Assert.AreEqual(
      "completed",
      completed["state"]!.GetValue<string>(),
      completed.ToJsonString()
    );
    Assert.AreEqual(
      1,
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
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "supervision.work-rejected")
    );
    Assert.HasCount(
      1,
      events.Where(item =>
        item["retryReason"]?.GetValue<string>() == "stale-evidence-reverification"
      )
    );
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ChangedEvidenceThatIsWrongReceivesOneBoundedCorrection()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync("supervision stale latest wrong");

    JsonObject verifying;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    do
    {
      verifying = await GetRunAsync(runId);
      if (
        verifying["phase"]!.GetValue<string>() == "verifying"
        && verifying["runtime"]!["activeRole"]!.GetValue<string>() == "supervisor"
        && verifying["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>() == 1
      )
      {
        break;
      }
      await Task.Delay(50);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.AreEqual(
      1,
      verifying["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>(),
      verifying.ToJsonString()
    );
    await File.WriteAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "hello.txt"),
      "externally changed and wrong"
    );

    var completed = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(40));
    Assert.AreEqual("completed", completed["state"]!.GetValue<string>(), completed.ToJsonString());
    Assert.AreEqual(
      2,
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
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.work-rejected"
    ));
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.retry-started"
      && item["retryReason"]?.GetValue<string>() == "acceptance-mismatch"
    ));
    Assert.AreEqual(
      2,
      completed["runtime"]!["telemetry"]!["actualWorkspaceMutationCount"]!.GetValue<int>()
    );
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task MalformedSupervisorDecisionUsesOneDifferentRecoveryThenBlocks()
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
    Assert.HasCount(2, decompositionRequests);
    Assert.IsTrue(
      decompositionRequests.Any(request => request.Messages.Any(message =>
        message.Content.Contains(
          "SUPERVISION_CANONICAL_RECOVERY_V1",
          StringComparison.Ordinal
        )
      ))
    );
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
  [Timeout(40_000, CooperativeCancellation = true)]
  public async Task InvalidVerificationDecisionUsesOneCanonicalCorrection()
  {
    _environment.FakeOllama.Reset();
    var runId = await StartNativeSupervisionAsync(
      "supervision first pass success with verification decision repair"
    );

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(
      1,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
    using var response = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.IsFalse(events.Any(item =>
      item["type"]!.GetValue<string>() == "supervision.blocked"
    ));
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request =>
      request.Messages.Any(message => message.Content.Contains(
        "SUPERVISION_VERIFICATION_DECISION_RECOVERY_V1",
        StringComparison.Ordinal
      ))
    ));
  }

  [TestMethod]
  [Timeout(40_000, CooperativeCancellation = true)]
  public async Task InvalidDecompositionQueueUsesOneBoundedSemanticCorrection()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var runId = await StartNativeSupervisionAsync(
      "invalid decomposition queue recovery"
    );

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(1, run["runtime"]!["totalItems"]!.GetValue<int>());
    Assert.AreEqual(4, run["runtime"]!["supervisorTransitionCount"]!.GetValue<int>());
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
    );

    using var response = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>()
        == "supervision.turn-decomposition-decision-recovery"
      && item["retryReason"]!.GetValue<string>()
        == "decomposition-decision-recovery"
    ));
    Assert.IsFalse(events.Any(item =>
      item["type"]!.GetValue<string>() == "supervision.blocked"
    ));
    var recoveryRequests = _environment.FakeOllama.Requests.Where(request =>
      request.Messages.Any(message => message.Content.Contains(
        "SUPERVISION_DECOMPOSITION_DECISION_RECOVERY_V1",
        StringComparison.Ordinal
      ))
    ).ToArray();
    Assert.HasCount(1, recoveryRequests);
  }

  [TestMethod]
  [DataRow(true)]
  [DataRow(false)]
  [DoNotParallelize]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenSupervisorRecoveryKeepsCompactedContinuityWithoutDuplicatingNativeHistory(bool toolLoop)
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    var targetPath = Path.Combine(_environment.WorkspaceDirectory, "qwen-supervised.txt");
    if (File.Exists(targetPath))
    {
      File.Delete(targetPath);
    }
    var markerPath = Path.Combine(
      _environment.DataDirectory,
      "qwen-code-runtime",
      "fake-qwen-supervision-tool-loop-recovery.json"
    );
    if (File.Exists(markerPath))
    {
      File.Delete(markerPath);
    }

    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = toolLoop ? "qwen supervision tool loop recovery" : "qwen supervision empty response recovery",
        model = "qwen3.8:27b-gpu0",
        harness = "qwen-code",
        approvalPolicy = "auto",
        resumePolicy = "manual",
        browserSessionId = Guid.NewGuid().ToString("N"),
        history = new object[]
        {
          new
          {
            role = "assistant",
            content = "AGENTIC_ROUTER_PERSISTED_HISTORY_COMPACTION_V1\nRequirements and constraints:\n- Preserve the prior implementation objective."
          },
          new { role = "user", content = "try again" },
          new { role = "assistant", content = "The previous supervised attempt failed." }
        }
      }
    );
    prepareResponse.EnsureSuccessStatusCode();
    var runId = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(45));
    Assert.AreEqual("completed", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual("visible Qwen activity", await File.ReadAllTextAsync(targetPath));
    using var eventsResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventsResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await eventsResponse.Content.ReadAsStringAsync());
    Assert.HasCount(1, events.Where(item =>
      item["type"]!.GetValue<string>() == "supervision.turn-harness-recovery"
      && item["retryReason"]!.GetValue<string>() == (toolLoop ? "tool-loop-recovery" : "harness-recovery")
    ));

    using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
    Assert.IsTrue(marker.RootElement.GetProperty("includesPersistedContinuity").GetBoolean());
    Assert.AreEqual(1, marker.RootElement.GetProperty("continuityHydrations").GetInt32());
    Assert.AreEqual(toolLoop, marker.RootElement.GetProperty("forbidsTools").GetBoolean());
    Assert.AreEqual(toolLoop, marker.RootElement.GetProperty("promptContainsPersistedContinuity").GetBoolean());
    Assert.AreEqual(toolLoop ? 1 : 2, marker.RootElement.GetProperty("promptNumber").GetInt32());
    Assert.IsFalse(events.Any(item => item["message"]?.GetValue<string>().Contains(
      "reset its native session", StringComparison.Ordinal
    ) == true));
  }

  [TestMethod]
  [Timeout(40_000, CooperativeCancellation = true)]
  public async Task FinalSupervisorStopBlockedPreservesItsTypedReason()
  {
    _environment.FakeOllama.Reset();
    var runId = await StartNativeSupervisionAsync(
      "supervision role aware completion fallback with supervision final blocked decision"
    );

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));
    Assert.AreEqual("blocked", run["state"]!.GetValue<string>(), run.ToJsonString());
    Assert.AreEqual(
      "supervision-completion-blocked",
      run["waitCode"]!.GetValue<string>()
    );
    Assert.AreEqual(
      "Required external validation is unavailable.",
      run["runtime"]!["lastFailure"]!.GetValue<string>()
    );
  }

  [TestMethod]
  [Timeout(45_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeSupervisorParsesOnlyCanonicalTextAfterLastToolCall()
  {
    ResetSupervisionFixture();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "claude supervision canonical after tool preamble",
        model = "qwen3-coder:30b",
        harness = "claude-code",
        approvalPolicy = "auto",
        resumePolicy = "manual",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    Assert.AreEqual(
      HttpStatusCode.Accepted,
      prepareResponse.StatusCode,
      await prepareResponse.Content.ReadAsStringAsync()
    );
    var prepared = JsonNode.Parse(
      await prepareResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var runId = prepared["runId"]!.GetValue<string>();
    using var startResponse = await _environment.HttpClient.PostAsync(
      $"api/supervision/runs/{runId}/start",
      null
    );
    startResponse.EnsureSuccessStatusCode();

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(30));

    Assert.AreEqual(
      "completed",
      run["state"]!.GetValue<string>(),
      run.ToJsonString()
    );
    Assert.IsNull(run["runtime"]!["lastFailure"]);
    Assert.AreEqual(
      "Created hello.txt and verified its exact content.",
      run["runtime"]!["finalAnswer"]!.GetValue<string>()
    );
    using var eventResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}/events?follow=false"
    );
    eventResponse.EnsureSuccessStatusCode();
    var events = ParseSseEvents(
      await eventResponse.Content.ReadAsStringAsync()
    );
    Assert.HasCount(
      1,
      events.Where(item =>
        item["type"]!.GetValue<string>() == "supervision.turn-canonical-recovery"
      )
    );
    Assert.AreEqual(
      "hello world today",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "hello.txt")
      )
    );
  }

  [TestMethod]
  [Timeout(45_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeAutonomousContextSnapshotsStaySilentAndRetainTerminalFit()
  {
    ResetSupervisionFixture();
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "claude supervision canonical after tool preamble",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "claude-code",
        approvalPolicy = "auto",
        executionStrategy = "autonomous",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    var contextEvents = events.Where(item =>
      item["type"]!.GetValue<string>() == "context.usage"
    ).ToArray();
    Assert.IsNotEmpty(contextEvents);
    Assert.IsTrue(contextEvents.All(item =>
      string.IsNullOrWhiteSpace(item["message"]?.GetValue<string>())
      && item["supervisionProgress"] is null
      && item["contextUsage"] is JsonObject
    ), string.Join(
      Environment.NewLine,
      contextEvents.Select(item =>
        $"message={item["message"]?.GetValue<string>() ?? "<null>"}; "
        + $"supervisionProgress={item["supervisionProgress"] is not null}; "
        + $"contextUsage={item["contextUsage"] is JsonObject}"
      )
    ));
    var terminal = events.Single(item =>
      item["type"]!.GetValue<string>() == "response.completed"
    );
    Assert.IsInstanceOfType<JsonObject>(terminal["contextUsage"]);
    var traceId = terminal["diagnostic"]!["traceId"]!.GetValue<string>();

    using var traceResponse = await _environment.HttpClient.GetAsync(
      $"api/diagnostics/traces/{Uri.EscapeDataString(traceId)}"
    );
    traceResponse.EnsureSuccessStatusCode();
    var report = JsonNode.Parse(
      await traceResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    Assert.IsInstanceOfType<JsonObject>(report["contextFit"]);
    Assert.IsFalse(report["events"]!.AsArray().Any(item =>
      item?["stage"]?.GetValue<string>() == "context.usage"
    ));
  }

  [TestMethod]
  [Timeout(45_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeSupervisorRecoversOutputTokenLimitInFreshNativeSession()
  {
    ResetSupervisionFixture();
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "claude supervision recovers output ceiling",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "claude-code",
        approvalPolicy = "auto",
        executionStrategy = "autonomous",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());

    Assert.HasCount(
      1,
      events.Where(item =>
        item["type"]!.GetValue<string>() == "supervision.turn-harness-recovery"
      )
    );
    Assert.HasCount(0, events.Where(item => item["type"]!.GetValue<string>() == "error"));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.AreEqual(
      "recovered",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "output-recovery.txt")
      )
    );
    using var recoveryMarker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
      _environment.DataDirectory,
      "claude-code-runtime",
      "fake-claude-harness-recovery.json"
    )));
    Assert.IsFalse(recoveryMarker.RootElement.GetProperty("resumed").GetBoolean());
    Assert.IsTrue(
      recoveryMarker.RootElement.GetProperty("promptContainsRecoveryMarker").GetBoolean()
    );
  }

  [TestMethod]
  [Timeout(45_000, CooperativeCancellation = true)]
  public async Task RepeatedClaudeCodeOutputTokenLimitRetainsTypedTerminalCode()
  {
    ResetSupervisionFixture();
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "claude supervision repeats output ceiling",
        model = "qwen3-coder:30b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "claude-code",
        approvalPolicy = "auto",
        executionStrategy = "autonomous",
        browserSessionId = Guid.NewGuid().ToString("N")
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());

    Assert.HasCount(
      1,
      events.Where(item =>
        item["type"]!.GetValue<string>() == "supervision.turn-harness-recovery"
      )
    );
    var terminal = events.Single(item => item["type"]!.GetValue<string>() == "error");
    var error = terminal["error"]!.AsObject();
    Assert.AreEqual(
      "claude-code-output-token-limit",
      error["code"]!.GetValue<string>()
    );
    StringAssert.Contains(
      error["message"]!.GetValue<string>(),
      "exceeded its configured output-token limit"
    );
    var runId = error["details"]!["runId"]!.GetValue<string>();
    var run = await GetRunAsync(runId);
    Assert.AreEqual(
      "claude-code-output-token-limit",
      run["waitCode"]!.GetValue<string>()
    );
    using var traceResponse = await _environment.HttpClient.GetAsync(
      $"api/diagnostics/traces/{Uri.EscapeDataString(terminal["diagnostic"]!["traceId"]!.GetValue<string>())}"
    );
    traceResponse.EnsureSuccessStatusCode();
    var trace = JsonNode.Parse(await traceResponse.Content.ReadAsStringAsync())!.AsObject();
    Assert.AreEqual(
      "claude-code-output-token-limit",
      trace["failureCode"]!.GetValue<string>()
    );
  }

  [TestMethod]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task ConfiguredEvidencePathLimitAllowsFourteenDeclaredPaths()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective = "supervision fourteen evidence paths",
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

    var run = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(20));

    Assert.AreEqual(
      "completed",
      run["state"]!.GetValue<string>(),
      run.ToJsonString()
    );
    Assert.HasCount(
      14,
      run["runtime"]!["workItems"]![0]!["evidencePaths"]!.AsArray()
    );
    Assert.IsNull(run["runtime"]!["lastFailure"]);
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(30_000, CooperativeCancellation = true)]
  public async Task SupervisionTurnStatusDoesNotDependOnProviderFrames()
  {
    _environment.FakeOllama.Reset();
    ResetSupervisionFixture();
    using var originalResponse = await _environment.HttpClient.GetAsync("api/settings");
    originalResponse.EnsureSuccessStatusCode();
    var original = JsonNode.Parse(
      await originalResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    var updated = original.DeepClone().AsObject();
    updated["runtime"]!["generationTimeoutSeconds"] = 6;
    using var updateResponse = await _environment.HttpClient.PutAsJsonAsync(
      "api/settings",
      updated
    );
    updateResponse.EnsureSuccessStatusCode();

    try
    {
      using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
        "api/supervision/runs/prepare",
        new
        {
          objective = "supervision independent periodic status create file hello.txt",
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

      IReadOnlyList<JsonObject> events = [];
      var deadline = DateTimeOffset.UtcNow.AddSeconds(3.5);
      do
      {
        await Task.Delay(100);
        using var eventResponse = await _environment.HttpClient.GetAsync(
          $"api/supervision/runs/{runId}/events?follow=false"
        );
        eventResponse.EnsureSuccessStatusCode();
        events = ParseSseEvents(
          await eventResponse.Content.ReadAsStringAsync()
        );
        if (events.Any(item =>
          item["type"]!.GetValue<string>() == "supervision.turn-status"
        ))
        {
          break;
        }
      } while (DateTimeOffset.UtcNow < deadline);

      Assert.HasCount(
        1,
        events.Where(item =>
          item["type"]!.GetValue<string>() == "supervision.turn-status"
        )
      );
      Assert.HasCount(
        0,
        events.Where(item =>
          item["type"]!.GetValue<string>() is "supervision.turn-slow-warning"
            or "supervision.turn-slow-critical"
        )
      );
      var active = await GetRunAsync(runId);
      Assert.IsFalse(active["terminal"]!.GetValue<bool>());
      _ = await WaitForTerminalAsync(runId, TimeSpan.FromSeconds(15));
    }
    finally
    {
      using var restoreResponse = await _environment.HttpClient.PutAsJsonAsync(
        "api/settings",
        original
      );
      restoreResponse.EnsureSuccessStatusCode();
    }
  }

  [TestMethod]
  [Timeout(45_000, CooperativeCancellation = true)]
  public async Task RepeatedIdenticalCorrectionStopsBeforeGenericRetryBudget()
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
    Assert.AreEqual(
      "supervision-correction-no-progress",
      run["waitCode"]!.GetValue<string>()
    );
    Assert.AreEqual(
      2,
      run["runtime"]!["workItems"]![0]!["attemptCount"]!.GetValue<int>()
    );
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
      5,
      checkpointDocument.RootElement.GetProperty("schemaVersion").GetInt32()
    );
    Assert.AreEqual(
      "supervised",
      checkpointDocument.RootElement.GetProperty("executionStrategy").GetString()
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
    var restored = await WaitForStateAsync(
      runId,
      "interrupted-recoverable",
      TimeSpan.FromSeconds(10)
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
    var restored = await WaitForStateAsync(
      runId,
      "awaiting-user",
      TimeSpan.FromSeconds(10)
    );
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
    var requestsWhileStopped = -1;
    await _environment.RestartApplicationAsync(
      () =>
      {
        requestsWhileStopped = _environment.FakeOllama.Requests.Count;
        return Task.CompletedTask;
      }
    );
    var restored = await WaitForStateAsync(
      runId,
      "awaiting-user",
      TimeSpan.FromSeconds(10)
    );
    Assert.AreEqual("awaiting-user", restored["state"]!.GetValue<string>());
    Assert.AreEqual(
      "supervision-recovery-workspace-drift",
      restored["waitCode"]!.GetValue<string>()
    );
    Assert.HasCount(requestsWhileStopped, _environment.FakeOllama.Requests);
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
    var restored = await WaitForStateAsync(
      runId,
      "awaiting-user",
      TimeSpan.FromSeconds(10)
    );
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
    var restored = await WaitForStateAsync(
      runId,
      "interrupted-recoverable",
      TimeSpan.FromSeconds(10)
    );
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
  public async Task VersionFourCheckpointMigratesWithoutLosingRecoveryState()
  {
    var workspaceId = await EnableHistoryAsync();
    var conversationId = Guid.NewGuid().ToString("N");
    var prepared = await PrepareAsync("manual", conversationId);
    var runId = prepared["runId"]!.GetValue<string>();
    var checkpointPath = Path.Combine(
      _environment.DataDirectory,
      "workspaces",
      workspaceId,
      "supervision",
      conversationId,
      $"{runId}.json"
    );
    var checkpoint = JsonNode.Parse(await File.ReadAllTextAsync(checkpointPath))!.AsObject();
    checkpoint["schemaVersion"] = 4;
    checkpoint["runtime"]!.AsObject().Remove("telemetry");
    foreach (var item in checkpoint["runtime"]!["workItems"]!.AsArray())
    {
      item!.AsObject().Remove("rejectionReason");
      item.AsObject().Remove("retryReason");
    }
    foreach (var item in checkpoint["events"]!.AsArray())
    {
      item!.AsObject().Remove("rejectionReason");
      item.AsObject().Remove("retryReason");
      item.AsObject().Remove("durationMilliseconds");
    }
    checkpoint["integritySha256"] = string.Empty;
    var canonical = RestoreLegacyDateTimeOffsetTokens(checkpoint.ToJsonString(
      new JsonSerializerOptions(JsonSerializerDefaults.Web)
    ));
    checkpoint["integritySha256"] = Convert.ToHexString(
      SHA256.HashData(Encoding.UTF8.GetBytes(canonical))
    ).ToLowerInvariant();
    var persisted = RestoreLegacyDateTimeOffsetTokens(checkpoint.ToJsonString(
      new JsonSerializerOptions(JsonSerializerDefaults.Web)
      {
        WriteIndented = true
      }
    ));
    StringAssert.Contains(persisted, "+00:00");
    await _environment.RestartApplicationAsync(
      () => File.WriteAllTextAsync(checkpointPath, persisted)
    );

    using var restoredResponse = await _environment.HttpClient.GetAsync(
      $"api/supervision/runs/{runId}"
    );
    Assert.AreEqual(
      HttpStatusCode.OK,
      restoredResponse.StatusCode,
      _environment.ApiOutput
    );
    var restored = JsonNode.Parse(
      await restoredResponse.Content.ReadAsStringAsync()
    )!.AsObject();
    Assert.AreEqual("interrupted-recoverable", restored["state"]!.GetValue<string>());
    Assert.IsNotNull(restored["runtime"]!["telemetry"]);
    Assert.AreEqual(0, restored["runtime"]!["telemetry"]!["workerAttemptCount"]!.GetValue<int>());
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

  private static async Task<string> StartNativeSupervisionAsync(string objective)
  {
    using var prepareResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/supervision/runs/prepare",
      new
      {
        objective,
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
    return runId;
  }

  private static string RestoreLegacyDateTimeOffsetTokens(string json)
  {
    return Regex.Replace(
      json,
      "(\\\"(?:timestamp|createdAt|updatedAt|preparedAt|capturedAt)\\\"\\s*:\\s*\\\"[^\\\"]*)\\\\u002B([^\\\"]*\\\")",
      "$1+$2",
      RegexOptions.CultureInvariant
    );
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

  private static async Task<JsonObject> WaitForStateAsync(
    string runId,
    string expectedState,
    TimeSpan timeout
  )
  {
    var deadline = DateTimeOffset.UtcNow.Add(timeout);
    JsonObject run;
    do
    {
      run = await GetRunAsync(runId);
      if (string.Equals(
        run["state"]!.GetValue<string>(),
        expectedState,
        StringComparison.Ordinal
      ))
      {
        return run;
      }
      await Task.Delay(100);
    } while (DateTimeOffset.UtcNow < deadline);
    Assert.Fail(
      $"Run {runId} did not reach state {expectedState}. Last view: {run}"
    );
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
