using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class OpenAiEndToEndTests : ChatEndToEndTestBase<OpenAiEndToEndTests>
{
  [TestMethod]
  [DataRow("supervised")]
  [DataRow("auto")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiExecuteUsesDirectWhenSupervisionRequiresLocalModel(string strategy)
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-direct-test");
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspaceId}/history", new { enabled = true }))
      enabled.EnsureSuccessStatusCode();
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), "OpenAI Execute continuation marker");
    await Page.GotoAsync("/");
    await SetExecuteModeAsync("auto");
    await Page.Locator("#model-selector").SelectOptionAsync("openai::gpt-6-sol");
    await Page.Locator("#send-strategy-toggle").ClickAsync();
    await Page.Locator($"[data-send-strategy={strategy}]").ClickAsync();
    const string objective = """
      Chat workspace read request. Read chat-readable.txt without changing files.
      - Inspect item one
      - Inspect item two
      - Inspect item three
      - Inspect item four
      - Inspect item five
      - Inspect item six
      """;
    var response = await Page.RunAndWaitForResponseAsync(() => StartMessageAsync(objective),
      result => result.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal));
    var events = ParseSseEvents(await response.TextAsync());
    var decision = events.Single(item => (string?)item["type"] == "supervision.cloud-model-direct");
    Assert.AreEqual("openai::gpt-6-sol", (string?)decision["selectedModel"]);
    Assert.IsFalse(events.Any(item => (string?)item["type"] is "supervision.run-started" or "error"));
    Assert.HasCount(1, events.Where(item => (string?)item["type"] == "response.completed"));
    Assert.IsTrue(_environment.FakeCloud.Requests.Any(item => item.Method == "POST" && item.Path == "/openai/v1/responses"
      && item.Body.Contains("function_call_output", StringComparison.Ordinal)
      && item.Body.Contains("OpenAI Execute continuation marker", StringComparison.Ordinal)));
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync("Using Direct Execute with the selected cloud model");
  }

  [TestMethod]
  [DataRow(false, false)]
  [DataRow(true, false)]
  [DataRow(true, true)]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiBackgroundRecoveryRetrievesTheSameResponse(bool manual, bool stop)
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-recovery-test");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("openai::gpt-6-sol");
    var scenario = manual ? "manual" : "auto";
    await StartMessageAsync($"trigger-openai-recovery-{scenario}");
    await Expect(Page.Locator("[data-inference-stage=provider-queued]")).ToBeVisibleAsync();
    await Expect(Page.Locator("[data-inference-stage=provider-in-progress]")).ToBeVisibleAsync();

    var pending = Page.Locator(".recovery-decision [data-recovery-option=retry]");
    if (manual)
    {
      await Expect(pending).ToContainTextAsync("Retry response retrieval");
      Assert.AreEqual(5, _environment.FakeCloud.Requests.Count(request => request.Path == "/openai/v1/responses/resp_recovery_manual"));
      if (stop)
        await Page.Locator(".recovery-decision [data-recovery-option=stop]").ClickAsync();
      else
        await pending.ClickAsync();
    }
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync(
      stop ? "OpenAI response recovery was stopped by the user." : "Recovered the same OpenAI response.");
    Assert.AreEqual(stop ? 5 : manual ? 6 : 4, _environment.FakeCloud.Requests.Count(request =>
      request.Method == "GET" && request.Path == $"/openai/v1/responses/resp_recovery_{scenario}"));
    Assert.AreEqual(1, _environment.FakeCloud.Requests.Count(request =>
      request.Method == "POST" && request.Path == "/openai/v1/responses" && request.Body.Contains($"trigger-openai-recovery-{scenario}", StringComparison.Ordinal)));
    Assert.AreEqual(stop ? 1 : 0, _environment.FakeCloud.Requests.Count(request =>
      request.Method == "POST" && request.Path == "/openai/v1/responses/resp_recovery_manual/cancel"));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiMenuProtectsKeyAndPersistsRequestOptionsAcrossRestart()
  {
    const string key = "sk-proj-openai-menu-test";
    await Page.GotoAsync("/");
    await Page.Locator("#open-settings").ClickAsync();
    await Page.Locator("[data-settings-target=providers]").ClickAsync();
    var card = Page.Locator(".cloud-provider-card[data-provider=openai]");
    await card.Locator("summary").ClickAsync();
    await card.Locator("[data-cloud-key=openai]").FillAsync(key);
    await card.Locator("[data-cloud-action=save-key]").ClickAsync();
    await Expect(card.Locator("[data-cloud-action=test]")).ToBeEnabledAsync();
    await Expect(card.Locator("[data-cloud-key=openai]")).ToHaveValueAsync("");
    await card.Locator("[data-cloud-action=refresh]").ClickAsync();
    await Page.Locator("#app-modal-confirm").ClickAsync();
    await Expect(card).ToContainTextAsync("Connected");
    await card.Locator("[data-openai-option=organizationId]").FillAsync("org-openai-test");
    await card.Locator("[data-openai-option=projectId]").FillAsync("proj-openai-test");
    await card.Locator("[data-openai-option=maximumOutputTokens]").FillAsync("2048");
    await card.Locator("[data-openai-option=serviceTier]").SelectOptionAsync("default");
    await card.Locator("[data-openai-option=verbosity]").SelectOptionAsync("low");
    await card.Locator("[data-cloud-billing=openai]").SelectOptionAsync("paid");
    await Page.Locator("#save-settings").ClickAsync();
    await Expect(Page.Locator("#save-status")).ToHaveTextAsync("Saved");
    await Expect(Page.Locator("#default-model optgroup[label=OpenAI]")).ToContainTextAsync("gpt-6-sol");
    await Expect(Page.Locator("#default-model option[value='openai::text-embedding-3-small']")).ToHaveCountAsync(0);

    var diskSettings = await File.ReadAllTextAsync(_environment.SettingsPath);
    Assert.DoesNotContain(key, diskSettings);
    using var yaml = await _environment.HttpClient.GetAsync("api/settings/yaml");
    Assert.DoesNotContain(key, await yaml.Content.ReadAsStringAsync());
    foreach (var path in Directory.GetFiles(Path.Combine(_environment.DataDirectory, "secrets"), "*.bin"))
      Assert.DoesNotContain(key, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));

    await _environment.RestartApplicationAsync();
    await Page.ReloadAsync();
    await Page.Locator("#open-settings").ClickAsync();
    await Page.Locator("[data-settings-target=providers]").ClickAsync();
    if (await card.GetAttributeAsync("open") is null)
      await card.Locator("summary").ClickAsync();
    await Expect(card.Locator("[data-openai-option=projectId]")).ToHaveValueAsync("proj-openai-test");
    await Expect(card.Locator("[data-openai-option=maximumOutputTokens]")).ToHaveValueAsync("2048");
    await Page.Locator("#cancel-settings").ClickAsync();
    await Page.Locator("#model-selector").SelectOptionAsync("openai::gpt-6-sol");
    await SendMessageAsync("Hello from OpenAI menu acceptance.");
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync("openai cloud answer");
    var request = _environment.FakeCloud.Requests.Last(item => item.Path == "/openai/v1/responses");
    Assert.AreEqual("Bearer " + key, request.Authorization);
    Assert.AreEqual("org-openai-test", request.OpenAiOrganization);
    Assert.AreEqual("proj-openai-test", request.OpenAiProject);
    using var body = JsonDocument.Parse(request.Body);
    Assert.IsFalse(body.RootElement.GetProperty("store").GetBoolean());
    Assert.AreEqual(2048, body.RootElement.GetProperty("max_output_tokens").GetInt32());
    Assert.AreEqual("default", body.RootElement.GetProperty("service_tier").GetString());
    Assert.AreEqual("low", body.RootElement.GetProperty("text").GetProperty("verbosity").GetString());
    using var removed = await _environment.HttpClient.DeleteAsync("api/cloud-providers/openai/key?confirmed=true");
    removed.EnsureSuccessStatusCode();
    Assert.DoesNotContain(key, await removed.Content.ReadAsStringAsync());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiResponsesUseReasoningAndRecordProviderUsage()
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-stream-test");
    var settings = await GetSettingsJsonAsync();
    settings["inferenceProfiles"]!["general-chat"]!["thinking"] = "high";
    settings["inferenceProfiles"]!["general-chat"]!["temperature"] = 0.3;
    using (var saved = await PutSettingsJsonAsync(settings)) saved.EnsureSuccessStatusCode();
    _environment.FakeCloud.Reset();
    var image = await ApproveImageAsync();
    var events = await PostChatStreamAsync("Hello OpenAI reasoning. trigger-openai-estimate-delay", "openai::gpt-6.1-sol", images: [image]);
    StringAssert.Contains(events, "openai cloud answer");
    StringAssert.Contains(events, "\"type\":\"response.completed\"");
    var metrics = ParseSseEvents(events).Last(item => item["chatSummary"]?["inferenceMetrics"] is not null)["chatSummary"]!["inferenceMetrics"]!;
    Assert.AreEqual("request-estimate", metrics["generationTimingSource"]!.GetValue<string>());
    Assert.AreEqual(8L, metrics["outputTokens"]!.GetValue<long>());
    Assert.IsGreaterThanOrEqualTo(100d, metrics["generationMilliseconds"]!.GetValue<double>());
    Assert.IsGreaterThan(0d, metrics["tokensPerSecond"]!.GetValue<double>());
    var request = _environment.FakeCloud.Requests.Last(item => item.Path == "/openai/v1/responses");
    using var payload = JsonDocument.Parse(request.Body);
    Assert.AreEqual("high", payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    Assert.IsFalse(payload.RootElement.TryGetProperty("temperature", out _));
    Assert.IsFalse(payload.RootElement.TryGetProperty("seed", out _));
    Assert.IsTrue(payload.RootElement.GetProperty("stream").GetBoolean());
    StringAssert.Contains(request.Body, "input_image");
    using var usage = await _environment.HttpClient.GetAsync("api/usage/summary?window=rolling-hour&providerId=openai");
    usage.EnsureSuccessStatusCode();
    var usageText = await usage.Content.ReadAsStringAsync();
    StringAssert.Contains(usageText, "provider");
    var ledger = Directory.GetFiles(Path.Combine(_environment.DataDirectory, "usage"), "*.jsonl")
      .SelectMany(File.ReadAllLines).Where(line => !string.IsNullOrWhiteSpace(line))
      .Select(line => JsonNode.Parse(line)!.AsObject()).Where(item => item["providerId"]?.GetValue<string>() == "openai").ToArray();
    Assert.IsNotEmpty(ledger);
    var exact = ledger.Last();
    Assert.AreEqual(12L, exact["inputTokens"]!.GetValue<long>());
    Assert.AreEqual(8L, exact["outputTokens"]!.GetValue<long>());
    Assert.AreEqual(2L, exact["cachedInputTokens"]!.GetValue<long>());
    Assert.AreEqual(5L, exact["reasoningTokens"]!.GetValue<long>());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiWithoutReportedUsageDoesNotInventThroughput()
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-no-usage-test");
    var events = await PostChatStreamAsync("trigger-openai-no-usage", "openai::gpt-6.1-terra");
    var metrics = ParseSseEvents(events).Last(item => item["chatSummary"]?["inferenceMetrics"] is not null)["chatSummary"]!["inferenceMetrics"]!;
    Assert.IsNull(metrics["outputTokens"]);
    Assert.IsNull(metrics["generationMilliseconds"]);
    Assert.IsNull(metrics["tokensPerSecond"]);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiToolCallsRoundTripThroughHostWorkspaceRead()
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-tools-test");
    using (var conformance = await _environment.HttpClient.PostAsJsonAsync("api/models/conformance", new
    {
      model = "openai::gpt-6-sol",
      restoreResidentModel = false,
      externalProviderPermissionGranted = true
    }))
    {
      var text = await conformance.Content.ReadAsStringAsync();
      Assert.AreEqual(HttpStatusCode.OK, conformance.StatusCode, text);
      Assert.IsTrue(JsonNode.Parse(text)!["passed"]!.GetValue<bool>(), text);
    }
    var workspaceId = await ActiveWorkspaceIdAsync();
    using (var enabled = await _environment.HttpClient.PutAsJsonAsync($"api/workspaces/{workspaceId}/history", new { enabled = true }))
      enabled.EnsureSuccessStatusCode();
    await File.WriteAllTextAsync(Path.Combine(_environment.WorkspaceDirectory, "chat-readable.txt"), "OpenAI protected workspace read");
    await Page.GotoAsync("/");
    await Page.Locator("#model-selector").SelectOptionAsync("openai::gpt-6-sol");
    await SendMessageAsync("hosted web search fixture; chat workspace read request: read chat-readable.txt");
    await Expect(Page.Locator(".message.assistant").Last).ToContainTextAsync("OpenAI read the workspace file.");
    var throughput = Page.Locator(".chat-session-footer [data-metric=throughput]").Last;
    await Expect(throughput).ToHaveAttributeAsync("data-metric-state", "estimated");
    await Expect(throughput).ToContainTextAsync("≈");
    await Expect(throughput).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex("Estimated average.*hosted tools"));
    Assert.AreEqual("rgb(233, 185, 73)", await throughput.EvaluateAsync<string>("element => getComputedStyle(element).color"));
    var result = _environment.FakeCloud.Requests.Last(item => item.Path == "/openai/v1/responses");
    using var payload = JsonDocument.Parse(result.Body);
    Assert.IsFalse(payload.RootElement.TryGetProperty("stream", out _));
    var toolResult = payload.RootElement.GetProperty("input").EnumerateArray().Single(item =>
      item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
    Assert.AreEqual("openai-read-call", toolResult.GetProperty("call_id").GetString());
    StringAssert.Contains(toolResult.GetProperty("output").GetString()!, "OpenAI protected workspace read");
    var nativeTypes = payload.RootElement.GetProperty("input").EnumerateArray()
      .Where(item => item.TryGetProperty("type", out _)).Select(item => item.GetProperty("type").GetString()).ToArray();
    CollectionAssert.AreEqual(new[] { "web_search_call", "web_search_call", "reasoning", "message", "function_call", "function_call_output" }, nativeTypes);
    var reasoning = payload.RootElement.GetProperty("input").EnumerateArray().Single(item =>
      item.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
    Assert.AreEqual("rs_fake_openai", reasoning.GetProperty("id").GetString());
    Assert.IsFalse(reasoning.TryGetProperty("encrypted_content", out _));
    var sessionFiles = Directory.GetFiles(Path.Combine(_environment.DataDirectory, "workspaces", workspaceId, "sessions"), "*.json");
    Assert.IsNotEmpty(sessionFiles);
    var conversationId = await Page.EvaluateAsync<string>("() => state.conversationSessionId");
    var saved = await _environment.HttpClient.GetFromJsonAsync<JsonObject>($"api/sessions/{conversationId}?workspaceId={workspaceId}");
    var metrics = saved!["messages"]!.AsArray().Last(item => item!["role"]!.GetValue<string>() == "assistant")!
      ["timeline"]!.AsArray().Single(item => item!["type"]!.GetValue<string>() == "response.completed")!
      ["chatSummary"]!["inferenceMetrics"]!;
    Assert.AreEqual("request-estimate", metrics["generationTimingSource"]!.GetValue<string>());
    Assert.IsGreaterThan(0d, metrics["tokensPerSecond"]!.GetValue<double>());
    foreach (var path in Directory.GetFiles(_environment.DataDirectory, "*.json", SearchOption.AllDirectories))
      Assert.DoesNotContain("rs_fake_openai", await File.ReadAllTextAsync(path));
    Assert.DoesNotContain("rs_fake_openai", _environment.ApiOutput);
    await _environment.RestartApplicationAsync();
    await Page.ReloadAsync();
    await Page.Locator("#recent-sessions").EvaluateAsync("element => element.open = true");
    await Page.Locator($".session-entry[data-session-id='{conversationId}'] .session-entry-content").ClickAsync();
    await Expect(Page.Locator(".chat-session-footer [data-metric=throughput]").Last)
      .ToHaveAttributeAsync("data-metric-state", "estimated");
  }

  [TestMethod]
  [DataRow("gpt-6-sol", "max", "max")]
  [DataRow("gpt-4.1", "auto", null)]
  [DataRow("gpt-6.1-terra", "disabled", "none")]
  [DataRow("gpt-6.1-terra", "auto", null)]
  [DataRow("gpt-6.1-terra", "low", "low")]
  [DataRow("gpt-6.1-terra", "max", "max")]
  [DataRow("gpt-6.1-sol", "disabled", null)]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiProfilesApplyModelSupportedControls(string model, string thinking, string? expectedEffort)
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-controls-test");
    var settings = await GetSettingsJsonAsync();
    settings["inferenceProfiles"]!["general-chat"]!["thinking"] = thinking;
    settings["inferenceProfiles"]!["general-chat"]!["temperature"] = 0.35;
    settings["inferenceProfiles"]!["general-chat"]!["topP"] = 0.8;
    using (var saved = await PutSettingsJsonAsync(settings)) saved.EnsureSuccessStatusCode();
    var events = await PostChatStreamAsync("Hello model controls.", "openai::" + model);
    StringAssert.Contains(events, "\"type\":\"response.completed\"");
    var request = _environment.FakeCloud.Requests.Last(item => item.Path == "/openai/v1/responses");
    using var payload = JsonDocument.Parse(request.Body);
    if (expectedEffort is not null)
      Assert.AreEqual(expectedEffort, payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    else
    {
      Assert.IsFalse(payload.RootElement.TryGetProperty("reasoning", out _));
      if (model == "gpt-4.1")
      {
        Assert.AreEqual(0.35, payload.RootElement.GetProperty("temperature").GetDouble());
        Assert.AreEqual(0.8, payload.RootElement.GetProperty("top_p").GetDouble());
      }
    }
  }

  [TestMethod]
  [DataRow("trigger-openai-incomplete")]
  [DataRow("trigger-openai-truncated")]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiUnfinishedStreamsDoNotReportCompletion(string trigger)
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-incomplete-test");
    await Page.GotoAsync("/");
    var image = await ApproveImageAsync();
    var events = await PostChatStreamAsync(trigger, "openai::gpt-6-sol", images: [image]);
    StringAssert.Contains(events, trigger == "trigger-openai-truncated" ? "provider-disconnected" : "provider-response-incomplete");
    Assert.DoesNotContain("\"type\":\"response.completed\"", events);
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiRejectsInvalidOptionsAtomicallyAndSanitizesCredentialErrors()
  {
    await Page.GotoAsync("/");
    var settings = await GetSettingsJsonAsync();
    settings["cloudProviders"]!["openAiOptions"]!["projectId"] = "proj-test\r\nX-Unsafe: injected";
    settings["cloudProviders"]!["openAiOptions"]!["maximumOutputTokens"] = 0;
    settings["cloudProviders"]!["openAiOptions"]!["serviceTier"] = "unsupported";
    using var rejected = await PutSettingsJsonAsync(settings);
    Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
    var current = await GetSettingsJsonAsync();
    Assert.AreEqual("auto", current["cloudProviders"]!["openAiOptions"]!["serviceTier"]!.GetValue<string>());
    const string key = "sk-rejected-openai-test";
    using (var saved = await _environment.HttpClient.PutAsJsonAsync("api/cloud-providers/openai/key", new { apiKey = key }))
      saved.EnsureSuccessStatusCode();
    using var tested = await _environment.HttpClient.PostAsync("api/cloud-providers/openai/test", null);
    Assert.IsFalse(tested.IsSuccessStatusCode);
    var error = await tested.Content.ReadAsStringAsync();
    StringAssert.Contains(error, "provider-key-invalid");
    Assert.DoesNotContain(key, error);
  }

  private static async Task<object> ApproveImageAsync()
  {
    using var approval = await _environment.HttpClient.PostAsJsonAsync("api/privacy/cloud-images/approve", new
    {
      browserSessionId = "browser-v095",
      provider = "openai"
    });
    approval.EnsureSuccessStatusCode();
    const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    return new { id = "openai-image", fileName = "pixel.png", mimeType = "image/png", base64Data = png, declaredBytes = Convert.FromBase64String(png).LongLength };
  }

  [TestMethod]
  [DataRow(false)]
  [DataRow(true)]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiHostedSearchIsAvailableCitedAndCountedInToolAndStreamingPaths(bool streaming)
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-web-test");
    await Page.GotoAsync("/");
    var events = await PostChatStreamAsync("hosted web search fixture: verify current official model information.",
      "openai::gpt-6.1-terra", images: streaming ? [await ApproveImageAsync()] : null);
    StringAssert.Contains(events, "web.search-completed");
    StringAssert.Contains(events, "https://developers.openai.com/api/docs/models/gpt-6.1-terra");
    StringAssert.Contains(events, "\"type\":\"response.completed\"");
    var request = _environment.FakeCloud.Requests.Last(item => item.Path == "/openai/v1/responses");
    var payload = JsonNode.Parse(request.Body)!;
    Assert.IsTrue(payload["tools"]!.AsArray().Any(tool => tool!["type"]!.GetValue<string>() == "web_search"));
    Assert.AreEqual(streaming, payload["stream"]?.GetValue<bool>() ?? false);
    if (!streaming) Assert.IsTrue(payload["tools"]!.AsArray().Any(tool => tool!["type"]!.GetValue<string>() == "function"));
    var record = Directory.GetFiles(Path.Combine(_environment.DataDirectory, "usage"), "*.jsonl")
      .SelectMany(File.ReadAllLines).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonNode.Parse(line)!)
      .Last(item => item["providerId"]?.GetValue<string>() == "openai");
    Assert.AreEqual(2, record["hostedWebSearchCallCount"]!.GetValue<int>());
    Assert.AreEqual(2, record["searchQueryCount"]!.GetValue<int>());
    Assert.AreEqual(1, record["groundedRequestCount"]!.GetValue<int>());
    Assert.AreEqual(1, record["citationCount"]!.GetValue<int>());
    Assert.AreEqual("exact", record["activityAccuracy"]!.GetValue<string>());
    Assert.IsNull(record["providerSearchCost"], "A hosted tool event is not proof of the amount billed.");
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task OpenAiStaleHealthIsNotDisplayedAsDisconnectedAfterSuccessfulInference()
  {
    await ConnectFakeCloudAsync("openai", "sk-openai-health-test");
    await Page.GotoAsync("/");
    var events = await PostChatStreamAsync("Hello stale health.", "openai::gpt-6.1-terra");
    StringAssert.Contains(events, "\"type\":\"response.completed\"");
    // Exercise real API staleness with its supported one-second threshold.
    await Task.Delay(1_100);
    await Page.EvaluateAsync("""
      async () => {
        state.providerHealth = await fetchJson('/api/provider-health?staleAfterSeconds=1');
        renderProviderHealth();
      }
      """);
    await Page.Locator("#external-app-warnings-summary").ClickAsync();
    var warning = Page.Locator("[data-external-app=openai]");
    await Expect(warning).ToContainTextAsync("Status is stale");
    await Expect(warning).Not.ToContainTextAsync("Unavailable");
  }
}
