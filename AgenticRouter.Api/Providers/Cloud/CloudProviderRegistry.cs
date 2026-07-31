using System.Collections.Concurrent;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Providers.Cloud;

public interface ICloudProviderRegistry
{
  Task<CloudProvidersView> GetViewAsync(
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<InstalledModel>> GetSelectableModelsAsync(
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<InstalledModel>> GetCachedModelsAsync(
    string providerId,
    CancellationToken cancellationToken
  );

  Task<CloudProviderOperationResult> RefreshAsync(
    string providerId,
    CancellationToken cancellationToken
  );

  Task<CloudProviderOperationResult> TestAsync(
    string providerId,
    CancellationToken cancellationToken
  );

  Task<CloudProviderSession> OpenAsync(
    string providerId,
    CancellationToken cancellationToken
  );

  void Invalidate(
    string providerId
  );
}

public sealed record CloudProviderSession(
  ICloudProviderAdapter Adapter,
  string ApiKey
);

public sealed class CloudProviderRegistry : ICloudProviderRegistry
{
  private readonly IReadOnlyDictionary<string, ICloudProviderAdapter> _adapters;
  private readonly ISettingsStore _settingsStore;
  private readonly IProtectedSecretStore _secretStore;
  private readonly string _cacheDirectory;
  private readonly ConcurrentDictionary<string, ProviderState> _states = new(
    StringComparer.Ordinal
  );
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(
    StringComparer.Ordinal
  );

  public CloudProviderRegistry(
    IEnumerable<ICloudProviderAdapter> adapters,
    ISettingsStore settingsStore,
    IProtectedSecretStore secretStore,
    string dataDirectory
  )
  {
    _adapters = adapters.ToDictionary(
      adapter => adapter.ProviderId,
      StringComparer.Ordinal
    );
    _settingsStore = settingsStore;
    _secretStore = secretStore;
    _cacheDirectory = Path.Combine(
      dataDirectory,
      "providers"
    );
  }

  public async Task<CloudProvidersView> GetViewAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var views = new List<CloudProviderConnectionView>();

    foreach (var adapter in OrderedAdapters())
    {
      var providerSettings = GetSettings(
        settings,
        adapter.ProviderId
      );
      var hasKey = await _secretStore.ExistsAsync(
        adapter.ProviderId,
        providerSettings.SecretReference,
        cancellationToken
      );
      var state = await GetCachedStateAsync(
        adapter.ProviderId,
        cancellationToken
      );
      views.Add(
        View(
          adapter,
          providerSettings,
          hasKey,
          state
        )
      );
    }

    return new CloudProvidersView(
      views
    );
  }

  public async Task<IReadOnlyList<InstalledModel>> GetSelectableModelsAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var models = new List<InstalledModel>();

    foreach (var adapter in OrderedAdapters())
    {
      var providerSettings = GetSettings(
        settings,
        adapter.ProviderId
      );

      if (
        !providerSettings.Enabled
        || !await _secretStore.ExistsAsync(
          adapter.ProviderId,
          providerSettings.SecretReference,
          cancellationToken
        )
      )
      {
        continue;
      }

      var state = await GetCachedStateAsync(
        adapter.ProviderId,
        cancellationToken
      );

      if (state is not null)
      {
        models.AddRange(
          state.Models.Where(
            model => model.Selectable
          )
        );
      }
    }

    return models;
  }

  public async Task<IReadOnlyList<InstalledModel>> GetCachedModelsAsync(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    RequireAdapter(
      providerId
    );
    return (
      await GetCachedStateAsync(
        providerId,
        cancellationToken
      )
    )?.Models ?? [];
  }

  public Task<CloudProviderOperationResult> TestAsync(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    return RefreshAsync(
      providerId,
      cancellationToken
    );
  }

