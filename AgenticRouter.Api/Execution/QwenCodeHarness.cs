using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticRouter.Api.Execution;

public sealed record QwenCodeHarnessOptions(
  string? ExecutablePath,
  string? ManagedCliPath,
  string RuntimeDirectory,
  TimeSpan StartupTimeout,
  TimeSpan RequestTimeout
);

public sealed class QwenCodeHarnessAdapter : IAgentHarness, IAgentHarnessTransport, IAgentHarnessSteeringTransport
{
  private const int MaximumActivityText = 8_192;
  private const int MaximumNativeDiagnosticsPerTurn = 8;
  private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromMinutes(1);
  private static readonly string[] RequiredFeatures =
  [
    "session_create",
    "session_scope_override",
    "session_prompt",
    "session_cancel",
    "session_events",
    "typed_event_schema",
    "session_set_model",
    "session_permission_vote",
    "session_mid_turn_message_mutation",
    "session_mid_turn_message_query",
    "session_context",
    "session_close",
    "workspace_providers",
    "require_auth"
  ];
  private static readonly string[] DefaultCoreTools =
  [
    "web_fetch",
    "web_search",
    "todo_write"
  ];
  private static readonly HarnessDefinition AdapterDefinition = new(
    HarnessIds.QwenCode,
    "Qwen Code",
    true,
    "Qwen Code HTTP daemon with typed SSE, isolated local configuration, and Host-observed effects.",
    new HarnessCapabilities(
      SupportsStreaming: true,
      SupportsThinking: true,
      SupportsResume: true,
      SupportsCancel: true,
      SupportsApprovals: true,
      SupportsToolEvents: true,
      SupportsStructuredEdits: true,
      SupportsStaleProtection: true,
      SupportsSubagents: false,
      SupportsSandbox: false,
      SupportsSessionDiff: false,
      SupportsNativePermissions: true,
      SupportsSteering: true
    ),
    ["ollama-local"]
  );

  private readonly QwenCodeHarnessOptions _options;
  private readonly IHttpClientFactory _httpClients;
  private readonly HarnessMcpHostBridge _hostTools;
  private readonly ILogger<QwenCodeHarnessAdapter> _logger;
  private readonly SemaphoreSlim _availabilityGate = new(1, 1);
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly SemaphoreSlim _turnGate = new(1, 1);
  private readonly object _processOutputGate = new();
  private readonly Queue<string> _recentProcessOutput = new();
  private readonly ConcurrentDictionary<string, QwenSession> _sessions = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, PendingPermission> _permissions = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, ActiveTurn> _activeTurns = new(StringComparer.Ordinal);
  private Process? _process;
  private Uri? _serverUri;
  private string? _token;
  private string? _configurationKey;
  private HarnessAvailability? _cachedAvailability;
  private bool _disposed;

  public QwenCodeHarnessAdapter(
    QwenCodeHarnessOptions options,
    IHttpClientFactory httpClients,
    HarnessMcpHostBridge hostTools,
    ILogger<QwenCodeHarnessAdapter> logger
  )
  {
    _options = options;
    _httpClients = httpClients;
    _hostTools = hostTools;
    _logger = logger;
  }

  public HarnessDefinition Definition => AdapterDefinition;

  public IAsyncEnumerable<TEvent> ExecuteAsync<TEvent>(
    AgentHarnessExecution<TEvent> execution,
    CancellationToken cancellationToken
  )
  {
    return execution.ExecuteExternalAsync(this, cancellationToken);
  }

