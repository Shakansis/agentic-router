using AgenticRouter.Api.Configuration;

namespace AgenticRouter.Api.Usage;

public interface IPricingCatalog
{
  PricingCatalogView Get();

  PricingSnapshot? Find(
    string providerId,
    string modelId
  );

  decimal Calculate(
    PricingSnapshot price,
    long inputTokens,
    long outputTokens,
    long? cachedInputTokens,
    long? reasoningTokens
  );
}

public sealed class BuiltInPricingCatalog : IPricingCatalog
{
  private const int StaleAfterDays = 90;
  public const string CatalogVersion = "2026-07-30.2";
  private static readonly DateTimeOffset UpdatedAt = new(
    2026,
    7,
    30,
    0,
    0,
    0,
    TimeSpan.Zero
  );

  private static readonly IReadOnlyList<PricingSnapshot> Comparisons =
  [
    new(
      CatalogVersion,
      "google-ai-studio",
      "gemini-3.5-flash-lite",
      0.30m,
      2.50m,
      0.03m,
      "Reasoning tokens are included in the output price.",
      "USD",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://ai.google.dev/gemini-api/docs/pricing",
      UpdatedAt,
      "official-page-snapshot",
      false
    ),
    new(
      CatalogVersion,
      "google-ai-studio",
      "gemini-3.5-flash",
      1.50m,
      9.00m,
      0.15m,
      "Reasoning tokens are included in the output price.",
      "USD",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://ai.google.dev/gemini-api/docs/pricing",
      UpdatedAt,
      "official-page-snapshot",
      false
    ),
    new(
      CatalogVersion,
      "groq",
      "openai/gpt-oss-120b",
      0.15m,
      0.60m,
      0.075m,
      "Reasoning tokens are included in the output price.",
      "USD",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://groq.com/pricing",
      UpdatedAt,
      "official-page-snapshot",
      false
    ),
    new(
      CatalogVersion,
      "groq",
      "openai/gpt-oss-20b",
      0.075m,
      0.30m,
      0.0375m,
      "Reasoning tokens are included in the output price.",
      "USD",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://groq.com/pricing",
      UpdatedAt,
      "official-page-snapshot",
      false
    ),
    new(
      CatalogVersion,
      "cerebras",
      "gpt-oss-120b",
      0.35m,
      0.75m,
      null,
      "Reasoning treatment follows the provider's reported completion usage.",
      "USD",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://inference-docs.cerebras.ai/api-reference/models/public-models",
      UpdatedAt,
      "official-structured-metadata-snapshot",
      false
    )
  ];

  private static readonly IReadOnlyList<OllamaPlanReference> Plans =
  [
    new(
      "Free",
      0m,
      "USD",
      "Light cloud usage; local models running on user hardware remain unlimited.",
      "Unavailable: Ollama does not publish a fixed token allowance for individual plans.",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://ollama.com/pricing",
      UpdatedAt,
      false
    ),
    new(
      "Pro",
      20m,
      "USD",
      "Day-to-day cloud usage with 50x more included usage than Free.",
      "Unavailable: plan use varies by model and processed input, cached input, and output.",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://ollama.com/pricing",
      UpdatedAt,
      false
    ),
    new(
      "Max",
      100m,
      "USD",
      "Heavy sustained cloud usage with 5x more included usage than Pro.",
      "Unavailable: plan use varies by model and processed input, cached input, and output.",
      new DateOnly(
        2026,
        7,
        30
      ),
      "https://ollama.com/pricing",
      UpdatedAt,
      false,
      "New subscriptions paused at the catalog effective date."
    )
  ];

  public PricingCatalogView Get()
  {
    var stale = DateTimeOffset.UtcNow > UpdatedAt.AddDays(
      StaleAfterDays
    );

    return new PricingCatalogView(
      CatalogVersion,
      UpdatedAt,
      Comparisons.Select(
        entry => entry with
        {
          Stale = stale
        }
      ).ToArray(),
      Plans.Select(
        entry => entry with
        {
          Stale = stale
        }
      ).ToArray()
    );
  }

  public PricingSnapshot? Find(
    string providerId,
    string modelId
  )
  {
    return Get().Comparisons.FirstOrDefault(
      entry => string.Equals(
        entry.ProviderId,
        providerId,
        StringComparison.Ordinal
      ) && string.Equals(
        entry.ModelId,
        modelId,
        StringComparison.Ordinal
      )
    );
  }

