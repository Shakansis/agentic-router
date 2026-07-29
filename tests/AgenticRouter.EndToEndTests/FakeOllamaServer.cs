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
    )
  ];

  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _shutdown = new();
  private readonly ConcurrentQueue<RecordedChatRequest> _requests = new();
  private readonly ConcurrentQueue<RecordedChatRequest> _allRequests = new();
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

  private static async Task ClassifyAsync(
    HttpListenerResponse response,
    IReadOnlyList<RecordedMessage> messages,
    CancellationToken cancellationToken
  )
  {
    var current = messages.Last().Content;
    var intention = current.Contains(
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
      : current.Contains(
        "architecture",
        StringComparison.OrdinalIgnoreCase
      )
        ? "software-architecture"
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
    else
    {
      content = JsonSerializer.Serialize(
        new
        {
          intention,
          confidence = 0.93
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
