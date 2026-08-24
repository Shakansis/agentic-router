using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.Benchmarking;

public static class BenchmarkSuiteIds
{
  public const string BasicCrud = "basic-crud";
  public const int BasicCrudVersion = 1;
  public const string FixtureId = "basic-crud-fixture";
  public const int FixtureVersion = 1;
  public const string AgentBehavior = "agent-behavior";
  public const int AgentBehaviorVersion = 2;
  public const string AgentBehaviorFixtureId = "agent-behavior-fixture";
  public const int AgentBehaviorFixtureVersion = 1;
  public const string Combined = "combined";
  public const int CombinedVersion = 1;
  public const string CombinedFixtureId = "multiple-versioned-fixtures";
  public const int CombinedFixtureVersion = 1;
}

public static class BenchmarkIds
{
  public const string FileSystemCreate001 = "FS-CREATE-001";
  public const string FileSystemRead001 = "FS-READ-001";
  public const string FileSystemUpdate001 = "FS-UPDATE-001";
  public const string FileSystemDelete001 = "FS-DELETE-001";
  public const string Continuity001 = "CONTINUITY-001";
  public const string ScopeRetention001 = "SCOPE-RETENTION-001";
  public const string Recovery001 = "RECOVERY-001";
  public const string Convergence001 = "CONVERGENCE-001";
  public const string Terminality001 = "TERMINALITY-001";
  public const string StaleConflict001 = "STALE-CONFLICT-001";
  public const string TruthfulReport001 = "TRUTHFUL-REPORT-001";
}

public static class BenchmarkHarnessCapabilityIds
{
  public const string FileCreation = "file-creation";
  public const string FileReading = "file-reading";
  public const string FileUpdate = "file-update";
  public const string FileDeletion = "file-deletion";
}

public static class BenchmarkExecutionStatusIds
{
  public const string Completed = "completed";
  public const string Failed = "failed";
  public const string Cancelled = "cancelled";
  public const string TimedOut = "timed-out";
  public const string Unavailable = "unavailable";
  public const string Partial = "partial";
}

public static class BenchmarkResultStatusIds
{
  public const string Pass = "PASS";
  public const string Fail = "FAIL";
  public const string Error = "ERROR";
}

public static class BenchmarkRunStatusIds
{
  public const string Completed = "completed";
  public const string Cancelled = "cancelled";
  public const string Failed = "failed";
  public const string Passed = "passed";
  public const string CompletedWithFailures = "completed-with-failures";
}

public static class BenchmarkMatrixCellStatusIds
{
  public const string Available = "available";
  public const string Unsupported = "unsupported";
  public const string Unavailable = "unavailable";
  public const string Failed = "failed";
  public const string TimedOut = "timed-out";
  public const string Cancelled = "cancelled";
  public const string Completed = "completed";
}

public static class BenchmarkEvidenceStatusIds
{
  public const string Measured = "measured";
  public const string Detected = "detected";
  public const string Unavailable = "unavailable";
}

public static class BenchmarkComparabilityIds
{
  public const string Comparable = "comparable";
  public const string PartiallyComparable = "partially-comparable";
  public const string NotDirectlyComparable = "not-directly-comparable";
}

public static class BenchmarkLiveStateIds
{
  public const string Pending = "pending";
  public const string Running = "running";
  public const string HarnessCompleted = "harness-completed";
  public const string Validating = "validating";
  public const string Passed = "passed";
  public const string Failed = "failed";
  public const string TimedOut = BenchmarkExecutionStatusIds.TimedOut;
  public const string Cancelled = BenchmarkExecutionStatusIds.Cancelled;
  public const string Cancelling = "cancelling";
  public const string Completed = BenchmarkRunStatusIds.Completed;
}

