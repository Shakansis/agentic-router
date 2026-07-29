using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Chat;

public interface IChatStreamService
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(
      ChatRequest request,
      string requestId,
      CancellationToken cancellationToken
    );
}

public sealed class ChatStageException : Exception
{
    public ChatStageException(
      string stage,
      string message,
      string technicalMessage,
      string? model,
      string? intention,
      int? httpStatus,
      bool recoverable,
      Exception? innerException = null
    )
      : base(
        message,
        innerException
      )
    {
        Stage = stage;
        TechnicalMessage = technicalMessage;
        Model = model;
        Intention = intention;
        HttpStatus = httpStatus;
        Recoverable = recoverable;
    }

    public string Stage { get; }

    public string TechnicalMessage { get; }

    public string? Model { get; }

    public string? Intention { get; }

    public int? HttpStatus { get; }

    public bool Recoverable { get; }
}
