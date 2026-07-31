using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed class FakeCloudProviderServer : IAsyncDisposable
{
  private static readonly JsonSerializerOptions CompactJson = new(
    JsonSerializerDefaults.Web
  );

  private readonly HttpListener _listener = new();
  private readonly CancellationTokenSource _shutdown = new();
  private readonly ConcurrentQueue<FakeCloudRequest> _requests = new();
  private Task? _listenTask;

  private FakeCloudProviderServer(
    int port
  )
  {
    BaseUrl = $"http://127.0.0.1:{port}";
    _listener.Prefixes.Add(
      $"{BaseUrl}/"
    );
  }

  public string BaseUrl { get; }

  public IReadOnlyList<FakeCloudRequest> Requests => _requests.ToArray();

  public static FakeCloudProviderServer Start()
  {
    var server = new FakeCloudProviderServer(
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
      catch (Exception exception) when (
        exception is OperationCanceledException
        or ObjectDisposedException
        or HttpListenerException
      )
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
      var path = context.Request.Url?.AbsolutePath
        ?? "/";
      var body = context.Request.HasEntityBody
        ? await new StreamReader(
          context.Request.InputStream,
          context.Request.ContentEncoding
        ).ReadToEndAsync(
          cancellationToken
        )
        : string.Empty;
      _requests.Enqueue(
        new FakeCloudRequest(
          context.Request.HttpMethod,
          path,
          body,
          context.Request.Headers["Authorization"],
          context.Request.Headers["x-goog-api-key"]
        )
      );

      var rejectedKey = context.Request.Headers["Authorization"]?
        .Split(
          ' ',
          2,
          StringSplitOptions.RemoveEmptyEntries
        )
        .LastOrDefault()
        ?? context.Request.Headers["x-goog-api-key"];

      if (
        rejectedKey?.Contains(
          "rejected",
          StringComparison.Ordinal
        ) == true
      )
      {
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.ContentType = "application/json";
        await WriteRawAsync(
          context.Response,
          JsonSerializer.Serialize(
            new
            {
              error = new
              {
                message = $"Rejected credential {rejectedKey}"
              }
            },
            CompactJson
          ),
          cancellationToken
        );
        context.Response.Close();
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Get.Method
        && path == "/groq/openai/v1/models"
      )
      {
        await WriteJsonAsync(
          context.Response,
          new
          {
            data = new[]
            {
              new
              {
                id = "openai/gpt-oss-120b",
                created = 1_700_000_000,
                revision = "groq-test-r1",
                context_window = 131_072,
                capabilities = new
                {
                  chat = true,
                  streaming = true,
                  tools = true
                }
              }
            }
          },
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Get.Method
        && path == "/cerebras/v1/models"
      )
      {
        await WriteJsonAsync(
          context.Response,
          new
          {
            data = new[]
            {
              new
              {
                id = "gpt-oss-120b",
                created = 1_700_000_001,
                revision = "cerebras-test-r1"
              }
            }
          },
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Get.Method
        && path == "/cerebras/public/v1/models/gpt-oss-120b"
      )
      {
        await WriteJsonAsync(
          context.Response,
          new
          {
            id = "gpt-oss-120b",
            context_window = 65_536,
            capabilities = new
            {
              chat = true,
              streaming = true,
              tools = true,
              vision = false
            },
            pricing = new
            {
              prompt = "0.00000035",
              completion = "0.00000075"
            }
          },
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Get.Method
        && path == "/gemini/v1beta/models"
      )
      {
        await WriteJsonAsync(
          context.Response,
          new
          {
            models = new[]
            {
              new
              {
                name = "models/gemini-test-flash",
                version = "gemini-test-r1",
                inputTokenLimit = 1_048_576,
                supportedGenerationMethods = new[]
                {
                  "generateContent",
                  "streamGenerateContent"
                }
              }
            }
          },
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Post.Method
        && (
          path == "/groq/openai/v1/chat/completions"
          || path == "/cerebras/v1/chat/completions"
        )
      )
      {
        await HandleOpenAiChatAsync(
          context,
          body,
          cancellationToken
        );
        return;
      }

      if (
        context.Request.HttpMethod == HttpMethod.Post.Method
        && path.StartsWith(
          "/gemini/v1beta/models/",
          StringComparison.Ordinal
        )
      )
      {
        await HandleGeminiChatAsync(
          context,
          body,
          path,
          cancellationToken
        );
        return;
      }

      context.Response.StatusCode = (int)HttpStatusCode.NotFound;
      context.Response.Close();
    }
    catch (Exception exception) when (
      exception is IOException
      or HttpListenerException
      or ObjectDisposedException
      or OperationCanceledException
    )
    {
      context.Response.Abort();
    }
  }

  private static async Task HandleOpenAiChatAsync(
    HttpListenerContext context,
    string body,
    CancellationToken cancellationToken
  )
  {
    using var document = JsonDocument.Parse(
      body
    );
    var root = document.RootElement;
    var stream = root.TryGetProperty(
      "stream",
      out var streamElement
    ) && streamElement.ValueKind == JsonValueKind.True;
    AddRateLimitHeaders(
      context.Response
    );

    if (stream)
    {
      context.Response.StatusCode = (int)HttpStatusCode.OK;
      context.Response.ContentType = "text/event-stream";
      await WriteSseAsync(
        context.Response,
        new
        {
          choices = new[]
          {
            new
            {
              delta = new
              {
                content = "cloud answer"
              }
            }
          }
        },
        cancellationToken
      );
      await WriteSseAsync(
        context.Response,
        new
        {
          choices = Array.Empty<object>(),
          usage = new
          {
            prompt_tokens = 12,
            completion_tokens = 3,
            total_tokens = 15
          }
        },
        cancellationToken
      );
      await WriteRawAsync(
        context.Response,
        "data: [DONE]\n\n",
        cancellationToken
      );
      context.Response.Close();
      return;
    }

    if (
      root.TryGetProperty(
        "tools",
        out var tools
      )
      && tools.ValueKind == JsonValueKind.Array
    )
    {
      var tool = tools[0].GetProperty(
        "function"
      ).GetProperty(
        "name"
      ).GetString()!;

      if (
        tool == "benchmark_edit"
        && (
          !root.TryGetProperty(
            "messages",
            out var messages
          )
          || !messages.EnumerateArray().Any(
            message => message.TryGetProperty(
                "role",
                out var role
              )
              && role.GetString() == "tool"
              && message.TryGetProperty(
                "tool_call_id",
                out var callId
              )
              && callId.GetString() == "call-test"
          )
        )
      )
      {
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        context.Response.ContentType = "application/json";
        await WriteRawAsync(
          context.Response,
          "{\"error\":{\"message\":\"tool_call_id mismatch\"}}",
          cancellationToken
        );
        context.Response.Close();
        return;
      }

      await WriteJsonAsync(
        context.Response,
        new
        {
          choices = new[]
          {
            new
            {
              message = new
              {
                role = "assistant",
                content = (string?)null,
                tool_calls = new[]
                {
                  new
                  {
                    id = "call-test",
                    type = "function",
                    function = new
                    {
                      name = tool,
                      arguments = ToolArguments(
                        tool
                      )
                    }
                  }
                }
              }
            }
          },
          usage = new
          {
            prompt_tokens = 20,
            completion_tokens = 4
          }
        },
        cancellationToken
      );
      return;
    }

    await WriteJsonAsync(
      context.Response,
      new
      {
        choices = new[]
        {
          new
          {
            message = new
            {
              role = "assistant",
              content = "{\"intent\":\"general-chat\"}"
            }
          }
        },
        usage = new
        {
          prompt_tokens = 10,
          completion_tokens = 5
        }
      },
      cancellationToken
    );
  }

  private static async Task HandleGeminiChatAsync(
    HttpListenerContext context,
    string body,
    string path,
    CancellationToken cancellationToken
  )
  {
    using var document = JsonDocument.Parse(
      body
    );
    var root = document.RootElement;
    var functionName = root.TryGetProperty(
      "tools",
      out var tools
    )
      ? tools[0].GetProperty(
        "functionDeclarations"
      )[0].GetProperty(
        "name"
      ).GetString()
      : null;
    object response = functionName is null
      ? new
      {
        candidates = new[]
        {
          new
          {
            content = new
            {
              role = "model",
              parts = new object[]
              {
                new
                {
                  text = path.Contains(
                    "streamGenerateContent",
                    StringComparison.Ordinal
                  )
                    ? "gemini cloud answer"
                    : "{\"intent\":\"general-chat\"}"
                }
              }
            }
          }
        },
        usageMetadata = new
        {
          promptTokenCount = 11,
          candidatesTokenCount = 4
        }
      }
      : new
      {
        candidates = new[]
        {
          new
          {
            content = new
            {
              role = "model",
              parts = new object[]
              {
                new
                {
                  functionCall = new
                  {
                    name = functionName,
                    args = JsonSerializer.Deserialize<JsonElement>(
                      ToolArguments(
                        functionName
                      )
                    )
                  }
                }
              }
            }
          }
        },
        usageMetadata = new
        {
          promptTokenCount = 21,
          candidatesTokenCount = 5
        }
      };

    if (path.Contains(
      "streamGenerateContent",
      StringComparison.Ordinal
    ))
    {
      context.Response.StatusCode = (int)HttpStatusCode.OK;
      context.Response.ContentType = "text/event-stream";
      await WriteSseAsync(
        context.Response,
        response,
        cancellationToken
      );
      context.Response.Close();
      return;
    }

    await WriteJsonAsync(
      context.Response,
      response,
      cancellationToken
    );
  }

  private static string ToolArguments(
    string tool
  )
  {
    return tool switch
    {
      "benchmark_echo" => "{\"value\":\"ok\"}",
      "benchmark_plan" =>
        "{\"objective\":\"verify\",\"steps\":[{\"title\":\"one\"},{\"title\":\"two\"}]}",
      "benchmark_read" => "{\"path\":\"sample.txt\"}",
      "benchmark_edit" =>
        "{\"path\":\"sample.txt\",\"content\":\"after\"}",
      _ => "{}"
    };
  }

  private static void AddRateLimitHeaders(
    HttpListenerResponse response
  )
  {
    response.Headers["x-ratelimit-limit-requests"] = "30";
    response.Headers["x-ratelimit-remaining-requests"] = "29";
    response.Headers["x-ratelimit-reset-requests"] = "60s";
    response.Headers["x-ratelimit-limit-tokens"] = "6000";
    response.Headers["x-ratelimit-remaining-tokens"] = "5900";
    response.Headers["x-ratelimit-reset-tokens"] = "60s";
  }

  private static async Task WriteJsonAsync(
    HttpListenerResponse response,
    object payload,
    CancellationToken cancellationToken
  )
  {
    response.StatusCode = (int)HttpStatusCode.OK;
    response.ContentType = "application/json";
    await WriteRawAsync(
      response,
      JsonSerializer.Serialize(
        payload,
        TestJson.Options
      ),
      cancellationToken
    );
    response.Close();
  }

  private static Task WriteSseAsync(
    HttpListenerResponse response,
    object payload,
    CancellationToken cancellationToken
  )
  {
    return WriteRawAsync(
      response,
      $"data: {JsonSerializer.Serialize(payload, CompactJson)}\n\n",
      cancellationToken
    );
  }

  private static async Task WriteRawAsync(
    HttpListenerResponse response,
    string payload,
    CancellationToken cancellationToken
  )
  {
    var bytes = Encoding.UTF8.GetBytes(
      payload
    );
    await response.OutputStream.WriteAsync(
      bytes,
      cancellationToken
    );
    await response.OutputStream.FlushAsync(
      cancellationToken
    );
  }

  private static int GetAvailablePort()
  {
    using var listener = new TcpListener(
      IPAddress.Loopback,
      0
    );
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }
}

internal sealed record FakeCloudRequest(
  string Method,
  string Path,
  string Body,
  string? Authorization,
  string? GoogleApiKey
);
