using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed record TestApplicationSettings
{
  public int SchemaVersion { get; init; } = 1;

  public string OllamaUrl { get; init; } = string.Empty;

  public string RouterModel { get; init; } = "router:latest";

  public string RouterGpu { get; init; } = "default";

  public string ActionModel { get; init; } = "router:latest";

  public string ActionGpu { get; init; } = "default";

  public string CoordinatorModel { get; init; } = "router:latest";

  public string CoordinatorGpu { get; init; } = "default";

  public string DefaultModel { get; init; } = "alpha:latest";

  public string DefaultGpu { get; init; } = "auto";

  public string? TrustedWorkspacePath { get; init; }

  public Dictionary<string, TestIntentionSettings> Intentions { get; init; } = [];

  public TestContextSettings Context { get; init; } = new();

  public TestRuntimeSettings Runtime { get; init; } = new();

  public TestOllamaRuntimeSettings OllamaRuntime { get; init; } = new();

  public TestExecutionSettings Execution { get; init; } = new();

  public TestSessionHistorySettings SessionHistory { get; init; } = new();

  public TestGitDeliverySettings GitDelivery { get; init; } = new();

  public TestUsageSettings Usage { get; init; } = new();

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

internal sealed record TestOllamaRuntimeSettings
{
  public int ProfileSchemaVersion { get; init; } = 1;

  public Dictionary<string, TestOllamaRoleRuntimeSettings> RoleDefaults
  {
    get;
    init;
  } = TestOllamaRuntimeDefaults.CreateRoleDefaults();

  public IReadOnlyList<TestOllamaModelRuntimeOverride> ModelOverrides
  {
    get;
    init;
  } = [];

  public IReadOnlyList<int> ContextEscalationLadder { get; init; } =
  [
    4_096,
    8_192,
    12_288,
    16_384,
    24_576,
    32_768,
    40_960
  ];

  public TestOllamaRuntimeMemoryPolicy Memory { get; init; } = new();
}

internal sealed record TestOllamaRoleRuntimeSettings(
  int MinimumContextTokens,
  int TargetContextTokens,
  int MaximumContextTokens,
  int KeepAlive,
  int OutputTokenLimit
);

internal sealed record TestOllamaModelRuntimeOverride(
  string Provider,
  string Model,
  string Digest,
  Dictionary<string, TestOllamaRoleRuntimeSettings> Overrides
);

internal sealed record TestOllamaRuntimeMemoryPolicy
{
  public int TargetMaximumGpuUsagePercent { get; init; } = 90;

  public long MinimumFreeVramBytes { get; init; } = 2_147_483_648;

  public long MinimumFreeSystemRamBytes { get; init; } = 4_294_967_296;

  public bool AllowCpuOffload { get; init; } = true;

  public bool PreferFullGpuForActivePrimary { get; init; } = true;

  public Dictionary<string, object> Devices { get; init; } = [];
}

internal static class TestOllamaRuntimeDefaults
{
  public static Dictionary<string, TestOllamaRoleRuntimeSettings> CreateRoleDefaults()
  {
    return new Dictionary<string, TestOllamaRoleRuntimeSettings>(
      StringComparer.Ordinal
    )
    {
      ["router"] = Profile(
        4_096,
        8_192,
        8_192,
        1_024
      ),
      ["residentCoordinator"] = Profile(
        4_096,
        8_192,
        16_384,
        1_024,
        -1
      ),
      ["specialist"] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      ["primary"] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      ["fallback"] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      ["benchmark"] = Profile(
        4_096,
        8_192,
        16_384,
        1_024
      ),
      ["modelTest"] = Profile(
        4_096,
        4_096,
        8_192,
        512
      ),
      ["webSearchSynthesis"] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      ["visionRequest"] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      )
    };
  }

  public static Dictionary<string, TestOllamaRoleRuntimeSettings> WithMaximum(
    int maximum
  )
  {
    return CreateRoleDefaults().ToDictionary(
      pair => pair.Key,
      pair => pair.Value with
      {
        MinimumContextTokens = Math.Min(
          pair.Value.MinimumContextTokens,
          maximum
        ),
        TargetContextTokens = Math.Min(
          pair.Value.TargetContextTokens,
          maximum
        ),
        MaximumContextTokens = Math.Min(
          pair.Value.MaximumContextTokens,
          maximum
        )
      },
      StringComparer.Ordinal
    );
  }

  private static TestOllamaRoleRuntimeSettings Profile(
    int minimum,
    int target,
    int maximum,
    int output,
    int keepAlive = 300
  )
  {
    return new TestOllamaRoleRuntimeSettings(
      minimum,
      target,
      maximum,
      keepAlive,
      output
    );
  }
}

internal sealed record TestExecutionSettings
{
  public int DirectCoordinatorPlanningFailuresBeforeHandoff { get; init; } = 5;

  public int ResidentCoordinatorPlanningFailuresBeforeFailure { get; init; } = 5;

  public int MaxCoordinatorHandoffsPerTurn { get; init; } = 1;

  public int MaxToolCallsPerTurn { get; init; } = 20;

  public int MaxConsecutiveToolFailures { get; init; } = 5;

  public int MaxRecoveryAttemptsPerTurn { get; init; } = 10;

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

internal sealed record TestGitDeliverySettings
{
  public bool Enabled { get; init; } = true;

  public bool RequireValidationBeforeCommit { get; init; } = true;

  public bool AllowExplicitCommitWithoutValidation { get; init; } = true;

  public int MaxDiffBytesPerFile { get; init; } = 262_144;

  public int MaxLogEntries { get; init; } = 50;
}

internal sealed record TestUsageSettings
{
  public int RetentionDays { get; init; } = 90;

  public int MaxEventBytes { get; init; } = 16_384;

  public string SelectedWindow { get; init; } = "rolling-hour";

  public string[] PinnedWindows { get; init; } =
  [
    "rolling-hour",
    "day",
    "rolling-seven-days",
    "calendar-month"
  ];

  public int ProviderShortWindowMinutes { get; init; } = 300;

  public int ProviderLongWindowMinutes { get; init; } = 10_080;

  public int CustomRollingWindowMinutes { get; init; } = 1_440;

  public string GoogleComparisonModel { get; init; } =
    "gemini-3.5-flash-lite";

  public string OllamaPlanReference { get; init; } = "Free";
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
