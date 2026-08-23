using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AgenticRouter.Api.Execution;

public sealed record ClaudeCodeHarnessOptions(
  string? ExecutablePath,
  string? ManagedExecutablePath,
  string RuntimeDirectory,
  TimeSpan StartupTimeout,
  TimeSpan RequestTimeout
);

public sealed class ClaudeCodeHarnessAdapter : IAgentHarness, IAgentHarnessTransport
{
  private const int MaximumActivityText = 8_192;
  private const int MinimumStreamChunkLength = 128;
  private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromMinutes(1);
  private static readonly string[] NativeTools = ["Read", "Glob", "Grep", "Edit", "Write"];
  private static readonly string[] ReadOnlyTools = ["Read", "Glob", "Grep"];
  private static readonly HarnessDefinition AdapterDefinition = new(
    HarnessIds.ClaudeCode,
    "Claude Code",
    true,
    "Claude Code Agent SDK stream protocol with exact Ollama routing and Host-observed effects.",
    new HarnessCapabilities(
      SupportsStreaming: true,
      SupportsThinking: true,
      SupportsResume: true,
      SupportsCancel: true,
      SupportsApprovals: true,
      SupportsToolEvents: true,
      SupportsStructuredEdits: true,
      SupportsStaleProtection: false,
      SupportsSubagents: false,
      SupportsSandbox: false,
      SupportsSessionDiff: false,
      SupportsNativePermissions: true
    ),
    ["ollama-local"]
  );

  private readonly ClaudeCodeHarnessOptions _options;
  private readonly HarnessMcpHostBridge _hostTools;
  private readonly ILogger<ClaudeCodeHarnessAdapter> _logger;
  private readonly SemaphoreSlim _availabilityGate = new(1, 1);
  private readonly SemaphoreSlim _turnGate = new(1, 1);
  private readonly ConcurrentDictionary<string, HarnessSession> _sessions = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, ActiveTurn> _activeTurns = new(StringComparer.Ordinal);
  private HarnessAvailability? _cachedAvailability;
  private bool _disposed;

