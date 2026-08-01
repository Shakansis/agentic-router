using System.Collections.Concurrent;

namespace AgenticRouter.Api.Observability;

public interface ITraceContext
{
  string TraceId { get; }

  long NextSequence();

  void Initialize(string traceId);

  void Link(string name, string? value);

  IReadOnlyDictionary<string, string> SnapshotLinks();
}

public sealed class TraceContext : ITraceContext
{
  private readonly ConcurrentDictionary<string, string> _links = new(StringComparer.Ordinal);
  private long _sequence;

  public string TraceId { get; private set; } = string.Empty;

  public void Initialize(string traceId)
  {
    if (!string.IsNullOrWhiteSpace(TraceId))
    {
      return;
    }

    TraceId = traceId;
  }

  public long NextSequence() => Interlocked.Increment(ref _sequence);

  public void Link(string name, string? value)
  {
    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
    {
      _links[name] = value;
    }
  }

  public IReadOnlyDictionary<string, string> SnapshotLinks()
  {
    return new Dictionary<string, string>(_links, StringComparer.Ordinal);
  }
}

public sealed class TraceContextMiddleware
{
  private readonly RequestDelegate _next;

  public TraceContextMiddleware(RequestDelegate next)
  {
    _next = next;
  }

  public async Task InvokeAsync(HttpContext context, ITraceContext trace)
  {
    trace.Initialize(context.TraceIdentifier);
    await _next(context);
  }
}
