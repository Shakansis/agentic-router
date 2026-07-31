using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Models;

public sealed record ModelPresentationPreference(
  string ProviderId,
  string ModelId,
  string? Alias,
  bool Favorite,
  bool Hidden,
  string? Note,
  DateTimeOffset UpdatedAt
);

public sealed record ModelConfigurationProfile
{
  public string Id { get; init; } = string.Empty;

  public string Name { get; init; } = string.Empty;

  public string PrimaryModel { get; init; } = string.Empty;

  public string FallbackModel { get; init; } = "none";

  public string RouterModel { get; init; } = string.Empty;

  public string CoordinatorModel { get; init; } = string.Empty;

  public string WebPreference { get; init; } = "off";

  public string? ComparisonModel { get; init; }

  public string? UsageWindow { get; init; }

  public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record OrganizedModelView(
  string ProviderId,
  string ModelId,
  string QualifiedId,
  string? Alias,
  bool Favorite,
  bool Hidden,
  string? Note,
  bool Available,
  string? Digest,
  ProviderModelCapabilities? Capabilities,
  bool ConformanceApproved,
  string? ConformanceIdentity
);

public sealed record ModelOrganizationView(
  int SchemaVersion,
  int MaximumProfiles,
  IReadOnlyList<OrganizedModelView> Models,
  IReadOnlyList<ModelConfigurationProfile> Profiles
);

public sealed record SaveModelPreferenceRequest(
  string ProviderId,
  string ModelId,
  string? Alias,
  bool Favorite,
  bool Hidden,
  string? Note
);

public sealed record SaveModelProfileRequest(
  string? Id,
  string Name,
  string PrimaryModel,
  string FallbackModel,
  string RouterModel,
  string CoordinatorModel,
  string WebPreference,
  string? ComparisonModel,
  string? UsageWindow
);

public sealed record ApplyModelProfileRequest(
  bool Confirmed
);

public sealed record SetWorkspaceModelProfileRequest(
  string? ProfileId
);

public sealed record ModelChainRoleView(
  string Role,
  string ProviderId,
  string ExactModelId,
  string QualifiedModel,
  string? Alias,
  bool Available,
  bool ConformanceApproved,
  string ToolPath,
  bool Web,
  bool Vision
);

public sealed record ModelProfilePreview(
  string ProfileId,
  string ProfileName,
  IReadOnlyList<ModelChainRoleView> Chain,
  IReadOnlyList<string> Capabilities,
  IReadOnlyList<string> Errors,
  IReadOnlyList<string> AffectedWorkspaces,
  bool LocalFallbackValid,
  bool Applied
);

public interface IModelOrganizationService
{
  Task<ModelOrganizationView> GetAsync(
    CancellationToken cancellationToken
  );

  Task<ModelOrganizationView> SavePreferenceAsync(
    SaveModelPreferenceRequest request,
    CancellationToken cancellationToken
  );

  Task<ModelProfilePreview> SaveProfileAsync(
    SaveModelProfileRequest request,
    CancellationToken cancellationToken
  );

  Task<ModelProfilePreview> PreviewAsync(
    string profileId,
    CancellationToken cancellationToken
  );

  Task<ModelProfilePreview> ApplyAsync(
    string profileId,
    bool confirmed,
    CancellationToken cancellationToken
  );

  Task<ModelOrganizationView> DeleteProfileAsync(
    string profileId,
    CancellationToken cancellationToken
  );

  Task<WorkspaceProfilesResponse> SetWorkspacePreferenceAsync(
    string workspaceId,
    string? profileId,
    CancellationToken cancellationToken
  );
}

public sealed class ModelOrganizationService : IModelOrganizationService
{
  private const int CurrentSchemaVersion = 1;
  private const int MaximumFileBytes = 1_048_576;
  private const int MaximumPreferences = 2_000;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };

