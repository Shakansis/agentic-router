using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Execution;

public interface IExpertExecutionGuidanceService
{
  Task<string> PrepareAsync(
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

  private const int MaximumGuidanceLength = 24_000;

  private const string GuidancePrompt =
    GuidanceMarker + "\n"
    + "You are the specialist model in a controlled local execution workflow. "
    + "Analyze the user's current request and produce implementation guidance for a "
    + "resident tooling agent. You do not have tools and must not claim that you changed "
    + "files or ran commands. Specify exact relative paths, complete file contents or "
    + "precise replacements, ordering dependencies, and safe structured commands when "
    + "local actions are required. Resolve ambiguous implementation details using the "
    + "conversation context. Do not address the user and do not merely describe a generic "
    + "plan. If no local action is needed, state NO_LOCAL_ACTION_REQUIRED.";

  private readonly IOllamaClient _ollamaClient;

  public ExpertExecutionGuidanceService(
    IOllamaClient ollamaClient
  )
  {
    _ollamaClient = ollamaClient;
  }

  public async Task<string> PrepareAsync(
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
    var guidance = await _ollamaClient.GenerateTextAsync(
      baseUri,
      model,
      guidanceMessages,
      "expert-execution-guidance",
      cancellationToken
    );

    if (string.IsNullOrWhiteSpace(
      guidance
    ))
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist model returned empty execution guidance."
      );
    }

    if (guidance.Length > MaximumGuidanceLength)
    {
      throw new LocalActionException(
        "expert-execution-guidance",
        "The specialist guidance exceeded the safe 24000-character bridge limit."
      );
    }

    return guidance;
  }
}
