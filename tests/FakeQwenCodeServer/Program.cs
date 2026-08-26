using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;

if (args.Contains("--version", StringComparer.Ordinal))
{
  Console.WriteLine("0.21.13-fake");
  return;
}

var serveIndex = Array.IndexOf(args, "serve");
var mcpConfigIndex = Array.IndexOf(args, "--mcp-config");
if (serveIndex != 0 || mcpConfigIndex >= 0 || args.Contains("--safe-mode", StringComparer.Ordinal))
{
  Environment.ExitCode = 2;
  return;
}

var port = ValueAfter("--port");
var workspace = ValueAfter("--workspace") ?? string.Empty;
if (!int.TryParse(port, out var parsedPort) || parsedPort <= 0 || workspace.Length == 0)
{
  Environment.ExitCode = 2;
  return;
}

var token = Environment.GetEnvironmentVariable("QWEN_SERVER_TOKEN") ?? string.Empty;
var runtime = Environment.GetEnvironmentVariable("QWEN_HOME") ?? string.Empty;
Directory.CreateDirectory(runtime);
await File.WriteAllTextAsync(
  Path.Combine(runtime, "fake-qwen-process-id.txt"),
  Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
);
var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
builder.WebHost.UseUrls($"http://127.0.0.1:{parsedPort}");
var app = builder.Build();
var sessions = new ConcurrentDictionary<string, FakeSession>(StringComparer.Ordinal);
var permissions = new ConcurrentDictionary<string, PendingPermission>(StringComparer.Ordinal);
var sessionNumber = 0;
var startupResponsesRemaining = 1;

app.Use(async (context, next) =>
{
  if (!string.Equals(
    context.Request.Headers.Authorization.ToString(),
    $"Bearer {token}",
    StringComparison.Ordinal
  ))
  {
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    return;
  }
  await next();
});

app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapGet("/capabilities", () => Results.Json(new
{
  v = 1,
  protocolVersions = new { current = "v1", supported = new[] { "v1" } },
  qwenCodeVersion = "0.21.13-fake",
  mode = "http-bridge",
  features = new[]
  {
    "session_create",
    "session_scope_override",
    "session_prompt",
    "session_cancel",
    "session_events",
    "typed_event_schema",
    "session_set_model",
    "session_permission_vote",
    "session_context",
    "session_close",
    "workspace_providers",
    "require_auth"
  },
  workspaceCwd = workspace,
  policy = new { permission = "first-responder" }
}));

app.MapPost("/session", async (HttpContext context) =>
{
  if (Interlocked.Exchange(ref startupResponsesRemaining, 0) == 1)
  {
    return Results.Json(
      new { error = "Daemon runtime is still starting", code = "daemon_runtime_starting" },
      statusCode: StatusCodes.Status503ServiceUnavailable
    );
  }
  var settingsPath = Path.Combine(runtime, "settings.json");
  using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
  var selectedAuthType = settings.RootElement
    .GetProperty("security")
    .GetProperty("auth")
    .GetProperty("selectedType")
    .GetString();
  var providerEnvKey = settings.RootElement
    .GetProperty("modelProviders")
    .GetProperty("openai")[0]
    .GetProperty("envKey")
    .GetString();
  var providerCredentialConfigured = !string.IsNullOrWhiteSpace(providerEnvKey)
    && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(providerEnvKey));
  if (!string.Equals(selectedAuthType, "openai", StringComparison.Ordinal)
    || !providerCredentialConfigured)
  {
    return Results.Json(
      new
      {
        error = "Authentication required: Use Qwen Code CLI to authenticate first.",
        code = -32000
      },
      statusCode: StatusCodes.Status500InternalServerError
    );
  }
  using var body = await JsonDocument.ParseAsync(context.Request.Body);
  var cwd = body.RootElement.GetProperty("cwd").GetString() ?? string.Empty;
  var scope = body.RootElement.GetProperty("sessionScope").GetString();
  if (!string.Equals(Path.GetFullPath(cwd), Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase)
    || !string.Equals(scope, "thread", StringComparison.Ordinal))
  {
    return Results.BadRequest(new { code = "workspace_mismatch" });
  }
  var id = $"qwen-session-{Interlocked.Increment(ref sessionNumber)}";
  var requestedClientId = context.Request.Headers["X-Qwen-Client-Id"].ToString();
  var clientId = $"client_{Guid.NewGuid():D}";
  var model = settings.RootElement.GetProperty("model").GetProperty("name").GetString()
    ?? string.Empty;
  var modelRouteId = $"$qwen-route|openai|{model}";
  var baseUrl = settings.RootElement.GetProperty("modelProviders")
    .GetProperty("openai")[0]
    .GetProperty("baseUrl")
    .GetString();
  var session = new FakeSession(id, clientId, cwd)
  {
    Model = model,
    ModelRouteId = modelRouteId
  };
  sessions[id] = session;
  await WriteMarkerAsync("fake-qwen-session.json", new
  {
    sessionId = id,
    requestedClientId,
    assignedClientId = clientId,
    cwd,
    sessionScope = scope,
    tokenConfigured = token.Length > 0,
    selectedAuthType,
    providerEnvKey,
    providerCredentialConfigured,
    args,
    qwenHome = runtime,
    systemSettingsPath = Environment.GetEnvironmentVariable("QWEN_CODE_SYSTEM_SETTINGS_PATH")
  });
  await WriteMarkerAsync("fake-qwen-model.json", new
  {
    sessionId = id,
    clientId,
    model,
    modelRouteId,
    source = "initial-settings",
    settings = await File.ReadAllTextAsync(settingsPath)
  });
  return Results.Json(new { sessionId = id, workspaceCwd = cwd, attached = false, clientId });
});

