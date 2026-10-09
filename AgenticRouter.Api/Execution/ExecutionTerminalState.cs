using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

internal static class ExecutionTerminalState
{
  public static string From(ChatStreamEvent item) => item.Type switch
  {
    "request.cancelled" => "cancelled",
    "error" => "failed",
    "response.completed" when item.ExecutionSession?.State == "blocked"
      || item.ExecutionSession?.CompletionStatus?.StartsWith("blocked-", StringComparison.Ordinal) == true => "blocked",
    "response.completed" when item.ExecutionSession?.CompletionStatus == "partial-context-exhausted" => "partial",
    "response.completed" => "completed",
    _ => "observed"
  };
}
