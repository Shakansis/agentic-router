using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/workspace/files")]
public sealed class WorkspaceFilesController(WorkspaceFileSearch search, ILogger<WorkspaceFilesController> logger) : ControllerBase
{
  [HttpGet]
  public async Task<IActionResult> Get(string workspaceId, string? query, CancellationToken token)
  {
    try { return Ok(await search.SearchAsync(workspaceId, query, token)); }
    catch (WorkspaceFileSearchException exception)
    {
      logger.LogWarning(exception, "Workspace file search failed: {Code}", exception.Code);
      return StatusCode(exception.Status, new { code = exception.Code, message = exception.Message, traceId = HttpContext.TraceIdentifier });
    }
  }
}
