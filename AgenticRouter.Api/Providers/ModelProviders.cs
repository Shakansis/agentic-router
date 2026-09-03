namespace AgenticRouter.Api.Providers;

public static class ModelEffortLevels
{
  public const string Low = "low";
  public const string Medium = "medium";
  public const string High = "high";

  public static readonly IReadOnlyList<string> All =
  [
    Low,
    Medium,
    High
  ];

  public static bool IsValid(string? value)
  {
    return value is Low or Medium or High;
  }
}

public static class ModelProviderIds
{
  public const string OllamaLocal = "ollama-local";
  public const string Groq = "groq";
  public const string GoogleAiStudio = "google-ai-studio";
  public const string Cerebras = "cerebras";

  public static readonly IReadOnlySet<string> Cloud = new HashSet<string>(
    [
      Groq,
      GoogleAiStudio,
      Cerebras
    ],
    StringComparer.Ordinal
  );

  public static string DisplayName(
    string providerId
  )
  {
    return providerId switch
    {
      OllamaLocal => "Ollama Local",
      Groq => "Groq",
      GoogleAiStudio => "Google AI Studio",
      Cerebras => "Cerebras",
      _ => providerId
    };
  }
}

public sealed record ProviderModelReference(
  string ProviderId,
  string ModelId
)
{
  private const string Separator = "::";

  public bool IsLocal => string.Equals(
    ProviderId,
    ModelProviderIds.OllamaLocal,
    StringComparison.Ordinal
  );

  public string Qualified => IsLocal
    ? ModelId
    : $"{ProviderId}{Separator}{ModelId}";

  public string Display =>
    $"{ModelProviderIds.DisplayName(ProviderId)} \u00b7 {ModelId}";

  public static ProviderModelReference Parse(
    string value
  )
  {
    var separator = value.IndexOf(
      Separator,
      StringComparison.Ordinal
    );

    if (separator <= 0)
    {
      return new ProviderModelReference(
        ModelProviderIds.OllamaLocal,
        value
      );
    }

    return new ProviderModelReference(
      value[..separator],
      value[(separator + Separator.Length)..]
    );
  }
}

public sealed record ProviderModelCapabilities(
  bool Chat,
  bool Streaming,
  bool NativeTools,
  bool Vision,
  bool WebSearch,
  int? ContextTokens,
  string Source,
  bool Confirmed,
  bool StructuredOutput = false,
  bool Reasoning = false,
  bool ProviderNativeWebSearch = false,
  bool ApplicationWebSearch = false,
  bool Citations = false,
  int MaximumImageCount = 0,
  long MaximumImageBytes = 0,
  IReadOnlyList<string>? SupportedImageMimeTypes = null,
  bool ToolProtocolConfirmed = false
);

public sealed record ProviderImagePayload(
  string Id,
  string FileName,
  string MimeType,
  byte[] Bytes,
  int? Width,
  int? Height
);

public sealed record ProviderChatOptions(
  bool WebSearchEnabled,
  IReadOnlyList<ProviderImagePayload> Images,
  string? RequestedEffort = null
)
{
  public static ProviderChatOptions Empty { get; } = new(
    false,
    []
  );
}

public sealed record ProviderCitation(
  string Id,
  string Title,
  string Url,
  int? StartIndex = null,
  int? EndIndex = null
);

public sealed record ProviderActivityMetadata(
  int ImageCount = 0,
  long ImageBytes = 0,
  int SearchQueryCount = 0,
  int GroundedRequestCount = 0,
  int CitationCount = 0,
  decimal? ProviderSearchCost = null,
  string Accuracy = "unavailable"
);

public sealed record ProviderModelPricing(
  decimal? InputPricePerToken,
  decimal? OutputPricePerToken,
  string Currency,
  string Source
);

public sealed record ProviderRateLimitSnapshot(
  long? RequestLimit,
  long? RequestRemaining,
  DateTimeOffset? RequestResetAt,
  long? TokenLimit,
  long? TokenRemaining,
  DateTimeOffset? TokenResetAt,
  string Source,
  DateTimeOffset ObservedAt
);
