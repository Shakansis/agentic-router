using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController : ControllerBase
{
  private readonly IPersistentSessionService _sessions;
  private readonly IConversationProductivityService _productivity;
  private readonly IMarkdownRenderer _markdown;

  public SessionsController(
    IPersistentSessionService sessions,
    IConversationProductivityService productivity,
    IMarkdownRenderer markdown
  )
  {
    _sessions = sessions;
    _productivity = productivity;
    _markdown = markdown;
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

  [HttpPost("search")]
  public async Task<IActionResult> Search(
    [FromBody] ConversationSearchRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _productivity.SearchAsync(
        request,
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
      async () => Present(
        await _sessions.ResumeAsync(
          id,
          request.BrowserSessionId,
          cancellationToken
        )
      )
    );
  }

  [HttpGet("{id}")]
  public async Task<IActionResult> OpenReadOnly(
    string id,
    [FromQuery] string workspaceId,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      async () => Present(
        await _sessions.OpenReadOnlyAsync(
          workspaceId,
          id,
          cancellationToken
        )
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

  [HttpPut("{id}/pin")]
  public async Task<IActionResult> SetPinned(
    string id,
    [FromBody] SetConversationPinnedRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _productivity.SetPinnedAsync(
        id,
        request.Pinned,
        cancellationToken
      )
    );
  }

  [HttpPost("{id}/duplicate")]
  public async Task<IActionResult> Duplicate(
    string id,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _productivity.DuplicateAsync(
        id,
        cancellationToken
      )
    );
  }

  [HttpGet("{id}/summary/estimate")]
  public async Task<IActionResult> EstimateSummary(
    string id,
    [FromQuery] string model,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _productivity.EstimateSummaryAsync(
        id,
        model,
        cancellationToken
      )
    );
  }

  [HttpGet("{id}/summary")]
  public async Task<IActionResult> GetSummary(
    string id,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _productivity.GetSummaryAsync(
        id,
        cancellationToken
      )
    );
  }

  [HttpPost("{id}/summary")]
  public async Task<IActionResult> GenerateSummary(
    string id,
    [FromBody] GenerateSessionSummaryRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _productivity.GenerateSummaryAsync(
        id,
        request,
        cancellationToken
      )
    );
  }

  [HttpPut("{id}/summary")]
  public async Task<IActionResult> UpdateSummary(
    string id,
    [FromBody] UpdateSessionSummaryRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      () => _productivity.UpdateSummaryAsync(
        id,
        request.Content,
        cancellationToken
      )
    );
  }

  [HttpDelete("{id}/summary")]
  public async Task<IActionResult> DeleteSummary(
    string id,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await _productivity.DeleteSummaryAsync(
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

  [HttpGet("{id}/export/markdown")]
  public async Task<IActionResult> ExportMarkdown(
    string id,
    [FromQuery] bool includeSummary = true,
    [FromQuery] bool includeModelMetadata = false,
    CancellationToken cancellationToken = default
  )
  {
    try
    {
      var content = await _productivity.ExportMarkdownAsync(
        id,
        includeSummary,
        includeModelMetadata,
        cancellationToken
      );
      return File(
        content,
        "text/markdown; charset=utf-8",
        $"agentic-router-session-{id}.md"
      );
    }
    catch (WorkspaceProfileException exception)
    {
      return BadRequest(
        Error(
          "session-markdown-export-failed",
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

  private ConversationSessionRecord Present(
    ConversationSessionRecord session
  )
  {
    return session with
    {
      Messages = session.Messages.Select(
        message => message.Role == "assistant"
          ? message with
          {
            RenderedHtml = _markdown.Render(
              message.Content
            ),
            ContentBlocks = message.ContentBlocks?.Select(
              block => block.Kind == "response"
                ? block with
                {
                  RenderedHtml = _markdown.Render(
                    block.Content
                  )
                }
                : block
            ).ToArray()
          }
          : message
      ).ToArray()
    };
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
