using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkProductionExecuteRunner
{
  Task<BenchmarkHarnessEvidence> ExecuteAsync(
    string prompt,
    string model,
    string harness,
    BenchmarkWorkspace workspace,
    int contextTokens,
    string gpu,
    int turnNumber,
    string turnName,
    BenchmarkProgressContext? progress,
    CancellationToken cancellationToken
  );
}

public sealed class BenchmarkProductionExecuteRunner : IBenchmarkProductionExecuteRunner
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true
  };
  private readonly IHttpClientFactory _httpClients;
  private readonly IServer _server;
  private readonly IBenchmarkExecutionScopeRegistry _scopes;
  private readonly ILogger<BenchmarkProductionExecuteRunner> _logger;

  public BenchmarkProductionExecuteRunner(
    IHttpClientFactory httpClients,
    IServer server,
    IBenchmarkExecutionScopeRegistry scopes,
    ILogger<BenchmarkProductionExecuteRunner> logger
  )
  {
    _httpClients = httpClients;
    _server = server;
    _scopes = scopes;
    _logger = logger;
  }

  public async Task<BenchmarkHarnessEvidence> ExecuteAsync(
    string prompt,
    string model,
    string harness,
    BenchmarkWorkspace workspace,
    int contextTokens,
    string gpu,
    int turnNumber,
    string turnName,
    BenchmarkProgressContext? progress,
    CancellationToken cancellationToken
  )
  {
    var setupStartedAt = DateTimeOffset.UtcNow;
    _logger.LogInformation(
      "Benchmark turn {TurnNumber} ({TurnName}) is entering production Execute with {Model} and {Harness}.",
      turnNumber,
      turnName,
      model,
      harness
    );
    using var scope = _scopes.Register(
      workspace,
      model,
      contextTokens,
      gpu
    );
    using var client = _httpClients.CreateClient();
    client.BaseAddress = ResolveLoopbackAddress();
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.Add(BenchmarkExecutionScopeRegistry.HeaderName, scope.Token);
    var setupDuration = Elapsed(setupStartedAt);
    progress?.Publish(
      BenchmarkProgressTypeIds.Activity,
      BenchmarkLiveStateIds.Running,
      "The scenario entered the production Execute pipeline with a disposable trusted workspace.",
      BenchmarkActivityKindIds.HostValidation
    );
    var browserSessionId = $"benchmark-{workspace.Id}";
    var request = new ChatRequest(
      prompt,
      model,
      [],
      InteractionMode: "execute",
      Harness: harness,
      ApprovalPolicy: "auto",
      BrowserSessionId: browserSessionId,
      AutoModelHarness: false,
      ExecutionStrategy: "auto",
      SupervisionResumePolicy: "manual"
    );
    using var content = new StringContent(
      JsonSerializer.Serialize(request, JsonOptions),
      Encoding.UTF8,
      "application/json"
    );
    var executionStartedAt = DateTimeOffset.UtcNow;
    using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
    {
      Content = content
    };
    using var response = await client.SendAsync(
      requestMessage,
      HttpCompletionOption.ResponseHeadersRead,
      cancellationToken
    );
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var reader = new StreamReader(stream);
    var collector = new ProductionEvidenceCollector(setupDuration, turnNumber, turnName, prompt);
    try
    {
      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
        if (line is null)
        {
          break;
        }
        if (!line.StartsWith("data: ", StringComparison.Ordinal))
        {
          continue;
        }
        var streamEvent = JsonSerializer.Deserialize<ChatStreamEvent>(line[6..], JsonOptions);
        if (streamEvent is null)
        {
          continue;
        }
        var recoveriesBefore = collector.RecoveryCount;
        collector.Observe(streamEvent);
        if (collector.RecoveryCount > recoveriesBefore)
        {
          progress?.Publish(
            BenchmarkProgressTypeIds.Activity,
            BenchmarkLiveStateIds.Running,
            "The production Host observed a successful action after a surfaced tool failure.",
            BenchmarkActivityKindIds.RecoveredError
          );
        }
        Publish(progress, streamEvent);
        if (streamEvent.RecoveryDecision is not null)
        {
          collector.MarkAwaitingUserRecovery(streamEvent.RecoveryDecision);
          break;
        }
        if (IsPendingApproval(streamEvent.LocalAction))
        {
          await DecideAsync(
            client,
            browserSessionId,
            streamEvent.LocalAction!,
            approved: true,
            cancellationToken
          );
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      collector.MarkCancelled();
      response.Dispose();
    }
    collector.SetExecutionDuration(Elapsed(executionStartedAt));
    _logger.LogInformation(
      "Benchmark turn {TurnNumber} ({TurnName}) left production Execute after {DurationMilliseconds} ms.",
      turnNumber,
      turnName,
      Elapsed(executionStartedAt)
    );
    if (!string.IsNullOrWhiteSpace(collector.ExecutionSessionId))
    {
      try
      {
        var review = await client.GetFromJsonAsync<ExecutionSessionReview>(
          $"/api/execution-sessions/{Uri.EscapeDataString(collector.ExecutionSessionId)}/review",
          JsonOptions,
          cancellationToken.IsCancellationRequested
            ? CancellationToken.None
            : cancellationToken
        );
        collector.Observe(review);
      }
      catch (HttpRequestException exception)
      {
        _logger.LogDebug(exception, "Benchmark production execution review was unavailable.");
      }
    }
    return collector.CreateEvidence();
  }

  private Uri ResolveLoopbackAddress()
  {
    var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
    foreach (var address in addresses)
    {
      if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
      {
        continue;
      }
      if (uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1")
      {
        return uri;
      }
    }
    throw new InvalidOperationException("The production Host has no active HTTP loopback endpoint.");
  }

  private static bool IsPendingApproval(LocalActionEvent? action) => action is
  {
    State: "awaiting-approval",
    RequiresApproval: true,
    ExecutionSessionId: not null
  };

  private static async Task DecideAsync(
    HttpClient client,
    string browserSessionId,
    LocalActionEvent action,
    bool approved,
    CancellationToken cancellationToken
  )
  {
    using var response = await client.PostAsJsonAsync(
      $"/api/actions/{Uri.EscapeDataString(action.ActionId)}/decision",
      new ApprovalDecisionRequest(approved, browserSessionId, action.ExecutionSessionId!),
      JsonOptions,
      cancellationToken
    );
    response.EnsureSuccessStatusCode();
  }

  private static void Publish(BenchmarkProgressContext? progress, ChatStreamEvent streamEvent)
  {
    if (progress is null || !ShouldPublishLiveActivity(streamEvent))
    {
      return;
    }
    var message = streamEvent.LocalAction?.Summary
      ?? streamEvent.Message
      ?? streamEvent.SupervisionProgress?.Phase;
    if (string.IsNullOrWhiteSpace(message))
    {
      return;
    }
    progress.Publish(
      BenchmarkProgressTypeIds.Activity,
      BenchmarkLiveStateIds.Running,
      message,
      streamEvent.LocalAction is null
        ? BenchmarkActivityKindIds.Turn
        : ActivityKind(streamEvent.LocalAction.Tool)
    );
  }

  private static bool ShouldPublishLiveActivity(ChatStreamEvent streamEvent)
  {
    if (streamEvent.LocalAction is { } action)
    {
      return action.State is "completed"
        or "failed"
        or "rejected"
        or "denied"
        or "awaiting-approval";
    }
    if (streamEvent.Error is not null)
    {
      return true;
    }
    if (
      streamEvent.ContextUsage is not null
      || streamEvent.Type is "context.usage"
        or "request.heartbeat"
        or "response.delta"
        or "response.completed"
        or "reasoning.delta"
    )
    {
      return false;
    }
    if (streamEvent.Type.StartsWith("request.slow-", StringComparison.Ordinal)
      || streamEvent.Type.StartsWith("action.", StringComparison.Ordinal)
      || streamEvent.Type.Contains("recovery", StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }
    if (
      streamEvent.Type.StartsWith("execution", StringComparison.Ordinal)
      && (streamEvent.Type.Contains("completed", StringComparison.OrdinalIgnoreCase)
        || streamEvent.Type.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || streamEvent.Type.Contains("blocked", StringComparison.OrdinalIgnoreCase)
        || streamEvent.Type.Contains("cancel", StringComparison.OrdinalIgnoreCase))
    )
    {
      return true;
    }
    return streamEvent.Type.StartsWith("supervision.", StringComparison.Ordinal)
      && streamEvent.Type is not (
        "supervision.turn-reasoning"
        or "supervision.turn-commentary"
        or "supervision.turn-status"
      );
  }

  private static string ActivityKind(string tool) => tool switch
  {
    "read_file" or "list_files" or "search_text" => BenchmarkActivityKindIds.FileRead,
    "create_file" or "create_files" => BenchmarkActivityKindIds.FileCreate,
    "write_file" or "replace_text" => BenchmarkActivityKindIds.FileEdit,
    "delete_paths" => BenchmarkActivityKindIds.FileDelete,
    "run_process" => BenchmarkActivityKindIds.Process,
    _ => BenchmarkActivityKindIds.Tool
  };

  private static long Elapsed(DateTimeOffset startedAt) =>
    Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

  private sealed class ProductionEvidenceCollector
  {
    private readonly long _setupDuration;
    private readonly int _turnNumber;
    private readonly string _turnName;
    private readonly string _prompt;
    private readonly Dictionary<string, LocalActionEvent> _actions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toolSignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _actionFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextUsageView> _usage = new(StringComparer.Ordinal);
    private readonly HashSet<string> _turns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recoveryEvents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingFailedActions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _supervisorTurns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _workerTurns = new(StringComparer.Ordinal);
    private readonly List<BenchmarkToolCallEvidence> _toolTrace = [];
    private readonly HashSet<string> _toolStates = new(StringComparer.Ordinal);
    private readonly List<string> _validationCodes = [];
    private readonly StringBuilder _report = new();
    private int _repeatedToolCalls;
    private int _repeatedActions;
    private int _recoveries;
    private int _surfacedPlanningErrors;
    private bool _pendingPlanningRecovery;
    private long _executionDuration;
    private bool _completed;
    private bool _cancelled;
    private ProviderError? _error;
    private ExecutionSessionReview? _review;
    private string _resolvedStrategy = "direct";
    private string _terminalReason = "stream-ended-without-terminal";
    private string? _terminalMessage;
    private DateTimeOffset? _lastProgressAt;
    private string? _lastProgressType;
    private string? _lastProgressMessage;

    public ProductionEvidenceCollector(
      long setupDuration,
      int turnNumber,
      string turnName,
      string prompt
    )
    {
      _setupDuration = setupDuration;
      _turnNumber = turnNumber;
      _turnName = turnName;
      _prompt = prompt;
    }

    public string? ExecutionSessionId { get; private set; }

    public int RecoveryCount => _recoveries;

    public void SetExecutionDuration(long value) => _executionDuration = value;

    public void MarkAwaitingUserRecovery(RecoveryDecisionEvent recovery)
    {
      _terminalReason = "awaiting-user-recovery";
      _terminalMessage = recovery.Reason;
    }

    public void MarkCancelled()
    {
      _cancelled = true;
      _terminalReason = "request.cancelled";
    }

    public void Observe(ChatStreamEvent streamEvent)
    {
      if (streamEvent.Type is not "response.delta" and not "reasoning.delta")
      {
        _lastProgressAt = DateTimeOffset.UtcNow;
        _lastProgressType = streamEvent.Type;
        _lastProgressMessage = streamEvent.Message
          ?? (streamEvent.LocalAction is null
            ? null
            : $"{streamEvent.LocalAction.Tool}: {streamEvent.LocalAction.State}");
      }
      ExecutionSessionId = streamEvent.ExecutionSession?.Id
        ?? streamEvent.LocalAction?.ExecutionSessionId
        ?? ExecutionSessionId;
      if (streamEvent.Type == "response.delta" && streamEvent.Delta is not null)
      {
        _report.Append(streamEvent.Delta);
      }
      if (streamEvent.Type == "response.completed")
      {
        _completed = true;
        _terminalReason = streamEvent.ExecutionSession?.CompletionStatus ?? "response.completed";
        if (
          _report.Length == 0
          && !string.IsNullOrWhiteSpace(streamEvent.SpecialistCompletion)
        )
        {
          _report.Append(streamEvent.SpecialistCompletion);
        }
        else if (_report.Length == 0 && !string.IsNullOrWhiteSpace(streamEvent.ResponseTail))
        {
          _report.Append(streamEvent.ResponseTail);
        }
      }
      else if (streamEvent.Type == "request.cancelled")
      {
        _cancelled = true;
        _terminalReason = "request.cancelled";
      }
      else if (streamEvent.Type == "error")
      {
        _error = streamEvent.Error;
        _terminalReason = streamEvent.Error?.Code ?? streamEvent.Error?.Stage ?? "error";
      }
      if (streamEvent.SupervisionProgress is not null)
      {
        var supervision = streamEvent.SupervisionProgress;
        _resolvedStrategy = supervision.ExecutionStrategy;
        if (!string.IsNullOrWhiteSpace(supervision.ContextId))
        {
          _turns.Add(supervision.ContextId);
          if (string.Equals(supervision.Role, "supervisor", StringComparison.OrdinalIgnoreCase))
          {
            _supervisorTurns.Add(supervision.ContextId);
          }
          else if (!string.IsNullOrWhiteSpace(supervision.Role))
          {
            _workerTurns.Add(supervision.ContextId);
          }
        }
      }
      if (streamEvent.Type == "action.semantic-repair-requested")
      {
        var recoveryIdentity = $"{streamEvent.Type}:{streamEvent.Message}";
        if (_recoveryEvents.Add(recoveryIdentity))
        {
          _surfacedPlanningErrors++;
          _pendingPlanningRecovery = true;
        }
      }
      else if (IsRecoveryAttempt(streamEvent.Type))
      {
        var recoveryIdentity = $"{streamEvent.Type}:{streamEvent.SupervisionProgress?.EventSequence}:{streamEvent.LocalAction?.ActionId}:{streamEvent.Message}";
        if (_recoveryEvents.Add(recoveryIdentity))
        {
          _recoveries++;
        }
      }
      ObserveUsage(streamEvent);
      if (streamEvent.LocalAction is not null)
      {
        ObserveAction(streamEvent.LocalAction);
      }
    }

    public void Observe(ExecutionSessionReview? review)
    {
      if (review is null)
      {
        return;
      }
      _review = review;
      _terminalReason = review.Summary.CompletionStatus;
    }

    public BenchmarkHarnessEvidence CreateEvidence()
    {
      var executionStatus = _completed
        ? BenchmarkExecutionStatusIds.Completed
        : _cancelled
          ? BenchmarkExecutionStatusIds.Cancelled
          : BenchmarkExecutionStatusIds.Failed;
      var latestActions = _actions.Values.ToArray();
      var failed = latestActions.Count(action => action.State is "failed" or "rejected")
        + _surfacedPlanningErrors;
      var validationErrors = latestActions
        .Where(action => action.State is "failed" or "rejected")
        .Where(action => IsValidationCode(action.Code))
        .Count() + _surfacedPlanningErrors;
      var filesWritten = (_review?.Files ?? [])
        .Where(file => file.Operation is "created" or "modified")
        .Select(file => file.RelativePath)
        .Distinct(BenchmarkWorkspaceFactory.PathComparer)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
      var readActions = latestActions.Where(action => action.Tool == "read_file"
        && action.State == "completed").ToArray();
      var filesRead = readActions
        .SelectMany(action => action.RelativePaths ?? [])
        .Distinct(BenchmarkWorkspaceFactory.PathComparer)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
      var unavailableMetrics = readActions.Any(action => action.RelativePaths is null)
        ? new[] { "files_read" }
        : Array.Empty<string>();
      var input = _usage.Values.Sum(item => item.InputTokens);
      var output = _usage.Values.Sum(item => item.OutputTokens);
      var tokenProvenance = _usage.Count == 0
        ? BenchmarkEvidenceStatusIds.Unavailable
        : _usage.Values.All(item => item.Accuracy == "exact")
          ? BenchmarkEvidenceStatusIds.Measured
          : "estimated";
      var directTurns = _resolvedStrategy == "direct"
        ? Math.Max(_turns.Count, _usage.Count)
        : 0;
      var diagnostics = new BenchmarkOperationalDiagnostics(
        "real-life-operational-v1",
        latestActions.Length,
        failed,
        validationErrors,
        _repeatedToolCalls,
        _repeatedActions,
        _recoveries,
        filesRead,
        filesWritten,
        [],
        [],
        [],
        Math.Max(_turns.Count, _usage.Count),
        directTurns,
        _supervisorTurns.Count,
        _workerTurns.Count,
        _terminalReason,
        _executionDuration,
        _setupDuration,
        0,
        _usage.Count == 0 ? null : input,
        _usage.Count == 0 ? null : output,
        tokenProvenance,
        "auto",
        _resolvedStrategy,
        _validationCodes.Distinct(StringComparer.Ordinal).ToArray(),
        unavailableMetrics,
        TotalInferenceDurationNanoseconds: SumUsage(value => value.TotalDurationNanoseconds),
        LoadDurationNanoseconds: SumUsage(value => value.LoadDurationNanoseconds),
        PromptEvalDurationNanoseconds: SumUsage(value => value.PromptEvalDurationNanoseconds),
        EvalDurationNanoseconds: SumUsage(value => value.EvalDurationNanoseconds),
        LastProgressAt: _lastProgressAt,
        LastProgressType: _lastProgressType,
        LastProgressMessage: _lastProgressMessage
      );
      var error = executionStatus == BenchmarkExecutionStatusIds.Completed
        ? null
        : new BenchmarkError(
          _error?.Code ?? _terminalReason,
          _error?.Message
            ?? _terminalMessage
            ?? "Production Execute ended without a successful terminal result.",
          _error?.Stage ?? "production-execute",
          _error?.Recoverable ?? true
        );
      return new BenchmarkHarnessEvidence(
        executionStatus,
        error,
        _report.ToString(),
        latestActions.Length,
        failed,
        Math.Min(failed, _recoveries),
        diagnostics.InputTokens,
        diagnostics.OutputTokens,
        [
          new BenchmarkTurnEvidence(
            _turnNumber,
            _turnName,
            _prompt,
            executionStatus,
            _report.ToString(),
            latestActions.Length,
            failed,
            Math.Min(failed, _recoveries),
            _executionDuration
          )
        ],
        [
          new BenchmarkHostEvent(
            _turnNumber,
            "production-execute",
            "The scenario ran through the production Execute endpoint.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
              ["requestedStrategy"] = "auto",
              ["resolvedStrategy"] = _resolvedStrategy,
              ["terminalReason"] = _terminalReason
            }
          )
        ],
        _toolTrace,
        diagnostics
      );
    }

    private void ObserveAction(LocalActionEvent action)
    {
      var firstObservation = !_actions.ContainsKey(action.ActionId);
      _actions[action.ActionId] = action;
      if (!string.IsNullOrWhiteSpace(action.Code)
        && IsValidationCode(action.Code)
        && !_validationCodes.Contains(action.Code, StringComparer.Ordinal))
      {
        _validationCodes.Add(action.Code);
      }
      var canonicalState = action.State switch
      {
        "executing" or "running" => "started",
        "completed" => "completed",
        "failed" or "rejected" => "failed",
        _ => null
      };
      if (canonicalState is "failed"
        && _toolStates.Add($"{action.ActionId}:started"))
      {
        _toolTrace.Add(new BenchmarkToolCallEvidence(
          _toolTrace.Count + 1,
          _turnNumber,
          action.Tool,
          "started",
          Path: SinglePath(action)
        ));
      }
      if (canonicalState is not null
        && _toolStates.Add($"{action.ActionId}:{canonicalState}"))
      {
        _toolTrace.Add(new BenchmarkToolCallEvidence(
          _toolTrace.Count + 1,
          _turnNumber,
          action.Tool,
          canonicalState,
          Path: SinglePath(action),
          ErrorCode: action.Code
        ));
      }
      if (canonicalState == "failed" && IsSemanticAction(action.Tool))
      {
        _pendingFailedActions.Add(action.ActionId);
      }
      else if (canonicalState == "completed"
        && IsSemanticAction(action.Tool)
        && _pendingFailedActions.Count > 0
        && !_pendingFailedActions.Contains(action.ActionId))
      {
        _recoveries++;
        _pendingFailedActions.Clear();
      }
      if (
        canonicalState == "completed"
        && IsSemanticAction(action.Tool)
        && _pendingPlanningRecovery
      )
      {
        _recoveries++;
        _pendingPlanningRecovery = false;
      }
      if (!firstObservation)
      {
        return;
      }
      if (!string.IsNullOrWhiteSpace(action.ArgumentsSha256))
      {
        var signature = $"{action.Tool}:{action.ArgumentsSha256}";
        if (!_toolSignatures.Add(signature))
        {
          _repeatedToolCalls++;
        }
      }
      if (!string.IsNullOrWhiteSpace(action.ActionFingerprint)
        && IsMutation(action.Tool)
        && !_actionFingerprints.Add(action.ActionFingerprint))
      {
        _repeatedActions++;
      }
    }

    private static string? SinglePath(LocalActionEvent action) =>
      action.RelativePaths is { Count: 1 }
        ? BenchmarkWorkspaceFactory.NormalizeRelative(action.RelativePaths[0])
        : null;

    private void ObserveUsage(ChatStreamEvent streamEvent)
    {
      if (streamEvent.ContextUsage is null)
      {
        return;
      }
      var context = streamEvent.SupervisionProgress?.ContextId ?? "direct";
      var key = $"{context}:{streamEvent.ContextUsage.InferenceSequence}";
      _usage[key] = streamEvent.ContextUsage;
      _turns.Add(key);
    }

    private long? SumUsage(Func<ContextUsageView, long?> selector)
    {
      var values = _usage.Values.Select(selector).Where(value => value.HasValue).ToArray();
      return values.Length == 0 ? null : values.Sum(value => value!.Value);
    }

    private static bool IsMutation(string tool) => tool is
      "create_file" or "create_files" or "write_file" or "replace_text"
      or "delete_paths" or "move_path" or "rename_path";

    private static bool IsSemanticAction(string tool) => !tool.StartsWith(
      "codex_",
      StringComparison.OrdinalIgnoreCase
    );

    private static bool IsValidationCode(string? code) =>
      code?.Contains("validation", StringComparison.OrdinalIgnoreCase) == true
      || code?.Contains("invalid", StringComparison.OrdinalIgnoreCase) == true
      || code?.Contains("malformed", StringComparison.OrdinalIgnoreCase) == true
      || code?.Contains("argument", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsRecoveryAttempt(string type) => type is
      "action.tool-protocol-repair-requested"
      or "action.recovery-planning"
      or "agent.execution-recovery-started"
      or "action.autonomous-recovery-escalated"
      or "supervision.retry-started"
      or "supervision.turn-watchdog-recovery"
      or "supervision.turn-canonical-recovery"
      or "supervision.turn-harness-recovery";
  }
}
