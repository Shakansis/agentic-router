using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class InstalledHarnessUpgradeEndToEndTests : PageTest
{
  [TestMethod]
  [DataRow("codex")]
  [DataRow("opencode")]
  [DataRow("qwen-code")]
  [DataRow("claude-code")]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledHarnessCompletesThroughBrowserWithIsolatedProvider(string harness) =>
    VerifyAsync(harness, false);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchPreservesCrlf() => VerifyAsync("codex", true);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchRejectsWorkspaceEscape() => VerifyAsync("codex", true, true);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchWaitsForHostAskApproval() => VerifyAsync("codex", true, ask: true);

  [TestMethod]
  [DoNotParallelize]
  [Timeout(120_000, CooperativeCancellation = true)]
  public Task InstalledCodexPatchRejectsMoveDestinationEscape() => VerifyAsync("codex", true, moveOutside: true);

  private async Task VerifyAsync(string harness, bool patch, bool outside = false, bool ask = false, bool moveOutside = false)
  {
    if (Environment.GetEnvironmentVariable("AGENTIC_ROUTER_INSTALLED_HARNESS_E2E") != "1")
      Assert.Inconclusive("Opt in with real executable overrides; provider remains deterministic and local.");
    await using var environment = await TestEnvironment.StartAsync();
    environment.FakeOllama.InstalledHarnessProbe = true;
    environment.FakeOllama.InstalledCodexPatchProbe = patch;
    environment.FakeOllama.InstalledCodexOutsidePatchProbe = outside;
    environment.FakeOllama.InstalledCodexMoveOutsideProbe = moveOutside;
    var patchPath = outside
      ? Path.Combine(Path.GetDirectoryName(environment.WorkspaceDirectory)!, "outside-probe.txt")
      : Path.Combine(environment.WorkspaceDirectory, "crlf-probe.txt");
    if (patch) await File.WriteAllTextAsync(patchPath, "FIRST\r\nOLD_VALUE\r\nLAST\r\n");
    try
    {
      var availability = JsonNode.Parse(await environment.HttpClient.GetStringAsync("api/harnesses"))!
        .AsArray().Single(item => item!["definition"]!["id"]!.GetValue<string>() == harness)!["availability"]!;
      var version = availability["version"]!.GetValue<string>();
      Assert.IsFalse(version.Contains("fake", StringComparison.OrdinalIgnoreCase));
      TestContext.WriteLine($"{harness}: {version}");
      await Page.GotoAsync(environment.BaseUri.ToString());
      await Page.Locator("#model-selector").SelectOptionAsync("qwen3.8:27b-gpu0");
      await Page.Locator("[data-mode=execute]").ClickAsync();
      await Page.Locator("#send-strategy-toggle").ClickAsync();
      await Page.Locator("#send-strategy-menu [data-send-strategy=direct]").ClickAsync();
      await Page.Locator("#approval-policy").SelectOptionAsync(ask ? "ask" : "auto");
      await Page.Locator("#harness-selector").SelectOptionAsync(harness);
      await Page.Locator("#message-input").FillAsync(patch
        ? "Replace OLD_VALUE with NEW_VALUE in crlf-probe.txt using apply_patch, preserve other bytes, then respond INSTALLED_HARNESS_OK."
        : "Without tools or changes, respond INSTALLED_HARNESS_OK.");
      await Page.Locator("#send-button").ClickAsync();
      if (ask)
      {
        var approval = Page.Locator(".action-approval").Last;
        await Expect(approval).ToBeVisibleAsync();
        Assert.AreEqual("FIRST\r\nOLD_VALUE\r\nLAST\r\n", await File.ReadAllTextAsync(patchPath));
        await approval.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
      }
      await Expect(Page.Locator(".message.assistant .activity").Last)
        .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 90_000 });
      await Expect(Page.Locator(".message.assistant .assistant-answer").Last)
        .ToContainTextAsync("INSTALLED_HARNESS_OK");
      var id = await Page.EvaluateAsync<string>("() => state.latestExecutionSessionId");
      var review = JsonNode.Parse(await environment.HttpClient.GetStringAsync($"api/execution-sessions/{id}/review"))!;
      if (!outside && !moveOutside) Assert.IsTrue(review["summary"]!["state"]!.GetValue<string>().StartsWith("completed", StringComparison.Ordinal));
      Assert.IsNotEmpty(environment.FakeOllama.InstalledHarnessRequests);
      Assert.DoesNotContain("AuthorizationValid=False", environment.ApiOutput);
      if (patch)
      {
        CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes(outside || moveOutside
          ? "FIRST\r\nOLD_VALUE\r\nLAST\r\n" : "FIRST\r\nNEW_VALUE\r\nLAST\r\n"),
          await File.ReadAllBytesAsync(patchPath));
        Assert.IsGreaterThanOrEqualTo(2, environment.FakeOllama.InstalledHarnessRequests.Count);
        foreach (var request in environment.FakeOllama.InstalledHarnessRequests)
        {
          var tool = request.GetProperty("tools").EnumerateArray().Single(item =>
            item.TryGetProperty("name", out var name) && name.GetString() == "apply_patch");
          Assert.AreEqual("function", tool.GetProperty("type").GetString());
        }
        Assert.IsTrue(environment.FakeOllama.InstalledHarnessRequests.Skip(1).Any(request =>
          request.GetProperty("input").EnumerateArray().Any(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            && item.GetProperty("call_id").GetString() == "call_patch_probe")));
        if (moveOutside) Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(environment.WorkspaceDirectory)!, "escaped-move.txt")));
        if (outside || moveOutside)
          Assert.IsTrue(environment.FakeOllama.InstalledHarnessRequests.Skip(1).Any(request =>
            request.GetProperty("input").EnumerateArray().Any(item =>
              item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
              && item.GetProperty("call_id").GetString() == "call_patch_probe"
              && item.GetProperty("output").ToString().Contains("rejected", StringComparison.OrdinalIgnoreCase))));
      }
      foreach (var request in environment.FakeOllama.InstalledHarnessRequests)
      {
        Assert.AreEqual("qwen3.8:27b-gpu0", request.GetProperty("model").GetString());
        if (harness == "opencode") Assert.IsFalse(request.TryGetProperty("textVerbosity", out _));
      }
      if (harness == "qwen-code")
      {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(environment.DataDirectory, "qwen-code-runtime", "settings.json")))!;
        Assert.IsFalse(config["agents"]!["crossSessionMessaging"]!.GetValue<bool>());
      }
    }
    catch
    {
      TestContext.WriteLine(environment.ApiOutput);
      throw;
    }
  }
}
