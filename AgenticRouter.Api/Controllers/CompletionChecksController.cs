using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/completion-checks")]
public sealed class CompletionChecksController(CompletionCheckService checks) : ControllerBase
{
  [HttpPost("{id}")]
  public async Task<ActionResult<ExecutionCompletionReport>> Check(string id,
    [FromBody] CompletionCheckRequest request, CancellationToken cancellationToken)
  {
    try
    {
      var report = await checks.CheckAsync(id, request, cancellationToken);
      return report is null ? NotFound(new
      {
        code = "completion-check-unavailable",
        message = "The recorded evidence for this completion check is unavailable."
      }) : Ok(report);
    }
    catch (WorkspaceProfileException exception)
    {
      return NotFound(new { code = exception.Code, message = exception.Message });
    }
  }
}
