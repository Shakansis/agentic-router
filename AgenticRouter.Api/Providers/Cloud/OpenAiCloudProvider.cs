using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Providers.Cloud;

public sealed class OpenAiCloudProvider(
  IHttpClientFactory httpClientFactory,
  IConfiguration configuration,
  ISettingsStore settingsStore,
  ILogger<OpenAiCloudProvider> logger
) : OpenAiCompatibleCloudProvider(
  httpClientFactory,
  new Uri(configuration["AgenticRouter:Providers:OpenAiBaseUrl"]
    ?? "https://api.openai.com/v1/", UriKind.Absolute)
)
{
  internal const string GenerationHttpClientName = "OpenAiGeneration";
  internal static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(10);
  private const int MaximumAutomaticRetries = 3;
  private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan PollRequestTimeout = TimeSpan.FromSeconds(15);

  // Explicit official compatibility, never inferred from the /models listing.
  // Keep unknown models visible in discovery, but do not claim unsupported modalities.
  private static readonly string[] ReasoningModels =
  [
    "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna",
    "gpt-5.6", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna",
    "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.2", "gpt-5.1",
    "gpt-5", "gpt-5-mini", "gpt-5-nano", "o3", "o3-mini", "o4-mini", "o1"
  ];
  private static readonly string[] TextModels =
  [
    "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4o", "gpt-4o-mini"
  ];
  private static readonly string[] WebSearchModels =
  [
    "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna",
    "gpt-5.6", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5",
    "gpt-5.4", "gpt-5.4-mini", "gpt-5.2", "gpt-5.1", "gpt-5", "gpt-5-mini",
    "gpt-4.1", "gpt-4.1-mini", "o3", "o4-mini"
  ];

  public override string ProviderId => ModelProviderIds.OpenAi;
  public override string DisplayName => "OpenAI";
  public override string ProtocolVersion => "openai-responses-v2";

  private static bool Matches(string model, IEnumerable<string> names) => names.Any(
    name => model == name || (model.StartsWith(name + "-", StringComparison.Ordinal)
      && Regex.IsMatch(model[name.Length..], @"^-\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)));

  protected override IReadOnlyList<string> SupportedThinkingModes(string? modelId)
  {
    if (modelId is null || !Matches(modelId, ReasoningModels)) return [];
    return ReasoningEfforts(modelId).Contains("none")
      ? ["disabled", "enabled", "low", "medium", "high", "max"]
      : ["enabled", "low", "medium", "high", "max"];
  }

  private static IReadOnlyList<string> ReasoningEfforts(string modelId)
  {
    if (!Matches(modelId, ReasoningModels)) return [];
    if (Matches(modelId, ["gpt-6-astra", "gpt-6.1-sol"])) return ["low", "medium", "high", "xhigh", "max"];
    if (Matches(modelId, ["gpt-6.1-terra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"]))
      return ["none", "low", "medium", "high", "xhigh", "max"];
    if (Matches(modelId, ["gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.2"]))
      return ["none", "low", "medium", "high", "xhigh"];
    if (Matches(modelId, ["gpt-5.1"])) return ["none", "low", "medium", "high"];
    return ["low", "medium", "high"];
  }

  protected override Task<IReadOnlyList<InstalledModel>> EnrichModelsAsync(
    IReadOnlyList<InstalledModel> models, string apiKey, CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult<IReadOnlyList<InstalledModel>>(models.Select(WithCurrentCapabilities).ToArray());
  }

  internal static InstalledModel WithCurrentCapabilities(InstalledModel model)
  {
    var id = ProviderModelReference.Parse(model.Name).ModelId;
    var reasoning = Matches(id, ReasoningModels);
    var supported = reasoning || Matches(id, TextModels);
    var vision = supported && !Matches(id, ["o3-mini"]);
    var webSearch = Matches(id, WebSearchModels);
    return model with
    {
      Selectable = supported,
      Capabilities = new ProviderModelCapabilities(
        supported, supported, supported, vision, webSearch, null,
        supported ? "provider-adapter-official-compatibility" : "provider-model-list-only",
        supported, StructuredOutput: supported, Reasoning: reasoning,
        ProviderNativeWebSearch: webSearch, Citations: webSearch,
        MaximumImageCount: vision ? CapabilityLimits.MaximumImageCount : 0,
        MaximumImageBytes: vision ? CapabilityLimits.MaximumImageBytes : 0,
        SupportedImageMimeTypes: vision ? CapabilityLimits.ImageMimeTypes : [],
        AdapterGenerationParameters: reasoning
          ? ["thinking", "maximumOutputTokens"]
          : ["temperature", "topP", "maximumOutputTokens"],
        ThinkingModes: reasoning
          ? ReasoningEfforts(id).Contains("none")
            ? ["disabled", "enabled", "low", "medium", "high", "max"]
            : ["enabled", "low", "medium", "high", "max"]
          : [],
        NativeWebSearchWithHostTools: webSearch)
    };
  }

  protected override async Task ConfigureRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    var options = (await settingsStore.GetAsync(cancellationToken)).CloudProviders.OpenAiOptions;
    if (!string.IsNullOrWhiteSpace(options.OrganizationId))
      request.Headers.Add("OpenAI-Organization", options.OrganizationId);
    if (!string.IsNullOrWhiteSpace(options.ProjectId))
      request.Headers.Add("OpenAI-Project", options.ProjectId);
  }

  private async Task<JsonObject> CreatePayloadAsync(
    string modelId, object input, ProviderGenerationProfile profile, CancellationToken cancellationToken,
    bool webSearchEnabled = false
  )
  {
    var options = (await settingsStore.GetAsync(cancellationToken)).CloudProviders.OpenAiOptions;
    var payload = new JsonObject
    {
      ["model"] = modelId,
      ["input"] = JsonSerializer.SerializeToNode(input),
      ["store"] = false,
      ["service_tier"] = options.ServiceTier
    };
    var maximumOutput = profile.MaximumOutputTokens is int profileLimit && options.MaximumOutputTokens is int providerLimit
      ? Math.Min(profileLimit, providerLimit) : profile.MaximumOutputTokens ?? options.MaximumOutputTokens;
    if (maximumOutput is not null) payload["max_output_tokens"] = maximumOutput.Value;
    var thinkingModes = SupportedThinkingModes(modelId);
    if (thinkingModes.Count > 0)
    {
      payload["include"] = new JsonArray("reasoning.encrypted_content");
      var thinking = profile.Thinking;
      string? effort = thinking == "auto" ? null : thinking == "enabled" ? "medium"
        : thinking == "disabled" ? "none" : thinking;
      var supportedEfforts = ReasoningEfforts(modelId);
      if (effort == "max") effort = supportedEfforts[^1];
      // Unavailable controls are reported by Host inference evidence; leave provider defaults intact.
      if (effort is not null && supportedEfforts.Contains(effort))
        payload["reasoning"] = new JsonObject { ["effort"] = effort };
    }
    else
    {
      payload["temperature"] = profile.Temperature;
      if (profile.TopP is not null) payload["top_p"] = profile.TopP.Value;
    }
    if (options.Verbosity != "auto" && Matches(modelId, ReasoningModels)
      && !Matches(modelId, ["o1", "o3", "o3-mini", "o4-mini"]))
      payload["text"] = new JsonObject { ["verbosity"] = options.Verbosity };
    if (webSearchEnabled && Matches(modelId, WebSearchModels))
    {
      payload["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" });
      var include = payload["include"] as JsonArray ?? new JsonArray();
      include.Add("web_search_call.action.sources");
      if (payload["include"] is null) payload["include"] = include;
    }
    return payload;
  }

  public override async Task<CloudCallResult<string>> GenerateStructuredAsync(
    string apiKey, string modelId, IReadOnlyList<ChatMessage> messages, JsonElement? schema,
    string stage, ProviderChatOptions? options, CancellationToken cancellationToken
  )
  {
    options ??= ProviderChatOptions.Empty;
    var payload = await CreatePayloadAsync(modelId, ToInput(messages, options.Images), options.EffectiveGenerationProfile, cancellationToken, options.WebSearchEnabled);
    var text = payload["text"] as JsonObject ?? new JsonObject();
    text["format"] = schema is null
      ? new JsonObject { ["type"] = "json_object" }
      : new JsonObject
      {
        ["type"] = "json_schema",
        ["name"] = "agentic_router_response",
        ["strict"] = true,
        ["schema"] = JsonNode.Parse(schema.Value.GetRawText())
      };
    if (payload["text"] is null) payload["text"] = text;
    var result = await GenerateAsync(apiKey, modelId, payload, stage, options, cancellationToken);
    using var document = result.Value;
    var content = ReadText(document.RootElement);
    if (string.IsNullOrEmpty(content)) throw ContractError(stage, modelId, "OpenAI returned no assistant text.");
    return new(content, result.Usage, result.RateLimit, result.Citations, result.Activity);
  }

  public override async Task<CloudCallResult<OllamaToolResponse>> GenerateToolCallAsync(
    string apiKey, string modelId, IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyList<OllamaToolDefinition> tools, string stage,
    ProviderGenerationProfile? generationProfile, CancellationToken cancellationToken,
    ProviderChatOptions? options = null
  )
  {
    var input = new List<object>();
    var callIds = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < messages.Count; index++)
    {
      var message = messages[index];
      if (message.Role == "tool")
      {
        var id = message.ToolCallId ?? (message.ToolName is not null
          && callIds.TryGetValue(message.ToolName, out var matched) ? matched : message.ToolName);
        input.Add(new { type = "function_call_output", call_id = id, output = message.Content ?? string.Empty });
        continue;
      }
      var continuation = message.ToolCalls?.Select(call => call.Continuation)
        .FirstOrDefault(state => state is { Provider: ModelProviderIds.OpenAi } && state.Model == modelId);
      if (continuation is not null)
      {
        // Replay the original output in order, including hosted tools and message phase.
        // Rebuilding assistant text loses the distinction between commentary and final answers.
        input.AddRange(continuation.Items.Cast<object>());
        foreach (var call in message.ToolCalls ?? [])
          if (call.Id is { } callId) callIds[call.Name] = callId;
        continue;
      }
      if (!string.IsNullOrEmpty(message.Content))
        input.Add(new { role = message.Role, content = ToContent(message.Content, message.Images ?? []) });
      foreach (var call in message.ToolCalls ?? [])
      {
        var id = call.Id ?? $"call_{index}_{call.Name}";
        callIds[call.Name] = id;
        input.Add(new { type = "function_call", call_id = id, name = call.Name, arguments = call.Arguments.GetRawText() });
      }
    }
    var payload = await CreatePayloadAsync(modelId, input, generationProfile ?? ProviderGenerationProfiles.Deterministic, cancellationToken, options?.WebSearchEnabled == true);
    if (tools.Count > 0)
    {
      var allTools = payload["tools"] as JsonArray ?? new JsonArray();
      foreach (var tool in tools) allTools.Add(JsonSerializer.SerializeToNode(new
      {
        type = "function",
        name = tool.Name,
        description = tool.Description,
        parameters = tool.Parameters,
        strict = false
      }));
      if (payload["tools"] is null) payload["tools"] = allTools;
    }
    var result = await GenerateAsync(apiKey, modelId, payload, stage, options ?? ProviderChatOptions.Empty, cancellationToken);
    using var document = result.Value;
    var calls = new List<OllamaToolCall>();
    // OpenAI can require the original reasoning item alongside its function call,
    // including when background retrieval returned only the reasoning ID.
    var outputItems = ReadOutput(document.RootElement).Select(item => item.Clone()).ToArray();
    var continuationState = new ProviderToolContinuation(ProviderId, modelId, outputItems);
    foreach (var item in ReadOutput(document.RootElement))
    {
      if (ReadString(item, "type") != "function_call") continue;
      var arguments = ReadString(item, "arguments") ?? "null";
      JsonElement parsed;
      try
      {
        using var argumentsDocument = JsonDocument.Parse(arguments);
        parsed = argumentsDocument.RootElement.Clone();
      }
      catch (JsonException)
      {
        parsed = JsonSerializer.SerializeToElement(arguments);
      }
      calls.Add(new(ReadString(item, "name") ?? string.Empty, parsed, ReadString(item, "call_id"), continuationState));
    }
    var content = ReadText(document.RootElement);
    if (calls.Count == 0 && string.IsNullOrEmpty(content))
      throw ContractError(stage, modelId, "OpenAI returned no assistant text or function calls.");
    return new(new(content, null, calls), result.Usage, result.RateLimit, result.Citations, result.Activity);
  }

  public override async IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    string apiKey, string modelId, IReadOnlyList<ChatMessage> messages, ProviderChatOptions? options,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    options ??= ProviderChatOptions.Empty;
    var payload = await CreatePayloadAsync(modelId, ToInput(messages, options.Images), options.EffectiveGenerationProfile, cancellationToken, options.WebSearchEnabled);
    payload["stream"] = true;
    using var request = CreateJsonRequest("responses", apiKey, payload);
    using var response = await SendAsync(request, "generation", modelId, cancellationToken,
      HttpCompletionOption.ResponseHeadersRead, GenerationHttpClientName, retryAmbiguousFailure: false);
    yield return new(true, null);
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
    while (await reader.ReadLineAsync(cancellationToken) is { } line)
    {
      if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
      var data = line[5..].Trim();
      if (data.Length == 0 || data == "[DONE]") continue;
      using var document = ParseEvent(data, modelId);
      var root = document.RootElement;
      var type = ReadString(root, "type");
      if (type is "response.output_text.delta" or "response.refusal.delta")
        yield return new(false, ReadString(root, "delta"));
      else if (type == "response.completed")
      {
        if (!root.TryGetProperty("response", out var completed))
          throw ContractError("generation", modelId, "OpenAI returned an invalid completion event.");
        RequireCompleted(completed, "generation", modelId);
        var evidence = ReadEvidence(completed);
        yield return new(false, null, Done: true, Usage: ProviderUsageMapper.FromOpenAiResponses(completed), RateLimit: ReadRateLimit(response),
          Citations: evidence.Citations, Activity: evidence.Activity);
        yield break;
      }
      else if (type is "error" or "response.failed" or "response.incomplete")
        throw new CloudProviderException(type == "response.incomplete" ? "provider-response-incomplete" : "provider-request-failed",
          "generation", ProviderId, modelId, type == "response.incomplete"
            ? "OpenAI stopped before completing the response. Check the output limit."
            : "OpenAI could not complete the response.", (int)HttpStatusCode.OK, type != "response.incomplete");
    }
    throw new CloudProviderException("provider-disconnected", "generation", ProviderId, modelId,
      "OpenAI closed the stream without a completed response.", (int)HttpStatusCode.OK, true);
  }

  private async Task<CloudCallResult<JsonDocument>> GenerateAsync(
    string apiKey, string modelId, JsonObject payload, string stage, ProviderChatOptions options,
    CancellationToken cancellationToken
  )
  {
    payload["background"] = true;
    // OpenAI treats background responses as persisted during the polling window.
    if (payload["include"] is JsonArray requestedIncludes)
    {
      var encryptedReasoning = requestedIncludes.FirstOrDefault(value => value?.GetValue<string>() == "reasoning.encrypted_content");
      if (encryptedReasoning is not null) requestedIncludes.Remove(encryptedReasoning);
      if (requestedIncludes.Count == 0) payload.Remove("include");
    }
    var includeQuery = payload["include"] is JsonArray include
      ? "?" + string.Join("&", include.Select(value => "include%5B%5D=" + Uri.EscapeDataString(value!.GetValue<string>())))
      : string.Empty;
    string? responsePath = null;
    string? lastStatus = null;
    ProviderRateLimitSnapshot? generationRateLimit = null;

    try
    {
      while (true)
      {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(GenerationTimeout);
        CloudProviderException failure;
        try
        {
          return await WaitForResponseAsync(deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
          failure = new("provider-timeout", stage, ProviderId, modelId,
            "OpenAI did not return a completed response within 10 minutes.", 504, false, innerException: exception);
        }
        catch (CloudProviderException exception) when (IsTemporaryResponseFailure(exception)
          || exception.Code == "provider-background-failed"
          || (responsePath is not null && exception.HttpStatus is 404 or 410))
        {
          var unavailable = exception.HttpStatus is 404 or 410;
          failure = new(unavailable ? "provider-response-unavailable" : exception.Code, stage, ProviderId, modelId,
            unavailable ? "OpenAI no longer has the background response available." : exception.Message,
            exception.HttpStatus, false, generationRateLimit ?? exception.RateLimit, exception);
          if (unavailable || exception.Code == "provider-background-failed")
            responsePath = null;
        }

        if (options.RequestProviderRetry is null)
          throw failure;
        if (!await options.RequestProviderRetry(responsePath is not null, cancellationToken))
        {
          await CancelResponseAsync();
          throw new CloudProviderException("provider-recovery-stopped", stage, ProviderId, modelId,
            "OpenAI response recovery was stopped by the user.", 409, false, innerException: failure);
        }
        lastStatus = null;
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      await CancelResponseAsync();
      throw;
    }

    async Task<CloudCallResult<JsonDocument>> WaitForResponseAsync(CancellationToken roundToken)
    {
      var retries = 0;
      while (true)
      {
        var retrieving = responsePath is not null;
        using var request = retrieving
          ? CreateRequest(HttpMethod.Get, responsePath + includeQuery, apiKey)
          : CreateJsonRequest("responses", apiKey, payload);
        HttpResponseMessage response;
        try
        {
          response = await SendResponseAsync(request, retrieving, roundToken);
        }
        catch (CloudProviderException exception) when (retries < MaximumAutomaticRetries
          && (retrieving ? IsTemporaryResponseFailure(exception) : exception.HttpStatus == 429))
        {
          retries++;
          options.ProviderProgress?.Invoke(new("provider-retrying", null, "openai-responses",
            $"OpenAI {(retrieving ? "retrieval" : "submission")} retry {retries} of {MaximumAutomaticRetries}."));
          lastStatus = null;
          var retryDelay = TimeSpan.FromMilliseconds(250 * retries);
          if (exception.RetryAfter is { } retryAfter && retryAfter > retryDelay)
            retryDelay = retryAfter > GenerationTimeout ? GenerationTimeout : retryAfter;
          await Task.Delay(retryDelay, roundToken);
          continue;
        }

        using (response)
        {
          if (!retrieving)
            generationRateLimit = ReadRateLimit(response);
          var document = await ReadDocumentAsync(response, stage, modelId, roundToken);
          try
          {
            var root = document.RootElement;
            var status = ReadString(root, "status");
            var id = ReadString(root, "id");
            if (retrieving && "responses/" + id != responsePath)
              throw ContractError(stage, modelId, "OpenAI returned a different response ID during retrieval.");
            if (status is not ("queued" or "in_progress"))
            {
              if (status is "failed" or "cancelled")
                throw new CloudProviderException("provider-background-failed", stage, ProviderId, modelId,
                  "OpenAI ended the background response before completion.", 200, false);
              RequireCompleted(root, stage, modelId);
              var evidence = ReadEvidence(root);
              return new(document, ProviderUsageMapper.FromOpenAiResponses(root),
                generationRateLimit ?? ReadRateLimit(response), evidence.Citations, evidence.Activity);
            }

            if (id is null || !id.StartsWith("resp_", StringComparison.Ordinal) || id.Length <= 5
              || !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
              throw ContractError(stage, modelId, "OpenAI returned no valid ID for the pending response.");
            responsePath = "responses/" + id;
            if (lastStatus != status)
            {
              lastStatus = status;
              options.ProviderProgress?.Invoke(new(status == "queued" ? "provider-queued" : "provider-in-progress",
                null, "openai-responses"));
            }
          }
          catch
          {
            document.Dispose();
            throw;
          }
          document.Dispose();
        }

        await Task.Delay(PollInterval, roundToken);
      }
    }

    async Task<HttpResponseMessage> SendResponseAsync(HttpRequestMessage request, bool retrieving, CancellationToken roundToken)
    {
      using var pollTimeout = retrieving ? CancellationTokenSource.CreateLinkedTokenSource(roundToken) : null;
      pollTimeout?.CancelAfter(PollRequestTimeout);
      try
      {
        return await SendAsync(request, stage, modelId, pollTimeout?.Token ?? roundToken,
          clientName: GenerationHttpClientName, retryAmbiguousFailure: false);
      }
      catch (OperationCanceledException exception) when (!roundToken.IsCancellationRequested && pollTimeout?.IsCancellationRequested == true)
      {
        throw new CloudProviderException("provider-timeout", stage, ProviderId, modelId,
          "OpenAI did not respond to the status request in time.", 504, false, innerException: exception);
      }
    }

    async Task CancelResponseAsync()
    {
      if (responsePath is null) return;
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
      using var request = CreateRequest(HttpMethod.Post, responsePath + "/cancel", apiKey);
      try
      {
        using var response = await SendAsync(request, stage, modelId, timeout.Token,
          clientName: GenerationHttpClientName, retryAmbiguousFailure: false);
      }
      catch (Exception exception) when (exception is CloudProviderException or OperationCanceledException)
      {
        logger.LogDebug("OpenAI background cancellation could not be confirmed ({Failure}).",
          (exception as CloudProviderException)?.Code ?? "timeout");
      }
    }
  }

  private static bool IsTemporaryResponseFailure(CloudProviderException exception) =>
    exception.Code is "provider-disconnected" or "provider-timeout"
    || exception.HttpStatus is 408 or 429 or >= 500 and <= 599;

  private static IReadOnlyList<object> ToInput(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ProviderImagePayload> images)
  {
    var lastUser = messages.Select((message, index) => (message, index)).LastOrDefault(pair => pair.message.Role == "user").index;
    return messages.Select((message, index) => (object)new
    {
      role = message.Role,
      content = ToContent(message.Content, index == lastUser ? images : [])
    }).ToArray();
  }

  private static object ToContent(string? text, IReadOnlyList<ProviderImagePayload> images)
  {
    if (images.Count == 0) return text ?? string.Empty;
    var content = new List<object> { new { type = "input_text", text = text ?? string.Empty } };
    content.AddRange(images.Select(image => (object)new
    {
      type = "input_image",
      image_url = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}"
    }));
    return content;
  }

  private static IEnumerable<JsonElement> ReadOutput(JsonElement root) =>
    root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array
      ? output.EnumerateArray() : [];

  private static (IReadOnlyList<ProviderCitation> Citations, ProviderActivityMetadata Activity) ReadEvidence(JsonElement root)
  {
    var citations = new Dictionary<string, ProviderCitation>(StringComparer.Ordinal);
    var calls = new HashSet<string>(StringComparer.Ordinal);
    var queryCount = 0;
    var queriesKnown = true;
    foreach (var item in ReadOutput(root))
    {
      if (ReadString(item, "type") == "web_search_call")
      {
        if (!calls.Add(ReadString(item, "id") ?? $"anonymous_{calls.Count}")) continue;
        if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object && ReadString(action, "type") == "search")
        {
          if (action.TryGetProperty("queries", out var queries) && queries.ValueKind == JsonValueKind.Array)
            queryCount += queries.EnumerateArray().Count(query => query.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(query.GetString()));
          else if (!string.IsNullOrWhiteSpace(ReadString(action, "query"))) queryCount++;
          else queriesKnown = false;
        }
      }
      if (ReadString(item, "type") != "message" || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
      foreach (var part in content.EnumerateArray())
      {
        if (!part.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array) continue;
        foreach (var annotation in annotations.EnumerateArray())
        {
          if (ReadString(annotation, "type") != "url_citation") continue;
          var url = ReadString(annotation, "url");
          if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new CapabilityException("invalid-citation", "provider-citations", "The provider returned an unsafe citation URL.",
              "Provider citations must use absolute HTTPS URLs.", ModelProviderIds.OpenAi, null);
          citations.TryAdd(uri.AbsoluteUri, new ProviderCitation($"openai-{citations.Count + 1}", ReadString(annotation, "title") ?? uri.Host, uri.AbsoluteUri));
        }
      }
    }
    return (citations.Values.ToArray(), new ProviderActivityMetadata(SearchQueryCount: queryCount,
      GroundedRequestCount: calls.Count > 0 ? 1 : 0, CitationCount: citations.Count,
      Accuracy: queriesKnown ? UsageAccuracy.Exact : UsageAccuracy.Unavailable,
      HostedWebSearchCallCount: calls.Count));
  }

  private static string ReadText(JsonElement root) => string.Concat(ReadOutput(root)
    .Where(item => ReadString(item, "type") == "message" && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
    .SelectMany(item => item.GetProperty("content").EnumerateArray())
    .Select(part => ReadString(part, "type") == "output_text" ? ReadString(part, "text")
      : ReadString(part, "type") == "refusal" ? ReadString(part, "refusal") : null));

  private void RequireCompleted(JsonElement root, string stage, string modelId)
  {
    if (ReadString(root, "status") == "incomplete")
      throw new CloudProviderException("provider-response-incomplete", stage, ProviderId, modelId,
        "OpenAI stopped before completing the response. Check the output limit.", (int)HttpStatusCode.OK, false);
    if (ReadString(root, "status") != "completed")
      throw ContractError(stage, modelId, "OpenAI did not complete the response. Check the output limit and provider status.");
  }

  private JsonDocument ParseEvent(string data, string modelId)
  {
    try { return JsonDocument.Parse(data); }
    catch (JsonException exception)
    {
      throw new CloudProviderException("provider-contract-invalid", "generation", ProviderId, modelId,
        "OpenAI returned invalid streaming JSON.", (int)HttpStatusCode.OK, false, innerException: exception);
    }
  }

  private CloudProviderException ContractError(string stage, string modelId, string message) =>
    new("provider-contract-invalid", stage, ProviderId, modelId, message, (int)HttpStatusCode.OK, false);
}
