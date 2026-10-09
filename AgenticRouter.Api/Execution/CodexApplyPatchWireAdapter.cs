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

  public static CodexApplyPatchWireAdapter? AdaptRequest(JsonObject body)
  {
    if (body["tools"] is not JsonArray tools) return null;
    var patch = tools.OfType<JsonObject>().SingleOrDefault(tool =>
      Text(tool, "type") == "custom" && Text(tool, "name") == "apply_patch");
    if (patch is null) return null;
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
    return new CodexApplyPatchWireAdapter();
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
      else if (frame.Count > 0) await WriteAsync(string.Join("\n", frame) + "\n\n");
      frame.Clear();
    }
    async Task WriteAsync(string text)
    {
      await output.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
      await output.FlushAsync(cancellationToken);
    }
  }

  private bool Transform(JsonObject value)
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
