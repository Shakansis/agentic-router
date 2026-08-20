using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.EndToEndTests;

internal sealed class FakeOllamaServer : IAsyncDisposable
{
  private static readonly JsonSerializerOptions CompactJsonOptions = new(
    JsonSerializerDefaults.Web
  );
  private static readonly ToolNameResolver ToolNames = new();

  private static readonly ModelDefinition[] Models =
  [
    new(
      "router:latest",
      1_000_000_000L
    ),
    new(
      "functiongemma:270m",
      270_000_000L
    ),
    new(
      "qwen3-coder:30b",
      18_000_000_000L
    ),
    new(
      "gpt-oss:20b",
      14_000_000_000L
    ),
    new(
      "alpha:latest",
      4_200_000_000L
    ),
    new(
      "qwen3.8:27b-gpu0",
      17_741_872_167L
    ),
    new(
      "docs:latest",
      5_100_000_000L
    ),
    new(
      "beta:code",
      7_300_000_000L
    ),
    new(
      "unused:latest",
      2_300_000_000L
    ),
    new(
      "command-r:latest",
      6_800_000_000L
    ),
    new(
      "structured-failure:latest",
      2_100_000_000L
    ),
    new(
      "structured:latest",
      2_200_000_000L
    )
  ];

  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _shutdown = new();
  private readonly ConcurrentQueue<RecordedChatRequest> _requests = new();
  private readonly ConcurrentQueue<RecordedChatRequest> _allRequests = new();
  private readonly ConcurrentQueue<string> _capabilityQueries = new();
  private readonly ConcurrentQueue<string> _errors = new();
  private readonly ConcurrentDictionary<string, RunningModel> _loaded = new(
    StringComparer.Ordinal
  );
  private readonly ConcurrentDictionary<string, int> _generationAttempts = new(
    StringComparer.Ordinal
  );
  private readonly object _residencyGate = new();
  private string _protocolVersion = "0.13.5-test";
  private string? _adaptiveConformanceModel;
  private string? _evictOnNextLoadedModel;
  private string? _evictOnNextRemovedModel;
  private Task? _listenTask;

  private FakeOllamaServer(
    int port
  )
  {
    BaseUrl = $"http://127.0.0.1:{port}";
    _listener.Prefixes.Add(
      $"{BaseUrl}/"
    );
  }

  public string BaseUrl { get; }

  public IReadOnlyList<RecordedChatRequest> Requests => _requests.ToArray();

  public IReadOnlyList<RecordedChatRequest> AllRequests => _allRequests.ToArray();

  public IReadOnlyList<string> CapabilityQueries => _capabilityQueries.ToArray();

  public IReadOnlyList<string> Errors => _errors.ToArray();

  public IReadOnlyCollection<string> LoadedModels => _loaded.Keys.ToArray();

  public static FakeOllamaServer Start()
  {
    var server = new FakeOllamaServer(
      GetAvailablePort()
    );
    server._listener.Start();
    server._listenTask = server.ListenAsync(
      server._shutdown.Token
    );
    return server;
  }

  public void Reset()
  {
    _requests.Clear();
    _capabilityQueries.Clear();
    _errors.Clear();
    _generationAttempts.Clear();
    _protocolVersion = "0.13.5-test";
    _adaptiveConformanceModel = null;
    _evictOnNextLoadedModel = null;
    _evictOnNextRemovedModel = null;
  }

  public void EvictModelOnNextLoad(
    string loadedModel,
    string removedModel
  )
  {
    lock (_residencyGate)
    {
      _evictOnNextLoadedModel = loadedModel;
      _evictOnNextRemovedModel = removedModel;
    }
  }

  public void EnableAdaptiveConformanceFixture(
    string model
  )
  {
    _adaptiveConformanceModel = model;
  }

  public void SetProtocolIdentity(
    string version
  )
  {
    _protocolVersion = version;
  }

  public void RemoveLoadedModel(
    string model
  )
  {
    _loaded.TryRemove(
      model,
      out _
    );
  }

  public void SetLoadedModelContext(
    string model,
    int contextTokens
  )
  {
    AddLoadedModel(
      model,
      -1,
      contextTokens
    );
  }

  public async ValueTask DisposeAsync()
  {
    await _shutdown.CancelAsync();
    _listener.Close();

    if (_listenTask is not null)
    {
      try
      {
        await _listenTask;
      }
      catch (OperationCanceledException)
      {
      }
      catch (ObjectDisposedException)
      {
      }
      catch (HttpListenerException)
      {
      }
    }

    _shutdown.Dispose();
  }

