using System.Collections.Concurrent;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Providers;

public static class ProviderConnectionStates
{
  public const string Healthy = "healthy";
  public const string Degraded = "degraded";
  public const string Unavailable = "unavailable";
  public const string NotConfigured = "not-configured";
  public const string Unknown = "unknown";
}

public sealed record ProviderDiagnosticView(
  int? LastStatusCode,
  string? ErrorCategory,
  string RetryDecision,
  string QuotaSource,
  string UsageCountSource,
  string? ProviderModelIdentity,
  string AdapterVersion,
  DateTimeOffset? LastSuccessfulModelRefresh
);

public sealed record ProviderHealthView(
  string ProviderId,
  string DisplayName,
  bool Enabled,
  string ConnectionState,
  DateTimeOffset? LastSuccessfulRequest,
  DateTimeOffset? LastFailedRequest,
  DateTimeOffset? LastCheckedAt,
  string? LastModelUsed,
  long? TimeToFirstChunkMilliseconds,
  long? TotalLatencyMilliseconds,
  decimal? RecentSuccessRate,
  int RecentFailureCount,
  bool RateLimited,
  string QuotaState,
  string TokenUsageAccuracy,
  string? CurrentDiagnostic,
  string HealthSource,
  bool Stale,
  ProviderDiagnosticView Diagnostic
);

public sealed record ProviderHealthResponse(
  IReadOnlyList<ProviderHealthView> Providers,
  DateTimeOffset GeneratedAt
);

public sealed record ProviderRetryDecision(
  bool Retry,
  TimeSpan Delay,
  string Category,
  string Reason,
  int Attempt,
  int MaximumAttempts
);

public interface IProviderRetryPolicy
{
  ProviderRetryDecision Decide(
    Exception exception,
    int attempt,
    TimeSpan elapsed,
    CancellationToken cancellationToken
  );
}

public sealed class ConservativeProviderRetryPolicy : IProviderRetryPolicy
{
  public const int MaximumAttempts = 3;
  private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(
    8
  );

  public ProviderRetryDecision Decide(
    Exception exception,
    int attempt,
    TimeSpan elapsed,
    CancellationToken cancellationToken
  )
  {
    if (
      cancellationToken.IsCancellationRequested
      || exception is OperationCanceledException
    )
    {
      return NoRetry(
        "cancellation",
        "User cancellation is never retried.",
        attempt
      );
    }

    var cloud = exception as CloudProviderException;
    var category = cloud?.Code
      ?? (
        exception is TimeoutException
          ? "timeout"
          : exception is HttpRequestException
            ? "temporary-network"
            : "provider-failure"
      );
    var retryable = cloud is not null
      ? cloud.Retryable
        && (
          cloud.HttpStatus is 408 or 429
          || cloud.HttpStatus is >= 500 and <= 599
          || cloud.Code.Contains(
            "timeout",
            StringComparison.Ordinal
          )
          || cloud.Code.Contains(
            "network",
            StringComparison.Ordinal
          )
        )
      : exception is TimeoutException or HttpRequestException;

    if (!retryable)
    {
      return NoRetry(
        category,
        "The provider failure is deterministic or explicitly non-retryable.",
        attempt
      );
    }

    if (attempt >= MaximumAttempts)
    {
      return NoRetry(
        category,
        "The conservative retry-attempt limit was reached.",
        attempt
      );
    }

    var retryAfter = cloud?.RetryAfter
      ?? ResetDelay(
        cloud?.RateLimit
      );
    var baseDelay = retryAfter
      ?? TimeSpan.FromMilliseconds(
        200 * Math.Pow(
          2,
          attempt - 1
        )
      );
    var jitter = TimeSpan.FromMilliseconds(
      Random.Shared.Next(
        25,
        126
      )
    );
    var delay = baseDelay + jitter;

    if (elapsed + delay > MaximumDuration)
    {
      return NoRetry(
        category,
        "The bounded retry-duration limit would be exceeded.",
        attempt
      );
    }

    return new ProviderRetryDecision(
      true,
      delay,
      category,
      retryAfter is null
        ? "Temporary provider failure; applying bounded jittered backoff."
        : "Provider Retry-After was accepted within the bounded retry window.",
      attempt,
      MaximumAttempts
    );
  }

  private static ProviderRetryDecision NoRetry(
    string category,
    string reason,
    int attempt
  )
  {
    return new ProviderRetryDecision(
      false,
      TimeSpan.Zero,
      category,
      reason,
      attempt,
      MaximumAttempts
    );
  }

