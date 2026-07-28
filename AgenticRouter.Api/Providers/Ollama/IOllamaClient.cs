using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Providers.Ollama;

public interface IOllamaClient
{
    Task<IReadOnlyList<InstalledModel>> GetModelsAsync(
      Uri baseUri,
      CancellationToken cancellationToken
    );

    Task<string> ClassifyAsync(
      Uri baseUri,
      string model,
      IReadOnlyList<ChatMessage> messages,
      CancellationToken cancellationToken
    );

    IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
      Uri baseUri,
      string model,
      IReadOnlyList<ChatMessage> messages,
      CancellationToken cancellationToken
    );
}

public sealed record OllamaChatUpdate(
  bool Accepted,
  string? Delta
);

public sealed class OllamaProviderException : Exception
{
    public OllamaProviderException(
      string stage,
      string message,
      string technicalMessage,
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
        HttpStatus = httpStatus;
        Recoverable = recoverable;
    }

    public string Stage { get; }

    public string TechnicalMessage { get; }

    public int? HttpStatus { get; }

    public bool Recoverable { get; }
}
