using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/actions")]
public sealed class ActionsController : ControllerBase
{
  private readonly IApprovalCoordinator _approvalCoordinator;
  private readonly IWorkspaceProfileService _workspaceProfiles;

  public ActionsController(
    IApprovalCoordinator approvalCoordinator,
    IWorkspaceProfileService workspaceProfiles
  )
  {
    _approvalCoordinator = approvalCoordinator;
    _workspaceProfiles = workspaceProfiles;
  }

  [HttpPost("{actionId}/decision")]
  public async Task<ActionResult<ApprovalDecisionResponse>> Decide(
    string actionId,
    [FromBody] ApprovalDecisionRequest request,
    CancellationToken cancellationToken
  )
  {
    var result = await _approvalCoordinator.TryDecideAsync(
      actionId,
      request.BrowserSessionId,
      request.ExecutionSessionId,
      request.Approved,
      request.EditedText,
      request.RememberForWorkspace
        ? PersistProcessPermissionAsync
        : null,
      cancellationToken
    );
    var response = new ApprovalDecisionResponse(
      actionId,
      result.Accepted,
      request.Approved,
      result.Accepted
        ? null
        : result.Diagnostic,
      result.Action?.Summary,
      result.Action?.Preview,
      result.Accepted
        && request.Approved
        && request.RememberForWorkspace
    );

    if (!result.Pending)
    {
      return NotFound(
        response
      );
    }

    return result.Accepted
      ? Ok(response)
      : BadRequest(response);
  }

  private async Task<ApprovalPreparationResult> PersistProcessPermissionAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    if (
      action.Tool != "run_process"
      || !action.RequiresExplicitApproval
      || action.TargetPath is null
      || !Path.IsPathFullyQualified(
        action.TargetPath
      )
      || action.WorkingDirectory is null
    )
    {
      return new ApprovalPreparationResult(
        false,
        "Only an exact high-risk process with a resolved executable awaiting explicit approval can be remembered."
      );
    }

    try
    {
      var arguments = action.Arguments.TryGetProperty(
        "arguments",
        out var argumentElement
      )
        ? argumentElement.EnumerateArray().Select(
          argument => argument.GetString() ?? string.Empty
        ).ToArray()
        : [];
      await _workspaceProfiles.GrantProcessPermissionAsync(
        action.TargetPath,
        arguments,
        action.WorkingDirectory,
        cancellationToken
      );
      return new ApprovalPreparationResult(
        true
      );
    }
    catch (WorkspaceProfileException exception)
    {
      return new ApprovalPreparationResult(
        false,
        exception.Message
      );
    }
  }

  [HttpPost("{actionId}/revision")]
  public async Task<ActionResult<ApprovalRevisionResponse>> Revise(
    string actionId,
    [FromBody] ApprovalRevisionRequest request,
    CancellationToken cancellationToken
  )
  {
    var result = await _approvalCoordinator.TryReviseAsync(
      actionId,
      request.BrowserSessionId,
      request.ExecutionSessionId,
      request.EditedText,
      cancellationToken
    );
    var response = new ApprovalRevisionResponse(
      actionId,
      result.Accepted,
      result.Action?.Summary,
      result.Action?.Preview,
      result.Diagnostic
    );

    if (!result.Pending)
    {
      return NotFound(
        response
      );
    }

    return result.Accepted
      ? Ok(
        response
      )
      : BadRequest(
        response
      );
  }
}
