using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Models;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Sessions;

public interface IConversationProductivityService
{
  Task<ConversationSearchResponse> SearchAsync(
    ConversationSearchRequest request,
    CancellationToken cancellationToken
  );

  Task<ConversationSessionRecord> SetPinnedAsync(
    string sessionId,
    bool pinned,
    CancellationToken cancellationToken
  );

  Task<DuplicateConversationResponse> DuplicateAsync(
    string sessionId,
    CancellationToken cancellationToken
  );

  Task<SessionSummaryEstimate> EstimateSummaryAsync(
    string sessionId,
    string model,
    CancellationToken cancellationToken
  );

  Task<SessionSummaryRecord> GenerateSummaryAsync(
    string sessionId,
    GenerateSessionSummaryRequest request,
    CancellationToken cancellationToken
  );

  Task<SessionSummaryRecord?> GetSummaryAsync(
    string sessionId,
    CancellationToken cancellationToken
  );

  Task<SessionSummaryRecord> UpdateSummaryAsync(
    string sessionId,
    SessionSummaryContent content,
    CancellationToken cancellationToken
  );

  Task DeleteSummaryAsync(
    string sessionId,
    CancellationToken cancellationToken
  );

  Task<byte[]> ExportMarkdownAsync(
    string sessionId,
    bool includeSummary,
    bool includeModelMetadata,
    CancellationToken cancellationToken
  );
}

