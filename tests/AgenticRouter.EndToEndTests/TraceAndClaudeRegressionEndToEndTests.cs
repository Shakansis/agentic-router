using System.Net.Http.Json;
using System.Text.Json;
using AgenticRouter.Api.Execution;

namespace AgenticRouter.EndToEndTests;

[TestClass]
public sealed class TraceAndClaudeRegressionEndToEndTests
  : ChatEndToEndTestBase<TraceAndClaudeRegressionEndToEndTests>
{
  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BoundedTraceProjectionPreservesSyntheticProviderTimeoutTerminalTruth()
  {
    var events = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude diagnostic overflow synthetic timeout",
      $"browser-claude-diagnostic-timeout-{Guid.NewGuid():N}",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var terminal = events.Single(IsTerminalStreamEvent);
    Assert.AreEqual(
      "error",
      terminal["type"]!.GetValue<string>(),
      $"Observed event types: {string.Join(", ", events.Select(item => item["type"]!.GetValue<string>()))}"
    );
    var error = terminal["error"]!.AsObject();
    Assert.AreEqual("claude-code-provider-timeout", error["code"]!.GetValue<string>());
    Assert.AreNotEqual("claude-code-model-substitution", error["code"]!.GetValue<string>());
    var traceId = terminal["diagnostic"]!["traceId"]!.GetValue<string>();

    using var reportDocument = await GetTraceAsync(traceId);
    var report = reportDocument.RootElement;
    Assert.AreEqual("failed", report.GetProperty("status").GetString());
    Assert.AreEqual("claude-code-provider-timeout", report.GetProperty("failureCode").GetString());
    Assert.AreEqual("claude-code-harness", report.GetProperty("failureStage").GetString());
    Assert.IsTrue(report.GetProperty("truncated").GetBoolean());
    Assert.IsGreaterThan(
      report.GetProperty("returnedEvents").GetInt32(),
      report.GetProperty("totalEvents").GetInt32()
    );
    Assert.AreEqual(
      report.GetProperty("returnedEvents").GetInt32(),
      report.GetProperty("events").GetArrayLength()
    );
    Assert.IsTrue(report.GetProperty("events").EnumerateArray().Any(item =>
      item.GetProperty("status").GetString() == "failed"
      && item.GetProperty("code").GetString() == "claude-code-provider-timeout"
    ));

    using var invocation = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
      _environment.DataDirectory,
      "claude-code-runtime",
      "fake-claude-invocation.json"
    )));
    Assert.AreEqual("43200000", invocation.RootElement.GetProperty("apiTimeoutMs").GetString());
    Assert.AreEqual("43200000", invocation.RootElement.GetProperty("streamIdleTimeoutMs").GetString());
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task BoundedTraceProjectionPreservesCompletedTerminalTruth()
  {
    var events = await ExecuteHarnessStreamAsync(
      HarnessIds.ClaudeCode,
      "claude diagnostic overflow success",
      $"browser-claude-diagnostic-success-{Guid.NewGuid():N}",
      "qwen3.8:27b-gpu0"
    );

    Assert.HasCount(1, events.Where(IsTerminalStreamEvent));
    var terminal = events.Single(
      item => item["type"]!.GetValue<string>() == "response.completed"
    );
    var traceId = terminal["diagnostic"]!["traceId"]!.GetValue<string>();

    using var reportDocument = await GetTraceAsync(traceId);
    var report = reportDocument.RootElement;
    Assert.AreEqual("completed", report.GetProperty("status").GetString());
    Assert.AreEqual(JsonValueKind.Null, report.GetProperty("failureCode").ValueKind);
    Assert.IsTrue(report.GetProperty("completed").GetBoolean());
    Assert.IsTrue(report.GetProperty("truncated").GetBoolean());
    Assert.IsGreaterThan(
      report.GetProperty("returnedEvents").GetInt32(),
      report.GetProperty("totalEvents").GetInt32()
    );
    Assert.IsTrue(report.GetProperty("events").EnumerateArray().Any(item =>
      item.GetProperty("status").GetString() == "completed"
      && item.GetProperty("completed").GetBoolean()
    ));
  }

  [TestMethod]
  [Timeout(60_000, CooperativeCancellation = true)]
  public async Task IncidentJournalRebuildsItsTraceIndexOnceAfterRestart()
  {
    var firstTrace = await SendJournalProbeAsync("before-restart");
    using var firstReportDocument = await GetTraceAsync(firstTrace);
    var firstTotal = firstReportDocument.RootElement.GetProperty("totalEvents").GetInt32();

    await _environment.RestartApplicationAsync();

    var secondTrace = await SendJournalProbeAsync("after-restart-one");
    using var secondReportDocument = await GetTraceAsync(secondTrace);
    var secondReport = secondReportDocument.RootElement;
    var secondMetrics = secondReport.GetProperty("journalMetrics");
    Assert.AreEqual(1, secondMetrics.GetProperty("traceIndexRebuilds").GetInt64());
    Assert.IsGreaterThanOrEqualTo(
      firstTotal,
      secondMetrics.GetProperty("traceIndexRecordsScanned").GetInt64()
    );
    Assert.IsTrue(secondReport.GetProperty("events").EnumerateArray().Any(item =>
      item.GetProperty("status").GetString() == "completed"
      && item.GetProperty("requestElapsedMilliseconds").ValueKind == JsonValueKind.Number
    ));

    var indexedRecords = secondMetrics.GetProperty("traceIndexRecordsScanned").GetInt64();
    var rebuilds = secondMetrics.GetProperty("traceIndexRebuilds").GetInt64();
    var appendAttempts = secondMetrics.GetProperty("appendAttempts").GetInt64();
    var thirdTrace = await SendJournalProbeAsync("after-restart-two");
    using var thirdReportDocument = await GetTraceAsync(thirdTrace);
    var thirdMetrics = thirdReportDocument.RootElement.GetProperty("journalMetrics");
    Assert.AreEqual(rebuilds, thirdMetrics.GetProperty("traceIndexRebuilds").GetInt64());
    Assert.AreEqual(indexedRecords, thirdMetrics.GetProperty("traceIndexRecordsScanned").GetInt64());
    Assert.IsGreaterThan(
      appendAttempts,
      thirdMetrics.GetProperty("appendAttempts").GetInt64()
    );

    using var retainedFirst = await GetTraceAsync(firstTrace);
    Assert.AreEqual(firstTrace, retainedFirst.RootElement.GetProperty("traceId").GetString());
  }

  private static async Task<string> SendJournalProbeAsync(string marker)
  {
    using var response = await _environment.HttpClient.PostAsJsonAsync(
      "api/chat/stream",
      new
      {
        message = $"Incident journal index probe {marker}.",
        model = "alpha:latest",
        history = Array.Empty<object>(),
        interactionMode = "chat",
        approvalPolicy = "ask",
        browserSessionId = $"browser-journal-index-{marker}-{Guid.NewGuid():N}"
      }
    );
    response.EnsureSuccessStatusCode();
    var events = ParseSseEvents(await response.Content.ReadAsStringAsync());
    return events.Last(item => item["type"]!.GetValue<string>() == "response.completed")
      ["diagnostic"]!["traceId"]!.GetValue<string>();
  }

  private static async Task<JsonDocument> GetTraceAsync(string traceId)
  {
    using var response = await _environment.HttpClient.GetAsync(
      $"api/diagnostics/traces/{Uri.EscapeDataString(traceId)}"
    );
    var body = await response.Content.ReadAsStringAsync();
    response.EnsureSuccessStatusCode();
    return JsonDocument.Parse(body);
  }
}
