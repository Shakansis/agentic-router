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

    private static readonly object[] Models =
    [
      new
    {
      name = "router:latest",
      size = 1_000_000_000L,
      modified_at = "2026-07-28T09:00:00Z"
    },
    new
    {
      name = "alpha:latest",
      size = 4_200_000_000L,
      modified_at = "2026-07-28T10:00:00Z"
    },
    new
    {
      name = "docs:latest",
      size = 5_100_000_000L,
      modified_at = "2026-07-28T10:30:00Z"
    },
    new
    {
      name = "beta:code",
      size = 7_300_000_000L,
      modified_at = "2026-07-28T11:00:00Z"
    }
    ];

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<RecordedChatRequest> _requests = new();
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
            if (
              context.Request.HttpMethod == HttpMethod.Get.Method
              && context.Request.Url?.AbsolutePath == "/api/tags"
            )
            {
                await WriteJsonAsync(
                  context.Response,
                  HttpStatusCode.OK,
                  new
                  {
                      models = Models
                  },
                  cancellationToken
                );
                return;
            }

            if (
              context.Request.HttpMethod == HttpMethod.Post.Method
              && context.Request.Url?.AbsolutePath == "/api/chat"
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
        _requests.Enqueue(
          new RecordedChatRequest(
            model,
            stream,
            messages
          )
        );

        if (!IsInstalled(
          model
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

        if (!stream)
        {
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

    private static async Task ClassifyAsync(
      HttpListenerResponse response,
      IReadOnlyList<RecordedMessage> messages,
      CancellationToken cancellationToken
    )
    {
        var current = messages.Last().Content;
        string content;

        if (current.Contains(
          "invalid router",
          StringComparison.OrdinalIgnoreCase
        ))
        {
            content = "not-json";
        }
        else
        {
            var intention = current.Contains(
              "document",
              StringComparison.OrdinalIgnoreCase
            ) || current.Contains(
              "markdown",
              StringComparison.OrdinalIgnoreCase
            )
              ? "documentation"
              : current.Contains(
                "architecture",
                StringComparison.OrdinalIgnoreCase
              )
                ? "software-architecture"
                : current.Contains(
                  "code",
                  StringComparison.OrdinalIgnoreCase
                )
                  ? "software-development"
                  : current.Contains(
                    "review",
                    StringComparison.OrdinalIgnoreCase
                  )
                    ? "review-and-testing"
                    : "general-chat";
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

    private static async Task StreamTargetAsync(
      HttpListenerResponse response,
      string model,
      IReadOnlyList<RecordedMessage> messages,
      CancellationToken cancellationToken
    )
    {
        var current = messages.Last().Content;
        var answer = current.Contains(
          "markdown fixture",
          StringComparison.OrdinalIgnoreCase
        )
          ? """
        # Heading

        **Bold** and *italic* with `inline`.

        - first
        - second

        > quote

        ```csharp
        Console.WriteLine("safe");
        ```

        | Name | Value |
        | --- | --- |
        | Alpha | 1 |

        ---

        [safe](https://example.com) [unsafe](javascript:alert(1))

        <script>window.__agenticInjected = true;</script>
        """
          : $"Hello from {model}. Context messages: {messages.Count}. "
            + $"System: {messages.First().Content}";
        var split = Math.Max(
          1,
          answer.Length / 2
        );

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/x-ndjson";
        response.SendChunked = true;

        await WriteChunkAsync(
          response,
          model,
          answer[..split],
          false,
          cancellationToken
        );
        await Task.Delay(
          180,
          cancellationToken
        );
        await WriteChunkAsync(
          response,
          model,
          answer[split..],
          false,
          cancellationToken
        );
        await WriteChunkAsync(
          response,
          model,
          string.Empty,
          true,
          cancellationToken
        );
        response.Close();
    }

    private static bool IsInstalled(
      string model
    )
    {
        return Models
          .Select(
            item => JsonSerializer.SerializeToElement(
              item
            ).GetProperty(
              "name"
            ).GetString()
          )
          .Contains(
            model,
            StringComparer.Ordinal
          );
    }

    private static async Task WriteChunkAsync(
      HttpListenerResponse response,
      string model,
      string content,
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
                  content
              },
              done
          },
          CompactJsonOptions
        );
        var bytes = Encoding.UTF8.GetBytes(
          $"{json}\n"
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
}

internal sealed record RecordedChatRequest(
  string Model,
  bool Stream,
  IReadOnlyList<RecordedMessage> Messages
);

internal sealed record RecordedMessage(
  string Role,
  string Content
);