public sealed class ConversationProductivityService
  : IConversationProductivityService
{
  public const string SummaryMarker = "SESSION_SUMMARY_V1";
  private const int MaximumSearchLimit = 100;
  private const int MaximumSummaryItems = 24;
  private const int MaximumSummaryItemLength = 500;
  private const int MaximumSummaryTextLength = 2_000;
  private const int MaximumFactMessageLength = 4_000;
  private const int MaximumFactTurns = 20;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  );
  private static readonly JsonElement SummarySchema = CreateSummarySchema();
  private static readonly Regex SecretPattern = new(
    @"(?ix)
      \b(?:gsk|sk|csk)_[a-z0-9_-]{8,}\b
      |
      \bAIza[a-z0-9_-]{12,}\b
      |
      \bauthorization\s*:\s*(?:bearer\s+)?[^\s]+",
    RegexOptions.Compiled,
    TimeSpan.FromMilliseconds(
      100
    )
  );
  private static readonly Regex AbsoluteWindowsPathPattern = new(
    @"(?i)\b[a-z]:\\(?:[^\\\r\n]+\\)*[^\\\r\n]*",
    RegexOptions.Compiled,
    TimeSpan.FromMilliseconds(
      100
    )
  );

  private readonly IPersistentSessionStore _store;
  private readonly IWorkspaceProfileService _profiles;
  private readonly ISettingsStore _settings;
  private readonly IOllamaClient _providers;
  private readonly ITokenEstimator _tokens;
  private readonly IModelOrganizationService _modelOrganization;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public ConversationProductivityService(
    IPersistentSessionStore store,
    IWorkspaceProfileService profiles,
    ISettingsStore settings,
    IOllamaClient providers,
    ITokenEstimator tokens,
    IModelOrganizationService modelOrganization
  )
  {
    _store = store;
    _profiles = profiles;
    _settings = settings;
    _providers = providers;
    _tokens = tokens;
    _modelOrganization = modelOrganization;
  }

  public async Task<ConversationSearchResponse> SearchAsync(
    ConversationSearchRequest request,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    var workspaces = await _profiles.GetAllAsync(
      cancellationToken
    );
    var selectedWorkspaces = request.AllWorkspaces
      ? workspaces.Profiles
      : workspaces.Profiles.Where(
        workspace => string.Equals(
          workspace.Id,
          active.Id,
          StringComparison.Ordinal
        )
      ).ToArray();
    var limit = Math.Clamp(
      request.Limit,
      1,
      MaximumSearchLimit
    );
    var query = NormalizeOptional(
      request.Query
    );
    var results = new List<ConversationSearchResult>();
    var scanned = 0;
    var matchedBeyondLimit = false;

    foreach (var workspace in selectedWorkspaces.OrderBy(
      item => item.Name,
      StringComparer.OrdinalIgnoreCase
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var sessions = await _store.ReadAllAsync(
        workspace.Id,
        cancellationToken
      );

      foreach (var session in sessions.OrderByDescending(
        item => item.UpdatedAt
      ).ThenBy(
        item => item.Id,
        StringComparer.Ordinal
      ))
      {
        cancellationToken.ThrowIfCancellationRequested();
        scanned++;

        if (!MatchesFilters(
          session,
          request
        ))
        {
          continue;
        }

        var match = FindMatch(
          session,
          workspace.Name,
          query
        );

        if (match is null)
        {
          continue;
        }

        if (results.Count >= limit)
        {
          matchedBeyondLimit = true;
          continue;
        }

        var reference = string.IsNullOrWhiteSpace(
          session.SelectedModel
        )
          ? null
          : ProviderModelReference.Parse(
            session.SelectedModel
          );
        results.Add(
          new ConversationSearchResult(
            session.Id,
            session.WorkspaceId,
            workspace.Name,
            session.Title,
            session.UpdatedAt,
            session.Archived,
            session.Pinned,
            session.SessionSummary is not null,
            reference?.ProviderId,
            session.SelectedModel,
            match.Value.Field,
            match.Value.Snippet,
            match.Value.Highlights
          )
        );
      }
    }

    return new ConversationSearchResponse(
      results.OrderByDescending(
        result => result.Pinned
      ).ThenByDescending(
        result => result.UpdatedAt
      ).ThenBy(
        result => result.Id,
        StringComparer.Ordinal
      ).ToArray(),
      matchedBeyondLimit,
      scanned,
      request.AllWorkspaces
        ? "all-workspaces"
        : "active-workspace"
    );
  }

  public Task<ConversationSessionRecord> SetPinnedAsync(
    string sessionId,
    bool pinned,
    CancellationToken cancellationToken
  )
  {
    return UpdateAsync(
      sessionId,
      session => session with
      {
        Pinned = pinned,
        PinnedAt = pinned
          ? session.PinnedAt
            ?? DateTimeOffset.UtcNow
          : null
      },
      cancellationToken
    );
  }

  public async Task<DuplicateConversationResponse> DuplicateAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    var source = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );
    var limits = (
      await _settings.GetAsync(
        cancellationToken
      )
    ).SessionHistory;
    await EnsureRetentionAsync(
      active,
      limits,
      cancellationToken
    );
    var preferredProfile = source.PreferredModelProfileId;

    if (!string.IsNullOrWhiteSpace(
      preferredProfile
    ))
    {
      var organization = await _modelOrganization.GetAsync(
        cancellationToken
      );

      if (!organization.Profiles.Any(
        profile => string.Equals(
          profile.Id,
          preferredProfile,
          StringComparison.Ordinal
        )
      ))
      {
        preferredProfile = null;
      }
    }

    var now = DateTimeOffset.UtcNow;
    var duplicate = new ConversationSessionRecord(
      1,
      Guid.NewGuid().ToString(
        "N"
      ),
      active.Id,
      DerivedTitle(
        source.Title
      ),
      now,
      now,
      false,
      "completed",
      "chat",
      null,
      source.Messages.Where(
        message => message.Role is "user" or "assistant"
          && !string.IsNullOrWhiteSpace(
            message.Content
          )
      ).ToArray(),
      [],
      false,
      false,
      false,
      0,
      [],
      false,
      null,
      source.SessionSummary,
      preferredProfile
    );
    var saved = await _store.WriteAsync(
      duplicate,
      limits.MaxSessionBytes,
      cancellationToken
    );
    return new DuplicateConversationResponse(
      saved,
      source.Id
    );
  }

  public async Task<SessionSummaryEstimate> EstimateSummaryAsync(
    string sessionId,
    string model,
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
    var resolved = await RequireModelAsync(
      model,
      cancellationToken
    );
    var facts = BuildSummaryMessages(
      session
    );
    return new SessionSummaryEstimate(
      session.Id,
      resolved.Name,
      resolved.Provider,
      ModelProviderIds.DisplayName(
        resolved.Provider
      ),
      _tokens.EstimateMessages(
        facts.Messages
      ),
      facts.IncludedMessages,
      facts.OmittedMessages,
      true
    );
  }

  public async Task<SessionSummaryRecord> GenerateSummaryAsync(
    string sessionId,
    GenerateSessionSummaryRequest request,
    CancellationToken cancellationToken
  )
  {
    if (!request.Confirmed || !request.ProviderPermissionGranted)
    {
      throw Error(
        "session-summary-permission-required",
        "session-summary",
        "Summary generation requires explicit confirmation for the selected real provider or GPU."
      );
    }

    var active = await RequireActiveAsync(
      cancellationToken
    );
    var session = await RequireSessionAsync(
      active,
      sessionId,
      cancellationToken
    );
    var resolved = await RequireModelAsync(
      request.Model,
      cancellationToken
    );
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    var facts = BuildSummaryMessages(
      session
    );
    var estimatedInput = _tokens.EstimateMessages(
      facts.Messages
    );
    var json = await _providers.GenerateStructuredAsync(
      new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      ),
      resolved.Name,
      facts.Messages,
      SummarySchema,
      "session-summary",
      new ProviderCallContext(
        active.Id,
        session.Id,
        Guid.NewGuid().ToString(
          "N"
        ),
        null,
        UsageModelRoles.Summary,
        "explicit-session-summary",
        resolved.Digest
      ),
      cancellationToken
    );
    SessionSummaryContent? content;

    try
    {
      content = JsonSerializer.Deserialize<SessionSummaryContent>(
        json,
        JsonOptions
      );
    }
    catch (JsonException exception)
    {
      throw Error(
        "session-summary-invalid",
        "session-summary",
        "The selected model returned an invalid structured session summary.",
        exception
      );
    }

    var normalized = ValidateSummary(
      content
    );
    var now = DateTimeOffset.UtcNow;
    var summary = new SessionSummaryRecord(
      normalized,
      resolved.Name,
      resolved.Provider,
      estimatedInput,
      session.SessionSummary?.CreatedAt
        ?? now,
      now,
      true
    );
    var saved = await UpdateAsync(
      sessionId,
      current => current with
      {
        SessionSummary = summary
      },
      cancellationToken
    );
    return saved.SessionSummary!;
  }

  public async Task<SessionSummaryRecord?> GetSummaryAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    var active = await RequireActiveAsync(
      cancellationToken
    );
    return (
      await RequireSessionAsync(
        active,
        sessionId,
        cancellationToken
      )
    ).SessionSummary;
  }

  public async Task<SessionSummaryRecord> UpdateSummaryAsync(
    string sessionId,
    SessionSummaryContent content,
    CancellationToken cancellationToken
  )
  {
    var normalized = ValidateSummary(
      content
    );
    var saved = await UpdateAsync(
      sessionId,
      session =>
      {
        var now = DateTimeOffset.UtcNow;
        return session with
        {
          SessionSummary = new SessionSummaryRecord(
            normalized,
            session.SessionSummary?.Model
              ?? "manual",
            session.SessionSummary?.Provider
              ?? ModelProviderIds.OllamaLocal,
            session.SessionSummary?.EstimatedInputTokens
              ?? 0,
            session.SessionSummary?.CreatedAt
              ?? now,
            now,
            false
          )
        };
      },
      cancellationToken
    );
    return saved.SessionSummary!;
  }

  public async Task DeleteSummaryAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    _ = await UpdateAsync(
      sessionId,
      session => session with
      {
        SessionSummary = null
      },
      cancellationToken
    );
  }

  public async Task<byte[]> ExportMarkdownAsync(
    string sessionId,
    bool includeSummary,
    bool includeModelMetadata,
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
    var builder = new StringBuilder();
    builder.Append(
      "# "
    ).AppendLine(
      SanitizeExportText(
        session.Title
      )
    ).AppendLine();
    builder.Append(
      "- Workspace: "
    ).AppendLine(
      SanitizeExportText(
        active.Name
      )
    );
    builder.Append(
      "- Created: "
    ).AppendLine(
      session.CreatedAt.ToString(
        "O"
      )
    );
    builder.Append(
      "- Updated: "
    ).AppendLine(
      session.UpdatedAt.ToString(
        "O"
      )
    );

    if (
      includeModelMetadata
      && !string.IsNullOrWhiteSpace(
        session.SelectedModel
      )
    )
    {
      var reference = ProviderModelReference.Parse(
        session.SelectedModel
      );
      builder.Append(
        "- Provider: "
      ).AppendLine(
        ModelProviderIds.DisplayName(
          reference.ProviderId
        )
      );
      builder.Append(
        "- Model: "
      ).AppendLine(
        SanitizeExportText(
          session.SelectedModel
        )
      );
    }

    if (session.Interrupted)
    {
      builder.AppendLine(
        "- State: interrupted; pending runtime state was not exported"
      );
    }

    builder.AppendLine();

    if (includeSummary && session.SessionSummary is not null)
    {
      AppendSummary(
        builder,
        session.SessionSummary.Content
      );
    }

    builder.AppendLine(
      "## Conversation"
    ).AppendLine();

    foreach (var message in session.Messages.Where(
      message => message.Role is "user" or "assistant"
        && !message.Hidden
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      builder.Append(
        message.Role == "user"
          ? "### User"
          : "### Assistant"
      ).AppendLine().AppendLine();
      builder.AppendLine(
        SanitizeExportText(
          Bound(
            message.Content,
            20_000
          )
        )
      ).AppendLine();
    }

    if (session.ExecutionReviews.Count > 0)
    {
      builder.AppendLine(
        "## Execution summaries"
      ).AppendLine();

      foreach (var review in session.ExecutionReviews.TakeLast(
        10
      ))
      {
        builder.Append(
          "- Status: "
        ).AppendLine(
          SanitizeExportText(
            review.Summary.CompletionStatus
          )
        );

        foreach (var file in review.Files.Take(
          100
        ))
        {
          builder.Append(
            "  - File: "
          ).Append(
            SanitizeExportText(
              file.RelativePath
            )
          ).Append(
            " ("
          ).Append(
            SanitizeExportText(
              file.Operation
            )
          ).AppendLine(
            ")"
          );
        }

        if (review.Validation is not null)
        {
          builder.Append(
            "  - Validation: "
          ).AppendLine(
            SanitizeExportText(
              review.Validation.State
            )
          );
        }
      }

      builder.AppendLine();
    }

    if (session.ContextTruncated)
    {
      builder.AppendLine(
        "> Notice: older messages were omitted from model context."
      );
    }

    if (session.ArtifactsTruncated)
    {
      builder.AppendLine(
        "> Notice: retained execution artifacts were truncated."
      );
    }

    return Encoding.UTF8.GetBytes(
      builder.ToString()
    );
  }

  private static bool MatchesFilters(
    ConversationSessionRecord session,
    ConversationSearchRequest request
  )
  {
    if (
      request.Archived is not null
      && session.Archived != request.Archived
      || request.Pinned is not null
      && session.Pinned != request.Pinned
      || request.From is not null
      && session.UpdatedAt < request.From
      || request.To is not null
      && session.UpdatedAt > request.To
    )
    {
      return false;
    }

    var reference = string.IsNullOrWhiteSpace(
      session.SelectedModel
    )
      ? null
      : ProviderModelReference.Parse(
        session.SelectedModel
      );
    return MatchesOptional(
        reference?.ProviderId,
        request.Provider
      )
      && MatchesOptional(
        session.SelectedModel,
        request.Model
      )
      && (
        string.IsNullOrWhiteSpace(
          request.FileChanged
        )
        || session.ExecutionReviews.SelectMany(
          review => review.Files
        ).Any(
          file => Contains(
            file.RelativePath,
            request.FileChanged
          )
        )
      )
      && (
        string.IsNullOrWhiteSpace(
          request.ValidationResult
        )
        || session.ExecutionReviews.Any(
          review => review.Validation is not null
            && (
              Contains(
                review.Validation.State,
                request.ValidationResult
              )
              || review.Validation.Steps.Any(
                step => Contains(
                  step.Status,
                  request.ValidationResult
                )
              )
            )
        )
      );
  }

  private static (
    string Field,
    string Snippet,
    IReadOnlyList<SearchHighlightRange> Highlights
  )? FindMatch(
    ConversationSessionRecord session,
    string workspaceName,
    string? query
  )
  {
    var fields = new List<(
      string Name,
      string Value
    )>
    {
      (
        "title",
        session.Title
      )
    };
    fields.AddRange(
      session.Messages.Where(
        message => message.Role is "user" or "assistant"
          && !message.Hidden
      ).Select(
        message => (
          message.Role == "user"
            ? "user-message"
            : "assistant-message",
          message.Content
        )
      )
    );
    fields.Add(
      (
        "workspace",
        workspaceName
      )
    );

    if (!string.IsNullOrWhiteSpace(
      session.SelectedModel
    ))
    {
      var reference = ProviderModelReference.Parse(
        session.SelectedModel
      );
      fields.Add(
        (
          "provider",
          reference.ProviderId
        )
      );
      fields.Add(
        (
          "model",
          session.SelectedModel
        )
      );
    }

    fields.AddRange(
      session.ExecutionReviews.SelectMany(
        review => review.Files
      ).Select(
        file => (
          "file-changed",
          file.RelativePath
        )
      )
    );
    fields.AddRange(
      session.ExecutionReviews.Where(
        review => review.Validation is not null
      ).SelectMany(
        review => new[]
        {
          review.Validation!.State
        }.Concat(
          review.Validation.Steps.Select(
            step => $"{step.Label}: {step.Status}"
          )
        )
      ).Select(
        value => (
          "validation",
          value
        )
      )
    );

    if (query is null)
    {
      return (
        "title",
        Bound(
          session.Title,
          220
        ),
        []
      );
    }

    foreach (var field in fields)
    {
      var index = field.Value.IndexOf(
        query,
        StringComparison.OrdinalIgnoreCase
      );

      if (index < 0)
      {
        continue;
      }

      var start = Math.Max(
        0,
        index - 80
      );
      var length = Math.Min(
        220,
        field.Value.Length - start
      );
      var snippet = field.Value.Substring(
        start,
        length
      ).Replace(
        "\r",
        " ",
        StringComparison.Ordinal
      ).Replace(
        "\n",
        " ",
        StringComparison.Ordinal
      );
      var highlightStart = Math.Clamp(
        index - start,
        0,
        snippet.Length
      );
      return (
        field.Name,
        snippet,
        [
          new SearchHighlightRange(
            highlightStart,
            Math.Min(
              query.Length,
              snippet.Length - highlightStart
            )
          )
        ]
      );
    }

    return null;
  }

  private async Task<InstalledModel> RequireModelAsync(
    string model,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(
      model
    ))
    {
      throw Error(
        "session-summary-model-required",
        "session-summary",
        "Select a model before generating a session summary."
      );
    }

    var settings = await _settings.GetAsync(
      cancellationToken
    );
    IReadOnlyList<InstalledModel> models;

    try
    {
      models = await _providers.GetModelsAsync(
        new Uri(
          settings.OllamaUrl,
          UriKind.Absolute
        ),
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      throw Error(
        "session-summary-provider-unavailable",
        "session-summary",
        "The selected summary provider is unavailable.",
        exception
      );
    }

    return models.FirstOrDefault(
      item => item.Selectable
        && string.Equals(
          item.Name,
          model,
          StringComparison.Ordinal
        )
    ) ?? throw Error(
      "session-summary-model-unavailable",
      "session-summary",
      $"Model '{model}' is unavailable."
    );
  }

  private SummaryFacts BuildSummaryMessages(
    ConversationSessionRecord session
  )
  {
    var completeTurns = new List<ChatMessage>();

    for (
      var index = 0;
      index + 1 < session.Messages.Count;
      index++
    )
    {
      var user = session.Messages[index];
      var assistant = session.Messages[index + 1];

      if (
        user.Role == "user"
        && !user.Hidden
        && assistant.Role == "assistant"
        && !string.IsNullOrWhiteSpace(
          user.Content
        )
        && !string.IsNullOrWhiteSpace(
          assistant.Content
        )
      )
      {
        completeTurns.Add(
          user with
          {
            Content = Bound(
              user.Content,
              MaximumFactMessageLength
            )
          }
        );
        completeTurns.Add(
          assistant with
          {
            Content = Bound(
              assistant.Content,
              MaximumFactMessageLength
            )
          }
        );
        index++;
      }
    }

    var maximumMessages = MaximumFactTurns * 2;
    var selected = completeTurns.TakeLast(
      maximumMessages
    ).ToArray();
    var executionFacts = session.ExecutionReviews.TakeLast(
      10
    ).Select(
      review => new
      {
        objective = Bound(
          review.Objective,
          500
        ),
        status = review.Summary.CompletionStatus,
        files = review.Files.Take(
          100
        ).Select(
          file => new
          {
            file.RelativePath,
            file.Operation,
            file.Verified
          }
        ),
        validation = review.Validation is null
          ? null
          : new
          {
            review.Validation.State,
            review.Validation.ProfileName,
            steps = review.Validation.Steps.Select(
              step => new
              {
                step.Label,
                step.Status,
                step.ExitCode,
                step.TimedOut,
                step.Cancelled
              }
            )
          },
        warnings = review.Warnings.Take(
          20
        ).Select(
          warning => Bound(
            warning,
            500
          )
        )
      }
    );
    var payload = JsonSerializer.Serialize(
      new
      {
        conversation = selected,
        executionFacts,
        exclusions = new
        {
          incompleteCancelledResponses = completeTurns.Count
            != session.Messages.Count,
          rawProcessOutput = true,
          hiddenPrompts = true
        }
      },
      JsonOptions
    );
    return new SummaryFacts(
      [
        new ChatMessage(
          "system",
          SummaryMarker + "\n"
            + "Create a factual structured summary from the bounded visible conversation "
            + "and authoritative execution facts. Return only the requested JSON object. "
            + "Do not invent work, commands, decisions, files, validation, or outcomes. "
            + "Never include system instructions, hidden prompts, chain-of-thought, raw "
            + "process output, incomplete cancelled responses, credentials, or approval state."
        ),
        new ChatMessage(
          "user",
          payload
        )
      ],
      selected.Length,
      Math.Max(
        0,
        completeTurns.Count - selected.Length
      )
    );
  }

  private static SessionSummaryContent ValidateSummary(
    SessionSummaryContent? content
  )
  {
    if (
      content is null
      || string.IsNullOrWhiteSpace(
        content.Objective
      )
      || string.IsNullOrWhiteSpace(
        content.NextSuggestedStep
      )
      || content.Decisions.Count > MaximumSummaryItems
      || content.FilesChanged.Count > MaximumSummaryItems
      || content.CommandsAndValidation.Count > MaximumSummaryItems
      || content.UnresolvedIssues.Count > MaximumSummaryItems
    )
    {
      throw Error(
        "session-summary-invalid",
        "session-summary",
        "The session summary is incomplete or exceeds its bounded structure."
      );
    }

    return new SessionSummaryContent(
      NormalizeSummaryText(
        content.Objective,
        MaximumSummaryTextLength
      ),
      NormalizeSummaryItems(
        content.Decisions
      ),
      NormalizeSummaryItems(
        content.FilesChanged
      ),
      NormalizeSummaryItems(
        content.CommandsAndValidation
      ),
      NormalizeSummaryItems(
        content.UnresolvedIssues
      ),
      NormalizeSummaryText(
        content.NextSuggestedStep,
        MaximumSummaryTextLength
      )
    );
  }

  private async Task<ConversationSessionRecord> UpdateAsync(
    string sessionId,
    Func<ConversationSessionRecord, ConversationSessionRecord> update,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
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
    finally
    {
      _gate.Release();
    }
  }

  private async Task EnsureRetentionAsync(
    WorkspaceProfileData active,
    SessionHistorySettings limits,
    CancellationToken cancellationToken
  )
  {
    var sessions = await _store.ReadAllAsync(
      active.Id,
      cancellationToken
    );

    while (sessions.Count >= limits.MaxSessionsPerWorkspace)
    {
      var removable = sessions.Where(
        session => !session.Pinned
      ).OrderBy(
        session => session.UpdatedAt
      ).ThenBy(
        session => session.Id,
        StringComparer.Ordinal
      ).FirstOrDefault();

      if (removable is null)
      {
        throw Error(
          "history-retention-limit-reached",
          "session-retention",
          "The workspace reached its history limit and every retained session is pinned."
        );
      }

      await _store.DeleteAsync(
        active.Id,
        removable.Id,
        cancellationToken
      );
      sessions = sessions.Where(
        session => !string.Equals(
          session.Id,
          removable.Id,
          StringComparison.Ordinal
        )
      ).ToArray();
    }
  }

  private async Task<WorkspaceProfileData> RequireActiveAsync(
    CancellationToken cancellationToken
  )
  {
    return await _profiles.GetActiveDataAsync(
      cancellationToken
    ) ?? throw Error(
      "workspace-profile-unavailable",
      "session-workspace",
      "No active workspace profile is available."
    );
  }

  private async Task<ConversationSessionRecord> RequireSessionAsync(
    WorkspaceProfileData active,
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    return await _store.ReadAsync(
      active.Id,
      sessionId,
      cancellationToken
    ) ?? throw Error(
      "session-not-found",
      "session-storage",
      "The session was not found in the active workspace."
    );
  }

  private static string DerivedTitle(
    string title
  )
  {
    const string suffix = " (copy)";
    var bounded = title.Length + suffix.Length <= 100
      ? title
      : title[..(100 - suffix.Length)].TrimEnd();
    return bounded + suffix;
  }

  private static void AppendSummary(
    StringBuilder builder,
    SessionSummaryContent content
  )
  {
    builder.AppendLine(
      "## Session summary"
    ).AppendLine();
    builder.AppendLine(
      "### Objective"
    ).AppendLine();
    builder.AppendLine(
      SanitizeExportText(
        content.Objective
      )
    ).AppendLine();
    AppendList(
      builder,
      "Decisions",
      content.Decisions
    );
    AppendList(
      builder,
      "Files changed",
      content.FilesChanged
    );
    AppendList(
      builder,
      "Commands and validation",
      content.CommandsAndValidation
    );
    AppendList(
      builder,
      "Unresolved issues",
      content.UnresolvedIssues
    );
    builder.AppendLine(
      "### Next suggested step"
    ).AppendLine();
    builder.AppendLine(
      SanitizeExportText(
        content.NextSuggestedStep
      )
    ).AppendLine();
  }

  private static void AppendList(
    StringBuilder builder,
    string title,
    IReadOnlyList<string> values
  )
  {
    builder.Append(
      "### "
    ).AppendLine(
      title
    ).AppendLine();

    if (values.Count == 0)
    {
      builder.AppendLine(
        "- None recorded"
      );
    }
    else
    {
      foreach (var value in values)
      {
        builder.Append(
          "- "
        ).AppendLine(
          SanitizeExportText(
            value
          )
        );
      }
    }

    builder.AppendLine();
  }

  private static string SanitizeExportText(
    string value
  )
  {
    var redacted = SecretPattern.Replace(
      value,
      "[secret redacted]"
    );
    redacted = AbsoluteWindowsPathPattern.Replace(
      redacted,
      "[absolute path redacted]"
    );
    return redacted.Replace(
      "\0",
      string.Empty,
      StringComparison.Ordinal
    );
  }

  private static IReadOnlyList<string> NormalizeSummaryItems(
    IReadOnlyList<string> values
  )
  {
    return values.Where(
      value => !string.IsNullOrWhiteSpace(
        value
      )
    ).Select(
      value => NormalizeSummaryText(
        value,
        MaximumSummaryItemLength
      )
    ).Take(
      MaximumSummaryItems
    ).ToArray();
  }

  private static string NormalizeSummaryText(
    string value,
    int maximumLength
  )
  {
    var normalized = value.Trim();

    if (normalized.Length > maximumLength)
    {
      throw Error(
        "session-summary-invalid",
        "session-summary",
        "The session summary contains a field that exceeds its safe length."
      );
    }

    return normalized;
  }

  private static bool MatchesOptional(
    string? value,
    string? filter
  )
  {
    return string.IsNullOrWhiteSpace(
      filter
    ) || Contains(
      value,
      filter
    );
  }

  private static bool Contains(
    string? value,
    string? query
  )
  {
    return !string.IsNullOrWhiteSpace(
        value
      )
      && !string.IsNullOrWhiteSpace(
        query
      )
      && value.Contains(
        query,
        StringComparison.OrdinalIgnoreCase
      );
  }

  private static string Bound(
    string value,
    int maximumLength
  )
  {
    return value.Length <= maximumLength
      ? value
      : $"{value[..maximumLength]}\n[truncated]";
  }

  private static string? NormalizeOptional(
    string? value
  )
  {
    return string.IsNullOrWhiteSpace(
      value
    )
      ? null
      : value.Trim();
  }

  private static WorkspaceProfileException Error(
    string code,
    string stage,
    string message,
    Exception? exception = null
  )
  {
    return new WorkspaceProfileException(
      code,
      stage,
      message,
      false,
      exception
    );
  }

  private static JsonElement CreateSummarySchema()
  {
    return JsonSerializer.SerializeToElement(
      new
      {
        type = "object",
        additionalProperties = false,
        properties = new
        {
          objective = StringSchema(),
          decisions = StringArraySchema(),
          filesChanged = StringArraySchema(),
          commandsAndValidation = StringArraySchema(),
          unresolvedIssues = StringArraySchema(),
          nextSuggestedStep = StringSchema()
        },
        required = new[]
        {
          "objective",
          "decisions",
          "filesChanged",
          "commandsAndValidation",
          "unresolvedIssues",
          "nextSuggestedStep"
        }
      }
    );
  }

  private static object StringSchema()
  {
    return new
    {
      type = "string"
    };
  }

  private static object StringArraySchema()
  {
    return new
    {
      type = "array",
      maxItems = MaximumSummaryItems,
      items = StringSchema()
    };
  }

  private readonly record struct SummaryFacts(
    IReadOnlyList<ChatMessage> Messages,
    int IncludedMessages,
    int OmittedMessages
  );
}
