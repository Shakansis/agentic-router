using System.Text.Json;
using System.Text.Json.Nodes;
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
    IReadOnlyCollection<string> grantedTools,
    string hostStateSummary,
    long maximumInputTokens,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    bool forceCompaction = false
  );

  SpecialistContextMeasurement MeasureRequest(
    IReadOnlyList<OllamaToolMessage> messages,
    SpecialistToolingProfile toolingProfile,
    IReadOnlyCollection<string> grantedTools,
    bool planRequired,
    int attemptNumber
  );

  Task<LocalActionPlanningResult> PlanAsync(
    Uri baseUri,
    string model,
    SpecialistToolingProfile toolingProfile,
    IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyCollection<string> grantedTools,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Func<string, CancellationToken, ValueTask>? onThinkingDelta = null
  );
}

public sealed class LocalActionPlanner : ILocalActionPlanner
{
  public const string PlannerMarker = "SPECIALIST_TOOL_LOOP_V2";
  public const string RequestToolsetTool = "request_toolset";
  public const string ToolCatalogMarker = "HOST_TOOL_CATALOG_V1";
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
    + "The Host supplies a compact catalog of available capabilities. Request the smallest set "
    + "of schemas needed through request_toolset before calling an executable tool. A catalog "
    + "entry is discoverable but cannot be called until its schema is granted. Requesting a "
    + "schema performs no workspace action and grants no approval to execute it. "
    + "An execution plan is optional. For a task that benefits from visible multi-step tracking, "
    + "you may request and call create_execution_plan with your own objective, titles, steps, and "
    + "dependencies. For a simple task, continue without a plan. The Host never invents plan steps. "
    + "The Host owns approval. Under ask, every mutation waits for approval; under auto, requested in-scope mutations execute after Host validation without a duplicate approval. "
    + "The application host is Windows. Use list_files to inspect directories; do not use "
    + "Unix commands such as ls, and do not invoke dir through a shell. Shell interpreters "
    + "are intentionally unavailable. "
    + "The trusted workspace is already the project root. Use paths relative to it and "
    + "always use '/' as the path separator in tool arguments; never place an unescaped "
    + "backslash in JSON paths. "
    + "Never prefix a path with the workspace display name or root directory name. Before "
    + "editing, fixing, or updating existing files, inspect their real paths and contents. "
    + "Never use create_file as a substitute for editing an existing file. "
    + "Deletion is available only through delete_paths with an explicit list of existing paths and an explicit recursive flag. "
    + "Never request moving, "
    + "a shell interpreter, command chaining, or access outside the workspace. "
    + "Do not return a prose plan and do not claim execution without an authoritative tool result.";

  private static readonly IReadOnlyList<CanonicalToolDefinition> ToolDefinitions =
    CreateToolDefinitions();

  public static IReadOnlyList<CanonicalToolDefinition> GetToolDefinitions(
    IEnumerable<string> names
  )
  {
    var requested = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    return ToolDefinitions.Where(tool => requested.Contains(tool.Name)).ToArray();
  }

