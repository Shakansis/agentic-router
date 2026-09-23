using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Chat;

// Owns attachment/cancellation, not routing or execution. The original request
// scope stays alive until its producer finishes, even after its HTTP peer leaves.
public sealed class LiveChatRuns(IHostApplicationLifetime lifetime)
{
  private readonly ConcurrentDictionary<string, LiveChatRun> _runs = new(StringComparer.Ordinal);
  private readonly object _admission = new();

  public LiveChatRun? Find(string id) => _runs.GetValueOrDefault(id);
  public IReadOnlyList<LiveChatRunView> Active => _runs.Values.Where(run => !run.Completed).Select(run => run.View).ToArray();
  public LiveChatRun? FindConversation(string id) => _runs.Values
    .FirstOrDefault(run => !run.Completed && run.View.ConversationSessionId == id);

  public LiveChatRun? Create(ChatRequest request)
  {
    lock (_admission)
    {
      if (!Guid.TryParse(request.ChatRunId, out _)) return null;
      if (request.ConversationSessionId is { } conversation && FindConversation(conversation) is not null) return null;
      foreach (var old in _runs.Values.Where(run => run.Completed)
        .OrderByDescending(run => run.CreatedAt).Skip(15))
        if (_runs.TryRemove(old.Id, out var removed)) removed.Dispose();
      var run = new LiveChatRun(request, lifetime.ApplicationStopping);
      if (_runs.TryAdd(run.Id, run)) return run;
      run.Dispose();
      return null;
    }
  }
}

public sealed record LiveChatRunView(string Id, string? ConversationSessionId,
  string BrowserSessionId, string Model, string Harness, string InteractionMode,
  string Message, string? TurnId, bool Completed, bool HistoryAvailable = false,
  string? SupervisionRunId = null, string ApprovalPolicy = "auto", string ExecutionStrategy = "auto");

public sealed class LiveChatRun : IDisposable
{
  private readonly object _sync = new();
  private readonly List<ChatStreamEvent> _events = [];
  private readonly CancellationTokenSource _cancellation;
  private TaskCompletionSource _changed = NewSignal();
  private LiveChatRunView _view;
  private bool _stopRequested;
  private bool _terminal;
  public string Id => _view.Id;
  public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
  public CancellationToken Token => _cancellation.Token;
  public LiveChatRunView View { get { lock (_sync) return _view; } }
  public bool Completed => View.Completed;
  public bool StopRequested { get { lock (_sync) return _stopRequested; } }
  public bool HasTerminal { get { lock (_sync) return _terminal; } }

  public LiveChatRun(ChatRequest request, CancellationToken stopping)
  {
    _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    _view = new(request.ChatRunId!, request.ConversationSessionId,
      request.BrowserSessionId ?? "", request.Model, request.Harness,
      request.InteractionMode, request.Message, null, false,
      ApprovalPolicy: request.ApprovalPolicy, ExecutionStrategy: request.ExecutionStrategy);
  }

  public ChatStreamEvent Publish(ChatStreamEvent item)
  {
    lock (_sync)
    {
      item = item with { ChatRunId = Id, ChatRunSequence = _events.Count + 1L };
      _view = _view with
      {
        ConversationSessionId = item.ConversationSessionId ?? _view.ConversationSessionId,
        HistoryAvailable = _view.HistoryAvailable || item.Type is "session-created" or "session-persisted",
        SupervisionRunId = item.SupervisionProgress?.RunId ?? _view.SupervisionRunId,
        TurnId = item.RequestId,
        Model = item.SelectedModel ?? _view.Model
      };
      _events.Add(item);
      _terminal |= item.Type is "response.completed" or "error" or "request.cancelled";
      Signal();
      return item;
    }
  }

  public void Cancel()
  {
    lock (_sync)
    {
      if (_view.Completed) return;
      _stopRequested = true;
    }
    // Token callbacks can finish the producer and publish its terminal event.
    // Never invoke them while holding the event journal lock.
    _cancellation.Cancel();
  }
  public void Complete() { lock (_sync) { _view = _view with { Completed = true }; Signal(); } }
  public async Task WaitForCompletionAsync(CancellationToken cancellationToken)
  {
    while (true)
    {
      Task changed;
      lock (_sync)
      {
        if (_view.Completed) return;
        changed = _changed.Task;
      }
      await changed.WaitAsync(cancellationToken);
    }
  }
  public void Dispose() => _cancellation.Dispose();

  public async IAsyncEnumerable<ChatStreamEvent> ReadAsync(long after,
    [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    var cursor = Math.Max(0, after);
    while (true)
    {
      ChatStreamEvent[] batch;
      Task changed;
      bool done;
      lock (_sync)
      {
        batch = _events.Skip((int)Math.Min(cursor, _events.Count)).Take(256).ToArray();
        changed = _changed.Task;
        done = _view.Completed;
      }
      foreach (var item in batch)
      {
        cancellationToken.ThrowIfCancellationRequested();
        cursor = item.ChatRunSequence!.Value;
        yield return item;
      }
      if (batch.Length > 0) continue;
      if (done) yield break;
      await changed.WaitAsync(cancellationToken);
    }
  }

  private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
  private void Signal() { var previous = _changed; _changed = NewSignal(); previous.TrySetResult(); }
}
