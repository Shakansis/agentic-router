using System.Collections.Concurrent;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;

namespace AgenticRouter.Api.Benchmarking;

public interface IAutoModelHarnessRoutingService
{
  Task<AutoModelHarnessRoutingResult> RouteAsync(
    string routeSessionId,
    string task,
    string selectedModel,
    IReadOnlyList<InstalledModel> installedModels,
    CancellationToken cancellationToken
  );
}

public sealed class AutoModelHarnessRoutingService : IAutoModelHarnessRoutingService
{
  public const string RouterVersion = "auto-model-harness-router-v2";
  private const int MaximumRetainedSessionRoutes = 100;

  private static readonly (string Category, string[] Terms)[] CategoryTerms =
  [
    (
      BenchmarkRecommendationCategoryIds.RecoveryHeavy,
      ["recover", "recovery", "retry", "fallback", "falha", "recuper", "tente novamente"]
    ),
    (
      BenchmarkRecommendationCategoryIds.LongContinuity,
      ["continuity", "continue", "resume", "session", "multi-turn", "context", "continuidade", "continuar", "retomar", "sessão", "contexto"]
    ),
    (
      BenchmarkRecommendationCategoryIds.ExactFilesystem,
      ["file", "folder", "directory", "rename", "delete", "create", "edit", "path", "arquivo", "pasta", "diretório", "renome", "exclu", "crie", "edite", "caminho"]
    ),
    (
      BenchmarkRecommendationCategoryIds.EfficiencyFirst,
      ["fast", "faster", "speed", "efficient", "performance", "rápid", "veloc", "efici", "desempenho"]
    ),
    (
      BenchmarkRecommendationCategoryIds.TerminalityFirst,
      ["finish", "complete", "terminal", "conclude", "finalize", "termine", "conclua", "finalize"]
    ),
    (
      BenchmarkRecommendationCategoryIds.CorrectnessFirst,
      ["correct", "correctness", "validate", "verify", "test", "bug", "fix", "corret", "valid", "verif", "teste", "corrija"]
    )
  ];

  private readonly IBenchmarkRecommendationService _recommendations;
  private readonly IHarnessRegistry _harnesses;
  private readonly ConcurrentDictionary<string, AutoModelHarnessRoutingResult> _sessionRoutes = new(
    StringComparer.Ordinal
  );
  private readonly ConcurrentQueue<string> _routeRetentionOrder = new();

  public AutoModelHarnessRoutingService(
    IBenchmarkRecommendationService recommendations,
    IHarnessRegistry harnesses
  )
  {
    _recommendations = recommendations;
    _harnesses = harnesses;
  }

