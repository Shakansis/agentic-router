using System.Globalization;
using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.Api.GitDelivery;

public interface IGitRepositoryService
{
  Task<GitRepositoryStatusView> GetStatusAsync(
    string workspacePath,
    bool includeIgnored,
    CancellationToken cancellationToken
  );

  Task<GitDiffView> GetDiffAsync(
    string workspacePath,
    IReadOnlyList<string> paths,
    bool staged,
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<GitLogEntryView>> GetLogAsync(
    string workspacePath,
    int maximumEntries,
    CancellationToken cancellationToken
  );

  Task<GitCommitView> ShowCommitAsync(
    string workspacePath,
    string commit,
    CancellationToken cancellationToken
  );

  Task<GitRepositoryStatusView> StageAsync(
    string workspacePath,
    IReadOnlyList<string> paths,
    CancellationToken cancellationToken
  );

  Task<GitRepositoryStatusView> UnstageAsync(
    string workspacePath,
    IReadOnlyList<string> paths,
    CancellationToken cancellationToken
  );

  Task<(string Hash, string Subject, GitRepositoryStatusView Status)> CommitAsync(
    string workspacePath,
    string message,
    CancellationToken cancellationToken
  );

  Task<GitRepositoryStatusView> CreateAnnotatedTagAsync(
    string workspacePath,
    string tag,
    string annotation,
    string targetCommit,
    CancellationToken cancellationToken
  );

  Task<GitRepositoryStatusView> PushCurrentBranchAsync(
    string workspacePath,
    CancellationToken cancellationToken
  );

  Task<GitRepositoryStatusView> PushTagAsync(
    string workspacePath,
    string tag,
    string targetCommit,
    CancellationToken cancellationToken
  );
}

public sealed class GitRepositoryService : IGitRepositoryService
{
  private const int MaximumStatusPaths = 500;
  private readonly IProcessExecutionService _processExecution;
  private readonly ISettingsStore _settingsStore;

  public GitRepositoryService(
    IProcessExecutionService processExecution,
    ISettingsStore settingsStore
  )
  {
    _processExecution = processExecution;
    _settingsStore = settingsStore;
  }