app.MapPost("/session/{sessionId}/model", async (string sessionId, HttpContext context) =>
{
  if (!sessions.TryGetValue(sessionId, out var session))
  {
    return Results.NotFound();
  }
  if (!HasRegisteredClient(context, session))
  {
    return InvalidClient(session, context);
  }
  return Results.Conflict(new
  {
    code = "unexpected_model_switch",
    error = "The fake forbids redundant model mutation after session creation."
  });
});

app.MapGet("/workspace/providers", async () =>
{
  var session = sessions.Values.FirstOrDefault();
  if (session is null)
  {
    return Results.Json(new
    {
      v = 1,
      workspaceCwd = workspace,
      initialized = false,
      providers = Array.Empty<object>()
    });
  }
  using var settings = JsonDocument.Parse(
    await File.ReadAllTextAsync(Path.Combine(runtime, "settings.json"))
  );
  var baseUrl = settings.RootElement.GetProperty("modelProviders")
    .GetProperty("openai")[0]
    .GetProperty("baseUrl")
    .GetString();
  return Results.Json(new
  {
    v = 1,
    workspaceCwd = workspace,
    initialized = true,
    current = new
    {
      authType = "openai",
      modelId = session.ModelRouteId,
      baseUrl
    },
    providers = new[]
    {
      new
      {
        kind = "model_provider",
        status = "ok",
        authType = "openai",
        current = true,
        models = new[]
        {
          new
          {
            modelId = session.ModelRouteId,
            baseModelId = session.Model,
            name = session.Model,
            baseUrl,
            isCurrent = true,
            isRuntime = false
          }
        }
      }
    }
  });
});

app.MapGet("/session/{sessionId}/context", (string sessionId, HttpContext context) =>
{
  if (!sessions.TryGetValue(sessionId, out var session))
  {
    return Results.NotFound();
  }
  if (!HasRegisteredClient(context, session))
  {
    return InvalidClient(session, context);
  }
  return Results.Json(new
  {
    v = 1,
    sessionId,
    workspaceCwd = session.Cwd,
    state = new
    {
      models = new
      {
        currentModelId = session.ModelRouteId,
        availableModels = new[] { new { modelId = session.ModelRouteId, name = session.Model } }
      },
      modes = new { },
      configOptions = Array.Empty<object>()
    }
  });
});

