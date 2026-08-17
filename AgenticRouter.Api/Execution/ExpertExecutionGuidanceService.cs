using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Execution;

public sealed record StructuredContextFit(
  IReadOnlyList<ChatMessage> Messages,
  long BeforeTokens,
  long AfterTokens,
  bool Compacted,
  bool TooLarge,
  string Outcome
);

public interface IExpertExecutionGuidanceService
{
  StructuredContextFit FitToBudget(
    IReadOnlyList<ChatMessage> messages,
    string hostStateSummary,
    long maximumInputTokens,
    bool forceCompaction = false
  );

  SpecialistContextMeasurement MeasureRequest(
    IReadOnlyList<ChatMessage> messages
  );

  Task<ExpertExecutionGuidance> PrepareAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Action<ProviderTokenUsage?>? usageObserver = null
  );
}

public sealed class ExpertExecutionGuidanceService : IExpertExecutionGuidanceService
{
  public const string GuidanceMarker = "EXPERT_EXECUTION_GUIDANCE_V1";

  public const string FinalResponseInstruction =
    "The local action results in this context are authoritative. "
    + "Answer with a concise summary of what was actually completed, any validation "
    + "or process result, and any remaining limitation. Do not return a future execution "
    + "plan and do not claim an action that lacks a completed tool result.";

  private const int MaximumGuidanceActions = 1;
  private const int MaximumGuidanceLength = 48_000;

  private const string GuidancePrompt =
    GuidanceMarker + "\n"
    + "You are the specialist model in a controlled local execution workflow. "
    + "Analyze the current request and return only the required structured execution brief. "
    + "You do not have tools and must not claim that you changed files or ran commands. "
    + "Treat minor spelling and grammar mistakes as recoverable input. Resolve an ambiguous phrase "
    + "from the surrounding requested assets and constraints, state the chosen conventional interpretation "
    + "in the objective, and do not invent unrelated behavior. "
    + "When local work is required, set actionRequired to true and provide exactly one action "
    + "with a short title, an exact supported tool name, and a complete JSON arguments object. "
    + "The Host owns plan and step IDs; do not generate identifiers. Use only "
    + "paths relative to the trusted workspace, which is already the project root; never "
    + "prefix a path with the workspace display name or root directory name. For requests "
    + "to edit, fix, update, or inspect existing files, start with list_files, read_file, "
    + "get_file_info, or search_text and let the tooling agent choose the mutation after "
    + "observing the result. When integrating an existing folder, list it and inspect the "
    + "relevant implementation before using its observed paths or public API; preserve the "
    + "supplied assets. Never use create_file as a substitute for editing an existing file. "
    + "Preserve complete file contents, explicit constraints, exact replacement text, "
    + "process arguments, and ordering dependencies. A statement such as "
    + "'I cannot access the disk' is not a valid substitute for execution guidance. "
    + "When no local action is required, set actionRequired to false and return no actions. "
    + "Do not address the user and do not return Markdown or a generic prose plan.";

  private static readonly JsonElement GuidanceSchema = CreateGuidanceSchema();

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  private readonly IOllamaClient _ollamaClient;
  private readonly IToolNameResolver _toolNames;
  private readonly ITokenEstimator _tokenEstimator;

  public ExpertExecutionGuidanceService(
    IOllamaClient ollamaClient,
    IToolNameResolver toolNames,
    ITokenEstimator tokenEstimator
  )
  {
    _ollamaClient = ollamaClient;
    _toolNames = toolNames;
    _tokenEstimator = tokenEstimator;
  }

  public StructuredContextFit FitToBudget(
    IReadOnlyList<ChatMessage> messages,
    string hostStateSummary,
    long maximumInputTokens,
    bool forceCompaction = false
  )
  {
    var scope = ExecutionTurnToolPolicy.Resolve(
      messages.Select(message => (message.Role, (string?)message.Content))
    );
    var before = MeasureRequest(
      messages,
      scope
    ).InputTokens;
    if (before <= maximumInputTokens && !forceCompaction)
    {
      return new StructuredContextFit(messages.ToArray(), before, before, false, false, "already-fits");
    }

    var compact = new List<ChatMessage>();
    AddLast(compact, messages, item => item.Role == "system" && item.Content.StartsWith("APPLICATION_OWNED_PROJECT_CONTEXT", StringComparison.Ordinal));
    AddLast(compact, messages, item => item.Role == "user" && !IsControlMessage(item.Content) && !item.Content.StartsWith(GuidanceMarker, StringComparison.Ordinal));
    AddLast(compact, messages, item => item.Content.StartsWith(GuidanceMarker, StringComparison.Ordinal));
    compact.Add(new ChatMessage("system", $"APPLICATION_OWNED_EXECUTION_STATE_V1\n{hostStateSummary}"));
    AddLast(compact, messages, item => IsControlMessage(item.Content));
    compact = compact.DistinctBy(item => $"{item.Role}:{item.Content}", StringComparer.Ordinal).ToList();
    var after = MeasureRequest(
      compact,
      scope
    ).InputTokens;
    var tooLarge = after > maximumInputTokens;
    var materiallySmaller = after <= before - Math.Max(256, before / 10);
    return new StructuredContextFit(
      compact,
      before,
      after,
      true,
      tooLarge,
      tooLarge ? "required-context-item-too-large" : materiallySmaller ? "compacted" : "not-materially-smaller"
    );
  }