  public async Task<GitRepositoryStatusView> GetStatusAsync(
    string workspacePath,
    bool includeIgnored,
    CancellationToken cancellationToken
  )
  {
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var branchResult = await RunAsync(
      repository.Root,
      [
        "symbolic-ref",
        "--quiet",
        "--short",
        "HEAD"
      ],
      "git-status",
      cancellationToken,
      allowFailure: true
    );
    var branch = branchResult.ExitCode == 0
      ? branchResult.StandardOutput.Trim()
      : null;
    var headResult = await RunAsync(
      repository.Root,
      [
        "rev-parse",
        "--verify",
        "HEAD"
      ],
      "git-status",
      cancellationToken,
      allowFailure: true
    );
    var head = headResult.ExitCode == 0
      ? headResult.StandardOutput.Trim()
      : null;
    var upstreamResult = await RunAsync(
      repository.Root,
      [
        "rev-parse",
        "--abbrev-ref",
        "--symbolic-full-name",
        "@{upstream}"
      ],
      "git-status",
      cancellationToken,
      allowFailure: true
    );
    var upstream = upstreamResult.ExitCode == 0
      ? upstreamResult.StandardOutput.Trim()
      : null;
    string? upstreamCommit = null;
    var ahead = 0;
    var behind = 0;

    if (!string.IsNullOrWhiteSpace(
      upstream
    ))
    {
      var upstreamCommitResult = await RunAsync(
        repository.Root,
        [
          "rev-parse",
          "--verify",
          "@{upstream}"
        ],
        "git-status",
        cancellationToken,
        allowFailure: true
      );
      upstreamCommit = upstreamCommitResult.ExitCode == 0
        ? upstreamCommitResult.StandardOutput.Trim()
        : null;
      var counts = await RunAsync(
        repository.Root,
        [
          "rev-list",
          "--left-right",
          "--count",
          "HEAD...@{upstream}"
        ],
        "git-status",
        cancellationToken,
        allowFailure: true
      );

      if (counts.ExitCode == 0)
      {
        var values = counts.StandardOutput.Split(
          [
            ' ',
            '\t',
            '\r',
            '\n'
          ],
          StringSplitOptions.RemoveEmptyEntries
        );
        if (values.Length == 2)
        {
          _ = int.TryParse(
            values[0],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ahead
          );
          _ = int.TryParse(
            values[1],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out behind
          );
        }
      }
    }

    var statusArguments = new List<string>
    {
      "status",
      "--porcelain=v1",
      "-z",
      "--untracked-files=all"
    };
    if (includeIgnored)
    {
      statusArguments.Add(
        "--ignored"
      );
    }

    var statusResult = await RunAsync(
      repository.Root,
      statusArguments,
      "git-status",
      cancellationToken
    );
    var parsed = ParseStatus(
      statusResult.StandardOutput
    );
    var truncated = parsed.Count > MaximumStatusPaths;
    var paths = parsed.Take(
      MaximumStatusPaths
    ).ToArray();
    var gitDirectory = await RunAsync(
      repository.Root,
      [
        "rev-parse",
        "--absolute-git-dir"
      ],
      "git-status",
      cancellationToken
    );
    var operation = DetectOperation(
      gitDirectory.StandardOutput.Trim()
    );

    return new GitRepositoryStatusView(
      true,
      null,
      Path.GetRelativePath(
        workspacePath,
        repository.Root
      ).Replace(
        '\\',
        '/'
      ),
      branch,
      head,
      upstream,
      upstreamCommit,
      ahead,
      behind,
      string.IsNullOrWhiteSpace(
        branch
      ),
      operation,
      paths.Where(
        item => item.Staged
      ).Select(
        item => item.Path
      ).ToArray(),
      paths.Where(
        item => item.Unstaged
      ).Select(
        item => item.Path
      ).ToArray(),
      paths.Where(
        item => item.Untracked
      ).Select(
        item => item.Path
      ).ToArray(),
      paths.Where(
        item => item.Conflicted
      ).Select(
        item => item.Path
      ).ToArray(),
      paths.Where(
        item => item.Ignored
      ).Select(
        item => item.Path
      ).ToArray(),
      paths,
      paths.Length == 0,
      truncated,
      DateTimeOffset.UtcNow
    );
  }

  public async Task<GitDiffView> GetDiffAsync(
    string workspacePath,
    IReadOnlyList<string> paths,
    bool staged,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var normalized = ValidatePaths(
      repository.Root,
      paths
    );
    var status = await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
    var files = new List<GitDiffFileView>();

    foreach (var path in normalized)
    {
      if (
        !staged
        && status.UntrackedPaths.Contains(
          path,
          StringComparer.OrdinalIgnoreCase
        )
      )
      {
        files.Add(
          await CreateUntrackedDiffAsync(
            repository.Root,
            path,
            settings.GitDelivery.MaxDiffBytesPerFile,
            cancellationToken
          )
        );
        continue;
      }
      var arguments = new List<string>
      {
        "diff",
        "--no-ext-diff",
        "--no-color"
      };
      if (staged)
      {
        arguments.Add(
          "--cached"
        );
      }
      arguments.Add(
        "--"
      );
      arguments.Add(
        path
      );
      var result = await RunAsync(
        repository.Root,
        arguments,
        "git-diff",
        cancellationToken
      );
      var limit = settings.GitDelivery.MaxDiffBytesPerFile;
      var content = result.StandardOutput;
      var truncated = content.Length > limit || result.StandardOutputTruncated;
      if (content.Length > limit)
      {
        content = string.Concat(
          content.AsSpan(
            0,
            limit
          ),
          "\n[diff truncated]"
        );
      }
      files.Add(
        new GitDiffFileView(
          path,
          staged,
          content.Contains(
            "Binary files ",
            StringComparison.Ordinal
          ) || content.Contains(
            "GIT binary patch",
            StringComparison.Ordinal
          ),
          truncated,
          content
        )
      );
    }

    return new GitDiffView(
      files,
      files.Any(
        file => file.Truncated
      )
    );
  }

