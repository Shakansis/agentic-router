using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Providers.Ollama;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/capabilities")]
public sealed class CapabilitiesController : ControllerBase
{
  private readonly IOllamaClient _providers;
  private readonly ISettingsStore _settingsStore;
  private readonly IToolNameResolver _toolNames;

  public CapabilitiesController(
    IOllamaClient providers,
    ISettingsStore settingsStore,
    IToolNameResolver toolNames
  )
  {
    _providers = providers;
    _settingsStore = settingsStore;
    _toolNames = toolNames;
  }

  [HttpGet("tool-names")]
  public IActionResult GetToolNames()
  {
    return Ok(
      new ToolNameRegistryView(
        "ordinal-ignore-case",
        true,
        _toolNames.CanonicalTools,
        _toolNames.Aliases
      )
    );
  }

  [HttpGet("model")]
  public async Task<IActionResult> GetModel(
    [FromQuery] string model,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(
      model
    ))
    {
      return BadRequest(
        Error(
          "model-required",
          "model-capabilities",
          null,
          null,
          "Select a model before inspecting capabilities.",
          false
        )
      );
    }

    var reference = ProviderModelReference.Parse(
      model
    );

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var capabilities = await _providers.GetProviderModelCapabilitiesAsync(
        new Uri(
          settings.OllamaUrl,
          UriKind.Absolute
        ),
        model,
        cancellationToken
      );
      var isFallback = settings.Intentions.Values.Any(
        intent => string.Equals(
          intent.FallbackModel,
          model,
          StringComparison.Ordinal
        )
      );

      return Ok(
        new ModelCapabilityView(
          model,
          reference.ProviderId,
          ModelProviderIds.DisplayName(
            reference.ProviderId
          ),
          isFallback
            ? "fallback"
            : "primary",
          capabilities,
          capabilities.WebSearch,
          capabilities.WebSearch
            ? null
            : "The selected model and configured integrations do not expose an authorized web-search path."
        )
      );
    }
    catch (CapabilityException exception)
    {
      return BadRequest(
        Error(
          exception.Code,
          exception.Stage,
          exception.Provider,
          exception.Model,
          exception.Message,
          exception.Recoverable,
          exception.TraceId
        )
      );
    }
    catch (OllamaProviderException exception)
    {
      return BadRequest(
        Error(
          "provider-unavailable",
          exception.Stage,
          reference.ProviderId,
          reference.ModelId,
          exception.Message,
          exception.Recoverable
        )
      );
    }
  }

  private static object Error(
    string code,
    string stage,
    string? provider,
    string? model,
    string message,
    bool retryable,
    string? traceId = null
  )
  {
    return new
    {
      code,
      stage,
      provider,
      model,
      message,
      retryable,
      traceId = traceId ?? Guid.NewGuid().ToString(
        "N"
      )
    };
  }
}

[ApiController]
[Route("api/privacy/cloud-images")]
public sealed class CloudImagePrivacyController : ControllerBase
{
  private readonly ICloudImageApprovalStore _approvals;

  public CloudImagePrivacyController(
    ICloudImageApprovalStore approvals
  )
  {
    _approvals = approvals;
  }

  [HttpPost("approve")]
  public IActionResult Approve(
    [FromBody] CloudImageApprovalRequest request
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        request.BrowserSessionId
      )
      || request.BrowserSessionId.Length > 128
      || !ModelProviderIds.Cloud.Contains(
        request.Provider
      )
    )
    {
      return BadRequest(
        new
        {
          code = "cloud-image-approval-invalid",
          stage = "cloud-image-privacy",
          provider = request.Provider,
          model = (string?)null,
          message = "The browser session or cloud provider is invalid.",
          retryable = false,
          traceId = Guid.NewGuid().ToString(
            "N"
          )
        }
      );
    }

    _approvals.Approve(
      request.BrowserSessionId,
      request.Provider
    );
    return Ok(
      new CloudImageApprovalView(
        request.BrowserSessionId,
        request.Provider,
        true
      )
    );
  }

  [HttpPost("reset")]
  public IActionResult Reset(
    [FromBody] CloudImageApprovalResetRequest request
  )
  {
    if (string.IsNullOrWhiteSpace(
      request.BrowserSessionId
    ) || request.BrowserSessionId.Length > 128)
    {
      return BadRequest();
    }

    _approvals.Reset(
      request.BrowserSessionId
    );
    return NoContent();
  }
}

