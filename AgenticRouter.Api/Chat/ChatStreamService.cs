using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;

namespace AgenticRouter.Api.Chat;

public sealed class ChatStreamService : IChatStreamService
{
  private const string GeneralChat = "general-chat";
  private static readonly JsonSerializerOptions RouterJsonOptions = new(
    JsonSerializerDefaults.Web
  );

  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IMarkdownRenderer _markdownRenderer;
  private readonly IResidentModelManager _residentModel;

  public ChatStreamService(
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IMarkdownRenderer markdownRenderer,
    IResidentModelManager residentModel
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _markdownRenderer = markdownRenderer;
    _residentModel = residentModel;
  }

  public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
    ChatRequest request,
    string requestId,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    using var requestLease = _residentModel.BeginRequest();
    var stopwatch = Stopwatch.StartNew();
    var recoveryActive = false;
    string? recoveryTarget = null;

    try
    {
      yield return Event(
        requestId,
        "request.received",
        $"Request {requestId} received.",
        stopwatch
      );

      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );

      yield return Event(
        requestId,
        "settings.loaded",
        "Settings loaded.",
        stopwatch
      );
      yield return Event(
        requestId,
        "ollama.models-query-started",
        "Checking installed Ollama models.",
        stopwatch
      );

      var models = await GetModelsAsync(
        baseUri,
        cancellationToken
      );
      var isAuto = string.IsNullOrWhiteSpace(
        request.Model
      ) || string.Equals(
        request.Model,
        "auto",
        StringComparison.OrdinalIgnoreCase
      );
      var intention = GeneralChat;
      var selectedModel = request.Model.Trim();

      if (!isAuto)
      {
        yield return Event(
          requestId,
          "model.explicit-selected",
          $"Explicit model selected: {selectedModel}. Router bypassed.",
          stopwatch,
          selectedModel
        );
      }
      else
      {
        yield return Event(
          requestId,
          "router.model-resolved",
          $"Resident router model resolved: {settings.RouterModel}.",
          stopwatch,
          settings.RouterModel
        );

        if (!ContainsModel(
          models,
          settings.RouterModel
        ))
        {
          yield return Event(
            requestId,
            "router.warning",
            $"Router model '{settings.RouterModel}' is unavailable; using general-chat fallback.",
            stopwatch,
            settings.RouterModel,
            GeneralChat
          );
        }
        else
        {
          yield return Event(
            requestId,
            "router.classification-started",
            "Classifying request intention.",
            stopwatch,
            settings.RouterModel
          );
          yield return Event(
            requestId,
            "ollama.connection-started",
            $"Connecting to Ollama for router model {settings.RouterModel}.",
            stopwatch,
            settings.RouterModel
          );

          var routing = await ClassifyAsync(
            baseUri,
            settings.RouterModel,
            request,
            cancellationToken
          );

          if (routing.Decision is not null)
          {
            intention = routing.Decision.Intention;
            yield return Event(
              requestId,
              "router.classified",
              $"Intention classified as {intention} "
                + $"({FormatConfidence(routing.Decision.Confidence)}).",
              stopwatch,
              settings.RouterModel,
              intention
            );
          }
          else
          {
            yield return Event(
              requestId,
              "router.warning",
              routing.Warning,
              stopwatch,
              settings.RouterModel,
              GeneralChat
            );
          }
        }

        selectedModel = ResolveTargetModel(
          settings,
          intention
        );
      }

      if (!ContainsModel(
        models,
        selectedModel
      ))
      {
        throw new ChatStageException(
          "target-model-resolution",
          $"The target model '{selectedModel}' is not installed in Ollama.",
          "The configured target model was not present in the /api/tags response.",
          selectedModel,
          intention,
          400,
          true
        );
      }

