using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Providers.Cloud;

namespace AgenticRouter.Api.Knowledge;

public sealed class AnythingLlmKnowledgeProvider : IKnowledgeProvider
{
  private const int MaximumResponseBytes = 2_097_152;
  private static readonly JsonSerializerOptions JsonOptions = new(
    JsonSerializerDefaults.Web
  );

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly ISettingsStore _settingsStore;
  private readonly IProtectedSecretStore _secretStore;

  public AnythingLlmKnowledgeProvider(
    IHttpClientFactory httpClientFactory,
    ISettingsStore settingsStore,
    IProtectedSecretStore secretStore
  )
  {
    _httpClientFactory = httpClientFactory;
    _settingsStore = settingsStore;
    _secretStore = secretStore;
  }

  public KnowledgeProviderDefinition Definition { get; } = new(
    KnowledgeProviderIds.AnythingLlm,
    "AnythingLLM"
  );

  public async ValueTask<KnowledgeProviderAvailability> GetAvailabilityAsync(
    CancellationToken cancellationToken
  )
  {
    AnythingLlmConnection? connection;
    try
    {
      connection = await ResolveConnectionAsync(cancellationToken);
    }
    catch (KnowledgeProviderException exception)
    {
      return new KnowledgeProviderAvailability(
        true,
        false,
        exception.Message
      );
    }
    if (connection is null)
    {
      return new KnowledgeProviderAvailability(
        false,
        false,
        "Configure the AnythingLLM address and API key."
      );
    }

    try
    {
      await ListLibrariesCoreAsync(
        connection,
        cancellationToken
      );
      return new KnowledgeProviderAvailability(
        true,
        true,
        null
      );
    }
    catch (KnowledgeProviderException exception)
    {
      return new KnowledgeProviderAvailability(
        true,
        false,
        exception.Message
      );
    }
  }

  public async Task<IReadOnlyList<KnowledgeLibrary>> ListLibrariesAsync(
    CancellationToken cancellationToken
  )
  {
    return await ListLibrariesCoreAsync(
      await RequireConnectionAsync(cancellationToken),
      cancellationToken
    );
  }

  public async Task<KnowledgeRetrievalResult> RetrieveAsync(
    KnowledgeRetrievalRequest request,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(request.Query))
    {
      throw Error(
        "knowledge-query-invalid",
        "knowledge-retrieval",
        "The knowledge query is empty.",
        false
      );
    }

    var connection = await RequireConnectionAsync(
      cancellationToken
    );
    var libraries = await ListLibrariesCoreAsync(
      connection,
      cancellationToken
    );
    var selected = request.LibraryIds.Distinct(
      StringComparer.Ordinal
    ).Select(
      id => libraries.FirstOrDefault(
        library => string.Equals(
          library.Id,
          id,
          StringComparison.Ordinal
        )
      ) ?? throw Error(
        "knowledge-library-not-found",
        "knowledge-retrieval",
        $"AnythingLLM library '{id}' is not available.",
        false
      )
    ).ToArray();
    var chunks = new List<KnowledgeChunk>();

    foreach (var library in selected)
    {
      using var response = await SendAsync(
        connection,
        HttpMethod.Post,
        $"api/v1/workspace/{Uri.EscapeDataString(library.Id)}/vector-search",
        JsonContent.Create(
          new
          {
            query = request.Query,
            topN = connection.Settings.TopN,
            scoreThreshold = connection.Settings.ScoreThreshold
          },
          options: JsonOptions
        ),
        cancellationToken
      );
      using var document = await ReadDocumentAsync(
        response,
        "knowledge-retrieval",
        cancellationToken
      );

      if (
        !document.RootElement.TryGetProperty("results", out var results)
        || results.ValueKind != JsonValueKind.Array
      )
      {
        throw Error(
          "knowledge-response-invalid",
          "knowledge-retrieval",
          "AnythingLLM returned an invalid vector-search response.",
          false,
          (int)response.StatusCode
        );
      }

      foreach (var result in results.EnumerateArray())
      {
        var text = StringProperty(result, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
          continue;
        }

        var metadata = result.TryGetProperty("metadata", out var value)
          && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
        chunks.Add(
          new KnowledgeChunk(
            library.Id,
            library.Name,
            text,
            StringProperty(metadata, "title"),
            StringProperty(metadata, "url")
              ?? StringProperty(metadata, "chunkSource")
              ?? StringProperty(metadata, "docSource"),
            NumberProperty(result, "score")
          )
        );
      }
    }

