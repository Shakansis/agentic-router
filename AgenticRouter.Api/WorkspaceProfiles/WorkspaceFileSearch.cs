using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.WorkspaceProfiles;

public sealed record WorkspaceFileReference(string Name, string Path);
public sealed record WorkspaceFileMatches(string WorkspaceId, IReadOnlyList<WorkspaceFileReference> Files, bool Partial);
public sealed class WorkspaceFileSearchException(string code, string message, int status = 400, Exception? cause = null)
  : Exception(message, cause)
{
  public string Code { get; } = code;
  public int Status { get; } = status;
}

public sealed class WorkspaceFileSearch(IWorkspaceProfileService profiles,
  IWorkspaceExecutionContextAccessor context, ITrustedWorkspaceService workspace)
{
  public async Task<WorkspaceFileMatches> SearchAsync(string workspaceId, string? query, CancellationToken token)
  {
    var active = await profiles.GetActiveDataAsync(token);
    if (active is null || active.Id != workspaceId)
      throw new WorkspaceFileSearchException("workspace-changed", "Select the active project and search again.", 409);
    query = (query ?? "").Replace('\\', '/');
    if (query.Length > 300 || Path.IsPathRooted(query) || query.Contains(':')
      || query.Split('/').Any(part => part is ".." or ".") || query.Any(char.IsControl))
      throw new WorkspaceFileSearchException("file-query-invalid", "Search by a file name or a relative project path.");
    using var scope = context.Push(active);
    var files = new List<WorkspaceFileReference>();
    var pending = new Queue<string>();
    var slash = query.LastIndexOf('/');
    pending.Enqueue(slash < 0 ? "." : query[..slash]);
    var inspected = 0;
    var partial = false;
    while (pending.TryDequeue(out var directory))
    {
      token.ThrowIfCancellationRequested();
      FileSystemInfo[] entries;
      try
      {
        var fullPath = await workspace.ResolvePathAsync(directory, token);
        entries = new DirectoryInfo(fullPath).EnumerateFileSystemInfos().Take(20001 - inspected)
          .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
      }
      catch (LocalActionException) { continue; }
      catch (DirectoryNotFoundException) { continue; }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
        throw new WorkspaceFileSearchException("file-search-unavailable", "Project files could not be listed. Try again.", 409, exception);
      }
      foreach (var entry in entries)
      {
        token.ThrowIfCancellationRequested();
        if (++inspected > 20000) { partial = true; break; }
        if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
        var relative = Path.GetRelativePath(active.Path, entry.FullName).Replace('\\', '/');
        if (entry is DirectoryInfo) { pending.Enqueue(relative); continue; }
        if (!relative.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
        try { await workspace.ResolvePathAsync(relative, token); }
        catch (LocalActionException) { continue; }
        files.Add(new(entry.Name, relative));
        if (files.Count == 50) { partial = true; break; }
      }
      if (partial) break;
    }
    return new(active.Id, files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(), partial);
  }
}
