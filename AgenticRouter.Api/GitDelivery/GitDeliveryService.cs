using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.GitDelivery;

public interface IGitDeliveryService
{
  Task<GitDeliveryStateView> GetAsync(
    ExecutionSession session,
    bool includeIgnored,
    CancellationToken cancellationToken
  );

  Task<GitDeliveryStateView> UpdateAsync(
    ExecutionSession session,
    UpdateDeliveryRequest request,
    CancellationToken cancellationToken
  );

  Task<GitDeliveryStateView> StageAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  );

  Task<GitDeliveryStateView> UnstageAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  );

  Task<GitDeliveryStateView> CommitAsync(
    ExecutionSession session,
    GitCommitRequest request,
    CancellationToken cancellationToken
  );

  Task<GitDeliveryStateView> TagAsync(
    ExecutionSession session,
    GitTagRequest request,
    CancellationToken cancellationToken
  );

  Task<GitDeliveryStateView> PushBranchAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  );

  Task<GitDeliveryStateView> PushTagAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  );

  Task<GitDiffView> GetDiffAsync(
    ExecutionSession session,
    GitDiffRequest request,
    CancellationToken cancellationToken
  );
}

public sealed class GitDeliveryService : IGitDeliveryService
{
  private readonly object _gate = new();
  private readonly Dictionary<string, DeliveryRecord> _records = new(
    StringComparer.Ordinal
  );
  private readonly IGitRepositoryService _git;
  private readonly ISettingsStore _settingsStore;

  public GitDeliveryService(
    IGitRepositoryService git,
    ISettingsStore settingsStore
  )
  {
    _git = git;
    _settingsStore = settingsStore;
  }

  public async Task<GitDeliveryStateView> GetAsync(
    ExecutionSession session,
    bool includeIgnored,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    if (!settings.GitDelivery.Enabled)
    {
      throw new GitDeliveryException(
        "git-delivery-disabled",
        "delivery-preparation",
        "Git delivery is disabled in application settings."
      );
    }
    var status = await _git.GetStatusAsync(
      session.WorkspacePath,
      includeIgnored,
      cancellationToken
    );
    var record = GetOrCreate(
      session,
      status
    );
    RefreshSelections(
      session,
      status,
      record
    );
    await RefreshSelectedHashesAsync(
      session,
      record,
      cancellationToken
    );
    await RefreshValidationBindingAsync(
      session,
      record,
      cancellationToken
    );
    record.Repository = status;
    AddEvent(
      record,
      "git-status-refreshed",
      "Git repository status refreshed."
    );
    UpdateState(
      record
    );
    var view = CreateView(
      record
    );
    session.RecordDelivery(
      view
    );
    return view;
  }

  public async Task<GitDeliveryStateView> UpdateAsync(
    ExecutionSession session,
    UpdateDeliveryRequest request,
    CancellationToken cancellationToken
  )
  {
    VerifyBrowser(
      session,
      request.BrowserSessionId
    );
    var current = await GetAsync(
      session,
      false,
      cancellationToken
    );
    var record = RequiredRecord(
      session.Id
    );
    var available = current.SessionChangedFiles.Concat(
      request.IncludePreExistingChanges
        ? current.PreExistingFiles
        : []
    ).ToHashSet(
      StringComparer.OrdinalIgnoreCase
    );
    var selected = NormalizePaths(
      request.SelectedFiles
    );
    if (selected.Any(
      path => !available.Contains(
        path
      )
    ))
    {
      throw new GitDeliveryException(
        "git-selected-path-invalid",
        "delivery-selection",
        "The selection contains a file that is not eligible for this delivery."
      );
    }
    if (selected.Any(
      path => current.Repository.ConflictedPaths.Contains(
        path,
        StringComparer.OrdinalIgnoreCase
      )
    ))
    {
      throw new GitDeliveryException(
        "git-conflicted-file",
        "delivery-selection",
        "Conflicted files cannot be selected for delivery."
      );
    }
    record.SelectedFiles = selected;
    record.IncludePreExistingChanges = request.IncludePreExistingChanges;
    record.CommitMessage = request.CommitMessage;
    record.ProposedTag = string.IsNullOrWhiteSpace(
      request.Tag
    )
      ? null
      : request.Tag.Trim();
    record.TagAnnotation = string.IsNullOrWhiteSpace(
      request.TagAnnotation
    )
      ? null
      : request.TagAnnotation.Trim();
    record.CommitWithoutValidation = request.CommitWithoutValidation;
    record.ValidationBinding = null;
    await RefreshSelectedHashesAsync(
      session,
      record,
      cancellationToken
    );
    AddEvent(
      record,
      "delivery-files-selected",
      $"Selected {record.SelectedFiles.Count} delivery files."
    );
    UpdateState(
      record
    );
    var view = CreateView(
      record
    );
    session.RecordDelivery(
      view
    );
    return view;
  }

