namespace AgenticRouter.Api.Contracts;

public sealed record RuntimeStatusResponse(
  DateTimeOffset CapturedAt,
  SystemMemoryStatus SystemMemory,
  IReadOnlyList<GpuMemoryStatus> Devices,
  IReadOnlyList<LoadedModelStatus> LoadedModels,
  ResidentModelStatus ResidentModel
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
  long? TotalSizeBytes,
  long? VramSizeBytes,
  long? EstimatedRamSizeBytes,
  string Processor,
  DateTimeOffset? ExpiresAt,
  bool IsResidentModel
);

public sealed record ResidentModelStatus(
  string ConfiguredModel,
  string State,
  bool Loaded,
  string Policy,
  string? Operation,
  string? Diagnostic,
  string? TraceId
);
