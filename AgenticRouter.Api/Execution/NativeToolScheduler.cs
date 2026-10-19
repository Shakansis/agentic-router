using System.Diagnostics;
using AgenticRouter.Api.Configuration;

namespace AgenticRouter.Api.Execution;

// Only runtime I/O overlaps. Admission, approvals, journal and history stay on
// the Host loop. Side effects/control calls drain this FIFO before admission.
public sealed class NativeToolScheduler : IAsyncDisposable
{
  public const int ToolTimeoutSeconds = 60;
  private readonly CancellationTokenSource _cancellation;
  private readonly ExecutionSession? _session;
  private readonly Queue<NativeToolExecution> _running = new();
  private int _active;
  private int _peak;

  public NativeToolScheduler(CancellationToken cancellationToken, ExecutionSession? session)
  {
    _session = session;
    _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
  }

  public long? FileReadBudgetPerCall { get; set; }

  public int Count => _running.Count;
  public int ActiveCount => Volatile.Read(ref _active);
  public void ResetPeak() => _peak = 0;

  public int PeakConcurrency => Volatile.Read(ref _peak);
  public bool MustDrain(LocalActionPlanningResult? next, ExecutionSettings settings)
    => Count > 0 && (Count >= settings.MaxConcurrentToolCalls || !IsEligible(next, settings));

  public static bool IsEligible(LocalActionPlanningResult? call, ExecutionSettings settings)
    => settings.ParallelToolExecution != "off" && call?.CallFailure is null
      && call?.Proposal is { } proposal && IsEligibleTool(proposal.Tool);

  public static bool IsEligibleTool(string tool)
    => tool is "read_file" or "get_file_info" or "list_files" or "search_text";

  public void Start(ValidatedLocalAction action, string? callId, string? batchId,
    int position, System.Text.Json.JsonElement? repeatArguments, Func<CancellationToken, Task<LocalActionResult>> execute)
  {
    _running.Enqueue(new NativeToolExecution(action, callId, batchId, position, repeatArguments, RunAsync(execute)));
  }

  public NativeToolExecution Take() => _running.Dequeue();

  private async Task<NativeToolExecutionResult> RunAsync(Func<CancellationToken, Task<LocalActionResult>> execute)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(ToolTimeoutSeconds));
    var active = Interlocked.Increment(ref _active);
    int prior;
    do { prior = Volatile.Read(ref _peak); }
    while (active > prior && Interlocked.CompareExchange(ref _peak, active, prior) != prior);
    var timer = Stopwatch.StartNew();
    try
    {
      return new(await execute(timeout.Token), null, timer.ElapsedMilliseconds);
    }
    catch (OperationCanceledException failure) when (!_cancellation.IsCancellationRequested)
    {
      return new(null, new LocalActionException("tool-timeout", "The read-only tool exceeded its individual timeout.", failure), timer.ElapsedMilliseconds);
    }
    catch (LocalActionException failure)
    {
      return new(null, failure, timer.ElapsedMilliseconds);
    }
    catch (Exception failure) when (failure is not OperationCanceledException)
    {
      return new(null, new LocalActionException("tool-execution", "The read-only tool failed.", failure), timer.ElapsedMilliseconds);
    }
    finally { Interlocked.Decrement(ref _active); }
  }

  public async ValueTask DisposeAsync()
  {
    await _cancellation.CancelAsync();
    foreach (var execution in _running)
    {
      try { await execution.Completion; }
      catch (OperationCanceledException) { }
    }
    _session?.InterruptNativeToolCalls();
    _cancellation.Dispose();
  }
}

public sealed record NativeToolExecution(
  ValidatedLocalAction Action, string? CallId, string? BatchId, int Position,
  System.Text.Json.JsonElement? RepeatArguments, Task<NativeToolExecutionResult> Completion
);

public sealed record NativeToolExecutionResult(LocalActionResult? Result, LocalActionException? Failure, long DurationMilliseconds);