  public decimal Calculate(
    PricingSnapshot price,
    long inputTokens,
    long outputTokens,
    long? cachedInputTokens,
    long? reasoningTokens
  )
  {
    var cached = Math.Min(
      inputTokens,
      Math.Max(
        0,
        cachedInputTokens ?? 0
      )
    );
    var uncached = Math.Max(
      0,
      inputTokens - cached
    );
    var output = Math.Max(
      0,
      outputTokens
    );
    var cachedRate = price.CachedInputPricePerMillion
      ?? price.InputPricePerMillion;
    var inputCost = uncached / 1_000_000m
      * price.InputPricePerMillion;
    var cachedCost = cached / 1_000_000m
      * cachedRate;
    var outputCost = output / 1_000_000m
      * price.OutputPricePerMillion;

    return inputCost + cachedCost + outputCost;
  }
}

public interface IUsageRecorder
{
  Task RecordAsync(
    UsageRecordRequest request,
    CancellationToken cancellationToken
  );
}

public sealed class UsageRecorder : IUsageRecorder
{
  private readonly ISettingsStore _settingsStore;
  private readonly IUsageLedger _ledger;
  private readonly IPricingCatalog _pricing;
  private readonly ILogger<UsageRecorder> _logger;

  public UsageRecorder(
    ISettingsStore settingsStore,
    IUsageLedger ledger,
    IPricingCatalog pricing,
    ILogger<UsageRecorder> logger
  )
  {
    _settingsStore = settingsStore;
    _ledger = ledger;
    _pricing = pricing;
    _logger = logger;
  }

  public async Task RecordAsync(
    UsageRecordRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var usage = request.ProviderUsage;
      var input = usage?.InputTokens
        ?? request.EstimatedInputTokens;
      var output = usage?.OutputTokens
        ?? request.EstimatedOutputTokens;
      var source = usage is null
        ? TokenCountSources.Estimated
        : TokenCountSources.Provider;
      var accuracy = usage is null
        ? UsageAccuracy.Estimated
        : UsageAccuracy.Exact;
      var equivalent = _pricing.Find(
        settings.Usage.ComparisonProvider,
        settings.Usage.ComparisonModel
      );
      var actualPrice = string.Equals(
        request.ProviderId,
        "ollama-local",
        StringComparison.Ordinal
      )
        ? null
        : _pricing.Find(
          request.ProviderId,
          request.ModelId
        );
      var actual = actualPrice is null
        ? 0m
        : _pricing.Calculate(
          actualPrice,
          input,
          output,
          usage?.CachedInputTokens,
          usage?.ReasoningTokens
        );
      var equivalentCost = equivalent is null
        ? 0m
        : _pricing.Calculate(
          equivalent,
          input,
          output,
          usage?.CachedInputTokens,
          usage?.ReasoningTokens
        );
      var usageEvent = new UsageEvent
      {
        EventId = Guid.NewGuid().ToString(
          "N"
        ),
        TimestampUtc = DateTimeOffset.UtcNow,
        WorkspaceId = request.Context.WorkspaceId,
        ConversationId = request.Context.ConversationId,
        TurnId = request.Context.TurnId,
        ExecutionSessionId = request.Context.ExecutionSessionId,
        ProviderId = request.ProviderId,
        ModelId = request.ModelId,
        ModelRevision = request.Context.ModelRevision,
        ModelRole = request.Context.ModelRole,
        RequestPurpose = request.Context.RequestPurpose,
        InputTokens = input,
        OutputTokens = output,
        CachedInputTokens = usage?.CachedInputTokens,
        ReasoningTokens = usage?.ReasoningTokens,
        MediaTokens = usage?.MediaTokens,
        TotalTokens = input + output,
        DurationMilliseconds = Math.Max(
          0,
          request.DurationMilliseconds
        ),
        Status = request.Status,
        TokenCountSource = source,
        Accuracy = accuracy,
        EstimatedActualCost = actual,
        EquivalentCloudCost = equivalentCost,
        Currency = equivalent?.Currency ?? "USD",
        PricingCatalogVersion = _pricing.Get().Version,
        ActualPriceSnapshot = actualPrice,
        EquivalentPriceSnapshot = equivalent,
        RateLimit = request.RateLimit
      };
      await _ledger.AppendAsync(
        usageEvent,
        settings.Usage.MaxEventBytes,
        settings.Usage.RetentionDays,
        cancellationToken
      );
    }
    catch (Exception exception)
    {
      _logger.LogWarning(
        exception,
        "Usage metadata could not be persisted for provider {Provider} and model {Model}.",
        request.ProviderId,
        request.ModelId
      );
    }
  }
}
