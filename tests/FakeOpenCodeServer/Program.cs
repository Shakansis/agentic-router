using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
var selections = new ConcurrentDictionary<string, ModelSelection>(StringComparer.Ordinal);
var permissions = new ConcurrentDictionary<string, PendingPermission>(StringComparer.Ordinal);
var sessionNumber = 0;
var password = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD") ?? string.Empty;
var runtime = Directory.GetParent(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? string.Empty)?.FullName;
if (runtime is not null)
{
  var configPath = Path.Combine(runtime, "config", "opencode", "opencode.json");
  using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
  var configuredModel = config.RootElement.GetProperty("provider")
    .GetProperty("agentic-router-ollama")
    .GetProperty("models")
    .EnumerateObject()
    .Single()
    .Value;
  var variants = configuredModel.GetProperty("variants");
  var validVariants = variants.ValueKind == JsonValueKind.Object
    && new[] { "low", "medium", "high" }.All(effort =>
      variants.TryGetProperty(effort, out var variant)
      && string.Equals(
        variant.GetProperty("body").GetProperty("reasoning_effort").GetString(),
        effort,
        StringComparison.Ordinal
      )
    );
  if (!validVariants)
  {
    Console.Error.WriteLine("OpenCode effort variants must be an object keyed by effort level.");
    Environment.ExitCode = 2;
    return;
  }
  Directory.CreateDirectory(runtime);
  await File.WriteAllTextAsync(
    Path.Combine(runtime, "fake-opencode-process-id.txt"),
    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
  );
}

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
  var model = body.RootElement.GetProperty("model").GetProperty("modelID").GetString()
    ?? throw new InvalidOperationException("OpenCode prompt omitted modelID.");
  var provider = body.RootElement.GetProperty("model").GetProperty("providerID").GetString()
    ?? throw new InvalidOperationException("OpenCode prompt omitted providerID.");
  var variant = body.RootElement.TryGetProperty("variant", out var variantElement)
    && variantElement.ValueKind == JsonValueKind.String
      ? variantElement.GetString()
      : null;
  if (variant is not null and not "low" and not "medium" and not "high")
  {
    context.Response.StatusCode = StatusCodes.Status400BadRequest;
    await context.Response.CompleteAsync();
    return;
  }
  prompts[sessionId] = text;
  selections[sessionId] = new ModelSelection(provider, model);
  if (runtime is not null)
  {
    await File.WriteAllTextAsync(
      Path.Combine(runtime, "fake-opencode-prompt.json"),
      JsonSerializer.Serialize(new
      {
        directory = context.Request.Query["directory"].ToString(),
        model = body.RootElement.GetProperty("model").GetProperty("modelID").GetString(),
        provider = body.RootElement.GetProperty("model").GetProperty("providerID").GetString(),
        variant,
        sessionId,
        text
      })
    );
  }
  context.Response.StatusCode = StatusCodes.Status204NoContent;
  await context.Response.CompleteAsync();
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new
    {
      id = $"prt_user_{sessionId}",
      messageID = $"msg_user_{sessionId}",
      sessionID = sessionId,
      type = "text",
      text
    },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  if (text.Contains("SUPERVISION_", StringComparison.Ordinal))
  {
    const string criterion = "opencode-supervised.txt contains the exact text visible native edit";
    if (text.Contains("SUPERVISION_COMPLETE_V1", StringComparison.Ordinal))
    {
      await CompleteAsync(
        sessionId,
        JsonSerializer.Serialize(new
        {
          decision = "complete_goal",
          finalAnswer = "Created opencode-supervised.txt and verified the native OpenCode edit."
        }),
        includeReadTool: false
      );
      return;
    }
    if (
      text.Contains("SUPERVISION_VERIFY_V1", StringComparison.Ordinal)
      || text.Contains("SUPERVISION_VERIFY_WITH_VALIDATION_V1", StringComparison.Ordinal)
    )
    {
      await CompleteAsync(
        sessionId,
        JsonSerializer.Serialize(new
        {
          decision = "accept_work",
          evidenceRevision = ExtractEvidenceRevision(text),
          coveredCriteria = new[] { criterion },
          summary = "The current Host evidence contains the native OpenCode edit."
        }),
        includeReadTool: false
      );
      return;
    }
    if (text.Contains("SUPERVISION_WORKER_V1", StringComparison.Ordinal))
    {
      var directory = context.Request.Query["directory"].ToString();
      const string relativePath = "opencode-supervised.txt";
      await EmitAsync("message.part.updated", new
      {
        sessionID = sessionId,
        part = new
        {
          id = $"prt_supervision_commentary_{sessionId}",
          sessionID = sessionId,
          type = "text",
          text = ""
        },
        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      });
      await EmitAsync("message.part.delta", new
      {
        sessionID = sessionId,
        messageID = $"msg_supervision_commentary_{sessionId}",
        partID = $"prt_supervision_commentary_{sessionId}",
        field = "text",
        delta = $"I will now edit {relativePath} with the required content."
      });
      await EmitAsync("message.part.updated", new
      {
        sessionID = sessionId,
        part = new
        {
          id = $"prt_supervision_edit_{sessionId}",
          sessionID = sessionId,
          type = "tool",
          callID = $"call_supervision_edit_{sessionId}",
          tool = "edit",
          state = new { status = "running", input = new { filePath = relativePath } }
        },
        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      });
      await File.WriteAllTextAsync(
        Path.Combine(directory, relativePath),
        "visible native edit"
      );
      await EmitAsync("message.part.updated", new
      {
        sessionID = sessionId,
        part = new
        {
          id = $"prt_supervision_edit_{sessionId}",
          sessionID = sessionId,
          type = "tool",
          callID = $"call_supervision_edit_{sessionId}",
          tool = "edit",
          state = new
          {
            status = "completed",
            output = "updated",
            title = $"edit: {relativePath}",
            metadata = new { }
          }
        },
        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      });
      await CompleteAsync(
        sessionId,
        "Native OpenCode edit completed for the assigned work item.",
        includeReadTool: false
      );
      return;
    }
    if (text.Contains("SUPERVISION_DECOMPOSE_V1", StringComparison.Ordinal))
    {
      await CompleteAsync(
        sessionId,
        JsonSerializer.Serialize(new
        {
          decision = "dispatch_work",
          items = new[]
          {
            new
            {
              objective = "Create opencode-supervised.txt through a native OpenCode edit.",
              acceptanceCriteria = new[] { criterion },
              evidencePaths = new[] { "opencode-supervised.txt" }
            }
          }
        }),
        includeReadTool: false
      );
      return;
    }
  }
  if (text.Contains("Benchmark test: FS-", StringComparison.Ordinal))
  {
    var directory = context.Request.Query["directory"].ToString();
    if (
      text.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal)
      && model is "unused:latest" or "docs:latest"
    )
    {
      return;
    }
    if (
      text.Contains("Benchmark test: FS-UPDATE-001", StringComparison.Ordinal)
      && string.Equals(model, "structured-failure:latest", StringComparison.Ordinal)
    )
    {
      await EmitAsync("session.error", new
      {
        sessionID = sessionId,
        error = "fake benchmark harness failure"
      });
      return;
    }
    await PrepareBenchmarkOutcomeAsync(directory, model, text);
    var report = text.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal)
      ? string.Equals(model, "beta:code", StringComparison.Ordinal)
        ? "codename=ORBIT-41"
        : "codename=ORBIT-41\nverification-word=marigold"
      : $"OpenCode completed {model}.";
    await CompleteBenchmarkAsync(sessionId, report);
    return;
  }
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
    var deleteRequested = text.Contains(
      "delete permission opencode",
      StringComparison.Ordinal
    );
    var readRequested = text.Contains(
      "read permission opencode",
      StringComparison.Ordinal
    );
    var directory = context.Request.Query["directory"].ToString();
    permissions[permissionId] = new PendingPermission(
      sessionId,
      deleteRequested
        ? Path.Combine(directory, "codex-created.txt")
        : null
    );
    var resources = deleteRequested
      ? new[] { "codex-created.txt" }
      : text.Contains("outside", StringComparison.Ordinal)
        ? new[] { "../outside.txt" }
        : new[] { "README.md" };
    if (text.Contains("legacy permission opencode", StringComparison.Ordinal))
    {
      await EmitAsync("permission.asked", new
      {
        id = permissionId,
        sessionID = sessionId,
        permission = deleteRequested ? "delete" : "edit",
        patterns = resources,
        metadata = new { },
        always = Array.Empty<string>()
      });
    }
    else
    {
      await EmitAsync("permission.v2.asked", new
      {
        id = permissionId,
        sessionID = sessionId,
        action = deleteRequested ? "delete" : readRequested ? "read" : "edit",
        resources
      });
    }
    return;
  }
  if (text.Contains("host bridge opencode", StringComparison.Ordinal))
  {
    var directory = context.Request.Query["directory"].ToString();
    if (text.Contains("web host bridge opencode", StringComparison.Ordinal))
    {
      var web = await InvokeHostToolAsync(
        "web_search",
        new { query = "generic Host web capability" }
      );
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-web.json"),
          JsonSerializer.Serialize(new { succeeded = web.Succeeded, output = web.Output })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    if (text.Contains("plan host bridge opencode", StringComparison.Ordinal))
    {
      var plan = await InvokeHostToolAsync(
        "create_execution_plan",
        new
        {
          objective = "Prove OpenCode plan bridging",
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
          stepId = "step-1"
        }
      );
      var secondAction = await InvokeHostToolAsync(
        "run_process",
        new
        {
          executable = "dotnet",
          arguments = new[] { "--version" },
          workingDirectory = ".",
          timeoutSeconds = 30
        }
      );
      var fullPlan = await InvokeHostToolAsync(
        "get_execution_plan",
        new { }
      );
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-plan.json"),
          JsonSerializer.Serialize(new
          {
            succeeded = plan.Succeeded && unboundAction.Succeeded
              && firstAction.Succeeded && secondAction.Succeeded && fullPlan.Succeeded,
            plan = new { succeeded = plan.Succeeded, output = plan.Output },
            unboundAction = new { succeeded = unboundAction.Succeeded, output = unboundAction.Output },
            firstAction = new { succeeded = firstAction.Succeeded, output = firstAction.Output },
            secondAction = new { succeeded = secondAction.Succeeded, output = secondAction.Output },
            fullPlan = new { succeeded = fullPlan.Succeeded, output = fullPlan.Output }
          })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    if (text.Contains("repeat guard host bridge opencode", StringComparison.Ordinal))
    {
      var first = await InvokeHostToolAsync(
        "read_file",
        new { path = "missing-repeat-guard.txt" }
      );
      var second = await InvokeHostToolAsync(
        "read_file",
        new { path = "missing-repeat-guard.txt" }
      );
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-repeat-guard.json"),
          JsonSerializer.Serialize(new
          {
            succeeded = !first.Succeeded && !second.Succeeded,
            first = new { succeeded = first.Succeeded, output = first.Output },
            second = new { succeeded = second.Succeeded, output = second.Output }
          })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    if (text.Contains("vertical two host bridge opencode", StringComparison.Ordinal))
    {
      async Task CheckpointAsync(string value)
      {
        if (runtime is not null)
        {
          await File.WriteAllTextAsync(
            Path.Combine(runtime, "fake-opencode-host-vertical-two.checkpoint"),
            value
          );
        }
      }
      await CheckpointAsync("started");
      var directoryAction = await InvokeHostToolAsync("create_directory", new { path = "empty" });
      var directoryAgain = await InvokeHostToolAsync("create_directory", new { path = "empty" });
      var emptyList = await InvokeHostToolAsync("list_files", new { path = "empty", recursive = false });
      await CheckpointAsync("directories");
      var create = await InvokeHostToolAsync(
        "create_files",
        new { files = new[] { new { path = "item.txt", content = "alpha beta" } } }
      );
      await CheckpointAsync("create-first");
      var createAgain = await InvokeHostToolAsync(
        "create_files",
        new { files = new[] { new { path = "item.txt", content = "alpha beta" } } }
      );
      await CheckpointAsync("create-again");
      var conflict = await InvokeHostToolAsync(
        "create_files",
        new { files = new[] { new { path = "item.txt", content = "different" } } }
      );
      await CheckpointAsync("create");
      var renameSource = await InvokeHostToolAsync(
        "create_files",
        new { files = new[] { new { path = "rename-source.txt", content = "rename me" } } }
      );
      var inspectRename = await InvokeHostToolAsync("read_file", new { path = "rename-source.txt" });
      var rename = await InvokeHostToolAsync(
        "rename_path",
        new { sourcePath = "rename-source.txt", destinationPath = "rename-destination.txt" }
      );
      var renameAgain = await InvokeHostToolAsync(
        "rename_path",
        new { sourcePath = "rename-source.txt", destinationPath = "rename-destination.txt" }
      );
      await CheckpointAsync("rename");
      var emptySearch = await InvokeHostToolAsync(
        "search_text",
        new { path = "item.txt", query = "missing" }
      );
      var largeContent = new string('x', 140_000);
      var largeCreate = await InvokeHostToolAsync(
        "create_files",
        new { files = new[] { new { path = "large.txt", content = largeContent } } }
      );
      var largeRead = await InvokeHostToolAsync("read_file", new { path = "large.txt" });
      var rangedRead = await InvokeHostToolAsync(
        "read_file",
        new { path = "large.txt", offsetBytes = 0, lengthBytes = 1024 }
      );
      await CheckpointAsync("large-read");
      var delete = await InvokeHostToolAsync(
        "delete_paths",
        new { paths = new[] { "item.txt" }, recursive = false }
      );
      var deleteAgain = await InvokeHostToolAsync(
        "delete_paths",
        new { paths = new[] { "item.txt" }, recursive = false }
      );
      await CheckpointAsync("delete");
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-vertical-two.json"),
          JsonSerializer.Serialize(new
          {
            directory = directoryAction,
            directoryAgain,
            emptyList,
            create,
            createAgain,
            conflict,
            renameSource,
            inspectRename,
            rename,
            renameAgain,
            emptySearch,
            largeCreate,
            largeRead,
            rangedRead,
            delete,
            deleteAgain
          })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    if (text.Contains("vertical two process host bridge opencode", StringComparison.Ordinal))
    {
      var processFiles = await InvokeHostToolAsync(
        "create_files",
        new
        {
          files = new[]
          {
            new
            {
              path = "effect.csproj",
              content = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            },
            new { path = "Program.cs", content = "this will not compile" }
          }
        }
      );
      var process = await InvokeHostToolAsync(
        "run_process",
        new
        {
          executable = "dotnet",
          arguments = new[] { "build", "effect.csproj", "--no-restore", "--nologo" },
          workingDirectory = ".",
          timeoutSeconds = 30
        }
      );
      var processWithoutRefresh = await InvokeHostToolAsync(
        "run_process",
        new
        {
          executable = "dotnet",
          arguments = new[] { "build", "effect.csproj", "--no-restore", "--nologo" },
          workingDirectory = ".",
          timeoutSeconds = 30
        }
      );
      var refresh = await InvokeHostToolAsync("list_files", new { path = ".", recursive = true });
      var processAfterRefresh = await InvokeHostToolAsync(
        "run_process",
        new
        {
          executable = "dotnet",
          arguments = new[] { "build", "effect.csproj", "--no-restore", "--nologo" },
          workingDirectory = ".",
          timeoutSeconds = 30
        }
      );
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-vertical-two-process.json"),
          JsonSerializer.Serialize(new
          {
            processFiles,
            process,
            processWithoutRefresh,
            refresh,
            processAfterRefresh
          })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    if (text.Contains("vertical two unborn git host bridge opencode", StringComparison.Ordinal))
    {
      var log = await InvokeHostToolAsync("git_log", new { maxEntries = 10 });
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-vertical-two-unborn.json"),
          JsonSerializer.Serialize(new { log })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    if (text.Contains("boundary host bridge opencode", StringComparison.Ordinal))
    {
      var rejected = await InvokeHostToolAsync(
        "create_files",
        new
        {
          files = new[] { new { path = "../opencode-escape.txt", content = "must not escape" } }
        }
      );
      var recovered = await InvokeHostToolAsync(
        "create_files",
        new
        {
          files = new[] { new { path = "opencode-recovered.txt", content = "recovered safely" } }
        }
      );
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-boundary.json"),
          JsonSerializer.Serialize(new
          {
            rejected = new { rejected.Succeeded, rejected.Output },
            recovered = new { recovered.Succeeded, recovered.Output }
          })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    if (text.Contains("parity host bridge opencode", StringComparison.Ordinal))
    {
      var steps = new List<McpStepResult>();
      await RunHostStepAsync(steps, "create_directory", new { path = "opencode-parity" });
      await RunHostStepAsync(steps, "create_files", new
      {
        files = new[] { new { path = "opencode-parity/item.txt", content = "parity" } }
      });
      await RunHostStepAsync(steps, "get_file_info", new { path = "opencode-parity/item.txt" });
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
        paths = new[] { "opencode-parity" },
        recursive = true
      });
      await RunHostStepAsync(steps, "create_files", new
      {
        files = new[] { new { path = "opencode-parity-complete.txt", content = "complete" } }
      });
      if (runtime is not null)
      {
        await File.WriteAllTextAsync(
          Path.Combine(runtime, "fake-opencode-host-parity.json"),
          JsonSerializer.Serialize(new
          {
            steps,
            deleted = !Directory.Exists(Path.Combine(directory, "opencode-parity")),
            finalObserved = File.Exists(Path.Combine(directory, "opencode-parity-complete.txt"))
          })
        );
      }
      await CompleteAsync(sessionId);
      return;
    }
    var relativePath = text.Contains("ask host bridge opencode", StringComparison.Ordinal)
      ? "opencode-host-ask.txt"
      : "opencode-host-auto.txt";
    var result = await InvokeHostToolAsync(
      "create_files",
      new
      {
        files = new[]
        {
          new
          {
            path = relativePath,
            content = $"OpenCode Host bridge created {relativePath}."
          }
        }
      }
    );
    if (runtime is not null)
    {
      await File.WriteAllTextAsync(
        Path.Combine(runtime, "fake-opencode-host-tool.json"),
        JsonSerializer.Serialize(new
        {
          tool = "create_files",
          relativePath,
          succeeded = result.Succeeded,
          output = result.Output,
          observed = File.Exists(Path.Combine(directory, relativePath))
        })
      );
    }
    await CompleteAsync(sessionId);
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
  if (permissions.TryRemove(requestId, out var pending))
  {
    if (string.Equals(
      body.RootElement.GetProperty("reply").GetString(),
      "once",
      StringComparison.Ordinal
    ) && pending.DeletePath is not null)
    {
      File.Delete(pending.DeletePath);
    }
    await CompleteAsync(pending.SessionId);
  }
  return Results.Json(true);
});

await app.RunAsync();

async Task CompleteAsync(
  string sessionId,
  string? answerOverride = null,
  bool includeReadTool = true
)
{
  const string reasoning = "Inspecting OpenCode workspace. Internal reasoning stays in Thinking.";
  var answer = answerOverride ?? "OpenCode streamed with qwen3.8:27b-gpu0";
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
  if (includeReadTool)
  {
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
  }
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
      providerID = prompts.TryGetValue(sessionId, out var prompt)
        && prompt.Contains("reroute opencode", StringComparison.Ordinal)
          ? "unexpected-cloud-provider"
          : selections[sessionId].Provider,
      modelID = prompt is not null
        && prompt.Contains("reroute opencode", StringComparison.Ordinal)
          ? "unexpected-cloud-model"
          : selections[sessionId].Model,
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

static long ExtractEvidenceRevision(string prompt)
{
  const string marker = "Host evidence revision ";
  var start = prompt.IndexOf(marker, StringComparison.Ordinal);
  if (start < 0)
  {
    return 0;
  }
  start += marker.Length;
  var end = prompt.IndexOf(':', start);
  return end > start
    && long.TryParse(
      prompt[start..end],
      System.Globalization.NumberStyles.None,
      System.Globalization.CultureInfo.InvariantCulture,
      out var revision
    )
      ? revision
      : 0;
}

async Task CompleteBenchmarkAsync(string sessionId, string answer)
{
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new
    {
      id = $"prt_benchmark_tool_{sessionId}",
      sessionID = sessionId,
      type = "tool",
      callID = $"call_benchmark_{sessionId}",
      tool = "structured_filesystem",
      state = new { status = "running", input = new { } }
    },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new
    {
      id = $"prt_benchmark_tool_{sessionId}",
      sessionID = sessionId,
      type = "tool",
      callID = $"call_benchmark_{sessionId}",
      tool = "structured_filesystem",
      state = new { status = "completed", output = "ok", metadata = new { } }
    },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.updated", new
  {
    sessionID = sessionId,
    part = new { id = $"prt_benchmark_text_{sessionId}", sessionID = sessionId, type = "text", text = "" },
    time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
  });
  await EmitAsync("message.part.delta", new
  {
    sessionID = sessionId,
    messageID = $"msg_benchmark_{sessionId}",
    partID = $"prt_benchmark_text_{sessionId}",
    field = "text",
    delta = answer
  });
  await EmitAsync("message.updated", new
  {
    sessionID = sessionId,
    info = new
    {
      id = $"msg_benchmark_{sessionId}",
      sessionID = sessionId,
      role = "assistant",
      providerID = selections[sessionId].Provider,
      modelID = selections[sessionId].Model,
      tokens = new
      {
        input = 200,
        output = 20,
        reasoning = 0,
        cache = new { read = 0, write = 0 }
      }
    }
  });
  await EmitAsync("session.idle", new { sessionID = sessionId });
}

async Task PrepareBenchmarkOutcomeAsync(string directory, string model, string request)
{
  if (request.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal))
  {
    return;
  }
  if (request.Contains("Benchmark test: FS-UPDATE-001", StringComparison.Ordinal))
  {
    await File.WriteAllTextAsync(
      Path.Combine(directory, "fixture", "update.txt"),
      string.Equals(model, "beta:code", StringComparison.Ordinal)
        ? "mode=preview\nretries=4\nowner=router\n"
        : "mode=preview\nretries=3\nowner=router\n"
    );
    return;
  }
  if (request.Contains("Benchmark test: FS-DELETE-001", StringComparison.Ordinal))
  {
    if (!string.Equals(model, "beta:code", StringComparison.Ordinal))
    {
      File.Delete(Path.Combine(directory, "fixture", "delete.txt"));
    }
    return;
  }
  const string content = "Agentic Router Benchmark\noperation=create\nresult=success";
  var benchmarkDirectory = Path.Combine(directory, "benchmark-data");
  Directory.CreateDirectory(benchmarkDirectory);
  await File.WriteAllTextAsync(
    Path.Combine(benchmarkDirectory, "result.txt"),
    string.Equals(model, "beta:code", StringComparison.Ordinal)
      ? content + "!"
      : content
  );
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

async Task<McpToolResult> InvokeHostToolAsync(string tool, object arguments)
{
  var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
    ?? throw new InvalidOperationException("XDG_CONFIG_HOME was not configured.");
  using var config = JsonDocument.Parse(
    await File.ReadAllTextAsync(Path.Combine(configRoot, "opencode", "opencode.json"))
  );
  var endpoint = config.RootElement.GetProperty("mcp").GetProperty("agentic_router")
    .GetProperty("url").GetString()
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
    clientInfo = new { name = "fake-opencode", version = "1" }
  });
  await SendMcpNotificationAsync(client, endpoint, "notifications/initialized");
  using var tools = await SendMcpAsync(client, endpoint, 2, "tools/list", new { });
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
  var response = await SendMcpAsync(client, endpoint, 3, "tools/call", new
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

internal sealed record PendingPermission(
  string SessionId,
  string? DeletePath
);

internal sealed record ModelSelection(string Provider, string Model);

internal sealed record McpToolResult(bool Succeeded, string Output);

internal sealed record McpStepResult(string Tool, bool Succeeded, string Output);
