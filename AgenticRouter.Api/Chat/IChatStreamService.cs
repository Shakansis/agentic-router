using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;

namespace AgenticRouter.Api.Chat;

public interface IChatStreamService
{
  IAsyncEnumerable<ChatStreamEvent> StreamAsync(
    ChatRequest request,
    string requestId,
    CancellationToken cancellationToken
  );
}

public sealed record ExecutionSpecialistTurnInvocation(
  string? ContextId,
  ExecutionContextRole Role,
  ExecutionTurnToolScope? ToolScopeOverride = null,
  bool UseMinimalToolInventory = false,
  Action<string>? CaptureRoleResult = null,
  IExecutionActionJournal? ActionJournal = null,
  string RequestedEffort = ModelEffortLevels.Medium
)
{
  public static ExecutionSpecialistTurnInvocation Direct { get; } = new(
    null,
    ExecutionContextRole.Direct
  );
}

public interface IExecutionSpecialistTurnService
{
  IAsyncEnumerable<ChatStreamEvent> RunAsync(
    ChatRequest request,
    string requestId,
    ExecutionSpecialistTurnInvocation invocation,
    CancellationToken cancellationToken
  );
}

public sealed class ChatStageException : Exception
{
  public ChatStageException(
    string stage,
    string message,
    string technicalMessage,
    string? model,
    string? intention,
    int? httpStatus,
    bool recoverable,
    Exception? innerException = null,
    IReadOnlyDictionary<string, string?>? details = null,
    string provider = "ollama-local"
  )
    : base(
      message,
      innerException
    )
  {
    Stage = stage;
    TechnicalMessage = technicalMessage;
    Model = model;
    Intention = intention;
    HttpStatus = httpStatus;
    Recoverable = recoverable;
    Details = details;
    Provider = provider;
  }

  public string Stage { get; }

  public string TechnicalMessage { get; }

  public string? Model { get; }

  public string? Intention { get; }

  public int? HttpStatus { get; }

  public bool Recoverable { get; }

  public IReadOnlyDictionary<string, string?>? Details { get; }

  public string Provider { get; }
}
