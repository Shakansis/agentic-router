using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Models;
using AgenticRouter.Api.Observability;
using AgenticRouter.Api.Platform;
using AgenticRouter.Api.ProjectAwareness;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Recovery;
using AgenticRouter.Api.Routing;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.Setup;
using AgenticRouter.Api.Supervision;
using AgenticRouter.Api.Usage;
using AgenticRouter.Api.WorkspaceProfiles;

var executableContentRoot = AppContext.BaseDirectory;
var contentRootPath = Directory.Exists(
  Path.Combine(
    executableContentRoot,
    "wwwroot"
  )
)
  ? executableContentRoot
  : Directory.GetCurrentDirectory();
var builder = WebApplication.CreateBuilder(
  new WebApplicationOptions
  {
    Args = args,
    ContentRootPath = contentRootPath
  }
);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddControllers();
builder.Services.AddHttpClient<OllamaClient>(
  client => client.Timeout = Timeout.InfiniteTimeSpan
);
builder.Services.AddSingleton<ISettingsValidator, SettingsValidator>();
builder.Services.AddSingleton<
  IPortableYamlSettingsService,
  PortableYamlSettingsService
>();
var configuredDirectory = builder.Configuration["AgenticRouter:DataDirectory"];
var dataDirectory = string.IsNullOrWhiteSpace(
  configuredDirectory
)
  ? Path.Combine(
    builder.Environment.ContentRootPath,
    "data"
  )
  : Path.GetFullPath(
    configuredDirectory
  );
builder.Services.AddHostPlatformServices(
  dataDirectory
);
var safeModeRequested = args.Any(
  argument => string.Equals(
    argument,
    "--safe-mode",
    StringComparison.OrdinalIgnoreCase
  )
) || string.Equals(
  builder.Configuration["AgenticRouter:SafeMode"],
  "true",
  StringComparison.OrdinalIgnoreCase
);
var safeModeState = new SafeModeState(
  safeModeRequested,
  safeModeRequested
    ? "Safe mode was explicitly requested for this startup."
    : null
);
builder.Services.AddSingleton(
  safeModeState
);
builder.Services.AddSingleton(
  new DataMigrationService(
    dataDirectory,
    safeModeState
  )
);
builder.Services.AddSingleton<ILocalBackupService>(
  services => new LocalBackupService(
    dataDirectory,
    services.GetRequiredService<IPricingCatalog>()
  )
);
builder.Services.AddSingleton<JsonSettingsStore>(
  services =>
  {
    return new JsonSettingsStore(
      dataDirectory,
      services.GetRequiredService<ISettingsValidator>(),
      services.GetRequiredService<ILogger<JsonSettingsStore>>()
    );
  }
);
builder.Services.AddSingleton<ISettingsStore>(
  services => new SafeModeSettingsStore(
    services.GetRequiredService<JsonSettingsStore>(),
    safeModeState
  )
);
builder.Services.AddSingleton<ICloudProviderAdapter, GroqCloudProvider>();
builder.Services.AddSingleton<ICloudProviderAdapter>(
  services => new GeminiCloudProvider(
    services.GetRequiredService<IHttpClientFactory>(),
    new Uri(
      builder.Configuration[
        "AgenticRouter:Providers:GoogleAiStudioBaseUrl"
      ] ?? "https://generativelanguage.googleapis.com/v1beta/",
      UriKind.Absolute
    )
  )
);
builder.Services.AddSingleton<ICloudProviderAdapter, CerebrasCloudProvider>();
builder.Services.AddSingleton<ICloudProviderRegistry>(
  services => new CloudProviderRegistry(
    services.GetServices<ICloudProviderAdapter>(),
    services.GetRequiredService<ISettingsStore>(),
    services.GetRequiredService<IProtectedSecretStore>(),
    dataDirectory
  )
);
builder.Services.AddSingleton<IPricingCatalog, BuiltInPricingCatalog>();
builder.Services.AddSingleton<IUsageLedger>(
  services => new JsonlUsageLedger(
    dataDirectory,
    services.GetRequiredService<IPricingCatalog>()
  )
);
builder.Services.AddSingleton<ITokenEstimator, ConservativeTokenEstimator>();
builder.Services.AddSingleton<IUsageRecorder, UsageRecorder>();
builder.Services.AddScoped<ITraceContext, TraceContext>();
builder.Services.AddSingleton<IIncidentJournal>(
  services => new JsonlIncidentJournal(
    dataDirectory,
    services.GetRequiredService<ISettingsStore>(),
    services.GetRequiredService<ILogger<JsonlIncidentJournal>>()
  )
);
builder.Services.AddSingleton<IProviderRetryPolicy, ConservativeProviderRetryPolicy>();
builder.Services.AddSingleton<IProviderHealthMonitor, ProviderHealthMonitor>();
builder.Services.AddSingleton<IUsageReconciliationService>(
  services => new UsageReconciliationService(
    dataDirectory,
    services.GetRequiredService<IPricingCatalog>()
  )
);
builder.Services.AddSingleton<IImageAttachmentValidator, ImageAttachmentValidator>();
builder.Services.AddSingleton<ICloudImageApprovalStore, CloudImageApprovalStore>();
builder.Services.AddSingleton<IOllamaWebSearchService, OllamaWebSearchService>();
builder.Services.AddTransient<IOllamaClient, ProviderDispatchClient>();
builder.Services.AddScoped<ICloudFallbackPolicy, CloudFallbackPolicy>();
builder.Services.AddSingleton<IWorkspaceProfileStore>(
  new WorkspaceProfileStore(
    dataDirectory
  )
);
builder.Services.AddSingleton<IWorkspaceProfileService, WorkspaceProfileService>();
builder.Services.AddSingleton<IPersistentSessionStore, PersistentSessionStore>();
builder.Services.AddSingleton<IPersistentSessionService, PersistentSessionService>();
builder.Services.AddSingleton<ISupervisionCheckpointStore, SupervisionCheckpointStore>();
builder.Services.AddSingleton<IMarkdownRenderer, SafeMarkdownRenderer>();
builder.Services.AddSingleton<IRouterResponseParser, RouterResponseParser>();
builder.Services.AddScoped<IIntentionRouter, IntentionRouter>();
builder.Services.AddScoped<IModelResolver, ModelResolver>();
builder.Services.AddScoped<IConversationContextBuilder, ConversationContextBuilder>();
builder.Services.AddScoped<ITrustedWorkspaceService, TrustedWorkspaceService>();
builder.Services.AddScoped<IProjectAwarenessService, ProjectAwarenessService>();
builder.Services.AddScoped<IRepositoryInstructionService, RepositoryInstructionService>();
builder.Services.AddSingleton<IToolNameResolver, ToolNameResolver>();
builder.Services.AddSingleton<
  ISpecialistToolingProfileResolver,
  SpecialistToolingProfileResolver