  public ClaudeCodeHarnessAdapter(
    ClaudeCodeHarnessOptions options,
    HarnessMcpHostBridge hostTools,
    ILogger<ClaudeCodeHarnessAdapter> logger
  )
  {
    _options = options;
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
      Directory.CreateDirectory(_options.RuntimeDirectory);
      var executable = ResolveExecutable();
      var startInfo = BaseStartInfo(executable, _options.RuntimeDirectory);
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
      try
      {
        await process.WaitForExitAsync(timeout.Token);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        if (!process.HasExited)
        {
          process.Kill(true);
          await process.WaitForExitAsync(CancellationToken.None);
        }
        throw new TimeoutException("Claude Code version detection timed out.");
      }
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
      _logger.LogInformation(exception, "Claude Code harness is unavailable.");
      _cachedAvailability = HarnessAvailability.Missing(
        "Claude Code executable was not found or could not be started."
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
    Task? run = null;
    try
    {
      var hostProfile = request.HostCapabilities ?? throw Failure(
        "claude-code-host-profile-missing",
        "Claude Code requires the Agentic Router Host capability profile."
      );
      var bridgeTools = LocalActionPlanner.GetToolDefinitions(
        HarnessCapabilityProjection.HostBridgeTools(HarnessIds.ClaudeCode, hostProfile)
      );
      var bridge = await _hostTools.ConfigureClientAsync(
        HarnessIds.ClaudeCode,
        bridgeTools,
        cancellationToken
      );
      var session = _sessions.AddOrUpdate(
        request.SessionId,
        _ => new HarnessSession(
          Guid.NewGuid().ToString(),
          request.Model,
          Path.GetFullPath(request.WorkingDirectory),
          hostProfile.Signature
        ),
        (_, current) => current.Matches(request, hostProfile)
          ? current
          : new HarnessSession(
            Guid.NewGuid().ToString(),
            request.Model,
            Path.GetFullPath(request.WorkingDirectory),
            hostProfile.Signature
          )
      );
      var turnId = $"claude-turn-{Guid.NewGuid():N}";
      active = new ActiveTurn(request.SessionId, session.NativeSessionId, turnId);
      if (!_activeTurns.TryAdd(request.SessionId, active))
      {
        throw Failure(
          "claude-code-session-busy",
          "Claude Code is already running a turn for this conversation."
        );
      }
      using var registration = cancellationToken.Register(
        static state => ((ActiveTurn)state!).RequestCancellation(),
        active
      );
      run = RunTurnAsync(active, session, request, hostProfile, bridge);

      await foreach (var harnessEvent in active.Events.Reader.ReadAllAsync(cancellationToken))
      {
        yield return harnessEvent;
      }
      await run;
    }
    finally
    {
      if (active is not null)
      {
        active.RequestCancellation();
        _activeTurns.TryRemove(request.SessionId, out _);
      }
      if (run is not null)
      {
        try
        {
          await run;
        }
        catch (OperationCanceledException)
        {
        }
      }
      active?.Dispose();
      if (request.ReleaseWorkspaceAfterTurn)
      {
        _sessions.TryRemove(request.SessionId, out _);
      }
      _turnGate.Release();
    }
  }

  public Task ResolveApprovalAsync(
    string approvalId,
    bool approved,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    foreach (var active in _activeTurns.Values)
    {
      if (active.ResolveApproval(approvalId, approved))
      {
        return Task.CompletedTask;
      }
    }
    throw Failure(
      "claude-code-approval-stale",
      "The Claude Code permission request is no longer pending."
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
      HarnessIds.ClaudeCode,
      toolCallId,
      succeeded,
      output,
      cancellationToken
    );
  }

  public Task CancelTurnAsync(
    string sessionId,
    CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (_activeTurns.TryGetValue(sessionId, out var active))
    {
      active.RequestCancellation();
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
    foreach (var active in _activeTurns.Values)
    {
      active.RequestCancellation();
    }
    await Task.WhenAll(_activeTurns.Values.Select(active => active.Completion.Task));
    foreach (var active in _activeTurns.Values)
    {
      active.Dispose();
    }
    _activeTurns.Clear();
    _availabilityGate.Dispose();
    _turnGate.Dispose();
  }

  private async Task RunTurnAsync(
    ActiveTurn active,
    HarnessSession session,
    HarnessTurnRequest request,
    HostCapabilityProfile hostProfile,
    HarnessMcpClientConfiguration bridge
  )
  {
    using var timeout = new CancellationTokenSource(_options.RequestTimeout);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
      timeout.Token,
      active.Lifetime.Token
    );
    var cancellationToken = linked.Token;
    Process? process = null;
    Task? errorDrain = null;
    Task? hostRelay = null;
    try
    {
      Directory.CreateDirectory(_options.RuntimeDirectory);
      var nativeToolNames = ActiveNativeTools(request, hostProfile);
      var turnPrompt = HarnessConversationPromptBuilder.Create(
        request,
        session.SynchronizedThroughVersion,
        [
          $"Claude native workspace tools intentionally available: {string.Join(", ", nativeToolNames)}.",
          $"Agentic Router Host bridge tools intentionally available: {string.Join(", ", HarnessCapabilityProjection.HostBridgeTools(HarnessIds.ClaudeCode, hostProfile))}.",
          $"Host approval policy: {hostProfile.ApprovalPolicy}. Use Host tools for structured delete, process execution, validation, Git, and other capabilities not present in the native list."
        ]
      );
      var startInfo = CreateTurnStartInfo(
        request,
        session,
        bridge,
        nativeToolNames
      );
      process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
      if (!process.Start())
      {
        throw Failure("claude-code-start-failed", "Claude Code could not be started.");
      }
      active.Attach(process);
      errorDrain = DrainErrorAsync(process, active, cancellationToken);
      using var hostTurn = _hostTools.BeginTurn(
        HarnessIds.ClaudeCode,
        session.NativeSessionId,
        active.TurnId,
        hostProfile
      );
      hostRelay = RelayHostEventsAsync(hostTurn.Events, active, cancellationToken);

      active.Write(Event(active, "turn.started", message: $"Claude Code session {session.NativeSessionId} started."));
      await WriteAsync(
        process.StandardInput,
        new
        {
          type = "control_request",
          request_id = active.InitializeRequestId,
          request = new { subtype = "initialize", hooks = (object?)null, skills = Array.Empty<string>() }
        },
        cancellationToken
      );
      await WaitForInitializeAsync(process.StandardOutput, active, request, cancellationToken);
      await WriteAsync(
        process.StandardInput,
        new
        {
          type = "user",
          message = new { role = "user", content = turnPrompt.Text },
          parent_tool_use_id = (string?)null,
          session_id = "default"
        },
        cancellationToken
      );

      while (!active.IsTerminal)
      {
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null)
        {
          break;
        }
        JsonElement payload;
        try
        {
          using var document = JsonDocument.Parse(line);
          payload = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
          throw new HarnessException(
            "claude-code-protocol-json",
            "Claude Code returned malformed structured event data.",
            Truncate(exception.Message),
            true,
            exception,
            HarnessIds.ClaudeCode
          );
        }

        if (String(payload, "type") == "control_request")
        {
          if (IsControlPermissionRequest(payload))
          {
            await HandlePermissionAsync(
              process.StandardInput,
              active,
              request,
              payload,
              cancellationToken
            );
          }
          else
          {
            await RejectUnsupportedControlRequestAsync(
              process.StandardInput,
              active,
              payload,
              cancellationToken
            );
          }
          continue;
        }
        foreach (var harnessEvent in MapFrame(active, request, session, payload))
        {
          if (harnessEvent.IsTerminal)
          {
            active.TryComplete(harnessEvent);
            break;
          }
          active.Write(harnessEvent);
        }
      }

      if (!active.IsTerminal)
      {
        WriteBufferedDeltas(active);
        if (active.CancellationRequested)
        {
          active.TryComplete(Event(
            active,
            "turn.cancelled",
            message: "Claude Code turn was cancelled.",
            terminalState: HarnessTerminalState.Cancelled
          ));
        }
        else
        {
          var exit = process.HasExited ? process.ExitCode : (int?)null;
          active.TryComplete(Event(
            active,
            "turn.failed",
            message: "Claude Code exited before a structured result was received.",
            output: active.LastError ?? (exit is null ? null : $"Exit code {exit}."),
            errorCode: "claude-code-terminal-missing",
            terminalState: HarnessTerminalState.Failed
          ));
        }
      }
      if (active.TerminalState == HarnessTerminalState.Completed)
      {
        session.SynchronizedThroughVersion = turnPrompt.SynchronizedThroughVersion;
      }
    }
    catch (OperationCanceledException)
    {
      WriteBufferedDeltas(active);
      active.TryComplete(Event(
        active,
        timeout.IsCancellationRequested && !active.CancellationRequested
          ? "turn.timed-out"
          : "turn.cancelled",
        message: timeout.IsCancellationRequested && !active.CancellationRequested
          ? "Claude Code exceeded the configured turn timeout."
          : "Claude Code turn was cancelled.",
        errorCode: timeout.IsCancellationRequested && !active.CancellationRequested
          ? "claude-code-timeout"
          : null,
        terminalState: timeout.IsCancellationRequested && !active.CancellationRequested
          ? HarnessTerminalState.TimedOut
          : HarnessTerminalState.Cancelled
      ));
    }
    catch (HarnessException exception)
    {
      WriteBufferedDeltas(active);
      active.TryComplete(Event(
        active,
        "turn.failed",
        message: exception.Message,
        output: Truncate(exception.TechnicalMessage),
        errorCode: exception.Code,
        terminalState: exception.Code is "claude-code-executable-not-found"
          ? HarnessTerminalState.Unavailable
          : HarnessTerminalState.Failed
      ));
    }
    catch (Exception exception)
    {
      _logger.LogWarning(exception, "Claude Code turn failed.");
      WriteBufferedDeltas(active);
      active.TryComplete(Event(
        active,
        "turn.failed",
        message: "Claude Code failed before completing the turn.",
        output: Truncate(exception.Message),
        errorCode: "claude-code-runtime",
        terminalState: HarnessTerminalState.Failed
      ));
    }
    finally
    {
      linked.Cancel();
      active.RequestProcessStop();
      if (hostRelay is not null)
      {
        await IgnoreCancellationAsync(hostRelay);
      }
      if (errorDrain is not null)
      {
        await IgnoreCancellationAsync(errorDrain);
      }
      process?.Dispose();
      active.Finish();
    }
  }

