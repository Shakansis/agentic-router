using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Execution;

public interface IExpertExecutionGuidanceService
{
  Task<ExpertExecutionGuidance> PrepareAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    CancellationToken cancellationToken
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

  private const int MaximumGuidanceActions = 8;
  private const int MaximumGuidanceLength = 48_000;

  private const string GuidancePrompt =
    GuidanceMarker + "\n"
    + "You are the specialist model in a controlled local execution workflow. "
    + "Analyze the current request and return only the required structured execution brief. "
    + "You do not have tools and must not claim that you changed files or ran commands. "
    + "When local work is required, set actionRequired to true and provide ordered actions "
    + "with an exact supported tool name and a complete JSON arguments object. Use only "
    + "paths relative to the trusted workspace. Preserve complete file contents, exact "
    + "replacement text, process arguments, and ordering dependencies. A statement such as "
    + "'I cannot access the disk' is not a valid substitute for execution guidance. "
    + "When no local action is required, set actionRequired to false and return no actions. "
    + "Do not address the user and do not return Markdown or a generic prose plan.";

  private static readonly HashSet<string> SupportedTools = new(
    [
      "list_files",
      "read_file",
      "get_file_info",
      "search_text",
      "create_file",
      "write_file",
      "replace_text",
      "apply_patch",
      "create_directory",
      "run_process",
      "run_validation_profile"
    ],
    StringComparer.Ordinal
  );

  private static readonly JsonElement GuidanceSchema = CreateGuidanceSchema();

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  private readonly IOllamaClient _ollamaClient;

  public ExpertExecutionGuidanceService(
    IOllamaClient ollamaClient
  )
  {
    _ollamaClient = ollamaClient;
  }

  public async Task<ExpertExecutionGuidance> PrepareAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    CancellationToken cancellationToken
  )
  {
    var guidanceMessages = new[]
    {
      new ChatMessage(
        "system",
        GuidancePrompt
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
      cancellationToken
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

    Validate(
      guidance
    );
    return guidance!;
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

  private static void Validate(
    ExpertExecutionGuidance? guidance
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

    var identifiers = new HashSet<string>(
      StringComparer.Ordinal
    );

    foreach (var action in guidance.Actions)
    {
      if (
        string.IsNullOrWhiteSpace(
          action.Id
        )
        || string.IsNullOrWhiteSpace(
          action.Title
        )
        || !identifiers.Add(
          action.Id
        )
      )
      {
        throw new LocalActionException(
          "expert-execution-guidance",
          "Every specialist action requires a unique non-empty id and title."
        );
      }

      if (!SupportedTools.Contains(
        action.Tool
      ))
      {
        throw new LocalActionException(
          "expert-execution-guidance",
          $"The specialist requested unsupported tool '{action.Tool}'."
        );
      }

      if (action.Arguments.ValueKind != JsonValueKind.Object)
      {
        throw new LocalActionException(
          "expert-execution-guidance",
          $"Arguments for specialist action '{action.Id}' must be a JSON object."
        );
      }
    }
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
                id = new
                {
                  type = "string"
                },
                title = new
                {
                  type = "string"
                },
                tool = new
                {
                  type = "string",
                  @enum = SupportedTools.ToArray()
                },
                arguments = new
                {
                  type = "object"
                }
              },
              required = new[]
              {
                "id",
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
  string Id,
  string Title,
  string Tool,
  JsonElement Arguments
);