    return new KnowledgeRetrievalResult(
      chunks
    );
  }

  private async Task<IReadOnlyList<KnowledgeLibrary>> ListLibrariesCoreAsync(
    AnythingLlmConnection connection,
    CancellationToken cancellationToken
  )
  {
    using var response = await SendAsync(
      connection,
      HttpMethod.Get,
      "api/v1/workspaces",
      null,
      cancellationToken
    );
    using var document = await ReadDocumentAsync(
      response,
      "knowledge-discovery",
      cancellationToken
    );

    if (
      !document.RootElement.TryGetProperty("workspaces", out var workspaces)
      || workspaces.ValueKind != JsonValueKind.Array
    )
    {
      throw Error(
        "knowledge-response-invalid",
        "knowledge-discovery",
        "AnythingLLM returned an invalid workspace list.",
        false,
        (int)response.StatusCode
      );
    }

    return workspaces.EnumerateArray().Select(
      workspace => new KnowledgeLibrary(
        StringProperty(workspace, "slug") ?? string.Empty,
        StringProperty(workspace, "name") ?? string.Empty
      )
    ).Where(
      library => !string.IsNullOrWhiteSpace(library.Id)
        && !string.IsNullOrWhiteSpace(library.Name)
        && library.Id.Length <= 200
        && library.Id.All(character => !char.IsControl(character))
    ).GroupBy(
      library => library.Id,
      StringComparer.Ordinal
    ).Select(
      group => group.First()
    ).OrderBy(
      library => library.Name,
      StringComparer.OrdinalIgnoreCase
    ).ToArray();
  }

  private async Task<HttpResponseMessage> SendAsync(
    AnythingLlmConnection connection,
    HttpMethod method,
    string relativePath,
    HttpContent? content,
    CancellationToken cancellationToken
  )
  {
    using var request = new HttpRequestMessage(
      method,
      new Uri(
        connection.BaseUri,
        relativePath
      )
    )
    {
      Content = content
    };
    request.Headers.Authorization = new AuthenticationHeaderValue(
      "Bearer",
      connection.ApiKey
    );
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
      cancellationToken
    );
    timeout.CancelAfter(
      TimeSpan.FromSeconds(connection.Settings.TimeoutSeconds)
    );

    try
    {
      return await _httpClientFactory.CreateClient().SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        timeout.Token
      );
    }
    catch (OperationCanceledException exception) when (
      !cancellationToken.IsCancellationRequested
    )
    {
      throw Error(
        "knowledge-timeout",
        "knowledge-transport",
        "AnythingLLM did not respond before the configured timeout.",
        true,
        innerException: exception
      );
    }
    catch (HttpRequestException exception)
    {
      throw Error(
        "knowledge-unavailable",
        "knowledge-transport",
        "AnythingLLM could not be reached at the configured address.",
        true,
        innerException: exception
      );
    }
  }

  private async Task<JsonDocument> ReadDocumentAsync(
    HttpResponseMessage response,
    string stage,
    CancellationToken cancellationToken
  )
  {
    if (!response.IsSuccessStatusCode)
    {
      var message = response.StatusCode is HttpStatusCode.Unauthorized
        or HttpStatusCode.Forbidden
          ? "AnythingLLM rejected the configured API key."
          : $"AnythingLLM returned HTTP {(int)response.StatusCode}.";
      throw Error(
        response.StatusCode is HttpStatusCode.Unauthorized
          or HttpStatusCode.Forbidden
            ? "knowledge-authentication-failed"
            : "knowledge-request-failed",
        stage,
        message,
        (int)response.StatusCode >= 500,
        (int)response.StatusCode
      );
    }

    try
    {
      if (response.Content.Headers.ContentLength > MaximumResponseBytes)
      {
        throw Error(
          "knowledge-response-too-large",
          stage,
          "AnythingLLM returned a response larger than the safe limit.",
          false,
          (int)response.StatusCode
        );
      }

      await using var stream = await response.Content.ReadAsStreamAsync(
        cancellationToken
      );
      using var buffer = new MemoryStream();
      var block = new byte[8_192];
      while (true)
      {
        var read = await stream.ReadAsync(block, cancellationToken);
        if (read == 0)
        {
          break;
        }
        if (buffer.Length + read > MaximumResponseBytes)
        {
          throw Error(
            "knowledge-response-too-large",
            stage,
            "AnythingLLM returned a response larger than the safe limit.",
            false,
            (int)response.StatusCode
          );
        }
        await buffer.WriteAsync(
          block.AsMemory(0, read),
          cancellationToken
        );
      }
      buffer.Position = 0;
      return await JsonDocument.ParseAsync(
        buffer,
        cancellationToken: cancellationToken
      );
    }
    catch (JsonException exception)
    {
      throw Error(
        "knowledge-response-invalid",
        stage,
        "AnythingLLM returned malformed JSON.",
        false,
        (int)response.StatusCode,
        exception
      );
    }
  }

  private async Task<AnythingLlmConnection> RequireConnectionAsync(
    CancellationToken cancellationToken
  )
  {
    return await ResolveConnectionAsync(cancellationToken)
      ?? throw Error(
        "knowledge-not-configured",
        "knowledge-configuration",
        "Configure the AnythingLLM address and API key before using knowledge retrieval.",
        false
      );
  }

  private async Task<AnythingLlmConnection?> ResolveConnectionAsync(
    CancellationToken cancellationToken
  )
  {
    var settings = await _settingsStore.GetAsync(cancellationToken);
    var provider = settings.KnowledgeProviders.AnythingLlm;
    string? apiKey;
    try
    {
      apiKey = await _secretStore.GetAsync(
        KnowledgeProviderIds.AnythingLlm,
        provider.SecretReference,
        cancellationToken
      );
    }
    catch (SecretStorageException exception)
    {
      throw Error(
        exception.Code,
        "knowledge-configuration",
        exception.Message,
        exception.Retryable,
        innerException: exception
      );
    }
    if (
      string.IsNullOrWhiteSpace(apiKey)
      || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUri)
    )
    {
      return null;
    }

    var normalized = new UriBuilder(baseUri)
    {
      Path = baseUri.AbsolutePath.TrimEnd('/') + "/",
      Query = string.Empty,
      Fragment = string.Empty
    }.Uri;
    return new AnythingLlmConnection(
      normalized,
      apiKey,
      provider
    );
  }

  private KnowledgeProviderException Error(
    string code,
    string stage,
    string message,
    bool retryable,
    int? httpStatus = null,
    Exception? innerException = null
  )
  {
    return new KnowledgeProviderException(
      code,
      stage,
      Definition.Id,
      message,
      retryable,
      httpStatus,
      innerException
    );
  }

  private static string? StringProperty(
    JsonElement element,
    string name
  )
  {
    return element.ValueKind == JsonValueKind.Object
      && element.TryGetProperty(name, out var value)
      && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
  }

  private static double? NumberProperty(
    JsonElement element,
    string name
  )
  {
    return element.ValueKind == JsonValueKind.Object
      && element.TryGetProperty(name, out var value)
      && value.ValueKind == JsonValueKind.Number
      && value.TryGetDouble(out var number)
        ? number
        : null;
  }

  private sealed record AnythingLlmConnection(
    Uri BaseUri,
    string ApiKey,
    AnythingLlmKnowledgeSettings Settings
  );
}