>();
builder.Services.AddSingleton<
  ISpecialistToolingProtocol,
  SpecialistToolingProtocol
>();
builder.Services.AddScoped<ILocalActionService, LocalActionService>();
builder.Services.AddScoped<IApprovalPolicyService, ApprovalPolicyService>();
builder.Services.AddSingleton<IProcessExecutionService, ProcessExecutionService>();
builder.Services.AddScoped<IProcessPolicyService, ProcessPolicyService>();
builder.Services.AddScoped<IValidationProfileService, ValidationProfileService>();
builder.Services.AddSingleton<IGitRepositoryService, GitRepositoryService>();
builder.Services.AddSingleton<IGitDeliveryService, GitDeliveryService>();
builder.Services.AddScoped<IWorkspaceGitActionService, WorkspaceGitActionService>();
builder.Services.AddScoped<ILocalActionPlanner, LocalActionPlanner>();
builder.Services.AddScoped<IFunctionGemmaResidentProtocol, FunctionGemmaResidentProtocol>();
builder.Services.AddSingleton<IPlanningFailureClassifier, PlanningFailureClassifier>();
builder.Services.AddSingleton<IToolProtocolConformanceService, ToolProtocolConformanceService>();
builder.Services.AddSingleton<IExecutionPlanService, ExecutionPlanService>();
builder.Services.AddScoped<IExpertExecutionGuidanceService, ExpertExecutionGuidanceService>();
builder.Services.AddSingleton<IApprovalCoordinator, ApprovalCoordinator>();
builder.Services.AddSingleton<IRecoveryDecisionCoordinator, RecoveryDecisionCoordinator>();
builder.Services.AddSingleton<IExecutionSessionStore, ExecutionSessionStore>();
builder.Services.AddSingleton<HarnessMcpHostBridge>();
builder.Services.AddSingleton<NativeHarnessAdapter>();
builder.Services.AddSingleton<IAgentHarness>(
  services => services.GetRequiredService<NativeHarnessAdapter>()
);
builder.Services.AddSingleton(
  new CodexHarnessOptions(
    builder.Configuration["AgenticRouter:Codex:ExecutablePath"],
    builder.Configuration["AgenticRouter:Codex:ManagedInstallRoot"]
      ?? (
        OperatingSystem.IsWindows()
          ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin"
          )
          : null
      ),
    Path.Combine(
      dataDirectory,
      "codex-runtime"
    ),
    TimeSpan.FromSeconds(10),
    TimeSpan.FromSeconds(3)
  )
);
builder.Services.AddSingleton<CodexHarnessAdapter>();
builder.Services.AddSingleton<IAgentHarness>(
  services => services.GetRequiredService<CodexHarnessAdapter>()
);
builder.Services.AddSingleton(
  new OpenCodeHarnessOptions(
    builder.Configuration["AgenticRouter:OpenCode:ExecutablePath"],
    OperatingSystem.IsWindows()
      ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm",
        "node_modules",
        "opencode-ai",
        "bin",
        "opencode.exe"
      )
      : null,
    Path.Combine(dataDirectory, "opencode-runtime"),
    TimeSpan.FromSeconds(15),
    TimeSpan.FromMinutes(5)
  )
);
builder.Services.AddSingleton<OpenCodeHarnessAdapter>();
builder.Services.AddSingleton<IAgentHarness>(
  services => services.GetRequiredService<OpenCodeHarnessAdapter>()
);
builder.Services.AddSingleton(
  new QwenCodeHarnessOptions(
    builder.Configuration["AgenticRouter:QwenCode:ExecutablePath"],
    OperatingSystem.IsWindows()
      ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm",
        "node_modules",
        "@qwen-code",
        "qwen-code",
        "cli.js"
      )
      : null,
    Path.Combine(dataDirectory, "qwen-code-runtime"),
    TimeSpan.FromSeconds(20),
    TimeSpan.FromMinutes(5)
  )
);
builder.Services.AddSingleton<QwenCodeHarnessAdapter>();
builder.Services.AddSingleton<IAgentHarness>(
  services => services.GetRequiredService<QwenCodeHarnessAdapter>()
);
builder.Services.AddSingleton(
  new ClaudeCodeHarnessOptions(
    builder.Configuration["AgenticRouter:ClaudeCode:ExecutablePath"],
    OperatingSystem.IsWindows()
      ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local",
        "bin",
        "claude.exe"
      )
      : null,
    Path.Combine(dataDirectory, "claude-code-runtime"),
    TimeSpan.FromSeconds(15),
    TimeSpan.FromMinutes(5)
  )
);
builder.Services.AddSingleton<ClaudeCodeHarnessAdapter>();
builder.Services.AddSingleton<IAgentHarness>(
  services => services.GetRequiredService<ClaudeCodeHarnessAdapter>()
);
builder.Services.AddSingleton<IHarnessRegistry, HarnessRegistry>();
builder.Services.AddScoped<
  IExecutionContextTurnRunner,
  ExecutionContextTurnRunner
