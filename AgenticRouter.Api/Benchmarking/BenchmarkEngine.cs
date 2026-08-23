using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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

  public BenchmarkEngine(
    IBenchmarkTestRegistry tests,
    IBenchmarkWorkspaceFactory workspaces,
    IHarnessRegistry harnesses,
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IBenchmarkNativeExecutor nativeExecutor,
    IBenchmarkScorer scorer,
    IBenchmarkResultStore results,
    IBenchmarkRunCancellationRegistry cancellations
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
      TimeSpan.FromSeconds(120),
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
    if (
      !_tests.TryGetSuite(
        request.SuiteId,
        request.SuiteVersion,
        out var suite,
        out var tests
      )
    )
    {
      throw new BenchmarkRequestException(
        "benchmark-suite-unknown",
        $"Benchmark suite '{request.SuiteId}' version {request.SuiteVersion} is unavailable.",
        "suiteId"
      );
    }
    if (request.TimeoutSeconds is < 5 or > 600)
    {
      throw new BenchmarkRequestException(
        "benchmark-timeout-invalid",
        "Benchmark test timeout must be between 5 and 600 seconds.",
        "timeoutSeconds"
      );
    }
    var requestedHarnesses = NormalizeHarnesses(request.Harnesses);
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var providerEndpoint = new Uri(settings.OllamaUrl, UriKind.Absolute);
    var model = await ResolveModelAsync(
      request.Model,
      providerEndpoint,
      cancellationToken
    );
    var harnesses = new List<ResolvedBenchmarkHarness>(requestedHarnesses.Count);
    foreach (var harnessId in requestedHarnesses)
    {
      harnesses.Add(await ResolveHarnessAsync(harnessId, cancellationToken));
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
      StartedAt: startedAt
    ));
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
          TotalTests: tests.Count
        ));
      }
    }

    var liveResults = new ConcurrentDictionary<string, BenchmarkHarnessResult>(
      StringComparer.OrdinalIgnoreCase
    );
    foreach (var harness in harnesses)
    {
      liveResults[harness.Adapter.Definition.Id] = EmptyPendingHarness(
        harness,
        tests.Count
      );
    }
    var completedHarnesses = new List<BenchmarkHarnessResult>(harnesses.Count);
    foreach (var harness in harnesses)
    {
      completedHarnesses.Add(await RunHarnessAsync(
        runId,
        harness,
        tests,
        model,
        providerEndpoint,
        settings,
        request.TimeoutSeconds,
        lease.Token,
        progressSink,
        liveResults
      ));
    }
    var harnessResults = completedHarnesses.ToArray();

    var endedAt = DateTimeOffset.UtcNow;
    var terminalState = lease.Token.IsCancellationRequested
      ? BenchmarkRunStatusIds.Cancelled
      : BenchmarkRunStatusIds.Completed;
    var allPassed = !lease.Token.IsCancellationRequested
      && harnessResults.Length == harnesses.Count
      && harnessResults.All(result => result.Passed == tests.Count);
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
    var suiteResult = new BenchmarkSuiteRunResult(
      runId,
      model.Name,
      model.Digest,
      model.Provider,
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
      _scorer.Weights,
      harnessResults,
      ranking
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
    CancellationToken cancellationToken,
    IBenchmarkProgressSink? progressSink,
    ConcurrentDictionary<string, BenchmarkHarnessResult> liveResults
  )
  {
    var harnessId = harness.Adapter.Definition.Id;
    var startedAt = DateTimeOffset.UtcNow;
    Publish(progressSink, new BenchmarkProgressEvent(
      runId,
      BenchmarkProgressTypeIds.HarnessStarted,
      startedAt,
      BenchmarkLiveStateIds.Running,
      harnessId,
      TotalTests: tests.Count,
      StartedAt: startedAt
    ));
    if (cancellationToken.IsCancellationRequested)
    {
      var empty = EmptyCancelledHarness(harness, tests.Count);
      liveResults[harnessId] = empty;
      PublishHarnessResult(runId, empty, startedAt, progressSink, liveResults, true);
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
        StartedAt: DateTimeOffset.UtcNow
      ));
      var result = await RunTestAsync(
        test,
        model,
        harness.Adapter,
        harness.Availability,
        providerEndpoint,
        settings,
        TimeSpan.FromSeconds(timeoutSeconds),
        cancellationToken,
        progressSink is null
          ? null
          : new BenchmarkProgressContext(runId, harnessId, test.Metadata.Id, progressSink)
      );
      testResults.Add(result);
      var partial = CreateHarnessResult(
        harness,
        tests.Count,
        testResults,
        cancellationToken.IsCancellationRequested
      );
      liveResults[harnessId] = partial;
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
        ElapsedMilliseconds: Elapsed(startedAt)
      ));
      PublishRanking(runId, progressSink, liveResults, harnessId);
    }

    var final = CreateHarnessResult(
      harness,
      tests.Count,
      testResults,
      cancellationToken.IsCancellationRequested
    );
    liveResults[harnessId] = final;
    PublishHarnessResult(runId, final, startedAt, progressSink, liveResults, false);
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
    CancellationToken runCancellationToken,
    BenchmarkProgressContext? progress = null
  )
  {
    var testRunId = Guid.NewGuid().ToString("N");
    var startedAt = DateTimeOffset.UtcNow;
    var workspace = await _workspaces.CreateAsync(testRunId, CancellationToken.None);
    var prompt = test.CreateTask();
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
      timeoutSource.CancelAfter(timeout);
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
              : $"The harness exceeded the configured {timeout.TotalSeconds:0}-second test timeout.",
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
        _scorer.Score(raw),
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
        Score = _scorer.Score(raw)
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
    var execution = new AgentHarnessExecution<BenchmarkHarnessEvidence>(
      nativeCancellationToken => _nativeExecutor.ExecuteAsync(
        test,
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
          test.CreateTask(),
          "auto",
          providerEndpoint,
          ContextWindowTokens: contextTokens,
          HostCapabilities: BenchmarkHostCapabilities,
          UseMinimalToolInventory: true,
          ReleaseWorkspaceAfterTurn: true
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
          string.Equals(harnessEvent.Type, "error", StringComparison.Ordinal)
          || string.Equals(harnessEvent.Type, "tool.failed", StringComparison.Ordinal)
          || (!harnessEvent.IsTerminal && harnessEvent.ErrorCode is not null)
        )
        {
          surfacedErrors++;
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
      null
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
      ValidationChecks: checks
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
      TestResult: result
    ));
  }

  private static void PublishHarnessResult(
    string runId,
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
      ElapsedMilliseconds: Elapsed(startedAt)
    ));
    PublishRanking(runId, progressSink, liveResults, result.Harness);
  }

  private static void PublishRanking(
    string runId,
    IBenchmarkProgressSink? progressSink,
    ConcurrentDictionary<string, BenchmarkHarnessResult> liveResults,
    string changedHarness
  )
  {
    var snapshot = liveResults.Values.ToArray();
    var ranks = snapshot
      .Where(result => result.Tests.Count > 0)
      .OrderByDescending(result => ProvisionalScore(result.Tests))
      .ThenByDescending(result => result.Passed)
      .ThenBy(result => result.DurationMilliseconds)
      .ThenBy(result => result.Harness, StringComparer.OrdinalIgnoreCase)
      .Select((result, index) => (result.Harness, Rank: index + 1))
      .ToDictionary(item => item.Harness, item => item.Rank, StringComparer.OrdinalIgnoreCase);
    var ranking = snapshot.Select(result =>
    {
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
        ranks.TryGetValue(result.Harness, out var rank) ? rank : null,
        result.Harness,
        result.Tests.Count,
        result.Total,
        result.Passed,
        ProvisionalScore(result.Tests),
        result.DurationMilliseconds,
        result.Terminality,
        state
      );
    }).OrderBy(entry => entry.Rank ?? int.MaxValue)
      .ThenBy(entry => entry.Harness, StringComparer.OrdinalIgnoreCase)
      .ToArray();
    Publish(progressSink, new BenchmarkProgressEvent(
      runId,
      BenchmarkProgressTypeIds.Ranking,
      DateTimeOffset.UtcNow,
      BenchmarkLiveStateIds.Running,
      changedHarness,
      Message: "Ranking is provisional while any harness remains unfinished.",
      Ranking: ranking
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
    var completed = tests.Count(test => string.Equals(
      test.RawResult.ExecutionStatus,
      BenchmarkExecutionStatusIds.Completed,
      StringComparison.Ordinal
    ));
    var score = tests.Sum(test => test.Score?.Total ?? 0m) / totalTests;
    return new BenchmarkHarnessResult(
      harness.Adapter.Definition.Id,
      harness.Availability.Version,
      passed,
      totalTests,
      decimal.Round(score, 2, MidpointRounding.AwayFromZero),
      duration,
      (int)Math.Round(completed * 100m / totalTests, MidpointRounding.AwayFromZero),
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
      BenchmarkSuiteIds.BasicCrud,
      BenchmarkSuiteIds.BasicCrudVersion,
      BenchmarkSuiteIds.FixtureId,
      BenchmarkSuiteIds.FixtureVersion,
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
      _scorer.Score(raw),
      Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds)
    );
  }

  private sealed record ResolvedBenchmarkHarness(
    IAgentHarness Adapter,
    HarnessAvailability Availability
  );

  private sealed record FinalSnapshotResult(
    BenchmarkWorkspaceSnapshot Snapshot,
    BenchmarkHarnessEvidence Evidence
  );
}
