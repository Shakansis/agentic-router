using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Execution;

public interface ILocalActionPlanner
{
  Task<LocalActionProposal?> PlanAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    int attemptNumber,
    CancellationToken cancellationToken
  );
}

public sealed class LocalActionPlanner : ILocalActionPlanner
{
  public const string PlannerMarker = "LOCAL_ACTION_PLANNER_V1";

  private const string PlannerPrompt =
    PlannerMarker + "\n"
    + "Coordinate controlled local tools by returning exactly one safe action at a time. "
    + "When a user message begins with EXPERT_EXECUTION_GUIDANCE_V1, you are the resident "
    + "tooling bridge: translate the specialist model's guidance into actions and preserve "
    + "its exact paths and file contents. Otherwise, derive the required actions directly "
    + "from the user request and context; you may be either the confirmed target model or "
    + "the resident agent taking over without specialist guidance. "
    + "Re-evaluate the remaining work after every LOCAL_ACTION_RESULT and never repeat a "
    + "completed action. "
    + "Return JSON only. To request a tool return "
    + "{\"tool\":\"tool_name\",\"arguments\":{},\"explanation\":\"short reason\"}. "
    + "When no more local action is needed return "
    + "{\"tool\":null,\"arguments\":{},\"explanation\":\"ready to answer\"}. "
    + "Available tools and arguments: "
    + "list_files {path,recursive}; read_file {path}; "
    + "create_file {path,content}; write_file {path,content}; "
    + "replace_text {path,oldText,newText,replaceAll}; "
    + "apply_patch {path,replacements:[{oldText,newText}]}; "
    + "create_directory {path}; "
    + "run_process {executable,arguments:[string],workingDirectory,timeoutSeconds}. "
    + "The application host is Windows. Use list_files to inspect directories; do not use "
    + "Unix commands such as ls, and do not invoke dir through a shell. Shell interpreters "
    + "are intentionally unavailable. "
    + "Use paths relative to the trusted workspace. Never request deletion, moving, "
    + "a shell interpreter, command chaining, or access outside the workspace. "
    + "Do not return a prose plan, do not claim execution, and do not stop while the "
    + "specialist guidance still contains an uncompleted local action.";

  private readonly IOllamaClient _ollamaClient;

  public LocalActionPlanner(
    IOllamaClient ollamaClient
  )
  {
    _ollamaClient = ollamaClient;
  }

  public async Task<LocalActionProposal?> PlanAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    int attemptNumber,
    CancellationToken cancellationToken
  )
  {
    var prompt = attemptNumber > 1
      ? PlannerPrompt
        + $"\nThis is retry attempt {attemptNumber}. The previous response was empty, invalid, "
        + "unavailable, or rejected during action validation. Return exactly one valid JSON "
        + "object matching the contract and use only an available tool name."
      : PlannerPrompt;
    var plannerMessages = new[]
    {
      new ChatMessage(
        "system",
        prompt
      )
    }.Concat(
      messages
    ).ToArray();
    var output = await _ollamaClient.GenerateJsonAsync(
      baseUri,
      model,
      plannerMessages,
      "local-action-planning",
      cancellationToken
    );

    try
    {
      using var document = JsonDocument.Parse(
        output
      );
      var root = document.RootElement;

      if (
        root.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty(
          "tool",
          out var toolElement
        )
      )
      {
        throw new JsonException(
          "The planner response must contain a tool property."
        );
      }

      if (toolElement.ValueKind == JsonValueKind.Null)
      {
        return null;
      }

      if (toolElement.ValueKind != JsonValueKind.String)
      {
        throw new JsonException(
          "The planner tool property must be a string or null."
        );
      }

      var tool = toolElement.GetString();

      if (
        tool is not null
        && IsNoActionSentinel(
          tool
        )
      )
      {
        if (messages.Any(
          message => message.Content.StartsWith(
            "LOCAL_ACTION_RESULT",
            StringComparison.Ordinal
          )
        ))
        {
          return null;
        }

        throw new JsonException(
          "The planner returned a textual no-action sentinel before any completed local action. "
            + "Use JSON null only when no action is required."
        );
      }

      if (string.IsNullOrWhiteSpace(
        tool
      ))
      {
        throw new JsonException(
          "The planner tool name cannot be empty."
        );
      }

      var arguments = root.TryGetProperty(
        "arguments",
        out var argumentsElement
      ) && argumentsElement.ValueKind == JsonValueKind.Object
        ? argumentsElement.Clone()
        : JsonSerializer.SerializeToElement(
          new Dictionary<string, object?>()
        );
      var explanation = root.TryGetProperty(
        "explanation",
        out var explanationElement
      ) && explanationElement.ValueKind == JsonValueKind.String
        ? explanationElement.GetString()
        : null;

      return new LocalActionProposal(
        tool,
        arguments,
        explanation
      );
    }
    catch (JsonException exception)
    {
      throw new LocalActionException(
        "local-action-planning",
        "The model returned an invalid local action proposal.",
        exception
      );
    }
  }

  private static bool IsNoActionSentinel(
    string tool
  )
  {
    return tool.Trim() switch
    {
      var value when value.Equals(
        "null",
        StringComparison.OrdinalIgnoreCase
      ) => true,
      var value when value.Equals(
        "none",
        StringComparison.OrdinalIgnoreCase
      ) => true,
      var value when value.Equals(
        "no_action",
        StringComparison.OrdinalIgnoreCase
      ) => true,
      var value when value.Equals(
        "no-action",
        StringComparison.OrdinalIgnoreCase
      ) => true,
      _ => false
    };
  }
}
