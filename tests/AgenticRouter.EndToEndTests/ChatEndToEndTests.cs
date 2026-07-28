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
          "v0.2.0"
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
    public async Task SavesRoutingSettingsAndRetainsUnavailableValues()
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

        var unavailable = _environment.BaselineSettings with
        {
            RouterModel = "saved-but-missing"
        };
        using var saveResponse = await _environment.PutSettingsAsync(
          unavailable
        );
        saveResponse.EnsureSuccessStatusCode();
        await Page.ReloadAsync();
        await OpenSettingsAsync();
        await Expect(
          Page.Locator(
            "#router-model"
          )
        ).ToHaveValueAsync(
          "saved-but-missing"
        );
        await Expect(
          Page.Locator(
            "#router-model option"
          ).Last
        ).ToHaveTextAsync(
          "saved-but-missing (indisponível)"
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
            "pre code"
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
