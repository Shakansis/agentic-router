using System.Diagnostics;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Models;

public interface IModelDiagnosticService
{
  Task<ModelDiagnosticsResponse> GetAsync(
    CancellationToken cancellationToken
  );

  Task<ModelTestResult> TestAsync(
    string model,
    string traceId,
    CancellationToken cancellationToken
  );
}

public sealed class ModelDiagnosticService : IModelDiagnosticService
{
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;

  public ModelDiagnosticService(
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
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
    IReadOnlyList<OllamaRunningModel> loaded;

    try
    {
      loaded = await _ollamaClient.GetRunningModelsAsync(
        baseUri,
        cancellationToken
      );
    }
    catch (OllamaProviderException)
    {
      loaded = [];
    }
    var configured = new List<ConfiguredModel>
    {
      new(
        "Router",
        settings.RouterModel,
        false
      ),
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
    string model,
    string traceId,
    CancellationToken cancellationToken
  )
  {
    var stopwatch = Stopwatch.StartNew();

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

      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken
      );
      timeout.CancelAfter(
        TimeSpan.FromSeconds(
          15
        )
      );
      long? firstChunk = null;

      await foreach (var update in _ollamaClient.StreamChatAsync(
        baseUri,
        model,
        [
          new ChatMessage(
            "user",
            "Reply with exactly: OK"
          )
        ],
        new ProviderCallContext(
          null,
          null,
          traceId,
          null,
          UsageModelRoles.ModelTest,
          "model-connectivity-test"
        ),
        timeout.Token
      ))
      {
        if (
          firstChunk is null
          && !string.IsNullOrEmpty(
            update.Delta
          )
        )
        {
          firstChunk = stopwatch.ElapsedMilliseconds;
        }
      }

      return new ModelTestResult(
        model,
        true,
        firstChunk,
        stopwatch.ElapsedMilliseconds,
        "Completed",
        null,
        null
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
      or OperationCanceledException
    )
    {
      return Failure(
        model,
        stopwatch,
        traceId,
        exception is OperationCanceledException
          ? "The model test timed out or was cancelled."
          : exception.Message
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
