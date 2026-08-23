using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkScorer
{
  BenchmarkScoreWeights Weights { get; }

  BenchmarkScore Score(BenchmarkRawResult rawResult);

  BenchmarkScore Score(
    BenchmarkRawResult rawResult,
    BenchmarkScoreWeights weights
  );

  BenchmarkScoringProjection Rescore(
    BenchmarkSuiteRunResult result,
    BenchmarkScoringProfile profile
  );

  void Validate(BenchmarkScoreWeights weights);
}

public sealed class BenchmarkScorer : IBenchmarkScorer
{
  public BenchmarkScoreWeights Weights => BenchmarkScoreWeights.Default;

  public BenchmarkScore Score(BenchmarkRawResult rawResult)
  {
    return Score(rawResult, Weights);
  }

  public BenchmarkScore Score(
    BenchmarkRawResult rawResult,
    BenchmarkScoreWeights weights
  )
  {
    Validate(weights);
    var objective = rawResult.ObjectiveAchieved ? 100 : 0;
    var metrics = rawResult.BehaviorMetrics;
    var correctness = AverageAvailable(
      Math.Clamp(rawResult.Exactness, 0, 100),
      metrics?.ContinuityPreservation,
      metrics?.Recovery,
      metrics?.TruthfulFinalReport
    );
    var terminality = metrics?.Terminality ?? (string.Equals(
        rawResult.ExecutionStatus,
        BenchmarkExecutionStatusIds.Completed,
        StringComparison.Ordinal
      ) ? 100 : 0);
    var workspace = AverageAvailable(
      Math.Clamp(rawResult.ContainmentAccuracy, 0, 100),
      metrics?.ScopeAccuracy,
      metrics?.Hygiene
    );
    var toolEfficiency = rawResult.ToolCallCount switch
    {
      null => 50,
      0 => 80,
      1 => 100,
      2 => 90,
      3 => 75,
      4 => 60,
      _ => Math.Max(20, 60 - ((rawResult.ToolCallCount.Value - 4) * 10))
    };
    var efficiency = AverageAvailable(toolEfficiency, metrics?.Convergence);
    var total = (
      (objective * weights.ObjectiveSuccess)
      + (correctness * weights.Correctness)
      + (terminality * weights.Terminality)
      + (workspace * weights.WorkspaceAccuracy)
      + (efficiency * weights.Efficiency)
    ) / (decimal)weights.Total;
    return new BenchmarkScore(
      decimal.Round(Math.Clamp(total, 0m, 100m), 2, MidpointRounding.AwayFromZero),
      objective,
      correctness,
      terminality,
      workspace,
      efficiency
    );
  }

  private static int AverageAvailable(int baseline, params int?[] additional)
  {
    var values = new List<int> { Math.Clamp(baseline, 0, 100) };
    values.AddRange(additional.Where(value => value.HasValue).Select(
      value => Math.Clamp(value!.Value, 0, 100)
    ));
    return (int)Math.Round(values.Average(), MidpointRounding.AwayFromZero);
  }

