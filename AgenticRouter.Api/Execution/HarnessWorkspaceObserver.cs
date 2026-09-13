using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Platform;
using AgenticRouter.Api.ProjectAwareness;

namespace AgenticRouter.Api.Execution;

public sealed class HarnessWorkspaceObserver
{
  internal const int MaximumFiles = 5_000;
  private readonly string _root;
  private readonly string _harnessId;
  private readonly int _maximumRollbackBytesPerFile;
  private readonly int _maximumRollbackBytesPerSession;
  private readonly Dictionary<string, HarnessFileSnapshot> _files;
  private readonly Dictionary<string, string> _protectedGit;
  private readonly HarnessWorkspaceBaselineCache? _baselineCache;

  internal HarnessWorkspaceObserver(
    string root,
    string harnessId,
    int maximumRollbackBytesPerFile,
    int maximumRollbackBytesPerSession,
    Dictionary<string, HarnessFileSnapshot> files,
    Dictionary<string, string> protectedGit,
    HarnessWorkspaceBaselineCache? baselineCache = null,
    bool baselineReused = false
  )
  {
    _root = root;
    _harnessId = harnessId;
    _maximumRollbackBytesPerFile = maximumRollbackBytesPerFile;
    _maximumRollbackBytesPerSession = maximumRollbackBytesPerSession;
    _files = files;
    _protectedGit = protectedGit;
    _baselineCache = baselineCache;
    BaselineReused = baselineReused;
  }

  public bool BaselineReused { get; }

  public static async Task<HarnessWorkspaceObserver> CaptureAsync(
    string workspacePath,
    string harnessId,
    ExecutionSettings limits,
    CancellationToken cancellationToken
  )
  {
    var root = Path.GetFullPath(workspacePath);
    var files = await CaptureFilesAsync(
      root,
      false,
      limits.MaxRollbackBytesPerFile,
      limits.MaxRollbackBytesPerSession,
      cancellationToken
    );
    var protectedGit = await CaptureProtectedGitAsync(root, cancellationToken);
    return new HarnessWorkspaceObserver(
      root,
      harnessId,
      limits.MaxRollbackBytesPerFile,
      limits.MaxRollbackBytesPerSession,
      files,
      protectedGit
    );
  }

  public async Task<IReadOnlyList<ExecutionFileChange>> ObserveAsync(
    IReadOnlySet<string> approvedDeletionPaths,
    bool policyAuthorizesDeletion,
    CancellationToken cancellationToken
  )
  {
    var currentGit = await CaptureProtectedGitAsync(_root, cancellationToken);
    if (!Equivalent(_protectedGit, currentGit))
    {
      throw new HarnessException(
        $"{_harnessId}-git-boundary-rejected",
        "The selected harness changed protected Git state.",
        "A protected .git control path changed during the external harness turn.",
        false,
        harnessId: _harnessId
      );
    }

    var current = await CaptureFilesAsync(
      _root,
      false,
      _maximumRollbackBytesPerFile,
      _maximumRollbackBytesPerSession,
      cancellationToken
    );
    var paths = _files.Keys.Concat(current.Keys)
      .Distinct(FileSystemPathSemantics.Comparer)
      .Order(FileSystemPathSemantics.Comparer);
    var changes = new List<ExecutionFileChange>();
    var rollbackBytes = 0L;

    foreach (var relativePath in paths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _files.TryGetValue(relativePath, out var before);
      current.TryGetValue(relativePath, out var after);
      if (before is not null && after is not null && string.Equals(before.Hash, after.Hash, StringComparison.Ordinal))
      {
        continue;
      }

      if (
        after is null
        && before is not null
        && !policyAuthorizesDeletion
        && !approvedDeletionPaths.Contains(relativePath)
      )
      {
        await RestoreDeletedFileAsync(relativePath, before, cancellationToken);
        throw new HarnessException(
          "codex-delete-approval-required",
          "The harness attempted a deletion that requires Host approval under the selected policy.",
          $"The unapproved deletion of '{relativePath}' was restored from the bounded Host snapshot.",
          true
        );
      }

      var originalBytes = before?.Bytes?.LongLength ?? 0;
      var undoAvailable = before is null
        || before.Bytes is not null
        && originalBytes <= _maximumRollbackBytesPerFile
        && rollbackBytes + originalBytes <= _maximumRollbackBytesPerSession;
      if (undoAvailable)
      {
        rollbackBytes += originalBytes;
      }
      var operation = before is null ? "created" : after is null ? "deleted" : "modified";
      changes.Add(new ExecutionFileChange(
        relativePath,
        operation,
        before is not null,
        before?.Hash ?? string.Empty,
        after?.Hash ?? string.Empty,
        before?.Text,
        after?.Text ?? string.Empty,
        after?.Length ?? 0,
        DateTimeOffset.UtcNow,
        true,
        undoAvailable,
        undoAvailable ? null : "The original file exceeded the bounded harness rollback budget or was unavailable.",
        undoAvailable ? originalBytes : 0,
        OriginalBinaryBase64: before?.Text is null && before?.Bytes is not null
          ? Convert.ToBase64String(before.Bytes)
          : null
      ));
    }

    if (_baselineCache is not null)
    {
      await _baselineCache.UpdateAsync(
        _root,
        _maximumRollbackBytesPerFile,
        _maximumRollbackBytesPerSession,
        current,
        currentGit,
        cancellationToken
      );
      if (changes.Count > 0)
      {
        _baselineCache.NotifyWorkspaceChanged(_root);
      }
    }
    return changes;
  }

