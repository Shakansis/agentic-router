using AgenticRouter.Api.Usage;

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
      "coordinatorModel",
      settings.CoordinatorModel
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
    ValidateExecution(
      errors,
      settings.Execution
    );

    if (
      settings.Execution.MaxToolOutputTokens
      >= settings.Context.ProviderContextTokens
    )
    {
      AddError(
        errors,
        "execution.maxToolOutputTokens",
        "Maximum tool output tokens must be smaller than the provider context limit."
      );
    }

    ValidateProjectAwareness(
      errors,
      settings.ProjectAwareness
    );
    ValidateValidationProfile(
      errors,
      settings.ValidationProfile
    );
    ValidateSessionHistory(
      errors,
      settings.SessionHistory
    );
    ValidateGitDelivery(
      errors,
      settings.GitDelivery
    );
    ValidateUsage(
      errors,
      settings.Usage
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
    ValidateInterval(
      errors,
      "runtime.generationTimeoutSeconds",
      settings.Runtime.GenerationTimeoutSeconds,
      1,
      1_800
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

  private static void ValidateGitDelivery(
    IDictionary<string, List<string>> errors,
    GitDeliverySettings gitDelivery
  )
  {
    if (gitDelivery.MaxDiffBytesPerFile is < 4_096 or > 1_048_576)
    {
      AddError(
        errors,
        "gitDelivery.maxDiffBytesPerFile",
        "Maximum Git diff bytes per file must be between 4096 and 1048576."
      );
    }

    if (gitDelivery.MaxLogEntries is < 1 or > 200)
    {
      AddError(
        errors,
        "gitDelivery.maxLogEntries",
        "Maximum Git log entries must be between 1 and 200."
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

    if (context.ProviderContextTokens is < 1_024 or > 131_072)
    {
      AddError(
        errors,
        "context.providerContextTokens",
        "Provider context tokens must be between 1024 and 131072."
      );
    }

    if (context.DefaultContextTokens > context.ProviderContextTokens)
    {
      AddError(
        errors,
        "context.defaultContextTokens",
        "Default context tokens must not exceed the provider context limit."
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

  private static void ValidateExecution(
    IDictionary<string, List<string>> errors,
    ExecutionSettings execution
  )
  {
    ValidateRange(
      errors,
      "execution.directCoordinatorPlanningFailuresBeforeHandoff",
      execution.DirectCoordinatorPlanningFailuresBeforeHandoff,
      1,
      5
    );
    ValidateRange(
      errors,
      "execution.residentCoordinatorPlanningFailuresBeforeFailure",
      execution.ResidentCoordinatorPlanningFailuresBeforeFailure,
      1,
      10
    );
    ValidateRange(
      errors,
      "execution.maxCoordinatorHandoffsPerTurn",
      execution.MaxCoordinatorHandoffsPerTurn,
      0,
      3
    );
    ValidateRange(
      errors,
      "execution.maxToolCallsPerTurn",
      execution.MaxToolCallsPerTurn,
      1,
      100
    );
    ValidateRange(
      errors,
      "execution.maxConsecutiveToolFailures",
      execution.MaxConsecutiveToolFailures,
      1,
      10
    );
    ValidateRange(
      errors,
      "execution.maxRecoveryAttemptsPerTurn",
      execution.MaxRecoveryAttemptsPerTurn,
      1,
      20
    );
    ValidateRange(
      errors,
      "execution.maxTrackedFilesPerSession",
      execution.MaxTrackedFilesPerSession,
      1,
      500
    );
    ValidateRange(
      errors,
      "execution.maxRollbackBytesPerFile",
      execution.MaxRollbackBytesPerFile,
      1_024,
      16 * 1_048_576
    );
    ValidateRange(
      errors,
      "execution.maxRollbackBytesPerSession",
      execution.MaxRollbackBytesPerSession,
      execution.MaxRollbackBytesPerFile,
      128 * 1_048_576
    );
    ValidateRange(
      errors,
      "execution.maxSearchFiles",
      execution.MaxSearchFiles,
      1,
      10_000
    );
    ValidateRange(
      errors,
      "execution.maxSearchMatches",
      execution.MaxSearchMatches,
      1,
      5_000
    );
    ValidateRange(
      errors,
      "execution.maxToolOutputTokens",
      execution.MaxToolOutputTokens,
      256,
      16_384
    );
  }

  private static void ValidateProjectAwareness(
    IDictionary<string, List<string>> errors,
    ProjectAwarenessSettings projectAwareness
  )
  {
    ValidateRange(
      errors,
      "projectAwareness.maxProjectMarkers",
      projectAwareness.MaxProjectMarkers,
      10,
      500
    );
    ValidateRange(
      errors,
      "projectAwareness.maxInstructionBytes",
      projectAwareness.MaxInstructionBytes,
      1_024,
      1_048_576
    );
    ValidateRange(
      errors,
      "projectAwareness.maxPlanSteps",
      projectAwareness.MaxPlanSteps,
      1,
      8
    );
    ValidateRange(
      errors,
      "projectAwareness.maxPlanRevisions",
      projectAwareness.MaxPlanRevisions,
      0,
      3
    );
  }

  private static void ValidateValidationProfile(
    IDictionary<string, List<string>> errors,
    ValidationProfileSettings? profile
  )
  {
    if (profile is null)
    {
      return;
    }

    if (
      string.IsNullOrWhiteSpace(
        profile.Name
      )
      || profile.Name.Length > 80
    )
    {
      AddError(
        errors,
        "validationProfile.name",
        "Name must contain between 1 and 80 characters."
      );
    }

    if (profile.Steps.Count is < 1 or > 8)
    {
      AddError(
        errors,
        "validationProfile.steps",
        "A validation profile must contain between 1 and 8 steps."
      );
    }

    var identifiers = new HashSet<string>(
      StringComparer.Ordinal
    );

    for (var index = 0; index < profile.Steps.Count; index++)
    {
      var step = profile.Steps[index];
      var prefix = $"validationProfile.steps[{index}]";

      if (
        string.IsNullOrWhiteSpace(
          step.Id
        )
        || step.Id.Length > 40
        || !identifiers.Add(
          step.Id
        )
      )
      {
        AddError(
          errors,
          $"{prefix}.id",
          "Step IDs must be unique and contain between 1 and 40 characters."
        );
      }

      if (
        string.IsNullOrWhiteSpace(
          step.Label
        )
        || step.Label.Length > 100
      )
      {
        AddError(
          errors,
          $"{prefix}.label",
          "Label must contain between 1 and 100 characters."
        );
      }

      if (
        string.IsNullOrWhiteSpace(
          step.Executable
        )
        || step.Executable.Length > 260
        || step.Executable.Contains(
          '\0',
          StringComparison.Ordinal
        )
      )
      {
        AddError(
          errors,
          $"{prefix}.executable",
          "Executable is required and must be a structured executable value."
        );
      }
      else if (
        !Path.GetFileNameWithoutExtension(
          step.Executable
        ).Equals(
          "dotnet",
          StringComparison.OrdinalIgnoreCase
        )
        && !Path.GetFileNameWithoutExtension(
          step.Executable
        ).Equals(
          "git",
          StringComparison.OrdinalIgnoreCase
        )
      )
      {
        AddError(
          errors,
          $"{prefix}.executable",
          "Validation executable must be allowed for structured execution: dotnet or read-only git."
        );
      }

      if (
        step.Arguments.Count > 100
        || step.Arguments.Any(
          argument => argument.Length > 2_048 || argument.Contains(
            '\0',
            StringComparison.Ordinal
          )
        )
      )
      {
        AddError(
          errors,
          $"{prefix}.arguments",
          "Arguments exceed the supported structured-command limits."
        );
      }
      else if (
        step.Arguments.Count == 0
        || !IsAllowedValidationCommand(
          step.Executable,
          step.Arguments[0]
        )
      )
      {
        AddError(
          errors,
          $"{prefix}.arguments",
          "The validation command is not on the existing structured-command allowlist."
        );
      }

      if (
        string.IsNullOrWhiteSpace(
          step.WorkingDirectory
        )
        || Path.IsPathFullyQualified(
          step.WorkingDirectory
        )
        || step.WorkingDirectory.Split(
          [
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
          ],
          StringSplitOptions.RemoveEmptyEntries
        ).Any(
          part => part == ".."
        )
      )
      {
        AddError(
          errors,
          $"{prefix}.workingDirectory",
          "Working directory must be a relative path inside the trusted workspace."
        );
      }

      if (step.TimeoutSeconds is < 1 or > 120)
      {
        AddError(
          errors,
          $"{prefix}.timeoutSeconds",
          "Timeout must be between 1 and 120 seconds."
        );
      }
    }
  }

  private static bool IsAllowedValidationCommand(
    string executable,
    string command
  )
  {
    var name = Path.GetFileNameWithoutExtension(
      executable
    );

    if (name.Equals(
      "dotnet",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      return command.Equals(
        "build",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "test",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "format",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "restore",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "--info",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "--version",
        StringComparison.OrdinalIgnoreCase
      );
    }

    return name.Equals(
      "git",
      StringComparison.OrdinalIgnoreCase
    ) && (
      command.Equals(
        "status",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "diff",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "log",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "show",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "branch",
        StringComparison.OrdinalIgnoreCase
      ) || command.Equals(
        "rev-parse",
        StringComparison.OrdinalIgnoreCase
      )
    );
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

  private static void ValidateSessionHistory(
    IDictionary<string, List<string>> errors,
    SessionHistorySettings settings
  )
  {
    ValidateRange(
      errors,
      "sessionHistory.maxSessionsPerWorkspace",
      settings.MaxSessionsPerWorkspace,
      1,
      200
    );
    ValidateRange(
      errors,
      "sessionHistory.maxSessionBytes",
      settings.MaxSessionBytes,
      262_144,
      20_971_520
    );
    ValidateRange(
      errors,
      "sessionHistory.maxStoredProcessOutputBytesPerTurn",
      settings.MaxStoredProcessOutputBytesPerTurn,
      1_024,
      262_144
    );
    ValidateRange(
      errors,
      "sessionHistory.maxStoredDiffBytesPerTurn",
      settings.MaxStoredDiffBytesPerTurn,
      4_096,
      1_048_576
    );
  }

  private static void ValidateUsage(
    IDictionary<string, List<string>> errors,
    UsageSettings settings
  )
  {
    ValidateRange(
      errors,
      "usage.retentionDays",
      settings.RetentionDays,
      1,
      730
    );
    ValidateRange(
      errors,
      "usage.maxEventBytes",
      settings.MaxEventBytes,
      4_096,
      65_536
    );
    ValidateRange(
      errors,
      "usage.providerShortWindowMinutes",
      settings.ProviderShortWindowMinutes,
      5,
      10_080
    );
    ValidateRange(
      errors,
      "usage.providerLongWindowMinutes",
      settings.ProviderLongWindowMinutes,
      settings.ProviderShortWindowMinutes,
      43_200
    );
    ValidateRange(
      errors,
      "usage.customRollingWindowMinutes",
      settings.CustomRollingWindowMinutes,
      5,
      43_200
    );

    if (!UsageWindowIds.All.Contains(
      settings.SelectedWindow
    ))
    {
      AddError(
        errors,
        "usage.selectedWindow",
        "Selected usage window is not supported."
      );
    }

    if (
      settings.PinnedWindows.Count > 4
      || settings.PinnedWindows.Distinct(
        StringComparer.Ordinal
      ).Count() != settings.PinnedWindows.Count
      || settings.PinnedWindows.Any(
        window => !UsageWindowIds.All.Contains(
          window
        )
      )
    )
    {
      AddError(
        errors,
        "usage.pinnedWindows",
        "Choose up to four distinct supported usage windows."
      );
    }

    if (
      string.IsNullOrWhiteSpace(
        settings.ComparisonProvider
      )
      || settings.ComparisonProvider.Length > 100
    )
    {
      AddError(
        errors,
        "usage.comparisonProvider",
        "Comparison provider is required and must contain at most 100 characters."
      );
    }

    if (
      string.IsNullOrWhiteSpace(
        settings.ComparisonModel
      )
      || settings.ComparisonModel.Length > 256
    )
    {
      AddError(
        errors,
        "usage.comparisonModel",
        "Comparison model is required and must contain at most 256 characters."
      );
    }

    if (
      string.IsNullOrWhiteSpace(
        settings.OllamaPlanReference
      )
      || settings.OllamaPlanReference.Length > 40
    )
    {
      AddError(
        errors,
        "usage.ollamaPlanReference",
        "Ollama plan reference is required and must contain at most 40 characters."
      );
    }
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

  private static void ValidateRange(
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
        $"Value must be between {minimum} and {maximum}."
      );
    }
  }
}
