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

  ExecutionPlanView NormalizeExistingReferencedDependencies(
    ExecutionPlanView plan,
    string objective,
    string? workspacePath
  );
}

public sealed class ExecutionPlanService : IExecutionPlanService
{
  private static readonly string[] ReuseIntentFragments =
  [
    "use",
    "reuse",
    "integrate",
    "existing",
    "supplied",
    "inside",
    "from",
    "usar",
    "reutilizar",
    "integrar",
    "existente",
    "fornecido",
    "dentro",
    "a partir"
  ];

  private static readonly string[] ExplicitMutationFragments =
  [
    "edit",
    "update",
    "modify",
    "change",
    "rewrite",
    "overwrite",
    "replace",
    "delete",
    "editar",
    "atualizar",
    "modificar",
    "alterar",
    "reescrever",
    "sobrescrever",
    "substituir",
    "excluir",
    "apagar"
  ];

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
    var steps = parsed.Titles.Select(
      (
        title,
        index
      ) => new ExecutionPlanStep(
        CreateStepId(
          index + 1
        ),
        title,
        "pending"
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

    for (var index = 0; index < parsed.Titles.Count; index++)
    {
      var title = parsed.Titles[index];
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
    }

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

  public ExecutionPlanView NormalizeExistingReferencedDependencies(
    ExecutionPlanView plan,
    string objective,
    string? workspacePath
  )
  {
    if (
      string.IsNullOrWhiteSpace(workspacePath)
      || !ContainsAny(objective, ReuseIntentFragments)
      || ContainsAny(objective, ExplicitMutationFragments)
    )
    {
      return plan;
    }

    string workspaceRoot;
    try
    {
      workspaceRoot = Path.GetFullPath(workspacePath);
    }
    catch (
      Exception exception
    ) when (
      exception is ArgumentException
      or NotSupportedException
      or PathTooLongException
    )
    {
      return plan;
    }

    var rootWithSeparator = Path.EndsInDirectorySeparator(workspaceRoot)
      ? workspaceRoot
      : workspaceRoot + Path.DirectorySeparatorChar;
    var changed = false;
    var steps = plan.Steps.Select(
      step =>
      {
        var target = ToolEffectRegistry.TryGetHostTarget(step.Title);
        if (
          target is null
          || ToolEffectRegistry.InferExpectedEffect(step.Title) != ToolEffects.FileCreated
          || !ObjectiveReferencesTarget(objective, target)
        )
        {
          return step;
        }

        string candidate;
        try
        {
          candidate = Path.GetFullPath(
            Path.Combine(
              workspaceRoot,
              target.Replace('/', Path.DirectorySeparatorChar)
            )
          );
        }
        catch (
          Exception exception
        ) when (
          exception is ArgumentException
          or NotSupportedException
          or PathTooLongException
        )
        {
          return step;
        }

        if (
          !candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
          || !File.Exists(candidate)
        )
        {
          return step;
        }

        changed = true;
        return step with
        {
          Title = ToolEffectRegistry.WithHostTarget(
            "Inspect existing referenced dependency",
            target
          )
        };
      }
    ).ToArray();

    return changed
      ? plan with
      {
        Steps = steps
      }
      : plan;
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

    var titles = new List<string>();

    foreach (var element in elements)
    {
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

      if (
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("target", out var targetElement)
        && targetElement.ValueKind == JsonValueKind.String
        && NormalizeStepTarget(targetElement.GetString()) is
        {
        } target
      )
      {
        title = ToolEffectRegistry.WithHostTarget(title, target);
      }

      titles.Add(
        title
      );
    }

    return new ParsedPlan(
      objective,
      titles
    );
  }

  private static string? NormalizeStepTarget(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var segments = new List<string>();
    foreach (var rawSegment in value.Replace('\\', '/').Split('/'))
    {
      var segment = rawSegment.Trim();
      if (
        segment.Length == 0
        || segment == "."
        || segment == ".."
        || (segment.Length == 2 && char.IsLetter(segment[0]) && segment[1] == ':')
      )
      {
        continue;
      }

      var safe = new string(
        segment.Where(
          character => !invalid.Contains(character) && !char.IsControl(character)
        ).ToArray()
      ).Trim();
      if (safe.Length > 0)
      {
        segments.Add(safe);
      }
    }

    if (segments.Count == 0)
    {
      return null;
    }

    var normalized = string.Join('/', segments);
    return normalized.Length <= 72
      ? normalized
      : normalized[^72..].TrimStart('/');
  }

  private static bool ObjectiveReferencesTarget(
    string objective,
    string target
  )
  {
    var normalizedObjective = objective.Replace('\\', '/');
    var normalizedTarget = target.Replace('\\', '/');
    var fileName = Path.GetFileName(normalizedTarget);
    return normalizedObjective.Contains(
        normalizedTarget,
        StringComparison.OrdinalIgnoreCase
      )
      || (
        fileName.Length > 0
        && normalizedObjective.Contains(
          fileName,
          StringComparison.OrdinalIgnoreCase
        )
      );
  }

  private static bool ContainsAny(
    string value,
    IEnumerable<string> fragments
  )
  {
    return fragments.Any(
      fragment => value.Contains(
        fragment,
        StringComparison.OrdinalIgnoreCase
      )
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
    IReadOnlyList<string> Titles
  );
}