  private static readonly CanonicalToolDefinition RequestToolsetDefinition =
    CreateRequestToolsetDefinition();

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
    IReadOnlyCollection<string> grantedTools,
    string hostStateSummary,
    long maximumInputTokens,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    bool forceCompaction = false
  )
  {
    var before = EstimateRequest(
      messages,
      toolingProfile,
      grantedTools,
      planRequired,
      attemptNumber
    );
    if (before <= maximumInputTokens && !forceCompaction)
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
        compact.Add(
          assistant with
          {
            Thinking = null
          }
        );
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
    var after = EstimateRequest(
      compact,
      toolingProfile,
      grantedTools,
      planRequired,
      attemptNumber
    );
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
    IReadOnlyCollection<string> grantedTools,
    bool planRequired,
    int attemptNumber,
    bool completionAllowed,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Func<string, CancellationToken, ValueTask>? onThinkingDelta = null
  )
  {
    var request = CreatePlanningRequest(
      messages,
      toolingProfile,
      grantedTools,
      planRequired,
      attemptNumber
    );
    var availableTools = request.Tools;
    var plannerMessages = request.Messages;
    var response = await _ollamaClient.GenerateToolCallAsync(
      baseUri,
      model,
      plannerMessages,
      availableTools,
      "local-action-planning",
      usageContext,
      cancellationToken,
      onThinkingDelta
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
    IReadOnlyCollection<string> grantedTools,
    bool planRequired,
    int attemptNumber
  )
  {
    var scope = ExecutionTurnToolPolicy.Resolve(
      messages.Select(message => (message.Role, message.Content))
    );
    var catalogTools = ToolDefinitions.Where(
      tool => scope.Allows(tool.Name)
    ).ToArray();
    var granted = new HashSet<string>(
      grantedTools,
      StringComparer.OrdinalIgnoreCase
    );
    var canonicalTools = new[]
    {
      RequestToolsetDefinition
    }.Concat(
      catalogTools.Where(
        tool => granted.Contains(
          tool.Name
        )
      ).Select(
        tool => !planRequired && tool.Name is not "create_execution_plan" and not "revise_execution_plan"
          ? AddPlanStepBinding(tool)
          : tool
      )
    ).ToArray();
    var availableTools = _toolingProtocol.ToOllamaDefinitions(
      toolingProfile,
      canonicalTools
    );
    var instructionText = PlannerPrompt
      + $"\n{toolingProfile.PromptInstructions}"
      + $"\nHost-owned turn constraints:\n{ExecutionTurnToolPolicy.Describe(scope)}"
      + $"\nNative schemas enabled now: {string.Join(", ", availableTools.Select(tool => tool.Name))}."
      + (
        attemptNumber > 1
          ? $"\nThis is retry attempt {attemptNumber}. The previous response was empty, invalid, "
            + "unavailable, or rejected during action validation. Change strategy: return one valid "
            + "available tool call when another action is necessary, or a final response without a "
            + "tool call when the verified work is complete or no safe action remains."
          : string.Empty
      )
      + (!planRequired
        ? "\nAn accepted Host plan exists. Every executable action must include the exact stepId returned by the Host; the Host will reject missing, unknown, terminal, or dependency-blocked step IDs."
        : string.Empty);
    var toolCatalogText = $"{ToolCatalogMarker}\n{CreateCompactCatalog(catalogTools)}";
    var prompt = $"{instructionText}\n{toolCatalogText}";
    var systemPrompt = string.Join(
      "\n\n",
      new[]
      {
        prompt
      }.Concat(
        messages.Where(
          message => string.Equals(
            message.Role,
            "system",
            StringComparison.Ordinal
          ) && !string.IsNullOrWhiteSpace(
            message.Content
          )
        ).Select(
          message => message.Content!
        )
      )
    );
    var plannerMessages = new[]
    {
      new OllamaToolMessage(
        "system",
        systemPrompt
      )
    }.Concat(
      messages.Where(
        message => !string.Equals(
          message.Role,
          "system",
          StringComparison.Ordinal
        )
      ).Select(
        message => message.Thinking is null
          ? message
          : message with
          {
            Thinking = null
          }
      )
    ).ToArray();
    return new PlanningRequest(
      plannerMessages,
      availableTools,
      instructionText,
      toolCatalogText
    );
  }

  private long EstimateRequest(
    IReadOnlyList<OllamaToolMessage> messages,
    SpecialistToolingProfile toolingProfile,
    IReadOnlyCollection<string> grantedTools,
    bool planRequired,
    int attemptNumber
  )
  {
    var request = CreatePlanningRequest(
      messages,
      toolingProfile,
      grantedTools,
      planRequired,
      attemptNumber
    );
    return _tokenEstimator.EstimateToolMessages(request.Messages)
      + _tokenEstimator.EstimateText(JsonSerializer.Serialize(request.Tools));
  }

  public SpecialistContextMeasurement MeasureRequest(
    IReadOnlyList<OllamaToolMessage> messages,
    SpecialistToolingProfile toolingProfile,
    IReadOnlyCollection<string> grantedTools,
    bool planRequired,
    int attemptNumber
  )
  {
    var request = CreatePlanningRequest(
      messages,
      toolingProfile,
      grantedTools,
      planRequired,
      attemptNumber
    );
    var total = _tokenEstimator.EstimateToolMessages(request.Messages)
      + _tokenEstimator.EstimateText(JsonSerializer.Serialize(request.Tools));
    var sourceMessages = request.Messages.Skip(1).ToArray();
    var project = sourceMessages.Where(
      message => message.Role == "system"
        && message.Content?.StartsWith(
          "APPLICATION_OWNED_PROJECT_CONTEXT",
          StringComparison.Ordinal
        ) == true
    ).Sum(
      message => _tokenEstimator.EstimateText(message.Content)
    );
    var host = sourceMessages.Where(
      message => message.Role == "tool" || IsControlMessage(message.Content)
    ).Sum(
      message => _tokenEstimator.EstimateText(message.Content)
        + _tokenEstimator.EstimateText(message.ToolName)
        + _tokenEstimator.EstimateText(message.ToolCallId)
        + (message.ToolCalls?.Sum(
          call => _tokenEstimator.EstimateText(call.Name)
            + _tokenEstimator.EstimateText(call.Arguments.GetRawText())
        ) ?? 0)
    );
    var conversation = sourceMessages.Where(
      message => message.Role != "system"
        && message.Role != "tool"
        && !IsControlMessage(message.Content)
    ).Sum(
      message => _tokenEstimator.EstimateText(message.Content)
        + (message.ToolCalls?.Sum(
          call => _tokenEstimator.EstimateText(call.Name)
            + _tokenEstimator.EstimateText(call.Arguments.GetRawText())
        ) ?? 0)
    );
    var currentUser = sourceMessages.LastOrDefault(
      message => message.Role == "user" && !IsControlMessage(message.Content)
    );
    var requestTool = request.Tools.First(
      tool => tool.Name == RequestToolsetTool
    );
    var discovery = _tokenEstimator.EstimateText(request.ToolCatalogText)
      + EstimateToolDefinition(requestTool);
    var grantedSchemas = request.Tools.Where(
      tool => tool.Name != RequestToolsetTool
    ).Sum(
      EstimateToolDefinition
    );
    var additionalSystem = sourceMessages.Where(
      message => message.Role == "system"
        && message.Content?.StartsWith(
          "APPLICATION_OWNED_PROJECT_CONTEXT",
          StringComparison.Ordinal
        ) != true
        && !IsControlMessage(message.Content)
    ).Sum(
      message => _tokenEstimator.EstimateText(message.Content)
    );
    var system = _tokenEstimator.EstimateText(request.InstructionText)
      + additionalSystem;
    var categorized = conversation + system + project + discovery + grantedSchemas + host;
    return new SpecialistContextMeasurement(
      conversation,
      system,
      _tokenEstimator.EstimateText(currentUser?.Content),
      project,
      discovery,
      grantedSchemas,
      host,
      Math.Max(0, total - categorized),
      total,
      request.Messages.Count,
      0,
      messages.Count(message => message.Role == "tool") > 1
        || messages.Count(message => message.Role == "user" && !IsControlMessage(message.Content)) > 1,
      "conservative-char-v1"
    );
  }

  private long EstimateToolDefinition(
    OllamaToolDefinition tool
  )
  {
    return _tokenEstimator.EstimateText(tool.Name)
      + _tokenEstimator.EstimateText(tool.Description)
      + _tokenEstimator.EstimateText(tool.Parameters.GetRawText());
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
    IReadOnlyList<OllamaToolDefinition> Tools,
    string InstructionText,
    string ToolCatalogText
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
            CallId: null,
            Usage: response.Usage
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

      var planStepId = call.Arguments.TryGetProperty(
        "stepId",
        out var stepIdElement
      ) && stepIdElement.ValueKind == JsonValueKind.String
        ? stepIdElement.GetString()
        : null;
      var actionArguments = RemoveProperty(
        call.Arguments,
        "stepId"
      );

      return new LocalActionPlanningResult(
        new LocalActionProposal(
          resolution.CanonicalName,
          actionArguments,
          null,
          resolution.OriginalName,
          resolution.Source,
          planStepId
        ),
        assistantMessage,
        false,
        canonicalTurn.IgnoredToolCallCount,
        response.ContextResolution,
        call.CallId,
        response.Usage
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
        "Optionally propose a visible bounded checklist for a multi-step task.",
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
        "create_files",
        "Create between 1 and 50 new UTF-8 text files as one Host-validated batch. Every target must be new; the Host verifies every result and rolls back files created by a failed batch.",
        new
        {
          files = FileCreationArrayProperty()
        },
        ["files"]
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
        "delete_paths",
        "Delete an explicit bounded list of existing files or directories. Recursive directory deletion requires recursive=true. The Host validates, snapshots, executes, and verifies every path.",
        new
        {
          paths = StringArrayProperty(),
          recursive = BooleanProperty()
        },
        ["paths", "recursive"]
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

  private static JsonElement RemoveProperty(
    JsonElement arguments,
    string propertyName
  )
  {
    if (!arguments.TryGetProperty(propertyName, out _))
    {
      return arguments.Clone();
    }
    var value = JsonNode.Parse(
      arguments.GetRawText()
    )!.AsObject();
    value.Remove(
      propertyName
    );
    return JsonSerializer.SerializeToElement(
      value
    );
  }

  private static CanonicalToolDefinition CreateRequestToolsetDefinition()
  {
    return Tool(
      RequestToolsetTool,
      "Request the smallest set of Host tools needed to continue the current objective. This enables schemas but performs no workspace action.",
      new
      {
        tools = new
        {
          type = "array",
          minItems = 1,
          maxItems = 8,
          uniqueItems = true,
          items = StringProperty()
        },
        reason = StringProperty()
      },
      ["tools"]
    );
  }

  private static CanonicalToolDefinition AddPlanStepBinding(
    CanonicalToolDefinition definition
  )
  {
    var parameters = JsonNode.Parse(
      definition.Parameters.GetRawText()
    )!.AsObject();
    parameters["properties"]!.AsObject()["stepId"] = new JsonObject
    {
      ["type"] = "string",
      ["description"] = "Exact Host-owned ID of the accepted plan step this action advances."
    };
    var required = parameters["required"]!.AsArray();
    required.Add(
      "stepId"
    );
    return definition with
    {
      Parameters = JsonSerializer.SerializeToElement(
        parameters
      )
    };
  }

  private static string CreateCompactCatalog(
    IReadOnlyList<CanonicalToolDefinition> tools
  )
  {
    return string.Join(
      "\n",
      tools.Select(
        tool => $"{tool.Name}({string.Join(", ", tool.Parameters.GetProperty("properties").EnumerateObject().Select(property => property.Name))}) - {CompactDescription(tool.Description)}"
      )
    );
  }

  private static string CompactDescription(string description)
  {
    var sentenceEnd = description.IndexOf(
      '.',
      StringComparison.Ordinal
    );
    var compact = sentenceEnd >= 0
      ? description[..sentenceEnd]
      : description;
    return compact.Length <= 140
      ? compact
      : compact[..140];
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

  private static object FileCreationArrayProperty()
  {
    return new
    {
      type = "array",
      minItems = 1,
      maxItems = 50,
      items = new
      {
        type = "object",
        properties = new
        {
          path = StringProperty(),
          content = StringProperty()
        },
        required = new[]
        {
          "path",
          "content"
        },
        additionalProperties = false
      }
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
            dependsOn = new
            {
              type = "array",
              maxItems = 8,
              uniqueItems = true,
              items = new
              {
                type = "integer",
                minimum = 1
              },
              description = "Optional one-based indexes of earlier proposed steps that must complete first."
            }
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
  string? CallId = null,
  ProviderTokenUsage? Usage = null
);