  public async Task<CloudProviderOperationResult> RefreshAsync(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    var gate = _gates.GetOrAdd(
      providerId,
      _ => new SemaphoreSlim(
        1,
        1
      )
    );
    await gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var adapter = RequireAdapter(
        providerId
      );
      var providerSettings = GetSettings(
        settings,
        providerId
      );

      if (!providerSettings.Enabled)
      {
        throw new CloudProviderException(
          "provider-disabled",
          "cloud-provider-refresh",
          providerId,
          null,
          $"{adapter.DisplayName} is disabled.",
          409,
          false
        );
      }

      var apiKey = await _secretStore.GetAsync(
        providerId,
        providerSettings.SecretReference,
        cancellationToken
      );

      if (string.IsNullOrWhiteSpace(
        apiKey
      ))
      {
        throw new CloudProviderException(
          "provider-key-missing",
          "cloud-provider-refresh",
          providerId,
          null,
          $"{adapter.DisplayName} has no protected API key.",
          409,
          false
        );
      }

      try
      {
        var models = await adapter.ListModelsAsync(
          apiKey,
          cancellationToken
        );
        var state = new ProviderState(
          1,
          models,
          DateTimeOffset.UtcNow,
          null,
          null
        );
        _states[providerId] = state;
        try
        {
          await SaveStateAsync(
            providerId,
            state,
            cancellationToken
          );
        }
        catch (Exception exception) when (
          exception is IOException
          or UnauthorizedAccessException
        )
        {
        }

        return new CloudProviderOperationResult(
          View(
            adapter,
            providerSettings,
            true,
            state
          ),
          models
        );
      }
      catch (CloudProviderException exception)
      {
        var failed = new ProviderState(
          1,
          [],
          DateTimeOffset.UtcNow,
          exception.RateLimit,
          exception.Message
        );
        _states[providerId] = failed;
        throw;
      }
    }
    finally
    {
      gate.Release();
    }
  }

  public async Task<CloudProviderSession> OpenAsync(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    var adapter = RequireAdapter(
      providerId
    );
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var providerSettings = GetSettings(
      settings,
      providerId
    );

    if (!providerSettings.Enabled)
    {
      throw new CloudProviderException(
        "provider-disabled",
        "provider-resolution",
        providerId,
        null,
        $"{adapter.DisplayName} is disabled.",
        409,
        false
      );
    }

    var apiKey = await _secretStore.GetAsync(
      providerId,
      providerSettings.SecretReference,
      cancellationToken
    );

    if (string.IsNullOrWhiteSpace(
      apiKey
    ))
    {
      throw new CloudProviderException(
        "provider-key-missing",
        "provider-resolution",
        providerId,
        null,
        $"{adapter.DisplayName} has no protected API key.",
        409,
        false
      );
    }

    return new CloudProviderSession(
      adapter,
      apiKey
    );
  }

  public void Invalidate(
    string providerId
  )
  {
    _states.TryRemove(
      providerId,
      out _
    );
    var path = CachePath(
      providerId
    );

    if (File.Exists(
      path
    ))
    {
      File.Delete(
        path
      );
    }
  }

  public static CloudProviderIntegrationSettings GetSettings(
    ApplicationSettings settings,
    string providerId
  )
  {
    return providerId switch
    {
      ModelProviderIds.Groq => settings.CloudProviders.Groq,
      ModelProviderIds.GoogleAiStudio => settings.CloudProviders.GoogleAiStudio,
      ModelProviderIds.Cerebras => settings.CloudProviders.Cerebras,
      _ => throw new CloudProviderException(
        "provider-unknown",
        "provider-resolution",
        providerId,
        null,
        "The cloud provider is not registered.",
        404,
        false
      )
    };
  }

  public static ApplicationSettings SetSettings(
    ApplicationSettings settings,
    string providerId,
    CloudProviderIntegrationSettings providerSettings
  )
  {
    var cloud = providerId switch
    {
      ModelProviderIds.Groq => settings.CloudProviders with
      {
        Groq = providerSettings
      },
      ModelProviderIds.GoogleAiStudio => settings.CloudProviders with
      {
        GoogleAiStudio = providerSettings
      },
      ModelProviderIds.Cerebras => settings.CloudProviders with
      {
        Cerebras = providerSettings
      },
      _ => throw new CloudProviderException(
        "provider-unknown",
        "provider-settings",
        providerId,
        null,
        "The cloud provider is not registered.",
        404,
        false
      )
    };

    return settings with
    {
      CloudProviders = cloud
    };
  }

  private ICloudProviderAdapter RequireAdapter(
    string providerId
  )
  {
    if (!_adapters.TryGetValue(
      providerId,
      out var adapter
    ))
    {
      throw new CloudProviderException(
        "provider-unknown",
        "provider-resolution",
        providerId,
        null,
        "The cloud provider is not registered.",
        404,
        false
      );
    }

    return adapter;
  }

  private IEnumerable<ICloudProviderAdapter> OrderedAdapters()
  {
    return
    [
      RequireAdapter(
        ModelProviderIds.Groq
      ),
      RequireAdapter(
        ModelProviderIds.GoogleAiStudio
      ),
      RequireAdapter(
        ModelProviderIds.Cerebras
      )
    ];
  }

  private async Task<ProviderState?> GetCachedStateAsync(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    if (_states.TryGetValue(
      providerId,
      out var state
    ))
    {
      return state;
    }

    var path = CachePath(
      providerId
    );

    if (!File.Exists(
      path
    ))
    {
      return null;
    }

    try
    {
      await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        16_384,
        FileOptions.Asynchronous
      );
      var cached = await JsonSerializer.DeserializeAsync<ProviderState>(
        stream,
        cancellationToken: cancellationToken
      );

      if (cached?.SchemaVersion == 1)
      {
        _states.TryAdd(
          providerId,
          cached
        );
      }

      return cached?.SchemaVersion == 1
        ? cached
        : null;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
    )
    {
      return null;
    }
  }

  private async Task SaveStateAsync(
    string providerId,
    ProviderState state,
    CancellationToken cancellationToken
  )
  {
    Directory.CreateDirectory(
      _cacheDirectory
    );
    var path = CachePath(
      providerId
    );
    var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

    try
    {
      await using (
        var stream = new FileStream(
          temporaryPath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          16_384,
          FileOptions.Asynchronous | FileOptions.WriteThrough
        )
      )
      {
        await JsonSerializer.SerializeAsync(
          stream,
          state,
          cancellationToken: cancellationToken
        );
        await stream.FlushAsync(
          cancellationToken
        );
      }

      File.Move(
        temporaryPath,
        path,
        true
      );
    }
    finally
    {
      if (File.Exists(
        temporaryPath
      ))
      {
        File.Delete(
          temporaryPath
        );
      }
    }
  }

  private string CachePath(
    string providerId
  )
  {
    return Path.Combine(
      _cacheDirectory,
      $"{providerId}.json"
    );
  }

  private static CloudProviderConnectionView View(
    ICloudProviderAdapter adapter,
    CloudProviderIntegrationSettings settings,
    bool hasKey,
    ProviderState? state
  )
  {
    return new CloudProviderConnectionView(
      adapter.ProviderId,
      adapter.DisplayName,
      settings.Enabled,
      hasKey,
      hasKey
        ? "•••••••• saved"
        : "Not configured",
      !settings.Enabled
        ? "disabled"
        : !hasKey
          ? "key-required"
          : state?.Diagnostic is not null
            ? "error"
            : state is not null
              ? "connected"
              : "not-tested",
      settings.ExpectedBillingMode,
      state?.Models.Count ?? 0,
      state?.LastRefreshAt,
      settings.ModelQuotas.Count > 0
        ? "user-configured-and-observed"
        : "provider-observed-only",
      state?.LastRateLimit,
      state?.Diagnostic
    );
  }

  private sealed record ProviderState(
    int SchemaVersion,
    IReadOnlyList<InstalledModel> Models,
    DateTimeOffset LastRefreshAt,
    ProviderRateLimitSnapshot? LastRateLimit,
    string? Diagnostic
  );
}