  public BenchmarkScoringProjection Rescore(
    BenchmarkSuiteRunResult result,
    BenchmarkScoringProfile profile
  )
  {
    Validate(profile.Weights);
    var projectedHarnesses = result.HarnessResults.Select(harness =>
    {
      var tests = harness.Tests.Select(test => new BenchmarkTestScoreProjection(
        test.Run.RunId,
        test.Run.TestId,
        Score(test.RawResult, profile.Weights)
      )).ToArray();
      var divisor = Math.Max(1, harness.Total);
      decimal Average(Func<BenchmarkScore, decimal> selector) => decimal.Round(
        tests.Sum(test => selector(test.Score)) / divisor,
        2,
        MidpointRounding.AwayFromZero
      );
      var total = Average(score => score.Total);
      return new BenchmarkHarnessScoreProjection(
        harness.Harness,
        total,
        new BenchmarkScoreBreakdown(
          Average(score => score.ObjectiveSuccess),
          Average(score => score.Correctness),
          Average(score => score.Terminality),
          Average(score => score.WorkspaceAccuracy),
          Average(score => score.Efficiency)
        ),
        tests
      );
    }).ToArray();
    var projectionByHarness = projectedHarnesses.ToDictionary(
      item => item.Harness,
      StringComparer.OrdinalIgnoreCase
    );
    var ranking = result.HarnessResults
      .OrderByDescending(harness => projectionByHarness[harness.Harness].Score)
      .ThenByDescending(harness => harness.Passed)
      .ThenBy(harness => Math.Max(0, harness.DurationMilliseconds))
      .ThenBy(harness => harness.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((harness, index) => new BenchmarkRankingEntry(
        index + 1,
        harness.Harness,
        harness.Passed,
        projectionByHarness[harness.Harness].Score,
        Math.Max(0, harness.DurationMilliseconds),
        harness.Terminality
      ))
      .ToArray();
    var matrixCells = result.Cells ?? [];
    var projectedCells = matrixCells.Where(cell => cell.Result is not null).Select(cell =>
    {
      var harness = cell.Result!;
      var tests = harness.Tests.Select(test => new BenchmarkTestScoreProjection(
        test.Run.RunId,
        test.Run.TestId,
        Score(test.RawResult, profile.Weights)
      )).ToArray();
      var divisor = Math.Max(1, harness.Total);
      decimal Average(Func<BenchmarkScore, decimal> selector) => decimal.Round(
        tests.Sum(test => selector(test.Score)) / divisor,
        2,
        MidpointRounding.AwayFromZero
      );
      return new BenchmarkMatrixCellScoreProjection(
        cell.Model,
        cell.Harness,
        Average(score => score.Total),
        new BenchmarkScoreBreakdown(
          Average(score => score.ObjectiveSuccess),
          Average(score => score.Correctness),
          Average(score => score.Terminality),
          Average(score => score.WorkspaceAccuracy),
          Average(score => score.Efficiency)
        ),
        tests
      );
    }).ToArray();
    var projectedByCell = projectedCells.ToDictionary(
      cell => $"{cell.Model}\u001f{cell.Harness}",
      StringComparer.OrdinalIgnoreCase
    );
    decimal CellScore(BenchmarkMatrixCellResult cell)
    {
      return projectedByCell.TryGetValue(
        $"{cell.Model}\u001f{cell.Harness}",
        out var projected
      ) ? projected.Score : 0m;
    }
    var pairRanking = matrixCells
      .OrderByDescending(CellScore)
      .ThenByDescending(cell => cell.Passed)
      .ThenBy(cell => cell.DurationMilliseconds)
      .ThenBy(cell => cell.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(cell => cell.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((cell, index) => new BenchmarkMatrixRankingEntry(
        index + 1,
        cell.Model,
        cell.Harness,
        cell.Passed,
        CellScore(cell),
        cell.DurationMilliseconds,
        cell.Terminality,
        cell.Status
      ))
      .ToArray();
    IReadOnlyList<BenchmarkAggregateRankingEntry> Aggregate(
      IReadOnlyList<string> identities,
      Func<BenchmarkMatrixCellResult, string> selector
    )
    {
      var summaries = identities.Select(id =>
      {
        var matching = matrixCells.Where(cell => string.Equals(
          selector(cell),
          id,
          StringComparison.OrdinalIgnoreCase
        )).ToArray();
        var divisor = Math.Max(1, matching.Length);
        return new
        {
          Id = id,
          Completed = matching.Count(cell => string.Equals(
            cell.Status,
            BenchmarkMatrixCellStatusIds.Completed,
            StringComparison.Ordinal
          )),
          Total = matching.Length,
          Passed = matching.Sum(cell => cell.Passed),
          Score = decimal.Round(
            matching.Sum(CellScore) / divisor,
            2,
            MidpointRounding.AwayFromZero
          ),
          Duration = matching.Sum(cell => Math.Max(0, cell.DurationMilliseconds)),
          Terminality = (int)Math.Round(
            matching.Sum(cell => cell.Terminality) / (decimal)divisor,
            MidpointRounding.AwayFromZero
          )
        };
      }).OrderByDescending(item => item.Score)
        .ThenByDescending(item => item.Passed)
        .ThenBy(item => item.Duration)
        .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();
      return summaries.Select((item, index) => new BenchmarkAggregateRankingEntry(
        index + 1,
        item.Id,
        item.Completed,
        item.Total,
        item.Passed,
        item.Score,
        item.Duration,
        item.Terminality
      )).ToArray();
    }
    var selectedModels = result.SelectedModels ?? [];
    var selectedHarnesses = result.SelectedHarnesses ?? [];
    return new BenchmarkScoringProjection(
      result.RunId,
      result.ScoreWeights ?? BenchmarkScoreWeights.Default,
      profile,
      projectedHarnesses,
      ranking,
      matrixCells.Count > 0 ? projectedCells : null,
      matrixCells.Count > 0 ? pairRanking : null,
      matrixCells.Count > 0 ? Aggregate(selectedModels, cell => cell.Model) : null,
      matrixCells.Count > 0 ? Aggregate(selectedHarnesses, cell => cell.Harness) : null
    );
  }

  public void Validate(BenchmarkScoreWeights weights)
  {
    ArgumentNullException.ThrowIfNull(weights);
    var values = new[]
    {
      weights.ObjectiveSuccess,
      weights.Correctness,
      weights.Terminality,
      weights.WorkspaceAccuracy,
      weights.Efficiency
    };
    if (values.Any(value => value is < 0 or > 100))
    {
      throw new BenchmarkRequestException(
        "benchmark-score-weight-invalid",
        "Each benchmark score weight must be between 0 and 100.",
        "weights"
      );
    }
    if (weights.Total <= 0)
    {
      throw new BenchmarkRequestException(
        "benchmark-score-weight-total-invalid",
        "At least one benchmark score weight must be greater than zero.",
        "weights"
      );
    }
  }
}

public interface IBenchmarkScoringProfileStore
{
  Task<BenchmarkScoringProfile> GetAsync(CancellationToken cancellationToken);

  Task<BenchmarkScoringProfile> SaveCustomAsync(
    BenchmarkScoreWeights weights,
    CancellationToken cancellationToken
  );

  Task<BenchmarkScoringProfile> ResetAsync(CancellationToken cancellationToken);
}

public sealed class JsonBenchmarkScoringProfileStore : IBenchmarkScoringProfileStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
  };
  private readonly string _path;
  private readonly IBenchmarkScorer _scorer;
  private readonly SemaphoreSlim _gate = new(1, 1);

  public JsonBenchmarkScoringProfileStore(
    string dataDirectory,
    IBenchmarkScorer scorer
  )
  {
    _path = Path.Combine(
      Path.GetFullPath(dataDirectory),
      "benchmark-scoring-profile.json"
    );
    _scorer = scorer;
  }

  public async Task<BenchmarkScoringProfile> GetAsync(
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (!File.Exists(_path))
      {
        return BenchmarkScoringProfile.Default;
      }
      try
      {
        var json = await File.ReadAllTextAsync(_path, cancellationToken);
        var profile = JsonSerializer.Deserialize<BenchmarkScoringProfile>(
          json,
          JsonOptions
        );
        if (
          profile is null
          || !string.Equals(
            profile.Id,
            BenchmarkScoringProfileIds.Custom,
            StringComparison.Ordinal
          )
          || profile.Version != BenchmarkScoringProfileIds.DefaultVersion
          || profile.Weights is null
        )
        {
          return BenchmarkScoringProfile.Default;
        }
        _scorer.Validate(profile.Weights);
        return profile;
      }
      catch (JsonException)
      {
        return BenchmarkScoringProfile.Default;
      }
      catch (BenchmarkRequestException)
      {
        return BenchmarkScoringProfile.Default;
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<BenchmarkScoringProfile> SaveCustomAsync(
    BenchmarkScoreWeights weights,
    CancellationToken cancellationToken
  )
  {
    _scorer.Validate(weights);
    var profile = new BenchmarkScoringProfile(
      BenchmarkScoringProfileIds.Custom,
      BenchmarkScoringProfileIds.DefaultVersion,
      "Custom",
      weights
    );
    await _gate.WaitAsync(cancellationToken);
    try
    {
      Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
      var temporary = _path + $".{Guid.NewGuid():N}.tmp";
      try
      {
        await File.WriteAllTextAsync(
          temporary,
          JsonSerializer.Serialize(profile, JsonOptions),
          new UTF8Encoding(false),
          cancellationToken
        );
        File.Move(temporary, _path, true);
      }
      finally
      {
        if (File.Exists(temporary))
        {
          File.Delete(temporary);
        }
      }
      return profile;
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<BenchmarkScoringProfile> ResetAsync(
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (File.Exists(_path))
      {
        File.Delete(_path);
      }
      return BenchmarkScoringProfile.Default;
    }
    finally
    {
      _gate.Release();
    }
  }
}

public interface IBenchmarkResultStore
{
  Task SaveAsync(
    BenchmarkSuiteRunResult result,
    CancellationToken cancellationToken
  );

  Task<BenchmarkSuiteRunResult?> GetAsync(
    string runId,
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<BenchmarkSuiteRunResult>> ListAsync(
    int limit,
    CancellationToken cancellationToken
  );
}

public sealed class JsonBenchmarkResultStore : IBenchmarkResultStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
  };
  private readonly string _directory;
  private readonly SemaphoreSlim _gate = new(1, 1);

  public JsonBenchmarkResultStore(string dataDirectory)
  {
    _directory = Path.Combine(Path.GetFullPath(dataDirectory), "benchmark-results");
  }

  public async Task SaveAsync(
    BenchmarkSuiteRunResult result,
    CancellationToken cancellationToken
  )
  {
    var path = Resolve(result.RunId);
    await _gate.WaitAsync(cancellationToken);
    try
    {
      Directory.CreateDirectory(_directory);
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
        File.Move(temporary, path, true);
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

  public async Task<BenchmarkSuiteRunResult?> GetAsync(
    string runId,
    CancellationToken cancellationToken
  )
  {
    var path = Resolve(runId);
    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (!File.Exists(path))
      {
        return null;
      }
      var json = await File.ReadAllTextAsync(path, cancellationToken);
      return JsonSerializer.Deserialize<BenchmarkSuiteRunResult>(json, JsonOptions);
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<IReadOnlyList<BenchmarkSuiteRunResult>> ListAsync(
    int limit,
    CancellationToken cancellationToken
  )
  {
    limit = Math.Clamp(limit, 1, 100);
    await _gate.WaitAsync(cancellationToken);
    try
    {
      if (!Directory.Exists(_directory))
      {
        return [];
      }
      var results = new List<BenchmarkSuiteRunResult>();
      foreach (var path in Directory.EnumerateFiles(_directory, "*.json")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .Take(limit))
      {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
          var json = await File.ReadAllTextAsync(path, cancellationToken);
          var result = JsonSerializer.Deserialize<BenchmarkSuiteRunResult>(
            json,
            JsonOptions
          );
          if (result is not null)
          {
            results.Add(result);
          }
        }
        catch (JsonException)
        {
        }
      }
      return results
        .OrderByDescending(result => result.StartedAt)
        .ToArray();
    }
    finally
    {
      _gate.Release();
    }
  }

  private string Resolve(string runId)
  {
    if (!Guid.TryParse(runId, out var parsed))
    {
      throw new BenchmarkRequestException(
        "benchmark-run-id-invalid",
        "Benchmark run id must be a UUID.",
        "runId"
      );
    }
    var normalized = parsed.ToString("N");
    return Path.Combine(_directory, normalized + ".json");
  }
}

public interface IBenchmarkRunCancellationRegistry
{
  BenchmarkRunCancellationLease Register(
    string runId,
    CancellationToken callerCancellationToken
  );

  bool Cancel(string runId);
}

public sealed class BenchmarkRunCancellationRegistry : IBenchmarkRunCancellationRegistry
{
  private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(
    StringComparer.OrdinalIgnoreCase
  );

  public BenchmarkRunCancellationLease Register(
    string runId,
    CancellationToken callerCancellationToken
  )
  {
    var source = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
    if (!_active.TryAdd(runId, source))
    {
      source.Dispose();
      throw new BenchmarkRequestException(
        "benchmark-run-active",
        $"Benchmark run '{runId}' is already active.",
        "clientRunId"
      );
    }
    return new BenchmarkRunCancellationLease(
      source.Token,
      () =>
      {
        _active.TryRemove(runId, out _);
        source.Dispose();
      }
    );
  }

  public bool Cancel(string runId)
  {
    if (!_active.TryGetValue(runId, out var source))
    {
      return false;
    }
    source.Cancel();
    return true;
  }
}

public sealed class BenchmarkRunCancellationLease : IDisposable
{
  private readonly Action _dispose;
  private int _disposed;

  public BenchmarkRunCancellationLease(
    CancellationToken token,
    Action dispose
  )
  {
    Token = token;
    _dispose = dispose;
  }

  public CancellationToken Token { get; }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) == 0)
    {
      _dispose();
    }
  }
}
