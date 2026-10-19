using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryFilesEndToEndTests : ChatEndToEndTestBase<HistoryFilesEndToEndTests>
{
  private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

  private static async Task SetHistoryAsync(bool enabled)
  {
    var workspace = await ActiveWorkspaceIdAsync();
    using var response = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspace}/history", new { enabled });
    response.EnsureSuccessStatusCode();
  }

  private static async Task<string> NewSessionAsync(string window)
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/sessions/new", new { browserSessionId = window });
    response.EnsureSuccessStatusCode();
    return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["sessionId"]!.GetValue<string>();
  }

  private static async Task<JsonObject[]> TurnAsync(string session, string window, string message, string mode = "chat", bool image = false,
    string harness = "native", string? selectedModel = null)
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream", new
    {
      message,
      model = selectedModel ?? (mode == "execute" ? "qwen3-coder:30b" : "alpha:latest"),
      history = Array.Empty<object>(),
      interactionMode = mode,
      harness,
      approvalPolicy = "auto",
      executionStrategy = "direct",
      conversationSessionId = session,
      browserSessionId = window,
      historyWindowId = window,
      images = image ? new[] { new { id = "pixel", fileName = "pixel.png", mimeType = "image/png", base64Data = Pixel,
        declaredBytes = Convert.FromBase64String(Pixel).Length } } : []
    });
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.IsTrue(events.Any(item => item["type"]?.GetValue<string>() == "response.completed"),
      string.Join("\n", events.Where(item => item["type"]?.GetValue<string>() == "error").Select(item => item.ToJsonString())));
    return events;
  }

  private static async Task<JsonNode> RewindAsync(string session, string window, string turn, int index = 0)
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync($"api/history-files/rewind/{Guid.NewGuid():N}", new
    { sessionId = session, turnId = turn, messageIndex = index, windowId = window, browserSessionId = window });
    response.EnsureSuccessStatusCode();
    return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
  }

  private static Task<string> GitAsync(params string[] arguments) => GitAsync(0, arguments);
  private static async Task<string> GitAsync(int expectedExitCode, params string[] arguments)
  {
    using var process = new Process
    {
      StartInfo = new ProcessStartInfo("git")
      {
        WorkingDirectory = _environment.WorkspaceDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      }
    };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Assert.AreEqual(expectedExitCode, process.ExitCode, await output + await error);
    return (await output).Trim();
  }

  private static async Task InitializeGitAsync()
  {
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, ".gitignore"), "hello.txt\nignored.dat\n");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "existing.txt"), "initial");
    await File.WriteAllBytesAsync(Path.Combine(_environment.WorkspaceDirectory, "ignored.dat"), Enumerable.Repeat((byte)42, 65_536).ToArray());
    await GitAsync("init");
    await GitAsync("config", "user.name", "AR E2E");
    await GitAsync("config", "user.email", "ar-e2e@example.invalid");
    await GitAsync("add", ".");
    await GitAsync("commit", "-m", "Initial fixture");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "existing.txt"), "user changes before prompt");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ReasoningHistoryRemainsExactAfterRestartAndLegacyNormalization(bool legacy)
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var workspace = await ActiveWorkspaceIdAsync();
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var live = await TurnAsync(session, window, "codex incident reasoning flood", "execute", harness: "codex");
    var original = string.Concat(live.Where(item => item["type"]?.GetValue<string>() == "reasoning.delta")
      .Select(item => item["reasoningDelta"]?.GetValue<string>()));
    Assert.IsTrue(original.Contains(new string('x', 150), StringComparison.Ordinal));
    if (legacy)
    {
      var path = Path.Combine(_environment.DataDirectory, "workspaces", workspace, "sessions", session + ".json");
      string json;
      if (File.Exists(path)) json = await File.ReadAllTextAsync(path);
      else
      {
        await using var source = File.OpenRead(path + ".gz");
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        json = await reader.ReadToEndAsync();
      }
      var saved = JsonNode.Parse(json)!;
      var assistant = saved["messages"]!.AsArray().Last(item => item!["role"]!.GetValue<string>() == "assistant")!;
      var expanded = new JsonArray();
      foreach (var item in assistant["timeline"]!.AsArray())
      {
        if (item!["type"]!.GetValue<string>() != "reasoning.delta") expanded.Add(item.DeepClone());
        else foreach (var character in item["reasoningDelta"]!.GetValue<string>())
        {
          var delta = item.DeepClone();
          delta["reasoningDelta"] = character.ToString();
          expanded.Add(delta);
        }
      }
      assistant["timeline"] = expanded;
      await File.WriteAllTextAsync(path, saved.ToJsonString());
      File.Delete(path + ".gz");
    }
    await _environment.RestartApplicationAsync();
    using var reopened = await _environment.HttpClient.PostAsJsonAsync($"api/sessions/{session}/open", new { browserSessionId = window });
    reopened.EnsureSuccessStatusCode();
    var messages = JsonNode.Parse(await reopened.Content.ReadAsStringAsync())!["messages"]!.AsArray();
    var timeline = messages.Last(item => item!["role"]!.GetValue<string>() == "assistant")!["timeline"]!.AsArray();
    var reasoning = timeline.Where(item => item!["type"]!.GetValue<string>() == "reasoning.delta").ToArray();
    Assert.AreEqual(original, string.Concat(reasoning.Select(item => item!["reasoningDelta"]?.GetValue<string>())));
    Assert.IsLessThan(live.Count(item => item["type"]?.GetValue<string>() == "reasoning.delta"), reasoning.Length);
    Assert.IsTrue(timeline.Any(item => item!["type"]?.GetValue<string>() == "response.completed"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ChatEditRewindsImmediatelyWithoutProviderCallAndRemainsRewoundAfterReload()
  {
    await SetHistoryAsync(true);
    await Page.GotoAsync("/");
    await SendMessageAsync("First message");
    await SendMessageAsync("Second message");
    var calls = _environment.FakeOllama.Requests.Count;
    await Page.GetByRole(AriaRole.Button, new() { Name = "Edit message", Exact = true }).First.ClickAsync();
    await Expect(Page.Locator("#message-input")).ToHaveJSPropertyAsync("value", "First message");
    await Expect(Page.Locator(".message.user")).ToHaveCountAsync(0);
    Assert.HasCount(calls, _environment.FakeOllama.Requests);
    using var sessions = await _environment.HttpClient.GetAsync("api/sessions");
    var id = JsonNode.Parse(await sessions.Content.ReadAsStringAsync())!["recent"]![0]!["id"]!.GetValue<string>();
    using var opened = await _environment.HttpClient.GetAsync($"api/sessions/{id}?workspaceId={await ActiveWorkspaceIdAsync()}");
    Assert.HasCount(0, JsonNode.Parse(await opened.Content.ReadAsStringAsync())!["messages"]!.AsArray());
    await Page.ReloadAsync();
    Assert.HasCount(calls, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ImagesSurviveRestartCompressionAndRecoveryAsIndependentCopies()
  {
    await SetHistoryAsync(true);
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "Describe the attached pixel.", image: true);
    var turn = events[0]["requestId"]!.GetValue<string>();
    using var archived = await _environment.HttpClient.PostAsync($"api/sessions/{session}/archive", null);
    archived.EnsureSuccessStatusCode();
    await Page.GotoAsync("/");
    await Page.Locator(".project-accordion.active .project-menu-button").ClickAsync();
    await Page.Locator("#project-menu-edit").ClickAsync();
    var compacted = Page.WaitForResponseAsync(response => response.Url.EndsWith("/api/history-files/compact", StringComparison.Ordinal)
      && response.Request.Method == "POST");
    await Page.Locator("#compact-history-files").ClickAsync();
    var compressed = await compacted;
    Assert.IsTrue(compressed.Ok);
    var result = JsonNode.Parse(await compressed.TextAsync())!;
    Assert.IsGreaterThan(0, result["savedBytes"]!.GetValue<long>());
    await Expect(Page.Locator("#history-file-operation-status")).ToContainTextAsync("History compacted");
    await Expect(Page.Locator("#history-file-operation-status")).ToContainTextAsync("100%");
    var sessionDirectory = Path.Combine(_environment.DataDirectory, "workspaces", await ActiveWorkspaceIdAsync(), "sessions");
    Assert.IsFalse(File.Exists(Path.Combine(sessionDirectory, session + ".json")));
    Assert.IsTrue(File.Exists(Path.Combine(sessionDirectory, session + ".json.gz")));
    await _environment.RestartApplicationAsync();
    await Page.GotoAsync("/");
    await Page.Locator(".project-accordion.active .project-menu-button").ClickAsync();
    await Page.Locator("#project-menu-edit").ClickAsync();
    await Page.Locator("#view-history-files").ClickAsync();
    await Expect(Page.Locator("#history-files-list .history-file-link")).ToHaveTextAsync("pixel.png");
    var downloaded = Page.WaitForDownloadAsync();
    await Page.Locator("#history-files-dialog").GetByRole(AriaRole.Button,
      new() { Name = "Download copy", Exact = true }).ClickAsync();
    var download = await downloaded;
    var downloadPath = await download.PathAsync();
    Assert.IsNotNull(downloadPath);
    CollectionAssert.AreEqual(Convert.FromBase64String(Pixel), await File.ReadAllBytesAsync(downloadPath));
    await Page.Locator("#close-history-files").ClickAsync();
    using var backup = await _environment.HttpClient.PostAsJsonAsync("api/recovery/backup", new
    { includeConversations = true, includeSessionSummaries = false, includeUsageHistory = false, includeReviewData = false });
    backup.EnsureSuccessStatusCode();
    var backupBytes = await backup.Content.ReadAsByteArrayAsync();
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (var archive = new ZipArchive(new MemoryStream(backupBytes), ZipArchiveMode.Read))
    {
      var record = archive.GetEntry($"data/workspaces/{workspaceId}/sessions/{session}.json");
      Assert.IsNotNull(record);
      using var text = new StreamReader(record.Open());
      StringAssert.Contains(await text.ReadToEndAsync(), "Describe the attached pixel.");
    }
    using var inspection = await _environment.HttpClient.PostAsJsonAsync("api/recovery/backup/inspect",
      new { archiveBase64 = Convert.ToBase64String(backupBytes) });
    inspection.EnsureSuccessStatusCode();
    var inspected = JsonNode.Parse(await inspection.Content.ReadAsStringAsync())!;
    Assert.IsTrue(inspected["hashesValid"]!.GetValue<bool>());
    Assert.IsTrue(inspected["conflicts"]!.AsArray().Any(item => item!.GetValue<string>().EndsWith(session + ".json", StringComparison.Ordinal)));
    using var restoredBackup = await _environment.HttpClient.PostAsJsonAsync("api/recovery/backup/restore", new
    { archiveBase64 = Convert.ToBase64String(backupBytes), categories = new[] { "conversations" }, confirmed = true });
    restoredBackup.EnsureSuccessStatusCode();
    Assert.IsTrue(File.Exists(Path.Combine(sessionDirectory, session + ".json")));
    Assert.IsFalse(File.Exists(Path.Combine(sessionDirectory, session + ".json.gz")));
    using var listing = await _environment.HttpClient.GetAsync("api/history-files");
    var items = JsonNode.Parse(await listing.Content.ReadAsStringAsync())!.AsArray();
    var item = items.Single(value => value!["sessionId"]!.GetValue<string>() == session && value["kind"]!.GetValue<string>() == "attachment")!;
    Assert.AreEqual("pixel.png", item["name"]!.GetValue<string>());
    Assert.IsFalse(item.ToJsonString().Contains(_environment.DataDirectory, StringComparison.OrdinalIgnoreCase));
    using var copy = await _environment.HttpClient.PostAsJsonAsync("api/history-files/download", new { fileId = item["id"]!.GetValue<string>() });
    copy.EnsureSuccessStatusCode();
    CollectionAssert.AreEqual(Convert.FromBase64String(Pixel), await copy.Content.ReadAsByteArrayAsync());
    var restored = await RewindAsync(session, Guid.NewGuid().ToString("N"), turn);
    Assert.AreEqual(Pixel, restored["images"]![0]!["base64Data"]!.GetValue<string>());
    Assert.HasCount(0, restored["session"]!["messages"]!.AsArray());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExecuteSnapshotsIgnoredBytesPreserveDirtyBaselineAndRemoveCreatedFiles()
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "Create hello.txt for footer inference metrics", "execute");
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
    var progress = events.Where(item => item["hostFileProgress"] is not null).ToArray();
    Assert.IsTrue(progress.Any(item => item["hostFileProgress"]!["action"]!.GetValue<string>() == "Creating snapshot"));
    Assert.AreEqual(100, progress.Last()["hostFileProgress"]!["percent"]!.GetValue<double>());
    using var listing = await _environment.HttpClient.GetAsync("api/history-files");
    var items = JsonNode.Parse(await listing.Content.ReadAsStringAsync())!.AsArray();
    Assert.IsFalse(items.Any(item => item!["name"]!.GetValue<string>() == "ignored.dat"), "Unmodified ignored files must not be snapshotted.");
    Assert.IsGreaterThan(Array.FindIndex(events, item => item["type"]?.GetValue<string>() == "action.execution-started"),
      Array.FindIndex(events, item => item["hostFileProgress"]?["action"]?.GetValue<string>() == "Creating snapshot"));
    await RewindAsync(session, window, events[0]["requestId"]!.GetValue<string>());
    Assert.IsFalse(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
    Assert.AreEqual("user changes before prompt", await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "existing.txt")));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BrowserRollbackRestoresFilesAndComposerWithoutAnotherProviderCall()
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    const string prompt = "Create hello.txt for footer inference metrics";
    await SendMessageAsync(prompt);
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    Assert.IsTrue(File.Exists(path));
    var calls = _environment.FakeOllama.Requests.Count;
    await Page.GetByRole(AriaRole.Button, new() { Name = "Rollback", Exact = true }).ClickAsync();
    await Expect(Page.Locator("#message-input")).ToHaveJSPropertyAsync("value", prompt);
    await Expect(Page.Locator("#message-input")).ToBeEnabledAsync();
    await Expect(Page.Locator(".message.user")).ToHaveCountAsync(0);
    await Expect(Page.Locator(".host-file-operation")).ToContainTextAsync("Rollback complete");
    Assert.IsFalse(File.Exists(path));
    Assert.HasCount(calls, _environment.FakeOllama.Requests);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task ExecuteRestoresOverwrittenIgnoredBytesAndLeavesGitHeadAndIndexUntouched()
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    var original = System.Text.Encoding.UTF8.GetBytes("hello · conteúdo anterior\r\n");
    await File.WriteAllBytesAsync(path, original);
    await GitAsync("add", "existing.txt");
    var head = await GitAsync("rev-parse", "HEAD");
    var indexTree = await GitAsync("write-tree");
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "execute write file", "execute", selectedModel: "alpha:latest");
    CollectionAssert.AreNotEqual(original, await File.ReadAllBytesAsync(path));
    await RewindAsync(session, window, events[0]["requestId"]!.GetValue<string>());
    CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
    Assert.AreEqual(head, await GitAsync("rev-parse", "HEAD"));
    Assert.AreEqual(indexTree, await GitAsync("write-tree"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task GitInitializationCreatesTheInitialLocalCommitBeforeExecute()
  {
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "initial.txt"), "initial project bytes");
    await GitAsync("init");
    await GitAsync("config", "user.name", "AR E2E");
    await GitAsync("config", "user.email", "ar-e2e@example.invalid");
    var window = Guid.NewGuid().ToString("N");
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/history-files/git-decision",
      new { windowId = window, choice = "initialize" });
    response.EnsureSuccessStatusCode();
    var decision = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    Assert.IsTrue(decision["ready"]!.GetValue<bool>());
    Assert.IsTrue(decision["fileRollback"]!.GetValue<bool>());
    await GitAsync("show", "HEAD:initial.txt");
    await GitAsync("diff", "--cached", "--exit-code");
    Assert.IsFalse(string.IsNullOrWhiteSpace(await GitAsync("rev-parse", "HEAD")));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task AnUnrelatedLockedIgnoredFileDoesNotBlockExecuteOrCreateASnapshot()
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var calls = _environment.FakeOllama.Requests.Count;
    using var locked = new FileStream(Path.Combine(_environment.WorkspaceDirectory, "ignored.dat"),
      FileMode.Open, FileAccess.Read, FileShare.None);
    using var response = await _environment.HttpClient.PostAsJsonAsync("api/chat/stream", new
    {
      message = "Recover this original prompt",
      model = "alpha:latest",
      interactionMode = "execute",
      harness = "native",
      approvalPolicy = "auto",
      conversationSessionId = session,
      browserSessionId = window,
      historyWindowId = window,
      images = new[] { new { id = "pixel", fileName = "pixel.png", mimeType = "image/png", base64Data = Pixel,
        declaredBytes = Convert.FromBase64String(Pixel).Length } }
    });
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    Assert.IsTrue(events.Any(item => item["type"]?.GetValue<string>() == "response.completed"), string.Join("\n", events.Where(item => item["type"]?.GetValue<string>() == "error").Select(item => item.ToJsonString())));
    Assert.IsGreaterThan(calls, _environment.FakeOllama.Requests.Count);
    Assert.IsFalse(events.Any(item => item["hostFileProgress"]?["action"]?.GetValue<string>() == "Creating snapshot"));
    locked.Dispose();
    var restored = await RewindAsync(session, window, events[0]["requestId"]!.GetValue<string>());
    Assert.AreEqual("Recover this original prompt", restored["message"]!.GetValue<string>());
    Assert.AreEqual(Pixel, restored["images"]![0]!["base64Data"]!.GetValue<string>());
    Assert.HasCount(0, restored["session"]!["messages"]!.AsArray());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task RollbackRejectsLaterFileChangesAndKeepsConversationAndCurrentContent()
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "Create hello.txt for footer inference metrics", "execute");
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    await File.WriteAllTextAsync(path, "external change after prompt");
    using var response = await _environment.HttpClient.PostAsJsonAsync($"api/history-files/rewind/{Guid.NewGuid():N}", new
    { sessionId = session, turnId = events[0]["requestId"]!.GetValue<string>(), messageIndex = 0, windowId = window, browserSessionId = window });
    Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    StringAssert.Contains(await response.Content.ReadAsStringAsync(), "snapshot-file-conflict");
    Assert.AreEqual("external change after prompt", await File.ReadAllTextAsync(path));
    using var opened = await _environment.HttpClient.GetAsync($"api/sessions/{session}?workspaceId={await ActiveWorkspaceIdAsync()}");
    Assert.IsGreaterThanOrEqualTo(2, JsonNode.Parse(await opened.Content.ReadAsStringAsync())!["messages"]!.AsArray().Count);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SnapshotSupportsUnmergedGitIndexWithoutBlockingFileWork()
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var path = Path.Combine(_environment.WorkspaceDirectory, "hello.txt");
    await File.WriteAllTextAsync(path, "base");
    await GitAsync("add", "-f", "hello.txt");
    await GitAsync("commit", "-m", "Conflict fixture base");
    var branch = await GitAsync("branch", "--show-current");
    await GitAsync("checkout", "-b", "codex/fixture-conflict");
    await File.WriteAllTextAsync(path, "other branch");
    await GitAsync("add", "hello.txt");
    await GitAsync("commit", "-m", "Other fixture branch");
    await GitAsync("checkout", branch);
    await File.WriteAllTextAsync(path, "current branch");
    await GitAsync("add", "hello.txt");
    await GitAsync("commit", "-m", "Current fixture branch");
    await GitAsync(1, "merge", "codex/fixture-conflict");
    var original = await File.ReadAllBytesAsync(path);
    var stages = await GitAsync("ls-files", "--stage");
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "execute write file", "execute", selectedModel: "alpha:latest");
    Assert.AreEqual("rewritten by agent", await File.ReadAllTextAsync(path));
    await RewindAsync(session, window, events[0]["requestId"]!.GetValue<string>());
    CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
    Assert.AreEqual(stages, await GitAsync("ls-files", "--stage"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task FullIndexFingerprintDetectsChangesBeyondRetainedGitOutput()
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    string last = "";
    for (var index = 0; index < 1_000; index++)
    {
      last = $"index-probe-{index:D4}.txt";
      await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, last), "original bytes");
    }
    await GitAsync("add", "--all");
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "Create hello.txt for footer inference metrics", "execute");
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "stage-source.txt"), "external staged bytes");
    var changed = await GitAsync("hash-object", "-w", "stage-source.txt");
    await GitAsync("update-index", "--cacheinfo", $"100644,{changed},{last}");
    using var response = await _environment.HttpClient.PostAsJsonAsync($"api/history-files/rewind/{Guid.NewGuid():N}", new
    { sessionId = session, turnId = events[0]["requestId"]!.GetValue<string>(), messageIndex = 0, windowId = window, browserSessionId = window });
    Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    StringAssert.Contains(await response.Content.ReadAsStringAsync(), "snapshot-git-conflict");
    Assert.IsTrue(File.Exists(Path.Combine(_environment.WorkspaceDirectory, "hello.txt")));
    Assert.AreEqual("original bytes", await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, last)));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task DisabledHistoryAndNoGitConsentAreWindowScoped()
  {
    await SetHistoryAsync(false);
    var window = Guid.NewGuid().ToString("N");
    using var choice = await _environment.HttpClient.PostAsJsonAsync("api/history-files/git-decision", new { windowId = window, choice = "context-only" });
    choice.EnsureSuccessStatusCode();
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "Describe the attached pixel.", image: true);
    using var different = await _environment.HttpClient.PostAsJsonAsync("api/history-files/git-decision", new { windowId = Guid.NewGuid().ToString("N"), choice = "inspect" });
    Assert.IsFalse(JsonNode.Parse(await different.Content.ReadAsStringAsync())!["ready"]!.GetValue<bool>());
    using var released = await _environment.HttpClient.PostAsync($"api/history-files/release-window/{window}", null);
    released.EnsureSuccessStatusCode();
    using var response = await _environment.HttpClient.PostAsJsonAsync($"api/history-files/rewind/{Guid.NewGuid():N}", new
    { sessionId = session, turnId = events[0]["requestId"]!.GetValue<string>(), messageIndex = 0, windowId = window, browserSessionId = window });
    Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    StringAssert.Contains(await response.Content.ReadAsStringAsync(), "snapshot-unavailable");
  }

  [TestMethod]
  [DataRow("codex")]
  [DataRow("claude-code")]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task HostSnapshotsAreCommonToExternalHarnesses(string harness)
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, "execute create file", "execute", harness: harness);
    Assert.IsTrue(events.Any(item => item["hostFileProgress"]?["action"]?.GetValue<string>() == "File rollback ready; snapshots deferred until mutation"));
    Assert.IsTrue(events.Any(item => item["hostFileProgress"]?["action"]?.GetValue<string>() == "Recording snapshot result"));
    await RewindAsync(session, window, events[0]["requestId"]!.GetValue<string>());
    Assert.AreEqual("user changes before prompt", await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "existing.txt")));
  }

  [TestMethod]
  [DataRow("codex", "delete codex file", "codex-delete.txt")]
  [DataRow("claude-code", "claude approval", "claude-approved.txt")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NativeMutationPersistsOriginalBytesBeforeApprovalAndRollsBackExactly(string harness, string prompt, string name)
  {
    await SetHistoryAsync(true);
    await InitializeGitAsync();
    var path = Path.Combine(_environment.WorkspaceDirectory, name);
    var original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(System.Text.Encoding.UTF8.GetBytes("conteúdo anterior 🙂\r\n")).ToArray();
    await File.WriteAllBytesAsync(path, original);
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    var events = await TurnAsync(session, window, prompt, "execute", harness: harness);
    var captured = Array.FindIndex(events, item => item["hostFileProgress"]?["action"]?.GetValue<string>() == "Creating snapshot"
      && item["hostFileProgress"]?["state"]?.GetValue<string>() == "completed");
    var approved = Array.FindIndex(events, item => item["type"]?.GetValue<string>() == "action.approved");
    Assert.IsTrue(captured >= 0 && approved > captured, "Original bytes must commit before the native capability is authorized.");
    using var listing = await _environment.HttpClient.GetAsync("api/history-files");
    var files = JsonNode.Parse(await listing.Content.ReadAsStringAsync())!.AsArray();
    Assert.IsTrue(files.Any(file => file!["name"]!.GetValue<string>() == name));
    Assert.IsFalse(files.Any(file => file!["name"]!.GetValue<string>() == "ignored.dat"));
    await RewindAsync(session, window, events[0]["requestId"]!.GetValue<string>());
    CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task SharedAttachmentRemainsAccessibleAfterOriginalConversationIsDeleted()
  {
    await SetHistoryAsync(true);
    var window = Guid.NewGuid().ToString("N");
    var session = await NewSessionAsync(window);
    await TurnAsync(session, window, "Describe the attached pixel.", image: true);
    using var duplicate = await _environment.HttpClient.PostAsync($"api/sessions/{session}/duplicate", null);
    duplicate.EnsureSuccessStatusCode();
    var duplicateId = JsonNode.Parse(await duplicate.Content.ReadAsStringAsync())!["session"]!["id"]!.GetValue<string>();
    using var deleted = await _environment.HttpClient.DeleteAsync($"api/sessions/{session}?confirmed=true");
    deleted.EnsureSuccessStatusCode();
    using var listing = await _environment.HttpClient.GetAsync("api/history-files");
    var item = JsonNode.Parse(await listing.Content.ReadAsStringAsync())!.AsArray()
      .Single(value => value!["sessionId"]!.GetValue<string>() == duplicateId && value["kind"]!.GetValue<string>() == "attachment")!;
    using var downloaded = await _environment.HttpClient.PostAsJsonAsync("api/history-files/download", new { fileId = item["id"]!.GetValue<string>() });
    downloaded.EnsureSuccessStatusCode();
    CollectionAssert.AreEqual(Convert.FromBase64String(Pixel), await downloaded.Content.ReadAsByteArrayAsync());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task NoGitDialogAndConsentResetAreVisibleInBrowser()
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#message-input").FillAsync("footer inference metrics create file");
    await Page.Locator("#send-button").ClickAsync();
    var dialog = Page.Locator("#snapshot-git-dialog");
    await Expect(dialog).ToBeVisibleAsync();
    await Expect(dialog).ToContainTextAsync("changes to project files will not be rolled back");
    var captures = Path.Combine(_environment.RepositoryRoot, ".artifacts", "history-validation", "screenshots");
    Directory.CreateDirectory(captures);
    await Page.ScreenshotAsync(new() { Path = Path.Combine(captures, "no-git-decision.png") });
    await dialog.GetByRole(AriaRole.Button, new() { Name = "Continue without file rollback", Exact = true }).ClickAsync();
    await Expect(Page.Locator(".message.assistant .activity").Last).ToHaveAttributeAsync("data-terminal", "true");
    await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Rollback", Exact = true })).ToBeVisibleAsync();
    Assert.IsGreaterThan(0, await Page.Locator(".activity-icon .assistant-running-host").CountAsync());
    await Page.Locator(".message.assistant .activity").Last.EvaluateAsync("element => element.open = true");
    await Page.Locator(".activity-group").EvaluateAllAsync<bool>("elements => { elements.forEach(element => element.open = true); return true; }");
    await Page.Locator(".activity-icon .assistant-running-host").First.ScrollIntoViewIfNeededAsync();
    await Page.ScreenshotAsync(new() { Path = Path.Combine(captures, "host-file-activity.png") });
    await Page.ReloadAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#message-input").FillAsync("footer inference metrics create file");
    await Page.Locator("#send-button").ClickAsync();
    await Expect(dialog).ToBeVisibleAsync();
    await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
  }
}
