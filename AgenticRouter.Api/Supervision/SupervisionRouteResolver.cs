using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.Supervision;

public interface ISupervisionRouteResolver
{
  Task<SupervisionRouteResolution> ResolveAsync(
    PrepareSupervisionRunRequest request,
    CancellationToken cancellationToken
  );

  Task<SupervisionResumeEligibility> EvaluateResumeAsync(
    DurableSupervisionCheckpoint checkpoint,
    CancellationToken cancellationToken
  );

  Task<SupervisionResumeEligibility> EvaluateExecutionAsync(
    DurableSupervisionCheckpoint checkpoint,
    CancellationToken cancellationToken
  );
}

public sealed class SupervisionRouteResolver : ISupervisionRouteResolver
{
  private readonly IWorkspaceProfileService _workspaces;
  private readonly ISettingsStore _settings;
  private readonly OllamaClient _ollama;
  private readonly IHarnessRegistry _harnesses;
  private readonly IAutoModelHarnessRoutingService _autoRoutes;

  public SupervisionRouteResolver(
    IWorkspaceProfileService workspaces,
    ISettingsStore settings,
    OllamaClient ollama,
    IHarnessRegistry harnesses,
    IAutoModelHarnessRoutingService autoRoutes
  )
  {
    _workspaces = workspaces;
    _settings = settings;
    _ollama = ollama;
    _harnesses = harnesses;
    _autoRoutes = autoRoutes;
  }

