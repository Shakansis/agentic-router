using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkEngine
{
  Task<BenchmarkRunResult> RunAsync(
    BenchmarkRunRequest request,
    CancellationToken cancellationToken
  );

  Task<BenchmarkSuiteRunResult> RunSuiteAsync(
    BenchmarkSuiteRunRequest request,
    CancellationToken cancellationToken
  );

  Task<BenchmarkSuiteRunResult> RunSuiteAsync(
    BenchmarkSuiteRunRequest request,
    IBenchmarkProgressSink progressSink,
    CancellationToken cancellationToken
  );
}

public sealed class BenchmarkEngine : IBenchmarkEngine
{
  private static readonly HostCapabilityProfile BenchmarkHostCapabilities =
    HostCapabilityProfile.Create(
      new ExecutionTurnToolScope(
        [
          "read_file",
          "create_file",
          "create_files",
          "write_file",
          "replace_text",
          "delete_paths"
        ],
        ProcessExecutionAllowed: false,
        ManualValidationRequested: false,
        ValidationProfileAvailable: false,
        GitToolsAvailable: false,
        DirectoryCreationAvailable: false,
        DeletionAvailable: true
      ),
      "auto"
    );
  private readonly IBenchmarkTestRegistry _tests;
  private readonly IBenchmarkWorkspaceFactory _workspaces;
  private readonly IHarnessRegistry _harnesses;
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IBenchmarkNativeExecutor _nativeExecutor;
  private readonly IBenchmarkScorer _scorer;
  private readonly IBenchmarkResultStore _results;
  private readonly IBenchmarkRunCancellationRegistry _cancellations;
  private readonly IBenchmarkEnvironmentSnapshotProvider _environmentSnapshots;

  public BenchmarkEngine(
    IBenchmarkTestRegistry tests,
    IBenchmarkWorkspaceFactory workspaces,
    IHarnessRegistry harnesses,
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IBenchmarkNativeExecutor nativeExecutor,
    IBenchmarkScorer scorer,
    IBenchmarkResultStore results,
    IBenchmarkRunCancellationRegistry cancellations,
    IBenchmarkEnvironmentSnapshotProvider environmentSnapshots
  )
  {
    _tests = tests;
    _workspaces = workspaces;
    _harnesses = harnesses;
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _nativeExecutor = nativeExecutor;
    _scorer = scorer;
    _results = results;
    _cancellations = cancellations;
    _environmentSnapshots = environmentSnapshots;
  }