  public async Task VerifyProtectedGitUnchangedAsync(
    CancellationToken cancellationToken
  )
  {
    var currentGit = await CaptureProtectedGitAsync(_root, cancellationToken);
    if (!Equivalent(_protectedGit, currentGit))
    {
      throw new HarnessException(
        $"{_harnessId}-git-boundary-rejected",
        "The selected harness changed protected Git state.",
        "A protected .git control path changed during the external harness turn.",
        false,
        harnessId: _harnessId
      );
    }
  }

  public async Task AcceptHostGitMutationAsync(
    CancellationToken cancellationToken
  )
  {
    var currentGit = await CaptureProtectedGitAsync(_root, cancellationToken);
    _protectedGit.Clear();
    foreach (var pair in currentGit)
    {
      _protectedGit[pair.Key] = pair.Value;
    }
  }

  public static void Record(
    ExecutionSession session,
    IReadOnlyList<ExecutionFileChange> changes
  )
  {
    using var document = JsonDocument.Parse("{}");
    foreach (var change in changes)
    {
      session.RecordFileChange(change);
      var tool = change.Operation switch
      {
        "created" => "create_file",
        "deleted" => "delete_paths",
        _ => "write_file"
      };
      session.RecordAction(
        new ValidatedLocalAction(
          Guid.NewGuid().ToString("N"),
          tool,
          document.RootElement.Clone(),
          Path.Combine(session.WorkspacePath, change.RelativePath),
          null,
          $"Host-observed harness {change.Operation}: {change.RelativePath}",
          null,
          false,
          false
        ),
        "completed",
        "Observed and hashed by Agentic Router after the external harness turn."
      );
    }
  }

  private async Task RestoreDeletedFileAsync(
    string relativePath,
    HarnessFileSnapshot snapshot,
    CancellationToken cancellationToken
  )
  {
    if (snapshot.Bytes is null)
    {
      throw new HarnessException(
        "codex-delete-rollback-unavailable",
        "The selected harness deleted a file without approval and the Host could not restore it.",
        $"'{relativePath}' exceeded the bounded rollback snapshot.",
        false
      );
    }
    var target = Path.GetFullPath(Path.Combine(_root, relativePath));
    EnsureConfined(target, _root);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    await File.WriteAllBytesAsync(target, snapshot.Bytes, cancellationToken);
  }

