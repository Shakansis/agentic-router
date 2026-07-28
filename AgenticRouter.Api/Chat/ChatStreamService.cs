using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Providers.Ollama;

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

    public ChatStreamService(
      ISettingsStore settingsStore,
      IOllamaClient ollamaClient,
      IMarkdownRenderer markdownRenderer
    )
    {
        _settingsStore = settingsStore;
        _ollamaClient = ollamaClient;
        _markdownRenderer = markdownRenderer;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
      ChatRequest request,
      string requestId,
      [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();

        yield return CreateEvent(
          requestId,
          "request.received",
          $"Request {requestId} received.",
          null,
          null,
          null,
          stopwatch.ElapsedMilliseconds
        );

        var settings = await _settingsStore.GetAsync(
          cancellationToken
        );
        var baseUri = new Uri(
          settings.OllamaUrl,
          UriKind.Absolute
        );

        yield return CreateEvent(
          requestId,
          "settings.loaded",
          "Settings loaded.",
          null,
          null,
          null,
          stopwatch.ElapsedMilliseconds
        );
        yield return CreateEvent(
          requestId,
          "ollama.models-query-started",
          "Checking installed Ollama models.",
          null,
          null,
          null,
          stopwatch.ElapsedMilliseconds
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
            yield return CreateEvent(
              requestId,
              "model.explicit-selected",
              $"Explicit model selected: {selectedModel}. Router bypassed.",
              null,
              selectedModel,
              null,
              stopwatch.ElapsedMilliseconds
            );
        }
        else
        {
            yield return CreateEvent(
              requestId,
              "router.model-resolved",
              $"Router model resolved: {settings.RouterModel}.",
              null,
              settings.RouterModel,
              null,
              stopwatch.ElapsedMilliseconds
            );

            if (!ContainsModel(
              models,
              settings.RouterModel
            ))
            {
                yield return CreateEvent(
                  requestId,
                  "router.warning",
                  $"Router model '{settings.RouterModel}' is unavailable; using general-chat fallback.",
                  null,
                  settings.RouterModel,
                  GeneralChat,
                  stopwatch.ElapsedMilliseconds
                );
            }
            else
            {
                yield return CreateEvent(
                  requestId,
                  "router.classification-started",
                  "Classifying request intention.",
                  null,
                  settings.RouterModel,
                  null,
                  stopwatch.ElapsedMilliseconds
                );
                yield return CreateEvent(
                  requestId,
                  "ollama.connection-started",
                  $"Connecting to Ollama for router model {settings.RouterModel}.",
                  null,
                  settings.RouterModel,
                  null,
                  stopwatch.ElapsedMilliseconds
                );

                var routingOutcome = await ClassifyAsync(
                  baseUri,
                  settings.RouterModel,
                  request,
                  cancellationToken
                );

                if (routingOutcome.Decision is not null)
                {
                    intention = routingOutcome.Decision.Intention;
                    yield return CreateEvent(
                      requestId,
                      "router.classified",
                      $"Intention classified as {intention} ({routingOutcome.Decision.Confidence:P0}).",
                      null,
                      settings.RouterModel,
                      intention,
                      stopwatch.ElapsedMilliseconds
                    );
                }
                else
                {
                    yield return CreateEvent(
                      requestId,
                      "router.warning",
                      routingOutcome.Warning,
                      null,
                      settings.RouterModel,
                      GeneralChat,
                      stopwatch.ElapsedMilliseconds
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

        yield return CreateEvent(
          requestId,
          "target.model-resolved",
          $"Target model resolved: {selectedModel}.",
          null,
          selectedModel,
          isAuto
            ? intention
            : null,
          stopwatch.ElapsedMilliseconds
        );
        yield return CreateEvent(
          requestId,
          "ollama.connection-started",
          $"Connecting to Ollama for target model {selectedModel}.",
          null,
          selectedModel,
          isAuto
            ? intention
            : null,
          stopwatch.ElapsedMilliseconds
        );

        var messages = BuildTargetMessages(
          request,
          settings,
          intention
        );
        var answer = new StringBuilder();
        var receivedFirstChunk = false;

        await using var updates = _ollamaClient.StreamChatAsync(
          baseUri,
          selectedModel,
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
                throw ToChatException(
                  exception,
                  selectedModel,
                  isAuto
                    ? intention
                    : null
                );
            }

            if (update.Accepted)
            {
                yield return CreateEvent(
                  requestId,
                  "ollama.generation-accepted",
                  "Generation accepted by Ollama.",
                  null,
                  selectedModel,
                  isAuto
                    ? intention
                    : null,
                  stopwatch.ElapsedMilliseconds
                );
                continue;
            }

            if (string.IsNullOrEmpty(
              update.Delta
            ))
            {
                continue;
            }

            if (!receivedFirstChunk)
            {
                receivedFirstChunk = true;
                yield return CreateEvent(
                  requestId,
                  "response.first-chunk",
                  "First response chunk received.",
                  null,
                  selectedModel,
                  isAuto
                    ? intention
                    : null,
                  stopwatch.ElapsedMilliseconds
                );
            }

            answer.Append(
              update.Delta
            );
            yield return CreateEvent(
              requestId,
              "response.delta",
              null,
              update.Delta,
              selectedModel,
              isAuto
                ? intention
                : null,
              stopwatch.ElapsedMilliseconds
            );
        }

        yield return CreateEvent(
          requestId,
          "response.completed",
          $"Response completed in {stopwatch.ElapsedMilliseconds} ms.",
          null,
          selectedModel,
          isAuto
            ? intention
            : null,
          stopwatch.ElapsedMilliseconds,
          _markdownRenderer.Render(
            answer.ToString()
          )
        );
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
            var routerOutput = await _ollamaClient.ClassifyAsync(
              baseUri,
              routerModel,
              BuildRouterMessages(
                request
              ),
              cancellationToken
            );

            return TryParseRouterDecision(
              routerOutput,
              out var decision,
              out var parseError
            )
              ? new RoutingOutcome(
                decision,
                null
              )
              : new RoutingOutcome(
                null,
                $"{parseError} Using general-chat fallback."
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

    private static IReadOnlyList<ChatMessage> BuildRouterMessages(
      ChatRequest request
    )
    {
        var recentContext = request.History?
          .Where(
            message => message.Role is "user" or "assistant"
          )
          .TakeLast(
            2
          )
          .Select(
            message => $"{message.Role}: {message.Content}"
          ) ?? [];
        var context = string.Join(
          "\n",
          recentContext
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
        BuildRouterSystemPrompt()
      ),
      new ChatMessage(
        "user",
        content
      )
        ];
    }

    private static string BuildRouterSystemPrompt()
    {
        return "Classify the user's current request into exactly one supported intention. "
          + "Return only strict JSON with this shape: "
          + "{\"intention\":\"general-chat\",\"confidence\":0.0}. "
          + $"Supported intentions: {string.Join(", ", SettingsDefaults.IntentionNames)}. "
          + "Confidence must be a number from 0 to 1.";
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
          || decision.Confidence is < 0 or > 1
        )
        {
            error = "Router returned an unsupported intention or invalid confidence.";
            return false;
        }

        error = string.Empty;
        return true;
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

    private static ChatStreamEvent CreateEvent(
      string requestId,
      string type,
      string? message,
      string? delta,
      string? selectedModel,
      string? intention,
      long? elapsedMilliseconds,
      string? renderedHtml = null
    )
    {
        return new ChatStreamEvent(
          requestId,
          type,
          DateTimeOffset.UtcNow,
          message,
          delta,
          selectedModel,
          intention,
          elapsedMilliseconds,
          renderedHtml,
          null
        );
    }

    private sealed record RouterDecision(
      string Intention,
      double Confidence
    );

    private sealed record RoutingOutcome(
      RouterDecision? Decision,
      string? Warning
    );
}
