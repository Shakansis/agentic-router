using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Models;
using AgenticRouter.Api.ProjectAwareness;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Routing;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.Usage;
using AgenticRouter.Api.WorkspaceProfiles;

var builder = WebApplication.CreateBuilder(
  args
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
builder.Services.AddSingleton<ISettingsStore>(
  services =>
  {
    return new JsonSettingsStore(
      dataDirectory,
      services.GetRequiredService<ISettingsValidator>(),
      services.GetRequiredService<ILogger<JsonSettingsStore>>()
    );
  }
);
builder.Services.AddSingleton<IProtectedSecretStore>(
  new DpapiProtectedSecretStore(
    dataDirectory
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
builder.Services.AddTransient<IOllamaClient, ProviderDispatchClient>();
builder.Services.AddSingleton<IWorkspaceProfileStore>(
  new WorkspaceProfileStore(
    dataDirectory
  )
);
builder.Services.AddSingleton<IWorkspaceProfileService, WorkspaceProfileService>();
builder.Services.AddSingleton<IPersistentSessionStore, PersistentSessionStore>();
builder.Services.AddSingleton<IPersistentSessionService, PersistentSessionService>();
builder.Services.AddSingleton<IGpuDiscoveryService, WindowsGpuDiscoveryService>();
builder.Services.AddSingleton<IMarkdownRenderer, SafeMarkdownRenderer>();
builder.Services.AddSingleton<IRouterResponseParser, RouterResponseParser>();
builder.Services.AddScoped<IIntentionRouter, IntentionRouter>();
builder.Services.AddScoped<IModelResolver, ModelResolver>();
builder.Services.AddScoped<IConversationContextBuilder, ConversationContextBuilder>();
builder.Services.AddScoped<ITrustedWorkspaceService, TrustedWorkspaceService>();
builder.Services.AddScoped<IProjectAwarenessService, ProjectAwarenessService>();
builder.Services.AddScoped<IRepositoryInstructionService, RepositoryInstructionService>();
builder.Services.AddSingleton<IFolderPickerService, WindowsFolderPickerService>();
builder.Services.AddScoped<ILocalActionService, LocalActionService>();
builder.Services.AddScoped<IApprovalPolicyService, ApprovalPolicyService>();
builder.Services.AddSingleton<IProcessExecutionService, ProcessExecutionService>();
builder.Services.AddScoped<IProcessPolicyService, ProcessPolicyService>();
builder.Services.AddScoped<IValidationProfileService, ValidationProfileService>();
builder.Services.AddSingleton<IGitRepositoryService, GitRepositoryService>();
builder.Services.AddSingleton<IGitDeliveryService, GitDeliveryService>();
builder.Services.AddScoped<ILocalActionPlanner, LocalActionPlanner>();
builder.Services.AddSingleton<IPlanningFailureClassifier, PlanningFailureClassifier>();
builder.Services.AddSingleton<IToolProtocolConformanceService, ToolProtocolConformanceService>();
builder.Services.AddSingleton<IExecutionPlanService, ExecutionPlanService>();
builder.Services.AddScoped<IExpertExecutionGuidanceService, ExpertExecutionGuidanceService>();
builder.Services.AddSingleton<IApprovalCoordinator, ApprovalCoordinator>();
builder.Services.AddSingleton<IRecoveryDecisionCoordinator, RecoveryDecisionCoordinator>();
builder.Services.AddSingleton<IExecutionSessionStore, ExecutionSessionStore>();
builder.Services.AddScoped<IModelDiagnosticService, ModelDiagnosticService>();
builder.Services.AddSingleton<ISystemMemoryMetricsProvider, WindowsSystemMemoryMetricsProvider>();
builder.Services.AddSingleton<IGpuMemoryMetricsProvider, WindowsGpuMemoryMetricsProvider>();
builder.Services.AddSingleton<ResidentModelManager>();
builder.Services.AddSingleton<IResidentModelManager>(
  services => services.GetRequiredService<ResidentModelManager>()
);
builder.Services.AddHostedService(
  services => services.GetRequiredService<ResidentModelManager>()
);
builder.Services.AddScoped<IRuntimeStatusService, RuntimeStatusService>();
builder.Services.AddScoped<IChatStreamService, ChatStreamService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();

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
await app.Services
  .GetRequiredService<IPersistentSessionService>()
  .RecoverInterruptedAsync(
    CancellationToken.None
  );

await app.RunAsync();
