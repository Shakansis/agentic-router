using AgenticRouter.Api.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/runtime/profiles")]
public sealed class OllamaRuntimeProfilesController : ControllerBase
{
  private readonly IOllamaRuntimeProfileService _profiles;

  public OllamaRuntimeProfilesController(
    IOllamaRuntimeProfileService profiles
  )
  {
    _profiles = profiles;
  }

  [HttpGet]
  public async Task<ActionResult<OllamaRuntimeProfilesView>> Get(
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(
        await _profiles.GetAsync(
          cancellationToken
        )
      );
    }
    catch (OllamaRuntimeProfileException exception)
    {
      return ProfileError(
        exception
      );
    }
  }

  [HttpPost("analyze")]
  public async Task<ActionResult<OllamaRuntimeAnalysisResult>> Analyze(
    [FromBody] OllamaRuntimeAnalysisRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(
        await _profiles.AnalyzeAsync(
          request,
          cancellationToken
        )
      );
    }
    catch (OllamaRuntimeProfileException exception)
    {
      return ProfileError(
        exception
      );
    }
  }

  [HttpPost("measure")]
  public async Task<ActionResult<OllamaRuntimeMeasurementResult>> Measure(
    [FromBody] OllamaRuntimeMeasurementRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(
        await _profiles.MeasureAsync(
          request,
          cancellationToken
        )
      );
    }
    catch (OllamaRuntimeProfileException exception)
    {
      return ProfileError(
        exception
      );
    }
  }

  private ObjectResult ProfileError(
    OllamaRuntimeProfileException exception
  )
  {
    var status = exception.Error.Code switch
    {
      "reload-blocked-by-active-request" => StatusCodes.Status409Conflict,
      "measurement-permission-required" => StatusCodes.Status428PreconditionRequired,
      "model-metadata-unavailable" => StatusCodes.Status503ServiceUnavailable,
      _ => StatusCodes.Status400BadRequest
    };

    return StatusCode(
      status,
      exception.Error
    );
  }
}
