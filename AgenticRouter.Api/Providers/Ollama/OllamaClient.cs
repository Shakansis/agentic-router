using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Providers.Ollama;

public sealed class OllamaClient : IOllamaClient
{
  private static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(
    100
  );

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private readonly HttpClient _httpClient;
  private readonly ISettingsStore _settingsStore;

  public OllamaClient(
    HttpClient httpClient,
    ISettingsStore settingsStore
  )
  {
    _httpClient = httpClient;
    _settingsStore = settingsStore;
  }

  public async Task<IReadOnlyList<InstalledModel>> GetModelsAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    using var request = new HttpRequestMessage(
      HttpMethod.Get,
      new Uri(
        baseUri,
        "/api/tags"
      )
    );
    using var response = await SendAsync(
      request,
      "model-discovery",
      cancellationToken
    );
    var payload = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(
      JsonOptions,
      cancellationToken
    ) ?? throw ProviderError(
      "model-discovery",
      "Ollama returned an empty model list.",
      "The /api/tags response body was empty.",
      (int)response.StatusCode
    );

    return payload.Models
      .Where(
        model => !string.IsNullOrWhiteSpace(
          model.Name
        )
      )
      .Select(
        model => new InstalledModel(
          model.Name!,
          model.Size,
          model.ModifiedAt,
          model.Digest
        )
      )
      .OrderBy(
        model => model.Name,
        StringComparer.OrdinalIgnoreCase
      )
      .ToArray();
  }

  public async Task<string> ClassifyAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    CancellationToken cancellationToken
  )
  {
    return await GenerateJsonAsync(
      baseUri,
      model,
      messages,
      "router-classification",
      cancellationToken
    );
  }

  public async Task<string> GenerateJsonAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    CancellationToken cancellationToken
  )
  {
    return await GenerateAsync(
      baseUri,
      model,
      messages,
      stage,
      JsonSerializer.SerializeToElement(
        "json"
      ),
      cancellationToken
    );
  }

  public async Task<string> GenerateTextAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    CancellationToken cancellationToken
  )
  {
    return await GenerateAsync(
      baseUri,
      model,
      messages,
      stage,
      null,
      cancellationToken
    );
  }

  public async Task<string> GenerateStructuredAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    JsonElement schema,
    string stage,
    CancellationToken cancellationToken
  )
  {
    return await GenerateAsync(
      baseUri,
      model,
      messages,
      stage,
      schema,
      cancellationToken
    );
  }

  public async Task<OllamaToolResponse> GenerateToolCallAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyList<OllamaToolDefinition> tools,
    string stage,
    CancellationToken cancellationToken
  )
  {
    var policy = await GetGenerationPolicyAsync(
      true,
      cancellationToken
    );
    var payload = CreateRequest(
      model,
      messages,
      false,
      null,
      new OllamaOptions(
        0,
        policy.ContextTokens,
        policy.OutputTokens
      ),
      null,
      tools.Select(
        tool => new OllamaApiTool(
          "function",
          new OllamaFunctionDefinition(
            tool.Name,
            tool.Description,
            tool.Parameters
          )
        )
      ).ToArray()
    );
    using var response = await SendChatAsync(
      baseUri,
      payload,
      stage,
      cancellationToken,
      requestTimeout: policy.Timeout
    );
    OllamaChatChunk result;

    try
    {
      result = await response.Content.ReadFromJsonAsync<OllamaChatChunk>(
        JsonOptions,
        cancellationToken
      ) ?? throw new JsonException(
        "The non-streaming /api/chat response body was empty."
      );
    }
    catch (JsonException exception)
    {
      throw new ToolProtocolException(
        stage,
        Sanitize(
          exception.Message
        ),
        (int)response.StatusCode,
        exception
      );
    }

    if (!string.IsNullOrWhiteSpace(
      result.Error
    ))
    {
      throw ProviderError(
        stage,
        "The model could not produce a response.",
        result.Error,
        (int)response.StatusCode
      );
    }

    return new OllamaToolResponse(
      result.Message?.Content,
      result.Message?.Thinking,
      result.Message?.ToolCalls?.Select(
        call => new OllamaToolCall(
          call.Function.Name,
          call.Function.Arguments.Clone()
        )
      ).ToArray() ?? []
    );
  }

  private async Task<string> GenerateAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    JsonElement? format,
    CancellationToken cancellationToken
  )
  {
    var policy = await GetGenerationPolicyAsync(
      false,
      cancellationToken
    );
    var payload = CreateRequest(
      model,
      messages,
      false,
      format,
      new OllamaOptions(
        0,
        policy.ContextTokens,
        policy.OutputTokens
      ),
      null
    );
    using var response = await SendChatAsync(
      baseUri,
      payload,
      stage,
      cancellationToken,
      requestTimeout: policy.Timeout
    );
    var result = await response.Content.ReadFromJsonAsync<OllamaChatChunk>(
      JsonOptions,
      cancellationToken
    ) ?? throw ProviderError(
      stage,
      "The model returned an empty response.",
      "The non-streaming /api/chat response body was empty.",
      (int)response.StatusCode
    );

    if (!string.IsNullOrWhiteSpace(
      result.Error
    ))
    {
      throw ProviderError(
        stage,
        "The model could not produce a response.",
        result.Error,
        (int)response.StatusCode
      );
    }

    return result.Message?.Content ?? string.Empty;
  }

  public async Task<OllamaModelCapabilities> GetModelCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var json = JsonSerializer.Serialize(
      new OllamaShowRequest(
        model
      ),
      JsonOptions
    );
    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      new Uri(
        baseUri,
        "/api/show"
      )
    )
    {
      Content = new StringContent(
        json,
        Encoding.UTF8,
        "application/json"
      )
    };
    using var response = await SendAsync(
      request,
      "model-capability-inspection",
      cancellationToken
    );
    OllamaShowResponse payload;

    try
    {
      payload = await response.Content.ReadFromJsonAsync<OllamaShowResponse>(
        JsonOptions,
        cancellationToken
      ) ?? throw new JsonException(
        "The /api/show response body was empty."
      );
    }
    catch (JsonException exception)
    {
      throw ProviderError(
        "model-capability-inspection",
        "Ollama returned an invalid model capability response.",
        exception.Message,
        (int)response.StatusCode
      );
    }

    var capabilities = payload.Capabilities?
      .Where(
        capability => !string.IsNullOrWhiteSpace(
          capability
        )
      )
      .Distinct(
        StringComparer.OrdinalIgnoreCase
      )
      .ToArray() ?? [];

    return new OllamaModelCapabilities(
      model,
      capabilities,
      capabilities.Contains(
        "tools",
        StringComparer.OrdinalIgnoreCase
      )
    );
  }

  public async Task<string> GetVersionAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    using var request = new HttpRequestMessage(
      HttpMethod.Get,
      new Uri(
        baseUri,
        "/api/version"
      )
    );
    using var response = await SendAsync(
      request,
      "provider-version-inspection",
      cancellationToken
    );
    var payload = await response.Content.ReadFromJsonAsync<OllamaVersionResponse>(
      JsonOptions,
      cancellationToken
    ) ?? throw ProviderError(
      "provider-version-inspection",
      "Ollama returned an empty version response.",
      "The /api/version response body was empty.",
      (int)response.StatusCode
    );

    if (string.IsNullOrWhiteSpace(
      payload.Version
    ))
    {
      throw ProviderError(
        "provider-version-inspection",
        "Ollama returned an invalid version response.",
        "The /api/version response did not contain a version.",
        (int)response.StatusCode
      );
    }

    return payload.Version;
  }

  public async Task<IReadOnlyList<OllamaRunningModel>> GetRunningModelsAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    using var request = new HttpRequestMessage(
      HttpMethod.Get,
      new Uri(
        baseUri,
        "/api/ps"
      )
    );
    using var response = await SendAsync(
      request,
      "runtime-model-inspection",
      cancellationToken
    );
    var payload = await response.Content.ReadFromJsonAsync<OllamaPsResponse>(
      JsonOptions,
      cancellationToken
    ) ?? throw ProviderError(
      "runtime-model-inspection",
      "Ollama returned an empty running-model response.",
      "The /api/ps response body was empty.",
      (int)response.StatusCode
    );

    return payload.Models
      .Where(
        model => !string.IsNullOrWhiteSpace(
          model.Name
        )
      )
      .Select(
        model => new OllamaRunningModel(
          model.Name!,
          model.Size,
          model.SizeVram,
          model.ExpiresAt
        )
      )
      .ToArray();
  }

  public async Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    CancellationToken cancellationToken
  )
  {
    var policy = await GetGenerationPolicyAsync(
      false,
      cancellationToken
    );
    var payload = CreateRequest(
      model,
      Array.Empty<ChatMessage>(),
      false,
      null,
      null,
      keepAlive
    );
    using var response = await SendChatAsync(
      baseUri,
      payload,
      keepAlive == 0
        ? "resident-model-unload"
        : "resident-model-preload",
      cancellationToken,
      requestTimeout: policy.Timeout
    );
  }

  public async IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var policy = await GetGenerationPolicyAsync(
      false,
      cancellationToken
    );
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken
    );
    timeout.CancelAfter(
      policy.Timeout
    );
    await using var enumerator = StreamChatCoreAsync(
      baseUri,
      model,
      messages,
      policy,
      timeout.Token
    ).GetAsyncEnumerator(
      timeout.Token
    );

    while (true)
    {
      bool hasNext;

      try
      {
        hasNext = await enumerator.MoveNextAsync();
      }
      catch (OperationCanceledException exception) when (
        !cancellationToken.IsCancellationRequested
        && timeout.IsCancellationRequested
      )
      {
        throw ProviderTimeout(
          "generation",
          policy.Timeout,
          exception
        );
      }

      if (!hasNext)
      {
        break;
      }

      yield return enumerator.Current;
    }
  }

  private async IAsyncEnumerable<OllamaChatUpdate> StreamChatCoreAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    GenerationPolicy policy,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var payload = CreateRequest(
      model,
      messages,
      true,
      null,
      new OllamaOptions(
        0,
        policy.ContextTokens,
        policy.OutputTokens
      ),
      null
    );
    using var response = await SendChatAsync(
      baseUri,
      payload,
      "generation",
      cancellationToken,
      HttpCompletionOption.ResponseHeadersRead,
      Timeout.InfiniteTimeSpan
    );
    yield return new OllamaChatUpdate(
      true,
      null
    );
    await using var stream = await response.Content.ReadAsStreamAsync(
      cancellationToken
    );
    using var reader = new StreamReader(
      stream
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

      if (string.IsNullOrWhiteSpace(
        line
      ))
      {
        continue;
      }

      OllamaChatChunk chunk;

      try
      {
        chunk = JsonSerializer.Deserialize<OllamaChatChunk>(
          line,
          JsonOptions
        ) ?? throw new JsonException(
          "The stream chunk was empty."
        );
      }
      catch (JsonException exception)
      {
        throw new OllamaProviderException(
          "generation",
          "Ollama returned an invalid streaming response.",
          exception.Message,
          (int)response.StatusCode,
          true,
          exception
        );
      }

      if (!string.IsNullOrWhiteSpace(
        chunk.Error
      ))
      {
        throw ProviderError(
          "generation",
          "Ollama could not generate the response.",
          chunk.Error,
          (int)response.StatusCode
        );
      }

      if (!string.IsNullOrEmpty(
        chunk.Message?.Content
      ))
      {
        yield return new OllamaChatUpdate(
          false,
          chunk.Message.Content
        );
      }

      if (chunk.Done)
      {
        break;
      }
    }
  }

  private async Task<HttpResponseMessage> SendChatAsync(
    Uri baseUri,
    OllamaChatRequest payload,
    string stage,
    CancellationToken cancellationToken,
    HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
    TimeSpan? requestTimeout = null
  )
  {
    var json = JsonSerializer.Serialize(
      payload,
      JsonOptions
    );
    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      new Uri(
        baseUri,
        "/api/chat"
      )
    )
    {
      Content = new StringContent(
        json,
        Encoding.UTF8,
        "application/json"
      )
    };

    return await SendAsync(
      request,
      stage,
      cancellationToken,
      completionOption,
      requestTimeout
    );
  }

  private async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    string stage,
    CancellationToken cancellationToken,
    HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
    TimeSpan? requestTimeout = null
  )
  {
    HttpResponseMessage response;
    var effectiveTimeout = requestTimeout ?? DefaultProviderTimeout;
    CancellationTokenSource? timeout = null;
    var requestCancellationToken = cancellationToken;

    if (effectiveTimeout != Timeout.InfiniteTimeSpan)
    {
      timeout = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken
      );
      timeout.CancelAfter(
        effectiveTimeout
      );
      requestCancellationToken = timeout.Token;
    }

    try
    {
      response = await _httpClient.SendAsync(
        request,
        completionOption,
        requestCancellationToken
      );
    }
    catch (OperationCanceledException exception) when (
      !cancellationToken.IsCancellationRequested
      && timeout?.IsCancellationRequested == true
    )
    {
      throw ProviderTimeout(
        stage,
        effectiveTimeout,
        exception
      );
    }
    catch (HttpRequestException exception)
    {
      throw new OllamaProviderException(
        stage,
        "Ollama is unavailable. Check that it is running and that the saved URL is correct.",
        exception.Message,
        null,
        true,
        exception
      );
    }
    finally
    {
      timeout?.Dispose();
    }

    if (response.IsSuccessStatusCode)
    {
      return response;
    }

    var details = await response.Content.ReadAsStringAsync(
      cancellationToken
    );
    var status = (int)response.StatusCode;
    response.Dispose();

    throw ProviderError(
      stage,
      $"Ollama returned HTTP {status}.",
      details,
      status
    );
  }

  private async Task<GenerationPolicy> GetGenerationPolicyAsync(
    bool toolOutput,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );

    return new GenerationPolicy(
      settings.Context.ProviderContextTokens,
      toolOutput
        ? settings.Execution.MaxToolOutputTokens
        : settings.Context.ReservedResponseTokens,
      TimeSpan.FromSeconds(
        settings.Runtime.GenerationTimeoutSeconds
      )
    );
  }

  private static OllamaProviderException ProviderTimeout(
    string stage,
    TimeSpan timeout,
    OperationCanceledException exception
  )
  {
    var seconds = Convert.ToInt32(
      timeout.TotalSeconds
    );

    return new OllamaProviderException(
      stage,
      $"Ollama did not complete the request within the configured generation timeout of {seconds} second{(seconds == 1 ? string.Empty : "s")}.",
      $"The configured generation timeout of {seconds} second{(seconds == 1 ? string.Empty : "s")} elapsed.",
      504,
      true,
      exception
    );
  }

  private static OllamaProviderException ProviderError(
    string stage,
    string message,
    string details,
    int? status
  )
  {
    var sanitized = Sanitize(
      details
    );

    if (IsToolProtocolFailure(
      stage,
      sanitized
    ))
    {
      return new ToolProtocolException(
        stage,
        sanitized,
        status
      );
    }

    return new OllamaProviderException(
      stage,
      message,
      sanitized,
      status,
      status is null || status >= 500,
      isMemoryPressure: IsMemoryPressure(
        sanitized
      )
    );
  }

  private static bool IsToolProtocolFailure(
    string stage,
    string details
  )
  {
    if (
      !string.Equals(
        stage,
        "local-action-planning",
        StringComparison.Ordinal
      )
      && !stage.StartsWith(
        "tool-conformance-",
        StringComparison.Ordinal
      )
    )
    {
      return false;
    }

    var protocolMarkers = new[]
    {
      "error parsing tool call",
      "tool call parse",
      "tool call parser",
      "tool call syntax",
      "malformed tool call",
      "invalid tool call",
      "xml syntax error",
      "invalid xml",
      "json syntax error",
      "unexpected end of json",
      "unexpected end of data",
      "harmony parser"
    };

    return protocolMarkers.Any(
      marker => details.Contains(
        marker,
        StringComparison.OrdinalIgnoreCase
      )
    ) || (
      details.Contains(
        "<parameter>",
        StringComparison.OrdinalIgnoreCase
      )
      && details.Contains(
        "</function>",
        StringComparison.OrdinalIgnoreCase
      )
    );
  }

  private static bool IsMemoryPressure(
    string value
  )
  {
    return new[]
    {
      "out of memory",
      "insufficient memory",
      "requires more system memory",
      "requires more gpu memory",
      "failed to load model",
      "model loading failed"
    }.Any(
      marker => value.Contains(
        marker,
        StringComparison.OrdinalIgnoreCase
      )
    );
  }

  private static string Sanitize(
    string value
  )
  {
    var singleLine = value
      .Replace(
        "\r",
        " ",
        StringComparison.Ordinal
      )
      .Replace(
        "\n",
        " ",
        StringComparison.Ordinal
      )
      .Trim();

    return singleLine.Length <= 1_000
      ? singleLine
      : singleLine[..1_000];
  }

  private static OllamaChatRequest CreateRequest(
    string model,
    IReadOnlyList<ChatMessage> messages,
    bool stream,
    JsonElement? format,
    OllamaOptions? options,
    int? keepAlive,
    IReadOnlyList<OllamaApiTool>? tools = null
  )
  {
    return new OllamaChatRequest(
      model,
      messages.Select(
        message => new OllamaChatMessage(
          message.Role,
          message.Content
        )
      ).ToArray(),
      stream,
      format,
      options,
      keepAlive,
      tools
    );
  }

  private static OllamaChatRequest CreateRequest(
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    bool stream,
    JsonElement? format,
    OllamaOptions? options,
    int? keepAlive,
    IReadOnlyList<OllamaApiTool>? tools = null
  )
  {
    return new OllamaChatRequest(
      model,
      messages.Select(
        message => new OllamaChatMessage(
          message.Role,
          message.Content,
          message.Thinking,
          message.ToolCalls?.Select(
            call => new OllamaApiToolCall(
              "function",
              new OllamaApiFunctionCall(
                call.Name,
                call.Arguments
              )
            )
          ).ToArray(),
          message.ToolName
        )
      ).ToArray(),
      stream,
      format,
      options,
      keepAlive,
      tools
    );
  }

  private sealed record OllamaTagsResponse(
    IReadOnlyList<OllamaModel> Models
  );

  private sealed record OllamaModel(
    string? Name,
    long? Size,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt,
    string? Digest
  );

  private sealed record OllamaVersionResponse(
    string? Version
  );

  private sealed record OllamaPsResponse(
    IReadOnlyList<OllamaPsModel> Models
  );

  private sealed record OllamaShowRequest(
    string Model
  );

  private sealed record OllamaShowResponse(
    IReadOnlyList<string>? Capabilities
  );

  private sealed record OllamaPsModel(
    string? Name,
    long? Size,
    [property: JsonPropertyName("size_vram")] long? SizeVram,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt
  );

  private sealed record OllamaChatRequest(
    string Model,
    IReadOnlyList<OllamaChatMessage> Messages,
    bool Stream,
    JsonElement? Format,
    OllamaOptions? Options,
    [property: JsonPropertyName("keep_alive")] int? KeepAlive,
    IReadOnlyList<OllamaApiTool>? Tools
  );

  private sealed record OllamaOptions(
    double Temperature,
    int NumCtx,
    int NumPredict
  );

  private sealed record GenerationPolicy(
    int ContextTokens,
    int OutputTokens,
    TimeSpan Timeout
  );

  private sealed record OllamaChatMessage(
    string Role,
    string? Content,
    string? Thinking = null,
    [property: JsonPropertyName("tool_calls")]
    IReadOnlyList<OllamaApiToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_name")]
    string? ToolName = null
  );

  private sealed record OllamaApiTool(
    string Type,
    OllamaFunctionDefinition Function
  );

  private sealed record OllamaFunctionDefinition(
    string Name,
    string Description,
    JsonElement Parameters
  );

  private sealed record OllamaApiToolCall(
    string? Type,
    OllamaApiFunctionCall Function
  );

  private sealed record OllamaApiFunctionCall(
    string Name,
    JsonElement Arguments
  );

  private sealed record OllamaChatChunk(
    OllamaChatMessage? Message,
    bool Done,
    string? Error
  );
}
