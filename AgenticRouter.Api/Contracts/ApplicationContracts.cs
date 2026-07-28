namespace AgenticRouter.Api.Contracts;

public sealed record InstalledModel(
  string Name,
  long? SizeBytes,
  DateTimeOffset? ModifiedAt
);

public sealed record ProviderError(
  string Stage,
  string Message,
  string? TechnicalMessage,
  string TraceId,
  string Provider,
  string? Model,
  string? Intention,
  int? HttpStatus,
  bool Recoverable
);

public sealed record ModelsResponse(
  string Provider,
  bool Available,
  IReadOnlyList<InstalledModel> Models,
  ProviderError? Error
);

public sealed record GraphicsDevice(
  string Id,
  string Name,
  string? Manufacturer,
  long? MemoryBytes,
  bool Available,
  bool IsAuto
);

public sealed record DevicesResponse(
  IReadOnlyList<GraphicsDevice> Devices,
  string? Diagnostic
);

public sealed record ChatMessage(
  string Role,
  string Content
);

public sealed record ChatRequest(
  string Message,
  string Model,
  IReadOnlyList<ChatMessage>? History
);

public sealed record ChatStreamEvent(
  string RequestId,
  string Type,
  DateTimeOffset Timestamp,
  string? Message,
  string? Delta,
  string? SelectedModel,
  string? Intention,
  long? ElapsedMilliseconds,
  string? RenderedHtml,
  ProviderError? Error
);

public sealed record ValidationErrorsResponse(
  string Message,
  IReadOnlyDictionary<string, string[]> Errors
);
