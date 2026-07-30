using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed record TestApplicationSettings
{
  public int SchemaVersion { get; init; } = 1;

  public string OllamaUrl { get; init; } = string.Empty;

  public string RouterModel { get; init; } = "router:latest";

  public string CoordinatorModel { get; init; } = "router:latest";

  public string DefaultModel { get; init; } = "alpha:latest";

  public string DefaultGpu { get; init; } = "auto";

  public string? TrustedWorkspacePath { get; init; }

  public Dictionary<string, TestIntentionSettings> Intentions { get; init; } = [];

  public TestContextSettings Context { get; init; } = new();

  public TestRuntimeSettings Runtime { get; init; } = new();

  public TestExecutionSettings Execution { get; init; } = new();

  public TestSessionHistorySettings SessionHistory { get; init; } = new();

  public static TestApplicationSettings Create(
    string ollamaUrl,
    string? trustedWorkspacePath = null
  )
  {
    return new TestApplicationSettings
    {
      OllamaUrl = ollamaUrl,
      TrustedWorkspacePath = trustedWorkspacePath,
      Intentions = new Dictionary<string, TestIntentionSettings>(
        StringComparer.Ordinal
      )
      {
        ["general-chat"] = new(
          "default",
          "none",
          "default",
          "You are a clear test assistant."
        ),
        ["documentation"] = new(
          "docs:latest",
          "none",
          "default",
          "You write documentation."
        ),
        ["software-development"] = new(
          "default",
          "none",
          "default",
          "You write software."
        ),
        ["software-architecture"] = new(
          "beta:code",
          "none",
          "default",
          "You design software."
        ),
        ["rpg-storytelling"] = new(
          "default",
          "none",
          "default",
          "You tell RPG stories."
        ),
        ["review-and-testing"] = new(
          "default",
          "none",
          "default",
          "You review and test software."
        )
      }
    };
  }

  public string ToJson()
  {
    return JsonSerializer.Serialize(
      this,
      TestJson.Options
    ) + "\n";
  }
}

internal sealed record TestIntentionSettings(
  string Model,
  string FallbackModel,
  string Gpu,
  string SystemPrompt
);

internal sealed record TestContextSettings
{
  public int DefaultContextTokens { get; init; } = 32_768;

  public int ProviderContextTokens { get; init; } = 40_960;

  public int ReservedResponseTokens { get; init; } = 4_096;

  public int MaxConversationMessages { get; init; } = 40;
}

internal sealed record TestRuntimeSettings
{
  public string ResidentModelPolicy { get; init; } = "adaptive";

  public int ResidentModelVerificationIntervalSeconds { get; init; } = 10;

  public int RuntimeStatusIdleRefreshSeconds { get; init; } = 5;

  public int RuntimeStatusActiveRefreshSeconds { get; init; } = 2;

  public int GenerationTimeoutSeconds { get; init; } = 300;
}

internal sealed record TestExecutionSettings
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

internal sealed record TestSessionHistorySettings
{
  public int MaxSessionsPerWorkspace { get; init; } = 50;

  public int MaxSessionBytes { get; init; } = 5_242_880;

  public int MaxStoredProcessOutputBytesPerTurn { get; init; } = 65_536;

  public int MaxStoredDiffBytesPerTurn { get; init; } = 262_144;
}

internal static class TestJson
{
  public static readonly JsonSerializerOptions Options = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };
}