  public async Task<GitDeliveryStateView> StageAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    var record = await PrepareWriteAsync(
      session,
      request.BrowserSessionId,
      request.ActionId,
      request.Confirmed,
      "stage",
      cancellationToken
    );
    if (record.SelectedFiles.Count == 0)
    {
      throw new GitDeliveryException(
        "git-selected-path-invalid",
        "git-stage",
        "Select at least one file before staging."
      );
    }
    RejectConflicts(
      record
    );
    AddEvent(
      record,
      "git-stage-requested",
      "Staging requested for the exact approved selection."
    );
    await VerifySessionFilesAsync(
      session,
      record.SelectedFiles,
      cancellationToken
    );
    record.Repository = await _git.StageAsync(
      session.WorkspacePath,
      record.SelectedFiles,
      cancellationToken
    );
    await RefreshValidationBindingAsync(
      session,
      record,
      cancellationToken
    );
    AddEvent(
      record,
      "git-files-staged",
      $"Staged {record.SelectedFiles.Count} files."
    );
    UpdateState(
      record
    );
    return StoreView(
      session,
      record
    );
  }

  public async Task<GitDeliveryStateView> UnstageAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    var record = await PrepareWriteAsync(
      session,
      request.BrowserSessionId,
      request.ActionId,
      request.Confirmed,
      "unstage",
      cancellationToken
    );
    var selectedStaged = record.SelectedFiles.Where(
      path => record.Repository.StagedPaths.Contains(
        path,
        StringComparer.OrdinalIgnoreCase
      )
    ).ToArray();
    if (selectedStaged.Length == 0)
    {
      throw new GitDeliveryException(
        "git-staged-path-missing",
        "git-unstage",
        "No selected file is currently staged."
      );
    }
    record.Repository = await _git.UnstageAsync(
      session.WorkspacePath,
      selectedStaged,
      cancellationToken
    );
    await RefreshValidationBindingAsync(
      session,
      record,
      cancellationToken
    );
    AddEvent(
      record,
      "git-files-unstaged",
      $"Unstaged {selectedStaged.Length} files."
    );
    UpdateState(
      record
    );
    return StoreView(
      session,
      record
    );
  }

  public async Task<GitDeliveryStateView> CommitAsync(
    ExecutionSession session,
    GitCommitRequest request,
    CancellationToken cancellationToken
  )
  {
    var record = await PrepareWriteAsync(
      session,
      request.BrowserSessionId,
      request.ActionId,
      request.Confirmed,
      request.CommitWithoutValidation
        ? "commit-without-validation"
        : "commit",
      cancellationToken
    );
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    if (request.CommitWithoutValidation != record.CommitWithoutValidation)
    {
      throw new GitDeliveryException(
        "git-approval-invalidated",
        "git-commit",
        "The validation override changed after the approval action was prepared."
      );
    }
    EnsureRepositoryReady(
      record.Repository,
      "git-commit"
    );
    RejectConflicts(
      record
    );
    _ = GitRepositoryService.ValidateCommitMessage(
      record.CommitMessage
    );
    var staged = record.Repository.StagedPaths.Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    var selected = record.SelectedFiles.Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    if (!staged.SequenceEqual(
      selected,
      StringComparer.OrdinalIgnoreCase
    ))
    {
      throw new GitDeliveryException(
        "git-staged-set-mismatch",
        "git-commit",
        "The staged files no longer exactly match the approved selection."
      );
    }
    await RefreshValidationBindingAsync(
      session,
      record,
      cancellationToken
    );
    var validationReady = record.ValidationBinding is
    {
      Passed: true,
      Stale: false
    };
    if (
      settings.GitDelivery.RequireValidationBeforeCommit
      && !validationReady
    )
    {
      var overrideAllowed = settings.GitDelivery.AllowExplicitCommitWithoutValidation
        && !ExplicitValidationRequired(
          session.Objective
        );
      if (
        !request.CommitWithoutValidation
        || !overrideAllowed
      )
      {
        throw new GitDeliveryException(
          record.ValidationBinding?.Stale == true
            ? "git-validation-stale"
            : "git-validation-missing",
          "git-commit",
          record.ValidationBinding?.Stale == true
            ? "Validation is stale because a selected file changed."
            : "A passing validation bound to the selected files is required."
        );
      }
      record.Warnings.Add(
        "Commit created with an explicit commit-without-validation override."
      );
    }
    var result = await _git.CommitAsync(
      session.WorkspacePath,
      record.CommitMessage,
      cancellationToken
    );
    record.CommitHash = result.Hash;
    record.CommitSubject = result.Subject;
    record.Repository = result.Status;
    record.BranchPushed = false;
    record.State = "committed";
    AddEvent(
      record,
      "git-commit-created",
      $"Commit {result.Hash} created."
    );
    session.MarkDeliveryCommitted(
      result.Hash
    );
    return StoreView(
      session,
      record
    );
  }

  public async Task<GitDeliveryStateView> TagAsync(
    ExecutionSession session,
    GitTagRequest request,
    CancellationToken cancellationToken
  )
  {
    var record = await PrepareWriteAsync(
      session,
      request.BrowserSessionId,
      request.ActionId,
      request.Confirmed,
      "tag",
      cancellationToken
    );
    if (string.IsNullOrWhiteSpace(
      record.CommitHash
    ))
    {
      throw new GitDeliveryException(
        "git-tag-target-missing",
        "git-tag",
        "Create a delivery commit before creating its tag."
      );
    }
    if (
      !string.Equals(
        request.Tag.Trim(),
        record.ProposedTag,
        StringComparison.Ordinal
      )
      || !string.Equals(
        request.Annotation.Trim(),
        record.TagAnnotation,
        StringComparison.Ordinal
      )
    )
    {
      throw new GitDeliveryException(
        "git-approval-invalidated",
        "git-tag",
        "The tag or annotation changed after the approval action was prepared."
      );
    }
    record.Repository = await _git.CreateAnnotatedTagAsync(
      session.WorkspacePath,
      request.Tag.Trim(),
      request.Annotation.Trim(),
      record.CommitHash,
      cancellationToken
    );
    record.CreatedTag = request.Tag.Trim();
    record.TagTarget = record.CommitHash;
    record.TagPushed = false;
    record.State = "tagged";
    AddEvent(
      record,
      "git-tag-created",
      $"Annotated tag {record.CreatedTag} created."
    );
    return StoreView(
      session,
      record
    );
  }

  public async Task<GitDeliveryStateView> PushBranchAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    var record = await PrepareWriteAsync(
      session,
      request.BrowserSessionId,
      request.ActionId,
      request.Confirmed,
      "push-branch",
      cancellationToken
    );
    if (string.IsNullOrWhiteSpace(
      record.CommitHash
    ))
    {
      throw new GitDeliveryException(
        "git-commit-missing",
        "git-push-branch",
        "Create the delivery commit before pushing."
      );
    }
    AddEvent(
      record,
      "push-preflight-started",
      "Guarded branch push preflight started."
    );
    try
    {
      record.Repository = await _git.PushCurrentBranchAsync(
        session.WorkspacePath,
        cancellationToken
      );
    }
    catch (GitDeliveryException exception)
    {
      record.Repository = await _git.GetStatusAsync(
        session.WorkspacePath,
        false,
        CancellationToken.None
      );
      record.State = exception.Code is
        "git-branch-behind"
        or "git-branch-diverged"
        ? "blocked"
        : "failed";
      record.Warnings.Add(
        exception.Message
      );
      AddEvent(
        record,
        exception.Code == "git-branch-diverged"
          ? "push-blocked-diverged"
          : "delivery-failed",
        exception.Message
      );
      session.RecordDelivery(
        CreateView(
          record
        )
      );
      throw;
    }
    record.BranchPushed = true;
    AddEvent(
      record,
      "git-branch-pushed",
      $"Branch commit {record.CommitHash} verified on upstream."
    );
    UpdateState(
      record
    );
    return StoreView(
      session,
      record
    );
  }

  public async Task<GitDeliveryStateView> PushTagAsync(
    ExecutionSession session,
    GitWriteRequest request,
    CancellationToken cancellationToken
  )
  {
    var record = await PrepareWriteAsync(
      session,
      request.BrowserSessionId,
      request.ActionId,
      request.Confirmed,
      "push-tag",
      cancellationToken
    );
    if (
      string.IsNullOrWhiteSpace(
        record.CreatedTag
      )
      || string.IsNullOrWhiteSpace(
        record.TagTarget
      )
    )
    {
      throw new GitDeliveryException(
        "git-tag-missing",
        "git-push-tag",
        "Create an annotated delivery tag before pushing it."
      );
    }
    try
    {
      record.Repository = await _git.PushTagAsync(
        session.WorkspacePath,
        record.CreatedTag,
        record.TagTarget,
        cancellationToken
      );
    }
    catch (GitDeliveryException exception)
    {
      record.State = "failed";
      record.Warnings.Add(
        exception.Message
      );
      AddEvent(
        record,
        "delivery-failed",
        exception.Message
      );
      session.RecordDelivery(
        CreateView(
          record
        )
      );
      throw;
    }
    record.TagPushed = true;
    AddEvent(
      record,
      "git-tag-pushed",
      $"Exact tag {record.CreatedTag} verified as pushed."
    );
    UpdateState(
      record
    );
    if (record.State == "pushed")
    {
      AddEvent(
        record,
        "delivery-completed",
        "Branch and requested tag delivery completed."
      );
    }
    return StoreView(
      session,
      record
    );
  }

  public Task<GitDiffView> GetDiffAsync(
    ExecutionSession session,
    GitDiffRequest request,
    CancellationToken cancellationToken
  )
  {
    return _git.GetDiffAsync(
      session.WorkspacePath,
      request.Paths,
      request.Staged,
      cancellationToken
    );
  }

  private async Task<DeliveryRecord> PrepareWriteAsync(
    ExecutionSession session,
    string browserSessionId,
    string actionId,
    bool confirmed,
    string operation,
    CancellationToken cancellationToken
  )
  {
    VerifyBrowser(
      session,
      browserSessionId
    );
    var current = await GetAsync(
      session,
      false,
      cancellationToken
    );
    var expected = operation switch
    {
      "stage" => current.StageActionId,
      "unstage" => current.UnstageActionId,
      "commit" or "commit-without-validation" => current.CommitActionId,
      "tag" => current.TagActionId,
      "push-branch" => current.PushBranchActionId,
      "push-tag" => current.PushTagActionId,
      _ => string.Empty
    };
    var approvalRequired = string.Equals(
      session.ApprovalPolicy,
      "ask",
      StringComparison.Ordinal
    );
    if (
      approvalRequired && !confirmed
      || string.IsNullOrWhiteSpace(
        actionId
      )
    )
    {
      throw new GitDeliveryException(
        "git-approval-required",
        $"git-{operation}",
        approvalRequired
          ? "The selected approval policy requires confirmation before this exact Git write."
          : "The Git action identifier is missing."
      );
    }
    if (!string.Equals(
      actionId,
      expected,
      StringComparison.Ordinal
    ))
    {
      throw new GitDeliveryException(
        "git-approval-invalidated",
        $"git-{operation}",
        "Repository or delivery inputs changed after this action was prepared."
      );
    }
    return RequiredRecord(
      session.Id
    );
  }

  private DeliveryRecord GetOrCreate(
    ExecutionSession session,
    GitRepositoryStatusView status
  )
  {
    lock (_gate)
    {
      if (_records.TryGetValue(
        session.Id,
        out var existing
      ))
      {
        return existing;
      }
      var review = session.CreateReview();
      var sessionFiles = review.Files.Select(
        file => file.RelativePath.Replace(
          '\\',
          '/'
        )
      ).Distinct(
        StringComparer.OrdinalIgnoreCase
      ).ToArray();
      var eligible = status.Paths.Where(
        path => !path.Conflicted
      ).Select(
        path => path.Path
      ).ToHashSet(
        StringComparer.OrdinalIgnoreCase
      );
      var selected = sessionFiles.Where(
        eligible.Contains
      ).Order(
        StringComparer.OrdinalIgnoreCase
      ).ToArray();
      var record = new DeliveryRecord(
        session.Id,
        status
      )
      {
        SessionChangedFiles = sessionFiles,
        SelectedFiles = review.Delivery?.SelectedFiles
          ?? selected,
        CommitMessage = review.Delivery?.CommitMessage
          ?? CreateDefaultCommitMessage(
            session.Objective
          ),
        CommitHash = review.Delivery?.CommitHash,
        CommitSubject = review.Delivery?.CommitSubject,
        ProposedTag = review.Delivery?.Tag,
        CreatedTag = review.Delivery?.TagTarget is null
          ? null
          : review.Delivery.Tag,
        TagAnnotation = review.Delivery?.TagAnnotation,
        TagTarget = review.Delivery?.TagTarget,
        BranchPushed = review.Delivery?.BranchPushed
          ?? false,
        TagPushed = review.Delivery?.TagPushed
          ?? false,
        CommitWithoutValidation = review.Delivery?.CommitWithoutValidation
          ?? false,
        ValidationBinding = review.Delivery?.ValidationBinding
      };
      if (review.Delivery is not null)
      {
        record.Warnings.AddRange(
          review.Delivery.Warnings
        );
        record.Events.AddRange(
          review.Delivery.Events
        );
      }
      AddEvent(
        record,
        "delivery-preparation-started",
        "Git delivery preparation started from fresh repository status."
      );
      _records[session.Id] = record;
      return record;
    }
  }

  private static void RefreshSelections(
    ExecutionSession session,
    GitRepositoryStatusView status,
    DeliveryRecord record
  )
  {
    var review = session.CreateReview();
    record.SessionChangedFiles = review.Files.Select(
      file => file.RelativePath.Replace(
        '\\',
        '/'
      )
    ).Distinct(
      StringComparer.OrdinalIgnoreCase
    ).Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    var dirty = status.Paths.Where(
      path => !path.Ignored
    ).Select(
      path => path.Path
    ).Distinct(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    record.PreExistingFiles = dirty.Where(
      path => !record.SessionChangedFiles.Contains(
        path,
        StringComparer.OrdinalIgnoreCase
      ) || review.Files.Any(
        file => file.PreExistingChange && string.Equals(
          file.RelativePath,
          path,
          StringComparison.OrdinalIgnoreCase
        )
      )
    ).Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
    var eligible = dirty.Where(
      path => !status.ConflictedPaths.Contains(
        path,
        StringComparer.OrdinalIgnoreCase
      )
    ).ToHashSet(
      StringComparer.OrdinalIgnoreCase
    );
    if (record.CommitHash is null)
    {
      record.SelectedFiles = record.SelectedFiles.Where(
        eligible.Contains
      ).ToArray();
    }
  }

  private async Task RefreshValidationBindingAsync(
    ExecutionSession session,
    DeliveryRecord record,
    CancellationToken cancellationToken
  )
  {
    var review = session.CreateReview();
    var validation = review.Validation;
    if (
      validation is null
      || validation.State is not (
        "passed"
        or "passed-with-warnings"
      )
      || record.SelectedFiles.Count == 0
    )
    {
      record.ValidationBinding = validation is null
        ? null
        : new DeliveryValidationBindingView(
          session.Id,
          ProfileDigest(
            review.ValidationProfile
          ),
          new Dictionary<string, string>(),
          validation.EndedAt,
          false,
          false,
          "The latest validation did not pass."
        );
      return;
    }
    var hashes = new Dictionary<string, string>(
      StringComparer.OrdinalIgnoreCase
    );
    var stale = false;
    foreach (var path in record.SelectedFiles)
    {
      var fullPath = Path.GetFullPath(
        Path.Combine(
          session.WorkspacePath,
          path
        )
      );
      if (!File.Exists(
        fullPath
      ))
      {
        stale = true;
        continue;
      }
      var hash = await HashFileAsync(
        fullPath,
        cancellationToken
      );
      hashes[path] = hash;
      var reviewed = review.Files.FirstOrDefault(
        file => string.Equals(
          file.RelativePath,
          path,
          StringComparison.OrdinalIgnoreCase
        )
      );
      if (
        reviewed is null
        || !string.Equals(
          reviewed.FinalHash,
          hash,
          StringComparison.OrdinalIgnoreCase
        )
        || File.GetLastWriteTimeUtc(
          fullPath
        ) > validation.EndedAt.UtcDateTime
      )
      {
        stale = true;
      }
    }
    var previousStale = record.ValidationBinding?.Stale;
    record.ValidationBinding = new DeliveryValidationBindingView(
      session.Id,
      ProfileDigest(
        review.ValidationProfile
      ),
      hashes,
      validation.EndedAt,
      true,
      stale,
      stale
        ? "A selected file changed after the latest validation."
        : null
    );
    if (stale && previousStale != true)
    {
      AddEvent(
        record,
        "validation-became-stale",
        "A selected file changed after validation."
      );
    }
    else if (!stale && record.ValidationBinding.Passed)
    {
      AddEvent(
        record,
        "validation-binding-created",
        "Passing validation bound to the current selected file hashes."
      );
    }
  }

  private static async Task VerifySessionFilesAsync(
    ExecutionSession session,
    IReadOnlyList<string> selected,
    CancellationToken cancellationToken
  )
  {
    var review = session.CreateReview();
    foreach (var file in review.Files.Where(
      file => selected.Contains(
        file.RelativePath,
        StringComparer.OrdinalIgnoreCase
      )
    ))
    {
      var path = Path.Combine(
        session.WorkspacePath,
        file.RelativePath
      );
      if (
        !File.Exists(
          path
        )
        || !string.Equals(
          await HashFileAsync(
            path,
            cancellationToken
          ),
          file.FinalHash,
          StringComparison.OrdinalIgnoreCase
        )
      )
      {
        throw new GitDeliveryException(
          "git-delivery-action-stale",
          "git-stage",
          $"The reviewed file '{file.RelativePath}' changed after execution review."
        );
      }
    }
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
    return Convert.ToHexString(
      await SHA256.HashDataAsync(
        stream,
        cancellationToken
      )
    ).ToLowerInvariant();
  }

  private static string ProfileDigest(
    ValidationProfileSettings? profile
  )
  {
    return Convert.ToHexString(
      SHA256.HashData(
        Encoding.UTF8.GetBytes(
          JsonSerializer.Serialize(
            profile
          )
        )
      )
    ).ToLowerInvariant();
  }

  private static void EnsureRepositoryReady(
    GitRepositoryStatusView status,
    string stage
  )
  {
    if (status.DetachedHead)
    {
      throw new GitDeliveryException(
        "git-detached-head",
        stage,
        "The operation is blocked while HEAD is detached."
      );
    }
    if (!string.IsNullOrWhiteSpace(
      status.OperationInProgress
    ))
    {
      throw new GitDeliveryException(
        "git-conflicting-operation",
        stage,
        $"A Git {status.OperationInProgress} operation is in progress."
      );
    }
  }

  private static void RejectConflicts(
    DeliveryRecord record
  )
  {
    if (record.SelectedFiles.Any(
      path => record.Repository.ConflictedPaths.Contains(
        path,
        StringComparer.OrdinalIgnoreCase
      )
    ))
    {
      throw new GitDeliveryException(
        "git-conflicted-file",
        "git-delivery",
        "The selected files contain an unresolved Git conflict."
      );
    }
  }

  private static bool ExplicitValidationRequired(
    string objective
  )
  {
    return objective.Contains(
      "must validate",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "validation required",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "required validation",
      StringComparison.OrdinalIgnoreCase
    ) || objective.Contains(
      "teste deve passar",
      StringComparison.OrdinalIgnoreCase
    );
  }

  private static IReadOnlyList<string> NormalizePaths(
    IReadOnlyList<string> paths
  )
  {
    return paths.Select(
      path => path.Replace(
        '\\',
        '/'
      ).Trim()
    ).Where(
      path => !string.IsNullOrWhiteSpace(
        path
      )
    ).Distinct(
      StringComparer.OrdinalIgnoreCase
    ).Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
  }

  private static string CreateDefaultCommitMessage(
    string objective
  )
  {
    var normalized = string.Join(
      " ",
      objective.Split(
        [
          '\r',
          '\n',
          '\t',
          ' '
        ],
        StringSplitOptions.RemoveEmptyEntries
      )
    );
    return normalized.Length <= 100
      ? normalized
      : normalized[..100].TrimEnd();
  }

  private static void UpdateState(
    DeliveryRecord record
  )
  {
    if (record.BranchPushed && (
      record.CreatedTag is null
      || record.TagPushed
    ))
    {
      record.State = "pushed";
      return;
    }
    if (record.BranchPushed || record.TagPushed)
    {
      record.State = "partially-pushed";
      return;
    }
    if (record.CreatedTag is not null)
    {
      record.State = "tagged";
      return;
    }
    if (record.CommitHash is not null)
    {
      record.State = "committed";
      return;
    }
    var selectedStaged = record.SelectedFiles.Count > 0
      && record.SelectedFiles.All(
        path => record.Repository.StagedPaths.Contains(
          path,
          StringComparer.OrdinalIgnoreCase
        )
      );
    if (selectedStaged)
    {
      record.State = record.ValidationBinding is
      {
        Passed: true,
        Stale: false
      }
        ? "ready-to-commit"
        : "validation-required";
      return;
    }
    record.State = record.SelectedFiles.Count > 0
      ? "changes-selected"
      : "not-prepared";
  }

  private GitDeliveryStateView StoreView(
    ExecutionSession session,
    DeliveryRecord record
  )
  {
    var view = CreateView(
      record
    );
    session.RecordDelivery(
      view
    );
    return view;
  }

  private static GitDeliveryStateView CreateView(
    DeliveryRecord record
  )
  {
    return new GitDeliveryStateView(
      record.State,
      record.Repository,
      record.SessionChangedFiles,
      record.PreExistingFiles,
      record.SelectedFiles,
      record.Repository.StagedPaths,
      record.ValidationBinding,
      record.CommitMessage,
      record.CommitHash,
      record.CommitSubject,
      record.CreatedTag
        ?? record.ProposedTag,
      record.TagAnnotation,
      record.TagTarget,
      record.BranchPushed,
      record.TagPushed,
      record.CommitWithoutValidation,
      record.Warnings.ToArray(),
      record.Events.ToArray(),
      ActionId(
        record,
        "stage"
      ),
      ActionId(
        record,
        "unstage"
      ),
      ActionId(
        record,
        "commit"
      ),
      ActionId(
        record,
        "tag"
      ),
      ActionId(
        record,
        "push-branch"
      ),
      ActionId(
        record,
        "push-tag"
      )
    );
  }

  private static string ActionId(
    DeliveryRecord record,
    string operation
  )
  {
    var payload = string.Join(
      "\n",
      operation,
      record.SessionId,
      record.Repository.Head,
      record.Repository.Branch,
      record.Repository.Upstream,
      string.Join(
        "\n",
        record.SelectedFiles
      ),
      string.Join(
        "\n",
        record.Repository.StagedPaths
      ),
      string.Join(
        "\n",
        record.SelectedFileHashes.OrderBy(
          pair => pair.Key,
          StringComparer.OrdinalIgnoreCase
        ).Select(
          pair => $"{pair.Key}:{pair.Value}"
        )
      ),
      record.CommitMessage,
      record.ProposedTag,
      record.TagAnnotation,
      record.CommitHash,
      record.CreatedTag,
      record.CommitWithoutValidation
    );
    return Convert.ToHexString(
      SHA256.HashData(
        Encoding.UTF8.GetBytes(
          payload
        )
      )
    ).ToLowerInvariant();
  }

  private static void AddEvent(
    DeliveryRecord record,
    string type,
    string message
  )
  {
    if (
      record.Events.LastOrDefault() is
      {
        Type: var lastType,
        Message: var lastMessage
      }
      && string.Equals(
        lastType,
        type,
        StringComparison.Ordinal
      )
      && string.Equals(
        lastMessage,
        message,
        StringComparison.Ordinal
      )
    )
    {
      return;
    }
    record.Events.Add(
      new GitDeliveryEventView(
        type,
        message,
        DateTimeOffset.UtcNow
      )
    );
    if (record.Events.Count > 100)
    {
      record.Events.RemoveAt(
        0
      );
    }
  }

  private DeliveryRecord RequiredRecord(
    string sessionId
  )
  {
    lock (_gate)
    {
      return _records.TryGetValue(
        sessionId,
        out var record
      )
        ? record
        : throw new GitDeliveryException(
          "git-delivery-unavailable",
          "delivery-preparation",
          "The delivery state is unavailable."
        );
    }
  }

  private static async Task RefreshSelectedHashesAsync(
    ExecutionSession session,
    DeliveryRecord record,
    CancellationToken cancellationToken
  )
  {
    var hashes = new Dictionary<string, string>(
      StringComparer.OrdinalIgnoreCase
    );
    foreach (var path in record.SelectedFiles)
    {
      var fullPath = Path.Combine(
        session.WorkspacePath,
        path
      );
      hashes[path] = File.Exists(
        fullPath
      )
        ? await HashFileAsync(
          fullPath,
          cancellationToken
        )
        : "[missing]";
    }
    record.SelectedFileHashes = hashes;
  }

  private static void VerifyBrowser(
    ExecutionSession session,
    string browserSessionId
  )
  {
    if (!string.Equals(
      session.BrowserSessionId,
      browserSessionId,
      StringComparison.Ordinal
    ))
    {
      throw new GitDeliveryException(
        "git-execution-session-mismatch",
        "git-delivery",
        "This execution session belongs to a different browser session."
      );
    }
  }

  private sealed class DeliveryRecord
  {
    public DeliveryRecord(
      string sessionId,
      GitRepositoryStatusView repository
    )
    {
      SessionId = sessionId;
      Repository = repository;
    }

    public string SessionId { get; }

    public GitRepositoryStatusView Repository { get; set; }

    public string State { get; set; } = "not-prepared";

    public IReadOnlyList<string> SessionChangedFiles { get; set; } = [];

    public IReadOnlyList<string> PreExistingFiles { get; set; } = [];

    public IReadOnlyList<string> SelectedFiles { get; set; } = [];

    public IReadOnlyDictionary<string, string> SelectedFileHashes { get; set; } =
      new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase
      );

    public bool IncludePreExistingChanges { get; set; }

    public DeliveryValidationBindingView? ValidationBinding { get; set; }

    public string CommitMessage { get; set; } = string.Empty;

    public string? ProposedTag { get; set; }

    public string? TagAnnotation { get; set; }

    public string? CommitHash { get; set; }

    public string? CommitSubject { get; set; }

    public string? CreatedTag { get; set; }

    public string? TagTarget { get; set; }

    public bool BranchPushed { get; set; }

    public bool TagPushed { get; set; }

    public bool CommitWithoutValidation { get; set; }

    public List<string> Warnings { get; } = [];

    public List<GitDeliveryEventView> Events { get; } = [];
  }
}
