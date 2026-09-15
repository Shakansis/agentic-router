using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Models;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
  private readonly ISettingsStore _settingsStore;
  private readonly IPortableYamlSettingsService _portableYaml;
  private readonly ISettingsValidator _validator;
  private readonly IWorkspaceProfileService _workspaceProfiles;
  private readonly ITrustedWorkspaceService _trustedWorkspace;
  private readonly ICloudFallbackPolicy _cloudFallbackPolicy;
  private readonly IOllamaRuntimeProfileService _runtimeProfiles;
  private readonly IModelOrganizationService _models;
  private readonly IModelGpuAffinityResolver _modelGpuAffinities;

  public SettingsController(
    ISettingsStore settingsStore,
    IPortableYamlSettingsService portableYaml,
    ISettingsValidator validator,
    IWorkspaceProfileService workspaceProfiles,
    ITrustedWorkspaceService trustedWorkspace,
    ICloudFallbackPolicy cloudFallbackPolicy,
    IOllamaRuntimeProfileService runtimeProfiles,
    IModelOrganizationService models,
    IModelGpuAffinityResolver modelGpuAffinities
  )
  {
    _settingsStore = settingsStore;
    _portableYaml = portableYaml;
    _validator = validator;
    _workspaceProfiles = workspaceProfiles;
    _trustedWorkspace = trustedWorkspace;
    _cloudFallbackPolicy = cloudFallbackPolicy;
    _runtimeProfiles = runtimeProfiles;
    _models = models;
    _modelGpuAffinities = modelGpuAffinities;
  }

  [HttpGet]
  public async Task<ActionResult<ApplicationSettings>> Get(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );

    return Ok(
      settings
    );
  }

  [HttpGet("yaml")]
  public async Task<IActionResult> GetYaml(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );

    return Content(
      _portableYaml.Export(
        settings
      ),
      "application/yaml; charset=utf-8"
    );
  }

  [HttpPut]
  public async Task<IActionResult> Put(
    [FromBody] ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    return await SaveAsync(
      settings,
      null,
      "Settings were not saved because validation failed.",
      cancellationToken
    );
  }

  [HttpPut("onboarding")]
  public async Task<IActionResult> PutOnboarding(
    [FromBody] OnboardingSettingsRequest request,
    CancellationToken cancellationToken
  )
  {
    var current = await _settingsStore.GetAsync(
      cancellationToken
    );
    var updated = current with
    {
      Onboarding = current.Onboarding with
      {
        ShowBeforeNewConversation = request.ShowBeforeNewConversation
      }
    };
    var result = await _settingsStore.SaveAsync(
      updated,
      cancellationToken
    );

    if (!result.IsValid || result.Settings is null)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "The onboarding preference was not saved.",
          result.Errors
        )
      );
    }

    return Ok(
      result.Settings
    );
  }

  [HttpPut("yaml")]
  public async Task<IActionResult> PutYaml(
    [FromBody] PortableYamlSettingsRequest request,
    CancellationToken cancellationToken
  )
  {
    var previous = await _settingsStore.GetAsync(
      cancellationToken
    );
    var imported = _portableYaml.Import(
      request.Yaml ?? string.Empty,
      previous
    );

    if (imported.Errors.Count > 0 || imported.Settings is null)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "YAML settings were not imported because the document is invalid.",
          imported.Errors
        )
      );
    }

    return await SaveAsync(
      imported.Settings,
      previous,
      "YAML settings were not imported because validation failed.",
      cancellationToken
    );
  }

  private async Task<IActionResult> SaveAsync(
    ApplicationSettings settings,
    ApplicationSettings? previous,
    string validationMessage,
    CancellationToken cancellationToken
  )
  {
    var errors = _validator.Validate(
      settings
    );

    if (errors.Count > 0)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          validationMessage,
          errors
        )
      );
    }

    var fallbackErrors = await _cloudFallbackPolicy.ValidateAsync(
      settings,
      cancellationToken
    );

    if (fallbackErrors.Count > 0)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "Settings were not saved because a cloud primary requires an available Ollama local fallback.",
          fallbackErrors
        )
      );
    }

    var runtimeErrors = await _runtimeProfiles.ValidateOverridesAsync(
      settings,
      cancellationToken
    );

    if (runtimeErrors.Count > 0)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "Settings were not saved because an Ollama runtime override exceeds the exact model capability.",
          runtimeErrors
        )
      );
    }

    var modelErrors = await ValidateModelSelectionsAsync(
      settings,
      cancellationToken
    );
    if (modelErrors.Count > 0)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "Settings were not saved because a Supervisor model or model GPU affinity is unavailable.",
          modelErrors
        )
      );
    }

    previous ??= await _settingsStore.GetAsync(
      cancellationToken
    );

    var result = await _settingsStore.SaveAsync(
      settings,
      cancellationToken
    );
    await _workspaceProfiles.UpdateDefaultModelAsync(
      settings.DefaultModel,
      cancellationToken
    );
    if (!string.IsNullOrWhiteSpace(
      settings.TrustedWorkspacePath
    ))
    {
      await _trustedWorkspace.ConfigureAsync(
        settings.TrustedWorkspacePath,
        cancellationToken
      );
    }

    return Ok(
      result.Settings
    );
  }

  private async Task<IReadOnlyDictionary<string, string[]>> ValidateModelSelectionsAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    if (
      string.Equals(
        settings.SupervisorModel,
        SupervisorModelSelection.SameAsWorker,
        StringComparison.Ordinal
      )
      && settings.ModelGpuAffinities.Count == 0
    )
    {
      return new Dictionary<string, string[]>();
    }

    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    var registry = await _models.GetAsync(cancellationToken);
    var configuredModels = registry.Models.ToDictionary(
      model => model.QualifiedId,
      StringComparer.OrdinalIgnoreCase
    );
    if (
      !string.Equals(
        settings.SupervisorModel,
        SupervisorModelSelection.SameAsWorker,
        StringComparison.Ordinal
      )
      && (
        !configuredModels.TryGetValue(settings.SupervisorModel, out var supervisor)
        || !string.Equals(
          supervisor.ProviderId,
          ModelProviderIds.OllamaLocal,
          StringComparison.Ordinal
        )
      )
    )
    {
      errors["supervisorModel"] =
      [
        "Supervisor model must be Same as Worker or an available local model from the configured registry."
      ];
    }

    foreach (var modelAffinity in settings.ModelGpuAffinities)
    {
      var field = $"modelGpuAffinities.{modelAffinity.Key}";
      if (
        !configuredModels.TryGetValue(modelAffinity.Key, out var model)
        || !string.Equals(
          model.ProviderId,
          ModelProviderIds.OllamaLocal,
          StringComparison.Ordinal
        )
      )
      {
        errors[field] = ["GPU affinity may be assigned only to a local model in the configured registry."];
        continue;
      }
      if (string.Equals(
        modelAffinity.Value,
        ModelGpuAffinitySelection.Auto,
        StringComparison.Ordinal
      ))
      {
        continue;
      }
      try
      {
        _ = await _modelGpuAffinities.ResolveAsync(
          settings,
          modelAffinity.Key,
          settings.DefaultGpu,
          cancellationToken
        );
      }
      catch (ModelGpuAffinityException exception)
      {
        errors[field] = [exception.Message];
      }
    }
    return errors;
  }
}

public sealed record OnboardingSettingsRequest(
  bool ShowBeforeNewConversation
);