  private ProcessStartInfo CreateTurnStartInfo(
    HarnessTurnRequest request,
    HarnessSession session,
    HarnessMcpClientConfiguration bridge,
    IReadOnlyList<string> nativeToolNames
  )
  {
    var executable = ResolveExecutable();
    var info = BaseStartInfo(executable, request.WorkingDirectory);
    foreach (var argument in new[]
    {
      "--print",
      "--input-format", "stream-json",
      "--output-format", "stream-json",
      "--verbose",
      "--include-partial-messages",
      "--permission-prompt-tool", "stdio",
      "--model", request.Model,
      "--bare",
      "--disable-slash-commands",
      "--no-chrome",
      "--strict-mcp-config",
      "--tools", string.Join(',', nativeToolNames),
      "--permission-mode", "manual",
      "--append-system-prompt", "Use only the explicitly offered Claude native tools and Agentic Router Host bridge tools. Do not seek web, browser, shell, plugins, skills, subagents, or other MCP capabilities."
    })
    {
      info.ArgumentList.Add(argument);
    }

    var hostNames = HarnessCapabilityProjection.HostBridgeTools(
      HarnessIds.ClaudeCode,
      request.HostCapabilities!
    );
    if (hostNames.Count > 0)
    {
      var mcp = JsonSerializer.Serialize(new
      {
        mcpServers = new Dictionary<string, object>
        {
          ["agentic_router"] = new
          {
            type = "http",
            url = bridge.Endpoint.AbsoluteUri,
            headers = new Dictionary<string, string>
            {
              ["Authorization"] = $"Bearer ${{{bridge.AuthorizationEnvironmentVariable}}}"
            }
          }
        }
      });
      info.ArgumentList.Add("--mcp-config");
      info.ArgumentList.Add(mcp);
      info.ArgumentList.Add("--allowedTools");
      info.ArgumentList.Add(string.Join(',',
        hostNames.Select(name => $"mcp__agentic_router__{name}")
      ));
    }
    else
    {
      info.ArgumentList.Add("--mcp-config");
      info.ArgumentList.Add("{\"mcpServers\":{}}");
    }
    info.ArgumentList.Add(session.HasStarted
      ? $"--resume={session.NativeSessionId}"
      : $"--session-id={session.NativeSessionId}");

    info.Environment["ANTHROPIC_AUTH_TOKEN"] = "ollama";
    info.Environment["ANTHROPIC_API_KEY"] = string.Empty;
    info.Environment["ANTHROPIC_BASE_URL"] = request.ProviderEndpoint!.AbsoluteUri.TrimEnd('/');
    info.Environment["ANTHROPIC_MODEL"] = request.Model;
    info.Environment["CLAUDE_CONFIG_DIR"] = _options.RuntimeDirectory;
    info.Environment["CLAUDE_CODE_ENTRYPOINT"] = "agentic-router";
    info.Environment["CLAUDE_CODE_SUBPROCESS_ENV_SCRUB"] = "1";
    info.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
    info.Environment["DISABLE_AUTOUPDATER"] = "1";
    info.Environment["DISABLE_ERROR_REPORTING"] = "1";
    info.Environment["DISABLE_TELEMETRY"] = "1";
    info.Environment["NO_COLOR"] = "1";
    info.Environment.Remove("CLAUDE_CODE_USE_BEDROCK");
    info.Environment.Remove("CLAUDE_CODE_USE_VERTEX");
    info.Environment.Remove("CLAUDE_CODE_USE_FOUNDRY");
    info.Environment[bridge.AuthorizationEnvironmentVariable] = bridge.AuthorizationToken;
    return info;
  }

  private static async Task WaitForInitializeAsync(
    StreamReader output,
    ActiveTurn active,
    HarnessTurnRequest request,
    CancellationToken cancellationToken
  )
  {
    while (true)
    {
      var line = await output.ReadLineAsync(cancellationToken);
      if (line is null)
      {
        throw Failure(
          "claude-code-initialize-ended",
          "Claude Code ended before completing the Agent SDK initialization handshake."
        );
      }
      JsonElement payload;
      try
      {
        using var document = JsonDocument.Parse(line);
        payload = document.RootElement.Clone();
      }
      catch (JsonException exception)
      {
        throw new HarnessException(
          "claude-code-protocol-json",
          "Claude Code returned malformed initialization data.",
          exception.Message,
          true,
          exception,
          HarnessIds.ClaudeCode
        );
      }
      if (
        String(payload, "type") == "control_response"
        && payload.TryGetProperty("response", out var response)
        && String(response, "request_id") == active.InitializeRequestId
      )
      {
        if (String(response, "subtype") != "success")
        {
          throw Failure(
            "claude-code-initialize-rejected",
            String(response, "error") ?? "Claude Code rejected Agent SDK initialization."
          );
        }
        return;
      }
      if (String(payload, "type") == "system" && String(payload, "subtype") == "init")
      {
        _ = ValidateInit(payload, request, resumed: false);
      }
      active.Write(Event(active, "native.event", native: payload));
    }
  }