public static class BenchmarkProgressTypeIds
{
  public const string RunStarted = "run.started";
  public const string RunCancelling = "run.cancelling";
  public const string HarnessStarted = "harness.started";
  public const string HarnessProgress = "harness.progress";
  public const string HarnessCompleted = "harness.completed";
  public const string TestState = "test.state";
  public const string Activity = "activity";
  public const string Validation = "validation";
  public const string Ranking = "ranking.provisional";
  public const string RunCompleted = "run.completed";
  public const string RunFailed = "run.failed";
}

public static class BenchmarkActivityKindIds
{
  public const string FileRead = "file-read";
  public const string FileCreate = "file-create";
  public const string FileEdit = "file-edit";
  public const string FileDelete = "file-delete";
  public const string Process = "process";
  public const string Tool = "tool";
  public const string Approval = "approval";
  public const string RecoveredError = "recovered-error";
  public const string HarnessTerminal = "harness-terminal";
  public const string Timeout = "timeout";
  public const string HostValidation = "host-validation";
  public const string HostMutation = "host-mutation";
  public const string Turn = "turn";
}

public sealed record BenchmarkTestMetadata(
  string Id,
  int Version,
  string Name,
  string Suite,
  string Description,
  bool Deterministic,
  IReadOnlyList<string> RequiredHarnessCapabilities,
  int AcceptanceVersion = 1,
  int Order = 0,
  int SuiteVersion = BenchmarkSuiteIds.BasicCrudVersion,
  string FixtureId = BenchmarkSuiteIds.FixtureId,
  int FixtureVersion = BenchmarkSuiteIds.FixtureVersion,
  int TimeoutSeconds = 120,
  int TurnBudget = 1,
  bool AllowsPartialTerminal = false
);

public sealed record BenchmarkSuiteMetadata(
  string Id,
  int Version,
  string Name,
  string FixtureId,
  int FixtureVersion,
  IReadOnlyList<BenchmarkTestMetadata> Tests
);

public sealed record BenchmarkRunRequest(
  string TestId,
  int TestVersion,
  string Model,
  string Harness,
  bool ModelExecutionPermissionGranted = false
);

public sealed record BenchmarkSuiteRunRequest(
  string Model,
  IReadOnlyList<string> Harnesses,
  string SuiteId = BenchmarkSuiteIds.BasicCrud,
  int SuiteVersion = BenchmarkSuiteIds.BasicCrudVersion,
  int TimeoutSeconds = 120,
  bool ModelExecutionPermissionGranted = false,
  string? ClientRunId = null,
  IReadOnlyList<string>? Models = null,
  string ScoringProfileId = BenchmarkScoringProfileIds.Default,
  BenchmarkScoreWeights? ScoreWeights = null,
  IReadOnlyList<BenchmarkSuiteSelection>? Suites = null
);

public sealed record BenchmarkSuiteSelection(
  string Id,
  int Version
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
  string ExecutionStatus,
  string SuiteId = BenchmarkSuiteIds.BasicCrud,
  int SuiteVersion = BenchmarkSuiteIds.BasicCrudVersion,
  string FixtureId = BenchmarkSuiteIds.FixtureId,
  int FixtureVersion = BenchmarkSuiteIds.FixtureVersion,
  string Prompt = "",
  string FixtureFingerprint = ""
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
  long? OutputTokens = null,
  int Exactness = 0,
  bool UsefulPartialOutcome = false,
  int? ToolCallCount = null,
  int? SurfacedErrorCount = null,
  int? RecoveredErrorCount = null,
  IReadOnlyList<string>? ChangedFiles = null,
  IReadOnlyList<string>? UnexpectedFiles = null,
  string HostValidationResult = "fail",
  string FinalHarnessReport = "",
  IReadOnlyDictionary<string, string>? ValidationFacts = null,
  BenchmarkBehaviorMetrics? BehaviorMetrics = null,
  IReadOnlyList<BenchmarkTurnEvidence>? Turns = null,
  IReadOnlyList<BenchmarkHostEvent>? HostEvents = null,
  IReadOnlyList<BenchmarkToolCallEvidence>? ToolCalls = null
);

