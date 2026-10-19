using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class ComposerFilesEndToEndTests : ChatEndToEndTestBase<ComposerFilesEndToEndTests>
{
  private async Task WriteAsync(string path, string content = "PRIVATE-FILE-CONTENT")
  {
    var full = Path.Combine(_environment.WorkspaceDirectory, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, content);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task KeyboardSearchInsertsInlineBadgeAndSendsRelativePathWithoutContents()
  {
    await WriteAsync("mention-a/scene.md");
    await WriteAsync("mention-b/scene.md");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    var input = Page.Locator("#message-input");
    await input.FillAsync("Please review @scene");
    await Expect(Page.Locator("#composer-file-list [role=option]")).ToHaveCountAsync(2);
    await input.PressAsync("ArrowDown");
    await input.PressAsync("Enter");
    await Expect(Page.Locator(".composer-file-badge")).ToHaveAttributeAsync("title", "mention-b/scene.md");
    await Expect(Page.Locator(".composer-file-badge > span")).ToHaveTextAsync("scene.md");
    Assert.HasCount(0, _environment.FakeOllama.Requests);
    await input.PressSequentiallyAsync(" carefully.");
    await Expect(input).ToHaveJSPropertyAsync("value", "Please review `mention-b/scene.md`  carefully.");
    await ScreenshotAsync("composer-file-badge.png");
    var response = await Page.RunAndWaitForResponseAsync(() => input.PressAsync("Enter"),
      response => response.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal));
    await response.FinishedAsync();
    var prompt = _environment.FakeOllama.Requests.Last(request => request.Model == "alpha:latest").Messages.Last().Content;
    Assert.Contains("`mention-b/scene.md`", prompt);
    Assert.DoesNotContain("PRIVATE-FILE-CONTENT", prompt);
    await Expect(Page.Locator(".composer-file-badge")).ToHaveCountAsync(0);
    await Expect(input).ToHaveJSPropertyAsync("value", "");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task MouseSelectionUndoRedoRemovalAndMultilinePastePreservePlainText()
  {
    await WriteAsync("mention-unicode/Cena `final` & ação.md");
    await Page.GotoAsync("/");
    var input = Page.Locator("#message-input");
    await input.FillAsync("@mention-unicode/");
    await Page.Locator("#composer-file-list [role=option]").ClickAsync();
    await Expect(input).ToHaveJSPropertyAsync("value", "``mention-unicode/Cena `final` & ação.md`` ");
    await input.PressAsync("Control+z");
    await Expect(Page.Locator(".composer-file-badge")).ToHaveCountAsync(0);
    await input.PressAsync("Control+y");
    await Expect(Page.Locator(".composer-file-badge")).ToHaveCountAsync(1);
    await Page.Locator(".composer-file-remove").ClickAsync();
    await Expect(Page.Locator(".composer-file-badge")).ToHaveCountAsync(0);
    await input.FillAsync("First line");
    await input.PressAsync("Shift+Enter");
    await input.PressSequentiallyAsync("Second line");
    await Expect(input).ToHaveJSPropertyAsync("value", "First line\nSecond line");
    await input.EvaluateAsync("""
      el => { const data = new DataTransfer(); data.setData('text/plain', '\n<script>literal</script>');
        data.setData('text/html', '<img src=x onerror="window.badPaste=true">');
        el.dispatchEvent(new ClipboardEvent('paste', { bubbles:true, cancelable:true, clipboardData:data })); }
      """);
    await Expect(input).ToHaveJSPropertyAsync("value", "First line\nSecond line\n<script>literal</script>");
    await Expect(input.Locator("script, img")).ToHaveCountAsync(0);
    Assert.HasCount(0, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task EmailEscapeEmptySearchAndCompositionDoNotSendAccidentally()
  {
    await Page.GotoAsync("/");
    var input = Page.Locator("#message-input");
    await input.FillAsync("name@example.com");
    await Expect(Page.Locator("#composer-file-picker")).ToBeHiddenAsync();
    await input.FillAsync("@no-such-file-unique");
    await Expect(Page.Locator("#composer-file-status")).ToHaveTextAsync("No matching project files.");
    await input.PressAsync("Enter");
    Assert.HasCount(0, _environment.FakeOllama.Requests);
    await input.FillAsync("@");
    await Expect(Page.Locator("#composer-file-picker")).ToBeVisibleAsync();
    await input.PressAsync("Escape");
    await Expect(Page.Locator("#composer-file-picker")).ToBeHiddenAsync();
    await Expect(input).ToHaveJSPropertyAsync("value", "@");
    await input.EvaluateAsync("el => el.dispatchEvent(new KeyboardEvent('keydown', { key:'Enter', bubbles:true, cancelable:true, isComposing:true }))");
    Assert.HasCount(0, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FileSearchIsProjectScopedSupportsAllTypesAndRejectsTraversal()
  {
    await WriteAsync("mention-api/source.cs");
    await WriteAsync("mention-api/image.png");
    await WriteAsync(".git/mention-secret.txt");
    var id = await ActiveWorkspaceIdAsync();
    var result = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/workspace/files?workspaceId={id}&query=mention-api/"))!;
    Assert.HasCount(2, result["files"]!.AsArray());
    foreach (var query in new[] { "../", "C:/", "folder/../" })
    {
      using var denied = await _environment.HttpClient.GetAsync($"api/workspace/files?workspaceId={id}&query={Uri.EscapeDataString(query)}");
      Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
    }
    var protectedFiles = await _environment.HttpClient.GetStringAsync($"api/workspace/files?workspaceId={id}&query=mention-secret");
    Assert.DoesNotContain("mention-secret.txt", protectedFiles);
    var other = _environment.CreateWorkspaceDirectory("composer-other-project");
    await File.WriteAllTextAsync(Path.Combine(other, "other-only.txt"), "Other project");
    using var created = await _environment.HttpClient.PostAsJsonAsync("api/workspaces", new { name = "Mention other", path = other });
    var otherId = (await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
    using var activate = await _environment.HttpClient.PostAsync($"api/workspaces/{otherId}/activate", null);
    activate.EnsureSuccessStatusCode();
    using var stale = await _environment.HttpClient.GetAsync($"api/workspace/files?workspaceId={id}");
    Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
    await Page.GotoAsync("/");
    await Page.Locator("#message-input").FillAsync("@other-only");
    await Expect(Page.Locator("#composer-file-list [role=option]")).ToHaveCountAsync(1);
    await Expect(Page.Locator("#composer-file-list")).ToContainTextAsync("other-only.txt");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DuplicateNamesRemainReadableOnNarrowScreensAndCopyUsesPaths()
  {
    await WriteAsync("mention-mobile/a/story.md");
    await WriteAsync("mention-mobile/b/story.md");
    await Page.SetViewportSizeAsync(390, 844);
    await Page.GotoAsync("/");
    var input = Page.Locator("#message-input");
    await input.FillAsync("Compare @mention-mobile");
    await Expect(Page.Locator("#composer-file-list [role=option]")).ToHaveCountAsync(2);
    await ScreenshotAsync("composer-file-search-mobile.png");
    await input.PressAsync("Enter");
    await input.PressSequentiallyAsync("and @mention-mobile/b");
    await Expect(Page.Locator("#composer-file-list [role=option]")).ToHaveCountAsync(1);
    await input.PressAsync("Tab");
    await Expect(Page.Locator(".composer-file-badge")).ToHaveCountAsync(2);
    await Expect(input).ToHaveJSPropertyAsync("value", "Compare `mention-mobile/a/story.md` and `mention-mobile/b/story.md` ");
    var copied = await input.EvaluateAsync<string>("""
      el => { const range=document.createRange(); range.selectNodeContents(el); getSelection().removeAllRanges(); getSelection().addRange(range);
        const data = new DataTransfer(); el.dispatchEvent(new ClipboardEvent('copy', { bubbles:true, cancelable:true, clipboardData:data }));
        return data.getData('text/plain'); }
      """);
    Assert.Contains("`mention-mobile/a/story.md`", copied);
    Assert.Contains("`mention-mobile/b/story.md`", copied);
    Assert.IsTrue(await input.EvaluateAsync<bool>("el => el.getBoundingClientRect().right <= innerWidth"));
    await input.PressAsync("End");
    await ScreenshotAsync("composer-file-badges-mobile.png");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ReferenceInMiddlePreservesSurroundingTextAndCanBeDeletedWithKeyboard()
  {
    await WriteAsync("mention-middle/chapter.md");
    await Page.GotoAsync("/");
    var input = Page.Locator("#message-input");
    await input.FillAsync("Before  after");
    await input.PressAsync("Home");
    for (var i = 0; i < 7; i++) await input.PressAsync("ArrowRight");
    await input.PressSequentiallyAsync("@mention-middle/");
    await Expect(Page.Locator("#composer-file-list [role=option]")).ToHaveCountAsync(1);
    await input.PressAsync("Enter");
    await Expect(input).ToHaveJSPropertyAsync("value", "Before `mention-middle/chapter.md`  after");
    await input.PressAsync("Backspace");
    await input.PressAsync("Backspace");
    await Expect(Page.Locator(".composer-file-badge")).ToHaveCountAsync(0);
    await Expect(input).ToHaveJSPropertyAsync("value", "Before  after");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SentReferenceIsRestoredAsBadgeByCurrentRewindFlow()
  {
    await WriteAsync("mention-restore/outline.md");
    await Page.GotoAsync("/");
    var input = Page.Locator("#message-input");
    await input.FillAsync("Review @mention-restore/");
    await Page.Locator("#composer-file-list [role=option]").ClickAsync();
    var response = await Page.RunAndWaitForResponseAsync(() => input.PressAsync("Enter"),
      response => response.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal));
    await response.FinishedAsync();
    await Page.GetByRole(AriaRole.Button, new() { Name = "Edit message", Exact = true }).ClickAsync();
    await Expect(input).ToHaveJSPropertyAsync("value", "Review `mention-restore/outline.md`");
    await Expect(Page.Locator(".composer-file-badge")).ToHaveCountAsync(1);
    await Expect(input).ToHaveAttributeAsync("contenteditable", "true");
    await Expect(Page.Locator(".message.user")).ToHaveCountAsync(0);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ReferenceSurvivesMessageQueueAndIsSentAfterCurrentTurn()
  {
    await WriteAsync("mention-queue/story.md");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await StartMessageAsync("browser message buffer source");
    var input = Page.Locator("#message-input");
    await input.FillAsync("Next review @mention-queue/");
    await Page.Locator("#composer-file-list [role=option]").ClickAsync();
    await input.PressAsync("Enter");
    await Expect(Page.Locator(".message-buffer-item")).ToContainTextAsync("mention-queue/story.md");
    await Expect(Page.Locator(".message.assistant")).ToHaveCountAsync(2, new() { Timeout = 15_000 });
    await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true");
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request => request.Messages.Last().Content.Contains("`mention-queue/story.md`", StringComparison.Ordinal)));
    await Expect(Page.Locator("#message-buffer")).ToBeHiddenAsync();
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SearchDoesNotFollowExternalDirectoryLinksAndCanRefinePartialResults()
  {
    var outside = _environment.CreateWorkspaceDirectory("composer-outside");
    await File.WriteAllTextAsync(Path.Combine(outside, "external-reference.md"), "Outside");
    var link = Path.Combine(_environment.WorkspaceDirectory, "mention-link");
    if (OperatingSystem.IsWindows())
    {
      using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
      {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        ArgumentList = { "/c", "mklink", "/J", link, outside }
      })!;
      await process.WaitForExitAsync();
      Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
    }
    else Directory.CreateSymbolicLink(link, outside);
    try
    {
      var id = await ActiveWorkspaceIdAsync();
      foreach (var query in new[] { "external-reference", "mention-link/" })
      {
        var response = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/workspace/files?workspaceId={id}&query={query}"))!;
        Assert.HasCount(0, response["files"]!.AsArray());
      }
      for (var i = 0; i < 55; i++) await WriteAsync($"mention-many/file-{i:D2}.md");
      await Page.GotoAsync("/");
      await Page.Locator("#message-input").FillAsync("@mention-many/");
      await Expect(Page.Locator("#composer-file-list [role=option]")).ToHaveCountAsync(50);
      await Expect(Page.Locator("#composer-file-status")).ToContainTextAsync("Refine by folder/name");
      await Page.Locator("#message-input").PressSequentiallyAsync("file-54");
      await Expect(Page.Locator("#composer-file-list [role=option]")).ToHaveCountAsync(1);
      await Expect(Page.Locator("#composer-file-list")).ToContainTextAsync("file-54.md");
    }
    finally { Directory.Delete(link); }
  }

  private async Task ScreenshotAsync(string name)
  {
    var path = Path.Combine(_environment.RepositoryRoot, ".artifacts", "rpg-editor", "visual-review", name);
    await Page.ScreenshotAsync(new() { Path = path, FullPage = true });
    TestContext.AddResultFile(path);
  }
}