app.MapGet("/session/{sessionId}/events", async (string sessionId, HttpContext context) =>
{
  if (!sessions.TryGetValue(sessionId, out var session))
  {
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    return;
  }
  if (!HasRegisteredClient(context, session))
  {
    context.Response.StatusCode = StatusCodes.Status400BadRequest;
    await context.Response.WriteAsJsonAsync(InvalidClientBody(session, context));
    return;
  }
  var subscriberId = Guid.NewGuid();
  var channel = Channel.CreateUnbounded<string>();
  session.Subscribers[subscriberId] = channel;
  context.Response.ContentType = "text/event-stream";
  try
  {
    await context.Response.StartAsync(context.RequestAborted);
    await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
    await foreach (var frame in channel.Reader.ReadAllAsync(context.RequestAborted))
    {
      await context.Response.WriteAsync($"data: {frame}\n\n", context.RequestAborted);
      await context.Response.Body.FlushAsync(context.RequestAborted);
    }
  }
  finally
  {
    session.Subscribers.TryRemove(subscriberId, out _);
  }
});

app.MapPost("/session/{sessionId}/prompt", async (string sessionId, HttpContext context) =>
{
  if (!sessions.TryGetValue(sessionId, out var session))
  {
    return Results.NotFound();
  }
  if (!HasRegisteredClient(context, session))
  {
    return InvalidClient(session, context);
  }
  using var body = await JsonDocument.ParseAsync(context.Request.Body);
  var text = body.RootElement.GetProperty("prompt")[0].GetProperty("text").GetString() ?? string.Empty;
  var promptId = $"{sessionId}########{Interlocked.Increment(ref session.PromptNumber)}";
  session.ActivePromptId = promptId;
  await WriteMarkerAsync("fake-qwen-prompt.json", new
  {
    sessionId,
    clientId = context.Request.Headers["X-Qwen-Client-Id"].ToString(),
    promptId,
    model = session.Model,
    cwd = session.Cwd,
    text
  });
  context.Response.StatusCode = StatusCodes.Status202Accepted;
  await context.Response.WriteAsJsonAsync(new { promptId, lastEventId = session.EventId });
  await context.Response.CompleteAsync();

  if (text.Contains("malformed qwen code", StringComparison.Ordinal))
  {
    await BroadcastRawAsync(session, "{bad-json");
    return Results.Empty;
  }
  if (text.Contains("crash qwen code", StringComparison.Ordinal))
  {
    Environment.Exit(23);
  }
  if (text.Contains("long qwen code", StringComparison.Ordinal))
  {
    await EmitSessionUpdateAsync(session, new
    {
      sessionUpdate = "agent_thought_chunk",
      content = new { type = "text", text = "Inspecting long Qwen Code task." }
    });
    return Results.Empty;
  }
  if (text.Contains("permission qwen code", StringComparison.Ordinal))
  {
    const string requestId = "qwen-permission-1";
    var unmappableNativeExtra = text.Contains(
      "unmappable permission qwen code",
      StringComparison.Ordinal
    );
    var outsideWorkspace = text.Contains(
      "outside",
      StringComparison.Ordinal
    );
    var readOnlyFile = text.Contains(
      "read permission qwen code",
      StringComparison.Ordinal
    );
    permissions[requestId] = new PendingPermission(
      sessionId,
      promptId,
      unmappableNativeExtra || outsideWorkspace || readOnlyFile
    );
    await EmitAsync(session, "permission_request", new
    {
      requestId,
      sessionId,
      toolCall = unmappableNativeExtra
        ? (object)new
        {
          toolCallId = "qwen-computer-use-permission",
          title = "List Windows applications",
          kind = "other",
          _meta = new { toolName = "computer_use__list_apps" }
        }
        : readOnlyFile
          ? new
          {
            toolCallId = "qwen-read-permission",
            title = "Read workspace file",
            kind = "read",
            locations = new[]
            {
              new
              {
                path = outsideWorkspace ? "../outside.txt" : "README.md",
                line = 1
              }
            },
            _meta = new { toolName = "read_file" }
          }
          : new
          {
            toolCallId = "qwen-edit-permission",
            title = "Edit README.md",
            kind = "edit",
            locations = new[]
          {
            new
            {
              path = outsideWorkspace ? "../outside.txt" : "README.md",
              line = 1
            }
          },
            _meta = new { toolName = "edit" }
          },
      options = new[]
      {
        new { optionId = "proceed_once", name = "Allow once", kind = "allow_once" },
        new { optionId = "reject_once", name = "Reject", kind = "reject_once" }
      }
    });
    return Results.Empty;
  }
  if (text.Contains("host bridge qwen code", StringComparison.Ordinal))
  {
    if (text.Contains("plan host bridge qwen code", StringComparison.Ordinal))
    {
      var plan = await InvokeHostToolAsync(
        "create_execution_plan",
        new
        {
          objective = "Prove Qwen Code plan bridging",
          steps = new[]
          {
            new { title = "Run the first Host process", dependsOn = Array.Empty<int>() },
            new { title = "Run the second Host process", dependsOn = new[] { 1 } }
          }
        }
      );
      var unboundAction = await InvokeHostToolAsync(
        "run_process",
        new
        {
          executable = "dotnet",
          arguments = new[] { "--version" },
          workingDirectory = ".",
          timeoutSeconds = 30
        }
      );
      var firstAction = await InvokeHostToolAsync(
        "run_process",
        new
        {
          executable = "dotnet",
          arguments = new[] { "--version" },
          workingDirectory = ".",
          timeoutSeconds = 30,
          stepId = ActionablePlanStepId(unboundAction)
        }
      );
      var secondAction = await InvokeHostToolAsync(
        "run_process",
        new
        {
          executable = "dotnet",
          arguments = new[] { "--version" },
          workingDirectory = ".",
          timeoutSeconds = 30,
          stepId = ActionablePlanStepId(firstAction)
        }
      );
      await WriteMarkerAsync(
        "fake-qwen-host-plan.json",
        new
        {
          succeeded = plan.Succeeded && !unboundAction.Succeeded
            && firstAction.Succeeded && secondAction.Succeeded,
          plan = new { succeeded = plan.Succeeded, output = plan.Output },
          unboundAction = new { succeeded = unboundAction.Succeeded, output = unboundAction.Output },
          firstAction = new { succeeded = firstAction.Succeeded, output = firstAction.Output },
          secondAction = new { succeeded = secondAction.Succeeded, output = secondAction.Output }
        }
      );
      await CompleteAsync(session, promptId);
      return Results.Empty;
    }
    if (text.Contains("boundary host bridge qwen code", StringComparison.Ordinal))
    {
      var rejected = await InvokeHostToolAsync(
        "create_files",
        new
        {
          files = new[] { new { path = "../qwen-escape.txt", content = "must not escape" } }
        }
      );
      var recovered = await InvokeHostToolAsync(
        "create_files",
        new
        {
          files = new[] { new { path = "qwen-recovered.txt", content = "recovered safely" } }
        }
      );
      await WriteMarkerAsync("fake-qwen-host-boundary.json", new
      {
        rejected = new { rejected.Succeeded, rejected.Output },
        recovered = new { recovered.Succeeded, recovered.Output }
      });
      await CompleteAsync(session, promptId);
      return Results.Empty;
    }
    if (text.Contains("parity host bridge qwen code", StringComparison.Ordinal))
    {
      var steps = new List<McpStepResult>();
      await RunHostStepAsync(steps, "create_directory", new { path = "qwen-parity" });
      await RunHostStepAsync(steps, "create_files", new
      {
        files = new[] { new { path = "qwen-parity/item.txt", content = "parity" } }
      });
      await RunHostStepAsync(steps, "get_file_info", new { path = "qwen-parity/item.txt" });
      await RunHostStepAsync(steps, "run_process", new
      {
        executable = "dotnet",
        arguments = new[] { "--version" },
        workingDirectory = ".",
        timeoutSeconds = 30
      });
      await RunHostStepAsync(steps, "git_status", new { });
      await RunHostStepAsync(steps, "delete_paths", new
      {
        paths = new[] { "qwen-parity" },
        recursive = true
      });
      await RunHostStepAsync(steps, "create_files", new
      {
        files = new[] { new { path = "qwen-parity-complete.txt", content = "complete" } }
      });
      await WriteMarkerAsync("fake-qwen-host-parity.json", new
      {
        steps,
        deleted = !Directory.Exists(Path.Combine(session.Cwd, "qwen-parity")),
        finalObserved = File.Exists(Path.Combine(session.Cwd, "qwen-parity-complete.txt"))
      });
      await CompleteAsync(session, promptId);
      return Results.Empty;
    }
    var relativePath = text.Contains("ask host bridge qwen code", StringComparison.Ordinal)
      ? "qwen-host-ask.txt"
      : "qwen-host-auto.txt";
    var result = await InvokeHostToolAsync(
      "create_files",
      new
      {
        files = new[]
        {
          new
          {
            path = relativePath,
            content = $"Qwen Code Host bridge created {relativePath}."
          }
        }
      }
    );
    await WriteMarkerAsync("fake-qwen-host-tool.json", new
    {
      tool = "create_files",
      relativePath,
      succeeded = result.Succeeded,
      output = result.Output,
      observed = File.Exists(Path.Combine(session.Cwd, relativePath))
    });
    await CompleteAsync(session, promptId);
    return Results.Empty;
  }
  if (text.Contains("unexpected qwen code", StringComparison.Ordinal))
  {
    for (var index = 0; index < 64; index++)
    {
      await EmitAsync(session, "future_qwen_event", new
      {
        sessionId,
        futureField = "preserve-me",
        index
      });
    }
  }
  if (text.Contains("storm qwen code", StringComparison.Ordinal))
  {
    await CompleteStormAsync(session, promptId);
    return Results.Empty;
  }
  if (text.Contains("empty qwen code", StringComparison.Ordinal))
  {
    await EmitSessionUpdateAsync(session, new
    {
      sessionUpdate = "session_info_update",
      title = "No assistant output"
    });
    await EmitAsync(session, "turn_complete", new
    {
      sessionId = session.Id,
      promptId,
      stopReason = "end_turn"
    });
    return Results.Empty;
  }
  if (
    string.Equals(session.Model, "unused:latest", StringComparison.Ordinal)
    && text.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal)
  )
  {
    await EmitSessionUpdateAsync(session, new
    {
      sessionUpdate = "agent_thought_chunk",
      content = new { type = "text", text = "Qwen benchmark read remains in progress." }
    });
    return Results.Empty;
  }
  if (text.Contains("Benchmark test: FS-", StringComparison.Ordinal))
  {
    await CompleteBenchmarkAsync(session, promptId, text);
    return Results.Empty;
  }
  await CompleteAsync(session, promptId);
  return Results.Empty;
});

