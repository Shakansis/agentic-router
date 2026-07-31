using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Providers;

public interface ICloudFallbackPolicy
{
  Task<IReadOnlyDictionary<string, string[]>> ValidateAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  );
}

public sealed class CloudFallbackPolicy : ICloudFallbackPolicy
{
  private readonly IOllamaClient _models;

  public CloudFallbackPolicy(
    IOllamaClient models
  )
  {
    _models = models;
  }

  public async Task<IReadOnlyDictionary<string, string[]>> ValidateAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    var cloudIntentions = settings.Intentions
      .Where(
        pair => !ResolvePrimary(
          pair.Value,
          settings.DefaultModel
        ).IsLocal
      )
      .ToArray();

    if (cloudIntentions.Length == 0)
    {
      return new Dictionary<string, string[]>(
        StringComparer.Ordinal
      );
    }

    IReadOnlyList<InstalledModel> models;

    try
    {
      models = await _models.GetModelsAsync(
        new Uri(
          settings.OllamaUrl,
          UriKind.Absolute
        ),
        cancellationToken
      );
    }
    catch (OllamaProviderException)
    {
      return cloudIntentions.ToDictionary(
        pair => $"intentions.{pair.Key}.fallbackModel",
        _ => new[]
        {
          "The Ollama local fallback could not be verified because local model discovery is unavailable."
        },
        StringComparer.Ordinal
      );
    }

    var localNames = models
      .Where(
        model => ProviderModelReference.Parse(
          model.Name
        ).IsLocal
      )
      .Select(
        model => model.Name
      )
      .ToArray();
    var errors = new Dictionary<string, string[]>(
      StringComparer.Ordinal
    );

    foreach (var pair in cloudIntentions)
    {
      var fallback = ResolveFallback(
        pair.Value,
        settings.DefaultModel
      );
      var matches = localNames.Count(
        model => string.Equals(
          model,
          fallback,
          StringComparison.OrdinalIgnoreCase
        )
      );

      if (matches == 0)
      {
        errors[$"intentions.{pair.Key}.fallbackModel"] =
        [
          $"The required Ollama local fallback '{fallback}' is not installed."
        ];
      }
      else if (matches > 1)
      {
        errors[$"intentions.{pair.Key}.fallbackModel"] =
        [
          $"The Ollama local fallback identity '{fallback}' is ambiguous."
        ];
      }
    }

    return errors;
  }

  public static ProviderModelReference ResolvePrimary(
    IntentionSettings intention,
    string defaultModel
  )
  {
    return ProviderModelReference.Parse(
      string.Equals(
        intention.Model,
        "default",
        StringComparison.OrdinalIgnoreCase
      )
        ? defaultModel
        : intention.Model
    );
  }

  public static string ResolveFallback(
    IntentionSettings intention,
    string defaultModel
  )
  {
    return string.Equals(
      intention.FallbackModel,
      "default",
      StringComparison.OrdinalIgnoreCase
    )
      ? defaultModel
      : intention.FallbackModel;
  }
}