  public async Task<IReadOnlyList<GitLogEntryView>> GetLogAsync(
    string workspacePath,
    int maximumEntries,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var limit = Math.Clamp(
      maximumEntries,
      1,
      settings.GitDelivery.MaxLogEntries
    );
    var result = await RunAsync(
      repository.Root,
      [
        "log",
        $"-n{limit}",
        "--date=iso-strict",
        "--pretty=format:%H%x1f%s%x1f%an%x1f%aI%x1e"
      ],
      "git-log",
      cancellationToken
    );
    return result.StandardOutput.Split(
      '\u001e',
      StringSplitOptions.RemoveEmptyEntries
    ).Select(
      ParseLogEntry
    ).ToArray();
  }

  public async Task<GitCommitView> ShowCommitAsync(
    string workspacePath,
    string commit,
    CancellationToken cancellationToken
  )
  {
    ValidateObjectName(
      commit
    );
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var result = await RunAsync(
      repository.Root,
      [
        "show",
        "--no-patch",
        "--date=iso-strict",
        "--pretty=format:%H%x1f%s%x1f%b%x1f%an%x1f%aI",
        commit
      ],
      "git-show-commit",
      cancellationToken
    );
    var fields = result.StandardOutput.Split(
      '\u001f'
    );

    if (fields.Length < 5)
    {
      throw new GitDeliveryException(
        "git-commit-unavailable",
        "git-show-commit",
        "Git did not return the requested commit."
      );
    }

    return new GitCommitView(
      fields[0],
      fields[1],
      fields[2],
      fields[3],
      ParseDate(
        fields[4]
      )
    );
  }

  public async Task<GitRepositoryStatusView> StageAsync(
    string workspacePath,
    IReadOnlyList<string> paths,
    CancellationToken cancellationToken
  )
  {
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var normalized = ValidatePaths(
      repository.Root,
      paths
    );
    await RunAsync(
      repository.Root,
      [
        "add",
        "--",
        .. normalized
      ],
      "git-stage",
      cancellationToken
    );
    return await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
  }

  public async Task<GitRepositoryStatusView> UnstageAsync(
    string workspacePath,
    IReadOnlyList<string> paths,
    CancellationToken cancellationToken
  )
  {
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var normalized = ValidatePaths(
      repository.Root,
      paths
    );
    await RunAsync(
      repository.Root,
      [
        "restore",
        "--staged",
        "--",
        .. normalized
      ],
      "git-unstage",
      cancellationToken
    );
    return await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
  }

  public async Task<(string Hash, string Subject, GitRepositoryStatusView Status)> CommitAsync(
    string workspacePath,
    string message,
    CancellationToken cancellationToken
  )
  {
    var normalized = ValidateCommitMessage(
      message
    );
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var lines = normalized.Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ).Split(
      '\n'
    );
    var subject = lines[0];
    var body = string.Join(
      "\n",
      lines.Skip(
        1
      )
    ).Trim();
    var arguments = new List<string>
    {
      "commit",
      "-m",
      subject
    };
    if (!string.IsNullOrWhiteSpace(
      body
    ))
    {
      arguments.Add(
        "-m"
      );
      arguments.Add(
        body
      );
    }
    await RunAsync(
      repository.Root,
      arguments,
      "git-commit",
      cancellationToken
    );
    var hashResult = await RunAsync(
      repository.Root,
      [
        "rev-parse",
        "HEAD"
      ],
      "git-commit",
      cancellationToken
    );
    var status = await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
    return (
      hashResult.StandardOutput.Trim(),
      subject,
      status
    );
  }

