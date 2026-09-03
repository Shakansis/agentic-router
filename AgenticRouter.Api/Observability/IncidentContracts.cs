using System.Text.Json.Serialization;

namespace AgenticRouter.Api.Observability;

public sealed record IncidentContextFit(
  int? EstimatedInputTokens,
  int? ReservedOutputTokens,
  int? RequiredContextTokens,
  int? MaximumContextTokens,
  int? EffectiveContextTokens = null
);

public sealed record IncidentEvent
{
  public int SchemaVersion { get; init; } = 1;

  public string EventId { get; init; } = string.Empty;

  public string TraceId { get; init; } = string.Empty;

  public long Sequence { get; init; }

  public DateTimeOffset TimestampUtc { get; init; }

  public string Category { get; init; } = string.Empty;

  public string Stage { get; init; } = string.Empty;

  public string Code { get; init; } = string.Empty;

  public string Status { get; init; } = string.Empty;

  public string Summary { get; init; } = string.Empty;

  public string? RequestId { get; init; }

  public string? ConversationId { get; init; }

  public string? TurnId { get; init; }

  public string? ExecutionSessionId { get; init; }

  public string? ProviderAttemptId { get; init; }

  public string? ActionId { get; init; }

  public string? Provider { get; init; }

  public string? Model { get; init; }

  public string? Coordinator { get; init; }

  public string? ExecutionPath { get; init; }

  public string? Tool { get; init; }

  public string? OriginalTool { get; init; }

  public int? RetryCount { get; init; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public long? RequestElapsedMilliseconds { get; init; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? SupervisionRunId { get; init; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? Role { get; init; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? ContextId { get; init; }

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? WorkItemId { get; init; }

  public bool? Completed { get; init; }

  public bool? ReviewAvailable { get; init; }

  public IncidentContextFit? ContextFit { get; init; }
}

public sealed record IncidentAppendResult(
  bool Persisted,
  string? EventId = null,
  string? FailureCode = null
);

public sealed record IncidentJournalMetrics(
  DateTimeOffset ObservedAt,
  long AppendAttempts,
  long PersistedEvents,
  long RejectedEvents,
  long QueueWaitMilliseconds,
  long WriteMilliseconds,
  long TraceIndexRebuilds,
  long TraceIndexFilesScanned,
  long TraceIndexRecordsScanned,
  long TraceIndexMilliseconds,
  long LookupCount,
  long LookupFilesScanned,
  long LookupRecordsScanned,
  long LookupMilliseconds,
  long RetentionEvaluations,
  long RetentionFilesDeleted,
  long RetentionMilliseconds,
  int IndexedTraceCount,
  long RetainedBytes
);

public sealed record IncidentTraceReport(
  string TraceId,
  string Status,
  string? FailureCode,
  string? FailureStage,
  string? Provider,
  string? Model,
  string? Coordinator,
  string? ExecutionPath,
  IncidentContextFit? ContextFit,
  bool Completed,
  bool ReviewAvailable,
  bool Truncated,
  int TotalEvents,
  int ReturnedEvents,
  IReadOnlyList<IncidentEvent> Events,
  string Recommendation,
  int MalformedRecordCount = 0,
  IncidentJournalMetrics? JournalMetrics = null
);

public interface IIncidentJournal
{
  Task<IncidentAppendResult> AppendAsync(IncidentEvent incident, CancellationToken cancellationToken);

  Task<IncidentTraceReport?> FindTraceAsync(string traceId, CancellationToken cancellationToken);
}
