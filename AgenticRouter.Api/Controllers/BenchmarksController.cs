using System.Text.Json;
using AgenticRouter.Api.Benchmarking;
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

  public BenchmarksController(
    IBenchmarkEngine engine,
    IBenchmarkTestRegistry tests,
    IHarnessRegistry harnesses,
    IBenchmarkResultStore results,
    IBenchmarkRunCancellationRegistry cancellations,
    IBenchmarkLiveRunCoordinator liveRuns,
    IBenchmarkScorer scorer,
    IBenchmarkScoringProfileStore scoringProfiles
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
  }

  [HttpGet("scoring-profile")]
  public async Task<IActionResult> GetScoringProfile(
    CancellationToken cancellationToken
  )
  {
    return Ok(await _scoringProfiles.GetAsync(cancellationToken));
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
      var profile = await _scoringProfiles.GetAsync(cancellationToken);
      return Ok(_scorer.Rescore(result, profile));
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

  [HttpGet("catalog")]
  public async Task<IActionResult> Catalog(CancellationToken cancellationToken)
  {
    var suites = _tests.GetSuites();
    var suite = suites.Single(item => item.Id == BenchmarkSuiteIds.BasicCrud
      && item.Version == BenchmarkSuiteIds.BasicCrudVersion);
    var harnesses = await _harnesses.DiscoverAsync(cancellationToken);
    return Ok(new
    {
      suite,
      suites,
      harnesses,
      defaultTimeoutSeconds = 120,
      minimumTimeoutSeconds = 5,
      maximumTimeoutSeconds = 600,
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

  [HttpGet("suite-runs/{runId}/live")]
  public IActionResult GetLive(string runId)
  {
    if (!TryNormalizeRunId(runId, out var normalized, out var invalid))
    {
      return invalid!;
    }
    return _liveRuns.TryGetView(normalized!, out var view)
      ? Ok(view)
      : NotFound();
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
    if (!_liveRuns.TryGetView(normalized!, out _))
    {
      return NotFound();
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
    await foreach (var progressEvent in _liveRuns.SubscribeAsync(
      normalized!,
      after,
      cancellationToken
    ))
    {
      await Response.WriteAsync($"id: {progressEvent.Sequence}\n", cancellationToken);
      await Response.WriteAsync("event: benchmark\n", cancellationToken);
      await Response.WriteAsync(
        $"data: {JsonSerializer.Serialize(progressEvent, jsonOptions)}\n\n",
        cancellationToken
      );
      await Response.Body.FlushAsync(cancellationToken);
    }
    return new EmptyResult();
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
