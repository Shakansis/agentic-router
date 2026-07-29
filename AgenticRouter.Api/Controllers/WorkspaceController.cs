using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/workspace")]
public sealed class WorkspaceController : ControllerBase
{
  private readonly ITrustedWorkspaceService _workspace;
  private readonly IFolderPickerService _folderPicker;

  public WorkspaceController(
    ITrustedWorkspaceService workspace,
    IFolderPickerService folderPicker
  )
  {
    _workspace = workspace;
    _folderPicker = folderPicker;
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
