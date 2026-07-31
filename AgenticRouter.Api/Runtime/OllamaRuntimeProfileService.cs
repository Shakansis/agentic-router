using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Runtime;

public sealed class OllamaRuntimeProfileService : IOllamaRuntimeProfileService
{
  private const int MeasurementSchemaVersion = 1;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };

  private readonly string _storePath;
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IResidentModelManager _residentModel;
  private readonly IGpuMemoryMetricsProvider _gpuMemory;
  private readonly ISystemMemoryMetricsProvider _systemMemory;
  private readonly SemaphoreSlim _measurementGate = new(
    1,
    1
  );

  public OllamaRuntimeProfileService(
    string dataDirectory,
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IResidentModelManager residentModel,
    IGpuMemoryMetricsProvider gpuMemory,
    ISystemMemoryMetricsProvider systemMemory
  )
  {
    _storePath = Path.Combine(
      dataDirectory,
      "runtime-profiles",
      "ollama-model-memory.json"
    );
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _residentModel = residentModel;
    _gpuMemory = gpuMemory;
    _systemMemory = systemMemory;
  }

  public async Task<OllamaRuntimeProfilesView> GetAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var baseUri = new Uri(
      settings.OllamaUrl,
      UriKind.Absolute
    );
    var installed = await _ollamaClient.GetModelsAsync(
      baseUri,
      cancellationToken
    );
    var running = await _ollamaClient.GetRunningModelsAsync(
      baseUri,
      cancellationToken
    );
    var version = await _ollamaClient.GetVersionAsync(
      baseUri,
      cancellationToken
    );
    var hardwareSignature = HardwareSignature(
      _gpuMemory.GetStatus().Devices,
      _systemMemory.GetStatus()
    );
    var records = await ReadRecordsAsync(
      cancellationToken
    );
    var recommendations = new List<OllamaRuntimeRecommendation>();
    var warnings = new List<OllamaSharedModelWarning>();

    foreach (var configured in ConfiguredModelRoles(
      settings
    ))
    {
      var exact = installed.FirstOrDefault(
        model => string.Equals(
          model.Name,
          configured.Model,
          StringComparison.Ordinal
        )
      );

      if (exact is null)
      {
        continue;
      }

      var metadata = await TryGetMetadataAsync(
        baseUri,
        exact.Name,
        cancellationToken
      );
      recommendations.Add(
        Recommendation(
          settings,
          exact,
          configured.Role,
          metadata,
          running,
          records,
          version,
          hardwareSignature
        )
      );
    }

    foreach (var group in ConfiguredModelRoles(
      settings
    ).GroupBy(
      entry => entry.Model,
      StringComparer.Ordinal
    ).Where(
      group => group.Select(
        entry => entry.Role
      ).Distinct(
        StringComparer.Ordinal
      ).Count() > 1
    ))
    {
      var exact = installed.FirstOrDefault(
        model => string.Equals(
          model.Name,
          group.Key,
          StringComparison.Ordinal
        )
      );
      var roles = group.Select(
        entry => entry.Role
      ).Distinct(
        StringComparer.Ordinal
      ).Order(
        StringComparer.Ordinal
      ).ToArray();
      warnings.Add(
        new OllamaSharedModelWarning(
          group.Key,
          exact?.Digest ?? "unknown",
          roles,
          roles.Select(
            role => OllamaRuntimeProfileResolver.GetConfiguredProfile(
              settings,
              group.Key,
              exact?.Digest,
              role
            ).TargetContextTokens
          ).Max(),
          $"One exact model is configured for {string.Join(", ", roles)}. "
            + "The loaded runner may need the largest active role context; separate models reduce reload pressure."
        )
      );
    }

    var measurementViews = records.Records.Select(
      record => record with
      {
        Stale = IsStale(
          record,
          installed,
          version,
          hardwareSignature,
          settings
        )
      }
    ).OrderByDescending(
      record => record.MeasuredAt
    ).ToArray();

    return new OllamaRuntimeProfilesView(
      settings.OllamaRuntime.ProfileSchemaVersion,
      settings.OllamaRuntime.RoleDefaults,
      settings.OllamaRuntime.ModelOverrides,
      settings.OllamaRuntime.ContextEscalationLadder,
      settings.OllamaRuntime.Memory,
      recommendations.DistinctBy(
        recommendation => string.Join(
          "|",
          recommendation.Model,
          recommendation.Digest,
          recommendation.Role
        )
      ).OrderBy(
        recommendation => recommendation.Role,
        StringComparer.Ordinal
      ).ThenBy(
        recommendation => recommendation.Model,
        StringComparer.Ordinal
      ).ToArray(),
      measurementViews,
      warnings,
      DateTimeOffset.UtcNow
    );
  }

  public async Task<OllamaRuntimeAnalysisResult> AnalyzeAsync(
    OllamaRuntimeAnalysisRequest request,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var role = RequireRole(
      request.Role,
      request.Model
    );
    var baseUri = new Uri(
      settings.OllamaUrl,
      UriKind.Absolute
    );
    var runningBefore = await _ollamaClient.GetRunningModelsAsync(
      baseUri,
      cancellationToken
    );
    var installed = await RequireInstalledAsync(
      baseUri,
      request.Model,
      role,
      cancellationToken
    );
    var metadata = await RequireMetadataAsync(
      baseUri,
      installed,
      role,
      cancellationToken
    );
    var version = await _ollamaClient.GetVersionAsync(
      baseUri,
      cancellationToken
    );
    var records = await ReadRecordsAsync(
      cancellationToken
    );
    var hardwareSignature = HardwareSignature(
      _gpuMemory.GetStatus().Devices,
      _systemMemory.GetStatus()
    );
    var resolution = OllamaRuntimeProfileResolver.Resolve(
      settings,
      installed.Name,
      installed.Digest,
      role,
      metadata.DeclaredContextTokens,
      0,
      0
    );
    var runningAfter = await _ollamaClient.GetRunningModelsAsync(
      baseUri,
      cancellationToken
    );

    return new OllamaRuntimeAnalysisResult(
      Recommendation(
        settings,
        installed,
        role,
        metadata,
        runningAfter,
        records,
        version,
        hardwareSignature
      ),
      resolution,
      !SameRunningModels(
        runningBefore,
        runningAfter
      )
    );
  }

  public async Task<OllamaRuntimeMeasurementResult> MeasureAsync(
    OllamaRuntimeMeasurementRequest request,
    CancellationToken cancellationToken
  )
  {
    var role = RequireRole(
      request.Role,
      request.Model
    );

    if (!request.PermissionGranted)
    {
      throw new OllamaRuntimeProfileException(
        "measurement-permission-required",
        "Explicit permission is required before loading a real Ollama model for measurement.",
        "memory-profile-measurement",
        request.Model,
        null,
        role,
        request.ContextCandidates.FirstOrDefault(),
        null,
        false,
        "Confirm the exact model, context candidates, expected loads, and optional test call."
      );
    }

    if (
      request.ContextCandidates.Count != 1
      || request.ContextCandidates.Any(
        context => context is < 1_024 or > 40_960
      )
    )
    {
      throw new OllamaRuntimeProfileException(
        "invalid-runtime-profile-request",
        "Choose exactly one bounded context candidate per measurement.",
        "memory-profile-measurement",
        request.Model,
        null,
        role,
        request.ContextCandidates.FirstOrDefault(),
        null,
        false,
        "A measurement request must contain one context candidate between 1024 and 40960 tokens."
      );
    }

    if (_residentModel.HasActiveRequests)
    {
      throw new OllamaRuntimeProfileException(
        "reload-blocked-by-active-request",
        "Runtime measurement is blocked by an active model request.",
        "memory-profile-measurement",
        request.Model,
        null,
        role,
        request.ContextCandidates[0],
        null,
        true,
        "Wait for the active request to finish or cancel it before measuring."
      );
    }

    await _measurementGate.WaitAsync(
      cancellationToken
    );

    try
    {
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      var installed = await RequireInstalledAsync(
        baseUri,
        request.Model,
        role,
        cancellationToken
      );
      var metadata = await RequireMetadataAsync(
        baseUri,
        installed,
        role,
        cancellationToken
      );
      var candidate = request.ContextCandidates[0];
      var roleProfile = OllamaRuntimeProfileResolver.GetConfiguredProfile(
        settings,
        installed.Name,
        installed.Digest,
        role
      );

      if (metadata.DeclaredContextTokens is null)
      {
        throw new OllamaRuntimeProfileException(
          "declared-context-unavailable",
          "Ollama did not declare a maximum context for this model.",
          "memory-profile-measurement",
          installed.Name,
          installed.Digest,
          role,
          candidate,
          null,
          false,
          "The native /api/show response did not contain a positive context_length."
        );
      }

      if (!settings.OllamaRuntime.ContextEscalationLadder.Contains(
        candidate
      ))
      {
        throw new OllamaRuntimeProfileException(
          "invalid-runtime-profile-request",
          "The measurement context must be one of the configured discrete ladder values.",
          "memory-profile-measurement",
          installed.Name,
          installed.Digest,
          role,
          candidate,
          null,
          false,
          $"Configured ladder: {string.Join(", ", settings.OllamaRuntime.ContextEscalationLadder)}."
        );
      }
      var maximum = Math.Min(
        roleProfile.MaximumContextTokens,
        settings.Context.ProviderContextTokens
      );

      maximum = Math.Min(
        maximum,
        metadata.DeclaredContextTokens.Value
      );

      if (candidate > maximum)
      {
        throw new OllamaRuntimeProfileException(
          "requested-context-exceeds-model-maximum",
          "The requested measurement context exceeds the applicable model or role maximum.",
          "memory-profile-measurement",
          installed.Name,
          installed.Digest,
          role,
          candidate,
          null,
          false,
          $"The applicable maximum is {maximum} tokens."
        );
      }

      var version = await _ollamaClient.GetVersionAsync(
        baseUri,
        cancellationToken
      );
      var beforeRunning = await _ollamaClient.GetRunningModelsAsync(
        baseUri,
        cancellationToken
      );
      var priorTarget = FindRunning(
        beforeRunning,
        installed.Name
      );
      var targetWasLoaded = priorTarget is not null;
      var gpuBefore = _gpuMemory.GetStatus().Devices;
      var ramBefore = _systemMemory.GetStatus();
      var hardwareSignature = HardwareSignature(
        gpuBefore,
        ramBefore
      );
      var residentEvicted = false;
      long? minimalRequestMilliseconds = null;
      OllamaRunningModel? measured = null;
      var loadStopwatch = Stopwatch.StartNew();
      using var requestLease = _residentModel.BeginRequest();

      try
      {
        if (
          priorTarget is not null
          && priorTarget.ContextLength != candidate
        )
        {
          await _ollamaClient.SetModelResidencyAsync(
            baseUri,
            installed.Name,
            0,
            cancellationToken
          );
        }

        if (priorTarget is null)
        {
          residentEvicted = await _residentModel.EvictForRecoveryAsync(
            installed.Name,
            cancellationToken
          );
        }

        if (priorTarget?.ContextLength != candidate)
        {
          await _ollamaClient.SetModelResidencyAsync(
            baseUri,
            installed.Name,
            role == OllamaRuntimeRoleIds.ResidentCoordinator
              ? -1
              : roleProfile.KeepAlive,
            candidate,
            cancellationToken
          );
        }

        var afterLoad = await _ollamaClient.GetRunningModelsAsync(
          baseUri,
          cancellationToken
        );
        measured = FindRunning(
          afterLoad,
          installed.Name
        );
        loadStopwatch.Stop();

        if (measured?.ContextLength != candidate)
        {
          throw new OllamaRuntimeProfileException(
            "actual-context-not-verified",
            "Ollama did not verify the requested measurement context.",
            "memory-profile-measurement",
            installed.Name,
            installed.Digest,
            role,
            candidate,
            measured?.ContextLength,
            true,
            "The exact model and requested context_length were not confirmed through /api/ps."
          );
        }

        if (request.RunMinimalRequest)
        {
          var requestStopwatch = Stopwatch.StartNew();
          await _ollamaClient.GenerateTextAsync(
            baseUri,
            installed.Name,
            [
              new ChatMessage(
                "user",
                "Reply with exactly: OK"
              )
            ],
            "memory-profile-minimal-request",
            new ProviderCallContext(
              null,
              null,
              Guid.NewGuid().ToString(
                "N"
              ),
              null,
              MeasurementUsageRole(
                role
              ),
              "memory-profile-minimal-request",
              installed.Digest,
              candidate
            ),
            cancellationToken
          );
          requestStopwatch.Stop();
          minimalRequestMilliseconds = requestStopwatch.ElapsedMilliseconds;
        }

        var gpuAfter = _gpuMemory.GetStatus().Devices;
        var ramAfter = _systemMemory.GetStatus();
        var estimatedRam = EstimatedRam(
          measured
        );
        var processor = Processor(
          measured
        );
        var settingSignature = RuntimeSettingSignature(
          candidate,
          measured.ContextLength,
          metadata,
          version
        );
        var measurement = new OllamaRuntimeMeasurementView(
          MeasurementSchemaVersion,
          ModelProviderIds.OllamaLocal,
          installed.Name,
          installed.Digest ?? "unknown",
          version,
          role,
          candidate,
          measured.ContextLength.Value,
          measured.SizeBytes,
          measured.VramSizeBytes,
          estimatedRam,
          gpuBefore,
          gpuAfter,
          ramBefore,
          ramAfter,
          loadStopwatch.ElapsedMilliseconds,
          minimalRequestMilliseconds,
          processor,
          DateTimeOffset.UtcNow,
          hardwareSignature,
          settingSignature,
          "measured",
          null,
          false
        );
        await SaveRecordAsync(
          measurement,
          cancellationToken
        );

        return new OllamaRuntimeMeasurementResult(
          measurement,
          true,
          targetWasLoaded
        );
      }
      finally
      {
        try
        {
          if (
            priorTarget is null
            && measured is not null
          )
          {
            await _ollamaClient.SetModelResidencyAsync(
              baseUri,
              installed.Name,
              0,
              CancellationToken.None
            );
          }
          else if (
            priorTarget is not null
            && priorTarget.ContextLength is not null
            && priorTarget.ContextLength != candidate
          )
          {
            await _ollamaClient.SetModelResidencyAsync(
              baseUri,
              installed.Name,
              0,
              CancellationToken.None
            );
            await _ollamaClient.SetModelResidencyAsync(
              baseUri,
              installed.Name,
              priorTarget.ExpiresAt is null
                ? -1
                : roleProfile.KeepAlive,
              priorTarget.ContextLength,
              CancellationToken.None
            );
          }

          if (residentEvicted)
          {
            await _residentModel.RestoreAfterRecoveryAsync(
              installed.Name,
              CancellationToken.None
            );
          }
        }
        catch (Exception exception)
        {
          throw new OllamaRuntimeProfileException(
            "measurement-restoration-failed",
            "The runtime measurement completed, but the prior resident state could not be restored.",
            "memory-profile-restoration",
            installed.Name,
            installed.Digest,
            role,
            candidate,
            measured?.ContextLength,
            true,
            exception.Message,
            exception
          );
        }
      }
    }
    finally
    {
      _measurementGate.Release();
    }
  }

  public async Task<IReadOnlyDictionary<string, string[]>> ValidateOverridesAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    var errors = new Dictionary<string, string[]>(
      StringComparer.Ordinal
    );

    if (settings.OllamaRuntime.ModelOverrides.Count == 0)
    {
      return errors;
    }

    var baseUri = new Uri(
      settings.OllamaUrl,
      UriKind.Absolute
    );
    var installed = await _ollamaClient.GetModelsAsync(
      baseUri,
      cancellationToken
    );

    for (
      var index = 0;
      index < settings.OllamaRuntime.ModelOverrides.Count;
      index++
    )
    {
      var modelOverride = settings.OllamaRuntime.ModelOverrides[index];
      var exact = installed.FirstOrDefault(
        model => string.Equals(
          model.Name,
          modelOverride.Model,
          StringComparison.Ordinal
        ) && string.Equals(
          model.Digest,
          modelOverride.Digest,
          StringComparison.Ordinal
        )
      );

      if (exact is null)
      {
        errors[$"ollamaRuntime.modelOverrides.{index}"] =
        [
          "The exact model ID and digest are not installed in the configured Ollama runtime."
        ];
        continue;
      }

      OllamaModelMetadata metadata;

      try
      {
        metadata = await RequireMetadataAsync(
          baseUri,
          exact,
          "override-validation",
          cancellationToken
        );
      }
      catch (OllamaRuntimeProfileException exception)
      {
        errors[$"ollamaRuntime.modelOverrides.{index}"] =
        [
          exception.Message
        ];
        continue;
      }

      if (metadata.DeclaredContextTokens is null)
      {
        errors[$"ollamaRuntime.modelOverrides.{index}"] =
        [
          "Ollama did not declare a maximum context for this exact model."
        ];
        continue;
      }

      foreach (var pair in modelOverride.Overrides)
      {
        if (pair.Value.MaximumContextTokens > metadata.DeclaredContextTokens)
        {
          errors[
            $"ollamaRuntime.modelOverrides.{index}.overrides.{pair.Key}.maximumContextTokens"
          ] =
          [
            $"The configured maximum exceeds the model-declared limit of {metadata.DeclaredContextTokens} tokens."
          ];
        }
      }
    }

    return errors;
  }

  private static OllamaRuntimeRecommendation Recommendation(
    ApplicationSettings settings,
    InstalledModel installed,
    string role,
    OllamaModelMetadata? metadata,
    IReadOnlyList<OllamaRunningModel> running,
    MeasurementDocument records,
    string version,
    string hardwareSignature
  )
  {
    var resolution = OllamaRuntimeProfileResolver.Resolve(
      settings,
      installed.Name,
      installed.Digest,
      role,
      metadata?.DeclaredContextTokens,
      0,
      0
    );
    var loaded = FindRunning(
      running,
      installed.Name
    );
    var measured = records.Records
      .Where(
        record => string.Equals(
          record.Model,
          installed.Name,
          StringComparison.Ordinal
        ) && string.Equals(
          record.Digest,
          installed.Digest,
          StringComparison.Ordinal
        ) && string.Equals(
          record.Role,
          role,
          StringComparison.Ordinal
        )
      )
      .OrderByDescending(
        record => record.MeasuredAt
      )
      .FirstOrDefault();
    var stale = measured is not null && IsStale(
      measured,
      [installed],
      version,
      hardwareSignature,
      settings
    );
    var reason = role switch
    {
      OllamaRuntimeRoleIds.ResidentCoordinator => "lightweight resident coordinator",
      OllamaRuntimeRoleIds.Router => "router classification only",
      OllamaRuntimeRoleIds.Specialist => "coding or task specialist",
      OllamaRuntimeRoleIds.WebSearchSynthesis => "web-search result synthesis",
      OllamaRuntimeRoleIds.VisionRequest => "image-token overhead",
      _ => "role default with insufficient measured evidence"
    };

    if (loaded is not null && Processor(
      loaded
    ) != "gpu")
    {
      reason = "CPU offload detected";
    }

    return new OllamaRuntimeRecommendation(
      ModelProviderIds.OllamaLocal,
      installed.Name,
      installed.Digest ?? "unknown",
      role,
      metadata?.DeclaredContextTokens,
      resolution.EffectiveContextTokens,
      loaded?.ContextLength,
      loaded?.VramSizeBytes,
      loaded is null
        ? null
        : EstimatedRam(
          loaded
        ),
      measured is not null && !stale
        ? "measured"
        : "metadata-analysis",
      resolution.MinimumContextTokens,
      resolution.TargetContextTokens,
      resolution.MaximumContextTokens,
      reason,
      measured is not null && !stale
        ? "high"
        : metadata?.DeclaredContextTokens is not null
          ? "medium"
          : "low",
      stale,
      resolution.SharedModelWarning
    );
  }

  private async Task<InstalledModel> RequireInstalledAsync(
    Uri baseUri,
    string model,
    string role,
    CancellationToken cancellationToken
  )
  {
    var exact = (await _ollamaClient.GetModelsAsync(
      baseUri,
      cancellationToken
    )).FirstOrDefault(
      candidate => string.Equals(
        candidate.Name,
        model,
        StringComparison.Ordinal
      )
    );

    return exact ?? throw new OllamaRuntimeProfileException(
      "model-metadata-unavailable",
      "The exact local Ollama model is not installed.",
      "model-metadata-inspection",
      model,
      null,
      role,
      null,
      null,
      false,
      "The exact model ID was absent from /api/tags."
    );
  }

  private async Task<OllamaModelMetadata> RequireMetadataAsync(
    Uri baseUri,
    InstalledModel installed,
    string role,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await _ollamaClient.GetModelMetadataAsync(
        baseUri,
        installed.Name,
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
        or OllamaRuntimeProfileException
    )
    {
      throw new OllamaRuntimeProfileException(
        "model-metadata-unavailable",
        "Ollama model metadata is unavailable.",
        "model-metadata-inspection",
        installed.Name,
        installed.Digest,
        role,
        null,
        null,
        true,
        exception.Message,
        exception
      );
    }
  }

  private async Task<OllamaModelMetadata?> TryGetMetadataAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await _ollamaClient.GetModelMetadataAsync(
        baseUri,
        model,
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is OllamaProviderException
        or OllamaRuntimeProfileException
    )
    {
      return null;
    }
  }

  private async Task<MeasurementDocument> ReadRecordsAsync(
    CancellationToken cancellationToken
  )
  {
    if (!File.Exists(
      _storePath
    ))
    {
      return new MeasurementDocument();
    }

    await using var stream = File.OpenRead(
      _storePath
    );
    var document = await JsonSerializer.DeserializeAsync<MeasurementDocument>(
      stream,
      JsonOptions,
      cancellationToken
    );

    return document?.SchemaVersion == MeasurementSchemaVersion
      ? document
      : new MeasurementDocument();
  }

  private async Task SaveRecordAsync(
    OllamaRuntimeMeasurementView measurement,
    CancellationToken cancellationToken
  )
  {
    var document = await ReadRecordsAsync(
      cancellationToken
    );
    var records = document.Records.Where(
      record => !string.Equals(
        record.Provider,
        measurement.Provider,
        StringComparison.Ordinal
      ) || !string.Equals(
        record.Model,
        measurement.Model,
        StringComparison.Ordinal
      ) || !string.Equals(
        record.Digest,
        measurement.Digest,
        StringComparison.Ordinal
      ) || !string.Equals(
        record.OllamaVersion,
        measurement.OllamaVersion,
        StringComparison.Ordinal
      ) || !string.Equals(
        record.Role,
        measurement.Role,
        StringComparison.Ordinal
      ) || !string.Equals(
        record.HardwareSignature,
        measurement.HardwareSignature,
        StringComparison.Ordinal
      ) || !string.Equals(
        record.RuntimeSettingSignature,
        measurement.RuntimeSettingSignature,
        StringComparison.Ordinal
      )
    ).Append(
      measurement
    ).OrderByDescending(
      record => record.MeasuredAt
    ).Take(
      200
    ).ToArray();
    var updated = new MeasurementDocument
    {
      Records = records
    };
    var directory = Path.GetDirectoryName(
      _storePath
    )!;
    Directory.CreateDirectory(
      directory
    );
    var temporary = Path.Combine(
      directory,
      $".ollama-model-memory-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      await File.WriteAllTextAsync(
        temporary,
        JsonSerializer.Serialize(
          updated,
          JsonOptions
        ).Replace(
          "\r\n",
          "\n",
          StringComparison.Ordinal
        ) + "\n",
        cancellationToken
      );
      File.Move(
        temporary,
        _storePath,
        true
      );
    }
    finally
    {
      if (File.Exists(
        temporary
      ))
      {
        File.Delete(
          temporary
        );
      }
    }
  }

  private static IReadOnlyList<ConfiguredModelRole> ConfiguredModelRoles(
    ApplicationSettings settings
  )
  {
    var values = new List<ConfiguredModelRole>();
    AddConfigured(
      values,
      settings.RouterModel,
      OllamaRuntimeRoleIds.Router
    );
    AddConfigured(
      values,
      settings.CoordinatorModel,
      OllamaRuntimeRoleIds.ResidentCoordinator
    );
    AddConfigured(
      values,
      settings.DefaultModel,
      OllamaRuntimeRoleIds.Primary
    );

    foreach (var intention in settings.Intentions.Values)
    {
      AddConfigured(
        values,
        string.Equals(
          intention.Model,
          "default",
          StringComparison.OrdinalIgnoreCase
        )
          ? settings.DefaultModel
          : intention.Model,
        OllamaRuntimeRoleIds.Specialist
      );

      if (!string.Equals(
        intention.FallbackModel,
        "none",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        AddConfigured(
          values,
          string.Equals(
            intention.FallbackModel,
            "default",
            StringComparison.OrdinalIgnoreCase
          )
            ? settings.DefaultModel
            : intention.FallbackModel,
          OllamaRuntimeRoleIds.Fallback
        );
      }
    }

    return values.Distinct().ToArray();
  }

  private static void AddConfigured(
    ICollection<ConfiguredModelRole> values,
    string model,
    string role
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    if (
      reference.IsLocal
      && !string.IsNullOrWhiteSpace(
        reference.ModelId
      )
      && !string.Equals(
        reference.ModelId,
        "configure-model",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      values.Add(
        new ConfiguredModelRole(
          reference.ModelId,
          role
        )
      );
    }
  }

  private static string RequireRole(
    string role,
    string model
  )
  {
    if (OllamaRuntimeRoleIds.All.Contains(
      role,
      StringComparer.Ordinal
    ))
    {
      return role;
    }

    throw new OllamaRuntimeProfileException(
      "invalid-runtime-profile-request",
      "The selected Ollama runtime role is not supported.",
      "runtime-profile-resolution",
      model,
      null,
      role,
      null,
      null,
      false,
      "Choose one of the application-owned runtime role IDs."
    );
  }

  private static bool IsStale(
    OllamaRuntimeMeasurementView record,
    IReadOnlyList<InstalledModel> installed,
    string version,
    string hardwareSignature,
    ApplicationSettings settings
  )
  {
    var exact = installed.FirstOrDefault(
      model => string.Equals(
        model.Name,
        record.Model,
        StringComparison.Ordinal
      )
    );

    if (
      exact is null
      || !string.Equals(
        exact.Digest,
        record.Digest,
        StringComparison.Ordinal
      )
      || !string.Equals(
        version,
        record.OllamaVersion,
        StringComparison.Ordinal
      )
      || !string.Equals(
        hardwareSignature,
        record.HardwareSignature,
        StringComparison.Ordinal
      )
    )
    {
      return true;
    }

    var expected = RuntimeSettingSignature(
      record.RequestedContext,
      record.ActualContext,
      null,
      version
    );
    return !string.Equals(
      expected,
      record.RuntimeSettingSignature,
      StringComparison.Ordinal
    ) || !settings.OllamaRuntime.ContextEscalationLadder.Contains(
      record.RequestedContext
    );
  }

  private static string RuntimeSettingSignature(
    int requestedContext,
    int? actualContext,
    OllamaModelMetadata? metadata,
    string version
  )
  {
    return Hash(
      $"requested={requestedContext};actual={actualContext?.ToString() ?? "unknown"};"
        + $"parallel=unknown;flashAttention=unknown;kvCacheType=unknown;"
        + "quantization=identified-by-model-digest;"
        + $"runner={version}"
    );
  }

  private static string HardwareSignature(
    IReadOnlyList<GpuMemoryStatus> devices,
    SystemMemoryStatus systemMemory
  )
  {
    return Hash(
      string.Join(
        "|",
        devices.OrderBy(
          device => device.Id,
          StringComparer.Ordinal
        ).Select(
          device => $"{device.Id}:{device.Name}:{device.TotalDedicatedMemoryBytes?.ToString() ?? "unknown"}"
        )
      ) + $"|ram:{systemMemory.TotalBytes?.ToString() ?? "unknown"}"
    );
  }

  private static string Hash(
    string value
  )
  {
    return Convert.ToHexString(
      SHA256.HashData(
        Encoding.UTF8.GetBytes(
          value
        )
      )
    ).ToLowerInvariant();
  }

  private static OllamaRunningModel? FindRunning(
    IReadOnlyList<OllamaRunningModel> running,
    string model
  )
  {
    return running.FirstOrDefault(
      candidate => string.Equals(
        candidate.Name,
        model,
        StringComparison.Ordinal
      )
    );
  }

  private static long? EstimatedRam(
    OllamaRunningModel model
  )
  {
    return model.SizeBytes is null || model.VramSizeBytes is null
      ? null
      : Math.Max(
        0,
        model.SizeBytes.Value - model.VramSizeBytes.Value
      );
  }

  private static string Processor(
    OllamaRunningModel model
  )
  {
    var ram = EstimatedRam(
      model
    );
    return model.SizeBytes is null || model.VramSizeBytes is null
      ? "unknown"
      : model.VramSizeBytes == 0
        ? "cpu"
        : ram == 0
          ? "gpu"
          : "hybrid";
  }

  private static string MeasurementUsageRole(
    string role
  )
  {
    return role switch
    {
      OllamaRuntimeRoleIds.ResidentCoordinator => UsageModelRoles.Coordinator,
      OllamaRuntimeRoleIds.Router => UsageModelRoles.Router,
      OllamaRuntimeRoleIds.Specialist => UsageModelRoles.Specialist,
      OllamaRuntimeRoleIds.Fallback => UsageModelRoles.Fallback,
      OllamaRuntimeRoleIds.Benchmark => UsageModelRoles.Benchmark,
      OllamaRuntimeRoleIds.ModelTest => UsageModelRoles.ModelTest,
      OllamaRuntimeRoleIds.WebSearchSynthesis => UsageModelRoles.WebSearchSynthesis,
      OllamaRuntimeRoleIds.VisionRequest => UsageModelRoles.VisionRequest,
      _ => UsageModelRoles.Primary
    };
  }

  private static bool SameRunningModels(
    IReadOnlyList<OllamaRunningModel> left,
    IReadOnlyList<OllamaRunningModel> right
  )
  {
    return left.OrderBy(
      model => model.Name,
      StringComparer.Ordinal
    ).SequenceEqual(
      right.OrderBy(
        model => model.Name,
        StringComparer.Ordinal
      )
    );
  }

  private sealed record ConfiguredModelRole(
    string Model,
    string Role
  );

  private sealed record MeasurementDocument
  {
    public int SchemaVersion { get; init; } = MeasurementSchemaVersion;

    public IReadOnlyList<OllamaRuntimeMeasurementView> Records
    {
      get;
      init;
    } = [];
  }
}
