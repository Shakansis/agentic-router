using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Providers.Ollama;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/provider-health")]
public sealed class ProviderHealthController : ControllerBase
{
  private readonly IProviderHealthMonitor _monitor;
  private readonly ICloudProviderRegistry _registry;
  private readonly IEnumerable<ICloudProviderAdapter> _adapters;
  private readonly ISettingsStore _settings;
  private readonly IProtectedSecretStore _secrets;
  private readonly OllamaClient _ollama;

  public ProviderHealthController(
    IProviderHealthMonitor monitor,
    ICloudProviderRegistry registry,
    IEnumerable<ICloudProviderAdapter> adapters,
    ISettingsStore settings,
    IProtectedSecretStore secrets,
    OllamaClient ollama
  )
  {
    _monitor = monitor;
    _registry = registry;
    _adapters = adapters;
    _settings = settings;
    _secrets = secrets;
    _ollama = ollama;
  }

  [HttpGet]
  public async Task<ActionResult<ProviderHealthResponse>> Get(
    [FromQuery] int staleAfterSeconds = 900,
    CancellationToken cancellationToken = default
  )
  {
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var views = new List<ProviderHealthView>();
    var staleAfter = TimeSpan.FromSeconds(
      Math.Clamp(
        staleAfterSeconds,
        1,
        86_400
      )
    );
    views.Add(
      _monitor.CreateView(
        ModelProviderIds.OllamaLocal,
        ModelProviderIds.DisplayName(
          ModelProviderIds.OllamaLocal
        ),
        true,
        Uri.TryCreate(
          settings.OllamaUrl,
          UriKind.Absolute,
          out _
        ),
        "local-no-provider-quota",
        "local-runtime",
        "ollama-api",
        staleAfter
      )
    );

    foreach (var adapter in _adapters.OrderBy(
      item => item.DisplayName,
      StringComparer.Ordinal
    ))
    {
      var providerSettings = CloudProviderRegistry.GetSettings(
        settings,
        adapter.ProviderId
      );
      var configured = await _secrets.ExistsAsync(
        adapter.ProviderId,
        providerSettings.SecretReference,
        cancellationToken
      );
      views.Add(
        _monitor.CreateView(
          adapter.ProviderId,
          adapter.DisplayName,
          providerSettings.Enabled,
          configured,
          providerSettings.ModelQuotas.Count > 0
            ? "user-configured"
            : "unavailable",
          providerSettings.ModelQuotas.Count > 0
            ? "user-configured"
            : "unavailable",
          adapter.ProtocolVersion,
          staleAfter
        )
      );
    }

    return Ok(
      new ProviderHealthResponse(
        views,
        DateTimeOffset.UtcNow
      )
    );
  }

  [HttpPost("{providerId}/test")]
  public async Task<IActionResult> Test(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    if (string.Equals(
      providerId,
      ModelProviderIds.OllamaLocal,
      StringComparison.Ordinal
    ))
    {
      var settings = await _settings.GetAsync(
        cancellationToken
      );
      var started = DateTimeOffset.UtcNow;

      try
      {
        await _ollama.GetModelsAsync(
          new Uri(
            settings.OllamaUrl,
            UriKind.Absolute
          ),
          cancellationToken
        );
        _monitor.ObserveModelRefresh(
          providerId,
          true,
          "ollama-api",
          200,
          null
        );
      }
      catch (Exception exception)
      {
        _monitor.ObserveFailure(
          providerId,
          null,
          DateTimeOffset.UtcNow - started,
          exception,
          new ProviderRetryDecision(
            false,
            TimeSpan.Zero,
            "connection-test-failed",
            "The connection test failed.",
            1,
            1
          ),
          "ollama-api",
          "explicit-connection-test"
        );
      }

      return (
        await Get(
          cancellationToken: cancellationToken
        )
      ).Result!;
    }

    var adapter = _adapters.FirstOrDefault(
      item => string.Equals(
        item.ProviderId,
        providerId,
        StringComparison.Ordinal
      )
    );

    if (adapter is null)
    {
      return NotFound();
    }

    try
    {
      await _registry.TestAsync(
        providerId,
        cancellationToken
      );
      _monitor.ObserveModelRefresh(
        providerId,
        true,
        adapter.ProtocolVersion,
        200,
        null
      );
    }
    catch (CloudProviderException exception)
    {
      _monitor.ObserveModelRefresh(
        providerId,
        false,
        adapter.ProtocolVersion,
        exception.HttpStatus,
        exception.Code
      );
    }

    return (
      await Get(
        cancellationToken: cancellationToken
      )
    ).Result!;
  }
}
