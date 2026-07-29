using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Sessions;

public interface IPersistentSessionService
{
  Task RecoverInterruptedAsync(
    CancellationToken cancellationToken
  );

  Task<ConversationSessionListResponse> ListAsync(
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord?> BeginTurnAsync(
    string? sessionId,
    string message,
    string interactionMode,
    string? model,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord?> CompleteTurnAsync(
    string? sessionId,
    string answer,
    string interactionMode,
    string? model,
    ExecutionSessionReview? review,
    CancellationToken cancellationToken
  );

  Task MarkTerminalAsync(
    string? sessionId,
    string state,
    ExecutionSessionReview? review,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord> ResumeAsync(
    string sessionId,
    string browserSessionId,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord> RenameAsync(
    string sessionId,
    string title,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord> ArchiveAsync(
    string sessionId,
    CancellationToken cancellationToken
  );

  Task DeleteAsync(
    string sessionId,
    CancellationToken cancellationToken
  );

  Task DeleteArchivedAsync(
    CancellationToken cancellationToken
  );

  Task DeleteAllAsync(
    CancellationToken cancellationToken
  );

  Task<byte[]> ExportAsync(
    string sessionId,
    CancellationToken cancellationToken
  );
}

public sealed class PersistentSessionService : IPersistentSessionService
{
  private readonly IPersistentSessionStore _store;
  private readonly IWorkspaceProfileService _profiles;
  private readonly ISettingsStore _settings;
  private readonly IExecutionSessionStore _executionSessions;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public PersistentSessionService(
    IPersistentSessionStore store,
    IWorkspaceProfileService profiles,
    ISettingsStore settings,
    IExecutionSessionStore executionSessions
  )
  {
    _store = store;
    _profiles = profiles;
    _settings = settings;
    _executionSessions = executionSessions;
  }

  public async Task RecoverInterruptedAsync(
    CancellationToken cancellationToken
  )
  {
    var workspaces = await _profiles.GetAllAsync(
      cancellationToken
    );
    var limits = (
      await _settings.GetAsync(
        cancellationToken
      )
    ).SessionHistory;

    foreach (var workspace in workspaces.Profiles)
    {
      var sessions = await _store.ReadAllAsync(
        workspace.Id,
        cancellationToken
      );

      foreach (var session in sessions.Where(
        item => item.State == "running"
      ))
      {
        await _store.WriteAsync(
          session with
          {
            State = "interrupted",
            Interrupted = true,
            UpdatedAt = DateTimeOffset.UtcNow
          },
          limits.MaxSessionBytes,
          cancellationToken
        );
      }
    }
  }

  public async Task<ConversationSessionListResponse> ListAsync(
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    var sessions = await _store.ReadAllAsync(
      active.Id,
      cancellationToken
    );
    var summaries = sessions.Select(
      ToSummary
    ).OrderByDescending(
      session => session.UpdatedAt
    ).ToArray();
    return new ConversationSessionListResponse(
      summaries.Where(
        session => !session.Archived
      ).ToArray(),
      summaries.Where(
        session => session.Archived
      ).ToArray(),
      CreateUsage(
        active,
        sessions
      )
    );
  }

  public async Task<ConversationSessionRecord?> BeginTurnAsync(
    string? sessionId,
    string message,
    string interactionMode,
    string? model,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );

    if (!active.HistoryEnabled)
    {
      return null;
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var limits = (
        await _settings.GetAsync(
          cancellationToken
        )
      ).SessionHistory;
      ConversationSessionRecord session;

      if (string.IsNullOrWhiteSpace(
        sessionId
      ))
      {
        var existing = await _store.ReadAllAsync(
          active.Id,
          cancellationToken
        );

        if (existing.Count >= limits.MaxSessionsPerWorkspace)
        {
          throw new WorkspaceProfileException(
            "history-retention-limit-reached",
            "session-retention",
            "The workspace reached its session-history limit. Remove an older session first.",
            false
          );
        }

        var now = DateTimeOffset.UtcNow;
        session = new ConversationSessionRecord(
          1,
          Guid.NewGuid().ToString(
            "N"
          ),
          active.Id,
          CreateTitle(
            message
          ),
          now,
          now,
          false,
          "running",
          interactionMode,
          NormalizeModel(
            model
          ),
          [],
          [],
          false,
          false,
          false,
          0
        );
      }
      else
      {
        session = await RequireSessionAsync(
          active,
          sessionId,
          cancellationToken
        );
      }

      session = session with
      {
        State = "running",
        Interrupted = false,
        UpdatedAt = DateTimeOffset.UtcNow,
        LastInteractionMode = interactionMode,
        SelectedModel = NormalizeModel(
          model
        ) ?? session.SelectedModel,
        Messages = session.Messages.Append(
          new ChatMessage(
            "user",
            message
          )
        ).ToArray()
      };
      return await _store.WriteAsync(
        session,
        limits.MaxSessionBytes,
        cancellationToken
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<ConversationSessionRecord?> CompleteTurnAsync(
    string? sessionId,
    string answer,
    string interactionMode,
    string? model,
    ExecutionSessionReview? review,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(
      sessionId
    ))
    {
      return null;
    }

    var active = await RequireActiveAsync(
      cancellationToken
    );
    var session = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );
    var limits = (
      await _settings.GetAsync(
        cancellationToken
      )
    ).SessionHistory;
    var artifactsTruncated = false;
    var bounded = review is null
      ? null
      : BoundReview(
        review,
        limits,
        out artifactsTruncated
      );
    var snapshot = review is null
      ? null
      : _executionSessions.CapturePersistenceSnapshot(
        review.Summary.Id
      );
    var rollbackTruncated = snapshot is not null
      && snapshot.Files.Sum(
        file => file.RollbackBytes
      ) > limits.MaxSessionBytes / 2;

    if (rollbackTruncated)
    {
      snapshot = null;
      artifactsTruncated = true;
      bounded = DisableUndo(
        bounded,
        "Undo metadata is unavailable because retained rollback content exceeded the session limit."
      );
    }
    var completed = session with
    {
      State = "completed",
      UpdatedAt = DateTimeOffset.UtcNow,
      LastInteractionMode = interactionMode,
      SelectedModel = NormalizeModel(
        model
      ) ?? session.SelectedModel,
      Messages = session.Messages.Append(
        new ChatMessage(
          "assistant",
          answer
        )
      ).ToArray(),
      ExecutionReviews = bounded is null
        ? session.ExecutionReviews
        : session.ExecutionReviews.Append(
          bounded
        ).ToArray(),
      ArtifactsTruncated = session.ArtifactsTruncated
        || (
          review is not null
          && artifactsTruncated
        ),
      ExecutionRollbacks = MergeSnapshot(
        session.ExecutionRollbacks,
        snapshot
      )
    };
    return await _store.WriteAsync(
      completed,
      limits.MaxSessionBytes,
      cancellationToken
    );
  }

  public async Task MarkTerminalAsync(
    string? sessionId,
    string state,
    ExecutionSessionReview? review,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(
      sessionId
    ))
    {
      return;
    }

    var active = await RequireActiveAsync(
      cancellationToken
    );
    var session = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );
    var limits = (
      await _settings.GetAsync(
        cancellationToken
      )
    ).SessionHistory;
    var bounded = review is null
      ? null
      : BoundReview(
        review,
        limits,
        out _
      );
    var snapshot = review is null
      ? null
      : _executionSessions.CapturePersistenceSnapshot(
        review.Summary.Id
      );

    if (
      snapshot is not null
      && snapshot.Files.Sum(
        file => file.RollbackBytes
      ) > limits.MaxSessionBytes / 2
    )
    {
      snapshot = null;
      bounded = DisableUndo(
        bounded,
        "Undo metadata is unavailable because retained rollback content exceeded the session limit."
      );
    }
    await _store.WriteAsync(
      session with
      {
        State = state,
        Interrupted = state == "interrupted",
        UpdatedAt = DateTimeOffset.UtcNow,
        ExecutionReviews = MergeReview(
          session.ExecutionReviews,
          bounded
        ),
        ExecutionRollbacks = MergeSnapshot(
          session.ExecutionRollbacks,
          snapshot
        )
      },
      limits.MaxSessionBytes,
      cancellationToken
    );
  }

  public async Task<ConversationSessionRecord> ResumeAsync(
    string sessionId,
    string browserSessionId,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(
      browserSessionId
    ) || browserSessionId.Length > 128)
    {
      throw new WorkspaceProfileException(
        "session-resume-blocked",
        "session-resume",
        "A valid browser session identifier is required.",
        false
      );
    }

    var active = await RequireActiveAsync(
      cancellationToken
    );
    _ = WorkspacePathValidator.Inspect(
      active.Path
    ) is
    {
      Valid: true
    } valid
      ? valid
      : throw new WorkspaceProfileException(
        "session-resume-blocked",
        "session-resume",
        "The active workspace is unavailable.",
        true
      );
    var session = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );

    if (session.State == "running")
    {
      await MarkTerminalAsync(
        sessionId,
        "interrupted",
        null,
        cancellationToken
      );
      session = session with
      {
        State = "interrupted",
        Interrupted = true
      };
    }

    var maximumMessages = (
      await _settings.GetAsync(
        cancellationToken
      )
    ).Context.MaxConversationMessages;
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var allSessions = await _store.ReadAllAsync(
      active.Id,
      cancellationToken
    );
    var latestChangingSession = allSessions.Where(
      candidate => candidate.ExecutionRollbacks?.Any(
        rollback => rollback.Files.Count > 0
      ) == true
    ).OrderByDescending(
      candidate => candidate.UpdatedAt
    ).FirstOrDefault();
    var reviews = session.ExecutionReviews;

    if (
      string.Equals(
        latestChangingSession?.Id,
        session.Id,
        StringComparison.Ordinal
      )
      && session.ExecutionRollbacks?.LastOrDefault() is
      {
        State: "completed" or "completed-with-warnings"
      } snapshot
    )
    {
      var restored = _executionSessions.RestorePersistenceSnapshot(
        snapshot,
        browserSessionId,
        active.Path,
        settings.Execution
      );
      reviews = MergeReview(
        reviews,
        restored
      );
    }
    else
    {
      reviews = reviews.Select(
        review => DisableUndo(
          review,
          "Undo is unavailable because this is not the latest eligible changing session."
        )!
      ).ToArray();
    }

    return session with
    {
      ContextTruncated = session.Messages.Count > maximumMessages,
      ExecutionReviews = reviews
    };
  }

  public Task<ConversationSessionRecord> RenameAsync(
    string sessionId,
    string title,
    CancellationToken cancellationToken
  )
  {
    var value = title.Trim();

    if (value.Length is < 1 or > 100)
    {
      throw new WorkspaceProfileException(
        "session-title-invalid",
        "session-rename",
        "Session title must contain between 1 and 100 characters.",
        false
      );
    }

    return UpdateAsync(
      sessionId,
      session => session with
      {
        Title = value
      },
      cancellationToken
    );
  }

  public Task<ConversationSessionRecord> ArchiveAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    return UpdateAsync(
      sessionId,
      session => session with
      {
        Archived = true
      },
      cancellationToken
    );
  }

