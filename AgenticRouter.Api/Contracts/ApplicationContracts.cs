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
  bool Recoverable,
  IReadOnlyDictionary<string, string?>? Details = null
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
  IReadOnlyList<ChatMessage>? History,
  bool ModelLocked = false,
  string InteractionMode = "chat",
  string ApprovalPolicy = "ask"
);

public sealed record TrustedWorkspaceRequest(
  string Path
);

public sealed record TrustedWorkspaceStatus(
  bool Configured,
  bool Valid,
  string? Path,
  string Status,
  string? Diagnostic
);

public sealed record FolderPickerResult(
  bool Selected,
  bool Cancelled,
  string? Path,
  string? Error
);

public sealed record ApprovalDecisionRequest(
  bool Approved
);

public sealed record ApprovalDecisionResponse(
  string ActionId,
  bool Accepted,
  bool Approved
);

public sealed record LocalActionEvent(
  string ActionId,
  string Tool,
  string Summary,
  string? Preview,
  string State,
  bool RequiresApproval
);

public sealed record RouterDecision(
  string Intention,
  double? Confidence,
  string? Reason
);

public sealed record ModelDiagnostic(
  string Configuration,
  string ConfiguredValue,
  string? ResolvedModel,
  string Status
);

public sealed record ModelDiagnosticsResponse(
  IReadOnlyList<ModelDiagnostic> Models,
  string ContextDiagnostic
);

public sealed record ModelTestRequest(
  string Model
);

public sealed record ModelTestResult(
  string Model,
  bool Connected,
  long? TimeToFirstChunkMilliseconds,
  long TotalDurationMilliseconds,
  string CompletionStatus,
  string? TraceId,
  string? Error
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
  ProviderError? Error,
  LocalActionEvent? LocalAction = null
);

public sealed record ValidationErrorsResponse(
  string Message,
  IReadOnlyDictionary<string, string[]> Errors
);
