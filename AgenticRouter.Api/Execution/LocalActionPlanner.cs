using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Execution;

public interface ILocalActionPlanner
{
  Task<LocalActionPlanningResult> PlanAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
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
    + "Use exactly one native tool call when a local action is required. "
    + "Before the first local action, call create_execution_plan once with a 1 to 8 step "
    + "checklist. When bridging structured specialist guidance, create one plan step for "
    + "each guidance action and preserve its id and title. Use revise_execution_plan only "
    + "when execution facts require changing "
    + "remaining steps. The plan cannot execute commands. "
    + "Before any completed tool result, when no local action is needed, reply exactly "
    + "NO_LOCAL_ACTION_REQUIRED without a tool call. After completed tool results, return a "
    + "concise final response without a tool call only when every planned step is complete. "
    + "Available tools and arguments: "
    + "list_files {path,recursive}; read_file {path}; "
    + "get_file_info {path}; search_text {path,query}; "
    + "create_file {path,content}; write_file {path,content}; "
    + "replace_text {path,oldText,newText,replaceAll}; "
    + "apply_patch {path,replacements:[{oldText,newText}]}; "
    + "create_directory {path}; "
    + "run_process {executable,arguments:[string],workingDirectory,timeoutSeconds}; "
    + "run_validation_profile {}. "
    + "The application host is Windows. Use list_files to inspect directories; do not use "
    + "Unix commands such as ls, and do not invoke dir through a shell. Shell interpreters "
    + "are intentionally unavailable. "
    + "Use paths relative to the trusted workspace. Never request deletion, moving, "
    + "a shell interpreter, command chaining, or access outside the workspace. "
    + "Do not return a prose plan, do not claim execution, and do not stop while the "
    + "specialist guidance still contains an uncompleted local action.";

  private static readonly IReadOnlyList<OllamaToolDefinition> ToolDefinitions =
    CreateToolDefinitions();

  private readonly IOllamaClient _ollamaClient;

  public LocalActionPlanner(
    IOllamaClient ollamaClient
  )
  {
    _ollamaClient = ollamaClient;
  }

