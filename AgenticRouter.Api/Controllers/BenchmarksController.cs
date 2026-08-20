using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/benchmarks")]
public sealed class BenchmarksController : ControllerBase
{
  private readonly IBenchmarkEngine _engine;

  public BenchmarksController(
    IBenchmarkEngine engine
  )
  {
    _engine = engine;
  }

  [HttpPost("runs")]
  public async Task<IActionResult> Run(
    [FromBody] BenchmarkRunRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(
        await _engine.RunAsync(request, cancellationToken)
      );
    }
    catch (BenchmarkRequestException exception)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "The benchmark could not start.",
          new Dictionary<string, string[]>
          {
            [exception.Field] = [exception.Message]
          }
        )
      );
    }
  }
}
