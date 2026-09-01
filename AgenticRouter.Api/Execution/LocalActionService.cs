using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.GitDelivery;
using AgenticRouter.Api.Observability;
using AgenticRouter.Api.Platform;

namespace AgenticRouter.Api.Execution;

public interface ILocalActionService
{
  Task<ValidatedLocalAction> ValidateAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  );

  Task<LocalActionResult> ExecuteAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  );
}

public sealed record LocalActionProposal(
  string Tool,
  JsonElement Arguments,
  string? Explanation,
  string? OriginalTool = null,
  string ToolResolutionSource = ToolNameResolver.CanonicalSource,
  string? PlanStepId = null,
  bool HostInitiated = false
);

public sealed record ValidatedLocalAction(
  string ActionId,
  string Tool,
  JsonElement Arguments,
  string? TargetPath,
  string? WorkingDirectory,
  string Summary,
  string? Preview,
  bool ReadOnly,
  bool RequiresExplicitApproval,
  PendingFileChange? PendingFileChange = null,
  string? OriginalTool = null,
  string ToolResolutionSource = ToolNameResolver.CanonicalSource,
  IReadOnlyList<PendingFileChange>? PendingFileChanges = null,
  IReadOnlyList<LocalActionCorrection>? Corrections = null,
  string? PlanStepId = null,
  string PlanBindingState = HostPlanBindingStates.Unbound,
  string? RequestedPlanStepId = null,
  PendingRenameChange? PendingRename = null
);

public sealed record LocalActionCorrection(
  string Field,
  string OriginalValue,
  string EffectiveValue,
  string Reason
);

public sealed record LocalActionResult(
  string Output,
  string EventType,
  ExecutionProcessReview? Process = null,
  bool Succeeded = true,
  ValidationRunView? Validation = null,
  string? Code = null,
  bool? RetryUnchanged = null,
  string EffectState = HostActionEffectStates.Complete,
  string Outcome = HostActionOutcomes.Succeeded,
  bool? Changed = null,
  bool? PostconditionSatisfied = null,
  IReadOnlyList<HostActionNextAction>? NextActions = null,
  IReadOnlyList<string>? ChangedPaths = null
);

public sealed record PendingFileChange(
  string RelativePath,
  string Operation,
  bool ExistedBefore,
  string OriginalHash,
  string? OriginalContent,
  string FinalContent,
  string ExpectedFinalHash,
  long OriginalBytes,
  bool UndoAvailable,
  string? UndoDiagnostic,
  string? OriginalBinaryBase64 = null
);

public sealed record PendingRenameChange(
  string SourceRelativePath,
  string DestinationRelativePath,
  string SourceHash,
  string ExpectedDestinationHash,
  bool AlreadyApplied,
  long OriginalBytes,
  bool UndoAvailable,
  string? UndoDiagnostic
);

public sealed class LocalActionService : ILocalActionService
{
  private const int FileReadLimit = 128 * 1_024;
  private const int FileWriteLimit = 1024 * 1_024;
  private const int BatchWriteLimit = 5 * 1024 * 1_024;
  private readonly ITrustedWorkspaceService _workspace;
  private readonly IProcessExecutionService _processExecution;
  private readonly IProcessPolicyService _processPolicy;
  private readonly IValidationProfileService _validationProfiles;
  private readonly IGitRepositoryService _git;
  private readonly IGitDeliveryService _gitDelivery;
  private readonly IToolNameResolver _toolNames;
  private readonly IIncidentJournal _incidents;

  public LocalActionService(
    ITrustedWorkspaceService workspace,
    IProcessExecutionService processExecution,
    IProcessPolicyService processPolicy,
    IValidationProfileService validationProfiles,
    IGitRepositoryService git,
    IGitDeliveryService gitDelivery,
    IToolNameResolver toolNames,
    IIncidentJournal incidents
  )
  {
    _workspace = workspace;
    _processExecution = processExecution;
    _processPolicy = processPolicy;
    _validationProfiles = validationProfiles;
    _git = git;
    _gitDelivery = gitDelivery;
    _toolNames = toolNames;
    _incidents = incidents;
  }

