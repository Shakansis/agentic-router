using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkLiveRunCoordinator
{
  Task<BenchmarkLiveRunStart> StartAsync(
    BenchmarkSuiteRunRequest request,
    CancellationToken cancellationToken
  );

  bool Cancel(string runId);

  bool TryGetView(string runId, out BenchmarkLiveRunView view);

  IAsyncEnumerable<BenchmarkProgressEvent> SubscribeAsync(
    string runId,
    long afterSequence,
    CancellationToken cancellationToken
  );
}

public sealed class BenchmarkLiveRunCoordinator : IBenchmarkLiveRunCoordinator
{
  private const int MaximumRuns = 20;
  private const int MaximumEventsPerRun = 512;
  private readonly ConcurrentDictionary<string, LiveRunState> _runs = new(
    StringComparer.OrdinalIgnoreCase
  );
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IBenchmarkRunCancellationRegistry _cancellations;
  private readonly ILogger<BenchmarkLiveRunCoordinator> _logger;

  public BenchmarkLiveRunCoordinator(
    IServiceScopeFactory scopeFactory,
    IBenchmarkRunCancellationRegistry cancellations,
    ILogger<BenchmarkLiveRunCoordinator> logger
  )
  {
    _scopeFactory = scopeFactory;
    _cancellations = cancellations;
    _logger = logger;
  }

  public Task<BenchmarkLiveRunStart> StartAsync(
    BenchmarkSuiteRunRequest request,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    var runId = NormalizeRunId(request.ClientRunId);
    EvictCompletedRuns();
    if (_runs.Count >= MaximumRuns)
    {
      throw new BenchmarkRequestException(
        "benchmark-live-capacity",
        "Too many live benchmark runs are retained. Try again after an active run completes.",
        "clientRunId"
      );
    }
    var state = new LiveRunState(runId, MaximumEventsPerRun);
    if (!_runs.TryAdd(runId, state))
    {
      throw new BenchmarkRequestException(
        "benchmark-run-id-conflict",
        $"Benchmark run '{runId}' already exists in live state.",
        "clientRunId"
      );
    }
    var normalizedRequest = request with { ClientRunId = runId };
    state.Execution = ExecuteAsync(state, normalizedRequest);
    return Task.FromResult(new BenchmarkLiveRunStart(
      runId,
      $"/api/benchmarks/suite-runs/{runId}/events"
    ));
  }

  public bool Cancel(string runId)
  {
    if (!_runs.TryGetValue(runId, out var state) || state.Terminal)
    {
      return false;
    }
    if (state.RequestCancellation())
    {
      state.Publish(new BenchmarkProgressEvent(
        runId,
        BenchmarkProgressTypeIds.RunCancelling,
        DateTimeOffset.UtcNow,
        BenchmarkLiveStateIds.Cancelling,
        Message: "Cancellation requested; active harness work is stopping."
      ));
    }
    _cancellations.Cancel(runId);
    return true;
  }

  public bool TryGetView(string runId, out BenchmarkLiveRunView view)
  {
    if (!_runs.TryGetValue(runId, out var state))
    {
      view = null!;
      return false;
    }
    view = state.CreateView();
    return true;
  }

