using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Execution;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class BenchmarkAndHarnessEndToEndTests : ChatEndToEndTestBase<BenchmarkAndHarnessEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HarnessRegistryDiscoversAvailableHarnessCapabilitiesAndVersions()
  {
    using var response = await _environment.HttpClient.GetAsync("api/harnesses");
    response.EnsureSuccessStatusCode();
    var statuses = JsonNode.Parse(await response.Content.ReadAsStringAsync())!
      .AsArray();
    Assert.HasCount(4, statuses);

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
        [HarnessIds.Native, HarnessIds.Codex, HarnessIds.OpenCode, HarnessIds.QwenCode],
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

    var firstHarnessCompletion = view.Events.First(
      item => item.Type == BenchmarkProgressTypeIds.HarnessCompleted
    ).Sequence;
    foreach (var harnessId in new[]
    {
      HarnessIds.Native,
      HarnessIds.Codex,
      HarnessIds.OpenCode,
      HarnessIds.QwenCode
    })
    {
      Assert.IsTrue(view.Events.Any(item =>
        item.Type == BenchmarkProgressTypeIds.HarnessStarted
        && item.Harness == harnessId
        && item.Sequence < firstHarnessCompletion
      ), harnessId);
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
      }
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
    Assert.AreEqual(
      2,
      qwen.Tests.Single(test => test.Run.TestId == BenchmarkIds.FileSystemRead001)
        .RawResult.ToolCallCount
    );
    Assert.IsTrue(persisted.Ranking.Any(entry => entry.Harness == HarnessIds.QwenCode));
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
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutomatedBenchmarkUiSelectsHarnessesRanksAndOpensCrudEvidence()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Expect(Page.Locator("#benchmark-dialog")).ToBeVisibleAsync();
    await Expect(Page.Locator("#benchmark-suite")).ToHaveValueAsync("basic-crud");
    await Expect(Page.Locator("#benchmark-harness-list input")).ToHaveCountAsync(4);
    await Expect(Page.Locator("#benchmark-harness-list input[value=\"qwen-code\"]"))
      .ToBeCheckedAsync();
    await Expect(Page.Locator("#benchmark-harness-list"))
      .ToContainTextAsync("Qwen Code [Experimental] · 0.21.13-fake");

    await Page.Locator("#benchmark-model").SelectOptionAsync("alpha:latest");
    await Page.Locator("#benchmark-harness-list input[value=\"native\"]").UncheckAsync();
    await Page.Locator("#benchmark-permission").CheckAsync();
    await Page.Locator("#benchmark-timeout").FillAsync("20");
    await Page.Locator("#run-benchmark").ClickAsync();

    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("Benchmark concluído", new() { Timeout = 30_000 });
    await Expect(Page.Locator("#benchmark-results-body tr")).ToHaveCountAsync(3);
    await Expect(Page.Locator("#benchmark-run-summary")).ToContainTextAsync("passed");
    await Expect(Page.Locator("#benchmark-results-body")).ToContainTextAsync("4/4");
    await Expect(Page.Locator("#benchmark-history")).Not.ToHaveValueAsync("");
    await Expect(Page.Locator("#benchmark-score-profile")).ToHaveTextAsync("Default v1");
    await Expect(Page.Locator("#benchmark-weight-total")).ToContainTextAsync("Total 100");

    await Page.Locator("#benchmark-weight-objective").FillAsync("0");
    await Page.Locator("#benchmark-weight-correctness").FillAsync("100");
    await Page.Locator("#benchmark-weight-terminality").FillAsync("0");
    await Page.Locator("#benchmark-weight-workspace").FillAsync("0");
    await Page.Locator("#benchmark-weight-efficiency").FillAsync("0");
    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("ranking recalculado", new() { Timeout = 10_000 });
    await Expect(Page.Locator("#benchmark-score-profile")).ToHaveTextAsync("Custom v1");
    await Expect(Page.Locator("#benchmark-score-context")).ToContainTextAsync("Measured evidence inalterada");

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
    await Page.Locator("#reset-benchmark-weights").ClickAsync();
    await Expect(Page.Locator("#benchmark-score-profile")).ToHaveTextAsync("Default v1");
    await Expect(Page.Locator("#benchmark-status")).ToContainTextAsync("Perfil Default restaurado");
    _environment.FakeOllama.RemoveLoadedModel("alpha:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LiveBenchmarkUiRendersProgressReopensAndSettlesCancellation()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#open-benchmarks").ClickAsync();
    await Page.Locator("#benchmark-model").SelectOptionAsync("docs:latest");
    await Page.Locator("#benchmark-harness-list input[value=\"codex\"]").UncheckAsync();
    await Page.Locator("#benchmark-harness-list input[value=\"opencode\"]").UncheckAsync();
    await Page.Locator("#benchmark-harness-list input[value=\"qwen-code\"]").UncheckAsync();
    await Page.Locator("#benchmark-permission").CheckAsync();
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
    await Expect(Page.Locator("#benchmark-dialog")).ToBeHiddenAsync();
    await Page.EvaluateAsync("document.getElementById('open-benchmarks').click()");
    await Expect(Page.Locator("#benchmark-dialog")).ToBeVisibleAsync();
    await Expect(Page.Locator("#benchmark-live-dashboard")).ToBeVisibleAsync();
    await Expect(Page.Locator("#cancel-benchmark")).ToBeEnabledAsync();
    await Page.Locator("#cancel-benchmark").ClickAsync();
    await Expect(Page.Locator("#benchmark-status"))
      .ToContainTextAsync("cancelada", new() { Timeout = 15_000 });
    await Expect(Page.Locator("#benchmark-live-dashboard")).ToBeHiddenAsync();
    await Expect(Page.Locator("#benchmark-ranking-note")).ToBeHiddenAsync();
    await Expect(Page.Locator("#benchmark-run-summary")).ToContainTextAsync("cancelled");
    await Expect(Page.Locator("#benchmark-history")).Not.ToHaveValueAsync("");
    _environment.FakeOllama.RemoveLoadedModel("docs:latest");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutomatedBenchmarkRejectsUnavailableHarnessBeforeExecution()
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
      Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
      var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
      Assert.IsNotNull(payload["errors"]!["harnesses"]);
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
      Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
      var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
      Assert.IsNotNull(payload["errors"]!["harnesses"]);
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
    await Expect(harness).ToBeDisabledAsync();
    await Expect(harness.Locator("option")).ToHaveTextAsync(
      new[]
      {
        "Native",
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
      .ToContainTextAsync("exato");
    await Expect(assistant.Locator(".activity"))
      .ToHaveAttributeAsync("data-terminal", "true");
    var capabilityProjection = assistant.Locator(
      "[data-event-type=\"execution-capability-profile-projected\"]"
    );
    await Expect(capabilityProjection).ToContainTextAsync(
      "native implementation [list_files, read_file, search_text"
    );
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

    static bool ProcessIsAlive(int id)
    {
      try
      {
        using var process = Process.GetProcessById(id);
        return !process.HasExited;
      }
      catch (ArgumentException)
      {
        return false;
      }
    }
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
    Assert.AreEqual(
      "exact",
      contextEvents.Last()["contextUsage"]!["accuracy"]!.GetValue<string>()
    );
    Assert.AreEqual(
      1_234,
      contextEvents.Last()["contextUsage"]!["inputTokens"]!.GetValue<long>()
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
    await Page.Locator("#send-button").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Enviar");
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
      .ToContainTextAsync("exato");
    await Expect(assistant.Locator(".activity"))
      .ToHaveAttributeAsync("data-terminal", "true");
    var capabilityProjection = assistant.Locator(
      "[data-event-type=\"execution-capability-profile-projected\"]"
    );
    await Expect(capabilityProjection).ToContainTextAsync(
      "native implementation [list_files, read_file, search_text"
    );
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
    Assert.HasCount(9, coreTools);
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
    Assert.AreEqual(
      "exact",
      contextEvents.Last()["contextUsage"]!["accuracy"]!.GetValue<string>()
    );
    Assert.AreEqual(
      4_321,
      contextEvents.Last()["contextUsage"]!["inputTokens"]!.GetValue<long>()
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
      new() { Name = "Aprovar", Exact = true }
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
      new() { Name = "Aprovar", Exact = true }
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
    await Page.Locator("#send-button").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Enviar");
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
    StringAssert.Contains(
      await File.ReadAllTextAsync(Path.Combine(codexRuntime, "config.toml")),
      "model_provider = \"ollama\""
    );
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
