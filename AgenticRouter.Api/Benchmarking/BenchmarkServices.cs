using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkScorer
{
  BenchmarkScoreWeights Weights { get; }

  BenchmarkScore Score(BenchmarkRawResult rawResult);
}

public sealed class BenchmarkScorer : IBenchmarkScorer
{
  public BenchmarkScoreWeights Weights => BenchmarkScoreWeights.Default;

  public BenchmarkScore Score(BenchmarkRawResult rawResult)
  {
    if (Weights.Total != 100)
    {
      throw new InvalidOperationException("Benchmark score weights must total 100.");
    }
    var objective = rawResult.ObjectiveAchieved ? 100 : 0;
    var correctness = Math.Clamp(rawResult.Exactness, 0, 100);
    var terminality = string.Equals(
      rawResult.ExecutionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    ) ? 100 : 0;
    var workspace = Math.Clamp(rawResult.ContainmentAccuracy, 0, 100);
    var efficiency = rawResult.ToolCallCount switch
    {
      null => 50,
      0 => 80,
      1 => 100,
      2 => 90,
      3 => 75,
      4 => 60,
      _ => Math.Max(20, 60 - ((rawResult.ToolCallCount.Value - 4) * 10))
    };
    var total = (
      (objective * Weights.ObjectiveSuccess)
      + (correctness * Weights.Correctness)
      + (terminality * Weights.Terminality)
      + (workspace * Weights.WorkspaceAccuracy)
      + (efficiency * Weights.Efficiency)
    ) / 100m;
    return new BenchmarkScore(
      decimal.Round(total, 2, MidpointRounding.AwayFromZero),
      objective,
      correctness,
      terminality,
      workspace,
      efficiency
    );
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
