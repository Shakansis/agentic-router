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
public sealed class ExecuteCoreEndToEndTests : ChatEndToEndTestBase<ExecuteCoreEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeHarnessCreatesUtf8FilesThroughOneHostBatchTool()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");

    await SendMessageAsync("native create host batch files");

    var assistant = Page.Locator(".message.assistant").Last;
    await Expect(assistant.Locator(".activity")).ToHaveAttributeAsync("data-terminal", "true");
    await Expect(assistant).ToContainTextAsync("create_files");
    Assert.AreEqual(
      "<!doctype html><title>Ação nativa</title>\n",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "native-ação.html")
      )
    );
    Assert.AreEqual(
      "/* revisão nativa */\nbody { color: #456; }\n",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "native-estilo.css")
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeCanModifyPreExistingFileWithoutDisturbingUnrelatedUserWork()
  {
    var requested = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    var unrelated = Path.Combine(_environment.WorkspaceDirectory, "unrelated-user-work.txt");
    await File.WriteAllTextAsync(requested, "pre-existing requested content");
    await File.WriteAllTextAsync(unrelated, "unrelated user content");

    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("execute write file");

    Assert.AreEqual("rewritten by agent", await File.ReadAllTextAsync(requested));
    Assert.AreEqual("unrelated user content", await File.ReadAllTextAsync(unrelated));
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessUsesHostCreateFilesAndPreservesUtf8ProtocolText()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await SendMessageAsync("create host batch files");

    var assistant = Page.Locator(".message.assistant").Last;
    await Expect(assistant.Locator(".assistant-reasoning-body")).ToContainTextAsync("revisão");
    await Expect(assistant.Locator(".assistant-answer")).ToContainTextAsync("Ação concluída");
    await Expect(assistant).ToContainTextAsync("create_files");
    await Expect(
      assistant.Locator("[data-event-type=\"validation-completed\"]")
    ).ToContainTextAsync("not-configured");
    var visibleText = await assistant.InnerTextAsync();
    Assert.DoesNotContain("Ã", visibleText);
    Assert.DoesNotContain("ÔÇ", visibleText);
    Assert.AreEqual(
      "<!doctype html><title>Ação</title>\n",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "batch-ação.html")
      )
    );
    Assert.AreEqual(
      "/* revisão */\nbody { color: #123; }\n",
      await File.ReadAllTextAsync(
        Path.Combine(_environment.WorkspaceDirectory, "batch-estilo.css")
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeHarnessRemainsDefaultAndDoesNotStartCodex()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Expect(Page.Locator("#harness-selector")).ToHaveValueAsync("native");

    await SendMessageAsync("execute create file");

    await Expect(
      Page.Locator(".message.assistant [data-event-type=\"harness.codex-selected\"]")
    ).ToHaveCountAsync(0);
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(request => request.Model == "alpha:latest")
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessCancellationInterruptsTheActiveTurn()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await StartMessageAsync("long codex turn");
    await Expect(
      Page.Locator(".message.assistant .assistant-reasoning-body").Last
    ).ToContainTextAsync("Inspecting");
    await Page.Locator("#send-button").ClickAsync();
    await Expect(Page.Locator("#send-button-label")).ToHaveTextAsync("Enviar");
    await Expect(
      Page.Locator(".message.assistant .activity").Last
    ).ToHaveAttributeAsync("data-terminal", "true");
    await Expect(
      Page.Locator(".message.assistant .assistant-answer").Last
    ).Not.ToContainTextAsync("Codex streamed");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessAutoExecutesRequestedDeletionWithoutDuplicateApproval()
  {
    var target = Path.Combine(
      _environment.WorkspaceDirectory,
      "codex-delete.txt"
    );
    var unrelated = Path.Combine(
      _environment.WorkspaceDirectory,
      "codex-delete-unrelated.txt"
    );
    await File.WriteAllTextAsync(target, "delete me");
    await File.WriteAllTextAsync(unrelated, "preserve me");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await SendMessageAsync("delete codex file");
    await Expect(Page.Locator(".action-approval")).ToHaveCountAsync(0);
    Assert.IsFalse(File.Exists(target));
    Assert.AreEqual("preserve me", await File.ReadAllTextAsync(unrelated));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessAskWaitsBeforeRequestedDeletion()
  {
    var target = Path.Combine(_environment.WorkspaceDirectory, "codex-delete.txt");
    await File.WriteAllTextAsync(target, "delete me");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("ask");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await StartMessageAsync("delete codex file");
    var approval = Page.Locator(".action-approval").Last;
    await Expect(approval).ToBeVisibleAsync();
    Assert.IsTrue(File.Exists(target));
    await approval.GetByRole(AriaRole.Button, new() { Name = "Aprovar" }).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    Assert.IsFalse(File.Exists(target));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessUsesOneEditableHostApprovalForBatchDeletion()
  {
    var first = Path.Combine(_environment.WorkspaceDirectory, "batch-delete-a.txt");
    var second = Path.Combine(_environment.WorkspaceDirectory, "batch-delete-b.txt");
    await File.WriteAllTextAsync(first, "primeiro");
    await File.WriteAllTextAsync(second, "segundo");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SetExecuteModeAsync("ask");
    await Page.Locator("#harness-selector").SelectOptionAsync("codex");

    await StartMessageAsync("delete host batch files");
    var approval = Page.Locator(".action-approval").Last;
    await Expect(approval).ToBeVisibleAsync();
    await Expect(approval).ToContainTextAsync("delete_paths");
    var editor = approval.GetByRole(
      AriaRole.Textbox,
      new() { Name = "Editar comando de delete_paths" }
    );
    await Expect(editor).ToHaveValueAsync("batch-delete-a.txt\nbatch-delete-b.txt");
    Assert.IsTrue(File.Exists(first));
    Assert.IsTrue(File.Exists(second));

    await approval.GetByRole(AriaRole.Button, new() { Name = "Aprovar" }).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last)
      .ToHaveAttributeAsync("data-terminal", "true");
    Assert.IsFalse(File.Exists(first));
    Assert.IsFalse(File.Exists(second));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingCodexFailsClearlyWhileNativeRemainsAvailable()
  {
    var missing = Path.Combine(
      _environment.DataDirectory,
      "missing-codex.exe"
    );
    await _environment.SetCodexExecutableAndRestartAsync(missing);

    try
    {
      await Page.GotoAsync("/");
      await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
      await SetExecuteModeAsync("auto");
      var selector = Page.Locator("#harness-selector");
      var unavailable = selector.Locator("option[value=\"codex\"]");
      Assert.IsTrue(
        await unavailable.EvaluateAsync<bool>("option => option.disabled")
      );
      await Expect(unavailable).ToContainTextAsync("Unavailable");
      await Expect(unavailable).ToHaveAttributeAsync(
        "title",
        new Regex("Codex executable was not found")
      );
      await Expect(selector).ToHaveValueAsync("native");

      var failed = await ExecuteCodexStreamAsync(
        "create codex file",
        "browser-missing-codex"
      );
      var terminal = failed.Single(item => IsTerminalStreamEvent(item));
      Assert.AreEqual("error", terminal["type"]!.GetValue<string>());
      Assert.AreEqual(
        "codex-executable-not-found",
        terminal["error"]!["code"]!.GetValue<string>()
      );

      await SendMessageAsync("execute create file");
      Assert.IsTrue(
        File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt"))
      );
    }
    finally
    {
      await _environment.SetCodexExecutableAndRestartAsync(
        _environment.FakeCodexExecutablePath
      );
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessChildFailureProducesExactlyOneTerminalEvent()
  {
    var events = await ExecuteCodexStreamAsync(
      "crash codex child",
      "browser-codex-child-crash"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var error = events.Single(item => item["type"]!.GetValue<string>() == "error");
    Assert.AreEqual(
      "codex-app-server-exited",
      error["error"]!["code"]!.GetValue<string>()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessMalformedEventFailsOnceAndUnexpectedPayloadIsPreserved()
  {
    var malformed = await ExecuteCodexStreamAsync(
      "malformed codex event",
      "browser-codex-malformed"
    );
    Assert.HasCount(1, malformed.Where(IsTerminalStreamEvent));
    Assert.AreEqual(
      "codex-protocol-json",
      malformed.Single(item => item["type"]!.GetValue<string>() == "error")
        ["error"]!["code"]!.GetValue<string>()
    );

    var unexpected = await ExecuteCodexStreamAsync(
      "unexpected codex event",
      "browser-codex-unexpected"
    );
    Assert.HasCount(1, unexpected.Where(IsTerminalStreamEvent));
    Assert.IsGreaterThanOrEqualTo(
      1,
      unexpected.Count(
        item => item["type"]!.GetValue<string>()
          == "harness.codex-native-event-preserved"
      ),
      "The future Codex event must be preserved even when other native diagnostics are present."
    );
    Assert.HasCount(
      1,
      unexpected.Where(
        item => item["type"]!.GetValue<string>() == "response.completed"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexRecoveredDiagnosticAndWarningRemainNonTerminal()
  {
    var events = await ExecuteCodexStreamAsync(
      "recovered codex diagnostic",
      "browser-codex-recovered-diagnostic"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(
        item => item["type"]!.GetValue<string>() == "harness.codex-warning"
      )
    );
    Assert.HasCount(
      1,
      events.Where(
        item => item["type"]!.GetValue<string>() == "harness.codex-error-recovered"
      )
    );
    Assert.HasCount(
      1,
      events.Where(
        item => item["type"]!.GetValue<string>() == "response.completed"
      )
    );
    Assert.IsEmpty(events.Where(item => item["type"]!.GetValue<string>() == "error"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexPreservesAbsoluteNestedWindowsWorkspacePathWithSpaces()
  {
    var workspace = _environment.CreateWorkspaceDirectory(
      Path.Combine("Codex workspace with spaces", "nested project")
    );
    using var created = await _environment.HttpClient.PostAsJsonAsync(
      "api/workspaces",
      new
      {
        name = "Codex path test",
        path = workspace
      }
    );
    created.EnsureSuccessStatusCode();
    using var createdDocument = JsonDocument.Parse(
      await created.Content.ReadAsStringAsync()
    );
    var workspaceId = createdDocument.RootElement.GetProperty("id").GetString()!;
    using var activated = await _environment.HttpClient.PostAsync(
      $"api/workspaces/{workspaceId}/activate",
      null
    );
    activated.EnsureSuccessStatusCode();

    var events = await ExecuteCodexStreamAsync(
      "create codex file",
      "browser-codex-path-with-spaces"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    Assert.HasCount(
      1,
      events.Where(item => item["type"]!.GetValue<string>() == "response.completed")
    );
    Assert.IsTrue(Path.IsPathFullyQualified(workspace));
    Assert.IsTrue(File.Exists(Path.Combine(workspace, "codex-created.txt")));
    using var threadRequest = JsonDocument.Parse(
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.DataDirectory,
          "codex-runtime",
          "fake-app-server-thread-request.json"
        )
      )
    );
    Assert.AreEqual(
      workspace,
      threadRequest.RootElement.GetProperty("cwd").GetString()
    );
    Assert.AreEqual(
      "alpha:latest",
      threadRequest.RootElement.GetProperty("model").GetString()
    );
    Assert.AreEqual(
      "ollama",
      threadRequest.RootElement.GetProperty("provider").GetString()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CodexHarnessRejectsUnsupportedProviderWithoutFallback()
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = "do not run this cloud request",
        model = "groq::openai/gpt-oss-120b",
        history = Array.Empty<object>(),
        interactionMode = "execute",
        harness = "codex",
        approvalPolicy = "auto",
        browserSessionId = "browser-codex-cloud-rejected"
      }
    );
    response.EnsureSuccessStatusCode();
    var stream = await response.Content.ReadAsStringAsync();
    StringAssert.Contains(stream, "codex-provider-unsupported");
    StringAssert.Contains(stream, "supports Ollama Local models only");
    Assert.DoesNotContain("harness.codex-selected", stream);
    Assert.DoesNotContain("cloud.local-fallback", stream);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SavesRoutingSettingsAndChangesResidentModel()
  {
    await Page.GotoAsync(
      "/"
    );
    await OpenSettingsAsync();
    await Page.Locator(
      "[data-settings-target=\"models-routing\"]"
    ).ClickAsync();
    await Page.Locator(
      "#router-model"
    ).SelectOptionAsync(
      "beta:code"
    );
    await Page.Locator(
      "#action-model"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await Page.Locator(
      "[data-intention=\"documentation\"] .intention-prompt"
    ).FillAsync(
      "Persisted documentation prompt."
    );
    await Page.Locator(
      "[data-settings-target=\"harnesses\"]"
    ).ClickAsync();
    await Page.Locator(
      "#provider-context-tokens"
    ).FillAsync(
      "49152"
    );
    await Page.Locator(
      "#generation-timeout-seconds"
    ).FillAsync(
      "240"
    );
    await Page.Locator(
      "[data-settings-target=\"execution\"]"
    ).ClickAsync();
    await Page.Locator(
      "#max-tool-output-tokens"
    ).FillAsync(
      "1536"
    );
    await Page.Locator(
      "#save-settings"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#save-status"
      )
    ).ToHaveTextAsync("Salvo");
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToBeVisibleAsync();
    await Page.Locator("#cancel-settings").ClickAsync();
    await Expect(Page.Locator("#settings-dialog")).ToBeHiddenAsync();

    using var savedDocument = JsonDocument.Parse(
      await File.ReadAllTextAsync(
        _environment.SettingsPath
      )
    );
    Assert.AreEqual(
      "beta:code",
      savedDocument.RootElement.GetProperty(
        "routerModel"
      ).GetString()
    );
    Assert.AreEqual(
      "command-r:latest",
      savedDocument.RootElement.GetProperty(
        "actionModel"
      ).GetString()
    );
    Assert.AreEqual(
      "Persisted documentation prompt.",
      savedDocument.RootElement
        .GetProperty(
          "intentions"
        )
        .GetProperty(
          "documentation"
        )
        .GetProperty(
          "systemPrompt"
        )
        .GetString()
    );
    Assert.AreEqual(
      49_152,
      savedDocument.RootElement
        .GetProperty(
          "context"
        )
        .GetProperty(
          "providerContextTokens"
        )
        .GetInt32()
    );
    Assert.AreEqual(
      240,
      savedDocument.RootElement
        .GetProperty(
          "runtime"
        )
        .GetProperty(
          "generationTimeoutSeconds"
        )
        .GetInt32()
    );
    Assert.AreEqual(
      1_536,
      savedDocument.RootElement
        .GetProperty(
          "execution"
        )
        .GetProperty(
          "maxToolOutputTokens"
        )
        .GetInt32()
    );
    Assert.AreEqual(
      10,
      savedDocument.RootElement
        .GetProperty(
          "execution"
        )
        .GetProperty(
          "maxRecoveryAttemptsPerTurn"
        )
        .GetInt32()
    );

    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && request.KeepAlive == 0
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "command-r:latest"
          && request.KeepAlive == -1
          && request.ContextTokens == 8_192
          && request.Messages.Count == 0
      )
    );
    CollectionAssert.Contains(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "command-r:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SettingsUseViewportNavigationDirtyProtectionAndResponsiveFocus()
  {
    await Page.SetViewportSizeAsync(
      1280,
      800
    );
    await Page.GotoAsync(
      "/"
    );
    await OpenSettingsAsync();
    var bounds = await Page.Locator(
      "#settings-dialog"
    ).BoundingBoxAsync();
    Assert.IsNotNull(
      bounds
    );
    Assert.IsGreaterThan(
      0.93 * 1280,
      bounds.Width
    );
    Assert.IsLessThan(
      0.97 * 1280,
      bounds.Width
    );
    Assert.IsGreaterThan(
      0.93 * 800,
      bounds.Height
    );
    Assert.IsLessThanOrEqualTo(
      0.96 * 800,
      bounds.Height
    );
    await Expect(
      Page.Locator(
        "#settings-navigation"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".settings-responsive-navigation"
      )
    ).ToBeHiddenAsync();
    Assert.IsGreaterThanOrEqualTo(
      8,
      await Page.Locator(
        "#settings-dialog .information-button"
      ).CountAsync()
    );
    await Expect(
      Page.Locator(
        "#runtime-shared-model-warnings .runtime-shared-warning"
      )
    ).Not.ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "#settings-dialog .settings-note"
      )
    ).ToHaveCountAsync(
      0
    );
    var navigationTop = await Page.Locator(
      "#settings-navigation"
    ).EvaluateAsync<double>(
      "element => element.getBoundingClientRect().top"
    );
    var settingsFooter = Page.Locator(
      "#settings-dialog .dialog-footer"
    );
    var settingsFooterTop = await settingsFooter.EvaluateAsync<double>(
      "element => element.getBoundingClientRect().top"
    );
    await Page.Locator(
      "#settings-content"
    ).EvaluateAsync(
      "element => element.scrollTop = element.scrollHeight"
    );
    Assert.AreEqual(
      navigationTop,
      await Page.Locator(
        "#settings-navigation"
      ).EvaluateAsync<double>(
        "element => element.getBoundingClientRect().top"
      ),
      1
    );
    Assert.AreEqual(
      settingsFooterTop,
      await settingsFooter.EvaluateAsync<double>(
        "element => element.getBoundingClientRect().top"
      ),
      1
    );
    Assert.AreEqual(
      await Page.Locator(
        "#settings-dialog"
      ).EvaluateAsync<double>(
        "element => element.getBoundingClientRect().bottom"
      ),
      await settingsFooter.EvaluateAsync<double>(
        "element => element.getBoundingClientRect().bottom"
      ),
      1
    );
    await Page.Locator(
      "[data-settings-target=\"workspaces\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-settings-target=\"workspaces\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-current",
      "page"
    );
    await Expect(
      Page.Locator(
        "#settings-git-summary"
      )
    ).ToContainTextAsync(
      "not initialized"
    );

    await Page.Locator(
      "[data-settings-target=\"harnesses\"]"
    ).ClickAsync();
    await Page.Locator(
      "#provider-context-tokens"
    ).FillAsync(
      "1024"
    );
    await Expect(
      Page.Locator(
        "#settings-dirty"
      )
    ).ToHaveTextAsync(
      "Unsaved changes"
    );
    await Expect(
      Page.Locator(
        "#save-settings"
      )
    ).ToBeEnabledAsync();
    await Page.Locator(
      "#save-settings"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#toast-region .app-toast[data-tone=\"error\"]"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        "#default-context-tokens"
      )
    ).ToHaveAttributeAsync(
      "aria-invalid",
      "true"
    );
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToHaveAttributeAsync(
      "data-section",
      "harnesses"
    );

    await Page.Keyboard.PressAsync(
      "Escape"
    );
    await Page.Locator("#app-modal-cancel").ClickAsync();
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToBeVisibleAsync();

    await Page.Keyboard.PressAsync(
      "Escape"
    );
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToBeHiddenAsync();
    await Expect(
      Page.Locator(
        "#open-settings"
      )
    ).ToBeFocusedAsync();

    await Page.SetViewportSizeAsync(
      700,
      720
    );
    await OpenSettingsAsync();
    await Expect(
      Page.Locator(
        "#settings-navigation"
      )
    ).ToBeHiddenAsync();
    await Expect(
      Page.Locator(
        ".settings-responsive-navigation"
      )
    ).ToBeVisibleAsync();
    Assert.IsTrue(
      await Page.EvaluateAsync<bool>(
        "document.documentElement.scrollWidth <= document.documentElement.clientWidth"
      )
    );
    await Page.Locator(
      "#settings-section-select"
    ).SelectOptionAsync(
      "workspaces"
    );
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToHaveAttributeAsync(
      "data-section",
      "workspaces"
    );
    Assert.IsTrue(
      await Page.EvaluateAsync<bool>(
        "document.querySelector('#settings-dialog').contains(document.activeElement)"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SettingsSectionsAndAdvancedSubsectionsRemainExclusiveAndScrollIndependent()
  {
    await Page.GotoAsync("/");
    await OpenSettingsAsync();

    var sectionTargets = new[]
    {
      ("general", new[] {"settings-general", "settings-ollama"}),
      ("models-routing", new[] {"settings-models", "settings-coordinator"}),
      ("providers", new[] {"settings-cloud-providers"}),
      ("harnesses", new[] {"settings-runtime"}),
      ("execution", new[] {"settings-execution"}),
      ("workspaces", new[] {"settings-workspaces", "settings-git", "settings-validation"}),
      ("advanced", new[] {"settings-advanced"})
    };

    async Task<string[]> GetActiveSectionIds()
    {
      return await Page.EvaluateAsync<string[]>(
        "() => Array.from(document.querySelectorAll('.settings-section.active')).map((section) => section.id)"
      );
    }

    async Task ClickSectionWithoutPageJumpAsync(string target)
    {
      await Page.EvaluateAsync(
        "() => window.scrollTo(0, 180)"
      );
      var scrollBefore = await Page.EvaluateAsync<double>(
        "() => window.scrollY"
      );
      await Page.Locator(
        $"[data-settings-target=\"{target}\"]"
      ).ClickAsync();
      var scrollAfter = await Page.EvaluateAsync<double>(
        "() => window.scrollY"
      );
      Assert.AreEqual(
        scrollBefore,
        scrollAfter
      );
    }

    foreach (var (target, expectedSections) in sectionTargets)
    {
      await ClickSectionWithoutPageJumpAsync(target);
      await Expect(
        Page.Locator(
          "#settings-dialog"
        )
      ).ToHaveAttributeAsync(
        "data-section",
        target
      );

      var activeSections = await GetActiveSectionIds();
      Array.Sort(activeSections);
      Array.Sort(expectedSections);
      CollectionAssert.AreEqual(
        expectedSections,
        activeSections
      );
      await Expect(
        Page.Locator(
          $"[data-settings-target=\"{target}\"]"
        )
      ).ToHaveAttributeAsync(
        "aria-current",
        "page"
      );
    }

    await Page.Locator(
      "[data-settings-target=\"advanced\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-settings-subtarget=\"portable-yaml\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-current",
      "page"
    );
    Assert.IsTrue(
      await Page.EvaluateAsync<bool>(
        "() => document.querySelector('#settings-advanced-yaml').classList.contains('active')"
      )
    );
    await Expect(Page.Locator("#settings-advanced-backup")).ToBeHiddenAsync();

    await Page.Locator(
      "[data-settings-subtarget=\"backup\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-settings-subtarget=\"backup\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-current",
      "page"
    );
    await Expect(
      Page.Locator(
        "[data-settings-subtarget=\"portable-yaml\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-current",
      "false"
    );
    Assert.IsTrue(
      await Page.EvaluateAsync<bool>(
        "() => document.querySelector('#settings-advanced-backup').classList.contains('active')"
      )
    );
    Assert.IsFalse(
      await Page.EvaluateAsync<bool>(
        "() => document.querySelector('#settings-advanced-yaml').classList.contains('active')"
      )
    );

    await Page.Locator(
      "[data-settings-target=\"providers\"]"
    ).ClickAsync();
    await Expect(Page.Locator("#settings-advanced-backup")).ToBeHiddenAsync();
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToHaveAttributeAsync(
      "data-section",
      "providers"
    );
    await Expect(
      Page.Locator(
        "[data-settings-target=\"providers\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-current",
      "page"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PortableYamlRoundTripsAndRejectsUnsupportedModelRolesAtomically()
  {
    using var exportResponse = await _environment.HttpClient.GetAsync(
      "api/settings/yaml"
    );
    exportResponse.EnsureSuccessStatusCode();
    Assert.AreEqual(
      "application/yaml",
      exportResponse.Content.Headers.ContentType?.MediaType
    );
    var exported = await exportResponse.Content.ReadAsStringAsync();
    StringAssert.Contains(
      exported,
      "schema_version: 1"
    );
    StringAssert.Contains(
      exported,
      "software-development:"
    );
    StringAssert.Contains(
      exported,
      "fallback:"
    );
    StringAssert.Contains(
      exported,
      "usage:"
    );
    StringAssert.Contains(
      exported,
      "retention_days: 90"
    );
    Assert.IsFalse(
      exported.Contains(
        "usage_history",
        StringComparison.Ordinal
      )
    );
    Assert.IsFalse(
      exported.Contains(
        "data/usage",
        StringComparison.Ordinal
      )
    );
    Assert.IsFalse(
      exported.Contains(
        _environment.WorkspaceDirectory,
        StringComparison.Ordinal
      )
    );
    Assert.IsFalse(
      exported.Contains(
        "validation_profile",
        StringComparison.Ordinal
      )
    );

    const string yaml = """
      schema_version: 1
      models:
        router:
          primary: beta:code
        coordinator:
          primary: docs:latest
        software-development:
          primary: beta:code
          fallback: docs:latest
      runtime:
        generation_timeout_seconds: 222
      ollama_runtime:
        memory:
          devices:
            device_1:
              id: gpu-test
              target_maximum_usage_percent: 88
              minimum_free_vram_bytes: 1073741824
      """;
    using var importResponse = await _environment.HttpClient.PutAsJsonAsync(
      "api/settings/yaml",
      new
      {
        yaml
      },
      TestJson.Options
    );
    importResponse.EnsureSuccessStatusCode();
    var imported = await importResponse.Content.ReadFromJsonAsync<JsonElement>(
      TestJson.Options
    );
    Assert.AreEqual(
      "beta:code",
      imported.GetProperty(
        "routerModel"
      ).GetString()
    );
    Assert.AreEqual(
      "docs:latest",
      imported.GetProperty(
        "coordinatorModel"
      ).GetString()
    );
    Assert.AreEqual(
      "docs:latest",
      imported.GetProperty(
          "intentions"
        )
        .GetProperty(
          "software-development"
        )
        .GetProperty(
          "fallbackModel"
        )
        .GetString()
    );
    Assert.AreEqual(
      222,
      imported.GetProperty(
          "runtime"
        )
        .GetProperty(
          "generationTimeoutSeconds"
        )
        .GetInt32()
    );
    var importedGpuPolicy = imported.GetProperty(
        "ollamaRuntime"
      )
      .GetProperty(
        "memory"
      )
      .GetProperty(
        "devices"
      )
      .GetProperty(
        "gpu-test"
      );
    Assert.AreEqual(
      88,
      importedGpuPolicy.GetProperty(
        "targetMaximumUsagePercent"
      ).GetInt32()
    );
    Assert.AreEqual(
      1_073_741_824,
      importedGpuPolicy.GetProperty(
        "minimumFreeVramBytes"
      ).GetInt64()
    );
    Assert.AreEqual(
      _environment.WorkspaceDirectory,
      imported.GetProperty(
        "trustedWorkspacePath"
      ).GetString()
    );

    using var secondExportResponse = await _environment.HttpClient.GetAsync(
      "api/settings/yaml"
    );
    secondExportResponse.EnsureSuccessStatusCode();
    var secondExport = await secondExportResponse.Content.ReadAsStringAsync();
    using var roundTripResponse = await _environment.HttpClient.PutAsJsonAsync(
      "api/settings/yaml",
      new
      {
        yaml = secondExport
      },
      TestJson.Options
    );
    Assert.AreEqual(
      HttpStatusCode.OK,
      roundTripResponse.StatusCode,
      await roundTripResponse.Content.ReadAsStringAsync()
    );
    var beforeInvalid = await File.ReadAllTextAsync(
      _environment.SettingsPath
    );

    const string unsupportedRoles = """
      models:
        software-development:
          primary: beta:code
          escalation: alpha:latest
          code_documentation: docs:latest
        review-and-testing:
          reviewer: beta:code
      """;
    using var invalidResponse = await _environment.HttpClient.PutAsJsonAsync(
      "api/settings/yaml",
      new
      {
        yaml = unsupportedRoles
      },
      TestJson.Options
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      invalidResponse.StatusCode
    );
    var invalid = await invalidResponse.Content.ReadFromJsonAsync<JsonElement>(
      TestJson.Options
    );
    var errors = invalid.GetProperty(
      "errors"
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "models.software-development.escalation",
        out _
      )
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "models.software-development.code_documentation",
        out _
      )
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "models.review-and-testing.reviewer",
        out _
      )
    );
    Assert.AreEqual(
      beforeInvalid,
      await File.ReadAllTextAsync(
        _environment.SettingsPath
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PortableYamlCanBeDownloadedAndAppliedFromAdvancedSettings()
  {
    await Page.GotoAsync(
      "/"
    );
    await OpenSettingsAsync();
    await Page.Locator(
      "[data-settings-target=\"advanced\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#settings-yaml"
      )
    ).ToHaveValueAsync(
      new Regex(
        "schema_version: 1"
      )
    );
    await Expect(
      Page.Locator(
        "#settings-dirty"
      )
    ).ToHaveTextAsync(
      "Sem alterações"
    );

    const string yaml = """
      schema_version: 1
      models:
        router:
          primary: beta:code
        coordinator:
          primary: docs:latest
        software-development:
          primary: beta:code
          fallback: docs:latest
      """;
    await Page.Locator(
      "#settings-yaml"
    ).FillAsync(
      yaml
    );
    await Expect(
      Page.Locator(
        "#settings-dirty"
      )
    ).ToHaveTextAsync(
      "Sem alterações"
    );
    var download = await Page.RunAndWaitForDownloadAsync(
      () => Page.Locator(
        "#download-settings-yaml"
      ).ClickAsync()
    );
    Assert.AreEqual(
      "agentic-router.yaml",
      download.SuggestedFilename
    );

    await Page.Locator(
      "#import-settings-yaml"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#settings-yaml-status"
      )
    ).ToHaveTextAsync(
      "Configuração YAML importada e aplicada."
    );
    await Expect(
      Page.Locator(
        "#router-model"
      )
    ).ToHaveValueAsync(
      "beta:code"
    );
    await Expect(
      Page.Locator(
        "#coordinator-model"
      )
    ).ToHaveValueAsync(
      "docs:latest"
    );
    await Expect(
      Page.Locator(
        "[data-intention=\"software-development\"] .intention-fallback-model"
      )
    ).ToHaveValueAsync(
      "docs:latest"
    );
    await Expect(
      Page.Locator(
        "#settings-dirty"
      )
    ).ToHaveTextAsync(
      "Sem alterações"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RejectsMissingModelsAndPromptsWithoutCorruptingJson()
  {
    var before = await File.ReadAllTextAsync(
      _environment.SettingsPath
    );
    var intentions = _environment.BaselineSettings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );
    intentions["documentation"] = intentions["documentation"] with
    {
      SystemPrompt = " "
    };
    var invalid = _environment.BaselineSettings with
    {
      RouterModel = " ",
      ActionModel = " ",
      CoordinatorModel = " ",
      DefaultModel = string.Empty,
      Intentions = intentions
    };
    using var response = await _environment.PutSettingsAsync(
      invalid
    );

    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      response.StatusCode
    );
    var payload = await response.Content.ReadFromJsonAsync<JsonElement>(
      TestJson.Options
    );
    var errors = payload.GetProperty(
      "errors"
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "routerModel",
        out _
      )
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "actionModel",
        out _
      )
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "defaultModel",
        out _
      )
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "coordinatorModel",
        out _
      )
    );
    Assert.IsTrue(
      errors.TryGetProperty(
        "intentions.documentation.systemPrompt",
        out _
      )
    );
    Assert.AreEqual(
      before,
      await File.ReadAllTextAsync(
        _environment.SettingsPath
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoClassifiesThenUsesConfiguredTargetWithProgressiveActivity()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "Write documentation for this API"
    );

    var activity = Page.Locator(
      ".message.assistant .activity"
    );
    await Expect(
      activity
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      Page.Locator(
        ".model-selection-note"
      )
    ).ToHaveTextAsync(
      "Modelo docs:latest roteado pelo agente."
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.classified\"]"
      )
    ).ToContainTextAsync(
      "documentation"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.auto-enabled\"]"
      )
    ).ToContainTextAsync(
      "Auto routing enabled"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.model-resolved\"]"
      )
    ).ToContainTextAsync(
      "router:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.confidence\"]"
      )
    ).ToContainTextAsync(
      "91%"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.reason\"]"
      )
    ).ToContainTextAsync(
      "Latest user request classification"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"target.configuration\"]"
      )
    ).ToContainTextAsync(
      "documentation"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"target.model-resolved\"]"
      )
    ).ToContainTextAsync(
      "docs:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"response.first-chunk\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello from docs:latest"
    );
    await Expect(
      activity
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      activity.Locator(
        ":scope > summary"
      )
    ).ToContainTextAsync(
      "Concluído"
    );

    var requests = _environment.FakeOllama.Requests;
    Assert.HasCount(
      2,
      requests
    );
    Assert.AreEqual(
      "router:latest",
      requests[0].Model
    );
    Assert.IsFalse(
      requests[0].Stream
    );
    Assert.AreEqual(
      "docs:latest",
      requests[1].Model
    );
    Assert.IsTrue(
      requests[1].Stream
    );
    Assert.AreEqual(
      "system",
      requests[1].Messages[0].Role
    );
    Assert.HasCount(
      1,
      requests[1].Messages.Where(
        message => string.Equals(
          message.Role,
          "system",
          StringComparison.Ordinal
        )
      ).ToArray()
    );
    Assert.AreEqual(
      "The latest user instruction has priority over earlier conversational patterns. "
        + "Do not continue a previous task when the user explicitly changes the objective. "
        + "Do not claim that you executed, tested, opened, accessed, or verified something "
        + "unless the application actually performed that action.\n\n"
        + "You write documentation.",
      requests[1].Messages[0].Content
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OllamaThinkingStreamsSeparatelyFromAssistantAnswer()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "show thinking"
    );

    var reasoning = Page.Locator(
      ".message.assistant .assistant-reasoning"
    );
    await Expect(
      reasoning
    ).ToHaveCountAsync(3);
    await Expect(
      reasoning.First.Locator(
        ".assistant-reasoning-body"
      )
    ).ToContainTextAsync(
      "inspect the request"
    );
    await Expect(
      reasoning.Locator(
        ".assistant-reasoning-body",
        new() { HasText = "The response should remain concise and grounded." }
      )
    ).ToHaveCountAsync(2);
    await Expect(
      reasoning.Last
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    var timelineKinds = await Page.Locator(
      ".message.assistant .assistant-work > [data-timeline-kind]"
    ).EvaluateAllAsync<string[]>(
      "nodes => nodes.map(node => node.dataset.timelineKind)"
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "thinking",
        "response",
        "thinking",
        "response",
        "thinking",
        "response"
      },
      timelineKinds
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).Not.ToContainTextAsync(
      "inspect the request"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OllamaThinkingSurvivesNativeToolCallNormalization()
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
      "Create hello.txt containing \"hello\"."
    );

    await Expect(
      Page.Locator(
        ".assistant-reasoning-body",
        new()
        {
          HasText = "Host tool create_file"
        }
      ).First
    ).ToBeAttachedAsync();
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).Not.ToContainTextAsync(
      "Host tool create_file"
    );
    var nativeToolRequest = _environment.FakeOllama.Requests.Last(
      request => request.Model == "qwen3-coder:30b"
        && request.HasTools
    );
    Assert.AreEqual(
      "system",
      nativeToolRequest.Messages[0].Role
    );
    Assert.AreEqual(
      1,
      nativeToolRequest.Messages.Count(
        message => message.Role == "system"
      ),
      "Native planning must consolidate Host instructions into one leading system message."
    );
    StringAssert.Contains(
      nativeToolRequest.Messages[0].Content,
      "SPECIALIST_TOOL_LOOP_V2"
    );
    StringAssert.Contains(
      nativeToolRequest.Messages[0].Content,
      "APPLICATION_OWNED_PROJECT_CONTEXT"
    );
    if (await Page.Locator("#send-button-label").TextContentAsync() == "Cancelar")
    {
      await Page.Locator(
        "#send-button"
      ).ClickAsync();
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExecuteThinkingStreamsInChronologicalBlocksBetweenActions()
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
      "chronological thinking stream create two files"
    );

    var assistant = Page.Locator(
      ".message.assistant"
    ).Last;
    var firstThinking = assistant.Locator(
      ".assistant-reasoning"
    ).First;
    await Expect(
      firstThinking.Locator(
        ".assistant-reasoning-body"
      )
    ).ToContainTextAsync(
      "I will use the Host"
    );
    await Expect(
      firstThinking
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    Assert.AreEqual(
      0,
      await assistant.Locator(
        ".work-action"
      ).CountAsync(),
      "Thinking must be visible before the tool-call response is complete."
    );

    await Expect(
      assistant.Locator(
        ".work-action"
      )
    ).ToHaveCountAsync(
      2,
      new()
      {
        Timeout = 20_000
      }
    );
    var timelineItems = assistant.Locator(
      ".assistant-work > [data-timeline-kind]"
    );
    await Expect(
      timelineItems
    ).ToHaveCountAsync(
      6
    );
    var timelineKinds = await timelineItems.EvaluateAllAsync<string[]>(
      "nodes => nodes.map(node => node.dataset.timelineKind)"
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "thinking",
        "toolset",
        "thinking",
        "action",
        "thinking",
        "action"
      },
      timelineKinds
    );
    await Expect(
      assistant.Locator(
        ".assistant-reasoning"
      )
    ).ToHaveCountAsync(
      3
    );
    await Expect(
      assistant.Locator(
        ".assistant-reasoning[data-delta-count=\"2\"]"
      )
    ).ToHaveCountAsync(
      3
    );
    await Expect(
      assistant.Locator(
        ".assistant-reasoning"
      ).Last
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      assistant.Locator(
        ".work-action-file"
      ).Nth(0)
    ).ToHaveTextAsync(
      "chrono-one.txt"
    );
    await Expect(
      assistant.Locator(
        ".work-action-file"
      ).Nth(1)
    ).ToHaveTextAsync(
      "chrono-two.txt"
    );
    if (await Page.Locator("#send-button-label").TextContentAsync() == "Cancelar")
    {
      await Page.Locator(
        "#send-button"
      ).ClickAsync();
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExplicitModelBypassesRouter()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "beta:code"
    );
    await SendMessageAsync(
      "Write documentation explicitly"
    );

    var requests = _environment.FakeOllama.Requests;
    Assert.HasCount(
      1,
      requests
    );
    Assert.AreEqual(
      "beta:code",
      requests[0].Model
    );
    Assert.IsTrue(
      requests[0].Stream
    );
    Assert.AreEqual(
      "system",
      requests[0].Messages[0].Role
    );
    Assert.AreEqual(
      1,
      requests[0].Messages.Count(
        message => string.Equals(
          message.Role,
          "system",
          StringComparison.Ordinal
        )
      ),
      "Explicit local Chat must send one leading system message."
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"model.explicit-selected\"]"
      )
    ).ToContainTextAsync(
      "Manual model override"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.bypassed\"]"
      )
    ).ToContainTextAsync(
      "Router bypassed"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.classified\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        ".model-selection-note"
      )
    ).ToHaveTextAsync(
      "Modelo beta:code selecionado pelo usuário."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HistoryDisabledNewConversationRequiresExplicitDiscardAndPreservesSettings()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "beta:code"
    );
    await SendMessageAsync(
      "Message in the old conversation"
    );
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Nova conversa",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#new-conversation-dialog"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      "#new-conversation-discard"
    ).ClickAsync();

    await Expect(
      Page.Locator(
        ".message"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "#empty-state"
      )
    ).ToBeVisibleAsync();
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
    await OpenSettingsAsync();
    await Expect(
      Page.Locator(
        "#ollama-url"
      )
    ).ToHaveValueAsync(
      _environment.BaselineSettings.OllamaUrl
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnsavedConversationCanBeCancelledOrSavedByExplicitlyEnablingHistory()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Conversation saved only after consent."
    );
    await Page.Locator(
      "#new-conversation"
    ).ClickAsync();
    await Page.Locator(
      "#new-conversation-cancel"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".message.user"
      )
    ).ToContainTextAsync(
      "Conversation saved only after consent."
    );
    await Expect(
      Page.Locator(
        "#new-conversation-dialog"
      )
    ).ToBeHiddenAsync();

    await Page.Locator(
      "#new-conversation"
    ).ClickAsync();
    await Page.Locator(
      "#new-conversation-enable-history"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".message"
      )
    ).ToHaveCountAsync(
      0
    );
    using var sessions = await _environment.HttpClient.GetAsync(
      "api/sessions"
    );
    sessions.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await sessions.Content.ReadAsStringAsync()
    );
    Assert.IsTrue(
      document.RootElement.GetProperty(
        "usage"
      ).GetProperty(
        "enabled"
      ).GetBoolean()
    );
    Assert.AreEqual(
      1,
      document.RootElement.GetProperty(
        "recent"
      ).GetArrayLength()
    );
    Assert.AreEqual(
      "Conversation saved only after consent.",
      document.RootElement.GetProperty(
        "recent"
      )[0].GetProperty(
        "title"
      ).GetString()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ActiveTurnBlocksNewConversationUntilCancelled()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "cancel stream"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello"
    );
    using (
      var measurement = await _environment.HttpClient.PostAsJsonAsync(
        "api/runtime/profiles/measure",
        new
        {
          model = "alpha:latest",
          role = "modelTest",
          contextCandidates = new[]
          {
            4_096
          },
          permissionGranted = true,
          runMinimalRequest = false
        }
      )
    )
    {
      Assert.AreEqual(
        HttpStatusCode.Conflict,
        measurement.StatusCode
      );
      var blocked = await measurement.Content.ReadAsStringAsync();
      StringAssert.Contains(
        blocked,
        "reload-blocked-by-active-request"
      );
    }
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Nova conversa",
        Exact = true
      }
    ).ClickAsync();

    await Expect(
      Page.Locator(
        ".message.user"
      )
    ).ToContainTextAsync(
      "cancel stream"
    );
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Cancelar"
    );
    await Page.Locator(
      "#send-button"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Enviar"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HistoryEnabledNewConversationPersistsDistinctSessionsAndResumesSafely()
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
    using var invalidSession = await _environment.HttpClient.PutAsJsonAsync(
      "api/sessions/current",
      new
      {
        sessionId = "../escape",
        messages = Array.Empty<object>(),
        state = "completed",
        interactionMode = "chat",
        selectedModel = (string?)null
      }
    );
    Assert.AreEqual(
      HttpStatusCode.BadRequest,
      invalidSession.StatusCode
    );
    using var invalidDocument = JsonDocument.Parse(
      await invalidSession.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      "session-record-invalid",
      invalidDocument.RootElement.GetProperty(
        "code"
      ).GetString()
    );
    await Page.GotoAsync(
      "/"
    );

    await SendMessageAsync(
      "First durable conversation."
    );
    await Expect(
      Page.Locator(
        "#conversation-persistence"
      )
    ).ToContainTextAsync(
      "Saved locally"
    );
    using var firstSessions = await _environment.HttpClient.GetAsync(
      "api/sessions"
    );
    firstSessions.EnsureSuccessStatusCode();
    using var firstDocument = JsonDocument.Parse(
      await firstSessions.Content.ReadAsStringAsync()
    );
    var firstId = firstDocument.RootElement.GetProperty(
      "recent"
    )[0].GetProperty(
      "id"
    ).GetString()!;

    await Page.RouteAsync(
      "**/api/sessions/current",
      async route =>
      {
        if (string.Equals(
          route.Request.Method,
          "PUT",
          StringComparison.Ordinal
        ))
        {
          await route.FulfillAsync(
            new RouteFulfillOptions
            {
              Status = 500,
              ContentType = "application/json",
              Body = "{\"message\":\"A redundant save was attempted.\"}"
            }
          );
          return;
        }

        await route.ContinueAsync();
      }
    );

    await Page.Locator(
      "#new-conversation"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".message"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "#conversation-persistence"
      )
    ).Not.ToContainTextAsync(
      "Save failed"
    );
    await Page.UnrouteAllAsync();
    await SendMessageAsync(
      "Second durable conversation."
    );
    using var secondSessions = await _environment.HttpClient.GetAsync(
      "api/sessions"
    );
    secondSessions.EnsureSuccessStatusCode();
    using var secondDocument = JsonDocument.Parse(
      await secondSessions.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      2,
      secondDocument.RootElement.GetProperty(
        "recent"
      ).GetArrayLength()
    );
    var secondId = secondDocument.RootElement.GetProperty(
      "recent"
    )[0].GetProperty(
      "id"
    ).GetString()!;
    Assert.AreNotEqual(
      firstId,
      secondId
    );

    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Page.Locator(
      $"#recent-sessions [data-session-id=\"{firstId}\"]"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Retomar"
      }
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        ".message.user"
      ).First
    ).ToContainTextAsync(
      "First durable conversation."
    );
    await Expect(
      Page.Locator(
        $"#recent-sessions [data-session-id=\"{firstId}\"]"
      )
    ).ToHaveAttributeAsync(
      "aria-current",
      "true"
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
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SessionSaveFailureKeepsTheVisibleConversationAndExposesTrace()
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
    await SendMessageAsync(
      "Conversation that must remain visible."
    );
    var sessionsDirectory = Path.Combine(
      _environment.DataDirectory,
      "workspaces",
      activeId,
      "sessions"
    );
    var backupDirectory = $"{sessionsDirectory}-backup";
    Directory.Move(
      sessionsDirectory,
      backupDirectory
    );
    await File.WriteAllTextAsync(
      sessionsDirectory,
      "blocks session persistence"
    );
    await Page.EvaluateAsync(
      "() => setPersistenceStatus('Unsaved')"
    );

    try
    {
      await Page.Locator(
        "#new-conversation"
      ).ClickAsync();
      await Expect(
        Page.Locator(
          ".message.user"
        )
      ).ToContainTextAsync(
        "Conversation that must remain visible."
      );
      await Expect(
        Page.Locator(
          "#conversation-persistence"
        )
      ).ToContainTextAsync(
        "Save failed"
      );
      await Expect(
        Page.Locator(
          "#composer-status"
        )
      ).ToContainTextAsync(
        "Trace ID:"
      );
    }
    finally
    {
      File.Delete(
        sessionsDirectory
      );
      Directory.Move(
        backupDirectory,
        sessionsDirectory
      );
    }
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingRouterConfidenceIsReportedAsUnavailable()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "documentation with missing confidence"
    );

    var classification = Page.Locator(
      "[data-event-type=\"router.confidence\"]"
    );
    await Expect(
      classification
    ).ToContainTextAsync(
      "Confidence: unavailable"
    );
    await Expect(
      classification
    ).Not.ToContainTextAsync(
      "0%"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello from docs:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OutOfRangeRouterConfidenceFallsBackSafely()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "documentation with out of range confidence"
    );

    await Expect(
      Page.Locator(
        "[data-event-type=\"router.warning\"]"
      )
    ).ToContainTextAsync(
      "invalid-confidence"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello from alpha:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task InvalidRouterOutputFallsBackToGeneralChat()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "invalid router response"
    );

    await Expect(
      Page.Locator(
        "[data-event-type=\"router.warning\"]"
      )
    ).ToContainTextAsync(
      "invalid-json"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello from alpha:latest"
    );
    var requests = _environment.FakeOllama.Requests;
    Assert.HasCount(
      2,
      requests
    );
    Assert.AreEqual(
      "alpha:latest",
      requests[1].Model
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LatestRequestedActionOverridesEarlierRpgTheme()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Write an RPG story about a wandering mage"
    );
    await SendMessageAsync(
      "Implement an HTML and JavaScript game about that RPG character"
    );

    await Expect(
      Page.Locator(
        "[data-event-type=\"router.classified\"]"
      ).Last
    ).ToContainTextAsync(
      "software-development"
    );
    var secondRouter = _environment.FakeOllama.Requests
      .Where(
        request => !request.Stream
      )
      .Last();
    Assert.HasCount(
      2,
      secondRouter.Messages
    );
    StringAssert.Contains(
      secondRouter.Messages[1].Content,
      "Implement an HTML and JavaScript game"
    );
    Assert.IsFalse(
      secondRouter.Messages[1].Content.Contains(
        "wandering mage",
        StringComparison.Ordinal
      )
    );

    await SendMessageAsync(
      "Write a story about a secret code hidden in an ancient RPG temple"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.classified\"]"
      ).Last
    ).ToContainTextAsync(
      "rpg-storytelling"
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(request => request.HasTools)
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RouterValidationDistinguishesZeroNegativeAndUnsupportedValues()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "documentation with explicit zero confidence"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.confidence\"]"
      ).Last
    ).ToContainTextAsync(
      "0%"
    );

    await SendMessageAsync(
      "documentation with negative confidence"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.warning\"]"
      ).Last
    ).ToContainTextAsync(
      "invalid-confidence"
    );

    await SendMessageAsync(
      "documentation with unsupported intention"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"router.warning\"]"
      ).Last
    ).ToContainTextAsync(
      "unsupported-intention"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConversationContextIsSentOnlyAsVisibleUserAssistantHistory()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "First message"
    );
    await SendMessageAsync(
      "Second message"
    );

    var requests = _environment.FakeOllama.Requests;
    Assert.HasCount(
      4,
      requests
    );
    var secondRouter = requests[2];
    var secondTarget = requests[3];
    Assert.HasCount(
      2,
      secondRouter.Messages
    );
    StringAssert.Contains(
      secondRouter.Messages[1].Content,
      "Second message"
    );
    Assert.IsFalse(
      secondRouter.Messages.Any(
        message => message.Content.Contains(
          "First message",
          StringComparison.Ordinal
        ) || message.Content.Contains(
          "Hello from",
          StringComparison.Ordinal
        ) || message.Content.Contains(
          "Router model",
          StringComparison.Ordinal
        )
      )
    );
    Assert.HasCount(
      4,
      secondTarget.Messages
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "system",
        "user",
        "assistant",
        "user"
      },
      secondTarget.Messages.Select(
        message => message.Role
      ).ToArray()
    );
    Assert.IsFalse(
      secondTarget.Messages.Any(
        message => message.Content.Contains(
          "Router model resolved",
          StringComparison.Ordinal
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConversationContextTrimsOldestCompleteTurnsAndKeepsLatestMessage()
  {
    using var saveResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        Context = _environment.BaselineSettings.Context with
        {
          MaxConversationMessages = 2
        }
      }
    );
    saveResponse.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Oldest complete turn"
    );
    await SendMessageAsync(
      "Recent complete turn"
    );
    await SendMessageAsync(
      "Latest message must survive trimming"
    );

    var target = _environment.FakeOllama.Requests
      .Last(
        request => request.Stream
      );
    Assert.HasCount(
      4,
      target.Messages
    );
    Assert.IsFalse(
      target.Messages.Any(
        message => message.Content.Contains(
          "Oldest complete turn",
          StringComparison.Ordinal
        )
      )
    );
    Assert.IsTrue(
      target.Messages.Any(
        message => message.Content.Contains(
          "Recent complete turn",
          StringComparison.Ordinal
        )
      )
    );
    Assert.AreEqual(
      "Latest message must survive trimming",
      target.Messages.Last().Content
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"context.trimmed\"]"
      ).Last
    ).ToContainTextAsync(
      "2 older messages omitted"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ClickingComposerSurfaceFocusesMessageInput()
  {
    await Page.GotoAsync(
      "/"
    );
    var input = Page.Locator(
      "#message-input"
    );
    await input.BlurAsync();
    await Page.Locator(
      "#composer"
    ).ClickAsync(
      new()
      {
        Position = new Position
        {
          X = 12,
          Y = 12
        }
      }
    );

    await Expect(
      input
    ).ToBeFocusedAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task EditingUserMessageReplacesTurnAndTruncatesLaterContext()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "First message"
    );
    await SendMessageAsync(
      "Second message"
    );
    await Page.Locator(
      ".message.user"
    ).First.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Editar mensagem",
        Exact = true
      }
    ).ClickAsync();
    var input = Page.Locator(
      "#message-input"
    );

    await Expect(
      input
    ).ToHaveValueAsync(
      "First message"
    );
    await Expect(
      input
    ).ToBeFocusedAsync();
    await Expect(
      Page.Locator(
        "#composer-status"
      )
    ).ToContainTextAsync(
      "Editando mensagem"
    );
    await input.FillAsync(
      "First message edited"
    );
    var previousStreamingRequestCount = _environment.FakeOllama.Requests.Count(
      request => request.Stream
    );
    await input.PressAsync(
      "Enter"
    );
    await WaitUntilAsync(
      () => _environment.FakeOllama.Requests.Count(
        request => request.Stream
      ) > previousStreamingRequestCount,
      TimeSpan.FromSeconds(
        5
      )
    );
    await Expect(
      Page.Locator(
        ".message.assistant .activity"
      ).Last
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );

    await Expect(
      Page.Locator(
        ".message.user"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        ".message.user .message-content"
      )
    ).ToHaveTextAsync(
      "First message edited"
    );
    var editedTarget = _environment.FakeOllama.Requests
      .Where(
        request => request.Stream
      )
      .Last();
    Assert.HasCount(
      2,
      editedTarget.Messages
    );
    Assert.AreEqual(
      "First message edited",
      editedTarget.Messages.Last().Content
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task EditingMessageKeepsSubmitControlsAlignedAndCanBeCancelledVisibly()
  {
    await Page.SetViewportSizeAsync(
      1024,
      720
    );
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Message to edit"
    );
    var editButton = Page.Locator(
      ".message.user"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Editar mensagem",
        Exact = true
      }
    );
    await editButton.ClickAsync();
    var cancelEdit = Page.Locator(
      "#cancel-message-edit"
    );
    await Expect(
      cancelEdit
    ).ToBeVisibleAsync();
    var alignmentDifference = await Page.EvaluateAsync<double>(
      """
      () => {
        const controls = document.querySelector(".composer-submit-actions");
        const approval = document.querySelector("#approval-policy");
        return Math.abs(
          controls.getBoundingClientRect().bottom
            - approval.getBoundingClientRect().bottom
        );
      }
      """
    );
    Assert.IsLessThanOrEqualTo(
      1,
      alignmentDifference
    );
    await cancelEdit.ClickAsync();
    await Expect(
      cancelEdit
    ).ToBeHiddenAsync();
    await Expect(
      Page.Locator(
        "#message-input"
      )
    ).ToHaveValueAsync(
      string.Empty
    );
    await Expect(
      Page.Locator(
        ".message.user"
      )
    ).ToHaveCountAsync(
      1
    );

    await editButton.ClickAsync();
    await Page.Locator(
      "#message-input"
    ).PressAsync(
      "Escape"
    );
    await Expect(
      cancelEdit
    ).ToBeHiddenAsync();
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Enviar"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RendersMarkdownAndBlocksExecutableHtmlAndUnsafeUrls()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "markdown fixture for documentation"
    );
    var answer = Page.Locator(
      ".assistant-answer"
    );

    await Expect(
      answer.Locator(
        "h1"
      )
    ).ToHaveTextAsync(
      "Heading"
    );
    await Expect(
      answer.Locator(
        "ul li"
      )
    ).ToHaveCountAsync(
      2
    );
    await Expect(
      answer.Locator(
        "pre"
      )
    ).ToContainTextAsync(
      "Console.WriteLine"
    );
    await Expect(
      answer.Locator(
        "table tbody tr"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      answer.Locator(
        "script"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsFalse(
      await Page.EvaluateAsync<bool>(
        "() => Boolean(window.__agenticInjected)"
      )
    );
    var unsafeLink = answer.Locator(
      "a",
      new()
      {
        HasText = "unsafe"
      }
    );
    Assert.IsNull(
      await unsafeLink.GetAttributeAsync(
        "href"
      )
    );
    Assert.AreEqual(
      "auto",
      await answer.Locator(
        "pre"
      ).EvaluateAsync<string>(
        "element => getComputedStyle(element).overflowX"
      )
    );
    Assert.AreEqual(
      "auto",
      await answer.Locator(
        "table"
      ).EvaluateAsync<string>(
        "element => getComputedStyle(element).overflowX"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CopiesCompleteMarkdownAndIndividualCodeBlock()
  {
    await Page.AddInitScriptAsync(
      """
      Object.defineProperty(
        navigator,
        "clipboard",
        {
          configurable: true,
          value: {
            writeText: async text => {
              window.__copiedTexts ??= [];
              window.__copiedTexts.push(text);
            }
          }
        }
      );
      """
    );
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "markdown fixture for documentation"
    );
    var answer = Page.Locator(
      ".message.assistant"
    ).Last;

    await Expect(
      answer.Locator(
        ".code-language"
      )
    ).ToHaveTextAsync(
      "C#"
    );
    await answer.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Copiar resposta",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      answer.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Resposta copiada",
          Exact = true
        }
      )
    ).ToBeVisibleAsync();
    var copiedResponse = await Page.EvaluateAsync<string>(
      "() => window.__copiedTexts.at(-1)"
    );
    StringAssert.StartsWith(
      copiedResponse,
      "# Heading"
    );
    StringAssert.Contains(
      copiedResponse,
      "```csharp"
    );

    await answer.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Copiar código C#",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      answer.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Código copiado",
          Exact = true
        }
      )
    ).ToBeVisibleAsync();
    var copiedCode = await Page.EvaluateAsync<string>(
      "() => window.__copiedTexts.at(-1)"
    );
    StringAssert.Contains(
      copiedCode,
      "Console.WriteLine"
    );
    Assert.IsFalse(
      copiedCode.Contains(
        "```",
        StringComparison.Ordinal
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RendersHighlightedMarkdownBeforeStreamingCompletes()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "streaming markdown preview for documentation"
    );
    var answer = Page.Locator(
      ".assistant-answer"
    );

    await Expect(
      answer.Locator(
        "h1"
      )
    ).ToHaveTextAsync(
      "Live heading"
    );
    await Expect(
      answer.Locator(
        "pre"
      )
    ).ToContainTextAsync(
      "\"active\""
    );
    await Expect(
      answer.Locator(
        "pre .jsonKey"
      ).First
    ).ToBeVisibleAsync();
    Assert.AreEqual(
      "rgb(156, 220, 254)",
      await answer.Locator(
        "pre .jsonKey"
      ).First.EvaluateAsync<string>(
        "element => getComputedStyle(element).color"
      )
    );
    await Expect(
      answer.Locator(
        ".code-language"
      )
    ).ToHaveTextAsync(
      "JSON"
    );
    await Expect(
      Page.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Cancelar solicitação",
          Exact = true
        }
      )
    ).ToBeVisibleAsync();
    await Expect(
      answer
    ).ToContainTextAsync(
      "Preview remained active."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingPrimaryFallsBackToGlobalDefault()
  {
    var intentions = _environment.BaselineSettings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );
    intentions["documentation"] = intentions["documentation"] with
    {
      Model = "missing:latest"
    };
    using var saveResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        Intentions = intentions
      }
    );
    saveResponse.EnsureSuccessStatusCode();

    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "documentation request"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"target.model-fallback\"]"
      )
    ).ToContainTextAsync(
      "missing:latest"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello from alpha:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingPrimaryUsesIntentionFallbackBeforeGlobalDefault()
  {
    var intentions = _environment.BaselineSettings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );
    intentions["documentation"] = intentions["documentation"] with
    {
      Model = "missing-primary:latest",
      FallbackModel = "beta:code"
    };
    using var saveResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        Intentions = intentions
      }
    );
    saveResponse.EnsureSuccessStatusCode();

    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "documentation request with configured fallback"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello from beta:code"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"target.model-fallback\"]"
      )
    ).ToContainTextAsync(
      "missing-primary:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NoAvailableConfiguredModelReturnsStructuredResolutionError()
  {
    var intentions = _environment.BaselineSettings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );
    intentions["documentation"] = intentions["documentation"] with
    {
      Model = "missing-primary:latest",
      FallbackModel = "missing-fallback:latest"
    };
    using var saveResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        DefaultModel = "missing-default:latest",
        Intentions = intentions
      }
    );
    saveResponse.EnsureSuccessStatusCode();

    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "documentation request with no available model"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "No installed model could be resolved"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Referência:"
    );
    await Expect(
      Page.Locator(
        ".activity-row.warning"
      ).Last
    ).ToContainTextAsync(
      "target-model-resolution"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GenericHttpFailureDoesNotTriggerResidentModelEviction()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "docs:latest"
    );
    await StartMessageAsync(
      "generic HTTP failure"
    );
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Falhou"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"memory-pressure-detected\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.KeepAlive == 0
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FailedAssistantOutputIsExcludedFromLaterTargetContext()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "generic HTTP failure"
    );
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Falhou"
    );
    var failedText = await Page.Locator(
      ".assistant-answer"
    ).InnerTextAsync();
    await SendMessageAsync(
      "Message after failed response"
    );
    var target = _environment.FakeOllama.Requests
      .Last(
        request => request.Stream
      );
    Assert.HasCount(
      2,
      target.Messages
    );
    Assert.IsFalse(
      target.Messages.Any(
        message => message.Content.Contains(
          failedText,
          StringComparison.Ordinal
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SettingsShowModelStatusesAndModelTestBypassesConversationRouter()
  {
    var intentions = _environment.BaselineSettings.Intentions.ToDictionary(
      pair => pair.Key,
      pair => pair.Value,
      StringComparer.Ordinal
    );
    intentions["documentation"] = intentions["documentation"] with
    {
      Model = "missing-diagnostic:latest",
      FallbackModel = "unused:latest"
    };
    using var saveResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        DefaultModel = "default",
        Intentions = intentions
      }
    );
    saveResponse.EnsureSuccessStatusCode();
    await Page.GotoAsync(
      "/"
    );
    await OpenSettingsAsync();

    await Expect(
      Page.Locator(
        ".model-diagnostic-row",
        new()
        {
          HasText = "Router"
        }
      )
    ).ToContainTextAsync(
      "Loaded"
    );
    await Expect(
      Page.Locator(
        ".model-diagnostic-row:has(> span:first-child:text-is(\"Default\"))"
      )
    ).ToContainTextAsync(
      "Misconfigured"
    );
    await Expect(
      Page.Locator(
        ".model-diagnostic-row",
        new()
        {
          HasText = "documentation · primary"
        }
      )
    ).ToContainTextAsync(
      "Unavailable"
    );
    await Expect(
      Page.Locator(
        ".model-diagnostic-row",
        new()
        {
          HasText = "documentation · fallback"
        }
      )
    ).ToContainTextAsync(
      "Installed"
    );

    await Page.Locator(
      "#model-test-selector"
    ).SelectOptionAsync(
      "unused:latest"
    );
    await Page.Locator(
      "#test-model"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#model-test-result"
      )
    ).ToContainTextAsync(
      "Time to first chunk"
    );
    await Expect(
      Page.Locator(
        "#model-test-result"
      )
    ).ToContainTextAsync(
      "Total duration"
    );
    await Expect(
      Page.Locator(
        ".message"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.HasCount(
      1,
      _environment.FakeOllama.Requests
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests[0].Stream
    );
    Assert.AreEqual(
      "Reply with exactly: OK",
      _environment.FakeOllama.Requests[0].Messages.Last().Content
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ShowsRuntimeMemoryAndConfirmedResidentModel()
  {
    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#runtime-summary"
      )
    ).ToContainTextAsync(
      "RAM"
    );
    await Page.Locator(
      "#runtime-summary"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#runtime-memory-list .runtime-row.system [role=\"progressbar\"]"
      )
    ).ToHaveCountAsync(
      1,
      new()
      {
        Timeout = 5_000
      }
    );
    Assert.IsGreaterThanOrEqualTo(
      12,
      await Page.Locator(
        "#runtime-summary"
      ).EvaluateAsync<double>(
        "element => Number.parseFloat(getComputedStyle(element).fontSize)"
      )
    );
    await Expect(
      Page.Locator(
        "#runtime-model-list"
      )
    ).ToContainTextAsync(
      "router:latest"
    );
    await Expect(
      Page.Locator(
        "#runtime-model-list"
      )
    ).ToContainTextAsync(
      "VRAM"
    );
    await Expect(
      Page.Locator(
        "#runtime-model-list"
      )
    ).ToContainTextAsync(
      "RAM estimada"
    );
    await Expect(
      Page.Locator(
        "#resident-model-status"
      )
    ).ToContainTextAsync(
      "ready"
    );

    using var response = await _environment.HttpClient.GetAsync(
      "api/runtime/status"
    );
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await response.Content.ReadAsStringAsync()
    );
    var root = document.RootElement;
    Assert.AreEqual(
      "available",
      root.GetProperty(
        "systemMemory"
      ).GetProperty(
        "status"
      ).GetString()
    );

    var availableGpuCount = 0;

    foreach (var device in root.GetProperty(
      "devices"
    ).EnumerateArray())
    {
      if (device.GetProperty(
        "usedDedicatedMemoryBytes"
      ).ValueKind == JsonValueKind.Null)
      {
        Assert.AreEqual(
          JsonValueKind.Null,
          device.GetProperty(
            "usedPercent"
          ).ValueKind
        );
      }
      else
      {
        availableGpuCount++;
      }
    }

    await Expect(
      Page.Locator(
        "#runtime-memory-list .runtime-row.gpu [role=\"progressbar\"]"
      )
    ).ToHaveCountAsync(
      availableGpuCount
    );

    Assert.IsTrue(
      _environment.FakeOllama.AllRequests.Any(
        request => request.Model == "router:latest"
          && request.KeepAlive == -1
          && request.ContextTokens == 8_192
          && request.Messages.Count == 0
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OllamaRuntimeProfilesResolveAnalyzeMeasurePersistAndRestore()
  {
    using var profilesResponse = await _environment.HttpClient.GetAsync(
      "api/runtime/profiles"
    );
    profilesResponse.EnsureSuccessStatusCode();
    using var profilesDocument = JsonDocument.Parse(
      await profilesResponse.Content.ReadAsStringAsync()
    );
    var profiles = profilesDocument.RootElement;
    Assert.AreEqual(
      8_192,
      profiles.GetProperty(
        "roleDefaults"
      ).GetProperty(
        "residentCoordinator"
      ).GetProperty(
        "targetContextTokens"
      ).GetInt32()
    );
    Assert.IsTrue(
      profiles.GetProperty(
        "sharedModelWarnings"
      ).EnumerateArray().Any(
        warning => warning.GetProperty(
          "model"
        ).GetString() == "router:latest"
      )
    );

    var loadedBefore = _environment.FakeOllama.LoadedModels.Order(
      StringComparer.Ordinal
    ).ToArray();
    using var analyzeResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/runtime/profiles/analyze",
      new
      {
        model = "alpha:latest",
        role = "primary"
      }
    );
    analyzeResponse.EnsureSuccessStatusCode();
    using var analyzeDocument = JsonDocument.Parse(
      await analyzeResponse.Content.ReadAsStringAsync()
    );
    Assert.IsFalse(
      analyzeDocument.RootElement.GetProperty(
        "loadedModelChanged"
      ).GetBoolean()
    );
    CollectionAssert.AreEqual(
      loadedBefore,
      _environment.FakeOllama.LoadedModels.Order(
        StringComparer.Ordinal
      ).ToArray()
    );

    using var deniedMeasurement = await _environment.HttpClient.PostAsJsonAsync(
      "api/runtime/profiles/measure",
      new
      {
        model = "alpha:latest",
        role = "modelTest",
        contextCandidates = new[]
        {
          4_096
        },
        permissionGranted = false,
        runMinimalRequest = false
      }
    );
    Assert.AreEqual(
      (HttpStatusCode)428,
      deniedMeasurement.StatusCode
    );

    using var measurementResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/runtime/profiles/measure",
      new
      {
        model = "alpha:latest",
        role = "modelTest",
        contextCandidates = new[]
        {
          4_096
        },
        permissionGranted = true,
        runMinimalRequest = false
      }
    );
    measurementResponse.EnsureSuccessStatusCode();
    using var measurementDocument = JsonDocument.Parse(
      await measurementResponse.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      4_096,
      measurementDocument.RootElement.GetProperty(
        "measurement"
      ).GetProperty(
        "actualContext"
      ).GetInt32()
    );
    Assert.IsFalse(
      measurementDocument.RootElement.GetProperty(
        "measurement"
      ).GetProperty(
        "stale"
      ).GetBoolean()
    );
    Assert.IsTrue(
      measurementDocument.RootElement.GetProperty(
        "priorResidentRestored"
      ).GetBoolean()
    );
    CollectionAssert.AreEquivalent(
      loadedBefore,
      _environment.FakeOllama.LoadedModels.ToArray()
    );
    var measurementPath = Path.Combine(
      _environment.DataDirectory,
      "runtime-profiles",
      "ollama-model-memory.json"
    );
    Assert.IsTrue(
      File.Exists(
        measurementPath
      )
    );
    var measurementStore = JsonNode.Parse(
      await File.ReadAllTextAsync(
        measurementPath
      )
    )!.AsObject();
    var originalMeasurement = measurementStore["records"]![0]!.DeepClone();

    foreach (var identityField in new[]
    {
      "digest",
      "ollamaVersion",
      "hardwareSignature",
      "runtimeSettingSignature"
    })
    {
      measurementStore["records"]![0] = originalMeasurement.DeepClone();
      measurementStore["records"]![0]![identityField] =
        $"stale-{identityField}";
      await File.WriteAllTextAsync(
        measurementPath,
        measurementStore.ToJsonString(
          TestJson.Options
        )
      );
      using var staleResponse = await _environment.HttpClient.GetAsync(
        "api/runtime/profiles"
      );
      staleResponse.EnsureSuccessStatusCode();
      using var staleDocument = JsonDocument.Parse(
        await staleResponse.Content.ReadAsStringAsync()
      );
      Assert.IsTrue(
        staleDocument.RootElement.GetProperty(
          "measurements"
        )[0].GetProperty(
          "stale"
        ).GetBoolean(),
        $"Expected {identityField} to invalidate the measurement."
      );
    }

    measurementStore["records"]![0] = originalMeasurement;
    await File.WriteAllTextAsync(
      measurementPath,
      measurementStore.ToJsonString(
        TestJson.Options
      )
    );

    using var yamlResponse = await _environment.HttpClient.GetAsync(
      "api/settings/yaml"
    );
    yamlResponse.EnsureSuccessStatusCode();
    var yaml = await yamlResponse.Content.ReadAsStringAsync();
    StringAssert.Contains(
      yaml,
      "ollama_runtime:"
    );
    Assert.DoesNotContain(
      "hardwareSignature",
      yaml
    );
    Assert.DoesNotContain(
      "ollama-model-memory",
      yaml
    );

    var runtimeOverride = new TestOllamaModelRuntimeOverride(
      "ollama-local",
      "router:latest",
      "digest-router:latest",
      new Dictionary<string, TestOllamaRoleRuntimeSettings>(
        StringComparer.Ordinal
      )
      {
        ["residentCoordinator"] = new(
          4_096,
          12_288,
          16_384,
          -1,
          1_024
        )
      }
    );
    using var saveResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        OllamaRuntime = _environment.BaselineSettings.OllamaRuntime with
        {
          ModelOverrides =
          [
            runtimeOverride
          ]
        }
      }
    );
    saveResponse.EnsureSuccessStatusCode();
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && request.KeepAlive == -1
          && request.ContextTokens == 12_288
          && request.Messages.Count == 0
      )
    );
    using var overrideYamlResponse = await _environment.HttpClient.GetAsync(
      "api/settings/yaml"
    );
    overrideYamlResponse.EnsureSuccessStatusCode();
    var overrideYaml = await overrideYamlResponse.Content.ReadAsStringAsync();
    StringAssert.Contains(
      overrideYaml,
      "model_overrides:"
    );
    StringAssert.Contains(
      overrideYaml,
      "digest-router:latest"
    );
    using var overrideRoundTrip = await _environment.HttpClient.PutAsJsonAsync(
      "api/settings/yaml",
      new
      {
        yaml = overrideYaml
      }
    );
    Assert.AreEqual(
      HttpStatusCode.OK,
      overrideRoundTrip.StatusCode,
      await overrideRoundTrip.Content.ReadAsStringAsync()
    );

    using var statusResponse = await _environment.HttpClient.GetAsync(
      "api/runtime/status"
    );
    statusResponse.EnsureSuccessStatusCode();
    using var statusDocument = JsonDocument.Parse(
      await statusResponse.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      12_288,
      statusDocument.RootElement.GetProperty(
        "residentModel"
      ).GetProperty(
        "actualContextTokens"
      ).GetInt32()
    );
    _environment.FakeOllama.SetLoadedModelContext(
      "router:latest",
      4_096
    );
    using var mismatchResponse = await _environment.HttpClient.GetAsync(
      "api/runtime/status"
    );
    mismatchResponse.EnsureSuccessStatusCode();
    using var mismatchDocument = JsonDocument.Parse(
      await mismatchResponse.Content.ReadAsStringAsync()
    );
    Assert.IsTrue(
      mismatchDocument.RootElement.GetProperty(
        "warning"
      ).GetBoolean()
    );
    Assert.IsTrue(
      mismatchDocument.RootElement.GetProperty(
        "loadedModels"
      ).EnumerateArray().Any(
        model => model.GetProperty(
          "name"
        ).GetString() == "router:latest"
          && model.GetProperty(
            "profileStatus"
          ).GetString() == "context-mismatch"
      )
    );

    var reloadedOverride = runtimeOverride with
    {
      Overrides = new Dictionary<string, TestOllamaRoleRuntimeSettings>(
        StringComparer.Ordinal
      )
      {
        ["residentCoordinator"] = new(
          4_096,
          16_384,
          16_384,
          -1,
          1_024
        )
      }
    };
    using var reloadResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        OllamaRuntime = _environment.BaselineSettings.OllamaRuntime with
        {
          ModelOverrides =
          [
            reloadedOverride
          ]
        }
      }
    );
    reloadResponse.EnsureSuccessStatusCode();
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && request.KeepAlive == 0
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && request.KeepAlive == -1
          && request.ContextTokens == 16_384
      )
    );

    await Page.GotoAsync(
      "/"
    );
    await OpenSettingsAsync();
    await Page.Locator(
      "[data-settings-target=\"harnesses\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#runtime-role-profiles"
      )
    ).ToContainTextAsync(
      "Coordenador residente"
    );
    var memoryPolicy = Page.Locator(
      ".runtime-profile-subsection",
      new()
      {
        Has = Page.GetByText(
          "Política de memória",
          new()
          {
            Exact = true
          }
        )
      }
    );
    await Expect(
      Page.Locator(
        "#runtime-memory-device-policies"
      )
    ).ToBeHiddenAsync();
    await memoryPolicy.Locator(
      "summary"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#runtime-memory-device-policies"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        "#runtime-profile-result"
      )
    ).ToContainTextAsync(
      "Nenhuma análise"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SharedCoordinatorAndSpecialistUseLargestConfiguredContext()
  {
    using var saveResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        ActionModel = "alpha:latest",
        DefaultModel = "alpha:latest"
      }
    );
    saveResponse.EnsureSuccessStatusCode();
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "alpha:latest"
          && request.KeepAlive == -1
          && request.ContextTokens == 32_768
          && request.Messages.Count == 0
      )
    );

    using var profilesResponse = await _environment.HttpClient.GetAsync(
      "api/runtime/profiles"
    );
    profilesResponse.EnsureSuccessStatusCode();
    using var profilesDocument = JsonDocument.Parse(
      await profilesResponse.Content.ReadAsStringAsync()
    );
    Assert.IsTrue(
      profilesDocument.RootElement.GetProperty(
        "sharedModelWarnings"
      ).EnumerateArray().Any(
        warning => warning.GetProperty(
          "model"
        ).GetString() == "alpha:latest"
          && warning.GetProperty(
            "largestConfiguredTarget"
          ).GetInt32() == 32_768
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RequestFitUsesDiscreteEscalationAndRejectsImpossibleContext()
  {
    _environment.FakeOllama.Reset();
    using var escalatedResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = new string(
          'a',
          88_000
        ),
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "chat",
        approvalPolicy = "ask"
      }
    );
    escalatedResponse.EnsureSuccessStatusCode();
    var escalatedEvents = await escalatedResponse.Content.ReadAsStringAsync();
    StringAssert.Contains(
      escalatedEvents,
      "request-context-escalated"
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "alpha:latest"
          && request.Stream
          && request.ContextTokens == 40_960
      )
    );

    _environment.FakeOllama.Reset();
    using var rejectedResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = new string(
          'b',
          132_000
        ),
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "chat",
        approvalPolicy = "ask"
      }
    );
    rejectedResponse.EnsureSuccessStatusCode();
    var rejectedEvents = await rejectedResponse.Content.ReadAsStringAsync();
    StringAssert.Contains(
      rejectedEvents,
      "request-context-does-not-fit"
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "alpha:latest"
          && request.Stream
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AssistantResponseAppearsAboveTechnicalDetails()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Simple ordering request"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"runtime-profile-inherited\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"request-context-fit-evaluated\"]"
      )
    ).ToContainTextAsync(
      "context tokens selected"
    );
    var responseComesFirst = await Page.Locator(
      ".message.assistant"
    ).EvaluateAsync<bool>(
      """
      turn => Boolean(
        turn.querySelector(".assistant-answer").compareDocumentPosition(
          turn.querySelector(".activity")
        ) & Node.DOCUMENT_POSITION_FOLLOWING
      )
      """
    );
    Assert.IsTrue(
      responseComesFirst
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoFollowPausesAndJumpToLatestRestoresIt()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "scroll stream"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Streaming paragraph 10"
    );
    await Page.Locator(
      "#messages"
    ).EvaluateAsync(
      """
      messages => {
        messages.scrollTop = 0;
        messages.dispatchEvent(new Event("scroll"));
      }
      """
    );
    await Expect(
      Page.Locator(
        "#jump-latest"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Concluído"
    );
    var remainingWhilePaused = await RemainingScrollAsync();
    Assert.IsGreaterThan(
      120,
      remainingWhilePaused
    );
    await Page.Locator(
      "#jump-latest"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#jump-latest"
      )
    ).ToBeHiddenAsync();
    Assert.IsLessThanOrEqualTo(
      3,
      await RemainingScrollAsync()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LongUnbrokenTextCannotExpandTheApplicationShell()
  {
    await Page.GotoAsync(
      "/"
    );
    var longToken = new string(
      'A',
      500
    );
    await SendMessageAsync(
      $"long token {longToken}"
    );

    Assert.IsTrue(
      await Page.EvaluateAsync<bool>(
        "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"
      )
    );
    Assert.IsTrue(
      await Page.Locator(
        ".message.user"
      ).EvaluateAsync<bool>(
        "message => message.scrollWidth <= message.clientWidth"
      )
    );
    Assert.IsTrue(
      await Page.Locator(
        ".assistant-answer"
      ).EvaluateAsync<bool>(
        "answer => answer.scrollWidth <= answer.clientWidth"
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CancellationPreservesPartialTextButExcludesItFromContext()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "cancel stream"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      "Hello"
    );
    var partial = await Page.Locator(
      ".assistant-answer"
    ).InnerTextAsync();
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Cancelar solicitação",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToHaveTextAsync(
      "Cancelado"
    );
    await Expect(
      Page.Locator(
        ".assistant-answer"
      )
    ).ToContainTextAsync(
      partial
    );

    await SendMessageAsync(
      "Message after cancellation"
    );
    var target = _environment.FakeOllama.Requests
      .Last(
        request => request.Stream
      );
    Assert.HasCount(
      2,
      target.Messages
    );
    Assert.IsFalse(
      target.Messages.Any(
        message => message.Content.Contains(
          partial,
          StringComparison.Ordinal
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"memory-pressure-detected\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MemoryPressureEvictsRetriesOnceAndRestoresResidentModel()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "memory pressure recover documentation"
    );
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Recuperado"
    );
    await Expect(
      Page.Locator(
        ".activity"
      )
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"memory-pressure-detected\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"resident-model-reloaded\"]"
      )
    ).ToHaveCountAsync(
      1
    );

    Assert.HasCount(
      2,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "docs:latest"
          && request.Stream
      )
    );
    CollectionAssert.Contains(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "router:latest"
    );
    CollectionAssert.DoesNotContain(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "docs:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FailedMemoryRetryDoesNotLoopAndStillRestoresResidentModel()
  {
    await Page.GotoAsync(
      "/"
    );
    await StartMessageAsync(
      "memory pressure fail documentation"
    );
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Falhou"
    );
    Assert.HasCount(
      2,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "docs:latest"
          && request.Stream
      )
    );
    CollectionAssert.Contains(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "router:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MissingResidentModelIsPreloadedAgain()
  {
    _environment.FakeOllama.RemoveLoadedModel(
      "router:latest"
    );
    await WaitUntilAsync(
      () => _environment.FakeOllama.LoadedModels.Contains(
        "router:latest"
      ),
      TimeSpan.FromSeconds(
        20
      )
    );
    CollectionAssert.Contains(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "router:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task TrustedWorkspaceModalValidatesPersistsAndClearsPath()
  {
    var additionalWorkspace = _environment.CreateWorkspaceDirectory(
      "workspace-modal"
    );
    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#workspace-badge"
      )
    ).ToHaveTextAsync(
      "Ativo"
    );
    await Expect(
      Page.Locator(
        "#workspace-path"
      )
    ).ToContainTextAsync(
      "workspace"
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#new-workspace-section"
      )
    ).ToBeHiddenAsync();
    Assert.IsTrue(
      await Page.Locator(
        "#saved-workspaces-section"
      ).EvaluateAsync<bool>(
        "section => section.open"
      )
    );
    Assert.IsTrue(
      await Page.Locator(
        "#local-history-section"
      ).EvaluateAsync<bool>(
        "section => section.open"
      )
    );
    Assert.IsFalse(
      await Page.Locator(
        "#project-profile-section"
      ).EvaluateAsync<bool>(
        "section => section.open"
      )
    );
    Assert.IsFalse(
      await Page.Locator(
        "#validation-profile-section"
      ).EvaluateAsync<bool>(
        "section => section.open"
      )
    );
    var workspaceFooter = Page.Locator(
      "#workspace-dialog .dialog-footer"
    );
    var workspaceFooterTop = await workspaceFooter.EvaluateAsync<double>(
      "element => element.getBoundingClientRect().top"
    );
    await Page.Locator(
      "#project-profile-section, #validation-profile-section"
    ).EvaluateAllAsync<bool>(
      """
      sections => {
        sections.forEach(section => section.open = true);
        return true;
      }
      """
    );
    Assert.IsTrue(
      await Page.Locator(
        "#workspace-dialog .dialog-body"
      ).EvaluateAsync<bool>(
        "body => body.scrollHeight > body.clientHeight"
      )
    );
    await Page.Locator(
      "#workspace-dialog .dialog-body"
    ).EvaluateAsync(
      "body => body.scrollTop = body.scrollHeight"
    );
    Assert.AreEqual(
      workspaceFooterTop,
      await workspaceFooter.EvaluateAsync<double>(
        "element => element.getBoundingClientRect().top"
      ),
      1
    );
    Assert.AreEqual(
      await Page.Locator(
        "#workspace-dialog"
      ).EvaluateAsync<double>(
        "element => element.getBoundingClientRect().bottom"
      ),
      await workspaceFooter.EvaluateAsync<double>(
        "element => element.getBoundingClientRect().bottom"
      ),
      1
    );
    await Page.Locator(
      "#project-profile-section, #validation-profile-section"
    ).EvaluateAllAsync<bool>(
      """
      sections => {
        sections.forEach(section => section.open = false);
        return true;
      }
      """
    );
    var workspaceInformation = Page.Locator(
      ".information-button[data-tooltip="
        + "'Somente um workspace fica ativo por vez.']"
    );
    await workspaceInformation.HoverAsync();
    await Page.WaitForFunctionAsync(
      """
      () => getComputedStyle(
        document.querySelector(
          '.information-button[data-tooltip="Somente um workspace fica ativo por vez."]'
        ),
        '::after'
      ).opacity === '1'
      """
    );
    Assert.AreEqual(
      "1",
      await workspaceInformation.EvaluateAsync<string>(
        "button => getComputedStyle(button, '::after').opacity"
      )
    );
    StringAssert.Contains(
      await workspaceInformation.EvaluateAsync<string>(
        "button => getComputedStyle(button, '::after').content"
      ),
      "Somente um workspace"
    );
    await Page.Locator(
      "#add-workspace"
    ).ClickAsync();
    await Expect(
      Page.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Selecionar pasta",
          Exact = true
        }
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      "#trusted-workspace-path"
    ).FillAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "missing"
      )
    );
    await Page.Locator(
      "#workspace-dialog"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Salvar workspace",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#workspace-validation"
      )
    ).ToContainTextAsync(
      "does not exist"
    );
    await Page.Locator(
      "#workspace-profile-name"
    ).FillAsync(
      "Workspace modal"
    );
    await Page.Locator(
      "#trusted-workspace-path"
    ).FillAsync(
      additionalWorkspace
    );
    await Page.Locator(
      "#workspace-dialog"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Salvar workspace",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".workspace-profile-entry.active"
      )
    ).ToContainTextAsync(
      "Workspace modal"
    );
    await Page.Locator(
      "#close-workspace"
    ).ClickAsync();
    await Page.ReloadAsync();
    await Expect(
      Page.Locator(
        "#workspace-path"
      )
    ).ToContainTextAsync(
      "Workspace modal"
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#clear-workspace"
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#workspace-badge"
      )
    ).ToHaveTextAsync(
      "Ativo"
    );
    Assert.IsTrue(
      Directory.Exists(
        additionalWorkspace
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ChatModeNeverPlansOrExecutesLocalActions()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "execute create file in chat mode"
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
        "[data-event-type=\"interaction.chat\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-started\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Content.Contains(
            "SPECIALIST_TOOL_LOOP_V2",
            StringComparison.Ordinal
          )
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AskPolicyWaitsForApprovalBeforeCreatingFile()
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
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToBeVisibleAsync();
    Assert.IsFalse(
      File.Exists(
        file
      )
    );
    await Page.Locator(
      ".action-approval"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Aprovar",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.edit-applied\"]"
      )
    ).ToBeAttachedAsync();
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Concluído"
    );
    Assert.AreEqual(
      "hello from agent",
      await File.ReadAllTextAsync(
        file
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
      request => request.Model == "alpha:latest"
        && !request.Stream
        && request.Messages.Any(
          message => message.Content.Contains(
            "EXPERT_EXECUTION_GUIDANCE_V1",
            StringComparison.Ordinal
          )
        )
      )
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.HasTools
          && request.Messages.Any(
            message => message.Content.Contains(
              "SPECIALIST_TOOL_LOOP_V2",
              StringComparison.Ordinal
            )
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.coordination-path-resolved\"]"
      )
    ).ToContainTextAsync(
      "alpha:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToHaveCountAsync(0);
    await Expect(
      Page.Locator(
        "[data-event-type=\"execution-plan-created\"]"
      )
    ).ToHaveCountAsync(0);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AskPolicyDeleteCanBeEditedApprovedAndUndone()
  {
    var first = Path.Combine(
      _environment.WorkspaceDirectory,
      "obsolete-a.txt"
    );
    var second = Path.Combine(
      _environment.WorkspaceDirectory,
      "obsolete-b.txt"
    );
    var keep = Path.Combine(
      _environment.WorkspaceDirectory,
      "keep.txt"
    );
    var secondBytes = new byte[]
    {
      0,
      1,
      2,
      255
    };
    await File.WriteAllTextAsync(first, "first obsolete file");
    await File.WriteAllBytesAsync(second, secondBytes);
    await File.WriteAllTextAsync(keep, "keep me");

    await Page.GotoAsync("/");
    await SetExecuteModeAsync("ask");
    await StartMessageAsync(
      "execute delete files direct obsolete-a.txt and obsolete-b.txt"
    );

    var approval = Page.Locator(
      ".action-approval"
    );
    await Expect(approval).ToBeVisibleAsync();
    Assert.IsTrue(File.Exists(first));
    Assert.IsTrue(File.Exists(second));
    var editor = approval.GetByRole(
      AriaRole.Textbox,
      new()
      {
        Name = "Editar comando de delete_paths",
        Exact = true
      }
    );
    await Expect(editor).ToHaveValueAsync(
      "obsolete-a.txt\nobsolete-b.txt"
    );
    await Expect(approval).ToContainTextAsync("delete_paths");
    await editor.FillAsync("obsolete-b.txt");
    await Expect(
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Atualizar",
          Exact = true
        }
      )
    ).ToHaveCountAsync(0);

    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Aprovar",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator("[data-event-type=\"action.edit-applied\"]")
    ).ToBeAttachedAsync();
    await Expect(
      Page.Locator(".activity > summary")
    ).ToContainTextAsync("Conclu");

    Assert.IsTrue(File.Exists(first));
    Assert.IsFalse(File.Exists(second));
    Assert.IsTrue(File.Exists(keep));

    await Page.Locator(".review-changes").ClickAsync();
    await Expect(Page.Locator("#undo-execution")).ToBeEnabledAsync();
    await Page.Locator("#undo-execution").ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(Page.Locator("#undo-status")).ToContainTextAsync("undone");

    Assert.AreEqual("first obsolete file", await File.ReadAllTextAsync(first));
    CollectionAssert.AreEqual(secondBytes, await File.ReadAllBytesAsync(second));
    Assert.IsTrue(File.Exists(keep));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoPolicyRecursivelyDeletesAndRestoresDirectoryWithoutApprovalPrompt()
  {
    var root = Path.Combine(_environment.WorkspaceDirectory, "obsolete-tree");
    var nested = Path.Combine(root, "nested");
    var empty = Path.Combine(root, "empty");
    Directory.CreateDirectory(nested);
    Directory.CreateDirectory(empty);
    await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "root");
    await File.WriteAllTextAsync(Path.Combine(nested, "child.txt"), "child");

    await Page.GotoAsync("/");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("delete directory recursive");
    await Expect(Page.Locator(".action-approval")).ToHaveCountAsync(0);
    Assert.IsFalse(Directory.Exists(root));

    await Page.Locator(".review-changes").ClickAsync();
    await Expect(Page.Locator("#undo-execution")).ToBeEnabledAsync();
    await Page.Locator("#undo-execution").ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(Page.Locator("#undo-status")).ToContainTextAsync("undone");
    Assert.AreEqual("root", await File.ReadAllTextAsync(Path.Combine(root, "root.txt")));
    Assert.AreEqual("child", await File.ReadAllTextAsync(Path.Combine(nested, "child.txt")));
    Assert.IsTrue(Directory.Exists(empty));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConfirmedToolingModelCoordinatesLocalActionsDirectly()
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
    await SendMessageAsync(
      "execute create file"
    );
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );

    Assert.AreEqual(
      "hello from agent",
      await File.ReadAllTextAsync(
        file
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.CapabilityQueries.Contains(
        "command-r:latest",
        StringComparer.Ordinal
      )
    );
    var specialistRequests = _environment.FakeOllama.Requests.Where(
        request => request.Model == "command-r:latest"
          && request.HasTools
          && request.Messages.Any(
            message => message.Content.Contains(
              "SPECIALIST_TOOL_LOOP_V2",
              StringComparison.Ordinal
            )
          )
      ).ToArray();
    Assert.IsNotEmpty(
      specialistRequests
    );
    Assert.IsTrue(
      specialistRequests.All(
        request => !request.AvailableTools.Contains(
          "create_execution_plan",
          StringComparer.Ordinal
        ) && !request.AvailableTools.Contains(
          "revise_execution_plan",
          StringComparer.Ordinal
        )
      )
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "EXPERT_EXECUTION_GUIDANCE_V1",
              StringComparison.Ordinal
            )
          )
          && !request.Messages.Any(
            message => message.Content.Contains(
              "SPECIALIST_TOOL_LOOP_V2",
              StringComparison.Ordinal
            )
          )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-validated\"]"
      )
    ).ToContainTextAsync(
      "command-r:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"execution-plan-created\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && request.Messages.Any(
            message => message.Content.Contains(
              "SPECIALIST_TOOL_LOOP_V2",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExplicitLocalSpecialistCreatesReadmeWithoutPlanResidentOrCloud()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("command-r:latest");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("execute create README");

    var readme = Path.Combine(
      _environment.WorkspaceDirectory,
      "README.md"
    );
    StringAssert.StartsWith(
      await File.ReadAllTextAsync(readme),
      "# Sample Project"
    );
    await Expect(
      Page.Locator("[data-event-type=\"execution-plan-created\"]")
    ).ToHaveCountAsync(0);
    await Expect(
      Page.Locator("[data-event-type=\"agent.resident-bridge-resolved\"]")
    ).ToHaveCountAsync(0);
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "command-r:latest"
          && request.HasTools
          && request.AvailableTools.Contains("create_file", StringComparer.Ordinal)
      )
    );
    Assert.IsEmpty(_environment.FakeCloud.Requests);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenToolingCreatesTextAndCompletesWithoutProcess()
  {
    await AssertQwenToolingScenarioAsync(
      "Create hello.txt containing \"hello\".",
      "hello.txt",
      "hello",
      expectProcessOffered: true,
      expectProcessExecuted: false
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenToolingCreatesReadmeAndCompletesWithoutProcess()
  {
    await AssertQwenToolingScenarioAsync(
      "Create README.md describing this project.",
      "README.md",
      "# Agentic Router\n\nA local-first application for routed chat and supervised development tasks.\n",
      expectProcessOffered: true,
      expectProcessExecuted: false
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenToolingCreatesStaticHtmlAndCompletesWithoutProcess()
  {
    await AssertQwenToolingScenarioAsync(
      "Create index.html with a Hello World page.",
      "index.html",
      "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Hello World</title></head><body><h1>Hello World</h1></body></html>",
      expectProcessOffered: true,
      expectProcessExecuted: false
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenToolingCreatesAndIntentionallyRunsPython()
  {
    await AssertQwenToolingScenarioAsync(
      "Create hello.py that prints \"hello\".",
      "hello.py",
      "print(\"hello\")\n",
      expectProcessOffered: true,
      expectProcessExecuted: true
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenToolingHonorsExplicitDoNotRunConstraint()
  {
    await AssertQwenToolingScenarioAsync(
      "Create hello.py that prints \"hello\". Do not run it.",
      "hello.py",
      "print(\"hello\")\n",
      expectProcessOffered: false,
      expectProcessExecuted: false
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenToolingCanCompleteAfterMalformedProcessIsRejected()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync(
      "Recover after malformed process once recovery.txt has been created."
    );

    Assert.AreEqual(
      "verified before rejected process",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "recovery.txt"
        )
      )
    );
    await Expect(
      Page.Locator("[data-event-type=\"action.policy-denied\"]")
    ).ToContainTextAsync("control character U+000C");
    await Expect(
      Page.Locator("[data-event-type=\"action.recovery-decision-required\"]")
    ).ToHaveCountAsync(0);
    await Expect(
      Page.Locator("[data-event-type=\"response.completed\"]")
    ).ToHaveCountAsync(1);

    var qwenToolingRequests = _environment.FakeOllama.Requests.Where(
      request => request.Model == "qwen3-coder:30b"
        && request.HasTools
        && request.Messages.Any(
          message => message.Content.Contains(
            "SPECIALIST_TOOL_LOOP_V2",
            StringComparison.Ordinal
          )
        )
    ).ToArray();
    var proposedProcesses = qwenToolingRequests.SelectMany(
      request => request.Messages
    ).SelectMany(
      message => message.ToolCalls
    ).Where(
      call => call.Name == "run_process"
    ).Select(
      call => call.Arguments.GetRawText()
    ).Distinct(
      StringComparer.Ordinal
    ).Count();
    Assert.AreEqual(1, proposedProcesses);
    Assert.IsTrue(
      qwenToolingRequests.Any(
        request => request.Messages.Any(
          message => message.Role == "tool"
            && message.ToolName == "run_process"
            && message.Content.Contains(
              "\"status\":\"policy-denied\"",
              StringComparison.Ordinal
            )
        )
      )
    );
    Assert.AreEqual(
      2,
      await Page.Locator(
        "[data-event-type=\"action.execution-started\"]"
      ).CountAsync()
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StructuredProcessPathCannotReadOutsideWorkspaceAndAgentRecovers()
  {
    var outsidePath = Path.Combine(
      Path.GetDirectoryName(_environment.WorkspaceDirectory)!,
      "outside-sentinel.txt"
    );
    await File.WriteAllTextAsync(outsidePath, "outside-token=FORBIDDEN");

    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync(
      "Recover after outside process path once process-boundary-recovered.txt has been created."
    );

    Assert.AreEqual(
      "verified before rejected outside process path",
      await File.ReadAllTextAsync(Path.Combine(
        _environment.WorkspaceDirectory,
        "process-boundary-recovered.txt"
      ))
    );
    await Expect(Page.Locator("[data-event-type=\"action.security-denied\"]"))
      .ToContainTextAsync("outside the trusted workspace");
    await Expect(Page.Locator("[data-event-type=\"response.completed\"]"))
      .ToHaveCountAsync(1);
    await Expect(Page.Locator(".message.assistant").Last)
      .Not.ToContainTextAsync("outside-token=FORBIDDEN");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task QwenToolingCorrectsPrematureCompletionBeforeRequiredMutation()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync(
      "Qwen premature completion correction: create correction.txt."
    );

    Assert.AreEqual(
      "created after authoritative completion correction",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "correction.txt"
        )
      )
    );
    await Expect(
      Page.Locator("[data-event-type=\"action.planning-retry\"]")
    ).ToHaveCountAsync(2);
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "qwen3-coder:30b"
          && request.Messages.Any(
            message => message.Content.Contains(
              "HOST_COMPLETION_FACTS",
              StringComparison.Ordinal
            ) && message.Content.Contains(
              "no verified mutation effect",
              StringComparison.Ordinal
            )
          )
      )
    );
    await Expect(
      Page.Locator("[data-event-type=\"response.completed\"]")
    ).ToHaveCountAsync(1);
    await Expect(
      Page.Locator("[data-event-type=\"action.recovery-decision-required\"]")
    ).ToHaveCountAsync(0);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task VanillaManualRequestCompletesWithoutProcessOrMechanicalReread()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "index.html"
      ),
      "<!doctype html><script src=\"game.js\"></script>"
    );
    using (
      var refresh = await _environment.HttpClient.PostAsync(
        "api/workspace/project-profile/refresh",
        null
      )
    )
    {
      refresh.EnsureSuccessStatusCode();
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
      "execute vanilla manual scope: create the game with HTML, vanilla JS and CSS only. "
        + "It does not use Node and does not execute; I will test it manually."
    );

    Assert.AreEqual(
      "window.gameReady = true;",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "game.js"
        )
      )
    );
    Assert.IsFalse(
      Directory.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "vanilla-manual-scope"
        )
      )
    );

    var actionRequest = _environment.FakeOllama.Requests.First(
      request => request.Model == "command-r:latest"
        && request.HasTools
        && request.AvailableTools.Contains(
          "create_file",
          StringComparer.Ordinal
        )
    );
    CollectionAssert.DoesNotContain(
      actionRequest.AvailableTools.ToArray(),
      "run_process"
    );
    CollectionAssert.DoesNotContain(
      actionRequest.AvailableTools.ToArray(),
      "run_validation_profile"
    );
    CollectionAssert.AreEqual(
      new[]
      {
        LocalActionPlanner.RequestToolsetTool,
        "create_file"
      },
      actionRequest.AvailableTools.ToArray()
    );
    Assert.AreEqual(
      0,
      await Page.Locator(
        "[data-event-type=\"execution-step-effect-unmatched\"]"
      ).CountAsync()
    );
    Assert.IsTrue(
      actionRequest.Messages.Any(
        message => message.Content.Contains(
          "This is a vanilla web project",
          StringComparison.Ordinal
        ) && message.Content.Contains(
          "reserved validation for manual testing",
          StringComparison.OrdinalIgnoreCase
        )
      )
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Content.Contains(
            "FUNCTIONGEMMA_FAILURE_EVALUATOR_V1",
            StringComparison.Ordinal
          )
        )
      )
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Content.Contains(
            "Changed files still requiring inspection",
            StringComparison.Ordinal
          )
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"execution-tool-scope-resolved\"]"
      )
    ).ToContainTextAsync(
      "Process execution is unavailable"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExactForkGameRequestInspectsFireworksAndCreatesReviewed256WordGame()
  {
    var fireworksDirectory = Path.Combine(
      _environment.WorkspaceDirectory,
      "fireworks"
    );
    Directory.CreateDirectory(
      fireworksDirectory
    );
    var fireworksScript = Path.Combine(
      fireworksDirectory,
      "firework_engine.js"
    );
    var fireworksStyles = Path.Combine(
      fireworksDirectory,
      "fireworks.css"
    );
    await File.WriteAllTextAsync(
      fireworksScript,
      ForkGameExecutionFixture.ExistingFireworksJavaScript
    );
    await File.WriteAllTextAsync(
      fireworksStyles,
      ForkGameExecutionFixture.ExistingFireworksCss
    );
    using (
      var refresh = await _environment.HttpClient.PostAsync(
        "api/workspace/project-profile/refresh",
        null
      )
    )
    {
      refresh.EnsureSuccessStatusCode();
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
      ForkGameExecutionFixture.ExactRequest
    );

    var rootFiles = Directory.GetFiles(
      _environment.WorkspaceDirectory
    ).Select(
      Path.GetFileName
    ).OrderBy(
      name => name,
      StringComparer.Ordinal
    ).ToArray();
    CollectionAssert.AreEqual(
      new[]
      {
        "game.js",
        "index.html",
        "styles.css",
        "words.js"
      },
      rootFiles
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "fireworks"
      },
      Directory.GetDirectories(
        _environment.WorkspaceDirectory
      ).Select(
        Path.GetFileName
      ).OrderBy(
        name => name,
        StringComparer.Ordinal
      ).ToArray()
    );
    Assert.IsFalse(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "package.json"
        )
      )
    );
    Assert.IsFalse(
      Directory.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "node_modules"
        )
      )
    );
    Assert.AreEqual(
      ForkGameExecutionFixture.ExistingFireworksJavaScript,
      await File.ReadAllTextAsync(
        fireworksScript
      )
    );
    Assert.AreEqual(
      ForkGameExecutionFixture.ExistingFireworksCss,
      await File.ReadAllTextAsync(
        fireworksStyles
      )
    );

    var wordsSource = await File.ReadAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "words.js"
      )
    );
    const string wordsPrefix = "window.FORK_GAME_WORDS = Object.freeze(";
    Assert.IsTrue(
      wordsSource.StartsWith(
        wordsPrefix,
        StringComparison.Ordinal
      )
    );
    Assert.IsTrue(
      wordsSource.EndsWith(
        ");",
        StringComparison.Ordinal
      )
    );
    using var wordsDocument = JsonDocument.Parse(
      wordsSource[wordsPrefix.Length..^2]
    );
    var words = wordsDocument.RootElement.EnumerateArray().Select(
      word => word.GetString()!
    ).ToArray();
    Assert.HasCount(
      256,
      words
    );
    Assert.AreEqual(
      256,
      words.Distinct(
        StringComparer.Ordinal
      ).Count()
    );
    Assert.IsTrue(
      words.All(
        word => Regex.IsMatch(
          word,
          "^[a-z]+$",
          RegexOptions.CultureInvariant
        )
      )
    );
    CollectionAssert.AreEqual(
      ForkGameExecutionFixture.Words,
      words
    );

    var index = await File.ReadAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "index.html"
      )
    );
    var game = await File.ReadAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "game.js"
      )
    );
    StringAssert.Contains(
      index,
      "href=\"fireworks/fireworks.css\""
    );
    StringAssert.Contains(
      index,
      "src=\"fireworks/firework_engine.js\""
    );
    Assert.IsLessThan(
      index.IndexOf("src=\"game.js\"", StringComparison.Ordinal),
      index.IndexOf("src=\"words.js\"", StringComparison.Ordinal)
    );
    Assert.IsLessThan(
      index.IndexOf("src=\"game.js\"", StringComparison.Ordinal),
      index.IndexOf("src=\"fireworks/firework_engine.js\"", StringComparison.Ordinal)
    );
    StringAssert.Contains(
      game,
      "function visibleWord()"
    );
    StringAssert.Contains(
      game,
      "Enter one letter from A to Z."
    );
    StringAssert.Contains(
      game,
      "finishGame(\"won\")"
    );
    StringAssert.Contains(
      game,
      "finishGame(\"lost\")"
    );
    StringAssert.Contains(
      game,
      "window.FireworksEffect.launch({ outcome, word });"
    );

    var finalActionRequest = _environment.FakeOllama.Requests.Last(
      request => request.Model == "qwen3-coder:30b"
        && request.HasTools
        && request.AvailableTools.Contains(
          "create_file",
          StringComparer.Ordinal
        )
    );
    CollectionAssert.DoesNotContain(
      finalActionRequest.AvailableTools.ToArray(),
      "run_process"
    );
    CollectionAssert.DoesNotContain(
      finalActionRequest.AvailableTools.ToArray(),
      "run_validation_profile"
    );
    var calls = _environment.FakeOllama.Requests.Where(
      request => request.Model == "qwen3-coder:30b"
        && request.HasTools
    ).SelectMany(
      request => request.Messages
    ).SelectMany(
      message => message.ToolCalls
    ).Where(
      call => call.Name != LocalActionPlanner.RequestToolsetTool
    ).DistinctBy(
      call => $"{call.Name}:{call.Arguments.GetRawText()}",
      StringComparer.Ordinal
    ).ToArray();
    CollectionAssert.DoesNotContain(
      calls.Select(call => call.Name).ToArray(),
      "run_process"
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "list_files",
        "read_file",
        "read_file"
      },
      calls.Take(
        3
      ).Select(
        call => call.Name
      ).ToArray()
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "fireworks/firework_engine.js",
        "fireworks/fireworks.css"
      },
      calls.Take(
        3
      ).Where(
        call => call.Name == "read_file"
      ).Select(
        call => call.Arguments.GetProperty("path").GetString()
      ).ToArray()
    );
    Assert.AreEqual(
      "fireworks",
      calls[0].Arguments.GetProperty("path").GetString()
    );
    Assert.IsTrue(
      calls[0].Arguments.GetProperty("recursive").GetBoolean()
    );
    var executedActions = await Page.Locator(
      "[data-event-type=\"action.execution-started\"]"
    ).AllTextContentsAsync();
    Assert.HasCount(
      11,
      executedActions,
      $"Observed actions: {string.Join(" | ", executedActions)}"
    );
    StringAssert.Contains(executedActions[0], "fireworks");
    StringAssert.Contains(executedActions[1], "firework_engine.js");
    StringAssert.Contains(executedActions[2], "fireworks.css");
    StringAssert.Contains(executedActions[3], "words.js");
    StringAssert.Contains(executedActions[4], "styles.css");
    StringAssert.Contains(executedActions[5], "index.html");
    StringAssert.Contains(executedActions[6], "game.js");
    StringAssert.Contains(executedActions[7], "index.html");
    StringAssert.Contains(executedActions[8], "words.js");
    StringAssert.Contains(executedActions[9], "game.js");
    StringAssert.Contains(executedActions[10], "styles.css");
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"execution-step-effect-unmatched\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.redundant-read-skipped\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-decision-required\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-profile-resolved\"]"
      )
    ).ToContainTextAsync(
      "qwen-code-ollama@1"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MultiFileMutationPlanRequiresBoundedStaticReviewBeforeCompletion()
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
    await SendMessageAsync(
      "execute multi file static review; do not run it"
    );

    Assert.IsTrue(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "review.html"
        )
      )
    );
    Assert.IsTrue(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "review-data.js"
        )
      )
    );
    Assert.AreEqual(
      0,
      await Page.Locator(
        "[data-event-type=\"action.semantic-repair-requested\"]"
      ).CountAsync()
    );
    var reviewedPaths = _environment.FakeOllama.Requests.Where(
      request => request.Model == "command-r:latest"
    ).SelectMany(
      request => request.Messages
    ).SelectMany(
      message => message.ToolCalls
    ).Where(
      call => call.Name == "read_file"
    ).Select(
      call => call.Arguments.GetProperty("path").GetString()
    ).Where(
      path => path is not null
    ).Distinct(
      StringComparer.Ordinal
    ).ToArray();
    CollectionAssert.AreEquivalent(
      new[]
      {
        "review.html",
        "review-data.js"
      },
      reviewedPaths
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "command-r:latest"
          && request.Messages.Any(
            message => message.Content.Contains(
              "Treat minor spelling and grammar mistakes as recoverable input",
              StringComparison.Ordinal
            ) && message.Content.Contains(
              "every explicit content or behavior constraint",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ToolAliasRegistryIsClosedCompleteAndCollisionFree()
  {
    using var response = await _environment.HttpClient.GetAsync(
      "api/capabilities/tool-names"
    );
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await response.Content.ReadAsStringAsync()
    );
    var root = document.RootElement;
    Assert.AreEqual(
      "ordinal-ignore-case",
      root.GetProperty(
        "comparison"
      ).GetString()
    );
    Assert.IsTrue(
      root.GetProperty(
        "collisionFree"
      ).GetBoolean()
    );
    var canonical = root.GetProperty(
      "canonicalTools"
    ).EnumerateArray().Select(
      item => item.GetString()!
    ).ToArray();
    var expected = ExpectedToolAliases();
    var aliases = root.GetProperty(
      "aliases"
    ).EnumerateArray().Select(
      item => (
        Alias: item.GetProperty(
          "alias"
        ).GetString()!,
        Canonical: item.GetProperty(
          "canonicalTool"
        ).GetString()!
      )
    ).ToArray();
    var actual = aliases.ToDictionary(
      item => item.Alias,
      item => item.Canonical,
      StringComparer.OrdinalIgnoreCase
    );

    CollectionAssert.AreEquivalent(
      expected.Keys.ToArray(),
      actual.Keys.ToArray()
    );
    CollectionAssert.AreEquivalent(
      ExpectedCanonicalTools(),
      canonical
    );

    foreach (var registration in expected)
    {
      Assert.IsTrue(
        actual.TryGetValue(
          registration.Key.ToUpperInvariant(),
          out var resolved
        )
      );
      Assert.AreEqual(
        registration.Value,
        resolved
      );
      CollectionAssert.Contains(
        canonical,
        resolved
      );
    }

    Assert.AreEqual(
      aliases.Length,
      aliases.Select(
        item => item.Alias
      ).Distinct(
        StringComparer.OrdinalIgnoreCase
      ).Count()
    );

    foreach (var rejected in DeliberatelyRejectedToolAliases())
    {
      Assert.IsFalse(
        actual.ContainsKey(
          rejected
        )
      );
    }

    Assert.IsTrue(
      expected.Where(
        item => item.Value == "read_file"
      ).All(
        item => !item.Value.Contains(
          "write",
          StringComparison.Ordinal
        ) && !item.Value.Contains(
          "process",
          StringComparison.Ordinal
        ) && !item.Value.StartsWith(
          "git_",
          StringComparison.Ordinal
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeAliasesAndCanonicalCasingUseDeterministicResolverAndAudit()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "alias evidence"
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
      "execute native alias read doc"
    );
    await Expect(
      Page.Locator(
        ".assistant-toolset-request"
      ).Filter(
        new()
        {
          HasText = "Read_Doc → read_file"
        }
      ).Last
    ).ToContainTextAsync(
      "Read_Doc → read_file"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      ).Last
    ).ToContainTextAsync(
      "Read file: hello.txt."
    );
    var aliasExecutionId = await Page.EvaluateAsync<string>(
      "() => state.latestExecutionSessionId"
    );
    using (
      var review = await _environment.HttpClient.GetAsync(
        $"api/execution-sessions/{aliasExecutionId}/review"
      )
    )
    {
      review.EnsureSuccessStatusCode();
      using var reviewDocument = JsonDocument.Parse(
        await review.Content.ReadAsStringAsync()
      );
      var evidence = reviewDocument.RootElement.GetProperty(
        "toolNameResolutions"
      )[0];
      Assert.AreEqual(
        "Read_Doc",
        evidence.GetProperty(
          "originalTool"
        ).GetString()
      );
      Assert.AreEqual(
        "read_file",
        evidence.GetProperty(
          "canonicalTool"
        ).GetString()
      );
      Assert.AreEqual(
        "curated-alias",
        evidence.GetProperty(
          "source"
        ).GetString()
      );
      Assert.AreEqual(
        "toolset-granted",
        evidence.GetProperty(
          "validationOutcome"
        ).GetString()
      );
    }

    await SendMessageAsync(
      "execute case canonical read file"
    );
    await Expect(
      Page.Locator(
        ".assistant-toolset-request"
      ).Filter(
        new()
        {
          HasText = "READ_FILE → read_file"
        }
      ).Last
    ).ToContainTextAsync(
      "READ_FILE → read_file"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StructuredGuidanceUsesTheSameCuratedAliasResolver()
  {
    await BenchmarkStructuredConformanceAsync(
      "structured:latest",
      true
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "structured alias evidence"
    );
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "structured:latest"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute structured alias read doc"
    );

    await Expect(
      Page.Locator(
        ".assistant-toolset-request"
      ).Last
    ).ToContainTextAsync(
      "Read_Doc → read_file"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      ).Last
    ).ToContainTextAsync(
      "Read file: hello.txt."
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnknownAmbiguousToolAliasIsRejectedWithoutExecution()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "must remain unread"
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
    await StartMessageAsync(
      "execute unknown tool alias"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-retry\"]"
      ).Last
    ).ToContainTextAsync(
      "neither canonical nor an approved alias"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.tool-name-normalized\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.proposed\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Page.Locator(
      "#send-button"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".activity > summary"
      ).Last
    ).ToHaveTextAsync(
      "Cancelado"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AliasNormalizationDoesNotBypassPathValidation()
  {
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
    await StartMessageAsync(
      "execute alias path traversal"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.tool-name-normalized\"]"
      ).Filter(
        new()
        {
          HasText = "read_doc -> read_file (curated alias)"
        }
      ).Last
    ).ToContainTextAsync(
      "read_doc -> read_file (curated alias)"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.security-denied\"]"
      ).Filter(
        new()
        {
          HasText = "outside the trusted workspace"
        }
      ).Last
    ).ToContainTextAsync(
      "outside the trusted workspace"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.proposed\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    var executionId = await Page.EvaluateAsync<string>(
      "() => state.latestExecutionSessionId"
    );
    using (
      var review = await _environment.HttpClient.GetAsync(
        $"api/execution-sessions/{executionId}/review"
      )
    )
    {
      review.EnsureSuccessStatusCode();
      using var reviewDocument = JsonDocument.Parse(
        await review.Content.ReadAsStringAsync()
      );
      Assert.AreEqual(
        "rejected",
        reviewDocument.RootElement.GetProperty(
          "toolNameResolutions"
        )[0].GetProperty(
          "validationOutcome"
        ).GetString()
      );
    }
    await Page.Locator(
      "#send-button"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".activity > summary"
      ).Last
    ).ToHaveTextAsync(
      "Cancelado"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnknownToolProposalIsReplannedBeforeFailing()
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
    await SendMessageAsync(
      "execute retry unknown tool create file"
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
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.toolset-request-rejected\"]"
      )
    ).ToContainTextAsync(
      "neither canonical nor an approved alias"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-fallback\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.validation-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RejectedActionDoesNotWriteFile()
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
      "ask"
    );
    await StartMessageAsync(
      "execute create file"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      ".action-approval"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Rejeitar",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.rejected\"]"
      )
    ).ToBeAttachedAsync();
    await Expect(
      Page.Locator(
        ".activity > summary"
      )
    ).ToContainTextAsync(
      "Concluído"
    );
    Assert.IsFalse(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AutoPolicyExecutesAllFileToolsInsideWorkspace()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create directory"
    );
    Assert.IsTrue(
      Directory.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "generated"
        )
      )
    );
    await SendMessageAsync(
      "execute create file"
    );
    var file = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    Assert.AreEqual(
      "hello from agent",
      await File.ReadAllTextAsync(
        file
      )
    );
    await SendMessageAsync(
      "execute list files"
    );
    await SendMessageAsync(
      "execute read file"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      ).Last
    ).ToContainTextAsync(
      "Read file: hello.txt."
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      ).Last
    ).Not.ToContainTextAsync(
      "hello from agent"
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
    await File.WriteAllTextAsync(
      file,
      "hello"
    );
    await SendMessageAsync(
      "execute replace file"
    );
    Assert.AreEqual(
      "updated",
      await File.ReadAllTextAsync(
        file
      )
    );
    await File.WriteAllTextAsync(
      file,
      "hello"
    );
    await SendMessageAsync(
      "execute apply patch"
    );
    Assert.AreEqual(
      "patched",
      await File.ReadAllTextAsync(
        file
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    var obsoleteA = Path.Combine(
      _environment.WorkspaceDirectory,
      "obsolete-a.txt"
    );
    var obsoleteB = Path.Combine(
      _environment.WorkspaceDirectory,
      "obsolete-b.txt"
    );
    await File.WriteAllTextAsync(obsoleteA, "obsolete");
    await File.WriteAllTextAsync(obsoleteB, "obsolete");
    await SendMessageAsync(
      "execute delete files direct obsolete-a.txt and obsolete-b.txt"
    );
    Assert.IsFalse(File.Exists(obsoleteA));
    Assert.IsFalse(File.Exists(obsoleteB));
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ProjectProfileDetectsMarkersInstructionsAndShowsSuggestion()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "AGENTS.md"
      ),
      "Use the repository instructions."
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "Demo.csproj"
      ),
      "<Project Sdk=\"Microsoft.NET.Sdk\" />"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "package.json"
      ),
      "{}"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "playwright.config.js"
      ),
      "export default {};"
    );
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#project-profile-section"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Expect(
      Page.Locator(
        "#project-profile-summary"
      )
    ).ToContainTextAsync(
      "dotnet"
    );
    await Expect(
      Page.Locator(
        "#project-profile-summary"
      )
    ).ToContainTextAsync(
      "node"
    );
    await Expect(
      Page.Locator(
        "#project-profile-details"
      )
    ).ToContainTextAsync(
      "1 arquivo(s) AGENTS.md"
    );
    await Page.Locator(
      "#validation-profile-section"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Expect(
      Page.Locator(
        "#detected-validation-profile"
      )
    ).ToContainTextAsync(
      "Detected .NET and Playwright"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DuplicateWorkspaceRootCreationIsRejectedAndReplannedAsExistingFileEdit()
  {
    var rootFile = Path.Combine(
      _environment.WorkspaceDirectory,
      "hello.txt"
    );
    await File.WriteAllTextAsync(
      rootFile,
      "existing root content"
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
    await SendMessageAsync(
      "execute duplicate workspace root edit"
    );

    Assert.AreEqual(
      "edited existing root file",
      await File.ReadAllTextAsync(
        rootFile
      )
    );
    Assert.IsFalse(
      File.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "workspace",
          "hello.txt"
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.semantic-repair-requested\"]"
      )
    ).ToContainTextAsync(
      "already the project root"
    );
    var plannerRequests = _environment.FakeOllama.Requests.Where(
      request => request.Model == "alpha:latest"
        && request.Messages.Any(
          message => message.Content.Contains(
            "duplicate workspace root edit",
            StringComparison.OrdinalIgnoreCase
          )
        )
    ).ToArray();
    Assert.IsGreaterThanOrEqualTo(3, plannerRequests.Length);
    Assert.IsTrue(
      plannerRequests.Any(
        request => request.Messages.Any(
          message => message.Content.Contains(
            "workspace is already the project root",
            StringComparison.Ordinal
          )
        )
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "alpha:latest"
          && request.Messages.Any(
            message => message.Content.Contains(
              "never prefix a path with the workspace display name",
              StringComparison.Ordinal
            )
          )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MultipleNativeToolCallsRetainAndExecuteOnlyTheFirst()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute multiple native tool calls create file"
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
    Assert.IsFalse(
      Directory.Exists(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "ignored-extra-call"
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests
        .SelectMany(
          request => request.Messages
        )
        .All(
          message => message.ToolCalls.Count <= 1
        )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConfiguredOllamaLimitsAndTimeoutApplyToSpecialistExecution()
  {
    using var save = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        Context = _environment.BaselineSettings.Context with
        {
          DefaultContextTokens = 16_384,
          ProviderContextTokens = 24_576,
          ReservedResponseTokens = 3_072
        },
        Runtime = _environment.BaselineSettings.Runtime with
        {
          GenerationTimeoutSeconds = 1
        },
        OllamaRuntime = _environment.BaselineSettings.OllamaRuntime with
        {
          RoleDefaults = TestOllamaRuntimeDefaults.WithMaximum(
            24_576
          )
        },
        Execution = _environment.BaselineSettings.Execution with
        {
          DirectCoordinatorPlanningFailuresBeforeHandoff = 1,
          ResidentCoordinatorPlanningFailuresBeforeFailure = 1,
          MaxCoordinatorHandoffsPerTurn = 0,
          MaxToolOutputTokens = 768
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
    await StartMessageAsync(
      "execute configurable planner timeout"
    );

    await Expect(
      Page.Locator(
        "[data-event-type=\"error\"]"
      )
    ).ToContainTextAsync(
      "configured generation timeout of 1 second"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"execution-plan-created\"]"
      )
    ).ToHaveCountAsync(0);

    var specialistRequests = _environment.FakeOllama.Requests.Where(
      request => request.Model == "alpha:latest"
        && !request.HasTools
        && request.Messages.Any(
          message => message.Content.Contains(
            "EXPERT_EXECUTION_GUIDANCE_V1",
            StringComparison.Ordinal
          )
        )
    ).ToArray();
    Assert.IsNotEmpty(specialistRequests);
    Assert.IsTrue(
      specialistRequests.All(
        request => request.ContextTokens == 24_576
          && request.PredictTokens == 3_072
      ),
      string.Join(
        ", ",
        specialistRequests.Select(request => $"ctx={request.ContextTokens};predict={request.PredictTokens}")
      )
    );
    var routerRequest = _environment.FakeOllama.Requests.First(
      request => request.Model == "router:latest"
        && !request.HasTools
        && request.Messages.Count > 0
    );
    Assert.AreEqual(
      8_192,
      routerRequest.ContextTokens
    );
    Assert.AreEqual(
      1_024,
      routerRequest.PredictTokens
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StructuredValidationProfileEditorPersistsWithoutShellTextarea()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "Demo.csproj"
      ),
      "<Project Sdk=\"Microsoft.NET.Sdk\" />"
    );
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#validation-profile-section"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Expect(
      Page.Locator("#detected-validation-profile")
    ).ToContainTextAsync("Sugestão detectada:");
    await Page.Locator(
      "#reset-validation-profile"
    ).ClickAsync();
    await Page.Locator(
      "#validation-profile-name"
    ).FillAsync(
      "Saved detected profile"
    );
    await Expect(
      Page.Locator(
        "#validation-profile-section textarea"
      )
    ).ToHaveCountAsync(
      0
    );
    await Page.Locator(
      "#save-validation-profile"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#validation-profile-status"
      )
    ).ToContainTextAsync(
      "Perfil salvo"
    );
    await Page.ReloadAsync();
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#validation-profile-section"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Expect(
      Page.Locator(
        "#validation-profile-name"
      )
    ).ToHaveValueAsync(
      "Saved detected profile"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NestedRepositoryInstructionsAppearInActivityAndReview()
  {
    var nested = Path.Combine(
      _environment.WorkspaceDirectory,
      "nested"
    );
    Directory.CreateDirectory(
      nested
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "AGENTS.md"
      ),
      "Root instruction."
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        nested,
        "AGENTS.md"
      ),
      "Nested instruction overrides root."
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create nested file"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"repository-instructions-loaded\"]"
      ).Last
    ).ToContainTextAsync(
      "AGENTS.md"
    );
    await Page.Locator(
      ".review-changes"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".change-review-context"
      ).First
    ).ToContainTextAsync(
      "nested/AGENTS.md"
    );
    Assert.AreEqual(
      "nested agent output",
      await File.ReadAllTextAsync(
        Path.Combine(
          nested,
          "hello.txt"
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExternalChangeAfterInspectionBlocksApprovedWrite()
  {
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
      "ask"
    );
    await StartMessageAsync(
      "execute write file"
    );
    var approval = Page.Locator(
      ".action-approval"
    );
    await Expect(
      approval
    ).ToBeVisibleAsync();
    await File.WriteAllTextAsync(
      file,
      "external change"
    );
    await approval.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Aprovar",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.execution-error\"]"
      )
    ).ToContainTextAsync(
      "current hash"
    );
    Assert.AreEqual(
      "external change",
      await File.ReadAllTextAsync(
        file
      )
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
        "#change-review-body"
      )
    ).ToContainTextAsync(
      "Conflito em hello.txt"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SequentialAgentEditsRefreshTheObservedFileHash()
  {
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
      "execute sequential apply patch"
    );

    Assert.AreEqual(
      "patched twice",
      await File.ReadAllTextAsync(
        file
      )
    );
    await Expect(
      Page.Locator(
        ".activity [data-event-type=\"action.edit-applied\"]"
      )
    ).ToHaveCountAsync(
      2
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"file-conflict-detected\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task TwoWorkspaceCorrectiveSmokeCoversGitHistoryResumeAndSettings()
  {
    var plainWorkspaceId = await ActiveWorkspaceIdAsync();
    var gitWorkspace = _environment.CreateWorkspaceDirectory(
      $"corrective-smoke-{Guid.NewGuid():N}"
    );
    _ = await RunGitTextAsync(
      gitWorkspace,
      "init",
      "-b",
      "main"
    );
    _ = await RunGitTextAsync(
      gitWorkspace,
      "config",
      "user.name",
      "Corrective Smoke"
    );
    _ = await RunGitTextAsync(
      gitWorkspace,
      "config",
      "user.email",
      "corrective-smoke@example.invalid"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        gitWorkspace,
        "smoke.txt"
      ),
      "baseline"
    );
    _ = await RunGitTextAsync(
      gitWorkspace,
      "add",
      "--",
      "smoke.txt"
    );
    _ = await RunGitTextAsync(
      gitWorkspace,
      "commit",
      "-m",
      "smoke baseline"
    );
    await File.AppendAllTextAsync(
      Path.Combine(
        gitWorkspace,
        "smoke.txt"
      ),
      "\nworking change"
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
    await OpenSettingsAsync();
    var settingsBounds = await Page.Locator(
      "#settings-dialog"
    ).BoundingBoxAsync();
    Assert.IsNotNull(
      settingsBounds
    );
    Assert.IsGreaterThan(
      0.93 * 1280,
      settingsBounds.Width
    );
    await Expect(
      Page.Locator(
        "#settings-navigation"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      "[data-settings-target=\"workspaces\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#settings-git"
      )
    ).ToBeInViewportAsync();
    await Page.Locator(
      "#cancel-settings"
    ).ClickAsync();

    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#add-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#workspace-profile-name"
    ).FillAsync(
      "Git smoke"
    );
    await Page.Locator(
      "#trusted-workspace-path"
    ).FillAsync(
      gitWorkspace
    );
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Salvar workspace"
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-summary"
      )
    ).ToContainTextAsync(
      "main"
    );
    await Page.Locator(
      "#cancel-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#git-card"
    ).ClickAsync();
    await Expect(Page.Locator("#git-dialog")).ToBeVisibleAsync();
    await Page.Locator(
      "[data-git-view=\"working-tree\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-file-list"
      )
    ).ToContainTextAsync(
      "smoke.txt"
    );
    await Page.Locator(
      "#dismiss-git"
    ).ClickAsync();

    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Page.Locator(
      "#enable-session-history"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#conversation-persistence"
      )
    ).ToContainTextAsync(
      "Saved locally"
    );
    await SendMessageAsync(
      "Corrective smoke conversation."
    );
    await Expect(
      Page.Locator(
        "#send-button-label"
      )
    ).ToHaveTextAsync(
      "Enviar"
    );
    await Page.Locator(
      "#new-conversation"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".message"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "#new-conversation"
      )
    ).ToBeEnabledAsync();
    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    await Page.Locator(
      "#recent-sessions .session-entry"
    ).Filter(
      new()
      {
        HasText = "Corrective smoke conversation."
      }
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Retomar"
      }
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        ".message.user"
      )
    ).ToContainTextAsync(
      "Corrective smoke conversation."
    );
    await Expect(
      Page.Locator(
        "#new-conversation"
      )
    ).ToBeEnabledAsync();

    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      $"[data-workspace-id=\"{plainWorkspaceId}\"]"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Ativar"
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        $"[data-workspace-id=\"{plainWorkspaceId}\"].active"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        "#git-badge"
      )
    ).ToHaveTextAsync(
      "Not initialized"
    );
    await Page.Locator(
      "#cancel-workspace"
    ).ClickAsync();
    await SetExecuteModeAsync(
      "ask"
    );
    await Page.Locator(
      "#git-card"
    ).ClickAsync();
    await Expect(Page.Locator("#git-dialog")).ToBeVisibleAsync();
    await Page.Locator(
      "#initialize-git"
    ).ClickAsync();
    await ConfirmAppModalAsync();
    await Expect(
      Page.Locator(
        "#git-summary"
      )
    ).ToContainTextAsync(
      "main"
    );
  }
}