      yield return Event(
        requestId,
        "target.model-resolved",
        $"Target model resolved: {selectedModel}.",
        stopwatch,
        selectedModel,
        isAuto
          ? intention
          : null
      );
      yield return Event(
        requestId,
        "ollama.connection-started",
        $"Connecting to Ollama for target model {selectedModel}.",
        stopwatch,
        selectedModel,
        isAuto
          ? intention
          : null
      );

      var messages = BuildTargetMessages(
        request,
        settings,
        intention
      );
      var progress = new GenerationProgress();

      await foreach (var streamEvent in StreamAttemptAsync(
        baseUri,
        selectedModel,
        messages,
        requestId,
        isAuto
          ? intention
          : null,
        stopwatch,
        progress,
        cancellationToken
      ))
      {
        yield return streamEvent;
      }

      if (progress.Failure is not null)
      {
        var failure = progress.Failure;
        var canRecover = failure.IsMemoryPressure
          && !progress.ReceivedFirstChunk
          && !string.Equals(
            selectedModel,
            settings.RouterModel,
            StringComparison.OrdinalIgnoreCase
          );

        if (!canRecover)
        {
          throw ToChatException(
            failure,
            selectedModel,
            isAuto
              ? intention
              : null
          );
        }

        yield return Event(
          requestId,
          "memory-pressure-detected",
          $"Ollama reported memory pressure while loading {selectedModel}.",
          stopwatch,
          selectedModel,
          intention
        );
        yield return Event(
          requestId,
          "resident-model-eviction-started",
          $"Evicting resident router model {settings.RouterModel} for one adaptive retry.",
          stopwatch,
          settings.RouterModel,
          intention
        );

        recoveryActive = await _residentModel.EvictForRecoveryAsync(
          selectedModel,
          cancellationToken
        );
        recoveryTarget = selectedModel;

        if (!recoveryActive)
        {
          throw ToChatException(
            failure,
            selectedModel,
            intention
          );
        }

        yield return Event(
          requestId,
          "resident-model-evicted",
          $"Resident router model {settings.RouterModel} was temporarily evicted.",
          stopwatch,
          settings.RouterModel,
          intention
        );
        yield return Event(
          requestId,
          "target-request-retry-started",
          $"Retrying target model {selectedModel} once.",
          stopwatch,
          selectedModel,
          intention
        );

        progress.Failure = null;

        await foreach (var streamEvent in StreamAttemptAsync(
          baseUri,
          selectedModel,
          messages,
          requestId,
          isAuto
            ? intention
            : null,
          stopwatch,
          progress,
          cancellationToken
        ))
        {
          yield return streamEvent;
        }

        if (progress.Failure is not null)
        {
          var retryFailure = progress.Failure;
          yield return Event(
            requestId,
            "resident-model-reload-started",
            $"Reloading resident router model {settings.RouterModel}.",
            stopwatch,
            settings.RouterModel,
            intention
          );
          var restored = await _residentModel.RestoreAfterRecoveryAsync(
            selectedModel,
            cancellationToken
          );
          recoveryActive = false;
          yield return Event(
            requestId,
            restored
              ? "resident-model-reloaded"
              : "resident-model-reload-failed",
            restored
              ? $"Resident router model {settings.RouterModel} was restored."
              : $"Resident router model {settings.RouterModel} could not be restored.",
            stopwatch,
            settings.RouterModel,
            intention
          );

          throw ToChatException(
            retryFailure,
            selectedModel,
            intention
          );
        }

        yield return Event(
          requestId,
          "target-request-recovered",
          $"Target model {selectedModel} recovered after adaptive eviction.",
          stopwatch,
          selectedModel,
          intention
        );
        yield return Event(
          requestId,
          "resident-model-reload-started",
          $"Reloading resident router model {settings.RouterModel}.",
          stopwatch,
          settings.RouterModel,
          intention
        );
        var reloaded = await _residentModel.RestoreAfterRecoveryAsync(
          selectedModel,
          cancellationToken
        );
        recoveryActive = false;
        yield return Event(
          requestId,
          reloaded
            ? "resident-model-reloaded"
            : "resident-model-reload-failed",
          reloaded
            ? $"Resident router model {settings.RouterModel} was restored."
            : $"Resident router model {settings.RouterModel} could not be restored.",
          stopwatch,
          settings.RouterModel,
          intention
        );
      }

