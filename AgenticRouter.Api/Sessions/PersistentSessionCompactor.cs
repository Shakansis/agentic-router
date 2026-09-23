using System.Security.Cryptography;
using System.Text;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Sessions;

internal sealed record SessionCompactionResult(
  ConversationSessionRecord Session,
  int BeforeBytes,
  int AfterBytes,
  bool Compacted
);

internal static class PersistentSessionCompactor
{
  internal const string ContextMarker =
    "AGENTIC_ROUTER_PERSISTED_HISTORY_COMPACTION_V1";

  private const int MaximumContextEntryCharacters = 4_096;
  private const int MaximumContextEntriesPerSection = 64;
  private const int MaximumRetainedMessageCharacters = 65_536;

  // An inference projection only: callers retain the original transcript and stored history.
  public static IReadOnlyList<ChatMessage>? FitHistoryToInput(
    IReadOnlyList<ChatMessage> history, long budget, ITokenEstimator tokens)
  {
    if (tokens.EstimateMessages(history) <= budget)
      return history;

    var context = new ContinuityContextBuilder(null, preserveRequirements: true);
    foreach (var message in history)
    {
      if (IsCompactionContext(message)) context.AddPriorContext(message.Content);
      else context.AddMessage(message);
    }
    for (var characters = MaximumContextEntryCharacters; characters >= 256; characters /= 2)
    {
      var candidate = new[] { new ChatMessage("assistant", context.Render(history.Count, 0, 0, characters)) };
      if (tokens.EstimateMessages(candidate) <= budget) return candidate;
    }
    // Keep requirements intact even when no optional detail can be retained.
    for (var entries = MaximumContextEntriesPerSection / 2; entries >= 0; entries = entries == 0 ? -1 : entries / 2)
    {
      var candidate = new[] { new ChatMessage("assistant", context.Render(history.Count, 0, 0, 256, entries)) };
      if (tokens.EstimateMessages(candidate) <= budget) return candidate;
    }
    return null;
  }

  public static SessionCompactionResult Compact(
    ConversationSessionRecord session,
    int targetBytes,
    Func<ConversationSessionRecord, int> measureBytes
  )
  {
    var beforeBytes = measureBytes(
      session
    );
    if (beforeBytes <= targetBytes)
    {
      return new SessionCompactionResult(
        session,
        beforeBytes,
        beforeBytes,
        false
      );
    }

    var context = new ContinuityContextBuilder(
      session
    );
    var messages = session.Messages.ToList();
    var reviews = session.ExecutionReviews.ToList();
    var rollbacks = (session.ExecutionRollbacks ?? []).ToList();
    var compactedMessages = 0;
    var compactedReviews = 0;
    var compactedRollbacks = 0;
    var artifactsCompacted = false;

    for (var index = messages.Count - 1; index >= 0; index--)
    {
      if (!IsCompactionContext(
        messages[index]
      ))
      {
        continue;
      }

      context.AddPriorContext(
        messages[index].Content
      );
      messages.RemoveAt(
        index
      );
    }

    ConversationSessionRecord Candidate()
    {
      var persistedMessages = messages.ToList();
      if (context.HasContent)
      {
        persistedMessages.Insert(
          0,
          new ChatMessage(
            "assistant",
            context.Render(
              compactedMessages,
              compactedReviews,
              compactedRollbacks
            ),
            TurnId: $"compaction-{session.Id}"
          )
        );
      }

      return session with
      {
        Messages = persistedMessages,
        ExecutionReviews = reviews,
        ExecutionRollbacks = rollbacks,
        ContextTruncated = true,
        ArtifactsTruncated = session.ArtifactsTruncated || artifactsCompacted,
        StorageBytes = 0
      };
    }

    var candidate = Candidate();
    while (measureBytes(candidate) > targetBytes)
    {
      if (TryRemoveOldestTurn(
        messages,
        context,
        out var removedMessages
      ))
      {
        compactedMessages += removedMessages;
        candidate = Candidate();
        continue;
      }

      if (reviews.Count > 1)
      {
        context.AddReview(
          reviews[0]
        );
        reviews.RemoveAt(
          0
        );
        compactedReviews++;
        artifactsCompacted = true;
        candidate = Candidate();
        continue;
      }

      if (rollbacks.Count > 1)
      {
        context.AddRollback(
          rollbacks[0]
        );
        rollbacks.RemoveAt(
          0
        );
        compactedRollbacks++;
        artifactsCompacted = true;
        candidate = Candidate();
        continue;
      }

      if (TryStripOldestDetailedPayload(
        messages,
        context
      ))
      {
        artifactsCompacted = true;
        candidate = Candidate();
        continue;
      }

      if (reviews.Count > 0)
      {
        context.AddReview(
          reviews[0]
        );
        reviews.RemoveAt(
          0
        );
        compactedReviews++;
        artifactsCompacted = true;
        candidate = Candidate();
        continue;
      }

      if (rollbacks.Count > 0)
      {
        context.AddRollback(
          rollbacks[0]
        );
        rollbacks.RemoveAt(
          0
        );
        compactedRollbacks++;
        artifactsCompacted = true;
        candidate = Candidate();
        continue;
      }

      if (TryCompactOldestMessageContent(
        messages,
        context
      ))
      {
        compactedMessages++;
        candidate = Candidate();
        continue;
      }

      break;
    }

    var afterBytes = measureBytes(
      candidate
    );
    return new SessionCompactionResult(
      candidate,
      beforeBytes,
      afterBytes,
      true
    );
  }