  public async Task<LocalActionPlanningResult> PlanAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    CancellationToken cancellationToken
  )
  {
    var availableTools = planRequired
      ? ToolDefinitions.Where(
        tool => tool.Name == "create_execution_plan"
      ).ToArray()
      : ToolDefinitions.Where(
        tool => tool.Name != "create_execution_plan"
      ).ToArray();
    var prompt = PlannerPrompt
      + (
        planRequired
          ? "\nNo valid execution plan is stored. Call create_execution_plan now; no other "
            + "tool is available until that plan passes validation."
          : "\nA valid execution plan is already stored. Do not call create_execution_plan again."
      )
      + (
        attemptNumber > 1
          ? $"\nThis is retry attempt {attemptNumber}. The previous response was empty, invalid, "
            + "unavailable, or rejected during action validation. Return exactly one valid native "
            + "tool call using an available tool name."
          : string.Empty
      );
    var plannerMessages = new[]
    {
      new OllamaToolMessage(
        "system",
        prompt
      )
    }.Concat(
      messages
    ).ToArray();
    var response = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      plannerMessages,
      availableTools,
      "local-action-planning",
      cancellationToken
    );
    var assistantMessage = new OllamaToolMessage(
      "assistant",
      response.Content,
      response.Thinking,
      response.ToolCalls
    );

    try
    {
      if (response.ToolCalls.Count > 1)
      {
        throw new JsonException(
          "The coordinator returned more than one native tool call."
        );
      }

      if (response.ToolCalls.Count == 0)
      {
        if (IsExplicitNoAction(
          response.Content
        ))
        {
          return new LocalActionPlanningResult(
            null,
            assistantMessage,
            true
          );
        }

        if (
          completionAllowed
          && !string.IsNullOrWhiteSpace(
            response.Content
          )
        )
        {
          return new LocalActionPlanningResult(
            null,
            assistantMessage,
            false
          );
        }

        throw new JsonException(
          string.IsNullOrWhiteSpace(
            response.Content
          )
            ? "The coordinator returned neither a native tool call nor a usable final response."
            : "The coordinator returned prose before local execution was complete. "
              + "It must call one available tool or return the exact NO_LOCAL_ACTION_REQUIRED sentinel."
        );
      }

      var call = response.ToolCalls[0];
      var tool = call.Name;

      if (
        tool is not null
        && IsNoActionSentinel(
          tool
        )
      )
      {
        if (messages.Any(
          message => message.Role == "tool"
        ))
        {
          return new LocalActionPlanningResult(
            null,
            assistantMessage,
            true
          );
        }

        throw new JsonException(
          "The coordinator returned a textual no-action sentinel as a native tool name."
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

      if (!availableTools.Any(
        available => available.Name == tool
      ))
      {
        throw new JsonException(
          $"Tool '{tool}' was not offered for the current planning phase."
        );
      }

      if (call.Arguments.ValueKind != JsonValueKind.Object)
      {
        throw new JsonException(
          "Native tool-call arguments must be a JSON object."
        );
      }

      return new LocalActionPlanningResult(
        new LocalActionProposal(
          tool,
          call.Arguments.Clone(),
          null
        ),
        assistantMessage,
        false
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

  private static IReadOnlyList<OllamaToolDefinition> CreateToolDefinitions()
  {
    return
    [
      Tool(
        "create_execution_plan",
        "Create the visible bounded checklist before the first local action.",
        PlanProperties(),
        [
          "objective",
          "steps"
        ]
      ),
      Tool(
        "revise_execution_plan",
        "Revise only remaining plan steps while preserving completed or failed steps.",
        PlanProperties(),
        [
          "objective",
          "steps"
        ]
      ),
      Tool(
        "list_files",
        "List bounded entries inside the trusted workspace.",
        new
        {
          path = StringProperty(),
          recursive = BooleanProperty()
        },
        ["path"]
      ),
      Tool(
        "read_file",
        "Read a bounded text file inside the trusted workspace.",
        new
        {
          path = StringProperty()
        },
        ["path"]
      ),
      Tool(
        "get_file_info",
        "Get bounded file or directory metadata.",
        new
        {
          path = StringProperty()
        },
        ["path"]
      ),
      Tool(
        "search_text",
        "Search text internally without starting a shell.",
        new
        {
          path = StringProperty(),
          query = StringProperty()
        },
        ["path", "query"]
      ),
      Tool(
        "create_file",
        "Create a new UTF-8 text file.",
        new
        {
          path = StringProperty(),
          content = StringProperty()
        },
        ["path", "content"]
      ),
      Tool(
        "write_file",
        "Replace an existing UTF-8 text file.",
        new
        {
          path = StringProperty(),
          content = StringProperty()
        },
        ["path", "content"]
      ),
      Tool(
        "replace_text",
        "Replace exact text in an existing file.",
        new
        {
          path = StringProperty(),
          oldText = StringProperty(),
          newText = StringProperty(),
          replaceAll = BooleanProperty()
        },
        ["path", "oldText", "newText"]
      ),
      Tool(
        "apply_patch",
        "Apply bounded exact search and replacement blocks to one file.",
        new
        {
          path = StringProperty(),
          replacements = new
          {
            type = "array",
            items = new
            {
              type = "object",
              properties = new
              {
                oldText = StringProperty(),
                newText = StringProperty()
              },
              required = new[]
              {
                "oldText",
                "newText"
              }
            }
          }
        },
        ["path", "replacements"]
      ),
      Tool(
        "create_directory",
        "Create one directory inside the trusted workspace.",
        new
        {
          path = StringProperty()
        },
        ["path"]
      ),
      Tool(
        "run_process",
        "Run one structured process without a shell.",
        new
        {
          executable = StringProperty(),
          arguments = new
          {
            type = "array",
            items = StringProperty()
          },
          workingDirectory = StringProperty(),
          timeoutSeconds = new
          {
            type = "integer"
          }
        },
        ["executable", "arguments", "workingDirectory"]
      ),
      Tool(
        "run_validation_profile",
        "Run the already saved active validation profile.",
        new
        {
        },
        []
      )
    ];
  }

  private static OllamaToolDefinition Tool(
    string name,
    string description,
    object properties,
    IReadOnlyList<string> required
  )
  {
    return new OllamaToolDefinition(
      name,
      description,
      JsonSerializer.SerializeToElement(
        new
        {
          type = "object",
          properties,
          required
        }
      )
    );
  }

  private static object StringProperty()
  {
    return new
    {
      type = "string"
    };
  }

  private static object BooleanProperty()
  {
    return new
    {
      type = "boolean"
    };
  }

  private static object PlanProperties()
  {
    return new
    {
      objective = PlanStringProperty(
        240,
        "Short textual objective."
      ),
      steps = new
      {
        type = "array",
        minItems = 1,
        maxItems = 8,
        items = new
        {
          type = "object",
          properties = new
          {
            id = PlanStringProperty(
              40,
              "Unique stable step identifier."
            ),
            title = PlanStringProperty(
              100,
              "Short single-line description only; do not include commands, code, or executable content."
            )
          },
          required = new[]
          {
            "id",
            "title"
          }
        }
      }
    };
  }

  private static object PlanStringProperty(
    int maximumLength,
    string description
  )
  {
    return new
    {
      type = "string",
      minLength = 1,
      maxLength = maximumLength,
      description
    };
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

  private static bool IsExplicitNoAction(
    string? content
  )
  {
    return string.Equals(
      content?.Trim(),
      "NO_LOCAL_ACTION_REQUIRED",
      StringComparison.Ordinal
    );
  }
}

public sealed record LocalActionPlanningResult(
  LocalActionProposal? Proposal,
  OllamaToolMessage AssistantMessage,
  bool ExplicitNoAction
);
