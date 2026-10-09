using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.Usage;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Execution;

public sealed record CompletionCheckRequest(string BrowserSessionId,
  string? ConversationSessionId = null, string? WorkspaceId = null);

public sealed record CompletionCheckContext(string Id, string BrowserSessionId,
  string Objective, string Answer, string Model, ProviderCallContext Usage,
  IReadOnlyList<ExecutionSessionReview> Reviews, IReadOnlyList<string>? Summary);

// Bounded volatile evidence for history-disabled turns. Saved turns use their existing timeline.
public sealed class CompletionCheckStore
{
  private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
  private readonly object _sync = new();
  public Entry Register(CompletionCheckContext context)
  {
    lock (_sync)
    {
      if (_entries.TryGetValue(context.Id, out var existing)) return existing;
      foreach (var old in _entries.Values.Where(entry => entry.Gate.CurrentCount > 0)
        .OrderBy(entry => entry.CreatedAt).Take(Math.Max(0, _entries.Count - 31)).ToArray())
        _entries.Remove(old.Context.Id);
      var entry = new Entry(context);
      _entries.Add(context.Id, entry);
      return entry;
    }
  }
  public Entry? Find(string id) { lock (_sync) return _entries.GetValueOrDefault(id); }
  public sealed class Entry(CompletionCheckContext context)
  {
    public CompletionCheckContext Context { get; } = context;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public ExecutionCompletionReport? Report { get; set; }
  }
}

public sealed class CompletionCheckService(CompletionCheckStore checks,
  ExecutionCompletionReportService reports, ISettingsStore settings,
  IPersistentSessionService history)
{
  public async Task<ExecutionCompletionReport?> CheckAsync(string id, CompletionCheckRequest request,
    CancellationToken cancellationToken)
  {
    var entry = checks.Find(id);
    ConversationSessionRecord? saved = null;
    if (request.ConversationSessionId is { } conversation && request.WorkspaceId is { } workspace)
    {
      try
      {
        saved = await history.OpenReadOnlyAsync(workspace, conversation, cancellationToken);
      }
      catch (WorkspaceProfileException exception) when (exception.Code == "session-not-found"
        && entry?.Context.BrowserSessionId == request.BrowserSessionId)
      {
        // The browser also assigns conversation IDs when history is disabled.
        // Only the owning live browser can use the retained Host evidence.
      }
    }
    if (saved is not null)
    {
      var message = saved.Messages.FirstOrDefault(message => message.Role == "assistant"
        && message.Timeline?.Any(item => item.Type == "response.completed" && item.CompletionCheckId == id) == true);
      if (message is null) return null;
      var terminal = message.Timeline!.Last(item => item.Type == "response.completed" && item.CompletionCheckId == id);
      if (terminal.CompletionReport is { } existing) return existing;
      if (entry is null)
      {
        var objective = saved.Messages.LastOrDefault(item => item.Role == "user" && item.TurnId == message.TurnId)?.Content;
        if (objective is null || terminal.SelectedModel is null) return null;
        var sessionIds = message.Timeline!.Select(item => item.ExecutionSession?.Id)
          .Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        entry = checks.Register(new CompletionCheckContext(id, request.BrowserSessionId, objective,
          message.Content, terminal.SelectedModel,
          new ProviderCallContext(saved.WorkspaceId, saved.Id, message.TurnId, null,
            UsageModelRoles.Summary, "execution-completion-report"),
          saved.ExecutionReviews.Where(review => sessionIds.Contains(review.Summary.Id)).ToArray(),
          message.Timeline!.LastOrDefault(item => item.SupervisionProgress is not null)?.SupervisionProgress?.CompletionSummary));
      }
    }
    if (entry is null || (saved is null && entry.Context.BrowserSessionId != request.BrowserSessionId)) return null;
    await entry.Gate.WaitAsync(cancellationToken);
    try
    {
      if (entry.Report is null)
      {
        var current = await settings.GetAsync(cancellationToken);
        var context = entry.Context;
        entry.Report = await reports.CreateAsync(context.Objective, context.Answer, context.Model,
          current.OllamaUrl, current, context.Usage, context.Reviews, context.Summary, cancellationToken);
      }
      if (saved is not null)
        await history.SaveCompletionReportAsync(saved.WorkspaceId, saved.Id, id, entry.Report, cancellationToken);
      return entry.Report;
    }
    finally { entry.Gate.Release(); }
  }
}
