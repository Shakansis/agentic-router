using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Configuration;

public interface IPortableYamlSettingsService
{
  string Export(
    ApplicationSettings settings
  );

  PortableYamlImportResult Import(
    string yaml,
    ApplicationSettings current
  );
}

public sealed record PortableYamlImportResult(
  ApplicationSettings? Settings,
  IReadOnlyDictionary<string, string[]> Errors
);

public sealed class PortableYamlSettingsService : IPortableYamlSettingsService
{
  public const int MaximumYamlCharacters = 262_144;
  private static readonly string[] TopLevelKeys =
  [
    "schema_version",
    "provider",
    "models",
    "routing",
    "context",
    "runtime",
    "ollama_runtime",
    "execution",
    "project_awareness",
    "session_history",
    "git_delivery",
    "usage",
    "web_search"
  ];

  public string Export(
    ApplicationSettings settings
  )
  {
    var yaml = new StringBuilder();
    yaml.AppendLine(
      "# Agentic Router portable configuration"
    );
    yaml.AppendLine(
      "# Workspace paths, conversations, validation commands, and approvals are not exported."
    );
    Scalar(
      yaml,
      0,
      "schema_version",
      settings.SchemaVersion
    );
    yaml.AppendLine(
      "provider:"
    );
    Scalar(
      yaml,
      1,
      "type",
      "ollama"
    );
    Scalar(
      yaml,
      1,
      "url",
      settings.OllamaUrl
    );
    yaml.AppendLine(
      "models:"
    );
    ModelGroup(
      yaml,
      "router",
      settings.RouterModel,
      null
    );
    ModelGroup(
      yaml,
      "action",
      settings.ActionModel,
      settings.CoordinatorModel
    );
    ModelGroup(
      yaml,
      "coordinator",
      settings.CoordinatorModel,
      null
    );
    ModelGroup(
      yaml,
      "default",
      settings.DefaultModel,
      null
    );

    foreach (var intentionName in SettingsDefaults.IntentionNames)
    {
      var intention = settings.Intentions[intentionName];
      ModelGroup(
        yaml,
        intentionName,
        intention.Model,
        intention.FallbackModel
      );
    }

    yaml.AppendLine(
      "routing:"
    );
    Scalar(
      yaml,
      1,
      "default_gpu",
      settings.DefaultGpu
    );
    Scalar(
      yaml,
      1,
      "router_gpu",
      settings.RouterGpu
    );
    Scalar(
      yaml,
      1,
      "action_gpu",
      settings.ActionGpu
    );
    Scalar(
      yaml,
      1,
      "coordinator_gpu",
      settings.CoordinatorGpu
    );
    yaml.AppendLine(
      "  gpus:"
    );

    foreach (var intentionName in SettingsDefaults.IntentionNames)
    {
      Scalar(
        yaml,
        2,
        intentionName,
        settings.Intentions[intentionName].Gpu
      );
    }

    yaml.AppendLine(
      "  system_prompts:"
    );

    foreach (var intentionName in SettingsDefaults.IntentionNames)
    {
      Scalar(
        yaml,
        2,
        intentionName,
        settings.Intentions[intentionName].SystemPrompt
      );
    }

    yaml.AppendLine(
      "context:"
    );
    Scalar(
      yaml,
      1,
      "default_context_tokens",
      settings.Context.DefaultContextTokens
    );
    Scalar(
      yaml,
      1,
      "provider_context_tokens",
      settings.Context.ProviderContextTokens
    );
    Scalar(
      yaml,
      1,
      "reserved_response_tokens",
      settings.Context.ReservedResponseTokens
    );
    Scalar(
      yaml,
      1,
      "max_conversation_messages",
      settings.Context.MaxConversationMessages
    );
    yaml.AppendLine(
      "runtime:"
    );
    Scalar(
      yaml,
      1,
      "resident_model_policy",
      settings.Runtime.ResidentModelPolicy
    );
    Scalar(
      yaml,
      1,
      "resident_model_verification_interval_seconds",
      settings.Runtime.ResidentModelVerificationIntervalSeconds
    );
    Scalar(
      yaml,
      1,
      "status_idle_refresh_seconds",
      settings.Runtime.RuntimeStatusIdleRefreshSeconds
    );
    Scalar(
      yaml,
      1,
      "status_active_refresh_seconds",
      settings.Runtime.RuntimeStatusActiveRefreshSeconds
    );
    Scalar(
      yaml,
      1,
      "generation_timeout_seconds",
      settings.Runtime.GenerationTimeoutSeconds
    );
    ExportOllamaRuntime(
      yaml,
      settings.OllamaRuntime
    );
    yaml.AppendLine(
      "execution:"
    );
    Scalar(
      yaml,
      1,
      "direct_planning_failures_before_handoff",
      settings.Execution.DirectCoordinatorPlanningFailuresBeforeHandoff
    );
    Scalar(
      yaml,
      1,
      "resident_planning_failures_before_failure",
      settings.Execution.ResidentCoordinatorPlanningFailuresBeforeFailure
    );
    Scalar(
      yaml,
      1,
      "max_coordinator_handoffs_per_turn",
      settings.Execution.MaxCoordinatorHandoffsPerTurn
    );
    Scalar(
      yaml,
      1,
      "max_tool_calls_per_turn",
      settings.Execution.MaxToolCallsPerTurn
    );
    Scalar(
      yaml,
      1,
      "max_consecutive_tool_failures",
      settings.Execution.MaxConsecutiveToolFailures
    );
    Scalar(
      yaml,
      1,
      "max_recovery_attempts_per_turn",
      settings.Execution.MaxRecoveryAttemptsPerTurn
    );
    Scalar(
      yaml,
      1,
      "max_tracked_files_per_session",
      settings.Execution.MaxTrackedFilesPerSession
    );
    Scalar(
      yaml,
      1,
      "max_rollback_bytes_per_file",
      settings.Execution.MaxRollbackBytesPerFile
    );
    Scalar(
      yaml,
      1,
      "max_rollback_bytes_per_session",
      settings.Execution.MaxRollbackBytesPerSession
    );
    Scalar(
      yaml,
      1,
      "max_search_files",
      settings.Execution.MaxSearchFiles
    );
    Scalar(
      yaml,
      1,
      "max_search_matches",
      settings.Execution.MaxSearchMatches
    );
    Scalar(
      yaml,
      1,
      "max_tool_output_tokens",
      settings.Execution.MaxToolOutputTokens
    );
    Scalar(
      yaml,
      1,
      "max_direct_plan_steps",
      settings.Execution.MaxDirectPlanSteps
    );
    Scalar(
      yaml,
      1,
      "plan_effort",
      settings.Execution.PhaseEffort.Plan
    );
    Scalar(
      yaml,
      1,
      "work_effort",
      settings.Execution.PhaseEffort.Work
    );
    Scalar(
      yaml,
      1,
      "verify_effort",
      settings.Execution.PhaseEffort.Verify
    );
    Scalar(
      yaml,
      1,
      "complete_effort",
      settings.Execution.PhaseEffort.Complete
    );
    Scalar(
      yaml,
      1,
      "recovery_effort",
      settings.Execution.PhaseEffort.Recovery
    );
    yaml.AppendLine(
      "project_awareness:"
    );
    Scalar(
      yaml,
      1,
      "max_project_markers",
      settings.ProjectAwareness.MaxProjectMarkers
    );
    Scalar(
      yaml,
      1,
      "max_instruction_bytes",
      settings.ProjectAwareness.MaxInstructionBytes
    );
    Scalar(
      yaml,
      1,
      "max_plan_steps",
      settings.ProjectAwareness.MaxPlanSteps
    );
    Scalar(
      yaml,
      1,
      "max_plan_revisions",
      settings.ProjectAwareness.MaxPlanRevisions
    );
    yaml.AppendLine(
      "session_history:"
    );
    Scalar(
      yaml,
      1,
      "max_sessions_per_workspace",
      settings.SessionHistory.MaxSessionsPerWorkspace
    );
    Scalar(
      yaml,
      1,
      "max_session_bytes",
      settings.SessionHistory.MaxSessionBytes
    );
    Scalar(
      yaml,
      1,
      "max_process_output_bytes_per_turn",
      settings.SessionHistory.MaxStoredProcessOutputBytesPerTurn
    );
    Scalar(
      yaml,
      1,
      "max_diff_bytes_per_turn",
      settings.SessionHistory.MaxStoredDiffBytesPerTurn
    );
    yaml.AppendLine(
      "git_delivery:"
    );
    Scalar(
      yaml,
      1,
      "enabled",
      settings.GitDelivery.Enabled
    );
    Scalar(
      yaml,
      1,
      "require_validation_before_commit",
      settings.GitDelivery.RequireValidationBeforeCommit
    );
    Scalar(
      yaml,
      1,
      "allow_explicit_commit_without_validation",
      settings.GitDelivery.AllowExplicitCommitWithoutValidation
    );
    Scalar(
      yaml,
      1,
      "max_diff_bytes_per_file",
      settings.GitDelivery.MaxDiffBytesPerFile
    );
    Scalar(
      yaml,
      1,
      "max_log_entries",
      settings.GitDelivery.MaxLogEntries
    );
    yaml.AppendLine(
      "usage:"
    );
    Scalar(
      yaml,
      1,
      "retention_days",
      settings.Usage.RetentionDays
    );
    Scalar(
      yaml,
      1,
      "max_event_bytes",
      settings.Usage.MaxEventBytes
    );
    Scalar(
      yaml,
      1,
      "selected_window",
      settings.Usage.SelectedWindow
    );
    Scalar(
      yaml,
      1,
      "pinned_windows",
      string.Join(
        ",",
        settings.Usage.PinnedWindows
      )
    );
    Scalar(
      yaml,
      1,
      "provider_short_window_minutes",
      settings.Usage.ProviderShortWindowMinutes
    );
    Scalar(
      yaml,
      1,
      "provider_long_window_minutes",
      settings.Usage.ProviderLongWindowMinutes
    );
    Scalar(
      yaml,
      1,
      "custom_rolling_window_minutes",
      settings.Usage.CustomRollingWindowMinutes
    );
    Scalar(
      yaml,
      1,
      "comparison_provider",
      settings.Usage.ComparisonProvider
    );
    Scalar(
      yaml,
      1,
      "comparison_model",
      settings.Usage.ComparisonModel
    );
    Scalar(
      yaml,
      1,
      "ollama_plan_reference",
      settings.Usage.OllamaPlanReference
    );
    Scalar(
      yaml,
      1,
      "alert_thresholds",
      string.Join(
        ",",
        settings.Usage.AlertThresholds
      )
    );
    yaml.AppendLine(
      "web_search:"
    );
    Scalar(
      yaml,
      1,
      "ollama_enabled",
      settings.WebSearch.OllamaEnabled
    );
    Scalar(
      yaml,
      1,
      "max_results",
      settings.WebSearch.MaxResults
    );
    Scalar(
      yaml,
      1,
      "timeout_seconds",
      settings.WebSearch.TimeoutSeconds
    );

    return yaml.ToString()
      .Replace(
        "\r\n",
        "\n",
        StringComparison.Ordinal
      );
  }

