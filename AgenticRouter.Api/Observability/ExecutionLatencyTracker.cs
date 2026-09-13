using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgenticRouter.Api.Observability;

public interface IExecutionLatencyTracker
{
  void Start(string requestId);

  void SetHarness(string? harnessId);

  void Mark(string point, long? durationMilliseconds = null);

  void MarkOnce(string point, long? durationMilliseconds = null);

  void AddSsePresentationTime(long elapsedMilliseconds);

  void AddSseFlushTime(long elapsedMilliseconds);

  void Complete();
}

public sealed class ExecutionLatencyTracker : IExecutionLatencyTracker
{
  private readonly ILogger<ExecutionLatencyTracker> _logger;
  private readonly ConcurrentDictionary<string, long> _points = new(
    StringComparer.Ordinal
  );
  private readonly object _sync = new();
  private string? _requestId;
  private string? _harnessId;
  private long _startedTimestamp;
  private long _ssePresentationTicks;
  private long _sseFlushTicks;
  private bool _completed;

  public ExecutionLatencyTracker(
    ILogger<ExecutionLatencyTracker> logger
  )
  {
    _logger = logger;
  }

  public void Start(string requestId)
  {
    lock (_sync)
    {
      _requestId = requestId;
      _harnessId = null;
      _startedTimestamp = Stopwatch.GetTimestamp();
      _completed = false;
      _ssePresentationTicks = 0;
      _sseFlushTicks = 0;
      _points.Clear();
    }
    MarkOnce("request-received");
  }

  public void SetHarness(string? harnessId)
  {
    if (string.IsNullOrWhiteSpace(harnessId))
    {
      return;
    }

    lock (_sync)
    {
      _harnessId = harnessId;
    }
  }

  public void Mark(string point, long? durationMilliseconds = null)
  {
    var elapsed = ElapsedMilliseconds();
    _points[point] = elapsed;
    LogPoint(point, elapsed, durationMilliseconds);
  }

  public void MarkOnce(string point, long? durationMilliseconds = null)
  {
    var elapsed = ElapsedMilliseconds();
    if (_points.TryAdd(point, elapsed))
    {
      LogPoint(point, elapsed, durationMilliseconds);
    }
  }

  public void AddSsePresentationTime(long elapsedTicks)
  {
    Interlocked.Add(
      ref _ssePresentationTicks,
      Math.Max(0, elapsedTicks)
    );
  }

  public void AddSseFlushTime(long elapsedTicks)
  {
    Interlocked.Add(
      ref _sseFlushTicks,
      Math.Max(0, elapsedTicks)
    );
  }

  public void Complete()
  {
    string? requestId;
    string? harnessId;
    lock (_sync)
    {
      if (_completed || _requestId is null)
      {
        return;
      }
      _completed = true;
      requestId = _requestId;
      harnessId = _harnessId;
    }

    _points.TryGetValue("harness-turn-start", out var harnessStart);
    _points.TryGetValue("first-real-action-execution-start", out var firstAction);
    _logger.LogInformation(
      "Execution latency summary request {RequestId} harness {HarnessId}: request_to_harness_start_ms={RequestToHarnessStartMilliseconds}; request_to_first_action_ms={RequestToFirstActionMilliseconds}; sse_presentation_ms={SsePresentationMilliseconds}; sse_flush_ms={SseFlushMilliseconds}.",
      requestId,
      harnessId ?? "unresolved",
      _points.ContainsKey("harness-turn-start") ? harnessStart : null,
      _points.ContainsKey("first-real-action-execution-start") ? firstAction : null,
      TimeSpan.FromTicks(Interlocked.Read(ref _ssePresentationTicks)).TotalMilliseconds,
      TimeSpan.FromTicks(Interlocked.Read(ref _sseFlushTicks)).TotalMilliseconds
    );
  }

  private long ElapsedMilliseconds()
  {
    var started = Volatile.Read(ref _startedTimestamp);
    return started == 0
      ? 0
      : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
  }

  private void LogPoint(
    string point,
    long elapsedMilliseconds,
    long? durationMilliseconds
  )
  {
    string? requestId;
    string? harnessId;
    lock (_sync)
    {
      requestId = _requestId;
      harnessId = _harnessId;
    }
    _logger.LogInformation(
      "Execution latency point {Point} request {RequestId} harness {HarnessId}: elapsed_ms={ElapsedMilliseconds}; duration_ms={DurationMilliseconds}.",
      point,
      requestId ?? "uninitialized",
      harnessId ?? "unresolved",
      elapsedMilliseconds,
      durationMilliseconds
    );
  }
}

public sealed class NullExecutionLatencyTracker : IExecutionLatencyTracker
{
  public static NullExecutionLatencyTracker Instance { get; } = new();

  private NullExecutionLatencyTracker()
  {
  }

  public void Start(string requestId) { }

  public void SetHarness(string? harnessId) { }

  public void Mark(string point, long? durationMilliseconds = null) { }

  public void MarkOnce(string point, long? durationMilliseconds = null) { }

  public void AddSsePresentationTime(long elapsedMilliseconds) { }

  public void AddSseFlushTime(long elapsedMilliseconds) { }

  public void Complete() { }
}