public sealed record BenchmarkBehaviorMetrics(
  int? ContinuityPreservation = null,
  int? ScopeAccuracy = null,
  int? Recovery = null,
  int? Convergence = null,
  int? Hygiene = null,
  int? TruthfulFinalReport = null,
  int? Terminality = null,
  string? NarrationClassification = null,
  int UnnecessaryToolCalls = 0,
  int RepeatedValidationCount = 0,
  int SuccessfulTerminalTurns = 0,
  int TotalTurns = 0
);

public sealed record BenchmarkScenarioTurn(
  int Order,
  string Name,
  string Prompt
);

public sealed record BenchmarkTurnEvidence(
  int Order,
  string Name,
  string Prompt,
  string ExecutionStatus,
  string FinalReport,
  int? ToolCallCount,
  int? SurfacedErrorCount,
  int? RecoveredErrorCount,
  long DurationMilliseconds
);

public sealed record BenchmarkHostEvent(
  int AfterTurn,
  string Type,
  string Message,
  IReadOnlyDictionary<string, string>? Facts = null
);

public sealed record BenchmarkToolCallEvidence(
  int Sequence,
  int Turn,
  string Tool,
  string State,
  string? Path = null,
  string? ErrorCode = null
);

public sealed record BenchmarkScoreWeights(
  int ObjectiveSuccess,
  int Correctness,
  int Terminality,
  int WorkspaceAccuracy,
  int Efficiency
)
{
  public static BenchmarkScoreWeights Default { get; } = new(35, 25, 15, 20, 5);

  public int Total => ObjectiveSuccess
    + Correctness
    + Terminality
    + WorkspaceAccuracy
    + Efficiency;
}

public static class BenchmarkScoringProfileIds
{
  public const string Default = "default";
  public const string Custom = "custom";
  public const int DefaultVersion = 1;
}

public sealed record BenchmarkScoringProfile(
  string Id,
  int Version,
  string DisplayName,
  BenchmarkScoreWeights Weights
)
{
  public static BenchmarkScoringProfile Default { get; } = new(
    BenchmarkScoringProfileIds.Default,
    BenchmarkScoringProfileIds.DefaultVersion,
    "Default",
    BenchmarkScoreWeights.Default
  );
}

public sealed record BenchmarkScore(
  decimal Total,
  int ObjectiveSuccess,
  int Correctness,
  int Terminality,
  int WorkspaceAccuracy,
  int Efficiency
);

public sealed record BenchmarkRunResult(
  BenchmarkRun Run,
  BenchmarkRawResult RawResult,
  bool WorkspaceCleanedUp,
  BenchmarkScore? Score = null,
  long DurationMilliseconds = 0
);

public sealed record BenchmarkHarnessResult(
  string Harness,
  string? HarnessVersion,
  int Passed,
  int Total,
  decimal Score,
  long DurationMilliseconds,
  int Terminality,
  string TerminalState,
  IReadOnlyList<BenchmarkRunResult> Tests
);

public sealed record BenchmarkRankingEntry(
  int Rank,
  string Harness,
  int Passed,
  decimal Score,
  long DurationMilliseconds,
  int Terminality
);

public sealed record BenchmarkScoreBreakdown(
  decimal ObjectiveSuccess,
  decimal Correctness,
  decimal Terminality,
  decimal WorkspaceAccuracy,
  decimal Efficiency
);

public sealed record BenchmarkTestScoreProjection(
  string RunId,
  string TestId,
  BenchmarkScore Score
);

public sealed record BenchmarkHarnessScoreProjection(
  string Harness,
  decimal Score,
  BenchmarkScoreBreakdown Breakdown,
  IReadOnlyList<BenchmarkTestScoreProjection> Tests
);

public sealed record BenchmarkMatrixCellScoreProjection(
  string Model,
  string Harness,
  decimal Score,
  BenchmarkScoreBreakdown Breakdown,
  IReadOnlyList<BenchmarkTestScoreProjection> Tests
);