  public PortableYamlImportResult Import(
    string yaml,
    ApplicationSettings current
  )
  {
    var errors = new Dictionary<string, List<string>>(
      StringComparer.Ordinal
    );

    if (yaml.Length > MaximumYamlCharacters)
    {
      AddError(
        errors,
        "yaml",
        $"YAML must contain at most {MaximumYamlCharacters} characters."
      );
      return Result(
        null,
        errors
      );
    }

    var root = Parse(
      yaml,
      errors
    );

    if (root is null)
    {
      return Result(
        null,
        errors
      );
    }

    ValidateKeys(
      root,
      TopLevelKeys,
      string.Empty,
      errors
    );
    var schemaVersion = ReadInt(
      root,
      "schema_version",
      current.SchemaVersion,
      "schema_version",
      errors
    );

    if (schemaVersion != 1)
    {
      AddError(
        errors,
        "schema_version",
        "Portable YAML schema version must be 1."
      );
    }

    var settings = current with
    {
      SchemaVersion = schemaVersion
    };
    settings = ApplyProvider(
      root,
      settings,
      errors
    );
    settings = ApplyModels(
      root,
      settings,
      errors
    );
    settings = ApplyRouting(
      root,
      settings,
      errors
    );
    settings = ApplySimpleSections(
      root,
      settings,
      errors
    );

    return Result(
      errors.Count == 0
        ? settings
        : null,
      errors
    );
  }

  private static ApplicationSettings ApplyProvider(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var provider = Map(
      root,
      "provider",
      "provider",
      errors
    );

    if (provider is null)
    {
      return settings;
    }

    ValidateKeys(
      provider,
      [
        "type",
        "url"
      ],
      "provider",
      errors
    );
    var type = ReadString(
      provider,
      "type",
      "ollama",
      "provider.type",
      errors
    );

    if (!string.Equals(
      type,
      "ollama",
      StringComparison.OrdinalIgnoreCase
    ))
    {
      AddError(
        errors,
        "provider.type",
        "Only the ollama provider is supported."
      );
    }

    return settings with
    {
      OllamaUrl = ReadString(
        provider,
        "url",
        settings.OllamaUrl,
        "provider.url",
        errors
      )
    };
  }

  private static ApplicationSettings ApplyModels(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var models = Map(
      root,
      "models",
      "models",
      errors
    );

    if (models is null)
    {
      return settings;
    }

    ValidateKeys(
      models,
      new[]
      {
        "router",
        "action",
        "coordinator",
        "default"
      }.Concat(
        SettingsDefaults.IntentionNames
      ),
      "models",
      errors
    );
    var router = ReadModelGroup(
      models,
      "router",
      settings.RouterModel,
      null,
      false,
      errors
    );
    var coordinator = ReadModelGroup(
      models,
      "coordinator",
      settings.CoordinatorModel,
      null,
      false,
      errors
    );
    var action = ReadModelGroup(
      models,
      "action",
      settings.ActionModel,
      null,
      true,
      errors
    );
    var defaultModel = ReadModelGroup(
      models,
      "default",
      settings.DefaultModel,
      null,
      false,
      errors
    );
    var intentions = settings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );

    foreach (var intentionName in SettingsDefaults.IntentionNames)
    {
      var current = intentions[intentionName];
      var imported = ReadModelGroup(
        models,
        intentionName,
        current.Model,
        current.FallbackModel,
        true,
        errors
      );
      intentions[intentionName] = current with
      {
        Model = imported.Primary,
        FallbackModel = imported.Fallback ?? current.FallbackModel
      };
    }

