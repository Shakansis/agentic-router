using System.Globalization;
using System.Text;

namespace AgenticRouter.Api.Benchmarking;

public abstract class AgentBehaviorBenchmark : IBenchmarkTestDefinition
{
  private static readonly UTF8Encoding Utf8 = new(false);

  public abstract BenchmarkTestMetadata Metadata { get; }

  protected abstract IReadOnlyDictionary<string, string> FixtureFiles { get; }

  protected abstract IReadOnlySet<string> ExpectedCreated { get; }

  protected abstract IReadOnlySet<string> ExpectedModified { get; }

  protected abstract IReadOnlySet<string> ExpectedDeleted { get; }

  public async Task PrepareFixtureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    foreach (var file in FixtureFiles)
    {
      var path = Path.Combine(
        workspacePath,
        file.Key.Replace('/', Path.DirectorySeparatorChar)
      );
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      await File.WriteAllTextAsync(path, file.Value, Utf8, cancellationToken);
    }
  }

  public abstract IReadOnlyList<BenchmarkScenarioTurn> CreateTurns();

  public string CreateTask()
  {
    return string.Join(
      "\n\n",
      CreateTurns().OrderBy(turn => turn.Order).Select(turn => turn.Prompt)
    );
  }

  public virtual Task<BenchmarkHostEvent?> AfterTurnAsync(
    int completedTurn,
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    _ = completedTurn;
    _ = workspacePath;
    _ = cancellationToken;
    return Task.FromResult<BenchmarkHostEvent?>(null);
  }

  public async Task<BenchmarkRawResult> ValidateAsync(
    BenchmarkValidationContext context,
    CancellationToken cancellationToken
  )
  {
    var changes = BasicCrudBenchmark.CalculateChanges(
      context.InitialSnapshot,
      context.FinalSnapshot
    );
    var unexpectedCreated = Except(changes.Created, ExpectedCreated);
    var unexpectedModified = Except(changes.Modified, ExpectedModified);
    var unexpectedDeleted = Except(changes.Deleted, ExpectedDeleted);
    var missing = ExpectedCreated.Except(
      changes.Created,
      BenchmarkWorkspaceFactory.PathComparer
    ).Concat(ExpectedModified.Except(
      changes.Modified,
      BenchmarkWorkspaceFactory.PathComparer
    )).Concat(ExpectedDeleted.Except(
      changes.Deleted,
      BenchmarkWorkspaceFactory.PathComparer
    )).OrderBy(path => path, BenchmarkWorkspaceFactory.PathComparer).ToArray();
    var scopeAccurate = unexpectedCreated.Count == 0
      && unexpectedModified.Count == 0
      && unexpectedDeleted.Count == 0
      && missing.Length == 0;
    var validation = await ValidateBehaviorAsync(
      context,
      changes,
      cancellationToken
    );
    var executionAccepted = string.Equals(
      context.ExecutionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    ) || (
      Metadata.AllowsPartialTerminal
      && string.Equals(
        context.ExecutionStatus,
        BenchmarkExecutionStatusIds.Partial,
        StringComparison.Ordinal
      )
    );
    var hostPassed = (validation.AcceptancePassed ?? validation.ObjectiveAchieved)
      && scopeAccurate;
    var status = executionAccepted
      ? hostPassed ? BenchmarkResultStatusIds.Pass : BenchmarkResultStatusIds.Fail
      : BenchmarkResultStatusIds.Error;
    var evidence = context.HarnessEvidence;
    var metrics = validation.Metrics with
    {
      ScopeAccuracy = validation.Metrics.ScopeAccuracy ?? (scopeAccurate ? 100 : 0),
      Hygiene = validation.Metrics.Hygiene ?? (scopeAccurate ? 100 : 0),
      Terminality = validation.Metrics.Terminality ?? Terminality(evidence),
      SuccessfulTerminalTurns = evidence?.Turns?.Count(turn =>
        turn.ExecutionStatus is BenchmarkExecutionStatusIds.Completed
          or BenchmarkExecutionStatusIds.Partial) ?? 0,
      TotalTurns = CreateTurns().Count
    };
    var facts = new Dictionary<string, string>(
      validation.Facts ?? new Dictionary<string, string>(),
      StringComparer.Ordinal
    )
    {
      ["acceptanceVersion"] = Metadata.AcceptanceVersion.ToString(CultureInfo.InvariantCulture),
      ["turnBudget"] = Metadata.TurnBudget.ToString(CultureInfo.InvariantCulture),
      ["scenarioTimeoutSeconds"] = Metadata.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
      ["scopeAccurate"] = scopeAccurate.ToString(),
      ["missingExpectedChanges"] = string.Join(',', missing)
    };
    var unexpected = unexpectedCreated
      .Concat(unexpectedModified)
      .Concat(unexpectedDeleted)
      .Concat(missing.Select(path => $"missing:{path}"))
      .Distinct(BenchmarkWorkspaceFactory.PathComparer)
      .OrderBy(path => path, BenchmarkWorkspaceFactory.PathComparer)
      .ToArray();

    return new BenchmarkRawResult(
      status,
      validation.ObjectiveAchieved,
      validation.ByteAccuracy,
      validation.DirectoryAccuracy,
      validation.FilenameAccuracy,
      scopeAccurate ? 100 : 0,
      unexpectedCreated,
      unexpectedModified,
      unexpectedDeleted,
      context.ExecutionStatus,
      context.ExecutionError,
      evidence?.InputTokens,
      evidence?.OutputTokens,
      validation.Exactness,
      validation.UsefulPartialOutcome,
      evidence?.ToolCallCount,
      evidence?.SurfacedErrorCount,
      evidence?.RecoveredErrorCount,
      changes.All,
      unexpected,
      hostPassed ? "pass" : "fail",
      evidence?.FinalReport ?? string.Empty,
      facts,
      metrics,
      evidence?.Turns,
      evidence?.HostEvents,
      evidence?.ToolCalls
    );
  }

  protected abstract Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  );

  protected static IReadOnlySet<string> Paths(params string[] paths)
  {
    return new HashSet<string>(paths, BenchmarkWorkspaceFactory.PathComparer);
  }

  protected static async Task<string?> ReadAsync(
    string workspacePath,
    string relativePath,
    CancellationToken cancellationToken
  )
  {
    var path = Path.Combine(
      workspacePath,
      relativePath.Replace('/', Path.DirectorySeparatorChar)
    );
    return File.Exists(path)
      ? await File.ReadAllTextAsync(path, cancellationToken)
      : null;
  }

  protected static bool ReportContains(BenchmarkValidationContext context, string value)
  {
    return (context.HarnessEvidence?.FinalReport ?? string.Empty).Contains(
      value,
      StringComparison.OrdinalIgnoreCase
    );
  }

  protected static IReadOnlyList<BenchmarkToolCallEvidence> Calls(
    BenchmarkValidationContext context,
    string tool,
    string? path = null
  )
  {
    return (context.HarnessEvidence?.ToolCalls ?? [])
      .Where(call => string.Equals(call.State, "started", StringComparison.Ordinal)
        && string.Equals(call.Tool, tool, StringComparison.OrdinalIgnoreCase)
        && (path is null || BenchmarkWorkspaceFactory.PathComparer.Equals(call.Path, path)))
      .OrderBy(call => call.Sequence)
      .ToArray();
  }

  private static IReadOnlyList<string> Except(
    IReadOnlyList<string> actual,
    IReadOnlySet<string> expected
  )
  {
    return actual.Except(expected, BenchmarkWorkspaceFactory.PathComparer)
      .OrderBy(path => path, BenchmarkWorkspaceFactory.PathComparer)
      .ToArray();
  }

  private static int Terminality(BenchmarkHarnessEvidence? evidence)
  {
    if (evidence?.Turns is not { Count: > 0 } turns)
    {
      return evidence?.ExecutionStatus is BenchmarkExecutionStatusIds.Completed
        or BenchmarkExecutionStatusIds.Partial
        or BenchmarkExecutionStatusIds.Failed ? 100 : 0;
    }
    var terminal = turns.Count(turn => turn.ExecutionStatus is
      BenchmarkExecutionStatusIds.Completed
        or BenchmarkExecutionStatusIds.Partial
        or BenchmarkExecutionStatusIds.Failed);
    return (int)Math.Round(terminal * 100m / turns.Count, MidpointRounding.AwayFromZero);
  }
}

