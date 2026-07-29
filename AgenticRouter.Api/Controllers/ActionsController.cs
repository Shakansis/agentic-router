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
  public ActionResult<ApprovalDecisionResponse> Decide(
    string actionId,
    [FromBody] ApprovalDecisionRequest request
  )
  {
    var accepted = _approvalCoordinator.TryDecide(
      actionId,
      request.BrowserSessionId,
      request.ExecutionSessionId,
      request.Approved
    );
    var response = new ApprovalDecisionResponse(
      actionId,
      accepted,
      request.Approved,
      accepted
        ? null
        : "The action is no longer pending or belongs to a different execution session."
    );

    return accepted
      ? Ok(
        response
      )
      : NotFound(
        response
      );
  }
}