  internal static async Task<Dictionary<string, HarnessFileSnapshot>> CaptureFilesAsync(
    string root,
    bool includeGit,
    int maximumSnapshotBytes,
    int maximumTotalSnapshotBytes,
    CancellationToken cancellationToken
  )
  {
    var result = new Dictionary<string, HarnessFileSnapshot>(FileSystemPathSemantics.Comparer);
    var capturedBytes = 0L;
    var directories = new Stack<string>();
    directories.Push(root);
    while (directories.Count > 0)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var directory = directories.Pop();
      foreach (var child in Directory.EnumerateDirectories(directory))
      {
        var info = new DirectoryInfo(child);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
          throw new HarnessException(
            "codex-workspace-reparse-point",
            "The selected external harness cannot use a workspace containing directory reparse points.",
            info.FullName,
            false
          );
        }
        if (!includeGit && string.Equals(info.Name, ".git", FileSystemPathSemantics.Comparison))
        {
          continue;
        }
        directories.Push(info.FullName);
      }
      foreach (var file in Directory.EnumerateFiles(directory))
      {
        if (result.Count >= MaximumFiles)
        {
          throw new HarnessException(
            "codex-workspace-too-large",
            "The trusted workspace is too large for bounded external-harness effect observation.",
            $"More than {MaximumFiles} files were found.",
            true
          );
        }
        var info = new FileInfo(file);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
          throw new HarnessException("codex-workspace-reparse-point", "The selected external harness cannot use file reparse points.", info.FullName, false);
        }
        var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
        byte[]? bytes = info.Length <= maximumSnapshotBytes
          && capturedBytes + info.Length <= maximumTotalSnapshotBytes
            ? await File.ReadAllBytesAsync(info.FullName, cancellationToken)
            : null;
        if (bytes is not null)
        {
          if (capturedBytes + bytes.LongLength <= maximumTotalSnapshotBytes)
          {
            capturedBytes += bytes.LongLength;
          }
          else
          {
            bytes = null;
          }
        }
        var hash = bytes is null
          ? await HashFileAsync(info.FullName, cancellationToken)
          : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        result[relative] = new HarnessFileSnapshot(
          hash,
          info.Length,
          bytes,
          bytes is not null && IsText(bytes) ? Encoding.UTF8.GetString(bytes) : null
        );
      }
    }
    return result;
  }

  internal static async Task<Dictionary<string, string>> CaptureProtectedGitAsync(
    string root,
    CancellationToken cancellationToken
  )
  {
    var git = Path.Combine(root, ".git");
    var result = new Dictionary<string, string>(FileSystemPathSemantics.Comparer);
    if (File.Exists(git))
    {
      EnsureNotReparsePoint(git);
      result[".git"] = await HashFileAsync(git, cancellationToken);
      return result;
    }
    if (!Directory.Exists(git))
    {
      return result;
    }
    EnsureNotReparsePoint(git);
    var candidates = new List<string>();
    foreach (var name in new[] { "HEAD", "config", "index", "packed-refs" })
    {
      var path = Path.Combine(git, name);
      if (File.Exists(path))
      {
        candidates.Add(path);
      }
    }
    foreach (var name in new[] { "refs", "logs" })
    {
      var path = Path.Combine(git, name);
      if (Directory.Exists(path))
      {
        candidates.AddRange(EnumerateFilesWithoutReparsePoints(path));
      }
    }
    if (candidates.Count > MaximumFiles)
    {
      throw new HarnessException(
        "codex-git-state-too-large",
        "The protected Git state is too large for bounded external-harness observation.",
        $"More than {MaximumFiles} protected Git files were found.",
        true
      );
    }
    foreach (var path in candidates.Order(FileSystemPathSemantics.Comparer))
    {
      cancellationToken.ThrowIfCancellationRequested();
      EnsureNotReparsePoint(path);
      var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
      result[relative] = await HashFileAsync(path, cancellationToken);
    }
    return result;
  }

  internal static async Task<HarnessFileSnapshot> CaptureFileAsync(
    string root,
    string path,
    int maximumSnapshotBytes,
    int maximumTotalSnapshotBytes,
    long otherCapturedBytes,
    CancellationToken cancellationToken
  )
  {
    var info = new FileInfo(path);
    EnsureConfined(info.FullName, root);
    var parent = info.Directory;
    while (
      parent is not null
      && !string.Equals(parent.FullName, root, FileSystemPathSemantics.Comparison)
    )
    {
      EnsureNotReparsePoint(parent.FullName);
      parent = parent.Parent;
    }
    if (parent is null)
    {
      throw new HarnessException(
        "codex-workspace-boundary",
        "An external-harness workspace path escaped the trusted root.",
        info.FullName,
        false
      );
    }
    EnsureNotReparsePoint(info.FullName);
    byte[]? bytes = info.Length <= maximumSnapshotBytes
      && otherCapturedBytes + info.Length <= maximumTotalSnapshotBytes
        ? await File.ReadAllBytesAsync(info.FullName, cancellationToken)
        : null;
    var hash = bytes is null
      ? await HashFileAsync(info.FullName, cancellationToken)
      : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    return new HarnessFileSnapshot(
      hash,
      info.Length,
      bytes,
      bytes is not null && IsText(bytes) ? Encoding.UTF8.GetString(bytes) : null
    );
  }

  private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
  {
    var directories = new Stack<string>();
    directories.Push(root);
    while (directories.Count > 0)
    {
      var directory = directories.Pop();
      EnsureNotReparsePoint(directory);
      foreach (var child in Directory.EnumerateDirectories(directory))
      {
        EnsureNotReparsePoint(child);
        directories.Push(child);
      }
      foreach (var file in Directory.EnumerateFiles(directory))
      {
        EnsureNotReparsePoint(file);
        yield return file;
      }
    }
  }

  private static void EnsureNotReparsePoint(string path)
  {
    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
    {
      throw new HarnessException(
        "codex-workspace-reparse-point",
        "The selected external harness cannot observe protected state through reparse points.",
        path,
        false
      );
    }
  }

  private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
  {
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65_536, true);
    var hash = await SHA256.HashDataAsync(stream, cancellationToken);
    return Convert.ToHexString(hash).ToLowerInvariant();
  }

  private static bool Equivalent(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
  {
    return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));
  }

  private static bool IsText(byte[] bytes)
  {
    if (bytes.AsSpan().IndexOf((byte)0) >= 0)
    {
      return false;
    }
    try
    {
      _ = new UTF8Encoding(false, true).GetString(bytes);
      return true;
    }
    catch (DecoderFallbackException)
    {
      return false;
    }
  }

  private static void EnsureConfined(string path, string root)
  {
    var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!path.StartsWith(prefix, FileSystemPathSemantics.Comparison))
    {
      throw new HarnessException("codex-workspace-boundary", "An external-harness workspace path escaped the trusted root.", path, false);
    }
  }

}

