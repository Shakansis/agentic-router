namespace AgenticRouter.Api.Usage;

using AgenticRouter.Api.Providers;

public static class UsageModelRoles
{
  public const string Router = "router";
  public const string Coordinator = "coordinator";
  public const string Specialist = "specialist";
  public const string Primary = "primary";
  public const string Fallback = "fallback";
  public const string Benchmark = "benchmark";
  public const string ModelTest = "model-test";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [
      Router,
      Coordinator,
      Specialist,
      Primary,
      Fallback,
      Benchmark,
      ModelTest
    ],
    StringComparer.Ordinal
  );
}

public static class UsageStatuses
{
  public const string Success = "success";
  public const string Failure = "failure";
  public const string Cancellation = "cancellation";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [
      Success,
      Failure,
      Cancellation
    ],
    StringComparer.Ordinal
  );
}

public static class TokenCountSources
{
  public const string Provider = "provider";
  public const string Estimated = "estimated";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [
      Provider,
      Estimated
    ],
    StringComparer.Ordinal
  );
}

public static class UsageAccuracy
{
  public const string Exact = "exact";
  public const string Estimated = "estimated";
  public const string Mixed = "mixed";
  public const string Unavailable = "unavailable";

  public static readonly IReadOnlySet<string> EventValues = new HashSet<string>(
    [
      Exact,
      Estimated
    ],
    StringComparer.Ordinal
  );
}

public static class UsageWindowIds
{
  public const string RollingHour = "rolling-hour";
  public const string ProviderShort = "provider-short";
  public const string Day = "day";
  public const string ProviderLong = "provider-long";
  public const string RollingSevenDays = "rolling-seven-days";
  public const string CalendarMonth = "calendar-month";
  public const string CustomRolling = "custom-rolling";

  public static readonly IReadOnlySet<string> All = new HashSet<string>(
    [
      RollingHour,
      ProviderShort,
      Day,
      ProviderLong,
      RollingSevenDays,
      CalendarMonth,
      CustomRolling
    ],
    StringComparer.Ordinal
  );
}

public sealed record ProviderCallContext(
  string? WorkspaceId,
  string? ConversationId,
  string? TurnId,
  string? ExecutionSessionId,
  string ModelRole,
  string RequestPurpose,
  string? ModelRevision = null
);

public sealed record ProviderTokenUsage(
  long InputTokens,
  long OutputTokens,
  long? CachedInputTokens = null,
  long? ReasoningTokens = null,
  long? MediaTokens = null
);

public sealed record UsageRecordRequest(
  ProviderCallContext Context,
  string ProviderId,
  string ModelId,
  long DurationMilliseconds,
  string Status,
  ProviderTokenUsage? ProviderUsage,
  long EstimatedInputTokens,
  long EstimatedOutputTokens,
  ProviderRateLimitSnapshot? RateLimit = null,
  string? ErrorCode = null,
  int? HttpStatus = null,
  ProviderActivityMetadata? Activity = null
);

public sealed record PricingSnapshot(
  string CatalogVersion,
  string ProviderId,
  string ModelId,
  decimal InputPricePerMillion,
  decimal OutputPricePerMillion,
  decimal? CachedInputPricePerMillion,
  string ReasoningTreatment,
  string Currency,
  DateOnly EffectiveDate,
  string OfficialSourceUrl,
  DateTimeOffset UpdatedAt,
  string SourceType,
  bool Stale
);

public sealed record UsageEvent
{
  public int SchemaVersion { get; init; } = 1;

  public string EventId { get; init; } = string.Empty;

  public DateTimeOffset TimestampUtc { get; init; }

  public string? WorkspaceId { get; init; }

  public string? ConversationId { get; init; }

  public string? TurnId { get; init; }

  public string? ExecutionSessionId { get; init; }

  public string ProviderId { get; init; } = string.Empty;

  public string ModelId { get; init; } = string.Empty;

  public string? ModelRevision { get; init; }

  public string ModelRole { get; init; } = UsageModelRoles.Primary;

  public string RequestPurpose { get; init; } = string.Empty;

  public long InputTokens { get; init; }

  public long OutputTokens { get; init; }

  public long? CachedInputTokens { get; init; }

  public long? ReasoningTokens { get; init; }

  public long? MediaTokens { get; init; }

  public long TotalTokens { get; init; }

  public long DurationMilliseconds { get; init; }

  public string Status { get; init; } = UsageStatuses.Success;

  public string TokenCountSource { get; init; } = TokenCountSources.Estimated;

  public string Accuracy { get; init; } = UsageAccuracy.Estimated;

  public decimal EstimatedActualCost { get; init; }

  public decimal EquivalentCloudCost { get; init; }

