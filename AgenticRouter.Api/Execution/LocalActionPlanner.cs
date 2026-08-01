using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Execution;

public sealed record CoordinatorContextFit(
  IReadOnlyList<OllamaToolMessage> Messages,
  long BeforeTokens,
  long AfterTokens,
  bool Compacted,
  bool TooLarge,
  string Outcome
);

public interface ILocalActionPlanner
{
  CoordinatorContextFit FitToBudget(
    IReadOnlyList<OllamaToolMessage> messages,
    string hostStateSummary,
    long maximumInputTokens,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed
  );

  Task<LocalActionPlanningResult> PlanAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    ProviderCallContext usageContext,
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
    + "each guidance action and preserve its title and order. The Host generates stable "
    + "step IDs; never invent or return IDs. Use revise_execution_plan only "
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
    + "delete_files {paths:[string]}; "
    + "create_directory {path}; "
    + "run_process {executable,arguments:[string],workingDirectory,timeoutSeconds}; "
    + "run_validation_profile {}; "
    + "git_status {}; git_diff {paths:[string],staged}; git_log {maxEntries}; "
    + "git_show_commit {commit}; git_stage_files {paths:[string]}; "
    + "git_unstage_files {paths:[string]}; git_create_commit {message,commitWithoutValidation}; "
    + "git_create_annotated_tag {tag,annotation}; git_push_current_branch {}; "
    + "git_push_tag {}. "
    + "Every Git write is separately approved by the user even under automatic approval. "
    + "The application host is Windows. Use list_files to inspect directories; do not use "
    + "Unix commands such as ls, and do not invoke dir through a shell. Shell interpreters "
    + "are intentionally unavailable. "
    + "The trusted workspace is already the project root. Use paths relative to it and "
    + "always use '/' as the path separator in tool arguments; never place an unescaped "
    + "backslash in JSON paths. "
    + "Never prefix a path with the workspace display name or root directory name. Before "
    + "editing, fixing, or updating existing files, inspect their real paths and contents. "
    + "Never use create_file as a substitute for editing an existing file. "
    + "Deletion is available only through delete_files with an explicit list of existing file paths. "
    + "Never request moving, "
    + "a shell interpreter, command chaining, or access outside the workspace. "
    + "Do not return a prose plan, do not claim execution, and do not stop while the "
    + "specialist guidance still contains an uncompleted local action.";

  private static readonly IReadOnlyList<OllamaToolDefinition> ToolDefinitions =
    CreateToolDefinitions();

  private readonly IOllamaClient _ollamaClient;
  private readonly IToolNameResolver _toolNames;
  private readonly ITokenEstimator _tokenEstimator;

  public LocalActionPlanner(
    IOllamaClient ollamaClient,
    IToolNameResolver toolNames,
    ITokenEstimator tokenEstimator
  )
  {
    _ollamaClient = ollamaClient;
    _toolNames = toolNames;
    _tokenEstimator = tokenEstimator;
  }

  public CoordinatorContextFit FitToBudget(
    IReadOnlyList<OllamaToolMessage> messages,
    string hostStateSummary,
    long maximumInputTokens,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed
  )
  {
    var before = EstimateRequest(messages, planRequired, attemptNumber);
    if (before <= maximumInputTokens)
    {
      return new CoordinatorContextFit(messages.ToArray(), before, before, false, false, "already-fits");
    }

    var compact = new List<OllamaToolMessage>();
    AddLast(compact, messages, item => item.Role == "system" && item.Content?.StartsWith("APPLICATION_OWNED_PROJECT_CONTEXT", StringComparison.Ordinal) == true);
    AddLast(compact, messages, item => item.Role == "user" && item.Content?.StartsWith(ExpertExecutionGuidanceService.GuidanceMarker, StringComparison.Ordinal) != true && !IsControlMessage(item.Content));
    AddLast(compact, messages, item => item.Content?.StartsWith(ExpertExecutionGuidanceService.GuidanceMarker, StringComparison.Ordinal) == true);
    compact.Add(new OllamaToolMessage("system", $"APPLICATION_OWNED_EXECUTION_STATE_V1\n{hostStateSummary}"));

    var latestControl = messages.LastOrDefault(item => IsControlMessage(item.Content));
    if (latestControl is not null)
    {
      compact.Add(latestControl);
    }

    var latestToolIndex = messages.Select((item, index) => (item, index)).LastOrDefault(pair => pair.item.Role == "tool").index;
    if (
      latestToolIndex > 0
      && messages[latestToolIndex].Content?.StartsWith("Status: completed", StringComparison.Ordinal) != true
    )
    {
      var assistant = messages.Take(latestToolIndex).LastOrDefault(item => item.Role == "assistant");
      if (assistant is not null) compact.Add(assistant);
      compact.Add(messages[latestToolIndex]);
    }

    compact = compact.DistinctBy(MessageFingerprint, StringComparer.Ordinal).ToList();
    var after = EstimateRequest(compact, planRequired, attemptNumber);
    var tooLarge = after > maximumInputTokens;
    var materiallySmaller = after <= before - Math.Max(256, before / 10);
    return new CoordinatorContextFit(
      compact,
      before,
      after,
      true,
      tooLarge,
      tooLarge ? "required-context-item-too-large" : materiallySmaller ? "compacted" : "not-materially-smaller"
    );
  }

