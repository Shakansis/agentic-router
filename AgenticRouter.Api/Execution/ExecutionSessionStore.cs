using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.GitDelivery;

namespace AgenticRouter.Api.Execution;

public interface IExecutionSessionStore
{
  ExecutionSession Begin(
    string browserSessionId,
    string requestId,
    string objective,
    string approvalPolicy,
    string workspacePath,
    string selectedModel,
    string coordinatorModel,
    string executionPath,
    ExecutionSettings limits
  );

  ExecutionSession? Get(
    string executionSessionId
  );

  ExecutionSession? GetActive(
    string browserSessionId
  );

  bool HasActiveSession();

  ExecutionSessionPersistenceSnapshot? CapturePersistenceSnapshot(
    string executionSessionId
  );

  ExecutionSessionReview RestorePersistenceSnapshot(
    ExecutionSessionPersistenceSnapshot snapshot,
    string browserSessionId,
    string workspacePath,
    ExecutionSettings limits
  );

  ExecutionSessionSummary? GetSummary(
    string executionSessionId
  );

  ExecutionSessionReview? GetReview(
    string executionSessionId
  );

  ExecutionSessionReview? GetLatestReview(
    string workspacePath
  );

  Task<UndoExecutionResponse> UndoAsync(
    string executionSessionId,
    string browserSessionId,
    bool confirmed,
    CancellationToken cancellationToken
  );
}

public sealed class ExecutionSessionStore : IExecutionSessionStore
{
  private const int MaximumRetainedSessions = 50;

  private readonly object _gate = new();
  private readonly Dictionary<string, ExecutionSession> _sessions = new(
    StringComparer.Ordinal
  );
  private readonly Dictionary<string, string> _activeByBrowser = new(
    StringComparer.Ordinal
  );
  private readonly Queue<string> _retentionOrder = new();

  public ExecutionSession Begin(
    string browserSessionId,
    string requestId,
    string objective,
    string approvalPolicy,
    string workspacePath,
    string selectedModel,
    string coordinatorModel,
    string executionPath,
    ExecutionSettings limits
  )
  {
    if (string.IsNullOrWhiteSpace(
      browserSessionId
    ) || browserSessionId.Length > 128)
    {
      throw new LocalActionException(
        "execution-session",
        "Execute mode requires a valid browser session identifier."
      );
    }

    var session = new ExecutionSession(
      Guid.NewGuid().ToString(
        "N"
      ),
      browserSessionId,
      requestId,
      objective,
      approvalPolicy,
      Path.GetFullPath(
        workspacePath
      ),
      selectedModel,
      coordinatorModel,
      executionPath,
      limits
    );

    lock (_gate)
    {
      if (
        _activeByBrowser.TryGetValue(
          browserSessionId,
          out var previousId
        )
        && _sessions.TryGetValue(
          previousId,
          out var previous
        )
        && previous.IsActive
      )
      {
        previous.Complete(
          "cancelled",
          "A newer Execute request replaced this browser session."
        );
      }

      _sessions[session.Id] = session;
      _activeByBrowser[browserSessionId] = session.Id;
      _retentionOrder.Enqueue(
        session.Id
      );
      Trim();
    }

    return session;
  }

  public ExecutionSession? Get(
    string executionSessionId
  )
  {
    lock (_gate)
    {
      return _sessions.GetValueOrDefault(
        executionSessionId
      );
    }
  }

  public ExecutionSession? GetActive(
    string browserSessionId
  )
  {
    lock (_gate)
    {
      return _activeByBrowser.TryGetValue(
        browserSessionId,
        out var id
      )
        ? _sessions.GetValueOrDefault(
          id
        )
        : null;
    }
  }

  public bool HasActiveSession()
  {
    lock (_gate)
    {
      return _sessions.Values.Any(
        session => session.IsActive
      );
    }
  }

  public ExecutionSessionPersistenceSnapshot? CapturePersistenceSnapshot(
    string executionSessionId
  )
  {
    return Get(
      executionSessionId
    )?.CreatePersistenceSnapshot();
  }

  public ExecutionSessionReview RestorePersistenceSnapshot(
    ExecutionSessionPersistenceSnapshot snapshot,
    string browserSessionId,
    string workspacePath,
    ExecutionSettings limits
  )
  {
    var session = new ExecutionSession(
      snapshot.Id,
      browserSessionId,
      Guid.NewGuid().ToString(
        "N"
      ),
      snapshot.Objective,
      "ask",
      Path.GetFullPath(
        workspacePath
      ),
      snapshot.SelectedModel,
      snapshot.CoordinatorModel,
      snapshot.ExecutionPath,
      limits
    );
    session.RestorePersistenceSnapshot(
      snapshot
    );

    lock (_gate)
    {
      _sessions[session.Id] = session;
      _activeByBrowser[browserSessionId] = session.Id;
      _retentionOrder.Enqueue(
        session.Id
      );
      Trim();
    }

    return session.CreateReview();
  }

  public ExecutionSessionSummary? GetSummary(
    string executionSessionId
  )
  {
    return Get(
      executionSessionId
    )?.CreateSummary();
  }

  public ExecutionSessionReview? GetReview(
    string executionSessionId
  )
  {
    return Get(
      executionSessionId
    )?.CreateReview();
  }

  public ExecutionSessionReview? GetLatestReview(
    string workspacePath
  )
  {
    var canonical = Path.GetFullPath(
      workspacePath
    );

    lock (_gate)
    {
      foreach (var id in _retentionOrder.Reverse())
      {
        if (
          _sessions.TryGetValue(
            id,
            out var session
          )
          && string.Equals(
            session.WorkspacePath,
            canonical,
            StringComparison.OrdinalIgnoreCase
          )
        )
        {
          return session.CreateReview();
        }
      }
    }

    return null;
  }

