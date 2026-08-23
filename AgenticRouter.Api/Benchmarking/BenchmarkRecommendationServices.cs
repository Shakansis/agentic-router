using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkRecommendationStore
{
  Task<BenchmarkRecommendationResult?> GetAsync(
    string recommendationId,
    CancellationToken cancellationToken
  );

  Task SaveAsync(
    BenchmarkRecommendationResult result,
    CancellationToken cancellationToken
  );
}

public sealed class JsonBenchmarkRecommendationStore : IBenchmarkRecommendationStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
  };
  private readonly string _directory;
  private readonly SemaphoreSlim _gate = new(1, 1);

  public JsonBenchmarkRecommendationStore(string dataDirectory)
  {
    _directory = Path.Combine(
      Path.GetFullPath(dataDirectory),
      "benchmark-recommendations"
    );
  }

  public async Task<BenchmarkRecommendationResult?> GetAsync(
    string recommendationId,
    CancellationToken cancellationToken
  )
  {
    var path = Resolve(recommendationId);
    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (!File.Exists(path))
      {
        return null;
      }
      var json = await File.ReadAllTextAsync(path, cancellationToken);
      return JsonSerializer.Deserialize<BenchmarkRecommendationResult>(
        json,
        JsonOptions
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task SaveAsync(
    BenchmarkRecommendationResult result,
    CancellationToken cancellationToken
  )
  {
    var path = Resolve(result.RecommendationId);
    await _gate.WaitAsync(cancellationToken);
    try
    {
      Directory.CreateDirectory(_directory);
      if (File.Exists(path))
      {
        return;
      }
      var temporary = path + $".{Guid.NewGuid():N}.tmp";
      try
      {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(
          temporary,
          json,
          new UTF8Encoding(false),
          cancellationToken
        );
        File.Move(temporary, path, false);
      }
      finally
      {
        if (File.Exists(temporary))
        {
          File.Delete(temporary);
        }
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  private string Resolve(string recommendationId)
  {
    var normalized = recommendationId.Trim().ToLowerInvariant();
    if (normalized.Length != 64 || normalized.Any(character =>
      !Uri.IsHexDigit(character)))
    {
      throw new BenchmarkRequestException(
        "benchmark-recommendation-id-invalid",
        "Benchmark recommendation id must be a SHA-256 identifier.",
        "recommendationId"
      );
    }
    return Path.Combine(_directory, normalized + ".json");
  }
}

public interface IBenchmarkRecommendationService
{
  Task<BenchmarkRecommendationCatalog> GetCatalogAsync(
    CancellationToken cancellationToken
  );

  Task<BenchmarkRecommendationResult> RecommendAsync(
    BenchmarkRecommendationRequest request,
    CancellationToken cancellationToken
  );

  Task<BenchmarkRecommendationResult?> GetAsync(
    string recommendationId,
    CancellationToken cancellationToken
  );
}

public sealed class BenchmarkRecommendationService : IBenchmarkRecommendationService
{
  public const string AlgorithmVersion = "benchmark-recommendation-v1";

  private static readonly IReadOnlyList<BenchmarkRecommendationCategory> Categories =
  [
    new(
      BenchmarkRecommendationCategoryIds.GeneralCoding,
      "General coding",
      "Uses the active scoring profile across all available scenarios.",
      ["active-profile score", "correctness", "terminality", "workspace", "efficiency"]
    ),
    new(
      BenchmarkRecommendationCategoryIds.ExactFilesystem,
      "Exact filesystem work",
      "Prioritizes correctness, workspace accuracy, and hygiene in filesystem scenarios.",
      ["correctness", "workspace accuracy", "hygiene"]
    ),
    new(
      BenchmarkRecommendationCategoryIds.LongContinuity,
      "Long continuity",
      "Uses continuity, scope retention, stale-conflict, correctness, and terminality evidence.",
      ["continuity", "correctness", "terminality"]
    ),
    new(
      BenchmarkRecommendationCategoryIds.RecoveryHeavy,
      "Recovery-heavy tasks",
      "Uses recovery, stale-conflict, truthful-report, correctness, and terminality evidence.",
      ["recovery", "correctness", "terminality"]
    ),
    new(
      BenchmarkRecommendationCategoryIds.CorrectnessFirst,
      "Correctness first",
      "Prioritizes measured correctness while retaining active-profile context.",
      ["correctness", "active-profile score"]
    ),
    new(
      BenchmarkRecommendationCategoryIds.TerminalityFirst,
      "Terminality first",
      "Prioritizes terminal completion while retaining active-profile context.",
      ["terminality", "active-profile score"]
    ),
    new(
      BenchmarkRecommendationCategoryIds.EfficiencyFirst,
      "Efficiency first",
      "Prioritizes measured efficiency while retaining active-profile context.",
      ["efficiency", "duration", "active-profile score"]
    )
  ];

  private readonly IBenchmarkResultStore _results;
  private readonly IBenchmarkScorer _scorer;
  private readonly IBenchmarkScoringProfileStore _profiles;
  private readonly IBenchmarkHistoryService _history;
  private readonly IBenchmarkRecommendationStore _recommendations;
  private readonly IOllamaWebSearchService _webSearch;

  public BenchmarkRecommendationService(
    IBenchmarkResultStore results,
    IBenchmarkScorer scorer,
    IBenchmarkScoringProfileStore profiles,
    IBenchmarkHistoryService history,
    IBenchmarkRecommendationStore recommendations,
    IOllamaWebSearchService webSearch
  )
  {
    _results = results;
    _scorer = scorer;
    _profiles = profiles;
    _history = history;
    _recommendations = recommendations;
    _webSearch = webSearch;
  }

  public async Task<BenchmarkRecommendationCatalog> GetCatalogAsync(
    CancellationToken cancellationToken
  )
  {
    var profile = await _profiles.GetAsync(cancellationToken);
    bool externalAvailable;
    try
    {
      externalAvailable = await _webSearch.IsAvailableAsync(cancellationToken);
    }
    catch (IOException)
    {
      externalAvailable = false;
    }
    return new BenchmarkRecommendationCatalog(
      AlgorithmVersion,
      Categories,
      profile,
      externalAvailable
    );
  }

  public async Task<BenchmarkRecommendationResult> RecommendAsync(
    BenchmarkRecommendationRequest request,
    CancellationToken cancellationToken
  )
  {
    var category = Categories.FirstOrDefault(candidate => string.Equals(
      candidate.Id,
      request.Category,
      StringComparison.OrdinalIgnoreCase
    )) ?? throw new BenchmarkRequestException(
      "benchmark-recommendation-category-invalid",
      $"Recommendation category '{request.Category}' is unavailable.",
      "category"
    );
    var profile = await ResolveProfileAsync(
      request.ScoringProfile,
      cancellationToken
    );
    var persisted = await _results.ListAsync(100, cancellationToken);
    var observations = persisted.SelectMany(Observations).ToArray();
    var ranked = RankCandidates(observations, category.Id, profile);
    var missing = MissingEvidence(observations, persisted, category.Id);
    var external = Array.Empty<BenchmarkExternalRecommendationEvidence>();
    var externalStatus = request.IncludeExternalEvidence
      ? "unavailable"
      : "not-requested";
    if (request.IncludeExternalEvidence)
    {
      (external, externalStatus) = await ResearchExternalAsync(
        category,
        ranked,
        missing,
        cancellationToken
      );
    }
    var evidenceFingerprint = Fingerprint(
      category.Id,
      profile,
      observations,
      external,
      externalStatus
    );
    var existing = await _recommendations.GetAsync(
      evidenceFingerprint,
      cancellationToken
    );
    if (existing is not null)
    {
      return existing;
    }
    var generatedAt = observations.Length == 0
      ? DateTimeOffset.UnixEpoch
      : observations.Max(observation => observation.Result.StartedAt);
    var result = new BenchmarkRecommendationResult(
      evidenceFingerprint,
      AlgorithmVersion,
      generatedAt,
      category.Id,
      profile,
      evidenceFingerprint,
      ranked,
      missing,
      external,
      externalStatus,
      ranked.Count == 0
        ? "Insufficient local evidence for this category."
        : $"Ranked {ranked.Count} Model x Harness candidate(s) from local benchmark evidence."
    );
    await _recommendations.SaveAsync(result, cancellationToken);
    return result;
  }

  public Task<BenchmarkRecommendationResult?> GetAsync(
    string recommendationId,
    CancellationToken cancellationToken
  )
  {
    return _recommendations.GetAsync(recommendationId, cancellationToken);
  }

  private async Task<BenchmarkScoringProfile> ResolveProfileAsync(
    string requested,
    CancellationToken cancellationToken
  )
  {
    if (string.Equals(requested, "default", StringComparison.OrdinalIgnoreCase))
    {
      return BenchmarkScoringProfile.Default;
    }
    if (!string.Equals(requested, "active", StringComparison.OrdinalIgnoreCase))
    {
      throw new BenchmarkRequestException(
        "benchmark-recommendation-profile-invalid",
        "Recommendation scoring profile must be active or default.",
        "scoringProfile"
      );
    }
    return await _profiles.GetAsync(cancellationToken);
  }

  private IReadOnlyList<BenchmarkRecommendationCandidate> RankCandidates(
    IReadOnlyList<CandidateObservation> observations,
    string category,
    BenchmarkScoringProfile profile
  )
  {
    var candidates = new List<CandidateProjection>();
    foreach (var group in observations.GroupBy(
      observation => CandidateKey(observation.Model, observation.Harness),
      StringComparer.OrdinalIgnoreCase
    ))
    {
      var relevant = group.Select(observation => ScoreObservation(
        observation,
        category,
        profile
      )).Where(observation => observation is not null)
        .Select(observation => observation!)
        .ToArray();
      if (relevant.Length == 0)
      {
        continue;
      }
      var current = new List<ScoredObservation>();
      var comparable = new List<ScoredObservation>();
      var partial = new List<ScoredObservation>();
      var incompatible = new List<ScoredObservation>();
      var links = new List<BenchmarkRecommendationEvidenceLink>();
      var ordered = relevant.OrderByDescending(observation =>
        observation.Observation.Result.StartedAt)
        .ThenByDescending(
          observation => observation.Observation.Result.RunId,
          StringComparer.Ordinal
        )
        .ToArray();
      var latest = ordered[0];
      current.Add(latest);
      links.Add(EvidenceLink(
        latest,
        BenchmarkRecommendationEvidenceSourceIds.MeasuredLocally,
        BenchmarkComparabilityIds.Comparable
      ));
      foreach (var historical in ordered.Skip(1))
      {
        var assessment = _history.AssessComparability(
          latest.Observation.Result,
          historical.Observation.Result
        );
        if (string.Equals(
          assessment.Classification,
          BenchmarkComparabilityIds.Comparable,
          StringComparison.Ordinal
        ))
        {
          comparable.Add(historical);
        }
        else if (string.Equals(
          assessment.Classification,
          BenchmarkComparabilityIds.PartiallyComparable,
          StringComparison.Ordinal
        ))
        {
          partial.Add(historical);
        }
        else
        {
          incompatible.Add(historical);
        }
        links.Add(EvidenceLink(
          historical,
          BenchmarkRecommendationEvidenceSourceIds.HistoricalLocal,
          assessment.Classification
        ));
      }
      var currentScore = Average(current.Select(item => item.CategoryScore));
      var finalScore = comparable.Count > 0
        ? decimal.Round(
          currentScore * 0.7m + Average(comparable.Select(item => item.CategoryScore)) * 0.3m,
          2,
          MidpointRounding.AwayFromZero
        )
        : partial.Count > 0
          ? decimal.Round(
            currentScore * 0.9m + Average(partial.Select(item => item.CategoryScore)) * 0.1m,
            2,
            MidpointRounding.AwayFromZero
          )
          : currentScore;
      var allScored = current.Concat(comparable).Concat(partial).ToArray();
      var conflicting = allScored.Length > 1
        && allScored.Max(item => item.CategoryScore)
          - allScored.Min(item => item.CategoryScore) >= 20m;
      var confidence = Confidence(
        comparable.Count,
        conflicting
      );
      var metrics = AverageMetrics(current.Select(item => item.Metrics));
      var sources = new List<string>
      {
        BenchmarkRecommendationEvidenceSourceIds.MeasuredLocally
      };
      if (comparable.Count > 0 || partial.Count > 0 || incompatible.Count > 0)
      {
        sources.Add(BenchmarkRecommendationEvidenceSourceIds.HistoricalLocal);
      }
      var weaknesses = Weaknesses(metrics, confidence, conflicting, allScored);
      candidates.Add(new CandidateProjection(
        relevant[0].Observation.Model,
        relevant[0].Observation.Harness,
        finalScore,
        confidence,
        comparable.Count > 0 || partial.Count > 0 || incompatible.Count > 0
          ? "Measured locally + Historical local"
          : "Measured locally",
        Strengths(metrics, category),
        weaknesses,
        sources,
        links.OrderByDescending(link => link.StartedAt)
          .ThenBy(link => link.RunId, StringComparer.Ordinal)
          .ToArray(),
        current.Count,
        comparable.Count,
        partial.Count,
        incompatible.Count
      ));
    }
    return candidates.OrderByDescending(candidate => candidate.Score)
      .ThenByDescending(candidate => ConfidenceOrder(candidate.Confidence))
      .ThenByDescending(candidate => candidate.ComparableHistoricalRunCount)
      .ThenBy(candidate => candidate.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(candidate => candidate.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((candidate, index) => new BenchmarkRecommendationCandidate(
        index + 1,
        candidate.Model,
        candidate.Harness,
        index == 0 ? "Recommended" : "Alternative",
        candidate.Score,
        candidate.Confidence,
        candidate.EvidenceStrength,
        candidate.Strengths,
        candidate.Weaknesses,
        candidate.EvidenceSources,
        candidate.Evidence,
        candidate.CurrentRunCount,
        candidate.ComparableHistoricalRunCount,
        candidate.PartialHistoricalRunCount,
        candidate.IncompatibleHistoricalRunCount
      )).ToArray();
  }

  private IReadOnlyList<BenchmarkMissingRecommendationEvidence> MissingEvidence(
    IReadOnlyList<CandidateObservation> observations,
    IReadOnlyList<BenchmarkSuiteRunResult> results,
    string category
  )
  {
    var missing = new List<BenchmarkMissingRecommendationEvidence>();
    foreach (var combination in results.SelectMany(Combinations)
      .DistinctBy(item => CandidateKey(item.Model, item.Harness), StringComparer.OrdinalIgnoreCase))
    {
      var relevant = observations.Where(observation =>
        string.Equals(observation.Model, combination.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(observation.Harness, combination.Harness, StringComparison.OrdinalIgnoreCase)
        && RelevantTests(observation.HarnessResult.Tests, category).Count > 0)
        .ToArray();
      if (relevant.Length == 0)
      {
        missing.Add(new BenchmarkMissingRecommendationEvidence(
          combination.Model,
          combination.Harness,
          "No relevant local benchmark evidence for this category.",
          SuggestedSuite(category),
          1
        ));
      }
      else if (relevant.Length == 1)
      {
        missing.Add(new BenchmarkMissingRecommendationEvidence(
          combination.Model,
          combination.Harness,
          "Only one relevant local run; repeat it under comparable conditions.",
          relevant[0].Result.SuiteId,
          2
        ));
      }
      else
      {
        var latest = relevant.OrderByDescending(item => item.Result.StartedAt)
          .ThenByDescending(item => item.Result.RunId, StringComparer.Ordinal)
          .First();
        var comparableCount = relevant.Where(item =>
          !string.Equals(
            item.Result.RunId,
            latest.Result.RunId,
            StringComparison.OrdinalIgnoreCase
          )).Count(item =>
            _history.AssessComparability(latest.Result, item.Result).Classification
              == BenchmarkComparabilityIds.Comparable);
        if (comparableCount == 0)
        {
          missing.Add(new BenchmarkMissingRecommendationEvidence(
            combination.Model,
            combination.Harness,
            "No repeated directly comparable run; repeat the current conditions.",
            latest.Result.SuiteId,
            3
          ));
        }
      }
    }
    return missing.OrderBy(item => item.Priority)
      .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(item => item.Harness, StringComparer.OrdinalIgnoreCase)
      .Take(8)
      .ToArray();
  }

  private async Task<(
    BenchmarkExternalRecommendationEvidence[] Evidence,
    string Status
  )> ResearchExternalAsync(
    BenchmarkRecommendationCategory category,
    IReadOnlyList<BenchmarkRecommendationCandidate> candidates,
    IReadOnlyList<BenchmarkMissingRecommendationEvidence> missing,
    CancellationToken cancellationToken
  )
  {
    if (!await _webSearch.IsAvailableAsync(cancellationToken))
    {
      return ([], "unavailable");
    }
    var identities = candidates.Take(3)
      .Select(candidate => $"{candidate.Model} with {candidate.Harness}")
      .Concat(missing.Take(2).Select(item => $"{item.Model} with {item.Harness}"))
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    var query = "Official documentation, official repositories, and release notes for "
      + $"{category.Name} using {string.Join("; ", identities)}";
    try
    {
      var context = await _webSearch.SearchAsync(
        query,
        new ProviderCallContext(
          null,
          null,
          Guid.NewGuid().ToString("N"),
          null,
          UsageModelRoles.WebSearchSynthesis,
          "explicit-benchmark-recommendation-research"
        ),
        cancellationToken
      );
      return (
        context.Citations.Select(citation =>
          new BenchmarkExternalRecommendationEvidence(
            citation.Title,
            citation.Url,
            BenchmarkRecommendationEvidenceSourceIds.ExternalEvidence,
            "unverified-external"
          )).ToArray(),
        "completed"
      );
    }
    catch (CapabilityException)
    {
      return ([], "unavailable");
    }
  }

  private ScoredObservation? ScoreObservation(
    CandidateObservation observation,
    string category,
    BenchmarkScoringProfile profile
  )
  {
    var tests = RelevantTests(observation.HarnessResult.Tests, category);
    if (tests.Count == 0)
    {
      return null;
    }
    var scores = tests.Select(test => _scorer.Score(
      test.RawResult,
      profile.Weights
    )).ToArray();
    var metrics = new RecommendationMetrics(
      Average(scores.Select(score => score.Total)),
      Average(scores.Select(score => (decimal)score.Correctness)),
      Average(scores.Select(score => (decimal)score.Terminality)),
      Average(scores.Select(score => (decimal)score.WorkspaceAccuracy)),
      Average(scores.Select(score => (decimal)score.Efficiency)),
      AverageOptional(tests.Select(test =>
        test.RawResult.BehaviorMetrics?.ContinuityPreservation)),
      AverageOptional(tests.Select(test => test.RawResult.BehaviorMetrics?.Recovery)),
      AverageOptional(tests.Select(test => test.RawResult.BehaviorMetrics?.Convergence)),
      AverageOptional(tests.Select(test => test.RawResult.BehaviorMetrics?.Hygiene)),
      tests.Count(test => string.Equals(
        test.RawResult.Status,
        BenchmarkResultStatusIds.Pass,
        StringComparison.Ordinal
      )),
      tests.Count,
      tests.Sum(test => Math.Max(0, test.DurationMilliseconds))
    );
    return new ScoredObservation(
      observation,
      CategoryScore(metrics, category),
      metrics
    );
  }

  private static decimal CategoryScore(
    RecommendationMetrics metrics,
    string category
  )
  {
    if (string.Equals(
      category,
      BenchmarkRecommendationCategoryIds.GeneralCoding,
      StringComparison.Ordinal
    ))
    {
      return metrics.ActiveScore;
    }
    var focus = category switch
    {
      BenchmarkRecommendationCategoryIds.ExactFilesystem => Weighted(
        (metrics.Correctness, 0.55m),
        (metrics.WorkspaceAccuracy, 0.35m),
        (metrics.Hygiene, 0.10m)
      ),
      BenchmarkRecommendationCategoryIds.LongContinuity => Weighted(
        (metrics.Continuity, 0.50m),
        (metrics.Terminality, 0.30m),
        (metrics.Correctness, 0.20m)
      ),
      BenchmarkRecommendationCategoryIds.RecoveryHeavy => Weighted(
        (metrics.Recovery, 0.50m),
        (metrics.Terminality, 0.30m),
        (metrics.Correctness, 0.20m)
      ),
      BenchmarkRecommendationCategoryIds.CorrectnessFirst => metrics.Correctness,
      BenchmarkRecommendationCategoryIds.TerminalityFirst => metrics.Terminality,
      BenchmarkRecommendationCategoryIds.EfficiencyFirst => metrics.Efficiency,
      _ => metrics.ActiveScore
    };
    return decimal.Round(
      focus * 0.8m + metrics.ActiveScore * 0.2m,
      2,
      MidpointRounding.AwayFromZero
    );
  }

  private static IReadOnlyList<BenchmarkRunResult> RelevantTests(
    IReadOnlyList<BenchmarkRunResult> tests,
    string category
  )
  {
    return category switch
    {
      BenchmarkRecommendationCategoryIds.ExactFilesystem => tests.Where(test =>
        test.Run.TestId.StartsWith("FS-", StringComparison.Ordinal)
        || test.Run.TestId is BenchmarkIds.ScopeRetention001
          or BenchmarkIds.StaleConflict001).ToArray(),
      BenchmarkRecommendationCategoryIds.LongContinuity => tests.Where(test =>
        test.Run.TestId is BenchmarkIds.Continuity001
          or BenchmarkIds.ScopeRetention001
          or BenchmarkIds.StaleConflict001).ToArray(),
      BenchmarkRecommendationCategoryIds.RecoveryHeavy => tests.Where(test =>
        test.Run.TestId is BenchmarkIds.Recovery001
          or BenchmarkIds.StaleConflict001
          or BenchmarkIds.TruthfulReport001).ToArray(),
      _ => tests
    };
  }

  private static IEnumerable<CandidateObservation> Observations(
    BenchmarkSuiteRunResult result
  )
  {
    if (result.Cells is { Count: > 0 })
    {
      return result.Cells.Where(cell => cell.Result is not null)
        .Select(cell => new CandidateObservation(
          result,
          cell.Model,
          cell.Harness,
          cell.Result!
        ));
    }
    return result.HarnessResults.Select(harness => new CandidateObservation(
      result,
      result.Model,
      harness.Harness,
      harness
    ));
  }

  private static BenchmarkRecommendationEvidenceLink EvidenceLink(
    ScoredObservation observation,
    string source,
    string comparability
  )
  {
    var tests = observation.Metrics;
    return new BenchmarkRecommendationEvidenceLink(
      observation.Observation.Result.RunId,
      observation.Observation.Result.SuiteId,
      observation.Observation.Result.SuiteVersion,
      observation.Observation.Result.StartedAt,
      source,
      comparability,
      observation.CategoryScore,
      tests.Passed,
      tests.Total
    );
  }

  private static string Confidence(
    int comparable,
    bool conflicting
  )
  {
    if (conflicting)
    {
      return BenchmarkRecommendationConfidenceIds.Mixed;
    }
    if (comparable >= 2)
    {
      return BenchmarkRecommendationConfidenceIds.Strong;
    }
    if (comparable == 1)
    {
      return BenchmarkRecommendationConfidenceIds.Moderate;
    }
    return BenchmarkRecommendationConfidenceIds.Limited;
  }

  private static IReadOnlyList<string> Strengths(
    RecommendationMetrics metrics,
    string category
  )
  {
    var values = MetricValues(metrics)
      .Where(metric => metric.Value is >= 80m)
      .OrderByDescending(metric => metric.Value)
      .ThenBy(metric => metric.Name, StringComparer.Ordinal)
      .Take(3)
      .Select(metric => $"{metric.Name} {metric.Value:0.##} from local evidence.")
      .ToList();
    if (values.Count == 0)
    {
      values.Add($"Category score is derived from measured {category} evidence.");
    }
    return values;
  }

  private static IReadOnlyList<string> Weaknesses(
    RecommendationMetrics metrics,
    string confidence,
    bool conflicting,
    IReadOnlyList<ScoredObservation> observations
  )
  {
    var values = MetricValues(metrics)
      .Where(metric => metric.Value is < 70m)
      .OrderBy(metric => metric.Value)
      .ThenBy(metric => metric.Name, StringComparer.Ordinal)
      .Take(3)
      .Select(metric => $"{metric.Name} is {metric.Value:0.##} in local evidence.")
      .ToList();
    if (conflicting)
    {
      values.Add(
        $"Comparable results conflict by {observations.Max(item => item.CategoryScore) - observations.Min(item => item.CategoryScore):0.##} points."
      );
    }
    if (string.Equals(
      confidence,
      BenchmarkRecommendationConfidenceIds.Limited,
      StringComparison.Ordinal
    ))
    {
      values.Add("Evidence is limited; another comparable run would improve confidence.");
    }
    return values.Count == 0
      ? ["No material weakness was observed in the available local evidence."]
      : values;
  }

  private static IReadOnlyList<(string Name, decimal Value)> MetricValues(
    RecommendationMetrics metrics
  )
  {
    var values = new List<(string Name, decimal Value)>
    {
      ("Correctness", metrics.Correctness),
      ("Terminality", metrics.Terminality),
      ("Workspace accuracy", metrics.WorkspaceAccuracy),
      ("Efficiency", metrics.Efficiency)
    };
    AddMetric(values, "Continuity", metrics.Continuity);
    AddMetric(values, "Recovery", metrics.Recovery);
    AddMetric(values, "Convergence", metrics.Convergence);
    AddMetric(values, "Hygiene", metrics.Hygiene);
    return values;
  }

  private static void AddMetric(
    ICollection<(string Name, decimal Value)> values,
    string name,
    decimal? value
  )
  {
    if (value is not null)
    {
      values.Add((name, value.Value));
    }
  }

  private static RecommendationMetrics AverageMetrics(
    IEnumerable<RecommendationMetrics> values
  )
  {
    var items = values.ToArray();
    return new RecommendationMetrics(
      Average(items.Select(item => item.ActiveScore)),
      Average(items.Select(item => item.Correctness)),
      Average(items.Select(item => item.Terminality)),
      Average(items.Select(item => item.WorkspaceAccuracy)),
      Average(items.Select(item => item.Efficiency)),
      AverageOptional(items.Select(item => item.Continuity)),
      AverageOptional(items.Select(item => item.Recovery)),
      AverageOptional(items.Select(item => item.Convergence)),
      AverageOptional(items.Select(item => item.Hygiene)),
      items.Sum(item => item.Passed),
      items.Sum(item => item.Total),
      items.Sum(item => item.DurationMilliseconds)
    );
  }

  private static string Fingerprint(
    string category,
    BenchmarkScoringProfile profile,
    IReadOnlyList<CandidateObservation> observations,
    IReadOnlyList<BenchmarkExternalRecommendationEvidence> external,
    string externalStatus
  )
  {
    var canonical = new StringBuilder()
      .AppendLine(AlgorithmVersion)
      .AppendLine(category)
      .AppendLine(profile.Id)
      .AppendLine(profile.Version.ToString())
      .AppendLine(externalStatus)
      .AppendLine(JsonSerializer.Serialize(profile.Weights));
    foreach (var observation in observations.OrderBy(item => item.Result.RunId, StringComparer.Ordinal)
      .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(item => item.Harness, StringComparer.OrdinalIgnoreCase))
    {
      canonical.AppendLine($"{observation.Result.RunId}|{observation.Model}|{observation.Harness}");
    }
    foreach (var source in external.OrderBy(item => item.Url, StringComparer.Ordinal))
    {
      canonical.AppendLine($"external|{source.Title}|{source.Url}|{source.Status}");
    }
    return Convert.ToHexString(SHA256.HashData(
      Encoding.UTF8.GetBytes(canonical.ToString())
    )).ToLowerInvariant();
  }

  private static IEnumerable<(string Model, string Harness)> Combinations(
    BenchmarkSuiteRunResult result
  )
  {
    if (result.Cells is { Count: > 0 })
    {
      return result.Cells.Select(cell => (cell.Model, cell.Harness));
    }
    if (result.SelectedModels is { Count: > 0 }
      && result.SelectedHarnesses is { Count: > 0 })
    {
      return result.SelectedModels.SelectMany(model =>
        result.SelectedHarnesses.Select(harness => (model, harness)));
    }
    return result.HarnessResults.Select(harness => (result.Model, harness.Harness));
  }

  private static string SuggestedSuite(string category)
  {
    return category is BenchmarkRecommendationCategoryIds.LongContinuity
      or BenchmarkRecommendationCategoryIds.RecoveryHeavy
      ? BenchmarkSuiteIds.AgentBehavior
      : BenchmarkSuiteIds.BasicCrud;
  }

  private static string CandidateKey(string model, string harness)
  {
    return $"{model}\n{harness}";
  }

  private static decimal Weighted(params (decimal? Value, decimal Weight)[] values)
  {
    var available = values.Where(item => item.Value is not null && item.Weight > 0).ToArray();
    var totalWeight = available.Sum(item => item.Weight);
    return totalWeight == 0
      ? 0
      : decimal.Round(
        available.Sum(item => item.Value!.Value * item.Weight) / totalWeight,
        2,
        MidpointRounding.AwayFromZero
      );
  }

  private static decimal Average(IEnumerable<decimal> values)
  {
    var items = values.ToArray();
    return items.Length == 0
      ? 0
      : decimal.Round(items.Average(), 2, MidpointRounding.AwayFromZero);
  }

  private static decimal? AverageOptional(IEnumerable<int?> values)
  {
    return AverageOptional(values.Where(value => value.HasValue)
      .Select(value => (decimal?)value!.Value));
  }

  private static decimal? AverageOptional(IEnumerable<decimal?> values)
  {
    var items = values.Where(value => value.HasValue)
      .Select(value => value!.Value)
      .ToArray();
    return items.Length == 0 ? null : Average(items);
  }

  private static int ConfidenceOrder(string confidence)
  {
    return confidence switch
    {
      BenchmarkRecommendationConfidenceIds.Strong => 4,
      BenchmarkRecommendationConfidenceIds.Moderate => 3,
      BenchmarkRecommendationConfidenceIds.Mixed => 2,
      BenchmarkRecommendationConfidenceIds.Limited => 1,
      _ => 0
    };
  }

  private sealed record CandidateObservation(
    BenchmarkSuiteRunResult Result,
    string Model,
    string Harness,
    BenchmarkHarnessResult HarnessResult
  );

  private sealed record ScoredObservation(
    CandidateObservation Observation,
    decimal CategoryScore,
    RecommendationMetrics Metrics
  );

  private sealed record RecommendationMetrics(
    decimal ActiveScore,
    decimal Correctness,
    decimal Terminality,
    decimal WorkspaceAccuracy,
    decimal Efficiency,
    decimal? Continuity,
    decimal? Recovery,
    decimal? Convergence,
    decimal? Hygiene,
    int Passed,
    int Total,
    long DurationMilliseconds
  );

  private sealed record CandidateProjection(
    string Model,
    string Harness,
    decimal Score,
    string Confidence,
    string EvidenceStrength,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Weaknesses,
    IReadOnlyList<string> EvidenceSources,
    IReadOnlyList<BenchmarkRecommendationEvidenceLink> Evidence,
    int CurrentRunCount,
    int ComparableHistoricalRunCount,
    int PartialHistoricalRunCount,
    int IncompatibleHistoricalRunCount
  );
}
