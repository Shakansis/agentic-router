using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Providers;

public sealed class ProviderDispatchClient : IOllamaClient
{
  private readonly OllamaClient _ollama;
  private readonly ICloudProviderRegistry _cloudProviders;
  private readonly IUsageRecorder _usageRecorder;
  private readonly ITokenEstimator _tokenEstimator;
  private readonly IOllamaWebSearchService _webSearch;
  private readonly IProviderRetryPolicy _retryPolicy;
  private readonly IProviderHealthMonitor _health;

  public ProviderDispatchClient(
    OllamaClient ollama,
    ICloudProviderRegistry cloudProviders,
    IUsageRecorder usageRecorder,
    ITokenEstimator tokenEstimator,
    IOllamaWebSearchService webSearch,
    IProviderRetryPolicy retryPolicy,
    IProviderHealthMonitor health
  )
  {
    _ollama = ollama;
    _cloudProviders = cloudProviders;
    _usageRecorder = usageRecorder;
    _tokenEstimator = tokenEstimator;
    _webSearch = webSearch;
    _retryPolicy = retryPolicy;
    _health = health;
  }

  public async Task<IReadOnlyList<InstalledModel>> GetModelsAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    var cloud = await _cloudProviders.GetSelectableModelsAsync(
      cancellationToken
    );
    IReadOnlyList<InstalledModel> local;

    try
    {
      local = await _ollama.GetModelsAsync(
        baseUri,
        cancellationToken
      );
      _health.ObserveModelRefresh(
        ModelProviderIds.OllamaLocal,
        true,
        "ollama-api",
        200,
        null
      );
    }
    catch (OllamaProviderException exception)
    {
      _health.ObserveModelRefresh(
        ModelProviderIds.OllamaLocal,
        false,
        "ollama-api",
        exception.HttpStatus,
        exception.Stage
      );
      if (cloud.Count == 0)
      {
        throw;
      }
      local = [];
    }

