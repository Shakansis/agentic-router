using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

if (args.Contains("--version", StringComparer.Ordinal))
{
  Console.WriteLine("2.1.234-fake (Claude Code)");
  return;
}

var runtime = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? string.Empty;
if (runtime.Length == 0)
{
  Environment.ExitCode = 2;
  return;
}
Directory.CreateDirectory(runtime);
var model = ValueAfter("--model") ?? string.Empty;
var configuredTools = (ValueAfter("--tools") ?? string.Empty)
  .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var configuredAllowedTools = (ValueAfter("--allowedTools") ?? string.Empty)
  .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var reportedTools = configuredTools.Concat(
  configuredAllowedTools.Where(tool => tool.StartsWith("mcp__", StringComparison.Ordinal))
).Distinct(StringComparer.Ordinal).ToArray();
var cwd = Environment.CurrentDirectory;
var baseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
var mcpConfig = ValueAfter("--mcp-config") ?? "{\"mcpServers\":{}}";
var resume = args.FirstOrDefault(value => value.StartsWith("--resume=", StringComparison.Ordinal));
var session = args.FirstOrDefault(value => value.StartsWith("--session-id=", StringComparison.Ordinal));
var nativeSessionId = resume?["--resume=".Length..]
  ?? session?["--session-id=".Length..]
  ?? Guid.NewGuid().ToString();

await WriteMarkerAsync("fake-claude-invocation.json", new
{
  args,
  processId = Environment.ProcessId,
  cwd,
  model,
  baseUrl,
  anthropicAuthToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"),
  anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
  claudeCodeUseBedrock = Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_BEDROCK"),
  claudeCodeUseVertex = Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_VERTEX"),
  claudeCodeUseFoundry = Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_FOUNDRY"),
  nonessentialTrafficDisabled = Environment.GetEnvironmentVariable(
    "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"
  ),
  apiTimeoutMs = Environment.GetEnvironmentVariable("API_TIMEOUT_MS"),
  streamIdleTimeoutMs = Environment.GetEnvironmentVariable("CLAUDE_STREAM_IDLE_TIMEOUT_MS"),
  claudeConfigDir = runtime,
  hostTokenConfigured = !string.IsNullOrWhiteSpace(
    Environment.GetEnvironmentVariable("AGENTIC_ROUTER_MCP_TOKEN")
  ),
  resumed = resume is not null,
  nativeSessionId,
  mcpConfig
});

var initializeLine = await Console.In.ReadLineAsync();
if (initializeLine is null)
{
  Environment.ExitCode = 2;
  return;
}
using var initialize = JsonDocument.Parse(initializeLine);
var initializeRequestId = initialize.RootElement.GetProperty("request_id").GetString();
await EmitAsync(new
{
  type = "control_response",
  response = new
  {
    subtype = "success",
    request_id = initializeRequestId,
    response = new { commands = Array.Empty<string>() }
  }
});

var promptLine = await Console.In.ReadLineAsync();
if (promptLine is null)
{
  Environment.ExitCode = 2;
  return;
}
using var promptDocument = JsonDocument.Parse(promptLine);
var prompt = promptDocument.RootElement.GetProperty("message").GetProperty("content").GetString()
  ?? string.Empty;
await File.WriteAllTextAsync(
  Path.Combine(runtime, "fake-claude-prompt.txt"),
  prompt,
  new UTF8Encoding(false)
);

if (prompt.Contains("malformed claude event", StringComparison.OrdinalIgnoreCase))
{
  Console.WriteLine("{not-json");
  await Console.Out.FlushAsync();
  return;
}

await EmitAsync(new
{
  type = "system",
  subtype = "init",
  cwd = prompt.Contains("claude wrong workspace", StringComparison.OrdinalIgnoreCase)
    ? Path.GetTempPath()
    : cwd,
  session_id = resume is null ? nativeSessionId : Guid.NewGuid().ToString(),
  model = prompt.Contains("claude wrong model", StringComparison.OrdinalIgnoreCase)
    ? "cloud-fallback"
    : model,
  tools = prompt.Contains("reduced claude inventory", StringComparison.OrdinalIgnoreCase)
    ? reportedTools.Where(tool => tool is not "Glob" and not "Grep" and not "Write").ToArray()
    : reportedTools,
  mcp_servers = new[] { new { name = "agentic_router", status = "connected" } },
  claude_code_version = "2.1.234-fake",
  permissionMode = "default",
  capabilities = new[] { "interrupt_receipt_v1" }
});