  private static bool TryRemoveOldestTurn(
    List<ChatMessage> messages,
    ContinuityContextBuilder context,
    out int removedMessages
  )
  {
    removedMessages = 0;
    if (messages.Count < 2)
    {
      return false;
    }

    var nextUser = messages.FindIndex(
      1,
      message => message.Role == "user"
    );
    var removeCount = nextUser > 0
      ? nextUser
      : messages.Count - 1;
    if (removeCount <= 0)
    {
      return false;
    }

    var removed = messages.Take(
      removeCount
    ).ToArray();
    foreach (var message in removed)
    {
      context.AddMessage(
        message
      );
    }
    messages.RemoveRange(
      0,
      removeCount
    );
    removedMessages = removeCount;
    return true;
  }

  private static bool TryStripOldestDetailedPayload(
    List<ChatMessage> messages,
    ContinuityContextBuilder context
  )
  {
    for (var index = 0; index < messages.Count; index++)
    {
      var message = messages[index];
      if (
        message.Timeline is not { Count: > 0 }
        && message.ContentBlocks is not { Count: > 0 }
        && string.IsNullOrWhiteSpace(
          message.RenderedHtml
        )
      )
      {
        continue;
      }

      context.AddMessage(
        message
      );
      messages[index] = message with
      {
        Timeline = null,
        ContentBlocks = null,
        RenderedHtml = null
      };
      return true;
    }

    return false;
  }

  private static bool TryCompactOldestMessageContent(
    List<ChatMessage> messages,
    ContinuityContextBuilder context
  )
  {
    for (var index = 0; index < messages.Count; index++)
    {
      var message = messages[index];
      if (message.Content.Length <= MaximumRetainedMessageCharacters)
      {
        continue;
      }

      context.AddMessage(
        message
      );
      messages[index] = message with
      {
        Content = "[Message compacted at a semantic text boundary.]\n"
          + CompactText(
            message.Content,
            MaximumRetainedMessageCharacters
          ),
        Timeline = null,
        ContentBlocks = null,
        RenderedHtml = null
      };
      return true;
    }

    return false;
  }

  private static bool IsCompactionContext(
    ChatMessage message
  )
  {
    return message.Role == "assistant"
      && message.Content.StartsWith(
        ContextMarker,
        StringComparison.Ordinal
      );
  }

  private static string CompactText(
    string value,
    int maximumCharacters
  )
  {
    var normalized = value.Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ).Trim();
    if (normalized.Length <= maximumCharacters)
    {
      return normalized;
    }