>();
builder.Services.AddScoped<ISupervisionRouteResolver, SupervisionRouteResolver>();
builder.Services.AddScoped<ISupervisionExecutionEngine, SupervisionExecutionEngine>();
builder.Services.AddScoped<ISupervisionRecoveryService, SupervisionRecoveryService>();
builder.Services.AddSingleton<
  IDurableSupervisionRunCoordinator,
  DurableSupervisionRunCoordinator
>();
builder.Services.AddSingleton<ILocalSetupService, LocalSetupService>();
var configuredBenchmarkDirectory = builder.Configuration[
  "AgenticRouter:Benchmarking:RootDirectory"
];
var benchmarkDirectory = string.IsNullOrWhiteSpace(configuredBenchmarkDirectory)
  ? Path.Combine(
    Path.GetTempPath(),
    "agentic-router",
    "benchmark-runs"
  )
  : Path.GetFullPath(configuredBenchmarkDirectory);
builder.Services.AddSingleton(
  new BenchmarkWorkspaceOptions(
    benchmarkDirectory,
    builder.Environment.ContentRootPath
  )
);
builder.Services.AddSingleton<IBenchmarkWorkspaceFactory, BenchmarkWorkspaceFactory>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, FileSystemCreateBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, FileSystemReadBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, FileSystemUpdateBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, FileSystemDeleteBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, ContinuityBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, ScopeRetentionBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, RecoveryBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, ConvergenceBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, TerminalityBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, StaleConflictBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestDefinition, TruthfulReportBenchmark>();
builder.Services.AddSingleton<IBenchmarkTestRegistry, BenchmarkTestRegistry>();
builder.Services.AddScoped<IBenchmarkNativeExecutor, BenchmarkNativeExecutor>();
builder.Services.AddSingleton<IBenchmarkScorer, BenchmarkScorer>();
builder.Services.AddSingleton<IBenchmarkScoringProfileStore>(services =>
  new JsonBenchmarkScoringProfileStore(
    dataDirectory,
    services.GetRequiredService<IBenchmarkScorer>()
  )
);
builder.Services.AddSingleton<IBenchmarkResultStore>(
  new JsonBenchmarkResultStore(dataDirectory)
);
builder.Services.AddSingleton<IBenchmarkHistoryService, BenchmarkHistoryService>();
builder.Services.AddSingleton<IBenchmarkRecommendationStore>(
  new JsonBenchmarkRecommendationStore(dataDirectory)
);
builder.Services.AddSingleton<
  IBenchmarkRecommendationService,
  BenchmarkRecommendationService
