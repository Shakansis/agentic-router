using System.Text.Json;
using System.Text.RegularExpressions;

// Shared deterministic behavior at the external harness boundary. No Host services
// or session mapping are mocked; the native IDs come from each actual adapter.
internal static class SupervisionSessionFixture
{
  public static async Task<string?> RespondAsync(
    string text, string sessionId, string runtime, string workspace)
  {
    if (!text.Contains("session isolation fixture", StringComparison.Ordinal)) return null;
    var phase = text.Contains("SUPERVISION_COMPLETE_V1", StringComparison.Ordinal) ? "complete"
      : text.Contains("SUPERVISION_VERIFY", StringComparison.Ordinal) ? "verify"
      : text.Contains("SUPERVISION_WORKER_V1", StringComparison.Ordinal)
        || text.Contains("SUPERVISION_CORRECTION_V1", StringComparison.Ordinal) ? "worker"
      : text.Contains("SUPERVISION_DECOMPOSE_V1", StringComparison.Ordinal) ? "decompose"
      : "direct";
    var item = Regex.Match(text, @"(?:Work item: |Active work item )(work-\d+)").Groups[1].Value;
    var log = Path.Combine(runtime, "fake-session-isolation.jsonl");
    var previous = File.Exists(log) ? await File.ReadAllLinesAsync(log) : [];
    await File.AppendAllTextAsync(log, JsonSerializer.Serialize(new { sessionId, phase, item, text, processId = Environment.ProcessId }) + "\n");
    if (phase == "direct") return "Inspected the workspace for the session isolation fixture.";
    if (phase == "decompose")
      return JsonSerializer.Serialize(new
      {
        decision = "dispatch_work",
        items = Enumerable.Range(1, 2).Select(index => new
        {
          objective = $"session isolation fixture: create isolation-{index:000}.txt containing isolated."
            + (text.Contains("pause for resume", StringComparison.Ordinal) ? " pause for resume" : "")
            + (text.Contains("corrective worker", StringComparison.Ordinal) ? " corrective worker" : ""),
          acceptanceCriteria = new[] { Criterion(index) },
          evidencePaths = new[] { $"isolation-{index:000}.txt" }
        })
      });
    if (phase == "worker")
    {
      var index = int.Parse(item.AsSpan("work-".Length));
      var correction = text.Contains("corrective worker", StringComparison.Ordinal) && index == 1;
      var priorWorker = previous.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Any(record =>
        record.GetProperty("phase").GetString() == "worker" && record.GetProperty("item").GetString() == item);
      await File.WriteAllTextAsync(Path.Combine(workspace, $"isolation-{index:000}.txt"), correction && !priorWorker ? "pending" : "isolated");
      return $"Created isolation-{index:000}.txt with the requested content.";
    }
    if (phase == "verify")
    {
      // Fail concretely if the adapter has brought worker-private native history
      // back to the Supervisor. Host-provided evidence is still permitted.
      var contaminated = previous.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Any(record =>
        record.GetProperty("phase").GetString() == "worker"
        && record.GetProperty("sessionId").GetString() == sessionId);
      if (contaminated)
        return JsonSerializer.Serialize(new { decision = "stop_blocked", summary = "Worker native history leaked into Supervisor." });
      if (text.Contains("pause for resume", StringComparison.Ordinal)
        && !previous.Any(line => line.Contains("\"phase\":\"verify\"", StringComparison.Ordinal)))
        return JsonSerializer.Serialize(new { decision = "await_user", summary = "Resume the session isolation fixture." });
      var revision = Regex.Match(text, @"Host evidence revision (\d+):").Groups[1].Value;
      var index = int.Parse(item.AsSpan("work-".Length));
      if (text.Contains("corrective worker", StringComparison.Ordinal) && index == 1
        && await File.ReadAllTextAsync(Path.Combine(workspace, "isolation-001.txt")) == "pending")
        return JsonSerializer.Serialize(new
        {
          decision = "reject_work",
          evidenceRevision = long.Parse(revision),
          blockingCriterion = Criterion(1),
          discrepancy = "The file contains pending rather than isolated.",
          correctiveBrief = "Replace pending with the requested exact text isolated."
        });
      return JsonSerializer.Serialize(new
      {
        decision = "accept_work",
        evidenceRevision = long.Parse(revision),
        coveredCriteria = new[] { Criterion(index) },
        summary = "The current Host evidence confirms the assigned file."
      });
    }
    return JsonSerializer.Serialize(new { decision = "complete_goal", finalAnswer = "Created and independently verified both isolated artifacts." });
  }

  private static string Criterion(int index) => $"isolation-{index:000}.txt contains the exact text isolated";
}