  private static async Task HandlePermissionAsync(
    StreamWriter input,
    ActiveTurn active,
    HarnessTurnRequest turnRequest,
    JsonElement payload,
    CancellationToken cancellationToken
  )
  {
    var requestId = RequiredString(payload, "request_id");
    var request = payload.GetProperty("request");
    var tool = RequiredString(request, "tool_name");
    var arguments = request.TryGetProperty("input", out var supplied)
      ? supplied.Clone()
      : EmptyObject();
    var paths = PermissionPaths(request, arguments);
    var readOnly = ReadOnlyTools.Contains(tool, StringComparer.Ordinal);
    var confinedRead = readOnly && PermissionPathsAreWorkspaceConfined(
      paths,
      turnRequest.WorkingDirectory
    );
    var mappable = confinedRead || (paths.Count > 0 && tool is "Edit" or "Write");
    var approval = new PendingApproval(requestId, arguments);
    if (!active.Approvals.TryAdd(requestId, approval))
    {
      throw Failure(
        "claude-code-approval-duplicate",
        "Claude Code repeated a pending permission identifier."
      );
    }
    active.Write(Event(
      active,
      "approval.requested",
      message: String(request, "description") ?? String(request, "decision_reason") ?? $"Claude Code requests {tool}.",
      tool: tool,
      approvalId: requestId,
      approvalCanBeMapped: mappable,
      paths: paths,
      destructive: false,
      readOnlyPermission: confinedRead,
      native: payload
    ));
    var approved = await approval.Decision.Task.WaitAsync(cancellationToken);
    active.Approvals.TryRemove(requestId, out _);
    await WriteAsync(
      input,
      new
      {
        type = "control_response",
        response = new
        {
          subtype = "success",
          request_id = requestId,
          response = approved
            ? (object)new { behavior = "allow", updatedInput = arguments }
            : new { behavior = "deny", message = "Agentic Router denied this native action." }
        }
      },
      cancellationToken
    );
  }

  private static IEnumerable<HarnessEvent> MapFrame(
    ActiveTurn active,
    HarnessTurnRequest request,
    HarnessSession session,
    JsonElement payload
  )
  {
    var type = String(payload, "type");
    ValidateFrameModel(payload, request, type);
    switch (type)
    {
      case "system":
        {
          var subtype = String(payload, "subtype");
          if (subtype == "init")
          {
            var resumed = session.HasStarted;
            var missingTools = ValidateInit(payload, request, resumed);
            session.HasStarted = true;
            if (missingTools.Count > 0)
            {
              yield return Event(
                active,
                "warning",
                message: $"Claude Code did not expose optional native tools: {string.Join(", ", missingTools)}. Agentic Router will keep the declared Host bridge available.",
                native: payload
              );
            }
            yield return Event(active, "native.event", native: payload);
            yield break;
          }
          if (subtype == "api_retry")
          {
            yield return Event(
              active,
              "warning",
              message: $"Claude Code is retrying the local provider request (attempt {Long(payload, "attempt") ?? 0}).",
              output: String(payload, "error"),
              native: payload
            );
            yield break;
          }
          if (subtype == "thinking_tokens")
          {
            yield break;
          }
          foreach (var buffered in FlushBufferedDeltas(active, payload))
          {
            yield return buffered;
          }
          yield return Event(active, "native.event", native: payload);
          yield break;
        }
      case "stream_event":
        foreach (var mapped in MapStreamEvent(active, payload))
        {
          yield return mapped;
        }
        yield break;
      case "assistant":
        foreach (var buffered in FlushBufferedDeltas(active, payload))
        {
          yield return buffered;
        }
        foreach (var mapped in MapMessageContent(active, payload, assistant: true))
        {
          yield return mapped;
        }
        yield break;
      case "user":
        foreach (var buffered in FlushBufferedDeltas(active, payload))
        {
          yield return buffered;
        }
        foreach (var mapped in MapMessageContent(active, payload, assistant: false))
        {
          yield return mapped;
        }
        yield break;
      case "result":
        {
          foreach (var buffered in FlushBufferedDeltas(active, payload))
          {
            yield return buffered;
          }
          var contextTokens = (Long(payload, "usage", "input_tokens") ?? 0)
            + (Long(payload, "usage", "cache_read_input_tokens") ?? 0)
            + (Long(payload, "usage", "cache_creation_input_tokens") ?? 0);
          if (contextTokens > 0)
          {
            yield return Event(
              active,
              "usage.updated",
              contextInputTokens: contextTokens,
              native: payload
            );
          }
          var reason = String(payload, "terminal_reason");
          var cancelled = reason is "aborted_streaming" or "aborted_tools";
          var failed = Bool(payload, "is_error") == true
            || !string.Equals(String(payload, "subtype"), "success", StringComparison.Ordinal);
          var message = String(payload, "result")
            ?? FirstString(payload, "errors")
            ?? (cancelled ? "Claude Code turn was cancelled." : failed
              ? "Claude Code reported a failed result."
              : "Claude Code completed the turn.");
          yield return Event(
            active,
            cancelled ? "turn.cancelled" : failed ? "turn.failed" : "turn.completed",
            message: Truncate(message),
            errorCode: failed ? $"claude-code-{String(payload, "subtype") ?? "result-error"}" : null,
            terminalState: cancelled
              ? HarnessTerminalState.Cancelled
              : failed ? HarnessTerminalState.Failed : HarnessTerminalState.Completed,
            native: payload
          );
          yield break;
        }
      case "control_response":
        yield break;
      default:
        foreach (var buffered in FlushBufferedDeltas(active, payload))
        {
          yield return buffered;
        }
        yield return Event(active, "native.event", native: payload);
        yield break;
    }
  }

