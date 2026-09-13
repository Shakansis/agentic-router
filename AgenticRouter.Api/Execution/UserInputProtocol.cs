using System.Text.Json;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public sealed record UserInputBatch(
  IReadOnlyList<UserInputQuestionView> Questions
);

public static class UserInputProtocol
{
  public const string ToolName = "request_user_input";
  public const int MaximumQuestions = 4;
  public const int MaximumOptions = 4;

  public static CanonicalToolDefinition ToolDefinition { get; } = new(
    ToolName,
    "Ask the user one atomic batch of 1 to 4 necessary questions, then wait for all answers before continuing. Each question may offer up to 4 predefined options and always accepts a custom answer.",
    JsonSerializer.SerializeToElement(new
    {
      type = "object",
      properties = new
      {
        questions = new
        {
          type = "array",
          minItems = 1,
          maxItems = MaximumQuestions,
          items = new
          {
            type = "object",
            properties = new
            {
              id = new { type = "string", minLength = 1, maxLength = 64 },
              header = new { type = "string", minLength = 1, maxLength = 80 },
              question = new { type = "string", minLength = 1, maxLength = 2000 },
              options = new
              {
                type = "array",
                maxItems = MaximumOptions,
                items = new
                {
                  type = "object",
                  properties = new
                  {
                    label = new { type = "string", minLength = 1, maxLength = 160 },
                    description = new { type = "string", maxLength = 500 }
                  },
                  required = new[] { "label" },
                  additionalProperties = false
                }
              }
            },
            required = new[] { "id", "header", "question", "options" },
            additionalProperties = false
          }
        }
      },
      required = new[] { "questions" },
      additionalProperties = false
    })
  );

  public static UserInputBatch Parse(JsonElement arguments)
  {
    if (
      arguments.ValueKind != JsonValueKind.Object
      || !arguments.TryGetProperty("questions", out var questionsElement)
      || questionsElement.ValueKind != JsonValueKind.Array
    )
    {
      throw new LocalActionException(
        "user-input-schema",
        "request_user_input requires a questions array."
      );
    }

    var elements = questionsElement.EnumerateArray().ToArray();
    if (elements.Length is < 1 or > MaximumQuestions)
    {
      throw new LocalActionException(
        "user-input-schema",
        $"request_user_input accepts between 1 and {MaximumQuestions} questions."
      );
    }

    var ids = new HashSet<string>(StringComparer.Ordinal);
    var questions = new List<UserInputQuestionView>(elements.Length);
    foreach (var element in elements)
    {
      var id = RequiredString(element, "id", 64);
      if (!ids.Add(id))
      {
        throw new LocalActionException(
          "user-input-schema",
          $"Question id '{id}' is duplicated."
        );
      }
      var header = RequiredString(element, "header", 80);
      var question = RequiredString(element, "question", 2000);
      if (
        !element.TryGetProperty("options", out var optionsElement)
        || optionsElement.ValueKind != JsonValueKind.Array
      )
      {
        throw new LocalActionException(
          "user-input-schema",
          $"Question '{id}' requires an options array."
        );
      }
      var optionElements = optionsElement.EnumerateArray().ToArray();
      if (optionElements.Length > MaximumOptions)
      {
        throw new LocalActionException(
          "user-input-schema",
          $"Question '{id}' exceeds the limit of {MaximumOptions} predefined options."
        );
      }
      var options = optionElements.Select(option => new UserInputOptionView(
        RequiredString(option, "label", 160),
        OptionalString(option, "description", 500)
      )).ToArray();
      questions.Add(new UserInputQuestionView(id, header, question, options));
    }
    return new UserInputBatch(questions);
  }

  public static string SerializeAnswers(
    IReadOnlyList<UserInputAnswerView> answers
  )
  {
    return JsonSerializer.Serialize(new
    {
      answers = answers.Select(answer => new
      {
        questionId = answer.QuestionId,
        answer = answer.Answer
      })
    });
  }

  private static string RequiredString(JsonElement element, string name, int maximumLength)
  {
    var value = OptionalString(element, name, maximumLength);
    if (string.IsNullOrWhiteSpace(value))
    {
      throw new LocalActionException(
        "user-input-schema",
        $"User-input field '{name}' is required."
      );
    }
    return value;
  }

  private static string? OptionalString(JsonElement element, string name, int maximumLength)
  {
    if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
    {
      return null;
    }
    if (property.ValueKind != JsonValueKind.String)
    {
      throw new LocalActionException(
        "user-input-schema",
        $"User-input field '{name}' must be a string."
      );
    }
    var value = property.GetString()?.Trim() ?? string.Empty;
    if (value.Length > maximumLength)
    {
      throw new LocalActionException(
        "user-input-schema",
        $"User-input field '{name}' exceeds {maximumLength} characters."
      );
    }
    return value;
  }
}
