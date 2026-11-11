using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
[DoNotParallelize]
public sealed class RealOpenAiWebSearchEndToEndTests : PageTest
{
  [TestMethod]
  [TestCategory("RealProviders")]
  [Timeout(240_000, CooperativeCancellation = true)]
  public async Task AstraExecuteRecoversSameBackgroundResponseAfterRetrievalFailure()
  {
    if (Environment.GetEnvironmentVariable("AR_OPENAI_REAL_ALLOWED") != "1")
      Assert.Inconclusive("Real paid OpenAI acceptance requires explicit opt-in.");

    var sourceDirectory = Path.Combine(TestEnvironment.FindRepositoryRoot(), "AgenticRouter.Api", "data");
    var source = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(sourceDirectory, "settings.json")))!;
    var connection = source["cloudProviders"]!["openAi"]!.DeepClone();
    var reference = connection["secretReference"]!.GetValue<string>();
    Assert.IsTrue(connection["enabled"]!.GetValue<bool>() && reference.StartsWith("secret-", StringComparison.Ordinal));

    await using var proxy = new RealOpenAiRecordingProxy(retrievalFailures: 1);
    await using var environment = await TestEnvironment.StartAsync(proxy.BaseUri);
    Directory.CreateDirectory(Path.Combine(environment.DataDirectory, "secrets"));
    File.Copy(Path.Combine(sourceDirectory, "secrets", reference + ".bin"), Path.Combine(environment.DataDirectory, "secrets", reference + ".bin"));
    var settings = (await environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    settings["cloudProviders"]!["openAi"] = connection;
    settings["cloudProviders"]!["openAiOptions"] = source["cloudProviders"]!["openAiOptions"]!.DeepClone();
    settings["cloudProviders"]!["openAiOptions"]!["maximumOutputTokens"] = 8192;
    settings["cloudProviders"]!["openAiOptions"]!["serviceTier"] = "default";
    using (var saved = await environment.HttpClient.PutAsJsonAsync("api/settings", settings)) saved.EnsureSuccessStatusCode();
    using (var refreshed = await environment.HttpClient.PostAsync("api/cloud-providers/openai/models/refresh", null))
      refreshed.EnsureSuccessStatusCode();

    const string marker = "ASTRA_RECOVERY_8D24";
    await File.WriteAllTextAsync(Path.Combine(environment.WorkspaceDirectory, "validation-marker.txt"), marker);
    await Page.GotoAsync(environment.BaseUri.ToString());
    await Page.Locator("[data-mode=execute]").ClickAsync();
    await Page.Locator("#approval-policy").SelectOptionAsync("auto");
    await Page.Locator("#model-selector").SelectOptionAsync("openai::gpt-6-astra");
    await Page.Locator("#thinking-selector").SelectOptionAsync("high");
    var window = await Page.EvaluateAsync<string>("historyWindowId");
    using (var consent = await environment.HttpClient.PostAsJsonAsync("api/history-files/git-decision",
      new { windowId = window, choice = "context-only" })) consent.EnsureSuccessStatusCode();
    await Page.Locator("#message-input").FillAsync("Use read_file to read validation-marker.txt, then report the exact marker. Read only. Do not search the web.");
    var response = await Page.RunAndWaitForResponseAsync(() => Page.Locator("#send-button").ClickAsync(),
      result => result.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal), new() { Timeout = 120_000 });
    var body = await response.TextAsync().WaitAsync(TimeSpan.FromSeconds(180));
    var events = body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
      .Select(line => JsonNode.Parse(line[5..])!).ToArray();
    var evidence = proxy.Evidence;
    var terminal = events.LastOrDefault(item => (string?)item["type"] is "response.completed" or "response.failed" or "error");
    var created = evidence.Where(item => (string?)item["method"] == "POST" && (string?)item["path"] == "/v1/responses").ToArray();
    var failedGet = evidence.SingleOrDefault(item => (bool?)item["injected"] == true);
    var failedPath = (string?)failedGet?["path"];
    Assert.AreEqual("response.completed", (string?)terminal?["type"],
      $"{(string?)terminal?["message"]}; provider statuses: {string.Join(", ", evidence.Select(item => (string?)item["responseStatus"] ?? item["httpStatus"]?.ToString()))}");
    Assert.IsTrue(events.Any(item => (string?)item["inferenceProgress"]?["stage"] == "provider-retrying"));
    Assert.IsNotNull(failedPath);
    var original = created.Single(item => (string?)item["responsePath"] == failedPath);
    Assert.AreEqual("high", (string?)original["payload"]?["reasoning"]?["effort"]);
    Assert.IsTrue(original["payload"]!["background"]!.GetValue<bool>());
    Assert.IsFalse(original["payload"]!["store"]!.GetValue<bool>());
    var completed = evidence.Single(item => (string?)item["method"] == "GET"
      && (string?)item["responsePath"] == failedPath && (string?)item["responseStatus"] == "completed");
    var first = evidence.ToList().IndexOf(original);
    var last = evidence.ToList().IndexOf(completed);
    Assert.IsLessThan(last, first);
    Assert.IsFalse(evidence.Skip(first + 1).Take(last - first - 1).Any(item => (string?)item["method"] == "POST"),
      "The retrieval retry submitted another generation.");
    Assert.IsGreaterThan(0, (int?)completed["usage"]?["output_tokens"] ?? 0);
    var visibleResponse = string.Concat(events.Where(item => (string?)item["type"] == "response.delta")
      .Select(item => (string?)item["delta"])) + (string?)terminal?["responseTail"];
    StringAssert.Contains(visibleResponse, marker);
    TestContext.WriteLine($"Execute completed at high effort. One injected retrieval failure recovered the same response via GET. {created.Length} POSTs across the complete tool workflow.");
  }

  [TestMethod]
  [TestCategory("RealProviders")]
  [DataRow("none", false)]
  [DataRow("low", false)]
  [DataRow("low", true)]
  [Timeout(180_000, CooperativeCancellation = true)]
  public async Task TerraUsesRealHostedSearchAndRecordsEvidence(string effort, bool readFile)
  {
    if (Environment.GetEnvironmentVariable("AR_OPENAI_REAL_ALLOWED") != "1")
      Assert.Inconclusive("Real paid OpenAI acceptance requires explicit opt-in.");
    const string model = "gpt-6.1-terra";
    var sourceDirectory = Environment.GetEnvironmentVariable("AR_OPENAI_SOURCE_DATA")
      ?? Path.Combine(TestEnvironment.FindRepositoryRoot(), "AgenticRouter.Api", "data");
    var source = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(sourceDirectory, "settings.json")))!;
    var connection = source["cloudProviders"]!["openAi"]!.DeepClone();
    var reference = connection["secretReference"]!.GetValue<string>();
    Assert.IsTrue(connection["enabled"]!.GetValue<bool>() && reference.StartsWith("secret-", StringComparison.Ordinal));
    await using var proxy = new RealOpenAiRecordingProxy();
    await using var environment = await TestEnvironment.StartAsync(proxy.BaseUri);
    Directory.CreateDirectory(Path.Combine(environment.DataDirectory, "secrets"));
    File.Copy(Path.Combine(sourceDirectory, "secrets", reference + ".bin"), Path.Combine(environment.DataDirectory, "secrets", reference + ".bin"));
    var settings = (await environment.HttpClient.GetFromJsonAsync<JsonObject>("api/settings"))!;
    settings["cloudProviders"]!["openAi"] = connection;
    settings["cloudProviders"]!["openAiOptions"] = source["cloudProviders"]!["openAiOptions"]!.DeepClone();
    settings["cloudProviders"]!["openAiOptions"]!["maximumOutputTokens"] = 4096;
    settings["cloudProviders"]!["openAiOptions"]!["serviceTier"] = "default";
    settings["inferenceProfiles"]!["general-chat"]!["thinking"] = effort == "none" ? "disabled" : effort;
    using (var saved = await environment.HttpClient.PutAsJsonAsync("api/settings", settings)) saved.EnsureSuccessStatusCode();
    using (var refreshed = await environment.HttpClient.PostAsync("api/cloud-providers/openai/models/refresh", null))
      refreshed.EnsureSuccessStatusCode();
    var caseName = effort + (readFile ? "-host-file-tools" : "-hosted-search");
    var outputDirectory = Path.Combine(environment.RepositoryRoot, ".artifacts", "openai-web-search", "real");
    Directory.CreateDirectory(outputDirectory);
    await File.WriteAllTextAsync(Path.Combine(environment.WorkspaceDirectory, "reference-marker.txt"), "TERRA_TEST_MARKER_29");
    // Remove the temporary workspace profile only for the text-streaming scenario.
    // The local model/provider is a fake external boundary; no Ollama/GPU inference runs.
    if (!readFile)
    {
      var profiles = await environment.HttpClient.GetFromJsonAsync<JsonObject>("api/workspaces");
      foreach (var profile in profiles!["profiles"]!.AsArray())
      {
        using var removed = await environment.HttpClient.DeleteAsync($"api/workspaces/{profile!["id"]!.GetValue<string>()}?confirmed=true");
        removed.EnsureSuccessStatusCode();
      }
    }
    await Page.SetViewportSizeAsync(1400, 1000);
    await Page.GotoAsync(environment.BaseUri.ToString());
    await Page.Locator("#model-selector").SelectOptionAsync("openai::" + model);
    // The composer's per-turn selection overrides the stored inference profile.
    await Page.Locator("#thinking-selector").SelectOptionAsync(effort);
    var prompt = (readFile ? "First read reference-marker.txt using the available file tool and include its marker in the answer. " : "")
      + "Search the live web on developers.openai.com for GPT-6.1 Terra. Verify whether it is an official OpenAI model, "
      + "its supported reasoning effort levels and which API supports tools. Cite the official source. "
      + "Do not answer solely from memory. Keep the final answer under 120 words.";
    var report = new JsonObject { ["case"] = caseName, ["model"] = model, ["requestedEffort"] = effort, ["prompt"] = prompt };
    try
    {
      await Page.Locator("#message-input").FillAsync(prompt);
      var response = await Page.RunAndWaitForResponseAsync(
        () => Page.Locator("#send-button").ClickAsync(),
        response => response.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal), new() { Timeout = 120_000 });
      var body = await response.TextAsync().WaitAsync(TimeSpan.FromSeconds(120));
      var events = body.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
        .Select(line => JsonNode.Parse(line[5..])!).ToArray();
      var terminal = events.LastOrDefault(item => item["type"]?.GetValue<string>() is "response.completed" or "response.failed" or "error");
      report["terminalType"] = terminal?["type"]?.DeepClone();
      report["traceId"] = terminal?["diagnostic"]?["traceId"]?.DeepClone();
      report["answer"] = string.Concat(events.Where(item => item["type"]?.GetValue<string>() == "response.delta").Select(item => item["delta"]?.GetValue<string>()));
      report["eventTypes"] = new JsonArray(events.Select(item => item["type"]?.DeepClone()).ToArray());
      var ledger = Directory.GetFiles(Path.Combine(environment.DataDirectory, "usage"), "*.jsonl")
        .SelectMany(File.ReadAllLines).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonNode.Parse(line)!)
        .Where(item => item["providerId"]?.GetValue<string>() == "openai").ToArray();
      report["usage"] = new JsonArray(ledger.Select(item => (JsonNode)new JsonObject
      {
        ["inputTokens"] = item["inputTokens"]?.DeepClone(),
        ["outputTokens"] = item["outputTokens"]?.DeepClone(),
        ["hostedWebSearchCallCount"] = item["hostedWebSearchCallCount"]?.DeepClone(),
        ["searchQueryCount"] = item["searchQueryCount"]?.DeepClone(),
        ["citationCount"] = item["citationCount"]?.DeepClone(),
        ["activityAccuracy"] = item["activityAccuracy"]?.DeepClone(),
        ["providerSearchCost"] = item["providerSearchCost"]?.DeepClone()
      }).ToArray());
      report["wire"] = new JsonArray(proxy.Evidence.Select(item => (JsonNode)item.DeepClone()).ToArray());
      Assert.AreEqual("response.completed", report["terminalType"]?.GetValue<string>(), report.ToJsonString());
      Assert.IsGreaterThan(0, ledger.Sum(item => item["hostedWebSearchCallCount"]!.GetValue<int>()), "No real hosted search was observed.");
      Assert.IsGreaterThan(0, ledger.Sum(item => item["citationCount"]!.GetValue<int>()), "No actual citations were returned.");
      Assert.IsTrue(proxy.Evidence.Where(item => item["path"]?.GetValue<string>() == "/v1/responses")
        .All(item => item["payload"]!["reasoning"]?["effort"]?.GetValue<string>() == effort), "Requested reasoning effort changed.");
      if (readFile) StringAssert.Contains(report["answer"]!.GetValue<string>(), "TERRA_TEST_MARKER_29");
      await Page.ScreenshotAsync(new() { Path = Path.Combine(outputDirectory, caseName + ".png") });
      report["passed"] = true;
    }
    finally
    {
      report["wire"] = new JsonArray(proxy.Evidence.Select(item => (JsonNode)item.DeepClone()).ToArray());
      await File.WriteAllTextAsync(Path.Combine(outputDirectory, caseName + ".json"), report.ToJsonString(new() { WriteIndented = true }));
    }
  }
}