  private readonly string _path;
  private readonly ISettingsStore _settings;
  private readonly ISettingsValidator _settingsValidator;
  private readonly IOllamaClient _models;
  private readonly IToolProtocolConformanceService _conformance;
  private readonly IWorkspaceProfileStore _workspaces;
  private readonly IWorkspaceProfileService _workspaceService;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public ModelOrganizationService(
    string dataDirectory,
    ISettingsStore settings,
    ISettingsValidator settingsValidator,
    IOllamaClient models,
    IToolProtocolConformanceService conformance,
    IWorkspaceProfileStore workspaces,
    IWorkspaceProfileService workspaceService
  )
  {
    _path = Path.Combine(
      dataDirectory,
      "model-organization.json"
    );
    _settings = settings;
    _settingsValidator = settingsValidator;
    _models = models;
    _conformance = conformance;
    _workspaces = workspaces;
    _workspaceService = workspaceService;
  }

  public async Task<ModelOrganizationView> GetAsync(
    CancellationToken cancellationToken
  )
  {
    var document = await ReadAsync(
      cancellationToken
    );
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var models = await DiscoverModelsAsync(
      settings,
      cancellationToken
    );
    var conformanceByModel = new Dictionary<
      string,
      ToolProtocolConformanceResult?
    >(
      StringComparer.Ordinal
    );
    var baseUri = new Uri(
      settings.OllamaUrl,
      UriKind.Absolute
    );

    foreach (var model in models)
    {
      conformanceByModel[model.Name] = await _conformance.GetCachedAsync(
        baseUri,
        model.Name,
        model.Digest,
        cancellationToken
      );
    }

    var preferences = document.Preferences.ToDictionary(
      preference => IdentityKey(
        preference.ProviderId,
        preference.ModelId
      ),
      StringComparer.Ordinal
    );
    var organized = models.Select(
      model =>
      {
        var reference = ProviderModelReference.Parse(
          model.Name
        );
        preferences.TryGetValue(
          IdentityKey(
            reference.ProviderId,
            reference.ModelId
          ),
          out var preference
        );
        conformanceByModel.TryGetValue(
          model.Name,
          out var conformance
        );
        return new OrganizedModelView(
          reference.ProviderId,
          reference.ModelId,
          model.Name,
          preference?.Alias,
          preference?.Favorite ?? false,
          preference?.Hidden ?? false,
          preference?.Note,
          model.Selectable,
          model.Digest,
          model.Capabilities,
          conformance?.Passed == true,
          conformance is null
            ? null
            : string.Join(
              "|",
              conformance.Model,
              conformance.Digest,
              conformance.OllamaVersion
            )
        );
      }
    ).Concat(
      document.Preferences.Where(
        preference => !models.Any(
          model =>
          {
            var reference = ProviderModelReference.Parse(
              model.Name
            );
            return string.Equals(
                reference.ProviderId,
                preference.ProviderId,
                StringComparison.Ordinal
              )
              && string.Equals(
                reference.ModelId,
                preference.ModelId,
                StringComparison.Ordinal
              );
          }
        )
      ).Select(
        preference => new OrganizedModelView(
          preference.ProviderId,
          preference.ModelId,
          Qualified(
            preference.ProviderId,
            preference.ModelId
          ),
          preference.Alias,
          preference.Favorite,
          preference.Hidden,
          preference.Note,
          false,
          null,
          null,
          false,
          null
        )
      )
    ).OrderBy(
      model => model.ProviderId,
      StringComparer.Ordinal
    ).ThenByDescending(
      model => model.Favorite
    ).ThenBy(
      model => model.Alias
        ?? model.ModelId,
      StringComparer.OrdinalIgnoreCase
    ).ThenBy(
      model => model.ModelId,
      StringComparer.Ordinal
    ).ToArray();

    return new ModelOrganizationView(
      CurrentSchemaVersion,
      settings.ModelOrganization.MaximumProfiles,
      organized,
      document.Profiles.OrderBy(
        profile => profile.Name,
        StringComparer.OrdinalIgnoreCase
      ).ThenBy(
        profile => profile.Id,
        StringComparer.Ordinal
      ).ToArray()
    );
  }

