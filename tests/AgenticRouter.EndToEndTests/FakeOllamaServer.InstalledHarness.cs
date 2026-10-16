using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed partial class FakeOllamaServer
{
  public bool InstalledHarnessProbe { get; set; }
  public bool InstalledNativeCompatibilityProbe { get; set; }
  public string? InstalledNativeMutationPath { get; set; }
  public string? InstalledNativeReadPath { get; set; }
  public string? InstalledNativeMutationTool { get; set; }
  public bool InstalledCodexPatchProbe { get; set; }
  public bool InstalledCodexOutsidePatchProbe { get; set; }
  public bool InstalledCodexMoveOutsideProbe { get; set; }
  public bool InstalledCodexReadProbe { get; set; }
  public bool InstalledCodexDisconnectProbe { get; set; }
  public bool InstalledCodexJsonProbe { get; set; }
  public bool InstalledCodexJsonErrorProbe { get; set; }
  public string? InstalledCodexSseContentType { get; set; }
  private int _installedPatchCalls;
  public ConcurrentQueue<JsonElement> InstalledHarnessRequests { get; } = new();

  private async Task HandleNativeCompatibilityProbeAsync(HttpListenerContext context, JsonElement request,
    IReadOnlyList<RecordedMessage> messages, IReadOnlyList<string> availableTools, CancellationToken token)
  {
    InstalledHarnessRequests.Enqueue(request.Clone());
    var tools = new System.Text.Json.Nodes.JsonArray();
    if (!messages.Any(message => message.Role == "tool" && (message.Content.Contains("NATIVE_COMPATIBILITY_READ_OK", StringComparison.Ordinal) || message.Content.Contains("OLD_VALUE", StringComparison.Ordinal))))
    {
      var read = availableTools.FirstOrDefault(name => name.EndsWith("_read_files", StringComparison.Ordinal) || name.EndsWith(".read_files", StringComparison.Ordinal) || name == "read_files")
        ?? availableTools.FirstOrDefault(name => name.EndsWith("_read_file", StringComparison.Ordinal) || name.EndsWith(".read_file", StringComparison.Ordinal) || name == "read_file");
      string? deferredRead = null;
      foreach (var result in messages.Where(message => message.Role == "tool"))
        foreach (System.Text.RegularExpressions.Match declaration in System.Text.RegularExpressions.Regex.Matches(result.Content, "<function>(.*?)</function>", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
          using var document = JsonDocument.Parse(declaration.Groups[1].Value);
          var candidate = document.RootElement.GetProperty("name").GetString();
          if (candidate?.EndsWith("__read_files", StringComparison.Ordinal) == true) deferredRead = candidate;
        }
      var name = read ?? (deferredRead is not null && availableTools.Contains("tool_call")
        ? "tool_call" : availableTools.FirstOrDefault(name => name == "tool_search"));
      if (name is null) throw new InvalidOperationException("The provider was not offered a Host read or native tool search.");
      var target = InstalledNativeMutationPath is null ? "initial.txt" : "crlf-probe.txt";
      var schema = request.GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("function").GetProperty("name").GetString() == name)
        .GetProperty("function").GetProperty("parameters");
      var arguments = name == "tool_search" ? new System.Text.Json.Nodes.JsonObject { ["query"] = "read_files", ["limit"] = 5 }
        : name.EndsWith("read_files", StringComparison.Ordinal) ? new System.Text.Json.Nodes.JsonObject
        { ["files"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["path"] = target }) }
        : schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("absolute_path", out _)
          ? new System.Text.Json.Nodes.JsonObject { ["absolute_path"] = InstalledNativeReadPath }
          : new System.Text.Json.Nodes.JsonObject { ["path"] = target };
      if (name == "tool_call") arguments = new System.Text.Json.Nodes.JsonObject
      {
        ["name"] = deferredRead,
        ["arguments"] = new System.Text.Json.Nodes.JsonObject
        { ["files"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["path"] = target }) }
      };
      if (name == "tool_search" && schema.TryGetProperty("properties", out var searchProperties) && !searchProperties.TryGetProperty("limit", out _)) arguments.Remove("limit");
      tools.Add(new System.Text.Json.Nodes.JsonObject
      {
        ["id"] = name == "tool_search" ? "call_native_search" : "call_native_read_host",
        ["type"] = "function",
        ["function"] = new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["arguments"] = arguments }
      });
    }
    else if (InstalledNativeMutationPath is not null && !messages.SelectMany(message => message.ToolCalls).Any(call => call.Name == InstalledNativeMutationTool || call.Name.EndsWith("." + InstalledNativeMutationTool, StringComparison.Ordinal)))
    {
      var tool = InstalledNativeMutationTool!;
      var nativeRead = tool is "Write" or "Edit" && !messages.SelectMany(message => message.ToolCalls).Any(call => call.Name == "Read");
      var member = nativeRead ? "Read" : tool;
      var name = availableTools.FirstOrDefault(name => name == member || name.EndsWith("." + member, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Native tool {member} was not offered.");
      var arguments = nativeRead
        ? new System.Text.Json.Nodes.JsonObject { ["file_path"] = InstalledNativeMutationPath }
        : tool == "Edit" ? new System.Text.Json.Nodes.JsonObject { ["file_path"] = InstalledNativeMutationPath, ["old_string"] = "OLD_VALUE", ["new_string"] = "NEW_VALUE" }
        : tool == "Write" ? new System.Text.Json.Nodes.JsonObject { ["file_path"] = InstalledNativeMutationPath, ["content"] = "FIRST\r\nNEW_VALUE\r\nLAST\r\n" }
        : new System.Text.Json.Nodes.JsonObject { ["input"] = "*** Begin Patch\n*** Update File: crlf-probe.txt\n@@\n-OLD_VALUE\n+NEW_VALUE\n*** End Patch" };
      tools.Add(new System.Text.Json.Nodes.JsonObject
      {
        ["id"] = nativeRead ? "call_native_read" : "call_native_mutation",
        ["type"] = "function",
        ["function"] = new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["arguments"] = arguments }
      });
    }
    var message = new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = tools.Count == 0 ? "INSTALLED_HARNESS_OK" : "", ["tool_calls"] = tools };
    var final = new System.Text.Json.Nodes.JsonObject
    {
      ["model"] = request.GetProperty("model").GetString(),
      ["message"] = message,
      ["done"] = true,
      ["done_reason"] = "stop",
      ["prompt_eval_count"] = 120,
      ["eval_count"] = 10,
      ["load_duration"] = 2_000_000,
      ["prompt_eval_duration"] = 30_000_000,
      ["eval_duration"] = 40_000_000
    };
    if (request.TryGetProperty("stream", out var streaming) && streaming.GetBoolean())
    {
      context.Response.ContentType = "application/x-ndjson";
      context.Response.SendChunked = true;
      foreach (var frame in new[] { new System.Text.Json.Nodes.JsonObject { ["message"] = new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["thinking"] = "Probe reasoning." }, ["done"] = false }, final })
      {
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n"), token);
        await context.Response.OutputStream.FlushAsync(token);
      }
      context.Response.Close();
    }
    else await WriteJsonAsync(context.Response, HttpStatusCode.OK, final, token);
  }

  // Only the provider boundary is scripted; the actual native executable,
  // Host, MCP bridge, approval path and browser run normally.
  private async Task HandleInstalledHarnessProbeAsync(HttpListenerContext context,
    JsonElement request, CancellationToken cancellationToken)
  {
    InstalledHarnessRequests.Enqueue(request.Clone());
    if (InstalledCodexJsonErrorProbe)
    {
      context.Response.ContentType = "application/json";
      await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
        "{\"error\":{\"code\":\"invalid_request_error\",\"message\":\"JSON_PROVIDER_FAILURE\"}}"), cancellationToken);
      context.Response.Close();
      return;
    }
    const string answer = "INSTALLED_HARNESS_OK";
    var model = request.GetProperty("model").GetString();
    context.Response.ContentType = InstalledCodexSseContentType == "" ? null : InstalledCodexSseContentType ?? "text/event-stream";
    context.Response.SendChunked = true;
    async Task FrameAsync(string? type, object data)
    {
      if (InstalledCodexJsonProbe)
      {
        if (type != "response.completed") return;
        var terminal = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(data))!["response"]!.DeepClone();
        terminal["object"] = "response";
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(terminal.ToJsonString()), cancellationToken);
        return;
      }
      var prefix = type is null ? "" : $"event: {type}\n";
      var bytes = Encoding.UTF8.GetBytes(prefix + "data: " + JsonSerializer.Serialize(data) + "\n\n");
      await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
      await context.Response.OutputStream.FlushAsync(cancellationToken);
    }
    switch (context.Request.Url!.AbsolutePath)
    {
      case "/v1/responses":
        if (InstalledCodexDisconnectProbe && InstalledHarnessRequests.Count == 5)
        {
          context.Response.Abort();
          return;
        }
        if (InstalledCodexReadProbe && InstalledHarnessRequests.Count <= 4)
        {
          var number = InstalledHarnessRequests.Count;
          var name = number == 1 ? "list_files" : number == 3 ? "search_text" : "read_file";
          var arguments = number switch
          {
            1 => JsonSerializer.Serialize(new { path = "", recursive = true }),
            3 => JsonSerializer.Serialize(new { path = "index.html", query = "script", recursive = false }),
            _ => JsonSerializer.Serialize(new { path = "index.html", lengthBytes = number == 2 ? 1000 : 2000, offsetBytes = number == 2 ? 0 : 150 })
          };
          var call = new { id = $"fc_read_probe_{number}", type = "function_call", call_id = $"call_read_probe_{number}", name, arguments };
          await FrameAsync("response.created", new { type = "response.created", response = new { id = $"resp_read_{number}", status = "in_progress" } });
          await FrameAsync("response.output_item.added", new { type = "response.output_item.added", output_index = 0, item = new { call.id, call.type, call.call_id, call.name, arguments = "" } });
          await FrameAsync("response.function_call_arguments.delta", new { type = "response.function_call_arguments.delta", item_id = call.id, output_index = 0, delta = arguments });
          await FrameAsync("response.function_call_arguments.done", new { type = "response.function_call_arguments.done", item_id = call.id, output_index = 0, arguments });
          await FrameAsync("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item = call });
          await FrameAsync("response.completed", new { type = "response.completed", response = new { id = $"resp_read_{number}", status = "completed", model, output = new[] { call }, usage = new { input_tokens = 100, output_tokens = 20, total_tokens = 120 } } });
          break;
        }
        if (InstalledCodexPatchProbe && Interlocked.Increment(ref _installedPatchCalls) == 1)
        {
          var patchPath = InstalledCodexOutsidePatchProbe ? "../outside-probe.txt" : "crlf-probe.txt";
          var move = InstalledCodexMoveOutsideProbe ? "*** Move to: ../escaped-move.txt\n" : "";
          var patch = $"*** Begin Patch\n*** Update File: {patchPath}\n{move}@@\n-OLD_VALUE\n+NEW_VALUE\n*** End Patch";
          var call = new { id = "fc_probe", type = "function_call", call_id = "call_patch_probe", name = "apply_patch", arguments = JsonSerializer.Serialize(new { input = patch }) };
          await FrameAsync("response.created", new { type = "response.created", response = new { id = "resp_patch", status = "in_progress" } });
          await FrameAsync("response.output_item.added", new
          {
            type = "response.output_item.added",
            output_index = 0,
            item = new { call.id, call.type, call.call_id, call.name, arguments = "" }
          });
          var middle = call.arguments.Length / 2;
          foreach (var delta in new[] { call.arguments[..middle], call.arguments[middle..] })
            await FrameAsync("response.function_call_arguments.delta", new { type = "response.function_call_arguments.delta", item_id = call.id, output_index = 0, delta });
          await FrameAsync("response.function_call_arguments.done", new { type = "response.function_call_arguments.done", item_id = call.id, output_index = 0, call.arguments });
          await FrameAsync("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item = call });
          await FrameAsync("response.completed", new { type = "response.completed", response = new { id = "resp_patch", status = "completed", model, output = new[] { call }, usage = new { input_tokens = 100, output_tokens = 20, total_tokens = 120 } } });
          break;
        }
        var item = new
        {
          id = "msg_probe",
          type = "message",
          status = "completed",
          role = "assistant",
          content = new[] { new { type = "output_text", text = answer, annotations = Array.Empty<object>() } }
        };
        await FrameAsync("response.created", new { type = "response.created", response = new { id = "resp_probe", status = "in_progress" } });
        await FrameAsync("response.output_item.added", new { type = "response.output_item.added", output_index = 0, item });
        await FrameAsync("response.content_part.added", new { type = "response.content_part.added", item_id = item.id, output_index = 0, content_index = 0, part = item.content[0] });
        await FrameAsync("response.output_text.delta", new { type = "response.output_text.delta", item_id = item.id, output_index = 0, content_index = 0, delta = answer });
        await FrameAsync("response.output_item.done", new { type = "response.output_item.done", output_index = 0, item });
        await FrameAsync("response.completed", new { type = "response.completed", response = new { id = "resp_probe", status = "completed", model, output = new[] { item }, usage = new { input_tokens = 100, output_tokens = 5, total_tokens = 105 } } });
        break;
      case "/v1/messages":
        await FrameAsync("message_start", new { type = "message_start", message = new { id = "msg_probe", type = "message", role = "assistant", content = Array.Empty<object>(), model, usage = new { input_tokens = 100, output_tokens = 0 } } });
        await FrameAsync("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        await FrameAsync("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = answer } });
        await FrameAsync("content_block_stop", new { type = "content_block_stop", index = 0 });
        await FrameAsync("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 5 } });
        await FrameAsync("message_stop", new { type = "message_stop" });
        break;
      default:
        await FrameAsync(null, new { id = "chatcmpl_probe", @object = "chat.completion.chunk", created = 1, model, choices = new[] { new { index = 0, delta = new { role = "assistant", content = answer }, finish_reason = (string?)null } } });
        await FrameAsync(null, new { id = "chatcmpl_probe", @object = "chat.completion.chunk", created = 1, model, choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 100, completion_tokens = 5, total_tokens = 105 } });
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), cancellationToken);
        break;
    }
    context.Response.Close();
  }
}
