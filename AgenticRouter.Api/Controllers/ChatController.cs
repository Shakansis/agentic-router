using System.Text.Json;
using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  private readonly IChatStreamService _chatStreamService;
  private readonly ILogger<ChatController> _logger;

  public ChatController(
    IChatStreamService chatStreamService,
    ILogger<ChatController> logger
  )
  {
    _chatStreamService = chatStreamService;
    _logger = logger;
  }

  [HttpPost("stream")]
  public async Task Stream(
    [FromBody] ChatRequest request,
    CancellationToken cancellationToken
  )
  {
    Response.StatusCode = StatusCodes.Status200OK;
    Response.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers.Connection = "keep-alive";

    var requestId = Guid.NewGuid().ToString(
      "N"
    );

    if (string.IsNullOrWhiteSpace(
      request.Message
    ))
    {
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          "request-validation",
          "Enter a message before sending.",
          "The message field was empty.",
          null,
          null,
          400,
          true
        ),
        cancellationToken
      );
      return;
    }

    try
    {
      await foreach (var streamEvent in _chatStreamService.StreamAsync(
        request,
        requestId,
        cancellationToken
      ))
      {
        await WriteEventAsync(
          streamEvent,
          cancellationToken
        );
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      _logger.LogInformation(
        "Chat request {RequestId} was cancelled.",
        requestId
      );

      try
      {
        await WriteEventAsync(
          new ChatStreamEvent(
            requestId,
            "request.cancelled",
            DateTimeOffset.UtcNow,
            "Request cancelled.",
            null,
            request.Model,
            null,
            null,
            null,
            null
          ),
          CancellationToken.None
        );
      }
      catch (Exception exception)
      {
        _logger.LogDebug(
          exception,
          "The cancelled client connection could not receive its terminal event."
        );
      }
    }
    catch (ChatStageException exception)
    {
      _logger.LogWarning(
        exception,
        "Chat request {RequestId} failed during {Stage}.",
        requestId,
        exception.Stage
      );
      await WriteErrorAsync(
        requestId,
        exception,
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      _logger.LogWarning(
        exception,
        "Chat request {RequestId} failed during Ollama {Stage}.",
        requestId,
        exception.Stage
      );
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          exception.Stage,
          exception.Message,
          exception.TechnicalMessage,
          request.Model,
          null,
          exception.HttpStatus,
          exception.Recoverable,
          exception
        ),
        cancellationToken
      );
    }
    catch (Exception exception)
    {
      _logger.LogError(
        exception,
        "Chat request {RequestId} failed unexpectedly.",
        requestId
      );
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          "application",
          "The application could not complete the request.",
          exception.Message,
          request.Model,
          null,
          500,
          false,
          exception
        ),
        cancellationToken
      );
    }
  }

  private async Task WriteErrorAsync(
    string requestId,
    ChatStageException exception,
    CancellationToken cancellationToken
  )
  {
    var error = new ProviderError(
      exception.Stage,
      exception.Message,
      exception.TechnicalMessage,
      HttpContext.TraceIdentifier,
      "ollama",
      exception.Model,
      exception.Intention,
      exception.HttpStatus,
      exception.Recoverable
    );
    var streamEvent = new ChatStreamEvent(
      requestId,
      "error",
      DateTimeOffset.UtcNow,
      exception.Message,
      null,
      exception.Model,
      exception.Intention,
      null,
      null,
      error
    );

    await WriteEventAsync(
      streamEvent,
      cancellationToken
    );
  }

  private async Task WriteEventAsync(
    ChatStreamEvent streamEvent,
    CancellationToken cancellationToken
  )
  {
    var json = JsonSerializer.Serialize(
      streamEvent,
      JsonOptions
    );

    await Response.WriteAsync(
      $"data: {json}\n\n",
      cancellationToken
    );
    await Response.Body.FlushAsync(
      cancellationToken
    );
  }
}
