using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

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
  private readonly IBenchmarkTestRegistry _tests;
  private readonly IBenchmarkWorkspaceFactory _workspaces;
  private readonly IHarnessRegistry _harnesses;
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IBenchmarkProductionExecuteRunner _productionExecute;
  private readonly IBenchmarkScorer _scorer;
  private readonly IBenchmarkResultStore _results;
  private readonly IBenchmarkRunCancellationRegistry _cancellations;
  private readonly IBenchmarkEnvironmentSnapshotProvider _environmentSnapshots;
  private readonly IOllamaManagedServerManager _managedOllamaServers;
  private readonly IModelGpuAffinityResolver _modelGpuAffinities;
  private readonly ISystemMemoryMetricsProvider _systemMemory;
  private readonly IGpuMemoryMetricsProvider _gpuMemory;
  private readonly IMarkdownRenderer _markdown;
  private readonly ILogger<BenchmarkEngine> _logger;

  public BenchmarkEngine(
    IBenchmarkTestRegistry tests,
    IBenchmarkWorkspaceFactory workspaces,
    IHarnessRegistry harnesses,
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IBenchmarkProductionExecuteRunner productionExecute,
    IBenchmarkScorer scorer,
    IBenchmarkResultStore results,
    IBenchmarkRunCancellationRegistry cancellations,
    IBenchmarkEnvironmentSnapshotProvider environmentSnapshots,
    IOllamaManagedServerManager managedOllamaServers,
    ISystemMemoryMetricsProvider systemMemory,
    IGpuMemoryMetricsProvider gpuMemory,
    IMarkdownRenderer markdown,
    IModelGpuAffinityResolver modelGpuAffinities,
    ILogger<BenchmarkEngine> logger
  )
  {
    _tests = tests;
    _workspaces = workspaces;
    _harnesses = harnesses;
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _productionExecute = productionExecute;
    _scorer = scorer;
    _results = results;
    _cancellations = cancellations;
    _environmentSnapshots = environmentSnapshots;
    _managedOllamaServers = managedOllamaServers;
    _systemMemory = systemMemory;
    _gpuMemory = gpuMemory;
    _markdown = markdown;
    _modelGpuAffinities = modelGpuAffinities;
    _logger = logger;
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
    var contextTokens = ResolveBenchmarkContextTokens(settings, null);
    var gpu = settings.DefaultGpu;
    var providerEndpoint = new Uri(settings.OllamaUrl, UriKind.Absolute);
    var model = await ResolveModelAsync(
      request.Model,
      providerEndpoint,
      cancellationToken
    );
    gpu = (await _modelGpuAffinities.ResolveAsync(
      settings, model.Name, settings.DefaultGpu, cancellationToken
    )).GpuSelection;
    providerEndpoint = (await _managedOllamaServers.ResolveAsync(
      providerEndpoint, gpu, settings.DefaultGpu, contextTokens, cancellationToken
    )).Endpoint;
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
      cancellationToken,
      contextTokens,
      gpu,
      true
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
    var manual = tests.Any(IsManualTest);
    var manualOnly = manual && tests.All(IsManualTest);
    if (request.TimeoutSeconds is < 5 or > 1600)
    {
      throw new BenchmarkRequestException(
        "benchmark-timeout-invalid",
        "Benchmark test timeout must be between 5 and 1600 seconds.",
        "timeoutSeconds"
      );
    }
    var requestedHarnesses = NormalizeHarnesses(request.Harnesses);
    var requestedModels = NormalizeModels(request);
    var scoreWeights = request.ScoreWeights ?? BenchmarkScoreWeights.Default;
    if (!manualOnly)
    {
      _scorer.Validate(scoreWeights);
    }
    var scoringProfileId = NormalizeScoringProfileId(request.ScoringProfileId);
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var gpu = request.DefaultGpu ?? settings.DefaultGpu;
    if (!OllamaGpuSelection.IsValid(gpu, allowDefault: false))
    {
      throw new BenchmarkRequestException(
        "benchmark-gpu-invalid",
        "Benchmark Default GPU must be auto, an exact CUDA/ROCm/Vulkan index, or combined Vulkan.",
        "defaultGpu"
      );
    }
    settings = settings with { DefaultGpu = gpu };
    var contextTokens = ResolveBenchmarkContextTokens(settings, request.ContextTokens);
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
    await ValidateModelContextLimitsAsync(
      models,
      providerEndpoint,
      contextTokens,
      cancellationToken
    );
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
    BenchmarkError? infrastructureError = null;
    foreach (var model in models)
    {
      foreach (var harness in harnesses)
      {
        executionOrder++;
        var compatibility = Compatibility(model, harness);
        if (infrastructureError is not null)
          compatibility = new CellCompatibility(BenchmarkMatrixCellStatusIds.NotRun,
            "Not executed because an earlier cell encountered an infrastructure failure.");
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
            preCancellationCompatibility,
            manual
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
        try
        {
          var affinity = await _modelGpuAffinities.ResolveAsync(
            settings, model.Installed!.Name, settings.DefaultGpu, lease.Token
          );
          var cellEndpoint = (await _managedOllamaServers.ResolveAsync(
            providerEndpoint, affinity.GpuSelection, settings.DefaultGpu, contextTokens, lease.Token
          )).Endpoint;
          var result = await RunHarnessAsync(
            runId,
            harness,
            tests,
            model.Installed!,
            cellEndpoint,
            settings,
            request.TimeoutSeconds,
            scoreWeights,
            contextTokens,
            affinity.GpuSelection,
            lease.Token,
            progressSink,
            liveResults,
            manualOnly
          );
          cells.Add(CreateCell(executionOrder, model.Installed!, result));
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
          cells.Add(CreateNonExecutableCell(executionOrder, model, harness,
            new CellCompatibility(BenchmarkMatrixCellStatusIds.Cancelled,
              "Cancelled while preparing the benchmark cell."), tests.Count, manual: manual));
        }
        catch (Exception exception) when (exception is ModelGpuAffinityException or OllamaProviderException
          or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
          _logger.LogError(exception, "Benchmark {RunId} infrastructure failed in cell {Cell}.",
            runId, executionOrder);
          infrastructureError = new BenchmarkError("benchmark-cell-infrastructure-failed",
            "The Host could not complete this cell because its execution infrastructure failed. Completed evidence was retained.",
            "benchmark-cell", true);
          var partial = liveResults[CellKey(model.RequestedName, harness.Adapter.Definition.Id)];
          cells.Add(partial.Tests.Count > 0
            ? CreateCell(executionOrder, model.Installed!, partial) with
            { Status = BenchmarkMatrixCellStatusIds.Failed, Message = infrastructureError.Message }
            : CreateNonExecutableCell(executionOrder, model, harness,
              new CellCompatibility(BenchmarkMatrixCellStatusIds.Failed, infrastructureError.Message),
              tests.Count, manual: manual));
        }
      }
    }
    var matrixCells = cells.ToArray();
    BenchmarkMatrixCellResult[] scoringCells = manualOnly ? [] : matrixCells.Select(cell => cell.Result is null
      ? cell
      : cell with
      {
        Passed = CountPredefinedPassed(cell.Result.Tests),
        Total = tests.Count(test => !IsManualTest(test))
      }).ToArray();
    var harnessResults = models.Length == 1
      ? matrixCells.Where(cell => cell.Result is not null)
        .Select(cell => cell.Result!)
        .ToArray()
      : [];

    var endedAt = DateTimeOffset.UtcNow;
    var terminalState = lease.Token.IsCancellationRequested
      ? BenchmarkRunStatusIds.Cancelled
      : infrastructureError is not null ? BenchmarkRunStatusIds.Failed : BenchmarkRunStatusIds.Completed;
    var allPassed = !lease.Token.IsCancellationRequested
      && matrixCells.Length == models.Length * harnesses.Count
      && matrixCells.All(cell => string.Equals(
        cell.Status,
        BenchmarkMatrixCellStatusIds.Completed,
        StringComparison.Ordinal
      ) && cell.Passed == tests.Count);
    var finalStatus = terminalState != BenchmarkRunStatusIds.Completed
      ? terminalState
      : allPassed
        ? BenchmarkRunStatusIds.Passed
        : BenchmarkRunStatusIds.CompletedWithFailures;
    IReadOnlyList<BenchmarkRankingEntry> ranking = manualOnly ? [] : harnessResults
      .OrderByDescending(result => result.Score)
      .ThenByDescending(result => CountPredefinedPassed(result.Tests))
      .ThenBy(result => result.DurationMilliseconds)
      .ThenBy(result => result.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((result, index) => new BenchmarkRankingEntry(
        index + 1,
        result.Harness,
        CountPredefinedPassed(result.Tests),
        result.Score,
        result.DurationMilliseconds,
        result.Terminality
      ))
      .ToArray();
    IReadOnlyList<BenchmarkMatrixRankingEntry> pairRanking = manualOnly ? [] : RankPairs(scoringCells);
    IReadOnlyList<BenchmarkAggregateRankingEntry> modelRanking = manualOnly ? [] : RankAggregate(
      scoringCells,
      cell => cell.Model,
      requestedModels
    );
    IReadOnlyList<BenchmarkAggregateRankingEntry> harnessRanking = manualOnly ? [] : RankAggregate(
      scoringCells,
      cell => cell.Harness,
      requestedHarnesses
    );
    var identities = await CreateModelIdentitiesAsync(
      models,
      providerEndpoint,
      contextTokens,
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
    var environment = _environmentSnapshots.Capture(
      "ollama-local",
      runtimeVersion,
      true,
      contextTokens
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
      SchemaVersion: 5,
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
          contextTokens,
          gpu,
          request.CustomPrompt
        ),
        contextTokens,
        gpu,
        UsageModelRoles.Benchmark
      ),
      ScoringProfileVersion: BenchmarkScoringProfileIds.DefaultVersion,
      RawMeasurementsStatus: BenchmarkEvidenceStatusIds.Measured,
      ValidationEvidenceStatus: BenchmarkEvidenceStatusIds.Measured,
      SelectedSuites: resolvedSuites.Selections,
      BenchmarkMode: manual ? BenchmarkModeIds.Manual : BenchmarkModeIds.Predefined,
      CustomPrompt: manual ? request.CustomPrompt : null,
      RunName: manual ? NormalizeRunName(request.RunName) : null,
      RerunOfRunId: manual ? request.RerunOfRunId : null,
      ReviewStatus: manual
        ? ManualSuiteReviewStatus(matrixCells)
        : BenchmarkReviewStatusIds.NotApplicable,
      InfrastructureError: infrastructureError
    );
    try
    {
      await _results.SaveAsync(suiteResult, CancellationToken.None);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      _logger.LogError(exception, "Benchmark {RunId} result could not be persisted.", runId);
      suiteResult = suiteResult with
      {
        PersistenceError = new BenchmarkError("benchmark-result-not-saved",
          "The result could not be saved. Export the available evidence before closing the Host.",
          "benchmark-persistence", true)
      };
      await _results.RetainUnsavedAsync(suiteResult, CancellationToken.None);
    }
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
    int contextTokens,
    string gpu,
    CancellationToken cancellationToken,
    IBenchmarkProgressSink? progressSink,
    ConcurrentDictionary<string, BenchmarkHarnessResult> liveResults,
    bool manualOnly
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
        true,
        manualOnly
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
        contextTokens,
        gpu,
        false,
        progressSink is null
          ? null
          : new BenchmarkProgressContext(
            runId,
            model.Name,
            harnessId,
            test.Metadata.Id,
            progressSink
          ),
        IsManualTest(test)
      );
      testResults.Add(result);
      var partial = CreateHarnessResult(
        harness,
        tests.Count,
        testResults,
        cancellationToken.IsCancellationRequested,
        manualOnly,
        tests.Count(test => !IsManualTest(test))
      );
      liveResults[liveKey] = partial;
      if (result.RawResult.Error?.Code == "benchmark-execution-unsettled")
        throw new BenchmarkExecutionUnsettledException(new InvalidOperationException(result.RawResult.Error.Message));
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
        ProvisionalScore: manualOnly ? null : ProvisionalScore(testResults),
        Terminality: partial.Terminality,
        ElapsedMilliseconds: Elapsed(startedAt),
        Model: model.Name
      ));
      if (!manualOnly)
      {
        PublishRanking(runId, progressSink, liveResults, harnessId, model.Name);
      }
    }

    var observedRuntime = await ObserveRunningModelAsync(
      providerEndpoint,
      model.Name,
      cancellationToken
    );
    var deviceMemory = CaptureDeviceMemorySample();
    for (var index = 0; index < testResults.Count; index++)
    {
      var testResult = testResults[index];
      if (testResult.RawResult.RuntimeEvidence is not null)
      {
        testResults[index] = testResult with
        {
          RawResult = testResult.RawResult with
          {
            RuntimeEvidence = WithRuntimeObservation(
              testResult.RawResult.RuntimeEvidence,
              observedRuntime,
              deviceMemory
            )
          }
        };
      }
    }

    var final = CreateHarnessResult(
      harness,
      tests.Count,
      testResults,
      cancellationToken.IsCancellationRequested,
      manualOnly,
      tests.Count(test => !IsManualTest(test))
    );
    liveResults[liveKey] = final;
    PublishHarnessResult(
      runId,
      model.Name,
      final,
      startedAt,
      progressSink,
      liveResults,
      false,
      manualOnly
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
    int contextTokens,
    string gpu,
    bool observeRuntime,
    BenchmarkProgressContext? progress = null,
    bool manual = false
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
    var retainWorkspace = manual || string.Equals(
      test.Metadata.Suite,
      BenchmarkSuiteIds.RealLifeProblem,
      StringComparison.OrdinalIgnoreCase
    );

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
      var metadataBoundedTimeout = string.Equals(
        test.Metadata.Suite,
        BenchmarkSuiteIds.AgentBehavior,
        StringComparison.OrdinalIgnoreCase
      ) || string.Equals(
        test.Metadata.Suite,
        BenchmarkSuiteIds.RealLifeProblem,
        StringComparison.OrdinalIgnoreCase
      );
      var effectiveTimeout = metadataBoundedTimeout
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
          workspace,
          contextTokens,
          gpu,
          timeoutSource.Token,
          progress
        );
        if (!runCancellationToken.IsCancellationRequested
          && timeoutSource.IsCancellationRequested
          && evidence.ExecutionStatus == BenchmarkExecutionStatusIds.Cancelled)
        {
          evidence = evidence with
          {
            ExecutionStatus = BenchmarkExecutionStatusIds.TimedOut,
            Error = new BenchmarkError(
              "benchmark-timeout",
              $"The harness exceeded the configured {effectiveTimeout.TotalSeconds:0}-second scenario timeout.",
              "harness-execution",
              true
            )
          };
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.TimedOut,
            evidence.Error.Message,
            BenchmarkActivityKindIds.Timeout
          );
        }
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
        exception is not BenchmarkExecutionUnsettledException && exception is IOException
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
      using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(30));
      var finalSnapshot = await CaptureFinalSnapshotAsync(
        workspace.WorkspacePath,
        evidence,
        finalization.Token
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
        finalization.Token
      );
      raw = raw with { FailureCategory = ClassifyFailure(raw) };
      raw = raw with
      {
        RuntimeEvidence = await CaptureRuntimeEvidenceAsync(
          providerEndpoint,
          model.Name,
          contextTokens,
          gpu,
          raw,
          runCancellationToken,
          observeRuntime
        )
      };
      raw = RenderNarrativeMarkdown(raw);
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
        manual ? null : _scorer.Score(raw, scoreWeights),
        Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
        ReviewStatus: manual
          ? ManualResultReviewStatus(raw)
          : BenchmarkReviewStatusIds.NotApplicable
      );
    }
    catch (BenchmarkExecutionUnsettledException exception)
    {
      retainWorkspace = true;
      result = PreparationFailure(testRunId, test, model, harness, availability, workspace,
        startedAt, prompt, fingerprint, scoreWeights, manual, BenchmarkExecutionStatusIds.Failed,
        new BenchmarkError("benchmark-execution-unsettled", exception.Message, "benchmark-shutdown", true));
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
        manual,
        runCancellationToken.IsCancellationRequested
          ? BenchmarkExecutionStatusIds.Cancelled
          : BenchmarkExecutionStatusIds.Failed,
        new BenchmarkError(
          runCancellationToken.IsCancellationRequested ? "benchmark-cancelled" : "benchmark-finalization-timeout",
          runCancellationToken.IsCancellationRequested ? "The benchmark was cancelled before final validation."
            : "Host finalization did not finish within its bounded deadline.",
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
        manual,
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
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        cleanedUp = !retainWorkspace
          && await _workspaces.CleanupAsync(workspace, cleanupTimeout.Token);
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
    if (retainWorkspace)
    {
      var validationFacts = result.RawResult.ValidationFacts is null
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : new Dictionary<string, string>(
          result.RawResult.ValidationFacts,
          StringComparer.Ordinal
        );
      validationFacts["workspaceRetention"] = "retained-for-human-review";
      var raw = result.RawResult with { ValidationFacts = validationFacts };
      finalResult = result with
      {
        RawResult = raw,
        WorkspaceCleanedUp = false,
        WorkspaceRetained = true
      };
    }
    else if (!cleanedUp)
    {
      var validationFacts = result.RawResult.ValidationFacts is null
        ? new Dictionary<string, string>(StringComparer.Ordinal)
        : new Dictionary<string, string>(
          result.RawResult.ValidationFacts,
          StringComparer.Ordinal
        );
      validationFacts["workspaceCleanup"] = "failed";
      var raw = result.RawResult with { ValidationFacts = validationFacts };
      finalResult = result with
      {
        RawResult = raw,
        WorkspaceCleanedUp = false,
        WorkspaceRetained = true
      };
    }
    else
    {
      finalResult = result with
      {
        WorkspaceCleanedUp = true,
        WorkspaceRetained = false
      };
    }
    PublishTestTerminal(progress, finalResult);
    return finalResult;
  }

  private BenchmarkRawResult RenderNarrativeMarkdown(BenchmarkRawResult raw)
  {
    return raw with
    {
      FinalHarnessReportHtml = string.IsNullOrWhiteSpace(raw.FinalHarnessReport)
        ? string.Empty
        : _markdown.Render(raw.FinalHarnessReport),
      Turns = raw.Turns?.Select(turn => turn with
      {
        FinalReportHtml = string.IsNullOrWhiteSpace(turn.FinalReport)
          ? string.Empty
          : _markdown.Render(turn.FinalReport)
      }).ToArray()
    };
  }

  private async Task<BenchmarkRuntimeEvidence> CaptureRuntimeEvidenceAsync(
    Uri providerEndpoint,
    string model,
    int requestedContextTokens,
    string gpu,
    BenchmarkRawResult raw,
    CancellationToken cancellationToken,
    bool observeRuntime
  )
  {
    var running = observeRuntime
      ? await ObserveRunningModelAsync(providerEndpoint, model, cancellationToken)
      : null;

    var diagnostics = raw.OperationalDiagnostics;
    var modelSize = running?.SizeBytes;
    var vramSize = running?.VramSizeBytes;
    long? ramSize = modelSize.HasValue && vramSize.HasValue
      ? Math.Max(0, modelSize.Value - vramSize.Value)
      : null;
    var gpuPercent = modelSize is > 0 && vramSize.HasValue
      ? Math.Clamp((int)Math.Round(
        100d * vramSize.Value / modelSize.Value,
        MidpointRounding.AwayFromZero
      ), 0, 100)
      : (int?)null;
    var target = OllamaGpuSelection.ResolveTarget(gpu, gpu);
    var promptTokensPerSecond = TokensPerSecond(
      diagnostics?.InputTokens,
      diagnostics?.PromptEvalDurationNanoseconds
    );
    var outputTokensPerSecond = TokensPerSecond(
      diagnostics?.OutputTokens,
      diagnostics?.EvalDurationNanoseconds
    );
    var lastProgress = diagnostics?.LastProgressType is null
      ? null
      : string.IsNullOrWhiteSpace(diagnostics.LastProgressMessage)
        ? diagnostics.LastProgressType
        : $"{diagnostics.LastProgressType}: {diagnostics.LastProgressMessage}";

    return new BenchmarkRuntimeEvidence(
      DateTimeOffset.UtcNow,
      gpu,
      target?.Backend ?? "auto",
      requestedContextTokens,
      running?.ContextLength,
      modelSize,
      vramSize,
      ramSize,
      gpuPercent.HasValue ? $"{gpuPercent.Value}% GPU" : "not-observed",
      running?.ContextLength is null
        ? "not-observed"
        : running.ContextLength == requestedContextTokens
          ? "matched"
          : "mismatch",
      diagnostics?.TotalInferenceDurationNanoseconds,
      diagnostics?.LoadDurationNanoseconds,
      diagnostics?.PromptEvalDurationNanoseconds,
      diagnostics?.EvalDurationNanoseconds,
      promptTokensPerSecond,
      outputTokensPerSecond,
      raw.FailureCategory == BenchmarkFailureCategoryIds.Timeout
        ? raw.Error?.Stage ?? diagnostics?.TerminalReason ?? "harness-execution"
        : null,
      lastProgress,
      observeRuntime ? CaptureDeviceMemorySample() : null
    );
  }

  private async Task<OllamaRunningModel?> ObserveRunningModelAsync(
    Uri providerEndpoint,
    string model,
    CancellationToken cancellationToken
  )
  {
    try
    {
      using var observationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken
      );
      observationTimeout.CancelAfter(TimeSpan.FromSeconds(2));
      var runningModels = await _ollamaClient.GetRunningModelsAsync(
        providerEndpoint,
        observationTimeout.Token
      );
      return runningModels.FirstOrDefault(candidate => string.Equals(
        candidate.Name,
        model,
        StringComparison.OrdinalIgnoreCase
      ));
    }
    catch (Exception exception) when (exception is OllamaProviderException
      or OperationCanceledException
      or HttpRequestException
      or IOException)
    {
      return null;
    }
  }

  private static BenchmarkRuntimeEvidence WithRuntimeObservation(
    BenchmarkRuntimeEvidence runtime,
    OllamaRunningModel? running,
    BenchmarkDeviceMemorySample? deviceMemory
  )
  {
    var modelSize = running?.SizeBytes;
    var vramSize = running?.VramSizeBytes;
    long? ramSize = modelSize.HasValue && vramSize.HasValue
      ? Math.Max(0, modelSize.Value - vramSize.Value)
      : null;
    var gpuPercent = modelSize is > 0 && vramSize.HasValue
      ? Math.Clamp((int)Math.Round(
        100d * vramSize.Value / modelSize.Value,
        MidpointRounding.AwayFromZero
      ), 0, 100)
      : (int?)null;
    return runtime with
    {
      ActualContextTokens = running?.ContextLength,
      ModelSizeBytes = modelSize,
      VramSizeBytes = vramSize,
      EstimatedRamSizeBytes = ramSize,
      Processor = gpuPercent.HasValue ? $"{gpuPercent.Value}% GPU" : "not-observed",
      ContextStatus = running?.ContextLength is null
        ? "not-observed"
        : running.ContextLength == runtime.RequestedContextTokens
          ? "matched"
          : "mismatch",
      DeviceMemory = deviceMemory
    };
  }

  private BenchmarkDeviceMemorySample? CaptureDeviceMemorySample()
  {
    try
    {
      return new BenchmarkDeviceMemorySample(
        DateTimeOffset.UtcNow,
        _systemMemory.GetStatus(),
        _gpuMemory.GetStatus().Devices
      );
    }
    catch (Exception exception) when (exception is InvalidOperationException
      or NotSupportedException)
    {
      return null;
    }
  }

  private static double? TokensPerSecond(long? tokens, long? nanoseconds) =>
    tokens.HasValue && nanoseconds is > 0
      ? Math.Round(tokens.Value * 1_000_000_000d / nanoseconds.Value, 3)
      : null;

  private static string ClassifyFailure(BenchmarkRawResult raw)
  {
    if (raw.Status == BenchmarkResultStatusIds.Pass && raw.ObjectiveAchieved)
    {
      return BenchmarkFailureCategoryIds.None;
    }
    if (raw.ExecutionStatus == BenchmarkExecutionStatusIds.TimedOut)
    {
      return BenchmarkFailureCategoryIds.Timeout;
    }
    if (raw.ExecutionStatus == BenchmarkExecutionStatusIds.Cancelled)
    {
      return BenchmarkFailureCategoryIds.Cancelled;
    }

    var errorIdentity = $"{raw.Error?.Code} {raw.Error?.Stage}";
    if (ContainsAny(errorIdentity, "preparation", "fixture"))
    {
      return BenchmarkFailureCategoryIds.Preparation;
    }
    if (ContainsAny(
      errorIdentity,
      "approval",
      "policy",
      "workspace",
      "trusted",
      "forbidden",
      "boundary",
      "not-evaluated",
      "not-offered",
      "tool-unavailable",
      "tool-not-available",
      "capability"
    ))
    {
      return BenchmarkFailureCategoryIds.HostPolicy;
    }
    if (ContainsAny(
      errorIdentity,
      "ollama",
      "provider",
      "runtime",
      "context",
      "generation",
      "transport",
      "http"
    ))
    {
      return BenchmarkFailureCategoryIds.ProviderRuntime;
    }
    if (ContainsAny(
      errorIdentity,
      "harness",
      "protocol",
      "qwen",
      "opencode",
      "claude",
      "codex"
    ))
    {
      return BenchmarkFailureCategoryIds.HarnessProtocol;
    }
    if (raw.ExecutionStatus is BenchmarkExecutionStatusIds.Completed
      or BenchmarkExecutionStatusIds.Partial)
    {
      return BenchmarkFailureCategoryIds.SemanticValidation;
    }
    return BenchmarkFailureCategoryIds.Unknown;
  }

  private static bool ContainsAny(string value, params string[] candidates) =>
    candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

  private async Task<BenchmarkHarnessEvidence> ExecuteHarnessAsync(
    IAgentHarness harness,
    IBenchmarkTestDefinition test,
    InstalledModel model,
    BenchmarkWorkspace workspace,
    int contextTokens,
    string gpu,
    CancellationToken cancellationToken,
    BenchmarkProgressContext? progress
  )
  {
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
        turn,
        model,
        workspace,
        contextTokens,
        gpu,
        progress,
        test.Metadata.Suite == BenchmarkSuiteIds.Manual,
        cancellationToken
      );
      outcomes.Add(outcome);
      if (outcome.ToolCalls is { Count: > 0 })
      {
        toolTrace.AddRange(outcome.ToolCalls);
      }
      if (outcome.HostEvents is { Count: > 0 })
      {
        hostEvents.AddRange(outcome.HostEvents);
      }
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
      toolTrace.Events,
      AggregateOperationalDiagnostics(outcomes)
    );
  }

  private async Task<BenchmarkHarnessEvidence> ExecuteHarnessTurnAsync(
    IAgentHarness harness,
    BenchmarkScenarioTurn turn,
    InstalledModel model,
    BenchmarkWorkspace workspace,
    int contextTokens,
    string gpu,
    BenchmarkProgressContext? progress,
    bool preserveExactUserMessage,
    CancellationToken cancellationToken
  )
  {
    return await _productionExecute.ExecuteAsync(
      turn.Prompt,
      model.Name,
      harness.Definition.Id,
      workspace,
      contextTokens,
      gpu,
      turn.Order,
      turn.Name,
      progress,
      preserveExactUserMessage,
      cancellationToken
    );
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

  private static BenchmarkOperationalDiagnostics? AggregateOperationalDiagnostics(
    IReadOnlyList<BenchmarkHarnessEvidence> outcomes
  )
  {
    var values = outcomes
      .Select(outcome => outcome.OperationalDiagnostics)
      .Where(diagnostics => diagnostics is not null)
      .Cast<BenchmarkOperationalDiagnostics>()
      .ToArray();
    if (values.Length == 0)
    {
      return null;
    }
    var last = values[^1];
    return last with
    {
      ToolCalls = SumDiagnostics(values, value => value.ToolCalls),
      FailedToolCalls = SumDiagnostics(values, value => value.FailedToolCalls),
      ToolValidationErrors = SumDiagnostics(values, value => value.ToolValidationErrors),
      RepeatedToolCalls = SumDiagnostics(values, value => value.RepeatedToolCalls),
      RepeatedIdenticalActions = SumDiagnostics(values, value => value.RepeatedIdenticalActions),
      RecoveryAttempts = SumDiagnostics(values, value => value.RecoveryAttempts),
      FilesRead = DistinctDiagnostics(values.SelectMany(value => value.FilesRead)),
      FilesWritten = DistinctDiagnostics(values.SelectMany(value => value.FilesWritten)),
      FilesModified = DistinctDiagnostics(values.SelectMany(value => value.FilesModified)),
      FilesCreated = DistinctDiagnostics(values.SelectMany(value => value.FilesCreated)),
      FilesDeleted = DistinctDiagnostics(values.SelectMany(value => value.FilesDeleted)),
      ExecutionTurns = SumDiagnostics(values, value => value.ExecutionTurns),
      DirectTurns = SumDiagnostics(values, value => value.DirectTurns),
      SupervisorTurns = SumDiagnostics(values, value => value.SupervisorTurns),
      WorkerTurns = SumDiagnostics(values, value => value.WorkerTurns),
      ExecutionDurationMilliseconds = values.Sum(value => value.ExecutionDurationMilliseconds),
      SetupDurationMilliseconds = values.Sum(value => value.SetupDurationMilliseconds),
      BrowserValidationDurationMilliseconds = values.Sum(value => value.BrowserValidationDurationMilliseconds),
      InputTokens = SumLongDiagnostics(values, value => value.InputTokens),
      OutputTokens = SumLongDiagnostics(values, value => value.OutputTokens),
      TotalInferenceDurationNanoseconds = SumLongDiagnostics(
        values,
        value => value.TotalInferenceDurationNanoseconds
      ),
      LoadDurationNanoseconds = SumLongDiagnostics(
        values,
        value => value.LoadDurationNanoseconds
      ),
      PromptEvalDurationNanoseconds = SumLongDiagnostics(
        values,
        value => value.PromptEvalDurationNanoseconds
      ),
      EvalDurationNanoseconds = SumLongDiagnostics(
        values,
        value => value.EvalDurationNanoseconds
      ),
      TokenProvenance = values.All(value => value.TokenProvenance == BenchmarkEvidenceStatusIds.Measured)
        ? BenchmarkEvidenceStatusIds.Measured
        : values.Any(value => value.TokenProvenance != BenchmarkEvidenceStatusIds.Unavailable)
          ? "estimated"
          : BenchmarkEvidenceStatusIds.Unavailable,
      ValidationErrorCodes = DistinctDiagnostics(values.SelectMany(value => value.ValidationErrorCodes)),
      UnavailableMetrics = DistinctDiagnostics(values.SelectMany(value => value.UnavailableMetrics))
    };
  }

  private static int? SumDiagnostics(
    IReadOnlyList<BenchmarkOperationalDiagnostics> values,
    Func<BenchmarkOperationalDiagnostics, int?> selector
  )
  {
    var present = values.Select(selector).Where(value => value.HasValue).ToArray();
    return present.Length == 0 ? null : present.Sum(value => value!.Value);
  }

  private static long? SumLongDiagnostics(
    IReadOnlyList<BenchmarkOperationalDiagnostics> values,
    Func<BenchmarkOperationalDiagnostics, long?> selector
  )
  {
    var present = values.Select(selector).Where(value => value.HasValue).ToArray();
    return present.Length == 0 ? null : present.Sum(value => value!.Value);
  }

  private static string[] DistinctDiagnostics(IEnumerable<string> values) => values
    .Distinct(BenchmarkWorkspaceFactory.PathComparer)
    .OrderBy(value => value, StringComparer.Ordinal)
    .ToArray();

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

  private static int ResolveBenchmarkContextTokens(
    ApplicationSettings settings,
    int? requestedContextTokens
  )
  {
    if (!settings.OllamaRuntime.RoleDefaults.TryGetValue(
      OllamaRuntimeRoleIds.Benchmark,
      out var profile
    ))
    {
      throw new BenchmarkRequestException(
        "benchmark-context-profile-missing",
        "The benchmark runtime context profile is unavailable.",
        "contextTokens"
      );
    }

    var contextTokens = requestedContextTokens ?? profile.TargetContextTokens;
    var minimum = settings.OllamaRuntime.ContextEscalationLadder.Min();
    var maximum = settings.Context.ProviderContextTokens;
    if (contextTokens < minimum || contextTokens > maximum)
    {
      throw new BenchmarkRequestException(
        "benchmark-context-invalid",
        $"Benchmark context must be between {minimum} and {maximum} tokens.",
        "contextTokens"
      );
    }

    return contextTokens;
  }

  private async Task ValidateModelContextLimitsAsync(
    IReadOnlyList<ResolvedBenchmarkModel> models,
    Uri providerEndpoint,
    int contextTokens,
    CancellationToken cancellationToken
  )
  {
    foreach (var model in models.Where(model => model.Installed is not null))
    {
      OllamaModelMetadata metadata;
      try
      {
        metadata = await _ollamaClient.GetModelMetadataAsync(
          providerEndpoint,
          model.Installed!.Name,
          cancellationToken
        );
      }
      catch (OllamaProviderException exception)
      {
        throw new BenchmarkRequestException(
          "benchmark-model-context-unavailable",
          $"The declared context limit for model '{model.RequestedName}' could not be read: {exception.Message}",
          "contextTokens"
        );
      }

      if (metadata.DeclaredContextTokens is not > 0)
      {
        throw new BenchmarkRequestException(
          "benchmark-model-context-unavailable",
          $"Model '{model.RequestedName}' does not declare a usable context limit.",
          "contextTokens"
        );
      }
      if (contextTokens > metadata.DeclaredContextTokens.Value)
      {
        throw new BenchmarkRequestException(
          "benchmark-context-exceeds-model",
          $"Benchmark context {contextTokens} exceeds model '{model.RequestedName}' limit {metadata.DeclaredContextTokens.Value}.",
          "contextTokens"
        );
      }
    }
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
    string? compatibilityStatus = null,
    bool manual = false
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
      null,
      manual ? BenchmarkReviewStatusIds.TechnicalFailure : BenchmarkReviewStatusIds.NotApplicable
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
      result,
      result.Tests.Select(test => test.ReviewStatus).FirstOrDefault(
        status => status != BenchmarkReviewStatusIds.NotApplicable
      ) ?? BenchmarkReviewStatusIds.NotApplicable,
      result.Tests.Select(test => test.UserReview?.Score).FirstOrDefault(score => score.HasValue)
    );
  }

  private async Task<IReadOnlyList<BenchmarkModelIdentity>> CreateModelIdentitiesAsync(
    IReadOnlyList<ResolvedBenchmarkModel> models,
    Uri providerEndpoint,
    int configuredContext,
    CancellationToken cancellationToken
  )
  {
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
    int configuredContextTokens,
    string gpu,
    string? customPrompt
  )
  {
    var canonical = string.Join("\n", new[]
    {
      $"suite={suite.Id}:{suite.Version}",
      $"fixture={suite.FixtureId}:{suite.FixtureVersion}",
      $"tests={string.Join('|', suite.Tests.Select(test => $"{test.Suite}:{test.SuiteVersion}:{test.Id}:{test.Version}"))}",
      $"timeout={timeoutSeconds}",
      $"context={configuredContextTokens}",
      $"gpu={gpu}",
      $"modelRole={UsageModelRoles.Benchmark}",
      "sequential=true",
      $"models={string.Join('|', models)}",
      $"harnesses={string.Join('|', harnesses)}",
      $"promptSha256={(customPrompt is null ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(customPrompt))).ToLowerInvariant())}"
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
      if (selection.Id == BenchmarkSuiteIds.Manual)
      {
        if (selection.Version != BenchmarkSuiteIds.ManualVersion)
        {
          throw new BenchmarkRequestException(
            "benchmark-suite-unknown",
            $"Custom Prompt test version {selection.Version} is unavailable.",
            "suites"
          );
        }
        if (string.IsNullOrWhiteSpace(request.CustomPrompt))
        {
          throw new BenchmarkRequestException(
            "benchmark-custom-prompt-required",
            "Custom Prompt test requires a non-empty prompt.",
            "customPrompt"
          );
        }
        var definition = new ManualPromptBenchmark(request.CustomPrompt);
        var manualSuite = new BenchmarkSuiteMetadata(
          BenchmarkSuiteIds.Manual,
          BenchmarkSuiteIds.ManualVersion,
          "Custom Prompt",
          BenchmarkSuiteIds.ManualFixtureId,
          BenchmarkSuiteIds.ManualFixtureVersion,
          [definition.Metadata]
        );
        selections.Add(new BenchmarkSuiteSelection(manualSuite.Id, manualSuite.Version));
        metadata.Add(manualSuite);
        definitions.Add(definition);
        continue;
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
    var scored = tests.Where(test => test.Score is not null).ToArray();
    if (scored.Length == 0)
    {
      return null;
    }
    return decimal.Round(
      scored.Sum(test => test.Score!.Total) / scored.Length,
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
    bool skipped,
    bool manual
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
      ProvisionalScore: manual ? null : ProvisionalScore(result.Tests),
      Terminality: result.Terminality,
      ElapsedMilliseconds: Elapsed(startedAt),
      Model: model
    ));
    if (!manual)
    {
      PublishRanking(runId, progressSink, liveResults, result.Harness, model);
    }
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
      .Where(item => item.Result.Tests.Any(test => test.Score is not null))
      .OrderByDescending(item => ProvisionalScore(item.Result.Tests))
      .ThenByDescending(item => CountPredefinedPassed(item.Result.Tests))
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
    bool cancelled,
    bool manual = false,
    int? scoringTestTotal = null
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
    var score = manual ? 0m : tests.Sum(test => test.Score?.Total ?? 0m)
      / Math.Max(1, scoringTestTotal ?? totalTests);
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
    BenchmarkHarnessEvidence evidence,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return new FinalSnapshotResult(
        await _workspaces.CaptureAsync(workspacePath, cancellationToken),
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
    bool manual,
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
      HostValidationResult: "error",
      FailureCategory: status == BenchmarkExecutionStatusIds.Cancelled
        ? BenchmarkFailureCategoryIds.Cancelled
        : BenchmarkFailureCategoryIds.Preparation
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
      manual ? null : _scorer.Score(raw, scoreWeights),
      Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
      ReviewStatus: manual
        ? BenchmarkReviewStatusIds.TechnicalFailure
        : BenchmarkReviewStatusIds.NotApplicable
    );
  }

  private static string ManualResultReviewStatus(BenchmarkRawResult raw) =>
    string.Equals(raw.ExecutionStatus, BenchmarkExecutionStatusIds.Completed, StringComparison.Ordinal)
      && raw.Error is null
        ? BenchmarkReviewStatusIds.AwaitingUserReview
        : BenchmarkReviewStatusIds.TechnicalFailure;

  private static bool IsManualTest(IBenchmarkTestDefinition test) =>
    string.Equals(test.Metadata.Suite, BenchmarkSuiteIds.Manual, StringComparison.Ordinal);

  private static int CountPredefinedPassed(IReadOnlyList<BenchmarkRunResult> tests) =>
    tests.Count(test => test.Run.SuiteId != BenchmarkSuiteIds.Manual
      && test.RawResult.Status == BenchmarkResultStatusIds.Pass);

  private static string ManualSuiteReviewStatus(
    IReadOnlyList<BenchmarkMatrixCellResult> cells
  ) => cells.SelectMany(cell => cell.Result?.Tests ?? [])
    .Any(test => test.ReviewStatus == BenchmarkReviewStatusIds.AwaitingUserReview)
      ? BenchmarkReviewStatusIds.AwaitingUserReview
      : BenchmarkReviewStatusIds.Reviewed;

  private static string? NormalizeRunName(string? runName)
  {
    if (string.IsNullOrWhiteSpace(runName))
    {
      return null;
    }
    var normalized = runName.Trim();
    if (normalized.Length > 200)
    {
      throw new BenchmarkRequestException(
        "benchmark-run-name-too-large",
        "Manual benchmark run name cannot exceed 200 characters.",
        "runName"
      );
    }
    return normalized;
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