app.MapPost("/permission/{requestId}", async (string requestId, HttpContext context) =>
{
  using var body = await JsonDocument.ParseAsync(context.Request.Body);
  if (!permissions.TryRemove(requestId, out var pending)
    || !sessions.TryGetValue(pending.SessionId, out var session))
  {
    return Results.NotFound();
  }
  if (!HasRegisteredClient(context, session))
  {
    return InvalidClient(session, context);
  }
  var outcome = body.RootElement.GetProperty("outcome");
  var selected = string.Equals(outcome.GetProperty("outcome").GetString(), "selected", StringComparison.Ordinal)
    && string.Equals(outcome.GetProperty("optionId").GetString(), "proceed_once", StringComparison.Ordinal);
  await EmitAsync(session, "permission_resolved", new
  {
    requestId,
    sessionId = session.Id,
    outcome = selected ? "selected" : "cancelled"
  });
  if (selected)
  {
    await CompleteAsync(session, pending.PromptId);
  }
  else if (pending.ContinueAfterRejection)
  {
    await CompleteAsync(session, pending.PromptId);
  }
  else
  {
    await EmitAsync(session, "turn_error", new
    {
      sessionId = session.Id,
      promptId = pending.PromptId,
      message = "Permission rejected."
    });
  }
  return Results.Json(new { });
});

