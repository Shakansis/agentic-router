using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

if (args.Contains("--version", StringComparer.Ordinal))
{
  Console.WriteLine("1.18.18-fake");
  return;
}

var portIndex = Array.IndexOf(args, "--port");
if (portIndex < 0 || portIndex + 1 >= args.Length || !int.TryParse(args[portIndex + 1], out var port))
{
  return;
}

var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();
var subscribers = new ConcurrentDictionary<Guid, Channel<string>>();
var prompts = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
var permissions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
var sessionNumber = 0;
var password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD") ?? string.Empty;
var runtime = Directory.GetParent(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? string.Empty)?.FullName;

app.Use(async (context, next) =>
{
  var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}"));
  if (password.Length == 0 || !string.Equals(context.Request.Headers.Authorization.ToString(), expected, StringComparison.Ordinal))
  {
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    return;
  }
  await next();
});

app.MapGet("/global/health", () => Results.Json(new { healthy = true, version = "1.18.18-fake" }));
app.MapGet("/event", async (HttpContext context) =>
{
  var subscriberId = Guid.NewGuid();
  var events = Channel.CreateUnbounded<string>();
  subscribers[subscriberId] = events;
  context.Response.ContentType = "text/event-stream";
  try
  {
    await context.Response.StartAsync(context.RequestAborted);
    await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
    await foreach (var item in events.Reader.ReadAllAsync(context.RequestAborted))
    {
      await context.Response.WriteAsync($"data: {item}\n\n", context.RequestAborted);
      await context.Response.Body.FlushAsync(context.RequestAborted);
    }
  }
  finally
  {
    subscribers.TryRemove(subscriberId, out _);
  }
});
app.MapPost("/session", async (HttpContext context) =>
{
  var directory = context.Request.Query["directory"].ToString();
  using var body = await JsonDocument.ParseAsync(context.Request.Body);
  var sessionId = $"ses_fake_opencode_{Interlocked.Increment(ref sessionNumber)}";
  if (runtime is not null)
  {
    Directory.CreateDirectory(runtime);
    await File.WriteAllTextAsync(
      Path.Combine(runtime, "fake-opencode-session.json"),
      JsonSerializer.Serialize(new
      {
        directory,
        model = body.RootElement.GetProperty("model").GetProperty("id").GetString(),
        provider = body.RootElement.GetProperty("model").GetProperty("providerID").GetString(),
        passwordConfigured = password.Length > 0,
        stateRoot = Environment.GetEnvironmentVariable("XDG_STATE_HOME")
      })
    );
  }
  return Results.Json(new
  {
    id = sessionId,
    slug = "fake",
    projectID = "project",
    directory,
    title = "Agentic Router Execute",
    version = "1.18.18-fake",
    time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
  });
});
app.MapPost("/session/{sessionId}/prompt_async", async (string sessionId, HttpContext context) =>
{
  using var body = await JsonDocument.ParseAsync(context.Request.Body);
  var text = body.RootElement.GetProperty("parts")[0].GetProperty("text").GetString() ?? string.Empty;
  prompts[sessionId] = text;
  if (runtime is not null)
  {
    await File.WriteAllTextAsync(
      Path.Combine(runtime, "fake-opencode-prompt.json"),
      JsonSerializer.Serialize(new
      {
        directory = context.Request.Query["directory"].ToString(),
        model = body.RootElement.GetProperty("model").GetProperty("modelID").GetString(),
        provider = body.RootElement.GetProperty("model").GetProperty("providerID").GetString(),
        sessionId,
        text
      })
    );
  }
  context.Response.StatusCode = StatusCodes.Status204NoContent;
  await context.Response.CompleteAsync();
  if (text.Contains("malformed opencode", StringComparison.Ordinal))
  {
    foreach (var subscriber in subscribers.Values)
    {
      await subscriber.Writer.WriteAsync("{bad-json");
    }
    return;
  }
  if (text.Contains("crash opencode", StringComparison.Ordinal))
  {
    Environment.Exit(23);
  }
  if (text.Contains("long opencode", StringComparison.Ordinal))
  {
    await EmitAsync("message.part.updated", new
    {
      sessionID = sessionId,
      part = new { id = "prt_reason_long", sessionID = sessionId, type = "reasoning", text = "" },
      time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    });
    await EmitAsync("message.part.delta", new
    {
      sessionID = sessionId,
      messageID = "msg_assistant_long",
      partID = "prt_reason_long",
      field = "text",
      delta = "Inspecting long OpenCode task."
    });
    return;
  }
  if (text.Contains("permission opencode", StringComparison.Ordinal))
  {
    const string permissionId = "per_fake_opencode";
    permissions[permissionId] = sessionId;
    await EmitAsync("permission.asked", new
    {
      id = permissionId,
      sessionID = sessionId,
      permission = "edit",
      resources = new[] { "README.md" }
    });
    return;
  }
  if (text.Contains("unexpected opencode", StringComparison.Ordinal))
  {
    await EmitAsync("future.opencode.event", new
    {
      sessionID = sessionId,
      futureField = "preserve-me"
    });
  }
  await CompleteAsync(sessionId);
});
app.MapPost("/session/{sessionId}/abort", async (string sessionId) =>
{
  await EmitAsync("session.idle", new { sessionID = sessionId });
  return Results.Json(true);
});
app.MapGet("/session/{sessionId}/diff", (string sessionId) =>
  prompts.TryGetValue(sessionId, out var text)
    && text.Contains("diff opencode", StringComparison.Ordinal)
      ? Results.Json(new[] { new { file = "README.md", before = "old", after = "new" } })
      : Results.Json(Array.Empty<object>())
);
app.MapPost("/permission/{requestId}/reply", async (string requestId, HttpContext context) =>
{
  using var body = await JsonDocument.ParseAsync(context.Request.Body);
  if (permissions.TryRemove(requestId, out var sessionId)
    && string.Equals(body.RootElement.GetProperty("reply").GetString(), "once", StringComparison.Ordinal))
  {
    await CompleteAsync(sessionId);
  }
  return Results.Json(true);
});

