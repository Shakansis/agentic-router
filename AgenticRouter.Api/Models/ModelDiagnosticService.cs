using System.Diagnostics;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Models;

public interface IModelDiagnosticService
{
  Task<ModelDiagnosticsResponse> GetAsync(
    CancellationToken cancellationToken
  );

  Task<ModelTestResult> TestAsync(
    ModelTestRequest request,
    string traceId,
    CancellationToken cancellationToken,
    Func<ModelTestProgress, Task>? onProgress = null
  );
}

public sealed class ModelDiagnosticService : IModelDiagnosticService
{
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IOllamaManagedServerManager _managedOllamaServers;

  public ModelDiagnosticService(
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IOllamaManagedServerManager managedOllamaServers
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _managedOllamaServers = managedOllamaServers;
  }

  public async Task<ModelDiagnosticsResponse> GetAsync(
    CancellationToken cancellationToken
  )
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
    var loaded = new List<OllamaRunningModel>();
    foreach (var endpoint in _managedOllamaServers.GetActiveServers()
      .Select(server => server.Endpoint).Prepend(baseUri).Distinct())
    {
      try
      {
        loaded.AddRange(await _ollamaClient.GetRunningModelsAsync(endpoint, cancellationToken));
      }
      catch (OllamaProviderException)
      {
        // A diagnostic read must not start or replace a server.
      }
    }
    var configured = new List<ConfiguredModel>
    {
      new(
        "Default",
        settings.DefaultModel,
        false
      )
    };

    foreach (var intention in settings.Intentions)
    {
      configured.Add(
        new ConfiguredModel(
          $"{intention.Key} · primary",
          intention.Value.Model,
          true
        )
      );

      if (!string.Equals(
        intention.Value.FallbackModel,
        "none",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        configured.Add(
          new ConfiguredModel(
            $"{intention.Key} · fallback",
            intention.Value.FallbackModel,
            true
          )
        );
      }
    }

    return new ModelDiagnosticsResponse(
      configured.Select(
        item => ToDiagnostic(
          item,
          settings.DefaultModel,
          installed,
          loaded
        )
      ).ToArray(),
      "The configured context limit and token counts are estimates because Ollama "
        + "does not reliably expose a context size for every installed model."
    );
  }