  private static IEnumerable<HarnessEvent> MapStreamEvent(
    ActiveTurn active,
    JsonElement payload
  )
  {
    if (!payload.TryGetProperty("event", out var stream) || stream.ValueKind != JsonValueKind.Object)
    {
      yield return Event(active, "native.event", native: payload);
      yield break;
    }
    var eventType = String(stream, "type");
    var index = Long(stream, "index") ?? 0;
    var itemId = $"claude-block-{index}";
    if (
      eventType == "content_block_delta"
      && stream.TryGetProperty("delta", out var delta)
    )
    {
      var deltaType = String(delta, "type");
      if (deltaType == "text_delta")
      {
        active.SawTextDelta = true;
        var buffered = active.AppendTextDelta(String(delta, "text"), itemId);
        if (buffered is not null)
        {
          yield return Event(
            active,
            "assistant.delta",
            delta: buffered.Text,
            itemId: buffered.ItemId,
            native: payload
          );
        }
        yield break;
      }
      if (deltaType == "thinking_delta")
      {
        active.SawThinkingDelta = true;
        var buffered = active.AppendReasoningDelta(String(delta, "thinking"), itemId);
        if (buffered is not null)
        {
          yield return Event(
            active,
            "reasoning.delta",
            delta: buffered.Text,
            itemId: buffered.ItemId,
            native: payload
          );
        }
        yield break;
      }
    }
    if (
      eventType == "content_block_start"
      && stream.TryGetProperty("content_block", out var block)
      && String(block, "type") == "tool_use"
    )
    {
      foreach (var buffered in FlushBufferedDeltas(active, payload))
      {
        yield return buffered;
      }
      var toolCallId = String(block, "id") ?? itemId;
      active.StartedTools.Add(toolCallId);
      yield return Event(
        active,
        "tool.started",
        message: $"Claude Code started {String(block, "name") ?? "tool"}.",
        itemId: toolCallId,
        tool: String(block, "name"),
        state: "running",
        native: payload
      );
      yield break;
    }
    if (eventType == "content_block_stop")
    {
      foreach (var buffered in FlushBufferedDeltas(active, payload))
      {
        yield return buffered;
      }
      yield break;
    }
    if (eventType is "message_start" or "content_block_start" or "content_block_delta" or "message_delta" or "message_stop")
    {
      yield break;
    }
    foreach (var buffered in FlushBufferedDeltas(active, payload))
    {
      yield return buffered;
    }
    yield return Event(active, "native.event", native: payload);
  }

  private static IEnumerable<HarnessEvent> FlushBufferedDeltas(
    ActiveTurn active,
    JsonElement? native = null
  )
  {
    var reasoning = active.FlushReasoningDelta();
    if (reasoning is not null)
    {
      yield return Event(
        active,
        "reasoning.delta",
        delta: reasoning.Text,
        itemId: reasoning.ItemId,
        native: native
      );
    }
    var text = active.FlushTextDelta();
    if (text is not null)
    {
      yield return Event(
        active,
        "assistant.delta",
        delta: text.Text,
        itemId: text.ItemId,
        native: native
      );
    }
  }

  private static void WriteBufferedDeltas(ActiveTurn active)
  {
    foreach (var buffered in FlushBufferedDeltas(active))
    {
      active.Write(buffered);
    }
  }

  private static IEnumerable<HarnessEvent> MapMessageContent(
    ActiveTurn active,
    JsonElement payload,
    bool assistant
  )
  {
    if (
      !payload.TryGetProperty("message", out var message)
      || !message.TryGetProperty("content", out var content)
      || content.ValueKind != JsonValueKind.Array
    )
    {
      yield return Event(active, "native.event", native: payload);
      yield break;
    }
    foreach (var block in content.EnumerateArray())
    {
      var blockType = String(block, "type");
      if (assistant && blockType == "text" && !active.SawTextDelta)
      {
        yield return Event(
          active,
          "assistant.delta",
          delta: String(block, "text"),
          itemId: String(payload, "uuid"),
          native: payload
        );
      }
      else if (assistant && blockType == "thinking" && !active.SawThinkingDelta)
      {
        yield return Event(
          active,
          "reasoning.delta",
          delta: String(block, "thinking"),
          itemId: String(payload, "uuid"),
          native: payload
        );
      }
      else if (assistant && blockType == "tool_use")
      {
        var toolCallId = String(block, "id") ?? $"claude-tool-{Guid.NewGuid():N}";
        if (active.StartedTools.Add(toolCallId))
        {
          yield return Event(
            active,
            "tool.started",
            message: $"Claude Code started {String(block, "name") ?? "tool"}.",
            itemId: toolCallId,
            tool: String(block, "name"),
            state: "running",
            native: payload
          );
        }
      }
      else if (!assistant && blockType == "tool_result")
      {
        var failed = Bool(block, "is_error") == true;
        var toolCallId = String(block, "tool_use_id");
        yield return Event(
          active,
          failed ? "tool.failed" : "tool.completed",
          message: failed ? "Claude Code tool failed." : "Claude Code tool completed.",
          itemId: toolCallId,
          state: failed ? "failed" : "completed",
          output: ContentText(block),
          native: payload
        );
      }
    }
  }

