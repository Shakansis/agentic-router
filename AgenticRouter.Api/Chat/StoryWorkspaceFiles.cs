using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Chat;

public sealed record StoryFileEntry(string Name, string Path, bool Directory, long? Bytes);
public sealed record StoryFileList(string WorkspaceId, string WorkspaceName, string Directory, IReadOnlyList<StoryFileEntry> Entries);
public sealed record StoryWorkspaceDocument(string WorkspaceId, string Path, string Document, string Version);
public sealed record StoryWorkspaceSave(string WorkspaceId, string Path, string Document, string ExpectedVersion);

public sealed class StoryWorkspaceFiles(IWorkspaceProfileService profiles,
  IWorkspaceExecutionContextAccessor context, ITrustedWorkspaceService workspace)
{
  private const int MaximumBytes = 800_003;
  private static readonly UTF8Encoding Utf8 = new(false, true);

  public Task<StoryFileList> ListAsync(string workspaceId, string? directory, CancellationToken token) =>
    InWorkspaceAsync(workspaceId, async profile =>
    {
      var root = await workspace.ResolvePathAsync(Relative(directory, allowRoot: true), token);
      var entries = new List<StoryFileEntry>();
      foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
      {
        token.ThrowIfCancellationRequested();
        if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)
          || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
        var folder = entry is DirectoryInfo;
        if (!folder && (!Editable(entry.Name) || entry.Attributes.HasFlag(FileAttributes.ReadOnly)
          || ((FileInfo)entry).Length > MaximumBytes)) continue;
        var relative = Path.GetRelativePath(profile.Path, entry.FullName).Replace('\\', '/');
        await workspace.ResolvePathAsync(relative, token);
        entries.Add(new(entry.Name, relative, folder, folder ? null : ((FileInfo)entry).Length));
      }
      return new StoryFileList(profile.Id, profile.Name,
        Path.GetRelativePath(profile.Path, root).Replace('\\', '/'),
        entries.OrderByDescending(entry => entry.Directory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }, token);

  public Task<StoryWorkspaceDocument> OpenAsync(string workspaceId, string path, CancellationToken token) =>
    InWorkspaceAsync(workspaceId, async profile =>
    {
      var fullPath = await ResolveFileAsync(path, token);
      await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
        65536, FileOptions.Asynchronous);
      var bytes = await ReadAsync(stream, token);
      return new StoryWorkspaceDocument(profile.Id, Path.GetRelativePath(profile.Path, fullPath).Replace('\\', '/'),
        Normalize(Decode(bytes)), Version(bytes));
    }, token);

  public Task<StoryWorkspaceDocument> SaveAsync(StoryWorkspaceSave request, CancellationToken token) =>
    InWorkspaceAsync(request.WorkspaceId, async profile =>
    {
      if (request.Document is null || request.Document.Length > 200_000 || request.Document.Contains('\0')
        || string.IsNullOrWhiteSpace(request.ExpectedVersion))
        throw new StoryEditorException("story-file-input-invalid", "A valid draft and the opened file version are required.");
      var fullPath = await ResolveFileAsync(request.Path, token);
      // One exclusive handle covers comparison, write and verification. A second writer
      // cannot slip between the version check and replacement on the supported Windows host.
      await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
        65536, FileOptions.Asynchronous);
      var original = await ReadAsync(stream, token);
      if (!string.Equals(Version(original), request.ExpectedVersion, StringComparison.Ordinal))
        throw new StoryEditorException("story-file-conflict",
          "The workspace file changed outside this draft. Your draft is preserved. Reopen the file to review the latest version, or export your draft.", 409);
      var text = Decode(original);
      var normalized = Normalize(request.Document);
      if (Normalize(text) == normalized)
        return new StoryWorkspaceDocument(profile.Id, request.Path, normalized, Version(original));
      var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') ? "\r" : "\n";
      var content = Utf8.GetBytes(normalized.Replace("\n", newline, StringComparison.Ordinal));
      var updated = original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })
        ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(content).ToArray() : content;
      token.ThrowIfCancellationRequested();
      stream.Position = 0;
      // Finish a started write even if the browser disconnects; cancellation must not
      // leave a deliberately truncated file. The next open observes the verified version.
      await stream.WriteAsync(updated, CancellationToken.None);
      stream.SetLength(updated.Length);
      await stream.FlushAsync(CancellationToken.None);
      stream.Position = 0;
      var verified = await ReadAsync(stream, CancellationToken.None);
      if (!verified.AsSpan().SequenceEqual(updated))
        throw new StoryEditorException("story-file-save-unverified", "The saved file could not be verified. Keep your draft and check the file before retrying.", 500);
      return new StoryWorkspaceDocument(profile.Id, request.Path, normalized, Version(verified));
    }, token);

  private async Task<string> ResolveFileAsync(string path, CancellationToken token)
  {
    var relative = Relative(path);
    if (!Editable(relative)) throw new StoryEditorException("story-file-type-invalid", "Select a UTF-8 .md or .txt file from the workspace.");
    var fullPath = await workspace.ResolvePathAsync(relative, token);
    if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly))
      throw new StoryEditorException("story-file-read-only", "This workspace file is read-only.", 409);
    return fullPath;
  }

  private async Task<T> InWorkspaceAsync<T>(string workspaceId, Func<WorkspaceProfileData, Task<T>> action, CancellationToken token)
  {
    try
    {
      var active = await profiles.GetActiveDataAsync(token);
      if (active is null || active.Id != workspaceId)
        throw new StoryEditorException("story-workspace-changed", "The active project changed. Reopen the workspace file selector. Your draft is preserved.", 409);
      using var scope = context.Push(active);
      return await action(active);
    }
    catch (LocalActionException exception)
    {
      throw new StoryEditorException("story-file-path-denied", "The file must be inside the active trusted workspace, outside .git and without symbolic links.", 400, innerException: exception);
    }
    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
    {
      throw new StoryEditorException("story-file-not-found", "The workspace file or folder no longer exists. Refresh the selector. Your draft is preserved.", 404, innerException: exception);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      throw new StoryEditorException("story-file-unavailable", "The workspace file is unavailable, locked or could not be saved. Your draft is preserved.", 409, innerException: exception);
    }
  }

  private static string Relative(string? path, bool allowRoot = false)
  {
    if (allowRoot && (string.IsNullOrEmpty(path) || path == ".")) return ".";
    if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
      || path.Replace('\\', '/').Split('/').Any(part => part is ".." or "." or ""
        || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
      throw new StoryEditorException("story-file-path-denied", "Select a relative file or folder path inside the active workspace.");
    return path;
  }

  private static bool Editable(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt";
  private static string Version(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
  private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
  private static string Decode(byte[] bytes)
  {
    try
    {
      var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
      var text = Utf8.GetString(bytes, offset, bytes.Length - offset);
      if (text.Length > 200_000 || text.Contains('\0'))
        throw new StoryEditorException("story-file-content-invalid", "Select a UTF-8 text file of up to 200,000 characters.");
      return text;
    }
    catch (DecoderFallbackException exception)
    {
      throw new StoryEditorException("story-file-encoding-invalid", "This file is not valid UTF-8 text. Convert it to UTF-8 before opening it.", innerException: exception);
    }
  }

  private static async Task<byte[]> ReadAsync(FileStream stream, CancellationToken token)
  {
    if (stream.Length > MaximumBytes) throw new StoryEditorException("story-file-too-large", "The file exceeds the editor size limit.");
    var bytes = new byte[(int)stream.Length];
    await stream.ReadExactlyAsync(bytes, token);
    return bytes;
  }
}
