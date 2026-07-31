using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;

namespace AgenticRouter.Api.Usage;

public interface IUsageLedger
{
  Task AppendAsync(
    UsageEvent usageEvent,
    int maximumEventBytes,
    int retentionDays,
    CancellationToken cancellationToken
  );

  Task<UsageAggregate> AggregateAsync(
    UsageWindow window,
    UsageFilter filter,
    bool recalculateWithCurrentPrices,
    CancellationToken cancellationToken
  );

  Task<UsagePurgeResult> PurgeAsync(
    DateTimeOffset? beforeUtc,
    CancellationToken cancellationToken
  );

  Task<IReadOnlyList<UsageEvent>> QueryAsync(
    UsageWindow window,
    UsageFilter filter,
    int maximumEvents,
    CancellationToken cancellationToken
  );

  UsageWindow ResolveWindow(
    string windowId,
    UsageSettings settings,
    DateTimeOffset nowUtc,
    int? customMinutes = null
  );
}

public sealed class JsonlUsageLedger : IUsageLedger
{
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  );

  private readonly string _usageDirectory;
  private readonly IPricingCatalog _pricing;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );
  private readonly HashSet<string> _knownEventIds = new(
    StringComparer.Ordinal
  );
  private bool _eventIdsLoaded;

  public JsonlUsageLedger(
    string dataDirectory,
    IPricingCatalog pricing
  )
  {
    _usageDirectory = Path.Combine(
      dataDirectory,
      "usage"
    );
    _pricing = pricing;
  }

  public async Task AppendAsync(
    UsageEvent usageEvent,
    int maximumEventBytes,
    int retentionDays,
    CancellationToken cancellationToken
  )
  {
    ValidateEvent(
      usageEvent,
      _pricing.Get().Version
    );
    var payload = JsonSerializer.SerializeToUtf8Bytes(
      usageEvent,
      JsonOptions
    );

    if (payload.Length > maximumEventBytes)
    {
      throw new UsageStorageException(
        "usage-record-too-large",
        "usage-storage",
        $"Usage metadata exceeded the configured {maximumEventBytes}-byte record limit.",
        false
      );
    }

    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      Directory.CreateDirectory(
        _usageDirectory
      );
      await EnsureEventIdsLoadedAsync(
        cancellationToken
      );

      if (!_knownEventIds.Add(
        usageEvent.EventId
      ))
      {
        throw new UsageStorageException(
          "usage-duplicate-event",
          "usage-storage",
          "A usage event with the same immutable event ID already exists.",
          false
        );
      }

      await ApplyRetentionAsync(
        retentionDays,
        usageEvent.TimestampUtc,
        cancellationToken
      );
      var path = Path.Combine(
        _usageDirectory,
        $"{usageEvent.TimestampUtc.UtcDateTime:yyyy-MM-dd}.jsonl"
      );
      await RecoverPartialTailAsync(
        path,
        cancellationToken
      );
      await using var stream = new FileStream(
        path,
        FileMode.Append,
        FileAccess.Write,
        FileShare.Read,
        16_384,
        FileOptions.Asynchronous | FileOptions.WriteThrough
      );
      await stream.WriteAsync(
        payload,
        cancellationToken
      );
      await stream.WriteAsync(
        "\n"u8.ToArray(),
        cancellationToken
      );
      await stream.FlushAsync(
        cancellationToken
      );
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
    )
    {
      throw new UsageStorageException(
        "usage-storage-failed",
        "usage-storage",
        "Usage metadata could not be appended safely.",
        true,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<UsageAggregate> AggregateAsync(
    UsageWindow window,
    UsageFilter filter,
    bool recalculateWithCurrentPrices,
    CancellationToken cancellationToken
  )
  {
    var modelBreakdown = new Dictionary<string, MutableBreakdown>(
      StringComparer.Ordinal
    );
    var roleBreakdown = new Dictionary<string, MutableBreakdown>(
      StringComparer.Ordinal
    );
    var providerBreakdown = new Dictionary<string, MutableBreakdown>(
      StringComparer.Ordinal
    );
    long input = 0;
    long output = 0;
    long requests = 0;
    long successes = 0;
    long failures = 0;
    long cancellations = 0;
    decimal actualCost = 0;
    decimal equivalentCost = 0;
    var exact = false;
    var estimated = false;
    DateTimeOffset? lastUpdated = null;

    if (Directory.Exists(
      _usageDirectory
    ))
    {
      foreach (var path in EnumerateWindowFiles(
        window
      ))
      {
        await foreach (var usageEvent in ReadEventsAsync(
          path,
          cancellationToken
        ))
        {
          var validation = UsageEventValidator.Validate(
            usageEvent,
            _pricing.Get().Version,
            DateTimeOffset.UtcNow
          );

          if (
            !validation.Accepted
            ||
            usageEvent.TimestampUtc < window.StartUtc
            || usageEvent.TimestampUtc >= window.EndUtc
            || !Matches(
              usageEvent,
              filter
            )
          )
          {
            continue;
          }

          var eventActual = usageEvent.EstimatedActualCost;
          var eventEquivalent = usageEvent.EquivalentCloudCost;

          if (recalculateWithCurrentPrices)
          {
            var actualPrice = usageEvent.ActualPriceSnapshot is null
              ? null
              : _pricing.Find(
                usageEvent.ActualPriceSnapshot.ProviderId,
                usageEvent.ActualPriceSnapshot.ModelId
              );
            var comparison = usageEvent.EquivalentPriceSnapshot is null
              ? null
              : _pricing.Find(
                usageEvent.EquivalentPriceSnapshot.ProviderId,
                usageEvent.EquivalentPriceSnapshot.ModelId
              );
            eventActual = actualPrice is null
              ? 0m
              : _pricing.Calculate(
                actualPrice,
                usageEvent.InputTokens,
                usageEvent.OutputTokens,
                usageEvent.CachedInputTokens,
                usageEvent.ReasoningTokens
              );
            eventEquivalent = comparison is null
              ? 0m
              : _pricing.Calculate(
                comparison,
                usageEvent.InputTokens,
                usageEvent.OutputTokens,
                usageEvent.CachedInputTokens,
                usageEvent.ReasoningTokens
              );
          }

          input += usageEvent.InputTokens;
          output += usageEvent.OutputTokens;
          requests++;
          actualCost += eventActual;
          equivalentCost += eventEquivalent;
          lastUpdated = lastUpdated is null
            || usageEvent.TimestampUtc > lastUpdated
              ? usageEvent.TimestampUtc
              : lastUpdated;
          exact = exact || string.Equals(
            usageEvent.Accuracy,
            UsageAccuracy.Exact,
            StringComparison.Ordinal
          );
          estimated = estimated || string.Equals(
            usageEvent.Accuracy,
            UsageAccuracy.Estimated,
            StringComparison.Ordinal
          );

          switch (usageEvent.Status)
          {
            case UsageStatuses.Success:
              successes++;
              break;
            case UsageStatuses.Cancellation:
              cancellations++;
              break;
            default:
              failures++;
              break;
          }

          AddBreakdown(
            modelBreakdown,
            $"{usageEvent.ProviderId} · {usageEvent.ModelId}",
            usageEvent,
            eventActual,
            eventEquivalent
          );
          AddBreakdown(
            roleBreakdown,
            usageEvent.ModelRole,
            usageEvent,
            eventActual,
            eventEquivalent
          );
          AddBreakdown(
            providerBreakdown,
            usageEvent.ProviderId,
            usageEvent,
            eventActual,
            eventEquivalent
          );
        }
      }
    }

    return new UsageAggregate(
      window,
      filter,
      input,
      output,
      input + output,
      requests,
      successes,
      failures,
      cancellations,
      exact && estimated
        ? UsageAccuracy.Mixed
        : exact
          ? UsageAccuracy.Exact
          : estimated
            ? UsageAccuracy.Estimated
            : UsageAccuracy.Unavailable,
      actualCost,
      equivalentCost,
      "USD",
      Top(
        modelBreakdown
      ),
      Top(
        roleBreakdown
      ),
      Top(
        providerBreakdown
      ),
      lastUpdated,
      recalculateWithCurrentPrices
    );
  }

  public async Task<UsagePurgeResult> PurgeAsync(
    DateTimeOffset? beforeUtc,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      if (!Directory.Exists(
        _usageDirectory
      ))
      {
        return new UsagePurgeResult(
          0,
          0,
          beforeUtc
        );
      }

      var deletedFiles = 0;
      long deletedEvents = 0;

      foreach (var path in Directory.EnumerateFiles(
        _usageDirectory,
        "*.jsonl",
        SearchOption.TopDirectoryOnly
      ))
      {
        cancellationToken.ThrowIfCancellationRequested();

        if (beforeUtc is null)
        {
          deletedEvents += await CountEventsAsync(
            path,
            cancellationToken
          );
          File.Delete(
            path
          );
          deletedFiles++;
          continue;
        }

        var fileDate = ParseFileDate(
          path
        );

        if (fileDate is not null && fileDate.Value < DateOnly.FromDateTime(
          beforeUtc.Value.UtcDateTime
        ))
        {
          deletedEvents += await CountEventsAsync(
            path,
            cancellationToken
          );
          File.Delete(
            path
          );
          deletedFiles++;
          continue;
        }

        if (
          fileDate is null
          || fileDate.Value > DateOnly.FromDateTime(
            beforeUtc.Value.UtcDateTime
          )
        )
        {
          continue;
        }

        deletedEvents += await RewriteBoundaryFileAsync(
          path,
          beforeUtc.Value,
          cancellationToken
        );
      }

      return new UsagePurgeResult(
        deletedFiles,
        deletedEvents,
        beforeUtc
      );
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
    )
    {
      throw new UsageStorageException(
        "usage-purge-failed",
        "usage-purge",
        "Usage history could not be purged safely.",
        true,
        exception
      );
    }
    finally
    {
      _gate.Release();
    }
  }

  public async Task<IReadOnlyList<UsageEvent>> QueryAsync(
    UsageWindow window,
    UsageFilter filter,
    int maximumEvents,
    CancellationToken cancellationToken
  )
  {
    if (maximumEvents is < 1 or > 10_000)
    {
      throw new UsageStorageException(
        "usage-query-limit-invalid",
        "usage-query",
        "Usage query limit must be between 1 and 10000 events.",
        false
      );
    }

    var events = new List<UsageEvent>();

    if (!Directory.Exists(
      _usageDirectory
    ))
    {
      return events;
    }

    foreach (var path in EnumerateWindowFiles(
      window
    ))
    {
      await foreach (var usageEvent in ReadEventsAsync(
        path,
        cancellationToken
      ))
      {
        var validation = UsageEventValidator.Validate(
          usageEvent,
          _pricing.Get().Version,
          DateTimeOffset.UtcNow
        );

        if (
          validation.Accepted
          &&
          usageEvent.TimestampUtc >= window.StartUtc
          && usageEvent.TimestampUtc < window.EndUtc
          && Matches(
            usageEvent,
            filter
          )
        )
        {
          events.Add(
            usageEvent
          );
        }
      }
    }

    return events
      .OrderByDescending(
        usageEvent => usageEvent.TimestampUtc
      )
      .Take(
        maximumEvents
      )
      .ToArray();
  }

  public UsageWindow ResolveWindow(
    string windowId,
    UsageSettings settings,
    DateTimeOffset nowUtc,
    int? customMinutes = null
  )
  {
    var normalizedNow = nowUtc.ToUniversalTime();
    var start = windowId switch
    {
      UsageWindowIds.RollingHour => normalizedNow.AddHours(
        -1
      ),
      UsageWindowIds.ProviderShort => normalizedNow.AddMinutes(
        -settings.ProviderShortWindowMinutes
      ),
      UsageWindowIds.Day => new DateTimeOffset(
        normalizedNow.Year,
        normalizedNow.Month,
        normalizedNow.Day,
        0,
        0,
        0,
        TimeSpan.Zero
      ),
      UsageWindowIds.ProviderLong => normalizedNow.AddMinutes(
        -settings.ProviderLongWindowMinutes
      ),
      UsageWindowIds.RollingSevenDays => normalizedNow.AddDays(
        -7
      ),
      UsageWindowIds.CalendarMonth => new DateTimeOffset(
        normalizedNow.Year,
        normalizedNow.Month,
        1,
        0,
        0,
        0,
        TimeSpan.Zero
      ),
      UsageWindowIds.CustomRolling => normalizedNow.AddMinutes(
        -Math.Clamp(
          customMinutes ?? settings.CustomRollingWindowMinutes,
          5,
          43_200
        )
      ),
      _ => throw new UsageStorageException(
        "usage-window-invalid",
        "usage-aggregation",
        $"Usage window '{windowId}' is not supported.",
        false
      )
    };

    return new UsageWindow(
      windowId,
      start,
      normalizedNow
    );
  }

  private IEnumerable<string> EnumerateWindowFiles(
    UsageWindow window
  )
  {
    var startDate = DateOnly.FromDateTime(
      window.StartUtc.UtcDateTime
    );
    var endDate = DateOnly.FromDateTime(
      window.EndUtc.UtcDateTime
    );

    for (
      var date = startDate;
      date <= endDate;
      date = date.AddDays(
        1
      )
    )
    {
      var path = Path.Combine(
        _usageDirectory,
        $"{date:yyyy-MM-dd}.jsonl"
      );

      if (File.Exists(
        path
      ))
      {
        yield return path;
      }
    }
  }

  private static async IAsyncEnumerable<UsageEvent> ReadEventsAsync(
    string path,
    [System.Runtime.CompilerServices.EnumeratorCancellation]
    CancellationToken cancellationToken
  )
  {
    await using var stream = new FileStream(
      path,
      FileMode.Open,
      FileAccess.Read,
      FileShare.ReadWrite,
      16_384,
      FileOptions.Asynchronous | FileOptions.SequentialScan
    );
    using var reader = new StreamReader(
      stream,
      Encoding.UTF8,
      true,
      16_384,
      false
    );

    while (true)
    {
      var line = await reader.ReadLineAsync(
        cancellationToken
      );

      if (line is null)
      {
        yield break;
      }

      if (string.IsNullOrWhiteSpace(
        line
      ))
      {
        continue;
      }

      UsageEvent? usageEvent;

      try
      {
        usageEvent = JsonSerializer.Deserialize<UsageEvent>(
          line,
          JsonOptions
        );
      }
      catch (JsonException)
      {
        continue;
      }

      if (usageEvent is not null)
      {
        yield return usageEvent;
      }
    }
  }

  private async Task ApplyRetentionAsync(
    int retentionDays,
    DateTimeOffset nowUtc,
    CancellationToken cancellationToken
  )
  {
    var oldestDate = DateOnly.FromDateTime(
      nowUtc.UtcDateTime.Date.AddDays(
        -(retentionDays - 1)
      )
    );

    foreach (var path in Directory.EnumerateFiles(
      _usageDirectory,
      "*.jsonl",
      SearchOption.TopDirectoryOnly
    ))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var date = ParseFileDate(
        path
      );

      if (date is not null && date.Value < oldestDate)
      {
        File.Delete(
          path
        );
      }
    }

    await Task.CompletedTask;
  }

  private static async Task RecoverPartialTailAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    if (!File.Exists(
      path
    ))
    {
      return;
    }

    await using var stream = new FileStream(
      path,
      FileMode.Open,
      FileAccess.ReadWrite,
      FileShare.Read,
      4_096,
      FileOptions.Asynchronous
    );

    if (stream.Length == 0)
    {
      return;
    }

    stream.Seek(
      -1,
      SeekOrigin.End
    );
    var finalByte = stream.ReadByte();

    if (finalByte == '\n')
    {
      return;
    }

    var originalLength = stream.Length;
    var position = originalLength - 1;

    while (position >= 0)
    {
      stream.Seek(
        position,
        SeekOrigin.Begin
      );

      if (stream.ReadByte() == '\n')
      {
        break;
      }

      position--;
    }

    var tailStart = position + 1;
    var tailLength = originalLength - tailStart;

    if (tailLength > 65_536)
    {
      stream.SetLength(
        tailStart
      );
      return;
    }

    stream.Seek(
      tailStart,
      SeekOrigin.Begin
    );
    var buffer = ArrayPool<byte>.Shared.Rent(
      Convert.ToInt32(
        tailLength
      )
    );

    try
    {
      var read = await stream.ReadAsync(
        buffer.AsMemory(
          0,
          Convert.ToInt32(
            tailLength
          )
        ),
        cancellationToken
      );
      var valid = false;

      try
      {
        using var document = JsonDocument.Parse(
          buffer.AsMemory(
            0,
            read
          )
        );
        valid = document.RootElement.ValueKind == JsonValueKind.Object;
      }
      catch (JsonException)
      {
      }

      if (valid)
      {
        stream.Seek(
          0,
          SeekOrigin.End
        );
        await stream.WriteAsync(
          "\n"u8.ToArray(),
          cancellationToken
        );
      }
      else
      {
        stream.SetLength(
          tailStart
        );
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(
        buffer
      );
    }
  }

  private static void ValidateEvent(
    UsageEvent usageEvent,
    string pricingCatalogVersion
  )
  {
    var result = UsageEventValidator.Validate(
      usageEvent,
      pricingCatalogVersion,
      DateTimeOffset.UtcNow
    );

    if (!result.Accepted)
    {
      throw new UsageStorageException(
        "usage-record-invalid",
        "usage-storage",
        "Usage metadata did not satisfy the application-owned schema.",
        false
      );
    }
  }

  private async Task EnsureEventIdsLoadedAsync(
    CancellationToken cancellationToken
  )
  {
    if (_eventIdsLoaded)
    {
      return;
    }

    if (Directory.Exists(
      _usageDirectory
    ))
    {
      foreach (var path in Directory.EnumerateFiles(
        _usageDirectory,
        "*.jsonl",
        SearchOption.TopDirectoryOnly
      ))
      {
        await foreach (var usageEvent in ReadEventsAsync(
          path,
          cancellationToken
        ))
        {
          if (!string.IsNullOrWhiteSpace(
            usageEvent.EventId
          ))
          {
            _knownEventIds.Add(
              usageEvent.EventId
            );
          }
        }
      }
    }

    _eventIdsLoaded = true;
  }

  private static bool Matches(
    UsageEvent usageEvent,
    UsageFilter filter
  )
  {
    return MatchesValue(
        usageEvent.WorkspaceId,
        filter.WorkspaceId
      )
      && MatchesValue(
        usageEvent.ProviderId,
        filter.ProviderId
      )
      && MatchesValue(
        usageEvent.ModelId,
        filter.ModelId
      )
      && MatchesValue(
        usageEvent.ModelRole,
        filter.ModelRole
      );
  }

  private static bool MatchesValue(
    string? value,
    string? filter
  )
  {
    return string.IsNullOrWhiteSpace(
      filter
    ) || string.Equals(
      value,
      filter,
      StringComparison.Ordinal
    );
  }

  private static void AddBreakdown(
    IDictionary<string, MutableBreakdown> target,
    string key,
    UsageEvent usageEvent,
    decimal actual,
    decimal equivalent
  )
  {
    if (!target.TryGetValue(
      key,
      out var value
    ))
    {
      value = new MutableBreakdown(
        key
      );
      target.Add(
        key,
        value
      );
    }

    value.InputTokens += usageEvent.InputTokens;
    value.OutputTokens += usageEvent.OutputTokens;
    value.Requests++;
    value.EstimatedActualCost += actual;
    value.EquivalentCloudCost += equivalent;
  }

  private static IReadOnlyList<UsageBreakdown> Top(
    IReadOnlyDictionary<string, MutableBreakdown> values
  )
  {
    return values.Values
      .OrderByDescending(
        value => value.InputTokens + value.OutputTokens
      )
      .ThenBy(
        value => value.Key,
        StringComparer.Ordinal
      )
      .Take(
        10
      )
      .Select(
        value => new UsageBreakdown(
          value.Key,
          value.InputTokens,
          value.OutputTokens,
          value.InputTokens + value.OutputTokens,
          value.Requests,
          value.EstimatedActualCost,
          value.EquivalentCloudCost
        )
      )
      .ToArray();
  }

  private static DateOnly? ParseFileDate(
    string path
  )
  {
    return DateOnly.TryParseExact(
      Path.GetFileNameWithoutExtension(
        path
      ),
      "yyyy-MM-dd",
      CultureInfo.InvariantCulture,
      DateTimeStyles.None,
      out var date
    )
      ? date
      : null;
  }

  private static async Task<long> CountEventsAsync(
    string path,
    CancellationToken cancellationToken
  )
  {
    long count = 0;

    await foreach (var _ in ReadEventsAsync(
      path,
      cancellationToken
    ))
    {
      count++;
    }

    return count;
  }

  private static async Task<long> RewriteBoundaryFileAsync(
    string path,
    DateTimeOffset beforeUtc,
    CancellationToken cancellationToken
  )
  {
    var retained = new List<UsageEvent>();
    long deleted = 0;

    await foreach (var usageEvent in ReadEventsAsync(
      path,
      cancellationToken
    ))
    {
      if (usageEvent.TimestampUtc < beforeUtc)
      {
        deleted++;
      }
      else
      {
        retained.Add(
          usageEvent
        );
      }
    }

    if (deleted == 0)
    {
      return 0;
    }

    var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

    try
    {
      await using (
        var stream = new FileStream(
          temporaryPath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          16_384,
          FileOptions.Asynchronous | FileOptions.WriteThrough
        )
      )
      {
        foreach (var usageEvent in retained)
        {
          await JsonSerializer.SerializeAsync(
            stream,
            usageEvent,
            JsonOptions,
            cancellationToken
          );
          await stream.WriteAsync(
            "\n"u8.ToArray(),
            cancellationToken
          );
        }

        await stream.FlushAsync(
          cancellationToken
        );
      }

      File.Move(
        temporaryPath,
        path,
        true
      );
    }
    finally
    {
      if (File.Exists(
        temporaryPath
      ))
      {
        File.Delete(
          temporaryPath
        );
      }
    }

    return deleted;
  }

  private sealed class MutableBreakdown
  {
    public MutableBreakdown(
      string key
    )
    {
      Key = key;
    }

    public string Key { get; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long Requests { get; set; }

    public decimal EstimatedActualCost { get; set; }

    public decimal EquivalentCloudCost { get; set; }
  }
}
