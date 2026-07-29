using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
public sealed class WorkspaceProfilesController : ControllerBase
{
  private readonly IWorkspaceProfileService _profiles;

  public WorkspaceProfilesController(
    IWorkspaceProfileService profiles
  )
  {
    _profiles = profiles;
  }

  [HttpGet]
  public async Task<ActionResult<WorkspaceProfilesResponse>> Get(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _profiles.GetAllAsync(
        cancellationToken
      )
    );
  }

  [HttpPost]
  public async Task<IActionResult> Create(
    [FromBody] CreateWorkspaceProfileRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _profiles.CreateAsync(
        request.Name,
        request.Path,
        cancellationToken
      )
    );
  }

  [HttpPut("{id}/name")]
  public async Task<IActionResult> Rename(
    string id,
    [FromBody] RenameWorkspaceProfileRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _profiles.RenameAsync(
        id,
        request.Name,
        cancellationToken
      )
    );
  }

  [HttpPost("{id}/activate")]
  public async Task<IActionResult> Activate(
    string id,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _profiles.ActivateAsync(
        id,
        cancellationToken
      )
    );
  }

  [HttpPut("{id}/history")]
  public async Task<IActionResult> SetHistory(
    string id,
    [FromBody] SetWorkspaceHistoryRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _profiles.SetHistoryEnabledAsync(
        id,
        request.Enabled,
        cancellationToken
      )
    );
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Remove(
    string id,
    [FromQuery] bool confirmed,
    CancellationToken cancellationToken
  )
  {
    if (!confirmed)
    {
      return BadRequest(
        Error(
          "workspace-removal-confirmation-required",
          "workspace-removal",
          "Removing a workspace profile requires explicit confirmation.",
          false
        )
      );
    }

    try
    {
      await _profiles.RemoveAsync(
        id,
        cancellationToken
      );
      return NoContent();
    }
    catch (WorkspaceProfileException exception)
    {
      return BadRequest(
        Error(
          exception.Code,
          exception.Stage,
          exception.Message,
          exception.Retryable
        )
      );
    }
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
    catch (WorkspaceProfileException exception)
    {
      return BadRequest(
        Error(
          exception.Code,
          exception.Stage,
          exception.Message,
          exception.Retryable
        )
      );
    }
  }

  private object Error(
    string code,
    string stage,
    string message,
    bool retryable
  )
  {
    return new
    {
      code,
      stage,
      message,
      retryable,
      traceId = HttpContext.TraceIdentifier
    };
  }
}
