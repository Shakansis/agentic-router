using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;

namespace AgenticRouter.Api.Execution;

// Local inference transport. The Host supplies the Execute output budget; payloads are never persisted.
public sealed class HarnessInferenceObserver(
  IHttpClientFactory clients,
  ILogger<HarnessInferenceObserver> logger,
  IOllamaManagedServerManager managedServers,
  HostFileReadPolicy fileReads
) : IAsyncDisposable
{
  public const string HttpClientName = nameof(HarnessInferenceObserver);
  private readonly ConcurrentDictionary<string, Client> _clients = new(StringComparer.Ordinal);
  private readonly SemaphoreSlim _startGate = new(1, 1);
  private readonly CancellationTokenSource _lifetime = new();
  private HttpListener? _listener;
  private Task? _listen;
  private Uri? _baseUri;

  public async Task<Turn> BeginAsync(string harness, Uri upstream, ExecutionSession session,
    string turnKey, CancellationToken cancellationToken, int? outputTokenLimit = null,
    Action<InferenceProgressView>? progressObserver = null, OllamaContextResolution? runtime = null, bool disableThinking = false)
  {
    await _startGate.WaitAsync(cancellationToken);
    try
    {
      if (_listener is null)
      {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        _baseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUri.AbsoluteUri);
        _listener.Start();
        _listen = ListenAsync();
      }
    }
    finally { _startGate.Release(); }
    var client = _clients.GetOrAdd(harness, _ => new Client());
    await client.Gate.WaitAsync(cancellationToken);
    var turn = new Turn(new Uri(_baseUri!, client.Key + "/"), upstream, session, turnKey,
      CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token),
      () => { client.Active = null; client.Gate.Release(); }, outputTokenLimit, progressObserver)
    { IsCodex = harness == HarnessIds.Codex, Runtime = runtime, DisableThinking = disableThinking };
    client.Active = turn;
    return turn;
  }

  public static string V1Endpoint(Uri endpoint) => endpoint.AbsoluteUri.TrimEnd('/') + "/v1";

  private async Task ListenAsync()
  {
    try
    {
      while (!_lifetime.IsCancellationRequested)
      {
        var context = await _listener!.GetContextAsync().WaitAsync(_lifetime.Token);
        _ = ForwardAsync(context);
      }
    }
    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    catch (HttpListenerException) when (_lifetime.IsCancellationRequested) { }
    catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
  }

  private async Task ForwardAsync(HttpListenerContext context)
  {
    Observation? observation = null;
    Turn? turn = null;
    string? responseContentType = null;
    string? inferencePath = null;
    int? responseStatus = null;
    try
    {
      var path = context.Request.Url!.AbsolutePath.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
      turn = path.Length >= 2 ? _clients.Values.FirstOrDefault(client => client.Key == path[0])?.Active : null;
      if (turn is null)
      {
        context.Response.StatusCode = 404;
        return;
      }
      var relative = context.Request.Url.PathAndQuery[(path[0].Length + 1)..];
      var cancellationToken = turn.Cancellation;
      var target = new Uri(turn.Upstream.GetLeftPart(UriPartial.Authority) + relative);
      inferencePath = relative.Split('?')[0];
      var inference = context.Request.HttpMethod == "POST"
        && inferencePath is "/v1/responses" or "/v1/chat/completions" or "/v1/messages" or "/api/chat" or "/api/generate";
      using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), target);
      CodexApplyPatchWireAdapter? patchWire = null;
      OllamaHarnessWireAdapter? nativeWire = null;
      if (context.Request.HasEntityBody)
      {
        if (inference && (turn.OutputTokenLimit is not null || turn.IsCodex && inferencePath == "/v1/responses"))
        {
          if (await JsonNode.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken) is not JsonObject body)
            throw new JsonException("The harness inference request must be a JSON object.");
          if (turn.OutputTokenLimit is int outputLimit && inferencePath is "/api/chat" or "/api/generate")
          {
            var options = body["options"] as JsonObject ?? new JsonObject();
            options["num_predict"] = outputLimit;
            body["options"] = options;
          }
          else if (turn.OutputTokenLimit is int compatibleOutputLimit)
          {
            var outputField = inferencePath == "/v1/responses" ? "max_output_tokens"
              : inferencePath == "/v1/chat/completions" && body.ContainsKey("max_completion_tokens")
                ? "max_completion_tokens" : "max_tokens";
            body.Remove("max_tokens");
            body.Remove("max_completion_tokens");
            body.Remove("max_output_tokens");
            body[outputField] = compatibleOutputLimit;
          }
          if (turn.IsCodex && inferencePath == "/v1/responses")
            patchWire = CodexApplyPatchWireAdapter.AdaptRequest(body);
          if (turn.Runtime is not null && (turn.Runtime.Performance?.HasRequestOptions == true || turn.DisableThinking) && inferencePath is "/v1/messages" or "/v1/chat/completions" or "/v1/responses")
          {
            nativeWire = new OllamaHarnessWireAdapter(inferencePath, body, patchWire);
            body = nativeWire.Request(turn.Runtime, turn.DisableThinking);
            request.RequestUri = new Uri(turn.Upstream.GetLeftPart(UriPartial.Authority) + "/api/chat");
          }
          if (nativeWire is null && inferencePath == "/v1/responses")
            OllamaHarnessWireAdapter.PreserveResponsesReasoningOrder(body);
          if (turn.Runtime is not null && turn.OutputTokenLimit is int ceiling)
          {
            var inputTokens = fileReads.MeasureRequest(body);
            var fitted = Math.Min(ceiling, ExecutionProgressPolicy.OutputTokenLimit(turn.Runtime.EffectiveContextTokens, inputTokens));
            if (nativeWire is not null || inferencePath is "/api/chat" or "/api/generate")
              ((JsonObject)body["options"]!)["num_predict"] = fitted;
            else
            {
              var field = inferencePath == "/v1/responses" ? "max_output_tokens"
                : body.ContainsKey("max_completion_tokens") ? "max_completion_tokens" : "max_tokens";
              body[field] = fitted;
            }
            if (fitted < ceiling)
              logger.LogInformation("Harness output budget {ExecutionSessionId}/{TurnKey}: estimated input {InputTokens}, context {ContextTokens}, generation ceiling {Ceiling}, applied output {OutputTokens}.",
                turn.SessionId, turn.Key, inputTokens, turn.Runtime.EffectiveContextTokens, ceiling, fitted);
          }
          request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        }
        else request.Content = new StreamContent(context.Request.InputStream);
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
          context.Request.ContentType ?? "application/json");
      }
      foreach (var name in new[] { "Accept", "Authorization", "x-api-key", "anthropic-version", "anthropic-beta" })
        if (context.Request.Headers[name] is string value)
          request.Headers.TryAddWithoutValidation(name, value);
      using var http = clients.CreateClient(HttpClientName);
      using var progress = inference ? managedServers.ObserveInference(turn.Upstream, turn.ProgressObserver) : null;
      if (inference) observation = turn.Dispatch();
      if (inference && observation is null)
      {
        context.Response.StatusCode = 410;
        return;
      }
      if (observation is not null) observation.Progress = progress;
      using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      responseStatus = (int)response.StatusCode;
      if (observation is not null && !response.IsSuccessStatusCode)
        observation.ResponseFailed(responseStatus.Value);
      responseContentType = response.Content.Headers.ContentType?.MediaType;
      context.Response.StatusCode = (int)response.StatusCode;
      context.Response.ContentType = nativeWire is not null && response.IsSuccessStatusCode
        ? nativeWire.Streaming ? "text/event-stream" : "application/json"
        : response.Content.Headers.ContentType?.ToString();
      context.Response.SendChunked = true;
      await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
      if (nativeWire is not null && response.IsSuccessStatusCode)
      {
        await nativeWire.ForwardAsync(input, context.Response.OutputStream, bytes =>
        {
          observation?.ReadFrame(bytes, observation.ElapsedMilliseconds, nativeWire.Streaming);
          turn.Publish();
        }, cancellationToken);
        return;
      }
      if (patchWire is not null && response.IsSuccessStatusCode)
      {
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mediaType == "application/json")
        {
          context.Response.ContentType = patchWire.StreamRequested ? "text/event-stream" : "application/json";
          await patchWire.ForwardJsonAsync(input, context.Response.OutputStream, bytes =>
          {
            observation?.ReadFrame(bytes, observation.ElapsedMilliseconds, false);
            turn.Publish();
          }, cancellationToken);
          return;
        }
        // Compatibility servers can commit an incorrect or absent media type
        // before their first Responses frame. Validate the actual SSE framing.
        context.Response.ContentType = "text/event-stream";
        await patchWire.ForwardAsync(input, context.Response.OutputStream, bytes =>
        {
          observation?.ReadFrame(bytes, observation.ElapsedMilliseconds, true);
          turn.Publish();
        }, cancellationToken);
        return;
      }
      var sse = response.Content.Headers.ContentType?.MediaType is "text/event-stream" or "application/x-ndjson";
      var buffer = new byte[16_384];
      // Observation is bounded; forwarding is not truncated when a telemetry frame is large.
      using var frame = new MemoryStream();
      var skipped = false;
      int count;
      while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
      {
        if (observation is not null)
        {
          var receivedAt = observation.ElapsedMilliseconds;
          for (var index = 0; index < count; index++)
          {
            var value = buffer[index];
            if (sse && value == (byte)'\n')
            {
              if (!skipped) observation.ReadFrame(frame.GetBuffer().AsSpan(0, (int)frame.Length), receivedAt, true);
              frame.SetLength(0);
              skipped = false;
            }
            else if (!skipped)
            {
              if (frame.Length == 2_097_152) { skipped = true; observation.Incomplete = true; frame.SetLength(0); }
              else frame.WriteByte(value);
            }
          }
          turn.Publish();
        }
        await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        await context.Response.OutputStream.FlushAsync(cancellationToken);
      }
      if (observation is not null && !skipped && frame.Length > 0)
        observation.ReadFrame(frame.GetBuffer().AsSpan(0, (int)frame.Length), observation.ElapsedMilliseconds, sse);
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException or HttpListenerException or OperationCanceledException or JsonException or InvalidOperationException)
    {
      if (observation is not null && !observation.Complete) observation.Incomplete = true;
      if (observation is not null && !observation.Complete)
        observation.Failure = exception is OperationCanceledException ? "Inference cancelled before final usage."
          : "Inference transport ended before final usage.";
      logger.LogWarning(exception, "Harness inference transport ended with {ErrorType}; response Content-Type {ResponseContentType}.",
        exception.GetType().Name, responseContentType is { Length: > 128 } ? responseContentType[..128] : responseContentType ?? "missing");
      if (observation is null && turn is not null && exception is JsonException or InvalidOperationException)
      {
        try
        {
          context.Response.StatusCode = 400;
          context.Response.ContentType = "application/json";
          var error = JsonSerializer.SerializeToUtf8Bytes(new
          {
            type = "error",
            error = new
            {
              type = "invalid_request_error",
              code = "ollama-native-translation-invalid",
              message = "The harness request uses an unsupported or malformed native Ollama protocol shape."
            }
          });
          await context.Response.OutputStream.WriteAsync(error, CancellationToken.None);
        }
        catch (Exception writeFailure) when (writeFailure is HttpListenerException or ObjectDisposedException or IOException)
        { context.Response.Abort(); }
      }
      else context.Response.Abort();
    }
    finally
    {
      turn?.Publish();
      if (observation is not null)
      {
        var metrics = observation.Metrics;
        logger.LogInformation("Harness inference {ExecutionSessionId}/{TurnKey} {InferencePath}: HTTP {Status}, rejected {Rejected}, output tokens {OutputTokens}, generation ms {GenerationMilliseconds}, source {TimingSource}, diagnostic {Diagnostic}.",
          turn!.SessionId, turn.Key, inferencePath, responseStatus, observation.Rejected,
          metrics.OutputTokens, metrics.GenerationMilliseconds, metrics.GenerationTimingSource,
          metrics.TelemetryError ?? (metrics.OutputTokens is null ? "Provider final usage missing." : "none"));
      }
      try { context.Response.Close(); }
      catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { }
    }
  }

  public async ValueTask DisposeAsync()
  {
    _lifetime.Cancel();
    _listener?.Close();
    if (_listen is not null) await _listen;
    _lifetime.Dispose();
    _startGate.Dispose();
  }

  private sealed class Client
  {
    public string Key { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public Turn? Active { get; set; }
  }

  public sealed class Turn(Uri endpoint, Uri upstream, ExecutionSession session, string key,
    CancellationTokenSource lifetime, Action release, int? outputTokenLimit = null,
    Action<InferenceProgressView>? progressObserver = null) : IDisposable
  {
    private readonly object _gate = new();
    private readonly List<Observation> _calls = [];
    private bool _closed;
    internal string SessionId => session.Id;
    internal string Key => key;
    public string? FinishReason
    {
      get { lock (_gate) return _calls.LastOrDefault()?.FinishReason; }
    }
    public Uri Endpoint { get; } = endpoint;
    internal int? OutputTokenLimit { get; } = outputTokenLimit;
    internal bool IsCodex { get; init; }
    internal OllamaContextResolution? Runtime { get; init; }
    internal bool DisableThinking { get; init; }
    internal Action<InferenceProgressView>? ProgressObserver { get; } = progressObserver;
    internal Uri Upstream { get; } = upstream;
    internal CancellationToken Cancellation { get; } = lifetime.Token;
    internal Observation? Dispatch()
    {
      lock (_gate)
      {
        if (_closed) return null;
        var call = new Observation();
        _calls.Add(call);
        Publish();
        return call;
      }
    }
    internal void Publish()
    {
      lock (_gate)
        if (_calls.Count > 0)
          session.RecordHarnessMetrics(key, _calls.Where(call => !call.Rejected).Select(call => call.Metrics)
            .Aggregate((ExecutionInferenceMetrics?)null, ExecutionInferenceMetrics.Combine)
            ?? _calls.Last().Metrics);
    }
    public void Dispose()
    {
      lock (_gate)
      {
        if (_closed) return;
        _closed = true;
      }
      lifetime.Cancel();
      Publish();
      release();
      lifetime.Dispose();
    }
  }

  internal sealed class Observation
  {
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private double? _first;
    private double? _last;
    private int _deltas;
    private long? _tokens;
    private double? _generation;
    private double? _load;
    private double? _prefill;
    public InferenceProgressSource.Subscription? Progress { get; set; }
    private bool _incomplete;
    private bool _complete;
    private bool _rejected;
    private string? _failure;
    private string? _finishReason;
    public string? FinishReason { get { lock (_gate) return _finishReason; } }
    public bool Complete { get { lock (_gate) return _complete; } }
    public bool Rejected { get { lock (_gate) return _rejected && _tokens is null && _first is null; } }
    public string? Failure { get { lock (_gate) return _failure; } set { lock (_gate) _failure ??= value; } }
    public void ResponseFailed(int status)
    {
      lock (_gate)
      {
        // A client-error response rejects admission. Server errors may have generated
        // tokens before failing and must keep the aggregate explicitly incomplete.
        _rejected = status is 400 or 401 or 403 or 404 or 405 or 413 or 415 or 422 or 429;
        _failure = $"Inference returned HTTP {status}.";
        _incomplete = true;
      }
    }
    public double ElapsedMilliseconds => _clock.Elapsed.TotalMilliseconds;
    public bool Incomplete { get { lock (_gate) return _incomplete; } set { lock (_gate) _incomplete = value; } }
    public ExecutionInferenceMetrics Metrics
    {
      get
      {
        lock (_gate) return new(
          _incomplete ? null : _tokens,
          _incomplete ? null : _generation ?? (_deltas > 1 && _last > _first ? _last - _first : null),
          _started, InferenceObservation.AfterModelLoad(_first, _load), _generation is not null ? "provider" : "stream",
          _first, _load, _load is not null ? "after-model-load" : "dispatch", _prefill, _failure);
      }
    }

    public void ReadFrame(ReadOnlySpan<byte> bytes, double receivedAt, bool streaming)
    {
      lock (_gate) ReadFrameUnsafe(bytes, receivedAt, streaming);
    }

    private void ReadFrameUnsafe(ReadOnlySpan<byte> bytes, double receivedAt, bool streaming)
    {
      var text = Encoding.UTF8.GetString(bytes).Trim();
      if (text.StartsWith("data:", StringComparison.Ordinal)) text = text[5..].TrimStart();
      if (!text.StartsWith('{')) return;
      try
      {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var type = String(root, "type");
        var delta = type is "response.output_text.delta" or "response.reasoning_summary_text.delta"
          or "response.reasoning_text.delta" or "response.function_call_arguments.delta"
            ? String(root, "delta") : null;
        if (type == "content_block_delta" && root.TryGetProperty("delta", out var content))
          delta = String(content, "text") ?? String(content, "thinking") ?? String(content, "partial_json");
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
          foreach (var choice in choices.EnumerateArray())
          {
            _finishReason = String(choice, "finish_reason") ?? _finishReason;
            if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("delta", out var chunk))
              delta = String(chunk, "content") ?? String(chunk, "reasoning") ?? String(chunk, "reasoning_content")
                ?? (chunk.ValueKind == JsonValueKind.Object && chunk.TryGetProperty("tool_calls", out var calls)
                  && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0 ? "tool" : null);
          }
        _finishReason = String(root, "done_reason") ?? String(root, "stop_reason") ?? _finishReason;
        if (type == "message_delta" && root.TryGetProperty("delta", out var messageDelta))
          _finishReason = String(messageDelta, "stop_reason") ?? _finishReason;
        if (root.TryGetProperty("message", out var message))
          delta ??= String(message, "content") ?? String(message, "thinking");
        delta ??= String(root, "response");
        if (streaming && !string.IsNullOrEmpty(delta))
        {
          _first ??= receivedAt;
          Progress?.Generating();
          _last = receivedAt;
          _deltas++;
        }
        // Native /api/generate uses response for text, whereas Responses wraps an object.
        var response = root.TryGetProperty("response", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        if (String(response, "status") == "incomplete"
          && response.TryGetProperty("incomplete_details", out var incomplete))
          _finishReason = String(incomplete, "reason") ?? _finishReason;
        long? reported = null;
        if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
          reported = Number(usage, "output_tokens") ?? Number(usage, "completion_tokens");
        reported = Number(response, "eval_count") ?? reported;
        if (reported is not null)
        {
          _tokens = reported;
          _complete = type is not ("message_start" or "response.created")
            && !(root.TryGetProperty("done", out var pending) && pending.ValueKind == JsonValueKind.False);
          if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
          {
            // Authoritative final native usage survives malformed optional earlier telemetry.
            _incomplete = false;
            _failure = null;
          }
        }
        _generation = Number(response, "eval_duration") / 1_000_000d ?? _generation;
        _load = Number(response, "load_duration") / 1_000_000d ?? _load;
        _prefill = Number(response, "prompt_eval_duration") / 1_000_000d ?? _prefill;
        if (response.TryGetProperty("timings", out var timings))
        {
          _generation = Double(timings, "predicted_ms") ?? _generation;
          _prefill = Double(timings, "prompt_ms") ?? _prefill;
        }
      }
      catch (Exception exception) when (exception is JsonException or InvalidOperationException)
      { _incomplete = true; _failure ??= "Inference telemetry was malformed."; }
    }
    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
      && field.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(field.GetString()) ? field.GetString() : null;
    private static long? Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
      && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var number) && number >= 0 ? number : null;
    private static double? Double(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
      && field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) && number >= 0 && double.IsFinite(number) ? number : null;
  }
}