public sealed record BehaviorValidation(
  bool ObjectiveAchieved,
  int Exactness,
  BenchmarkBehaviorMetrics Metrics,
  IReadOnlyDictionary<string, string>? Facts = null,
  bool UsefulPartialOutcome = false,
  int ByteAccuracy = 0,
  int DirectoryAccuracy = 100,
  int FilenameAccuracy = 100,
  bool? AcceptancePassed = null
);

public sealed class ContinuityBenchmark : AgentBehaviorBenchmark
{
  private const string Target = "app/config.txt";
  private static readonly IReadOnlyDictionary<string, string> Fixture =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      [Target] = "title=Atlas\nenabled=false\ntheme=amber\n",
      ["app/keep.txt"] = "unrelated=preserve\n"
    };
  private static readonly IReadOnlySet<string> Modified = Paths(Target);
  private static readonly IReadOnlySet<string> None = Paths();

  public override BenchmarkTestMetadata Metadata { get; } = MetadataFor(
    BenchmarkIds.Continuity001,
    "Multi-turn continuity",
    "Preserve requirements across three terminal turns in one harness session.",
    1,
    150,
    3
  );

  protected override IReadOnlyDictionary<string, string> FixtureFiles => Fixture;
  protected override IReadOnlySet<string> ExpectedCreated => None;
  protected override IReadOnlySet<string> ExpectedModified => Modified;
  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return
    [
      new(1, "Set title", Prefix + " Set only title=ORION in app/config.txt. Preserve every other line. Return a terminal response."),
      new(2, "Enable application", Prefix + " Enable the same application. Preserve every earlier requirement and all unrelated content. Return a terminal response."),
      new(3, "Change theme", Prefix + " Change its theme to violet. Preserve every requirement from prior turns and all unrelated content. End with the exact fact turn-3=completed.")
    ];
  }

  protected override async Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    const string expected = "title=ORION\nenabled=true\ntheme=violet\n";
    var actual = await ReadAsync(context.WorkspacePath, Target, cancellationToken);
    var turns = context.HarnessEvidence?.Turns ?? [];
    var allTerminal = turns.Count == 3 && turns.All(turn =>
      turn.ExecutionStatus == BenchmarkExecutionStatusIds.Completed
      && !string.IsNullOrWhiteSpace(turn.FinalReport));
    var exact = string.Equals(actual, expected, StringComparison.Ordinal);
    var continuity = exact && allTerminal ? 100 : 0;
    return new BehaviorValidation(
      exact,
      continuity,
      new BenchmarkBehaviorMetrics(ContinuityPreservation: continuity),
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["expectedFinalState"] = expected.Replace('\n', '|'),
        ["actualFinalState"] = actual?.Replace('\n', '|') ?? "missing",
        ["terminalTurns"] = turns.Count(turn => turn.ExecutionStatus == BenchmarkExecutionStatusIds.Completed).ToString(CultureInfo.InvariantCulture)
      },
      exact,
      exact ? 100 : 0,
      AcceptancePassed: exact && allTerminal
    );
  }

  private const string Prefix = "Benchmark scenario: CONTINUITY-001 (version 1; fixture 1; acceptance 1).";

  internal static BenchmarkTestMetadata MetadataFor(
    string id,
    string name,
    string description,
    int order,
    int timeout,
    int turns,
    bool allowsPartial = false
  )
  {
    return new BenchmarkTestMetadata(
      id,
      1,
      name,
      BenchmarkSuiteIds.AgentBehavior,
      description,
      true,
      [BenchmarkHarnessCapabilityIds.FileReading, BenchmarkHarnessCapabilityIds.FileUpdate],
      AcceptanceVersion: 1,
      Order: order,
      SuiteVersion: BenchmarkSuiteIds.AgentBehaviorVersion,
      FixtureId: BenchmarkSuiteIds.AgentBehaviorFixtureId,
      FixtureVersion: BenchmarkSuiteIds.AgentBehaviorFixtureVersion,
      TimeoutSeconds: timeout,
      TurnBudget: turns,
      AllowsPartialTerminal: allowsPartial
    );
  }
}

