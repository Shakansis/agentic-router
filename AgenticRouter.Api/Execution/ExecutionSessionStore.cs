using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

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

        if (!File.Exists(
          path
        ))
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

      foreach (var change in changes.Where(
        item => item.ExistedBefore
      ))
      {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveSessionPath(
          session,
          change.RelativePath
        );
        await WriteAtomicallyAsync(
          path,
          change.OriginalContent!,
          cancellationToken
        );
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
          await WriteAtomicallyAsync(
            path,
            change.FinalContent,
            CancellationToken.None
          );
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
  private string _completionStatus = "not-evaluated";

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

  public void RecordPlanActionStarted(
    string tool
  )
  {
    lock (_gate)
    {
      if (_plan is null)
      {
        return;
      }

      var index = FindPlanStep(
        tool,
        [
          "pending",
          "in-progress"
        ]
      );

      if (index < 0)
      {
        return;
      }

      var steps = _plan.Steps.ToArray();
      steps[index] = steps[index] with
      {
        Status = "in-progress"
      };
      _plan = _plan with
      {
        Steps = steps,
        CurrentStepId = steps[index].Id
      };
    }
  }

  public void RecordPlanActionResult(
    string tool,
    string status
  )
  {
    lock (_gate)
    {
      if (_plan is null)
      {
        return;
      }

      var index = FindPlanStep(
        tool,
        [
          "in-progress",
          "pending"
        ]
      );

      if (index < 0)
      {
        return;
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
    }
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
        DateTimeOffset.UtcNow
      );

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
        _completionStatus
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
        _validation
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
      _project = snapshot.Review.Project;
      _baseline = snapshot.Review.Baseline;
      _plan = snapshot.Review.Summary.Plan;
      _validationProfile = snapshot.Review.ValidationProfile;
      _validation = snapshot.Review.Validation;
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
      State = snapshot.State is "completed" or "completed-with-warnings"
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

  private int FindPlanStep(
    string tool,
    IReadOnlyCollection<string> states
  )
  {
    string[] keywords = tool switch
    {
      "list_files" or "read_file" or "get_file_info" or "search_text" =>
      [
        "inspect",
        "read",
        "review",
        "analis",
        "ler"
      ],
      "run_validation_profile" =>
      [
        "valid",
        "test",
        "build",
        "format"
      ],
      _ =>
      [
        "implement",
        "change",
        "edit",
        "create",
        "apply",
        "alter",
        "criar"
      ]
    };
    var matching = _plan!.Steps.Select(
      (
        step,
        index
      ) => new
      {
        Step = step,
        Index = index
      }
    ).FirstOrDefault(
      item => states.Contains(
        item.Step.Status,
        StringComparer.Ordinal
      ) && keywords.Any(
        keyword => item.Step.Title.Contains(
          keyword,
          StringComparison.OrdinalIgnoreCase
        )
      )
    );
    return matching?.Index
      ?? _plan.Steps.Select(
        (
          step,
          index
        ) => new
        {
          Step = step,
          Index = index
        }
      ).FirstOrDefault(
        item => states.Contains(
          item.Step.Status,
          StringComparer.Ordinal
        )
      )?.Index
      ?? -1;
  }

  private void EvaluateCompletionGate()
  {
    var validationRequested = Objective.Contains(
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
    ) == true;

    if (_files.Count > 0)
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
      State = State == "cancelled"
        ? State
        : "completed-with-warnings";
    }
  }
}

public sealed record ExecutionActionRecord(
  string ActionId,
  string Tool,
  string Summary,
  string State,
  string? Result,
  DateTimeOffset Timestamp
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
  string? CurrentGitStatus = null
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
      $"+++ b/{RelativePath}"
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
