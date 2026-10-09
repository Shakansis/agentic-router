using AgenticRouter.Api.Configuration;

namespace AgenticRouter.Api.Runtime;

public static class ModelRuntimePerformance
{
  public static ModelRuntimePerformanceSettings? Resolve(
    ApplicationSettings settings,
    string provider,
    string model,
    string? digest
  )
  {
    return settings.OllamaRuntime.ModelOverrides.FirstOrDefault(
      candidate => candidate.Provider == provider
        && candidate.Model == model
        && candidate.Digest == digest
    )?.Performance;
  }

  public static string Signature(ModelRuntimePerformanceSettings? performance)
  {
    return performance?.HasExplicitValues == true
      ? $";draftTokens={performance.DraftTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}"
        + $";batchSize={performance.BatchSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}"
      : string.Empty;
  }
}
