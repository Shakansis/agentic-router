using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AgenticRouter.Api.Providers.Cloud;

public interface IProtectedSecretStore
{
  Task<string> StoreAsync(
    string providerId,
    string secret,
    CancellationToken cancellationToken
  );

  Task<string?> GetAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  );

  Task<bool> ExistsAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  );

  Task DeleteAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  );
}

public sealed class DpapiProtectedSecretStore : IProtectedSecretStore
{
  private readonly string _directory;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public DpapiProtectedSecretStore(
    string dataDirectory
  )
  {
    _directory = Path.Combine(
      dataDirectory,
      "secrets"
    );
  }

  public async Task<string> StoreAsync(
    string providerId,
    string secret,
    CancellationToken cancellationToken
  )
  {
    if (
      !OperatingSystem.IsWindows()
      || string.IsNullOrWhiteSpace(
        secret
      )
      || secret.Length > 16_384
    )
    {
      throw new SecretStorageException(
        "secret-invalid",
        "The API key could not be protected.",
        false
      );
    }

    var reference = $"secret-{Guid.NewGuid():N}";
    var plaintext = Encoding.UTF8.GetBytes(
      secret
    );
    byte[] protectedBytes;

    try
    {
      protectedBytes = WindowsDataProtection.Protect(
        plaintext,
        Entropy(
          providerId
        )
      );
    }
    catch (CryptographicException exception)
    {
      throw new SecretStorageException(
        "secret-protection-failed",
        "Windows could not protect the API key for the current user.",
        true,
        exception
      );
    }
    finally
    {
      CryptographicOperations.ZeroMemory(
        plaintext
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      Directory.CreateDirectory(
        _directory
      );
      var path = PathFor(
        reference
      );
      var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

      try
      {
        await using (
          var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4_096,
            FileOptions.Asynchronous | FileOptions.WriteThrough
          )
        )
        {
          await stream.WriteAsync(
            protectedBytes,
            cancellationToken
          );
          await stream.FlushAsync(
            cancellationToken
          );
        }

        File.Move(
          temporaryPath,
          path
        );
      }
      finally
      {
        if (File.Exists(
          temporaryPath
        ))
        {
          File.Delete(
            temporaryPath
          );
        }
      }

      return reference;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new SecretStorageException(
        "secret-storage-failed",
        "The protected API key could not be stored.",
        true,
        exception
      );
    }
    finally
    {
      CryptographicOperations.ZeroMemory(
        protectedBytes
      );
      _gate.Release();
    }
  }

