using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;

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
  private ResidentModelStatus _status = new(
    string.Empty,
    "disabled",
    false,
    "adaptive",
    null,
    null,
    null
  );

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

  public async Task ChangeRouterModelAsync(
    ApplicationSettings previousSettings,
    ApplicationSettings nextSettings,
    CancellationToken cancellationToken
  )
  {
    var modelChanged = !string.Equals(
      previousSettings.RouterModel,
      nextSettings.RouterModel,
      StringComparison.OrdinalIgnoreCase
    );
    var endpointChanged = !string.Equals(
      previousSettings.OllamaUrl,
      nextSettings.OllamaUrl,
      StringComparison.OrdinalIgnoreCase
    );

    if (!modelChanged && !endpointChanged)
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
      var installed = await _ollamaClient.GetModelsAsync(
        nextBaseUri,
        cancellationToken
      );

      if (!installed.Any(
        model => string.Equals(
          model.Name,
          nextSettings.RouterModel,
          StringComparison.OrdinalIgnoreCase
        )
      ))
      {
        throw new InvalidOperationException(
          $"Router model '{nextSettings.RouterModel}' is not installed in Ollama."
        );
      }

      if (
        modelChanged
        && !string.IsNullOrWhiteSpace(
          previousSettings.RouterModel
        )
      )
      {
        await _ollamaClient.SetModelResidencyAsync(
          new Uri(
            previousSettings.OllamaUrl,
            UriKind.Absolute
          ),
          previousSettings.RouterModel,
          0,
          cancellationToken
        );
      }

      await PreloadAndVerifyAsync(
        nextBaseUri,
        nextSettings.RouterModel,
        "preloading",
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
        settings.RouterModel,
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
        settings.RouterModel,
        "temporarily-evicted",
        false,
        "unloading",
        null,
        null
      );
      await _ollamaClient.SetModelResidencyAsync(
        baseUri,
        settings.RouterModel,
        0,
        cancellationToken
      );
      var running = await _ollamaClient.GetRunningModelsAsync(
        baseUri,
        cancellationToken
      );
      var absent = !ContainsModel(
        running,
        settings.RouterModel
      );

      if (!absent)
      {
        throw new InvalidOperationException(
          "Ollama still reports the resident model after unload."
        );
      }

      UpdateStatus(
        settings.RouterModel,
        "temporarily-evicted",
        false,
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
        settings.RouterModel,
        "reloading",
        false,
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
      await PreloadAndVerifyAsync(
        baseUri,
        settings.RouterModel,
        "reloading",
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

  protected override async Task ExecuteAsync(
    CancellationToken stoppingToken
  )
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        if (Volatile.Read(
          ref _activeRequests
        ) == 0)
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
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      var installed = await _ollamaClient.GetModelsAsync(
        baseUri,
        cancellationToken
      );

      if (!installed.Any(
        model => string.Equals(
          model.Name,
          settings.RouterModel,
          StringComparison.OrdinalIgnoreCase
        )
      ))
      {
        UpdateStatus(
          settings.RouterModel,
          "unavailable",
          false,
          "model-validation",
          $"Router model '{settings.RouterModel}' is not installed.",
          null
        );
        return;
      }

      var running = await _ollamaClient.GetRunningModelsAsync(
        baseUri,
        cancellationToken
      );

      if (ContainsModel(
        running,
        settings.RouterModel
      ))
      {
        UpdateStatus(
          settings.RouterModel,
          "ready",
          true,
          "verified",
          null,
          null
        );
        return;
      }

      await PreloadAndVerifyAsync(
        baseUri,
        settings.RouterModel,
        "preloading",
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
    string model,
    string state,
    CancellationToken cancellationToken
  )
  {
    UpdateStatus(
      model,
      state,
      false,
      "preloading",
      null,
      null
    );
    await _ollamaClient.SetModelResidencyAsync(
      baseUri,
      model,
      -1,
      cancellationToken
    );
    var running = await _ollamaClient.GetRunningModelsAsync(
      baseUri,
      cancellationToken
    );

    if (!ContainsModel(
      running,
      model
    ))
    {
      throw new InvalidOperationException(
        "Ollama did not confirm the resident model through /api/ps."
      );
    }

    UpdateStatus(
      model,
      "ready",
      true,
      "verified",
      null,
      null
    );
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

  private void SetFailure(
    string operation,
    Exception exception
  )
  {
    var current = GetStatus();
    UpdateStatus(
      current.ConfiguredModel,
      "error",
      false,
      operation,
      exception.Message,
      Guid.NewGuid().ToString(
        "N"
      )
    );
  }

  private void UpdateStatus(
    string model,
    string state,
    bool loaded,
    string? operation,
    string? diagnostic,
    string? traceId
  )
  {
    lock (_stateGate)
    {
      _status = new ResidentModelStatus(
        model,
        state,
        loaded,
        "adaptive",
        operation,
        diagnostic,
        traceId
      );
    }
  }

  private static bool ContainsModel(
    IReadOnlyList<OllamaRunningModel> models,
    string model
  )
  {
    return models.Any(
      running => string.Equals(
        running.Name,
        model,
        StringComparison.OrdinalIgnoreCase
      )
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
