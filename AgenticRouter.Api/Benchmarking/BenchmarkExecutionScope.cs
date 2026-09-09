using System.Collections.Concurrent;
using System.Net;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkExecutionScopeRegistry
{
  BenchmarkExecutionScopeLease Register(BenchmarkWorkspace workspace, string model);

  bool TryEnter(string token, out IDisposable? scope);
}

public sealed class BenchmarkExecutionScopeRegistry : IBenchmarkExecutionScopeRegistry
{
  public const string HeaderName = "X-AgenticRouter-Benchmark-Scope";
  private readonly ConcurrentDictionary<string, WorkspaceProfileData> _profiles = new(StringComparer.Ordinal);
  private readonly IWorkspaceExecutionContextAccessor _contexts;

  public BenchmarkExecutionScopeRegistry(IWorkspaceExecutionContextAccessor contexts)
  {
    _contexts = contexts;
  }

  public BenchmarkExecutionScopeLease Register(BenchmarkWorkspace workspace, string model)
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
    if (!_profiles.TryAdd(token, profile))
    {
      throw new InvalidOperationException("The benchmark execution scope could not be registered.");
    }
    return new BenchmarkExecutionScopeLease(token, () => _profiles.TryRemove(token, out _));
  }

  public bool TryEnter(string token, out IDisposable? scope)
  {
    if (_profiles.TryGetValue(token, out var profile))
    {
      scope = _contexts.Push(profile);
      return true;
    }
    scope = null;
    return false;
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
