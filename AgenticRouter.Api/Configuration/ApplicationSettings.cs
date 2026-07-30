namespace AgenticRouter.Api.Configuration;

public sealed record ApplicationSettings
{
  public int SchemaVersion { get; init; } = 1;

  public string OllamaUrl { get; init; } = "http://localhost:11434";

  public string RouterModel { get; init; } = "configure-model";

  public string CoordinatorModel { get; init; } = "configure-model";

  public string DefaultModel { get; init; } = "configure-model";

  public string DefaultGpu { get; init; } = "auto";

  public string? TrustedWorkspacePath { get; init; }

  public Dictionary<string, IntentionSettings> Intentions { get; init; } = [];

  public ContextSettings Context { get; init; } = new();

  public RuntimeSettings Runtime { get; init; } = new();

  public ExecutionSettings Execution { get; init; } = new();

  public ProjectAwarenessSettings ProjectAwareness { get; init; } = new();

  public ValidationProfileSettings? ValidationProfile { get; init; }

  public SessionHistorySettings SessionHistory { get; init; } = new();
}

public sealed record SessionHistorySettings
{
  public int MaxSessionsPerWorkspace { get; init; } = 50;

  public int MaxSessionBytes { get; init; } = 5_242_880;

  public int MaxStoredProcessOutputBytesPerTurn { get; init; } = 65_536;

  public int MaxStoredDiffBytesPerTurn { get; init; } = 262_144;
}

public sealed record IntentionSettings
{
  public string Model { get; init; } = "default";

  public string FallbackModel { get; init; } = "none";

  public string Gpu { get; init; } = "default";

  public string SystemPrompt { get; init; } = string.Empty;
}

public sealed record ContextSettings
{
  public int DefaultContextTokens { get; init; } = 32_768;

  public int ProviderContextTokens { get; init; } = 40_960;

  public int ReservedResponseTokens { get; init; } = 4_096;

  public int MaxConversationMessages { get; init; } = 40;
}

public sealed record RuntimeSettings
{
  public string ResidentModelPolicy { get; init; } = "adaptive";

  public int ResidentModelVerificationIntervalSeconds { get; init; } = 30;

  public int RuntimeStatusIdleRefreshSeconds { get; init; } = 5;

  public int RuntimeStatusActiveRefreshSeconds { get; init; } = 2;

  public int GenerationTimeoutSeconds { get; init; } = 300;
}

public sealed record ExecutionSettings
{
  public int DirectCoordinatorPlanningFailuresBeforeHandoff { get; init; } = 2;

  public int ResidentCoordinatorPlanningFailuresBeforeFailure { get; init; } = 5;

  public int MaxCoordinatorHandoffsPerTurn { get; init; } = 1;

  public int MaxToolCallsPerTurn { get; init; } = 20;

  public int MaxConsecutiveToolFailures { get; init; } = 5;

  public int MaxRecoveryAttemptsPerTurn { get; init; } = 5;

  public int MaxTrackedFilesPerSession { get; init; } = 50;

  public int MaxRollbackBytesPerFile { get; init; } = 1_048_576;

  public int MaxRollbackBytesPerSession { get; init; } = 10_485_760;

  public int MaxSearchFiles { get; init; } = 500;

  public int MaxSearchMatches { get; init; } = 200;

  public int MaxToolOutputTokens { get; init; } = 2_048;
}

public sealed record ProjectAwarenessSettings
{
  public int MaxProjectMarkers { get; init; } = 100;

  public int MaxInstructionBytes { get; init; } = 131_072;

  public int MaxPlanSteps { get; init; } = 8;

  public int MaxPlanRevisions { get; init; } = 3;
}

public sealed record ValidationProfileSettings
{
  public string Name { get; init; } = string.Empty;

  public string Source { get; init; } = "user";

  public IReadOnlyList<ValidationStepSettings> Steps { get; init; } = [];
}

public sealed record ValidationStepSettings
{
  public string Id { get; init; } = string.Empty;

  public string Label { get; init; } = string.Empty;

  public string Executable { get; init; } = string.Empty;

  public IReadOnlyList<string> Arguments { get; init; } = [];

  public string WorkingDirectory { get; init; } = ".";

  public int TimeoutSeconds { get; init; } = 60;

  public bool Required { get; init; } = true;
}

public static class SettingsDefaults
{
  public const string GlobalTargetInstruction =
    "The latest user instruction has priority over earlier conversational patterns. "
    + "Do not continue a previous task when the user explicitly changes the objective. "
    + "Do not claim that you executed, tested, opened, accessed, or verified something "
    + "unless the application actually performed that action.";

  public static readonly IReadOnlyList<string> IntentionNames =
  [
    "general-chat",
    "documentation",
    "software-development",
    "software-architecture",
    "rpg-storytelling",
    "review-and-testing"
  ];

  public static ApplicationSettings Create()
  {
    return new ApplicationSettings
    {
      Intentions = IntentionNames.ToDictionary(
        name => name,
        name => new IntentionSettings
        {
          SystemPrompt = GetDefaultPrompt(
            name
          )
        },
        StringComparer.Ordinal
      )
    };
  }

  private static string GetDefaultPrompt(
    string intention
  )
  {
    return intention switch
    {
      "general-chat" => "You are a clear, helpful local assistant.",
      "documentation" => "You write concise and accurate technical documentation.",
      "software-development" => "You are a pragmatic senior software developer.",
      "software-architecture" => "You are a pragmatic software architect.",
      "rpg-storytelling" => "You are an imaginative RPG storyteller.",
      "review-and-testing" => "You review software carefully and focus on verifiable quality.",
      _ => string.Empty
    };
  }
}
