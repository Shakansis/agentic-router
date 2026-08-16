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
    SpecialistToolingProfile toolingProfile,
    string hostStateSummary,
    long maximumInputTokens,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed
  );

  Task<LocalActionPlanningResult> PlanAsync(
    Uri baseUri,
    string model,
    SpecialistToolingProfile toolingProfile,
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
  public const string PlannerMarker = "SPECIALIST_TOOL_LOOP_V2";
  private const int RetainedToolPairs = 4;

  private const string PlannerPrompt =
    PlannerMarker + "\n"
    + "You are the selected specialist and retain ownership of this task. When another local "
    + "action is necessary, return exactly one safe tool call, observe the authoritative result, and decide the "
    + "next action yourself. The Host executes tools and enforces deterministic boundaries; "
    + "it does not reinterpret your intent through another model. "
    + "Treat minor spelling and grammar mistakes as recoverable input. When a phrase is "
    + "ambiguous or unfamiliar, use the surrounding requested assets and constraints to choose "
    + "the most conventional coherent interpretation, keep that assumption visible in the final result, "
    + "and continue without inventing unrelated behavior. "
    + "Re-evaluate the remaining work after every authoritative tool result and never repeat a "
    + "completed action. "
    + "Use exactly one native tool call when a local action is required. "
    + "When the user says to use, reuse, integrate, or inspect an existing file, dependency, or "
    + "asset, inspect it instead of creating or overwriting it. If the stated path does not "
    + "exist, list its parent directory and inspect the actual candidate. Never create a "
    + "duplicate file merely to make a mistaken name true. For a referenced existing folder, "
    + "list the folder and read the relevant implementation files before integration; preserve "
    + "them and use only public names and paths observed in their contents. Review cross-file "
    + "references and every explicit content or behavior constraint before completion. "
    + "A response without a tool call is a completion proposal. Return a concise final response "
    + "without a tool call when the requested work is complete or no safe action remains. "
    + "The Host supplies a request-specific closed tool set with native schemas. "
    + "A tool omitted from that set is outside the Host-authorized capabilities for this turn. "
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
    + "Do not return a prose plan and do not claim execution without an authoritative tool result.";

  private static readonly IReadOnlyList<CanonicalToolDefinition> ToolDefinitions =
    CreateToolDefinitions();

  private readonly IOllamaClient _ollamaClient;
  private readonly IToolNameResolver _toolNames;
  private readonly ITokenEstimator _tokenEstimator;
  private readonly ISpecialistToolingProtocol _toolingProtocol;

  public LocalActionPlanner(
    IOllamaClient ollamaClient,
    IToolNameResolver toolNames,
    ITokenEstimator tokenEstimator,
    ISpecialistToolingProtocol toolingProtocol
  )
  {
    _ollamaClient = ollamaClient;
    _toolNames = toolNames;
    _tokenEstimator = tokenEstimator;
    _toolingProtocol = toolingProtocol;
  }

  public CoordinatorContextFit FitToBudget(
    IReadOnlyList<OllamaToolMessage> messages,
    SpecialistToolingProfile toolingProfile,
    string hostStateSummary,
    long maximumInputTokens,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed
  )
  {
    var before = EstimateRequest(messages, toolingProfile, planRequired, attemptNumber);
    var hasReplaceableHistory = messages.Count(
      item => item.Role == "tool"
    ) > RetainedToolPairs;
    if (before <= maximumInputTokens && !hasReplaceableHistory)
    {
      return new CoordinatorContextFit(messages.ToArray(), before, before, false, false, "already-fits");
    }

    var compact = new List<OllamaToolMessage>();
    AddLast(compact, messages, item => item.Role == "system" && item.Content?.StartsWith("APPLICATION_OWNED_PROJECT_CONTEXT", StringComparison.Ordinal) == true);
    AddLast(compact, messages, item => item.Role == "user" && item.Content?.StartsWith(ExpertExecutionGuidanceService.GuidanceMarker, StringComparison.Ordinal) != true && !IsControlMessage(item.Content));
    AddLast(compact, messages, item => item.Content?.StartsWith(ExpertExecutionGuidanceService.GuidanceMarker, StringComparison.Ordinal) == true);
    compact.Add(new OllamaToolMessage("system", $"APPLICATION_OWNED_EXECUTION_STATE_V1\n{hostStateSummary}"));

    var retainedToolIndexes = messages.Select(
      (item, index) => (item, index)
    ).Where(
      pair => pair.item.Role == "tool"
    ).TakeLast(
      RetainedToolPairs
    ).Select(
      pair => pair.index
    ).ToArray();
    var previousToolIndex = -1;
    foreach (var toolIndex in retainedToolIndexes)
    {
      var assistant = messages.Skip(
        previousToolIndex + 1
      ).Take(
        toolIndex - previousToolIndex - 1
      ).LastOrDefault(
        item => item.Role == "assistant"
      );
      if (assistant is not null)
      {
        compact.Add(assistant);
      }
      compact.Add(messages[toolIndex]);
      previousToolIndex = toolIndex;
    }

    var latestControl = messages.LastOrDefault(item => IsControlMessage(item.Content));
    if (latestControl is not null)
    {
      compact.Add(latestControl);
    }

    compact = compact.DistinctBy(MessageFingerprint, StringComparer.Ordinal).ToList();
    var after = EstimateRequest(compact, toolingProfile, planRequired, attemptNumber);
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
    SpecialistToolingProfile toolingProfile,
    IReadOnlyList<OllamaToolMessage> messages,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var request = CreatePlanningRequest(messages, toolingProfile, planRequired, attemptNumber);
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
    var canonicalTurn = _toolingProtocol.Normalize(toolingProfile, response);
    var assistantMessage = _toolingProtocol.CreateAssistantMessage(canonicalTurn);

    try
    {
      return ParseResponse(
        completionAllowed,
        response,
        canonicalTurn,
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
    SpecialistToolingProfile toolingProfile,
    bool planRequired,
    int attemptNumber
  )
  {
    var scope = ExecutionTurnToolPolicy.Resolve(
      messages.Select(message => (message.Role, message.Content))
    );
    var canonicalTools = ToolDefinitions.Where(
      tool => tool.Name is not "create_execution_plan" and not "revise_execution_plan"
        && scope.Allows(tool.Name)
    ).ToArray();
    var availableTools = _toolingProtocol.ToOllamaDefinitions(
      toolingProfile,
      canonicalTools
    );
    var prompt = PlannerPrompt
      + $"\n{toolingProfile.PromptInstructions}"
      + $"\nHost-owned turn constraints:\n{ExecutionTurnToolPolicy.Describe(scope)}"
      + $"\nTools offered in this phase: {string.Join(", ", availableTools.Select(tool => tool.Name))}."
      + (
        attemptNumber > 1
          ? $"\nThis is retry attempt {attemptNumber}. The previous response was empty, invalid, "
            + "unavailable, or rejected during action validation. Change strategy: return one valid "
            + "available tool call when another action is necessary, or a final response without a "
            + "tool call when the verified work is complete or no safe action remains."
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

  private long EstimateRequest(
    IReadOnlyList<OllamaToolMessage> messages,
    SpecialistToolingProfile toolingProfile,
    bool planRequired,
    int attemptNumber
  )
  {
    var request = CreatePlanningRequest(messages, toolingProfile, planRequired, attemptNumber);
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
      || content?.StartsWith("LOCAL_ACTION_PLANNING_CORRECTION", StringComparison.Ordinal) == true
      || content?.StartsWith("TOOL_PROTOCOL_CORRECTION", StringComparison.Ordinal) == true
      || content?.StartsWith("LOCAL_ACTION_CORRECTION", StringComparison.Ordinal) == true
      || content?.StartsWith("HOST_REVIEW_EVIDENCE_REJECTED", StringComparison.Ordinal) == true
      || content?.StartsWith("PLAN_ACTION_NOT_BOUND", StringComparison.Ordinal) == true
      || content?.StartsWith("EXECUTION_COMPLETION_REJECTED", StringComparison.Ordinal) == true
      || content?.StartsWith("COMPLETION_REJECTED", StringComparison.Ordinal) == true
      || content?.StartsWith("HOST_COMPLETION_FACTS", StringComparison.Ordinal) == true
      || content?.StartsWith("RECOVERY_", StringComparison.Ordinal) == true
      || content?.StartsWith("RESIDENT_", StringComparison.Ordinal) == true
      || content?.StartsWith("AUTHORITATIVE_EXECUTION_SESSION_FACTS", StringComparison.Ordinal) == true;
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
    bool completionAllowed,
    OllamaToolResponse response,
    CanonicalSpecialistTurn canonicalTurn,
    OllamaToolMessage assistantMessage,
    IReadOnlyList<OllamaToolDefinition> availableTools
  )
  {
    try
    {
      if (canonicalTurn.ToolCalls.Count == 0)
      {
        if (
          completionAllowed
          && canonicalTurn.Completion is not null
        )
        {
          return new LocalActionPlanningResult(
            null,
            assistantMessage,
            false,
            ContextResolution: response.ContextResolution,
            CallId: null
          );
        }

        throw new JsonException(
          canonicalTurn.Completion is null
            ? "The coordinator returned neither a native tool call nor a usable final response."
            : "The specialist proposed completion before the Host verified the required effects. "
              + "It must call one available tool that materially advances the objective."
        );
      }

      var call = canonicalTurn.ToolCalls[0];
      var tool = call.Name;

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
        canonicalTurn.IgnoredToolCallCount,
        response.ContextResolution,
        call.CallId
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

  private static IReadOnlyList<CanonicalToolDefinition> CreateToolDefinitions()
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
              },
              additionalProperties = false
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
        "Run one structured executable and argument list without a shell only when process execution materially advances or validates the requested goal.",
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

  private static CanonicalToolDefinition Tool(
    string name,
    string description,
    object properties,
    IReadOnlyList<string> required
  )
  {
    return new CanonicalToolDefinition(
      name,
      description,
      JsonSerializer.SerializeToElement(
        new
        {
          type = "object",
          properties,
          required,
          additionalProperties = false
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
            ),
            target = PlanStringProperty(
              72,
              "Workspace-relative file path for this file step. If the user did not name it, choose a concise compatible filename and extension. Omit only for non-file steps."
            )
          },
          required = new[]
          {
            "title"
          },
          additionalProperties = false
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

}

public sealed record LocalActionPlanningResult(
  LocalActionProposal? Proposal,
  OllamaToolMessage AssistantMessage,
  bool ExplicitNoAction,
  int IgnoredToolCallCount = 0,
  OllamaContextResolution? ContextResolution = null,
  string? CallId = null
);
