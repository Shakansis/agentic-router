using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class ChatEndToEndTests : PageTest
{
  private static TestEnvironment _environment = null!;

  [ClassInitialize]
  public static async Task InitializeAsync(
    TestContext _
  )
  {
    _environment = await TestEnvironment.StartAsync();
  }

  [ClassCleanup]
  public static async Task CleanupAsync()
  {
    await _environment.DisposeAsync();
  }

  [TestInitialize]
  public async Task ResetAsync()
  {
    await _environment.ResetSettingsAsync();
  }

  public override BrowserNewContextOptions ContextOptions()
  {
    return new BrowserNewContextOptions
    {
      BaseURL = _environment.BaseUri.ToString(),
      ColorScheme = ColorScheme.Dark,
      ViewportSize = new ViewportSize
      {
        Width = 1280,
        Height = 720
      }
    };
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StartupResolvesRecoveryAndWorkspaceServices()
  {
    using var workspacesResponse = await _environment.HttpClient.GetAsync(
      "api/workspaces"
    );
    workspacesResponse.EnsureSuccessStatusCode();

    using var recoveryResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/recovery/not-pending/decision",
      new
      {
        option = "retry",
        browserSessionId = "startup-smoke",
        executionSessionId = "startup-smoke"
      }
    );

    Assert.AreEqual(
      HttpStatusCode.NotFound,
      recoveryResponse.StatusCode
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task LoadsVersionModelsAndCleanGpuNames()
  {
    using var modelsResponse = await _environment.HttpClient.GetAsync(
      "api/models"
    );
    modelsResponse.EnsureSuccessStatusCode();
    using var modelsDocument = JsonDocument.Parse(
      await modelsResponse.Content.ReadAsStringAsync()
    );
    Assert.AreEqual(
      6,
      modelsDocument.RootElement.GetProperty(
        "models"
      ).GetArrayLength()
    );

    await Page.GotoAsync(
      "/"
    );

    await Expect(
      Page.GetByRole(
        AriaRole.Heading,
        new()
        {
          Name = "Conversa",
          Exact = true
        }
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".app-version"
      )
    ).ToHaveTextAsync(
      "v0.9.1"
    );
    await Expect(
      Page.Locator(
        "#model-selector option"
      )
    ).ToHaveCountAsync(
      7
    );
    await Expect(
      Page.Locator(
        "#model-count"
      )
    ).ToHaveTextAsync(
      "6 instalados"
    );

    using var response = await _environment.HttpClient.GetAsync(
      "api/devices"
    );
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await response.Content.ReadAsStringAsync()
    );

    foreach (var device in document.RootElement.GetProperty(
      "devices"
    ).EnumerateArray())
    {
      var name = device.GetProperty(
        "name"
      ).GetString() ?? string.Empty;
      Assert.IsFalse(
        name.Contains(
          ';',
          StringComparison.Ordinal
        ),
        $"GPU name contains metadata: {name}"
      );
      Assert.IsTrue(
        device.TryGetProperty(
          "available",
          out _
        )
      );
    }

    await OpenSettingsAsync();
    var optionLabels = await Page.Locator(
      "#default-gpu option"
    ).AllTextContentsAsync();
    Assert.IsFalse(
      optionLabels.Any(
        label => label.Contains(
          '·',
          StringComparison.Ordinal
        ) || label.Contains(
          ';',
          StringComparison.Ordinal
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExplicitConformanceBenchmarkUsesInstalledDigestAndTypedFailure()
  {
    using var passingResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/models/conformance",
      new
      {
        model = "alpha:latest",
        restoreResidentModel = false
      }
    );
    passingResponse.EnsureSuccessStatusCode();
    var passing = await passingResponse.Content.ReadFromJsonAsync<JsonElement>(
      TestJson.Options
    );
    Assert.IsTrue(
      passing.GetProperty(
        "passed"
      ).GetBoolean()
    );
    Assert.AreEqual(
      "alpha:latest",
      passing.GetProperty(
        "model"
      ).GetString()
    );
    Assert.AreNotEqual(
      "unknown",
      passing.GetProperty(
        "digest"
      ).GetString()
    );
    Assert.AreEqual(
      "0.13.5-test",
      passing.GetProperty(
        "ollamaVersion"
      ).GetString()
    );
    CollectionAssert.DoesNotContain(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "router:latest"
    );

    using var failingResponse = await _environment.HttpClient.PostAsJsonAsync(
      "api/models/conformance",
      new
      {
        model = "unused:latest",
        restoreResidentModel = true
      }
    );
    failingResponse.EnsureSuccessStatusCode();
    var failing = await failingResponse.Content.ReadFromJsonAsync<JsonElement>(
      TestJson.Options
    );
    Assert.IsFalse(
      failing.GetProperty(
        "passed"
      ).GetBoolean()
    );
    StringAssert.Contains(
      failing.GetProperty(
        "failure"
      ).GetString(),
      "invalid native tool call"
    );
    CollectionAssert.Contains(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "router:latest"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CompactLayoutKeepsControlsIconsAndActiveAgentIdentity()
  {
    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#active-agent-label"
      )
    ).ToHaveTextAsync(
      "Auto (Roteador)"
    );
    await Expect(
      Page.Locator(
        ".status-icon"
      )
    ).ToHaveCountAsync(
      5
    );
    await Expect(
      Page.Locator(
        "#new-conversation .button-icon, "
          + "#open-settings .button-icon, "
          + "#send-button .button-icon"
      )
    ).ToHaveCountAsync(
      3
    );
    Assert.AreEqual(
      "16px",
      await Page.Locator(
        ".sidebar"
      ).EvaluateAsync<string>(
        "element => getComputedStyle(element).gap"
      )
    );
    Assert.IsLessThanOrEqualTo(
      64,
      await Page.Locator(
        ".chat-header"
      ).EvaluateAsync<double>(
        "element => element.getBoundingClientRect().height"
      )
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await Expect(
      Page.Locator(
        "#active-agent-label"
      )
    ).ToHaveTextAsync(
      "command-r:latest"
    );
    await Expect(
      Page.Locator(
        "#approval-policy"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        "#model-lock"
      )
    ).ToBeAttachedAsync();
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
      "#router-model"
    ).SelectOptionAsync(
      "beta:code"
    );
    await Page.Locator(
      "#coordinator-model"
    ).SelectOptionAsync(
      "command-r:latest"
    );
    await Page.Locator(
      "[data-intention=\"documentation\"] .intention-prompt"
    ).FillAsync(
      "Persisted documentation prompt."
    );
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
      "#max-tool-output-tokens"
    ).FillAsync(
      "1536"
    );
    await Page.Locator(
      "#save-settings"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToBeHiddenAsync();

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
        "coordinatorModel"
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
      5,
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
        request => request.Model == "beta:code"
          && request.KeepAlive == -1
          && request.Messages.Count == 0
      )
    );
    CollectionAssert.Contains(
      _environment.FakeOllama.LoadedModels.ToArray(),
      "beta:code"
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
    await Expect(
      Page.Locator(
        "#settings-dialog .information-button"
      )
    ).ToHaveCountAsync(
      5
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
      "[data-settings-target=\"git\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-settings-target=\"git\"]"
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
      "[data-settings-target=\"runtime\"]"
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
        "#settings-errors"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToHaveAttributeAsync(
      "data-section",
      "runtime"
    );

    EventHandler<IDialog>? dismissDialog = null;
    dismissDialog = async (
      _,
      dialog
    ) =>
    {
      Page.Dialog -= dismissDialog;
      await dialog.DismissAsync();
    };
    Page.Dialog += dismissDialog;
    await Page.Keyboard.PressAsync(
      "Escape"
    );
    await Expect(
      Page.Locator(
        "#settings-dialog"
      )
    ).ToBeVisibleAsync();

    EventHandler<IDialog>? acceptDialog = null;
    acceptDialog = async (
      _,
      dialog
    ) =>
    {
      Page.Dialog -= acceptDialog;
      await dialog.AcceptAsync();
    };
    Page.Dialog += acceptDialog;
    await Page.Keyboard.PressAsync(
      "Escape"
    );
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
    roundTripResponse.EnsureSuccessStatusCode();
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
    ).ToHaveAttributeAsync(
      "open",
      string.Empty
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
      "The latest user instruction has priority over earlier conversational patterns. "
        + "Do not continue a previous task when the user explicitly changes the objective. "
        + "Do not claim that you executed, tested, opened, accessed, or verified something "
        + "unless the application actually performed that action.",
      requests[1].Messages[0].Content
    );
    Assert.AreEqual(
      "You write documentation.",
      requests[1].Messages[1].Content
    );
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
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ConversationModelLockBypassesRouterUntilUnlocked()
  {
    await Page.GotoAsync(
      "/"
    );
    var selector = Page.Locator(
      "#model-selector"
    );
    var modelLock = Page.Locator(
      "#model-lock"
    );
    await Expect(
      modelLock
    ).ToBeDisabledAsync();
    await selector.SelectOptionAsync(
      "beta:code"
    );
    await modelLock.CheckAsync();
    await Expect(
      selector
    ).ToBeDisabledAsync();

    await SendMessageAsync(
      "First locked request"
    );
    await SendMessageAsync(
      "Second locked request"
    );
    var lockedRequests = _environment.FakeOllama.Requests;
    Assert.HasCount(
      2,
      lockedRequests
    );
    Assert.IsTrue(
      lockedRequests.All(
        request => request.Stream && request.Model == "beta:code"
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"model.lock-active\"]"
      )
    ).ToHaveCountAsync(
      2
    );

    await modelLock.UncheckAsync();
    await selector.SelectOptionAsync(
      "auto"
    );
    await SendMessageAsync(
      "Write documentation after unlocking"
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests
        .Skip(
          2
        )
        .Any(
          request => !request.Stream && request.Model == "router:latest"
        )
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
    await Page.Locator(
      "#model-lock"
    ).CheckAsync();
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
      "ask"
    );
    await Expect(
      Page.Locator(
        "#model-lock"
      )
    ).Not.ToBeCheckedAsync();
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
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      $"#recent-sessions [data-session-id=\"{firstId}\"]"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Retomar"
      }
    ).ClickAsync();
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
      "ask"
    );
    await Expect(
      Page.Locator(
        "#model-lock"
      )
    ).Not.ToBeCheckedAsync();
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
      5,
      secondTarget.Messages
    );
    CollectionAssert.AreEqual(
      new[]
      {
        "system",
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
      5,
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
          X = 3,
          Y = 3
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
    await input.PressAsync(
      "Enter"
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
      3,
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
      3,
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
          && request.Messages.Count == 0
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ActivityAppearsAboveAssistantResponse()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "Simple ordering request"
    );
    var activityComesFirst = await Page.Locator(
      ".message.assistant"
    ).EvaluateAsync<bool>(
      """
      turn => Boolean(
        turn.querySelector(".activity").compareDocumentPosition(
          turn.querySelector(".assistant-answer")
        ) & Node.DOCUMENT_POSITION_FOLLOWING
      )
      """
    );
    Assert.IsTrue(
      activityComesFirst
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
      3,
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
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#clear-workspace"
    ).ClickAsync();
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
            "LOCAL_ACTION_PLANNER_V1",
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
    var guidanceRequest = _environment.FakeOllama.Requests.Single(
      request => request.Model == "alpha:latest"
        && !request.Stream
        && request.Messages.Any(
          message => message.Content.Contains(
            "EXPERT_EXECUTION_GUIDANCE_V1",
            StringComparison.Ordinal
          )
        )
    );
    Assert.IsFalse(
      guidanceRequest.Messages.Any(
        message => message.Content.Contains(
          "LOCAL_ACTION_PLANNER_V1",
          StringComparison.Ordinal
        )
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.HasTools
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-unconfirmed\"]"
      )
    ).ToContainTextAsync(
      "alpha:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.expert-guidance-prepared\"]"
      )
    ).ToContainTextAsync(
      "alpha:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToContainTextAsync(
      "router:latest"
    );
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
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "command-r:latest"
          && !request.Stream
          && request.HasTools
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
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
              "LOCAL_ACTION_PLANNER_V1",
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
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AdvertisedToolsDoNotBypassFailedBehavioralConformance()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "unused:latest"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
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
        "[data-event-type=\"agent.tooling-advertised\"]"
      )
    ).ToContainTextAsync(
      "behavioral conformance must pass"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-conformance-failed\"]"
      )
    ).ToContainTextAsync(
      "configured coordinator will bridge"
    );
    Assert.IsFalse(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "unused:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task CapabilityInspectionFailureUsesResidentBridge()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#model-selector"
    ).SelectOptionAsync(
      "beta:code"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file"
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
        "[data-event-type=\"agent.tooling-unconfirmed\"]"
      )
    ).ToContainTextAsync(
      "could not be confirmed"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToContainTextAsync(
      "router:latest"
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RouterAndToolingCoordinatorUseSeparateConfiguredModels()
  {
    using var settingsResponse = await _environment.PutSettingsAsync(
      _environment.BaselineSettings with
      {
        CoordinatorModel = "command-r:latest"
      }
    );
    Assert.AreEqual(
      HttpStatusCode.OK,
      settingsResponse.StatusCode
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
        "[data-event-type=\"router.model-resolved\"]"
      )
    ).ToContainTextAsync(
      "router:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToContainTextAsync(
      "command-r:latest"
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "command-r:latest"
          && !request.Stream
          && request.HasTools
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
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
      )
    );
    var requests = _environment.FakeOllama.Requests.ToArray();
    var residentEvictionIndex = Array.FindIndex(
      requests,
      request => request.Model == "router:latest"
        && request.KeepAlive == 0
        && request.Messages.Count == 0
    );
    var coordinatorToolRequestIndex = Array.FindIndex(
      requests,
      request => request.Model == "command-r:latest"
        && request.HasTools
    );
    Assert.IsGreaterThanOrEqualTo(
      0,
      residentEvictionIndex
    );
    Assert.IsGreaterThan(
      residentEvictionIndex,
      coordinatorToolRequestIndex
    );
    Assert.IsTrue(
      requests.Any(
        request => request.Model == "router:latest"
          && request.KeepAlive == -1
          && request.Messages.Count == 0
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task InvalidPlannerResponseRetriesAndRecoversOnThirdAttempt()
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
      "execute retry invalid planner create file"
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
    var retries = Page.Locator(
      "[data-event-type=\"action.planning-retry\"]"
    );
    await Expect(
      retries
    ).ToHaveCountAsync(
      3
    );
    await Expect(
      retries.First
    ).ToContainTextAsync(
      "attempt 1 of 2 failed"
    );
    await Expect(
      retries.Last
    ).ToContainTextAsync(
      "attempt 2 of 5 failed"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-fallback\"]"
      )
    ).ToContainTextAsync(
      "resident agent will take over with a reset error counter"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToContainTextAsync(
      "reset planning error counter"
    );
    Assert.HasCount(
      2,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "command-r:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
    Assert.HasCount(
      5,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ResidentTakesOverWhenFallbackGuidanceIsEmpty()
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
      "execute target planner invalid empty takeover guidance create file"
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
        "[data-event-type=\"agent.tooling-fallback\"]"
      )
    ).ToContainTextAsync(
      "resident agent will take over"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.expert-guidance-unavailable\"]"
      )
    ).ToContainTextAsync(
      "resident agent will take over directly"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.resident-bridge-resolved\"]"
      )
    ).ToContainTextAsync(
      "reset planning error counter"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.validation-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.HasCount(
      2,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "command-r:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StringNullToolFromResidentIsReplannedBeforeExecution()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute string null planner create file"
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
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToContainTextAsync(
      "invalid local action proposal"
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
        request => request.Model == "router:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
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
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToContainTextAsync(
      "invalid local action proposal"
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
  public async Task RecoveryLimitOffersChoicesAndCanStopWithoutFatalFailure()
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
      "execute always invalid planner"
    );

    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToHaveCountAsync(
      3
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-fallback\"]"
      )
    ).ToContainTextAsync(
      "resident agent will take over with a reset error counter"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-decision-required\"]"
      )
    ).ToContainTextAsync(
      "Automatic recovery reached its bounded limit"
    );
    var checkpoint = Page.Locator(
      "[data-event-type=\"action.recovery-decision-required\"]"
    );
    await Expect(
      checkpoint.Locator(
        "[data-recovery-option]"
      )
    ).ToHaveCountAsync(
      3
    );
    await Expect(
      checkpoint
    ).ToContainTextAsync(
      "A · Tentar novamente"
    );
    await Expect(
      checkpoint
    ).ToContainTextAsync(
      "B · Pedir nova estratégia"
    );
    await Expect(
      checkpoint
    ).ToContainTextAsync(
      "C · Encerrar e manter alterações"
    );
    await checkpoint.Locator(
      "[data-recovery-option=\"stop\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-stopped\"]"
      )
    ).ToContainTextAsync(
      "Existing changes were preserved"
    );
    await Expect(
      checkpoint
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"response.completed\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    Assert.HasCount(
      2,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "command-r:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
    Assert.HasCount(
      3,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RecoveryCheckpointRetryResumesWithFreshBoundedBudget()
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
      "execute human recovery retry create file"
    );
    var checkpoint = Page.Locator(
      "[data-event-type=\"action.recovery-decision-required\"]"
    );
    await Expect(
      checkpoint
    ).ToBeVisibleAsync();
    await checkpoint.Locator(
      "[data-recovery-option=\"retry\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-resumed\"]"
      )
    ).ToContainTextAsync(
      "fresh bounded recovery budget"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.edit-applied\"]"
      )
    ).ToHaveCountAsync(
      1
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
        "[data-event-type=\"error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RecoveryBudgetResetsAfterVerifiedProgress()
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
    await SendMessageAsync(
      "execute recovery budget reset create file"
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
    var retries = Page.Locator(
      "[data-event-type=\"action.planning-retry\"]"
    );
    await Expect(
      retries
    ).ToHaveCountAsync(
      5
    );
    await Expect(
      retries.Last
    ).ToContainTextAsync(
      "Recovery budget: 1/5"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-decision-required\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RecoveryCheckpointCanRequestRevisedStrategy()
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
      "execute human recovery specialist create file"
    );
    var checkpoint = Page.Locator(
      "[data-event-type=\"action.recovery-decision-required\"]"
    );
    await Expect(
      checkpoint
    ).ToBeVisibleAsync();
    await checkpoint.Locator(
      "[data-recovery-option=\"specialist\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-guidance-prepared\"]"
      ).First
    ).ToContainTextAsync(
      "materially revised strategy"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.edit-applied\"]"
      )
    ).ToHaveCountAsync(
      1
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
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Content.Contains(
            "Exact failure to correct:",
            StringComparison.Ordinal
          ) && message.Content.Contains(
            "Every execution plan step requires a textual title.",
            StringComparison.Ordinal
          )
        )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task UnchangedRecoveryStrategyIsRejectedAndReported()
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
      "execute human recovery unchanged strategy create file"
    );
    var checkpoint = Page.Locator(
      "[data-event-type=\"action.recovery-decision-required\"]"
    );
    await Expect(
      checkpoint
    ).ToBeVisibleAsync();
    await checkpoint.Locator(
      "[data-recovery-option=\"specialist\"]"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-guidance-unchanged\"]"
      )
    ).ToContainTextAsync(
      "repeated the previous strategy after two bounded revision attempts"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-guidance-prepared\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.edit-applied\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"response.completed\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Messages.Any(
          message => message.Content.StartsWith(
            "RECOVERY_STRATEGY_REVISION_REJECTED",
            StringComparison.Ordinal
          )
        )
      )
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
  public async Task ExecuteCreatesVisibleFactDrivenPlanAboveAnswer()
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
    var plan = Page.Locator(
      ".execution-plan"
    ).Last;
    await Expect(
      plan
    ).ToBeVisibleAsync();
    await Expect(
      plan
    ).ToContainTextAsync(
      "completed"
    );
    var isBeforeAnswer = await Page.EvaluateAsync<bool>(
      """
      () => {
        const message = document.querySelector(".message.assistant:last-of-type");
        const plan = message.querySelector(".execution-plan");
        const answer = message.querySelector(".assistant-answer");
        return Boolean(plan.compareDocumentPosition(answer) & Node.DOCUMENT_POSITION_FOLLOWING);
      }
      """
    );
    Assert.IsTrue(
      isBeforeAnswer
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HostGeneratesStablePlanIdsFromModelTitles()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute create file with host generated plan ids"
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
        ".execution-plan .plan-step[data-step-id=\"step-1\"]"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToHaveCountAsync(
      0
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FileEditRecoversFromInvalidPlanBeforeAnyLocalAction()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "hello"
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute recover rejected execution plan write file"
    );

    Assert.AreEqual(
      "rewritten by agent",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToContainTextAsync(
      "Execution plan step titles must be short descriptions"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"execution-plan-created\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.validation-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    var plannerRequests = _environment.FakeOllama.Requests.Where(
      request => !request.Stream
        && request.Messages.Any(
          message => message.Content.Contains(
            "LOCAL_ACTION_PLANNER_V1",
            StringComparison.Ordinal
          )
        )
    ).ToArray();
    CollectionAssert.AreEqual(
      new[]
      {
        "create_execution_plan"
      },
      plannerRequests[0].AvailableTools.ToArray()
    );
    Assert.IsTrue(
      plannerRequests.Any(
        request => request.AvailableTools.Contains(
          "write_file",
          StringComparer.Ordinal
        ) && !request.AvailableTools.Contains(
          "create_execution_plan",
          StringComparer.Ordinal
        )
      )
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
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToContainTextAsync(
      "already the project root"
    );
    var plannerRequests = _environment.FakeOllama.Requests.Where(
      request => request.HasTools
        && request.Messages.Any(
          message => message.Content.Contains(
            "duplicate workspace root edit",
            StringComparison.OrdinalIgnoreCase
          )
        )
    ).ToArray();
    Assert.IsTrue(
      plannerRequests.Any(
        request => request.AvailableTools.Contains(
          "read_file",
          StringComparer.Ordinal
        )
      )
    );
    Assert.IsTrue(
      plannerRequests.Any(
        request => request.AvailableTools.Contains(
          "write_file",
          StringComparer.Ordinal
        )
      )
    );
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
  public async Task RevisionPreservesCompletedStepsOmittedByCoordinator()
  {
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "hello.txt"
      ),
      "hello"
    );
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await SendMessageAsync(
      "execute revise plan omitting completed step write file"
    );

    Assert.AreEqual(
      "rewritten by agent",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"execution-plan-revised\"]"
      )
    ).ToContainTextAsync(
      "preserved"
    );
    await Expect(
      Page.Locator(
        ".execution-plan .plan-step[data-step-id=\"step-1\"]"
      )
    ).ToContainTextAsync(
      "completed"
    );
    await Expect(
      Page.Locator(
        ".execution-plan .plan-step[data-step-id=\"step-2\"]"
      )
    ).ToContainTextAsync(
      "completed"
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
        "[data-event-type=\"action.planning-normalized\"]"
      )
    ).ToContainTextAsync(
      "2 native tool calls"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToHaveCountAsync(
      0
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
  public async Task ConfiguredOllamaLimitsAndTimeoutApplyToExecutionPlanning()
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
        ".execution-plan"
      ).Last
    ).ToBeVisibleAsync();

    var plannerRequests = _environment.FakeOllama.Requests.Where(
      request => request.HasTools
        && request.Messages.Any(
          message => message.Content.Contains(
            "LOCAL_ACTION_PLANNER_V1",
            StringComparison.Ordinal
          )
        )
    ).ToArray();
    Assert.IsGreaterThanOrEqualTo(
      2,
      plannerRequests.Length
    );
    Assert.IsTrue(
      plannerRequests.All(
        request => request.ContextTokens == 24_576
          && request.PredictTokens == 768
      )
    );
    var routerRequest = _environment.FakeOllama.Requests.First(
      request => request.Model == "router:latest"
        && !request.HasTools
        && request.Messages.Count > 0
    );
    Assert.AreEqual(
      24_576,
      routerRequest.ContextTokens
    );
    Assert.AreEqual(
      3_072,
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
        "[data-event-type=\"action.edit-applied\"]"
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
      "[data-settings-target=\"git\"]"
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
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
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
    await Page.Locator(
      "#initialize-git"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-summary"
      )
    ).ToContainTextAsync(
      "main"
    );
  }

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
    await Page.Locator(
      "#initialize-git"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-action-status"
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
      "#dismiss-git"
    ).ClickAsync();
    await SetExecuteModeAsync(
      "ask"
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

    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#initialize-git"
    ).ClickAsync();
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

    await Page.Locator(
      "#git-user-name"
    ).FillAsync(
      "Repository User"
    );
    await Page.Locator(
      "#save-git-user-name"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-action-status"
      )
    ).ToContainTextAsync(
      "user.name saved"
    );
    await Page.Locator(
      "#git-user-email"
    ).FillAsync(
      "repository-user@example.invalid"
    );
    await Page.Locator(
      "#save-git-user-email"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#git-action-status"
      )
    ).ToContainTextAsync(
      "user.email saved"
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
      "Alterações pré-existentes: hello.txt"
    );
    await Expect(
      Page.Locator(
        ".preexisting-change"
      )
    ).ToContainTextAsync(
      "já possuía alterações"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitDeliverySeparatesSelectionAndRequiresExplicitStageAndUnstageApproval()
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
  public async Task SavedValidationProfileRunsInOrderAndGroundsCompletion()
  {
    using var save = await _environment.HttpClient.PutAsJsonAsync(
      "api/workspace/validation-profile",
      new
      {
        name = "Version check",
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
      "Check dotnet version: passed"
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
      "1 arquivos"
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
      "Verificado"
    );
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#undo-execution"
    ).ClickAsync();
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
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#undo-execution"
    ).ClickAsync();
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
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#undo-execution"
    ).ClickAsync();
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
      approval
    ).ToHaveAttributeAsync(
      "data-decision",
      "approved"
    );
    await Expect(
      approval
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
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
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Aprovar",
          Exact = true
        }
      )
    ).ToBeDisabledAsync();
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
        Name = "Rejeitar",
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
      "Rejeitada"
    );
    await Expect(
      approval.GetByRole(
        AriaRole.Button,
        new()
        {
          Name = "Rejeitar",
          Exact = true
        }
      )
    ).ToBeDisabledAsync();
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
      "Enviar"
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
        modelLocked = false,
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
  public async Task CancellationAfterCompletedWriteKeepsChangeReviewAvailable()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute create file cancel stream"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.edit-applied\"]"
      )
    ).ToBeAttachedAsync();
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
    await Expect(
      Page.Locator(
        "[data-event-type=\"request.cancelled\"]"
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
        "[data-event-type=\"action.edit-applied\"]"
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
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Nova conversa",
        Exact = true
      }
    ).ClickAsync();
    await Page.Locator(
      "#new-conversation-discard"
    ).ClickAsync();
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute destructive process"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.policy-denied\"]"
      )
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
  public async Task TruncatedPlannerToolCallTriggersResidentRecovery()
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
      "execute target planner invalid truncated planner tool call create file"
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
        "[data-event-type=\"agent.tooling-fallback\"]"
      )
    ).ToContainTextAsync(
      "configured coordinator will take over"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.provider-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.HasCount(
      2,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "command-r:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task XmlToolProtocolFailureHandsOffWithoutIdenticalRetry()
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
      "execute xml syntax tool call create file"
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
        "[data-event-type=\"action.tool-protocol-error\"]"
      )
    ).ToContainTextAsync(
      "will not be retried automatically"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.tooling-fallback\"]"
      )
    ).ToContainTextAsync(
      "disabled for this turn after its first protocol failure"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.HasCount(
      1,
      _environment.FakeOllama.Requests.Where(
        request => request.Model == "command-r:latest"
          && !request.Stream
          && request.HasTools
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
    Assert.IsTrue(
      _environment.FakeOllama.Requests.Any(
        request => request.Model == "router:latest"
          && !request.Stream
          && request.HasTools
          && request.Messages.Any(
            message => message.Content.Contains(
              "LOCAL_ACTION_PLANNER_V1",
              StringComparison.Ordinal
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task StructuredProcessCapturesOutputAndUnknownCommandNeedsApproval()
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
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.process-output\"]"
      )
    ).ToContainTextAsync(
      "Exit code: 0"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await StartMessageAsync(
      "execute unknown process"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.awaiting-approval\"]"
      )
    ).ToBeVisibleAsync();
    await Page.Locator(
      ".action-approval"
    ).Last.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Aprovar",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.process-output\"]"
      ).Last
    ).ToContainTextAsync(
      "Exit code: 0"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task PrematureProseAfterApprovedProcessReceivesPendingPlanFeedback()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "ask"
    );
    await StartMessageAsync(
      "execute recover premature prose after approved process"
    );
    var approval = Page.Locator(
      ".action-approval"
    );
    await Expect(
      approval
    ).ToBeVisibleAsync();
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
        "[data-event-type=\"action.planning-retry\"]"
      )
    ).ToHaveCountAsync(
      1
    );
    var recoveredApproval = Page.Locator(
      ".action-approval"
    ).Last;
    await Expect(
      recoveredApproval
    ).ToBeVisibleAsync();
    await recoveredApproval.GetByRole(
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
        "[data-event-type=\"action.validation-error\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    Assert.AreEqual(
      "recovered after premature prose",
      await File.ReadAllTextAsync(
        Path.Combine(
          _environment.WorkspaceDirectory,
          "hello.txt"
        )
      )
    );
    await Expect(
      Page.Locator(
        ".execution-plan .plan-step"
      )
    ).ToHaveCountAsync(
      2
    );
    await Expect(
      Page.Locator(
        ".execution-plan .plan-step.completed"
      )
    ).ToHaveCountAsync(
      2
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
        "[data-event-type=\"action.execution-error\"]"
      )
    ).ToContainTextAsync(
      "could not be started"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-started\"]"
      )
    ).ToContainTextAsync(
      "alpha:latest"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-guidance-prepared\"]"
      )
    ).ToContainTextAsync(
      "materially different strategy"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.recovery-planning\"]"
      )
    ).ToContainTextAsync(
      "returned to the active coordinator"
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
      "Concluído"
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
              "RESIDENT_STRATEGY_SUPERVISION",
              StringComparison.Ordinal
            ) && message.Content.Contains(
              "do not recreate the project",
              StringComparison.OrdinalIgnoreCase
            )
          )
      )
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RepeatedAutomaticSpecialistStrategyIsRejectedAfterTwoAttempts()
  {
    await Page.GotoAsync(
      "/"
    );
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute recover failed process unchanged strategy"
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
        Name = "Aprovar",
        Exact = true
      }
    ).ClickAsync();

    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-guidance-unchanged\"]"
      )
    ).ToContainTextAsync(
      "repeated the previous strategy twice"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"agent.execution-recovery-guidance-prepared\"]"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.output\"]"
      )
    ).ToContainTextAsync(
      "list_files"
    );
    Assert.HasCount(
      3,
      _environment.FakeOllama.Requests.Where(
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
        Name = "Workspace confiável"
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
      "histórico desativado"
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
    await Page.Locator(
      "#model-lock"
    ).CheckAsync();
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
        Name = "Salvar workspace"
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
      "ask"
    );
    await Expect(
      Page.Locator(
        "#model-lock"
      )
    ).Not.ToBeCheckedAsync();

    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync(
      "Renamed project"
    );
    await Page.Locator(
      ".workspace-profile-entry.active"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Renomear"
      }
    ).ClickAsync();
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
        Name = "Salvar workspace"
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
          Name = "Aprovar",
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
      "Enviar"
    );
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HistoryOptInPersistsButRestartRequiresExplicitResume()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#workspace-history-enabled"
    ).CheckAsync();
    await Expect(
      Page.Locator(
        "#history-usage"
      )
    ).ToContainTextAsync(
      "histórico ativo"
    );
    await Page.Locator(
      "#close-workspace"
    ).ClickAsync();
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
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Retomar"
      }
    ).ClickAsync();
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
      "ask"
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
  public async Task RestartMarksRunningPersistentExecutionInterrupted()
  {
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#workspace-history-enabled"
    ).CheckAsync();
    await Expect(
      Page.Locator(
        "#history-usage"
      )
    ).ToContainTextAsync(
      "histórico ativo"
    );
    await Page.Locator(
      "#close-workspace"
    ).ClickAsync();
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
          Name = "Aprovar",
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
    await Expect(
      Page.Locator(
        "#recent-sessions .session-interrupted"
      )
    ).ToContainTextAsync(
      "interrompida"
    );
    await Page.Locator(
      "#recent-sessions .session-entry"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Retomar"
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        ".message.assistant"
      ).Last
    ).ToContainTextAsync(
      "Nenhum processo ou aprovação pendente foi retomado"
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

    await _environment.RestartApplicationAsync();
    await Page.GotoAsync(
      "/"
    );
    await Page.Locator(
      "#session-history"
    ).EvaluateAsync(
      "element => element.open = true"
    );
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
    await Page.Locator(
      "#recent-sessions .session-entry"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Retomar"
      }
    ).ClickAsync();
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Revisar alterações concluídas"
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
  public async Task RetentionLimitRequiresRemovingAnOlderSession()
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
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "First retained conversation."
    );
    await Page.Locator(
      "#new-conversation"
    ).ClickAsync();
    await SendMessageAsync(
      "Second conversation beyond the retention limit."
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"history-retention-limit-reached\"]"
      )
    ).ToHaveCountAsync(
      1
    );
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
      "First retained conversation.",
      document.RootElement.GetProperty(
        "recent"
      )[0].GetProperty(
        "title"
      ).GetString()
    );
  }

  private static async Task<string> ActiveWorkspaceIdAsync()
  {
    using var response = await _environment.HttpClient.GetAsync(
      "api/workspaces"
    );
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(
      await response.Content.ReadAsStringAsync()
    );
    return document.RootElement.GetProperty(
      "activeWorkspaceId"
    ).GetString()!;
  }

  private static async Task RunGitAsync(
    params string[] arguments
  )
  {
    _ = await RunGitTextAsync(
      _environment.WorkspaceDirectory,
      arguments
    );
  }

  private static async Task<string> RunGitTextAsync(
    string workingDirectory,
    params string[] arguments
  )
  {
    var result = await RunGitAllowFailureAsync(
      workingDirectory,
      arguments
    );
    Assert.AreEqual(
      0,
      result.ExitCode,
      result.Error
    );
    return result.Output;
  }

  private static async Task<(
    int ExitCode,
    string Output,
    string Error
  )> RunGitAllowFailureAsync(
    string workingDirectory,
    params string[] arguments
  )
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = "git",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };

    foreach (var argument in arguments)
    {
      process.StartInfo.ArgumentList.Add(
        argument
      );
    }

    Assert.IsTrue(
      process.Start()
    );
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (
      process.ExitCode,
      (
        await outputTask
      ).Trim(),
      (
        await errorTask
      ).Trim()
    );
  }

  private static async Task<string> InitializeDeliveryRepositoryAsync()
  {
    await RunGitAsync(
      "init",
      "-b",
      "main"
    );
    await RunGitAsync(
      "config",
      "user.name",
      "Agentic Router E2E"
    );
    await RunGitAsync(
      "config",
      "user.email",
      "agentic-router-e2e@example.invalid"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "baseline.txt"
      ),
      "baseline"
    );
    await File.WriteAllTextAsync(
      Path.Combine(
        _environment.WorkspaceDirectory,
        "preexisting.txt"
      ),
      "user baseline"
    );
    await RunGitAsync(
      "add",
      "--",
      "baseline.txt",
      "preexisting.txt"
    );
    await RunGitAsync(
      "commit",
      "-m",
      "test baseline"
    );
    var remote = _environment.CreateWorkspaceDirectory(
      $"delivery-remote-{Guid.NewGuid():N}.git"
    );
    _ = await RunGitTextAsync(
      _environment.WorkspaceDirectory,
      "init",
      "--bare",
      remote
    );
    await RunGitAsync(
      "remote",
      "add",
      "origin",
      remote
    );
    await RunGitAsync(
      "push",
      "-u",
      "origin",
      "main"
    );
    _ = await RunGitTextAsync(
      remote,
      "symbolic-ref",
      "HEAD",
      "refs/heads/main"
    );
    return remote;
  }

  private async Task StageDeliveryAsync(
    ILocator panel
  )
  {
    await ApproveDeliveryOperationAsync(
      panel,
      "Stage selected"
    );
  }

  private async Task ApproveDeliveryOperationAsync(
    ILocator panel,
    string operation,
    bool expectSuccess = true
  )
  {
    await panel.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = operation,
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

    if (expectSuccess)
    {
      var operationCode = operation switch
      {
        "Stage selected" => "stage",
        "Unstage selected" => "unstage",
        "Create commit" => "commit",
        "Create annotated tag" => "tag",
        "Push current branch" => "push-branch",
        "Push exact tag" => "push-tag",
        _ => operation
      };
      await Expect(
        Page.Locator(
          "#undo-status"
        )
      ).ToContainTextAsync(
        $"Git {operationCode} completed"
      );
    }
  }

  private async Task SetExecuteModeAsync(
    string approvalPolicy
  )
  {
    await Page.Locator(
      "[data-mode=\"execute\"]"
    ).ClickAsync();
    await Page.Locator(
      "#approval-policy"
    ).SelectOptionAsync(
      approvalPolicy
    );
  }

  private async Task OpenSettingsAsync()
  {
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Configurações",
        Exact = true
      }
    ).ClickAsync();
  }

  private async Task<double> RemainingScrollAsync()
  {
    return await Page.Locator(
      "#messages"
    ).EvaluateAsync<double>(
      "messages => messages.scrollHeight - messages.scrollTop - messages.clientHeight"
    );
  }

  private static async Task WaitUntilAsync(
    Func<bool> condition,
    TimeSpan timeout
  )
  {
    using var cancellation = new CancellationTokenSource(
      timeout
    );

    while (!condition())
    {
      await Task.Delay(
        50,
        cancellation.Token
      );
    }
  }
  private async Task StartMessageAsync(
    string message
  )
  {
    await Page.Locator(
      "#message-input"
    ).FillAsync(
      message
    );
    await Page.Locator(
      "#message-input"
    ).PressAsync(
      "Enter"
    );
  }

  private async Task SendMessageAsync(
    string message
  )
  {
    await StartMessageAsync(
      message
    );
    await Expect(
      Page.Locator(
        ".message.assistant .activity"
      ).Last
    ).Not.ToHaveAttributeAsync(
      "open",
      string.Empty
    );
  }
}
