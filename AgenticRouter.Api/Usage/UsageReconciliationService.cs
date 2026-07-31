using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Usage;

public interface IUsageReconciliationService
{
  Task<UsageReconciliationResult?> InitializeAsync(
    CancellationToken cancellationToken
  );

  Task<UsageReconciliationResult> RebuildAsync(
    bool automatic,
    CancellationToken cancellationToken
  );
}

public sealed class UsageReconciliationService : IUsageReconciliationService
{
  public const int AggregateSchemaVersion = 1;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  )
  {
    WriteIndented = true
  };

  private readonly string _usageDirectory;
  private readonly string _aggregateDirectory;
  private readonly string _aggregatePath;
  private readonly IPricingCatalog _pricing;
  private readonly SemaphoreSlim _gate = new(
    1,
    1
  );

  public UsageReconciliationService(
    string dataDirectory,
    IPricingCatalog pricing
  )
  {
    _usageDirectory = Path.Combine(
      dataDirectory,
      "usage"
    );
    _aggregateDirectory = Path.Combine(
      dataDirectory,
      "usage-aggregates"
    );
    _aggregatePath = Path.Combine(
      _aggregateDirectory,
      "aggregate-v1.json"
    );
    _pricing = pricing;
  }

  public async Task<UsageReconciliationResult?> InitializeAsync(
    CancellationToken cancellationToken
  )
  {
    if (await IsCurrentAggregateValidAsync(
      cancellationToken
    ))
    {
      return null;
    }

    return await RebuildAsync(
      true,
      cancellationToken
    );
  }

  public async Task<UsageReconciliationResult> RebuildAsync(
    bool automatic,
    CancellationToken cancellationToken
  )
  {
    await _gate.WaitAsync(
      cancellationToken
    );

    try
    {
      var started = DateTimeOffset.UtcNow;
      long accepted = 0;
      long warned = 0;
      long estimated = 0;
      long rejected = 0;
      long duplicates = 0;
      long input = 0;
      long output = 0;
      var eventIds = new HashSet<string>(
        StringComparer.Ordinal
      );
      var lastResetByProvider = new Dictionary<string, DateTimeOffset>(
        StringComparer.Ordinal
      );

      if (Directory.Exists(
        _usageDirectory
      ))
      {
        foreach (var path in Directory.EnumerateFiles(
          _usageDirectory,
          "*.jsonl",
          SearchOption.TopDirectoryOnly
        ).OrderBy(
          value => value,
          StringComparer.Ordinal
        ))
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
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(
              cancellationToken
            );

            if (line is null)
            {
              break;
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
              rejected++;
              continue;
            }

            if (usageEvent is null)
            {
              rejected++;
              continue;
            }

            if (!eventIds.Add(
              usageEvent.EventId
            ))
            {
              duplicates++;
              continue;
            }

            var validation = UsageEventValidator.Validate(
              usageEvent,
              _pricing.Get().Version,
              DateTimeOffset.UtcNow
            );

            if (!validation.Accepted)
            {
              rejected++;
              continue;
            }

            var reset = usageEvent.RateLimit?.TokenResetAt
              ?? usageEvent.RateLimit?.RequestResetAt;
            var resetMovedBackward = reset is not null
              && lastResetByProvider.TryGetValue(
                usageEvent.ProviderId,
                out var previousReset
              )
              && reset < previousReset;

            if (reset is not null)
            {
              lastResetByProvider[usageEvent.ProviderId] = reset.Value;
            }

            accepted++;
            input += usageEvent.InputTokens;
            output += usageEvent.OutputTokens;

            if (
              string.Equals(
                validation.Status,
                UsageValidationStatuses.Estimated,
                StringComparison.Ordinal
              )
            )
            {
              estimated++;
            }
            else if (
              string.Equals(
                validation.Status,
                UsageValidationStatuses.ValidWithWarning,
                StringComparison.Ordinal
              )
              || resetMovedBackward
            )
            {
              warned++;
            }
          }
        }
      }

      var completed = DateTimeOffset.UtcNow;
      var result = new UsageReconciliationResult(
        AggregateSchemaVersion,
        accepted,
        warned,
        estimated,
        rejected,
        duplicates,
        input,
        output,
        input + output,
        accepted,
        started,
        completed,
        "usage-aggregates/aggregate-v1.json",
        automatic
      );
      var document = new UsageAggregateDocument(
        AggregateSchemaVersion,
        _pricing.Get().Version,
        result
      );
      Directory.CreateDirectory(
        _aggregateDirectory
      );
      var temporary = Path.Combine(
        _aggregateDirectory,
        $".aggregate-{Guid.NewGuid():N}.tmp"
      );

      try
      {
        await File.WriteAllTextAsync(
          temporary,
          JsonSerializer.Serialize(
            document,
            JsonOptions
          ).Replace(
            "\r\n",
            "\n",
            StringComparison.Ordinal
          ) + "\n",
          cancellationToken
        );
        File.Move(
          temporary,
          _aggregatePath,
          true
        );
      }
      finally
      {
        if (File.Exists(
          temporary
        ))
        {
          File.Delete(
            temporary
          );
        }
      }

      return result;
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task<bool> IsCurrentAggregateValidAsync(
    CancellationToken cancellationToken
  )
  {
    if (!File.Exists(
      _aggregatePath
    ))
    {
      return false;
    }

    try
    {
      await using var stream = File.OpenRead(
        _aggregatePath
      );
      var document = await JsonSerializer.DeserializeAsync<UsageAggregateDocument>(
        stream,
        JsonOptions,
        cancellationToken
      );
      return document?.SchemaVersion == AggregateSchemaVersion
        && document.Result.AggregateSchemaVersion == AggregateSchemaVersion;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or JsonException
    )
    {
      return false;
    }
  }

  private sealed record UsageAggregateDocument(
    int SchemaVersion,
    string PricingCatalogVersion,
    UsageReconciliationResult Result
  );
}