  public async ValueTask<HarnessAvailability> GetAvailabilityAsync(
    CancellationToken cancellationToken
  )
  {
    if (
      _cachedAvailability is { } cached
      && DateTimeOffset.UtcNow - cached.CheckedAt < AvailabilityCacheDuration
    )
    {
      return cached;
    }

    await _availabilityGate.WaitAsync(cancellationToken);
    try
    {
      var command = ResolveCommand();
      var startInfo = CreateStartInfo(command, _options.RuntimeDirectory);
      startInfo.ArgumentList.Add("--version");
      using var process = new Process { StartInfo = startInfo };
      if (!process.Start())
      {
        throw new InvalidOperationException("Process.Start returned false.");
      }
      var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
      var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(_options.StartupTimeout);
      await process.WaitForExitAsync(timeout.Token);
      var output = (await outputTask).Trim();
      var error = (await errorTask).Trim();
      if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
      {
        throw new InvalidOperationException($"Exit {process.ExitCode}: {Truncate(error)}");
      }
      _cachedAvailability = HarnessAvailability.Ready(
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]
      );
    }
    catch (Exception exception) when (
      exception is not OperationCanceledException
      || !cancellationToken.IsCancellationRequested
    )
    {
      _logger.LogInformation(exception, "Qwen Code harness is unavailable.");
      _cachedAvailability = HarnessAvailability.Missing(
        "Qwen Code executable was not found or could not be started."
      );
    }
    finally
    {
      _availabilityGate.Release();
    }
    return _cachedAvailability;
  }

  public async IAsyncEnumerable<HarnessEvent> StartTurnAsync(
    HarnessTurnRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    Validate(request);
    await _turnGate.WaitAsync(cancellationToken);
    ActiveTurn? active = null;
    try
    {
      var hostProfile = request.HostCapabilities ?? throw Failure(
        "qwen-code-host-profile-missing",
        "Qwen Code requires the Agentic Router Host capability profile."
      );
      var hostBridgeTools = HostBridgeTools(hostProfile);
      var bridge = await _hostTools.ConfigureClientAsync(
        HarnessIds.QwenCode,
        hostBridgeTools,
        cancellationToken
      );
      await EnsureStartedAsync(
        request.ProviderEndpoint!,
        request.Model,
        request.ContextWindowTokens
          ?? throw Failure(
            "qwen-code-context-missing",
            "Qwen Code requires a Host-resolved context window."
        ),
        request.WorkingDirectory,
        bridge,
        hostProfile,
        hostBridgeTools,
        request.UseMinimalToolInventory,
        cancellationToken
      );
      var session = await GetOrCreateSessionAsync(request, cancellationToken);
      var nativeCapabilities = request.UseMinimalToolInventory
        ? ActiveNativeTools(hostProfile)
        : HarnessCapabilityProjection.NativeCommonTools(HarnessIds.QwenCode);
      var hostBridgeNames = HarnessCapabilityProjection.HostBridgeTools(
        HarnessIds.QwenCode,
        hostProfile
      );
      var turnPrompt = HarnessConversationPromptBuilder.Create(
        request,
        session.SynchronizedThroughVersion,
        [
          $"Agentic Router common capabilities implemented by Qwen native tools for this turn: {string.Join(", ", nativeCapabilities)}.",
          $"Agentic Router common capabilities supplied through the Host bridge for this turn: {string.Join(", ", hostBridgeNames)}.",
          hostBridgeNames.Count == 0
            ? "No Host bridge tool is available for this turn."
            : $"Deferred Host tools use exact names mcp__agentic_router__<canonical-name>. Resolve only one permitted selector with tool_search when needed: {string.Join(", ", hostBridgeNames.Select(name => $"select:mcp__agentic_router__{name}"))}.",
          $"Host approval policy: {hostProfile.ApprovalPolicy}."
        ]
      );
      active = new ActiveTurn(
        request.SessionId,
        session.SessionId,
        session.ClientId,
        request.WorkingDirectory
      );
      _activeTurns[request.SessionId] = active;

      yield return Event(
        "turn.started",
        active,
        message: $"Qwen Code session {session.SessionId} started."
      );

      using var eventsRequest = CreateRequest(
        HttpMethod.Get,
        $"session/{EncodePath(session.SessionId)}/events?connectReason=initial",
        session.ClientId
      );
      eventsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
      eventsRequest.Headers.TryAddWithoutValidation("Last-Event-ID", "0");
      using var eventsResponse = await Client().SendAsync(
        eventsRequest,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken
      );
      await EnsureSuccessAsync(eventsResponse, "qwen-code-event-stream", cancellationToken);

      var prompt = await SendAsync(
        HttpMethod.Post,
        $"session/{EncodePath(session.SessionId)}/prompt",
        new
        {
          prompt = new[]
          {
            new { type = "text", text = turnPrompt.Text }
          }
        },
        session.ClientId,
        cancellationToken,
        HttpStatusCode.Accepted
      );
      active.PromptId = RequiredString(prompt, "promptId");
      var baselineEventId = Long(prompt, "lastEventId") ?? 0;
      using var hostTurn = _hostTools.BeginTurn(
        HarnessIds.QwenCode,
        session.SessionId,
        active.PromptId,
        hostProfile
      );

      await using var stream = await eventsResponse.Content.ReadAsStreamAsync(cancellationToken);
      using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
      var tools = new Dictionary<string, string>(StringComparer.Ordinal);
      var preservedUpdateTypes = new HashSet<string>(StringComparer.Ordinal);
      var preservedEventTypes = new HashSet<string>(StringComparer.Ordinal);
      var textDeltas = new TextDeltaCoalescer();
      var assistantCharacters = 0;
      var nativeDiagnostics = 0;
      Task<string?>? lineRead = null;
      Task<HarnessEvent>? hostToolRead = null;
      while (true)
      {
        lineRead ??= ReadEventLineAsync(reader, cancellationToken);
        hostToolRead ??= hostTurn.Events.ReadAsync(cancellationToken).AsTask();
        var next = await Task.WhenAny(lineRead, hostToolRead);
        if (ReferenceEquals(next, hostToolRead))
        {
          var hostToolEvent = await hostToolRead;
          hostToolRead = null;
          yield return hostToolEvent;
          continue;
        }
        var line = await lineRead;
        lineRead = null;
        if (line is null)
        {
          throw Failure(
            "qwen-code-event-stream-ended",
            "Qwen Code event stream ended before terminal state."
          );
        }
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
          continue;
        }
        var json = line[5..].TrimStart();
        if (json.Length == 0)
        {
          continue;
        }

        JsonElement payload;
        try
        {
          using var document = JsonDocument.Parse(json);
          payload = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
          throw new HarnessException(
            "qwen-code-protocol-json",
            "Qwen Code returned malformed event data.",
            Truncate(exception.Message),
            true,
            exception,
            HarnessIds.QwenCode
          );
        }

        var eventId = Long(payload, "id");
        if (eventId.HasValue && eventId.Value <= baselineEventId)
        {
          continue;
        }
        var type = String(payload, "type");
        if (type is null || !payload.TryGetProperty("data", out var data))
        {
          foreach (var pending in textDeltas.Flush())
          {
            yield return pending;
          }
          if (nativeDiagnostics < MaximumNativeDiagnosticsPerTurn)
          {
            nativeDiagnostics++;
            yield return Event("native.event", active, native: payload);
          }
          continue;
        }

        if (!string.Equals(type, "session_update", StringComparison.Ordinal))
        {
          foreach (var pending in textDeltas.Flush())
          {
            yield return pending;
          }
        }

        switch (type)
        {
          case "session_update":
            {
              var mapped = MapSessionUpdate(
                active,
                data,
                payload,
                tools,
                preservedUpdateTypes
              );
              if (mapped is null)
              {
                break;
              }
              if (string.Equals(mapped.Type, "assistant.delta", StringComparison.Ordinal))
              {
                assistantCharacters += mapped.Delta?.Length ?? 0;
              }
              if (string.Equals(mapped.Type, "native.event", StringComparison.Ordinal))
              {
                if (nativeDiagnostics >= MaximumNativeDiagnosticsPerTurn)
                {
                  break;
                }
                nativeDiagnostics++;
              }
              foreach (var ready in textDeltas.Accept(mapped))
              {
                yield return ready;
              }
              break;
            }
          case "permission_request":
            yield return MapPermission(active, data, payload);
            break;
          case "turn_complete" when MatchesPrompt(active, data):
            {
              var stopReason = String(data, "stopReason") ?? "unknown";
              if (string.Equals(stopReason, "end_turn", StringComparison.Ordinal))
              {
                if (assistantCharacters == 0)
                {
                  yield return Event(
                    "turn.failed",
                    active,
                    message: "Qwen Code completed without an assistant response.",
                    errorCode: "qwen-code-empty-response",
                    terminal: HarnessTerminalState.Failed,
                    native: payload
                  );
                }
                else
                {
                  session.SynchronizedThroughVersion = turnPrompt.SynchronizedThroughVersion;
                  yield return Event(
                    "turn.completed",
                    active,
                    message: "Qwen Code completed the turn.",
                    terminal: HarnessTerminalState.Completed,
                    native: payload
                  );
                }
              }
              else if (string.Equals(stopReason, "cancelled", StringComparison.Ordinal))
              {
                yield return Event(
                  "turn.cancelled",
                  active,
                  message: "Qwen Code cancelled the turn.",
                  terminal: HarnessTerminalState.Cancelled,
                  native: payload
                );
              }
              else
              {
                if (
                  assistantCharacters > 0
                  && stopReason is "max_tokens" or "length"
                )
                {
                  session.SynchronizedThroughVersion = turnPrompt.SynchronizedThroughVersion;
                }
                yield return Event(
                  "turn.failed",
                  active,
                  message: $"Qwen Code stopped with reason {stopReason}.",
                  errorCode: $"qwen-code-stop-{stopReason}",
                  terminal: stopReason is "max_tokens" or "length"
                    ? HarnessTerminalState.Partial
                    : HarnessTerminalState.Failed,
                  native: payload
                );
              }
              yield break;
            }
          case "turn_error" when MatchesPrompt(active, data):
            yield return Event(
              "turn.failed",
              active,
              message: String(data, "message") ?? "Qwen Code reported a turn error.",
              errorCode: "qwen-code-turn-error",
              terminal: HarnessTerminalState.Failed,
              native: payload
            );
            yield break;
          case "session_died":
          case "client_evicted":
          case "stream_error":
            yield return Event(
              "turn.failed",
              active,
              message: $"Qwen Code event stream terminated: {type}.",
              errorCode: $"qwen-code-{type.Replace('_', '-')}",
              terminal: HarnessTerminalState.Failed,
              native: payload
            );
            yield break;
          case "slow_client_warning":
          case "history_truncated":
          case "state_resync_required":
            yield return Event(
              "warning",
              active,
              message: $"Qwen Code reported {type.Replace('_', ' ')}.",
              native: payload
            );
            break;
          case "permission_resolved":
          case "model_switched":
          case "session_snapshot":
            if (
              nativeDiagnostics < MaximumNativeDiagnosticsPerTurn
              && preservedEventTypes.Add(type)
            )
            {
              nativeDiagnostics++;
              yield return Event("native.event", active, native: payload);
            }
            break;
          case "model_switch_failed":
            yield return Event(
              "turn.failed",
              active,
              message: $"Qwen Code rejected exact model {request.Model}.",
              errorCode: "qwen-code-model-switch-failed",
              terminal: HarnessTerminalState.Failed,
              native: payload
            );
            yield break;
          default:
            if (
              nativeDiagnostics < MaximumNativeDiagnosticsPerTurn
              && preservedEventTypes.Add(type)
            )
            {
              nativeDiagnostics++;
              yield return Event("native.event", active, native: payload);
            }
            break;
        }
      }
    }
    finally
    {
      try
      {
        if (cancellationToken.IsCancellationRequested)
        {
          await CancelTurnAsync(request.SessionId, CancellationToken.None);
        }
        _activeTurns.TryRemove(request.SessionId, out _);
        if (active is not null)
        {
          foreach (var pending in _permissions.Where(
            pair => string.Equals(pair.Value.SessionId, active.QwenSessionId, StringComparison.Ordinal)
          ).ToArray())
          {
            _permissions.TryRemove(pending.Key, out _);
          }
        }
        if (
          request.ReleaseWorkspaceAfterTurn
          || (
            request.ReleaseWorkspaceOnCancellation
            && cancellationToken.IsCancellationRequested
          )
        )
        {
          await ReleaseWorkspaceAsync();
        }
      }
      finally
      {
        _turnGate.Release();
      }
    }
  }

  public async Task ResolveApprovalAsync(
    string approvalId,
    bool approved,
    CancellationToken cancellationToken
  )
  {
    if (!_permissions.TryRemove(approvalId, out var pending))
    {
      throw Failure("qwen-code-permission-stale", "The Qwen Code permission is no longer pending.");
    }
    var optionId = approved ? pending.AllowOnceOptionId : pending.RejectOnceOptionId;
    object body = optionId is null
      ? new { outcome = new { outcome = "cancelled" } }
      : new { outcome = new { outcome = "selected", optionId } };
    await SendAsync(
      HttpMethod.Post,
      $"permission/{EncodePath(approvalId)}",
      body,
      pending.ClientId,
      cancellationToken
    );
  }

  public Task ResolveToolCallAsync(
    string toolCallId,
    bool succeeded,
    string output,
    CancellationToken cancellationToken
  )
  {
    return _hostTools.ResolveToolCallAsync(
      HarnessIds.QwenCode,
      toolCallId,
      succeeded,
      output,
      cancellationToken
    );
  }

  public async Task CancelTurnAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    if (!_activeTurns.TryGetValue(sessionId, out var active))
    {
      return;
    }
    await SendAsync(
      HttpMethod.Post,
      $"session/{EncodePath(active.QwenSessionId)}/cancel",
      null,
      active.ClientId,
      cancellationToken,
      HttpStatusCode.NoContent
    );
  }

  public async Task<HarnessSteerResult> SteerTurnAsync(
    HarnessSteerRequest request,
    CancellationToken cancellationToken
  )
  {
    if (
      !_activeTurns.TryGetValue(request.SessionId, out var active)
      || string.IsNullOrWhiteSpace(active.PromptId)
    )
    {
      throw Failure(
        "qwen-code-steer-stale",
        "The Qwen Code turn is no longer available for steering."
      );
    }

    var result = await SendAsync(
      HttpMethod.Post,
      $"session/{EncodePath(active.QwenSessionId)}/mid-turn-message",
      new
      {
        message = request.Message,
        messageId = request.MessageId
      },
      active.ClientId,
      cancellationToken
    );
    if (
      result.ValueKind != JsonValueKind.Object
      || !result.TryGetProperty("accepted", out var accepted)
      || accepted.ValueKind != JsonValueKind.True
    )
    {
      throw Failure(
        "qwen-code-steer-rejected",
        "Qwen Code did not accept the steering message."
      );
    }

    var returnedMessageId = String(result, "messageId") ?? request.MessageId;
    var reconciliation = await SendAsync(
      HttpMethod.Get,
      $"session/{EncodePath(active.QwenSessionId)}/mid-turn-messages",
      null,
      active.ClientId,
      cancellationToken
    );
    if (StringArrayContains(reconciliation, "promotedMessageIds", returnedMessageId))
    {
      await SendAsync(
        HttpMethod.Delete,
        $"session/{EncodePath(active.QwenSessionId)}/mid-turn-messages/{EncodePath(returnedMessageId)}",
        null,
        active.ClientId,
        cancellationToken
      );
      throw Failure(
        "qwen-code-steer-promoted",
        "The Qwen Code turn ended before it could receive the steering message. The message remains in the composer so it can be queued explicitly."
      );
    }
    return new HarnessSteerResult(
      HarnessIds.QwenCode,
      request.SessionId,
      active.PromptId,
      returnedMessageId,
      true
    );
  }

  private async Task ReleaseWorkspaceAsync()
  {
    await _lifecycleGate.WaitAsync(CancellationToken.None);
    try
    {
      await StopOwnedProcessAsync();
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed)
    {
      return;
    }
    _disposed = true;
    await StopOwnedProcessAsync();
    _availabilityGate.Dispose();
    _lifecycleGate.Dispose();
    _turnGate.Dispose();
  }

  private static HarnessEvent? MapSessionUpdate(
    ActiveTurn active,
    JsonElement data,
    JsonElement payload,
    IDictionary<string, string> tools,
    ISet<string> preservedUpdateTypes
  )
  {
    if (
      !data.TryGetProperty("update", out var updateData)
      || updateData.ValueKind != JsonValueKind.Object
    )
    {
      return preservedUpdateTypes.Add("missing-update")
        ? Event("native.event", active, native: payload)
        : null;
    }
    var update = String(updateData, "sessionUpdate");
    switch (update)
    {
      case "agent_message_chunk":
        return Event(
          "assistant.delta",
          active,
          delta: String(updateData, "content", "text"),
          itemId: $"qwen-answer-{active.PromptId}",
          native: payload
        );
      case "agent_thought_chunk":
        return Event(
          "reasoning.delta",
          active,
          delta: String(updateData, "content", "text"),
          itemId: $"qwen-thought-{active.PromptId}",
          native: payload
        );
      case "tool_call":
        {
          var callId = RequiredString(updateData, "toolCallId");
          var tool = ToolName(updateData);
          tools[callId] = tool;
          return Event(
            "tool.started",
            active,
            message: String(updateData, "title") ?? $"Qwen Code started {tool}.",
            itemId: callId,
            tool: tool,
            state: String(updateData, "status") ?? "running",
            output: Json(updateData, "rawInput"),
            native: payload
          );
        }
      case "tool_call_update":
        {
          var callId = RequiredString(updateData, "toolCallId");
          var tool = tools.TryGetValue(callId, out var known) ? known : ToolName(updateData);
          var status = String(updateData, "status") ?? "in_progress";
          var normalized = status switch
          {
            "completed" => "tool.completed",
            "failed" => "tool.failed",
            _ => "tool.output"
          };
          return Event(
            normalized,
            active,
            message: String(updateData, "title") ?? $"Qwen Code {tool}: {status}.",
            itemId: callId,
            tool: tool,
            state: status,
            output: Json(updateData, "rawOutput") ?? ContentText(updateData),
            native: payload
          );
        }
      case "usage_update":
        return Event(
          "usage.updated",
          active,
          contextTotalTokens: Long(updateData, "used"),
          native: payload
        );
      case "user_message_chunk":
      case "plan":
      case "available_commands_update":
      case "current_mode_update":
      case "config_option_update":
      case "session_info_update":
        return null;
      default:
        var diagnosticKey = update ?? "missing-session-update";
        return preservedUpdateTypes.Add(diagnosticKey)
          ? Event("native.event", active, native: payload)
          : null;
    }
  }

  private HarnessEvent MapPermission(
    ActiveTurn active,
    JsonElement data,
    JsonElement payload
  )
  {
    var requestId = RequiredString(data, "requestId");
    var eventSessionId = String(data, "sessionId");
    if (!string.Equals(eventSessionId, active.QwenSessionId, StringComparison.Ordinal))
    {
      return Event("native.event", active, native: payload);
    }
    data.TryGetProperty("toolCall", out var toolCall);
    string? allowOnce = null;
    string? rejectOnce = null;
    if (data.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
    {
      foreach (var option in options.EnumerateArray())
      {
        var kind = String(option, "kind");
        if (string.Equals(kind, "allow_once", StringComparison.Ordinal))
        {
          allowOnce = String(option, "optionId");
        }
        else if (string.Equals(kind, "reject_once", StringComparison.Ordinal))
        {
          rejectOnce = String(option, "optionId");
        }
      }
    }
    _permissions[requestId] = new PendingPermission(
      active.QwenSessionId,
      active.ClientId,
      allowOnce,
      rejectOnce
    );
    var paths = Paths(toolCall);
    var tool = ToolName(toolCall);
    var readOnlyPermission = allowOnce is not null && IsReadOnlyPermission(tool);
    var approvalCanBeMapped = !readOnlyPermission && allowOnce is not null && paths.Count > 0;
    return Event(
      "approval.requested",
      active,
      message: String(toolCall, "title") ?? "Qwen Code requests workspace permission.",
      tool: tool,
      approvalId: requestId,
      approvalCanBeMapped: approvalCanBeMapped,
      destructive: string.Equals(String(toolCall, "kind"), "delete", StringComparison.Ordinal),
      paths: paths,
      readOnlyPermission: readOnlyPermission,
      native: payload
    );
  }

  private async Task EnsureStartedAsync(
    Uri ollamaEndpoint,
    string model,
    int contextWindowTokens,
    string workingDirectory,
    HarnessMcpClientConfiguration bridge,
    HostCapabilityProfile hostProfile,
    IReadOnlyList<CanonicalToolDefinition> hostBridgeTools,
    bool useMinimalToolInventory,
    CancellationToken cancellationToken
  )
  {
    await _lifecycleGate.WaitAsync(cancellationToken);
    try
    {
      var toolInventoryKey = useMinimalToolInventory
        ? hostProfile.Signature
        : "full-native-inventory";
      var key = $"{ollamaEndpoint.GetLeftPart(UriPartial.Authority)}|{model}|{contextWindowTokens}|{Path.GetFullPath(workingDirectory)}|{toolInventoryKey}";
      if (_process is { HasExited: false } && string.Equals(key, _configurationKey, StringComparison.Ordinal))
      {
        return;
      }
      await StopOwnedProcessAsync();
      Directory.CreateDirectory(_options.RuntimeDirectory);
      await WriteConfigurationAsync(
        ollamaEndpoint,
        model,
        contextWindowTokens,
        bridge,
        hostProfile,
        hostBridgeTools,
        useMinimalToolInventory,
        cancellationToken
      );
      var port = ReservePort();
      var command = ResolveCommand();
      _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
      _serverUri = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
      var startInfo = CreateStartInfo(command, _options.RuntimeDirectory);
      foreach (var argument in new[]
      {
        "serve",
        "--no-chat-recording",
        "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--hostname", "127.0.0.1",
        "--require-auth",
        "--no-web",
        "--max-sessions", "4",
        "--max-pending-prompts-per-session", "1",
        "--workspace", Path.GetFullPath(workingDirectory),
        "--memory-project-scope", "workspace",
        "--prompt-deadline-ms", ((long)_options.RequestTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--permission-response-timeout-ms", ((long)_options.RequestTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--channel-idle-timeout-ms", "60000"
      })
      {
        startInfo.ArgumentList.Add(argument);
      }
      SetRuntimeEnvironment(startInfo, _token, bridge.AuthorizationToken);
      var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
      ClearProcessOutput();
      if (!process.Start())
      {
        throw new InvalidOperationException("Process.Start returned false.");
      }
      _process = process;
      _configurationKey = key;
      _sessions.Clear();
      _ = DrainAsync(process.StandardOutput, process, "stdout");
      _ = DrainAsync(process.StandardError, process, "stderr");
      await WaitForReadyAsync(workingDirectory, cancellationToken);
    }
    catch (Exception exception) when (exception is not HarnessException)
    {
      await StopOwnedProcessAsync();
      throw new HarnessException(
        "qwen-code-start-failed",
        "Qwen Code could not start its isolated daemon.",
        Truncate(exception.Message),
        true,
        exception,
        HarnessIds.QwenCode
      );
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  private async Task WaitForReadyAsync(
    string workingDirectory,
    CancellationToken cancellationToken
  )
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(_options.StartupTimeout);
    while (!timeout.IsCancellationRequested)
    {
      if (_process is null || _process.HasExited)
      {
        var diagnostic = RecentProcessOutput();
        throw Failure(
          "qwen-code-daemon-exited",
          string.IsNullOrWhiteSpace(diagnostic)
            ? "Qwen Code exited during startup."
            : $"Qwen Code exited during startup. {diagnostic}"
        );
      }
      try
      {
        var health = await SendAsync(HttpMethod.Get, "health", null, null, timeout.Token);
        if (!string.Equals(String(health, "status"), "ok", StringComparison.Ordinal))
        {
          throw new InvalidOperationException("Health response was not ok.");
        }
        var capabilities = await SendAsync(HttpMethod.Get, "capabilities", null, null, timeout.Token);
        ValidateCapabilities(capabilities, workingDirectory);
        return;
      }
      catch (Exception exception) when (
        (exception is HttpRequestException or TaskCanceledException)
        && !cancellationToken.IsCancellationRequested
      )
      {
      }
      await Task.Delay(100, timeout.Token);
    }
    throw Failure("qwen-code-start-timeout", "Qwen Code did not become healthy before timeout.");
  }

  private static void ValidateCapabilities(JsonElement capabilities, string workingDirectory)
  {
    if (
      Long(capabilities, "v") != 1
      || !string.Equals(String(capabilities, "protocolVersions", "current"), "v1", StringComparison.Ordinal)
    )
    {
      throw Failure("qwen-code-protocol-unsupported", "Qwen Code daemon protocol v1 is required.");
    }
    if (!string.Equals(
      Path.GetFullPath(String(capabilities, "workspaceCwd") ?? string.Empty),
      Path.GetFullPath(workingDirectory),
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw Failure("qwen-code-workspace-mismatch", "Qwen Code registered a different workspace.");
    }
    var features = capabilities.GetProperty("features").EnumerateArray()
      .Select(item => item.GetString())
      .Where(item => item is not null)
      .ToHashSet(StringComparer.Ordinal);
    var missing = RequiredFeatures.Where(feature => !features.Contains(feature)).ToArray();
    if (missing.Length > 0)
    {
      throw Failure(
        "qwen-code-capabilities-missing",
        $"Qwen Code is missing required daemon capabilities: {string.Join(", ", missing)}."
      );
    }
  }

  private async Task<QwenSession> GetOrCreateSessionAsync(
    HarnessTurnRequest request,
    CancellationToken cancellationToken
  )
  {
    if (_sessions.TryGetValue(request.SessionId, out var existing))
    {
      return existing;
    }
    var requestedClientId = $"agentic-router-{Guid.NewGuid():N}";
    var created = await CreateSessionAsync(
      request.WorkingDirectory,
      requestedClientId,
      cancellationToken
    );
    var sessionId = RequiredString(created, "sessionId");
    var clientId = RequiredString(created, "clientId");
    var returnedWorkspace = RequiredString(created, "workspaceCwd");
    if (!string.Equals(
      Path.GetFullPath(returnedWorkspace),
      Path.GetFullPath(request.WorkingDirectory),
      StringComparison.OrdinalIgnoreCase
    ))
    {
      throw Failure("qwen-code-workspace-mismatch", "Qwen Code returned a different session workspace.");
    }
    var context = await SendAsync(
      HttpMethod.Get,
      $"session/{EncodePath(sessionId)}/context",
      null,
      clientId,
      cancellationToken
    );
    var currentModel = RequiredString(context.GetProperty("state").GetProperty("models"), "currentModelId");
    var availableModels = context.GetProperty("state").GetProperty("models").GetProperty("availableModels");
    if (
      availableModels.ValueKind != JsonValueKind.Array
      || !availableModels.EnumerateArray().Any(
        candidate => string.Equals(String(candidate, "modelId"), currentModel, StringComparison.Ordinal)
      )
    )
    {
      throw Failure("qwen-code-model-mismatch", "Qwen Code session context did not retain the selected model route.");
    }
    var providers = await SendAsync(
      HttpMethod.Get,
      "workspace/providers",
      null,
      null,
      cancellationToken
    );
    ValidateSelectedProvider(
      providers,
      currentModel,
      request.Model,
      request.ProviderEndpoint!,
      request.WorkingDirectory
    );
    var session = new QwenSession(sessionId, clientId);
    _sessions[request.SessionId] = session;
    return session;
  }

  private async Task<JsonElement> CreateSessionAsync(
    string workingDirectory,
    string requestedClientId,
    CancellationToken cancellationToken
  )
  {
    var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
    var delay = TimeSpan.FromMilliseconds(100);
    var attempt = 0;
    while (true)
    {
      attempt++;
      using var request = CreateRequest(HttpMethod.Post, "session", requestedClientId);
      request.Content = new StringContent(
        JsonSerializer.Serialize(new
        {
          cwd = Path.GetFullPath(workingDirectory),
          sessionScope = "thread"
        }),
        Encoding.UTF8,
        "application/json"
      );
      using var response = await Client().SendAsync(request, cancellationToken);
      var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
      if (response.IsSuccessStatusCode)
      {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
      }
      var body = Truncate(Encoding.UTF8.GetString(bytes));
      var runtimeStarting = response.StatusCode == HttpStatusCode.ServiceUnavailable
        && body.Contains("\"code\":\"daemon_runtime_starting\"", StringComparison.Ordinal);
      if (!runtimeStarting)
      {
        throw Failure(
          "qwen-code-http",
          $"Qwen Code returned HTTP {(int)response.StatusCode} ({response.StatusCode}). {body}"
        );
      }
      if (DateTimeOffset.UtcNow + delay > deadline)
      {
        throw Failure(
          "qwen-code-daemon-runtime-start-timeout",
          "Qwen Code did not finish starting its workspace runtime before the startup timeout."
        );
      }
      _logger.LogInformation(
        "Qwen Code workspace runtime is still starting; retrying session creation after {DelayMilliseconds} ms (attempt {Attempt}).",
        delay.TotalMilliseconds,
        attempt
      );
      await Task.Delay(delay, cancellationToken);
      delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 500));
    }
  }

  private async Task WriteConfigurationAsync(
    Uri endpoint,
    string model,
    int contextWindowTokens,
    HarnessMcpClientConfiguration bridge,
    HostCapabilityProfile hostProfile,
    IReadOnlyList<CanonicalToolDefinition> hostBridgeTools,
    bool useMinimalToolInventory,
    CancellationToken cancellationToken
  )
  {
    Directory.CreateDirectory(_options.RuntimeDirectory);
    var path = Path.Combine(_options.RuntimeDirectory, "settings.json");
    var hostMcpServer = new
    {
      httpUrl = bridge.Endpoint.AbsoluteUri,
      headers = new Dictionary<string, string>
      {
        ["Authorization"] = $"Bearer ${{{bridge.AuthorizationEnvironmentVariable}}}"
      },
      timeout = (long)_options.RequestTimeout.TotalMilliseconds,
      trust = true,
      includeTools = (useMinimalToolInventory
          ? hostBridgeTools
          : MaximumHostBridgeTools(HarnessIds.QwenCode))
        .Select(tool => tool.Name)
        .ToArray()
    };
    var config = new Dictionary<string, object?>
    {
      ["$version"] = 4,
      ["model"] = new
      {
        name = model,
        maxWallTimeSeconds = Math.Max(1, (int)_options.RequestTimeout.TotalSeconds)
      },
      ["modelProviders"] = new Dictionary<string, object>
      {
        ["openai"] = new[]
        {
          new
          {
            id = model,
            name = model,
            envKey = "OLLAMA_API_KEY",
            baseUrl = $"{endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/v1",
            generationConfig = new
            {
              contextWindowSize = contextWindowTokens,
              timeout = (long)_options.RequestTimeout.TotalMilliseconds,
              maxRetries = 0
            }
          }
        }
      },
      ["security"] = new
      {
        auth = new
        {
          selectedType = "openai"
        }
      },
      ["tools"] = new
      {
        approvalMode = "default",
        core = useMinimalToolInventory
          ? MinimalCoreTools(hostProfile)
          : DefaultCoreTools
      },
      ["permissions"] = new
      {
        deny = new[]
        {
          "read_file",
          "list_directory",
          "glob",
          "grep_search",
          "edit",
          "write_file",
          "run_shell_command",
          "agent",
          "skill",
          "image_gen",
          "save_memory",
          "ask_user_question",
          "enter_worktree",
          "exit_worktree",
          "workflow",
          "cron_create",
          "cron_list",
          "cron_delete"
        }
      },
      ["privacy"] = new { usageStatisticsEnabled = false },
      ["general"] = new
      {
        enableAutoUpdate = false,
        showSessionRecap = false,
        outputLanguage = "English"
      },
      ["memory"] = new
      {
        enableManagedAutoMemory = false,
        enableManagedAutoDream = false,
        enableAutoSkill = false,
        enableTeamMemory = false,
        enableTeamMemorySync = false
      },
      ["disableAllHooks"] = true,
      ["mcp"] = new
      {
        allowed = new[] { "agentic_router" },
        excluded = Array.Empty<string>()
      },
      ["mcpServers"] = new Dictionary<string, object>
      {
        ["agentic_router"] = hostMcpServer
      }
    };
    var content = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    var temporary = path + ".tmp";
    await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
    File.Move(temporary, path, true);
  }

  private static bool MatchesOllamaV1Endpoint(string? actual, Uri providerEndpoint)
  {
    if (!Uri.TryCreate(actual, UriKind.Absolute, out var actualUri))
    {
      return false;
    }
    var expected = new Uri(
      $"{providerEndpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/v1",
      UriKind.Absolute
    );
    return Uri.Compare(
      actualUri,
      expected,
      UriComponents.SchemeAndServer | UriComponents.Path,
      UriFormat.Unescaped,
      StringComparison.OrdinalIgnoreCase
    ) == 0;
  }

  private static void ValidateSelectedProvider(
    JsonElement status,
    string currentModelRoute,
    string expectedModel,
    Uri providerEndpoint,
    string workingDirectory
  )
  {
    if (
      Long(status, "v") != 1
      || !string.Equals(
        Path.GetFullPath(RequiredString(status, "workspaceCwd")),
        Path.GetFullPath(workingDirectory),
        StringComparison.OrdinalIgnoreCase
      )
      || !string.Equals(String(status, "current", "authType"), "openai", StringComparison.Ordinal)
      || !string.Equals(
        String(status, "current", "modelId"),
        currentModelRoute,
        StringComparison.Ordinal
      )
      || !MatchesOllamaV1Endpoint(String(status, "current", "baseUrl"), providerEndpoint)
    )
    {
      throw Failure("qwen-code-model-mismatch", "Qwen Code provider status does not match the selected local model.");
    }
    if (!status.TryGetProperty("providers", out var providers)
      || providers.ValueKind != JsonValueKind.Array)
    {
      throw Failure("qwen-code-model-mismatch", "Qwen Code did not report its model provider catalog.");
    }
    var matched = providers.EnumerateArray()
      .Where(provider => string.Equals(String(provider, "authType"), "openai", StringComparison.Ordinal))
      .SelectMany(
        provider => provider.TryGetProperty("models", out var models)
          && models.ValueKind == JsonValueKind.Array
            ? models.EnumerateArray().Select(model => model.Clone())
            : []
      )
      .Any(
        model => string.Equals(String(model, "modelId"), currentModelRoute, StringComparison.Ordinal)
          && string.Equals(String(model, "baseModelId"), expectedModel, StringComparison.Ordinal)
          && model.TryGetProperty("isCurrent", out var isCurrent)
          && isCurrent.ValueKind == JsonValueKind.True
          && MatchesOllamaV1Endpoint(String(model, "baseUrl"), providerEndpoint)
      );
    if (!matched)
    {
      throw Failure("qwen-code-model-mismatch", "Qwen Code did not confirm the exact selected Ollama model.");
    }
  }

  private ResolvedCommand ResolveCommand()
  {
    foreach (var candidate in new[] { _options.ExecutablePath, _options.ManagedCliPath })
    {
      if (string.IsNullOrWhiteSpace(candidate))
      {
        continue;
      }
      var fullPath = Path.GetFullPath(candidate);
      if (!File.Exists(fullPath))
      {
        if (string.Equals(candidate, _options.ExecutablePath, StringComparison.Ordinal))
        {
          throw Failure(
            "qwen-code-executable-not-found",
            $"Configured Qwen Code executable does not exist: {fullPath}"
          );
        }
        continue;
      }
      if (string.Equals(Path.GetExtension(fullPath), ".js", StringComparison.OrdinalIgnoreCase))
      {
        return new ResolvedCommand(FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node"), fullPath);
      }
      return new ResolvedCommand(fullPath, null);
    }
    var executable = FindOnPath(OperatingSystem.IsWindows() ? "qwen.exe" : "qwen", false);
    if (executable is not null)
    {
      return new ResolvedCommand(executable, null);
    }
    throw Failure("qwen-code-executable-not-found", "No Qwen Code executable was found.");
  }

  private static string FindOnPath(string executable, bool required = true)
  {
    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    foreach (var directory in path.Split(
      Path.PathSeparator,
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    ))
    {
      var candidate = Path.Combine(directory, executable);
      if (File.Exists(candidate))
      {
        return Path.GetFullPath(candidate);
      }
    }
    if (!required)
    {
      return null!;
    }
    throw Failure("qwen-code-node-not-found", "Qwen Code's Node.js runtime was not found on PATH.");
  }

  private ProcessStartInfo CreateStartInfo(ResolvedCommand command, string workingDirectory)
  {
    Directory.CreateDirectory(workingDirectory);
    var info = new ProcessStartInfo
    {
      FileName = command.FileName,
      WorkingDirectory = workingDirectory,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = new UTF8Encoding(false, true),
      StandardErrorEncoding = new UTF8Encoding(false, true)
    };
    if (command.ScriptPath is not null)
    {
      info.ArgumentList.Add(command.ScriptPath);
    }
    SetRuntimeEnvironment(info, null, null);
    return info;
  }

  private void SetRuntimeEnvironment(
    ProcessStartInfo info,
    string? token,
    string? hostBridgeToken
  )
  {
    info.Environment["QWEN_HOME"] = _options.RuntimeDirectory;
    info.Environment["QWEN_CODE_SYSTEM_SETTINGS_PATH"] = Path.Combine(
      _options.RuntimeDirectory,
      "settings.json"
    );
    info.Environment["OLLAMA_API_KEY"] = "ollama";
    info.Environment["NO_COLOR"] = "1";
    info.Environment["QWEN_CODE_NO_UPDATE_NOTIFIER"] = "1";
    if (hostBridgeToken is not null)
    {
      info.Environment[HarnessMcpHostBridge.AuthorizationEnvironmentVariable] = hostBridgeToken;
    }
    if (token is not null)
    {
      info.Environment["QWEN_SERVER_TOKEN"] = token;
    }
  }

  private static IReadOnlyList<CanonicalToolDefinition> MaximumHostBridgeTools(
    string harnessId
  )
  {
    var maximum = HostCapabilityProfile.Create(
      ExecutionTurnToolPolicy.Resolve(string.Empty, validationProfileAvailable: true),
      "auto"
    );
    return LocalActionPlanner.GetToolDefinitions(
      HarnessCapabilityProjection.HostBridgeTools(harnessId, maximum)
    );
  }

  private static IReadOnlyList<CanonicalToolDefinition> HostBridgeTools(
    HostCapabilityProfile profile
  )
  {
    return LocalActionPlanner.GetToolDefinitions(
      HarnessCapabilityProjection.HostBridgeTools(HarnessIds.QwenCode, profile)
    );
  }

  private static IReadOnlyList<string> ActiveNativeTools(
    HostCapabilityProfile profile
  )
  {
    return HarnessCapabilityProjection.NativeCommonTools(HarnessIds.QwenCode)
      .Where(profile.Allows)
      .ToArray();
  }

  private static string[] MinimalCoreTools(HostCapabilityProfile profile)
  {
    return [];
  }

  private async Task StopOwnedProcessAsync()
  {
    var process = _process;
    var sessions = _sessions.Values.ToArray();
    _process = null;
    _sessions.Clear();
    _permissions.Clear();
    _activeTurns.Clear();
    if (process is not null && !process.HasExited && _serverUri is not null && _token is not null)
    {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
      foreach (var session in sessions)
      {
        try
        {
          await SendAsync(
            HttpMethod.Delete,
            $"session/{EncodePath(session.SessionId)}",
            null,
            session.ClientId,
            timeout.Token,
            HttpStatusCode.NoContent
          );
        }
        catch (Exception)
        {
        }
      }
    }
    _serverUri = null;
    _token = null;
    _configurationKey = null;
    if (process is null)
    {
      return;
    }
    try
    {
      if (!process.HasExited)
      {
        process.Kill(true);
        await process.WaitForExitAsync();
      }
    }
    catch (InvalidOperationException)
    {
    }
    finally
    {
      process.Dispose();
    }
  }

  private async Task<JsonElement> SendAsync(
    HttpMethod method,
    string relativeUri,
    object? body,
    string? clientId,
    CancellationToken cancellationToken,
    HttpStatusCode expectedStatus = HttpStatusCode.OK
  )
  {
    using var request = CreateRequest(method, relativeUri, clientId);
    if (body is not null)
    {
      request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }
    using var response = await Client().SendAsync(request, cancellationToken);
    if (response.StatusCode != expectedStatus)
    {
      await EnsureSuccessAsync(response, "qwen-code-http", cancellationToken);
    }
    if (response.Content.Headers.ContentLength == 0 || expectedStatus == HttpStatusCode.NoContent)
    {
      using var empty = JsonDocument.Parse("null");
      return empty.RootElement.Clone();
    }
    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    using var document = JsonDocument.Parse(bytes);
    return document.RootElement.Clone();
  }

  private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri, string? clientId)
  {
    var request = new HttpRequestMessage(method, relativeUri);
    if (!string.IsNullOrWhiteSpace(clientId))
    {
      request.Headers.TryAddWithoutValidation("X-Qwen-Client-Id", clientId);
    }
    return request;
  }

  private HttpClient Client()
  {
    var client = _httpClients.CreateClient();
    client.BaseAddress = _serverUri ?? throw Failure("qwen-code-daemon-missing", "Qwen Code daemon is unavailable.");
    client.Timeout = _options.RequestTimeout;
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
      "Bearer",
      _token ?? throw Failure("qwen-code-auth-missing", "Qwen Code bearer token is unavailable.")
    );
    return client;
  }

  private static async Task EnsureSuccessAsync(
    HttpResponseMessage response,
    string stage,
    CancellationToken cancellationToken
  )
  {
    if (response.IsSuccessStatusCode)
    {
      return;
    }
    var body = Truncate(await response.Content.ReadAsStringAsync(cancellationToken));
    throw Failure(
      stage,
      $"Qwen Code returned HTTP {(int)response.StatusCode} ({response.StatusCode}). {body}"
    );
  }

  private async Task<string?> ReadEventLineAsync(
    StreamReader reader,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await reader.ReadLineAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or HttpRequestException)
    {
      var exited = _process is null || _process.HasExited;
      throw new HarnessException(
        exited ? "qwen-code-daemon-exited" : "qwen-code-event-stream-failed",
        exited
          ? "Qwen Code exited before the active turn completed."
          : "The Qwen Code event stream failed before terminal state.",
        Truncate(exception.Message),
        true,
        exception,
        HarnessIds.QwenCode
      );
    }
  }

  private async Task DrainAsync(StreamReader reader, Process process, string stream)
  {
    try
    {
      while (await reader.ReadLineAsync() is { } line)
      {
        var sanitized = Truncate(line);
        lock (_processOutputGate)
        {
          _recentProcessOutput.Enqueue($"{stream}: {sanitized}");
          while (_recentProcessOutput.Count > 8)
          {
            _recentProcessOutput.Dequeue();
          }
        }
        _logger.LogDebug("Qwen Code {Stream}: {Line}", stream, sanitized);
      }
    }
    catch (Exception exception) when (exception is IOException or InvalidOperationException)
    {
      _logger.LogDebug(exception, "Qwen Code {Stream} drain ended.", stream);
    }
  }

  private void ClearProcessOutput()
  {
    lock (_processOutputGate)
    {
      _recentProcessOutput.Clear();
    }
  }

  private string RecentProcessOutput()
  {
    lock (_processOutputGate)
    {
      return Truncate(string.Join(" | ", _recentProcessOutput));
    }
  }

  private static HarnessEvent Event(
    string type,
    ActiveTurn active,
    string? message = null,
    string? delta = null,
    string? itemId = null,
    string? tool = null,
    string? state = null,
    string? output = null,
    string? approvalId = null,
    bool approvalCanBeMapped = false,
    bool destructive = false,
    string? errorCode = null,
    IReadOnlyList<string>? paths = null,
    HarnessTerminalState? terminal = null,
    JsonElement? native = null,
    long? contextInputTokens = null,
    long? contextTotalTokens = null,
    bool readOnlyPermission = false
  )
  {
    return new HarnessEvent(
      type,
      message,
      delta,
      itemId,
      tool,
      state,
      output,
      approvalId,
      approvalCanBeMapped,
      destructive,
      errorCode,
      paths,
      harnessId: HarnessIds.QwenCode,
      sessionId: active.QwenSessionId,
      turnId: active.PromptId,
      terminalState: terminal,
      nativePayload: native?.Clone(),
      contextInputTokens: contextInputTokens,
      contextTotalTokens: contextTotalTokens,
      readOnlyPermission: readOnlyPermission
    );
  }

  private static bool MatchesPrompt(ActiveTurn active, JsonElement data)
  {
    return string.Equals(String(data, "sessionId"), active.QwenSessionId, StringComparison.Ordinal)
      && string.Equals(String(data, "promptId"), active.PromptId, StringComparison.Ordinal);
  }

  private static IReadOnlyList<string> Paths(JsonElement toolCall)
  {
    if (
      toolCall.ValueKind != JsonValueKind.Object
      || !toolCall.TryGetProperty("locations", out var locations)
      || locations.ValueKind != JsonValueKind.Array
    )
    {
      return [];
    }
    return locations.EnumerateArray()
      .Select(location => String(location, "path"))
      .Where(path => !string.IsNullOrWhiteSpace(path))
      .Cast<string>()
      .ToArray();
  }

  private static string ToolName(JsonElement value)
  {
    return String(value, "_meta", "toolName")
      ?? String(value, "title")
      ?? String(value, "kind")
      ?? "qwen_code_tool";
  }

  private static bool IsReadOnlyPermission(string tool)
  {
    return tool is "list_directory" or "read_file" or "glob" or "grep_search"
      or "web_fetch" or "web_search" or "computer_use__list_apps";
  }

  private static string? ContentText(JsonElement value)
  {
    if (!value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
    {
      return null;
    }
    return Truncate(string.Join(
      "\n",
      content.EnumerateArray()
        .Select(item => String(item, "content", "text") ?? String(item, "text"))
        .Where(text => !string.IsNullOrWhiteSpace(text))
    ));
  }

  private static string? Json(JsonElement value, string property)
  {
    if (!value.TryGetProperty(property, out var element))
    {
      return null;
    }
    return element.ValueKind == JsonValueKind.String
      ? Truncate(element.GetString())
      : Truncate(element.GetRawText());
  }

  private static string RequiredString(JsonElement value, string property)
  {
    return String(value, property)
      ?? throw Failure("qwen-code-protocol-field", $"Qwen Code omitted required field {property}.");
  }

  private static string? String(JsonElement value, params string[] path)
  {
    var current = value;
    foreach (var segment in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
      {
        return null;
      }
    }
    return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
  }

  private static long? Long(JsonElement value, params string[] path)
  {
    var current = value;
    foreach (var segment in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
      {
        return null;
      }
    }
    return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var result)
      ? result
      : null;
  }

  private static void Validate(HarnessTurnRequest request)
  {
    if (!string.Equals(request.HarnessId, HarnessIds.QwenCode, StringComparison.OrdinalIgnoreCase))
    {
      throw Failure("qwen-code-request-invalid", "The Qwen Code adapter received another harness id.");
    }
    if (!string.Equals(request.Provider, "ollama-local", StringComparison.OrdinalIgnoreCase))
    {
      throw Failure("qwen-code-provider-unsupported", "Qwen Code supports Ollama Local models only.");
    }
    if (request.ProviderEndpoint is null || string.IsNullOrWhiteSpace(request.Model))
    {
      throw Failure("qwen-code-request-invalid", "Qwen Code requires an Ollama endpoint and exact model.");
    }
    if (!Directory.Exists(Path.GetFullPath(request.WorkingDirectory)))
    {
      throw Failure("qwen-code-workspace-invalid", "The trusted workspace is unavailable.");
    }
  }

  private static int ReservePort()
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  private static string EncodePath(string value)
  {
    return Uri.EscapeDataString(value);
  }

  private static bool StringArrayContains(
    JsonElement value,
    string property,
    string expected
  )
  {
    return value.ValueKind == JsonValueKind.Object
      && value.TryGetProperty(property, out var items)
      && items.ValueKind == JsonValueKind.Array
      && items.EnumerateArray().Any(
        item => string.Equals(item.GetString(), expected, StringComparison.Ordinal)
      );
  }

  private static HarnessException Failure(string code, string message)
  {
    return new HarnessException(code, message, message, true, harnessId: HarnessIds.QwenCode);
  }

  private static string Truncate(string? value)
  {
    if (string.IsNullOrEmpty(value) || value.Length <= MaximumActivityText)
    {
      return value ?? string.Empty;
    }
    return value[..MaximumActivityText] + "\n[truncated]";
  }

  private sealed record ResolvedCommand(string FileName, string? ScriptPath);

  private sealed class QwenSession(string sessionId, string clientId)
  {
    public string SessionId { get; } = sessionId;

    public string ClientId { get; } = clientId;

    public long? SynchronizedThroughVersion { get; set; }
  }

  private sealed record PendingPermission(
    string SessionId,
    string ClientId,
    string? AllowOnceOptionId,
    string? RejectOnceOptionId
  );

  private sealed class TextDeltaCoalescer
  {
    private const int MaximumCharacters = 256;
    private static readonly TimeSpan MaximumLatency = TimeSpan.FromMilliseconds(50);
    private readonly StringBuilder _buffer = new();
    private readonly HashSet<string> _startedStreams = new(StringComparer.Ordinal);
    private HarnessEvent? _pending;
    private long _startedAt;

    public IReadOnlyList<HarnessEvent> Accept(HarnessEvent value)
    {
      if (!IsTextDelta(value) || string.IsNullOrEmpty(value.Delta))
      {
        var ready = Flush().ToList();
        ready.Add(value);
        return ready;
      }

      var streamKey = $"{value.Type}\0{value.ItemId}";
      if (_startedStreams.Add(streamKey))
      {
        var ready = Flush().ToList();
        ready.Add(value with { NativePayload = null });
        return ready;
      }

      var readyText = new List<HarnessEvent>();
      if (
        _pending is not null
        && (
          !string.Equals(_pending.Type, value.Type, StringComparison.Ordinal)
          || !string.Equals(_pending.ItemId, value.ItemId, StringComparison.Ordinal)
          || Stopwatch.GetElapsedTime(_startedAt) >= MaximumLatency
        )
      )
      {
        readyText.AddRange(Flush());
      }

      if (_pending is null)
      {
        _pending = value;
        _startedAt = Stopwatch.GetTimestamp();
      }
      _buffer.Append(value.Delta);

      if (_buffer.Length >= MaximumCharacters)
      {
        readyText.AddRange(Flush());
      }
      return readyText;
    }

    public IReadOnlyList<HarnessEvent> Flush()
    {
      if (_pending is null)
      {
        return [];
      }
      var ready = _pending with
      {
        Delta = _buffer.ToString(),
        NativePayload = null
      };
      _pending = null;
      _buffer.Clear();
      _startedAt = 0;
      return [ready];
    }

    private static bool IsTextDelta(HarnessEvent value)
    {
      return string.Equals(value.Type, "assistant.delta", StringComparison.Ordinal)
        || string.Equals(value.Type, "reasoning.delta", StringComparison.Ordinal);
    }
  }

  private sealed class ActiveTurn
  {
    public ActiveTurn(
      string conversationId,
      string qwenSessionId,
      string clientId,
      string workingDirectory
    )
    {
      ConversationId = conversationId;
      QwenSessionId = qwenSessionId;
      ClientId = clientId;
      WorkingDirectory = workingDirectory;
    }

    public string ConversationId { get; }

    public string QwenSessionId { get; }

    public string ClientId { get; }

    public string WorkingDirectory { get; }

    public string? PromptId { get; set; }
  }
}
