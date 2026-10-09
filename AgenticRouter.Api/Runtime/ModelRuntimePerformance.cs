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
    var configured = settings.OllamaRuntime.ModelOverrides.FirstOrDefault(
      candidate => candidate.Provider == provider
        && candidate.Model == model
        && candidate.Digest == digest
    )?.Performance;
    if (provider != "ollama-local") return configured;
    var cache = configured?.KvCacheType ?? settings.OllamaRuntime.ManagedKvCacheType;
    return cache == "auto" ? configured : (configured ?? new ModelRuntimePerformanceSettings()) with { KvCacheType = cache };
  }

  public static string ResolveKvCacheType(ApplicationSettings settings, string model, string? digest)
    => Resolve(settings, "ollama-local", model, digest)?.KvCacheType ?? settings.OllamaRuntime.ManagedKvCacheType;

  public static string Signature(ModelRuntimePerformanceSettings? performance)
  {
    return performance?.HasExplicitValues == true
      ? $";draftTokens={performance.DraftTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}"
        + $";batchSize={performance.BatchSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto"}"
        + (performance.KvCacheType is null ? string.Empty : $";kvCacheType={performance.KvCacheType}")
      : string.Empty;
  }
}
