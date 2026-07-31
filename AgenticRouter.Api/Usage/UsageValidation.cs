namespace AgenticRouter.Api.Usage;

public sealed record UsageValidationResult(
  string Status,
  IReadOnlyList<string> Issues
)
{
  public bool Accepted => UsageValidationStatuses.Accepted.Contains(
    Status
  );
}

public static class UsageEventValidator
{
  public static UsageValidationResult Validate(
    UsageEvent usageEvent,
    string currentPricingCatalogVersion,
    DateTimeOffset nowUtc
  )
  {
    var rejected = new List<string>();
    var warnings = new List<string>();

    if (usageEvent.SchemaVersion != 1)
    {
      rejected.Add(
        "unsupported-schema"
      );
    }

    if (string.IsNullOrWhiteSpace(
      usageEvent.EventId
    ))
    {
      rejected.Add(
        "missing-event-id"
      );
    }

    if (
      string.IsNullOrWhiteSpace(
        usageEvent.ProviderId
      )
      || string.IsNullOrWhiteSpace(
        usageEvent.ModelId
      )
    )
    {
      rejected.Add(
        "missing-exact-model-identity"
      );
    }

    if (string.IsNullOrWhiteSpace(
      usageEvent.RequestPurpose
    ))
    {
      rejected.Add(
        "missing-request-purpose"
      );
    }

    if (
      !UsageModelRoles.All.Contains(
        usageEvent.ModelRole
      )
      || !UsageStatuses.All.Contains(
        usageEvent.Status
      )
      || !TokenCountSources.All.Contains(
        usageEvent.TokenCountSource
      )
      || !UsageAccuracy.EventValues.Contains(
        usageEvent.Accuracy
      )
    )
    {
      rejected.Add(
        "invalid-contract-enum"
      );
    }

    if (
      usageEvent.InputTokens < 0
      || usageEvent.OutputTokens < 0
      || usageEvent.CachedInputTokens < 0
      || usageEvent.ReasoningTokens < 0
      || usageEvent.MediaTokens < 0
    )
    {
      rejected.Add(
        "negative-token-count"
      );
    }

    var knownComponents = usageEvent.InputTokens + usageEvent.OutputTokens;

    if (usageEvent.TotalTokens < knownComponents)
    {
      rejected.Add(
        "total-less-than-components"
      );
    }
    else if (usageEvent.TotalTokens != knownComponents)
    {
      warnings.Add(
        "provider-total-has-additional-components"
      );
    }

    if (
      usageEvent.DurationMilliseconds < 0
      || usageEvent.ImageCount < 0
      || usageEvent.ImageBytes < 0
      || usageEvent.SearchQueryCount < 0
      || usageEvent.GroundedRequestCount < 0
      || usageEvent.CitationCount < 0
    )
    {
      rejected.Add(
        "negative-metadata-value"
      );
    }

    if (
      usageEvent.EstimatedActualCost < 0
      || usageEvent.EquivalentCloudCost < 0
      || usageEvent.ProviderSearchCost < 0
    )
    {
      rejected.Add(
        "invalid-monetary-value"
      );
    }

    if (
      usageEvent.TimestampUtc == default
      || usageEvent.TimestampUtc > nowUtc.AddMinutes(
        5
      )
    )
    {
      rejected.Add(
        "impossible-timestamp-order"
      );
    }

    if (
      usageEvent.RateLimit?.RequestResetAt < usageEvent.RateLimit?.ObservedAt
      || usageEvent.RateLimit?.TokenResetAt < usageEvent.RateLimit?.ObservedAt
    )
    {
      warnings.Add(
        "quota-reset-precedes-observation"
      );
    }

    if (string.IsNullOrWhiteSpace(
      usageEvent.Currency
    ))
    {
      rejected.Add(
        "missing-currency"
      );
    }

    if (string.IsNullOrWhiteSpace(
      usageEvent.PricingCatalogVersion
    ))
    {
      rejected.Add(
        "missing-pricing-catalog"
      );
    }
    else if (!string.Equals(
      usageEvent.PricingCatalogVersion,
      currentPricingCatalogVersion,
      StringComparison.Ordinal
    ))
    {
      warnings.Add(
        "pricing-catalog-mismatch"
      );
    }

    if (rejected.Count > 0)
    {
      return new UsageValidationResult(
        UsageValidationStatuses.Rejected,
        rejected.Concat(
          warnings
        ).ToArray()
      );
    }

    if (
      string.Equals(
        usageEvent.TokenCountSource,
        TokenCountSources.Estimated,
        StringComparison.Ordinal
      )
      || string.Equals(
        usageEvent.Accuracy,
        UsageAccuracy.Estimated,
        StringComparison.Ordinal
      )
    )
    {
      return new UsageValidationResult(
        UsageValidationStatuses.Estimated,
        warnings.Concat(
          [
            "provider-usage-omitted"
          ]
        ).Distinct(
          StringComparer.Ordinal
        ).ToArray()
      );
    }

    return warnings.Count > 0
      ? new UsageValidationResult(
        UsageValidationStatuses.ValidWithWarning,
        warnings
      )
      : new UsageValidationResult(
        UsageValidationStatuses.Valid,
        []
      );
  }
}