internal sealed record HarnessFileSnapshot(
  string Hash,
  long Length,
  byte[]? Bytes,
  string? Text
);

public sealed class HarnessWorkspaceBaselineCache : IDisposable
{
  private readonly ConcurrentDictionary<string, Lazy<Entry>> _entries = new(
    FileSystemPathSemantics.Comparer
  );
  private readonly ILogger<HarnessWorkspaceBaselineCache> _logger;
  private readonly ProjectAwarenessCache _projectAwarenessCache;

  public HarnessWorkspaceBaselineCache(
    ILogger<HarnessWorkspaceBaselineCache> logger,
    ProjectAwarenessCache projectAwarenessCache
  )
  {
    _logger = logger;
    _projectAwarenessCache = projectAwarenessCache;
  }

  public async Task<HarnessWorkspaceObserver> CaptureAsync(
    string workspacePath,
    string harnessId,
    ExecutionSettings limits,
    CancellationToken cancellationToken
  )
  {
    var root = Path.GetFullPath(workspacePath);
    var entry = _entries.GetOrAdd(
      root,
      path => new Lazy<Entry>(
        () => new Entry(path),
        LazyThreadSafetyMode.ExecutionAndPublication
      )
    ).Value;
    await entry.Gate.WaitAsync(cancellationToken);
    try
    {
      var reusable = entry.Files is not null
        && entry.ProtectedGit is not null
        && entry.MaximumRollbackBytesPerFile == limits.MaxRollbackBytesPerFile
        && entry.MaximumRollbackBytesPerSession == limits.MaxRollbackBytesPerSession
        && entry.CapturedGeneration == entry.Generation;
      if (
        !reusable
        && entry.Files is not null
        && entry.ProtectedGit is not null
        && entry.MaximumRollbackBytesPerFile == limits.MaxRollbackBytesPerFile
        && entry.MaximumRollbackBytesPerSession == limits.MaxRollbackBytesPerSession
      )
      {
        reusable = await TryRefreshChangedPathsAsync(
          entry,
          root,
          limits,
          cancellationToken
        );
      }
      if (reusable)
      {
        _logger.LogInformation(
          "Workspace observer baseline reused for {WorkspacePath} at generation {Generation}.",
          root,
          entry.Generation
        );
        return new HarnessWorkspaceObserver(
          root,
          harnessId,
          limits.MaxRollbackBytesPerFile,
          limits.MaxRollbackBytesPerSession,
          Clone(entry.Files!),
          new Dictionary<string, string>(entry.ProtectedGit!, FileSystemPathSemantics.Comparer),
          this,
          true
        );
      }

      var files = await HarnessWorkspaceObserver.CaptureFilesAsync(
        root,
        false,
        limits.MaxRollbackBytesPerFile,
        limits.MaxRollbackBytesPerSession,
        cancellationToken
      );
      var protectedGit = await HarnessWorkspaceObserver.CaptureProtectedGitAsync(
        root,
        cancellationToken
      );
      Store(entry, limits.MaxRollbackBytesPerFile, limits.MaxRollbackBytesPerSession, files, protectedGit);
      _logger.LogInformation(
        "Workspace observer baseline rebuilt for {WorkspacePath} at generation {Generation}.",
        root,
        entry.Generation
      );
      return new HarnessWorkspaceObserver(
        root,
        harnessId,
        limits.MaxRollbackBytesPerFile,
        limits.MaxRollbackBytesPerSession,
        Clone(files),
        new Dictionary<string, string>(protectedGit, FileSystemPathSemantics.Comparer),
        this,
        false
      );
    }
    finally
    {
      entry.Gate.Release();
    }
  }

