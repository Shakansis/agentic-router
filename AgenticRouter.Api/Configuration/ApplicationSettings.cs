namespace AgenticRouter.Api.Configuration;

public sealed record ApplicationSettings
{
  public int SchemaVersion { get; init; } = 1;

  public string OllamaUrl { get; init; } = "http://localhost:11434";

  public string RouterModel { get; init; } = "configure-model";

  public string ActionModel { get; init; } = "functiongemma:270m";

  public string CoordinatorModel { get; init; } = "configure-model";

  public string DefaultModel { get; init; } = "configure-model";

  public string DefaultGpu { get; init; } = "auto";

  public string? TrustedWorkspacePath { get; init; }

  public Dictionary<string, IntentionSettings> Intentions { get; init; } = [];

  public ContextSettings Context { get; init; } = new();

  public RuntimeSettings Runtime { get; init; } = new();

  public OllamaRuntimeSettings OllamaRuntime { get; init; } = new();

  public ExecutionSettings Execution { get; init; } = new();

  public ProjectAwarenessSettings ProjectAwareness { get; init; } = new();

  public ValidationProfileSettings? ValidationProfile { get; init; }

  public SessionHistorySettings SessionHistory { get; init; } = new();

  public GitDeliverySettings GitDelivery { get; init; } = new();

  public UsageSettings Usage { get; init; } = new();

  public IncidentJournalSettings Incidents { get; init; } = new();

  public CloudProvidersSettings CloudProviders { get; init; } = new();

  public WebSearchSettings WebSearch { get; init; } = new();

  public ModelOrganizationSettings ModelOrganization { get; init; } = new();
}

public sealed record IncidentJournalSettings
{
  public bool Enabled { get; init; } = true;

  public int RetentionDays { get; init; } = 14;

  public long MaximumFileBytes { get; init; } = 8_388_608;

  public long MaximumTotalBytes { get; init; } = 67_108_864;

  public int MaximumEventsPerTrace { get; init; } = 500;

  public int BrowserMaximumEvents { get; init; } = 200;

  public long BrowserMaximumBytes { get; init; } = 262_144;
}

public sealed record ModelOrganizationSettings
{
  public int MaximumProfiles { get; init; } = 20;
}

public sealed record WebSearchSettings
{
  public bool OllamaEnabled { get; init; }

  public string? OllamaSecretReference { get; init; }

  public int MaxResults { get; init; } = 5;

  public int TimeoutSeconds { get; init; } = 15;
}

public sealed record CloudProvidersSettings
{
  public CloudProviderIntegrationSettings Groq { get; init; } = new();

  public CloudProviderIntegrationSettings GoogleAiStudio { get; init; } = new();

  public CloudProviderIntegrationSettings Cerebras { get; init; } = new();
}

public sealed record CloudProviderIntegrationSettings
{
  public bool Enabled { get; init; }

  public string? SecretReference { get; init; }

  public string ExpectedBillingMode { get; init; } = "unknown";

  public IReadOnlyDictionary<string, CloudModelQuotaSettings> ModelQuotas
  {
    get;
    init;
  } = new Dictionary<string, CloudModelQuotaSettings>(
    StringComparer.Ordinal
  );
}

public sealed record CloudModelQuotaSettings
{
  public long? ShortWindowTokenLimit { get; init; }

  public int ShortWindowMinutes { get; init; } = 1;

  public long? LongWindowTokenLimit { get; init; }

  public int LongWindowMinutes { get; init; } = 1_440;
}

public sealed record UsageSettings
{
  public int RetentionDays { get; init; } = 90;

  public int MaxEventBytes { get; init; } = 16_384;

  public string SelectedWindow { get; init; } = "rolling-hour";

  public IReadOnlyList<string> PinnedWindows { get; init; } =
  [
    "rolling-hour",
    "day",
    "rolling-seven-days",
    "calendar-month"
  ];

  public int ProviderShortWindowMinutes { get; init; } = 300;

  public int ProviderLongWindowMinutes { get; init; } = 10_080;

  public int CustomRollingWindowMinutes { get; init; } = 1_440;

  public string ComparisonProvider { get; init; } = "google-ai-studio";

  public string ComparisonModel { get; init; } = "gemini-3.5-flash-lite";

  public string OllamaPlanReference { get; init; } = "Free";

  public IReadOnlyList<int> AlertThresholds { get; init; } =
  [
    70,
    85,
    95
  ];
}

public sealed record GitDeliverySettings
{
  public bool Enabled { get; init; } = true;

  public bool RequireValidationBeforeCommit { get; init; } = true;

  public bool AllowExplicitCommitWithoutValidation { get; init; } = true;

