using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.GitDelivery;

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
  string? PlanStepId = null
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
  string? PlanStepId = null
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
  ValidationRunView? Validation = null
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

public sealed class LocalActionService : ILocalActionService
{
  private const int FileReadLimit = 128 * 1_024;
  private const int FileWriteLimit = 1024 * 1_024;
  private readonly ITrustedWorkspaceService _workspace;
  private readonly IProcessExecutionService _processExecution;
  private readonly IProcessPolicyService _processPolicy;
  private readonly IValidationProfileService _validationProfiles;
  private readonly IGitRepositoryService _git;
  private readonly IGitDeliveryService _gitDelivery;
  private readonly IToolNameResolver _toolNames;

  public LocalActionService(
    ITrustedWorkspaceService workspace,
    IProcessExecutionService processExecution,
    IProcessPolicyService processPolicy,
    IValidationProfileService validationProfiles,
    IGitRepositoryService git,
    IGitDeliveryService gitDelivery,
    IToolNameResolver toolNames
  )
  {
    _workspace = workspace;
    _processExecution = processExecution;
    _processPolicy = processPolicy;
    _validationProfiles = validationProfiles;
    _git = git;
    _gitDelivery = gitDelivery;
    _toolNames = toolNames;
  }

  public async Task<ValidatedLocalAction> ValidateAsync(
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

    if (proposal.Tool == "run_validation_profile")
    {
      if (executionSession is null)
      {
        throw new LocalActionException(
          "validation-profile",
          "Validation requires an active execution session."
        );
      }

      return AttachAndValidatePlanBinding(
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
      return AttachAndValidatePlanBinding(
        await ValidateGitActionAsync(
        proposal,
        executionSession,
        cancellationToken
        ),
        proposal,
        executionSession
      );
    }

    if (proposal.Tool == "delete_files")
    {
      return AttachAndValidatePlanBinding(
        await ValidateDeleteFilesAsync(
          proposal,
          executionSession,
          cancellationToken
        ),
        proposal,
        executionSession
      );
    }

    var validated = proposal.Tool == "run_process"
      ? await ValidateProcessAsync(
        proposal,
        cancellationToken
      )
      : await ValidateFileActionAsync(
        proposal,
        executionSession,
        cancellationToken
      );
    return AttachAndValidatePlanBinding(
      validated,
      proposal,
      executionSession
    );
  }

  private static ValidatedLocalAction AttachAndValidatePlanBinding(
    ValidatedLocalAction action,
    LocalActionProposal proposal,
    ExecutionSession? executionSession
  )
  {
    var validated = action with
    {
      OriginalTool = proposal.OriginalTool ?? proposal.Tool,
      ToolResolutionSource = proposal.ToolResolutionSource,
      PlanStepId = proposal.PlanStepId
    };
    var bindingFailure = executionSession?.ValidatePlanStepBinding(
      proposal.PlanStepId
    );
    if (bindingFailure is not null)
    {
      throw new LocalActionException(
        "plan-action-binding",
        bindingFailure
      );
    }
    return validated;
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
      await ValidatePendingDeleteStatesAsync(
        action,
        executionSession,
        cancellationToken
      );
      var result = action.Tool switch
      {
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
        "delete_files" => await DeleteFilesAsync(
          action,
          executionSession,
          cancellationToken
        ),
        "create_directory" => CreateDirectory(
          action
        ),
        "run_process" => await RunProcessAsync(
          action,
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
        await VerifyAndRecordDeletedFilesAsync(
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
        result.Process is not null
        && executionSession is not null
      )
      {
        executionSession.RecordProcess(
          result.Process
        );
      }

      return result;
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
        result = await _git.GetLogAsync(
          session.WorkspacePath,
          GetOptionalInteger(
            action.Arguments,
            "maxEntries",
            20
          ),
          cancellationToken
        );
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
                  StringComparer.OrdinalIgnoreCase
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
                  StringComparer.OrdinalIgnoreCase
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
      proposal.Tool == "create_file"
      && File.Exists(targetPath)
    )
    {
      var effectivePath = relativePath.Replace(
        Path.DirectorySeparatorChar,
        '/'
      );
      if (
        executionSession is not null
        && executionSession.TryGetObservedFile(
          relativePath,
          out var observed
        )
        && observed is not null
      )
      {
        proposal = proposal with
        {
          Tool = "write_file"
        };
        corrections.Add(
          new LocalActionCorrection(
            "tool",
            "create_file",
            "write_file",
            "The exact plan target already exists and was inspected during this session. The Host changed the repeated creation proposal to a stale-write-protected replacement using the model-supplied content."
          )
        );
      }
      else
      {
        corrections.Add(
          new LocalActionCorrection(
            "tool",
            "create_file",
            "read_file",
            "The exact plan target already exists. The Host changed this proposal to a safe inspection; after receiving its content, use write_file or a bounded edit instead of retrying create_file."
          )
        );
        return new ValidatedLocalAction(
          Guid.NewGuid().ToString("N"),
          "read_file",
          JsonSerializer.SerializeToElement(new
          {
            path = effectivePath
          }),
          targetPath,
          null,
          $"read_file: {relativePath}",
          null,
          true,
          false,
          Corrections: corrections
        );
      }
    }

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

    if (creationResolution?.RebasedToWorkspace == true)
    {
      var effectivePath = relativePath.Replace(
        Path.DirectorySeparatorChar,
        '/'
      );
      var originalPath = creationResolution.OriginalPath ?? string.Empty;
      proposal = proposal with
      {
        Arguments = ReplaceStringArgument(
          proposal.Arguments,
          "path",
          effectivePath
        )
      };
      corrections.Add(
        new LocalActionCorrection(
          "path",
          originalPath,
          effectivePath,
          "The relative traversal would leave the trusted workspace, so the Host rebased the creation inside its root."
        )
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
        StringComparison.OrdinalIgnoreCase
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

  private async Task<ValidatedLocalAction> ValidateDeleteFilesAsync(
    LocalActionProposal proposal,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    if (executionSession is null)
    {
      throw new LocalActionException(
        "action-validation",
        "File deletion requires an active execution session."
      );
    }

    var requestedPaths = GetStringArray(proposal.Arguments, "paths")
      .Select(path => path.Trim())
      .Where(path => path.Length > 0)
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    if (requestedPaths.Length is < 1 or > 50)
    {
      throw new LocalActionException(
        "action-validation",
        "delete_files requires between 1 and 50 explicit unique file paths."
      );
    }

    var prepared = new List<PendingFileChange>(requestedPaths.Length);
    var totalOriginalBytes = 0L;

    foreach (var requestedPath in requestedPaths)
    {
      var target = await _workspace.ResolvePathAsync(requestedPath, cancellationToken);
      var relative = await GetRelativePathAsync(target, cancellationToken);

      if (!File.Exists(target))
      {
        throw new LocalActionException(
          "action-validation",
          $"{relative}: delete_files accepts existing files only; directories and missing paths are rejected."
        );
      }

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
        originalContent = new UTF8Encoding(
          false,
          true
        ).GetString(originalBytes);
        originalBinaryBase64 = null;
      }
      catch (DecoderFallbackException)
      {
        originalContent = null;
        originalBinaryBase64 = Convert.ToBase64String(originalBytes);
      }
      totalOriginalBytes = checked(totalOriginalBytes + info.Length);
      prepared.Add(
        new PendingFileChange(
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
        )
      );
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
        $"delete_files cannot guarantee bounded recovery for this batch: {undoDiagnostic}"
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
      $"delete_files: {prepared.Count} explicit file(s)",
      string.Join("\n", prepared.Select(item => item.RelativePath)),
      false,
      false,
      null,
      PendingFileChanges: prepared
    );
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
    var preview = $"{command.Executable} {string.Join(" ", arguments.Select(QuoteArgument))}";

    return new ValidatedLocalAction(
      Guid.NewGuid().ToString(
        "N"
      ),
      proposal.Tool,
      proposal.Arguments.Clone(),
      command.Executable,
      command.WorkingDirectory,
      $"run_process: {Path.GetFileName(command.Executable)}",
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
        ? "[directory is empty]"
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

    if (file.Length > FileReadLimit)
    {
      throw new LocalActionException(
        "action-execution",
        "The file exceeds the 128 KiB read limit."
      );
    }

    var content = await File.ReadAllTextAsync(
      target,
      cancellationToken
    );
    await RecordObservationAsync(
      target,
      content,
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
        ? $"[no matches; searched {searched} text files]"
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
      "action.edit-applied"
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
      "action.edit-applied"
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
      "action.edit-applied"
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
      "action.edit-applied"
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
      throw new LocalActionException(
        "action-execution",
        "The directory already exists."
      );
    }

    Directory.CreateDirectory(
      target
    );

    return new LocalActionResult(
      $"Created directory {Path.GetFileName(target)}.",
      "action.edit-applied"
    );
  }

  private async Task<LocalActionResult> DeleteFilesAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    var pendingChanges = action.PendingFileChanges
      ?? throw new LocalActionException(
        "action-execution",
        "delete_files did not contain a validated explicit file set."
      );
    var deleted = new List<(string Path, PendingFileChange Change)>();

    try
    {
      foreach (var pending in pendingChanges)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var target = await _workspace.ResolvePathAsync(pending.RelativePath, cancellationToken);
        File.Delete(target);
        deleted.Add((target, pending));
      }
    }
    catch
    {
      foreach (var item in deleted.AsEnumerable().Reverse())
      {
        if (
          (
            item.Change.OriginalContent is null
            && item.Change.OriginalBinaryBase64 is null
          )
          || File.Exists(item.Path)
        )
        {
          continue;
        }

        if (item.Change.OriginalBinaryBase64 is not null)
        {
          await WriteBytesAtomicallyAsync(
            item.Path,
            Convert.FromBase64String(item.Change.OriginalBinaryBase64),
            CancellationToken.None
          );
        }
        else
        {
          await WriteAtomicallyAsync(
            item.Path,
            item.Change.OriginalContent!,
            CancellationToken.None
          );
        }
      }

      throw;
    }

    return new LocalActionResult(
      $"Deleted {deleted.Count} verified file(s): {string.Join(", ", pendingChanges.Select(item => item.RelativePath))}.",
      "action.edit-applied"
    );
  }

  private async Task<LocalActionResult> RunProcessAsync(
    ValidatedLocalAction action,
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

    var result = await _processExecution.ExecuteAsync(
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

    return new LocalActionResult(
      output.ToString().TrimEnd(),
      "action.process-output",
      new ExecutionProcessReview(
        action.TargetPath!,
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
      result.ExitCode == 0
        && !result.TimedOut
        && !result.Cancelled
    );
  }

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

    if (proposal.Tool == "create_file" && existedBefore)
    {
      throw new LocalActionException(
        "action-validation",
        "The file already exists."
      );
    }

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

  private async Task ValidatePendingDeleteStatesAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    foreach (var pending in action.PendingFileChanges ?? [])
    {
      var target = await _workspace.ResolvePathAsync(pending.RelativePath, cancellationToken);

      if (!File.Exists(target))
      {
        throw new LocalActionException(
          "file-conflict",
          $"{pending.RelativePath}: the file no longer exists after delete_files was proposed."
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
        $"{pending.RelativePath}: the file changed after delete_files was proposed."
      );
    }
  }

  private async Task VerifyAndRecordDeletedFilesAsync(
    ValidatedLocalAction action,
    ExecutionSession? executionSession,
    CancellationToken cancellationToken
  )
  {
    foreach (var pending in action.PendingFileChanges ?? [])
    {
      var target = await _workspace.ResolvePathAsync(pending.RelativePath, cancellationToken);

      if (File.Exists(target) || Directory.Exists(target))
      {
        throw new LocalActionException(
          "post-delete-verification",
          $"{pending.RelativePath}: the path still exists after delete_files completed."
        );
      }

      executionSession?.RecordFileChange(
        new ExecutionFileChange(
          pending.RelativePath,
          "deleted",
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
        StringComparer.OrdinalIgnoreCase
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
      StringComparison.OrdinalIgnoreCase
    ) || name.Equals(
      ".editorconfig",
      StringComparison.OrdinalIgnoreCase
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
        StringComparison.OrdinalIgnoreCase
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
        StringComparison.OrdinalIgnoreCase
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
}
