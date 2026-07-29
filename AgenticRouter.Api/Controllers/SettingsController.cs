using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
  private readonly ISettingsStore _settingsStore;
  private readonly IResidentModelManager _residentModel;
  private readonly ISettingsValidator _validator;

  public SettingsController(
    ISettingsStore settingsStore,
    IResidentModelManager residentModel,
    ISettingsValidator validator
  )
  {
    _settingsStore = settingsStore;
    _residentModel = residentModel;
    _validator = validator;
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
    var errors = _validator.Validate(
      settings
    );

    if (errors.Count > 0)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "Settings were not saved because validation failed.",
          errors
        )
      );
    }

    var previous = await _settingsStore.GetAsync(
      cancellationToken
    );

    try
    {
      await _residentModel.ChangeRouterModelAsync(
        previous,
        settings,
        cancellationToken
      );
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "Settings were not saved because the resident router model could not be changed.",
          new Dictionary<string, string[]>
          {
            ["routerModel"] =
            [
              exception.Message
            ]
          }
        )
      );
    }

    var result = await _settingsStore.SaveAsync(
      settings,
      cancellationToken
    );

    return Ok(
      result.Settings
    );
  }
}
