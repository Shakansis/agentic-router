using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Runtime;

public static class OllamaRuntimeProfileResolver
{
  public static OllamaContextResolution Resolve(
    ApplicationSettings settings,
    string model,
    string? digest,
    string usageRole,
    int? declaredModelMaximum,
    long requiredInputTokens,
    int requestedOutputTokens
  )
  {
    var role = NormalizeRole(
      usageRole
    );
    var savedOverride = GetModelOverride(
      settings,
      model,
      digest,
      role
    );
    var overridden = savedOverride is not null;
    var profile = savedOverride ?? GetRoleProfile(
      settings,
      role
    );
    var configuredRoles = ConfiguredRoles(
      settings,
      model
    ).Where(
      configuredRole => !(
        role == OllamaRuntimeRoleIds.ResidentCoordinator
        && configuredRole == OllamaRuntimeRoleIds.Fallback
      ) && !(
        role == OllamaRuntimeRoleIds.Router
        && configuredRole == OllamaRuntimeRoleIds.Fallback
      ) && !(
        role == OllamaRuntimeRoleIds.Fallback
        && configuredRole == OllamaRuntimeRoleIds.ResidentCoordinator
      )
    ).ToArray();
    var shared = configuredRoles.Length > 1;
    var configuredProfiles = configuredRoles.Select(
      configuredRole => GetConfiguredProfile(
        settings,
        model,
        digest,
        configuredRole
      )
    ).ToArray();
    var sharedTarget = shared
      ? configuredProfiles.Max(
        candidate => candidate.TargetContextTokens
      )
      : profile.TargetContextTokens;
    var sharedMaximum = shared
      ? configuredProfiles.Max(
        candidate => candidate.MaximumContextTokens
      )
      : profile.MaximumContextTokens;
    var target = Math.Max(
      profile.TargetContextTokens,
      sharedTarget
    );
    var providerCeiling = settings.Context.ProviderContextTokens;
    var maximum = Math.Min(
      sharedMaximum,
      providerCeiling
    );
    var cappedByModel = declaredModelMaximum is > 0
      && maximum > declaredModelMaximum.Value;

    if (declaredModelMaximum is > 0)
    {
      maximum = Math.Min(
        maximum,
        declaredModelMaximum.Value
      );
    }

    var minimum = Math.Min(
      profile.MinimumContextTokens,
      maximum
    );
    target = Math.Clamp(
      target,
      minimum,
      maximum
    );
    var outputTokens = Math.Min(
      requestedOutputTokens,
      profile.OutputTokenLimit
    );
    var required = checked(
      (int)Math.Min(
        int.MaxValue,
        requiredInputTokens + outputTokens
      )
    );
    var effective = target;
    var escalated = false;

    if (required > effective)
    {
      var candidate = settings.OllamaRuntime.ContextEscalationLadder.FirstOrDefault(
        value => value >= required
          && value >= target
          && value <= maximum
      );

      if (candidate == 0)
      {
        throw new OllamaRuntimeProfileException(
          "request-context-does-not-fit",
          "The request does not fit the configured Ollama runtime context.",
          "request-context-fit",
          model,
          digest,
          role,
          required,
          null,
          false,
          $"Required {required} tokens but the configured maximum is {maximum}.",
          estimatedInputTokens: checked((int)Math.Min(int.MaxValue, requiredInputTokens)),
          reservedOutputTokens: outputTokens,
          requiredContextTokens: required,
          maximumContextTokens: maximum,
          effectiveContextTokens: effective
        );
      }

      effective = candidate;
      escalated = effective > target;
    }

    var sharedWarning = shared
      ? $"The exact model is configured for {string.Join(", ", configuredRoles)}. "
        + $"One loaded Ollama runner may use the largest active role context ({sharedTarget} tokens); "
        + "separate models provide more stable residency."
      : null;

    return new OllamaContextResolution(
      ModelProviderIds.OllamaLocal,
      model,
      digest,
      role,
      minimum,
      target,
      maximum,
      effective,
      required,
      outputTokens,
      profile.KeepAlive,
      declaredModelMaximum,
      overridden,
      escalated,
      cappedByModel,
      shared,
      sharedWarning,
      escalated
        ? $"The request required {required} tokens, so context escalated from {target} to {effective}."
        : overridden
          ? "The exact model and digest override was applied."
          : "The role default was inherited."
    );
  }

