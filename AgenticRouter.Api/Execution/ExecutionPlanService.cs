using System.Text.Json;
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
    return Parse(
      arguments,
      maximumSteps,
      0
    );
  }

  public ExecutionPlanView ValidateRevision(
    JsonElement arguments,
    ExecutionPlanView current,
    int maximumSteps
  )
  {
    var proposed = Parse(
      arguments,
      maximumSteps,
      current.RevisionCount + 1
    );
    var proposedIds = proposed.Steps.Select(
      step => step.Id
    ).ToHashSet(
      StringComparer.Ordinal
    );

    foreach (var step in current.Steps.Where(
      step => step.Status is "completed" or "failed" or "blocked"
    ))
    {
      if (!proposedIds.Contains(
        step.Id
      ))
      {
        throw new LocalActionException(
          "execution-plan",
          $"Revised plan cannot remove {step.Status} step '{step.Id}'."
        );
      }
    }

    var existingSteps = current.Steps.ToDictionary(
      step => step.Id,
      step => step,
      StringComparer.Ordinal
    );
    var steps = proposed.Steps.Select(
      step => existingSteps.TryGetValue(
        step.Id,
        out var existing
      )
        ? existing.Status is "completed" or "failed" or "blocked"
          ? existing
          : step with
          {
            Status = existing.Status
          }
        : step
    ).ToArray();
    return proposed with
    {
      Steps = steps,
      CurrentStepId = steps.FirstOrDefault(
        step => step.Status == "in-progress"
      )?.Id,
      CompletedStepCount = steps.Count(
        step => step.Status == "completed"
      )
    };
  }

  private static ExecutionPlanView Parse(
    JsonElement arguments,
    int maximumSteps,
    int revisionCount
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

    if (objective.Length is < 1 or > 240)
    {
      throw new LocalActionException(
        "execution-plan",
        $"Execution plan objective must contain between 1 and 240 characters; received {objective.Length}."
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

    var identifiers = new HashSet<string>(
      StringComparer.Ordinal
    );
    var steps = new List<ExecutionPlanStep>();

    foreach (var element in elements)
    {
      if (
        element.ValueKind != JsonValueKind.Object
        || !element.TryGetProperty(
          "id",
          out var idElement
        )
        || idElement.ValueKind != JsonValueKind.String
        || !element.TryGetProperty(
          "title",
          out var titleElement
        )
        || titleElement.ValueKind != JsonValueKind.String
      )
      {
        throw new LocalActionException(
          "execution-plan",
          "Every execution plan step requires string id and title fields."
        );
      }

      var id = idElement.GetString()?.Trim() ?? string.Empty;
      var title = titleElement.GetString()?.Trim() ?? string.Empty;

      if (
        id.Length is < 1 or > 40
        || !identifiers.Add(
          id
        )
      )
      {
        throw new LocalActionException(
          "execution-plan",
          "Execution plan step IDs must be unique and contain between 1 and 40 characters."
        );
      }

      if (
        title.Length is < 1 or > 100
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

      steps.Add(
        new ExecutionPlanStep(
          id,
          title,
          "pending"
        )
      );
    }

    return new ExecutionPlanView(
      objective,
      steps,
      null,
      0,
      revisionCount
    );
  }
}