  public async Task DeleteAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    _ = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );
    await _store.DeleteAsync(
      active.Id,
      sessionId,
      cancellationToken
    );
  }

  public async Task DeleteArchivedAsync(
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    var sessions = await _store.ReadAllAsync(
      active.Id,
      cancellationToken
    );

    foreach (var session in sessions.Where(
      item => item.Archived
    ))
    {
      await _store.DeleteAsync(
        active.Id,
        session.Id,
        cancellationToken
      );
    }
  }

  public async Task DeleteAllAsync(
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    var sessions = await _store.ReadAllAsync(
      active.Id,
      cancellationToken
    );

    foreach (var session in sessions)
    {
      await _store.DeleteAsync(
        active.Id,
        session.Id,
        cancellationToken
      );
    }
  }

  public async Task<byte[]> ExportAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    var session = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );
    return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
      session with
      {
        ExecutionRollbacks = []
      },
      new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web
      )
      {
        WriteIndented = true
      }
    );
  }

  private async Task<ConversationSessionRecord> UpdateAsync(
    string sessionId,
    Func<ConversationSessionRecord, ConversationSessionRecord> update,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    var session = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );
    var limits = (
      await _settings.GetAsync(
        cancellationToken
      )
    ).SessionHistory;
    return await _store.WriteAsync(
      update(
        session
      ) with
      {
        UpdatedAt = DateTimeOffset.UtcNow
      },
      limits.MaxSessionBytes,
      cancellationToken
    );
  }

  private async Task<WorkspaceProfileData> RequireActiveAsync(
    CancellationToken cancellationToken
  )
  {
    return await _profiles.GetActiveDataAsync(
      cancellationToken
    ) ?? throw new WorkspaceProfileException(
      "workspace-profile-unavailable",
      "session-workspace",
      "No active workspace profile is available.",
      false
    );
  }

  private async Task<ConversationSessionRecord> RequireSessionAsync(
    WorkspaceProfileData active,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    var session = await _store.ReadAsync(
      active.Id,
      sessionId,
      cancellationToken
    );

    if (session is not null)
    {
      return session;
    }

    var profiles = await _profiles.GetAllAsync(
      cancellationToken
    );

    foreach (var profile in profiles.Profiles.Where(
      profile => !string.Equals(
        profile.Id,
        active.Id,
        StringComparison.Ordinal
      )
    ))
    {
      if (await _store.ReadAsync(
        profile.Id,
        sessionId,
        cancellationToken
      ) is not null)
      {
        throw new WorkspaceProfileException(
          "session-belongs-to-another-workspace",
          "session-workspace",
          "The requested session belongs to another workspace.",
          false
        );
      }
    }

    throw new WorkspaceProfileException(
      "session-not-found",
      "session-storage",
      "The session was not found in the active workspace.",
      false
    );
  }

  private static ConversationSessionSummary ToSummary(
    ConversationSessionRecord session
  )
  {
    return new ConversationSessionSummary(
      session.Id,
      session.WorkspaceId,
      session.Title,
      session.CreatedAt,
      session.UpdatedAt,
      session.Archived,
      session.LastInteractionMode,
      session.Interrupted,
      session.StorageBytes
    );
  }

  private static IReadOnlyList<ExecutionSessionReview> MergeReview(
    IReadOnlyList<ExecutionSessionReview> reviews,
    ExecutionSessionReview? review
  )
  {
    if (review is null)
    {
      return reviews;
    }

    return reviews.Where(
      existing => !string.Equals(
        existing.Summary.Id,
        review.Summary.Id,
        StringComparison.Ordinal
      )
    ).Append(
      review
    ).ToArray();
  }

  private static IReadOnlyList<ExecutionSessionPersistenceSnapshot> MergeSnapshot(
    IReadOnlyList<ExecutionSessionPersistenceSnapshot>? snapshots,
    ExecutionSessionPersistenceSnapshot? snapshot
  )
  {
    var existing = snapshots
      ?? [];

    if (snapshot is null)
    {
      return existing;
    }

    return existing.Where(
      item => !string.Equals(
        item.Id,
        snapshot.Id,
        StringComparison.Ordinal
      )
    ).Append(
      snapshot
    ).ToArray();
  }

  private static ExecutionSessionReview? DisableUndo(
    ExecutionSessionReview? review,
    string diagnostic
  )
  {
    return review is null
      ? null
      : review with
      {
        Summary = review.Summary with
        {
          UndoAvailable = false,
          UndoDiagnostic = diagnostic
        }
      };
  }

  private static WorkspaceHistoryUsage CreateUsage(
    WorkspaceProfileData workspace,
    IReadOnlyList<ConversationSessionRecord> sessions
  )
  {
    return new WorkspaceHistoryUsage(
      workspace.Id,
      workspace.HistoryEnabled,
      sessions.Count,
      sessions.Sum(
        session => session.StorageBytes
      ),
      sessions.Count == 0
        ? null
        : sessions.Min(
          session => session.CreatedAt
        ),
      sessions.Count == 0
        ? null
        : sessions.Max(
          session => session.UpdatedAt
        )
    );
  }

  private static string CreateTitle(
    string message
  )
  {
    var normalized = string.Join(
      " ",
      message.Split(
        (char[]?)null,
        StringSplitOptions.RemoveEmptyEntries
      )
    );
    return normalized.Length <= 80
      ? normalized
      : string.Concat(
        normalized.AsSpan(
          0,
          77
        ),
        "..."
      );
  }

  private static string? NormalizeModel(
    string? model
  )
  {
    return string.IsNullOrWhiteSpace(
      model
    ) || model == "auto"
      ? null
      : model;
  }

  private static ExecutionSessionReview BoundReview(
    ExecutionSessionReview review,
    SessionHistorySettings limits,
    out bool truncated
  )
  {
    var wasTruncated = false;
    var files = new List<ExecutionFileReview>();

    foreach (var file in review.Files)
    {
      files.Add(
        file with
        {
          UnifiedDiff = Truncate(
            file.UnifiedDiff,
            limits.MaxStoredDiffBytesPerTurn,
            ref wasTruncated
          )
        }
      );
    }

    var perProcess = Math.Max(
      512,
      limits.MaxStoredProcessOutputBytesPerTurn
        / Math.Max(
          1,
          review.Processes.Count
        )
    );
    var processes = new List<ExecutionProcessReview>();

    foreach (var process in review.Processes)
    {
      processes.Add(
        process with
        {
          StandardOutput = Truncate(
            process.StandardOutput,
            perProcess,
            ref wasTruncated
          ) ?? string.Empty,
          StandardError = Truncate(
            process.StandardError,
            perProcess,
            ref wasTruncated
          ) ?? string.Empty
        }
      );
    }

    truncated = wasTruncated;
    return review with
    {
      WorkspacePath = ".",
      Files = files.ToArray(),
      Processes = processes.ToArray()
    };
  }

  private static string? Truncate(
    string? value,
    int maximumCharacters,
    ref bool truncated
  )
  {
    if (
      value is null
      || value.Length <= maximumCharacters
    )
    {
      return value;
    }

    truncated = true;
    return string.Concat(
      value.AsSpan(
        0,
        maximumCharacters
      ),
      "\n[truncated in persistent history]"
    );
  }
}