  public async Task<LocalActionPlanningResult> PlanAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var request = CreatePlanningRequest(messages, planRequired, attemptNumber);
    var availableTools = request.Tools;
    var plannerMessages = request.Messages;
    var response = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      plannerMessages,
      availableTools,
      "local-action-planning",
      usageContext,
      cancellationToken
    );
    var selectedToolCalls = response.ToolCalls.Take(
      1
    ).ToArray();
    var assistantMessage = new OllamaToolMessage(
      "assistant",
      response.Content,
      response.Thinking,
      selectedToolCalls
    );

    try
    {
      return ParseResponse(
        messages,
        completionAllowed,
        response,
        selectedToolCalls,
        assistantMessage,
        availableTools
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

  private PlanningRequest CreatePlanningRequest(
    IReadOnlyList<OllamaToolMessage> messages,
    bool planRequired,
    int attemptNumber
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
    return new PlanningRequest(plannerMessages, availableTools);
  }

  private long EstimateRequest(IReadOnlyList<OllamaToolMessage> messages, bool planRequired, int attemptNumber)
  {
    var request = CreatePlanningRequest(messages, planRequired, attemptNumber);
    return _tokenEstimator.EstimateToolMessages(request.Messages)
      + _tokenEstimator.EstimateText(JsonSerializer.Serialize(request.Tools));
  }

  private static void AddLast(
    List<OllamaToolMessage> target,
    IReadOnlyList<OllamaToolMessage> messages,
    Func<OllamaToolMessage, bool> predicate
  )
  {
    var item = messages.LastOrDefault(predicate);
    if (item is not null) target.Add(item);
  }

  private static bool IsControlMessage(string? content)
  {
    return content?.StartsWith("LOCAL_ACTION_RESULT", StringComparison.Ordinal) == true
      || content?.StartsWith("STRUCTURED_ACTION_CORRECTION", StringComparison.Ordinal) == true
      || content?.StartsWith("COMPLETION_REJECTED", StringComparison.Ordinal) == true;
  }

  private static string MessageFingerprint(OllamaToolMessage message)
  {
    return $"{message.Role}:{message.ToolName}:{message.Content}:{message.ToolCalls?.Count ?? 0}";
  }

  private sealed record PlanningRequest(
    IReadOnlyList<OllamaToolMessage> Messages,
    IReadOnlyList<OllamaToolDefinition> Tools
  );

  private LocalActionPlanningResult ParseResponse(
    IReadOnlyList<OllamaToolMessage> messages,
    bool completionAllowed,
    OllamaToolResponse response,
    IReadOnlyList<OllamaToolCall> selectedToolCalls,
    OllamaToolMessage assistantMessage,
    IReadOnlyList<OllamaToolDefinition> availableTools
  )
  {
    try
    {
      if (selectedToolCalls.Count == 0)
      {
        if (IsExplicitNoAction(
          response.Content
        ))
        {
          return new LocalActionPlanningResult(
            null,
            assistantMessage,
            true,
            ContextResolution: response.ContextResolution
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
            false,
            ContextResolution: response.ContextResolution
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

      var call = selectedToolCalls[0];
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
            true,
            ContextResolution: response.ContextResolution
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

      var resolution = _toolNames.Resolve(
        tool,
        availableTools.Select(
          available => available.Name
        )
      );

      if (call.Arguments.ValueKind != JsonValueKind.Object)
      {
        throw new JsonException(
          "Native tool-call arguments must be a JSON object."
        );
      }

      return new LocalActionPlanningResult(
        new LocalActionProposal(
          resolution.CanonicalName,
          call.Arguments.Clone(),
          null,
          resolution.OriginalName,
          resolution.Source
        ),
        assistantMessage,
        false,
        response.ToolCalls.Count - selectedToolCalls.Count,
        response.ContextResolution
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
        "delete_files",
        "Delete an explicit bounded list of existing files. The Host validates and snapshots every path; directories are never removed and user approval is always required.",
        new
        {
          paths = StringArrayProperty()
        },
        ["paths"]
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
      ),
      Tool(
        "git_status",
        "Refresh structured Git status for the trusted repository.",
        new
        {
        },
        []
      ),
      Tool(
        "git_diff",
        "Read bounded authoritative Git diffs for explicit repository paths.",
        new
        {
          paths = StringArrayProperty(),
          staged = BooleanProperty()
        },
        ["paths"]
      ),
      Tool(
        "git_log",
        "Read a bounded structured Git log.",
        new
        {
          maxEntries = new
          {
            type = "integer"
          }
        },
        []
      ),
      Tool(
        "git_show_commit",
        "Read one structured Git commit.",
        new
        {
          commit = StringProperty()
        },
        ["commit"]
      ),
      Tool(
        "git_stage_files",
        "Stage only explicit repository-relative files after user approval.",
        new
        {
          paths = StringArrayProperty()
        },
        ["paths"]
      ),
      Tool(
        "git_unstage_files",
        "Unstage only explicit repository-relative files after user approval.",
        new
        {
          paths = StringArrayProperty()
        },
        ["paths"]
      ),
      Tool(
        "git_create_commit",
        "Create one commit from the exact approved staged set.",
        new
        {
          message = StringProperty(),
          commitWithoutValidation = BooleanProperty()
        },
        ["message"]
      ),
      Tool(
        "git_create_annotated_tag",
        "Create one annotated tag on the delivery commit.",
        new
        {
          tag = StringProperty(),
          annotation = StringProperty()
        },
        [
          "tag",
          "annotation"
        ]
      ),
      Tool(
        "git_push_current_branch",
        "Push only the current branch to its existing upstream after guarded preflight.",
        new
        {
        },
        []
      ),
      Tool(
        "git_push_tag",
        "Push only the exact annotated tag created for this delivery.",
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

  private static object StringArrayProperty()
  {
    return new
    {
      type = "array",
      minItems = 1,
      maxItems = 100,
      items = StringProperty()
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
            title = PlanStringProperty(
              100,
              "Short single-line description only; do not include commands, code, or executable content."
            )
          },
          required = new[]
          {
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
  bool ExplicitNoAction,
  int IgnoredToolCallCount = 0,
  OllamaContextResolution? ContextResolution = null
);