>();
builder.Services.AddSingleton<
  IAutoModelHarnessRoutingService,
  AutoModelHarnessRoutingService
>();
builder.Services.AddSingleton<
  IBenchmarkRunCancellationRegistry,
  BenchmarkRunCancellationRegistry
>();
builder.Services.AddSingleton<
  IBenchmarkLiveRunCoordinator,
  BenchmarkLiveRunCoordinator
>();
builder.Services.AddScoped<IBenchmarkEngine, BenchmarkEngine>();
builder.Services.AddScoped<IModelDiagnosticService, ModelDiagnosticService>();
builder.Services.AddSingleton<IModelOrganizationService>(
  services => new ModelOrganizationService(
    dataDirectory,
    services.GetRequiredService<ISettingsStore>(),
    services.GetRequiredService<ISettingsValidator>(),
    services.GetRequiredService<IOllamaClient>(),
    services.GetRequiredService<IToolProtocolConformanceService>(),
    services.GetRequiredService<IWorkspaceProfileStore>(),
    services.GetRequiredService<IWorkspaceProfileService>()
  )
);
builder.Services.AddSingleton<
  IConversationProductivityService,
  ConversationProductivityService
>();
builder.Services.AddSingleton<
  IBenchmarkEnvironmentSnapshotProvider,
  BenchmarkEnvironmentSnapshotProvider
>();
builder.Services.AddSingleton<
  IResidentCoordinationEligibilityService,
  ResidentCoordinationEligibilityService
>();
builder.Services.AddSingleton<ResidentModelManager>();
builder.Services.AddSingleton<IResidentModelManager>(
  services => services.GetRequiredService<ResidentModelManager>()
);
builder.Services.AddSingleton<IOllamaRuntimeProfileService>(
  services => new OllamaRuntimeProfileService(
    dataDirectory,
    services.GetRequiredService<ISettingsStore>(),
    services.GetRequiredService<IOllamaClient>(),
    services.GetRequiredService<IResidentModelManager>(),
    services.GetRequiredService<IGpuMemoryMetricsProvider>(),
    services.GetRequiredService<ISystemMemoryMetricsProvider>()
  )
);
builder.Services.AddHostedService<SafeResidentModelHostedService>();
builder.Services.AddScoped<IRuntimeStatusService, RuntimeStatusService>();
builder.Services.AddScoped<ChatStreamService>();
builder.Services.AddScoped<IChatStreamService>(
  services => services.GetRequiredService<ChatStreamService>()
);
builder.Services.AddScoped<IExecutionSpecialistTurnService>(
  services => services.GetRequiredService<ChatStreamService>()
);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<TraceContextMiddleware>();
app.UseMiddleware<SafeModeMiddleware>();
app.UseAuthorization();
app.MapControllers();

var migration = await app.Services
  .GetRequiredService<DataMigrationService>()
  .InitializeAsync(
    safeModeRequested,
    CancellationToken.None
  );

await app.Services
  .GetRequiredService<ISettingsStore>()
  .GetAsync(
    CancellationToken.None
  );

await app.Services
  .GetRequiredService<IWorkspaceProfileService>()
  .InitializeAsync(
    CancellationToken.None
  );
if (!safeModeState.Enabled)
{
  await app.Services
    .GetRequiredService<IPersistentSessionService>()
    .RecoverInterruptedAsync(
      CancellationToken.None
    );
  await app.Services
    .GetRequiredService<IDurableSupervisionRunCoordinator>()
    .InitializeAsync(
      CancellationToken.None
    );
}
await app.Services
  .GetRequiredService<IUsageReconciliationService>()
  .InitializeAsync(
    CancellationToken.None
  );

await app.RunAsync();
