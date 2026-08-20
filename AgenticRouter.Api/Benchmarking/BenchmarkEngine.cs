using System.Runtime.CompilerServices;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;

namespace AgenticRouter.Api.Benchmarking;

public interface IBenchmarkEngine
{
  Task<BenchmarkRunResult> RunAsync(
    BenchmarkRunRequest request,
    CancellationToken cancellationToken
  );
}

public sealed class BenchmarkEngine : IBenchmarkEngine
{
  private readonly IBenchmarkTestRegistry _tests;
  private readonly IBenchmarkWorkspaceFactory _workspaces;
  private readonly IHarnessRegistry _harnesses;
  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;

  public BenchmarkEngine(
    IBenchmarkTestRegistry tests,
    IBenchmarkWorkspaceFactory workspaces,
    IHarnessRegistry harnesses,
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient
  )
  {
    _tests = tests;
    _workspaces = workspaces;
    _harnesses = harnesses;
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
  }

  public async Task<BenchmarkRunResult> RunAsync(
    BenchmarkRunRequest request,
    CancellationToken cancellationToken
  )
  {
    var test = ResolveTest(request);
    var harness = ResolveHarness(request, test.Metadata);
    if (!request.ModelExecutionPermissionGranted)
    {
      throw new BenchmarkRequestException(
        "benchmark-model-permission-required",
        "Explicit permission is required immediately before benchmark model execution.",
        "modelExecutionPermissionGranted"
      );
    }

    var settings = await _settingsStore.GetAsync(cancellationToken);
    var providerEndpoint = new Uri(settings.OllamaUrl, UriKind.Absolute);
    var selectedModel = await ResolveModelAsync(
      request.Model,
      providerEndpoint,
      cancellationToken
    );
    var availability = await harness.GetAvailabilityAsync(cancellationToken);
    if (!availability.Available)
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-unavailable",
        availability.Message ?? $"Harness '{harness.Definition.Id}' is unavailable.",
        "harness"
      );
    }

    var runId = Guid.NewGuid().ToString("N");
    var startedAt = DateTimeOffset.UtcNow;
    var workspace = await _workspaces.CreateAsync(runId, cancellationToken);
    BenchmarkRunResult? result = null;
    var cleanedUp = false;

    try
    {
      await test.PrepareFixtureAsync(workspace.WorkspacePath, cancellationToken);
      var initialSnapshot = await _workspaces.CaptureAsync(
        workspace.WorkspacePath,
        cancellationToken
      );
      BenchmarkHarnessOutcome outcome;
      try
      {
        outcome = await ExecuteHarnessAsync(
          harness,
          test,
          selectedModel,
          availability.Version,
          providerEndpoint,
          workspace,
          initialSnapshot,
          settings,
          cancellationToken
        );
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        outcome = new BenchmarkHarnessOutcome(
          BenchmarkExecutionStatusIds.Cancelled,
          new BenchmarkError(
            "benchmark-cancelled",
            "The benchmark run was cancelled.",
            "harness-execution",
            true
          )
        );
      }
      catch (HarnessException exception)
      {
        outcome = new BenchmarkHarnessOutcome(
          BenchmarkExecutionStatusIds.Failed,
          new BenchmarkError(
            exception.Code,
            exception.Message,
            "harness-execution",
            exception.Recoverable
          )
        );
      }
      catch (Exception exception) when (
        exception is IOException
          or InvalidOperationException
          or UnauthorizedAccessException
      )
      {
        outcome = new BenchmarkHarnessOutcome(
          BenchmarkExecutionStatusIds.Failed,
          new BenchmarkError(
            "benchmark-harness-execution",
            exception.Message,
            "harness-execution",
            true
          )
        );
      }

      BenchmarkWorkspaceSnapshot finalSnapshot;
      try
      {
        finalSnapshot = await _workspaces.CaptureAsync(
          workspace.WorkspacePath,
          cancellationToken.IsCancellationRequested
            ? CancellationToken.None
            : cancellationToken
        );
      }
      catch (Exception exception) when (
        exception is IOException
          or UnauthorizedAccessException
          or DirectoryNotFoundException
      )
      {
        finalSnapshot = new BenchmarkWorkspaceSnapshot(
          new Dictionary<string, BenchmarkWorkspaceEntry>(
            BenchmarkWorkspaceFactory.PathComparer
          )
        );
        outcome = new BenchmarkHarnessOutcome(
          BenchmarkExecutionStatusIds.Failed,
          new BenchmarkError(
            "benchmark-snapshot-failed",
            exception.Message,
            "workspace-snapshot",
            true
          )
        );
      }

      var rawResult = await test.ValidateAsync(
        new BenchmarkValidationContext(
          workspace.WorkspacePath,
          initialSnapshot,
          finalSnapshot,
          outcome.ExecutionStatus,
          outcome.Error
        ),
        CancellationToken.None
      );
      var endedAt = DateTimeOffset.UtcNow;
      result = new BenchmarkRunResult(
        new BenchmarkRun(
          runId,
          test.Metadata.Id,
          test.Metadata.Version,
          selectedModel.Name,
          selectedModel.Digest,
          selectedModel.Provider,
          harness.Definition.Id,
          availability.Version,
          workspace.Id,
          workspace.WorkspacePath,
          startedAt,
          endedAt,
          outcome.ExecutionStatus
        ),
        rawResult,
        false
      );
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      result = CreatePreparationFailure(
        runId,
        test,
        selectedModel,
        harness,
        availability.Version,
        workspace,
        startedAt,
        BenchmarkExecutionStatusIds.Cancelled,
        new BenchmarkError(
          "benchmark-cancelled",
          "The benchmark run was cancelled before final validation.",
          "benchmark-preparation",
          true
        )
      );
    }
    catch (Exception exception) when (
      exception is IOException
        or UnauthorizedAccessException
        or InvalidOperationException
    )
    {
      result = CreatePreparationFailure(
        runId,
        test,
        selectedModel,
        harness,
        availability.Version,
        workspace,
        startedAt,
        BenchmarkExecutionStatusIds.Failed,
        new BenchmarkError(
          "benchmark-preparation-failed",
          exception.Message,
          "benchmark-preparation",
          true
        )
      );
    }
    finally
    {
      try
      {
        cleanedUp = await _workspaces.CleanupAsync(
          workspace,
          CancellationToken.None
        );
      }
      catch (Exception) when (
        result is not null
      )
      {
        cleanedUp = false;
      }
    }

    if (result is null)
    {
      throw new InvalidOperationException(
        "The benchmark ended before a structured result could be captured."
      );
    }
    if (!cleanedUp)
    {
      return result with
      {
        Run = result.Run with
        {
          ExecutionStatus = BenchmarkExecutionStatusIds.Failed,
          EndedAt = DateTimeOffset.UtcNow
        },
        RawResult = result.RawResult with
        {
          Status = BenchmarkResultStatusIds.Error,
          ExecutionStatus = BenchmarkExecutionStatusIds.Failed,
          Error = new BenchmarkError(
            "benchmark-cleanup-failed",
            "The benchmark result was captured, but the disposable workspace could not be removed.",
            "workspace-cleanup",
            true
          )
        },
        WorkspaceCleanedUp = false
      };
    }

    return result with
    {
      WorkspaceCleanedUp = true
    };
  }

  private static BenchmarkRunResult CreatePreparationFailure(
    string runId,
    IBenchmarkTestDefinition test,
    InstalledModel selectedModel,
    IAgentHarness harness,
    string? harnessVersion,
    BenchmarkWorkspace workspace,
    DateTimeOffset startedAt,
    string executionStatus,
    BenchmarkError error
  )
  {
    return new BenchmarkRunResult(
      new BenchmarkRun(
        runId,
        test.Metadata.Id,
        test.Metadata.Version,
        selectedModel.Name,
        selectedModel.Digest,
        selectedModel.Provider,
        harness.Definition.Id,
        harnessVersion,
        workspace.Id,
        workspace.WorkspacePath,
        startedAt,
        DateTimeOffset.UtcNow,
        executionStatus
      ),
      new BenchmarkRawResult(
        BenchmarkResultStatusIds.Error,
        false,
        0,
        0,
        0,
        0,
        [],
        [],
        [],
        executionStatus,
        error
      ),
      false
    );
  }

  private IBenchmarkTestDefinition ResolveTest(
    BenchmarkRunRequest request
  )
  {
    if (
      string.IsNullOrWhiteSpace(request.TestId)
      || !_tests.TryGet(request.TestId, request.TestVersion, out var test)
    )
    {
      throw new BenchmarkRequestException(
        "benchmark-test-unknown",
        $"Benchmark test '{request.TestId}' version {request.TestVersion} is unavailable.",
        "testId"
      );
    }
    if (!test.Metadata.Deterministic)
    {
      throw new BenchmarkRequestException(
        "benchmark-test-nondeterministic",
        "Milestone 0 accepts deterministic benchmark tests only.",
        "testId"
      );
    }
    return test;
  }

  private IAgentHarness ResolveHarness(
    BenchmarkRunRequest request,
    BenchmarkTestMetadata test
  )
  {
    if (string.IsNullOrWhiteSpace(request.Harness))
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-required",
        "A harness must be selected.",
        "harness"
      );
    }
    if (!_harnesses.TryGetAdapter(request.Harness, out var harness))
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-unknown",
        $"Harness '{request.Harness}' is not registered.",
        "harness"
      );
    }
    if (!string.Equals(harness.Definition.Id, HarnessIds.Codex, StringComparison.Ordinal))
    {
      throw new BenchmarkRequestException(
        "benchmark-harness-out-of-scope",
        "Milestone 0 wires only the existing Codex harness for benchmark execution.",
        "harness"
      );
    }
    foreach (var capability in test.RequiredHarnessCapabilities)
    {
      if (
        string.Equals(capability, BenchmarkHarnessCapabilityIds.FileCreation, StringComparison.Ordinal)
        && !harness.Definition.Capabilities.SupportsStructuredEdits
      )
      {
        throw new BenchmarkRequestException(
          "benchmark-harness-capability",
          $"Harness '{harness.Definition.Id}' does not support required capability '{capability}'.",
          "harness"
        );
      }
    }
    return harness;
  }

  private async Task<InstalledModel> ResolveModelAsync(
    string model,
    Uri providerEndpoint,
    CancellationToken cancellationToken
  )
  {
    if (string.IsNullOrWhiteSpace(model))
    {
      throw new BenchmarkRequestException(
        "benchmark-model-required",
        "A model must be selected.",
        "model"
      );
    }
    var installed = await _ollamaClient.GetModelsAsync(
      providerEndpoint,
      cancellationToken
    );
    var selected = installed.FirstOrDefault(candidate =>
      string.Equals(candidate.Name, model.Trim(), StringComparison.OrdinalIgnoreCase)
      && string.Equals(candidate.Provider, ModelProviderIds.OllamaLocal, StringComparison.Ordinal)
    );
    if (selected is null)
    {
      throw new BenchmarkRequestException(
        "benchmark-model-unavailable",
        $"Ollama Local model '{model}' is unavailable in the configured provider registry.",
        "model"
      );
    }
    return selected;
  }

  private async Task<BenchmarkHarnessOutcome> ExecuteHarnessAsync(
    IAgentHarness harness,
    IBenchmarkTestDefinition test,
    InstalledModel model,
    string? harnessVersion,
    Uri providerEndpoint,
    BenchmarkWorkspace workspace,
    BenchmarkWorkspaceSnapshot initialSnapshot,
    ApplicationSettings settings,
    CancellationToken cancellationToken
  )
  {
    _ = harnessVersion;
    var execution = new AgentHarnessExecution<BenchmarkHarnessOutcome>(
      NativeBenchmarkUnsupportedAsync,
      (transport, transportCancellationToken) => ExecuteExternalHarnessAsync(
        transport,
        test,
        model,
        providerEndpoint,
        workspace,
        initialSnapshot,
        settings,
        transportCancellationToken
      )
    );
    BenchmarkHarnessOutcome? outcome = null;
    await foreach (var current in harness.ExecuteAsync(execution, cancellationToken))
    {
      if (outcome is not null)
      {
        throw new InvalidOperationException(
          "The benchmark harness returned more than one terminal outcome."
        );
      }
      outcome = current;
    }
    return outcome ?? new BenchmarkHarnessOutcome(
      BenchmarkExecutionStatusIds.Failed,
      new BenchmarkError(
        "benchmark-terminal-missing",
        "The harness stream ended without a terminal outcome.",
        "harness-execution",
        true
      )
    );
  }

  private static async IAsyncEnumerable<BenchmarkHarnessOutcome> NativeBenchmarkUnsupportedAsync(
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    cancellationToken.ThrowIfCancellationRequested();
    yield return new BenchmarkHarnessOutcome(
      BenchmarkExecutionStatusIds.Unavailable,
      new BenchmarkError(
        "benchmark-native-not-wired",
        "The Native harness is registered but is outside the Milestone 0 benchmark spike.",
        "harness-selection",
        false
      )
    );
    await Task.CompletedTask;
  }

  private static async IAsyncEnumerable<BenchmarkHarnessOutcome> ExecuteExternalHarnessAsync(
    IAgentHarnessTransport harness,
    IBenchmarkTestDefinition test,
    InstalledModel model,
    Uri providerEndpoint,
    BenchmarkWorkspace workspace,
    BenchmarkWorkspaceSnapshot initialSnapshot,
    ApplicationSettings settings,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    HarnessEvent? terminal = null;
    int? contextTokens = settings.OllamaRuntime.RoleDefaults.TryGetValue(
      OllamaRuntimeRoleIds.Benchmark,
      out var benchmarkRuntime
    )
      ? benchmarkRuntime.TargetContextTokens
      : null;
    try
    {
      await foreach (var harnessEvent in harness.StartTurnAsync(
        new HarnessTurnRequest(
          harness.Definition.Id,
          workspace.Id,
          model.Name,
          ModelProviderIds.OllamaLocal,
          workspace.WorkspacePath,
          test.CreateTask(),
          "auto",
          providerEndpoint,
          ContextWindowTokens: contextTokens
        ),
        cancellationToken
      ))
      {
        if (!string.Equals(
          harnessEvent.HarnessId,
          harness.Definition.Id,
          StringComparison.OrdinalIgnoreCase
        ))
        {
          throw new HarnessException(
            "benchmark-harness-identity-mismatch",
            "The harness returned an event with an invalid identity.",
            $"Expected '{harness.Definition.Id}', received '{harnessEvent.HarnessId}'.",
            false,
            harnessId: harness.Definition.Id
          );
        }
        if (
          string.Equals(harnessEvent.Type, "approval.requested", StringComparison.Ordinal)
          && harnessEvent.ApprovalId is not null
        )
        {
          await harness.ResolveApprovalAsync(
            harnessEvent.ApprovalId,
            harnessEvent.ApprovalCanBeMapped && !harnessEvent.Destructive,
            cancellationToken
          );
        }
        else if (
          string.Equals(harnessEvent.Type, "host-tool.requested", StringComparison.Ordinal)
          && harnessEvent.ToolCallId is not null
        )
        {
          await harness.ResolveToolCallAsync(
            harnessEvent.ToolCallId,
            false,
            "Benchmark Milestone 0 does not expose Host dynamic tools. Use the harness workspace-write mechanism for this single-file task.",
            cancellationToken
          );
        }
        if (harnessEvent.IsTerminal)
        {
          terminal = harnessEvent;
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      await harness.CancelTurnAsync(workspace.Id, CancellationToken.None);
      throw;
    }

    if (terminal is null)
    {
      yield return new BenchmarkHarnessOutcome(
        BenchmarkExecutionStatusIds.Failed,
        new BenchmarkError(
          "benchmark-terminal-missing",
          "The harness stream ended without a terminal event.",
          "harness-execution",
          true
        )
      );
      yield break;
    }
    yield return BenchmarkHarnessOutcome.FromTerminal(terminal);
  }
}
