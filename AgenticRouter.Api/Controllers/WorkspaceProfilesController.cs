using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Knowledge;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
public sealed class WorkspaceProfilesController : ControllerBase
{
  private readonly IWorkspaceProfileService _profiles;
  private readonly IFolderLauncherService _folderLauncher;
  private readonly IKnowledgeProviderRegistry _knowledgeProviders;

  public WorkspaceProfilesController(
    IWorkspaceProfileService profiles,
    IFolderLauncherService folderLauncher,
    IKnowledgeProviderRegistry knowledgeProviders
  )
  {
    _profiles = profiles;
    _folderLauncher = folderLauncher;
    _knowledgeProviders = knowledgeProviders;
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

  [HttpPost("active/open-folder")]
  public async Task<IActionResult> OpenActiveFolder(
    CancellationToken cancellationToken
  )
  {
    var active = await _profiles.GetActiveDataAsync(
      cancellationToken
    );
    if (active is null)
    {
      return BadRequest(
        Error(
          "workspace-not-configured",
          "workspace-folder-open",
          "No active workspace is configured.",
          false
        )
      );
    }

    var result = await _folderLauncher.OpenAsync(
      active.Path,
      cancellationToken
    );
    return result.Opened
      ? Ok(
        result
      )
      : BadRequest(
        Error(
          "workspace-folder-open-failed",
          "workspace-folder-open",
          result.Error ?? "The active workspace folder could not be opened.",
          true
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

  [HttpPut("{id}/knowledge")]
  public async Task<IActionResult> SetKnowledge(
    string id,
    [FromBody] SetWorkspaceKnowledgeRequest request,
    CancellationToken cancellationToken
  )
  {
    if (
      request.ProviderId is not null
      && !_knowledgeProviders.TryGet(request.ProviderId, out _)
    )
    {
      return BadRequest(
        Error(
          "knowledge-provider-not-found",
          "workspace-knowledge-settings",
          "The selected knowledge provider is not registered.",
          false
        )
      );
    }

    return await ExecuteAsync(
      () => _profiles.SetKnowledgeAsync(
        id,
        request.Enabled,
        request.ProviderId,
        request.LibraryIds,
        cancellationToken
      )
    );
  }

  [HttpDelete("{id}/process-permissions/{permissionId}")]
  public async Task<IActionResult> RevokeProcessPermission(
    string id,
    string permissionId,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _profiles.RevokeProcessPermissionAsync(
        id,
        permissionId,
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
