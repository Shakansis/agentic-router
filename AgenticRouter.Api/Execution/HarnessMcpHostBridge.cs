using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AgenticRouter.Api.Execution;

public sealed record HarnessMcpClientConfiguration(
  Uri Endpoint,
  string AuthorizationEnvironmentVariable,
  string AuthorizationToken
);

public sealed class HarnessMcpHostBridge : IAsyncDisposable
{
  public const string AuthorizationEnvironmentVariable = "AGENTIC_ROUTER_MCP_TOKEN";
  private const int MaximumRequestBytes = 1_048_576;
  private const string ProtocolVersion = "2025-03-26";
  private readonly ConcurrentDictionary<string, BridgeClient> _clients = new(
    StringComparer.OrdinalIgnoreCase
  );
  private readonly SemaphoreSlim _startGate = new(1, 1);
  private readonly ILogger<HarnessMcpHostBridge> _logger;
  private HttpListener? _listener;
  private CancellationTokenSource? _lifetime;
  private Task? _listenerTask;
  private Uri? _baseUri;
  private bool _disposed;

  public HarnessMcpHostBridge(ILogger<HarnessMcpHostBridge> logger)
  {
    _logger = logger;
  }

  public async Task<HarnessMcpClientConfiguration> ConfigureClientAsync(
    string harnessId,
    IReadOnlyList<CanonicalToolDefinition> tools,
    CancellationToken cancellationToken
  )
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    await EnsureStartedAsync(cancellationToken);
    var client = _clients.GetOrAdd(
      harnessId,
      static id => new BridgeClient(
        id,
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
      )
    );
    client.SetTools(tools);
    return new HarnessMcpClientConfiguration(
      new Uri(_baseUri!, $"mcp/{Uri.EscapeDataString(harnessId)}"),
      AuthorizationEnvironmentVariable,
      client.Token
    );
  }

  public HarnessMcpHostTurn BeginTurn(
    string harnessId,
    string sessionId,
    string turnId,
    HostCapabilityProfile profile
  )
  {
    if (!_clients.TryGetValue(harnessId, out var client))
    {
      throw new HarnessException(
        $"{harnessId}-host-bridge-unavailable",
        $"The {harnessId} Host capability bridge is unavailable.",
        "The MCP bridge client was not configured before the turn started.",
        true,
        harnessId: harnessId
      );
    }
    return client.BeginTurn(sessionId, turnId, profile);
  }

  public Task ResolveToolCallAsync(
    string harnessId,
    string toolCallId,
    bool succeeded,
    string output,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (
      !_clients.TryGetValue(harnessId, out var client)
      || !client.Resolve(toolCallId, succeeded, output)
    )
    {
      throw new HarnessException(
        $"{harnessId}-tool-call-stale",
        $"The {harnessId} Host tool call is no longer pending.",
        $"No pending MCP Host tool request exists for call {toolCallId}.",
        true,
        harnessId: harnessId
      );
    }
    return Task.CompletedTask;
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed)
    {
      return;
    }
    _disposed = true;
    _lifetime?.Cancel();
    _listener?.Close();
    if (_listenerTask is not null)
    {
      try
      {
        await _listenerTask;
      }
      catch (OperationCanceledException)
      {
      }
      catch (HttpListenerException) when (_lifetime?.IsCancellationRequested == true)
      {
      }
    }
    foreach (var client in _clients.Values)
    {
      client.Dispose();
    }
    _clients.Clear();
    _lifetime?.Dispose();
    _listener?.Close();
    _startGate.Dispose();
  }

  private async Task EnsureStartedAsync(CancellationToken cancellationToken)
  {
    if (_listener is { IsListening: true })
    {
      return;
    }
    await _startGate.WaitAsync(cancellationToken);
    try
    {
      if (_listener is { IsListening: true })
      {
        return;
      }
      var port = ReservePort();
      var baseUri = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
      var listener = new HttpListener();
      listener.Prefixes.Add(baseUri.AbsoluteUri);
      listener.Start();
      _baseUri = baseUri;
      _listener = listener;
      _lifetime = new CancellationTokenSource();
      _listenerTask = ListenAsync(listener, _lifetime.Token);
    }
    finally
    {
      _startGate.Release();
    }
  }

  private async Task ListenAsync(
    HttpListener listener,
    CancellationToken cancellationToken
  )
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      HttpListenerContext context;
      try
      {
        context = await listener.GetContextAsync().WaitAsync(cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      _ = HandleAsync(context, cancellationToken);
    }
  }

  private async Task HandleAsync(
    HttpListenerContext context,
    CancellationToken cancellationToken
  )
  {
    try
    {
      var path = context.Request.Url?.AbsolutePath.Trim('/').Split('/');
      var harnessId = path is ["mcp", var requestedHarnessId]
        ? Uri.UnescapeDataString(requestedHarnessId)
        : null;
      BridgeClient? matchedClient = null;
      var pathMatched = harnessId is not null;
      var clientKnown = pathMatched && _clients.TryGetValue(harnessId!, out matchedClient);
      var authorizationPresent = !string.IsNullOrWhiteSpace(
        context.Request.Headers["Authorization"]
      );
      var authorizationValid = clientKnown && FixedTimeTokenEquals(
        context.Request.Headers["Authorization"],
        matchedClient!.Token
      );
      if (
        !pathMatched
        || !clientKnown
        || !authorizationValid
      )
      {
        _logger.LogInformation(
          "The harness MCP Host bridge rejected {Method} {Path}. PathMatched={PathMatched} ClientKnown={ClientKnown} AuthorizationPresent={AuthorizationPresent} AuthorizationValid={AuthorizationValid}.",
          context.Request.HttpMethod,
          context.Request.Url?.AbsolutePath,
          pathMatched,
          clientKnown,
          authorizationPresent,
          authorizationValid
        );
        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        return;
      }
      var client = matchedClient!;
      if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.Ordinal))
      {
        context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
        return;
      }
      if (context.Request.ContentLength64 > MaximumRequestBytes)
      {
        context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
        return;
      }

      using var body = new MemoryStream();
      var buffer = new byte[16_384];
      while (true)
      {
        var read = await context.Request.InputStream.ReadAsync(buffer, cancellationToken);
        if (read == 0)
        {
          break;
        }
        if (body.Length + read > MaximumRequestBytes)
        {
          context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
          return;
        }
        await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
      }
      body.Position = 0;
      using var document = await JsonDocument.ParseAsync(
        body,
        new JsonDocumentOptions { MaxDepth = 64 },
        cancellationToken
      );
      var request = document.RootElement;
      var hasId = request.TryGetProperty("id", out var id);
      var method = request.TryGetProperty("method", out var methodValue)
        ? methodValue.GetString()
        : null;
      request.TryGetProperty("params", out var parameters);

      _logger.LogInformation(
        "The harness MCP Host bridge received {McpMethod} for {HarnessId}. HasId={HasId}.",
        method,
        harnessId,
        hasId
      );

      if (!hasId)
      {
        context.Response.StatusCode = (int)HttpStatusCode.Accepted;
        return;
      }

      object result = method switch
      {
        "initialize" => InitializeResult(parameters),
        "ping" => new { },
        "prompts/list" => new { prompts = Array.Empty<object>() },
        "resources/list" => new { resources = Array.Empty<object>() },
        "tools/list" => new { tools = client.Tools.Select(ToMcpTool).ToArray() },
        "tools/call" => await CallToolAsync(client, parameters, cancellationToken),
        _ => throw new McpMethodException(method)
      };
      await WriteJsonAsync(
        context.Response,
        new { jsonrpc = "2.0", id = id.Clone(), result },
        HttpStatusCode.OK,
        cancellationToken
      );
    }
    catch (McpMethodException exception)
    {
      await WriteErrorAsync(context.Response, -32601, exception.Message, cancellationToken);
    }
    catch (JsonException exception)
    {
      await WriteErrorAsync(context.Response, -32700, exception.Message, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
    }
    catch (Exception exception)
    {
      _logger.LogWarning(exception, "The harness MCP Host bridge rejected a request.");
      await WriteErrorAsync(
        context.Response,
        -32603,
        "The Agentic Router Host bridge could not complete this request.",
        CancellationToken.None
      );
    }
    finally
    {
      context.Response.Close();
    }
  }

  private static object InitializeResult(JsonElement parameters)
  {
    var requested = parameters.ValueKind == JsonValueKind.Object
      && parameters.TryGetProperty("protocolVersion", out var version)
      && version.ValueKind == JsonValueKind.String
        ? version.GetString()
        : null;
    var selected = requested is "2024-11-05" or "2025-03-26" or "2025-06-18"
      ? requested
      : ProtocolVersion;
    return new
    {
      protocolVersion = selected,
      capabilities = new { tools = new { listChanged = false } },
      serverInfo = new { name = "agentic-router-host", version = "1" },
      instructions = "Agentic Router validates policy, approvals, trusted-workspace boundaries, effects, and execution truth for every tool call."
    };
  }

  private static async Task<object> CallToolAsync(
    BridgeClient client,
    JsonElement parameters,
    CancellationToken cancellationToken
  )
  {
    if (
      parameters.ValueKind != JsonValueKind.Object
      || !parameters.TryGetProperty("name", out var nameValue)
      || nameValue.ValueKind != JsonValueKind.String
    )
    {
      return ToolResult(false, "Host tool request omitted its canonical name.");
    }
    var name = nameValue.GetString()!;
    JsonElement arguments;
    if (parameters.TryGetProperty("arguments", out var supplied))
    {
      arguments = supplied.Clone();
    }
    else
    {
      using var empty = JsonDocument.Parse("{}");
      arguments = empty.RootElement.Clone();
    }
    var result = await client.CallAsync(name, arguments, cancellationToken);
    return ToolResult(result.Succeeded, result.Output);
  }

  private static object ToolResult(bool succeeded, string output)
  {
    var payload = HostActionResultAdapter.IsSerialized(output)
      ? output
      : HostActionResultAdapter.FromLegacy(
        output,
        succeeded,
        succeeded ? "MCP_BRIDGE_COMPLETED" : "MCP_BRIDGE_FAILED",
        null,
        effectVerified: false,
        retryUnchanged: succeeded ? null : true
      ).Serialize();
    return new
    {
      content = new[] { new { type = "text", text = payload } },
      isError = !succeeded
    };
  }

  private static object ToMcpTool(CanonicalToolDefinition definition)
  {
    return new
    {
      name = definition.Name,
      description = definition.Description,
      inputSchema = definition.Parameters
    };
  }

  private static bool FixedTimeTokenEquals(string? authorization, string token)
  {
    const string prefix = "Bearer ";
    if (authorization?.StartsWith(prefix, StringComparison.Ordinal) != true)
    {
      return false;
    }
    var supplied = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
    var expected = Encoding.UTF8.GetBytes(token);
    return supplied.Length == expected.Length
      && CryptographicOperations.FixedTimeEquals(supplied, expected);
  }

  private static async Task WriteErrorAsync(
    HttpListenerResponse response,
    int code,
    string message,
    CancellationToken cancellationToken
  )
  {
    if (response.OutputStream.CanWrite)
    {
      await WriteJsonAsync(
        response,
        new
        {
          jsonrpc = "2.0",
          id = (object?)null,
          error = new { code, message }
        },
        HttpStatusCode.OK,
        cancellationToken
      );
    }
  }

  private static async Task WriteJsonAsync(
    HttpListenerResponse response,
    object value,
    HttpStatusCode status,
    CancellationToken cancellationToken
  )
  {
    var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
    response.StatusCode = (int)status;
    response.ContentType = "application/json";
    response.ContentEncoding = Encoding.UTF8;
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes, cancellationToken);
  }

  private static int ReservePort()
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  private sealed class BridgeClient
  {
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeToolResult>> _pending = new(
      StringComparer.Ordinal
    );
    private IReadOnlyList<CanonicalToolDefinition> _tools = [];
    private ActiveBridgeTurn? _active;

    public BridgeClient(string harnessId, string token)
    {
      HarnessId = harnessId;
      Token = token;
    }

    public string HarnessId { get; }

    public string Token { get; }

    public IReadOnlyList<CanonicalToolDefinition> Tools
    {
      get
      {
        lock (_gate)
        {
          return _tools;
        }
      }
    }

    public void SetTools(IReadOnlyList<CanonicalToolDefinition> tools)
    {
      lock (_gate)
      {
        _tools = tools.ToArray();
      }
    }

    public HarnessMcpHostTurn BeginTurn(
      string sessionId,
      string turnId,
      HostCapabilityProfile profile
    )
    {
      lock (_gate)
      {
        if (_active is not null)
        {
          throw new HarnessException(
            $"{HarnessId}-host-bridge-busy",
            $"The {HarnessId} Host bridge is already serving another turn.",
            $"Active bridge turn: {_active.TurnId}.",
            true,
            harnessId: HarnessId
          );
        }
        var active = new ActiveBridgeTurn(sessionId, turnId, profile);
        _active = active;
        return new HarnessMcpHostTurn(active.Events.Reader, () => EndTurn(active));
      }
    }

    public async Task<BridgeToolResult> CallAsync(
      string tool,
      JsonElement arguments,
      CancellationToken cancellationToken
    )
    {
      ActiveBridgeTurn? active;
      lock (_gate)
      {
        active = _active;
      }
      if (active is null)
      {
        return new BridgeToolResult(
          false,
          "The Agentic Router Host bridge has no active turn. Retry through the active harness turn."
        );
      }
      if (
        !active.Profile.Allows(tool)
        || !HarnessCapabilityProjection.HostBridgeTools(HarnessId, active.Profile).Contains(
          tool,
          StringComparer.OrdinalIgnoreCase
        )
      )
      {
        return new BridgeToolResult(
          false,
          $"Host capability '{tool}' is not available for this turn. Choose a currently offered materially different capability."
        );
      }

      var callId = $"mcp-{Guid.NewGuid():N}";
      var completion = new TaskCompletionSource<BridgeToolResult>(
        TaskCreationOptions.RunContinuationsAsynchronously
      );
      if (!_pending.TryAdd(callId, completion))
      {
        return new BridgeToolResult(false, "The Host could not allocate a unique tool call identifier.");
      }
      if (!active.Events.Writer.TryWrite(new HarnessEvent(
        "host-tool.requested",
        $"{HarnessId} requested Host tool {tool} through MCP.",
        tool: tool,
        state: "proposed",
        toolCallId: callId,
        arguments: arguments,
        harnessId: HarnessId,
        sessionId: active.SessionId,
        turnId: active.TurnId
      )))
      {
        _pending.TryRemove(callId, out _);
        return new BridgeToolResult(false, "The active Agentic Router turn ended before the tool request could be delivered.");
      }
      try
      {
        return await completion.Task.WaitAsync(cancellationToken);
      }
      finally
      {
        _pending.TryRemove(callId, out _);
      }
    }

    public bool Resolve(string callId, bool succeeded, string output)
    {
      return _pending.TryRemove(callId, out var completion)
        && completion.TrySetResult(new BridgeToolResult(succeeded, output));
    }

    public void Dispose()
    {
      ActiveBridgeTurn? active;
      lock (_gate)
      {
        active = _active;
        _active = null;
      }
      active?.Events.Writer.TryComplete();
      FailPending("The Agentic Router Host bridge stopped before this tool call completed.");
    }

    private void EndTurn(ActiveBridgeTurn turn)
    {
      lock (_gate)
      {
        if (!ReferenceEquals(_active, turn))
        {
          return;
        }
        _active = null;
      }
      turn.Events.Writer.TryComplete();
      FailPending("The harness turn ended before this Host tool call completed.");
    }

    private void FailPending(string message)
    {
      foreach (var pending in _pending.ToArray())
      {
        if (_pending.TryRemove(pending.Key, out var completion))
        {
          completion.TrySetResult(new BridgeToolResult(false, message));
        }
      }
    }
  }

  private sealed record BridgeToolResult(bool Succeeded, string Output);

  private sealed class ActiveBridgeTurn(
    string sessionId,
    string turnId,
    HostCapabilityProfile profile
  )
  {
    public string SessionId { get; } = sessionId;

    public string TurnId { get; } = turnId;

    public HostCapabilityProfile Profile { get; } = profile;

    public Channel<HarnessEvent> Events { get; } = Channel.CreateUnbounded<HarnessEvent>(
      new UnboundedChannelOptions
      {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
      }
    );
  }

  private sealed class McpMethodException(string? method) : Exception(
    $"MCP method '{method ?? "<missing>"}' is not supported."
  );
}

public sealed class HarnessMcpHostTurn : IDisposable
{
  private readonly Action _dispose;
  private int _disposed;

  internal HarnessMcpHostTurn(ChannelReader<HarnessEvent> events, Action dispose)
  {
    Events = events;
    _dispose = dispose;
  }

  public ChannelReader<HarnessEvent> Events { get; }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) == 0)
    {
      _dispose();
    }
  }
}
