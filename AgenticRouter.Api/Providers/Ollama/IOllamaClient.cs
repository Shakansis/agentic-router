using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

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
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  );

  Task<string> GenerateJsonAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  );

  Task<string> GenerateTextAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  );

  Task<string> GenerateStructuredAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    System.Text.Json.JsonElement schema,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Action<ProviderTokenUsage?>? usageObserver = null,
    ProviderChatOptions? options = null
  );

  Task<OllamaToolResponse> GenerateToolCallAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyList<OllamaToolDefinition> tools,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Func<string, CancellationToken, ValueTask>? onThinkingDelta = null,
    Func<string, CancellationToken, ValueTask>? onContentDelta = null,
    bool toolOutput = true
  );

  Task<OllamaModelCapabilities> GetModelCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  );

  Task<OllamaModelMetadata> GetModelMetadataAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  );

  Task<ProviderModelCapabilities> GetProviderModelCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  );

  Task<string> GetVersionAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  );

  Task<string> GetProtocolVersionAsync(
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

  Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    int? contextTokens,
    CancellationToken cancellationToken
  );

  Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    int? contextTokens,
    int? mainGpu,
    CancellationToken cancellationToken
  );

  IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    ProviderCallContext usageContext,
    ProviderChatOptions? options,
    CancellationToken cancellationToken
  );
}

public sealed record OllamaChatUpdate(
  bool Accepted,
  string? Delta,
  bool Done = false,
  ProviderTokenUsage? Usage = null,
  ProviderRateLimitSnapshot? RateLimit = null,
  IReadOnlyList<ProviderCitation>? Citations = null,
  ProviderActivityMetadata? Activity = null,
  string? RetryActivity = null,
  OllamaContextResolution? ContextResolution = null,
  string? ThinkingDelta = null
);

public sealed record OllamaToolDefinition(
  string Name,
  string Description,
  System.Text.Json.JsonElement Parameters
);

public sealed record OllamaToolCall(
  string Name,
  System.Text.Json.JsonElement Arguments,
  string? Id = null
);

public sealed record OllamaToolMessage(
  string Role,
  string? Content = null,
  string? Thinking = null,
  IReadOnlyList<OllamaToolCall>? ToolCalls = null,
  string? ToolName = null,
  string? ToolCallId = null,
  IReadOnlyList<ProviderImagePayload>? Images = null
);

public sealed record OllamaToolResponse(
  string? Content,
  string? Thinking,
  IReadOnlyList<OllamaToolCall> ToolCalls,
  OllamaContextResolution? ContextResolution = null,
  ProviderTokenUsage? Usage = null,
  string? RetryActivity = null
);

public sealed record OllamaRunningModel(
  string Name,
  string? Digest,
  long? SizeBytes,
  long? VramSizeBytes,
  int? ContextLength,
  DateTimeOffset? ExpiresAt
);

public sealed record OllamaModelMetadata(
  string Model,
  int? DeclaredContextTokens,
  string? ParameterSize,
  string? Quantization,
  string? Format,
  string? Family,
  IReadOnlyList<string> Families
);

public sealed record OllamaModelCapabilities(
  string Model,
  IReadOnlyList<string> Capabilities,
  bool ToolingConfirmed
);

public class OllamaProviderException : Exception
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

public sealed class ToolProtocolException : OllamaProviderException
{
  public ToolProtocolException(
    string stage,
    string technicalMessage,
    int? httpStatus,
    Exception? innerException = null
  )
    : base(
      stage,
      "The model returned an invalid native tool call.",
      technicalMessage,
      httpStatus,
      true,
      innerException
    )
  {
  }
}

public sealed class RoutedProviderException : OllamaProviderException
{
  public RoutedProviderException(
    Cloud.CloudProviderException exception
  )
    : base(
      exception.Stage,
      exception.Message,
      $"{exception.Code}; trace={exception.TraceId}",
      exception.HttpStatus,
      exception.Retryable,
      exception
    )
  {
    Code = exception.Code;
    Provider = exception.Provider;
    Model = exception.Model;
    RateLimit = exception.RateLimit;
    TraceId = exception.TraceId;
  }

  public string Code { get; }

  public string Provider { get; }

  public string? Model { get; }

  public ProviderRateLimitSnapshot? RateLimit { get; }

  public string TraceId { get; }
}