  public async IAsyncEnumerable<BenchmarkProgressEvent> SubscribeAsync(
    string runId,
    long afterSequence,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    if (!_runs.TryGetValue(runId, out var state))
    {
      yield break;
    }
    var cursor = Math.Max(0, afterSequence);
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var batch = state.ReadAfter(cursor);
      foreach (var progressEvent in batch.Events)
      {
        cursor = Math.Max(cursor, progressEvent.Sequence);
        yield return progressEvent;
      }
      if (batch.Terminal)
      {
        yield break;
      }
      await batch.Signal.WaitAsync(cancellationToken);
    }
  }

  private async Task ExecuteAsync(
    LiveRunState state,
    BenchmarkSuiteRunRequest request
  )
  {
    try
    {
      using var scope = _scopeFactory.CreateScope();
      var engine = scope.ServiceProvider.GetRequiredService<IBenchmarkEngine>();
      var sink = new LiveProgressSink(state, _cancellations);
      var result = await engine.RunSuiteAsync(request, sink, CancellationToken.None);
      state.PublishTerminal(new BenchmarkProgressEvent(
        state.RunId,
        BenchmarkProgressTypeIds.RunCompleted,
        DateTimeOffset.UtcNow,
        result.TerminalState,
        FinalResult: result
      ));
    }
    catch (BenchmarkRequestException exception)
    {
      PublishFailure(state, exception.Code, exception.Message, "benchmark-start");
    }
    catch (OperationCanceledException)
    {
      PublishFailure(
        state,
        "benchmark-cancelled",
        "The benchmark run was cancelled.",
        "benchmark-execution"
      );
    }
    catch (Exception exception)
    {
      _logger.LogError(exception, "Live benchmark run {RunId} failed.", state.RunId);
      PublishFailure(
        state,
        "benchmark-live-failed",
        "The live benchmark run ended because of an internal execution failure.",
        "benchmark-execution"
      );
    }
  }

  private static void PublishFailure(
    LiveRunState state,
    string code,
    string message,
    string stage
  )
  {
    state.PublishTerminal(new BenchmarkProgressEvent(
      state.RunId,
      BenchmarkProgressTypeIds.RunFailed,
      DateTimeOffset.UtcNow,
      state.CancellationRequested
        ? BenchmarkLiveStateIds.Cancelled
        : BenchmarkLiveStateIds.Failed,
      Message: message,
      Error: new BenchmarkError(code, message, stage, false)
    ));
  }

  private void EvictCompletedRuns()
  {
    if (_runs.Count < MaximumRuns)
    {
      return;
    }
    foreach (var candidate in _runs.Values
      .Where(run => run.Terminal)
      .OrderBy(run => run.CreatedAt)
      .Take(Math.Max(1, _runs.Count - MaximumRuns + 1)))
    {
      _runs.TryRemove(candidate.RunId, out _);
    }
  }

  private static string NormalizeRunId(string? clientRunId)
  {
    if (string.IsNullOrWhiteSpace(clientRunId))
    {
      return Guid.NewGuid().ToString("N");
    }
    if (!Guid.TryParse(clientRunId, out var parsed))
    {
      throw new BenchmarkRequestException(
        "benchmark-run-id-invalid",
        "Client run id must be a UUID.",
        "clientRunId"
      );
    }
    return parsed.ToString("N");
  }

  private sealed class LiveProgressSink : IBenchmarkProgressSink
  {
    private readonly LiveRunState _state;
    private readonly IBenchmarkRunCancellationRegistry _cancellations;

    public LiveProgressSink(
      LiveRunState state,
      IBenchmarkRunCancellationRegistry cancellations
    )
    {
      _state = state;
      _cancellations = cancellations;
    }

    public void Publish(BenchmarkProgressEvent progressEvent)
    {
      if (_state.CancellationRequested)
      {
        _cancellations.Cancel(_state.RunId);
      }
      if (string.Equals(
        progressEvent.Type,
        BenchmarkProgressTypeIds.RunCompleted,
        StringComparison.Ordinal
      ))
      {
        _state.PublishTerminal(progressEvent);
      }
      else
      {
        _state.Publish(progressEvent);
      }
    }
  }

  private sealed class LiveRunState
  {
    private readonly object _gate = new();
    private readonly int _maximumEvents;
    private readonly List<BenchmarkProgressEvent> _events = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _sequence;
    private bool _terminal;
    private bool _cancellationRequested;

    public LiveRunState(string runId, int maximumEvents)
    {
      RunId = runId;
      _maximumEvents = maximumEvents;
      CreatedAt = DateTimeOffset.UtcNow;
    }

    public string RunId { get; }

    public DateTimeOffset CreatedAt { get; }

    public Task? Execution { get; set; }

    public bool Terminal
    {
      get
      {
        lock (_gate)
        {
          return _terminal;
        }
      }
    }

    public bool CancellationRequested
    {
      get
      {
        lock (_gate)
        {
          return _cancellationRequested;
        }
      }
    }

    public bool RequestCancellation()
    {
      lock (_gate)
      {
        if (_cancellationRequested || _terminal)
        {
          return false;
        }
        _cancellationRequested = true;
        return true;
      }
    }

    public void Publish(BenchmarkProgressEvent progressEvent)
    {
      PublishCore(progressEvent, terminal: false);
    }

    public void PublishTerminal(BenchmarkProgressEvent progressEvent)
    {
      PublishCore(progressEvent, terminal: true);
    }

    public BenchmarkLiveRunView CreateView()
    {
      lock (_gate)
      {
        return new BenchmarkLiveRunView(
          RunId,
          _terminal,
          _cancellationRequested,
          _sequence,
          _events.ToArray()
        );
      }
    }

    public LiveReadBatch ReadAfter(long afterSequence)
    {
      lock (_gate)
      {
        return new LiveReadBatch(
          _events.Where(item => item.Sequence > afterSequence).ToArray(),
          _terminal,
          _changed.Task
        );
      }
    }

    private void PublishCore(BenchmarkProgressEvent progressEvent, bool terminal)
    {
      TaskCompletionSource signal;
      lock (_gate)
      {
        if (_terminal)
        {
          return;
        }
        progressEvent = progressEvent with { Sequence = ++_sequence };
        _events.Add(progressEvent);
        if (_events.Count > _maximumEvents)
        {
          _events.RemoveRange(0, _events.Count - _maximumEvents);
        }
        _terminal = terminal;
        signal = _changed;
        _changed = NewSignal();
      }
      signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal()
    {
      return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
  }

  private sealed record LiveReadBatch(
    IReadOnlyList<BenchmarkProgressEvent> Events,
    bool Terminal,
    Task Signal
  );
}
