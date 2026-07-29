using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed class FakeOllamaServer : IAsyncDisposable
{
  private static readonly JsonSerializerOptions CompactJsonOptions = new(
    JsonSerializerDefaults.Web
  );

  private static readonly ModelDefinition[] Models =
  [
    new(
      "router:latest",
      1_000_000_000L
    ),
    new(
      "alpha:latest",
      4_200_000_000L
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
    )
  ];

  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _shutdown = new();
  private readonly ConcurrentQueue<RecordedChatRequest> _requests = new();
  private readonly ConcurrentQueue<RecordedChatRequest> _allRequests = new();
  private readonly ConcurrentQueue<string> _capabilityQueries = new();
  private readonly ConcurrentDictionary<string, RunningModel> _loaded = new(
    StringComparer.Ordinal
  );
  private readonly ConcurrentDictionary<string, int> _generationAttempts = new(
    StringComparer.Ordinal
  );
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
    _generationAttempts.Clear();
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
                modified_at = "2026-07-28T10:00:00Z"
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
                size = model.Size,
                size_vram = model.VramSize,
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
        capabilities = string.Equals(
          model,
          "command-r:latest",
          StringComparison.Ordinal
        )
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
          message.GetProperty(
            "content"
          ).GetString() ?? string.Empty
        )
      )
      .ToArray();
    var recorded = new RecordedChatRequest(
      model,
      stream,
      keepAlive,
      messages
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
        keepAlive.Value
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
        -1
      );
      await ClassifyAsync(
        context.Response,
        model,
        messages,
        cancellationToken
      );
      return;
    }

    await StreamTargetAsync(
      context.Response,
      model,
      messages,
      cancellationToken
    );
  }

  private void UpdateResidency(
    string model,
    int keepAlive
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
      keepAlive
    );
  }

  private void AddLoadedModel(
    string model,
    int keepAlive
  )
  {
    var definition = Models.Single(
      candidate => candidate.Name == model
    );
    _loaded[model] = new RunningModel(
      model,
      definition.Size,
      definition.Size * 3 / 4,
      keepAlive < 0
        ? null
        : DateTimeOffset.UtcNow.AddMinutes(
          5
        )
    );
  }

  private async Task ClassifyAsync(
    HttpListenerResponse response,
    string model,
    IReadOnlyList<RecordedMessage> messages,
    CancellationToken cancellationToken
  )
  {
    if (messages.Any(
      message => message.Content.Contains(
        "LOCAL_ACTION_PLANNER_V1",
        StringComparison.Ordinal
      )
    ))
    {
      await PlanLocalActionAsync(
        response,
        model,
        messages,
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

  private static async Task PrepareExpertGuidanceAsync(
    HttpListenerResponse response,
    IReadOnlyList<RecordedMessage> messages,
    CancellationToken cancellationToken
  )
  {
    var current = messages.Last(
      message => message.Role == "user"
    ).Content;
    var guidance = current.Contains(
      "empty takeover guidance",
      StringComparison.OrdinalIgnoreCase
    )
      ? string.Empty
      : "Execute the requested change using the controlled local tools. "
        + $"Original user request: {current}. "
        + "Preserve the exact fixture paths and contents implied by that request.";

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

  private async Task PlanLocalActionAsync(
    HttpListenerResponse response,
    string model,
    IReadOnlyList<RecordedMessage> messages,
    CancellationToken cancellationToken
  )
  {
    var current = messages.Last(
      message => message.Role == "user"
    ).Content;
    var attempt = _generationAttempts.AddOrUpdate(
      $"planner:{current}",
      1,
      (
        _,
        count
      ) => count + 1
    );
    var hasResult = messages.Any(
      message => message.Content.StartsWith(
        "LOCAL_ACTION_RESULT",
        StringComparison.Ordinal
      )
    );
    var allContent = string.Join(
      "\n",
      messages.Select(
        message => message.Content
      )
    );
    var latestResult = messages.LastOrDefault(
      message => message.Content.StartsWith(
        "LOCAL_ACTION_RESULT",
        StringComparison.Ordinal
      )
    )?.Content;
    object plan;

    if (
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
        current.Contains(
          "retry unknown tool",
          StringComparison.OrdinalIgnoreCase
        )
        && attempt == 1
      )
      {
        plan = new
        {
          tool = "unknown_tool",
          arguments = new { },
          explanation = "Invalid tool fixture."
        };
      }
      else
      {
        plan = CreateLocalActionPlan(
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
    ) && attempt < 3;
    var targetInvalid = string.Equals(
      model,
      "command-r:latest",
      StringComparison.Ordinal
    ) && current.Contains(
      "target planner invalid",
      StringComparison.OrdinalIgnoreCase
    );
    var content = alwaysInvalid || recoverableInvalid || targetInvalid
      ? string.Empty
      : JsonSerializer.Serialize(
        plan,
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

  private static object CreateLocalActionPlan(
    string current
  )
  {
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
      300
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

    foreach (var part in parts)
    {
      await WriteChunkAsync(
        response,
        model,
        part,
        false,
        cancellationToken
      );

      if (current.Contains(
        "cancel stream",
        StringComparison.OrdinalIgnoreCase
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
    CancellationToken cancellationToken
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
            content
          },
          done
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

  private static async Task WriteJsonAsync(
    HttpListenerResponse response,
    HttpStatusCode statusCode,
    object payload,
    CancellationToken cancellationToken
  )
  {
    var bytes = JsonSerializer.SerializeToUtf8Bytes(
      payload,
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
    long Size,
    long VramSize,
    DateTimeOffset? ExpiresAt
  );
}

internal sealed record RecordedChatRequest(
  string Model,
  bool Stream,
  int? KeepAlive,
  IReadOnlyList<RecordedMessage> Messages
);

internal sealed record RecordedMessage(
  string Role,
  string Content
);
