using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class RealHarnessSteeringEndToEndTests : PageTest
{
  // Only run against an isolated Host after explicit user permission for inference.
  [TestMethod]
  [DataRow("codex")]
  [DataRow("opencode")]
  [DoNotParallelize]
  [Timeout(180_000, CooperativeCancellation = true)]
  public async Task InstalledHarnessConsumesSteeringDuringGeneration(string harness)
  {
    await VerifySteeringAsync(harness,
      "Do not use any tools or modify files. Write the numbers from 1 to 200, one per line, without skipping or abbreviating. Start immediately with 1.", "1");
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(180_000, CooperativeCancellation = true)]
  public async Task InstalledQwenConsumesSteeringAtToolBoundary()
  {
    await VerifySteeringAsync("qwen-code",
      "First say STARTING_STEERING_PROBE. Then use list_files to inspect the workspace and read any text files you find, one tool call at a time. Finally summarize the contents. Do not modify files.", "STARTING_STEERING_PROBE");
  }

  [TestMethod]
  [DoNotParallelize]
  [Timeout(180_000, CooperativeCancellation = true)]
  public async Task InstalledQwenRejectsPromotionInsteadOfClaimingSameTurnDelivery()
  {
    await VerifySteeringAsync("qwen-code",
      "Do not use any tools or modify files. Write the numbers from 1 to 200, one per line, without skipping or abbreviating. Start immediately with 1.",
      "1", expectPromotion: true);
  }

  private async Task VerifySteeringAsync(string harness, string prompt, string initialOutput, bool expectPromotion = false)
  {
    var endpoint = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_STEERING_URL");
    var model = Environment.GetEnvironmentVariable("AGENTIC_ROUTER_REAL_STEERING_MODEL");
    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
      Assert.Inconclusive("Real inference requires an explicitly authorized isolated URL and installed model.");

    await Page.GotoAsync(endpoint);
    await Page.Locator("#model-selector").SelectOptionAsync(model);
    await Page.Locator("[data-mode=execute]").ClickAsync();
    await Page.Locator("#send-strategy-toggle").ClickAsync();
    await Page.Locator("#send-strategy-menu [data-send-strategy=direct]").ClickAsync();
    await Page.Locator("#approval-policy").SelectOptionAsync("auto");
    await Page.Locator("#harness-selector").SelectOptionAsync(harness);
    await Page.Locator("#message-input").FillAsync(prompt);
    await Page.Locator("#send-button").ClickAsync();
    try
    {
      // Observe real assistant output before steering: an admission during startup
      // would not demonstrate interruption of an in-flight model response.
      await Expect(Page.Locator(".message.assistant .assistant-answer").Last)
        .ToContainTextAsync(initialOutput, new() { Timeout = 90_000 });
      const string supplemental = "Stop listing numbers now. Do not use tools. Reply only with STEERING_APPLIED_7319 and finish.";
      await Page.Locator("#message-input").FillAsync(supplemental);
      await Page.Locator("#send-button").ClickAsync();
      var responseTask = Page.WaitForResponseAsync(response =>
        response.Url.EndsWith($"/api/harnesses/{harness}/steer", StringComparison.Ordinal));
      var elapsed = Stopwatch.StartNew();
      await Page.Locator(".message-buffer-action[data-action=\"steer\"]").ClickAsync();
      var response = await responseTask;
      var receipt = await response.TextAsync();
      TestContext.WriteLine($"{harness}: receipt after {elapsed.ElapsedMilliseconds}ms: {receipt}");
      using var json = JsonDocument.Parse(receipt);
      if (expectPromotion)
      {
        Assert.AreEqual(409, response.Status);
        Assert.AreEqual("qwen-code-steer-promoted", json.RootElement.GetProperty("code").GetString());
        await Expect(Page.Locator(".steered-message.failed")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".message-buffer-item-body")).ToContainTextAsync(supplemental);
        Assert.IsTrue(await Page.EvaluateAsync<bool>("() => state.messageQueuePaused"));
        await Expect(Page.Locator(".message.assistant .assistant-answer").Last)
          .Not.ToContainTextAsync("STEERING_APPLIED_7319");
        return;
      }
      Assert.AreEqual(200, response.Status);
      Assert.IsTrue(json.RootElement.GetProperty("accepted").GetBoolean());
      await Expect(Page.Locator(".message.assistant .assistant-answer").Last)
        .ToContainTextAsync("STEERING_APPLIED_7319", new() { Timeout = 60_000 });
      TestContext.WriteLine($"{harness}: generated the steering marker after {elapsed.ElapsedMilliseconds}ms using {model}.");
      await Expect(Page.Locator(".message.assistant .activity").Last)
        .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = 30_000 });
    }
    finally
    {
      if (await Page.Locator("#cancel-request").IsVisibleAsync())
        await Page.Locator("#cancel-request").ClickAsync();
    }
  }
}