  public string Currency { get; init; } = "USD";

  public string PricingCatalogVersion { get; init; } = string.Empty;

  public PricingSnapshot? ActualPriceSnapshot { get; init; }

  public PricingSnapshot? EquivalentPriceSnapshot { get; init; }

  public ProviderRateLimitSnapshot? RateLimit { get; init; }

  public string? ErrorCode { get; init; }

  public int? HttpStatus { get; init; }

  public int ImageCount { get; init; }

  public long ImageBytes { get; init; }

  public int SearchQueryCount { get; init; }

  public int GroundedRequestCount { get; init; }

  public int CitationCount { get; init; }

  public decimal? ProviderSearchCost { get; init; }

  public string ActivityAccuracy { get; init; } = UsageAccuracy.Unavailable;
}

public sealed record UsageFilter(
  string? WorkspaceId = null,
  string? ProviderId = null,
  string? ModelId = null,
  string? ModelRole = null
);

public sealed record UsageWindow(
  string Id,
  DateTimeOffset StartUtc,
  DateTimeOffset EndUtc
);

public sealed record UsageBreakdown(
  string Key,
  long InputTokens,
  long OutputTokens,
  long TotalTokens,
  long Requests,
  decimal EstimatedActualCost,
  decimal EquivalentCloudCost
);

public sealed record UsageAggregate(
  UsageWindow Window,
  UsageFilter Filter,
  long InputTokens,
  long OutputTokens,
  long TotalTokens,
  long Requests,
  long Successes,
  long Failures,
  long Cancellations,
  string Accuracy,
  decimal EstimatedActualCost,
  decimal EquivalentCloudCost,
  string Currency,
  IReadOnlyList<UsageBreakdown> TopModels,
  IReadOnlyList<UsageBreakdown> TopRoles,
  IReadOnlyList<UsageBreakdown> ProviderBreakdown,
  DateTimeOffset? LastUpdatedAt,
  bool RecalculatedWithCurrentPrices
);

public sealed record UsageOverview(
  string SelectedWindow,
  IReadOnlyList<string> PinnedWindows,
  UsageAggregate Selected,
  IReadOnlyList<UsageAggregate> Pinned,
  string ComparisonProvider,
  string ComparisonModel,
  string PricingCatalogVersion
);

public sealed record CloudUsageModelView(
  string ProviderId,
  string ModelId,
  long InputTokens,
  long OutputTokens,
  long TotalTokens,
  long Requests,
  decimal EstimatedActualCost,
  IReadOnlyList<string> Roles,
  IReadOnlyList<string> Capabilities
);

public sealed record CloudUsageProviderView(
  string ProviderId,
  string DisplayName,
  string ConnectionState,
  string ExpectedBillingMode,
  string QuotaSource,
  string Accuracy,
  decimal? Percentage,
  string Window,
  DateTimeOffset? ResetAt,
  long Requests,
  long InputTokens,
  long OutputTokens,
  long TotalTokens,
  decimal EstimatedActualCost,
  DateTimeOffset? LatestRequestAt,
  bool HasRateLimitWarning,
  int? AlertThreshold,
  IReadOnlyList<CloudUsageModelView> Models
);

public sealed record CloudUsageDashboard(
  string SelectedWindow,
  IReadOnlyList<int> AlertThresholds,
  int ConnectedProviderCount,
  IReadOnlyList<CloudUsageProviderView> Providers,
  DateTimeOffset GeneratedAt
);

public sealed record PricingCatalogView(
  string Version,
  DateTimeOffset UpdatedAt,
  IReadOnlyList<PricingSnapshot> Comparisons,
  IReadOnlyList<OllamaPlanReference> OllamaPlans
);

public sealed record OllamaPlanReference(
  string Plan,
  decimal MonthlyPrice,
  string Currency,
  string UsageDescription,
  string TokenEquivalent,
  DateOnly EffectiveDate,
  string OfficialSourceUrl,
  DateTimeOffset UpdatedAt,
  bool Stale,
  string? Availability = null
);

public sealed record UsagePurgeResult(
  int DeletedFiles,
  long DeletedEvents,
  DateTimeOffset? BeforeUtc
);

public sealed class UsageStorageException : Exception
{
  public UsageStorageException(
    string code,
    string stage,
    string message,
    bool retryable,
    Exception? innerException = null
  )
    : base(
      message,
      innerException
    )
  {
    Code = code;
    Stage = stage;
    Retryable = retryable;
    TraceId = Guid.NewGuid().ToString(
      "N"
    );
  }

  public string Code { get; }

  public string Stage { get; }

  public bool Retryable { get; }

  public string TraceId { get; }
}