  public async Task<string?> GetAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  )
  {
    if (!IsValidReference(
      reference
    ))
    {
      return null;
    }

    byte[] protectedBytes;

    try
    {
      protectedBytes = await File.ReadAllBytesAsync(
        PathFor(
          reference!
        ),
        cancellationToken
      );
    }
    catch (FileNotFoundException)
    {
      return null;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new SecretStorageException(
        "secret-read-failed",
        "The protected API key could not be read.",
        true,
        exception
      );
    }

    byte[] plaintext;

    try
    {
      plaintext = WindowsDataProtection.Unprotect(
        protectedBytes,
        Entropy(
          providerId
        )
      );
    }
    catch (CryptographicException exception)
    {
      throw new SecretStorageException(
        "secret-unprotection-failed",
        "Windows could not unlock the API key for the current user.",
        false,
        exception
      );
    }
    finally
    {
      CryptographicOperations.ZeroMemory(
        protectedBytes
      );
    }

    try
    {
      return Encoding.UTF8.GetString(
        plaintext
      );
    }
    finally
    {
      CryptographicOperations.ZeroMemory(
        plaintext
      );
    }
  }

  public Task<bool> ExistsAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();

    return Task.FromResult(
      IsValidReference(
        reference
      ) && File.Exists(
        PathFor(
          reference!
        )
      )
    );
  }

  public async Task DeleteAsync(
    string providerId,
    string? reference,
    CancellationToken cancellationToken
  )
  {
    if (!IsValidReference(
      reference
    ))
    {
      return;
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      File.Delete(
        PathFor(
          reference!
        )
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private string PathFor(
    string reference
  )
  {
    return Path.Combine(
      _directory,
      $"{reference}.bin"
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
      character => char.IsAsciiHexDigit(
        character
      )
    );
  }

  private static byte[] Entropy(
    string providerId
  )
  {
    return Encoding.UTF8.GetBytes(
      $"AgenticRouter:{providerId}:v1"
    );
  }
}

public sealed class SecretStorageException : Exception
{
  public SecretStorageException(
    string code,
    string message,
    bool retryable,
    Exception? innerException = null
  )
    : base(
      message,
      innerException
    )
  {
    Code = code;
    Retryable = retryable;
  }

  public string Code { get; }

  public bool Retryable { get; }
}

internal static partial class WindowsDataProtection
{
  private const int CryptProtectUiForbidden = 0x1;

  public static byte[] Protect(
    byte[] plaintext,
    byte[] entropy
  )
  {
    return Transform(
      plaintext,
      entropy,
      true
    );
  }

  public static byte[] Unprotect(
    byte[] protectedBytes,
    byte[] entropy
  )
  {
    return Transform(
      protectedBytes,
      entropy,
      false
    );
  }

  private static byte[] Transform(
    byte[] input,
    byte[] entropy,
    bool protect
  )
  {
    var inputBlob = Allocate(
      input
    );
    var entropyBlob = Allocate(
      entropy
    );

    try
    {
      var succeeded = protect
        ? CryptProtectData(
          ref inputBlob,
          null,
          ref entropyBlob,
          IntPtr.Zero,
          IntPtr.Zero,
          CryptProtectUiForbidden,
          out var outputBlob
        )
        : CryptUnprotectData(
          ref inputBlob,
          IntPtr.Zero,
          ref entropyBlob,
          IntPtr.Zero,
          IntPtr.Zero,
          CryptProtectUiForbidden,
          out outputBlob
        );

      if (!succeeded)
      {
        throw new Win32Exception(
          Marshal.GetLastWin32Error()
        );
      }

      try
      {
        var output = new byte[outputBlob.Size];
        Marshal.Copy(
          outputBlob.Data,
          output,
          0,
          output.Length
        );
        return output;
      }
      finally
      {
        if (outputBlob.Data != IntPtr.Zero)
        {
          LocalFree(
            outputBlob.Data
          );
        }
      }
    }
    finally
    {
      Free(
        inputBlob
      );
      Free(
        entropyBlob
      );
    }
  }

  private static DataBlob Allocate(
    byte[] value
  )
  {
    var pointer = Marshal.AllocHGlobal(
      value.Length
    );
    Marshal.Copy(
      value,
      0,
      pointer,
      value.Length
    );

    return new DataBlob(
      value.Length,
      pointer
    );
  }

  private static void Free(
    DataBlob blob
  )
  {
    if (blob.Data == IntPtr.Zero)
    {
      return;
    }

    for (var index = 0; index < blob.Size; index++)
    {
      Marshal.WriteByte(
        blob.Data,
        index,
        0
      );
    }

    Marshal.FreeHGlobal(
      blob.Data
    );
  }

  [LibraryImport(
    "Crypt32.dll",
    EntryPoint = "CryptProtectData",
    SetLastError = true,
    StringMarshalling = StringMarshalling.Utf16
  )]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool CryptProtectData(
    ref DataBlob dataIn,
    string? description,
    ref DataBlob optionalEntropy,
    IntPtr reserved,
    IntPtr promptStructure,
    int flags,
    out DataBlob dataOut
  );

  [LibraryImport(
    "Crypt32.dll",
    EntryPoint = "CryptUnprotectData",
    SetLastError = true
  )]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool CryptUnprotectData(
    ref DataBlob dataIn,
    IntPtr description,
    ref DataBlob optionalEntropy,
    IntPtr reserved,
    IntPtr promptStructure,
    int flags,
    out DataBlob dataOut
  );

  [LibraryImport(
    "Kernel32.dll"
  )]
  private static partial IntPtr LocalFree(
    IntPtr memory
  );

  [StructLayout(
    LayoutKind.Sequential
  )]
  private readonly record struct DataBlob(
    int Size,
    IntPtr Data
  );
}