  public async Task<GitRepositoryStatusView> CreateAnnotatedTagAsync(
    string workspacePath,
    string tag,
    string annotation,
    string targetCommit,
    CancellationToken cancellationToken
  )
  {
    ValidateTag(
      tag,
      annotation
    );
    ValidateObjectName(
      targetCommit
    );
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var check = await RunAsync(
      repository.Root,
      [
        "check-ref-format",
        $"refs/tags/{tag}"
      ],
      "git-tag",
      cancellationToken,
      allowFailure: true
    );
    if (check.ExitCode != 0)
    {
      throw new GitDeliveryException(
        "git-tag-invalid",
        "git-tag",
        "The tag name is invalid."
      );
    }
    var existing = await RunAsync(
      repository.Root,
      [
        "show-ref",
        "--verify",
        $"refs/tags/{tag}"
      ],
      "git-tag",
      cancellationToken,
      allowFailure: true
    );
    if (existing.ExitCode == 0)
    {
      throw new GitDeliveryException(
        "git-tag-exists",
        "git-tag",
        "The tag already exists locally."
      );
    }
    var status = await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
    if (!string.IsNullOrWhiteSpace(
      status.Upstream
    ))
    {
      var (
        remote,
        _
      ) = ParseUpstream(
        status.Upstream
      );
      var remoteTag = await RunAsync(
        repository.Root,
        [
          "ls-remote",
          "--tags",
          remote,
          $"refs/tags/{tag}"
        ],
        "git-tag",
        cancellationToken
      );
      if (!string.IsNullOrWhiteSpace(
        remoteTag.StandardOutput
      ))
      {
        throw new GitDeliveryException(
          "git-remote-tag-exists",
          "git-tag",
          "The tag already exists on the remote."
        );
      }
    }
    await RunAsync(
      repository.Root,
      [
        "tag",
        "-a",
        tag,
        targetCommit,
        "-m",
        annotation
      ],
      "git-tag",
      cancellationToken
    );
    return await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
  }