  internal async Task UpdateAsync(
    string root,
    int maximumRollbackBytesPerFile,
    int maximumRollbackBytesPerSession,
    Dictionary<string, HarnessFileSnapshot> files,
    Dictionary<string, string> protectedGit,
    CancellationToken cancellationToken
  )
  {
    var canonical = Path.GetFullPath(root);
    var entry = _entries.GetOrAdd(
      canonical,
      path => new Lazy<Entry>(
        () => new Entry(path),
        LazyThreadSafetyMode.ExecutionAndPublication
      )
    ).Value;
    await entry.Gate.WaitAsync(cancellationToken);
    try
    {
      Store(
        entry,
        maximumRollbackBytesPerFile,
        maximumRollbackBytesPerSession,
        files,
        protectedGit
      );
    }
    finally
    {
      entry.Gate.Release();
    }
  }

  public void Dispose()
  {
    foreach (var entry in _entries.Values.Where(entry => entry.IsValueCreated))
    {
      entry.Value.Dispose();
    }
    _entries.Clear();
  }

  internal void NotifyWorkspaceChanged(string workspacePath)
  {
    _projectAwarenessCache.Invalidate(workspacePath);
  }

  private static void Store(
    Entry entry,
    int maximumRollbackBytesPerFile,
    int maximumRollbackBytesPerSession,
    Dictionary<string, HarnessFileSnapshot> files,
    Dictionary<string, string> protectedGit
  )
  {
    entry.Files = Clone(files);
    entry.ProtectedGit = new Dictionary<string, string>(
      protectedGit,
      FileSystemPathSemantics.Comparer
    );
    entry.MaximumRollbackBytesPerFile = maximumRollbackBytesPerFile;
    entry.MaximumRollbackBytesPerSession = maximumRollbackBytesPerSession;
    entry.CapturedGeneration = entry.Generation;
    entry.ClearChanges();
  }