if (prompt.Contains("diagnostic overflow", StringComparison.OrdinalIgnoreCase))
{
  for (var index = 0; index < 260; index++)
  {
    await EmitAsync(new
    {
      type = "future_claude_event",
      detail = $"bounded-diagnostic-{index}",
      session_id = nativeSessionId
    });
  }
}

if (prompt.Contains("synthetic timeout", StringComparison.OrdinalIgnoreCase))
{
  await EmitAsync(new
  {
    type = "assistant",
    message = new
    {
      role = "assistant",
      model = "<synthetic>",
      stop_reason = "stop_sequence",
      usage = new { input_tokens = 0, output_tokens = 0 },
      content = new object[]
      {
        new { type = "text", text = "API Error: The operation timed out." }
      }
    },
    uuid = Guid.NewGuid().ToString(),
    session_id = nativeSessionId,
    parent_tool_use_id = (string?)null
  });
  return;
}

if (prompt.Contains("claude envelope storm", StringComparison.OrdinalIgnoreCase))
{
  for (var index = 0; index < 1_000; index++)
  {
    await EmitStreamDeltaAsync("thinking_delta", "thinking", "x");
    await EmitAsync(new
    {
      type = "system",
      subtype = "thinking_tokens",
      token_count = index + 1,
      session_id = nativeSessionId
    });
  }
}

if (
  prompt.Contains("long claude task", StringComparison.OrdinalIgnoreCase)
  || (
    string.Equals(model, "unused:latest", StringComparison.Ordinal)
    && prompt.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal)
  )
)
{
  await Task.Delay(Timeout.InfiniteTimeSpan);
  return;
}

await EmitStreamDeltaAsync(
  "thinking_delta",
  "thinking",
  $"Inspecting Claude Code workspace {new string('x', 128)}"
);
if (prompt.Contains("claude live context usage", StringComparison.OrdinalIgnoreCase))
{
  await Task.Delay(1_500);
}
var finalText = "Claude Code streamed with " + model;

