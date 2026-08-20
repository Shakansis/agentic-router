using AgenticRouter.Api.Providers;
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
      "actionModel",
      settings.ActionModel
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
    ValidateIncidents(
      errors,
      settings.Incidents
    );
    ValidateCloudProviders(
      errors,
      settings.CloudProviders
    );
    ValidateWebSearch(
      errors,
      settings.WebSearch
    );

    if (settings.ModelOrganization.MaximumProfiles is < 1 or > 50)
    {
      AddError(
        errors,
        "modelOrganization.maximumProfiles",
        "Maximum model configuration profiles must be between 1 and 50."
      );
    }

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
    ValidateOllamaRuntime(
      errors,
      settings.OllamaRuntime,
      settings.Context.ProviderContextTokens
    );

    ValidateGpuSelection(
      errors,
      "defaultGpu",
      settings.DefaultGpu,
      false
    );
    ValidateGpuSelection(
      errors,
      "routerGpu",
      settings.RouterGpu,
      true
    );
    ValidateGpuSelection(
      errors,
      "actionGpu",
      settings.ActionGpu,
      true
    );
    ValidateGpuSelection(
      errors,
      "coordinatorGpu",
      settings.CoordinatorGpu,
      true
    );

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

      ValidateCloudFallbackShape(
        errors,
        intentionName,
        intention,
        settings.DefaultModel
      );

      ValidateGpuSelection(
        errors,
        $"intentions.{intentionName}.gpu",
        intention.Gpu,
        true
      );

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

    ValidateModelGpuConflicts(
      errors,
      settings
    );

    return errors.ToDictionary(
      pair => pair.Key,
      pair => pair.Value.ToArray(),
      StringComparer.Ordinal
    );
  }

  private static void ValidateModelGpuConflicts(
    IDictionary<string, List<string>> errors,
    ApplicationSettings settings
  )
  {
    var assignments = new Dictionary<
      string,
      List<(string Gpu, string Field)>
    >(
      StringComparer.OrdinalIgnoreCase
    );
    AddGpuAssignment(
      assignments,
      settings.RouterModel,
      settings.RouterGpu,
      "routerGpu",
      settings.DefaultGpu
    );
    AddGpuAssignment(
      assignments,
      settings.ActionModel,
      settings.ActionGpu,
      "actionGpu",
      settings.DefaultGpu
    );
    AddGpuAssignment(
      assignments,
      settings.CoordinatorModel,
      settings.CoordinatorGpu,
      "coordinatorGpu",
      settings.DefaultGpu
    );
    AddGpuAssignment(
      assignments,
      settings.DefaultModel,
      settings.DefaultGpu,
      "defaultGpu",
      settings.DefaultGpu
    );

    foreach (var intention in settings.Intentions)
    {
      var model = string.Equals(
        intention.Value.Model,
        "default",
        StringComparison.OrdinalIgnoreCase
      )
        ? settings.DefaultModel
        : intention.Value.Model;
      AddGpuAssignment(
        assignments,
        model,
        intention.Value.Gpu,
        $"intentions.{intention.Key}.gpu",
        settings.DefaultGpu
      );
      var fallback = string.Equals(
        intention.Value.FallbackModel,
        "default",
        StringComparison.OrdinalIgnoreCase
      )
        ? settings.DefaultModel
        : intention.Value.FallbackModel;
      AddGpuAssignment(
        assignments,
        fallback,
        intention.Value.Gpu,
        $"intentions.{intention.Key}.gpu",
        settings.DefaultGpu
      );
    }

    foreach (var assignment in assignments)
    {
      var distinct = assignment.Value.Select(
        item => item.Gpu
      ).Distinct(
        StringComparer.Ordinal
      ).ToArray();

      if (distinct.Length <= 1)
      {
        continue;
      }

      foreach (var field in assignment.Value.Select(
        item => item.Field
      ).Distinct(
        StringComparer.Ordinal
      ))
      {
        AddError(
          errors,
          field,
          $"Model '{assignment.Key}' cannot use conflicting GPU affinities in one Ollama daemon."
        );
      }
    }
  }

  private static void AddGpuAssignment(
    IDictionary<string, List<(string Gpu, string Field)>> assignments,
    string model,
    string selection,
    string field,
    string defaultGpu
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        model
      )
      || string.Equals(
        model,
        "none",
        StringComparison.OrdinalIgnoreCase
      )
      || string.Equals(
        model,
        "configure-model",
        StringComparison.OrdinalIgnoreCase
      )
      || !ProviderModelReference.Parse(
        model
      ).IsLocal
    )
    {
      return;
    }

    var effectiveGpu = string.Equals(
      selection,
      OllamaGpuSelection.Default,
      StringComparison.Ordinal
    )
      ? defaultGpu
      : selection;

    if (!assignments.TryGetValue(
      model,
      out var modelAssignments
    ))
    {
      modelAssignments = [];
      assignments[model] = modelAssignments;
    }

    modelAssignments.Add(
      (effectiveGpu, field)
    );
  }

  private static void ValidateGpuSelection(
    IDictionary<string, List<string>> errors,
    string field,
    string? selection,
    bool allowDefault
  )
  {
    if (OllamaGpuSelection.IsValid(
      selection,
      allowDefault
    ))
    {
      return;
    }

    AddError(
      errors,
      field,
      allowDefault
        ? "GPU selection must be default, auto, or an exact Ollama GPU index."
        : "GPU selection must be auto or an exact Ollama GPU index."
    );
  }

  private static void ValidateIncidents(
    Dictionary<string, List<string>> errors,
    IncidentJournalSettings settings
  )
  {
    ValidateInterval(errors, "incidents.retentionDays", settings.RetentionDays, 1, 365);
    ValidateLongRange(errors, "incidents.maximumFileBytes", settings.MaximumFileBytes, 65_536, 67_108_864);
    ValidateLongRange(errors, "incidents.maximumTotalBytes", settings.MaximumTotalBytes, settings.MaximumFileBytes, 1_073_741_824);
    ValidateInterval(errors, "incidents.maximumEventsPerTrace", settings.MaximumEventsPerTrace, 10, 5_000);
    ValidateInterval(errors, "incidents.browserMaximumEvents", settings.BrowserMaximumEvents, 1, settings.MaximumEventsPerTrace);
    ValidateLongRange(errors, "incidents.browserMaximumBytes", settings.BrowserMaximumBytes, 16_384, 4_194_304);
  }

  private static void ValidateLongRange(
    Dictionary<string, List<string>> errors,
    string key,
    long value,
    long minimum,
    long maximum
  )
  {
    if (value < minimum || value > maximum)
    {
      AddError(errors, key, $"Value must be between {minimum} and {maximum}.");
    }
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

  private static void ValidateWebSearch(
    IDictionary<string, List<string>> errors,
    WebSearchSettings webSearch
  )
  {
    if (webSearch.MaxResults is < 1 or > 10)
    {
      AddError(
        errors,
        "webSearch.maxResults",
        "Maximum web-search results must be between 1 and 10."
      );
    }

    if (webSearch.TimeoutSeconds is < 3 or > 60)
    {
      AddError(
        errors,
        "webSearch.timeoutSeconds",
        "Web-search timeout must be between 3 and 60 seconds."
      );
    }

    if (
      webSearch.OllamaEnabled
      && string.IsNullOrWhiteSpace(
        webSearch.OllamaSecretReference
      )
    )
    {
      AddError(
        errors,
        "webSearch.ollamaSecretReference",
        "Enabled Ollama Web Search requires a protected key reference."
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

    if (
      settings.AlertThresholds.Count is < 1 or > 5
      || settings.AlertThresholds.Any(
        threshold => threshold is < 1 or > 100
      )
      || settings.AlertThresholds.Distinct().Count()
        != settings.AlertThresholds.Count
      || !settings.AlertThresholds.SequenceEqual(
        settings.AlertThresholds.Order()
      )
    )
    {
      AddError(
        errors,
        "usage.alertThresholds",
        "Choose between one and five distinct alert thresholds in ascending order from 1 to 100."
      );
    }
  }

  private static void ValidateCloudProviders(
    IDictionary<string, List<string>> errors,
    CloudProvidersSettings providers
  )
  {
    ValidateCloudProvider(
      errors,
      "cloudProviders.groq",
      providers.Groq
    );
    ValidateCloudProvider(
      errors,
      "cloudProviders.googleAiStudio",
      providers.GoogleAiStudio
    );
    ValidateCloudProvider(
      errors,
      "cloudProviders.cerebras",
      providers.Cerebras
    );
  }

  private static void ValidateCloudProvider(
    IDictionary<string, List<string>> errors,
    string field,
    CloudProviderIntegrationSettings provider
  )
  {
    if (
      provider.ExpectedBillingMode is not (
        "free-tier"
        or "paid"
        or "unknown"
      )
    )
    {
      AddError(
        errors,
        $"{field}.expectedBillingMode",
        "Expected billing mode must be free-tier, paid, or unknown."
      );
    }

    if (
      provider.SecretReference is not null
      && (
        !provider.SecretReference.StartsWith(
          "secret-",
          StringComparison.Ordinal
        )
        || provider.SecretReference.Length != 39
      )
    )
    {
      AddError(
        errors,
        $"{field}.secretReference",
        "The protected secret reference is invalid."
      );
    }

    if (
      provider.Enabled
      && string.IsNullOrWhiteSpace(
        provider.SecretReference
      )
    )
    {
      AddError(
        errors,
        $"{field}.enabled",
        "An enabled cloud provider requires a protected API key."
      );
    }

    foreach (var pair in provider.ModelQuotas)
    {
      if (string.IsNullOrWhiteSpace(
        pair.Key
      ))
      {
        AddError(
          errors,
          $"{field}.modelQuotas",
          "Quota model identities cannot be empty."
        );
      }

      if (
        pair.Value.ShortWindowTokenLimit is <= 0
        || pair.Value.LongWindowTokenLimit is <= 0
      )
      {
        AddError(
          errors,
          $"{field}.modelQuotas.{pair.Key}",
          "Configured token limits must be greater than zero."
        );
      }

      if (
        pair.Value.ShortWindowMinutes is < 1 or > 43_200
        || pair.Value.LongWindowMinutes is < 1 or > 525_600
      )
      {
        AddError(
          errors,
          $"{field}.modelQuotas.{pair.Key}",
          "Configured quota windows are outside the supported range."
        );
      }
    }
  }

  private static void ValidateCloudFallbackShape(
    IDictionary<string, List<string>> errors,
    string intentionName,
    IntentionSettings intention,
    string defaultModel
  )
  {
    var primary = string.Equals(
      intention.Model,
      "default",
      StringComparison.OrdinalIgnoreCase
    )
      ? defaultModel
      : intention.Model;

    if (ProviderModelReference.Parse(
      primary
    ).IsLocal)
    {
      return;
    }

    var fallbackField = $"intentions.{intentionName}.fallbackModel";

    if (
      string.IsNullOrWhiteSpace(
        intention.FallbackModel
      )
      || string.Equals(
        intention.FallbackModel,
        "none",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      AddError(
        errors,
        fallbackField,
        "A cloud primary requires an installed Ollama local fallback."
      );
      return;
    }

    var fallback = string.Equals(
      intention.FallbackModel,
      "default",
      StringComparison.OrdinalIgnoreCase
    )
      ? defaultModel
      : intention.FallbackModel;

    if (!ProviderModelReference.Parse(
      fallback
    ).IsLocal)
    {
      AddError(
        errors,
        fallbackField,
        "A cloud primary fallback must be an Ollama local model."
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

  private static void ValidateOllamaRuntime(
    IDictionary<string, List<string>> errors,
    OllamaRuntimeSettings runtime,
    int providerContextCeiling
  )
  {
    if (runtime.ProfileSchemaVersion != 1)
    {
      AddError(
        errors,
        "ollamaRuntime.profileSchemaVersion",
        "Ollama runtime profile schema version must be 1."
      );
    }

    foreach (var role in OllamaRuntimeRoleIds.All)
    {
      if (!runtime.RoleDefaults.TryGetValue(
        role,
        out var profile
      ))
      {
        AddError(
          errors,
          $"ollamaRuntime.roleDefaults.{role}",
          "A runtime context profile is required for every supported role."
        );
        continue;
      }

      ValidateOllamaRoleProfile(
        errors,
        $"ollamaRuntime.roleDefaults.{role}",
        profile,
        providerContextCeiling
      );
    }

    foreach (var unsupported in runtime.RoleDefaults.Keys.Except(
      OllamaRuntimeRoleIds.All,
      StringComparer.Ordinal
    ))
    {
      AddError(
        errors,
        $"ollamaRuntime.roleDefaults.{unsupported}",
        "The Ollama runtime role is not supported."
      );
    }

    if (
      runtime.ContextEscalationLadder.Count == 0
      || runtime.ContextEscalationLadder.Count > 16
      || runtime.ContextEscalationLadder.Any(
        value => value is < 1_024 or > 131_072
      )
      || runtime.ContextEscalationLadder.Distinct().Count()
        != runtime.ContextEscalationLadder.Count
      || !runtime.ContextEscalationLadder.SequenceEqual(
        runtime.ContextEscalationLadder.Order()
      )
    )
    {
      AddError(
        errors,
        "ollamaRuntime.contextEscalationLadder",
        "The context escalation ladder must contain distinct ascending values between 1024 and 131072."
      );
    }

    if (runtime.ModelOverrides.Count > 100)
    {
      AddError(
        errors,
        "ollamaRuntime.modelOverrides",
        "At most 100 local Ollama model overrides may be configured."
      );
    }

    var identities = new HashSet<string>(
      StringComparer.Ordinal
    );

    for (var index = 0; index < runtime.ModelOverrides.Count; index++)
    {
      var modelOverride = runtime.ModelOverrides[index];
      var prefix = $"ollamaRuntime.modelOverrides.{index}";

      if (!string.Equals(
        modelOverride.Provider,
        "ollama-local",
        StringComparison.Ordinal
      ))
      {
        AddError(
          errors,
          $"{prefix}.provider",
          "Runtime overrides are supported only for ollama-local."
        );
      }

      if (
        string.IsNullOrWhiteSpace(
          modelOverride.Model
        )
        || modelOverride.Model.Length > 256
      )
      {
        AddError(
          errors,
          $"{prefix}.model",
          "An exact local model ID with at most 256 characters is required."
        );
      }

      if (
        string.IsNullOrWhiteSpace(
          modelOverride.Digest
        )
        || modelOverride.Digest.Length > 256
      )
      {
        AddError(
          errors,
          $"{prefix}.digest",
          "An exact model digest with at most 256 characters is required."
        );
      }

      if (!identities.Add(
        $"{modelOverride.Provider}|{modelOverride.Model}|{modelOverride.Digest}"
      ))
      {
        AddError(
          errors,
          prefix,
          "Duplicate runtime override identities are not allowed."
        );
      }

      if (modelOverride.Overrides.Count == 0)
      {
        AddError(
          errors,
          $"{prefix}.overrides",
          "At least one role override is required."
        );
      }

      foreach (var pair in modelOverride.Overrides)
      {
        if (!OllamaRuntimeRoleIds.All.Contains(
          pair.Key,
          StringComparer.Ordinal
        ))
        {
          AddError(
            errors,
            $"{prefix}.overrides.{pair.Key}",
            "The Ollama runtime role is not supported."
          );
          continue;
        }

        ValidateOllamaRoleProfile(
          errors,
          $"{prefix}.overrides.{pair.Key}",
          pair.Value,
          providerContextCeiling
        );
      }
    }

    if (runtime.Memory.TargetMaximumGpuUsagePercent is < 50 or > 100)
    {
      AddError(
        errors,
        "ollamaRuntime.memory.targetMaximumGpuUsagePercent",
        "Target maximum GPU usage must be between 50 and 100 percent."
      );
    }

    if (
      runtime.Memory.MinimumFreeVramBytes is < 0 or > 1_099_511_627_776
      || runtime.Memory.MinimumFreeSystemRamBytes is < 0 or > 4_398_046_511_104
    )
    {
      AddError(
        errors,
        "ollamaRuntime.memory",
        "Runtime memory headroom values are outside the supported bounds."
      );
    }

    foreach (var pair in runtime.Memory.Devices)
    {
      if (string.IsNullOrWhiteSpace(
        pair.Key
      ) || pair.Key.Length > 256)
      {
        AddError(
          errors,
          "ollamaRuntime.memory.devices",
          "Every GPU memory policy requires a bounded device ID."
        );
      }

      if (
        pair.Value.TargetMaximumUsagePercent is < 50 or > 100
        || pair.Value.MinimumFreeVramBytes is < 0 or > 1_099_511_627_776
      )
      {
        AddError(
          errors,
          $"ollamaRuntime.memory.devices.{pair.Key}",
          "The GPU-specific runtime memory policy is outside the supported bounds."
        );
      }
    }
  }

  private static void ValidateOllamaRoleProfile(
    IDictionary<string, List<string>> errors,
    string prefix,
    OllamaRoleRuntimeSettings profile,
    int providerContextCeiling
  )
  {
    if (
      profile.MinimumContextTokens < 1_024
      || profile.MinimumContextTokens > profile.TargetContextTokens
      || profile.TargetContextTokens > profile.MaximumContextTokens
      || profile.MaximumContextTokens > providerContextCeiling
    )
    {
      AddError(
        errors,
        prefix,
        "Context minimum, target, and maximum must be ordered and remain within the provider context ceiling."
      );
    }

    if (
      profile.OutputTokenLimit is < 128
      || profile.OutputTokenLimit >= profile.MaximumContextTokens
    )
    {
      AddError(
        errors,
        $"{prefix}.outputTokenLimit",
        "Output token limit must be at least 128 and smaller than the role maximum context."
      );
    }

    if (profile.KeepAlive is < -1 or > 86_400)
    {
      AddError(
        errors,
        $"{prefix}.keepAlive",
        "Keep alive must be -1 for indefinite residency or between 0 and 86400 seconds."
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