    return settings with
    {
      RouterModel = router.Primary,
      ActionModel = action.Primary,
      CoordinatorModel = action.Fallback ?? coordinator.Primary,
      DefaultModel = defaultModel.Primary,
      Intentions = intentions
    };
  }

  private static ApplicationSettings ApplyRouting(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var routing = Map(
      root,
      "routing",
      "routing",
      errors
    );

    if (routing is null)
    {
      return settings;
    }

    ValidateKeys(
      routing,
      [
        "default_gpu",
        "router_gpu",
        "action_gpu",
        "coordinator_gpu",
        "gpus",
        "system_prompts"
      ],
      "routing",
      errors
    );
    var intentions = settings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );
    var gpus = Map(
      routing,
      "gpus",
      "routing.gpus",
      errors
    );
    var prompts = Map(
      routing,
      "system_prompts",
      "routing.system_prompts",
      errors
    );

    if (gpus is not null)
    {
      ValidateKeys(
        gpus,
        SettingsDefaults.IntentionNames,
        "routing.gpus",
        errors
      );
    }

    if (prompts is not null)
    {
      ValidateKeys(
        prompts,
        SettingsDefaults.IntentionNames,
        "routing.system_prompts",
        errors
      );
    }

    foreach (var intentionName in SettingsDefaults.IntentionNames)
    {
      var current = intentions[intentionName];
      intentions[intentionName] = current with
      {
        Gpu = gpus is null
          ? current.Gpu
          : ReadString(
            gpus,
            intentionName,
            current.Gpu,
            $"routing.gpus.{intentionName}",
            errors
          ),
        SystemPrompt = prompts is null
          ? current.SystemPrompt
          : ReadString(
            prompts,
            intentionName,
            current.SystemPrompt,
            $"routing.system_prompts.{intentionName}",
            errors
          )
      };
    }

    return settings with
    {
      DefaultGpu = ReadString(
        routing,
        "default_gpu",
        settings.DefaultGpu,
        "routing.default_gpu",
        errors
      ),
      RouterGpu = ReadString(
        routing,
        "router_gpu",
        settings.RouterGpu,
        "routing.router_gpu",
        errors
      ),
      ActionGpu = ReadString(
        routing,
        "action_gpu",
        settings.ActionGpu,
        "routing.action_gpu",
        errors
      ),
      CoordinatorGpu = ReadString(
        routing,
        "coordinator_gpu",
        settings.CoordinatorGpu,
        "routing.coordinator_gpu",
        errors
      ),
      Intentions = intentions
    };
  }

  private static ApplicationSettings ApplySimpleSections(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    settings = ApplyContext(
      root,
      settings,
      errors
    );
    settings = ApplyRuntime(
      root,
      settings,
      errors
    );
    settings = ApplyOllamaRuntime(
      root,
      settings,
      errors
    );
    settings = ApplyExecution(
      root,
      settings,
      errors
    );
    settings = ApplyProjectAwareness(
      root,
      settings,
      errors
    );
    settings = ApplySessionHistory(
      root,
      settings,
      errors
    );
    settings = ApplyGitDelivery(
      root,
      settings,
      errors
    );
    settings = ApplyUsage(
      root,
      settings,
      errors
    );
    return ApplyWebSearch(
      root,
      settings,
      errors
    );
  }

  private static ApplicationSettings ApplyOllamaRuntime(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "ollama_runtime",
      "ollama_runtime",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    ValidateKeys(
      section,
      [
        "profile_schema_version",
        "context_escalation_ladder",
        "role_defaults",
        "memory",
        "model_overrides"
      ],
      "ollama_runtime",
      errors
    );
    var current = settings.OllamaRuntime;
    var roles = current.RoleDefaults.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );
    var roleMap = Map(
      section,
      "role_defaults",
      "ollama_runtime.role_defaults",
      errors
    );

    if (roleMap is not null)
    {
      ValidateKeys(
        roleMap,
        OllamaRuntimeRoleIds.All,
        "ollama_runtime.role_defaults",
        errors
      );

      foreach (var role in OllamaRuntimeRoleIds.All)
      {
        var node = Map(
          roleMap,
          role,
          $"ollama_runtime.role_defaults.{role}",
          errors
        );

        if (node is not null)
        {
          roles[role] = ReadRoleProfile(
            node,
            roles[role],
            $"ollama_runtime.role_defaults.{role}",
            errors
          );
        }
      }
    }

    var memory = current.Memory;
    var memoryMap = Map(
      section,
      "memory",
      "ollama_runtime.memory",
      errors
    );

    if (memoryMap is not null)
    {
      ValidateKeys(
        memoryMap,
        [
          "target_maximum_gpu_usage_percent",
          "minimum_free_vram_bytes",
          "minimum_free_system_ram_bytes",
          "allow_cpu_offload",
          "prefer_full_gpu_for_active_primary",
          "devices"
        ],
        "ollama_runtime.memory",
        errors
      );
      memory = memory with
      {
        TargetMaximumGpuUsagePercent = ReadInt(
          memoryMap,
          "target_maximum_gpu_usage_percent",
          memory.TargetMaximumGpuUsagePercent,
          "ollama_runtime.memory.target_maximum_gpu_usage_percent",
          errors
        ),
        MinimumFreeVramBytes = ReadLong(
          memoryMap,
          "minimum_free_vram_bytes",
          memory.MinimumFreeVramBytes,
          "ollama_runtime.memory.minimum_free_vram_bytes",
          errors
        ),
        MinimumFreeSystemRamBytes = ReadLong(
          memoryMap,
          "minimum_free_system_ram_bytes",
          memory.MinimumFreeSystemRamBytes,
          "ollama_runtime.memory.minimum_free_system_ram_bytes",
          errors
        ),
        AllowCpuOffload = ReadBoolean(
          memoryMap,
          "allow_cpu_offload",
          memory.AllowCpuOffload,
          "ollama_runtime.memory.allow_cpu_offload",
          errors
        ),
        PreferFullGpuForActivePrimary = ReadBoolean(
          memoryMap,
          "prefer_full_gpu_for_active_primary",
          memory.PreferFullGpuForActivePrimary,
          "ollama_runtime.memory.prefer_full_gpu_for_active_primary",
          errors
        )
      };
      var memoryDevices = Map(
        memoryMap,
        "devices",
        "ollama_runtime.memory.devices",
        errors
      );

      if (memoryDevices?.Children is not null)
      {
        var importedDevices = new Dictionary<string, OllamaGpuMemoryPolicy>(
          StringComparer.Ordinal
        );

        foreach (var entry in memoryDevices.Children.OrderBy(
          pair => pair.Key,
          StringComparer.Ordinal
        ))
        {
          var path = $"ollama_runtime.memory.devices.{entry.Key}";

          if (entry.Value.Children is null)
          {
            AddError(
              errors,
              path,
              $"Line {entry.Value.Line}: expected a mapping."
            );
            continue;
          }

          ValidateKeys(
            entry.Value,
            [
              "id",
              "target_maximum_usage_percent",
              "minimum_free_vram_bytes"
            ],
            path,
            errors
          );
          var id = ReadString(
            entry.Value,
            "id",
            string.Empty,
            $"{path}.id",
            errors
          );

          if (!string.IsNullOrWhiteSpace(
            id
          ))
          {
            importedDevices[id] = new OllamaGpuMemoryPolicy
            {
              TargetMaximumUsagePercent = ReadInt(
                entry.Value,
                "target_maximum_usage_percent",
                memory.TargetMaximumGpuUsagePercent,
                $"{path}.target_maximum_usage_percent",
                errors
              ),
              MinimumFreeVramBytes = ReadLong(
                entry.Value,
                "minimum_free_vram_bytes",
                memory.MinimumFreeVramBytes,
                $"{path}.minimum_free_vram_bytes",
                errors
              )
            };
          }
        }

        memory = memory with
        {
          Devices = importedDevices
        };
      }
    }

    var overrides = new List<OllamaModelRuntimeOverride>();
    var overrideMap = Map(
      section,
      "model_overrides",
      "ollama_runtime.model_overrides",
      errors
    );

    if (overrideMap?.Children is not null)
    {
      foreach (var entry in overrideMap.Children.OrderBy(
        pair => pair.Key,
        StringComparer.Ordinal
      ))
      {
        var path = $"ollama_runtime.model_overrides.{entry.Key}";

        if (entry.Value.Children is null)
        {
          AddError(
            errors,
            path,
            $"Line {entry.Value.Line}: expected a mapping."
          );
          continue;
        }

        ValidateKeys(
          entry.Value,
          [
            "provider",
            "model",
            "digest",
            "roles"
          ],
          path,
          errors
        );
        var overrideRoles = new Dictionary<string, OllamaRoleRuntimeSettings>(
          StringComparer.Ordinal
        );
        var overrideRoleMap = Map(
          entry.Value,
          "roles",
          $"{path}.roles",
          errors
        );

        if (overrideRoleMap is not null)
        {
          ValidateKeys(
            overrideRoleMap,
            OllamaRuntimeRoleIds.All,
            $"{path}.roles",
            errors
          );

          foreach (var role in OllamaRuntimeRoleIds.All)
          {
            var roleNode = Map(
              overrideRoleMap,
              role,
              $"{path}.roles.{role}",
              errors
            );

            if (roleNode is not null)
            {
              overrideRoles[role] = ReadRoleProfile(
                roleNode,
                roles[role],
                $"{path}.roles.{role}",
                errors
              );
            }
          }
        }

        overrides.Add(
          new OllamaModelRuntimeOverride
          {
            Provider = ReadString(
              entry.Value,
              "provider",
              "ollama-local",
              $"{path}.provider",
              errors
            ),
            Model = ReadString(
              entry.Value,
              "model",
              string.Empty,
              $"{path}.model",
              errors
            ),
            Digest = ReadString(
              entry.Value,
              "digest",
              string.Empty,
              $"{path}.digest",
              errors
            ),
            Overrides = overrideRoles
          }
        );
      }
    }

    var ladderValue = ReadString(
      section,
      "context_escalation_ladder",
      string.Join(
        ",",
        current.ContextEscalationLadder
      ),
      "ollama_runtime.context_escalation_ladder",
      errors
    );
    var ladder = ladderValue.Split(
      ',',
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    ).Select(
      value => int.TryParse(
        value,
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var parsed
      )
        ? parsed
        : 0
    ).ToArray();

    if (ladder.Any(
      value => value == 0
    ))
    {
      AddError(
        errors,
        "ollama_runtime.context_escalation_ladder",
        "Every context escalation ladder entry must be an integer."
      );
      ladder = current.ContextEscalationLadder.ToArray();
    }

    var importedRuntime = current with
    {
      ProfileSchemaVersion = ReadInt(
        section,
        "profile_schema_version",
        current.ProfileSchemaVersion,
        "ollama_runtime.profile_schema_version",
        errors
      ),
      RoleDefaults = roles,
      ContextEscalationLadder = ladder,
      Memory = memory,
      ModelOverrides = overrideMap is null
        ? current.ModelOverrides
        : overrides
    };
    if (importedRuntime.ProfileSchemaVersion == 1)
    {
      importedRuntime = OllamaRuntimeDefaults.Upgrade(importedRuntime);
    }

    return settings with
    {
      OllamaRuntime = importedRuntime
    };
  }

  private static OllamaRoleRuntimeSettings ReadRoleProfile(
    YamlNode node,
    OllamaRoleRuntimeSettings fallback,
    string path,
    IDictionary<string, List<string>> errors
  )
  {
    ValidateKeys(
      node,
      [
        "minimum_context_tokens",
        "target_context_tokens",
        "maximum_context_tokens",
        "output_token_limit",
        "keep_alive"
      ],
      path,
      errors
    );

    return fallback with
    {
      MinimumContextTokens = ReadInt(
        node,
        "minimum_context_tokens",
        fallback.MinimumContextTokens,
        $"{path}.minimum_context_tokens",
        errors
      ),
      TargetContextTokens = ReadInt(
        node,
        "target_context_tokens",
        fallback.TargetContextTokens,
        $"{path}.target_context_tokens",
        errors
      ),
      MaximumContextTokens = ReadInt(
        node,
        "maximum_context_tokens",
        fallback.MaximumContextTokens,
        $"{path}.maximum_context_tokens",
        errors
      ),
      OutputTokenLimit = ReadInt(
        node,
        "output_token_limit",
        fallback.OutputTokenLimit,
        $"{path}.output_token_limit",
        errors
      ),
      KeepAlive = ReadInt(
        node,
        "keep_alive",
        fallback.KeepAlive,
        $"{path}.keep_alive",
        errors
      )
    };
  }

  private static ApplicationSettings ApplyWebSearch(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "web_search",
      "web_search",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var keys = new[]
    {
      "ollama_enabled",
      "max_results",
      "timeout_seconds"
    };
    ValidateKeys(
      section,
      keys,
      "web_search",
      errors
    );
    var current = settings.WebSearch;
    var requestedEnabled = ReadBoolean(
      section,
      keys[0],
      current.OllamaEnabled,
      $"web_search.{keys[0]}",
      errors
    );
    return settings with
    {
      WebSearch = current with
      {
        OllamaEnabled = !string.IsNullOrWhiteSpace(
          current.OllamaSecretReference
        ) && requestedEnabled,
        MaxResults = ReadInt(
          section,
          keys[1],
          current.MaxResults,
          $"web_search.{keys[1]}",
          errors
        ),
        TimeoutSeconds = ReadInt(
          section,
          keys[2],
          current.TimeoutSeconds,
          $"web_search.{keys[2]}",
          errors
        )
      }
    };
  }

  private static ApplicationSettings ApplyContext(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "context",
      "context",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var keys = new[]
    {
      "default_context_tokens",
      "provider_context_tokens",
      "reserved_response_tokens",
      "max_conversation_messages"
    };
    ValidateKeys(
      section,
      keys,
      "context",
      errors
    );
    var current = settings.Context;
    return settings with
    {
      Context = current with
      {
        DefaultContextTokens = ReadInt(
          section,
          keys[0],
          current.DefaultContextTokens,
          $"context.{keys[0]}",
          errors
        ),
        ProviderContextTokens = ReadInt(
          section,
          keys[1],
          current.ProviderContextTokens,
          $"context.{keys[1]}",
          errors
        ),
        ReservedResponseTokens = ReadInt(
          section,
          keys[2],
          current.ReservedResponseTokens,
          $"context.{keys[2]}",
          errors
        ),
        MaxConversationMessages = ReadInt(
          section,
          keys[3],
          current.MaxConversationMessages,
          $"context.{keys[3]}",
          errors
        )
      }
    };
  }

  private static ApplicationSettings ApplyRuntime(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "runtime",
      "runtime",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var keys = new[]
    {
      "resident_model_policy",
      "resident_model_verification_interval_seconds",
      "status_idle_refresh_seconds",
      "status_active_refresh_seconds",
      "generation_timeout_seconds"
    };
    ValidateKeys(
      section,
      keys,
      "runtime",
      errors
    );
    var current = settings.Runtime;
    return settings with
    {
      Runtime = current with
      {
        ResidentModelPolicy = ReadString(
          section,
          keys[0],
          current.ResidentModelPolicy,
          $"runtime.{keys[0]}",
          errors
        ),
        ResidentModelVerificationIntervalSeconds = ReadInt(
          section,
          keys[1],
          current.ResidentModelVerificationIntervalSeconds,
          $"runtime.{keys[1]}",
          errors
        ),
        RuntimeStatusIdleRefreshSeconds = ReadInt(
          section,
          keys[2],
          current.RuntimeStatusIdleRefreshSeconds,
          $"runtime.{keys[2]}",
          errors
        ),
        RuntimeStatusActiveRefreshSeconds = ReadInt(
          section,
          keys[3],
          current.RuntimeStatusActiveRefreshSeconds,
          $"runtime.{keys[3]}",
          errors
        ),
        GenerationTimeoutSeconds = ReadInt(
          section,
          keys[4],
          current.GenerationTimeoutSeconds,
          $"runtime.{keys[4]}",
          errors
        )
      }
    };
  }

  private static ApplicationSettings ApplyExecution(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "execution",
      "execution",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var numericKeys = new[]
    {
      "direct_planning_failures_before_handoff",
      "resident_planning_failures_before_failure",
      "max_coordinator_handoffs_per_turn",
      "max_tool_calls_per_turn",
      "max_consecutive_tool_failures",
      "max_recovery_attempts_per_turn",
      "max_tracked_files_per_session",
      "max_rollback_bytes_per_file",
      "max_rollback_bytes_per_session",
      "max_search_files",
      "max_search_matches",
      "max_tool_output_tokens",
      "max_direct_plan_steps"
    };
    var effortKeys = new[]
    {
      "plan_effort",
      "work_effort",
      "verify_effort",
      "complete_effort",
      "recovery_effort"
    };
    ValidateKeys(
      section,
      numericKeys.Concat(effortKeys).ToArray(),
      "execution",
      errors
    );
    var current = settings.Execution;
    var values = numericKeys.Select(
      (
        key,
        index
      ) => ReadInt(
        section,
        key,
        GetExecutionValue(
          current,
          index
        ),
        $"execution.{key}",
        errors
      )
    ).ToArray();
    return settings with
    {
      Execution = current with
      {
        DirectCoordinatorPlanningFailuresBeforeHandoff = values[0],
        ResidentCoordinatorPlanningFailuresBeforeFailure = values[1],
        MaxCoordinatorHandoffsPerTurn = values[2],
        MaxToolCallsPerTurn = values[3],
        MaxConsecutiveToolFailures = values[4],
        MaxRecoveryAttemptsPerTurn = values[5],
        MaxTrackedFilesPerSession = values[6],
        MaxRollbackBytesPerFile = values[7],
        MaxRollbackBytesPerSession = values[8],
        MaxSearchFiles = values[9],
        MaxSearchMatches = values[10],
        MaxToolOutputTokens = values[11],
        MaxDirectPlanSteps = values[12],
        PhaseEffort = new PhaseEffortSettings
        {
          Plan = ReadString(
            section,
            effortKeys[0],
            current.PhaseEffort.Plan,
            "execution.plan_effort",
            errors
          ),
          Work = ReadString(
            section,
            effortKeys[1],
            current.PhaseEffort.Work,
            "execution.work_effort",
            errors
          ),
          Verify = ReadString(
            section,
            effortKeys[2],
            current.PhaseEffort.Verify,
            "execution.verify_effort",
            errors
          ),
          Complete = ReadString(
            section,
            effortKeys[3],
            current.PhaseEffort.Complete,
            "execution.complete_effort",
            errors
          ),
          Recovery = ReadString(
            section,
            effortKeys[4],
            current.PhaseEffort.Recovery,
            "execution.recovery_effort",
            errors
          )
        }
      }
    };
  }

  private static int GetExecutionValue(
    ExecutionSettings settings,
    int index
  )
  {
    return index switch
    {
      0 => settings.DirectCoordinatorPlanningFailuresBeforeHandoff,
      1 => settings.ResidentCoordinatorPlanningFailuresBeforeFailure,
      2 => settings.MaxCoordinatorHandoffsPerTurn,
      3 => settings.MaxToolCallsPerTurn,
      4 => settings.MaxConsecutiveToolFailures,
      5 => settings.MaxRecoveryAttemptsPerTurn,
      6 => settings.MaxTrackedFilesPerSession,
      7 => settings.MaxRollbackBytesPerFile,
      8 => settings.MaxRollbackBytesPerSession,
      9 => settings.MaxSearchFiles,
      10 => settings.MaxSearchMatches,
      11 => settings.MaxToolOutputTokens,
      _ => settings.MaxDirectPlanSteps
    };
  }

  private static ApplicationSettings ApplyProjectAwareness(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "project_awareness",
      "project_awareness",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var keys = new[]
    {
      "max_project_markers",
      "max_instruction_bytes",
      "max_plan_steps",
      "max_plan_revisions"
    };
    ValidateKeys(
      section,
      keys,
      "project_awareness",
      errors
    );
    var current = settings.ProjectAwareness;
    return settings with
    {
      ProjectAwareness = current with
      {
        MaxProjectMarkers = ReadInt(
          section,
          keys[0],
          current.MaxProjectMarkers,
          $"project_awareness.{keys[0]}",
          errors
        ),
        MaxInstructionBytes = ReadInt(
          section,
          keys[1],
          current.MaxInstructionBytes,
          $"project_awareness.{keys[1]}",
          errors
        ),
        MaxPlanSteps = ReadInt(
          section,
          keys[2],
          current.MaxPlanSteps,
          $"project_awareness.{keys[2]}",
          errors
        ),
        MaxPlanRevisions = ReadInt(
          section,
          keys[3],
          current.MaxPlanRevisions,
          $"project_awareness.{keys[3]}",
          errors
        )
      }
    };
  }

  private static ApplicationSettings ApplySessionHistory(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "session_history",
      "session_history",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var keys = new[]
    {
      "max_sessions_per_workspace",
      "max_session_bytes",
      "max_process_output_bytes_per_turn",
      "max_diff_bytes_per_turn"
    };
    ValidateKeys(
      section,
      keys,
      "session_history",
      errors
    );
    var current = settings.SessionHistory;
    return settings with
    {
      SessionHistory = current with
      {
        MaxSessionsPerWorkspace = ReadInt(
          section,
          keys[0],
          current.MaxSessionsPerWorkspace,
          $"session_history.{keys[0]}",
          errors
        ),
        MaxSessionBytes = ReadInt(
          section,
          keys[1],
          current.MaxSessionBytes,
          $"session_history.{keys[1]}",
          errors
        ),
        MaxStoredProcessOutputBytesPerTurn = ReadInt(
          section,
          keys[2],
          current.MaxStoredProcessOutputBytesPerTurn,
          $"session_history.{keys[2]}",
          errors
        ),
        MaxStoredDiffBytesPerTurn = ReadInt(
          section,
          keys[3],
          current.MaxStoredDiffBytesPerTurn,
          $"session_history.{keys[3]}",
          errors
        )
      }
    };
  }

  private static ApplicationSettings ApplyGitDelivery(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "git_delivery",
      "git_delivery",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var keys = new[]
    {
      "enabled",
      "require_validation_before_commit",
      "allow_explicit_commit_without_validation",
      "max_diff_bytes_per_file",
      "max_log_entries"
    };
    ValidateKeys(
      section,
      keys,
      "git_delivery",
      errors
    );
    var current = settings.GitDelivery;
    return settings with
    {
      GitDelivery = current with
      {
        Enabled = ReadBoolean(
          section,
          keys[0],
          current.Enabled,
          $"git_delivery.{keys[0]}",
          errors
        ),
        RequireValidationBeforeCommit = ReadBoolean(
          section,
          keys[1],
          current.RequireValidationBeforeCommit,
          $"git_delivery.{keys[1]}",
          errors
        ),
        AllowExplicitCommitWithoutValidation = ReadBoolean(
          section,
          keys[2],
          current.AllowExplicitCommitWithoutValidation,
          $"git_delivery.{keys[2]}",
          errors
        ),
        MaxDiffBytesPerFile = ReadInt(
          section,
          keys[3],
          current.MaxDiffBytesPerFile,
          $"git_delivery.{keys[3]}",
          errors
        ),
        MaxLogEntries = ReadInt(
          section,
          keys[4],
          current.MaxLogEntries,
          $"git_delivery.{keys[4]}",
          errors
        )
      }
    };
  }

  private static ApplicationSettings ApplyUsage(
    YamlNode root,
    ApplicationSettings settings,
    IDictionary<string, List<string>> errors
  )
  {
    var section = Map(
      root,
      "usage",
      "usage",
      errors
    );

    if (section is null)
    {
      return settings;
    }

    var keys = new[]
    {
      "retention_days",
      "max_event_bytes",
      "selected_window",
      "pinned_windows",
      "provider_short_window_minutes",
      "provider_long_window_minutes",
      "custom_rolling_window_minutes",
      "comparison_provider",
      "comparison_model",
      "ollama_plan_reference",
      "alert_thresholds"
    };
    ValidateKeys(
      section,
      keys,
      "usage",
      errors
    );
    var current = settings.Usage;
    var pinnedValue = ReadString(
      section,
      keys[3],
      string.Join(
        ",",
        current.PinnedWindows
      ),
      $"usage.{keys[3]}",
      errors
    );
    var pinned = pinnedValue.Split(
      ',',
      StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
    );
    var alertValue = ReadString(
      section,
      keys[10],
      string.Join(
        ",",
        current.AlertThresholds
      ),
      $"usage.{keys[10]}",
      errors
    );
    var alerts = alertValue.Split(
      ',',
      StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
    ).Select(
      value => int.TryParse(
        value,
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var threshold
      )
        ? threshold
        : -1
    ).ToArray();

    return settings with
    {
      Usage = current with
      {
        RetentionDays = ReadInt(
          section,
          keys[0],
          current.RetentionDays,
          $"usage.{keys[0]}",
          errors
        ),
        MaxEventBytes = ReadInt(
          section,
          keys[1],
          current.MaxEventBytes,
          $"usage.{keys[1]}",
          errors
        ),
        SelectedWindow = ReadString(
          section,
          keys[2],
          current.SelectedWindow,
          $"usage.{keys[2]}",
          errors
        ),
        PinnedWindows = pinned,
        ProviderShortWindowMinutes = ReadInt(
          section,
          keys[4],
          current.ProviderShortWindowMinutes,
          $"usage.{keys[4]}",
          errors
        ),
        ProviderLongWindowMinutes = ReadInt(
          section,
          keys[5],
          current.ProviderLongWindowMinutes,
          $"usage.{keys[5]}",
          errors
        ),
        CustomRollingWindowMinutes = ReadInt(
          section,
          keys[6],
          current.CustomRollingWindowMinutes,
          $"usage.{keys[6]}",
          errors
        ),
        ComparisonProvider = ReadString(
          section,
          keys[7],
          current.ComparisonProvider,
          $"usage.{keys[7]}",
          errors
        ),
        ComparisonModel = ReadString(
          section,
          keys[8],
          current.ComparisonModel,
          $"usage.{keys[8]}",
          errors
        ),
        OllamaPlanReference = ReadString(
          section,
          keys[9],
          current.OllamaPlanReference,
          $"usage.{keys[9]}",
          errors
        ),
        AlertThresholds = alerts
      }
    };
  }

  private static ModelGroupValue ReadModelGroup(
    YamlNode models,
    string name,
    string currentPrimary,
    string? currentFallback,
    bool allowFallback,
    IDictionary<string, List<string>> errors
  )
  {
    var group = Map(
      models,
      name,
      $"models.{name}",
      errors
    );

    if (group is null)
    {
      return new ModelGroupValue(
        currentPrimary,
        currentFallback
      );
    }

    ValidateKeys(
      group,
      allowFallback
        ? [
          "primary",
          "fallback"
        ]
        : [
          "primary"
        ],
      $"models.{name}",
      errors
    );
    return new ModelGroupValue(
      ReadString(
        group,
        "primary",
        currentPrimary,
        $"models.{name}.primary",
        errors
      ),
      allowFallback
        ? ReadString(
          group,
          "fallback",
          currentFallback ?? "none",
          $"models.{name}.fallback",
          errors
        )
        : null
    );
  }

  private static YamlNode? Parse(
    string yaml,
    IDictionary<string, List<string>> errors
  )
  {
    var root = YamlNode.Map(
      0
    );
    var stack = new Stack<(
      int Indent,
      YamlNode Node,
      string Path
    )>();
    stack.Push(
      (
        -2,
        root,
        string.Empty
      )
    );
    var lines = yaml.Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ).Split(
      '\n'
    );

    if (lines.Length > 5_000)
    {
      AddError(
        errors,
        "yaml",
        "YAML must contain at most 5000 lines."
      );
      return null;
    }

    for (var index = 0; index < lines.Length; index++)
    {
      var lineNumber = index + 1;
      var raw = lines[index];

      if (
        string.IsNullOrWhiteSpace(
          raw
        )
        || raw.TrimStart().StartsWith(
          '#'
        )
      )
      {
        continue;
      }

      if (raw.Contains(
        '\t',
        StringComparison.Ordinal
      ))
      {
        AddError(
          errors,
          "yaml",
          $"Line {lineNumber}: tabs are not supported; use two spaces per level."
        );
        continue;
      }

      var indent = raw.TakeWhile(
        character => character == ' '
      ).Count();

      if (indent % 2 != 0 || indent > 10)
      {
        AddError(
          errors,
          "yaml",
          $"Line {lineNumber}: indentation must use two spaces per level, up to five levels."
        );
        continue;
      }

      var content = StripComment(
        raw[indent..]
      );
      var separator = FindSeparator(
        content
      );

      if (separator <= 0)
      {
        AddError(
          errors,
          "yaml",
          $"Line {lineNumber}: expected a key followed by ':'."
        );
        continue;
      }

      var key = content[..separator].Trim();

      if (!IsValidKey(
        key
      ))
      {
        AddError(
          errors,
          "yaml",
          $"Line {lineNumber}: key '{key}' is invalid."
        );
        continue;
      }

      while (stack.Peek().Indent >= indent)
      {
        stack.Pop();
      }

      var parent = stack.Peek();

      if (parent.Indent != indent - 2)
      {
        AddError(
          errors,
          "yaml",
          $"Line {lineNumber}: indentation skipped a level."
        );
        continue;
      }

      if (parent.Node.Children is null)
      {
        AddError(
          errors,
          "yaml",
          $"Line {lineNumber}: scalar values cannot contain nested keys."
        );
        continue;
      }

      if (parent.Node.Children.ContainsKey(
        key
      ))
      {
        AddError(
          errors,
          string.IsNullOrEmpty(
            parent.Path
          )
            ? key
            : $"{parent.Path}.{key}",
          $"Line {lineNumber}: duplicate key."
        );
        continue;
      }

      var rawValue = content[(separator + 1)..].Trim();
      var path = string.IsNullOrEmpty(
        parent.Path
      )
        ? key
        : $"{parent.Path}.{key}";
      YamlNode node;

      if (rawValue.Length == 0)
      {
        node = YamlNode.Map(
          lineNumber
        );
      }
      else
      {
        var value = ParseScalar(
          rawValue,
          path,
          lineNumber,
          errors
        );
        node = YamlNode.Value(
          lineNumber,
          value
        );
      }

      parent.Node.Children[key] = node;

      if (node.Children is not null)
      {
        stack.Push(
          (
            indent,
            node,
            path
          )
        );
      }
    }

    return errors.Count == 0
      ? root
      : null;
  }

  private static string ParseScalar(
    string value,
    string path,
    int line,
    IDictionary<string, List<string>> errors
  )
  {
    if (value.StartsWith(
      '"'
    ))
    {
      try
      {
        return JsonSerializer.Deserialize<string>(
          value
        ) ?? string.Empty;
      }
      catch (JsonException)
      {
        AddError(
          errors,
          path,
          $"Line {line}: invalid double-quoted scalar."
        );
        return string.Empty;
      }
    }

    if (
      value.StartsWith(
        [
          '{',
          '[',
          '&',
          '*',
          '!',
          '|',
          '>'
        ]
      )
      || value.StartsWith(
        '-'
      )
    )
    {
      AddError(
        errors,
        path,
        $"Line {line}: only scalar mapping values are supported."
      );
      return string.Empty;
    }

    return value;
  }

  private static string StripComment(
    string content
  )
  {
    var quoted = false;
    var escaped = false;

    for (var index = 0; index < content.Length; index++)
    {
      var character = content[index];

      if (escaped)
      {
        escaped = false;
        continue;
      }

      if (
        character == '\\'
        && quoted
      )
      {
        escaped = true;
        continue;
      }

      if (character == '"')
      {
        quoted = !quoted;
        continue;
      }

      if (
        character == '#'
        && !quoted
        && (
          index == 0
          || char.IsWhiteSpace(
            content[index - 1]
          )
        )
      )
      {
        return content[..index].TrimEnd();
      }
    }

    return content.TrimEnd();
  }

  private static int FindSeparator(
    string content
  )
  {
    var quoted = false;
    var escaped = false;

    for (var index = 0; index < content.Length; index++)
    {
      var character = content[index];

      if (escaped)
      {
        escaped = false;
        continue;
      }

      if (
        character == '\\'
        && quoted
      )
      {
        escaped = true;
      }
      else if (character == '"')
      {
        quoted = !quoted;
      }
      else if (
        character == ':'
        && !quoted
      )
      {
        return index;
      }
    }

    return -1;
  }

  private static bool IsValidKey(
    string key
  )
  {
    return key.Length is > 0 and <= 80
      && (
        char.IsAsciiLetter(
          key[0]
        )
        || key[0] == '_'
      )
      && key.All(
        character => char.IsAsciiLetterOrDigit(
          character
        )
          || character is '_' or '-'
      );
  }

  private static YamlNode? Map(
    YamlNode parent,
    string key,
    string path,
    IDictionary<string, List<string>> errors
  )
  {
    if (
      parent.Children is null
      || !parent.Children.TryGetValue(
        key,
        out var node
      )
    )
    {
      return null;
    }

    if (node.Children is not null)
    {
      return node;
    }

    AddError(
      errors,
      path,
      $"Line {node.Line}: expected a mapping."
    );
    return null;
  }

  private static string ReadString(
    YamlNode parent,
    string key,
    string fallback,
    string path,
    IDictionary<string, List<string>> errors
  )
  {
    if (
      parent.Children is null
      || !parent.Children.TryGetValue(
        key,
        out var node
      )
    )
    {
      return fallback;
    }

    if (node.Scalar is not null)
    {
      return node.Scalar;
    }

    AddError(
      errors,
      path,
      $"Line {node.Line}: expected a scalar value."
    );
    return fallback;
  }

  private static int ReadInt(
    YamlNode parent,
    string key,
    int fallback,
    string path,
    IDictionary<string, List<string>> errors
  )
  {
    var value = ReadString(
      parent,
      key,
      fallback.ToString(
        CultureInfo.InvariantCulture
      ),
      path,
      errors
    );

    if (int.TryParse(
      value,
      NumberStyles.Integer,
      CultureInfo.InvariantCulture,
      out var parsed
    ))
    {
      return parsed;
    }

    AddError(
      errors,
      path,
      "Value must be an integer."
    );
    return fallback;
  }

  private static long ReadLong(
    YamlNode parent,
    string key,
    long fallback,
    string path,
    IDictionary<string, List<string>> errors
  )
  {
    var value = ReadString(
      parent,
      key,
      fallback.ToString(
        CultureInfo.InvariantCulture
      ),
      path,
      errors
    );

    if (long.TryParse(
      value,
      NumberStyles.Integer,
      CultureInfo.InvariantCulture,
      out var parsed
    ))
    {
      return parsed;
    }

    AddError(
      errors,
      path,
      "Value must be an integer."
    );
    return fallback;
  }

  private static bool ReadBoolean(
    YamlNode parent,
    string key,
    bool fallback,
    string path,
    IDictionary<string, List<string>> errors
  )
  {
    var value = ReadString(
      parent,
      key,
      fallback
        ? "true"
        : "false",
      path,
      errors
    );

    if (bool.TryParse(
      value,
      out var parsed
    ))
    {
      return parsed;
    }

    AddError(
      errors,
      path,
      "Value must be true or false."
    );
    return fallback;
  }

  private static void ValidateKeys(
    YamlNode node,
    IEnumerable<string> allowed,
    string path,
    IDictionary<string, List<string>> errors
  )
  {
    if (node.Children is null)
    {
      return;
    }

    var allowedSet = allowed.ToHashSet(
      StringComparer.Ordinal
    );

    foreach (var pair in node.Children)
    {
      if (!allowedSet.Contains(
        pair.Key
      ))
      {
        AddError(
          errors,
          string.IsNullOrEmpty(
            path
          )
            ? pair.Key
            : $"{path}.{pair.Key}",
          $"Line {pair.Value.Line}: unsupported key."
        );
      }
    }
  }

  private static PortableYamlImportResult Result(
    ApplicationSettings? settings,
    IDictionary<string, List<string>> errors
  )
  {
    return new PortableYamlImportResult(
      settings,
      errors.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.ToArray(),
        StringComparer.Ordinal
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
      out var messages
    ))
    {
      messages = [];
      errors[field] = messages;
    }

    messages.Add(
      message
    );
  }

  private static void ExportOllamaRuntime(
    StringBuilder yaml,
    OllamaRuntimeSettings runtime
  )
  {
    yaml.AppendLine(
      "ollama_runtime:"
    );
    Scalar(
      yaml,
      1,
      "profile_schema_version",
      runtime.ProfileSchemaVersion
    );
    Scalar(
      yaml,
      1,
      "context_escalation_ladder",
      string.Join(
        ",",
        runtime.ContextEscalationLadder
      )
    );
    yaml.AppendLine(
      "  role_defaults:"
    );

    foreach (var role in OllamaRuntimeRoleIds.All)
    {
      yaml.Append(
        "    "
      ).Append(
        role
      ).AppendLine(
        ":"
      );
      ExportRoleProfile(
        yaml,
        3,
        runtime.RoleDefaults[role]
      );
    }

    yaml.AppendLine(
      "  memory:"
    );
    Scalar(
      yaml,
      2,
      "target_maximum_gpu_usage_percent",
      runtime.Memory.TargetMaximumGpuUsagePercent
    );
    Scalar(
      yaml,
      2,
      "minimum_free_vram_bytes",
      runtime.Memory.MinimumFreeVramBytes
    );
    Scalar(
      yaml,
      2,
      "minimum_free_system_ram_bytes",
      runtime.Memory.MinimumFreeSystemRamBytes
    );
    Scalar(
      yaml,
      2,
      "allow_cpu_offload",
      runtime.Memory.AllowCpuOffload
    );
    Scalar(
      yaml,
      2,
      "prefer_full_gpu_for_active_primary",
      runtime.Memory.PreferFullGpuForActivePrimary
    );
    yaml.AppendLine(
      "    devices:"
    );
    var deviceIndex = 0;

    foreach (var device in runtime.Memory.Devices.OrderBy(
      pair => pair.Key,
      StringComparer.Ordinal
    ))
    {
      deviceIndex++;
      yaml.Append(
        "      device_"
      ).Append(
        deviceIndex
      ).AppendLine(
        ":"
      );
      Scalar(
        yaml,
        4,
        "id",
        device.Key
      );
      Scalar(
        yaml,
        4,
        "target_maximum_usage_percent",
        device.Value.TargetMaximumUsagePercent
      );
      Scalar(
        yaml,
        4,
        "minimum_free_vram_bytes",
        device.Value.MinimumFreeVramBytes
      );
    }
    yaml.AppendLine(
      "  model_overrides:"
    );

    for (
      var index = 0;
      index < runtime.ModelOverrides.Count;
      index++
    )
    {
      var modelOverride = runtime.ModelOverrides[index];
      yaml.Append(
        "    override_"
      ).Append(
        index + 1
      ).AppendLine(
        ":"
      );
      Scalar(
        yaml,
        3,
        "provider",
        modelOverride.Provider
      );
      Scalar(
        yaml,
        3,
        "model",
        modelOverride.Model
      );
      Scalar(
        yaml,
        3,
        "digest",
        modelOverride.Digest
      );
      yaml.AppendLine(
        "      roles:"
      );

      foreach (var role in modelOverride.Overrides.OrderBy(
        pair => pair.Key,
        StringComparer.Ordinal
      ))
      {
        yaml.Append(
          "        "
        ).Append(
          role.Key
        ).AppendLine(
          ":"
        );
        ExportRoleProfile(
          yaml,
          5,
          role.Value
        );
      }
    }
  }

  private static void ExportRoleProfile(
    StringBuilder yaml,
    int level,
    OllamaRoleRuntimeSettings profile
  )
  {
    Scalar(
      yaml,
      level,
      "minimum_context_tokens",
      profile.MinimumContextTokens
    );
    Scalar(
      yaml,
      level,
      "target_context_tokens",
      profile.TargetContextTokens
    );
    Scalar(
      yaml,
      level,
      "maximum_context_tokens",
      profile.MaximumContextTokens
    );
    Scalar(
      yaml,
      level,
      "output_token_limit",
      profile.OutputTokenLimit
    );
    Scalar(
      yaml,
      level,
      "keep_alive",
      profile.KeepAlive
    );
  }

  private static void ModelGroup(
    StringBuilder yaml,
    string name,
    string primary,
    string? fallback
  )
  {
    yaml.Append(
      "  "
    ).Append(
      name
    ).AppendLine(
      ":"
    );
    Scalar(
      yaml,
      2,
      "primary",
      primary
    );

    if (fallback is not null)
    {
      Scalar(
        yaml,
        2,
        "fallback",
        fallback
      );
    }
  }

  private static void Scalar(
    StringBuilder yaml,
    int level,
    string key,
    string value
  )
  {
    yaml.Append(
      ' ',
      level * 2
    ).Append(
      key
    ).Append(
      ": "
    ).AppendLine(
      JsonSerializer.Serialize(
        value
      )
    );
  }

  private static void Scalar(
    StringBuilder yaml,
    int level,
    string key,
    long value
  )
  {
    yaml.Append(
      ' ',
      level * 2
    ).Append(
      key
    ).Append(
      ": "
    ).AppendLine(
      value.ToString(
        CultureInfo.InvariantCulture
      )
    );
  }

  private static void Scalar(
    StringBuilder yaml,
    int level,
    string key,
    int value
  )
  {
    yaml.Append(
      ' ',
      level * 2
    ).Append(
      key
    ).Append(
      ": "
    ).AppendLine(
      value < 0
        ? JsonSerializer.Serialize(
          value.ToString(
            CultureInfo.InvariantCulture
          )
        )
        : value.ToString(
          CultureInfo.InvariantCulture
        )
    );
  }

  private static void Scalar(
    StringBuilder yaml,
    int level,
    string key,
    bool value
  )
  {
    yaml.Append(
      ' ',
      level * 2
    ).Append(
      key
    ).Append(
      ": "
    ).AppendLine(
      value
        ? "true"
        : "false"
    );
  }

  private sealed record ModelGroupValue(
    string Primary,
    string? Fallback
  );

  private sealed class YamlNode
  {
    private YamlNode(
      int line,
      Dictionary<string, YamlNode>? children,
      string? scalar
    )
    {
      Line = line;
      Children = children;
      Scalar = scalar;
    }

    public int Line { get; }

    public Dictionary<string, YamlNode>? Children { get; }

    public string? Scalar { get; }

    public static YamlNode Map(
      int line
    )
    {
      return new YamlNode(
        line,
        new Dictionary<string, YamlNode>(
          StringComparer.Ordinal
        ),
        null
      );
    }

    public static YamlNode Value(
      int line,
      string value
    )
    {
      return new YamlNode(
        line,
        null,
        value
      );
    }
  }
}
