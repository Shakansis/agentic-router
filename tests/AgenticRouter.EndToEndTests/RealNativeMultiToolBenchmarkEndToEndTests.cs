using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright.MSTest;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class RealNativeMultiToolBenchmarkEndToEndTests : PageTest
{
  [TestMethod]
  [TestCategory("RealModels")]
  [DoNotParallelize]
  [Timeout(7_200_000, CooperativeCancellation = true)]
  public async Task CompareSingleSequentialParallelAndBatchTools()
  {
    var endpoint = Environment.GetEnvironmentVariable("AR_NATIVE_BENCHMARK_URL");
    var model = Environment.GetEnvironmentVariable("AR_NATIVE_BENCHMARK_MODEL");
    var output = Environment.GetEnvironmentVariable("AR_NATIVE_BENCHMARK_OUTPUT");
    if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(output))
      Assert.Inconclusive("Requires an explicitly authorized isolated Host, installed model, and output path.");

    using var client = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(5) };
    var settings = JsonNode.Parse(await client.GetStringAsync("api/settings"))!.AsObject();
    var taskPath = Environment.GetEnvironmentVariable("AR_NATIVE_BENCHMARK_TASK_FILE");
    var task = string.IsNullOrWhiteSpace(taskPath) ? null : JsonNode.Parse(await File.ReadAllTextAsync(taskPath))!.AsObject();
    var scenarioCount = task is null ? 4 : 3;
    var thinking = task?["thinking"]?.GetValue<string>() ?? "none";
    var turnTimeout = task is null ? 240_000 : 1_200_000;
    var rows = new JsonArray();
    var repetitions = int.TryParse(Environment.GetEnvironmentVariable("AR_NATIVE_BENCHMARK_REPETITIONS"), out var count) ? count : 3;
    var startRepetition = int.TryParse(Environment.GetEnvironmentVariable("AR_NATIVE_BENCHMARK_START_REPETITION"), out var first) ? first - 1 : 0;
    if (startRepetition > 0)
      rows = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsArray();
    // Rotate order to reduce warm-cache/order bias. Warmup is performed by the launcher.
    for (var repeat = startRepetition; repeat < repetitions; repeat++)
      foreach (var scenario in Enumerable.Range(0, scenarioCount).Select(index => (index + repeat) % scenarioCount))
      {
        var mode = scenario == 2 ? "auto" : "off";
        settings["execution"]!["parallelToolExecution"] = mode;
        settings["execution"]!["maxConcurrentToolCalls"] = 4;
        using var saved = await client.PutAsync("api/settings", new StringContent(settings.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        saved.EnsureSuccessStatusCode();
        await Page.GotoAsync(endpoint);
        await Page.Locator("#model-selector").SelectOptionAsync(model);
        await Page.Locator("[data-mode=execute]").ClickAsync();
        await Page.Locator("#approval-policy").SelectOptionAsync("auto");
        await Page.Locator("#harness-selector").SelectOptionAsync("native");
        await Page.Locator("#thinking-selector").SelectOptionAsync(thinking);
        await Page.Locator("#send-strategy-toggle").ClickAsync();
        await Page.Locator("#send-strategy-menu [data-send-strategy=direct]").ClickAsync();
        // Consume a clone while the UI streams. Chromium can evict a long SSE
        // response before Network.getResponseBody is requested after completion.
        await Page.EvaluateAsync("""
          () => {
            const originalFetch = window.fetch;
            window.benchmarkEvents = [];
            window.fetch = async (...args) => {
              const response = await originalFetch(...args);
              if (new URL(String(args[0]), location.href).pathname === '/api/chat/stream') {
                const reader = response.clone().body.getReader();
                window.benchmarkCapture = (async () => {
                  const decoder = new TextDecoder();
                  let pending = '';
                  while (true) {
                    const { value, done } = await reader.read();
                    pending += decoder.decode(value, { stream: !done });
                    const lines = pending.split('\n');
                    pending = lines.pop();
                    for (const line of lines) {
                      if (!line.startsWith('data: ')) continue;
                      const event = JSON.parse(line.slice(6));
                      if (!['response.delta', 'reasoning.delta', 'inference.progress'].includes(event.type))
                        window.benchmarkEvents.push(event);
                    }
                    if (done) break;
                  }
                })();
              }
              return response;
            };
          }
          """);
        var instruction = scenario switch
        {
          0 => "Use read_file. Emit at most one tool call per response, and wait for its result before the next read.",
          3 => "Use read_files. Read the three files together with one read_files call, using files entries with path fields.",
          _ => "Use read_file. Emit three independent read_file calls together in ONE response."
        };
        var prompt = "Analyze Scheduler.cs, Batch.cs and Policy.cs in this workspace. These exact paths exist; no directory listing is needed. Read all three files. "
          + "For each, report the main public type and one concrete behavior from its source. Do not modify files or execute commands. "
          + "The read_file and read_files schemas are already offered; do not request them again. "
          + "Before the first tool call, include a short user-visible progress introduction in the SAME response as the call. "
          + "An introduction is not the final report. Continue until all three reads succeed, then finish with a report under 120 words. " + instruction;
        if (task is not null)
          prompt = task["objective"]!.GetValue<string>() + "\n" + (scenario == 0
            ? "Emit at most ONE tool call per response. Wait for each result before issuing the next call."
            : "Group independent inspections in the SAME response, up to four tool calls. Wait for earlier results when needed to determine the next arguments.");
        var window = await Page.EvaluateAsync<string>("historyWindowId");
        using var consent = await client.PostAsJsonAsync("api/history-files/git-decision", new { windowId = window, choice = "context-only" });
        consent.EnsureSuccessStatusCode();
        var responseTask = Page.WaitForResponseAsync(response => response.Url.EndsWith("/api/chat/stream", StringComparison.Ordinal), new() { Timeout = turnTimeout });
        var elapsed = Stopwatch.StartNew();
        await Page.Locator("#message-input").FillAsync(prompt);
        await Page.Locator("#send-button").ClickAsync();
        _ = await responseTask;
        await Expect(Page.Locator(".message.assistant .activity").Last)
          .ToHaveAttributeAsync("data-terminal", "true", new() { Timeout = turnTimeout });
        var wall = elapsed.ElapsedMilliseconds;
        var eventJson = await Page.EvaluateAsync<string>("async () => { await window.benchmarkCapture; return JSON.stringify(window.benchmarkEvents); }");
        var events = JsonNode.Parse(eventJson)!.AsArray().Select(item => item!.AsObject()).ToArray();
        var session = events.LastOrDefault(item => item["executionSession"] is JsonObject)?["executionSession"];
        JsonNode? review = null;
        if (session?["id"] is { } id)
          review = JsonNode.Parse(await client.GetStringAsync($"api/execution-sessions/{id.GetValue<string>()}/review"));
        var batches = review?["nativeToolBatches"]?.AsArray() ?? [];
        var answer = await Page.Locator(".message.assistant .assistant-answer").Last.InnerTextAsync();
        var readCalls = batches.SelectMany(batch => batch!["calls"]!.AsArray())
          .Where(call => call!["status"]!.GetValue<string>() == "completed")
          .Select(call => call!["tool"]!.GetValue<string>()).ToArray();
        var quality = answer.Contains("NativeToolScheduler", StringComparison.Ordinal)
          && answer.Contains("ToolExecutionBatch", StringComparison.Ordinal)
          && (answer.Contains("ExecutionTurnToolPolicy", StringComparison.Ordinal) || answer.Contains("ExecutionTurnToolScope", StringComparison.Ordinal))
          && (readCalls.Count(tool => tool == "read_file") >= 3 || readCalls.Contains("read_files"));
        if (task is not null)
          quality = readCalls.Count(tool => tool == "read_file") >= 8
            && readCalls.Contains("search_text") && !string.IsNullOrWhiteSpace(answer);
        var contexts = events.Where(item => item["contextUsage"] is JsonObject).Select(item => item["contextUsage"]!.AsObject())
          .GroupBy(item => item["inferenceSequence"]?.ToJsonString()).Select(group => group.Last()).ToArray();
        var row = new JsonObject
        {
          ["scenario"] = "ABCD"[scenario].ToString(),
          ["repetition"] = repeat + 1,
          ["model"] = model,
          ["mode"] = mode,
          ["prompt"] = prompt,
          ["wallMilliseconds"] = wall,
          ["qualityPassed"] = quality,
          ["acceptanceCriterion"] = task is null ? "Expected types and completed reads" : "Investigation activity only; no diagnosis scoring",
          ["answer"] = answer,
          ["inferenceCount"] = contexts.Length,
          ["contexts"] = new JsonArray(contexts.Select(item => item.DeepClone()).ToArray()),
          ["metrics"] = review?["summary"]?["inferenceMetrics"]?.DeepClone(),
          ["batches"] = batches.DeepClone(),
          ["actions"] = review?["actions"]?.DeepClone(),
          ["observedFiles"] = review?["files"]?.DeepClone(),
          ["activity"] = new JsonArray(events.Where(item => item["type"]?.GetValue<string>() != "response.delta")
            .Select(item => (JsonNode)new JsonObject
            {
              ["type"] = item["type"]?.DeepClone(),
              ["message"] = item["message"]?.DeepClone(),
              ["intention"] = item["intention"]?.DeepClone(),
              ["elapsedMilliseconds"] = item["elapsedMilliseconds"]?.DeepClone(),
              ["toolExecution"] = item["toolExecution"]?.DeepClone(),
              ["localAction"] = item["localAction"]?.DeepClone()
            }).ToArray()),
          ["errors"] = new JsonArray(events.Where(item => item["type"]?.GetValue<string>() == "error").Select(item => item.DeepClone()).ToArray())
        };
        rows.Add(row);
        await File.WriteAllTextAsync(output, rows.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        TestContext.WriteLine($"{row["scenario"]}/{repeat + 1}: {wall} ms, {contexts.Length} inferences, quality={quality}");
      }
    if (task is null)
      Assert.IsTrue(rows.All(row => row!["qualityPassed"]!.GetValue<bool>()), "Inspect the saved benchmark evidence for failed outcomes.");
    else
      Assert.HasCount(repetitions * scenarioCount, rows, "Every investigation turn must produce measurements, including failed outcomes.");
  }
}
