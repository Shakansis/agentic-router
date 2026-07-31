using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Usage;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/usage")]
public sealed class UsageController : ControllerBase
{
  private readonly ISettingsStore _settingsStore;
  private readonly IUsageLedger _ledger;
  private readonly IPricingCatalog _pricing;
  private readonly ICloudProviderRegistry _cloudProviders;
  private readonly IUsageReconciliationService _reconciliation;

  public UsageController(
    ISettingsStore settingsStore,
    IUsageLedger ledger,
    IPricingCatalog pricing,
    ICloudProviderRegistry cloudProviders,
    IUsageReconciliationService reconciliation
  )
  {
    _settingsStore = settingsStore;
    _ledger = ledger;
    _pricing = pricing;
    _cloudProviders = cloudProviders;
    _reconciliation = reconciliation;
  }

  [HttpGet("overview")]
  public async Task<ActionResult<UsageOverview>> Overview(
    [FromQuery] string? workspaceId,
    [FromQuery] string? providerId,
    [FromQuery] string? modelId,
    [FromQuery] string? modelRole,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var filter = new UsageFilter(
      workspaceId,
      providerId,
      modelId,
      modelRole
    );
    var now = DateTimeOffset.UtcNow;
    var pinned = new List<UsageAggregate>();

    foreach (var windowId in settings.Usage.PinnedWindows)
    {
      pinned.Add(
        await _ledger.AggregateAsync(
          _ledger.ResolveWindow(
            windowId,
            settings.Usage,
            now
          ),
          filter,
          false,
          cancellationToken
        )
      );
    }

    var selected = pinned.FirstOrDefault(
      aggregate => string.Equals(
        aggregate.Window.Id,
        settings.Usage.SelectedWindow,
        StringComparison.Ordinal
      )
    ) ?? await _ledger.AggregateAsync(
      _ledger.ResolveWindow(
        settings.Usage.SelectedWindow,
        settings.Usage,
        now
      ),
      filter,
      false,
      cancellationToken
    );

    return Ok(
      new UsageOverview(
        settings.Usage.SelectedWindow,
        settings.Usage.PinnedWindows,
        selected,
        pinned,
        settings.Usage.ComparisonProvider,
        settings.Usage.ComparisonModel,
        _pricing.Get().Version
      )
    );
  }

  [HttpGet("summary")]
  public async Task<ActionResult<UsageAggregate>> Summary(
    [FromQuery] string window = UsageWindowIds.RollingHour,
    [FromQuery] int? customMinutes = null,
    [FromQuery] string? workspaceId = null,
    [FromQuery] string? providerId = null,
    [FromQuery] string? modelId = null,
    [FromQuery] string? modelRole = null,
    [FromQuery] bool recalculate = false,
    CancellationToken cancellationToken = default
  )
  {
    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );

