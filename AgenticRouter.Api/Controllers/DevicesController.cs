using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Devices;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase
{
  private readonly IGpuDiscoveryService _gpuDiscoveryService;

  public DevicesController(
    IGpuDiscoveryService gpuDiscoveryService
  )
  {
    _gpuDiscoveryService = gpuDiscoveryService;
  }

  [HttpGet]
  public ActionResult<DevicesResponse> Get()
  {
    return Ok(
      _gpuDiscoveryService.Discover()
    );
  }
}
