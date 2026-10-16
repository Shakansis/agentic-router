using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Usage;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class FileReadPolicyEndToEndTests : ChatEndToEndTestBase<FileReadPolicyEndToEndTests>
{
  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task NativeCanUseACompleteFileBeyondTheHalfContextGenerationCeiling()
  {
    await ConfigureContextAsync(65_536);
    var original = new string('a', 145_000) + "NATIVE_FILE_END";
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "native-large.txt"), original);
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3-coder:30b");
    await SetExecuteModeAsync("auto");
    await SendMessageAsync("native large read fixture: inspect native-large.txt and report its end marker.");
    await Expect(Page.Locator(".assistant-answer").Last).ToContainTextAsync("NATIVE_FULL_READ_OK");
    var final = _environment.FakeOllama.AllRequests.Last(request => request.Messages.Any(message => message.Role == "tool" && message.Content.Contains("NATIVE_FILE_END", StringComparison.Ordinal)));
    Assert.IsTrue(final.Messages.Any(message => message.Content.Contains(original, StringComparison.Ordinal)));
    Assert.IsGreaterThan(0, final.PredictTokens!.Value);
    Assert.IsLessThan(32_768, final.PredictTokens.Value,
      "The output allowance must fit the remaining window after the complete file read.");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task ChatCompactsPriorEvidenceAndReadsAnotherRangeWithoutExtraInference()
  {
    await ConfigureContextAsync(16_384);
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), new string('a', 90_000));
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "read-b.txt"), new string('b', 2_000));
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("alpha:latest");
    var before = _environment.FakeOllama.AllRequests.Count;
    await SendMessageAsync("chat workspace read request adaptive file read single two reads\nREAD_ARGUMENTS:{\"path\":\"chat-readable.txt\"}");
    var requests = _environment.FakeOllama.AllRequests.Skip(before).ToArray();
    Assert.HasCount(3, requests);
    var last = requests.Last().Messages;
    Assert.IsTrue(last.Any(message => message.Role == "tool" && message.Content.Contains("HOST_READ_CONTEXT_COMPACTED_V1", StringComparison.Ordinal)));
    Assert.IsTrue(last.Any(message => message.Role == "tool" && message.Content.Contains(new string('b', 512), StringComparison.Ordinal)));
    await Expect(Page.Locator(".message.assistant .activity").Last).ToContainTextAsync("Host compacted active read context");
    await Expect(Page.Locator(".assistant-work .work-action-preview").First).ToContainTextAsync(new string('a', 512));
  }

  [TestMethod]
  [DataRow(false, false, DisplayName = "HarnessReadsHeadroomSingle")]
  [DataRow(true, false, DisplayName = "HarnessReadsHeadroomBatch")]
  [DataRow(false, true, DisplayName = "HarnessReadsExhaustedSingle")]
  [DataRow(true, true, DisplayName = "HarnessReadsExhaustedBatch")]
  [DoNotParallelize]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task HarnessReadsUseActualHeadroomAndRecoverWhenContextIsExhausted(bool batch, bool exhausted)
  {
    // Each recovery needs a spare native session; other fixture conversations remain protected.
    await _environment.DisposeAsync();
    _environment = await TestEnvironment.StartAsync();
    await ConfigureContextAsync(131_072);
    var original = new string('a', 43_471);
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "context-read.txt"), original);
    var fixtureLog = Path.Combine(_environment.DataDirectory, "qwen-code-runtime", "host-read-pressure.jsonl");
    if (File.Exists(fixtureLog)) File.Delete(fixtureLog);
    await Page.GotoAsync(_environment.BaseUri.AbsoluteUri);
    await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync("qwen-code");
    try
    {
      await SendMessageAsync($"host read pressure fixture {(batch ? "batch" : "single")} {(exhausted ? "exhausted" : "headroom")}");
    }
    catch
    {
      if (File.Exists(fixtureLog)) TestContext.WriteLine(await File.ReadAllTextAsync(fixtureLog));
      TestContext.WriteLine(string.Join("\n", _environment.ApiOutput.Split('\n').Where(line => line.Contains("Host file read budget", StringComparison.Ordinal))));
      throw;
    }
    await Expect(Page.Locator(".assistant-answer").Last).ToContainTextAsync("PARTIAL_READ_OK");
    var records = (await File.ReadAllLinesAsync(fixtureLog)).Select(line => JsonNode.Parse(line)!).ToArray();
    Assert.HasCount(exhausted ? 2 : 1, records);
    Assert.IsTrue(records.Last()["succeeded"]!.GetValue<bool>());
    StringAssert.Contains(records.Last()["output"]!.GetValue<string>(), new string('a', 512));
    if (exhausted)
    {
      Assert.IsFalse(records[0]["succeeded"]!.GetValue<bool>());
      Assert.IsTrue(records[1]["recovering"]!.GetValue<bool>());
      Assert.AreNotEqual(records[0]["sessionId"]!.GetValue<string>(), records[1]["sessionId"]!.GetValue<string>());
      await Expect(Page.Locator(".message.assistant .activity").Last).ToContainTextAsync("Host context recovery completed.");
    }
    var id = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
    var review = JsonNode.Parse(await _environment.HttpClient.GetStringAsync($"api/execution-sessions/{id}/review"))!;
    Assert.IsEmpty(review["files"]!.AsArray());
    Assert.IsEmpty(review["processes"]!.AsArray());
    Assert.AreEqual(original, await File.ReadAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "context-read.txt")));
  }

  [TestMethod]
  [DataRow(30_000, 32_768, "alpha:latest")]
  [DataRow(150_000, 65_536, "alpha:latest")]
  [DataRow(30_000, 32_768, "qwen3-coder:30b")]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task CompleteReadsReachTheProviderWithoutCharacterOrLegacyByteTruncation(int characters, int context, string model)
  {
    await ConfigureContextAsync(context);
    var content = new string('a', characters) + "END_OF_FILE";
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), content);
    var output = await ReadAsync("read_file", new { path = "chat-readable.txt" }, model);
    Assert.AreEqual(content, output);
    await Expect(Page.Locator(".assistant-answer")).ToContainTextAsync("Adaptive file evidence collected.");
  }

  [TestMethod]
  [DataRow(90_000, 16_384, "context-budget")]
  [DataRow(240_000, 65_536, "selective-context-policy")]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task PartialReadsExposeTheirReasonAndExactContinuation(int characters, int context, string reason)
  {
    await ConfigureContextAsync(context);
    var original = new string('a', characters);
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), original);
    var range = JsonNode.Parse(await ReadAsync("read_file", new { path = "chat-readable.txt" }))!;
    Assert.IsTrue(range["truncated"]!.GetValue<bool>());
    Assert.AreEqual(reason, range["truncationReason"]!.GetValue<string>());
    var content = range["content"]!.GetValue<string>();
    Assert.IsGreaterThan(16_000, content.Length);
    Assert.AreEqual(Encoding.UTF8.GetByteCount(content), range["nextOffsetBytes"]!.GetValue<long>());
    Assert.IsLessThanOrEqualTo(range["availableContextTokens"]!.GetValue<long>(),
      new ConservativeTokenEstimator().EstimateText(JsonSerializer.Serialize(range.ToJsonString())) + 256);
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(120_000, CooperativeCancellation = true)]
  public async Task ExplicitRangesReconstructMultibyteTextWithoutReplacementOrGaps(bool utf16)
  {
    await ConfigureContextAsync(32_768);
    var encoding = utf16 ? (Encoding)new UnicodeEncoding(false, true, true) : new UTF8Encoding(false, true);
    var original = string.Concat(Enumerable.Repeat("🙂漢á\n", 300));
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), original, encoding);
    var result = new StringBuilder();
    long offset = 0;
    var size = new FileInfo(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt")).Length;
    for (var attempt = 0; attempt < 12; attempt++)
    {
      var range = JsonNode.Parse(await ReadAsync("read_file", new { path = "chat-readable.txt", offsetBytes = offset, lengthBytes = 511 }))!;
      result.Append(range["content"]!.GetValue<string>());
      var next = range["nextOffsetBytes"]?.GetValue<long>();
      Assert.AreEqual(offset + range["lengthBytes"]!.GetValue<long>(), next ?? size);
      if (next is null) break;
      Assert.IsGreaterThan(offset, next.Value);
      offset = next.Value;
    }
    Assert.AreEqual(original, result.ToString());
  }

  [TestMethod]
  [DataRow(2)]
  [DataRow(16)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BatchReadsDeliverAllFittingFilesInOneToolCall(int count)
  {
    await ConfigureContextAsync(65_536);
    var first = new string('a', 30_000) + "FIRST_END";
    var second = new string('b', 35_000) + "SECOND_END";
    await WriteBatchFilesAsync(first, second);
    var paths = new List<string> { "read-a.txt", "read-b.txt" };
    for (var index = 2; index < count; index++)
    {
      var path = $"read-{index}.txt";
      paths.Add(path);
      await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, path), $"complete file {index}");
    }
    var batch = JsonNode.Parse(await ReadAsync("read_files", new { files = paths.Select(path => new { path }).ToArray() }))!;
    var files = batch["files"]!.AsArray();
    Assert.HasCount(count, files);
    Assert.AreEqual("read-a.txt", files[0]!["path"]!.GetValue<string>());
    Assert.AreEqual(first, files[0]!["content"]!.GetValue<string>());
    Assert.AreEqual(second, files[1]!["content"]!.GetValue<string>());
    Assert.IsFalse(files[0]!["truncated"]!.GetValue<bool>());
    Assert.IsFalse(files[1]!["truncated"]!.GetValue<bool>());
    for (var index = 2; index < count; index++)
    {
      Assert.AreEqual($"complete file {index}", files[index]!["content"]!.GetValue<string>());
      Assert.IsFalse(files[index]!["truncated"]!.GetValue<bool>());
    }
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BatchReadsHonorPriorityAndAggregateContext()
  {
    await ConfigureContextAsync(16_384);
    await WriteBatchFilesAsync(new string('a', 40_000), new string('b', 40_000));
    var batch = JsonNode.Parse(await ReadAsync("read_files", new { files = new[] { new { path = "read-a.txt" }, new { path = "read-b.txt" } } }))!;
    var first = batch["files"]![0]!;
    var second = batch["files"]![1]!;
    Assert.IsGreaterThan(16_000, first["content"]!.GetValue<string>().Length);
    Assert.AreEqual("", second["content"]!.GetValue<string>());
    Assert.IsTrue(first["truncated"]!.GetValue<bool>());
    Assert.AreEqual(0L, second["nextOffsetBytes"]!.GetValue<long>());
    var budget = batch["availableContextTokens"]!.GetValue<long>();
    Assert.IsLessThanOrEqualTo(budget,
      new ConservativeTokenEstimator().EstimateText(JsonSerializer.Serialize(batch.ToJsonString())) + 256);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BatchRejectsWorkspaceEscapesAndExcessiveFileCounts()
  {
    await ConfigureContextAsync(32_768);
    await WriteBatchFilesAsync("first", "second");
    var escaped = await ReadAsync("read_files", new { files = new[] { new { path = "../outside.txt" }, new { path = "read-a.txt" } } });
    StringAssert.Contains(escaped, "outside the trusted workspace");
    StringAssert.Contains(_environment.FakeOllama.AllRequests.Last().Messages.Last(item => item.Role == "tool").Content,
      "Status: rejected");
    var oversized = await ReadAsync("read_files", new { files = Enumerable.Range(0, 129).Select(_ => new { path = "read-a.txt" }).ToArray() });
    StringAssert.Contains(oversized, "1 to 128 files");
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task OtherToolsKeepTheirExistingResultLimit()
  {
    await ConfigureContextAsync(32_768);
    for (var index = 0; index < 500; index++)
      await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, $"entry-{index:D4}-{new string('x', 50)}.txt"), "x");
    var output = await ReadAsync("list_files", new { path = "." });
    StringAssert.EndsWith(output, "[tool result truncated]");
    Assert.AreEqual(16_000 + "\n[tool result truncated]".Length, output.Length);
  }

  [TestMethod]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task LargeFilesStayPhysicallyBoundedAndRejectOversizedRanges()
  {
    await ConfigureContextAsync(65_536);
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), new string('a', 2 * 1024 * 1024));
    var range = JsonNode.Parse(await ReadAsync("read_file", new { path = "chat-readable.txt" }))!;
    Assert.AreEqual(1024 * 1024, range["maxRangeBytes"]!.GetValue<int>());
    Assert.AreEqual(2 * 1024 * 1024L, range["sizeBytes"]!.GetValue<long>());
    Assert.IsTrue(range["truncated"]!.GetValue<bool>());
    Assert.IsLessThan(1024 * 1024L, range["lengthBytes"]!.GetValue<long>());
    var rejected = await ReadAsync("read_file", new { path = "chat-readable.txt", lengthBytes = 1024 * 1024 + 1 });
    StringAssert.Contains(rejected, "lengthBytes between 1 and 1048576");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task OffsetsInsideCharactersAreRejectedWithoutReplacement(bool utf16)
  {
    await ConfigureContextAsync(32_768);
    var encoding = utf16 ? (Encoding)new UnicodeEncoding(false, true, true) : new UTF8Encoding(false, true);
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), "🙂text", encoding);
    var rejected = await ReadAsync("read_file", new { path = "chat-readable.txt", offsetBytes = utf16 ? 3 : 1, lengthBytes = 1 });
    StringAssert.Contains(rejected, "character");
    StringAssert.Contains(_environment.FakeOllama.AllRequests.Last().Messages.Last(item => item.Role == "tool").Content, "Status: failed");
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(90_000, CooperativeCancellation = true)]
  public async Task BatchMemoryDeferralKeepsTheOriginalContinuationOffset(bool utf16)
  {
    await ConfigureContextAsync(131_072);
    var paths = Enumerable.Range(0, 16).Select(index => $"large-{index}.txt").ToArray();
    foreach (var path in paths)
      await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, path), new string('a', 1024 * 1024));
    // Leave one byte: neither a UTF-16 BOM nor a UTF-8 emoji can be partially admitted.
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, paths[7]), new string('a', 1024 * 1024 - 1));
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, paths[8]), "🙂small text",
      utf16 ? Encoding.Unicode : new UTF8Encoding(false));
    var batch = JsonNode.Parse(await ReadAsync("read_files", new { files = paths.Select(path => new { path }).ToArray() }, "qwen3.8:27b-gpu0"))!;
    Assert.AreEqual("FILE_READ_MEMORY_EXHAUSTED", batch["files"]![8]!["code"]!.GetValue<string>());
    Assert.AreEqual(0L, batch["files"]![8]!["nextOffsetBytes"]!.GetValue<long>());
    var last = batch["files"]!.AsArray().Last()!;
    Assert.AreEqual("FILE_READ_MEMORY_EXHAUSTED", last["code"]!.GetValue<string>());
    Assert.AreEqual(0L, last["nextOffsetBytes"]!.GetValue<long>());
    Assert.AreEqual("", last["content"]!.GetValue<string>());
  }

  private async Task ConfigureContextAsync(int context)
  {
    var settings = (await _environment.HttpClient.GetFromJsonAsync<TestApplicationSettings>("api/settings", TestJson.Options))!;
    using var saved = await _environment.PutSettingsAsync(settings with
    {
      Context = settings.Context with { DefaultContextTokens = context, ProviderContextTokens = context },
      OllamaRuntime = settings.OllamaRuntime with
      {
        RoleDefaults = settings.OllamaRuntime.RoleDefaults.ToDictionary(pair => pair.Key,
          pair => pair.Value with
          {
            MinimumContextTokens = Math.Min(context, pair.Value.MinimumContextTokens),
            MaximumContextTokens = context,
            TargetContextTokens = context
          })
      }
    });
    Assert.IsTrue(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
  }

  private async Task WriteBatchFilesAsync(string first, string second)
  {
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "read-a.txt"), first);
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "read-b.txt"), second);
  }

  private async Task<string> ReadAsync(string tool, object arguments, string model = "alpha:latest")
  {
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    var before = _environment.FakeOllama.AllRequests.Count;
    await SendMessageAsync($"chat workspace read request adaptive file read {(tool == "read_files" ? "batch" : tool == "list_files" ? "list" : "single")}\nREAD_ARGUMENTS:{JsonSerializer.Serialize(arguments)}");
    var requests = _environment.FakeOllama.AllRequests.Skip(before).ToArray();
    Assert.HasCount(2, requests, "One tool call and one final synthesis must suffice.");
    var message = requests.Last().Messages.Last(item => item.Role == "tool").Content;
    const string marker = "Output:\n";
    return message.Contains(marker, StringComparison.Ordinal) ? message.Split(marker, 2)[1]
      : JsonNode.Parse(message)!["output"]!.GetValue<string>();
  }
}
