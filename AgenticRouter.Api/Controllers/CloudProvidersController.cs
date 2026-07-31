using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Cloud;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/cloud-providers")]
public sealed class CloudProvidersController : ControllerBase
{
  private readonly ICloudProviderRegistry _registry;
  private readonly IProtectedSecretStore _secretStore;
  private readonly ISettingsStore _settingsStore;
  private readonly IProviderHealthMonitor _health;

  public CloudProvidersController(
    ICloudProviderRegistry registry,
    IProtectedSecretStore secretStore,
    ISettingsStore settingsStore,
    IProviderHealthMonitor health
  )
  {
    _registry = registry;
    _secretStore = secretStore;
    _settingsStore = settingsStore;
    _health = health;
  }

  [HttpGet]
  public async Task<ActionResult<CloudProvidersView>> Get(
    CancellationToken cancellationToken
  )
  {
    return Ok(
      await _registry.GetViewAsync(
        cancellationToken
      )
    );
  }

  [HttpPut("{providerId}/key")]
  public async Task<IActionResult> SaveKey(
    string providerId,
    [FromBody] SaveCloudProviderKeyRequest request,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        request.ApiKey
      )
      || request.ApiKey.Length > 16_384
    )
    {
      return BadRequest(
        Error(
          "provider-key-invalid",
          providerId,
          "Enter a valid API key."
        )
      );
    }

    string? newReference = null;

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var previous = CloudProviderRegistry.GetSettings(
        settings,
        providerId
      );
      newReference = await _secretStore.StoreAsync(
        providerId,
        request.ApiKey,
        cancellationToken
      );
      var updated = CloudProviderRegistry.SetSettings(
        settings,
        providerId,
        previous with
        {
          Enabled = true,
          SecretReference = newReference
        }
      );
      var saved = await _settingsStore.SaveAsync(
        updated,
        cancellationToken
      );

      if (!saved.IsValid)
      {
        await _secretStore.DeleteAsync(
          providerId,
          newReference,
          CancellationToken.None
        );

        return BadRequest(
          new
          {
            code = "provider-settings-invalid",
            provider = providerId,
            message = "The protected key was not attached because provider settings are invalid.",
            errors = saved.Errors
          }
        );
      }

      if (!string.IsNullOrWhiteSpace(
        previous.SecretReference
      ))
      {
        await _secretStore.DeleteAsync(
          providerId,
          previous.SecretReference,
          CancellationToken.None
        );
      }

      _registry.Invalidate(
        providerId
      );
      _health.Reset(
        providerId
      );
      return Ok(
        await _registry.GetViewAsync(
          cancellationToken
        )
      );
    }
    catch (Exception exception) when (
      exception is CloudProviderException
      or SecretStorageException
    )
    {
      if (newReference is not null)
      {
        await _secretStore.DeleteAsync(
          providerId,
          newReference,
          CancellationToken.None
        );
      }

      return BadRequest(
        Error(
          exception is CloudProviderException cloud
            ? cloud.Code
            : "secret-storage-failed",
          providerId,
          exception.Message
        )
      );
    }
  }

  [HttpDelete("{providerId}/key")]
  public async Task<IActionResult> RemoveKey(
    string providerId,
    [FromQuery] bool confirmed,
    CancellationToken cancellationToken
  )
  {
    if (!confirmed)
    {
      return BadRequest(
        Error(
          "confirmation-required",
          providerId,
          "Confirm removal before deleting the protected key."
        )
      );
    }

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var previous = CloudProviderRegistry.GetSettings(
        settings,
        providerId
      );
      var updated = CloudProviderRegistry.SetSettings(
        settings,
        providerId,
        previous with
        {
          Enabled = false,
          SecretReference = null
        }
      );
      var saved = await _settingsStore.SaveAsync(
        updated,
        cancellationToken
      );

      if (!saved.IsValid)
      {
        return BadRequest(
          new
          {
            code = "provider-settings-invalid",
            provider = providerId,
            message = "The protected key was not removed because provider settings are invalid.",
            errors = saved.Errors
          }
        );
      }

      await _secretStore.DeleteAsync(
        providerId,
        previous.SecretReference,
        cancellationToken
      );
      _registry.Invalidate(
        providerId
      );
      _health.Reset(
        providerId
      );

      return Ok(
        await _registry.GetViewAsync(
          cancellationToken
        )
      );
    }
    catch (Exception exception) when (
      exception is CloudProviderException
      or SecretStorageException
    )
    {
      return BadRequest(
        Error(
          exception is CloudProviderException cloud
            ? cloud.Code
            : "secret-storage-failed",
          providerId,
          exception.Message
        )
      );
    }
  }

  [HttpPost("{providerId}/test")]
  public Task<IActionResult> Test(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    return RunAsync(
      providerId,
      true,
      cancellationToken
    );
  }

  [HttpPost("{providerId}/models/refresh")]
  public Task<IActionResult> Refresh(
    string providerId,
    CancellationToken cancellationToken
  )
  {
    return RunAsync(
      providerId,
      false,
      cancellationToken
    );
  }

  private async Task<IActionResult> RunAsync(
    string providerId,
    bool test,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return Ok(
        test
          ? await _registry.TestAsync(
            providerId,
            cancellationToken
          )
          : await _registry.RefreshAsync(
            providerId,
            cancellationToken
          )
      );
    }
    catch (CloudProviderException exception)
    {
      var payload = new
      {
        exception.Code,
        exception.Stage,
        exception.Provider,
        exception.Model,
        exception.Message,
        exception.HttpStatus,
        exception.Retryable,
        exception.RateLimit,
        exception.TraceId
      };

      return exception.HttpStatus switch
      {
        401 or 403 => StatusCode(
          StatusCodes.Status401Unauthorized,
          payload
        ),
        404 => NotFound(
          payload
        ),
        429 => StatusCode(
          StatusCodes.Status429TooManyRequests,
          payload
        ),
        _ => BadRequest(
          payload
        )
      };
    }
  }

  private static object Error(
    string code,
    string provider,
    string message
  )
  {
    return new
    {
      code,
      stage = "cloud-provider-settings",
      provider,
      message,
      retryable = false,
      traceId = Guid.NewGuid().ToString(
        "N"
      )
    };
  }
}
