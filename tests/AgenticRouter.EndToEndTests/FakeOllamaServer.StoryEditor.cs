using System.Net;
using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed partial class FakeOllamaServer
{
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
