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
      "v0.5.0"
    );
    await Expect(
      Page.Locator(
        "#model-selector option"
      )
    ).ToHaveCountAsync(
      7
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
  public async Task NewConversationClearsMessagesAndModelLockButPreservesSettings()
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
        "#model-selector"
      )
    ).ToHaveValueAsync(
      "auto"
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
  public async Task ConfirmedNewConversationCancelsActiveRequest()
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
    Page.Dialog += async (
      _,
      dialog
    ) => await dialog.AcceptAsync();
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
        "#empty-state"
      )
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".message"
      )
    ).ToHaveCountAsync(
      0
    );
    await Expect(
      Page.Locator(
        "#send-button"
      )
    ).ToHaveTextAsync(
      "Enviar"
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
        ".activity summary"
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

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task TrustedWorkspaceModalValidatesPersistsAndClearsPath()
  {
    await Page.GotoAsync(
      "/"
    );
    await Expect(
      Page.Locator(
        "#workspace-badge"
      )
    ).ToHaveTextAsync(
      "Configurado"
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
        Name = "Salvar",
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
      "#trusted-workspace-path"
    ).FillAsync(
      _environment.WorkspaceDirectory
    );
    await Page.Locator(
      "#workspace-dialog"
    ).GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Salvar",
        Exact = true
      }
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#workspace-dialog"
      )
    ).ToBeHiddenAsync();
    await Page.ReloadAsync();
    await Expect(
      Page.Locator(
        "#workspace-path"
      )
    ).ToHaveTextAsync(
      _environment.WorkspaceDirectory
    );
    await Page.Locator(
      "#open-workspace"
    ).ClickAsync();
    await Page.Locator(
      "#clear-workspace"
    ).ClickAsync();
    await Expect(
      Page.Locator(
        "#workspace-badge"
      )
    ).ToHaveTextAsync(
      "Não configurado"
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
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".activity summary"
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
        "[data-event-type=\"agent.tooling-confirmed\"]"
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
      "attempt 2 of 3 failed"
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
      4,
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
      "Tool 'unknown_tool' is not available"
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
  public async Task InvalidPlannerResponseFailsOnlyAfterThreeAttempts()
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
        "[data-event-type=\"action.validation-error\"]"
      )
    ).ToContainTextAsync(
      "failed after 3 attempts"
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
  public async Task RejectedActionDoesNotWriteFile()
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
    ).ToBeVisibleAsync();
    await Expect(
      Page.Locator(
        ".activity summary"
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
        "[data-event-type=\"action.validation-error\"]"
      )
    ).ToContainTextAsync(
      "outside the trusted workspace"
    );
    await Expect(
      Page.Locator(
        ".activity summary"
      )
    ).ToContainTextAsync(
      "Falhou"
    );
    await Page.GetByRole(
      AriaRole.Button,
      new()
      {
        Name = "Nova conversa",
        Exact = true
      }
    ).ClickAsync();
    await SetExecuteModeAsync(
      "auto"
    );
    await StartMessageAsync(
      "execute destructive process"
    );
    await Expect(
      Page.Locator(
        "[data-event-type=\"action.validation-error\"]"
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
      "alpha:latest"
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
        ".activity summary"
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
