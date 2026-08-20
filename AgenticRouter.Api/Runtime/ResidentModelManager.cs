using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Runtime;

public sealed class ResidentModelManager : BackgroundService, IResidentModelManager
{
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly ILogger<ResidentModelManager> _logger;
  private readonly SemaphoreSlim _lifecycleGate = new(
    1,
    1
  );
  private readonly object _stateGate = new();
  private TaskCompletionSource _idleSignal = CompletedSignal();
  private int _activeRequests;
  private ResidentModelStatus _status = EmptyStatus();

  public ResidentModelManager(
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    ILogger<ResidentModelManager> logger
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _logger = logger;
  }

  public bool HasActiveRequests => Volatile.Read(
    ref _activeRequests
  ) > 0;

  public IDisposable BeginRequest()
  {
    lock (_stateGate)
    {
      if (_activeRequests == 0)
      {
        _idleSignal = new TaskCompletionSource(
          TaskCreationOptions.RunContinuationsAsynchronously
        );
      }

      _activeRequests++;
    }

    return new RequestLease(
      this
    );
  }

  public ResidentModelStatus GetStatus()
  {
    lock (_stateGate)
    {
      return _status;
    }
  }

  public async Task ChangeResidentModelAsync(
    ApplicationSettings previousSettings,
    ApplicationSettings nextSettings,
    CancellationToken cancellationToken
  )
  {
    var modelChanged = !string.Equals(
      previousSettings.ActionModel,
      nextSettings.ActionModel,
      StringComparison.OrdinalIgnoreCase
    );
    var endpointChanged = !string.Equals(
      previousSettings.OllamaUrl,
      nextSettings.OllamaUrl,
      StringComparison.OrdinalIgnoreCase
    );
    var deviceChanged = ResolveResidentGpu(
      previousSettings
    ) != ResolveResidentGpu(
      nextSettings
    );
    var profileChanged = ResidentProfileChanged(
      previousSettings,
      nextSettings
    );

    if (!modelChanged && !endpointChanged && !deviceChanged && !profileChanged)
    {
      return;
    }

    await WaitForIdleAsync(
      cancellationToken
    );
    await _lifecycleGate.WaitAsync(
      cancellationToken
    );

    try
    {
      var nextBaseUri = new Uri(
        nextSettings.OllamaUrl,
        UriKind.Absolute
      );
      var installed = await RequireInstalledAsync(
        nextBaseUri,
        nextSettings.ActionModel,
        cancellationToken
      );

      if (
        (modelChanged || deviceChanged)
        && !string.IsNullOrWhiteSpace(
          previousSettings.ActionModel
        )
      )
      {
        await _ollamaClient.SetModelResidencyAsync(
          new Uri(
            previousSettings.OllamaUrl,
            UriKind.Absolute
          ),
          previousSettings.ActionModel,
          0,
          cancellationToken
        );
      }

      var resolution = await ResolveResidentAsync(
        nextBaseUri,
        nextSettings,
        installed,
        cancellationToken
      );
      var running = await _ollamaClient.GetRunningModelsAsync(
        nextBaseUri,
        cancellationToken
      );
      var current = FindModel(
        running,
        installed.Name
      );

      if (
        current is not null
        && current.ContextLength != resolution.EffectiveContextTokens
      )
      {
        await UnloadAndVerifyAsync(
          nextBaseUri,
          installed.Name,
          cancellationToken
        );
      }

      await PreloadAndVerifyAsync(
        nextBaseUri,
        installed,
        resolution,
        "preloading",
        ResolveResidentGpu(
          nextSettings
        ),
        cancellationToken
      );
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      SetFailure(
        "resident-model-settings-change",
        exception
      );
      throw;
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  public async Task<bool> EvictForRecoveryAsync(
    string targetModel,
    CancellationToken cancellationToken
  )
  {
    await _lifecycleGate.WaitAsync(
      cancellationToken
    );

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );

      if (string.Equals(
        settings.ActionModel,
        targetModel,
        StringComparison.OrdinalIgnoreCase
      ))
      {
        return false;
      }

      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      UpdateStatus(
        settings.ActionModel,
        null,
        "temporarily-evicted",
        false,
        null,
        null,
        null,
        null,
        null,
        "unloading",
        null,
        null
      );
      await UnloadAndVerifyAsync(
        baseUri,
        settings.ActionModel,
        cancellationToken
      );
      UpdateStatus(
        settings.ActionModel,
        null,
        "temporarily-evicted",
        false,
        null,
        null,
        null,
        null,
        null,
        "adaptive-recovery",
        null,
        null
      );
      return true;
    }
    catch (Exception exception)
    {
      SetFailure(
        "resident-model-eviction",
        exception
      );
      throw;
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  public async Task<bool> RestoreAfterRecoveryAsync(
    string targetModel,
    CancellationToken cancellationToken
  )
  {
    await _lifecycleGate.WaitAsync(
      cancellationToken
    );

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      UpdateStatus(
        settings.ActionModel,
        null,
        "reloading",
        false,
        null,
        null,
        null,
        null,
        null,
        "unloading-recovery-target",
        null,
        null
      );
      await _ollamaClient.SetModelResidencyAsync(
        baseUri,
        targetModel,
        0,
        cancellationToken
      );
      var installed = await RequireInstalledAsync(
        baseUri,
        settings.ActionModel,
        cancellationToken
      );
      var resolution = await ResolveResidentAsync(
        baseUri,
        settings,
        installed,
        cancellationToken
      );
      await PreloadAndVerifyAsync(
        baseUri,
        installed,
        resolution,
        "reloading",
        ResolveResidentGpu(
          settings
        ),
        cancellationToken
      );
      return true;
    }
    catch (Exception exception)
    {
      SetFailure(
        "resident-model-reload",
        exception
      );
      _logger.LogWarning(
        exception,
        "Resident model reload failed after adaptive recovery."
      );
      return false;
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  public async Task<ResidentCoexistenceResult> EnsureResidentAlongsideTargetAsync(
    string targetModel,
    CancellationToken cancellationToken
  )
  {
    await _lifecycleGate.WaitAsync(
      cancellationToken
    );

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var residentModel = settings.ActionModel;
      var sameModel = string.Equals(
        residentModel,
        targetModel,
        StringComparison.OrdinalIgnoreCase
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      var running = await _ollamaClient.GetRunningModelsAsync(
        baseUri,
        cancellationToken
      );
      var target = FindModel(
        running,
        targetModel
      );
      var resident = FindModel(
        running,
        residentModel
      );

      if (sameModel)
      {
        return new ResidentCoexistenceResult(
          resident is not null,
          resident is not null,
          false,
          resident is null
            ? "shared-model-not-loaded"
            : "shared-model-ready"
        );
      }

      if (target is null)
      {
        return new ResidentCoexistenceResult(
          resident is not null,
          false,
          false,
          "target-not-loaded"
        );
      }

      var installed = await RequireInstalledAsync(
        baseUri,
        residentModel,
        cancellationToken
      );
      var resolution = await ResolveResidentAsync(
        baseUri,
        settings,
        installed,
        cancellationToken
      );

      if (
        resident is not null
        && resident.ContextLength == resolution.EffectiveContextTokens
      )
      {
        SetVerifiedStatus(
          installed,
          resident,
          resolution
        );
        return new ResidentCoexistenceResult(
          true,
          true,
          false,
          "already-coexisting"
        );
      }

      if (resident is not null)
      {
        return new ResidentCoexistenceResult(
          true,
          true,
          false,
          "resident-context-mismatch"
        );
      }

      await PreloadAndVerifyAsync(
        baseUri,
        installed,
        resolution,
        "reasserting",
        ResolveResidentGpu(
          settings
        ),
        cancellationToken
      );
      running = await _ollamaClient.GetRunningModelsAsync(
        baseUri,
        cancellationToken
      );
      resident = FindModel(
        running,
        residentModel
      );
      target = FindModel(
        running,
        targetModel
      );

      if (resident is not null && target is not null)
      {
        return new ResidentCoexistenceResult(
          true,
          true,
          true,
          "reasserted-and-verified"
        );
      }

      if (resident is not null && target is null)
      {
        await UnloadAndVerifyAsync(
          baseUri,
          residentModel,
          cancellationToken
        );
        UpdateStatus(
          residentModel,
          installed.Digest,
          "temporarily-evicted",
          false,
          resolution.EffectiveContextTokens,
          null,
          null,
          null,
          null,
          "coexistence-rejected",
          "Reasserting the resident displaced the active target; the resident was rolled back for this turn.",
          null
        );
      }

      return new ResidentCoexistenceResult(
        false,
        target is not null,
        true,
        target is null
          ? "target-displaced-resident-rolled-back"
          : "resident-reassertion-not-verified"
      );
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      _logger.LogWarning(
        exception,
        "Resident coexistence verification failed for target {TargetModel}.",
        targetModel
      );
      return new ResidentCoexistenceResult(
        false,
        false,
        false,
        "coexistence-verification-failed"
      );
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  protected override async Task ExecuteAsync(
    CancellationToken stoppingToken
  )
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        if (!HasActiveRequests)
        {
          await EnsureReadyAsync(
            stoppingToken
          );
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception exception)
      {
        SetFailure(
          "resident-model-preload",
          exception
        );
        _logger.LogWarning(
          exception,
          "Resident model verification failed."
        );
      }

      var seconds = 30;

      try
      {
        seconds = (await _settingsStore.GetAsync(
          stoppingToken
        )).Runtime.ResidentModelVerificationIntervalSeconds;
      }
      catch (Exception exception)
      {
        _logger.LogDebug(
          exception,
          "Using the default resident verification interval."
        );
      }

      await Task.Delay(
        TimeSpan.FromSeconds(
          seconds
        ),
        stoppingToken
      );
    }
  }

  private async Task EnsureReadyAsync(
    CancellationToken cancellationToken
  )
  {
    await _lifecycleGate.WaitAsync(
      cancellationToken
    );

    try
    {
      if (HasActiveRequests)
      {
        return;
      }

      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      InstalledModel installed;

      try
      {
        installed = await RequireInstalledAsync(
          baseUri,
          settings.ActionModel,
          cancellationToken
        );
      }
      catch (OllamaRuntimeProfileException exception) when (
        exception.Error.Code == "model-metadata-unavailable"
      )
      {
        UpdateStatus(
          settings.ActionModel,
          null,
          "unavailable",
          false,
          null,
          null,
          null,
          null,
          null,
          "model-validation",
          exception.Message,
          exception.Error.TraceId
        );
        return;
      }

      var resolution = await ResolveResidentAsync(
        baseUri,
        settings,
        installed,
        cancellationToken
      );
      var running = await _ollamaClient.GetRunningModelsAsync(
        baseUri,
        cancellationToken
      );
      var current = FindModel(
        running,
        installed.Name
      );

      if (
        current is not null
        && current.ContextLength == resolution.EffectiveContextTokens
      )
      {
        SetVerifiedStatus(
          installed,
          current,
          resolution
        );
        return;
      }

      if (current is not null)
      {
        if (HasActiveRequests)
        {
          throw new OllamaRuntimeProfileException(
            "reload-blocked-by-active-request",
            "The resident context reload is blocked by an active request.",
            "resident-context-reload",
            installed.Name,
            installed.Digest,
            OllamaRuntimeRoleIds.ResidentCoordinator,
            resolution.EffectiveContextTokens,
            current.ContextLength,
            true,
            "The current request must finish or be cancelled before resident reload."
          );
        }

        UpdateStatus(
          installed.Name,
          installed.Digest,
          "context-mismatch",
          true,
          resolution.EffectiveContextTokens,
          current.ContextLength,
          current.SizeBytes,
          current.VramSizeBytes,
          EstimatedRam(
            current
          ),
          "resident-context-reload-started",
          "The loaded context differs from the configured resident profile.",
          null
        );
        await UnloadAndVerifyAsync(
          baseUri,
          installed.Name,
          cancellationToken
        );
      }

      await PreloadAndVerifyAsync(
        baseUri,
        installed,
        resolution,
        current is null
          ? "preloading"
          : "reloading",
        ResolveResidentGpu(
          settings
        ),
        cancellationToken
      );
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  private async Task PreloadAndVerifyAsync(
    Uri baseUri,
    InstalledModel installed,
    OllamaContextResolution resolution,
    string state,
    int? mainGpu,
    CancellationToken cancellationToken
  )
  {
    UpdateStatus(
      installed.Name,
      installed.Digest,
      state,
      false,
      resolution.EffectiveContextTokens,
      null,
      null,
      null,
      null,
      "resident-context-preload-started",
      null,
      null
    );
    await _ollamaClient.SetModelResidencyAsync(
      baseUri,
      installed.Name,
      -1,
      resolution.EffectiveContextTokens,
      mainGpu,
      cancellationToken
    );
    var running = await _ollamaClient.GetRunningModelsAsync(
      baseUri,
      cancellationToken
    );
    var verified = FindModel(
      running,
      installed.Name
    );

    if (verified is null)
    {
      throw new OllamaRuntimeProfileException(
        "actual-context-not-verified",
        "Ollama did not confirm the resident model through /api/ps.",
        "resident-context-verification",
        installed.Name,
        installed.Digest,
        OllamaRuntimeRoleIds.ResidentCoordinator,
        resolution.EffectiveContextTokens,
        null,
        true,
        "The exact resident model was absent from /api/ps after preload."
      );
    }

    if (verified.ContextLength != resolution.EffectiveContextTokens)
    {
      throw new OllamaRuntimeProfileException(
        "resident-context-mismatch",
        "The loaded resident context does not match the configured runtime profile.",
        "resident-context-verification",
        installed.Name,
        installed.Digest,
        OllamaRuntimeRoleIds.ResidentCoordinator,
        resolution.EffectiveContextTokens,
        verified.ContextLength,
        true,
        $"Ollama reported context_length {verified.ContextLength?.ToString() ?? "unavailable"}."
      );
    }

    SetVerifiedStatus(
      installed,
      verified,
      resolution
    );
  }

  private static int? ResolveResidentGpu(
    ApplicationSettings settings
  )
  {
    return OllamaGpuSelection.Resolve(
      settings.ActionGpu,
      settings.DefaultGpu
    );
  }

  private async Task<InstalledModel> RequireInstalledAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var installed = (await _ollamaClient.GetModelsAsync(
      baseUri,
      cancellationToken
    )).FirstOrDefault(
      candidate => string.Equals(
        candidate.Name,
        model,
        StringComparison.OrdinalIgnoreCase
      )
    );

    return installed ?? throw new OllamaRuntimeProfileException(
      "model-metadata-unavailable",
      $"Resident coordinator model '{model}' is not installed in Ollama.",
      "resident-model-validation",
      model,
      null,
      OllamaRuntimeRoleIds.ResidentCoordinator,
      null,
      null,
      false,
      "The exact model ID was absent from /api/tags."
    );
  }

  private async Task<OllamaContextResolution> ResolveResidentAsync(
    Uri baseUri,
    ApplicationSettings settings,
    InstalledModel installed,
    CancellationToken cancellationToken
  )
  {
    OllamaModelMetadata metadata;

    try
    {
      metadata = await _ollamaClient.GetModelMetadataAsync(
        baseUri,
        installed.Name,
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
        or OllamaRuntimeProfileException
    )
    {
      throw new OllamaRuntimeProfileException(
        "model-metadata-unavailable",
        "Ollama model metadata is unavailable for resident context resolution.",
        "resident-context-resolution",
        installed.Name,
        installed.Digest,
        OllamaRuntimeRoleIds.ResidentCoordinator,
        null,
        null,
        true,
        exception.Message,
        exception
      );
    }

    if (metadata.DeclaredContextTokens is null)
    {
      throw new OllamaRuntimeProfileException(
        "declared-context-unavailable",
        "Ollama did not declare a maximum context for the resident coordinator.",
        "resident-context-resolution",
        installed.Name,
        installed.Digest,
        OllamaRuntimeRoleIds.ResidentCoordinator,
        null,
        null,
        false,
        "The native /api/show response did not contain a positive context_length."
      );
    }

    return OllamaRuntimeProfileResolver.Resolve(
      settings,
      installed.Name,
      installed.Digest,
      UsageModelRoles.Action,
      metadata.DeclaredContextTokens,
      0,
      0
    );
  }

  private async Task UnloadAndVerifyAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    await _ollamaClient.SetModelResidencyAsync(
      baseUri,
      model,
      0,
      cancellationToken
    );
    var running = await _ollamaClient.GetRunningModelsAsync(
      baseUri,
      cancellationToken
    );

    if (FindModel(
      running,
      model
    ) is not null)
    {
      throw new OllamaRuntimeProfileException(
        "model-load-failed",
        "Ollama still reports the resident model after unload.",
        "resident-context-unload",
        model,
        null,
        OllamaRuntimeRoleIds.ResidentCoordinator,
        null,
        null,
        true,
        "The exact model remained present in /api/ps after keep_alive 0."
      );
    }
  }

  private async Task WaitForIdleAsync(
    CancellationToken cancellationToken
  )
  {
    Task signal;

    lock (_stateGate)
    {
      signal = _idleSignal.Task;
    }

    await signal.WaitAsync(
      cancellationToken
    );
  }

  private void EndRequest()
  {
    lock (_stateGate)
    {
      _activeRequests--;

      if (_activeRequests == 0)
      {
        _idleSignal.TrySetResult();
      }
    }
  }

  private void SetVerifiedStatus(
    InstalledModel installed,
    OllamaRunningModel running,
    OllamaContextResolution resolution
  )
  {
    UpdateStatus(
      installed.Name,
      installed.Digest ?? running.Digest,
      "ready",
      true,
      resolution.EffectiveContextTokens,
      running.ContextLength,
      running.SizeBytes,
      running.VramSizeBytes,
      EstimatedRam(
        running
      ),
      "resident-context-verified",
      resolution.SharedModelWarning,
      null
    );
  }

  private void SetFailure(
    string operation,
    Exception exception
  )
  {
    var current = GetStatus();
    var traceId = exception is OllamaRuntimeProfileException profile
      ? profile.Error.TraceId
      : Guid.NewGuid().ToString(
        "N"
      );
    UpdateStatus(
      current.ConfiguredModel,
      current.Digest,
      "error",
      false,
      current.RequestedContextTokens,
      current.ActualContextTokens,
      current.TotalSizeBytes,
      current.VramSizeBytes,
      current.EstimatedRamSizeBytes,
      operation,
      exception.Message,
      traceId
    );
  }

  private void UpdateStatus(
    string model,
    string? digest,
    string state,
    bool loaded,
    int? requestedContext,
    int? actualContext,
    long? totalSize,
    long? vramSize,
    long? estimatedRam,
    string? operation,
    string? diagnostic,
    string? traceId
  )
  {
    lock (_stateGate)
    {
      _status = new ResidentModelStatus(
        model,
        digest,
        state,
        loaded,
        "adaptive",
        requestedContext,
        actualContext,
        totalSize,
        vramSize,
        estimatedRam,
        operation,
        diagnostic,
        traceId
      );
    }
  }

  private static ResidentModelStatus EmptyStatus()
  {
    return new ResidentModelStatus(
      string.Empty,
      null,
      "disabled",
      false,
      "adaptive",
      null,
      null,
      null,
      null,
      null,
      null,
      null,
      null
    );
  }

  private static OllamaRunningModel? FindModel(
    IReadOnlyList<OllamaRunningModel> models,
    string model
  )
  {
    return models.FirstOrDefault(
      running => string.Equals(
        running.Name,
        model,
        StringComparison.OrdinalIgnoreCase
      )
    );
  }

  private static bool ResidentProfileChanged(
    ApplicationSettings previous,
    ApplicationSettings next
  )
  {
    if (
      previous.Context.ProviderContextTokens
        != next.Context.ProviderContextTokens
      || !Equals(
        ResidentRoleProfile(
          previous
        ),
        ResidentRoleProfile(
          next
        )
      )
    )
    {
      return true;
    }

    var previousRoles = OllamaRuntimeProfileResolver.ConfiguredRoles(
      previous,
      previous.ActionModel
    );
    var nextRoles = OllamaRuntimeProfileResolver.ConfiguredRoles(
      next,
      next.ActionModel
    );

    if (!previousRoles.SequenceEqual(
      nextRoles,
      StringComparer.Ordinal
    ))
    {
      return true;
    }

    return !ResidentOverrides(
      previous
    ).SequenceEqual(
      ResidentOverrides(
        next
      ),
      StringComparer.Ordinal
    );
  }

  private static OllamaRoleRuntimeSettings ResidentRoleProfile(
    ApplicationSettings settings
  )
  {
    return settings.OllamaRuntime.RoleDefaults.TryGetValue(
      OllamaRuntimeRoleIds.ResidentCoordinator,
      out var profile
    )
      ? profile
      : OllamaRuntimeDefaults.CreateRoleDefaults()[
        OllamaRuntimeRoleIds.ResidentCoordinator
      ];
  }

  private static IReadOnlyList<string> ResidentOverrides(
    ApplicationSettings settings
  )
  {
    var reference = Providers.ProviderModelReference.Parse(
      settings.ActionModel
    );

    return settings.OllamaRuntime.ModelOverrides
      .Where(
        modelOverride => string.Equals(
          modelOverride.Provider,
          Providers.ModelProviderIds.OllamaLocal,
          StringComparison.Ordinal
        ) && string.Equals(
          modelOverride.Model,
          reference.ModelId,
          StringComparison.Ordinal
        )
      )
      .Select(
        modelOverride =>
        {
          modelOverride.Overrides.TryGetValue(
            OllamaRuntimeRoleIds.ResidentCoordinator,
            out var profile
          );
          return string.Join(
            "|",
            modelOverride.Model,
            modelOverride.Digest,
            profile?.MinimumContextTokens,
            profile?.TargetContextTokens,
            profile?.MaximumContextTokens,
            profile?.OutputTokenLimit,
            profile?.KeepAlive
          );
        }
      )
      .Order(
        StringComparer.Ordinal
      )
      .ToArray();
  }

  private static long? EstimatedRam(
    OllamaRunningModel model
  )
  {
    return model.SizeBytes is null || model.VramSizeBytes is null
      ? null
      : Math.Max(
        0,
        model.SizeBytes.Value - model.VramSizeBytes.Value
      );
  }

  private static TaskCompletionSource CompletedSignal()
  {
    var signal = new TaskCompletionSource(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    signal.SetResult();
    return signal;
  }

  private sealed class RequestLease : IDisposable
  {
    private ResidentModelManager? _owner;

    public RequestLease(
      ResidentModelManager owner
    )
    {
      _owner = owner;
    }

    public void Dispose()
    {
      Interlocked.Exchange(
        ref _owner,
        null
      )?.EndRequest();
    }
  }
}