      return Ok(
        await _ledger.AggregateAsync(
          _ledger.ResolveWindow(
            window,
            settings.Usage,
            DateTimeOffset.UtcNow,
            customMinutes
          ),
          new UsageFilter(
            workspaceId,
            providerId,
            modelId,
            modelRole
          ),
          recalculate,
          cancellationToken
        )
      );
    }
    catch (UsageStorageException exception)
    {
      return BadRequest(
        Error(
          exception
        )
      );
    }
  }

  [HttpGet("cloud-dashboard")]
  public async Task<ActionResult<CloudUsageDashboard>> CloudDashboard(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var now = DateTimeOffset.UtcNow;
    var window = _ledger.ResolveWindow(
      settings.Usage.SelectedWindow,
      settings.Usage,
      now
    );
    var connections = await _cloudProviders.GetViewAsync(
      cancellationToken
    );
    var providers = new List<CloudUsageProviderView>();

    foreach (var connection in connections.Providers.Where(
      provider => provider.Enabled || provider.HasKey
    ))
    {
      var filter = new UsageFilter(
        ProviderId: connection.Provider
      );
      var aggregate = await _ledger.AggregateAsync(
        window,
        filter,
        false,
        cancellationToken
      );
      var events = await _ledger.QueryAsync(
        window,
        filter,
        5_000,
        cancellationToken
      );
      var cachedModels = await _cloudProviders.GetCachedModelsAsync(
        connection.Provider,
        cancellationToken
      );
      var providerSettings = CloudProviderRegistry.GetSettings(
        settings,
        connection.Provider
      );
      var quota = await ResolveQuotaAsync(
        connection,
        providerSettings,
        settings.Usage,
        events,
        now,
        cancellationToken
      );
      var alert = quota.Percentage is null
        ? null
        : settings.Usage.AlertThresholds
          .Where(
            threshold => quota.Percentage >= threshold
          )
          .Select(
            threshold => (int?)threshold
          )
          .LastOrDefault();

      providers.Add(
        new CloudUsageProviderView(
          connection.Provider,
          connection.DisplayName,
          connection.ConnectionState,
          connection.ExpectedBillingMode,
          quota.Source,
          quota.Accuracy,
          quota.Percentage,
          quota.Window,
          quota.ResetAt,
          aggregate.Requests,
          aggregate.InputTokens,
          aggregate.OutputTokens,
          aggregate.TotalTokens,
          aggregate.EstimatedActualCost,
          aggregate.LastUpdatedAt,
          events.Any(
            usageEvent => usageEvent.HttpStatus == 429
              || string.Equals(
                usageEvent.ErrorCode,
                "provider-rate-limited",
                StringComparison.Ordinal
              )
          ),
          alert,
          BuildModelViews(
            connection.Provider,
            events,
            cachedModels
          )
        )
      );
    }

    return Ok(
      new CloudUsageDashboard(
        settings.Usage.SelectedWindow,
        settings.Usage.AlertThresholds,
        connections.Providers.Count(
          provider => string.Equals(
            provider.ConnectionState,
            "connected",
            StringComparison.Ordinal
          )
        ),
        providers,
        now
      )
    );
  }

  [HttpGet("pricing")]
  public ActionResult<PricingCatalogView> Pricing()
  {
    return Ok(
      _pricing.Get()
    );
  }

  [HttpPost("reconcile")]
  public async Task<ActionResult<UsageReconciliationResult>> Reconcile(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _reconciliation.RebuildAsync(
        false,
        cancellationToken
      )
    );
  }

  [HttpDelete]
  public async Task<IActionResult> Purge(
    [FromQuery] bool confirmed,
    [FromQuery] DateTimeOffset? beforeUtc,
    CancellationToken cancellationToken
  )
  {
    if (!confirmed)
    {
      return BadRequest(
        new
        {
          code = "usage-purge-confirmation-required",
          stage = "usage-purge",
          message = "Explicit confirmation is required before purging usage history.",
          retryable = true,
          traceId = HttpContext.TraceIdentifier
        }
      );
    }

    try
    {
      return Ok(
        await _ledger.PurgeAsync(
          beforeUtc,
          cancellationToken
        )
      );
    }
    catch (UsageStorageException exception)
    {
      return StatusCode(
        StatusCodes.Status500InternalServerError,
        Error(
          exception
        )
      );
    }
  }

  private static object Error(
    UsageStorageException exception
  )
  {
    return new
    {
      exception.Code,
      exception.Stage,
      exception.Message,
      exception.Retryable,
      exception.TraceId
    };
  }

  private async Task<QuotaView> ResolveQuotaAsync(
    CloudProviderConnectionView connection,
    CloudProviderIntegrationSettings providerSettings,
    UsageSettings usageSettings,
    IReadOnlyList<UsageEvent> selectedEvents,
    DateTimeOffset now,
    CancellationToken cancellationToken
  )
  {
    var rateLimit = selectedEvents
      .Select(
        usageEvent => usageEvent.RateLimit
      )
      .FirstOrDefault(
        value => value is not null
      ) ?? connection.LastRateLimit;

    if (
      rateLimit?.TokenLimit is > 0
      && rateLimit.TokenRemaining is not null
    )
    {
      return new QuotaView(
        Percentage(
          rateLimit.TokenLimit.Value,
          rateLimit.TokenRemaining.Value
        ),
        UsageAccuracy.Exact,
        rateLimit.Source,
        "provider-reported-token-window",
        rateLimit.TokenResetAt
      );
    }

    if (
      rateLimit?.RequestLimit is > 0
      && rateLimit.RequestRemaining is not null
    )
    {
      return new QuotaView(
        Percentage(
          rateLimit.RequestLimit.Value,
          rateLimit.RequestRemaining.Value
        ),
        UsageAccuracy.Exact,
        rateLimit.Source,
        "provider-reported-request-window",
        rateLimit.RequestResetAt
      );
    }

    var quota = providerSettings.ModelQuotas
      .OrderByDescending(
        pair => selectedEvents.Count(
          usageEvent => string.Equals(
            usageEvent.ModelId,
            pair.Key,
            StringComparison.Ordinal
          )
        )
      )
      .FirstOrDefault();
    var limit = quota.Value?.LongWindowTokenLimit
      ?? quota.Value?.ShortWindowTokenLimit;
    var minutes = quota.Value?.LongWindowTokenLimit is not null
      ? quota.Value.LongWindowMinutes
      : quota.Value?.ShortWindowMinutes;

    if (
      string.IsNullOrWhiteSpace(
        quota.Key
      )
      || limit is null
      || minutes is null
    )
    {
      return new QuotaView(
        null,
        UsageAccuracy.Unavailable,
        "unavailable",
        usageSettings.SelectedWindow,
        null
      );
    }

    var configuredWindow = _ledger.ResolveWindow(
      UsageWindowIds.CustomRolling,
      usageSettings,
      now,
      minutes
    );
    var observed = await _ledger.AggregateAsync(
      configuredWindow,
      new UsageFilter(
        ProviderId: connection.Provider,
        ModelId: quota.Key
      ),
      false,
      cancellationToken
    );

    return new QuotaView(
      Math.Round(
        observed.TotalTokens * 100m / limit.Value,
        2,
        MidpointRounding.AwayFromZero
      ),
      UsageAccuracy.Estimated,
      "user-configured-and-observed",
      $"{minutes}-minute configured window",
      null
    );
  }

  private static IReadOnlyList<CloudUsageModelView> BuildModelViews(
    string providerId,
    IReadOnlyList<UsageEvent> events,
    IReadOnlyList<InstalledModel> cachedModels
  )
  {
    var modelIds = events.Select(
      usageEvent => usageEvent.ModelId
    ).Concat(
      cachedModels.Select(
        model => ProviderModelReference.Parse(
          model.Name
        ).ModelId
      )
    ).Distinct(
      StringComparer.Ordinal
    );

    return modelIds.Select(
      modelId =>
      {
        var modelEvents = events.Where(
          usageEvent => string.Equals(
            usageEvent.ModelId,
            modelId,
            StringComparison.Ordinal
          )
        ).ToArray();
        var metadata = cachedModels.FirstOrDefault(
          model => string.Equals(
            ProviderModelReference.Parse(
              model.Name
            ).ModelId,
            modelId,
            StringComparison.Ordinal
          )
        );
        return new CloudUsageModelView(
          providerId,
          modelId,
          modelEvents.Sum(
            usageEvent => usageEvent.InputTokens
          ),
          modelEvents.Sum(
            usageEvent => usageEvent.OutputTokens
          ),
          modelEvents.Sum(
            usageEvent => usageEvent.TotalTokens
          ),
          modelEvents.LongLength,
          modelEvents.Sum(
            usageEvent => usageEvent.EstimatedActualCost
          ),
          modelEvents.Select(
            usageEvent => usageEvent.ModelRole
          ).Distinct(
            StringComparer.Ordinal
          ).Order().ToArray(),
          Capabilities(
            metadata?.Capabilities
          )
        );
      }
    ).OrderByDescending(
      model => model.TotalTokens
    ).ThenBy(
      model => model.ModelId,
      StringComparer.Ordinal
    ).Take(
      50
    ).ToArray();
  }

  private static IReadOnlyList<string> Capabilities(
    ProviderModelCapabilities? capabilities
  )
  {
    if (capabilities is null)
    {
      return [];
    }

    var values = new List<string>();

    if (capabilities.Chat)
    {
      values.Add(
        "chat"
      );
    }

    if (capabilities.Streaming)
    {
      values.Add(
        "streaming"
      );
    }

    if (capabilities.NativeTools)
    {
      values.Add(
        "tools"
      );
    }

    if (capabilities.Vision)
    {
      values.Add(
        "vision"
      );
    }

    if (capabilities.WebSearch)
    {
      values.Add(
        "web"
      );
    }

    return values;
  }

  private static decimal Percentage(
    long limit,
    long remaining
  )
  {
    return Math.Round(
      Math.Max(
        0,
        limit - remaining
      ) * 100m / limit,
      2,
      MidpointRounding.AwayFromZero
    );
  }

  private sealed record QuotaView(
    decimal? Percentage,
    string Accuracy,
    string Source,
    string Window,
    DateTimeOffset? ResetAt
  );
}
