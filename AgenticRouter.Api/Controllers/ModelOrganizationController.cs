using AgenticRouter.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/model-organization")]
public sealed class ModelOrganizationController : ControllerBase
{
  private readonly IModelOrganizationService _organization;

  public ModelOrganizationController(
    IModelOrganizationService organization
  )
  {
    _organization = organization;
  }

  [HttpGet]
  public Task<IActionResult> Get(
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      () => _organization.GetAsync(
        cancellationToken
      )
    );
  }

  [HttpPut("preference")]
  public Task<IActionResult> SavePreference(
    [FromBody] SaveModelPreferenceRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      () => _organization.SavePreferenceAsync(
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("profiles")]
  public Task<IActionResult> SaveProfile(
    [FromBody] SaveModelProfileRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      () => _organization.SaveProfileAsync(
        request,
        cancellationToken
      )
    );
  }

  [HttpGet("profiles/{profileId}/preview")]
  public Task<IActionResult> Preview(
    string profileId,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      () => _organization.PreviewAsync(
        profileId,
        cancellationToken
      )
    );
  }

  [HttpPost("profiles/{profileId}/apply")]
  public Task<IActionResult> Apply(
    string profileId,
    [FromBody] ApplyModelProfileRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      () => _organization.ApplyAsync(
        profileId,
        request.Confirmed,
        cancellationToken
      )
    );
  }

  [HttpDelete("profiles/{profileId}")]
  public Task<IActionResult> Delete(
    string profileId,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      () => _organization.DeleteProfileAsync(
        profileId,
        cancellationToken
      )
    );
  }

  [HttpPut("workspaces/{workspaceId}/preferred-profile")]
  public Task<IActionResult> SetWorkspacePreference(
    string workspaceId,
    [FromBody] SetWorkspaceModelProfileRequest request,
    CancellationToken cancellationToken
  )
  {
    return ExecuteAsync(
      () => _organization.SetWorkspacePreferenceAsync(
        workspaceId,
        request.ProfileId,
        cancellationToken
      )
    );
  }

  private async Task<IActionResult> ExecuteAsync<T>(
    Func<Task<T>> operation
  )
  {
    try
    {
      return Ok(
        await operation()
      );
    }
    catch (ModelOrganizationException exception)
    {
      return BadRequest(
        new
        {
          exception.Code,
          exception.Stage,
          exception.Message,
          exception.TraceId
        }
      );
    }
  }
}