      yield return new ChatStreamEvent(
        requestId,
        "response.completed",
        DateTimeOffset.UtcNow,
        $"Response completed in {stopwatch.ElapsedMilliseconds} ms.",
        null,
        selectedModel,
        isAuto
          ? intention
          : null,
        stopwatch.ElapsedMilliseconds,
        _markdownRenderer.Render(
          progress.Answer.ToString()
        ),
        null
      );
    }
    finally
    {
      if (recoveryActive && recoveryTarget is not null)
      {
        await _residentModel.RestoreAfterRecoveryAsync(
          recoveryTarget,
          CancellationToken.None
        );
      }
    }
  }

  private async IAsyncEnumerable<ChatStreamEvent> StreamAttemptAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string requestId,
    string? intention,
    Stopwatch stopwatch,
    GenerationProgress progress,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    await using var updates = _ollamaClient.StreamChatAsync(
      baseUri,
      model,
      messages,
      cancellationToken
    ).GetAsyncEnumerator(
      cancellationToken
    );

    while (true)
    {
      OllamaChatUpdate update;

      try
      {
        if (!await updates.MoveNextAsync())
        {
          break;
        }

        update = updates.Current;
      }
      catch (OllamaProviderException exception)
      {
        progress.Failure = exception;
        yield break;
      }

      if (update.Accepted)
      {
        yield return Event(
          requestId,
          "ollama.generation-accepted",
          "Generation accepted by Ollama.",
          stopwatch,
          model,
          intention
        );
        continue;
      }

      if (string.IsNullOrEmpty(
        update.Delta
      ))
      {
        continue;
      }

      if (!progress.ReceivedFirstChunk)
      {
        progress.ReceivedFirstChunk = true;
        yield return Event(
          requestId,
          "response.first-chunk",
          "First response chunk received.",
          stopwatch,
          model,
          intention
        );
      }

      progress.Answer.Append(
        update.Delta
      );
      yield return new ChatStreamEvent(
        requestId,
        "response.delta",
        DateTimeOffset.UtcNow,
        null,
        update.Delta,
        model,
        intention,
        stopwatch.ElapsedMilliseconds,
        _markdownRenderer.Render(
          progress.Answer.ToString()
        ),
        null
      );
    }
  }

  private async Task<IReadOnlyList<InstalledModel>> GetModelsAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await _ollamaClient.GetModelsAsync(
        baseUri,
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      throw ToChatException(
        exception,
        null,
        null
      );
    }
  }

  private async Task<RoutingOutcome> ClassifyAsync(
    Uri baseUri,
    string routerModel,
    ChatRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var output = await _ollamaClient.ClassifyAsync(
        baseUri,
        routerModel,
        BuildRouterMessages(
          request
        ),
        cancellationToken
      );

      return TryParseRouterDecision(
        output,
        out var decision,
        out var error
      )
        ? new RoutingOutcome(
          decision,
          null
        )
        : new RoutingOutcome(
          null,
          $"{error} Using general-chat fallback."
        );
    }
    catch (OllamaProviderException exception)
    {
      return new RoutingOutcome(
        null,
        $"Router classification failed: {exception.Message} Using general-chat fallback."
      );
    }
  }

  private static IReadOnlyList<ChatMessage> BuildRouterMessages(
    ChatRequest request
  )
  {
    var context = string.Join(
      "\n",
      request.History?
        .Where(
          message => message.Role is "user" or "assistant"
        )
        .TakeLast(
          2
        )
        .Select(
          message => $"{message.Role}: {message.Content}"
        ) ?? []
    );
    var content = string.IsNullOrWhiteSpace(
      context
    )
      ? $"Current request:\n{request.Message}"
      : $"Recent visible context:\n{context}\n\nCurrent request:\n{request.Message}";

    return
    [
      new ChatMessage(
        "system",
        "Classify the current request into one supported intention. "
          + "Return only strict JSON shaped as "
          + "{\"intention\":\"general-chat\",\"confidence\":0.0}. "
          + $"Supported intentions: {string.Join(", ", SettingsDefaults.IntentionNames)}."
      ),
      new ChatMessage(
        "user",
        content
      )
    ];
  }

  private static IReadOnlyList<ChatMessage> BuildTargetMessages(
    ChatRequest request,
    ApplicationSettings settings,
    string intention
  )
  {
    var messages = new List<ChatMessage>
    {
      new(
        "system",
        settings.Intentions[intention].SystemPrompt
      )
    };

    if (request.History is not null)
    {
      messages.AddRange(
        request.History.Where(
          message => message.Role is "user" or "assistant"
        )
      );
    }

    messages.Add(
      new ChatMessage(
        "user",
        request.Message
      )
    );
    return messages;
  }

  private static string ResolveTargetModel(
    ApplicationSettings settings,
    string intention
  )
  {
    var configured = settings.Intentions[intention].Model;

    return string.Equals(
      configured,
      "default",
      StringComparison.Ordinal
    )
      ? settings.DefaultModel
      : configured;
  }

  private static bool ContainsModel(
    IReadOnlyList<InstalledModel> models,
    string model
  )
  {
    return models.Any(
      installed => string.Equals(
        installed.Name,
        model,
        StringComparison.OrdinalIgnoreCase
      )
    );
  }

  private static bool TryParseRouterDecision(
    string value,
    out RouterDecision? decision,
    out string error
  )
  {
    try
    {
      decision = JsonSerializer.Deserialize<RouterDecision>(
        value,
        RouterJsonOptions
      );
    }
    catch (JsonException)
    {
      decision = null;
      error = "Router returned invalid JSON.";
      return false;
    }

    if (
      decision is null
      || !SettingsDefaults.IntentionNames.Contains(
        decision.Intention,
        StringComparer.Ordinal
      )
      || (
        decision.Confidence is double confidence
        && (
          !double.IsFinite(
            confidence
          )
          || confidence is < 0 or > 1
        )
      )
    )
    {
      error = "Router returned an unsupported intention or invalid confidence.";
      return false;
    }

    error = string.Empty;
    return true;
  }

  private static string FormatConfidence(
    double? confidence
  )
  {
    return confidence is double value
      ? value.ToString(
        "P0",
        CultureInfo.InvariantCulture
      )
      : "confidence unavailable";
  }

  private static ChatStageException ToChatException(
    OllamaProviderException exception,
    string? model,
    string? intention
  )
  {
    return new ChatStageException(
      exception.Stage,
      exception.Message,
      exception.TechnicalMessage,
      model,
      intention,
      exception.HttpStatus,
      exception.Recoverable,
      exception
    );
  }

  private static ChatStreamEvent Event(
    string requestId,
    string type,
    string? message,
    Stopwatch stopwatch,
    string? model = null,
    string? intention = null
  )
  {
    return new ChatStreamEvent(
      requestId,
      type,
      DateTimeOffset.UtcNow,
      message,
      null,
      model,
      intention,
      stopwatch.ElapsedMilliseconds,
      null,
      null
    );
  }

  private sealed class GenerationProgress
  {
    public StringBuilder Answer { get; } = new();

    public bool ReceivedFirstChunk { get; set; }

    public OllamaProviderException? Failure { get; set; }
  }

  private sealed record RouterDecision(
    string Intention,
    double? Confidence
  );

  private sealed record RoutingOutcome(
    RouterDecision? Decision,
    string? Warning
  );
}
