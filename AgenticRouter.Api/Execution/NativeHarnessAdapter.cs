namespace AgenticRouter.Api.Execution;

public sealed class NativeHarnessAdapter : IAgentHarness
{
  private static readonly HarnessDefinition AdapterDefinition = new(
    HarnessIds.Native,
    "Native",
    false,
    "Agentic Router's built-in specialist and Host tool loop.",
    new HarnessCapabilities(
      SupportsStreaming: true,
      SupportsThinking: true,
      SupportsResume: false,
      SupportsCancel: true,
      SupportsApprovals: true,
      SupportsToolEvents: true,
      SupportsStructuredEdits: true,
      SupportsStaleProtection: true,
      SupportsSubagents: false,
      SupportsSandbox: false,
      SupportsSessionDiff: true,
      SupportsNativePermissions: false
    )
  );

  public HarnessDefinition Definition => AdapterDefinition;

  public ValueTask<HarnessAvailability> GetAvailabilityAsync(
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult(
      HarnessAvailability.Ready("built-in")
    );
  }

  public IAsyncEnumerable<TEvent> ExecuteAsync<TEvent>(
    AgentHarnessExecution<TEvent> execution,
    CancellationToken cancellationToken
  )
  {
    return execution.ExecuteNativeAsync(cancellationToken);
  }

  public ValueTask DisposeAsync()
  {
    return ValueTask.CompletedTask;
  }
}
