using System.Text.Json;
using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/benchmarks")]
public sealed class BenchmarksController : ControllerBase
{
  private readonly IBenchmarkEngine _engine;
  private readonly IBenchmarkTestRegistry _tests;
  private readonly IHarnessRegistry _harnesses;
  private readonly IBenchmarkResultStore _results;
  private readonly IBenchmarkRunCancellationRegistry _cancellations;
  private readonly IBenchmarkLiveRunCoordinator _liveRuns;
  private readonly IBenchmarkScorer _scorer;
  private readonly IBenchmarkScoringProfileStore _scoringProfiles;
  private readonly IBenchmarkHistoryService _history;
  private readonly IBenchmarkRecommendationService _recommendations;
  private readonly IBenchmarkWorkspaceFactory _workspaces;
  private readonly IBenchmarkRecommendationStore _recommendationStore;
  private readonly IFolderLauncherService _folderLauncher;
  private readonly ISettingsStore _settingsStore;

  public BenchmarksController(
    IBenchmarkEngine engine,
    IBenchmarkTestRegistry tests,
    IHarnessRegistry harnesses,
    IBenchmarkResultStore results,
    IBenchmarkRunCancellationRegistry cancellations,
    IBenchmarkLiveRunCoordinator liveRuns,
    IBenchmarkScorer scorer,
    IBenchmarkScoringProfileStore scoringProfiles,
    IBenchmarkHistoryService history,
    IBenchmarkRecommendationService recommendations,
    IBenchmarkWorkspaceFactory workspaces,
    IBenchmarkRecommendationStore recommendationStore,
    IFolderLauncherService folderLauncher,
    ISettingsStore settingsStore
  )
  {
    _engine = engine;
    _tests = tests;
    _harnesses = harnesses;
    _results = results;
    _cancellations = cancellations;
    _liveRuns = liveRuns;
    _scorer = scorer;
    _scoringProfiles = scoringProfiles;
    _history = history;
    _recommendations = recommendations;
    _workspaces = workspaces;
    _recommendationStore = recommendationStore;
    _folderLauncher = folderLauncher;
    _settingsStore = settingsStore;
  }

  [HttpGet("scoring-profile")]
  public async Task<IActionResult> GetScoringProfile(
    CancellationToken cancellationToken
  )
  {
    return Ok(await _scoringProfiles.GetAsync(cancellationToken));
  }

  [HttpGet("recommendation-catalog")]
  public async Task<IActionResult> RecommendationCatalog(
    CancellationToken cancellationToken
  )
  {
    return Ok(await _recommendations.GetCatalogAsync(cancellationToken));
  }

