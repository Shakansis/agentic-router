using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.Chat;

public sealed partial class ChatStreamService
{
  private ToolWindowTurn? _toolWindow;

  private (ChatStreamEvent Event, string Result, bool Succeeded) SelectToolWindowTarget(
    JsonElement arguments, string requestId, string model, string? intention)
  {
    ToolWindowOutput? output = null;
    LocalActionException? failure = null;
    try
    {
      if (_toolWindow is null)
        throw new LocalActionException("tool-window-unavailable", "No auxiliary tool is attached to this turn. Continue using the offered tools.");
      output = _toolWindow.Select(arguments);
    }
    catch (LocalActionException exception) { failure = exception; }
    var message = failure?.Message ?? $"Response target: {output!.Target}. {output.Reason}"
      + (output.Replacement is null ? string.Empty : " Replacement proposed; review and Apply are still required.");
    return (new ChatStreamEvent(requestId,
      failure is not null ? "tool-window.target-rejected" : output!.Replacement is null ? "tool-window.target-selected" : "tool-window.proposal",
      DateTimeOffset.UtcNow, message, null, model, intention, null, null, null, ToolWindow: output),
      failure is null ? message : $"{failure.Stage}: {message}", failure is null);
  }

  private async IAsyncEnumerable<ChatStreamEvent> StreamCoreAsync(
    ChatRequest request, string requestId, ExecutionSpecialistTurnInvocation invocation,
    [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    ToolWindowTurn.Validate(request.ToolWindow);
    _toolWindow = request.ToolWindow is null ? null : new ToolWindowTurn(request.ToolWindow, invocation.Role == ExecutionContextRole.Supervisor);
    var content = new StringBuilder();
    if (_toolWindow is not null)
      yield return new ChatStreamEvent(requestId, "tool-window.target-selected", DateTimeOffset.UtcNow,
        "Response target: story-editor (active auxiliary tab).", null, null, null, null, null, null);
    await foreach (var item in StreamUnroutedCoreAsync(request, requestId, invocation, cancellationToken))
    {
      if (invocation.Role != ExecutionContextRole.Direct || _toolWindow?.Target != "story-editor") { yield return item; continue; }
      if (item.Type == "response.delta")
      {
        content.Append(item.Delta);
        yield return item with
        {
          Type = "tool-window.delta",
          Delta = null,
          RenderedHtml = null,
          ResponseSegmentHtml = null,
          ToolWindow = new ToolWindowOutput(_toolWindow.Target, _toolWindow.Reason, _toolWindow.Context.Revision, Delta: item.Delta)
        };
        // Keep response text out of Chat while retaining progress and action events.
        continue;
      }
      if (item.Type == "response.completed")
      {
        content.Append(item.ResponseTail);
        yield return item with
        {
          Type = "tool-window.response",
          Delta = null,
          RenderedHtml = null,
          ResponseSegmentHtml = null,
          ResponseTail = null,
          ResponseTailHtml = null,
          SpecialistCompletion = null,
          ToolWindow = new ToolWindowOutput(_toolWindow.Target, _toolWindow.Reason, _toolWindow.Context.Revision,
            RenderedHtml: _markdownRenderer.Render(content.ToString()))
        };
        const string delivered = "Response delivered to Storytelling Editor. Proposed changes require review and Apply.";
        yield return item with
        {
          Delta = null,
          RenderedHtml = _markdownRenderer.Render(delivered),
          ResponseSegmentHtml = null,
          SpecialistCompletion = null,
          ResponseTail = delivered,
          ResponseTailHtml = _markdownRenderer.Render(delivered)
        };
        continue;
      }
      yield return item with { ResponseSegmentHtml = null };
    }
  }
}