    return local.Concat(
      cloud
    ).ToArray();
  }

  public Task<string> ClassifyAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    return reference.IsLocal
      ? _ollama.ClassifyAsync(
        baseUri,
        model,
        messages,
        usageContext,
        cancellationToken
      )
      : GenerateCloudStructuredAsync(
        reference,
        messages,
        null,
        "router-classification",
        usageContext,
        cancellationToken
      );
  }

  public Task<string> GenerateJsonAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    return reference.IsLocal
      ? _ollama.GenerateJsonAsync(
        baseUri,
        model,
        messages,
        stage,
        usageContext,
        cancellationToken
      )
      : GenerateCloudStructuredAsync(
        reference,
        messages,
        null,
        stage,
        usageContext,
        cancellationToken
      );
  }

  public Task<string> GenerateTextAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    return reference.IsLocal
      ? _ollama.GenerateTextAsync(
        baseUri,
        model,
        messages,
        stage,
        usageContext,
        cancellationToken
      )
      : GenerateCloudStructuredAsync(
        reference,
        messages,
        null,
        stage,
        usageContext,
        cancellationToken
      );
  }

  public Task<string> GenerateStructuredAsync(
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
    var reference = ProviderModelReference.Parse(
      model
    );

    return reference.IsLocal
      ? _ollama.GenerateStructuredAsync(
        baseUri,
        model,
        messages,
        schema,
        stage,
        usageContext,
        cancellationToken,
        usageObserver,
        options
      )
      : GenerateCloudStructuredAsync(
        reference,
        messages,
        schema,
        stage,
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
    var reference = ProviderModelReference.Parse(
      model
    );

    if (reference.IsLocal)
    {
      return await _ollama.GenerateToolCallAsync(
        baseUri,
        model,
        messages,
        tools,
        stage,
        usageContext,
        cancellationToken,
        onThinkingDelta,
        onContentDelta,
        toolOutput
      );
    }

    var operationStopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateToolMessages(
      messages
    ) + tools.Sum(
      tool => _tokenEstimator.EstimateText(
        tool.Name
      ) + _tokenEstimator.EstimateText(
        tool.Description
      ) + _tokenEstimator.EstimateText(
        tool.Parameters.GetRawText()
      )
    );
    CloudProviderSession? session = null;
    string? retryActivity = null;

    for (var attempt = 1; ; attempt++)
    {
      var attemptStopwatch = Stopwatch.StartNew();
      CloudCallResult<OllamaToolResponse>? result = null;

      try
      {
        session ??= await _cloudProviders.OpenAsync(
          reference.ProviderId,
          cancellationToken
        );
        result = await session.Adapter.GenerateToolCallAsync(
          session.ApiKey,
          reference.ModelId,
          messages,
          tools,
          stage,
          cancellationToken
        );
        await RecordAsync(
          usageContext,
          reference,
          attemptStopwatch,
          UsageStatuses.Success,
          result.Usage,
          estimatedInput,
          _tokenEstimator.EstimateToolResponse(
            result.Value
          ),
          result.RateLimit
        );
        _health.ObserveSuccess(
          reference.ProviderId,
          reference.ModelId,
          attemptStopwatch.Elapsed,
          attemptStopwatch.Elapsed,
          result.Usage,
          result.RateLimit,
          session.Adapter.ProtocolVersion,
          "provider-request"
        );
        var response = result.Value with
        {
          Usage = result.Usage,
          RetryActivity = retryActivity
        };
        if (
          onContentDelta is not null
          && response.ToolCalls.Count == 0
          && !string.IsNullOrEmpty(
            response.Content
          )
        )
        {
          await onContentDelta(
            response.Content,
            cancellationToken
          );
        }
        return response;
      }
      catch (CloudProviderException exception)
      {
        var decision = _retryPolicy.Decide(
          exception,
          attempt,
          operationStopwatch.Elapsed,
          cancellationToken
        );
        await RecordAsync(
          usageContext,
          reference,
          attemptStopwatch,
          cancellationToken.IsCancellationRequested
            ? UsageStatuses.Cancellation
            : UsageStatuses.Failure,
          result?.Usage,
          estimatedInput,
          0,
          exception.RateLimit,
          exception.Code,
          exception.HttpStatus
        );
        _health.ObserveFailure(
          reference.ProviderId,
          reference.ModelId,
          attemptStopwatch.Elapsed,
          exception,
          decision,
          session?.Adapter.ProtocolVersion ?? "provider-resolution",
          "provider-request"
        );

        if (
          !decision.Retry
          || string.Equals(
            stage,
            "chat-read-only-tools",
            StringComparison.Ordinal
          )
            && exception.HttpStatus == (int)HttpStatusCode.TooManyRequests
            && exception.RetryAfter is null
        )
        {
          throw new RoutedProviderException(
            exception
          );
        }

        await Task.Delay(
          decision.Delay,
          cancellationToken
        );
        retryActivity =
          $"Provider retry {decision.Attempt + 1} of {decision.MaximumAttempts}: "
          + $"{decision.Category}; waited {Math.Ceiling(decision.Delay.TotalMilliseconds)} ms.";
      }
      catch (OperationCanceledException)
      {
        await RecordAsync(
          usageContext,
          reference,
          attemptStopwatch,
          UsageStatuses.Cancellation,
          result?.Usage,
          estimatedInput,
          0,
          result?.RateLimit
        );
        throw;
      }
    }
  }

  public async Task<OllamaModelCapabilities> GetModelCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    if (reference.IsLocal)
    {
      return await _ollama.GetModelCapabilitiesAsync(
        baseUri,
        model,
        cancellationToken
      );
    }

    var installed = await _cloudProviders.GetSelectableModelsAsync(
      cancellationToken
    );
    var match = installed.FirstOrDefault(
      candidate => string.Equals(
        candidate.Name,
        model,
        StringComparison.Ordinal
      )
    );

    if (match is null)
    {
      throw new RoutedProviderException(
        new CloudProviderException(
          "model-unavailable",
          "model-capabilities",
          reference.ProviderId,
          reference.ModelId,
          "The cloud model is unavailable or the provider has not been refreshed.",
          404,
          false
        )
      );
    }

    var capabilities = new List<string>();

    if (match.Capabilities?.Chat == true)
    {
      capabilities.Add(
        "chat"
      );
    }

    if (match.Capabilities?.Streaming == true)
    {
      capabilities.Add(
        "streaming"
      );
    }

    if (match.Capabilities?.NativeTools == true)
    {
      capabilities.Add(
        "tools"
      );
    }

    if (match.Capabilities?.Vision == true)
    {
      capabilities.Add(
        "vision"
      );
    }

    return new OllamaModelCapabilities(
      model,
      capabilities,
      match.Capabilities?.NativeTools == true
    );
  }

  public Task<OllamaModelMetadata> GetModelMetadataAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    if (!reference.IsLocal)
    {
      throw new OllamaRuntimeProfileException(
        "model-metadata-unavailable",
        "Runtime profile metadata is available only for Ollama Local models.",
        "model-metadata-inspection",
        model,
        null,
        "unknown",
        null,
        null,
        false,
        "The selected provider is not ollama-local."
      );
    }

    return _ollama.GetModelMetadataAsync(
      baseUri,
      reference.ModelId,
      cancellationToken
    );
  }

  public async Task<ProviderModelCapabilities> GetProviderModelCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    if (reference.IsLocal)
    {
      var capabilities = await _ollama.GetProviderModelCapabilitiesAsync(
        baseUri,
        model,
        cancellationToken
      );
      var applicationWebSearch = capabilities.NativeTools
        && await _webSearch.IsAvailableAsync(
          cancellationToken
        );
      return capabilities with
      {
        WebSearch = applicationWebSearch,
        ApplicationWebSearch = applicationWebSearch,
        Citations = applicationWebSearch
      };
    }

    var installed = await _cloudProviders.GetSelectableModelsAsync(
      cancellationToken
    );
    var match = installed.FirstOrDefault(
      candidate => string.Equals(
        candidate.Name,
        model,
        StringComparison.Ordinal
      )
    );

    if (match?.Capabilities is null)
    {
      throw new CapabilityException(
        "unknown-capability",
        "model-capabilities",
        "The selected model does not expose authoritative capability metadata.",
        "The cached provider model entry has no capability contract.",
        reference.ProviderId,
        reference.ModelId,
        400,
        false
      );
    }

    return match.Capabilities;
  }

  public Task<string> GetVersionAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    return _ollama.GetVersionAsync(
      baseUri,
      cancellationToken
    );
  }

  public async Task<string> GetProtocolVersionAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    if (reference.IsLocal)
    {
      return await _ollama.GetProtocolVersionAsync(
        baseUri,
        model,
        cancellationToken
      );
    }

    var session = await _cloudProviders.OpenAsync(
      reference.ProviderId,
      cancellationToken
    );
    return session.Adapter.ProtocolVersion;
  }

  public Task<IReadOnlyList<OllamaRunningModel>> GetRunningModelsAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    return _ollama.GetRunningModelsAsync(
      baseUri,
      cancellationToken
    );
  }

  public Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    return reference.IsLocal
      ? _ollama.SetModelResidencyAsync(
        baseUri,
        reference.ModelId,
        keepAlive,
        cancellationToken
      )
      : Task.CompletedTask;
  }

  public Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    int? contextTokens,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    return reference.IsLocal
      ? _ollama.SetModelResidencyAsync(
        baseUri,
        reference.ModelId,
        keepAlive,
        contextTokens,
        cancellationToken
      )
      : Task.CompletedTask;
  }

  public Task SetModelResidencyAsync(
    Uri baseUri,
    string model,
    int keepAlive,
    int? contextTokens,
    int? mainGpu,
    CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    return reference.IsLocal
      ? _ollama.SetModelResidencyAsync(
        baseUri,
        reference.ModelId,
        keepAlive,
        contextTokens,
        mainGpu,
        cancellationToken
      )
      : Task.CompletedTask;
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
    var reference = ProviderModelReference.Parse(
      model
    );

    if (reference.IsLocal)
    {
      WebSearchContext? web = null;

      if (options.WebSearchEnabled)
      {
        web = await _webSearch.SearchAsync(
          messages.LastOrDefault(
            message => string.Equals(
              message.Role,
              "user",
              StringComparison.Ordinal
            )
          )?.Content ?? string.Empty,
          usageContext,
          cancellationToken
        );
        messages = messages.Concat(
          [
            new ChatMessage(
              "system",
              web.UntrustedContext
            )
          ]
        ).ToArray();
      }

      await foreach (var update in _ollama.StreamChatAsync(
        baseUri,
        model,
        messages,
        usageContext,
        options,
        cancellationToken
      ))
      {
        yield return update.Done && web is not null
          ? update with
          {
            Citations = web.Citations,
            Activity = web.Activity
          }
          : update;
      }

      yield break;
    }

    var operationStopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateMessages(
      messages
    ) + options.Images.Sum(
      image => Math.Max(1_024L, (long)Math.Ceiling(image.Bytes.LongLength / 512d))
    );
    CloudProviderSession? session = null;

    for (var attempt = 1; ; attempt++)
    {
      var attemptStopwatch = Stopwatch.StartNew();
      var output = new System.Text.StringBuilder();
      ProviderTokenUsage? providerUsage = null;
      ProviderRateLimitSnapshot? rateLimit = null;
      ProviderActivityMetadata? activity = null;
      TimeSpan? timeToFirstChunk = null;
      var emitted = false;
      IAsyncEnumerator<OllamaChatUpdate>? updates = null;
      CloudProviderException? failure = null;
      var completed = false;

      try
      {
        session ??= await _cloudProviders.OpenAsync(
          reference.ProviderId,
          cancellationToken
        );
        updates = session.Adapter.StreamChatAsync(
          session.ApiKey,
          reference.ModelId,
          messages,
          options,
          cancellationToken
        ).GetAsyncEnumerator(
          cancellationToken
        );
      }
      catch (CloudProviderException exception)
      {
        failure = exception;
      }

      while (failure is null && !completed)
      {
        OllamaChatUpdate? update = null;

        try
        {
          if (updates is null || !await updates.MoveNextAsync())
          {
            completed = true;
          }
          else
          {
            update = updates.Current;
          }
        }
        catch (CloudProviderException exception)
        {
          failure = exception;
        }
        catch (OperationCanceledException)
        {
          if (updates is not null)
          {
            await updates.DisposeAsync();
          }

          await RecordAsync(
            usageContext,
            reference,
            attemptStopwatch,
            UsageStatuses.Cancellation,
            providerUsage,
            estimatedInput,
            _tokenEstimator.EstimateText(
              output.ToString()
            ),
            rateLimit
          );
          throw;
        }

        if (update is null)
        {
          continue;
        }

        timeToFirstChunk ??= attemptStopwatch.Elapsed;

        if (!string.IsNullOrEmpty(
          update.Delta
        ))
        {
          output.Append(
            update.Delta
          );
        }

        providerUsage = update.Usage
          ?? providerUsage;
        rateLimit = update.RateLimit
          ?? rateLimit;
        activity = update.Activity
          ?? activity;
        emitted = true;
        yield return update;
      }

      if (updates is not null)
      {
        await updates.DisposeAsync();
      }

      if (failure is not null)
      {
        var evaluated = _retryPolicy.Decide(
          failure,
          attempt,
          operationStopwatch.Elapsed,
          cancellationToken
        );
        var decision = emitted
          ? evaluated with
          {
            Retry = false,
            Delay = TimeSpan.Zero,
            Reason = "A streaming response already emitted content; replay is unsafe."
          }
          : evaluated;
        await RecordFailureAsync(
          usageContext,
          reference,
          attemptStopwatch,
          estimatedInput,
          _tokenEstimator.EstimateText(
            output.ToString()
          ),
          providerUsage,
          failure,
          cancellationToken,
          rateLimit
        );
        _health.ObserveFailure(
          reference.ProviderId,
          reference.ModelId,
          attemptStopwatch.Elapsed,
          failure,
          decision,
          session?.Adapter.ProtocolVersion ?? "provider-resolution",
          "provider-stream"
        );

        if (!decision.Retry)
        {
          throw new RoutedProviderException(
            failure
          );
        }

        yield return new OllamaChatUpdate(
          false,
          null,
          RetryActivity:
          $"Provider retry {decision.Attempt + 1} of {decision.MaximumAttempts}: "
            + $"{decision.Category}; waiting {Math.Ceiling(decision.Delay.TotalMilliseconds)} ms."
        );
        await Task.Delay(
          decision.Delay,
          cancellationToken
        );
        continue;
      }

      await RecordAsync(
        usageContext,
        reference,
        attemptStopwatch,
        UsageStatuses.Success,
        providerUsage,
        estimatedInput,
        _tokenEstimator.EstimateText(
          output.ToString()
        ),
        rateLimit,
        activity: MergeActivity(
          activity,
          options
        )
      );
      _health.ObserveSuccess(
        reference.ProviderId,
        reference.ModelId,
        attemptStopwatch.Elapsed,
        timeToFirstChunk,
        providerUsage,
        rateLimit,
        session!.Adapter.ProtocolVersion,
        "provider-stream"
      );
      yield break;
    }
  }

  private async Task<string> GenerateCloudStructuredAsync(
    ProviderModelReference reference,
    IReadOnlyList<ChatMessage> messages,
    JsonElement? schema,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken,
    Action<ProviderTokenUsage?>? usageObserver = null,
    ProviderChatOptions? options = null
  )
  {
    options ??= ProviderChatOptions.Empty;
    var operationStopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateMessages(
      messages
    );
    CloudProviderSession? session = null;

    for (var attempt = 1; ; attempt++)
    {
      var attemptStopwatch = Stopwatch.StartNew();
      CloudCallResult<string>? result = null;

      try
      {
        session ??= await _cloudProviders.OpenAsync(
          reference.ProviderId,
          cancellationToken
        );
        result = await session.Adapter.GenerateStructuredAsync(
          session.ApiKey,
          reference.ModelId,
          messages,
          schema,
          stage,
          options,
          cancellationToken
        );
        await RecordAsync(
          usageContext,
          reference,
          attemptStopwatch,
          UsageStatuses.Success,
          result.Usage,
          estimatedInput,
          _tokenEstimator.EstimateText(
            result.Value
          ),
          result.RateLimit
        );
        _health.ObserveSuccess(
          reference.ProviderId,
          reference.ModelId,
          attemptStopwatch.Elapsed,
          attemptStopwatch.Elapsed,
          result.Usage,
          result.RateLimit,
          session.Adapter.ProtocolVersion,
          "provider-request"
        );
        usageObserver?.Invoke(
          result.Usage
        );
        return result.Value;
      }
      catch (CloudProviderException exception)
      {
        var decision = _retryPolicy.Decide(
          exception,
          attempt,
          operationStopwatch.Elapsed,
          cancellationToken
        );
        await RecordAsync(
          usageContext,
          reference,
          attemptStopwatch,
          cancellationToken.IsCancellationRequested
            ? UsageStatuses.Cancellation
            : UsageStatuses.Failure,
          result?.Usage,
          estimatedInput,
          0,
          exception.RateLimit,
          exception.Code,
          exception.HttpStatus
        );
        _health.ObserveFailure(
          reference.ProviderId,
          reference.ModelId,
          attemptStopwatch.Elapsed,
          exception,
          decision,
          session?.Adapter.ProtocolVersion ?? "provider-resolution",
          "provider-request"
        );

        if (!decision.Retry)
        {
          throw new RoutedProviderException(
            exception
          );
        }

        await Task.Delay(
          decision.Delay,
          cancellationToken
        );
      }
      catch (OperationCanceledException)
      {
        await RecordAsync(
          usageContext,
          reference,
          attemptStopwatch,
          UsageStatuses.Cancellation,
          result?.Usage,
          estimatedInput,
          0,
          result?.RateLimit
        );
        throw;
      }
    }
  }

  private Task RecordAsync(
    ProviderCallContext context,
    ProviderModelReference reference,
    Stopwatch stopwatch,
    string status,
    ProviderTokenUsage? usage,
    long estimatedInput,
    long estimatedOutput,
    ProviderRateLimitSnapshot? rateLimit,
    string? errorCode = null,
    int? httpStatus = null,
    ProviderActivityMetadata? activity = null
  )
  {
    return _usageRecorder.RecordAsync(
      new UsageRecordRequest(
        context,
        reference.ProviderId,
        reference.ModelId,
        stopwatch.ElapsedMilliseconds,
        status,
        usage,
        estimatedInput,
        estimatedOutput,
        rateLimit,
        errorCode,
        httpStatus,
        activity
      ),
      CancellationToken.None
    );
  }

  private static ProviderActivityMetadata MergeActivity(
    ProviderActivityMetadata? activity,
    ProviderChatOptions options
  )
  {
    return new ProviderActivityMetadata(
      ImageCount: options.Images.Count,
      ImageBytes: options.Images.Sum(
        image => image.Bytes.LongLength
      ),
      SearchQueryCount: activity?.SearchQueryCount ?? 0,
      GroundedRequestCount: activity?.GroundedRequestCount ?? 0,
      CitationCount: activity?.CitationCount ?? 0,
      ProviderSearchCost: activity?.ProviderSearchCost,
      Accuracy: activity?.Accuracy ?? UsageAccuracy.Exact
    );
  }

  private Task RecordFailureAsync(
    ProviderCallContext context,
    ProviderModelReference reference,
    Stopwatch stopwatch,
    long estimatedInput,
    long estimatedOutput,
    ProviderTokenUsage? usage,
    CloudProviderException exception,
    CancellationToken cancellationToken,
    ProviderRateLimitSnapshot? observedRateLimit = null
  )
  {
    return RecordAsync(
      context,
      reference,
      stopwatch,
      cancellationToken.IsCancellationRequested
        ? UsageStatuses.Cancellation
        : UsageStatuses.Failure,
      usage,
      estimatedInput,
      estimatedOutput,
      exception.RateLimit
        ?? observedRateLimit,
      exception.Code,
      exception.HttpStatus
    );
  }
}
