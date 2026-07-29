using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Providers.Ollama;

public sealed class OllamaClient : IOllamaClient
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  private readonly HttpClient _httpClient;

  public OllamaClient(
    HttpClient httpClient
  )
  {
    _httpClient = httpClient;
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
          model.ModifiedAt
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
      "json",
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

  private async Task<string> GenerateAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string stage,
    string? format,
    CancellationToken cancellationToken
  )
  {
    var payload = CreateRequest(
      model,
      messages,
      false,
      format,
      new OllamaOptions(
        0
      ),
      null
    );
    using var response = await SendChatAsync(
      baseUri,
      payload,
      stage,
      cancellationToken
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
    var payload = CreateRequest(
      model,
      [],
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
      cancellationToken
    );
  }

  public async IAsyncEnumerable<OllamaChatUpdate> StreamChatAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var payload = CreateRequest(
      model,
      messages,
      true,
      null,
      null,
      null
    );
    using var response = await SendChatAsync(
      baseUri,
      payload,
      "generation",
      cancellationToken,
      HttpCompletionOption.ResponseHeadersRead
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
    HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead
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
      completionOption
    );
  }

  private async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    string stage,
    CancellationToken cancellationToken,
    HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead
  )
  {
    HttpResponseMessage response;

    try
    {
      response = await _httpClient.SendAsync(
        request,
        completionOption,
        cancellationToken
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
    string? format,
    OllamaOptions? options,
    int? keepAlive
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
      keepAlive
    );
  }

  private sealed record OllamaTagsResponse(
    IReadOnlyList<OllamaModel> Models
  );

  private sealed record OllamaModel(
    string? Name,
    long? Size,
    [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt
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
    string? Format,
    OllamaOptions? Options,
    [property: JsonPropertyName("keep_alive")] int? KeepAlive
  );

  private sealed record OllamaOptions(
    double Temperature
  );

  private sealed record OllamaChatMessage(
    string Role,
    string Content
  );

  private sealed record OllamaChatChunk(
    OllamaChatMessage? Message,
    bool Done,
    string? Error
  );
}
