using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public interface IExecutionPlanService
{
  ExecutionPlanView ValidateCreate(
    JsonElement arguments,
    int maximumSteps
  );

  ExecutionPlanView ValidateRevision(
    JsonElement arguments,
    ExecutionPlanView current,
    int maximumSteps
  );

}

public sealed class ExecutionPlanService : IExecutionPlanService
{
  private static readonly string[] ExecutableFragments =
  [
    "&&",
    "||",
    "cmd.exe",
    "powershell",
    "pwsh ",
    "dotnet ",
    "git "
  ];

  public ExecutionPlanView ValidateCreate(
    JsonElement arguments,
    int maximumSteps
  )
  {
    var parsed = ParseArguments(
      arguments,
      maximumSteps
    );
    var steps = parsed.Steps.Select(
      (
        step,
        index
      ) => new ExecutionPlanStep(
        CreateStepId(
          index + 1
        ),
        step.Title,
        "pending",
        step.Dependencies.Select(
          CreateStepId
        ).ToArray()
      )
    ).ToArray();

    return new ExecutionPlanView(
      parsed.Objective,
      steps,
      null,
      0,
      0
    );
  }

  public ExecutionPlanView ValidateRevision(
    JsonElement arguments,
    ExecutionPlanView current,
    int maximumSteps
  )
  {
    var parsed = ParseArguments(
      arguments,
      maximumSteps
    );
    var usedIds = new HashSet<string>(
      StringComparer.Ordinal
    );
    var allocatedIds = current.Steps.Select(
      step => step.Id
    ).ToHashSet(
      StringComparer.Ordinal
    );
    var steps = new List<ExecutionPlanStep>();

    var proposedDependencies = new Dictionary<string, IReadOnlyList<int>>(
      StringComparer.Ordinal
    );
    for (var index = 0; index < parsed.Steps.Count; index++)
    {
      var proposed = parsed.Steps[index];
      var title = proposed.Title;
      var existing = current.Steps.FirstOrDefault(
        step => !usedIds.Contains(
          step.Id
        ) && string.Equals(
          step.Title,
          title,
          StringComparison.Ordinal
        )
      );

      if (
        existing is null
        && index < current.Steps.Count
        && current.Steps[index].Status is not (
          "completed" or "failed" or "blocked"
        )
        && !usedIds.Contains(
          current.Steps[index].Id
        )
      )
      {
        existing = current.Steps[index];
      }

      if (existing is not null)
      {
        usedIds.Add(
          existing.Id
        );
        steps.Add(
          existing.Status is "completed" or "failed" or "blocked"
            ? existing
            : existing with
            {
              Title = title
            }
        );
        proposedDependencies[existing.Id] = proposed.Dependencies;
        continue;
      }

      var generatedId = NextStepId(
        allocatedIds
      );
      allocatedIds.Add(
        generatedId
      );
      usedIds.Add(
        generatedId
      );
      steps.Add(
        new ExecutionPlanStep(
          generatedId,
          title,
          "pending"
        )
      );
      proposedDependencies[generatedId] = proposed.Dependencies;
    }

    steps = steps.Select(
      step => proposedDependencies.TryGetValue(step.Id, out var dependencies)
        ? step with
        {
          Dependencies = dependencies.Select(
            dependency => steps[dependency - 1].Id
          ).ToArray()
        }
        : step
    ).ToList();

    var omittedTerminalSteps = current.Steps.Select(
      (
        step,
        index
      ) => new
      {
        Step = step,
        Index = index
      }
    ).Where(
      item => (
        item.Step.Status is "completed" or "failed" or "blocked"
      )
        && !usedIds.Contains(
          item.Step.Id
        )
    ).ToArray();

    if (steps.Count + omittedTerminalSteps.Length > maximumSteps)
    {
      throw new LocalActionException(
        "execution-plan",
        $"Revised plan cannot preserve terminal steps within the {maximumSteps}-step limit."
      );
    }

    foreach (var omitted in omittedTerminalSteps)
    {
      steps.Insert(
        Math.Min(
          omitted.Index,
          steps.Count
        ),
        omitted.Step
      );
    }

    return new ExecutionPlanView(
      parsed.Objective,
      steps,
      steps.FirstOrDefault(
        step => step.Status == "in-progress"
      )?.Id,
      steps.Count(
        step => step.Status == "completed"
      ),
      current.RevisionCount + 1
    );
  }

