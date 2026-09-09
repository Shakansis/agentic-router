namespace AgenticRouter.Api.WorkspaceProfiles;

public interface IWorkspaceExecutionContextAccessor
{
  WorkspaceProfileData? Current { get; }

  IDisposable Push(WorkspaceProfileData profile);

  bool TryUpdate(string workspaceId, Func<WorkspaceProfileData, WorkspaceProfileData> update);
}

public sealed class WorkspaceExecutionContextAccessor : IWorkspaceExecutionContextAccessor
{
  private readonly AsyncLocal<Scope?> _current = new();

  public WorkspaceProfileData? Current => _current.Value?.Profile;

  public IDisposable Push(WorkspaceProfileData profile)
  {
    ArgumentNullException.ThrowIfNull(profile);
    var previous = _current.Value;
    var current = new Scope(profile);
    _current.Value = current;
    return new PopScope(this, current, previous);
  }

  public bool TryUpdate(
    string workspaceId,
    Func<WorkspaceProfileData, WorkspaceProfileData> update
  )
  {
    var current = _current.Value;
    if (current is null || !string.Equals(current.Profile.Id, workspaceId, StringComparison.Ordinal))
    {
      return false;
    }
    current.Profile = update(current.Profile);
    return true;
  }

  private sealed class Scope(WorkspaceProfileData profile)
  {
    public WorkspaceProfileData Profile { get; set; } = profile;
  }

  private sealed class PopScope(
    WorkspaceExecutionContextAccessor owner,
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