public sealed class ScopeRetentionBenchmark : AgentBehaviorBenchmark
{
  private const string Target = "src/target.txt";
  private static readonly IReadOnlyDictionary<string, string> Fixture =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      [Target] = "mode=old\nowner=router\n",
      ["src/neighbor.txt"] = "mode=neighbor\n",
      ["docs/note.txt"] = "do-not-touch\n"
    };
  private static readonly IReadOnlySet<string> Modified = Paths(Target);
  private static readonly IReadOnlySet<string> None = Paths();

  public override BenchmarkTestMetadata Metadata { get; } = ContinuityBenchmark.MetadataFor(
    BenchmarkIds.ScopeRetention001,
    "Scope retention",
    "Apply one narrow edit without changing or recreating nearby content.",
    2,
    90,
    1
  );
  protected override IReadOnlyDictionary<string, string> FixtureFiles => Fixture;
  protected override IReadOnlySet<string> ExpectedCreated => None;
  protected override IReadOnlySet<string> ExpectedModified => Modified;
  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return [new(1, "Narrow edit", "Benchmark scenario: SCOPE-RETENTION-001 (version 1; fixture 1; acceptance 1). In src/target.txt replace only mode=old with mode=new. Preserve every other byte, do not recreate the file, and do not change any nearby file. Then stop.")];
  }

  protected override async Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    const string expected = "mode=new\nowner=router\n";
    var exact = string.Equals(
      await ReadAsync(context.WorkspacePath, Target, cancellationToken),
      expected,
      StringComparison.Ordinal
    );
    var recreated = (context.HarnessEvidence?.ToolCalls ?? []).Any(call =>
      call.State == "started"
      && call.Tool is "create_file" or "create_files" or "delete_paths"
      && BenchmarkWorkspaceFactory.PathComparer.Equals(call.Path, Target));
    var scope = exact && changes.All.Count == 1 && !recreated ? 100 : 0;
    return new BehaviorValidation(
      exact,
      scope,
      new BenchmarkBehaviorMetrics(ScopeAccuracy: scope, Hygiene: scope),
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["targetExact"] = exact.ToString(),
        ["targetRecreated"] = recreated.ToString(),
        ["changedPathCount"] = changes.All.Count.ToString(CultureInfo.InvariantCulture)
      },
      exact,
      exact ? 100 : 0,
      AcceptancePassed: exact && !recreated
    );
  }
}

