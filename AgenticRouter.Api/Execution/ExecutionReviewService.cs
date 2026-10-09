using System.Security.Cryptography;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Execution;

public sealed class ExecutionReviewService(
  IExecutionSessionStore sessions,
  IPersistentSessionService history,
  IWorkspaceProfileService workspaces,
  IGitRepositoryService git)
{
  public async Task<ExecutionSessionReview?> GetAsync(
    string executionSessionId,
    string? conversationSessionId,
    string? workspaceId,
    CancellationToken cancellationToken)
  {
    var review = sessions.GetReview(executionSessionId);
    var active = await workspaces.GetActiveDataAsync(cancellationToken);
    var selectedId = active?.Id;
    var selectedPath = active?.Path;
    if (!string.IsNullOrWhiteSpace(workspaceId))
    {
      var profiles = await workspaces.GetAllAsync(cancellationToken);
      var profile = profiles.Profiles.FirstOrDefault(item =>
        string.Equals(item.Id, workspaceId, StringComparison.Ordinal));
      if (profile is null)
      {
        return null;
      }
      selectedId = profile.Id;
      selectedPath = profile.Path;
    }
    var historical = review is null;
    if (review is null && selectedId is not null && !string.IsNullOrWhiteSpace(conversationSessionId))
    {
      var conversation = await history.OpenReadOnlyAsync(
        selectedId, conversationSessionId, cancellationToken);
      review = conversation.ExecutionReviews.FirstOrDefault(item =>
        string.Equals(item.Summary.Id, executionSessionId, StringComparison.Ordinal));
    }
    if (review is null)
    {
      return null;
    }
    if (selectedPath is null || (!historical && !string.Equals(
      Path.GetFullPath(selectedPath), review.WorkspacePath,
      StringComparison.OrdinalIgnoreCase)))
    {
      return review with
      {
        Historical = historical,
        Summary = review.Summary with
        {
          UndoAvailable = false,
          UndoDiagnostic = "The trusted workspace has changed."
        }
      };
    }

    GitRepositoryStatusView? gitStatus = null;
    if (review.Project?.Repository?.IsGitRepository == true)
    {
      try
      {
        gitStatus = await git.GetStatusAsync(selectedPath, true, cancellationToken);
      }
      catch (GitDeliveryException)
      {
        // Hashes still identify restored or changed content when Git is unavailable.
      }
    }
    var files = new List<ExecutionFileReview>(review.Files.Count);
    foreach (var file in review.Files)
    {
      try
      {
        files.Add(await ReconcileFileAsync(selectedPath, file, gitStatus, cancellationToken));
      }
      catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException)
      {
        files.Add(file with
        {
          UndoAvailable = false,
          UndoDiagnostic = "The current file state could not be read.",
          CurrentGitStatus = "The current file state could not be read."
        });
      }
    }
    var canUndo = !historical && active is not null
      && string.Equals(selectedId, active.Id, StringComparison.Ordinal)
      && review.Summary.UndoAvailable
      && files.All(file => file.UndoAvailable);
    return review with
    {
      Files = files,
      Historical = historical,
      Summary = review.Summary with
      {
        UndoAvailable = canUndo,
        UndoDiagnostic = canUndo ? review.Summary.UndoDiagnostic
          : historical ? "Undo is unavailable for a saved historical review."
          : files.FirstOrDefault(file => !file.UndoAvailable)?.UndoDiagnostic
            ?? review.Summary.UndoDiagnostic
      }
    };
  }

  private static async Task<ExecutionFileReview> ReconcileFileAsync(
    string workspacePath,
    ExecutionFileReview file,
    GitRepositoryStatusView? gitStatus,
    CancellationToken cancellationToken)
  {
    var root = Path.GetFullPath(workspacePath);
    var path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
    if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
      StringComparison.OrdinalIgnoreCase))
    {
      return file with
      {
        UndoAvailable = false,
        UndoDiagnostic = "The saved path is outside the trusted workspace."
      };
    }
    for (var parent = path; !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
      parent = Path.GetDirectoryName(parent) ?? root)
    {
      if ((File.Exists(parent) || Directory.Exists(parent))
        && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
      {
        return file with
        {
          UndoAvailable = false,
          UndoDiagnostic = "The saved path traverses a reparse point."
        };
      }
    }

    string? currentHash = null;
    if (File.Exists(path))
    {
      await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 65_536,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
      currentHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
        .ToLowerInvariant();
    }
    var gitPath = gitStatus?.RepositoryRoot is { } repositoryRoot
      ? Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')
      : file.RelativePath.Replace('\\', '/');
    var changed = gitStatus?.Paths.Any(item => string.Equals(item.Path,
      gitPath, StringComparison.OrdinalIgnoreCase)) == true;
    var gitKnown = gitStatus is { Truncated: false };
    var restored = file.ExistedBefore
      ? currentHash == file.OriginalHash
      : currentHash is null;
    var matchesFinal = file.Operation.StartsWith("deleted", StringComparison.Ordinal)
      ? currentHash is null
      : currentHash == file.FinalHash;
    var state = restored ? "Changes rolled back or restored to the original content."
      : !matchesFinal ? "File changed again after this execution."
      : gitKnown && !changed ? "No pending Git change; content may be committed or merged."
      : "Session content is present in the workspace.";
    var undoAvailable = file.UndoAvailable && !restored
      && matchesFinal && (!gitKnown || changed);
    return file with
    {
      CurrentGitStatus = state,
      UndoAvailable = undoAvailable,
      UndoDiagnostic = undoAvailable ? file.UndoDiagnostic : state
    };
  }
}
