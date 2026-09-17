using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Chat;

public interface IConversationContextBuilder
{
  ConversationContextResult Build(
    ChatRequest request,
    ApplicationSettings settings,
    string intention,
    string? knowledgeContext,
    int? modelContextTokens
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
  private const string PersistedHistoryCompactionMarker =
    "AGENTIC_ROUTER_PERSISTED_HISTORY_COMPACTION_V1";
  private readonly ITokenEstimator _tokenEstimator;

  public ConversationContextBuilder(ITokenEstimator tokenEstimator)
  {
    _tokenEstimator = tokenEstimator;
  }

  public ConversationContextResult Build(
    ChatRequest request,
    ApplicationSettings settings,
    string intention,
    string? knowledgeContext,
    int? modelContextTokens
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
    var effectiveContextTokens = Math.Min(
      settings.Context.DefaultContextTokens,
      modelContextTokens is > 0
        ? modelContextTokens.Value
        : int.MaxValue
    );
    var historyBudget = Math.Max(
      0,
      effectiveContextTokens
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
      if (IsPersistedHistoryCompaction(user))
      {
        turns.Add([user]);
        continue;
      }
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

    if (
      history.Count > 0
      && IsPersistedHistoryCompaction(history[^1])
    )
    {
      turns.Add([history[^1]]);
    }

    return turns;
  }

  private static bool IsPersistedHistoryCompaction(ChatMessage message)
  {
    return message.Role == "assistant"
      && message.Content.StartsWith(
        PersistedHistoryCompactionMarker,
        StringComparison.Ordinal
      );
  }

  private int EstimateTokens(
    ChatMessage message
  )
  {
    return checked((int)Math.Min(
      int.MaxValue,
      _tokenEstimator.EstimateMessages([message])
    ));
  }
}
