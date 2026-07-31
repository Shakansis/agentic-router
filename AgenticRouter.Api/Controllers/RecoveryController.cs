using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Recovery;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/recovery")]
public sealed class RecoveryController : ControllerBase
{
  private readonly SafeModeState _safeMode;
  private readonly ILocalBackupService _backups;
  private readonly IRecoveryDecisionCoordinator _recoveryDecisions;

  public RecoveryController(
    SafeModeState safeMode,
    ILocalBackupService backups,
    IRecoveryDecisionCoordinator recoveryDecisions
  )
  {
    _safeMode = safeMode;
    _backups = backups;
    _recoveryDecisions = recoveryDecisions;
  }

  [HttpGet("status")]
  public ActionResult<RecoveryStatus> Status()
  {
    return Ok(
      new RecoveryStatus(
        _safeMode.Enabled,
        _safeMode.Reason,
        _safeMode.Enabled,
        _safeMode.Enabled,
        _safeMode.Enabled,
        _safeMode.Enabled
      )
    );
  }

  [HttpPost("backup")]
  public async Task<IActionResult> CreateBackup(
    [FromBody] LocalBackupOptions options,
    CancellationToken cancellationToken
  )
  {
    var content = await _backups.CreateAsync(
      options,
      cancellationToken
    );
    return File(
      content,
      "application/zip",
      $"agentic-router-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip"
    );
  }

  [HttpPost("{checkpointId}/decision")]
  public ActionResult<RecoveryDecisionResponse> Decide(
    string checkpointId,
    [FromBody] RecoveryDecisionRequest request
  )
  {
    var normalizedOption = request.Option.Trim().ToLowerInvariant();
    var accepted = _recoveryDecisions.TryDecide(
      checkpointId,
      request.BrowserSessionId,
      request.ExecutionSessionId,
      normalizedOption
    );
    var response = new RecoveryDecisionResponse(
      checkpointId,
      accepted,
      normalizedOption,
      accepted
        ? null
        : "The recovery checkpoint is no longer pending, the option is unavailable, or it belongs to a different execution session."
    );

    return accepted
      ? Ok(
        response
      )
      : NotFound(
        response
      );
  }

  [HttpPost("backup/inspect")]
  public async Task<ActionResult<BackupInspection>> Inspect(
    [FromBody] InspectBackupRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(
        await _backups.InspectAsync(
          Convert.FromBase64String(
            request.ArchiveBase64
          ),
          cancellationToken
        )
      );
    }
    catch (Exception exception) when (
      exception is FormatException
      or InvalidDataException
      or IOException
    )
    {
      return BadRequest(
        new
        {
          code = "backup-invalid",
          stage = "backup-inspection",
          message = exception.Message,
          retryable = false
        }
      );
    }
  }

  [HttpPost("backup/restore")]
  public async Task<ActionResult<RestoreBackupResult>> Restore(
    [FromBody] RestoreBackupRequest request,
    CancellationToken cancellationToken
  )
  {
    if (!request.Confirmed)
    {
      return BadRequest(
        new
        {
          code = "backup-restore-confirmation-required",
          stage = "backup-restore",
          message = "Restore requires explicit confirmation.",
          retryable = false
        }
      );
    }

    try
    {
      return Ok(
        await _backups.RestoreAsync(
          Convert.FromBase64String(
            request.ArchiveBase64
          ),
          request.Categories,
          cancellationToken
        )
      );
    }
    catch (Exception exception) when (
      exception is FormatException
      or InvalidDataException
      or IOException
      or UnauthorizedAccessException
    )
    {
      return BadRequest(
        new
        {
          code = "backup-restore-failed",
          stage = "backup-restore",
          message = exception.Message,
          retryable = false
        }
      );
    }
  }
}
