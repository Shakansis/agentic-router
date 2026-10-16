using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Platform;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Sessions;

public sealed record HostFileProgress(string OperationId, string Action, string State,
  double? Percent, long CompletedBytes, long? TotalBytes, long ElapsedMilliseconds,
  IReadOnlyList<HostFileTiming> Timings, string? ScopeNote = null);
public sealed record HostFileTiming(string Action, long ElapsedMilliseconds, long Bytes);
public sealed record HistoryAttachment(string Id, string FileName, string MimeType, long Bytes, string Digest);
public sealed record HistoryFileVersion(string Path, string Digest, long Bytes, DateTimeOffset ModifiedAt);
public sealed record HistoryTurn(string SessionId, string TurnId, int MessageIndex, string Message,
  string Mode, DateTimeOffset CreatedAt, IReadOnlyList<HistoryAttachment> Attachments,
  IReadOnlyList<HistoryFileVersion>? Before, IReadOnlyList<HistoryFileVersion>? After,
  string? GitHead, string? GitBranch, IReadOnlyList<string>? GitStaged,
  SessionSummaryRecord? PriorSummary, IReadOnlyList<HostFileTiming> Timings,
  bool Settled = false, string? CaptureError = null, string? GitIndexDigest = null,
  IReadOnlyList<string>? MutationPaths = null);
public sealed record HistoryFileItem(string Id, string Name, string SessionId, string Conversation,
  string TurnId, DateTimeOffset CreatedAt, long Bytes, string Kind, bool Compressed);
public sealed record HistoryRewindRequest(string SessionId, string TurnId, int MessageIndex,
  string WindowId, string BrowserSessionId);
public sealed record HistoryRewindResult(ConversationSessionRecord? Session, string Message,
  string Mode, IReadOnlyList<ChatImageAttachment> Images, int MessageIndex, string BrowserSessionId);
public sealed record HostFileOperationRequest(string OperationId, string WindowId);
public sealed record HistoryOpenFileRequest(string FileId);
public sealed record HistoryGitDecision(string WindowId, string Choice);

