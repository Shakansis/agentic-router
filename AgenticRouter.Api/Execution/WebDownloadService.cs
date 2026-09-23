using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AgenticRouter.Api.Execution;

public sealed record WebDownloadReceipt(long Bytes, string Sha256);

public interface IWebDownloadService
{
  Task<WebDownloadReceipt> DownloadAsync(
    Uri uri,
    string destination,
    long maximumBytes,
    CancellationToken cancellationToken,
    string? expectedExistingHash = null
  );
}

public sealed class WebDownloadService : IWebDownloadService, IDisposable
{
  private const int MaximumRedirects = 5;
  private readonly HttpClient _client;

  public WebDownloadService()
  {
    var handler = new SocketsHttpHandler
    {
      AllowAutoRedirect = false,
      UseProxy = false,
      ConnectTimeout = TimeSpan.FromSeconds(30),
      ConnectCallback = ConnectPublicIpv4Async
    };
    _client = new HttpClient(handler)
    {
      Timeout = Timeout.InfiniteTimeSpan
    };
  }

  public async Task<WebDownloadReceipt> DownloadAsync(
    Uri uri,
    string destination,
    long maximumBytes,
    CancellationToken cancellationToken,
    string? expectedExistingHash = null
  )
  {
    if (maximumBytes <= 0)
    {
      throw new LocalActionException("download-limit", "The approved download size limit was reached.");
    }

    var current = uri;
    HttpResponseMessage? response = null;
    try
    {
      for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
      {
        ValidatePublicHttpsUrl(current);
        using var request = new HttpRequestMessage(HttpMethod.Get, current);
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(90));
        response = await _client.SendAsync(
          request,
          HttpCompletionOption.ResponseHeadersRead,
          headerTimeout.Token
        );
        if ((int)response.StatusCode is >= 300 and < 400)
        {
          if (redirect == MaximumRedirects || response.Headers.Location is null)
          {
            throw new LocalActionException("download-redirect", "The download exceeded the allowed redirect chain or has no destination.");
          }
          current = response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location
            : new Uri(current, response.Headers.Location);
          response.Dispose();
          response = null;
          continue;
        }
        if (!response.IsSuccessStatusCode)
        {
          throw new LocalActionException(
            response.StatusCode is HttpStatusCode.RequestTimeout
              or HttpStatusCode.TooManyRequests
              or >= HttpStatusCode.InternalServerError
              ? "download-http-transient"
              : "download-http",
            $"The download server returned HTTP {(int)response.StatusCode}."
          );
        }
        if (response.Content.Headers.ContentLength is long declared && declared > maximumBytes)
        {
          throw new LocalActionException("download-limit", "The response exceeds the approved download size limit.");
        }
        return await SaveResponseAsync(
          response,
          destination,
          maximumBytes,
          cancellationToken,
          expectedExistingHash
        );
      }
      throw new LocalActionException("download-redirect", "The download exceeded the allowed redirect chain.");
    }
    finally
    {
      response?.Dispose();
    }
  }

  public static void ValidatePublicHttpsUrl(Uri uri)
  {
    if (!uri.IsAbsoluteUri
      || uri.Scheme != Uri.UriSchemeHttps
      || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4)
      || uri.UserInfo.Length != 0
      || uri.Fragment.Length != 0
      || uri.AbsoluteUri.Length > 2048)
    {
      throw new LocalActionException(
        "download-url",
        "Downloads require a public HTTPS URL without credentials or a fragment."
      );
    }
    if (IPAddress.TryParse(uri.Host, out var address) && !IsPublicIpv4(address))
    {
      throw new LocalActionException("download-url", "The download URL cannot target a local or reserved address.");
    }
  }

  private static async ValueTask<Stream> ConnectPublicIpv4Async(
    SocketsHttpConnectionContext context,
    CancellationToken cancellationToken
  )
  {
    IPAddress[] addresses;
    try
    {
      addresses = await Dns.GetHostAddressesAsync(
        context.DnsEndPoint.Host,
        AddressFamily.InterNetwork,
        cancellationToken
      );
    }
    catch (SocketException exception)
    {
      throw new LocalActionException("download-network", "The download host could not be resolved.", exception);
    }
    var publicAddresses = addresses.Where(IsPublicIpv4).ToArray();
    if (publicAddresses.Length == 0)
    {
      throw new LocalActionException("download-network", "The URL did not resolve to a public IPv4 address.");
    }
    SocketException? lastFailure = null;
    foreach (var address in publicAddresses)
    {
      var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
      try
      {
        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
      }
      catch (SocketException exception)
      {
        lastFailure = exception;
        socket.Dispose();
      }
      catch
      {
        socket.Dispose();
        throw;
      }
    }
    throw new LocalActionException("download-network", "Could not connect to a public download address.", lastFailure);
  }

  private static bool IsPublicIpv4(IPAddress address)
  {
    if (address.AddressFamily != AddressFamily.InterNetwork) return false;
    var bytes = address.GetAddressBytes();
    var first = bytes[0];
    var second = bytes[1];
    var third = bytes[2];
    return first is > 0 and < 224
      && first != 10
      && first != 127
      && !(first == 100 && second is >= 64 and <= 127)
      && !(first == 169 && second == 254)
      && !(first == 172 && second is >= 16 and <= 31)
      && !(first == 192 && second == 168)
      && !(first == 192 && second == 0 && third == 0)
      && !(first == 192 && second == 0 && third == 2)
      && !(first == 198 && second is 18 or 19)
      && !(first == 198 && second == 51 && third == 100)
      && !(first == 203 && second == 0 && third == 113);
  }

  private static async Task<WebDownloadReceipt> SaveResponseAsync(
    HttpResponseMessage response,
    string destination,
    long maximumBytes,
    CancellationToken cancellationToken,
    string? expectedExistingHash
  )
  {
    var parent = Path.GetDirectoryName(destination)
      ?? throw new LocalActionException("download-path", "The download destination has no parent directory.");
    var createdParents = new List<string>();
    for (var directory = parent; !Directory.Exists(directory); directory = Path.GetDirectoryName(directory))
    {
      if (directory is null)
      {
        throw new LocalActionException("download-path", "The download destination has no existing root directory.");
      }
      createdParents.Add(directory);
    }
    var temporary = Path.Combine(parent, $".ar-download-{Guid.NewGuid():N}.part");
    var completed = false;
    try
    {
      Directory.CreateDirectory(parent);
      await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
      await using var output = new FileStream(
        temporary,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        65_536,
        FileOptions.Asynchronous | FileOptions.SequentialScan
      );
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[65_536];
      long count = 0;
      while (true)
      {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(TimeSpan.FromSeconds(90));
        var read = await input.ReadAsync(buffer, idle.Token);
        if (read == 0) break;
        count = checked(count + read);
        if (count > maximumBytes)
        {
          throw new LocalActionException("download-limit", "The response exceeded the approved download size limit during transfer.");
        }
        hash.AppendData(buffer.AsSpan(0, read));
        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
      }
      await output.FlushAsync(cancellationToken);
      output.Close();
      var expectedHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
      var actualHash = await HashFileAsync(temporary, cancellationToken);
      if (new FileInfo(temporary).Length != count || actualHash != expectedHash)
      {
        throw new LocalActionException("download-verification", "The temporary file failed byte-count or SHA-256 verification.");
      }
      if (expectedExistingHash is null)
      {
        File.Move(temporary, destination, overwrite: false);
      }
      else
      {
        if (!File.Exists(destination)
          || await HashFileAsync(destination, cancellationToken) != expectedExistingHash)
        {
          throw new LocalActionException(
            "file-conflict",
            "The existing destination changed before replacement; it was preserved."
          );
        }
        File.Replace(temporary, destination, null);
      }
      completed = true;
      return new WebDownloadReceipt(count, expectedHash);
    }
    finally
    {
      if (File.Exists(temporary)) File.Delete(temporary);
      if (!completed)
      {
        foreach (var directory in createdParents)
        {
          if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
          {
            Directory.Delete(directory);
          }
        }
      }
    }
  }

  private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
  {
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
  }

  public void Dispose() => _client.Dispose();
}