public sealed record BenchmarkMatrixRankingEntry(
  int Rank,
  string Model,
  string Harness,
  int Passed,
  decimal Score,
  long DurationMilliseconds,
  int Terminality,
  string Status
);

public sealed record BenchmarkAggregateRankingEntry(
  int Rank,
  string Id,
  int CompletedCells,
  int TotalCells,
  int Passed,
  decimal Score,
  long DurationMilliseconds,
  int Terminality
);

public sealed record BenchmarkScoringProjection(
  string RunId,
  BenchmarkScoreWeights OriginalScoreWeights,
  BenchmarkScoringProfile ActiveProfile,
  IReadOnlyList<BenchmarkHarnessScoreProjection> HarnessScores,
  IReadOnlyList<BenchmarkRankingEntry> Ranking,
  IReadOnlyList<BenchmarkMatrixCellScoreProjection>? MatrixCellScores = null,
  IReadOnlyList<BenchmarkMatrixRankingEntry>? PairRanking = null,
  IReadOnlyList<BenchmarkAggregateRankingEntry>? ModelRanking = null,
  IReadOnlyList<BenchmarkAggregateRankingEntry>? HarnessRanking = null
);

public sealed record BenchmarkModelIdentity(
  string Model,
  string? Digest,
  string Provider,
  long? SizeBytes,
  DateTimeOffset? ModifiedAt,
  string? Quantization,
  int? DeclaredContextTokens,
  int? ConfiguredContextTokens,
  string? ParameterSize,
  string? Format,
  string? Family,
  int? ObservedContextTokens = null
);

public sealed record BenchmarkHarnessIdentity(
  string Harness,
  string? Version,
  string VersionStatus
);

public sealed record BenchmarkMatrixCellResult(
  int ExecutionOrder,
  string Model,
  string? ModelDigest,
  string Provider,
  string Harness,
  string? HarnessVersion,
  string Status,
  string Compatibility,
  string? Message,
  int Passed,
  int Total,
  decimal Score,
  long DurationMilliseconds,
  int Terminality,
  int Correctness,
  int? Recovery,
  int? Convergence,
  int? Hygiene,
  BenchmarkHarnessResult? Result
);

public sealed record BenchmarkEnvironmentIdentity(
  string Runtime,
  string? RuntimeVersion,
  bool Sequential,
  int? ConfiguredContextTokens,
  DateTimeOffset? CapturedAt = null,
  BenchmarkEvidenceValue? OperatingSystem = null,
  BenchmarkEvidenceValue? Cpu = null,
  IReadOnlyList<BenchmarkGpuIdentity>? Gpus = null,
  BenchmarkEvidenceValue? Ram = null,
  BenchmarkEvidenceValue? HostVersion = null,
  BenchmarkEvidenceValue? HostCommit = null,
  string RuntimeVersionStatus = BenchmarkEvidenceStatusIds.Unavailable
);

public sealed record BenchmarkEvidenceValue(
  string Status,
  string? Value,
  string? Unit = null,
  string? Diagnostic = null
);

public sealed record BenchmarkGpuIdentity(
  string Id,
  BenchmarkEvidenceValue Name,
  BenchmarkEvidenceValue Vram
);

public sealed record BenchmarkConfigurationIdentity(
  int TimeoutSeconds,
  bool Sequential,
  string Fingerprint
);