app.MapPost("/session/{sessionId}/cancel", async (string sessionId, HttpContext context) =>
{
  if (!sessions.TryGetValue(sessionId, out var session))
  {
    return Results.NotFound();
  }
  if (!HasRegisteredClient(context, session))
  {
    return InvalidClient(session, context);
  }
  await EmitAsync(session, "turn_complete", new
  {
    sessionId,
    promptId = session.ActivePromptId,
    stopReason = "cancelled"
  });
  return Results.NoContent();
});

app.MapDelete("/session/{sessionId}", (string sessionId, HttpContext context) =>
{
  if (sessions.TryGetValue(sessionId, out var session)
    && !HasRegisteredClient(context, session))
  {
    return InvalidClient(session, context);
  }
  sessions.TryRemove(sessionId, out _);
  return Results.NoContent();
});

await app.RunAsync();

string? ValueAfter(string flag)
{
  var index = Array.IndexOf(args, flag);
  return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

async Task CompleteAsync(FakeSession session, string promptId)
{
  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "agent_thought_chunk",
    content = new { type = "text", text = "Inspecting Qwen Code workspace." }
  });
  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "tool_call",
    toolCallId = "qwen-read-1",
    title = "Read README.md",
    kind = "read",
    status = "in_progress",
    rawInput = new { file_path = "README.md" },
    locations = new[] { new { path = "README.md", line = 1 } },
    _meta = new { toolName = "read_file" }
  });
  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "tool_call_update",
    toolCallId = "qwen-read-1",
    title = "Read README.md",
    kind = "read",
    status = "completed",
    rawOutput = "ok",
    _meta = new { toolName = "read_file" }
  });
  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "usage_update",
    used = 4321,
    size = 131072
  });
  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "agent_message_chunk",
    content = new { type = "text", text = "Qwen Code streamed with qwen3.8:27b-gpu0" }
  });
  await EmitAsync(session, "turn_complete", new
  {
    sessionId = session.Id,
    promptId,
    stopReason = "end_turn"
  });
}

