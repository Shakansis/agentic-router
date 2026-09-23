using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Observability;

namespace AgenticRouter.Api.Controllers;

internal sealed class SsePresentationWriter : IAsyncDisposable
{
  internal static readonly TimeSpan PresentationInterval = TimeSpan.FromMilliseconds(40);

  private readonly HttpResponse _response;
  private readonly JsonSerializerOptions _jsonOptions;
  private readonly IExecutionLatencyTracker _latency;
  private readonly CancellationToken _requestCancellationToken;
  private readonly object _sync = new();
  private readonly List<PendingPresentationEvent> _pending = [];
  private CancellationTokenSource? _timerCancellation;
  private Task? _timerTask;
  private Task _writeTail = Task.CompletedTask;
  private bool _disposed;

  public SsePresentationWriter(
    HttpResponse response,
    JsonSerializerOptions jsonOptions,
    IExecutionLatencyTracker latency,
    CancellationToken requestCancellationToken
  )
  {
    _response = response;
    _jsonOptions = jsonOptions;
    _latency = latency;
    _requestCancellationToken = requestCancellationToken;
  }

  public Task WriteAsync(
    ChatStreamEvent streamEvent,
    CancellationToken cancellationToken
  )
  {
    lock (_sync)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (IsPresentationDelta(streamEvent))
      {
        BufferPresentationLocked(streamEvent);
      }
      else
      {
        var events = TakePendingLocked();
        events.Add(streamEvent);
        ScheduleWriteLocked(events, cancellationToken);
      }
    }
    return Task.CompletedTask;
  }

  public async ValueTask DisposeAsync()
  {
    Task? timerTask;
    Task writeTail;
    lock (_sync)
    {
      if (_disposed)
      {
        return;
      }
      _disposed = true;
      _timerCancellation?.Cancel();
      timerTask = _timerTask;
      var pending = TakePendingLocked();
      if (pending.Count > 0)
      {
        ScheduleWriteLocked(pending, CancellationToken.None);
      }
      writeTail = _writeTail;
    }

    if (timerTask is not null)
    {
      try
      {
        await timerTask;
      }
      catch (OperationCanceledException)
      {
      }
    }
    await writeTail;
    lock (_sync)
    {
      _timerCancellation?.Dispose();
      _timerCancellation = null;
      _timerTask = null;
    }
  }

  private void BufferPresentationLocked(ChatStreamEvent streamEvent)
  {
    var last = _pending.LastOrDefault();
    if (last is not null && last.CanMerge(streamEvent))
    {
      last.Append(streamEvent);
    }
    else
    {
      _pending.Add(new PendingPresentationEvent(streamEvent));
    }
    if (_timerTask is null)
    {
      _timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
        _requestCancellationToken
      );
      _timerTask = FlushAfterIntervalAsync(_timerCancellation.Token);
    }
  }

  private async Task FlushAfterIntervalAsync(CancellationToken cancellationToken)
  {
    try
    {
      await Task.Delay(PresentationInterval, cancellationToken);
      lock (_sync)
      {
        if (_disposed)
        {
          return;
        }
        _timerCancellation?.Dispose();
        _timerCancellation = null;
        _timerTask = null;
        var pending = TakePendingLocked();
        if (pending.Count > 0)
        {
          ScheduleWriteLocked(pending, cancellationToken);
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
  }

  private List<ChatStreamEvent> TakePendingLocked()
  {
    _timerCancellation?.Cancel();
    _timerCancellation?.Dispose();
    _timerCancellation = null;
    _timerTask = null;
    if (_pending.Count == 0)
    {
      return [];
    }
    var result = _pending.Select(item => item.Build()).ToList();
    _pending.Clear();
    return result;
  }

  private void ScheduleWriteLocked(
    IReadOnlyList<ChatStreamEvent> events,
    CancellationToken cancellationToken
  )
  {
    _writeTail = WriteAfterAsync(_writeTail, events, cancellationToken);
  }

  private async Task WriteAfterAsync(
    Task previous,
    IReadOnlyList<ChatStreamEvent> events,
    CancellationToken cancellationToken
  )
  {
    await Task.Yield();
    await previous;
    var started = Stopwatch.GetTimestamp();
    var payload = Serialize(events);
    await _response.WriteAsync(payload, cancellationToken);
    _latency.AddSsePresentationTime(
      Stopwatch.GetElapsedTime(started).Ticks
    );
    var flushStarted = Stopwatch.GetTimestamp();
    await _response.Body.FlushAsync(cancellationToken);
    _latency.AddSseFlushTime(
      Stopwatch.GetElapsedTime(flushStarted).Ticks
    );
  }

  private string Serialize(IReadOnlyList<ChatStreamEvent> events)
  {
    var payload = new StringBuilder();
    foreach (var streamEvent in events)
    {
      payload.Append("data: ");
      payload.Append(JsonSerializer.Serialize(streamEvent, _jsonOptions));
      payload.Append("\n\n");
    }
    return payload.ToString();
  }

  private static bool IsPresentationDelta(ChatStreamEvent streamEvent)
  {
    return streamEvent.Type is "reasoning.delta" or "response.delta";
  }

  private sealed class PendingPresentationEvent
  {
    private readonly ChatStreamEvent _first;
    private readonly StringBuilder _content;
    private long? _sequence;

    public PendingPresentationEvent(ChatStreamEvent streamEvent)
    {
      _first = streamEvent;
      _sequence = streamEvent.ChatRunSequence;
      _content = new StringBuilder(
        streamEvent.Type == "reasoning.delta"
          ? streamEvent.ReasoningDelta
          : streamEvent.Delta
      );
    }

    public bool CanMerge(ChatStreamEvent streamEvent)
    {
      return string.Equals(_first.Type, streamEvent.Type, StringComparison.Ordinal)
        && string.Equals(
          _first.ContentBlockId,
          streamEvent.ContentBlockId,
          StringComparison.Ordinal
        );
    }

    public void Append(ChatStreamEvent streamEvent)
    {
      _sequence = streamEvent.ChatRunSequence;
      _content.Append(
        streamEvent.Type == "reasoning.delta"
          ? streamEvent.ReasoningDelta
          : streamEvent.Delta
      );
    }

    public ChatStreamEvent Build()
    {
      return _first.Type == "reasoning.delta"
        ? _first with { ReasoningDelta = _content.ToString(), ChatRunSequence = _sequence }
        : _first with { Delta = _content.ToString(), ChatRunSequence = _sequence };
    }
  }
}