  public int MaxDiffBytesPerFile { get; init; } = 262_144;

  public int MaxLogEntries { get; init; } = 50;
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

public sealed record OllamaRuntimeSettings
{
  public int ProfileSchemaVersion { get; init; } = 1;

  public Dictionary<string, OllamaRoleRuntimeSettings> RoleDefaults
  {
    get;
    init;
  } = OllamaRuntimeDefaults.CreateRoleDefaults();

  public IReadOnlyList<OllamaModelRuntimeOverride> ModelOverrides
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

  public OllamaRuntimeMemoryPolicy Memory { get; init; } = new();
}

public sealed record OllamaRoleRuntimeSettings
{
  public int MinimumContextTokens { get; init; } = 4_096;

  public int TargetContextTokens { get; init; } = 8_192;

  public int MaximumContextTokens { get; init; } = 16_384;

  public int KeepAlive { get; init; } = 300;

  public int OutputTokenLimit { get; init; } = 4_096;
}

public sealed record OllamaModelRuntimeOverride
{
  public string Provider { get; init; } = "ollama-local";

  public string Model { get; init; } = string.Empty;

  public string Digest { get; init; } = string.Empty;

  public Dictionary<string, OllamaRoleRuntimeSettings> Overrides
  {
    get;
    init;
  } = [];
}

public sealed record OllamaRuntimeMemoryPolicy
{
  public int TargetMaximumGpuUsagePercent { get; init; } = 90;

  public long MinimumFreeVramBytes { get; init; } = 2_147_483_648;

  public long MinimumFreeSystemRamBytes { get; init; } = 4_294_967_296;

  public bool AllowCpuOffload { get; init; } = true;

  public bool PreferFullGpuForActivePrimary { get; init; } = true;

  public IReadOnlyDictionary<string, OllamaGpuMemoryPolicy> Devices
  {
    get;
    init;
  } = new Dictionary<string, OllamaGpuMemoryPolicy>(
    StringComparer.Ordinal
  );
}

public sealed record OllamaGpuMemoryPolicy
{
  public int TargetMaximumUsagePercent { get; init; } = 90;

  public long MinimumFreeVramBytes { get; init; } = 2_147_483_648;
}

public static class OllamaRuntimeRoleIds
{
  public const string Router = "router";
  public const string ResidentCoordinator = "residentCoordinator";
  public const string Specialist = "specialist";
  public const string Primary = "primary";
  public const string Fallback = "fallback";
  public const string Benchmark = "benchmark";
  public const string ModelTest = "modelTest";
  public const string WebSearchSynthesis = "webSearchSynthesis";
  public const string VisionRequest = "visionRequest";

  public static readonly IReadOnlyList<string> All =
  [
    Router,
    ResidentCoordinator,
    Specialist,
    Primary,
    Fallback,
    Benchmark,
    ModelTest,
    WebSearchSynthesis,
    VisionRequest
  ];
}

public static class OllamaRuntimeDefaults
{
  public static Dictionary<string, OllamaRoleRuntimeSettings> CreateRoleDefaults()
  {
    return new Dictionary<string, OllamaRoleRuntimeSettings>(
      StringComparer.Ordinal
    )
    {
      [OllamaRuntimeRoleIds.Router] = Profile(
        4_096,
        8_192,
        8_192,
        1_024
      ),
      [OllamaRuntimeRoleIds.ResidentCoordinator] = Profile(
        4_096,
        8_192,
        16_384,
        1_024,
        -1
      ),
      [OllamaRuntimeRoleIds.Specialist] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      [OllamaRuntimeRoleIds.Primary] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      [OllamaRuntimeRoleIds.Fallback] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      [OllamaRuntimeRoleIds.Benchmark] = Profile(
        4_096,
        8_192,
        16_384,
        1_024
      ),
      [OllamaRuntimeRoleIds.ModelTest] = Profile(
        4_096,
        4_096,
        8_192,
        512
      ),
      [OllamaRuntimeRoleIds.WebSearchSynthesis] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      ),
      [OllamaRuntimeRoleIds.VisionRequest] = Profile(
        8_192,
        32_768,
        40_960,
        4_096
      )
    };
  }

  private static OllamaRoleRuntimeSettings Profile(
    int minimum,
    int target,
    int maximum,
    int output,
    int keepAlive = 300
  )
  {
    return new OllamaRoleRuntimeSettings
    {
      MinimumContextTokens = minimum,
      TargetContextTokens = target,
      MaximumContextTokens = maximum,
      KeepAlive = keepAlive,
      OutputTokenLimit = output
    };
  }
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