public sealed class ConversationHistoryFiles(
  HistoryContentStore content, IWorkspaceProfileService profiles, IPersistentSessionStore sessions,
  IPersistentSessionService sessionService, IGitRepositoryService git, LiveChatRuns runs,
  ISettingsStore settings, ILogger<ConversationHistoryFiles> logger, IExecutionSessionStore executions) : IDisposable
{
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly ConcurrentDictionary<string, HistoryTurn> _temporary = new();
  private readonly ConcurrentDictionary<string, byte> _contextOnly = new();
  private readonly ConcurrentDictionary<string, byte> _releasedWindows = new();
  private readonly ConcurrentDictionary<string, HostFileProgress> _progress = new();
  private readonly ConcurrentDictionary<string, string> _operationWorkspaces = new();
  private readonly ConcurrentDictionary<string, byte> _activeCaptures = new();
  private readonly ConcurrentDictionary<string, WorkspaceProfileData> _captureWorkspaces = new();
  private readonly ConcurrentDictionary<string, ChatRequest> _captureRequests = new();
  private readonly ConcurrentDictionary<string, Func<HostFileProgress, Task>> _capturePublishers = new();
  private readonly string _temporaryRoot = Path.Combine(content.Root, "temporary", Guid.NewGuid().ToString("N"));

  public HostFileProgress? Progress(string workspaceId, string id) =>
    _operationWorkspaces.GetValueOrDefault(id) == workspaceId ? _progress.GetValueOrDefault(id) : null;

  private void TrackOperation(string id, string workspace)
  {
    foreach (var old in _progress.Where(pair => pair.Value.State is "completed" or "failed")
      .Take(Math.Max(0, _progress.Count - 128)))
    { _progress.TryRemove(old.Key, out _); _operationWorkspaces.TryRemove(old.Key, out _); }
    _operationWorkspaces[id] = workspace;
  }

  private string Space(WorkspaceProfileData workspace, string window) => workspace.HistoryEnabled
    ? content.Namespace(workspace.Id)
    : Path.Combine(_temporaryRoot, HistoryContentStore.RequireId(window), workspace.Id);
  private string TemporarySpace(string workspace, string window) =>
    Path.Combine(_temporaryRoot, HistoryContentStore.RequireId(window), workspace);
  private string SourceSpace(WorkspaceProfileData workspace, string window, string digest)
  {
    var temporary = TemporarySpace(workspace.Id, window);
    var path = HistoryContentStore.BlobPath(temporary, digest);
    return File.Exists(path) || File.Exists(path + ".gz") ? temporary : content.Namespace(workspace.Id);
  }
  private static string TurnPath(string space, string session, string turn) =>
    Path.Combine(space, "turns", HistoryContentStore.RequireId(session),
      Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(turn))) + ".json");
  private static string Key(string workspace, string window, string session, string turn) =>
    $"{workspace}/{window}/{session}/{turn}";

  public async Task<WorkspaceProfileData> ActiveAsync(CancellationToken token) =>
    await profiles.GetActiveDataAsync(token)
      ?? throw Error("workspace-required", "Select a trusted workspace first.");

  public async Task PromoteAsync(string window, string sessionId, CancellationToken token)
  {
    var workspace = await ActiveAsync(token);
    if (!workspace.HistoryEnabled) return;
    await _gate.WaitAsync(token);
    try
    {
      var prefix = Key(workspace.Id, window, sessionId, "");
      foreach (var (key, turn) in _temporary.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
      {
        var digests = turn.Attachments.Select(item => item.Digest)
          .Concat((turn.Before ?? []).Select(item => item.Digest)).Concat((turn.After ?? []).Select(item => item.Digest)).Distinct();
        foreach (var digest in digests)
          await content.CloneAsync(SourceSpace(workspace, window, digest), content.Namespace(workspace.Id), digest, token);
        await SaveAsync(workspace, window, turn, token);
        _temporary.TryRemove(key, out _);
      }
      var stored = await sessions.ReadAsync(workspace.Id, sessionId, token);
      if (stored is not null)
      {
        var turns = await ReadTurnsAsync(workspace, window, sessionId, token);
        var attachments = turns.ToDictionary(turn => turn.TurnId, turn => turn.Attachments);
        var limits = (await settings.GetAsync(token)).SessionHistory;
        await sessions.WriteAsync(stored with
        {
          Messages = stored.Messages.Select(message => message.Role == "user" && message.TurnId is { } id && attachments.TryGetValue(id, out var images)
            ? message with { Attachments = images } : message).ToArray()
        }, limits.SessionCompactionThresholdBytes, limits.SessionCompactionTargetBytes, token);
      }
    }
    finally { _gate.Release(); }
  }

  public async Task<ConversationSessionRecord> DuplicateAttachmentsAsync(string sourceId, ConversationSessionRecord duplicate, CancellationToken token)
  {
    var workspace = (await ActiveAsync(token)) with { HistoryEnabled = true };
    await _gate.WaitAsync(token);
    try
    {
      var source = (await ReadTurnsAsync(workspace, "", sourceId, token)).OrderBy(turn => turn.MessageIndex).ToList();
      var messages = duplicate.Messages.ToArray();
      for (var index = 0; index < messages.Length; index++)
      {
        if (messages[index].Role != "user") continue;
        var original = source.FirstOrDefault(turn => turn.Message == messages[index].Content);
        if (original is null) continue;
        source.Remove(original);
        var turnId = Guid.NewGuid().ToString("N");
        messages[index] = messages[index] with { TurnId = turnId, InteractionMode = "chat", Attachments = original.Attachments };
        await SaveAsync(workspace, "", original with
        {
          SessionId = duplicate.Id,
          TurnId = turnId,
          MessageIndex = index,
          Mode = "chat",
          Before = null,
          After = null,
          GitHead = null,
          GitBranch = null,
          GitStaged = null,
          GitIndexDigest = null,
          Settled = true,
          CaptureError = null,
          PriorSummary = null,
          Timings = []
        }, token);
      }
      var limits = (await settings.GetAsync(token)).SessionHistory;
      return await sessions.WriteAsync(duplicate with { Messages = messages }, limits.SessionCompactionThresholdBytes,
        limits.SessionCompactionTargetBytes, token);
    }
    finally { _gate.Release(); }
  }

  public async Task<object> GitDecisionAsync(HistoryGitDecision request, CancellationToken token)
  {
    var workspace = await ActiveAsync(token);
    HistoryContentStore.RequireId(request.WindowId);
    var overview = await git.GetWorkspaceOverviewAsync(workspace.Id, workspace.Path, [], token);
    if (request.Choice == "context-only")
    {
      _contextOnly[Key(workspace.Id, request.WindowId, "", "")] = 0;
      return new { ready = true, fileRollback = false };
    }
    if (request.Choice == "initialize")
    {
      if (overview.State == "not-initialized")
        overview = await git.InitializeAsync(workspace.Id, workspace.Path, [],
          new GitInitializeRequest(request.WindowId, "execute", overview.InitializeActionId, true), token);
      if (overview.Repository is not { Available: true })
        throw Error("git-unavailable", overview.Diagnostic ?? "Git is unavailable.");
      if (string.IsNullOrWhiteSpace(overview.UserName.Value) || string.IsNullOrWhiteSpace(overview.UserEmail.Value))
        throw Error("git-identity-required", "Configure Git user name and email in the project Git settings, then retry.");
      var status = await git.GetStatusAsync(workspace.Path, false, token);
      if (status.Head is null)
      {
        var paths = status.StagedPaths.Concat(status.UnstagedPaths).Concat(status.UntrackedPaths).Distinct().ToArray();
        if (paths.Length == 0) throw Error("git-initial-commit-empty", "There are no non-ignored files for the initial commit.");
        await git.StageInitialStateAsync(workspace.Path, token);
        await git.CommitAsync(workspace.Path, "Initial project state", token);
        status = await git.GetStatusAsync(workspace.Path, false, token);
        if (status.Head is null) throw Error("git-initial-commit-unverified", "The initial commit could not be verified.");
      }
    }
    if (request.Choice == "initialize")
      overview = await git.GetWorkspaceOverviewAsync(workspace.Id, workspace.Path, [], token);
    var available = overview.Repository is { Available: true, Head: not null };
    var accepted = _contextOnly.ContainsKey(Key(workspace.Id, request.WindowId, "", ""));
    return new { ready = available || accepted, fileRollback = available, state = overview.State, diagnostic = overview.Diagnostic };
  }

  public async Task PrepareAsync(ChatRequest request, string turnId,
    Func<HostFileProgress, Task> publish, CancellationToken token, IReadOnlyList<HostFileTiming>? preceding = null)
  {
    if (request.HistoryWindowId is null || request.ConversationSessionId is null) return;
    HistoryContentStore.RequireId(request.HistoryWindowId);
    HistoryContentStore.RequireId(request.ConversationSessionId);
    var workspace = await profiles.GetActiveDataAsync(token);
    if (workspace is null) return;
    await _gate.WaitAsync(token);
    var clock = Stopwatch.StartNew();
    try
    {
      var space = Space(workspace, request.HistoryWindowId);
      var timings = new List<HostFileTiming>(preceding ?? []);
      var operation = turnId;
      TrackOperation(operation, workspace.Id);
      long done = (preceding ?? []).Sum(item => item.Bytes);
      var precedingMilliseconds = (preceding ?? []).Sum(item => item.ElapsedMilliseconds);
      long? total = null;
      async Task Report(string action, string state = "running")
      {
        var value = new HostFileProgress(operation, action, state,
          total is > 0 ? Math.Min(100, 100d * done / total.Value) : state == "completed" ? 100 : null,
          done, total, clock.ElapsedMilliseconds + precedingMilliseconds, timings.ToArray(), request.InteractionMode == "execute"
            ? "Snapshots are captured only for mutation targets, including ignored files." : null);
        _progress[operation] = value;
        await publish(value);
      }
      await Report(request.InteractionMode == "execute" ? "Checking snapshot coverage" : "Preparing prompt data");
      GitRepositoryStatusView? repository = null;
      List<FileInfo> files = [];
      if (request.InteractionMode == "execute")
      {
        try { repository = await git.GetStatusAsync(workspace.Path, false, token); }
        catch (GitDeliveryException exception) when (exception.Code is "git-repository-unavailable" or "git-repository-outside-workspace")
        { repository = null; }
        if (repository is not { Available: true, Head: not null }
          && !_contextOnly.ContainsKey(Key(workspace.Id, request.HistoryWindowId, "", "")))
          throw Error("snapshot-git-decision-required", "Choose whether to initialize Git or continue with context-only rollback before Execute.");
        timings.Add(new("Checking snapshot coverage", clock.ElapsedMilliseconds, 0));
      }
      var attachmentBytes = (request.Images ?? []).Sum(image => image.DeclaredBytes);
      total = done + files.Sum(file => file.Length) + attachmentBytes + 1;
      var attachments = new List<HistoryAttachment>();
      var started = clock.ElapsedMilliseconds;
      await Report("Saving prompt attachments");
      foreach (var image in request.Images ?? [])
      {
        var bytes = Convert.FromBase64String(image.Base64Data);
        await using var input = new MemoryStream(bytes, false);
        var digest = await content.PutAsync(space, input, token, size => done += size);
        attachments.Add(new(image.Id, SafeName(image.FileName), image.MimeType, bytes.Length, digest));
        await Report("Saving prompt attachments");
      }
      timings.Add(new("Saving prompt attachments", clock.ElapsedMilliseconds - started, attachmentBytes));
      var session = workspace.HistoryEnabled
        ? await sessions.ReadAsync(workspace.Id, request.ConversationSessionId, token) : null;
      var index = session is null ? request.HistoryMessageIndex ?? request.History?.Count ?? 0 : session.Messages.Count - 1;
      var turn = new HistoryTurn(request.ConversationSessionId, turnId, index, request.Message,
        request.InteractionMode, DateTimeOffset.UtcNow, attachments, null, null,
        repository?.Head, repository?.Branch, repository?.StagedPaths, session?.SessionSummary, timings.ToArray(),
        CaptureError: repository is { Available: true, Head: not null }
          ? "Snapshot preparation did not complete; execution did not start." : null);
      // Retain the original Composer payload even if baseline capture is interrupted.
      await SaveAsync(workspace, request.HistoryWindowId, turn, token);
      IReadOnlyList<HistoryFileVersion>? before = null;
      if (repository is { Available: true, Head: not null })
      {
        before = [];
      }
      turn = turn with
      {
        Before = before,
        MutationPaths = before is null ? null : [],
        CaptureError = null,
        Timings = timings.ToArray(),
        GitIndexDigest = repository?.Available == true ? await git.GetIndexFingerprintAsync(workspace.Path, token) : null
      };
      await SaveAsync(workspace, request.HistoryWindowId, turn, token);
      if (session is not null && attachments.Count > 0)
      {
        var limits = (await settings.GetAsync(token)).SessionHistory;
        var savingStarted = clock.ElapsedMilliseconds;
        await Report(session.StorageBytes >= limits.SessionCompactionThresholdBytes
          ? "Compacting historical chat" : "Saving historical chat");
        await sessions.WriteAsync(session with
        {
          Messages = session.Messages.Select(message => message.TurnId == turnId && message.Role == "user"
            ? message with { Attachments = attachments } : message).ToArray()
        }, limits.SessionCompactionThresholdBytes, limits.SessionCompactionTargetBytes, token);
        timings.Add(new("Saving historical chat", clock.ElapsedMilliseconds - savingStarted, 0));
        await SaveAsync(workspace, request.HistoryWindowId, turn with { Timings = timings.ToArray() }, token);
      }
      _activeCaptures[Key(workspace.Id, request.HistoryWindowId, turn.SessionId, turnId)] = 0;
      _captureWorkspaces[Key("", request.HistoryWindowId, turn.SessionId, turnId)] = workspace;
      _captureRequests[Key("", request.HistoryWindowId, turn.SessionId, turnId)] = request;
      _capturePublishers[turnId] = publish;
      done++;
      await Report(before is null ? "Prompt data saved" : "File rollback ready; snapshots deferred until mutation", "completed");
      logger.LogInformation("Host file preparation {OperationId}: {Bytes} bytes, {Milliseconds} ms, {Timings}",
        operation, done, clock.ElapsedMilliseconds, JsonSerializer.Serialize(timings));
    }
    catch
    {
      if (_progress.TryGetValue(turnId, out var last))
      {
        var elapsed = clock.ElapsedMilliseconds + (preceding ?? []).Sum(item => item.ElapsedMilliseconds);
        _progress[turnId] = last with
        {
          State = "failed",
          Action = "Prompt preparation failed; execution did not start",
          ElapsedMilliseconds = elapsed,
          Timings = [.. last.Timings,
            new(last.Action, Math.Max(0, elapsed - last.Timings.Sum(item => item.ElapsedMilliseconds)), 0)]
        };
        try { await publish(_progress[turnId]); }
        catch (Exception exception) { logger.LogWarning(exception, "Prompt preparation failure could not be presented to its browser."); }
        if (workspace.HistoryEnabled)
        {
          try
          {
            await HistoryContentStore.WriteJsonAsync(Path.Combine(content.Namespace(workspace.Id), "operations", turnId + ".json"),
              _progress[turnId], CancellationToken.None);
          }
          catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
          { logger.LogWarning(exception, "Failed prompt preparation diagnostics could not be saved."); }
        }
      }
      throw;
    }
    finally { _gate.Release(); }
  }

  public async Task CaptureBeforeMutationAsync(string turnId, IReadOnlyList<string> targets, CancellationToken token)
  {
    var entry = _captureRequests.FirstOrDefault(pair => pair.Key == Key("", pair.Value.HistoryWindowId!,
      pair.Value.ConversationSessionId!, turnId));
    if (entry.Value is not { } request || request.HistoryWindowId is null || request.ConversationSessionId is null) return;
    var workspace = _captureWorkspaces.GetValueOrDefault(entry.Key);
    if (workspace is null) return;
    await _gate.WaitAsync(token);
    try
    {
      var turn = await ReadAsync(workspace, request.HistoryWindowId, request.ConversationSessionId, turnId, token);
      if (turn?.Before is null || turn.Settled || turn.MutationPaths is null) return;
      var latest = (await profiles.GetAllAsync(token)).Profiles.FirstOrDefault(profile => profile.Id == workspace.Id);
      workspace = workspace with { HistoryEnabled = latest?.HistoryEnabled == true };
      var paths = turn.MutationPaths.ToHashSet(FileSystemPathSemantics.Comparer);
      var files = new List<FileInfo>();
      foreach (var target in targets)
      {
        var full = Path.GetFullPath(Path.Combine(workspace.Path, target));
        EnsureSafe(workspace.Path, full);
        if (full.StartsWith(content.HostDataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
          FileSystemPathSemantics.Comparison)) throw Error("snapshot-host-storage", "AR storage cannot be a mutation snapshot target.");
        var candidates = Directory.Exists(full) ? Enumerate(full, content.HostDataDirectory, token) : [new FileInfo(full)];
        foreach (var file in candidates)
          if (paths.Add(Path.GetRelativePath(workspace.Path, file.FullName).Replace('\\', '/')) && file.Exists)
            files.Add(file);
      }
      if (paths.Count == turn.MutationPaths.Count) return;
      var clock = Stopwatch.StartNew();
      var operation = Guid.NewGuid().ToString("N");
      TrackOperation(operation, workspace.Id);
      long done = 0;
      var total = files.Sum(file => file.Length) + 1;
      async Task Report(string state = "running")
      {
        var progress = new HostFileProgress(operation, "Creating snapshot", state, 100d * done / total,
          done, total, clock.ElapsedMilliseconds, []);
        _progress[operation] = progress;
        if (_capturePublishers.TryGetValue(turnId, out var publish)) await publish(progress);
      }
      await Report();
      var captured = await CaptureAsync(workspace.Path, Space(workspace, request.HistoryWindowId), files,
        size => done += size, () => Report(), token);
      var timing = new HostFileTiming("Creating snapshot", clock.ElapsedMilliseconds, done);
      await SaveAsync(workspace, request.HistoryWindowId, turn with
      {
        Before = [.. turn.Before, .. captured],
        MutationPaths = paths.Order(FileSystemPathSemantics.Comparer).ToArray(),
        Timings = [.. turn.Timings, timing]
      }, token);
      done = total;
      await Report("completed");
      logger.LogInformation("Host mutation snapshot {TurnId}: {Files} targets, {Bytes} bytes, {Milliseconds} ms",
        turnId, captured.Count, timing.Bytes, timing.ElapsedMilliseconds);
    }
    finally { _gate.Release(); }
  }

  private async Task<HistoryTurn> RetainVerifiedMutationBytesAsync(WorkspaceProfileData workspace, string window,
    HistoryTurn turn, IReadOnlyList<ExecutionFileChange> changes, CancellationToken token)
  {
    var paths = turn.MutationPaths!.ToHashSet(FileSystemPathSemantics.Comparer);
    var before = turn.Before!.ToList();
    var uncaptured = changes.Where(change => !paths.Contains(change.RelativePath)
      && !change.Operation.EndsWith("directory", StringComparison.Ordinal)).ToArray();
    if (uncaptured.Length == 0) return turn;
    var status = await git.GetStatusAsync(workspace.Path, false, token);
    var visiblePaths = status.Paths.Where(path => !path.Ignored)
      .Select(path => path.Path).ToHashSet(FileSystemPathSemantics.Comparer);
    foreach (var change in uncaptured.Where(change => visiblePaths.Contains(change.RelativePath)))
    {
      if (!paths.Add(change.RelativePath)) continue;
      if (!change.ExistedBefore) continue;
      var bytes = change.OriginalBinaryBase64 is not null ? Convert.FromBase64String(change.OriginalBinaryBase64)
        : change.OriginalContent is not null ? System.Text.Encoding.UTF8.GetBytes(change.OriginalContent) : null;
      if (bytes is null)
      {
        turn = turn with { CaptureError = "A native mutation has no retained original bytes; file rollback is unavailable." };
        continue;
      }
      await using var input = new MemoryStream(bytes, false);
      var digest = await content.PutAsync(Space(workspace, window), input, token);
      before.Add(new(change.RelativePath, digest, bytes.Length, turn.CreatedAt));
    }
    return turn with { Before = before, MutationPaths = paths.Order(FileSystemPathSemantics.Comparer).ToArray() };
  }

  public async Task SettleAsync(ChatRequest request, string turnId,
    Func<HostFileProgress, Task> publish, CancellationToken token)
  {
    if (request.HistoryWindowId is null || request.ConversationSessionId is null) return;
    var workspace = _captureWorkspaces.GetValueOrDefault(Key("", request.HistoryWindowId, request.ConversationSessionId, turnId));
    if (workspace is null) return;
    var key = Key(workspace.Id, request.HistoryWindowId, request.ConversationSessionId, turnId);
    if (!_activeCaptures.TryRemove(key, out _)) return;
    await _gate.WaitAsync(token);
    try
    {
      var turn = await ReadAsync(workspace, request.HistoryWindowId, request.ConversationSessionId, turnId, token);
      if (turn is null || turn.Settled) return;
      // A privacy toggle stops new durable byte writes even for a turn already running.
      var latest = (await profiles.GetAllAsync(token)).Profiles.FirstOrDefault(profile => profile.Id == workspace.Id);
      workspace = workspace with { HistoryEnabled = latest?.HistoryEnabled == true };
      if (turn.Before is null)
      {
        await SaveAsync(workspace, request.HistoryWindowId, turn with { Settled = true }, token);
        return;
      }
      var clock = Stopwatch.StartNew();
      var operation = Guid.NewGuid().ToString("N");
      TrackOperation(operation, workspace.Id);
      await publish(new(operation, "Enumerating snapshot result", "running", null, 0, null, 0, []));
      // Native/process recovery already owns verified pre-mutation bytes. Persist only
      // affected files from that evidence when the native protocol had no pre-write callback.
      if (turn.MutationPaths is not null && request.BrowserSessionId is not null
        && executions.GetActive(request.BrowserSessionId) is { } execution && execution.RequestId == turnId)
        turn = await RetainVerifiedMutationBytesAsync(workspace, request.HistoryWindowId, turn,
          execution.CreatePersistenceSnapshot().Files, token);
      var files = turn.MutationPaths is null
        ? Enumerate(workspace.Path, content.HostDataDirectory, token)
        : turn.MutationPaths.Select(path => new FileInfo(Path.Combine(workspace.Path, path)))
          .Where(file => file.Exists).ToList();
      var total = files.Sum(file => file.Length) + 1;
      long done = 0;
      var after = await CaptureAsync(workspace.Path, Space(workspace, request.HistoryWindowId), files,
        size => done += size, async () =>
        {
          var value = new HostFileProgress(operation, "Recording snapshot result", "running", 100d * done / total,
            done, total, clock.ElapsedMilliseconds, []);
          _progress[operation] = value;
          await publish(value);
        }, token);
      var timing = new HostFileTiming("Recording snapshot result", clock.ElapsedMilliseconds, done);
      await SaveAsync(workspace, request.HistoryWindowId,
        turn with { After = after, Settled = true, Timings = [.. turn.Timings, timing] }, token);
      var completed = new HostFileProgress(operation, timing.Action, "completed", 100, total, total,
        clock.ElapsedMilliseconds, [timing]);
      _progress[operation] = completed;
      await publish(completed);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorkspaceProfileException)
    {
      logger.LogWarning(exception, "The final snapshot could not be recorded for {TurnId}.", turnId);
      var turn = await ReadAsync(workspace, request.HistoryWindowId, request.ConversationSessionId, turnId, token);
      if (turn is not null)
        await SaveAsync(workspace, request.HistoryWindowId,
          turn with { Settled = true, CaptureError = "Final snapshot unavailable; automatic file rollback cannot be verified." }, token);
      throw;
    }
    finally
    {
      _captureWorkspaces.TryRemove(Key("", request.HistoryWindowId, request.ConversationSessionId, turnId), out _);
      _captureRequests.TryRemove(Key("", request.HistoryWindowId, request.ConversationSessionId, turnId), out _);
      _capturePublishers.TryRemove(turnId, out _);
      _gate.Release();
      if (_releasedWindows.TryRemove(request.HistoryWindowId, out _))
        await ReleaseWindowAsync(request.HistoryWindowId, CancellationToken.None);
    }
  }

  public async Task SettleConversationAsync(string workspaceId, string sessionId)
  {
    foreach (var (key, request) in _captureRequests.Where(pair => pair.Value.ConversationSessionId == sessionId).ToArray())
    {
      if (_captureWorkspaces.GetValueOrDefault(key)?.Id != workspaceId) continue;
      var turnId = key[(key.LastIndexOf('/') + 1)..];
      try { await SettleAsync(request, turnId, _ => Task.CompletedTask, CancellationToken.None); }
      catch (Exception exception) { logger.LogWarning(exception, "Could not settle detached Host snapshot for {TurnId}.", turnId); }
    }
  }

  private static List<FileInfo> Enumerate(string root, string? hostDataDirectory = null, CancellationToken token = default)
  {
    if (hostDataDirectory is not null && FileSystemPathSemantics.Comparer.Equals(Path.GetFullPath(root), hostDataDirectory))
      throw Error("snapshot-host-storage-root", "AR's own data directory cannot be used as a project snapshot root.");
    var files = new List<FileInfo>();
    var directories = new Stack<string>();
    directories.Push(root);
    while (directories.TryPop(out var directory))
    {
      token.ThrowIfCancellationRequested();
      EnsureSafe(root, directory);
      foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
      {
        if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
        if (hostDataDirectory is not null && FileSystemPathSemantics.Comparer.Equals(entry.FullName, hostDataDirectory)) continue;
        token.ThrowIfCancellationRequested();
        // The directory's ancestry was checked immediately above. Enumeration
        // supplies direct child paths and cached attributes; file capture checks
        // the full ancestry again before reading any bytes.
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
          throw Error("snapshot-reparse-point", "Snapshot coverage cannot traverse linked files or directories.");
        if ((entry.Attributes & FileAttributes.Directory) != 0) directories.Push(entry.FullName);
        else files.Add((FileInfo)entry);
      }
    }
    files.Sort((a, b) => FileSystemPathSemantics.Comparer.Compare(a.FullName, b.FullName));
    return files;
  }

  private static void EnsureSafe(string root, string path)
  {
    var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(path);
    if (!FileSystemPathSemantics.Comparer.Equals(full.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar))
      && !full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
      throw Error("snapshot-path-escape", "A snapshot target escaped the trusted workspace.");
    var relative = Path.GetRelativePath(root, full);
    if (relative.Split(Path.DirectorySeparatorChar).Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
      throw Error("snapshot-protected-path", "Git metadata cannot be restored through file rollback.");
    var current = full;
    while (true)
    {
      if (File.Exists(current) || Directory.Exists(current))
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
          throw Error("snapshot-reparse-point", "Snapshot coverage cannot traverse linked files or directories.");
      if (FileSystemPathSemantics.Comparer.Equals(current.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar))) break;
      current = Path.GetDirectoryName(current) ?? throw Error("snapshot-path-escape", "Invalid snapshot path.");
    }
  }

  private async Task<IReadOnlyList<HistoryFileVersion>> CaptureAsync(string root, string space, List<FileInfo> files,
    Action<long> advanced, Func<Task> report, CancellationToken token)
  {
    var result = new List<HistoryFileVersion>();
    var clock = Stopwatch.StartNew();
    foreach (var file in files)
    {
      token.ThrowIfCancellationRequested();
      EnsureSafe(root, file.FullName);
      var length = file.Length;
      var modified = file.LastWriteTimeUtc;
      await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, true);
      var digest = await content.PutAsync(space, input, token, advanced, report);
      file.Refresh();
      if (file.Length != length || file.LastWriteTimeUtc != modified)
        throw Error("snapshot-capture-conflict", "A workspace file changed during snapshot capture. Retry when file writes have settled.");
      result.Add(new(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), digest, length, modified));
      if (clock.ElapsedMilliseconds >= 100) { await report(); clock.Restart(); }
    }
    await report();
    return result;
  }

  private async Task SaveAsync(WorkspaceProfileData workspace, string window, HistoryTurn turn, CancellationToken token)
  {
    if (workspace.HistoryEnabled)
    {
      await HistoryContentStore.WriteJsonAsync(TurnPath(Space(workspace, window), turn.SessionId, turn.TurnId), turn, token);
      content.InvalidateSize(Space(workspace, window));
    }
    else _temporary[Key(workspace.Id, window, turn.SessionId, turn.TurnId)] = turn;
  }

  private async Task<HistoryTurn?> ReadAsync(WorkspaceProfileData workspace, string window, string session, string turn, CancellationToken token)
  {
    if (_temporary.TryGetValue(Key(workspace.Id, window, session, turn), out var temporary)) return temporary;
    var path = TurnPath(content.Namespace(workspace.Id), session, turn);
    if (!File.Exists(path) && File.Exists(path + ".gz")) path += ".gz";
    if (!File.Exists(path)) return null;
    await using var input = OpenManifest(path);
    return await JsonSerializer.DeserializeAsync<HistoryTurn>(input, HistoryContentStore.JsonOptions, token);
  }

  private async Task<IReadOnlyList<HistoryTurn>> ReadTurnsAsync(WorkspaceProfileData workspace, string window, string? session, CancellationToken token)
  {
    var temporary = _temporary.Where(pair => pair.Key.StartsWith($"{workspace.Id}/{window}/", StringComparison.Ordinal))
      .Select(pair => pair.Value).Where(turn => session is null || turn.SessionId == session).ToArray();
    var directory = Path.Combine(content.Namespace(workspace.Id), "turns");
    if (session is not null) directory = Path.Combine(directory, HistoryContentStore.RequireId(session));
    if (!Directory.Exists(directory)) return temporary;
    var result = new List<HistoryTurn>(temporary);
    foreach (var path in ManifestPaths(directory))
    {
      await using var input = OpenManifest(path);
      var turn = await JsonSerializer.DeserializeAsync<HistoryTurn>(input, HistoryContentStore.JsonOptions, token);
      if (turn is not null) result.Add(turn);
    }
    return result.DistinctBy(turn => (turn.SessionId, turn.TurnId)).ToArray();
  }

  public async Task<HistoryRewindResult> RewindAsync(HistoryRewindRequest request, string operationId, CancellationToken token)
  {
    var workspace = await ActiveAsync(token);
    if (runs.FindConversation(request.SessionId) is { HasTerminal: true } settling)
      await settling.WaitForCompletionAsync(token);
    using var admission = runs.ReserveRewind(request.SessionId)
      ?? throw Error("history-operation-active", "Wait for conversation execution or rollback to settle.");
    await _gate.WaitAsync(token);
    var clock = Stopwatch.StartNew();
    var timings = new List<HostFileTiming>();
    var completed = false;
    try
    {
      var turn = await ReadAsync(workspace, request.WindowId, request.SessionId, request.TurnId, token);
      if (turn is null)
      {
        var legacy = await sessions.ReadAsync(workspace.Id, request.SessionId, token);
        var message = legacy?.Messages.ElementAtOrDefault(request.MessageIndex);
        var execute = message?.InteractionMode == "execute" || legacy?.Messages.Skip(request.MessageIndex + 1)
          .TakeWhile(item => item.Role != "user").SelectMany(item => item.Timeline ?? [])
          .Any(item => item.ExecutionSession is not null) == true;
        if (message?.Role == "user" && message.TurnId == request.TurnId && !execute)
          turn = new(request.SessionId, request.TurnId, request.MessageIndex, message.Content, "chat",
            message.CreatedAt ?? DateTimeOffset.UtcNow, [], null, null, null, null, null, null, [], true);
      }
      if (turn is null) throw Error("snapshot-unavailable", "The original prompt data is unavailable for this window or historical turn.");
      if (turn.MessageIndex != request.MessageIndex) throw Error("session-edit-conflict", "The conversation changed before rollback.");
      var current = await sessions.ReadAsync(workspace.Id, request.SessionId, token);
      var durableTarget = current is not null && request.MessageIndex < current.Messages.Count
        && current.Messages[request.MessageIndex].TurnId == request.TurnId;
      if (workspace.HistoryEnabled && !durableTarget)
        throw Error("session-edit-conflict", "The stored conversation changed before rollback.");
      if (runs.FindConversation(request.SessionId) is not null
        || _activeCaptures.Keys.Any(key => key.StartsWith($"{workspace.Id}/", StringComparison.Ordinal)))
        throw Error("history-operation-active", "Wait for active workspace execution to settle before rollback.");
      var affected = (await ReadTurnsAsync(workspace, request.WindowId, request.SessionId, token))
        .Where(item => item.MessageIndex >= turn.MessageIndex).OrderBy(item => item.MessageIndex).ToArray();
      TrackOperation(operationId, workspace.Id);
      _progress[operationId] = new(operationId, "Checking rollback conflicts", "running", null, 0, null, 0, []);
      var images = new List<ChatImageAttachment>();
      if (turn.Attachments.Sum(item => item.Bytes) > 20 * 1024 * 1024)
        throw Error("history-attachment-invalid", "The stored prompt attachment sizes are invalid.");
      foreach (var attachment in turn.Attachments)
      {
        await using var copy = new MemoryStream();
        await content.CopyAsync(SourceSpace(workspace, request.WindowId, attachment.Digest), attachment.Digest, copy, token, 20 * 1024 * 1024);
        if (copy.Length != attachment.Bytes) throw Error("history-attachment-invalid", "The stored attachment size could not be verified.");
        images.Add(new(attachment.Id, attachment.FileName, attachment.MimeType,
          Convert.ToBase64String(copy.ToArray()), attachment.Bytes));
      }
      timings.Add(new("Checking rollback and recovering prompt attachments", clock.ElapsedMilliseconds,
        turn.Attachments.Sum(item => item.Bytes)));
      var started = clock.ElapsedMilliseconds;
      var recovery = new Dictionary<string, (HistoryFileVersion? Previous, HistoryFileVersion? Restored)>(FileSystemPathSemantics.Comparer);
      if (turn.Mode == "execute" && turn.Before is not null)
        recovery = await RestoreAsync(workspace, request.WindowId, turn, affected, operationId, clock, token);
      timings.Add(new("Verifying and restoring snapshot files", clock.ElapsedMilliseconds - started,
        recovery.Values.Sum(item => item.Restored?.Bytes ?? 0)));
      started = clock.ElapsedMilliseconds;
      _progress[operationId] = _progress[operationId] with
      {
        Action = "Restoring conversation context",
        ElapsedMilliseconds = clock.ElapsedMilliseconds,
        Timings = timings.ToArray()
      };
      ConversationSessionRecord? session;
      try
      {
        session = durableTarget
          ? await sessionService.RewindAsync(request.SessionId, request.TurnId, request.MessageIndex, turn.PriorSummary, token) : null;
      }
      catch (Exception exception)
      {
        logger.LogWarning(exception, "Context rewind failed; recovering the pre-rollback working files.");
        await RecoverWorkingFilesAsync(workspace, request.WindowId, recovery);
        throw;
      }
      timings.Add(new("Restoring conversation context", clock.ElapsedMilliseconds - started, 0));
      foreach (var removed in affected)
      {
        var path = TurnPath(content.Namespace(workspace.Id), removed.SessionId, removed.TurnId);
        try { File.Delete(path); File.Delete(path + ".gz"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { logger.LogWarning(exception, "Discarded snapshot cleanup will be retried during history collection."); }
        _temporary.TryRemove(Key(workspace.Id, request.WindowId, removed.SessionId, removed.TurnId), out _);
      }
      _progress[operationId] = new(operationId, "Rollback complete", "completed", 100, 1, 1,
        clock.ElapsedMilliseconds, timings.ToArray());
      if (workspace.HistoryEnabled)
      {
        try
        {
          await HistoryContentStore.WriteJsonAsync(Path.Combine(content.Namespace(workspace.Id), "operations", operationId + ".json"),
            _progress[operationId], CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { logger.LogWarning(exception, "Rollback completed; its timing record could not be saved."); }
      }
      completed = true;
      return new(session, turn.Message, turn.Mode, images, turn.MessageIndex, Guid.NewGuid().ToString("N"));
    }
    catch (Exception exception)
    {
      _progress[operationId] = new(operationId, exception is WorkspaceProfileException ? exception.Message : "Host rollback failed",
        "failed", null, 0, null, clock.ElapsedMilliseconds, timings.ToArray());
      throw;
    }
    finally
    {
      _gate.Release();
      if (completed)
      {
        try { await CollectAsync(workspace.Id, CancellationToken.None); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or WorkspaceProfileException)
        { logger.LogWarning(exception, "Rollback completed; unreferenced history cleanup will be retried."); }
      }
    }
  }

  private async Task<Dictionary<string, (HistoryFileVersion? Previous, HistoryFileVersion? Restored)>> RestoreAsync(
    WorkspaceProfileData workspace, string window, HistoryTurn selected,
    IReadOnlyList<HistoryTurn> affected, string operation, Stopwatch clock, CancellationToken token)
  {
    if (affected.Any(turn => turn.Before is not null && (!turn.Settled || turn.After is null || turn.CaptureError is not null)))
      throw Error("snapshot-incomplete", "File rollback is unavailable because an affected turn has no verified final snapshot.");
    var repository = await git.GetStatusAsync(workspace.Path, false, token);
    if (!repository.Available || repository.Head != selected.GitHead || repository.Branch != selected.GitBranch
      || !(repository.StagedPaths ?? []).SequenceEqual(selected.GitStaged ?? [])
      || await git.GetIndexFingerprintAsync(workspace.Path, token) != selected.GitIndexDigest)
      throw Error("snapshot-git-conflict", "Git HEAD, branch or staging changed. Resolve the Git state before file rollback.");
    var targets = new Dictionary<string, HistoryFileVersion?>(FileSystemPathSemantics.Comparer);
    var expected = new Dictionary<string, HistoryFileVersion?>(FileSystemPathSemantics.Comparer);
    foreach (var turn in affected.Where(item => item.Before is not null))
    {
      var before = turn.Before!.ToDictionary(file => file.Path, FileSystemPathSemantics.Comparer);
      var after = turn.After!.ToDictionary(file => file.Path, FileSystemPathSemantics.Comparer);
      foreach (var path in before.Keys.Union(after.Keys, FileSystemPathSemantics.Comparer))
      {
        before.TryGetValue(path, out var original);
        after.TryGetValue(path, out var result);
        if (original?.Digest == result?.Digest) continue;
        if (expected.TryGetValue(path, out var priorResult) && priorResult?.Digest != original?.Digest)
          throw Error("snapshot-between-turns-conflict", $"'{path}' changed between affected turns. Its external changes were preserved.");
        targets.TryAdd(path, original);
        expected[path] = result;
      }
    }
    // All conflicts and all stored content are checked before the first mutation.
    var staged = new List<(string Path, string? Copy, HistoryFileVersion? Original, string? Backup)>();
    var applied = new List<(string Path, string? Copy, HistoryFileVersion? Original, string? Backup)>();
    var temporary = Path.Combine(_temporaryRoot, "restore", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
      foreach (var (relative, original) in targets)
      {
        var path = Path.GetFullPath(Path.Combine(workspace.Path, relative));
        EnsureSafe(workspace.Path, path);
        if (FileSystemPathSemantics.Comparer.Equals(content.HostDataDirectory, path)
          || path.StartsWith(content.HostDataDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            FileSystemPathSemantics.Comparison))
          throw Error("snapshot-host-storage", "A snapshot cannot restore AR internal storage.");
        var current = File.Exists(path) ? await HashAsync(path, token) : null;
        if (current != expected[relative]?.Digest)
          throw Error("snapshot-file-conflict", $"'{relative}' changed after execution. Its current content was preserved.");
        string? copy = null;
        if (original is not null)
        {
          copy = Path.Combine(temporary, staged.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
          await using var output = File.Create(copy);
          await content.CopyAsync(SourceSpace(workspace, window, original.Digest), original.Digest, output, token);
        }
        string? backup = null;
        if (File.Exists(path))
        {
          backup = Path.Combine(temporary, staged.Count + ".current");
          File.Copy(path, backup);
          if (await HashAsync(backup, token) != expected[relative]?.Digest)
            throw Error("snapshot-file-conflict", $"'{relative}' changed while preparing rollback. Its current content was preserved.");
        }
        staged.Add((path, copy, original, backup));
      }
      var total = staged.Sum(item => item.Original?.Bytes ?? 1) + 1;
      long completed = 0;
      foreach (var item in staged)
      {
        token.ThrowIfCancellationRequested();
        EnsureSafe(workspace.Path, item.Path);
        var relative = Path.GetRelativePath(workspace.Path, item.Path).Replace('\\', '/');
        if ((File.Exists(item.Path) ? await HashAsync(item.Path, token) : null) != expected[relative]?.Digest)
          throw Error("snapshot-file-conflict", $"'{relative}' changed during rollback. Conversation history was retained.");
        if (item.Copy is null) File.Delete(item.Path);
        else
        {
          Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
          await ReplaceWithCopyAsync(item.Copy, item.Path, token);
        }
        applied.Add(item);
        if (item.Copy is not null) File.SetLastWriteTimeUtc(item.Path, item.Original!.ModifiedAt.UtcDateTime);
        var restored = File.Exists(item.Path) ? await HashAsync(item.Path, token) : null;
        if (restored != item.Original?.Digest) throw Error("snapshot-restore-unverified", $"Restoration of '{relative}' could not be verified.");
        completed += item.Original?.Bytes ?? 1;
        _progress[operation] = new(operation, "Restoring snapshot files", "running", 100d * completed / total,
          completed, total, clock.ElapsedMilliseconds, []);
      }
      return targets.ToDictionary(pair => pair.Key, pair => (expected[pair.Key], pair.Value), FileSystemPathSemantics.Comparer);
    }
    catch
    {
      // Recover the workspace if an I/O failure interrupts a multi-file restore.
      // Never overwrite an external edit that arrived after our own mutation.
      foreach (var item in applied.AsEnumerable().Reverse())
      {
        EnsureSafe(workspace.Path, item.Path);
        if ((File.Exists(item.Path) ? await HashAsync(item.Path, CancellationToken.None) : null) != item.Original?.Digest)
          continue;
        if (item.Backup is null) File.Delete(item.Path);
        else await ReplaceWithCopyAsync(item.Backup, item.Path, CancellationToken.None);
      }
      throw;
    }
    finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
  }

  private async Task RecoverWorkingFilesAsync(WorkspaceProfileData workspace, string window,
    Dictionary<string, (HistoryFileVersion? Previous, HistoryFileVersion? Restored)> recovery)
  {
    foreach (var (relative, versions) in recovery.Reverse())
    {
      var path = Path.GetFullPath(Path.Combine(workspace.Path, relative));
      EnsureSafe(workspace.Path, path);
      if ((File.Exists(path) ? await HashAsync(path, CancellationToken.None) : null) != versions.Restored?.Digest) continue;
      if (versions.Previous is null) File.Delete(path);
      else
      {
        var copy = Path.Combine(_temporaryRoot, "recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        try
        {
          await using (var output = File.Create(copy))
            await content.CopyAsync(SourceSpace(workspace, window, versions.Previous.Digest), versions.Previous.Digest, output, CancellationToken.None);
          await ReplaceWithCopyAsync(copy, path, CancellationToken.None);
          File.SetLastWriteTimeUtc(path, versions.Previous.ModifiedAt.UtcDateTime);
        }
        finally { if (File.Exists(copy)) File.Delete(copy); }
      }
    }
  }

  private static async Task<string> HashAsync(string path, CancellationToken token)
  {
    await using var input = File.OpenRead(path);
    return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
  }

  private static async Task ReplaceWithCopyAsync(string source, string target, CancellationToken token)
  {
    // Replacing the directory entry avoids writing through an existing hard link.
    var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".ar-restore-{Guid.NewGuid():N}.tmp");
    try
    {
      await using (var input = File.OpenRead(source))
      await using (var output = File.Create(temporary)) await input.CopyToAsync(output, token);
      File.Move(temporary, target, true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }

  public async Task<IReadOnlyList<HistoryFileItem>> ListAsync(CancellationToken token)
  {
    var workspace = await ActiveAsync(token);
    workspace = workspace with { HistoryEnabled = true };
    var metadata = await sessions.ReadAllMetadataAsync(workspace.Id, token);
    var titles = metadata.ToDictionary(item => item.Id, item => item.Title);
    var turns = await ReadTurnsAsync(workspace, "", null, token);
    var result = new List<HistoryFileItem>();
    foreach (var turn in turns.Where(turn => titles.ContainsKey(turn.SessionId)))
    {
      void Add(string name, string digest, long bytes, string kind, int ordinal) => result.Add(new(
        $"{turn.SessionId}|{turn.TurnId}|{kind}|{ordinal}", name, turn.SessionId, titles[turn.SessionId], turn.TurnId,
        turn.CreatedAt, bytes, kind, File.Exists(HistoryContentStore.BlobPath(Space(workspace, ""), digest) + ".gz")));
      for (var index = 0; index < turn.Attachments.Count; index++)
      {
        var item = turn.Attachments[index]; Add(item.FileName, item.Digest, item.Bytes, "attachment", index);
      }
      for (var index = 0; index < (turn.Before?.Count ?? 0); index++)
      {
        var item = turn.Before![index]; Add(item.Path, item.Digest, item.Bytes, "snapshot", index);
      }
    }
    return result.OrderByDescending(item => item.CreatedAt).ToArray();
  }

  public async Task<string> MaterializeAsync(string fileId, CancellationToken token)
  {
    var workspace = await ActiveAsync(token);
    workspace = workspace with { HistoryEnabled = true };
    var parts = fileId.Split('|');
    if (parts.Length != 4 || !int.TryParse(parts[3], out var index) || index < 0)
      throw Error("history-file-invalid", "Invalid saved file identifier.");
    var turn = await ReadAsync(workspace, "", parts[0], parts[1], token)
      ?? throw Error("history-file-missing", "The saved file is unavailable.");
    string name;
    string digest;
    if (parts[2] == "attachment" && index < turn.Attachments.Count)
    { name = turn.Attachments[index].FileName; digest = turn.Attachments[index].Digest; }
    else if (parts[2] == "snapshot" && index < (turn.Before?.Count ?? 0))
    { name = turn.Before![index].Path; digest = turn.Before[index].Digest; }
    else throw Error("history-file-invalid", "Invalid saved file version.");
    var directory = Path.Combine(_temporaryRoot, "consultation", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var copy = Path.Combine(directory, SafeName(name));
    await using (var output = File.Create(copy)) await content.CopyAsync(Space(workspace, ""), digest, output, token);
    return copy;
  }

  public async Task OpenAsync(string fileId, CancellationToken token)
  {
    if (!OperatingSystem.IsWindows())
      throw Error("history-open-platform", "Use Download to consult this file on this platform.");
    var copy = await MaterializeAsync(fileId, token);
    // Only this disposable consultation copy is passed to the default application.
    Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
  }

  public async Task<object> CompactAsync(HostFileOperationRequest request, CancellationToken token)
  {
    var workspace = await ActiveAsync(token);
    var operation = HistoryContentStore.RequireId(request.OperationId);
    TrackOperation(operation, workspace.Id);
    await _gate.WaitAsync(token);
    var clock = Stopwatch.StartNew();
    try
    {
      if (_activeCaptures.Keys.Any(key => key.StartsWith($"{workspace.Id}/", StringComparison.Ordinal)))
        throw Error("history-operation-active", "Wait for workspace execution to settle before compacting history.");
      _progress[operation] = new(operation, "Enumerating archived history", "running", null, 0, null, 0, []);
      var archived = (await sessions.ReadAllMetadataAsync(workspace.Id, token)).Where(item => item.Archived).ToArray();
      var turns = await ReadTurnsAsync(workspace, "", null, token);
      var ids = archived.Select(item => item.Id).ToHashSet();
      var digests = turns.Where(turn => ids.Contains(turn.SessionId))
        .SelectMany(turn => turn.Attachments.Select(item => item.Digest)
          .Concat((turn.Before ?? []).Select(item => item.Digest)).Concat((turn.After ?? []).Select(item => item.Digest))).Distinct().ToArray();
      var files = digests.Select(id => HistoryContentStore.BlobPath(Space(workspace, ""), id)).Where(File.Exists)
        .Concat(turns.Where(turn => ids.Contains(turn.SessionId))
          .Select(turn => TurnPath(Space(workspace, ""), turn.SessionId, turn.TurnId)).Where(File.Exists)).ToArray();
      var total = files.Sum(path => new FileInfo(path).Length * 3)
        + archived.Where(item => !item.Compressed).Sum(item => item.RecordBytes * 3);
      long done = 0, saved = 0;
      var timings = new List<HostFileTiming>();
      void Completed(HostFileTiming timing)
      {
        var index = timings.FindIndex(item => item.Action == timing.Action);
        if (index < 0) timings.Add(timing);
        else timings[index] = timings[index] with
        {
          ElapsedMilliseconds = timings[index].ElapsedMilliseconds + timing.ElapsedMilliseconds,
          Bytes = timings[index].Bytes + timing.Bytes
        };
      }
      void Advanced(long count, string action)
      {
        done += count;
        _progress[operation] = new(operation, action, "running", total > 0 ? Math.Min(99.9, 100d * done / total) : null,
          done, total, clock.ElapsedMilliseconds, timings.ToArray());
      }
      foreach (var session in archived)
      {
        saved += await sessions.CompressAsync(workspace.Id, session.Id, token, Advanced, Completed);
      }
      foreach (var path in files)
      {
        var before = new FileInfo(path).Length;
        var compressed = await content.CompressAsync(path, token, Advanced, Completed);
        if (compressed) saved += before - new FileInfo(path + ".gz").Length;
      }
      _progress[operation] = new(operation, "History compacted", "completed", 100, done, total, clock.ElapsedMilliseconds, timings.ToArray());
      await HistoryContentStore.WriteJsonAsync(Path.Combine(Space(workspace, ""), "operations", operation + ".json"),
        _progress[operation], token);
      content.InvalidateSize(content.Namespace(workspace.Id));
      return new { savedBytes = saved, sessions = archived.Length, progress = _progress[operation] };
    }
    catch (Exception exception)
    {
      _progress[operation] = new(operation, exception.Message, "failed", null, 0, null, clock.ElapsedMilliseconds, []);
      throw;
    }
    finally { _gate.Release(); }
  }

  public async Task ReleaseWindowAsync(string window, CancellationToken token)
  {
    HistoryContentStore.RequireId(window);
    await _gate.WaitAsync(token);
    try
    {
      if (_activeCaptures.Keys.Any(key => key.Split('/')[1] == window))
      {
        // Producer settlement owns the active snapshot; release will be retried on settlement.
        _releasedWindows[window] = 0;
        return;
      }
      foreach (var key in _temporary.Keys.Where(key => key.Split('/')[1] == window)) _temporary.TryRemove(key, out _);
      foreach (var key in _contextOnly.Keys.Where(key => key.Split('/')[1] == window)) _contextOnly.TryRemove(key, out _);
      var path = Path.Combine(_temporaryRoot, window);
      if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    finally { _gate.Release(); }
  }

  public async Task CollectAsync(string workspaceId, CancellationToken token)
  {
    await _gate.WaitAsync(token);
    try
    {
      content.InvalidateSize(content.Namespace(workspaceId));
      var space = content.Namespace(workspaceId);
      var existing = (await sessions.ReadAllMetadataAsync(workspaceId, token)).Select(item => item.Id).ToHashSet();
      var directory = Path.Combine(space, "turns");
      if (Directory.Exists(directory))
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
          var id = Path.GetFileName(folder);
          var recordPath = Path.Combine(content.HostDataDirectory, sessions.GetRelativePath(workspaceId, id));
          if (File.Exists(recordPath) || File.Exists(recordPath + ".gz")) existing.Add(id);
        }
      if (existing.Count == 0 && !_activeCaptures.Keys.Any(key => key.StartsWith(workspaceId + "/", StringComparison.Ordinal)))
      {
        if (Directory.Exists(space)) Directory.Delete(space, true);
        return;
      }
      if (!Directory.Exists(directory)) return;
      var retained = new HashSet<string>();
      foreach (var folder in Directory.EnumerateDirectories(directory))
      {
        if (!existing.Contains(Path.GetFileName(folder))) { Directory.Delete(folder, true); continue; }
        HashSet<string?>? currentTurns = null;
        try
        {
          var session = await sessions.ReadAsync(workspaceId, Path.GetFileName(folder), token);
          currentTurns = (session?.Messages ?? []).Where(item => item.Role == "user").Select(item => item.TurnId).ToHashSet();
        }
        catch (WorkspaceProfileException exception) when (exception.Code == "session-file-invalid")
        { logger.LogWarning(exception, "Keeping immutable file references for an unreadable conversation."); }
        foreach (var path in ManifestPaths(folder))
        {
          HistoryTurn? turn;
          await using (var input = OpenManifest(path))
            turn = await JsonSerializer.DeserializeAsync<HistoryTurn>(input, HistoryContentStore.JsonOptions, token);
          if (turn is null) continue;
          if (currentTurns is not null && !currentTurns.Contains(turn.TurnId)) { File.Delete(path); continue; }
          retained.UnionWith(turn.Attachments.Select(item => item.Digest));
          retained.UnionWith((turn.Before ?? []).Select(item => item.Digest));
          retained.UnionWith((turn.After ?? []).Select(item => item.Digest));
        }
      }
      var blobs = Path.Combine(space, "blobs");
      if (Directory.Exists(blobs))
        foreach (var path in Directory.EnumerateFiles(blobs))
          if (!retained.Contains(Path.GetFileName(path).Replace(".gz", "", StringComparison.Ordinal))) File.Delete(path);
    }
    finally { content.InvalidateSize(content.Namespace(workspaceId)); _gate.Release(); }
  }

  public static string SafeName(string name)
  {
    name = Path.GetFileName(name.Replace('\\', '/'));
    foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
    name = name.TrimEnd(' ', '.');
    if (string.IsNullOrWhiteSpace(name)) return "saved-file";
    var stem = Path.GetFileNameWithoutExtension(name);
    if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
      name = "saved-" + name;
    return name;
  }
  private static IEnumerable<string> ManifestPaths(string directory) =>
    Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
      .Concat(Directory.EnumerateFiles(directory, "*.json.gz", SearchOption.AllDirectories))
      .DistinctBy(path => path.EndsWith(".gz", StringComparison.Ordinal) ? path[..^3] : path);
  private static Stream OpenManifest(string path) => path.EndsWith(".gz", StringComparison.Ordinal)
    ? new GZipStream(File.OpenRead(path), CompressionMode.Decompress) : File.OpenRead(path);
  public void Dispose()
  {
    try { if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, true); }
    catch (IOException exception) { logger.LogWarning(exception, "Could not clean temporary history copies."); }
    catch (UnauthorizedAccessException exception) { logger.LogWarning(exception, "Could not clean temporary history copies."); }
    _gate.Dispose();
  }
  private static WorkspaceProfileException Error(string code, string message) => new(code, "history-files", message, true);
}
