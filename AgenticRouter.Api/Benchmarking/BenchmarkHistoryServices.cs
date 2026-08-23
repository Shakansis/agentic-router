using System.Reflection;
using System.Runtime.InteropServices;
using AgenticRouter.Api.Runtime;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkEnvironmentSnapshotProvider
{
  BenchmarkEnvironmentIdentity Capture(
    string runtime,
    string? runtimeVersion,
    bool sequential,
    int? configuredContextTokens
  );
}

public sealed class BenchmarkEnvironmentSnapshotProvider
  : IBenchmarkEnvironmentSnapshotProvider
{
  private readonly IGpuMemoryMetricsProvider _gpuMemory;
  private readonly ISystemMemoryMetricsProvider _systemMemory;

  public BenchmarkEnvironmentSnapshotProvider(
    IGpuMemoryMetricsProvider gpuMemory,
    ISystemMemoryMetricsProvider systemMemory
  )
  {
    _gpuMemory = gpuMemory;
    _systemMemory = systemMemory;
  }

  public BenchmarkEnvironmentIdentity Capture(
    string runtime,
    string? runtimeVersion,
    bool sequential,
    int? configuredContextTokens
  )
  {
    var gpu = SafeGpuSnapshot();
    var ram = SafeRamSnapshot();
    var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
    var informationalVersion = assembly
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
      ?.InformationalVersion;
    var hostVersion = assembly.GetName().Version?.ToString();
    var commit = CommitFrom(informationalVersion);
    return new BenchmarkEnvironmentIdentity(
      runtime,
      runtimeVersion,
      sequential,
      configuredContextTokens,
      DateTimeOffset.UtcNow,
      Detected(RuntimeInformation.OSDescription),
      Detected(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")),
      gpu.Devices.Select(device => new BenchmarkGpuIdentity(
        device.Id,
        Detected(device.Name),
        device.TotalDedicatedMemoryBytes is null
          ? Unavailable(device.Diagnostic)
          : Detected(device.TotalDedicatedMemoryBytes.Value.ToString(), "bytes")
      )).ToArray(),
      ram.TotalBytes is null
        ? Unavailable(ram.Diagnostic)
        : Detected(ram.TotalBytes.Value.ToString(), "bytes"),
      Detected(hostVersion ?? informationalVersion),
      Detected(commit),
      string.IsNullOrWhiteSpace(runtimeVersion)
        ? BenchmarkEvidenceStatusIds.Unavailable
        : BenchmarkEvidenceStatusIds.Detected
    );
  }

  private GpuMemoryMetricsSnapshot SafeGpuSnapshot()
  {
    try
    {
      return _gpuMemory.GetStatus();
    }
    catch (Exception exception)
    {
      return new GpuMemoryMetricsSnapshot(
        [],
        BenchmarkEvidenceStatusIds.Unavailable,
        exception.Message
      );
    }
  }

  private AgenticRouter.Api.Contracts.SystemMemoryStatus SafeRamSnapshot()
  {
    try
    {
      return _systemMemory.GetStatus();
    }
    catch (Exception exception)
    {
      return new AgenticRouter.Api.Contracts.SystemMemoryStatus(
        null,
        null,
        null,
        null,
        BenchmarkEvidenceStatusIds.Unavailable,
        exception.Message
      );
    }
  }

  private static BenchmarkEvidenceValue Detected(
    string? value,
    string? unit = null
  )
  {
    return string.IsNullOrWhiteSpace(value)
      ? Unavailable()
      : new BenchmarkEvidenceValue(
        BenchmarkEvidenceStatusIds.Detected,
        value.Trim(),
        unit
      );
  }

  private static BenchmarkEvidenceValue Unavailable(string? diagnostic = null)
  {
    return new BenchmarkEvidenceValue(
      BenchmarkEvidenceStatusIds.Unavailable,
      null,
      Diagnostic: diagnostic
    );
  }

  private static string? CommitFrom(string? informationalVersion)
  {
    if (string.IsNullOrWhiteSpace(informationalVersion))
    {
      return null;
    }
    var separator = informationalVersion.LastIndexOf('+');
    return separator >= 0 && separator < informationalVersion.Length - 1
      ? informationalVersion[(separator + 1)..]
      : null;
  }
}

public interface IBenchmarkHistoryService
{
  Task<IReadOnlyList<BenchmarkHistorySummary>> ListAsync(
    int limit,
    string? model,
    string? harness,
    string? suite,
    CancellationToken cancellationToken
  );

  Task<BenchmarkHistoricalComparison?> CompareAsync(
    BenchmarkComparisonRequest request,
    CancellationToken cancellationToken
  );

  BenchmarkComparabilityAssessment AssessComparability(
    BenchmarkSuiteRunResult baseline,
    BenchmarkSuiteRunResult candidate
  );
}

public sealed class BenchmarkHistoryService : IBenchmarkHistoryService
{
  private readonly IBenchmarkResultStore _results;
  private readonly IBenchmarkScorer _scorer;
  private readonly IBenchmarkScoringProfileStore _profiles;

  public BenchmarkHistoryService(
    IBenchmarkResultStore results,
    IBenchmarkScorer scorer,
    IBenchmarkScoringProfileStore profiles
  )
  {
    _results = results;
    _scorer = scorer;
    _profiles = profiles;
  }

  public async Task<IReadOnlyList<BenchmarkHistorySummary>> ListAsync(
    int limit,
    string? model,
    string? harness,
    string? suite,
    CancellationToken cancellationToken
  )
  {
    var profile = await _profiles.GetAsync(cancellationToken);
    var results = await _results.ListAsync(100, cancellationToken);
    return results
      .Where(result => Matches(GetModels(result), model))
      .Where(result => Matches(GetHarnesses(result), harness))
      .Where(result => string.IsNullOrWhiteSpace(suite)
        || string.Equals(result.SuiteId, suite.Trim(), StringComparison.OrdinalIgnoreCase))
      .Take(Math.Clamp(limit, 1, 100))
      .Select(result => Summarize(result, profile))
      .ToArray();
  }

  public async Task<BenchmarkHistoricalComparison?> CompareAsync(
    BenchmarkComparisonRequest request,
    CancellationToken cancellationToken
  )
  {
    var baseline = await _results.GetAsync(
      request.BaselineRunId,
      cancellationToken
    );
    var candidate = await _results.GetAsync(
      request.CandidateRunId,
      cancellationToken
    );
    if (baseline is null || candidate is null)
    {
      return null;
    }
    var profile = await _profiles.GetAsync(cancellationToken);
    var assessment = AssessComparability(baseline, candidate);
    return new BenchmarkHistoricalComparison(
      Summarize(baseline, profile),
      Summarize(candidate, profile),
      assessment.Classification,
      assessment.Reasons,
      ChangedMetadata(baseline, candidate),
      CalculateDeltas(baseline, candidate, profile),
      string.Equals(
        assessment.Classification,
        BenchmarkComparabilityIds.Comparable,
        StringComparison.Ordinal
      )
        ? CalculateSignals(baseline, candidate, profile)
        : []
    );
  }

  private BenchmarkHistorySummary Summarize(
    BenchmarkSuiteRunResult result,
    BenchmarkScoringProfile profile
  )
  {
    var tests = Tests(result).ToArray();
    var projected = tests.Select(test => _scorer.Score(
      test.Result.RawResult,
      profile.Weights
    )).ToArray();
    var originalScore = OriginalAggregateScore(result);
    return new BenchmarkHistorySummary(
      result.RunId,
      result.StartedAt,
      result.SuiteId,
      result.SuiteVersion,
      result.FixtureId,
      result.FixtureVersion,
      GetModels(result),
      GetHarnesses(result),
      result.FinalStatus,
      Math.Max(0, result.DurationMilliseconds),
      result.SchemaVersion,
      tests.Count(test => string.Equals(
        test.Result.RawResult.Status,
        BenchmarkResultStatusIds.Pass,
        StringComparison.Ordinal
      )),
      tests.Length,
      originalScore,
      Average(projected.Select(score => score.Total)),
      result.ScoringProfileId,
      result.ScoringProfileVersion,
      profile.Id,
      profile.Version
    );
  }

  public BenchmarkComparabilityAssessment AssessComparability(
    BenchmarkSuiteRunResult baseline,
    BenchmarkSuiteRunResult candidate
  )
  {
    var incompatible = new List<string>();
    var partial = new List<string>();
    if (!string.Equals(baseline.SuiteId, candidate.SuiteId, StringComparison.OrdinalIgnoreCase))
    {
      incompatible.Add("Benchmark suites differ.");
    }
    if (baseline.SuiteVersion != candidate.SuiteVersion)
    {
      incompatible.Add("Benchmark suite versions differ.");
    }
    if (!string.Equals(baseline.FixtureId, candidate.FixtureId, StringComparison.OrdinalIgnoreCase)
      || baseline.FixtureVersion != candidate.FixtureVersion)
    {
      incompatible.Add("Fixture identities or versions differ.");
    }
    var baselineModels = GetModels(baseline);
    var candidateModels = GetModels(candidate);
    var baselineHarnesses = GetHarnesses(baseline);
    var candidateHarnesses = GetHarnesses(candidate);
    if (!baselineModels.Intersect(candidateModels, StringComparer.OrdinalIgnoreCase).Any()
      && !baselineHarnesses.Intersect(candidateHarnesses, StringComparer.OrdinalIgnoreCase).Any())
    {
      incompatible.Add("The runs share neither a model nor a harness identity.");
    }
    if (incompatible.Count > 0)
    {
      return new BenchmarkComparabilityAssessment(
        BenchmarkComparabilityIds.NotDirectlyComparable,
        incompatible
      );
    }

    CompareSets(baselineModels, candidateModels, "Model selections differ.", partial);
    CompareSets(baselineHarnesses, candidateHarnesses, "Harness selections differ.", partial);
    CompareModelDigests(baseline, candidate, partial);
    CompareHarnessVersions(baseline, candidate, partial);
    CompareValue(
      baseline.Provider,
      candidate.Provider,
      "Providers differ.",
      "Provider identity is unavailable.",
      partial
    );
    CompareValue(
      baseline.Environment?.RuntimeVersion,
      candidate.Environment?.RuntimeVersion,
      "Runtime versions differ.",
      "Runtime version evidence is unavailable.",
      partial
    );
    CompareValue(
      baseline.Environment?.ConfiguredContextTokens?.ToString(),
      candidate.Environment?.ConfiguredContextTokens?.ToString(),
      "Configured runtime contexts differ.",
      "Configured runtime context evidence is unavailable.",
      partial
    );
    if (baseline.Configuration is null || candidate.Configuration is null)
    {
      partial.Add("Relevant benchmark configuration evidence is unavailable.");
    }
    else if (!string.Equals(
      baseline.Configuration.Fingerprint,
      candidate.Configuration.Fingerprint,
      StringComparison.Ordinal
    ))
    {
      partial.Add("Relevant benchmark configuration differs.");
    }
    var baselineHardware = HardwareFingerprint(baseline.Environment);
    var candidateHardware = HardwareFingerprint(candidate.Environment);
    if (baselineHardware is null || candidateHardware is null)
    {
      partial.Add("Complete hardware evidence is unavailable.");
    }
    else if (!string.Equals(baselineHardware, candidateHardware, StringComparison.Ordinal))
    {
      partial.Add("Hardware conditions differ.");
    }
    return new BenchmarkComparabilityAssessment(
      partial.Count == 0
        ? BenchmarkComparabilityIds.Comparable
        : BenchmarkComparabilityIds.PartiallyComparable,
      partial.Distinct(StringComparer.Ordinal).ToArray()
    );
  }

  private IReadOnlyList<BenchmarkMetricDelta> CalculateDeltas(
    BenchmarkSuiteRunResult baseline,
    BenchmarkSuiteRunResult candidate,
    BenchmarkScoringProfile profile
  )
  {
    var left = Metrics(baseline, profile);
    var right = Metrics(candidate, profile);
    var deltas = new List<BenchmarkMetricDelta>
    {
      Delta("score", left.Score, right.Score, "points"),
      Delta("passed-tests", left.Passed, right.Passed, "tests"),
      Delta("terminality", left.Terminality, right.Terminality, "points"),
      Delta("correctness", left.Correctness, right.Correctness, "points"),
      Delta("duration", left.Duration, right.Duration, "ms")
    };
    AddOptionalDelta(deltas, "recovery", left.Recovery, right.Recovery);
    AddOptionalDelta(deltas, "convergence", left.Convergence, right.Convergence);
    AddOptionalDelta(deltas, "hygiene", left.Hygiene, right.Hygiene);
    return deltas;
  }

  private IReadOnlyList<BenchmarkRegressionSignal> CalculateSignals(
    BenchmarkSuiteRunResult baseline,
    BenchmarkSuiteRunResult candidate,
    BenchmarkScoringProfile profile
  )
  {
    var signals = new List<BenchmarkRegressionSignal>();
    var baselineTests = Tests(baseline).ToDictionary(
      test => test.Key,
      StringComparer.OrdinalIgnoreCase
    );
    foreach (var current in Tests(candidate))
    {
      if (!baselineTests.TryGetValue(current.Key, out var previous))
      {
        continue;
      }
      var before = previous.Result.RawResult;
      var after = current.Result.RawResult;
      if (string.Equals(before.Status, BenchmarkResultStatusIds.Pass, StringComparison.Ordinal)
        && !string.Equals(after.Status, BenchmarkResultStatusIds.Pass, StringComparison.Ordinal))
      {
        signals.Add(Signal("pass-to-fail", "regression", "PASS changed to FAIL or ERROR.", current));
      }
      if (!string.Equals(before.ExecutionStatus, BenchmarkExecutionStatusIds.TimedOut, StringComparison.Ordinal)
        && string.Equals(after.ExecutionStatus, BenchmarkExecutionStatusIds.TimedOut, StringComparison.Ordinal))
      {
        signals.Add(Signal("terminal-to-timeout", "regression", "A terminal result changed to timeout.", current));
      }
      if (before.ObjectiveAchieved && !after.ObjectiveAchieved && after.UsefulPartialOutcome)
      {
        signals.Add(Signal("correct-to-partial", "regression", "A correct result changed to a partial outcome.", current));
      }
      if (UnexpectedMutationCount(before) == 0 && UnexpectedMutationCount(after) > 0)
      {
        signals.Add(Signal("unexpected-mutation", "regression", "A new unexpected workspace mutation was observed.", current));
      }
      var scoreDelta = _scorer.Score(after, profile.Weights).Total
        - _scorer.Score(before, profile.Weights).Total;
      if (Math.Abs(scoreDelta) >= 1m)
      {
        signals.Add(Signal(
          "score-change",
          scoreDelta < 0 ? "regression" : "improvement",
          $"Current-profile score changed by {scoreDelta:+0.##;-0.##;0} points.",
          current
        ));
      }
    }
    var durationDelta = candidate.DurationMilliseconds - baseline.DurationMilliseconds;
    var meaningfulDuration = Math.Max(100m, Math.Abs(baseline.DurationMilliseconds) * 0.1m);
    if (Math.Abs(durationDelta) >= meaningfulDuration)
    {
      signals.Add(new BenchmarkRegressionSignal(
        "duration-change",
        durationDelta > 0 ? "regression" : "improvement",
        $"Duration changed by {durationDelta:+#;-#;0} ms."
      ));
    }
    return signals;
  }

  private static IReadOnlyList<BenchmarkMetadataChange> ChangedMetadata(
    BenchmarkSuiteRunResult baseline,
    BenchmarkSuiteRunResult candidate
  )
  {
    var changes = new List<BenchmarkMetadataChange>();
    AddChange(changes, "suite", $"{baseline.SuiteId} v{baseline.SuiteVersion}", $"{candidate.SuiteId} v{candidate.SuiteVersion}");
    AddChange(changes, "fixture", $"{baseline.FixtureId} v{baseline.FixtureVersion}", $"{candidate.FixtureId} v{candidate.FixtureVersion}");
    AddChange(changes, "models", string.Join(", ", GetModels(baseline)), string.Join(", ", GetModels(candidate)));
    AddChange(changes, "harnesses", string.Join(", ", GetHarnesses(baseline)), string.Join(", ", GetHarnesses(candidate)));
    AddChange(changes, "model-digests", DigestSummary(baseline), DigestSummary(candidate));
    AddChange(changes, "harness-versions", HarnessVersionSummary(baseline), HarnessVersionSummary(candidate));
    AddChange(changes, "runtime", RuntimeSummary(baseline), RuntimeSummary(candidate));
    AddChange(changes, "hardware", HardwareFingerprint(baseline.Environment) ?? "unavailable", HardwareFingerprint(candidate.Environment) ?? "unavailable");
    AddChange(changes, "configuration", baseline.Configuration?.Fingerprint ?? "unavailable", candidate.Configuration?.Fingerprint ?? "unavailable");
    AddChange(
      changes,
      "original-scoring-profile",
      ScoringSummary(baseline),
      ScoringSummary(candidate)
    );
    return changes;
  }

  private MetricsSnapshot Metrics(
    BenchmarkSuiteRunResult result,
    BenchmarkScoringProfile profile
  )
  {
    var tests = Tests(result).ToArray();
    var scores = tests.Select(test => _scorer.Score(test.Result.RawResult, profile.Weights)).ToArray();
    return new MetricsSnapshot(
      Average(scores.Select(score => score.Total)),
      tests.Count(test => string.Equals(test.Result.RawResult.Status, BenchmarkResultStatusIds.Pass, StringComparison.Ordinal)),
      Average(scores.Select(score => (decimal)score.Terminality)),
      Average(scores.Select(score => (decimal)score.Correctness)),
      Math.Max(0, result.DurationMilliseconds),
      AverageOptional(tests.Select(test => test.Result.RawResult.BehaviorMetrics?.Recovery)),
      AverageOptional(tests.Select(test => test.Result.RawResult.BehaviorMetrics?.Convergence)),
      AverageOptional(tests.Select(test => test.Result.RawResult.BehaviorMetrics?.Hygiene))
    );
  }

  private static IEnumerable<HistoricalTest> Tests(BenchmarkSuiteRunResult result)
  {
    if (result.Cells is { Count: > 0 })
    {
      return result.Cells.Where(cell => cell.Result is not null)
        .SelectMany(cell => cell.Result!.Tests.Select(test => new HistoricalTest(
          $"{cell.Model}|{cell.Harness}|{test.Run.TestId}",
          cell.Model,
          cell.Harness,
          test
        )));
    }
    return result.HarnessResults.SelectMany(harness => harness.Tests.Select(test =>
      new HistoricalTest(
        $"{result.Model}|{harness.Harness}|{test.Run.TestId}",
        result.Model,
        harness.Harness,
        test
      )));
  }

  private static IReadOnlyList<string> GetModels(BenchmarkSuiteRunResult result)
  {
    return result.SelectedModels is { Count: > 0 }
      ? result.SelectedModels
      : result.Cells is { Count: > 0 }
        ? result.Cells.Select(cell => cell.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        : [result.Model];
  }

  private static IReadOnlyList<string> GetHarnesses(BenchmarkSuiteRunResult result)
  {
    return result.SelectedHarnesses is { Count: > 0 }
      ? result.SelectedHarnesses
      : result.Cells is { Count: > 0 }
        ? result.Cells.Select(cell => cell.Harness).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        : result.HarnessResults.Select(harness => harness.Harness).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }

  private static decimal OriginalAggregateScore(BenchmarkSuiteRunResult result)
  {
    return result.Cells is { Count: > 0 }
      ? Average(result.Cells.Select(cell => cell.Score))
      : Average(result.HarnessResults.Select(harness => harness.Score));
  }

  private static bool Matches(IReadOnlyList<string> values, string? filter)
  {
    return string.IsNullOrWhiteSpace(filter) || values.Any(value => value.Contains(
      filter.Trim(),
      StringComparison.OrdinalIgnoreCase
    ));
  }

  private static void CompareSets(
    IReadOnlyList<string> baseline,
    IReadOnlyList<string> candidate,
    string reason,
    ICollection<string> reasons
  )
  {
    if (!baseline.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).SequenceEqual(
      candidate.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
      StringComparer.OrdinalIgnoreCase
    ))
    {
      reasons.Add(reason);
    }
  }

  private static void CompareModelDigests(
    BenchmarkSuiteRunResult baseline,
    BenchmarkSuiteRunResult candidate,
    ICollection<string> reasons
  )
  {
    var left = ModelDigests(baseline);
    var right = ModelDigests(candidate);
    var leftIdentities = ModelIdentities(baseline);
    var rightIdentities = ModelIdentities(candidate);
    foreach (var model in GetModels(baseline).Intersect(GetModels(candidate), StringComparer.OrdinalIgnoreCase))
    {
      left.TryGetValue(model, out var before);
      right.TryGetValue(model, out var after);
      CompareValue(before, after, $"Model digest differs for '{model}'.", $"Model digest evidence is unavailable for '{model}'.", reasons);
      leftIdentities.TryGetValue(model, out var beforeIdentity);
      rightIdentities.TryGetValue(model, out var afterIdentity);
      CompareValue(
        beforeIdentity?.Quantization,
        afterIdentity?.Quantization,
        $"Model quantization differs for '{model}'.",
        $"Model quantization evidence is unavailable for '{model}'.",
        reasons
      );
      CompareValue(
        beforeIdentity?.DeclaredContextTokens?.ToString(),
        afterIdentity?.DeclaredContextTokens?.ToString(),
        $"Declared model context differs for '{model}'.",
        $"Declared model context evidence is unavailable for '{model}'.",
        reasons
      );
      CompareValue(
        beforeIdentity?.ConfiguredContextTokens?.ToString(),
        afterIdentity?.ConfiguredContextTokens?.ToString(),
        $"Configured model context differs for '{model}'.",
        $"Configured model context evidence is unavailable for '{model}'.",
        reasons
      );
      CompareOptionalValue(
        beforeIdentity?.ObservedContextTokens?.ToString(),
        afterIdentity?.ObservedContextTokens?.ToString(),
        $"Observed model context differs for '{model}'.",
        reasons
      );
    }
  }

  private static void CompareHarnessVersions(
    BenchmarkSuiteRunResult baseline,
    BenchmarkSuiteRunResult candidate,
    ICollection<string> reasons
  )
  {
    var left = HarnessVersions(baseline);
    var right = HarnessVersions(candidate);
    foreach (var harness in GetHarnesses(baseline).Intersect(GetHarnesses(candidate), StringComparer.OrdinalIgnoreCase))
    {
      left.TryGetValue(harness, out var before);
      right.TryGetValue(harness, out var after);
      CompareValue(before, after, $"Harness version differs for '{harness}'.", $"Harness version evidence is unavailable for '{harness}'.", reasons);
    }
  }

  private static void CompareValue(
    string? baseline,
    string? candidate,
    string changedReason,
    string unavailableReason,
    ICollection<string> reasons
  )
  {
    if (string.IsNullOrWhiteSpace(baseline) || string.IsNullOrWhiteSpace(candidate))
    {
      reasons.Add(unavailableReason);
    }
    else if (!string.Equals(baseline, candidate, StringComparison.OrdinalIgnoreCase))
    {
      reasons.Add(changedReason);
    }
  }

  private static Dictionary<string, string?> ModelDigests(BenchmarkSuiteRunResult result)
  {
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (result.ModelIdentities is not null)
    {
      foreach (var identity in result.ModelIdentities)
      {
        values[identity.Model] = identity.Digest;
      }
    }
    if (GetModels(result).Count == 1 && !values.ContainsKey(GetModels(result)[0]))
    {
      values[GetModels(result)[0]] = result.ModelDigest;
    }
    return values;
  }

  private static Dictionary<string, BenchmarkModelIdentity> ModelIdentities(
    BenchmarkSuiteRunResult result
  )
  {
    return (result.ModelIdentities ?? []).ToDictionary(
      identity => identity.Model,
      StringComparer.OrdinalIgnoreCase
    );
  }

  private static void CompareOptionalValue(
    string? baseline,
    string? candidate,
    string changedReason,
    ICollection<string> reasons
  )
  {
    if (!string.IsNullOrWhiteSpace(baseline)
      && !string.IsNullOrWhiteSpace(candidate)
      && !string.Equals(baseline, candidate, StringComparison.OrdinalIgnoreCase))
    {
      reasons.Add(changedReason);
    }
  }

  private static Dictionary<string, string?> HarnessVersions(BenchmarkSuiteRunResult result)
  {
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (result.HarnessIdentities is not null)
    {
      foreach (var identity in result.HarnessIdentities)
      {
        values[identity.Harness] = identity.Version;
      }
    }
    foreach (var cell in result.Cells ?? [])
    {
      values.TryAdd(cell.Harness, cell.HarnessVersion);
    }
    foreach (var harness in result.HarnessResults)
    {
      values.TryAdd(harness.Harness, harness.HarnessVersion);
    }
    return values;
  }

  private static string DigestSummary(BenchmarkSuiteRunResult result)
  {
    return string.Join(", ", ModelDigests(result).OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value ?? "unavailable"}"));
  }

  private static string HarnessVersionSummary(BenchmarkSuiteRunResult result)
  {
    return string.Join(", ", HarnessVersions(result).OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value ?? "unavailable"}"));
  }

  private static string RuntimeSummary(BenchmarkSuiteRunResult result)
  {
    return $"{result.Provider}|{result.Environment?.Runtime ?? "unavailable"}|{result.Environment?.RuntimeVersion ?? "unavailable"}";
  }

  private static string ScoringSummary(BenchmarkSuiteRunResult result)
  {
    return $"{result.ScoringProfileId} v{result.ScoringProfileVersion?.ToString() ?? "unavailable"} "
      + $"[{result.ScoreWeights.ObjectiveSuccess},{result.ScoreWeights.Correctness},"
      + $"{result.ScoreWeights.Terminality},{result.ScoreWeights.WorkspaceAccuracy},"
      + $"{result.ScoreWeights.Efficiency}]";
  }

  private static string? HardwareFingerprint(BenchmarkEnvironmentIdentity? environment)
  {
    if (environment?.OperatingSystem?.Value is null
      || environment.Cpu?.Value is null
      || environment.Ram?.Value is null
      || environment.Gpus is null
      || environment.Gpus.Count == 0
      || environment.Gpus.Any(gpu => gpu.Name.Value is null || gpu.Vram.Value is null))
    {
      return null;
    }
    return string.Join("|", new[]
    {
      environment.OperatingSystem.Value,
      environment.Cpu.Value,
      environment.Ram.Value,
      string.Join(",", environment.Gpus.OrderBy(gpu => gpu.Id, StringComparer.Ordinal).Select(gpu => $"{gpu.Id}:{gpu.Name.Value}:{gpu.Vram.Value}"))
    });
  }

  private static void AddChange(
    ICollection<BenchmarkMetadataChange> changes,
    string field,
    string baseline,
    string candidate
  )
  {
    if (!string.Equals(baseline, candidate, StringComparison.Ordinal))
    {
      changes.Add(new BenchmarkMetadataChange(field, baseline, candidate));
    }
  }

  private static BenchmarkMetricDelta Delta(
    string metric,
    decimal baseline,
    decimal candidate,
    string unit
  )
  {
    return new BenchmarkMetricDelta(metric, baseline, candidate, candidate - baseline, unit);
  }

  private static void AddOptionalDelta(
    ICollection<BenchmarkMetricDelta> deltas,
    string metric,
    decimal? baseline,
    decimal? candidate
  )
  {
    if (baseline is not null && candidate is not null)
    {
      deltas.Add(Delta(metric, baseline.Value, candidate.Value, "points"));
    }
  }

  private static BenchmarkRegressionSignal Signal(
    string kind,
    string direction,
    string message,
    HistoricalTest test
  )
  {
    return new BenchmarkRegressionSignal(
      kind,
      direction,
      message,
      test.Model,
      test.Harness,
      test.Result.Run.TestId
    );
  }

  private static int UnexpectedMutationCount(BenchmarkRawResult result)
  {
    return result.UnexpectedCreatedFiles.Count
      + result.UnexpectedModifiedFiles.Count
      + result.UnexpectedDeletedFiles.Count;
  }

  private static decimal Average(IEnumerable<decimal> values)
  {
    var materialized = values.ToArray();
    return materialized.Length == 0
      ? 0
      : decimal.Round(materialized.Average(), 2, MidpointRounding.AwayFromZero);
  }

  private static decimal? AverageOptional(IEnumerable<int?> values)
  {
    var materialized = values.Where(value => value.HasValue).Select(value => (decimal)value!.Value).ToArray();
    return materialized.Length == 0 ? null : Average(materialized);
  }

  private sealed record HistoricalTest(
    string Key,
    string Model,
    string Harness,
    BenchmarkRunResult Result
  );

  private sealed record MetricsSnapshot(
    decimal Score,
    decimal Passed,
    decimal Terminality,
    decimal Correctness,
    decimal Duration,
    decimal? Recovery,
    decimal? Convergence,
    decimal? Hygiene
  );
}
