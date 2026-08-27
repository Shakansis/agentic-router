using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Platform;

namespace AgenticRouter.Api.Execution;

public sealed record CodexHarnessOptions(
  string? ExecutablePath,
  string? ManagedInstallRoot,
  string RuntimeDirectory,
  TimeSpan StartupTimeout,
  TimeSpan InterruptTimeout
);

public sealed class CodexHarnessAdapter : IAgentHarness, IAgentHarnessTransport, IAgentHarnessSteeringTransport, IAsyncDisposable
{
  private const int AutoCompactPercentage = 98;
  private const int MaximumActivityText = 8_192;
  private const string PermissionProfileId = ":workspace";
  private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromMinutes(1);

  private static readonly HarnessDefinition AdapterDefinition = new(
    HarnessIds.Codex,
    "Codex",
    true,
    "OpenAI Codex App Server with Agentic Router Host-owned effects.",
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
      SupportsSandbox: true,
      SupportsSessionDiff: true,
      SupportsNativePermissions: true,
      SupportsSteering: true
    ),
    ["ollama-local"]
  );

  private readonly CodexHarnessOptions _options;
  private readonly ISettingsStore _settingsStore;
  private readonly ILogger<CodexHarnessAdapter> _logger;
  private readonly SemaphoreSlim _availabilityGate = new(1, 1);
  private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
  private readonly SemaphoreSlim _writeGate = new(1, 1);
  private readonly SemaphoreSlim _turnGate = new(1, 1);
  private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
  private readonly ConcurrentDictionary<string, ActiveHarnessTurn> _activeByThread = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, HarnessSessionState> _threadsByConversation = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, byte> _attachedThreads = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, PendingServerApproval> _approvals = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, PendingDynamicToolCall> _toolCalls = new(StringComparer.Ordinal);
  private readonly Dictionary<string, CodexLocalModelMetadata> _knownModelMetadata = new(StringComparer.Ordinal);
  private Process? _process;
  private StreamWriter? _input;
  private CancellationTokenSource? _processLifetime;
  private Task? _readerTask;
  private Task? _errorTask;
  private long _nextRequestId;
  private string? _activeOllamaUrl;
  private HarnessAvailability? _cachedAvailability;
  private bool _disposed;

  public CodexHarnessAdapter(
    CodexHarnessOptions options,
    ISettingsStore settingsStore,
    ILogger<CodexHarnessAdapter> logger
  )
  {
    _options = options;
    _settingsStore = settingsStore;
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
    var cached = _cachedAvailability;
    if (
      cached is not null
      && DateTimeOffset.UtcNow - cached.CheckedAt < AvailabilityCacheDuration
    )
    {
      return cached;
    }

    await _availabilityGate.WaitAsync(cancellationToken);
    try
    {
      cached = _cachedAvailability;
      if (
        cached is not null
        && DateTimeOffset.UtcNow - cached.CheckedAt < AvailabilityCacheDuration
      )
      {
        return cached;
      }

      var executable = ResolveExecutable();
      Directory.CreateDirectory(_options.RuntimeDirectory);
      var startInfo = new ProcessStartInfo
      {
        FileName = executable,
        WorkingDirectory = _options.RuntimeDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(false, true),
        StandardErrorEncoding = new UTF8Encoding(false, true)
      };
      startInfo.ArgumentList.Add("--version");
      startInfo.Environment["CODEX_HOME"] = _options.RuntimeDirectory;

      using var process = new Process { StartInfo = startInfo };
      try
      {
        if (!process.Start())
        {
          throw new InvalidOperationException("Process.Start returned false.");
        }
      }
      catch (Exception exception)
      {
        throw CreateStartException(executable, exception);
      }

      var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
      var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
      using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutSource.CancelAfter(_options.StartupTimeout);
      try
      {
        await process.WaitForExitAsync(timeoutSource.Token);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        if (!process.HasExited)
        {
          process.Kill(true);
        }
        throw new HarnessException(
          "codex-version-timeout",
          "Codex version detection timed out.",
          $"'{executable} --version' exceeded {_options.StartupTimeout.TotalSeconds:0} seconds.",
          true
        );
      }

      var output = (await outputTask).Trim();
      var error = (await errorTask).Trim();
      if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
      {
        throw new HarnessException(
          "codex-version-failed",
          "Codex version detection failed.",
          $"Exit {process.ExitCode}: {Truncate(error)}",
          true
        );
      }

      _cachedAvailability = HarnessAvailability.Ready(
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]
      );
    }
    catch (HarnessException exception)
    {
      _logger.LogInformation(
        "Codex harness unavailable during discovery: {Code}. {Diagnostic}",
        exception.Code,
        exception.TechnicalMessage
      );
      _cachedAvailability = HarnessAvailability.Missing(exception.Message);
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
    ValidateTurn(request);
    var eventIdleTimeout = TimeSpan.FromSeconds(
      (await _settingsStore.GetAsync(cancellationToken)).Runtime.GenerationTimeoutSeconds
    );
    await _turnGate.WaitAsync(cancellationToken);
    ActiveHarnessTurn? active = null;

    try
    {
      if (!string.Equals(request.HarnessId, HarnessIds.Codex, StringComparison.OrdinalIgnoreCase))
      {
        throw new HarnessException(
          "codex-request-invalid",
          "The Codex adapter received a request for another harness.",
          $"Requested harness '{request.HarnessId}'.",
          false
        );
      }
      var providerEndpoint = request.ProviderEndpoint ?? throw new HarnessException(
        "codex-provider-endpoint-missing",
        "Codex requires the selected Ollama Local endpoint.",
        "ProviderEndpoint was null.",
        false
      );
      var contextConfiguration = ResolveContextConfiguration(request);
      await EnsureStartedAsync(
        request,
        contextConfiguration,
        providerEndpoint,
        cancellationToken
      );
      var harnessSession = await GetOrStartThreadAsync(
        request,
        contextConfiguration,
        cancellationToken
      );
      var threadId = harnessSession.NativeSessionId;
      var turnPrompt = HarnessConversationPromptBuilder.Create(
        request,
        harnessSession.SynchronizedThroughVersion,
        [
          "Agentic Router common capabilities are additive to Codex built-ins. Use Codex native filesystem and sandboxed command tools when suitable, and use the offered Host tools for structured Host-owned operations.",
          request.HostCapabilities is null
            ? "No Host capability profile was supplied."
            : $"Host approval policy: {request.HostCapabilities.ApprovalPolicy}. Host bridge tools: {string.Join(", ", HarnessCapabilityProjection.HostBridgeTools(HarnessIds.Codex, request.HostCapabilities))}."
        ]
      );
      active = new ActiveHarnessTurn(
        request.SessionId,
        threadId,
        request.WorkingDirectory,
        request.HostCapabilities is null
          ? []
          : HarnessCapabilityProjection.HostBridgeTools(HarnessIds.Codex, request.HostCapabilities)
      );

      if (!_activeByThread.TryAdd(threadId, active))
      {
        throw new HarnessException(
          "codex-thread-busy",
          "The Codex thread is already running another turn.",
          $"Thread {threadId} already has an active turn.",
          true
        );
      }

      var turnResult = await SendRequestAsync(
        "turn/start",
        new
        {
          threadId,
          input = CreateTurnInput(turnPrompt.Text, request.Images),
          cwd = request.WorkingDirectory,
          approvalPolicy = request.ApprovalPolicy == "ask" ? "on-request" : "never",
          permissions = PermissionProfileId,
          runtimeWorkspaceRoots = new[] { request.WorkingDirectory },
          model = request.Model
        },
        _options.StartupTimeout,
        cancellationToken
      );
      active.TurnId = RequiredString(
        turnResult,
        "turn",
        "id"
      );

      yield return CreateEvent(
        active,
        new HarnessEvent(
          "turn.started",
          $"Codex turn {active.TurnId} started."
        ),
        turnResult
      );

      await foreach (var harnessEvent in ReadTurnEventsAsync(
        active,
        eventIdleTimeout,
        cancellationToken
      ))
      {
        if (harnessEvent.IsTerminal)
        {
          harnessSession.SynchronizedThroughVersion = turnPrompt.SynchronizedThroughVersion;
        }
        yield return harnessEvent;

        if (harnessEvent.IsTerminal)
        {
          yield break;
        }
      }

      throw new HarnessException(
        "codex-stream-ended",
        "The Codex event stream ended before the turn reached a terminal state.",
        "The active turn channel completed without turn/completed.",
        true
      );
    }
    finally
    {
      if (cancellationToken.IsCancellationRequested)
      {
        await CancelTurnAsync(request.SessionId, CancellationToken.None);
      }
      if (active is not null)
      {
        _activeByThread.TryRemove(active.ThreadId, out _);
        foreach (var approval in _approvals.Where(pair => pair.Value.ThreadId == active.ThreadId).ToArray())
        {
          _approvals.TryRemove(approval.Key, out _);
        }
        foreach (var toolCall in _toolCalls.Where(pair => pair.Value.ThreadId == active.ThreadId).ToArray())
        {
          _toolCalls.TryRemove(toolCall.Key, out _);
        }
      }

      _turnGate.Release();
    }
  }

  private async IAsyncEnumerable<HarnessEvent> ReadTurnEventsAsync(
    ActiveHarnessTurn active,
    TimeSpan eventIdleTimeout,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    while (true)
    {
      var timedOut = false;
      var canRead = false;
      using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
      {
        timeout.CancelAfter(eventIdleTimeout);
        try
        {
          canRead = await active.Events.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
          timedOut = true;
        }
      }

      if (timedOut)
      {
        await CancelTurnAsync(active.SessionId, CancellationToken.None);
        yield return CreateEvent(
          active,
          new HarnessEvent(
            "turn.timed-out",
            $"Codex produced no event within the configured {eventIdleTimeout.TotalSeconds:0}-second generation timeout.",
            output: $"No Codex App Server event arrived for {eventIdleTimeout.TotalSeconds:0} seconds while the turn remained active.",
            errorCode: "codex-event-idle-timeout",
            terminalState: HarnessTerminalState.TimedOut
          )
        );
        yield break;
      }

      if (!canRead)
      {
        yield break;
      }

      while (active.Events.Reader.TryRead(out var harnessEvent))
      {
        yield return harnessEvent;
      }
    }
  }

  private static IReadOnlyList<object> CreateTurnInput(
    string text,
    IReadOnlyList<HarnessImageInput>? images
  )
  {
    var input = new List<object>
    {
      new
      {
        type = "text",
        text
      }
    };
    if (images is null)
    {
      return input;
    }
    input.AddRange(
      images.Select(
        image => (object)new
        {
          type = "image",
          url = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}",
          detail = "auto"
        }
      )
    );
    return input;
  }

  public async Task ResolveApprovalAsync(
    string approvalId,
    bool approved,
    CancellationToken cancellationToken
  )
  {
    if (!_approvals.TryRemove(approvalId, out var approval))
    {
      throw new HarnessException(
        "codex-approval-stale",
        "The Codex approval is no longer pending.",
        $"No pending server request exists for approval {approvalId}.",
        true
      );
    }

    await SendResponseAsync(
      approval.ServerRequestId,
      new
      {
        decision = approved ? "accept" : "decline"
      },
      cancellationToken
    );
  }

  public async Task ResolveToolCallAsync(
    string toolCallId,
    bool succeeded,
    string output,
    CancellationToken cancellationToken
  )
  {
    if (!_toolCalls.TryRemove(toolCallId, out var pending))
    {
      throw new HarnessException(
        "codex-tool-call-stale",
        "The Codex Host tool call is no longer pending.",
        $"No pending dynamic tool request exists for call {toolCallId}.",
        true
      );
    }

    await SendResponseAsync(
      pending.ServerRequestId,
      new
      {
        contentItems = new[]
        {
          new
          {
            type = "inputText",
            text = Truncate(output)
          }
        },
        success = succeeded
      },
      cancellationToken
    );
  }

  public async Task CancelTurnAsync(
    string conversationId,
    CancellationToken cancellationToken
  )
  {
    if (
      !_threadsByConversation.TryGetValue(conversationId, out var harnessSession)
      || !_activeByThread.TryGetValue(harnessSession.NativeSessionId, out var active)
      || string.IsNullOrWhiteSpace(active.TurnId)
    )
    {
      return;
    }

    try
    {
      await SendRequestAsync(
        "turn/interrupt",
        new
        {
          threadId = active.ThreadId,
          turnId = active.TurnId
        },
        _options.InterruptTimeout,
        cancellationToken
      );
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
      _logger.LogWarning(
        exception,
        "Codex turn {TurnId} did not acknowledge interruption; terminating the owned App Server.",
        active.TurnId
      );
      await StopOwnedProcessAsync();
    }
  }

  public async Task<HarnessSteerResult> SteerTurnAsync(
    HarnessSteerRequest request,
    CancellationToken cancellationToken
  )
  {
    if (
      !_threadsByConversation.TryGetValue(request.SessionId, out var harnessSession)
      || !_activeByThread.TryGetValue(harnessSession.NativeSessionId, out var active)
      || string.IsNullOrWhiteSpace(active.TurnId)
    )
    {
      throw new HarnessException(
        "codex-steer-stale",
        "The Codex turn is no longer available for steering.",
        $"No active Codex turn exists for conversation {request.SessionId}.",
        true
      );
    }

    var result = await SendRequestAsync(
      "turn/steer",
      new
      {
        threadId = active.ThreadId,
        input = CreateTurnInput(request.Message, null),
        expectedTurnId = active.TurnId
      },
      _options.InterruptTimeout,
      cancellationToken
    );
    var returnedTurnId = RequiredString(result, "turnId");
    if (!string.Equals(returnedTurnId, active.TurnId, StringComparison.Ordinal))
    {
      throw new HarnessException(
        "codex-steer-turn-mismatch",
        "Codex accepted steering for a different turn.",
        $"Expected turn {active.TurnId}, received {returnedTurnId}.",
        false
      );
    }

    return new HarnessSteerResult(
      HarnessIds.Codex,
      request.SessionId,
      returnedTurnId,
      request.MessageId,
      true
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
    _writeGate.Dispose();
    _turnGate.Dispose();
  }

  private async Task EnsureStartedAsync(
    HarnessTurnRequest request,
    CodexContextConfiguration contextConfiguration,
    Uri ollamaUrl,
    CancellationToken cancellationToken
  )
  {
    await _lifecycleGate.WaitAsync(cancellationToken);

    try
    {
      var catalogChanged = RegisterModelMetadata(request, contextConfiguration);
      if (
        _process is { HasExited: false }
        && string.Equals(_activeOllamaUrl, ollamaUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
        && !catalogChanged
      )
      {
        return;
      }

      if (_process is { HasExited: false })
      {
        if (_activeByThread.Count > 0)
        {
          throw new HarnessException(
            "codex-runtime-configuration-busy",
            "The Codex runtime configuration cannot change while a turn is active.",
            $"Active endpoint {_activeOllamaUrl}; requested {ollamaUrl}; model catalog changed: {catalogChanged}.",
            true
          );
        }

        await StopOwnedProcessAsync();
      }
      else if (_process is not null)
      {
        _process.Dispose();
        _process = null;
        _input = null;
        _processLifetime?.Dispose();
        _processLifetime = null;
        _attachedThreads.Clear();
      }

      var executable = ResolveExecutable();
      Directory.CreateDirectory(_options.RuntimeDirectory);
      await WriteIsolatedConfigurationAsync(cancellationToken);
      var startInfo = new ProcessStartInfo
      {
        FileName = executable,
        WorkingDirectory = _options.RuntimeDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false),
        StandardOutputEncoding = new UTF8Encoding(false, true),
        StandardErrorEncoding = new UTF8Encoding(false, true)
      };
      startInfo.ArgumentList.Add("app-server");
      startInfo.ArgumentList.Add("--listen");
      startInfo.ArgumentList.Add("stdio://");
      startInfo.ArgumentList.Add("--strict-config");
      startInfo.ArgumentList.Add("--disable");
      startInfo.ArgumentList.Add("remote_plugin");
      startInfo.ArgumentList.Add("--disable");
      startInfo.ArgumentList.Add("plugins");
      startInfo.ArgumentList.Add("--disable");
      startInfo.ArgumentList.Add("remote_control");
      startInfo.Environment["CODEX_HOME"] = _options.RuntimeDirectory;
      var ollamaAuthority = ollamaUrl.GetLeftPart(UriPartial.Authority);
      startInfo.Environment["OLLAMA_HOST"] = ollamaAuthority;
      startInfo.Environment["CODEX_OSS_BASE_URL"] = $"{ollamaAuthority.TrimEnd('/')}/v1";

      var process = new Process
      {
        StartInfo = startInfo,
        EnableRaisingEvents = true
      };
      process.Exited += (_, _) => HandleUnexpectedExit(process);

      try
      {
        if (!process.Start())
        {
          throw new InvalidOperationException("Process.Start returned false.");
        }
      }
      catch (Exception exception)
      {
        process.Dispose();
        throw CreateStartException(executable, exception);
      }

      _process = process;
      _input = process.StandardInput;
      _input.AutoFlush = true;
      _processLifetime = new CancellationTokenSource();
      _readerTask = ReadLoopAsync(process, _processLifetime.Token);
      _errorTask = ReadErrorLoopAsync(process, _processLifetime.Token);
      _activeOllamaUrl = ollamaUrl.AbsoluteUri;
      _attachedThreads.Clear();

      await SendRequestAsync(
        "initialize",
        new
        {
          clientInfo = new
          {
            name = "agentic_router",
            title = "Agentic Router",
            version = "0.9.16"
          },
          capabilities = new
          {
            experimentalApi = true
          }
        },
        _options.StartupTimeout,
        cancellationToken
      );
      await SendNotificationAsync("initialized", new { }, cancellationToken);
    }
    catch
    {
      if (_process is { HasExited: false } && _input is not null)
      {
        await StopOwnedProcessAsync();
      }
      throw;
    }
    finally
    {
      _lifecycleGate.Release();
    }
  }

  private async Task<HarnessSessionState> GetOrStartThreadAsync(
    HarnessTurnRequest request,
    CodexContextConfiguration contextConfiguration,
    CancellationToken cancellationToken
  )
  {
    if (_threadsByConversation.TryGetValue(request.SessionId, out var existing)
      && string.Equals(
        existing.CapabilitySignature,
        request.HostCapabilities?.Signature,
        StringComparison.Ordinal
      )
      && existing.ContextConfiguration == contextConfiguration)
    {
      if (_attachedThreads.ContainsKey(existing.NativeSessionId))
      {
        return existing;
      }

      JsonElement resumed;
      try
      {
        resumed = await SendRequestAsync(
          "thread/resume",
          new
          {
            threadId = existing.NativeSessionId,
            model = request.Model,
            modelProvider = "ollama",
            cwd = request.WorkingDirectory,
            approvalPolicy = request.ApprovalPolicy == "ask" ? "on-request" : "never",
            permissions = PermissionProfileId,
            runtimeWorkspaceRoots = new[] { request.WorkingDirectory },
            config = CreateThreadConfig(contextConfiguration)
          },
          _options.StartupTimeout,
          cancellationToken
        );
      }
      catch (HarnessException exception)
      {
        throw new HarnessException(
          "codex-session-resume-failed",
          "Codex could not resume the existing harness session.",
          $"Thread {existing.NativeSessionId}: {exception.TechnicalMessage}",
          true,
          exception
        );
      }
      VerifyThreadSelection(resumed, request);
      _attachedThreads[existing.NativeSessionId] = 0;
      return existing;
    }

    var result = await SendRequestAsync(
      "thread/start",
      new
      {
        model = request.Model,
        modelProvider = "ollama",
        cwd = request.WorkingDirectory,
        approvalPolicy = request.ApprovalPolicy == "ask" ? "on-request" : "never",
        permissions = PermissionProfileId,
        runtimeWorkspaceRoots = new[] { request.WorkingDirectory },
        serviceName = "agentic_router",
        dynamicTools = CreateDynamicTools(request.HostCapabilities),
        config = CreateThreadConfig(contextConfiguration)
      },
      _options.StartupTimeout,
      cancellationToken
    );
    var threadId = RequiredString(result, "thread", "id");
    VerifyThreadSelection(result, request);
    var harnessSession = new HarnessSessionState(
      threadId,
      request.HostCapabilities?.Signature,
      contextConfiguration
    );
    _threadsByConversation[request.SessionId] = harnessSession;
    _attachedThreads[threadId] = 0;
    return harnessSession;
  }

  private static void VerifyThreadSelection(
    JsonElement result,
    HarnessTurnRequest request
  )
  {
    var provider = RequiredString(result, "thread", "modelProvider");
    var selectedModel = RequiredString(result, "model");
    var sandboxType = RequiredString(result, "sandbox", "type");
    var permissionProfile = RequiredString(result, "activePermissionProfile", "id");

    if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
    {
      throw new HarnessException(
        "codex-provider-substitution",
        "Codex did not use the selected Ollama Local provider.",
        $"App Server reported modelProvider '{provider}' for model '{request.Model}'.",
        false
      );
    }
    if (!string.Equals(selectedModel, request.Model, StringComparison.Ordinal))
    {
      throw new HarnessException(
        "codex-model-substitution",
        "Codex did not use the exact selected model.",
        $"App Server reported model '{selectedModel}' after requesting '{request.Model}'.",
        false
      );
    }
    if (!string.Equals(sandboxType, "workspaceWrite", StringComparison.Ordinal))
    {
      throw new HarnessException(
        "codex-sandbox-incompatible",
        "Codex did not activate the required workspace-write sandbox.",
        $"App Server reported sandbox type '{sandboxType}'.",
        false
      );
    }
    if (!string.Equals(permissionProfile, PermissionProfileId, StringComparison.Ordinal))
    {
      throw new HarnessException(
        "codex-permission-profile-incompatible",
        "Codex did not activate the Agentic Router workspace permission profile.",
        $"App Server reported permission profile '{permissionProfile}'.",
        false
      );
    }
  }

  private async Task<JsonElement> SendRequestAsync(
    string method,
    object parameters,
    TimeSpan timeout,
    CancellationToken cancellationToken
  )
  {
    var id = Interlocked.Increment(ref _nextRequestId);
    var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!_pending.TryAdd(id, source))
    {
      throw new InvalidOperationException("Duplicate Codex request identifier.");
    }

    try
    {
      await WriteMessageAsync(new { method, id, @params = parameters }, cancellationToken);
      using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeoutSource.CancelAfter(timeout);
      return await source.Task.WaitAsync(timeoutSource.Token);
    }
    catch (HarnessException exception) when (exception.Code == "codex-protocol-error")
    {
      throw new HarnessException(
        exception.Code,
        exception.Message,
        $"Method {method}: {exception.TechnicalMessage}",
        exception.Recoverable,
        exception
      );
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
      throw new HarnessException(
        "codex-protocol-timeout",
        "Codex App Server did not respond in time.",
        $"Method {method} exceeded {timeout.TotalSeconds:0} seconds.",
        true
      );
    }
    finally
    {
      _pending.TryRemove(id, out _);
    }
  }

  private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken)
  {
    return WriteMessageAsync(new { method, @params = parameters }, cancellationToken);
  }

  private Task SendResponseAsync(long id, object result, CancellationToken cancellationToken)
  {
    return WriteMessageAsync(new { id, result }, cancellationToken);
  }

  private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
  {
    var input = _input ?? throw new HarnessException(
      "codex-process-unavailable",
      "Codex App Server is not running.",
      "The JSONL input stream is unavailable.",
      true
    );
    var json = JsonSerializer.Serialize(message);
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      await input.WriteLineAsync(json.AsMemory(), cancellationToken);
      await input.FlushAsync(cancellationToken);
    }
    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
    {
      throw new HarnessException(
        "codex-protocol-write",
        "The Codex protocol connection failed.",
        exception.Message,
        true,
        exception
      );
    }
    finally
    {
      _writeGate.Release();
    }
  }

  private async Task ReadLoopAsync(Process process, CancellationToken cancellationToken)
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null)
        {
          break;
        }

        JsonDocument document;
        try
        {
          document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
          FailActiveTurns(new HarnessException(
            "codex-protocol-json",
            "Codex App Server returned an invalid protocol message.",
            exception.Message,
            true,
            exception
          ));
          continue;
        }

        using (document)
        {
          var root = document.RootElement;
          if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
          {
            if (root.TryGetProperty("method", out _))
            {
              HandleServerRequest(id, root);
            }
            else if (_pending.TryGetValue(id, out var pending))
            {
              if (root.TryGetProperty("error", out var error))
              {
                pending.TrySetException(CreateProtocolError(error));
              }
              else if (root.TryGetProperty("result", out var result))
              {
                pending.TrySetResult(result.Clone());
              }
              else
              {
                pending.TrySetException(new HarnessException(
                  "codex-protocol-response",
                  "Codex App Server returned an incomplete response.",
                  line,
                  true
                ));
              }
            }
            continue;
          }

          HandleNotification(root);
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
      FailActiveTurns(new HarnessException(
        "codex-protocol-stream",
        "The Codex event stream failed.",
        exception.Message,
        true,
        exception
      ));
    }
  }

  private async Task ReadErrorLoopAsync(Process process, CancellationToken cancellationToken)
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        var line = await process.StandardError.ReadLineAsync(cancellationToken);
        if (line is null)
        {
          return;
        }
        _logger.LogDebug("Codex App Server: {Message}", Truncate(line));
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
  }

  private void HandleServerRequest(long id, JsonElement root)
  {
    var method = root.GetProperty("method").GetString() ?? string.Empty;
    var parameters = root.TryGetProperty("params", out var value) ? value : default;
    var threadId = GetString(parameters, "threadId");

    if (method == "item/tool/call")
    {
      HandleDynamicToolCall(id, parameters, threadId, root);
      return;
    }

    if (threadId is null || !_activeByThread.TryGetValue(threadId, out var active))
    {
      _ = SendResponseAsync(id, new { decision = "decline" }, CancellationToken.None);
      return;
    }

    if (method is not "item/commandExecution/requestApproval" and not "item/fileChange/requestApproval")
    {
      active.UnsupportedApprovalCount++;
      active.Events.Writer.TryWrite(
        CreateEvent(
          active,
          new HarnessEvent(
            "approval.requested",
            $"Unsupported Codex approval request: {method}.",
            approvalId: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            approvalCanBeMapped: false,
            recoveryExhausted: active.UnsupportedApprovalCount > 1,
            errorCode: active.UnsupportedApprovalCount > 1
              ? "codex-approval-unsupported-repeated"
              : "codex-approval-unsupported"
          ),
          root
        )
      );
      _approvals[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new PendingServerApproval(id, threadId);
      return;
    }

    var itemId = GetString(parameters, "itemId");
    JsonElement? item = itemId is null
      ? null
      : active.Items.GetValueOrDefault(itemId);
    var fileChange = method == "item/fileChange/requestApproval";
    var destructive = fileChange && item is JsonElement fileItem && FileChangeDeletes(fileItem);
    var paths = fileChange
      ? FileChangePaths(item)
      : CommandApprovalPaths(parameters, active.WorkingDirectory);
    var summary = fileChange
      ? SummarizeFileChange(item)
      : Truncate(GetString(parameters, "command") ?? GetString(item, "command") ?? "Codex command approval");
    var approvalId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var approvalCanBeMapped = fileChange
      ? FileChangeIsWorkspaceConfined(item, active.WorkingDirectory)
      : CommandApprovalIsWorkspaceConfined(parameters, active.WorkingDirectory);
    if (!approvalCanBeMapped)
    {
      active.UnsupportedApprovalCount++;
    }
    _approvals[approvalId] = new PendingServerApproval(id, threadId);
    active.Events.Writer.TryWrite(
      CreateEvent(
        active,
        new HarnessEvent(
          "approval.requested",
          summary,
          itemId: itemId,
          tool: fileChange ? "codex_file_change" : "codex_command",
          state: "awaiting-approval",
          approvalId: approvalId,
          approvalCanBeMapped: approvalCanBeMapped,
          destructive: destructive,
          recoveryExhausted: !approvalCanBeMapped && active.UnsupportedApprovalCount > 1,
          errorCode: !approvalCanBeMapped && active.UnsupportedApprovalCount > 1
            ? "codex-approval-unsupported-repeated"
            : !approvalCanBeMapped
              ? "codex-approval-unsupported"
              : null,
          paths: paths
        ),
        root
      )
    );
  }

  private void HandleDynamicToolCall(
    long id,
    JsonElement parameters,
    string? threadId,
    JsonElement root
  )
  {
    var callId = GetString(parameters, "callId");
    var tool = GetString(parameters, "tool");
    if (
      threadId is null
      || callId is null
      || tool is null
      || !_activeByThread.TryGetValue(threadId, out var active)
      || !parameters.TryGetProperty("arguments", out var arguments)
    )
    {
      _ = SendResponseAsync(
        id,
        new
        {
          contentItems = new[]
          {
            new { type = "inputText", text = "The Host rejected an incomplete dynamic tool request." }
          },
          success = false
        },
        CancellationToken.None
      );
      return;
    }

    if (!active.HostBridgeTools.Contains(tool))
    {
      _ = SendResponseAsync(
        id,
        new
        {
          contentItems = new[]
          {
            new { type = "inputText", text = $"Host tool '{tool}' is not available for this Codex thread." }
          },
          success = false
        },
        CancellationToken.None
      );
      return;
    }

    if (!_toolCalls.TryAdd(callId, new PendingDynamicToolCall(id, threadId)))
    {
      _ = SendResponseAsync(
        id,
        new
        {
          contentItems = new[]
          {
            new { type = "inputText", text = $"Duplicate Host tool call id '{callId}'." }
          },
          success = false
        },
        CancellationToken.None
      );
      return;
    }

    active.Events.Writer.TryWrite(
      CreateEvent(
        active,
        new HarnessEvent(
          "host-tool.requested",
          $"Codex requested Host tool {tool}.",
          tool: tool,
          state: "proposed",
          toolCallId: callId,
          arguments: arguments.Clone()
        ),
        root
      )
    );
  }

  private void HandleNotification(JsonElement root)
  {
    if (!root.TryGetProperty("method", out var methodElement))
    {
      return;
    }
    var method = methodElement.GetString() ?? string.Empty;
    var parameters = root.TryGetProperty("params", out var value) ? value : default;
    var threadId = GetString(parameters, "threadId")
      ?? GetString(parameters, "thread", "id");
    if (threadId is null && _activeByThread.Count == 1)
    {
      threadId = _activeByThread.Keys.Single();
    }
    if (threadId is null || !_activeByThread.TryGetValue(threadId, out var active))
    {
      _logger.LogDebug("Ignoring Codex notification {Method} without an active matching thread.", method);
      return;
    }

    switch (method)
    {
      case "turn/started":
        break;
      case "item/agentMessage/delta":
        active.Events.Writer.TryWrite(CreateEvent(
          active,
          new HarnessEvent(
            "assistant.delta",
            delta: GetString(parameters, "delta"),
            itemId: GetString(parameters, "itemId")
          ),
          root
        ));
        break;
      case "item/reasoning/summaryTextDelta":
      case "item/reasoning/textDelta":
        active.Events.Writer.TryWrite(CreateEvent(
          active,
          new HarnessEvent(
            "reasoning.delta",
            delta: GetString(parameters, "delta"),
            itemId: GetString(parameters, "itemId")
          ),
          root
        ));
        break;
      case "item/commandExecution/outputDelta":
        active.Events.Writer.TryWrite(CreateEvent(
          active,
          new HarnessEvent(
            "tool.output",
            delta: Truncate(GetString(parameters, "delta")),
            itemId: GetString(parameters, "itemId"),
            tool: "codex_command",
            state: "running"
          ),
          root
        ));
        break;
      case "item/started":
        HandleItem(active, parameters, root, false);
        break;
      case "item/completed":
        HandleItem(active, parameters, root, true);
        break;
      case "turn/diff/updated":
        active.Events.Writer.TryWrite(CreateEvent(
          active,
          new HarnessEvent(
            "files.changed",
            "Codex reported an updated workspace diff.",
            output: Truncate(GetString(parameters, "diff"))
          ),
          root
        ));
        break;
      case "thread/tokenUsage/updated":
        {
          var contextInputTokens = GetInt64(
            parameters,
            "tokenUsage",
            "last",
            "inputTokens"
          );
          var contextTotalTokens = GetInt64(
            parameters,
            "tokenUsage",
            "last",
            "totalTokens"
          );
          if (contextInputTokens is > 0 || contextTotalTokens is > 0)
          {
            active.Events.Writer.TryWrite(CreateEvent(
              active,
              new HarnessEvent(
                "usage.updated",
                "Codex reported live active-context usage.",
                contextInputTokens: contextInputTokens,
                contextTotalTokens: contextTotalTokens,
                contextWindowTokens: GetInt64(
                  parameters,
                  "tokenUsage",
                  "modelContextWindow"
                )
              ),
              root
            ));
          }
          break;
        }
      case "error":
        active.Events.Writer.TryWrite(CreateEvent(
          active,
          new HarnessEvent(
            "error",
            "Codex App Server reported a turn error.",
            errorCode: ReadCodexErrorCode(parameters)
          ),
          root
        ));
        break;
      case "turn/completed":
        CompleteTurn(active, parameters, root);
        break;
      case "model/rerouted":
        _ = RejectModelRerouteAsync(
          active,
          GetString(parameters, "fromModel"),
          GetString(parameters, "toModel"),
          root
        );
        break;
      case "warning":
      case "configWarning":
        active.Events.Writer.TryWrite(CreateEvent(
          active,
          new HarnessEvent(
            "warning",
            Truncate(GetString(parameters, "message") ?? GetString(parameters, "summary") ?? "Codex warning.")
          ),
          root
        ));
        break;
      default:
        _logger.LogDebug("Ignoring unknown Codex notification {Method}.", method);
        active.Events.Writer.TryWrite(CreateEvent(
          active,
          new HarnessEvent(
            "native.event",
            $"Codex notification {method}."
          ),
          root
        ));
        break;
    }
  }

  private static void HandleItem(
    ActiveHarnessTurn active,
    JsonElement parameters,
    JsonElement root,
    bool completed
  )
  {
    if (!parameters.TryGetProperty("item", out var item))
    {
      return;
    }
    var itemId = GetString(item, "id");
    var type = GetString(item, "type");
    if (itemId is not null)
    {
      active.Items[itemId] = item.Clone();
    }
    if (type is not "commandExecution" and not "fileChange")
    {
      return;
    }
    var tool = type == "fileChange" ? "codex_file_change" : "codex_command";
    var status = GetString(item, "status") ?? (completed ? "completed" : "inProgress");
    var succeeded = status is "completed" or "success";
    var message = type == "fileChange"
      ? SummarizeFileChange(item)
      : Truncate(GetString(item, "command") ?? "Codex command");
    active.Events.Writer.TryWrite(CreateEvent(
      active,
      new HarnessEvent(
        completed ? succeeded ? "tool.completed" : "tool.failed" : "tool.started",
        message,
        itemId: itemId,
        tool: tool,
        state: completed ? succeeded ? "completed" : "failed" : "running",
        output: completed ? Truncate(GetString(item, "aggregatedOutput")) : null
      ),
      root
    ));
  }

  private static void CompleteTurn(
    ActiveHarnessTurn active,
    JsonElement parameters,
    JsonElement root
  )
  {
    var status = GetString(parameters, "turn", "status") ?? "failed";
    var failure = ReadCodexFailure(parameters);
    var result = status switch
    {
      "completed" => new HarnessEvent(
        "turn.completed",
        "Codex finished its turn.",
        terminalState: HarnessTerminalState.Completed
      ),
      "interrupted" => new HarnessEvent(
        "turn.cancelled",
        "Codex turn was interrupted.",
        terminalState: HarnessTerminalState.Cancelled
      ),
      _ => new HarnessEvent(
        failure.TimedOut ? "turn.timed-out" : "turn.failed",
        failure.Message,
        output: failure.TechnicalMessage,
        errorCode: failure.Code,
        terminalState: failure.TimedOut
          ? HarnessTerminalState.TimedOut
          : HarnessTerminalState.Failed
      )
    };
    active.TryComplete(CreateEvent(active, result, root));
  }

  private async Task RejectModelRerouteAsync(
    ActiveHarnessTurn active,
    string? fromModel,
    string? toModel,
    JsonElement nativePayload
  )
  {
    active.TryComplete(CreateEvent(
      active,
      new HarnessEvent(
        "turn.failed",
        "Codex attempted to substitute the selected model.",
        errorCode: "codex-model-substitution",
        output: $"Requested {fromModel ?? "unknown"}; rerouted to {toModel ?? "unknown"}.",
        terminalState: HarnessTerminalState.Failed
      ),
      nativePayload
    ));
    await CancelTurnAsync(active.SessionId, CancellationToken.None);
  }

  private void HandleUnexpectedExit(Process process)
  {
    if (!ReferenceEquals(process, _process) || _processLifetime?.IsCancellationRequested == true)
    {
      return;
    }
    FailActiveTurns(new HarnessException(
      "codex-app-server-exited",
      "Codex App Server exited unexpectedly.",
      $"Owned process {process.Id} exited with code {SafeExitCode(process)}.",
      true
    ));
  }

  private void FailActiveTurns(HarnessException exception)
  {
    foreach (var pending in _pending.Values)
    {
      pending.TrySetException(exception);
    }
    foreach (var active in _activeByThread.Values)
    {
      var terminalState = exception.Code switch
      {
        "codex-protocol-timeout" or "codex-version-timeout" => HarnessTerminalState.TimedOut,
        "codex-executable-not-found" or "codex-executable-access-denied" => HarnessTerminalState.Unavailable,
        _ => HarnessTerminalState.Failed
      };
      active.TryComplete(CreateEvent(
        active,
        new HarnessEvent(
          "turn.failed",
          exception.Message,
          output: exception.TechnicalMessage,
          errorCode: exception.Code,
          terminalState: terminalState
        )
      ));
    }
  }

  private async Task StopOwnedProcessAsync()
  {
    var process = _process;
    _process = null;
    _input = null;
    _activeOllamaUrl = null;
    _attachedThreads.Clear();
    _processLifetime?.Cancel();
    _processLifetime?.Dispose();
    _processLifetime = null;
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

  private async Task WriteIsolatedConfigurationAsync(CancellationToken cancellationToken)
  {
    var catalogPath = await WriteModelCatalogAsync(cancellationToken);
    var path = Path.Combine(_options.RuntimeDirectory, "config.toml");
    var content = "model_provider = \"ollama\"\n"
      + "oss_provider = \"ollama\"\n"
      + $"model_catalog_json = \"{TomlPath(catalogPath)}\"\n"
      + "approval_policy = \"on-request\"\n"
      + $"default_permissions = \"{PermissionProfileId}\"\n"
      + "check_for_update_on_startup = false\n"
      + "web_search = \"disabled\"\n\n"
      + "[agents]\n"
      + "enabled = false\n\n"
      + "[analytics]\n"
      + "enabled = false\n\n"
      + "[feedback]\n"
      + "enabled = false\n\n"
      + "[features]\n"
      + "shell_tool = false\n"
      + "unified_exec = false\n"
      + "memories = false\n"
      + "multi_agent = false\n"
      + "remote_plugin = false\n"
      + "skill_mcp_dependency_install = false\n\n"
      + "[tools]\n"
      + "web_search = false\n\n"
      + "[windows]\n"
      + "sandbox = \"unelevated\"\n";
    if (File.Exists(path) && string.Equals(await File.ReadAllTextAsync(path, cancellationToken), content, StringComparison.Ordinal))
    {
      return;
    }
    var temporary = path + ".tmp";
    await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
    File.Move(temporary, path, true);
  }

  private bool RegisterModelMetadata(
    HarnessTurnRequest request,
    CodexContextConfiguration contextConfiguration
  )
  {
    var supportsImages = request.Images is { Count: > 0 };
    var metadata = new CodexLocalModelMetadata(
      request.Model,
      contextConfiguration.ContextWindowTokens,
      supportsImages
    );
    if (_knownModelMetadata.TryGetValue(request.Model, out var current))
    {
      metadata = metadata with
      {
        SupportsImages = current.SupportsImages || metadata.SupportsImages
      };
      if (metadata == current)
      {
        return false;
      }
    }
    _knownModelMetadata[request.Model] = metadata;
    return true;
  }

  private async Task<string> WriteModelCatalogAsync(CancellationToken cancellationToken)
  {
    var path = Path.Combine(_options.RuntimeDirectory, "model-catalog.json");
    var models = _knownModelMetadata.Values
      .OrderBy(model => model.Slug, StringComparer.Ordinal)
      .Select((model, index) => new
      {
        slug = model.Slug,
        display_name = model.Slug,
        description = "Agentic Router local Ollama model.",
        supported_reasoning_levels = Array.Empty<object>(),
        shell_type = "shell_command",
        visibility = "list",
        supported_in_api = true,
        priority = index + 1,
        support_verbosity = false,
        truncation_policy = new
        {
          mode = "tokens",
          limit = 10_000
        },
        experimental_supported_tools = Array.Empty<string>(),
        context_window = model.ContextWindowTokens,
        max_context_window = model.ContextWindowTokens,
        effective_context_window_percent = 100,
        input_modalities = model.SupportsImages
          ? new[] { "text", "image" }
          : new[] { "text" },
        base_instructions = "You are a coding agent. Follow the supplied developer and user instructions, use the available tools to complete the request, and report only actions and results that actually occurred."
      })
      .ToArray();
    var content = JsonSerializer.Serialize(
      new { models },
      new JsonSerializerOptions
      {
        WriteIndented = true
      }
    ) + "\n";
    if (
      File.Exists(path)
      && string.Equals(
        await File.ReadAllTextAsync(path, cancellationToken),
        content,
        StringComparison.Ordinal
      )
    )
    {
      return path;
    }
    var temporary = path + ".tmp";
    await File.WriteAllTextAsync(
      temporary,
      content,
      new UTF8Encoding(false),
      cancellationToken
    );
    File.Move(temporary, path, true);
    return path;
  }

  private static string TomlPath(string path)
  {
    return Path.GetFullPath(path).Replace('\\', '/');
  }

  private string ResolveExecutable()
  {
    if (!string.IsNullOrWhiteSpace(_options.ExecutablePath))
    {
      var explicitPath = Path.GetFullPath(_options.ExecutablePath);
      if (!File.Exists(explicitPath))
      {
        throw NotFound($"Configured executable does not exist: {explicitPath}");
      }
      return explicitPath;
    }

    var managedExecutable = ResolveManagedExecutable();
    if (managedExecutable is not null)
    {
      return managedExecutable;
    }

    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    var extensions = OperatingSystem.IsWindows()
      ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
      : new[] { string.Empty };
    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      foreach (var extension in extensions)
      {
        var candidate = Path.GetFullPath(Path.Combine(directory, "codex" + extension.ToLowerInvariant()));
        if (File.Exists(candidate))
        {
          return candidate;
        }
      }
    }
    throw NotFound(
      "No codex executable was found in AgenticRouter:Codex:ExecutablePath, the managed user-local install root, or PATH."
    );
  }

  private string? ResolveManagedExecutable()
  {
    if (
      !OperatingSystem.IsWindows()
      || string.IsNullOrWhiteSpace(_options.ManagedInstallRoot)
    )
    {
      return null;
    }

    try
    {
      var root = Path.GetFullPath(_options.ManagedInstallRoot);
      if (!Directory.Exists(root))
      {
        return null;
      }

      return Directory.EnumerateDirectories(root)
        .Select(directory => Path.Combine(directory, "codex.exe"))
        .Where(File.Exists)
        .Select(path => new FileInfo(path))
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .ThenByDescending(file => file.DirectoryName, StringComparer.OrdinalIgnoreCase)
        .Select(file => file.FullName)
        .FirstOrDefault();
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException
    )
    {
      _logger.LogDebug(
        exception,
        "Codex managed-install discovery could not inspect {ManagedInstallRoot}.",
        _options.ManagedInstallRoot
      );
      return null;
    }
  }

  private static void ValidateTurn(HarnessTurnRequest request)
  {
    if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.Model))
    {
      throw new HarnessException("codex-request-invalid", "Codex requires a session and exact model.", "SessionId or Model was empty.", false);
    }
    if (!string.Equals(request.Provider, "ollama-local", StringComparison.OrdinalIgnoreCase))
    {
      throw new HarnessException(
        "codex-provider-unsupported",
        "Codex (Experimental) supports Ollama Local models only.",
        $"Requested provider '{request.Provider}'.",
        false
      );
    }
    if (request.ContextWindowTokens is not > 0)
    {
      throw new HarnessException(
        "codex-context-window-missing",
        "Codex requires the Host-resolved context window.",
        $"ContextWindowTokens was {request.ContextWindowTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}.",
        false
      );
    }
    var root = Path.GetFullPath(request.WorkingDirectory);
    if (!Directory.Exists(root))
    {
      throw new HarnessException("codex-workspace-invalid", "The trusted workspace is unavailable.", root, true);
    }
  }

  private static CodexContextConfiguration ResolveContextConfiguration(
    HarnessTurnRequest request
  )
  {
    var contextWindowTokens = request.ContextWindowTokens!.Value;
    var autoCompactTokenLimit = checked(
      (int)((long)contextWindowTokens * AutoCompactPercentage / 100)
    );
    if (autoCompactTokenLimit <= 0 || autoCompactTokenLimit >= contextWindowTokens)
    {
      throw new HarnessException(
        "codex-context-configuration-invalid",
        "Codex received an invalid Host context or compaction limit.",
        $"Context window {contextWindowTokens}; {AutoCompactPercentage}-percent auto-compaction limit {autoCompactTokenLimit}.",
        false
      );
    }
    return new CodexContextConfiguration(
      contextWindowTokens,
      autoCompactTokenLimit
    );
  }

  private static object CreateThreadConfig(
    CodexContextConfiguration configuration
  )
  {
    return new
    {
      model_context_window = configuration.ContextWindowTokens,
      model_auto_compact_token_limit = configuration.AutoCompactTokenLimit,
      model_auto_compact_token_limit_scope = "total"
    };
  }

  private static object[] CreateDynamicTools(HostCapabilityProfile? profile)
  {
    if (profile is null)
    {
      return [];
    }
    var bridgeTools = HarnessCapabilityProjection.HostBridgeTools(
      HarnessIds.Codex,
      profile
    );
    return LocalActionPlanner.GetToolDefinitions(bridgeTools)
      .Select(definition => (object)new
      {
        type = "function",
        name = definition.Name,
        description = definition.Description,
        inputSchema = definition.Parameters
      })
      .ToArray();
  }

  private static HarnessException NotFound(string technicalMessage)
  {
    return new HarnessException(
      "codex-executable-not-found",
      "The Codex executable was not found. Configure AgenticRouter:Codex:ExecutablePath and restart Agentic Router.",
      technicalMessage,
      true
    );
  }

  private static HarnessException CreateStartException(
    string executable,
    Exception exception
  )
  {
    if (
      exception is UnauthorizedAccessException
      || exception is Win32Exception { NativeErrorCode: 5 }
    )
    {
      return new HarnessException(
        "codex-executable-access-denied",
        "Windows denied access to the discovered Codex executable. Configure AgenticRouter:Codex:ExecutablePath with an accessible codex.exe and restart Agentic Router.",
        $"Access was denied while starting '{executable} app-server': {exception.Message}",
        true,
        exception
      );
    }

    return new HarnessException(
      "codex-start-failed",
      "Codex App Server could not be started.",
      $"Could not start '{executable} app-server': {exception.Message}",
      true,
      exception
    );
  }

  private static HarnessException CreateProtocolError(JsonElement error)
  {
    return new HarnessException(
      "codex-protocol-error",
      "Codex App Server rejected the request.",
      error.GetRawText(),
      true
    );
  }

  private static string RequiredString(JsonElement element, params string[] path)
  {
    return GetString(element, path) ?? throw new HarnessException(
      "codex-protocol-response",
      "Codex App Server returned an incomplete response.",
      $"Missing string field {string.Join('.', path)}.",
      true
    );
  }

  private static string? GetString(JsonElement? element, params string[] path)
  {
    if (element is null)
    {
      return null;
    }
    var current = element.Value;
    foreach (var segment in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
      {
        return null;
      }
    }
    return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
  }

  private static long? GetInt64(JsonElement? element, params string[] path)
  {
    if (element is null)
    {
      return null;
    }
    var current = element.Value;
    foreach (var segment in path)
    {
      if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
      {
        return null;
      }
    }
    return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var value)
      ? value
      : null;
  }

  private static bool FileChangeDeletes(JsonElement item)
  {
    return item.TryGetProperty("changes", out var changes)
      && changes.ValueKind == JsonValueKind.Array
      && changes.EnumerateArray().Any(change => string.Equals(GetString(change, "kind"), "delete", StringComparison.OrdinalIgnoreCase));
  }

  private static IReadOnlyList<string>? FileChangePaths(JsonElement? item)
  {
    if (item is null || !item.Value.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
    {
      return null;
    }
    return changes.EnumerateArray().Select(change => GetString(change, "path")).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray();
  }

  private static bool FileChangeIsWorkspaceConfined(JsonElement? item, string workspacePath)
  {
    if (item is null || !item.Value.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
    {
      return false;
    }
    var entries = changes.EnumerateArray().ToArray();
    if (entries.Length == 0)
    {
      return false;
    }

    try
    {
      var workspaceRoot = Path.GetFullPath(workspacePath);
      var rootPrefix = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
      foreach (var change in entries)
      {
        var path = GetString(change, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
          return false;
        }
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));
        if (!full.StartsWith(rootPrefix, FileSystemPathSemantics.Comparison))
        {
          return false;
        }
        var relative = Path.GetRelativePath(workspaceRoot, full).Replace('\\', '/');
        if (
          string.Equals(relative, ".git", FileSystemPathSemantics.Comparison)
          || relative.StartsWith(".git/", FileSystemPathSemantics.Comparison)
        )
        {
          return false;
        }
      }
      return true;
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return false;
    }
  }

  private static IReadOnlyList<string> CommandApprovalPaths(
    JsonElement parameters,
    string workspacePath
  )
  {
    var paths = new List<string>();
    if (parameters.TryGetProperty("commandActions", out var actions)
      && actions.ValueKind == JsonValueKind.Array)
    {
      paths.AddRange(
        actions.EnumerateArray()
          .Select(action => GetString(action, "path"))
          .Where(path => !string.IsNullOrWhiteSpace(path))
          .Cast<string>()
      );
    }
    paths.Add(GetString(parameters, "cwd") ?? workspacePath);
    return paths.Distinct(FileSystemPathSemantics.Comparer).ToArray();
  }

  private static bool CommandApprovalIsWorkspaceConfined(
    JsonElement parameters,
    string workspacePath
  )
  {
    if (parameters.TryGetProperty("networkApprovalContext", out var network)
      && network.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
    {
      return false;
    }
    if (parameters.TryGetProperty("proposedNetworkPolicyAmendments", out var amendments)
      && amendments.ValueKind == JsonValueKind.Array
      && amendments.GetArrayLength() > 0)
    {
      return false;
    }
    return PathsAreWorkspaceConfined(
      CommandApprovalPaths(parameters, workspacePath),
      workspacePath
    );
  }

  private static bool PathsAreWorkspaceConfined(
    IEnumerable<string> paths,
    string workspacePath
  )
  {
    try
    {
      var workspaceRoot = Path.GetFullPath(workspacePath);
      var rootPrefix = Path.TrimEndingDirectorySeparator(workspaceRoot)
        + Path.DirectorySeparatorChar;
      foreach (var path in paths)
      {
        var full = Path.GetFullPath(
          Path.IsPathRooted(path)
            ? path
            : Path.Combine(workspaceRoot, path)
        );
        if (!string.Equals(full, workspaceRoot, FileSystemPathSemantics.Comparison)
          && !full.StartsWith(rootPrefix, FileSystemPathSemantics.Comparison))
        {
          return false;
        }
        var relative = Path.GetRelativePath(workspaceRoot, full).Replace('\\', '/');
        if (relative == ".git"
          || relative.StartsWith(".git/", FileSystemPathSemantics.Comparison))
        {
          return false;
        }
      }
      return true;
    }
    catch (Exception exception) when (
      exception is ArgumentException or NotSupportedException or PathTooLongException
    )
    {
      return false;
    }
  }

  private static string SummarizeFileChange(JsonElement? item)
  {
    if (item is null || !item.Value.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
    {
      return "Codex file change";
    }
    var labels = changes.EnumerateArray().Take(5).Select(change => $"{GetString(change, "kind") ?? "change"}: {GetString(change, "path") ?? "unknown"}").ToArray();
    return Truncate(labels.Length == 0 ? "Codex file change" : string.Join(", ", labels));
  }

  private static string? ReadCodexErrorCode(JsonElement parameters)
  {
    return ReadCodexFailure(parameters).Code;
  }

  private static CodexTurnFailure ReadCodexFailure(JsonElement parameters)
  {
    var technicalMessage = GetString(parameters, "turn", "error", "message")
      ?? GetString(parameters, "error", "message")
      ?? GetString(parameters, "message");
    var nativeCode = GetString(parameters, "turn", "error", "codexErrorInfo", "type")
      ?? GetString(parameters, "error", "codexErrorInfo", "type")
      ?? GetString(parameters, "codexErrorInfo", "type");
    if (
      technicalMessage?.Contains(
        "idle timeout waiting for SSE",
        StringComparison.OrdinalIgnoreCase
      ) == true
    )
    {
      return new CodexTurnFailure(
        "codex-provider-stream-idle-timeout",
        "The local model stream became idle before Codex could finish the turn.",
        technicalMessage,
        true
      );
    }
    if (
      technicalMessage?.Contains(
        "stream disconnected before completion",
        StringComparison.OrdinalIgnoreCase
      ) == true
    )
    {
      return new CodexTurnFailure(
        "codex-provider-stream-disconnected",
        "The local model stream disconnected before Codex could finish the turn.",
        technicalMessage,
        false
      );
    }
    return new CodexTurnFailure(
      string.IsNullOrWhiteSpace(nativeCode) || nativeCode == "other"
        ? "codex-turn-failed"
        : nativeCode,
      "Codex turn failed.",
      technicalMessage,
      false
    );
  }

  private static HarnessEvent CreateEvent(
    ActiveHarnessTurn active,
    HarnessEvent harnessEvent,
    JsonElement? nativePayload = null
  )
  {
    return harnessEvent with
    {
      HarnessId = HarnessIds.Codex,
      SessionId = active.SessionId,
      TurnId = active.TurnId,
      Timestamp = DateTimeOffset.UtcNow,
      NativePayload = nativePayload?.Clone()
    };
  }

  private static string Truncate(string? value)
  {
    if (string.IsNullOrEmpty(value) || value.Length <= MaximumActivityText)
    {
      return value ?? string.Empty;
    }
    return value[..MaximumActivityText] + "\n[truncated]";
  }

  private static int? SafeExitCode(Process process)
  {
    try
    {
      return process.ExitCode;
    }
    catch (InvalidOperationException)
    {
      return null;
    }
  }

  private sealed class HarnessSessionState(
    string nativeSessionId,
    string? capabilitySignature,
    CodexContextConfiguration contextConfiguration
  )
  {
    public string NativeSessionId { get; } = nativeSessionId;

    public string? CapabilitySignature { get; } = capabilitySignature;

    public CodexContextConfiguration ContextConfiguration { get; } = contextConfiguration;

    public long? SynchronizedThroughVersion { get; set; }
  }

  private sealed record CodexContextConfiguration(
    int ContextWindowTokens,
    int AutoCompactTokenLimit
  );

  private sealed record CodexLocalModelMetadata(
    string Slug,
    int ContextWindowTokens,
    bool SupportsImages
  );

  private sealed record CodexTurnFailure(
    string Code,
    string Message,
    string? TechnicalMessage,
    bool TimedOut
  );

  private sealed class ActiveHarnessTurn
  {
    public ActiveHarnessTurn(
      string sessionId,
      string threadId,
      string workingDirectory,
      IEnumerable<string> hostBridgeTools
    )
    {
      SessionId = sessionId;
      ThreadId = threadId;
      WorkingDirectory = workingDirectory;
      HostBridgeTools = hostBridgeTools.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string SessionId { get; }

    public string ThreadId { get; }

    public string WorkingDirectory { get; }

    public IReadOnlySet<string> HostBridgeTools { get; }

    public string? TurnId { get; set; }

    public Channel<HarnessEvent> Events { get; } = Channel.CreateUnbounded<HarnessEvent>(new UnboundedChannelOptions
    {
      SingleReader = true,
      SingleWriter = false
    });

    public ConcurrentDictionary<string, JsonElement> Items { get; } = new(StringComparer.Ordinal);

    public int UnsupportedApprovalCount { get; set; }

    private int _terminalWritten;

    public bool TryComplete(HarnessEvent terminalEvent)
    {
      if (!terminalEvent.IsTerminal)
      {
        throw new InvalidOperationException(
          $"Harness event '{terminalEvent.Type}' is not terminal."
        );
      }
      if (Interlocked.Exchange(ref _terminalWritten, 1) != 0)
      {
        return false;
      }

      Events.Writer.TryWrite(terminalEvent);
      Events.Writer.TryComplete();
      return true;
    }
  }

  private sealed record PendingServerApproval(long ServerRequestId, string ThreadId);

  private sealed record PendingDynamicToolCall(long ServerRequestId, string ThreadId);
}
