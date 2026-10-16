using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticRouter.Api.Execution;

// Ollama speaks JSON function calls; current Codex exposes its native patch
// engine only as a custom tool. Translate this exact tool at the provider wire
// boundary. Execution and approvals remain with Codex and the existing Host path.
internal sealed class CodexApplyPatchWireAdapter
{
  private readonly HashSet<string> _patchItems = new(StringComparer.Ordinal);
  public bool StreamRequested { get; private init; }

  public static CodexApplyPatchWireAdapter? AdaptRequest(JsonObject body)
  {
    if (body["tools"] is not JsonArray tools) return null;
    static IEnumerable<(JsonArray Tools, JsonObject Tool)> Definitions(JsonArray array)
    {
      foreach (var tool in array.OfType<JsonObject>())
      {
        yield return (array, tool);
        if (Text(tool, "type") == "namespace" && tool["tools"] is JsonArray members)
          foreach (var member in Definitions(members)) yield return member;
      }
    }
    var declaration = Definitions(tools).SingleOrDefault(item =>
      Text(item.Tool, "type") == "custom" && Text(item.Tool, "name") == "apply_patch");
    if (declaration.Tool is not { } patch) return null;
    tools = declaration.Tools;
    tools[tools.IndexOf(patch)] = new JsonObject
    {
      ["type"] = "function",
      ["name"] = "apply_patch",
      ["description"] = patch["description"]?.DeepClone(),
      ["parameters"] = new JsonObject
      {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
          ["input"] = new JsonObject
          {
            ["type"] = "string",
            ["description"] = patch["format"] is JsonObject format
              ? Text(format, "definition")
              : "Native Codex patch text, from *** Begin Patch through *** End Patch."
          }
        },
        ["required"] = new JsonArray("input"),
        ["additionalProperties"] = false
      }
    };
    if (body["input"] is JsonArray input)
    {
      var calls = input.OfType<JsonObject>().Where(item =>
        Text(item, "type") == "custom_tool_call" && Text(item, "name") == "apply_patch").ToArray();
      var ids = calls.Select(item => Text(item, "call_id")).ToHashSet(StringComparer.Ordinal);
      foreach (var call in calls)
      {
        call["type"] = "function_call";
        call["arguments"] = new JsonObject { ["input"] = call["input"]?.DeepClone() }.ToJsonString();
        call.Remove("input");
      }
      foreach (var item in input.OfType<JsonObject>().Where(item =>
        Text(item, "type") == "custom_tool_call_output" && ids.Contains(Text(item, "call_id"))))
        item["type"] = "function_call_output";
    }
    return new CodexApplyPatchWireAdapter
    {
      StreamRequested = body["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var requested) && requested
    };
  }

  public async Task ForwardJsonAsync(Stream input, Stream output, Action<byte[]> observe,
    CancellationToken cancellationToken)
  {
    var response = await JsonNode.ParseAsync(input, cancellationToken: cancellationToken) as JsonObject
      ?? throw new JsonException("codex-apply-patch-protocol: Invalid Responses JSON object.");
    observe(Encoding.UTF8.GetBytes(response.ToJsonString()));
    // Non-streaming auxiliary requests use the same tool wire contract.
    Transform(new JsonObject { ["response"] = response });
    if (!StreamRequested)
    {
      await output.WriteAsync(Encoding.UTF8.GetBytes(response.ToJsonString()), cancellationToken);
      return;
    }
    if (response["error"] is JsonObject error && Text(response, "status") is null)
    {
      await FrameAsync("response.failed", new JsonObject
      {
        ["response"] = new JsonObject { ["status"] = "failed", ["error"] = error.DeepClone() }
      });
      return;
    }
    var status = Text(response, "status");
    if (Text(response, "object") != "response" || Text(response, "id") is null
      || status is not ("completed" or "incomplete" or "failed")
      || response["output"] is not JsonArray items)
      throw new JsonException("codex-apply-patch-protocol: Expected a terminal Responses JSON object.");
    await FrameAsync("response.created", new JsonObject
    {
      ["response"] = new JsonObject { ["id"] = Text(response, "id"), ["status"] = "in_progress" }
    });
    for (var index = 0; index < items.Count; index++)
    {
      if (items[index] is not JsonObject item)
        throw new JsonException("codex-apply-patch-protocol: Invalid Responses output item.");
      await FrameAsync("response.output_item.added", new JsonObject
      {
        ["output_index"] = index,
        ["item"] = item.DeepClone()
      });
      if (Text(item, "type") == "message" && item["content"] is JsonArray parts)
        for (var contentIndex = 0; contentIndex < parts.Count; contentIndex++)
          if (parts[contentIndex] is JsonObject part && Text(part, "type") == "output_text")
            await FrameAsync("response.output_text.delta", new JsonObject
            {
              ["item_id"] = Text(item, "id"),
              ["output_index"] = index,
              ["content_index"] = contentIndex,
              ["delta"] = Text(part, "text") ?? ""
            });
      await FrameAsync("response.output_item.done", new JsonObject
      {
        ["output_index"] = index,
        ["item"] = item.DeepClone()
      });
    }
    await FrameAsync($"response.{status}", new JsonObject { ["response"] = response.DeepClone() });

    async Task FrameAsync(string type, JsonObject frame)
    {
      frame["type"] = type;
      await output.WriteAsync(Encoding.UTF8.GetBytes($"event: {type}\ndata: {frame.ToJsonString()}\n\n"), cancellationToken);
      await output.FlushAsync(cancellationToken);
    }
  }

