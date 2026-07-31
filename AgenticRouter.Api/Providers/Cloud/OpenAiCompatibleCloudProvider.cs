using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Providers.Cloud;

public abstract class OpenAiCompatibleCloudProvider : ICloudProviderAdapter
{
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  );

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly Uri _baseUri;

  protected OpenAiCompatibleCloudProvider(
    IHttpClientFactory httpClientFactory,
    Uri baseUri
  )
  {
    _httpClientFactory = httpClientFactory;
    _baseUri = baseUri;
  }

  public abstract string ProviderId { get; }

  public abstract string DisplayName { get; }

  public abstract string ProtocolVersion { get; }

  public virtual async Task<IReadOnlyList<InstalledModel>> ListModelsAsync(
    string apiKey,
    CancellationToken cancellationToken
  )
  {
    using var request = CreateRequest(
      HttpMethod.Get,
      "models",
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
        "data",
        out var data
      )
      || data.ValueKind != JsonValueKind.Array
    )
    {
      throw Error(
        "provider-contract-invalid",
        "cloud-model-list",
        null,
        "The provider returned an invalid model list.",
        response.StatusCode,
        false,
        response
      );
    }

    var models = new List<InstalledModel>();

    foreach (var item in data.EnumerateArray())
    {
      if (
        !item.TryGetProperty(
          "id",
          out var idElement
        )
        || string.IsNullOrWhiteSpace(
          idElement.GetString()
        )
      )
      {
        continue;
      }

      var id = idElement.GetString()!;
      var reference = new ProviderModelReference(
        ProviderId,
        id
      );
      var created = ReadInt64(
        item,
        "created"
      );
      var revision = created is null
        ? ReadString(
          item,
          "version"
        )
        : created.Value.ToString(
          CultureInfo.InvariantCulture
        );
      models.Add(
        new InstalledModel(
          reference.Qualified,
          null,
          created is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(
              created.Value
            )
            : null,
          revision,
          ProviderId,
          reference.Display,
          ReadCapabilities(
            item
          )
        )
      );
    }

    return await EnrichModelsAsync(
      models,
      apiKey,
      cancellationToken
    );
  }

  public async Task<CloudCallResult<string>> GenerateStructuredAsync(
    string apiKey,
    string modelId,
    IReadOnlyList<ChatMessage> messages,
    JsonElement? schema,
    string stage,
    CancellationToken cancellationToken
  )
  {
    Dictionary<string, object?> responseFormat = schema is null
      ? new()
      {
        ["type"] = "json_object"
      }
      : new()
      {
        ["type"] = "json_schema",
        ["json_schema"] = new
        {
          name = "agentic_router_response",
          strict = true,
          schema
        }
      };
    using var request = CreateJsonRequest(
      "chat/completions",
      apiKey,
      new
      {
        model = modelId,
        messages,
        temperature = 0,
        stream = false,
        response_format = responseFormat
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
    var content = ReadAssistantContent(
      document.RootElement
    );

    if (content is null)
    {
      throw Error(
        "provider-contract-invalid",
        stage,
        modelId,
        "The provider returned no assistant content.",
        response.StatusCode,
        false,
        response
      );
    }

    return new CloudCallResult<string>(
      content,
      ProviderUsageMapper.FromOpenAiCompatible(
        document.RootElement
      ),
      ReadRateLimit(
        response
      )
    );
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
      "chat/completions",
      apiKey,
      new
      {
        model = modelId,
        messages = ToOpenAiMessages(
          messages
        ),
        tools = tools.Select(
          tool => new
          {
            type = "function",
            function = new
            {
              name = tool.Name,
              description = tool.Description,
              parameters = tool.Parameters
            }
          }
        ),
        tool_choice = "auto",
        temperature = 0,
        stream = false
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
    var message = ReadAssistantMessage(
      document.RootElement
    );

    if (message is null)
    {
      throw Error(
        "provider-contract-invalid",
        stage,
        modelId,
        "The provider returned no assistant tool message.",
        response.StatusCode,
        false,
        response
      );
    }

    return new CloudCallResult<OllamaToolResponse>(
      new OllamaToolResponse(
        ReadString(
          message.Value,
          "content"
        ),
        ReadString(
          message.Value,
          "reasoning"
        ) ?? ReadString(
          message.Value,
          "reasoning_content"
        ),
        ReadToolCalls(
          message.Value
        )
      ),
      ProviderUsageMapper.FromOpenAiCompatible(
        document.RootElement
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
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    using var request = CreateJsonRequest(
      "chat/completions",
      apiKey,
      new
      {
        model = modelId,
        messages,
        temperature = 0,
        stream = true,
        stream_options = new
        {
          include_usage = true
        }
      }
    );
    using var response = await SendAsync(
      request,
      "generation",
      modelId,
      cancellationToken,
      HttpCompletionOption.ResponseHeadersRead
    );
    var rateLimit = ReadRateLimit(
      response
    );
    await using var stream = await response.Content.ReadAsStreamAsync(
      cancellationToken
    );
    using var reader = new StreamReader(
      stream,
      Encoding.UTF8,
      true,
      16_384,
      false
    );
    ProviderTokenUsage? finalUsage = null;
    var accepted = false;

    while (true)
    {
      var line = await reader.ReadLineAsync(
        cancellationToken
      );

      if (line is null)
      {
        break;
      }

      if (!line.StartsWith(
        "data:",
        StringComparison.Ordinal
      ))
      {
        continue;
      }

      var payload = line[5..].Trim();

      if (string.Equals(
        payload,
        "[DONE]",
        StringComparison.Ordinal
      ))
      {
        yield return new OllamaChatUpdate(
          false,
          null,
          true,
          finalUsage,
          rateLimit
        );
        yield break;
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
        throw Error(
          "provider-stream-invalid",
          "generation",
          modelId,
          "The provider returned an invalid streaming event.",
          response.StatusCode,
          false,
          response,
          exception
        );
      }

      using (document)
      {
        finalUsage = ProviderUsageMapper.FromOpenAiCompatible(
          document.RootElement
        ) ?? finalUsage;
        var delta = ReadDeltaContent(
          document.RootElement
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
      false,
      null,
      true,
      finalUsage,
      rateLimit
    );
  }

  protected virtual Task<IReadOnlyList<InstalledModel>> EnrichModelsAsync(
    IReadOnlyList<InstalledModel> models,
    string apiKey,
    CancellationToken cancellationToken
  )
  {
    return Task.FromResult(
      models
    );
  }

  protected HttpRequestMessage CreateRequest(
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
    request.Headers.Authorization = new AuthenticationHeaderValue(
      "Bearer",
      apiKey
    );
    request.Headers.Accept.Add(
      new MediaTypeWithQualityHeaderValue(
        "application/json"
      )
    );
    return request;
  }

  protected HttpRequestMessage CreateJsonRequest(
    string path,
    string apiKey,
    object payload
  )
  {
    var request = CreateRequest(
      HttpMethod.Post,
      path,
      apiKey
    );
    request.Content = JsonContent.Create(
      payload,
      options: JsonOptions
    );
    return request;
  }

  protected async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    string stage,
    string? modelId,
    CancellationToken cancellationToken,
    HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead
  )
  {
    HttpResponseMessage response;

    try
    {
      response = await _httpClientFactory.CreateClient()
        .SendAsync(
          request,
          completionOption,
          cancellationToken
        );
    }
    catch (OperationCanceledException exception) when (
      !cancellationToken.IsCancellationRequested
    )
    {
      throw new CloudProviderException(
        "provider-timeout",
        stage,
        ProviderId,
        modelId,
        $"{DisplayName} did not complete the request in time.",
        504,
        true,
        null,
        exception
      );
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (HttpRequestException exception)
    {
      throw new CloudProviderException(
        "provider-disconnected",
        stage,
        ProviderId,
        modelId,
        $"{DisplayName} could not be reached.",
        null,
        true,
        null,
        exception
      );
    }

    if (!response.IsSuccessStatusCode)
    {
      var error = await ReadSanitizedErrorAsync(
        response,
        request.Headers.Authorization?.Parameter,
        cancellationToken
      );
      var rateLimit = ReadRateLimit(
        response
      );
      var status = (int)response.StatusCode;
      var code = response.StatusCode == HttpStatusCode.TooManyRequests
        ? "provider-rate-limited"
        : response.StatusCode is HttpStatusCode.Unauthorized
          or HttpStatusCode.Forbidden
          ? "provider-key-invalid"
          : response.StatusCode == HttpStatusCode.NotFound
            ? "provider-model-unavailable"
            : "provider-request-failed";
      response.Dispose();
      throw new CloudProviderException(
        code,
        stage,
        ProviderId,
        modelId,
        $"{DisplayName} rejected the request. {error}",
        status,
        status is 408 or 429 or >= 500,
        rateLimit
      );
    }

    return response;
  }

  protected ProviderRateLimitSnapshot? ReadRateLimit(
    HttpResponseMessage response
  )
  {
    var requestLimit = ReadHeaderLong(
      response,
      "x-ratelimit-limit-requests"
    );
    var requestRemaining = ReadHeaderLong(
      response,
      "x-ratelimit-remaining-requests"
    );
    var tokenLimit = ReadHeaderLong(
      response,
      "x-ratelimit-limit-tokens"
    );
    var tokenRemaining = ReadHeaderLong(
      response,
      "x-ratelimit-remaining-tokens"
    );
    var requestReset = ReadReset(
      response,
      "x-ratelimit-reset-requests"
    );
    var tokenReset = ReadReset(
      response,
      "x-ratelimit-reset-tokens"
    );

    if (
      requestLimit is null
      && requestRemaining is null
      && tokenLimit is null
      && tokenRemaining is null
      && requestReset is null
      && tokenReset is null
    )
    {
      return null;
    }

    return new ProviderRateLimitSnapshot(
      requestLimit,
      requestRemaining,
      requestReset,
      tokenLimit,
      tokenRemaining,
      tokenReset,
      "provider-headers",
      DateTimeOffset.UtcNow
    );
  }

  protected ProviderModelCapabilities ReadCapabilities(
    JsonElement model
  )
  {
    var context = ReadInt64(
      model,
      "context_window"
    ) ?? ReadNestedInt64(
      model,
      "limits",
      "max_context_length"
    );
    var capabilities = model.TryGetProperty(
      "capabilities",
      out var value
    )
      ? value
      : default;
    var tools = ReadCapability(
      capabilities,
      "tools",
      "tool_use",
      "function_calling"
    );
    var vision = ReadCapability(
      capabilities,
      "vision",
      "image"
    );
    var streaming = capabilities.ValueKind == JsonValueKind.Undefined
      || ReadCapability(
        capabilities,
        "streaming"
      );
    var confirmed = capabilities.ValueKind is
      JsonValueKind.Object
      or JsonValueKind.Array;

    return new ProviderModelCapabilities(
      true,
      streaming,
      tools,
      vision,
      false,
      context is null
        ? null
        : Convert.ToInt32(
          Math.Min(
            int.MaxValue,
            context.Value
          )
        ),
      confirmed
        ? "provider-model-metadata"
        : "provider-adapter-default",
      confirmed
    );
  }

  protected CloudProviderException Error(
    string code,
    string stage,
    string? modelId,
    string message,
    HttpStatusCode statusCode,
    bool retryable,
    HttpResponseMessage response,
    Exception? innerException = null
  )
  {
    return new CloudProviderException(
      code,
      stage,
      ProviderId,
      modelId,
      message,
      (int)statusCode,
      retryable,
      ReadRateLimit(
        response
      ),
      innerException
    );
  }

  protected static string? ReadString(
    JsonElement parent,
    string propertyName
  )
  {
    return parent.TryGetProperty(
        propertyName,
        out var value
      ) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
  }

  protected static long? ReadInt64(
    JsonElement parent,
    string propertyName
  )
  {
    return parent.TryGetProperty(
        propertyName,
        out var value
      ) && value.TryGetInt64(
        out var parsed
      )
        ? parsed
        : null;
  }

  private static IReadOnlyList<object> ToOpenAiMessages(
    IReadOnlyList<OllamaToolMessage> messages
  )
  {
    var result = new List<object>();
    var latestCallIds = new Dictionary<string, string>(
      StringComparer.Ordinal
    );

    for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
    {
      var message = messages[messageIndex];
      var calls = message.ToolCalls?.Select(
        call => new
        {
          id = call.Id
            ?? $"call_{messageIndex}_{call.Name}",
          type = "function",
          function = new
          {
            name = call.Name,
            arguments = call.Arguments.GetRawText()
          }
        }
      ).ToArray();

      if (calls is not null)
      {
        foreach (var call in calls)
        {
          latestCallIds[call.function.name] = call.id;
        }
      }

      var toolCallId = message.ToolName is not null
        && latestCallIds.TryGetValue(
          message.ToolName,
          out var matchedId
        )
          ? matchedId
          : message.ToolName;
      result.Add(
        new
        {
          role = message.Role,
          content = message.Content,
          tool_call_id = toolCallId,
          tool_calls = calls
        }
      );
    }

    return result;
  }

  private IReadOnlyList<OllamaToolCall> ReadToolCalls(
    JsonElement message
  )
  {
    if (
      !message.TryGetProperty(
        "tool_calls",
        out var calls
      )
      || calls.ValueKind != JsonValueKind.Array
    )
    {
      return [];
    }

    var result = new List<OllamaToolCall>();

    foreach (var call in calls.EnumerateArray())
    {
      if (
        !call.TryGetProperty(
          "function",
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
      var arguments = ReadString(
        function,
        "arguments"
      );

      if (
        string.IsNullOrWhiteSpace(
          name
        )
        || string.IsNullOrWhiteSpace(
          arguments
        )
      )
      {
        continue;
      }

      try
      {
        using var document = JsonDocument.Parse(
          arguments
        );
        result.Add(
          new OllamaToolCall(
            name,
            document.RootElement.Clone(),
            ReadString(
              call,
              "id"
            )
          )
        );
      }
      catch (JsonException exception)
      {
        throw new CloudProviderException(
          "tool-protocol-invalid",
          "tool-call-parse",
          ProviderId,
          null,
          "The provider returned malformed tool arguments.",
          null,
          false,
          null,
          exception
        );
      }
    }

    return result;
  }

  private static JsonElement? ReadAssistantMessage(
    JsonElement root
  )
  {
    if (
      root.TryGetProperty(
        "choices",
        out var choices
      )
      && choices.ValueKind == JsonValueKind.Array
    )
    {
      var first = choices.EnumerateArray().FirstOrDefault();

      if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty(
        "message",
        out var message
      ))
      {
        return message;
      }
    }

    return null;
  }

  private static string? ReadAssistantContent(
    JsonElement root
  )
  {
    var message = ReadAssistantMessage(
      root
    );

    return message is null
      ? null
      : ReadString(
        message.Value,
        "content"
      );
  }

  private static string? ReadDeltaContent(
    JsonElement root
  )
  {
    if (
      !root.TryGetProperty(
        "choices",
        out var choices
      )
      || choices.ValueKind != JsonValueKind.Array
    )
    {
      return null;
    }

    var first = choices.EnumerateArray().FirstOrDefault();

    return first.ValueKind == JsonValueKind.Object && first.TryGetProperty(
      "delta",
      out var delta
    )
      ? ReadString(
        delta,
        "content"
      )
      : null;
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
        "The provider returned invalid JSON.",
        (int)response.StatusCode,
        false,
        null,
        exception
      );
    }
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

      if (
        document.RootElement.TryGetProperty(
          "error",
          out var error
        )
      )
      {
        var diagnostic = ReadString(
          error,
          "message"
        )
          ?? (
            error.ValueKind == JsonValueKind.String
              ? error.GetString()
              : null
          )
          ?? "No provider diagnostic was available.";
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

    return "No provider diagnostic was available.";
  }

  private static long? ReadHeaderLong(
    HttpResponseMessage response,
    string name
  )
  {
    return response.Headers.TryGetValues(
        name,
        out var values
      ) && long.TryParse(
        values.FirstOrDefault(),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var parsed
      )
        ? parsed
        : null;
  }

  private static DateTimeOffset? ReadReset(
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
      CultureInfo.InvariantCulture,
      DateTimeStyles.AssumeUniversal,
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
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
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

  private static bool ReadCapability(
    JsonElement capabilities,
    params string[] names
  )
  {
    if (capabilities.ValueKind == JsonValueKind.Object)
    {
      return names.Any(
        name => capabilities.TryGetProperty(
          name,
          out var value
        ) && value.ValueKind == JsonValueKind.True
      );
    }

    if (capabilities.ValueKind == JsonValueKind.Array)
    {
      return capabilities.EnumerateArray().Any(
        item => item.ValueKind == JsonValueKind.String
          && names.Contains(
            item.GetString(),
            StringComparer.OrdinalIgnoreCase
          )
      );
    }

    return false;
  }
}

public sealed class GroqCloudProvider : OpenAiCompatibleCloudProvider
{
  public GroqCloudProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration
  )
    : base(
      httpClientFactory,
      new Uri(
        configuration["AgenticRouter:Providers:GroqBaseUrl"]
          ?? "https://api.groq.com/openai/v1/",
        UriKind.Absolute
      )
    )
  {
  }

  public override string ProviderId => ModelProviderIds.Groq;

  public override string DisplayName => "Groq";

  public override string ProtocolVersion => "groq-openai-v1";
}

public sealed class CerebrasCloudProvider : OpenAiCompatibleCloudProvider
{
  private readonly IHttpClientFactory _httpClientFactory;
  private readonly Uri _publicBaseUri;

  public CerebrasCloudProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration
  )
    : base(
      httpClientFactory,
      new Uri(
        configuration["AgenticRouter:Providers:CerebrasBaseUrl"]
          ?? "https://api.cerebras.ai/v1/",
        UriKind.Absolute
      )
    )
  {
    _httpClientFactory = httpClientFactory;
    _publicBaseUri = new Uri(
      configuration["AgenticRouter:Providers:CerebrasPublicBaseUrl"]
        ?? "https://api.cerebras.ai/public/v1/",
      UriKind.Absolute
    );
  }

  public override string ProviderId => ModelProviderIds.Cerebras;

  public override string DisplayName => "Cerebras";

  public override string ProtocolVersion => "cerebras-openai-v2";

  protected override async Task<IReadOnlyList<InstalledModel>> EnrichModelsAsync(
    IReadOnlyList<InstalledModel> models,
    string apiKey,
    CancellationToken cancellationToken
  )
  {
    var enriched = new List<InstalledModel>();

    foreach (var model in models)
    {
      var reference = ProviderModelReference.Parse(
        model.Name
      );
      using var response = await _httpClientFactory.CreateClient().GetAsync(
        new Uri(
          _publicBaseUri,
          $"models/{Uri.EscapeDataString(reference.ModelId)}"
        ),
        cancellationToken
      );

      if (!response.IsSuccessStatusCode)
      {
        enriched.Add(
          model
        );
        continue;
      }

      using var document = await JsonDocument.ParseAsync(
        await response.Content.ReadAsStreamAsync(
          cancellationToken
        ),
        cancellationToken: cancellationToken
      );
      enriched.Add(
        model with
        {
          Capabilities = ReadCapabilities(
            document.RootElement
          ),
          Pricing = ReadPublicPricing(
            document.RootElement
          )
        }
      );
    }

    return enriched;
  }

  private static ProviderModelPricing? ReadPublicPricing(
    JsonElement model
  )
  {
    if (
      !model.TryGetProperty(
        "pricing",
        out var pricing
      )
      || pricing.ValueKind != JsonValueKind.Object
    )
    {
      return null;
    }

    var input = ReadDecimal(
      pricing,
      "prompt"
    );
    var output = ReadDecimal(
      pricing,
      "completion"
    );

    return input is null && output is null
      ? null
      : new ProviderModelPricing(
        input,
        output,
        "USD",
        "provider-public-model-metadata"
      );
  }

  private static decimal? ReadDecimal(
    JsonElement parent,
    string propertyName
  )
  {
    if (!parent.TryGetProperty(
      propertyName,
      out var value
    ))
    {
      return null;
    }

    return value.ValueKind switch
    {
      JsonValueKind.Number when value.TryGetDecimal(
        out var numeric
      ) => numeric,
      JsonValueKind.String when decimal.TryParse(
        value.GetString(),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var text
      ) => text,
      _ => null
    };
  }
}