if (prompt.Contains("Benchmark test: FS-CREATE-001", StringComparison.Ordinal))
{
  var directory = Path.Combine(cwd, "benchmark-data");
  Directory.CreateDirectory(directory);
  await File.WriteAllTextAsync(
    Path.Combine(directory, "result.txt"),
    "Agentic Router Benchmark\noperation=create\nresult=success",
    new UTF8Encoding(false)
  );
  await EmitToolAsync("claude-create", "Write", "created benchmark-data/result.txt");
  finalText = "Created the canonical benchmark file.";
}
else if (prompt.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal))
{
  _ = await File.ReadAllTextAsync(Path.Combine(cwd, "fixture", "read-primary.txt"));
  _ = await File.ReadAllTextAsync(Path.Combine(cwd, "fixture", "read-secondary.txt"));
  await EmitToolAsync("claude-read", "Read", "read fixture files");
  finalText = "codename=ORBIT-41\nverification-word=marigold";
}
else if (prompt.Contains("Benchmark test: FS-UPDATE-001", StringComparison.Ordinal))
{
  var path = Path.Combine(cwd, "fixture", "update.txt");
  var text = await File.ReadAllTextAsync(path);
  await File.WriteAllTextAsync(
    path,
    text.Replace("retries=2", "retries=3", StringComparison.Ordinal),
    new UTF8Encoding(false)
  );
  await EmitToolAsync("claude-edit", "Edit", "updated fixture/update.txt");
  finalText = "Updated retries exactly.";
}
else if (prompt.Contains("Benchmark test: FS-DELETE-001", StringComparison.Ordinal))
{
  var result = await InvokeHostToolAsync(
    "delete_paths",
    new { paths = new[] { "fixture/delete.txt" }, recursive = false }
  );
  await EmitToolAsync("claude-delete", "mcp__agentic_router__delete_paths", result);
  finalText = result;
}
else if (prompt.Contains("claude approval", StringComparison.OrdinalIgnoreCase))
{
  var approvalId = $"permission-{Guid.NewGuid():N}";
  var target = Path.Combine(cwd, "claude-approved.txt");
  await EmitAsync(new
  {
    type = "control_request",
    request_id = approvalId,
    request = new
    {
      subtype = "can_use_tool",
      tool_name = "Write",
      input = new { file_path = target, content = "approved" },
      tool_use_id = "claude-write-approval",
      description = "Write claude-approved.txt"
    }
  });
  var responseLine = await Console.In.ReadLineAsync();
  using var response = JsonDocument.Parse(responseLine!);
  var behavior = response.RootElement.GetProperty("response").GetProperty("response")
    .GetProperty("behavior").GetString();
  await WriteMarkerAsync("fake-claude-approval.json", new { approvalId, behavior });
  if (behavior == "allow")
  {
    await File.WriteAllTextAsync(target, "approved", new UTF8Encoding(false));
    finalText = "Approved write completed.";
  }
  else
  {
    finalText = "Write was denied.";
  }
}
else if (prompt.Contains("claude workspace recovery", StringComparison.OrdinalIgnoreCase))
{
  var outsideBehavior = await RequestPermissionAsync(
    "Read",
    new { file_path = Path.Combine(Path.GetTempPath(), "claude-outside.txt") },
    "Read outside the trusted workspace"
  );
  var insideBehavior = await RequestPermissionAsync(
    "Read",
    new { file_path = Path.Combine(cwd, "fixture", "read-primary.txt") },
    "Read inside the trusted workspace"
  );
  await WriteMarkerAsync(
    "fake-claude-workspace-recovery.json",
    new { outsideBehavior, insideBehavior }
  );
  finalText = outsideBehavior == "deny" && insideBehavior == "allow"
    ? "Recovered with a workspace-confined read."
    : "Workspace recovery failed.";
}
else if (prompt.Contains("claude nested batch step binding", StringComparison.OrdinalIgnoreCase))
{
  var plan = await InvokeHostToolAsync(
    "create_execution_plan",
    new
    {
      objective = "Create a two-file batch",
      steps = new[]
      {
        new
        {
          title = "Create both files",
          dependsOn = Array.Empty<int>()
        }
      }
    }
  );
  var result = await InvokeHostToolAsync(
    "create_files",
    new
    {
      files = new object[]
      {
        new
        {
          path = "claude-nested-binding/index.html",
          content = "<!doctype html><title>Recovered binding</title>\n",
          stepId = "step-1"
        },
        new
        {
          path = "claude-nested-binding/README.md",
          content = "# Recovered binding\n"
        }
      }
    }
  );
  await WriteMarkerAsync(
    "fake-claude-nested-binding.json",
    new
    {
      plan,
      result,
      htmlExists = File.Exists(Path.Combine(cwd, "claude-nested-binding", "index.html")),
      readmeExists = File.Exists(Path.Combine(cwd, "claude-nested-binding", "README.md"))
    }
  );
  finalText = result;
}
else if (prompt.Contains("claude slow watchdog tool recovery", StringComparison.OrdinalIgnoreCase))
{
  await Task.Delay(2600);
  var result = await InvokeHostToolAsync(
    "create_files",
    new
    {
      files = new[]
      {
        new
        {
          path = "slow-watchdog-recovery.txt",
          content = "successful Host tool activity reset the idle window\n"
        }
      }
    }
  );
  await Task.Delay(1700);
  finalText = $"Claude recovered after a slow warning. {result}";
}
else if (prompt.Contains("claude active slow watchdog", StringComparison.OrdinalIgnoreCase))
{
  for (var index = 0; index < 6; index++)
  {
    await Task.Delay(450);
    await EmitStreamDeltaAsync(
      "thinking_delta",
      "thinking",
      $"meaningful-progress-{index} {new string('x', 160)}"
    );
  }
  finalText = "Claude completed after sustained meaningful activity.";
}
else if (prompt.Contains("web host bridge claude code", StringComparison.OrdinalIgnoreCase))
{
  var web = await InvokeHostToolAsync(
    "web_search",
    new { query = "generic Host web capability" }
  );
  await WriteMarkerAsync(
    "fake-claude-host-web.json",
    new { succeeded = true, output = web }
  );
  finalText = "Claude used the Agentic Router Host web_search tool.";
}
else if (prompt.Contains("claude run process", StringComparison.OrdinalIgnoreCase))
{
  finalText = await InvokeHostToolAsync(
    "run_process",
    new
    {
      executable = "dotnet",
      arguments = new[] { "--version" },
      workingDirectory = ".",
      timeoutSeconds = 30
    }
  );
}
else if (prompt.Contains("claude git failure recovery", StringComparison.OrdinalIgnoreCase))
{
  finalText = await InvokeHostToolAsync(
    "git_create_commit",
    new
    {
      message = "test commit without staged files",
      commitWithoutValidation = true
    }
  );
}

