using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Models;
using AgenticRouter.Api.Providers.Ollama;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IModelDiagnosticService _diagnostics;
  private readonly ILogger<ModelsController> _logger;

  public ModelsController(
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IModelDiagnosticService diagnostics,
    ILogger<ModelsController> logger
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _diagnostics = diagnostics;
    _logger = logger;
  }

  [HttpGet]
  public async Task<ActionResult<ModelsResponse>> Get(
    CancellationToken cancellationToken
  )
  {
    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var models = await _ollamaClient.GetModelsAsync(
        new Uri(
          settings.OllamaUrl,
          UriKind.Absolute
        ),
        cancellationToken
      );

      return Ok(
        new ModelsResponse(
          "ollama",
          true,
          models,
          null
        )
      );
    }
    catch (OllamaProviderException exception)
    {
      _logger.LogWarning(
        exception,
        "Ollama model discovery failed."
      );
      var error = new ProviderError(
        exception.Stage,
        exception.Message,
        exception.TechnicalMessage,
        HttpContext.TraceIdentifier,
        "ollama",
        null,
        null,
        exception.HttpStatus,
        exception.Recoverable
      );

      return Ok(
        new ModelsResponse(
          "ollama",
          false,
          [],
          error
        )
      );
    }
  }

  [HttpGet("diagnostics")]
  public async Task<ActionResult<ModelDiagnosticsResponse>> GetDiagnostics(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _diagnostics.GetAsync(
        cancellationToken
      )
    );
  }

  [HttpPost("test")]
  public async Task<ActionResult<ModelTestResult>> Test(
    [FromBody] ModelTestRequest request,
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _diagnostics.TestAsync(
        request.Model,
        HttpContext.TraceIdentifier,
        cancellationToken
      )
    );
  }
}
