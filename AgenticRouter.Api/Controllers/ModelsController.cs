using System.Diagnostics;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Models;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IModelDiagnosticService _diagnostics;
  private readonly IToolProtocolConformanceService _toolConformance;
  private readonly IResidentModelManager _residentModel;
  private readonly ILogger<ModelsController> _logger;

  public ModelsController(
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IModelDiagnosticService diagnostics,
    IToolProtocolConformanceService toolConformance,
    IResidentModelManager residentModel,
    ILogger<ModelsController> logger
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _diagnostics = diagnostics;
    _toolConformance = toolConformance;
    _residentModel = residentModel;
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
          "provider-registry",
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
        exception is RoutedProviderException routed
          ? routed.Provider
          : ModelProviderIds.OllamaLocal,
        exception is RoutedProviderException routedModel
          ? routedModel.Model
          : null,
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

  [HttpPost("conformance")]
  public async Task<IActionResult> Conformance(
    [FromBody] ModelConformanceBenchmarkRequest request,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(
      request.Model
    ))
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "The protocol benchmark could not start.",
          new Dictionary<string, string[]>
          {
            ["model"] =
            [
              "A model must be selected."
            ]
          }
        )
      );
    }

    if (!CoordinationConformanceProfiles.IsKnown(
      request.Profile
    ))
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "The protocol benchmark could not start.",
          new Dictionary<string, string[]>
          {
            ["profile"] =
            [
              "Profile must be native-strict, native-adaptive, structured-action, or guidance-only."
            ]
          }
        )
      );
    }

    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var baseUri = new Uri(
      settings.OllamaUrl,
      UriKind.Absolute
    );
    var reference = ProviderModelReference.Parse(
      request.Model
    );

    if (
      !reference.IsLocal
      && !request.ExternalProviderPermissionGranted
    )
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "The cloud protocol benchmark requires explicit permission.",
          new Dictionary<string, string[]>
          {
            ["externalProviderPermissionGranted"] =
            [
              "Confirm that this benchmark may make real provider calls and consume quota."
            ]
          }
        )
      );
    }
    IReadOnlyList<InstalledModel> installed;

    try
    {
      installed = await _ollamaClient.GetModelsAsync(
        baseUri,
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      return StatusCode(
        StatusCodes.Status503ServiceUnavailable,
        new ProviderError(
          exception.Stage,
          exception.Message,
          exception.TechnicalMessage,
          HttpContext.TraceIdentifier,
          "ollama",
          request.Model,
          null,
          exception.HttpStatus,
          exception.Recoverable
        )
      );
    }

    var selected = installed.FirstOrDefault(
      model => string.Equals(
        model.Name,
        request.Model,
        StringComparison.OrdinalIgnoreCase
      )
    );

    if (selected is null)
    {
      return BadRequest(
        new ValidationErrorsResponse(
          "The protocol benchmark could not start.",
          new Dictionary<string, string[]>
          {
            ["model"] =
            [
              $"Model '{request.Model}' is unavailable in the configured provider registry."
            ]
          }
        )
      );
    }

    using var requestLease = _residentModel.BeginRequest();
    var routerEvicted = false;
    var stopwatch = Stopwatch.StartNew();

    try
    {
      routerEvicted = reference.IsLocal
        && await _residentModel.EvictForRecoveryAsync(
          selected.Name,
          cancellationToken
        );
      var result = await _toolConformance.VerifyPathAsync(
        baseUri,
        selected.Name,
        selected.Digest,
        request.Profile,
        new ProviderCallContext(
          null,
          null,
          HttpContext.TraceIdentifier,
          null,
          UsageModelRoles.Benchmark,
          "tool-protocol-conformance",
          selected.Digest
        ),
        cancellationToken
      );

      return Ok(
        new ModelConformanceBenchmarkResult(
          result.Passed,
          result.Model,
          result.Digest,
          result.OllamaVersion,
          stopwatch.ElapsedMilliseconds,
          result.Failure,
          result.Profile,
          result.Status,
          result.Provider,
          result.AdapterVersion,
          result.BenchmarkVersion,
          result.Identity
        )
      );
    }
    finally
    {
      if (
        routerEvicted
        && request.RestoreResidentModel
      )
      {
        try
        {
          await _residentModel.RestoreAfterRecoveryAsync(
            selected.Name,
            CancellationToken.None
          );
        }
        catch (Exception exception) when (
          exception is OllamaProviderException
          or InvalidOperationException
        )
        {
          _logger.LogWarning(
            exception,
            "The resident coordinator model could not be restored after benchmarking {Model}.",
            selected.Name
          );
        }
      }
    }
  }
}
