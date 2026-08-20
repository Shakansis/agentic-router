using System.Collections.Concurrent;
using System.Text.Json;

if (args.SequenceEqual(new[] { "--version" }, StringComparer.Ordinal))
{
  Console.WriteLine("codex-cli fake-0.148.0");
  return;
}

var expectedArguments = new[]
{
  "app-server",
  "--listen",
  "stdio://",
  "--strict-config",
  "--disable",
  "remote_plugin",
  "--disable",
  "plugins",
  "--disable",
  "remote_control"
};
if (!args.SequenceEqual(expectedArguments, StringComparer.Ordinal))
{
  Console.Error.WriteLine(
    $"Unexpected fake App Server arguments: {string.Join(' ', args)}"
  );
  Environment.ExitCode = 2;
  return;
}

var outputGate = new SemaphoreSlim(1, 1);
var turns = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
var approvals = new ConcurrentDictionary<long, TaskCompletionSource<bool>>();
var toolResponses = new ConcurrentDictionary<long, TaskCompletionSource<(bool Success, string Text)>>();
var threadNumber = 0;
var turnNumber = 0;
var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
if (!string.IsNullOrWhiteSpace(codexHome))
{
  Directory.CreateDirectory(codexHome);
  await File.WriteAllTextAsync(
    Path.Combine(codexHome, "fake-app-server-started.marker"),
    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
  );
  await File.WriteAllTextAsync(
    Path.Combine(codexHome, "fake-app-server-environment.json"),
    JsonSerializer.Serialize(new
    {
      ollamaHost = Environment.GetEnvironmentVariable("OLLAMA_HOST"),
      codexOssBaseUrl = Environment.GetEnvironmentVariable("CODEX_OSS_BASE_URL")
    })
  );
}