  public async Task<ModelOrganizationView> SavePreferenceAsync(
    SaveModelPreferenceRequest request,
    CancellationToken cancellationToken
  )
  {
    ValidatePreference(
      request
    );
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var document = await ReadUnlockedAsync(
        cancellationToken
      );
      var preferences = document.Preferences.Where(
        preference => !SameIdentity(
          preference,
          request.ProviderId,
          request.ModelId
        )
      ).ToList();
      preferences.Add(
        new ModelPresentationPreference(
          request.ProviderId,
          request.ModelId,
          NormalizeOptional(
            request.Alias
          ),
          request.Favorite,
          request.Hidden,
          NormalizeOptional(
            request.Note
          ),
          DateTimeOffset.UtcNow
        )
      );

      if (preferences.Count > MaximumPreferences)
      {
        throw new ModelOrganizationException(
          "model-preference-limit",
          "model-preference-save",
          $"At most {MaximumPreferences} model presentation preferences may be saved."
        );
      }

      await WriteUnlockedAsync(
        document with
        {
          Preferences = preferences
        },
        cancellationToken
      );
    }
    finally
    {
      _gate.Release();
    }

    return await GetAsync(
      cancellationToken
    );
  }

  public async Task<ModelProfilePreview> SaveProfileAsync(
    SaveModelProfileRequest request,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var models = await DiscoverModelsAsync(
      settings,
      cancellationToken
    );
    var profile = NormalizeProfile(
      request
    );
    var errors = ValidateProfile(
      profile,
      models
    );

    if (errors.Count > 0)
    {
      throw new ModelOrganizationException(
        "model-profile-invalid",
        "model-profile-save",
        string.Join(
          " ",
          errors
        )
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var document = await ReadUnlockedAsync(
        cancellationToken
      );
      var profiles = document.Profiles.Where(
        item => !string.Equals(
          item.Id,
          profile.Id,
          StringComparison.Ordinal
        )
      ).ToList();

      if (
        profiles.Count >= settings.ModelOrganization.MaximumProfiles
        && !document.Profiles.Any(
          item => string.Equals(
            item.Id,
            profile.Id,
            StringComparison.Ordinal
          )
        )
      )
      {
        throw new ModelOrganizationException(
          "model-profile-limit",
          "model-profile-save",
          $"At most {settings.ModelOrganization.MaximumProfiles} model profiles may be saved."
        );
      }

      profiles.Add(
        profile
      );
      await WriteUnlockedAsync(
        document with
        {
          Profiles = profiles
        },
        cancellationToken
      );
    }
    finally
    {
      _gate.Release();
    }

    return await PreviewAsync(
      profile.Id,
      cancellationToken
    );
  }

  public async Task<ModelProfilePreview> PreviewAsync(
    string profileId,
    CancellationToken cancellationToken
  )
  {
    var document = await ReadAsync(
      cancellationToken
    );
    var profile = RequireProfile(
      document,
      profileId
    );
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var models = await DiscoverModelsAsync(
      settings,
      cancellationToken
    );
    return await BuildPreviewAsync(
      profile,
      document,
      models,
      false,
      cancellationToken
    );
  }

  public async Task<ModelProfilePreview> ApplyAsync(
    string profileId,
    bool confirmed,
    CancellationToken cancellationToken
  )
  {
    if (!confirmed)
    {
      throw new ModelOrganizationException(
        "model-profile-confirmation-required",
        "model-profile-apply",
        "Applying a model profile requires explicit confirmation."
      );
    }

    var document = await ReadAsync(
      cancellationToken
    );
    var profile = RequireProfile(
      document,
      profileId
    );
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var models = await DiscoverModelsAsync(
      settings,
      cancellationToken
    );
    var preview = await BuildPreviewAsync(
      profile,
      document,
      models,
      false,
      cancellationToken
    );

    if (preview.Errors.Count > 0)
    {
      throw new ModelOrganizationException(
        "model-profile-unavailable",
        "model-profile-apply",
        string.Join(
          " ",
          preview.Errors
        )
      );
    }

    var comparison = string.IsNullOrWhiteSpace(
      profile.ComparisonModel
    )
      ? null
      : ProviderModelReference.Parse(
        profile.ComparisonModel
      );
    var intentions = settings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value with
      {
        Model = profile.PrimaryModel,
        FallbackModel = profile.FallbackModel
      },
      StringComparer.Ordinal
    );
    var updated = settings with
    {
      DefaultModel = profile.PrimaryModel,
      RouterModel = profile.RouterModel,
      CoordinatorModel = profile.CoordinatorModel,
      Intentions = intentions,
      Usage = settings.Usage with
      {
        ComparisonProvider = comparison?.ProviderId
          ?? settings.Usage.ComparisonProvider,
        ComparisonModel = comparison?.ModelId
          ?? settings.Usage.ComparisonModel,
        SelectedWindow = profile.UsageWindow
          ?? settings.Usage.SelectedWindow
      }
    };
    var validation = _settingsValidator.Validate(
      updated
    );

    if (validation.Count > 0)
    {
      throw new ModelOrganizationException(
        "model-profile-settings-invalid",
        "model-profile-apply",
        "The profile did not produce a valid atomic settings document."
      );
    }

    var saved = await _settings.SaveAsync(
      updated,
      cancellationToken
    );

    if (!saved.IsValid)
    {
      throw new ModelOrganizationException(
        "model-profile-settings-invalid",
        "model-profile-apply",
        "The profile could not be applied atomically."
      );
    }

    return preview with
    {
      Applied = true
    };
  }

  public async Task<ModelOrganizationView> DeleteProfileAsync(
    string profileId,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var document = await ReadUnlockedAsync(
        cancellationToken
      );
      var profiles = document.Profiles.Where(
        profile => !string.Equals(
          profile.Id,
          profileId,
          StringComparison.Ordinal
        )
      ).ToArray();

      if (profiles.Length == document.Profiles.Count)
      {
        throw new ModelOrganizationException(
          "model-profile-not-found",
          "model-profile-delete",
          "The model profile does not exist."
        );
      }

      await WriteUnlockedAsync(
        document with
        {
          Profiles = profiles
        },
        cancellationToken
      );
    }
    finally
    {
      _gate.Release();
    }

    return await GetAsync(
      cancellationToken
    );
  }

  public async Task<WorkspaceProfilesResponse> SetWorkspacePreferenceAsync(
    string workspaceId,
    string? profileId,
    CancellationToken cancellationToken
  )
  {
    var organization = await ReadAsync(
      cancellationToken
    );

    if (
      !string.IsNullOrWhiteSpace(
        profileId
      )
      && !organization.Profiles.Any(
        profile => string.Equals(
          profile.Id,
          profileId,
          StringComparison.Ordinal
        )
      )
    )
    {
      throw new ModelOrganizationException(
        "model-profile-not-found",
        "workspace-model-profile",
        "The preferred model profile does not exist."
      );
    }

    var workspaces = await _workspaces.ReadAsync(
      cancellationToken
    );
    var found = false;
    var updated = workspaces.Profiles.Select(
      workspace =>
      {
        if (!string.Equals(
          workspace.Id,
          workspaceId,
          StringComparison.Ordinal
        ))
        {
          return workspace;
        }

        found = true;
        return workspace with
        {
          PreferredModelProfileId = NormalizeOptional(
            profileId
          )
        };
      }
    ).ToArray();

    if (!found)
    {
      throw new ModelOrganizationException(
        "workspace-not-found",
        "workspace-model-profile",
        "The workspace profile does not exist."
      );
    }

    await _workspaces.WriteAsync(
      workspaces with
      {
        Profiles = updated
      },
      cancellationToken
    );
    return await _workspaceService.GetAllAsync(
      cancellationToken
    );
  }

  private async Task<ModelProfilePreview> BuildPreviewAsync(
    ModelConfigurationProfile profile,
    ModelOrganizationDocument document,
    IReadOnlyList<InstalledModel> models,
    bool applied,
    CancellationToken cancellationToken
  )
  {
    var preferences = document.Preferences.ToDictionary(
      preference => IdentityKey(
        preference.ProviderId,
        preference.ModelId
      ),
      StringComparer.Ordinal
    );
    var chain = new List<ModelChainRoleView>();
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var baseUri = new Uri(
      settings.OllamaUrl,
      UriKind.Absolute
    );

    foreach (var role in new[]
    {
      (
        Role: "primary",
        Model: profile.PrimaryModel
      ),
      (
        Role: "fallback",
        Model: profile.FallbackModel
      ),
      (
        Role: "router",
        Model: profile.RouterModel
      ),
      (
        Role: "coordinator",
        Model: profile.CoordinatorModel
      )
    })
    {
      if (string.Equals(
        role.Model,
        "none",
        StringComparison.Ordinal
      ))
      {
        continue;
      }

      var reference = ProviderModelReference.Parse(
        role.Model
      );
      var installed = models.FirstOrDefault(
        model => string.Equals(
          model.Name,
          role.Model,
          StringComparison.Ordinal
        )
      );
      preferences.TryGetValue(
        IdentityKey(
          reference.ProviderId,
          reference.ModelId
        ),
        out var preference
      );
      var conformance = installed is null
        ? null
        : await _conformance.GetCachedAsync(
          baseUri,
          installed.Name,
          installed.Digest,
          cancellationToken
        );
      chain.Add(
        new ModelChainRoleView(
          role.Role,
          reference.ProviderId,
          reference.ModelId,
          role.Model,
          preference?.Alias,
          installed?.Selectable == true,
          conformance?.Passed == true,
          reference.IsLocal
            ? conformance?.Passed == true
              ? "direct-or-resident"
              : "resident-bridge"
            : installed?.Capabilities?.NativeTools == true
              ? "provider-native"
              : "not-supported",
          installed?.Capabilities?.WebSearch == true,
          installed?.Capabilities?.Vision == true
        )
      );
    }

    var errors = ValidateProfile(
      profile,
      models
    );
    var workspaceDocument = await _workspaces.ReadAsync(
      cancellationToken
    );
    var affected = workspaceDocument.Profiles.Where(
      workspace => string.Equals(
        workspace.PreferredModelProfileId,
        profile.Id,
        StringComparison.Ordinal
      )
    ).Select(
      workspace => workspace.Name
    ).Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    var capabilities = chain.Where(
      item => item.Role is "primary" or "fallback"
    ).SelectMany(
      item => new[]
      {
        item.Web
          ? "web"
          : null,
        item.Vision
          ? "vision"
          : null,
        item.ToolPath != "not-supported"
          ? "tools"
          : null
      }
    ).Where(
      value => value is not null
    ).Cast<string>().Distinct(
      StringComparer.Ordinal
    ).Order(
      StringComparer.Ordinal
    ).ToArray();
    var localFallbackValid = !ProviderModelReference.Parse(
        profile.PrimaryModel
      ).IsLocal
      ? chain.Any(
        item => item.Role == "fallback"
          && string.Equals(
            item.ProviderId,
            ModelProviderIds.OllamaLocal,
            StringComparison.Ordinal
          )
          && item.Available
      )
      : true;
    return new ModelProfilePreview(
      profile.Id,
      profile.Name,
      chain,
      capabilities,
      errors,
      affected,
      localFallbackValid,
      applied
    );
  }

  private async Task<IReadOnlyList<InstalledModel>> DiscoverModelsAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    IReadOnlyList<InstalledModel> discovered;

    try
    {
      discovered = await _models.GetModelsAsync(
        new Uri(settings.OllamaUrl, UriKind.Absolute),
        cancellationToken
      );
    }
    catch (OllamaProviderException)
    {
      return [];
    }

    var baseUri = new Uri(
      settings.OllamaUrl,
      UriKind.Absolute
    );
    var enriched = new List<InstalledModel>(
      discovered.Count
    );

    foreach (var model in discovered)
    {
      if (
        !string.Equals(
          model.Provider,
          ModelProviderIds.OllamaLocal,
          StringComparison.Ordinal
        )
        || model.Capabilities is not null
      )
      {
        enriched.Add(
          model
        );
        continue;
      }

      try
      {
        enriched.Add(
          model with
          {
            Capabilities = await _models.GetProviderModelCapabilitiesAsync(
              baseUri,
              model.Name,
              cancellationToken
            )
          }
        );
      }
      catch (OllamaProviderException)
      {
        enriched.Add(
          model
        );
      }
    }

    return enriched;
  }

  private static List<string> ValidateProfile(
    ModelConfigurationProfile profile,
    IReadOnlyList<InstalledModel> models
  )
  {
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(
      profile.Name
    ) || profile.Name.Length > 80)
    {
      errors.Add(
        "Profile name must contain 1 to 80 characters."
      );
    }

    foreach (var pair in new[]
    {
      (
        Role: "primary",
        Model: profile.PrimaryModel
      ),
      (
        Role: "router",
        Model: profile.RouterModel
      ),
      (
        Role: "coordinator",
        Model: profile.CoordinatorModel
      )
    })
    {
      if (!models.Any(
        model => model.Selectable
          && string.Equals(
            model.Name,
            pair.Model,
            StringComparison.Ordinal
          )
      ))
      {
        errors.Add(
          $"{pair.Role} model '{pair.Model}' is unavailable."
        );
      }
    }

    var primary = ProviderModelReference.Parse(
      profile.PrimaryModel
    );

    if (!primary.IsLocal)
    {
      var fallback = ProviderModelReference.Parse(
        profile.FallbackModel
      );

      if (
        string.Equals(
          profile.FallbackModel,
          "none",
          StringComparison.Ordinal
        )
        || !fallback.IsLocal
        || !models.Any(
          model => model.Selectable
            && string.Equals(
              model.Name,
              profile.FallbackModel,
              StringComparison.Ordinal
            )
        )
      )
      {
        errors.Add(
          "A cloud primary requires one available Ollama Local fallback."
        );
      }
    }
    else if (
      !string.Equals(
        profile.FallbackModel,
        "none",
        StringComparison.Ordinal
      )
      && !models.Any(
        model => model.Selectable
          && string.Equals(
            model.Name,
            profile.FallbackModel,
            StringComparison.Ordinal
          )
      )
    )
    {
      errors.Add(
        $"fallback model '{profile.FallbackModel}' is unavailable."
      );
    }

    if (
      profile.ComparisonModel is not null
      && !models.Any(
        model => model.Selectable
          && string.Equals(
            model.Name,
            profile.ComparisonModel,
            StringComparison.Ordinal
          )
      )
    )
    {
      errors.Add(
        $"comparison model '{profile.ComparisonModel}' is unavailable."
      );
    }

    if (
      profile.WebPreference is not "off" and not "available" and not "enabled"
    )
    {
      errors.Add(
        "Web preference must be off, available, or enabled."
      );
    }

    if (
      profile.UsageWindow is not null
      && !UsageWindowIds.All.Contains(
        profile.UsageWindow
      )
    )
    {
      errors.Add(
        "Usage window is not supported."
      );
    }

    return errors;
  }

  private static ModelConfigurationProfile NormalizeProfile(
    SaveModelProfileRequest request
  )
  {
    var id = string.IsNullOrWhiteSpace(
      request.Id
    )
      ? Guid.NewGuid().ToString(
        "N"
      )
      : request.Id.Trim();

    if (
      id.Length > 64
      || id.Any(
        character => !char.IsAsciiLetterOrDigit(
          character
        ) && character is not '-' and not '_'
      )
    )
    {
      throw new ModelOrganizationException(
        "model-profile-id-invalid",
        "model-profile-save",
        "The model profile identifier is invalid."
      );
    }

    return new ModelConfigurationProfile
    {
      Id = id,
      Name = request.Name.Trim(),
      PrimaryModel = request.PrimaryModel.Trim(),
      FallbackModel = string.IsNullOrWhiteSpace(
        request.FallbackModel
      )
        ? "none"
        : request.FallbackModel.Trim(),
      RouterModel = request.RouterModel.Trim(),
      CoordinatorModel = request.CoordinatorModel.Trim(),
      WebPreference = request.WebPreference.Trim(),
      ComparisonModel = NormalizeOptional(
        request.ComparisonModel
      ),
      UsageWindow = NormalizeOptional(
        request.UsageWindow
      ),
      UpdatedAt = DateTimeOffset.UtcNow
    };
  }

  private static void ValidatePreference(
    SaveModelPreferenceRequest request
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        request.ProviderId
      )
      || request.ProviderId.Length > 80
      || string.IsNullOrWhiteSpace(
        request.ModelId
      )
      || request.ModelId.Length > 512
      || request.Alias?.Trim().Length > 80
      || request.Note?.Trim().Length > 500
    )
    {
      throw new ModelOrganizationException(
        "model-preference-invalid",
        "model-preference-save",
        "The provider-qualified model preference is invalid."
      );
    }
  }

  private async Task<ModelOrganizationDocument> ReadAsync(
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      return await ReadUnlockedAsync(
        cancellationToken
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task<ModelOrganizationDocument> ReadUnlockedAsync(
    CancellationToken cancellationToken
  )
  {
    if (!File.Exists(
      _path
    ))
    {
      return new ModelOrganizationDocument();
    }

    var info = new FileInfo(
      _path
    );

    if (info.Length > MaximumFileBytes)
    {
      throw new ModelOrganizationException(
        "model-organization-too-large",
        "model-organization-storage",
        "The local model organization file is too large."
      );
    }

    try
    {
      await using var stream = File.OpenRead(
        _path
      );
      var document = await JsonSerializer.DeserializeAsync<ModelOrganizationDocument>(
        stream,
        JsonOptions,
        cancellationToken
      ) ?? throw new InvalidDataException(
        "The model organization document is empty."
      );

      if (
        document.SchemaVersion != CurrentSchemaVersion
        || document.Preferences.Count > MaximumPreferences
        || document.Profiles.Count > 50
      )
      {
        throw new InvalidDataException(
          "The model organization schema is invalid."
        );
      }

      return document;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
      or InvalidDataException
    )
    {
      throw new ModelOrganizationException(
        "model-organization-invalid",
        "model-organization-storage",
        "The local model organization file is invalid.",
        exception
      );
    }
  }

  private async Task WriteUnlockedAsync(
    ModelOrganizationDocument document,
    CancellationToken cancellationToken
  )
  {
    var directory = Path.GetDirectoryName(
      _path
    )!;
    Directory.CreateDirectory(
      directory
    );
    var temporary = Path.Combine(
      directory,
      $".model-organization-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      var content = JsonSerializer.Serialize(
        document,
        JsonOptions
      ).Replace(
        "\r\n",
        "\n",
        StringComparison.Ordinal
      ) + "\n";

      if (System.Text.Encoding.UTF8.GetByteCount(
        content
      ) > MaximumFileBytes)
      {
        throw new ModelOrganizationException(
          "model-organization-too-large",
          "model-organization-storage",
          "The local model organization file is too large."
        );
      }

      await File.WriteAllTextAsync(
        temporary,
        content,
        cancellationToken
      );
      File.Move(
        temporary,
        _path,
        true
      );
    }
    finally
    {
      if (File.Exists(
        temporary
      ))
      {
        File.Delete(
          temporary
        );
      }
    }
  }

  private static ModelConfigurationProfile RequireProfile(
    ModelOrganizationDocument document,
    string profileId
  )
  {
    return document.Profiles.FirstOrDefault(
      profile => string.Equals(
        profile.Id,
        profileId,
        StringComparison.Ordinal
      )
    ) ?? throw new ModelOrganizationException(
      "model-profile-not-found",
      "model-profile",
      "The model configuration profile does not exist."
    );
  }

  private static string IdentityKey(
    string providerId,
    string modelId
  )
  {
    return $"{providerId}\u001f{modelId}";
  }

  private static bool SameIdentity(
    ModelPresentationPreference preference,
    string providerId,
    string modelId
  )
  {
    return string.Equals(
        preference.ProviderId,
        providerId,
        StringComparison.Ordinal
      )
      && string.Equals(
        preference.ModelId,
        modelId,
        StringComparison.Ordinal
      );
  }

  private static string Qualified(
    string providerId,
    string modelId
  )
  {
    return new ProviderModelReference(
      providerId,
      modelId
    ).Qualified;
  }

  private static string? NormalizeOptional(
    string? value
  )
  {
    return string.IsNullOrWhiteSpace(
      value
    )
      ? null
      : value.Trim();
  }

  private sealed record ModelOrganizationDocument
  {
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<ModelPresentationPreference> Preferences { get; init; } =
      [];

    public IReadOnlyList<ModelConfigurationProfile> Profiles { get; init; } = [];
  }
}

public sealed class ModelOrganizationException : Exception
{
  public ModelOrganizationException(
    string code,
    string stage,
    string message,
    Exception? innerException = null
  )
    : base(
      message,
      innerException
    )
  {
    Code = code;
    Stage = stage;
    TraceId = Guid.NewGuid().ToString(
      "N"
    );
  }

  public string Code { get; }

  public string Stage { get; }

  public string TraceId { get; }
}