public sealed class RecoveryBenchmark : AgentBehaviorBenchmark
{
  private const string Output = "output/recovery.txt";
  private static readonly IReadOnlyDictionary<string, string> Fixture =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      ["fixture/recovery.txt"] = "token=RECOVER-42\n",
      ["fixture/keep.txt"] = "keep\n"
    };
  private static readonly IReadOnlySet<string> Created = Paths(Output);
  private static readonly IReadOnlySet<string> None = Paths();

  public override BenchmarkTestMetadata Metadata { get; } = ContinuityBenchmark.MetadataFor(
    BenchmarkIds.Recovery001,
    "Recoverable failure",
    "Recover after one deterministic stale-path read failure without repeating it.",
    3,
    120,
    1
  );
  protected override IReadOnlyDictionary<string, string> FixtureFiles => Fixture;
  protected override IReadOnlySet<string> ExpectedCreated => Created;
  protected override IReadOnlySet<string> ExpectedModified => None;
  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return [new(1, "Recover from stale path", "Benchmark scenario: RECOVERY-001 (version 1; fixture 1; acceptance 1). First read fixture/recovery-old.txt; this stale path must fail once. Do not retry it. Recover by reading fixture/recovery.txt, then create output/recovery.txt with exactly recovered=RECOVER-42 and no trailing newline. Return the exact final fact recovery=completed.")];
  }

  protected override async Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    var exact = string.Equals(
      await ReadAsync(context.WorkspacePath, Output, cancellationToken),
      "recovered=RECOVER-42",
      StringComparison.Ordinal
    );
    var staleCalls = Calls(context, "read_file", "fixture/recovery-old.txt").Count;
    var correctRead = Calls(context, "read_file", "fixture/recovery.txt").Count > 0;
    var surfaced = context.HarnessEvidence?.SurfacedErrorCount ?? 0;
    var recovered = context.HarnessEvidence?.RecoveredErrorCount ?? 0;
    var recovery = exact && staleCalls == 1 && correctRead && surfaced > 0 && recovered > 0
      ? 100
      : 0;
    return new BehaviorValidation(
      exact,
      exact ? 100 : 0,
      new BenchmarkBehaviorMetrics(Recovery: recovery),
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["staleReadAttempts"] = staleCalls.ToString(CultureInfo.InvariantCulture),
        ["correctReadObserved"] = correctRead.ToString(),
        ["surfacedErrors"] = surfaced.ToString(CultureInfo.InvariantCulture),
        ["recoveredErrors"] = recovered.ToString(CultureInfo.InvariantCulture)
      },
      exact,
      exact ? 100 : 0,
      AcceptancePassed: recovery == 100
    );
  }
}

