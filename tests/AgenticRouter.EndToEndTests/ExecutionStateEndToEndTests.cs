using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Benchmarking;
using AgenticRouter.Api.Execution;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class ExecutionStateEndToEndTests : ChatEndToEndTestBase<ExecutionStateEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitCardAndPanelExposeAuthoritativeBoundedRepositoryViews()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "baseline.txt"
      ),
      "working tree change"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "staged.txt"
      ),
      "staged change"
    );
    await RunGitAsync(
      "add",
      "--",
      "staged.txt"
    );
    await RunGitAsync(
      "remote",
      "set-url",
      "origin",
      "https://remote-user:remote-secret@example.test/repository.git?token=hidden"
    );
    await File.WriteAllBytesAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "binary.dat"
      ),
      [
        0,
        1,
        2,
        3,
        255
      ]
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "large.txt"
      ),
      new string(
        'x',
        80_000
      )
    );

    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#git-summary"
      )
    ).ToContainTextAsync(
      "main"
    );
    await Expect(
      Page.Locator(
        "#git-badge"
      )
    ).ToContainTextAsync(
      "changes"
    );
    await Page.Locator(
      "#git-card"
    ).FocusAsync();
    await Page.Locator(
      "#git-card"
    ).PressAsync(
      "Enter"
    );
    await Expect(
      Page.Locator(
        "#git-dialog"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        "#git-overview-section"
      )
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      Page.Locator(
        "#git-configuration-section"
      )
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      Page.Locator(
        "#git-overview"
      )
    ).ToContainTextAsync(
      "Latest commit"
    );
    await Expect(
      Page.Locator(
        "#git-overview"
      )
    ).ToContainTextAsync(
      "test baseline"
    );
    await Expect(
      Page.Locator(
        "#git-overview"
      )
    ).ToContainTextAsync(
      "origin/main"
    );
    await Expect(
      Page.Locator(
        "#git-overview"
      )
    ).ToContainTextAsync(
      "Git version"
    );
    await Expect(
      Page.Locator(
        "#git-remotes"
      )
    ).ToContainTextAsync(
      "example.test/repository.git"
    );
    await Expect(
      Page.Locator(
        "#git-remotes"
      )
    ).Not.ToContainTextAsync(
      "remote-secret"
    );
    await Expect(
      Page.Locator(
        "#git-remotes"
      )
    ).Not.ToContainTextAsync(
      "token=hidden"
    );
    await Page.Locator(
      "#git-configuration-section > summary"
    ).ClickAsync();

    await Page.Locator(
      "[data-git-view=\"working-tree\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).ToContainTextAsync(
      "baseline.txt"
    );
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).ToContainTextAsync(
      "binary.dat"
    );
    await Page.Locator(
      "#git-file-list button"
    ).Filter(
      new()
      {
        HasText = "binary.dat"
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-diff-metadata"
      )
    ).ToContainTextAsync(
      "binary"
    );
    await Page.Locator(
      "#git-file-list button"
    ).Filter(
      new()
      {
        HasText = "large.txt"
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-diff-metadata"
      )
    ).ToContainTextAsync(
      "truncated"
    );

    await Page.Locator(
      "[data-git-view=\"staged\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).ToContainTextAsync(
      "staged.txt"
    );
    await Page.Locator(
      "[data-git-view=\"last-commit\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).ToContainTextAsync(
      "baseline.txt"
    );

    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "external-refresh.txt"
      ),
      "external"
    );
    await Page.Locator(
      "#refresh-git"
    ).ClickAsync();
    await Page.Locator(
      "[data-git-view=\"working-tree\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).ToContainTextAsync(
      "external-refresh.txt"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitCurrentSessionViewExcludesPreExistingWorkspaceChanges()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    await File.AppendAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "preexisting.txt"
      ),
      "\nuser change"
    );
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );
    await Page.Locator(
      "#git-card"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).ToContainTextAsync(
      "hello.txt"
    );
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).Not.ToContainTextAsync(
      "preexisting.txt"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitInitializationAndIdentityRemainExplicitAndRepositoryScoped()
  {
    var globalBefore = await RunGitAllowFailureAsync(
      _environment.WorkspaceDirectory,
      "config",
      "--global",
      "--list"
    );
    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#git-badge"
      )
    ).ToHaveTextAsync(
      "Not initialized"
    );
    await Page.Locator(
      "#git-card"
    ).ClickAsync();
    await Expect(Page.Locator("#git-dialog")).ToBeVisibleAsync();
    await Page.Locator(
      "#initialize-git"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#app-modal"
      )
    ).ToContainTextAsync(
      "Execute mode"
    );
    Assert.IsFalse(
      Directory.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          ".git"
        )
      )
    );

    await Page.Locator(
      "#app-modal-confirm"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-dialog"
      )
    ).Not.ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".mode-option[data-mode=\"execute\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-pressed",
      "true"
    );
    await Page.Locator(
      "#git-card"
    ).ClickAsync();
    await Page.Locator(
      "#initialize-git"
    ).ClickAsync();
    Assert.IsFalse(
      Directory.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          ".git"
        )
      )
    );

    await Expect(Page.Locator("#app-modal")).ToBeVisibleAsync();
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(
      Page.Locator(
        "#git-action-status"
      )
    ).ToContainTextAsync(
      "Repository initialized on main"
    );
    Assert.AreEqual(
      "main",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "symbolic-ref",
        "--short",
        "HEAD"
      )
    );
    var head = await RunGitAllowFailureAsync(
      _environment.WorkspaceDirectory,
      "rev-parse",
      "--verify",
      "HEAD"
    );
    Assert.AreNotEqual(
      0,
      head.ExitCode
    );
    Assert.AreEqual(
      string.Empty,
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "status",
        "--short"
      )
    );
    Assert.AreEqual(
      string.Empty,
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "remote"
      )
    );

    await Page.Locator("#git-configuration-section > summary").ClickAsync();
    await Expect(Page.Locator("#git-user-name")).ToHaveAttributeAsync("readonly", string.Empty);
    await Expect(Page.Locator("#git-origin-url")).ToHaveAttributeAsync("readonly", string.Empty);
    await Page.Locator("#edit-git-configuration").ClickAsync();
    await Expect(Page.Locator("#git-user-name")).Not.ToHaveAttributeAsync("readonly", string.Empty);
    await Page.Locator("#git-user-name").FillAsync("Repository User");
    await Page.Locator("#git-user-email").FillAsync("repository-user@example.invalid");
    await Page.Locator("#git-origin-url").FillAsync("https://example.invalid/owner/repository.git");
    await Page.Locator("#save-git-configuration").ClickAsync();
    await Expect(Page.Locator("#app-modal")).ToContainTextAsync("origin");
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(
      Page.Locator(
        "#git-action-status"
      )
    ).ToContainTextAsync(
      "Local repository configuration saved.",
      new() { Timeout = 15_000 }
    );
    Assert.AreEqual(
      "Repository User",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "config",
        "--local",
        "user.name"
      )
    );
    Assert.AreEqual(
      "repository-user@example.invalid",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "config",
        "--local",
        "user.email"
      )
    );
    Assert.AreEqual(
      "https://example.invalid/owner/repository.git",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "remote",
        "get-url",
        "origin"
      )
    );
    using (
      var rejectedRemote = await _environment.HttpClient.PostAsJsonAsync(
        "api/git/remote/preview",
        new
        {
          remoteName = "origin",
          url = "https://user:secret@example.invalid/repository.git"
        }
      )
    )
    {
      Assert.AreEqual(
        HttpStatusCode.BadRequest,
        rejectedRemote.StatusCode
      );
      Assert.Contains(
        "git-remote-url-invalid",
        await rejectedRemote.Content.ReadAsStringAsync()
      );
    }
    using (
      var rejectedQuery = await _environment.HttpClient.PostAsJsonAsync(
        "api/git/remote/preview",
        new
        {
          remoteName = "origin",
          url = "https://example.invalid/repository.git?token=secret"
        }
      )
    )
    {
      Assert.AreEqual(
        HttpStatusCode.BadRequest,
        rejectedQuery.StatusCode
      );
    }
    var globalAfter = await RunGitAllowFailureAsync(
      _environment.WorkspaceDirectory,
      "config",
      "--global",
      "--list"
    );
    Assert.AreEqual(
      globalBefore,
      globalAfter
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitCardSurfacesDetachedHeadAndMergeConflicts()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    await RunGitAsync(
      "checkout",
      "--detach",
      "HEAD"
    );
    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#git-card"
      )
    ).ToHaveAttributeAsync(
      "aria-label",
      new Regex(
        "detached",
        RegexOptions.IgnoreCase
      )
    );

    await RunGitAsync(
      "checkout",
      "main"
    );
    await RunGitAsync(
      "checkout",
      "-b",
      "conflict-side"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "baseline.txt"
      ),
      "side"
    );
    await RunGitAsync(
      "add",
      "--",
      "baseline.txt"
    );
    await RunGitAsync(
      "commit",
      "-m",
      "side change"
    );
    await RunGitAsync(
      "checkout",
      "main"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "baseline.txt"
      ),
      "main"
    );
    await RunGitAsync(
      "add",
      "--",
      "baseline.txt"
    );
    await RunGitAsync(
      "commit",
      "-m",
      "main change"
    );
    var merge = await RunGitAllowFailureAsync(
      _environment.WorkspaceDirectory,
      "merge",
      "conflict-side"
    );
    Assert.AreNotEqual(
      0,
      merge.ExitCode
    );

    await Page.Locator(
      "#git-card"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-badge"
      )
    ).ToHaveTextAsync(
      "Conflicts"
    );
    await Expect(
      Page.Locator(
        "#git-overview"
      )
    ).ToContainTextAsync(
      "merge"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitBaselineDistinguishesPreExistingAndSessionChanges()
  {
    await RunGitAsync(
      "init"
    );
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    await File.WriteAllTextAsync(
      file,
      "hello"
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute write file"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".change-review-context"
      ).First
    ).ToContainTextAsync(
      "Pre-existing changes: hello.txt"
    );
    await Expect(
      Page.Locator(
        ".preexisting-change"
      )
    ).ToContainTextAsync(
      "already had changes"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliveryAskSeparatesSelectionAndRequiresStageAndUnstageApproval()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    await File.AppendAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "preexisting.txt"
      ),
      "\nuser change"
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    var createApproval = Page.Locator(".action-approval").Last;
    await Expect(createApproval).ToBeVisibleAsync();
    await createApproval.GetByRole(
      AriaRole.Button,
      new() { Name = "Approve", Exact = true }
    ).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    var panel = Page.Locator(
      ".git-delivery-panel"
    );
    await Expect(
      panel
    ).ToContainTextAsync(
      "main"
    );
    await Expect(
      panel.Locator(
        ".git-delivery-files"
      ).First
    ).ToContainTextAsync(
      "hello.txt"
    );
    await Expect(
      panel.Locator(
        ".git-delivery-files.preexisting"
      )
    ).ToContainTextAsync(
      "preexisting.txt"
    );
    await Expect(
      panel.Locator(
        ".delivery-file-selection[value=\"hello.txt\"]"
      )
    ).ToBeCheckedAsync();
    await Expect(
      panel.Locator(
        ".delivery-file-selection[value=\"preexisting.txt\"]"
      )
    ).Not.ToBeCheckedAsync();

    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Stage selected",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      panel.Locator(
        ".delivery-approval"
      )
    ).ToContainTextAsync(
      "Explicit approval required"
    );
    Assert.AreEqual(
      string.Empty,
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "diff",
        "--cached",
        "--name-only"
      )
    );
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve exact action",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      panel
    ).ToHaveAttributeAsync(
      "data-delivery-state",
      "validation-required"
    );
    Assert.AreEqual(
      "hello.txt",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "diff",
        "--cached",
        "--name-only"
      )
    );

    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Unstage selected",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      panel.Locator(
        ".delivery-approval"
      )
    ).ToBeVisibleAsync();
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve exact action",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      panel
    ).ToHaveAttributeAsync(
      "data-delivery-state",
      "changes-selected"
    );
    Assert.AreEqual(
      string.Empty,
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "diff",
        "--cached",
        "--name-only"
      )
    );
    await panel.Locator(
      ".delivery-file-selection[value=\"preexisting.txt\"]"
    ).CheckAsync();
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await Expect(
      panel.Locator(
        ".delivery-file-selection[value=\"preexisting.txt\"]"
      )
    ).ToBeCheckedAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliveryAutoStagesAndUnstagesWithoutDuplicateApproval()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    await Page.GotoAsync("/");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("execute create file");
    await Page.Locator(".review-changes").ClickAsync();
    var panel = Page.Locator(".git-delivery-panel");

    await panel.GetByRole(
      AriaRole.Button,
      new() { Name = "Stage selected", Exact = true }
    ).ClickAsync();
    await Expect(panel.Locator(".delivery-approval")).ToBeHiddenAsync();
    await Expect(panel).ToHaveAttributeAsync("data-delivery-state", "validation-required");
    Assert.AreEqual(
      "hello.txt",
      await RunGitTextAsync(_environment.WorkspaceDirectory, "diff", "--cached", "--name-only")
    );

    await panel.GetByRole(
      AriaRole.Button,
      new() { Name = "Unstage selected", Exact = true }
    ).ClickAsync();
    await Expect(panel.Locator(".delivery-approval")).ToBeHiddenAsync();
    await Expect(panel).ToHaveAttributeAsync("data-delivery-state", "changes-selected");
    Assert.AreEqual(
      string.Empty,
      await RunGitTextAsync(_environment.WorkspaceDirectory, "diff", "--cached", "--name-only")
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliveryUnstagesExactFilesBeforeTheInitialCommit()
  {
    await RunGitAsync(
      "init",
      "-b",
      "main"
    );
    await Page.GotoAsync("/");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("execute create file");
    await Page.Locator(".review-changes").ClickAsync();
    var panel = Page.Locator(".git-delivery-panel");

    await panel.GetByRole(
      AriaRole.Button,
      new() { Name = "Stage selected", Exact = true }
    ).ClickAsync();
    await Expect(panel).ToHaveAttributeAsync("data-delivery-state", "validation-required");
    Assert.AreEqual(
      "hello.txt",
      await RunGitTextAsync(_environment.WorkspaceDirectory, "diff", "--cached", "--name-only")
    );

    await panel.GetByRole(
      AriaRole.Button,
      new() { Name = "Unstage selected", Exact = true }
    ).ClickAsync();
    await Expect(panel).ToHaveAttributeAsync("data-delivery-state", "changes-selected");
    Assert.AreEqual(
      "?? hello.txt",
      await RunGitTextAsync(_environment.WorkspaceDirectory, "status", "--short", "--", "hello.txt")
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliveryCommitsTagsAndPushesExactFactsThroughDisposableRemote()
  {
    var remote = await InitializeDeliveryRepositoryAsync();
    using var save = await _environment.HttpClient.PutAsJsonAsync(
      "api/workspace/validation-profile",
      new
      {
        name = "Delivery validation",
        source = "user",
        steps = new[]
        {
          new
          {
            id = "version",
            label = "Check dotnet version",
            executable = "dotnet",
            arguments = new[]
            {
              "--version"
            },
            workingDirectory = ".",
            timeoutSeconds = 30,
            required = true
          }
        }
      }
    );
    save.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file validate"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    var panel = Page.Locator(
      ".git-delivery-panel"
    );
    await Expect(
      panel.Locator(
        ".delivery-validation"
      )
    ).ToContainTextAsync(
      "Validation bound"
    );
    await panel.Locator(
      ".delivery-commit-message"
    ).FillAsync(
      "feat: deliver hello"
    );
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await StageDeliveryAsync(
      panel
    );
    await Expect(
      panel
    ).ToHaveAttributeAsync(
      "data-delivery-state",
      "ready-to-commit"
    );

    await ApproveDeliveryOperationAsync(
      panel,
      "Create commit"
    );
    await Expect(
      panel
    ).ToHaveAttributeAsync(
      "data-delivery-state",
      "committed"
    );
    await Expect(
      panel.Locator(
        ".delivery-facts"
      )
    ).ToContainTextAsync(
      "feat: deliver hello"
    );
    await Expect(
      Page.Locator(
        "#undo-execution"
      )
    ).ToBeDisabledAsync();
    var commit = await RunGitTextAsync(
      _environment.WorkspaceDirectory,
      "rev-parse",
      "HEAD"
    );
    Assert.AreEqual(
      "feat: deliver hello",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "show",
        "-s",
        "--format=%s",
        "HEAD"
      )
    );

    await panel.Locator(
      ".delivery-tag-name"
    ).FillAsync(
      "v-test-0.9.0"
    );
    await panel.Locator(
      ".delivery-tag-annotation"
    ).FillAsync(
      "Disposable delivery tag"
    );
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create annotated tag"
    );
    await Expect(
      panel
    ).ToHaveAttributeAsync(
      "data-delivery-state",
      "tagged"
    );
    Assert.AreEqual(
      "tag",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "cat-file",
        "-t",
        "v-test-0.9.0"
      )
    );
    Assert.AreEqual(
      commit,
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "rev-list",
        "-n",
        "1",
        "v-test-0.9.0"
      )
    );

    await ApproveDeliveryOperationAsync(
      panel,
      "Push current branch"
    );
    await Expect(
      panel
    ).ToHaveAttributeAsync(
      "data-delivery-state",
      "partially-pushed"
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Push exact tag"
    );
    await Expect(
      panel
    ).ToHaveAttributeAsync(
      "data-delivery-state",
      "pushed"
    );
    Assert.AreEqual(
      commit,
      await RunGitTextAsync(
        remote,
        "rev-parse",
        "refs/heads/main"
      )
    );
    Assert.AreEqual(
      commit,
      await RunGitTextAsync(
        remote,
        "rev-list",
        "-n",
        "1",
        "refs/tags/v-test-0.9.0"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliveryMarksValidationStaleAndBlocksCommit()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    using var save = await _environment.HttpClient.PutAsJsonAsync(
      "api/workspace/validation-profile",
      new
      {
        name = "Delivery validation",
        source = "user",
        steps = new[]
        {
          new
          {
            id = "version",
            label = "Check dotnet version",
            executable = "dotnet",
            arguments = new[]
            {
              "--version"
            },
            workingDirectory = ".",
            timeoutSeconds = 30,
            required = true
          }
        }
      }
    );
    save.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file validate"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    var panel = Page.Locator(
      ".git-delivery-panel"
    );
    await panel.Locator(
      ".delivery-commit-message"
    ).FillAsync(
      "feat: stale delivery"
    );
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await StageDeliveryAsync(
      panel
    );
    await File.AppendAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "\nexternal edit"
    );
    await Page.Locator(
      "#close-change-review"
    ).ClickAsync();
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    panel = Page.Locator(
      ".git-delivery-panel"
    );
    await Expect(
      panel.Locator(
        ".delivery-validation"
      )
    ).ToContainTextAsync(
      "Validation stale"
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create commit",
      false
    );
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "Validation is stale"
    );
    Assert.AreEqual(
      "1",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "rev-list",
        "--count",
        "HEAD"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliveryRejectsMissingApprovalStaleActionAndOutsidePath()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    var createApproval = Page.Locator(".action-approval").Last;
    await Expect(createApproval).ToBeVisibleAsync();
    await createApproval.GetByRole(
      AriaRole.Button,
      new() { Name = "Approve", Exact = true }
    ).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".git-delivery-panel"
      )
    ).ToBeVisibleAsync();
    var executionSessionId = await Page.EvaluateAsync<string>(
      "() => state.activeReview.summary.id"
    );
    var browserSessionId = await Page.EvaluateAsync<string>(
      "() => state.browserSessionId"
    );
    using var current = await _environment.HttpClient.GetAsync(
      $"api/execution-sessions/{executionSessionId}/delivery"
    );
    current.EnsureSuccessStatusCode();
    using var currentDocument = JsonDocument.Parse(
      await current.Content.ReadAsStringAsync()
    );
    var stageActionId = currentDocument.RootElement.GetProperty(
      "stageActionId"
    ).GetString()!;

    using var missingApproval = await _environment.HttpClient.PostAsJsonAsync(
      $"api/execution-sessions/{executionSessionId}/delivery/stage",
      new
      {
        browserSessionId,
        actionId = stageActionId,
        confirmed = false
      }
    );
    Assert.AreEqual(
      HttpStatusCode.Conflict,
      missingApproval.StatusCode
    );
    using var missingDocument = JsonDocument.Parse(
      await missingApproval.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "git-approval-required",
      missingDocument.RootElement.GetProperty(
        "code"
      ).GetString()
    );

    using var outside = await _environment.HttpClient.PostAsJsonAsync(
      $"api/execution-sessions/{executionSessionId}/delivery/diff",
      new
      {
        paths = new[]
        {
          "../outside.txt"
        },
        staged = false
      }
    );
    Assert.AreEqual(
      HttpStatusCode.Conflict,
      outside.StatusCode
    );
    using var outsideDocument = JsonDocument.Parse(
      await outside.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "git-selected-path-outside-repository",
      outsideDocument.RootElement.GetProperty(
        "code"
      ).GetString()
    );

    using var changed = await _environment.HttpClient.PostAsJsonAsync(
      $"api/execution-sessions/{executionSessionId}/delivery/selection",
      new
      {
        browserSessionId,
        selectedFiles = new[]
        {
          "hello.txt"
        },
        includePreExistingChanges = false,
        commitMessage = "changed after approval",
        tag = (string?)null,
        tagAnnotation = (string?)null,
        commitWithoutValidation = false
      }
    );
    changed.EnsureSuccessStatusCode();
    using var stale = await _environment.HttpClient.PostAsJsonAsync(
      $"api/execution-sessions/{executionSessionId}/delivery/stage",
      new
      {
        browserSessionId,
        actionId = stageActionId,
        confirmed = true
      }
    );
    Assert.AreEqual(
      HttpStatusCode.Conflict,
      stale.StatusCode
    );
    using var staleDocument = JsonDocument.Parse(
      await stale.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "git-approval-invalidated",
      staleDocument.RootElement.GetProperty(
        "code"
      ).GetString()
    );
    Assert.AreEqual(
      string.Empty,
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "diff",
        "--cached",
        "--name-only"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitPushPreflightBlocksDivergedDisposableRemoteWithoutRewriting()
  {
    var remote = await InitializeDeliveryRepositoryAsync();
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    var panel = Page.Locator(
      ".git-delivery-panel"
    );
    await panel.Locator(
      ".delivery-commit-message"
    ).FillAsync(
      "feat: local delivery"
    );
    await panel.Locator(
      ".delivery-commit-override"
    ).CheckAsync();
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await StageDeliveryAsync(
      panel
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create commit"
    );
    var localCommit = await RunGitTextAsync(
      _environment.WorkspaceDirectory,
      "rev-parse",
      "HEAD"
    );

    var competingClone = _environment.CreateWorkspaceDirectory(
      $"delivery-clone-{Guid.NewGuid():N}"
    );
    _ = await RunGitTextAsync(
      competingClone,
      "clone",
      remote,
      "."
    );
    _ = await RunGitTextAsync(
      competingClone,
      "config",
      "user.name",
      "Competing E2E"
    );
    _ = await RunGitTextAsync(
      competingClone,
      "config",
      "user.email",
      "competing@example.invalid"
    );
    await File.AppendAllTextAsync(
      Path.Combine(
        competingClone,
        "baseline.txt"
      ),
      "\nremote change"
    );
    _ = await RunGitTextAsync(
      competingClone,
      "add",
      "--",
      "baseline.txt"
    );
    _ = await RunGitTextAsync(
      competingClone,
      "commit",
      "-m",
      "competing remote commit"
    );
    _ = await RunGitTextAsync(
      competingClone,
      "push",
      "origin",
      "main"
    );
    var remoteCommit = await RunGitTextAsync(
      remote,
      "rev-parse",
      "refs/heads/main"
    );

    await ApproveDeliveryOperationAsync(
      panel,
      "Push current branch",
      false
    );
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "diverged"
    );
    Assert.AreEqual(
      localCommit,
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "rev-parse",
        "HEAD"
      )
    );
    Assert.AreEqual(
      remoteCommit,
      await RunGitTextAsync(
        remote,
        "rev-parse",
        "refs/heads/main"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliveryBoundsUntrackedDiffAndReportsTruncation()
  {
    using var settings = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        GitDelivery = new TestGitDeliverySettings
        {
          MaxDiffBytesPerFile = 4_096
        }
      }
    );
    settings.EnsureSuccessStatusCode();
    _ = await InitializeDeliveryRepositoryAsync();
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "large.txt"
      ),
      new string(
        'x',
        20_000
      )
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#change-review-body"
      )
    ).ToContainTextAsync(
      "large.txt"
    );
    var executionSessionId = await Page.EvaluateAsync<string>(
      "() => state.activeReview.summary.id"
    );
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      $"api/execution-sessions/{executionSessionId}/delivery/diff",
      new
      {
        paths = new[]
        {
          "large.txt"
        },
        staged = false
      }
    );
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await response.Content.ReadAsStringAsync()
    );
    var file = document.RootElement.GetProperty(
      "files"
    )[0];
    Assert.IsTrue(
      file.GetProperty(
        "truncated"
      ).GetBoolean()
    );
    Assert.IsFalse(
      file.GetProperty(
        "binary"
      ).GetBoolean()
    );
    var content = file.GetProperty(
      "content"
    ).GetString()!;
    StringAssert.Contains(
      content,
      "[diff truncated]"
    );
    Assert.IsLessThan(
8_500,
      content.Length, $"Bounded diff was unexpectedly large: {content.Length}."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitCommitAndTagPreflightRejectsInvalidInputsAndDetachedHead()
  {
    _ = await InitializeDeliveryRepositoryAsync();
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    var panel = Page.Locator(
      ".git-delivery-panel"
    );
    await panel.Locator(
      ".delivery-commit-message"
    ).FillAsync(
      string.Empty
    );
    await panel.Locator(
      ".delivery-commit-override"
    ).CheckAsync();
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await StageDeliveryAsync(
      panel
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create commit",
      false
    );
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "cannot be empty"
    );
    await panel.Locator(
      ".delivery-commit-message"
    ).FillAsync(
      "feat: guarded commit"
    );
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await RunGitAsync(
      "checkout",
      "--detach"
    );
    await Page.Locator(
      "#close-change-review"
    ).ClickAsync();
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    panel = Page.Locator(
      ".git-delivery-panel"
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create commit",
      false
    );
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "detached"
    );
    Assert.AreEqual(
      "1",
      await RunGitTextAsync(
        _environment.WorkspaceDirectory,
        "rev-list",
        "--count",
        "HEAD"
      )
    );
    await RunGitAsync(
      "checkout",
      "main"
    );
    await Page.Locator(
      "#close-change-review"
    ).ClickAsync();
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    panel = Page.Locator(
      ".git-delivery-panel"
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create commit"
    );

    await panel.Locator(
      ".delivery-tag-name"
    ).FillAsync(
      "invalid tag"
    );
    await panel.Locator(
      ".delivery-tag-annotation"
    ).FillAsync(
      "Invalid tag test"
    );
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create annotated tag",
      false
    );
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "tag name is invalid"
    );
    await RunGitAsync(
      "tag",
      "-a",
      "existing-tag",
      "-m",
      "External tag"
    );
    await panel.Locator(
      ".delivery-tag-name"
    ).FillAsync(
      "existing-tag"
    );
    await panel.Locator(
      ".delivery-tag-annotation"
    ).FillAsync(
      "Existing tag test"
    );
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save selection",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "selection saved"
    );
    await ApproveDeliveryOperationAsync(
      panel,
      "Create annotated tag",
      false
    );
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "already exists locally"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SpecialistInvestigatesEditsBuildsAndTestsInOneDirectLoop()
  {
    await File.WriteAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "Sample.csproj"),
      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>"
    );
    await File.WriteAllTextAsync(
      Path.Combine(_environment.WorkspaceDirectory, "Program.cs"),
      "Console.WriteLine(\"broken\";\n"
    );
    using var save = await _environment.HttpClient.PutAsJsonAsync(
      "api/workspace/validation-profile",
      new
      {
        name = "Build and test",
        source = "user",
        steps = new[]
        {
          new
          {
            id = "build",
            label = "Build sample project",
            executable = "dotnet",
            arguments = new[]
            {
              "build",
              "Sample.csproj",
              "-c",
              "Release"
            },
            workingDirectory = ".",
            timeoutSeconds = 30,
            required = true
          },
          new
          {
            id = "test",
            label = "Test sample project",
            executable = "dotnet",
            arguments = new[]
            {
              "test",
              "Sample.csproj",
              "-c",
              "Release",
              "--no-build"
            },
            workingDirectory = ".",
            timeoutSeconds = 30,
            required = true
          }
        }
      }
    );
    save.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute write file coding task validate"
    );
    Assert.AreEqual(
      "Console.WriteLine(\"fixed\");\n",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "Program.cs")
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"validation-completed\"]"
      )
    ).ToContainTextAsync(
      "Validation passed"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".validation-results"
      )
    ).ToContainTextAsync(
      "Build sample project: passed"
    );
    await Expect(
      Page.Locator(
        ".validation-results"
      )
    ).ToContainTextAsync(
      "Test sample project: passed"
    );
    await Expect(
      Page.Locator(
        ".change-review-summary"
      )
    ).ToContainTextAsync(
      "implemented-and-validated"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RequiredValidationFailureBlocksSuccessfulCompletionClaim()
  {
    using var save = await _environment.HttpClient.PutAsJsonAsync(
      "api/workspace/validation-profile",
      new
      {
        name = "Required failure",
        source = "user",
        steps = new[]
        {
          new
          {
            id = "build",
            label = "Build missing project",
            executable = "dotnet",
            arguments = new[]
            {
              "build",
              "missing-project.csproj",
              "-c",
              "Release"
            },
            workingDirectory = ".",
            timeoutSeconds = 30,
            required = true
          }
        }
      }
    );
    save.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file validate"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"validation-completed\"]"
      )
    ).ToContainTextAsync(
      "Validation failed"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      ).Last
    ).ToContainTextAsync(
      "Implemented; validation failed."
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".change-review-summary"
      )
    ).ToContainTextAsync(
      "implemented-validation-failed"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExecutionSessionReviewsVerifiedChangeAndUndoesCreatedFile()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    Assert.IsTrue(
      File.Exists(
        file
      )
    );
    await Expect(
      Page.Locator(
        ".execution-session-header"
      )
    ).ToContainTextAsync(
      "1 files"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#change-review-dialog"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".change-file-review"
      )
    ).ToContainTextAsync(
      "hello.txt"
    );
    await Expect(
      Page.Locator(
        ".verification-ok"
      )
    ).ToContainTextAsync(
      "Verified"
    );
    await Page.Locator(
      "#undo-execution"
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "undone"
    );
    Assert.IsFalse(
      File.Exists(
        file
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UndoDetectsConflictBeforeChangingAnyFile()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    await File.WriteAllTextAsync(
      file,
      "external change"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Page.Locator(
      "#undo-execution"
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "conflicts were detected"
    );
    Assert.AreEqual(
      "external change",
      await File.ReadAllTextAsync(
        file
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UndoRestoresOriginalContentForModifiedFile()
  {
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    await File.WriteAllTextAsync(
      file,
      "original"
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute write file"
    );
    Assert.AreEqual(
      "rewritten by agent",
      await File.ReadAllTextAsync(
        file
      )
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Page.Locator(
      "#undo-execution"
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "undone"
    );
    Assert.AreEqual(
      "original",
      await File.ReadAllTextAsync(
        file
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ProcessJournalAppearsInChangeReview()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute run process"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".process-review"
      )
    ).ToContainTextAsync(
      "dotnet --version"
    );
    await Expect(
      Page.Locator(
        "#undo-execution"
      )
    ).ToBeDisabledAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ApprovalFromWrongExecutionSessionIsRejected()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    var approval = Page.Locator(
      ".action-approval"
    );
    await Expect(
      approval
    ).ToBeVisibleAsync();
    var actionId = await approval.GetAttributeAsync(
      "data-action-id"
    );
    var status = await Page.EvaluateAsync<int>(
      """
      async actionId => {
        const response = await fetch(`/api/actions/${encodeURIComponent(actionId)}/decision`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            approved: true,
            browserSessionId: "wrong-browser",
            executionSessionId: "wrong-execution"
          })
        });
        return response.status;
      }
      """,
      actionId
    );
    Assert.AreEqual(
      404,
      status
    );
    var revisionStatus = await Page.EvaluateAsync<int>(
      """
      async actionId => {
        const response = await fetch(`/api/actions/${encodeURIComponent(actionId)}/revision`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            editedText: "dotnet --version",
            browserSessionId: "wrong-browser",
            executionSessionId: "wrong-execution"
          })
        });
        return response.status;
      }
      """,
      actionId
    );
    Assert.AreEqual(
      404,
      revisionStatus
    );
    Assert.IsFalse(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.edit-applied\"]"
      )
    ).ToBeAttachedAsync();
    await Expect(
      approval
    ).ToHaveAttributeAsync(
      "data-decision",
      "completed"
    );
    await Expect(
      approval
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Approve",
          Exact = true
        }
      )
    ).ToHaveCountAsync(
      0
    );
    var response = approval.Locator(
      ".action-response"
    );
    await Expect(
      response
    ).ToBeAttachedAsync();
    await Expect(
      response
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Send"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ApprovalCardCollapsesAfterRejectionAndRemainsExpandable()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    var approval = Page.Locator(
      ".action-approval"
    );
    await Expect(
      approval
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    var approvalWidth = await approval.EvaluateAsync<double>(
      "element => element.getBoundingClientRect().width"
    );
    var approvalSummary = approval.Locator(
      ":scope > summary"
    );
    var summaryWidth = await approvalSummary.EvaluateAsync<double>(
      "element => element.getBoundingClientRect().width"
    );
    var summaryHeight = await approvalSummary.EvaluateAsync<double>(
      "element => element.getBoundingClientRect().height"
    );
    Assert.IsGreaterThanOrEqualTo(
      approvalWidth * 0.9,
      summaryWidth
    );
    Assert.IsLessThanOrEqualTo(
      80,
      summaryHeight
    );
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Reject",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      approval
    ).ToHaveAttributeAsync(
      "data-decision",
      "rejected"
    );
    await Expect(
      approval
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    var activity = Page.Locator(
      ".message.assistant .activity"
    ).Last;
    await Expect(
      activity.Locator(":scope > summary")
    ).ToContainTextAsync("Completed");
    if (await activity.GetAttributeAsync("open") is null)
    {
      await activity.Locator(":scope > summary").ClickAsync();
    }
    await approval.Locator(
      "summary"
    ).ClickAsync();
    await Expect(
      approval
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      approval
    ).ToContainTextAsync(
      "Rejected"
    );
    await Expect(
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Reject",
          Exact = true
        }
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsFalse(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Send"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NewExecuteRequestCancelsOlderSessionBeforeItsApprovedActionRuns()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    await Expect(
      Page.Locator(
        ".action-approval"
      )
    ).ToBeVisibleAsync();
    var browserSessionId = await Page.EvaluateAsync<string>(
      "() => state.browserSessionId"
    );
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "execute create file",
        model = "auto",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        approvalPolicy = "auto",
        browserSessionId
      }
    );
    response.EnsureSuccessStatusCode();
    await Expect(
      Page.Locator(
        ".assistant-answer.error"
      )
    ).ToContainTextAsync(
      "replaced by a newer request"
    );
    Assert.AreEqual(
      "hello from agent",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CompletedWriteUsesHostTerminalResponseAndKeepsChangeReviewAvailable()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file cancel stream"
    );
    await Expect(
      Page.Locator(
        ".activity [data-event-type=\"action.edit-applied\"]"
      )
    ).ToBeAttachedAsync();
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "cancel stream",
              StringComparison.OrdinalIgnoreCase
            )
          )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"response.completed\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        ".review-changes"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".change-file-review"
      )
    ).ToContainTextAsync(
      "hello.txt"
    );
    Assert.AreEqual(
      "hello from agent",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BoundedMetadataAndTextSearchUseInternalTools()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "hello search"
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute file info"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      ).Last
    ).ToContainTextAsync(
      "Inspected: hello.txt."
    );
    await SendMessageAsync(
      "execute search text"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      ).Last
    ).ToContainTextAsync(
      "Search completed in '.'"
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Content.Contains(
            "\"tool\":\"run_process\"",
            StringComparison.Ordinal
          )
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FileMutationIsOneExpandableUserFacingAction()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute retry unknown tool create file"
    );

    var technicalDetails = Page.Locator(
      ".message.assistant .activity"
    );
    await Expect(
      technicalDetails
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    var action = Page.Locator(
      ".message.assistant .work-action"
    );
    await Expect(
      action
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      action
    ).ToHaveAttributeAsync(
      "data-state",
      "completed"
    );
    await Expect(
      action.Locator(
        ":scope > summary"
      )
    ).ToContainTextAsync(
      "Create"
    );
    await Expect(
      action.Locator(
        ".work-action-file"
      )
    ).ToHaveTextAsync(
      "hello.txt"
    );
    await action.Locator(
      ".work-action-label"
    ).ClickAsync();
    await Expect(
      action.Locator(
        ".work-action-preview"
      )
    ).ToContainTextAsync(
      "hello from agent"
    );
    await Expect(
      action.Locator(
        ".work-action-path"
      )
    ).ToContainTextAsync(
      _environment.WorkspaceDirectory
    );
    await action.Locator(
      ".work-action-file"
    ).ClickAsync();
    var reviewedFile = Page.Locator(
      ".change-file-review[data-relative-path=\"hello.txt\"]"
    );
    await Expect(
      reviewedFile
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Page.Locator(
      "#close-change-review"
    ).ClickAsync();
    if (await Page.Locator("#cancel-request").IsVisibleAsync())
    {
      await Page.Locator(
        "#cancel-request"
      ).ClickAsync();
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ToolActivityIsCollapsedAndLongDurationsRemainCompact()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "private file contents"
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute read file"
    );

    var output = Page.Locator(
      "[data-event-type=\"action.output\"]"
    ).Last;
    var group = Page.Locator(
      ".activity-group"
    ).Filter(
      new()
      {
        Has = output
      }
    );
    await Expect(
      group
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      group.Locator(
        ":scope > summary"
      )
    ).ToContainTextAsync(
      "read_file: hello.txt"
    );
    Assert.AreEqual(
      "0px",
      await group.EvaluateAsync<string>(
        "element => getComputedStyle(element).borderTopWidth"
      )
    );
    await Expect(
      output
    ).Not.ToContainTextAsync(
      "private file contents"
    );
    Assert.AreEqual(
      "3 min 42 s",
      await Page.EvaluateAsync<string>(
        "() => formatElapsed(221793)"
      )
    );
    Assert.AreEqual(
      "nowrap",
      await Page.Locator(
        ".activity-time"
      ).First.EvaluateAsync<string>(
        "element => getComputedStyle(element).whiteSpace"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PathValidationDenialIsReturnedForSafeReplanning()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute path traversal recover"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      )
    ).ToContainTextAsync(
      "was not permitted and was not executed"
    );
    await Expect(
      Page.Locator(
        ".activity [data-event-type=\"action.edit-applied\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    Assert.AreEqual(
      "recovered inside trusted workspace",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "safe.txt"
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-limit\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RelativeTraversalCreationIsDeniedThenRecoveredInsideTrustedWorkspace()
  {
    var outsideCandidate = Path.GetFullPath(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "..",
        "..",
        "rebased-create.txt"
      )
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute path traversal create corrected"
    );

    Assert.AreEqual(
      "created inside the trusted workspace",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "rebased-create.txt"
        )
      )
    );
    Assert.IsFalse(
      File.Exists(
        outsideCandidate
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      )
    ).ToContainTextAsync(
      "outside the trusted workspace"
    );

    var executionId = await Page.EvaluateAsync<string>(
      "() => state.latestExecutionSessionId"
    );
    using var review = await _environment.HttpClient.GetAsync(
      $"api/execution-sessions/{executionId}/review"
    );
    review.EnsureSuccessStatusCode();
    using var reviewDocument = JsonDocument.Parse(
      await review.Content.ReadAsStringAsync()
    );
    Assert.IsFalse(
      reviewDocument.RootElement.GetProperty("warnings")
        .EnumerateArray().Any(
          warning => warning.GetString()?.Contains(
            "rebased the creation inside its root",
            StringComparison.Ordinal
          ) == true
        )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task JsonEscapedControlCharacterInPathIsRejectedBeforeExecution()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute control character path recover"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      )
    ).ToContainTextAsync(
      "control character U+000C"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.AreEqual(
      "recovered after invalid control path",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "safe-control-path.txt"
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task JsonEscapedControlCharacterInProcessArgumentIsRejectedBeforeExecution()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute control character process recover"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.policy-denied\"]"
      ).Last
    ).ToContainTextAsync(
      "control character U+000C"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.AreEqual(
      "recovered after invalid process argument",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "safe-control-process.txt"
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PathTraversalAndDestructiveCommandsAreBlocked()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute path traversal"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      )
    ).ToHaveCountAsync(
      2
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      ).Last
    ).ToContainTextAsync(
      "repeated an identical denied proposal"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      ).First
    ).ToContainTextAsync(
      "outside the trusted workspace"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-decision-required\"]"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      "[data-event-type=\"action.recovery-decision-required\"] "
        + "[data-recovery-option=\"stop\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-stopped\"]"
      )
    ).ToBeAttachedAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"response.completed\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator("#send-button-label")
    ).ToHaveTextAsync("Send");
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "New conversation",
        Exact = true
      }
    ).ClickAsync();
    var discard = Page.Locator(
      "#new-conversation-discard"
    );
    await Page.WaitForTimeoutAsync(100);
    if (await discard.IsVisibleAsync())
    {
      await discard.ClickAsync();
    }
    await Expect(
      Page.Locator("[data-mode=\"execute\"]")
    ).ToBeEnabledAsync();
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute destructive process"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.policy-denied\"]"
      ).Last
    ).ToContainTextAsync(
      "blocked"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-started\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DirectGitMetadataMutationIsDeterministicallyBlocked()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute git metadata access"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      ).First
    ).ToContainTextAsync(
      "Direct filesystem access to .git metadata is not allowed"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-started\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-decision-required\"]"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      "[data-event-type=\"action.recovery-decision-required\"] "
        + "[data-recovery-option=\"stop\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"response.completed\"]"
      )
    ).ToHaveCountAsync(
      1
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RepeatedIdenticalPolicyDenialStopsWithoutPlanningFailure()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute repeat denied process"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.policy-denied\"]"
      )
    ).ToHaveCountAsync(
      2
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.policy-denied\"]"
      ).Last
    ).ToContainTextAsync(
      "repeated an identical denied proposal"
    );
    await Expect(
      Page.Locator(
        ".execution-session-header"
      )
    ).ToContainTextAsync(
      "planning 0"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-started\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Page.Locator(
      "[data-event-type=\"action.recovery-decision-required\"] "
        + "[data-recovery-option=\"stop\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-stopped\"]"
      )
    ).ToBeAttachedAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ProviderPlanningFailureDoesNotIncrementOrTriggerHandoff()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute planner provider failure"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.provider-error\"]"
      )
    ).ToBeAttachedAsync();
    await Expect(
      Page.Locator(
        ".execution-session-header"
      )
    ).ToContainTextAsync(
      "planning 0"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-fallback\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task KeywordAutoRoutingSelectsConfiguredApplicationIntentionWithoutModelRouter()
  {
    _environment.FakeOllama.Reset();
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "architect the service boundaries"
    );

    await Expect(
      Page.Locator(
        "[data-event-type=\"router.classified\"]"
      )
    ).ToContainTextAsync(
      "software-architecture"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"target.configuration\"]"
      )
    ).ToContainTextAsync(
      "software-architecture"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"target.model-resolved\"]"
      )
    ).ToContainTextAsync(
      "beta:code"
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(request =>
        request.Model is "router:latest" or "functiongemma:270m"
      )
    );
  }
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExplicitExecuteModelIgnoresLegacyRouterSettingsAndNegotiatesMinimalToolSchemas()
  {
    using (
      var settings = await _environment.PutSettingsAsync(
        _environment.BaselineSettings with
        {
          RouterModel = "functiongemma:270m",
          ActionModel = "functiongemma:270m",
          CoordinatorModel = "router:latest"
        }
      )
    )
    {
      settings.EnsureSuccessStatusCode();
    }
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "qwen3-coder:30b"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );

    await Expect(
      Page.Locator(
        ".model-selection-note"
      )
    ).ToHaveTextAsync(
      "Model qwen3-coder:30b selected by the user."
    );
    await Expect(
      Page.Locator(
        "[data-event-type^=\"agent.functiongemma-routing\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "functiongemma:270m"
          && request.AvailableTools.Contains(
            "route_to_teacher",
            StringComparer.Ordinal
          )
      )
    );

    var plannerRequests = _environment.FakeOllama.Requests.Where(
      request => request.Model == "qwen3-coder:30b"
        && request.Messages.Any(
          message => message.Content.Contains(
            LocalActionPlanner.PlannerMarker,
            StringComparison.Ordinal
          )
        )
    ).ToArray();
    Assert.IsGreaterThanOrEqualTo(
      5,
      plannerRequests.Length
    );
    CollectionAssert.AreEqual(
      new[]
      {
        LocalActionPlanner.RequestToolsetTool
      },
      plannerRequests[0].AvailableTools.ToArray()
    );
    StringAssert.Contains(
      plannerRequests[0].Messages[0].Content,
      LocalActionPlanner.ToolCatalogMarker
    );
    StringAssert.Contains(
      plannerRequests[0].Messages[0].Content,
      "create_file(path, content)"
    );
    Assert.IsTrue(
      plannerRequests.Any(
        request => request.AvailableTools.SequenceEqual(
          [
            LocalActionPlanner.RequestToolsetTool,
            "create_file"
          ]
        )
      )
    );
    Assert.IsTrue(
      plannerRequests.Any(
        request => request.AvailableTools.SequenceEqual(
          [
            LocalActionPlanner.RequestToolsetTool,
            "read_file",
            "create_file"
          ]
        )
      )
    );
    Assert.IsTrue(
      plannerRequests.All(
        request => request.AvailableTools.All(
          tool => tool is LocalActionPlanner.RequestToolsetTool
            or "create_file"
            or "read_file"
        )
      )
    );
    await Expect(
      Page.Locator(
        ".assistant-toolset-request"
      )
    ).ToHaveCountAsync(
      2
    );
    await Expect(
      Page.Locator(
        ".assistant-toolset-request"
      ).Nth(0)
    ).ToContainTextAsync(
      "create_file"
    );
    await Expect(
      Page.Locator(
        ".assistant-toolset-request"
      ).Nth(1)
    ).ToContainTextAsync(
      "read_file"
    );
    Assert.AreEqual(
      "hello from agent",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );

    var usageDirectory = Path.Combine(
      _environment.DataDirectory,
      "usage"
    );
    var usageEvents = Directory.Exists(
      usageDirectory
    )
      ? Directory.GetFiles(
        usageDirectory,
        "*.jsonl"
      ).SelectMany(
        File.ReadAllLines
      ).Where(
        line => !string.IsNullOrWhiteSpace(
          line
        )
      ).Select(
        line => JsonNode.Parse(
          line
        )
      ).OfType<JsonObject>().ToArray()
      : [];
    Assert.IsFalse(
      usageEvents.Any(
        usage => usage["requestPurpose"]?.GetValue<string>() is
          "functiongemma-routing" or "functiongemma-routing-repair"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnknownToolsetNameIsRejectedExactlyWithoutWorkspaceAction()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "qwen3-coder:30b"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute unknown tool alias"
    );

    var rejection = Page.Locator(
      "[data-event-type=\"agent.toolset-request-rejected\"]"
    ).First;
    await Expect(
      rejection
    ).ToContainTextAsync(
      "open_file"
    );
    await Expect(
      rejection
    ).ToContainTextAsync(
      "neither canonical nor an approved alias"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-started\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsFalse(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
    var firstPlannerRequest = _environment.FakeOllama.Requests.First(
      request => request.Model == "qwen3-coder:30b"
        && request.Messages.Any(
          message => message.Content.Contains(
            LocalActionPlanner.PlannerMarker,
            StringComparison.Ordinal
          )
        )
    );
    CollectionAssert.AreEqual(
      new[]
      {
        LocalActionPlanner.RequestToolsetTool
      },
      firstPlannerRequest.AvailableTools.ToArray()
    );
    if (await Page.Locator("#cancel-request").IsVisibleAsync())
    {
      await Page.Locator(
        "#cancel-request"
      ).ClickAsync();
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExecuteSpecialistCanCompleteWithoutRequestingAnyToolset()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "qwen3-coder:30b"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "explain why no local action is required"
    );

    var plannerRequests = _environment.FakeOllama.Requests.Where(
      request => request.Model == "qwen3-coder:30b"
        && request.Messages.Any(
          message => message.Content.Contains(
            LocalActionPlanner.PlannerMarker,
            StringComparison.Ordinal
          )
        )
    ).ToArray();
    Assert.HasCount(
      1,
      plannerRequests
    );
    CollectionAssert.AreEqual(
      new[]
      {
        LocalActionPlanner.RequestToolsetTool
      },
      plannerRequests[0].AvailableTools.ToArray()
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.toolset-requested\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-started\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        ".message.assistant > .execution-plan"
      )
    ).ToBeHiddenAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SpecialistProposedPlanRendersAuthoritativeProgressOutsideTechnicalDetails()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("execute specialist tracked plan create file");

    var plan = Page.Locator(".message.assistant > .execution-plan");
    await Expect(plan).ToHaveCountAsync(1);
    await Expect(plan.Locator("summary")).ToContainTextAsync("Plan");
    await Expect(plan.Locator(".plan-step")).ToHaveCountAsync(2);
    await Expect(plan.Locator(".plan-step").Nth(0)).ToContainTextAsync(
      "Create tracked fixture"
    );
    await Expect(plan.Locator(".plan-step").Nth(0)).ToHaveClassAsync(
      new System.Text.RegularExpressions.Regex("completed")
    );
    await Expect(plan.Locator(".plan-step").Nth(1)).ToHaveClassAsync(
      new System.Text.RegularExpressions.Regex("completed")
    );
    await Expect(plan.Locator(".execution-plan-progress")).ToContainTextAsync(
      "Steps 2/2"
    );
    await Expect(
      Page.Locator(".activity .execution-plan")
    ).ToHaveCountAsync(0);
    Assert.AreEqual(
      "created through a specialist-proposed plan",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "tracked-plan.txt")
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExecuteContextUsesFinalSpecialistPayloadAndPublishesOrderedSnapshots()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("execute create file");

    await Expect(Page.Locator("#context-usage-summary")).ToContainTextAsync("exact");
    await Page.Locator("#context-usage > summary").ClickAsync();
    var details = Page.Locator("#context-usage-details");
    foreach (var category in new[]
    {
      "Current conversation and message",
      "System and instructions",
      "Project context",
      "Toolset discovery",
      "Granted schemas",
      "Host state/results",
      "Structural overhead",
      "Total input",
      "Output reserve",
      "Required context",
      "Effective limit",
      "Count source"
    })
    {
      await Expect(details).ToContainTextAsync(category);
    }
    var contextEvents = Page.Locator("[data-event-type=\"context.usage\"]");
    Assert.IsGreaterThanOrEqualTo(4, await contextEvents.CountAsync());
    await Expect(contextEvents.First).ToContainTextAsync("Specialist inference 1");
    await Expect(contextEvents.Last).ToContainTextAsync("provider-reported input");
    Assert.IsGreaterThanOrEqualTo(
      2,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "qwen3-coder:30b"
          && request.Messages.Any(
            message => message.Content.Contains(
              LocalActionPlanner.PlannerMarker,
              StringComparison.Ordinal
            )
          )
      ).Select(
        request => string.Join(",", request.AvailableTools)
      ).Distinct(StringComparer.Ordinal).Count()
    );
    var visibleMessageCount = await Page.Locator(".message").CountAsync();
    await Expect(Page.Locator("#compact-context")).ToBeVisibleAsync();
    await Page.Locator("#compact-context").ClickAsync();
    await Expect(Page.Locator("#app-modal")).ToBeVisibleAsync();
    await Expect(Page.Locator("#app-modal-title")).ToHaveTextAsync(
      "Compact submitted context?"
    );
    await Expect(Page.Locator("#app-modal-message")).ToContainTextAsync(
      "will not delete saved messages"
    );
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(Page.Locator("#app-modal")).ToBeHiddenAsync();
    await Expect(Page.Locator("#compact-context")).ToHaveTextAsync(
      "Compaction prepared"
    );
    Assert.AreEqual(
      visibleMessageCount,
      await Page.Locator(".message").CountAsync()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GrantedDeleteSchemaStillRequiresApprovalBeforeAnyEffect()
  {
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "obsolete-a.txt"
    );
    await File.WriteAllTextAsync(
      file,
      "keep until approved"
    );
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "qwen3-coder:30b"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute delete files direct obsolete-a.txt"
    );

    var approval = Page.Locator(
      ".action-approval"
    );
    await Expect(
      approval
    ).ToBeVisibleAsync();
    Assert.IsTrue(
      File.Exists(
        file
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.AvailableTools.Contains(
          "delete_paths",
          StringComparer.Ordinal
        )
      )
    );
    await Expect(
      Page.Locator(
        ".assistant-toolset-request"
      ).Last
    ).ToContainTextAsync(
      "delete_paths"
    );
    await Page.Locator(
      "#send-button"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Send"
    );
    Assert.IsTrue(
      File.Exists(
        file
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AskPolicyPendingCommandCanBeEditedBeforeExecution()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute unknown process"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToBeVisibleAsync();
    var approval = Page.Locator(
      ".action-approval"
    ).Last;
    var editor = approval.GetByRole(
      AriaRole.Textbox,
      new()
      {
        Name = "Edit run_process command",
        Exact = true
      }
    );
    await Expect(
      editor
    ).ToHaveValueAsync(
      "dotnet --list-sdks"
    );
    await editor.FillAsync(
      "dotnet --version"
    );
    await Expect(
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Refresh",
          Exact = true
        }
      )
    ).ToHaveCountAsync(0);
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve",
        Exact = true
      }
    ).ClickAsync();
    var response = approval.Locator(
      ".action-response"
    );
    await Expect(
      response
    ).ToBeAttachedAsync();
    await Expect(
      response
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    var completedActivity = Page.Locator(
      ".message.assistant .activity"
    ).Last;
    await Expect(
      completedActivity.Locator(":scope > summary")
    ).ToContainTextAsync("Completed");
    if (await completedActivity.GetAttributeAsync("open") is null)
    {
      await completedActivity.Locator(":scope > summary").ClickAsync();
    }
    if (!await response.Locator(":scope > summary").IsVisibleAsync())
    {
      await approval.Locator(":scope > summary").ClickAsync();
    }
    await Expect(
      response.Locator(
        ":scope > summary"
      )
    ).ToContainTextAsync(
      "Execution"
    );
    await response.Locator(
      ":scope > summary"
    ).ClickAsync();
    await Expect(
      response.Locator(
        ".action-response-output"
      )
    ).ToContainTextAsync(
      "Exit code: 0"
    );
    await Expect(
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Approve",
          Exact = true
        }
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoPolicyRequiresExplicitApprovalForUntrustedCommand()
  {
    await Page.GotoAsync("/");
    await SetExecuteModeAsync("auto");
    await StartMessageAsync("execute unknown process");

    await Expect(
      Page.Locator("[data-event-type=\"action.awaiting-approval\"]")
    ).ToBeVisibleAsync();
    await Page.Locator(".action-approval").Last.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator("[data-event-type=\"action.process-output\"]")
    ).ToContainTextAsync("Exit code: 0");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PowerShellExactPermissionCanBeRememberedAndRevokedPerWorkspace()
  {
    await Page.GotoAsync("/");
    await SetExecuteModeAsync("auto");
    await StartMessageAsync("execute powershell process");

    var approval = Page.Locator(".action-approval").Last;
    await Expect(approval).ToContainTextAsync(
      "may access files, processes, the registry, or the network outside the trusted workspace"
    );
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Always allow exact command",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator("[data-event-type=\"action.process-output\"]").Last
    ).ToContainTextAsync("AR-POWERSHELL-OK");
    var approvalCount = await Page.Locator(
      "[data-event-type=\"action.awaiting-approval\"]"
    ).CountAsync();

    await SendMessageAsync("execute powershell process");
    Assert.AreEqual(
      approvalCount,
      await Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      ).CountAsync()
    );
    await Expect(
      Page.Locator("[data-event-type=\"action.process-output\"]").Last
    ).ToContainTextAsync("AR-POWERSHELL-OK");

    await Page.Locator("#open-settings").ClickAsync();
    await Page.Locator(
      "#settings-navigation [data-settings-target=\"execution\"]"
    ).ClickAsync();
    var permission = Page.Locator("#settings-process-permissions .settings-permission-row");
    await Expect(permission).ToHaveCountAsync(1);
    await Expect(permission).ToContainTextAsync("powershell");
    await Expect(permission).ToContainTextAsync("4 exact argument(s)");
    await Expect(permission).ToContainTextAsync("SHA-256");
    await permission.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Revoke",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator("#settings-process-permissions")
    ).ToContainTextAsync("No persistent process permissions");
    await Page.Locator("#close-settings").ClickAsync();

    await StartMessageAsync("execute powershell process");
    await Expect(
      Page.Locator("[data-event-type=\"action.awaiting-approval\"]")
    ).ToHaveCountAsync(approvalCount + 1);
    await Page.Locator(".action-approval").Last.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Reject",
        Exact = true
      }
    ).ClickAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PendingStructuredFileActionRunsInlineEditedArgumentsOnApproval()
  {
    var originalPath = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    var correctedPath = Path.Combine(
      _environment.WorkspaceDirectory,
      "corrected.txt"
    );
    File.Delete(originalPath);
    File.Delete(correctedPath);
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    var approval = Page.Locator(
      ".action-approval"
    ).Last;
    var editor = approval.GetByRole(
      AriaRole.Textbox,
      new()
      {
        Name = "Edit create_file command",
        Exact = true
      }
    );
    await Expect(approval).ToContainTextAsync("create_file");
    await editor.FillAsync(
      """
      {
        "path": "corrected.txt",
        "content": "approved inline edit"
      }
      """
    );
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator("[data-event-type=\"action.edit-applied\"]")
    ).ToBeAttachedAsync();

    Assert.AreEqual(
      "approved inline edit",
      await File.ReadAllTextAsync(
        correctedPath
      )
    );
    Assert.IsFalse(
      File.Exists(
        originalPath
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task EditedPendingCommandMustPassExistingProcessPolicy()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute unknown process"
    );
    var approval = Page.Locator(
      ".action-approval"
    ).Last;
    var editor = approval.GetByRole(
      AriaRole.Textbox,
      new()
      {
        Name = "Edit run_process command",
        Exact = true
      }
    );
    await editor.FillAsync(
      "git clean -fd"
    );
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      editor
    ).ToHaveAttributeAsync(
      "aria-invalid",
      "true"
    );
    await Expect(
      approval.Locator(
        ".approval-status"
      )
    ).ToHaveTextAsync(
      "Invalid change"
    );
    await Expect(
      Page.Locator(
        "#toast-region .app-toast[data-tone=\"error\"]"
      ).Last
    ).ToBeVisibleAsync();
    await Expect(
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Approve",
          Exact = true
        }
      )
    ).ToBeEnabledAsync();
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Reject",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Send"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FailedProcessIsReturnedToSpecialistAndReplanned()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute recover failed process"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(".action-approval").Last.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Approve",
        Exact = true
      }
    ).ClickAsync();

    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-error\"]"
      )
    ).ToContainTextAsync(
      "could not be started"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-started\"]"
      )
    ).ToHaveCountAsync(0);
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-guidance-prepared\"]"
      )
    ).ToHaveCountAsync(0);
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-planning\"]"
      )
    ).ToContainTextAsync(
      "returned to the active specialist"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      )
    ).ToContainTextAsync(
      "list_files"
    );
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Completed"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.validation-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "alpha:latest"
          && request.Messages.Any(
            message => message.Content.StartsWith(
              "LOCAL_ACTION_RESULT",
              StringComparison.Ordinal
            ) && message.Content.Contains(
              "Tool: run_process",
              StringComparison.Ordinal
            ) && message.Content.Contains(
              "Status: failed",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MigratesTrustedWorkspaceOnceWithHistoryDisabled()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Trusted workspace"
      }
    ).ClickAsync();

    await Expect(
      Page.Locator(
        ".workspace-profile-entry"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        ".workspace-profile-entry"
      )
    ).ToContainTextAsync(
      "history disabled"
    );
    Assert.IsTrue(
      File.Exists(
        Path.Combine(
          _environment.DataDirectory,
          "workspaces.json"
        )
      )
    );
    using var first = await _environment.HttpClient.GetAsync(
      "api/workspaces"
    );
    using var second = await _environment.HttpClient.GetAsync(
      "api/workspaces"
    );
    first.EnsureSuccessStatusCode();
    second.EnsureSuccessStatusCode();
    using var firstDocument = JsonDocument.Parse(
      await first.Content.ReadAsStringAsync()
    );
    using var secondDocument = JsonDocument.Parse(
      await second.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      firstDocument.RootElement.GetProperty(
        "activeWorkspaceId"
      ).GetString(),
      secondDocument.RootElement.GetProperty(
        "activeWorkspaceId"
      ).GetString()
    );
    Assert.AreEqual(
      1,
      secondDocument.RootElement.GetProperty(
        "profiles"
      ).GetArrayLength()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task WorkspaceManagerAddsRenamesSwitchesAndResetsSessionAuthority()
  {
    var secondPath = _environment.CreateWorkspaceDirectory(
      $"second-workspace-{Guid.NewGuid():N}"
    );
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "alpha:latest"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#add-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#workspace-profile-name"
    ).FillAsync(
      "Second project"
    );
    await Page.Locator(
      "#trusted-workspace-path"
    ).FillAsync(
      secondPath
    );
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save workspace"
      }
    ).ClickAsync();

    await Expect(
      Page.Locator(
        ".workspace-profile-entry.active"
      )
    ).ToContainTextAsync(
      "Second project"
    );
    await Expect(
      Page.Locator(
        "[data-mode=\"chat\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-pressed",
      "true"
    );
    await Expect(
      Page.Locator(
        "#approval-policy"
      )
    ).ToHaveValueAsync(
      "auto"
    );
    await Expect(
      Page.Locator(
        "#harness-selector"
      )
    ).ToHaveValueAsync("native");

    await Page.Locator(
      ".workspace-profile-entry.active"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Rename"
      }
    ).ClickAsync();
    await Page.Locator("#app-modal-input").FillAsync("Renamed project");
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        ".workspace-profile-entry.active"
      )
    ).ToContainTextAsync(
      "Renamed project"
    );

    await Page.Locator(
      "#add-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#workspace-profile-name"
    ).FillAsync(
      "Duplicate"
    );
    await Page.Locator(
      "#trusted-workspace-path"
    ).FillAsync(
      secondPath
    );
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Save workspace"
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#workspace-validation"
      )
    ).ToContainTextAsync(
      "already uses"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ActiveExecutionBlocksWorkspaceActivation()
  {
    var secondPath = _environment.CreateWorkspaceDirectory(
      "blocked-switch"
    );
    using var created = await _environment.HttpClient.PostAsJsonAsync(
      "api/workspaces",
      new
      {
        name = "Blocked switch",
        path = secondPath
      }
    );
    created.EnsureSuccessStatusCode();
    using var createdDocument = JsonDocument.Parse(
      await created.Content.ReadAsStringAsync()
    );
    var secondId = createdDocument.RootElement.GetProperty(
      "id"
    ).GetString()!;
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    await Expect(
      Page.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Approve",
          Exact = true
        }
      )
    ).ToBeVisibleAsync();

    using var activation = await _environment.HttpClient.PostAsync(
      $"api/workspaces/{secondId}/activate",
      null
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      activation.StatusCode
    );
    Assert.Contains(
      "workspace-activation-blocked",
      await activation.Content.ReadAsStringAsync()
    );
    await Page.Locator(
      "#send-button"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Send"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HistoryOptInPersistsButRestartRequiresExplicitResume()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (
      var history = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history",
        new
        {
          enabled = true
        }
      )
    )
    {
      history.EnsureSuccessStatusCode();
    }
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Persist this explicit local conversation."
    );
    await Expect(
      Page.Locator(
        "#recent-sessions .session-entry"
      )
    ).ToHaveCountAsync(
      1
    );

    await _environment.RestartApplicationAsync();
    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        ".message.user"
      )
    ).ToHaveCountAsync(
      0
    );
    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Page.Locator(
      "#recent-sessions .session-entry"
    ).Locator(".session-entry-content").ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        ".message.user"
      )
    ).ToContainTextAsync(
      "Persist this explicit local conversation."
    );
    await Expect(
      Page.Locator(
        "#approval-policy"
      )
    ).ToHaveValueAsync(
      "auto"
    );
    await Expect(
      Page.Locator(
        "[data-mode=\"chat\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-pressed",
      "true"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RecentSessionCardIsCompactAndDetailsModalOwnsActionsAndSummary()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (
      var history = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history",
        new
        {
          enabled = true
        }
      )
    )
    {
      history.EnsureSuccessStatusCode();
    }
    using (
      var saved = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/current",
        new
        {
          sessionId = "compact-card-v0913",
          messages = new[]
          {
            new
            {
              role = "user",
              content = "Keep this compact conversation available."
            },
            new
            {
              role = "assistant",
              content = "The compact conversation is saved."
            }
          },
          interactionMode = "execute",
          selectedModel = "command-r:latest",
          state = "completed"
        }
      )
    )
    {
      saved.EnsureSuccessStatusCode();
    }
    using (
      var summary = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/compact-card-v0913/summary",
        new
        {
          content = new
          {
            objective = "Preserve the compact navigation contract.",
            decisions = new[]
            {
              "Keep secondary actions in the details modal."
            },
            filesChanged = Array.Empty<string>(),
            commandsAndValidation = new[]
            {
              "Browser interaction covered by Playwright."
            },
            unresolvedIssues = Array.Empty<string>(),
            nextSuggestedStep = "Resume only when more work is needed."
          }
        }
      )
    )
    {
      summary.EnsureSuccessStatusCode();
    }
    using (
      var withoutSummary = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/current",
        new
        {
          sessionId = "compact-card-without-summary-v0914",
          messages = new[]
          {
            new
            {
              role = "user",
              content = "Keep this conversation without a continuity summary."
            }
          },
          interactionMode = "chat",
          selectedModel = "command-r:latest",
          state = "completed"
        }
      )
    )
    {
      withoutSummary.EnsureSuccessStatusCode();
    }

    await Page.GotoAsync("/");
    await Page.Locator("#session-history").EvaluateAsync(
      "element => element.open = true"
    );
    await Expect(
      Page.Locator("#pinned-session-section")
    ).ToBeHiddenAsync();
    var summaryFreeCard = Page.Locator(
      "#recent-sessions .session-entry[data-session-id=\"compact-card-without-summary-v0914\"]"
    );
    await summaryFreeCard.Locator(".session-details-button").ClickAsync();
    await Expect(Page.Locator("#session-details-dialog")).ToBeVisibleAsync();
    var compactDetailsHeight = await Page.Locator(
      "#session-details-dialog .session-details-shell"
    ).EvaluateAsync<double>(
      "element => element.getBoundingClientRect().height"
    );
    Assert.IsLessThan(
      560,
      compactDetailsHeight
    );
    await Page.Locator("#dismiss-session-details").ClickAsync();
    var card = Page.Locator(
      "#recent-sessions .session-entry[data-session-id=\"compact-card-v0913\"]"
    );
    await Expect(card).ToBeVisibleAsync();
    await Expect(card.Locator("button")).ToHaveCountAsync(2);
    await Expect(card.Locator(".session-entry-content")).ToHaveAttributeAsync(
      "aria-label",
      "Resume Keep this compact conversation available."
    );
    await Expect(card).Not.ToContainTextAsync("Rename");
    await Expect(card).Not.ToContainTextAsync("Summary");
    await Expect(card.Locator("small")).Not.ToBeEmptyAsync();

    await card.Locator(".session-details-button").ClickAsync();
    await Expect(Page.Locator("#session-details-dialog")).ToBeVisibleAsync();
    await Expect(Page.Locator("#session-details-title")).ToHaveTextAsync(
      "Keep this compact conversation available."
    );
    await Expect(Page.Locator("#session-details-summary")).ToContainTextAsync(
      "Preserve the compact navigation contract."
    );
    await Expect(Page.Locator("#session-details-summary")).ToContainTextAsync(
      "Keep secondary actions in the details modal."
    );
    await Expect(Page.Locator("#session-details-dialog textarea")).ToHaveCountAsync(0);
    await Expect(Page.Locator("#session-details-rename")).ToBeVisibleAsync();
    await Expect(Page.Locator("#edit-session-summary")).ToBeVisibleAsync();
    var detailsHeight = await Page.Locator(
      "#session-details-dialog .session-details-shell"
    ).EvaluateAsync<double>(
      "element => element.getBoundingClientRect().height"
    );
    Assert.IsLessThanOrEqualTo(
      684,
      detailsHeight
    );
    var tallestAction = await Page.Locator(
      "#session-details-dialog .session-details-actions > *"
    ).EvaluateAllAsync<double>(
      "elements => Math.max(...elements.map(element => element.getBoundingClientRect().height))"
    );
    Assert.IsLessThanOrEqualTo(
      48,
      tallestAction
    );

    await Page.Locator("#resume-session-details").ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(Page.Locator(".message.user")).ToContainTextAsync(
      "Keep this compact conversation available."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RestartMarksRunningPersistentExecutionInterrupted()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (
      var history = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history",
        new
        {
          enabled = true
        }
      )
    )
    {
      history.EnsureSuccessStatusCode();
    }
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    await Expect(
      Page.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Approve",
          Exact = true
        }
      )
    ).ToBeVisibleAsync();

    await _environment.RestartApplicationAsync();
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Page.Locator(
      "#recent-sessions .session-entry .session-details-button"
    ).ClickAsync();
    await Expect(Page.Locator("#session-details-state")).ToContainTextAsync(
      "Interrupted"
    );
    await Page.Locator("#resume-session-details").ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        ".message.assistant"
      ).Last
    ).ToContainTextAsync(
      "No pending process or approval was resumed"
    );
    Assert.IsFalse(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
        "generated.txt"
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SessionExportAndDeletionPreserveWorkspaceFiles()
  {
    var marker = Path.Combine(
      _environment.WorkspaceDirectory,
      "keep-project-file.txt"
    );
    await File.WriteAllTextAsync(
      marker,
      "keep"
    );
    var activeId = await ActiveWorkspaceIdAsync();
    using var history = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{activeId}/history",
      new
      {
        enabled = true
      }
    );
    history.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Create an exportable local record."
    );
    using var sessions = await _environment.HttpClient.GetAsync(
      "api/sessions"
    );
    sessions.EnsureSuccessStatusCode();
    using var sessionsDocument = JsonDocument.Parse(
      await sessions.Content.ReadAsStringAsync()
    );
    var sessionId = sessionsDocument.RootElement.GetProperty(
      "recent"
    )[0].GetProperty(
      "id"
    ).GetString()!;
    using var export = await _environment.HttpClient.GetAsync(
      $"api/sessions/{sessionId}/export"
    );
    export.EnsureSuccessStatusCode();
    var json = await export.Content.ReadAsStringAsync();
    Assert.Contains(
      "\"schemaVersion\": 1",
      json
    );
    Assert.DoesNotContain(
      "approvalToken",
      json
    );
    Assert.DoesNotContain(
      "processId",
      json
    );
    using var deleted = await _environment.HttpClient.DeleteAsync(
      "api/sessions?confirmed=true"
    );
    deleted.EnsureSuccessStatusCode();
    Assert.IsTrue(
      File.Exists(
        marker
      )
    );
    using var profiles = await _environment.HttpClient.GetAsync(
      "api/workspaces"
    );
    profiles.EnsureSuccessStatusCode();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ReviewAndUndoRemainEligibleAfterExplicitResume()
  {
    var activeId = await ActiveWorkspaceIdAsync();
    using var history = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{activeId}/history",
      new
      {
        enabled = true
      }
    );
    history.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
    );
    var changedFile = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    Assert.IsTrue(
      File.Exists(
        changedFile
      )
    );

    using (
      var legacySettings = await _environment.PutSettingsAsync(
        _environment.BaselineSettings with
        {
          TrustedWorkspacePath = null
        }
      )
    )
    {
      legacySettings.EnsureSuccessStatusCode();
    }

    await _environment.RestartApplicationAsync();
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Page.Locator(
      "#recent-sessions .session-entry"
    ).Locator(".session-entry-content").ClickAsync();
    await ConfirmAppModalAsync();
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Review completed changes"
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#change-review-body"
      )
    ).ToContainTextAsync(
      "hello.txt"
    );
    await Expect(
      Page.Locator(
        "#undo-execution"
      )
    ).ToBeEnabledAsync();
    await Page.Locator(
      "#undo-execution"
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#undo-status"
      )
    ).ToContainTextAsync(
      "undone"
    );
    Assert.IsFalse(
      File.Exists(
        changedFile
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RetentionRemovesOldestUnpinnedSessionAndProtectsPinnedSessions()
  {
    using var settingsResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        SessionHistory = new TestSessionHistorySettings
        {
          MaxSessionsPerWorkspace = 1
        }
      }
    );
    settingsResponse.EnsureSuccessStatusCode();
    var activeId = await ActiveWorkspaceIdAsync();
    using var history = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{activeId}/history",
      new
      {
        enabled = true
      }
    );
    history.EnsureSuccessStatusCode();
    foreach (var item in new[]
    {
      new
      {
        Id = "retention-first-v098",
        Message = "First retained conversation."
      },
      new
      {
        Id = "retention-second-v098",
        Message = "Second conversation beyond the retention limit."
      }
    })
    {
      using var saved = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/current",
        new
        {
          sessionId = item.Id,
          messages = new[]
          {
            new
            {
              role = "user",
              content = item.Message
            }
          },
          interactionMode = "chat",
          selectedModel = "alpha:latest",
          state = "completed"
        }
      );
      saved.EnsureSuccessStatusCode();
    }
    using var sessions = await _environment.HttpClient.GetAsync(
      "api/sessions"
    );
    sessions.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await sessions.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      1,
      document.RootElement.GetProperty(
        "recent"
      ).GetArrayLength()
    );
    Assert.AreEqual(
      "Second conversation beyond the retention limit.",
      document.RootElement.GetProperty(
        "recent"
      )[0].GetProperty(
        "title"
      ).GetString()
    );

    var retainedId = document.RootElement.GetProperty(
      "recent"
    )[0].GetProperty(
      "id"
    ).GetString()!;
    using (
      var pinned = await _environment.HttpClient.PutAsJsonAsync(
        $"api/sessions/{retainedId}/pin",
        new
        {
          pinned = true
        }
      )
    )
    {
      pinned.EnsureSuccessStatusCode();
    }
    using var blocked = await _environment.HttpClient.PutAsJsonAsync(
      "api/sessions/current",
      new
      {
        sessionId = "pinned-retention-blocked",
        messages = new[]
        {
          new
          {
            role = "user",
            content = "Pinned sessions require an explicit deletion."
          }
        },
        interactionMode = "chat",
        selectedModel = "alpha:latest",
        state = "completed"
      }
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      blocked.StatusCode
    );
    StringAssert.Contains(
      await blocked.Content.ReadAsStringAsync(),
      "history-retention-limit-reached"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConversationSearchAndPinnedHistoryUseOnlySafeLocalFields()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (
      var history = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history",
        new
        {
          enabled = true
        }
      )
    )
    {
      history.EnsureSuccessStatusCode();
    }
    using (
      var saved = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/current",
        new
        {
          sessionId = "searchable-v098",
          messages = new[]
          {
            new
            {
              role = "user",
              content = "Investigate the cobalt indexing contract."
            },
            new
            {
              role = "assistant",
              content = "The visible cobalt result is ready."
            }
          },
          interactionMode = "chat",
          selectedModel = "command-r:latest",
          state = "completed"
        }
      )
    )
    {
      saved.EnsureSuccessStatusCode();
    }

    using (
      var pinned = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/searchable-v098/pin",
        new
        {
          pinned = true
        }
      )
    )
    {
      pinned.EnsureSuccessStatusCode();
    }
    using var search = await _environment.HttpClient.PostAsJsonAsync(
      "api/sessions/search",
      new
      {
        query = "cobalt",
        allWorkspaces = false,
        provider = "ollama-local",
        model = "command-r:latest",
        pinned = true,
        from = DateTimeOffset.UtcNow.AddMinutes(
          -5
        ),
        to = DateTimeOffset.UtcNow.AddMinutes(
          5
        ),
        limit = 10
      }
    );
    search.EnsureSuccessStatusCode();
    using var searchDocument = JsonDocument.Parse(
      await search.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "active-workspace",
      searchDocument.RootElement.GetProperty(
        "workspaceScope"
      ).GetString()
    );
    Assert.AreEqual(
      1,
      searchDocument.RootElement.GetProperty(
        "results"
      ).GetArrayLength()
    );
    var result = searchDocument.RootElement.GetProperty(
      "results"
    )[0];
    Assert.AreEqual(
      "searchable-v098",
      result.GetProperty(
        "id"
      ).GetString()
    );
    Assert.IsTrue(
      result.GetProperty(
        "snippet"
      ).GetString()!.Contains(
        "cobalt",
        StringComparison.OrdinalIgnoreCase
      )
    );
    Assert.IsGreaterThan(
      0,
      result.GetProperty(
        "highlights"
      ).GetArrayLength()
    );
    using var titleSearch = await _environment.HttpClient.PostAsJsonAsync(
      "api/sessions/search",
      new
      {
        query = "Investigate the cobalt",
        limit = 10
      }
    );
    titleSearch.EnsureSuccessStatusCode();
    using var titleDocument = JsonDocument.Parse(
      await titleSearch.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "title",
      titleDocument.RootElement.GetProperty(
        "results"
      )[0].GetProperty(
        "matchField"
      ).GetString()
    );

    using var noHiddenMatch = await _environment.HttpClient.PostAsJsonAsync(
      "api/sessions/search",
      new
      {
        query = "approvalToken",
        allWorkspaces = true,
        limit = 10
      }
    );
    noHiddenMatch.EnsureSuccessStatusCode();
    using var noHiddenDocument = JsonDocument.Parse(
      await noHiddenMatch.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      0,
      noHiddenDocument.RootElement.GetProperty(
        "results"
      ).GetArrayLength()
    );

    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Expect(
      Page.Locator(
        "#pinned-sessions .session-entry"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        "#pinned-sessions"
      )
    ).ToContainTextAsync(
      "Investigate the cobalt indexing contract."
    );
    await Page.Locator(
      "#open-session-search"
    ).ClickAsync();
    await Page.Locator(
      "#session-search-query"
    ).FillAsync(
      "cobalt"
    );
    await Page.Locator(
      "#run-session-search"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#session-search-results"
      )
    ).ToContainTextAsync(
      "cobalt"
    );
    using (
      var unpinned = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/searchable-v098/pin",
        new
        {
          pinned = false
        }
      )
    )
    {
      unpinned.EnsureSuccessStatusCode();
    }
    using var afterUnpin = await _environment.HttpClient.GetAsync(
      "api/sessions"
    );
    afterUnpin.EnsureSuccessStatusCode();
    using var afterUnpinDocument = JsonDocument.Parse(
      await afterUnpin.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      0,
      afterUnpinDocument.RootElement.GetProperty(
        "pinned"
      ).GetArrayLength()
    );
    Assert.AreEqual(
      "searchable-v098",
      afterUnpinDocument.RootElement.GetProperty(
        "recent"
      )[0].GetProperty(
        "id"
      ).GetString()
    );

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Assert.ThrowsExactlyAsync<TaskCanceledException>(
      () => _environment.HttpClient.PostAsJsonAsync(
        "api/sessions/search",
        new
        {
          query = "cobalt",
          allWorkspaces = true,
          limit = 100
        },
        cancellation.Token
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConversationSearchFindsChangedFilesAndValidationFacts()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (
      var history = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history",
        new
        {
          enabled = true
        }
      )
    )
    {
      history.EnsureSuccessStatusCode();
    }
    using (
      var profile = await _environment.HttpClient.PutAsJsonAsync(
        "api/workspace/validation-profile",
        new
        {
          name = "Searchable validation",
          source = "user",
          steps = new[]
          {
            new
            {
              id = "version",
              label = "Searchable dotnet version",
              executable = "dotnet",
              arguments = new[]
              {
                "--version"
              },
              workingDirectory = ".",
              timeoutSeconds = 30,
              required = true
            }
          }
        }
      )
    )
    {
      profile.EnsureSuccessStatusCode();
    }
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file validate"
    );

    using var search = await _environment.HttpClient.PostAsJsonAsync(
      "api/sessions/search",
      new
      {
        query = "hello.txt",
        fileChanged = "hello.txt",
        validationResult = "passed",
        limit = 10
      }
    );
    search.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await search.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      1,
      document.RootElement.GetProperty(
        "results"
      ).GetArrayLength()
    );
    Assert.AreEqual(
      "file-changed",
      document.RootElement.GetProperty(
        "results"
      )[0].GetProperty(
        "matchField"
      ).GetString()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SessionSummaryDuplicateAndMarkdownExportAreExplicitAndBounded()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (
      var history = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history",
        new
        {
          enabled = true
        }
      )
    )
    {
      history.EnsureSuccessStatusCode();
    }
    const string secret = "gsk_fake_secret_v098";
    using (
      var saved = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/current",
        new
        {
          sessionId = "summary-source-v098",
          messages = new[]
          {
            new
            {
              role = "user",
              content = $"Document the bounded result. Authorization: Bearer {secret}"
            },
            new
            {
              role = "assistant",
              content = "The tested conversation outcome is visible."
            }
          },
          interactionMode = "execute",
          selectedModel = "command-r:latest",
          state = "completed"
        }
      )
    )
    {
      saved.EnsureSuccessStatusCode();
    }
    var requestsBeforeEstimate = _environment.FakeOllama.AllRequests.Count;
    using var estimate = await _environment.HttpClient.GetAsync(
      "api/sessions/summary-source-v098/summary/estimate?model=command-r%3Alatest"
    );
    estimate.EnsureSuccessStatusCode();
    using var estimateDocument = JsonDocument.Parse(
      await estimate.Content.ReadAsStringAsync()
    );
    Assert.IsTrue(
      estimateDocument.RootElement.GetProperty(
        "permissionRequired"
      ).GetBoolean()
    );
    Assert.HasCount(
      requestsBeforeEstimate,
      _environment.FakeOllama.AllRequests);

    using var denied = await _environment.HttpClient.PostAsJsonAsync(
      "api/sessions/summary-source-v098/summary",
      new
      {
        model = "command-r:latest",
        confirmed = false,
        providerPermissionGranted = false
      }
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      denied.StatusCode
    );
    Assert.HasCount(
      requestsBeforeEstimate,
      _environment.FakeOllama.AllRequests);

    using var generated = await _environment.HttpClient.PostAsJsonAsync(
      "api/sessions/summary-source-v098/summary",
      new
      {
        model = "command-r:latest",
        confirmed = true,
        providerPermissionGranted = true
      }
    );
    generated.EnsureSuccessStatusCode();
    using var generatedDocument = JsonDocument.Parse(
      await generated.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "Preserve the tested conversation outcome.",
      generatedDocument.RootElement.GetProperty(
        "content"
      ).GetProperty(
        "objective"
      ).GetString()
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Content.Contains(
            "SESSION_SUMMARY_V1",
            StringComparison.Ordinal
          )
        )
      )
    );

    using var edited = await _environment.HttpClient.PutAsJsonAsync(
      "api/sessions/summary-source-v098/summary",
      new
      {
        content = new
        {
          objective = "Keep the manually reviewed result.",
          decisions = new[]
          {
            "Retain only visible facts."
          },
          filesChanged = Array.Empty<string>(),
          commandsAndValidation = new[]
          {
            "Deterministic fake-provider test passed."
          },
          unresolvedIssues = Array.Empty<string>(),
          nextSuggestedStep = "Review the duplicate."
        }
      }
    );
    edited.EnsureSuccessStatusCode();

    using var duplicate = await _environment.HttpClient.PostAsync(
      "api/sessions/summary-source-v098/duplicate",
      null
    );
    duplicate.EnsureSuccessStatusCode();
    using var duplicateDocument = JsonDocument.Parse(
      await duplicate.Content.ReadAsStringAsync()
    );
    var duplicateSession = duplicateDocument.RootElement.GetProperty(
      "session"
    );
    Assert.AreEqual(
      "completed",
      duplicateSession.GetProperty(
        "state"
      ).GetString()
    );
    Assert.AreEqual(
      "chat",
      duplicateSession.GetProperty(
        "lastInteractionMode"
      ).GetString()
    );
    Assert.AreEqual(
      JsonValueKind.Null,
      duplicateSession.GetProperty(
        "selectedModel"
      ).ValueKind
    );
    Assert.AreEqual(
      0,
      duplicateSession.GetProperty(
        "executionReviews"
      ).GetArrayLength()
    );
    Assert.IsFalse(
      duplicateSession.GetProperty(
        "pinned"
      ).GetBoolean()
    );
    Assert.AreEqual(
      "Keep the manually reviewed result.",
      duplicateSession.GetProperty(
        "sessionSummary"
      ).GetProperty(
        "content"
      ).GetProperty(
        "objective"
      ).GetString()
    );

    using var markdownResponse = await _environment.HttpClient.GetAsync(
      "api/sessions/summary-source-v098/export/markdown"
        + "?includeSummary=true&includeModelMetadata=true"
    );
    markdownResponse.EnsureSuccessStatusCode();
    var markdown = await markdownResponse.Content.ReadAsStringAsync();
    StringAssert.Contains(
      markdown,
      "## Session summary"
    );
    StringAssert.Contains(
      markdown,
      "## Conversation"
    );
    StringAssert.Contains(
      markdown,
      "[secret redacted]"
    );
    Assert.DoesNotContain(
      secret,
      markdown
    );
    Assert.DoesNotContain(
      "approvalToken",
      markdown
    );
    Assert.DoesNotContain(
      _environment.WorkspaceDirectory,
      markdown
    );

    using var deleted = await _environment.HttpClient.DeleteAsync(
      "api/sessions/summary-source-v098/summary"
    );
    Assert.AreEqual(
      HttpStatusCode.NoContent,
      deleted.StatusCode
    );
    using var missing = await _environment.HttpClient.GetAsync(
      "api/sessions/summary-source-v098/summary"
    );
    missing.EnsureSuccessStatusCode();
    Assert.AreEqual(
      string.Empty,
      await missing.Content.ReadAsStringAsync()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ContextIndicatorWaitsForPayloadThenMovesFromEstimateToExact()
  {
    using var settingsResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        Context = new TestContextSettings
        {
          MaxConversationMessages = 2
        }
      }
    );
    settingsResponse.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#message-input"
    ).FillAsync(
      "Estimate this request."
    );
    await Expect(
      Page.Locator(
        "#context-usage-summary"
      )
    ).ToContainTextAsync(
      "calculated when sending"
    );

    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "Return exact provider usage after trimming.",
        model = "alpha:latest",
        history = new[]
        {
          new
          {
            role = "user",
            content = "Old visible user message."
          },
          new
          {
            role = "assistant",
            content = "Old visible assistant message."
          },
          new
          {
            role = "user",
            content = "Recent visible user message."
          },
          new
          {
            role = "assistant",
            content = "Recent visible assistant message."
          }
        },
        interactionMode = "chat",
        approvalPolicy = "ask",
        browserSessionId = "browser-context-v098",
        conversationSessionId = (string?)null,
        webSearchEnabled = false
      }
    );
    response.EnsureSuccessStatusCode();
    var stream = await response.Content.ReadAsStringAsync();
    var events = stream.Split(
      '\n',
      StringSplitOptions.RemoveEmptyEntries
    ).Where(
      line => line.StartsWith(
        "data: ",
        StringComparison.Ordinal
      )
    ).Select(
      line => JsonNode.Parse(
        line[6..]
      )!.AsObject()
    ).ToArray();
    var estimated = events.First(
      item => item["type"]!.GetValue<string>() == "context.usage"
    )["contextUsage"]!.AsObject();
    Assert.AreEqual(
      "estimated",
      estimated["accuracy"]!.GetValue<string>()
    );
    Assert.IsTrue(
      estimated["trimmed"]!.GetValue<bool>()
    );
    Assert.AreEqual(
      5,
      estimated["visibleMessages"]!.GetValue<int>()
    );
    Assert.AreEqual(
      3,
      estimated["includedMessages"]!.GetValue<int>()
    );
    Assert.AreEqual(
      2,
      estimated["omittedMessages"]!.GetValue<int>()
    );
    var completed = events.Last(
      item => item["type"]!.GetValue<string>() == "response.completed"
    )["contextUsage"]!.AsObject();
    Assert.AreEqual(
      "exact",
      completed["accuracy"]!.GetValue<string>()
    );
    Assert.AreEqual(
      120L,
      completed["inputTokens"]!.GetValue<long>()
    );
    Assert.AreEqual(
      4_096,
      completed["reservedResponseTokens"]!.GetValue<int>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FullBackupUsesHashesAndIncludesConversationsOnlyWhenSelected()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (
      var history = await _environment.HttpClient.PutAsJsonAsync(
        $"api/workspaces/{workspaceId}/history",
        new
        {
          enabled = true
        }
      )
    )
    {
      history.EnsureSuccessStatusCode();
    }
    using (
      var saved = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/current",
        new
        {
          sessionId = "backup-session-v099",
          messages = new[]
          {
            new
            {
              role = "user",
              content = "Conversation selected for local recovery."
            },
            new
            {
              role = "assistant",
              content = "Visible recovery fact."
            }
          },
          interactionMode = "chat",
          selectedModel = "alpha:latest",
          state = "completed"
        }
      )
    )
    {
      saved.EnsureSuccessStatusCode();
    }
    using (
      var summary = await _environment.HttpClient.PutAsJsonAsync(
        "api/sessions/backup-session-v099/summary",
        new
        {
          content = new
          {
            objective = "Recover the selected conversation.",
            decisions = Array.Empty<string>(),
            filesChanged = Array.Empty<string>(),
            commandsAndValidation = Array.Empty<string>(),
            unresolvedIssues = Array.Empty<string>(),
            nextSuggestedStep = "Inspect the manifest."
          }
        }
      )
    )
    {
      summary.EnsureSuccessStatusCode();
    }
    var secretDirectory = Path.Combine(
      _environment.DataDirectory,
      "secrets"
    );
    Directory.CreateDirectory(
      secretDirectory
    );
    const string secret = "gsk_never_export_this_v099";
    await File.WriteAllTextAsync(
      Path.Combine(
        secretDirectory,
        "test.protected"
      ),
      secret
    );

    using var baseBackup = await _environment.HttpClient.PostAsJsonAsync(
      "api/recovery/backup",
      new
      {
        includeConversations = false,
        includeSessionSummaries = false,
        includeUsageHistory = false,
        includeReviewData = false
      }
    );
    baseBackup.EnsureSuccessStatusCode();
    var baseBytes = await baseBackup.Content.ReadAsByteArrayAsync();
    using (
      var archive = new ZipArchive(
        new MemoryStream(
          baseBytes
        ),
        ZipArchiveMode.Read
      )
    )
    {
      Assert.IsNull(
        archive.Entries.FirstOrDefault(
          entry => entry.FullName.Contains(
            "sessions/",
            StringComparison.Ordinal
          )
        )
      );
      Assert.IsNotNull(
        archive.GetEntry(
          "data/catalog/pricing-catalog.json"
        )
      );
    }
    Assert.DoesNotContain(
      secret,
      Encoding.UTF8.GetString(
        baseBytes
      )
    );

    using var completeBackup = await _environment.HttpClient.PostAsJsonAsync(
      "api/recovery/backup",
      new
      {
        includeConversations = true,
        includeSessionSummaries = true,
        includeUsageHistory = false,
        includeReviewData = false
      }
    );
    completeBackup.EnsureSuccessStatusCode();
    var completeBytes = await completeBackup.Content.ReadAsByteArrayAsync();
    using var completeArchive = new ZipArchive(
      new MemoryStream(
        completeBytes
      ),
      ZipArchiveMode.Read
    );
    var manifestEntry = completeArchive.GetEntry(
      "manifest.json"
    );
    Assert.IsNotNull(
      manifestEntry
    );
    using var manifestDocument = JsonDocument.Parse(
      manifestEntry.Open()
    );
    var entries = manifestDocument.RootElement.GetProperty(
      "entries"
    ).EnumerateArray().ToArray();
    var sessionEntry = entries.Single(
      entry => entry.GetProperty(
        "path"
      ).GetString()!.EndsWith(
        "/backup-session-v099.json",
        StringComparison.Ordinal
      )
    );
    var sessionArchiveEntry = completeArchive.GetEntry(
      $"data/{sessionEntry.GetProperty("path").GetString()}"
    )!;
    using var sessionBuffer = new MemoryStream();
    await sessionArchiveEntry.Open().CopyToAsync(
      sessionBuffer
    );
    Assert.AreEqual(
      sessionEntry.GetProperty(
        "sha256"
      ).GetString(),
      Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
          sessionBuffer.ToArray()
        )
      ).ToLowerInvariant()
    );
    StringAssert.Contains(
      Encoding.UTF8.GetString(
        sessionBuffer.ToArray()
      ),
      "sessionSummary"
    );

    using var inspected = await _environment.HttpClient.PostAsJsonAsync(
      "api/recovery/backup/inspect",
      new
      {
        archiveBase64 = Convert.ToBase64String(
          completeBytes
        )
      }
    );
    inspected.EnsureSuccessStatusCode();
    using var inspection = JsonDocument.Parse(
      await inspected.Content.ReadAsStringAsync()
    );
    Assert.IsTrue(
      inspection.RootElement.GetProperty(
        "hashesValid"
      ).GetBoolean()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BackupRejectsCorruptionAndSelectiveRestoreCreatesCurrentDataBackup()
  {
    using var created = await _environment.HttpClient.PostAsJsonAsync(
      "api/recovery/backup",
      new
      {
        includeConversations = false,
        includeSessionSummaries = false,
        includeUsageHistory = false,
        includeReviewData = false
      }
    );
    created.EnsureSuccessStatusCode();
    var archive = await created.Content.ReadAsByteArrayAsync();
    var corrupted = archive.ToArray();
    corrupted[corrupted.Length / 2] ^= 0x5A;
    using var rejected = await _environment.HttpClient.PostAsJsonAsync(
      "api/recovery/backup/inspect",
      new
      {
        archiveBase64 = Convert.ToBase64String(
          corrupted
        )
      }
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      rejected.StatusCode
    );

    var changed = _environment.BaselineSettings with
    {
      DefaultModel = "docs:latest"
    };
    using (
      var saved = await _environment.PutSettingsAsync(
        changed
      )
    )
    {
      saved.EnsureSuccessStatusCode();
    }
    await using (
      var workspaceLock = new FileStream(
        Path.Combine(
          _environment.DataDirectory,
          "workspaces.json"
        ),
        FileMode.Open,
        FileAccess.Read,
        FileShare.None
      )
    )
    {
      using var failedRestore = await _environment.HttpClient.PostAsJsonAsync(
        "api/recovery/backup/restore",
        new
        {
          archiveBase64 = Convert.ToBase64String(
            archive
          ),
          categories = new[]
          {
            "settings",
            "workspaces"
          },
          confirmed = true
        }
      );
      Assert.AreEqual(
        HttpStatusCode.BadRequest,
        failedRestore.StatusCode
      );
    }
    using (
      var afterRollback = await _environment.HttpClient.GetAsync(
        "api/settings"
      )
    )
    {
      afterRollback.EnsureSuccessStatusCode();
      using var afterRollbackDocument = JsonDocument.Parse(
        await afterRollback.Content.ReadAsStringAsync()
      );
      Assert.AreEqual(
        "docs:latest",
        afterRollbackDocument.RootElement.GetProperty(
          "defaultModel"
        ).GetString()
      );
    }
    using var restored = await _environment.HttpClient.PostAsJsonAsync(
      "api/recovery/backup/restore",
      new
      {
        archiveBase64 = Convert.ToBase64String(
          archive
        ),
        categories = new[]
        {
          "settings"
        },
        confirmed = true
      }
    );
    restored.EnsureSuccessStatusCode();
    using var restoreDocument = JsonDocument.Parse(
      await restored.Content.ReadAsStringAsync()
    );
    var currentBackup = restoreDocument.RootElement.GetProperty(
      "currentDataBackup"
    ).GetString()!;
    Assert.IsTrue(
      File.Exists(
        Path.Combine(
          _environment.DataDirectory,
          currentBackup.Replace(
            '/',
            Path.DirectorySeparatorChar
          )
        )
      )
    );
    using var settings = await _environment.HttpClient.GetAsync(
      "api/settings"
    );
    settings.EnsureSuccessStatusCode();
    using var settingsDocument = JsonDocument.Parse(
      await settings.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "alpha:latest",
      settingsDocument.RootElement.GetProperty(
        "defaultModel"
      ).GetString()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MigrationBacksUpLegacyStoreAndFailureStartsSafeMode()
  {
    var settingsNode = JsonNode.Parse(
      await File.ReadAllTextAsync(
        _environment.SettingsPath
      )
    )!.AsObject();
    settingsNode.Remove(
      "schemaVersion"
    );
    await _environment.RestartApplicationAsync(
      () => File.WriteAllTextAsync(
        _environment.SettingsPath,
        settingsNode.ToJsonString(
          TestJson.Options
        )
      )
    );
    var migratedNode = JsonNode.Parse(
      await File.ReadAllTextAsync(
        _environment.SettingsPath
      )
    )!.AsObject();
    Assert.AreEqual(
      1,
      migratedNode["schemaVersion"]!.GetValue<int>()
    );
    Assert.IsTrue(
      Directory.EnumerateFiles(
        Path.Combine(
          _environment.DataDirectory,
          "migration-backups"
        ),
        "settings.json",
        SearchOption.AllDirectories
      ).Any()
    );

    migratedNode["schemaVersion"] = 99;
    try
    {
      await _environment.RestartApplicationAsync(
        () => File.WriteAllTextAsync(
          _environment.SettingsPath,
          migratedNode.ToJsonString(
            TestJson.Options
          )
        )
      );
      using var status = await _environment.HttpClient.GetAsync(
        "api/recovery/status"
      );
      status.EnsureSuccessStatusCode();
      using var statusDocument = JsonDocument.Parse(
        await status.Content.ReadAsStringAsync()
      );
      Assert.IsTrue(
        statusDocument.RootElement.GetProperty(
          "safeMode"
        ).GetBoolean()
      );
      Assert.IsTrue(
        statusDocument.RootElement.GetProperty(
          "executeDisabled"
        ).GetBoolean()
      );
      using var blocked = await _environment.HttpClient.PutAsJsonAsync(
        "api/settings",
        _environment.BaselineSettings
      );
      Assert.AreEqual(
        HttpStatusCode.Locked,
        blocked.StatusCode
      );
      using var cloudBlocked = await _environment.HttpClient.PostAsync(
        "api/cloud-providers/groq/test",
        null
      );
      Assert.AreEqual(
        HttpStatusCode.Locked,
        cloudBlocked.StatusCode
      );
      using var safeBackup = await _environment.HttpClient.PostAsJsonAsync(
        "api/recovery/backup",
        new
        {
          includeConversations = false,
          includeSessionSummaries = false,
          includeUsageHistory = false,
          includeReviewData = false
        }
      );
      safeBackup.EnsureSuccessStatusCode();
      Assert.IsGreaterThan(
        0,
        (
          await safeBackup.Content.ReadAsByteArrayAsync()
        ).Length
      );
      var preservedFailure = JsonNode.Parse(
        await File.ReadAllTextAsync(
          _environment.SettingsPath
        )
      )!.AsObject();
      Assert.AreEqual(
        99,
        preservedFailure["schemaVersion"]!.GetValue<int>()
      );
      Assert.IsEmpty(
        _environment.FakeOllama.Requests);
      await Page.GotoAsync(
        "/"
      );
      await Expect(
        Page.Locator(
          "#safe-mode-banner"
        )
      ).ToBeVisibleAsync();
      await Expect(
        Page.Locator(
          "[data-mode=\"execute\"]"
        )
      ).ToBeDisabledAsync();
      await Expect(
        Page.Locator(
          "#save-settings"
        )
      ).ToBeDisabledAsync();
      await Expect(
        Page.Locator(
          "body"
        )
      ).ToHaveAttributeAsync(
        "data-history-autoload",
        "disabled"
      );
    }
    finally
    {
      await _environment.RestartApplicationAsync(
        async () =>
        {
          await File.WriteAllTextAsync(
            _environment.SettingsPath,
            _environment.BaselineSettings.ToJson()
          );
          var failure = Path.Combine(
            _environment.DataDirectory,
            "migration-failure.json"
          );

          if (File.Exists(
            failure
          ))
          {
            File.Delete(
              failure
            );
          }
        }
      );
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ModelOrganizationFiltersProfilesAndWorkspaceReferencesAreAuthoritative()
  {
    const string groqKey = "gsk_fake_organization_097";
    await ConnectFakeCloudAsync(
      "groq",
      groqKey
    );

    foreach (var preference in new[]
    {
      new
      {
        providerId = "ollama-local",
        modelId = "command-r:latest",
        alias = "A Tools",
        favorite = true,
        hidden = false,
        note = "Preferred local tool model"
      },
      new
      {
        providerId = "ollama-local",
        modelId = "alpha:latest",
        alias = "Z Vision",
        favorite = true,
        hidden = false,
        note = "Preferred visual model"
      },
      new
      {
        providerId = "ollama-local",
        modelId = "docs:latest",
        alias = "Docs Hidden",
        favorite = true,
        hidden = true,
        note = "Repairable hidden selection"
      }
    })
    {
      using var saved = await _environment.HttpClient.PutAsJsonAsync(
        "api/model-organization/preference",
        preference
      );
      saved.EnsureSuccessStatusCode();
    }

    using (
      var conformance = await _environment.HttpClient.PostAsJsonAsync(
        "api/models/conformance",
        new
        {
          model = "alpha:latest",
          restoreResidentModel = false
        }
      )
    )
    {
      conformance.EnsureSuccessStatusCode();
    }

    using (
      var response = await _environment.HttpClient.GetAsync(
        "api/model-organization"
      )
    )
    {
      response.EnsureSuccessStatusCode();
      using var organization = JsonDocument.Parse(
        await response.Content.ReadAsStringAsync()
      );
      var models = organization.RootElement.GetProperty(
        "models"
      ).EnumerateArray().ToArray();
      var alpha = models.Single(
        model => model.GetProperty(
          "qualifiedId"
        ).GetString() == "alpha:latest"
      );
      var tools = models.Single(
        model => model.GetProperty(
          "qualifiedId"
        ).GetString() == "command-r:latest"
      );
      var hidden = models.Single(
        model => model.GetProperty(
          "qualifiedId"
        ).GetString() == "docs:latest"
      );
      Assert.AreEqual(
        "Z Vision",
        alpha.GetProperty(
          "alias"
        ).GetString()
      );
      Assert.AreEqual(
        "alpha:latest",
        alpha.GetProperty(
          "modelId"
        ).GetString()
      );
      Assert.IsTrue(
        alpha.GetProperty(
          "capabilities"
        ).GetProperty(
          "vision"
        ).GetBoolean()
      );
      Assert.IsTrue(
        alpha.GetProperty(
          "capabilities"
        ).GetProperty(
          "structuredOutput"
        ).GetBoolean()
      );
      Assert.IsTrue(
        alpha.GetProperty(
          "conformanceApproved"
        ).GetBoolean()
      );
      StringAssert.Contains(
        alpha.GetProperty(
          "conformanceIdentity"
        ).GetString(),
        "0.13.5-test"
      );
      Assert.IsTrue(
        tools.GetProperty(
          "capabilities"
        ).GetProperty(
          "nativeTools"
        ).GetBoolean()
      );
      Assert.IsTrue(
        hidden.GetProperty(
          "hidden"
        ).GetBoolean()
      );
      var localModels = models.Where(
        model => model.GetProperty(
          "providerId"
        ).GetString() == "ollama-local"
      ).ToArray();
      var toolsIndex = Array.FindIndex(
        localModels,
        model => model.GetProperty(
          "qualifiedId"
        ).GetString() == "command-r:latest"
      );
      var alphaIndex = Array.FindIndex(
        localModels,
        model => model.GetProperty(
          "qualifiedId"
        ).GetString() == "alpha:latest"
      );
      Assert.IsTrue(
        toolsIndex >= 0 && toolsIndex < alphaIndex
      );
    }

    using (
      var unavailable = await _environment.HttpClient.PostAsJsonAsync(
        "api/model-organization/profiles",
        new
        {
          id = "unavailable-profile",
          name = "Unavailable",
          primaryModel = "missing:latest",
          fallbackModel = "none",
          routerModel = "router:latest",
          coordinatorModel = "router:latest",
          webPreference = "off",
          comparisonModel = (string?)null,
          usageWindow = (string?)null
        }
      )
    )
    {
      Assert.AreEqual(
        HttpStatusCode.BadRequest,
        unavailable.StatusCode
      );
      StringAssert.Contains(
        await unavailable.Content.ReadAsStringAsync(),
        "primary model 'missing:latest' is unavailable"
      );
    }

    using (
      var cloudWithoutFallback = await _environment.HttpClient.PostAsJsonAsync(
        "api/model-organization/profiles",
        new
        {
          id = "unsafe-cloud-profile",
          name = "Unsafe cloud",
          primaryModel = "groq::openai/gpt-oss-120b",
          fallbackModel = "none",
          routerModel = "router:latest",
          coordinatorModel = "router:latest",
          webPreference = "available",
          comparisonModel = (string?)null,
          usageWindow = "rolling-hour"
        }
      )
    )
    {
      Assert.AreEqual(
        HttpStatusCode.BadRequest,
        cloudWithoutFallback.StatusCode
      );
      StringAssert.Contains(
        await cloudWithoutFallback.Content.ReadAsStringAsync(),
        "cloud primary requires one available Ollama Local fallback"
      );
    }

    var inferenceRequestsBeforeProfileSave =
      _environment.FakeOllama.AllRequests.Count(request => request.Stream);
    using var savedProfile = await _environment.HttpClient.PostAsJsonAsync(
      "api/model-organization/profiles",
      new
      {
        id = "balanced-cloud",
        name = "Balanced Cloud",
        primaryModel = "groq::openai/gpt-oss-120b",
        fallbackModel = "alpha:latest",
        routerModel = "command-r:latest",
        coordinatorModel = "router:latest",
        webPreference = "available",
        comparisonModel = "groq::openai/gpt-oss-120b",
        usageWindow = "rolling-seven-days"
      }
    );
    savedProfile.EnsureSuccessStatusCode();
    using var savedProfileDocument = JsonDocument.Parse(
      await savedProfile.Content.ReadAsStringAsync()
    );
    Assert.IsTrue(
      savedProfileDocument.RootElement.GetProperty(
        "localFallbackValid"
      ).GetBoolean()
    );
    Assert.HasCount(
      inferenceRequestsBeforeProfileSave,
      _environment.FakeOllama.AllRequests.Where(request => request.Stream));

    var workspaceId = await ActiveWorkspaceIdAsync();
    string workspaceName;
    using (
      var preferred = await _environment.HttpClient.PutAsJsonAsync(
        $"api/model-organization/workspaces/{workspaceId}/preferred-profile",
        new
        {
          profileId = "balanced-cloud"
        }
      )
    )
    {
      preferred.EnsureSuccessStatusCode();
      using var workspace = JsonDocument.Parse(
        await preferred.Content.ReadAsStringAsync()
      );
      var active = workspace.RootElement.GetProperty(
        "profiles"
      ).EnumerateArray().Single(
        item => item.GetProperty(
          "id"
        ).GetString() == workspaceId
      );
      Assert.AreEqual(
        "balanced-cloud",
        active.GetProperty(
          "preferredModelProfileId"
        ).GetString()
      );
      workspaceName = active.GetProperty(
        "name"
      ).GetString()!;
    }

    using (
      var preview = await _environment.HttpClient.GetAsync(
        "api/model-organization/profiles/balanced-cloud/preview"
      )
    )
    {
      preview.EnsureSuccessStatusCode();
      using var document = JsonDocument.Parse(
        await preview.Content.ReadAsStringAsync()
      );
      CollectionAssert.Contains(
        document.RootElement.GetProperty(
          "affectedWorkspaces"
        ).EnumerateArray().Select(
          item => item.GetString()
        ).ToArray(),
        workspaceName
      );
      CollectionAssert.AreEquivalent(
        new[]
        {
          "primary",
          "fallback",
          "router",
          "coordinator"
        },
        document.RootElement.GetProperty(
          "chain"
        ).EnumerateArray().Select(
          item => item.GetProperty(
            "role"
          ).GetString()
        ).ToArray()
      );
    }

    using (
      var unconfirmed = await _environment.HttpClient.PostAsJsonAsync(
        "api/model-organization/profiles/balanced-cloud/apply",
        new
        {
          confirmed = false
        }
      )
    )
    {
      Assert.AreEqual(
        HttpStatusCode.BadRequest,
        unconfirmed.StatusCode
      );
    }

    using (
      var applied = await _environment.HttpClient.PostAsJsonAsync(
        "api/model-organization/profiles/balanced-cloud/apply",
        new
        {
          confirmed = true
        }
      )
    )
    {
      applied.EnsureSuccessStatusCode();
      using var document = JsonDocument.Parse(
        await applied.Content.ReadAsStringAsync()
      );
      Assert.IsTrue(
        document.RootElement.GetProperty(
          "applied"
        ).GetBoolean()
      );
    }
    Assert.HasCount(
      inferenceRequestsBeforeProfileSave,
      _environment.FakeOllama.AllRequests.Where(request => request.Stream));

    var settings = await GetSettingsJsonAsync();
    Assert.AreEqual(
      "groq::openai/gpt-oss-120b",
      settings["defaultModel"]!.GetValue<string>()
    );
    Assert.AreEqual(
      "command-r:latest",
      settings["routerModel"]!.GetValue<string>()
    );
    Assert.AreEqual(
      "router:latest",
      settings["coordinatorModel"]!.GetValue<string>()
    );

    foreach (var intention in settings["intentions"]!.AsObject())
    {
      Assert.AreEqual(
        "groq::openai/gpt-oss-120b",
        intention.Value!["model"]!.GetValue<string>()
      );
      Assert.AreEqual(
        "alpha:latest",
        intention.Value["fallbackModel"]!.GetValue<string>()
      );
    }

    Assert.AreEqual(
      "rolling-seven-days",
      settings["usage"]!["selectedWindow"]!.GetValue<string>()
    );

    using (
      var yamlResponse = await _environment.HttpClient.GetAsync(
        "api/settings/yaml"
      )
    )
    {
      yamlResponse.EnsureSuccessStatusCode();
      var yaml = await yamlResponse.Content.ReadAsStringAsync();
      Assert.DoesNotContain(
        groqKey,
        yaml
      );
      Assert.DoesNotContain(
        "Z Vision",
        yaml
      );
      Assert.DoesNotContain(
        "Balanced Cloud",
        yaml
      );
      Assert.DoesNotContain(
        "Preferred visual model",
        yaml
      );
      Assert.DoesNotContain(
        "private-history-marker-v097",
        yaml
      );
    }

    var storedOrganization = await File.ReadAllTextAsync(
      Path.Combine(
        _environment.DataDirectory,
        "model-organization.json"
      )
    );
    StringAssert.Contains(
      storedOrganization,
      "\"alias\": \"Z Vision\""
    );
    StringAssert.Contains(
      storedOrganization,
      "\"favorite\": true"
    );
    StringAssert.Contains(
      storedOrganization,
      "\"id\": \"balanced-cloud\""
    );

    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#model-selector option[value=\"docs:latest\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "alpha:latest"
    );
    await OpenSettingsAsync();
    await Page.Locator(
      "[data-settings-target=\"models-routing\"]"
    ).ClickAsync();
    await Page.Locator(
      ".model-organization-panel"
    ).Nth(
      0
    ).Locator(
      "summary"
    ).ClickAsync();

    var organizationCards = Page.Locator(
      "#model-organization-list .model-organization-card"
    );
    await Expect(
      Page.Locator(
        "#model-organization-list "
          + ".model-organization-card[data-model-identity=\"docs:latest\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Page.Locator(
      "#model-filter-location"
    ).SelectOptionAsync(
      "local"
    );
    await Page.Locator(
      "#model-filter-tools"
    ).CheckAsync();
    await Expect(
      organizationCards
    ).ToHaveCountAsync(
      6
    );
    await Expect(
      Page.Locator(
        "#model-organization-list "
          + ".model-organization-card[data-model-identity=\"qwen3-coder:30b\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      organizationCards.Filter(
        new()
        {
          HasText = "A Tools"
        }
      )
    ).ToHaveCountAsync(
      1
    );
    await Page.Locator(
      "#model-filter-tools"
    ).UncheckAsync();
    await Page.Locator(
      "#model-filter-vision"
    ).CheckAsync();
    await Page.Locator(
      "#model-filter-conformance"
    ).CheckAsync();
    await Expect(
      organizationCards
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      organizationCards
    ).ToContainTextAsync(
      "Z Vision"
    );
    await Expect(
      organizationCards
    ).ToContainTextAsync(
      "alpha:latest"
    );
    await Page.Locator(
      "#model-filter-vision"
    ).UncheckAsync();
    await Page.Locator(
      "#model-filter-conformance"
    ).UncheckAsync();
    await Page.Locator(
      "#model-filter-hidden"
    ).CheckAsync();
    await Page.Locator(
      "#model-filter-search"
    ).FillAsync(
      "Docs Hidden"
    );
    await Expect(
      organizationCards
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      organizationCards
    ).ToContainTextAsync(
      "docs:latest"
    );

    await Page.Locator(
      ".model-organization-panel"
    ).Nth(
      1
    ).Locator(
      "summary"
    ).ClickAsync();
    await Page.Locator(
      "#model-profile-selector"
    ).SelectOptionAsync(
      "balanced-cloud"
    );
    await Expect(
      Page.Locator(
        "#model-profile-preview"
      )
    ).ToContainTextAsync(
      "PRIMARY"
    );
    await Expect(
      Page.Locator(
        "#model-profile-preview"
      )
    ).ToContainTextAsync(
      "Groq · openai/gpt-oss-120b"
    );
    await Expect(
      Page.Locator(
        "#model-chain-preview"
      )
    ).ToContainTextAsync(
      "Groq · openai/gpt-oss-120b"
    );

    await Page.Locator(
      "#apply-model-profile"
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#model-profile-status"
      )
    ).ToContainTextAsync(
      "current conversation selection was preserved"
    );
    await Expect(
      Page.Locator(
        "#harness-selector"
      )
    ).ToHaveValueAsync("native");
    await Expect(
      Page.Locator(
        "#model-selector"
      )
    ).ToHaveValueAsync(
      "alpha:latest"
    );
    Assert.HasCount(
      inferenceRequestsBeforeProfileSave,
      _environment.FakeOllama.AllRequests.Where(request => request.Stream));

    settings = await GetSettingsJsonAsync();
    settings["defaultModel"] = "docs:latest";
    using (
      var saved = await PutSettingsJsonAsync(
        settings
      )
    )
    {
      saved.EnsureSuccessStatusCode();
    }
    await Page.ReloadAsync();
    await OpenSettingsAsync();
    await Expect(
      Page.Locator(
        "#default-model"
      )
    ).ToHaveValueAsync(
      "docs:latest"
    );
    await Expect(
      Page.Locator(
        "#default-model option[value=\"docs:latest\"]"
      )
    ).ToContainTextAsync(
      "unavailable"
    );

    var routed = await PostChatStreamAsync(
      "Route normally after model organization.",
      "alpha:latest",
      "browser-organization-v097"
    );
    StringAssert.Contains(
      routed,
      "Hello from alpha:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CanonicalTracePersistsSanitizedFailureAndUsageAcrossRestart()
  {
    const string secretMarker = "PROMPT-MUST-NOT-ENTER-INCIDENT-JOURNAL";
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = secretMarker + new string('x', 132_000),
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "chat",
        approvalPolicy = "ask",
        browserSessionId = "browser-trace-v0913"
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    var error = events.Last(item => item["type"]!.GetValue<string>() == "error")["error"]!.AsObject();
    var traceId = error["traceId"]!.GetValue<string>();
    var diagnostic = events.Last(item => item["type"]!.GetValue<string>() == "error")["diagnostic"]!.AsObject();
    Assert.AreEqual(traceId, diagnostic["traceId"]!.GetValue<string>());
    Assert.AreEqual("failed", diagnostic["terminalState"]!.GetValue<string>());
    Assert.IsTrue(diagnostic["persisted"]!.GetValue<bool>());
    Assert.AreEqual("request-context-does-not-fit", error["code"]!.GetValue<string>());
    Assert.IsTrue(error["diagnosticsPersisted"]!.GetValue<bool>());

    using (var lookup = await _environment.HttpClient.GetAsync($"api/diagnostics/traces/{Uri.EscapeDataString(traceId)}"))
    {
      lookup.EnsureSuccessStatusCode();
      var report = await lookup.Content.ReadFromJsonAsync<JsonElement>(TestJson.Options);
      Assert.AreEqual(traceId, report.GetProperty("traceId").GetString());
      Assert.AreEqual("request-context-does-not-fit", report.GetProperty("failureCode").GetString());
      Assert.IsGreaterThan(
        report.GetProperty("contextFit").GetProperty("maximumContextTokens").GetInt32(),
        report.GetProperty("contextFit").GetProperty("requiredContextTokens").GetInt32()
      );
    }

    var incidentText = string.Join("\n", Directory.GetFiles(Path.Combine(_environment.DataDirectory, "incidents"), "*.jsonl").Select(File.ReadAllText));
    Assert.DoesNotContain(secretMarker, incidentText);
    Assert.DoesNotContain(new string('x', 1_000), incidentText);

    var usageText = string.Join("\n", Directory.GetFiles(Path.Combine(_environment.DataDirectory, "usage"), "*.jsonl").Select(File.ReadAllText));
    StringAssert.Contains(usageText, $"\"traceId\":\"{traceId}\"");
    StringAssert.Contains(usageText, "\"errorCode\":\"request-context-does-not-fit\"");
    StringAssert.Contains(usageText, "\"requiredContextTokens\":");
    using var usageDocument = JsonDocument.Parse(
      usageText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .First(line => line.Contains($"\"traceId\":\"{traceId}\"", StringComparison.Ordinal))
    );
    var linkedIncidentEventId = usageDocument.RootElement.GetProperty("incidentEventId").GetString();
    Assert.IsFalse(string.IsNullOrWhiteSpace(linkedIncidentEventId));
    StringAssert.Contains(incidentText, $"\"eventId\":\"{linkedIncidentEventId}\"");

    var diagnosticTool = await RunPowerShellAsync(
      Path.Combine(_environment.RepositoryRoot, "tools", "diagnostics", "Find-AgenticRouterTrace.ps1"),
      "-TraceId",
      traceId,
      "-DataDirectory",
      _environment.DataDirectory,
      "-Format",
      "Json"
    );
    Assert.AreEqual(0, diagnosticTool.ExitCode, diagnosticTool.Error);
    using var diagnosticDocument = JsonDocument.Parse(diagnosticTool.Output);
    Assert.AreEqual(traceId, diagnosticDocument.RootElement.GetProperty("traceId").GetString());
    StringAssert.Contains(diagnosticTool.Output, "request-context-does-not-fit");

    await _environment.RestartApplicationAsync();
    using var restarted = await _environment.HttpClient.GetAsync($"api/diagnostics/traces/{Uri.EscapeDataString(traceId)}");
    restarted.EnsureSuccessStatusCode();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task TraceLookupIsExactBoundedAndToleratesMalformedTail()
  {
    var stream = await PostChatStreamAsync("Create trace evidence.", "alpha:latest", "browser-trace-lookup-v0913");
    var events = ParseSseEvents(stream);
    var completedEvent = events.Last(item => item["type"]!.GetValue<string>() == "response.completed");
    var traceId = completedEvent["requestId"]!.GetValue<string>();
    var canonicalTrace = completedEvent["diagnostic"]!["traceId"]!.GetValue<string>();
    Assert.AreEqual("completed", completedEvent["diagnostic"]!["terminalState"]!.GetValue<string>());
    Assert.IsTrue(completedEvent["diagnostic"]!["persisted"]!.GetValue<bool>());

    var incidentFile = Directory.GetFiles(Path.Combine(_environment.DataDirectory, "incidents"), "*.jsonl").OrderBy(path => path, StringComparer.Ordinal).Last();
    var persisted = File.ReadLines(incidentFile).Select(line => JsonNode.Parse(line)!.AsObject()).Last(item => item["requestId"]?.GetValue<string>() == traceId);
    Assert.AreEqual(canonicalTrace, persisted["traceId"]!.GetValue<string>());
    await File.AppendAllTextAsync(incidentFile, $"{{\"traceId\":\"{canonicalTrace}\"\n");

    using var lookup = await _environment.HttpClient.GetAsync($"api/diagnostics/traces/{Uri.EscapeDataString(canonicalTrace)}");
    var lookupText = await lookup.Content.ReadAsStringAsync();
    Assert.AreEqual(HttpStatusCode.OK, lookup.StatusCode, lookupText + Environment.NewLine + _environment.ApiOutput);
    using var reportDocument = JsonDocument.Parse(lookupText);
    var report = reportDocument.RootElement;
    Assert.IsGreaterThanOrEqualTo(1, report.GetProperty("malformedRecordCount").GetInt32());
    Assert.IsGreaterThan(0, report.GetProperty("totalEvents").GetInt32());

    using var wildcard = await _environment.HttpClient.GetAsync("api/diagnostics/traces/%2A");
    Assert.AreEqual(HttpStatusCode.BadRequest, wildcard.StatusCode);
    using var unknown = await _environment.HttpClient.GetAsync("api/diagnostics/traces/unknown-valid-trace");
    Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "diagnostic-trace-not-found");

    using var investigation = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "TRACE_DIAGNOSTIC_INVESTIGATION_V1\n"
          + $"Trace ID: {canonicalTrace}\n"
          + "Analyze only the sanitized Host evidence.",
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "chat",
        approvalPolicy = "auto",
        browserSessionId = "browser-trace-investigation-v61",
        diagnosticTraceId = canonicalTrace,
        hideUserMessage = true
      }
    );
    investigation.EnsureSuccessStatusCode();
    var investigationEvents = ParseSseEvents(
      await investigation.Content.ReadAsStringAsync()
    );
    Assert.IsTrue(
      investigationEvents.Any(
        item => item["type"]!.GetValue<string>() == "chat.diagnostic-read-completed"
      )
    );
    Assert.AreEqual(
      "response.completed",
      investigationEvents.Last()["type"]!.GetValue<string>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task IncidentSettingsRejectInvalidLimitsAtomically()
  {
    var before = await GetSettingsJsonAsync();
    var invalid = before.DeepClone().AsObject();
    invalid["incidents"]!["maximumFileBytes"] = 8_388_608;
    invalid["incidents"]!["maximumTotalBytes"] = 65_536;
    using var save = await PutSettingsJsonAsync(invalid);
    Assert.AreEqual(HttpStatusCode.BadRequest, save.StatusCode);
    StringAssert.Contains(await save.Content.ReadAsStringAsync(), "incidents.maximumTotalBytes");
    var after = await GetSettingsJsonAsync();
    Assert.AreEqual(
      before["incidents"]!["maximumTotalBytes"]!.GetValue<long>(),
      after["incidents"]!["maximumTotalBytes"]!.GetValue<long>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BrowserErrorOffersOneTransparentDiagnosticInvestigation()
  {
    await Page.GotoAsync("/");
    await SelectFixtureModelAsync("alpha:latest");
    await StartMessageAsync("generic HTTP failure");
    await Expect(
      Page.Locator(".message.assistant .activity").Last
    ).ToHaveAttributeAsync(
      "data-terminal",
      "true",
      new()
      {
        Timeout = 20_000
      }
    );
    await Expect(Page.Locator(".trace-diagnostic-actions")).ToBeVisibleAsync();
    var buttons = Page.Locator(".trace-diagnostic-actions button");
    await Expect(buttons).ToHaveCountAsync(1);
    await Expect(buttons).ToHaveTextAsync("Investigate error");
    await Expect(Page.Locator(".message.assistant .activity > summary").Last).ToContainTextAsync("Trace:");
    var visibleUserMessages = Page.Locator(".message.user");
    await Expect(visibleUserMessages).ToHaveCountAsync(1);

    await buttons.ClickAsync();

    await Expect(
      Page.Locator(".message.assistant .assistant-answer").Last
    ).ToContainTextAsync(
      "Diagnostic investigation completed from sanitized Host evidence"
    );
    await Expect(visibleUserMessages).ToHaveCountAsync(1);
    await Expect(Page.Locator(".trace-diagnostic-actions")).ToHaveCountAsync(1);
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Role == "system"
            && message.Content.Contains(
              "APPLICATION_OWNED_TRACE_DIAGNOSTIC_V1",
              StringComparison.Ordinal
            )
            && message.Content.Contains(
              "\"failureCode\"",
              StringComparison.Ordinal
            )
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BrowserCompletedTurnShowsTraceWithoutInvestigationAction()
  {
    await Page.GotoAsync("/");
    await SelectFixtureModelAsync("alpha:latest");
    await SendMessageAsync("Completed trace presentation.");

    var assistant = Page.Locator(".message.assistant").Last;
    await Expect(assistant.Locator(".activity > summary")).ToContainTextAsync("Completed");
    await Expect(assistant.Locator(".activity > summary")).ToContainTextAsync("Trace:");
    await Expect(assistant.Locator(".trace-diagnostic-actions")).ToHaveCountAsync(0);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DiagnosticReferencesAndHiddenInvestigationPersistAcrossResume()
  {
    using var profiles = await _environment.HttpClient.GetAsync(
      "api/workspaces"
    );
    profiles.EnsureSuccessStatusCode();
    using var profilesDocument = JsonDocument.Parse(
      await profiles.Content.ReadAsStringAsync()
    );
    var matching = profilesDocument.RootElement.GetProperty("profiles")
      .EnumerateArray()
      .FirstOrDefault(
        profile => string.Equals(
          Path.GetFullPath(profile.GetProperty("path").GetString()!),
          Path.GetFullPath(_environment.WorkspaceDirectory),
          StringComparison.OrdinalIgnoreCase
        )
      );
    string workspaceId;
    if (matching.ValueKind == JsonValueKind.Object)
    {
      workspaceId = matching.GetProperty("id").GetString()!;
      using var activated = await _environment.HttpClient.PostAsync(
        $"api/workspaces/{workspaceId}/activate",
        null
      );
      activated.EnsureSuccessStatusCode();
    }
    else
    {
      using var workspace = await _environment.HttpClient.PostAsJsonAsync(
        "api/workspaces",
        new
        {
          name = "Trace persistence",
          path = _environment.WorkspaceDirectory
        }
      );
      workspace.EnsureSuccessStatusCode();
      using var workspaceDocument = JsonDocument.Parse(
        await workspace.Content.ReadAsStringAsync()
      );
      workspaceId = workspaceDocument.RootElement.GetProperty("id").GetString()!;
    }
    using (var history = await _environment.HttpClient.PutAsJsonAsync(
      $"api/workspaces/{workspaceId}/history",
      new
      {
        enabled = true
      }
    ))
    {
      history.EnsureSuccessStatusCode();
    }

    var firstStream = await PostChatStreamAsync(
      "Persist completed trace metadata.",
      "alpha:latest",
      "browser-trace-persistence-v61"
    );
    var firstEvents = ParseSseEvents(firstStream);
    var firstCompleted = firstEvents.Last(
      item => item["type"]!.GetValue<string>() == "response.completed"
    );
    var traceId = firstCompleted["diagnostic"]!["traceId"]!.GetValue<string>();
    var sessionId = firstCompleted["conversationSessionId"]!.GetValue<string>();

    using var investigation = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "TRACE_DIAGNOSTIC_INVESTIGATION_V1\n"
          + $"Trace ID: {traceId}\n"
          + "Analyze only the sanitized Host evidence.",
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "chat",
        approvalPolicy = "auto",
        browserSessionId = "browser-trace-persistence-v61",
        conversationSessionId = sessionId,
        diagnosticTraceId = traceId,
        hideUserMessage = true
      }
    );
    investigation.EnsureSuccessStatusCode();
    Assert.AreEqual(
      "response.completed",
      ParseSseEvents(await investigation.Content.ReadAsStringAsync())
        .Last()["type"]!.GetValue<string>()
    );

    using var resumed = await _environment.HttpClient.PostAsJsonAsync(
      $"api/sessions/{sessionId}/resume",
      new
      {
        browserSessionId = "browser-trace-persistence-resume-v61"
      }
    );
    resumed.EnsureSuccessStatusCode();
    using var resumedDocument = JsonDocument.Parse(
      await resumed.Content.ReadAsStringAsync()
    );
    var messages = resumedDocument.RootElement.GetProperty("messages")
      .EnumerateArray()
      .ToArray();
    Assert.HasCount(4, messages);
    Assert.AreEqual(
      traceId,
      messages[1].GetProperty("diagnostic").GetProperty("traceId").GetString()
    );
    Assert.IsTrue(messages[2].GetProperty("hidden").GetBoolean());
    StringAssert.Contains(
      messages[2].GetProperty("content").GetString()!,
      "TRACE_DIAGNOSTIC_INVESTIGATION_V1"
    );
    Assert.IsFalse(
      messages[3].TryGetProperty("hidden", out var hidden)
        && hidden.GetBoolean()
    );
  }

  private async Task SelectFixtureModelAsync(string model)
  {
    await Page.EvaluateAsync(
      "if (!resizeObserver) initializeScrollFollowing();"
    );
    await Page.Locator("#model-selector").EvaluateAsync(
      "(select, model) => { const option = document.createElement('option'); option.value = model; option.textContent = model; select.append(option); select.value = model; select.dispatchEvent(new Event('change', { bubbles: true })); }",
      model
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OversizedCoordinatorHistoryIsCompactedBeforeNativePlanning()
  {
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"), "hello");
    var settings = await GetSettingsJsonAsync();
    foreach (var role in new[] { "specialist", "primary", "fallback" })
    {
      var profile = settings["ollamaRuntime"]!["roleDefaults"]![role]!.AsObject();
      profile["targetContextTokens"] = 8_192;
      profile["maximumContextTokens"] = 8_192;
    }
    using (var saved = await PutSettingsJsonAsync(settings))
    {
      saved.EnsureSuccessStatusCode();
    }

    var history = Enumerable.Range(0, 24).Select(index => new
    {
      role = index % 2 == 0 ? "user" : "assistant",
      content = $"OLD-CONTEXT-{index:D2}-MUST-BE-COMPACTED " + new string((char)('a' + index % 20), 700)
    }).ToArray();
    _environment.FakeOllama.Reset();
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "execute read file",
        model = "command-r:latest",
        history,
        interactionMode = "execute",
        approvalPolicy = "auto",
        browserSessionId = "browser-context-compaction-v0913"
      }
    );
    response.EnsureSuccessStatusCode();
    var stream = await response.Content.ReadAsStringAsync();
    StringAssert.Contains(stream, "request-context-compacted");
    StringAssert.Contains(stream, "response.completed");
    var planningRequests = _environment.FakeOllama.Requests.Where(
      request => request.Model == "command-r:latest"
        && request.HasTools
        && request.Messages.Any(message => message.Content.Contains("SPECIALIST_TOOL_LOOP_V2", StringComparison.Ordinal))
    ).ToArray();
    Assert.IsNotEmpty(planningRequests);
    Assert.IsFalse(planningRequests.Any(request => request.Messages.Any(message => message.Content.Contains("OLD-CONTEXT-00-MUST-BE-COMPACTED", StringComparison.Ordinal))));
    Assert.IsTrue(planningRequests.Any(request => request.Messages.Any(message => message.Content.Contains("APPLICATION_OWNED_EXECUTION_STATE_V1", StringComparison.Ordinal))));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RequiredOversizedCoordinatorItemFailsTypedWithoutPlannerRetry()
  {
    var settings = await GetSettingsJsonAsync();
    foreach (var role in new[] { "specialist", "primary", "fallback" })
    {
      var profile = settings["ollamaRuntime"]!["roleDefaults"]![role]!.AsObject();
      profile["targetContextTokens"] = 8_192;
      profile["maximumContextTokens"] = 8_192;
    }
    using (var saved = await PutSettingsJsonAsync(settings))
    {
      saved.EnsureSuccessStatusCode();
    }

    _environment.FakeOllama.Reset();
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "execute " + new string('q', 30_000),
        model = "command-r:latest",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        approvalPolicy = "auto",
        browserSessionId = "browser-context-item-v0913"
      }
    );
    response.EnsureSuccessStatusCode();
    var stream = await response.Content.ReadAsStringAsync();
    StringAssert.Contains(stream, "context-item-too-large");
    StringAssert.Contains(stream, "request-context-exhausted");
    Assert.IsFalse(_environment.FakeOllama.Requests.Any(
      request => request.HasTools
        && request.Messages.Any(message => message.Content.Contains("SPECIALIST_TOOL_LOOP_V2", StringComparison.Ordinal))
    ));
  }

  [TestMethod]
  [DataRow("opencode", "plan host bridge opencode", "opencode-runtime", "fake-opencode-host-plan.json")]
  [DataRow("qwen-code", "plan host bridge qwen code", "qwen-code-runtime", "fake-qwen-host-plan.json")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalHarnessHostBridgeSupportsOptionalVisiblePlan(
    string harness,
    string prompt,
    string runtimeName,
    string markerName
  )
  {
    var events = await ExecuteHarnessStreamAsync(
      harness,
      prompt,
      $"browser-{harness}-host-bridge-plan",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "execution-plan-created")
    );
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "action.input-rejected")
    );
    Assert.HasCount(
      2,
      events.Where(item => item["type"]!.GetValue<string>() == "execution-step-started")
    );
    Assert.HasCount(
      2,
      events.Where(item => item["type"]!.GetValue<string>() == "execution-step-completed")
    );
    Assert.IsTrue(
      events.Any(item => item["type"]!.GetValue<string>() == "action.completed")
    );
    using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
      _environment.DataDirectory,
      runtimeName,
      markerName
    )));
    Assert.IsTrue(marker.RootElement.GetProperty("succeeded").GetBoolean());
    var unboundAction = marker.RootElement.GetProperty("unboundAction");
    Assert.IsFalse(unboundAction.GetProperty("succeeded").GetBoolean());
    var unboundOutput = unboundAction.GetProperty("output").GetString()!;
    StringAssert.Contains(unboundOutput, "must bind every action");
    StringAssert.Contains(unboundOutput, "\"ActionableStepIds\":[\"step-1\"]");
    var firstAction = marker.RootElement.GetProperty("firstAction");
    Assert.IsTrue(firstAction.GetProperty("succeeded").GetBoolean());
    var firstOutput = firstAction.GetProperty("output").GetString()!;
    StringAssert.Contains(firstOutput, "HOST_OWNED_PLAN_STATE");
    StringAssert.Contains(
      firstOutput,
      "\"Id\":\"step-1\",\"Title\":\"Run the first Host process\",\"Status\":\"completed\""
    );
    StringAssert.Contains(firstOutput, "\"ActionableStepIds\":[\"step-2\"]");
    var secondAction = marker.RootElement.GetProperty("secondAction");
    Assert.IsTrue(secondAction.GetProperty("succeeded").GetBoolean());
    var secondOutput = secondAction.GetProperty("output").GetString()!;
    StringAssert.Contains(secondOutput, "\"ActionableStepIds\":[]");
    StringAssert.Contains(secondOutput, "accepted Host plan has no actionable steps");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConcurrentTraceWritesRemainValidOrderedAndRotateBySize()
  {
    var settings = await GetSettingsJsonAsync();
    settings["incidents"]!["maximumFileBytes"] = 65_536;
    settings["incidents"]!["maximumTotalBytes"] = 1_048_576;
    using (var saved = await PutSettingsJsonAsync(settings))
    {
      saved.EnsureSuccessStatusCode();
    }
    var incidentDirectory = Path.Combine(_environment.DataDirectory, "incidents");
    var malformedBefore = Directory.Exists(incidentDirectory)
      ? Directory.GetFiles(incidentDirectory, "*.jsonl")
        .SelectMany(File.ReadLines)
        .Count(line => !TryParseJsonObject(line, out _))
      : 0;

    var requests = Enumerable.Range(0, 20).Select(async index =>
    {
      using var response = await _environment.HttpClient.PostAsJsonAsync(
        "api/chat/stream",
        new
        {
          message = $"Concurrent trace request {index}.",
          model = "alpha:latest",
          history = Array.Empty<object>(),
          interactionMode = "chat",
          approvalPolicy = "ask",
          browserSessionId = $"browser-concurrent-v0913-{index}"
        }
      );
      response.EnsureSuccessStatusCode();
      return ParseSseEvents(await response.Content.ReadAsStringAsync())
        .Last(item => item["type"]!.GetValue<string>() == "response.completed")["requestId"]!.GetValue<string>();
    });
    var requestIds = await Task.WhenAll(requests);
    var files = Directory.GetFiles(incidentDirectory, "*.jsonl");
    Assert.IsGreaterThanOrEqualTo(2, files.Length);
    var allLines = files.SelectMany(File.ReadLines).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
    var records = allLines.Select(line => TryParseJsonObject(line, out var item) ? item : null).OfType<JsonObject>().ToArray();
    Assert.IsLessThanOrEqualTo(malformedBefore, allLines.Length - records.Length);
    foreach (var requestId in requestIds)
    {
      var traceRecords = records.Where(item => item["requestId"]?.GetValue<string>() == requestId).OrderBy(item => item["sequence"]!.GetValue<long>()).ToArray();
      Assert.IsNotEmpty(traceRecords);
      var sequences = traceRecords.Select(item => item["sequence"]!.GetValue<long>()).ToArray();
      Assert.HasCount(sequences.Length, sequences.Distinct().ToArray());
      Assert.IsTrue(sequences.Zip(sequences.Skip(1), (previous, next) => next > previous).All(value => value));
      Assert.HasCount(1, traceRecords.Select(item => item["traceId"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray());
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StructuredSpecialistHistoryUsesTheSameTokenBudget()
  {
    var settings = await GetSettingsJsonAsync();
    foreach (var role in new[] { "specialist", "primary", "fallback" })
    {
      var profile = settings["ollamaRuntime"]!["roleDefaults"]![role]!.AsObject();
      profile["targetContextTokens"] = 8_192;
      profile["maximumContextTokens"] = 8_192;
    }
    using (var saved = await PutSettingsJsonAsync(settings))
    {
      saved.EnsureSuccessStatusCode();
    }
    var history = Enumerable.Range(0, 40).Select(index => new
    {
      role = index % 2 == 0 ? "user" : "assistant",
      content = $"STRUCTURED-OLD-{index:D2} " + new string('s', 1_200)
    }).ToArray();
    _environment.FakeOllama.Reset();
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "execute create file",
        model = "alpha:latest",
        history,
        interactionMode = "execute",
        approvalPolicy = "auto",
        browserSessionId = "browser-structured-context-v0913"
      }
    );
    response.EnsureSuccessStatusCode();
    var stream = await response.Content.ReadAsStringAsync();
    StringAssert.Contains(stream, "request-context-compacted");
    StringAssert.Contains(stream, "direct-structured");
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(
      request => request.Model == "alpha:latest"
        && request.Messages.Any(message => message.Content.Contains("EXPERT_EXECUTION_GUIDANCE_V1", StringComparison.Ordinal))
        && request.Messages.Any(message => message.Content.Contains("APPLICATION_OWNED_EXECUTION_STATE_V1", StringComparison.Ordinal))
        && !request.Messages.Any(message => message.Content.Contains("STRUCTURED-OLD-00", StringComparison.Ordinal))
    ));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task JournalWriteFailureDoesNotReplacePrimaryChatFailure()
  {
    var incidentDirectory = Path.Combine(_environment.DataDirectory, "incidents");
    var retainedDirectory = incidentDirectory + ".retained-v0913";
    if (Directory.Exists(retainedDirectory))
    {
      Directory.Delete(retainedDirectory, true);
    }
    if (Directory.Exists(incidentDirectory))
    {
      Directory.Move(incidentDirectory, retainedDirectory);
    }
    await File.WriteAllTextAsync(incidentDirectory, "block directory creation");

    try
    {
      using var response = await _environment.HttpClient.PostAsJsonAsync(
        "api/chat/stream",
        new
        {
          message = "",
          model = "alpha:latest",
          history = Array.Empty<object>(),
          interactionMode = "chat",
          approvalPolicy = "ask"
        }
      );
      response.EnsureSuccessStatusCode();
      var error = ParseSseEvents(await response.Content.ReadAsStringAsync())
        .Single(item => item["type"]!.GetValue<string>() == "error")["error"]!.AsObject();
      Assert.AreEqual("Enter a message before sending.", error["message"]!.GetValue<string>());
      Assert.IsFalse(error["diagnosticsPersisted"]!.GetValue<bool>());
      StringAssert.Contains(_environment.ApiOutput, "Incident journal append failed");
    }
    finally
    {
      File.Delete(incidentDirectory);
      if (Directory.Exists(retainedDirectory))
      {
        Directory.Move(retainedDirectory, incidentDirectory);
      }
    }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task CompactProjectGitCommitsExplicitAndGeneratedMessagesAndReportsMissingUpstream()
  {
    var workspace = _environment.CreateWorkspaceDirectory(
      $"compact-project-git-{Guid.NewGuid():N}"
    );
    _ = await RunGitTextAsync(workspace, "init", "-b", "main");
    _ = await RunGitTextAsync(workspace, "config", "user.name", "Project Git E2E");
    _ = await RunGitTextAsync(
      workspace,
      "config",
      "user.email",
      "project-git@example.invalid"
    );
    await File.WriteAllTextAsync(
      Path.Combine(workspace, "baseline.txt"),
      "baseline"
    );
    _ = await RunGitTextAsync(workspace, "add", "--", "baseline.txt");
    _ = await RunGitTextAsync(workspace, "commit", "-m", "baseline");
    using var createdResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/workspaces",
      new
      {
        name = "Compact project Git",
        path = workspace
      }
    );
    createdResponse.EnsureSuccessStatusCode();
    using var createdDocument = JsonDocument.Parse(
      await createdResponse.Content.ReadAsStringAsync()
    );
    var workspaceId = createdDocument.RootElement.GetProperty("id").GetString()!;
    using (
      var activateResponse = await _environment.HttpClient.PostAsync(
        $"api/workspaces/{workspaceId}/activate",
        null
      )
    )
    {
      activateResponse.EnsureSuccessStatusCode();
    }
    await File.WriteAllTextAsync(
      Path.Combine(workspace, "explicit.txt"),
      "explicit"
    );

    await Page.GotoAsync("/");
    await SetExecuteModeAsync("auto");
    await Expect(Page.Locator("#git-summary")).ToContainTextAsync("1 changes");
    await Page.Locator("#git-commit-quick").ClickAsync();
    await Page.Locator("#app-modal-input").FillAsync("feat: exact sidebar commit");
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(Page.Locator("#git-quick-status")).ToContainTextAsync(
      "feat: exact sidebar commit"
    );
    Assert.AreEqual(
      "feat: exact sidebar commit",
      await RunGitTextAsync(workspace, "log", "-1", "--pretty=%s")
    );

    await File.WriteAllTextAsync(
      Path.Combine(workspace, "generated.txt"),
      "generated"
    );
    await Page.Locator("#git-card").ClickAsync();
    await Page.Locator("#dismiss-git").ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await Page.Locator("#git-commit-quick").ClickAsync();
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(Page.Locator("#git-quick-status")).ToContainTextAsync(
      "chore: update project changes"
    );
    Assert.AreEqual(
      "chore: update project changes",
      await RunGitTextAsync(workspace, "log", "-1", "--pretty=%s")
    );
    var generationRequest = _environment.FakeOllama.Requests.Last(
      request => request.Messages.Any(
        message => message.Content.Contains(
          "GIT_COMMIT_SUBJECT_V1",
          StringComparison.Ordinal
        )
      )
    );
    StringAssert.Contains(
      generationRequest.Messages.Last().Content,
      "generated.txt"
    );
    StringAssert.Contains(
      generationRequest.Messages.Last().Content,
      "+generated"
    );

    await Page.Locator("#git-push-quick").ClickAsync();
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(Page.Locator("#git-quick-status")).ToContainTextAsync(
      "no configured upstream"
    );
  }
}
