using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Routing;

public interface IIntentionRouter
{
  IntentionRoutingResult Route(
    ChatRequest request
  );
}

public sealed record IntentionRoutingResult(
  RouterDecision Decision,
  string RuleId
);

public sealed class IntentionRouter : IIntentionRouter
{
  public const string RouterVersion = "keyword-intention-router-v1";

  private static readonly IReadOnlyList<KeywordRule> Rules =
  [
    Rule(
      "review-and-testing",
      "review-and-testing",
      "review|test|validate|verify|bug|fix|audit|inspect|revis|test|valid|verific|corrij|erro|falha|auditor|inspec"
    ),
    Rule(
      "software-development",
      "software-development",
      "implement|program|refactor|compile|build|create file|edit file|write file|write (?:some )?code|implem|program|refator|compil|crie (?:um )?arquivo|editar? (?:um )?arquivo|escrev(?:a|er) (?:um )?arquivo|escrev(?:a|er) (?:algum )?codigo"
    ),
    Rule(
      "software-architecture",
      "software-architecture",
      "architect|architecture|service boundar|component design|system design|design pattern|arquitet|limite de servico|fronteira de servico|design de componente|desenho do sistema|padrao de projeto"
    ),
    Rule(
      "documentation",
      "documentation",
      "document|documentation|readme|specification|technical writing|document|documentacao|especificacao|redacao tecnica"
    ),
    Rule(
      "rpg-storytelling",
      "rpg-storytelling",
      "rpg|role.?playing|story|storytelling|character|campaign|narrative|historia|narrativa|personagem|campanha|mestre"
    )
  ];

  public IntentionRoutingResult Route(
    ChatRequest request
  )
  {
    var text = Normalize(
      RoutingText(request)
    );
    foreach (var rule in Rules)
    {
      if (rule.Pattern.IsMatch(text))
      {
        return new IntentionRoutingResult(
          new RouterDecision(
            rule.Intention,
            1,
            $"Matched deterministic keyword rule '{rule.Id}'."
          ),
          rule.Id
        );
      }
    }

    return new IntentionRoutingResult(
      new RouterDecision(
        "general-chat",
        1,
        "No deterministic keyword rule matched; using general-chat."
      ),
      "general-chat-fallback"
    );
  }

  private static KeywordRule Rule(
    string id,
    string intention,
    string alternatives
  )
  {
    return new KeywordRule(
      id,
      intention,
      new Regex(
        $"(?<![a-z0-9])(?:{alternatives})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50)
      )
    );
  }

  private static string RoutingText(
    ChatRequest request
  )
  {
    if (!IsDependentFollowUp(request.Message))
    {
      return request.Message;
    }

    var previousUser = request.History?.LastOrDefault(message =>
      string.Equals(message.Role, "user", StringComparison.Ordinal)
    )?.Content;
    return string.IsNullOrWhiteSpace(previousUser)
      ? request.Message
      : previousUser + "\n" + request.Message;
  }

  private static bool IsDependentFollowUp(
    string message
  )
  {
    if (message.Length > 180)
    {
      return false;
    }

    var normalized = Normalize(message).Trim();
    return new[]
    {
      "continue",
      "continua",
      "make it shorter",
      "shorten it",
      "faca menor",
      "deixe mais curto",
      "melhore isso",
      "revise isso"
    }.Any(marker => normalized.StartsWith(marker, StringComparison.Ordinal));
  }

  private static string Normalize(
    string value
  )
  {
    var decomposed = value.Normalize(NormalizationForm.FormD);
    var builder = new StringBuilder(decomposed.Length);
    foreach (var character in decomposed)
    {
      if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
      {
        builder.Append(char.ToLowerInvariant(character));
      }
    }
    return builder.ToString().Normalize(NormalizationForm.FormC);
  }

  private sealed record KeywordRule(
    string Id,
    string Intention,
    Regex Pattern
  );
}
