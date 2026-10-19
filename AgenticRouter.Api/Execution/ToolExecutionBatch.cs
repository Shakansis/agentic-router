namespace AgenticRouter.Api.Execution;

// One provider inference; order and identity survive validation and execution.
// Concurrency is a Host scheduling decision, independent of provider capability.
public sealed record ToolExecutionBatch(
  string Id,
  IReadOnlyList<LocalActionPlanningResult> Calls
);

// Persistence contains Host metadata and hashes, never raw arguments or file contents.
public sealed record NativeToolCallSnapshot(
  string CallId, string Tool, int Position, string ArgumentsSha256, string Status,
  string? ActionId = null, string? Code = null, long? DurationMilliseconds = null,
  long? AdmissionWaitMilliseconds = null, string? SchedulingReason = null
);

public sealed record NativeToolBatchSnapshot(
  string Id, int InferenceSequence, DateTimeOffset ReceivedAt,
  IReadOnlyList<NativeToolCallSnapshot> Calls,
  int PeakConcurrency = 0, DateTimeOffset? CompletedAt = null,
  long ResourceWaitMilliseconds = 0
);
