using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;

namespace AgenticRouter.Api.Execution;

public sealed class HarnessWorkspaceObserver
{
  private const int MaximumFiles = 5_000;
  private readonly string _root;
  private readonly int _maximumRollbackBytesPerFile;
  private readonly int _maximumRollbackBytesPerSession;
  private readonly Dictionary<string, FileSnapshot> _files;
  private readonly Dictionary<string, string> _protectedGit;

  private HarnessWorkspaceObserver(
    string root,
    int maximumRollbackBytesPerFile,
    int maximumRollbackBytesPerSession,
    Dictionary<string, FileSnapshot> files,
    Dictionary<string, string> protectedGit
  )
  {
    _root = root;
    _maximumRollbackBytesPerFile = maximumRollbackBytesPerFile;
    _maximumRollbackBytesPerSession = maximumRollbackBytesPerSession;
    _files = files;
    _protectedGit = protectedGit;
  }

  public static async Task<HarnessWorkspaceObserver> CaptureAsync(
    string workspacePath,
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
      limits.MaxRollbackBytesPerFile,
      limits.MaxRollbackBytesPerSession,
      files,
      protectedGit
    );
  }

  public async Task<IReadOnlyList<ExecutionFileChange>> ObserveAsync(
    IReadOnlySet<string> approvedDeletionPaths,
    CancellationToken cancellationToken
  )
  {
    var currentGit = await CaptureProtectedGitAsync(_root, cancellationToken);
    if (!Equivalent(_protectedGit, currentGit))
    {
      throw new HarnessException(
        "codex-git-boundary-rejected",
        "Codex changed protected Git state.",
        "A protected .git control path changed during the Codex turn.",
        false
      );
    }

    var current = await CaptureFilesAsync(
      _root,
      false,
      _maximumRollbackBytesPerFile,
      _maximumRollbackBytesPerSession,
      cancellationToken
    );
    var paths = _files.Keys.Concat(current.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
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
        && !approvedDeletionPaths.Contains(relativePath)
      )
      {
        await RestoreDeletedFileAsync(relativePath, before, cancellationToken);
        throw new HarnessException(
          "codex-delete-approval-required",
          "Codex attempted a deletion that requires explicit Host approval.",
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
        undoAvailable ? null : "The original file exceeded the bounded Codex-harness rollback budget or was unavailable.",
        undoAvailable ? originalBytes : 0,
        OriginalBinaryBase64: before?.Text is null && before?.Bytes is not null
          ? Convert.ToBase64String(before.Bytes)
          : null
      ));
    }

    return changes;
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
        "deleted" => "delete_files",
        _ => "write_file"
      };
      session.RecordAction(
        new ValidatedLocalAction(
          Guid.NewGuid().ToString("N"),
          tool,
          document.RootElement.Clone(),
          Path.Combine(session.WorkspacePath, change.RelativePath),
          null,
          $"Host-observed Codex {change.Operation}: {change.RelativePath}",
          null,
          false,
          false
        ),
        "completed",
        "Observed and hashed by Agentic Router after the Codex turn."
      );
    }
  }

  private async Task RestoreDeletedFileAsync(
    string relativePath,
    FileSnapshot snapshot,
    CancellationToken cancellationToken
  )
  {
    if (snapshot.Bytes is null)
    {
      throw new HarnessException(
        "codex-delete-rollback-unavailable",
        "Codex deleted a file without approval and the Host could not restore it.",
        $"'{relativePath}' exceeded the bounded rollback snapshot.",
        false
      );
    }
    var target = Path.GetFullPath(Path.Combine(_root, relativePath));
    EnsureConfined(target, _root);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    await File.WriteAllBytesAsync(target, snapshot.Bytes, cancellationToken);
  }

  private static async Task<Dictionary<string, FileSnapshot>> CaptureFilesAsync(
    string root,
    bool includeGit,
    int maximumSnapshotBytes,
    int maximumTotalSnapshotBytes,
    CancellationToken cancellationToken
  )
  {
    var result = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
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
            "Codex (Experimental) cannot use a workspace containing directory reparse points.",
            info.FullName,
            false
          );
        }
        if (!includeGit && string.Equals(info.Name, ".git", StringComparison.OrdinalIgnoreCase))
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
            "The trusted workspace is too large for bounded Codex effect observation.",
            $"More than {MaximumFiles} files were found.",
            true
          );
        }
        var info = new FileInfo(file);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
          throw new HarnessException("codex-workspace-reparse-point", "Codex (Experimental) cannot use file reparse points.", info.FullName, false);
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
        result[relative] = new FileSnapshot(
          hash,
          info.Length,
          bytes,
          bytes is not null && IsText(bytes) ? Encoding.UTF8.GetString(bytes) : null
        );
      }
    }
    return result;
  }

  private static async Task<Dictionary<string, string>> CaptureProtectedGitAsync(
    string root,
    CancellationToken cancellationToken
  )
  {
    var git = Path.Combine(root, ".git");
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        "The protected Git state is too large for bounded Codex observation.",
        $"More than {MaximumFiles} protected Git files were found.",
        true
      );
    }
    foreach (var path in candidates.Order(StringComparer.OrdinalIgnoreCase))
    {
      cancellationToken.ThrowIfCancellationRequested();
      EnsureNotReparsePoint(path);
      var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
      result[relative] = await HashFileAsync(path, cancellationToken);
    }
    return result;
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
        "Codex (Experimental) cannot observe protected state through reparse points.",
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
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
      throw new HarnessException("codex-workspace-boundary", "A Codex workspace path escaped the trusted root.", path, false);
    }
  }

  private sealed record FileSnapshot(
    string Hash,
    long Length,
    byte[]? Bytes,
    string? Text
  );
}
