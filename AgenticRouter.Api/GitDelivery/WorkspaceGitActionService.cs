using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;
using AgenticRouter.Api.WorkspaceProfiles;

namespace AgenticRouter.Api.GitDelivery;

public interface IWorkspaceGitActionService
{
  Task<GitWorkspaceActionView> CommitAsync(
    WorkspaceProfileData workspace,
    IReadOnlyList<string> currentSessionPaths,
    GitWorkspaceCommitRequest request,
    CancellationToken cancellationToken
  );

  Task<GitWorkspaceActionView> PushAsync(
    WorkspaceProfileData workspace,
    IReadOnlyList<string> currentSessionPaths,
    GitWorkspacePushRequest request,
    CancellationToken cancellationToken
  );
}

public sealed class WorkspaceGitActionService : IWorkspaceGitActionService
{
  private readonly IGitRepositoryService _git;
  private readonly ISettingsStore _settings;
  private readonly IOllamaClient _ollama;

  public WorkspaceGitActionService(
    IGitRepositoryService git,
    ISettingsStore settings,
    IOllamaClient ollama
  )
  {
    _git = git;
    _settings = settings;
    _ollama = ollama;
  }

  public async Task<GitWorkspaceActionView> CommitAsync(
    WorkspaceProfileData workspace,
    IReadOnlyList<string> currentSessionPaths,
    GitWorkspaceCommitRequest request,
    CancellationToken cancellationToken
  )
  {
    ValidatePrivilegedRequest(
      request.BrowserSessionId,
      request.InteractionMode,
      request.Confirmed,
      "git-project-commit"
    );
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    ValidateCommitPolicy(
      settings,
      request.CommitWithoutValidation
    );
    var overview = await RequireCurrentOverviewAsync(
      workspace,
      currentSessionPaths,
      request.ActionId,
      true,
      cancellationToken
    );
    var status = overview.Repository!;
    RequireCommittableStatus(
      status
    );
    var paths = status.StagedPaths.Concat(
      status.UnstagedPaths
    ).Concat(
      status.UntrackedPaths
    ).Distinct(
      StringComparer.OrdinalIgnoreCase
    ).Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    var message = string.IsNullOrWhiteSpace(
      request.Message
    )
      ? await GenerateCommitMessageAsync(
        workspace,
        request.Model,
        status,
        cancellationToken
      )
      : RequireExactExplicitMessage(
        request.Message
      );

    overview = await RequireCurrentOverviewAsync(
      workspace,
      currentSessionPaths,
      request.ActionId,
      true,
      cancellationToken
    );
    status = overview.Repository!;
    RequireCommittableStatus(
      status
    );
    await _git.StageAsync(
      workspace.Path,
      paths,
      cancellationToken
    );
    var staged = await _git.GetStatusAsync(
      workspace.Path,
      false,
      cancellationToken
    );
    VerifyExactStagedSet(
      paths,
      staged
    );
    var committed = await _git.CommitAsync(
      workspace.Path,
      message,
      cancellationToken
    );
    var refreshed = await _git.GetWorkspaceOverviewAsync(
      workspace.Id,
      workspace.Path,
      currentSessionPaths,
      cancellationToken
    );
    return new GitWorkspaceActionView(
      refreshed,
      message,
      committed.Hash,
      committed.Subject
    );
  }

  public async Task<GitWorkspaceActionView> PushAsync(
    WorkspaceProfileData workspace,
    IReadOnlyList<string> currentSessionPaths,
    GitWorkspacePushRequest request,
    CancellationToken cancellationToken
  )
  {
    ValidatePrivilegedRequest(
      request.BrowserSessionId,
      request.InteractionMode,
      request.Confirmed,
      "git-project-push"
    );
    var settings = await _settings.GetAsync(
      cancellationToken
    );
    if (!settings.GitDelivery.Enabled)
    {
      throw new GitDeliveryException(
        "git-delivery-disabled",
        "git-project-push",
        "Git delivery is disabled in Settings."
      );
    }
    _ = await RequireCurrentOverviewAsync(
      workspace,
      currentSessionPaths,
      request.ActionId,
      false,
      cancellationToken
    );
    _ = await _git.PushCurrentBranchAsync(
      workspace.Path,
      cancellationToken
    );
    var refreshed = await _git.GetWorkspaceOverviewAsync(
      workspace.Id,
      workspace.Path,
      currentSessionPaths,
      cancellationToken
    );
    return new GitWorkspaceActionView(
      refreshed
    );
  }

