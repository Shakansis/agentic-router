using System.Security.Cryptography;
using AgenticRouter.Api.Configuration;

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
  bool ToolProtocolConfirmed = false,
  IReadOnlyList<string>? AdapterGenerationParameters = null,
  IReadOnlyList<string>? ThinkingModes = null
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
  string? RequestedEffort = null,
  ProviderGenerationProfile? GenerationProfile = null
)
{
  public static ProviderChatOptions Empty { get; } = new(
    false,
    []
  );

  public ProviderGenerationProfile EffectiveGenerationProfile =>
    GenerationProfile ?? ProviderGenerationProfiles.Deterministic;
}

public sealed record ProviderGenerationProfile(
  string Id,
  double Temperature,
  double? TopP = null,
  double? RepeatPenalty = null,
  int? MaximumContextTokens = null,
  int? MaximumOutputTokens = null,
  int? TopK = null,
  double? MinP = null,
  int? RepeatLastN = null,
  string Thinking = InferenceThinkingModes.Auto,
  int? Seed = null
);

public static class ProviderGenerationProfiles
{
  public static IReadOnlyList<string> ConfiguredParameters(ProviderGenerationProfile profile)
  {
    var parameters = new List<string> { "temperature" };
    if (profile.TopP is not null) parameters.Add("topP");
    if (profile.TopK is not null) parameters.Add("topK");
    if (profile.MinP is not null) parameters.Add("minP");
    if (profile.RepeatPenalty is not null) parameters.Add("repeatPenalty");
    if (profile.RepeatLastN is not null) parameters.Add("repeatLastN");
    if (profile.Thinking != InferenceThinkingModes.Auto) parameters.Add("thinking");
    if (profile.Seed is not null) parameters.Add("seed");
    if (profile.MaximumContextTokens is not null) parameters.Add("maximumContextTokens");
    if (profile.MaximumOutputTokens is not null) parameters.Add("maximumOutputTokens");
    return parameters;
  }

  public static ProviderGenerationProfile Deterministic { get; } = new(
    "deterministic",
    0
  );

  public static ProviderGenerationProfile Resolve(
    IReadOnlyDictionary<string, InferenceProfileSettings> profiles,
    string intention,
    string interactionMode,
    bool supervisedExecution,
    double? requestTemperatureOverride = null,
    string? requestThinkingOverride = null
  )
  {
    if (!supervisedExecution
      && !string.Equals(interactionMode, "chat", StringComparison.Ordinal))
    {
      return Deterministic with { Seed = RandomNumberGenerator.GetInt32(1, int.MaxValue) };
    }
    var id = supervisedExecution
      ? InferenceProfileDefaults.Supervisor
      : intention;
    if (!profiles.TryGetValue(id, out var profile))
    {
      return Deterministic with { Seed = RandomNumberGenerator.GetInt32(1, int.MaxValue) };
    }
    return new ProviderGenerationProfile(
      id,
      requestTemperatureOverride ?? profile.Temperature,
      profile.TopP,
      profile.RepeatPenalty,
      TopK: profile.TopK,
      MinP: profile.MinP,
      RepeatLastN: profile.RepeatLastN,
      Thinking: supervisedExecution
        ? InferenceThinkingModes.Auto
        : requestThinkingOverride ?? profile.Thinking,
      Seed: profile.Seed ?? RandomNumberGenerator.GetInt32(1, int.MaxValue)
    );
  }
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