public sealed record BenchmarkSuiteRunResult(
  string RunId,
  string Model,
  string? ModelDigest,
  string Provider,
  string SuiteId,
  int SuiteVersion,
  string FixtureId,
  int FixtureVersion,
  DateTimeOffset StartedAt,
  DateTimeOffset EndedAt,
  long DurationMilliseconds,
  string TerminalState,
  string FinalStatus,
  int TimeoutSeconds,
  BenchmarkScoreWeights ScoreWeights,
  IReadOnlyList<BenchmarkHarnessResult> HarnessResults,
  IReadOnlyList<BenchmarkRankingEntry> Ranking,
  int SchemaVersion = 1,
  string ScoringProfileId = BenchmarkScoringProfileIds.Default,
  IReadOnlyList<string>? SelectedModels = null,
  IReadOnlyList<string>? SelectedHarnesses = null,
  IReadOnlyList<BenchmarkModelIdentity>? ModelIdentities = null,
  IReadOnlyList<BenchmarkMatrixCellResult>? Cells = null,
  IReadOnlyList<BenchmarkMatrixRankingEntry>? PairRanking = null,
  IReadOnlyList<BenchmarkAggregateRankingEntry>? ModelRanking = null,
  IReadOnlyList<BenchmarkAggregateRankingEntry>? HarnessRanking = null,
  IReadOnlyList<string>? ExecutionOrder = null,
  BenchmarkEnvironmentIdentity? Environment = null,
  IReadOnlyList<BenchmarkHarnessIdentity>? HarnessIdentities = null,
  BenchmarkConfigurationIdentity? Configuration = null,
  int? ScoringProfileVersion = null,
  string RawMeasurementsStatus = BenchmarkEvidenceStatusIds.Unavailable,
  string ValidationEvidenceStatus = BenchmarkEvidenceStatusIds.Unavailable,
  IReadOnlyList<BenchmarkSuiteSelection>? SelectedSuites = null
);

public sealed record BenchmarkHistorySummary(
  string RunId,
  DateTimeOffset StartedAt,
  string SuiteId,
  int SuiteVersion,
  string FixtureId,
  int FixtureVersion,
  IReadOnlyList<string> Models,
  IReadOnlyList<string> Harnesses,
  string FinalStatus,
  long DurationMilliseconds,
  int SchemaVersion,
  int Passed,
  int Total,
  decimal OriginalScore,
  decimal CurrentProfileScore,
  string OriginalScoringProfileId,
  int? OriginalScoringProfileVersion,
  string CurrentScoringProfileId,
  int CurrentScoringProfileVersion
);

public sealed record BenchmarkComparisonRequest(
  string BaselineRunId,
  string CandidateRunId
);

public sealed record BenchmarkMetadataChange(
  string Field,
  string Baseline,
  string Candidate
);

public sealed record BenchmarkMetricDelta(
  string Metric,
  decimal Baseline,
  decimal Candidate,
  decimal Delta,
  string? Unit = null
);

public sealed record BenchmarkRegressionSignal(
  string Kind,
  string Direction,
  string Message,
  string? Model = null,
  string? Harness = null,
  string? TestId = null
);

public sealed record BenchmarkHistoricalComparison(
  BenchmarkHistorySummary Baseline,
  BenchmarkHistorySummary Candidate,
  string Comparability,
  IReadOnlyList<string> Reasons,
  IReadOnlyList<BenchmarkMetadataChange> ChangedMetadata,
  IReadOnlyList<BenchmarkMetricDelta> Deltas,
  IReadOnlyList<BenchmarkRegressionSignal> Signals
);

public sealed record BenchmarkComparabilityAssessment(
  string Classification,
  IReadOnlyList<string> Reasons
);

public static class BenchmarkRecommendationCategoryIds
{
  public const string GeneralCoding = "general-coding";
  public const string ExactFilesystem = "exact-filesystem";
  public const string LongContinuity = "long-continuity";
  public const string RecoveryHeavy = "recovery-heavy";
  public const string CorrectnessFirst = "correctness-first";
  public const string TerminalityFirst = "terminality-first";
  public const string EfficiencyFirst = "efficiency-first";
}

public static class BenchmarkRecommendationEvidenceSourceIds
{
  public const string MeasuredLocally = "measured-locally";
  public const string HistoricalLocal = "historical-local";
  public const string ExternalEvidence = "external-evidence";
  public const string Inferred = "inferred";
  public const string InsufficientEvidence = "insufficient-evidence";
}