  private static TimeSpan? ResetDelay(
    ProviderRateLimitSnapshot? rateLimit
  )
  {
    if (
      rateLimit?.RequestRemaining != 0
      && rateLimit?.TokenRemaining != 0
    )
    {
      return null;
    }

    var reset = rateLimit?.RequestResetAt
      ?? rateLimit?.TokenResetAt;

    if (reset is null)
    {
      return null;
    }

    var delay = reset.Value - DateTimeOffset.UtcNow;
    return delay > TimeSpan.Zero
      ? delay
      : TimeSpan.Zero;
  }
}

public interface IProviderHealthMonitor
{
  void Reset(
    string providerId
  );

  void ObserveSuccess(
    string providerId,
    string modelId,
    TimeSpan latency,
    TimeSpan? timeToFirstChunk,
    ProviderTokenUsage? usage,
    ProviderRateLimitSnapshot? rateLimit,
    string adapterVersion,
    string source
  );

  void ObserveFailure(
    string providerId,
    string? modelId,
    TimeSpan latency,
    Exception exception,
    ProviderRetryDecision decision,
    string adapterVersion,
    string source
  );

  void ObserveModelRefresh(
    string providerId,
    bool success,
    string adapterVersion,
    int? statusCode,
    string? errorCategory
  );

  ProviderHealthView CreateView(
    string providerId,
    string displayName,
    bool enabled,
    bool configured,
    string quotaState,
    string quotaSource,
    string adapterVersion,
    TimeSpan staleAfter
  );
}

public sealed class ProviderHealthMonitor : IProviderHealthMonitor
{
  private readonly ConcurrentDictionary<string, MutableProviderHealth> _states =
    new(
      StringComparer.Ordinal
    );

  public void Reset(
    string providerId
  )
  {
    _states.TryRemove(
      providerId,
      out _
    );
  }

  public void ObserveSuccess(
    string providerId,
    string modelId,
    TimeSpan latency,
    TimeSpan? timeToFirstChunk,
    ProviderTokenUsage? usage,
    ProviderRateLimitSnapshot? rateLimit,
    string adapterVersion,
    string source
  )
  {
    var now = DateTimeOffset.UtcNow;
    var state = State(
      providerId
    );

    lock (state.Gate)
    {
      state.LastSuccessfulRequest = now;
      state.LastCheckedAt = now;
      state.LastModelUsed = modelId;
      state.TimeToFirstChunkMilliseconds = ToMilliseconds(
        timeToFirstChunk
      );
      state.TotalLatencyMilliseconds = ToMilliseconds(
        latency
      );
      state.AdapterVersion = adapterVersion;
      state.Source = source;
      state.LastStatusCode = 200;
      state.ErrorCategory = null;
      state.CurrentDiagnostic = null;
      state.RetryDecision = "request-completed";
      state.UsageAccuracy = usage is null
        ? UsageAccuracy.Estimated
        : UsageAccuracy.Exact;
      state.RateLimit = rateLimit;
      AddOutcome(
        state,
        true
      );
    }
  }

  public void ObserveFailure(
    string providerId,
    string? modelId,
    TimeSpan latency,
    Exception exception,
    ProviderRetryDecision decision,
    string adapterVersion,
    string source
  )
  {
    var now = DateTimeOffset.UtcNow;
    var cloud = exception as CloudProviderException;
    var state = State(
      providerId
    );

    lock (state.Gate)
    {
      state.LastFailedRequest = now;
      state.LastCheckedAt = now;
      state.LastModelUsed = modelId;
      state.TotalLatencyMilliseconds = ToMilliseconds(
        latency
      );
      state.AdapterVersion = adapterVersion;
      state.Source = source;
      state.LastStatusCode = cloud?.HttpStatus;
      state.ErrorCategory = decision.Category;
      state.CurrentDiagnostic = decision.Reason;
      state.RetryDecision = decision.Retry
        ? $"retry-{decision.Attempt}-of-{decision.MaximumAttempts}"
        : "not-retried";
      state.RateLimit = cloud?.RateLimit
        ?? state.RateLimit;
      AddOutcome(
        state,
        false
      );
    }
  }

  public void ObserveModelRefresh(
    string providerId,
    bool success,
    string adapterVersion,
    int? statusCode,
    string? errorCategory
  )
  {
    var now = DateTimeOffset.UtcNow;
    var state = State(
      providerId
    );

    lock (state.Gate)
    {
      state.LastCheckedAt = now;
      state.AdapterVersion = adapterVersion;
      state.Source = "provider-model-refresh";
      state.LastStatusCode = statusCode;
      state.ErrorCategory = errorCategory;
      state.CurrentDiagnostic = errorCategory;

      if (success)
      {
        state.Outcomes.Clear();
        state.LastSuccessfulModelRefresh = now;
        state.CurrentDiagnostic = null;
        AddOutcome(
          state,
          true
        );
      }
      else
      {
        state.LastFailedRequest = now;
        AddOutcome(
          state,
          false
        );
      }
    }
  }

