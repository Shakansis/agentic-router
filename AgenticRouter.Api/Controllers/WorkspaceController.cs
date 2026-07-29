using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.ProjectAwareness;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/workspace")]
public sealed class WorkspaceController : ControllerBase
{
  private readonly ITrustedWorkspaceService _workspace;
  private readonly IFolderPickerService _folderPicker;
  private readonly IProjectAwarenessService _projectAwareness;
  private readonly IValidationProfileService _validationProfiles;

  public WorkspaceController(
    ITrustedWorkspaceService workspace,
    IFolderPickerService folderPicker,
    IProjectAwarenessService projectAwareness,
    IValidationProfileService validationProfiles
  )
  {
    _workspace = workspace;
    _folderPicker = folderPicker;
    _projectAwareness = projectAwareness;
    _validationProfiles = validationProfiles;
  }

  [HttpGet("validation-profile")]
  public async Task<ActionResult<ValidationProfileState>> ValidationProfile(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _validationProfiles.GetStateAsync(
        cancellationToken
      )
    );
  }

  [HttpPut("validation-profile")]
  public async Task<IActionResult> SaveValidationProfile(
    [FromBody] ValidationProfileSettings profile,
    CancellationToken cancellationToken
  )
  {
    var result = await _validationProfiles.SaveAsync(
      profile,
      cancellationToken
    );
    return result.IsValid
      ? Ok(
        result.Settings!.ValidationProfile
      )
      : BadRequest(
        new ValidationErrorsResponse(
          "Validation profile was not saved because validation failed.",
          result.Errors
        )
      );
  }

  [HttpDelete("validation-profile")]
  public async Task<IActionResult> ClearValidationProfile(
    CancellationToken cancellationToken
  )
  {
    var result = await _validationProfiles.ClearAsync(
      cancellationToken
    );
    return result.IsValid
      ? Ok(
        new ValidationProfileState(
          null,
          (
            await _projectAwareness.GetAsync(
              true,
              cancellationToken
            )
          ).DetectedValidationProfile
        )
      )
      : BadRequest(
        new ValidationErrorsResponse(
          "Validation profile could not be cleared.",
          result.Errors
        )
      );
  }

  [HttpGet("project-profile")]
  public async Task<ActionResult<ProjectProfile>> ProjectProfile(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _projectAwareness.GetAsync(
        false,
        cancellationToken
      )
    );
  }

  [HttpPost("project-profile/refresh")]
  public async Task<ActionResult<ProjectProfile>> RefreshProjectProfile(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _projectAwareness.GetAsync(
        true,
        cancellationToken
      )
    );
  }

  [HttpGet]
  public async Task<ActionResult<TrustedWorkspaceStatus>> Get(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _workspace.GetStatusAsync(
        cancellationToken
      )
    );
  }

  [HttpPost("pick")]
  public async Task<ActionResult<FolderPickerResult>> Pick(
    CancellationToken cancellationToken
  )
  {
    var status = await _workspace.GetStatusAsync(
      cancellationToken
    );

    return Ok(
      await _folderPicker.PickAsync(
        status.Valid
          ? status.Path
          : null,
        cancellationToken
      )
    );
  }

  [HttpPut]
  public async Task<ActionResult<TrustedWorkspaceStatus>> Put(
    [FromBody] TrustedWorkspaceRequest request,
    CancellationToken cancellationToken
  )
  {
    var status = await _workspace.ConfigureAsync(
      request.Path,
      cancellationToken
    );

    return status.Valid
      ? Ok(
        status
      )
      : BadRequest(
        status
      );
  }

  [HttpDelete]
  public async Task<ActionResult<TrustedWorkspaceStatus>> Delete(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _workspace.ClearAsync(
        cancellationToken
      )
    );
  }
}
