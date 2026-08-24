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
    IReadOnlyList<InstalledModel> installedModels,
    CancellationToken cancellationToken
  );
}

public sealed class AutoModelHarnessRoutingService : IAutoModelHarnessRoutingService
{
  public const string RouterVersion = "auto-model-harness-router-v1";
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
      new BenchmarkRecommendationRequest(
        category,
        "active",
        IncludeExternalEvidence: false
      ),
      cancellationToken
    );
    var localModels = installedModels.Where(model =>
      model.Selectable
      && string.Equals(
        model.Provider,
        ModelProviderIds.OllamaLocal,
        StringComparison.Ordinal
      )
    ).Select(model => model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var harnessStatuses = await _harnesses.DiscoverAsync(cancellationToken);
    var availableHarnesses = harnessStatuses.Where(status =>
      status.Availability.Available
      && SupportsLocalOllama(status.Definition)
    ).Select(status => status.Definition.Id)
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unavailable = new List<string>();
    BenchmarkRecommendationCandidate? selected = null;
    foreach (var candidate in recommendation.Candidates)
    {
      if (!Acceptable(candidate))
      {
        unavailable.Add($"{candidate.Model}|{candidate.Harness}|insufficient-evidence");
        continue;
      }
      if (!localModels.Contains(candidate.Model))
      {
        unavailable.Add($"{candidate.Model}|{candidate.Harness}|model-unavailable");
        continue;
      }
      if (!availableHarnesses.Contains(candidate.Harness))
      {
        unavailable.Add($"{candidate.Model}|{candidate.Harness}|harness-unavailable");
        continue;
      }
      selected = candidate;
      break;
    }

    if (selected is null)
    {
      return new AutoModelHarnessRoutingResult(
        RouterVersion,
        AutoModelHarnessRoutingStatusIds.InsufficientEvidence,
        category,
        recommendation.RecommendationId,
        recommendation.AlgorithmVersion,
        recommendation.ScoringProfile,
        null,
        recommendation.Candidates.Count == 0
          ? "No local recommendation evidence is available for this task."
          : "No recommended local Model x Harness candidate is currently executable.",
        false,
        null,
        unavailable
      );
    }

    var fallback = selected.Rank > 1;
    var result = new AutoModelHarnessRoutingResult(
      RouterVersion,
      AutoModelHarnessRoutingStatusIds.Selected,
      category,
      recommendation.RecommendationId,
      recommendation.AlgorithmVersion,
      recommendation.ScoringProfile,
      selected,
      "Best available local evidence for this task.",
      fallback,
      fallback
        ? $"Higher-ranked candidate(s) were unavailable: {string.Join(", ", unavailable)}."
        : null,
      unavailable
    );
    if (!string.IsNullOrWhiteSpace(routeSessionId))
    {
      if (_sessionRoutes.TryAdd(routeSessionId, result))
      {
        _routeRetentionOrder.Enqueue(routeSessionId);
        while (
          _sessionRoutes.Count > MaximumRetainedSessionRoutes
          && _routeRetentionOrder.TryDequeue(out var expired)
        )
        {
          _sessionRoutes.TryRemove(expired, out _);
        }
      }
    }
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
