using System.Text.Json;

namespace AgenticRouter.EndToEndTests;

internal sealed record TestApplicationSettings
{
  public int SchemaVersion { get; init; } = 1;

  public string OllamaUrl { get; init; } = string.Empty;

  public string RouterModel { get; init; } = "router:latest";

  public string DefaultModel { get; init; } = "alpha:latest";

  public string DefaultGpu { get; init; } = "auto";

  public Dictionary<string, TestIntentionSettings> Intentions { get; init; } = [];

  public TestRuntimeSettings Runtime { get; init; } = new();

  public static TestApplicationSettings Create(
    string ollamaUrl
  )
  {
    return new TestApplicationSettings
    {
      OllamaUrl = ollamaUrl,
      Intentions = new Dictionary<string, TestIntentionSettings>(
        StringComparer.Ordinal
      )
      {
        ["general-chat"] = new(
          "default",
          "default",
          "You are a clear test assistant."
        ),
        ["documentation"] = new(
          "docs:latest",
          "default",
          "You write documentation."
        ),
        ["software-development"] = new(
          "default",
          "default",
          "You write software."
        ),
        ["software-architecture"] = new(
          "beta:code",
          "default",
          "You design software."
        ),
        ["rpg-storytelling"] = new(
          "default",
          "default",
          "You tell RPG stories."
        ),
        ["review-and-testing"] = new(
          "default",
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
  string Gpu,
  string SystemPrompt
);

internal sealed record TestRuntimeSettings
{
  public string ResidentModelPolicy { get; init; } = "adaptive";

  public int ResidentModelVerificationIntervalSeconds { get; init; } = 10;

  public int RuntimeStatusIdleRefreshSeconds { get; init; } = 5;

  public int RuntimeStatusActiveRefreshSeconds { get; init; } = 2;
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
