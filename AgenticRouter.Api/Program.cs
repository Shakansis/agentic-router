using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Models;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Routing;
using AgenticRouter.Api.Runtime;

var builder = WebApplication.CreateBuilder(
  args
);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddControllers();
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>();
builder.Services.AddSingleton<ISettingsValidator, SettingsValidator>();
builder.Services.AddSingleton<ISettingsStore>(
  services =>
  {
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

    return new JsonSettingsStore(
      dataDirectory,
      services.GetRequiredService<ISettingsValidator>(),
      services.GetRequiredService<ILogger<JsonSettingsStore>>()
    );
  }
);
builder.Services.AddSingleton<IGpuDiscoveryService, WindowsGpuDiscoveryService>();
builder.Services.AddSingleton<IMarkdownRenderer, SafeMarkdownRenderer>();
builder.Services.AddSingleton<IRouterResponseParser, RouterResponseParser>();
builder.Services.AddScoped<IIntentionRouter, IntentionRouter>();
builder.Services.AddScoped<IModelResolver, ModelResolver>();
builder.Services.AddScoped<IConversationContextBuilder, ConversationContextBuilder>();
builder.Services.AddScoped<ITrustedWorkspaceService, TrustedWorkspaceService>();
builder.Services.AddSingleton<IFolderPickerService, WindowsFolderPickerService>();
builder.Services.AddScoped<ILocalActionService, LocalActionService>();
builder.Services.AddScoped<IApprovalPolicyService, ApprovalPolicyService>();
builder.Services.AddScoped<IProcessExecutionService, ProcessExecutionService>();
builder.Services.AddScoped<ILocalActionPlanner, LocalActionPlanner>();
builder.Services.AddScoped<IExpertExecutionGuidanceService, ExpertExecutionGuidanceService>();
builder.Services.AddSingleton<IApprovalCoordinator, ApprovalCoordinator>();
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

await app.RunAsync();
