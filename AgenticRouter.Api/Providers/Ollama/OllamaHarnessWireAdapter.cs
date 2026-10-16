using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.Providers.Ollama;

// Translate the local compatibility transport only; Host policy and native tools remain unchanged.
internal sealed class OllamaHarnessWireAdapter(string protocol, JsonObject original, CodexApplyPatchWireAdapter? patchWire = null)
{
  private readonly Dictionary<string, (string Namespace, string Name)> _namespaces = new(StringComparer.Ordinal);
  private bool _toolSearch;

  public bool Streaming => original["stream"]?.GetValue<bool>() != false;

  public JsonObject Request(OllamaContextResolution runtime, bool disableThinking = false)
  {
    var messages = new JsonArray();
    var instructions = protocol == "/v1/responses" ? original["instructions"] : original["system"];
    if (instructions is not null) messages.Add(new JsonObject { ["role"] = "system", ["content"] = Text(instructions) });
    if (protocol == "/v1/responses")
    {
      if (original["input"] is JsonValue value && value.TryGetValue<string>(out var prompt))
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
      else foreach (var input in original["input"]?.AsArray() ?? [])
      {
        if (input?["type"]?.GetValue<string>() == "tool_search_output")
          messages.Add(new JsonObject { ["role"] = "tool", ["content"] = input["tools"]?.ToJsonString() ?? "[]", ["tool_call_id"] = input["call_id"]?.DeepClone() });
        else if (input?["type"]?.GetValue<string>() == "tool_search_call")
          messages.Add(new JsonObject
          {
            ["role"] = "assistant",
            ["content"] = "",
            ["tool_calls"] = new JsonArray(new JsonObject
            { ["id"] = input["call_id"]?.DeepClone(), ["function"] = new JsonObject { ["name"] = "tool_search", ["arguments"] = input["arguments"]?.DeepClone() ?? new JsonObject() } })
          });
        else if (input?["type"]?.GetValue<string>() == "function_call_output")
          messages.Add(new JsonObject { ["role"] = "tool", ["content"] = Text(input["output"]), ["tool_call_id"] = input["call_id"]?.DeepClone() });
        else if (input?["type"]?.GetValue<string>() == "function_call")
          messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(Call(input)) });
        else if (input?["type"]?.GetValue<string>() == "reasoning")
          messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["thinking"] = Text(input["summary"]) });
        else AddMessage(messages, input?.AsObject() ?? throw new JsonException("Invalid response input."));
      }
    }
    else foreach (var message in original["messages"]?.AsArray() ?? [])
      AddMessage(messages, message?.AsObject() ?? throw new JsonException("Invalid message."));

    var options = new JsonObject { ["num_ctx"] = runtime.EffectiveContextTokens, ["num_predict"] = runtime.OutputTokenLimit };
    if (runtime.Performance?.DraftTokens is int draft) options["draft_num_predict"] = draft;
    if (runtime.Performance?.BatchSize is int batch) options["num_batch"] = batch;
    foreach (var (source, target) in new[] { ("temperature", "temperature"), ("top_p", "top_p"), ("top_k", "top_k"), ("seed", "seed"), ("presence_penalty", "presence_penalty"), ("frequency_penalty", "frequency_penalty") })
      if (original[source] is not null) options[target] = original[source]!.DeepClone();
    var stop = original["stop_sequences"] ?? original["stop"];
    if (stop is not null) options["stop"] = stop is JsonArray ? stop.DeepClone() : new JsonArray(stop.DeepClone());
    var tools = new JsonArray();
    void AddTools(JsonArray definitions, string prefix = "")
    {
      foreach (var tool in definitions)
      {
        if (tool?["type"]?.GetValue<string>() == "namespace")
        {
          AddTools(tool["tools"]?.AsArray() ?? [], Qualify(prefix, tool["name"]!.GetValue<string>()));
          continue;
        }
        var function = protocol == "/v1/chat/completions" ? tool?["function"] : tool;
        var type = tool?["type"]?.GetValue<string>();
        var name = function?["name"]?.GetValue<string>();
        if (type == "tool_search") { name = "tool_search"; _toolSearch = true; }
        if (type == "web_search") name = "web_search";
        if (function is null || name is null) throw new JsonException($"Unsupported native compatibility tool type: {type}.");
        var qualified = Qualify(prefix, name);
        if (prefix.Length > 0) _namespaces[qualified] = (prefix, name);
        tools.Add(new JsonObject
        {
          ["type"] = "function",
          ["function"] = new JsonObject
          {
            ["name"] = qualified,
            ["description"] = function["description"]?.DeepClone(),
            ["parameters"] = (function["input_schema"] ?? function["parameters"])?.DeepClone() ?? (type == "web_search"
            ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("query") }
            : new JsonObject { ["type"] = "object" })
          }
        });
      }
    }
    AddTools(original["tools"]?.AsArray() ?? []);
    foreach (var item in original["input"] as JsonArray ?? [])
      if (item?["type"]?.GetValue<string>() == "tool_search_output") AddTools(item["tools"]?.AsArray() ?? []);
    var request = new JsonObject
    {
      ["model"] = original["model"]?.DeepClone(),
      ["messages"] = messages,
      ["stream"] = original["stream"]?.DeepClone() ?? true,
      ["options"] = options,
      ["keep_alive"] = runtime.KeepAlive,
      ["tools"] = tools
    };
    var effort = original["reasoning_effort"] ?? original["reasoning"]?["effort"] ?? original["output_config"]?["effort"];
    if (effort is not null) request["think"] = effort.GetValue<string>() == "none" ? JsonValue.Create(false) : effort.DeepClone();
    if (original["thinking"]?["type"]?.GetValue<string>() is "enabled" or "disabled")
      request["think"] = original["thinking"]!["type"]!.GetValue<string>() == "enabled";
    // The explicit Host turn selection takes precedence over harness defaults.
    if (disableThinking) request["think"] = false;
    var format = original["response_format"] ?? original["text"]?["format"];
    if (format?["type"]?.GetValue<string>() == "json_object") request["format"] = "json";
    else if (format?["type"]?.GetValue<string>() == "json_schema") request["format"] = (format["json_schema"]?["schema"] ?? format["schema"])?.DeepClone();
    return request;
  }

  private static void AddMessage(JsonArray messages, JsonObject source)
  {
    var message = new JsonObject { ["role"] = source["role"]?.DeepClone() ?? "user", ["content"] = "" };
    var content = new StringBuilder();
    var thinking = new StringBuilder();
    var calls = new JsonArray();
    var images = new JsonArray();
    if (source["content"] is JsonArray blocks)
    {
      foreach (var block in blocks)
      {
        switch (block?["type"]?.GetValue<string>())
        {
          case "text": case "input_text": case "output_text": content.Append(block["text"]?.GetValue<string>()); break;
          case "thinking": thinking.Append(block["thinking"]?.GetValue<string>()); break;
          case "redacted_thinking": break; // No visible text or reusable model reasoning is present.
          case "tool_use": calls.Add(Call(block)); break;
          case "tool_result":
            var result = new JsonObject { ["role"] = "tool", ["content"] = Text(block["content"]), ["tool_call_id"] = block["tool_use_id"]?.DeepClone() };
            if (block["content"] is JsonArray results)
            {
              var resultImages = new JsonArray();
              foreach (var toolImage in results.Where(item => item?["type"]?.GetValue<string>() == "image"))
                resultImages.Add(toolImage!["source"]?["data"]?.DeepClone() ?? throw new JsonException("Only inline local tool images are supported."));
              if (resultImages.Count > 0) result["images"] = resultImages;
            }
            messages.Add(result);
            break;
          case "image": images.Add(block["source"]?["data"]?.DeepClone() ?? throw new JsonException("Only inline local images are supported.")); break;
          case "image_url":
          case "input_image":
            var url = (block["image_url"] is JsonObject image ? image["url"] : block["image_url"])?.GetValue<string>()
              ?? throw new JsonException("Invalid inline image.");
            if (!url.StartsWith("data:image/", StringComparison.Ordinal) || !url.Contains(";base64,", StringComparison.Ordinal))
              throw new JsonException("Only inline local images are supported.");
            images.Add(url[(url.IndexOf(',') + 1)..]); break;
          default: throw new JsonException("Unsupported compatibility content block.");
        }
      }
    }
    else content.Append(Text(source["content"]));
    foreach (var call in source["tool_calls"]?.AsArray() ?? []) calls.Add(Call(call));
    message["content"] = content.ToString();
    if (thinking.Length > 0) message["thinking"] = thinking.ToString();
    if (source["reasoning"] is not null) message["thinking"] = source["reasoning"]!.DeepClone();
    if (calls.Count > 0) message["tool_calls"] = calls;
    if (images.Count > 0) message["images"] = images;
    if (source["tool_call_id"] is not null) message["tool_call_id"] = source["tool_call_id"]!.DeepClone();
    if (content.Length > 0 || calls.Count > 0 || images.Count > 0 || thinking.Length > 0 || source["content"] is not JsonArray)
      messages.Add(message);
  }

  private static JsonObject Call(JsonNode? node)
  {
    var function = node?["function"] ?? node ?? throw new JsonException("Invalid tool call.");
    var arguments = function["arguments"] ?? function["input"] ?? new JsonObject();
    if (arguments is JsonValue value && value.TryGetValue<string>(out var text)) arguments = JsonNode.Parse(text);
    return new JsonObject
    {
      ["id"] = (node?["id"] ?? node?["call_id"])?.DeepClone(),
      ["type"] = "function",
      ["function"] = new JsonObject
      {
        ["name"] = node?["namespace"] is { } scope
        ? Qualify(scope.GetValue<string>(), function["name"]!.GetValue<string>()) : function["name"]?.DeepClone(),
        ["arguments"] = arguments?.DeepClone()
      }
    };
  }

  private static string Qualify(string prefix, string name) => prefix.Length == 0 || name.StartsWith(prefix + ".", StringComparison.Ordinal)
    || name.StartsWith(prefix + "_", StringComparison.Ordinal) ? name : prefix + (name.StartsWith('_') ? "" : ".") + name;

  private static string Text(JsonNode? node) => node switch
  {
    null => "",
    JsonValue value when value.TryGetValue<string>(out var text) => text,
    JsonArray array => string.Concat(array.Select(item => Text(item?["text"] ?? item?["content"] ?? item?["output"]))),
    _ => node.ToJsonString()
  };

  public async Task ForwardAsync(Stream input, Stream output, Action<byte[]> observe, CancellationToken token)
  {
    var id = "msg_" + Guid.NewGuid().ToString("N");
    var model = original["model"]?.GetValue<string>() ?? "";
    var streaming = original["stream"]?.GetValue<bool>() != false;
    var text = new StringBuilder();
    var reasoning = new StringBuilder();
    var calls = new JsonArray();
    JsonObject? final = null;
    var blockIndex = -1;
    string? blockType = null;
    long sequence = 0;
    var responseIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
    async Task Emit(string type, JsonObject frame)
    {
      if (protocol != "/v1/chat/completions") frame["type"] = type;
      if (patchWire?.Transform(frame) == false) return;
      type = frame["type"]?.GetValue<string>() ?? type;
      if (protocol == "/v1/responses") frame["sequence_number"] = sequence++;
      var prefix = protocol == "/v1/chat/completions" ? "" : $"event: {type}\n";
      await output.WriteAsync(Encoding.UTF8.GetBytes(prefix + "data: " + frame.ToJsonString() + "\n\n"), token);
      await output.FlushAsync(token);
    }
    JsonObject Usage()
    {
      var usage = new JsonObject { ["input_tokens"] = final?["prompt_eval_count"]?.DeepClone() ?? 0, ["output_tokens"] = final?["eval_count"]?.DeepClone() ?? 0 };
      if (protocol == "/v1/responses") usage["total_tokens"] = (final?["prompt_eval_count"]?.GetValue<long>() ?? 0) + (final?["eval_count"]?.GetValue<long>() ?? 0);
      return usage;
    }
    JsonObject Response(string status) => new()
    {
      ["id"] = id,
      ["object"] = "response",
      ["model"] = model,
      ["status"] = status,
      ["output"] = ResponseOutput(id, text.ToString(), reasoning.ToString(), calls),
      ["usage"] = Usage()
    };
    JsonObject Chat(JsonObject delta, string? finish = null) => new()
    {
      ["id"] = id,
      ["object"] = "chat.completion.chunk",
      ["model"] = model,
      ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
    };
    async Task AnthropicBlock(string type)
    {
      if (blockType == type) return;
      if (blockType is not null) await Emit("content_block_stop", new() { ["index"] = blockIndex });
      blockType = type;
      blockIndex++;
      await Emit("content_block_start", new()
      {
        ["index"] = blockIndex,
        ["content_block"] = new JsonObject
        {
          ["type"] = type,
          [type == "thinking" ? "thinking" : "text"] = ""
        }
      });
    }
    if (streaming && protocol == "/v1/messages") await Emit("message_start", new()
    {
      ["message"] = new JsonObject
      { ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = new JsonArray(), ["stop_reason"] = null, ["usage"] = Usage() }
    });
    if (streaming && protocol == "/v1/responses") await Emit("response.created", new() { ["response"] = Response("in_progress") });
    using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: true);
    while (await reader.ReadLineAsync(token) is { } line)
    {
      if (line.Length == 0) continue;
      if (line.Length > 2_097_152) throw new JsonException("Native inference frame exceeds the transport limit.");
      var frame = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("Invalid native inference frame.");
      if (frame["error"] is not null) throw new JsonException("Native Ollama inference failed.");
      observe(Encoding.UTF8.GetBytes(line));
      final = frame;
      var message = frame["message"];
      foreach (var (field, buffer) in new[] { ("thinking", reasoning), ("content", text) })
      {
        var delta = message?[field]?.GetValue<string>();
        if (string.IsNullOrEmpty(delta)) continue;
        buffer.Append(delta);
        if (!streaming) continue;
        if (protocol == "/v1/messages")
        {
          await AnthropicBlock(field == "thinking" ? "thinking" : "text");
          await Emit("content_block_delta", new()
          {
            ["index"] = blockIndex,
            ["delta"] = new JsonObject
            { ["type"] = field == "thinking" ? "thinking_delta" : "text_delta", [field == "thinking" ? "thinking" : "text"] = delta }
          });
        }
        else if (protocol == "/v1/chat/completions") await Emit("chat.completion.chunk", Chat(new() { [field == "thinking" ? "reasoning" : "content"] = delta }));
        else
        {
          var itemId = id + (field == "thinking" ? "_reasoning" : "_text");
          if (!responseIndexes.TryGetValue(field, out var index))
          {
            index = responseIndexes.Count;
            responseIndexes[field] = index;
            await Emit("response.output_item.added", new()
            {
              ["output_index"] = index,
              ["item"] = field == "thinking"
              ? new JsonObject { ["id"] = itemId, ["type"] = "reasoning", ["summary"] = new JsonArray() }
              : new JsonObject { ["id"] = itemId, ["type"] = "message", ["role"] = "assistant", ["status"] = "in_progress", ["content"] = new JsonArray() }
            });
            if (field == "content") await Emit("response.content_part.added", new()
            {
              ["item_id"] = itemId,
              ["output_index"] = index,
              ["content_index"] = 0,
              ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() }
            });
          }
          await Emit(field == "thinking" ? "response.reasoning_text.delta" : "response.output_text.delta", new()
          { ["item_id"] = itemId, ["output_index"] = index, ["content_index"] = 0, ["delta"] = delta });
        }
      }
      foreach (var call in message?["tool_calls"]?.AsArray() ?? []) calls.Add(call?.DeepClone());
      if (frame["done"]?.GetValue<bool>() == true) break;
    }
    if (final?["done"]?.GetValue<bool>() != true) throw new JsonException("Native inference ended before completion.");
    var stopReason = calls.Count > 0 ? "tool_use" : final["done_reason"]?.GetValue<string>() == "length" ? "max_tokens" : "end_turn";
    if (protocol == "/v1/messages")
    {
      var content = new JsonArray();
      if (reasoning.Length > 0) content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.ToString(), ["signature"] = "" });
      if (text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = text.ToString() });
      if (streaming && blockType is not null) await Emit("content_block_stop", new() { ["index"] = blockIndex });
      foreach (var call in calls)
      {
        var function = call!["function"]!;
        var item = new JsonObject { ["type"] = "tool_use", ["id"] = CallId(call), ["name"] = function["name"]?.DeepClone(), ["input"] = function["arguments"]?.DeepClone() ?? new JsonObject() };
        content.Add(item);
        if (!streaming) continue;
        var start = item.DeepClone().AsObject(); start["input"] = new JsonObject();
        await Emit("content_block_start", new() { ["index"] = ++blockIndex, ["content_block"] = start });
        await Emit("content_block_delta", new()
        {
          ["index"] = blockIndex,
          ["delta"] = new JsonObject
          { ["type"] = "input_json_delta", ["partial_json"] = (item["input"] ?? new JsonObject()).ToJsonString() }
        });
        await Emit("content_block_stop", new() { ["index"] = blockIndex });
      }
      if (streaming)
      {
        await Emit("message_delta", new() { ["delta"] = new JsonObject { ["stop_reason"] = stopReason, ["stop_sequence"] = null }, ["usage"] = Usage() });
        await Emit("message_stop", new());
      }
      else await output.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["type"] = "message", ["role"] = "assistant", ["model"] = model, ["content"] = content, ["stop_reason"] = stopReason, ["usage"] = Usage() }.ToJsonString()), token);
    }
    else if (protocol == "/v1/chat/completions")
    {
      var toolCalls = new JsonArray();
      foreach (var call in calls) toolCalls.Add(new JsonObject
      {
        ["index"] = toolCalls.Count,
        ["id"] = CallId(call!),
        ["type"] = "function",
        ["function"] = new JsonObject
        { ["name"] = call!["function"]!["name"]?.DeepClone(), ["arguments"] = (call["function"]!["arguments"] ?? new JsonObject()).ToJsonString() }
      });
      var usage = new JsonObject
      {
        ["prompt_tokens"] = final["prompt_eval_count"]?.DeepClone() ?? 0,
        ["completion_tokens"] = final["eval_count"]?.DeepClone() ?? 0,
        ["total_tokens"] = (final["prompt_eval_count"]?.GetValue<long>() ?? 0) + (final["eval_count"]?.GetValue<long>() ?? 0)
      };
      var finish = calls.Count > 0 ? "tool_calls" : stopReason == "max_tokens" ? "length" : "stop";
      if (streaming)
      {
        if (toolCalls.Count > 0) await Emit("chat.completion.chunk", Chat(new() { ["tool_calls"] = toolCalls }));
        var completed = Chat(new(), finish); completed["usage"] = usage;
        await Emit("chat.completion.chunk", completed);
        await output.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), token);
      }
      else await output.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject
      {
        ["id"] = id,
        ["object"] = "chat.completion",
        ["model"] = model,
        ["choices"] = new JsonArray(new JsonObject
        { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString(), ["reasoning"] = reasoning.ToString(), ["tool_calls"] = toolCalls }, ["finish_reason"] = finish }),
        ["usage"] = usage
      }.ToJsonString()), token);
    }
    else
    {
      var response = Response(stopReason == "max_tokens" ? "incomplete" : "completed");
      if (stopReason == "max_tokens") response["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
      if (streaming)
      {
        foreach (var item in response["output"]!.AsArray())
        {
          var index = response["output"]!.AsArray().IndexOf(item);
          if (item!["type"]?.GetValue<string>() is "function_call" or "tool_search_call")
          {
            await Emit("response.output_item.added", new() { ["output_index"] = index, ["item"] = item.DeepClone() });
            if (item["type"]!.GetValue<string>() == "function_call")
              await Emit("response.function_call_arguments.done", new() { ["item_id"] = item["id"]!.DeepClone(), ["output_index"] = index, ["arguments"] = item["arguments"]!.DeepClone() });
          }
          else if (item["type"]?.GetValue<string>() == "message")
          {
            await Emit("response.output_text.done", new() { ["item_id"] = item["id"]!.DeepClone(), ["output_index"] = index, ["content_index"] = 0, ["text"] = text.ToString() });
            await Emit("response.content_part.done", new() { ["item_id"] = item["id"]!.DeepClone(), ["output_index"] = index, ["content_index"] = 0, ["part"] = item["content"]![0]!.DeepClone() });
          }
          await Emit("response.output_item.done", new() { ["output_index"] = index, ["item"] = item!.DeepClone() });
        }
        await Emit("response.completed", new() { ["response"] = response });
      }
      else
      {
        patchWire?.Transform(new JsonObject { ["response"] = response });
        await output.WriteAsync(Encoding.UTF8.GetBytes(response.ToJsonString()), token);
      }
    }
    await output.FlushAsync(token);
  }

  private static string CallId(JsonNode call)
  {
    var id = call["id"]?.GetValue<string>();
    if (!string.IsNullOrEmpty(id)) return id;
    id = "call_" + Guid.NewGuid().ToString("N"); call["id"] = id; return id;
  }

  private JsonArray ResponseOutput(string id, string text, string reasoning, JsonArray calls)
  {
    var output = new JsonArray();
    if (reasoning.Length > 0) output.Add(new JsonObject { ["id"] = id + "_reasoning", ["type"] = "reasoning", ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = reasoning }) });
    if (text.Length > 0) output.Add(new JsonObject
    {
      ["id"] = id + "_text",
      ["type"] = "message",
      ["role"] = "assistant",
      ["status"] = "completed",
      ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() })
    });
    foreach (var call in calls)
    {
      var name = call!["function"]!["name"]!.GetValue<string>();
      var arguments = call["function"]!["arguments"] ?? new JsonObject();
      var item = new JsonObject
      {
        ["id"] = CallId(call),
        ["call_id"] = CallId(call),
        ["type"] = "function_call",
        ["status"] = "completed",
        ["name"] = name,
        ["arguments"] = arguments.ToJsonString()
      };
      if (_namespaces.TryGetValue(name, out var scoped)) { item["namespace"] = scoped.Namespace; item["name"] = scoped.Name; }
      if (_toolSearch && name == "tool_search")
      {
        item["type"] = "tool_search_call"; item["execution"] = "client"; item["arguments"] = arguments.DeepClone(); item.Remove("name");
      }
      output.Add(item);
    }
    return output;
  }
}