  public async Task<ModelTestResult> TestAsync(
    ModelTestRequest request,
    string traceId,
    CancellationToken cancellationToken,
    Func<ModelTestProgress, Task>? onProgress = null
  )
  {
    var stopwatch = Stopwatch.StartNew();
    var model = request.Model;
    var requestCancellation = cancellationToken;

    if (string.IsNullOrWhiteSpace(
      model
    ))
    {
      return Failure(
        model,
        stopwatch,
        traceId,
        "A model must be selected."
      );
    }

    try
    {
      async Task ReportAsync(string stage, long characters = 0)
      {
        if (onProgress is not null)
          await onProgress(new ModelTestProgress(stage, stopwatch.ElapsedMilliseconds, characters));
      }
      await ReportAsync("preparing");
      var settings = request.Settings ?? await _settingsStore.GetAsync(cancellationToken);
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(TimeSpan.FromSeconds(settings.Runtime.GenerationTimeoutSeconds));
      cancellationToken = timeout.Token;
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      var installed = await _ollamaClient.GetModelsAsync(
        baseUri,
        cancellationToken
      );

      if (!installed.Any(
        item => string.Equals(
          item.Name,
          model,
          StringComparison.OrdinalIgnoreCase
        )
      ))
      {
        return Failure(
          model,
          stopwatch,
          traceId,
          "The selected model is not installed."
        );
      }

      ExecutionInferenceMetrics? metrics = null;
      ProviderTokenUsage? usage = null;
      OllamaContextResolution? runtime = null;
      var profile = ProviderGenerationProfiles.Resolve(settings.InferenceProfiles,
        request.Profile, "chat", false) with
      { MaximumOutputTokens = 8192 };

      await ReportAsync("loading-prefill");
      long receivedCharacters = 0;
      long lastProgressMilliseconds = -1000;

      await foreach (var update in _ollamaClient.StreamChatAsync(
        baseUri,
        model,
        [
          new ChatMessage(
            "user",
            "MODEL_TPS_PROBE_V2: Implement a complete local task queue in C# with bounded concurrency, "
              + "cancellation, retries, durable JSON state, graceful shutdown and an HTTP API. "
              + "Include the full implementation, meaningful integration tests, usage examples and a detailed "
              + "discussion of failure recovery and performance. Work entirely in your response, without tools. "
              + "Write at least 9000 tokens of substantive code and explanation. Do not abbreviate files, "
              + "use placeholders or stop at an outline. Continue until every component and test is complete."
          )
        ],
        new ProviderCallContext(
          null,
          null,
          traceId,
          null,
          UsageModelRoles.ModelTest,
          "model-throughput-test",
          InferenceObserver: value => metrics = ExecutionInferenceMetrics.Combine(metrics, value),
          RuntimeSettingsOverride: settings
        ),
        new ProviderChatOptions(false, [], GenerationProfile: profile),
        cancellationToken
      ))
      {
        runtime ??= update.ContextResolution;
        usage = update.Usage ?? usage;
        receivedCharacters += (update.Delta?.Length ?? 0) + (update.ThinkingDelta?.Length ?? 0);
        if (receivedCharacters > 0 && stopwatch.ElapsedMilliseconds - lastProgressMilliseconds >= 500)
        {
          await ReportAsync("generating", receivedCharacters);
          lastProgressMilliseconds = stopwatch.ElapsedMilliseconds;
        }
      }

      await ReportAsync("collecting", receivedCharacters);

      return new ModelTestResult(
        model,
        true,
        metrics?.TimeToFirstTokenMilliseconds is double ttft ? (long)Math.Round(ttft) : null,
        stopwatch.ElapsedMilliseconds,
        "Completed",
        null,
        null,
        metrics,
        runtime,
        profile.Id,
        profile.Thinking,
        usage?.InputTokens,
        usage?.LoadDurationNanoseconds / 1_000_000d,
        usage?.PromptEvalDurationNanoseconds / 1_000_000d,
        profile.MaximumOutputTokens ?? 8192
      );
    }
    catch (OllamaRuntimeProfileException exception)
    {
      var message = exception.Error.Code == "request-context-does-not-fit"
        ? $"The 8K output test needs {exception.Error.RequiredContextTokens} context tokens, "
          + $"but the selected maximum is {exception.Error.MaximumContextTokens}. "
          + "Increase the Model test maximum context (and any model override) in Ollama context settings, then test again without saving."
        : exception.Message;
      return Failure(model, stopwatch, traceId, message);
    }
    catch (OllamaProviderException exception)
    {
      return Failure(
        model,
        stopwatch,
        traceId,
        exception.Message
      );
    }
    catch (OperationCanceledException)
    {
      return Failure(
        model,
        stopwatch,
        traceId,
        requestCancellation.IsCancellationRequested
          ? "The model test was cancelled."
          : "The model test exceeded the selected generation timeout. Increase it in Runtime settings for longer tests."
      );
    }
  }

  private static ModelDiagnostic ToDiagnostic(
    ConfiguredModel item,
    string defaultModel,
    IReadOnlyList<InstalledModel> installed,
    IReadOnlyList<OllamaRunningModel> loaded
  )
  {
    var resolved = item.CanUseDefault
      && string.Equals(
        item.Value,
        "default",
        StringComparison.OrdinalIgnoreCase
      )
        ? defaultModel
        : item.Value;
    var misconfigured = string.IsNullOrWhiteSpace(
      resolved
    ) || string.Equals(
      resolved,
      "default",
      StringComparison.OrdinalIgnoreCase
    ) || string.Equals(
      resolved,
      "none",
      StringComparison.OrdinalIgnoreCase
    );
    var isInstalled = !misconfigured && installed.Any(
      model => string.Equals(
        model.Name,
        resolved,
        StringComparison.OrdinalIgnoreCase
      )
    );
    var isLoaded = isInstalled && loaded.Any(
      model => string.Equals(
        model.Name,
        resolved,
        StringComparison.OrdinalIgnoreCase
      )
    );
    var status = misconfigured
      ? "Misconfigured"
      : isLoaded
        ? "Loaded"
        : isInstalled
          ? "Installed"
          : "Unavailable";

    return new ModelDiagnostic(
      item.Configuration,
      item.Value,
      misconfigured
        ? null
        : resolved,
      status
    );
  }

  private static ModelTestResult Failure(
    string model,
    Stopwatch stopwatch,
    string traceId,
    string error
  )
  {
    return new ModelTestResult(
      model,
      false,
      null,
      stopwatch.ElapsedMilliseconds,
      "Failed",
      traceId,
      error
    );
  }

  private sealed record ConfiguredModel(
    string Configuration,
    string Value,
    bool CanUseDefault
  );
}
