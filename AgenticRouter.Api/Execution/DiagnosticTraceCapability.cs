using System.Text.Json;
using AgenticRouter.Api.Observability;

namespace AgenticRouter.Api.Execution;

public static class DiagnosticTraceCapability
{
  public const string ToolName = "get_trace_diagnostic";

  private static readonly JsonSerializerOptions ReportJsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
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
}
