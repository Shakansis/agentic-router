using AgenticRouter.Api.Execution;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.ProjectAwareness;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/git")]
public sealed class GitWorkspaceController : ControllerBase
{
  private readonly IGitRepositoryService _git;
  private readonly IWorkspaceProfileService _profiles;
  private readonly IExecutionSessionStore _executionSessions;
  private readonly IProjectAwarenessService _projectAwareness;

  public GitWorkspaceController(
    IGitRepositoryService git,
    IWorkspaceProfileService profiles,
    IExecutionSessionStore executionSessions,
    IProjectAwarenessService projectAwareness
  )
  {
    _git = git;
    _profiles = profiles;
    _executionSessions = executionSessions;
    _projectAwareness = projectAwareness;
  }

  [HttpGet]
  public async Task<IActionResult> Get(
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      "git-overview",
      async active => await _git.GetWorkspaceOverviewAsync(
        active.Id,
        active.Path,
        CurrentSessionPaths(
          active.Path
        ),
        cancellationToken
      )
    );
  }

  [HttpPost("diff")]
  public async Task<IActionResult> Diff(
    [FromBody] GitPanelDiffRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      "git-diff",
      async active =>
      {
        var overview = await _git.GetWorkspaceOverviewAsync(
          active.Id,
          active.Path,
          CurrentSessionPaths(
            active.Path
          ),
          cancellationToken
        );
        if (
          overview.State != "available"
          || overview.Repository is null
        )
        {
          throw new GitDeliveryException(
            "git-repository-unavailable",
            "git-diff",
            "An initialized Git repository is required to view diffs."
          );
        }

        return request.View switch
        {
          "current-session" => CurrentSessionDiff(
            active.Path,
            request.Paths
          ),
          "working-tree" => await RepositoryDiffAsync(
            active.Path,
            request.Paths.Count > 0
              ? request.Paths
              : overview.Repository.UnstagedPaths.Concat(
                overview.Repository.UntrackedPaths
              ).Distinct(
                StringComparer.OrdinalIgnoreCase
              ).ToArray(),
            false,
            cancellationToken
          ),
          "staged" => await RepositoryDiffAsync(
            active.Path,
            request.Paths.Count > 0
              ? request.Paths
              : overview.Repository.StagedPaths,
            true,
            cancellationToken
          ),
          "last-commit" => await _git.GetLastCommitDiffAsync(
            active.Path,
            cancellationToken
          ),
          _ => throw new GitDeliveryException(
            "git-diff-view-invalid",
            "git-diff",
            "The requested Git diff view is invalid."
          )
        };
      }
    );
  }

  [HttpPost("initialize")]
  public async Task<IActionResult> Initialize(
    [FromBody] GitInitializeRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      "git-initialize",
      async active =>
      {
        var overview = await _git.InitializeAsync(
          active.Id,
          active.Path,
          CurrentSessionPaths(
            active.Path
          ),
          request,
          cancellationToken
        );
        _ = await _projectAwareness.GetAsync(
          true,
          cancellationToken
        );
        return overview;
      }
    );
  }

  [HttpPost("identity/preview")]
  public async Task<IActionResult> PreviewIdentity(
    [FromBody] GitIdentityPreviewRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      "git-local-identity",
      async active => await _git.PreviewIdentityAsync(
        active.Id,
        active.Path,
        request,
        cancellationToken
      )
    );
  }

  [HttpPut("identity")]
  public async Task<IActionResult> SetIdentity(
    [FromBody] GitIdentityRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      "git-local-identity",
      async active => await _git.SetLocalIdentityAsync(
        active.Id,
        active.Path,
        CurrentSessionPaths(
          active.Path
        ),
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("remote/preview")]
  public async Task<IActionResult> PreviewRemote(
    [FromBody] GitRemotePreviewRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      "git-remote-configuration",
      async active => await _git.PreviewRemoteAsync(
        active.Id,
        active.Path,
        request,
        cancellationToken
      )
    );
  }

  [HttpPut("remote")]
  public async Task<IActionResult> SetRemote(
    [FromBody] GitRemoteRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      "git-remote-configuration",
      async active => await _git.SetRemoteAsync(
        active.Id,
        active.Path,
        CurrentSessionPaths(
          active.Path
        ),
        request,
        cancellationToken
      )
    );
  }

  private async Task<GitDiffView> RepositoryDiffAsync(
    string workspacePath,
    IReadOnlyList<string> paths,
    bool staged,
    CancellationToken cancellationToken
  )
  {
    return paths.Count == 0
      ? new GitDiffView(
        [],
        false
      )
      : await _git.GetDiffAsync(
        workspacePath,
        paths,
        staged,
        cancellationToken
      );
  }

  private GitDiffView CurrentSessionDiff(
    string workspacePath,
    IReadOnlyList<string> requestedPaths
  )
  {
    var review = _executionSessions.GetLatestReview(
      workspacePath
    );
    if (review is null)
    {
      return new GitDiffView(
        [],
        false
      );
    }

    var requested = requestedPaths.ToHashSet(
      StringComparer.OrdinalIgnoreCase
    );
    var files = review.Files.Where(
      file => !file.PreExistingChange
        && (
          requested.Count == 0
          || requested.Contains(
            file.RelativePath
          )
        )
    ).Select(
      file => new GitDiffFileView(
        file.RelativePath,
        false,
        file.UnifiedDiff?.Contains(
          "Binary files ",
          StringComparison.Ordinal
        ) == true,
        file.UnifiedDiff?.Contains(
          "[truncated",
          StringComparison.OrdinalIgnoreCase
        ) == true,
        file.UnifiedDiff
          ?? "[diff unavailable for this completed session]",
        file.Operation
      )
    ).ToArray();
    return new GitDiffView(
      files,
      files.Any(
        file => file.Truncated
      )
    );
  }

  private IReadOnlyList<string> CurrentSessionPaths(
    string workspacePath
  )
  {
    return _executionSessions.GetLatestReview(
      workspacePath
    )?.Files.Where(
      file => !file.PreExistingChange
    ).Select(
      file => file.RelativePath
    ).Distinct(
      StringComparer.OrdinalIgnoreCase
    ).Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray() ?? [];
  }

  private async Task<IActionResult> ExecuteAsync<T>(
    string operation,
    Func<WorkspaceProfileData, Task<T>> action
  )
  {
    var active = await _profiles.GetActiveDataAsync(
      HttpContext.RequestAborted
    );
    if (active is null)
    {
      return BadRequest(
        Error(
          "workspace-profile-unavailable",
          "git-workspace",
          "No active trusted workspace is available.",
          null,
          operation,
          false,
          null
        )
      );
    }

    try
    {
      return Ok(
        await action(
          active
        )
      );
    }
    catch (GitDeliveryException exception)
    {
      return BadRequest(
        Error(
          exception.Code,
          exception.Stage,
          exception.Message,
          active.Id,
          operation,
          exception.Retryable,
          exception.Diagnostic
        )
      );
    }
  }

  private object Error(
    string code,
    string stage,
    string message,
    string? workspaceId,
    string operation,
    bool retryable,
    string? diagnostic
  )
  {
    return new
    {
      code,
      stage,
      message,
      workspaceId,
      gitOperation = operation,
      traceId = HttpContext.TraceIdentifier,
      retryable,
      diagnostic
    };
  }
}