await app.RunAsync();

async Task CompleteAsync(string sessionId)
{
  const string reasoning = "Inspecting OpenCode workspace. Internal reasoning stays in Thinking.";
  const string answer = "OpenCode streamed with qwen3.8:27b-gpu0";
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new { id = "prt_reason", sessionID = sessionId, type = "reasoning", text = "" },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.delta", new
  {
    sessionID = sessionId,
    messageID = "msg_assistant",
    partID = "prt_reason",
    field = "text",
    delta = reasoning
  });
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new { id = "prt_reason", sessionID = sessionId, type = "reasoning", text = reasoning },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new
    {
      id = "prt_tool",
      sessionID = sessionId,
      type = "tool",
      callID = "call_read",
      tool = "read",
      state = new { status = "running", input = new { path = "README.md" } }
    },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new
    {
      id = "prt_tool",
      sessionID = sessionId,
      type = "tool",
      callID = "call_read",
      tool = "read",
      state = new { status = "completed", output = "ok", title = "Read README.md", metadata = new { } }
    },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new { id = "prt_text", sessionID = sessionId, type = "text", text = "" },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.delta", new
  {
    sessionID = sessionId,
    messageID = "msg_assistant",
    partID = "prt_text",
    field = "text",
    delta = answer
  });
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new { id = "prt_text", sessionID = sessionId, type = "text", text = answer },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.updated", new
  {
    sessionID = sessionId,
    info = new
    {
      id = "msg_assistant",
      sessionID = sessionId,
      role = "assistant",
      tokens = new
      {
        input = 1_000,
        output = 40,
        reasoning = 20,
        cache = new { read = 200, write = 34 }
      }
    }
  });
  await EmitAsync("session.diff", new { sessionID = sessionId, diff = Array.Empty<object>() });
  await EmitAsync("session.idle", new { sessionID = sessionId });
}

async Task EmitAsync(string type, object properties)
{
  var payload = JsonSerializer.Serialize(new
  {
    id = $"evt_{Guid.NewGuid():N}",
    type,
    properties
  });
  foreach (var subscriber in subscribers.Values)
  {
    await subscriber.Writer.WriteAsync(payload);
  }
}