  public async Task<ValidatedLocalAction> ValidateAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await ValidateCoreAsync(
        proposal,
        executionSession,
        cancellationToken
      );
    }
    catch (GitDeliveryException exception)
    {
      throw ConvertGitFailure(exception);
    }
  }

  private async Task<ValidatedLocalAction> ValidateCoreAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var resolution = _toolNames.Resolve(
      proposal.Tool,
      _toolNames.ExecutableTools
    );
    proposal = proposal with
    {
      Tool = resolution.CanonicalName,
      OriginalTool = proposal.OriginalTool ?? resolution.OriginalName,
      ToolResolutionSource = proposal.OriginalTool is null
        ? resolution.Source
        : proposal.ToolResolutionSource
    };

    if (proposal.Tool == DiagnosticTraceCapability.ToolName)
    {
      var traceId = DiagnosticTraceCapability.ReadTraceId(
        proposal.Arguments
      );
      if (
        executionSession is not null
        && !executionSession.IsDiagnosticTraceAuthorized(traceId)
      )
      {
        throw new LocalActionException(
          "diagnostic-trace-not-authorized",
          "The requested diagnostic trace does not match the exact trace authorized for this turn."
        );
      }
      return AttachPlanBinding(
        new ValidatedLocalAction(
          Guid.NewGuid().ToString("N"),
          proposal.Tool,
          proposal.Arguments.Clone(),
          null,
          null,
          $"get_trace_diagnostic: {traceId}",
          "Read one exact bounded, sanitized Host diagnostic trace.",
          true,
          false
        ),
        proposal,
        executionSession
      );
    }

    if (proposal.Tool == "run_validation_profile")
    {
      if (executionSession is null)
      {
        throw new LocalActionException(
          "validation-profile",
          "Validation requires an active execution session."
        );
      }

      return AttachPlanBinding(
        new ValidatedLocalAction(
        Guid.NewGuid().ToString(
          "N"
        ),
        proposal.Tool,
        proposal.Arguments.Clone(),
        null,
        null,
        "run_validation_profile",
        "Run the saved structured validation profile.",
        false,
        false
        ),
        proposal,
        executionSession
      );
    }

    if (proposal.Tool.StartsWith(
      "git_",
      StringComparison.Ordinal
    ))
    {
      return AttachPlanBinding(
        await ValidateGitActionAsync(
        proposal,
        executionSession,
        cancellationToken
        ),
        proposal,
        executionSession
      );
    }

    if (proposal.Tool == "delete_paths")
    {
      return AttachPlanBinding(
        await ValidateDeletePathsAsync(
          proposal,
          executionSession,
          cancellationToken
        ),
        proposal,
        executionSession
      );
    }

    if (proposal.Tool == "rename_path")
    {
      return AttachPlanBinding(
        await ValidateRenamePathAsync(proposal, executionSession, cancellationToken),
        proposal,
        executionSession
      );
    }

    if (proposal.Tool == "create_files")
    {
      return AttachPlanBinding(
        await ValidateCreateFilesAsync(
          proposal,
          executionSession,
          cancellationToken
        ),
        proposal,
        executionSession
      );
    }

    var validated = proposal.Tool == "run_process"
      ? executionSession?.ProcessRefreshRequired == true
        ? throw new LocalActionException(
          "process-refresh-required",
          "The previous process left partial or unknown effects. Inspect the relevant workspace state before another process execution."
        )
        : await ValidateProcessAsync(
          proposal,
          cancellationToken
        )
      : await ValidateFileActionAsync(
        proposal,
        executionSession,
        cancellationToken
      );
    return AttachPlanBinding(
      validated,
      proposal,
      executionSession
    );
  }

  private static ValidatedLocalAction AttachPlanBinding(
    ValidatedLocalAction action,
    LocalActionProposal proposal,
    ExecutionSession? executionSession
  )
  {
    var binding = executionSession?.ResolvePlanActionBinding(
      proposal.PlanStepId
    ) ?? new PlanActionBindingResolution(
      HostPlanBindingStates.Unbound,
      proposal.PlanStepId,
      null
    );
    return action with
    {
      OriginalTool = proposal.OriginalTool ?? proposal.Tool,
      ToolResolutionSource = proposal.ToolResolutionSource,
      PlanStepId = binding.EffectiveStepId,
      PlanBindingState = binding.State,
      RequestedPlanStepId = binding.RequestedStepId
    };
  }

  public async Task<LocalActionResult> ExecuteAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await ValidatePendingFileStateAsync(
        action,
        executionSession,
        cancellationToken
      );
      await ValidatePendingBatchStatesAsync(
        action,
        executionSession,
        cancellationToken
      );
      await ValidatePendingRenameStateAsync(action, cancellationToken);
      if (action.Tool == "run_process")
      {
        await RevalidateProcessActionAsync(action, cancellationToken);
      }
      var result = action.Tool switch
      {
        DiagnosticTraceCapability.ToolName => await GetTraceDiagnosticAsync(
          action,
          cancellationToken
        ),
        "list_files" => await ListFilesAsync(
          action,
          cancellationToken
        ),
        "read_file" => await ReadFileAsync(
          action,
          executionSession,
          cancellationToken
        ),
        "get_file_info" => await GetFileInfoAsync(
          action,
          executionSession,
          cancellationToken
        ),
        "search_text" => await SearchTextAsync(
          action,
          executionSession,
          cancellationToken
        ),
        "create_file" => await CreateFileAsync(
          action,
          cancellationToken
        ),
        "create_files" => await CreateFilesAsync(
          action,
          cancellationToken
        ),
        "write_file" => await WriteFileAsync(
          action,
          cancellationToken
        ),
        "replace_text" => await ReplaceTextAsync(
          action,
          cancellationToken
        ),
        "apply_patch" => await ApplyPatchAsync(
          action,
          cancellationToken
        ),
        "delete_paths" => await DeletePathsAsync(
          action,
          executionSession,
          cancellationToken
        ),
        "rename_path" => await RenamePathAsync(
          action,
          executionSession,
          cancellationToken
        ),
        "create_directory" => CreateDirectory(
          action
        ),
        "run_process" => await RunProcessAsync(
          action,
          executionSession,
          cancellationToken
        ),
        "run_validation_profile" => await RunValidationProfileAsync(
          executionSession
            ?? throw new LocalActionException(
              "validation-profile",
              "Validation requires an active execution session."
            ),
          cancellationToken
        ),
        var tool when tool.StartsWith(
          "git_",
          StringComparison.Ordinal
        ) => await RunGitActionAsync(
          action,
          executionSession
            ?? throw new LocalActionException(
              "git-delivery",
              "Git tools require an active execution session."
            ),
          cancellationToken
        ),
        _ => throw new LocalActionException(
          "action-execution",
          $"Tool '{action.Tool}' is not available."
        )
      };

      if (action.PendingFileChanges?.Count > 0)
      {
        await VerifyAndRecordBatchFileChangesAsync(
          action,
          executionSession,
          cancellationToken
        );
      }
      else if (action.PendingFileChange is not null)
      {
        await VerifyAndRecordFileChangeAsync(
          action,
          executionSession,
          cancellationToken
        );
      }
      else if (
        action.Tool == "create_directory"
        && executionSession is not null
        && result.Changed != false
      )
      {
        executionSession.RecordCreatedDirectory(
          await GetRelativePathAsync(
            RequiredTarget(
              action
            ),
            cancellationToken
          )
        );
      }

      if (
        executionSession is not null
        && result.PostconditionSatisfied == true
        && action.PendingFileChange is { } satisfiedFile
      )
      {
        executionSession.RecordVerifiedPostcondition(
          action.Tool,
          action.Arguments,
          satisfiedFile.ExpectedFinalHash
        );
      }
      if (
        executionSession is not null
        && result.PostconditionSatisfied == true
        && action.PendingRename is { } satisfiedRename
      )
      {
        executionSession.RecordVerifiedPostcondition(
          action.Tool,
          action.Arguments,
          satisfiedRename.ExpectedDestinationHash
        );
      }

      if (
        result.Process is not null
        && executionSession is not null
      )
      {
        executionSession.RecordProcess(
          result.Process
        );
      }

      if (
        executionSession is not null
        && action.Tool is "list_files" or "read_file" or "get_file_info" or "search_text" or "git_status" or "git_diff"
      )
      {
        executionSession.RecordWorkspaceRefresh();
      }

      return result;
    }
    catch (GitDeliveryException exception)
    {
      throw ConvertGitFailure(exception);
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
    )
    {
      throw new LocalActionException(
        "action-execution",
        exception.Message,
        exception
      );
    }
  }

  private async Task<LocalActionResult> GetTraceDiagnosticAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var traceId = DiagnosticTraceCapability.ReadTraceId(
      action.Arguments
    );
    var report = await _incidents.FindTraceAsync(
      traceId,
      cancellationToken
    );
    if (report is null)
    {
      throw new LocalActionException(
        "diagnostic-trace-not-found",
        "No retained diagnostic events match this exact trace identifier."
      );
    }

    return new LocalActionResult(
      DiagnosticTraceCapability.Serialize(report),
      "diagnostic.trace-read"
    );
  }

  private static LocalActionException ConvertGitFailure(
    GitDeliveryException exception
  )
  {
    return new LocalActionException(
      exception.Stage,
      exception.Message,
      exception
    );
  }

  private async Task<LocalActionResult> RunValidationProfileAsync(
    ExecutionSession executionSession,
    CancellationToken cancellationToken
  )
  {
    var validation = await _validationProfiles.RunAsync(
      executionSession,
      cancellationToken
    );
    return new LocalActionResult(
      ValidationProfileService.FormatResult(
        validation
      ),
      "action.output",
      null,
      validation.State is "passed" or "passed-with-warnings",
      validation
    );
  }

  private async Task<ValidatedLocalAction> ValidateGitActionAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (executionSession is null)
    {
      throw new LocalActionException(
        "git-delivery",
        "Git tools require an active Execute session."
      );
    }
    _ = await _git.GetStatusAsync(
      executionSession.WorkspacePath,
      false,
      cancellationToken
    );
    var readOnly = proposal.Tool is
      "git_status"
      or "git_diff"
      or "git_log"
      or "git_show_commit";
    var preview = proposal.Tool switch
    {
      "git_diff" or "git_stage_files" or "git_unstage_files" =>
        string.Join(
          "\n",
          GetStringArray(
            proposal.Arguments,
            "paths"
          )
        ),
      "git_create_commit" => GetRequiredString(
        proposal.Arguments,
        "message"
      ),
      "git_create_annotated_tag" =>
        $"{GetRequiredString(proposal.Arguments, "tag")}\n"
          + GetRequiredString(
            proposal.Arguments,
            "annotation"
          ),
      "git_show_commit" => GetRequiredString(
        proposal.Arguments,
        "commit"
      ),
      _ => proposal.Tool
    };
    var action = new ValidatedLocalAction(
      Guid.NewGuid().ToString(
        "N"
      ),
      proposal.Tool,
      proposal.Arguments.Clone(),
      null,
      executionSession.WorkspacePath,
      proposal.Tool,
      LimitPreview(
        preview
      ),
      readOnly,
      !readOnly
    );
    return action;
  }

  private async Task<LocalActionResult> RunGitActionAsync(
    ValidatedLocalAction action,
    ExecutionSession session,
    CancellationToken cancellationToken
  )
  {
    object result;
    switch (action.Tool)
    {
      case "git_status":
        result = await _git.GetStatusAsync(
          session.WorkspacePath,
          false,
          cancellationToken
        );
        break;
      case "git_diff":
        result = await _git.GetDiffAsync(
          session.WorkspacePath,
          GetStringArray(
            action.Arguments,
            "paths"
          ),
          GetOptionalBoolean(
            action.Arguments,
            "staged"
          ),
          cancellationToken
        );
        break;
      case "git_log":
        var status = await _git.GetStatusAsync(
          session.WorkspacePath,
          false,
          cancellationToken
        );
        result = string.IsNullOrWhiteSpace(status.Head)
          ? new
          {
            commits = (IReadOnlyList<GitLogEntryView>)Array.Empty<GitLogEntryView>(),
            repositoryState = "unborn"
          }
          : new
          {
            commits = await _git.GetLogAsync(
              session.WorkspacePath,
              GetOptionalInteger(action.Arguments, "maxEntries", 20),
              cancellationToken
            ),
            repositoryState = "initialized"
          };
        break;
      case "git_show_commit":
        result = await _git.ShowCommitAsync(
          session.WorkspacePath,
          GetRequiredString(
            action.Arguments,
            "commit"
          ),
          cancellationToken
        );
        break;
      case "git_stage_files":
        {
          var delivery = await _gitDelivery.GetAsync(
            session,
            false,
            cancellationToken
          );
          delivery = await _gitDelivery.UpdateAsync(
            session,
            new UpdateDeliveryRequest(
              session.BrowserSessionId,
              GetStringArray(
                action.Arguments,
                "paths"
              ),
              true,
              delivery.CommitMessage,
              delivery.Tag,
              delivery.TagAnnotation,
              false
            ),
            cancellationToken
          );
          result = await _gitDelivery.StageAsync(
            session,
            new GitWriteRequest(
              session.BrowserSessionId,
              delivery.StageActionId,
              true
            ),
            cancellationToken
          );
          break;
        }
      case "git_unstage_files":
        {
          var delivery = await _gitDelivery.GetAsync(
            session,
            false,
            cancellationToken
          );
          delivery = await _gitDelivery.UpdateAsync(
            session,
            new UpdateDeliveryRequest(
              session.BrowserSessionId,
              GetStringArray(
                action.Arguments,
                "paths"
              ),
              true,
              delivery.CommitMessage,
              delivery.Tag,
              delivery.TagAnnotation,
              false
            ),
            cancellationToken
          );
          result = await _gitDelivery.UnstageAsync(
            session,
            new GitWriteRequest(
              session.BrowserSessionId,
              delivery.UnstageActionId,
              true
            ),
            cancellationToken
          );
          break;
        }
      case "git_create_commit":
        {
          var delivery = await _gitDelivery.GetAsync(
            session,
            false,
            cancellationToken
          );
          delivery = await _gitDelivery.UpdateAsync(
            session,
            new UpdateDeliveryRequest(
              session.BrowserSessionId,
              delivery.SelectedFiles,
              delivery.PreExistingFiles.Any(
                path => delivery.SelectedFiles.Contains(
                  path,
                  FileSystemPathSemantics.Comparer
                )
              ),
              GetRequiredString(
                action.Arguments,
                "message"
              ),
              delivery.Tag,
              delivery.TagAnnotation,
              GetOptionalBoolean(
                action.Arguments,
                "commitWithoutValidation"
              )
            ),
            cancellationToken
          );
          result = await _gitDelivery.CommitAsync(
            session,
            new GitCommitRequest(
              session.BrowserSessionId,
              delivery.CommitActionId,
              true,
              GetOptionalBoolean(
                action.Arguments,
                "commitWithoutValidation"
              )
            ),
            cancellationToken
          );
          break;
        }
      case "git_create_annotated_tag":
        {
          var delivery = await _gitDelivery.GetAsync(
            session,
            false,
            cancellationToken
          );
          var tag = GetRequiredString(
            action.Arguments,
            "tag"
          );
          var annotation = GetRequiredString(
            action.Arguments,
            "annotation"
          );
          delivery = await _gitDelivery.UpdateAsync(
            session,
            new UpdateDeliveryRequest(
              session.BrowserSessionId,
              delivery.SelectedFiles,
              delivery.PreExistingFiles.Any(
                path => delivery.SelectedFiles.Contains(
                  path,
                  FileSystemPathSemantics.Comparer
                )
              ),
              delivery.CommitMessage,
              tag,
              annotation,
              false
            ),
            cancellationToken
          );
          result = await _gitDelivery.TagAsync(
            session,
            new GitTagRequest(
              session.BrowserSessionId,
              delivery.TagActionId,
              true,
              tag,
              annotation
            ),
            cancellationToken
          );
          break;
        }
      case "git_push_current_branch":
        {
          var delivery = await _gitDelivery.GetAsync(
            session,
            false,
            cancellationToken
          );
          result = await _gitDelivery.PushBranchAsync(
            session,
            new GitWriteRequest(
              session.BrowserSessionId,
              delivery.PushBranchActionId,
              true
            ),
            cancellationToken
          );
          break;
        }
      case "git_push_tag":
        {
          var delivery = await _gitDelivery.GetAsync(
            session,
            false,
            cancellationToken
          );
          result = await _gitDelivery.PushTagAsync(
            session,
            new GitWriteRequest(
              session.BrowserSessionId,
              delivery.PushTagActionId,
              true
            ),
            cancellationToken
          );
          break;
        }
      default:
        throw new LocalActionException(
          "git-delivery",
          $"Git tool '{action.Tool}' is unavailable."
        );
    }
    return new LocalActionResult(
      JsonSerializer.Serialize(
        result
      ),
      action.ReadOnly
        ? "git-status-refreshed"
        : action.Tool switch
        {
          "git_stage_files" => "git-files-staged",
          "git_unstage_files" => "git-files-unstaged",
          "git_create_commit" => "git-commit-created",
          "git_create_annotated_tag" => "git-tag-created",
          "git_push_current_branch" => "git-branch-pushed",
          "git_push_tag" => "git-tag-pushed",
          _ => "action.output"
        }
    );
  }

  private async Task<ValidatedLocalAction> ValidateFileActionAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var corrections = new List<LocalActionCorrection>();
    var requestedPath = GetOptionalString(
      proposal.Arguments,
      "path"
    );
    if (
      IsPlanTargetBoundFileTool(proposal.Tool)
      && TryInferHostPlanTarget(
        proposal.Tool,
        executionSession,
        out var inferredPath
      )
      && !MatchesHostPlanTarget(requestedPath, inferredPath)
    )
    {
      proposal = proposal with
      {
        Arguments = ReplaceStringArgument(
          proposal.Arguments,
          "path",
          inferredPath
        )
      };
      corrections.Add(
        new LocalActionCorrection(
          "path",
          requestedPath ?? string.Empty,
          inferredPath,
          IsWorkspaceRootPlaceholder(requestedPath)
            ? "The model omitted the file target; the Host used the explicit target bound to the compatible pending plan step."
            : "The proposed file did not match the compatible pending plan step; the Host kept execution on that step's explicit workspace-confined target."
        )
      );
    }

    var path = NormalizeWorkspaceRootAlias(
      proposal.Tool,
      GetOptionalString(
        proposal.Arguments,
        "path"
      ),
      executionSession
    );
    TrustedWorkspacePathResolution? creationResolution = null;
    string targetPath;

    if (proposal.Tool is "create_file" or "create_directory")
    {
      creationResolution = await _workspace.ResolveCreationPathAsync(
        path,
        cancellationToken
      );
      targetPath = creationResolution.FullPath;
    }
    else
    {
      targetPath = await _workspace.ResolvePathAsync(
        path,
        cancellationToken
      );
    }

    var relativePath = creationResolution?.RelativePath
      ?? await GetRelativePathAsync(
        targetPath,
        cancellationToken
      );

    if (
      proposal.Tool is "create_file" or "create_directory"
      && relativePath == "."
    )
    {
      throw new LocalActionException(
        "action-validation",
        $"{proposal.Tool} must name a target inside the trusted workspace."
      );
    }

    ValidateWorkspaceRootAlias(
      proposal,
      relativePath,
      executionSession
    );
    var readOnly = proposal.Tool is
      "list_files"
      or "read_file"
      or "get_file_info"
      or "search_text";
    string? preview = null;

    if (proposal.Tool is "create_file" or "write_file")
    {
      preview = LimitPreview(
        GetRequiredString(
          proposal.Arguments,
          "content"
        )
      );
    }
    else if (proposal.Tool == "replace_text")
    {
      preview = $"Replace:\n{LimitPreview(GetRequiredString(proposal.Arguments, "oldText"))}"
        + $"\n\nWith:\n{LimitPreview(GetRequiredString(proposal.Arguments, "newText"))}";
    }
    else if (proposal.Tool == "apply_patch")
    {
      var replacements = GetReplacements(
        proposal.Arguments
      );
      preview = string.Join(
        "\n\n",
        replacements.Select(
          replacement => $"Replace:\n{LimitPreview(replacement.OldText)}"
            + $"\nWith:\n{LimitPreview(replacement.NewText)}"
        )
      );
    }
    else if (proposal.Tool == "search_text")
    {
      preview = $"Search for: {LimitPreview(GetRequiredString(proposal.Arguments, "query"))}";
    }

    var pendingFileChange = await PrepareFileChangeAsync(
      proposal,
      targetPath,
      relativePath,
      executionSession,
      cancellationToken
    );
    var protectedInstructionFile = IsProtectedInstructionFile(
      relativePath
    );
    var explicitlyRequested = protectedInstructionFile
      && executionSession?.Objective.Contains(
        Path.GetFileName(
          relativePath
        ),
        StringComparison.OrdinalIgnoreCase
      ) == true;

    if (protectedInstructionFile && !explicitlyRequested)
    {
      preview = "Elevated risk: repository instructions or formatting policy were not explicitly requested.\n\n"
        + preview;
    }

    var action = new ValidatedLocalAction(
      Guid.NewGuid().ToString(
        "N"
      ),
      proposal.Tool,
      proposal.Arguments.Clone(),
      targetPath,
      null,
      $"{proposal.Tool}: {relativePath}",
      preview,
      readOnly,
      protectedInstructionFile && !explicitlyRequested,
      pendingFileChange,
      Corrections: corrections.Count == 0
        ? null
        : corrections
    );
    return action;
  }

  private static bool IsWorkspaceRootPlaceholder(string? path)
  {
    return string.IsNullOrWhiteSpace(path)
      || path.Trim() is "." or "./" or ".\\";
  }

  private static bool IsPlanTargetBoundFileTool(string tool)
  {
    return tool is "create_file"
      or "write_file"
      or "replace_text"
      or "apply_patch"
      or "read_file"
      or "get_file_info"
      or "search_text";
  }

  private static bool MatchesHostPlanTarget(
    string? requestedPath,
    string inferredPath
  )
  {
    return !string.IsNullOrWhiteSpace(requestedPath)
      && string.Equals(
        requestedPath.Trim().Replace('\\', '/').TrimStart('/', '.'),
        inferredPath.Replace('\\', '/').TrimStart('/', '.'),
        FileSystemPathSemantics.Comparison
      );
  }

  private static bool TryInferHostPlanTarget(
    string tool,
    ExecutionSession? executionSession,
    out string path
  )
  {
    path = string.Empty;
    var effect = ToolEffectRegistry.ForTool(tool);
    var plan = executionSession?.Plan;
    if (effect is null || plan is null)
    {
      return false;
    }

    string? target = null;
    for (var index = 0; index < plan.Steps.Count; index++)
    {
      var step = plan.Steps[index];
      if (step.Status is not "pending" and not "in-progress")
      {
        continue;
      }

      var expected = ToolEffectRegistry.InferExpectedEffect(step.Title);
      var candidate = ToolEffectRegistry.TryGetHostTarget(step.Title);
      if (
        expected is null
        || candidate is null
        || !ToolEffectRegistry.AreCompatible(expected, effect)
      )
      {
        continue;
      }

      if (
        effect == ToolEffects.Inspected
        && plan.Steps.Take(index).Any(
          earlier => (earlier.Status is "pending" or "in-progress")
            && ToolEffectRegistry.InferExpectedEffect(earlier.Title) is
            {
            } earlierEffect
            && ToolEffectRegistry.IsMutation(earlierEffect)
        )
      )
      {
        continue;
      }

      target = candidate;
      break;
    }

    if (string.IsNullOrWhiteSpace(target))
    {
      return false;
    }

    path = target;
    return true;
  }

  private static JsonElement ReplaceStringArgument(
    JsonElement arguments,
    string propertyName,
    string value
  )
  {
    var objectNode = JsonNode.Parse(
      arguments.GetRawText()
    ) as JsonObject ?? throw new LocalActionException(
      "action-validation",
      "Native tool-call arguments must be a JSON object."
    );
    objectNode[propertyName] = value;
    return JsonSerializer.SerializeToElement(
      objectNode
    );
  }

  private async Task<ValidatedLocalAction> ValidateRenamePathAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (executionSession is null)
    {
      throw new LocalActionException(
        "action-validation",
        "rename_path requires an active execution session."
      );
    }
    var source = await _workspace.ResolveCreationPathAsync(
      GetRequiredString(proposal.Arguments, "sourcePath"),
      cancellationToken
    );
    var destination = await _workspace.ResolveCreationPathAsync(
      GetRequiredString(proposal.Arguments, "destinationPath"),
      cancellationToken
    );
    if (
      source.RelativePath == "."
      || destination.RelativePath == "."
      || string.Equals(source.FullPath, destination.FullPath, FileSystemPathSemantics.Comparison)
    )
    {
      throw new LocalActionException(
        "action-validation",
        "rename_path requires distinct file paths inside the trusted workspace."
      );
    }
    if (Directory.Exists(source.FullPath) || Directory.Exists(destination.FullPath))
    {
      throw new LocalActionException(
        "target-conflict",
        "rename_path supports files only; a directory occupies the source or destination."
      );
    }

    PendingRenameChange pending;
    if (File.Exists(source.FullPath))
    {
      if (File.Exists(destination.FullPath))
      {
        throw new LocalActionException(
          "target-conflict",
          $"{destination.RelativePath}: the rename destination already exists."
        );
      }
      var sourceInfo = new FileInfo(source.FullPath);
      var sourceHash = await HashFileAsync(source.FullPath, cancellationToken);
      var undoAvailable = executionSession.CanTrackRollback(sourceInfo.Length, out var undoDiagnostic);
      pending = new PendingRenameChange(
        source.RelativePath,
        destination.RelativePath,
        sourceHash,
        sourceHash,
        false,
        sourceInfo.Length,
        undoAvailable,
        undoDiagnostic
      );
    }
    else if (File.Exists(destination.FullPath))
    {
      var destinationHash = await HashFileAsync(destination.FullPath, cancellationToken);
      if (!executionSession.IsVerifiedPostcondition(proposal.Tool, proposal.Arguments, destinationHash))
      {
        throw new LocalActionException(
          "target-conflict",
          "The source is absent and the destination exists, but this session has no verified matching rename postcondition."
        );
      }
      pending = new PendingRenameChange(
        source.RelativePath,
        destination.RelativePath,
        destinationHash,
        destinationHash,
        true,
        new FileInfo(destination.FullPath).Length,
        false,
        null
      );
    }
    else
    {
      throw new LocalActionException(
        "action-validation",
        $"{source.RelativePath}: the rename source does not exist."
      );
    }

    var protectedPath = IsProtectedInstructionFile(source.RelativePath)
      || IsProtectedInstructionFile(destination.RelativePath);
    var explicitlyRequested = executionSession.Objective.Contains(
      Path.GetFileName(source.RelativePath),
      StringComparison.OrdinalIgnoreCase
    ) || executionSession.Objective.Contains(
      Path.GetFileName(destination.RelativePath),
      StringComparison.OrdinalIgnoreCase
    );
    return new ValidatedLocalAction(
      Guid.NewGuid().ToString("N"),
      proposal.Tool,
      proposal.Arguments.Clone(),
      source.FullPath,
      destination.FullPath,
      $"rename_path: {source.RelativePath} -> {destination.RelativePath}",
      $"{source.RelativePath} -> {destination.RelativePath}",
      false,
      protectedPath && !explicitlyRequested,
      PendingRename: pending
    );
  }

  private static async Task ValidatePendingRenameStateAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    if (action.PendingRename is not { } pending)
    {
      return;
    }
    var source = RequiredTarget(action);
    var destination = action.WorkingDirectory
      ?? throw new LocalActionException("action-execution", "rename_path omitted its destination.");
    if (pending.AlreadyApplied)
    {
      if (File.Exists(source) || !File.Exists(destination))
      {
        throw new LocalActionException(
          "target-conflict",
          "The already-applied rename postcondition changed before execution."
        );
      }
      var destinationHash = await HashFileAsync(destination, cancellationToken);
      if (!string.Equals(destinationHash, pending.ExpectedDestinationHash, StringComparison.Ordinal))
      {
        throw new LocalActionException(
          "target-conflict",
          "The rename destination no longer has the verified expected hash."
        );
      }
      return;
    }
    if (!File.Exists(source) || File.Exists(destination) || Directory.Exists(destination))
    {
      throw new LocalActionException(
        "target-conflict",
        "The rename source or destination changed after validation."
      );
    }
    var sourceHash = await HashFileAsync(source, cancellationToken);
    if (!string.Equals(sourceHash, pending.SourceHash, StringComparison.Ordinal))
    {
      throw new LocalActionException(
        "target-conflict",
        "The rename source changed after validation."
      );
    }
  }

  private async Task<ValidatedLocalAction> ValidateDeletePathsAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (executionSession is null)
    {
      throw new LocalActionException(
        "action-validation",
        "Path deletion requires an active execution session."
      );
    }

    var requestedPaths = GetStringArray(proposal.Arguments, "paths")
      .Select(path => path.Trim())
      .Where(path => path.Length > 0)
      .Distinct(FileSystemPathSemantics.Comparer)
      .ToArray();

    if (requestedPaths.Length is < 1 or > 50)
    {
      throw new LocalActionException(
        "action-validation",
        "delete_paths requires between 1 and 50 explicit unique paths."
      );
    }

    var recursive = GetOptionalBoolean(proposal.Arguments, "recursive");
    var prepared = new List<PendingFileChange>();
    var explicitTargets = new List<(string FullPath, string RelativePath, bool Directory)>();
    var totalOriginalBytes = 0L;

    foreach (var requestedPath in requestedPaths)
    {
      var resolution = await _workspace.ResolveCreationPathAsync(requestedPath, cancellationToken);
      var target = resolution.FullPath;
      var relative = resolution.RelativePath;

      if (relative == ".")
      {
        throw new LocalActionException(
          "action-validation",
          "delete_paths cannot delete the trusted workspace root."
        );
      }

      var isFile = File.Exists(target);
      var isDirectory = Directory.Exists(target);
      if (!isFile && !isDirectory)
      {
        explicitTargets.Add((target, relative, false));
        prepared.Add(
          new PendingFileChange(
            relative,
            "already-absent",
            false,
            HashText(string.Empty),
            null,
            string.Empty,
            HashText(string.Empty),
            0,
            false,
            null
          )
        );
        continue;
      }

      if (explicitTargets.Any(existing => PathsOverlap(existing.FullPath, target)))
      {
        throw new LocalActionException(
          "action-validation",
          $"{relative}: delete_paths does not accept overlapping parent and child targets."
        );
      }
      explicitTargets.Add((target, relative, isDirectory));

      if (isFile)
      {
        var pending = await PrepareDeletedFileAsync(
          target,
          relative,
          executionSession,
          cancellationToken
        );
        prepared.Add(pending);
        totalOriginalBytes = checked(totalOriginalBytes + pending.OriginalBytes);
        continue;
      }

      var entries = EnumerateDeletionTree(target, relative);
      if (!recursive && entries.Count > 0)
      {
        throw new LocalActionException(
          "action-validation",
          $"{relative}: recursive must be true to delete a non-empty directory."
        );
      }

      prepared.Add(
        new PendingFileChange(
          relative,
          "deleted-directory",
          true,
          HashText("directory"),
          null,
          string.Empty,
          HashText(string.Empty),
          0,
          false,
          null
        )
      );
      foreach (var entry in entries.Where(entry => entry.Directory))
      {
        prepared.Add(
          new PendingFileChange(
            entry.RelativePath,
            "deleted-directory",
            true,
            HashText("directory"),
            null,
            string.Empty,
            HashText(string.Empty),
            0,
            false,
            null
          )
        );
      }
      foreach (var entry in entries.Where(entry => !entry.Directory))
      {
        var pending = await PrepareDeletedFileAsync(
          entry.FullPath,
          entry.RelativePath,
          executionSession,
          cancellationToken
        );
        prepared.Add(pending);
        totalOriginalBytes = checked(totalOriginalBytes + pending.OriginalBytes);
      }
    }

    var undoAvailable = executionSession.CanTrackRollbackBatch(
      prepared.Count,
      totalOriginalBytes,
      out var undoDiagnostic
    );
    if (!undoAvailable)
    {
      throw new LocalActionException(
        "action-validation",
        $"delete_paths cannot guarantee bounded recovery for this batch: {undoDiagnostic}"
      );
    }

    prepared = prepared.Select(
      pending => pending with
      {
        UndoAvailable = true,
        UndoDiagnostic = null
      }
    ).ToList();

    return new ValidatedLocalAction(
      Guid.NewGuid().ToString("N"),
      proposal.Tool,
      proposal.Arguments.Clone(),
      null,
      null,
      $"delete_paths: {explicitTargets.Count} explicit path(s)",
      string.Join("\n", explicitTargets.Select(item => item.RelativePath)),
      false,
      false,
      null,
      PendingFileChanges: prepared
    );
  }

  private async Task<ValidatedLocalAction> ValidateCreateFilesAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (executionSession is null)
    {
      throw new LocalActionException(
        "action-validation",
        "Batch file creation requires an active execution session."
      );
    }

    var requestedFiles = GetFileCreations(proposal.Arguments);
    if (requestedFiles.Count is < 1 or > 50)
    {
      throw new LocalActionException(
        "action-validation",
        "create_files requires between 1 and 50 explicit file entries."
      );
    }

    var prepared = new List<PendingFileChange>(requestedFiles.Count);
    var normalized = new List<object>(requestedFiles.Count);
    var corrections = new List<LocalActionCorrection>();
    var canonicalTargets = new HashSet<string>(FileSystemPathSemantics.Comparer);
    var totalBytes = 0;
    var requiresExplicitApproval = false;

    foreach (var requested in requestedFiles)
    {
      var path = requested.Path.Trim();
      if (path.Length == 0)
      {
        throw new LocalActionException(
          "action-validation",
          "create_files entries require a non-empty path."
        );
      }

      ValidateContent(requested.Content);
      totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(requested.Content));
      if (totalBytes > BatchWriteLimit)
      {
        throw new LocalActionException(
          "action-validation",
          "create_files exceeds the 5 MiB total batch write limit."
        );
      }

      var resolution = await _workspace.ResolveCreationPathAsync(path, cancellationToken);
      var target = resolution.FullPath;
      var relative = resolution.RelativePath;
      if (relative == ".")
      {
        throw new LocalActionException(
          "action-validation",
          "create_files entries must name files inside the trusted workspace."
        );
      }
      if (!canonicalTargets.Add(target))
      {
        throw new LocalActionException(
          "action-validation",
          $"{relative}: create_files contains the same effective target more than once."
        );
      }
      if (Directory.Exists(target))
      {
        throw new LocalActionException(
          "target-conflict",
          $"{relative}: an existing directory conflicts with the requested file."
        );
      }

      if (File.Exists(target))
      {
        var existingContent = await File.ReadAllTextAsync(target, cancellationToken);
        if (!string.Equals(existingContent, requested.Content, StringComparison.Ordinal))
        {
          throw new LocalActionException(
            "target-conflict",
            $"{relative}: the file already exists with different content."
          );
        }
        var existingHash = HashText(existingContent);
        var effectiveExistingPath = relative.Replace(Path.DirectorySeparatorChar, '/');
        normalized.Add(new { path = effectiveExistingPath, content = requested.Content });
        prepared.Add(
          new PendingFileChange(
            relative,
            "already-present",
            true,
            existingHash,
            null,
            requested.Content,
            existingHash,
            new FileInfo(target).Length,
            false,
            null
          )
        );
        continue;
      }

      var parent = Path.GetDirectoryName(target);
      while (
        !string.IsNullOrWhiteSpace(parent)
        && !Directory.Exists(parent)
      )
      {
        if (File.Exists(parent))
        {
          throw new LocalActionException(
            "action-validation",
            $"{relative}: a parent path is an existing file."
          );
        }
        parent = Path.GetDirectoryName(parent);
      }

      var effectivePath = relative.Replace(Path.DirectorySeparatorChar, '/');
      var protectedInstructionFile = IsProtectedInstructionFile(relative);
      if (
        protectedInstructionFile
        && !executionSession.Objective.Contains(relative, StringComparison.OrdinalIgnoreCase)
        && !executionSession.Objective.Contains(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase)
      )
      {
        requiresExplicitApproval = true;
      }

      normalized.Add(new { path = effectivePath, content = requested.Content });
      prepared.Add(
        new PendingFileChange(
          relative,
          "created",
          false,
          HashText(string.Empty),
          null,
          requested.Content,
          HashText(requested.Content),
          0,
          false,
          null
        )
      );
    }

    var undoAvailable = executionSession.CanTrackRollbackBatch(
      prepared.Count,
      0,
      out var undoDiagnostic
    );
    prepared = prepared.Select(
      pending => pending with
      {
        UndoAvailable = undoAvailable,
        UndoDiagnostic = undoAvailable ? null : undoDiagnostic
      }
    ).ToList();

    return new ValidatedLocalAction(
      Guid.NewGuid().ToString("N"),
      proposal.Tool,
      JsonSerializer.SerializeToElement(new { files = normalized }),
      null,
      executionSession.WorkspacePath,
      $"create_files: {prepared.Count} new file(s)",
      string.Join("\n", prepared.Select(item => item.RelativePath)),
      false,
      requiresExplicitApproval,
      null,
      PendingFileChanges: prepared,
      Corrections: corrections.Count == 0 ? null : corrections
    );
  }

  private async Task<PendingFileChange> PrepareDeletedFileAsync(
    string target,
    string relative,
    ExecutionSession executionSession,
    CancellationToken cancellationToken
  )
  {
    if (IsProtectedInstructionFile(relative)
      && !executionSession.Objective.Contains(relative, StringComparison.OrdinalIgnoreCase)
      && !executionSession.Objective.Contains(Path.GetFileName(relative), StringComparison.OrdinalIgnoreCase))
    {
      throw new LocalActionException(
        "action-validation",
        $"{relative}: protected repository instructions can be deleted only when that exact file is named in the user objective."
      );
    }

    var info = new FileInfo(target);
    var maximumRecoverableBytes = Math.Min(
      FileWriteLimit,
      executionSession.Limits.MaxRollbackBytesPerFile
    );
    if (info.Length > maximumRecoverableBytes)
    {
      throw new LocalActionException(
        "action-validation",
        $"{relative}: bounded recoverable deletion supports files up to {maximumRecoverableBytes} bytes."
      );
    }

    var currentHash = await HashFileAsync(target, cancellationToken);
    var originalBytes = await File.ReadAllBytesAsync(target, cancellationToken);
    string? originalContent;
    string? originalBinaryBase64;
    try
    {
      originalContent = new UTF8Encoding(false, true).GetString(originalBytes);
      originalBinaryBase64 = null;
    }
    catch (DecoderFallbackException)
    {
      originalContent = null;
      originalBinaryBase64 = Convert.ToBase64String(originalBytes);
    }

    return new PendingFileChange(
      relative,
      "deleted",
      true,
      currentHash,
      originalContent,
      string.Empty,
      HashText(string.Empty),
      info.Length,
      false,
      null,
      originalBinaryBase64
    );
  }

  private static IReadOnlyList<DeletionTreeEntry> EnumerateDeletionTree(
    string directory,
    string relativeDirectory
  )
  {
    var result = new List<DeletionTreeEntry>();
    var pending = new Stack<(string FullPath, string RelativePath)>();
    pending.Push((directory, relativeDirectory));
    while (pending.Count > 0)
    {
      var current = pending.Pop();
      foreach (var entry in Directory.EnumerateFileSystemEntries(current.FullPath))
      {
        var attributes = File.GetAttributes(entry);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
          throw new LocalActionException(
            "path-validation",
            $"{Path.GetRelativePath(directory, entry)}: delete_paths rejects reparse points inside a recursive target."
          );
        }
        var relative = Path.Combine(
          current.RelativePath,
          Path.GetFileName(entry)
        );
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        result.Add(new DeletionTreeEntry(entry, relative, isDirectory));
        if (isDirectory)
        {
          pending.Push((entry, relative));
        }
      }
    }
    return result;
  }

  private static bool PathsOverlap(string left, string right)
  {
    var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
    var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
    return string.Equals(normalizedLeft, normalizedRight, FileSystemPathSemantics.Comparison)
      || normalizedLeft.StartsWith(normalizedRight + Path.DirectorySeparatorChar, FileSystemPathSemantics.Comparison)
      || normalizedRight.StartsWith(normalizedLeft + Path.DirectorySeparatorChar, FileSystemPathSemantics.Comparison);
  }

  private static int PathDepth(string path)
  {
    return path.Count(character => character is '/' or '\\');
  }

  private async Task<ValidatedLocalAction> ValidateProcessAsync(
    LocalActionProposal proposal,
    CancellationToken cancellationToken
  )
  {
    var executable = GetRequiredString(
      proposal.Arguments,
      "executable"
    );
    var arguments = GetStringArray(
      proposal.Arguments,
      "arguments"
    );
    var command = await _processPolicy.ValidateAsync(
      executable,
      arguments,
      GetOptionalString(
        proposal.Arguments,
        "workingDirectory"
      ),
      cancellationToken
    );
    var preview = $"{executable} {string.Join(" ", arguments.Select(QuoteArgument))}";

    return new ValidatedLocalAction(
      Guid.NewGuid().ToString(
        "N"
      ),
      proposal.Tool,
      proposal.Arguments.Clone(),
      command.Executable,
      command.WorkingDirectory,
      $"run_process: {Path.GetFileName(executable)}",
      LimitPreview(
        preview
      ),
      false,
      command.RequiresExplicitApproval
    );
  }

  private async Task<LocalActionResult> ListFilesAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );

    if (!Directory.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The directory does not exist."
      );
    }

    var recursive = GetOptionalBoolean(
      action.Arguments,
      "recursive"
    );
    var entries = EnumerateEntries(
      target,
      recursive
    ).Take(
      500
    );
    var root = await _workspace.ResolvePathAsync(
      null,
      cancellationToken
    );
    var output = string.Join(
      "\n",
      entries.Select(
        entry => Directory.Exists(
          entry
        )
          ? $"{Path.GetRelativePath(root, entry)}/"
          : Path.GetRelativePath(
            root,
            entry
          )
      )
    );

    return new LocalActionResult(
      string.IsNullOrEmpty(
        output
      )
        ? JsonSerializer.Serialize(new { entries = Array.Empty<string>() })
        : output,
      "action.output"
    );
  }

  private async Task<LocalActionResult> ReadFileAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var file = new FileInfo(
      target
    );

    if (!file.Exists)
    {
      throw new LocalActionException(
        "action-execution",
        "The file does not exist."
      );
    }

    var requestedOffset = GetOptionalLong(action.Arguments, "offsetBytes", 0);
    var hasRequestedLength = action.Arguments.TryGetProperty("lengthBytes", out _);
    var requestedLength = GetOptionalInteger(action.Arguments, "lengthBytes", FileReadLimit);
    if (requestedOffset < 0 || requestedLength is < 1 or > FileReadLimit)
    {
      throw new LocalActionException(
        "action-validation",
        "read_file range must use offsetBytes >= 0 and lengthBytes between 1 and 131072."
      );
    }

    if (file.Length > FileReadLimit && !hasRequestedLength)
    {
      var preparedLength = (int)Math.Min(FileReadLimit, file.Length);
      return new LocalActionResult(
        JsonSerializer.Serialize(new
        {
          sizeBytes = file.Length,
          maxRangeBytes = FileReadLimit,
          validRange = new { minimumOffsetBytes = 0, maximumOffsetBytes = Math.Max(0, file.Length - 1) }
        }),
        "action.output",
        Succeeded: false,
        Code: "FILE_READ_RANGE_REQUIRED",
        RetryUnchanged: false,
        EffectState: HostActionEffectStates.None,
        Outcome: HostActionOutcomes.Recoverable,
        Changed: false,
        PostconditionSatisfied: false,
        NextActions:
        [
          new HostActionNextAction(
            "read_file",
            JsonSerializer.SerializeToElement(new
            {
              path = await GetRelativePathAsync(target, cancellationToken),
              offsetBytes = 0,
              lengthBytes = preparedLength
            }),
            "Read the first valid bounded range."
          )
        ]
      );
    }

    if (requestedOffset >= file.Length && file.Length > 0)
    {
      throw new LocalActionException(
        "action-validation",
        $"read_file offsetBytes must be lower than the file size ({file.Length})."
      );
    }

    var rangeRead = hasRequestedLength || requestedOffset > 0;
    string content;
    if (rangeRead)
    {
      await using var stream = new FileStream(
        target,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        65_536,
        FileOptions.Asynchronous | FileOptions.SequentialScan
      );
      stream.Seek(requestedOffset, SeekOrigin.Begin);
      var buffer = new byte[Math.Min(requestedLength, (int)Math.Min(int.MaxValue, file.Length - requestedOffset))];
      var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
      content = JsonSerializer.Serialize(new
      {
        offsetBytes = requestedOffset,
        lengthBytes = read,
        sizeBytes = file.Length,
        content = Encoding.UTF8.GetString(buffer, 0, read)
      });
    }
    else
    {
      content = await File.ReadAllTextAsync(target, cancellationToken);
    }
    await RecordObservationAsync(
      target,
      rangeRead ? null : content,
      executionSession,
      cancellationToken
    );
    return new LocalActionResult(
      content,
      "action.output"
    );
  }

  private async Task<LocalActionResult> GetFileInfoAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var root = await _workspace.ResolvePathAsync(
      null,
      cancellationToken
    );

    if (
      !File.Exists(
        target
      )
      && !Directory.Exists(
        target
      )
    )
    {
      throw new LocalActionException(
        "action-execution",
        "The requested path does not exist."
      );
    }

    var attributes = File.GetAttributes(
      target
    );
    var isDirectory = (
      attributes
      & FileAttributes.Directory
    ) != 0;
    var size = isDirectory
      ? null
      : new FileInfo(
        target
      ).Length as long?;
    var modified = isDirectory
      ? Directory.GetLastWriteTimeUtc(
        target
      )
      : File.GetLastWriteTimeUtc(
        target
      );
    var output = JsonSerializer.Serialize(
      new
      {
        path = Path.GetRelativePath(
          root,
          target
        ),
        type = isDirectory
          ? "directory"
          : "file",
        sizeBytes = size,
        lastWriteTimeUtc = modified,
        readOnly = (
          attributes
          & FileAttributes.ReadOnly
        ) != 0,
        reparsePoint = (
          attributes
          & FileAttributes.ReparsePoint
        ) != 0
      },
      new JsonSerializerOptions
      {
        WriteIndented = true
      }
    );

    if (!isDirectory)
    {
      await RecordObservationAsync(
        target,
        null,
        executionSession,
        cancellationToken
      );
    }

    return new LocalActionResult(
      output,
      "action.output"
    );
  }

  private async Task<LocalActionResult> SearchTextAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    const int maximumSearchFileBytes = 1024 * 1024;
    const int excerptLimit = 240;
    var target = RequiredTarget(
      action
    );
    var query = GetRequiredString(
      action.Arguments,
      "query"
    );

    if (string.IsNullOrWhiteSpace(
      query
    ) || query.Length > 512)
    {
      throw new LocalActionException(
        "action-validation",
        "Search query must contain between 1 and 512 characters."
      );
    }

    var maxFiles = executionSession?.Limits.MaxSearchFiles
      ?? 500;
    var maxMatches = executionSession?.Limits.MaxSearchMatches
      ?? 200;
    var root = await _workspace.ResolvePathAsync(
      null,
      cancellationToken
    );
    var files = File.Exists(
      target
    )
      ? new[]
      {
        target
      }.AsEnumerable()
      : EnumerateEntries(
        target,
        true
      ).Where(
        File.Exists
      );
    var output = new StringBuilder();
    var searched = 0;
    var matches = 0;
    var truncated = false;

    foreach (var file in files)
    {
      cancellationToken.ThrowIfCancellationRequested();

      if (searched >= maxFiles)
      {
        truncated = true;
        break;
      }

      var info = new FileInfo(
        file
      );

      if (
        !info.Exists
        || info.Length > maximumSearchFileBytes
        || (
          info.Attributes
          & FileAttributes.ReparsePoint
        ) != 0
        || await IsBinaryAsync(
          file,
          cancellationToken
        )
      )
      {
        continue;
      }

      searched++;
      using var reader = new StreamReader(
        file,
        Encoding.UTF8,
        true
      );
      var lineNumber = 0;

      while (
        await reader.ReadLineAsync(
          cancellationToken
        ) is
        {
        } line
      )
      {
        lineNumber++;

        if (!line.Contains(
          query,
          StringComparison.OrdinalIgnoreCase
        ))
        {
          continue;
        }

        var excerpt = line.Length <= excerptLimit
          ? line
          : string.Concat(
            line.AsSpan(
              0,
              excerptLimit
            ),
            "..."
          );
        output.Append(
          Path.GetRelativePath(
            root,
            file
          )
        ).Append(
          ':'
        ).Append(
          lineNumber
        ).Append(
          ": "
        ).AppendLine(
          excerpt
        );
        matches++;

        if (matches >= maxMatches)
        {
          truncated = true;
          break;
        }
      }

      if (matches >= maxMatches)
      {
        break;
      }
    }

    if (truncated)
    {
      output.AppendLine(
        $"[search truncated: files={searched}/{maxFiles}, matches={matches}/{maxMatches}]"
      );
    }

    return new LocalActionResult(
      matches == 0
        ? JsonSerializer.Serialize(new
        {
          results = Array.Empty<object>(),
          searchedFiles = searched,
          truncated
        })
        : output.ToString().TrimEnd(),
      "action.output"
    );
  }

  private static async Task<LocalActionResult> CreateFileAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var content = GetRequiredString(
      action.Arguments,
      "content"
    );
    ValidateContent(
      content
    );

    if (action.PendingFileChange?.Operation == "already-present")
    {
      var actual = await File.ReadAllTextAsync(target, cancellationToken);
      if (!string.Equals(actual, content, StringComparison.Ordinal))
      {
        throw new LocalActionException(
          "target-conflict",
          "The existing file no longer matches the requested content."
        );
      }
      return NoOpResult(
        "already_present",
        $"{Path.GetFileName(target)} already contains the requested content."
      );
    }

    if (File.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The file already exists."
      );
    }

    EnsureParentExists(
      target
    );
    await File.WriteAllTextAsync(
      target,
      content,
      new UTF8Encoding(
        false
      ),
      cancellationToken
    );

    return new LocalActionResult(
      $"Created {Path.GetFileName(target)} ({content.Length} characters).",
      "action.edit-applied",
      Code: "create_file_completed",
      Changed: true,
      PostconditionSatisfied: true
    );
  }

  private static async Task<LocalActionResult> CreateFilesAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var pendingChanges = action.PendingFileChanges
      ?? throw new LocalActionException(
        "action-execution",
        "create_files did not contain a validated explicit file set."
      );
    var createdFiles = new List<string>(pendingChanges.Count);
    var createdDirectories = new HashSet<string>(FileSystemPathSemantics.Comparer);

    try
    {
      foreach (var pending in pendingChanges)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(
          Path.Combine(
            action.WorkingDirectory
              ?? throw new LocalActionException(
                "action-execution",
                "create_files is missing its validated workspace root."
              ),
            pending.RelativePath
          )
        );
        if (pending.Operation == "already-present")
        {
          var existingContent = await File.ReadAllTextAsync(target, cancellationToken);
          if (!string.Equals(existingContent, pending.FinalContent, StringComparison.Ordinal))
          {
            throw new LocalActionException(
              "target-conflict",
              $"{pending.RelativePath}: the existing file no longer matches the requested content."
            );
          }
          continue;
        }
        TrackAndCreateParents(target, createdDirectories);
        await using var stream = new FileStream(
          target,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          65_536,
          FileOptions.Asynchronous
        );
        createdFiles.Add(target);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(pending.FinalContent.AsMemory(), cancellationToken);
      }
    }
    catch
    {
      foreach (var path in createdFiles.AsEnumerable().Reverse())
      {
        if (File.Exists(path))
        {
          File.Delete(path);
        }
      }
      foreach (var directory in createdDirectories.OrderByDescending(path => path.Length))
      {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
          Directory.Delete(directory);
        }
      }
      throw;
    }

    return createdFiles.Count == 0
      ? NoOpResult(
        "already_present",
        $"All {pendingChanges.Count} requested file(s) already contain the requested content."
      )
      : new LocalActionResult(
        $"Created {createdFiles.Count} verified file(s): {string.Join(", ", pendingChanges.Where(item => item.Operation == "created").Select(item => item.RelativePath))}.",
        "action.edit-applied",
        Code: "create_files_completed",
        Changed: true,
        PostconditionSatisfied: true
      );
  }

  private static async Task<LocalActionResult> WriteFileAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var content = GetRequiredString(
      action.Arguments,
      "content"
    );
    ValidateContent(
      content
    );

    if (action.PendingFileChange?.Operation == "already-present")
    {
      return NoOpResult(
        "already_present",
        $"{Path.GetFileName(target)} already contains the requested content."
      );
    }

    if (!File.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The file does not exist. Use create_file for a new file."
      );
    }

    await WriteAtomicallyAsync(
      target,
      content,
      cancellationToken
    );

    return new LocalActionResult(
      $"Updated {Path.GetFileName(target)} ({content.Length} characters).",
      "action.edit-applied",
      Code: "write_file_completed",
      Changed: true,
      PostconditionSatisfied: true
    );
  }

  private static async Task<LocalActionResult> ReplaceTextAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var oldText = GetRequiredString(
      action.Arguments,
      "oldText"
    );
    var newText = GetRequiredString(
      action.Arguments,
      "newText"
    );
    var replaceAll = GetOptionalBoolean(
      action.Arguments,
      "replaceAll"
    );
    if (action.PendingFileChange?.Operation == "already-applied")
    {
      return NoOpResult(
        "already_applied",
        $"The requested replacement is already proven in {Path.GetFileName(target)}."
      );
    }
    var content = await ReadEditableFileAsync(
      target,
      cancellationToken
    );
    var first = content.IndexOf(
      oldText,
      StringComparison.Ordinal
    );

    if (first < 0)
    {
      throw new LocalActionException(
        "action-execution",
        "The requested text was not found."
      );
    }

    var updated = replaceAll
      ? content.Replace(
        oldText,
        newText,
        StringComparison.Ordinal
      )
      : string.Concat(
        content.AsSpan(
          0,
          first
        ),
        newText,
        content.AsSpan(
          first + oldText.Length
        )
      );
    ValidateContent(
      updated
    );
    await WriteAtomicallyAsync(
      target,
      updated,
      cancellationToken
    );

    return new LocalActionResult(
      $"Replaced text in {Path.GetFileName(target)}.",
      "action.edit-applied",
      Code: "replace_text_completed",
      Changed: true,
      PostconditionSatisfied: true
    );
  }

  private static async Task<LocalActionResult> ApplyPatchAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var target = RequiredTarget(
      action
    );
    var content = await ReadEditableFileAsync(
      target,
      cancellationToken
    );
    var replacements = GetReplacements(
      action.Arguments
    );

    foreach (var replacement in replacements)
    {
      var index = content.IndexOf(
        replacement.OldText,
        StringComparison.Ordinal
      );

      if (index < 0)
      {
        throw new LocalActionException(
          "action-execution",
          "A patch search block was not found; no changes were written."
        );
      }

      content = string.Concat(
        content.AsSpan(
          0,
          index
        ),
        replacement.NewText,
        content.AsSpan(
          index + replacement.OldText.Length
        )
      );
    }

    ValidateContent(
      content
    );
    await WriteAtomicallyAsync(
      target,
      content,
      cancellationToken
    );

    return new LocalActionResult(
      $"Applied {replacements.Count} patch replacement(s) to {Path.GetFileName(target)}.",
      "action.edit-applied",
      Code: "apply_patch_completed",
      Changed: true,
      PostconditionSatisfied: true
    );
  }

  private static LocalActionResult CreateDirectory(
    ValidatedLocalAction action
  )
  {
    var target = RequiredTarget(
      action
    );

    if (Directory.Exists(
      target
    ))
    {
      return NoOpResult(
        "already_present",
        $"Directory {Path.GetFileName(target)} already exists."
      );
    }

    Directory.CreateDirectory(
      target
    );

    return new LocalActionResult(
      $"Created directory {Path.GetFileName(target)}.",
      "action.edit-applied",
      Code: "create_directory_completed",
      Changed: true,
      PostconditionSatisfied: true
    );
  }

  private static LocalActionResult NoOpResult(string code, string output)
  {
    return new LocalActionResult(
      output,
      "action.output",
      Code: code,
      EffectState: HostActionEffectStates.None,
      Outcome: HostActionOutcomes.NoOp,
      Changed: false,
      PostconditionSatisfied: true
    );
  }

  private static async Task<LocalActionResult> RenamePathAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var pending = action.PendingRename
      ?? throw new LocalActionException("action-execution", "rename_path omitted its validated state.");
    var source = RequiredTarget(action);
    var destination = action.WorkingDirectory
      ?? throw new LocalActionException("action-execution", "rename_path omitted its destination.");
    if (pending.AlreadyApplied)
    {
      return NoOpResult(
        "already_applied",
        $"Rename {pending.SourceRelativePath} -> {pending.DestinationRelativePath} is already verified."
      );
    }
    EnsureParentExists(destination);
    File.Move(source, destination, false);
    if (File.Exists(source) || !File.Exists(destination))
    {
      throw new LocalActionException(
        "post-write-verification",
        "The rename postcondition was not observed."
      );
    }
    var destinationHash = await HashFileAsync(destination, cancellationToken);
    if (!string.Equals(destinationHash, pending.ExpectedDestinationHash, StringComparison.Ordinal))
    {
      throw new LocalActionException(
        "post-write-verification",
        "The rename destination hash does not match the validated source hash."
      );
    }
    executionSession?.RecordFileChange(
      new ExecutionFileChange(
        pending.SourceRelativePath,
        "deleted",
        true,
        pending.SourceHash,
        string.Empty,
        null,
        string.Empty,
        0,
        DateTimeOffset.UtcNow,
        true,
        false,
        "Rename rollback is represented by the verified destination, not an automatic content restore.",
        0
      )
    );
    executionSession?.RecordFileChange(
      new ExecutionFileChange(
        pending.DestinationRelativePath,
        "created",
        false,
        string.Empty,
        destinationHash,
        null,
        string.Empty,
        new FileInfo(destination).Length,
        DateTimeOffset.UtcNow,
        true,
        false,
        "Rename rollback is represented by the verified source, not an automatic content restore.",
        0
      )
    );
    return new LocalActionResult(
      $"Renamed {pending.SourceRelativePath} to {pending.DestinationRelativePath}.",
      "action.edit-applied",
      Code: "rename_completed",
      Changed: true,
      PostconditionSatisfied: true
    );
  }

  private async Task<LocalActionResult> DeletePathsAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var pendingChanges = action.PendingFileChanges
      ?? throw new LocalActionException(
        "action-execution",
        "delete_paths did not contain a validated path snapshot."
      );
    var requestedPaths = GetStringArray(action.Arguments, "paths")
      .Select(path => path.Trim())
      .Where(path => path.Length > 0)
      .Distinct(FileSystemPathSemantics.Comparer)
      .ToArray();
    var recursive = GetOptionalBoolean(action.Arguments, "recursive");

    try
    {
      foreach (var requestedPath in requestedPaths)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var target = (await _workspace.ResolveCreationPathAsync(requestedPath, cancellationToken)).FullPath;
        if (!File.Exists(target) && !Directory.Exists(target))
        {
          continue;
        }
        if (Directory.Exists(target))
        {
          Directory.Delete(target, recursive);
        }
        else
        {
          File.Delete(target);
        }
      }
    }
    catch
    {
      foreach (var directory in pendingChanges
        .Where(change => change.Operation == "deleted-directory")
        .OrderBy(change => PathDepth(change.RelativePath)))
      {
        var path = (await _workspace.ResolveCreationPathAsync(
          directory.RelativePath,
          CancellationToken.None
        )).FullPath;
        Directory.CreateDirectory(path);
      }
      foreach (var change in pendingChanges.Where(change => change.Operation == "deleted"))
      {
        var path = (await _workspace.ResolveCreationPathAsync(
          change.RelativePath,
          CancellationToken.None
        )).FullPath;
        if (File.Exists(path))
        {
          continue;
        }
        if (change.OriginalBinaryBase64 is not null)
        {
          await WriteBytesAtomicallyAsync(
            path,
            Convert.FromBase64String(change.OriginalBinaryBase64),
            CancellationToken.None
          );
        }
        else
        {
          await WriteAtomicallyAsync(
            path,
            change.OriginalContent!,
            CancellationToken.None
          );
        }
      }

      throw;
    }

    var changedCount = pendingChanges.Count(change => change.Operation != "already-absent");
    return changedCount == 0
      ? NoOpResult(
        "already_absent",
        $"All {requestedPaths.Length} requested path(s) are already absent."
      )
      : new LocalActionResult(
        $"Deleted {requestedPaths.Length} verified path(s): {string.Join(", ", requestedPaths)}.",
        "action.edit-applied",
        Code: "delete_completed",
        Changed: true,
        PostconditionSatisfied: true
      );
  }

  private async Task<LocalActionResult> RunProcessAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var arguments = GetStringArray(
      action.Arguments,
      "arguments"
    );
    var timeoutSeconds = GetOptionalInteger(
      action.Arguments,
      "timeoutSeconds",
      30
    );

    if (timeoutSeconds is < 1 or > 120)
    {
      throw new LocalActionException(
        "process-validation",
        "Process timeout must be between 1 and 120 seconds."
      );
    }

    var workspaceRevisionBefore = executionSession?.WorkspaceRevision ?? 0;
    var observationRoot = executionSession?.WorkspacePath ?? action.WorkingDirectory!;
    var before = await CaptureProcessWorkspaceAsync(
      observationRoot,
      cancellationToken
    );
    executionSession?.RecordProcessStarted(action.ActionId);
    var processClock = System.Diagnostics.Stopwatch.StartNew();
    ProcessExecutionResult result;
    try
    {
      result = await _processExecution.ExecuteAsync(
        new ProcessExecutionRequest(
          action.TargetPath!,
          arguments,
          action.WorkingDirectory!,
          TimeSpan.FromSeconds(
            timeoutSeconds
          )
        ),
        cancellationToken
      );
    }
    finally
    {
      executionSession?.RecordProcessFinished(action.ActionId, processClock.ElapsedMilliseconds);
    }
    var changedPaths = await ObserveProcessWorkspaceChangesAsync(
      observationRoot,
      before,
      executionSession,
      cancellationToken
    );
    var output = new StringBuilder();
    output.AppendLine(
      $"Exit code: {result.ExitCode}"
    );

    if (!string.IsNullOrWhiteSpace(
      result.StandardOutput
    ))
    {
      output.AppendLine(
        "stdout:"
      );
      output.AppendLine(
        result.StandardOutput.TrimEnd()
      );
    }

    if (!string.IsNullOrWhiteSpace(
      result.StandardError
    ))
    {
      output.AppendLine(
        "stderr:"
      );
      output.AppendLine(
        result.StandardError.TrimEnd()
      );
    }

    if (result.TimedOut)
    {
      output.AppendLine(
        "Process timed out and was terminated."
      );
    }

    if (changedPaths.Count > 0)
    {
      output.AppendLine("changed paths:");
      foreach (var path in changedPaths)
      {
        output.AppendLine(path);
      }
    }
    output.AppendLine($"Workspace revision: {workspaceRevisionBefore} -> {executionSession?.WorkspaceRevision ?? workspaceRevisionBefore}");

    var succeeded = result.ExitCode == 0 && !result.TimedOut && !result.Cancelled;
    var effectState = succeeded
      ? HostActionEffectStates.Complete
      : changedPaths.Count > 0
        ? HostActionEffectStates.Partial
        : HostActionEffectStates.Unknown;
    if (!succeeded)
    {
      executionSession?.RequireProcessRefresh();
    }

    return new LocalActionResult(
      output.ToString().TrimEnd(),
      "action.process-output",
      new ExecutionProcessReview(
        GetRequiredString(
          action.Arguments,
          "executable"
        ),
        arguments,
        await GetRelativePathAsync(
          action.WorkingDirectory!,
          cancellationToken
        ),
        result.ExitCode,
        result.DurationMilliseconds,
        result.TimedOut,
        result.Cancelled,
        result.StandardOutputTruncated,
        result.StandardErrorTruncated,
        result.StandardOutput,
        result.StandardError
      ),
      succeeded,
      Code: result.Cancelled
        ? HostActionCodes.UserCancelled
        : result.TimedOut
          ? HostActionCodes.ProcessTimeout
          : result.ExitCode == 0
            ? "PROCESS_COMPLETED"
            : "PROCESS_EXIT_NONZERO",
      RetryUnchanged: succeeded ? null : false,
      EffectState: effectState,
      Outcome: result.Cancelled
        ? HostActionOutcomes.Cancelled
        : succeeded
          ? HostActionOutcomes.Succeeded
          : HostActionOutcomes.Recoverable,
      Changed: changedPaths.Count > 0,
      PostconditionSatisfied: succeeded,
      ChangedPaths: changedPaths
    );
  }

  private async Task RevalidateProcessActionAsync(
    ValidatedLocalAction action,
    CancellationToken cancellationToken
  )
  {
    var validated = await _processPolicy.ValidateAsync(
      GetRequiredString(action.Arguments, "executable"),
      GetStringArray(action.Arguments, "arguments"),
      GetOptionalString(action.Arguments, "workingDirectory"),
      cancellationToken
    );
    if (
      !string.Equals(validated.Executable, action.TargetPath, FileSystemPathSemantics.Comparison)
      || !string.Equals(validated.WorkingDirectory, action.WorkingDirectory, FileSystemPathSemantics.Comparison)
      || !validated.Arguments.SequenceEqual(
        GetStringArray(action.Arguments, "arguments"),
        StringComparer.Ordinal
      )
    )
    {
      throw new LocalActionException(
        "approval-state-conflict",
        "The process policy resolution changed while approval was pending. Propose the action again against current Host state."
      );
    }
  }

  private static async Task<Dictionary<string, ProcessFileStamp>> CaptureProcessWorkspaceAsync(
    string root,
    CancellationToken cancellationToken
  )
  {
    const int maximumFiles = 5_000;
    var result = new Dictionary<string, ProcessFileStamp>(FileSystemPathSemantics.Comparer);
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.Count > 0 && result.Count < maximumFiles)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var directory = pending.Pop();
      foreach (var childDirectory in Directory.EnumerateDirectories(directory))
      {
        var info = new DirectoryInfo(childDirectory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Name == ".git")
        {
          continue;
        }
        pending.Push(childDirectory);
      }
      foreach (var file in Directory.EnumerateFiles(directory))
      {
        var info = new FileInfo(file);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
          continue;
        }
        result[Path.GetRelativePath(root, file)] = new ProcessFileStamp(
          info.Length,
          info.LastWriteTimeUtc.Ticks
        );
        if (result.Count >= maximumFiles)
        {
          break;
        }
      }
    }
    await Task.CompletedTask;
    return result;
  }

  private static async Task<IReadOnlyList<string>> ObserveProcessWorkspaceChangesAsync(
    string root,
    IReadOnlyDictionary<string, ProcessFileStamp> before,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var after = await CaptureProcessWorkspaceAsync(root, cancellationToken);
    var changedPaths = before.Keys.Concat(after.Keys)
      .Distinct(FileSystemPathSemantics.Comparer)
      .Where(path =>
      {
        var hadBefore = before.TryGetValue(path, out var previous);
        var hasAfter = after.TryGetValue(path, out var current);
        return hadBefore != hasAfter || previous != current;
      })
      .Order(FileSystemPathSemantics.Comparer)
      .ToArray();

    foreach (var relativePath in changedPaths)
    {
      cancellationToken.ThrowIfCancellationRequested();
      before.TryGetValue(relativePath, out var previous);
      after.TryGetValue(relativePath, out var current);
      var target = Path.GetFullPath(Path.Combine(root, relativePath));
      var finalHash = current is null
        ? string.Empty
        : await HashFileAsync(target, cancellationToken);
      var originalHash = executionSession?.TryGetObservedFile(relativePath, out var observed) == true
        ? observed?.Hash ?? string.Empty
        : string.Empty;
      executionSession?.RecordFileChange(
        new ExecutionFileChange(
          relativePath,
          previous is null ? "created" : current is null ? "deleted" : "modified",
          previous is not null,
          originalHash,
          finalHash,
          null,
          string.Empty,
          current?.Length ?? 0,
          DateTimeOffset.UtcNow,
          true,
          false,
          "Process effects are observed after execution but are not automatically rollbackable.",
          0
        )
      );
    }
    return changedPaths;
  }

  private sealed record ProcessFileStamp(long Length, long LastWriteTicks);

  private static async Task<PendingFileChange?> PrepareFileChangeAsync(
    LocalActionProposal proposal,
    string targetPath,
    string relativePath,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (proposal.Tool is not (
      "create_file"
      or "write_file"
      or "replace_text"
      or "apply_patch"
    ))
    {
      return null;
    }

    var existedBefore = File.Exists(
      targetPath
    );

    if (proposal.Tool != "create_file" && !existedBefore)
    {
      throw new LocalActionException(
        "action-validation",
        "The file does not exist."
      );
    }

    string? originalContent = null;
    long originalBytes = 0;
    var originalHash = HashText(
      string.Empty
    );

    if (proposal.Tool == "create_file" && existedBefore)
    {
      var expectedContent = GetRequiredString(proposal.Arguments, "content");
      ValidateContent(expectedContent);
      var actualContent = await File.ReadAllTextAsync(targetPath, cancellationToken);
      if (!string.Equals(actualContent, expectedContent, StringComparison.Ordinal))
      {
        throw new LocalActionException(
          "target-conflict",
          $"{relativePath}: the file already exists with different content."
        );
      }
      var actualHash = HashText(actualContent);
      return new PendingFileChange(
        relativePath,
        "already-present",
        true,
        actualHash,
        null,
        expectedContent,
        actualHash,
        new FileInfo(targetPath).Length,
        false,
        null
      );
    }

    if (existedBefore)
    {
      var info = new FileInfo(
        targetPath
      );
      originalBytes = info.Length;
      originalHash = await HashFileAsync(
        targetPath,
        cancellationToken
      );

      if (
        executionSession is null
        || !executionSession.TryGetObservedFile(
          relativePath,
          out var observed
        )
        || observed is null
      )
      {
        throw new LocalActionException(
          "file-not-inspected",
          $"{relativePath}: the file must be read or inspected during this execution session before it can be modified."
        );
      }

      if (!string.Equals(
        observed.Hash,
        originalHash,
        StringComparison.Ordinal
      ))
      {
        executionSession.RecordConflict(
          new FileConflictView(
            relativePath,
            observed.Hash,
            originalHash,
            "pre-write-validation",
            true,
            null
          )
        );
        throw new LocalActionException(
          "file-conflict",
          $"{relativePath}: the file changed since inspection. Expected hash {observed.Hash}; current hash {originalHash}. Read the file again before retrying."
        );
      }

      if (
        originalBytes
        <= (
          executionSession?.Limits.MaxRollbackBytesPerFile
            ?? FileWriteLimit
        )
      )
      {
        originalContent = await File.ReadAllTextAsync(
          targetPath,
          cancellationToken
        );
      }
    }

    string finalContent;

    if (proposal.Tool is "create_file" or "write_file")
    {
      finalContent = GetRequiredString(
        proposal.Arguments,
        "content"
      );
    }
    else
    {
      if (originalContent is null)
      {
        throw new LocalActionException(
          "action-validation",
          "The file is too large for a bounded text edit."
        );
      }

      finalContent = originalContent;

      if (proposal.Tool == "replace_text")
      {
        var oldText = GetRequiredString(
          proposal.Arguments,
          "oldText"
        );
        var newText = GetRequiredString(
          proposal.Arguments,
          "newText"
        );
        var first = finalContent.IndexOf(
          oldText,
          StringComparison.Ordinal
        );

        if (first < 0)
        {
          var currentHash = HashText(finalContent);
          if (
            executionSession?.IsVerifiedPostcondition(
              proposal.Tool,
              proposal.Arguments,
              currentHash
            ) == true
          )
          {
            return new PendingFileChange(
              relativePath,
              "already-applied",
              true,
              currentHash,
              null,
              finalContent,
              currentHash,
              originalBytes,
              false,
              null
            );
          }
          throw new LocalActionException(
            "action-validation",
            "The requested text was not found."
          );
        }

        finalContent = GetOptionalBoolean(
          proposal.Arguments,
          "replaceAll"
        )
          ? finalContent.Replace(
            oldText,
            newText,
            StringComparison.Ordinal
          )
          : string.Concat(
            finalContent.AsSpan(
              0,
              first
            ),
            newText,
            finalContent.AsSpan(
              first + oldText.Length
            )
          );
      }
      else
      {
        foreach (var replacement in GetReplacements(
          proposal.Arguments
        ))
        {
          var index = finalContent.IndexOf(
            replacement.OldText,
            StringComparison.Ordinal
          );

          if (index < 0)
          {
            throw new LocalActionException(
              "action-validation",
              "A patch search block was not found."
            );
          }

          finalContent = string.Concat(
            finalContent.AsSpan(
              0,
              index
            ),
            replacement.NewText,
            finalContent.AsSpan(
              index + replacement.OldText.Length
            )
          );
        }
      }
    }

    ValidateContent(
      finalContent
    );
    if (
      existedBefore
      && string.Equals(originalHash, HashText(finalContent), StringComparison.Ordinal)
    )
    {
      return new PendingFileChange(
        relativePath,
        proposal.Tool == "replace_text" ? "already-applied" : "already-present",
        true,
        originalHash,
        null,
        finalContent,
        originalHash,
        originalBytes,
        false,
        null
      );
    }
    string? undoDiagnostic = null;
    var undoAvailable = executionSession is not null
      && executionSession.CanTrackRollback(
        originalBytes,
        out undoDiagnostic
      );

    if (executionSession is null)
    {
      undoDiagnostic = "No execution session is associated with this action.";
    }

    return new PendingFileChange(
      relativePath,
      existedBefore
        ? "modified"
        : "created",
      existedBefore,
      originalHash,
      undoAvailable
        ? originalContent
        : null,
      finalContent,
      HashText(
        finalContent
      ),
      originalBytes,
      undoAvailable,
      undoDiagnostic
    );
  }

  private static async Task ValidatePendingFileStateAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var pending = action.PendingFileChange;

    if (pending is null)
    {
      return;
    }

    var target = RequiredTarget(
      action
    );
    var exists = File.Exists(
      target
    );

    if (exists != pending.ExistedBefore)
    {
      throw new LocalActionException(
        "file-conflict",
        "The target file changed after the action was proposed."
      );
    }

    if (exists)
    {
      var currentHash = await HashFileAsync(
        target,
        cancellationToken
      );

      if (!string.Equals(
        currentHash,
        pending.OriginalHash,
        StringComparison.Ordinal
      ))
      {
        executionSession?.RecordConflict(
          new FileConflictView(
            pending.RelativePath,
            pending.OriginalHash,
            currentHash,
            "pre-write-execution",
            true,
            null
          )
        );
        throw new LocalActionException(
          "file-conflict",
          $"{pending.RelativePath}: the file changed after the action was proposed. Expected hash {pending.OriginalHash}; current hash {currentHash}."
        );
      }
    }
  }

  private async Task ValidatePendingBatchStatesAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (action.Tool == "delete_paths")
    {
      var snapshot = action.PendingFileChanges ?? [];
      foreach (var requestedPath in GetStringArray(action.Arguments, "paths"))
      {
        var target = (await _workspace.ResolveCreationPathAsync(requestedPath, cancellationToken)).FullPath;
        if (!Directory.Exists(target))
        {
          continue;
        }
        var relative = await GetRelativePathAsync(target, cancellationToken);
        var expected = snapshot
          .Where(change => !string.Equals(change.RelativePath, relative, FileSystemPathSemantics.Comparison)
            && IsRelativeDescendant(relative, change.RelativePath))
          .Select(change => change.RelativePath)
          .ToHashSet(FileSystemPathSemantics.Comparer);
        var current = EnumerateDeletionTree(target, relative)
          .Select(entry => entry.RelativePath)
          .ToHashSet(FileSystemPathSemantics.Comparer);
        if (!expected.SetEquals(current))
        {
          throw new LocalActionException(
            "file-conflict",
            $"{relative}: directory contents changed after delete_paths was proposed."
          );
        }
      }
    }

    foreach (var pending in action.PendingFileChanges ?? [])
    {
      var target = pending.Operation is "created" or "already-absent"
        ? (await _workspace.ResolveCreationPathAsync(pending.RelativePath, cancellationToken)).FullPath
        : await _workspace.ResolvePathAsync(pending.RelativePath, cancellationToken);

      if (pending.Operation == "already-absent")
      {
        if (File.Exists(target) || Directory.Exists(target))
        {
          throw new LocalActionException(
            "file-conflict",
            $"{pending.RelativePath}: the target appeared after its absence was verified."
          );
        }
        continue;
      }

      if (pending.Operation == "created")
      {
        if (File.Exists(target) || Directory.Exists(target))
        {
          throw new LocalActionException(
            "file-conflict",
            $"{pending.RelativePath}: the target appeared after create_files was proposed."
          );
        }
        continue;
      }

      if (pending.Operation == "already-present")
      {
        if (!File.Exists(target))
        {
          throw new LocalActionException(
            "file-conflict",
            $"{pending.RelativePath}: the previously satisfied file disappeared."
          );
        }
        var satisfiedHash = await HashFileAsync(target, cancellationToken);
        if (!string.Equals(satisfiedHash, pending.ExpectedFinalHash, StringComparison.Ordinal))
        {
          throw new LocalActionException(
            "target-conflict",
            $"{pending.RelativePath}: the existing file no longer satisfies the requested content."
          );
        }
        continue;
      }

      if (pending.Operation == "deleted-directory")
      {
        if (!Directory.Exists(target))
        {
          throw new LocalActionException(
            "file-conflict",
            $"{pending.RelativePath}: the directory no longer exists after delete_paths was proposed."
          );
        }
        continue;
      }

      if (!File.Exists(target))
      {
        throw new LocalActionException(
          "file-conflict",
          $"{pending.RelativePath}: the file no longer exists after delete_paths was proposed."
        );
      }

      var currentHash = await HashFileAsync(target, cancellationToken);
      if (string.Equals(currentHash, pending.OriginalHash, StringComparison.Ordinal))
      {
        continue;
      }

      executionSession?.RecordConflict(
        new FileConflictView(
          pending.RelativePath,
          pending.OriginalHash,
          currentHash,
          "pre-delete-execution",
          true,
          null
        )
      );
      throw new LocalActionException(
        "file-conflict",
        $"{pending.RelativePath}: the file changed after delete_paths was proposed."
      );
    }
  }

  private static bool IsRelativeDescendant(string parent, string candidate)
  {
    var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
    var normalizedCandidate = candidate.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    return normalizedCandidate.StartsWith(prefix, FileSystemPathSemantics.Comparison);
  }

  private async Task VerifyAndRecordBatchFileChangesAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    foreach (var pending in action.PendingFileChanges ?? [])
    {
      var target = pending.Operation is "created" or "already-absent"
        ? (await _workspace.ResolveCreationPathAsync(pending.RelativePath, cancellationToken)).FullPath
        : await _workspace.ResolvePathAsync(pending.RelativePath, cancellationToken);

      if (pending.Operation == "already-absent")
      {
        if (File.Exists(target) || Directory.Exists(target))
        {
          throw new LocalActionException(
            "target-conflict",
            $"{pending.RelativePath}: the target no longer satisfies the already-absent postcondition."
          );
        }
        continue;
      }

      if (pending.Operation == "created")
      {
        if (!File.Exists(target))
        {
          throw new LocalActionException(
            "post-write-verification",
            $"{pending.RelativePath}: the created file could not be found during verification."
          );
        }
        var content = await File.ReadAllTextAsync(target, cancellationToken);
        var hash = HashText(content);
        if (
          !string.Equals(content, pending.FinalContent, StringComparison.Ordinal)
          || !string.Equals(hash, pending.ExpectedFinalHash, StringComparison.Ordinal)
        )
        {
          throw new LocalActionException(
            "post-write-verification",
            $"{pending.RelativePath}: the created file did not match the intended UTF-8 content."
          );
        }
        executionSession?.RecordFileChange(
          new ExecutionFileChange(
            pending.RelativePath,
            "created",
            false,
            pending.OriginalHash,
            pending.ExpectedFinalHash,
            null,
            pending.FinalContent,
            new FileInfo(target).Length,
            DateTimeOffset.UtcNow,
            true,
            pending.UndoAvailable,
            pending.UndoDiagnostic,
            0
          )
        );
        continue;
      }

      if (pending.Operation == "already-present")
      {
        if (!File.Exists(target))
        {
          throw new LocalActionException(
            "post-write-verification",
            $"{pending.RelativePath}: the already-present file disappeared."
          );
        }
        var content = await File.ReadAllTextAsync(target, cancellationToken);
        if (
          !string.Equals(content, pending.FinalContent, StringComparison.Ordinal)
          || !string.Equals(HashText(content), pending.ExpectedFinalHash, StringComparison.Ordinal)
        )
        {
          throw new LocalActionException(
            "target-conflict",
            $"{pending.RelativePath}: the existing file does not satisfy the requested content."
          );
        }
        await RecordObservationAsync(target, content, executionSession, cancellationToken);
        continue;
      }

      if (File.Exists(target) || Directory.Exists(target))
      {
        throw new LocalActionException(
          "post-delete-verification",
          $"{pending.RelativePath}: the path still exists after delete_paths completed."
        );
      }

      executionSession?.RecordFileChange(
        new ExecutionFileChange(
          pending.RelativePath,
          pending.Operation,
          true,
          pending.OriginalHash,
          pending.ExpectedFinalHash,
          pending.OriginalContent,
          string.Empty,
          0,
          DateTimeOffset.UtcNow,
          true,
          pending.UndoAvailable,
          pending.UndoDiagnostic,
          pending.UndoAvailable ? pending.OriginalBytes : 0,
          OriginalBinaryBase64: pending.OriginalBinaryBase64
        )
      );
    }
  }

  private async Task RecordObservationAsync(
    string target,
    string? knownContent,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (executionSession is null)
    {
      return;
    }

    var relative = await GetRelativePathAsync(
      target,
      cancellationToken
    );
    var info = new FileInfo(
      target
    );
    var hash = knownContent is null
      ? await HashFileAsync(
        target,
        cancellationToken
      )
      : HashText(
        knownContent
      );
    var preExisting = executionSession.CreateReview().Baseline
      ?.PreExistingDirtyPaths.Contains(
        relative,
        FileSystemPathSemantics.Comparer
      ) == true;
    executionSession.RecordObservedFile(
      new ObservedFileView(
        relative,
        hash,
        info.Length,
        info.LastWriteTimeUtc,
        preExisting
      )
    );
  }

  private async Task VerifyAndRecordFileChangeAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var pending = action.PendingFileChange!;
    var target = RequiredTarget(
      action
    );

    if (pending.Operation is "already-present" or "already-applied")
    {
      if (!File.Exists(target))
      {
        throw new LocalActionException(
          "post-write-verification",
          "The file disappeared before its convergent postcondition was verified."
        );
      }
      var satisfiedContent = await File.ReadAllTextAsync(target, cancellationToken);
      if (
        !string.Equals(HashText(satisfiedContent), pending.ExpectedFinalHash, StringComparison.Ordinal)
        || !string.Equals(satisfiedContent, pending.FinalContent, StringComparison.Ordinal)
      )
      {
        throw new LocalActionException(
          "target-conflict",
          "The file no longer satisfies the requested postcondition."
        );
      }
      await RecordObservationAsync(target, satisfiedContent, executionSession, cancellationToken);
      return;
    }

    if (!File.Exists(
      target
    ))
    {
      throw new LocalActionException(
        "post-write-verification",
        "The written file could not be found during verification."
      );
    }

    var content = await File.ReadAllTextAsync(
      target,
      cancellationToken
    );
    var hash = HashText(
      content
    );

    if (
      !string.Equals(
        hash,
        pending.ExpectedFinalHash,
        StringComparison.Ordinal
      )
      || !string.Equals(
        content,
        pending.FinalContent,
        StringComparison.Ordinal
      )
    )
    {
      throw new LocalActionException(
        "post-write-verification",
        "The file read-back did not match the intended content."
      );
    }

    executionSession?.RecordFileChange(
      new ExecutionFileChange(
        pending.RelativePath,
        pending.Operation,
        pending.ExistedBefore,
        pending.OriginalHash,
        hash,
        pending.OriginalContent,
        pending.FinalContent,
        Encoding.UTF8.GetByteCount(
          content
        ),
        DateTimeOffset.UtcNow,
        true,
        pending.UndoAvailable,
        pending.UndoDiagnostic,
        pending.UndoAvailable
          ? pending.OriginalBytes
          : 0
      )
    );
    await RecordObservationAsync(
      target,
      content,
      executionSession,
      cancellationToken
    );
  }

  private static async Task<bool> IsBinaryAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    var buffer = new byte[4_096];
    await using var stream = new FileStream(
      path,
      FileMode.Open,
      FileAccess.Read,
      FileShare.ReadWrite,
      buffer.Length,
      FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    var read = await stream.ReadAsync(
      buffer,
      cancellationToken
    );

    for (var index = 0; index < read; index++)
    {
      if (buffer[index] == 0)
      {
        return true;
      }
    }

    return false;
  }

  private static async Task<string> HashFileAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    await using var stream = new FileStream(
      path,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      65_536,
      FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    var hash = await SHA256.HashDataAsync(
      stream,
      cancellationToken
    );
    return Convert.ToHexString(
      hash
    ).ToLowerInvariant();
  }

  private static string HashText(
    string content
  )
  {
    return Convert.ToHexString(
      SHA256.HashData(
        Encoding.UTF8.GetBytes(
          content
        )
      )
    ).ToLowerInvariant();
  }

  private static bool IsProtectedInstructionFile(
    string relativePath
  )
  {
    var name = Path.GetFileName(
      relativePath
    );
    return name.Equals(
      "AGENTS.md",
      FileSystemPathSemantics.Comparison
    ) || name.Equals(
      ".editorconfig",
      FileSystemPathSemantics.Comparison
    );
  }

  private static void ValidateWorkspaceRootAlias(
    LocalActionProposal proposal,
    string relativePath,
    ExecutionSession? executionSession
  )
  {
    if (
      proposal.Tool != "create_file"
      || executionSession is null
    )
    {
      return;
    }

    var segments = relativePath.Split(
      [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      ],
      StringSplitOptions.RemoveEmptyEntries
    );
    var workspaceName = Path.GetFileName(
      Path.TrimEndingDirectorySeparator(
        executionSession.WorkspacePath
      )
    );

    if (
      segments.Length < 2
      || !segments[0].Equals(
        workspaceName,
        FileSystemPathSemantics.Comparison
      )
    )
    {
      return;
    }

    var unprefixedPath = Path.Combine(
      executionSession.WorkspacePath,
      Path.Combine(
        segments[1..]
      )
    );

    if (!File.Exists(
      unprefixedPath
    ))
    {
      return;
    }

    var unprefixedRelativePath = Path.GetRelativePath(
      executionSession.WorkspacePath,
      unprefixedPath
    );
    throw new LocalActionException(
      "action-validation",
      $"The trusted workspace is already the project root. '{relativePath}' repeats "
        + $"the workspace directory name while existing file '{unprefixedRelativePath}' "
        + "is available at the root. Read that existing file before proposing an edit."
    );
  }

  private static string? NormalizeWorkspaceRootAlias(
    string tool,
    string? path,
    ExecutionSession? executionSession
  )
  {
    if (
      executionSession is null
      || string.IsNullOrWhiteSpace(
        path
      )
      || Path.IsPathRooted(
        path
      )
      || tool is not (
        "list_files"
        or "read_file"
        or "get_file_info"
        or "search_text"
      )
    )
    {
      return path;
    }

    var segments = path.Split(
      [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      ],
      StringSplitOptions.RemoveEmptyEntries
    );
    var workspaceName = Path.GetFileName(
      Path.TrimEndingDirectorySeparator(
        executionSession.WorkspacePath
      )
    );

    if (
      segments.Length == 0
      || !segments[0].Equals(
        workspaceName,
        FileSystemPathSemantics.Comparison
      )
    )
    {
      return path;
    }

    var prefixedPath = Path.Combine(
      executionSession.WorkspacePath,
      Path.Combine(
        segments
      )
    );
    var unprefixedPath = segments.Length == 1
      ? executionSession.WorkspacePath
      : Path.Combine(
        executionSession.WorkspacePath,
        Path.Combine(
          segments[1..]
        )
      );

    if (
      File.Exists(
        prefixedPath
      )
      || Directory.Exists(
        prefixedPath
      )
      || (
        !File.Exists(
          unprefixedPath
        )
        && !Directory.Exists(
          unprefixedPath
        )
      )
    )
    {
      return path;
    }

    return segments.Length == 1
      ? "."
      : Path.Combine(
        segments[1..]
      );
  }

  private async Task<string> GetRelativePathAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    var root = await _workspace.ResolvePathAsync(
      null,
      cancellationToken
    );

    return Path.GetRelativePath(
      root,
      path
    );
  }

  private static string RequiredTarget(
    ValidatedLocalAction action
  )
  {
    return action.TargetPath ?? throw new LocalActionException(
      "action-validation",
      "The action has no validated target path."
    );
  }

  private static void ValidateContent(
    string content
  )
  {
    if (Encoding.UTF8.GetByteCount(
      content
    ) > FileWriteLimit)
    {
      throw new LocalActionException(
        "action-validation",
        "File content exceeds the 1 MiB write limit."
      );
    }
  }

  private static async Task<string> ReadEditableFileAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    if (!File.Exists(
      path
    ))
    {
      throw new LocalActionException(
        "action-execution",
        "The file does not exist."
      );
    }

    var content = await File.ReadAllTextAsync(
      path,
      cancellationToken
    );
    ValidateContent(
      content
    );
    return content;
  }

  private static async Task WriteAtomicallyAsync(
    string path,
    string content,
    CancellationToken cancellationToken
  )
  {
    var temporary = Path.Combine(
      Path.GetDirectoryName(
        path
      )!,
      $".agentic-router-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      await File.WriteAllTextAsync(
        temporary,
        content,
        new UTF8Encoding(
          false
        ),
        cancellationToken
      );
      File.Move(
        temporary,
        path,
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

  private static async Task WriteBytesAtomicallyAsync(
    string path,
    byte[] content,
    CancellationToken cancellationToken
  )
  {
    var temporary = Path.Combine(
      Path.GetDirectoryName(path)!,
      $".agentic-router-{Guid.NewGuid():N}.tmp"
    );

    try
    {
      await File.WriteAllBytesAsync(
        temporary,
        content,
        cancellationToken
      );
      File.Move(
        temporary,
        path,
        true
      );
    }
    finally
    {
      if (File.Exists(temporary))
      {
        File.Delete(temporary);
      }
    }
  }

  private static void EnsureParentExists(
    string path
  )
  {
    var parent = Path.GetDirectoryName(
      path
    );

    if (
      parent is null
      || !Directory.Exists(
        parent
      )
    )
    {
      throw new LocalActionException(
        "action-execution",
        "The parent directory does not exist."
      );
    }
  }

  private static void TrackAndCreateParents(
    string path,
    ISet<string> createdDirectories
  )
  {
    var missing = new Stack<string>();
    var parent = Path.GetDirectoryName(path);
    while (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
    {
      missing.Push(parent);
      parent = Path.GetDirectoryName(parent);
    }
    while (missing.TryPop(out var directory))
    {
      Directory.CreateDirectory(directory);
      createdDirectories.Add(directory);
    }
  }

  private static IEnumerable<string> EnumerateEntries(
    string root,
    bool recursive
  )
  {
    var pending = new Queue<string>();
    pending.Enqueue(
      root
    );

    while (pending.Count > 0)
    {
      var directory = pending.Dequeue();

      foreach (var entry in Directory.EnumerateFileSystemEntries(
        directory
      ))
      {
        yield return entry;

        if (
          recursive
          && Directory.Exists(
            entry
          )
          && (
            File.GetAttributes(
              entry
            )
            & FileAttributes.ReparsePoint
          ) == 0
        )
        {
          pending.Enqueue(
            entry
          );
        }
      }

      if (!recursive)
      {
        yield break;
      }
    }
  }

  private static string GetRequiredString(
    JsonElement arguments,
    string name
  )
  {
    var value = GetOptionalString(
      arguments,
      name
    );

    if (value is null)
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' is required."
      );
    }

    return value;
  }

  private static string? GetOptionalString(
    JsonElement arguments,
    string name
  )
  {
    if (
      arguments.ValueKind != JsonValueKind.Object
      || !arguments.TryGetProperty(
        name,
        out var value
      )
      || value.ValueKind == JsonValueKind.Null
    )
    {
      return null;
    }

    if (value.ValueKind != JsonValueKind.String)
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be a string."
      );
    }

    return value.GetString();
  }

  private static IReadOnlyList<string> GetStringArray(
    JsonElement arguments,
    string name
  )
  {
    if (
      !arguments.TryGetProperty(
        name,
        out var value
      )
      || value.ValueKind != JsonValueKind.Array
    )
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be an array of strings."
      );
    }

    var result = new List<string>();

    foreach (var item in value.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        throw new LocalActionException(
          "action-validation",
          $"Action argument '{name}' must contain only strings."
        );
      }

      result.Add(
        item.GetString() ?? string.Empty
      );
    }

    return result;
  }

  private static IReadOnlyList<FileCreationInput> GetFileCreations(
    JsonElement arguments
  )
  {
    if (
      arguments.ValueKind != JsonValueKind.Object
      || !arguments.TryGetProperty("files", out var value)
      || value.ValueKind != JsonValueKind.Array
    )
    {
      throw new LocalActionException(
        "action-validation",
        "Action argument 'files' must be an array of { path, content } objects."
      );
    }

    var result = new List<FileCreationInput>();
    foreach (var item in value.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.Object)
      {
        throw new LocalActionException(
          "action-validation",
          "Action argument 'files' must contain only objects."
        );
      }
      result.Add(
        new FileCreationInput(
          GetRequiredString(item, "path"),
          GetRequiredString(item, "content")
        )
      );
    }
    return result;
  }

  private sealed record FileCreationInput(string Path, string Content);

  private static bool GetOptionalBoolean(
    JsonElement arguments,
    string name
  )
  {
    return arguments.TryGetProperty(
      name,
      out var value
    ) && value.ValueKind switch
    {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be a boolean."
      )
    };
  }

  private static int GetOptionalInteger(
    JsonElement arguments,
    string name,
    int defaultValue
  )
  {
    if (!arguments.TryGetProperty(
      name,
      out var value
    ))
    {
      return defaultValue;
    }

    if (
      value.ValueKind != JsonValueKind.Number
      || !value.TryGetInt32(
        out var number
      )
    )
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be an integer."
      );
    }

    return number;
  }

  private static long GetOptionalLong(
    JsonElement arguments,
    string name,
    long defaultValue
  )
  {
    if (!arguments.TryGetProperty(name, out var value))
    {
      return defaultValue;
    }
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
    {
      throw new LocalActionException(
        "action-validation",
        $"Action argument '{name}' must be an integer."
      );
    }
    return number;
  }

  private static IReadOnlyList<PatchReplacement> GetReplacements(
    JsonElement arguments
  )
  {
    if (
      !arguments.TryGetProperty(
        "replacements",
        out var value
      )
      || value.ValueKind != JsonValueKind.Array
    )
    {
      throw new LocalActionException(
        "action-validation",
        "Action argument 'replacements' must be an array."
      );
    }

    var replacements = value.EnumerateArray()
      .Select(
        replacement => new PatchReplacement(
          GetRequiredString(
            replacement,
            "oldText"
          ),
          GetRequiredString(
            replacement,
            "newText"
          )
        )
      )
      .ToArray();

    if (replacements.Length is < 1 or > 20)
    {
      throw new LocalActionException(
        "action-validation",
        "A patch must contain between 1 and 20 replacements."
      );
    }

    return replacements;
  }

  private static string LimitPreview(
    string value
  )
  {
    const int limit = 4_000;

    if (value.Length <= limit)
    {
      return value;
    }

    const string marker = "\n[preview middle omitted]\n";
    var retained = limit - marker.Length;
    var headLength = retained / 2;
    var tailLength = retained - headLength;
    return $"{value[..headLength]}{marker}{value[^tailLength..]}";
  }

  private static string QuoteArgument(
    string argument
  )
  {
    return argument.Any(
      char.IsWhiteSpace
    )
      ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
      : argument;
  }

  private sealed record PatchReplacement(
    string OldText,
    string NewText
  );

  private sealed record DeletionTreeEntry(
    string FullPath,
    string RelativePath,
    bool Directory
  );
}
