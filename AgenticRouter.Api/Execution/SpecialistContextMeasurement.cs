namespace AgenticRouter.Api.Execution;

public sealed record SpecialistContextMeasurement(
  long ConversationTokens,
  long SystemInstructionTokens,
  long CurrentUserMessageTokens,
  long ProjectContextTokens,
  long ToolDiscoveryTokens,
  long GrantedToolSchemaTokens,
  long HostStateTokens,
  long StructuralOverheadTokens,
  long InputTokens,
  int IncludedMessages,
  int OmittedBlocks,
  bool CompactionEligible,
  string Estimator
);