  private static IReadOnlyList<string> ValidateInit(
    JsonElement payload,
    HarnessTurnRequest request,
    bool resumed
  )
  {
    var model = String(payload, "model");
    if (!string.Equals(model, request.Model, StringComparison.Ordinal))
    {
      throw Failure(
        "claude-code-model-mismatch",
        $"Claude Code reported model '{model ?? "<missing>"}' after Agentic Router selected '{request.Model}'."
      );
    }
    var cwd = String(payload, "cwd");
    if (
      string.IsNullOrWhiteSpace(cwd)
      || !string.Equals(
        Path.GetFullPath(cwd),
        Path.GetFullPath(request.WorkingDirectory),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
      )
    )
    {
      throw Failure(
        "claude-code-workspace-mismatch",
        "Claude Code reported a different working directory."
      );
    }
    var native = ActiveNativeTools(request, request.HostCapabilities!);
    var host = HarnessCapabilityProjection.HostBridgeTools(
      HarnessIds.ClaudeCode,
      request.HostCapabilities!
    ).Select(name => $"mcp__agentic_router__{name}").ToArray();
    var allowed = native.Concat(host).ToHashSet(StringComparer.Ordinal);
    if (!payload.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
    {
      throw Failure(
        "claude-code-tool-surface",
        "Claude Code did not report its structured tool inventory."
      );
    }
    var reportedTools = tools.EnumerateArray()
      .Select(item => item.GetString())
      .Where(name => !string.IsNullOrWhiteSpace(name))
      .Cast<string>()
      .ToHashSet(StringComparer.Ordinal);
    var unexpected = reportedTools.Except(allowed, StringComparer.Ordinal).Order().ToArray();
    if (unexpected.Length > 0)
    {
      throw Failure(
        "claude-code-tool-surface",
        $"Claude Code exposed disallowed tools: {string.Join(", ", unexpected)}."
      );
    }
    var missing = allowed.Except(reportedTools, StringComparer.Ordinal).Order().ToArray();
    if (
      payload.TryGetProperty("mcp_servers", out var mcpServers)
      && mcpServers.ValueKind == JsonValueKind.Array
    )
    {
      var reportedServers = mcpServers.EnumerateArray()
        .Select(server => String(server, "name"))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Cast<string>()
        .ToArray();
      var unrelated = reportedServers
        .Where(name => !string.Equals(name, "agentic_router", StringComparison.Ordinal))
        .ToArray();
      if (unrelated.Length > 0)
      {
        throw Failure(
          "claude-code-mcp-surface",
          $"Claude Code loaded unrelated MCP servers: {string.Join(", ", unrelated)}."
        );
      }
      if (host.Length > 0 && !reportedServers.Contains("agentic_router", StringComparer.Ordinal))
      {
        throw Failure(
          "claude-code-mcp-surface",
          "Claude Code did not load the required Agentic Router Host bridge."
        );
      }
    }
    else if (host.Length > 0)
    {
      throw Failure(
        "claude-code-mcp-surface",
        "Claude Code did not report its structured MCP inventory."
      );
    }
    if (
      payload.TryGetProperty("plugins", out var plugins)
      && plugins.ValueKind == JsonValueKind.Array
      && plugins.GetArrayLength() > 0
    )
    {
      throw Failure(
        "claude-code-plugin-surface",
        "Claude Code loaded plugins in the controlled Agentic Router session."
      );
    }
    if (!resumed && string.IsNullOrWhiteSpace(String(payload, "session_id")))
    {
      throw Failure(
        "claude-code-session-missing",
        "Claude Code did not report a native session identifier."
      );
    }
    return missing;
  }

  private static bool IsControlPermissionRequest(JsonElement payload)
  {
    return String(payload, "type") == "control_request"
      && payload.TryGetProperty("request", out var request)
      && String(request, "subtype") == "can_use_tool";
  }

  private static async Task RejectUnsupportedControlRequestAsync(
    StreamWriter input,
    ActiveTurn active,
    JsonElement payload,
    CancellationToken cancellationToken
  )
  {
    var requestId = String(payload, "request_id");
    if (!string.IsNullOrWhiteSpace(requestId))
    {
      await WriteAsync(
        input,
        new
        {
          type = "control_response",
          response = new
          {
            subtype = "error",
            request_id = requestId,
            error = "Agentic Router does not support this Claude Code control request."
          }
        },
        cancellationToken
      );
    }
    active.Write(Event(active, "native.event", native: payload));
  }

  private static void ValidateFrameModel(
    JsonElement payload,
    HarnessTurnRequest request,
    string? type
  )
  {
    if (type == "assistant")
    {
      var reported = String(payload, "message", "model");
      if (!string.IsNullOrWhiteSpace(reported)
        && !string.Equals(reported, request.Model, StringComparison.Ordinal))
      {
        throw Failure(
          "claude-code-model-substitution",
          $"Claude Code emitted assistant content from '{reported}' after Agentic Router selected '{request.Model}'."
        );
      }
    }
    if (type == "result")
    {
      var hasUsage = payload.TryGetProperty("modelUsage", out var usage)
        || payload.TryGetProperty("model_usage", out usage);
      if (!hasUsage || usage.ValueKind != JsonValueKind.Object)
      {
        return;
      }
      var substitutions = usage.EnumerateObject()
        .Select(property => property.Name)
        .Where(model => !string.Equals(model, request.Model, StringComparison.Ordinal))
        .ToArray();
      if (substitutions.Length > 0)
      {
        throw Failure(
          "claude-code-model-substitution",
          $"Claude Code reported usage for an unselected model: {string.Join(", ", substitutions)}."
        );
      }
    }
  }

  private static IReadOnlyList<string> PermissionPaths(
    JsonElement request,
    JsonElement arguments
  )
  {
    var values = new List<string>();
    foreach (var property in new[] { "file_path", "path", "notebook_path" })
    {
      var value = String(arguments, property);
      if (!string.IsNullOrWhiteSpace(value))
      {
        values.Add(value);
      }
    }
    var blocked = String(request, "blocked_path");
    if (!string.IsNullOrWhiteSpace(blocked))
    {
      values.Add(blocked);
    }
    return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }

  private static bool PermissionPathsAreWorkspaceConfined(
    IReadOnlyList<string> paths,
    string workingDirectory
  )
  {
    if (paths.Count == 0)
    {
      return true;
    }
    var root = Path.GetFullPath(workingDirectory);
    var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
      + Path.DirectorySeparatorChar;
    foreach (var path in paths)
    {
      try
      {
        var fullPath = Path.IsPathFullyQualified(path)
          ? Path.GetFullPath(path)
          : Path.GetFullPath(path, root);
        if (!string.Equals(
            fullPath,
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
          )
          && !fullPath.StartsWith(
            prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
          ))
        {
          return false;
        }
      }
      catch (Exception exception) when (
        exception is ArgumentException or NotSupportedException or PathTooLongException
      )
      {
        return false;
      }
    }
    return true;
  }

  private static IReadOnlyList<string> ActiveNativeTools(
    HarnessTurnRequest request,
    HostCapabilityProfile profile
  )
  {
    if (!request.UseMinimalToolInventory)
    {
      return NativeTools;
    }
    var tools = new List<string>();
    if (profile.Allows("read_file"))
    {
      tools.Add("Read");
    }
    if (profile.Allows("list_files"))
    {
      tools.Add("Glob");
    }
    if (profile.Allows("search_text"))
    {
      tools.Add("Grep");
    }
    if (profile.Allows("replace_text") || profile.Allows("apply_patch"))
    {
      tools.Add("Edit");
    }
    if (profile.Allows("create_file") || profile.Allows("write_file"))
    {
      tools.Add("Write");
    }
    return tools;
  }

  private async Task RelayHostEventsAsync(
    ChannelReader<HarnessEvent> reader,
    ActiveTurn active,
    CancellationToken cancellationToken
  )
  {
    try
    {
      await foreach (var harnessEvent in reader.ReadAllAsync(cancellationToken))
      {
        active.Write(harnessEvent with
        {
          HarnessId = HarnessIds.ClaudeCode,
          SessionId = active.SessionId,
          TurnId = active.TurnId
        });
      }
    }
    catch (OperationCanceledException)
    {
    }
  }

  private async Task DrainErrorAsync(
    Process process,
    ActiveTurn active,
    CancellationToken cancellationToken
  )
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        var line = await process.StandardError.ReadLineAsync(cancellationToken);
        if (line is null)
        {
          break;
        }
        _logger.LogDebug("Claude Code stderr: {Line}", Truncate(line));
        active.LastError = Truncate(line);
      }
    }
    catch (OperationCanceledException)
    {
    }
  }

  private static ProcessStartInfo BaseStartInfo(string executable, string workingDirectory)
  {
    return new ProcessStartInfo
    {
      FileName = executable,
      WorkingDirectory = workingDirectory,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardInputEncoding = new UTF8Encoding(false),
      StandardOutputEncoding = new UTF8Encoding(false, true),
      StandardErrorEncoding = new UTF8Encoding(false, true)
    };
  }

  private string ResolveExecutable()
  {
    foreach (var candidate in new[] { _options.ExecutablePath, _options.ManagedExecutablePath })
    {
      if (string.IsNullOrWhiteSpace(candidate))
      {
        continue;
      }
      var fullPath = Path.GetFullPath(candidate);
      if (File.Exists(fullPath))
      {
        return fullPath;
      }
      if (candidate == _options.ExecutablePath)
      {
        throw Failure(
          "claude-code-executable-not-found",
          $"Configured Claude Code executable does not exist: {fullPath}"
        );
      }
    }
    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    var extensions = OperatingSystem.IsWindows()
      ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM")
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
      : [string.Empty];
    foreach (var directory in path.Split(
      Path.PathSeparator,
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
    ))
    {
      foreach (var extension in extensions)
      {
        var candidate = Path.Combine(directory, "claude" + extension.ToLowerInvariant());
        if (File.Exists(candidate))
        {
          return Path.GetFullPath(candidate);
        }
      }
    }
    throw Failure(
      "claude-code-executable-not-found",
      "No Claude Code executable was found in AgenticRouter:ClaudeCode:ExecutablePath, the managed user-local path, or PATH."
    );
  }

  private static async Task WriteAsync(
    StreamWriter writer,
    object value,
    CancellationToken cancellationToken
  )
  {
    var json = JsonSerializer.Serialize(value);
    await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    await writer.FlushAsync(cancellationToken);
  }

  private static async Task IgnoreCancellationAsync(Task task)
  {
    try
    {
      await task;
    }
    catch (OperationCanceledException)
    {
    }
    catch (ChannelClosedException)
    {
    }
  }

  private static void Validate(HarnessTurnRequest request)
  {
    if (!string.Equals(request.HarnessId, HarnessIds.ClaudeCode, StringComparison.OrdinalIgnoreCase))
    {
      throw Failure(
        "claude-code-request-invalid",
        "The Claude Code adapter received another harness id."
      );
    }
    if (!string.Equals(request.Provider, "ollama-local", StringComparison.OrdinalIgnoreCase))
    {
      throw Failure(
        "claude-code-provider-unsupported",
        "Claude Code supports Ollama Local models only."
      );
    }
    if (
      string.IsNullOrWhiteSpace(request.Model)
      || request.ProviderEndpoint is null
      || !request.ProviderEndpoint.IsLoopback
    )
    {
      throw Failure(
        "claude-code-request-invalid",
        "Claude Code requires an exact model and loopback Ollama endpoint."
      );
    }
    if (!Directory.Exists(Path.GetFullPath(request.WorkingDirectory)))
    {
      throw Failure(
        "claude-code-workspace-invalid",
        "The trusted workspace is unavailable."
      );
    }
  }

  private static HarnessEvent Event(
    ActiveTurn active,
    string type,
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
    HarnessTerminalState? terminalState = null,
    JsonElement? native = null,
    long? contextInputTokens = null,
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
      harnessId: HarnessIds.ClaudeCode,
      sessionId: active.SessionId,
      turnId: active.TurnId,
      terminalState: terminalState,
      nativePayload: native?.Clone(),
      contextInputTokens: contextInputTokens,
      readOnlyPermission: readOnlyPermission
    );
  }

  private static HarnessException Failure(string code, string message)
  {
    return new HarnessException(
      code,
      message,
      message,
      true,
      harnessId: HarnessIds.ClaudeCode
    );
  }

  private static string RequiredString(JsonElement element, string property)
  {
    return String(element, property) ?? throw Failure(
      "claude-code-protocol-field",
      $"Claude Code omitted required field {property}."
    );
  }

  private static string? String(JsonElement element, params string[] path)
  {
    var current = element;
    foreach (var property in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
      {
        return null;
      }
    }
    return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
  }

  private static long? Long(JsonElement element, params string[] path)
  {
    var current = element;
    foreach (var property in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
      {
        return null;
      }
    }
    return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var value)
      ? value
      : null;
  }

  private static bool? Bool(JsonElement element, params string[] path)
  {
    var current = element;
    foreach (var property in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
      {
        return null;
      }
    }
    return current.ValueKind is JsonValueKind.True or JsonValueKind.False
      ? current.GetBoolean()
      : null;
  }

  private static string? FirstString(JsonElement element, string property)
  {
    if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
    {
      return null;
    }
    foreach (var item in array.EnumerateArray())
    {
      if (item.ValueKind == JsonValueKind.String)
      {
        return item.GetString();
      }
    }
    return null;
  }

  private static string? ContentText(JsonElement block)
  {
    if (!block.TryGetProperty("content", out var content))
    {
      return null;
    }
    if (content.ValueKind == JsonValueKind.String)
    {
      return Truncate(content.GetString());
    }
    if (content.ValueKind != JsonValueKind.Array)
    {
      return Truncate(content.GetRawText());
    }
    return Truncate(string.Join('\n', content.EnumerateArray().Select(item =>
      String(item, "text") ?? item.GetRawText()
    )));
  }

  private static JsonElement EmptyObject()
  {
    using var document = JsonDocument.Parse("{}");
    return document.RootElement.Clone();
  }

  private static string Truncate(string? value)
  {
    if (string.IsNullOrEmpty(value) || value.Length <= MaximumActivityText)
    {
      return value ?? string.Empty;
    }
    return value[..MaximumActivityText] + "\n[truncated]";
  }

  private sealed class HarnessSession(
    string nativeSessionId,
    string model,
    string workingDirectory,
    string capabilitySignature
  )
  {
    public string NativeSessionId { get; } = nativeSessionId;

    public string Model { get; } = model;

    public string WorkingDirectory { get; } = workingDirectory;

    public string CapabilitySignature { get; } = capabilitySignature;

    public bool HasStarted { get; set; }

    public long? SynchronizedThroughVersion { get; set; }

    public bool Matches(HarnessTurnRequest request, HostCapabilityProfile profile)
    {
      return string.Equals(Model, request.Model, StringComparison.Ordinal)
        && string.Equals(
          WorkingDirectory,
          Path.GetFullPath(request.WorkingDirectory),
          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        )
        && string.Equals(CapabilitySignature, profile.Signature, StringComparison.Ordinal);
    }
  }

  private sealed class PendingApproval(string requestId, JsonElement arguments)
  {
    public string RequestId { get; } = requestId;

    public JsonElement Arguments { get; } = arguments;

    public TaskCompletionSource<bool> Decision { get; } = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
  }

  private sealed record BufferedDelta(string Text, string? ItemId);

  private sealed class ActiveTurn(string sessionId, string nativeSessionId, string turnId)
  {
    private int _terminal;
    private Process? _process;
    private readonly StringBuilder _reasoningBuffer = new();
    private readonly StringBuilder _textBuffer = new();
    private string? _reasoningItemId;
    private string? _textItemId;

    public string SessionId { get; } = sessionId;

    public string NativeSessionId { get; } = nativeSessionId;

    public string TurnId { get; } = turnId;

    public string InitializeRequestId { get; } = $"ar-init-{Guid.NewGuid():N}";

    public Channel<HarnessEvent> Events { get; } = Channel.CreateUnbounded<HarnessEvent>(
      new UnboundedChannelOptions
      {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
      }
    );

    public CancellationTokenSource Lifetime { get; } = new();

    public ConcurrentDictionary<string, PendingApproval> Approvals { get; } = new(StringComparer.Ordinal);

    public HashSet<string> StartedTools { get; } = new(StringComparer.Ordinal);

    public TaskCompletionSource Completion { get; } = new(
      TaskCreationOptions.RunContinuationsAsynchronously
    );

    public bool SawTextDelta { get; set; }

    public bool SawThinkingDelta { get; set; }

    public bool CancellationRequested { get; private set; }

    public string? LastError { get; set; }

    public HarnessTerminalState? TerminalState { get; private set; }

    public bool IsTerminal => Volatile.Read(ref _terminal) != 0;

    public BufferedDelta? AppendReasoningDelta(string? delta, string? itemId)
    {
      return AppendDelta(_reasoningBuffer, ref _reasoningItemId, delta, itemId);
    }

    public BufferedDelta? AppendTextDelta(string? delta, string? itemId)
    {
      return AppendDelta(_textBuffer, ref _textItemId, delta, itemId);
    }

    public BufferedDelta? FlushReasoningDelta()
    {
      return FlushDelta(_reasoningBuffer, ref _reasoningItemId);
    }

    public BufferedDelta? FlushTextDelta()
    {
      return FlushDelta(_textBuffer, ref _textItemId);
    }

    public void Attach(Process process)
    {
      _process = process;
      if (CancellationRequested)
      {
        RequestProcessStop();
      }
    }

    public void Write(HarnessEvent harnessEvent)
    {
      if (!IsTerminal)
      {
        Events.Writer.TryWrite(harnessEvent);
      }
    }

    public bool TryComplete(HarnessEvent terminalEvent)
    {
      if (!terminalEvent.IsTerminal)
      {
        throw new InvalidOperationException($"Harness event '{terminalEvent.Type}' is not terminal.");
      }
      if (Interlocked.Exchange(ref _terminal, 1) != 0)
      {
        return false;
      }
      TerminalState = terminalEvent.TerminalState;
      Events.Writer.TryWrite(terminalEvent);
      Events.Writer.TryComplete();
      return true;
    }

    public bool ResolveApproval(string requestId, bool approved)
    {
      return Approvals.TryGetValue(requestId, out var pending)
        && pending.Decision.TrySetResult(approved);
    }

    public void RequestCancellation()
    {
      CancellationRequested = true;
      Lifetime.Cancel();
      foreach (var pending in Approvals.Values)
      {
        pending.Decision.TrySetCanceled();
      }
      RequestProcessStop();
    }

    public void RequestProcessStop()
    {
      try
      {
        if (_process is { HasExited: false })
        {
          _process.Kill(true);
        }
      }
      catch (InvalidOperationException)
      {
      }
      catch (Win32Exception)
      {
      }
    }

    public void Finish()
    {
      Completion.TrySetResult();
    }

    public void Dispose()
    {
      Lifetime.Dispose();
    }

    private static BufferedDelta? AppendDelta(
      StringBuilder buffer,
      ref string? bufferedItemId,
      string? delta,
      string? itemId
    )
    {
      if (string.IsNullOrEmpty(delta))
      {
        return null;
      }
      bufferedItemId ??= itemId;
      buffer.Append(delta);
      return buffer.Length >= MinimumStreamChunkLength
        ? FlushDelta(buffer, ref bufferedItemId)
        : null;
    }

    private static BufferedDelta? FlushDelta(
      StringBuilder buffer,
      ref string? bufferedItemId
    )
    {
      if (buffer.Length == 0)
      {
        return null;
      }
      var buffered = new BufferedDelta(buffer.ToString(), bufferedItemId);
      buffer.Clear();
      bufferedItemId = null;
      return buffered;
    }
  }
}
