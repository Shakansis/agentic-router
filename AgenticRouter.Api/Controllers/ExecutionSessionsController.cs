using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/execution-sessions")]
public sealed class ExecutionSessionsController : ControllerBase
{
  private readonly IExecutionSessionStore _sessions;
  private readonly IValidationProfileService _validationProfiles;
  private readonly IWorkspaceProfileService _workspaceProfiles;

  public ExecutionSessionsController(
    IExecutionSessionStore sessions,
    IValidationProfileService validationProfiles,
    IWorkspaceProfileService workspaceProfiles
  )
  {
    _sessions = sessions;
    _validationProfiles = validationProfiles;
    _workspaceProfiles = workspaceProfiles;
  }

  [HttpPost("{executionSessionId}/validate")]
  public async Task<ActionResult<ValidationRunView>> Validate(
    string executionSessionId,
    [FromBody] RunValidationRequest request,
    CancellationToken cancellationToken
  )
  {
    var session = _sessions.Get(
      executionSessionId
    );

    if (session is null)
    {
      return NotFound();
    }

    if (!string.Equals(
      session.BrowserSessionId,
      request.BrowserSessionId,
      StringComparison.Ordinal
    ))
    {
      return Conflict(
        new
        {
          code = "execution-session-mismatch",
          message = "This execution session belongs to a different browser session."
        }
      );
    }

    if (session.CreateReview().Files.Count == 0)
    {
      return Conflict(
        new
        {
          code = "validation-not-available",
          message = "Validate changes is available after the execution session changes a file."
        }
      );
    }

    if (
      string.Equals(
        session.ApprovalPolicy,
        "ask",
        StringComparison.Ordinal
      )
      && !request.Confirmed
    )
    {
      return Conflict(
        new
        {
          code = "validation-approval-required",
          message = "Running the saved validation profile requires explicit confirmation."
        }
      );
    }

    return Ok(
      await _validationProfiles.RunAsync(
        session,
        cancellationToken
      )
    );
  }

  [HttpGet("{executionSessionId}/review")]
  public ActionResult<ExecutionSessionReview> Review(
    string executionSessionId
  )
  {
    var review = _sessions.GetReview(
      executionSessionId
    );
    return review is null
      ? NotFound()
      : Ok(
        review
      );
  }

  [HttpPost("{executionSessionId}/undo")]
  public async Task<ActionResult<UndoExecutionResponse>> Undo(
    string executionSessionId,
    [FromBody] UndoExecutionRequest request,
    CancellationToken cancellationToken
  )
  {
    var review = _sessions.GetReview(
      executionSessionId
    );
    var activeWorkspace = await _workspaceProfiles.GetActiveDataAsync(
      cancellationToken
    );

    if (
      review is not null
      && (
        activeWorkspace is null
        || !string.Equals(
          Path.GetFullPath(activeWorkspace.Path),
          review.WorkspacePath,
          StringComparison.OrdinalIgnoreCase
        )
      )
    )
    {
      return Conflict(
        new UndoExecutionResponse(
          false,
          executionSessionId,
          "The trusted workspace changed after this execution session.",
          [],
          []
        )
      );
    }

    var response = await _sessions.UndoAsync(
      executionSessionId,
      request.BrowserSessionId,
      request.Confirmed,
      cancellationToken
    );
    return response.Succeeded
      ? Ok(
        response
      )
      : Conflict(
        response
      );
  }
}
