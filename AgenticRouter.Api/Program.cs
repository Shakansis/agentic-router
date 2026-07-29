using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Devices;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Providers.Ollama;

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
