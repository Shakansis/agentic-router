namespace AgenticRouter.Api.Configuration;

public sealed class SettingsValidator : ISettingsValidator
{
    public IReadOnlyDictionary<string, string[]> Validate(
      ApplicationSettings settings
    )
    {
        var errors = new Dictionary<string, List<string>>(
          StringComparer.Ordinal
        );

        if (settings.SchemaVersion != 1)
        {
            AddError(
              errors,
              "schemaVersion",
              "Schema version must be 1."
            );
        }

        if (!Uri.TryCreate(
          settings.OllamaUrl,
          UriKind.Absolute,
          out var ollamaUri
        ) || (
          ollamaUri.Scheme != Uri.UriSchemeHttp
          && ollamaUri.Scheme != Uri.UriSchemeHttps
        ))
        {
            AddError(
              errors,
              "ollamaUrl",
              "Ollama URL must be an absolute HTTP or HTTPS URL."
            );
        }

        ValidateRequiredModel(
          errors,
          "routerModel",
          settings.RouterModel
        );
        ValidateRequiredModel(
          errors,
          "defaultModel",
          settings.DefaultModel
        );

        if (string.IsNullOrWhiteSpace(
          settings.DefaultGpu
        ))
        {
            AddError(
              errors,
              "defaultGpu",
              "Default GPU is required."
            );
        }

        foreach (var intentionName in SettingsDefaults.IntentionNames)
        {
            if (!settings.Intentions.TryGetValue(
              intentionName,
              out var intention
            ))
            {
                AddError(
                  errors,
                  $"intentions.{intentionName}",
                  "Intention configuration is required."
                );
                continue;
            }

            if (string.IsNullOrWhiteSpace(
              intention.Model
            ))
            {
                AddError(
                  errors,
                  $"intentions.{intentionName}.model",
                  "Model selection is required."
                );
            }

            if (string.IsNullOrWhiteSpace(
              intention.Gpu
            ))
            {
                AddError(
                  errors,
                  $"intentions.{intentionName}.gpu",
                  "GPU selection is required."
                );
            }

            if (string.IsNullOrWhiteSpace(
              intention.SystemPrompt
            ))
            {
                AddError(
                  errors,
                  $"intentions.{intentionName}.systemPrompt",
                  "System prompt is required."
                );
            }
            else if (intention.SystemPrompt.Length > 8_000)
            {
                AddError(
                  errors,
                  $"intentions.{intentionName}.systemPrompt",
                  "System prompt must contain at most 8000 characters."
                );
            }
        }

        var unknownIntentions = settings.Intentions.Keys
          .Except(
            SettingsDefaults.IntentionNames,
            StringComparer.Ordinal
          );

        foreach (var unknownIntention in unknownIntentions)
        {
            AddError(
              errors,
              $"intentions.{unknownIntention}",
              "Unknown intention."
            );
        }

        return errors.ToDictionary(
          pair => pair.Key,
          pair => pair.Value.ToArray(),
          StringComparer.Ordinal
        );
    }

    private static void ValidateRequiredModel(
      IDictionary<string, List<string>> errors,
      string field,
      string value
    )
    {
        if (string.IsNullOrWhiteSpace(
          value
        ))
        {
            AddError(
              errors,
              field,
              "Model name is required."
            );
        }
        else if (value.Length > 256)
        {
            AddError(
              errors,
              field,
              "Model name must contain at most 256 characters."
            );
        }
    }

    private static void AddError(
      IDictionary<string, List<string>> errors,
      string field,
      string message
    )
    {
        if (!errors.TryGetValue(
          field,
          out var fieldErrors
        ))
        {
            fieldErrors = [];
            errors[field] = fieldErrors;
        }

        fieldErrors.Add(
          message
        );
    }
}
