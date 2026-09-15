namespace AgenticRouter.Api.Benchmarking;

public sealed class ManualPromptBenchmark : IBenchmarkTestDefinition
{
  private readonly string _prompt;

  public ManualPromptBenchmark(string prompt)
  {
    _prompt = prompt;
  }

  public BenchmarkTestMetadata Metadata { get; } = new(
    BenchmarkIds.ManualCustomPrompt001,
    1,
    "Manual / Custom Prompt",
    BenchmarkSuiteIds.Manual,
    "Runs the exact user prompt through production Execute and records technical health for human review.",
    false,
    [],
    SuiteVersion: BenchmarkSuiteIds.ManualVersion,
    FixtureId: BenchmarkSuiteIds.ManualFixtureId,
    FixtureVersion: BenchmarkSuiteIds.ManualFixtureVersion,
    TimeoutSeconds: 1600,
    TurnBudget: 1
  );

  public Task PrepareFixtureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    _ = workspacePath;
    cancellationToken.ThrowIfCancellationRequested();
    return Task.CompletedTask;
  }

  public string CreateTask() => _prompt;

  public Task<BenchmarkRawResult> ValidateAsync(
    BenchmarkValidationContext context,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    var evidence = context.HarnessEvidence;
    var completed = string.Equals(
      context.ExecutionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    ) && context.ExecutionError is null;
    var changed = ChangedPaths(context.InitialSnapshot, context.FinalSnapshot);
    var raw = new BenchmarkRawResult(
      completed ? BenchmarkResultStatusIds.Pass : BenchmarkResultStatusIds.Error,
      completed,
      0,
      0,
      0,
      100,
      [],
      [],
      [],
      context.ExecutionStatus,
      context.ExecutionError,
      evidence?.InputTokens,
      evidence?.OutputTokens,
      ToolCallCount: evidence?.ToolCallCount,
      SurfacedErrorCount: evidence?.SurfacedErrorCount,
      RecoveredErrorCount: evidence?.RecoveredErrorCount,
      ChangedFiles: changed,
      UnexpectedFiles: [],
      HostValidationResult: completed ? "technical-pass" : "technical-failure",
      FinalHarnessReport: evidence?.FinalReport ?? string.Empty,
      ValidationFacts: new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["technicalExecution"] = completed ? "completed" : "failed",
        ["qualityEvaluation"] = completed ? "awaiting-user-review" : "not-required"
      },
      Turns: evidence?.Turns,
      HostEvents: evidence?.HostEvents,
      ToolCalls: evidence?.ToolCalls,
      OperationalDiagnostics: evidence?.OperationalDiagnostics
    );
    return Task.FromResult(raw);
  }

  private static IReadOnlyList<string> ChangedPaths(
    BenchmarkWorkspaceSnapshot initial,
    BenchmarkWorkspaceSnapshot final
  )
  {
    return initial.Entries.Keys.Concat(final.Entries.Keys)
      .Distinct(BenchmarkWorkspaceFactory.PathComparer)
      .Where(path => !initial.Entries.TryGetValue(path, out var before)
        || !final.Entries.TryGetValue(path, out var after)
        || before != after)
      .OrderBy(path => path, StringComparer.Ordinal)
      .ToArray();
  }
}