async Task CompleteBenchmarkAsync(FakeSession session, string promptId, string request)
{
  var report = "Benchmark completed.";
  if (request.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal))
  {
    await RunBenchmarkToolAsync(
      session,
      "qwen-benchmark-read-primary",
      "read_file",
      new { path = "fixture/read-primary.txt" },
      "fixture/read-primary.txt"
    );
    await RunBenchmarkToolAsync(
      session,
      "qwen-benchmark-read-secondary",
      "read_file",
      new { path = "fixture/read-secondary.txt" },
      "fixture/read-secondary.txt"
    );
    report = "codename=ORBIT-41\nverification-word=marigold";
  }
  else if (request.Contains("Benchmark test: FS-UPDATE-001", StringComparison.Ordinal))
  {
    await RunBenchmarkToolAsync(
      session,
      "qwen-benchmark-update",
      "replace_text",
      new
      {
        path = "fixture/update.txt",
        oldText = "retries=2",
        newText = "retries=3",
        replaceAll = false
      },
      "fixture/update.txt"
    );
  }
  else if (request.Contains("Benchmark test: FS-DELETE-001", StringComparison.Ordinal))
  {
    await RunBenchmarkToolAsync(
      session,
      "qwen-benchmark-delete",
      "delete_paths",
      new { paths = new[] { "fixture/delete.txt" }, recursive = false },
      "fixture/delete.txt"
    );
  }
  else
  {
    await RunBenchmarkToolAsync(
      session,
      "qwen-benchmark-create",
      "create_files",
      new
      {
        files = new[]
        {
          new
          {
            path = "benchmark-data/result.txt",
            content = "Agentic Router Benchmark\noperation=create\nresult=success"
          }
        }
      },
      "benchmark-data/result.txt"
    );
  }

  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "agent_message_chunk",
    content = new { type = "text", text = report }
  });
  await EmitAsync(session, "turn_complete", new
  {
    sessionId = session.Id,
    promptId,
    stopReason = "end_turn"
  });
}

async Task RunBenchmarkToolAsync(
  FakeSession session,
  string toolCallId,
  string tool,
  object arguments,
  string path
)
{
  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "tool_call",
    toolCallId,
    title = $"Benchmark {tool}",
    kind = tool == "read_file" ? "read" : "edit",
    status = "in_progress",
    rawInput = arguments,
    locations = new[] { new { path, line = 1 } },
    _meta = new { toolName = tool }
  });
  McpToolResult result;
  if (tool == "read_file")
  {
    result = await InvokeHostToolAsync(tool, arguments);
  }
  else
  {
    result = await InvokeHostToolAsync(tool, arguments);
  }
  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "tool_call_update",
    toolCallId,
    title = $"Benchmark {tool}",
    kind = tool == "read_file" ? "read" : "edit",
    status = result.Succeeded ? "completed" : "failed",
    rawOutput = result.Output,
    _meta = new { toolName = tool }
  });
  if (!result.Succeeded)
  {
    throw new InvalidOperationException(result.Output);
  }
}