while (await Console.In.ReadLineAsync() is { } line)
{
  using var document = JsonDocument.Parse(line);
  var root = document.RootElement;
  var hasId = root.TryGetProperty("id", out var id);
  if (!root.TryGetProperty("method", out var methodElement))
  {
    if (
      hasId
      && toolResponses.TryRemove(id.GetInt64(), out var toolResponse)
      && root.TryGetProperty("result", out var dynamicResult)
    )
    {
      var text = dynamicResult.GetProperty("contentItems")[0].GetProperty("text").GetString()
        ?? string.Empty;
      toolResponse.TrySetResult((dynamicResult.GetProperty("success").GetBoolean(), text));
      continue;
    }
    if (
      hasId
      && approvals.TryRemove(id.GetInt64(), out var approval)
      && root.TryGetProperty("result", out var approvalResult)
    )
    {
      approval.TrySetResult(
        string.Equals(
          approvalResult.GetProperty("decision").GetString(),
          "accept",
          StringComparison.Ordinal
        )
      );
    }
    continue;
  }
  var method = methodElement.GetString() ?? string.Empty;
  var parameters = root.TryGetProperty("params", out var value) ? value : default;

  switch (method)
  {
    case "initialize":
      if (
        !parameters.TryGetProperty("capabilities", out var capabilities)
        || !capabilities.GetProperty("experimentalApi").GetBoolean()
      )
      {
        await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected experimentalApi capability." } });
        break;
      }
      await SendAsync(new { id = id.GetInt64(), result = new { userAgent = "fake-codex", platformFamily = "windows", platformOs = "windows" } });
      break;
    case "initialized":
      break;
    case "thread/start":
      {
        var dynamicTools = parameters.GetProperty("dynamicTools");
        var dynamicToolNames = dynamicTools.EnumerateArray()
          .Select(tool => tool.GetProperty("name").GetString())
          .ToArray();
        if (dynamicToolNames.Length > 0
          && (
            dynamicToolNames.Distinct(StringComparer.Ordinal).Count() != dynamicToolNames.Length
            || !new[]
            {
              "create_files",
              "delete_paths",
              "run_process",
              "git_status",
              "git_create_commit"
            }.All(expected => dynamicToolNames.Contains(expected, StringComparer.Ordinal))
          ))
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the projected Agentic Router Host capability tools." } });
          break;
        }
        if (
          !string.Equals(
            parameters.GetProperty("modelProvider").GetString(),
            "ollama",
            StringComparison.Ordinal
          )
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32602, message = "Expected the exact Ollama provider." } });
          break;
        }
        if (
          parameters.GetProperty("approvalPolicy").GetString() is not ("on-request" or "never")
          || !string.Equals(
            parameters.GetProperty("sandbox").GetString(),
            "workspace-write",
            StringComparison.Ordinal
          )
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the supported thread policy values." } });
          break;
        }
        var threadId = $"fake-thread-{Interlocked.Increment(ref threadNumber)}";
        var model = parameters.GetProperty("model").GetString();
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-thread-request.json"),
            JsonSerializer.Serialize(new
            {
              method,
              cwd = parameters.GetProperty("cwd").GetString(),
              model,
              provider = parameters.GetProperty("modelProvider").GetString()
            })
          );
        }
        await SendAsync(new
        {
          id = id.GetInt64(),
          result = new
          {
            model,
            sandbox = new
            {
              type = "workspaceWrite"
            },
            thread = new
            {
              id = threadId,
              sessionId = threadId,
              modelProvider = "ollama"
            }
          }
        });
        await SendAsync(new { method = "thread/started", @params = new { thread = new { id = threadId } } });
        break;
      }
    case "thread/resume":
      {
        var threadId = parameters.GetProperty("threadId").GetString()!;
        var model = parameters.GetProperty("model").GetString();
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-thread-resumed.json"),
            JsonSerializer.Serialize(new
            {
              threadId,
              cwd = parameters.GetProperty("cwd").GetString(),
              model,
              provider = parameters.GetProperty("modelProvider").GetString()
            })
          );
        }
        await SendAsync(new
        {
          id = id.GetInt64(),
          result = new
          {
            model,
            sandbox = new { type = "workspaceWrite" },
            thread = new
            {
              id = threadId,
              sessionId = threadId,
              modelProvider = "ollama"
            }
          }
        });
        await SendAsync(new { method = "thread/started", @params = new { thread = new { id = threadId } } });
        break;
      }
    case "turn/start":
      {
        var threadId = parameters.GetProperty("threadId").GetString()!;
        var turnId = $"fake-turn-{Interlocked.Increment(ref turnNumber)}";
        var input = parameters.GetProperty("input")[0].GetProperty("text").GetString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-turn-input.txt"),
            input
          );
        }
        var cwd = parameters.GetProperty("cwd").GetString()!;
        var model = parameters.GetProperty("model").GetString()!;
        var sandboxPolicy = parameters.GetProperty("sandboxPolicy");
        if (
          parameters.GetProperty("approvalPolicy").GetString() is not ("on-request" or "never")
          || !string.Equals(
            sandboxPolicy.GetProperty("type").GetString(),
            "workspaceWrite",
            StringComparison.Ordinal
          )
          || sandboxPolicy.TryGetProperty("readOnlyAccess", out _)
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the supported turn policy values." } });
          break;
        }
        var source = new CancellationTokenSource();
        turns[turnId] = source;
        await SendAsync(new
        {
          id = id.GetInt64(),
          result = new { turn = new { id = turnId, status = "inProgress", items = Array.Empty<object>(), error = (object?)null } }
        });
        _ = RunTurnAsync(threadId, turnId, input, cwd, model, source.Token);
        break;
      }
    case "turn/interrupt":
      {
        var threadId = parameters.GetProperty("threadId").GetString()!;
        var turnId = parameters.GetProperty("turnId").GetString()!;
        if (turns.TryRemove(turnId, out var source))
        {
          source.Cancel();
          source.Dispose();
        }
        await SendAsync(new { id = id.GetInt64(), result = new { } });
        await SendAsync(new
        {
          method = "turn/completed",
          @params = new { threadId, turn = new { id = turnId, status = "interrupted", error = (object?)null } }
        });
        break;
      }
    default:
      if (hasId)
      {
        await SendAsync(new { id = id.GetInt64(), error = new { code = -32601, message = $"Unsupported fake method {method}" } });
      }
      break;
  }
}

