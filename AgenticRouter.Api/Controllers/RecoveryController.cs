using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/recovery")]
public sealed class RecoveryController : ControllerBase
{
  private readonly IRecoveryDecisionCoordinator _recoveryDecisions;

  public RecoveryController(
    IRecoveryDecisionCoordinator recoveryDecisions
  )
  {
    _recoveryDecisions = recoveryDecisions;
  }

  [HttpPost("{checkpointId}/decision")]
  public ActionResult<RecoveryDecisionResponse> Decide(
    string checkpointId,
    [FromBody] RecoveryDecisionRequest request
  )
  {
    var normalizedOption = request.Option.Trim().ToLowerInvariant();
    var accepted = _recoveryDecisions.TryDecide(
      checkpointId,
      request.BrowserSessionId,
      request.ExecutionSessionId,
      normalizedOption
    );
    var response = new RecoveryDecisionResponse(
      checkpointId,
      accepted,
      normalizedOption,
      accepted
        ? null
        : "The recovery checkpoint is no longer pending, the option is unavailable, or it belongs to a different execution session."
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
