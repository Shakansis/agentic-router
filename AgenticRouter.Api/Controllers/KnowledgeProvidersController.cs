using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Knowledge;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/knowledge-providers")]
public sealed class KnowledgeProvidersController : ControllerBase
{
  private readonly IKnowledgeProviderRegistry _providers;
  private readonly ISettingsStore _settingsStore;
  private readonly IProtectedSecretStore _secretStore;
  private readonly ISystemMemoryMetricsProvider _systemMemory;

  public KnowledgeProvidersController(
    IKnowledgeProviderRegistry providers,
    ISettingsStore settingsStore,
    IProtectedSecretStore secretStore,
    ISystemMemoryMetricsProvider systemMemory
  )
  {
    _providers = providers;
    _settingsStore = settingsStore;
    _secretStore = secretStore;
    _systemMemory = systemMemory;
  }

  [HttpGet]
  public async Task<ActionResult<KnowledgeProvidersView>> Get(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await BuildViewAsync(cancellationToken)
    );
  }

  [HttpPost("{providerId}/refresh")]
  public async Task<IActionResult> Refresh(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    if (!_providers.TryGet(providerId, out _))
    {
      return NotFound(
        Error(
          "knowledge-provider-not-found",
          providerId,
          "The selected knowledge provider is not registered."
        )
      );
    }

    return Ok(
      await BuildViewAsync(cancellationToken)
    );
  }

  [HttpPut("anythingllm/connection")]
  public async Task<IActionResult> ConfigureAnythingLlm(
    [FromBody] ConfigureAnythingLlmRequest request,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(request.BaseUrl))
    {
      return BadRequest(
        Error(
          "knowledge-address-invalid",
          KnowledgeProviderIds.AnythingLlm,
          "Enter the AnythingLLM server address."
        )
      );
    }

    if (
      string.IsNullOrWhiteSpace(request.ApiKey)
      || request.ApiKey.Length > 16_384
    )
    {
      return BadRequest(
        Error(
          "knowledge-key-invalid",
          KnowledgeProviderIds.AnythingLlm,
          "Enter the AnythingLLM developer API key."
        )
      );
    }

    string? newReference = null;
    try
    {
      var current = await _settingsStore.GetAsync(cancellationToken);
      var previous = current.KnowledgeProviders.AnythingLlm;
      newReference = await _secretStore.StoreAsync(
        KnowledgeProviderIds.AnythingLlm,
        request.ApiKey,
        cancellationToken
      );
      var updated = current with
      {
        KnowledgeProviders = current.KnowledgeProviders with
        {
          AnythingLlm = previous with
          {
            BaseUrl = request.BaseUrl.Trim(),
            SecretReference = newReference
          }
        }
      };
      var saved = await _settingsStore.SaveAsync(
        updated,
        cancellationToken
      );
      if (!saved.IsValid)
      {
        await _secretStore.DeleteAsync(
          KnowledgeProviderIds.AnythingLlm,
          newReference,
          CancellationToken.None
        );
        return BadRequest(
          new
          {
            code = "knowledge-settings-invalid",
            stage = "knowledge-configuration",
            provider = KnowledgeProviderIds.AnythingLlm,
            message = "The AnythingLLM connection was not saved because it is invalid.",
            errors = saved.Errors,
            traceId = HttpContext.TraceIdentifier
          }
        );
      }

      await _secretStore.DeleteAsync(
        KnowledgeProviderIds.AnythingLlm,
        previous.SecretReference,
        CancellationToken.None
      );
      return Ok(
        await BuildViewAsync(cancellationToken)
      );
    }
    catch (SecretStorageException exception)
    {
      if (newReference is not null)
      {
        await _secretStore.DeleteAsync(
          KnowledgeProviderIds.AnythingLlm,
          newReference,
          CancellationToken.None
        );
      }

      return BadRequest(
        Error(
          exception.Code,
          KnowledgeProviderIds.AnythingLlm,
          exception.Message
        )
      );
    }
  }

  private async Task<KnowledgeProvidersView> BuildViewAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var views = new List<KnowledgeProviderView>();
    foreach (var provider in _providers.Providers)
    {
      var availability = await provider.GetAvailabilityAsync(cancellationToken);
      IReadOnlyList<KnowledgeLibrary> libraries = [];
      if (availability.Available)
      {
        try
        {
          libraries = await provider.ListLibrariesAsync(cancellationToken);
        }
        catch (KnowledgeProviderException exception)
        {
          availability = availability with
          {
            Available = false,
            Diagnostic = exception.Message
          };
        }
      }

      views.Add(
        new KnowledgeProviderView(
          provider.Definition,
          availability,
          provider.Definition.Id == KnowledgeProviderIds.AnythingLlm
            ? settings.KnowledgeProviders.AnythingLlm.BaseUrl
            : null,
          provider.Definition.Id == KnowledgeProviderIds.AnythingLlm
            && !string.IsNullOrWhiteSpace(
              settings.KnowledgeProviders.AnythingLlm.SecretReference
            ),
          libraries
        )
      );
    }

    return new KnowledgeProvidersView(
      views,
      BuildInstallationGuidance()
    );
  }

  private KnowledgeInstallationGuidance BuildInstallationGuidance()
  {
    var memory = _systemMemory.GetStatus();
    var enoughRam = memory.AvailableBytes is null
      || memory.AvailableBytes >= 2_147_483_648;
    return new KnowledgeInstallationGuidance(
      OperatingSystem.IsWindows()
        ? "windows"
        : OperatingSystem.IsLinux()
          ? "linux"
          : "other",
      "https://docs.useanything.com/installation-desktop/overview",
      "https://docs.useanything.com/features/api",
      enoughRam
        ? "Prefer AnythingLLM's built-in all-MiniLM-L6-v2 embedder: it is a small CPU model and avoids competing for AR GPU memory."
        : "Available RAM is below the built-in embedder's documented 2 GB headroom. Free memory or use a remote embedder before indexing.",
      "Install or start AnythingLLM using its official current-user installation guidance. Do not install a generation model for Agentic Router. Configure the built-in CPU embedder when resources permit, enable the developer API, create an API key, then report the local server address and key so I can connect this project. Use the existing Host process tools and approval policy for every download or installer action."
    );
  }

  private object Error(
    string code,
    string provider,
    string message
  )
  {
    return new
    {
      code,
      stage = "knowledge-configuration",
      provider,
      message,
      retryable = false,
      traceId = HttpContext.TraceIdentifier
    };
  }
}

public sealed record ConfigureAnythingLlmRequest(
  string BaseUrl,
  string ApiKey
);

public sealed record KnowledgeProvidersView(
  IReadOnlyList<KnowledgeProviderView> Providers,
  KnowledgeInstallationGuidance Installation
);

public sealed record KnowledgeProviderView(
  KnowledgeProviderDefinition Definition,
  KnowledgeProviderAvailability Availability,
  string? BaseUrl,
  bool AuthenticationConfigured,
  IReadOnlyList<KnowledgeLibrary> Libraries
);

public sealed record KnowledgeInstallationGuidance(
  string Platform,
  string InstallationDocumentationUrl,
  string ApiDocumentationUrl,
  string EmbeddingRecommendation,
  string ExecutePrompt
);