  public async Task<GitRepositoryStatusView> PushCurrentBranchAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    var status = await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
    EnsurePushableBranch(
      status
    );
    var (
      remote,
      upstreamBranch
    ) = ParseUpstream(
      status.Upstream!
    );
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    await RunAsync(
      repository.Root,
      [
        "fetch",
        "--prune",
        remote
      ],
      "git-push-preflight",
      cancellationToken
    );
    status = await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
    EnsurePushableBranch(
      status
    );
    if (status.Behind > 0)
    {
      throw new GitDeliveryException(
        status.Ahead > 0
          ? "git-branch-diverged"
          : "git-branch-behind",
        "git-push-preflight",
        status.Ahead > 0
          ? "The local branch diverged from its upstream."
          : "The local branch is behind its upstream.",
        diagnostic: $"ahead={status.Ahead}; behind={status.Behind}"
      );
    }
    await RunAsync(
      repository.Root,
      [
        "push",
        remote,
        $"HEAD:refs/heads/{upstreamBranch}"
      ],
      "git-push-branch",
      cancellationToken
    );
    var refreshed = await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
    if (refreshed.Ahead != 0)
    {
      throw new GitDeliveryException(
        "git-push-verification-failed",
        "git-push-branch",
        "The upstream did not verify the pushed commit.",
        true
      );
    }
    return refreshed;
  }

  public async Task<GitRepositoryStatusView> PushTagAsync(
    string workspacePath,
    string tag,
    string targetCommit,
    CancellationToken cancellationToken
  )
  {
    ValidateTag(
      tag,
      tag
    );
    ValidateObjectName(
      targetCommit
    );
    var status = await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
    if (string.IsNullOrWhiteSpace(
      status.Upstream
    ))
    {
      throw new GitDeliveryException(
        "git-upstream-missing",
        "git-push-tag",
        "The current branch has no configured upstream."
      );
    }
    var (
      remote,
      _
    ) = ParseUpstream(
      status.Upstream
    );
    var repository = await ResolveRepositoryAsync(
      workspacePath,
      cancellationToken
    );
    var tagType = await RunAsync(
      repository.Root,
      [
        "cat-file",
        "-t",
        tag
      ],
      "git-push-tag",
      cancellationToken,
      allowFailure: true
    );
    if (
      tagType.ExitCode != 0
      || !string.Equals(
        tagType.StandardOutput.Trim(),
        "tag",
        StringComparison.Ordinal
      )
    )
    {
      throw new GitDeliveryException(
        "git-tag-not-annotated",
        "git-push-tag",
        "Only an existing annotated tag can be pushed."
      );
    }
    var resolved = await RunAsync(
      repository.Root,
      [
        "rev-list",
        "-n",
        "1",
        tag
      ],
      "git-push-tag",
      cancellationToken
    );
    if (!string.Equals(
      resolved.StandardOutput.Trim(),
      targetCommit,
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw new GitDeliveryException(
        "git-tag-target-mismatch",
        "git-push-tag",
        "The tag target no longer matches the approved commit."
      );
    }
    var remoteTag = await RunAsync(
      repository.Root,
      [
        "ls-remote",
        "--tags",
        remote,
        $"refs/tags/{tag}"
      ],
      "git-push-tag",
      cancellationToken
    );
    if (!string.IsNullOrWhiteSpace(
      remoteTag.StandardOutput
    ))
    {
      throw new GitDeliveryException(
        "git-remote-tag-exists",
        "git-push-tag",
        "The tag already exists on the remote."
      );
    }
    await RunAsync(
      repository.Root,
      [
        "push",
        remote,
        $"refs/tags/{tag}:refs/tags/{tag}"
      ],
      "git-push-tag",
      cancellationToken
    );
    return await GetStatusAsync(
      workspacePath,
      false,
      cancellationToken
    );
  }

  public static string ValidateCommitMessage(
    string message
  )
  {
    var normalized = message.Trim();
    if (string.IsNullOrWhiteSpace(
      normalized
    ))
    {
      throw new GitDeliveryException(
        "git-commit-message-invalid",
        "git-commit",
        "The commit message cannot be empty."
      );
    }
    if (
      normalized.Any(
        character => char.IsControl(
          character
        ) && character is not '\r' and not '\n' and not '\t'
      )
      || normalized.Contains(
        '\0',
        StringComparison.Ordinal
      )
    )
    {
      throw new GitDeliveryException(
        "git-commit-message-invalid",
        "git-commit",
        "The commit message contains control characters."
      );
    }
    var lines = normalized.Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ).Split(
      '\n'
    );
    var commandInjectionMarkers = new[]
    {
      "; git ",
      "&& git ",
      "|| git ",
      "`git ",
      "$(git "
    };
    if (
      lines[0].StartsWith(
        '-'
      )
      || commandInjectionMarkers.Any(
        marker => normalized.Contains(
          marker,
          StringComparison.OrdinalIgnoreCase
        )
      )
    )
    {
      throw new GitDeliveryException(
        "git-commit-message-invalid",
        "git-commit",
        "The commit message resembles a hidden argument or another Git command."
      );
    }
    if (lines[0].Length > 100)
    {
      throw new GitDeliveryException(
        "git-commit-message-invalid",
        "git-commit",
        "The commit subject must contain at most 100 characters."
      );
    }
    if (normalized.Length > 10_000)
    {
      throw new GitDeliveryException(
        "git-commit-message-invalid",
        "git-commit",
        "The commit message body is too long."
      );
    }
    return normalized;
  }

  private async Task<(string Root, string Workspace)> ResolveRepositoryAsync(
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    var workspace = Path.GetFullPath(
      workspacePath
    );
    if (
      !Directory.Exists(
        workspace
      )
      || (
        File.GetAttributes(
          workspace
        )
        & FileAttributes.ReparsePoint
      ) != 0
    )
    {
      throw new GitDeliveryException(
        "git-repository-unavailable",
        "git-repository",
        "The trusted workspace is unavailable or is a reparse point."
      );
    }
    var result = await RunAsync(
      workspace,
      [
        "rev-parse",
        "--show-toplevel"
      ],
      "git-repository",
      cancellationToken,
      allowFailure: true
    );
    if (result.ExitCode != 0)
    {
      throw new GitDeliveryException(
        "git-repository-unavailable",
        "git-repository",
        "The trusted workspace is not inside a Git repository.",
        diagnostic: Sanitize(
          result.StandardError,
          workspace
        )
      );
    }
    var root = Path.GetFullPath(
      result.StandardOutput.Trim()
    );
    var relative = Path.GetRelativePath(
      workspace,
      root
    );
    if (
      Path.IsPathFullyQualified(
        relative
      )
      || relative == ".."
      || relative.StartsWith(
        $"..{Path.DirectorySeparatorChar}",
        StringComparison.Ordinal
      )
    )
    {
      throw new GitDeliveryException(
        "git-repository-outside-workspace",
        "git-repository",
        "The Git repository root is outside the trusted workspace."
      );
    }
    EnsureNoReparsePoints(
      workspace,
      root
    );
    return (
      root,
      workspace
    );
  }

  private static IReadOnlyList<string> ValidatePaths(
    string repositoryRoot,
    IReadOnlyList<string> paths
  )
  {
    if (paths.Count == 0 || paths.Count > 100)
    {
      throw new GitDeliveryException(
        "git-selected-path-invalid",
        "git-path-validation",
        "Select between 1 and 100 repository files."
      );
    }
    var normalized = new List<string>();
    foreach (var rawPath in paths)
    {
      if (
        string.IsNullOrWhiteSpace(
          rawPath
        )
        || Path.IsPathFullyQualified(
          rawPath
        )
      )
      {
        throw new GitDeliveryException(
          "git-selected-path-outside-repository",
          "git-path-validation",
          "Selected Git paths must be relative to the repository."
        );
      }
      var fullPath = Path.GetFullPath(
        Path.Combine(
          repositoryRoot,
          rawPath
        )
      );
      var relative = Path.GetRelativePath(
        repositoryRoot,
        fullPath
      );
      if (
        relative == ".."
        || relative.StartsWith(
          $"..{Path.DirectorySeparatorChar}",
          StringComparison.Ordinal
        )
        || Path.IsPathFullyQualified(
          relative
        )
      )
      {
        throw new GitDeliveryException(
          "git-selected-path-outside-repository",
          "git-path-validation",
          "A selected path escaped the repository."
        );
      }
      EnsureNoReparsePoints(
        repositoryRoot,
        fullPath
      );
      normalized.Add(
        relative.Replace(
          '\\',
          '/'
        )
      );
    }
    return normalized.Distinct(
      StringComparer.OrdinalIgnoreCase
    ).Order(
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
  }

  private static void EnsureNoReparsePoints(
    string root,
    string target
  )
  {
    var relative = Path.GetRelativePath(
      root,
      target
    );
    var current = root;
    foreach (var segment in relative.Split(
      [
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar
      ],
      StringSplitOptions.RemoveEmptyEntries
    ))
    {
      current = Path.Combine(
        current,
        segment
      );
      if (
        (
          File.Exists(
            current
          )
          || Directory.Exists(
            current
          )
        )
        && (
          File.GetAttributes(
            current
          )
          & FileAttributes.ReparsePoint
        ) != 0
      )
      {
        throw new GitDeliveryException(
          "git-selected-path-reparse-point",
          "git-path-validation",
          "Git paths containing reparse points are not allowed."
        );
      }
    }
  }

  private async Task<ProcessExecutionResult> RunAsync(
    string workingDirectory,
    IReadOnlyList<string> arguments,
    string stage,
    CancellationToken cancellationToken,
    bool allowFailure = false
  )
  {
    var result = await _processExecution.ExecuteAsync(
      new ProcessExecutionRequest(
        "git",
        arguments,
        workingDirectory,
        TimeSpan.FromSeconds(
          stage.Contains(
            "push",
            StringComparison.Ordinal
          ) ? 30 : 10
        )
      ),
      cancellationToken
    );
    if (
      !allowFailure
      && (
        result.ExitCode != 0
        || result.TimedOut
        || result.Cancelled
      )
    )
    {
      var diagnostic = Sanitize(
        result.StandardError,
        workingDirectory
      );
      throw new GitDeliveryException(
        ClassifyFailure(
          stage,
          diagnostic
        ),
        stage,
        $"Git could not complete {stage}.",
        result.TimedOut,
        diagnostic
      );
    }
    return result;
  }

  private static List<GitPathStatusView> ParseStatus(
    string output
  )
  {
    var entries = output.Split(
      '\0',
      StringSplitOptions.RemoveEmptyEntries
    );
    var result = new List<GitPathStatusView>();
    var skipRenameSource = false;

    foreach (var entry in entries)
    {
      if (skipRenameSource)
      {
        skipRenameSource = false;
        continue;
      }
      if (entry.Length < 3)
      {
        continue;
      }
      var index = entry[0];
      var working = entry[1];
      var path = entry[3..].Replace(
        '\\',
        '/'
      );
      var pair = string.Concat(
        index,
        working
      );
      var untracked = pair == "??";
      var ignored = pair == "!!";
      var conflicted = pair is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";
      var staged = !untracked && !ignored && index != ' ';
      var unstaged = !ignored && (
        untracked
        || working != ' '
      );
      result.Add(
        new GitPathStatusView(
          path,
          index.ToString(),
          working.ToString(),
          staged,
          unstaged,
          untracked,
          conflicted,
          ignored
        )
      );
      skipRenameSource = index is 'R' or 'C';
    }
    return result;
  }

  private static string? DetectOperation(
    string gitDirectory
  )
  {
    if (File.Exists(
      Path.Combine(
        gitDirectory,
        "MERGE_HEAD"
      )
    ))
    {
      return "merge";
    }
    if (
      Directory.Exists(
        Path.Combine(
          gitDirectory,
          "rebase-merge"
        )
      )
      || Directory.Exists(
        Path.Combine(
          gitDirectory,
          "rebase-apply"
        )
      )
    )
    {
      return "rebase";
    }
    if (File.Exists(
      Path.Combine(
        gitDirectory,
        "CHERRY_PICK_HEAD"
      )
    ))
    {
      return "cherry-pick";
    }
    return File.Exists(
      Path.Combine(
        gitDirectory,
        "REVERT_HEAD"
      )
    )
      ? "revert"
      : null;
  }

  private static void EnsurePushableBranch(
    GitRepositoryStatusView status
  )
  {
    if (
      status.DetachedHead
      || string.IsNullOrWhiteSpace(
        status.Branch
      )
    )
    {
      throw new GitDeliveryException(
        "git-detached-head",
        "git-push-preflight",
        "Push is blocked while HEAD is detached."
      );
    }
    if (string.IsNullOrWhiteSpace(
      status.Upstream
    ))
    {
      throw new GitDeliveryException(
        "git-upstream-missing",
        "git-push-preflight",
        "The current branch has no configured upstream."
      );
    }
  }

  private static (string Remote, string Branch) ParseUpstream(
    string upstream
  )
  {
    var separator = upstream.IndexOf(
      '/'
    );
    if (
      separator <= 0
      || separator == upstream.Length - 1
    )
    {
      throw new GitDeliveryException(
        "git-upstream-invalid",
        "git-push-preflight",
        "The configured upstream could not be resolved safely."
      );
    }
    return (
      upstream[..separator],
      upstream[
        (
          separator + 1
        )..
      ]
    );
  }

  private static void ValidateTag(
    string tag,
    string annotation
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        tag
      )
      || tag.Length > 200
      || tag.Any(
        char.IsControl
      )
      || string.IsNullOrWhiteSpace(
        annotation
      )
      || annotation.Length > 10_000
      || annotation.Any(
        character => char.IsControl(
          character
        ) && character is not '\r' and not '\n' and not '\t'
      )
    )
    {
      throw new GitDeliveryException(
        "git-tag-invalid",
        "git-tag",
        "The tag name or annotation is invalid."
      );
    }
  }

  private static void ValidateObjectName(
    string value
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        value
      )
      || value.Length > 200
      || value.Any(
        character => !char.IsAsciiLetterOrDigit(
          character
        ) && character is not '-' and not '_' and not '.' and not '/' and not '^' and not '~'
      )
    )
    {
      throw new GitDeliveryException(
        "git-object-invalid",
        "git-object-validation",
        "The Git object name is invalid."
      );
    }
  }

  private static GitLogEntryView ParseLogEntry(
    string entry
  )
  {
    var fields = entry.Trim().Split(
      '\u001f'
    );
    return new GitLogEntryView(
      fields.ElementAtOrDefault(
        0
      ) ?? string.Empty,
      fields.ElementAtOrDefault(
        1
      ) ?? string.Empty,
      fields.ElementAtOrDefault(
        2
      ) ?? string.Empty,
      ParseDate(
        fields.ElementAtOrDefault(
          3
        )
      )
    );
  }

  private static async Task<GitDiffFileView> CreateUntrackedDiffAsync(
    string repositoryRoot,
    string path,
    int limit,
    CancellationToken cancellationToken
  )
  {
    var fullPath = Path.Combine(
      repositoryRoot,
      path
    );
    var file = new FileInfo(
      fullPath
    );
    if (!file.Exists)
    {
      throw new GitDeliveryException(
        "git-diff-file-unavailable",
        "git-diff",
        "An untracked file disappeared before its diff was read."
      );
    }
    var readLimit = Math.Min(
      limit,
      64 * 1_024
    );
    var bytes = new byte[Math.Min(
      file.Length,
      readLimit + 1
    )];
    await using var stream = new FileStream(
      fullPath,
      FileMode.Open,
      FileAccess.Read,
      FileShare.ReadWrite,
      16_384,
      FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    var read = await stream.ReadAsync(
      bytes,
      cancellationToken
    );
    var truncated = file.Length > readLimit;
    var payload = bytes.AsSpan(
      0,
      Math.Min(
        read,
        readLimit
      )
    );
    if (payload.Contains(
      (byte)0
    ))
    {
      return new GitDiffFileView(
        path,
        false,
        true,
        truncated,
        "[binary untracked file]"
      );
    }
    string text;
    try
    {
      text = new UTF8Encoding(
        false,
        true
      ).GetString(
        payload
      );
    }
    catch (DecoderFallbackException)
    {
      return new GitDiffFileView(
        path,
        false,
        true,
        truncated,
        "[binary untracked file]"
      );
    }
    var builder = new StringBuilder();
    builder.AppendLine(
      "--- /dev/null"
    );
    builder.AppendLine(
      $"+++ b/{path}"
    );
    builder.AppendLine(
      "@@ new untracked file @@"
    );
    foreach (var line in text.Replace(
      "\r\n",
      "\n",
      StringComparison.Ordinal
    ).Split(
      '\n'
    ))
    {
      builder.Append(
        '+'
      ).AppendLine(
        line
      );
    }
    if (truncated)
    {
      builder.AppendLine(
        "[diff truncated]"
      );
    }
    return new GitDiffFileView(
      path,
      false,
      false,
      truncated,
      builder.ToString()
    );
  }

  private static DateTimeOffset? ParseDate(
    string? value
  )
  {
    return DateTimeOffset.TryParse(
      value?.Trim(),
      CultureInfo.InvariantCulture,
      DateTimeStyles.RoundtripKind,
      out var parsed
    )
      ? parsed
      : null;
  }

  private static string ClassifyFailure(
    string stage,
    string diagnostic
  )
  {
    if (
      diagnostic.Contains(
        "Authentication failed",
        StringComparison.OrdinalIgnoreCase
      )
      || diagnostic.Contains(
        "could not read Username",
        StringComparison.OrdinalIgnoreCase
      )
      || diagnostic.Contains(
        "Permission denied",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      return "git-authentication-required";
    }
    if (
      diagnostic.Contains(
        "Could not resolve host",
        StringComparison.OrdinalIgnoreCase
      )
      || diagnostic.Contains(
        "unable to access",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      return "git-network-unavailable";
    }
    if (
      stage.Contains(
        "push",
        StringComparison.Ordinal
      )
      && diagnostic.Contains(
        "rejected",
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      return "git-push-rejected";
    }
    return stage switch
    {
      "git-commit" => "git-commit-failed",
      "git-tag" => "git-tag-failed",
      _ => "git-operation-failed"
    };
  }

  private static string Sanitize(
    string diagnostic,
    string root
  )
  {
    var sanitized = diagnostic.Replace(
      root,
      "[repository]",
      StringComparison.OrdinalIgnoreCase
    ).Trim();
    return sanitized.Length <= 2_000
      ? sanitized
      : sanitized[..2_000];
  }
}
