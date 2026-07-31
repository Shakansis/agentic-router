using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.GitDelivery;

public sealed record GitPathStatusView(
  string Path,
  string IndexStatus,
  string WorkingTreeStatus,
  bool Staged,
  bool Unstaged,
  bool Untracked,
  bool Conflicted,
  bool Ignored
);

public sealed record GitRepositoryStatusView(
  bool Available,
  string? Diagnostic,
  string? RepositoryRoot,
  string? Branch,
  string? Head,
  string? Upstream,
  string? UpstreamCommit,
  int Ahead,
  int Behind,
  bool DetachedHead,
  string? OperationInProgress,
  IReadOnlyList<string> StagedPaths,
  IReadOnlyList<string> UnstagedPaths,
  IReadOnlyList<string> UntrackedPaths,
  IReadOnlyList<string> ConflictedPaths,
  IReadOnlyList<string> IgnoredPaths,
  IReadOnlyList<GitPathStatusView> Paths,
  bool Clean,
  bool Truncated,
  DateTimeOffset RefreshedAt
);

public sealed record GitDiffFileView(
  string Path,
  bool Staged,
  bool Binary,
  bool Truncated,
  string Content,
  string ChangeType = "modified"
);

public sealed record GitDiffView(
  IReadOnlyList<GitDiffFileView> Files,
  bool Truncated
);

public sealed record GitLogEntryView(
  string Hash,
  string Subject,
  string Author,
  DateTimeOffset? AuthoredAt
);

public sealed record GitCommitView(
  string Hash,
  string Subject,
  string Body,
  string Author,
  DateTimeOffset? AuthoredAt
);

public sealed record GitIdentityValueView(
  string? Value,
  string Scope
);

public sealed record GitRemoteView(
  string Name,
  string FetchUrl
);

public sealed record GitWorkspaceOverviewView(
  string State,
  string? Diagnostic,
  GitRepositoryStatusView? Repository,
  string? ExecutablePath,
  string? Version,
  GitLogEntryView? LatestCommit,
  GitIdentityValueView UserName,
  GitIdentityValueView UserEmail,
  string? DefaultBranch,
  IReadOnlyList<GitRemoteView> Remotes,
  IReadOnlyList<string> CurrentSessionPaths,
  string InitializeActionId
);

public sealed record GitInitializeRequest(
  string BrowserSessionId,
  string InteractionMode,
  string ActionId,
  bool Confirmed
);

public sealed record GitIdentityRequest(
  string BrowserSessionId,
  string InteractionMode,
  string ActionId,
  bool Confirmed,
  string Field,
  string Value
);

public sealed record GitIdentityPreviewRequest(
  string Field,
  string Value
);

public sealed record GitIdentityApprovalView(
  string Field,
  string Value,
  string ActionId
);

public sealed record GitPanelDiffRequest(
  string View,
  IReadOnlyList<string> Paths
);

public sealed record DeliveryValidationBindingView(
  string ExecutionSessionId,
  string ProfileDigest,
  IReadOnlyDictionary<string, string> FileHashes,
  DateTimeOffset CompletedAt,
  bool Passed,
  bool Stale,
  string? Diagnostic
);

public sealed record GitDeliveryEventView(
  string Type,
  string Message,
  DateTimeOffset Timestamp
);

public sealed record GitDeliveryStateView(
  string State,
  GitRepositoryStatusView Repository,
  IReadOnlyList<string> SessionChangedFiles,
  IReadOnlyList<string> PreExistingFiles,
  IReadOnlyList<string> SelectedFiles,
  IReadOnlyList<string> StagedFiles,
  DeliveryValidationBindingView? ValidationBinding,
  string CommitMessage,
  string? CommitHash,
  string? CommitSubject,
  string? Tag,
  string? TagAnnotation,
  string? TagTarget,
  bool BranchPushed,
  bool TagPushed,
  bool CommitWithoutValidation,
  IReadOnlyList<string> Warnings,
  IReadOnlyList<GitDeliveryEventView> Events,
  string StageActionId,
  string UnstageActionId,
  string CommitActionId,
  string TagActionId,
  string PushBranchActionId,
  string PushTagActionId
);

public sealed record UpdateDeliveryRequest(
  string BrowserSessionId,
  IReadOnlyList<string> SelectedFiles,
  bool IncludePreExistingChanges,
  string CommitMessage,
  string? Tag,
  string? TagAnnotation,
  bool CommitWithoutValidation = false
);

public sealed record GitWriteRequest(
  string BrowserSessionId,
  string ActionId,
  bool Confirmed
);

public sealed record GitCommitRequest(
  string BrowserSessionId,
  string ActionId,
  bool Confirmed,
  bool CommitWithoutValidation
);

public sealed record GitTagRequest(
  string BrowserSessionId,
  string ActionId,
  bool Confirmed,
  string Tag,
  string Annotation
);

public sealed record GitDiffRequest(
  IReadOnlyList<string> Paths,
  bool Staged
);

public sealed class GitDeliveryException : Exception
{
  public GitDeliveryException(
    string code,
    string stage,
    string message,
    bool retryable = false,
    string? diagnostic = null,
    Exception? innerException = null
  )
    : base(
      message,
      innerException
    )
  {
    Code = code;
    Stage = stage;
    Retryable = retryable;
    Diagnostic = diagnostic;
  }

  public string Code { get; }

  public string Stage { get; }

  public bool Retryable { get; }

  public string? Diagnostic { get; }
}

public sealed record GitErrorView(
  string Code,
  string Stage,
  string Message,
  string? Branch,
  int? Ahead,
  int? Behind,
  string? ActionId,
  string? ExecutionSessionId,
  string TraceId,
  bool Retryable,
  string? Diagnostic
);