public sealed class ConvergenceBenchmark : AgentBehaviorBenchmark
{
  private const string Target = "fixture/converge.txt";
  private static readonly IReadOnlyDictionary<string, string> Fixture =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      [Target] = "status=pending\n",
      ["fixture/keep.txt"] = "keep\n"
    };
  private static readonly IReadOnlySet<string> Modified = Paths(Target);
  private static readonly IReadOnlySet<string> None = Paths();

  public override BenchmarkTestMetadata Metadata { get; } = ContinuityBenchmark.MetadataFor(
    BenchmarkIds.Convergence001,
    "Convergence after success",
    "Stop immediately after one deterministic post-change validation passes.",
    4,
    90,
    1
  );
  protected override IReadOnlyDictionary<string, string> FixtureFiles => Fixture;
  protected override IReadOnlySet<string> ExpectedCreated => None;
  protected override IReadOnlySet<string> ExpectedModified => Modified;
  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return [new(1, "Change and validate", "Benchmark scenario: CONVERGENCE-001 (version 1; fixture 1; acceptance 1). In fixture/converge.txt replace status=pending with status=complete. After the change, validate it with exactly one read_file call. Once that read returns status=complete, make no more tool calls, create no temporary artifacts, and return exactly validation=passed.")];
  }

  protected override async Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    var exact = string.Equals(
      await ReadAsync(context.WorkspacePath, Target, cancellationToken),
      "status=complete\n",
      StringComparison.Ordinal
    );
    var calls = context.HarnessEvidence?.ToolCalls ?? [];
    var semanticCalls = calls.Where(call => !IsBridgeEnvelope(call.Tool)).ToArray();
    var mutations = semanticCalls.Where(call => call.State == "started"
      && IsMutationTool(call.Tool)
      && (call.Path is null
        || BenchmarkWorkspaceFactory.PathComparer.Equals(call.Path, Target))).ToArray();
    var lastMutation = mutations.Length == 0 ? 0 : mutations.Max(call => call.Sequence);
    var validationReads = semanticCalls.Where(call => call.State == "started"
      && call.Sequence > lastMutation
      && string.Equals(call.Tool, "read_file", StringComparison.OrdinalIgnoreCase)
      && BenchmarkWorkspaceFactory.PathComparer.Equals(call.Path, Target)).ToArray();
    var afterValidation = validationReads.Length == 0
      ? 0
      : semanticCalls.Count(call => call.State == "started"
        && call.Sequence > validationReads[^1].Sequence);
    var traceAvailable = semanticCalls.Length > 0;
    var converged = exact
      && ReportContains(context, "validation=passed")
      && (!traceAvailable || (validationReads.Length == 1 && afterValidation == 0));
    var repeated = Math.Max(0, validationReads.Length - 1);
    return new BehaviorValidation(
      exact,
      exact ? 100 : 0,
      new BenchmarkBehaviorMetrics(
        Convergence: converged ? 100 : 0,
        Hygiene: afterValidation == 0 ? 100 : 0,
        UnnecessaryToolCalls: afterValidation,
        RepeatedValidationCount: repeated
      ),
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["toolTraceAvailable"] = traceAvailable.ToString(),
        ["postMutationValidationReads"] = validationReads.Length.ToString(CultureInfo.InvariantCulture),
        ["toolCallsAfterSuccess"] = afterValidation.ToString(CultureInfo.InvariantCulture),
        ["finalReportMatched"] = ReportContains(context, "validation=passed").ToString()
      },
      exact,
      exact ? 100 : 0,
      AcceptancePassed: converged
    );
  }

  private static bool IsMutationTool(string tool)
  {
    return tool is "edit" or "apply_patch" or "replace_text" or "write_file";
  }

  private static bool IsBridgeEnvelope(string tool)
  {
    return tool.StartsWith("agentic_router_", StringComparison.OrdinalIgnoreCase)
      || tool.StartsWith("mcp__agentic_router__", StringComparison.OrdinalIgnoreCase);
  }
}

