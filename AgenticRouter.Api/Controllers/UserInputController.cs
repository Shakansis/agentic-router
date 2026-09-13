using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/user-input")]
public sealed class UserInputController : ControllerBase
{
  private readonly IUserInputCoordinator _userInput;

  public UserInputController(IUserInputCoordinator userInput)
  {
    _userInput = userInput;
  }

  [HttpGet("pending")]
  public ActionResult<IReadOnlyList<UserInputRequestView>> Pending(
    [FromQuery] string browserSessionId
  )
  {
    if (string.IsNullOrWhiteSpace(browserSessionId))
    {
      return BadRequest(new { code = "user-input-session", message = "browserSessionId is required." });
    }
    return Ok(_userInput.GetPending(browserSessionId));
  }

  [HttpPut("{userInputId}/draft")]
  public async Task<ActionResult<UserInputRequestView>> SaveDraft(
    string userInputId,
    [FromBody] UserInputDraftRequest request,
    CancellationToken cancellationToken
  )
  {
    var result = await _userInput.SaveDraftAsync(userInputId, request, cancellationToken);
    if (!result.Found) return NotFound(new { code = "user-input-stale", message = result.Diagnostic });
    if (!result.Accepted) return Conflict(new { code = "user-input-invalid", message = result.Diagnostic });
    return Ok(result.Request);
  }

  [HttpPost("{userInputId}/decision")]
  public async Task<ActionResult<UserInputDecisionResponse>> Decide(
    string userInputId,
    [FromBody] UserInputDecisionRequest request,
    CancellationToken cancellationToken
  )
  {
    var result = await _userInput.DecideAsync(userInputId, request, cancellationToken);
    var response = new UserInputDecisionResponse(
      userInputId,
      result.Accepted,
      result.Cancelled,
      result.Diagnostic
    );
    if (!result.Found) return NotFound(response);
    return result.Accepted ? Ok(response) : Conflict(response);
  }
}
