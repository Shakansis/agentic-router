using AgenticRouter.Api.Execution;
using AgenticRouter.Api.GitDelivery;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/execution-sessions/{executionSessionId}/delivery")]
public sealed class GitDeliveryController : ControllerBase
{
  private readonly IGitDeliveryService _delivery;
  private readonly IGitRepositoryService _git;
  private readonly IExecutionSessionStore _sessions;

  public GitDeliveryController(
    IGitDeliveryService delivery,
    IGitRepositoryService git,
    IExecutionSessionStore sessions
  )
  {
    _delivery = delivery;
    _git = git;
    _sessions = sessions;
  }

  [HttpGet]
  public Task<ActionResult<GitDeliveryStateView>> Get(
    string executionSessionId,
    [FromQuery] bool includeIgnored,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.GetAsync(
        session,
        includeIgnored,
        cancellationToken
      )
    );
  }

  [HttpPost("selection")]
  public Task<ActionResult<GitDeliveryStateView>> Update(
    string executionSessionId,
    [FromBody] UpdateDeliveryRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.UpdateAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("diff")]
  public Task<ActionResult<GitDiffView>> Diff(
    string executionSessionId,
    [FromBody] GitDiffRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.GetDiffAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  [HttpGet("log")]
  public Task<ActionResult<IReadOnlyList<GitLogEntryView>>> Log(
    string executionSessionId,
    [FromQuery] int maximumEntries,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _git.GetLogAsync(
        session.WorkspacePath,
        maximumEntries <= 0
          ? 20
          : maximumEntries,
        cancellationToken
      )
    );
  }

  [HttpGet("commits/{commit}")]
  public Task<ActionResult<GitCommitView>> ShowCommit(
    string executionSessionId,
    string commit,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _git.ShowCommitAsync(
        session.WorkspacePath,
        commit,
        cancellationToken
      )
    );
  }

  [HttpPost("stage")]
  public Task<ActionResult<GitDeliveryStateView>> Stage(
    string executionSessionId,
    [FromBody] GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.StageAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("unstage")]
  public Task<ActionResult<GitDeliveryStateView>> Unstage(
    string executionSessionId,
    [FromBody] GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.UnstageAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("commit")]
  public Task<ActionResult<GitDeliveryStateView>> Commit(
    string executionSessionId,
    [FromBody] GitCommitRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.CommitAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("tag")]
  public Task<ActionResult<GitDeliveryStateView>> Tag(
    string executionSessionId,
    [FromBody] GitTagRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.TagAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("push-branch")]
  public Task<ActionResult<GitDeliveryStateView>> PushBranch(
    string executionSessionId,
    [FromBody] GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.PushBranchAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("push-tag")]
  public Task<ActionResult<GitDeliveryStateView>> PushTag(
    string executionSessionId,
    [FromBody] GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      executionSessionId,
      session => _delivery.PushTagAsync(
        session,
        request,
        cancellationToken
      )
    );
  }

  private async Task<ActionResult<T>> ExecuteAsync<T>(
    string executionSessionId,
    Func<ExecutionSession, Task<T>> action
  )
  {
    var session = _sessions.Get(
      executionSessionId
    );
    if (session is null)
    {
      return NotFound();
    }

    try
    {
      return Ok(
        await action(
          session
        )
      );
    }
    catch (GitDeliveryException exception)
    {
      var status = exception.Code is
        "git-authentication-required"
        or "git-network-unavailable"
        or "git-push-rejected"
        ? StatusCodes.Status502BadGateway
        : StatusCodes.Status409Conflict;
      return StatusCode(
        status,
        new GitErrorView(
          exception.Code,
          exception.Stage,
          exception.Message,
          session.CreateReview().Delivery?.Repository.Branch,
          session.CreateReview().Delivery?.Repository.Ahead,
          session.CreateReview().Delivery?.Repository.Behind,
          null,
          session.Id,
          HttpContext.TraceIdentifier,
          exception.Retryable,
          exception.Diagnostic
        )
      );
    }
  }
}
