using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;

namespace AgenticRouter.Api.Observability;

public sealed class JsonlIncidentJournal : IIncidentJournal
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  private readonly string _directory;
  private readonly ISettingsStore _settings;
  private readonly ILogger<JsonlIncidentJournal> _logger;
  private readonly SemaphoreSlim _gate = new(1, 1);

  public JsonlIncidentJournal(
    string dataDirectory,
    ISettingsStore settings,
    ILogger<JsonlIncidentJournal> logger
  )
  {
    _directory = Path.Combine(dataDirectory, "incidents");
    _settings = settings;
    _logger = logger;
  }

  public async Task<IncidentAppendResult> AppendAsync(
    IncidentEvent incident,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var policy = (await _settings.GetAsync(cancellationToken)).Incidents;
      if (!policy.Enabled)
      {
        return new IncidentAppendResult(false, FailureCode: "incident-journal-disabled");
      }

      ValidateIncident(incident);
      var json = JsonSerializer.Serialize(incident, JsonOptions);
      if (Encoding.UTF8.GetByteCount(json) > 16_384)
      {
        return new IncidentAppendResult(false, FailureCode: "incident-event-too-large");
      }

      await _gate.WaitAsync(cancellationToken);
      try
      {
        Directory.CreateDirectory(_directory);
        var existing = await CountTraceEventsAsync(incident.TraceId, policy.MaximumEventsPerTrace, cancellationToken);
        var terminal = incident.Status is "completed" or "failed" or "cancelled";
        var maximumBeforeAppend = terminal
          ? policy.MaximumEventsPerTrace
          : policy.MaximumEventsPerTrace - 1;
        if (existing >= maximumBeforeAppend)
        {
          return new IncidentAppendResult(false, FailureCode: "incident-trace-limit-reached");
        }

        var file = SelectWritableFile(policy.MaximumFileBytes, Encoding.UTF8.GetByteCount(json) + 1);
        await File.AppendAllTextAsync(file, json + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
        EnforceRetention(policy);
        return new IncidentAppendResult(true, incident.EventId);
      }
      finally
      {
        _gate.Release();
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      _logger.LogWarning(exception, "Incident journal append failed for trace {TraceId}.", incident.TraceId);
      return new IncidentAppendResult(false, FailureCode: "incident-journal-write-failed");
    }
  }

  public async Task<IncidentTraceReport?> FindTraceAsync(
    string traceId,
    CancellationToken cancellationToken
  )
  {
    ValidateTraceId(traceId);
    var policy = (await _settings.GetAsync(cancellationToken)).Incidents;
    if (!Directory.Exists(_directory))
    {
      return null;
    }

    var matches = new List<IncidentEvent>();
    long bytes = 0;
    var truncated = false;
    var malformed = 0;
    foreach (var file in EnumerateFilesNewestFirst())
    {
      using var reader = new StreamReader(file.FullName, Encoding.UTF8, true, 4_096);
      while (await reader.ReadLineAsync(cancellationToken) is { } line)
      {
        if (!line.Contains(traceId, StringComparison.Ordinal))
        {
          continue;
        }

        bytes += Encoding.UTF8.GetByteCount(line);
        if (matches.Count >= policy.BrowserMaximumEvents || bytes > policy.BrowserMaximumBytes)
        {
          truncated = true;
          continue;
        }

        try
        {
          var item = JsonSerializer.Deserialize<IncidentEvent>(line, JsonOptions);
          if (item is not null && string.Equals(item.TraceId, traceId, StringComparison.Ordinal))
          {
            matches.Add(item);
          }
        }
        catch (JsonException)
        {
          malformed++;
        }
      }
    }

    if (matches.Count == 0)
    {
      return null;
    }

    matches.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
    var failure = matches.LastOrDefault(item => item.Status == "failed");
    var last = matches[^1];
    var context = matches.LastOrDefault(item => item.ContextFit is not null)?.ContextFit;
    var completed = matches.Any(item => item.Completed == true);
    var review = matches.Any(item => item.ReviewAvailable == true);
    return new IncidentTraceReport(
      traceId,
      failure is not null ? "failed" : completed ? "completed" : last.Status,
      failure?.Code,
      failure?.Stage,
      failure?.Provider ?? last.Provider,
      failure?.Model ?? last.Model,
      matches.LastOrDefault(item => item.Coordinator is not null)?.Coordinator,
      matches.LastOrDefault(item => item.ExecutionPath is not null)?.ExecutionPath,
      context,
      completed,
      review,
      truncated,
      matches.Count,
      matches,
      completed
        ? "Review the terminal execution summary and retained artifacts."
        : review
          ? "Open the execution review before deciding whether to retry."
          : "Use the failure code and context arithmetic to choose a different execution path.",
      malformed
    );
  }

  private string SelectWritableFile(long maximumBytes, int incomingBytes)
  {
    var date = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
    var existing = Directory.EnumerateFiles(_directory, $"incidents-{date}-*.jsonl")
      .Select(path => new FileInfo(path))
      .OrderByDescending(file => file.Name, StringComparer.Ordinal)
      .FirstOrDefault();
    if (existing is not null && existing.Length + incomingBytes <= maximumBytes)
    {
      return existing.FullName;
    }

    var index = existing is null
      ? 1
      : int.TryParse(Path.GetFileNameWithoutExtension(existing.Name).Split('-').Last(), out var parsed)
        ? parsed + 1
        : 1;
    return Path.Combine(_directory, $"incidents-{date}-{index:000}.jsonl");
  }

  private void EnforceRetention(IncidentJournalSettings policy)
  {
    var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.RetentionDays);
    var files = EnumerateFilesNewestFirst().ToList();
    foreach (var expired in files.Where(file => file.LastWriteTimeUtc < cutoff.UtcDateTime))
    {
      File.Delete(expired.FullName);
    }

    long retained = 0;
    foreach (var file in EnumerateFilesNewestFirst())
    {
      retained += file.Length;
      if (retained > policy.MaximumTotalBytes)
      {
        File.Delete(file.FullName);
      }
    }
  }

  private IEnumerable<FileInfo> EnumerateFilesNewestFirst()
  {
    return Directory.EnumerateFiles(_directory, "incidents-*.jsonl")
      .Select(path => new FileInfo(path))
      .OrderByDescending(file => file.LastWriteTimeUtc)
      .ThenByDescending(file => file.Name, StringComparer.Ordinal);
  }

  private async Task<int> CountTraceEventsAsync(string traceId, int stopAt, CancellationToken cancellationToken)
  {
    var count = 0;
    foreach (var file in EnumerateFilesNewestFirst())
    {
      using var reader = new StreamReader(file.FullName, Encoding.UTF8, true, 4_096);
      while (await reader.ReadLineAsync(cancellationToken) is { } line)
      {
        if (line.Contains($"\"traceId\":\"{traceId}\"", StringComparison.Ordinal))
        {
          count++;
          if (count >= stopAt) return count;
        }
      }
    }
    return count;
  }

  private static void ValidateIncident(IncidentEvent incident)
  {
    ValidateTraceId(incident.TraceId);
    if (incident.SchemaVersion != 1 || string.IsNullOrWhiteSpace(incident.EventId) || incident.Sequence <= 0)
    {
      throw new InvalidDataException("The incident event contract is invalid.");
    }
  }

  public static void ValidateTraceId(string traceId)
  {
    if (string.IsNullOrWhiteSpace(traceId) || traceId.Length > 128 || traceId.Any(character => !char.IsLetterOrDigit(character) && character is not ':' and not '-' and not '_' and not '.'))
    {
      throw new ArgumentException("The trace identifier is invalid.", nameof(traceId));
    }
  }
}
