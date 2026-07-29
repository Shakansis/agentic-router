using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Routing;

public interface IModelResolver
{
  ModelResolution Resolve(
    ApplicationSettings settings,
    string intention,
    IReadOnlyList<InstalledModel> installedModels
  );
}

public sealed record ModelResolutionAttempt(
  string Source,
  string ConfiguredValue,
  string ResolvedModel,
  bool Installed
);

public sealed record ModelResolution(
  string? Model,
  IReadOnlyList<ModelResolutionAttempt> Attempts
);

public sealed class ModelResolver : IModelResolver
{
  public ModelResolution Resolve(
    ApplicationSettings settings,
    string intention,
    IReadOnlyList<InstalledModel> installedModels
  )
  {
    var intentionSettings = settings.Intentions[intention];
    var candidates = new[]
    {
      new Candidate(
        "intention primary",
        intentionSettings.Model
      ),
      new Candidate(
        "intention fallback",
        intentionSettings.FallbackModel
      ),
      new Candidate(
        "global default",
        settings.DefaultModel
      )
    };
    var attempts = new List<ModelResolutionAttempt>();
    var seen = new HashSet<string>(
      StringComparer.OrdinalIgnoreCase
    );

    foreach (var candidate in candidates)
    {
      if (string.Equals(
        candidate.Value,
        "none",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        continue;
      }

      var resolved = string.Equals(
        candidate.Value,
        "default",
        StringComparison.OrdinalIgnoreCase
      )
        ? settings.DefaultModel
        : candidate.Value;

      if (
        string.IsNullOrWhiteSpace(
          resolved
        )
        || !seen.Add(
          resolved
        )
      )
      {
        continue;
      }

      var installed = installedModels.Any(
        model => string.Equals(
          model.Name,
          resolved,
          StringComparison.OrdinalIgnoreCase
        )
      );
      attempts.Add(
        new ModelResolutionAttempt(
          candidate.Source,
          candidate.Value,
          resolved,
          installed
        )
      );

      if (installed)
      {
        return new ModelResolution(
          resolved,
          attempts
        );
      }
    }

    return new ModelResolution(
      null,
      attempts
    );
  }

  private sealed record Candidate(
    string Source,
    string Value
  );
}
