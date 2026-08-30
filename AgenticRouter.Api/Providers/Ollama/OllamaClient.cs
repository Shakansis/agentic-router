using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

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
  private readonly ITokenEstimator _tokenEstimator;
  private readonly IUsageRecorder _usageRecorder;

  public OllamaClient(
    HttpClient httpClient,
    ISettingsStore settingsStore,
    ITokenEstimator tokenEstimator,
    IUsageRecorder usageRecorder
  )
  {
    _httpClient = httpClient;
    _settingsStore = settingsStore;
    _tokenEstimator = tokenEstimator;
    _usageRecorder = usageRecorder;
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
        ) && !model.Name.StartsWith(
          "functiongemma",
          StringComparison.OrdinalIgnoreCase
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

  public async IAsyncEnumerable<OllamaPullProgress> PullModelAsync(
    Uri baseUri,
    string model,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var payload = JsonSerializer.Serialize(
      new
      {
        model,
        stream = true
      },
      JsonOptions
    );
    using var request = new HttpRequestMessage(
      HttpMethod.Post,
      new Uri(
        baseUri,
        "/api/pull"
      )
    )
    {
      Content = new StringContent(
        payload,
        Encoding.UTF8,
        "application/json"
      )
    };
    using var response = await SendAsync(
      request,
      "model-pull",
      cancellationToken,
      HttpCompletionOption.ResponseHeadersRead,
      Timeout.InfiniteTimeSpan
    );
    await using var stream = await response.Content.ReadAsStreamAsync(
      cancellationToken
    );
    using var reader = new StreamReader(stream);

    while (await reader.ReadLineAsync(cancellationToken) is { } line)
    {
      if (string.IsNullOrWhiteSpace(line))
      {
        continue;
      }

      OllamaPullResponse update;
      try
      {
        update = JsonSerializer.Deserialize<OllamaPullResponse>(
          line,
          JsonOptions
        ) ?? throw new JsonException("The pull update was empty.");
      }
      catch (JsonException exception)
      {
        throw new OllamaProviderException(
          "model-pull",
          "Ollama returned an invalid model download update.",
          exception.Message,
          (int)response.StatusCode,
          false,
          exception
        );
      }

      if (!string.IsNullOrWhiteSpace(update.Error))
      {
        throw new OllamaProviderException(
          "model-pull",
          "Ollama could not download the selected model.",
          update.Error,
          (int)response.StatusCode,
          true
        );
      }

      yield return new OllamaPullProgress(
        update.Status ?? "Downloading",
        update.Digest,
        update.Total,
        update.Completed
      );
    }
  }

  public async Task<string> ClassifyAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    return await GenerateJsonAsync(
      baseUri,
      model,
      messages,
      "router-classification",
      usageContext,
      cancellationToken
    );
  }

  public async Task<string> GenerateJsonAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    ProviderCallContext usageContext,
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
      usageContext,
      cancellationToken
    );
  }

  public async Task<string> GenerateTextAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    return await GenerateAsync(
      baseUri,
      model,
      messages,
      stage,
      null,
      usageContext,
      cancellationToken
    );
  }

  public async Task<string> GenerateStructuredAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    JsonElement schema,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Action<ProviderTokenUsage?>? usageObserver = null,
    ProviderChatOptions? options = null
  )
  {
    return await GenerateAsync(
      baseUri,
      model,
      messages,
      stage,
      schema,
      usageContext,
      cancellationToken,
      usageObserver,
      options
    );
  }

  public async Task<OllamaToolResponse> GenerateToolCallAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyList<OllamaToolDefinition> tools,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Func<string, CancellationToken, ValueTask>? onThinkingDelta = null,
    Func<string, CancellationToken, ValueTask>? onContentDelta = null,
    bool toolOutput = true
  )
  {
    var stopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateToolMessages(
      messages
    ) + _tokenEstimator.EstimateText(
      JsonSerializer.Serialize(
        tools,
        JsonOptions
      )
    );
    long estimatedOutput = 0;
    ProviderTokenUsage? providerUsage = null;
    var status = UsageStatuses.Failure;
    OllamaRuntimeProfileError? runtimeFailure = null;

    try
    {
      var policy = await GetGenerationPolicyAsync(
        baseUri,
        model,
        usageContext,
        estimatedInput,
        toolOutput,
        cancellationToken
      );
      var payload = CreateRequest(
        model,
        messages,
        onThinkingDelta is not null || onContentDelta is not null,
        null,
        new OllamaOptions(
          0,
          policy.Resolution.EffectiveContextTokens,
          policy.OutputTokens,
          policy.MainGpu
        ),
        null,
        tools.Count == 0
          ? null
          : tools.Select(
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
        onThinkingDelta is null
          ? HttpCompletionOption.ResponseContentRead
          : HttpCompletionOption.ResponseHeadersRead,
        requestTimeout: onThinkingDelta is null && onContentDelta is null
          ? policy.Timeout
          : Timeout.InfiniteTimeSpan
      );
      OllamaToolResponse toolResponse;

      if (onThinkingDelta is null && onContentDelta is null)
      {
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

        toolResponse = new OllamaToolResponse(
          result.Message?.Content,
          result.Message?.Thinking,
          result.Message?.ToolCalls?.Select(
            call => new OllamaToolCall(
              call.Function.Name,
              call.Function.Arguments.Clone()
            )
          ).ToArray() ?? [],
          policy.Resolution
        );
        providerUsage = ReadUsage(
          result
        );
      }
      else
      {
        var streamed = await ReadStreamingToolResponseAsync(
          response,
          stage,
          policy.Resolution,
          onThinkingDelta,
          onContentDelta,
          cancellationToken
        );
        toolResponse = streamed.Response;
        providerUsage = streamed.Usage;
      }

      estimatedOutput = _tokenEstimator.EstimateToolResponse(
        toolResponse
      );
      status = UsageStatuses.Success;
      return toolResponse with
      {
        Usage = providerUsage
      };
    }
    catch (OllamaRuntimeProfileException exception)
    {
      runtimeFailure = exception.Error;
      throw;
    }
    finally
    {
      if (cancellationToken.IsCancellationRequested)
      {
        status = UsageStatuses.Cancellation;
      }

      await RecordUsageAsync(
        usageContext,
        model,
        stopwatch,
        status,
        providerUsage,
        estimatedInput,
        estimatedOutput,
        runtimeFailure: runtimeFailure
      );
    }
  }

  private static async Task<StreamingToolResponse> ReadStreamingToolResponseAsync(
    HttpResponseMessage response,
    string stage,
    OllamaContextResolution contextResolution,
    Func<string, CancellationToken, ValueTask>? onThinkingDelta,
    Func<string, CancellationToken, ValueTask>? onContentDelta,
    CancellationToken cancellationToken
  )
  {
    var content = new StringBuilder();
    var thinking = new StringBuilder();
    var toolCalls = new List<OllamaToolCall>();
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
          "The streaming tool response chunk was empty."
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
        chunk.Error
      ))
      {
        throw ProviderError(
          stage,
          "The model could not produce a response.",
          chunk.Error,
          (int)response.StatusCode
        );
      }

      if (!string.IsNullOrEmpty(
        chunk.Message?.Thinking
      ))
      {
        thinking.Append(
          chunk.Message.Thinking
        );
        if (onThinkingDelta is not null)
        {
          await onThinkingDelta(
            chunk.Message.Thinking,
            cancellationToken
          );
        }
      }

      if (!string.IsNullOrEmpty(
        chunk.Message?.Content
      ))
      {
        content.Append(
          chunk.Message.Content
        );
        if (onContentDelta is not null)
        {
          await onContentDelta(
            chunk.Message.Content,
            cancellationToken
          );
        }
      }

      if (chunk.Message?.ToolCalls is not null)
      {
        toolCalls.AddRange(
          chunk.Message.ToolCalls.Select(
            call => new OllamaToolCall(
              call.Function.Name,
              call.Function.Arguments.Clone()
            )
          )
        );
      }

      if (chunk.Done)
      {
        return new StreamingToolResponse(
          new OllamaToolResponse(
            content.Length == 0
              ? null
              : content.ToString(),
            thinking.Length == 0
              ? null
              : thinking.ToString(),
            toolCalls,
            contextResolution
          ),
          ReadUsage(
            chunk
          )
        );
      }
    }

    throw new ToolProtocolException(
      stage,
      "The streaming tool response ended before Ollama sent a terminal chunk.",
      (int)response.StatusCode
    );
  }

  private async Task<string> GenerateAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    JsonElement? format,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Action<ProviderTokenUsage?>? usageObserver = null,
    ProviderChatOptions? options = null
  )
  {
    options ??= ProviderChatOptions.Empty;
    var stopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateMessages(
      messages
    ) + options.Images.Sum(
      image => Math.Max(1_024L, (long)Math.Ceiling(image.Bytes.LongLength / 512d))
    ) + (format is null ? 0 : _tokenEstimator.EstimateText(format.Value.GetRawText()));
    long estimatedOutput = 0;
    ProviderTokenUsage? providerUsage = null;
    var status = UsageStatuses.Failure;
    OllamaRuntimeProfileError? runtimeFailure = null;

    try
    {
      var policy = await GetGenerationPolicyAsync(
        baseUri,
        model,
        usageContext,
        estimatedInput,
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
          policy.Resolution.EffectiveContextTokens,
          policy.OutputTokens,
          policy.MainGpu
        ),
        null,
        images: options.Images
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

      var content = result.Message?.Content ?? string.Empty;
      estimatedOutput = _tokenEstimator.EstimateText(
        content
      );
      providerUsage = ReadUsage(
        result
      );
      status = UsageStatuses.Success;
      usageObserver?.Invoke(
        providerUsage
      );
      return content;
    }
    catch (OllamaRuntimeProfileException exception)
    {
      runtimeFailure = exception.Error;
      throw;
    }
    finally
    {
      if (cancellationToken.IsCancellationRequested)
      {
        status = UsageStatuses.Cancellation;
      }

      await RecordUsageAsync(
        usageContext,
        model,
        stopwatch,
        status,
        providerUsage,
        estimatedInput,
        estimatedOutput,
        runtimeFailure: runtimeFailure
      );
    }
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
    request.Headers.TryAddWithoutValidation(
      "X-Agentic-Router-Operation",
      "model-capability-inspection"
    );
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

  public async Task<OllamaModelMetadata> GetModelMetadataAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var payload = await GetShowResponseAsync(
      baseUri,
      model,
      "model-metadata-inspection",
      cancellationToken
    );
    int? declaredContext = null;

    if (payload.ModelInfo is not null)
    {
      foreach (var pair in payload.ModelInfo)
      {
        if (
          !pair.Key.EndsWith(
            ".context_length",
            StringComparison.OrdinalIgnoreCase
          )
          && !string.Equals(
            pair.Key,
            "context_length",
            StringComparison.OrdinalIgnoreCase
          )
        )
        {
          continue;
        }

        if (
          pair.Value.ValueKind == JsonValueKind.Number
          && pair.Value.TryGetInt32(
            out var context
          )
          && context > 0
        )
        {
          declaredContext = declaredContext is null
            ? context
            : Math.Max(
              declaredContext.Value,
              context
            );
        }
      }
    }

    return new OllamaModelMetadata(
      model,
      declaredContext,
      payload.Details?.ParameterSize,
      payload.Details?.QuantizationLevel,
      payload.Details?.Format,
      payload.Details?.Family,
      payload.Details?.Families ?? []
    );
  }

  private async Task<OllamaShowResponse> GetShowResponseAsync(
    Uri baseUri,
    string model,
    string stage,
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
      stage,
      cancellationToken
    );

    try
    {
      return await response.Content.ReadFromJsonAsync<OllamaShowResponse>(
        JsonOptions,
        cancellationToken
      ) ?? throw new JsonException(
        "The /api/show response body was empty."
      );
    }
    catch (JsonException exception)
    {
      throw ProviderError(
        stage,
        "Ollama returned invalid model metadata.",
        exception.Message,
        (int)response.StatusCode
      );
    }
  }

  public async Task<ProviderModelCapabilities> GetProviderModelCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var inspected = await GetModelCapabilitiesAsync(
      baseUri,
      model,
      cancellationToken
    );
    var capabilities = inspected.Capabilities;
    var chat = capabilities.Contains(
      "completion",
      StringComparer.OrdinalIgnoreCase
    ) || capabilities.Contains(
      "chat",
      StringComparer.OrdinalIgnoreCase
    );
    var vision = capabilities.Contains(
      "vision",
      StringComparer.OrdinalIgnoreCase
    );

    return new ProviderModelCapabilities(
      chat,
      chat,
      capabilities.Contains(
        "tools",
        StringComparer.OrdinalIgnoreCase
      ),
      vision,
      false,
      null,
      "ollama-api-show",
      true,
      StructuredOutput: chat,
      Reasoning: capabilities.Contains(
        "thinking",
        StringComparer.OrdinalIgnoreCase
      ) || capabilities.Contains(
        "reasoning",
        StringComparer.OrdinalIgnoreCase
      ),
      MaximumImageCount: vision
        ? CapabilityLimits.MaximumImageCount
        : 0,
      MaximumImageBytes: vision
        ? CapabilityLimits.MaximumImageBytes
        : 0,
      SupportedImageMimeTypes: vision
        ? CapabilityLimits.ImageMimeTypes
        : [],
      ToolProtocolConfirmed: false
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

  public Task<string> GetProtocolVersionAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    return GetVersionAsync(
      baseUri,
      cancellationToken
    );
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
          model.Digest,
          model.Size,
          model.SizeVram,
          model.ContextLength,
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
    await SetModelResidencyAsync(
      baseUri,
      model,
      keepAlive,
      null,
      null,
      cancellationToken
    );
  }

  public async Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    int? contextTokens,
    CancellationToken cancellationToken
  )
  {
    await SetModelResidencyAsync(
      baseUri,
      model,
      keepAlive,
      contextTokens,
      null,
      cancellationToken
    );
  }

  public async Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    int? contextTokens,
    int? mainGpu,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );
    var payload = CreateRequest(
      model,
      Array.Empty<ChatMessage>(),
      false,
      null,
      contextTokens is null || keepAlive == 0
        ? null
        : new OllamaOptions(
          0,
          contextTokens,
          null,
          mainGpu
        ),
      keepAlive
    );
    using var response = await SendChatAsync(
      baseUri,
      payload,
      keepAlive == 0
        ? "model-unload"
        : "model-preload",
      cancellationToken,
      requestTimeout: TimeSpan.FromSeconds(
        settings.Runtime.GenerationTimeoutSeconds
      )
    );
  }

  public async IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    ProviderCallContext usageContext,
    ProviderChatOptions? options,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    options ??= ProviderChatOptions.Empty;
    var stopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateMessages(
      messages
    ) + options.Images.Sum(
      image => Math.Max(
        1_024L,
        (long)Math.Ceiling(
          image.Bytes.LongLength / 512d
        )
      )
    );
    long estimatedOutput = 0;
    ProviderTokenUsage? providerUsage = null;
    var status = UsageStatuses.Failure;
    GenerationPolicy policy;
    try
    {
      policy = await GetGenerationPolicyAsync(
        baseUri,
        model,
        options.Images.Count > 0
          ? usageContext with
          {
            ModelRole = UsageModelRoles.VisionRequest
          }
          : usageContext,
        estimatedInput,
        false,
        cancellationToken
      );
    }
    catch (OllamaRuntimeProfileException exception)
    {
      await RecordUsageAsync(
        usageContext,
        model,
        stopwatch,
        status,
        providerUsage,
        estimatedInput,
        estimatedOutput,
        runtimeFailure: exception.Error
      );
      throw;
    }

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(policy.Timeout);
    await using var enumerator = StreamChatCoreAsync(
      baseUri,
      model,
      messages,
      policy,
      options,
      timeout.Token
    ).GetAsyncEnumerator(timeout.Token);

    try
    {
      var resolutionEmitted = false;

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

        var update = enumerator.Current;
        estimatedOutput += _tokenEstimator.EstimateText(
          update.Delta
        );
        providerUsage = update.Usage
          ?? providerUsage;

        if (update.Done)
        {
          status = UsageStatuses.Success;
        }

        yield return !resolutionEmitted
          ? update with
          {
            ContextResolution = policy.Resolution
          }
          : update;
        resolutionEmitted = true;
      }
    }
    finally
    {
      if (cancellationToken.IsCancellationRequested)
      {
        status = UsageStatuses.Cancellation;
      }

      await RecordUsageAsync(
        usageContext,
        model,
        stopwatch,
        status,
        providerUsage,
        estimatedInput,
        estimatedOutput,
        new ProviderActivityMetadata(
          ImageCount: options.Images.Count,
          ImageBytes: options.Images.Sum(
            image => image.Bytes.LongLength
          ),
          Accuracy: UsageAccuracy.Exact
        )
      );
    }
  }

  private async IAsyncEnumerable<OllamaChatUpdate> StreamChatCoreAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    GenerationPolicy policy,
    ProviderChatOptions options,
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
        policy.Resolution.EffectiveContextTokens,
        policy.OutputTokens,
        policy.MainGpu
      ),
      null,
      images: options.Images
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
        chunk.Message?.Thinking
      ))
      {
        yield return new OllamaChatUpdate(
          false,
          null,
          ThinkingDelta: chunk.Message.Thinking
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
        yield return new OllamaChatUpdate(
          false,
          null,
          true,
          ReadUsage(
            chunk
          )
        );
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
    Uri baseUri,
    string model,
    ProviderCallContext usageContext,
    long estimatedInputTokens,
    bool toolOutput,
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(
      cancellationToken
    );

    var requestedOutput = toolOutput
      ? settings.Execution.MaxToolOutputTokens
      : settings.Context.ReservedResponseTokens;
    OllamaModelMetadata metadata;

    try
    {
      metadata = await GetModelMetadataAsync(
        baseUri,
        model,
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      throw new OllamaRuntimeProfileException(
        "model-metadata-unavailable",
        "Ollama model metadata is unavailable for runtime context resolution.",
        usageContext.RequestPurpose,
        model,
        usageContext.ModelRevision,
        OllamaRuntimeProfileResolver.NormalizeRole(
          usageContext.ModelRole
        ),
        null,
        null,
        true,
        exception.Message,
        exception
      );
    }

    if (metadata.DeclaredContextTokens is null)
    {
      throw new OllamaRuntimeProfileException(
        "declared-context-unavailable",
        "Ollama did not declare a maximum context for this model.",
        usageContext.RequestPurpose,
        model,
        usageContext.ModelRevision,
        OllamaRuntimeProfileResolver.NormalizeRole(
          usageContext.ModelRole
        ),
        null,
        null,
        false,
        "The native /api/show response did not contain a positive context_length."
      );
    }

    var resolution = OllamaRuntimeProfileResolver.Resolve(
      settings,
      model,
      usageContext.ModelRevision,
      usageContext.ModelRole,
      metadata.DeclaredContextTokens,
      estimatedInputTokens,
      requestedOutput
    );

    if (usageContext.RuntimeContextTokens is not null)
    {
      var explicitContext = usageContext.RuntimeContextTokens.Value;

      if (
        explicitContext < resolution.RequiredContextTokens
        || explicitContext > resolution.MaximumContextTokens
      )
      {
        throw new OllamaRuntimeProfileException(
          "invalid-runtime-context-override",
          "The request-specific runtime context does not fit the resolved profile.",
          usageContext.RequestPurpose,
          model,
          usageContext.ModelRevision,
          resolution.Role,
          explicitContext,
          null,
          false,
          $"Required={resolution.RequiredContextTokens}; maximum={resolution.MaximumContextTokens}."
        );
      }

      resolution = resolution with
      {
        EffectiveContextTokens = explicitContext,
        Escalated = explicitContext > resolution.TargetContextTokens,
        Reason = $"An explicit bounded runtime context of {explicitContext} tokens was requested."
      };
    }

    return new GenerationPolicy(
      resolution,
      resolution.OutputTokenLimit,
      TimeSpan.FromSeconds(
        settings.Runtime.GenerationTimeoutSeconds
      ),
      ResolveMainGpu(
        settings,
        usageContext
      )
    );
  }

  private static int? ResolveMainGpu(
    ApplicationSettings settings,
    ProviderCallContext usageContext
  )
  {
    var selection = usageContext.ModelRole switch
    {
      UsageModelRoles.Router => settings.RouterGpu,
      UsageModelRoles.Action => settings.ActionGpu,
      UsageModelRoles.Coordinator => settings.CoordinatorGpu,
      _ => usageContext.Gpu ?? settings.DefaultGpu
    };

    return OllamaGpuSelection.Resolve(
      selection,
      settings.DefaultGpu
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

  private async Task RecordUsageAsync(
    ProviderCallContext context,
    string model,
    Stopwatch stopwatch,
    string status,
    ProviderTokenUsage? providerUsage,
    long estimatedInput,
    long estimatedOutput,
    ProviderActivityMetadata? activity = null,
    OllamaRuntimeProfileError? runtimeFailure = null
  )
  {
    await _usageRecorder.RecordAsync(
      new UsageRecordRequest(
        context,
        "ollama-local",
        model,
        stopwatch.ElapsedMilliseconds,
        status,
        providerUsage,
        estimatedInput,
        estimatedOutput,
        ErrorCode: runtimeFailure?.Code,
        Activity: activity,
        ErrorStage: runtimeFailure?.Stage,
        EstimatedInputContextTokens: runtimeFailure?.EstimatedInputTokens,
        ReservedOutputTokens: runtimeFailure?.ReservedOutputTokens,
        RequiredContextTokens: runtimeFailure?.RequiredContextTokens,
        MaximumContextTokens: runtimeFailure?.MaximumContextTokens,
        EffectiveContextTokens: runtimeFailure?.EffectiveContextTokens
      ),
      CancellationToken.None
    );
  }

  private static ProviderTokenUsage? ReadUsage(
    OllamaChatChunk chunk
  )
  {
    return chunk.PromptEvalCount is null
      || chunk.EvalCount is null
        ? null
        : new ProviderTokenUsage(
          chunk.PromptEvalCount.Value,
          chunk.EvalCount.Value
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
    IReadOnlyList<OllamaApiTool>? tools = null,
    IReadOnlyList<ProviderImagePayload>? images = null
  )
  {
    var normalizedMessages = NormalizeSystemMessages(
      messages
    );
    var lastUserIndex = -1;

    for (var index = 0; index < normalizedMessages.Count; index++)
    {
      if (string.Equals(
        normalizedMessages[index].Role,
        "user",
        StringComparison.Ordinal
      ))
      {
        lastUserIndex = index;
      }
    }

    return new OllamaChatRequest(
      model,
      normalizedMessages.Select(
        (
          message,
          index
        ) => new OllamaChatMessage(
          message.Role,
          message.Content,
          Images: index == lastUserIndex && images is not null
            ? images.Select(
              image => Convert.ToBase64String(
                image.Bytes
              )
            ).ToArray()
            : null
        )
      ).ToArray(),
      stream,
      format,
      options,
      keepAlive,
      tools
    );
  }

  private static IReadOnlyList<ChatMessage> NormalizeSystemMessages(
    IReadOnlyList<ChatMessage> messages
  )
  {
    var systemMessages = messages.Where(
      message => string.Equals(
        message.Role,
        "system",
        StringComparison.Ordinal
      )
    ).ToArray();

    if (
      systemMessages.Length == 0
      || systemMessages.Length == 1
        && ReferenceEquals(
          systemMessages[0],
          messages[0]
        )
    )
    {
      return messages;
    }

    return
    [
      new ChatMessage(
        "system",
        string.Join(
          "\n\n",
          systemMessages.Select(
            message => message.Content
          )
        )
      ),
      .. messages.Where(
        message => !string.Equals(
          message.Role,
          "system",
          StringComparison.Ordinal
        )
      )
    ];
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
          message.ToolName,
          Images: message.Images?.Select(
            image => Convert.ToBase64String(image.Bytes)
          ).ToArray()
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

  private sealed record OllamaPullResponse(
    string? Status,
    string? Digest,
    long? Total,
    long? Completed,
    string? Error
  );

  private sealed record OllamaPsResponse(
    IReadOnlyList<OllamaPsModel> Models
  );

  private sealed record OllamaShowRequest(
    string Model
  );

  private sealed record OllamaShowResponse(
    IReadOnlyList<string>? Capabilities,
    [property: JsonPropertyName("model_info")]
    IReadOnlyDictionary<string, JsonElement>? ModelInfo,
    OllamaModelDetails? Details
  );

  private sealed record OllamaModelDetails(
    string? Format,
    string? Family,
    IReadOnlyList<string>? Families,
    [property: JsonPropertyName("parameter_size")] string? ParameterSize,
    [property: JsonPropertyName("quantization_level")] string? QuantizationLevel
  );

  private sealed record OllamaPsModel(
    string? Name,
    string? Digest,
    long? Size,
    [property: JsonPropertyName("size_vram")] long? SizeVram,
    [property: JsonPropertyName("context_length")] int? ContextLength,
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
    double? Temperature,
    int? NumCtx,
    int? NumPredict,
    int? MainGpu
  );

  private sealed record GenerationPolicy(
    OllamaContextResolution Resolution,
    int OutputTokens,
    TimeSpan Timeout,
    int? MainGpu
  );

  private sealed record StreamingToolResponse(
    OllamaToolResponse Response,
    ProviderTokenUsage? Usage
  );

  private sealed record OllamaChatMessage(
    string Role,
    string? Content,
    string? Thinking = null,
    [property: JsonPropertyName("tool_calls")]
    IReadOnlyList<OllamaApiToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_name")]
    string? ToolName = null,
    IReadOnlyList<string>? Images = null
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
    string? Error,
    [property: JsonPropertyName("prompt_eval_count")] long? PromptEvalCount,
    [property: JsonPropertyName("eval_count")] long? EvalCount
  );
}

public sealed record OllamaPullProgress(
  string Status,
  string? Digest,
  long? TotalBytes,
  long? CompletedBytes
);