async Task CompleteStormAsync(FakeSession session, string promptId)
{
  for (var index = 0; index < 600; index++)
  {
    await EmitSessionUpdateAsync(session, new
    {
      sessionUpdate = "available_commands_update",
      availableCommands = new[]
      {
        new { name = $"command-{index}", description = "Qwen Code command metadata." }
      }
    });
  }

  const string reasoning = "Qwen Code inspected the workspace using its real nested SSE envelope. ";
  foreach (var character in reasoning)
  {
    await EmitSessionUpdateAsync(session, new
    {
      sessionUpdate = "agent_thought_chunk",
      content = new { type = "text", text = character.ToString() }
    });
  }

  var answer = string.Concat(Enumerable.Repeat(
    "Qwen Code returned a useful bounded response. ",
    32
  ));
  foreach (var character in answer)
  {
    await EmitSessionUpdateAsync(session, new
    {
      sessionUpdate = "agent_message_chunk",
      content = new { type = "text", text = character.ToString() }
    });
  }

  await EmitSessionUpdateAsync(session, new
  {
    sessionUpdate = "usage_update",
    used = 5432,
    size = 131072
  });
  await EmitAsync(session, "turn_complete", new
  {
    sessionId = session.Id,
    promptId,
    stopReason = "end_turn"
  });
}

Task EmitSessionUpdateAsync(FakeSession session, object update)
{
  return EmitAsync(session, "session_update", new { sessionId = session.Id, update });
}

async Task EmitAsync(FakeSession session, string type, object data)
{
  var id = Interlocked.Increment(ref session.EventId);
  await BroadcastRawAsync(session, JsonSerializer.Serialize(new { id, v = 1, type, data }));
}

async Task BroadcastRawAsync(FakeSession session, string payload)
{
  foreach (var subscriber in session.Subscribers.Values)
  {
    await subscriber.Writer.WriteAsync(payload);
  }
}

async Task WriteMarkerAsync(string name, object value)
{
  if (runtime.Length == 0)
  {
    return;
  }
  Directory.CreateDirectory(runtime);
  await File.WriteAllTextAsync(
    Path.Combine(runtime, name),
    JsonSerializer.Serialize(value)
  );
}

string ActionablePlanStepId(McpToolResult result)
{
  var lines = result.Output.Split('\n');
  var marker = Array.FindIndex(lines, line => line.Trim() == "HOST_OWNED_PLAN_STATE");
  if (marker < 0 || marker + 1 >= lines.Length)
  {
    throw new InvalidOperationException("The Host action result omitted authoritative plan state.");
  }
  using var document = JsonDocument.Parse(lines[marker + 1]);
  var ids = document.RootElement.TryGetProperty("actionableStepIds", out var camelIds)
    ? camelIds
    : document.RootElement.GetProperty("ActionableStepIds");
  return ids[0].GetString()
    ?? throw new InvalidOperationException("The Host action result omitted its next actionable step ID.");
}