  public static string NormalizeRole(
    string usageRole
  )
  {
    return usageRole switch
    {
      UsageModelRoles.Router => OllamaRuntimeRoleIds.Router,
      UsageModelRoles.Action => OllamaRuntimeRoleIds.ResidentCoordinator,
      UsageModelRoles.Coordinator => OllamaRuntimeRoleIds.Fallback,
      UsageModelRoles.Specialist => OllamaRuntimeRoleIds.Specialist,
      UsageModelRoles.Fallback => OllamaRuntimeRoleIds.Fallback,
      UsageModelRoles.Benchmark => OllamaRuntimeRoleIds.Benchmark,
      UsageModelRoles.ModelTest => OllamaRuntimeRoleIds.ModelTest,
      UsageModelRoles.WebSearchSynthesis => OllamaRuntimeRoleIds.WebSearchSynthesis,
      UsageModelRoles.VisionRequest => OllamaRuntimeRoleIds.VisionRequest,
      _ => OllamaRuntimeRoleIds.Primary
    };
  }

  public static IReadOnlyList<string> ConfiguredRoles(
    ApplicationSettings settings,
    string model
  )
  {
    var roles = new HashSet<string>(
      StringComparer.Ordinal
    );

    AddRole(
      roles,
      model,
      settings.CoordinatorModel,
      OllamaRuntimeRoleIds.Fallback
    );
    AddRole(
      roles,
      model,
      settings.DefaultModel,
      OllamaRuntimeRoleIds.Primary
    );

    foreach (var intention in settings.Intentions.Values)
    {
      var primary = string.Equals(
        intention.Model,
        "default",
        StringComparison.OrdinalIgnoreCase
      )
        ? settings.DefaultModel
        : intention.Model;
      var fallback = string.Equals(
        intention.FallbackModel,
        "default",
        StringComparison.OrdinalIgnoreCase
      )
        ? settings.DefaultModel
        : intention.FallbackModel;
      AddRole(
        roles,
        model,
        primary,
        OllamaRuntimeRoleIds.Specialist
      );

      if (!string.Equals(
        fallback,
        "none",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        AddRole(
          roles,
          model,
          fallback,
          OllamaRuntimeRoleIds.Fallback
        );
      }
    }

    return roles.Order(
      StringComparer.Ordinal
    ).ToArray();
  }

  public static OllamaRoleRuntimeSettings GetConfiguredProfile(
    ApplicationSettings settings,
    string model,
    string? digest,
    string role
  )
  {
    return GetModelOverride(
      settings,
      model,
      digest,
      role
    ) ?? GetRoleProfile(
      settings,
      role
    );
  }

  private static OllamaRoleRuntimeSettings GetRoleProfile(
    ApplicationSettings settings,
    string role
  )
  {
    if (settings.OllamaRuntime.RoleDefaults.TryGetValue(
      role,
      out var profile
    ))
    {
      return profile;
    }

    return OllamaRuntimeDefaults.CreateRoleDefaults()[role];
  }

  private static OllamaRoleRuntimeSettings? GetModelOverride(
    ApplicationSettings settings,
    string model,
    string? digest,
    string role
  )
  {
    var modelOverride = settings.OllamaRuntime.ModelOverrides.FirstOrDefault(
      candidate => string.Equals(
        candidate.Provider,
        ModelProviderIds.OllamaLocal,
        StringComparison.Ordinal
      ) && string.Equals(
        candidate.Model,
        model,
        StringComparison.Ordinal
      ) && string.Equals(
        candidate.Digest,
        digest,
        StringComparison.Ordinal
      )
    );

    return modelOverride is not null
      && modelOverride.Overrides.TryGetValue(
        role,
        out var profile
      )
        ? profile
        : null;
  }

  private static void AddRole(
    ISet<string> roles,
    string model,
    string configuredModel,
    string role
  )
  {
    var reference = ProviderModelReference.Parse(
      configuredModel
    );

    if (
      reference.IsLocal
      && string.Equals(
        reference.ModelId,
        model,
        StringComparison.Ordinal
      )
    )
    {
      roles.Add(
        role
      );
    }
  }
}
