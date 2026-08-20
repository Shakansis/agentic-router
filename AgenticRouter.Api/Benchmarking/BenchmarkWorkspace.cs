using System.Security.Cryptography;

namespace AgenticRouter.Api.Benchmarking;

public sealed record BenchmarkWorkspaceEntry(
  string RelativePath,
  string Kind,
  string ContentHash
);

public sealed record BenchmarkWorkspaceSnapshot(
  IReadOnlyDictionary<string, BenchmarkWorkspaceEntry> Entries
);

public sealed record BenchmarkWorkspace(
  string Id,
  string RunDirectory,
  string WorkspacePath,
  string RootDirectory
);

public interface IBenchmarkWorkspaceFactory
{
  Task<BenchmarkWorkspace> CreateAsync(
    string runId,
    CancellationToken cancellationToken
  );

  Task<BenchmarkWorkspaceSnapshot> CaptureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  );

  Task<bool> CleanupAsync(
    BenchmarkWorkspace workspace,
    CancellationToken cancellationToken
  );
}

public sealed record BenchmarkWorkspaceOptions(
  string RootDirectory,
  string ApplicationContentRoot
);

public sealed class BenchmarkWorkspaceFactory : IBenchmarkWorkspaceFactory
{
  private const int MaximumEntries = 2_000;
  private const long MaximumSnapshotBytes = 64L * 1024 * 1024;
  private readonly string _rootDirectory;

  public BenchmarkWorkspaceFactory(
    BenchmarkWorkspaceOptions options
  )
  {
    _rootDirectory = Path.GetFullPath(options.RootDirectory);
    var contentRoot = Path.GetFullPath(options.ApplicationContentRoot);
    if (ContainsPath(contentRoot, _rootDirectory))
    {
      throw new InvalidOperationException(
        "The benchmark-runs root must be outside the Agentic Router source/content root."
      );
    }
  }

  public Task<BenchmarkWorkspace> CreateAsync(
    string runId,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (
      string.IsNullOrWhiteSpace(runId)
      || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
      || runId.Contains(Path.DirectorySeparatorChar)
      || runId.Contains(Path.AltDirectorySeparatorChar)
    )
    {
      throw new InvalidOperationException("The benchmark run id is invalid.");
    }

    Directory.CreateDirectory(_rootDirectory);
    EnsureNoReparsePointAncestors(_rootDirectory);
    var runDirectory = Path.GetFullPath(Path.Combine(_rootDirectory, runId));
    if (!IsDirectChild(_rootDirectory, runDirectory) || Directory.Exists(runDirectory))
    {
      throw new InvalidOperationException("The benchmark run directory is unsafe or already exists.");
    }
    var workspacePath = Path.Combine(runDirectory, "workspace");
    Directory.CreateDirectory(workspacePath);
    return Task.FromResult(
      new BenchmarkWorkspace(
        runId,
        runDirectory,
        workspacePath,
        _rootDirectory
      )
    );
  }

  public async Task<BenchmarkWorkspaceSnapshot> CaptureAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    var root = Path.GetFullPath(workspacePath);
    if (!Directory.Exists(root))
    {
      throw new DirectoryNotFoundException("The benchmark workspace no longer exists.");
    }
    if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
    {
      throw new IOException("The benchmark workspace root became a reparse point.");
    }

    var comparer = PathComparer;
    var entries = new Dictionary<string, BenchmarkWorkspaceEntry>(comparer);
    var pending = new Stack<string>();
    pending.Push(root);
    long totalBytes = 0;