[ApiController]
[Route("api/web-search")]
public sealed class WebSearchController : ControllerBase
{
  private readonly IProtectedSecretStore _secretStore;
  private readonly ISettingsStore _settingsStore;

  public WebSearchController(
    IProtectedSecretStore secretStore,
    ISettingsStore settingsStore
  )
  {
    _secretStore = secretStore;
    _settingsStore = settingsStore;
  }

  [HttpGet]
  public async Task<IActionResult> Get(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var hasKey = await _secretStore.ExistsAsync(
      OllamaWebSearchService.SecretProviderId,
      settings.WebSearch.OllamaSecretReference,
      cancellationToken
    );

    return Ok(
      new
      {
        provider = OllamaWebSearchService.SecretProviderId,
        displayName = "Ollama Web Search",
        enabled = settings.WebSearch.OllamaEnabled,
        hasKey,
        state = settings.WebSearch.OllamaEnabled && hasKey
          ? "available"
          : "unavailable",
        settings.WebSearch.MaxResults,
        settings.WebSearch.TimeoutSeconds,
        diagnostic = settings.WebSearch.OllamaEnabled && hasKey
          ? (string?)null
          : "Configure the separate Ollama Web Search API key to enable application-mediated search for local models."
      }
    );
  }

  [HttpPut("key")]
  public async Task<IActionResult> SaveKey(
    [FromBody] SaveCloudProviderKeyRequest request,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        request.ApiKey
      )
      || request.ApiKey.Length > 16_384
    )
    {
      return BadRequest(
        Error(
          "web-search-key-invalid",
          "Enter a valid Ollama Web Search API key."
        )
      );
    }

    string? newReference = null;
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );

    try
    {
      newReference = await _secretStore.StoreAsync(
        OllamaWebSearchService.SecretProviderId,
        request.ApiKey,
        cancellationToken
      );
      var saved = await _settingsStore.SaveAsync(
        settings with
        {
          WebSearch = settings.WebSearch with
          {
            OllamaEnabled = true,
            OllamaSecretReference = newReference
          }
        },
        cancellationToken
      );

      if (!saved.IsValid)
      {
        await _secretStore.DeleteAsync(
          OllamaWebSearchService.SecretProviderId,
          newReference,
          CancellationToken.None
        );
        return BadRequest(
          new
          {
            code = "web-search-settings-invalid",
            stage = "web-search-settings",
            provider = OllamaWebSearchService.SecretProviderId,
            model = (string?)null,
            message = "The protected key was not attached because settings are invalid.",
            retryable = false,
            traceId = Guid.NewGuid().ToString(
              "N"
            ),
            errors = saved.Errors
          }
        );
      }

      await _secretStore.DeleteAsync(
        OllamaWebSearchService.SecretProviderId,
        settings.WebSearch.OllamaSecretReference,
        CancellationToken.None
      );
      return await Get(
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is SecretStorageException
      or IOException
    )
    {
      if (newReference is not null)
      {
        await _secretStore.DeleteAsync(
          OllamaWebSearchService.SecretProviderId,
          newReference,
          CancellationToken.None
        );
      }

      return BadRequest(
        Error(
          "web-search-key-storage-failed",
          exception.Message
        )
      );
    }
  }

  [HttpDelete("key")]
  public async Task<IActionResult> RemoveKey(
    [FromQuery] bool confirmed,
    CancellationToken cancellationToken
  )
  {
    if (!confirmed)
    {
      return BadRequest(
        Error(
          "confirmation-required",
          "Confirm removal of the protected Ollama Web Search key."
        )
      );
    }

    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var saved = await _settingsStore.SaveAsync(
      settings with
      {
        WebSearch = settings.WebSearch with
        {
          OllamaEnabled = false,
          OllamaSecretReference = null
        }
      },
      cancellationToken
    );

    if (!saved.IsValid)
    {
      return BadRequest(
        Error(
          "web-search-settings-invalid",
          "The key was not removed because settings are invalid."
        )
      );
    }

    await _secretStore.DeleteAsync(
      OllamaWebSearchService.SecretProviderId,
      settings.WebSearch.OllamaSecretReference,
      cancellationToken
    );
    return await Get(
      cancellationToken
    );
  }

  private static object Error(
    string code,
    string message
  )
  {
    return new
    {
      code,
      stage = "web-search-settings",
      provider = OllamaWebSearchService.SecretProviderId,
      model = (string?)null,
      message,
      retryable = false,
      traceId = Guid.NewGuid().ToString(
        "N"
      )
    };
  }
}
