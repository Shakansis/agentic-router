using System.Net;
using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed partial class FakeOllamaServer
{
  private static async Task RespondToToolWindowAsync(HttpListenerResponse response, string model,
    IReadOnlyList<RecordedMessage> messages, IReadOnlyList<string> tools, CancellationToken token)
  {
    var prompt = messages.Last(message => message.Role == "user").Content;
    var priorCall = messages.Any(message => message.ToolCalls.Any(call => call.Name == "set_response_target")
      || message.Role == "user" && message.Content.Contains("TOOL_WINDOW_TARGET_SELECTED", StringComparison.Ordinal));
    var structured = messages.Any(message => message.Role == "system" && message.Content.StartsWith("EXPERT_EXECUTION_GUIDANCE_V1", StringComparison.Ordinal))
      && !messages.Any(message => message.Role == "system" && message.Content.Contains("Answer with a concise summary of what was actually completed", StringComparison.Ordinal));
    if (structured)
    {
      var sourceMessage = messages.First(message => message.Role == "system" && message.Content.Contains("AR_TOOL_WINDOW_V1", StringComparison.Ordinal)).Content;
      using var source = JsonDocument.Parse(sourceMessage.Split('\n').First(line => line.StartsWith("{\"toolId\":", StringComparison.Ordinal)));
      var guidance = JsonSerializer.Serialize(new
      {
        objective = priorCall ? "Tool window fixture response." : "I will review the passage in the active tool.",
        actionRequired = !priorCall,
        actions = priorCall ? Array.Empty<object>() : new object[] { new
        {
          title = "Propose a replacement in the editor", tool = "set_response_target",
          arguments = new { target = "story-editor", reason = "Revise the active draft.",
            replacement = source.RootElement.GetProperty("selection").GetString() + " — composer revision" }
        } },
        completionCriteria = new[] { "A reviewable proposal exists; the draft has not been applied." }
      });
      await WriteJsonAsync(response, HttpStatusCode.OK, new { model, message = new { role = "assistant", content = guidance }, done = true }, token);
      return;
    }
    if (!priorCall && !prompt.Contains("analysis tool window fixture", StringComparison.Ordinal)
      && tools.Contains("set_response_target"))
    {
      var chat = prompt.Contains("chat tool window fixture", StringComparison.Ordinal);
      var sourceMessage = messages.First(message => message.Role == "system" && message.Content.Contains("AR_TOOL_WINDOW_V1", StringComparison.Ordinal)).Content;
      using var source = JsonDocument.Parse(sourceMessage.Split('\n').First(line => line.StartsWith("{\"toolId\":", StringComparison.Ordinal)));
      object arguments = chat
        ? new { target = "chat", reason = "The user explicitly requested an answer in Chat." }
        : new { target = "story-editor", reason = "Revise the passage in the active editor.", replacement = source.RootElement.GetProperty("selection").GetString() + " — composer revision" };
      await WriteStreamingToolResponseAsync(response, model, "I will review the passage in the active tool.", null,
        new[] { new { id = "tool-window-call", function = new { name = "set_response_target", arguments } } }, 0, token);
      return;
    }
    await WriteStreamingToolResponseAsync(response, model, "Tool window fixture response.", null, null, 0, token);
  }

  private static async Task RespondToStoryEditorAsync(HttpListenerResponse response, string model,
    IReadOnlyList<RecordedMessage> messages, CancellationToken token)
  {
    var prompt = messages.Last(message => message.Role == "user").Content;
    if (prompt.Contains("editor provider failure fixture", StringComparison.Ordinal))
    {
      await WriteJsonAsync(response, HttpStatusCode.ServiceUnavailable,
        new { error = "Story editor provider unavailable fixture" }, token);
      return;
    }
    using var source = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
    var original = source.RootElement.GetProperty("selection").GetString()!;
    var output = prompt.Contains("Suggest 2–6 synonyms", StringComparison.Ordinal)
      ? prompt.Contains("editor malformed synonyms fixture", StringComparison.Ordinal)
        ? "These suggestions are not valid JSON."
        : JsonSerializer.Serialize(new
        {
          alternatives = new[]
        {
          new { text = "ancestral", explanation = "Suggests a distant origin." },
          new { text = "vetusto", explanation = "A more solemn narrative register." }
        }
        })
      : prompt.Contains("Rewrite ONLY the selection", StringComparison.Ordinal)
        ? "O velho mago cruzou a porta."
      : prompt.Contains("Check grammatical agreement ONLY", StringComparison.Ordinal)
        ? original.Replace("As menina estava cansado.", "As meninas estavam cansadas.", StringComparison.Ordinal)
        : original + " — revisado";
    response.StatusCode = 200;
    response.ContentType = "application/x-ndjson";
    response.SendChunked = true;
    await WriteChunkAsync(response, model, output, false, token, "Private editor reasoning.");
    await WriteChunkAsync(response, model, "", true, token);
    response.Close();
  }
}
