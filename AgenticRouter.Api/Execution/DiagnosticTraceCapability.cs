using System.Text.Json;
using AgenticRouter.Api.Observability;

namespace AgenticRouter.Api.Execution;

public static class DiagnosticTraceCapability
{
  public const string ToolName = "get_trace_diagnostic";

  private static readonly JsonSerializerOptions ReportJsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true
  };

  private static readonly JsonSerializerOptions CompactJsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
  };

  public static CanonicalToolDefinition ToolDefinition { get; } = new(
    ToolName,
    "Read one exact bounded, sanitized Agentic Router diagnostic trace. This capability cannot list traces or read application log files.",
    JsonSerializer.SerializeToElement(
      new
      {
        type = "object",
        properties = new
        {
          traceId = new
          {
            type = "string",
            description = "The exact trace identifier supplied by the user or Host."
          }
        },
        required = new[] { "traceId" },
        additionalProperties = false
      }
    )
  );

  public static string ReadTraceId(JsonElement arguments)
  {
    if (
      arguments.ValueKind != JsonValueKind.Object
      || !arguments.TryGetProperty("traceId", out var traceElement)
      || traceElement.ValueKind != JsonValueKind.String
      || string.IsNullOrWhiteSpace(traceElement.GetString())
    )
    {
      throw new LocalActionException(
        "diagnostic-trace-id-invalid",
        "get_trace_diagnostic requires one exact non-empty traceId."
      );
    }

    var traceId = traceElement.GetString()!.Trim();
    try
    {
      JsonlIncidentJournal.ValidateTraceId(traceId);
    }
    catch (ArgumentException exception)
    {
      throw new LocalActionException(
        "diagnostic-trace-id-invalid",
        "The diagnostic trace identifier is invalid.",
        exception
      );
    }
    return traceId;
  }

  public static string Serialize(IncidentTraceReport report)
  {
    return JsonSerializer.Serialize(report, ReportJsonOptions);
  }

  public static string CompactForModel(
    string serializedReport,
    int maximumCharacters
  )
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(serializedReport);
    maximumCharacters = Math.Max(2_048, maximumCharacters);
    if (serializedReport.Length <= maximumCharacters)
    {
      return serializedReport;
    }

    var report = JsonSerializer.Deserialize<IncidentTraceReport>(
      serializedReport,
      ReportJsonOptions
    ) ?? throw new InvalidOperationException(
      "The diagnostic trace report could not be deserialized for model compaction."
    );
    var selectedEvents = SelectModelEvents(report.Events).ToList();
    var stageSummary = report.Events
      .GroupBy(
        item => new
        {
          item.Stage,
          item.Code,
          item.Status
        }
      )
      .Select(group => new ModelTraceStageSummary(
        group.Key.Stage,
        group.Key.Code,
        group.Key.Status,
        group.Count(),
        group.Min(item => item.Sequence),
        group.Max(item => item.Sequence)
      ))
      .OrderByDescending(item => item.Count)
      .ThenBy(item => item.FirstSequence)
      .Take(64)
      .ToList();

    string SerializeCompact() => JsonSerializer.Serialize(
      new ModelTraceReport(
        report.TraceId,
        report.Status,
        report.FailureCode,
        report.FailureStage,
        report.Provider,
        report.Model,
        report.Coordinator,
        report.ExecutionPath,
        report.ContextFit,
        report.Completed,
        report.ReviewAvailable,
        true,
        report.TotalEvents,
        selectedEvents.Count,
        report.TotalEvents - selectedEvents.Count,
        selectedEvents,
        stageSummary,
        report.Recommendation,
        report.MalformedRecordCount
      ),
      CompactJsonOptions
    );

    var compact = SerializeCompact();
    while (compact.Length > maximumCharacters && selectedEvents.Count > 2)
    {
      var removable = selectedEvents.FindIndex(
        item => !IsCritical(item)
      );
      selectedEvents.RemoveAt(removable >= 0 ? removable : 1);
      compact = SerializeCompact();
    }
    while (compact.Length > maximumCharacters && stageSummary.Count > 0)
    {
      stageSummary.RemoveAt(stageSummary.Count - 1);
      compact = SerializeCompact();
    }

    return compact;
  }

  private static IReadOnlyList<ModelTraceEvent> SelectModelEvents(
    IReadOnlyList<IncidentEvent> events
  )
  {
    return events
      .Where((item, index) =>
        index < 8
        || index >= events.Count - 8
        || IsCritical(item)
      )
      .GroupBy(item => item.Sequence)
      .Select(group => group.First())
      .OrderBy(item => item.Sequence)
      .Select(item => new ModelTraceEvent(
        item.Sequence,
        item.TimestampUtc,
        item.Category,
        item.Stage,
        item.Code,
        item.Status,
        item.Summary,
        item.Provider,
        item.Model,
        item.Coordinator,
        item.ExecutionPath,
        item.Tool,
        item.RetryCount,
        item.RequestElapsedMilliseconds,
        item.Completed,
        item.ReviewAvailable,
        item.ContextFit
      ))
      .ToArray();
  }

  private static bool IsCritical(IncidentEvent item)
  {
    return item.Status is "failure" or "failed"
      || item.Completed == true
      || item.ReviewAvailable == true
      || item.ContextFit is not null
      || item.Stage.StartsWith("action.", StringComparison.Ordinal)
      || item.Stage.StartsWith("request.slow-", StringComparison.Ordinal)
      || item.Stage.StartsWith("response.", StringComparison.Ordinal);
  }

  private static bool IsCritical(ModelTraceEvent item)
  {
    return item.Status is "failure" or "failed"
      || item.Completed == true
      || item.ReviewAvailable == true
      || item.ContextFit is not null
      || item.Stage.StartsWith("action.", StringComparison.Ordinal)
      || item.Stage.StartsWith("request.slow-", StringComparison.Ordinal)
      || item.Stage.StartsWith("response.", StringComparison.Ordinal);
  }

  private sealed record ModelTraceReport(
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
    int OmittedEvents,
    IReadOnlyList<ModelTraceEvent> Events,
    IReadOnlyList<ModelTraceStageSummary> StageSummary,
    string Recommendation,
    int MalformedRecordCount
  );

  private sealed record ModelTraceEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string Category,
    string Stage,
    string Code,
    string Status,
    string Summary,
    string? Provider,
    string? Model,
    string? Coordinator,
    string? ExecutionPath,
    string? Tool,
    int? RetryCount,
    long? RequestElapsedMilliseconds,
    bool? Completed,
    bool? ReviewAvailable,
    IncidentContextFit? ContextFit
  );

  private sealed record ModelTraceStageSummary(
    string Stage,
    string Code,
    string Status,
    int Count,
    long FirstSequence,
    long LastSequence
  );
}
