using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class StoryEditorEndToEndTests : ChatEndToEndTestBase<StoryEditorEndToEndTests>
{
  [TestMethod]
  [DataRow(true)]
  [DataRow(false)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task StorySelectionRemainsReviewableWhileTypingInstructions(bool view)
  {
    const string source = "# Cena\n\nO **mago antigo** entrou.\n\nO mago antigo voltou.";
    await OpenEditorAsync(source);
    if (view)
    {
      await Page.Locator("#story-mode-view").ClickAsync();
      await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
      await SelectViewPassageAsync("mago antigo", "mago antigo", 1);
    }
    else await Page.Locator("#story-document").EvaluateAsync("el => { const start = el.value.lastIndexOf('mago antigo'); el.focus(); el.setSelectionRange(start, start + 11); el.dispatchEvent(new Event('select')); }");
    await Expect(Page.Locator("#story-revise")).ToBeEnabledAsync();
    await Page.Locator("#story-instruction").ClickAsync();
    await Page.Locator("#story-instruction").PressSequentiallyAsync("Mantenha o significado.");
    Assert.AreEqual("mago antigo", await SelectedPassageAsync());
    if (view) Assert.IsTrue(await Page.EvaluateAsync<bool>("CSS.highlights.has('story-selection') && [...CSS.highlights.get('story-selection')][0].toString() === 'mago antigo'"));
    await SaveScreenshotAsync($"story-preserved-{(view ? "view" : "raw")}-selection.png");
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Expect(Page.Locator("#story-revise")).ToBeEnabledAsync();
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("mago antigo");
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("# Cena\n\nO **mago antigo** entrou.\n\nO mago antigo — revisado voltou.");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ViewContextMenuSelectsParagraphSpeechAndSceneWithoutNextSceneAnchor()
  {
    const string scene = "## Cena 6\n\nO **mago** entrou.\n\n> — Não volte.\n> Ainda há perigo.\n\n### Um detalhe\n\nA porta fechou.";
    const string source = "<a id=\"cena-6\"></a>\n\n" + scene + "\n\n<a id=\"cena-7\"></a>\n\n## Cena 7\n\nDepois.";
    await OpenEditorAsync(source);
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    await Page.Locator("#story-preview p").Filter(new() { HasText = "O mago entrou." }).ClickAsync(new() { Button = MouseButton.Right });
    await Page.Locator("#story-select-section").ClickAsync();
    Assert.AreEqual("O **mago** entrou.", await SelectedPassageAsync());
    await Page.Locator("#story-preview blockquote p").ClickAsync(new() { Button = MouseButton.Right });
    await Page.Locator("#story-select-section").ClickAsync();
    Assert.AreEqual("> — Não volte.\n> Ainda há perigo.", await SelectedPassageAsync());
    await Page.Locator("#story-preview").GetByRole(AriaRole.Heading, new() { Name = "Cena 6", Exact = true }).ClickAsync(new() { Button = MouseButton.Right });
    await SaveScreenshotAsync("story-view-section-menu.png");
    await Page.Locator("#story-select-section").ClickAsync();
    await Page.Locator("#story-instruction").FillAsync("Revise a cena inteira.");
    Assert.AreEqual(scene, await SelectedPassageAsync());
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync(scene);
    await Page.Locator("#story-discard").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ViewWordMenuUsesClickedOccurrenceForContextualSynonyms()
  {
    const string source = "O mago antigo entrou.\n\nO castelo antigo caiu.";
    await OpenEditorAsync(source);
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    var point = await Page.Locator("#story-preview p").Nth(1).EvaluateAsync<double[]>("""
      el => {
        const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
        while (walker.nextNode()) {
          const node = walker.currentNode, index = node.textContent.indexOf('antigo');
          if (index < 0) continue;
          const range = document.createRange(); range.setStart(node, index); range.setEnd(node, index + 6);
          const r = range.getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2];
        }
        throw new Error('Word missing');
      }
      """);
    await Page.Mouse.ClickAsync((float)point[0], (float)point[1], new() { Button = MouseButton.Right });
    await Expect(Page.Locator("#story-view-synonym")).ToHaveTextAsync("Find synonym for “antigo”");
    await SaveScreenshotAsync("story-view-synonym-menu.png");
    await Page.Locator("#story-view-synonym").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("O castelo antigo caiu.");
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago antigo entrou.\n\nO castelo ancestral caiu.");
  }

  [TestMethod]
  [DataRow(300, 1800)]
  [DataRow(3600, 3600)]
  [DataRow(-1, -1)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task EditorKeepsLocalModelWarmWithoutChangingSettingsOrNormalChat(int configured, int expected)
  {
    var settings = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    // Auto Chat resolves the intention's preferred model through the Primary runtime role.
    settings["ollamaRuntime"]!["roleDefaults"]!["primary"]!["keepAlive"] = configured;
    using var saved = await _environment.HttpClient.PutAsJsonAsync("api/settings", settings);
    saved.EnsureSuccessStatusCode();
    await OpenEditorAsync("O mago antigo entrou.");
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    Assert.AreEqual(expected, _environment.FakeOllama.Requests.Last(IsEditorRequest).KeepAlive);
    var after = (await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    Assert.AreEqual(configured, after["ollamaRuntime"]!["roleDefaults"]!["primary"]!["keepAlive"]!.GetValue<int>());
    await Page.Locator("#story-discard").ClickAsync();
    await Page.Locator("#close-story-editor").ClickAsync();
    await SendMessageAsync("Tell a story about a castle.");
    Assert.IsNull(_environment.FakeOllama.Requests.Last().KeepAlive);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task WorkspacePickerSavesExplicitlyPreservesEncodingAndRejectsExternalChanges()
  {
    var folder = Path.Combine(_environment.WorkspaceDirectory, "chapters");
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "cena.md");
    var encoding = new UTF8Encoding(true);
    await File.WriteAllTextAsync(path, "# Cena\r\n\r\nO mago antigo entrou.\r\n", encoding);
    await File.WriteAllTextAsync(Path.Combine(folder, "image.png"), "not an editable file");
    await OpenEditorAsync("");
    await Expect(Page.Locator("#story-file-input")).ToHaveCountAsync(0);
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Page.Locator("#story-open-file").ClickAsync();
    await Page.Locator("#story-files-list").GetByRole(AriaRole.Button, new() { Name = "Folder: chapters", Exact = true }).ClickAsync();
    await Expect(Page.Locator("#story-files-directory")).ToHaveTextAsync("chapters");
    await Expect(Page.Locator("#story-files-list")).Not.ToContainTextAsync("image.png");
    await Page.Locator("#story-files-filter").FillAsync("cena");
    await SaveScreenshotAsync("story-workspace-picker.png");
    await Page.Locator("#story-files-list").GetByRole(AriaRole.Button, new() { Name = "cena.md", Exact = true }).ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("# Cena\n\nO mago antigo entrou.\n");
    await Expect(Page.Locator("#story-save-file")).ToBeDisabledAsync();
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-apply").ClickAsync();
    Assert.AreEqual("# Cena\r\n\r\nO mago antigo entrou.\r\n", await File.ReadAllTextAsync(path));
    await Page.Locator("#story-save-file").ClickAsync();
    await Expect(Page.Locator("#story-file-source")).ToContainTextAsync("Saved in workspace");
    var saved = await File.ReadAllBytesAsync(path);
    Assert.IsTrue(saved.AsSpan().StartsWith(encoding.GetPreamble()));
    Assert.AreEqual("# Cena\r\n\r\nO mago antigo — revisado entrou.\r\n", await File.ReadAllTextAsync(path));
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-save-file")).ToBeEnabledAsync();
    await File.WriteAllTextAsync(path, "External edit.", encoding);
    await Page.Locator("#story-save-file").ClickAsync();
    await Expect(Page.Locator("#story-file-save-status")).ToContainTextAsync("changed outside this draft");
    Assert.IsTrue(await Page.Locator("#story-file-save-status").EvaluateAsync<bool>("el => { const rect = el.getBoundingClientRect(); return rect.top >= 0 && rect.bottom <= innerHeight; }"));
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("# Cena\n\nO mago antigo entrou.\n");
    Assert.AreEqual("External edit.", await File.ReadAllTextAsync(path));
    await SaveScreenshotAsync("story-workspace-conflict.png");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task WorkspaceFilesEnforcePathsTypesVersionsAndActiveProjectAtApiBoundary()
  {
    var id = await ActiveWorkspaceIdAsync();
    var path = Path.Combine(_environment.WorkspaceDirectory, "workspace-document.md");
    await File.WriteAllTextAsync(path, "Original.");
    using var opened = await _environment.HttpClient.GetAsync($"api/story-editor/file?workspaceId={id}&path=workspace-document.md");
    opened.EnsureSuccessStatusCode();
    var source = (await opened.Content.ReadFromJsonAsync<JsonObject>())!;
    foreach (var denied in new[] { "../escape.md", path, ".git/config.md", "script.cs", "workspace-document.md:secret.txt" })
    {
      using var response = await _environment.HttpClient.GetAsync($"api/story-editor/file?workspaceId={id}&path={Uri.EscapeDataString(denied)}");
      Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, denied);
      using var write = await _environment.HttpClient.PutAsJsonAsync("api/story-editor/file", new
      {
        workspaceId = id,
        path = denied,
        document = "No.",
        expectedVersion = source["version"]!.GetValue<string>()
      });
      Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode, denied);
    }
    var updates = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => _environment.HttpClient.PutAsJsonAsync("api/story-editor/file", new
    {
      workspaceId = id,
      path = "workspace-document.md",
      document = $"Version {index}.",
      expectedVersion = source["version"]!.GetValue<string>()
    })));
    try
    {
      Assert.HasCount(1, updates.Where(response => response.IsSuccessStatusCode).ToArray());
      Assert.HasCount(1, updates.Where(response => response.StatusCode == HttpStatusCode.Conflict).ToArray());
    }
    finally { foreach (var response in updates) response.Dispose(); }
    var committed = await File.ReadAllTextAsync(path);
    var otherPath = _environment.CreateWorkspaceDirectory("story-other-project");
    using var created = await _environment.HttpClient.PostAsJsonAsync("api/workspaces", new { name = "Other stories", path = otherPath });
    created.EnsureSuccessStatusCode();
    var other = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
    using var activated = await _environment.HttpClient.PostAsync($"api/workspaces/{other["id"]!.GetValue<string>()}/activate", null);
    activated.EnsureSuccessStatusCode();
    using var stale = await _environment.HttpClient.PutAsJsonAsync("api/story-editor/file", new
    {
      workspaceId = id,
      path = "workspace-document.md",
      document = "Wrong workspace.",
      expectedVersion = source["version"]!.GetValue<string>()
    });
    Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
    Assert.AreEqual(committed, await File.ReadAllTextAsync(path));
    Assert.IsFalse(File.Exists(Path.Combine(otherPath, "workspace-document.md")));
    using var oldList = await _environment.HttpClient.GetAsync($"api/story-editor/files?workspaceId={id}");
    Assert.AreEqual(HttpStatusCode.Conflict, oldList.StatusCode);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task WorkspaceSelectorDoesNotFollowDirectoryLinksOutsideTrustedRoot()
  {
    var outside = _environment.CreateWorkspaceDirectory("story-outside-link");
    await File.WriteAllTextAsync(Path.Combine(outside, "external.md"), "External original.");
    var link = Path.Combine(_environment.WorkspaceDirectory, "linked-stories");
    if (OperatingSystem.IsWindows())
    {
      using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
      {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
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
      var listing = await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/story-editor/files?workspaceId={id}");
      Assert.IsFalse(listing!["entries"]!.AsArray().Any(entry => entry!["name"]!.GetValue<string>() == "linked-stories"));
      using var read = await _environment.HttpClient.GetAsync($"api/story-editor/file?workspaceId={id}&path=linked-stories/external.md");
      Assert.AreEqual(HttpStatusCode.BadRequest, read.StatusCode);
      using var write = await _environment.HttpClient.PutAsJsonAsync("api/story-editor/file", new
      {
        workspaceId = id,
        path = "linked-stories/external.md",
        document = "No escape.",
        expectedVersion = "fake"
      });
      Assert.AreEqual(HttpStatusCode.BadRequest, write.StatusCode);
      Assert.AreEqual("External original.", await File.ReadAllTextAsync(Path.Combine(outside, "external.md")));
    }
    finally { Directory.Delete(link); }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task WorkspaceFileErrorsPreserveDraftAndPickerWorksAtNarrowWidths()
  {
    var folder = Path.Combine(_environment.WorkspaceDirectory, "invalid-stories");
    Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "binary.txt"), [0xff, 0xff]);
    var readOnly = Path.Combine(folder, "readonly.md");
    await File.WriteAllTextAsync(readOnly, "Read only.");
    File.SetAttributes(readOnly, FileAttributes.ReadOnly);
    try
    {
      await OpenEditorAsync("Preserve my draft.");
      await Page.Locator("#story-open-file").ClickAsync();
      await Page.Locator("#story-files-list").GetByRole(AriaRole.Button, new() { Name = "Folder: invalid-stories", Exact = true }).ClickAsync();
      await Expect(Page.Locator("#story-files-list")).Not.ToContainTextAsync("readonly.md");
      await Page.Locator("#story-files-list").GetByRole(AriaRole.Button, new() { Name = "binary.txt", Exact = true }).ClickAsync();
      await Expect(Page.Locator("#story-files-status")).ToContainTextAsync("not valid UTF-8");
      await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Preserve my draft.");
      foreach (var width in new[] { 1600, 390, 320 })
      {
        await Page.SetViewportSizeAsync(width, 900);
        Assert.IsTrue(await Page.Locator("#story-files-dialog").EvaluateAsync<bool>("el => { const r = el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && el.scrollWidth <= el.clientWidth + 1; }"));
        await SaveScreenshotAsync($"story-workspace-picker-{width}.png");
      }
      await Page.Keyboard.PressAsync("Escape");
      await Expect(Page.Locator("#story-open-file")).ToBeFocusedAsync();
    }
    finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task RewritePreservesContextAndOnlyAppliesApprovedSelection()
  {
    const string source = "Antes. O mago antigo entrou. Depois saiu.";
    await OpenEditorAsync(source);
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    await SelectViewPassageAsync("O mago", "entrou.");
    await Page.Locator("#story-rewrite").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("O mago antigo entrou.");
    await Expect(Page.Locator("#story-replacement")).ToHaveValueAsync("O velho mago cruzou a porta.");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    var prompt = _environment.FakeOllama.Requests.Last(IsEditorRequest).Messages.Last(message => message.Role == "user").Content;
    StringAssert.Contains(prompt, "Rewrite ONLY the selection");
    StringAssert.Contains(prompt, "preserving the same meaning");
    using var context = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
    Assert.AreEqual("Antes. ", context.RootElement.GetProperty("before").GetString());
    Assert.AreEqual(" Depois saiu.", context.RootElement.GetProperty("after").GetString());
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Antes. O velho mago cruzou a porta. Depois saiu.");
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task RepetitionsExcludeDistantOccurrencesAndDistanceIsAdjustable()
  {
    var source = "dragão viu DRAGÃO. " + string.Join(' ', Enumerable.Range(0, 60).Select(index => $"termo{index}")) + " dragão.";
    await OpenEditorAsync(source);
    await Page.Locator("#story-repetitions").ClickAsync();
    var results = Page.Locator("#story-repetition-results");
    await Expect(results.GetByRole(AriaRole.Button, new() { Name = "dragão · 2", Exact = true })).ToBeVisibleAsync();
    await Page.Locator("#story-repetition-distance").FillAsync("100");
    await Expect(results.GetByRole(AriaRole.Button, new() { Name = "dragão · 3", Exact = true })).ToBeVisibleAsync();
    await Page.Locator("#story-repetition-distance").FillAsync("1");
    await Expect(results).ToHaveTextAsync("No repeated words found with these filters.");
    await Page.Locator("#story-repetition-distance").FillAsync("2");
    await Expect(results.GetByRole(AriaRole.Button, new() { Name = "dragão · 2", Exact = true })).ToBeVisibleAsync();
    await Page.Locator("#story-repetition-distance").FillAsync("0");
    await Expect(results).ToContainTextAsync("Choose a distance from 1 to 500");
    Assert.HasCount(0, _environment.FakeOllama.Requests.Where(IsEditorRequest).ToArray());
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task WordContextMenuHighlightsAllAndIgnoresReversiblyWithoutChangingMarkdown()
  {
    const string source = "# Cena\n\nO **dragão** viu o DRAGÃO.\n\nUm dra**gão** dormiu. Dragões ficaram longe.";
    await Page.SetViewportSizeAsync(1600, 1000);
    await OpenEditorAsync(source);
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Page.Locator("#story-repetitions").ClickAsync();
    var word = Page.Locator("#story-repetition-results button[data-story-word='dragão']");
    await word.ClickAsync(new() { Button = MouseButton.Right });
    await Expect(Page.GetByRole(AriaRole.Menu, new() { Name = "Repeated word actions" })).ToBeVisibleAsync();
    await SaveScreenshotAsync("story-repetition-context-menu.png");
    await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Highlight all", Exact = true }).ClickAsync();
    await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    await Expect(Page.Locator("#story-highlight-status")).ToContainTextAsync("3 highlighted");
    await Expect(Page.Locator("#story-preview mark")).ToHaveCountAsync(4);
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    await SelectViewPassageAsync("DRAGÃO", "DRAGÃO");
    await Expect(Page.Locator("#story-revise")).ToBeEnabledAsync();
    Assert.AreEqual("DRAGÃO", await SelectedPassageAsync());
    await SaveScreenshotAsync("story-repetition-highlights.png");
    var download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual(source, await File.ReadAllTextAsync((await download.PathAsync())!));
    await word.PressAsync("Shift+F10");
    await Page.Keyboard.PressAsync("Escape");
    await Expect(Page.Locator("#story-word-menu")).ToBeHiddenAsync();
    await Expect(Page.Locator("#story-fullscreen")).ToBeVisibleAsync();
    await word.PressAsync("Shift+F10");
    await Page.Keyboard.PressAsync("ArrowDown");
    await Page.Keyboard.PressAsync("Enter");
    await Expect(word).ToHaveCountAsync(0);
    await Expect(Page.Locator("#story-preview mark")).ToHaveCountAsync(0);
    await Page.GetByRole(AriaRole.Button, new() { Name = "Restore dragão", Exact = true }).ClickAsync();
    await Expect(word).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    Assert.HasCount(0, _environment.FakeOllama.Requests.Where(IsEditorRequest).ToArray());
  }

  [TestMethod]
  [DataRow("# Cena\n\nEle voltou. O mago **antigo** entrou! Depois saiu.", "antigo", "O mago **antigo** entrou!")]
  [DataRow("O mago antigo entrou.", "antigo", "O mago antigo entrou.")]
  [DataRow("Uma voz perguntou: “O mago antigo voltou?”\n\nFim.", "antigo", "Uma voz perguntou: “O mago antigo voltou?”")]
  [DataRow("Antes. O mago antigo entrou. E saiu. Fim.", "antigo entrou. E saiu", "O mago antigo entrou. E saiu.")]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task SynonymsShowFullSentenceAndOnlyApplySelectedReplacement(string source, string selected, string sentence)
  {
    await OpenEditorAsync(source);
    await SelectPassageAsync(selected);
    await Page.Locator("#story-synonyms").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync(sentence);
    await Expect(Page.Locator("#story-original del")).ToHaveTextAsync(selected);
    await Expect(Page.Locator("#story-replacement-diff")).ToHaveTextAsync(sentence.Replace(selected, "ancestral", StringComparison.Ordinal));
    await Page.Locator("#story-alternatives button").Filter(new() { HasText = "vetusto" }).ClickAsync();
    await Expect(Page.Locator("#story-replacement-diff")).ToHaveTextAsync(sentence.Replace(selected, "vetusto", StringComparison.Ordinal));
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    await SaveScreenshotAsync("story-synonym-sentence.png");
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Expect(Page.Locator("#story-replacement")).ToHaveValueAsync("vetusto");
    await Expect(Page.Locator("#story-replacement-label")).ToHaveTextAsync("Replacement for selected text only");
    await Page.Locator("#story-replacement").FillAsync("velho");
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Expect(Page.Locator("#story-replacement-diff")).ToHaveTextAsync(sentence.Replace(selected, "velho", StringComparison.Ordinal));
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source.Replace(selected, "velho", StringComparison.Ordinal));
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
  }

  [TestMethod]
  [DataRow("revise")]
  [DataRow("synonyms")]
  [DataRow("agreement")]
  [DataRow("rewrite")]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ProposalShowsLoaderUntilModelCompletesAndSurvivesFullscreenChange(string operation)
  {
    await OpenEditorAsync("O mago antigo entrou.");
    await SelectPassageAsync("antigo");
    var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _environment.FakeOllama.BeforeChatResponse = request =>
    {
      if (!IsEditorRequest(request)) return Task.CompletedTask;
      arrived.TrySetResult();
      return release.Task;
    };
    try
    {
      await Page.Locator($"#story-{operation}").ClickAsync();
      await arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
      await Expect(Page.Locator("#story-loading")).ToBeVisibleAsync();
      await Expect(Page.Locator(".story-editor-actions > .story-editor-toolbar")).ToHaveAttributeAsync("aria-busy", "true");
      await Expect(Page.Locator("#story-cancel")).ToBeEnabledAsync();
      Assert.AreNotEqual("none", await Page.Locator("#story-loading .conversation-history-spinner").EvaluateAsync<string>("el => getComputedStyle(el).animationName"));
      await Page.Locator("#story-fullscreen-toggle").ClickAsync();
      await Expect(Page.Locator("#story-loading")).ToBeVisibleAsync();
      Assert.IsTrue(await Page.Locator("#story-loading").EvaluateAsync<bool>("el => { const rect = el.getBoundingClientRect(); return rect.top >= 0 && rect.bottom <= innerHeight; }"));
      await SaveScreenshotAsync("story-model-loading.png");
      await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago antigo entrou.");
      release.TrySetResult();
      await Expect(Page.Locator("#story-comparison")).ToBeVisibleAsync();
      await Expect(Page.Locator("#story-loading")).ToBeHiddenAsync();
      await Expect(Page.Locator(".story-editor-actions > .story-editor-toolbar")).ToHaveAttributeAsync("aria-busy", "false");
    }
    finally { release.TrySetResult(); _environment.FakeOllama.BeforeChatResponse = null; }
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task AgreementChecksSelectionAndRequiresExplicitApplyInRawAndView(bool view)
  {
    const string passage = "As menina estava cansado.";
    const string source = "# Cena\n\n" + passage + "\n\nO mago esperou.";
    await OpenEditorAsync(source);
    await Page.Locator("#story-document").PressAsync("ArrowRight");
    await Expect(Page.Locator("#story-agreement")).ToBeDisabledAsync();
    if (view)
    {
      await Page.Locator("#story-mode-view").ClickAsync();
      await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
      await SelectViewPassageAsync("As menina", "cansado.");
    }
    else await SelectPassageAsync(passage);
    await Page.Locator("#story-instruction").FillAsync("Preserve a voz do narrador.");
    await Page.Locator("#story-agreement").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync(passage);
    await Expect(Page.Locator("#story-replacement")).ToHaveValueAsync("As meninas estavam cansadas.");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    var request = _environment.FakeOllama.Requests.Last(IsEditorRequest);
    var prompt = request.Messages.Last(message => message.Role == "user").Content;
    StringAssert.Contains(prompt, "Check grammatical agreement ONLY");
    StringAssert.Contains(prompt, "Preserve a voz do narrador.");
    using var quoted = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
    Assert.AreEqual(passage, quoted.RootElement.GetProperty("selection").GetString());
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source.Replace(passage, "As meninas estavam cansadas.", StringComparison.Ordinal));
    if (view) await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    if (view)
    {
      await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
      await SelectViewPassageAsync("O mago", "esperou.");
    }
    else await SelectPassageAsync("O mago esperou.");
    await Page.Locator("#story-agreement").ClickAsync();
    await Expect(Page.Locator("#story-diff-note")).ToHaveTextAsync("No changes");
    await Page.Locator("#story-discard").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    await Expect(Page.Locator("#messages .message")).ToHaveCountAsync(0);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task RepeatedWordSynonymsUseChosenOccurrenceAndNeverReplaceAll()
  {
    const string source = "O mago antigo entrou. O castelo antigo caiu. O livro antigo ficou.";
    await OpenEditorAsync(source);
    await Page.Locator("#story-repetitions").ClickAsync();
    var word = Page.Locator("#story-repetition-results").GetByRole(AriaRole.Button, new() { Name = "antigo · 3", Exact = true });
    await word.ClickAsync();
    await word.ClickAsync();
    Assert.AreEqual(source.IndexOf("antigo caiu", StringComparison.Ordinal), await Page.Locator("#story-document").EvaluateAsync<int>("el => el.selectionStart"));
    Assert.HasCount(0, _environment.FakeOllama.Requests.Where(IsEditorRequest).ToArray());
    await SaveScreenshotAsync("story-repetition-synonyms.png");
    await Page.GetByRole(AriaRole.Button, new() { Name = "Find synonyms for antigo", Exact = true }).ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("O castelo antigo caiu.");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    await Page.Locator("#story-alternatives button").Filter(new() { HasText = "vetusto" }).ClickAsync();
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago antigo entrou. O castelo vetusto caiu. O livro antigo ficou.");
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    Assert.HasCount(1, _environment.FakeOllama.Requests.Where(IsEditorRequest).ToArray());
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task FullscreenPreservesViewSelectionProposalUndoAndExportAcrossReturnToSplit()
  {
    const string source = "# O último farol\n\nAs menina estava cansado.\n\nClara esperou diante da porta.";
    await Page.SetViewportSizeAsync(1600, 1000);
    await OpenEditorAsync(source);
    await Page.Locator("#story-file-name").FillAsync("farol.md");
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    await SelectViewPassageAsync("As menina", "cansado.");
    await Page.Locator("#story-instruction").FillAsync("Preserve a voz.");
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Expect(Page.Locator("#story-fullscreen")).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-mode-view")).ToHaveAttributeAsync("aria-pressed", "true");
    await Expect(Page.Locator("#story-agreement")).ToBeEnabledAsync();
    await Expect(Page.Locator("#story-instruction")).ToHaveValueAsync("Preserve a voz.");
    await Page.Locator("#story-agreement").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("As menina estava cansado.");
    await SaveScreenshotAsync("story-fullscreen-comparison.png");
    await Page.Keyboard.PressAsync("Escape");
    await Expect(Page.Locator("#story-comparison")).ToBeHiddenAsync();
    await Expect(Page.Locator("#story-fullscreen")).ToBeVisibleAsync();
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Expect(Page.Locator("#conversation-view > #story-editor")).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-review-proposal")).ToBeVisibleAsync();
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Page.Locator("#story-review-proposal").ClickAsync();
    await Page.Locator("#story-apply").ClickAsync();
    var expected = source.Replace("As menina estava cansado.", "As meninas estavam cansadas.", StringComparison.Ordinal);
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(expected);
    var download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual("farol.md", download.SuggestedFilename);
    Assert.AreEqual(expected, await File.ReadAllTextAsync((await download.PathAsync())!));
    await Page.Keyboard.PressAsync("Escape");
    await Expect(Page.Locator("#story-fullscreen")).ToBeHiddenAsync();
    await Expect(Page.Locator("#story-fullscreen-toggle")).ToBeFocusedAsync();
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    await Page.Locator("#story-fullscreen-toggle").ClickAsync();
    await Page.Locator("#close-story-editor").ClickAsync();
    await Expect(Page.Locator("#story-fullscreen")).ToBeHiddenAsync();
    await Expect(Page.Locator("#story-editor")).ToBeHiddenAsync();
    await Page.Locator("#open-story-editor").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task FullscreenFillsViewportWithoutOverflowAndKeepsRawSelection()
  {
    var errors = new List<string>();
    Page.PageError += (_, error) => errors.Add(error);
    await OpenEditorAsync("# O último farol\n\nA chuva apagava as pegadas na estrada. Clara parou diante do farol antigo, com a carta dobrada no bolso.\n\nLá dentro, uma luz atravessou a janela. O farol estava abandonado havia vinte anos — mas alguém acabara de acender a lâmpada.");
    await SelectPassageAsync("farol antigo");
    foreach (var width in new[] { 1600, 760, 390, 320 })
    {
      await Page.SetViewportSizeAsync(width, 900);
      await Page.Locator("#story-fullscreen-toggle").ClickAsync();
      Assert.IsTrue(await Page.Locator("#story-fullscreen").EvaluateAsync<bool>("""
        el => {
          const rect = el.getBoundingClientRect(), editor = document.getElementById('story-editor');
          return rect.x === 0 && rect.y === 0 && Math.abs(rect.width - innerWidth) < 1
            && Math.abs(rect.height - innerHeight) < 1 && editor.scrollWidth <= editor.clientWidth + 1;
        }
        """), $"Fullscreen dimensions at {width}px");
      Assert.AreEqual("farol antigo", await SelectedPassageAsync());
      await Expect(Page.Locator("#story-agreement")).ToBeEnabledAsync();
      await Page.Keyboard.PressAsync("Tab");
      Assert.IsTrue(await Page.EvaluateAsync<bool>("document.getElementById('story-fullscreen').contains(document.activeElement)"));
      await SaveScreenshotAsync($"story-fullscreen-{width}.png");
      await Page.Locator("#story-fullscreen-toggle").ClickAsync();
      await Expect(Page.Locator("#story-fullscreen")).ToBeHiddenAsync();
      Assert.AreEqual("farol antigo", await SelectedPassageAsync());
    }
    Assert.HasCount(0, errors);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task PreviewIndexNavigatesOnlyInsideEditorAndPreservesOriginalMarkdown()
  {
    var source = "# Sumário\n\n[Cena 6](#cena-6) · [Nome legado](#legado) · [Título](#epilogo) · [Ausente](#ausente) · [Inválido](#%ZZ)\n\n"
      + string.Concat(Enumerable.Repeat("Um parágrafo antes da cena.\n\n", 45))
      + "<a id=\"cena-6\"></a>\n\n## Cena 6 — A busca pelo Cálice\n\nJonas encontrou Serena.\n\n"
      + "<a name='legado'></a>\n\nOutro trecho.\n\n## Epílogo {#epilogo}\n\nFim.\n\n"
      + "```html\n<a id=\"falso\"></a>\n```\n\n<script>window.storyInjected=true</script>";
    await OpenEditorAsync(source);
    await Page.Locator("#story-mode-view").ClickAsync();
    var preview = Page.Locator("#story-preview");
    await Expect(preview).ToHaveAttributeAsync("aria-busy", "false");
    var initialUrl = Page.Url;
    var initialPages = Context.Pages.Count;
    var timeOrigin = await Page.EvaluateAsync<double>("performance.timeOrigin");
    await Expect(preview.Locator("[data-story-anchor='cena-6']")).ToHaveCountAsync(1);
    await Expect(preview.Locator("[data-story-anchor='falso']")).ToHaveCountAsync(0);
    await preview.GetByRole(AriaRole.Link, new() { Name = "Cena 6", Exact = true }).ClickAsync();
    await Expect(Page.Locator("#story-status")).ToContainTextAsync("Showing section: cena-6");
    Assert.IsTrue(await preview.EvaluateAsync<bool>("el => el.scrollTop > 100"));
    await SaveScreenshotAsync("story-local-index.png");
    foreach (var label in new[] { "Nome legado", "Título", "Ausente", "Inválido" })
      await preview.GetByRole(AriaRole.Link, new() { Name = label, Exact = true }).PressAsync("Enter");
    await Expect(Page.Locator("#story-status")).ToContainTextAsync("invalid anchor");
    Assert.AreEqual(initialUrl, Page.Url);
    Assert.HasCount(initialPages, Context.Pages);
    Assert.AreEqual(timeOrigin, await Page.EvaluateAsync<double>("performance.timeOrigin"));
    Assert.IsTrue(await Page.EvaluateAsync<bool>("window.storyInjected === undefined"));
    var download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual(source, await File.ReadAllTextAsync((await download.PathAsync())!));
    await Page.Locator("#story-mode-raw").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
  }

  [TestMethod]
  [DataRow("Jonas", "voltou.", "**Jonas** voltou.")]
  [DataRow("Jonas", "carta.", "**Jonas** voltou.\n\nSerena trouxe o cálice &amp; a carta.")]
  [DataRow("Serena", "Serena", "Serena")]
  [DataRow("&", "&", "&amp;")]
  [DataRow("cálice", "cálice", "cálice")]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ViewSelectionEnablesReviewAfterEditingInstructionsAndAppliesExactSource(string startText, string endText, string original)
  {
    const string source = "# Cena 6\n\n**Jonas** voltou.\n\nSerena trouxe o cálice &amp; a carta.\n\nSerena trouxe o cálice &amp; a carta.\n\n## Cena 7\n\nNada mudou aqui.";
    await OpenEditorAsync(source);
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    await SelectViewPassageAsync(startText, endText);
    await Expect(Page.Locator("#story-revise")).ToBeEnabledAsync();
    await Page.Locator("#story-instruction").FillAsync("Melhore a fluidez, preservando a motivação de Serena.");
    await Expect(Page.Locator("#story-revise")).ToBeEnabledAsync();
    await SaveScreenshotAsync("story-view-selection.png");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync(original);
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    const string replacement = "Trecho aprovado.";
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Page.Locator("#story-replacement").FillAsync(replacement);
    await Page.Locator("#story-apply").ClickAsync();
    var start = source.IndexOf(original, StringComparison.Ordinal);
    var expected = source[..start] + replacement + source[(start + original.Length)..];
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(expected);
    var download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual(expected, await File.ReadAllTextAsync((await download.PathAsync())!));
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    await Expect(Page.Locator("#story-preview")).ToHaveAttributeAsync("aria-busy", "false");
    await SelectViewPassageAsync("cálice", "cálice", occurrence: 1);
    await Page.Locator("#story-synonyms").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("Serena trouxe o cálice &amp; a carta.");
    await Expect(Page.Locator("#story-alternatives button")).ToHaveCountAsync(2);
    await Page.Locator("#story-apply").ClickAsync();
    var second = source.LastIndexOf("cálice", StringComparison.Ordinal);
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source[..second] + "ancestral" + source[(second + "cálice".Length)..]);
  }

  private async Task SelectViewPassageAsync(string startText, string endText, int occurrence = 0)
  {
    await Page.Locator("#story-preview").EvaluateAsync("""
      (element, { startText, endText, occurrence }) => {
        const nodes = [], walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        let text = '';
        while (walker.nextNode()) {
          nodes.push({ node: walker.currentNode, start: text.length });
          text += walker.currentNode.textContent;
        }
        let start = -1;
        for (let i = 0; i <= occurrence; i++) start = text.indexOf(startText, start + 1);
        const end = text.indexOf(endText, start) + endText.length;
        if (start < 0 || end <= start) throw new Error('Fixture passage not found');
        const first = nodes.find(item => item.start <= start && item.start + item.node.length > start);
        const last = nodes.find(item => item.start < end && item.start + item.node.length >= end);
        const range = document.createRange();
        range.setStart(first.node, start - first.start);
        range.setEnd(last.node, end - last.start);
        element.focus();
        window.getSelection().removeAllRanges();
        window.getSelection().addRange(range);
      }
      """, new { startText, endText, occurrence });
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ReviewHighlightsChangedWordsAndUpdatesAfterEditingWithoutChangingTheDraft()
  {
    const string original = "O dragão antigo voltou.\nClara esperou.";
    await OpenEditorAsync(original);
    await SelectPassageAsync(original);
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-replacement-diff ins")).ToContainTextAsync("revisado");
    await Page.Locator("#story-edit-proposal").ClickAsync();
    const string replacement = "O dragão ancestral voltou!\nClara esperou.";
    await Page.Locator("#story-replacement").FillAsync(replacement);
    await Page.Locator("#story-edit-proposal").ClickAsync();
    Assert.AreEqual(original, await Page.Locator("#story-original").TextContentAsync());
    Assert.AreEqual(replacement, await Page.Locator("#story-replacement-diff").TextContentAsync());
    Assert.AreEqual("antigo.", string.Concat(await Page.Locator("#story-original del").AllTextContentsAsync()));
    Assert.AreEqual("ancestral!", string.Concat(await Page.Locator("#story-replacement-diff ins").AllTextContentsAsync()));
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(original);
    await SaveScreenshotAsync("story-word-diff.png");
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Page.Locator("#story-replacement").FillAsync(original);
    await Expect(Page.Locator("#story-diff-note")).ToHaveTextAsync("No changes");
    await Expect(Page.Locator("#story-original del, #story-replacement-diff ins")).ToHaveCountAsync(0);
    await Page.Locator("#story-replacement").FillAsync("<img src=x onerror=alert(1)>\n" + original);
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Expect(Page.Locator("#story-replacement-diff img, #story-replacement-diff script")).ToHaveCountAsync(0);
    Assert.AreEqual("<img src=x onerror=alert(1)>\n" + original, await Page.Locator("#story-replacement-diff").TextContentAsync());
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("<img src=x onerror=alert(1)>\n" + original);
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(original);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ComparisonPairsChangesAndSynchronizesBothScrollDirectionsAndEditMode()
  {
    var tail = string.Concat(Enumerable.Range(1, 70).Select(index => $"\n\nParágrafo {index}. Clara seguiu o caminho até a torre, observando a floresta ao redor."));
    var original = "O mago antigo caminhou lentamente.\n\nA floresta escura permaneceu silenciosa." + tail;
    var replacement = "O mago ancestral caminhou depressa.\n\nA floresta sombria permaneceu quieta." + tail;
    await OpenEditorAsync(original);
    await SelectPassageAsync(original);
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Page.Locator("#story-replacement").FillAsync(replacement);
    await Page.Locator("#story-edit-proposal").ClickAsync();
    var first = Page.Locator("#story-replacement-diff ins").First;
    await first.HoverAsync();
    await Expect(Page.Locator("#story-original .story-change-active")).ToHaveTextAsync("antigo");
    await Expect(Page.Locator("#story-replacement-diff .story-change-active")).ToHaveTextAsync("ancestral");
    var colors = await Page.Locator("#story-replacement-diff ins").EvaluateAllAsync<string[]>("nodes => nodes.map(n => getComputedStyle(n).backgroundColor)");
    Assert.HasCount(4, colors.Distinct().ToArray());
    Assert.AreEqual(await first.EvaluateAsync<string>("n => getComputedStyle(n).backgroundColor"),
      await Page.Locator("#story-original del").First.EvaluateAsync<string>("n => getComputedStyle(n).backgroundColor"));
    await SaveScreenshotAsync("story-paired-changes-hover.png");
    await Page.Locator("#story-proposal-model").HoverAsync();
    await Expect(Page.Locator(".story-change-active")).ToHaveCountAsync(0);
    await first.FocusAsync();
    await Expect(Page.Locator("#story-original .story-change-active")).ToHaveTextAsync("antigo");
    foreach (var source in new[] { "story-original", "story-replacement-diff" })
    {
      await Page.Locator($"#{source}").EvaluateAsync("el => el.scrollTop = (el.scrollHeight - el.clientHeight) * 0.7");
      await Page.WaitForFunctionAsync("""
        () => {
          const a = document.getElementById('story-original'), b = document.getElementById('story-replacement-diff');
          return Math.abs(a.scrollTop/(a.scrollHeight-a.clientHeight) - b.scrollTop/(b.scrollHeight-b.clientHeight)) < .005
            && a.scrollTop > 100 && b.scrollTop > 100;
        }
        """);
      await Page.Locator($"#{source}").EvaluateAsync("el => el.scrollTop = 0");
      await Page.WaitForFunctionAsync("document.getElementById('story-original').scrollTop === 0 && document.getElementById('story-replacement-diff').scrollTop === 0");
    }
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Expect(Page.Locator(".story-change-active")).ToHaveCountAsync(0);
    await Page.Locator("#story-replacement").EvaluateAsync("el => el.scrollTop = el.scrollHeight");
    await Page.WaitForFunctionAsync("""
      () => { const a = document.getElementById('story-original'); return a.scrollTop > 100 && a.scrollHeight-a.clientHeight-a.scrollTop < 2; }
      """);
    await Page.Locator("#story-original del").First.FocusAsync();
    await Expect(Page.Locator(".story-change-active")).ToHaveCountAsync(0);
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(original);
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(replacement);
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(original);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task LargeUnrelatedReplacementUsesBoundedBlockComparisonAndPreservesExactText()
  {
    var original = string.Concat(Enumerable.Repeat("antigo ", 1500));
    var replacement = string.Concat(Enumerable.Repeat("novo! ", 1500));
    await OpenEditorAsync(original);
    await SelectPassageAsync(original);
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Page.Locator("#story-replacement").FillAsync(replacement);
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Expect(Page.Locator("#story-diff-note")).ToContainTextAsync("highlighted as a block");
    Assert.AreEqual(original, await Page.Locator("#story-original").TextContentAsync());
    Assert.AreEqual(replacement, await Page.Locator("#story-replacement-diff").TextContentAsync());
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(replacement);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task MarkdownViewRendersSafelyAndPreservesRawSelectionAndExportWithoutInference()
  {
    const string source = "# A torre\n\nUm **segredo** e uma *carta*.\n\n- Primeiro\n- Segundo\n\n| Nome | Lugar |\n| --- | --- |\n| Clara | Torre |\n\n```text\n<vento>\n```\n\n<script>window.storyInjected = true</script>\n\n[perigo](javascript:alert(1))\n\n![imagem](https://example.invalid/tracker.png)";
    await OpenEditorAsync(source);
    var calls = _environment.FakeOllama.AllRequests.Count;
    await SelectPassageAsync("segredo");
    await Page.Locator("#story-mode-view").ClickAsync();
    var preview = Page.Locator("#story-preview");
    await Expect(preview.Locator("h1")).ToHaveTextAsync("A torre");
    await Expect(preview.Locator("strong")).ToHaveTextAsync("segredo");
    await Expect(preview.Locator("ul li")).ToHaveCountAsync(2);
    await Expect(preview.Locator("table td")).ToHaveCountAsync(2);
    await Expect(preview.Locator("pre code")).ToContainTextAsync("<vento>");
    await Expect(preview.Locator("script, iframe, img, [onclick], a[href^='javascript:']")).ToHaveCountAsync(0);
    Assert.IsTrue(await Page.EvaluateAsync<bool>("window.storyInjected === undefined"));
    await Expect(Page.Locator("#story-revise")).ToBeDisabledAsync();
    await Expect(Page.Locator("#story-document")).ToBeHiddenAsync();
    foreach (var width in new[] { 1600, 760, 320 })
    {
      await Page.SetViewportSizeAsync(width, 900);
      await preview.ScrollIntoViewIfNeededAsync();
      Assert.IsTrue(await Page.Locator("#story-editor").EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
      await SaveScreenshotAsync($"story-view-{width}.png");
    }
    var download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual(source, await File.ReadAllTextAsync((await download.PathAsync())!));
    await Page.Locator("#story-mode-raw").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync(source);
    Assert.AreEqual("segredo", await SelectedPassageAsync());
    await Expect(Page.Locator("#story-revise")).ToBeEnabledAsync();
    Assert.HasCount(calls, _environment.FakeOllama.AllRequests);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task TokenEstimateAndViewFollowImportApplyUndoAndNewEdits()
  {
    await OpenEditorAsync("");
    var estimate = Page.Locator("#story-token-estimate");
    await Expect(estimate).ToHaveTextAsync("~0 tokens · Whole draft");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "conto.md"), "abcdef");
    await OpenWorkspaceStoryAsync("conto.md");
    await Expect(estimate).ToHaveTextAsync("~2 tokens · Whole draft");
    await SelectPassageAsync("abcdef");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Page.Locator("#story-replacement").FillAsync("um texto breve");
    await Expect(estimate).ToHaveTextAsync("~2 tokens · Whole draft");
    await Page.Locator("#story-close-comparison").ClickAsync();
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview")).ToHaveTextAsync("abcdef");
    await Page.Locator("#story-review-proposal").ClickAsync();
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(estimate).ToHaveTextAsync("~5 tokens · Whole draft");
    await Expect(Page.Locator("#story-preview")).ToHaveTextAsync("um texto breve");
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(estimate).ToHaveTextAsync("~2 tokens · Whole draft");
    await Expect(Page.Locator("#story-preview")).ToHaveTextAsync("abcdef");
    await Page.Locator("#story-mode-raw").ClickAsync();
    await Page.Locator("#story-document").FillAsync("# Novo\n\nTexto atualizado.");
    await Page.Locator("#story-mode-view").ClickAsync();
    await Expect(Page.Locator("#story-preview h1")).ToHaveTextAsync("Novo");
    await Expect(Page.Locator("#story-preview")).Not.ToContainTextAsync("abcdef");
    using var invalid = await _environment.HttpClient.PostAsJsonAsync("api/story-editor/preview",
      new { document = new string('x', 200_001) });
    Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task FixedSpecialistSurvivesRevisionKeywordsAndManualModelWhileAutomaticRemainsUnchanged()
  {
    await Page.GotoAsync("/");
    await Expect(Page.Locator("#specialist-selector")).ToHaveValueAsync("");
    await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
    await SendMessageAsync("Revise a história do personagem de RPG.");
    await Expect(Page.Locator("#active-specialist")).ToHaveTextAsync("RPG storytelling · Fixed");
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request => request.Temperature == 0.8
      && request.Messages.Any(message => message.Role == "system" && message.Content.Contains("You tell RPG stories.", StringComparison.Ordinal))));
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    await SendMessageAsync("Inspect this passage and fix the repeated character name.");
    var fixedRequest = _environment.FakeOllama.Requests.Last();
    Assert.AreEqual("alpha:latest", fixedRequest.Model);
    Assert.AreEqual(0.8, fixedRequest.Temperature);
    Assert.IsTrue(fixedRequest.Messages.Any(message => message.Role == "system" && message.Content.Contains("You tell RPG stories.", StringComparison.Ordinal)));
    await Page.Locator("#specialist-selector").SelectOptionAsync("");
    await SendMessageAsync("Revise a história do personagem de RPG.");
    await Expect(Page.Locator("#active-specialist")).ToHaveTextAsync("Automatic · Review and testing");
    await Expect(Page.Locator("#specialist-selector option:checked")).ToHaveTextAsync("Auto · Review and testing");
    Assert.IsTrue(_environment.FakeOllama.Requests.Last().Messages.Any(message => message.Role == "system" && message.Content.Contains("You review and test software.", StringComparison.Ordinal)));
    await Page.Locator("#specialist-selector").SelectOptionAsync("documentation");
    await Page.Locator("#model-selector").SelectOptionAsync("auto");
    await SendMessageAsync("Tell a story about an old castle.");
    Assert.AreEqual("docs:latest", _environment.FakeOllama.Requests.Last().Model);
    Assert.IsTrue(_environment.FakeOllama.Requests.Last().Messages.Any(message => message.Role == "system" && message.Content.Contains("You write documentation.", StringComparison.Ordinal)));
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task FixedSpecialistIsRestoredWithEnabledHistoryAndNewConversationStartsAutomatic()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspaceId}/history", new { enabled = true });
    enabled.EnsureSuccessStatusCode();
    await Page.GotoAsync("/");
    await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
    await SendMessageAsync("Revise a história de RPG.");
    var id = await Page.EvaluateAsync<string>("state.conversationSessionId");
    var saved = await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/sessions/{id}?workspaceId={workspaceId}");
    Assert.AreEqual("rpg-storytelling", saved!["specialist"]!.GetValue<string>());
    await _environment.RestartApplicationAsync();
    await Page.ReloadAsync();
    await Page.Locator("#recent-sessions").EvaluateAsync("element => element.open = true");
    await Page.Locator($".session-entry[data-session-id='{id}'] .session-entry-content").ClickAsync();
    await Expect(Page.Locator("#specialist-selector")).ToHaveValueAsync("rpg-storytelling");
    await Expect(Page.Locator("#active-specialist")).ToHaveTextAsync("RPG storytelling · Fixed");
    await Page.Locator("#new-conversation").ClickAsync();
    await Expect(Page.Locator("#specialist-selector")).ToHaveValueAsync("");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task InvalidSpecialistFailsBeforeInferenceAndSupervisedRouteCarriesFixedSelection()
  {
    await Page.GotoAsync("/");
    var before = _environment.FakeOllama.AllRequests.Count;
    using var invalid = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream", new
    {
      message = "hello",
      model = "auto",
      specialist = "RPG",
      history = Array.Empty<object>()
    });
    var events = ParseSseEvents(await invalid.Content.ReadAsStringAsync());
    Assert.AreEqual("request-validation", events.Single(item => item["type"]!.GetValue<string>() == "error")["error"]!["stage"]!.GetValue<string>());
    Assert.HasCount(before, _environment.FakeOllama.AllRequests);
    using var prepared = await _environment.HttpClient.PostAsJsonAsync("api/supervision/runs/prepare", new
    {
      objective = "Write a story about a castle",
      model = "auto",
      harness = "native",
      browserSessionId = "fixed-specialist-route",
      specialist = "documentation"
    });
    prepared.EnsureSuccessStatusCode();
    var run = await prepared.Content.ReadFromJsonAsync<JsonObject>();
    var view = await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/supervision/runs/{run!["runId"]!.GetValue<string>()}");
    Assert.AreEqual("docs:latest", view!["route"]!["model"]!.GetValue<string>());
    Assert.AreEqual("documentation", view["route"]!["specialist"]!.GetValue<string>());
    using var cancelled = await _environment.HttpClient.PostAsJsonAsync($"api/supervision/runs/{run["runId"]!.GetValue<string>()}/cancel", new { });
    cancelled.EnsureSuccessStatusCode();
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ReviewComparesWithoutChangingThenAppliesOnlyOriginalSelectionAndCanUndo()
  {
    var workspaceId = await ActiveWorkspaceIdAsync();
    using var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspaceId}/history", new { enabled = true });
    enabled.EnsureSuccessStatusCode();
    await OpenEditorAsync("Prólogo.\nO mago antigo entrou.\nEpílogo.");
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-original")).ToHaveTextAsync("antigo");
    await Expect(Page.Locator("#story-replacement")).ToHaveValueAsync("antigo — revisado");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Prólogo.\nO mago antigo entrou.\nEpílogo.");
    await Expect(Page.Locator("#messages .message")).ToHaveCountAsync(0);
    await Expect(Page.Locator("#story-comparison")).Not.ToContainTextAsync("Private editor reasoning");
    await Page.Locator("#story-close-comparison").ClickAsync();
    await Page.Locator("#story-document").EvaluateAsync("element => element.setSelectionRange(0, 7)");
    await Page.Locator("#story-review-proposal").ClickAsync();
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Prólogo.\nO mago antigo — revisado entrou.\nEpílogo.");
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Prólogo.\nO mago antigo entrou.\nEpílogo.");
    Assert.IsTrue(_environment.FakeOllama.Requests.Any(request => request.Messages.Any(message => message.Role == "system" && message.Content.Contains("You tell RPG stories.", StringComparison.Ordinal))));
    var sessions = await _environment.HttpClient.GetFromJsonAsync<JsonObject>("api/sessions");
    Assert.HasCount(0, sessions!["recent"]!.AsArray());
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task SynonymCandidatesRequireChoiceComparisonAndExplicitApply()
  {
    await OpenEditorAsync("O mago antigo entrou.");
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-synonyms").ClickAsync();
    await Expect(Page.Locator("#story-alternatives button")).ToHaveCountAsync(2);
    await Page.Locator("#story-alternatives button").Filter(new() { HasText = "vetusto" }).ClickAsync();
    await Expect(Page.Locator("#story-replacement")).ToHaveValueAsync("vetusto");
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago antigo entrou.");
    await Expect(Page.Locator("#story-replacement-diff ins")).ToHaveTextAsync("vetusto");
    await Page.Locator("#story-edit-proposal").ClickAsync();
    await Page.Locator("#story-replacement").FillAsync("ancestral e silencioso");
    await Page.Locator("#story-apply").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago ancestral e silencioso entrou.");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task DraftEditsDuringAndAfterGenerationInvalidateProposalWithoutOverwriting()
  {
    await OpenEditorAsync("O mago antigo entrou.");
    var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _environment.FakeOllama.BeforeChatResponse = request =>
    {
      if (!IsEditorRequest(request)) return Task.CompletedTask;
      arrived.TrySetResult();
      return release.Task;
    };
    try
    {
      await SelectPassageAsync("antigo");
      await Page.Locator("#story-revise").ClickAsync();
      await arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
      await Page.Locator("#story-document").FillAsync("O mago antigo saiu.");
      release.TrySetResult();
      await Expect(Page.Locator("#story-comparison")).ToBeVisibleAsync();
      await Expect(Page.Locator("#story-apply")).ToBeDisabledAsync();
      await Expect(Page.Locator("#story-status")).ToContainTextAsync("Draft changed during generation");
      await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago antigo saiu.");
    }
    finally { release.TrySetResult(); _environment.FakeOllama.BeforeChatResponse = null; }
    await Page.Locator("#story-close-comparison").ClickAsync();
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-close-comparison").ClickAsync();
    await Page.Locator("#story-document").FillAsync("O mago antigo voltou.");
    await Page.Locator("#story-review-proposal").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeDisabledAsync();
    await Expect(Page.Locator("#story-status")).ToContainTextAsync("Draft changed");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task RepetitionsUseUnicodeCountsAndCycleExactOccurrencesWithoutInference()
  {
    await OpenEditorAsync("O dragão viu o DRAGÃO. Ação, ação; avó e avô. Guarda-chuva guarda-chuva.");
    var requests = _environment.FakeOllama.AllRequests.Count;
    await Page.Locator("#story-repetitions").ClickAsync();
    var dragon = Page.Locator("#story-repetition-results button").Filter(new() { HasText = "dragão · 2" });
    await Expect(dragon).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-repetition-results")).ToContainTextAsync("ação · 2");
    await Expect(Page.Locator("#story-repetition-results")).ToContainTextAsync("guarda-chuva · 2");
    await dragon.ClickAsync();
    Assert.AreEqual("dragão", await SelectedPassageAsync());
    await dragon.ClickAsync();
    Assert.AreEqual("DRAGÃO", await SelectedPassageAsync());
    await Page.Locator("#story-ignore-common").UncheckAsync();
    await Expect(Page.Locator("#story-repetition-results").GetByRole(AriaRole.Button, new() { Name = "o · 2", Exact = true })).ToBeVisibleAsync();
    Assert.HasCount(requests, _environment.FakeOllama.AllRequests);
    await Page.Locator("#story-document").FillAsync("Outro dragão sozinho.");
    await Expect(Page.Locator("#story-repetition-results")).ToHaveTextAsync("No repeated words found with these filters.");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task FileImportProtectsDraftAndMarkdownExportContainsOnlyAppliedText()
  {
    await OpenEditorAsync("Texto que deve ser preservado.");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "capítulo.txt"), "# Capítulo\n\nO mago antigo entrou.\n");
    await OpenWorkspaceStoryAsync("capítulo.txt");
    await Expect(Page.Locator("#story-import-confirmation")).ToBeVisibleAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Texto que deve ser preservado.");
    await Page.Locator("#story-cancel-import").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Texto que deve ser preservado.");
    await OpenWorkspaceStoryAsync("capítulo.txt");
    await Page.Locator("#story-confirm-import").ClickAsync();
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-close-comparison").ClickAsync();
    var download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual("capítulo.md", download.SuggestedFilename);
    Assert.AreEqual("# Capítulo\n\nO mago antigo entrou.\n", await File.ReadAllTextAsync((await download.PathAsync())!));
    await Page.Locator("#story-review-proposal").ClickAsync();
    await Page.Locator("#story-apply").ClickAsync();
    download = await Page.RunAndWaitForDownloadAsync(() => Page.Locator("#story-export").ClickAsync());
    Assert.AreEqual("# Capítulo\n\nO mago antigo — revisado entrou.\n", await File.ReadAllTextAsync((await download.PathAsync())!));
    await Page.Locator("#close-story-editor").ClickAsync();
    await Page.Locator("#open-story-editor").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("# Capítulo\n\nO mago antigo — revisado entrou.\n");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ProviderFailureMalformedSynonymsAndCancellationPreserveDraft()
  {
    await OpenEditorAsync("O mago antigo entrou.");
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-instruction").FillAsync("editor malformed synonyms fixture");
    await Page.Locator("#story-synonyms").ClickAsync();
    await Expect(Page.Locator("#story-status")).ToContainTextAsync("did not return usable synonyms");
    await Expect(Page.Locator("#story-loading")).ToBeHiddenAsync();
    await Expect(Page.Locator("#story-comparison")).ToBeHiddenAsync();
    await Page.Locator("#story-instruction").FillAsync("editor provider failure fixture");
    var failed = await Page.RunAndWaitForResponseAsync(() => Page.Locator("#story-revise").ClickAsync(),
      response => response.Url.EndsWith("/api/story-editor/proposals", StringComparison.Ordinal));
    Assert.AreEqual(503, failed.Status);
    using var failure = JsonDocument.Parse(await failed.TextAsync());
    Assert.IsFalse(string.IsNullOrWhiteSpace(failure.RootElement.GetProperty("code").GetString()));
    Assert.IsTrue(failure.RootElement.GetProperty("recoverable").GetBoolean());
    await Expect(Page.Locator("#story-status")).ToContainTextAsync("Ollama returned HTTP 503.");
    await Expect(Page.Locator("#story-status")).ToContainTextAsync("Draft preserved");
    Assert.DoesNotContain("An unhandled exception was thrown", _environment.ApiOutput);
    await Expect(Page.Locator("#story-cancel")).ToBeHiddenAsync();
    await Expect(Page.Locator("#story-loading")).ToBeHiddenAsync();
    var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _environment.FakeOllama.BeforeChatResponse = request =>
    {
      if (!IsEditorRequest(request)) return Task.CompletedTask;
      arrived.TrySetResult();
      return release.Task;
    };
    try
    {
      await Page.Locator("#story-instruction").FillAsync("");
      await Page.Locator("#story-revise").ClickAsync();
      await arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
      await Expect(Page.Locator("#story-loading")).ToBeVisibleAsync();
      await Page.Locator("#story-cancel").ClickAsync();
      await Expect(Page.Locator("#story-status")).ToContainTextAsync("cancelled");
      await Expect(Page.Locator("#story-loading")).ToBeHiddenAsync();
      await Expect(Page.Locator("#story-comparison")).ToBeHiddenAsync();
      await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago antigo entrou.");
    }
    finally { release.TrySetResult(); _environment.FakeOllama.BeforeChatResponse = null; }
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    Assert.DoesNotContain("System.NotSupportedException", _environment.ApiOutput);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task EditorValidatesRangesBoundsContextAndUsesOnlyReadOnlyChatTools()
  {
    await Page.GotoAsync("/");
    var requests = _environment.FakeOllama.AllRequests.Count;
    foreach (var body in new object[]
    {
      new { operation = "revise", document = "abc", selectionStart = -1, selectionEnd = 2 },
      new { operation = "revise", document = "abc", selectionStart = 0, selectionEnd = 4 },
      new { operation = "execute", document = "abc", selectionStart = 0, selectionEnd = 2 }
    })
    {
      using var invalid = await _environment.HttpClient.PostAsJsonAsync("api/story-editor/proposals", body);
      Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
      var error = await invalid.Content.ReadFromJsonAsync<JsonObject>();
      Assert.AreEqual("story-editor-input-invalid", error!["code"]!.GetValue<string>());
    }
    Assert.HasCount(requests, _environment.FakeOllama.AllRequests);
    var document = "UNSENT PREFIX" + new string('a', 3000) + "antigo" + new string('b', 3000) + "UNSENT SUFFIX";
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/story-editor/proposals", new
    {
      operation = "revise",
      document,
      selectionStart = 3013,
      selectionEnd = 3019,
      model = "alpha:latest"
    });
    response.EnsureSuccessStatusCode();
    var request = _environment.FakeOllama.Requests.Last(IsEditorRequest);
    var prompt = request.Messages.Last(message => message.Role == "user").Content;
    Assert.DoesNotContain("UNSENT PREFIX", prompt);
    Assert.DoesNotContain("UNSENT SUFFIX", prompt);
    using var source = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
    Assert.AreEqual(2000, source.RootElement.GetProperty("before").GetString()!.Length);
    Assert.AreEqual(2000, source.RootElement.GetProperty("after").GetString()!.Length);
    Assert.AreEqual("antigo", source.RootElement.GetProperty("selection").GetString());
    Assert.IsFalse(request.AvailableTools.Any(tool => tool is "write_file" or "create_file" or "run_process"));
    Assert.IsTrue(request.Messages.Any(message => message.Role == "system" && message.Content.Contains("You tell RPG stories.", StringComparison.Ordinal)));
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task StorytellingEntryRequiresFixedSpecialistAndKeepsDraftWhenSwitching()
  {
    await Page.GotoAsync("/");
    await Expect(Page.Locator("#open-story-editor")).ToBeHiddenAsync();
    await SendMessageAsync("Tell an RPG story about a dragon.");
    await Expect(Page.Locator("#active-specialist")).ToHaveTextAsync("Automatic · RPG storytelling");
    await Expect(Page.Locator("#specialist-selector option:checked")).ToHaveTextAsync("Auto · RPG storytelling");
    await Expect(Page.Locator("#open-story-editor")).ToBeHiddenAsync();
    await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
    await Page.GetByRole(AriaRole.Button, new() { Name = "Storytelling Editor", Exact = true }).ClickAsync();
    await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Storytelling Editor", Exact = true })).ToBeVisibleAsync();
    await Page.Locator("#story-document").FillAsync("Uma história preservada.");
    foreach (var specialist in new[] { "documentation", "" })
    {
      await Page.Locator("#specialist-selector").SelectOptionAsync(specialist);
      await Expect(Page.Locator("#open-story-editor")).ToBeHiddenAsync();
      await Expect(Page.Locator("#story-editor")).ToBeHiddenAsync();
      await Expect(Page.Locator("#message-input")).ToBeVisibleAsync();
      await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
      await Page.Locator("#open-story-editor").ClickAsync();
      await Expect(Page.Locator("#story-document")).ToHaveValueAsync("Uma história preservada.");
    }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task UndoRemainsAvailableAfterAnotherProposalIsDiscardedAndEditorReopened()
  {
    await OpenEditorAsync("O mago antigo entrou.");
    await SelectPassageAsync("antigo");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-apply").ClickAsync();
    await SelectPassageAsync("mago");
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-apply")).ToBeEnabledAsync();
    await Page.Locator("#story-discard").ClickAsync();
    await Expect(Page.Locator("#story-comparison")).ToBeHiddenAsync();
    await Page.Locator("#close-story-editor").ClickAsync();
    await Expect(Page.Locator("#open-story-editor")).ToBeFocusedAsync();
    await Page.Keyboard.PressAsync("Enter");
    await Expect(Page.Locator("#story-document")).ToBeFocusedAsync();
    await Expect(Page.Locator("#story-undo")).ToBeVisibleAsync();
    await Page.Locator("#story-undo").ClickAsync();
    await Expect(Page.Locator("#story-document")).ToHaveValueAsync("O mago antigo entrou.");
    await Expect(Page.Locator("#story-undo")).ToBeDisabledAsync();
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task SpecialistSitsBesideLocalSessionWithoutOverlappingHeaderControls()
  {
    await Page.GotoAsync("/");
    var selector = Page.GetByLabel("Specialist", new() { Exact = true });
    await Expect(Page.Locator("#app-loader")).ToBeHiddenAsync();
    await Expect(selector).ToBeEnabledAsync();
    await selector.FocusAsync();
    await Page.Keyboard.PressAsync("ArrowDown");
    await Page.Keyboard.PressAsync("Enter");
    await Page.Keyboard.PressAsync("Tab");
    await Expect(selector).ToHaveValueAsync("general-chat");
    foreach (var width in new[] { 1600, 1280, 1100, 760, 390, 320 })
    {
      await Page.SetViewportSizeAsync(width, 900);
      foreach (var specialist in new[] { "", "rpg-storytelling", "software-development" })
      {
        await selector.SelectOptionAsync(specialist);
        await Expect(Page.Locator(".session-heading .eyebrow")).ToBeVisibleAsync();
        Assert.IsTrue(await Page.EvaluateAsync<bool>("""
          () => {
            const rect = selector => document.querySelector(selector).getBoundingClientRect();
            const label = rect('.session-heading .eyebrow');
            const picker = rect('#specialist-selector');
            const header = rect('.chat-header');
            const runtime = rect('#runtime-summary');
            const groups = ['.title-group', '.runtime-details', '.header-actions'].map(rect);
            return Math.abs(label.y + label.height / 2 - picker.y - picker.height / 2) < 2
              && picker.x - label.right >= 6 && picker.x - label.right <= 12
              && picker.right <= header.right - 8
              && Math.abs(runtime.x + runtime.width / 2 - header.x - header.width / 2) <= 2
              && runtime.left >= header.left && runtime.right <= header.right
              && [...document.querySelectorAll('#runtime-summary .runtime-compact-indicator')].every(element => {
                const meter = element.getBoundingClientRect();
                return meter.left >= header.left && meter.right <= header.right;
              })
              && groups.every((a, index) => groups.slice(index + 1).every(b =>
                a.right <= b.x || b.right <= a.x || a.bottom <= b.y || b.bottom <= a.y))
              && document.documentElement.scrollWidth <= innerWidth
              && header.height <= (header.width > 980 ? 64 : header.width > 600 ? 110 : 170);
          }
          """), $"Header alignment/overlap at {width}px with {specialist}");
      }
      await selector.SelectOptionAsync("rpg-storytelling");
      await SaveScreenshotAsync($"specialist-header-{width}.png");
    }
    await Page.SetViewportSizeAsync(1600, 900);
    await Page.Locator("#toggle-sidebar").ClickAsync();
    await Expect(selector).ToBeVisibleAsync();
    await selector.SelectOptionAsync("");
    await SaveScreenshotAsync("specialist-header-collapsed-auto.png");
    await Page.Locator("#toggle-sidebar").ClickAsync();
    await Page.SetViewportSizeAsync(1280, 900);
    await Page.Locator("#sidebar-resizer").FocusAsync();
    await Page.Keyboard.PressAsync("End");
    await selector.SelectOptionAsync("rpg-storytelling");
    await Page.Locator("#open-story-editor").ClickAsync();
    await Page.WaitForFunctionAsync("""
      () => {
        const header = document.querySelector('.chat-header').getBoundingClientRect();
        const runtime = document.querySelector('#runtime-summary').getBoundingClientRect();
        const editor = document.querySelector('#story-editor').getBoundingClientRect();
        return Math.abs(runtime.x + runtime.width / 2 - header.x - header.width / 2) <= 2
          && Math.abs(editor.top - header.bottom) <= 1
          && Math.abs(editor.left - header.left) <= 1 && Math.abs(editor.right - header.right) <= 1;
      }
      """);
    await SaveScreenshotAsync("specialist-header-wide-sidebar.png");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task EditorAndSpecialistRemainUsableAtDesktopAndMobileWidths()
  {
    var errors = new List<string>();
    Page.PageError += (_, error) => errors.Add(error);
    await OpenEditorAsync("# O último farol\n\nA chuva apagava as pegadas na estrada. Clara parou diante do farol antigo, com a carta dobrada no bolso.\n\nLá dentro, uma luz atravessou a janela. O farol estava abandonado havia vinte anos — mas alguém acabara de acender a lâmpada.\n\nClara respirou fundo e bateu à porta.");
    await SelectPassageAsync(await Page.Locator("#story-document").InputValueAsync());
    await Page.Locator("#story-revise").ClickAsync();
    await Expect(Page.Locator("#story-comparison")).ToBeVisibleAsync();
    await Page.Locator("#story-close-comparison").ClickAsync();
    foreach (var width in new[] { 1600, 1280, 1100, 760, 390, 320 })
    {
      await Page.SetViewportSizeAsync(width, 900);
      await Expect(Page.Locator("#specialist-selector")).ToBeVisibleAsync();
      await Expect(Page.Locator("#story-document")).ToBeVisibleAsync();
      Assert.IsTrue(await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
      Assert.IsTrue(await Page.Locator("#story-editor").EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth + 1"));
      Assert.IsTrue(await Page.EvaluateAsync<bool>("""
        () => {
          const rect = id => document.getElementById(id).getBoundingClientRect();
          const open = rect('story-open-file'), name = rect('story-file-name'), save = rect('story-export');
          return Math.abs(open.height - save.height) < 1 && Math.abs(open.bottom - save.bottom) < 1
            && (innerWidth <= 600 ? name.bottom <= open.top : Math.abs(name.bottom - open.bottom) < 1);
        }
        """), $"File toolbar alignment at {width}px");
      await Page.WaitForFunctionAsync("document.querySelector('#story-editor').getBoundingClientRect().top >= document.querySelector('.chat-header').getBoundingClientRect().bottom - 1");
      if (width <= 1100)
      {
        Assert.IsTrue(await Page.EvaluateAsync<bool>("""
          () => {
            const header = document.querySelector('.chat-header').getBoundingClientRect();
            const editor = document.querySelector('#story-editor').getBoundingClientRect();
            return Math.abs(editor.top - header.bottom) <= 1
              && Math.abs(editor.left - header.left) <= 1 && Math.abs(editor.right - header.right) <= 1;
          }
          """), $"Full-width editor placement at {width}px");
      }
      await Page.Locator("#story-editor").EvaluateAsync("element => element.scrollTop = 0");
      await SaveScreenshotAsync($"rpg-editor-{width}.png");
      await Page.Locator("#story-review-proposal").ClickAsync();
      Assert.IsTrue(await Page.EvaluateAsync<bool>("""
        () => {
          const rect = id => document.getElementById(id).getBoundingClientRect();
          const dialog = rect('story-comparison'), original = rect('story-original');
          const replacement = rect('story-replacement-diff'), apply = rect('story-apply');
          return dialog.left >= 0 && dialog.right <= innerWidth && dialog.top >= 0 && dialog.bottom <= innerHeight
            && apply.bottom <= dialog.bottom && apply.top >= dialog.top
            && (innerWidth <= 600 || (dialog.width >= innerWidth * 0.7
              && Math.abs(original.height - replacement.height) <= 1 && original.height >= 300));
        }
        """), $"Review modal dimensions at {width}px");
      await Page.Keyboard.PressAsync("Tab");
      Assert.IsTrue(await Page.EvaluateAsync<bool>("document.querySelector('#story-comparison').contains(document.activeElement)"));
      await SaveScreenshotAsync($"rpg-comparison-{width}.png");
      await Page.Keyboard.PressAsync("Escape");
      await Expect(Page.Locator("#story-comparison")).ToBeHiddenAsync();
      await Expect(Page.Locator("#story-review-proposal")).ToBeFocusedAsync();
    }
    await Page.Locator("#close-story-editor").ClickAsync();
    await Expect(Page.Locator("#message-input")).ToBeVisibleAsync();
    Assert.HasCount(0, errors);
  }

  private async Task SaveScreenshotAsync(string name)
  {
    var directory = Path.Combine(_environment.RepositoryRoot, ".artifacts", "rpg-editor", "visual-review");
    Directory.CreateDirectory(directory);
    var screenshot = Path.Combine(directory, name);
    await Page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
    TestContext.AddResultFile(screenshot);
    TestContext.WriteLine($"Inspected viewport capture: {name}");
  }

  private async Task OpenEditorAsync(string text)
  {
    await Page.GotoAsync("/");
    await Page.Locator("#specialist-selector").SelectOptionAsync("rpg-storytelling");
    await Expect(Page.Locator("#open-story-editor")).ToBeEnabledAsync();
    await Page.Locator("#open-story-editor").ClickAsync();
    await Expect(Page.Locator("#active-specialist")).ToHaveTextAsync("RPG storytelling · Fixed");
    await Page.Locator("#story-document").FillAsync(text);
  }

  private async Task OpenWorkspaceStoryAsync(string name)
  {
    await Page.Locator("#story-open-file").ClickAsync();
    await Page.Locator("#story-files-list").GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();
  }

  private async Task SelectPassageAsync(string text)
  {
    await Page.Locator("#story-document").EvaluateAsync("""
      (element, text) => {
        const start = element.value.indexOf(text);
        element.focus();
        element.setSelectionRange(start, start + text.length);
        element.dispatchEvent(new Event('select'));
      }
      """, text);
  }

  private async Task<string> SelectedPassageAsync() => await Page.Locator("#story-document")
    .EvaluateAsync<string>("element => element.value.slice(element.selectionStart, element.selectionEnd)");

  private static bool IsEditorRequest(RecordedChatRequest request) => request.Messages.Any(message =>
    message.Role == "user" && message.Content.StartsWith("AGENTIC_ROUTER_STORY_EDITOR_V1\n", StringComparison.Ordinal));
}