await EmitStreamDeltaAsync("text_delta", "text", finalText);
await EmitAsync(new
{
  type = "assistant",
  message = new
  {
    role = "assistant",
    content = new object[] { new { type = "text", text = finalText } }
  },
  uuid = Guid.NewGuid().ToString(),
  session_id = nativeSessionId,
  parent_tool_use_id = (string?)null
});
await EmitAsync(new
{
  type = "future_claude_event",
  detail = "preserve native payload",
  session_id = nativeSessionId
});
await EmitAsync(new
{
  type = "result",
  subtype = "success",
  is_error = false,
  result = finalText,
  session_id = nativeSessionId,
  terminal_reason = "completed",
  usage = new
  {
    input_tokens = 120,
    cache_read_input_tokens = 10,
    cache_creation_input_tokens = 5,
    output_tokens = 20
  }
});
if (prompt.Contains("duplicate claude terminal", StringComparison.OrdinalIgnoreCase))
{
  await EmitAsync(new
  {
    type = "result",
    subtype = "error_during_execution",
    is_error = true,
    result = "duplicate",
    session_id = nativeSessionId,
    terminal_reason = "completed"
  });
}

string? ValueAfter(string flag)
{
  var index = Array.IndexOf(args, flag);
  return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

async Task<string?> RequestPermissionAsync(string tool, object input, string description)
{
  var approvalId = $"permission-{Guid.NewGuid():N}";
  await EmitAsync(new
  {
    type = "control_request",
    request_id = approvalId,
    request = new
    {
      subtype = "can_use_tool",
      tool_name = tool,
      input,
      tool_use_id = $"claude-{tool.ToLowerInvariant()}-{approvalId}",
      description
    }
  });
  var responseLine = await Console.In.ReadLineAsync();
  using var response = JsonDocument.Parse(responseLine!);
  return response.RootElement.GetProperty("response").GetProperty("response")
    .GetProperty("behavior").GetString();
}

async Task<string> InvokeHostToolAsync(string name, object arguments)
{
  using var config = JsonDocument.Parse(mcpConfig);
  var endpoint = config.RootElement.GetProperty("mcpServers").GetProperty("agentic_router")
    .GetProperty("url").GetString()!;
  var token = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_MCP_TOKEN");
  using var client = new HttpClient();
  client.DefaultRequestHeaders.Authorization = new("Bearer", token);
  using var initializeResponse = await client.PostAsJsonAsync(
    endpoint,
    new
    {
      jsonrpc = "2.0",
      id = 1,
      method = "initialize",
      @params = new
      {
        protocolVersion = "2025-03-26",
        capabilities = new { },
        clientInfo = new { name = "fake-claude", version = "1" }
      }
    }
  );
  initializeResponse.EnsureSuccessStatusCode();
  using var response = await client.PostAsJsonAsync(
    endpoint,
    new
    {
      jsonrpc = "2.0",
      id = 2,
      method = "tools/call",
      @params = new { name, arguments }
    }
  );
  response.EnsureSuccessStatusCode();
  using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  return result.RootElement.GetProperty("result").GetProperty("content")[0]
    .GetProperty("text").GetString() ?? string.Empty;
}

async Task EmitStreamDeltaAsync(string deltaType, string property, string value)
{
  var delta = property == "thinking"
    ? (object)new { type = deltaType, thinking = value }
    : new { type = deltaType, text = value };
  await EmitAsync(new
  {
    type = "stream_event",
    @event = new
    {
      type = "content_block_delta",
      index = property == "thinking" ? 0 : 1,
      delta
    },
    session_id = nativeSessionId,
    parent_tool_use_id = (string?)null
  });
}

async Task EmitToolAsync(string id, string name, string output)
{
  await EmitAsync(new
  {
    type = "assistant",
    message = new
    {
      role = "assistant",
      content = new object[] { new { type = "tool_use", id, name, input = new { } } }
    },
    uuid = Guid.NewGuid().ToString(),
    session_id = nativeSessionId,
    parent_tool_use_id = (string?)null
  });
  await EmitAsync(new
  {
    type = "user",
    message = new
    {
      role = "user",
      content = new object[]
      {
        new { type = "tool_result", tool_use_id = id, content = output, is_error = false }
      }
    },
    uuid = Guid.NewGuid().ToString(),
    session_id = nativeSessionId,
    parent_tool_use_id = (string?)null
  });
}

async Task EmitAsync(object value)
{
  await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
  await Console.Out.FlushAsync();
}

Task WriteMarkerAsync(string fileName, object value)
{
  return File.WriteAllTextAsync(
    Path.Combine(runtime, fileName),
    JsonSerializer.Serialize(value),
    new UTF8Encoding(false)
  );
}