  public async Task<UndoExecutionResponse> UndoAsync(
    string executionSessionId,
    string browserSessionId,
    bool confirmed,
    CancellationToken cancellationToken
  )
  {
    var session = Get(
      executionSessionId
    );

    if (session is null)
    {
      return Failure(
        executionSessionId,
        "Execution session was not found."
      );
    }

    if (!confirmed)
    {
      return Failure(
        session.Id,
        "Undo requires explicit confirmation."
      );
    }

    if (!string.Equals(
      session.BrowserSessionId,
      browserSessionId,
      StringComparison.Ordinal
    ))
    {
      return Failure(
        session.Id,
        "This execution session belongs to a different browser session."
      );
    }

    lock (_gate)
    {
      if (
        !_activeByBrowser.TryGetValue(
          browserSessionId,
          out var latestId
        )
        || !string.Equals(
          latestId,
          executionSessionId,
          StringComparison.Ordinal
        )
      )
      {
        return Failure(
          session.Id,
          "Only the latest execution session in this browser can be undone."
        );
      }
    }

    var preparation = session.PrepareUndo();

    if (!preparation.Allowed)
    {
      return Failure(
        session.Id,
        preparation.Diagnostic
          ?? "This execution session cannot be undone."
      );
    }

    var changes = preparation.Changes;
    var conflicts = new List<string>();

    foreach (var change in changes)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        var path = ResolveSessionPath(
          session,
          change.RelativePath
        );

        if (change.Operation is "deleted" or "deleted-directory")
        {
          if (File.Exists(path) || Directory.Exists(path))
          {
            conflicts.Add(
              $"{change.RelativePath}: the deleted path was recreated after the execution session."
            );
          }

          continue;
        }

        if (!File.Exists(path))
        {
          conflicts.Add(
            $"{change.RelativePath}: the current file no longer exists."
          );
          continue;
        }

        var currentHash = await HashFileAsync(
          path,
          cancellationToken
        );

        if (!string.Equals(
          currentHash,
          change.FinalHash,
          StringComparison.Ordinal
        ))
        {
          conflicts.Add(
            $"{change.RelativePath}: the file changed after the execution session."
          );
        }
      }
      catch (Exception exception) when (
        exception is IOException
        or UnauthorizedAccessException
      )
      {
        conflicts.Add(
          $"{change.RelativePath}: {exception.Message}"
        );
      }
    }

    if (conflicts.Count > 0)
    {
      session.CancelUndo(
        string.Join(
          " ",
          conflicts
        )
      );
      return new UndoExecutionResponse(
        false,
        session.Id,
        "Undo was cancelled before changing any file because conflicts were detected.",
        [],
        conflicts
      );
    }

    var restored = new List<string>();
    var warnings = new List<string>();
    var stagedCreatedFiles = new List<(string Path, string TemporaryPath)>();
    var restoredModifiedFiles = new List<ExecutionFileChange>();
    var restoredDeletedDirectories = new List<string>();

    try
    {
      foreach (var change in changes.Where(
        item => !item.ExistedBefore
      ))
      {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveSessionPath(
          session,
          change.RelativePath
        );
        var temporaryPath = $"{path}.agentic-router-undo-{Guid.NewGuid():N}.tmp";
        File.Move(
          path,
          temporaryPath
        );
        stagedCreatedFiles.Add(
          (
            path,
            temporaryPath
          )
        );
      }

      foreach (var change in changes
        .Where(item => item.Operation == "deleted-directory")
        .OrderBy(item => PathDepth(item.RelativePath)))
      {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveSessionPath(session, change.RelativePath);
        Directory.CreateDirectory(path);
        restoredDeletedDirectories.Add(path);
        restored.Add(change.RelativePath);
      }

      foreach (var change in changes.Where(
        item => item.ExistedBefore && item.Operation != "deleted-directory"
      ))
      {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveSessionPath(
          session,
          change.RelativePath
        );
        if (change.OriginalBinaryBase64 is not null)
        {
          await WriteBytesAtomicallyAsync(
            path,
            Convert.FromBase64String(change.OriginalBinaryBase64),
            cancellationToken
          );
        }
        else
        {
          var originalContent = change.OriginalContent
            ?? throw new IOException(
              $"Undo content is unavailable for {change.RelativePath}."
            );
          await WriteAtomicallyAsync(
            path,
            originalContent,
            cancellationToken
          );
        }
        var restoredHash = await HashFileAsync(
          path,
          cancellationToken
        );

        if (!string.Equals(
          restoredHash,
          change.OriginalHash,
          StringComparison.Ordinal
        ))
        {
          throw new IOException(
            $"Undo verification failed for {change.RelativePath}."
          );
        }

        restoredModifiedFiles.Add(
          change
        );
        restored.Add(
          change.RelativePath
        );
      }

      foreach (var staged in stagedCreatedFiles)
      {
        restored.Add(
          Path.GetRelativePath(
            session.WorkspacePath,
            staged.Path
          )
        );
      }

      foreach (var directory in session.CreatedDirectories.OrderByDescending(
        path => path.Length
      ))
      {
        var fullPath = ResolveSessionPath(
          session,
          directory
        );

        if (
          Directory.Exists(
            fullPath
          )
          && !Directory.EnumerateFileSystemEntries(
            fullPath
          ).Any()
        )
        {
          Directory.Delete(
            fullPath
          );
        }
        else if (Directory.Exists(
          fullPath
        ))
        {
          warnings.Add(
            $"{directory}: directory was left in place because it is not empty."
          );
        }
      }

      foreach (var staged in stagedCreatedFiles)
      {
        try
        {
          File.Delete(
            staged.TemporaryPath
          );
        }
        catch (Exception exception) when (
          exception is IOException
          or UnauthorizedAccessException
        )
        {
          warnings.Add(
            $"{Path.GetFileName(staged.TemporaryPath)}: temporary undo backup could not be removed: {exception.Message}"
          );
        }
      }

      session.CompleteUndo(
        warnings
      );
      return new UndoExecutionResponse(
        true,
        session.Id,
        "The latest eligible execution changes were undone.",
        restored,
        warnings
      );
    }
    catch (Exception exception)
    {
      foreach (var change in restoredModifiedFiles)
      {
        try
        {
          var path = ResolveSessionPath(
            session,
            change.RelativePath
          );
          if (change.Operation == "deleted")
          {
            File.Delete(path);
          }
          else
          {
            await WriteAtomicallyAsync(
              path,
              change.FinalContent,
              CancellationToken.None
            );
          }
        }
        catch
        {
          warnings.Add(
            $"{change.RelativePath}: rollback of the failed undo could not be verified."
          );
        }
      }

      foreach (var staged in stagedCreatedFiles)
      {
        try
        {
          if (File.Exists(
            staged.TemporaryPath
          ))
          {
            File.Move(
              staged.TemporaryPath,
              staged.Path
            );
          }
        }
        catch
        {
          warnings.Add(
            $"{Path.GetFileName(staged.Path)}: recovery after the failed undo was not completed."
          );
        }
      }

      foreach (var directory in restoredDeletedDirectories.OrderByDescending(PathDepth))
      {
        try
        {
          if (Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
          {
            Directory.Delete(directory);
          }
        }
        catch
        {
          warnings.Add(
            $"{Path.GetFileName(directory)}: recovery after the failed undo could not remove a restored directory."
          );
        }
      }

      session.CancelUndo(
        exception.Message
      );
      warnings.Insert(
        0,
        exception.Message
      );
      return new UndoExecutionResponse(
        false,
        session.Id,
        "Undo failed and the application attempted to restore the pre-undo state.",
        [],
        warnings
      );
    }
  }

  private static int PathDepth(string path)
  {
    return path.Count(character => character is '/' or '\\');
  }

  private static UndoExecutionResponse Failure(
    string sessionId,
    string message
  )
  {
    return new UndoExecutionResponse(
      false,
      sessionId,
      message,
      [],
      []
    );
  }

  private static string ResolveSessionPath(
    ExecutionSession session,
    string relativePath
  )
  {
    if (
      !Directory.Exists(
        session.WorkspacePath
      )
      || (
        File.GetAttributes(
          session.WorkspacePath
        )
        & FileAttributes.ReparsePoint
      ) != 0
    )
    {
      throw new IOException(
        "The original trusted workspace is unavailable or is now a reparse point."
      );
    }

    var candidate = Path.GetFullPath(
      Path.Combine(
        session.WorkspacePath,
        relativePath
      )
    );
    var relative = Path.GetRelativePath(
      session.WorkspacePath,
      candidate
    );

    if (
      Path.IsPathFullyQualified(
        relative
      )
      || relative == ".."
      || relative.StartsWith(
        $"..{Path.DirectorySeparatorChar}",
        StringComparison.Ordinal
      )
    )
    {
      throw new IOException(
        "Undo path escaped the original trusted workspace."
      );
    }

    var current = session.WorkspacePath;

    foreach (var segment in relative.Split(
      [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      ],
      StringSplitOptions.RemoveEmptyEntries
    ))
    {
      current = Path.Combine(
        current,
        segment
      );

      if (
        (
          File.Exists(
            current
          )
          || Directory.Exists(
            current
          )
        )
        && (
          File.GetAttributes(
            current
          )
          & FileAttributes.ReparsePoint
        ) != 0
      )
      {
        throw new IOException(
          "Undo paths containing reparse points are not allowed."
        );
      }
    }

    return candidate;
  }

  private static async Task<string> HashFileAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    await using var stream = new FileStream(
      path,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      65_536,
      FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    var hash = await SHA256.HashDataAsync(
      stream,
      cancellationToken
    );
    return Convert.ToHexString(
      hash
    ).ToLowerInvariant();
  }

  private static async Task WriteAtomicallyAsync(
    string path,
    string content,
    CancellationToken cancellationToken
  )
  {
    var directory = Path.GetDirectoryName(
      path
    ) ?? throw new IOException(
      "The file has no parent directory."
    );
    Directory.CreateDirectory(
      directory
    );
    var temporaryPath = Path.Combine(
      directory,
      $".agentic-router-undo-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      await File.WriteAllTextAsync(
        temporaryPath,
        content,
        new UTF8Encoding(
          false
        ),
        cancellationToken
      );
      File.Move(
        temporaryPath,
        path,
        true
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
  }

  private static async Task WriteBytesAtomicallyAsync(
    string path,
    byte[] content,
    CancellationToken cancellationToken
  )
  {
    var directory = Path.GetDirectoryName(path) ?? throw new IOException(
      "The file has no parent directory."
    );
    Directory.CreateDirectory(directory);
    var temporaryPath = Path.Combine(
      directory,
      $".agentic-router-undo-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      await File.WriteAllBytesAsync(
        temporaryPath,
        content,
        cancellationToken
      );
      File.Move(
        temporaryPath,
        path,
        true
      );
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private void Trim()
  {
    var attempts = _retentionOrder.Count;

    while (
      _sessions.Count > MaximumRetainedSessions
      && attempts-- > 0
    )
    {
      var id = _retentionOrder.Dequeue();

      if (
        _sessions.TryGetValue(
          id,
          out var session
        )
        && !session.IsActive
      )
      {
        _sessions.Remove(
          id
        );
        if (
          _activeByBrowser.TryGetValue(
            session.BrowserSessionId,
            out var activeId
          )
          && string.Equals(
            activeId,
            id,
            StringComparison.Ordinal
          )
        )
        {
          _activeByBrowser.Remove(
            session.BrowserSessionId
          );
        }
      }
      else
      {
        _retentionOrder.Enqueue(
          id
        );
      }
    }
  }
}

public sealed class ExecutionSession
{
  private readonly object _gate = new();
  private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
  private readonly List<ExecutionActionRecord> _actions = [];
  private readonly List<ExecutionFileChange> _files = [];
  private readonly List<ExecutionProcessReview> _processes = [];
  private readonly List<string> _warnings = [];
  private readonly List<ToolNameResolutionEvidence> _toolNameResolutions = [];
  private readonly Dictionary<string, PlanActionBinding> _planActionBindings = new(
    StringComparer.Ordinal
  );
  private readonly Dictionary<string, int> _verifiedEffectCounts = new(
    StringComparer.Ordinal
  );
  private readonly HashSet<string> _appliedInstructions = new(
    StringComparer.OrdinalIgnoreCase
  );
  private readonly Dictionary<string, ObservedFileView> _observedFiles = new(
    StringComparer.OrdinalIgnoreCase
  );
  private readonly List<FileConflictView> _conflicts = [];
  private readonly HashSet<string> _createdDirectories = new(
    StringComparer.OrdinalIgnoreCase
  );
  private readonly CancellationTokenSource _cancellation = new();
  private bool _undoInProgress;
  private bool _undoCompleted;
  private string? _undoDiagnostic;
  private long _rollbackBytes;
  private ProjectProfile? _project;
  private WorkspaceBaselineView? _baseline;
  private ExecutionPlanView? _plan;
  private ValidationProfileSettings? _validationProfile;
  private ValidationRunView? _validation;
  private GitDeliveryStateView? _delivery;
  private string? _deliveryCommitHash;
  private string _completionStatus = "not-evaluated";
  private string? _forcedCompletionStatus;
  private ExecutionRoutingEvidence? _routingEvidence;

  public ExecutionSession(
    string id,
    string browserSessionId,
    string requestId,
    string objective,
    string approvalPolicy,
    string workspacePath,
    string selectedModel,
    string coordinatorModel,
    string executionPath,
    ExecutionSettings limits
  )
  {
    Id = id;
    BrowserSessionId = browserSessionId;
    RequestId = requestId;
    Objective = objective;
    ApprovalPolicy = approvalPolicy;
    WorkspacePath = workspacePath;
    SelectedModel = selectedModel;
    CoordinatorModel = coordinatorModel;
    ExecutionPath = executionPath;
    Limits = limits;
    StartedAt = DateTimeOffset.UtcNow;
  }

  public string Id { get; }

  public string BrowserSessionId { get; }

  public string RequestId { get; }

  public string Objective { get; }

  public string ApprovalPolicy { get; }

  public string WorkspacePath { get; }

  public string SelectedModel { get; }

  public ExecutionSettings Limits { get; }

  public DateTimeOffset StartedAt { get; }

  public DateTimeOffset? CompletedAt { get; private set; }

  public CancellationToken CancellationToken => _cancellation.Token;

  public string CoordinatorModel { get; private set; }

  public string ExecutionPath { get; private set; }

  public string? ResidentModel { get; private set; }

  public string? ConformanceIdentity { get; private set; }

  public string? HandoffReason { get; private set; }

  public string State { get; private set; } = "running";

  public int PlanningFailureCount { get; private set; }

  public int ConsecutiveToolFailureCount { get; private set; }

  public int HandoffCount { get; private set; }

  public int CompletedActionCount
  {
    get
    {
      lock (_gate)
      {
        return _actions.Count(
          action => action.State == "completed"
        );
      }
    }
  }

  public bool IsActive
  {
    get
    {
      lock (_gate)
      {
        return State == "running";
      }
    }
  }

  public bool HasWarnings
  {
    get
    {
      lock (_gate)
      {
        return _warnings.Count > 0;
      }
    }
  }

  public ExecutionPlanView? Plan
  {
    get
    {
      lock (_gate)
      {
        return _plan;
      }
    }
  }

  public bool RequiresMutation
  {
    get
    {
      lock (_gate)
      {
        return RequiresMutationUnsafe();
      }
    }
  }

  public bool HasVerifiedMutation
  {
    get
    {
      lock (_gate)
      {
        return _verifiedEffectCounts.Keys.Any(
          effect => ToolEffectRegistry.IsMutation(effect)
            && EffectCount(effect) > 0
        );
      }
    }
  }

  public bool HasReviewedChangedFiles
  {
    get
    {
      lock (_gate)
      {
        var latestChanges = LatestVerifiedChangedFilesUnsafe();
        return latestChanges.Length == 0
          ? _files.Any(
            file => file.Verified && file.Operation.StartsWith("deleted", StringComparison.Ordinal)
          )
          : UnreviewedChangedFilesUnsafe(latestChanges).Length == 0;
      }
    }
  }

  public IReadOnlyList<string> UnreviewedChangedFiles
  {
    get
    {
      lock (_gate)
      {
        return UnreviewedChangedFilesUnsafe(
          LatestVerifiedChangedFilesUnsafe()
        );
      }
    }
  }

  public IReadOnlyList<string> UnresolvedChangedFileReferences
  {
    get
    {
      lock (_gate)
      {
        return UnresolvedChangedFileReferencesUnsafe(
          LatestVerifiedChangedFilesUnsafe()
        );
      }
    }
  }

  public IReadOnlyList<string> StaticCompletionIssues
  {
    get
    {
      lock (_gate)
      {
        return StaticCompletionIssuesUnsafe(
          LatestVerifiedChangedFilesUnsafe()
        );
      }
    }
  }

  public bool HasVerifiedChangedFiles
  {
    get
    {
      lock (_gate)
      {
        return _files.Any(
          file => file.Verified
        );
      }
    }
  }

  public bool HasPendingMutationPlanStep
  {
    get
    {
      lock (_gate)
      {
        return _planActionBindings.Values.Any(
          binding => ToolEffectRegistry.IsMutation(binding.ExpectedEffect)
            && _plan?.Steps.Any(
              step => step.Id == binding.StepId
                && step.Status is "pending" or "in-progress"
            ) == true
        );
      }
    }
  }

  public bool CanCompletePlan()
  {
    lock (_gate)
    {
      return _plan is not null
        && _plan.Steps.Count > 0
        && _plan.Steps.All(step => step.Status == "completed")
        && (!RequiresMutationUnsafe() || HasVerifiedMutationUnsafe());
    }
  }

  public void AttachProject(
    ProjectProfile project
  )
  {
    lock (_gate)
    {
      _project = project;
      _baseline = new WorkspaceBaselineView(
        project.Repository.Branch,
        project.Repository.DirtyPaths,
        DateTimeOffset.UtcNow,
        project.Repository.IsGitRepository,
        project.Status == "partial"
          ? project.Diagnostic
          : null
      );
    }
  }

  public void ApplyInstructions(
    RepositoryInstructionSet instructions
  )
  {
    lock (_gate)
    {
      foreach (var path in instructions.AppliedFiles)
      {
        _appliedInstructions.Add(
          path
        );
      }

      if (!string.IsNullOrWhiteSpace(
        instructions.Diagnostic
      ))
      {
        _warnings.Add(
          instructions.Diagnostic
        );
      }
    }
  }

  public void CreatePlan(
    ExecutionPlanView plan
  )
  {
    lock (_gate)
    {
      if (_plan is not null)
      {
        throw new LocalActionException(
          "execution-plan",
          "This execution session already has a plan."
        );
      }

      _plan = plan;
    }
  }

  public void RevisePlan(
    ExecutionPlanView plan
  )
  {
    lock (_gate)
    {
      _plan = plan;
    }
  }

  public bool RecordPlanActionStarted(
    string actionId,
    string tool,
    string? targetPath = null,
    string? stepId = null
  )
  {
    lock (_gate)
    {
      if (_plan is null)
      {
        return false;
      }

      var effect = ToolEffectRegistry.ForTool(tool);

      if (effect is null)
      {
        return false;
      }

      var bindingFailure = ValidatePlanStepBindingUnsafe(
        stepId
      );
      if (bindingFailure is not null)
      {
        throw new LocalActionException(
          "plan-action-binding",
          bindingFailure
        );
      }

      var steps = _plan.Steps.ToArray();
      var index = Array.FindIndex(
        steps,
        step => string.Equals(
          step.Id,
          stepId,
          StringComparison.Ordinal
        )
      );
      _planActionBindings[actionId] = new PlanActionBinding(
        steps[index].Id,
        effect,
        EffectCount(effect)
      );
      steps[index] = steps[index] with
      {
        Status = "in-progress"
      };
      _plan = _plan with
      {
        Steps = steps,
        CurrentStepId = steps[index].Id
      };
      return true;
    }
  }

  public string? ValidatePlanStepBinding(
    string? stepId
  )
  {
    lock (_gate)
    {
      return ValidatePlanStepBindingUnsafe(
        stepId
      );
    }
  }

  private string? ValidatePlanStepBindingUnsafe(
    string? stepId
  )
  {
    if (_plan is null)
    {
      return stepId is null
        ? null
        : "An action cannot bind to a plan step because this execution has no accepted plan.";
    }
    if (string.IsNullOrWhiteSpace(stepId))
    {
      return "The specialist must bind every action to an exact Host-owned stepId while an accepted plan exists.";
    }
    var step = _plan.Steps.FirstOrDefault(
      candidate => string.Equals(
        candidate.Id,
        stepId,
        StringComparison.Ordinal
      )
    );
    if (step is null)
    {
      return $"Plan step '{stepId}' does not exist in the accepted Host plan.";
    }
    if (step.Status is "completed" or "failed" or "blocked" or "skipped")
    {
      return $"Plan step '{stepId}' is already terminal ({step.Status}).";
    }
    var blockedDependencies = (step.Dependencies ?? []).Where(
      dependency => _plan.Steps.FirstOrDefault(
        candidate => string.Equals(
          candidate.Id,
          dependency,
          StringComparison.Ordinal
        )
      )?.Status != "completed"
    ).ToArray();
    return blockedDependencies.Length == 0
      ? null
      : $"Plan step '{stepId}' is waiting for completed dependencies: {string.Join(", ", blockedDependencies)}.";
  }

  public bool RecordPlanActionResult(
    string actionId,
    string tool,
    string status
  )
  {
    lock (_gate)
    {
      if (_plan is null)
      {
        return false;
      }

      if (!_planActionBindings.TryGetValue(actionId, out var binding))
      {
        return false;
      }

      var index = _plan.Steps.ToList().FindIndex(
        step => string.Equals(step.Id, binding.StepId, StringComparison.Ordinal)
      );

      if (index < 0)
      {
        return false;
      }

      if (
        status == "completed"
        && (
          !ToolEffectRegistry.AreCompatible(
            ToolEffectRegistry.ForTool(tool) ?? string.Empty,
            binding.ExpectedEffect
          )
          || EffectCount(binding.ExpectedEffect) <= binding.EvidenceCountBefore
          || !HasVerifiedEffect(binding.ExpectedEffect)
        )
      )
      {
        return false;
      }

      var steps = _plan.Steps.ToArray();
      steps[index] = steps[index] with
      {
        Status = status
      };
      _plan = _plan with
      {
        Steps = steps,
        CurrentStepId = steps.FirstOrDefault(
          step => step.Status == "in-progress"
        )?.Id,
        CompletedStepCount = steps.Count(
          step => step.Status == "completed"
        )
      };
      _planActionBindings.Remove(actionId);
      return true;
    }
  }

  private static int? CountTopLevelArrayItems(string value)
  {
    var start = value.IndexOf('[');
    if (start < 0)
    {
      return null;
    }

    var arrayDepth = 0;
    var objectDepth = 0;
    var parenthesisDepth = 0;
    var itemCount = 0;
    var hasItemContent = false;
    var inString = false;
    var escaped = false;
    var quote = '\0';

    for (var index = start; index < value.Length; index++)
    {
      var character = value[index];
      if (inString)
      {
        if (escaped)
        {
          escaped = false;
        }
        else if (character == '\\')
        {
          escaped = true;
        }
        else if (character == quote)
        {
          inString = false;
        }
        continue;
      }

      if (character is '\'' or '"' or '`')
      {
        inString = true;
        quote = character;
        if (arrayDepth == 1) hasItemContent = true;
        continue;
      }

      switch (character)
      {
        case '[':
          arrayDepth++;
          if (arrayDepth > 1) hasItemContent = true;
          break;
        case ']':
          if (arrayDepth == 1)
          {
            return hasItemContent
              ? itemCount + 1
              : 0;
          }
          arrayDepth--;
          break;
        case '{':
          if (arrayDepth == 1) hasItemContent = true;
          objectDepth++;
          break;
        case '}':
          objectDepth = Math.Max(0, objectDepth - 1);
          break;
        case '(':
          if (arrayDepth == 1) hasItemContent = true;
          parenthesisDepth++;
          break;
        case ')':
          parenthesisDepth = Math.Max(0, parenthesisDepth - 1);
          break;
        case ',' when arrayDepth == 1 && objectDepth == 0 && parenthesisDepth == 0:
          if (hasItemContent)
          {
            itemCount++;
            hasItemContent = false;
          }
          break;
        default:
          if (arrayDepth == 1 && !char.IsWhiteSpace(character))
          {
            hasItemContent = true;
          }
          break;
      }
    }

    return null;
  }

  public void RecordObservedFile(
    ObservedFileView file
  )
  {
    lock (_gate)
    {
      _observedFiles[file.RelativePath] = file;
    }
  }

  public bool TryGetObservedFile(
    string relativePath,
    out ObservedFileView? file
  )
  {
    lock (_gate)
    {
      var found = _observedFiles.TryGetValue(
        relativePath,
        out var observed
      );
      file = observed;
      return found;
    }
  }

  public void RecordConflict(
    FileConflictView conflict
  )
  {
    lock (_gate)
    {
      _conflicts.Add(
        conflict
      );
      _warnings.Add(
        $"{conflict.RelativePath}: file changed after inspection."
      );
    }
  }

  public void SelectValidationProfile(
    ValidationProfileSettings? profile
  )
  {
    lock (_gate)
    {
      _validationProfile = profile;
    }
  }

  public void RecordValidation(
    ValidationRunView validation
  )
  {
    lock (_gate)
    {
      _validation = validation;
      EvaluateCompletionGate();
    }
  }

  public void RecordDelivery(
    GitDeliveryStateView delivery
  )
  {
    lock (_gate)
    {
      _delivery = delivery;
    }
  }

  public void MarkDeliveryCommitted(
    string commitHash
  )
  {
    lock (_gate)
    {
      _deliveryCommitHash = commitHash;
    }
  }

  public void RefreshCompletionGate()
  {
    lock (_gate)
    {
      EvaluateCompletionGate();
    }
  }

  public IReadOnlyList<string> CreatedDirectories
  {
    get
    {
      lock (_gate)
      {
        return _createdDirectories.ToArray();
      }
    }
  }

  public void RecordPlanningFailure()
  {
    lock (_gate)
    {
      PlanningFailureCount++;
    }
  }

  public void ResetPlanningFailures()
  {
    lock (_gate)
    {
      PlanningFailureCount = 0;
    }
  }

  public void RecordHandoff(
    string coordinatorModel,
    string executionPath
  )
  {
    lock (_gate)
    {
      HandoffCount++;
      CoordinatorModel = coordinatorModel;
      ExecutionPath = executionPath;
      PlanningFailureCount = 0;
      ConsecutiveToolFailureCount = 0;
      _warnings.Add(
        $"Coordination handed off to {coordinatorModel} through {executionPath}."
      );
    }
  }

  public void ResolveCoordinator(
    string coordinatorModel,
    string executionPath
  )
  {
    lock (_gate)
    {
      CoordinatorModel = coordinatorModel;
      ExecutionPath = executionPath;
    }
  }

  public void RecordCoordinationMetadata(
    string residentModel,
    string? conformanceIdentity,
    string? handoffReason
  )
  {
    lock (_gate)
    {
      ResidentModel = residentModel;
      ConformanceIdentity = conformanceIdentity;
      HandoffReason = handoffReason;
    }
  }

  public void RecordRoutingEvidence(ExecutionRoutingEvidence evidence)
  {
    ArgumentNullException.ThrowIfNull(evidence);
    lock (_gate)
    {
      _routingEvidence = evidence;
    }
  }

  public void RecordAction(
    ValidatedLocalAction action,
    string state,
    string? result = null
  )
  {
    lock (_gate)
    {
      var existing = _actions.FindIndex(
        item => item.ActionId == action.ActionId
      );
      var record = new ExecutionActionRecord(
        action.ActionId,
        action.Tool,
        action.Summary,
        state,
        result,
        DateTimeOffset.UtcNow,
        action.OriginalTool,
        action.ToolResolutionSource
      );

      var wasCompleted = existing >= 0 && _actions[existing].State == "completed";

      if (existing >= 0)
      {
        _actions[existing] = record;
      }
      else
      {
        _actions.Add(
          record
        );
      }

      if (state == "completed" && !wasCompleted)
      {
        var effect = ToolEffectRegistry.ForTool(action.Tool);

        if (effect is not null)
        {
          _verifiedEffectCounts[effect] = EffectCount(effect) + 1;
        }
      }
    }
  }

  public void RecordToolNameResolution(
    LocalActionProposal proposal,
    string validationOutcome
  )
  {
    var original = proposal.OriginalTool ?? proposal.Tool;

    if (string.Equals(
      original,
      proposal.Tool,
      StringComparison.Ordinal
    ))
    {
      return;
    }

    lock (_gate)
    {
      _toolNameResolutions.Add(
        new ToolNameResolutionEvidence(
          original,
          proposal.Tool,
          proposal.ToolResolutionSource,
          validationOutcome,
          DateTimeOffset.UtcNow
        )
      );
    }
  }

  public void RecordToolSuccess()
  {
    lock (_gate)
    {
      ConsecutiveToolFailureCount = 0;
      PlanningFailureCount = 0;
    }
  }

  public void RecordToolFailure()
  {
    lock (_gate)
    {
      ConsecutiveToolFailureCount++;
    }
  }

  public void RecordFileChange(
    ExecutionFileChange change
  )
  {
    lock (_gate)
    {
      var existing = _files.FindIndex(
        item => string.Equals(
          item.RelativePath,
          change.RelativePath,
          StringComparison.OrdinalIgnoreCase
        )
      );

      if (existing >= 0)
      {
        var original = _files[existing];
        change = change with
        {
          ExistedBefore = original.ExistedBefore,
          OriginalHash = original.OriginalHash,
          OriginalContent = original.OriginalContent,
          OriginalBinaryBase64 = original.OriginalBinaryBase64,
          RollbackBytes = original.RollbackBytes,
          UndoAvailable = original.UndoAvailable && change.UndoAvailable,
          UndoDiagnostic = original.UndoDiagnostic ?? change.UndoDiagnostic
        };
        _files[existing] = change;
      }
      else
      {
        var preExisting = _baseline?.PreExistingDirtyPaths.Contains(
          change.RelativePath,
          StringComparer.OrdinalIgnoreCase
        ) == true;
        change = change with
        {
          PreExistingChange = preExisting,
          CurrentGitStatus = preExisting
            ? "pre-existing and changed by session"
            : "changed by session"
        };
        _files.Add(
          change
        );
        _rollbackBytes += change.RollbackBytes;
      }

      if (
        !change.UndoAvailable
        && !string.IsNullOrWhiteSpace(
          change.UndoDiagnostic
        )
        && !_warnings.Contains(
          change.UndoDiagnostic,
          StringComparer.Ordinal
        )
      )
      {
        _warnings.Add(
          change.UndoDiagnostic
        );
      }
    }
  }

  public bool CanTrackRollback(
    long originalBytes,
    out string? diagnostic
  )
  {
    lock (_gate)
    {
      if (_files.Count >= Limits.MaxTrackedFilesPerSession)
      {
        diagnostic = "The session file tracking limit was reached.";
        return false;
      }

      if (originalBytes > Limits.MaxRollbackBytesPerFile)
      {
        diagnostic = "The original file is larger than the per-file rollback limit.";
        return false;
      }

      if (_rollbackBytes + originalBytes > Limits.MaxRollbackBytesPerSession)
      {
        diagnostic = "The session rollback byte limit would be exceeded.";
        return false;
      }

      diagnostic = null;
      return true;
    }
  }

  public bool CanTrackRollbackBatch(
    int fileCount,
    long originalBytes,
    out string? diagnostic
  )
  {
    lock (_gate)
    {
      if (_files.Count + fileCount > Limits.MaxTrackedFilesPerSession)
      {
        diagnostic = "The deletion would exceed the session file tracking limit.";
        return false;
      }

      if (_rollbackBytes + originalBytes > Limits.MaxRollbackBytesPerSession)
      {
        diagnostic = "The deletion would exceed the session rollback byte limit.";
        return false;
      }

      diagnostic = null;
      return true;
    }
  }

  public void RecordCreatedDirectory(
    string relativePath
  )
  {
    lock (_gate)
    {
      _createdDirectories.Add(
        relativePath
      );
    }
  }

  public void RecordProcess(
    ExecutionProcessReview process
  )
  {
    lock (_gate)
    {
      _processes.Add(
        process
      );
    }
  }

  public void AddWarning(
    string warning
  )
  {
    lock (_gate)
    {
      if (!_warnings.Contains(
        warning,
        StringComparer.Ordinal
      ))
      {
        _warnings.Add(
          warning
        );
      }
    }
  }

  public void Complete(
    string state,
    string? warning = null
  )
  {
    lock (_gate)
    {
      if (State != "running")
      {
        return;
      }

      State = state;
      EvaluateCompletionGate();
      CompletedAt = DateTimeOffset.UtcNow;
      _stopwatch.Stop();

      if (!string.IsNullOrWhiteSpace(
        warning
      ))
      {
        _warnings.Add(
          warning
        );
      }

      if (state == "cancelled")
      {
        _cancellation.Cancel();
      }
    }
  }

  public void MarkPartialContextExhausted()
  {
    lock (_gate)
    {
      _forcedCompletionStatus = "partial-context-exhausted";
      if (!_warnings.Contains("Coordinator context was exhausted after bounded deterministic compaction.", StringComparer.Ordinal))
      {
        _warnings.Add("Coordinator context was exhausted after bounded deterministic compaction.");
      }
      EvaluateCompletionGate();
    }
  }

  public string CreateCoordinatorStateSummary()
  {
    lock (_gate)
    {
      var objectiveHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Objective))).ToLowerInvariant();
      var plan = _plan is null
        ? "none"
        : string.Join(",", _plan.Steps.Select(step => $"{SafeState(step.Id)}:{SafeState(step.Status)}"));
      var actions = string.Join(",", _actions.Select(action => $"{SafeState(action.ActionId)}:{SafeState(action.OriginalTool ?? action.Tool)}->{SafeState(action.Tool)}:{SafeState(action.State)}:{Encoding.UTF8.GetByteCount(action.Result ?? string.Empty)}"));
      var files = string.Join(",", _files.Select(file => $"{SafeState(file.RelativePath)}:{SafeState(file.Operation)}:{file.FinalSizeBytes}:{SafeState(file.FinalHash)}"));
      return $"objectiveSha256={objectiveHash}\nplan={plan}\nactions={actions}\nfiles={files}\nvalidation={SafeState(_validation?.State ?? "not-run")}\ncompletion={SafeState(_completionStatus)}";
    }
  }

  public ExecutionSessionSummary CreateSummary()
  {
    lock (_gate)
    {
      var undo = EvaluateUndo();
      return new ExecutionSessionSummary(
        Id,
        BrowserSessionId,
        State,
        CoordinatorModel,
        ExecutionPath,
        PlanningFailureCount,
        ConsecutiveToolFailureCount,
        HandoffCount,
        _actions.Count,
        _files.Count,
        _stopwatch.ElapsedMilliseconds,
        _files.Count > 0 || _processes.Count > 0 || _conflicts.Count > 0,
        undo.Allowed,
        undo.Diagnostic,
        _plan,
        _completionStatus,
        _delivery,
        SelectedModel,
        ResidentModel,
        ConformanceIdentity,
        HandoffReason,
        _routingEvidence
      );
    }
  }

  public ExecutionSessionReview CreateReview()
  {
    lock (_gate)
    {
      return new ExecutionSessionReview(
        CreateSummary(),
        Objective,
        WorkspacePath,
        _files.Select(
          item => item.ToReview()
        ).ToArray(),
        _processes.ToArray(),
        _warnings.ToArray(),
        _project,
        _appliedInstructions.ToArray(),
        _baseline,
        _conflicts.ToArray(),
        _validationProfile,
        _validation,
        _delivery,
        _toolNameResolutions.ToArray()
      );
    }
  }

  public ExecutionSessionPersistenceSnapshot CreatePersistenceSnapshot()
  {
    lock (_gate)
    {
      return new ExecutionSessionPersistenceSnapshot(
        Id,
        Objective,
        SelectedModel,
        CoordinatorModel,
        ExecutionPath,
        State,
        _files.ToArray(),
        CreateReview()
      );
    }
  }

  public void RestorePersistenceSnapshot(
    ExecutionSessionPersistenceSnapshot snapshot
  )
  {
    lock (_gate)
    {
      _files.AddRange(
        snapshot.Files
      );
      _processes.AddRange(
        snapshot.Review.Processes
      );
      _warnings.AddRange(
        snapshot.Review.Warnings
      );
      _toolNameResolutions.AddRange(
        snapshot.Review.ToolNameResolutions
          ?? []
      );
      _project = snapshot.Review.Project;
      _baseline = snapshot.Review.Baseline;
      _plan = snapshot.Review.Summary.Plan;
      _validationProfile = snapshot.Review.ValidationProfile;
      _validation = snapshot.Review.Validation;
      _delivery = snapshot.Review.Delivery;
      _deliveryCommitHash = snapshot.Review.Delivery?.CommitHash;
      _conflicts.AddRange(
        snapshot.Review.Conflicts
          ?? []
      );
      _appliedInstructions.UnionWith(
        snapshot.Review.AppliedInstructionFiles
          ?? []
      );
      CoordinatorModel = snapshot.CoordinatorModel;
      ExecutionPath = snapshot.ExecutionPath;
      ResidentModel = snapshot.Review.Summary.ResidentModel;
      ConformanceIdentity = snapshot.Review.Summary.ConformanceIdentity;
      HandoffReason = snapshot.Review.Summary.HandoffReason;
      _routingEvidence = snapshot.Review.Summary.RoutingEvidence;
      State = snapshot.State is "completed" or "completed-with-warnings" or "blocked"
        ? snapshot.State
        : "interrupted";
      _completionStatus = snapshot.Review.Summary.CompletionStatus;
      CompletedAt = DateTimeOffset.UtcNow;
      _stopwatch.Stop();
    }
  }

  public (
    bool Allowed,
    string? Diagnostic,
    IReadOnlyList<ExecutionFileChange> Changes
  ) PrepareUndo()
  {
    lock (_gate)
    {
      var eligibility = EvaluateUndo();

      if (!eligibility.Allowed)
      {
        return (
          false,
          eligibility.Diagnostic,
          []
        );
      }

      _undoInProgress = true;
      return (
        true,
        null,
        _files.ToArray()
      );
    }
  }

  public void CompleteUndo(
    IReadOnlyList<string> warnings
  )
  {
    lock (_gate)
    {
      _undoInProgress = false;
      _undoCompleted = true;
      _warnings.AddRange(
        warnings
      );
    }
  }

  public void CancelUndo(
    string diagnostic
  )
  {
    lock (_gate)
    {
      _undoInProgress = false;
      _undoDiagnostic = diagnostic;
    }
  }

  private (
    bool Allowed,
    string? Diagnostic
  ) EvaluateUndo()
  {
    if (!string.IsNullOrWhiteSpace(
      _deliveryCommitHash
    ))
    {
      return (
        false,
        $"Undo is unavailable because this delivery was committed as {_deliveryCommitHash}. v0.9.12 does not rewrite Git history."
      );
    }

    if (_undoCompleted)
    {
      return (
        false,
        "This execution session was already undone."
      );
    }

    if (_undoInProgress)
    {
      return (
        false,
        "Undo is already in progress."
      );
    }

    if (State is not (
      "completed"
      or "completed-with-warnings"
    ))
    {
      return (
        false,
        "Only a completed execution session can be undone."
      );
    }

    if (_files.Count == 0)
    {
      return (
        false,
        "This execution session did not change any tracked file."
      );
    }

    var unavailable = _files.FirstOrDefault(
      item => !item.UndoAvailable
    );

    if (unavailable is not null)
    {
      return (
        false,
        unavailable.UndoDiagnostic
          ?? $"{unavailable.RelativePath} is not eligible for undo."
      );
    }

    return (
      true,
      _undoDiagnostic
    );
  }

  private int EffectCount(string effect)
  {
    return _verifiedEffectCounts.TryGetValue(effect, out var count)
      ? count
      : 0;
  }

  private bool HasVerifiedEffect(string effect)
  {
    return effect switch
    {
      ToolEffects.FileCreated => _files.Any(file => file.Verified && file.Operation == "created"),
      ToolEffects.FileChanged => _files.Any(file => file.Verified && file.Operation == "modified"),
      ToolEffects.FileDeleted => _files.Any(
        file => file.Verified && file.Operation.StartsWith("deleted", StringComparison.Ordinal)
      ),
      ToolEffects.DirectoryCreated => _createdDirectories.Count > 0,
      ToolEffects.Validated => _validation?.State is "passed" or "passed-with-warnings",
      _ => EffectCount(effect) > 0
    };
  }

  private void EvaluateCompletionGate()
  {
    if (!string.IsNullOrWhiteSpace(_forcedCompletionStatus))
    {
      _completionStatus = _forcedCompletionStatus;
      State = State == "cancelled" ? State : "completed-with-warnings";
      return;
    }

    var turnScope = ExecutionTurnToolPolicy.Resolve(
      Objective,
      _validationProfile is not null
    );
    var validationRequested = !turnScope.ManualValidationRequested && (Objective.Contains(
      "valid",
      StringComparison.OrdinalIgnoreCase
    ) || Objective.Contains(
      "test",
      StringComparison.OrdinalIgnoreCase
    ) || Objective.Contains(
      "build",
      StringComparison.OrdinalIgnoreCase
    ) || Objective.Contains(
      "compil",
      StringComparison.OrdinalIgnoreCase
    ) || _plan?.Steps.Any(
      step => step.Title.Contains(
        "valid",
        StringComparison.OrdinalIgnoreCase
      ) || step.Title.Contains(
        "test",
        StringComparison.OrdinalIgnoreCase
      ) || step.Title.Contains(
        "build",
        StringComparison.OrdinalIgnoreCase
      )
    ) == true);

    if (RequiresMutationUnsafe() && !HasVerifiedMutationUnsafe())
    {
      _completionStatus = "blocked-mutation-not-performed";
      if (State is "completed" or "completed-with-warnings")
      {
        State = "blocked";
      }
    }
    else if (_files.Count > 0)
    {
      _completionStatus = _validation?.State switch
      {
        "passed" => "implemented-and-validated",
        "passed-with-warnings" => "implemented-and-validated-with-warnings",
        "failed" => "implemented-validation-failed",
        "cancelled" => "implemented-validation-cancelled",
        "not-configured" => "implemented-validation-not-configured",
        _ => "implemented-validation-not-run"
      };
    }
    else if (HasVerifiedMutationUnsafe())
    {
      _completionStatus = _validation?.State switch
      {
        "passed" => "implemented-and-validated",
        "passed-with-warnings" => "implemented-and-validated-with-warnings",
        "failed" => "implemented-validation-failed",
        "cancelled" => "implemented-validation-cancelled",
        _ => "verified-mutation-no-file-artifacts"
      };
    }
    else if (_validation?.State is "passed" or "passed-with-warnings")
    {
      _completionStatus = "validation-passed-no-files-changed";
    }
    else
    {
      _completionStatus = validationRequested
        ? _validationProfile is null
          ? "blocked-validation-not-configured"
          : "blocked-validation-not-run"
        : "inspected-no-files-changed";
    }

    if (_completionStatus.StartsWith(
      "blocked-",
      StringComparison.Ordinal
    ) || _completionStatus.Contains(
      "failed",
      StringComparison.Ordinal
    ))
    {
      if (State is "completed" or "completed-with-warnings")
      {
        State = _completionStatus == "blocked-mutation-not-performed"
          ? "blocked"
          : "completed-with-warnings";
      }
    }
  }

  private bool RequiresMutationUnsafe()
  {
    return new[]
    {
      "implement", "change", "edit", "update", "fix", "create", "delete", "remove",
      "alter", "corrig", "criar", "excluir", "apagar", "adicionar", "remover"
    }.Any(fragment => Objective.Contains(fragment, StringComparison.OrdinalIgnoreCase));
  }

  private bool HasVerifiedMutationUnsafe()
  {
    return _verifiedEffectCounts.Any(
      pair => pair.Value > 0 && ToolEffectRegistry.IsMutation(pair.Key)
    );
  }

  private static string SummaryTarget(string summary)
  {
    var separator = summary.IndexOf(": ", StringComparison.Ordinal);
    return separator < 0
      ? summary
      : summary[(separator + 2)..].Replace('\\', '/');
  }

  private ExecutionFileChange[] LatestVerifiedChangedFilesUnsafe()
  {
    return _files.Where(
      file => file.Verified
    ).GroupBy(
      file => file.RelativePath,
      StringComparer.OrdinalIgnoreCase
    ).Select(
      group => group.Last()
    ).Where(
      file => !file.Operation.StartsWith("deleted", StringComparison.Ordinal)
    ).ToArray();
  }

  private string[] UnreviewedChangedFilesUnsafe(
    IReadOnlyList<ExecutionFileChange> latestChanges
  )
  {
    return latestChanges.Where(
      change => !_actions.Any(
        action => action.State == "completed"
          && action.Tool is "read_file" or "get_file_info"
          && action.Timestamp >= change.VerifiedAt
          && string.Equals(
            SummaryTarget(action.Summary),
            change.RelativePath.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase
          )
      )
    ).Select(
      change => change.RelativePath
    ).OrderBy(
      path => path,
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
  }

  private string[] UnresolvedChangedFileReferencesUnsafe(
    IReadOnlyList<ExecutionFileChange> latestChanges
  )
  {
    var root = Path.GetFullPath(WorkspacePath);
    var rootPrefix = root.TrimEnd(
      Path.DirectorySeparatorChar,
      Path.AltDirectorySeparatorChar
    ) + Path.DirectorySeparatorChar;
    var unresolved = new HashSet<string>(
      StringComparer.OrdinalIgnoreCase
    );
    var referencePattern = new System.Text.RegularExpressions.Regex(
      "\\b(?:src|href)\\s*=\\s*[\\\"'](?<path>[^\\\"']+)[\\\"']",
      System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );

    foreach (var change in latestChanges.Where(
      file => Path.GetExtension(file.RelativePath) is ".html" or ".htm"
    ))
    {
      var sourceDirectory = Path.GetDirectoryName(
        change.RelativePath.Replace(
          '/',
          Path.DirectorySeparatorChar
        )
      ) ?? string.Empty;
      foreach (System.Text.RegularExpressions.Match match in referencePattern.Matches(
        change.FinalContent
      ))
      {
        var reference = match.Groups["path"].Value.Trim();
        if (
          reference.Length == 0
          || reference.StartsWith('#')
          || reference.StartsWith("//", StringComparison.Ordinal)
          || Uri.TryCreate(reference, UriKind.Absolute, out _)
        )
        {
          continue;
        }

        var suffixIndex = reference.IndexOfAny(['?', '#']);
        var pathOnly = suffixIndex < 0
          ? reference
          : reference[..suffixIndex];
        var extension = Path.GetExtension(pathOnly);
        if (extension is not ".js" and not ".css" and not ".html" and not ".htm" and not ".json")
        {
          continue;
        }

        try
        {
          var candidate = Path.GetFullPath(
            Path.Combine(
              root,
              sourceDirectory,
              pathOnly.Replace(
                '/',
                Path.DirectorySeparatorChar
              )
            )
          );
          if (
            !string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
          )
          {
            unresolved.Add(reference);
            continue;
          }

          if (!File.Exists(candidate))
          {
            unresolved.Add(
              Path.GetRelativePath(root, candidate).Replace(
                Path.DirectorySeparatorChar,
                '/'
              )
            );
          }
        }
        catch (Exception exception) when (
          exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
        )
        {
          unresolved.Add(reference);
        }
      }
    }

    return unresolved.OrderBy(
      path => path,
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
  }

  private string[] StaticCompletionIssuesUnsafe(
    IReadOnlyList<ExecutionFileChange> latestChanges
  )
  {
    var issues = new List<string>();
    var expectedMatch = System.Text.RegularExpressions.Regex.Match(
      Objective,
      @"\b(?:collection|list|array|colecao|coleção|lista)\b[^\d]{0,32}(?:of\s+)?(?<count>\d+)\b",
      System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
    );
    if (
      expectedMatch.Success
      && int.TryParse(expectedMatch.Groups["count"].Value, out var expectedCount)
    )
    {
      var candidates = latestChanges.Where(
        file => Path.GetExtension(file.RelativePath).Equals(".js", StringComparison.OrdinalIgnoreCase)
          || Path.GetExtension(file.RelativePath).Equals(".json", StringComparison.OrdinalIgnoreCase)
      ).Select(
        file => new
        {
          file.RelativePath,
          file.FinalContent,
          Count = CountTopLevelArrayItems(file.FinalContent)
        }
      ).Where(candidate => candidate.Count is not null).OrderByDescending(
        candidate => candidate.Count
      ).ToArray();
      var candidate = candidates.FirstOrDefault();
      if (candidate is null)
      {
        issues.Add(
          $"The objective requires a fixed collection of exactly {expectedCount} items, but no bounded JavaScript or JSON array was found in the changed files."
        );
      }
      else if (candidate.Count != expectedCount)
      {
        issues.Add(
          $"The objective requires exactly {expectedCount} collection items, but Host static review counted {candidate.Count} in {candidate.RelativePath}."
        );
      }
      else if (Objective.Contains("word", StringComparison.OrdinalIgnoreCase))
      {
        var strings = ExtractTopLevelArrayWords(candidate.FinalContent);
        var unique = strings.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (strings.Count != expectedCount || unique != expectedCount)
        {
          issues.Add(
            $"The objective requires {expectedCount} fixed substantive words; {candidate.RelativePath} contains {strings.Count} word strings and {unique} unique values."
          );
        }
      }
    }

    var htmlFiles = latestChanges.Where(
      file => Path.GetExtension(file.RelativePath).Equals(".html", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(file.RelativePath).Equals(".htm", StringComparison.OrdinalIgnoreCase)
    ).ToArray();
    var scriptFiles = latestChanges.Where(
      file => Path.GetExtension(file.RelativePath).Equals(".js", StringComparison.OrdinalIgnoreCase)
    ).ToArray();
    if (
      htmlFiles.Length > 0
      && scriptFiles.Length > 0
      && Objective.Contains("game", StringComparison.OrdinalIgnoreCase)
    )
    {
      var html = string.Join("\n", htmlFiles.Select(file => file.FinalContent));
      var script = string.Join("\n", scriptFiles.Select(file => file.FinalContent));
      var htmlIds = System.Text.RegularExpressions.Regex.Matches(
        html,
        "\\bid\\s*=\\s*[\\\"'](?<id>[^\\\"']+)[\\\"']",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
          | System.Text.RegularExpressions.RegexOptions.CultureInvariant
      ).Select(match => match.Groups["id"].Value).ToHashSet(StringComparer.Ordinal);
      var referencedIds = System.Text.RegularExpressions.Regex.Matches(
        script,
        "getElementById\\(\\s*[\\\"'](?<id>[^\\\"']+)[\\\"']\\s*\\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
      ).Select(match => match.Groups["id"].Value).Concat(
        System.Text.RegularExpressions.Regex.Matches(
          script,
          "querySelector(?:All)?\\(\\s*[\\\"']#(?<id>[A-Za-z][A-Za-z0-9_:-]*)[\\\"']\\s*\\)",
          System.Text.RegularExpressions.RegexOptions.CultureInvariant
        ).Select(match => match.Groups["id"].Value)
      ).Distinct(StringComparer.Ordinal).ToArray();
      var missingIds = referencedIds.Where(id => !htmlIds.Contains(id)).ToArray();
      if (missingIds.Length > 0)
      {
        issues.Add(
          "JavaScript references DOM IDs that are absent from changed HTML: "
            + string.Join(", ", missingIds)
            + "."
        );
      }

      var controlIds = System.Text.RegularExpressions.Regex.Matches(
        html,
        "<(?:button|input|select|textarea)\\b[^>]*\\bid\\s*=\\s*[\\\"'](?<id>[^\\\"']+)[\\\"']",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
          | System.Text.RegularExpressions.RegexOptions.CultureInvariant
      ).Select(match => match.Groups["id"].Value).Distinct(StringComparer.Ordinal).ToArray();
      var unboundControls = controlIds.Where(
        id => !referencedIds.Contains(id, StringComparer.Ordinal)
      ).ToArray();
      if (unboundControls.Length > 0)
      {
        issues.Add(
          "Interactive controls are not referenced by changed JavaScript: "
            + string.Join(", ", unboundControls)
            + "."
        );
      }
    }

    return issues.ToArray();
  }

  private static IReadOnlyList<string> ExtractTopLevelArrayWords(string value)
  {
    var start = value.IndexOf('[');
    if (start < 0) return [];
    var end = value.IndexOf(']', start + 1);
    if (end < 0) return [];
    return System.Text.RegularExpressions.Regex.Matches(
      value[(start + 1)..end],
      "[\\\"'](?<word>[A-Za-z][A-Za-z-]*)[\\\"']",
      System.Text.RegularExpressions.RegexOptions.CultureInvariant
    ).Select(match => match.Groups["word"].Value).ToArray();
  }

  private static string SafeState(string value)
  {
    return new string(value.Where(character => !char.IsControl(character)).Take(512).ToArray());
  }

}

internal sealed record PlanActionBinding(
  string StepId,
  string ExpectedEffect,
  int EvidenceCountBefore
);

public sealed record ExecutionActionRecord(
  string ActionId,
  string Tool,
  string Summary,
  string State,
  string? Result,
  DateTimeOffset Timestamp,
  string? OriginalTool = null,
  string ToolResolutionSource = ToolNameResolver.CanonicalSource
);

public sealed record ExecutionSessionPersistenceSnapshot(
  string Id,
  string Objective,
  string SelectedModel,
  string CoordinatorModel,
  string ExecutionPath,
  string State,
  IReadOnlyList<ExecutionFileChange> Files,
  ExecutionSessionReview Review
);

public sealed record ExecutionFileChange(
  string RelativePath,
  string Operation,
  bool ExistedBefore,
  string OriginalHash,
  string FinalHash,
  string? OriginalContent,
  string FinalContent,
  long FinalSizeBytes,
  DateTimeOffset VerifiedAt,
  bool Verified,
  bool UndoAvailable,
  string? UndoDiagnostic,
  long RollbackBytes,
  bool PreExistingChange = false,
  string? CurrentGitStatus = null,
  string? OriginalBinaryBase64 = null
)
{
  public ExecutionFileReview ToReview()
  {
    return new ExecutionFileReview(
      RelativePath,
      Operation,
      ExistedBefore,
      OriginalHash,
      FinalHash,
      FinalSizeBytes,
      Verified,
      UndoAvailable,
      UndoDiagnostic,
      CreateUnifiedDiff(),
      PreExistingChange,
      CurrentGitStatus
    );
  }

  private string? CreateUnifiedDiff()
  {
    if (OriginalBinaryBase64 is not null)
    {
      return null;
    }

    if (OriginalContent is null && ExistedBefore)
    {
      return null;
    }

    var oldLines = (
      OriginalContent
        ?? string.Empty
    ).Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ).Split(
      '\n'
    );
    var newLines = FinalContent.Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ).Split(
      '\n'
    );
    var builder = new StringBuilder();
    builder.AppendLine(
      $"--- a/{RelativePath}"
    );
    builder.AppendLine(
      Operation == "deleted"
        ? "+++ /dev/null"
        : $"+++ b/{RelativePath}"
    );
    builder.AppendLine(
      $"@@ -1,{oldLines.Length} +1,{newLines.Length} @@"
    );

    foreach (var line in oldLines)
    {
      builder.Append(
        '-'
      ).AppendLine(
        line
      );
    }

    foreach (var line in newLines)
    {
      builder.Append(
        '+'
      ).AppendLine(
        line
      );
    }

    const int limit = 65_536;
    return builder.Length <= limit
      ? builder.ToString()
      : string.Concat(
        builder.ToString(
          0,
          limit
        ),
        "\n... diff truncated ..."
      );
  }
}