  public SpecialistContextMeasurement MeasureRequest(
    IReadOnlyList<ChatMessage> messages
  )
  {
    var scope = ExecutionTurnToolPolicy.Resolve(
      messages.Select(message => (message.Role, (string?)message.Content))
    );
    return MeasureRequest(
      messages,
      scope
    );
  }

  private SpecialistContextMeasurement MeasureRequest(
    IReadOnlyList<ChatMessage> messages,
    ExecutionTurnToolScope scope
  )
  {
    var supportedTools = $"Supported tools for this turn: {string.Join(", ", scope.AvailableTools)}.";
    var policy = $"Host-owned turn constraints:\n{ExecutionTurnToolPolicy.Describe(scope)}";
    var request = new[]
    {
      new ChatMessage(
        "system",
        CreateGuidancePrompt(scope)
      )
    }.Concat(messages).ToArray();
    var total = _tokenEstimator.EstimateMessages(request)
      + _tokenEstimator.EstimateText(GuidanceSchema.GetRawText());
    var project = messages.Where(
      message => message.Role == "system"
        && message.Content.StartsWith(
          "APPLICATION_OWNED_PROJECT_CONTEXT",
          StringComparison.Ordinal
        )
    ).Sum(
      message => _tokenEstimator.EstimateText(message.Content)
    );
    var host = messages.Where(
      message => IsControlMessage(message.Content)
    ).Sum(
      message => _tokenEstimator.EstimateText(message.Content)
    );
    var conversation = messages.Where(
      message => message.Role != "system" && !IsControlMessage(message.Content)
    ).Sum(
      message => _tokenEstimator.EstimateText(message.Content)
    );
    var currentUser = messages.LastOrDefault(
      message => message.Role == "user" && !IsControlMessage(message.Content)
    );
    var discovery = _tokenEstimator.EstimateText(supportedTools);
    var system = _tokenEstimator.EstimateText(GuidancePrompt)
      + _tokenEstimator.EstimateText(policy)
      + _tokenEstimator.EstimateText(GuidanceSchema.GetRawText())
      + messages.Where(
        message => message.Role == "system"
          && !message.Content.StartsWith(
            "APPLICATION_OWNED_PROJECT_CONTEXT",
            StringComparison.Ordinal
          )
      ).Sum(
        message => _tokenEstimator.EstimateText(message.Content)
      );
    var categorized = conversation + system + project + discovery + host;
    return new SpecialistContextMeasurement(
      conversation,
      system,
      _tokenEstimator.EstimateText(currentUser?.Content),
      project,
      discovery,
      0,
      host,
      Math.Max(0, total - categorized),
      total,
      request.Length,
      0,
      messages.Count > 4,
      "conservative-char-v1"
    );
  }

  private static void AddLast(List<ChatMessage> target, IReadOnlyList<ChatMessage> messages, Func<ChatMessage, bool> predicate)
  {
    var item = messages.LastOrDefault(predicate);
    if (item is not null) target.Add(item);
  }

  private static bool IsControlMessage(string content)
  {
    return content.StartsWith("LOCAL_ACTION_", StringComparison.Ordinal)
      || content.StartsWith("STRUCTURED_ACTION_", StringComparison.Ordinal)
      || content.StartsWith("TOOL_PROTOCOL_", StringComparison.Ordinal)
      || content.StartsWith("EXECUTION_", StringComparison.Ordinal)
      || content.StartsWith("COMPLETION_", StringComparison.Ordinal)
      || content.StartsWith("HOST_", StringComparison.Ordinal)
      || content.StartsWith("RECOVERY_", StringComparison.Ordinal)
      || content.StartsWith("RESIDENT_", StringComparison.Ordinal);
  }