  private static async Task<bool> TryRefreshChangedPathsAsync(
    Entry entry,
    string root,
    ExecutionSettings limits,
    CancellationToken cancellationToken
  )
  {
    var changes = entry.TakeChanges();
    if (changes.Invalidated || changes.Paths.Count == 0)
    {
      return false;
    }

    var files = Clone(entry.Files!);
    var protectedGit = new Dictionary<string, string>(
      entry.ProtectedGit!,
      FileSystemPathSemantics.Comparer
    );
    var refreshGit = false;
    foreach (var relativePath in changes.Paths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (
        relativePath.Equals(".git", FileSystemPathSemantics.Comparison)
        || relativePath.StartsWith(".git/", FileSystemPathSemantics.Comparison)
      )
      {
        refreshGit = true;
        continue;
      }

      var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
      var relativeCheck = Path.GetRelativePath(root, fullPath);
      if (
        Path.IsPathRooted(relativeCheck)
        || relativeCheck.Equals("..", StringComparison.Ordinal)
        || relativeCheck.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
      )
      {
        return false;
      }
      if (Directory.Exists(fullPath))
      {
        return false;
      }
      if (!File.Exists(fullPath))
      {
        var prefix = relativePath.TrimEnd('/') + "/";
        if (files.Keys.Any(path => path.StartsWith(prefix, FileSystemPathSemantics.Comparison)))
        {
          return false;
        }
        files.Remove(relativePath);
        continue;
      }

      files.TryGetValue(relativePath, out var prior);
      var otherCapturedBytes = files.Values.Sum(snapshot => snapshot.Bytes?.LongLength ?? 0)
        - (prior?.Bytes?.LongLength ?? 0);
      files[relativePath] = await HarnessWorkspaceObserver.CaptureFileAsync(
        root,
        fullPath,
        limits.MaxRollbackBytesPerFile,
        limits.MaxRollbackBytesPerSession,
        otherCapturedBytes,
        cancellationToken
      );
      if (files.Count > HarnessWorkspaceObserver.MaximumFiles)
      {
        return false;
      }
    }
    if (refreshGit)
    {
      protectedGit = await HarnessWorkspaceObserver.CaptureProtectedGitAsync(
        root,
        cancellationToken
      );
    }

    entry.Files = files;
    entry.ProtectedGit = protectedGit;
    entry.MaximumRollbackBytesPerFile = limits.MaxRollbackBytesPerFile;
    entry.MaximumRollbackBytesPerSession = limits.MaxRollbackBytesPerSession;
    entry.CapturedGeneration = changes.Generation;
    return entry.CapturedGeneration == entry.Generation;
  }

  private static Dictionary<string, HarnessFileSnapshot> Clone(
    IReadOnlyDictionary<string, HarnessFileSnapshot> files
  )
  {
    return files.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      FileSystemPathSemantics.Comparer
    );
  }

  private sealed class Entry : IDisposable
  {
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, byte> _changedPaths = new(
      FileSystemPathSemantics.Comparer
    );
    private long _generation;
    private int _invalidated;
    private readonly string _root;

    public Entry(string root)
    {
      _root = root;
      _watcher = new FileSystemWatcher(root)
      {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName
          | NotifyFilters.DirectoryName
          | NotifyFilters.LastWrite
          | NotifyFilters.Size,
        EnableRaisingEvents = true
      };
      _watcher.Changed += Changed;
      _watcher.Created += Changed;
      _watcher.Deleted += Changed;
      _watcher.Renamed += Renamed;
      _watcher.Error += Error;
    }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public Dictionary<string, HarnessFileSnapshot>? Files { get; set; }

    public Dictionary<string, string>? ProtectedGit { get; set; }

    public int MaximumRollbackBytesPerFile { get; set; }

    public int MaximumRollbackBytesPerSession { get; set; }

    public long CapturedGeneration { get; set; } = -1;

    public long Generation => Interlocked.Read(ref _generation);

    public void Dispose()
    {
      _watcher.Dispose();
      Gate.Dispose();
    }

    public WorkspaceChanges TakeChanges()
    {
      var generation = Generation;
      var paths = _changedPaths.Keys.ToArray();
      foreach (var path in paths)
      {
        _changedPaths.TryRemove(path, out _);
      }
      return new WorkspaceChanges(
        generation,
        Interlocked.Exchange(ref _invalidated, 0) != 0,
        paths
      );
    }

    public void ClearChanges()
    {
      _changedPaths.Clear();
      Interlocked.Exchange(ref _invalidated, 0);
    }

    private void Changed(object sender, FileSystemEventArgs eventArgs)
    {
      Record(eventArgs.FullPath);
      Interlocked.Increment(ref _generation);
    }

    private void Renamed(object sender, RenamedEventArgs eventArgs)
    {
      Record(eventArgs.OldFullPath);
      Record(eventArgs.FullPath);
      Interlocked.Increment(ref _generation);
    }

    private void Error(object sender, ErrorEventArgs eventArgs)
    {
      Interlocked.Exchange(ref _invalidated, 1);
      Interlocked.Increment(ref _generation);
    }

    private void Record(string path)
    {
      var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
      _changedPaths[relative] = 0;
    }
  }

  private sealed record WorkspaceChanges(
    long Generation,
    bool Invalidated,
    IReadOnlyList<string> Paths
  );
}
