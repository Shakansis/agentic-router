using System.Text.Json;
using AgenticRouter.Api.Supervision;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/supervision/runs")]
public sealed class SupervisionRunsController : ControllerBase
{
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  );

  private readonly IDurableSupervisionRunCoordinator _runs;

  public SupervisionRunsController(
    IDurableSupervisionRunCoordinator runs
  )
  {
    _runs = runs;
  }

  [HttpPost("prepare")]
  public async Task<IActionResult> Prepare(
    [FromBody] PrepareSupervisionRunRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      async () => Accepted(
        await _runs.PrepareAsync(
          request,
          cancellationToken
        )
      )
    );
  }

  [HttpGet]
  public async Task<IActionResult> List(
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      async () => Ok(
        await _runs.ListAsync(
          cancellationToken
        )
      )
    );
  }

  [HttpPost("{runId}/start")]
  public async Task<IActionResult> Start(
    string runId,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      async () => await _runs.StartAsync(
        runId,
        cancellationToken
      ) is { } view
        ? view.State == DurableSupervisionRunStates.AwaitingUser
          ? Conflict(view)
          : Accepted(view)
        : NotFound()
    );
  }

  [HttpGet("{runId}")]
  public IActionResult Get(string runId)
  {
    try
    {
      return _runs.TryGetView(
        runId,
        out var view
      )
        ? Ok(
          view
        )
        : NotFound();
    }
    catch (SupervisionException exception)
    {
      return Error(
        exception
      );
    }
  }

  [HttpGet("{runId}/events")]
  public async Task<IActionResult> Events(
    string runId,
    [FromQuery] long after = 0,
    [FromQuery] bool follow = true,
    CancellationToken cancellationToken = default
  )
  {
    try
    {
      if (!_runs.TryGetView(
        runId,
        out _
      ))
      {
        return NotFound();
      }

      if (
        Request.Headers.TryGetValue(
          "Last-Event-ID",
          out var eventId
        )
        && long.TryParse(
          eventId.ToString(),
          out var parsedEventId
        )
      )
      {
        after = Math.Max(
          after,
          parsedEventId
        );
      }

      Response.StatusCode = StatusCodes.Status200OK;
      Response.ContentType = "text/event-stream";
      Response.Headers.CacheControl = "no-cache";
      Response.Headers.Append(
        "X-Accel-Buffering",
        "no"
      );

      await foreach (var progressEvent in _runs.SubscribeAsync(
        runId,
        after,
        follow,
        cancellationToken
      ))
      {
        await Response.WriteAsync(
          $"id: {progressEvent.Sequence}\n",
          cancellationToken
        );
        await Response.WriteAsync(
          "event: supervision\n",
          cancellationToken
        );
        await Response.WriteAsync(
          $"data: {JsonSerializer.Serialize(progressEvent, JsonOptions)}\n\n",
          cancellationToken
        );
        await Response.Body.FlushAsync(
          cancellationToken
        );
      }

      return new EmptyResult();
    }
    catch (SupervisionException exception)
    {
      return Error(
        exception
      );
    }
  }

  [HttpPost("{runId}/cancel")]
  public async Task<IActionResult> Cancel(
    string runId,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      async () => await _runs.CancelAsync(
        runId,
        cancellationToken
      ) is { } view
        ? Accepted(
          view
        )
        : NotFound()
    );
  }

  [HttpPost("{runId}/resume")]
  public async Task<IActionResult> Resume(
    string runId,
    [FromBody] ResumeSupervisionRunRequest request,
    CancellationToken cancellationToken
  )
  {
    return await ExecuteAsync(
      async () => await _runs.ResumeAsync(
        runId,
        request,
        cancellationToken
      ) is { } view
        ? view.State == DurableSupervisionRunStates.AwaitingUser
          ? Conflict(
            view
          )
          : Ok(
            view
          )
        : NotFound()
    );
  }

  [HttpDelete("{runId}")]
  public async Task<IActionResult> Discard(
    string runId,
    [FromQuery] bool confirmed,
    CancellationToken cancellationToken
  )
  {
    if (!confirmed)
    {
      return BadRequest(
        new
        {
          code = "supervision-discard-confirmation-required",
          stage = "supervision-discard",
          message = "Discarding durable supervision recovery state requires confirmation.",
          retryable = true
        }
      );
    }

    return await ExecuteAsync(
      async () => await _runs.DiscardAsync(
        runId,
        cancellationToken
      )
        ? NoContent()
        : NotFound()
    );
  }

  private async Task<IActionResult> ExecuteAsync(
    Func<Task<IActionResult>> operation
  )
  {
    try
    {
      return await operation();
    }
    catch (SupervisionException exception)
    {
      return Error(
        exception
      );
    }
  }

  private ObjectResult Error(SupervisionException exception)
  {
    return StatusCode(
      exception.StatusCode,
      new
      {
        code = exception.Code,
        stage = exception.Stage,
        message = exception.Message,
        retryable = exception.Retryable
      }
    );
  }
}