async Task RunTurnAsync(
  string threadId,
  string turnId,
  string input,
  string cwd,
  string model,
  CancellationToken cancellationToken
)
{
  try
  {
    var currentRequest = CurrentUserRequest(input);
    await SendAsync(new { method = "turn/started", @params = new { threadId, turn = new { id = turnId, status = "inProgress" } } });
    await SendAsync(new { method = "item/reasoning/summaryTextDelta", @params = new { threadId, turnId, itemId = $"reason-{turnId}", delta = "Inspecting — revisão " } });

    if (currentRequest.Contains("crash codex child", StringComparison.OrdinalIgnoreCase))
    {
      Environment.Exit(23);
    }

    if (currentRequest.Contains("malformed codex event", StringComparison.OrdinalIgnoreCase))
    {
      await SendRawAsync("{ this is not valid JSON");
      turns.TryRemove(turnId, out _);
      return;
    }

    if (currentRequest.Contains("unexpected codex event", StringComparison.OrdinalIgnoreCase))
    {
      await SendAsync(new
      {
        method = "future/nativeEvent",
        @params = new
        {
          threadId,
          turnId,
          nativeOnlyMarker = "preserve-me",
          nested = new { value = 42 }
        }
      });
    }

    if (currentRequest.Contains("recovered codex diagnostic", StringComparison.OrdinalIgnoreCase))
    {
      await SendAsync(new
      {
        method = "warning",
        @params = new
        {
          threadId,
          turnId,
          message = "Fake Codex warning retained as activity."
        }
      });
      await SendAsync(new
      {
        method = "error",
        @params = new
        {
          threadId,
          turnId,
          message = "Fake recoverable Codex diagnostic.",
          codexErrorInfo = new { type = "fakeRecoverable" }
        }
      });
    }

    await Task.Delay(200, cancellationToken);
    await SendAsync(new { method = "item/reasoning/summaryTextDelta", @params = new { threadId, turnId, itemId = $"reason-{turnId}", delta = "the trusted workspace." } });

    if (currentRequest.Contains("chronological codex content", StringComparison.OrdinalIgnoreCase))
    {
      await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-first-{turnId}", delta = "First **response** " } });
      await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-first-{turnId}", delta = "segment." } });
      await SendAsync(new { method = "item/reasoning/summaryTextDelta", @params = new { threadId, turnId, itemId = $"reason-second-{turnId}", delta = "Thinking again after " } });
      await SendAsync(new { method = "item/reasoning/summaryTextDelta", @params = new { threadId, turnId, itemId = $"reason-second-{turnId}", delta = "the first response." } });
    }

    if (currentRequest.Contains("long codex turn", StringComparison.OrdinalIgnoreCase))
    {
      await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }

    var itemId = $"command-{turnId}";
    var command = "fake bounded workspace action";
    await SendAsync(new
    {
      method = "item/started",
      @params = new
      {
        threadId,
        turnId,
        item = new { type = "commandExecution", id = itemId, command, cwd, status = "inProgress", commandActions = Array.Empty<object>() }
      }
    });
    await SendAsync(new { method = "item/commandExecution/outputDelta", @params = new { threadId, turnId, itemId, delta = "fake output\n" } });

    if (currentRequest.Contains("recover command deletion with host batch", StringComparison.OrdinalIgnoreCase))
    {
      var approvalId = 40_000L + int.Parse(turnId["fake-turn-".Length..], System.Globalization.CultureInfo.InvariantCulture);
      var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      approvals[approvalId] = approval;
      await SendAsync(new
      {
        method = "item/commandExecution/requestApproval",
        id = approvalId,
        @params = new
        {
          threadId,
          turnId,
          itemId,
          reason = "Attempt shell deletion before Host correction",
          command = "Remove-Item batch-delete-a.txt,batch-delete-b.txt"
        }
      });
      if (await approval.Task.WaitAsync(cancellationToken))
      {
        throw new InvalidOperationException("The Host unexpectedly approved an unmappable shell deletion.");
      }
    }

    if (currentRequest.Contains("create host batch files", StringComparison.OrdinalIgnoreCase))
    {
      var dynamicItemId = $"dynamic-{turnId}";
      var callId = $"call-{turnId}";
      var requestId = 20_000L + int.Parse(turnId["fake-turn-".Length..], System.Globalization.CultureInfo.InvariantCulture);
      var arguments = new
      {
        files = new[]
        {
          new { path = "batch-ação.html", content = "<!doctype html><title>Ação</title>\n" },
          new { path = "batch-estilo.css", content = "/* revisão */\nbody { color: #123; }\n" }
        }
      };
      await SendAsync(new
      {
        method = "item/started",
        @params = new
        {
          threadId,
          turnId,
          item = new { type = "dynamicToolCall", id = dynamicItemId, status = "inProgress", tool = "create_files", arguments }
        }
      });
      var response = new TaskCompletionSource<(bool Success, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
      toolResponses[requestId] = response;
      await SendAsync(new
      {
        method = "item/tool/call",
        id = requestId,
        @params = new { threadId, turnId, callId, tool = "create_files", arguments }
      });
      var result = await response.Task.WaitAsync(cancellationToken);
      await SendAsync(new
      {
        method = "item/completed",
        @params = new
        {
          threadId,
          turnId,
          item = new
          {
            type = "dynamicToolCall",
            id = dynamicItemId,
            status = result.Success ? "completed" : "failed",
            tool = "create_files",
            arguments,
            success = result.Success,
            contentItems = new[] { new { type = "inputText", text = result.Text } }
          }
        }
      });
    }

    if (
      currentRequest.Contains("delete host batch files", StringComparison.OrdinalIgnoreCase)
      || currentRequest.Contains("recover command deletion with host batch", StringComparison.OrdinalIgnoreCase)
    )
    {
      var dynamicItemId = $"dynamic-{turnId}";
      var callId = $"call-{turnId}";
      var requestId = 30_000L + int.Parse(turnId["fake-turn-".Length..], System.Globalization.CultureInfo.InvariantCulture);
      var arguments = new
      {
        paths = new[] { "batch-delete-a.txt", "batch-delete-b.txt" },
        recursive = false
      };
      await SendAsync(new
      {
        method = "item/started",
        @params = new
        {
          threadId,
          turnId,
          item = new { type = "dynamicToolCall", id = dynamicItemId, status = "inProgress", tool = "delete_paths", arguments }
        }
      });
      var response = new TaskCompletionSource<(bool Success, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
      toolResponses[requestId] = response;
      await SendAsync(new
      {
        method = "item/tool/call",
        id = requestId,
        @params = new { threadId, turnId, callId, tool = "delete_paths", arguments }
      });
      var result = await response.Task.WaitAsync(cancellationToken);
      await SendAsync(new
      {
        method = "item/completed",
        @params = new
        {
          threadId,
          turnId,
          item = new
          {
            type = "dynamicToolCall",
            id = dynamicItemId,
            status = result.Success ? "completed" : "failed",
            tool = "delete_paths",
            arguments,
            success = result.Success,
            contentItems = new[] { new { type = "inputText", text = result.Text } }
          }
        }
      });
    }

    if (currentRequest.Contains("delete codex file", StringComparison.OrdinalIgnoreCase))
    {
      var fileItemId = $"file-{turnId}";
      var target = Path.Combine(cwd, "codex-delete.txt");
      var approvalId = 10_000L + int.Parse(turnId["fake-turn-".Length..], System.Globalization.CultureInfo.InvariantCulture);
      var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      approvals[approvalId] = approval;
      await SendAsync(new
      {
        method = "item/started",
        @params = new
        {
          threadId,
          turnId,
          item = new
          {
            type = "fileChange",
            id = fileItemId,
            status = "inProgress",
            changes = new[] { new { path = target, kind = "delete", diff = "--- codex-delete.txt" } }
          }
        }
      });
      await SendAsync(new
      {
        method = "item/fileChange/requestApproval",
        id = approvalId,
        @params = new { threadId, turnId, itemId = fileItemId, reason = "Delete disposable test file" }
      });
      var approved = await approval.Task.WaitAsync(cancellationToken);
      if (approved)
      {
        File.Delete(target);
      }
      await SendAsync(new
      {
        method = "item/completed",
        @params = new
        {
          threadId,
          turnId,
          item = new
          {
            type = "fileChange",
            id = fileItemId,
            status = approved ? "completed" : "declined",
            changes = new[] { new { path = target, kind = "delete", diff = "--- codex-delete.txt" } }
          }
        }
      });
    }

    if (currentRequest.Contains("Benchmark test: FS-CREATE-001", StringComparison.Ordinal))
    {
      await PrepareBenchmarkOutcomeAsync(cwd, model, cancellationToken);
      await SendAsync(new
      {
        method = "turn/diff/updated",
        @params = new { threadId, turnId, diff = "benchmark fixture outcome prepared" }
      });
    }
    else if (currentRequest.Contains("create codex file", StringComparison.OrdinalIgnoreCase))
    {
      await File.WriteAllTextAsync(Path.Combine(cwd, "codex-created.txt"), "created by fake Codex App Server\n", cancellationToken);
      await SendAsync(new { method = "turn/diff/updated", @params = new { threadId, turnId, diff = "+++ codex-created.txt" } });
    }
    else if (currentRequest.Contains("codex second turn", StringComparison.OrdinalIgnoreCase))
    {
      await File.WriteAllTextAsync(Path.Combine(cwd, "codex-created.txt"), "edited on the reused Codex thread\n", cancellationToken);
      await SendAsync(new { method = "turn/diff/updated", @params = new { threadId, turnId, diff = "--- codex-created.txt\n+++ codex-created.txt" } });
    }

    await SendAsync(new
    {
      method = "item/completed",
      @params = new
      {
        threadId,
        turnId,
        item = new { type = "commandExecution", id = itemId, command, cwd, status = "completed", aggregatedOutput = "fake output\n", exitCode = 0, durationMs = 5 }
      }
    });
    await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-{turnId}", delta = "Codex streamed " } });
    await Task.Delay(200, cancellationToken);
    await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-{turnId}", delta = $"with {model} on {threadId}. Ação concluída." } });
    turns.TryRemove(turnId, out _);
    await SendAsync(new
    {
      method = "turn/completed",
      @params = new { threadId, turn = new { id = turnId, status = "completed", error = (object?)null } }
    });
    if (currentRequest.Contains("restart codex after completion", StringComparison.OrdinalIgnoreCase))
    {
      if (!string.IsNullOrWhiteSpace(codexHome))
      {
        await File.WriteAllTextAsync(
          Path.Combine(codexHome, "fake-app-server-exited-after-terminal.marker"),
          threadId
        );
      }
      Environment.Exit(0);
    }
  }
  catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
  {
  }
}

string CurrentUserRequest(string input)
{
  const string marker = "\nCurrent user request:\n";
  var markerIndex = input.LastIndexOf(marker, StringComparison.Ordinal);
  return markerIndex < 0
    ? input
    : input[(markerIndex + marker.Length)..];
}

async Task PrepareBenchmarkOutcomeAsync(
  string cwd,
  string model,
  CancellationToken cancellationToken
)
{
  const string expected = "Agentic Router Benchmark\noperation=create\nresult=success";
  var expectedDirectory = Path.Combine(cwd, "benchmark-data");
  var expectedPath = Path.Combine(expectedDirectory, "result.txt");

  switch (model)
  {
    case "alpha:latest":
      Directory.CreateDirectory(expectedDirectory);
      await File.WriteAllTextAsync(expectedPath, expected, cancellationToken);
      break;
    case "beta:code":
      Directory.CreateDirectory(expectedDirectory);
      await File.WriteAllTextAsync(expectedPath, expected + "!", cancellationToken);
      break;
    case "docs:latest":
      Directory.CreateDirectory(expectedDirectory);
      await File.WriteAllTextAsync(
        Path.Combine(expectedDirectory, "wrong-result.txt"),
        expected,
        cancellationToken
      );
      break;
    case "unused:latest":
      var wrongDirectory = Path.Combine(cwd, "wrong-directory");
      Directory.CreateDirectory(wrongDirectory);
      await File.WriteAllTextAsync(
        Path.Combine(wrongDirectory, "result.txt"),
        expected,
        cancellationToken
      );
      break;
    case "command-r:latest":
      break;
    case "structured-failure:latest":
      Directory.CreateDirectory(expectedDirectory);
      await File.WriteAllTextAsync(expectedPath, expected, cancellationToken);
      await File.WriteAllTextAsync(
        Path.Combine(cwd, "unexpected.txt"),
        "unexpected\n",
        cancellationToken
      );
      break;
    case "structured:latest":
      Directory.CreateDirectory(expectedDirectory);
      await File.WriteAllTextAsync(expectedPath, expected, cancellationToken);
      await File.WriteAllTextAsync(
        Path.Combine(cwd, "fixture", "keep.txt"),
        "fixture-keep-modified\n",
        cancellationToken
      );
      break;
    case "gpt-oss:20b":
      Directory.CreateDirectory(expectedDirectory);
      await File.WriteAllTextAsync(expectedPath, expected, cancellationToken);
      File.Delete(Path.Combine(cwd, "fixture", "delete.txt"));
      break;
    default:
      break;
  }
}

async Task SendAsync(object message)
{
  await outputGate.WaitAsync();
  try
  {
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message));
    await Console.Out.FlushAsync();
  }
  finally
  {
    outputGate.Release();
  }
}

async Task SendRawAsync(string message)
{
  await outputGate.WaitAsync();
  try
  {
    await Console.Out.WriteLineAsync(message);
    await Console.Out.FlushAsync();
  }
  finally
  {
    outputGate.Release();
  }
}
