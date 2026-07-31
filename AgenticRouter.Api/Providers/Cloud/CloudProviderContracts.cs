using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Providers.Cloud;

public sealed record CloudCallResult<T>(
  T Value,
  ProviderTokenUsage? Usage,
  ProviderRateLimitSnapshot? RateLimit
);

public sealed record CloudProviderConnectionView(
  string Provider,
  string DisplayName,
  bool Enabled,
  bool HasKey,
  string MaskedKeyState,
  string ConnectionState,
  string ExpectedBillingMode,
  int ModelCount,
  DateTimeOffset? LastRefreshAt,
  string QuotaSource,
  ProviderRateLimitSnapshot? LastRateLimit,
  string? Diagnostic
);

public sealed record CloudProvidersView(
  IReadOnlyList<CloudProviderConnectionView> Providers
);

public sealed record SaveCloudProviderKeyRequest(
  string ApiKey
);

public sealed record CloudProviderOperationResult(
  CloudProviderConnectionView Provider,
  IReadOnlyList<InstalledModel> Models
);

public interface ICloudProviderAdapter
{
  string ProviderId { get; }

  string DisplayName { get; }

  string ProtocolVersion { get; }

  Task<IReadOnlyList<InstalledModel>> ListModelsAsync(
    string apiKey,
    CancellationToken cancellationToken
  );

  Task<CloudCallResult<string>> GenerateStructuredAsync(
    string apiKey,
    string modelId,
    IReadOnlyList<ChatMessage> messages,
    JsonElement? schema,
    string stage,
    CancellationToken cancellationToken
  );

  Task<CloudCallResult<OllamaToolResponse>> GenerateToolCallAsync(
    string apiKey,
    string modelId,
    IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyList<OllamaToolDefinition> tools,
    string stage,
    CancellationToken cancellationToken
  );

  IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    string apiKey,
    string modelId,
    IReadOnlyList<ChatMessage> messages,
    ProviderChatOptions? options,
    CancellationToken cancellationToken
  );
}

public sealed class CloudProviderException : Exception
{
  public CloudProviderException(
    string code,
    string stage,
    string provider,
    string? model,
    string message,
    int? httpStatus,
    bool retryable,
    ProviderRateLimitSnapshot? rateLimit = null,
    Exception? innerException = null,
    TimeSpan? retryAfter = null
  )
    : base(
      message,
      innerException
    )
  {
    Code = code;
    Stage = stage;
    Provider = provider;
    Model = model;
    HttpStatus = httpStatus;
    Retryable = retryable;
    RateLimit = rateLimit;
    RetryAfter = retryAfter;
    TraceId = Guid.NewGuid().ToString(
      "N"
    );
  }

  public string Code { get; }

  public string Stage { get; }

  public string Provider { get; }

  public string? Model { get; }

  public int? HttpStatus { get; }

  public bool Retryable { get; }

  public ProviderRateLimitSnapshot? RateLimit { get; }

  public TimeSpan? RetryAfter { get; }

  public string TraceId { get; }
}