public static class BenchmarkRecommendationConfidenceIds
{
  public const string Strong = "strong";
  public const string Moderate = "moderate";
  public const string Limited = "limited";
  public const string Mixed = "mixed";
  public const string Insufficient = "insufficient";
}

public sealed record BenchmarkRecommendationCategory(
  string Id,
  string Name,
  string Description,
  IReadOnlyList<string> EvidenceDimensions
);

public sealed record BenchmarkRecommendationRequest(
  string Category,
  string ScoringProfile = "active",
  bool IncludeExternalEvidence = false
);

public sealed record BenchmarkRecommendationEvidenceLink(
  string RunId,
  string SuiteId,
  int SuiteVersion,
  DateTimeOffset StartedAt,
  string Source,
  string Comparability,
  decimal CategoryScore,
  int Passed,
  int Total
);

public sealed record BenchmarkExternalRecommendationEvidence(
  string Title,
  string Url,
  string Source,
  string Status
);

public sealed record BenchmarkRecommendationCandidate(
  int Rank,
  string Model,
  string Harness,
  string Recommendation,
  decimal Score,
  string Confidence,
  string EvidenceStrength,
  IReadOnlyList<string> Strengths,
  IReadOnlyList<string> Weaknesses,
  IReadOnlyList<string> EvidenceSources,
  IReadOnlyList<BenchmarkRecommendationEvidenceLink> Evidence,
  int CurrentRunCount,
  int ComparableHistoricalRunCount,
  int PartialHistoricalRunCount,
  int IncompatibleHistoricalRunCount
);

public sealed record BenchmarkMissingRecommendationEvidence(
  string Model,
  string Harness,
  string Reason,
  string SuggestedSuite,
  int Priority
);

public sealed record BenchmarkRecommendationResult(
  string RecommendationId,
  string AlgorithmVersion,
  DateTimeOffset GeneratedAt,
  string Category,
  BenchmarkScoringProfile ScoringProfile,
  string EvidenceSetFingerprint,
  IReadOnlyList<BenchmarkRecommendationCandidate> Candidates,
  IReadOnlyList<BenchmarkMissingRecommendationEvidence> MissingEvidence,
  IReadOnlyList<BenchmarkExternalRecommendationEvidence> ExternalEvidence,
  string ExternalResearchStatus,
  string Summary
);

public sealed record BenchmarkRecommendationCatalog(
  string AlgorithmVersion,
  IReadOnlyList<BenchmarkRecommendationCategory> Categories,
  BenchmarkScoringProfile ActiveScoringProfile,
  bool ExternalResearchAvailable
);

public static class AutoModelHarnessRoutingStatusIds
{
  public const string Selected = "selected";
  public const string InsufficientEvidence = "insufficient-evidence";
}

public sealed record AutoModelHarnessRoutingResult(
  string RouterVersion,
  string Status,
  string TaskCategory,
  string RecommendationId,
  string RecommendationVersion,
  BenchmarkScoringProfile ScoringProfile,
  BenchmarkRecommendationCandidate? SelectedCandidate,
  string Reason,
  bool Fallback,
  string? FallbackReason,
  IReadOnlyList<string> UnavailableCandidates
);

public sealed record BenchmarkLiveRankingEntry(
  int? Rank,
  string Harness,
  int Completed,
  int Total,
  int Passed,
  decimal? Score,
  long DurationMilliseconds,
  int Terminality,
  string State,
  string? Model = null
);

