using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ISettingsStore _settingsStore;

    public SettingsController(
      ISettingsStore settingsStore
    )
    {
        _settingsStore = settingsStore;
    }

    [HttpGet]
    public async Task<ActionResult<ApplicationSettings>> Get(
      CancellationToken cancellationToken
    )
    {
        var settings = await _settingsStore.GetAsync(
          cancellationToken
        );

        return Ok(
          settings
        );
    }

    [HttpPut]
    public async Task<IActionResult> Put(
      [FromBody] ApplicationSettings settings,
      CancellationToken cancellationToken
    )
    {
        var result = await _settingsStore.SaveAsync(
          settings,
          cancellationToken
        );

        if (!result.IsValid)
        {
            return BadRequest(
              new ValidationErrorsResponse(
                "Settings were not saved because validation failed.",
                result.Errors
              )
            );
        }

        return Ok(
          result.Settings
        );
    }
}
