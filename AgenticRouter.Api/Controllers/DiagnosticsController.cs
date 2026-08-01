using AgenticRouter.Api.Observability;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/diagnostics/traces")]
public sealed class DiagnosticsController : ControllerBase
{
  private readonly IIncidentJournal _journal;

  public DiagnosticsController(IIncidentJournal journal)
  {
    _journal = journal;
  }

  [HttpGet("{traceId}")]
  public async Task<ActionResult<IncidentTraceReport>> GetTrace(
    string traceId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var report = await _journal.FindTraceAsync(traceId, cancellationToken);
      return report is null
        ? NotFound(new { code = "diagnostic-trace-not-found", message = "No retained diagnostic events match this exact trace identifier." })
        : Ok(report);
    }
    catch (ArgumentException)
    {
      return BadRequest(new { code = "invalid-trace-identifier", message = "The trace identifier is invalid." });
    }
  }
}