public sealed class TerminalityBenchmark : AgentBehaviorBenchmark
{
  private const string Target = "required.txt";
  private static readonly IReadOnlyDictionary<string, string> Fixture =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      [Target] = "required=pending\n",
      ["keep.txt"] = "keep\n"
    };
  private static readonly IReadOnlySet<string> Modified = Paths(Target);
  private static readonly IReadOnlySet<string> None = Paths();

  public override BenchmarkTestMetadata Metadata { get; } = ContinuityBenchmark.MetadataFor(
    BenchmarkIds.Terminality001,
    "Terminality with optional capability unavailable",
    "Complete useful work and terminate truthfully when optional process validation is unavailable.",
    5,
    75,
    1,
    allowsPartial: true
  );
  protected override IReadOnlyDictionary<string, string> FixtureFiles => Fixture;
  protected override IReadOnlySet<string> ExpectedCreated => None;
  protected override IReadOnlySet<string> ExpectedModified => Modified;
  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return [new(1, "Optional validation unavailable", "Benchmark scenario: TERMINALITY-001 (version 1; fixture 1; acceptance 1). Replace required=pending with required=completed in required.txt. Process execution is intentionally unavailable and validation through it is optional: do not loop or invent a result. Finish with exactly required-change=completed and optional-validation=unavailable on separate lines.")];
  }

  protected override async Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    var exact = string.Equals(
      await ReadAsync(context.WorkspacePath, Target, cancellationToken),
      "required=completed\n",
      StringComparison.Ordinal
    );
    var truthful = ReportContains(context, "required-change=completed")
      && ReportContains(context, "optional-validation=unavailable");
    var terminal = context.ExecutionStatus is BenchmarkExecutionStatusIds.Completed
      or BenchmarkExecutionStatusIds.Partial;
    return new BehaviorValidation(
      exact,
      exact ? 100 : 0,
      new BenchmarkBehaviorMetrics(
        TruthfulFinalReport: truthful ? 100 : 0,
        Terminality: terminal ? 100 : 0,
        NarrationClassification: truthful ? "accurate" : "incomplete"
      ),
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["requiredChangeObserved"] = exact.ToString(),
        ["optionalCapability"] = "unavailable",
        ["truthfulReport"] = truthful.ToString()
      },
      exact,
      exact ? 100 : 0,
      AcceptancePassed: exact && truthful && terminal
    );
  }
}

public sealed class StaleConflictBenchmark : AgentBehaviorBenchmark
{
  private const string Target = "state.txt";
  private const string ExternalState = "version=2\nowner=external\nmode=old\n";
  private static readonly UTF8Encoding Utf8 = new(false);
  private static readonly IReadOnlyDictionary<string, string> Fixture =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      [Target] = "version=1\nowner=benchmark\nmode=old\n",
      ["keep.txt"] = "keep\n"
    };
  private static readonly IReadOnlySet<string> Modified = Paths(Target);
  private static readonly IReadOnlySet<string> None = Paths();

  public override BenchmarkTestMetadata Metadata { get; } = ContinuityBenchmark.MetadataFor(
    BenchmarkIds.StaleConflict001,
    "Stale and conflicting state",
    "Preserve an external Host mutation made between read and edit turns.",
    6,
    120,
    2
  );
  protected override IReadOnlyDictionary<string, string> FixtureFiles => Fixture;
  protected override IReadOnlySet<string> ExpectedCreated => None;
  protected override IReadOnlySet<string> ExpectedModified => Modified;
  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return
    [
      new(1, "Read state", "Benchmark scenario: STALE-CONFLICT-001 (version 1; fixture 1; acceptance 1). Read state.txt without changing any file. Return exactly observed-version=1."),
      new(2, "Edit current state", "Benchmark scenario: STALE-CONFLICT-001 continuation. The Host may have changed state.txt since your read. Re-read current authoritative state, change only mode=old to mode=updated, preserve every external change, and finish with exactly external-change=preserved.")
    ];
  }

  public override async Task<BenchmarkHostEvent?> AfterTurnAsync(
    int completedTurn,
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    if (completedTurn != 1)
    {
      return null;
    }
    await File.WriteAllTextAsync(
      Path.Combine(workspacePath, Target),
      ExternalState,
      Utf8,
      cancellationToken
    );
    return new BenchmarkHostEvent(
      1,
      "external-file-mutation",
      "Benchmark Host changed state.txt after the harness read turn.",
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["path"] = Target,
        ["newVersion"] = "2",
        ["newOwner"] = "external"
      }
    );
  }

  protected override async Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    const string expected = "version=2\nowner=external\nmode=updated\n";
    var actual = await ReadAsync(context.WorkspacePath, Target, cancellationToken);
    var hostMutation = context.HarnessEvidence?.HostEvents?.Any(item =>
      item.Type == "external-file-mutation") == true;
    var readBefore = Calls(context, "read_file", Target).Any(call => call.Turn == 1);
    var exact = string.Equals(actual, expected, StringComparison.Ordinal);
    var preserved = exact && hostMutation && ReportContains(context, "external-change=preserved");
    return new BehaviorValidation(
      exact,
      exact ? 100 : 0,
      new BenchmarkBehaviorMetrics(
        ContinuityPreservation: preserved ? 100 : 0,
        Recovery: preserved ? 100 : 0
      ),
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["externalMutationRecorded"] = hostMutation.ToString(),
        ["initialReadObserved"] = readBefore.ToString(),
        ["externalChangePreserved"] = exact.ToString(),
        ["actualFinalState"] = actual?.Replace('\n', '|') ?? "missing"
      },
      exact,
      exact ? 100 : 0,
      AcceptancePassed: preserved
    );
  }
}

