using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Supervision;
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

  Task<ConversationPersistenceView> CreateAsync(
    string browserSessionId,
    CancellationToken cancellationToken
  );

  Task<ConversationPersistenceView> SaveAsync(
    SaveConversationSessionRequest request,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord?> BeginTurnAsync(
    string? sessionId,
    string message,
    string interactionMode,
    string? model,
    IReadOnlyList<ChatImageAttachment>? images,
    bool hidden,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord?> CompleteTurnAsync(
    string? sessionId,
    string answer,
    string interactionMode,
    string? model,
    ExecutionSessionReview? review,
    CancellationToken cancellationToken,
    TraceDiagnosticReference? diagnostic = null
  );

  Task MarkTerminalAsync(
    string? sessionId,
    string state,
    ExecutionSessionReview? review,
    CancellationToken cancellationToken,
    ChatMessage? terminalMessage = null
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
  private readonly IDurableSupervisionRunCoordinator _supervisionRuns;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public PersistentSessionService(
    IPersistentSessionStore store,
    IWorkspaceProfileService profiles,
    ISettingsStore settings,
    IExecutionSessionStore executionSessions,
    IDurableSupervisionRunCoordinator supervisionRuns
  )
  {
    _store = store;
    _profiles = profiles;
    _settings = settings;
    _executionSessions = executionSessions;
    _supervisionRuns = supervisionRuns;
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
    ).ToArray();
    var pinned = summaries.Where(
      session => session.Pinned && !session.Archived
    ).OrderByDescending(
      session => session.PinnedAt
    ).ThenByDescending(
      session => session.UpdatedAt
    ).ThenBy(
      session => session.Id,
      StringComparer.Ordinal
    ).ToArray();
    var recent = summaries.Where(
      session => !session.Archived && !session.Pinned
    ).OrderByDescending(
      session => session.UpdatedAt
    ).ThenBy(
      session => session.Id,
      StringComparer.Ordinal
    ).Take(
      20
    ).ToArray();
    var archived = summaries.Where(
      session => session.Archived
    ).OrderByDescending(
      session => session.UpdatedAt
    ).ThenBy(
      session => session.Id,
      StringComparer.Ordinal
    ).Take(
      20
    ).ToArray();
    return new ConversationSessionListResponse(
      recent,
      pinned,
      archived,
      CreateUsage(
        active,
        sessions
      )
    );
  }

  public async Task<ConversationPersistenceView> CreateAsync(
    string browserSessionId,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        browserSessionId
      )
      || browserSessionId.Length > 128
    )
    {
      throw new WorkspaceProfileException(
        "conversation-identity-invalid",
        "conversation-create",
        "A valid browser session identifier is required.",
        false
      );
    }

    if (_executionSessions.GetActive(
      browserSessionId
    )?.IsActive == true)
    {
      throw new WorkspaceProfileException(
        "conversation-switch-blocked",
        "conversation-create",
        "Finish or cancel the active execution before starting another conversation.",
        true
      );
    }

    var active = await _profiles.GetActiveDataAsync(
      cancellationToken
    );
    if (active is null)
    {
      return new ConversationPersistenceView(
        Guid.NewGuid().ToString(
          "N"
        ),
        false,
        false,
        "History disabled",
        null
      );
    }
    return new ConversationPersistenceView(
      Guid.NewGuid().ToString(
        "N"
      ),
      active.HistoryEnabled,
      false,
      active.HistoryEnabled
        ? "Unsaved"
        : "History disabled",
      null
    );
  }

  public async Task<ConversationPersistenceView> SaveAsync(
    SaveConversationSessionRequest request,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );

    if (!active.HistoryEnabled)
    {
      throw new WorkspaceProfileException(
        "history-disabled",
        "session-persistence",
        "Local history is disabled for the active workspace.",
        false
      );
    }

    ValidateSnapshot(
      request
    );
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
      var existing = await _store.ReadAsync(
        active.Id,
        request.SessionId,
        cancellationToken
      );

      if (
        existing is null
        && request.Messages.Count == 0
      )
      {
        return new ConversationPersistenceView(
          request.SessionId,
          true,
          false,
          "Saved locally",
          null
        );
      }

      if (existing is null)
      {
        await EnsureSessionIdAvailableAsync(
          active.Id,
          request.SessionId,
          cancellationToken
        );
        await EnsureRetentionAvailableAsync(
          active,
          limits,
          cancellationToken
        );
        var now = DateTimeOffset.UtcNow;
        existing = new ConversationSessionRecord(
          1,
          request.SessionId,
          active.Id,
          CreateTitle(
            request.Messages.First(
              message => message.Role == "user"
            ).Content
          ),
          now,
          now,
          false,
          request.State,
          request.InteractionMode,
          NormalizeModel(
            request.SelectedModel
          ),
          [],
          [],
          request.State == "interrupted",
          false,
          false,
          0
        )
        {
          PreferredModelProfileId = active.PreferredModelProfileId
        };
      }

      var saved = await _store.WriteAsync(
        existing with
        {
          State = request.State,
          Interrupted = request.State == "interrupted",
          UpdatedAt = DateTimeOffset.UtcNow,
          LastInteractionMode = request.InteractionMode,
          SelectedModel = NormalizeModel(
            request.SelectedModel
          ) ?? existing.SelectedModel,
          Messages = request.Messages.ToArray()
        },
        limits.MaxSessionBytes,
        cancellationToken
      );
      return new ConversationPersistenceView(
        saved.Id,
        true,
        true,
        saved.Interrupted
          ? "Interrupted"
          : "Saved locally",
        saved.UpdatedAt
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<ConversationSessionRecord?> BeginTurnAsync(
    string? sessionId,
    string message,
    string interactionMode,
    string? model,
    IReadOnlyList<ChatImageAttachment>? images,
    bool hidden,
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
        await EnsureRetentionAvailableAsync(
          active,
          limits,
          cancellationToken
        );

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
        )
        {
          PreferredModelProfileId = active.PreferredModelProfileId
        };
      }
      else
      {
        var stored = await _store.ReadAsync(
          active.Id,
          sessionId,
          cancellationToken
        );

        if (stored is null)
        {
          await EnsureSessionIdAvailableAsync(
            active.Id,
            sessionId,
            cancellationToken
          );
          await EnsureRetentionAvailableAsync(
            active,
            limits,
            cancellationToken
          );
          var now = DateTimeOffset.UtcNow;
          session = new ConversationSessionRecord(
            1,
            sessionId,
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
          )
          {
            PreferredModelProfileId = active.PreferredModelProfileId
          };
        }
        else
        {
          session = stored;
        }
      }

      var updatedAt = DateTimeOffset.UtcNow;
      session = session with
      {
        State = "running",
        Interrupted = false,
        UpdatedAt = updatedAt,
        LastInteractionMode = interactionMode,
        SelectedModel = NormalizeModel(
          model
        ) ?? session.SelectedModel,
        Messages = session.Messages.Append(
          new ChatMessage(
            "user",
            PersistedUserMessage(
              message,
              images
            ),
            updatedAt,
            Hidden: hidden
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

  private static string PersistedUserMessage(
    string message,
    IReadOnlyList<ChatImageAttachment>? images
  )
  {
    if (images is null || images.Count == 0)
    {
      return message;
    }

    var metadata = images.Select(
      image =>
        $"- {Path.GetFileName(image.FileName)} ({image.MimeType}, {Math.Max(0, image.DeclaredBytes)} bytes, missing-attachment)"
    );
    return string.Concat(
      message,
      "\n\n[Attachment metadata; image bytes were not persisted]\n",
      string.Join(
        "\n",
        metadata
      )
    );
  }

  public async Task<ConversationSessionRecord?> CompleteTurnAsync(
    string? sessionId,
    string answer,
    string interactionMode,
    string? model,
    ExecutionSessionReview? review,
    CancellationToken cancellationToken,
    TraceDiagnosticReference? diagnostic = null
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
    if (
      session.State == "completed"
      && !session.Interrupted
      && session.Messages.LastOrDefault() is { Role: "assistant" } last
      && string.Equals(last.Content, answer, StringComparison.Ordinal)
    )
    {
      return session;
    }
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
    var updatedAt = DateTimeOffset.UtcNow;
    var completed = session with
    {
      State = "completed",
      Interrupted = false,
      UpdatedAt = updatedAt,
      LastInteractionMode = interactionMode,
      SelectedModel = NormalizeModel(
        model
      ) ?? session.SelectedModel,
      Messages = session.Messages.Append(
        new ChatMessage(
          "assistant",
          answer,
          updatedAt,
          diagnostic
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
    CancellationToken cancellationToken,
    ChatMessage? terminalMessage = null
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
        Messages = AppendTerminalMessage(
          session.Messages,
          terminalMessage
        ),
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

  private static IReadOnlyList<ChatMessage> AppendTerminalMessage(
    IReadOnlyList<ChatMessage> messages,
    ChatMessage? terminalMessage
  )
  {
    if (terminalMessage is null)
    {
      return messages;
    }

    var traceId = terminalMessage.Diagnostic?.TraceId;
    if (
      traceId is not null
      && messages.LastOrDefault()?.Diagnostic?.TraceId is { } lastTraceId
      && string.Equals(traceId, lastTraceId, StringComparison.Ordinal)
    )
    {
      return messages;
    }

    return messages.Append(terminalMessage).ToArray();
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
    await DeleteSupervisionAsync(
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
      await DeleteSupervisionAsync(
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
      await DeleteSupervisionAsync(
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

  private async Task EnsureSessionIdAvailableAsync(
    string activeWorkspaceId,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    var profiles = await _profiles.GetAllAsync(
      cancellationToken
    );

    foreach (var profile in profiles.Profiles.Where(
      profile => !string.Equals(
        profile.Id,
        activeWorkspaceId,
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
          "duplicate-session-id",
          "session-persistence",
          "The conversation identifier is already used by another workspace.",
          false
        );
      }
    }
  }

  private async Task EnsureRetentionAvailableAsync(
    WorkspaceProfileData active,
    SessionHistorySettings limits,
    CancellationToken cancellationToken
  )
  {
    var existing = await _store.ReadAllAsync(
      active.Id,
      cancellationToken
    );

    while (existing.Count >= limits.MaxSessionsPerWorkspace)
    {
      var removable = existing.Where(
        session => !session.Pinned
      ).OrderBy(
        session => session.UpdatedAt
      ).ThenBy(
        session => session.Id,
        StringComparer.Ordinal
      ).FirstOrDefault();

      if (removable is null)
      {
        throw new WorkspaceProfileException(
          "history-retention-limit-reached",
          "session-retention",
          "The workspace reached its session-history limit and every retained session is pinned.",
          false
        );
      }

      await _store.DeleteAsync(
        active.Id,
        removable.Id,
        cancellationToken
      );
      await DeleteSupervisionAsync(
        active.Id,
        removable.Id,
        cancellationToken
      );
      existing = existing.Where(
        session => !string.Equals(
          session.Id,
          removable.Id,
          StringComparison.Ordinal
        )
      ).ToArray();
    }
  }

  private async Task DeleteSupervisionAsync(
    string workspaceId,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await _supervisionRuns.DiscardConversationAsync(
        workspaceId,
        sessionId,
        cancellationToken
      );
    }
    catch (SupervisionException exception)
    {
      throw new WorkspaceProfileException(
        exception.Code,
        "session-deletion",
        exception.Message,
        exception.Retryable,
        exception
      );
    }
  }

  private static void ValidateSnapshot(
    SaveConversationSessionRequest request
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        request.SessionId
      )
      || request.SessionId.Length > 64
      || request.SessionId.Any(
        character => !char.IsAsciiLetterOrDigit(
          character
        ) && character is not '-' and not '_'
      )
      || request.Messages.Count > 400
      || request.Messages.Any(
        message => message.Role is not "user" and not "assistant"
          || string.IsNullOrWhiteSpace(
            message.Content
          )
          || (
            message.Hidden
            && (
              message.Role != "user"
              || !message.Content.StartsWith(
                "TRACE_DIAGNOSTIC_INVESTIGATION_V1",
                StringComparison.Ordinal
              )
            )
          )
      )
      || (
        request.Messages.Count > 0
        && request.Messages.All(
          message => message.Role != "user"
        )
      )
      || request.InteractionMode is not "chat" and not "execute"
      || request.State is not "completed"
        and not "failed"
        and not "cancelled"
        and not "interrupted"
    )
    {
      throw new WorkspaceProfileException(
        "session-record-invalid",
        "session-persistence",
        "The conversation snapshot is invalid.",
        false
      );
    }
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
      session.StorageBytes,
      session.Pinned,
      session.PinnedAt,
      session.SessionSummary is not null,
      session.PreferredModelProfileId,
      session.SelectedModel
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
