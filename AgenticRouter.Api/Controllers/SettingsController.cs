using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
  private readonly ISettingsStore _settingsStore;
  private readonly IPortableYamlSettingsService _portableYaml;
  private readonly IResidentModelManager _residentModel;
  private readonly ISettingsValidator _validator;
  private readonly IWorkspaceProfileService _workspaceProfiles;
  private readonly ITrustedWorkspaceService _trustedWorkspace;

  public SettingsController(
    ISettingsStore settingsStore,
    IPortableYamlSettingsService portableYaml,
    IResidentModelManager residentModel,
    ISettingsValidator validator,
    IWorkspaceProfileService workspaceProfiles,
    ITrustedWorkspaceService trustedWorkspace
  )
  {
    _settingsStore = settingsStore;
    _portableYaml = portableYaml;
    _residentModel = residentModel;
    _validator = validator;
    _workspaceProfiles = workspaceProfiles;
    _trustedWorkspace = trustedWorkspace;
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

  [HttpGet("yaml")]
  public async Task<IActionResult> GetYaml(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );

    return Content(
      _portableYaml.Export(
        settings
      ),
      "application/yaml; charset=utf-8"
    );
  }

  [HttpPut]
  public async Task<IActionResult> Put(
    [FromBody] ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    return await SaveAsync(
      settings,
      null,
      "Settings were not saved because validation failed.",
      cancellationToken
    );
  }

  [HttpPut("yaml")]
  public async Task<IActionResult> PutYaml(
    [FromBody] PortableYamlSettingsRequest request,
    CancellationToken cancellationToken
  )
  {
    var previous = await _settingsStore.GetAsync(
      cancellationToken
    );
    var imported = _portableYaml.Import(
      request.Yaml ?? string.Empty,
      previous
    );

    if (imported.Errors.Count > 0 || imported.Settings is null)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "YAML settings were not imported because the document is invalid.",
          imported.Errors
        )
      );
    }

    return await SaveAsync(
      imported.Settings,
      previous,
      "YAML settings were not imported because validation failed.",
      cancellationToken
    );
  }

  private async Task<IActionResult> SaveAsync(
    ApplicationSettings settings,
    ApplicationSettings? previous,
    string validationMessage,
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
          validationMessage,
          errors
        )
      );
    }

    previous ??= await _settingsStore.GetAsync(
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
    await _workspaceProfiles.UpdateDefaultModelAsync(
      settings.DefaultModel,
      cancellationToken
    );
    if (!string.IsNullOrWhiteSpace(
      settings.TrustedWorkspacePath
    ))
    {
      await _trustedWorkspace.ConfigureAsync(
        settings.TrustedWorkspacePath,
        cancellationToken
      );
    }

    return Ok(
      result.Settings
    );
  }
}
