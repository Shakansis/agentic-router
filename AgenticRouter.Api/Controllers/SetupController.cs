using AgenticRouter.Api.Setup;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/setup")]
public sealed class SetupController : ControllerBase
{
  private readonly ILocalSetupService _setup;
  private readonly IOllamaProfileSwitchService _profileSwitch;

  public SetupController(
    ILocalSetupService setup,
    IOllamaProfileSwitchService profileSwitch
  )
  {
    _setup = setup;
    _profileSwitch = profileSwitch;
  }

  [HttpPost("ollama/profile-switch/plan")]
  public async Task<ActionResult<OllamaProfileSwitchPlan>> PlanProfileSwitch(
    [FromBody] OllamaProfileSwitchPlanRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _profileSwitch.PrepareAsync(
        request.TargetProfile,
        cancellationToken
      ));
    }
    catch (SetupException exception)
    {
      return SetupProblem(exception);
    }
  }

  [HttpPost("ollama/profile-switch/apply")]
  public async Task<ActionResult<SetupActionResult>> ApplyProfileSwitch(
    [FromBody] OllamaProfileSwitchApplyRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _profileSwitch.StartAsync(
        request.PlanId,
        cancellationToken
      ));
    }
    catch (SetupException exception)
    {
      return SetupProblem(exception);
    }
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
    [FromQuery] string? profile,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _setup.StartInstallerAsync(
        resourceId,
        profile,
        cancellationToken
      ));
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

public sealed record OllamaProfileSwitchPlanRequest(string TargetProfile);

public sealed record OllamaProfileSwitchApplyRequest(string PlanId);
