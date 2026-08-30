namespace AgenticRouter.Api.Runtime;

public interface IModelRequestTracker
{
  bool HasActiveRequests { get; }

  IDisposable BeginRequest();
}

public sealed class ModelRequestTracker : IModelRequestTracker
{
  private int _activeRequests;

  public bool HasActiveRequests => Volatile.Read(ref _activeRequests) > 0;

  public IDisposable BeginRequest()
  {
    Interlocked.Increment(ref _activeRequests);
    return new RequestLease(this);
  }

  private void EndRequest()
  {
    Interlocked.Decrement(ref _activeRequests);
  }

  private sealed class RequestLease : IDisposable
  {
    private ModelRequestTracker? _owner;

    public RequestLease(ModelRequestTracker owner)
    {
      _owner = owner;
    }

    public void Dispose()
    {
      Interlocked.Exchange(ref _owner, null)?.EndRequest();
    }
  }
}
