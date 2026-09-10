using System.Collections.Concurrent;
using System.Net;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkExecutionScopeRegistry
{
  BenchmarkExecutionScopeLease Register(
    BenchmarkWorkspace workspace,
    string model,
    int contextTokens,
    string gpu
  );

  bool TryEnter(string token, out IDisposable? scope);
}

public sealed class BenchmarkExecutionScopeRegistry : IBenchmarkExecutionScopeRegistry
{
  public const string HeaderName = "X-AgenticRouter-Benchmark-Scope";
  private readonly ConcurrentDictionary<string, BenchmarkExecutionScopeData> _profiles = new(StringComparer.Ordinal);
  private readonly IWorkspaceExecutionContextAccessor _contexts;
  private readonly IBenchmarkExecutionContextAccessor _benchmarkContexts;

  public BenchmarkExecutionScopeRegistry(
    IWorkspaceExecutionContextAccessor contexts,
    IBenchmarkExecutionContextAccessor benchmarkContexts
  )
  {
    _contexts = contexts;
    _benchmarkContexts = benchmarkContexts;
  }

  public BenchmarkExecutionScopeLease Register(
    BenchmarkWorkspace workspace,
    string model,
    int contextTokens,
    string gpu
  )
  {
    var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    var now = DateTimeOffset.UtcNow;
    var profile = new WorkspaceProfileData
    {
      Id = $"benchmark-{workspace.Id}",
      Name = "Benchmark workspace",
      Path = workspace.WorkspacePath,
      Active = true,
      HistoryEnabled = false,
      CreatedAt = now,
      LastOpenedAt = now,
      DefaultModel = model,
      ValidationProfile = null,
      ProcessPermissions = []
    };
    var data = new BenchmarkExecutionScopeData(
      profile,
      new BenchmarkExecutionContext(model, contextTokens, gpu)
    );
    if (!_profiles.TryAdd(token, data))
    {
      throw new InvalidOperationException("The benchmark execution scope could not be registered.");
    }
    return new BenchmarkExecutionScopeLease(token, () => _profiles.TryRemove(token, out _));
  }

  public bool TryEnter(string token, out IDisposable? scope)
  {
    if (_profiles.TryGetValue(token, out var data))
    {
      scope = new CompositeScope(
        _contexts.Push(data.Workspace),
        _benchmarkContexts.Push(data.Benchmark)
      );
      return true;
    }
    scope = null;
    return false;
  }

  private sealed record BenchmarkExecutionScopeData(
    WorkspaceProfileData Workspace,
    BenchmarkExecutionContext Benchmark
  );

  private sealed class CompositeScope(
    IDisposable workspace,
    IDisposable benchmark
  ) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) != 0)
      {
        return;
      }
      benchmark.Dispose();
      workspace.Dispose();
    }
  }
}

public sealed record BenchmarkExecutionContext(
  string Model,
  int ContextTokens,
  string Gpu
);

public interface IBenchmarkExecutionContextAccessor
{
  BenchmarkExecutionContext? Current { get; }

  IDisposable Push(BenchmarkExecutionContext context);
}

public sealed class BenchmarkExecutionContextAccessor : IBenchmarkExecutionContextAccessor
{
  private readonly AsyncLocal<Scope?> _current = new();

  public BenchmarkExecutionContext? Current => _current.Value?.Context;

  public IDisposable Push(BenchmarkExecutionContext context)
  {
    ArgumentNullException.ThrowIfNull(context);
    var previous = _current.Value;
    var current = new Scope(context);
    _current.Value = current;
    return new PopScope(this, current, previous);
  }

  private sealed record Scope(BenchmarkExecutionContext Context);

  private sealed class PopScope(
    BenchmarkExecutionContextAccessor owner,
    Scope current,
    Scope? previous
  ) : IDisposable
  {
    private int _disposed;

    public void Dispose()
    {
      if (Interlocked.Exchange(ref _disposed, 1) == 0
        && ReferenceEquals(owner._current.Value, current))
      {
        owner._current.Value = previous;
      }
    }
  }
}

public sealed class BenchmarkExecutionScopeMiddleware
{
  private readonly RequestDelegate _next;

  public BenchmarkExecutionScopeMiddleware(RequestDelegate next)
  {
    _next = next;
  }

  public async Task InvokeAsync(HttpContext context, IBenchmarkExecutionScopeRegistry scopes)
  {
    if (!context.Request.Headers.TryGetValue(BenchmarkExecutionScopeRegistry.HeaderName, out var values))
    {
      await _next(context);
      return;
    }
    var remote = context.Connection.RemoteIpAddress;
    if (remote is null || !IPAddress.IsLoopback(remote)
      || values.Count != 1 || !scopes.TryEnter(values[0]!, out var executionScope))
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return;
    }
    using (executionScope)
    {
      await _next(context);
    }
  }
}

public sealed class BenchmarkExecutionScopeLease : IDisposable
{
  private readonly Action _release;
  private int _disposed;

  public BenchmarkExecutionScopeLease(string token, Action release)
  {
    Token = token;
    _release = release;
  }

  public string Token { get; }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) == 0)
    {
      _release();
    }
  }
}