  public async Task<SupervisionRouteResolution> ResolveAsync(
    PrepareSupervisionRunRequest request,
    CancellationToken cancellationToken
  )
  {
    var objective = request.Objective.Trim();
    if (objective.Length is < 1 or > 16_384)
    {
      throw new SupervisionException(
        "supervision-objective-invalid",
        "supervision-prepare",
        "The supervision objective must contain between 1 and 16,384 characters.",
        true
      );
    }

    if (
      string.IsNullOrWhiteSpace(
        request.BrowserSessionId
      )
      || request.BrowserSessionId.Length > 128
    )
    {
      throw new SupervisionException(
        "supervision-browser-session-invalid",
        "supervision-prepare",
        "A valid browser session identifier is required.",
        true
      );
    }

    var requestedModel = request.Model?.Trim() ?? string.Empty;
    var useAutoRoute = request.AutoModelHarness
      || string.IsNullOrWhiteSpace(requestedModel)
      || string.Equals(requestedModel, "auto", StringComparison.OrdinalIgnoreCase);
    var modelReference = ProviderModelReference.Parse(requestedModel);
    if (
      (
        !string.IsNullOrWhiteSpace(requestedModel)
        && !modelReference.IsLocal
      )
      || (
        !useAutoRoute
        && string.IsNullOrWhiteSpace(modelReference.ModelId)
      )
    )
    {
      throw new SupervisionException(
        "supervision-local-model-required",
        "supervision-route",
        "Durable Supervised Execute requires one exact local Ollama model.",
        true
      );
    }

    var active = await _workspaces.GetActiveDataAsync(
      cancellationToken
    ) ?? throw new SupervisionException(
      "supervision-workspace-required",
      "supervision-route",
      "Durable Supervised Execute requires an active trusted workspace.",
      true
    );
    var workspaceInspection = WorkspacePathValidator.Inspect(
      active.Path
    );
    if (!workspaceInspection.Valid)
    {
      throw new SupervisionException(
        "supervision-workspace-invalid",
        "supervision-route",
        workspaceInspection.Diagnostic
          ?? "The active trusted workspace is unavailable.",
        true
      );
    }

    var resumePolicy = SupervisionRequestPolicy.NormalizeResumePolicy(
      request.ResumePolicy
    );
    if (
      string.Equals(
        resumePolicy,
        SupervisionResumePolicies.AutoSafe,
        StringComparison.Ordinal
      )
      && !active.HistoryEnabled
    )
    {
      throw new SupervisionException(
        "supervision-auto-resume-requires-history",
        "supervision-route",
        "Auto-safe restart requires local history for the active workspace.",
        true,
        409
      );
    }

    var settings = await _settings.GetAsync(
      cancellationToken
    );
    if (!Uri.TryCreate(
      settings.OllamaUrl,
      UriKind.Absolute,
      out var ollamaEndpoint
    ))
    {
      throw new SupervisionException(
        "supervision-ollama-endpoint-invalid",
        "supervision-route",
        "The configured local Ollama endpoint is invalid.",
        false
      );
    }

    IReadOnlyList<InstalledModel> installed;
    try
    {
      installed = await _ollama.GetModelsAsync(
        ollamaEndpoint,
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      throw new SupervisionException(
        "supervision-local-model-discovery-failed",
        "supervision-route",
        "Installed local Ollama models could not be discovered.",
        exception.Recoverable,
        exception.HttpStatus ?? 503,
        exception
      );
    }

    var selectedModel = modelReference.ModelId;
    var selectedHarness = request.Harness;
    if (useAutoRoute)
    {
      var autoRoute = await _autoRoutes.RouteAsync(
        request.ClientRunId ?? request.BrowserSessionId,
        objective,
        installed,
        cancellationToken
      );
      if (
        autoRoute.Status != AutoModelHarnessRoutingStatusIds.Selected
        || autoRoute.SelectedCandidate is null
      )
      {
        throw new SupervisionException(
          "supervision-auto-route-unavailable",
          "supervision-route",
          autoRoute.Reason,
          true,
          409
        );
      }
      selectedModel = autoRoute.SelectedCandidate.Model;
      selectedHarness = autoRoute.SelectedCandidate.Harness;
    }

    var model = installed.SingleOrDefault(
      item => string.Equals(
        item.Name,
        selectedModel,
        StringComparison.OrdinalIgnoreCase
      ) && string.Equals(
        item.Provider,
        ModelProviderIds.OllamaLocal,
        StringComparison.Ordinal
      )
    ) ?? throw new SupervisionException(
      "supervision-local-model-unavailable",
      "supervision-route",
      $"The exact local model '{selectedModel}' is not installed.",
      true,
      409
    );
    if (string.IsNullOrWhiteSpace(
      model.Digest
    ))
    {
      throw new SupervisionException(
        "supervision-local-model-digest-unavailable",
        "supervision-route",
        "The exact local model digest is unavailable for durable route identity.",
        true,
        409
      );
    }

    var harness = await ResolveHarnessAsync(
      selectedHarness,
      cancellationToken
    );
    if (string.IsNullOrWhiteSpace(
      harness.Availability.Version
    ))
    {
      throw new SupervisionException(
        "supervision-harness-version-unavailable",
        "supervision-route",
        "The selected harness version is unavailable for durable route identity.",
        true,
        409
      );
    }
    var conversationSessionId = string.IsNullOrWhiteSpace(
      request.ConversationSessionId
    )
      ? Guid.NewGuid().ToString(
        "N"
      )
      : request.ConversationSessionId.Trim();
    SupervisionRequestPolicy.ValidateId(
      conversationSessionId,
      "conversation session"
    );
    var canonicalWorkspace = Path.GetFullPath(
      active.Path
    ).TrimEnd(
      Path.DirectorySeparatorChar,
      Path.AltDirectorySeparatorChar
    );

    return new SupervisionRouteResolution(
      active.Id,
      conversationSessionId,
      active.HistoryEnabled,
      new SupervisionRouteSnapshot(
        ModelProviderIds.OllamaLocal,
        model.Name,
        model.Digest,
        harness.Definition.Id,
        harness.Availability.Version,
        NormalizeEndpoint(
          ollamaEndpoint
        ),
        SupervisionRequestPolicy.Hash(
          OperatingSystem.IsWindows()
            ? canonicalWorkspace.ToUpperInvariant()
            : canonicalWorkspace
        )
      )
    );
  }

  public Task<SupervisionResumeEligibility> EvaluateResumeAsync(
    DurableSupervisionCheckpoint checkpoint,
    CancellationToken cancellationToken
  )
  {
    return EvaluateRouteAsync(
      checkpoint,
      requireDurableHistory: true,
      cancellationToken
    );
  }

  public Task<SupervisionResumeEligibility> EvaluateExecutionAsync(
    DurableSupervisionCheckpoint checkpoint,
    CancellationToken cancellationToken
  )
  {
    return EvaluateRouteAsync(
      checkpoint,
      requireDurableHistory: false,
      cancellationToken
    );
  }

  private async Task<SupervisionResumeEligibility> EvaluateRouteAsync(
    DurableSupervisionCheckpoint checkpoint,
    bool requireDurableHistory,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var active = await _workspaces.GetActiveDataAsync(
        cancellationToken
      );
      if (
        active is null
        || !string.Equals(
          active.Id,
          checkpoint.WorkspaceId,
          StringComparison.Ordinal
        )
      )
      {
        return Ineligible(
          "The checkpoint workspace is not the active trusted workspace."
        );
      }

      if (requireDurableHistory && !active.HistoryEnabled)
      {
        return Ineligible(
          "Local history is disabled for the checkpoint workspace."
        );
      }

      var canonicalWorkspace = Path.GetFullPath(
        active.Path
      ).TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      );
      var pathHash = SupervisionRequestPolicy.Hash(
        OperatingSystem.IsWindows()
          ? canonicalWorkspace.ToUpperInvariant()
          : canonicalWorkspace
      );
      if (!string.Equals(
        pathHash,
        checkpoint.Route.WorkspacePathSha256,
        StringComparison.Ordinal
      ))
      {
        return Ineligible(
          "The canonical trusted workspace path changed."
        );
      }

      var settings = await _settings.GetAsync(
        cancellationToken
      );
      var endpoint = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      if (!string.Equals(
        NormalizeEndpoint(
          endpoint
        ),
        checkpoint.Route.OllamaEndpoint,
        StringComparison.OrdinalIgnoreCase
      ))
      {
        return Ineligible(
          "The configured local Ollama endpoint changed."
        );
      }

      var installed = await _ollama.GetModelsAsync(
        endpoint,
        cancellationToken
      );
      var model = installed.SingleOrDefault(
        item => string.Equals(
          item.Provider,
          ModelProviderIds.OllamaLocal,
          StringComparison.Ordinal
        ) && string.Equals(
          item.Name,
          checkpoint.Route.Model,
          StringComparison.OrdinalIgnoreCase
        )
      );
      if (
        model is null
        || !string.Equals(
          model.Digest ?? "unavailable",
          checkpoint.Route.ModelDigest,
          StringComparison.Ordinal
        )
      )
      {
        return Ineligible(
          "The exact local model digest is unavailable or changed."
        );
      }

      var harness = await ResolveHarnessAsync(
        checkpoint.Route.Harness,
        cancellationToken
      );
      if (!string.Equals(
        harness.Availability.Version ?? "unavailable",
        checkpoint.Route.HarnessVersion,
        StringComparison.Ordinal
      ))
      {
        return Ineligible(
          "The selected harness version changed."
        );
      }

      return new SupervisionResumeEligibility(
        true,
        null
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
      or SupervisionException
      or UriFormatException
      or IOException
      or UnauthorizedAccessException
    )
    {
      return Ineligible(
        $"Resume validation failed: {exception.Message}"
      );
    }
  }