    while (pending.Count > 0)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var directory = pending.Pop();
      foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.Count >= MaximumEntries)
        {
          throw new IOException($"The benchmark workspace exceeded {MaximumEntries} entries.");
        }

        var fullPath = Path.GetFullPath(entry.FullName);
        if (!ContainsPath(root, fullPath))
        {
          throw new IOException("A benchmark workspace entry escaped the owned root.");
        }
        var relative = NormalizeRelative(Path.GetRelativePath(root, fullPath));
        var reparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
        if (reparsePoint)
        {
          entries.Add(
            relative,
            new BenchmarkWorkspaceEntry(relative, "reparse-point", string.Empty)
          );
          continue;
        }
        if (entry is DirectoryInfo)
        {
          pending.Push(fullPath);
          continue;
        }

        var file = (FileInfo)entry;
        totalBytes += file.Length;
        if (totalBytes > MaximumSnapshotBytes)
        {
          throw new IOException(
            $"The benchmark workspace exceeded {MaximumSnapshotBytes} snapshot bytes."
          );
        }
        await using var stream = new FileStream(
          fullPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          64 * 1024,
          FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var hash = Convert.ToHexString(
          await SHA256.HashDataAsync(stream, cancellationToken)
        ).ToLowerInvariant();
        entries.Add(
          relative,
          new BenchmarkWorkspaceEntry(relative, "file", hash)
        );
      }
    }

    return new BenchmarkWorkspaceSnapshot(entries);
  }

  public Task<bool> CleanupAsync(
    BenchmarkWorkspace workspace,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    var root = Path.GetFullPath(workspace.RootDirectory);
    var runDirectory = Path.GetFullPath(workspace.RunDirectory);
    if (!IsDirectChild(root, runDirectory))
    {
      throw new InvalidOperationException("Refusing to clean an unowned benchmark path.");
    }
    if (!Directory.Exists(runDirectory))
    {
      return Task.FromResult(true);
    }
    EnsureNoReparsePointAncestors(root);
    if (new DirectoryInfo(runDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
    {
      throw new InvalidOperationException("Refusing to clean a reparse-point run root.");
    }

    DeleteOwnedTree(runDirectory, root);
    return Task.FromResult(!Directory.Exists(runDirectory));
  }

  public static StringComparer PathComparer => OperatingSystem.IsWindows()
    ? StringComparer.OrdinalIgnoreCase
    : StringComparer.Ordinal;

  public static string NormalizeRelative(string path)
  {
    return path.Replace('\\', '/');
  }

  private static void DeleteOwnedTree(
    string directory,
    string root
  )
  {
    var fullDirectory = Path.GetFullPath(directory);
    if (!ContainsPath(root, fullDirectory))
    {
      throw new InvalidOperationException("Refusing to clean outside benchmark-runs.");
    }

    foreach (var entry in new DirectoryInfo(fullDirectory).EnumerateFileSystemInfos())
    {
      var fullPath = Path.GetFullPath(entry.FullName);
      if (!ContainsPath(root, fullPath))
      {
        throw new InvalidOperationException("Refusing to clean an escaped benchmark entry.");
      }
      if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
      {
        DeleteOwnedTree(fullPath, root);
        continue;
      }
      entry.Attributes = FileAttributes.Normal;
      if (entry is DirectoryInfo)
      {
        Directory.Delete(fullPath, false);
      }
      else
      {
        File.Delete(fullPath);
      }
    }
    Directory.Delete(fullDirectory, false);
  }

  private static bool IsDirectChild(
    string parent,
    string child
  )
  {
    var childParent = Directory.GetParent(child)?.FullName;
    return childParent is not null
      && string.Equals(
        Path.TrimEndingDirectorySeparator(parent),
        Path.TrimEndingDirectorySeparator(childParent),
        PathComparison
      );
  }

  private static void EnsureNoReparsePointAncestors(
    string path
  )
  {
    DirectoryInfo? current = new DirectoryInfo(path);
    while (current is not null)
    {
      if (
        current.Exists
        && current.Attributes.HasFlag(FileAttributes.ReparsePoint)
      )
      {
        throw new InvalidOperationException(
          "The benchmark-runs path must not traverse a reparse point."
        );
      }
      current = current.Parent;
    }
  }

  private static bool ContainsPath(
    string root,
    string candidate
  )
  {
    var relative = Path.GetRelativePath(root, candidate);
    return !Path.IsPathRooted(relative)
      && !string.Equals(relative, "..", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
  }

  private static StringComparison PathComparison => OperatingSystem.IsWindows()
    ? StringComparison.OrdinalIgnoreCase
    : StringComparison.Ordinal;
}
