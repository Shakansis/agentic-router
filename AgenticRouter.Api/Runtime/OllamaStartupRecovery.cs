namespace AgenticRouter.Api.Runtime;

public sealed class OllamaStartupOptions
{
  public int AttemptTimeoutSeconds { get; init; } = 120;
  public int RecoveryTimeoutSeconds { get; init; } = 600;
}

public sealed record OllamaStartupProgress(string Stage, string Message);

public sealed record OllamaStartupRecovery(Action<OllamaStartupProgress> Report);
