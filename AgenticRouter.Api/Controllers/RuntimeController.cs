using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/runtime/status")]
public sealed class RuntimeController : ControllerBase
{
  private readonly IRuntimeStatusService _runtimeStatus;

  public RuntimeController(
    IRuntimeStatusService runtimeStatus
  )
  {
    _runtimeStatus = runtimeStatus;
  }

  [HttpGet]
  public async Task<ActionResult<RuntimeStatusResponse>> Get(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _runtimeStatus.GetAsync(
        cancellationToken
      )
    );
  }
}
