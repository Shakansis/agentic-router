namespace AgenticRouter.Api.Providers;

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
  bool Confirmed
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
