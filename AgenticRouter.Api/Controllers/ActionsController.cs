using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/actions")]
public sealed class ActionsController : ControllerBase
{
  private readonly IApprovalCoordinator _approvalCoordinator;

  public ActionsController(
    IApprovalCoordinator approvalCoordinator
  )
  {
    _approvalCoordinator = approvalCoordinator;
  }

  [HttpPost("{actionId}/decision")]
  public async Task<ActionResult<ApprovalDecisionResponse>> Decide(
    string actionId,
    [FromBody] ApprovalDecisionRequest request,
    CancellationToken cancellationToken
  )
  {
    var result = await _approvalCoordinator.TryDecideAsync(
      actionId,
      request.BrowserSessionId,
      request.ExecutionSessionId,
      request.Approved,
      request.EditedText,
      cancellationToken
    );
    var response = new ApprovalDecisionResponse(
      actionId,
      result.Accepted,
      request.Approved,
      result.Accepted
        ? null
        : result.Diagnostic,
      result.Action?.Summary,
      result.Action?.Preview
    );

    if (!result.Pending)
    {
      return NotFound(
        response
      );
    }

    return result.Accepted
      ? Ok(response)
      : BadRequest(response);
  }

  [HttpPost("{actionId}/revision")]
  public async Task<ActionResult<ApprovalRevisionResponse>> Revise(
    string actionId,
    [FromBody] ApprovalRevisionRequest request,
    CancellationToken cancellationToken
  )
  {
    var result = await _approvalCoordinator.TryReviseAsync(
      actionId,
      request.BrowserSessionId,
      request.ExecutionSessionId,
      request.EditedText,
      cancellationToken
    );
    var response = new ApprovalRevisionResponse(
      actionId,
      result.Accepted,
      result.Action?.Summary,
      result.Action?.Preview,
      result.Diagnostic
    );

    if (!result.Pending)
    {
      return NotFound(
        response
      );
    }

    return result.Accepted
      ? Ok(
        response
      )
      : BadRequest(
        response
      );
  }
}