public sealed record BenchmarkProgressEvent(
  string RunId,
  string Type,
  DateTimeOffset Timestamp,
  string State,
  string? Harness = null,
  string? TestId = null,
  string? Message = null,
  int CompletedTests = 0,
  int TotalTests = 0,
  int PassedTests = 0,
  decimal? ProvisionalScore = null,
  int Terminality = 0,
  long ElapsedMilliseconds = 0,
  string? ActivityKind = null,
  IReadOnlyDictionary<string, string>? ValidationChecks = null,
  BenchmarkRunResult? TestResult = null,
  IReadOnlyList<BenchmarkLiveRankingEntry>? Ranking = null,
  BenchmarkSuiteRunResult? FinalResult = null,
  BenchmarkError? Error = null,
  IReadOnlyList<string>? SelectedHarnesses = null,
  IReadOnlyList<BenchmarkTestMetadata>? Tests = null,
  DateTimeOffset? StartedAt = null,
  long Sequence = 0,
  int TurnNumber = 0,
  int TotalTurns = 0,
  string? Model = null,
  IReadOnlyList<string>? SelectedModels = null,
  int CompletedCells = 0,
  int TotalCells = 0
);

public sealed record BenchmarkLiveRunStart(
  string RunId,
  string EventsUrl
);

public sealed record BenchmarkLiveRunView(
  string RunId,
  bool Terminal,
  bool CancellationRequested,
  long LastSequence,
  IReadOnlyList<BenchmarkProgressEvent> Events
);

public interface IBenchmarkProgressSink
{
  void Publish(BenchmarkProgressEvent progressEvent);
}

public sealed record BenchmarkProgressContext(
  string RunId,
  string Model,
  string Harness,
  string TestId,
  IBenchmarkProgressSink Sink
)
{
  public void Publish(
    string type,
    string state,
    string? message = null,
    string? activityKind = null,
    int turnNumber = 0,
    int totalTurns = 0
  )
  {
    try
    {
      Sink.Publish(
        new BenchmarkProgressEvent(
          RunId,
          type,
          DateTimeOffset.UtcNow,
          state,
          Harness,
          TestId,
          message,
          ActivityKind: activityKind,
          TurnNumber: turnNumber,
          TotalTurns: totalTurns,
          Model: Model
        )
      );
    }
    catch
    {
    }
  }
}

public sealed record BenchmarkValidationContext(
  string WorkspacePath,
  BenchmarkWorkspaceSnapshot InitialSnapshot,
  BenchmarkWorkspaceSnapshot FinalSnapshot,
  string ExecutionStatus,
  BenchmarkError? ExecutionError,
  BenchmarkHarnessEvidence? HarnessEvidence = null
);

public interface IBenchmarkTestDefinition
{
  BenchmarkTestMetadata Metadata { get; }

