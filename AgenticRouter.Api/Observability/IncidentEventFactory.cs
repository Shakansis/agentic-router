using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Observability;

public static class IncidentEventFactory
{
  private static readonly IReadOnlySet<string> IgnoredTypes = new HashSet<string>(
    ["reasoning.delta", "response.delta", "request.heartbeat"],
    StringComparer.Ordinal
  );

  public static IncidentEvent? FromChatEvent(ITraceContext trace, ChatStreamEvent source)
  {
    if (IgnoredTypes.Contains(source.Type))
    {
      return null;
    }

    var error = source.Error;
    var links = trace.SnapshotLinks();
    var summary = source.Type == "error"
      ? "The request reached a typed terminal failure."
      : SummaryFor(source.Type);
    var details = error?.Details;
    var contextFit = source.IncidentContextFit is null
      ? CreateContextFit(details, source.ContextUsage)
      : new IncidentContextFit(
        source.IncidentContextFit.EstimatedInputTokens,
        source.IncidentContextFit.ReservedOutputTokens,
        source.IncidentContextFit.RequiredContextTokens,
        source.IncidentContextFit.MaximumContextTokens,
        source.IncidentContextFit.EffectiveContextTokens
      );

    return new IncidentEvent
    {
      EventId = Guid.NewGuid().ToString("N"),
      TraceId = trace.TraceId,
      Sequence = trace.NextSequence(),
      TimestampUtc = source.Timestamp,
      Category = CategoryFor(source.Type),
      Stage = error?.Stage ?? source.Type,
      Code = Detail(details, "code") ?? source.Type,
      Status = source.Type switch
      {
        "error" => "failed",
        "response.completed" => "completed",
        "request.cancelled" => "cancelled",
        _ => "observed"
      },
      Summary = summary,
      RequestId = source.RequestId,
      ConversationId = source.ConversationSessionId,
      TurnId = links.GetValueOrDefault("turnId"),
      ExecutionSessionId = source.ExecutionSession?.Id ?? links.GetValueOrDefault("executionSessionId"),
      Provider = error?.Provider,
      Model = source.SelectedModel ?? error?.Model,
      Coordinator = source.ExecutionSession?.CoordinatorModel ?? Detail(details, "coordinator"),
      ExecutionPath = source.ExecutionSession?.ExecutionPath ?? Detail(details, "executionPath"),
      Tool = source.LocalAction?.Tool,
      OriginalTool = source.LocalAction?.OriginalTool,
      ActionId = source.LocalAction?.ActionId,
      RetryCount = ParseInt(Detail(details, "retryCount")),
      Completed = source.Type == "response.completed",
      ReviewAvailable = source.ExecutionSession?.ReviewAvailable,
      ContextFit = contextFit
    };
  }

  private static IncidentContextFit? CreateContextFit(
    IReadOnlyDictionary<string, string?>? details,
    ContextUsageView? usage
  )
  {
    if (details is null && usage is null)
    {
      return null;
    }

    var value = new IncidentContextFit(
      ParseInt(Detail(details, "estimatedInputTokens")),
      ParseInt(Detail(details, "reservedOutputTokens")),
      ParseInt(Detail(details, "requiredContextTokens")),
      ParseInt(Detail(details, "maximumContextTokens")),
      usage?.ConfiguredProviderLimit
    );
    return value.EstimatedInputTokens is null
      && value.ReservedOutputTokens is null
      && value.RequiredContextTokens is null
      && value.MaximumContextTokens is null
      && value.EffectiveContextTokens is null
        ? null
        : value;
  }

  private static string? Detail(IReadOnlyDictionary<string, string?>? details, string name)
  {
    return details?.TryGetValue(name, out var value) == true ? SafeIdentifier(value) : null;
  }

  private static int? ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

  private static string? SafeIdentifier(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/').Take(256).ToArray());
    return string.IsNullOrWhiteSpace(safe) ? null : safe;
  }

  private static string CategoryFor(string type)
  {
    if (type == "error") return "failure";
    if (type.Contains("tool", StringComparison.OrdinalIgnoreCase) || type.Contains("execution", StringComparison.OrdinalIgnoreCase)) return "execution";
    if (type.Contains("recover", StringComparison.OrdinalIgnoreCase) || type.Contains("handoff", StringComparison.OrdinalIgnoreCase)) return "recovery";
    if (type.Contains("context", StringComparison.OrdinalIgnoreCase)) return "context";
    if (type.Contains("session", StringComparison.OrdinalIgnoreCase)) return "persistence";
    return "coordination";
  }

  private static string SummaryFor(string type) => type switch
  {
    "response.completed" => "The request completed and produced a reviewable terminal state.",
    "request.cancelled" => "The request was cancelled.",
    "session-created" => "A local conversation session was created.",
    "session-persisted" => "A local conversation checkpoint was persisted.",
    _ => $"Host milestone: {SafeIdentifier(type) ?? "event"}."
  };
}
