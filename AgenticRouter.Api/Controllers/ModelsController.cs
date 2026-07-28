using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly IOllamaClient _ollamaClient;
    private readonly ILogger<ModelsController> _logger;

    public ModelsController(
      ISettingsStore settingsStore,
      IOllamaClient ollamaClient,
      ILogger<ModelsController> logger
    )
    {
        _settingsStore = settingsStore;
        _ollamaClient = ollamaClient;
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
}