  private async Task ListenAsync(
    CancellationToken cancellationToken
  )
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      var context = await _listener.GetContextAsync()
        .WaitAsync(
          cancellationToken
        );
      _ = HandleAsync(
        context,
        cancellationToken
      );
    }
  }

  private async Task HandleAsync(
    HttpListenerContext context,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var path = context.Request.Url?.AbsolutePath;

      if (
        context.Request.HttpMethod == HttpMethod.Get.Method
        && path == "/api/version"
      )
      {
        await WriteJsonAsync(
          context.Response,
          HttpStatusCode.OK,
          new
          {
            version = _protocolVersion
          },
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Get.Method
        && path == "/api/tags"
      )
      {
        await WriteJsonAsync(
          context.Response,
          HttpStatusCode.OK,
          new
          {
            models = Models.Select(
              model => new
              {
                name = model.Name,
                size = model.Size,
                modified_at = "2026-07-28T10:00:00Z",
                digest = $"digest-{model.Name}"
              }
            )
          },
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Get.Method
        && path == "/api/ps"
      )
      {
        await WriteJsonAsync(
          context.Response,
          HttpStatusCode.OK,
          new
          {
            models = _loaded.Values.Select(
              model => new
              {
                name = model.Name,
                digest = model.Digest,
                size = model.Size,
                size_vram = model.VramSize,
                context_length = model.ContextTokens,
                expires_at = model.ExpiresAt
              }
            )
          },
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Post.Method
        && path == "/api/show"
      )
      {
        await HandleShowAsync(
          context,
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Post.Method
        && path == "/api/chat"
      )
      {
        await HandleChatAsync(
          context,
          cancellationToken
        );
        return;
      }

      await WriteJsonAsync(
        context.Response,
        HttpStatusCode.NotFound,
        new
        {
          error = "not found"
        },
        cancellationToken
      );
    }
    catch (OperationCanceledException)
    {
      context.Response.Abort();
    }
    catch (HttpListenerException)
    {
      context.Response.Abort();
    }
    catch (Exception exception)
    {
      _errors.Enqueue(
        exception.ToString()
      );
      context.Response.Abort();
    }
  }

  private async Task HandleShowAsync(
    HttpListenerContext context,
    CancellationToken cancellationToken
  )
  {
    using var document = await JsonDocument.ParseAsync(
      context.Request.InputStream,
      cancellationToken: cancellationToken
    );
    var model = document.RootElement.GetProperty(
      "model"
    ).GetString() ?? string.Empty;
    _capabilityQueries.Enqueue(
      model
    );

    if (!Models.Any(
      candidate => candidate.Name == model
    ))
    {
      await WriteJsonAsync(
        context.Response,
        HttpStatusCode.NotFound,
        new
        {
          error = $"model '{model}' not found"
        },
        cancellationToken
      );
      return;
    }

    if (string.Equals(
      model,
      "beta:code",
      StringComparison.Ordinal
    ) && string.Equals(
      context.Request.Headers["X-Agentic-Router-Operation"],
      "model-capability-inspection",
      StringComparison.Ordinal
    ))
    {
      await WriteJsonAsync(
        context.Response,
        HttpStatusCode.InternalServerError,
        new
        {
          error = "capability inspection fixture failure"
        },
        cancellationToken
      );
      return;
    }

    await WriteJsonAsync(
      context.Response,
      HttpStatusCode.OK,
      new
      {
        model_info = new Dictionary<string, object>
        {
          ["general.context_length"] = string.Equals(
            model,
            "qwen3.8:27b-gpu0",
            StringComparison.Ordinal
          )
            ? 262_144
            : 65_536
        },
        details = new
        {
          format = "gguf",
          family = "qwen3",
          families = new[]
          {
            "qwen3"
          },
          parameter_size = "8B",
          quantization_level = "Q4_K_M"
        },
        capabilities = model is "alpha:latest" or "qwen3.8:27b-gpu0"
          ? new[]
          {
            "completion",
            "vision"
          }
          : model is "command-r:latest" or "router:latest" or "functiongemma:270m" or "qwen3-coder:30b" or "gpt-oss:20b" or "unused:latest" or "structured:latest"
          ? new[]
          {
            "completion",
            "tools"
          }
          : ["completion"]
      },
      cancellationToken
    );
  }

  private async Task HandleChatAsync(
    HttpListenerContext context,
    CancellationToken cancellationToken
  )
  {
    using var document = await JsonDocument.ParseAsync(
      context.Request.InputStream,
      cancellationToken: cancellationToken
    );
    var root = document.RootElement;
    var model = root.GetProperty(
      "model"
    ).GetString() ?? string.Empty;
    var stream = root.GetProperty(
      "stream"
    ).GetBoolean();
    var hasTools = root.TryGetProperty(
      "tools",
      out var toolsElement
    ) && toolsElement.ValueKind == JsonValueKind.Array;
    var availableTools = hasTools
      ? toolsElement.EnumerateArray()
        .Select(
          tool => tool.GetProperty(
            "function"
          ).GetProperty(
            "name"
          ).GetString() ?? string.Empty
        ).ToArray()
      : [];
    var keepAlive = root.TryGetProperty(
      "keep_alive",
      out var keepAliveElement
    )
      ? keepAliveElement.GetInt32()
      : (int?)null;
    var messages = root.GetProperty(
      "messages"
    ).EnumerateArray()
      .Select(
        message => new RecordedMessage(
          message.GetProperty(
            "role"
          ).GetString() ?? string.Empty,
          message.TryGetProperty(
            "content",
            out var contentElement
          )
            ? contentElement.GetString() ?? string.Empty
            : string.Empty,
          message.TryGetProperty(
            "tool_name",
            out var toolNameElement
          )
            ? toolNameElement.GetString()
            : null,
          message.TryGetProperty(
            "tool_calls",
            out var toolCallsElement
          ) && toolCallsElement.ValueKind == JsonValueKind.Array
            ? toolCallsElement.EnumerateArray()
              .Select(
                call => new RecordedToolCall(
                  call.GetProperty(
                    "function"
                  ).GetProperty(
                    "name"
                  ).GetString() ?? string.Empty,
                  call.GetProperty(
                    "function"
                  ).GetProperty(
                    "arguments"
                  ).Clone()
                )
              ).ToArray()
            : [],
          message.TryGetProperty(
            "images",
            out var imagesElement
          ) && imagesElement.ValueKind == JsonValueKind.Array
            ? imagesElement.GetArrayLength()
            : 0
        )
      )
      .ToArray();
    int? contextTokens = root.TryGetProperty(
      "options",
      out var options
    ) && options.TryGetProperty(
      "num_ctx",
      out var contextTokensElement
    )
      ? contextTokensElement.GetInt32()
      : null;
    int? predictTokens = options.ValueKind == JsonValueKind.Object
      && options.TryGetProperty(
        "num_predict",
        out var predictTokensElement
      )
        ? predictTokensElement.GetInt32()
        : null;
    int? mainGpu = options.ValueKind == JsonValueKind.Object
      && options.TryGetProperty(
        "main_gpu",
        out var mainGpuElement
      )
        ? mainGpuElement.GetInt32()
        : null;
    var recorded = new RecordedChatRequest(
      model,
      stream,
      keepAlive,
      messages,
      hasTools,
      availableTools,
      contextTokens,
      predictTokens,
      mainGpu
    );
    _requests.Enqueue(
      recorded
    );
    _allRequests.Enqueue(
      recorded
    );

    if (!Models.Any(
      candidate => candidate.Name == model
    ))
    {
      await WriteJsonAsync(
        context.Response,
        HttpStatusCode.NotFound,
        new
        {
          error = $"model '{model}' not found"
        },
        cancellationToken
      );
      return;
    }

    if (messages.Length == 0 && keepAlive is not null)
    {
      UpdateResidency(
        model,
        keepAlive.Value,
        contextTokens
      );
      await WriteJsonAsync(
        context.Response,
        HttpStatusCode.OK,
        new
        {
          model,
          done = true
        },
        cancellationToken
      );
      return;
    }

    if (!stream)
    {
      AddLoadedModel(
        model,
        -1,
        contextTokens
      );
      await ClassifyAsync(
        context.Response,
        model,
        messages,
        hasTools,
        availableTools,
        cancellationToken
      );
      return;
    }

    if (
      hasTools
      && messages.Any(
        message => message.Content.Contains(
          "SPECIALIST_TOOL_LOOP_V2",
          StringComparison.Ordinal
        )
      )
    )
    {
      AddLoadedModel(
        model,
        -1,
        contextTokens
      );
      await PlanLocalActionAsync(
        context.Response,
        model,
        messages,
        hasTools,
        availableTools,
        true,
        cancellationToken
      );
      return;
    }

    await StreamTargetAsync(
      context.Response,
      model,
      messages,
      contextTokens,
      cancellationToken
    );
  }

  private void UpdateResidency(
    string model,
    int keepAlive,
    int? contextTokens
  )
  {
    if (keepAlive == 0)
    {
      _loaded.TryRemove(
        model,
        out _
      );
      return;
    }

    AddLoadedModel(
      model,
      keepAlive,
      contextTokens
    );
  }

  private void AddLoadedModel(
    string model,
    int keepAlive,
    int? contextTokens = null
  )
  {
    lock (_residencyGate)
    {
      var definition = Models.Single(
        candidate => candidate.Name == model
      );
      _loaded[model] = new RunningModel(
        model,
        $"digest-{model}",
        definition.Size,
        definition.Size * 3 / 4,
        contextTokens ?? 8_192,
        keepAlive < 0
          ? null
          : DateTimeOffset.UtcNow.AddMinutes(
            5
          )
      );

      if (string.Equals(
        model,
        _evictOnNextLoadedModel,
        StringComparison.Ordinal
      ))
      {
        if (!string.IsNullOrWhiteSpace(_evictOnNextRemovedModel))
        {
          _loaded.TryRemove(
            _evictOnNextRemovedModel,
            out _
          );
        }
        _evictOnNextLoadedModel = null;
        _evictOnNextRemovedModel = null;
      }
    }
  }

  private async Task ClassifyAsync(
    HttpListenerResponse response,
    string model,
    IReadOnlyList<RecordedMessage> messages,
    bool hasTools,
    IReadOnlyList<string> availableTools,
    CancellationToken cancellationToken
  )
  {
    if (messages.Any(
      message => message.Content.Contains(
        "SESSION_SUMMARY_V1",
        StringComparison.Ordinal
      )
    ))
    {
      await RespondToSessionSummaryAsync(
        response,
        cancellationToken
      );
      return;
    }

    if (messages.Any(
      message => message.Content.Contains(
        "NATIVE_ADAPTIVE_CONFORMANCE_V1",
        StringComparison.Ordinal
      )
    ))
    {
      await RespondToNativeAdaptiveConformanceAsync(
        response,
        string.Equals(
          model,
          _adaptiveConformanceModel,
          StringComparison.Ordinal
        ),
        cancellationToken
      );
      return;
    }

    if (messages.Any(
      message => message.Content.Contains(
        "TOOL_PROTOCOL_CONFORMANCE_V1",
        StringComparison.Ordinal
      )
    ))
    {
      await RespondToToolConformanceAsync(
        response,
        model,
        availableTools,
        string.Equals(
          model,
          _adaptiveConformanceModel,
          StringComparison.Ordinal
        ),
        cancellationToken
      );
      return;
    }

    if (messages.Any(
      message => message.Content.Contains(
        "STRUCTURED_ACTION_CONFORMANCE_V1",
        StringComparison.Ordinal
      )
    ))
    {
      await RespondToStructuredActionConformanceAsync(
        response,
        model,
        cancellationToken
      );
      return;
    }

    if (availableTools.Contains(
      "route_to_teacher",
      StringComparer.Ordinal
    ))
    {
      var request = messages.Last().Content;
      await RespondWithToolCallAsync(
        response,
        "route_to_teacher",
        new
        {
          teacher_model = request.Contains(
            "route repair required",
            StringComparison.OrdinalIgnoreCase
          ) && !request.Contains(
            "ROUTING_CORRECTION",
            StringComparison.Ordinal
          )
            ? " unavailable-teacher "
            : request.Contains(
            "functiongemma resident contract",
            StringComparison.OrdinalIgnoreCase
          )
            ? " qwen3-coder:30b "
            : " alpha:latest ",
          intent = request.Contains(
            "mismatched route pair",
            StringComparison.OrdinalIgnoreCase
          )
            ? " file-operations "
            : request.Contains(
            "functiongemma resident contract",
            StringComparison.OrdinalIgnoreCase
          )
            ? " software-development "
            : " general-chat ",
          reason = "Selected through the trained FunctionGemma routing contract."
        },
        cancellationToken
      );
      return;
    }

    if (availableTools.Contains(
      "explain_teacher_trace",
      StringComparer.Ordinal
    ))
    {
      await RespondWithToolCallAsync(
        response,
        "explain_teacher_trace",
        new
        {
          reason = "The Host rejected the first invalid step using authoritative comparison facts."
        },
        cancellationToken
      );
      return;
    }

    if (availableTools.Contains(
      "recover_teacher_trace",
      StringComparer.Ordinal
    ))
    {
      var recoveryPrompt = messages.Last().Content;
      var failureCode = ExtractRequiredPolicyValue(
        recoveryPrompt,
        "failure_code"
      );
      var failedStep = ExtractRequiredPolicyValue(
        recoveryPrompt,
        "failed_step"
      );
      var action = ExtractRequiredPolicyValue(
        recoveryPrompt,
        "action"
      );
      var nextTool = ExtractRequiredPolicyValue(
        recoveryPrompt,
        "next_tool"
      );
      await RespondWithToolCallAsync(
        response,
        "recover_teacher_trace",
        new
        {
          action = $" {action} ",
          failure_code = $" {failureCode} ",
          failed_step = $" {failedStep} ",
          next_tool = $" {nextTool} ",
          reason = $"Apply {action} after {failureCode}."
        },
        cancellationToken
      );
      return;
    }

    if (messages.Any(
      message => message.Content.Contains(
        "SPECIALIST_TOOL_LOOP_V2",
        StringComparison.Ordinal
      )
    ))
    {
      await PlanLocalActionAsync(
        response,
        model,
        messages,
        hasTools,
        availableTools,
        false,
        cancellationToken
      );
      return;
    }

    if (messages.Any(
      message => message.Content.Contains(
        "EXPERT_EXECUTION_GUIDANCE_V1",
        StringComparison.Ordinal
      )
    ))
    {
      await PrepareExpertGuidanceAsync(
        response,
        messages,
        cancellationToken
      );
      return;
    }

    var current = messages.Last().Content;
    var intention = current.Contains(
      "review tests",
      StringComparison.OrdinalIgnoreCase
    )
      ? "review-and-testing"
      : current.Contains(
        "service boundaries",
        StringComparison.OrdinalIgnoreCase
      ) || current.Contains(
        "architecture",
        StringComparison.OrdinalIgnoreCase
      )
        ? "software-architecture"
        : current.Contains(
          "write a plan",
          StringComparison.OrdinalIgnoreCase
        ) || current.Contains(
          "specification",
          StringComparison.OrdinalIgnoreCase
        ) || current.Contains(
          "document",
          StringComparison.OrdinalIgnoreCase
        ) || current.Contains(
          "markdown",
          StringComparison.OrdinalIgnoreCase
        ) || current.Contains(
          "memory pressure",
          StringComparison.OrdinalIgnoreCase
        )
          ? "documentation"
          : (
            current.Contains(
              "story",
              StringComparison.OrdinalIgnoreCase
            ) || current.Contains(
              "história",
              StringComparison.OrdinalIgnoreCase
            )
          )
            ? "rpg-storytelling"
            : current.Contains(
              "implement",
              StringComparison.OrdinalIgnoreCase
            ) || current.Contains(
              "code request",
              StringComparison.OrdinalIgnoreCase
            ) || current.Contains(
              "coding a game",
              StringComparison.OrdinalIgnoreCase
            ) || (
              current.Contains(
                "html",
                StringComparison.OrdinalIgnoreCase
              ) && current.Contains(
                "javascript",
                StringComparison.OrdinalIgnoreCase
              )
            )
              ? "software-development"
              : "general-chat";
    string content;

    if (current.Contains(
      "invalid router",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      content = "not-json";
    }
    else if (current.Contains(
      "missing confidence",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      content = JsonSerializer.Serialize(
        new
        {
          intention
        },
        CompactJsonOptions
      );
    }
    else if (current.Contains(
      "negative confidence",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      content = JsonSerializer.Serialize(
        new
        {
          intention,
          confidence = -0.1
        },
        CompactJsonOptions
      );
    }
    else if (current.Contains(
      "out of range confidence",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      content = JsonSerializer.Serialize(
        new
        {
          intention,
          confidence = 1.5
        },
        CompactJsonOptions
      );
    }
    else if (current.Contains(
      "zero confidence",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      content = JsonSerializer.Serialize(
        new
        {
          intention,
          confidence = 0,
          reason = "Explicit zero-confidence fixture."
        },
        CompactJsonOptions
      );
    }
    else if (current.Contains(
      "unsupported intention",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      content = JsonSerializer.Serialize(
        new
        {
          intention = "unsupported",
          confidence = 0.5
        },
        CompactJsonOptions
      );
    }
    else
    {
      content = JsonSerializer.Serialize(
        new
        {
          intention,
          confidence = 0.91,
          reason = intention == "software-development"
            ? "Explicit implementation request."
            : "Latest user request classification."
        },
        CompactJsonOptions
      );
    }

    await WriteJsonAsync(
      response,
      HttpStatusCode.OK,
      new
      {
        message = new
        {
          role = "assistant",
          content
        },
        done = true
      },
      cancellationToken
    );
  }

  private static Task RespondToSessionSummaryAsync(
    HttpListenerResponse response,
    CancellationToken cancellationToken
  )
  {
    return WriteJsonAsync(
      response,
      HttpStatusCode.OK,
      new
      {
        message = new
        {
          role = "assistant",
          content = JsonSerializer.Serialize(
            new
            {
              objective = "Preserve the tested conversation outcome.",
              decisions = new[]
              {
                "Use the authoritative local session facts."
              },
              filesChanged = new[]
              {
                "hello.txt"
              },
              commandsAndValidation = new[]
              {
                "Deterministic validation passed."
              },
              unresolvedIssues = Array.Empty<string>(),
              nextSuggestedStep = "Review the persisted result."
            },
            CompactJsonOptions
          )
        },
        done = true,
        prompt_eval_count = 72,
        eval_count = 28
      },
      cancellationToken
    );
  }

  private static async Task RespondToToolConformanceAsync(
    HttpListenerResponse response,
    string model,
    IReadOnlyList<string> availableTools,
    bool adaptiveFixture,
    CancellationToken cancellationToken
  )
  {
    if (string.Equals(
      model,
      "unused:latest",
      StringComparison.Ordinal
    ) || string.Equals(
      model,
      "structured:latest",
      StringComparison.Ordinal
    ))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.InternalServerError,
        new
        {
          error = "xml syntax error: element <parameter> closed by </function>"
        },
        cancellationToken
      );
      return;
    }

    var tool = availableTools.Single();
    object arguments = tool switch
    {
      "benchmark_echo" => new
      {
        value = "ok"
      },
      "benchmark_plan" => new
      {
        objective = "verify",
        steps = new[]
        {
          new
          {
            title = "Read synthetic input"
          },
          new
          {
            title = "Edit synthetic output"
          }
        }
      },
      "benchmark_read" => new
      {
        path = "sample.txt"
      },
      "benchmark_edit" when adaptiveFixture => new
      {
        path = "sample.txt",
        content = string.Empty
      },
      "benchmark_edit" => new
      {
        path = "sample.txt",
        content = "after"
      },
      _ => throw new InvalidOperationException(
        $"Unknown conformance tool {tool}."
      )
    };
    await WriteJsonAsync(
      response,
      HttpStatusCode.OK,
      new
      {
        message = new
        {
          role = "assistant",
          content = string.Empty,
          tool_calls = new[]
          {
            new
            {
              function = new
              {
                name = tool,
                arguments
              }
            }
          }
        },
        done = true
      },
      cancellationToken
    );
  }

  private static async Task RespondToNativeAdaptiveConformanceAsync(
    HttpListenerResponse response,
    bool adaptiveFixture,
    CancellationToken cancellationToken
  )
  {
    if (!adaptiveFixture)
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.InternalServerError,
        new
        {
          error = "xml syntax error: adaptive native tool call is unavailable"
        },
        cancellationToken
      );
      return;
    }

    await WriteJsonAsync(
      response,
      HttpStatusCode.OK,
      new
      {
        message = new
        {
          role = "assistant",
          content = string.Empty,
          tool_calls = new[]
          {
            new
            {
              function = new
              {
                name = "benchmark_edit",
                arguments = new
                {
                  path = "sample.txt",
                  content = "after"
                }
              }
            }
          }
        },
        done = true
      },
      cancellationToken
    );
  }

  private static async Task RespondToStructuredActionConformanceAsync(
    HttpListenerResponse response,
    string model,
    CancellationToken cancellationToken
  )
  {
    var content = string.Equals(
      model,
      "structured-failure:latest",
      StringComparison.Ordinal
    )
      ? "{\"completed\":false,\"title\":\"\",\"tool\":\"benchmark_echo\",\"arguments\":{\"value\":\"\"}}"
      : JsonSerializer.Serialize(
        new
        {
          completed = false,
          title = "Verify structured action",
          tool = "benchmark_echo",
          arguments = new
          {
            value = "ok"
          }
        },
        CompactJsonOptions
      );
    await WriteJsonAsync(
      response,
      HttpStatusCode.OK,
      new
      {
        message = new
        {
          role = "assistant",
          content
        },
        done = true
      },
      cancellationToken
    );
  }

  private static async Task PrepareExpertGuidanceAsync(
    HttpListenerResponse response,
    IReadOnlyList<RecordedMessage> messages,
    CancellationToken cancellationToken
  )
  {
    var currentEntry = messages.Select(
      (message, index) => (message, index)
    ).Where(
      entry => IsExecuteObjective(entry.message)
    ).Last();
    var current = currentEntry.message.Content;
    var activeMessages = messages.Skip(
      currentEntry.index + 1
    ).ToArray();
    if (current.Contains(
      "configurable planner timeout",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      await Task.Delay(
        1_250,
        cancellationToken
      );
    }
    var validationFailed = activeMessages.Any(
      message => (
        message.ToolName == "run_validation_profile"
        || message.Content.Contains(
          "Tool: run_validation_profile",
          StringComparison.Ordinal
        )
      )
        && message.Content.Contains(
          "Status: failed",
          StringComparison.Ordinal
        )
    );
    var revisionRequested = activeMessages.Any(
      message => message.Content.StartsWith(
        "RECOVERY_STRATEGY_REVISION",
        StringComparison.Ordinal
      )
    );
    var supervisionRequested = activeMessages.Any(
      message => message.Content.StartsWith(
        "RESIDENT_STRATEGY_SUPERVISION",
        StringComparison.Ordinal
      )
    );
    var forceUnchangedRevision = current.Contains(
      "unchanged strategy",
      StringComparison.OrdinalIgnoreCase
    );
    var semanticCorrectionRequested = activeMessages.Any(
      message => message.Content.StartsWith(
          "STRUCTURED_ACTION_CORRECTION",
          StringComparison.Ordinal
        )
        || message.Content.StartsWith(
          "LOCAL_ACTION_CORRECTION",
          StringComparison.Ordinal
        )
    );
    var policyCorrectionRequested = activeMessages.Any(
      message => message.Content.StartsWith(
        "LOCAL_ACTION_RESULT",
        StringComparison.Ordinal
      )
        && message.Content.Contains(
          "Status: policy-denied",
          StringComparison.Ordinal
        )
    );
    var structuredSemanticRepair = current.Contains(
      "structured semantic repair",
      StringComparison.OrdinalIgnoreCase
    );
    var repeatStructuredSemanticFailure = current.Contains(
      "repeat structured semantic failure",
      StringComparison.OrdinalIgnoreCase
    );
    var completedStructuredAction = activeMessages.Any(
      message => message.Content.StartsWith(
        "LOCAL_ACTION_RESULT",
        StringComparison.Ordinal
      )
        && message.Content.Contains(
          "Status: completed",
          StringComparison.Ordinal
        )
        && !message.Content.Contains(
          "Tool: create_execution_plan",
          StringComparison.Ordinal
        )
        && !message.Content.Contains(
          "Tool: revise_execution_plan",
          StringComparison.Ordinal
        )
    );
    var completedStructuredInspection = activeMessages.Any(
      message => message.Content.StartsWith(
        "LOCAL_ACTION_RESULT",
        StringComparison.Ordinal
      )
        && message.Content.Contains(
          "Tool: read_file",
          StringComparison.Ordinal
        )
        && message.Content.Contains(
          "Status: completed",
          StringComparison.Ordinal
        )
    );
    var completedStructuredMutation = activeMessages.Any(
      message => message.Content.StartsWith(
        "LOCAL_ACTION_RESULT",
        StringComparison.Ordinal
      )
        && (
          message.Content.Contains("Tool: write_file", StringComparison.Ordinal)
          || message.Content.Contains("Tool: create_files", StringComparison.Ordinal)
          || message.Content.Contains("Tool: replace_text", StringComparison.Ordinal)
          || message.Content.Contains("Tool: apply_patch", StringComparison.Ordinal)
        )
        && message.Content.Contains(
          "Status: completed",
          StringComparison.Ordinal
        )
    );
    var completedStructuredReads = activeMessages.Count(
      message => message.Content.Contains("Tool: read_file", StringComparison.Ordinal)
        && message.Content.Contains("Status: completed", StringComparison.Ordinal)
    );
    var completedStructuredDeletion = activeMessages.Any(
      message => message.Content.Contains("Tool: delete_paths", StringComparison.Ordinal)
        && message.Content.Contains("Status: completed", StringComparison.Ordinal)
    );
    var completedStructuredValidation = activeMessages.Any(
      message => message.Content.Contains("Tool: run_validation_profile", StringComparison.Ordinal)
        && (
          message.Content.Contains("Status: completed", StringComparison.Ordinal)
          || message.Content.Contains("Status: failed", StringComparison.Ordinal)
        )
    );
    var failedStructuredProcess = activeMessages.Any(
      message => message.Content.Contains("Tool: run_process", StringComparison.Ordinal)
        && message.Content.Contains("Status: failed", StringComparison.Ordinal)
    );
    var completedStructuredPatchCount = activeMessages.Count(
      message => message.Content.Contains("Tool: apply_patch", StringComparison.Ordinal)
        && message.Content.Contains("Status: completed", StringComparison.Ordinal)
    );
    var latestStructuredMutationIndex = Array.FindLastIndex(
      activeMessages,
      message => message.Content.StartsWith(
          "LOCAL_ACTION_RESULT",
          StringComparison.Ordinal
        )
        && message.Content.Contains(
          "Status: completed",
          StringComparison.Ordinal
        )
        && (
          message.Content.Contains("Tool: create_file", StringComparison.Ordinal)
          || message.Content.Contains("Tool: create_files", StringComparison.Ordinal)
          || message.Content.Contains("Tool: write_file", StringComparison.Ordinal)
          || message.Content.Contains("Tool: replace_text", StringComparison.Ordinal)
          || message.Content.Contains("Tool: apply_patch", StringComparison.Ordinal)
        )
    );
    var latestStructuredReadIndex = Array.FindLastIndex(
      activeMessages,
      message => message.Content.StartsWith(
          "LOCAL_ACTION_RESULT",
          StringComparison.Ordinal
        )
        && message.Content.Contains(
          "Tool: read_file",
          StringComparison.Ordinal
        )
        && message.Content.Contains(
          "Status: completed",
          StringComparison.Ordinal
        )
    );
    var activeContent = string.Join(
      "\n",
      activeMessages.Select(
        message => message.Content
      )
    );
    var structuredCompletionReviewRequested = latestStructuredMutationIndex
      > latestStructuredReadIndex
      && activeContent.Contains(
        "The latest changed files have not all been inspected after their latest mutation:",
        StringComparison.Ordinal
      );
    object NextDeleteProposal()
    {
      if (completedStructuredReads == 0)
      {
        return CreateLocalActionPlan(current);
      }

      if (completedStructuredReads == 1)
      {
        return new
        {
          tool = "read_file",
          arguments = new
          {
            path = "obsolete-b.txt"
          },
          explanation = "Inspect the second file before deletion."
        };
      }

      return new
      {
        tool = "delete_paths",
        arguments = new
        {
          paths = new[]
          {
            "obsolete-a.txt",
            "obsolete-b.txt"
          },
          recursive = false
        },
        explanation = "Delete the exact inspected files after Host validation."
      };
    }

    var nativeBatchCreation = current.Contains(
      "native create host batch files",
      StringComparison.OrdinalIgnoreCase
    );
    var guidance = nativeBatchCreation
      && !completedStructuredMutation
      ? CreateStructuredGuidance(
        current,
        new
        {
          tool = "create_files",
          arguments = new
          {
            files = new[]
            {
              new { path = "native-ação.html", content = "<!doctype html><title>Ação nativa</title>\n" },
              new { path = "native-estilo.css", content = "/* revisão nativa */\nbody { color: #456; }\n" }
            }
          },
          explanation = "Create both independent UTF-8 files through one Host batch action."
        }
      )
      : nativeBatchCreation
        && !activeContent.Contains("<!doctype html><title>Ação nativa", StringComparison.Ordinal)
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "read_file",
            arguments = new { path = "native-ação.html" },
            explanation = "Verify the first file created by the Host batch."
          }
        )
      : nativeBatchCreation
        && !activeContent.Contains("/* revisão nativa */", StringComparison.Ordinal)
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "read_file",
            arguments = new { path = "native-estilo.css" },
            explanation = "Verify the second file created by the Host batch."
          }
        )
      : current.Contains("duplicate workspace root edit", StringComparison.OrdinalIgnoreCase)
      && !completedStructuredMutation
      ? CreateStructuredGuidance(
        current,
        completedStructuredInspection
          ? new
          {
            tool = "write_file",
            arguments = new
            {
              path = "hello.txt",
              content = "edited existing root file"
            },
            explanation = "Edit the inspected file at the actual workspace root."
          }
          : semanticCorrectionRequested
            ? new
            {
              tool = "read_file",
              arguments = new
              {
                path = "hello.txt"
              },
              explanation = "Inspect the existing file at the actual workspace root."
            }
            : CreateLocalActionPlan(current)
      )
      : failedStructuredProcess && !completedStructuredAction
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "list_files",
            arguments = new
            {
              path = ".",
              recursive = false
            },
            explanation = "Recover from the unavailable process with a bounded workspace inspection."
          }
        )
      : (semanticCorrectionRequested || policyCorrectionRequested)
        && !completedStructuredAction
        && current.Contains("control character path recover", StringComparison.OrdinalIgnoreCase)
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "create_file",
            arguments = new
            {
              path = "safe-control-path.txt",
              content = "recovered after invalid control path"
            },
            explanation = "Use a valid workspace-relative path after the malformed path was rejected."
          }
        )
      : (semanticCorrectionRequested || policyCorrectionRequested)
        && !completedStructuredAction
        && current.Contains("control character process recover", StringComparison.OrdinalIgnoreCase)
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "create_file",
            arguments = new
            {
              path = "safe-control-process.txt",
              content = "recovered after invalid process argument"
            },
            explanation = "Use a structured safe action after the malformed process argument was rejected."
          }
        )
      : (semanticCorrectionRequested || policyCorrectionRequested)
        && !completedStructuredAction
        && current.Contains("path traversal recover", StringComparison.OrdinalIgnoreCase)
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "create_file",
            arguments = new
            {
              path = "safe.txt",
              content = "recovered inside trusted workspace"
            },
            explanation = "Use a safe path inside the trusted workspace."
          }
        )
      : current.Contains("delete directory recursive", StringComparison.OrdinalIgnoreCase)
      && !completedStructuredDeletion
      ? CreateStructuredGuidance(
        current,
        new
        {
          tool = "delete_paths",
          arguments = new
          {
            paths = new[]
            {
              "obsolete-tree"
            },
            recursive = true
          },
          explanation = "Delete the explicit directory recursively through the Host-owned path tool."
        }
      )
      : current.Contains("delete files", StringComparison.OrdinalIgnoreCase)
      && !completedStructuredDeletion
      ? CreateStructuredGuidance(current, NextDeleteProposal())
      : current.Contains("sequential apply patch", StringComparison.OrdinalIgnoreCase)
        && completedStructuredPatchCount == 1
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "apply_patch",
            arguments = new
            {
              path = "hello.txt",
              replacements = new[]
              {
                new
                {
                  oldText = "patched",
                  newText = "patched twice"
                }
              }
            },
            explanation = "Apply the second edit after observing the first verified patch."
          }
        )
      : IsMutationFixture(current) && !completedStructuredMutation
      ? CreateStructuredGuidance(
        current,
        completedStructuredInspection
          ? CreateLocalActionPlan(current)
          : new
          {
            tool = "read_file",
            arguments = new
            {
              path = current.Contains("coding task", StringComparison.OrdinalIgnoreCase)
                ? "Program.cs"
                : "hello.txt"
            },
            explanation = "Inspect the existing file before modification."
          }
      )
      : current.Contains("validate", StringComparison.OrdinalIgnoreCase)
        && completedStructuredAction
        && !completedStructuredValidation
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "run_validation_profile",
            arguments = new { },
            explanation = "Run the saved build and test validation profile."
          }
        )
      : structuredCompletionReviewRequested
        ? CreateStructuredGuidance(
          current,
          new
          {
            tool = "read_file",
            arguments = new
            {
              path = ExtractCompletionReviewPath(
                activeContent
              )
            },
            explanation = "Inspect the latest changed file after the Host rejected premature completion."
          }
        )
      : completedStructuredAction
      ? JsonSerializer.Serialize(
        new
        {
          actionRequired = false,
          objective = current,
          actions = Array.Empty<object>(),
          completionCriteria = new[]
          {
            "The authoritative Host result completed the requested action."
          }
        },
        CompactJsonOptions
      )
      : structuredSemanticRepair || repeatStructuredSemanticFailure
        ? CreateSemanticRepairGuidance(
          current,
          semanticCorrectionRequested && !repeatStructuredSemanticFailure
        )
      : validationFailed
      ? JsonSerializer.Serialize(
        new
        {
          actionRequired = false,
          objective = current,
          actions = Array.Empty<object>(),
          completionCriteria = new[]
          {
            "Report the completed implementation and failed validation."
          }
        },
        CompactJsonOptions
      )
      : current.Contains(
        "empty takeover guidance",
        StringComparison.OrdinalIgnoreCase
      )
        ? string.Empty
        : (
          revisionRequested
          || supervisionRequested
        ) && !forceUnchangedRevision
          ? CreateRevisedStructuredGuidance(
            current
          )
          : CreateStructuredGuidance(
            current
          );

    await WriteJsonAsync(
      response,
      HttpStatusCode.OK,
      new
      {
        message = new
        {
          role = "assistant",
          content = guidance
        },
        done = true
      },
      cancellationToken
    );
  }

  private static string CreateStructuredGuidance(
    string current,
    object? structuredProposal = null
  )
  {
    var proposal = JsonSerializer.SerializeToElement(
      structuredProposal ?? CreateLocalActionPlan(current),
      CompactJsonOptions
    );
    var tool = proposal.TryGetProperty(
      "tool",
      out var toolElement
    ) && toolElement.ValueKind == JsonValueKind.String
      ? toolElement.GetString()
      : null;
    var arguments = proposal.TryGetProperty(
      "arguments",
      out var argumentsElement
    )
      ? argumentsElement
      : JsonSerializer.SerializeToElement(
        new { }
      );
    object[] actions = tool is null
      ? []
      :
      [
        new
        {
          id = "guidance-1",
          title = $"Execute {tool}",
          tool,
          arguments
        }
      ];

    return JsonSerializer.Serialize(
      new
      {
        actionRequired = tool is not null,
        objective = current,
        actions,
        completionCriteria = tool is null
          ? new[]
          {
            "No local action is required."
          }
          : new[]
          {
            "The requested local action completed with a verified tool result."
          }
      },
      CompactJsonOptions
    );
  }

  private static string CreateSemanticRepairGuidance(
    string current,
    bool corrected
  )
  {
    return JsonSerializer.Serialize(
      new
      {
        actionRequired = true,
        objective = current,
        actions = new[]
        {
          new
          {
            title = "Create the structured repair file",
            tool = "create_file",
            arguments = corrected
              ? (object)new
              {
                path = "structured-repair.txt",
                content = "repaired by bounded feedback"
              }
              : new
              {
                path = "structured-repair.txt"
              }
          }
        },
        completionCriteria = new[]
        {
          "The Host accepted and executed the corrected non-empty content."
        }
      },
      CompactJsonOptions
    );
  }

  private static string CreateRevisedStructuredGuidance(
    string current
  )
  {
    var proposal = JsonSerializer.SerializeToElement(
      CreateLocalActionPlan(
        current
      ),
      CompactJsonOptions
    );
    var tool = proposal.TryGetProperty(
      "tool",
      out var toolElement
    ) && toolElement.ValueKind == JsonValueKind.String
      ? toolElement.GetString()
      : null;
    var arguments = proposal.TryGetProperty(
      "arguments",
      out var argumentsElement
    )
      ? argumentsElement
      : JsonSerializer.SerializeToElement(
        new { }
      );
    object[] actions = tool is null
      ? []
      :
      [
        new
        {
          id = "recovery-guidance-1",
          title = $"Correct the failed planning contract, then execute {tool}",
          tool,
          arguments
        }
      ];

    return JsonSerializer.Serialize(
      new
      {
        actionRequired = tool is not null,
        objective = $"Recover from the reported planning failure while completing: {current}",
        actions,
        completionCriteria = tool is null
          ? new[]
          {
            "No local action is required after correcting the reported failure."
          }
          : new[]
          {
            "The corrected visible plan uses non-empty string id and title fields.",
            "The requested local action completes with a verified tool result."
          }
      },
      CompactJsonOptions
    );
  }

  private static bool IsExecuteObjective(RecordedMessage message)
  {
    if (!string.Equals(message.Role, "user", StringComparison.Ordinal))
    {
      return false;
    }

    return !message.Content.StartsWith("LOCAL_ACTION_", StringComparison.Ordinal)
      && !message.Content.StartsWith("STRUCTURED_ACTION_", StringComparison.Ordinal)
      && !message.Content.StartsWith("TOOL_PROTOCOL_", StringComparison.Ordinal)
      && !message.Content.StartsWith("EXECUTION_", StringComparison.Ordinal)
      && !message.Content.StartsWith("COMPLETION_", StringComparison.Ordinal)
      && !message.Content.StartsWith("HOST_COMPLETION_FACTS", StringComparison.Ordinal)
      && !message.Content.StartsWith("RECOVERY_", StringComparison.Ordinal)
      && !message.Content.StartsWith("EXPERT_EXECUTION_GUIDANCE_V1", StringComparison.Ordinal);
  }

  private async Task PlanLocalActionAsync(
    HttpListenerResponse response,
    string model,
    IReadOnlyList<RecordedMessage> messages,
    bool hasTools,
    IReadOnlyList<string> availableTools,
    bool stream,
    CancellationToken cancellationToken
  )
  {
    var currentEntry = messages.Select(
      (message, index) => (message, index)
    ).Where(
      entry => IsExecuteObjective(entry.message)
    ).Last();
    var current = currentEntry.message.Content;
    var activeMessages = messages.Skip(
      currentEntry.index + 1
    ).ToArray();
    var attempt = _generationAttempts.AddOrUpdate(
      $"planner:{model}:{current}",
      1,
      (
        _,
        count
      ) => count + 1
    );
    var results = activeMessages.Where(
      message => message.Role == "tool"
        || message.Content.StartsWith(
          "LOCAL_ACTION_RESULT",
          StringComparison.Ordinal
        )
    ).ToArray();
    var trackedPlanFixture = current.Contains(
      "specialist tracked plan",
      StringComparison.OrdinalIgnoreCase
    );
    var hasPlan = !trackedPlanFixture || results.Any(
      message => (
        message.ToolName == "create_execution_plan"
        && (
          message.Content.StartsWith(
            "Status: completed",
            StringComparison.Ordinal
          )
          || message.Content.Contains(
            "Accepted Host plan",
            StringComparison.Ordinal
          )
        )
      )
        || message.Content.Contains(
          "Tool: create_execution_plan\nStatus: completed",
          StringComparison.Ordinal
        )
    );

    if (
      hasPlan
      && current.Contains(
        "configurable planner timeout",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      await Task.Delay(
        1_250,
        cancellationToken
      );
    }

    var actionResults = results.Where(
      message => message.ToolName is not LocalActionPlanner.RequestToolsetTool
        and not "create_execution_plan"
        and not "revise_execution_plan"
        && !message.Content.Contains(
          $"Tool: {LocalActionPlanner.RequestToolsetTool}",
          StringComparison.Ordinal
        )
        && !message.Content.Contains(
          "Tool: create_execution_plan",
          StringComparison.Ordinal
        )
        && !message.Content.Contains(
          "Tool: revise_execution_plan",
          StringComparison.Ordinal
        )
    ).ToArray();
    var hasResult = actionResults.Length > 0
      || activeMessages.Any(message => message.Content.StartsWith("APPLICATION_OWNED_EXECUTION_STATE_V1", StringComparison.Ordinal)
        && message.Content.Contains(":completed:", StringComparison.Ordinal));
    var allContent = string.Join(
      "\n",
      new[]
      {
        current
      }.Concat(
        activeMessages.Select(
          message => message.Content
        )
      )
    );
    var humanRecoveryNeedsInvalidPlan = current.Contains(
      "human recovery",
      StringComparison.OrdinalIgnoreCase
    ) && !allContent.Contains(
      "RECOVERY_DECISION",
      StringComparison.Ordinal
    ) && !allContent.Contains(
      "RECOVERY_STRATEGY_REVISION",
      StringComparison.Ordinal
    );
    var latestResult = actionResults.LastOrDefault()?.Content;
    object plan;

    if (allContent.Contains(
      "planner provider failure",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.ServiceUnavailable,
        new
        {
          error = "planner provider fixture failure"
        },
        cancellationToken
      );
      return;
    }

    if (
      string.Equals(
        model,
        "command-r:latest",
        StringComparison.Ordinal
      )
      && current.Contains(
        "xml syntax tool call",
        StringComparison.OrdinalIgnoreCase
      )
      && attempt == 1
    )
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.InternalServerError,
        new
        {
          error = "xml syntax error on line 1: element <parameter> closed by </function>"
        },
        cancellationToken
      );
      return;
    }

    if (
      string.Equals(
        model,
        "router:latest",
        StringComparison.Ordinal
      )
      && current.Contains(
        "resident protocol correction",
        StringComparison.OrdinalIgnoreCase
      )
      && attempt == 1
    )
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.InternalServerError,
        new
        {
          error = "error parsing tool call: unexpected end of JSON input"
        },
        cancellationToken
      );
      return;
    }

    if (
      string.Equals(
        model,
        "command-r:latest",
        StringComparison.Ordinal
      )
      && current.Contains(
        "truncated planner tool call",
        StringComparison.OrdinalIgnoreCase
      )
      && attempt == 2
    )
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.InternalServerError,
        new
        {
          error = "error parsing tool call: raw='{\"objective\":\"Create a file\",\"steps\":[{\"id\":\"step-1\",\"title\":\"Create requested file\"}]', err=unexpected end of JSON input"
        },
        cancellationToken
      );
      return;
    }

    var planOnly = availableTools.Count == 1
      && availableTools[0] == "create_execution_plan";

    if (current.Contains(
      "chronological thinking stream",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      var completedChronologicalActions = actionResults.Count(
        result => string.Equals(
          result.ToolName,
          "create_file",
          StringComparison.Ordinal
        ) || result.Content.Contains(
          "Tool: create_file",
          StringComparison.Ordinal
        )
      );
      plan = completedChronologicalActions == 0
        ? new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "chrono-one.txt",
            content = "first chronological action"
          },
          explanation = "Create the first chronological fixture."
        }
        : completedChronologicalActions == 1
          ? new
          {
            tool = (string?)"create_file",
            arguments = (object)new
            {
              path = "chrono-two.txt",
              content = "second chronological action"
            },
            explanation = "Create the second chronological fixture."
          }
          : new
          {
            tool = (string?)null,
            arguments = (object)new { },
            explanation = "Both chronological fixtures are complete."
          };
    }
    else if (
      !hasPlan
      && current.Contains(
        "alias phase bypass",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = new
      {
        tool = "read_doc",
        arguments = new
        {
          path = "hello.txt"
        },
        explanation = "Attempt a read alias before the execution plan phase is complete."
      };
    }
    else if (
      !hasPlan
      && current.Contains(
        "recover rejected execution plan write file",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = planOnly
        ? attempt == 1
          ? CreateInvalidExecutionPlan(
            current
          )
          : CreateExecutionPlan(
            current
          )
        : attempt % 2 == 0
          ? CreateInvalidExecutionPlan(
            current
          )
          : CreateLocalActionPlan(
            current
          );
    }
    else if (
      !hasPlan
      && current.Contains(
        "string null planner",
        StringComparison.OrdinalIgnoreCase
      )
      && attempt == 1
    )
    {
      plan = new
      {
        tool = "null",
        arguments = new { },
        explanation = "No local action remains."
      };
    }
    else if (
      !hasPlan
      && humanRecoveryNeedsInvalidPlan
    )
    {
      plan = CreateMissingFieldsExecutionPlan(
        current
      );
    }
    else if (
      !hasPlan
      && allContent.Contains(
        "vanilla manual scope",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = CreateExecutionPlan(
        allContent
      );
    }
    else if (
      !hasPlan
      && allContent.Contains(
        "multi file static review",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = CreateExecutionPlan(
        allContent
      );
    }
    else if (
      hasPlan
      && ForkGameExecutionFixture.Matches(current)
    )
    {
      plan = CreateForkGameAction(
        Math.Max(
          0,
          attempt - availableTools.Count
        )
      );
    }
    else if (
      hasPlan
      && trackedPlanFixture
    )
    {
      plan = actionResults.Length switch
      {
        0 => CreateLocalActionPlan(current),
        1 => new
        {
          tool = (string?)"read_file",
          arguments = (object)new
          {
            path = "tracked-plan.txt"
          },
          explanation = "Inspect the latest changed fixture in the second specialist plan step."
        },
        _ => new
        {
          tool = (string?)null,
          arguments = (object)new { },
          explanation = "Both specialist-proposed plan steps have proven effects."
        }
      };
    }
    else if (
      hasPlan
      && current.Contains(
        "multi file static review",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = actionResults.Length switch
      {
        0 => new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "review.html",
            content = "<!doctype html><script src=\"review-data.js\"></script>"
          },
          explanation = "Create the static entrypoint."
        },
        1 => new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "review-data.js",
            content = "window.items = ['one', 'two'];"
          },
          explanation = "Create the constrained data file."
        },
        2 => new
        {
          tool = (string?)"read_file",
          arguments = (object)new
          {
            path = "review.html"
          },
          explanation = "Inspect entrypoint integration."
        },
        3 => new
        {
          tool = (string?)"read_file",
          arguments = (object)new
          {
            path = "review-data.js"
          },
          explanation = "Inspect the data integration."
        },
        _ => new
        {
          tool = (string?)null,
          arguments = (object)new { },
          explanation = "The bounded static review plan is complete."
        }
      };
    }
    else if (
      hasResult
      && allContent.Contains(
        "The latest changed files have not all been inspected after their latest mutation:",
        StringComparison.Ordinal
      )
      && !actionResults.Any(
        result => result.ToolName == "read_file"
          || result.Content.Contains(
            "Tool: read_file",
            StringComparison.Ordinal
          )
      )
    )
    {
      plan = new
      {
        tool = "read_file",
        arguments = (object)new
        {
          path = ExtractCompletionReviewPath(
            allContent
          )
        },
        explanation = "Inspect the latest changed file after the Host rejected premature completion."
      };
    }
    else if (
      hasPlan
      && string.Equals(
        model,
        "qwen3-coder:30b",
        StringComparison.Ordinal
      )
      && IsQwenToolingMatrixFixture(current)
    )
    {
      plan = CreateQwenToolingMatrixAction(
        current,
        actionResults,
        allContent
      );
    }
    else if (!hasPlan)
    {
      plan = CreateExecutionPlan(
        current
      );
    }
    else if (
      !hasResult
      && current.Contains(
        "delete directory recursive",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = new
      {
        tool = "delete_paths",
        arguments = new
        {
          paths = new[]
          {
            "obsolete-tree"
          },
          recursive = true,
          stepId = "step-1"
        },
        explanation = "Delete the explicit directory recursively through the Host-owned path tool."
      };
    }
    else if (
      !hasResult
      && current.Contains(
        "delete files direct",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = new
      {
        tool = "delete_paths",
        arguments = new
        {
          paths = new[]
          {
            "obsolete-a.txt",
            "obsolete-b.txt"
          },
          recursive = false
        },
        explanation = "Submit the explicit files directly for Host validation and approval."
      };
    }
    else if (
      !hasResult
      && current.Contains(
        "vanilla manual scope",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = availableTools.Contains(
        "run_process",
        StringComparer.Ordinal
      )
        ? new
        {
          tool = "run_process",
          arguments = (object)new
          {
            executable = "node",
            arguments = new[]
            {
              "server.js"
            },
            workingDirectory = ".",
            timeoutSeconds = 10
          },
          explanation = "Attempt the irrelevant Node path only if the Host offered it."
        }
        : new
        {
          tool = "create_file",
          arguments = (object)new
          {
            path = "game.js",
            content = "window.gameReady = true;"
          },
          explanation = "Create the browser script without inventing a process."
        };
    }
    else if (
      hasResult
      && allContent.Contains(
        "vanilla manual scope",
        StringComparison.OrdinalIgnoreCase
      )
      && allContent.Contains(
        "Changed files still requiring inspection",
        StringComparison.Ordinal
      )
      && allContent.Contains(
        "'game.js'",
        StringComparison.Ordinal
      )
      && !actionResults.Any(
        result => result.ToolName == "read_file"
          || result.Content.Contains(
            "Tool: read_file",
            StringComparison.Ordinal
          )
      )
    )
    {
      plan = new
      {
        tool = "read_file",
        arguments = (object)new
        {
          path = "game.js"
        },
        explanation = "Perform the Host-required static review after the file-only mutation."
      };
    }
    else if (allContent.Contains(
      "repeat denied process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      plan = new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "git",
          arguments = new[]
          {
            "clean",
            "-fd"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Repeat the denied process fixture."
      };
    }
    else if (
      hasResult
      && current.Contains(
        "partial context after completed action",
        StringComparison.OrdinalIgnoreCase
      )
      && !allContent.Contains(
        "STRUCTURED_ACTION_CORRECTION",
        StringComparison.Ordinal
      )
    )
    {
      plan = new
      {
        tool = "create_file",
        arguments = new
        {
          content = new string('p', 16_000)
        },
        explanation = "Return one intentionally invalid oversized follow-up after a completed action."
      };
    }
    else if (
      hasResult
      && latestResult?.Contains(
        "Status: failed",
        StringComparison.Ordinal
      ) == true
      && allContent.Contains(
        "recover failed process",
        StringComparison.OrdinalIgnoreCase
      )
      && !results.Any(
        result => result.ToolName == "revise_execution_plan"
          || result.Content.Contains(
            "Tool: revise_execution_plan",
            StringComparison.Ordinal
          )
      )
    )
    {
      plan = new
      {
        tool = "revise_execution_plan",
        arguments = new
        {
          objective = current,
          steps = new[]
          {
            new
            {
              title = "Inspect workspace after failed process"
            }
          }
        },
        explanation = "Replace the blocked process strategy with a typed inspection step."
      };
    }
    else if (
      hasResult
      && latestResult?.Contains(
        "Status: failed",
        StringComparison.Ordinal
      ) == true
      && allContent.Contains(
        "recover failed process",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = new
      {
        tool = "list_files",
        arguments = new
        {
          path = ".",
          recursive = false
        },
        explanation = "Recover from the unavailable process with the structured directory tool."
      };
    }
    else if (
      hasResult
      && actionResults.Last().ToolName == "read_file"
      && current.Contains(
        "delete files",
        StringComparison.OrdinalIgnoreCase
      )
      && actionResults.Count(result => result.ToolName == "read_file") == 1
    )
    {
      plan = new
      {
        tool = "read_file",
        arguments = new
        {
          path = "obsolete-b.txt"
        },
        explanation = "Inspect the second file before deletion."
      };
    }
    else if (
      hasResult
      && actionResults.Last().ToolName == "read_file"
      && current.Contains(
        "delete files",
        StringComparison.OrdinalIgnoreCase
      )
      && actionResults.Count(result => result.ToolName == "read_file") >= 2
    )
    {
      plan = new
      {
        tool = "delete_paths",
        arguments = new
        {
          paths = new[]
          {
            "obsolete-a.txt",
            "obsolete-b.txt"
          },
          recursive = false
        },
        explanation = "Delete the exact inspected files after Host validation."
      };
    }
    else if (
      hasResult
      && actionResults.Last().ToolName == "read_file"
      && current.Contains(
        "revise plan omitting completed step",
        StringComparison.OrdinalIgnoreCase
      )
      && !results.Any(
        result => result.ToolName == "revise_execution_plan"
          || result.Content.Contains(
            "Tool: revise_execution_plan",
            StringComparison.Ordinal
          )
      )
    )
    {
      plan = new
      {
        tool = "revise_execution_plan",
        arguments = new
        {
          objective = current,
          steps = new[]
          {
            new
            {
              id = "step-2",
              title = "Implement requested file changes"
            }
          }
        },
        explanation = "Revise the remaining work while omitting the completed inspection step."
      };
    }
    else if (
      hasResult
      && actionResults.Last().ToolName == "read_file"
      && current.Contains(
        "duplicate workspace root edit",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = new
      {
        tool = "write_file",
        arguments = new
        {
          path = "hello.txt",
          content = "edited existing root file"
        },
        explanation = "Edit the inspected file at the actual workspace root."
      };
    }
    else if (
      hasResult
      && latestResult?.Contains(
        "already the project root",
        StringComparison.OrdinalIgnoreCase
      ) == true
      && current.Contains(
        "duplicate workspace root edit",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = new
      {
        tool = "read_file",
        arguments = new
        {
          path = "workspace/hello.txt"
        },
        explanation = "Retry inspection while still repeating the workspace root alias."
      };
    }
    else if (
      hasResult
      && IsMutationFixture(
        current
      )
      && !actionResults.Any(
        result => result.ToolName is "write_file" or "replace_text" or "apply_patch"
          || result.Content.Contains(
            "Tool: write_file",
            StringComparison.Ordinal
          )
          || result.Content.Contains(
            "Tool: replace_text",
            StringComparison.Ordinal
          )
          || result.Content.Contains(
            "Tool: apply_patch",
            StringComparison.Ordinal
          )
      )
    )
    {
      plan = CreateLocalActionPlan(
        current
      );
    }
    else if (
      hasResult
      && current.Contains(
        "sequential apply patch",
        StringComparison.OrdinalIgnoreCase
      )
      && actionResults.Count(
        result => result.ToolName == "apply_patch"
          || result.Content.Contains(
            "Tool: apply_patch",
            StringComparison.Ordinal
          )
      ) == 1
    )
    {
      plan = new
      {
        tool = "apply_patch",
        arguments = new
        {
          path = "hello.txt",
          replacements = new[]
          {
            new
            {
              oldText = "patched",
              newText = "patched twice"
            }
          }
        },
        explanation = "Apply a second patch to the file changed by the previous action."
      };
    }
    else if (
      hasResult
      && current.Contains(
        "recover premature prose after approved process",
        StringComparison.OrdinalIgnoreCase
      )
      && allContent.Contains(
        "EXECUTION_COMPLETION_REJECTED",
        StringComparison.Ordinal
      )
      && !actionResults.Any(
        result => result.ToolName == "create_file"
          || result.Content.Contains(
            "Tool: create_file",
            StringComparison.Ordinal
          )
      )
    )
    {
      plan = new
      {
        tool = "create_file",
        arguments = new
        {
          path = "hello.txt",
          content = "recovered after premature prose"
        },
        explanation = "Continue with the pending file creation after the completion rejection."
      };
    }
    else if (
      hasResult
      && current.Contains(
        "control character path recover",
        StringComparison.OrdinalIgnoreCase
      )
      && actionResults.Last().ToolName == "read_file"
    )
    {
      plan = new
      {
        tool = "create_file",
        arguments = new
        {
          path = "safe-control-path.txt",
          content = "recovered after invalid control path"
        },
        explanation = "Use a valid workspace-relative path after the malformed path was rejected."
      };
    }
    else if (
      hasResult
      && current.Contains(
        "control character process recover",
        StringComparison.OrdinalIgnoreCase
      )
      && actionResults.Last().ToolName == "run_process"
    )
    {
      plan = new
      {
        tool = "create_file",
        arguments = new
        {
          path = "safe-control-process.txt",
          content = "recovered after invalid process argument"
        },
        explanation = "Use a structured safe action after the malformed process argument was rejected."
      };
    }
    else if (
      hasResult
      && current.Contains(
        "path traversal recover",
        StringComparison.OrdinalIgnoreCase
      )
      && actionResults.Last().ToolName == "read_file"
    )
    {
      plan = new
      {
        tool = "create_file",
        arguments = new
        {
          path = "safe.txt",
          content = "recovered inside trusted workspace"
        },
        explanation = "Use a safe path inside the trusted workspace."
      };
    }
    else if (
      hasResult
      && current.Contains(
        "path traversal",
        StringComparison.OrdinalIgnoreCase
      )
      && !current.Contains(
        "path traversal recover",
        StringComparison.OrdinalIgnoreCase
      )
      && !current.Contains(
        "path traversal create corrected",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      plan = CreateLocalActionPlan(
        current
      );
    }
    else if (
      hasResult
      && current.Contains(
        "validate",
        StringComparison.OrdinalIgnoreCase
      )
      && !actionResults.Any(
        result => result.Content.Contains(
          "Tool: run_validation_profile",
          StringComparison.Ordinal
        ) || result.ToolName == "run_validation_profile"
      )
    )
    {
      plan = new
      {
        tool = "run_validation_profile",
        arguments = new { },
        explanation = "Run the saved validation profile."
      };
    }
    else if (hasResult)
    {
      plan = new
      {
        tool = (string?)null,
        arguments = new { },
        explanation = "The requested local action has a result."
      };
    }
    else
    {
      if (current.Contains(
        "string null planner",
        StringComparison.OrdinalIgnoreCase
      ) && attempt == 1)
      {
        plan = new
        {
          tool = "null",
          arguments = new { },
          explanation = "No local action remains."
        };
      }
      else if (
        (
          current.Contains(
            "recovery budget reset",
            StringComparison.OrdinalIgnoreCase
          )
          && attempt == 6
        )
      )
      {
        plan = new
        {
          tool = "unknown_tool",
          arguments = new { },
          explanation = "Invalid tool fixture."
        };
      }
      else if (
        current.Contains(
          "retry unknown tool",
          StringComparison.OrdinalIgnoreCase
        )
        && attempt == 1
      )
      {
        plan = new
        {
          tool = "open_file",
          arguments = new
          {
            path = "hello.txt"
          },
          explanation = "Return one unknown tool name before applying the Host correction."
        };
      }
      else
      {
        plan = current.Contains(
          "duplicate workspace root edit",
          StringComparison.OrdinalIgnoreCase
        )
          ? CreateLocalActionPlan(
            current
          )
          : IsMutationFixture(
            current
          )
          ? new
          {
            tool = "read_file",
            arguments = new
            {
              path = "hello.txt"
            },
            explanation = "Inspect the existing file before modification."
          }
          : CreateLocalActionPlan(
            current
          );
      }
    }
    var alwaysInvalid = current.Contains(
      "always invalid planner",
      StringComparison.OrdinalIgnoreCase
    );
    var recoverableInvalid = current.Contains(
      "retry invalid planner",
      StringComparison.OrdinalIgnoreCase
    ) && attempt < 3
      || current.Contains(
        "recovery budget reset",
        StringComparison.OrdinalIgnoreCase
      ) && attempt < 5;
    var targetInvalid = string.Equals(
      model,
      "command-r:latest",
      StringComparison.Ordinal
    ) && current.Contains(
      "target planner invalid",
      StringComparison.OrdinalIgnoreCase
    );
    var invalid = alwaysInvalid
      || recoverableInvalid
      || targetInvalid;
    var planElement = JsonSerializer.SerializeToElement(
      plan,
      CompactJsonOptions
    );
    var toolName = planElement.TryGetProperty(
      "tool",
      out var toolElement
    ) && toolElement.ValueKind == JsonValueKind.String
      ? toolElement.GetString()
      : null;
    var arguments = planElement.TryGetProperty(
      "arguments",
      out var argumentElement
    )
      ? argumentElement
      : JsonSerializer.SerializeToElement(
        new { }
      );
    if (
      hasTools
      && toolName is not null
      && !availableTools.Contains(
        toolName,
        StringComparer.OrdinalIgnoreCase
      )
    )
    {
      try
      {
        toolName = ToolNames.Resolve(
          toolName,
          availableTools
        ).CanonicalName;
      }
      catch (LocalActionException)
      {
      }
    }
    if (
      hasTools
      && toolName is not null
      && !availableTools.Contains(
        toolName,
        StringComparer.OrdinalIgnoreCase
      )
      && availableTools.Contains(
        LocalActionPlanner.RequestToolsetTool,
        StringComparer.Ordinal
      )
    )
    {
      var requestedTool = toolName;
      toolName = LocalActionPlanner.RequestToolsetTool;
      arguments = JsonSerializer.SerializeToElement(
        new
        {
          tools = new[]
          {
            requestedTool
          },
          reason = $"The specialist needs {requestedTool} to continue the current objective."
        },
        CompactJsonOptions
      );
    }
    if (
      trackedPlanFixture
      && hasPlan
      && toolName is not null
      && toolName is not LocalActionPlanner.RequestToolsetTool
        and not "create_execution_plan"
        and not "revise_execution_plan"
    )
    {
      var boundArguments = JsonNode.Parse(
        arguments.GetRawText()
      )!.AsObject();
      boundArguments["stepId"] = actionResults.Length == 0
        ? "step-1"
        : "step-2";
      arguments = JsonSerializer.SerializeToElement(
        boundArguments,
        CompactJsonOptions
      );
    }
    var validationFailed = actionResults.LastOrDefault() is
    {
      ToolName: "run_validation_profile"
    } validationResult
      && validationResult.Content.Contains(
        "Status: failed",
        StringComparison.Ordinal
      );
    var content = invalid
      ? string.Empty
      : hasTools
        ? toolName is null
          ? validationFailed
            ? "NO_LOCAL_ACTION_REQUIRED"
            : "The requested local work is complete."
          : string.Empty
        : JsonSerializer.Serialize(
          plan,
          CompactJsonOptions
        );
    var toolCalls = !invalid && hasTools && toolName is not null
      ? new[]
      {
        new
        {
          function = new
          {
            name = toolName,
            arguments
          }
        }
      }
      : null;

    if (
      toolCalls is not null
      && hasPlan
      && !hasResult
      && current.Contains(
        "multiple native tool calls",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      toolCalls =
      [
        toolCalls[0],
        new
        {
          function = new
          {
            name = "create_directory",
            arguments = JsonSerializer.SerializeToElement(
              new
              {
                path = "ignored-extra-call"
              },
              CompactJsonOptions
            )
          }
        }
      ];
    }

    var thinking = toolCalls is null
      ? null
      : $"I will use the Host tool {toolName} and inspect its authoritative result.";

    if (stream)
    {
      await WriteStreamingToolResponseAsync(
        response,
        model,
        content,
        thinking,
        toolCalls,
        current.Contains(
          "chronological thinking stream",
          StringComparison.OrdinalIgnoreCase
        )
          ? 800
          : 0,
        cancellationToken
      );
    }
    else
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.OK,
        new
        {
          message = new
          {
            role = "assistant",
            content,
            thinking,
            tool_calls = toolCalls
          },
          done = true
        },
        cancellationToken
      );
    }
  }

  private static string ExtractCompletionReviewPath(string content)
  {
    const string marker =
      "The latest changed files have not all been inspected after their latest mutation: ";
    var markerIndex = content.LastIndexOf(
      marker,
      StringComparison.Ordinal
    );
    if (markerIndex < 0)
    {
      return "hello.txt";
    }

    var start = markerIndex + marker.Length;
    var end = content.IndexOf(
      ". Use read_file",
      start,
      StringComparison.Ordinal
    );
    var paths = content[start..(end < 0 ? content.Length : end)];
    return paths.Split(
      ',',
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    )[0];
  }

  private static object CreateForkGameAction(int completedActionCount)
  {
    return completedActionCount switch
    {
      0 => new
      {
        tool = (string?)"list_files",
        arguments = (object)new
        {
          path = "fireworks",
          recursive = true
        },
        explanation = "Inspect the supplied fireworks folder before choosing integration paths."
      },
      1 => new
      {
        tool = (string?)"read_file",
        arguments = (object)new
        {
          path = "fireworks/firework_engine.js"
        },
        explanation = "Read the supplied fireworks public API before using it."
      },
      2 => new
      {
        tool = (string?)"read_file",
        arguments = (object)new
        {
          path = "fireworks/fireworks.css"
        },
        explanation = "Read the supplied fireworks stylesheet before linking it."
      },
      3 => new
      {
        tool = (string?)"create_file",
        arguments = (object)new
        {
          path = "words.js",
          content = ForkGameExecutionFixture.WordsJavaScript
        },
        explanation = "Create the requested fixed word collection."
      },
      4 => new
      {
        tool = (string?)"create_file",
        arguments = (object)new
        {
          path = "styles.css",
          content = ForkGameExecutionFixture.StylesCss
        },
        explanation = "Create the plain CSS game presentation."
      },
      5 => new
      {
        tool = (string?)"create_file",
        arguments = (object)new
        {
          path = "index.html",
          content = ForkGameExecutionFixture.IndexHtml
        },
        explanation = "Create the HTML entrypoint with the observed fireworks assets."
      },
      6 => new
      {
        tool = (string?)"create_file",
        arguments = (object)new
        {
          path = "game.js",
          content = ForkGameExecutionFixture.GameJavaScript
        },
        explanation = "Create the vanilla JavaScript guessing loop and terminal fireworks trigger."
      },
      7 => new
      {
        tool = (string?)"read_file",
        arguments = (object)new
        {
          path = "index.html"
        },
        explanation = "Review cross-file references and fireworks load order."
      },
      8 => new
      {
        tool = (string?)"read_file",
        arguments = (object)new
        {
          path = "words.js"
        },
        explanation = "Review the exact fixed word collection."
      },
      9 => new
      {
        tool = (string?)"read_file",
        arguments = (object)new
        {
          path = "game.js"
        },
        explanation = "Review the gameplay endings and observed fireworks API call."
      },
      10 => new
      {
        tool = (string?)"read_file",
        arguments = (object)new
        {
          path = "styles.css"
        },
        explanation = "Review the final plain CSS artifact."
      },
      _ => new
      {
        tool = (string?)"read_file",
        arguments = (object)new
        {
          path = "styles.css"
        },
        explanation = "Propose one redundant read-only action after the complete static review."
      }
    };
  }

  private static object CreateExecutionPlan(
    string objective
  )
  {
    if (objective.Contains(
      "specialist tracked plan",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_execution_plan",
        arguments = new
        {
          objective = "Create and inspect a tracked fixture",
          steps = new object[]
          {
            new
            {
              title = "Create tracked fixture"
            },
            new
            {
              title = "Inspect tracked fixture",
              dependsOn = new[]
              {
                1
              }
            }
          }
        },
        explanation = "The selected specialist proposes its own objective, steps, titles, and dependency."
      };
    }

    if (objective.Contains(
      "multi file static review",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      var corrected = objective.Contains(
        "execution-plan-quality-review",
        StringComparison.Ordinal
      );
      return new
      {
        tool = "create_execution_plan",
        arguments = new
        {
          objective = "Create and statically review a multi-file browser fixture",
          steps = corrected
            ? new[]
            {
              new { title = "Create integrated HTML entrypoint" },
              new { title = "Create JavaScript data file" },
              new { title = "Review integrated entrypoint references" },
              new { title = "Review JavaScript data file" }
            }
            : new[]
            {
              new { title = "Create integrated HTML entrypoint" },
              new { title = "Create JavaScript data file" }
            }
        },
        explanation = corrected
          ? "Add the Host-required static review steps."
          : "Omit static review so the Host can request a correction."
      };
    }

    if (objective.Contains(
      "vanilla manual scope",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      var scopedSteps = objective.Contains(
        "STRUCTURED_ACTION_CORRECTION",
        StringComparison.Ordinal
      )
        ? new[]
        {
          new
          {
            title = "Implement browser game script"
          }
        }
        :
        [
          new
          {
            title = "Implement browser game script"
          },
          new
          {
            title = "Run automated validation"
          }
        ];
      return new
      {
        tool = "create_execution_plan",
        arguments = new
        {
          objective = "Create the requested vanilla browser game artifact",
          steps = scopedSteps
        },
        explanation = objective.Contains(
          "STRUCTURED_ACTION_CORRECTION",
          StringComparison.Ordinal
        )
          ? "Remove the rejected validation step and keep the corrected file-only plan."
          : "Include an invalid validation step so the Host can request one bounded correction."
      };
    }

    if (objective.Contains(
      "host generated plan ids",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_execution_plan",
        arguments = new
        {
          objective,
          steps = new[]
          {
            new
            {
              title = "Perform requested local action"
            }
          }
        },
        explanation = "Return only a plan title and let the Host assign the stable step ID."
      };
    }

    if (objective.Contains(
      "recover premature prose after approved process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_execution_plan",
        arguments = new
        {
          objective,
          steps = new[]
          {
            new
            {
              id = "step-1",
              title = "Run approved process"
            },
            new
            {
              id = "step-2",
              title = "Create requested file"
            }
          }
        },
        explanation = "Create a two-step plan that must not stop after the process."
      };
    }

    var planObjective = objective.Length <= 240
      ? objective
      : "Complete the requested local action";
    var steps = new List<object>();

    if (objective.Contains("delete files direct", StringComparison.OrdinalIgnoreCase))
    {
      steps.Add(
        new
        {
          id = "step-1",
          title = "Delete selected files"
        }
      );
    }
    else if (objective.Contains("delete files", StringComparison.OrdinalIgnoreCase))
    {
      steps.Add(
        new
        {
          id = "step-1",
          title = "Inspect files selected for deletion"
        }
      );
      steps.Add(
        new
        {
          id = "step-2",
          title = "Delete inspected files"
        }
      );
    }
    else if (IsMutationFixture(
      objective
    ))
    {
      steps.Add(
        new
        {
          id = "step-1",
          title = "Inspect relevant project files"
        }
      );
    }

    if (!objective.Contains("delete files", StringComparison.OrdinalIgnoreCase))
    {
      steps.Add(
        new
        {
          id = $"step-{steps.Count + 1}",
          title = IsMutationFixture(
            objective
          )
            ? "Implement requested file changes"
            : "Perform requested local action"
        }
      );
    }

    if (
      objective.Contains(
        "validate",
        StringComparison.OrdinalIgnoreCase
      )
      || objective.Contains(
        "test",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      steps.Add(
        new
        {
          id = $"step-{steps.Count + 1}",
          title = "Run configured validation"
        }
      );
    }

    return new
    {
      tool = "create_execution_plan",
      arguments = new
      {
        objective = planObjective,
        steps
      },
      explanation = "Create the required visible plan."
    };
  }

  private static object CreateInvalidExecutionPlan(
    string objective
  )
  {
    return new
    {
      tool = "create_execution_plan",
      arguments = new
      {
        objective,
        steps = new[]
        {
          new
          {
            id = "step-1",
            title = new string(
              'x',
              101
            )
          }
        }
      },
      explanation = "Create an invalid visible plan fixture."
    };
  }

  private static object CreateMissingFieldsExecutionPlan(
    string objective
  )
  {
    return new
    {
      tool = "create_execution_plan",
      arguments = new
      {
        objective,
        steps = new[]
        {
          new
          {
            name = "Missing required id and title fields"
          }
        }
      },
      explanation = "Create a plan step without the required id and title fields."
    };
  }

  private static bool IsMutationFixture(
    string objective
  )
  {
    return objective.Contains(
      "write file",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "replace file",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "apply patch",
      StringComparison.OrdinalIgnoreCase
    );
  }

  private static bool IsQwenToolingMatrixFixture(
    string objective
  )
  {
    return objective.Contains(
      "Create hello.txt containing \"hello\".",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "Create README.md describing this project.",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "Create index.html with a Hello World page.",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "Create hello.py that prints \"hello\".",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "Recover after malformed process",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "Qwen premature completion correction",
      StringComparison.OrdinalIgnoreCase
    );
  }

  private static object CreateQwenToolingMatrixAction(
    string objective,
    IReadOnlyList<RecordedMessage> results,
    string allContent
  )
  {
    if (objective.Contains(
      "Qwen premature completion correction",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      if (
        results.Count == 0
        && !allContent.Contains(
          "HOST_COMPLETION_FACTS",
          StringComparison.Ordinal
        )
      )
      {
        return new
        {
          tool = (string?)null,
          arguments = (object)new { },
          explanation = "Attempt completion before the required mutation effect exists."
        };
      }

      if (results.Count == 0)
      {
        return new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "correction.txt",
            content = "created after authoritative completion correction"
          },
          explanation = "Create the missing file after the Host identified the unverified mutation requirement."
        };
      }

      return new
      {
        tool = (string?)null,
        arguments = (object)new { },
        explanation = "The corrected mutation effect is verified."
      };
    }

    if (results.Count == 0)
    {
      if (objective.Contains(
        "Recover after malformed process",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        return new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "recovery.txt",
            content = "verified before rejected process"
          },
          explanation = "Create and verify the requested file before the malformed process proposal."
        };
      }

      if (objective.Contains(
        "hello.txt",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        return new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "hello.txt",
            content = "hello"
          },
          explanation = "Create the requested text file."
        };
      }

      if (objective.Contains(
        "README.md",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        return new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "README.md",
            content = "# Agentic Router\n\nA local-first application for routed chat and supervised development tasks.\n"
          },
          explanation = "Create the requested project description."
        };
      }

      if (objective.Contains(
        "index.html",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        return new
        {
          tool = (string?)"create_file",
          arguments = (object)new
          {
            path = "index.html",
            content = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Hello World</title></head><body><h1>Hello World</h1></body></html>"
          },
          explanation = "Create the requested static page."
        };
      }

      return new
      {
        tool = (string?)"create_file",
        arguments = (object)new
        {
          path = "hello.py",
          content = "print(\"hello\")\n"
        },
        explanation = "Create the requested Python program."
      };
    }

    if (
      objective.Contains(
        "Recover after malformed process",
        StringComparison.OrdinalIgnoreCase
      )
      && !results.Any(result => result.ToolName == "run_process")
    )
    {
      return new
      {
        tool = (string?)"run_process",
        arguments = (object)new
        {
          executable = "dotnet",
          arguments = new[]
          {
            "bad\fform"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Return one malformed process argument after the verified file effect."
      };
    }

    if (
      objective.Contains(
        "hello.py",
        StringComparison.OrdinalIgnoreCase
      )
      && !objective.Contains(
        "Do not run it.",
        StringComparison.OrdinalIgnoreCase
      )
      && !results.Any(result => result.ToolName == "run_process")
    )
    {
      return new
      {
        tool = (string?)"run_process",
        arguments = (object)new
        {
          executable = OperatingSystem.IsWindows() ? "python" : "python3",
          arguments = new[]
          {
            "hello.py"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Run the requested executable program once to validate its behavior."
      };
    }

    return new
    {
      tool = (string?)null,
      arguments = (object)new { },
      explanation = "The requested effects are verified and no further tool is necessary."
    };
  }

  private static object CreateLocalActionPlan(
    string current
  )
  {
    if (current.Contains(
      "specialist tracked plan",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_file",
        arguments = new
        {
          path = "tracked-plan.txt",
          content = "created through a specialist-proposed plan"
        },
        explanation = "Create the fixture bound to the specialist-proposed plan step."
      };
    }

    if (current.Contains(
      "unknown tool alias",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "open_file",
        arguments = new
        {
          path = "hello.txt"
        },
        explanation = "Return a deliberately unapproved ambiguous alias."
      };
    }

    if (current.Contains(
      "resident protocol correction",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_file",
        arguments = new
        {
          path = "protocol-repaired.txt",
          content = "recovered after one host protocol correction"
        },
        explanation = "Complete the objective after applying the Host protocol correction."
      };
    }

    if (current.Contains(
      "path traversal create corrected",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_file",
        arguments = new
        {
          path = "../../rebased-create.txt",
          content = "created inside the trusted workspace"
        },
        explanation = "Return a creation path with excess parent traversal."
      };
    }

    if (current.Contains(
      "control character path recover",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "read_file",
        arguments = new
        {
          path = "fireworks\firework_engine.js"
        },
        explanation = "Return a path containing a JSON-decoded form-feed character."
      };
    }

    if (current.Contains(
      "control character process recover",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "dotnet",
          arguments = new[]
          {
            "fireworks\firework_engine.js"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Return a process argument containing a JSON-decoded form-feed character."
      };
    }

    if (current.Contains(
      "alias path traversal",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "read_doc",
        arguments = new
        {
          path = "../outside.txt"
        },
        explanation = "Use an approved read alias with an invalid path."
      };
    }

    if (current.Contains(
      "case canonical read file",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "READ_FILE",
        arguments = new
        {
          path = "hello.txt"
        },
        explanation = "Use a casing-only canonical tool variant."
      };
    }

    if (current.Contains(
      "alias read doc",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "Read_Doc",
        arguments = new
        {
          path = "hello.txt"
        },
        explanation = "Use a curated read alias."
      };
    }

    if (current.Contains(
      "recover premature prose after approved process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "dotnet",
          arguments = new[]
          {
            "--list-sdks"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Run the first approved process before continuing the remaining plan."
      };
    }

    if (current.Contains(
      "recover failed process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "agentic-router-missing-executable",
          arguments = new[]
          {
            "-la"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Attempt the unavailable listing process before recovering."
      };
    }

    if (current.Contains(
      "git metadata access",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "write_file",
        arguments = new
        {
          path = ".git/config",
          content = "forbidden"
        },
        explanation = "Attempt direct mutation of protected Git metadata."
      };
    }

    if (current.Contains(
      "path traversal",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "read_file",
        arguments = new
        {
          path = "../outside.txt"
        },
        explanation = "Attempt to leave the workspace."
      };
    }

    if (current.Contains(
      "delete files",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "read_file",
        arguments = new
        {
          path = "obsolete-a.txt"
        },
        explanation = "Inspect the first file before deletion."
      };
    }

    if (current.Contains(
      "create directory",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_directory",
        arguments = new
        {
          path = "generated"
        },
        explanation = "Create the requested directory."
      };
    }

    if (current.Contains(
      "duplicate workspace root edit",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_file",
        arguments = new
        {
          path = "workspace/hello.txt",
          content = "incorrect duplicate file"
        },
        explanation = "Incorrectly repeat the workspace root while trying to edit an existing file."
      };
    }

    if (current.Contains(
      "create README",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_file",
        arguments = new
        {
          path = "README.md",
          content = "# Sample Project\n\nCreated by the selected specialist.\n"
        },
        explanation = "Create the requested README at the trusted workspace root."
      };
    }

    if (current.Contains(
      "coding task",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "write_file",
        arguments = new
        {
          path = "Program.cs",
          content = "Console.WriteLine(\"fixed\");\n"
        },
        explanation = "Fix the inspected C# compile error."
      };
    }

    if (current.Contains(
      "apply patch",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "apply_patch",
        arguments = new
        {
          path = "hello.txt",
          replacements = new[]
          {
            new
            {
              oldText = "hello",
              newText = "patched"
            }
          }
        },
        explanation = "Apply the requested patch."
      };
    }

    if (current.Contains(
      "replace file",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "replace_text",
        arguments = new
        {
          path = "hello.txt",
          oldText = "hello",
          newText = "updated",
          replaceAll = false
        },
        explanation = "Replace text in the requested file."
      };
    }

    if (current.Contains(
      "write file",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "write_file",
        arguments = new
        {
          path = "hello.txt",
          content = "rewritten by agent"
        },
        explanation = "Overwrite the requested existing file."
      };
    }

    if (current.Contains(
      "create nested file",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_file",
        arguments = new
        {
          path = "nested/hello.txt",
          content = "nested agent output"
        },
        explanation = "Create the requested nested file."
      };
    }

    if (current.Contains(
      "create file",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "create_file",
        arguments = new
        {
          path = "hello.txt",
          content = "hello from agent"
        },
        explanation = "Create the requested file."
      };
    }

    if (current.Contains(
      "read file",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "read_file",
        arguments = new
        {
          path = "hello.txt"
        },
        explanation = "Read the requested file."
      };
    }

    if (current.Contains(
      "file info",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "get_file_info",
        arguments = new
        {
          path = "hello.txt"
        },
        explanation = "Inspect bounded file metadata."
      };
    }

    if (current.Contains(
      "search text",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "search_text",
        arguments = new
        {
          path = ".",
          query = "hello"
        },
        explanation = "Search text without starting a shell."
      };
    }

    if (current.Contains(
      "list files",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "list_files",
        arguments = new
        {
          path = ".",
          recursive = false
        },
        explanation = "List the trusted workspace."
      };
    }

    if (current.Contains(
      "repeat denied process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "git",
          arguments = new[]
          {
            "clean",
            "-fd"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Repeat the denied process fixture."
      };
    }

    if (current.Contains(
      "unknown process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "dotnet",
          arguments = new[]
          {
            "--list-sdks"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Run a non-allowlisted structured process."
      };
    }

    if (current.Contains(
      "destructive process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "git",
          arguments = new[]
          {
            "clean",
            "-fd"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Attempt a destructive process."
      };
    }

    if (current.Contains(
      "run process",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new
      {
        tool = "run_process",
        arguments = new
        {
          executable = "dotnet",
          arguments = new[]
          {
            "--version"
          },
          workingDirectory = ".",
          timeoutSeconds = 10
        },
        explanation = "Run a safe structured process."
      };
    }

    return new
    {
      tool = (string?)null,
      arguments = new { },
      explanation = "No local action is needed."
    };
  }

  private async Task StreamTargetAsync(
    HttpListenerResponse response,
    string model,
    IReadOnlyList<RecordedMessage> messages,
    int? contextTokens,
    CancellationToken cancellationToken
  )
  {
    var current = messages.Last().Content;
    var attempt = _generationAttempts.AddOrUpdate(
      current,
      1,
      (
        _,
        count
      ) => count + 1
    );
    var residentLoaded = _loaded.ContainsKey(
      "router:latest"
    );
    var shouldFailForMemory =
      current.Contains(
        "memory pressure recover",
        StringComparison.OrdinalIgnoreCase
      )
      && attempt == 1
      && residentLoaded;
    var shouldAlwaysFail =
      current.Contains(
        "memory pressure fail",
        StringComparison.OrdinalIgnoreCase
      );

    if (current.Contains(
      "generic HTTP failure",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.ServiceUnavailable,
        new
        {
          error = "temporary upstream failure"
        },
        cancellationToken
      );
      return;
    }

    if (shouldFailForMemory || shouldAlwaysFail)
    {
      await WriteJsonAsync(
        response,
        HttpStatusCode.InternalServerError,
        new
        {
          error = "model requires more system memory than is currently available"
        },
        cancellationToken
      );
      return;
    }

    AddLoadedModel(
      model,
      300,
      contextTokens
    );
    var answer = BuildAnswer(
      current,
      model,
      messages.Count
    );
    var isStreamingMarkdownPreview = current.Contains(
      "streaming markdown preview",
      StringComparison.OrdinalIgnoreCase
    );
    var parts = isStreamingMarkdownPreview
      ? new[]
      {
        """
        # Live heading

        ```json
        {"active": true, "count": 2}
        ```
        """,
        "\n\nPreview remained active."
      }
      : Split(
        answer,
        3
      );
    response.StatusCode = (int)HttpStatusCode.OK;
    response.ContentType = "application/x-ndjson";
    response.SendChunked = true;
    var includeThinking = current.Contains(
      "show thinking",
      StringComparison.OrdinalIgnoreCase
    );
    var partIndex = 0;

    foreach (var part in parts)
    {
      await WriteChunkAsync(
        response,
        model,
        part,
        false,
        cancellationToken,
        includeThinking
          ? partIndex == 0
            ? "I need to inspect the request and choose the relevant response. "
            : "The response should remain concise and grounded."
          : null
      );
      partIndex++;

      if (messages.Any(
        message => message.Content.Contains(
          "cancel stream",
          StringComparison.OrdinalIgnoreCase
        )
      ))
      {
        await Task.Delay(
          TimeSpan.FromSeconds(
            10
          ),
          cancellationToken
        );
      }
      else
      {
        await Task.Delay(
          isStreamingMarkdownPreview
            ? 1_000
            : 120,
          cancellationToken
        );
      }
    }

    await WriteChunkAsync(
      response,
      model,
      string.Empty,
      true,
      cancellationToken
    );
    response.Close();
  }

  private static string BuildAnswer(
    string current,
    string model,
    int messageCount
  )
  {
    if (current.Contains(
      "Reply with exactly: OK",
      StringComparison.Ordinal
    ))
    {
      return "OK";
    }

    if (current.Contains(
      "long token",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return new string(
        'X',
        500
      );
    }

    if (current.Contains(
      "scroll stream",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return string.Join(
        "\n\n",
        Enumerable.Range(
          1,
          80
        ).Select(
          index => $"Streaming paragraph {index}: {new string('s', 40)}"
        )
      );
    }

    if (current.Contains(
      "markdown fixture",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return """
        # Heading

        **Bold** and *italic* with `inline`.

        - first
        - second

        > quote

        ```csharp
        Console.WriteLine("a very long source line that should scroll horizontally instead of wrapping xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
        ```

        | Name | Value |
        | --- | --- |
        | Alpha | 1 |

        ---

        [safe](https://example.com) [unsafe](javascript:alert(1))

        <script>window.__agenticInjected = true;</script>
        """;
    }

    return $"Hello from {model}. Context messages: {messageCount}.";
  }

  private static IReadOnlyList<string> Split(
    string value,
    int count
  )
  {
    var parts = new List<string>();
    var start = 0;

    for (var index = count; index > 1; index--)
    {
      var length = (value.Length - start) / index;
      parts.Add(
        value.Substring(
          start,
          length
        )
      );
      start += length;
    }

    parts.Add(
      value[start..]
    );
    return parts;
  }

  private static async Task WriteChunkAsync(
    HttpListenerResponse response,
    string model,
    string content,
    bool done,
    CancellationToken cancellationToken,
    string? thinking = null
  )
  {
    var bytes = Encoding.UTF8.GetBytes(
      JsonSerializer.Serialize(
        new
        {
          model,
          message = new
          {
            role = "assistant",
            content,
            thinking
          },
          done,
          prompt_eval_count = done
            ? 120
            : (int?)null,
          eval_count = done
            ? 30
            : (int?)null
        },
        CompactJsonOptions
      ) + "\n"
    );
    await response.OutputStream.WriteAsync(
      bytes,
      cancellationToken
    );
    await response.OutputStream.FlushAsync(
      cancellationToken
    );
  }

  private static async Task WriteStreamingToolResponseAsync(
    HttpListenerResponse response,
    string model,
    string content,
    string? thinking,
    object? toolCalls,
    int thinkingDelayMilliseconds,
    CancellationToken cancellationToken
  )
  {
    response.StatusCode = (int)HttpStatusCode.OK;
    response.ContentType = "application/x-ndjson";
    response.SendChunked = true;

    if (!string.IsNullOrEmpty(
      thinking
    ))
    {
      var split = Math.Max(
        1,
        thinking.Length / 2
      );
      await WriteToolChunkAsync(
        response,
        model,
        string.Empty,
        thinking[..split],
        null,
        false,
        cancellationToken
      );

      if (thinkingDelayMilliseconds > 0)
      {
        await Task.Delay(
          thinkingDelayMilliseconds,
          cancellationToken
        );
      }

      await WriteToolChunkAsync(
        response,
        model,
        string.Empty,
        thinking[split..],
        null,
        false,
        cancellationToken
      );
    }

    await WriteToolChunkAsync(
      response,
      model,
      content,
      null,
      toolCalls,
      true,
      cancellationToken
    );
  }

  private static async Task WriteToolChunkAsync(
    HttpListenerResponse response,
    string model,
    string content,
    string? thinking,
    object? toolCalls,
    bool done,
    CancellationToken cancellationToken
  )
  {
    var json = JsonSerializer.Serialize(
        new
        {
          model,
          message = new
          {
            role = "assistant",
            content,
            thinking,
            tool_calls = toolCalls
          },
          done,
          prompt_eval_count = done
            ? 120
            : (int?)null,
          eval_count = done
            ? 30
            : (int?)null
        },
        CompactJsonOptions
      );
    var bytes = Encoding.UTF8.GetBytes(
      json + "\n"
    );
    await response.OutputStream.WriteAsync(
      bytes,
      cancellationToken
    );
    await response.OutputStream.FlushAsync(
      cancellationToken
    );
  }

  private static async Task RespondWithToolCallAsync(
    HttpListenerResponse response,
    string tool,
    object arguments,
    CancellationToken cancellationToken
  )
  {
    await WriteJsonAsync(
      response,
      HttpStatusCode.OK,
      new
      {
        message = new
        {
          role = "assistant",
          content = string.Empty,
          thinking = $"I will use the Host tool {tool} and inspect its authoritative result.",
          tool_calls = new[]
          {
            new
            {
              function = new
              {
                name = tool,
                arguments
              }
            }
          }
        },
        done = true
      },
      cancellationToken
    );
  }

  private static string ExtractRequiredPolicyValue(
    string content,
    string propertyName
  )
  {
    const string marker = "REQUIRED_POLICY (copy the four typed fields exactly):\n";
    var markerIndex = content.LastIndexOf(
      marker,
      StringComparison.Ordinal
    );
    if (markerIndex < 0)
    {
      throw new InvalidOperationException(
        "FunctionGemma recovery fixture did not receive REQUIRED_POLICY."
      );
    }

    using var document = JsonDocument.Parse(
      content[(markerIndex + marker.Length)..]
    );
    return document.RootElement.GetProperty(
      propertyName
    ).GetString() ?? throw new InvalidOperationException(
      $"FunctionGemma recovery fixture omitted {propertyName}."
    );
  }

  private static async Task WriteJsonAsync(
    HttpListenerResponse response,
    HttpStatusCode statusCode,
    object payload,
    CancellationToken cancellationToken
  )
  {
    var node = JsonSerializer.SerializeToNode(
      payload,
      TestJson.Options
    );
    if (
      statusCode == HttpStatusCode.OK
      && node is JsonObject root
      && root["done"]?.GetValue<bool>() == true
    )
    {
      root["prompt_eval_count"] ??= 40;
      root["eval_count"] ??= 8;
    }
    var bytes = JsonSerializer.SerializeToUtf8Bytes(
      node,
      TestJson.Options
    );
    response.StatusCode = (int)statusCode;
    response.ContentType = "application/json";
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(
      bytes,
      cancellationToken
    );
    response.Close();
  }

  private static int GetAvailablePort()
  {
    var listener = new TcpListener(
      IPAddress.Loopback,
      0
    );
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
  }

  private sealed record ModelDefinition(
    string Name,
    long Size
  );

  private sealed record RunningModel(
    string Name,
    string Digest,
    long Size,
    long VramSize,
    int ContextTokens,
    DateTimeOffset? ExpiresAt
  );
}

internal sealed record RecordedChatRequest(
  string Model,
  bool Stream,
  int? KeepAlive,
  IReadOnlyList<RecordedMessage> Messages,
  bool HasTools,
  IReadOnlyList<string> AvailableTools,
  int? ContextTokens,
  int? PredictTokens,
  int? MainGpu
);

internal sealed record RecordedMessage(
  string Role,
  string Content,
  string? ToolName,
  IReadOnlyList<RecordedToolCall> ToolCalls,
  int ImageCount
);

internal sealed record RecordedToolCall(
  string Name,
  JsonElement Arguments
);