  public ProviderHealthView CreateView(
    string providerId,
    string displayName,
    bool enabled,
    bool configured,
    string quotaState,
    string quotaSource,
    string adapterVersion,
    TimeSpan staleAfter
  )
  {
    var state = State(
      providerId
    );

    lock (state.Gate)
    {
      var now = DateTimeOffset.UtcNow;
      var stale = state.LastCheckedAt is not null
        && now - state.LastCheckedAt > staleAfter;
      var failures = state.Outcomes.Count(
        outcome => !outcome.Success
      );
      var successes = state.Outcomes.Count - failures;
      decimal? successRate = state.Outcomes.Count == 0
        ? null
        : Math.Round(
          successes * 100m / state.Outcomes.Count,
          1,
          MidpointRounding.AwayFromZero
        );
      var connectionState = !enabled || !configured
        ? ProviderConnectionStates.NotConfigured
        : state.LastCheckedAt is null
          ? ProviderConnectionStates.Unknown
          : state.LastSuccessfulRequest is null
            && state.LastSuccessfulModelRefresh is null
              ? ProviderConnectionStates.Unavailable
              : failures > 0 || stale
                ? ProviderConnectionStates.Degraded
                : ProviderConnectionStates.Healthy;

      return new ProviderHealthView(
        providerId,
        displayName,
        enabled,
        connectionState,
        state.LastSuccessfulRequest,
        state.LastFailedRequest,
        state.LastCheckedAt,
        state.LastModelUsed,
        state.TimeToFirstChunkMilliseconds,
        state.TotalLatencyMilliseconds,
        successRate,
        failures,
        state.RateLimit?.RequestRemaining == 0
          || state.RateLimit?.TokenRemaining == 0
          || state.LastStatusCode == 429,
        quotaState,
        state.UsageAccuracy,
        state.CurrentDiagnostic,
        state.Source,
        stale,
        new ProviderDiagnosticView(
          state.LastStatusCode,
          state.ErrorCategory,
          state.RetryDecision,
          quotaSource,
          state.UsageAccuracy == UsageAccuracy.Exact
            ? TokenCountSources.Provider
            : TokenCountSources.Estimated,
          state.LastModelUsed is null
            ? null
            : new ProviderModelReference(
              providerId,
              state.LastModelUsed
            ).Qualified,
          string.IsNullOrWhiteSpace(
            state.AdapterVersion
          )
            ? adapterVersion
            : state.AdapterVersion,
          state.LastSuccessfulModelRefresh
        )
      );
    }
  }

  private MutableProviderHealth State(
    string providerId
  )
  {
    return _states.GetOrAdd(
      providerId,
      _ => new MutableProviderHealth()
    );
  }

  private static void AddOutcome(
    MutableProviderHealth state,
    bool success
  )
  {
    state.Outcomes.Enqueue(
      new ProviderOutcome(
        DateTimeOffset.UtcNow,
        success
      )
    );

    while (
      state.Outcomes.Count > 20
      || state.Outcomes.TryPeek(
        out var outcome
      ) && DateTimeOffset.UtcNow - outcome.At > TimeSpan.FromHours(
        1
      )
    )
    {
      state.Outcomes.Dequeue();
    }
  }

  private static long? ToMilliseconds(
    TimeSpan? duration
  )
  {
    return duration is null
      ? null
      : Convert.ToInt64(
        Math.Round(
          duration.Value.TotalMilliseconds,
          MidpointRounding.AwayFromZero
        )
      );
  }

  private sealed class MutableProviderHealth
  {
    public object Gate { get; } = new();

    public Queue<ProviderOutcome> Outcomes { get; } = new();

    public DateTimeOffset? LastSuccessfulRequest { get; set; }

    public DateTimeOffset? LastFailedRequest { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    public DateTimeOffset? LastSuccessfulModelRefresh { get; set; }

    public string? LastModelUsed { get; set; }

    public long? TimeToFirstChunkMilliseconds { get; set; }

    public long? TotalLatencyMilliseconds { get; set; }

    public int? LastStatusCode { get; set; }

    public string? ErrorCategory { get; set; }

    public string RetryDecision { get; set; } = "not-evaluated";

    public string UsageAccuracy { get; set; } =
      AgenticRouter.Api.Usage.UsageAccuracy.Unavailable;

    public string? CurrentDiagnostic { get; set; }

    public string Source { get; set; } = "runtime-observation";

    public string AdapterVersion { get; set; } = "unknown";

    public ProviderRateLimitSnapshot? RateLimit { get; set; }
  }

  private sealed record ProviderOutcome(
    DateTimeOffset At,
    bool Success
  );
}