async Task<McpToolResult> InvokeHostToolAsync(string tool, object arguments)
{
  var qwenHome = Environment.GetEnvironmentVariable("QWEN_HOME")
    ?? throw new InvalidOperationException("QWEN_HOME was not configured.");
  using var config = JsonDocument.Parse(
    await File.ReadAllTextAsync(Path.Combine(qwenHome, "settings.json"))
  );
  var endpoint = config.RootElement.GetProperty("mcpServers")
    .GetProperty("agentic_router").GetProperty("httpUrl").GetString()
    ?? throw new InvalidOperationException("Agentic Router MCP URL was not configured.");
  var token = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_MCP_TOKEN")
    ?? throw new InvalidOperationException("Agentic Router MCP token was not configured.");
  using var client = new HttpClient();
  client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
  client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
  await SendMcpAsync(client, endpoint, 1, "initialize", new
  {
    protocolVersion = "2025-03-26",
    capabilities = new { },
    clientInfo = new { name = "fake-qwen-code", version = "1" }
  });
  await SendMcpNotificationAsync(client, endpoint, "notifications/initialized");
  using var prompts = await SendMcpAsync(client, endpoint, 2, "prompts/list", new { });
  if (prompts.RootElement.GetProperty("result").GetProperty("prompts").ValueKind != JsonValueKind.Array)
  {
    throw new InvalidOperationException("Agentic Router MCP prompts/list was not compatible.");
  }
  using var resources = await SendMcpAsync(client, endpoint, 3, "resources/list", new { });
  if (resources.RootElement.GetProperty("result").GetProperty("resources").ValueKind != JsonValueKind.Array)
  {
    throw new InvalidOperationException("Agentic Router MCP resources/list was not compatible.");
  }
  using var tools = await SendMcpAsync(client, endpoint, 4, "tools/list", new { });
  var advertised = tools.RootElement.GetProperty("result").GetProperty("tools")
    .EnumerateArray().FirstOrDefault(
      candidate => string.Equals(candidate.GetProperty("name").GetString(), tool, StringComparison.Ordinal)
    );
  if (advertised.ValueKind == JsonValueKind.Undefined)
  {
    throw new InvalidOperationException($"Agentic Router MCP tool '{tool}' was not advertised.");
  }
  var serializedArguments = JsonSerializer.SerializeToElement(arguments);
  if (
    serializedArguments.TryGetProperty("stepId", out _)
    && !advertised.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("stepId", out _)
  )
  {
    throw new InvalidOperationException($"Agentic Router MCP tool '{tool}' omitted its plan step binding schema.");
  }
  using var response = await SendMcpAsync(client, endpoint, 5, "tools/call", new
  {
    name = tool,
    arguments
  });
  var result = response.RootElement.GetProperty("result");
  return new McpToolResult(
    !result.GetProperty("isError").GetBoolean(),
    result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty
  );
}

async Task RunHostStepAsync(List<McpStepResult> steps, string tool, object arguments)
{
  var result = await InvokeHostToolAsync(tool, arguments);
  steps.Add(new McpStepResult(tool, result.Succeeded, result.Output));
}

async Task<JsonDocument> SendMcpAsync(
  HttpClient client,
  string endpoint,
  int id,
  string method,
  object parameters
)
{
  using var response = await client.PostAsJsonAsync(endpoint, new
  {
    jsonrpc = "2.0",
    id,
    method,
    @params = parameters
  });
  response.EnsureSuccessStatusCode();
  return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
}

async Task SendMcpNotificationAsync(HttpClient client, string endpoint, string method)
{
  using var response = await client.PostAsJsonAsync(endpoint, new
  {
    jsonrpc = "2.0",
    method
  });
  response.EnsureSuccessStatusCode();
}

bool HasRegisteredClient(HttpContext context, FakeSession session) =>
  string.Equals(
    context.Request.Headers["X-Qwen-Client-Id"].ToString(),
    session.ClientId,
    StringComparison.Ordinal
  );

object InvalidClientBody(FakeSession session, HttpContext context)
{
  var clientId = context.Request.Headers["X-Qwen-Client-Id"].ToString();
  return new
  {
    error = $"Client id \"{clientId}\" is not registered for session {session.Id}",
    code = "invalid_client_id",
    sessionId = session.Id,
    clientId
  };
}

IResult InvalidClient(FakeSession session, HttpContext context) =>
  Results.Json(
    InvalidClientBody(session, context),
    statusCode: StatusCodes.Status400BadRequest
  );

sealed class FakeSession
{
  public FakeSession(string id, string clientId, string cwd)
  {
    Id = id;
    ClientId = clientId;
    Cwd = cwd;
  }

  public string Id { get; }

  public string ClientId { get; }

  public string Cwd { get; }

  public string Model { get; set; } = string.Empty;

  public string ModelRouteId { get; set; } = string.Empty;

  public string? ActivePromptId { get; set; }

  public int PromptNumber;

  public long EventId;

  public ConcurrentDictionary<Guid, Channel<string>> Subscribers { get; } = new();
}

sealed record PendingPermission(
  string SessionId,
  string PromptId,
  bool ContinueAfterRejection = false
);

sealed record McpToolResult(bool Succeeded, string Output);

sealed record McpStepResult(string Tool, bool Succeeded, string Output);
