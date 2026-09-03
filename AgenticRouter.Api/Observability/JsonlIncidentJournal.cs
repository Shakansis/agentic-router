using System.Diagnostics;
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
  private readonly Dictionary<string, int> _traceEventCounts = new(StringComparer.Ordinal);
  private bool _traceIndexLoaded;
  private string? _currentFilePath;
  private long _currentFileLength;
  private long _retainedBytes;
  private DateTime? _oldestRetainedWriteUtc;
  private int _retainedFileCount;
  private long _appendAttempts;
  private long _persistedEvents;
  private long _rejectedEvents;
  private long _queueWaitMilliseconds;
  private long _writeMilliseconds;
  private long _traceIndexRebuilds;
  private long _traceIndexFilesScanned;
  private long _traceIndexRecordsScanned;
  private long _traceIndexMilliseconds;
  private long _lookupCount;
  private long _lookupFilesScanned;
  private long _lookupRecordsScanned;
  private long _lookupMilliseconds;
  private long _retentionEvaluations;
  private long _retentionFilesDeleted;
  private long _retentionMilliseconds;

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
    Interlocked.Increment(ref _appendAttempts);
    try
    {
      var policy = (await _settings.GetAsync(cancellationToken)).Incidents;
      if (!policy.Enabled)
      {
        Interlocked.Increment(ref _rejectedEvents);
        return new IncidentAppendResult(false, FailureCode: "incident-journal-disabled");
      }

      ValidateIncident(incident);
      var json = JsonSerializer.Serialize(incident, JsonOptions);
      if (Encoding.UTF8.GetByteCount(json) > 16_384)
      {
        Interlocked.Increment(ref _rejectedEvents);
        return new IncidentAppendResult(false, FailureCode: "incident-event-too-large");
      }

      var queueStarted = Stopwatch.GetTimestamp();
      await _gate.WaitAsync(cancellationToken);
      _queueWaitMilliseconds += ElapsedMilliseconds(queueStarted);
      try
      {
        Directory.CreateDirectory(_directory);
        if (!_traceIndexLoaded)
        {
          EnforceRetentionMeasured(policy);
          await RebuildTraceIndexAsync(cancellationToken);
        }
        else if (
          _oldestRetainedWriteUtc is { } oldest
          && oldest < DateTime.UtcNow.AddDays(-policy.RetentionDays)
        )
        {
          if (EnforceRetentionMeasured(policy) > 0)
          {
            await RebuildTraceIndexAsync(cancellationToken);
          }
        }
        else if (CurrentFileChangedExternally())
        {
          await RebuildTraceIndexAsync(cancellationToken);
        }

        var existing = _traceEventCounts.GetValueOrDefault(incident.TraceId);
        var terminal = incident.Status is "completed" or "failed" or "cancelled";
        var maximumBeforeAppend = terminal
          ? policy.MaximumEventsPerTrace
          : policy.MaximumEventsPerTrace - 1;
        if (existing >= maximumBeforeAppend)
        {
          _rejectedEvents++;
          return new IncidentAppendResult(false, FailureCode: "incident-trace-limit-reached");
        }

        var incomingBytes = Encoding.UTF8.GetByteCount(json) + Environment.NewLine.Length;
        var file = SelectWritableFile(policy.MaximumFileBytes, incomingBytes);
        var fileExisted = File.Exists(file);
        var writeStarted = Stopwatch.GetTimestamp();
        await File.AppendAllTextAsync(file, json + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
        _writeMilliseconds += ElapsedMilliseconds(writeStarted);
        _currentFilePath = file;
        _currentFileLength += incomingBytes;
        _retainedBytes += incomingBytes;
        if (!fileExisted)
        {
          _retainedFileCount++;
        }
        if (_retainedFileCount == 1 || _oldestRetainedWriteUtc is null)
        {
          _oldestRetainedWriteUtc = DateTime.UtcNow;
        }
        _traceEventCounts[incident.TraceId] = existing + 1;
        _persistedEvents++;

        if (_retainedBytes > policy.MaximumTotalBytes)
        {
          if (EnforceRetentionMeasured(policy) > 0)
          {
            await RebuildTraceIndexAsync(cancellationToken);
          }
        }
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
      Interlocked.Increment(ref _rejectedEvents);
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
    await _gate.WaitAsync(cancellationToken);
    try
    {
      var lookupStarted = Stopwatch.GetTimestamp();
      long lookupFiles = 0;
      long lookupRecords = 0;
      if (!Directory.Exists(_directory))
      {
        RecordLookup(lookupStarted, lookupFiles, lookupRecords);
        return null;
      }

      var matches = new List<IncidentEvent>();
      var malformed = 0;
      foreach (var file in EnumerateFilesNewestFirst())
      {
        lookupFiles++;
        using var reader = new StreamReader(file.FullName, Encoding.UTF8, true, 4_096);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
          lookupRecords++;
          if (!line.Contains(traceId, StringComparison.Ordinal))
          {
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
        RecordLookup(lookupStarted, lookupFiles, lookupRecords);
        return null;
      }

      matches.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
      var projected = ProjectEvents(
        matches,
        policy.BrowserMaximumEvents,
        policy.BrowserMaximumBytes
      );
      var truncated = projected.Count < matches.Count;
      var failure = matches.LastOrDefault(item => item.Status == "failed");
      var last = matches[^1];
      var context = matches.LastOrDefault(item => item.ContextFit is not null)?.ContextFit;
      var completed = matches.Any(item => item.Completed == true);
      var review = matches.Any(item => item.ReviewAvailable == true);
      RecordLookup(lookupStarted, lookupFiles, lookupRecords);
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
        projected.Count,
        projected,
        completed
          ? "Review the terminal execution summary and retained artifacts."
          : review
            ? "Open the execution review before deciding whether to retry."
            : "Use the failure code and context arithmetic to choose a different execution path.",
        malformed,
        CreateMetricsSnapshot()
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  private static IReadOnlyList<IncidentEvent> ProjectEvents(
    IReadOnlyList<IncidentEvent> events,
    int maximumEvents,
    long maximumBytes
  )
  {
    if (events.Count == 0)
    {
      return [];
    }

    var firstSequence = events[0].Sequence;
    var lastSequence = events[^1].Sequence;
    var selected = new List<IncidentEvent>(Math.Min(events.Count, maximumEvents));
    long selectedBytes = 0;
    foreach (var incident in events
      .OrderBy(item => ProjectionPriority(item, firstSequence, lastSequence))
      .ThenBy(item => item.Sequence))
    {
      if (selected.Count >= maximumEvents)
      {
        break;
      }

      var serializedBytes = Encoding.UTF8.GetByteCount(
        JsonSerializer.Serialize(incident, JsonOptions)
      );
      if (selectedBytes + serializedBytes > maximumBytes)
      {
        continue;
      }

      selected.Add(incident);
      selectedBytes += serializedBytes;
    }

    selected.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
    return selected;
  }

  private static int ProjectionPriority(
    IncidentEvent incident,
    long firstSequence,
    long lastSequence
  )
  {
    if (
      incident.Status is "completed" or "failed" or "cancelled"
      || incident.Completed == true
      || incident.ReviewAvailable == true
    )
    {
      return 0;
    }
    if (incident.Sequence == firstSequence || incident.Sequence == lastSequence)
    {
      return 1;
    }
    return string.Equals(incident.Category, "context", StringComparison.Ordinal)
      ? 3
      : 2;
  }

  private string SelectWritableFile(long maximumBytes, int incomingBytes)
  {
    var date = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
    if (
      _currentFilePath is not null
      && Path.GetFileName(_currentFilePath).StartsWith($"incidents-{date}-", StringComparison.Ordinal)
      && File.Exists(_currentFilePath)
      && _currentFileLength + incomingBytes <= maximumBytes
    )
    {
      return _currentFilePath;
    }

    var existing = Directory.EnumerateFiles(_directory, $"incidents-{date}-*.jsonl")
      .Select(path => new FileInfo(path))
      .OrderByDescending(file => file.Name, StringComparer.Ordinal)
      .FirstOrDefault();
    if (existing is not null && existing.Length + incomingBytes <= maximumBytes)
    {
      _currentFilePath = existing.FullName;
      _currentFileLength = existing.Length;
      return existing.FullName;
    }

    var index = existing is null
      ? 1
      : int.TryParse(Path.GetFileNameWithoutExtension(existing.Name).Split('-').Last(), out var parsed)
        ? parsed + 1
        : 1;
    _currentFilePath = Path.Combine(_directory, $"incidents-{date}-{index:000}.jsonl");
    _currentFileLength = File.Exists(_currentFilePath)
      ? new FileInfo(_currentFilePath).Length
      : 0;
    return _currentFilePath;
  }

  private bool CurrentFileChangedExternally()
  {
    return _currentFilePath is not null
      && (
        !File.Exists(_currentFilePath)
        || new FileInfo(_currentFilePath).Length != _currentFileLength
      );
  }

  private int EnforceRetentionMeasured(IncidentJournalSettings policy)
  {
    var started = Stopwatch.GetTimestamp();
    var deleted = EnforceRetention(policy);
    _retentionEvaluations++;
    _retentionFilesDeleted += deleted;
    _retentionMilliseconds += ElapsedMilliseconds(started);
    return deleted;
  }

  private int EnforceRetention(IncidentJournalSettings policy)
  {
    var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.RetentionDays);
    var files = EnumerateFilesNewestFirst().ToList();
    var deleted = 0;
    foreach (var expired in files.Where(file => file.LastWriteTimeUtc < cutoff.UtcDateTime))
    {
      File.Delete(expired.FullName);
      deleted++;
      if (string.Equals(expired.FullName, _currentFilePath, StringComparison.OrdinalIgnoreCase))
      {
        _currentFilePath = null;
        _currentFileLength = 0;
      }
    }

    long retained = 0;
    foreach (var file in EnumerateFilesNewestFirst())
    {
      retained += file.Length;
      if (retained > policy.MaximumTotalBytes)
      {
        File.Delete(file.FullName);
        deleted++;
        if (string.Equals(file.FullName, _currentFilePath, StringComparison.OrdinalIgnoreCase))
        {
          _currentFilePath = null;
          _currentFileLength = 0;
        }
      }
    }
    var retainedFiles = EnumerateFilesNewestFirst().ToArray();
    _retainedBytes = retainedFiles.Sum(file => file.Length);
    _retainedFileCount = retainedFiles.Length;
    _oldestRetainedWriteUtc = retainedFiles.Length == 0
      ? null
      : retainedFiles.Min(file => file.LastWriteTimeUtc);
    return deleted;
  }

  private IEnumerable<FileInfo> EnumerateFilesNewestFirst()
  {
    return Directory.EnumerateFiles(_directory, "incidents-*.jsonl")
      .Select(path => new FileInfo(path))
      .OrderByDescending(file => file.LastWriteTimeUtc)
      .ThenByDescending(file => file.Name, StringComparer.Ordinal);
  }

  private async Task RebuildTraceIndexAsync(CancellationToken cancellationToken)
  {
    var started = Stopwatch.GetTimestamp();
    long filesScanned = 0;
    long recordsScanned = 0;
    long retainedBytes = 0;
    DateTime? oldestRetainedWriteUtc = null;
    _traceEventCounts.Clear();
    foreach (var file in EnumerateFilesNewestFirst())
    {
      filesScanned++;
      retainedBytes += file.Length;
      oldestRetainedWriteUtc = oldestRetainedWriteUtc is null
        || file.LastWriteTimeUtc < oldestRetainedWriteUtc.Value
          ? file.LastWriteTimeUtc
          : oldestRetainedWriteUtc;
      using var reader = new StreamReader(file.FullName, Encoding.UTF8, true, 4_096);
      while (await reader.ReadLineAsync(cancellationToken) is { } line)
      {
        recordsScanned++;
        try
        {
          using var document = JsonDocument.Parse(line);
          if (
            document.RootElement.TryGetProperty("traceId", out var traceIdElement)
            && traceIdElement.ValueKind == JsonValueKind.String
            && traceIdElement.GetString() is { Length: > 0 } traceId
          )
          {
            _traceEventCounts[traceId] = _traceEventCounts.GetValueOrDefault(traceId) + 1;
          }
        }
        catch (JsonException)
        {
          // Exact lookups continue to count malformed records separately.
        }
      }
    }
    _retainedBytes = retainedBytes;
    _retainedFileCount = checked((int)filesScanned);
    _oldestRetainedWriteUtc = oldestRetainedWriteUtc;
    _traceIndexLoaded = true;
    _currentFilePath = null;
    _currentFileLength = 0;
    _traceIndexRebuilds++;
    _traceIndexFilesScanned += filesScanned;
    _traceIndexRecordsScanned += recordsScanned;
    _traceIndexMilliseconds += ElapsedMilliseconds(started);
  }

  private void RecordLookup(long started, long filesScanned, long recordsScanned)
  {
    _lookupCount++;
    _lookupFilesScanned += filesScanned;
    _lookupRecordsScanned += recordsScanned;
    _lookupMilliseconds += ElapsedMilliseconds(started);
  }

  private IncidentJournalMetrics CreateMetricsSnapshot()
  {
    return new IncidentJournalMetrics(
      DateTimeOffset.UtcNow,
      Interlocked.Read(ref _appendAttempts),
      Interlocked.Read(ref _persistedEvents),
      Interlocked.Read(ref _rejectedEvents),
      Interlocked.Read(ref _queueWaitMilliseconds),
      Interlocked.Read(ref _writeMilliseconds),
      Interlocked.Read(ref _traceIndexRebuilds),
      Interlocked.Read(ref _traceIndexFilesScanned),
      Interlocked.Read(ref _traceIndexRecordsScanned),
      Interlocked.Read(ref _traceIndexMilliseconds),
      Interlocked.Read(ref _lookupCount),
      Interlocked.Read(ref _lookupFilesScanned),
      Interlocked.Read(ref _lookupRecordsScanned),
      Interlocked.Read(ref _lookupMilliseconds),
      Interlocked.Read(ref _retentionEvaluations),
      Interlocked.Read(ref _retentionFilesDeleted),
      Interlocked.Read(ref _retentionMilliseconds),
      _traceEventCounts.Count,
      _retainedBytes
    );
  }

  private static long ElapsedMilliseconds(long started)
  {
    return Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