  public async Task<AutoModelHarnessRoutingResult> RouteAsync(
    string routeSessionId,
    string task,
    string selectedModel,
    IReadOnlyList<InstalledModel> installedModels,
    CancellationToken cancellationToken
  )
  {
    if (
      !string.IsNullOrWhiteSpace(routeSessionId)
      && _sessionRoutes.TryGetValue(routeSessionId, out var retained)
    )
    {
      return retained with
      {
        Reason = "Route retained for this active task session; no mid-session rerouting was performed."
      };
    }

    var category = Classify(task);
    var recommendation = await _recommendations.RecommendAsync(
      new BenchmarkRecommendationRequest(category, "active", IncludeExternalEvidence: false),
      cancellationToken
    );
    var installed = installedModels.Where(model =>
      model.Selectable
      && string.Equals(model.Provider, ModelProviderIds.OllamaLocal, StringComparison.Ordinal)
    ).Select(model => model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!installed.Contains(selectedModel))
    {
      throw new InvalidOperationException(
        $"Auto harness routing received unavailable selected model '{selectedModel}'."
      );
    }

    var harnessStatuses = await _harnesses.DiscoverAsync(cancellationToken);
    var availableHarnesses = harnessStatuses.Where(status =>
      status.Availability.Available
      && SupportsLocalOllama(status.Definition)
    ).Select(status => status.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unavailable = recommendation.Candidates.Where(candidate =>
      !availableHarnesses.Contains(candidate.Harness)
      || !Acceptable(candidate)
    ).Select(candidate =>
      $"{candidate.Model}|{candidate.Harness}|{(!availableHarnesses.Contains(candidate.Harness) ? "harness-unavailable" : "insufficient-evidence")}"
    ).ToArray();

    var exact = recommendation.Candidates.Where(candidate =>
      string.Equals(candidate.Model, selectedModel, StringComparison.OrdinalIgnoreCase)
      && availableHarnesses.Contains(candidate.Harness)
      && Acceptable(candidate)
    ).OrderByDescending(candidate => candidate.Score)
      .ThenBy(candidate => candidate.Harness, StringComparer.Ordinal)
      .FirstOrDefault();

    BenchmarkRecommendationCandidate? selected = exact;
    var reason = exact is null
      ? string.Empty
      : $"Best ranked available harness for selected model {selectedModel}.";
    var fallback = false;
    string? fallbackReason = null;

    if (selected is null)
    {
      selected = AggregateHarnessCandidates(
        recommendation.Candidates,
        selectedModel,
        availableHarnesses
      ).FirstOrDefault();
      if (selected is not null)
      {
        fallback = true;
        reason = $"Selected model {selectedModel} has no usable exact harness ranking; using the best aggregate harness score across ranked models.";
        fallbackReason = "Exact model x harness benchmark evidence was unavailable.";
      }
    }

    if (selected is null && availableHarnesses.Contains(HarnessIds.Native))
    {
      selected = new BenchmarkRecommendationCandidate(
        1,
        selectedModel,
        HarnessIds.Native,
        "Deterministic default",
        0,
        BenchmarkRecommendationConfidenceIds.Insufficient,
        "No usable local benchmark evidence",
        ["Native is the deterministic built-in fallback."],
        ["No usable local benchmark evidence exists for an available harness."],
        [BenchmarkRecommendationEvidenceSourceIds.InsufficientEvidence],
        [],
        0,
        0,
        0,
        0
      );
      fallback = true;
      reason = $"No usable harness ranking exists for selected model {selectedModel}; using deterministic Native fallback.";
      fallbackReason = "Local benchmark evidence was unavailable.";
    }

    var result = selected is null
      ? new AutoModelHarnessRoutingResult(
        RouterVersion,
        AutoModelHarnessRoutingStatusIds.InsufficientEvidence,
        category,
        recommendation.RecommendationId,
        recommendation.AlgorithmVersion,
        recommendation.ScoringProfile,
        null,
        "No ranked or deterministic fallback harness is currently available.",
        true,
        "Native is unavailable and no available harness has usable local evidence.",
        unavailable
      )
      : new AutoModelHarnessRoutingResult(
        RouterVersion,
        AutoModelHarnessRoutingStatusIds.Selected,
        category,
        recommendation.RecommendationId,
        recommendation.AlgorithmVersion,
        recommendation.ScoringProfile,
        selected,
        reason,
        fallback,
        fallbackReason,
        unavailable
      );

    Retain(routeSessionId, result);
    return result;
  }

  internal static string Classify(string task)
  {
    var normalized = task.Trim().ToLowerInvariant();
    foreach (var (category, terms) in CategoryTerms)
    {
      if (terms.Any(term => ContainsTerm(normalized, term)))
      {
        return category;
      }
    }
    return BenchmarkRecommendationCategoryIds.GeneralCoding;
  }

  private static IReadOnlyList<BenchmarkRecommendationCandidate> AggregateHarnessCandidates(
    IReadOnlyList<BenchmarkRecommendationCandidate> candidates,
    string selectedModel,
    IReadOnlySet<string> availableHarnesses
  )
  {
    return candidates.Where(candidate =>
      availableHarnesses.Contains(candidate.Harness) && Acceptable(candidate)
    ).GroupBy(candidate => candidate.Harness, StringComparer.OrdinalIgnoreCase)
      .Select(group =>
      {
        var items = group.ToArray();
        var evidence = items.SelectMany(item => item.Evidence)
          .DistinctBy(link => $"{link.RunId}|{link.SuiteId}|{link.Source}", StringComparer.Ordinal)
          .OrderByDescending(link => link.StartedAt)
          .ThenBy(link => link.RunId, StringComparer.Ordinal)
          .ToArray();
        return new BenchmarkRecommendationCandidate(
          0,
          selectedModel,
          group.Key,
          "Best aggregate harness",
          decimal.Round(items.Average(item => item.Score), 2, MidpointRounding.AwayFromZero),
          items.OrderBy(item => ConfidenceOrder(item.Confidence)).First().Confidence,
          $"Aggregate of {items.Length} ranked model(s)",
          ["Best mean score among available harnesses across ranked models."],
          ["No usable exact ranking exists for the selected model."],
          items.SelectMany(item => item.EvidenceSources).Distinct(StringComparer.Ordinal).ToArray(),
          evidence,
          items.Sum(item => item.CurrentRunCount),
          items.Sum(item => item.ComparableHistoricalRunCount),
          items.Sum(item => item.PartialHistoricalRunCount),
          items.Sum(item => item.IncompatibleHistoricalRunCount)
        );
      }).OrderByDescending(candidate => candidate.Score)
      .ThenBy(candidate => candidate.Harness, StringComparer.Ordinal)
      .Select((candidate, index) => candidate with { Rank = index + 1 })
      .ToArray();
  }

  private void Retain(
    string routeSessionId,
    AutoModelHarnessRoutingResult result
  )
  {
    if (string.IsNullOrWhiteSpace(routeSessionId) || !_sessionRoutes.TryAdd(routeSessionId, result))
    {
      return;
    }
    _routeRetentionOrder.Enqueue(routeSessionId);
    while (
      _sessionRoutes.Count > MaximumRetainedSessionRoutes
      && _routeRetentionOrder.TryDequeue(out var expired)
    )
    {
      _sessionRoutes.TryRemove(expired, out _);
    }
  }

  private static bool ContainsTerm(string text, string term)
  {
    var index = 0;
    while ((index = text.IndexOf(term, index, StringComparison.Ordinal)) >= 0)
    {
      if (index == 0 || !char.IsLetterOrDigit(text[index - 1]))
      {
        return true;
      }
      index++;
    }
    return false;
  }

  private static bool Acceptable(BenchmarkRecommendationCandidate candidate)
  {
    return candidate.Evidence.Count > 0
      && candidate.EvidenceSources.Contains(
        BenchmarkRecommendationEvidenceSourceIds.MeasuredLocally,
        StringComparer.Ordinal
      )
      && !string.Equals(
        candidate.Confidence,
        BenchmarkRecommendationConfidenceIds.Insufficient,
        StringComparison.Ordinal
      );
  }

  private static int ConfidenceOrder(string confidence)
  {
    return confidence switch
    {
      BenchmarkRecommendationConfidenceIds.Insufficient => 0,
      BenchmarkRecommendationConfidenceIds.Mixed => 1,
      BenchmarkRecommendationConfidenceIds.Limited => 2,
      BenchmarkRecommendationConfidenceIds.Moderate => 3,
      BenchmarkRecommendationConfidenceIds.Strong => 4,
      _ => 0
    };
  }

  private static bool SupportsLocalOllama(HarnessDefinition definition)
  {
    return definition.SupportedProviders is null
      || definition.SupportedProviders.Count == 0
      || definition.SupportedProviders.Contains(
        ModelProviderIds.OllamaLocal,
        StringComparer.OrdinalIgnoreCase
      );
  }
}
