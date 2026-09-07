using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkProductionExecuteRunner
{
  Task<BenchmarkHarnessEvidence> ExecuteAsync(
    string prompt,
    string model,
    string harness,
    ApplicationSettings sourceSettings,
    BenchmarkWorkspace workspace,
    BenchmarkProgressContext? progress,
    CancellationToken cancellationToken
  );
}

public sealed class BenchmarkProductionExecuteRunner : IBenchmarkProductionExecuteRunner
{
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true
  };
  private readonly ILogger<BenchmarkProductionExecuteRunner> _logger;

  public BenchmarkProductionExecuteRunner(ILogger<BenchmarkProductionExecuteRunner> logger)
  {
    _logger = logger;
  }

  public async Task<BenchmarkHarnessEvidence> ExecuteAsync(
    string prompt,
    string model,
    string harness,
    ApplicationSettings sourceSettings,
    BenchmarkWorkspace workspace,
    BenchmarkProgressContext? progress,
    CancellationToken cancellationToken
  )
  {
    var setupStartedAt = DateTimeOffset.UtcNow;
    var dataDirectory = Path.Combine(workspace.RunDirectory, "production-host-data");
    var nestedBenchmarkDirectory = Path.Combine(workspace.RunDirectory, "production-host-benchmarks");
    Directory.CreateDirectory(dataDirectory);
    Directory.CreateDirectory(nestedBenchmarkDirectory);
    var isolatedSettings = sourceSettings with
    {
      TrustedWorkspacePath = workspace.WorkspacePath,
      DefaultModel = model,
      CoordinatorModel = model,
      ActionModel = model,
      ValidationProfile = null,
      CloudProviders = new CloudProvidersSettings(),
      WebSearch = new WebSearchSettings(),
      KnowledgeProviders = new KnowledgeProvidersSettings(),
      Incidents = sourceSettings.Incidents with { Enabled = false }
    };
    await File.WriteAllTextAsync(
      Path.Combine(dataDirectory, "settings.json"),
      JsonSerializer.Serialize(isolatedSettings, JsonOptions),
      cancellationToken
    );

    var port = ReserveLoopbackPort();
    var endpoint = new Uri($"http://127.0.0.1:{port}");
    using var process = StartProductionHost(
      endpoint,
      dataDirectory,
      nestedBenchmarkDirectory
    );
    var output = new BoundedProcessOutput();
    process.OutputDataReceived += (_, args) => output.Add(args.Data);
    process.ErrorDataReceived += (_, args) => output.Add(args.Data);
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    try
    {
      using var client = new HttpClient
      {
        BaseAddress = endpoint,
        Timeout = Timeout.InfiniteTimeSpan
      };
      await WaitUntilReadyAsync(client, process, cancellationToken);
      var setupDuration = Elapsed(setupStartedAt);
      progress?.Publish(
        BenchmarkProgressTypeIds.Activity,
        BenchmarkLiveStateIds.Running,
        "Production Execute host started with an isolated trusted workspace.",
        BenchmarkActivityKindIds.HostValidation
      );
      var request = new ChatRequest(
        prompt,
        model,
        [],
        InteractionMode: "execute",
        Harness: harness,
        ApprovalPolicy: "auto",
        BrowserSessionId: $"benchmark-{workspace.Id}",
        AutoModelHarness: false,
        ExecutionStrategy: "auto",
        SupervisionResumePolicy: "manual"
      );
      using var content = new StringContent(
        JsonSerializer.Serialize(request, JsonOptions),
        Encoding.UTF8,
        "application/json"
      );
      var executionStartedAt = DateTimeOffset.UtcNow;
      using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
      {
        Content = content
      };
      using var response = await client.SendAsync(
        requestMessage,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken
      );
      response.EnsureSuccessStatusCode();
      await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
      using var reader = new StreamReader(stream);
      var collector = new ProductionEvidenceCollector(setupDuration);
      while (true)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
          break;
        }
        if (!line.StartsWith("data: ", StringComparison.Ordinal))
        {
          continue;
        }
        var streamEvent = JsonSerializer.Deserialize<ChatStreamEvent>(line[6..], JsonOptions);
        if (streamEvent is null)
        {
          continue;
        }
        collector.Observe(streamEvent);
        Publish(progress, streamEvent);
      }
      collector.SetExecutionDuration(Elapsed(executionStartedAt));
      if (!string.IsNullOrWhiteSpace(collector.ExecutionSessionId))
      {
        try
        {
          var review = await client.GetFromJsonAsync<ExecutionSessionReview>(
            $"/api/execution-sessions/{Uri.EscapeDataString(collector.ExecutionSessionId)}/review",
            JsonOptions,
            cancellationToken
          );
          collector.Observe(review);
        }
        catch (HttpRequestException exception)
        {
          _logger.LogDebug(exception, "Benchmark production execution review was unavailable.");
        }
      }
      return collector.CreateEvidence();
    }
    finally
    {
      await StopOwnedProcessAsync(process);
    }
  }

  private static Process StartProductionHost(
    Uri endpoint,
    string dataDirectory,
    string benchmarkDirectory
  )
  {
    var applicationAssembly = typeof(BenchmarkProductionExecuteRunner).Assembly;
    var applicationName = applicationAssembly.GetName().Name;
    var currentExecutable = Environment.ProcessPath;
    var runningApplicationExecutable = !string.IsNullOrWhiteSpace(currentExecutable)
      && string.Equals(
        Path.GetFileNameWithoutExtension(currentExecutable),
        applicationName,
        OperatingSystem.IsWindows()
          ? StringComparison.OrdinalIgnoreCase
          : StringComparison.Ordinal
      );
    var start = new ProcessStartInfo
    {
      FileName = runningApplicationExecutable
        ? currentExecutable!
        : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
      WorkingDirectory = AppContext.BaseDirectory,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true
    };
    if (!runningApplicationExecutable)
    {
      if (string.IsNullOrWhiteSpace(applicationAssembly.Location))
      {
        throw new InvalidOperationException("The production application entry point is unavailable.");
      }
      start.ArgumentList.Add(applicationAssembly.Location);
    }
    start.ArgumentList.Add("--urls");
    start.ArgumentList.Add(endpoint.ToString().TrimEnd('/'));
    start.Environment["AgenticRouter__DataDirectory"] = dataDirectory;
    start.Environment["AgenticRouter__Benchmarking__RootDirectory"] = benchmarkDirectory;
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    return Process.Start(start)
      ?? throw new InvalidOperationException("The isolated production Execute host could not be started.");
  }

  private static async Task WaitUntilReadyAsync(
    HttpClient client,
    Process process,
    CancellationToken cancellationToken
  )
  {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
    while (DateTimeOffset.UtcNow < deadline)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (process.HasExited)
      {
        throw new InvalidOperationException(
          $"The isolated production Execute host exited with code {process.ExitCode}."
        );
      }
      try
      {
        using var response = await client.GetAsync("/api/settings", cancellationToken);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest)
        {
          return;
        }
      }
      catch (HttpRequestException)
      {
      }
      await Task.Delay(100, cancellationToken);
    }
    throw new TimeoutException("The isolated production Execute host did not become ready.");
  }

  private static void Publish(BenchmarkProgressContext? progress, ChatStreamEvent streamEvent)
  {
    if (progress is null)
    {
      return;
    }
    var message = streamEvent.LocalAction?.Summary
      ?? streamEvent.Message
      ?? streamEvent.SupervisionProgress?.Phase;
    if (string.IsNullOrWhiteSpace(message))
    {
      return;
    }
    progress.Publish(
      BenchmarkProgressTypeIds.Activity,
      BenchmarkLiveStateIds.Running,
      message,
      streamEvent.LocalAction is null
        ? BenchmarkActivityKindIds.Turn
        : ActivityKind(streamEvent.LocalAction.Tool)
    );
  }

  private static string ActivityKind(string tool) => tool switch
  {
    "read_file" or "list_files" or "search_text" => BenchmarkActivityKindIds.FileRead,
    "create_file" or "create_files" => BenchmarkActivityKindIds.FileCreate,
    "write_file" or "replace_text" => BenchmarkActivityKindIds.FileEdit,
    "delete_paths" => BenchmarkActivityKindIds.FileDelete,
    "run_process" => BenchmarkActivityKindIds.Process,
    _ => BenchmarkActivityKindIds.Tool
  };

  private static int ReserveLoopbackPort()
  {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
  }

  private static async Task StopOwnedProcessAsync(Process process)
  {
    if (!process.HasExited)
    {
      try
      {
        process.Kill(entireProcessTree: true);
      }
      catch (InvalidOperationException)
      {
      }
    }
    try
    {
      await process.WaitForExitAsync();
    }
    catch (InvalidOperationException)
    {
    }
  }

  private static long Elapsed(DateTimeOffset startedAt) =>
    Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

  private sealed class BoundedProcessOutput
  {
    private readonly Queue<string> _lines = new();

    public void Add(string? line)
    {
      if (string.IsNullOrWhiteSpace(line))
      {
        return;
      }
      lock (_lines)
      {
        _lines.Enqueue(line);
        while (_lines.Count > 20)
        {
          _lines.Dequeue();
        }
      }
    }
  }

  private sealed class ProductionEvidenceCollector
  {
    private readonly long _setupDuration;
    private readonly Dictionary<string, LocalActionEvent> _actions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toolSignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _actionFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextUsageView> _usage = new(StringComparer.Ordinal);
    private readonly HashSet<string> _turns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recoveryEvents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _supervisorTurns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _workerTurns = new(StringComparer.Ordinal);
    private readonly List<BenchmarkToolCallEvidence> _toolTrace = [];
    private readonly HashSet<string> _toolStates = new(StringComparer.Ordinal);
    private readonly List<string> _validationCodes = [];
    private readonly StringBuilder _report = new();
    private int _repeatedToolCalls;
    private int _repeatedActions;
    private int _recoveries;
    private long _executionDuration;
    private bool _completed;
    private bool _cancelled;
    private ProviderError? _error;
    private ExecutionSessionReview? _review;
    private string _resolvedStrategy = "direct";
    private string _terminalReason = "stream-ended-without-terminal";

    public ProductionEvidenceCollector(long setupDuration)
    {
      _setupDuration = setupDuration;
    }

    public string? ExecutionSessionId { get; private set; }

    public void SetExecutionDuration(long value) => _executionDuration = value;

    public void Observe(ChatStreamEvent streamEvent)
    {
      ExecutionSessionId = streamEvent.ExecutionSession?.Id
        ?? streamEvent.LocalAction?.ExecutionSessionId
        ?? ExecutionSessionId;
      if (streamEvent.Type == "response.delta" && streamEvent.Delta is not null)
      {
        _report.Append(streamEvent.Delta);
      }
      if (streamEvent.Type == "response.completed")
      {
        _completed = true;
        _terminalReason = streamEvent.ExecutionSession?.CompletionStatus ?? "response.completed";
      }
      else if (streamEvent.Type == "request.cancelled")
      {
        _cancelled = true;
        _terminalReason = "request.cancelled";
      }
      else if (streamEvent.Type == "error")
      {
        _error = streamEvent.Error;
        _terminalReason = streamEvent.Error?.Code ?? streamEvent.Error?.Stage ?? "error";
      }
      if (streamEvent.SupervisionProgress is not null)
      {
        var supervision = streamEvent.SupervisionProgress;
        _resolvedStrategy = supervision.ExecutionStrategy;
        if (!string.IsNullOrWhiteSpace(supervision.ContextId))
        {
          _turns.Add(supervision.ContextId);
          if (string.Equals(supervision.Role, "supervisor", StringComparison.OrdinalIgnoreCase))
          {
            _supervisorTurns.Add(supervision.ContextId);
          }
          else if (!string.IsNullOrWhiteSpace(supervision.Role))
          {
            _workerTurns.Add(supervision.ContextId);
          }
        }
      }
      if (IsRecoveryAttempt(streamEvent.Type))
      {
        var recoveryIdentity = $"{streamEvent.Type}:{streamEvent.SupervisionProgress?.EventSequence}:{streamEvent.LocalAction?.ActionId}:{streamEvent.Message}";
        if (_recoveryEvents.Add(recoveryIdentity))
        {
          _recoveries++;
        }
      }
      ObserveUsage(streamEvent);
      if (streamEvent.LocalAction is not null)
      {
        ObserveAction(streamEvent.LocalAction);
      }
    }

    public void Observe(ExecutionSessionReview? review)
    {
      if (review is null)
      {
        return;
      }
      _review = review;
      _terminalReason = review.Summary.CompletionStatus;
    }

    public BenchmarkHarnessEvidence CreateEvidence()
    {
      var executionStatus = _completed
        ? BenchmarkExecutionStatusIds.Completed
        : _cancelled
          ? BenchmarkExecutionStatusIds.Cancelled
          : BenchmarkExecutionStatusIds.Failed;
      var latestActions = _actions.Values.ToArray();
      var failed = latestActions.Count(action => action.State is "failed" or "rejected");
      var validationErrors = latestActions
        .Where(action => action.State is "failed" or "rejected")
        .Where(action => IsValidationCode(action.Code))
        .ToArray();
      var filesWritten = (_review?.Files ?? [])
        .Where(file => file.Operation is "created" or "modified")
        .Select(file => file.RelativePath)
        .Distinct(BenchmarkWorkspaceFactory.PathComparer)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
      var readActions = latestActions.Where(action => action.Tool == "read_file"
        && action.State == "completed").ToArray();
      var filesRead = readActions
        .SelectMany(action => action.RelativePaths ?? [])
        .Distinct(BenchmarkWorkspaceFactory.PathComparer)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
      var unavailableMetrics = readActions.Any(action => action.RelativePaths is null)
        ? new[] { "files_read" }
        : Array.Empty<string>();
      var input = _usage.Values.Sum(item => item.InputTokens);
      var output = _usage.Values.Sum(item => item.OutputTokens);
      var tokenProvenance = _usage.Count == 0
        ? BenchmarkEvidenceStatusIds.Unavailable
        : _usage.Values.All(item => item.Accuracy == "exact")
          ? BenchmarkEvidenceStatusIds.Measured
          : "estimated";
      var directTurns = _resolvedStrategy == "direct"
        ? Math.Max(_turns.Count, _usage.Count)
        : 0;
      var diagnostics = new BenchmarkOperationalDiagnostics(
        "real-life-operational-v1",
        latestActions.Length,
        failed,
        validationErrors.Length,
        _repeatedToolCalls,
        _repeatedActions,
        _recoveries,
        filesRead,
        filesWritten,
        [],
        [],
        [],
        Math.Max(_turns.Count, _usage.Count),
        directTurns,
        _supervisorTurns.Count,
        _workerTurns.Count,
        _terminalReason,
        _executionDuration,
        _setupDuration,
        0,
        _usage.Count == 0 ? null : input,
        _usage.Count == 0 ? null : output,
        tokenProvenance,
        "auto",
        _resolvedStrategy,
        _validationCodes.Distinct(StringComparer.Ordinal).ToArray(),
        unavailableMetrics
      );
      var error = executionStatus == BenchmarkExecutionStatusIds.Completed
        ? null
        : new BenchmarkError(
          _error?.Code ?? _terminalReason,
          _error?.Message ?? "Production Execute ended without a successful terminal result.",
          _error?.Stage ?? "production-execute",
          _error?.Recoverable ?? true
        );
      return new BenchmarkHarnessEvidence(
        executionStatus,
        error,
        _report.ToString(),
        latestActions.Length,
        failed,
        Math.Min(failed, _recoveries),
        diagnostics.InputTokens,
        diagnostics.OutputTokens,
        [
          new BenchmarkTurnEvidence(
            1,
            "Production Execute",
            MissingGameBenchmark.Prompt,
            executionStatus,
            _report.ToString(),
            latestActions.Length,
            failed,
            Math.Min(failed, _recoveries),
            _executionDuration
          )
        ],
        [
          new BenchmarkHostEvent(
            1,
            "production-execute",
            "The scenario ran through the production Execute endpoint.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
              ["requestedStrategy"] = "auto",
              ["resolvedStrategy"] = _resolvedStrategy,
              ["terminalReason"] = _terminalReason
            }
          )
        ],
        _toolTrace,
        diagnostics
      );
    }

    private void ObserveAction(LocalActionEvent action)
    {
      var firstObservation = !_actions.ContainsKey(action.ActionId);
      _actions[action.ActionId] = action;
      if (!string.IsNullOrWhiteSpace(action.Code)
        && IsValidationCode(action.Code)
        && !_validationCodes.Contains(action.Code, StringComparer.Ordinal))
      {
        _validationCodes.Add(action.Code);
      }
      if (_toolStates.Add($"{action.ActionId}:{action.State}"))
      {
        _toolTrace.Add(new BenchmarkToolCallEvidence(
          _toolTrace.Count + 1,
          1,
          action.Tool,
          action.State,
          ErrorCode: action.Code
        ));
      }
      if (!firstObservation)
      {
        return;
      }
      if (!string.IsNullOrWhiteSpace(action.ArgumentsSha256))
      {
        var signature = $"{action.Tool}:{action.ArgumentsSha256}";
        if (!_toolSignatures.Add(signature))
        {
          _repeatedToolCalls++;
        }
      }
      if (!string.IsNullOrWhiteSpace(action.ActionFingerprint)
        && IsMutation(action.Tool)
        && !_actionFingerprints.Add(action.ActionFingerprint))
      {
        _repeatedActions++;
      }
    }

    private void ObserveUsage(ChatStreamEvent streamEvent)
    {
      if (streamEvent.ContextUsage is null)
      {
        return;
      }
      var context = streamEvent.SupervisionProgress?.ContextId ?? "direct";
      var key = $"{context}:{streamEvent.ContextUsage.InferenceSequence}";
      _usage[key] = streamEvent.ContextUsage;
      _turns.Add(key);
    }

    private static bool IsMutation(string tool) => tool is
      "create_file" or "create_files" or "write_file" or "replace_text"
      or "delete_paths" or "move_path" or "rename_path";

    private static bool IsValidationCode(string? code) =>
      code?.Contains("validation", StringComparison.OrdinalIgnoreCase) == true
      || code?.Contains("invalid", StringComparison.OrdinalIgnoreCase) == true
      || code?.Contains("malformed", StringComparison.OrdinalIgnoreCase) == true
      || code?.Contains("argument", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsRecoveryAttempt(string type) => type is
      "action.tool-protocol-repair-requested"
      or "action.recovery-planning"
      or "agent.execution-recovery-started"
      or "action.autonomous-recovery-escalated"
      or "supervision.retry-started"
      or "supervision.turn-watchdog-recovery"
      or "supervision.turn-canonical-recovery"
      or "supervision.turn-harness-recovery";
  }
}
