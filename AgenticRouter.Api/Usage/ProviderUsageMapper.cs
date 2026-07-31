using System.Text.Json;

namespace AgenticRouter.Api.Usage;

public static class ProviderUsageMapper
{
  public static ProviderTokenUsage? FromOpenAiCompatible(
    JsonElement response
  )
  {
    if (
      !response.TryGetProperty(
        "usage",
        out var usage
      )
      || !TryGetInt64(
        usage,
        "prompt_tokens",
        out var input
      )
      || !TryGetInt64(
        usage,
        "completion_tokens",
        out var output
      )
    )
    {
      return null;
    }

    return new ProviderTokenUsage(
      input,
      output,
      ReadNestedInt64(
        usage,
        "prompt_tokens_details",
        "cached_tokens"
      ),
      ReadNestedInt64(
        usage,
        "completion_tokens_details",
        "reasoning_tokens"
      ),
      ReadNestedInt64(
        usage,
        "prompt_tokens_details",
        "audio_tokens"
      )
    );
  }

  public static ProviderTokenUsage? FromGemini(
    JsonElement response
  )
  {
    if (
      !response.TryGetProperty(
        "usageMetadata",
        out var usage
      )
      || !TryGetInt64(
        usage,
        "promptTokenCount",
        out var input
      )
      || !TryGetInt64(
        usage,
        "candidatesTokenCount",
        out var output
      )
    )
    {
      return null;
    }

    return new ProviderTokenUsage(
      input,
      output,
      ReadInt64(
        usage,
        "cachedContentTokenCount"
      ),
      ReadInt64(
        usage,
        "thoughtsTokenCount"
      ),
      SumModalityTokens(
        usage
      )
    );
  }

  private static long? SumModalityTokens(
    JsonElement usage
  )
  {
    if (
      !usage.TryGetProperty(
        "promptTokensDetails",
        out var details
      )
      || details.ValueKind != JsonValueKind.Array
    )
    {
      return null;
    }

    long total = 0;
    var found = false;

    foreach (var detail in details.EnumerateArray())
    {
      var modality = detail.TryGetProperty(
        "modality",
        out var modalityElement
      )
        ? modalityElement.GetString()
        : null;

      if (
        string.Equals(
          modality,
          "TEXT",
          StringComparison.OrdinalIgnoreCase
        )
        || !TryGetInt64(
          detail,
          "tokenCount",
          out var tokenCount
        )
      )
      {
        continue;
      }

      total += tokenCount;
      found = true;
    }

    return found
      ? total
      : null;
  }

  private static long? ReadNestedInt64(
    JsonElement parent,
    string containerName,
    string propertyName
  )
  {
    return parent.TryGetProperty(
        containerName,
        out var container
      )
      ? ReadInt64(
        container,
        propertyName
      )
      : null;
  }

  private static long? ReadInt64(
    JsonElement parent,
    string propertyName
  )
  {
    return TryGetInt64(
      parent,
      propertyName,
      out var value
    )
      ? value
      : null;
  }

  private static bool TryGetInt64(
    JsonElement parent,
    string propertyName,
    out long value
  )
  {
    value = 0;

    return parent.TryGetProperty(
        propertyName,
        out var property
      )
      && property.ValueKind == JsonValueKind.Number
      && property.TryGetInt64(
        out value
      )
      && value >= 0;
  }
}
