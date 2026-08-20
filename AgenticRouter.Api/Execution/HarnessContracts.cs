using System.Text.Json;

namespace AgenticRouter.Api.Execution;

public static class HarnessIds
{
  public const string Native = "native";
  public const string Codex = "codex";
  public const string OpenCode = "opencode";
  public const string QwenCode = "qwen-code";
}

public enum HarnessAvailabilityState
{
  Available,
  Unavailable
}

public enum HarnessTerminalState
{
  Completed,
  Partial,
  Failed,
  Cancelled,
  TimedOut,
  Unavailable
}

public sealed record HarnessCapabilities(
  bool SupportsStreaming,
  bool SupportsThinking,
  bool SupportsResume,
  bool SupportsCancel,
  bool SupportsApprovals,
  bool SupportsToolEvents,
  bool SupportsStructuredEdits,
  bool SupportsStaleProtection,
  bool SupportsSubagents,
  bool SupportsSandbox,
  bool SupportsSessionDiff,
  bool SupportsNativePermissions
);

public sealed record HarnessDefinition(
  string Id,
  string DisplayName,
  bool Experimental,
  string Description,
  HarnessCapabilities Capabilities,
  IReadOnlyList<string>? SupportedProviders = null
);

public sealed record HarnessAvailability(
  HarnessAvailabilityState State,
  string? Version,
  string? Message,
  DateTimeOffset CheckedAt
)
{
  public bool Available => State == HarnessAvailabilityState.Available;

  public static HarnessAvailability Ready(string version)
  {
    return new HarnessAvailability(
      HarnessAvailabilityState.Available,
      version,
      null,
      DateTimeOffset.UtcNow
    );
  }

  public static HarnessAvailability Missing(string message)
  {
    return new HarnessAvailability(
      HarnessAvailabilityState.Unavailable,
      null,
      message,
      DateTimeOffset.UtcNow
    );
  }
}

public sealed record HarnessStatus(
  HarnessDefinition Definition,
  HarnessAvailability Availability
);

public sealed record HarnessConversationMessage(
  long Sequence,
  string Role,
  string Content
);

public sealed record HarnessConversationContext(
  long Version,
  int OmittedMessages,
  IReadOnlyList<HarnessConversationMessage> Messages
);

public sealed record HarnessTurnRequest(
  string HarnessId,
  string SessionId,
  string Model,
  string Provider,
  string WorkingDirectory,
  string Prompt,
  string ApprovalPolicy,
  Uri? ProviderEndpoint,
  HarnessConversationContext? Conversation = null,
  IReadOnlyDictionary<string, JsonElement>? NativeOptions = null,
  int? ContextWindowTokens = null,
  HostCapabilityProfile? HostCapabilities = null
);

public sealed record HarnessEvent
{
  public HarnessEvent(
    string type,
    string? message = null,
    string? delta = null,
    string? itemId = null,
    string? tool = null,
    string? state = null,
    string? output = null,
    string? approvalId = null,
    bool approvalCanBeMapped = false,
    bool destructive = false,
    string? errorCode = null,
    IReadOnlyList<string>? paths = null,
    string? toolCallId = null,
    JsonElement? arguments = null,
    string harnessId = "",
    string? sessionId = null,
    string? turnId = null,
    DateTimeOffset? timestamp = null,
    HarnessTerminalState? terminalState = null,
    JsonElement? nativePayload = null,
    long? contextInputTokens = null,
    bool recoveryExhausted = false
  )
  {
    Type = type;
    Message = message;
    Delta = delta;
    ItemId = itemId;
    Tool = tool;
    State = state;
    Output = output;
    ApprovalId = approvalId;
    ApprovalCanBeMapped = approvalCanBeMapped;
    Destructive = destructive;
    RecoveryExhausted = recoveryExhausted;
    ErrorCode = errorCode;
    Paths = paths;
    ToolCallId = toolCallId;
    Arguments = arguments;
    HarnessId = harnessId;
    SessionId = sessionId;
    TurnId = turnId;
    Timestamp = timestamp ?? DateTimeOffset.UtcNow;
    TerminalState = terminalState;
    NativePayload = nativePayload;
    ContextInputTokens = contextInputTokens;
  }

  public string Type { get; init; }

  public string? Message { get; init; }

  public string? Delta { get; init; }

  public string? ItemId { get; init; }

  public string? Tool { get; init; }

  public string? State { get; init; }

  public string? Output { get; init; }

  public string? ApprovalId { get; init; }

  public bool ApprovalCanBeMapped { get; init; }

  public bool Destructive { get; init; }

  public bool RecoveryExhausted { get; init; }

  public string? ErrorCode { get; init; }

  public IReadOnlyList<string>? Paths { get; init; }

  public string? ToolCallId { get; init; }

  public JsonElement? Arguments { get; init; }

  public string HarnessId { get; init; }

  public string? SessionId { get; init; }

  public string? TurnId { get; init; }

  public DateTimeOffset Timestamp { get; init; }

  public HarnessTerminalState? TerminalState { get; init; }

  public JsonElement? NativePayload { get; init; }

  public long? ContextInputTokens { get; init; }

  public bool IsTerminal => TerminalState.HasValue;
}

public sealed record AgentHarnessExecution<TEvent>(
  Func<CancellationToken, IAsyncEnumerable<TEvent>> ExecuteNativeAsync,
  Func<IAgentHarnessTransport, CancellationToken, IAsyncEnumerable<TEvent>> ExecuteExternalAsync
);

public interface IAgentHarness : IAsyncDisposable
{
  HarnessDefinition Definition { get; }

  ValueTask<HarnessAvailability> GetAvailabilityAsync(
    CancellationToken cancellationToken
  );

  IAsyncEnumerable<TEvent> ExecuteAsync<TEvent>(
    AgentHarnessExecution<TEvent> execution,
    CancellationToken cancellationToken
  );
}

public interface IAgentHarnessTransport
{
  HarnessDefinition Definition { get; }

  IAsyncEnumerable<HarnessEvent> StartTurnAsync(
    HarnessTurnRequest request,
    CancellationToken cancellationToken
  );

  Task ResolveApprovalAsync(
    string approvalId,
    bool approved,
    CancellationToken cancellationToken
  );

  Task ResolveToolCallAsync(
    string toolCallId,
    bool succeeded,
    string output,
    CancellationToken cancellationToken
  );

  Task CancelTurnAsync(
    string sessionId,
    CancellationToken cancellationToken
  );
}

public sealed class HarnessException : Exception
{
  public HarnessException(
    string code,
    string message,
    string technicalMessage,
    bool recoverable,
    Exception? innerException = null,
    string harnessId = HarnessIds.Codex
  ) : base(message, innerException)
  {
    Code = code;
    TechnicalMessage = technicalMessage;
    Recoverable = recoverable;
    HarnessId = harnessId;
  }

  public string Code { get; }

  public string TechnicalMessage { get; }

  public bool Recoverable { get; }

  public string HarnessId { get; }
}
