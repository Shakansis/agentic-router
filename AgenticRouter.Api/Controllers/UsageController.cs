using AgenticRouter.Api.Configuration;
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

  public UsageController(
    ISettingsStore settingsStore,
    IUsageLedger ledger,
    IPricingCatalog pricing
  )
  {
    _settingsStore = settingsStore;
    _ledger = ledger;
    _pricing = pricing;
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

  [HttpGet("pricing")]
  public ActionResult<PricingCatalogView> Pricing()
  {
    return Ok(
      _pricing.Get()
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
}
