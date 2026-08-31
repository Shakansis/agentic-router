using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Chat;

public interface IConversationContextBuilder
{
  ConversationContextResult Build(
    ChatRequest request,
    ApplicationSettings settings,
    string intention,
    string? knowledgeContext
  );
}

public sealed record ConversationContextResult(
  IReadOnlyList<ChatMessage> Messages,
  int OmittedMessages,
  int EstimatedInputTokens,
  string Diagnostic,
  int VisibleMessages,
  int IncludedMessages,
  int SystemInstructionTokens,
  int CurrentUserMessageTokens
);

public sealed class ConversationContextBuilder : IConversationContextBuilder
{
  public ConversationContextResult Build(
    ChatRequest request,
    ApplicationSettings settings,
    string intention,
    string? knowledgeContext
  )
  {
    var systemMessages = new List<ChatMessage>
    {
      new ChatMessage(
        "system",
        SettingsDefaults.GlobalTargetInstruction
      ),
      new ChatMessage(
        "system",
        settings.Intentions[intention].SystemPrompt
      )
    };
    if (!string.IsNullOrWhiteSpace(knowledgeContext))
    {
      systemMessages.Add(
        new ChatMessage(
          "system",
          knowledgeContext
        )
      );
    }
    var current = new ChatMessage(
      "user",
      request.Message
    );
    var systemInstructionTokens = systemMessages.Sum(
      EstimateTokens
    );
    var currentUserMessageTokens = EstimateTokens(
      current
    );
    var fixedTokens = systemInstructionTokens + currentUserMessageTokens;
    var historyBudget = Math.Max(
      0,
      settings.Context.DefaultContextTokens
        - settings.Context.ReservedResponseTokens
        - fixedTokens
    );
    var completeTurns = GetCompleteTurns(
      request.History
    );
    var selected = new List<IReadOnlyList<ChatMessage>>();
    var selectedMessages = 0;
    var selectedTokens = 0;

    for (var index = completeTurns.Count - 1; index >= 0; index--)
    {
      var turn = completeTurns[index];
      var turnTokens = turn.Sum(
        EstimateTokens
      );

      if (
        selectedMessages + turn.Count > settings.Context.MaxConversationMessages
        || selectedTokens + turnTokens > historyBudget
      )
      {
        break;
      }

      selected.Add(
        turn
      );
      selectedMessages += turn.Count;
      selectedTokens += turnTokens;
    }

    selected.Reverse();
    var history = selected
      .SelectMany(
        turn => turn
      )
      .ToArray();
    var totalUsefulHistory = completeTurns.Sum(
      turn => turn.Count
    );
    var messages = systemMessages
      .Concat(
        history
      )
      .Append(
        current
      )
      .ToArray();

    return new ConversationContextResult(
      messages,
      totalUsefulHistory - history.Length,
      fixedTokens + selectedTokens,
      "Context size and token count are conservative estimates because Ollama did not "
        + "report a reliable model context limit.",
      (
        request.History?.Count
          ?? 0
      ) + 1,
      history.Length + 1,
      systemInstructionTokens,
      currentUserMessageTokens
    );
  }

  private static IReadOnlyList<IReadOnlyList<ChatMessage>> GetCompleteTurns(
    IReadOnlyList<ChatMessage>? history
  )
  {
    var turns = new List<IReadOnlyList<ChatMessage>>();

    if (history is null)
    {
      return turns;
    }

    for (var index = 0; index + 1 < history.Count; index++)
    {
      var user = history[index];
      var assistant = history[index + 1];

      if (
        user.Role == "user"
        && assistant.Role == "assistant"
        && !string.IsNullOrWhiteSpace(
          user.Content
        )
        && !string.IsNullOrWhiteSpace(
          assistant.Content
        )
      )
      {
        turns.Add(
          [
            user,
            assistant
          ]
        );
        index++;
      }
    }

    return turns;
  }

  private static int EstimateTokens(
    ChatMessage message
  )
  {
    return Math.Max(
      1,
      (int)Math.Ceiling(
        message.Content.Length / 4d
      ) + 4
    );
  }
}
