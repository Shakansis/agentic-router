namespace AgenticRouter.Api.Configuration;

public sealed record ApplicationSettings
{
  public int SchemaVersion { get; init; } = 1;

  public string OllamaUrl { get; init; } = "http://localhost:11434";

  public string RouterModel { get; init; } = "configure-model";

  public string DefaultModel { get; init; } = "configure-model";

  public string DefaultGpu { get; init; } = "auto";

  public string? TrustedWorkspacePath { get; init; }

  public Dictionary<string, IntentionSettings> Intentions { get; init; } = [];

  public ContextSettings Context { get; init; } = new();

  public RuntimeSettings Runtime { get; init; } = new();
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
  public int DefaultContextTokens { get; init; } = 8_192;

  public int ReservedResponseTokens { get; init; } = 2_048;

  public int MaxConversationMessages { get; init; } = 40;
}

public sealed record RuntimeSettings
{
  public string ResidentModelPolicy { get; init; } = "adaptive";

  public int ResidentModelVerificationIntervalSeconds { get; init; } = 30;

  public int RuntimeStatusIdleRefreshSeconds { get; init; } = 5;

  public int RuntimeStatusActiveRefreshSeconds { get; init; } = 2;
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
