using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Usage;

public interface ITokenEstimator
{
  long EstimateMessages(
    IReadOnlyList<ChatMessage> messages
  );

  long EstimateToolMessages(
    IReadOnlyList<OllamaToolMessage> messages
  );

  long EstimateText(
    string? value
  );

  long EstimateToolResponse(
    OllamaToolResponse response
  );
}

public sealed class ConservativeTokenEstimator : ITokenEstimator
{
  private const int CharactersPerToken = 3;
  private const int MessageOverheadTokens = 6;

  public long EstimateMessages(
    IReadOnlyList<ChatMessage> messages
  )
  {
    return messages.Sum(
      message => MessageOverheadTokens
        + EstimateText(
          message.Role
        )
        + EstimateText(
          message.Content
        )
    );
  }

  public long EstimateToolMessages(
    IReadOnlyList<OllamaToolMessage> messages
  )
  {
    return messages.Sum(
      message => MessageOverheadTokens
        + EstimateText(
          message.Role
        )
        + EstimateText(
          message.Content
        )
        + EstimateText(
          message.Thinking
        )
        + EstimateText(
          message.ToolName
        )
        + EstimateText(
          message.ToolCallId
        )
        + (message.Images?.Sum(
          image => Math.Max(1_024L, (long)Math.Ceiling(image.Bytes.LongLength / 512d))
        ) ?? 0)
        + (
          message.ToolCalls?.Sum(
            call => EstimateText(
              call.Name
            ) + EstimateText(
              call.Arguments.GetRawText()
            )
          ) ?? 0
        )
    );
  }

  public long EstimateText(
    string? value
  )
  {
    if (string.IsNullOrEmpty(
      value
    ))
    {
      return 0;
    }

    return Math.Max(
      1,
      (value.Length + CharactersPerToken - 1) / CharactersPerToken
    );
  }

  public long EstimateToolResponse(
    OllamaToolResponse response
  )
  {
    return EstimateText(
      response.Content
    ) + EstimateText(
      response.Thinking
    ) + response.ToolCalls.Sum(
      call => EstimateText(
        call.Name
      ) + EstimateText(
        call.Arguments.GetRawText()
      )
    );
  }
}
