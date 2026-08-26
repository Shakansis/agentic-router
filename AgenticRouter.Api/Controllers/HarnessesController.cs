using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/harnesses")]
public sealed class HarnessesController : ControllerBase
{
  private readonly IHarnessRegistry _harnesses;

  public HarnessesController(IHarnessRegistry harnesses)
  {
    _harnesses = harnesses;
  }

  [HttpGet]
  public async Task<ActionResult<IReadOnlyList<HarnessStatus>>> Get(
    CancellationToken cancellationToken
  )
  {
    return Ok(await _harnesses.DiscoverAsync(cancellationToken));
  }

  [HttpPost("{harnessId}/steer")]
  public async Task<ActionResult<HarnessSteerResult>> Steer(
    string harnessId,
    [FromBody] HarnessSteerInput input,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(input.SessionId)
      || string.IsNullOrWhiteSpace(input.Message)
      || string.IsNullOrWhiteSpace(input.MessageId)
    )
    {
      return BadRequest(new ProblemDetails
      {
        Title = "Invalid steering message",
        Detail = "SessionId, message, and messageId are required.",
        Status = StatusCodes.Status400BadRequest
      });
    }
    if (input.Message.Length > 16_384 || input.MessageId.Length > 128)
    {
      return BadRequest(new ProblemDetails
      {
        Title = "Invalid steering message",
        Detail = "Steering messages are limited to 16,384 characters and identifiers to 128 characters.",
        Status = StatusCodes.Status400BadRequest
      });
    }
    if (
      !_harnesses.TryGetDefinition(harnessId, out var definition)
      || !_harnesses.TryGetAdapter(harnessId, out var adapter)
    )
    {
      return NotFound(new ProblemDetails
      {
        Title = "Harness not found",
        Detail = $"Harness '{harnessId}' is not registered.",
        Status = StatusCodes.Status404NotFound
      });
    }
    if (
      !definition.Capabilities.SupportsSteering
      || adapter is not IAgentHarnessSteeringTransport steering
    )
    {
      return Conflict(new ProblemDetails
      {
        Title = "Steering is unavailable",
        Detail = $"{definition.DisplayName} does not support same-turn steering.",
        Status = StatusCodes.Status409Conflict
      });
    }

    try
    {
      return Ok(await steering.SteerTurnAsync(
        new HarnessSteerRequest(
          input.SessionId,
          input.Message.Trim(),
          input.MessageId
        ),
        cancellationToken
      ));
    }
    catch (HarnessException exception)
    {
      var problem = new ProblemDetails
      {
        Title = "Steering was not accepted",
        Detail = exception.Message,
        Status = StatusCodes.Status409Conflict
      };
      problem.Extensions["code"] = exception.Code;
      problem.Extensions["harnessId"] = exception.HarnessId;
      return Conflict(problem);
    }
  }
}