  [HttpPost("recommendations")]
  public async Task<IActionResult> Recommend(
    [FromBody] BenchmarkRecommendationRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _recommendations.RecommendAsync(request, cancellationToken));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpGet("recommendations/{recommendationId}")]
  public async Task<IActionResult> GetRecommendation(
    string recommendationId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await _recommendations.GetAsync(
        recommendationId,
        cancellationToken
      );
      return result is null ? NotFound() : Ok(result);
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpPut("scoring-profile")]
  public async Task<IActionResult> SaveScoringProfile(
    [FromBody] BenchmarkScoreWeights weights,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _scoringProfiles.SaveCustomAsync(weights, cancellationToken));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpPost("scoring-profile/reset")]
  public async Task<IActionResult> ResetScoringProfile(
    CancellationToken cancellationToken
  )
  {
    return Ok(await _scoringProfiles.ResetAsync(cancellationToken));
  }

  [HttpPost("suite-runs/{runId}/rescore")]
  public async Task<IActionResult> Rescore(
    string runId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      if (result is null)
      {
        return NotFound();
      }
      if (result.BenchmarkMode == BenchmarkModeIds.Manual
        && (result.SuiteId == BenchmarkSuiteIds.Manual
          || result.SelectedSuites?.All(selection =>
            selection.Id == BenchmarkSuiteIds.Manual) == true))
      {
        return Ok(result);
      }
      var profile = await _scoringProfiles.GetAsync(cancellationToken);
      return Ok(_scorer.Rescore(result, profile));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpPut("suite-runs/{runId}/results/{testRunId}/review")]
  public async Task<IActionResult> SaveManualReview(
    string runId,
    string testRunId,
    [FromBody] BenchmarkUserReviewRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      if (request.Score is < 0 or > 100)
      {
        throw new BenchmarkRequestException(
          "benchmark-review-score-invalid",
          "User score must be between 0 and 100.",
          "score"
        );
      }
      if (request.Notes?.Length > 10_000)
      {
        throw new BenchmarkRequestException(
          "benchmark-review-notes-too-large",
          "Review notes cannot exceed 10000 characters.",
          "notes"
        );
      }
      var result = await _results.GetAsync(runId, cancellationToken);
      if (result is null)
      {
        return NotFound();
      }
      if (result.BenchmarkMode != BenchmarkModeIds.Manual)
      {
        throw new BenchmarkRequestException(
          "benchmark-review-not-manual",
          "User quality reviews apply only to Manual / Custom Prompt benchmarks.",
          "runId"
        );
      }
      var updated = ApplyManualReview(result, testRunId, request);
      await _results.UpdateAsync(updated, cancellationToken);
      return Ok(updated);
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpPost("suite-runs/{runId}/rerun")]
  public async Task<IActionResult> RerunManual(
    string runId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      if (result is null)
      {
        return NotFound();
      }
      if (result.BenchmarkMode != BenchmarkModeIds.Manual
        || result.CustomPrompt is null)
      {
        throw new BenchmarkRequestException(
          "benchmark-rerun-not-manual",
          "Only a persisted Manual / Custom Prompt benchmark can use this rerun endpoint.",
          "runId"
        );
      }
      var models = result.SelectedModels ?? [result.Model];
      var harnesses = result.SelectedHarnesses
        ?? result.Cells?.Select(cell => cell.Harness).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        ?? result.HarnessResults.Select(harness => harness.Harness).ToArray();
      var selections = result.SelectedSuites is { Count: > 0 }
        ? result.SelectedSuites
        : [new BenchmarkSuiteSelection(BenchmarkSuiteIds.Manual, BenchmarkSuiteIds.ManualVersion)];
      var request = new BenchmarkSuiteRunRequest(
        models[0],
        harnesses,
        selections[0].Id,
        selections[0].Version,
        result.TimeoutSeconds,
        true,
        ClientRunId: Guid.NewGuid().ToString("N"),
        Models: models,
        ScoringProfileId: result.ScoringProfileId,
        ScoreWeights: result.ScoreWeights,
        ContextTokens: result.Configuration?.ContextTokens
          ?? result.Environment?.ConfiguredContextTokens,
        Suites: selections,
        BenchmarkMode: BenchmarkModeIds.Manual,
        CustomPrompt: result.CustomPrompt,
        RunName: result.RunName,
        RerunOfRunId: result.RunId
      );
      return Accepted(await _liveRuns.StartAsync(request, cancellationToken));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpPost("suite-runs/live")]
  public async Task<IActionResult> StartLiveSuite(
    [FromBody] BenchmarkSuiteRunRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Accepted(await _liveRuns.StartAsync(request, cancellationToken));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpGet("suite-runs/live")]
  public IActionResult ListLiveSuites()
  {
    return Ok(_liveRuns.ListViews());
  }

  [HttpGet("catalog")]
  public async Task<IActionResult> Catalog(CancellationToken cancellationToken)
  {
    var suites = _tests.GetSuites();
    var suite = suites.Single(item => item.Id == BenchmarkSuiteIds.BasicCrud
      && item.Version == BenchmarkSuiteIds.BasicCrudVersion);
    var harnesses = await _harnesses.DiscoverAsync(cancellationToken);
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var benchmarkContext = settings.OllamaRuntime.RoleDefaults[
      OllamaRuntimeRoleIds.Benchmark
    ];
    return Ok(new
    {
      suite,
      suites,
      harnesses,
      defaultTimeoutSeconds = 120,
      minimumTimeoutSeconds = 5,
      maximumTimeoutSeconds = 1600,
      defaultContextTokens = benchmarkContext.TargetContextTokens,
      minimumContextTokens = settings.OllamaRuntime.ContextEscalationLadder.Min(),
      maximumContextTokens = settings.Context.ProviderContextTokens,
      contextPresets = settings.OllamaRuntime.ContextEscalationLadder
        .Where(value => value <= settings.Context.ProviderContextTokens)
        .ToArray(),
      defaultGpu = settings.DefaultGpu,
      scoreWeights = BenchmarkScoreWeights.Default
    });
  }

  [HttpPost("runs")]
  public async Task<IActionResult> Run(
    [FromBody] BenchmarkRunRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _engine.RunAsync(request, cancellationToken));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpPost("suite-runs")]
  public async Task<IActionResult> RunSuite(
    [FromBody] BenchmarkSuiteRunRequest request,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(await _engine.RunSuiteAsync(request, cancellationToken));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpGet("suite-runs")]
  public async Task<IActionResult> List(
    [FromQuery] int limit = 25,
    CancellationToken cancellationToken = default
  )
  {
    return Ok(await _results.ListAsync(limit, cancellationToken));
  }

  [HttpGet("history")]
  public async Task<IActionResult> History(
    [FromQuery] int limit = 50,
    [FromQuery] string? model = null,
    [FromQuery] string? harness = null,
    [FromQuery] string? suite = null,
    CancellationToken cancellationToken = default
  )
  {
    return Ok(await _history.ListAsync(
      limit,
      model,
      harness,
      suite,
      cancellationToken
    ));
  }

  [HttpGet("comparisons")]
  public async Task<IActionResult> Compare(
    [FromQuery] string baselineRunId,
    [FromQuery] string candidateRunId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var comparison = await _history.CompareAsync(
        new BenchmarkComparisonRequest(baselineRunId, candidateRunId),
        cancellationToken
      );
      return comparison is null ? NotFound() : Ok(comparison);
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpGet("suite-runs/{runId}")]
  public async Task<IActionResult> Get(
    string runId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      return result is null ? NotFound() : Ok(result);
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpGet("suite-runs/{runId}/raw")]
  public async Task<IActionResult> Raw(
    string runId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      return result is null ? NotFound() : Ok(result);
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpGet("suite-runs/{runId}/workspaces/{workspaceId}")]
  public async Task<IActionResult> GetWorkspace(
    string runId,
    string workspaceId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      if (result is null || !OwnsWorkspace(result, workspaceId))
      {
        return NotFound();
      }
      return Ok(await _workspaces.GetStatusAsync(workspaceId, cancellationToken));
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpDelete("suite-runs/{runId}/workspaces/{workspaceId}")]
  public async Task<IActionResult> DeleteWorkspace(
    string runId,
    string workspaceId,
    [FromQuery] bool confirmed = false,
    CancellationToken cancellationToken = default
  )
  {
    if (!confirmed)
    {
      return BadRequest(new { message = "Workspace deletion must be confirmed." });
    }
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      if (result is null || !OwnsWorkspace(result, workspaceId))
      {
        return NotFound();
      }
      var before = await _workspaces.GetStatusAsync(workspaceId, cancellationToken);
      var deleted = await _workspaces.CleanupAsync(workspaceId, cancellationToken);
      return deleted
        ? Ok(new
        {
          workspaceId = before.WorkspaceId,
          workspacePath = before.WorkspacePath,
          available = false,
          deleted = before.Available
        })
        : Conflict(new { message = "The retained workspace could not be deleted." });
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
    )
    {
      return Conflict(new { message = exception.Message });
    }
  }

  [HttpPost("suite-runs/{runId}/workspaces/{workspaceId}/open-folder")]
  public async Task<IActionResult> OpenWorkspaceFolder(
    string runId,
    string workspaceId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      if (result is null || !OwnsWorkspace(result, workspaceId))
      {
        return NotFound();
      }
      var workspace = await _workspaces.GetStatusAsync(workspaceId, cancellationToken);
      if (!workspace.Available)
      {
        return BadRequest(new { message = "The retained workspace is no longer available." });
      }
      var opened = await _folderLauncher.OpenAsync(
        workspace.WorkspacePath,
        cancellationToken
      );
      return opened.Opened
        ? Ok(opened)
        : BadRequest(new
        {
          message = opened.Error ?? "The retained workspace folder could not be opened."
        });
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
  }

  [HttpDelete("suite-runs/{runId}")]
  public async Task<IActionResult> DeleteResult(
    string runId,
    [FromQuery] bool confirmed = false,
    CancellationToken cancellationToken = default
  )
  {
    if (!confirmed)
    {
      return BadRequest(new { message = "Benchmark result deletion must be confirmed." });
    }
    try
    {
      var result = await _results.GetAsync(runId, cancellationToken);
      if (result is null)
      {
        return NotFound();
      }
      await DeleteOwnedWorkspacesAsync([result], cancellationToken);
      var deleted = await _results.DeleteAsync(runId, cancellationToken);
      var recommendationsDeleted = await _recommendationStore.DeleteReferencingRunAsync(
        runId,
        cancellationToken
      );
      return Ok(new { runId = result.RunId, deleted, recommendationsDeleted });
    }
    catch (BenchmarkRequestException exception)
    {
      return InvalidRequest(exception);
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
    )
    {
      return Conflict(new { message = exception.Message });
    }
  }

  [HttpDelete("suite-runs")]
  public async Task<IActionResult> DeleteAllResults(
    [FromQuery] bool confirmed = false,
    CancellationToken cancellationToken = default
  )
  {
    if (!confirmed)
    {
      return BadRequest(new { message = "Deleting all benchmark results must be confirmed." });
    }
    try
    {
      var results = await _results.ListAllAsync(cancellationToken);
      await DeleteOwnedWorkspacesAsync(results, cancellationToken);
      var deleted = await _results.DeleteAllAsync(cancellationToken);
      var recommendationsDeleted = await _recommendationStore.DeleteAllAsync(
        cancellationToken
      );
      return Ok(new { deleted, recommendationsDeleted });
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
    )
    {
      return Conflict(new { message = exception.Message });
    }
  }

  [HttpGet("suite-runs/{runId}/live")]
  public async Task<IActionResult> GetLive(
    string runId,
    CancellationToken cancellationToken = default
  )
  {
    if (!TryNormalizeRunId(runId, out var normalized, out var invalid))
    {
      return invalid!;
    }
    if (_liveRuns.TryGetView(normalized!, out var view))
    {
      return Ok(view);
    }
    var persisted = await _results.GetAsync(normalized!, cancellationToken);
    if (persisted is null)
    {
      return NotFound();
    }
    var completed = CreateRecoveredCompletionEvent(persisted, 1);
    return Ok(new BenchmarkLiveRunView(
      persisted.RunId,
      Terminal: true,
      CancellationRequested: string.Equals(
        persisted.TerminalState,
        BenchmarkRunStatusIds.Cancelled,
        StringComparison.Ordinal
      ),
      LastSequence: completed.Sequence,
      Events: [completed]
    ));
  }

  [HttpGet("suite-runs/{runId}/events")]
  public async Task<IActionResult> Events(
    string runId,
    [FromQuery] long after = 0,
    CancellationToken cancellationToken = default
  )
  {
    if (!TryNormalizeRunId(runId, out var normalized, out var invalid))
    {
      return invalid!;
    }
    var live = _liveRuns.TryGetView(normalized!, out _);
    BenchmarkSuiteRunResult? persisted = null;
    if (!live)
    {
      persisted = await _results.GetAsync(normalized!, cancellationToken);
      if (persisted is null)
      {
        return NotFound();
      }
    }
    if (
      Request.Headers.TryGetValue("Last-Event-ID", out var eventId)
      && long.TryParse(eventId.ToString(), out var parsedEventId)
    )
    {
      after = Math.Max(after, parsedEventId);
    }
    Response.StatusCode = StatusCodes.Status200OK;
    Response.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers.Append("X-Accel-Buffering", "no");
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    if (persisted is not null)
    {
      var recoveredSequence = after == long.MaxValue
        ? long.MaxValue
        : Math.Max(1, after + 1);
      await WriteBenchmarkEventAsync(
        CreateRecoveredCompletionEvent(persisted, recoveredSequence),
        jsonOptions,
        cancellationToken
      );
      return new EmptyResult();
    }
    await foreach (var progressEvent in _liveRuns.SubscribeAsync(
      normalized!,
      after,
      cancellationToken
    ))
    {
      await WriteBenchmarkEventAsync(progressEvent, jsonOptions, cancellationToken);
    }
    return new EmptyResult();
  }

  private async Task WriteBenchmarkEventAsync(
    BenchmarkProgressEvent progressEvent,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken
  )
  {
    await Response.WriteAsync($"id: {progressEvent.Sequence}\n", cancellationToken);
    await Response.WriteAsync("event: benchmark\n", cancellationToken);
    await Response.WriteAsync(
      $"data: {JsonSerializer.Serialize(progressEvent, jsonOptions)}\n\n",
      cancellationToken
    );
    await Response.Body.FlushAsync(cancellationToken);
  }

  private static BenchmarkProgressEvent CreateRecoveredCompletionEvent(
    BenchmarkSuiteRunResult result,
    long sequence
  )
  {
    return new BenchmarkProgressEvent(
      result.RunId,
      BenchmarkProgressTypeIds.RunCompleted,
      result.EndedAt,
      result.TerminalState,
      Message: "Recovered the persisted benchmark result after the live session ended.",
      ElapsedMilliseconds: result.DurationMilliseconds,
      FinalResult: result,
      Sequence: sequence
    );
  }

  [HttpPost("suite-runs/{runId}/cancel")]
  public IActionResult Cancel(string runId)
  {
    if (!Guid.TryParse(runId, out var parsed))
    {
      return InvalidRequest(
        new BenchmarkRequestException(
          "benchmark-run-id-invalid",
          "Benchmark run id must be a UUID.",
          "runId"
        )
      );
    }
    var normalized = parsed.ToString("N");
    return (_liveRuns.Cancel(normalized) || _cancellations.Cancel(normalized))
      ? Accepted(new { runId = parsed.ToString("N"), cancellationRequested = true })
      : NotFound(new { runId = parsed.ToString("N"), cancellationRequested = false });
  }

  private bool TryNormalizeRunId(
    string runId,
    out string? normalized,
    out IActionResult? invalid
  )
  {
    if (Guid.TryParse(runId, out var parsed))
    {
      normalized = parsed.ToString("N");
      invalid = null;
      return true;
    }
    normalized = null;
    invalid = InvalidRequest(new BenchmarkRequestException(
      "benchmark-run-id-invalid",
      "Benchmark run id must be a UUID.",
      "runId"
    ));
    return false;
  }

  private static bool OwnsWorkspace(
    BenchmarkSuiteRunResult result,
    string workspaceId
  )
  {
    return AllTests(result)
      .Any(test => string.Equals(
        test.Run.WorkspaceId,
        workspaceId,
        StringComparison.OrdinalIgnoreCase
      ));
  }

  private static BenchmarkSuiteRunResult ApplyManualReview(
    BenchmarkSuiteRunResult result,
    string testRunId,
    BenchmarkUserReviewRequest request
  )
  {
    var found = false;
    var review = new BenchmarkUserReview(
      request.Score,
      string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes,
      DateTimeOffset.UtcNow
    );
    BenchmarkRunResult UpdateTest(BenchmarkRunResult test)
    {
      if (!string.Equals(test.Run.RunId, testRunId, StringComparison.OrdinalIgnoreCase))
      {
        return test;
      }
      if (test.Run.SuiteId != BenchmarkSuiteIds.Manual)
      {
        throw new BenchmarkRequestException(
          "benchmark-review-not-custom-prompt",
          "Only the Custom Prompt test accepts a user quality score.",
          "testRunId"
        );
      }
      if (test.ReviewStatus == BenchmarkReviewStatusIds.TechnicalFailure
        || test.RawResult.ExecutionStatus != BenchmarkExecutionStatusIds.Completed
        || test.RawResult.Error is not null)
      {
        throw new BenchmarkRequestException(
          "benchmark-review-technical-failure",
          "A technical execution failure does not accept a user quality score.",
          "testRunId"
        );
      }
      found = true;
      return test with
      {
        ReviewStatus = BenchmarkReviewStatusIds.Reviewed,
        UserReview = review
      };
    }
    BenchmarkHarnessResult UpdateHarness(BenchmarkHarnessResult harness) => harness with
    {
      Tests = harness.Tests.Select(UpdateTest).ToArray()
    };
    var harnessResults = result.HarnessResults.Select(UpdateHarness).ToArray();
    var cells = (result.Cells ?? []).Select(cell => cell.Result is null
      ? cell
      : cell with
      {
        Result = UpdateHarness(cell.Result),
        ReviewStatus = cell.Result.Tests.Any(test => string.Equals(
          test.Run.RunId,
          testRunId,
          StringComparison.OrdinalIgnoreCase
        )) ? BenchmarkReviewStatusIds.Reviewed : cell.ReviewStatus,
        UserScore = cell.Result.Tests.Any(test => string.Equals(
          test.Run.RunId,
          testRunId,
          StringComparison.OrdinalIgnoreCase
        )) ? request.Score : cell.UserScore
      }).ToArray();
    if (!found)
    {
      throw new BenchmarkRequestException(
        "benchmark-review-result-unknown",
        $"Manual benchmark result '{testRunId}' is unavailable.",
        "testRunId"
      );
    }
    var reviewedCells = cells.Where(cell => cell.UserScore.HasValue)
      .OrderByDescending(cell => cell.UserScore)
      .ThenBy(cell => cell.DurationMilliseconds)
      .ThenBy(cell => cell.Model, StringComparer.OrdinalIgnoreCase)
      .ThenBy(cell => cell.Harness, StringComparer.OrdinalIgnoreCase)
      .ToArray();
    var ranking = reviewedCells.Select((cell, index) => new BenchmarkMatrixRankingEntry(
      index + 1,
      cell.Model,
      cell.Harness,
      cell.Passed,
      cell.UserScore!.Value,
      cell.DurationMilliseconds,
      cell.Terminality,
      cell.Status
    )).ToArray();
    var awaiting = cells.SelectMany(cell => cell.Result?.Tests ?? [])
      .Any(test => test.ReviewStatus == BenchmarkReviewStatusIds.AwaitingUserReview);
    var manualOnly = AllTests(result).All(test =>
      test.Run.SuiteId == BenchmarkSuiteIds.Manual);
    return result with
    {
      HarnessResults = harnessResults,
      Cells = cells,
      PairRanking = manualOnly ? ranking : result.PairRanking,
      ReviewStatus = awaiting
        ? BenchmarkReviewStatusIds.AwaitingUserReview
        : BenchmarkReviewStatusIds.Reviewed
    };
  }

  private async Task DeleteOwnedWorkspacesAsync(
    IEnumerable<BenchmarkSuiteRunResult> results,
    CancellationToken cancellationToken
  )
  {
    var workspaceIds = results
      .SelectMany(AllTests)
      .Where(test => !test.WorkspaceCleanedUp)
      .Select(test => test.Run.WorkspaceId)
      .Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var workspaceId in workspaceIds)
    {
      if (!await _workspaces.CleanupAsync(workspaceId, cancellationToken))
      {
        throw new IOException(
          $"Retained workspace '{workspaceId}' could not be deleted."
        );
      }
    }
  }

  private static IEnumerable<BenchmarkRunResult> AllTests(
    BenchmarkSuiteRunResult result
  )
  {
    return result.HarnessResults.SelectMany(harness => harness.Tests)
      .Concat((result.Cells ?? []).SelectMany(cell => cell.Result?.Tests ?? []))
      .GroupBy(test => test.Run.RunId, StringComparer.OrdinalIgnoreCase)
      .Select(group => group.First());
  }

  private BadRequestObjectResult InvalidRequest(BenchmarkRequestException exception)
  {
    return BadRequest(
      new ValidationErrorsResponse(
        "The benchmark could not start.",
        new Dictionary<string, string[]>
        {
          [exception.Field] = [exception.Message]
        }
      )
    );
  }
}
