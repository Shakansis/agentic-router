using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Providers.Cloud;

public sealed class GeminiCloudProvider : ICloudProviderAdapter
{
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  );

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly Uri _baseUri;

  public GeminiCloudProvider(
    IHttpClientFactory httpClientFactory,
    Uri baseUri
  )
  {
    _httpClientFactory = httpClientFactory;
    _baseUri = baseUri;
  }

  public string ProviderId => ModelProviderIds.GoogleAiStudio;

  public string DisplayName => ModelProviderIds.DisplayName(
    ProviderId
  );

  public string ProtocolVersion => "gemini-developer-v1beta-2026-07";

  public async Task<IReadOnlyList<InstalledModel>> ListModelsAsync(
    string apiKey,
    CancellationToken cancellationToken
  )
  {
    using var request = CreateRequest(
      HttpMethod.Get,
      "models?pageSize=1000",
      apiKey
    );
    using var response = await SendAsync(
      request,
      "cloud-model-list",
      null,
      cancellationToken
    );
    using var document = await ReadDocumentAsync(
      response,
      "cloud-model-list",
      null,
      cancellationToken
    );

    if (
      !document.RootElement.TryGetProperty(
        "models",
        out var models
      )
      || models.ValueKind != JsonValueKind.Array
    )
    {
      throw Error(
        "provider-contract-invalid",
        "cloud-model-list",
        null,
        "Google AI Studio returned an invalid model list.",
        response.StatusCode,
        false,
        response
      );
    }

    var result = new List<InstalledModel>();

    foreach (var model in models.EnumerateArray())
    {
      var rawName = ReadString(
        model,
        "name"
      );

      if (string.IsNullOrWhiteSpace(
        rawName
      ))
      {
        continue;
      }

      var modelId = rawName.StartsWith(
        "models/",
        StringComparison.Ordinal
      )
        ? rawName["models/".Length..]
        : rawName;
      var methods = ReadStringArray(
        model,
        "supportedGenerationMethods"
      );
      var reference = new ProviderModelReference(
        ProviderId,
        modelId
      );
      var supportsChat = methods.Contains(
        "generateContent",
        StringComparer.Ordinal
      );
      var inputModalities = ReadStringArray(
        model,
        "inputModalities"
      );
      var supportedTools = ReadStringArray(
        model,
        "supportedTools"
      );
      var vision = inputModalities.Contains(
        "IMAGE",
        StringComparer.OrdinalIgnoreCase
      );
      var webSearch = supportedTools.Contains(
        "google_search",
        StringComparer.OrdinalIgnoreCase
      ) || SupportsDocumentedGoogleSearch(
        modelId
      );

      result.Add(
        new InstalledModel(
          reference.Qualified,
          null,
          null,
          ReadString(
            model,
            "version"
          ),
          ProviderId,
          reference.Display,
          new ProviderModelCapabilities(
            supportsChat,
            methods.Contains(
              "streamGenerateContent",
              StringComparer.Ordinal
            ) || supportsChat,
            supportsChat,
            vision,
            webSearch,
            ReadInt32(
              model,
              "inputTokenLimit"
            ),
            "provider-model-metadata",
            inputModalities.Count > 0 || supportedTools.Count > 0,
            StructuredOutput: supportsChat,
            ProviderNativeWebSearch: webSearch,
            Citations: webSearch,
            MaximumImageCount: vision
              ? CapabilityLimits.MaximumImageCount
              : 0,
            MaximumImageBytes: vision
              ? CapabilityLimits.MaximumImageBytes
              : 0,
            SupportedImageMimeTypes: vision
              ? CapabilityLimits.ImageMimeTypes
              : []
          ),
          supportsChat
        )
      );
    }

    return result;
  }

  public async Task<CloudCallResult<string>> GenerateStructuredAsync(
    string apiKey,
    string modelId,
    IReadOnlyList<ChatMessage> messages,
    JsonElement? schema,
    string stage,
    ProviderChatOptions? options,
    CancellationToken cancellationToken
  )
  {
    options ??= ProviderChatOptions.Empty;
    var generationConfig = new Dictionary<string, object?>
    {
      ["temperature"] = 0,
      ["responseMimeType"] = "application/json"
    };

    if (schema is not null)
    {
      generationConfig["responseJsonSchema"] = schema;
    }

    using var request = CreateJsonRequest(
      $"models/{Uri.EscapeDataString(modelId)}:generateContent",
      apiKey,
      new
      {
        contents = ToGeminiContents(
          messages,
          options.Images
        ),
        generationConfig
      }
    );
    using var response = await SendAsync(
      request,
      stage,
      modelId,
      cancellationToken
    );
    using var document = await ReadDocumentAsync(
      response,
      stage,
      modelId,
      cancellationToken
    );

    return new CloudCallResult<string>(
      ReadText(
        document.RootElement,
        stage,
        modelId
      ),
      ProviderUsageMapper.FromGemini(
        document.RootElement
      ),
      ReadRateLimit(
        response
      )
    );
  }

  private static TimeSpan? ReadRetryAfter(
    HttpResponseMessage response
  )
  {
    var value = response.Headers.RetryAfter;

    if (value?.Delta is not null)
    {
      return value.Delta.Value;
    }

    if (value?.Date is null)
    {
      return null;
    }

    var delay = value.Date.Value - DateTimeOffset.UtcNow;
    return delay > TimeSpan.Zero
      ? delay
      : TimeSpan.Zero;
  }

  public async Task<CloudCallResult<OllamaToolResponse>> GenerateToolCallAsync(
    string apiKey,
    string modelId,
    IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyList<OllamaToolDefinition> tools,
    string stage,
    CancellationToken cancellationToken
  )
  {
    using var request = CreateJsonRequest(
      $"models/{Uri.EscapeDataString(modelId)}:generateContent",
      apiKey,
      new
      {
        contents = ToGeminiToolContents(
          messages
        ),
        tools = new[]
        {
          new
          {
            functionDeclarations = tools.Select(
              tool => new
              {
                name = tool.Name,
                description = tool.Description,
                parametersJsonSchema = tool.Parameters
              }
            )
          }
        },
        toolConfig = new
        {
          functionCallingConfig = new
          {
            mode = "AUTO"
          }
        },
        generationConfig = new
        {
          temperature = 0
        }
      }
    );
    using var response = await SendAsync(
      request,
      stage,
      modelId,
      cancellationToken
    );
    using var document = await ReadDocumentAsync(
      response,
      stage,
      modelId,
      cancellationToken
    );
    var root = document.RootElement;

    return new CloudCallResult<OllamaToolResponse>(
      new OllamaToolResponse(
        ReadOptionalText(
          root
        ),
        null,
        ReadFunctionCalls(
          root,
          stage,
          modelId
        )
      ),
      ProviderUsageMapper.FromGemini(
        root
      ),
      ReadRateLimit(
        response
      )
    );
  }

  public async IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    string apiKey,
    string modelId,
    IReadOnlyList<ChatMessage> messages,
    ProviderChatOptions? options,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    options ??= ProviderChatOptions.Empty;
    using var request = CreateJsonRequest(
      $"models/{Uri.EscapeDataString(modelId)}:streamGenerateContent?alt=sse",
      apiKey,
      new
      {
        contents = ToGeminiContents(
          messages,
          options.Images
        ),
        tools = options.WebSearchEnabled
          ? new object[]
          {
            new
            {
              googleSearch = new
              {
              }
            }
          }
          : null
      }
    );
    using var response = await SendAsync(
      request,
      "chat-stream",
      modelId,
      cancellationToken,
      HttpCompletionOption.ResponseHeadersRead
    );
    var rateLimit = ReadRateLimit(
      response
    );
    ProviderTokenUsage? usage = null;
    var accepted = false;
    var citations = new Dictionary<string, ProviderCitation>(
      StringComparer.Ordinal
    );
    var searchQueries = 0;

    await using var stream = await response.Content.ReadAsStreamAsync(
      cancellationToken
    );
    using var reader = new StreamReader(
      stream,
      Encoding.UTF8
    );

    while (true)
    {
      var line = await reader.ReadLineAsync(
        cancellationToken
      );

      if (line is null)
      {
        break;
      }

      if (
        string.IsNullOrWhiteSpace(
          line
        )
        || !line.StartsWith(
          "data:",
          StringComparison.Ordinal
        )
      )
      {
        continue;
      }

      var payload = line["data:".Length..].Trim();

      if (string.Equals(
        payload,
        "[DONE]",
        StringComparison.Ordinal
      ))
      {
        break;
      }

      JsonDocument document;

      try
      {
        document = JsonDocument.Parse(
          payload
        );
      }
      catch (JsonException exception)
      {
        throw new CloudProviderException(
          "provider-contract-invalid",
          "chat-stream",
          ProviderId,
          modelId,
          "Google AI Studio returned an invalid stream event.",
          (int)response.StatusCode,
          false,
          rateLimit,
          exception
        );
      }

      using (document)
      {
        var root = document.RootElement;
        usage = ProviderUsageMapper.FromGemini(
            root
          )
          ?? usage;
        foreach (var citation in ReadGroundingCitations(
          root
        ))
        {
          citations[citation.Url] = citation;
        }
        searchQueries += ReadGroundingQueryCount(
          root
        );
        var delta = ReadOptionalText(
          root
        );

        if (!string.IsNullOrEmpty(
          delta
        ))
        {
          if (!accepted)
          {
            accepted = true;
            yield return new OllamaChatUpdate(
              true,
              null
            );
          }

          yield return new OllamaChatUpdate(
            false,
            delta
          );
        }
      }
    }

    yield return new OllamaChatUpdate(
      accepted,
      null,
      true,
      usage,
      rateLimit,
      citations.Values.ToArray(),
      new ProviderActivityMetadata(
        ImageCount: options.Images.Count,
        ImageBytes: options.Images.Sum(
          image => image.Bytes.LongLength
        ),
        SearchQueryCount: searchQueries,
        GroundedRequestCount: options.WebSearchEnabled
          ? 1
          : 0,
        CitationCount: citations.Count,
        Accuracy: UsageAccuracy.Exact
      )
    );
  }

  private HttpRequestMessage CreateRequest(
    HttpMethod method,
    string path,
    string apiKey
  )
  {
    var request = new HttpRequestMessage(
      method,
      new Uri(
        _baseUri,
        path
      )
    );
    request.Headers.TryAddWithoutValidation(
      "x-goog-api-key",
      apiKey
    );
    return request;
  }

  private HttpRequestMessage CreateJsonRequest(
    string path,
    string apiKey,
    object body
  )
  {
    var request = CreateRequest(
      HttpMethod.Post,
      path,
      apiKey
    );
    request.Content = JsonContent.Create(
      body,
      options: JsonOptions
    );
    return request;
  }

  private async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    string stage,
    string? modelId,
    CancellationToken cancellationToken,
    HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead
  )
  {
    try
    {
      var response = await _httpClientFactory
        .CreateClient(
          nameof(
            GeminiCloudProvider
          )
        )
        .SendAsync(
          request,
          completion,
          cancellationToken
        );

      if (response.IsSuccessStatusCode)
      {
        return response;
      }

      var diagnostic = await ReadSanitizedErrorAsync(
        response,
        request.Headers.TryGetValues(
          "x-goog-api-key",
          out var apiKeys
        )
          ? apiKeys.FirstOrDefault()
          : null,
        cancellationToken
      );
      var exception = Error(
        response.StatusCode == HttpStatusCode.TooManyRequests
          ? "provider-rate-limited"
          : response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            ? "provider-authentication-failed"
            : "provider-request-failed",
        stage,
        modelId,
        diagnostic,
        response.StatusCode,
        response.StatusCode == HttpStatusCode.TooManyRequests
          || (int)response.StatusCode >= 500,
        response
      );
      response.Dispose();
      throw exception;
    }
    catch (OperationCanceledException) when (
      !cancellationToken.IsCancellationRequested
    )
    {
      throw new CloudProviderException(
        "provider-timeout",
        stage,
        ProviderId,
        modelId,
        "Google AI Studio did not complete the request in time.",
        504,
        true
      );
    }
    catch (HttpRequestException exception)
    {
      throw new CloudProviderException(
        "provider-unavailable",
        stage,
        ProviderId,
        modelId,
        "Google AI Studio could not be reached.",
        null,
        true,
        null,
        exception
      );
    }
  }

  private async Task<JsonDocument> ReadDocumentAsync(
    HttpResponseMessage response,
    string stage,
    string? modelId,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await JsonDocument.ParseAsync(
        await response.Content.ReadAsStreamAsync(
          cancellationToken
        ),
        cancellationToken: cancellationToken
      );
    }
    catch (JsonException exception)
    {
      throw new CloudProviderException(
        "provider-contract-invalid",
        stage,
        ProviderId,
        modelId,
        "Google AI Studio returned invalid JSON.",
        (int)response.StatusCode,
        false,
        ReadRateLimit(
          response
        ),
        exception
      );
    }
  }

  private CloudProviderException Error(
    string code,
    string stage,
    string? modelId,
    string message,
    HttpStatusCode? status,
    bool retryable,
    HttpResponseMessage response
  )
  {
    return new CloudProviderException(
      code,
      stage,
      ProviderId,
      modelId,
      message,
      status is null
        ? null
        : (int)status.Value,
      retryable,
      ReadRateLimit(
        response
      ),
      retryAfter: ReadRetryAfter(
        response
      )
    );
  }

  private static IReadOnlyList<object> ToGeminiContents(
    IReadOnlyList<ChatMessage> messages,
    IReadOnlyList<ProviderImagePayload>? images = null
  )
  {
    var lastUserIndex = -1;

    for (var index = 0; index < messages.Count; index++)
    {
      if (string.Equals(
        messages[index].Role,
        "user",
        StringComparison.OrdinalIgnoreCase
      ))
      {
        lastUserIndex = index;
      }
    }

    return messages.Select(
      (
        message,
        index
      ) =>
      {
        var parts = new List<object>
        {
          new
          {
            text = message.Content
          }
        };

        if (index == lastUserIndex && images is not null)
        {
          parts.AddRange(
            images.Select(
              image => (object)new
              {
                inlineData = new
                {
                  mimeType = image.MimeType,
                  data = Convert.ToBase64String(
                    image.Bytes
                  )
                }
              }
            )
          );
        }

        return (object)new
        {
          role = string.Equals(
            message.Role,
            "assistant",
            StringComparison.OrdinalIgnoreCase
          )
            ? "model"
            : "user",
          parts
        };
      }
    ).ToArray();
  }

  private static bool SupportsDocumentedGoogleSearch(
    string modelId
  )
  {
    return modelId.StartsWith(
      "gemini-2.0-",
      StringComparison.OrdinalIgnoreCase
    ) || modelId.StartsWith(
      "gemini-2.5-",
      StringComparison.OrdinalIgnoreCase
    ) || modelId.StartsWith(
      "gemini-3",
      StringComparison.OrdinalIgnoreCase
    );
  }

  private static IReadOnlyList<ProviderCitation> ReadGroundingCitations(
    JsonElement root
  )
  {
    var result = new List<ProviderCitation>();

    foreach (var metadata in EnumerateGroundingMetadata(
      root
    ))
    {
      if (
        !metadata.TryGetProperty(
          "groundingChunks",
          out var chunks
        )
        || chunks.ValueKind != JsonValueKind.Array
      )
      {
        continue;
      }

      foreach (var chunk in chunks.EnumerateArray())
      {
        if (
          !chunk.TryGetProperty(
            "web",
            out var web
          )
        )
        {
          continue;
        }

        var url = ReadString(
          web,
          "uri"
        );

        if (
          !Uri.TryCreate(
            url,
            UriKind.Absolute,
            out var uri
          )
          || !string.Equals(
            uri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.Ordinal
          )
        )
        {
          throw new CapabilityException(
            "invalid-citation",
            "provider-citations",
            "Google AI Studio returned an unsafe citation URL.",
            "Grounding citations must use absolute HTTPS URLs.",
            ModelProviderIds.GoogleAiStudio,
            null
          );
        }

        result.Add(
          new ProviderCitation(
            $"source-{result.Count + 1}",
            ReadString(
              web,
              "title"
            ) ?? uri.Host,
            uri.AbsoluteUri
          )
        );
      }
    }

    return result;
  }

  private static int ReadGroundingQueryCount(
    JsonElement root
  )
  {
    return EnumerateGroundingMetadata(
      root
    ).Sum(
      metadata => metadata.TryGetProperty(
        "webSearchQueries",
        out var queries
      ) && queries.ValueKind == JsonValueKind.Array
        ? queries.GetArrayLength()
        : 0
    );
  }

  private static IEnumerable<JsonElement> EnumerateGroundingMetadata(
    JsonElement root
  )
  {
    if (
      !root.TryGetProperty(
        "candidates",
        out var candidates
      )
      || candidates.ValueKind != JsonValueKind.Array
    )
    {
      yield break;
    }

    foreach (var candidate in candidates.EnumerateArray())
    {
      if (candidate.TryGetProperty(
        "groundingMetadata",
        out var metadata
      ) && metadata.ValueKind == JsonValueKind.Object)
      {
        yield return metadata;
      }
    }
  }

  private static IReadOnlyList<object> ToGeminiToolContents(
    IReadOnlyList<OllamaToolMessage> messages
  )
  {
    var contents = new List<object>();

    foreach (var message in messages)
    {
      var role = string.Equals(
        message.Role,
        "assistant",
        StringComparison.OrdinalIgnoreCase
      )
        ? "model"
        : "user";
      var parts = new List<object>();

      if (!string.IsNullOrWhiteSpace(
        message.Content
      ))
      {
        parts.Add(
          new
          {
            text = message.Content
          }
        );
      }

      if (message.Images is { Count: > 0 })
      {
        parts.AddRange(
          message.Images.Select(
            image => (object)new
            {
              inlineData = new
              {
                mimeType = image.MimeType,
                data = Convert.ToBase64String(image.Bytes)
              }
            }
          )
        );
      }

      if (message.ToolCalls is not null)
      {
        parts.AddRange(
          message.ToolCalls.Select(
            call => (object)new
            {
              functionCall = new
              {
                name = call.Name,
                args = call.Arguments
              }
            }
          )
        );
      }

      if (!string.IsNullOrWhiteSpace(
        message.ToolName
      ))
      {
        parts.Add(
          new
          {
            functionResponse = new
            {
              name = message.ToolName,
              response = new
              {
                output = message.Content ?? string.Empty
              }
            }
          }
        );
      }

      contents.Add(
        new
        {
          role,
          parts
        }
      );
    }

    return contents;
  }

  private static string ReadText(
    JsonElement root,
    string stage,
    string modelId
  )
  {
    var text = ReadOptionalText(
      root
    );

    if (string.IsNullOrWhiteSpace(
      text
    ))
    {
      throw new CloudProviderException(
        "provider-contract-invalid",
        stage,
        ModelProviderIds.GoogleAiStudio,
        modelId,
        "Google AI Studio returned no text candidate.",
        null,
        false
      );
    }

    return text;
  }

  private static string? ReadOptionalText(
    JsonElement root
  )
  {
    return EnumerateParts(
      root
    )
      .Where(
        part => part.TryGetProperty(
          "text",
          out _
        )
      )
      .Select(
        part => part.GetProperty(
          "text"
        ).GetString()
      )
      .FirstOrDefault(
        value => !string.IsNullOrEmpty(
          value
        )
      );
  }

  private static IReadOnlyList<OllamaToolCall> ReadFunctionCalls(
    JsonElement root,
    string stage,
    string modelId
  )
  {
    var calls = new List<OllamaToolCall>();

    foreach (var part in EnumerateParts(
      root
    ))
    {
      if (
        !part.TryGetProperty(
          "functionCall",
          out var function
        )
      )
      {
        continue;
      }

      var name = ReadString(
        function,
        "name"
      );

      if (
        string.IsNullOrWhiteSpace(
          name
        )
        || !function.TryGetProperty(
          "args",
          out var arguments
        )
      )
      {
        throw new CloudProviderException(
          "tool-protocol-invalid",
          stage,
          ModelProviderIds.GoogleAiStudio,
          modelId,
          "Google AI Studio returned an invalid function call.",
          null,
          false
        );
      }

      calls.Add(
        new OllamaToolCall(
          name,
          arguments.Clone()
        )
      );
    }

    return calls;
  }

  private static IEnumerable<JsonElement> EnumerateParts(
    JsonElement root
  )
  {
    if (
      !root.TryGetProperty(
        "candidates",
        out var candidates
      )
      || candidates.ValueKind != JsonValueKind.Array
    )
    {
      yield break;
    }

    foreach (var candidate in candidates.EnumerateArray())
    {
      if (
        !candidate.TryGetProperty(
          "content",
          out var content
        )
        || !content.TryGetProperty(
          "parts",
          out var parts
        )
        || parts.ValueKind != JsonValueKind.Array
      )
      {
        continue;
      }

      foreach (var part in parts.EnumerateArray())
      {
        yield return part;
      }
    }
  }

  private static IReadOnlyList<string> ReadStringArray(
    JsonElement parent,
    string propertyName
  )
  {
    return parent.TryGetProperty(
        propertyName,
        out var value
      )
      && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray()
          .Select(
            item => item.GetString()
          )
          .Where(
            item => !string.IsNullOrWhiteSpace(
              item
            )
          )
          .Cast<string>()
          .ToArray()
        : [];
  }

  private static string? ReadString(
    JsonElement parent,
    string propertyName
  )
  {
    return parent.TryGetProperty(
        propertyName,
        out var value
      )
      && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
  }

  private static int? ReadInt32(
    JsonElement parent,
    string propertyName
  )
  {
    return parent.TryGetProperty(
        propertyName,
        out var value
      )
      && value.TryGetInt32(
        out var parsed
      )
        ? parsed
        : null;
  }

  private static ProviderRateLimitSnapshot? ReadRateLimit(
    HttpResponseMessage response
  )
  {
    var remaining = ReadHeaderLong(
      response,
      "x-ratelimit-remaining"
    );
    var reset = ReadHeaderDate(
      response,
      "x-ratelimit-reset"
    );

    return remaining is null && reset is null
      ? null
      : new ProviderRateLimitSnapshot(
        null,
        remaining,
        reset,
        null,
        null,
        null,
        "provider-headers",
        DateTimeOffset.UtcNow
      );
  }

  private static long? ReadHeaderLong(
    HttpResponseMessage response,
    string name
  )
  {
    return response.Headers.TryGetValues(
        name,
        out var values
      )
      && long.TryParse(
        values.FirstOrDefault(),
        out var parsed
      )
        ? parsed
        : null;
  }

  private static DateTimeOffset? ReadHeaderDate(
    HttpResponseMessage response,
    string name
  )
  {
    if (!response.Headers.TryGetValues(
      name,
      out var values
    ))
    {
      return null;
    }

    var value = values.FirstOrDefault();

    if (DateTimeOffset.TryParse(
      value,
      out var date
    ))
    {
      return date.ToUniversalTime();
    }

    if (
      double.TryParse(
        value?.TrimEnd(
          's'
        ),
        out var seconds
      )
    )
    {
      return DateTimeOffset.UtcNow.AddSeconds(
        seconds
      );
    }

    return null;
  }

  private static async Task<string> ReadSanitizedErrorAsync(
    HttpResponseMessage response,
    string? apiKey,
    CancellationToken cancellationToken
  )
  {
    var body = await response.Content.ReadAsStringAsync(
      cancellationToken
    );

    if (body.Length > 2_048)
    {
      body = body[..2_048];
    }

    try
    {
      using var document = JsonDocument.Parse(
        body
      );
      var root = document.RootElement;

      if (
        root.TryGetProperty(
          "error",
          out var error
        )
        && error.TryGetProperty(
          "message",
          out var message
        )
      )
      {
        var diagnostic = message.GetString()
          ?? "Google AI Studio rejected the request.";
        return string.IsNullOrEmpty(
          apiKey
        )
          ? diagnostic
          : diagnostic.Replace(
            apiKey,
            "[redacted]",
            StringComparison.Ordinal
          );
      }
    }
    catch (JsonException)
    {
    }

    return "Google AI Studio rejected the request.";
  }
}
