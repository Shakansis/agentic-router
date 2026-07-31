using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.Providers;

namespace AgenticRouter.Api.Contracts;

public sealed record InstalledModel(
  string Name,
  long? SizeBytes,
  DateTimeOffset? ModifiedAt,
  string? Digest = null,
  string Provider = ModelProviderIds.OllamaLocal,
  string? DisplayName = null,
  ProviderModelCapabilities? Capabilities = null,
  bool Selectable = true,
  ProviderModelPricing? Pricing = null
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

public sealed record ChatImageAttachment(
  string Id,
  string FileName,
  string MimeType,
  string Base64Data,
  long DeclaredBytes
);

public sealed record ChatRequest(
  string Message,
  string Model,
  IReadOnlyList<ChatMessage>? History,
  bool ModelLocked = false,
  string InteractionMode = "chat",
  string ApprovalPolicy = "ask",
  string? BrowserSessionId = null,
  string? ConversationSessionId = null,
  bool WebSearchEnabled = false,
  IReadOnlyList<ChatImageAttachment>? Images = null
);

public sealed record ModelCapabilityView(
  string Model,
  string Provider,
  string ProviderDisplayName,
  string Role,
  ProviderModelCapabilities Capabilities,
  bool WebAvailable,
  string? WebUnavailableReason
);

public sealed record CloudImageApprovalRequest(
  string BrowserSessionId,
  string Provider
);

public sealed record CloudImageApprovalView(
  string BrowserSessionId,
  string Provider,
  bool Approved
);

public sealed record CloudImageApprovalResetRequest(
  string BrowserSessionId
);

public sealed record WorkspaceProfileView(
  string Id,
  string Name,
  string Path,
  bool Active,
  bool HistoryEnabled,
  DateTimeOffset CreatedAt,
  DateTimeOffset LastOpenedAt,
  ProjectProfile? ProjectProfile,
  string? DefaultModel,
  ValidationProfileSettings? ValidationProfile,
  bool Available,
  string? Diagnostic,
  string? PreferredModelProfileId = null
);

public sealed record WorkspaceProfilesResponse(
  int SchemaVersion,
  IReadOnlyList<WorkspaceProfileView> Profiles,
  string? ActiveWorkspaceId
);

public sealed record CreateWorkspaceProfileRequest(
  string Name,
  string Path
);

public sealed record RenameWorkspaceProfileRequest(
  string Name
);

public sealed record SetWorkspaceHistoryRequest(
  bool Enabled
);

public sealed record WorkspaceHistoryUsage(
  string WorkspaceId,
  bool Enabled,
  int SessionCount,
  long StorageBytes,
  DateTimeOffset? OldestSessionAt,
  DateTimeOffset? NewestSessionAt
);

public sealed record ConversationSessionSummary(
  string Id,
  string WorkspaceId,
  string Title,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt,
  bool Archived,
  string LastInteractionMode,
  bool Interrupted,
  long StorageBytes
);

public sealed record ConversationSessionRecord(
  int SchemaVersion,
  string Id,
  string WorkspaceId,
  string Title,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt,
  bool Archived,
  string State,
  string LastInteractionMode,
  string? SelectedModel,
  IReadOnlyList<ChatMessage> Messages,
  IReadOnlyList<ExecutionSessionReview> ExecutionReviews,
  bool Interrupted,
  bool ContextTruncated,
  bool ArtifactsTruncated,
  long StorageBytes,
  IReadOnlyList<ExecutionSessionPersistenceSnapshot>? ExecutionRollbacks = null
);

public sealed record RenameConversationSessionRequest(
  string Title
);

public sealed record ResumeConversationSessionRequest(
  string BrowserSessionId
);

public sealed record CreateConversationSessionRequest(
  string BrowserSessionId
);

public sealed record SaveConversationSessionRequest(
  string SessionId,
  IReadOnlyList<ChatMessage> Messages,
  string InteractionMode,
  string? SelectedModel,
  string State
);

public sealed record ConversationPersistenceView(
  string SessionId,
  bool HistoryEnabled,
  bool Persisted,
  string Status,
  DateTimeOffset? UpdatedAt
);

public sealed record ConversationSessionListResponse(
  IReadOnlyList<ConversationSessionSummary> Recent,
  IReadOnlyList<ConversationSessionSummary> Archived,
  WorkspaceHistoryUsage Usage
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

public sealed record ProjectRepositoryProfile(
  bool IsGitRepository,
  string? RootRelativePath,
  string? Branch,
  bool HasUncommittedChanges,
  IReadOnlyList<string> DirtyPaths,
  GitRepositoryStatusView? Status = null
);

public sealed record ValidationProfileReference(
  string Name,
  string Source
);

public sealed record ProjectProfile(
  string? WorkspacePath,
  string? DisplayName,
  ProjectRepositoryProfile Repository,
  IReadOnlyList<string> ProjectTypes,
  IReadOnlyList<string> DetectedFiles,
  IReadOnlyList<string> InstructionFiles,
  ValidationProfileReference? ValidationProfile,
  ValidationProfileSettings? DetectedValidationProfile,
  string Status,
  string? Diagnostic,
  bool Truncated
);

public sealed record RepositoryInstructionSet(
  IReadOnlyList<string> AppliedFiles,
  string Content,
  bool Truncated,
  string? Diagnostic
);

public sealed record ExecutionPlanStep(
  string Id,
  string Title,
  string Status
);

public sealed record ExecutionPlanView(
  string Objective,
  IReadOnlyList<ExecutionPlanStep> Steps,
  string? CurrentStepId,
  int CompletedStepCount,
  int RevisionCount
);

public sealed record WorkspaceBaselineView(
  string? Branch,
  IReadOnlyList<string> PreExistingDirtyPaths,
  DateTimeOffset CapturedAt,
  bool GitAvailable,
  string? Diagnostic
);

public sealed record ObservedFileView(
  string RelativePath,
  string Hash,
  long SizeBytes,
  DateTimeOffset LastWriteTimeUtc,
  bool PreExistingChange
);

public sealed record FileConflictView(
  string RelativePath,
  string ExpectedHash,
  string CurrentHash,
  string Stage,
  bool Retryable,
  string? TraceId
);

public sealed record ValidationStepResultView(
  string Id,
  string Label,
  string Executable,
  IReadOnlyList<string> Arguments,
  string WorkingDirectory,
  bool Required,
  DateTimeOffset StartedAt,
  DateTimeOffset EndedAt,
  int? ExitCode,
  long DurationMilliseconds,
  bool TimedOut,
  bool Cancelled,
  bool StandardOutputTruncated,
  bool StandardErrorTruncated,
  string Status,
  string StandardOutput,
  string StandardError
);

public sealed record ValidationRunView(
  string State,
  string? ProfileName,
  DateTimeOffset StartedAt,
  DateTimeOffset EndedAt,
  IReadOnlyList<ValidationStepResultView> Steps,
  IReadOnlyList<ValidationRunView> PriorAttempts
);

public sealed record ValidationProfileState(
  ValidationProfileSettings? Active,
  ValidationProfileSettings? Detected
);

public sealed record RunValidationRequest(
  string BrowserSessionId,
  bool Confirmed
);

public sealed record ApprovalDecisionRequest(
  bool Approved,
  string BrowserSessionId,
  string ExecutionSessionId
);

public sealed record ApprovalDecisionResponse(
  string ActionId,
  bool Accepted,
  bool Approved,
  string? Diagnostic = null
);

public sealed record RecoveryDecisionRequest(
  string Option,
  string BrowserSessionId,
  string ExecutionSessionId
);

public sealed record RecoveryDecisionResponse(
  string CheckpointId,
  bool Accepted,
  string Option,
  string? Diagnostic = null
);

public sealed record RecoveryOptionView(
  string Id,
  string Label,
  string Description
);

public sealed record RecoveryDecisionEvent(
  string CheckpointId,
  string ExecutionSessionId,
  string Reason,
  IReadOnlyList<RecoveryOptionView> Options
);

public sealed record LocalActionEvent(
  string ActionId,
  string Tool,
  string Summary,
  string? Preview,
  string State,
  bool RequiresApproval,
  string? ExecutionSessionId = null,
  bool Undoable = false,
  string? UndoWarning = null
);

public sealed record ExecutionSessionSummary(
  string Id,
  string BrowserSessionId,
  string State,
  string CoordinatorModel,
  string ExecutionPath,
  int PlanningFailureCount,
  int ConsecutiveToolFailureCount,
  int HandoffCount,
  int ActionCount,
  int ChangedFileCount,
  long ElapsedMilliseconds,
  bool ReviewAvailable,
  bool UndoAvailable,
  string? UndoDiagnostic,
  ExecutionPlanView? Plan = null,
  string CompletionStatus = "not-evaluated",
  GitDeliveryStateView? Delivery = null
);

public sealed record ExecutionFileReview(
  string RelativePath,
  string Operation,
  bool ExistedBefore,
  string OriginalHash,
  string FinalHash,
  long FinalSizeBytes,
  bool Verified,
  bool UndoAvailable,
  string? UndoDiagnostic,
  string? UnifiedDiff,
  bool PreExistingChange = false,
  string? CurrentGitStatus = null
);

public sealed record ExecutionProcessReview(
  string Executable,
  IReadOnlyList<string> Arguments,
  string WorkingDirectory,
  int? ExitCode,
  long DurationMilliseconds,
  bool TimedOut,
  bool Cancelled,
  bool StandardOutputTruncated,
  bool StandardErrorTruncated,
  string StandardOutput,
  string StandardError
);

public sealed record ExecutionSessionReview(
  ExecutionSessionSummary Summary,
  string Objective,
  string WorkspacePath,
  IReadOnlyList<ExecutionFileReview> Files,
  IReadOnlyList<ExecutionProcessReview> Processes,
  IReadOnlyList<string> Warnings,
  ProjectProfile? Project = null,
  IReadOnlyList<string>? AppliedInstructionFiles = null,
  WorkspaceBaselineView? Baseline = null,
  IReadOnlyList<FileConflictView>? Conflicts = null,
  ValidationProfileSettings? ValidationProfile = null,
  ValidationRunView? Validation = null,
  GitDeliveryStateView? Delivery = null
);

public sealed record UndoExecutionRequest(
  bool Confirmed,
  string BrowserSessionId
);

public sealed record UndoExecutionResponse(
  bool Succeeded,
  string ExecutionSessionId,
  string Message,
  IReadOnlyList<string> RestoredFiles,
  IReadOnlyList<string> Warnings
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

public sealed record ModelConformanceBenchmarkRequest(
  string Model,
  bool RestoreResidentModel = true,
  bool ExternalProviderPermissionGranted = false
);

public sealed record PortableYamlSettingsRequest(
  string Yaml
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

public sealed record ModelConformanceBenchmarkResult(
  bool Passed,
  string Model,
  string Digest,
  string OllamaVersion,
  long DurationMilliseconds,
  string? Failure
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
  LocalActionEvent? LocalAction = null,
  ExecutionSessionSummary? ExecutionSession = null,
  string? ConversationSessionId = null,
  RecoveryDecisionEvent? RecoveryDecision = null,
  IReadOnlyList<ProviderCitation>? Citations = null
);

public sealed record ValidationErrorsResponse(
  string Message,
  IReadOnlyDictionary<string, string[]> Errors
);
