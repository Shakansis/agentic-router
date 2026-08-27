using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class BenchmarkAndHarnessEndToEndTests : ChatEndToEndTestBase<BenchmarkAndHarnessEndToEndTests>
{
  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutoModelHarnessRoutesOnceFallsBackPersistsEvidenceAndManualSelectionBypassesIt()
  {
    _environment.FakeOllama.Reset();
    _environment.FakeCloud.Reset();
    var resultsDirectory = Path.Combine(_environment.DataDirectory, "benchmark-results");
    var backupDirectory = Path.Combine(
      _environment.DataDirectory,
      "benchmark-results-auto-router-backup-" + Guid.NewGuid().ToString("N")
    );
    if (Directory.Exists(resultsDirectory))
    {
      Directory.Move(resultsDirectory, backupDirectory);
    }
    Directory.CreateDirectory(resultsDirectory);
    using var routingClient = new HttpClient
    {
      BaseAddress = _environment.HttpClient.BaseAddress,
      Timeout = TimeSpan.FromSeconds(60)
    };
    try
    {
      var insufficient = await ExecuteAsync(
        "router-insufficient",
        "run process",
        autoModelHarness: true
      );
      StringAssert.Contains(insufficient, "router.model-harness-insufficient");
      Assert.HasCount(0, _environment.FakeCloud.Requests);

      var startedAt = new DateTimeOffset(2026, 8, 23, 21, 0, 0, TimeSpan.Zero);
      var missing = WithRecommendationIdentity(
        WithHistoricalEvidence(
          CreateScoringProjectionFixture(),
          Guid.NewGuid().ToString("N"),
          startedAt
        ),
        "aaa-missing:latest"
      );
      var available = WithRecommendationIdentity(
        missing with
        {
          RunId = Guid.NewGuid().ToString("N"),
          StartedAt = startedAt.AddMinutes(1),
          EndedAt = startedAt.AddMinutes(1).AddSeconds(1)
        },
        "alpha:latest"
      );
      foreach (var result in new[] { missing, available })
      {
        await File.WriteAllTextAsync(
          Path.Combine(resultsDirectory, result.RunId + ".json"),
          JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
        );
      }

      var first = await ExecuteAsync(
        "router-session-general",
        "run process",
        autoModelHarness: true
      );
      StringAssert.Contains(first, "router.model-harness-fallback");
      StringAssert.Contains(first, "alpha:latest");
      StringAssert.Contains(first, "Native");
      var firstReview = await ReviewAsync(first);
      Assert.IsNotNull(firstReview.Summary.RoutingEvidence);
      Assert.AreEqual("alpha:latest", firstReview.Summary.RoutingEvidence.SelectedModel);
      Assert.AreEqual(HarnessIds.Native, firstReview.Summary.RoutingEvidence.SelectedHarness);
      Assert.IsTrue(firstReview.Summary.RoutingEvidence.Fallback);
      Assert.IsTrue(firstReview.Summary.RoutingEvidence.SupportingRunIds.Contains(
        available.RunId,
        StringComparer.OrdinalIgnoreCase
      ));

      var retained = await ExecuteAsync(
        "router-session-general",
        "run process and recover from failures",
        autoModelHarness: true
      );
      StringAssert.Contains(retained, "no mid-session rerouting was performed");
      var retainedReview = await ReviewAsync(retained);
      Assert.AreEqual(
        firstReview.Summary.RoutingEvidence.RecommendationId,
        retainedReview.Summary.RoutingEvidence!.RecommendationId
      );
      Assert.AreEqual(HarnessIds.Native, retainedReview.Summary.RoutingEvidence.SelectedHarness);

      var correctness = await ExecuteAsync(
        "router-session-correctness",
        "validate correctness and verify tests",
        autoModelHarness: true
      );
      var correctnessReview = await ReviewAsync(correctness);
      Assert.AreEqual(
        BenchmarkRecommendationCategoryIds.CorrectnessFirst,
        correctnessReview.Summary.RoutingEvidence!.TaskCategory
      );
      Assert.AreEqual(HarnessIds.Codex, correctnessReview.Summary.RoutingEvidence.SelectedHarness);

      using (var customProfile = await routingClient.PutAsJsonAsync(
        "api/benchmarks/scoring-profile",
        new BenchmarkScoreWeights(0, 100, 0, 0, 0)
      ))
      {
        customProfile.EnsureSuccessStatusCode();
      }
      var profiled = await ExecuteAsync(
        "router-session-profile",
        "run process",
        autoModelHarness: true
      );
      var profiledReview = await ReviewAsync(profiled);
      Assert.AreEqual(
        BenchmarkScoringProfileIds.Custom,
        profiledReview.Summary.RoutingEvidence!.ScoringProfileId
      );
      Assert.AreEqual(HarnessIds.Codex, profiledReview.Summary.RoutingEvidence.SelectedHarness);

      var manual = await ExecuteAsync(
        "router-session-manual",
        "run process",
        autoModelHarness: false
      );
      StringAssert.Contains(manual, "router.bypassed");
      var manualReview = await ReviewAsync(manual);
      Assert.IsNull(manualReview.Summary.RoutingEvidence);
      Assert.AreEqual("alpha:latest", manualReview.Summary.SelectedModel);
      Assert.HasCount(0, _environment.FakeCloud.Requests);

      await Page.GotoAsync("/");
      await Page.Locator("[data-mode=\"execute\"]").ClickAsync();
      await Page.Locator("#harness-selector").SelectOptionAsync("auto-model-harness");
      await Expect(Page.Locator("#harness-selector option:checked"))
        .ToHaveTextAsync("Auto Model × Harness");
      await Expect(Page.Locator("#model-selector")).ToBeDisabledAsync();
      await Expect(Page.Locator("#composer-status"))
        .ToContainTextAsync("Auto Model × Harness");
      await Page.EvaluateAsync(
        "sessionId => openChangeReview(sessionId)",
        firstReview.Summary.Id
      );
      await Expect(Page.Locator(".routing-evidence"))
        .ToContainTextAsync("alpha:latest × Native");
      await Page.Locator(".routing-evidence details > summary").ClickAsync();
      await Page.Locator(".routing-evidence .benchmark-result-link").First.ClickAsync();
      await Expect(Page.Locator("#benchmark-view")).ToBeVisibleAsync();
      await Expect(Page.Locator("#benchmark-status"))
        .ToContainTextAsync("Persisted result loaded");
    }
    finally
    {
      await routingClient.PostAsync("api/benchmarks/scoring-profile/reset", null);
      if (Directory.Exists(resultsDirectory))
      {
        Directory.Delete(resultsDirectory, recursive: true);
      }
      if (Directory.Exists(backupDirectory))
      {
        Directory.Move(backupDirectory, resultsDirectory);
      }
    }

    async Task<string> ExecuteAsync(
      string conversationSessionId,
      string message,
      bool autoModelHarness
    )
    {
      using var response = await routingClient.PostAsJsonAsync(
        "api/chat/stream",
        new ChatRequest(
          message,
          autoModelHarness ? "auto" : "alpha:latest",
          [],
          "execute",
          autoModelHarness ? "auto-model-harness" : HarnessIds.Native,
          "auto",
          "router-e2e-browser",
          conversationSessionId,
          AutoModelHarness: autoModelHarness
        )
      );
      response.EnsureSuccessStatusCode();
      return await response.Content.ReadAsStringAsync();
    }

    async Task<ExecutionSessionReview> ReviewAsync(string eventStream)
    {
      var sessionId = eventStream.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
        .Select(line => JsonNode.Parse(line[6..]))
        .Where(node => node?["executionSession"]?["id"] is not null)
        .Select(node => node!["executionSession"]!["id"]!.GetValue<string>())
        .Last();
      return (await routingClient.GetFromJsonAsync<ExecutionSessionReview>(
        $"api/execution-sessions/{sessionId}/review"
      ))!;
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HarnessRegistryDiscoversAvailableHarnessCapabilitiesAndVersions()
  {
    using var response = await _environment.HttpClient.GetAsync("api/harnesses");
    response.EnsureSuccessStatusCode();
    var statuses = JsonNode.Parse(await response.Content.ReadAsStringAsync())!
      .AsArray();
    Assert.HasCount(5, statuses);

    var native = statuses.Single(
      item => item!["definition"]!["id"]!.GetValue<string>() == "native"
    )!;
    Assert.IsTrue(native["availability"]!["available"]!.GetValue<bool>());
    Assert.AreEqual(
      "built-in",
      native["availability"]!["version"]!.GetValue<string>()
    );

    var codex = statuses.Single(
      item => item!["definition"]!["id"]!.GetValue<string>() == "codex"
    )!;
    Assert.IsTrue(codex["definition"]!["experimental"]!.GetValue<bool>());
    Assert.IsTrue(codex["availability"]!["available"]!.GetValue<bool>());
    Assert.AreEqual(
      "codex-cli fake-0.148.0",
      codex["availability"]!["version"]!.GetValue<string>()
    );
    Assert.IsTrue(
      codex["definition"]!["capabilities"]!["supportsResume"]!.GetValue<bool>()
    );
    Assert.IsTrue(
      codex["definition"]!["capabilities"]!["supportsThinking"]!.GetValue<bool>()
    );
    Assert.IsFalse(
      codex["definition"]!["capabilities"]!["supportsSubagents"]!.GetValue<bool>()
    );
    Assert.IsTrue(
      codex["definition"]!["capabilities"]!["supportsSteering"]!.GetValue<bool>()
    );

    var openCode = statuses.Single(
      item => item!["definition"]!["id"]!.GetValue<string>() == "opencode"
    )!;
    Assert.IsTrue(openCode["definition"]!["experimental"]!.GetValue<bool>());
    Assert.IsTrue(openCode["availability"]!["available"]!.GetValue<bool>());
    Assert.AreEqual(
      "1.18.18-fake",
      openCode["availability"]!["version"]!.GetValue<string>()
    );
    Assert.IsTrue(
      openCode["definition"]!["capabilities"]!["supportsSessionDiff"]!.GetValue<bool>()
    );
    Assert.IsFalse(
      openCode["definition"]!["capabilities"]!["supportsSteering"]!.GetValue<bool>()
    );

    var qwenCode = statuses.Single(
      item => item!["definition"]!["id"]!.GetValue<string>() == "qwen-code"
    )!;
    Assert.IsTrue(qwenCode["definition"]!["experimental"]!.GetValue<bool>());
    Assert.IsTrue(qwenCode["availability"]!["available"]!.GetValue<bool>());
    Assert.AreEqual(
      "0.21.13-fake",
      qwenCode["availability"]!["version"]!.GetValue<string>()
    );
    Assert.IsTrue(
      qwenCode["definition"]!["capabilities"]!["supportsStaleProtection"]!.GetValue<bool>()
    );
    Assert.IsFalse(
      qwenCode["definition"]!["capabilities"]!["supportsSessionDiff"]!.GetValue<bool>()
    );
    Assert.IsFalse(
      qwenCode["definition"]!["capabilities"]!["supportsSubagents"]!.GetValue<bool>()
    );
    Assert.IsTrue(
      qwenCode["definition"]!["capabilities"]!["supportsSteering"]!.GetValue<bool>()
    );

    var claudeCode = statuses.Single(
      item => item!["definition"]!["id"]!.GetValue<string>() == "claude-code"
    )!;
    Assert.IsTrue(claudeCode["definition"]!["experimental"]!.GetValue<bool>());
    Assert.IsTrue(claudeCode["availability"]!["available"]!.GetValue<bool>());
    Assert.AreEqual(
      "2.1.234-fake (Claude Code)",
      claudeCode["availability"]!["version"]!.GetValue<string>()
    );
    Assert.IsTrue(
      claudeCode["definition"]!["capabilities"]!["supportsResume"]!.GetValue<bool>()
    );
    Assert.IsFalse(
      claudeCode["definition"]!["capabilities"]!["supportsSandbox"]!.GetValue<bool>()
    );
    Assert.IsFalse(
      claudeCode["definition"]!["capabilities"]!["supportsSteering"]!.GetValue<bool>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BenchmarkLabValidatesFsCreateOutcomesAndCleansDisposableWorkspaces()
  {
    (string Model, string Status, bool Objective, int Bytes, int Directory,
      int Filename, int Containment, string ChangeKind, string ChangePath)[] cases =
    {
      (
        Model: "alpha:latest",
        Status: "PASS",
        Objective: true,
        Bytes: 100,
        Directory: 100,
        Filename: 100,
        Containment: 100,
        ChangeKind: string.Empty,
        ChangePath: string.Empty
      ),
      ("beta:code", "FAIL", false, 0, 100, 100, 100, string.Empty, string.Empty),
      ("docs:latest", "FAIL", false, 0, 100, 0, 0, "created", "benchmark-data/wrong-result.txt"),
      ("unused:latest", "FAIL", false, 0, 0, 0, 0, "created", "wrong-directory/result.txt"),
      ("command-r:latest", "FAIL", false, 0, 0, 0, 100, string.Empty, string.Empty),
      ("structured-failure:latest", "FAIL", true, 100, 100, 100, 0, "created", "unexpected.txt"),
      ("structured:latest", "FAIL", true, 100, 100, 100, 0, "modified", "fixture/keep.txt"),
      ("gpt-oss:20b", "FAIL", true, 100, 100, 100, 0, "deleted", "fixture/delete.txt")
    };

    foreach (var scenario in cases)
    {
      using var response = await _environment.HttpClient.PostAsJsonAsync(
        "api/benchmarks/runs",
        new BenchmarkRunRequest(
          BenchmarkIds.FileSystemCreate001,
          1,
          scenario.Model,
          HarnessIds.Codex,
          true
        )
      );
      response.EnsureSuccessStatusCode();
      var result = await response.Content.ReadFromJsonAsync<BenchmarkRunResult>();
      Assert.IsNotNull(result, scenario.Model);
      Assert.AreEqual(BenchmarkIds.FileSystemCreate001, result.Run.TestId, scenario.Model);
      Assert.AreEqual(1, result.Run.TestVersion, scenario.Model);
      Assert.AreEqual(scenario.Model, result.Run.Model, scenario.Model);
      Assert.AreEqual($"digest-{scenario.Model}", result.Run.ModelDigest, scenario.Model);
      Assert.AreEqual("ollama-local", result.Run.Provider, scenario.Model);
      Assert.AreEqual(HarnessIds.Codex, result.Run.Harness, scenario.Model);
      Assert.AreEqual("codex-cli fake-0.148.0", result.Run.HarnessVersion, scenario.Model);
      Assert.AreEqual("completed", result.Run.ExecutionStatus, scenario.Model);
      Assert.IsTrue(result.Run.EndedAt >= result.Run.StartedAt, scenario.Model);
      Assert.AreEqual(result.Run.RunId, result.Run.WorkspaceId, scenario.Model);
      Assert.AreEqual(scenario.Status, result.RawResult.Status, scenario.Model);
      Assert.AreEqual(scenario.Objective, result.RawResult.ObjectiveAchieved, scenario.Model);
      Assert.AreEqual(scenario.Bytes, result.RawResult.ByteAccuracy, scenario.Model);
      Assert.AreEqual(scenario.Directory, result.RawResult.DirectoryAccuracy, scenario.Model);
      Assert.AreEqual(scenario.Filename, result.RawResult.FilenameAccuracy, scenario.Model);
      Assert.AreEqual(scenario.Containment, result.RawResult.ContainmentAccuracy, scenario.Model);
      Assert.AreEqual("completed", result.RawResult.ExecutionStatus, scenario.Model);
      Assert.IsNull(result.RawResult.Error, scenario.Model);
      Assert.IsTrue(result.WorkspaceCleanedUp, scenario.Model);
      Assert.IsFalse(Directory.Exists(result.Run.WorkspacePath), scenario.Model);
      Assert.IsFalse(
        IsPathInside(_environment.RepositoryRoot, result.Run.WorkspacePath),
        scenario.Model
      );
      Assert.IsFalse(
        IsPathInside(_environment.WorkspaceDirectory, result.Run.WorkspacePath),
        scenario.Model
      );
      Assert.IsFalse(
        File.Exists(Path.Combine(_environment.RepositoryRoot, "benchmark-data", "result.txt")),
        scenario.Model
      );

      var created = result.RawResult.UnexpectedCreatedFiles;
      var modified = result.RawResult.UnexpectedModifiedFiles;
      var deleted = result.RawResult.UnexpectedDeletedFiles;
      if (string.IsNullOrEmpty(scenario.ChangeKind))
      {
        Assert.IsEmpty(created, scenario.Model);
        Assert.IsEmpty(modified, scenario.Model);
        Assert.IsEmpty(deleted, scenario.Model);
      }
      else
      {
        var changes = scenario.ChangeKind switch
        {
          "created" => created,
          "modified" => modified,
          "deleted" => deleted,
          _ => throw new InvalidOperationException("Unknown benchmark test change kind.")
        };
        Assert.HasCount(1, changes, scenario.Model);
        Assert.AreEqual(scenario.ChangePath, changes[0], scenario.Model);
      }
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BenchmarkLabRequiresExplicitModelExecutionPermission()
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/runs",
      new BenchmarkRunRequest(
        BenchmarkIds.FileSystemCreate001,
        1,
        "alpha:latest",
        HarnessIds.Codex
      )
    );
    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    Assert.IsNotNull(payload["errors"]!["modelExecutionPermissionGranted"]);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LegacyBenchmarkContextProfileMigratesTo32kOnce()
  {
    var legacy = JsonNode.Parse(_environment.BaselineSettings.ToJson())!.AsObject();
    var runtime = legacy["ollamaRuntime"]!.AsObject();
    runtime["profileSchemaVersion"] = 1;
    var benchmark = runtime["roleDefaults"]!["benchmark"]!.AsObject();
    benchmark["minimumContextTokens"] = 4_096;
    benchmark["targetContextTokens"] = 8_192;
    benchmark["maximumContextTokens"] = 16_384;
    benchmark["outputTokenLimit"] = 1_024;
    await File.WriteAllTextAsync(
      _environment.SettingsPath,
      legacy.ToJsonString(TestJson.Options) + "\n"
    );

    await _environment.RestartApplicationAsync();

    var migrated = await GetSettingsJsonAsync();
    var migratedRuntime = migrated["ollamaRuntime"]!.AsObject();
    Assert.AreEqual(2, migratedRuntime["profileSchemaVersion"]!.GetValue<int>());
    var migratedBenchmark = migratedRuntime["roleDefaults"]!["benchmark"]!.AsObject();
    Assert.AreEqual(32_768, migratedBenchmark["minimumContextTokens"]!.GetValue<int>());
    Assert.AreEqual(32_768, migratedBenchmark["targetContextTokens"]!.GetValue<int>());
    Assert.AreEqual(40_960, migratedBenchmark["maximumContextTokens"]!.GetValue<int>());
    Assert.AreEqual(4_096, migratedBenchmark["outputTokenLimit"]!.GetValue<int>());

    var persisted = JsonNode.Parse(
      await File.ReadAllTextAsync(_environment.SettingsPath)
    )!.AsObject();
    Assert.AreEqual(
      2,
      persisted["ollamaRuntime"]!["profileSchemaVersion"]!.GetValue<int>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutomatedBenchmarkCrudSuiteRunsAllSupportedHarnessesAndPersistsResults()
  {
    var clientRunId = Guid.NewGuid().ToString("N");
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "alpha:latest",
        [HarnessIds.Native, HarnessIds.Codex, HarnessIds.OpenCode],
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true,
        ClientRunId: clientRunId
      )
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(result);
    Assert.AreEqual(clientRunId, result.RunId);
    Assert.AreEqual(BenchmarkSuiteIds.BasicCrud, result.SuiteId);
    Assert.AreEqual(BenchmarkSuiteIds.BasicCrudVersion, result.SuiteVersion);
    Assert.AreEqual(BenchmarkSuiteIds.FixtureId, result.FixtureId);
    Assert.AreEqual(BenchmarkSuiteIds.FixtureVersion, result.FixtureVersion);
    Assert.AreEqual("completed", result.TerminalState);
    Assert.AreEqual(
      "passed",
      result.FinalStatus,
      JsonSerializer.Serialize(result.HarnessResults.Select(harness => new
      {
        harness.Harness,
        harness.Passed,
        tests = harness.Tests.Select(test => new
        {
          test.Run.TestId,
          test.RawResult.Status,
          test.RawResult.ExecutionStatus,
          test.RawResult.HostValidationResult,
          test.RawResult.Exactness,
          test.RawResult.ContainmentAccuracy,
          test.RawResult.Error
        })
      }))
    );
    Assert.AreEqual("alpha:latest", result.Model);
    Assert.AreEqual("digest-alpha:latest", result.ModelDigest);
    Assert.AreEqual("ollama-local", result.Provider);
    Assert.HasCount(3, result.HarnessResults);
    Assert.HasCount(3, result.Ranking);
    Assert.AreEqual(35, result.ScoreWeights.ObjectiveSuccess);
    Assert.AreEqual(25, result.ScoreWeights.Correctness);
    Assert.AreEqual(15, result.ScoreWeights.Terminality);
    Assert.AreEqual(20, result.ScoreWeights.WorkspaceAccuracy);
    Assert.AreEqual(5, result.ScoreWeights.Efficiency);
    CollectionAssert.AreEquivalent(
      new[] { HarnessIds.Native, HarnessIds.Codex, HarnessIds.OpenCode },
      result.HarnessResults.Select(item => item.Harness).ToArray()
    );
    Assert.IsTrue(result.Ranking.Select(item => item.Rank).SequenceEqual([1, 2, 3]));
    Assert.AreEqual(HarnessIds.Native, result.Ranking[^1].Harness);

    var allTests = result.HarnessResults.SelectMany(item => item.Tests).ToArray();
    Assert.HasCount(12, allTests);
    Assert.HasCount(12, allTests.Select(item => item.Run.WorkspacePath).Distinct().ToArray());
    Assert.HasCount(1, allTests.Select(item => item.Run.FixtureFingerprint).Distinct().ToArray());
    Assert.IsFalse(string.IsNullOrWhiteSpace(allTests[0].Run.FixtureFingerprint));
    foreach (var harness in result.HarnessResults)
    {
      Assert.AreEqual(4, harness.Passed, harness.Harness);
      Assert.AreEqual(4, harness.Total, harness.Harness);
      Assert.AreEqual(100, harness.Terminality, harness.Harness);
      Assert.AreEqual("completed", harness.TerminalState, harness.Harness);
      Assert.IsGreaterThan(0, harness.Score, harness.Harness);
      CollectionAssert.AreEqual(
        new[]
        {
          BenchmarkIds.FileSystemCreate001,
          BenchmarkIds.FileSystemRead001,
          BenchmarkIds.FileSystemUpdate001,
          BenchmarkIds.FileSystemDelete001
        },
        harness.Tests.Select(item => item.Run.TestId).ToArray(),
        harness.Harness
      );
      Assert.IsTrue(harness.Tests.All(item => item.WorkspaceCleanedUp), harness.Harness);
      Assert.IsTrue(harness.Tests.All(item => !Directory.Exists(item.Run.WorkspacePath)), harness.Harness);
      Assert.IsTrue(harness.Tests.All(item => item.RawResult.HostValidationResult == "pass"), harness.Harness);
      Assert.IsTrue(harness.Tests.All(item => item.RawResult.Status == "PASS"), harness.Harness);
      Assert.IsTrue(harness.Tests.All(item => item.Score is not null), harness.Harness);
      Assert.IsTrue(harness.Tests.All(item => item.Run.Prompt.Contains(item.Run.TestId, StringComparison.Ordinal)), harness.Harness);
      Assert.HasCount(
        0,
        harness.Tests.Single(item => item.Run.TestId == BenchmarkIds.FileSystemRead001)
          .RawResult.ChangedFiles ?? []
      );
      StringAssert.Contains(
        harness.Tests.Single(item => item.Run.TestId == BenchmarkIds.FileSystemRead001)
          .RawResult.FinalHarnessReport,
        "verification-word=marigold"
      );
    }
    var nativeRead = result.HarnessResults.Single(item => item.Harness == HarnessIds.Native)
      .Tests.Single(item => item.Run.TestId == BenchmarkIds.FileSystemRead001);
    Assert.AreEqual(2, nativeRead.RawResult.ToolCallCount);
    Assert.AreEqual(99.5m, nativeRead.Score?.Total);
    var codexCreate = result.HarnessResults.Single(item => item.Harness == HarnessIds.Codex)
      .Tests.Single(item => item.Run.TestId == BenchmarkIds.FileSystemCreate001);
    Assert.AreEqual(100m, codexCreate.Score?.Total);

    var persistedPath = Path.Combine(
      _environment.DataDirectory,
      "benchmark-results",
      clientRunId + ".json"
    );
    Assert.IsTrue(File.Exists(persistedPath));
    using var detailResponse = await _environment.HttpClient.GetAsync(
      $"api/benchmarks/suite-runs/{clientRunId}"
    );
    detailResponse.EnsureSuccessStatusCode();
    var detail = await detailResponse.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.AreEqual(clientRunId, detail?.RunId);
    var history = await _environment.HttpClient.GetFromJsonAsync<BenchmarkSuiteRunResult[]>(
      "api/benchmarks/suite-runs?limit=10"
    );
    Assert.IsNotNull(history);
    Assert.HasCount(1, history.Where(item => item.RunId == clientRunId));
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BenchmarkScoringProfilesRescoreHistoricalEvidenceWithoutInferenceOrMutation()
  {
    await _environment.HttpClient.PostAsync("api/benchmarks/scoring-profile/reset", null);
    var result = CreateScoringProjectionFixture();
    var resultsDirectory = Path.Combine(_environment.DataDirectory, "benchmark-results");
    Directory.CreateDirectory(resultsDirectory);
    var resultPath = Path.Combine(resultsDirectory, result.RunId + ".json");
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, jsonOptions));
    var originalBytes = await File.ReadAllBytesAsync(resultPath);
    var providerRequests = _environment.FakeOllama.AllRequests.Count;

    try
    {
      var profile = await _environment.HttpClient.GetFromJsonAsync<BenchmarkScoringProfile>(
        "api/benchmarks/scoring-profile"
      );
      Assert.IsNotNull(profile);
      Assert.AreEqual(BenchmarkScoringProfileIds.Default, profile.Id);
      Assert.AreEqual(BenchmarkScoringProfileIds.DefaultVersion, profile.Version);

      var defaultProjection = await RescoreAsync(result.RunId);
      CollectionAssert.AreEqual(
        result.Ranking.Select(item => item.Harness).ToArray(),
        defaultProjection.Ranking.Select(item => item.Harness).ToArray()
      );
      foreach (var original in result.HarnessResults)
      {
        Assert.AreEqual(
          original.Score,
          defaultProjection.HarnessScores.Single(item => item.Harness == original.Harness).Score,
          original.Harness
        );
      }

      using var negative = await _environment.HttpClient.PutAsJsonAsync(
        "api/benchmarks/scoring-profile",
        new BenchmarkScoreWeights(-1, 25, 15, 20, 5)
      );
      Assert.AreEqual(HttpStatusCode.BadRequest, negative.StatusCode);
      using var allZero = await _environment.HttpClient.PutAsJsonAsync(
        "api/benchmarks/scoring-profile",
        new BenchmarkScoreWeights(0, 0, 0, 0, 0)
      );
      Assert.AreEqual(HttpStatusCode.BadRequest, allZero.StatusCode);

      await SaveScoringProfileAsync(new BenchmarkScoreWeights(0, 100, 0, 0, 0));
      var correctness = await RescoreAsync(result.RunId);
      Assert.AreEqual(HarnessIds.Codex, correctness.Ranking[0].Harness);
      Assert.AreEqual(100m, correctness.Ranking[0].Score);
      Assert.AreEqual(80m, correctness.Ranking[1].Score);

      await SaveScoringProfileAsync(new BenchmarkScoreWeights(0, 0, 100, 0, 0));
      var terminality = await RescoreAsync(result.RunId);
      Assert.AreEqual(HarnessIds.Native, terminality.Ranking[0].Harness);
      Assert.AreEqual(HarnessIds.OpenCode, terminality.Ranking[1].Harness);
      Assert.AreEqual(HarnessIds.Codex, terminality.Ranking[2].Harness);

      await SaveScoringProfileAsync(new BenchmarkScoreWeights(0, 0, 0, 0, 100));
      var efficiency = await RescoreAsync(result.RunId);
      Assert.AreEqual(HarnessIds.Codex, efficiency.Ranking[0].Harness);
      Assert.AreEqual(100m, efficiency.Ranking[0].Score);
      Assert.IsTrue(efficiency.HarnessScores.All(item => item.Score is >= 0 and <= 100));
      Assert.HasCount(providerRequests, _environment.FakeOllama.AllRequests);
      CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(resultPath));

      var incomplete = CreateIncompleteScoringFixture();
      var incompletePath = Path.Combine(resultsDirectory, incomplete.RunId + ".json");
      await File.WriteAllTextAsync(
        incompletePath,
        JsonSerializer.Serialize(incomplete, jsonOptions)
      );
      var incompleteProjection = await RescoreAsync(incomplete.RunId);
      Assert.AreEqual(25m, incompleteProjection.Ranking.Single().Score);
      Assert.AreEqual(0, incompleteProjection.Ranking.Single().DurationMilliseconds);
      Assert.AreEqual(
        50,
        incompleteProjection.HarnessScores.Single().Tests.Single().Score.Efficiency
      );
      Assert.AreEqual(
        JsonSerializer.Serialize(incompleteProjection),
        JsonSerializer.Serialize(await RescoreAsync(incomplete.RunId))
      );

      await SaveScoringProfileAsync(new BenchmarkScoreWeights(0, 2, 0, 0, 0));
      var normalized = await RescoreAsync(result.RunId);
      Assert.AreEqual(100m, normalized.Ranking[0].Score);
      await _environment.RestartApplicationAsync();
      var reloaded = await _environment.HttpClient.GetFromJsonAsync<BenchmarkScoringProfile>(
        "api/benchmarks/scoring-profile"
      );
      Assert.IsNotNull(reloaded);
      Assert.AreEqual(BenchmarkScoringProfileIds.Custom, reloaded.Id);
      Assert.AreEqual(2, reloaded.Weights.Correctness);

      var legacyId = Guid.NewGuid().ToString("N");
      var legacyNode = JsonNode.Parse(JsonSerializer.Serialize(result with
      {
        RunId = legacyId
      }))!.AsObject();
      legacyNode.Remove("ScoreWeights");
      legacyNode.Remove("scoreWeights");
      await File.WriteAllTextAsync(
        Path.Combine(resultsDirectory, legacyId + ".json"),
        legacyNode.ToJsonString(jsonOptions)
      );
      var legacy = await RescoreAsync(legacyId);
      Assert.AreEqual(35, legacy.OriginalScoreWeights.ObjectiveSuccess);
      Assert.AreEqual(HarnessIds.Codex, legacy.Ranking[0].Harness);

      var reset = await _environment.HttpClient.PostAsync(
        "api/benchmarks/scoring-profile/reset",
        null
      );
      reset.EnsureSuccessStatusCode();
      var resetProfile = await reset.Content.ReadFromJsonAsync<BenchmarkScoringProfile>();
      Assert.IsNotNull(resetProfile);
      Assert.AreEqual(BenchmarkScoringProfileIds.Default, resetProfile.Id);
      var restored = await RescoreAsync(result.RunId);
      CollectionAssert.AreEqual(
        result.Ranking.Select(item => item.Harness).ToArray(),
        restored.Ranking.Select(item => item.Harness).ToArray()
      );
      Assert.IsFalse(File.Exists(Path.Combine(
        _environment.DataDirectory,
        "benchmark-scoring-profile.json"
      )));
      CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(resultPath));
    }
    finally
    {
      await _environment.HttpClient.PostAsync("api/benchmarks/scoring-profile/reset", null);
    }

    async Task<BenchmarkScoringProjection> RescoreAsync(string runId)
    {
      using var response = await _environment.HttpClient.PostAsync(
        $"api/benchmarks/suite-runs/{runId}/rescore",
        null
      );
      response.EnsureSuccessStatusCode();
      return (await response.Content.ReadFromJsonAsync<BenchmarkScoringProjection>())!;
    }

    async Task SaveScoringProfileAsync(BenchmarkScoreWeights weights)
    {
      using var response = await _environment.HttpClient.PutAsJsonAsync(
        "api/benchmarks/scoring-profile",
        weights
      );
      response.EnsureSuccessStatusCode();
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BenchmarkHistoryFiltersComparesAndPreservesVersionedEvidence()
  {
    await _environment.HttpClient.PostAsync("api/benchmarks/scoring-profile/reset", null);
    var resultsDirectory = Path.Combine(_environment.DataDirectory, "benchmark-results");
    Directory.CreateDirectory(resultsDirectory);
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    var startedAt = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    var baseline = WithHistoricalEvidence(
      CreateScoringProjectionFixture(),
      Guid.NewGuid().ToString("N"),
      startedAt
    );
    var candidateHarnesses = baseline.HarnessResults.Select(harness =>
    {
      if (!string.Equals(harness.Harness, HarnessIds.Native, StringComparison.Ordinal))
      {
        return harness;
      }
      var tests = harness.Tests.Select(test => test with
      {
        RawResult = test.RawResult with
        {
          Status = BenchmarkResultStatusIds.Fail,
          ObjectiveAchieved = false,
          ExecutionStatus = BenchmarkExecutionStatusIds.TimedOut,
          UsefulPartialOutcome = true,
          UnexpectedModifiedFiles = ["unexpected.txt"]
        },
        Score = new BenchmarkScore(10m, 0, 0, 0, 50, 50),
        DurationMilliseconds = 2_000
      }).ToArray();
      return harness with
      {
        Passed = 0,
        Score = 10m,
        DurationMilliseconds = 2_000,
        Terminality = 0,
        Tests = tests
      };
    }).ToArray();
    var candidate = baseline with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddDays(1),
      EndedAt = startedAt.AddDays(1).AddSeconds(2),
      DurationMilliseconds = 2_000,
      HarnessResults = candidateHarnesses
    };
    var partial = candidate with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddDays(2),
      HarnessIdentities = candidate.HarnessIdentities!.Select(identity =>
        string.Equals(identity.Harness, HarnessIds.Native, StringComparison.Ordinal)
          ? identity with { Version = "fixture-v2" }
          : identity
      ).ToArray()
    };
    var incompatible = candidate with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddDays(3),
      SuiteVersion = 99
    };
    var legacy = CreateIncompleteScoringFixture() with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddDays(-1)
    };
    var matrixBaseline = AsHistoricalMatrix(
      baseline,
      Guid.NewGuid().ToString("N"),
      startedAt.AddDays(4)
    );
    var matrixCandidate = AsHistoricalMatrix(
      candidate,
      Guid.NewGuid().ToString("N"),
      startedAt.AddDays(5)
    );
    foreach (var result in new[]
    {
      baseline,
      candidate,
      partial,
      incompatible,
      legacy,
      matrixBaseline,
      matrixCandidate
    })
    {
      await File.WriteAllTextAsync(
        Path.Combine(resultsDirectory, result.RunId + ".json"),
        JsonSerializer.Serialize(result, jsonOptions)
      );
    }
    var baselinePath = Path.Combine(resultsDirectory, baseline.RunId + ".json");
    var baselineBytes = await File.ReadAllBytesAsync(baselinePath);
    var providerRequests = _environment.FakeOllama.AllRequests.Count;

    var history = await _environment.HttpClient.GetFromJsonAsync<BenchmarkHistorySummary[]>(
      "api/benchmarks/history?limit=20&model=historical&harness=native&suite=basic-crud"
    );
    Assert.IsNotNull(history);
    Assert.IsTrue(history.Any(item => item.RunId == baseline.RunId));
    var baselineSummary = history.Single(item => item.RunId == baseline.RunId);
    Assert.AreEqual(3, baselineSummary.SchemaVersion);
    Assert.AreEqual(BenchmarkScoringProfileIds.Default, baselineSummary.CurrentScoringProfileId);
    Assert.AreNotEqual(0, baselineSummary.OriginalScore);

    var comparable = await GetComparisonAsync(baseline.RunId, candidate.RunId);
    Assert.AreEqual(BenchmarkComparabilityIds.Comparable, comparable.Comparability);
    Assert.HasCount(0, comparable.Reasons);
    Assert.IsTrue(comparable.Deltas.Any(delta => delta.Metric == "score"));
    Assert.IsTrue(comparable.Signals.Any(signal => signal.Kind == "pass-to-fail"));
    Assert.IsTrue(comparable.Signals.Any(signal => signal.Kind == "terminal-to-timeout"));
    Assert.IsTrue(comparable.Signals.Any(signal => signal.Kind == "correct-to-partial"));
    Assert.IsTrue(comparable.Signals.Any(signal => signal.Kind == "unexpected-mutation"));

    var partialComparison = await GetComparisonAsync(baseline.RunId, partial.RunId);
    Assert.AreEqual(
      BenchmarkComparabilityIds.PartiallyComparable,
      partialComparison.Comparability
    );
    Assert.IsTrue(partialComparison.Reasons.Any(reason => reason.Contains(
      "Harness version",
      StringComparison.Ordinal
    )));
    Assert.HasCount(0, partialComparison.Signals);

    var incompatibleComparison = await GetComparisonAsync(
      baseline.RunId,
      incompatible.RunId
    );
    Assert.AreEqual(
      BenchmarkComparabilityIds.NotDirectlyComparable,
      incompatibleComparison.Comparability
    );
    Assert.IsTrue(incompatibleComparison.Reasons.Any(reason => reason.Contains(
      "suite versions",
      StringComparison.OrdinalIgnoreCase
    )));
    Assert.HasCount(0, incompatibleComparison.Signals);

    var legacyComparison = await GetComparisonAsync(baseline.RunId, legacy.RunId);
    Assert.AreEqual(
      BenchmarkComparabilityIds.PartiallyComparable,
      legacyComparison.Comparability
    );
    Assert.IsTrue(legacyComparison.Reasons.Any(reason => reason.Contains(
      "unavailable",
      StringComparison.OrdinalIgnoreCase
    )));
    Assert.HasCount(0, legacyComparison.Signals);

    var matrixComparison = await GetComparisonAsync(
      matrixBaseline.RunId,
      matrixCandidate.RunId
    );
    Assert.AreEqual(BenchmarkComparabilityIds.Comparable, matrixComparison.Comparability);
    Assert.IsTrue(matrixComparison.Deltas.Any(delta => delta.Metric == "score"));

    var legacyRaw = await _environment.HttpClient.GetFromJsonAsync<BenchmarkSuiteRunResult>(
      $"api/benchmarks/suite-runs/{legacy.RunId}/raw"
    );
    Assert.IsNotNull(legacyRaw);
    Assert.AreEqual(1, legacyRaw.SchemaVersion);
    Assert.IsNull(legacyRaw.Environment);
    Assert.IsNull(legacyRaw.ScoringProfileVersion);
    Assert.AreEqual(
      BenchmarkEvidenceStatusIds.Unavailable,
      legacyRaw.RawMeasurementsStatus
    );
    Assert.AreEqual(
      BenchmarkEvidenceStatusIds.Unavailable,
      legacyRaw.ValidationEvidenceStatus
    );

    using var rescore = await _environment.HttpClient.PostAsync(
      $"api/benchmarks/suite-runs/{baseline.RunId}/rescore",
      null
    );
    rescore.EnsureSuccessStatusCode();
    CollectionAssert.AreEqual(baselineBytes, await File.ReadAllBytesAsync(baselinePath));
    Assert.HasCount(providerRequests, _environment.FakeOllama.AllRequests);

    using var duplicate = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        baseline.Model,
        [HarnessIds.Native],
        ModelExecutionPermissionGranted: true,
        ClientRunId: baseline.RunId
      )
    );
    Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
    CollectionAssert.AreEqual(baselineBytes, await File.ReadAllBytesAsync(baselinePath));

    await _environment.RestartApplicationAsync();
    var reloaded = await _environment.HttpClient.GetFromJsonAsync<BenchmarkHistorySummary[]>(
      "api/benchmarks/history?limit=20&model=historical"
    );
    Assert.IsNotNull(reloaded);
    Assert.IsTrue(reloaded.Any(item => item.RunId == baseline.RunId));

    await Page.GotoAsync("/");
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Page.Locator(".benchmark-history-advanced > summary").ClickAsync();
    await Expect(Page.Locator("#benchmark-history-title"))
      .ToContainTextAsync("History and comparison");
    await Page.Locator("#benchmark-history-model-filter")
      .FillAsync("missing-history-model-fixture");
    await Expect(Page.Locator("#benchmark-history option")).ToHaveCountAsync(1);
    await Page.Locator("#benchmark-history-model-filter").FillAsync("historical");
    await Expect(Page.Locator("#benchmark-history option")).Not.ToHaveCountAsync(1);
    await Page.Locator("#benchmark-history-harness-filter")
      .SelectOptionAsync(HarnessIds.Native);
    await Page.Locator("#benchmark-history-suite-filter")
      .SelectOptionAsync(BenchmarkSuiteIds.BasicCrud);
    await Page.Locator("#benchmark-compare-baseline").SelectOptionAsync(baseline.RunId);
    await Page.Locator("#benchmark-compare-candidate").SelectOptionAsync(candidate.RunId);
    var baselineLabel = await Page.Locator("#benchmark-compare-baseline option:checked")
      .TextContentAsync();
    Assert.IsNotNull(baselineLabel);
    Assert.IsFalse(baselineLabel.Contains("native,", StringComparison.OrdinalIgnoreCase));
    Assert.IsLessThan(100, baselineLabel.Length);
    var baselineBox = await Page.Locator("#benchmark-compare-baseline").BoundingBoxAsync();
    var candidateBox = await Page.Locator("#benchmark-compare-candidate").BoundingBoxAsync();
    var compareButtonBox = await Page.Locator("#compare-benchmark-runs").BoundingBoxAsync();
    Assert.IsNotNull(baselineBox);
    Assert.IsNotNull(candidateBox);
    Assert.IsNotNull(compareButtonBox);
    Assert.IsGreaterThanOrEqualTo(baselineBox.Y + baselineBox.Height, candidateBox.Y);
    Assert.IsGreaterThanOrEqualTo(candidateBox.Y + candidateBox.Height, compareButtonBox.Y);
    var historyPanelBox = await Page.Locator(".benchmark-history-tools").BoundingBoxAsync();
    var persistedSelectBox = await Page.Locator("#benchmark-history").BoundingBoxAsync();
    Assert.IsNotNull(historyPanelBox);
    Assert.IsNotNull(persistedSelectBox);
    Assert.IsLessThanOrEqualTo(historyPanelBox.Width, persistedSelectBox.Width);
    await Page.Locator("#compare-benchmark-runs").ClickAsync();
    await Expect(Page.Locator("#benchmark-comparison")).ToContainTextAsync("Comparable");
    await Expect(Page.Locator("#benchmark-comparison")).ToContainTextAsync("PASS changed");
    await Page.Locator("#benchmark-history").SelectOptionAsync(baseline.RunId);
    await Expect(Page.Locator("#benchmark-score-context"))
      .ToContainTextAsync("Original score");
    await Expect(Page.Locator("#benchmark-score-context"))
      .ToContainTextAsync("Current-profile score");
    await Page.Locator("#benchmark-raw-evidence summary").ClickAsync();
    await Expect(Page.Locator("#benchmark-raw-evidence-content"))
      .ToContainTextAsync(baseline.RunId);

    async Task<BenchmarkHistoricalComparison> GetComparisonAsync(
      string baselineRunId,
      string candidateRunId
    )
    {
      var path = "api/benchmarks/comparisons?baselineRunId="
        + Uri.EscapeDataString(baselineRunId)
        + "&candidateRunId="
        + Uri.EscapeDataString(candidateRunId);
      return (await _environment.HttpClient
        .GetFromJsonAsync<BenchmarkHistoricalComparison>(path))!;
    }
  }

  private static BenchmarkSuiteRunResult WithHistoricalEvidence(
    BenchmarkSuiteRunResult result,
    string runId,
    DateTimeOffset startedAt
  )
  {
    BenchmarkEvidenceValue Detected(string value, string? unit = null) => new(
      BenchmarkEvidenceStatusIds.Detected,
      value,
      unit
    );
    var harnesses = result.HarnessResults.Select(harness => harness.Harness).ToArray();
    return result with
    {
      RunId = runId,
      StartedAt = startedAt,
      EndedAt = startedAt.AddMilliseconds(result.DurationMilliseconds),
      SchemaVersion = 3,
      SelectedModels = [result.Model],
      SelectedHarnesses = harnesses,
      ModelIdentities =
      [
        new BenchmarkModelIdentity(
          result.Model,
          result.ModelDigest,
          result.Provider,
          1_000,
          startedAt,
          "Q4_K_M",
          32_768,
          16_384,
          "fixture",
          "gguf",
          "qwen",
          16_384
        )
      ],
      HarnessIdentities = harnesses.Select(harness => new BenchmarkHarnessIdentity(
        harness,
        "fixture-v1",
        BenchmarkEvidenceStatusIds.Detected
      )).ToArray(),
      Environment = new BenchmarkEnvironmentIdentity(
        "ollama-local",
        "fixture-runtime-v1",
        true,
        16_384,
        startedAt,
        Detected("Windows fixture"),
        Detected("CPU fixture"),
        [new BenchmarkGpuIdentity("gpu-0", Detected("GPU fixture"), Detected("24000000000", "bytes"))],
        Detected("64000000000", "bytes"),
        Detected("1.0.0"),
        Detected("commit-fixture"),
        BenchmarkEvidenceStatusIds.Detected
      ),
      Configuration = new BenchmarkConfigurationIdentity(
        result.TimeoutSeconds,
        true,
        "fixture-conditions-v1"
      ),
      ScoringProfileVersion = BenchmarkScoringProfileIds.DefaultVersion,
      RawMeasurementsStatus = BenchmarkEvidenceStatusIds.Measured,
      ValidationEvidenceStatus = BenchmarkEvidenceStatusIds.Measured
    };
  }

  private static BenchmarkSuiteRunResult AsHistoricalMatrix(
    BenchmarkSuiteRunResult result,
    string runId,
    DateTimeOffset startedAt
  )
  {
    var cells = result.HarnessResults.Select((harness, index) =>
      new BenchmarkMatrixCellResult(
        index + 1,
        result.Model,
        result.ModelDigest,
        result.Provider,
        harness.Harness,
        harness.HarnessVersion,
        BenchmarkMatrixCellStatusIds.Completed,
        BenchmarkMatrixCellStatusIds.Available,
        null,
        harness.Passed,
        harness.Total,
        harness.Score,
        harness.DurationMilliseconds,
        harness.Terminality,
        (int)Math.Round(harness.Tests.Average(test => test.Score?.Correctness ?? 0)),
        null,
        null,
        null,
        harness
      )).ToArray();
    return result with
    {
      RunId = runId,
      Model = "matrix",
      ModelDigest = null,
      StartedAt = startedAt,
      EndedAt = startedAt.AddMilliseconds(result.DurationMilliseconds),
      Cells = cells,
      ExecutionOrder = cells.Select(cell => $"{cell.Model}|{cell.Harness}").ToArray()
    };
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task EvidenceBasedRecommendationsAreDeterministicTraceableAndNeverExecuteAutomatically()
  {
    await _environment.HttpClient.PostAsync("api/benchmarks/scoring-profile/reset", null);
    var resultsDirectory = Path.Combine(_environment.DataDirectory, "benchmark-results");
    Directory.CreateDirectory(resultsDirectory);
    var options = new JsonSerializerOptions { WriteIndented = true };
    var startedAt = new DateTimeOffset(2026, 8, 23, 20, 0, 0, TimeSpan.Zero);
    const string measuredModel = "aaa-m12-measured:latest";
    const string partialModel = "aaa-m12-partial:latest";
    const string conflictModel = "aaa-m12-conflict:latest";
    const string incompatibleModel = "aaa-m12-incompatible:latest";
    const string legacyModel = "aaa-m12-legacy:latest";

    var measuredOne = WithRecommendationIdentity(
      WithHistoricalEvidence(
        CreateScoringProjectionFixture(),
        Guid.NewGuid().ToString("N"),
        startedAt
      ),
      measuredModel
    );
    var measuredTwo = measuredOne with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddMinutes(1),
      EndedAt = startedAt.AddMinutes(1).AddSeconds(1)
    };
    var measuredThree = measuredOne with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddMinutes(2),
      EndedAt = startedAt.AddMinutes(2).AddSeconds(1)
    };
    var partialOne = WithRecommendationIdentity(
      measuredOne with
      {
        RunId = Guid.NewGuid().ToString("N"),
        StartedAt = startedAt.AddMinutes(3)
      },
      partialModel
    );
    var partialTwo = partialOne with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddMinutes(4),
      HarnessIdentities = partialOne.HarnessIdentities!.Select(identity =>
        identity with { Version = identity.Version + "-changed" }
      ).ToArray()
    };
    var conflictOne = WithRecommendationIdentity(
      measuredOne with
      {
        RunId = Guid.NewGuid().ToString("N"),
        StartedAt = startedAt.AddMinutes(5)
      },
      conflictModel
    );
    var conflictTwo = FailRecommendationHarness(
      conflictOne with
      {
        RunId = Guid.NewGuid().ToString("N"),
        StartedAt = startedAt.AddMinutes(6)
      },
      HarnessIds.Native
    );
    var incompatibleOne = WithRecommendationIdentity(
      measuredOne with
      {
        RunId = Guid.NewGuid().ToString("N"),
        StartedAt = startedAt.AddMinutes(7)
      },
      incompatibleModel
    );
    var incompatibleTwo = incompatibleOne with
    {
      RunId = Guid.NewGuid().ToString("N"),
      StartedAt = startedAt.AddMinutes(8),
      SuiteVersion = 99
    };
    var legacy = CreateIncompleteScoringFixture() with
    {
      RunId = Guid.NewGuid().ToString("N"),
      Model = legacyModel,
      ModelDigest = "legacy-digest",
      StartedAt = startedAt.AddMinutes(9)
    };
    var fixtures = new[]
    {
      measuredOne,
      measuredTwo,
      measuredThree,
      partialOne,
      partialTwo,
      conflictOne,
      conflictTwo,
      incompatibleOne,
      incompatibleTwo,
      legacy
    };
    foreach (var result in fixtures)
    {
      await File.WriteAllTextAsync(
        Path.Combine(resultsDirectory, result.RunId + ".json"),
        JsonSerializer.Serialize(result, options)
      );
    }
    var persistedResultCount = Directory.EnumerateFiles(resultsDirectory, "*.json").Count();
    _environment.FakeOllama.Reset();
    _environment.FakeCloud.Reset();

    var local = await RecommendAsync(
      BenchmarkRecommendationCategoryIds.GeneralCoding,
      "default"
    );
    Assert.AreEqual(
      BenchmarkRecommendationService.AlgorithmVersion,
      local.AlgorithmVersion
    );
    Assert.AreEqual("not-requested", local.ExternalResearchStatus);
    Assert.HasCount(0, local.ExternalEvidence);
    var measuredNative = Candidate(local, measuredModel, HarnessIds.Native);
    var measuredCodex = Candidate(local, measuredModel, HarnessIds.Codex);
    Assert.IsGreaterThan(measuredNative.Rank, measuredCodex.Rank);
    Assert.AreEqual(BenchmarkRecommendationConfidenceIds.Strong, measuredNative.Confidence);
    Assert.AreEqual(2, measuredNative.ComparableHistoricalRunCount);
    Assert.IsTrue(measuredNative.EvidenceSources.Contains(
      BenchmarkRecommendationEvidenceSourceIds.MeasuredLocally
    ));
    Assert.IsTrue(measuredNative.EvidenceSources.Contains(
      BenchmarkRecommendationEvidenceSourceIds.HistoricalLocal
    ));
    Assert.HasCount(3, measuredNative.Evidence);
    Assert.IsTrue(measuredNative.Evidence.All(link => fixtures.Any(result =>
      result.RunId == link.RunId)));

    var repeated = await RecommendAsync(
      BenchmarkRecommendationCategoryIds.GeneralCoding,
      "default"
    );
    Assert.AreEqual(local.RecommendationId, repeated.RecommendationId);
    Assert.AreEqual(
      JsonSerializer.Serialize(local.Candidates),
      JsonSerializer.Serialize(repeated.Candidates)
    );
    var persistedRecommendation = await _environment.HttpClient
      .GetFromJsonAsync<BenchmarkRecommendationResult>(
        $"api/benchmarks/recommendations/{local.RecommendationId}"
      );
    Assert.IsNotNull(persistedRecommendation);
    Assert.AreEqual(local.EvidenceSetFingerprint, persistedRecommendation.EvidenceSetFingerprint);

    var correctness = await RecommendAsync(
      BenchmarkRecommendationCategoryIds.CorrectnessFirst,
      "default"
    );
    Assert.IsGreaterThan(
      Candidate(correctness, measuredModel, HarnessIds.Codex).Rank,
      Candidate(correctness, measuredModel, HarnessIds.Native).Rank
    );
    var terminality = await RecommendAsync(
      BenchmarkRecommendationCategoryIds.TerminalityFirst,
      "default"
    );
    Assert.IsGreaterThan(
      Candidate(terminality, measuredModel, HarnessIds.Native).Rank,
      Candidate(terminality, measuredModel, HarnessIds.Codex).Rank
    );

    try
    {
      using var custom = await _environment.HttpClient.PutAsJsonAsync(
        "api/benchmarks/scoring-profile",
        new BenchmarkScoreWeights(0, 100, 0, 0, 0)
      );
      custom.EnsureSuccessStatusCode();
      var active = await RecommendAsync(
        BenchmarkRecommendationCategoryIds.GeneralCoding,
        "active"
      );
      Assert.AreEqual(BenchmarkScoringProfileIds.Custom, active.ScoringProfile.Id);
      Assert.IsGreaterThan(
        Candidate(active, measuredModel, HarnessIds.Codex).Rank,
        Candidate(active, measuredModel, HarnessIds.Native).Rank
      );
    }
    finally
    {
      using var reset = await _environment.HttpClient.PostAsync(
        "api/benchmarks/scoring-profile/reset",
        null
      );
      reset.EnsureSuccessStatusCode();
    }

    var partialCandidate = Candidate(local, partialModel, HarnessIds.Native);
    Assert.AreEqual(
      BenchmarkRecommendationConfidenceIds.Limited,
      partialCandidate.Confidence
    );
    Assert.AreEqual(1, partialCandidate.PartialHistoricalRunCount);
    Assert.IsTrue(partialCandidate.Evidence.Any(link =>
      link.Comparability == BenchmarkComparabilityIds.PartiallyComparable));
    var conflictingCandidate = Candidate(local, conflictModel, HarnessIds.Native);
    Assert.AreEqual(
      BenchmarkRecommendationConfidenceIds.Mixed,
      conflictingCandidate.Confidence
    );
    Assert.IsTrue(conflictingCandidate.Weaknesses.Any(weakness => weakness.Contains(
      "conflict",
      StringComparison.OrdinalIgnoreCase
    )));
    var incompatibleCandidate = Candidate(
      local,
      incompatibleModel,
      HarnessIds.Native
    );
    Assert.AreEqual(
      BenchmarkRecommendationConfidenceIds.Limited,
      incompatibleCandidate.Confidence
    );
    Assert.AreEqual(1, incompatibleCandidate.IncompatibleHistoricalRunCount);
    Assert.IsTrue(incompatibleCandidate.Evidence.Any(link =>
      link.Comparability == BenchmarkComparabilityIds.NotDirectlyComparable));

    var insufficient = await RecommendAsync(
      BenchmarkRecommendationCategoryIds.RecoveryHeavy,
      "default"
    );
    Assert.IsTrue(insufficient.MissingEvidence.Any(item =>
      item.Model == measuredModel
      && item.Reason.Contains("No relevant local", StringComparison.Ordinal)));

    using (var invalid = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/recommendations",
      new BenchmarkRecommendationRequest("invented-category")
    ))
    {
      Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
    Assert.HasCount(0, _environment.FakeOllama.Requests);
    Assert.HasCount(0, _environment.FakeCloud.Requests);
    Assert.AreEqual(
      persistedResultCount,
      Directory.EnumerateFiles(resultsDirectory, "*.json").Count()
    );

    using (var saved = await _environment.HttpClient.PutAsJsonAsync(
      "api/web-search/key",
      new { apiKey = "ollama_fake_m12_search_key" }
    ))
    {
      saved.EnsureSuccessStatusCode();
    }
    try
    {
      _environment.FakeCloud.Reset();
      var external = await RecommendAsync(
        BenchmarkRecommendationCategoryIds.GeneralCoding,
        "default",
        includeExternalEvidence: true
      );
      Assert.AreEqual("completed", external.ExternalResearchStatus);
      Assert.HasCount(3, external.ExternalEvidence);
      Assert.IsTrue(external.ExternalEvidence.All(item =>
        item.Source == BenchmarkRecommendationEvidenceSourceIds.ExternalEvidence
        && item.Status == "unverified-external"));
      Assert.IsTrue(external.Candidates.All(candidate =>
        !candidate.EvidenceSources.Contains(
          BenchmarkRecommendationEvidenceSourceIds.ExternalEvidence
        )));
      var webRequest = _environment.FakeCloud.Requests.Single(request =>
        request.Path == "/ollama/api/web_search");
      Assert.DoesNotContain(measuredOne.RunId, webRequest.Body);
      Assert.DoesNotContain(measuredOne.ModelDigest!, webRequest.Body);
      Assert.HasCount(0, _environment.FakeOllama.Requests);

      await Page.GotoAsync("/");
      await Page.Locator("#open-benchmarks").ClickAsync();
      await Expect(Page.Locator("#benchmark-recommendation-results")).ToBeVisibleAsync();
      await Page.Locator(".benchmark-inline-advanced > summary").ClickAsync();
      await Page.Locator("#benchmark-recommendation-category")
        .SelectOptionAsync(BenchmarkRecommendationCategoryIds.GeneralCoding);
      await Page.Locator("#benchmark-recommendation-profile").SelectOptionAsync("default");
      await Page.Locator("#generate-benchmark-recommendation").ClickAsync();
      await Page.Locator(".benchmark-recommendation-alternatives > summary")
        .ClickAsync();
      var measuredCard = Page.Locator(".benchmark-recommendation-card").Filter(
        new LocatorFilterOptions { HasTextString = measuredModel }
      ).First;
      await Expect(measuredCard).ToBeVisibleAsync();
      await Expect(measuredCard).ToContainTextAsync("Measured locally + Historical local");
      await measuredCard.Locator("details summary").ClickAsync();
      await measuredCard.Locator("[data-recommendation-run-id]").First.ClickAsync();
      await Expect(Page.Locator("#benchmark-run-summary")).ToContainTextAsync(measuredModel);
      await Page.Locator("#research-benchmark-recommendation").ClickAsync();
      await Expect(Page.Locator(".benchmark-recommendation-external"))
        .ToContainTextAsync("completed");
      await Expect(Page.Locator(".benchmark-recommendation-external a"))
        .ToHaveCountAsync(3);
      Assert.AreEqual(
        persistedResultCount,
        Directory.EnumerateFiles(resultsDirectory, "*.json").Count()
      );
      Assert.HasCount(0, _environment.FakeOllama.Requests);
    }
    finally
    {
      await _environment.HttpClient.DeleteAsync(
        "api/web-search/key?confirmed=true"
      );
    }

    async Task<BenchmarkRecommendationResult> RecommendAsync(
      string category,
      string profile,
      bool includeExternalEvidence = false
    )
    {
      using var response = await _environment.HttpClient.PostAsJsonAsync(
        "api/benchmarks/recommendations",
        new BenchmarkRecommendationRequest(
          category,
          profile,
          includeExternalEvidence
        )
      );
      response.EnsureSuccessStatusCode();
      return (await response.Content
        .ReadFromJsonAsync<BenchmarkRecommendationResult>())!;
    }

    static BenchmarkRecommendationCandidate Candidate(
      BenchmarkRecommendationResult result,
      string model,
      string harness
    )
    {
      return result.Candidates.Single(candidate =>
        candidate.Model == model && candidate.Harness == harness);
    }
  }

  private static BenchmarkSuiteRunResult WithRecommendationIdentity(
    BenchmarkSuiteRunResult result,
    string model
  )
  {
    var digest = $"digest-{model}";
    return result with
    {
      Model = model,
      ModelDigest = digest,
      SelectedModels = [model],
      ModelIdentities = result.ModelIdentities!.Select(identity => identity with
      {
        Model = model,
        Digest = digest
      }).ToArray()
    };
  }

  private static BenchmarkSuiteRunResult FailRecommendationHarness(
    BenchmarkSuiteRunResult result,
    string harnessId
  )
  {
    return result with
    {
      HarnessResults = result.HarnessResults.Select(harness =>
      {
        if (!string.Equals(harness.Harness, harnessId, StringComparison.Ordinal))
        {
          return harness;
        }
        return harness with
        {
          Passed = 0,
          Score = 0,
          Terminality = 0,
          Tests = harness.Tests.Select(test => test with
          {
            RawResult = test.RawResult with
            {
              Status = BenchmarkResultStatusIds.Fail,
              ObjectiveAchieved = false,
              ByteAccuracy = 0,
              ContainmentAccuracy = 0,
              ExecutionStatus = BenchmarkExecutionStatusIds.TimedOut,
              Exactness = 0,
              UsefulPartialOutcome = false,
              HostValidationResult = "fail",
              BehaviorMetrics = null
            },
            Score = new BenchmarkScore(0, 0, 0, 0, 0, 0)
          }).ToArray()
        };
      }).ToArray()
    };
  }

  private static BenchmarkSuiteRunResult CreateScoringProjectionFixture()
  {
    var startedAt = new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);
    var codex = ScoringHarness(
      HarnessIds.Codex,
      BenchmarkResultStatusIds.Fail,
      BenchmarkExecutionStatusIds.TimedOut,
      exactness: 100,
      toolCalls: 1,
      score: new BenchmarkScore(85m, 100, 100, 0, 100, 100),
      passed: 0,
      terminality: 0,
      duration: 500,
      startedAt
    );
    var native = ScoringHarness(
      HarnessIds.Native,
      BenchmarkResultStatusIds.Pass,
      BenchmarkExecutionStatusIds.Completed,
      exactness: 80,
      toolCalls: 4,
      score: new BenchmarkScore(93m, 100, 80, 100, 100, 60),
      passed: 1,
      terminality: 100,
      duration: 1_000,
      startedAt
    );
    var openCode = ScoringHarness(
      HarnessIds.OpenCode,
      BenchmarkResultStatusIds.Pass,
      BenchmarkExecutionStatusIds.Completed,
      exactness: 80,
      toolCalls: 4,
      score: new BenchmarkScore(93m, 100, 80, 100, 100, 60),
      passed: 1,
      terminality: 100,
      duration: 1_000,
      startedAt
    );
    var runId = Guid.NewGuid().ToString("N");
    return new BenchmarkSuiteRunResult(
      runId,
      "historical:latest",
      "digest-historical",
      "ollama-local",
      BenchmarkSuiteIds.BasicCrud,
      BenchmarkSuiteIds.BasicCrudVersion,
      BenchmarkSuiteIds.FixtureId,
      BenchmarkSuiteIds.FixtureVersion,
      startedAt,
      startedAt.AddSeconds(1),
      1_000,
      BenchmarkRunStatusIds.Completed,
      BenchmarkRunStatusIds.CompletedWithFailures,
      120,
      BenchmarkScoreWeights.Default,
      [codex, native, openCode],
      [
        new BenchmarkRankingEntry(1, HarnessIds.Native, 1, 93m, 1_000, 100),
        new BenchmarkRankingEntry(2, HarnessIds.OpenCode, 1, 93m, 1_000, 100),
        new BenchmarkRankingEntry(3, HarnessIds.Codex, 0, 85m, 500, 0)
      ]
    );
  }

  private static BenchmarkSuiteRunResult CreateIncompleteScoringFixture()
  {
    var startedAt = new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);
    var test = ScoringTest(
      HarnessIds.Native,
      BenchmarkResultStatusIds.Error,
      BenchmarkExecutionStatusIds.TimedOut,
      exactness: 0,
      toolCalls: null,
      score: new BenchmarkScore(2.5m, 0, 0, 0, 0, 50),
      startedAt
    );
    var harness = new BenchmarkHarnessResult(
      HarnessIds.Native,
      "fixture",
      0,
      2,
      1.25m,
      -5,
      0,
      BenchmarkRunStatusIds.Completed,
      [test]
    );
    var runId = Guid.NewGuid().ToString("N");
    return new BenchmarkSuiteRunResult(
      runId,
      "historical:latest",
      "digest-historical",
      "ollama-local",
      BenchmarkSuiteIds.BasicCrud,
      BenchmarkSuiteIds.BasicCrudVersion,
      BenchmarkSuiteIds.FixtureId,
      BenchmarkSuiteIds.FixtureVersion,
      startedAt,
      startedAt,
      0,
      BenchmarkRunStatusIds.Completed,
      BenchmarkRunStatusIds.CompletedWithFailures,
      120,
      BenchmarkScoreWeights.Default,
      [harness],
      [new BenchmarkRankingEntry(1, HarnessIds.Native, 0, 1.25m, -5, 0)]
    );
  }

  private static BenchmarkHarnessResult ScoringHarness(
    string harness,
    string status,
    string executionStatus,
    int exactness,
    int? toolCalls,
    BenchmarkScore score,
    int passed,
    int terminality,
    long duration,
    DateTimeOffset startedAt
  )
  {
    return new BenchmarkHarnessResult(
      harness,
      "fixture",
      passed,
      1,
      score.Total,
      duration,
      terminality,
      BenchmarkRunStatusIds.Completed,
      [ScoringTest(
        harness,
        status,
        executionStatus,
        exactness,
        toolCalls,
        score,
        startedAt
      )]
    );
  }

  private static BenchmarkRunResult ScoringTest(
    string harness,
    string status,
    string executionStatus,
    int exactness,
    int? toolCalls,
    BenchmarkScore score,
    DateTimeOffset startedAt
  )
  {
    var completed = string.Equals(
      executionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    );
    var testRunId = Guid.NewGuid().ToString("N");
    var raw = new BenchmarkRawResult(
      status,
      ObjectiveAchieved: exactness > 0,
      ByteAccuracy: exactness,
      DirectoryAccuracy: 100,
      FilenameAccuracy: 100,
      ContainmentAccuracy: exactness > 0 ? 100 : 0,
      UnexpectedCreatedFiles: [],
      UnexpectedModifiedFiles: [],
      UnexpectedDeletedFiles: [],
      ExecutionStatus: executionStatus,
      Error: completed
        ? null
        : new BenchmarkError("fixture-non-terminal", "Fixture did not complete.", "fixture", true),
      Exactness: exactness,
      ToolCallCount: toolCalls,
      ChangedFiles: [],
      UnexpectedFiles: [],
      HostValidationResult: status == BenchmarkResultStatusIds.Pass ? "pass" : "fail"
    );
    return new BenchmarkRunResult(
      new BenchmarkRun(
        testRunId,
        BenchmarkIds.FileSystemCreate001,
        1,
        "historical:latest",
        "digest-historical",
        "ollama-local",
        harness,
        "fixture",
        testRunId,
        $"C:\\benchmark-fixture\\{testRunId}",
        startedAt,
        startedAt,
        executionStatus,
        Prompt: "fixture",
        FixtureFingerprint: "fixture"
      ),
      raw,
      true,
      score,
      0
    );
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task ModelHarnessMatrixIsSequentialIsolatedPersistedAndRescoredWithoutRerun()
  {
    var runId = Guid.NewGuid().ToString("N");
    var models = new[] { "missing:latest", "alpha:latest", "beta:code" };
    var harnesses = new[] { HarnessIds.Native, HarnessIds.Codex };
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs/live",
      new BenchmarkSuiteRunRequest(
        models[0],
        harnesses,
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true,
        ClientRunId: runId,
        Models: models,
        ScoringProfileId: BenchmarkScoringProfileIds.Custom,
        ScoreWeights: new BenchmarkScoreWeights(0, 100, 0, 0, 0)
      )
    );
    Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);

    BenchmarkLiveRunView? view = null;
    for (var attempt = 0; attempt < 900; attempt++)
    {
      view = await _environment.HttpClient.GetFromJsonAsync<BenchmarkLiveRunView>(
        $"api/benchmarks/suite-runs/{runId}/live"
      );
      if (view?.Terminal == true)
      {
        break;
      }
      await Task.Delay(100);
    }
    Assert.IsNotNull(view);
    Assert.IsTrue(view.Terminal);
    var started = view.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunStarted);
    CollectionAssert.AreEqual(models, started.SelectedModels!.ToArray());
    CollectionAssert.AreEqual(harnesses, started.SelectedHarnesses!.ToArray());
    Assert.AreEqual(6, started.TotalCells);

    var result = view.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunCompleted)
      .FinalResult;
    Assert.IsNotNull(result);
    Assert.AreEqual(4, result.SchemaVersion);
    Assert.HasCount(1, result.SelectedSuites!);
    Assert.AreEqual(BenchmarkScoringProfileIds.Custom, result.ScoringProfileId);
    Assert.AreEqual(100, result.ScoreWeights.Correctness);
    CollectionAssert.AreEqual(models, result.SelectedModels!.ToArray());
    CollectionAssert.AreEqual(harnesses, result.SelectedHarnesses!.ToArray());
    Assert.HasCount(6, result.Cells!);
    var cells = result.Cells!;
    CollectionAssert.AreEqual(
      new[]
      {
        "missing:latest|native",
        "missing:latest|codex",
        "alpha:latest|native",
        "alpha:latest|codex",
        "beta:code|native",
        "beta:code|codex"
      },
      result.ExecutionOrder!.ToArray()
    );
    Assert.IsTrue(cells.Take(2).All(cell =>
      cell.Status == BenchmarkMatrixCellStatusIds.Unavailable
      && cell.Result is null));
    var completed = cells.Skip(2).ToArray();
    Assert.IsTrue(completed.All(cell =>
      cell.Status == BenchmarkMatrixCellStatusIds.Completed
      && cell.Compatibility == BenchmarkMatrixCellStatusIds.Available
      && cell.Result is not null));
    Assert.IsTrue(completed.All(cell => cell.Result!.Tests.All(test =>
      test.Run.Model == cell.Model
      && test.Run.ModelDigest == $"digest-{cell.Model}"
      && test.WorkspaceCleanedUp
      && !Directory.Exists(test.Run.WorkspacePath))));
    var testRuns = completed.SelectMany(cell => cell.Result!.Tests).ToArray();
    Assert.AreEqual(testRuns.Length, testRuns.Select(test => test.Run.WorkspaceId).Distinct().Count());
    foreach (var testId in testRuns.Select(test => test.Run.TestId).Distinct())
    {
      Assert.HasCount(1, testRuns.Where(test => test.Run.TestId == testId)
        .Select(test => test.Run.FixtureFingerprint).Distinct().ToArray());
      Assert.HasCount(1, testRuns.Where(test => test.Run.TestId == testId)
        .Select(test => test.Run.Prompt).Distinct().ToArray());
    }
    Assert.HasCount(6, result.PairRanking!);
    Assert.HasCount(3, result.ModelRanking!);
    Assert.HasCount(2, result.HarnessRanking!);
    Assert.HasCount(3, result.ModelIdentities!);
    var modelIdentities = result.ModelIdentities!;
    Assert.IsNull(modelIdentities.Single(model => model.Model == "missing:latest").Digest);
    Assert.AreEqual("Q4_K_M", modelIdentities.Single(
      model => model.Model == "alpha:latest").Quantization);
    Assert.IsTrue(result.Environment!.Sequential);
    Assert.IsNotNull(result.Environment.CapturedAt);
    Assert.AreEqual(
      BenchmarkEvidenceStatusIds.Detected,
      result.Environment.OperatingSystem!.Status
    );
    Assert.IsNotNull(result.Environment.Ram);
    Assert.HasCount(2, result.HarnessIdentities!);
    Assert.IsTrue(result.HarnessIdentities!.All(identity =>
      identity.VersionStatus == BenchmarkEvidenceStatusIds.Detected));
    Assert.IsNotNull(result.Configuration);
    Assert.AreEqual(20, result.Configuration.TimeoutSeconds);
    Assert.IsFalse(string.IsNullOrWhiteSpace(result.Configuration.Fingerprint));
    Assert.AreEqual(
      BenchmarkScoringProfileIds.DefaultVersion,
      result.ScoringProfileVersion
    );
    Assert.AreEqual(
      BenchmarkEvidenceStatusIds.Measured,
      result.RawMeasurementsStatus
    );
    Assert.AreEqual(
      BenchmarkEvidenceStatusIds.Measured,
      result.ValidationEvidenceStatus
    );

    long previousCompletion = 0;
    foreach (var cell in completed.OrderBy(cell => cell.ExecutionOrder))
    {
      var cellStarted = view.Events.Single(item =>
        item.Type == BenchmarkProgressTypeIds.HarnessStarted
        && item.Model == cell.Model
        && item.Harness == cell.Harness);
      var cellCompleted = view.Events.Single(item =>
        item.Type == BenchmarkProgressTypeIds.HarnessCompleted
        && item.Model == cell.Model
        && item.Harness == cell.Harness);
      Assert.IsGreaterThan(previousCompletion, cellStarted.Sequence);
      Assert.IsGreaterThan(cellStarted.Sequence, cellCompleted.Sequence);
      previousCompletion = cellCompleted.Sequence;
    }

    var persisted = await _environment.HttpClient.GetFromJsonAsync<BenchmarkSuiteRunResult>(
      $"api/benchmarks/suite-runs/{runId}"
    );
    Assert.AreEqual(JsonSerializer.Serialize(result), JsonSerializer.Serialize(persisted));
    var inferenceCount = _environment.FakeOllama.AllRequests.Count;
    using var saveProfile = await _environment.HttpClient.PutAsJsonAsync(
      "api/benchmarks/scoring-profile",
      new BenchmarkScoreWeights(100, 0, 0, 0, 0)
    );
    saveProfile.EnsureSuccessStatusCode();
    using var rescore = await _environment.HttpClient.PostAsync(
      $"api/benchmarks/suite-runs/{runId}/rescore",
      null
    );
    rescore.EnsureSuccessStatusCode();
    var projection = await rescore.Content.ReadFromJsonAsync<BenchmarkScoringProjection>();
    Assert.IsNotNull(projection);
    Assert.HasCount(4, projection.MatrixCellScores!);
    Assert.HasCount(6, projection.PairRanking!);
    Assert.HasCount(3, projection.ModelRanking!);
    Assert.HasCount(2, projection.HarnessRanking!);
    Assert.HasCount(inferenceCount, _environment.FakeOllama.AllRequests);
    using var resetProfile = await _environment.HttpClient.PostAsync(
      "api/benchmarks/scoring-profile/reset",
      null
    );
    resetProfile.EnsureSuccessStatusCode();
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
    _environment.FakeOllama.RemoveLoadedModel("beta:code");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ModelHarnessMatrixContinuesAfterFailedCell()
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "structured-failure:latest",
        [HarnessIds.Codex],
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true,
        Models: ["structured-failure:latest", "alpha:latest"]
      )
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(result);
    Assert.HasCount(2, result.Cells!);
    Assert.AreEqual(BenchmarkMatrixCellStatusIds.Failed, result.Cells![0].Status);
    Assert.AreEqual(BenchmarkMatrixCellStatusIds.Completed, result.Cells[1].Status);
    Assert.HasCount(4, result.Cells[1].Result!.Tests);
    Assert.IsTrue(result.Cells[1].Result!.Tests.All(test =>
      test.Run.Model == "alpha:latest" && test.WorkspaceCleanedUp));
    _environment.FakeOllama.RemoveLoadedModel("structured-failure:latest");
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ModelHarnessMatrixCancellationSettlesCurrentAndRemainingCells()
  {
    var runId = Guid.NewGuid().ToString("N");
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs/live",
      new BenchmarkSuiteRunRequest(
        "docs:latest",
        [HarnessIds.Native],
        TimeoutSeconds: 60,
        ModelExecutionPermissionGranted: true,
        ClientRunId: runId,
        Models: ["docs:latest", "alpha:latest"]
      )
    );
    Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
    for (var attempt = 0; attempt < 200; attempt++)
    {
      var current = await _environment.HttpClient.GetFromJsonAsync<BenchmarkLiveRunView>(
        $"api/benchmarks/suite-runs/{runId}/live"
      );
      if (current?.Events.Any(item =>
        item.Type == BenchmarkProgressTypeIds.HarnessStarted
        && item.Model == "docs:latest") == true)
      {
        break;
      }
      await Task.Delay(50);
    }
    using var cancel = await _environment.HttpClient.PostAsync(
      $"api/benchmarks/suite-runs/{runId}/cancel",
      null
    );
    Assert.AreEqual(HttpStatusCode.Accepted, cancel.StatusCode);
    BenchmarkLiveRunView? view = null;
    for (var attempt = 0; attempt < 300; attempt++)
    {
      view = await _environment.HttpClient.GetFromJsonAsync<BenchmarkLiveRunView>(
        $"api/benchmarks/suite-runs/{runId}/live"
      );
      if (view?.Terminal == true)
      {
        break;
      }
      await Task.Delay(100);
    }
    Assert.IsNotNull(view);
    Assert.IsTrue(view.Terminal);
    Assert.IsTrue(view.CancellationRequested);
    var result = view.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunCompleted)
      .FinalResult;
    Assert.IsNotNull(result);
    Assert.AreEqual(BenchmarkRunStatusIds.Cancelled, result.TerminalState);
    Assert.HasCount(2, result.Cells!);
    var cells = result.Cells!;
    Assert.IsTrue(cells.All(cell =>
      cell.Status == BenchmarkMatrixCellStatusIds.Cancelled));
    Assert.IsNull(cells[1].Result);
    _environment.FakeOllama.RemoveLoadedModel("docs:latest");
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task LiveBenchmarkPublishesReplayableLifecycleAndOneAuthoritativeFinalResult()
  {
    using var resetProfile = await _environment.HttpClient.PostAsync(
      "api/benchmarks/scoring-profile/reset",
      null
    );
    resetProfile.EnsureSuccessStatusCode();
    var clientRunId = Guid.NewGuid().ToString("N");
    using var startResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs/live",
      new BenchmarkSuiteRunRequest(
        "alpha:latest",
        [
          HarnessIds.Native,
          HarnessIds.Codex,
          HarnessIds.OpenCode,
          HarnessIds.QwenCode,
          HarnessIds.ClaudeCode
        ],
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true,
        ClientRunId: clientRunId
      )
    );
    Assert.AreEqual(HttpStatusCode.Accepted, startResponse.StatusCode);
    var started = await startResponse.Content.ReadFromJsonAsync<BenchmarkLiveRunStart>();
    Assert.IsNotNull(started);
    Assert.AreEqual(clientRunId, started.RunId);

    BenchmarkLiveRunView? view = null;
    for (var attempt = 0; attempt < 900; attempt++)
    {
      view = await _environment.HttpClient.GetFromJsonAsync<BenchmarkLiveRunView>(
        $"api/benchmarks/suite-runs/{clientRunId}/live"
      );
      if (view?.Terminal == true)
      {
        break;
      }
      await Task.Delay(100);
    }
    Assert.IsNotNull(view);
    Assert.IsTrue(view.Terminal);
    Assert.IsGreaterThan(0, view.LastSequence);
    Assert.AreEqual(
      view.Events.Count,
      view.Events.Select(item => item.Sequence).Distinct().Count()
    );
    Assert.IsTrue(view.Events.Select(item => item.Sequence).SequenceEqual(
      view.Events.Select(item => item.Sequence).OrderBy(sequence => sequence)
    ));
    Assert.HasCount(
      1,
      view.Events.Where(item => item.Type == BenchmarkProgressTypeIds.RunStarted)
    );
    Assert.HasCount(
      1,
      view.Events.Where(item => item.Type == BenchmarkProgressTypeIds.RunCompleted)
    );
    Assert.HasCount(
      0,
      view.Events.Where(item => item.Type == BenchmarkProgressTypeIds.RunFailed)
    );
    Assert.IsTrue(view.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.Activity
      && item.ActivityKind is BenchmarkActivityKindIds.FileRead
        or BenchmarkActivityKindIds.FileCreate
        or BenchmarkActivityKindIds.FileEdit
        or BenchmarkActivityKindIds.FileDelete
    ));
    Assert.IsTrue(view.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.Validation
      && item.ValidationChecks?.ContainsKey("Host validation") == true
    ));
    Assert.IsTrue(view.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.Ranking
      && item.Ranking?.Any(entry => entry.Rank is null) == true
    ));

    var harnessOrder = new[]
    {
      HarnessIds.Native,
      HarnessIds.Codex,
      HarnessIds.OpenCode,
      HarnessIds.QwenCode,
      HarnessIds.ClaudeCode
    };
    long? previousHarnessCompletion = null;
    foreach (var harnessId in harnessOrder)
    {
      var harnessStarted = view.Events.Single(item =>
        item.Type == BenchmarkProgressTypeIds.HarnessStarted
        && item.Harness == harnessId
      );
      var harnessCompleted = view.Events.Single(item =>
        item.Type == BenchmarkProgressTypeIds.HarnessCompleted
        && item.Harness == harnessId
      );
      Assert.IsLessThan(
        harnessCompleted.Sequence,
        harnessStarted.Sequence,
        harnessId
      );
      if (previousHarnessCompletion is not null)
      {
        Assert.IsLessThan(
          harnessStarted.Sequence,
          previousHarnessCompletion.Value,
          $"Harness {harnessId} started before the preceding harness completed."
        );
      }

      long? previousTestTerminal = null;
      foreach (var testId in new[]
      {
        BenchmarkIds.FileSystemCreate001,
        BenchmarkIds.FileSystemRead001,
        BenchmarkIds.FileSystemUpdate001,
        BenchmarkIds.FileSystemDelete001
      })
      {
        var states = view.Events.Where(item =>
          item.Type == BenchmarkProgressTypeIds.TestState
          && item.Harness == harnessId
          && item.TestId == testId
        ).Select(item => item.State).ToArray();
        CollectionAssert.IsSubsetOf(
          new[]
          {
            BenchmarkLiveStateIds.Pending,
            BenchmarkLiveStateIds.Running,
            BenchmarkLiveStateIds.HarnessCompleted,
            BenchmarkLiveStateIds.Validating,
            BenchmarkLiveStateIds.Passed
          },
          states,
          $"{harnessId}/{testId}: {string.Join(", ", states)}"
        );
        var running = view.Events.First(item =>
          item.Type == BenchmarkProgressTypeIds.TestState
          && item.Harness == harnessId
          && item.TestId == testId
          && item.State == BenchmarkLiveStateIds.Running
        );
        var terminal = view.Events.Single(item =>
          item.Type == BenchmarkProgressTypeIds.TestState
          && item.Harness == harnessId
          && item.TestId == testId
          && item.TestResult is not null
        );
        Assert.IsLessThan(
          terminal.Sequence,
          running.Sequence,
          $"{harnessId}/{testId}"
        );
        Assert.IsLessThan(
          harnessCompleted.Sequence,
          terminal.Sequence,
          $"{harnessId}/{testId} completed after its harness."
        );
        if (previousTestTerminal is not null)
        {
          Assert.IsLessThan(
            running.Sequence,
            previousTestTerminal.Value,
            $"Test {harnessId}/{testId} started before the preceding test completed."
          );
        }
        previousTestTerminal = terminal.Sequence;
      }
      previousHarnessCompletion = harnessCompleted.Sequence;
    }

    var finalEvent = view.Events.Single(
      item => item.Type == BenchmarkProgressTypeIds.RunCompleted
    );
    Assert.IsNotNull(finalEvent.FinalResult);
    using var persistedResponse = await _environment.HttpClient.GetAsync(
      $"api/benchmarks/suite-runs/{clientRunId}"
    );
    persistedResponse.EnsureSuccessStatusCode();
    var persisted = await persistedResponse.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(persisted);
    var openCode = persisted.HarnessResults.Single(
      result => result.Harness == HarnessIds.OpenCode
    );
    Assert.IsTrue(openCode.Tests.All(test => test.WorkspaceCleanedUp));
    Assert.IsTrue(openCode.Tests.All(test => !Directory.Exists(test.Run.WorkspacePath)));
    var openCodeProcessId = int.Parse(
      await File.ReadAllTextAsync(Path.Combine(
        _environment.DataDirectory,
        "opencode-runtime",
        "fake-opencode-process-id.txt"
      )),
      System.Globalization.CultureInfo.InvariantCulture
    );
    Assert.IsFalse(
      ProcessIsAlive(openCodeProcessId),
      "The workspace-scoped OpenCode server must stop before benchmark cleanup."
    );
    var qwen = persisted.HarnessResults.Single(result => result.Harness == HarnessIds.QwenCode);
    Assert.AreEqual("0.21.13-fake", qwen.HarnessVersion);
    Assert.AreEqual(4, qwen.Passed);
    Assert.AreEqual(4, qwen.Total);
    Assert.AreEqual(100, qwen.Terminality);
    Assert.IsGreaterThan(0, qwen.Score);
    Assert.IsTrue(qwen.Tests.All(test => test.Run.Model == "alpha:latest"));
    Assert.IsTrue(qwen.Tests.All(test => test.Run.Provider == "ollama-local"));
    Assert.IsTrue(qwen.Tests.All(test => test.RawResult.HostValidationResult == "pass"));
    Assert.IsTrue(qwen.Tests.All(test => test.WorkspaceCleanedUp));
    Assert.IsTrue(qwen.Tests.All(test => !Directory.Exists(test.Run.WorkspacePath)));
    Assert.AreEqual(
      2,
      qwen.Tests.Single(test => test.Run.TestId == BenchmarkIds.FileSystemRead001)
        .RawResult.ToolCallCount
    );
    Assert.IsTrue(persisted.Ranking.Any(entry => entry.Harness == HarnessIds.QwenCode));
    var claude = persisted.HarnessResults.Single(
      result => result.Harness == HarnessIds.ClaudeCode
    );
    Assert.AreEqual("2.1.234-fake (Claude Code)", claude.HarnessVersion);
    Assert.AreEqual(4, claude.Passed);
    Assert.AreEqual(4, claude.Total);
    Assert.AreEqual(100, claude.Terminality);
    Assert.IsTrue(claude.Tests.All(test => test.RawResult.HostValidationResult == "pass"));
    Assert.IsTrue(claude.Tests.All(test => test.WorkspaceCleanedUp));
    Assert.IsTrue(persisted.Ranking.Any(entry => entry.Harness == HarnessIds.ClaudeCode));
    using var scoringResponse = await _environment.HttpClient.PostAsync(
      $"api/benchmarks/suite-runs/{clientRunId}/rescore",
      null
    );
    scoringResponse.EnsureSuccessStatusCode();
    var scoring = await scoringResponse.Content.ReadFromJsonAsync<BenchmarkScoringProjection>();
    Assert.IsNotNull(scoring);
    Assert.AreEqual(BenchmarkScoringProfileIds.Default, scoring.ActiveProfile.Id);
    Assert.AreEqual(
      qwen.Score,
      scoring.HarnessScores.Single(score => score.Harness == HarnessIds.QwenCode).Score
    );
    Assert.AreEqual(
      JsonSerializer.Serialize(persisted.Ranking),
      JsonSerializer.Serialize(finalEvent.FinalResult.Ranking)
    );

    var qwenRuntime = Path.Combine(_environment.DataDirectory, "qwen-code-runtime");
    using var qwenSettings = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(qwenRuntime, "settings.json"))
    );
    Assert.AreEqual(
      32_768,
      qwenSettings.RootElement.GetProperty("modelProviders").GetProperty("openai")[0]
        .GetProperty("generationConfig").GetProperty("contextWindowSize").GetInt32()
    );
    CollectionAssert.AreEqual(
      Array.Empty<string>(),
      qwenSettings.RootElement.GetProperty("tools").GetProperty("core")
        .EnumerateArray().Select(item => item.GetString()).ToArray()
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "read_file",
        "create_file",
        "create_files",
        "write_file",
        "replace_text",
        "delete_paths"
      },
      qwenSettings.RootElement.GetProperty("mcpServers").GetProperty("agentic_router")
        .GetProperty("includeTools").EnumerateArray().Select(item => item.GetString()).ToArray()
    );
    var qwenProcessId = int.Parse(
      await File.ReadAllTextAsync(Path.Combine(qwenRuntime, "fake-qwen-process-id.txt")),
      System.Globalization.CultureInfo.InvariantCulture
    );
    Assert.IsFalse(
      ProcessIsAlive(qwenProcessId),
      "The workspace-scoped Qwen daemon must stop before benchmark cleanup."
    );
    using var qwenPrompt = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(qwenRuntime, "fake-qwen-prompt.json"))
    );
    var qwenPromptText = qwenPrompt.RootElement.GetProperty("text").GetString()!;
    StringAssert.Contains(qwenPromptText, "read_file, create_file, create_files, write_file, replace_text, delete_paths");
    Assert.DoesNotContain("run_process", qwenPromptText);
    Assert.DoesNotContain("git_status", qwenPromptText);

    var replayAfter = view.Events[view.Events.Count / 2].Sequence;
    using var replayResponse = await _environment.HttpClient.GetAsync(
      $"api/benchmarks/suite-runs/{clientRunId}/events?after={replayAfter}"
    );
    replayResponse.EnsureSuccessStatusCode();
    var replay = await replayResponse.Content.ReadAsStringAsync();
    var replayIds = replay.Split('\n', StringSplitOptions.RemoveEmptyEntries)
      .Where(line => line.StartsWith("id: ", StringComparison.Ordinal))
      .Select(line => long.Parse(line[4..]))
      .ToArray();
    Assert.IsNotEmpty(replayIds);
    Assert.IsTrue(replayIds.All(sequence => sequence > replayAfter));
    Assert.AreEqual(view.LastSequence, replayIds[^1]);
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LiveBenchmarkReportsRecoveryTimeoutAndContinuesAfterFailure()
  {
    async Task<BenchmarkLiveRunView> RunLiveAsync(
      string model,
      string harness,
      int timeoutSeconds
    )
    {
      var runId = Guid.NewGuid().ToString("N");
      using var response = await _environment.HttpClient.PostAsJsonAsync(
        "api/benchmarks/suite-runs/live",
        new BenchmarkSuiteRunRequest(
          model,
          [harness],
          TimeoutSeconds: timeoutSeconds,
          ModelExecutionPermissionGranted: true,
          ClientRunId: runId
        )
      );
      Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
      for (var attempt = 0; attempt < 250; attempt++)
      {
        var current = await _environment.HttpClient
          .GetFromJsonAsync<BenchmarkLiveRunView>(
            $"api/benchmarks/suite-runs/{runId}/live"
          );
        if (current?.Terminal == true)
        {
          return current;
        }
        await Task.Delay(100);
      }
      Assert.Fail($"Live benchmark {runId} did not reach a terminal state.");
      throw new InvalidOperationException();
    }

    var recovered = await RunLiveAsync("structured:latest", HarnessIds.Native, 20);
    Assert.IsTrue(recovered.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.Activity
      && item.ActivityKind == BenchmarkActivityKindIds.RecoveredError
    ));
    Assert.AreEqual(
      BenchmarkResultStatusIds.Pass,
      recovered.Events.Single(item => item.Type == BenchmarkProgressTypeIds.RunCompleted)
        .FinalResult!.HarnessResults.Single().Tests.Single(
          item => item.Run.TestId == BenchmarkIds.FileSystemUpdate001
        ).RawResult.Status
    );

    var failed = await RunLiveAsync(
      "structured-failure:latest",
      HarnessIds.Codex,
      20
    );
    var failedResult = failed.Events.Single(
      item => item.Type == BenchmarkProgressTypeIds.RunCompleted
    ).FinalResult!;
    Assert.AreEqual(
      BenchmarkResultStatusIds.Error,
      failedResult.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemUpdate001
      ).RawResult.Status
    );
    Assert.AreEqual(
      BenchmarkResultStatusIds.Pass,
      failedResult.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemDelete001
      ).RawResult.Status
    );

    var timedOut = await RunLiveAsync("unused:latest", HarnessIds.Native, 5);
    Assert.IsTrue(timedOut.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.TestState
      && item.TestId == BenchmarkIds.FileSystemRead001
      && item.State == BenchmarkLiveStateIds.TimedOut
    ));
    var timeoutResult = timedOut.Events.Single(
      item => item.Type == BenchmarkProgressTypeIds.RunCompleted
    ).FinalResult!;
    Assert.AreEqual(
      BenchmarkResultStatusIds.Pass,
      timeoutResult.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemDelete001
      ).RawResult.Status
    );
    _environment.FakeOllama.RemoveLoadedModel("structured:latest");
    _environment.FakeOllama.RemoveLoadedModel("structured-failure:latest");
    _environment.FakeOllama.RemoveLoadedModel("unused:latest");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AgentBehaviorV2PersistsMultiTurnMetricsAndKeepsCrudV1CatalogImmutable()
  {
    var catalog = await _environment.HttpClient.GetFromJsonAsync<JsonObject>(
      "api/benchmarks/catalog"
    );
    Assert.IsNotNull(catalog);
    var suites = catalog["suites"]!.AsArray();
    Assert.HasCount(2, suites);
    Assert.IsTrue(suites.Any(node =>
      node!["id"]!.GetValue<string>() == BenchmarkSuiteIds.BasicCrud
      && node["version"]!.GetValue<int>() == BenchmarkSuiteIds.BasicCrudVersion
      && node["tests"]!.AsArray().Count == 4));
    Assert.IsTrue(suites.Any(node =>
      node!["id"]!.GetValue<string>() == BenchmarkSuiteIds.AgentBehavior
      && node["version"]!.GetValue<int>() == BenchmarkSuiteIds.AgentBehaviorVersion
      && node["tests"]!.AsArray().Count == 7));

    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "alpha:latest",
        [HarnessIds.Native],
        BenchmarkSuiteIds.AgentBehavior,
        BenchmarkSuiteIds.AgentBehaviorVersion,
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true
      )
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(result);
    Assert.AreEqual(BenchmarkSuiteIds.AgentBehavior, result.SuiteId);
    Assert.AreEqual(BenchmarkSuiteIds.AgentBehaviorVersion, result.SuiteVersion);
    Assert.AreEqual(BenchmarkSuiteIds.AgentBehaviorFixtureId, result.FixtureId);
    Assert.AreEqual(BenchmarkSuiteIds.AgentBehaviorFixtureVersion, result.FixtureVersion);
    var harness = result.HarnessResults.Single();
    Assert.AreEqual(7, harness.Total);
    Assert.AreEqual(7, harness.Passed);
    Assert.AreEqual(100, harness.Terminality);
    Assert.IsTrue(harness.Tests.All(test => test.WorkspaceCleanedUp));
    Assert.IsTrue(harness.Tests.All(test => test.Run.SuiteId == BenchmarkSuiteIds.AgentBehavior));
    Assert.IsTrue(harness.Tests.All(test => test.Run.SuiteVersion == BenchmarkSuiteIds.AgentBehaviorVersion));

    var continuity = harness.Tests.Single(test => test.Run.TestId == BenchmarkIds.Continuity001);
    Assert.HasCount(3, continuity.RawResult.Turns!);
    Assert.AreEqual(100, continuity.RawResult.BehaviorMetrics!.ContinuityPreservation);
    Assert.AreEqual(3, continuity.RawResult.BehaviorMetrics.SuccessfulTerminalTurns);
    Assert.IsTrue(continuity.RawResult.Turns!.All(turn =>
      turn.ExecutionStatus == BenchmarkExecutionStatusIds.Completed));

    var scope = harness.Tests.Single(test => test.Run.TestId == BenchmarkIds.ScopeRetention001);
    Assert.AreEqual(100, scope.RawResult.BehaviorMetrics!.ScopeAccuracy);
    CollectionAssert.AreEqual(new[] { "src/target.txt" }, scope.RawResult.ChangedFiles!.ToArray());

    var recovery = harness.Tests.Single(test => test.Run.TestId == BenchmarkIds.Recovery001);
    Assert.AreEqual(100, recovery.RawResult.BehaviorMetrics!.Recovery);
    Assert.AreEqual("1", recovery.RawResult.ValidationFacts!["staleReadAttempts"]);
    Assert.IsGreaterThan(0, recovery.RawResult.RecoveredErrorCount ?? 0);

    var convergence = harness.Tests.Single(test => test.Run.TestId == BenchmarkIds.Convergence001);
    Assert.AreEqual(100, convergence.RawResult.BehaviorMetrics!.Convergence);
    Assert.AreEqual(0, convergence.RawResult.BehaviorMetrics.UnnecessaryToolCalls);
    Assert.AreEqual(0, convergence.RawResult.BehaviorMetrics.RepeatedValidationCount);

    var stale = harness.Tests.Single(test => test.Run.TestId == BenchmarkIds.StaleConflict001);
    Assert.HasCount(2, stale.RawResult.Turns!);
    Assert.HasCount(1, stale.RawResult.HostEvents!);
    Assert.AreEqual("external-file-mutation", stale.RawResult.HostEvents![0].Type);
    Assert.AreEqual("True", stale.RawResult.ValidationFacts!["externalChangePreserved"]);

    var truthful = harness.Tests.Single(test => test.Run.TestId == BenchmarkIds.TruthfulReport001);
    Assert.AreEqual("accurate", truthful.RawResult.BehaviorMetrics!.NarrationClassification);
    Assert.AreEqual(100, truthful.RawResult.BehaviorMetrics.TruthfulFinalReport);
    Assert.IsGreaterThan(0, truthful.RawResult.SurfacedErrorCount ?? 0);

    var persisted = await _environment.HttpClient.GetFromJsonAsync<BenchmarkSuiteRunResult>(
      $"api/benchmarks/suite-runs/{result.RunId}"
    );
    Assert.IsNotNull(persisted);
    Assert.AreEqual(
      JsonSerializer.Serialize(result),
      JsonSerializer.Serialize(persisted)
    );
    using var rescoreResponse = await _environment.HttpClient.PostAsync(
      $"api/benchmarks/suite-runs/{result.RunId}/rescore",
      null
    );
    rescoreResponse.EnsureSuccessStatusCode();
    var projection = await rescoreResponse.Content
      .ReadFromJsonAsync<BenchmarkScoringProjection>();
    Assert.IsNotNull(projection);
    Assert.AreEqual(harness.Score, projection.HarnessScores.Single().Score);
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CombinedCrudAndBehaviorSelectionRunsOnceAndPersistsInternalSuiteVersions()
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "alpha:latest",
        [HarnessIds.Native],
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true,
        Suites:
        [
          new BenchmarkSuiteSelection(
            BenchmarkSuiteIds.BasicCrud,
            BenchmarkSuiteIds.BasicCrudVersion
          ),
          new BenchmarkSuiteSelection(
            BenchmarkSuiteIds.AgentBehavior,
            BenchmarkSuiteIds.AgentBehaviorVersion
          )
        ]
      )
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(result);
    Assert.AreEqual(4, result.SchemaVersion);
    Assert.AreEqual(BenchmarkSuiteIds.Combined, result.SuiteId);
    Assert.HasCount(2, result.SelectedSuites!);
    Assert.HasCount(11, result.HarnessResults.Single().Tests);
    Assert.AreEqual(4, result.HarnessResults.Single().Tests.Count(test =>
      test.Run.SuiteId == BenchmarkSuiteIds.BasicCrud
      && test.Run.SuiteVersion == BenchmarkSuiteIds.BasicCrudVersion));
    Assert.AreEqual(7, result.HarnessResults.Single().Tests.Count(test =>
      test.Run.SuiteId == BenchmarkSuiteIds.AgentBehavior
      && test.Run.SuiteVersion == BenchmarkSuiteIds.AgentBehaviorVersion));
    var crudHistory = await _environment.HttpClient
      .GetFromJsonAsync<BenchmarkHistorySummary[]>(
        "api/benchmarks/history?limit=20&suite=basic-crud"
      );
    var behaviorHistory = await _environment.HttpClient
      .GetFromJsonAsync<BenchmarkHistorySummary[]>(
        "api/benchmarks/history?limit=20&suite=agent-behavior"
      );
    Assert.IsTrue(crudHistory!.Any(item => item.RunId == result.RunId));
    Assert.IsTrue(behaviorHistory!.Any(item => item.RunId == result.RunId));
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AgentBehaviorV2ReusesOneExternalHarnessSessionAcrossContinuityTurns()
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/runs",
      new BenchmarkRunRequest(
        BenchmarkIds.Continuity001,
        1,
        "alpha:latest",
        HarnessIds.Codex,
        ModelExecutionPermissionGranted: true
      )
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<BenchmarkRunResult>();
    Assert.IsNotNull(result);
    Assert.AreEqual(BenchmarkResultStatusIds.Pass, result.RawResult.Status);
    Assert.HasCount(3, result.RawResult.Turns!);
    Assert.AreEqual(100, result.RawResult.BehaviorMetrics!.ContinuityPreservation);
    Assert.IsTrue(result.RawResult.Turns!.All(turn =>
      turn.ExecutionStatus == BenchmarkExecutionStatusIds.Completed));
    Assert.IsTrue(result.WorkspaceCleanedUp);
    var threadIds = result.RawResult.Turns!
      .Select(turn => turn.FinalReport.Split(
        ' ',
        StringSplitOptions.RemoveEmptyEntries
      ).SingleOrDefault(part => part.StartsWith("fake-thread-", StringComparison.Ordinal))
        ?.TrimEnd('.'))
      .Where(threadId => threadId is not null)
      .Distinct(StringComparer.Ordinal)
      .ToArray();
    Assert.HasCount(1, threadIds);
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task AgentBehaviorV2RunsEveryExternalHarnessWithIdenticalScenarioInputs()
  {
    using var client = new HttpClient
    {
      BaseAddress = _environment.HttpClient.BaseAddress,
      Timeout = TimeSpan.FromSeconds(120)
    };
    using var response = await client.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "alpha:latest",
        [HarnessIds.Codex, HarnessIds.OpenCode, HarnessIds.QwenCode, HarnessIds.ClaudeCode],
        BenchmarkSuiteIds.AgentBehavior,
        BenchmarkSuiteIds.AgentBehaviorVersion,
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true
      )
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(result);
    Assert.HasCount(4, result.HarnessResults);
    Assert.IsTrue(result.HarnessResults.All(harness =>
      harness.Total == 7
      && harness.Tests.Count == 7
      && harness.Tests.All(test => test.WorkspaceCleanedUp)));
    foreach (var scenario in result.HarnessResults[0].Tests.Select(test => test.Run.TestId))
    {
      var comparable = result.HarnessResults.Select(harness =>
        harness.Tests.Single(test => test.Run.TestId == scenario)).ToArray();
      Assert.HasCount(1, comparable.Select(test => test.Run.Prompt).Distinct().ToArray());
      Assert.HasCount(1, comparable.Select(test => test.Run.FixtureFingerprint).Distinct().ToArray());
      Assert.IsTrue(comparable.All(test =>
        test.Run.SuiteId == BenchmarkSuiteIds.AgentBehavior
        && test.Run.SuiteVersion == BenchmarkSuiteIds.AgentBehaviorVersion));
    }
    Assert.IsTrue(result.HarnessResults.All(harness =>
      harness.Tests.Any(test => test.RawResult.Status != BenchmarkResultStatusIds.Pass)),
      "Fake external harness failures must remain per-scenario evidence without aborting the suite.");
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AgentBehaviorV2LiveProgressTimesOutOneScenarioAndContinuesToTruthfulReport()
  {
    var runId = Guid.NewGuid().ToString("N");
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs/live",
      new BenchmarkSuiteRunRequest(
        "unused:latest",
        [HarnessIds.Native],
        BenchmarkSuiteIds.AgentBehavior,
        BenchmarkSuiteIds.AgentBehaviorVersion,
        TimeoutSeconds: 5,
        ModelExecutionPermissionGranted: true,
        ClientRunId: runId
      )
    );
    Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
    BenchmarkLiveRunView? view = null;
    for (var attempt = 0; attempt < 300; attempt++)
    {
      view = await _environment.HttpClient.GetFromJsonAsync<BenchmarkLiveRunView>(
        $"api/benchmarks/suite-runs/{runId}/live"
      );
      if (view?.Terminal == true)
      {
        break;
      }
      await Task.Delay(100);
    }
    Assert.IsNotNull(view);
    Assert.IsTrue(view.Terminal);
    Assert.IsTrue(view.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.Activity
      && item.ActivityKind == BenchmarkActivityKindIds.Turn
      && item.TurnNumber > 0));
    Assert.IsTrue(view.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.Activity
      && item.ActivityKind == BenchmarkActivityKindIds.HostMutation));
    Assert.IsTrue(view.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.Activity
      && item.ActivityKind == BenchmarkActivityKindIds.RecoveredError));
    Assert.IsTrue(view.Events.Any(item =>
      item.Type == BenchmarkProgressTypeIds.TestState
      && item.TestId == BenchmarkIds.Convergence001
      && item.State == BenchmarkLiveStateIds.TimedOut));
    var final = view.Events.Single(item =>
      item.Type == BenchmarkProgressTypeIds.RunCompleted).FinalResult!;
    var tests = final.HarnessResults.Single().Tests;
    Assert.AreEqual(BenchmarkResultStatusIds.Error, tests.Single(test =>
      test.Run.TestId == BenchmarkIds.Convergence001).RawResult.Status);
    Assert.AreEqual(BenchmarkResultStatusIds.Pass, tests.Single(test =>
      test.Run.TestId == BenchmarkIds.Terminality001).RawResult.Status);
    Assert.AreEqual(BenchmarkResultStatusIds.Pass, tests.Single(test =>
      test.Run.TestId == BenchmarkIds.TruthfulReport001).RawResult.Status);
    Assert.HasCount(7, tests);
    _environment.FakeOllama.RemoveLoadedModel("unused:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AgentBehaviorV2ClassifiesMisleadingFinalNarrationFromHostReality()
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "beta:code",
        [HarnessIds.Native],
        BenchmarkSuiteIds.AgentBehavior,
        BenchmarkSuiteIds.AgentBehaviorVersion,
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true
      )
    );
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(result);
    var truthful = result.HarnessResults.Single().Tests.Single(test =>
      test.Run.TestId == BenchmarkIds.TruthfulReport001);
    Assert.IsTrue(truthful.RawResult.ObjectiveAchieved);
    Assert.AreEqual(BenchmarkResultStatusIds.Fail, truthful.RawResult.Status);
    Assert.AreEqual("misleading", truthful.RawResult.BehaviorMetrics!.NarrationClassification);
    Assert.AreEqual(0, truthful.RawResult.BehaviorMetrics.TruthfulFinalReport);
    _environment.FakeOllama.RemoveLoadedModel("beta:code");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutomatedBenchmarkUiSelectsHarnessesRanksAndOpensCrudEvidence()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Expect(Page.Locator("#benchmark-view")).ToBeVisibleAsync();
    await Expect(Page.Locator("#conversation-view")).ToBeHiddenAsync();
    await Expect(Page.Locator("#close-benchmarks"))
      .ToContainTextAsync("Back to conversation");
    await Expect(Page.Locator("#benchmark-suite-list input")).ToHaveCountAsync(2);
    await Expect(Page.Locator("#benchmark-suite-list input").First)
      .ToHaveAttributeAsync("role", "switch");
    await Page.Locator("#benchmark-suite-list input[value=\"basic-crud\"]")
      .CheckAsync();
    await Page.Locator("#benchmark-suite-list input[value=\"agent-behavior\"]")
      .UncheckAsync();
    await Expect(Page.Locator("#run-benchmark")).ToContainTextAsync("Run benchmark");
    await Expect(Page.Locator("#benchmark-harness-list input")).ToHaveCountAsync(5);
    await Expect(Page.Locator("#benchmark-harness-list input").First)
      .ToHaveAttributeAsync("role", "switch");
    await Expect(Page.Locator("#benchmark-harness-list input[value=\"qwen-code\"]"))
      .ToBeCheckedAsync();
    await Expect(Page.Locator("#benchmark-harness-list"))
      .ToContainTextAsync("Qwen Code [Experimental]");
    await Expect(Page.Locator("#benchmark-harness-list"))
      .ToContainTextAsync("0.21.13-fake");
    await Expect(Page.Locator("#benchmark-harness-list"))
      .ToContainTextAsync("2.1.234-fake (Claude Code)");

    await Expect(Page.Locator("#benchmark-model")).ToBeHiddenAsync();
    await Expect(Page.Locator("#benchmark-model-list")).ToBeVisibleAsync();
    await Expect(Page.Locator("#benchmark-model-list input").First)
      .ToHaveAttributeAsync("role", "switch");
    Assert.AreEqual(
      "auto",
      await Page.Locator("#benchmark-model-list")
        .EvaluateAsync<string>("element => getComputedStyle(element).overflowY")
    );
    foreach (var selector in new[]
    {
      "#benchmark-model-list input",
      "#benchmark-suite-list input",
      "#benchmark-harness-list input"
    })
    {
      var switchBox = await Page.Locator(selector).First.BoundingBoxAsync();
      Assert.IsNotNull(switchBox);
      Assert.IsLessThanOrEqualTo(
        36,
        switchBox.Width,
        $"Expected a compact switch for {selector}, but width was {switchBox.Width}."
      );
      Assert.IsLessThanOrEqualTo(
        22,
        switchBox.Height,
        $"Expected a compact switch for {selector}, but height was {switchBox.Height}."
      );
    }
    Assert.AreEqual(
      "grid",
      await Page.Locator("#benchmark-harness-list .benchmark-harness-option").First
        .EvaluateAsync<string>("element => getComputedStyle(element).display")
    );
    var nativeHarness = Page.Locator(
      "#benchmark-harness-list .benchmark-harness-option:has(input[value=\"native\"])"
    );
    var nativeNameBox = await nativeHarness.Locator("strong").BoundingBoxAsync();
    var nativeVersionBox = await nativeHarness.Locator("small").BoundingBoxAsync();
    Assert.IsNotNull(nativeNameBox);
    Assert.IsNotNull(nativeVersionBox);
    Assert.IsGreaterThanOrEqualTo(
      nativeNameBox.Y + nativeNameBox.Height - 1,
      nativeVersionBox.Y,
      "Harness version must render on its own line below the harness name."
    );

    var tooltipTrigger = Page.GetByRole(AriaRole.Button, new() { Name = "Timeout information" });
    await tooltipTrigger.DispatchEventAsync("pointerover");
    var tooltip = Page.Locator("#benchmark-floating-tooltip");
    await Expect(tooltip).ToBeVisibleAsync();
    var triggerBox = await tooltipTrigger.BoundingBoxAsync();
    var tooltipBox = await tooltip.BoundingBoxAsync();
    Assert.IsNotNull(triggerBox);
    Assert.IsNotNull(tooltipBox);
    var viewportWidth = await Page.EvaluateAsync<double>("window.innerWidth");
    Assert.IsGreaterThanOrEqualTo(0, tooltipBox.X);
    Assert.IsLessThanOrEqualTo(viewportWidth, tooltipBox.X + tooltipBox.Width);
    Assert.IsTrue(
      tooltipBox.Y >= triggerBox.Y + triggerBox.Height
        || tooltipBox.Y + tooltipBox.Height <= triggerBox.Y,
      "Benchmark tooltip must not cover its trigger."
    );

    await SelectBenchmarkModelsAsync("alpha:latest");
    await Page.Locator("#benchmark-harness-list input[value=\"native\"]").UncheckAsync();
    await Page.Locator("#benchmark-timeout").FillAsync("20");
    await Page.Locator("#run-benchmark").ClickAsync();

    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("Benchmark completed", new() { Timeout = 30_000 });
    await Expect(Page.Locator("#benchmark-results-body tr")).ToHaveCountAsync(4);
    await Expect(Page.Locator("#benchmark-run-summary")).ToContainTextAsync("passed");
    await Expect(Page.Locator("#benchmark-results-body")).ToContainTextAsync("4/4");
    await Expect(Page.Locator("#benchmark-history")).Not.ToHaveValueAsync("");
    await Expect(Page.Locator("#benchmark-score-profile")).ToHaveTextAsync("Default v1");
    await Expect(Page.Locator("#benchmark-weight-total")).ToContainTextAsync("Total 100");

    await Page.Locator(".benchmark-scoring-advanced > summary").ClickAsync();
    await Page.Locator("#benchmark-weight-objective").FillAsync("0");
    await Page.Locator("#benchmark-weight-correctness").FillAsync("100");
    await Page.Locator("#benchmark-weight-terminality").FillAsync("0");
    await Page.Locator("#benchmark-weight-workspace").FillAsync("0");
    await Page.Locator("#benchmark-weight-efficiency").FillAsync("0");
    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("ranking recalculated", new() { Timeout = 10_000 });
    await Expect(Page.Locator("#benchmark-score-profile")).ToHaveTextAsync("Custom v1");
    await Expect(Page.Locator("#benchmark-score-context")).ToContainTextAsync("Measured evidence unchanged");

    await Page.Locator("#benchmark-results-body [data-harness=\"codex\"]").ClickAsync();
    await Expect(Page.Locator("#benchmark-result-detail .benchmark-test-detail"))
      .ToHaveCountAsync(4);
    var update = Page.Locator("#benchmark-result-detail .benchmark-test-detail")
      .Filter(new() { HasText = BenchmarkIds.FileSystemUpdate001 });
    await update.Locator("summary").ClickAsync();
    await Expect(update).ToContainTextAsync("Host validation");
    await Expect(update).ToContainTextAsync("fixture/update.txt");
    await Expect(update).ToContainTextAsync("FS-UPDATE-001");
    await Expect(Page.Locator("#benchmark-result-detail")).ToContainTextAsync("Calculated score");
    await Expect(Page.Locator("#benchmark-result-detail")).ToContainTextAsync("Measured evidence");
    await Page.Locator("#benchmark-results-body [data-harness=\"qwen-code\"]").ClickAsync();
    await Expect(Page.Locator("#benchmark-result-detail")).ToContainTextAsync("Qwen Code");
    await Expect(Page.Locator("#benchmark-result-detail .benchmark-test-detail"))
      .ToHaveCountAsync(4);
    await Page.Locator("#benchmark-results-body [data-harness=\"claude-code\"]").ClickAsync();
    await Expect(Page.Locator("#benchmark-result-detail")).ToContainTextAsync("Claude Code");
    await Page.Locator("#reset-benchmark-weights").ClickAsync();
    await Expect(Page.Locator("#benchmark-score-profile")).ToHaveTextAsync("Default v1");
    await Expect(Page.Locator("#benchmark-status")).ToContainTextAsync("Default profile restored");
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  private async Task SelectBenchmarkModelsAsync(params string[] selectedModels)
  {
    var modelSwitches = Page.Locator("#benchmark-model-list input");
    await modelSwitches.First.WaitForAsync();
    for (var index = 0; index < await modelSwitches.CountAsync(); index++)
    {
      var modelSwitch = modelSwitches.Nth(index);
      var model = await modelSwitch.GetAttributeAsync("value");
      await modelSwitch.SetCheckedAsync(
        model is not null && selectedModels.Contains(model, StringComparer.Ordinal)
      );
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BenchmarkUiRunsAndReloadsInspectableTwoModelMatrixRankings()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#open-benchmarks").ClickAsync();
    await SelectBenchmarkModelsAsync(
      "alpha:latest",
      "beta:code"
    );
    await Page.Locator("#benchmark-suite-list input[value=\"basic-crud\"]")
      .CheckAsync();
    await Page.Locator("#benchmark-suite-list input[value=\"agent-behavior\"]")
      .UncheckAsync();
    foreach (var harness in new[]
    {
      HarnessIds.Codex,
      HarnessIds.OpenCode,
      HarnessIds.QwenCode,
      HarnessIds.ClaudeCode
    })
    {
      await Page.Locator($"#benchmark-harness-list input[value=\"{harness}\"]")
        .UncheckAsync();
    }
    await Page.Locator("#benchmark-timeout").FillAsync("20");
    await Page.Locator("#run-benchmark").ClickAsync();
    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("Benchmark completed", new() { Timeout = 30_000 });
    await Expect(Page.Locator("#benchmark-matrix")).ToBeVisibleAsync();
    await Expect(Page.Locator("#benchmark-matrix tbody tr")).ToHaveCountAsync(2);
    await Expect(Page.Locator("#benchmark-matrix .benchmark-matrix-cell"))
      .ToHaveCountAsync(2);
    await Expect(Page.Locator("#benchmark-run-summary")).ToContainTextAsync("2 models × 1 harnesses");
    await Page.Locator("#benchmark-matrix [data-model=\"beta:code\"][data-harness=\"native\"]")
      .ClickAsync();
    await Expect(Page.Locator("#benchmark-result-detail"))
      .ToContainTextAsync("beta:code × Native");
    await Expect(Page.Locator("#benchmark-result-detail .benchmark-test-detail"))
      .ToHaveCountAsync(4);

    await Page.Locator("#benchmark-ranking-scope").SelectOptionAsync("model");
    await Expect(Page.Locator("#benchmark-results-body tr")).ToHaveCountAsync(2);
    await Page.Locator("#benchmark-ranking-scope").SelectOptionAsync("harness");
    await Expect(Page.Locator("#benchmark-results-body tr")).ToHaveCountAsync(1);
    await Page.Locator("#benchmark-ranking-scope").SelectOptionAsync("pair");
    await Expect(Page.Locator("#benchmark-results-body tr")).ToHaveCountAsync(2);

    var persistedRun = await Page.Locator("#benchmark-history").InputValueAsync();
    Assert.IsFalse(string.IsNullOrWhiteSpace(persistedRun));
    await Page.ReloadAsync();
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Page.Locator(".benchmark-history-advanced > summary").ClickAsync();
    await Page.Locator("#benchmark-history").SelectOptionAsync(persistedRun);
    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("Persisted result loaded");
    await Expect(Page.Locator("#benchmark-matrix tbody tr")).ToHaveCountAsync(2);
    await Expect(Page.Locator("#benchmark-recommendation-results")).ToBeVisibleAsync();
    await Expect(Page.Locator("#benchmark-result-detail"))
      .ToContainTextAsync("Measured evidence");
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
    _environment.FakeOllama.RemoveLoadedModel("beta:code");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LiveBenchmarkUiRendersProgressReopensAndSettlesCancellation()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#open-benchmarks").ClickAsync();
    await SelectBenchmarkModelsAsync("docs:latest");
    await Page.Locator("#benchmark-suite-list input[value=\"basic-crud\"]")
      .CheckAsync();
    await Page.Locator("#benchmark-suite-list input[value=\"agent-behavior\"]")
      .UncheckAsync();
    await Page.Locator("#benchmark-harness-list input[value=\"codex\"]").UncheckAsync();
    await Page.Locator("#benchmark-harness-list input[value=\"opencode\"]").UncheckAsync();
    await Page.Locator("#benchmark-harness-list input[value=\"qwen-code\"]").UncheckAsync();
    await Page.Locator("#benchmark-harness-list input[value=\"claude-code\"]").UncheckAsync();
    await Page.Locator("#benchmark-timeout").FillAsync("60");
    await Page.Locator("#run-benchmark").ClickAsync();

    await Expect(Page.Locator("#benchmark-live-dashboard")).ToBeVisibleAsync();
    await Expect(Page.Locator("#benchmark-ranking-note")).ToBeVisibleAsync();
    await Expect(Page.Locator(".benchmark-live-harness")).ToHaveCountAsync(1);
    await Expect(Page.Locator("#benchmark-live-dashboard"))
      .ToContainTextAsync(BenchmarkIds.FileSystemRead001, new() { Timeout = 15_000 });
    await Expect(Page.Locator("#benchmark-live-dashboard"))
      .ToContainTextAsync("file-create", new() { Timeout = 15_000 });

    await Page.Locator("#close-benchmarks").ClickAsync();
    await Expect(Page.Locator("#benchmark-view")).ToBeHiddenAsync();
    await Expect(Page.Locator("#conversation-view")).ToBeVisibleAsync();
    await Page.EvaluateAsync("document.getElementById('open-benchmarks').click()");
    await Expect(Page.Locator("#benchmark-view")).ToBeVisibleAsync();
    await Expect(Page.Locator("#conversation-view")).ToBeHiddenAsync();
    await Expect(Page.Locator("#benchmark-live-dashboard")).ToBeVisibleAsync();
    await Expect(Page.Locator("#cancel-benchmark")).ToBeEnabledAsync();
    await Page.Locator("#cancel-benchmark").ClickAsync();
    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("canceled", new() { Timeout = 15_000 });
    await Expect(Page.Locator("#benchmark-live-dashboard")).ToBeHiddenAsync();
    await Expect(Page.Locator("#benchmark-ranking-note")).ToBeHiddenAsync();
    await Expect(Page.Locator("#benchmark-run-summary")).ToContainTextAsync("cancelled");
    await Expect(Page.Locator("#benchmark-history")).Not.ToHaveValueAsync("");
    _environment.FakeOllama.RemoveLoadedModel("docs:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutomatedBenchmarkRecordsUnavailableHarnessCellWithoutSubstitution()
  {
    var missing = Path.Combine(
      Path.GetTempPath(),
      "agentic-router-e2e",
      Guid.NewGuid().ToString("N"),
      "missing-opencode.exe"
    );
    try
    {
      await _environment.SetOpenCodeExecutableAndRestartAsync(missing);
      using var response = await _environment.HttpClient.PostAsJsonAsync(
        "api/benchmarks/suite-runs",
        new BenchmarkSuiteRunRequest(
          "alpha:latest",
          [HarnessIds.OpenCode],
          TimeoutSeconds: 20,
          ModelExecutionPermissionGranted: true
        )
      );
      response.EnsureSuccessStatusCode();
      var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
      Assert.IsNotNull(result);
      Assert.HasCount(1, result.Cells!);
      Assert.AreEqual(BenchmarkMatrixCellStatusIds.Unavailable, result.Cells![0].Status);
      Assert.AreEqual("alpha:latest", result.Cells[0].Model);
      Assert.AreEqual(HarnessIds.OpenCode, result.Cells[0].Harness);
      Assert.IsNull(result.Cells[0].Result);
    }
    finally
    {
      await _environment.SetOpenCodeExecutableAndRestartAsync(
        _environment.FakeOpenCodeExecutablePath
      );
    }

    try
    {
      await _environment.SetQwenCodeExecutableAndRestartAsync(missing);
      using var response = await _environment.HttpClient.PostAsJsonAsync(
        "api/benchmarks/suite-runs",
        new BenchmarkSuiteRunRequest(
          "alpha:latest",
          [HarnessIds.QwenCode],
          TimeoutSeconds: 20,
          ModelExecutionPermissionGranted: true
        )
      );
      response.EnsureSuccessStatusCode();
      var result = await response.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
      Assert.IsNotNull(result);
      Assert.HasCount(1, result.Cells!);
      Assert.AreEqual(BenchmarkMatrixCellStatusIds.Unavailable, result.Cells![0].Status);
      Assert.AreEqual("alpha:latest", result.Cells[0].Model);
      Assert.AreEqual(HarnessIds.QwenCode, result.Cells[0].Harness);
      Assert.IsNull(result.Cells[0].Result);
    }
    finally
    {
      await _environment.SetQwenCodeExecutableAndRestartAsync(
        _environment.FakeQwenCodeExecutablePath
      );
    }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AutomatedBenchmarkContinuesAfterFailureAndTimeoutAndCancelsCleanly()
  {
    using var failureResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "structured-failure:latest",
        [HarnessIds.Codex],
        TimeoutSeconds: 20,
        ModelExecutionPermissionGranted: true
      )
    );
    failureResponse.EnsureSuccessStatusCode();
    var failure = await failureResponse.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(failure);
    Assert.AreEqual("completed", failure.TerminalState);
    Assert.AreEqual("completed-with-failures", failure.FinalStatus);
    Assert.AreEqual(BenchmarkMatrixCellStatusIds.Failed, failure.Cells!.Single().Status);
    Assert.HasCount(4, failure.HarnessResults.Single().Tests);
    Assert.AreEqual(
      "ERROR",
      failure.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemUpdate001
      ).RawResult.Status
    );
    Assert.AreEqual(
      "PASS",
      failure.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemDelete001
      ).RawResult.Status
    );

    using var timeoutResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "unused:latest",
        [HarnessIds.Native],
        TimeoutSeconds: 5,
        ModelExecutionPermissionGranted: true
      )
    );
    timeoutResponse.EnsureSuccessStatusCode();
    var timeout = await timeoutResponse.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(timeout);
    Assert.AreEqual(BenchmarkMatrixCellStatusIds.TimedOut, timeout.Cells!.Single().Status);
    Assert.HasCount(4, timeout.HarnessResults.Single().Tests);
    Assert.AreEqual(
      "timed-out",
      timeout.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemRead001
      ).RawResult.ExecutionStatus
    );
    Assert.AreEqual(
      "PASS",
      timeout.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemDelete001
      ).RawResult.Status
    );

    using var qwenTimeoutResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "unused:latest",
        [HarnessIds.QwenCode],
        TimeoutSeconds: 5,
        ModelExecutionPermissionGranted: true
      )
    );
    qwenTimeoutResponse.EnsureSuccessStatusCode();
    var qwenTimeout = await qwenTimeoutResponse.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(qwenTimeout);
    Assert.AreEqual(
      BenchmarkExecutionStatusIds.TimedOut,
      qwenTimeout.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemRead001
      ).RawResult.ExecutionStatus
    );
    Assert.AreEqual(
      BenchmarkResultStatusIds.Pass,
      qwenTimeout.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemDelete001
      ).RawResult.Status
    );

    using var claudeTimeoutResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "unused:latest",
        [HarnessIds.ClaudeCode],
        TimeoutSeconds: 5,
        ModelExecutionPermissionGranted: true
      )
    );
    claudeTimeoutResponse.EnsureSuccessStatusCode();
    var claudeTimeout = await claudeTimeoutResponse.Content
      .ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(claudeTimeout);
    Assert.AreEqual(
      BenchmarkExecutionStatusIds.TimedOut,
      claudeTimeout.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemRead001
      ).RawResult.ExecutionStatus
    );
    Assert.AreEqual(
      BenchmarkResultStatusIds.Pass,
      claudeTimeout.HarnessResults.Single().Tests.Single(
        item => item.Run.TestId == BenchmarkIds.FileSystemDelete001
      ).RawResult.Status
    );

    var cancelledRunId = Guid.NewGuid().ToString("N");
    var runTask = _environment.HttpClient.PostAsJsonAsync(
      "api/benchmarks/suite-runs",
      new BenchmarkSuiteRunRequest(
        "docs:latest",
        [HarnessIds.Native],
        TimeoutSeconds: 60,
        ModelExecutionPermissionGranted: true,
        ClientRunId: cancelledRunId
      )
    );
    await Task.Delay(750);
    using var cancelResponse = await _environment.HttpClient.PostAsync(
      $"api/benchmarks/suite-runs/{cancelledRunId}/cancel",
      null
    );
    Assert.AreEqual(HttpStatusCode.Accepted, cancelResponse.StatusCode);
    using var cancelledResponse = await runTask;
    cancelledResponse.EnsureSuccessStatusCode();
    var cancelled = await cancelledResponse.Content.ReadFromJsonAsync<BenchmarkSuiteRunResult>();
    Assert.IsNotNull(cancelled);
    Assert.AreEqual("cancelled", cancelled.TerminalState);
    Assert.AreEqual("cancelled", cancelled.FinalStatus);
    Assert.HasCount(
      1,
      cancelled.HarnessResults.SelectMany(item => item.Tests).Where(
        item => item.RawResult.ExecutionStatus == "cancelled"
      )
    );
    Assert.IsTrue(File.Exists(Path.Combine(
      _environment.DataDirectory,
      "benchmark-results",
      cancelledRunId + ".json"
    )));
    _environment.FakeOllama.RemoveLoadedModel("structured-failure:latest");
    _environment.FakeOllama.RemoveLoadedModel("unused:latest");
    _environment.FakeOllama.RemoveLoadedModel("docs:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ComposerOffersNativeAndExperimentalHarnessesWithoutModelLock()
  {
    await Page.GotoAsync("/");

    await Expect(Page.Locator("#model-lock")).ToHaveCountAsync(0);
    var harness = Page.Locator("#harness-selector");
    await Expect(harness).ToHaveValueAsync("native");
    await Expect(harness).ToBeEnabledAsync();
    await Expect(harness.Locator("option[value=\"auto-model-harness\"]"))
      .ToHaveAttributeAsync("disabled", "");
    await Expect(harness.Locator("option")).ToHaveTextAsync(
      new[]
      {
        "Auto Model × Harness",
        "Native",
        "Claude Code [Experimental]",
        "Codex (Experimental)",
        "OpenCode [Experimental]",
        "Qwen Code [Experimental]"
      }
    );
    await Expect(harness.Locator("option[value=\"codex\"]"))
      .ToHaveAttributeAsync("title", new Regex("codex-cli fake-0.148.0"));

    await Page.Locator("[data-mode=\"execute\"]").ClickAsync();
    await Expect(harness).ToBeEnabledAsync();
    await harness.SelectOptionAsync("codex");
    await Expect(Page.Locator("#composer-status")).ToContainTextAsync(
      "Codex (Experimental)"
    );
    await harness.SelectOptionAsync("opencode");
    await Expect(Page.Locator("#composer-status")).ToContainTextAsync(
      "OpenCode [Experimental]"
    );
    await harness.SelectOptionAsync("claude-code");
    await Expect(Page.Locator("#composer-status")).ToContainTextAsync(
      "Claude Code [Experimental]"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeUsesStructuredSdkStreamExactOllamaWorkspaceAndContinuity()
  {
    var browserSessionId = $"browser-claude-{Guid.NewGuid():N}";
    var first = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude deterministic turn",
      browserSessionId,
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, first.Where(IsTerminalStreamEvent));
    Assert.IsTrue(first.Any(item =>
      item["type"]!.GetValue<string>() == "reasoning.delta"
      && item["reasoningDelta"]!.GetValue<string>().Contains(
        "Inspecting Claude Code workspace",
        StringComparison.Ordinal
      )
    ));
    Assert.IsTrue(first.Any(item =>
      item["type"]!.GetValue<string>() == "response.delta"
      && item["delta"]!.GetValue<string>().Contains(
        "Claude Code streamed with qwen3.8:27b-gpu0",
        StringComparison.Ordinal
      )
    ));
    Assert.IsTrue(first.Any(item =>
      item["type"]!.GetValue<string>() == "harness.claude-code-native-event-preserved"
    ));
    var exactUsage = first.Last(item =>
      item["type"]!.GetValue<string>() == "context.usage"
      && item["contextUsage"]!["accuracy"]!.GetValue<string>() == "exact"
    );
    Assert.AreEqual(
      155,
      exactUsage["contextUsage"]!["activeContextTokens"]!.GetValue<long>()
    );

    var runtime = Path.Combine(_environment.DataDirectory, "claude-code-runtime");
    using (var invocation = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-claude-invocation.json"))
    ))
    {
      Assert.AreEqual("qwen3.8:27b-gpu0", invocation.RootElement.GetProperty("model").GetString());
      Assert.AreEqual(
        Path.GetFullPath(_environment.WorkspaceDirectory),
        Path.GetFullPath(invocation.RootElement.GetProperty("cwd").GetString()!),
        true
      );
      Assert.AreEqual("ollama", invocation.RootElement.GetProperty("anthropicAuthToken").GetString());
      Assert.AreEqual(JsonValueKind.Null, invocation.RootElement.GetProperty("claudeCodeUseBedrock").ValueKind);
      Assert.AreEqual(JsonValueKind.Null, invocation.RootElement.GetProperty("claudeCodeUseVertex").ValueKind);
      Assert.AreEqual(JsonValueKind.Null, invocation.RootElement.GetProperty("claudeCodeUseFoundry").ValueKind);
      Assert.AreEqual("1", invocation.RootElement.GetProperty("nonessentialTrafficDisabled").GetString());
      Assert.IsTrue(invocation.RootElement.GetProperty("hostTokenConfigured").GetBoolean());
      var arguments = invocation.RootElement.GetProperty("args")
        .EnumerateArray().Select(item => item.GetString()).ToArray();
      CollectionAssert.Contains(arguments, "stream-json");
      CollectionAssert.Contains(arguments, "--strict-mcp-config");
      CollectionAssert.Contains(arguments, "Read,Glob,Grep,Edit,Write");
      Assert.DoesNotContain("Bash", arguments);
      Assert.DoesNotContain("WebSearch", arguments);
      var allowedToolsIndex = Array.IndexOf(arguments, "--allowedTools");
      Assert.IsGreaterThanOrEqualTo(0, allowedToolsIndex);
      Assert.IsFalse(arguments[allowedToolsIndex + 1]!.Contains("Read", StringComparison.Ordinal));
      Assert.IsTrue(arguments[allowedToolsIndex + 1]!.Contains(
        "mcp__agentic_router__",
        StringComparison.Ordinal
      ));
    }

    var second = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude continuity follow-up",
      browserSessionId,
      "qwen3.8:27b-gpu0",
      [
        new { role = "user", content = "claude deterministic turn" },
        new { role = "assistant", content = "Claude Code streamed with qwen3.8:27b-gpu0" }
      ]
    );
    Assert.HasCount(1, second.Where(IsTerminalStreamEvent));
    using var resumed = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-claude-invocation.json"))
    );
    Assert.IsTrue(resumed.RootElement.GetProperty("resumed").GetBoolean());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeAcceptsReducedNativeInventoryAndKeepsHostBridge()
  {
    var events = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "reduced claude inventory",
      $"browser-claude-reduced-tools-{Guid.NewGuid():N}"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsTrue(events.Any(item =>
      item["type"]!.GetValue<string>() == "harness.claude-code-warning"
      && item["message"]!.GetValue<string>().Contains("Glob, Grep, Write", StringComparison.Ordinal)
    ));
    Assert.IsTrue(events.Any(item =>
      item["type"]!.GetValue<string>() == "response.completed"
    ));
    Assert.IsTrue(events.Any(item =>
      item["type"]!.GetValue<string>() == "execution-capability-profile-projected"
      && item["message"]!.GetValue<string>().Contains("list_files", StringComparison.Ordinal)
      && item["message"]!.GetValue<string>().Contains("search_text", StringComparison.Ordinal)
      && item["message"]!.GetValue<string>().Contains("write_file", StringComparison.Ordinal)
    ));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeRecoversFromTypedHostGitFailureAndTerminatesOnce()
  {
    await RunGitAsync(
      "init",
      "-b",
      "main"
    );
    var events = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude git failure recovery",
      $"browser-claude-git-recovery-{Guid.NewGuid():N}"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var eventDump = string.Join(
      Environment.NewLine,
      events.Select(item => item.ToJsonString())
    );
    Assert.IsTrue(events.Any(item =>
      item["type"]!.GetValue<string>() == "action.execution-error"
      && item["localAction"]?["resultOutput"]?.GetValue<string>().Contains(
        "git-commit-failed",
        StringComparison.Ordinal
      ) == true
    ), eventDump);
    Assert.IsTrue(events.Any(item =>
      item["type"]!.GetValue<string>() == "response.completed"
    ));
    Assert.IsFalse(events.Any(item => item["type"]!.GetValue<string>() == "error"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeEnvelopeStormIsBoundedAndPreservesCompleteReasoning()
  {
    var events = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude envelope storm",
      $"browser-claude-storm-{Guid.NewGuid():N}"
    );

    var reasoningDeltas = events.Where(item =>
      item["type"]!.GetValue<string>() == "reasoning.delta"
    ).ToArray();
    var reasoning = string.Concat(reasoningDeltas.Select(item =>
      item["reasoningDelta"]?.GetValue<string>() ?? string.Empty
    ));
    var nativeEvents = events.Where(item =>
      item["type"]!.GetValue<string>() == "harness.claude-code-native-event-preserved"
    ).ToArray();

    Assert.IsTrue(reasoning.Contains(new string('x', 1_000), StringComparison.Ordinal));
    Assert.IsLessThanOrEqualTo(10, reasoningDeltas.Length);
    Assert.HasCount(2, nativeEvents, "Only init and the intentionally unknown future event should be preserved.");
    Assert.IsLessThan(60, events.Length, "Known Claude transport frames must not flood the Host stream.");
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeMapsNativeApprovalAndFailsMalformedStreamExactlyOnce()
  {
    var approved = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude approval",
      $"browser-claude-approval-{Guid.NewGuid():N}"
    );
    Assert.HasCount(1, approved.Where(IsTerminalStreamEvent));
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "claude-approved.txt")));
    using var approval = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
      _environment.DataDirectory,
      "claude-code-runtime",
      "fake-claude-approval.json"
    )));
    Assert.AreEqual("allow", approval.RootElement.GetProperty("behavior").GetString());

    var malformed = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "malformed claude event",
      $"browser-claude-malformed-{Guid.NewGuid():N}"
    );
    Assert.HasCount(1, malformed.Where(IsTerminalStreamEvent));
    Assert.IsTrue(malformed.Any(item =>
      item["type"]!.GetValue<string>() == "error"
      && item["error"]!["code"]!.GetValue<string>() == "claude-code-protocol-json"
      && item["error"]!["stage"]!.GetValue<string>() == "claude-code-harness"
      && item["error"]!["details"]!["harnessId"]!.GetValue<string>() == HarnessIds.ClaudeCode
    ));

    var duplicate = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "duplicate claude terminal",
      $"browser-claude-duplicate-{Guid.NewGuid():N}"
    );
    Assert.HasCount(1, duplicate.Where(IsTerminalStreamEvent));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeCancellationStopsTheOwnedProcessAndTerminatesOnce()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(HarnessIds.ClaudeCode);

    await StartMessageAsync("long claude task");
    var invocationPath = Path.Combine(
      _environment.DataDirectory,
      "claude-code-runtime",
      "fake-claude-invocation.json"
    );
    await WaitUntilAsync(() => File.Exists(invocationPath), TimeSpan.FromSeconds(15));
    await Page.Locator("#cancel-request").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Send");
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    using var invocation = JsonDocument.Parse(await File.ReadAllTextAsync(invocationPath));
    var processId = invocation.RootElement.GetProperty("processId").GetInt32();
    await WaitUntilAsync(() => !ProcessIsAlive(processId), TimeSpan.FromSeconds(5));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeAskUsesTheExistingHostApprovalBeforeNativeWrite()
  {
    var target = Path.Combine(_environment.WorkspaceDirectory, "claude-approved.txt");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("ask");
    await Page.Locator("#harness-selector").SelectOptionAsync(HarnessIds.ClaudeCode);

    await StartMessageAsync("claude approval");
    var approval = Page.Locator(".action-approval").Last;
    await Expect(approval).ToBeVisibleAsync();
    await Expect(approval).ToContainTextAsync("claude-approved.txt");
    Assert.IsFalse(File.Exists(target));
    await approval.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    Assert.IsTrue(File.Exists(target));
    await Expect(Page.Locator(".action-approval")).ToHaveCountAsync(1);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodeRejectsExternalNativeReadAndRecoversInsideWorkspace()
  {
    var events = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude workspace recovery",
      $"browser-claude-workspace-recovery-{Guid.NewGuid():N}"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsTrue(events.Any(item =>
      item["type"]!.GetValue<string>() == "harness.claude-code-approval-corrected"
    ));
    using var recovery = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
      _environment.DataDirectory,
      "claude-code-runtime",
      "fake-claude-workspace-recovery.json"
    )));
    Assert.AreEqual("deny", recovery.RootElement.GetProperty("outsideBehavior").GetString());
    Assert.AreEqual("allow", recovery.RootElement.GetProperty("insideBehavior").GetString());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodePreservesAbsoluteNestedWindowsWorkspacePathWithSpaces()
  {
    var workspace = _environment.CreateWorkspaceDirectory(
      Path.Combine("Claude workspace with spaces", "nested project")
    );
    using var created = await _environment.HttpClient.PostAsJsonAsync(
      "api/workspaces",
      new { name = "Claude path test", path = workspace }
    );
    created.EnsureSuccessStatusCode();
    using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
    var workspaceId = createdDocument.RootElement.GetProperty("id").GetString()!;
    using var activated = await _environment.HttpClient.PostAsync(
      $"api/workspaces/{workspaceId}/activate",
      null
    );
    activated.EnsureSuccessStatusCode();

    var events = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude path turn",
      $"browser-claude-path-{Guid.NewGuid():N}"
    );
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsTrue(Path.IsPathFullyQualified(workspace));
    using var invocation = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
      _environment.DataDirectory,
      "claude-code-runtime",
      "fake-claude-invocation.json"
    )));
    Assert.AreEqual(
      Path.GetFullPath(workspace),
      Path.GetFullPath(invocation.RootElement.GetProperty("cwd").GetString()!),
      true
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeHarnessUsesIsolatedAuthenticatedServerAndExactWorkspaceModel()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("opencode");

    await SendMessageAsync("opencode deterministic turn");

    var assistant = Page.Locator(".message.assistant").Last;
    await Expect(assistant.Locator(".assistant-reasoning-body"))
      .ToContainTextAsync("Inspecting OpenCode workspace");
    await Expect(assistant.Locator(".assistant-answer"))
      .ToContainTextAsync("OpenCode streamed with qwen3.8:27b-gpu0");
    await Expect(assistant.Locator(".assistant-answer"))
      .Not.ToContainTextAsync("Internal reasoning stays in Thinking");
    await Expect(assistant.Locator(".assistant-answer"))
      .Not.ToContainTextAsync("Agentic Router context for this turn");
    await Expect(Page.Locator("#context-usage-summary"))
      .ToContainTextAsync("exact");
    await Expect(assistant.Locator(".activity"))
      .ToHaveAttributeAsync("data-terminal", "true");
    var capabilityProjection = assistant.Locator(
      "[data-event-type=\"execution-capability-profile-projected\"]"
    );
    await Expect(capabilityProjection).ToContainTextAsync(
      "native implementation [create_file, write_file, replace_text, apply_patch]"
    );
    await Expect(capabilityProjection).ToContainTextAsync("Host bridge [create_execution_plan, revise_execution_plan, list_files, read_file");
    await Expect(capabilityProjection).ToContainTextAsync("delete_paths");
    await Expect(capabilityProjection).ToContainTextAsync("run_process");
    await Expect(capabilityProjection).ToContainTextAsync("missing adapter []");
    var runtime = Path.Combine(_environment.DataDirectory, "opencode-runtime");
    using var session = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-opencode-session.json"))
    );
    Assert.AreEqual(
      Path.GetFullPath(_environment.WorkspaceDirectory),
      Path.GetFullPath(session.RootElement.GetProperty("directory").GetString()!)
    );
    Assert.AreEqual(
      "qwen3.8:27b-gpu0",
      session.RootElement.GetProperty("model").GetString()
    );
    Assert.AreEqual(
      "agentic-router-ollama",
      session.RootElement.GetProperty("provider").GetString()
    );
    Assert.IsTrue(session.RootElement.GetProperty("passwordConfigured").GetBoolean());
    StringAssert.Contains(
      session.RootElement.GetProperty("stateRoot").GetString(),
      "opencode-runtime"
    );
    var config = await File.ReadAllTextAsync(
      Path.Combine(runtime, "config", "opencode", "opencode.json")
    );
    StringAssert.Contains(config, "http://127.0.0.1:");
    StringAssert.Contains(config, "qwen3.8:27b-gpu0");
    StringAssert.Contains(config, "\"bash\": \"deny\"");
    StringAssert.Contains(config, "\"read\": \"deny\"");
    StringAssert.Contains(config, "\"glob\": \"deny\"");
    StringAssert.Contains(config, "\"grep\": \"deny\"");
    StringAssert.Contains(config, "\"list\": \"deny\"");
    StringAssert.Contains(config, "\"webfetch\": \"allow\"");
    StringAssert.Contains(config, "\"websearch\": \"allow\"");
    StringAssert.Contains(config, "\"agentic_router\"");
    StringAssert.Contains(config, "\"agentic_router_*\": \"allow\"");

    using var firstPrompt = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-opencode-prompt.json"))
    );
    var firstSessionId = firstPrompt.RootElement.GetProperty("sessionId").GetString();
    await SendMessageAsync("opencode second deterministic turn");
    using var secondPrompt = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-opencode-prompt.json"))
    );
    Assert.AreEqual(
      firstSessionId,
      secondPrompt.RootElement.GetProperty("sessionId").GetString()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeOwnedProcessIsCleanedUpWhenApplicationRestarts()
  {
    var events = await ExecuteHarnessStreamAsync(
      "opencode",
      "opencode cleanup turn",
      "browser-opencode-cleanup",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));

    var processId = int.Parse(
      await File.ReadAllTextAsync(Path.Combine(
        _environment.DataDirectory,
        "opencode-runtime",
        "fake-opencode-process-id.txt"
      )),
      System.Globalization.CultureInfo.InvariantCulture
    );
    Assert.IsTrue(ProcessIsAlive(processId));

    await _environment.RestartApplicationAsync();
    await WaitUntilAsync(
      () => !ProcessIsAlive(processId),
      TimeSpan.FromSeconds(5)
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeToolEventsAreNormalized()
  {
    var events = await ExecuteHarnessStreamAsync(
      "opencode",
      "opencode tool events",
      "browser-opencode-tools",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.started")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.completed")
    );
    var contextEvents = events.Where(
      item => item["type"]!.GetValue<string>() == "context.usage"
    ).ToArray();
    Assert.IsGreaterThanOrEqualTo(2, contextEvents.Length);
    Assert.AreEqual(
      "estimated",
      contextEvents.First()["contextUsage"]!["accuracy"]!.GetValue<string>()
    );
    Assert.IsTrue(contextEvents.Any(item =>
      item["contextUsage"]!["accuracy"]!.GetValue<string>() == "estimated"
      && item["contextUsage"]!["activeContextTokens"]!.GetValue<long>()
        > item["contextUsage"]!["inputTokens"]!.GetValue<long>()
    ));
    Assert.AreEqual(
      "exact",
      contextEvents.Last()["contextUsage"]!["accuracy"]!.GetValue<string>()
    );
    Assert.AreEqual(
      1_234,
      contextEvents.Last()["contextUsage"]!["inputTokens"]!.GetValue<long>()
    );
    Assert.AreEqual(
      1_274,
      contextEvents.Last()["contextUsage"]!["activeContextTokens"]!.GetValue<long>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeSessionDiffAndUnexpectedNativeEventArePreserved()
  {
    var diff = await ExecuteHarnessStreamAsync(
      "opencode",
      "diff opencode",
      "browser-opencode-diff",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, diff.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      diff.Where(
        item => item["type"]!.GetValue<string>()
          == "harness.opencode-files-changed"
      )
    );

    var unexpected = await ExecuteHarnessStreamAsync(
      "opencode",
      "unexpected opencode",
      "browser-opencode-unexpected",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, unexpected.Where(IsTerminalStreamEvent));
    Assert.IsGreaterThanOrEqualTo(
      2,
      unexpected.Count(
        item => item["type"]!.GetValue<string>()
          == "harness.opencode-native-event-preserved"
      ),
      "The future event and native diagnostic events must be preserved."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeNativePermissionIsMappedAndAutoApproved()
  {
    var events = await ExecuteHarnessStreamAsync(
      "opencode",
      "permission opencode",
      "browser-opencode-permission",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.approved")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodePermissionOutsideWorkspaceIsRejectedByHostBeforeApproval()
  {
    var events = await ExecuteHarnessStreamAsync(
      "opencode",
      "outside permission opencode",
      "browser-opencode-outside-permission",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(
        item => item["type"]!.GetValue<string>()
          == "harness.opencode-approval-corrected"
      )
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "error")
    );
    Assert.IsFalse(
      File.Exists(Path.Combine(Path.GetDirectoryName(_environment.WorkspaceDirectory)!, "outside.txt"))
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeLegacyPermissionShapeMapsPatternsToHostApproval()
  {
    var events = await ExecuteHarnessStreamAsync(
      "opencode",
      "legacy permission opencode",
      "browser-opencode-legacy-permission",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.approved")
    );
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeRejectsReportedProviderOrModelSubstitution()
  {
    var events = await ExecuteHarnessStreamAsync(
      "opencode",
      "reroute opencode",
      "browser-opencode-reroute",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.AreEqual(
      "opencode-provider-substitution",
      events.Single(item => item["type"]!.GetValue<string>() == "error")
        ["error"]!["code"]!.GetValue<string>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingOpenCodeFailsClearlyWhileNativeRemainsAvailable()
  {
    var missing = Path.Combine(_environment.DataDirectory, "missing-opencode.exe");
    await _environment.SetOpenCodeExecutableAndRestartAsync(missing);

    try
    {
      await Page.GotoAsync("/");
      await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
      await SetExecuteModeAsync("auto");
      var selector = Page.Locator("#harness-selector");
      var unavailable = selector.Locator("option[value=\"opencode\"]");
      Assert.IsTrue(await unavailable.EvaluateAsync<bool>("option => option.disabled"));
      await Expect(unavailable).ToContainTextAsync("Unavailable");
      await Expect(unavailable).ToHaveAttributeAsync(
        "title",
        new Regex("OpenCode executable was not found or could not be started")
      );
      await Expect(selector).ToHaveValueAsync("native");

      var failed = await ExecuteHarnessStreamAsync(
        "opencode",
        "opencode unavailable",
        "browser-missing-opencode",
        "qwen3.8:27b-gpu0"
      );
      Assert.HasCount(1, failed.Where(IsTerminalStreamEvent));
      Assert.AreEqual(
        "opencode-executable-not-found",
        failed.Single(item => item["type"]!.GetValue<string>() == "error")
          ["error"]!["code"]!.GetValue<string>()
      );

      await SendMessageAsync("execute create file");
      Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
    }
    finally
    {
      await _environment.SetOpenCodeExecutableAndRestartAsync(
        _environment.FakeOpenCodeExecutablePath
      );
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeHarnessCancellationAbortsTheActiveSession()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("opencode");

    await StartMessageAsync("long opencode");
    await Expect(
      Page.Locator(".message.assistant .assistant-reasoning-body").Last
    ).ToContainTextAsync("Inspecting long OpenCode task");
    await Page.Locator("#cancel-request").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Send");
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    await Expect(Page.Locator(".message.assistant .assistant-answer").Last)
      .Not.ToContainTextAsync("OpenCode streamed");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenCodeMalformedEventAndServerCrashEachFailExactlyOnce()
  {
    var malformed = await ExecuteHarnessStreamAsync(
      "opencode",
      "malformed opencode",
      "browser-opencode-malformed",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, malformed.Where(IsTerminalStreamEvent));
    var malformedError = malformed.SingleOrDefault(
      item => item["type"]!.GetValue<string>() == "error"
    );
    Assert.IsNotNull(
      malformedError,
      string.Join(Environment.NewLine, malformed.Select(item => item.ToJsonString()))
    );
    Assert.AreEqual(
      "opencode-protocol-json",
      malformedError["error"]!["code"]!.GetValue<string>()
    );

    var crashed = await ExecuteHarnessStreamAsync(
      "opencode",
      "crash opencode",
      "browser-opencode-crash",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, crashed.Where(IsTerminalStreamEvent));
    StringAssert.Contains(
      crashed.Single(item => item["type"]!.GetValue<string>() == "error")
        ["error"]!["code"]!.GetValue<string>(),
      "opencode"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeHarnessUsesIsolatedAuthenticatedServerAndExactWorkspaceModel()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("qwen-code");

    await SendMessageAsync("qwen code deterministic turn");

    var assistant = Page.Locator(".message.assistant").Last;
    await Expect(assistant.Locator(".assistant-reasoning-body"))
      .ToContainTextAsync("Inspecting Qwen Code workspace");
    await Expect(assistant.Locator(".assistant-answer"))
      .ToContainTextAsync("Qwen Code streamed with qwen3.8:27b-gpu0");
    await Expect(Page.Locator("#context-usage-summary"))
      .ToContainTextAsync("live estimated");
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToBeVisibleAsync();
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToHaveAttributeAsync(
        "title",
        new Regex("Qwen Code.*does not report exact", RegexOptions.IgnoreCase)
      );
    await Expect(assistant.Locator(".activity"))
      .ToHaveAttributeAsync("data-terminal", "true");
    var capabilityProjection = assistant.Locator(
      "[data-event-type=\"execution-capability-profile-projected\"]"
    );
    await Expect(capabilityProjection).ToContainTextAsync(
      "native implementation []"
    );
    await Expect(capabilityProjection).ToContainTextAsync("Host bridge [create_execution_plan, revise_execution_plan, list_files, read_file");
    await Expect(capabilityProjection).ToContainTextAsync("delete_paths");
    await Expect(capabilityProjection).ToContainTextAsync("run_process");
    await Expect(capabilityProjection).ToContainTextAsync("missing adapter []");

    var runtime = Path.Combine(_environment.DataDirectory, "qwen-code-runtime");
    using var session = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-session.json"))
    );
    Assert.AreEqual(
      Path.GetFullPath(_environment.WorkspaceDirectory),
      Path.GetFullPath(session.RootElement.GetProperty("cwd").GetString()!)
    );
    Assert.AreEqual(
      "thread",
      session.RootElement.GetProperty("sessionScope").GetString()
    );
    var requestedClientId = session.RootElement.GetProperty("requestedClientId").GetString();
    var assignedClientId = session.RootElement.GetProperty("assignedClientId").GetString();
    StringAssert.StartsWith(requestedClientId, "agentic-router-");
    StringAssert.StartsWith(assignedClientId, "client_");
    Assert.AreNotEqual(requestedClientId, assignedClientId);
    Assert.IsTrue(session.RootElement.GetProperty("tokenConfigured").GetBoolean());
    Assert.AreEqual(
      "openai",
      session.RootElement.GetProperty("selectedAuthType").GetString()
    );
    Assert.AreEqual(
      "OLLAMA_API_KEY",
      session.RootElement.GetProperty("providerEnvKey").GetString()
    );
    Assert.IsTrue(
      session.RootElement.GetProperty("providerCredentialConfigured").GetBoolean()
    );
    StringAssert.Contains(
      session.RootElement.GetProperty("qwenHome").GetString(),
      "qwen-code-runtime"
    );
    var arguments = session.RootElement.GetProperty("args")
      .EnumerateArray()
      .Select(item => item.GetString())
      .ToArray();
    CollectionAssert.Contains(arguments, "--require-auth");
    CollectionAssert.Contains(arguments, "--no-web");
    CollectionAssert.Contains(arguments, "--workspace");
    CollectionAssert.DoesNotContain(arguments, "--safe-mode");
    CollectionAssert.DoesNotContain(arguments, "--mcp-config");
    CollectionAssert.DoesNotContain(arguments, "--enable-session-shell");
    CollectionAssert.DoesNotContain(arguments, "--enable-local-control");
    Assert.AreEqual(
      Path.Combine(runtime, "settings.json"),
      session.RootElement.GetProperty("systemSettingsPath").GetString()
    );

    using var model = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-model.json"))
    );
    Assert.AreEqual(
      "qwen3.8:27b-gpu0",
      model.RootElement.GetProperty("model").GetString()
    );
    Assert.AreNotEqual(
      model.RootElement.GetProperty("model").GetString(),
      model.RootElement.GetProperty("modelRouteId").GetString(),
      "The fake must preserve Qwen's distinction between the exact model and its ACP route ID."
    );
    Assert.AreEqual(
      assignedClientId,
      model.RootElement.GetProperty("clientId").GetString()
    );
    Assert.AreEqual(
      "initial-settings",
      model.RootElement.GetProperty("source").GetString()
    );
    using var settings = JsonDocument.Parse(
      model.RootElement.GetProperty("settings").GetString()!
    );
    Assert.AreEqual(
      "qwen3.8:27b-gpu0",
      settings.RootElement.GetProperty("model").GetProperty("name").GetString()
    );
    Assert.AreEqual(
      "openai",
      settings.RootElement.GetProperty("security").GetProperty("auth")
        .GetProperty("selectedType").GetString()
    );
    StringAssert.Contains(
      settings.RootElement.GetProperty("modelProviders").GetProperty("openai")[0]
        .GetProperty("baseUrl").GetString(),
      "/v1"
    );
    Assert.AreEqual(
      32_768,
      settings.RootElement.GetProperty("modelProviders").GetProperty("openai")[0]
        .GetProperty("generationConfig").GetProperty("contextWindowSize").GetInt32()
    );
    var coreTools = settings.RootElement.GetProperty("tools").GetProperty("core")
      .EnumerateArray()
      .Select(item => item.GetString())
      .ToArray();
    Assert.HasCount(3, coreTools);
    CollectionAssert.DoesNotContain(coreTools, "read_file");
    CollectionAssert.DoesNotContain(coreTools, "list_directory");
    CollectionAssert.DoesNotContain(coreTools, "glob");
    CollectionAssert.DoesNotContain(coreTools, "grep_search");
    CollectionAssert.DoesNotContain(coreTools, "run_shell_command");
    CollectionAssert.Contains(coreTools, "web_fetch");
    CollectionAssert.Contains(coreTools, "web_search");
    CollectionAssert.Contains(coreTools, "todo_write");
    Assert.IsTrue(
      settings.RootElement.GetProperty("mcp").GetProperty("allowed")
        .EnumerateArray().Any(item => item.GetString() == "agentic_router")
    );
    Assert.IsTrue(
      settings.RootElement.GetProperty("mcpServers").TryGetProperty(
        "agentic_router",
        out var qwenHostBridge
      )
    );
    var denied = settings.RootElement.GetProperty("permissions").GetProperty("deny")
      .EnumerateArray()
      .Select(item => item.GetString())
      .ToArray();
    CollectionAssert.Contains(denied, "run_shell_command");
    CollectionAssert.Contains(denied, "read_file");
    CollectionAssert.Contains(denied, "list_directory");
    CollectionAssert.Contains(denied, "glob");
    CollectionAssert.Contains(denied, "grep_search");
    CollectionAssert.Contains(denied, "edit");
    CollectionAssert.Contains(denied, "write_file");
    CollectionAssert.Contains(denied, "agent");
    Assert.IsFalse(
      settings.RootElement.GetProperty("memory")
        .GetProperty("enableManagedAutoMemory").GetBoolean()
    );

    using var firstPrompt = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-prompt.json"))
    );
    var firstPromptText = firstPrompt.RootElement.GetProperty("text").GetString()!;
    StringAssert.Contains(firstPromptText, "select:mcp__agentic_router__delete_paths");
    StringAssert.Contains(firstPromptText, "select:mcp__agentic_router__run_process");
    Assert.AreEqual(
      assignedClientId,
      firstPrompt.RootElement.GetProperty("clientId").GetString()
    );
    var firstSessionId = firstPrompt.RootElement.GetProperty("sessionId").GetString();
    await SendMessageAsync("qwen code second deterministic turn");
    using var secondPrompt = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-prompt.json"))
    );
    Assert.AreEqual(
      firstSessionId,
      secondPrompt.RootElement.GetProperty("sessionId").GetString()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeUsesTheHostResolved128kContextWindow()
  {
    using var saved = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        Context = _environment.BaselineSettings.Context with
        {
          DefaultContextTokens = 131_072,
          ProviderContextTokens = 131_072
        }
      }
    );
    saved.EnsureSuccessStatusCode();

    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "qwen code 128k context",
      "browser-qwen-code-128k",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsTrue(
      events.Where(item => item["type"]!.GetValue<string>() == "context.usage")
        .Any(item => item["contextUsage"]?["effectiveLimitTokens"]?.GetValue<int>() == 131_072)
    );

    var runtime = Path.Combine(_environment.DataDirectory, "qwen-code-runtime");
    using var model = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-model.json"))
    );
    using var settings = JsonDocument.Parse(
      model.RootElement.GetProperty("settings").GetString()!
    );
    Assert.AreEqual(
      131_072,
      settings.RootElement.GetProperty("modelProviders").GetProperty("openai")[0]
        .GetProperty("generationConfig").GetProperty("contextWindowSize").GetInt32()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeToolEventsAndExactUsageAreNormalized()
  {
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "qwen code tool events",
      "browser-qwen-code-tools",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.started")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.completed")
    );
    var contextEvents = events.Where(
      item => item["type"]!.GetValue<string>() == "context.usage"
    ).ToArray();
    Assert.IsGreaterThanOrEqualTo(2, contextEvents.Length);
    Assert.AreEqual(
      "estimated",
      contextEvents.First()["contextUsage"]!["accuracy"]!.GetValue<string>()
    );
    Assert.IsTrue(contextEvents.Any(item =>
      item["contextUsage"]!["accuracy"]!.GetValue<string>() == "estimated"
      && item["contextUsage"]!["activeContextTokens"]!.GetValue<long>()
        > item["contextUsage"]!["inputTokens"]!.GetValue<long>()
    ));
    var exactContext = contextEvents.Last(item =>
      item["contextUsage"]!["accuracy"]!.GetValue<string>() == "exact"
    );
    Assert.AreEqual(
      4_321,
      exactContext["contextUsage"]!["inputTokens"]!.GetValue<long>()
    );
    Assert.AreEqual(
      "estimated",
      contextEvents.Last()["contextUsage"]!["accuracy"]!.GetValue<string>()
    );
    Assert.IsGreaterThan(
      exactContext["contextUsage"]!["activeContextTokens"]!.GetValue<long>(),
      contextEvents.Last()["contextUsage"]!["activeContextTokens"]!.GetValue<long>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeNativePermissionIsMappedAndAutoApproved()
  {
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "permission qwen code",
      "browser-qwen-code-permission",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.approved")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeReadOnlyNativeExtraWithoutWorkspaceTargetsExecutesWithoutMutationApproval()
  {
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "unmappable permission qwen code",
      "browser-qwen-code-unmappable-permission",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(
        item => item["type"]!.GetValue<string>()
          == "harness.qwen-code-readonly-authorized"
      )
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "error")
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "action.awaiting-approval")
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeReadOnlyNativeExtraAlsoRunsDirectlyInAskMode()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("ask");
    await Page.Locator("#harness-selector").SelectOptionAsync("qwen-code");

    await SendMessageAsync("unmappable permission qwen code");

    await Expect(Page.Locator(".action-approval")).ToHaveCountAsync(0);
    await Expect(Page.Locator(
      "[data-event-type=\"harness.qwen-code-readonly-authorized\"]"
    )).ToHaveCountAsync(1);
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodePermissionOutsideWorkspaceIsRejectedByHostBeforeApproval()
  {
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "outside permission qwen code",
      "browser-qwen-code-outside-permission",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(
        item => item["type"]!.GetValue<string>()
          == "harness.qwen-code-approval-corrected"
      )
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "error")
    );
    Assert.IsFalse(
      File.Exists(Path.Combine(Path.GetDirectoryName(_environment.WorkspaceDirectory)!, "outside.txt"))
    );
  }

  [TestMethod]
  [DataRow("opencode", "outside read permission opencode")]
  [DataRow("qwen-code", "outside read permission qwen code")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessReadPermissionOutsideWorkspaceIsRejectedByHost(
    string harness,
    string prompt
  )
  {
    var events = await ExecuteHarnessStreamAsync(
      harness,
      prompt,
      $"browser-{harness}-outside-read-permission",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == $"harness.{harness}-approval-corrected"),
      string.Join(Environment.NewLine, events.Select(item => item.ToJsonString()))
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "action.approved")
    );
    Assert.HasCount(
      0,
      events.Where(item => item.ToJsonString().Contains("outside-token=FORBIDDEN", StringComparison.Ordinal))
    );
  }

  [TestMethod]
  [DataRow("opencode", "permission opencode")]
  [DataRow("qwen-code", "permission qwen code")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessAskWaitsBeforeNativeMutation(
    string harness,
    string prompt
  )
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("ask");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);

    await StartMessageAsync(prompt);
    var approval = Page.Locator(".action-approval").Last;
    await Expect(approval).ToBeVisibleAsync();
    await Expect(approval).ToContainTextAsync("README.md");
    await approval.GetByRole(
      AriaRole.Button,
      new() { Name = "Approve", Exact = true }
    ).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
  }

  [TestMethod]
  [DataRow("opencode", "host bridge opencode", "opencode-host-auto.txt", "fake-opencode-host-tool.json")]
  [DataRow("qwen-code", "host bridge qwen code", "qwen-host-auto.txt", "fake-qwen-host-tool.json")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessHostBridgeExecutesCanonicalBatchThroughHost(
    string harness,
    string prompt,
    string relativePath,
    string markerName
  )
  {
    var events = await ExecuteHarnessStreamAsync(
      harness,
      prompt,
      $"browser-{harness}-host-bridge-auto",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
    Assert.IsTrue(
      events.Any(item => item["type"]!.GetValue<string>() == "action.proposed")
    );
    Assert.IsTrue(
      events.Any(item => item["type"]!.GetValue<string>() == "action.completed")
    );
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, relativePath)));
    var runtime = Path.Combine(
      _environment.DataDirectory,
      harness == "opencode" ? "opencode-runtime" : "qwen-code-runtime"
    );
    using var marker = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, markerName))
    );
    Assert.AreEqual("create_files", marker.RootElement.GetProperty("tool").GetString());
    Assert.IsTrue(marker.RootElement.GetProperty("succeeded").GetBoolean());
    Assert.IsTrue(marker.RootElement.GetProperty("observed").GetBoolean());
  }

  [TestMethod]
  [DataRow("opencode", "ask host bridge opencode", "opencode-host-ask.txt")]
  [DataRow("qwen-code", "ask host bridge qwen code", "qwen-host-ask.txt")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessHostBridgeAskWaitsBeforeCanonicalMutation(
    string harness,
    string prompt,
    string relativePath
  )
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("ask");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);

    await StartMessageAsync(prompt);
    var approval = Page.Locator(".action-approval").Last;
    await Expect(approval).ToBeVisibleAsync();
    await Expect(approval).ToContainTextAsync("create_files");
    Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, relativePath)));
    await approval.GetByRole(
      AriaRole.Button,
      new() { Name = "Approve", Exact = true }
    ).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, relativePath)));
  }

  [TestMethod]
  [DataRow("opencode", "parity host bridge opencode", "opencode", "opencode-parity-complete.txt")]
  [DataRow("qwen-code", "parity host bridge qwen code", "qwen-code", "qwen-parity-complete.txt")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessHostBridgeProvidesDirectoryDeleteProcessAndGit(
    string harness,
    string prompt,
    string runtimeName,
    string finalPath
  )
  {
    await RunGitAsync("init");
    var events = await ExecuteHarnessStreamAsync(
      harness,
      prompt,
      $"browser-{harness}-host-bridge-parity",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
    var runtime = Path.Combine(_environment.DataDirectory, $"{runtimeName}-runtime");
    var markerName = harness == "opencode"
      ? "fake-opencode-host-parity.json"
      : "fake-qwen-host-parity.json";
    using var marker = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, markerName))
    );
    var steps = marker.RootElement.GetProperty("steps").EnumerateArray().ToArray();
    Assert.HasCount(7, steps);
    Assert.IsTrue(steps.All(step => step.GetProperty("Succeeded").GetBoolean()));
    CollectionAssert.AreEquivalent(
      new[]
      {
        "create_directory",
        "create_files",
        "get_file_info",
        "run_process",
        "git_status",
        "delete_paths",
        "create_files"
      },
      steps.Select(step => step.GetProperty("Tool").GetString()).ToArray()
    );
    Assert.IsTrue(marker.RootElement.GetProperty("deleted").GetBoolean());
    Assert.IsTrue(marker.RootElement.GetProperty("finalObserved").GetBoolean());
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, finalPath)));
  }

  [TestMethod]
  [DataRow("opencode", "boundary host bridge opencode", "opencode-recovered.txt", "opencode-escape.txt")]
  [DataRow("qwen-code", "boundary host bridge qwen code", "qwen-recovered.txt", "qwen-escape.txt")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessHostBridgeRejectsBoundaryAndLetsAgentRecover(
    string harness,
    string prompt,
    string recoveredPath,
    string escapedPath
  )
  {
    var events = await ExecuteHarnessStreamAsync(
      harness,
      prompt,
      $"browser-{harness}-host-bridge-boundary",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
    Assert.IsTrue(
      events.Any(item => item["type"]!.GetValue<string>() == "action.input-rejected"),
      $"Observed event types: {string.Join(", ", events.Select(item => item["type"]!.GetValue<string>()))}"
    );
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, recoveredPath)));
    Assert.IsFalse(File.Exists(Path.Combine(
      Path.GetDirectoryName(_environment.WorkspaceDirectory)!,
      escapedPath
    )));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeHarnessCancellationAbortsTheActivePrompt()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("qwen-code");

    await StartMessageAsync("long qwen code");
    await Expect(
      Page.Locator(".message.assistant .assistant-reasoning-body").Last
    ).ToContainTextAsync("Inspecting long Qwen Code task");
    await Page.Locator("#cancel-request").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Send");
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    await Expect(Page.Locator(".message.assistant .assistant-answer").Last)
      .Not.ToContainTextAsync("Qwen Code streamed");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeMalformedEventAndServerCrashEachFailExactlyOnce()
  {
    var malformed = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "malformed qwen code",
      "browser-qwen-code-malformed",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, malformed.Where(IsTerminalStreamEvent));
    var malformedError = malformed.SingleOrDefault(
      item => item["type"]!.GetValue<string>() == "error"
    );
    Assert.IsNotNull(
      malformedError,
      string.Join(Environment.NewLine, malformed.Select(item => item.ToJsonString()))
    );
    Assert.AreEqual(
      "qwen-code-protocol-json",
      malformedError["error"]!["code"]!.GetValue<string>()
    );

    var crashed = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "crash qwen code",
      "browser-qwen-code-crash",
      "qwen3.8:27b-gpu0"
    );
    Assert.HasCount(1, crashed.Where(IsTerminalStreamEvent));
    StringAssert.Contains(
      crashed.Single(item => item["type"]!.GetValue<string>() == "error")
        ["error"]!["code"]!.GetValue<string>(),
      "qwen-code"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeUnexpectedNativeEventIsPreserved()
  {
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "unexpected qwen code",
      "browser-qwen-code-unexpected",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(
        item => item["type"]!.GetValue<string>()
          == "harness.qwen-code-native-event-preserved"
      ),
      "Repeated unknown native events must be represented once per event type."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeRealEnvelopeStormIsBoundedAndReturnsTheCompleteAnswer()
  {
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "storm qwen code",
      "browser-qwen-code-storm",
      "qwen3.8:27b-gpu0"
    );

    var responseDeltas = events.Where(
      item => item["type"]!.GetValue<string>() == "response.delta"
    ).ToArray();
    var reasoningDeltas = events.Where(
      item => item["type"]!.GetValue<string>() == "reasoning.delta"
    ).ToArray();
    var answer = string.Concat(responseDeltas.Select(
      item => item["delta"]?.GetValue<string>() ?? string.Empty
    ));
    var expectedAnswer = string.Concat(Enumerable.Repeat(
      "Qwen Code returned a useful bounded response. ",
      32
    ));

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.AreEqual(expectedAnswer, answer);
    Assert.IsLessThanOrEqualTo(8, responseDeltas.Length);
    Assert.IsLessThanOrEqualTo(4, reasoningDeltas.Length);
    Assert.HasCount(
      0,
      events.Where(
        item => item["type"]!.GetValue<string>()
          == "harness.qwen-code-native-event-preserved"
      ),
      "Known Qwen state updates must not leak into the activity timeline."
    );
    Assert.IsLessThan(
      40,
      events.Count(),
      "The Host stream must stay bounded even when Qwen emits thousands of frames."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeEndTurnWithoutAssistantOutputFailsReviewably()
  {
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "empty qwen code",
      "browser-qwen-code-empty",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var error = events.Single(item => item["type"]!.GetValue<string>() == "error");
    Assert.AreEqual(
      "qwen-code-empty-response",
      error["error"]!["code"]!.GetValue<string>()
    );
    Assert.HasCount(
      0,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessStreamsThinkingToolsAssistantAndReusesConversationThread()
  {
    var settings = await GetSettingsJsonAsync();
    var projectAwareness = settings["projectAwareness"]!.AsObject();
    Assert.AreEqual(2, projectAwareness["planLimitsSchemaVersion"]!.GetValue<int>());
    Assert.AreEqual(20, projectAwareness["maxPlanSteps"]!.GetValue<int>());
    Assert.AreEqual(6, projectAwareness["maxPlanRevisions"]!.GetValue<int>());

    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await StartMessageAsync("create codex file");
    var firstAssistant = Page.Locator(".message.assistant").Last;
    await Expect(
      firstAssistant.Locator(".assistant-reasoning-body")
    ).ToContainTextAsync("Inspecting");
    await Expect(
      firstAssistant.Locator(".assistant-answer")
    ).ToContainTextAsync("Codex streamed with alpha:latest");
    await Expect(
      firstAssistant.Locator(".activity")
    ).ToHaveAttributeAsync("data-terminal", "true");
    var capabilityProjection = firstAssistant.Locator(
      "[data-event-type=\"execution-capability-profile-projected\"]"
    );
    await Expect(capabilityProjection).ToContainTextAsync(
      "Host bridge ["
    );
    await Expect(capabilityProjection).ToContainTextAsync("delete_paths");
    await Expect(capabilityProjection).ToContainTextAsync("run_process");
    await Expect(capabilityProjection).ToContainTextAsync("missing adapter []");
    await Expect(
      firstAssistant.Locator("[data-event-type=\"action.started\"]")
    ).ToHaveCountAsync(1);
    await Expect(
      firstAssistant.Locator("[data-event-type=\"harness.codex-effects-observed\"]")
    ).ToHaveCountAsync(1);
    Assert.IsTrue(
      File.Exists(Path.Combine(_environment.WorkspaceDirectory, "codex-created.txt"))
    );
    var codexRuntime = Path.Combine(_environment.DataDirectory, "codex-runtime");
    Assert.IsTrue(
      File.Exists(Path.Combine(codexRuntime, "fake-app-server-started.marker"))
    );
    var codexConfig = await File.ReadAllTextAsync(Path.Combine(codexRuntime, "config.toml"));
    StringAssert.Contains(codexConfig, "model_provider = \"ollama\"");
    StringAssert.Contains(codexConfig, "model_catalog_json = \"");
    StringAssert.Contains(codexConfig, "default_permissions = \":workspace\"");
    StringAssert.Contains(codexConfig, "shell_tool = false");
    StringAssert.Contains(codexConfig, "unified_exec = false");
    Assert.DoesNotContain("sandbox_mode", codexConfig);
    using (var environmentDocument = JsonDocument.Parse(
      await File.ReadAllTextAsync(
        Path.Combine(codexRuntime, "fake-app-server-environment.json")
      )
    ))
    {
      Assert.AreEqual(
        _environment.FakeOllama.BaseUrl,
        environmentDocument.RootElement.GetProperty("ollamaHost").GetString()
      );
      Assert.AreEqual(
        $"{_environment.FakeOllama.BaseUrl}/v1",
        environmentDocument.RootElement.GetProperty("codexOssBaseUrl").GetString()
      );
    }
    using (var threadRequest = JsonDocument.Parse(
      await File.ReadAllTextAsync(
        Path.Combine(codexRuntime, "fake-app-server-thread-request.json")
      )
    ))
    {
      Assert.AreEqual(
        Path.GetFullPath(_environment.WorkspaceDirectory),
        Path.GetFullPath(threadRequest.RootElement.GetProperty("cwd").GetString()!)
      );
      Assert.AreEqual(
        "alpha:latest",
        threadRequest.RootElement.GetProperty("model").GetString()
      );
      Assert.AreEqual(
        "ollama",
        threadRequest.RootElement.GetProperty("provider").GetString()
      );
      Assert.AreEqual(
        32_768,
        threadRequest.RootElement.GetProperty("contextWindowTokens").GetInt32()
      );
      Assert.AreEqual(
        32_112,
        threadRequest.RootElement.GetProperty("autoCompactTokenLimit").GetInt32()
      );
      Assert.AreEqual(
        "total",
        threadRequest.RootElement.GetProperty("autoCompactTokenLimitScope").GetString()
      );
      var planSchema = threadRequest.RootElement.GetProperty("planSchema");
      Assert.AreEqual(500, planSchema.GetProperty("objectiveMaximumLength").GetInt32());
      Assert.AreEqual(20, planSchema.GetProperty("maximumSteps").GetInt32());
      Assert.AreEqual(160, planSchema.GetProperty("titleMaximumLength").GetInt32());
      Assert.AreEqual(20, planSchema.GetProperty("maximumDependencies").GetInt32());
    }
    using (var modelCatalog = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(codexRuntime, "model-catalog.json"))
    ))
    {
      var alphaMetadata = modelCatalog.RootElement.GetProperty("models")
        .EnumerateArray()
        .Single(model => model.GetProperty("slug").GetString() == "alpha:latest");
      Assert.AreEqual(32_768, alphaMetadata.GetProperty("context_window").GetInt32());
      Assert.AreEqual(32_768, alphaMetadata.GetProperty("max_context_window").GetInt32());
      Assert.AreEqual(100, alphaMetadata.GetProperty("effective_context_window_percent").GetInt32());
      Assert.AreEqual("shell_command", alphaMetadata.GetProperty("shell_type").GetString());
    }
    var codexTurnInput = await File.ReadAllTextAsync(
      Path.Combine(codexRuntime, "fake-app-server-turn-input.txt")
    );
    StringAssert.Contains(
      codexTurnInput,
      "Preserve unrelated existing user changes"
    );
    StringAssert.Contains(codexTurnInput, "Current user request:\ncreate codex file");
    Assert.DoesNotContain("Protected pre-existing paths", codexTurnInput);

    var firstText = await firstAssistant.Locator(".assistant-answer").InnerTextAsync();
    var thread = Regex.Match(firstText, @"fake-thread-\d+").Value;
    Assert.IsFalse(string.IsNullOrWhiteSpace(thread), firstText);

    await SendMessageAsync("codex second turn");
    await Expect(
      Page.Locator(".message.assistant .assistant-answer").Last
    ).ToContainTextAsync(thread);
    Assert.AreEqual(
      "edited on the reused Codex thread\n",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "codex-created.txt")
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessRegistersExactQwenLocalModelMetadataBeforeStartup()
  {
    var events = await ExecuteHarnessStreamAsync(
      "codex",
      "inspect the exact local model metadata",
      "browser-codex-qwen-metadata",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.AreEqual("response.completed", events.Single(IsTerminalStreamEvent)["type"]!.GetValue<string>());
    var codexRuntime = Path.Combine(_environment.DataDirectory, "codex-runtime");
    using var threadRequest = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(codexRuntime, "fake-app-server-thread-request.json"))
    );
    var resolvedContext = threadRequest.RootElement.GetProperty("contextWindowTokens").GetInt32();
    var autoCompactLimit = threadRequest.RootElement.GetProperty("autoCompactTokenLimit").GetInt32();
    Assert.AreEqual((int)((long)resolvedContext * 98 / 100), autoCompactLimit);
    Assert.IsLessThan(resolvedContext, autoCompactLimit);
    Assert.AreEqual(
      "total",
      threadRequest.RootElement.GetProperty("autoCompactTokenLimitScope").GetString()
    );
    using var catalog = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(codexRuntime, "model-catalog.json"))
    );
    var metadata = catalog.RootElement.GetProperty("models")
      .EnumerateArray()
      .Single(model => model.GetProperty("slug").GetString() == "qwen3.8:27b-gpu0");
    Assert.AreEqual(resolvedContext, metadata.GetProperty("context_window").GetInt32());
    Assert.AreEqual(resolvedContext, metadata.GetProperty("max_context_window").GetInt32());
    Assert.AreEqual(100, metadata.GetProperty("effective_context_window_percent").GetInt32());
    Assert.IsTrue(metadata.GetProperty("supported_in_api").GetBoolean());
    CollectionAssert.Contains(
      metadata.GetProperty("input_modalities").EnumerateArray()
        .Select(item => item.GetString())
        .ToArray(),
      "text"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexSteerTargetsTheExactActiveTurnAndRemainsVisibleInTheConversation()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(HarnessIds.Codex);

    await StartMessageAsync("long codex turn");
    var initialUserMessage = Page.Locator("#messages > .message.user").Last;
    var initialTimestamp = initialUserMessage.Locator(".message-timestamp");
    await Expect(initialTimestamp).ToBeVisibleAsync();
    Assert.IsTrue(
      DateTimeOffset.TryParse(
        await initialTimestamp.GetAttributeAsync("datetime"),
        out _
      )
    );
    var initialAssistant = Page.Locator("#messages > .message.assistant").Last;
    await Expect(initialAssistant.Locator(".assistant-running-indicator"))
      .ToBeVisibleAsync();
    await Expect(initialAssistant.Locator(".assistant-running-brain path"))
      .ToHaveCountAsync(3);
    await Expect(initialAssistant.Locator(".assistant-reasoning-body"))
      .ToContainTextAsync("Inspecting", new() { Timeout = 10_000 });
    await Page.Locator("#message-input").FillAsync("Focus on the requested validation first.");
    await Page.Locator("#send-button").ClickAsync();
    var codexSteer = Page.Locator(".message-buffer-action[data-action=\"steer\"]");
    await Expect(codexSteer).ToBeEnabledAsync();
    await codexSteer.ClickAsync();

    var steeredMessage = Page.Locator(".steered-message");
    await Expect(steeredMessage).ToContainTextAsync(
      "Focus on the requested validation first."
    );
    await Expect(steeredMessage.Locator(".message-timestamp")).ToBeVisibleAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 15_000 });
    var chronologicalMessages = await Page.Locator("#messages > .message")
      .EvaluateAllAsync<string[]>(
        """
        nodes => nodes.map(node =>
          node.classList.contains("steered-message")
            ? "steer"
            : node.classList.contains("user")
              ? "user"
              : "assistant")
        """
      );
    CollectionAssert.AreEqual(
      new[] { "user", "assistant", "steer", "assistant" },
      chronologicalMessages
    );
    await Expect(
      Page.Locator("#messages > .message.assistant").Nth(0)
        .Locator(".assistant-reasoning-body")
    ).ToContainTextAsync("Inspecting");
    var acceptedSteering = Page.Locator(
      "#messages > .message.assistant"
    ).Nth(1).Locator(
      ".assistant-reasoning-body",
      new() { HasText = "Steering accepted" }
    );
    await Expect(acceptedSteering).ToHaveCountAsync(1);
    await Expect(acceptedSteering).ToContainTextAsync("Steering accepted");
    await Expect(Page.Locator(".assistant-running-indicator:visible"))
      .ToHaveCountAsync(0);
    using var evidence = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(
        _environment.DataDirectory,
        "codex-runtime",
        "fake-app-server-steer.json"
      ))
    );
    Assert.AreEqual(
      "Focus on the requested validation first.",
      evidence.RootElement.GetProperty("message").GetString()
    );
    StringAssert.StartsWith(
      evidence.RootElement.GetProperty("turnId").GetString(),
      "fake-turn-"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeSteerUsesTheOwnedMidTurnMessageEndpoint()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(HarnessIds.QwenCode);

    await StartMessageAsync("long qwen code");
    await Expect(Page.Locator(".message.assistant .assistant-reasoning-body").Last)
      .ToContainTextAsync("Inspecting long Qwen Code task", new() { Timeout = 10_000 });
    await Page.Locator("#message-input").FillAsync("Use the supplemental Qwen instruction.");
    await Page.Locator("#send-button").ClickAsync();
    var qwenSteer = Page.Locator(".message-buffer-action[data-action=\"steer\"]");
    await Expect(qwenSteer).ToBeEnabledAsync();
    await qwenSteer.ClickAsync();

    await Expect(Page.Locator(".steered-message")).ToContainTextAsync(
      "Use the supplemental Qwen instruction."
    );
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 15_000 });
    using var evidence = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(
        _environment.DataDirectory,
        "qwen-code-runtime",
        "fake-qwen-steer.json"
      ))
    );
    Assert.AreEqual(
      "Use the supplemental Qwen instruction.",
      evidence.RootElement.GetProperty("message").GetString()
    );
    StringAssert.StartsWith(
      evidence.RootElement.GetProperty("promptId").GetString(),
      "qwen-session-"
    );
  }

  [TestMethod]
  [DataRow(HarnessIds.OpenCode, "long opencode", "OpenCode")]
  [DataRow(HarnessIds.ClaudeCode, "long claude task", "Claude Code")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SteerExplainsWhyItIsDisabledForQueueOnlyHarnesses(
    string harness,
    string prompt,
    string harnessLabel
  )
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);

    await StartMessageAsync(prompt);
    await Expect(Page.Locator("#cancel-request")).ToBeVisibleAsync();
    await Page.Locator("#message-input").FillAsync("supplemental message");
    await Page.Locator("#send-button").ClickAsync();
    var steer = Page.Locator(".message-buffer-action[data-action=\"steer\"]");
    var steerTooltip = Page.Locator(".message-buffer-action-tooltip");
    var explanation =
      $"Steer is unavailable for {harnessLabel}. Use Codex or Qwen Code.";
    await Expect(steer).ToBeDisabledAsync();
    await Expect(steer).ToHaveAttributeAsync(
      "title",
      explanation
    );
    await Expect(steerTooltip).ToHaveAttributeAsync("data-tooltip", explanation);
    await Expect(steerTooltip).ToHaveAttributeAsync("tabindex", "0");
    await steerTooltip.HoverAsync();
    await Page.WaitForTimeoutAsync(200);
    var tooltipOpacity = await steerTooltip.EvaluateAsync<string>(
      "element => getComputedStyle(element, '::after').opacity"
    );
    Assert.IsGreaterThan(
      0.1f,
      float.Parse(
        tooltipOpacity,
        System.Globalization.CultureInfo.InvariantCulture
      ),
      "The unsupported-steering tooltip must become visible on hover."
    );
    await Page.Locator("#cancel-request").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Send");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessForwardsImagesAndPublishesLiveContextUsage()
  {
    const string png =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    var imagePath = Path.Combine(_environment.WorkspaceDirectory, "codex-vision.png");
    await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(png));

    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");
    await Page.Locator("#image-input").SetInputFilesAsync(imagePath);

    await StartMessageAsync("codex live context usage");
    var messageImage = Page.Locator(".message.user .message-image-preview");
    await Expect(messageImage).ToHaveCountAsync(1);
    await Expect(messageImage.Locator("img")).ToHaveAttributeAsync(
      "src",
      new Regex("^data:image/png;base64,")
    );
    await messageImage.ClickAsync();
    await Expect(Page.Locator("#image-review-dialog")).ToBeVisibleAsync();
    await Expect(Page.Locator("#image-review-title"))
      .ToHaveTextAsync("codex-vision.png");
    await Expect(Page.Locator("#image-review-content")).ToBeVisibleAsync();
    Assert.IsGreaterThan(
      0,
      await Page.Locator("#image-review-content").EvaluateAsync<int>(
        "image => image.naturalWidth"
      )
    );
    await Page.Keyboard.PressAsync("Escape");
    await Expect(Page.Locator("#image-review-dialog")).ToBeHiddenAsync();
    await Expect(Page.Locator("#context-usage-summary"))
      .ToContainTextAsync("30.0k / 32.8k · live exact", new() { Timeout = 10_000 });
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToBeHiddenAsync();
    await Expect(Page.Locator("#cancel-request")).ToBeVisibleAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 20_000 });

    var imageEvidencePath = Path.Combine(
      _environment.DataDirectory,
      "codex-runtime",
      "fake-app-server-turn-images.json"
    );
    using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(imageEvidencePath));
    var images = evidence.RootElement.GetProperty("images");
    Assert.AreEqual(1, images.GetArrayLength());
    Assert.AreEqual("image", images[0].GetProperty("type").GetString());
    Assert.AreEqual("auto", images[0].GetProperty("detail").GetString());
    StringAssert.StartsWith(
      images[0].GetProperty("urlPrefix").GetString(),
      "data:image/png;base64,"
    );
    using var catalog = JsonDocument.Parse(
      await File.ReadAllTextAsync(
        Path.Combine(_environment.DataDirectory, "codex-runtime", "model-catalog.json")
      )
    );
    var alphaMetadata = catalog.RootElement.GetProperty("models")
      .EnumerateArray()
      .Single(model => model.GetProperty("slug").GetString() == "alpha:latest");
    CollectionAssert.Contains(
      alphaMetadata.GetProperty("input_modalities").EnumerateArray()
        .Select(item => item.GetString())
        .ToArray(),
      "image"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeHarnessPublishesLiveEstimatedContextDuringThinking()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("native");

    await StartMessageAsync(
      "create file native-live-context.txt with marker LIVE-CONTEXT chronological thinking stream"
    );
    await Expect(Page.Locator("#context-usage-summary"))
      .ToContainTextAsync("live estimated", new() { Timeout = 10_000 });
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToBeVisibleAsync();
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToHaveAttributeAsync(
        "title",
        new Regex("Native.*does not report exact", RegexOptions.IgnoreCase)
      );
    await Expect(Page.Locator("#cancel-request")).ToBeVisibleAsync();
    await Page.Locator("#cancel-request").ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 10_000 });
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClaudeCodePublishesEstimatedLiveContextThenExactReportedUsage()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(HarnessIds.ClaudeCode);

    await StartMessageAsync("claude live context usage");
    await Expect(Page.Locator("#context-usage-summary"))
      .ToContainTextAsync("live estimated", new() { Timeout = 10_000 });
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToBeVisibleAsync();
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToHaveAttributeAsync(
        "title",
        new Regex("Claude Code.*does not report exact", RegexOptions.IgnoreCase)
      );
    await Expect(Page.Locator("#cancel-request")).ToBeVisibleAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 20_000 });
    await Expect(Page.Locator("#context-usage-summary"))
      .ToContainTextAsync("155 / 32.8k · live exact");
    await Expect(Page.Locator("#context-usage-estimate-warning"))
      .ToBeHiddenAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HarnessHandoffHydratesCanonicalConversationAndRoundTripSendsOnlyDelta()
  {
    const string conversationId = "browser-harness-canonical-round-trip";
    var first = await ExecuteHarnessStreamAsync(
      "codex",
      "Continuity origin: alpha.txt was created with marker ORIGIN-ALPHA.",
      conversationId
    );
    Assert.HasCount(1, first.Where(IsTerminalStreamEvent));

    object[] historyAfterCodex =
    [
      new
      {
        role = "user",
        content = "Continuity origin: alpha.txt was created with marker ORIGIN-ALPHA."
      },
      new
      {
        role = "assistant",
        content = "Codex completed alpha.txt and recorded marker ORIGIN-ALPHA."
      }
    ];
    var second = await ExecuteHarnessStreamAsync(
      "opencode",
      "Continue from that result and treat beta.txt as referencing alpha.txt.",
      conversationId,
      "qwen3.8:27b-gpu0",
      historyAfterCodex
    );
    Assert.HasCount(1, second.Where(IsTerminalStreamEvent));

    var openCodeRuntime = Path.Combine(_environment.DataDirectory, "opencode-runtime");
    using (var openCodePrompt = JsonDocument.Parse(
      await File.ReadAllTextAsync(
        Path.Combine(openCodeRuntime, "fake-opencode-prompt.json")
      )
    ))
    {
      var text = openCodePrompt.RootElement.GetProperty("text").GetString()!;
      StringAssert.Contains(text, "Canonical Agentic Router conversation hydration:");
      StringAssert.Contains(text, "ORIGIN-ALPHA");
      StringAssert.Contains(text, "beta.txt as referencing alpha.txt");
      Assert.DoesNotContain("Protected pre-existing paths", text);
      Assert.DoesNotContain("Do not use shell commands", text);
      Assert.DoesNotContain("Do not use Git writes", text);
      Assert.DoesNotContain("Do not use subagents", text);
      Assert.DoesNotContain("Do not use network tools", text);
      Assert.DoesNotContain("Do not use sharing", text);
    }

    object[] historyAfterOpenCode =
    [
      .. historyAfterCodex,
      new
      {
        role = "user",
        content = "Continue from that result and treat beta.txt as referencing alpha.txt."
      },
      new
      {
        role = "assistant",
        content = "OpenCode recorded that beta.txt references alpha.txt."
      }
    ];
    var third = await ExecuteHarnessStreamAsync(
      "codex",
      "Does the remaining beta.txt reference the earlier alpha.txt?",
      conversationId,
      "alpha:latest",
      historyAfterOpenCode
    );
    Assert.HasCount(1, third.Where(IsTerminalStreamEvent));

    var codexRuntime = Path.Combine(_environment.DataDirectory, "codex-runtime");
    var codexPrompt = await File.ReadAllTextAsync(
      Path.Combine(codexRuntime, "fake-app-server-turn-input.txt")
    );
    StringAssert.Contains(
      codexPrompt,
      "Canonical Agentic Router conversation delta since this harness last ran:"
    );
    StringAssert.Contains(codexPrompt, "OpenCode recorded that beta.txt references alpha.txt.");
    StringAssert.Contains(codexPrompt, "Does the remaining beta.txt reference the earlier alpha.txt?");
    Assert.DoesNotContain(
      "Continuity origin: alpha.txt was created with marker ORIGIN-ALPHA.",
      codexPrompt
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexOpenCodeCodexRoundTripPreservesContextAndObservedFileState()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await SendMessageAsync("create codex file");
    var created = Path.Combine(_environment.WorkspaceDirectory, "codex-created.txt");
    var unrelated = Path.Combine(_environment.WorkspaceDirectory, "round-trip-unrelated.txt");
    Assert.IsTrue(File.Exists(created));
    await File.WriteAllTextAsync(unrelated, "preserved unrelated bytes");

    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await Page.Locator("#harness-selector").SelectOptionAsync("opencode");
    await SendMessageAsync(
      "delete permission opencode: delete the file created by the earlier harness"
    );
    await Expect(Page.Locator(".action-approval")).ToHaveCountAsync(0);
    Assert.IsFalse(File.Exists(created));
    Assert.AreEqual("preserved unrelated bytes", await File.ReadAllTextAsync(unrelated));

    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");
    await SendMessageAsync(
      "Does the conversation show that the file created earlier was removed?"
    );

    Assert.IsFalse(File.Exists(created));
    var codexPrompt = await File.ReadAllTextAsync(
      Path.Combine(
        _environment.DataDirectory,
        "codex-runtime",
        "fake-app-server-turn-input.txt"
      )
    );
    StringAssert.Contains(
      codexPrompt,
      "delete permission opencode: delete the file created by the earlier harness"
    );
    StringAssert.Contains(
      codexPrompt,
      "Canonical Agentic Router conversation delta since this harness last ran:"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenCodeNewSessionHydratesCanonicalConversationWithoutPromptPolicyInventory()
  {
    object[] history =
    [
      new { role = "user", content = "Native established the earlier goal." },
      new { role = "assistant", content = "Native created alpha.txt for that goal." }
    ];
    var events = await ExecuteHarnessStreamAsync(
      "qwen-code",
      "Continue by inspecting the file created earlier.",
      "browser-native-to-qwen-hydration",
      "qwen3.8:27b-gpu0",
      history
    );
    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));

    var runtime = Path.Combine(_environment.DataDirectory, "qwen-code-runtime");
    using var prompt = JsonDocument.Parse(
      await File.ReadAllTextAsync(Path.Combine(runtime, "fake-qwen-prompt.json"))
    );
    var text = prompt.RootElement.GetProperty("text").GetString()!;
    StringAssert.Contains(text, "Canonical Agentic Router conversation hydration:");
    StringAssert.Contains(text, "Native created alpha.txt for that goal.");
    Assert.DoesNotContain("Protected pre-existing paths", text);
    Assert.DoesNotContain("Do not use shell commands", text);
  }

  [TestMethod]
  [DataRow("native", "opencode")]
  [DataRow("opencode", "native")]
  [DataRow("native", "codex")]
  [DataRow("codex", "native")]
  [DataRow("opencode", "codex")]
  [DataRow("codex", "opencode")]
  [DataRow("native", "qwen-code")]
  [DataRow("qwen-code", "native")]
  [DataRow("opencode", "qwen-code")]
  [DataRow("qwen-code", "opencode")]
  [DataRow("codex", "qwen-code")]
  [DataRow("qwen-code", "codex")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HarnessSwitchDirectionsCarryCanonicalReferences(
    string sourceHarness,
    string targetHarness
  )
  {
    var conversationId = $"browser-switch-{sourceHarness}-{targetHarness}";
    var sourceModel = sourceHarness is "opencode" or "qwen-code"
      ? "qwen3.8:27b-gpu0"
      : "alpha:latest";
    var source = await ExecuteHarnessStreamAsync(
      sourceHarness,
      "read file for the source harness continuity turn",
      conversationId,
      sourceModel
    );
    Assert.HasCount(1, source.Where(IsTerminalStreamEvent));

    var marker = $"SOURCE-{sourceHarness.ToUpperInvariant()}-TO-{targetHarness.ToUpperInvariant()}";
    object[] history =
    [
      new
      {
        role = "user",
        content = "read file for the source harness continuity turn"
      },
      new
      {
        role = "assistant",
        content = $"The source harness concluded with canonical marker {marker}."
      }
    ];
    var requestCount = _environment.FakeOllama.Requests.Count;
    var targetModel = targetHarness is "opencode" or "qwen-code"
      ? "qwen3.8:27b-gpu0"
      : "alpha:latest";
    var target = await ExecuteHarnessStreamAsync(
      targetHarness,
      "read file and continue from the canonical marker",
      conversationId,
      targetModel,
      history
    );
    Assert.HasCount(1, target.Where(IsTerminalStreamEvent));

    if (string.Equals(targetHarness, "native", StringComparison.Ordinal))
    {
      Assert.IsTrue(
        _environment.FakeOllama.Requests
          .Skip(requestCount)
          .Any(
            request => request.Messages.Any(
              message => message.Content.Contains(marker, StringComparison.Ordinal)
            )
          ),
        $"Native did not receive the canonical {sourceHarness} history."
      );
      return;
    }

    var promptPath = targetHarness switch
    {
      "codex" => Path.Combine(
        _environment.DataDirectory,
        "codex-runtime",
        "fake-app-server-turn-input.txt"
      ),
      "qwen-code" => Path.Combine(
        _environment.DataDirectory,
        "qwen-code-runtime",
        "fake-qwen-prompt.json"
      ),
      _ => Path.Combine(
        _environment.DataDirectory,
        "opencode-runtime",
        "fake-opencode-prompt.json"
      )
    };
    var promptText = string.Equals(targetHarness, "codex", StringComparison.Ordinal)
      ? await File.ReadAllTextAsync(promptPath)
      : JsonNode.Parse(await File.ReadAllTextAsync(promptPath))!["text"]!.GetValue<string>();
    StringAssert.Contains(promptText, marker);
    StringAssert.Contains(promptText, "read file and continue from the canonical marker");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessResumesItsSessionAfterOwnedProcessRestart()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await SendMessageAsync("restart codex after completion");
    var firstAnswer = await Page.Locator(
      ".message.assistant .assistant-answer"
    ).Last.InnerTextAsync();
    var threadId = Regex.Match(firstAnswer, @"fake-thread-\d+").Value;
    Assert.IsFalse(string.IsNullOrWhiteSpace(threadId), firstAnswer);
    var runtime = Path.Combine(_environment.DataDirectory, "codex-runtime");
    await WaitUntilAsync(
      () => File.Exists(
        Path.Combine(runtime, "fake-app-server-exited-after-terminal.marker")
      ),
      TimeSpan.FromSeconds(5)
    );

    await SendMessageAsync("codex second turn");
    await Expect(
      Page.Locator(".message.assistant .assistant-answer").Last
    ).ToContainTextAsync(threadId);
    using var resumed = JsonDocument.Parse(
      await File.ReadAllTextAsync(
        Path.Combine(runtime, "fake-app-server-thread-resumed.json")
      )
    );
    Assert.AreEqual(
      threadId,
      resumed.RootElement.GetProperty("threadId").GetString()
    );
    Assert.AreEqual(
      Path.GetFullPath(_environment.WorkspaceDirectory),
      Path.GetFullPath(resumed.RootElement.GetProperty("cwd").GetString()!)
    );
    Assert.AreEqual(
      "alpha:latest",
      resumed.RootElement.GetProperty("model").GetString()
    );
    Assert.AreEqual(
      "ollama",
      resumed.RootElement.GetProperty("provider").GetString()
    );
    Assert.AreEqual(
      32_768,
      resumed.RootElement.GetProperty("contextWindowTokens").GetInt32()
    );
    Assert.AreEqual(
      32_112,
      resumed.RootElement.GetProperty("autoCompactTokenLimit").GetInt32()
    );
    Assert.AreEqual(
      "total",
      resumed.RootElement.GetProperty("autoCompactTokenLimitScope").GetString()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexManagedInstallDiscoverySurvivesVersionedPathChanges()
  {
    if (!OperatingSystem.IsWindows())
    {
      Assert.Inconclusive("Managed Codex discovery is a Windows integration.");
    }

    await _environment.UseManagedCodexInstallAndRestartAsync();

    try
    {
      using var response = await _environment.HttpClient.GetAsync("api/harnesses");
      response.EnsureSuccessStatusCode();
      var codex = JsonNode.Parse(await response.Content.ReadAsStringAsync())!
        .AsArray()
        .Single(
          item => item!["definition"]!["id"]!.GetValue<string>() == "codex"
        )!;
      Assert.IsTrue(codex["availability"]!["available"]!.GetValue<bool>());
      Assert.AreEqual(
        "codex-cli fake-0.148.0",
        codex["availability"]!["version"]!.GetValue<string>()
      );
    }
    finally
    {
      await _environment.SetCodexExecutableAndRestartAsync(
        _environment.FakeCodexExecutablePath
      );
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessPreservesChronologicalThinkingAndResponseItems()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await SendMessageAsync("chronological codex content");

    var assistant = Page.Locator(".message.assistant").Last;
    var timelineItems = assistant.Locator(
      ".assistant-work > [data-timeline-kind]"
    );
    await Expect(timelineItems).ToHaveCountAsync(5);
    var timelineKinds = await timelineItems.EvaluateAllAsync<string[]>(
      "nodes => nodes.map(node => node.dataset.timelineKind)"
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "thinking",
        "response",
        "thinking",
        "response",
        "response"
      },
      timelineKinds
    );

    await Expect(
      assistant.Locator(".assistant-reasoning[data-delta-count=\"2\"]")
    ).ToHaveCountAsync(2);
    await Expect(
      assistant.Locator(".assistant-response[data-delta-count=\"2\"]")
    ).ToHaveCountAsync(2);
    await Expect(
      assistant.Locator(".assistant-response[data-delta-count=\"1\"]")
    ).ToHaveCountAsync(1);
    await Expect(
      assistant.Locator(".assistant-reasoning").Nth(0)
    ).ToContainTextAsync("Inspecting — revisão the trusted workspace.");
    await Expect(
      assistant.Locator(".assistant-response").Nth(0).Locator("strong")
    ).ToHaveTextAsync("response");
    await Expect(
      assistant.Locator(".assistant-reasoning").Nth(1)
    ).ToContainTextAsync("Thinking again after the first response.");
    await Expect(
      assistant.Locator(".assistant-answer")
    ).ToContainTextAsync("Codex streamed with alpha:latest");
  }
}
