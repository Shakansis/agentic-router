using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Sessions;

// Owns immutable bytes, never project files. Every consumer receives a new stream/copy.
public sealed class HistoryContentStore(IWorkspaceProfileStore profiles)
{
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.Ordinal);
  public string Root { get; } = Path.Combine(profiles.DataDirectory, "history-content");
  public string HostDataDirectory { get; } = Path.GetFullPath(profiles.DataDirectory);
  public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  public void InvalidateSize(string space) => _sizes.TryRemove(space, out _);
  public long SavedBytes(string workspaceId)
  {
    var space = Namespace(workspaceId);
    return _sizes.GetOrAdd(space, directory => Directory.Exists(directory)
      ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0);
  }

  public static string RequireId(string id)
  {
    if (string.IsNullOrWhiteSpace(id) || id.Length > 128
      || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
      throw new WorkspaceProfileException("history-id-invalid", "history-files", "Invalid history identifier.", false);
    return id;
  }

  public string Namespace(string workspace, string? window = null) => window is null
    ? Path.Combine(Root, "saved", RequireId(workspace))
    : Path.Combine(Root, "temporary", RequireId(window), RequireId(workspace));

  public async Task<string> PutAsync(string space, Stream source, CancellationToken token,
    Action<long>? advanced = null, Func<Task>? progress = null)
  {
    await _gate.WaitAsync(token);
    try
    {
      Directory.CreateDirectory(Path.Combine(space, "blobs"));
      var temporary = Path.Combine(space, "blobs", $".{Guid.NewGuid():N}.tmp");
      try
      {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var startPosition = source.CanSeek ? source.Position : 0;
        var buffer = new byte[131_072];
        FileStream? staging = source.CanSeek ? null : File.Create(temporary);
        try
        {
          int read;
          while ((read = await source.ReadAsync(buffer, token)) > 0)
          {
            hash.AppendData(buffer, 0, read);
            if (staging is not null) await staging.WriteAsync(buffer.AsMemory(0, read), token);
            advanced?.Invoke(read);
            if (progress is not null && clock.ElapsedMilliseconds >= 100)
            { await progress(); clock.Restart(); }
          }
        }
        finally { if (staging is not null) await staging.DisposeAsync(); }
        var id = Convert.ToHexStringLower(hash.GetHashAndReset());
        var destination = BlobPath(space, id);
        if (File.Exists(destination) || File.Exists(destination + ".gz")) return id;
        if (source.CanSeek)
        {
          source.Position = startPosition;
          await using var output = File.Create(temporary);
          int read;
          while ((read = await source.ReadAsync(buffer, token)) > 0)
          {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            if (progress is not null && clock.ElapsedMilliseconds >= 100)
            { await progress(); clock.Restart(); }
          }
          if (Convert.ToHexStringLower(hash.GetHashAndReset()) != id)
            throw new InvalidDataException("A file changed while storing its snapshot.");
        }
        File.Move(temporary, destination);
        InvalidateSize(space);
        return id;
      }
      finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    finally { _gate.Release(); }
  }

  public static string BlobPath(string space, string id)
  {
    if (id.Length != 64 || id.Any(c => !char.IsAsciiHexDigit(c)))
      throw new InvalidDataException("Invalid history content digest.");
    return Path.Combine(space, "blobs", id.ToLowerInvariant());
  }

  public async Task CopyAsync(string space, string id, Stream destination, CancellationToken token, long? maximumBytes = null)
  {
    await _gate.WaitAsync(token);
    try
    {
      var path = BlobPath(space, id);
      await using var source = File.OpenRead(File.Exists(path) ? path : path + ".gz");
      await using Stream input = File.Exists(path) ? source : new GZipStream(source, CompressionMode.Decompress);
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[131_072];
      long bytes = 0;
      int read;
      while ((read = await input.ReadAsync(buffer, token)) > 0)
      {
        hash.AppendData(buffer, 0, read);
        bytes += read;
        if (maximumBytes is { } maximum && bytes > maximum)
          throw new InvalidDataException("Stored attachment exceeds its permitted size.");
        await destination.WriteAsync(buffer.AsMemory(0, read), token);
      }
      if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), id, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("History content failed integrity verification.");
    }
    finally { _gate.Release(); }
  }

  public async Task<bool> CompressAsync(string path, CancellationToken token, Action<long, string>? advanced = null,
    Action<HostFileTiming>? completed = null)
  {
    await _gate.WaitAsync(token);
    try
    {
      if (!File.Exists(path)) return false;
      var temporary = path + $".{Guid.NewGuid():N}.tmp";
      try
      {
        byte[] originalHash;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using (var input = File.OpenRead(path))
        {
          originalHash = await SHA256.HashDataAsync(input, token);
          advanced?.Invoke(input.Length, "Checking data integrity");
          completed?.Invoke(new("Checking data integrity", clock.ElapsedMilliseconds, input.Length));
          clock.Restart();
          input.Position = 0;
          await using var output = File.Create(temporary);
          await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
          await input.CopyToAsync(gzip, token);
          advanced?.Invoke(input.Length, "Compacting historical data");
        }
        completed?.Invoke(new("Compacting historical data", clock.ElapsedMilliseconds, new FileInfo(path).Length));
        clock.Restart();
        await using (var input = File.OpenRead(temporary))
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
          var restoredHash = await SHA256.HashDataAsync(gzip, token);
          if (!originalHash.AsSpan().SequenceEqual(restoredHash))
            throw new InvalidDataException("Compressed history failed integrity verification.");
        }
        advanced?.Invoke(new FileInfo(path).Length, "Verifying compressed data");
        completed?.Invoke(new("Verifying compressed data", clock.ElapsedMilliseconds, new FileInfo(path).Length));
        if (new FileInfo(temporary).Length >= new FileInfo(path).Length) return false;
        File.Move(temporary, path + ".gz", true);
        try { File.Delete(path); }
        catch { File.Delete(path + ".gz"); throw; }
        return true;
      }
      finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    finally { _gate.Release(); }
  }

  public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var temporary = path + $".{Guid.NewGuid():N}.tmp";
    try
    {
      await using (var output = File.Create(temporary))
        await JsonSerializer.SerializeAsync(output, value, JsonOptions, token);
      File.Move(temporary, path, true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }

  public async Task CloneAsync(string sourceSpace, string destinationSpace, string id, CancellationToken token)
  {
    var destination = BlobPath(destinationSpace, id);
    if (File.Exists(destination) || File.Exists(destination + ".gz")) return;
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    var temporary = destination + $".{Guid.NewGuid():N}.tmp";
    try
    {
      await using (var output = File.Create(temporary)) await CopyAsync(sourceSpace, id, output, token);
      File.Move(temporary, destination, true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }
}