  public async Task ForwardAsync(Stream input, Stream output, Action<byte[]> observe,
    CancellationToken cancellationToken)
  {
    using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
    var frame = new List<string>();
    while (await reader.ReadLineAsync(cancellationToken) is { } line)
    {
      if (line.Length > 0) { frame.Add(line); continue; }
      await ForwardFrameAsync();
    }
    if (frame.Count > 0) await ForwardFrameAsync();

    async Task ForwardFrameAsync()
    {
      var data = string.Join("\n", frame.Where(line => line.StartsWith("data:", StringComparison.Ordinal))
        .Select(line => line[5..].TrimStart()));
      if (data.StartsWith('{'))
      {
        observe(Encoding.UTF8.GetBytes(data));
        var value = JsonNode.Parse(data) as JsonObject
          ?? throw new JsonException("codex-apply-patch-protocol: Invalid Responses frame.");
        if (Transform(value))
        {
          var type = Text(value, "type");
          await WriteAsync($"event: {type}\ndata: {value.ToJsonString()}\n\n");
        }
      }
      else if (frame.Count > 0)
      {
        if (frame.Any(line => !line.StartsWith(':') && !line.StartsWith("event:", StringComparison.Ordinal)
          && !line.StartsWith("data:", StringComparison.Ordinal) && !line.StartsWith("id:", StringComparison.Ordinal)
          && !line.StartsWith("retry:", StringComparison.Ordinal)))
          throw new JsonException("codex-apply-patch-protocol: Invalid Responses event framing.");
        await WriteAsync(string.Join("\n", frame) + "\n\n");
      }
      frame.Clear();
    }
    async Task WriteAsync(string text)
    {
      await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
      await output.FlushAsync(cancellationToken);
    }
  }

  internal bool Transform(JsonObject value)
  {
    if (value["item"] is JsonObject item) ConvertCall(item);
    var type = Text(value, "type");
    if (_patchItems.Contains(Text(value, "item_id") ?? ""))
    {
      // JSON string fragments cannot be forwarded as raw patch fragments. The
      // completed arguments/item carry the full input; other tool streams flow.
      if (type == "response.function_call_arguments.delta") return false;
      if (type == "response.function_call_arguments.done")
      {
        value["type"] = "response.custom_tool_call_input.done";
        value["input"] = PatchInput(Text(value, "arguments"));
        value.Remove("arguments");
      }
    }
    if (value["response"] is JsonObject response && response["output"] is JsonArray items)
      foreach (var call in items.OfType<JsonObject>()) ConvertCall(call);
    return true;
  }

  private void ConvertCall(JsonObject item)
  {
    if (Text(item, "type") != "function_call" || Text(item, "name") != "apply_patch") return;
    if (Text(item, "id") is string id) _patchItems.Add(id);
    item["type"] = "custom_tool_call";
    var arguments = Text(item, "arguments");
    item["input"] = string.IsNullOrEmpty(arguments) ? "" : PatchInput(arguments);
    item.Remove("arguments");
  }

  private static string PatchInput(string? arguments)
  {
    if (JsonNode.Parse(arguments ?? "") is JsonObject parsed
      && Text(parsed, "input") is string input) return input;
    throw new JsonException("codex-apply-patch-protocol: Missing string input.");
  }

  private static string? Text(JsonObject value, string name) =>
    value[name] is JsonValue field && field.TryGetValue<string>(out var text) ? text : null;
}