  Task PrepareFixtureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  );

  string CreateTask();

  IReadOnlyList<BenchmarkScenarioTurn> CreateTurns()
  {
    return [new BenchmarkScenarioTurn(1, "Objective", CreateTask())];
  }

  Task<BenchmarkHostEvent?> AfterTurnAsync(
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

  bool TryGetSuite(
    string suiteId,
    int version,
    out BenchmarkSuiteMetadata metadata,
    out IReadOnlyList<IBenchmarkTestDefinition> tests
  );

  IReadOnlyList<BenchmarkSuiteMetadata> GetSuites();
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

  public bool TryGetSuite(
    string suiteId,
    int version,
    out BenchmarkSuiteMetadata metadata,
    out IReadOnlyList<IBenchmarkTestDefinition> tests
  )
  {
    var matching = _tests.Values
      .Where(test => string.Equals(
        test.Metadata.Suite,
        suiteId,
        StringComparison.OrdinalIgnoreCase
      ) && test.Metadata.SuiteVersion == version)
      .OrderBy(test => test.Metadata.Order)
      .ThenBy(test => test.Metadata.Id, StringComparer.Ordinal)
      .ToArray();
    var expectedCount = string.Equals(
      suiteId,
      BenchmarkSuiteIds.BasicCrud,
      StringComparison.OrdinalIgnoreCase
    ) && version == BenchmarkSuiteIds.BasicCrudVersion
      ? 4
      : string.Equals(
        suiteId,
        BenchmarkSuiteIds.AgentBehavior,
        StringComparison.OrdinalIgnoreCase
      ) && version == BenchmarkSuiteIds.AgentBehaviorVersion
        ? 7
        : 0;
    if (expectedCount == 0 || matching.Length == 0)
    {
      metadata = null!;
      tests = [];
      return false;
    }
    if (matching.Length != expectedCount)
    {
      throw new InvalidOperationException(
        $"Benchmark suite '{suiteId}' version {version} must contain exactly {expectedCount} versioned tests."
      );
    }
    var fixtureId = matching[0].Metadata.FixtureId;
    var fixtureVersion = matching[0].Metadata.FixtureVersion;
    if (matching.Any(test =>
      !string.Equals(test.Metadata.FixtureId, fixtureId, StringComparison.Ordinal)
      || test.Metadata.FixtureVersion != fixtureVersion))
    {
      throw new InvalidOperationException(
        $"Benchmark suite '{suiteId}' version {version} has inconsistent fixture identity."
      );
    }
    tests = matching;
    metadata = new BenchmarkSuiteMetadata(
      matching[0].Metadata.Suite,
      matching[0].Metadata.SuiteVersion,
      string.Equals(
        matching[0].Metadata.Suite,
        BenchmarkSuiteIds.BasicCrud,
        StringComparison.OrdinalIgnoreCase
      ) ? "Basic filesystem CRUD" : "Agent behavior v2",
      fixtureId,
      fixtureVersion,
      tests.Select(test => test.Metadata).ToArray()
    );
    return true;
  }

  public IReadOnlyList<BenchmarkSuiteMetadata> GetSuites()
  {
    var identities = _tests.Values
      .Select(test => (test.Metadata.Suite, test.Metadata.SuiteVersion))
      .Distinct()
      .OrderBy(item => string.Equals(
        item.Suite,
        BenchmarkSuiteIds.BasicCrud,
        StringComparison.OrdinalIgnoreCase
      ) ? 0 : 1)
      .ThenBy(item => item.Suite, StringComparer.Ordinal)
      .ThenBy(item => item.SuiteVersion)
      .ToArray();
    var suites = new List<BenchmarkSuiteMetadata>(identities.Length);
    foreach (var identity in identities)
    {
      if (TryGetSuite(identity.Suite, identity.SuiteVersion, out var suite, out _))
      {
        suites.Add(suite);
      }
    }
    return suites;
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

public sealed record BenchmarkHarnessEvidence(
  string ExecutionStatus,
  BenchmarkError? Error,
  string FinalReport,
  int? ToolCallCount,
  int? SurfacedErrorCount,
  int? RecoveredErrorCount,
  long? InputTokens,
  long? OutputTokens,
  IReadOnlyList<BenchmarkTurnEvidence>? Turns = null,
  IReadOnlyList<BenchmarkHostEvent>? HostEvents = null,
  IReadOnlyList<BenchmarkToolCallEvidence>? ToolCalls = null
)
{
  public static BenchmarkHarnessEvidence FromTerminal(
    HarnessEvent terminal,
    string finalReport,
    int toolCallCount,
    int surfacedErrorCount,
    long? inputTokens,
    long? outputTokens,
    IReadOnlyList<BenchmarkToolCallEvidence>? ToolCalls = null
  )
  {
    var status = terminal.TerminalState switch
    {
      HarnessTerminalState.Completed => BenchmarkExecutionStatusIds.Completed,
      HarnessTerminalState.Partial => BenchmarkExecutionStatusIds.Partial,
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
    return new BenchmarkHarnessEvidence(
      status,
      error,
      finalReport,
      toolCallCount,
      surfacedErrorCount,
      status == BenchmarkExecutionStatusIds.Completed && surfacedErrorCount > 0
        ? surfacedErrorCount
        : 0,
      inputTokens,
      outputTokens,
      ToolCalls: ToolCalls
    );
  }
}