  private async Task<GitWorkspaceOverviewView> RequireCurrentOverviewAsync(
    WorkspaceProfileData workspace,
    IReadOnlyList<string> currentSessionPaths,
    string actionId,
    bool commit,
    CancellationToken cancellationToken
  )
  {
    var overview = await _git.GetWorkspaceOverviewAsync(
      workspace.Id,
      workspace.Path,
      currentSessionPaths,
      cancellationToken
    );
    if (
      overview.State != "available"
      || overview.Repository is null
    )
    {
      throw new GitDeliveryException(
        "git-repository-unavailable",
        commit ? "git-project-commit" : "git-project-push",
        "An initialized Git repository is required."
      );
    }
    var expected = commit
      ? overview.CommitActionId
      : overview.PushActionId;
    if (!string.Equals(
      actionId,
      expected,
      StringComparison.Ordinal
    ))
    {
      throw new GitDeliveryException(
        "git-action-stale",
        commit ? "git-project-commit" : "git-project-push",
        "The Git action is stale because the repository state changed. Refresh and try again."
      );
    }
    return overview;
  }

  private async Task<string> GenerateCommitMessageAsync(
    WorkspaceProfileData workspace,
    string? requestedModel,
    GitRepositoryStatusView status,
    CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        requestedModel
      )
      || requestedModel.Contains(
        "::",
        StringComparison.Ordinal
      )
    )
    {
      throw new GitDeliveryException(
        "git-commit-model-invalid",
        "git-commit-message",
        "Select one specific local Ollama model before leaving the commit message empty."
      );
    }
    try
    {
      var settings = await _settings.GetAsync(
        cancellationToken
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );
      var models = await _ollama.GetModelsAsync(
        baseUri,
        cancellationToken
      );
      var model = models.FirstOrDefault(
        candidate => string.Equals(
          candidate.Name,
          requestedModel,
          StringComparison.Ordinal
        )
      ) ?? throw new GitDeliveryException(
        "git-commit-model-unavailable",
        "git-commit-message",
        "The selected local model is not installed or no longer available."
      );
      var changeFacts = await BuildCommitMessageFactsAsync(
        workspace.Path,
        status,
        cancellationToken
      );
      var response = await _ollama.GenerateTextAsync(
        baseUri,
        model.Name,
        [
          new ChatMessage(
            "system",
            "GIT_COMMIT_SUBJECT_V1\n"
              + "Write one concise Git commit subject, at most 100 characters. "
              + "Return only the subject with no quotes, Markdown, prefix, or explanation."
          ),
          new ChatMessage(
            "user",
            changeFacts
          )
        ],
        "git-commit-message",
        new ProviderCallContext(
          workspace.Id,
          null,
          Guid.NewGuid().ToString(
            "N"
          ),
          null,
          UsageModelRoles.CommitMessage,
          "explicit-git-commit-message",
          model.Digest
        ),
        cancellationToken
      );
      var subject = response.Replace(
        "\r\n",
        "\n",
        StringComparison.Ordinal
      ).Split(
        '\n',
        StringSplitOptions.RemoveEmptyEntries
      ).FirstOrDefault()?.Trim();
      if (string.IsNullOrWhiteSpace(
        subject
      ))
      {
        throw new GitDeliveryException(
          "git-commit-message-invalid",
          "git-commit-message",
          "The selected local model did not return a commit subject."
        );
      }
      return GitRepositoryService.ValidateCommitMessage(
        subject
      );
    }
    catch (OllamaProviderException exception)
    {
      throw new GitDeliveryException(
        "git-commit-message-generation-failed",
        "git-commit-message",
        exception.Message,
        exception.Recoverable,
        exception.TechnicalMessage,
        exception
      );
    }
  }

  private async Task<string> BuildCommitMessageFactsAsync(
    string workspacePath,
    GitRepositoryStatusView status,
    CancellationToken cancellationToken
  )
  {
    const int maximumCharacters = 16_000;
    var builder = new StringBuilder(
      "Current repository changes:\n"
    );
    foreach (var path in status.Paths.Where(
      path => path.Staged || path.Unstaged || path.Untracked
    ))
    {
      builder.Append(
        path.IndexStatus
      ).Append(
        path.WorkingTreeStatus
      ).Append(
        ' '
      ).AppendLine(
        path.Path
      );
    }
    var stagedPaths = status.StagedPaths.Distinct(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    var workingPaths = status.UnstagedPaths.Concat(
      status.UntrackedPaths
    ).Distinct(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    if (stagedPaths.Length > 0)
    {
      AppendDiffs(
        builder,
        "Staged diff",
        await _git.GetDiffAsync(
          workspacePath,
          stagedPaths,
          true,
          cancellationToken
        ),
        maximumCharacters
      );
    }
    if (
      workingPaths.Length > 0
      && builder.Length < maximumCharacters
    )
    {
      AppendDiffs(
        builder,
        "Working tree diff",
        await _git.GetDiffAsync(
          workspacePath,
          workingPaths,
          false,
          cancellationToken
        ),
        maximumCharacters
      );
    }
    if (builder.Length > maximumCharacters)
    {
      builder.Length = maximumCharacters;
      builder.Append(
        "\n[change summary truncated]"
      );
    }
    return builder.ToString();
  }

  private static void AppendDiffs(
    StringBuilder builder,
    string heading,
    GitDiffView diff,
    int maximumCharacters
  )
  {
    builder.AppendLine().AppendLine(
      heading
    );
    foreach (var file in diff.Files)
    {
      if (builder.Length >= maximumCharacters)
      {
        return;
      }
      builder.Append("--- ").AppendLine(
        file.Path
      );
      builder.AppendLine(
        file.Binary
          ? "[binary change]"
          : file.Content
      );
    }
  }

  private static string RequireExactExplicitMessage(
    string message
  )
  {
    var validated = GitRepositoryService.ValidateCommitMessage(
      message
    );
    if (!string.Equals(
      message,
      validated,
      StringComparison.Ordinal
    ))
    {
      throw new GitDeliveryException(
        "git-commit-message-invalid",
        "git-project-commit",
        "Remove leading or trailing whitespace from the commit message. The Host will not rewrite a supplied message."
      );
    }
    return message;
  }

  private static void ValidateCommitPolicy(
    ApplicationSettings settings,
    bool commitWithoutValidation
  )
  {
    if (!settings.GitDelivery.Enabled)
    {
      throw new GitDeliveryException(
        "git-delivery-disabled",
        "git-project-commit",
        "Git delivery is disabled in Settings."
      );
    }
    if (!settings.GitDelivery.RequireValidationBeforeCommit)
    {
      return;
    }
    if (!commitWithoutValidation)
    {
      throw new GitDeliveryException(
        "git-validation-required",
        "git-project-commit",
        "Validation is required before commit. Use an execution-session delivery or explicitly confirm the configured compact-flow override."
      );
    }
    if (!settings.GitDelivery.AllowExplicitCommitWithoutValidation)
    {
      throw new GitDeliveryException(
        "git-validation-override-disabled",
        "git-project-commit",
        "Settings do not allow an explicit commit without validation."
      );
    }
  }

  private static void RequireCommittableStatus(
    GitRepositoryStatusView status
  )
  {
    if (status.Clean)
    {
      throw new GitDeliveryException(
        "git-working-tree-clean",
        "git-project-commit",
        "There are no current changes to commit."
      );
    }
    if (status.Truncated)
    {
      throw new GitDeliveryException(
        "git-status-truncated",
        "git-project-commit",
        "The repository has more changed paths than the compact commit flow can bind safely."
      );
    }
    if (status.ConflictedPaths.Count > 0)
    {
      throw new GitDeliveryException(
        "git-conflicts-present",
        "git-project-commit",
        "Resolve Git conflicts before committing."
      );
    }
    if (!string.IsNullOrWhiteSpace(
      status.OperationInProgress
    ))
    {
      throw new GitDeliveryException(
        "git-operation-in-progress",
        "git-project-commit",
        $"Complete the current Git operation first: {status.OperationInProgress}."
      );
    }
  }

  private static void VerifyExactStagedSet(
    IReadOnlyList<string> expected,
    GitRepositoryStatusView status
  )
  {
    var expectedSet = expected.ToHashSet(
      StringComparer.OrdinalIgnoreCase
    );
    var stagedSet = status.StagedPaths.ToHashSet(
      StringComparer.OrdinalIgnoreCase
    );
    if (
      !expectedSet.SetEquals(
        stagedSet
      )
      || status.UnstagedPaths.Count > 0
      || status.UntrackedPaths.Count > 0
      || status.ConflictedPaths.Count > 0
    )
    {
      throw new GitDeliveryException(
        "git-staged-set-mismatch",
        "git-project-commit",
        "The staged set no longer matches the current changes; the commit was not created."
      );
    }
  }

  private static void ValidatePrivilegedRequest(
    string browserSessionId,
    string interactionMode,
    bool confirmed,
    string stage
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        browserSessionId
      )
      || browserSessionId.Length > 128
    )
    {
      throw new GitDeliveryException(
        "git-browser-session-invalid",
        stage,
        "A valid browser session is required."
      );
    }
    if (!string.Equals(
      interactionMode,
      "execute",
      StringComparison.Ordinal
    ))
    {
      throw new GitDeliveryException(
        "git-execute-mode-required",
        stage,
        "Git writes require Execute mode."
      );
    }
    if (!confirmed)
    {
      throw new GitDeliveryException(
        "git-explicit-approval-required",
        stage,
        "This Git write requires explicit confirmation."
      );
    }
  }
}
