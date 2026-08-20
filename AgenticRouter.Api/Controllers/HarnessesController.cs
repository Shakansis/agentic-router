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
}
