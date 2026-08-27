using AgenticRouter.Api.Providers;

namespace AgenticRouter.Api.Execution;

public enum ExecutionContextRole
{
  Direct,
  Supervisor,
  Worker
}

public sealed record ExecutionContextTurnRoute(
  string ProviderId,
  string Model,
  string HarnessId,
  Uri? ProviderEndpoint,
  string WorkspacePath
);

public sealed record ExecutionContextTurnRequest<TEvent>(
  string ContextId,
  ExecutionContextRole Role,
  ExecutionContextTurnRoute Route,
  string Prompt,
  HostCapabilityProfile HostCapabilities,
  ExecutionSession HostSession,
  IAgentHarness Harness,
  AgentHarnessExecution<TEvent> Execution
);

public interface IExecutionContextTurnRunner
{
  IAsyncEnumerable<TEvent> RunAsync<TEvent>(
    ExecutionContextTurnRequest<TEvent> request,
    CancellationToken cancellationToken
  );
}

public interface IExecutionActionJournal
{
  Task RecordAsync(
    ValidatedLocalAction action,
    string phase,
    bool requiresApproval,
    string? result,
    CancellationToken cancellationToken
  );
}

public static class ExecutionActionJournalPhases
{
  public const string Prepared = "prepared";
  public const string AwaitingApproval = "awaiting-approval";
  public const string InFlight = "in-flight";
  public const string Committed = "committed";
  public const string Failed = "failed";
  public const string Rejected = "rejected";
}

public sealed class ExecutionContextTurnRunner : IExecutionContextTurnRunner
{
  public async IAsyncEnumerable<TEvent> RunAsync<TEvent>(
    ExecutionContextTurnRequest<TEvent> request,
    [System.Runtime.CompilerServices.EnumeratorCancellation]
    CancellationToken cancellationToken
  )
  {
    Validate(request);

    await foreach (var item in request.Harness.ExecuteAsync(
      request.Execution,
      cancellationToken
    ))
    {
      yield return item;
    }
  }

  private static void Validate<TEvent>(
    ExecutionContextTurnRequest<TEvent> request
  )
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(request.Route);
    ArgumentNullException.ThrowIfNull(request.HostCapabilities);
    ArgumentNullException.ThrowIfNull(request.HostSession);
    ArgumentNullException.ThrowIfNull(request.Harness);
    ArgumentNullException.ThrowIfNull(request.Execution);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.ContextId);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Route.ProviderId);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Route.Model);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Route.HarnessId);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Route.WorkspacePath);

    if (!Enum.IsDefined(request.Role))
    {
      throw new InvalidOperationException(
        "The execution context turn role is invalid."
      );
    }

    if (
      request.Role == ExecutionContextRole.Direct
      && !string.Equals(
        request.ContextId,
        request.HostSession.Id,
        StringComparison.Ordinal
      )
    )
    {
      throw new InvalidOperationException(
        "A direct execution context must use its Host session identity."
      );
    }

    var modelReference = ProviderModelReference.Parse(request.Route.Model);
    if (!string.Equals(
      request.Route.ProviderId,
      modelReference.ProviderId,
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw new InvalidOperationException(
        "The execution context turn provider does not match the prepared model route."
      );
    }

    if (
      modelReference.IsLocal
      && request.Route.ProviderEndpoint is null
    )
    {
      throw new InvalidOperationException(
        "A local execution context turn requires its prepared provider endpoint."
      );
    }

    if (
      request.Route.ProviderEndpoint is not null
      && !request.Route.ProviderEndpoint.IsAbsoluteUri
    )
    {
      throw new InvalidOperationException(
        "The execution context turn provider endpoint must be absolute."
      );
    }

    if (!string.Equals(
      request.Route.HarnessId,
      request.Harness.Definition.Id,
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw new InvalidOperationException(
        "The execution context turn harness does not match the prepared route."
      );
    }

    if (!string.Equals(
      request.Route.Model,
      request.HostSession.SelectedModel,
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw new InvalidOperationException(
        "The execution context turn model does not match the Host session."
      );
    }

    if (!string.Equals(
      request.HostCapabilities.ApprovalPolicy,
      request.HostSession.ApprovalPolicy,
      StringComparison.Ordinal
    ))
    {
      throw new InvalidOperationException(
        "The execution context turn approval policy does not match the Host session."
      );
    }

    if (!PathsMatch(
      request.Route.WorkspacePath,
      request.HostSession.WorkspacePath
    ))
    {
      throw new InvalidOperationException(
        "The execution context turn workspace does not match the Host session."
      );
    }
  }

  private static bool PathsMatch(
    string left,
    string right
  )
  {
    return string.Equals(
      Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
      Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
      OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal
    );
  }
}
