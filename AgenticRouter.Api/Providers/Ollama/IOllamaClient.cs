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

  Task<string> GenerateJsonAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    CancellationToken cancellationToken
  );

  Task<string> GenerateTextAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    CancellationToken cancellationToken
  );

  Task<OllamaModelCapabilities> GetModelCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<OllamaRunningModel>> GetRunningModelsAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  );

  Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
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

public sealed record OllamaRunningModel(
  string Name,
  long? SizeBytes,
  long? VramSizeBytes,
  DateTimeOffset? ExpiresAt
);

public sealed record OllamaModelCapabilities(
  string Model,
  IReadOnlyList<string> Capabilities,
  bool ToolingConfirmed
);

public sealed class OllamaProviderException : Exception
{
  public OllamaProviderException(
    string stage,
    string message,
    string technicalMessage,
    int? httpStatus,
    bool recoverable,
    Exception? innerException = null,
    bool isMemoryPressure = false
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
    IsMemoryPressure = isMemoryPressure;
  }

  public string Stage { get; }

  public string TechnicalMessage { get; }

  public int? HttpStatus { get; }

  public bool Recoverable { get; }

  public bool IsMemoryPressure { get; }
}
