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
var steerMessages = new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
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
        if (!TryReadContextConfiguration(
          parameters,
          out var contextWindowTokens,
          out var autoCompactTokenLimit
        ))
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the Host-resolved Codex context and 98-percent total compaction limit." } });
          break;
        }
        var dynamicTools = parameters.GetProperty("dynamicTools");
        var dynamicToolNames = dynamicTools.EnumerateArray()
          .Select(tool => tool.GetProperty("name").GetString())
          .ToArray();
        var uniqueDynamicTools = dynamicToolNames.Distinct(StringComparer.Ordinal).ToArray();
        var fullProjection = new[]
        {
          "create_files",
          "delete_paths",
          "run_process",
          "git_status",
          "git_create_commit"
        }.All(expected => dynamicToolNames.Contains(expected, StringComparer.Ordinal));
        var benchmarkProjection = uniqueDynamicTools.Order(StringComparer.Ordinal).SequenceEqual(
          new[]
          {
            "read_file",
            "create_file",
            "create_files",
            "write_file",
            "replace_text",
            "delete_paths"
          }.Order(StringComparer.Ordinal),
          StringComparer.Ordinal
        );
        if (dynamicToolNames.Length > 0
          && (
            uniqueDynamicTools.Length != dynamicToolNames.Length
            || (!fullProjection && !benchmarkProjection)
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
            parameters.GetProperty("permissions").GetString(),
            ":workspace",
            StringComparison.Ordinal
          )
          || parameters.TryGetProperty("sandbox", out _)
          || !HasExactRuntimeWorkspaceRoot(parameters)
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the supported thread policy values." } });
          break;
        }
        var threadId = $"fake-thread-{Interlocked.Increment(ref threadNumber)}";
        var model = parameters.GetProperty("model").GetString();
        var catalogError = "Expected an exact model identifier.";
        if (
          string.IsNullOrWhiteSpace(model)
          || !TryValidateModelCatalog(
            codexHome,
            model,
            contextWindowTokens,
            out catalogError
          )
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = catalogError } });
          break;
        }
        var planSchema = ReadPlanSchema(dynamicTools);
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-thread-request.json"),
            JsonSerializer.Serialize(new
            {
              method,
              cwd = parameters.GetProperty("cwd").GetString(),
              model,
              provider = parameters.GetProperty("modelProvider").GetString(),
              contextWindowTokens,
              autoCompactTokenLimit,
              autoCompactTokenLimitScope = "total",
              planSchema
            })
          );
        }
        await SendAsync(new
        {
          id = id.GetInt64(),
          result = new
          {
            model,
            activePermissionProfile = new { id = ":workspace" },
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
        if (!TryReadContextConfiguration(
          parameters,
          out var contextWindowTokens,
          out var autoCompactTokenLimit
        ))
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the Host-resolved Codex context and 98-percent total compaction limit." } });
          break;
        }
        if (
          !string.Equals(parameters.GetProperty("permissions").GetString(), ":workspace", StringComparison.Ordinal)
          || parameters.TryGetProperty("sandbox", out _)
          || !HasExactRuntimeWorkspaceRoot(parameters)
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the supported resumed thread policy values." } });
          break;
        }
        var threadId = parameters.GetProperty("threadId").GetString()!;
        var model = parameters.GetProperty("model").GetString();
        var catalogError = "Expected an exact model identifier.";
        if (
          string.IsNullOrWhiteSpace(model)
          || !TryValidateModelCatalog(
            codexHome,
            model,
            contextWindowTokens,
            out catalogError
          )
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = catalogError } });
          break;
        }
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-thread-resumed.json"),
            JsonSerializer.Serialize(new
            {
              threadId,
              cwd = parameters.GetProperty("cwd").GetString(),
              model,
              provider = parameters.GetProperty("modelProvider").GetString(),
              contextWindowTokens,
              autoCompactTokenLimit,
              autoCompactTokenLimitScope = "total"
            })
          );
        }
        await SendAsync(new
        {
          id = id.GetInt64(),
          result = new
          {
            model,
            activePermissionProfile = new { id = ":workspace" },
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
        var turnInput = parameters.GetProperty("input");
        var input = turnInput.EnumerateArray()
          .First(item => item.GetProperty("type").GetString() == "text")
          .GetProperty("text")
          .GetString() ?? string.Empty;
        var effort = parameters.TryGetProperty("effort", out var effortElement)
          ? effortElement.GetString()
          : null;
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-turn-request.json"),
            JsonSerializer.Serialize(new { threadId, effort })
          );
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-turn-input.txt"),
            input
          );
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-turn-images.json"),
            JsonSerializer.Serialize(
              new
              {
                images = turnInput.EnumerateArray()
                  .Where(item => item.GetProperty("type").GetString() == "image")
                  .Select(item => new
                  {
                    type = item.GetProperty("type").GetString(),
                    detail = item.GetProperty("detail").GetString(),
                    urlPrefix = (item.GetProperty("url").GetString() ?? string.Empty)[..Math.Min(
                      32,
                      (item.GetProperty("url").GetString() ?? string.Empty).Length
                    )]
                  })
                  .ToArray()
              }
            )
          );
        }
        var cwd = parameters.GetProperty("cwd").GetString()!;
        var model = parameters.GetProperty("model").GetString()!;
        if (
          parameters.GetProperty("approvalPolicy").GetString() is not ("on-request" or "never")
          || !string.Equals(
            parameters.GetProperty("permissions").GetString(),
            ":workspace",
            StringComparison.Ordinal
          )
          || parameters.TryGetProperty("sandboxPolicy", out _)
          || !HasExactRuntimeWorkspaceRoot(parameters)
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32600, message = "Expected the supported turn policy values." } });
          break;
        }
        var source = new CancellationTokenSource();
        turns[turnId] = source;
        steerMessages[turnId] = new TaskCompletionSource<string>(
          TaskCreationOptions.RunContinuationsAsynchronously
        );
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
        steerMessages.TryRemove(turnId, out _);
        await SendAsync(new { id = id.GetInt64(), result = new { } });
        await SendAsync(new
        {
          method = "turn/completed",
          @params = new { threadId, turn = new { id = turnId, status = "interrupted", error = (object?)null } }
        });
        break;
      }
    case "turn/steer":
      {
        var threadId = parameters.GetProperty("threadId").GetString()!;
        var turnId = parameters.GetProperty("expectedTurnId").GetString()!;
        var message = parameters.GetProperty("input").EnumerateArray()
          .First(item => item.GetProperty("type").GetString() == "text")
          .GetProperty("text")
          .GetString() ?? string.Empty;
        if (
          !turns.ContainsKey(turnId)
          || !steerMessages.TryGetValue(turnId, out var pendingSteer)
          || string.IsNullOrWhiteSpace(message)
        )
        {
          await SendAsync(new { id = id.GetInt64(), error = new { code = -32602, message = "Expected an active turn and non-empty steering input." } });
          break;
        }
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
          await File.WriteAllTextAsync(
            Path.Combine(codexHome, "fake-app-server-steer.json"),
            JsonSerializer.Serialize(new { threadId, turnId, message })
          );
        }
        pendingSteer.TrySetResult(message);
        await SendAsync(new
        {
          id = id.GetInt64(),
          result = new { turnId }
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

static bool HasExactRuntimeWorkspaceRoot(JsonElement parameters)
{
  var cwd = parameters.GetProperty("cwd").GetString();
  return parameters.TryGetProperty("runtimeWorkspaceRoots", out var roots)
    && roots.ValueKind == JsonValueKind.Array
    && roots.GetArrayLength() == 1
    && string.Equals(roots[0].GetString(), cwd, StringComparison.OrdinalIgnoreCase);
}

static bool TryReadContextConfiguration(
  JsonElement parameters,
  out int contextWindowTokens,
  out int autoCompactTokenLimit
)
{
  contextWindowTokens = 0;
  autoCompactTokenLimit = 0;
  if (
    !parameters.TryGetProperty("config", out var config)
    || config.ValueKind != JsonValueKind.Object
    || !config.TryGetProperty("model_context_window", out var contextWindow)
    || !contextWindow.TryGetInt32(out contextWindowTokens)
    || contextWindowTokens <= 0
    || !config.TryGetProperty("model_auto_compact_token_limit", out var compactLimit)
    || !compactLimit.TryGetInt32(out autoCompactTokenLimit)
    || autoCompactTokenLimit != (int)((long)contextWindowTokens * 98 / 100)
    || !config.TryGetProperty("model_auto_compact_token_limit_scope", out var compactScope)
    || !string.Equals(compactScope.GetString(), "total", StringComparison.Ordinal)
  )
  {
    return false;
  }
  return true;
}

static object? ReadPlanSchema(JsonElement dynamicTools)
{
  var planTool = dynamicTools.EnumerateArray().FirstOrDefault(
    tool => string.Equals(
      tool.GetProperty("name").GetString(),
      "create_execution_plan",
      StringComparison.Ordinal
    )
  );
  if (planTool.ValueKind == JsonValueKind.Undefined)
  {
    return null;
  }
  var properties = planTool.GetProperty("inputSchema").GetProperty("properties");
  var steps = properties.GetProperty("steps");
  var stepProperties = steps.GetProperty("items").GetProperty("properties");
  return new
  {
    objectiveMaximumLength = properties.GetProperty("objective").GetProperty("maxLength").GetInt32(),
    maximumSteps = steps.GetProperty("maxItems").GetInt32(),
    titleMaximumLength = stepProperties.GetProperty("title").GetProperty("maxLength").GetInt32(),
    maximumDependencies = stepProperties.GetProperty("dependsOn").GetProperty("maxItems").GetInt32()
  };
}

static bool TryValidateModelCatalog(
  string? codexHome,
  string model,
  int contextWindowTokens,
  out string error
)
{
  error = "Expected exact Agentic Router local-model metadata in the isolated Codex catalog.";
  if (string.IsNullOrWhiteSpace(codexHome))
  {
    return false;
  }
  var configPath = Path.Combine(codexHome, "config.toml");
  var expectedCatalogPath = Path.Combine(codexHome, "model-catalog.json");
  if (!File.Exists(configPath) || !File.Exists(expectedCatalogPath))
  {
    return false;
  }
  var expectedConfigEntry = $"model_catalog_json = \"{Path.GetFullPath(expectedCatalogPath).Replace('\\', '/')}\"";
  if (!File.ReadAllText(configPath).Contains(expectedConfigEntry, StringComparison.Ordinal))
  {
    return false;
  }
  using var document = JsonDocument.Parse(File.ReadAllText(expectedCatalogPath));
  var entry = document.RootElement.GetProperty("models").EnumerateArray().FirstOrDefault(
    candidate => string.Equals(
      candidate.GetProperty("slug").GetString(),
      model,
      StringComparison.Ordinal
    )
  );
  if (entry.ValueKind == JsonValueKind.Undefined)
  {
    return false;
  }
  var modalities = entry.GetProperty("input_modalities").EnumerateArray()
    .Select(item => item.GetString())
    .ToArray();
  var efforts = entry.GetProperty("supported_reasoning_levels").EnumerateArray()
    .Select(item => item.GetProperty("effort").GetString())
    .ToArray();
  var exposesReasoning = entry.TryGetProperty(
    "default_reasoning_level",
    out var defaultReasoningLevel
  );
  var validReasoning = exposesReasoning
    ? string.Equals(defaultReasoningLevel.GetString(), "medium", StringComparison.Ordinal)
      && efforts.SequenceEqual(new[] { "low", "medium", "high" }, StringComparer.Ordinal)
    : efforts.Length == 0;
  var valid = entry.GetProperty("context_window").GetInt32() == contextWindowTokens
    && entry.GetProperty("max_context_window").GetInt32() == contextWindowTokens
    && entry.GetProperty("effective_context_window_percent").GetInt32() == 100
    && string.Equals(entry.GetProperty("shell_type").GetString(), "shell_command", StringComparison.Ordinal)
    && entry.GetProperty("supported_in_api").GetBoolean()
    && validReasoning
    && modalities.Contains("text", StringComparer.Ordinal)
    && !string.IsNullOrWhiteSpace(entry.GetProperty("base_instructions").GetString());
  if (valid)
  {
    error = string.Empty;
  }
  return valid;
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
  FileStream? observationLock = null;
  try
  {
    var currentRequest = CurrentUserRequest(input);
    var recoveryContinuation = input.Contains(
      "\nHost recovery continuation:\n",
      StringComparison.Ordinal
    );
    await SendAsync(new { method = "turn/started", @params = new { threadId, turn = new { id = turnId, status = "inProgress" } } });
    await SendAsync(new { method = "item/reasoning/summaryTextDelta", @params = new { threadId, turnId, itemId = $"reason-{turnId}", delta = "Inspecting — revisão " } });

    if (currentRequest.Contains("codex live context usage", StringComparison.OrdinalIgnoreCase))
    {
      await SendAsync(new
      {
        method = "thread/tokenUsage/updated",
        @params = new
        {
          threadId,
          turnId,
          tokenUsage = new
          {
            last = new
            {
              inputTokens = 29_000,
              outputTokens = 1_000,
              totalTokens = 30_000,
              cachedInputTokens = 0,
              cacheWriteInputTokens = 0,
              reasoningOutputTokens = 1_000
            },
            total = new
            {
              inputTokens = 29_000,
              outputTokens = 1_000,
              totalTokens = 30_000,
              cachedInputTokens = 0,
              cacheWriteInputTokens = 0,
              reasoningOutputTokens = 1_000
            },
            modelContextWindow = 32_768
          }
        }
      });
      await Task.Delay(1_500, cancellationToken);
    }

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

    if (
      currentRequest.Contains("codex host idle timeout", StringComparison.OrdinalIgnoreCase)
      && !recoveryContinuation
    )
    {
      await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
      return;
    }

    if (
      currentRequest.Contains("codex persistent idle failure", StringComparison.OrdinalIgnoreCase)
    )
    {
      await CompleteFailedTurnAsync(
        threadId,
        turnId,
        "stream disconnected before completion: idle timeout waiting for SSE"
      );
      return;
    }

    if (
      currentRequest.Contains("codex provider disconnected", StringComparison.OrdinalIgnoreCase)
      && !recoveryContinuation
    )
    {
      await CompleteFailedTurnAsync(
        threadId,
        turnId,
        "stream disconnected before completion: connection reset by peer"
      );
      return;
    }

    if (
      currentRequest.Contains("codex app server exit recovery", StringComparison.OrdinalIgnoreCase)
      && !recoveryContinuation
    )
    {
      Environment.Exit(23);
    }

    if (
      currentRequest.Contains("codex native idle failure", StringComparison.OrdinalIgnoreCase)
      && !recoveryContinuation
    )
    {
      var turnIndex = int.Parse(
        turnId["fake-turn-".Length..],
        System.Globalization.CultureInfo.InvariantCulture
      );
      var acceptedPlan = await CallDynamicToolAsync(
        threadId,
        turnId,
        "create_execution_plan",
        new
        {
          objective = "Resume a partially completed Codex plan after a transient failure",
          steps = new[]
          {
            new { title = "Create the first recovery fixture" },
            new { title = "Create the second recovery fixture" }
          }
        },
        70_000L + turnIndex,
        cancellationToken
      );
      if (!acceptedPlan.Success)
      {
        await CompleteFailedTurnAsync(threadId, turnId, acceptedPlan.Text);
        return;
      }
      var firstStep = await CallDynamicToolAsync(
        threadId,
        turnId,
        "create_files",
        new
        {
          files = new[]
          {
            new
            {
              path = "codex-transient-step-1.txt",
              content = "completed before the transient failure\n"
            }
          },
          stepId = "step-1"
        },
        71_000L + turnIndex,
        cancellationToken
      );
      if (!firstStep.Success)
      {
        await CompleteFailedTurnAsync(threadId, turnId, firstStep.Text);
        return;
      }
      await CompleteFailedTurnAsync(
        threadId,
        turnId,
        "stream disconnected before completion: idle timeout waiting for SSE"
      );
      return;
    }

    if (currentRequest.Contains("codex incident reasoning flood", StringComparison.OrdinalIgnoreCase))
    {
      for (var index = 0; index < 150; index++)
      {
        await SendAsync(new
        {
          method = "item/reasoning/summaryTextDelta",
          @params = new
          {
            threadId,
            turnId,
            itemId = $"reason-flood-{turnId}",
            delta = "x"
          }
        });
      }
      for (var index = 0; index < 120; index++)
      {
        await SendAsync(new
        {
          method = "warning",
          @params = new
          {
            threadId,
            turnId,
            message = $"Retained incident saturation marker {index}."
          }
        });
      }
    }

    if (currentRequest.Contains("chronological codex content", StringComparison.OrdinalIgnoreCase))
    {
      await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-first-{turnId}", delta = "First **response** " } });
      await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-first-{turnId}", delta = "segment." } });
      await SendAsync(new { method = "item/reasoning/summaryTextDelta", @params = new { threadId, turnId, itemId = $"reason-second-{turnId}", delta = "Thinking again after " } });
      await SendAsync(new { method = "item/reasoning/summaryTextDelta", @params = new { threadId, turnId, itemId = $"reason-second-{turnId}", delta = "the first response." } });
    }

    if (currentRequest.Contains("long codex turn", StringComparison.OrdinalIgnoreCase))
    {
      var steering = await steerMessages[turnId].Task.WaitAsync(cancellationToken);
      await SendAsync(new
      {
        method = "item/reasoning/summaryTextDelta",
        @params = new
        {
          threadId,
          turnId,
          itemId = $"reason-steer-{turnId}",
          delta = $"Steering accepted: {steering}"
        }
      });
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

    if (
      recoveryContinuation
      && currentRequest.Contains("codex native idle failure", StringComparison.OrdinalIgnoreCase)
    )
    {
      var validRecoveryPrompt = currentRequest.Contains(
        "Failure code: codex-provider-stream-idle-timeout.",
        StringComparison.Ordinal
      )
        && currentRequest.Contains(
          "Actual cause: stream disconnected before completion: idle timeout waiting for SSE",
          StringComparison.Ordinal
        )
        && currentRequest.Contains(
          "codex-transient-step-1.txt [created]",
          StringComparison.Ordinal
        )
        && currentRequest.Contains(
          "step-1: Create the first recovery fixture",
          StringComparison.Ordinal
        )
        && currentRequest.Contains(
          "step-2: Create the second recovery fixture [pending]",
          StringComparison.Ordinal
        )
        && currentRequest.Contains(
          "Do not repeat completed actions",
          StringComparison.Ordinal
        );
      if (!validRecoveryPrompt)
      {
        await CompleteFailedTurnAsync(
          threadId,
          turnId,
          "The automatic continuation prompt omitted the actual cause or authoritative Host state."
        );
        return;
      }
      var turnIndex = int.Parse(
        turnId["fake-turn-".Length..],
        System.Globalization.CultureInfo.InvariantCulture
      );
      var secondStep = await CallDynamicToolAsync(
        threadId,
        turnId,
        "create_files",
        new
        {
          files = new[]
          {
            new
            {
              path = "codex-transient-step-2.txt",
              content = "completed by the automatic continuation\n"
            }
          },
          stepId = "step-2"
        },
        72_000L + turnIndex,
        cancellationToken
      );
      if (!secondStep.Success)
      {
        await CompleteFailedTurnAsync(threadId, turnId, secondStep.Text);
        return;
      }
    }

    if (currentRequest.Contains("codex plan automatic validation", StringComparison.OrdinalIgnoreCase))
    {
      var turnIndex = int.Parse(
        turnId["fake-turn-".Length..],
        System.Globalization.CultureInfo.InvariantCulture
      );
      var plan = new
      {
        objective = "Create one verified file while preserving a pending plan step",
        steps = new[]
        {
          new { title = "Create the requested validation fixture" },
          new { title = "Preserve the remaining follow-up work" }
        }
      };
      var acceptedPlan = await CallDynamicToolAsync(
        threadId,
        turnId,
        "create_execution_plan",
        plan,
        60_000L + turnIndex,
        cancellationToken
      );
      if (!acceptedPlan.Success)
      {
        throw new InvalidOperationException(
          $"The fake Host plan was rejected: {acceptedPlan.Text}"
        );
      }

      var unboundSpecialistAction = await CallDynamicToolAsync(
        threadId,
        turnId,
        "list_files",
        new
        {
          path = ".",
          recursive = false
        },
        61_000L + turnIndex,
        cancellationToken
      );
      if (!unboundSpecialistAction.Success)
      {
        throw new InvalidOperationException(
          $"The unbound specialist inspection was not auto-bound to the sole actionable step: {unboundSpecialistAction.Text}"
        );
      }

      var created = await CallDynamicToolAsync(
        threadId,
        turnId,
        "create_files",
        new
        {
          files = new[]
          {
            new
            {
              path = "codex-plan-validation.txt",
              content = "created before automatic Host validation\n"
            }
          },
          stepId = "step-1"
        },
        62_000L + turnIndex,
        cancellationToken
      );
      if (!created.Success)
      {
        throw new InvalidOperationException(
          $"The terminal-step fixture creation was not corrected to the sole actionable step: {created.Text}"
        );
      }
    }

    if (currentRequest.Contains("codex expanded plan limits", StringComparison.OrdinalIgnoreCase))
    {
      const string titlePrefix = "Detailed validation stage 01 ";
      var maximumTitle = titlePrefix + new string('x', 160 - titlePrefix.Length);
      var plan = new
      {
        objective = new string('o', 500),
        steps = Enumerable.Range(1, 20)
          .Select(index => new
          {
            title = index == 1
              ? maximumTitle
              : $"Detailed validation stage {index:00}"
          })
          .ToArray()
      };
      var acceptedPlan = await CallDynamicToolAsync(
        threadId,
        turnId,
        "create_execution_plan",
        plan,
        63_000L + int.Parse(
          turnId["fake-turn-".Length..],
          System.Globalization.CultureInfo.InvariantCulture
        ),
        cancellationToken
      );
      if (!acceptedPlan.Success)
      {
        throw new InvalidOperationException(
          $"The expanded Host plan was rejected: {acceptedPlan.Text}"
        );
      }
    }

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

    if (currentRequest.Contains("web host bridge codex", StringComparison.OrdinalIgnoreCase))
    {
      var result = await CallDynamicToolAsync(
        threadId,
        turnId,
        "agentic_router_web_search",
        new { query = "generic Host web capability" },
        50_000L + int.Parse(turnId["fake-turn-".Length..], System.Globalization.CultureInfo.InvariantCulture),
        cancellationToken
      );
      if (!string.IsNullOrWhiteSpace(codexHome))
      {
        await File.WriteAllTextAsync(
          Path.Combine(codexHome, "fake-codex-host-web.json"),
          JsonSerializer.Serialize(new { succeeded = result.Success, output = result.Text }),
          cancellationToken
        );
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

    if (
      currentRequest.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal)
      && model is "unused:latest" or "docs:latest"
    )
    {
      await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }
    if (
      currentRequest.Contains("Benchmark test: FS-UPDATE-001", StringComparison.Ordinal)
      && string.Equals(model, "structured-failure:latest", StringComparison.Ordinal)
    )
    {
      turns.TryRemove(turnId, out _);
      await SendAsync(new
      {
        method = "turn/completed",
        @params = new
        {
          threadId,
          turn = new
          {
            id = turnId,
            status = "failed",
            error = new { codexErrorInfo = new { type = "fake-benchmark-failure" } }
          }
        }
      });
      return;
    }
    if (currentRequest.Contains("Benchmark test: FS-", StringComparison.Ordinal))
    {
      await PrepareBenchmarkOutcomeAsync(cwd, model, currentRequest, cancellationToken);
      await SendAsync(new
      {
        method = "turn/diff/updated",
        @params = new { threadId, turnId, diff = "benchmark fixture outcome prepared" }
      });
    }
    else if (currentRequest.Contains("Benchmark scenario: CONTINUITY-001", StringComparison.Ordinal))
    {
      var path = Path.Combine(cwd, "app", "config.txt");
      var content = await File.ReadAllTextAsync(path, cancellationToken);
      content = currentRequest.Contains("Set only title=ORION", StringComparison.Ordinal)
        ? content.Replace("title=Atlas", "title=ORION", StringComparison.Ordinal)
        : currentRequest.Contains("Enable the same application", StringComparison.Ordinal)
          ? content.Replace("enabled=false", "enabled=true", StringComparison.Ordinal)
          : content.Replace("theme=amber", "theme=violet", StringComparison.Ordinal);
      await File.WriteAllTextAsync(path, content, cancellationToken);
      await SendAsync(new
      {
        method = "turn/diff/updated",
        @params = new { threadId, turnId, diff = "continuity fixture updated" }
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
    else if (currentRequest.Contains("codex transient observation lock", StringComparison.OrdinalIgnoreCase))
    {
      var lockedPath = Path.Combine(cwd, "codex-transient-observation.txt");
      await File.WriteAllTextAsync(
        lockedPath,
        "completed before the transient observation lock\n",
        cancellationToken
      );
      observationLock = new FileStream(
        lockedPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None
      );
      await SendAsync(new { method = "turn/diff/updated", @params = new { threadId, turnId, diff = "+++ codex-transient-observation.txt" } });
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
    var finalReport = currentRequest.Contains("Benchmark scenario: CONTINUITY-001", StringComparison.Ordinal)
      ? (currentRequest.Contains("theme to violet", StringComparison.Ordinal)
        ? "turn-3=completed"
        : currentRequest.Contains("Enable the same application", StringComparison.Ordinal)
          ? "turn-2=completed"
          : "turn-1=completed") + $" on {threadId}"
      : currentRequest.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal)
      ? string.Equals(model, "beta:code", StringComparison.Ordinal)
        ? "codename=ORBIT-41"
        : "codename=ORBIT-41\nverification-word=marigold"
      : $"Codex streamed with {model} on {threadId}. Ação concluída.";
    await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-{turnId}", delta = finalReport[..Math.Min(finalReport.Length, 20)] } });
    await Task.Delay(200, cancellationToken);
    await SendAsync(new { method = "item/agentMessage/delta", @params = new { threadId, turnId, itemId = $"answer-{turnId}", delta = finalReport[Math.Min(finalReport.Length, 20)..] } });
    turns.TryRemove(turnId, out _);
    steerMessages.TryRemove(turnId, out _);
    await SendAsync(new
    {
      method = "turn/completed",
      @params = new { threadId, turn = new { id = turnId, status = "completed", error = (object?)null } }
    });
    if (observationLock is not null)
    {
      _ = ReleaseObservationLockAsync(observationLock);
      observationLock = null;
    }
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
    steerMessages.TryRemove(turnId, out _);
    if (observationLock is not null)
    {
      await observationLock.DisposeAsync();
    }
  }
}

async Task ReleaseObservationLockAsync(FileStream stream)
{
  await Task.Delay(60);
  await stream.DisposeAsync();
}

async Task CompleteFailedTurnAsync(
  string threadId,
  string turnId,
  string message
)
{
  turns.TryRemove(turnId, out _);
  await SendAsync(new
  {
    method = "turn/completed",
    @params = new
    {
      threadId,
      turn = new
      {
        id = turnId,
        status = "failed",
        error = new
        {
          message,
          codexErrorInfo = new { type = "other" }
        }
      }
    }
  });
}

string CurrentUserRequest(string input)
{
  const string marker = "\nCurrent user request:\n";
  var markerIndex = input.LastIndexOf(marker, StringComparison.Ordinal);
  return markerIndex < 0
    ? input
    : input[(markerIndex + marker.Length)..];
}

async Task<(bool Success, string Text)> CallDynamicToolAsync(
  string threadId,
  string turnId,
  string tool,
  object arguments,
  long requestId,
  CancellationToken cancellationToken
)
{
  var dynamicItemId = $"dynamic-{tool}-{turnId}";
  var callId = $"call-{tool}-{turnId}";
  await SendAsync(new
  {
    method = "item/started",
    @params = new
    {
      threadId,
      turnId,
      item = new
      {
        type = "dynamicToolCall",
        id = dynamicItemId,
        status = "inProgress",
        tool,
        arguments
      }
    }
  });
  var response = new TaskCompletionSource<(bool Success, string Text)>(
    TaskCreationOptions.RunContinuationsAsynchronously
  );
  toolResponses[requestId] = response;
  await SendAsync(new
  {
    method = "item/tool/call",
    id = requestId,
    @params = new
    {
      threadId,
      turnId,
      callId,
      tool,
      arguments
    }
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
        tool,
        arguments,
        success = result.Success,
        contentItems = new[]
        {
          new
          {
            type = "inputText",
            text = result.Text
          }
        }
      }
    }
  });
  return result;
}

async Task PrepareBenchmarkOutcomeAsync(
  string cwd,
  string model,
  string request,
  CancellationToken cancellationToken
)
{
  if (request.Contains("Benchmark test: FS-READ-001", StringComparison.Ordinal))
  {
    return;
  }
  if (request.Contains("Benchmark test: FS-UPDATE-001", StringComparison.Ordinal))
  {
    var updatePath = Path.Combine(cwd, "fixture", "update.txt");
    var content = string.Equals(model, "beta:code", StringComparison.Ordinal)
      ? "mode=preview\nretries=4\nowner=router\n"
      : "mode=preview\nretries=3\nowner=router\n";
    await File.WriteAllTextAsync(updatePath, content, cancellationToken);
    return;
  }
  if (request.Contains("Benchmark test: FS-DELETE-001", StringComparison.Ordinal))
  {
    var deletePath = Path.Combine(cwd, "fixture", "delete.txt");
    if (!string.Equals(model, "beta:code", StringComparison.Ordinal))
    {
      File.Delete(deletePath);
    }
    return;
  }

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