  private static ParsedPlan ParseArguments(
    JsonElement arguments,
    int maximumSteps
  )
  {
    if (
      arguments.ValueKind != JsonValueKind.Object
      || !arguments.TryGetProperty(
        "objective",
        out var objectiveElement
      )
      || objectiveElement.ValueKind != JsonValueKind.String
    )
    {
      throw new LocalActionException(
        "execution-plan",
        "Execution plan requires a textual objective."
      );
    }

    var objective = objectiveElement.GetString()?.Trim() ?? string.Empty;

    if (
      objective.Length is < 1
        or > ProjectAwarenessSettings.MaximumPlanObjectiveCharacters
    )
    {
      throw new LocalActionException(
        "execution-plan",
        $"Execution plan objective must contain between 1 and {ProjectAwarenessSettings.MaximumPlanObjectiveCharacters} characters; received {objective.Length}."
      );
    }

    if (
      !arguments.TryGetProperty(
        "steps",
        out var stepsElement
      )
      || stepsElement.ValueKind != JsonValueKind.Array
    )
    {
      throw new LocalActionException(
        "execution-plan",
        "Execution plan requires a steps array."
      );
    }

    var elements = stepsElement.EnumerateArray().ToArray();

    if (elements.Length < 1 || elements.Length > maximumSteps)
    {
      throw new LocalActionException(
        "execution-plan",
        $"Execution plan must contain between 1 and {maximumSteps} steps."
      );
    }

    var steps = new List<ParsedStep>();

    for (var index = 0; index < elements.Length; index++)
    {
      var element = elements[index];
      JsonElement titleElement;

      if (element.ValueKind == JsonValueKind.String)
      {
        titleElement = element;
      }
      else if (
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(
          "title",
          out var objectTitle
        )
        && objectTitle.ValueKind == JsonValueKind.String
      )
      {
        titleElement = objectTitle;
      }
      else
      {
        throw new LocalActionException(
          "execution-plan",
          "Every execution plan step requires a textual title."
        );
      }

      var title = titleElement.GetString()?.Trim() ?? string.Empty;

      if (
        title.Length is < 1
          or > ProjectAwarenessSettings.MaximumPlanStepTitleCharacters
        || title.Contains(
          '\n',
          StringComparison.Ordinal
        )
        || ExecutableFragments.Any(
          fragment => title.Contains(
            fragment,
            StringComparison.OrdinalIgnoreCase
          )
        )
      )
      {
        throw new LocalActionException(
          "execution-plan",
          "Execution plan step titles must be short descriptions without executable content."
        );
      }

      var dependencies = Array.Empty<int>();
      if (
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(
          "dependsOn",
          out var dependsOn
        )
      )
      {
        if (dependsOn.ValueKind != JsonValueKind.Array)
        {
          throw new LocalActionException(
            "execution-plan",
            "Execution plan step dependencies must be an array of one-based earlier step indexes."
          );
        }

        dependencies = dependsOn.EnumerateArray().Select(
          dependency => dependency.ValueKind == JsonValueKind.Number
            && dependency.TryGetInt32(out var value)
            ? value
            : 0
        ).ToArray();
        if (
          dependencies.Any(
            dependency => dependency < 1 || dependency > index
          )
          || dependencies.Distinct().Count() != dependencies.Length
        )
        {
          throw new LocalActionException(
            "execution-plan",
            "Execution plan dependencies must be unique one-based indexes of earlier proposed steps."
          );
        }
      }

      steps.Add(
        new ParsedStep(
          title,
          dependencies
        )
      );
    }

    return new ParsedPlan(
      objective,
      steps
    );
  }

  private static string NextStepId(
    IReadOnlySet<string> usedIds
  )
  {
    for (var index = 1; ; index++)
    {
      var candidate = CreateStepId(
        index
      );

      if (!usedIds.Contains(
        candidate
      ))
      {
        return candidate;
      }
    }
  }

  private static string CreateStepId(
    int index
  )
  {
    return $"step-{index}";
  }

  private sealed record ParsedPlan(
    string Objective,
    IReadOnlyList<ParsedStep> Steps
  );

  private sealed record ParsedStep(
    string Title,
    IReadOnlyList<int> Dependencies
  );
}
