using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController : ControllerBase
{
  private readonly IPersistentSessionService _sessions;

  public SessionsController(
    IPersistentSessionService sessions
  )
  {
    _sessions = sessions;
  }

  [HttpGet]
  public async Task<IActionResult> List(
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _sessions.ListAsync(
        cancellationToken
      )
    );
  }

  [HttpPost("new")]
  public async Task<IActionResult> Create(
    [FromBody] CreateConversationSessionRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _sessions.CreateAsync(
        request.BrowserSessionId,
        cancellationToken
      )
    );
  }

  [HttpPut("current")]
  public async Task<IActionResult> Save(
    [FromBody] SaveConversationSessionRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _sessions.SaveAsync(
        request,
        cancellationToken
      )
    );
  }

  [HttpPost("{id}/resume")]
  public async Task<IActionResult> Resume(
    string id,
    [FromBody] ResumeConversationSessionRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _sessions.ResumeAsync(
        id,
        request.BrowserSessionId,
        cancellationToken
      )
    );
  }

  [HttpPut("{id}/name")]
  public async Task<IActionResult> Rename(
    string id,
    [FromBody] RenameConversationSessionRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _sessions.RenameAsync(
        id,
        request.Title,
        cancellationToken
      )
    );
  }

  [HttpPost("{id}/archive")]
  public async Task<IActionResult> Archive(
    string id,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _sessions.ArchiveAsync(
        id,
        cancellationToken
      )
    );
  }

  [HttpDelete("{id}")]
  public async Task<IActionResult> Delete(
    string id,
    [FromQuery] bool confirmed,
    CancellationToken cancellationToken
  )
  {
    if (!confirmed)
    {
      return BadRequest(
        Error(
          "session-deletion-confirmation-required",
          "session-deletion",
          "Deleting local session history requires explicit confirmation.",
          false
        )
      );
    }

    try
    {
      await _sessions.DeleteAsync(
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

  [HttpDelete("archived")]
  public async Task<IActionResult> DeleteArchived(
    [FromQuery] bool confirmed,
    CancellationToken cancellationToken
  )
  {
    return await DeleteManyAsync(
      confirmed,
      () => _sessions.DeleteArchivedAsync(
        cancellationToken
      )
    );
  }

  [HttpDelete]
  public async Task<IActionResult> DeleteAll(
    [FromQuery] bool confirmed,
    CancellationToken cancellationToken
  )
  {
    return await DeleteManyAsync(
      confirmed,
      () => _sessions.DeleteAllAsync(
        cancellationToken
      )
    );
  }

  [HttpGet("{id}/export")]
  public async Task<IActionResult> Export(
    string id,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var content = await _sessions.ExportAsync(
        id,
        cancellationToken
      );
      return File(
        content,
        "application/json",
        $"agentic-router-session-{id}.json"
      );
    }
    catch (WorkspaceProfileException exception)
    {
      return BadRequest(
        Error(
          "session-export-failed",
          exception.Stage,
          exception.Message,
          exception.Retryable
        )
      );
    }
  }

  private async Task<IActionResult> DeleteManyAsync(
    bool confirmed,
    Func<Task> operation
  )
  {
    if (!confirmed)
    {
      return BadRequest(
        Error(
          "session-deletion-confirmation-required",
          "session-deletion",
          "Deleting local session history requires explicit confirmation.",
          false
        )
      );
    }

    try
    {
      await operation();
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
