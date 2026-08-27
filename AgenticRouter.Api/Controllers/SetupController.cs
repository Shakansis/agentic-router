using AgenticRouter.Api.Setup;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/setup")]
public sealed class SetupController : ControllerBase
{
  private readonly ILocalSetupService _setup;

  public SetupController(ILocalSetupService setup)
  {
    _setup = setup;
  }

  [HttpGet("status")]
  public async Task<ActionResult<LocalSetupStatus>> GetStatus(
    CancellationToken cancellationToken
  )
  {
    return Ok(await _setup.GetStatusAsync(cancellationToken));
  }

  [HttpPost("install/{resourceId}")]
  public async Task<ActionResult<SetupActionResult>> Install(
    string resourceId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _setup.StartInstallerAsync(resourceId, cancellationToken));
    }
    catch (SetupException exception)
    {
      return SetupProblem(exception);
    }
  }

  [HttpPost("models/pull")]
  public async Task<ActionResult<SetupActionResult>> PullModel(
    [FromBody] PullSetupModelRequest request,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(request.Model))
    {
      return BadRequest(new ProblemDetails
      {
        Title = "A model is required",
        Detail = "Select one of the models recommended for the detected GPU.",
        Status = StatusCodes.Status400BadRequest
      });
    }

    try
    {
      return Ok(await _setup.StartModelPullAsync(
        request.Model.Trim(),
        cancellationToken
      ));
    }
    catch (SetupException exception)
    {
      return SetupProblem(exception);
    }
  }

  private ObjectResult SetupProblem(SetupException exception)
  {
    var status = exception.Code switch
    {
      "installer-not-found" => StatusCodes.Status404NotFound,
      "installer-start-failed" => StatusCodes.Status503ServiceUnavailable,
      _ => StatusCodes.Status409Conflict
    };
    var problem = new ProblemDetails
    {
      Title = "Setup action could not start",
      Detail = exception.Message,
      Status = status
    };
    problem.Extensions["code"] = exception.Code;
    problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
    return StatusCode(status, problem);
  }
}

public sealed record PullSetupModelRequest(string Model);
