using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Routing;

public interface IIntentionRouter
{
  Task<IntentionRoutingResult> RouteAsync(
    Uri baseUri,
    string routerModel,
    ChatRequest request,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  );
}

public sealed record IntentionRoutingResult(
  RouterDecision? Decision,
  string? FailureType,
  string? Warning,
  bool RawOutputCaptured
);

public sealed class IntentionRouter : IIntentionRouter
{
  private const string RouterPrompt =
    "Classify the latest user request into exactly one supported intention: "
    + "general-chat, documentation, software-development, software-architecture, "
    + "rpg-storytelling, review-and-testing. Return JSON only with intention, "
    + "optional confidence from 0 to 1, and optional reason up to 240 characters. "
    + "Do not return Markdown or explanatory prose. The latest user message has priority. "
    + "Explicit implementation requests override earlier conversational themes. "
    + "Classify the requested action, not merely the subject: coding a game about an RPG "
    + "character is software-development; writing a story that mentions code is not "
    + "automatically software-development; reviewing tests is review-and-testing; "
    + "designing service boundaries is software-architecture; writing a plan or "
    + "specification is documentation.";

  private readonly IOllamaClient _ollamaClient;
  private readonly IRouterResponseParser _parser;
  private readonly ILogger<IntentionRouter> _logger;

  public IntentionRouter(
    IOllamaClient ollamaClient,
    IRouterResponseParser parser,
    ILogger<IntentionRouter> logger
  )
  {
    _ollamaClient = ollamaClient;
    _parser = parser;
    _logger = logger;
  }

  public async Task<IntentionRoutingResult> RouteAsync(
    Uri baseUri,
    string routerModel,
    ChatRequest request,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var output = await _ollamaClient.ClassifyAsync(
        baseUri,
        routerModel,
        BuildMessages(
          request
        ),
        usageContext,
        cancellationToken
      );
      var parsed = _parser.Parse(
        output
      );

      if (parsed.Decision is not null)
      {
        return new IntentionRoutingResult(
          parsed.Decision,
          null,
          null,
          parsed.RawOutputCaptured
        );
      }

      _logger.LogWarning(
        "Router output was captured internally and rejected as {FailureType}.",
        parsed.FailureType
      );

      return new IntentionRoutingResult(
        null,
        parsed.FailureType,
        $"Router response rejected ({parsed.FailureType}); using general-chat fallback.",
        parsed.RawOutputCaptured
      );
    }
    catch (OllamaProviderException exception)
    {
      _logger.LogWarning(
        exception,
        "Router classification failed."
      );

      return new IntentionRoutingResult(
        null,
        "provider-failure",
        $"Router classification failed: {exception.Message} Using general-chat fallback.",
        false
      );
    }
  }

  private static IReadOnlyList<ChatMessage> BuildMessages(
    ChatRequest request
  )
  {
    var current = $"Current user request:\n{request.Message}";
    var previousUser = request.History?
      .LastOrDefault(
        message => message.Role == "user"
      )
      ?.Content;

    if (
      IsDependentFollowUp(
        request.Message
      )
      && !string.IsNullOrWhiteSpace(
        previousUser
      )
    )
    {
      current =
        $"Immediately previous user request (classification hint only):\n{previousUser}\n\n"
        + current;
    }

    return
    [
      new ChatMessage(
        "system",
        RouterPrompt
      ),
      new ChatMessage(
        "user",
        current
      )
    ];
  }

  private static bool IsDependentFollowUp(
    string message
  )
  {
    if (message.Length > 180)
    {
      return false;
    }

    var normalized = message.Trim();
    return new[]
    {
      "continue",
      "continue.",
      "continua",
      "continue daí",
      "make it shorter",
      "shorten it",
      "faça menor",
      "deixe mais curto",
      "melhore isso",
      "revise isso"
    }.Any(
      marker => normalized.StartsWith(
        marker,
        StringComparison.OrdinalIgnoreCase
      )
    );
  }
}
