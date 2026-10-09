using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed partial class FakeOllamaServer
{
  public bool InstalledHarnessProbe { get; set; }
  public bool InstalledCodexPatchProbe { get; set; }
  public bool InstalledCodexOutsidePatchProbe { get; set; }
  public bool InstalledCodexMoveOutsideProbe { get; set; }
  private int _installedPatchCalls;
  public ConcurrentQueue<JsonElement> InstalledHarnessRequests { get; } = new();

  // Only the provider boundary is scripted; the actual native executable,
  // Host, MCP bridge, approval path and browser run normally.
  private async Task HandleInstalledHarnessProbeAsync(HttpListenerContext context,
    JsonElement request, CancellationToken cancellationToken)
  {
    InstalledHarnessRequests.Enqueue(request.Clone());
    const string answer = "INSTALLED_HARNESS_OK";
    var model = request.GetProperty("model").GetString();
    context.Response.ContentType = "text/event-stream";
    context.Response.SendChunked = true;
    async Task FrameAsync(string? type, object data)
    {
      var prefix = type is null ? "" : $"event: {type}\n";
      var bytes = Encoding.UTF8.GetBytes(prefix + "data: " + JsonSerializer.Serialize(data) + "\n\n");
      await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
      await context.Response.OutputStream.FlushAsync(cancellationToken);
    }
    switch (context.Request.Url!.AbsolutePath)
    {
      case "/v1/responses":
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
