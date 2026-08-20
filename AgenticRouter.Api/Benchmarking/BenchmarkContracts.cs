using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.Benchmarking;

public static class BenchmarkIds
{
  public const string FileSystemCreate001 = "FS-CREATE-001";
}

public static class BenchmarkHarnessCapabilityIds
{
  public const string FileCreation = "file-creation";
}

public static class BenchmarkExecutionStatusIds
{
  public const string Completed = "completed";
  public const string Failed = "failed";
  public const string Cancelled = "cancelled";
  public const string TimedOut = "timed-out";
  public const string Unavailable = "unavailable";
}

public static class BenchmarkResultStatusIds
{
  public const string Pass = "PASS";
  public const string Fail = "FAIL";
  public const string Error = "ERROR";
}

public sealed record BenchmarkTestMetadata(
  string Id,
  int Version,
  string Name,
  string Suite,
  string Description,
  bool Deterministic,
  IReadOnlyList<string> RequiredHarnessCapabilities
);

public sealed record BenchmarkRunRequest(
  string TestId,
  int TestVersion,
  string Model,
  string Harness,
  bool ModelExecutionPermissionGranted = false
);

public sealed record BenchmarkRun(
  string RunId,
  string TestId,
  int TestVersion,
  string Model,
  string? ModelDigest,
  string Provider,
  string Harness,
  string? HarnessVersion,
  string WorkspaceId,
  string WorkspacePath,
  DateTimeOffset StartedAt,
  DateTimeOffset EndedAt,
  string ExecutionStatus
);

public sealed record BenchmarkError(
  string Code,
  string Message,
  string Stage,
  bool Recoverable
);

public sealed record BenchmarkRawResult(
  string Status,
  bool ObjectiveAchieved,
  int ByteAccuracy,
  int DirectoryAccuracy,
  int FilenameAccuracy,
  int ContainmentAccuracy,
  IReadOnlyList<string> UnexpectedCreatedFiles,
  IReadOnlyList<string> UnexpectedModifiedFiles,
  IReadOnlyList<string> UnexpectedDeletedFiles,
  string ExecutionStatus,
  BenchmarkError? Error = null,
  long? InputTokens = null,
  long? OutputTokens = null
);

public sealed record BenchmarkRunResult(
  BenchmarkRun Run,
  BenchmarkRawResult RawResult,
  bool WorkspaceCleanedUp
);

public sealed record BenchmarkValidationContext(
  string WorkspacePath,
  BenchmarkWorkspaceSnapshot InitialSnapshot,
  BenchmarkWorkspaceSnapshot FinalSnapshot,
  string ExecutionStatus,
  BenchmarkError? ExecutionError
);

public interface IBenchmarkTestDefinition
{
  BenchmarkTestMetadata Metadata { get; }

  Task PrepareFixtureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  );

  string CreateTask();

  Task<BenchmarkRawResult> ValidateAsync(
    BenchmarkValidationContext context,
    CancellationToken cancellationToken
  );
}

public interface IBenchmarkTestRegistry
{
  bool TryGet(
    string testId,
    int version,
    out IBenchmarkTestDefinition definition
  );
}

public sealed class BenchmarkTestRegistry : IBenchmarkTestRegistry
{
  private readonly IReadOnlyDictionary<(string Id, int Version), IBenchmarkTestDefinition> _tests;

  public BenchmarkTestRegistry(
    IEnumerable<IBenchmarkTestDefinition> tests
  )
  {
    var definitions = new Dictionary<(string Id, int Version), IBenchmarkTestDefinition>();
    foreach (var test in tests)
    {
      var key = (test.Metadata.Id.ToUpperInvariant(), test.Metadata.Version);
      if (!definitions.TryAdd(key, test))
      {
        throw new InvalidOperationException(
          $"Duplicate benchmark test '{test.Metadata.Id}' version {test.Metadata.Version}."
        );
      }
    }
    _tests = definitions;
  }

  public bool TryGet(
    string testId,
    int version,
    out IBenchmarkTestDefinition definition
  )
  {
    return _tests.TryGetValue(
      (testId.Trim().ToUpperInvariant(), version),
      out definition!
    );
  }
}

public sealed class BenchmarkRequestException : Exception
{
  public BenchmarkRequestException(
    string code,
    string message,
    string field
  ) : base(message)
  {
    Code = code;
    Field = field;
  }

  public string Code { get; }

  public string Field { get; }
}

public sealed record BenchmarkHarnessOutcome(
  string ExecutionStatus,
  BenchmarkError? Error
)
{
  public static BenchmarkHarnessOutcome FromTerminal(
    HarnessEvent terminal
  )
  {
    var status = terminal.TerminalState switch
    {
      HarnessTerminalState.Completed => BenchmarkExecutionStatusIds.Completed,
      HarnessTerminalState.Cancelled => BenchmarkExecutionStatusIds.Cancelled,
      HarnessTerminalState.TimedOut => BenchmarkExecutionStatusIds.TimedOut,
      HarnessTerminalState.Unavailable => BenchmarkExecutionStatusIds.Unavailable,
      _ => BenchmarkExecutionStatusIds.Failed
    };
    var error = status == BenchmarkExecutionStatusIds.Completed
      ? null
      : new BenchmarkError(
        terminal.ErrorCode ?? "harness-execution-failed",
        terminal.Message ?? "The harness did not complete the benchmark task.",
        "harness-execution",
        status is BenchmarkExecutionStatusIds.TimedOut
          or BenchmarkExecutionStatusIds.Unavailable
      );
    return new BenchmarkHarnessOutcome(status, error);
  }
}