  private async Task<HarnessStatus> ResolveHarnessAsync(
    string harnessId,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        harnessId
      )
      || !_harnesses.TryGetAdapter(
        harnessId.Trim(),
        out var adapter
      )
    )
    {
      throw new SupervisionException(
        "supervision-harness-unavailable",
        "supervision-route",
        "The selected harness is not registered.",
        true,
        409
      );
    }

    if (
      adapter.Definition.SupportedProviders is { Count: > 0 }
      && !adapter.Definition.SupportedProviders.Contains(
        ModelProviderIds.OllamaLocal,
        StringComparer.Ordinal
      )
    )
    {
      throw new SupervisionException(
        "supervision-harness-local-provider-unsupported",
        "supervision-route",
        "The selected harness does not support the mandatory local Ollama provider.",
        false,
        409
      );
    }

    var availability = await adapter.GetAvailabilityAsync(
      cancellationToken
    );
    if (!availability.Available)
    {
      throw new SupervisionException(
        "supervision-harness-unavailable",
        "supervision-route",
        availability.Message
          ?? "The selected harness is unavailable.",
        true,
        409
      );
    }

    return new HarnessStatus(
      adapter.Definition,
      availability
    );
  }

  private static SupervisionResumeEligibility Ineligible(string reason)
  {
    return new SupervisionResumeEligibility(
      false,
      reason
    );
  }

  private static string NormalizeEndpoint(Uri endpoint)
  {
    return endpoint.AbsoluteUri.TrimEnd(
      '/'
    );
  }
}
