namespace AgenticRouter.Api.Contracts;

public sealed record RuntimeStatusResponse(
  DateTimeOffset CapturedAt,
  SystemMemoryStatus SystemMemory,
  IReadOnlyList<GpuMemoryStatus> Devices,
  string DevicesStatus,
  string? DevicesDiagnostic,
  IReadOnlyList<LoadedModelStatus> LoadedModels,
  string LoadedModelsStatus,
  string? LoadedModelsDiagnostic,
  ResidentModelStatus ResidentModel,
  bool Warning,
  IReadOnlyList<string> Warnings
);

public sealed record SystemMemoryStatus(
  long? TotalBytes,
  long? UsedBytes,
  long? AvailableBytes,
  double? UsedPercent,
  string Status,
  string? Diagnostic
);

public sealed record GpuMemoryStatus(
  string Id,
  string Name,
  long? TotalDedicatedMemoryBytes,
  long? UsedDedicatedMemoryBytes,
  double? UsedPercent,
  string Status,
  string? Diagnostic
);

public sealed record LoadedModelStatus(
  string Name,
  string? Digest,
  string? Role,
  int? RequestedContextTokens,
  int? ActualContextTokens,
  long? TotalSizeBytes,
  long? VramSizeBytes,
  long? EstimatedRamSizeBytes,
  string Processor,
  DateTimeOffset? ExpiresAt,
  bool IsResidentModel,
  string ProfileStatus,
  bool SharedAcrossRoles
);

public sealed record ResidentModelStatus(
  string ConfiguredModel,
  string? Digest,
  string State,
  bool Loaded,
  string Policy,
  int? RequestedContextTokens,
  int? ActualContextTokens,
  long? TotalSizeBytes,
  long? VramSizeBytes,
  long? EstimatedRamSizeBytes,
  string? Operation,
  string? Diagnostic,
  string? TraceId
);