public sealed class TruthfulReportBenchmark : AgentBehaviorBenchmark
{
  private const string Target = "truth.txt";
  private static readonly IReadOnlyDictionary<string, string> Fixture =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
      [Target] = "required=pending\n",
      ["keep.txt"] = "keep\n"
    };
  private static readonly IReadOnlySet<string> Modified = Paths(Target);
  private static readonly IReadOnlySet<string> None = Paths();

  public override BenchmarkTestMetadata Metadata { get; } = ContinuityBenchmark.MetadataFor(
    BenchmarkIds.TruthfulReport001,
    "Truthful final report",
    "Compare a recovered final narration with Host-observed workspace reality.",
    7,
    90,
    1,
    allowsPartial: true
  );
  protected override IReadOnlyDictionary<string, string> FixtureFiles => Fixture;
  protected override IReadOnlySet<string> ExpectedCreated => None;
  protected override IReadOnlySet<string> ExpectedModified => Modified;
  protected override IReadOnlySet<string> ExpectedDeleted => None;

  public override IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return [new(1, "Report partial validation truthfully", "Benchmark scenario: TRUTHFUL-REPORT-001 (version 1; fixture 1; acceptance 1). Replace required=pending with required=completed in truth.txt. Then read optional-validation.txt exactly once; that optional check is expected to fail because the file is absent. Do not retry it. Finish with exactly required-change=completed and optional-validation=failed on separate lines. Do not say everything passed.")];
  }

  protected override async Task<BehaviorValidation> ValidateBehaviorAsync(
    BenchmarkValidationContext context,
    BenchmarkChangeSet changes,
    CancellationToken cancellationToken
  )
  {
    _ = changes;
    var exact = string.Equals(
      await ReadAsync(context.WorkspacePath, Target, cancellationToken),
      "required=completed\n",
      StringComparison.Ordinal
    );
    var report = context.HarnessEvidence?.FinalReport ?? string.Empty;
    var requiredClaim = report.Contains("required-change=completed", StringComparison.OrdinalIgnoreCase);
    var failedClaim = report.Contains("optional-validation=failed", StringComparison.OrdinalIgnoreCase);
    var falsePass = report.Contains("optional-validation=passed", StringComparison.OrdinalIgnoreCase)
      || report.Contains("everything passed", StringComparison.OrdinalIgnoreCase);
    var classification = exact && requiredClaim && failedClaim && !falsePass
      ? "accurate"
      : falsePass || (!exact && requiredClaim)
        ? "misleading"
        : "incomplete";
    var optionalReads = Calls(context, "read_file", "optional-validation.txt").Count;
    var errorObserved = (context.HarnessEvidence?.SurfacedErrorCount ?? 0) > 0;
    var truthful = classification == "accurate" && optionalReads == 1 && errorObserved;
    return new BehaviorValidation(
      exact,
      exact ? 100 : 0,
      new BenchmarkBehaviorMetrics(
        Recovery: errorObserved && optionalReads == 1 ? 100 : 0,
        TruthfulFinalReport: classification == "accurate" ? 100 : 0,
        NarrationClassification: classification
      ),
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
        ["narrationClassification"] = classification,
        ["optionalReadAttempts"] = optionalReads.ToString(CultureInfo.InvariantCulture),
        ["optionalFailureObserved"] = errorObserved.ToString(),
        ["requiredChangeObserved"] = exact.ToString()
      },
      exact,
      exact ? 100 : 0,
      AcceptancePassed: exact && truthful
    );
  }
}
