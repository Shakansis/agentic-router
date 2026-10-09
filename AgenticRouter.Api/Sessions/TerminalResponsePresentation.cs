using System.Text;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Markdown;

namespace AgenticRouter.Api.Sessions;

internal static class TerminalResponsePresentation
{
  public static ChatMessage Normalize(ChatMessage message, IMarkdownRenderer markdown)
  {
    if (message.Role != "assistant") return message;

    // Legacy harness terminals repeated the streamed answer before the Host status.
    var response = new StringBuilder();
    var timeline = message.Timeline?.Select(item =>
    {
      if (item.Type == "response.delta") response.Append(item.Delta);
      if (item.Type != "response.completed" || item.ResponseTail is null) return item;
      var tail = RemoveRepeatedResponse(item.ResponseTail, response.ToString());
      return tail == item.ResponseTail ? item : item with
      {
        ResponseTail = tail.Length == 0 ? null : tail,
        ResponseTailHtml = tail.Length == 0 ? null : markdown.Render(tail),
        RenderedHtml = tail.Length == 0 ? null : markdown.Render(tail)
      };
    }).ToArray();

    response.Clear();
    var blocks = new List<ChatMessageContentBlock>();
    foreach (var block in message.ContentBlocks ?? [])
    {
      if (block.Kind != "response")
      {
        blocks.Add(block);
        continue;
      }
      if (block.Id?.StartsWith("terminal:", StringComparison.Ordinal) == true)
      {
        var tail = RemoveRepeatedResponse(block.Content, response.ToString());
        if (tail.Length > 0) blocks.Add(block with { Content = tail });
      }
      else
      {
        response.Append(block.Content);
        blocks.Add(block);
      }
    }
    return message with
    {
      Timeline = timeline,
      ContentBlocks = message.ContentBlocks is null ? null : blocks.ToArray()
    };
  }

  private static string RemoveRepeatedResponse(string tail, string response)
  {
    if (response.Length == 0) return tail;
    if (string.Equals(tail, response, StringComparison.Ordinal)) return string.Empty;
    const string separator = "\n\n---\n";
    return tail.StartsWith(response + separator, StringComparison.Ordinal)
      ? tail[(response.Length + separator.Length)..]
      : tail;
  }
}
