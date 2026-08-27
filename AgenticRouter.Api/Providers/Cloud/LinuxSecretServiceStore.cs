using System.ComponentModel;
using System.Diagnostics;

namespace AgenticRouter.Api.Providers.Cloud;

public sealed class LinuxSecretServiceStore : IProtectedSecretStore
{
  private const int MaximumSecretLength = 16_384;

  public async Task<string> StoreAsync(
    string providerId,
    string secret,
    CancellationToken cancellationToken
  )
  {
    if (
      !OperatingSystem.IsLinux()
      || string.IsNullOrWhiteSpace(secret)
      || secret.Length > MaximumSecretLength
    )
    {
      throw new SecretStorageException(
        "secret-invalid",
        "The API key could not be protected.",
        false
      );
    }

    var reference = $"secret-{Guid.NewGuid():N}";
    var result = await RunAsync(
      [
        "store",
        $"--label=Agentic Router: {providerId}",
        "service",
        "AgenticRouter",
        "provider",
        providerId,
        "reference",
        reference
      ],
      secret,
      cancellationToken
    );
    if (result.ExitCode != 0)
    {
      throw Failure(
        "secret-storage-failed",
        "Linux Secret Service could not store the API key.",
        result.StandardError,
        true
      );
    }

    return reference;
  }

  public async Task<string?> GetAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  )
  {
    if (!IsValidReference(reference))
    {
      return null;
    }

    var result = await LookupAsync(
      providerId,
      reference!,
      cancellationToken
    );
    if (result.ExitCode == 1)
    {
      return null;
    }
    if (result.ExitCode != 0)
    {
      throw Failure(
        "secret-read-failed",
        "Linux Secret Service could not unlock the API key.",
        result.StandardError,
        true
      );
    }

    return result.StandardOutput.TrimEnd(
      '\r',
      '\n'
    );
  }

  public async Task<bool> ExistsAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  )
  {
    if (!IsValidReference(reference))
    {
      return false;
    }

    var result = await LookupAsync(
      providerId,
      reference!,
      cancellationToken
    );
    if (result.ExitCode == 1)
    {
      return false;
    }
    if (result.ExitCode != 0)
    {
      throw Failure(
        "secret-read-failed",
        "Linux Secret Service could not verify the protected API key.",
        result.StandardError,
        true
      );
    }

    return true;
  }

  public async Task DeleteAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  )
  {
    if (!IsValidReference(reference))
    {
      return;
    }

    var result = await RunAsync(
      [
        "clear",
        "service",
        "AgenticRouter",
        "provider",
        providerId,
        "reference",
        reference!
      ],
      null,
      cancellationToken
    );
    if (result.ExitCode is not 0 and not 1)
    {
      throw Failure(
        "secret-delete-failed",
        "Linux Secret Service could not delete the API key.",
        result.StandardError,
        true
      );
    }
  }

  private static Task<SecretCommandResult> LookupAsync(
    string providerId,
    string reference,
    CancellationToken cancellationToken
  )
  {
    return RunAsync(
      [
        "lookup",
        "service",
        "AgenticRouter",
        "provider",
        providerId,
        "reference",
        reference
      ],
      null,
      cancellationToken
    );
  }

  private static async Task<SecretCommandResult> RunAsync(
    IReadOnlyList<string> arguments,
    string? standardInput,
    CancellationToken cancellationToken
  )
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "secret-tool",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = standardInput is not null,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };
    foreach (var argument in arguments)
    {
      process.StartInfo.ArgumentList.Add(
        argument
      );
    }

    try
    {
      if (!process.Start())
      {
        throw Unavailable();
      }

      var outputTask = process.StandardOutput.ReadToEndAsync(
        cancellationToken
      );
      var errorTask = process.StandardError.ReadToEndAsync(
        cancellationToken
      );
      if (standardInput is not null)
      {
        await process.StandardInput.WriteAsync(
          standardInput.AsMemory(),
          cancellationToken
        );
        process.StandardInput.Close();
      }

      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken
      );
      timeout.CancelAfter(
        TimeSpan.FromSeconds(
          10
        )
      );
      try
      {
        await process.WaitForExitAsync(
          timeout.Token
        );
      }
      catch
      {
        TryStop(
          process
        );
        throw;
      }

      return new SecretCommandResult(
        process.ExitCode,
        await outputTask,
        await errorTask
      );
    }
    catch (Win32Exception exception)
    {
      throw new SecretStorageException(
        "secret-service-unavailable",
        "Linux Secret Service tooling is unavailable. Install libsecret-tools and ensure a user keyring is running.",
        true,
        exception
      );
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
      throw new SecretStorageException(
        "secret-service-timeout",
        "Linux Secret Service did not respond in time.",
        true,
        exception
      );
    }
    catch (Exception exception) when (
      exception is IOException
        or InvalidOperationException
    )
    {
      throw new SecretStorageException(
        "secret-service-failed",
        "Linux Secret Service could not process the API key.",
        true,
        exception
      );
    }
  }

  private static SecretStorageException Unavailable()
  {
    return new SecretStorageException(
      "secret-service-unavailable",
      "Linux Secret Service tooling is unavailable. Install libsecret-tools and ensure a user keyring is running.",
      true
    );
  }

  private static SecretStorageException Failure(
    string code,
    string message,
    string diagnostic,
    bool retryable
  )
  {
    var detail = string.IsNullOrWhiteSpace(diagnostic)
      ? message
      : $"{message} {diagnostic.Trim()}";
    return new SecretStorageException(
      code,
      detail,
      retryable
    );
  }

  private static bool IsValidReference(
    string? reference
  )
  {
    return reference is
    {
      Length: 39
    } && reference.StartsWith(
      "secret-",
      StringComparison.Ordinal
    ) && reference[7..].All(
      char.IsAsciiHexDigit
    );
  }

  private static void TryStop(
    Process process
  )
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(
          entireProcessTree: true
        );
      }
    }
    catch (InvalidOperationException)
    {
    }
  }

  private sealed record SecretCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError
  );
}
