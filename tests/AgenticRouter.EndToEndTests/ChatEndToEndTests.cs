using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
  public async Task LoadsVersionModelsAndCleanGpuNames()
  {
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
  "v0.3.0"
    );
    await Expect(
      Page.Locator(
        "#model-selector option"
      )
    ).ToHaveCountAsync(
      5
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
      "[data-intention=\"documentation\"] .intention-prompt"
    ).FillAsync(
      "Persisted documentation prompt."
    );
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Salvar",
        Exact = true
      }
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
        "summary"
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
      "You write documentation.",
      requests[1].Messages[0].Content
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
  public async Task MissingRouterConfidenceIsReportedAsUnavailable()
  {
    await Page.GotoAsync(
      "/"
    );
    await SendMessageAsync(
      "documentation with missing confidence"
    );

    var classification = Page.Locator(
      "[data-event-type=\"router.classified\"]"
    );
    await Expect(
      classification
    ).ToContainTextAsync(
      "confidence unavailable"
    );
    await Expect(
      classification
    ).Not.ToContainTextAsync(
      "(0%)"
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
      "invalid confidence"
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
      "invalid JSON"
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
  public async Task UnavailableTargetFailsWithStageAndTrace()
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
    var answer = Page.Locator(
      ".assistant-answer"
    );
    await Expect(
      answer
    ).ToContainTextAsync(
      "not installed"
    );
    await Expect(
      answer
    ).ToContainTextAsync(
      "Referência:"
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
        ".activity summary"
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
        ".activity summary"
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
      2,
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
        ".activity summary"
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
        ".activity summary"
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
        ".activity summary"
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
