using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/history-files")]
public sealed class HistoryFilesController(ConversationHistoryFiles files, ILogger<HistoryFilesController> logger) : ControllerBase
{
  public sealed record PromoteHistoryRequest(string WindowId, string SessionId);
  [HttpPost("promote")]
  public Task<IActionResult> Promote(PromoteHistoryRequest request, CancellationToken token) => ExecuteAsync(async () =>
  {
    await files.PromoteAsync(request.WindowId, request.SessionId, token);
    return new { saved = true };
  });
  [HttpPost("git-decision")]
  public Task<IActionResult> GitDecision(HistoryGitDecision request, CancellationToken token) =>
    ExecuteAsync(() => files.GitDecisionAsync(request, token));

  [HttpPost("rewind/{operationId}")]
  public Task<IActionResult> Rewind(string operationId, HistoryRewindRequest request, CancellationToken token) =>
    ExecuteAsync(() => files.RewindAsync(request, HistoryContentStore.RequireId(operationId), token));

  [HttpGet]
  public Task<IActionResult> List(CancellationToken token) => ExecuteAsync(() => files.ListAsync(token));

  [HttpGet("operations/{id}")]
  public Task<IActionResult> Progress(string id, CancellationToken token) => ExecuteAsync(async () =>
    files.Progress((await files.ActiveAsync(token)).Id, id));

  [HttpPost("compact")]
  public Task<IActionResult> Compact(HostFileOperationRequest request, CancellationToken token) =>
    ExecuteAsync(() => files.CompactAsync(request, token));

  [HttpPost("open")]
  public Task<IActionResult> Open(HistoryOpenFileRequest request, CancellationToken token) => ExecuteAsync(async () =>
  {
    await files.OpenAsync(request.FileId, token);
    return new { opened = true };
  });

  [HttpPost("download")]
  public async Task<IActionResult> Download(HistoryOpenFileRequest request, CancellationToken token)
  {
    try
    {
      var copy = await files.MaterializeAsync(request.FileId, token);
      return File(new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read),
        "application/octet-stream", Path.GetFileName(copy));
    }
    catch (WorkspaceProfileException exception) { return Failure(exception); }
    catch (GitDeliveryException exception)
    {
      logger.LogWarning(exception, "Host history Git state verification failed.");
      return BadRequest(new { code = exception.Code, stage = exception.Stage, message = exception.Message, retryable = exception.Retryable });
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      logger.LogWarning(exception, "History consultation copy could not be materialized.");
      return BadRequest(new
      {
        code = "history-storage-failed",
        stage = "history-files",
        message = "The saved file could not be accessed or verified.",
        retryable = true
      });
    }
  }

  [HttpPost("release-window/{id}")]
  public async Task<IActionResult> Release(string id, CancellationToken token)
  {
    await files.ReleaseWindowAsync(id, token);
    return NoContent();
  }

  private async Task<IActionResult> ExecuteAsync<T>(Func<Task<T>> action)
  {
    try { return Ok(await action()); }
    catch (WorkspaceProfileException exception) { return Failure(exception); }
    catch (GitDeliveryException exception)
    {
      logger.LogWarning(exception, "Host history Git state verification failed.");
      return BadRequest(new { code = exception.Code, stage = exception.Stage, message = exception.Message, retryable = exception.Retryable });
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
      or System.ComponentModel.Win32Exception)
    {
      logger.LogWarning(exception, "Host history operation failed without changing its conversation projection.");
      return BadRequest(new
      {
        code = "history-storage-failed",
        stage = "history-files",
        message = "History content could not be accessed or verified. The conversation was retained.",
        retryable = true
      });
    }
  }
  private IActionResult Failure(WorkspaceProfileException exception) => BadRequest(new
  {
    code = exception.Code,
    stage = exception.Stage,
    message = exception.Message,
    retryable = exception.Retryable
  });
}
