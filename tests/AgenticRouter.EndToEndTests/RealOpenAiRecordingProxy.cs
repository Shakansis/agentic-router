using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AgenticRouter.EndToEndTests;

// An opt-in external-boundary recorder. Responses come from the real OpenAI API.
// Neither credentials, input contents, tool arguments nor opaque reasoning are recorded.
internal sealed class RealOpenAiRecordingProxy : IAsyncDisposable
{
  private readonly HttpListener _listener = new();
  private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(120) };
  private readonly CancellationTokenSource _stop = new();
  private readonly ConcurrentBag<Task> _requests = [];
  private readonly ConcurrentQueue<JsonObject> _evidence = [];
  private readonly Task _loop;
  private int _retrievalFailuresRemaining;

  public Uri BaseUri { get; }
  public IReadOnlyList<JsonObject> Evidence => _evidence.ToArray();

  public RealOpenAiRecordingProxy(int retrievalFailures = 0)
  {
    _retrievalFailuresRemaining = retrievalFailures;
    using var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    BaseUri = new Uri($"http://127.0.0.1:{port}/v1/");
    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    _listener.Start();
    _loop = RunAsync();
  }

  private async Task RunAsync()
  {
    try
    {
      while (!_stop.IsCancellationRequested)
        _requests.Add(ForwardAsync(await _listener.GetContextAsync().WaitAsync(_stop.Token)));
    }
    catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
  }

  private async Task ForwardAsync(HttpListenerContext context)
  {
    var watch = Stopwatch.StartNew();
    var evidence = new JsonObject { ["path"] = context.Request.Url!.AbsolutePath, ["method"] = context.Request.HttpMethod };
    try
    {
      if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath.StartsWith("/v1/responses/resp_", StringComparison.Ordinal)
        && Interlocked.Decrement(ref _retrievalFailuresRemaining) >= 0)
      {
        evidence["injected"] = true;
        evidence["httpStatus"] = 503;
        context.Response.StatusCode = 503;
        return;
      }
      using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod),
        new Uri("https://api.openai.com" + context.Request.Url.PathAndQuery));
      if (context.Request.HasEntityBody)
      {
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync(_stop.Token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (JsonNode.Parse(body) is JsonObject payload)
        {
          var safe = new JsonObject();
          foreach (var name in new[] { "model", "store", "stream", "background", "reasoning", "max_output_tokens", "include", "service_tier", "temperature", "top_p" })
            if (payload[name] is { } value) safe[name] = value.DeepClone();
          safe["tools"] = new JsonArray(payload["tools"]?.AsArray().Select(tool => (JsonNode)new JsonObject
          {
            ["type"] = tool!["type"]?.DeepClone(),
            ["name"] = tool["name"]?.DeepClone()
          }).ToArray() ?? []);
          safe["inputItemCount"] = payload["input"]?.AsArray().Count;
          evidence["payload"] = safe;
        }
      }
      foreach (var name in new[] { "Authorization", "OpenAI-Organization", "OpenAI-Project" })
        if (context.Request.Headers[name] is { } value) request.Headers.TryAddWithoutValidation(name, value);
      using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _stop.Token);
      evidence["httpStatus"] = (int)response.StatusCode;
      evidence["requestId"] = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
      context.Response.StatusCode = (int)response.StatusCode;
      context.Response.SendChunked = true;
      context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
      foreach (var header in response.Headers.Where(header => header.Key.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase)))
        context.Response.Headers[header.Key] = string.Join(",", header.Value);
      using var capture = new MemoryStream();
      await using var source = await response.Content.ReadAsStreamAsync(_stop.Token);
      var buffer = new byte[16_384];
      while (await source.ReadAsync(buffer, _stop.Token) is var read && read > 0)
      {
        if (capture.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Real-provider diagnostic capture exceeded its bound.");
        capture.Write(buffer, 0, read);
        await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
        await context.Response.OutputStream.FlushAsync(_stop.Token);
        if (evidence["firstTextDeltaMilliseconds"] is null
          && Encoding.UTF8.GetString(capture.GetBuffer(), 0, checked((int)capture.Length)).Contains("response.output_text.delta", StringComparison.Ordinal))
          evidence["firstTextDeltaMilliseconds"] = watch.ElapsedMilliseconds;
      }
      var content = Encoding.UTF8.GetString(capture.GetBuffer(), 0, checked((int)capture.Length));
      JsonNode? result = null;
      if (context.Response.ContentType.StartsWith("text/event-stream", StringComparison.Ordinal))
      {
        foreach (var line in content.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)))
        {
          if (line[5..].Trim() == "[DONE]") continue;
          var item = JsonNode.Parse(line[5..]);
          if (item?["response"] is { } terminal && item["type"]?.GetValue<string>() is "response.completed" or "response.incomplete" or "response.failed") result = terminal;
        }
      }
      else result = JsonNode.Parse(content);
      evidence["responseModel"] = result?["model"]?.DeepClone();
      evidence["responseStatus"] = result?["status"]?.DeepClone();
      if (result?["id"]?.GetValue<string>() is { } responseId && context.Request.Url.AbsolutePath.StartsWith("/v1/responses", StringComparison.Ordinal))
        evidence["responsePath"] = "/v1/responses/" + responseId;
      evidence["usage"] = result?["usage"]?.DeepClone();
      evidence["incompleteReason"] = result?["incomplete_details"]?["reason"]?.DeepClone();
      evidence["errorCode"] = result?["error"]?["code"]?.DeepClone();
      evidence["errorParameter"] = result?["error"]?["param"]?.DeepClone();
      if (result?["output"] is JsonArray output)
      {
        evidence["outputTypes"] = new JsonArray(output.Select(item => item?["type"]?.DeepClone()).ToArray());
        evidence["hostedWebSearchCallCount"] = output.Count(item => item?["type"]?.GetValue<string>() == "web_search_call");
      }
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or HttpListenerException)
    {
      evidence["transportFailure"] = exception.GetType().Name;
      context.Response.Abort();
    }
    finally
    {
      evidence["totalMilliseconds"] = watch.ElapsedMilliseconds;
      _evidence.Enqueue(evidence);
      context.Response.Close();
    }
  }

  public async ValueTask DisposeAsync()
  {
    await _stop.CancelAsync();
    _listener.Close();
    await _loop;
    await Task.WhenAll(_requests.ToArray());
    _client.Dispose();
    _stop.Dispose();
  }
}