    var digest = Convert.ToHexString(
      SHA256.HashData(
        Encoding.UTF8.GetBytes(
          normalized
        )
      )
    ).ToLowerInvariant();
    var suffix = $"\n… [compacted; sha256:{digest}]\n";
    var contentBudget = Math.Max(
      256,
      maximumCharacters - suffix.Length
    );
    var prefixBudget = contentBudget * 3 / 4;
    var tailBudget = contentBudget - prefixBudget;
    var prefix = PrefixAtBoundary(
      normalized,
      prefixBudget
    );
    var tail = SuffixAtBoundary(
      normalized,
      tailBudget
    );
    return prefix + suffix + tail;
  }

  private static string PrefixAtBoundary(
    string value,
    int maximumCharacters
  )
  {
    if (value.Length <= maximumCharacters)
    {
      return value;
    }

    var boundary = value.LastIndexOfAny(
      ['\n', '.', ';', ' '],
      maximumCharacters - 1,
      maximumCharacters
    );
    return value[..Math.Max(
      1,
      boundary + 1
    )].TrimEnd();
  }

  private static string SuffixAtBoundary(
    string value,
    int maximumCharacters
  )
  {
    if (value.Length <= maximumCharacters)
    {
      return value;
    }

    var start = value.Length - maximumCharacters;
    var boundary = value.IndexOfAny(
      ['\n', '.', ';', ' '],
      start,
      maximumCharacters
    );
    return value[Math.Min(
      value.Length - 1,
      boundary < 0 ? start : boundary + 1
    )..].TrimStart();
  }

  private sealed class ContinuityContextBuilder
  {
    private readonly Section _requirements = new(
      "Requirements and constraints"
    );
    private readonly Section _facts = new(
      "Decisions and relevant facts"
    );
    private readonly Section _changes = new(
      "Important file and workspace changes"
    );
    private readonly Section _validation = new(
      "Commands and validation"
    );
    private readonly Section _unresolved = new(
      "Unresolved work and failures"
    );
    private readonly ConversationSessionRecord? _session;
    private readonly bool _preserveRequirements;

    public ContinuityContextBuilder(
      ConversationSessionRecord? session,
      bool preserveRequirements = false
    )
    {
      _session = session;
      _preserveRequirements = preserveRequirements;
      if (session?.SessionSummary is not { } summary)
      {
        return;
      }

      _facts.Add(
        $"Continuity objective: {summary.Content.Objective}"
      );
      foreach (var decision in summary.Content.Decisions)
      {
        _facts.Add(
          decision
        );
      }
      foreach (var file in summary.Content.FilesChanged)
      {
        _changes.Add(
          file
        );
      }
      foreach (var command in summary.Content.CommandsAndValidation)
      {
        _validation.Add(
          command
        );
      }
      foreach (var issue in summary.Content.UnresolvedIssues)
      {
        _unresolved.Add(
          issue
        );
      }
      _unresolved.Add(
        summary.Content.NextSuggestedStep
      );
    }

    public bool HasContent => _requirements.Count > 0
      || _facts.Count > 0
      || _changes.Count > 0
      || _validation.Count > 0
      || _unresolved.Count > 0;

    public void AddPriorContext(
      string content
    )
    {
      // V1 embeds previous summaries verbatim. Read their sections instead of nesting
      // the complete document again. Indented continuation lines remain literal data.
      Section? section = null;
      var entry = new StringBuilder();
      void Flush()
      {
        if (entry.Length == 0) return;
        var target = section ?? _requirements;
        target.Add(entry.ToString(), _preserveRequirements && target == _requirements);
        entry.Clear();
      }
      foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
      {
        var heading = line switch
        {
          "Requirements and constraints:" => _requirements,
          "Decisions and relevant facts:" => _facts,
          "Important file and workspace changes:" => _changes,
          "Commands and validation:" => _validation,
          "Unresolved work and failures:" => _unresolved,
          _ => null
        };
        if (heading is not null) { Flush(); section = heading; continue; }
        if (line == ContextMarker || line == "Previously compacted continuity context:"
          || line.StartsWith("This Host-generated context ", StringComparison.Ordinal)
          || line.StartsWith("Conversation: ", StringComparison.Ordinal)
          || line.StartsWith("Persisted route: ", StringComparison.Ordinal)
          || line.StartsWith("Compacted records: ", StringComparison.Ordinal)
          || line == "Optional historical details may be omitted; reinspect files and Host evidence before relying on them.")
        { Flush(); section = null; continue; }
        if (line.StartsWith("- ", StringComparison.Ordinal))
        { Flush(); entry.Append(line.AsSpan(2)); }
        else if (line.StartsWith("  ", StringComparison.Ordinal))
          entry.Append('\n').Append(line.AsSpan(2));
        else if (!string.IsNullOrWhiteSpace(line))
          entry.Append(entry.Length == 0 ? "" : "\n").Append(line);
      }
      Flush();
    }

    public void AddMessage(
      ChatMessage message
    )
    {
      if (!string.IsNullOrWhiteSpace(
        message.Content
      ))
      {
        (message.Role == "user"
          ? _requirements
          : _facts).Add(
            message.Content,
            _preserveRequirements && message.Role == "user"
          );
      }

      if (message.Timeline is not { Count: > 0 } timeline)
      {
        return;
      }

      var response = string.Concat(
        timeline.Where(
          item => item.Type == "response.delta"
        ).Select(
          item => item.Delta
        )
      );
      _facts.Add(
        response
      );
      foreach (var item in timeline)
      {
        if (item.LocalAction is { } action)
        {
          var paths = action.RelativePaths is { Count: > 0 }
            ? $" Paths: {string.Join(", ", action.RelativePaths)}."
            : string.Empty;
          var actionFact = $"{action.Tool}: {action.Summary}; state={action.State}.{paths}";
          if (action.RelativePaths is { Count: > 0 })
          {
            _changes.Add(
              actionFact
            );
          }
          else
          {
            _facts.Add(
              actionFact
            );
          }
        }

        if (item.Error is { } error)
        {
          _unresolved.Add(
            $"{error.Stage}: {error.Message}"
          );
        }
        else if (
          item.Type.Contains(
            "failed",
            StringComparison.OrdinalIgnoreCase
          )
          || item.Type.Contains(
            "rejected",
            StringComparison.OrdinalIgnoreCase
          )
          || item.Type.Contains(
            "cancel",
            StringComparison.OrdinalIgnoreCase
          )
          || item.Type.Contains(
            "warning",
            StringComparison.OrdinalIgnoreCase
          )
        )
        {
          _unresolved.Add(
            item.Message ?? item.Type
          );
        }
        else if (!string.IsNullOrWhiteSpace(
          item.SpecialistCompletion
        ))
        {
          _facts.Add(
            item.SpecialistCompletion
          );
        }
      }
    }

    public void AddReview(
      ExecutionSessionReview review
    )
    {
      _facts.Add(
        $"Execution objective: {review.Objective}; state={review.Summary.State}; "
          + $"completion={review.Summary.CompletionStatus}."
      );
      foreach (var file in review.Files)
      {
        _changes.Add(
          $"{file.Operation}: {file.RelativePath}; verified={file.Verified}; "
            + $"final-size={file.FinalSizeBytes}."
        );
      }
      foreach (var process in review.Processes)
      {
        _validation.Add(
          $"{process.Executable} {string.Join(" ", process.Arguments)}; "
            + $"exit={process.ExitCode?.ToString() ?? "unavailable"}; "
            + $"timed-out={process.TimedOut}; cancelled={process.Cancelled}."
        );
      }
      foreach (var warning in review.Warnings)
      {
        _unresolved.Add(
          warning
        );
      }
      if (review.Validation is { } validation)
      {
        _validation.Add(
          $"Validation state: {validation.State}; profile={validation.ProfileName ?? "none"}."
        );
        foreach (var step in validation.Steps)
        {
          _validation.Add(
            $"{step.Label}: {step.Status}; exit={step.ExitCode?.ToString() ?? "unavailable"}."
          );
        }
      }
      foreach (var conflict in review.Conflicts ?? [])
      {
        _unresolved.Add(
          $"Conflict: {conflict.RelativePath}; stage={conflict.Stage}."
        );
      }
    }

    public void AddRollback(
      ExecutionSessionPersistenceSnapshot rollback
    )
    {
      _facts.Add(
        $"Retained execution snapshot: {rollback.Objective}; state={rollback.State}."
      );
      foreach (var file in rollback.Files)
      {
        _changes.Add(
          $"{file.Operation}: {file.RelativePath}; verified={file.Verified}; "
            + $"undo-available={file.UndoAvailable}."
        );
      }
    }

    public string Render(
      int compactedMessages,
      int compactedReviews,
      int compactedRollbacks,
      int entryCharacters = MaximumContextEntryCharacters,
      int optionalEntries = MaximumContextEntriesPerSection
    )
    {
      var builder = new StringBuilder();
      builder.AppendLine(
        ContextMarker
      );
      builder.AppendLine(
        "This Host-generated context preserves continuity from older conversation history compacted at semantic boundaries."
      );
      builder.AppendLine(
        $"Conversation: {_session?.Title ?? "Current conversation"}"
      );
      if (_session is not null)
        builder.AppendLine(
          $"Persisted route: mode={_session.LastInteractionMode}; model={_session.SelectedModel ?? "auto"}; "
            + $"harness={_session.SelectedHarness}; strategy={_session.LastExecutionStrategy}; "
            + $"approval={_session.LastApprovalPolicy}."
        );
      builder.AppendLine(
        $"Compacted records: messages={compactedMessages}; reviews={compactedReviews}; "
          + $"rollback-snapshots={compactedRollbacks}."
      );
      builder.AppendLine("Optional historical details may be omitted; reinspect files and Host evidence before relying on them.");
      _requirements.Render(
        builder, _preserveRequirements ? int.MaxValue : entryCharacters,
        _preserveRequirements ? int.MaxValue : optionalEntries
      );
      _facts.Render(
        builder, entryCharacters, optionalEntries
      );
      _changes.Render(
        builder, entryCharacters, optionalEntries
      );
      _validation.Render(
        builder, entryCharacters, optionalEntries
      );
      _unresolved.Render(
        builder, entryCharacters, optionalEntries
      );
      return builder.ToString().TrimEnd();
    }
  }

  private sealed class Section(
    string title
  )
  {
    private readonly HashSet<string> _seen = new(
      StringComparer.Ordinal
    );
    private readonly List<string> _values = [];

    public int Count => _values.Count;

    public void Add(
      string? value,
      bool preserve = false
    )
    {
      if (
        string.IsNullOrWhiteSpace(
          value
        )
      )
      {
        return;
      }

      var compact = preserve ? value.Trim() : CompactText(
        value,
        MaximumContextEntryCharacters
      );
      if (_seen.Add(
        compact
      ))
      {
        if (!preserve && _values.Count >= MaximumContextEntriesPerSection)
        {
          _seen.Remove(_values[0]);
          _values.RemoveAt(0);
        }
        _values.Add(
          compact
        );
      }
    }

    public void Render(
      StringBuilder builder,
      int entryCharacters = MaximumContextEntryCharacters,
      int maximumEntries = MaximumContextEntriesPerSection
    )
    {
      if (_values.Count == 0)
      {
        return;
      }

      builder.AppendLine();
      builder.AppendLine(
        $"{title}:"
      );
      foreach (var value in _values.TakeLast(maximumEntries))
      {
        builder.Append("- ");
        builder.AppendLine(
          CompactText(value, entryCharacters).Replace(
            "\n",
            "\n  ",
            StringComparison.Ordinal
          )
        );
      }
    }
  }
}
