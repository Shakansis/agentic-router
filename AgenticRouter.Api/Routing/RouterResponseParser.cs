using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Routing;

public interface IRouterResponseParser
{
  RouterParseResult Parse(
    string value
  );
}

public sealed record RouterParseResult(
  RouterDecision? Decision,
  string? FailureType,
  bool RawOutputCaptured
);

public sealed class RouterResponseParser : IRouterResponseParser
{
  public RouterParseResult Parse(
    string value
  )
  {
    JsonDocument document;

    try
    {
      document = JsonDocument.Parse(
        value
      );
    }
    catch (JsonException)
    {
      return Failure(
        "invalid-json"
      );
    }

    using (document)
    {
      var root = document.RootElement;

      if (
        root.ValueKind != JsonValueKind.Object
        || !root.TryGetProperty(
          "intention",
          out var intentionElement
        )
        || intentionElement.ValueKind != JsonValueKind.String
      )
      {
        return Failure(
          "missing-intention"
        );
      }

      var intention = intentionElement.GetString();

      if (
        string.IsNullOrWhiteSpace(
          intention
        )
        || !SettingsDefaults.IntentionNames.Contains(
          intention,
          StringComparer.Ordinal
        )
      )
      {
        return Failure(
          "unsupported-intention"
        );
      }

      double? confidence = null;

      if (root.TryGetProperty(
        "confidence",
        out var confidenceElement
      ))
      {
        if (confidenceElement.ValueKind == JsonValueKind.Null)
        {
          confidence = null;
        }
        else if (
          confidenceElement.ValueKind != JsonValueKind.Number
          || !confidenceElement.TryGetDouble(
            out var parsedConfidence
          )
          || !double.IsFinite(
            parsedConfidence
          )
          || parsedConfidence is < 0 or > 1
        )
        {
          return Failure(
            "invalid-confidence"
          );
        }
        else
        {
          confidence = parsedConfidence;
        }
      }

      string? reason = null;

      if (root.TryGetProperty(
        "reason",
        out var reasonElement
      ))
      {
        if (reasonElement.ValueKind == JsonValueKind.String)
        {
          reason = reasonElement.GetString()?.Trim();
        }
        else if (reasonElement.ValueKind != JsonValueKind.Null)
        {
          return Failure(
            "invalid-reason"
          );
        }

        if (reason?.Length > 240)
        {
          return Failure(
            "reason-too-long"
          );
        }
      }

      return new RouterParseResult(
        new RouterDecision(
          intention,
          confidence,
          string.IsNullOrWhiteSpace(
            reason
          )
            ? null
            : reason
        ),
        null,
        true
      );
    }
  }

  private static RouterParseResult Failure(
    string type
  )
  {
    return new RouterParseResult(
      null,
      type,
      true
    );
  }
}
