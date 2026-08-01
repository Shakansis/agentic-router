using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Runtime;

public sealed record OllamaContextResolution(
  string Provider,
  string Model,
  string? Digest,
  string Role,
  int MinimumContextTokens,
  int TargetContextTokens,
  int MaximumContextTokens,
  int EffectiveContextTokens,
  int RequiredContextTokens,
  int OutputTokenLimit,
  int KeepAlive,
  int? DeclaredModelMaximum,
  bool Overridden,
  bool Escalated,
  bool ModelMaximumCapped,
  bool SharedModel,
  string? SharedModelWarning,
  string Reason
);

public sealed record OllamaRuntimeProfileError(
  string Code,
  string Message,
  string Stage,
  string Provider,
  string Model,
  string? Digest,
  string Role,
  int? RequestedContext,
  int? ActualContext,
  bool Retryable,
  string TraceId,
  string Diagnostic,
  int? EstimatedInputTokens = null,
  int? ReservedOutputTokens = null,
  int? RequiredContextTokens = null,
  int? MaximumContextTokens = null,
  int? EffectiveContextTokens = null
);

public sealed class OllamaRuntimeProfileException : Exception
{
  public OllamaRuntimeProfileException(
    string code,
    string message,
    string stage,
    string model,
    string? digest,
    string role,
    int? requestedContext,
    int? actualContext,
    bool retryable,
    string diagnostic,
    Exception? innerException = null,
    int? estimatedInputTokens = null,
    int? reservedOutputTokens = null,
    int? requiredContextTokens = null,
    int? maximumContextTokens = null,
    int? effectiveContextTokens = null
  )
    : base(
      message,
      innerException
    )
  {
    Error = new OllamaRuntimeProfileError(
      code,
      message,
      stage,
      "ollama-local",
      model,
      digest,
      role,
      requestedContext,
      actualContext,
      retryable,
      Guid.NewGuid().ToString(
        "N"
      ),
      Sanitize(
        diagnostic
      ),
      estimatedInputTokens,
      reservedOutputTokens,
      requiredContextTokens,
      maximumContextTokens,
      effectiveContextTokens
    );
  }

  public OllamaRuntimeProfileError Error { get; }

  private static string Sanitize(
    string value
  )
  {
    var sanitized = value
      .Replace(
        "\r",
        " ",
        StringComparison.Ordinal
      )
      .Replace(
        "\n",
        " ",
        StringComparison.Ordinal
      )
      .Trim();
    return sanitized.Length <= 1_000
      ? sanitized
      : sanitized[..1_000];
  }
}

public sealed record OllamaRuntimeProfilesView(
  int ProfileSchemaVersion,
  IReadOnlyDictionary<string, OllamaRoleRuntimeSettings> RoleDefaults,
  IReadOnlyList<OllamaModelRuntimeOverride> ModelOverrides,
  IReadOnlyList<int> ContextEscalationLadder,
  OllamaRuntimeMemoryPolicy MemoryPolicy,
  IReadOnlyList<OllamaRuntimeRecommendation> Recommendations,
  IReadOnlyList<OllamaRuntimeMeasurementView> Measurements,
  IReadOnlyList<OllamaSharedModelWarning> SharedModelWarnings,
  DateTimeOffset GeneratedAt
);

public sealed record OllamaRuntimeRecommendation(
  string Provider,
  string Model,
  string Digest,
  string Role,
  int? DeclaredMaximumContext,
  int ConfiguredContext,
  int? ActualLoadedContext,
  long? CurrentVramBytes,
  long? EstimatedRamBytes,
  string Source,
  int SuggestedMinimum,
  int SuggestedTarget,
  int SuggestedMaximum,
  string Reason,
  string Confidence,
  bool Stale,
  string? Diagnostic
);

public sealed record OllamaSharedModelWarning(
  string Model,
  string Digest,
  IReadOnlyList<string> Roles,
  int LargestConfiguredTarget,
  string Message
);

public sealed record OllamaRuntimeAnalysisRequest(
  string Model,
  string Role
);

public sealed record OllamaRuntimeAnalysisResult(
  OllamaRuntimeRecommendation Recommendation,
  OllamaContextResolution Resolution,
  bool LoadedModelChanged
);

public sealed record OllamaRuntimeMeasurementRequest(
  string Model,
  string Role,
  IReadOnlyList<int> ContextCandidates,
  bool PermissionGranted,
  bool RunMinimalRequest = false
);

public sealed record OllamaRuntimeMeasurementResult(
  OllamaRuntimeMeasurementView Measurement,
  bool PriorResidentRestored,
  bool TargetWasAlreadyLoaded
);

public sealed record OllamaRuntimeMeasurementView(
  int SchemaVersion,
  string Provider,
  string Model,
  string Digest,
  string OllamaVersion,
  string Role,
  int RequestedContext,
  int ActualContext,
  long? TotalLoadedSizeBytes,
  long? VramSizeBytes,
  long? EstimatedRamSizeBytes,
  IReadOnlyList<GpuMemoryStatus> GpuBefore,
  IReadOnlyList<GpuMemoryStatus> GpuAfter,
  SystemMemoryStatus SystemRamBefore,
  SystemMemoryStatus SystemRamAfter,
  long LoadDurationMilliseconds,
  long? MinimalRequestDurationMilliseconds,
  string Processor,
  DateTimeOffset MeasuredAt,
  string HardwareSignature,
  string RuntimeSettingSignature,
  string Status,
  string? Diagnostic,
  bool Stale
);

public interface IOllamaRuntimeProfileService
{
  Task<OllamaRuntimeProfilesView> GetAsync(
    CancellationToken cancellationToken
  );

  Task<OllamaRuntimeAnalysisResult> AnalyzeAsync(
    OllamaRuntimeAnalysisRequest request,
    CancellationToken cancellationToken
  );

  Task<OllamaRuntimeMeasurementResult> MeasureAsync(
    OllamaRuntimeMeasurementRequest request,
    CancellationToken cancellationToken
  );

  Task<IReadOnlyDictionary<string, string[]>> ValidateOverridesAsync(
    ApplicationSettings settings,
    CancellationToken cancellationToken
  );
}
