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
    ValidateContext(
      errors,
      settings.Context
    );
    ValidateTrustedWorkspace(
      errors,
      settings.TrustedWorkspacePath
    );

    if (!string.Equals(
      settings.Runtime.ResidentModelPolicy,
      "adaptive",
      StringComparison.Ordinal
    ))
    {
      AddError(
        errors,
        "runtime.residentModelPolicy",
        "Resident model policy must be adaptive."
      );
    }

    ValidateInterval(
      errors,
      "runtime.residentModelVerificationIntervalSeconds",
      settings.Runtime.ResidentModelVerificationIntervalSeconds,
      10,
      300
    );
    ValidateInterval(
      errors,
      "runtime.runtimeStatusIdleRefreshSeconds",
      settings.Runtime.RuntimeStatusIdleRefreshSeconds,
      2,
      60
    );
    ValidateInterval(
      errors,
      "runtime.runtimeStatusActiveRefreshSeconds",
      settings.Runtime.RuntimeStatusActiveRefreshSeconds,
      1,
      10
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
      else if (intention.Model.Length > 256)
      {
        AddError(
          errors,
          $"intentions.{intentionName}.model",
          "Model selection must contain at most 256 characters."
        );
      }

      if (string.IsNullOrWhiteSpace(
        intention.FallbackModel
      ))
      {
        AddError(
          errors,
          $"intentions.{intentionName}.fallbackModel",
          "Fallback model selection is required."
        );
      }
      else if (intention.FallbackModel.Length > 256)
      {
        AddError(
          errors,
          $"intentions.{intentionName}.fallbackModel",
          "Fallback model selection must contain at most 256 characters."
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

  private static void ValidateContext(
    IDictionary<string, List<string>> errors,
    ContextSettings context
  )
  {
    if (context.DefaultContextTokens is < 1_024 or > 131_072)
    {
      AddError(
        errors,
        "context.defaultContextTokens",
        "Default context tokens must be between 1024 and 131072."
      );
    }

    if (
      context.ReservedResponseTokens < 256
      || context.ReservedResponseTokens >= context.DefaultContextTokens
    )
    {
      AddError(
        errors,
        "context.reservedResponseTokens",
        "Reserved response tokens must be at least 256 and smaller than the context limit."
      );
    }

    if (context.MaxConversationMessages is < 2 or > 200)
    {
      AddError(
        errors,
        "context.maxConversationMessages",
        "Maximum conversation messages must be between 2 and 200."
      );
    }
  }

  private static void ValidateTrustedWorkspace(
    IDictionary<string, List<string>> errors,
    string? path
  )
  {
    if (string.IsNullOrWhiteSpace(
      path
    ))
    {
      return;
    }

    if (path.Length > 1_024)
    {
      AddError(
        errors,
        "trustedWorkspacePath",
        "Trusted workspace path must contain at most 1024 characters."
      );
      return;
    }

    if (!Path.IsPathFullyQualified(
      path
    ))
    {
      AddError(
        errors,
        "trustedWorkspacePath",
        "Trusted workspace path must be absolute."
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

  private static void ValidateInterval(
    IDictionary<string, List<string>> errors,
    string field,
    int value,
    int minimum,
    int maximum
  )
  {
    if (value < minimum || value > maximum)
    {
      AddError(
        errors,
        field,
        $"Value must be between {minimum} and {maximum} seconds."
      );
    }
  }
}
