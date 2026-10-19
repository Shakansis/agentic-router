namespace AgenticRouter.Api.Runtime;

public interface IModelRequestTracker
{
  bool HasActiveRequests { get; }

  Task<IDisposable> BeginRequestAsync(bool exclusive, CancellationToken cancellationToken);
}

public sealed class ModelRequestTracker : IModelRequestTracker
{
  private int _activeRequests;
  private readonly SemaphoreSlim _admission = new(1, 1);
  private readonly SemaphoreSlim _idle = new(1, 1);
  private readonly object _state = new();

  public bool HasActiveRequests => Volatile.Read(ref _activeRequests) > 0;

  public async Task<IDisposable> BeginRequestAsync(bool exclusive, CancellationToken cancellationToken)
  {
    await _admission.WaitAsync(cancellationToken);
    var retainAdmission = false;
    try
    {
      var joined = false;
      if (!exclusive)
      {
        lock (_state)
        {
          if (_activeRequests > 0)
          {
            _activeRequests++;
            joined = true;
          }
        }
      }
      if (!joined)
      {
        await _idle.WaitAsync(cancellationToken);
        lock (_state) _activeRequests++;
      }
      retainAdmission = exclusive;
      return new RequestLease(this, exclusive);
    }
    finally
    {
      if (!retainAdmission) _admission.Release();
    }
  }

  private void EndRequest(bool exclusive)
  {
    lock (_state)
    {
      if (--_activeRequests == 0) _idle.Release();
    }
    if (exclusive) _admission.Release();
  }

  private sealed class RequestLease : IDisposable
  {
    private ModelRequestTracker? _owner;

    private readonly bool _exclusive;

    public RequestLease(ModelRequestTracker owner, bool exclusive)
    {
      _owner = owner;
      _exclusive = exclusive;
    }

    public void Dispose()
    {
      Interlocked.Exchange(ref _owner, null)?.EndRequest(_exclusive);
    }
  }
}
