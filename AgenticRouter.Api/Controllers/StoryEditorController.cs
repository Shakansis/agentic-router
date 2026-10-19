using AgenticRouter.Api.Chat;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/story-editor")]
public sealed class StoryEditorController(StoryEditorService editor, StoryWorkspaceFiles files, ILogger<StoryEditorController> logger) : ControllerBase
{
  [HttpGet("files")]
  public async Task<ActionResult<StoryFileList>> Files(string workspaceId, string? directory, CancellationToken token)
  {
    try { return Ok(await files.ListAsync(workspaceId, directory, token)); }
    catch (StoryEditorException exception) { return Failure(exception); }
  }

  [HttpGet("file")]
  public async Task<ActionResult<StoryWorkspaceDocument>> OpenFile(string workspaceId, string path, CancellationToken token)
  {
    try { return Ok(await files.OpenAsync(workspaceId, path, token)); }
    catch (StoryEditorException exception) { return Failure(exception); }
  }

  [HttpPut("file")]
  public async Task<ActionResult<StoryWorkspaceDocument>> SaveFile(StoryWorkspaceSave request, CancellationToken token)
  {
    try { return Ok(await files.SaveAsync(request, token)); }
    catch (StoryEditorException exception) { return Failure(exception); }
  }

  [HttpPost("preview")]
  public ActionResult<StoryEditorPreview> Preview(StoryEditorPreviewRequest request)
  {
    try { return Ok(editor.Preview(request)); }
    catch (StoryEditorException exception) { return Failure(exception); }
  }

  [HttpPost("proposals")]
  public async Task<ActionResult<StoryEditorProposal>> Propose(StoryEditorRequest request, CancellationToken token)
  {
    try { return Ok(await editor.ProposeAsync(request, token)); }
    catch (StoryEditorException exception)
    {
      return Failure(exception);
    }
  }

  private ObjectResult Failure(StoryEditorException exception)
  {
    logger.LogWarning(exception, "Story editor request failed: {Code}", exception.Code);
    return StatusCode(exception.StatusCode, new
    {
      message = exception.Message,
      code = exception.Code,
      traceId = HttpContext.TraceIdentifier,
      recoverable = exception.Recoverable
    });
  }
}