  public async Task<ExpertExecutionGuidance> PrepareAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Action<ProviderTokenUsage?>? usageObserver = null
  )
  {
    var scope = ExecutionTurnToolPolicy.Resolve(
      messages.Select(message => (message.Role, (string?)message.Content))
    );
    var guidanceMessages = new[]
    {
      new ChatMessage(
        "system",
        CreateGuidancePrompt(scope)
      )
    }.Concat(
      messages
    ).ToArray();
    var guidanceJson = await _ollamaClient.GenerateStructuredAsync(
      baseUri,
      model,
      guidanceMessages,
      GuidanceSchema,
      "expert-execution-guidance",
      usageContext,
      cancellationToken,
      usageObserver
    );

    if (string.IsNullOrWhiteSpace(
      guidanceJson
    ))
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist model returned empty execution guidance."
      );
    }

    if (guidanceJson.Length > MaximumGuidanceLength)
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist guidance exceeded the safe 48000-character bridge limit."
      );
    }

    ExpertExecutionGuidance? guidance;

    try
    {
      guidance = JsonSerializer.Deserialize<ExpertExecutionGuidance>(
        guidanceJson,
        JsonOptions
      );
    }
    catch (JsonException exception)
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist model returned invalid structured execution guidance.",
        exception
      );
    }

    return Validate(
      guidance,
      scope.AvailableTools
    );
  }

  private static string CreateGuidancePrompt(ExecutionTurnToolScope scope)
  {
    return GuidancePrompt
      + $"\nHost-owned turn constraints:\n{ExecutionTurnToolPolicy.Describe(scope)}"
      + $"\nSupported tools for this turn: {string.Join(", ", scope.AvailableTools)}.";
  }

  public static string Serialize(
    ExpertExecutionGuidance guidance
  )
  {
    return JsonSerializer.Serialize(
      guidance,
      JsonOptions
    );
  }

  private ExpertExecutionGuidance Validate(
    ExpertExecutionGuidance? guidance,
    IReadOnlyCollection<string> availableTools
  )
  {
    if (
      guidance is null
      || string.IsNullOrWhiteSpace(
        guidance.Objective
      )
    )
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist guidance must include an objective."
      );
    }

    if (guidance.Actions.Count > MaximumGuidanceActions)
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        $"The specialist guidance exceeded the limit of {MaximumGuidanceActions} actions."
      );
    }

    if (
      guidance.ActionRequired
      && guidance.Actions.Count == 0
    )
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist marked local action as required but returned no actions."
      );
    }

    if (
      !guidance.ActionRequired
      && guidance.Actions.Count > 0
    )
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist returned actions while marking local action as unnecessary."
      );
    }

    var normalizedActions = new List<ExpertExecutionAction>();

    foreach (var action in guidance.Actions)
    {
      if (
        string.IsNullOrWhiteSpace(
          action.Title
        )
      )
      {
        throw new LocalActionException(
          "expert-execution-guidance",
          "Every specialist action requires a non-empty normalizable title."
        );
      }

      var resolution = _toolNames.Resolve(
        action.Tool,
        availableTools
      );

      if (action.Arguments.ValueKind != JsonValueKind.Object)
      {
        throw new LocalActionException(
          "expert-execution-guidance",
          $"Arguments for specialist action '{action.Title}' must be a JSON object."
        );
      }

      normalizedActions.Add(
        action with
        {
          Tool = resolution.CanonicalName,
          OriginalTool = resolution.OriginalName,
          ToolResolutionSource = resolution.Source
        }
      );
    }

    return guidance with
    {
      Actions = normalizedActions
    };
  }

  private static JsonElement CreateGuidanceSchema()
  {
    return JsonSerializer.SerializeToElement(
      new
      {
        type = "object",
        properties = new
        {
          actionRequired = new
          {
            type = "boolean"
          },
          objective = new
          {
            type = "string"
          },
          actions = new
          {
            type = "array",
            maxItems = MaximumGuidanceActions,
            items = new
            {
              type = "object",
              properties = new
              {
                title = new
                {
                  type = "string"
                },
                tool = new
                {
                  type = "string"
                },
                arguments = new
                {
                  type = "object"
                }
              },
              required = new[]
              {
                "title",
                "tool",
                "arguments"
              }
            }
          },
          completionCriteria = new
          {
            type = "array",
            items = new
            {
              type = "string"
            }
          }
        },
        required = new[]
        {
          "actionRequired",
          "objective",
          "actions",
          "completionCriteria"
        }
      }
    );
  }
}

public sealed record ExpertExecutionGuidance(
  bool ActionRequired,
  string Objective,
  IReadOnlyList<ExpertExecutionAction> Actions,
  IReadOnlyList<string> CompletionCriteria
);

public sealed record ExpertExecutionAction(
  string Title,
  string Tool,
  JsonElement Arguments,
  string? OriginalTool = null,
  string ToolResolutionSource = ToolNameResolver.CanonicalSource
);
