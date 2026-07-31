using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Cloud;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Providers;

public sealed class ProviderDispatchClient : IOllamaClient
{
  private readonly OllamaClient _ollama;
  private readonly ICloudProviderRegistry _cloudProviders;
  private readonly IUsageRecorder _usageRecorder;
  private readonly ITokenEstimator _tokenEstimator;

  public ProviderDispatchClient(
    OllamaClient ollama,
    ICloudProviderRegistry cloudProviders,
    IUsageRecorder usageRecorder,
    ITokenEstimator tokenEstimator
  )
  {
    _ollama = ollama;
    _cloudProviders = cloudProviders;
    _usageRecorder = usageRecorder;
    _tokenEstimator = tokenEstimator;
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
    }
    catch (OllamaProviderException) when (cloud.Count > 0)
    {
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
    CancellationToken cancellationToken
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
        cancellationToken
      )
      : GenerateCloudStructuredAsync(
        reference,
        messages,
        schema,
        stage,
        usageContext,
        cancellationToken
      );
  }

  public async Task<OllamaToolResponse> GenerateToolCallAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<OllamaToolMessage> messages,
    IReadOnlyList<OllamaToolDefinition> tools,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
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
        cancellationToken
      );
    }

    var stopwatch = Stopwatch.StartNew();
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
    CloudCallResult<OllamaToolResponse>? result = null;

    try
    {
      var session = await _cloudProviders.OpenAsync(
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
        stopwatch,
        UsageStatuses.Success,
        result.Usage,
        estimatedInput,
        _tokenEstimator.EstimateToolResponse(
          result.Value
        ),
        result.RateLimit
      );
      return result.Value;
    }
    catch (CloudProviderException exception)
    {
      await RecordAsync(
        usageContext,
        reference,
        stopwatch,
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
      throw new RoutedProviderException(
        exception
      );
    }
    catch (OperationCanceledException)
    {
      await RecordAsync(
        usageContext,
        reference,
        stopwatch,
        UsageStatuses.Cancellation,
        result?.Usage,
        estimatedInput,
        0,
        result?.RateLimit
      );
      throw;
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
        model,
        keepAlive,
        cancellationToken
      )
      : Task.CompletedTask;
  }

  public async IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    ProviderCallContext usageContext,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    if (reference.IsLocal)
    {
      await foreach (var update in _ollama.StreamChatAsync(
        baseUri,
        model,
        messages,
        usageContext,
        cancellationToken
      ))
      {
        yield return update;
      }

      yield break;
    }

    var stopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateMessages(
      messages
    );
    var output = new System.Text.StringBuilder();
    ProviderTokenUsage? providerUsage = null;
    ProviderRateLimitSnapshot? rateLimit = null;

    CloudProviderSession session;

    try
    {
      session = await _cloudProviders.OpenAsync(
        reference.ProviderId,
        cancellationToken
      );
    }
    catch (CloudProviderException exception)
    {
      await RecordFailureAsync(
        usageContext,
        reference,
        stopwatch,
        estimatedInput,
        0,
        null,
        exception,
        cancellationToken
      );
      throw new RoutedProviderException(
        exception
      );
    }
    catch (OperationCanceledException)
    {
      await RecordAsync(
        usageContext,
        reference,
        stopwatch,
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

    await using var updates = session.Adapter.StreamChatAsync(
      session.ApiKey,
      reference.ModelId,
      messages,
      cancellationToken
    ).GetAsyncEnumerator(
      cancellationToken
    );

    while (true)
    {
      OllamaChatUpdate update;

      try
      {
        if (!await updates.MoveNextAsync())
        {
          break;
        }

        update = updates.Current;
      }
      catch (CloudProviderException exception)
      {
        await RecordFailureAsync(
          usageContext,
          reference,
          stopwatch,
          estimatedInput,
          _tokenEstimator.EstimateText(
            output.ToString()
          ),
          providerUsage,
          exception,
          cancellationToken,
          rateLimit
        );
        throw new RoutedProviderException(
          exception
        );
      }
      catch (OperationCanceledException)
      {
        await RecordAsync(
          usageContext,
          reference,
          stopwatch,
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
      yield return update;
    }

    await RecordAsync(
      usageContext,
      reference,
      stopwatch,
      UsageStatuses.Success,
      providerUsage,
      estimatedInput,
      _tokenEstimator.EstimateText(
        output.ToString()
      ),
      rateLimit
    );
  }

  private async Task<string> GenerateCloudStructuredAsync(
    ProviderModelReference reference,
    IReadOnlyList<ChatMessage> messages,
    JsonElement? schema,
    string stage,
    ProviderCallContext usageContext,
    CancellationToken cancellationToken
  )
  {
    var stopwatch = Stopwatch.StartNew();
    var estimatedInput = _tokenEstimator.EstimateMessages(
      messages
    );
    CloudCallResult<string>? result = null;

    try
    {
      var session = await _cloudProviders.OpenAsync(
        reference.ProviderId,
        cancellationToken
      );
      result = await session.Adapter.GenerateStructuredAsync(
        session.ApiKey,
        reference.ModelId,
        messages,
        schema,
        stage,
        cancellationToken
      );
      await RecordAsync(
        usageContext,
        reference,
        stopwatch,
        UsageStatuses.Success,
        result.Usage,
        estimatedInput,
        _tokenEstimator.EstimateText(
          result.Value
        ),
        result.RateLimit
      );
      return result.Value;
    }
    catch (CloudProviderException exception)
    {
      await RecordAsync(
        usageContext,
        reference,
        stopwatch,
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
      throw new RoutedProviderException(
        exception
      );
    }
    catch (OperationCanceledException)
    {
      await RecordAsync(
        usageContext,
        reference,
        stopwatch,
        UsageStatuses.Cancellation,
        result?.Usage,
        estimatedInput,
        0,
        result?.RateLimit
      );
      throw;
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
    int? httpStatus = null
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
        httpStatus
      ),
      CancellationToken.None
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
