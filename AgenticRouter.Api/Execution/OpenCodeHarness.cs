using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Platform;

namespace AgenticRouter.Api.Execution;

public sealed record OpenCodeHarnessOptions(
  string? ExecutablePath,
  string? ManagedExecutablePath,
  string RuntimeDirectory,
  TimeSpan StartupTimeout,
  TimeSpan RequestTimeout
);

public sealed class OpenCodeHarnessAdapter : IAgentHarness, IAgentHarnessTransport
{
  private const string ProviderId = "agentic-router-ollama";
  private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromMinutes(1);
  private static readonly HarnessDefinition AdapterDefinition = new(
    HarnessIds.OpenCode,
    "OpenCode",
    true,
    "OpenCode headless server with isolated local configuration and Host-observed effects.",
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
      SupportsSessionDiff: true,
      SupportsNativePermissions: true,
      SupportsSteering: false
    ),
    ["ollama-local"]
  );

  private readonly OpenCodeHarnessOptions _options;
  private readonly IHttpClientFactory _httpClients;
  private readonly HarnessMcpHostBridge _hostTools;
  private readonly ILogger<OpenCodeHarnessAdapter> _logger;
  private readonly SemaphoreSlim _availabilityGate = new(1, 1);
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly SemaphoreSlim _turnGate = new(1, 1);
  private readonly ConcurrentDictionary<string, HarnessSessionState> _sessions = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, PendingPermission> _permissions = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, ActiveTurn> _activeTurns = new(StringComparer.Ordinal);
  private Process? _process;
  private Uri? _serverUri;
  private string? _password;
  private string? _configurationKey;
  private HarnessAvailability? _cachedAvailability;
  private bool _disposed;

  public OpenCodeHarnessAdapter(
    OpenCodeHarnessOptions options,
    IHttpClientFactory httpClients,
    HarnessMcpHostBridge hostTools,
    ILogger<OpenCodeHarnessAdapter> logger
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
      var executable = ResolveExecutable();
      var startInfo = CreateStartInfo(executable, _options.RuntimeDirectory);
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
        throw new TimeoutException("OpenCode version detection timed out.");
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
      _logger.LogInformation(exception, "OpenCode harness is unavailable.");
      _cachedAvailability = HarnessAvailability.Missing(
        "OpenCode executable was not found or could not be started."
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
    var turnId = $"oc-turn-{Guid.NewGuid():N}";
    ActiveTurn? active = null;
    try
    {
      var endpoint = request.ProviderEndpoint!;
      var bridge = await _hostTools.ConfigureClientAsync(
        HarnessIds.OpenCode,
        MaximumHostBridgeTools(HarnessIds.OpenCode),
        cancellationToken
      );
      await EnsureStartedAsync(endpoint, request.Model, bridge, cancellationToken);
      var harnessSession = await GetOrCreateSessionAsync(request, cancellationToken);
      var sessionId = harnessSession.NativeSessionId;
      var turnPrompt = HarnessConversationPromptBuilder.Create(
        request,
        harnessSession.SynchronizedThroughVersion,
        request.HostCapabilities is null
          ? []
          :
          [
            $"Agentic Router common capabilities implemented by OpenCode native tools: {string.Join(", ", HarnessCapabilityProjection.NativeCommonTools(HarnessIds.OpenCode))}.",
            $"Agentic Router common capabilities supplied through the Host bridge: {string.Join(", ", HarnessCapabilityProjection.HostBridgeTools(HarnessIds.OpenCode, request.HostCapabilities))}.",
            $"Host approval policy: {request.HostCapabilities.ApprovalPolicy}."
          ]
      );
      active = new ActiveTurn(request.SessionId, sessionId, request.WorkingDirectory, turnId);
      _activeTurns[request.SessionId] = active;
      using var hostTurn = _hostTools.BeginTurn(
        HarnessIds.OpenCode,
        sessionId,
        turnId,
        request.HostCapabilities ?? throw Failure(
          "opencode-host-profile-missing",
          "OpenCode requires the Agentic Router Host capability profile."
        )
      );

      yield return Event(
        "turn.started",
        sessionId,
        turnId,
        message: $"OpenCode session {sessionId} started."
      );

      using var eventsRequest = CreateRequest(
        HttpMethod.Get,
        $"event?directory={Encode(request.WorkingDirectory)}"
      );
      using var eventsResponse = await Client().SendAsync(
        eventsRequest,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken
      );
      await EnsureSuccessAsync(eventsResponse, "opencode-event-stream", cancellationToken);

      await SendAsync(
        HttpMethod.Post,
        $"session/{EncodePath(sessionId)}/prompt_async?directory={Encode(request.WorkingDirectory)}",
        new
        {
          model = new { providerID = ProviderId, modelID = request.Model },
          agent = "build",
          tools = new Dictionary<string, bool>(StringComparer.Ordinal)
          {
            ["bash"] = false,
            ["task"] = false
          },
          parts = new[] { new { type = "text", text = turnPrompt.Text } }
        },
        cancellationToken,
        expectedStatus: HttpStatusCode.NoContent
      );

      await using var stream = await eventsResponse.Content.ReadAsStreamAsync(cancellationToken);
      using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
      var parts = new Dictionary<string, OpenCodePart>(StringComparer.Ordinal);
      var toolStates = new Dictionary<string, string>(StringComparer.Ordinal);
      var terminal = false;
      Task<string?>? lineRead = null;
      Task<HarnessEvent>? hostToolRead = null;
      while (!terminal)
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
          var exited = _process is null || _process.HasExited;
          throw Failure(
            exited ? "opencode-server-exited" : "opencode-event-stream-ended",
            exited
              ? "OpenCode exited before the active turn completed."
              : "OpenCode event stream ended before terminal state."
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
            "opencode-protocol-json",
            "OpenCode returned malformed event data.",
            Truncate(exception.Message),
            true,
            exception,
            HarnessIds.OpenCode
          );
        }

        if (!TryType(payload, out var type) || !TryProperties(payload, out var properties))
        {
          yield return Event("native.event", sessionId, turnId, native: payload);
          continue;
        }
        if (
          properties.TryGetProperty("sessionID", out var eventSession)
          && !string.Equals(eventSession.GetString(), sessionId, StringComparison.Ordinal)
        )
        {
          continue;
        }

        switch (type)
        {
          case "session.next.text.delta":
            yield return Event(
              "assistant.delta",
              sessionId,
              turnId,
              delta: String(properties, "delta"),
              itemId: String(properties, "partID") ?? String(properties, "partId"),
              native: payload
            );
            break;
          case "session.next.reasoning.delta":
            yield return Event(
              "reasoning.delta",
              sessionId,
              turnId,
              delta: String(properties, "delta"),
              itemId: String(properties, "partID") ?? String(properties, "partId"),
              native: payload
            );
            break;
          case "message.part.delta":
            {
              var partId = String(properties, "partID") ?? String(properties, "partId");
              var field = String(properties, "field");
              var delta = String(properties, "delta");
              if (
                partId is null
                || delta is null
                || !string.Equals(field, "text", StringComparison.Ordinal)
                || !parts.TryGetValue(partId, out var part)
                || part.Type is not ("text" or "reasoning")
              )
              {
                yield return Event("native.event", sessionId, turnId, native: payload);
                break;
              }
              parts[partId] = part with { Text = part.Text + delta };
              yield return Event(
                part.Type == "reasoning" ? "reasoning.delta" : "assistant.delta",
                sessionId,
                turnId,
                delta: delta,
                itemId: partId,
                native: payload
              );
              break;
            }
          case "message.part.updated":
            {
              if (
                !properties.TryGetProperty("part", out var part)
                || part.ValueKind != JsonValueKind.Object
              )
              {
                yield return Event("native.event", sessionId, turnId, native: payload);
                break;
              }
              var partId = String(part, "id");
              var partType = String(part, "type");
              if (partId is null || partType is null)
              {
                yield return Event("native.event", sessionId, turnId, native: payload);
                break;
              }

              if (partType is "text" or "reasoning")
              {
                var fullText = String(part, "text") ?? string.Empty;
                if (
                  partType == "text"
                  && string.Equals(fullText, turnPrompt.Text, StringComparison.Ordinal)
                )
                {
                  parts[partId] = new OpenCodePart("user", fullText);
                  yield return Event("native.event", sessionId, turnId, native: payload);
                  break;
                }
                var previous = parts.TryGetValue(partId, out var known)
                  ? known.Text
                  : string.Empty;
                parts[partId] = new OpenCodePart(partType, fullText);
                var delta = UnseenSuffix(previous, fullText);
                if (delta.Length == 0)
                {
                  yield return Event("native.event", sessionId, turnId, native: payload);
                  break;
                }
                yield return Event(
                  partType == "reasoning" ? "reasoning.delta" : "assistant.delta",
                  sessionId,
                  turnId,
                  delta: delta,
                  itemId: partId,
                  native: payload
                );
                break;
              }

              if (
                partType == "tool"
                && part.TryGetProperty("state", out var toolState)
                && toolState.ValueKind == JsonValueKind.Object
              )
              {
                var status = String(toolState, "status");
                if (
                  status is null
                  || (
                    toolStates.TryGetValue(partId, out var previousStatus)
                    && string.Equals(previousStatus, status, StringComparison.Ordinal)
                  )
                )
                {
                  yield return Event("native.event", sessionId, turnId, native: payload);
                  break;
                }
                toolStates[partId] = status;
                var callId = String(part, "callID") ?? partId;
                var tool = String(part, "tool");
                switch (status)
                {
                  case "running":
                    yield return Event(
                      "tool.started",
                      sessionId,
                      turnId,
                      tool: tool,
                      state: status,
                      itemId: callId,
                      native: payload
                    );
                    break;
                  case "completed":
                    yield return Event(
                      "tool.completed",
                      sessionId,
                      turnId,
                      tool: tool,
                      state: status,
                      output: String(toolState, "output"),
                      itemId: callId,
                      native: payload
                    );
                    break;
                  case "error":
                    yield return Event(
                      "tool.failed",
                      sessionId,
                      turnId,
                      tool: tool,
                      state: "failed",
                      output: String(toolState, "error"),
                      itemId: callId,
                      native: payload
                    );
                    break;
                  default:
                    yield return Event("native.event", sessionId, turnId, native: payload);
                    break;
                }
                break;
              }

              yield return Event("native.event", sessionId, turnId, native: payload);
              break;
            }
          case "message.updated":
            {
              if (
                properties.TryGetProperty("info", out var info)
                && string.Equals(String(info, "role"), "assistant", StringComparison.Ordinal)
              )
              {
                var reportedProvider = String(info, "providerID");
                var reportedModel = String(info, "modelID");
                var selectionError = reportedProvider is null || reportedModel is null
                  ? "opencode-selection-unverified"
                  : !string.Equals(reportedProvider, ProviderId, StringComparison.Ordinal)
                    ? "opencode-provider-substitution"
                    : !string.Equals(reportedModel, request.Model, StringComparison.Ordinal)
                      ? "opencode-model-substitution"
                      : null;
                if (selectionError is not null)
                {
                  yield return Event(
                    "turn.failed",
                    sessionId,
                    turnId,
                    message: reportedProvider is null || reportedModel is null
                      ? "OpenCode did not report the provider and model used for the assistant turn."
                      : $"OpenCode reported '{reportedProvider}/{reportedModel}' after Agentic Router selected '{ProviderId}/{request.Model}'.",
                    errorCode: selectionError,
                    terminal: HarnessTerminalState.Failed,
                    native: payload
                  );
                  terminal = true;
                  try
                  {
                    await CancelTurnAsync(request.SessionId, CancellationToken.None);
                  }
                  catch (Exception exception)
                  {
                    _logger.LogWarning(
                      exception,
                      "OpenCode did not acknowledge abort after selection verification failed."
                    );
                  }
                  break;
                }

                if (!info.TryGetProperty("tokens", out var tokens))
                {
                  yield return Event("native.event", sessionId, turnId, native: payload);
                  break;
                }
                var inputTokens = Number(tokens, "input")
                  + CacheTokens(tokens, "read")
                  + CacheTokens(tokens, "write");
                var outputTokens = Number(tokens, "output");
                if (inputTokens > 0)
                {
                  yield return Event(
                    "usage.updated",
                    sessionId,
                    turnId,
                    native: payload,
                    contextInputTokens: inputTokens,
                    contextTotalTokens: inputTokens + Math.Max(0, outputTokens)
                  );
                  break;
                }
              }
              yield return Event("native.event", sessionId, turnId, native: payload);
              break;
            }
          case "session.next.tool.called":
            yield return Event(
              "tool.started",
              sessionId,
              turnId,
              tool: String(properties, "tool"),
              state: "running",
              itemId: String(properties, "callID"),
              native: payload
            );
            break;
          case "session.next.tool.success":
            yield return Event(
              "tool.completed",
              sessionId,
              turnId,
              tool: String(properties, "tool"),
              state: "completed",
              output: String(properties, "output"),
              itemId: String(properties, "callID"),
              native: payload
            );
            break;
          case "session.next.tool.failed":
            yield return Event(
              "tool.failed",
              sessionId,
              turnId,
              tool: String(properties, "tool"),
              state: "failed",
              output: String(properties, "error"),
              itemId: String(properties, "callID"),
              native: payload
            );
            break;
          case "permission.v2.asked":
          case "permission.asked":
            {
              var permissionId = String(properties, "id")
                ?? throw Failure("opencode-permission-invalid", "OpenCode permission omitted its id.");
              _permissions[permissionId] = new PendingPermission(
                permissionId,
                sessionId,
                request.WorkingDirectory
              );
              var resources = type == "permission.asked"
                ? Strings(properties, "patterns")
                : Strings(properties, "resources");
              var permission = String(properties, "action")
                ?? String(properties, "permission")
                ?? "workspace permission";
              var readOnlyPermission = IsReadOnlyPermission(permission);
              yield return Event(
                "approval.requested",
                sessionId,
                turnId,
                message: $"OpenCode requests {permission}.",
                tool: permission,
                approvalId: permissionId,
                approvalCanBeMapped: !readOnlyPermission && resources.Count > 0,
                readOnlyPermission: readOnlyPermission,
                destructive: IsDestructiveResource(permission)
                  || resources.Any(IsDestructiveResource),
                paths: resources,
                native: payload
              );
              break;
            }
          case "session.error":
            yield return Event(
              "turn.failed",
              sessionId,
              turnId,
              message: String(properties, "error") ?? "OpenCode session failed.",
              errorCode: "opencode-session-error",
              terminal: HarnessTerminalState.Failed,
              native: payload
            );
            terminal = true;
            break;
          case "session.idle":
            {
              var diff = await GetDiffAsync(sessionId, request.WorkingDirectory, cancellationToken);
              if (diff is { ValueKind: JsonValueKind.Array } && diff.GetArrayLength() > 0)
              {
                yield return Event(
                  "files.changed",
                  sessionId,
                  turnId,
                  message: $"OpenCode reported {diff.GetArrayLength()} changed file(s).",
                  native: diff
                );
              }
              yield return Event(
                "turn.completed",
                sessionId,
                turnId,
                message: "OpenCode session became idle.",
                terminal: HarnessTerminalState.Completed,
                native: payload
              );
              harnessSession.SynchronizedThroughVersion = turnPrompt.SynchronizedThroughVersion;
              terminal = true;
              break;
            }
          case "session.status" when StatusType(properties) == "idle":
            harnessSession.SynchronizedThroughVersion = turnPrompt.SynchronizedThroughVersion;
            yield return Event(
              "turn.completed",
              sessionId,
              turnId,
              message: "OpenCode session became idle.",
              terminal: HarnessTerminalState.Completed,
              native: payload
            );
            terminal = true;
            break;
          case "server.connected":
          case "session.status":
          case "session.diff":
            yield return Event("native.event", sessionId, turnId, native: payload);
            break;
          default:
            yield return Event("native.event", sessionId, turnId, native: payload);
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
          foreach (var pending in _permissions)
          {
            if (string.Equals(
              pending.Value.SessionId,
              active.OpenCodeSessionId,
              StringComparison.Ordinal
            ))
            {
              _permissions.TryRemove(pending.Key, out _);
            }
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
      throw Failure("opencode-permission-stale", "The OpenCode permission is no longer pending.");
    }
    await SendAsync(
      HttpMethod.Post,
      $"permission/{EncodePath(approvalId)}/reply?directory={Encode(pending.WorkingDirectory)}",
      new { reply = approved ? "once" : "reject" },
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
      HarnessIds.OpenCode,
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
      $"session/{EncodePath(active.OpenCodeSessionId)}/abort?directory={Encode(active.WorkingDirectory)}",
      null,
      cancellationToken
    );
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

  private async Task EnsureStartedAsync(
    Uri ollamaEndpoint,
    string model,
    HarnessMcpClientConfiguration bridge,
    CancellationToken cancellationToken
  )
  {
    await _lifecycleGate.WaitAsync(cancellationToken);
    try
    {
      var key = $"{ollamaEndpoint.GetLeftPart(UriPartial.Authority)}|{model}";
      if (_process is { HasExited: false } && string.Equals(key, _configurationKey, StringComparison.Ordinal))
      {
        return;
      }
      await StopOwnedProcessAsync();
      Directory.CreateDirectory(_options.RuntimeDirectory);
      await WriteConfigurationAsync(ollamaEndpoint, model, bridge, cancellationToken);
      var port = ReservePort();
      var executable = ResolveExecutable();
      _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
      _serverUri = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
      var startInfo = CreateStartInfo(executable, _options.RuntimeDirectory);
      startInfo.ArgumentList.Add("serve");
      startInfo.ArgumentList.Add("--pure");
      startInfo.ArgumentList.Add("--hostname");
      startInfo.ArgumentList.Add("127.0.0.1");
      startInfo.ArgumentList.Add("--port");
      startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
      SetRuntimeEnvironment(startInfo, _password, bridge.AuthorizationToken);
      var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
      if (!process.Start())
      {
        throw new InvalidOperationException("Process.Start returned false.");
      }
      _process = process;
      _configurationKey = key;
      _sessions.Clear();
      _ = DrainAsync(process.StandardOutput, process, "stdout");
      _ = DrainAsync(process.StandardError, process, "stderr");
      await WaitForHealthAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is not HarnessException)
    {
      await StopOwnedProcessAsync();
      throw new HarnessException(
        "opencode-start-failed",
        "OpenCode could not start its isolated server.",
        Truncate(exception.Message),
        true,
        exception,
        HarnessIds.OpenCode
      );
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  private async Task<HarnessSessionState> GetOrCreateSessionAsync(
    HarnessTurnRequest request,
    CancellationToken cancellationToken
  )
  {
    if (_sessions.TryGetValue(request.SessionId, out var existing))
    {
      return existing;
    }
    var result = await SendAsync(
      HttpMethod.Post,
      $"session?directory={Encode(request.WorkingDirectory)}",
      new
      {
        title = "Agentic Router Execute",
        agent = "build",
        model = new { id = request.Model, providerID = ProviderId }
      },
      cancellationToken
    );
    var sessionId = String(result, "id")
      ?? throw Failure("opencode-session-invalid", "OpenCode did not return a session id.");
    var directory = String(result, "directory");
    if (
      directory is not null
      && !string.Equals(
        Path.GetFullPath(directory),
        Path.GetFullPath(request.WorkingDirectory),
        FileSystemPathSemantics.Comparison
      )
    )
    {
      throw Failure("opencode-workspace-mismatch", "OpenCode returned a different working directory.");
    }
    var harnessSession = new HarnessSessionState(sessionId);
    _sessions[request.SessionId] = harnessSession;
    return harnessSession;
  }

  private async Task<JsonElement> GetDiffAsync(
    string sessionId,
    string workingDirectory,
    CancellationToken cancellationToken
  )
  {
    return await SendAsync(
      HttpMethod.Get,
      $"session/{EncodePath(sessionId)}/diff?directory={Encode(workingDirectory)}",
      null,
      cancellationToken
    );
  }

  private async Task<string?> ReadEventLineAsync(
    StreamReader reader,
    CancellationToken cancellationToken
  )
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(_options.RequestTimeout);
    try
    {
      return await reader.ReadLineAsync(timeout.Token);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (OperationCanceledException)
    {
      throw new HarnessException(
        "opencode-event-timeout",
        "OpenCode produced no event before the configured turn timeout.",
        $"The SSE stream was idle for {_options.RequestTimeout.TotalSeconds:0} seconds.",
        true,
        harnessId: HarnessIds.OpenCode
      );
    }
    catch (Exception exception) when (exception is IOException or HttpRequestException)
    {
      var exited = _process is null || _process.HasExited;
      throw new HarnessException(
        exited ? "opencode-server-exited" : "opencode-event-stream-failed",
        exited
          ? "OpenCode exited before the active turn completed."
          : "The OpenCode event stream failed before terminal state.",
        Truncate(exception.Message),
        true,
        exception,
        HarnessIds.OpenCode
      );
    }
  }

  private async Task<JsonElement> SendAsync(
    HttpMethod method,
    string relativeUri,
    object? body,
    CancellationToken cancellationToken,
    HttpStatusCode expectedStatus = HttpStatusCode.OK
  )
  {
    using var request = CreateRequest(method, relativeUri);
    if (body is not null)
    {
      request.Content = new StringContent(
        JsonSerializer.Serialize(body),
        Encoding.UTF8,
        "application/json"
      );
    }
    using var response = await Client().SendAsync(request, cancellationToken);
    if (response.StatusCode != expectedStatus)
    {
      await EnsureSuccessAsync(response, "opencode-http", cancellationToken);
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

  private HttpClient Client()
  {
    var client = _httpClients.CreateClient();
    client.BaseAddress = _serverUri ?? throw Failure("opencode-server-missing", "OpenCode server is unavailable.");
    client.Timeout = _options.RequestTimeout;
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
      "Basic",
      Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{_password}"))
    );
    return client;
  }

  private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri)
  {
    return new HttpRequestMessage(method, relativeUri);
  }

  private async Task WaitForHealthAsync(CancellationToken cancellationToken)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(_options.StartupTimeout);
    while (!timeout.IsCancellationRequested)
    {
      if (_process is null || _process.HasExited)
      {
        throw Failure("opencode-server-exited", "OpenCode exited during startup.");
      }
      try
      {
        var health = await SendAsync(HttpMethod.Get, "global/health", null, timeout.Token);
        if (health.TryGetProperty("healthy", out var healthy) && healthy.GetBoolean())
        {
          return;
        }
      }
      catch (Exception exception) when (
        (exception is HttpRequestException or TaskCanceledException)
        && !cancellationToken.IsCancellationRequested
      )
      {
      }
      await Task.Delay(100, timeout.Token);
    }
    throw Failure("opencode-start-timeout", "OpenCode did not become healthy before the startup timeout.");
  }

  private async Task WriteConfigurationAsync(
    Uri endpoint,
    string model,
    HarnessMcpClientConfiguration bridge,
    CancellationToken cancellationToken
  )
  {
    var configDirectory = Path.Combine(_options.RuntimeDirectory, "config", "opencode");
    Directory.CreateDirectory(configDirectory);
    var path = Path.Combine(configDirectory, "opencode.json");
    var config = new Dictionary<string, object?>
    {
      ["$schema"] = "https://opencode.ai/config.json",
      ["model"] = $"{ProviderId}/{model}",
      ["autoupdate"] = false,
      ["share"] = "disabled",
      ["plugin"] = Array.Empty<string>(),
      ["instructions"] = Array.Empty<string>(),
      ["provider"] = new Dictionary<string, object>
      {
        [ProviderId] = new
        {
          npm = "@ai-sdk/openai-compatible",
          name = "Agentic Router Ollama",
          options = new { baseURL = $"{endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/')}/v1" },
          models = new Dictionary<string, object> { [model] = new { name = model } }
        }
      },
      ["mcp"] = new Dictionary<string, object>
      {
        ["agentic_router"] = new
        {
          type = "remote",
          url = bridge.Endpoint.AbsoluteUri,
          enabled = true,
          oauth = false,
          headers = new Dictionary<string, string>
          {
            ["Authorization"] = $"Bearer {{env:{bridge.AuthorizationEnvironmentVariable}}}"
          },
          timeout = (long)_options.RequestTimeout.TotalMilliseconds
        }
      },
      ["permission"] = new Dictionary<string, string>
      {
        ["*"] = "ask",
        ["read"] = "deny",
        ["glob"] = "deny",
        ["grep"] = "deny",
        ["list"] = "deny",
        ["edit"] = "ask",
        ["bash"] = "deny",
        ["task"] = "deny",
        ["webfetch"] = "allow",
        ["websearch"] = "allow",
        ["external_directory"] = "deny",
        ["agentic_router_*"] = "allow"
      }
    };
    var content = JsonSerializer.Serialize(
      config,
      new JsonSerializerOptions { WriteIndented = true }
    );
    var temporary = path + ".tmp";
    await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
    File.Move(temporary, path, true);
  }

  private ProcessStartInfo CreateStartInfo(string executable, string workingDirectory)
  {
    Directory.CreateDirectory(workingDirectory);
    var info = new ProcessStartInfo
    {
      FileName = executable,
      WorkingDirectory = workingDirectory,
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      StandardOutputEncoding = new UTF8Encoding(false, true),
      StandardErrorEncoding = new UTF8Encoding(false, true)
    };
    SetRuntimeEnvironment(info, null, null);
    return info;
  }

  private void SetRuntimeEnvironment(
    ProcessStartInfo info,
    string? password,
    string? hostBridgeToken
  )
  {
    info.Environment["XDG_CONFIG_HOME"] = Path.Combine(_options.RuntimeDirectory, "config");
    info.Environment["XDG_DATA_HOME"] = Path.Combine(_options.RuntimeDirectory, "data");
    info.Environment["XDG_CACHE_HOME"] = Path.Combine(_options.RuntimeDirectory, "cache");
    info.Environment["XDG_STATE_HOME"] = Path.Combine(_options.RuntimeDirectory, "state");
    info.Environment["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
    info.Environment["OPENCODE_DISABLE_SHARE"] = "true";
    if (hostBridgeToken is not null)
    {
      info.Environment[HarnessMcpHostBridge.AuthorizationEnvironmentVariable] = hostBridgeToken;
    }
    if (password is not null)
    {
      info.Environment["OPENCODE_SERVER_USERNAME"] = "opencode";
      info.Environment["OPENCODE_SERVER_PASSWORD"] = password;
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

  private string ResolveExecutable()
  {
    foreach (var candidate in new[] { _options.ExecutablePath, _options.ManagedExecutablePath })
    {
      if (string.IsNullOrWhiteSpace(candidate))
      {
        continue;
      }
      var fullPath = Path.GetFullPath(candidate);
      if (!File.Exists(fullPath))
      {
        if (candidate == _options.ExecutablePath)
        {
          throw Failure("opencode-executable-not-found", $"Configured OpenCode executable does not exist: {fullPath}");
        }
        continue;
      }
      return fullPath;
    }
    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
      if (File.Exists(candidate))
      {
        return Path.GetFullPath(candidate);
      }
    }
    throw Failure("opencode-executable-not-found", "No OpenCode executable was found.");
  }

  private async Task StopOwnedProcessAsync()
  {
    var process = _process;
    _process = null;
    _serverUri = null;
    _password = null;
    _configurationKey = null;
    _sessions.Clear();
    _permissions.Clear();
    _activeTurns.Clear();
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

  private async Task DrainAsync(StreamReader reader, Process process, string stream)
  {
    try
    {
      while (!process.HasExited && await reader.ReadLineAsync() is { } line)
      {
        _logger.LogDebug("OpenCode {Stream}: {Line}", stream, Truncate(line));
      }
    }
    catch (Exception exception)
    {
      _logger.LogDebug(exception, "OpenCode {Stream} drain ended.", stream);
    }
  }

  private static async Task EnsureSuccessAsync(
    HttpResponseMessage response,
    string code,
    CancellationToken cancellationToken
  )
  {
    if (response.IsSuccessStatusCode)
    {
      return;
    }
    var detail = await response.Content.ReadAsStringAsync(cancellationToken);
    throw new HarnessException(
      code,
      "OpenCode returned an unsuccessful response.",
      $"HTTP {(int)response.StatusCode}: {Truncate(detail)}",
      true,
      harnessId: HarnessIds.OpenCode
    );
  }

  private static HarnessEvent Event(
    string type,
    string sessionId,
    string turnId,
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
      harnessId: HarnessIds.OpenCode,
      sessionId: sessionId,
      turnId: turnId,
      terminalState: terminal,
      nativePayload: native,
      contextInputTokens: contextInputTokens,
      contextTotalTokens: contextTotalTokens,
      readOnlyPermission: readOnlyPermission
    );
  }

  private static void Validate(HarnessTurnRequest request)
  {
    if (!string.Equals(request.HarnessId, HarnessIds.OpenCode, StringComparison.OrdinalIgnoreCase))
    {
      throw Failure("opencode-request-invalid", "The OpenCode adapter received another harness id.");
    }
    if (!string.Equals(request.Provider, "ollama-local", StringComparison.OrdinalIgnoreCase))
    {
      throw Failure("opencode-provider-unsupported", "OpenCode supports Ollama Local models only.");
    }
    if (request.ProviderEndpoint is null || string.IsNullOrWhiteSpace(request.Model))
    {
      throw Failure("opencode-request-invalid", "OpenCode requires an Ollama endpoint and exact model.");
    }
    var root = Path.GetFullPath(request.WorkingDirectory);
    if (!Directory.Exists(root))
    {
      throw Failure("opencode-workspace-invalid", "The trusted workspace is unavailable.");
    }
  }

  private static int ReservePort()
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  private static bool TryType(JsonElement payload, out string type)
  {
    type = String(payload, "type") ?? string.Empty;
    return type.Length > 0;
  }

  private static bool TryProperties(JsonElement payload, out JsonElement properties)
  {
    return payload.TryGetProperty("properties", out properties)
      && properties.ValueKind == JsonValueKind.Object;
  }

  private static string? String(JsonElement element, string name)
  {
    return element.ValueKind == JsonValueKind.Object
      && element.TryGetProperty(name, out var value)
      && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
  }

  private static long Number(JsonElement element, string name)
  {
    if (
      element.ValueKind != JsonValueKind.Object
      || !element.TryGetProperty(name, out var value)
      || value.ValueKind != JsonValueKind.Number
    )
    {
      return 0;
    }
    return value.TryGetInt64(out var integer)
      ? Math.Max(0, integer)
      : Math.Max(0, (long)Math.Ceiling(value.GetDouble()));
  }

  private static long CacheTokens(JsonElement tokens, string name)
  {
    return tokens.ValueKind == JsonValueKind.Object
      && tokens.TryGetProperty("cache", out var cache)
        ? Number(cache, name)
        : 0;
  }

  private static string UnseenSuffix(string previous, string current)
  {
    if (current.Length == 0 || string.Equals(previous, current, StringComparison.Ordinal))
    {
      return string.Empty;
    }
    return current.StartsWith(previous, StringComparison.Ordinal)
      ? current[previous.Length..]
      : previous.Length == 0
        ? current
        : string.Empty;
  }

  private static string? StatusType(JsonElement properties)
  {
    return properties.TryGetProperty("status", out var status)
      ? String(status, "type")
      : null;
  }

  private static IReadOnlyList<string> Strings(JsonElement element, string name)
  {
    if (
      element.ValueKind != JsonValueKind.Object
      || !element.TryGetProperty(name, out var values)
      || values.ValueKind != JsonValueKind.Array
    )
    {
      return [];
    }
    return values.EnumerateArray()
      .Where(value => value.ValueKind == JsonValueKind.String)
      .Select(value => value.GetString()!)
      .ToArray();
  }

  private static bool IsDestructiveResource(string resource)
  {
    return resource.Contains("delete", StringComparison.OrdinalIgnoreCase)
      || resource.Contains("remove", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsReadOnlyPermission(string permission)
  {
    return permission is "read" or "glob" or "grep" or "list" or "lsp"
      or "webfetch" or "websearch";
  }

  private static string Encode(string value) => Uri.EscapeDataString(value);

  private static string EncodePath(string value) => Uri.EscapeDataString(value);

  private static HarnessException Failure(string code, string message)
  {
    return new HarnessException(code, message, message, true, harnessId: HarnessIds.OpenCode);
  }

  private static string Truncate(string value)
  {
    return value.Length <= 8_192 ? value : value[..8_192];
  }

  private sealed record PendingPermission(
    string Id,
    string SessionId,
    string WorkingDirectory
  );

  private sealed record ActiveTurn(
    string ConversationId,
    string OpenCodeSessionId,
    string WorkingDirectory,
    string TurnId
  );

  private sealed class HarnessSessionState(string nativeSessionId)
  {
    public string NativeSessionId { get; } = nativeSessionId;

    public long? SynchronizedThroughVersion { get; set; }
  }

  private sealed record OpenCodePart(string Type, string Text);
}
