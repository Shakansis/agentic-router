using AgenticRouter.Api.Chat;
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
  private readonly LiveChatRuns _chatRuns;
  private readonly IPersistentSessionService _sessions;
  private readonly IConversationProductivityService _productivity;
  private readonly IMarkdownRenderer _markdown;

  public SessionsController(
    IPersistentSessionService sessions,
    LiveChatRuns chatRuns,
    IConversationProductivityService productivity,
    IMarkdownRenderer markdown
  )
  {
    _sessions = sessions;
    _chatRuns = chatRuns;
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
        await _sessions.OpenAsync(
          id,
          request.BrowserSessionId,
          cancellationToken
        )
      )
    );
  }

  [HttpPost("{id}/open")]
  public async Task<IActionResult> Open(
    string id,
    [FromBody] ResumeConversationSessionRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      async () => Present(
        await _sessions.OpenAsync(
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

  [HttpGet("{id}/history")]
  public async Task<IActionResult> History(
    string id,
    [FromQuery] string workspaceId,
    [FromQuery] int before,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(async () =>
    {
      var session = await _sessions.OpenReadOnlyAsync(workspaceId, id, cancellationToken);
      var end = Math.Clamp(before, 0, session.Messages.Count);
      var start = HistoryPageStart(session.Messages, end);
      return new ConversationHistoryPage(
        start,
        session.Messages.Skip(start).Take(end - start).Select(PresentMessage).ToArray()
      );
    });
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
    var run = _chatRuns.FindConversation(session.Id)?.View;
    if (run?.TurnId is not null)
      session = session with
      {
        Messages = session.Messages.Select(message => message.TurnId == run.TurnId
        ? message with { Timeline = null, Hidden = message.Role == "assistant" || message.Hidden }
        : message).ToArray()
      };
    var offset = HistoryPageStart(session.Messages, session.Messages.Count);
    return session with
    {
      ActiveChatRun = run,
      PresentationOffset = offset,
      LastContextUsage = session.Messages.SelectMany(message => message.Timeline ?? [])
        .LastOrDefault(item => item.ContextUsage is not null)?.ContextUsage,
      Messages = session.Messages.Select(
        (message, index) => index < offset
          ? message with { Timeline = null, ContentBlocks = null, RenderedHtml = null }
          : PresentMessage(message)
      ).ToArray()
    };
  }

  private ChatMessage PresentMessage(ChatMessage message)
  {
    return message.Role == "assistant"
      ? message with
      {
        RenderedHtml = _markdown.Render(message.Content),
        ContentBlocks = message.ContentBlocks?.Select(block => block.Kind == "response"
          ? block with { RenderedHtml = _markdown.Render(block.Content) }
          : block).ToArray()
      }
      : message;
  }

  private static int HistoryPageStart(IReadOnlyList<ChatMessage> messages, int end)
  {
    // A single long execution can contain tens of thousands of streamed events.
    // Always include one message; subsequent pages retain original edit indices.
    var start = end;
    long events = 0;
    while (start > 0 && end - start < 20)
    {
      var count = messages[start - 1].Timeline?.Count ?? 0;
      if (start < end && count > 0 && events + count > 2_000) break;
      events += count;
      start--;
    }
    return start;
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