  public async Task<BenchmarkRunResult> RunAsync(
    BenchmarkRunRequest request,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(request.TestId)
      || !_tests.TryGet(request.TestId, request.TestVersion, out var test)
    )
    {
      throw new BenchmarkRequestException(
        "benchmark-test-unknown",
        $"Benchmark test '{request.TestId}' version {request.TestVersion} is unavailable.",
        "testId"
      );
    }
    RequirePermission(request.ModelExecutionPermissionGranted);
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var providerEndpoint = new Uri(settings.OllamaUrl, UriKind.Absolute);
    var model = await ResolveModelAsync(
      request.Model,
      providerEndpoint,
      cancellationToken
    );
    var harness = await ResolveHarnessAsync(request.Harness, cancellationToken);
    return await RunTestAsync(
      test,
      model,
      harness.Adapter,
      harness.Availability,
      providerEndpoint,
      settings,
      TimeSpan.FromSeconds(test.Metadata.TimeoutSeconds),
      BenchmarkScoreWeights.Default,
      cancellationToken
    );
  }

  public async Task<BenchmarkSuiteRunResult> RunSuiteAsync(
    BenchmarkSuiteRunRequest request,
    CancellationToken cancellationToken
  )
  {
    return await RunSuiteCoreAsync(request, null, cancellationToken);
  }

  public async Task<BenchmarkSuiteRunResult> RunSuiteAsync(
    BenchmarkSuiteRunRequest request,
    IBenchmarkProgressSink progressSink,
    CancellationToken cancellationToken
  )
  {
    ArgumentNullException.ThrowIfNull(progressSink);
    return await RunSuiteCoreAsync(request, progressSink, cancellationToken);
  }

  private async Task<BenchmarkSuiteRunResult> RunSuiteCoreAsync(
    BenchmarkSuiteRunRequest request,
    IBenchmarkProgressSink? progressSink,
    CancellationToken cancellationToken
  )
  {
    RequirePermission(request.ModelExecutionPermissionGranted);
    var resolvedSuites = ResolveSuites(request);
    var suite = resolvedSuites.Metadata;
    var tests = resolvedSuites.Tests;
    if (request.TimeoutSeconds is < 5 or > 600)
    {
      throw new BenchmarkRequestException(
        "benchmark-timeout-invalid",
        "Benchmark test timeout must be between 5 and 600 seconds.",
        "timeoutSeconds"
      );
    }
    var requestedHarnesses = NormalizeHarnesses(request.Harnesses);
    var requestedModels = NormalizeModels(request);
    var scoreWeights = request.ScoreWeights ?? BenchmarkScoreWeights.Default;
    _scorer.Validate(scoreWeights);
    var scoringProfileId = NormalizeScoringProfileId(request.ScoringProfileId);
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var providerEndpoint = new Uri(settings.OllamaUrl, UriKind.Absolute);
    var installedModels = await _ollamaClient.GetModelsAsync(
      providerEndpoint,
      cancellationToken
    );
    var models = requestedModels.Select(name => new ResolvedBenchmarkModel(
      name,
      installedModels.FirstOrDefault(candidate =>
        string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.Provider, ModelProviderIds.OllamaLocal, StringComparison.Ordinal))
    )).ToArray();
    var harnesses = new List<ResolvedBenchmarkHarness>(requestedHarnesses.Count);
    foreach (var harnessId in requestedHarnesses)
    {
      harnesses.Add(await ResolveHarnessStatusAsync(harnessId, cancellationToken));
    }

    var runId = NormalizeRunId(request.ClientRunId);
    if (await _results.GetAsync(runId, cancellationToken) is not null)
    {
      throw new BenchmarkRequestException(
        "benchmark-run-id-conflict",
        $"Benchmark run '{runId}' already exists.",
        "clientRunId"
      );
    }
    using var lease = _cancellations.Register(runId, cancellationToken);
    var startedAt = DateTimeOffset.UtcNow;
    Publish(progressSink, new BenchmarkProgressEvent(
      runId,
      BenchmarkProgressTypeIds.RunStarted,
      startedAt,
      BenchmarkLiveStateIds.Running,
      TotalTests: tests.Count,
      SelectedHarnesses: harnesses.Select(item => item.Adapter.Definition.Id).ToArray(),
      Tests: tests.Select(item => item.Metadata).ToArray(),
      StartedAt: startedAt,
      SelectedModels: requestedModels,
      TotalCells: models.Length * harnesses.Count
    ));
    foreach (var model in models)
    {
      foreach (var harness in harnesses)
      {
        foreach (var test in tests)
        {
          Publish(progressSink, new BenchmarkProgressEvent(
            runId,
            BenchmarkProgressTypeIds.TestState,
            DateTimeOffset.UtcNow,
            BenchmarkLiveStateIds.Pending,
            harness.Adapter.Definition.Id,
            test.Metadata.Id,
            TotalTests: tests.Count,
            Model: model.RequestedName,
            TotalCells: models.Length * harnesses.Count
          ));
        }
      }
    }

    var liveResults = new ConcurrentDictionary<string, BenchmarkHarnessResult>(
      StringComparer.OrdinalIgnoreCase
    );
    foreach (var model in models)
    {
      foreach (var harness in harnesses)
      {
        liveResults[CellKey(model.RequestedName, harness.Adapter.Definition.Id)] =
          EmptyPendingHarness(harness, tests.Count);
      }
    }
    var cells = new List<BenchmarkMatrixCellResult>(models.Length * harnesses.Count);
    var executionOrder = 0;
    foreach (var model in models)
    {
      foreach (var harness in harnesses)
      {
        executionOrder++;
        var compatibility = Compatibility(model, harness);
        string? preCancellationCompatibility = null;
        if (lease.Token.IsCancellationRequested)
        {
          preCancellationCompatibility = compatibility.Status;
          compatibility = new CellCompatibility(
            BenchmarkMatrixCellStatusIds.Cancelled,
            "The matrix run was cancelled before this cell started."
          );
        }
        if (!string.Equals(
          compatibility.Status,
          BenchmarkMatrixCellStatusIds.Available,
          StringComparison.Ordinal
        ))
        {
          cells.Add(CreateNonExecutableCell(
            executionOrder,
            model,
            harness,
            compatibility,
            tests.Count,
            preCancellationCompatibility
          ));
          Publish(progressSink, new BenchmarkProgressEvent(
            runId,
            BenchmarkProgressTypeIds.HarnessCompleted,
            DateTimeOffset.UtcNow,
            compatibility.Status,
            harness.Adapter.Definition.Id,
            Message: compatibility.Message,
            TotalTests: tests.Count,
            Model: model.RequestedName,
            CompletedCells: cells.Count,
            TotalCells: models.Length * harnesses.Count
          ));
          continue;
        }
        var result = await RunHarnessAsync(
          runId,
          harness,
          tests,
          model.Installed!,
          providerEndpoint,
          settings,
          request.TimeoutSeconds,
          scoreWeights,
          lease.Token,
          progressSink,
          liveResults
        );
        cells.Add(CreateCell(executionOrder, model.Installed!, result));
      }
    }
    var matrixCells = cells.ToArray();
    var harnessResults = models.Length == 1
      ? matrixCells.Where(cell => cell.Result is not null)
        .Select(cell => cell.Result!)
        .ToArray()
      : [];

    var endedAt = DateTimeOffset.UtcNow;
    var terminalState = lease.Token.IsCancellationRequested
      ? BenchmarkRunStatusIds.Cancelled
      : BenchmarkRunStatusIds.Completed;
    var allPassed = !lease.Token.IsCancellationRequested
      && matrixCells.Length == models.Length * harnesses.Count
      && matrixCells.All(cell => string.Equals(
        cell.Status,
        BenchmarkMatrixCellStatusIds.Completed,
        StringComparison.Ordinal
      ) && cell.Passed == tests.Count);
    var finalStatus = terminalState == BenchmarkRunStatusIds.Cancelled
      ? BenchmarkRunStatusIds.Cancelled
      : allPassed
        ? BenchmarkRunStatusIds.Passed
        : BenchmarkRunStatusIds.CompletedWithFailures;
    var ranking = harnessResults
      .OrderByDescending(result => result.Score)
      .ThenByDescending(result => result.Passed)
      .ThenBy(result => result.DurationMilliseconds)
      .ThenBy(result => result.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((result, index) => new BenchmarkRankingEntry(
        index + 1,
        result.Harness,
        result.Passed,
        result.Score,
        result.DurationMilliseconds,
        result.Terminality
      ))
      .ToArray();
    var pairRanking = RankPairs(matrixCells);
    var modelRanking = RankAggregate(
      matrixCells,
      cell => cell.Model,
      requestedModels
    );
    var harnessRanking = RankAggregate(
      matrixCells,
      cell => cell.Harness,
      requestedHarnesses
    );
    var identities = await CreateModelIdentitiesAsync(
      models,
      providerEndpoint,
      settings,
      cancellationToken
    );
    string? runtimeVersion;
    try
    {
      runtimeVersion = await _ollamaClient.GetVersionAsync(
        providerEndpoint,
        cancellationToken
      );
    }
    catch (OllamaProviderException)
    {
      runtimeVersion = null;
    }
    int? configuredContext = settings.OllamaRuntime.RoleDefaults.TryGetValue(
      OllamaRuntimeRoleIds.Benchmark,
      out var benchmarkRuntime
    ) ? benchmarkRuntime.TargetContextTokens : null;
    var environment = _environmentSnapshots.Capture(
      "ollama-local",
      runtimeVersion,
      true,
      configuredContext
    );
    var harnessIdentities = harnesses.Select(harness => new BenchmarkHarnessIdentity(
      harness.Adapter.Definition.Id,
      harness.Availability.Version,
      string.IsNullOrWhiteSpace(harness.Availability.Version)
        ? BenchmarkEvidenceStatusIds.Unavailable
        : BenchmarkEvidenceStatusIds.Detected
    )).ToArray();
    var suiteResult = new BenchmarkSuiteRunResult(
      runId,
      models.Length == 1 ? models[0].RequestedName : "matrix",
      models.Length == 1 ? models[0].Installed?.Digest : null,
      ModelProviderIds.OllamaLocal,
      suite.Id,
      suite.Version,
      suite.FixtureId,
      suite.FixtureVersion,
      startedAt,
      endedAt,
      Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
      terminalState,
      finalStatus,
      request.TimeoutSeconds,
      scoreWeights,
      harnessResults,
      ranking,
      SchemaVersion: 4,
      ScoringProfileId: scoringProfileId,
      SelectedModels: requestedModels,
      SelectedHarnesses: requestedHarnesses,
      ModelIdentities: identities,
      Cells: matrixCells,
      PairRanking: pairRanking,
      ModelRanking: modelRanking,
      HarnessRanking: harnessRanking,
      ExecutionOrder: matrixCells.OrderBy(cell => cell.ExecutionOrder)
        .Select(cell => $"{cell.Model}|{cell.Harness}")
        .ToArray(),
      Environment: environment,
      HarnessIdentities: harnessIdentities,
      Configuration: new BenchmarkConfigurationIdentity(
        request.TimeoutSeconds,
        true,
        ConfigurationFingerprint(
          suite,
          request.TimeoutSeconds,
          requestedModels,
          requestedHarnesses,
          configuredContext
        )
      ),
      ScoringProfileVersion: BenchmarkScoringProfileIds.DefaultVersion,
      RawMeasurementsStatus: BenchmarkEvidenceStatusIds.Measured,
      ValidationEvidenceStatus: BenchmarkEvidenceStatusIds.Measured,
      SelectedSuites: resolvedSuites.Selections
    );
    await _results.SaveAsync(suiteResult, CancellationToken.None);
    Publish(progressSink, new BenchmarkProgressEvent(
      runId,
      BenchmarkProgressTypeIds.RunCompleted,
      DateTimeOffset.UtcNow,
      terminalState,
      FinalResult: suiteResult
    ));
    return suiteResult;
  }

  private async Task<BenchmarkHarnessResult> RunHarnessAsync(
    string runId,
    ResolvedBenchmarkHarness harness,
    IReadOnlyList<IBenchmarkTestDefinition> tests,
    InstalledModel model,
    Uri providerEndpoint,
    ApplicationSettings settings,
    int timeoutSeconds,
    BenchmarkScoreWeights scoreWeights,
    CancellationToken cancellationToken,
    IBenchmarkProgressSink? progressSink,
    ConcurrentDictionary<string, BenchmarkHarnessResult> liveResults
  )
  {
    var harnessId = harness.Adapter.Definition.Id;
    var liveKey = CellKey(model.Name, harnessId);
    var startedAt = DateTimeOffset.UtcNow;
    Publish(progressSink, new BenchmarkProgressEvent(
      runId,
      BenchmarkProgressTypeIds.HarnessStarted,
      startedAt,
      BenchmarkLiveStateIds.Running,
      harnessId,
      TotalTests: tests.Count,
      StartedAt: startedAt,
      Model: model.Name
    ));
    if (cancellationToken.IsCancellationRequested)
    {
      var empty = EmptyCancelledHarness(harness, tests.Count);
      liveResults[liveKey] = empty;
      PublishHarnessResult(
        runId,
        model.Name,
        empty,
        startedAt,
        progressSink,
        liveResults,
        true
      );
      return empty;
    }

    var testResults = new List<BenchmarkRunResult>(tests.Count);
    foreach (var test in tests)
    {
      if (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      Publish(progressSink, new BenchmarkProgressEvent(
        runId,
        BenchmarkProgressTypeIds.TestState,
        DateTimeOffset.UtcNow,
        BenchmarkLiveStateIds.Running,
        harnessId,
        test.Metadata.Id,
        TotalTests: tests.Count,
        CompletedTests: testResults.Count,
        PassedTests: CountPassed(testResults),
        StartedAt: DateTimeOffset.UtcNow,
        Model: model.Name
      ));
      var result = await RunTestAsync(
        test,
        model,
        harness.Adapter,
        harness.Availability,
        providerEndpoint,
        settings,
        TimeSpan.FromSeconds(timeoutSeconds),
        scoreWeights,
        cancellationToken,
        progressSink is null
          ? null
          : new BenchmarkProgressContext(
            runId,
            model.Name,
            harnessId,
            test.Metadata.Id,
            progressSink
          )
      );
      testResults.Add(result);
      var partial = CreateHarnessResult(
        harness,
        tests.Count,
        testResults,
        cancellationToken.IsCancellationRequested
      );
      liveResults[liveKey] = partial;
      Publish(progressSink, new BenchmarkProgressEvent(
        runId,
        BenchmarkProgressTypeIds.HarnessProgress,
        DateTimeOffset.UtcNow,
        cancellationToken.IsCancellationRequested
          ? BenchmarkLiveStateIds.Cancelling
          : BenchmarkLiveStateIds.Running,
        harnessId,
        CompletedTests: testResults.Count,
        TotalTests: tests.Count,
        PassedTests: partial.Passed,
        ProvisionalScore: ProvisionalScore(testResults),
        Terminality: partial.Terminality,
        ElapsedMilliseconds: Elapsed(startedAt),
        Model: model.Name
      ));
      PublishRanking(runId, progressSink, liveResults, harnessId, model.Name);
    }

    var final = CreateHarnessResult(
      harness,
      tests.Count,
      testResults,
      cancellationToken.IsCancellationRequested
    );
    liveResults[liveKey] = final;
    PublishHarnessResult(
      runId,
      model.Name,
      final,
      startedAt,
      progressSink,
      liveResults,
      false
    );
    return final;
  }

  private async Task<BenchmarkRunResult> RunTestAsync(
    IBenchmarkTestDefinition test,
    InstalledModel model,
    IAgentHarness harness,
    HarnessAvailability availability,
    Uri providerEndpoint,
    ApplicationSettings settings,
    TimeSpan timeout,
    BenchmarkScoreWeights scoreWeights,
    CancellationToken runCancellationToken,
    BenchmarkProgressContext? progress = null
  )
  {
    var testRunId = Guid.NewGuid().ToString("N");
    var startedAt = DateTimeOffset.UtcNow;
    var workspace = await _workspaces.CreateAsync(testRunId, CancellationToken.None);
    var prompt = string.Join(
      "\n\n",
      test.CreateTurns().OrderBy(turn => turn.Order).Select(turn => turn.Prompt)
    );
    var fingerprint = string.Empty;
    BenchmarkRunResult? result = null;
    var cleanedUp = false;

    try
    {
      await test.PrepareFixtureAsync(workspace.WorkspacePath, runCancellationToken);
      var initialSnapshot = await _workspaces.CaptureAsync(
        workspace.WorkspacePath,
        runCancellationToken
      );
      fingerprint = FixtureFingerprint(initialSnapshot);
      using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
        runCancellationToken
      );
      var effectiveTimeout = string.Equals(
        test.Metadata.Suite,
        BenchmarkSuiteIds.AgentBehavior,
        StringComparison.OrdinalIgnoreCase
      )
        ? TimeSpan.FromSeconds(Math.Min(
          timeout.TotalSeconds,
          test.Metadata.TimeoutSeconds
        ))
        : timeout;
      timeoutSource.CancelAfter(effectiveTimeout);
      BenchmarkHarnessEvidence evidence;
      try
      {
        evidence = await ExecuteHarnessAsync(
          harness,
          test,
          model,
          providerEndpoint,
          workspace,
          settings,
          timeoutSource.Token,
          progress
        );
      }
      catch (OperationCanceledException)
      {
        var cancelled = runCancellationToken.IsCancellationRequested;
        evidence = new BenchmarkHarnessEvidence(
          cancelled
            ? BenchmarkExecutionStatusIds.Cancelled
            : BenchmarkExecutionStatusIds.TimedOut,
          new BenchmarkError(
            cancelled ? "benchmark-cancelled" : "benchmark-timeout",
            cancelled
              ? "The benchmark run was cancelled."
              : $"The harness exceeded the configured {effectiveTimeout.TotalSeconds:0}-second scenario timeout.",
            "harness-execution",
            true
          ),
          string.Empty,
          null,
          null,
          null,
          null,
          null
        );
        progress?.Publish(
          BenchmarkProgressTypeIds.Activity,
          cancelled ? BenchmarkLiveStateIds.Cancelled : BenchmarkLiveStateIds.TimedOut,
          evidence.Error!.Message,
          cancelled ? BenchmarkActivityKindIds.HarnessTerminal : BenchmarkActivityKindIds.Timeout
        );
      }
      catch (HarnessException exception)
      {
        evidence = FailureEvidence(
          exception.Code,
          exception.Message,
          exception.Recoverable
        );
      }
      catch (OllamaProviderException exception)
      {
        evidence = FailureEvidence(
          exception.Stage,
          exception.Message,
          exception.Recoverable
        );
      }
      catch (Exception exception) when (
        exception is IOException
          or InvalidOperationException
          or UnauthorizedAccessException
      )
      {
        evidence = FailureEvidence(
          "benchmark-harness-execution",
          exception.Message,
          true
        );
      }

      progress?.Publish(
        BenchmarkProgressTypeIds.TestState,
        BenchmarkLiveStateIds.HarnessCompleted,
        $"Harness execution {evidence.ExecutionStatus}."
      );
      progress?.Publish(
        BenchmarkProgressTypeIds.TestState,
        BenchmarkLiveStateIds.Validating,
        "Host is validating the observed workspace effect."
      );
      progress?.Publish(
        BenchmarkProgressTypeIds.Activity,
        BenchmarkLiveStateIds.Validating,
        "Deterministic Host validation started.",
        BenchmarkActivityKindIds.HostValidation
      );
      var finalSnapshot = await CaptureFinalSnapshotAsync(
        workspace.WorkspacePath,
        evidence
      );
      var raw = await test.ValidateAsync(
        new BenchmarkValidationContext(
          workspace.WorkspacePath,
          initialSnapshot,
          finalSnapshot.Snapshot,
          finalSnapshot.Evidence.ExecutionStatus,
          finalSnapshot.Evidence.Error,
          finalSnapshot.Evidence
        ),
        CancellationToken.None
      );
      PublishValidation(progress, raw);
      var endedAt = DateTimeOffset.UtcNow;
      result = new BenchmarkRunResult(
        CreateRun(
          testRunId,
          test,
          model,
          harness,
          availability,
          workspace,
          startedAt,
          endedAt,
          finalSnapshot.Evidence.ExecutionStatus,
          prompt,
          fingerprint
        ),
        raw,
        false,
        _scorer.Score(raw, scoreWeights),
        Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds)
      );
    }
    catch (OperationCanceledException)
    {
      result = PreparationFailure(
        testRunId,
        test,
        model,
        harness,
        availability,
        workspace,
        startedAt,
        prompt,
        fingerprint,
        scoreWeights,
        runCancellationToken.IsCancellationRequested
          ? BenchmarkExecutionStatusIds.Cancelled
          : BenchmarkExecutionStatusIds.Failed,
        new BenchmarkError(
          "benchmark-cancelled",
          "The benchmark was cancelled before final validation.",
          "benchmark-preparation",
          true
        )
      );
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
    )
    {
      result = PreparationFailure(
        testRunId,
        test,
        model,
        harness,
        availability,
        workspace,
        startedAt,
        prompt,
        fingerprint,
        scoreWeights,
        BenchmarkExecutionStatusIds.Failed,
        new BenchmarkError(
          "benchmark-preparation-failed",
          exception.Message,
          "benchmark-preparation",
          true
        )
      );
    }
    finally
    {
      try
      {
        cleanedUp = await _workspaces.CleanupAsync(workspace, CancellationToken.None);
      }
      catch (Exception) when (result is not null)
      {
        cleanedUp = false;
      }
    }

    if (result is null)
    {
      throw new InvalidOperationException(
        "The benchmark ended before a structured result could be captured."
      );
    }
    BenchmarkRunResult finalResult;
    if (!cleanedUp)
    {
      var validationFacts = result.RawResult.ValidationFacts is null
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : new Dictionary<string, string>(
          result.RawResult.ValidationFacts,
          StringComparer.Ordinal
        );
      validationFacts["workspaceCleanup"] = "failed";
      var raw = result.RawResult.Error is null
        ? result.RawResult with
        {
          Status = BenchmarkResultStatusIds.Error,
          ExecutionStatus = BenchmarkExecutionStatusIds.Failed,
          HostValidationResult = "error",
          Error = new BenchmarkError(
            "benchmark-cleanup-failed",
            "The benchmark result was captured, but the disposable workspace could not be removed.",
            "workspace-cleanup",
            true
          ),
          ValidationFacts = validationFacts
        }
        : result.RawResult with
        {
          ValidationFacts = validationFacts
        };
      finalResult = result with
      {
        Run = result.Run with
        {
          ExecutionStatus = BenchmarkExecutionStatusIds.Failed,
          EndedAt = DateTimeOffset.UtcNow
        },
        RawResult = raw,
        WorkspaceCleanedUp = false,
        Score = _scorer.Score(raw, scoreWeights)
      };
    }
    else
    {
      finalResult = result with { WorkspaceCleanedUp = true };
    }
    PublishTestTerminal(progress, finalResult);
    return finalResult;
  }

  private async Task<BenchmarkHarnessEvidence> ExecuteHarnessAsync(
    IAgentHarness harness,
    IBenchmarkTestDefinition test,
    InstalledModel model,
    Uri providerEndpoint,
    BenchmarkWorkspace workspace,
    ApplicationSettings settings,
    CancellationToken cancellationToken,
    BenchmarkProgressContext? progress
  )
  {
    int? contextTokens = settings.OllamaRuntime.RoleDefaults.TryGetValue(
      OllamaRuntimeRoleIds.Benchmark,
      out var benchmarkRuntime
    )
      ? benchmarkRuntime.TargetContextTokens
      : null;
    var turns = test.CreateTurns().OrderBy(turn => turn.Order).ToArray();
    if (
      turns.Length != test.Metadata.TurnBudget
      || turns.Select(turn => turn.Order).Distinct().Count() != turns.Length
      || turns.Where((turn, index) => turn.Order != index + 1).Any()
    )
    {
      throw new InvalidOperationException(
        $"Benchmark scenario '{test.Metadata.Id}' does not match its ordered turn budget."
      );
    }
    var nativeSession = new BenchmarkNativeSession();
    var toolTrace = new BenchmarkToolTrace();
    var turnEvidence = new List<BenchmarkTurnEvidence>(turns.Length);
    var hostEvents = new List<BenchmarkHostEvent>();
    var outcomes = new List<BenchmarkHarnessEvidence>(turns.Length);

    foreach (var turn in turns)
    {
      cancellationToken.ThrowIfCancellationRequested();
      progress?.Publish(
        BenchmarkProgressTypeIds.Activity,
        BenchmarkLiveStateIds.Running,
        $"Turn {turn.Order}/{turns.Length}: {turn.Name}.",
        BenchmarkActivityKindIds.Turn,
        turn.Order,
        turns.Length
      );
      var turnStarted = DateTimeOffset.UtcNow;
      var outcome = await ExecuteHarnessTurnAsync(
        harness,
        test,
        turn,
        turn.Order == turns.Length,
        nativeSession,
        toolTrace,
        model,
        providerEndpoint,
        workspace,
        contextTokens,
        progress,
        cancellationToken
      );
      outcomes.Add(outcome);
      turnEvidence.Add(new BenchmarkTurnEvidence(
        turn.Order,
        turn.Name,
        turn.Prompt,
        outcome.ExecutionStatus,
        outcome.FinalReport,
        outcome.ToolCallCount,
        outcome.SurfacedErrorCount,
        outcome.RecoveredErrorCount,
        Math.Max(0, (long)(DateTimeOffset.UtcNow - turnStarted).TotalMilliseconds)
      ));
      if (turn.Order < turns.Length && outcome.ExecutionStatus is
        BenchmarkExecutionStatusIds.Completed or BenchmarkExecutionStatusIds.Partial)
      {
        var hostEvent = await test.AfterTurnAsync(
          turn.Order,
          workspace.WorkspacePath,
          cancellationToken
        );
        if (hostEvent is not null)
        {
          hostEvents.Add(hostEvent);
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.Running,
            hostEvent.Message,
            BenchmarkActivityKindIds.HostMutation,
            turn.Order,
            turns.Length
          );
        }
      }
    }

    var status = AggregateExecutionStatus(outcomes, turns.Length);
    var error = outcomes.Select(outcome => outcome.Error).FirstOrDefault(item => item is not null);
    var finalReport = outcomes.LastOrDefault()?.FinalReport ?? string.Empty;
    return new BenchmarkHarnessEvidence(
      status,
      error,
      finalReport,
      Sum(outcomes, outcome => outcome.ToolCallCount),
      Sum(outcomes, outcome => outcome.SurfacedErrorCount),
      Sum(outcomes, outcome => outcome.RecoveredErrorCount),
      SumLong(outcomes, outcome => outcome.InputTokens),
      SumLong(outcomes, outcome => outcome.OutputTokens),
      turnEvidence,
      hostEvents,
      toolTrace.Events
    );
  }

  private async Task<BenchmarkHarnessEvidence> ExecuteHarnessTurnAsync(
    IAgentHarness harness,
    IBenchmarkTestDefinition test,
    BenchmarkScenarioTurn turn,
    bool releaseWorkspaceAfterTurn,
    BenchmarkNativeSession nativeSession,
    BenchmarkToolTrace toolTrace,
    InstalledModel model,
    Uri providerEndpoint,
    BenchmarkWorkspace workspace,
    int? contextTokens,
    BenchmarkProgressContext? progress,
    CancellationToken cancellationToken
  )
  {
    var execution = new AgentHarnessExecution<BenchmarkHarnessEvidence>(
      nativeCancellationToken => _nativeExecutor.ExecuteAsync(
        test,
        turn,
        nativeSession,
        model,
        providerEndpoint,
        workspace,
        contextTokens,
        progress,
        nativeCancellationToken
      ),
      (transport, transportCancellationToken) => ExecuteExternalHarnessAsync(
        transport,
        _nativeExecutor,
        test,
        turn,
        releaseWorkspaceAfterTurn,
        toolTrace,
        model,
        providerEndpoint,
        workspace,
        contextTokens,
        progress,
        transportCancellationToken
      )
    );
    BenchmarkHarnessEvidence? outcome = null;
    await foreach (var current in harness.ExecuteAsync(execution, cancellationToken))
    {
      if (outcome is not null)
      {
        throw new InvalidOperationException(
          "The benchmark harness returned more than one terminal outcome."
        );
      }
      outcome = current;
    }
    if (
      string.Equals(harness.Definition.Id, HarnessIds.Native, StringComparison.OrdinalIgnoreCase)
      && outcome?.ToolCalls is { Count: > 0 }
    )
    {
      toolTrace.AddRange(outcome.ToolCalls);
    }
    return outcome ?? FailureEvidence(
      "benchmark-terminal-missing",
      "The harness stream ended without a terminal outcome.",
      true
    );
  }

  private static async IAsyncEnumerable<BenchmarkHarnessEvidence> ExecuteExternalHarnessAsync(
    IAgentHarnessTransport harness,
    IBenchmarkNativeExecutor toolExecutor,
    IBenchmarkTestDefinition test,
    BenchmarkScenarioTurn turn,
    bool releaseWorkspaceAfterTurn,
    BenchmarkToolTrace toolTrace,
    InstalledModel model,
    Uri providerEndpoint,
    BenchmarkWorkspace workspace,
    int? contextTokens,
    BenchmarkProgressContext? progress,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    HarnessEvent? terminal = null;
    var report = new StringBuilder();
    var toolIds = new HashSet<string>(StringComparer.Ordinal);
    var anonymousToolCalls = 0;
    var surfacedErrors = 0;
    long? inputTokens = null;
    try
    {
      await foreach (var harnessEvent in harness.StartTurnAsync(
        new HarnessTurnRequest(
          harness.Definition.Id,
          workspace.Id,
          model.Name,
          ModelProviderIds.OllamaLocal,
          workspace.WorkspacePath,
          turn.Prompt,
          "auto",
          providerEndpoint,
          ContextWindowTokens: contextTokens,
          HostCapabilities: BenchmarkHostCapabilities,
          UseMinimalToolInventory: true,
          ReleaseWorkspaceAfterTurn: releaseWorkspaceAfterTurn,
          ReleaseWorkspaceOnCancellation: true
        ),
        cancellationToken
      ))
      {
        if (!string.Equals(
          harnessEvent.HarnessId,
          harness.Definition.Id,
          StringComparison.OrdinalIgnoreCase
        ))
        {
          throw new HarnessException(
            "benchmark-harness-identity-mismatch",
            "The harness returned an event with an invalid identity.",
            $"Expected '{harness.Definition.Id}', received '{harnessEvent.HarnessId}'.",
            false,
            harnessId: harness.Definition.Id
          );
        }
        if (string.Equals(harnessEvent.Type, "assistant.delta", StringComparison.Ordinal))
        {
          report.Append(harnessEvent.Delta);
        }
        if (string.Equals(harnessEvent.Type, "tool.started", StringComparison.Ordinal))
        {
          if (harnessEvent.ItemId is null)
          {
            anonymousToolCalls++;
          }
          else
          {
            toolIds.Add(harnessEvent.ItemId);
          }
          toolTrace.Start(
            turn.Order,
            harnessEvent.ItemId ?? $"anonymous-{anonymousToolCalls}",
            harnessEvent.Tool ?? "unknown",
            ToolPath(harnessEvent.Arguments, harnessEvent.Paths, workspace.WorkspacePath)
          );
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.Running,
            harnessEvent.Tool is null
              ? "Harness tool execution started."
              : $"Executing {harnessEvent.Tool}.",
            BenchmarkNativeExecutor.ActivityKind(harnessEvent.Tool ?? string.Empty)
          );
        }
        if (
          string.Equals(harnessEvent.Type, "tool.completed", StringComparison.Ordinal)
          || string.Equals(harnessEvent.Type, "tool.succeeded", StringComparison.Ordinal)
        )
        {
          toolTrace.Complete(
            turn.Order,
            harnessEvent.ItemId ?? harnessEvent.ToolCallId,
            harnessEvent.Tool ?? "unknown",
            ToolPath(harnessEvent.Arguments, harnessEvent.Paths, workspace.WorkspacePath)
          );
        }
        if (
          string.Equals(harnessEvent.Type, "error", StringComparison.Ordinal)
          || string.Equals(harnessEvent.Type, "tool.failed", StringComparison.Ordinal)
          || (!harnessEvent.IsTerminal && harnessEvent.ErrorCode is not null)
        )
        {
          surfacedErrors++;
          toolTrace.Fail(
            turn.Order,
            harnessEvent.ItemId ?? harnessEvent.ToolCallId,
            harnessEvent.Tool ?? "unknown",
            ToolPath(harnessEvent.Arguments, harnessEvent.Paths, workspace.WorkspacePath),
            harnessEvent.ErrorCode
          );
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.Running,
            harnessEvent.ErrorCode is null
              ? "Harness surfaced a recoverable execution error."
              : $"{harnessEvent.ErrorCode}: {harnessEvent.Message}",
            BenchmarkActivityKindIds.Tool
          );
        }
        if (harnessEvent.ContextInputTokens.HasValue)
        {
          inputTokens = Math.Max(inputTokens ?? 0, harnessEvent.ContextInputTokens.Value);
        }
        if (
          string.Equals(harnessEvent.Type, "approval.requested", StringComparison.Ordinal)
          && harnessEvent.ApprovalId is not null
        )
        {
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.Running,
            "Harness requested approval; Host policy resolved it.",
            BenchmarkActivityKindIds.Approval
          );
          await harness.ResolveApprovalAsync(
            harnessEvent.ApprovalId,
            CanApprove(test, workspace.WorkspacePath, harnessEvent),
            cancellationToken
          );
        }
        else if (
          string.Equals(harnessEvent.Type, "host-tool.requested", StringComparison.Ordinal)
          && harnessEvent.ToolCallId is not null
        )
        {
          var path = ToolPath(
            harnessEvent.Arguments,
            harnessEvent.Paths,
            workspace.WorkspacePath
          );
          toolTrace.Start(
            turn.Order,
            harnessEvent.ToolCallId,
            harnessEvent.Tool ?? "unknown",
            path
          );
          try
          {
            if (harnessEvent.Tool is null || harnessEvent.Arguments is null)
            {
              throw new BenchmarkNativeToolException(
                "benchmark-host-tool-invalid",
                "The harness omitted the structured Host tool name or arguments."
              );
            }
            progress?.Publish(
              BenchmarkProgressTypeIds.Activity,
              BenchmarkLiveStateIds.Running,
              $"Executing Host tool {harnessEvent.Tool}.",
              BenchmarkNativeExecutor.ActivityKind(harnessEvent.Tool)
            );
            var output = await toolExecutor.ExecuteToolAsync(
              workspace.WorkspacePath,
              harnessEvent.Tool,
              harnessEvent.Arguments.Value,
              cancellationToken
            );
            await harness.ResolveToolCallAsync(
              harnessEvent.ToolCallId,
              true,
              output,
              cancellationToken
            );
            toolTrace.Complete(
              turn.Order,
              harnessEvent.ToolCallId,
              harnessEvent.Tool,
              path
            );
          }
          catch (BenchmarkNativeToolException exception)
          {
            surfacedErrors++;
            progress?.Publish(
              BenchmarkProgressTypeIds.Activity,
              BenchmarkLiveStateIds.Running,
              $"{exception.Code}: {exception.Message}",
              BenchmarkActivityKindIds.Tool
            );
            await harness.ResolveToolCallAsync(
              harnessEvent.ToolCallId,
              false,
              $"{exception.Code}: {exception.Message}",
              cancellationToken
            );
            toolTrace.Fail(
              turn.Order,
              harnessEvent.ToolCallId,
              harnessEvent.Tool ?? "unknown",
              path,
              exception.Code
            );
          }
        }
        if (harnessEvent.IsTerminal)
        {
          terminal = harnessEvent;
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.HarnessCompleted,
            $"Harness terminal state: {harnessEvent.TerminalState}.",
            BenchmarkActivityKindIds.HarnessTerminal
          );
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      await harness.CancelTurnAsync(workspace.Id, CancellationToken.None);
      throw;
    }

    if (terminal is null)
    {
      yield return FailureEvidence(
        "benchmark-terminal-missing",
        "The harness stream ended without a terminal event.",
        true
      );
      yield break;
    }
    if (surfacedErrors > 0 && terminal.TerminalState == HarnessTerminalState.Completed)
    {
      progress?.Publish(
        BenchmarkProgressTypeIds.Activity,
        BenchmarkLiveStateIds.HarnessCompleted,
        $"Harness recovered after {surfacedErrors} surfaced error(s).",
        BenchmarkActivityKindIds.RecoveredError
      );
    }
    yield return BenchmarkHarnessEvidence.FromTerminal(
      terminal,
      report.ToString().Trim(),
      toolIds.Count + anonymousToolCalls,
      surfacedErrors,
      inputTokens,
      null,
      ToolCalls: toolTrace.Events.Where(item => item.Turn == turn.Order).ToArray()
    );
  }

  private static bool CanApprove(
    IBenchmarkTestDefinition test,
    string workspacePath,
    HarnessEvent harnessEvent
  )
  {
    if (!harnessEvent.ApprovalCanBeMapped)
    {
      return false;
    }
    if (!harnessEvent.Destructive)
    {
      return true;
    }
    if (
      !string.Equals(test.Metadata.Id, BenchmarkIds.FileSystemDelete001, StringComparison.Ordinal)
      || harnessEvent.Paths is not { Count: > 0 }
    )
    {
      return false;
    }
    try
    {
      var root = Path.GetFullPath(workspacePath);
      return harnessEvent.Paths.All(path =>
      {
        var full = Path.IsPathRooted(path)
          ? Path.GetFullPath(path)
          : Path.GetFullPath(Path.Combine(root, path));
        var relative = BenchmarkWorkspaceFactory.NormalizeRelative(
          Path.GetRelativePath(root, full)
        );
        return string.Equals(
          relative,
          "fixture/delete.txt",
          OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal
        );
      });
    }
    catch (Exception exception) when (
      exception is ArgumentException
        or NotSupportedException
        or PathTooLongException
    )
    {
      return false;
    }
  }

  private static string AggregateExecutionStatus(
    IReadOnlyList<BenchmarkHarnessEvidence> outcomes,
    int expectedTurns
  )
  {
    if (outcomes.Count != expectedTurns)
    {
      return BenchmarkExecutionStatusIds.Failed;
    }
    foreach (var status in new[]
    {
      BenchmarkExecutionStatusIds.Cancelled,
      BenchmarkExecutionStatusIds.TimedOut,
      BenchmarkExecutionStatusIds.Unavailable,
      BenchmarkExecutionStatusIds.Failed,
      BenchmarkExecutionStatusIds.Partial
    })
    {
      if (outcomes.Any(outcome => string.Equals(
        outcome.ExecutionStatus,
        status,
        StringComparison.Ordinal
      )))
      {
        return status;
      }
    }
    return BenchmarkExecutionStatusIds.Completed;
  }

  private static int? Sum(
    IReadOnlyList<BenchmarkHarnessEvidence> outcomes,
    Func<BenchmarkHarnessEvidence, int?> selector
  )
  {
    var values = outcomes.Select(selector).Where(value => value.HasValue).ToArray();
    return values.Length == 0 ? null : values.Sum(value => value!.Value);
  }

  private static long? SumLong(
    IReadOnlyList<BenchmarkHarnessEvidence> outcomes,
    Func<BenchmarkHarnessEvidence, long?> selector
  )
  {
    var values = outcomes.Select(selector).Where(value => value.HasValue).ToArray();
    return values.Length == 0 ? null : values.Sum(value => value!.Value);
  }

  private static string? ToolPath(
    JsonElement? arguments,
    IReadOnlyList<string>? paths,
    string workspacePath
  )
  {
    if (paths is { Count: 1 })
    {
      return NormalizeToolPath(paths[0], workspacePath);
    }
    if (arguments is not { ValueKind: JsonValueKind.Object } value)
    {
      return null;
    }
    if (
      value.TryGetProperty("path", out var path)
      && path.ValueKind == JsonValueKind.String
    )
    {
      return NormalizeToolPath(path.GetString(), workspacePath);
    }
    if (
      value.TryGetProperty("paths", out var pathList)
      && pathList.ValueKind == JsonValueKind.Array
      && pathList.GetArrayLength() == 1
    )
    {
      return NormalizeToolPath(pathList[0].GetString(), workspacePath);
    }
    if (
      value.TryGetProperty("files", out var files)
      && files.ValueKind == JsonValueKind.Array
      && files.GetArrayLength() == 1
      && files[0].TryGetProperty("path", out var filePath)
    )
    {
      return NormalizeToolPath(filePath.GetString(), workspacePath);
    }
    return null;
  }

  private static string? NormalizeToolPath(string? path, string workspacePath)
  {
    if (string.IsNullOrWhiteSpace(path))
    {
      return null;
    }
    try
    {
      if (Path.IsPathRooted(path))
      {
        var relative = Path.GetRelativePath(
          Path.GetFullPath(workspacePath),
          Path.GetFullPath(path)
        );
        if (
          !Path.IsPathRooted(relative)
          && relative != ".."
          && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
          && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
          path = relative;
        }
      }
    }
    catch (Exception exception) when (
      exception is ArgumentException or NotSupportedException or PathTooLongException
    )
    {
      return null;
    }
    return path.Replace('\\', '/');
  }

  private async Task<ResolvedBenchmarkHarness> ResolveHarnessAsync(
    string harnessId,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(harnessId))
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-required",
        "At least one harness must be selected.",
        "harnesses"
      );
    }
    if (!_harnesses.TryGetAdapter(harnessId, out var harness))
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-unknown",
        $"Harness '{harnessId}' is not registered.",
        "harnesses"
      );
    }
    var availability = await harness.GetAvailabilityAsync(cancellationToken);
    if (!availability.Available)
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-unavailable",
        availability.Message ?? $"Harness '{harness.Definition.Id}' is unavailable.",
        "harnesses"
      );
    }
    return new ResolvedBenchmarkHarness(harness, availability);
  }

  private async Task<ResolvedBenchmarkHarness> ResolveHarnessStatusAsync(
    string harnessId,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(harnessId) || !_harnesses.TryGetAdapter(harnessId, out var harness))
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-unknown",
        $"Harness '{harnessId}' is not registered.",
        "harnesses"
      );
    }
    return new ResolvedBenchmarkHarness(
      harness,
      await harness.GetAvailabilityAsync(cancellationToken)
    );
  }

  private async Task<InstalledModel> ResolveModelAsync(
    string model,
    Uri providerEndpoint,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(model))
    {
      throw new BenchmarkRequestException(
        "benchmark-model-required",
        "A model must be selected.",
        "model"
      );
    }
    var installed = await _ollamaClient.GetModelsAsync(
      providerEndpoint,
      cancellationToken
    );
    var selected = installed.FirstOrDefault(candidate =>
      string.Equals(candidate.Name, model.Trim(), StringComparison.OrdinalIgnoreCase)
      && string.Equals(candidate.Provider, ModelProviderIds.OllamaLocal, StringComparison.Ordinal)
    );
    if (selected is null)
    {
      throw new BenchmarkRequestException(
        "benchmark-model-unavailable",
        $"Ollama Local model '{model}' is unavailable in the configured provider registry.",
        "model"
      );
    }
    return selected;
  }

  private static IReadOnlyList<string> NormalizeHarnesses(
    IReadOnlyList<string>? harnesses
  )
  {
    if (harnesses is null || harnesses.Count == 0)
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-required",
        "Select at least one benchmark harness.",
        "harnesses"
      );
    }
    var normalized = harnesses
      .Where(harness => !string.IsNullOrWhiteSpace(harness))
      .Select(harness => harness.Trim().ToLowerInvariant())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    if (normalized.Length != harnesses.Count)
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-selection-invalid",
        "Benchmark harness selections must be non-empty and unique.",
        "harnesses"
      );
    }
    return normalized;
  }

  private static IReadOnlyList<string> NormalizeModels(BenchmarkSuiteRunRequest request)
  {
    var selections = request.Models is { Count: > 0 }
      ? request.Models
      : [request.Model];
    var normalized = selections
      .Where(model => !string.IsNullOrWhiteSpace(model))
      .Select(model => model.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();
    if (normalized.Length == 0)
    {
      throw new BenchmarkRequestException(
        "benchmark-model-required",
        "Select at least one benchmark model.",
        "models"
      );
    }
    if (normalized.Length != selections.Count)
    {
      throw new BenchmarkRequestException(
        "benchmark-model-selection-invalid",
        "Benchmark model selections must be non-empty and unique.",
        "models"
      );
    }
    return normalized;
  }

  private static string NormalizeScoringProfileId(string? profileId)
  {
    var normalized = string.IsNullOrWhiteSpace(profileId)
      ? BenchmarkScoringProfileIds.Default
      : profileId.Trim().ToLowerInvariant();
    if (normalized is not BenchmarkScoringProfileIds.Default
      and not BenchmarkScoringProfileIds.Custom)
    {
      throw new BenchmarkRequestException(
        "benchmark-scoring-profile-invalid",
        $"Benchmark scoring profile '{profileId}' is unavailable.",
        "scoringProfileId"
      );
    }
    return normalized;
  }

  private static CellCompatibility Compatibility(
    ResolvedBenchmarkModel model,
    ResolvedBenchmarkHarness harness
  )
  {
    if (model.Installed is null)
    {
      return new CellCompatibility(
        BenchmarkMatrixCellStatusIds.Unavailable,
        $"Ollama Local model '{model.RequestedName}' is unavailable."
      );
    }
    if (!harness.Availability.Available)
    {
      return new CellCompatibility(
        BenchmarkMatrixCellStatusIds.Unavailable,
        harness.Availability.Message ?? $"Harness '{harness.Adapter.Definition.Id}' is unavailable."
      );
    }
    var providers = harness.Adapter.Definition.SupportedProviders;
    if (providers is { Count: > 0 } && !providers.Contains(
      model.Installed.Provider,
      StringComparer.OrdinalIgnoreCase
    ))
    {
      return new CellCompatibility(
        BenchmarkMatrixCellStatusIds.Unsupported,
        $"Harness '{harness.Adapter.Definition.Id}' does not support provider '{model.Installed.Provider}'."
      );
    }
    return new CellCompatibility(BenchmarkMatrixCellStatusIds.Available, null);
  }

  private static BenchmarkMatrixCellResult CreateNonExecutableCell(
    int executionOrder,
    ResolvedBenchmarkModel model,
    ResolvedBenchmarkHarness harness,
    CellCompatibility compatibility,
    int totalTests,
    string? compatibilityStatus = null
  )
  {
    return new BenchmarkMatrixCellResult(
      executionOrder,
      model.RequestedName,
      model.Installed?.Digest,
      model.Installed?.Provider ?? ModelProviderIds.OllamaLocal,
      harness.Adapter.Definition.Id,
      harness.Availability.Version,
      compatibility.Status,
      compatibilityStatus ?? compatibility.Status,
      compatibility.Message,
      0,
      totalTests,
      0,
      0,
      0,
      0,
      null,
      null,
      null,
      null
    );
  }

  private static BenchmarkMatrixCellResult CreateCell(
    int executionOrder,
    InstalledModel model,
    BenchmarkHarnessResult result
  )
  {
    var status = string.Equals(
      result.TerminalState,
      BenchmarkRunStatusIds.Cancelled,
      StringComparison.Ordinal
    )
      ? BenchmarkMatrixCellStatusIds.Cancelled
      : result.Tests.Any(test => string.Equals(
        test.RawResult.ExecutionStatus,
        BenchmarkExecutionStatusIds.TimedOut,
        StringComparison.Ordinal
      ))
        ? BenchmarkMatrixCellStatusIds.TimedOut
        : result.Tests.Any(test => test.RawResult.ExecutionStatus is
          BenchmarkExecutionStatusIds.Failed or BenchmarkExecutionStatusIds.Unavailable)
          ? BenchmarkMatrixCellStatusIds.Failed
          : BenchmarkMatrixCellStatusIds.Completed;
    var message = result.Tests.Select(test => test.RawResult.Error?.Message)
      .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    int AverageScore(Func<BenchmarkRunResult, int?> selector)
    {
      var values = result.Tests.Select(selector).Where(value => value.HasValue).ToArray();
      return values.Length == 0
        ? 0
        : (int)Math.Round(values.Average(value => value!.Value), MidpointRounding.AwayFromZero);
    }
    int? AverageMetric(Func<BenchmarkRunResult, int?> selector)
    {
      var values = result.Tests.Select(selector).Where(value => value.HasValue).ToArray();
      return values.Length == 0
        ? null
        : (int)Math.Round(values.Average(value => value!.Value), MidpointRounding.AwayFromZero);
    }
    return new BenchmarkMatrixCellResult(
      executionOrder,
      model.Name,
      model.Digest,
      model.Provider,
      result.Harness,
      result.HarnessVersion,
      status,
      BenchmarkMatrixCellStatusIds.Available,
      message,
      result.Passed,
      result.Total,
      result.Score,
      result.DurationMilliseconds,
      result.Terminality,
      AverageScore(test => test.Score?.Correctness),
      AverageMetric(test => test.RawResult.BehaviorMetrics?.Recovery),
      AverageMetric(test => test.RawResult.BehaviorMetrics?.Convergence),
      AverageMetric(test => test.RawResult.BehaviorMetrics?.Hygiene),
      result
    );
  }

  private async Task<IReadOnlyList<BenchmarkModelIdentity>> CreateModelIdentitiesAsync(
    IReadOnlyList<ResolvedBenchmarkModel> models,
    Uri providerEndpoint,
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    int? configuredContext = settings.OllamaRuntime.RoleDefaults.TryGetValue(
      OllamaRuntimeRoleIds.Benchmark,
      out var benchmarkRuntime
    ) ? benchmarkRuntime.TargetContextTokens : null;
    IReadOnlyList<OllamaRunningModel> runningModels;
    try
    {
      runningModels = await _ollamaClient.GetRunningModelsAsync(
        providerEndpoint,
        cancellationToken
      );
    }
    catch (OllamaProviderException)
    {
      runningModels = [];
    }
    var identities = new List<BenchmarkModelIdentity>(models.Count);
    foreach (var resolved in models)
    {
      OllamaModelMetadata? metadata = null;
      if (resolved.Installed is not null)
      {
        try
        {
          metadata = await _ollamaClient.GetModelMetadataAsync(
            providerEndpoint,
            resolved.Installed.Name,
            cancellationToken
          );
        }
        catch (OllamaProviderException)
        {
        }
      }
      var running = runningModels.FirstOrDefault(candidate => string.Equals(
        candidate.Name,
        resolved.RequestedName,
        StringComparison.OrdinalIgnoreCase
      ));
      identities.Add(new BenchmarkModelIdentity(
        resolved.RequestedName,
        resolved.Installed?.Digest,
        resolved.Installed?.Provider ?? ModelProviderIds.OllamaLocal,
        resolved.Installed?.SizeBytes,
        resolved.Installed?.ModifiedAt,
        metadata?.Quantization,
        metadata?.DeclaredContextTokens,
        configuredContext,
        metadata?.ParameterSize,
        metadata?.Format,
        metadata?.Family,
        running?.ContextLength
      ));
    }
    return identities;
  }

  private static string ConfigurationFingerprint(
    BenchmarkSuiteMetadata suite,
    int timeoutSeconds,
    IReadOnlyList<string> models,
    IReadOnlyList<string> harnesses,
    int? configuredContextTokens
  )
  {
    var canonical = string.Join("\n", new[]
    {
      $"suite={suite.Id}:{suite.Version}",
      $"fixture={suite.FixtureId}:{suite.FixtureVersion}",
      $"tests={string.Join('|', suite.Tests.Select(test => $"{test.Suite}:{test.SuiteVersion}:{test.Id}:{test.Version}"))}",
      $"timeout={timeoutSeconds}",
      $"context={configuredContextTokens?.ToString() ?? "unavailable"}",
      "sequential=true",
      $"models={string.Join('|', models)}",
      $"harnesses={string.Join('|', harnesses)}"
    });
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
      .ToLowerInvariant();
  }

  private ResolvedBenchmarkSuites ResolveSuites(BenchmarkSuiteRunRequest request)
  {
    var requested = request.Suites is { Count: > 0 }
      ? request.Suites
      : [new BenchmarkSuiteSelection(request.SuiteId, request.SuiteVersion)];
    var selections = new List<BenchmarkSuiteSelection>(requested.Count);
    var definitions = new List<IBenchmarkTestDefinition>();
    var metadata = new List<BenchmarkSuiteMetadata>(requested.Count);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var selection in requested)
    {
      if (
        string.IsNullOrWhiteSpace(selection.Id)
        || !seen.Add($"{selection.Id.Trim()}|{selection.Version}")
      )
      {
        throw new BenchmarkRequestException(
          "benchmark-suite-selection-invalid",
          "Benchmark test groups must be non-empty and unique.",
          "suites"
        );
      }
      if (!_tests.TryGetSuite(
        selection.Id,
        selection.Version,
        out var suite,
        out var tests
      ))
      {
        throw new BenchmarkRequestException(
          "benchmark-suite-unknown",
          $"Benchmark suite '{selection.Id}' version {selection.Version} is unavailable.",
          "suites"
        );
      }
      selections.Add(new BenchmarkSuiteSelection(suite.Id, suite.Version));
      metadata.Add(suite);
      definitions.AddRange(tests);
    }
    if (selections.Count == 1)
    {
      return new ResolvedBenchmarkSuites(
        metadata[0],
        definitions,
        selections
      );
    }
    return new ResolvedBenchmarkSuites(
      new BenchmarkSuiteMetadata(
        BenchmarkSuiteIds.Combined,
        BenchmarkSuiteIds.CombinedVersion,
        "Selected benchmark tests",
        BenchmarkSuiteIds.CombinedFixtureId,
        BenchmarkSuiteIds.CombinedFixtureVersion,
        definitions.Select(definition => definition.Metadata).ToArray()
      ),
      definitions,
      selections
    );
  }

  private static IReadOnlyList<BenchmarkMatrixRankingEntry> RankPairs(
    IReadOnlyList<BenchmarkMatrixCellResult> cells
  )
  {
    return cells
      .OrderByDescending(cell => cell.Score)
      .ThenByDescending(cell => cell.Passed)
      .ThenBy(cell => cell.DurationMilliseconds)
      .ThenBy(cell => cell.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(cell => cell.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((cell, index) => new BenchmarkMatrixRankingEntry(
        index + 1,
        cell.Model,
        cell.Harness,
        cell.Passed,
        cell.Score,
        cell.DurationMilliseconds,
        cell.Terminality,
        cell.Status
      ))
      .ToArray();
  }

  private static IReadOnlyList<BenchmarkAggregateRankingEntry> RankAggregate(
    IReadOnlyList<BenchmarkMatrixCellResult> cells,
    Func<BenchmarkMatrixCellResult, string> selector,
    IReadOnlyList<string> identities
  )
  {
    var summaries = identities.Select(id =>
    {
      var matching = cells.Where(cell => string.Equals(
        selector(cell),
        id,
        StringComparison.OrdinalIgnoreCase
      )).ToArray();
      var divisor = Math.Max(1, matching.Length);
      return new
      {
        Id = id,
        Completed = matching.Count(cell => string.Equals(
          cell.Status,
          BenchmarkMatrixCellStatusIds.Completed,
          StringComparison.Ordinal
        )),
        Total = matching.Length,
        Passed = matching.Sum(cell => cell.Passed),
        Score = decimal.Round(
          matching.Sum(cell => cell.Score) / divisor,
          2,
          MidpointRounding.AwayFromZero
        ),
        Duration = matching.Sum(cell => Math.Max(0, cell.DurationMilliseconds)),
        Terminality = (int)Math.Round(
          matching.Sum(cell => cell.Terminality) / (decimal)divisor,
          MidpointRounding.AwayFromZero
        )
      };
    }).OrderByDescending(item => item.Score)
      .ThenByDescending(item => item.Passed)
      .ThenBy(item => item.Duration)
      .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
      .ToArray();
    return summaries.Select((item, index) => new BenchmarkAggregateRankingEntry(
      index + 1,
      item.Id,
      item.Completed,
      item.Total,
      item.Passed,
      item.Score,
      item.Duration,
      item.Terminality
    )).ToArray();
  }

  private static string CellKey(string model, string harness)
  {
    return $"{model}\u001f{harness}";
  }

  private static string CellModel(string key)
  {
    var separator = key.IndexOf('\u001f');
    return separator < 0 ? string.Empty : key[..separator];
  }

  private static void RequirePermission(bool granted)
  {
    if (!granted)
    {
      throw new BenchmarkRequestException(
        "benchmark-model-permission-required",
        "Explicit permission is required immediately before benchmark model execution.",
        "modelExecutionPermissionGranted"
      );
    }
  }

  private static int CountPassed(IEnumerable<BenchmarkRunResult> tests)
  {
    return tests.Count(test => string.Equals(
      test.RawResult.Status,
      BenchmarkResultStatusIds.Pass,
      StringComparison.Ordinal
    ));
  }

  private static decimal? ProvisionalScore(IReadOnlyCollection<BenchmarkRunResult> tests)
  {
    if (tests.Count == 0)
    {
      return null;
    }
    return decimal.Round(
      tests.Sum(test => test.Score?.Total ?? 0m) / tests.Count,
      2,
      MidpointRounding.AwayFromZero
    );
  }

  private static long Elapsed(DateTimeOffset startedAt)
  {
    return Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
  }

  private static void Publish(
    IBenchmarkProgressSink? progressSink,
    BenchmarkProgressEvent progressEvent
  )
  {
    if (progressSink is null)
    {
      return;
    }
    try
    {
      progressSink.Publish(progressEvent);
    }
    catch
    {
      // Live observability is deliberately non-authoritative.
    }
  }

  private static void PublishValidation(
    BenchmarkProgressContext? progress,
    BenchmarkRawResult raw
  )
  {
    if (progress is null)
    {
      return;
    }
    var checks = new Dictionary<string, string>(StringComparer.Ordinal);
    if (raw.ValidationFacts is not null)
    {
      foreach (var fact in raw.ValidationFacts)
      {
        checks[fact.Key] = fact.Value;
      }
    }
    checks["Host validation"] = raw.HostValidationResult;
    checks["Workspace containment"] = raw.ContainmentAccuracy == 100 ? "PASS" : "FAIL";
    Publish(progress.Sink, new BenchmarkProgressEvent(
      progress.RunId,
      BenchmarkProgressTypeIds.Validation,
      DateTimeOffset.UtcNow,
      BenchmarkLiveStateIds.Validating,
      progress.Harness,
      progress.TestId,
      "Deterministic Host validation completed.",
      ValidationChecks: checks,
      Model: progress.Model
    ));
  }

  private static void PublishTestTerminal(
    BenchmarkProgressContext? progress,
    BenchmarkRunResult result
  )
  {
    if (progress is null)
    {
      return;
    }
    var state = result.RawResult.ExecutionStatus switch
    {
      BenchmarkExecutionStatusIds.TimedOut => BenchmarkLiveStateIds.TimedOut,
      BenchmarkExecutionStatusIds.Cancelled => BenchmarkLiveStateIds.Cancelled,
      _ when string.Equals(
        result.RawResult.Status,
        BenchmarkResultStatusIds.Pass,
        StringComparison.Ordinal
      ) => BenchmarkLiveStateIds.Passed,
      _ => BenchmarkLiveStateIds.Failed
    };
    Publish(progress.Sink, new BenchmarkProgressEvent(
      progress.RunId,
      BenchmarkProgressTypeIds.TestState,
      DateTimeOffset.UtcNow,
      state,
      progress.Harness,
      progress.TestId,
      result.RawResult.Error?.Message,
      ElapsedMilliseconds: result.DurationMilliseconds,
      TestResult: result,
      Model: progress.Model
    ));
  }

  private static void PublishHarnessResult(
    string runId,
    string model,
    BenchmarkHarnessResult result,
    DateTimeOffset startedAt,
    IBenchmarkProgressSink? progressSink,
    ConcurrentDictionary<string, BenchmarkHarnessResult> liveResults,
    bool skipped
  )
  {
    var state = string.Equals(
      result.TerminalState,
      BenchmarkRunStatusIds.Cancelled,
      StringComparison.Ordinal
    )
      ? BenchmarkLiveStateIds.Cancelled
      : BenchmarkLiveStateIds.Completed;
    Publish(progressSink, new BenchmarkProgressEvent(
      runId,
      BenchmarkProgressTypeIds.HarnessCompleted,
      DateTimeOffset.UtcNow,
      state,
      result.Harness,
      Message: skipped ? "Harness was cancelled before its first test." : null,
      CompletedTests: result.Tests.Count,
      TotalTests: result.Total,
      PassedTests: result.Passed,
      ProvisionalScore: ProvisionalScore(result.Tests),
      Terminality: result.Terminality,
      ElapsedMilliseconds: Elapsed(startedAt),
      Model: model
    ));
    PublishRanking(runId, progressSink, liveResults, result.Harness, model);
  }

  private static void PublishRanking(
    string runId,
    IBenchmarkProgressSink? progressSink,
    ConcurrentDictionary<string, BenchmarkHarnessResult> liveResults,
    string changedHarness,
    string changedModel
  )
  {
    var snapshot = liveResults.Select(pair => (
      Key: pair.Key,
      Model: CellModel(pair.Key),
      Result: pair.Value
    )).ToArray();
    var ranks = snapshot
      .Where(item => item.Result.Tests.Count > 0)
      .OrderByDescending(item => ProvisionalScore(item.Result.Tests))
      .ThenByDescending(item => item.Result.Passed)
      .ThenBy(item => item.Result.DurationMilliseconds)
      .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(item => item.Result.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((item, index) => (item.Key, Rank: index + 1))
      .ToDictionary(item => item.Key, item => item.Rank, StringComparer.OrdinalIgnoreCase);
    var ranking = snapshot.Select(item =>
    {
      var result = item.Result;
      var cancelled = string.Equals(
        result.TerminalState,
        BenchmarkRunStatusIds.Cancelled,
        StringComparison.Ordinal
      );
      var state = cancelled
        ? BenchmarkLiveStateIds.Cancelled
        : result.Tests.Count >= result.Total
          ? BenchmarkLiveStateIds.Completed
          : result.Tests.Count > 0
            ? BenchmarkLiveStateIds.Running
            : BenchmarkLiveStateIds.Pending;
      return new BenchmarkLiveRankingEntry(
        ranks.TryGetValue(item.Key, out var rank) ? rank : null,
        result.Harness,
        result.Tests.Count,
        result.Total,
        result.Passed,
        ProvisionalScore(result.Tests),
        result.DurationMilliseconds,
        result.Terminality,
        state,
        item.Model
      );
    }).OrderBy(entry => entry.Rank ?? int.MaxValue)
      .ThenBy(entry => entry.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(entry => entry.Harness, StringComparer.OrdinalIgnoreCase)
      .ToArray();
    Publish(progressSink, new BenchmarkProgressEvent(
      runId,
      BenchmarkProgressTypeIds.Ranking,
      DateTimeOffset.UtcNow,
      BenchmarkLiveStateIds.Running,
      changedHarness,
      Message: "Ranking is provisional while any harness remains unfinished.",
      Ranking: ranking,
      Model: changedModel
    ));
  }

  private static string NormalizeRunId(string? clientRunId)
  {
    if (string.IsNullOrWhiteSpace(clientRunId))
    {
      return Guid.NewGuid().ToString("N");
    }
    if (!Guid.TryParse(clientRunId, out var parsed))
    {
      throw new BenchmarkRequestException(
        "benchmark-run-id-invalid",
        "Client run id must be a UUID.",
        "clientRunId"
      );
    }
    return parsed.ToString("N");
  }

  private static string FixtureFingerprint(BenchmarkWorkspaceSnapshot snapshot)
  {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var entry in snapshot.Entries.Values.OrderBy(
      entry => entry.RelativePath,
      BenchmarkWorkspaceFactory.PathComparer
    ))
    {
      hash.AppendData(Encoding.UTF8.GetBytes(
        $"{entry.RelativePath}\0{entry.Kind}\0{entry.ContentHash}\n"
      ));
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
  }

  private static BenchmarkHarnessResult CreateHarnessResult(
    ResolvedBenchmarkHarness harness,
    int totalTests,
    IReadOnlyList<BenchmarkRunResult> tests,
    bool cancelled
  )
  {
    var duration = Math.Max(
      0,
      tests.Sum(test => test.DurationMilliseconds)
    );
    var passed = tests.Count(test => string.Equals(
      test.RawResult.Status,
      BenchmarkResultStatusIds.Pass,
      StringComparison.Ordinal
    ));
    var terminality = tests.Sum(test => test.RawResult.BehaviorMetrics?.Terminality
      ?? (string.Equals(
        test.RawResult.ExecutionStatus,
        BenchmarkExecutionStatusIds.Completed,
        StringComparison.Ordinal
      ) ? 100 : 0));
    var score = tests.Sum(test => test.Score?.Total ?? 0m) / totalTests;
    return new BenchmarkHarnessResult(
      harness.Adapter.Definition.Id,
      harness.Availability.Version,
      passed,
      totalTests,
      decimal.Round(score, 2, MidpointRounding.AwayFromZero),
      duration,
      (int)Math.Round(terminality / (decimal)totalTests, MidpointRounding.AwayFromZero),
      cancelled ? BenchmarkRunStatusIds.Cancelled : BenchmarkRunStatusIds.Completed,
      tests
    );
  }

  private static BenchmarkHarnessResult EmptyCancelledHarness(
    ResolvedBenchmarkHarness harness,
    int totalTests
  )
  {
    return new BenchmarkHarnessResult(
      harness.Adapter.Definition.Id,
      harness.Availability.Version,
      0,
      totalTests,
      0,
      0,
      0,
      BenchmarkRunStatusIds.Cancelled,
      []
    );
  }

  private static BenchmarkHarnessResult EmptyPendingHarness(
    ResolvedBenchmarkHarness harness,
    int totalTests
  )
  {
    return new BenchmarkHarnessResult(
      harness.Adapter.Definition.Id,
      harness.Availability.Version,
      0,
      totalTests,
      0,
      0,
      0,
      BenchmarkLiveStateIds.Pending,
      []
    );
  }

  private async Task<FinalSnapshotResult> CaptureFinalSnapshotAsync(
    string workspacePath,
    BenchmarkHarnessEvidence evidence
  )
  {
    try
    {
      return new FinalSnapshotResult(
        await _workspaces.CaptureAsync(workspacePath, CancellationToken.None),
        evidence
      );
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or DirectoryNotFoundException
        or InvalidOperationException
    )
    {
      return new FinalSnapshotResult(
        new BenchmarkWorkspaceSnapshot(
          new Dictionary<string, BenchmarkWorkspaceEntry>(
            BenchmarkWorkspaceFactory.PathComparer
          )
        ),
        FailureEvidence(
          "benchmark-snapshot-failed",
          exception.Message,
          true,
          evidence
        )
      );
    }
  }

  private static BenchmarkHarnessEvidence FailureEvidence(
    string code,
    string message,
    bool recoverable,
    BenchmarkHarnessEvidence? previous = null
  )
  {
    return new BenchmarkHarnessEvidence(
      BenchmarkExecutionStatusIds.Failed,
      new BenchmarkError(code, message, "harness-execution", recoverable),
      previous?.FinalReport ?? string.Empty,
      previous?.ToolCallCount,
      previous?.SurfacedErrorCount,
      previous?.RecoveredErrorCount,
      previous?.InputTokens,
      previous?.OutputTokens
    );
  }

  private static BenchmarkRun CreateRun(
    string runId,
    IBenchmarkTestDefinition test,
    InstalledModel model,
    IAgentHarness harness,
    HarnessAvailability availability,
    BenchmarkWorkspace workspace,
    DateTimeOffset startedAt,
    DateTimeOffset endedAt,
    string executionStatus,
    string prompt,
    string fingerprint
  )
  {
    return new BenchmarkRun(
      runId,
      test.Metadata.Id,
      test.Metadata.Version,
      model.Name,
      model.Digest,
      model.Provider,
      harness.Definition.Id,
      availability.Version,
      workspace.Id,
      workspace.WorkspacePath,
      startedAt,
      endedAt,
      executionStatus,
      test.Metadata.Suite,
      test.Metadata.SuiteVersion,
      test.Metadata.FixtureId,
      test.Metadata.FixtureVersion,
      prompt,
      fingerprint
    );
  }

  private BenchmarkRunResult PreparationFailure(
    string runId,
    IBenchmarkTestDefinition test,
    InstalledModel model,
    IAgentHarness harness,
    HarnessAvailability availability,
    BenchmarkWorkspace workspace,
    DateTimeOffset startedAt,
    string prompt,
    string fingerprint,
    BenchmarkScoreWeights scoreWeights,
    string status,
    BenchmarkError error
  )
  {
    var endedAt = DateTimeOffset.UtcNow;
    var raw = new BenchmarkRawResult(
      BenchmarkResultStatusIds.Error,
      false,
      0,
      0,
      0,
      0,
      [],
      [],
      [],
      status,
      error,
      HostValidationResult: "error"
    );
    return new BenchmarkRunResult(
      CreateRun(
        runId,
        test,
        model,
        harness,
        availability,
        workspace,
        startedAt,
        endedAt,
        status,
        prompt,
        fingerprint
      ),
      raw,
      false,
      _scorer.Score(raw, scoreWeights),
      Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds)
    );
  }

  private sealed record ResolvedBenchmarkHarness(
    IAgentHarness Adapter,
    HarnessAvailability Availability
  );

  private sealed record ResolvedBenchmarkModel(
    string RequestedName,
    InstalledModel? Installed
  );

  private sealed record ResolvedBenchmarkSuites(
    BenchmarkSuiteMetadata Metadata,
    IReadOnlyList<IBenchmarkTestDefinition> Tests,
    IReadOnlyList<BenchmarkSuiteSelection> Selections
  );

  private sealed record CellCompatibility(
    string Status,
    string? Message
  );

  private sealed class BenchmarkToolTrace
  {
    private readonly List<BenchmarkToolCallEvidence> _events = [];
    private readonly Dictionary<(int Turn, string Id), int> _sequences = [];
    private int _sequence;

    public IReadOnlyList<BenchmarkToolCallEvidence> Events => _events.ToArray();

    public void AddRange(IEnumerable<BenchmarkToolCallEvidence> events)
    {
      foreach (var item in events)
      {
        if (_events.Any(existing => existing.Sequence == item.Sequence
          && existing.Turn == item.Turn
          && existing.State == item.State))
        {
          continue;
        }
        _events.Add(item);
        _sequence = Math.Max(_sequence, item.Sequence);
      }
    }

    public void Start(
      int turn,
      string? id,
      string tool,
      string? path
    )
    {
      var key = (turn, id ?? $"anonymous-{_sequence + 1}");
      if (_sequences.ContainsKey(key))
      {
        return;
      }
      var sequence = ++_sequence;
      _sequences[key] = sequence;
      _events.Add(new BenchmarkToolCallEvidence(
        sequence,
        turn,
        tool,
        "started",
        path
      ));
    }

    public void Complete(
      int turn,
      string? id,
      string tool,
      string? path
    )
    {
      Finish(turn, id, tool, "completed", path, null);
    }

    public void Fail(
      int turn,
      string? id,
      string tool,
      string? path,
      string? errorCode
    )
    {
      Finish(turn, id, tool, "failed", path, errorCode);
    }

    private void Finish(
      int turn,
      string? id,
      string tool,
      string state,
      string? path,
      string? errorCode
    )
    {
      var key = (turn, id ?? string.Empty);
      if (!_sequences.TryGetValue(key, out var sequence))
      {
        sequence = ++_sequence;
        if (id is not null)
        {
          _sequences[(turn, id)] = sequence;
        }
      }
      if (_events.Any(item => item.Sequence == sequence && item.State == state))
      {
        return;
      }
      _events.Add(new BenchmarkToolCallEvidence(
        sequence,
        turn,
        tool,
        state,
        path,
        errorCode
      ));
    }
  }

  private sealed record FinalSnapshotResult(
    BenchmarkWorkspaceSnapshot Snapshot,
    BenchmarkHarnessEvidence Evidence
  );
}
