using System.Diagnostics;
using System.Text.Json;
using AgenticRouter.Api.Chat;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Observability;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Sessions;
using AgenticRouter.Api.Supervision;
using AgenticRouter.Api.WorkspaceProfiles;
using Microsoft.AspNetCore.Mvc;

namespace AgenticRouter.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  private readonly IChatStreamService _chatStreamService;
  private readonly IExecutionSessionStore _executionSessions;
  private readonly IImageAttachmentValidator _imageValidator;
  private readonly ILogger<ChatController> _logger;
  private readonly IIncidentJournal _incidents;
  private readonly ITraceContext _trace;
  private readonly IPersistentSessionService _persistentSessions;
  private readonly IDurableSupervisionRunCoordinator _supervisionRuns;
  private readonly ISettingsStore _settings;
  private readonly IMarkdownRenderer _markdown;
  private string? _executionSessionId;
  private string? _conversationSessionId;
  private string? _turnId;
  private string? _durableSupervisionRunId;
  private string? _lastExecutionCheckpoint;
  private readonly List<Task<IncidentAppendResult>> _incidentWrites = [];
  private readonly List<ChatStreamEvent> _presentationTimeline = [];

  public ChatController(
    IChatStreamService chatStreamService,
    IExecutionSessionStore executionSessions,
    IPersistentSessionService persistentSessions,
    IImageAttachmentValidator imageValidator,
    IDurableSupervisionRunCoordinator supervisionRuns,
    ISettingsStore settings,
    IMarkdownRenderer markdown,
    IIncidentJournal incidents,
    ITraceContext trace,
    ILogger<ChatController> logger
  )
  {
    _chatStreamService = chatStreamService;
    _executionSessions = executionSessions;
    _persistentSessions = persistentSessions;
    _imageValidator = imageValidator;
    _supervisionRuns = supervisionRuns;
    _settings = settings;
    _markdown = markdown;
    _incidents = incidents;
    _trace = trace;
    _logger = logger;
  }

  [HttpPost("stream")]
  public async Task Stream(
    [FromBody] ChatRequest request,
    CancellationToken cancellationToken
  )
  {
    Response.StatusCode = StatusCodes.Status200OK;
    Response.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers.Connection = "keep-alive";

    var requestId = Guid.NewGuid().ToString(
      "N"
    );
    _turnId = requestId;
    _trace.Link("requestId", requestId);

    if (string.IsNullOrWhiteSpace(
      request.Message
    ))
    {
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          "request-validation",
          "Enter a message before sending.",
          "The message field was empty.",
          null,
          null,
          400,
          true
        ),
        cancellationToken
      );
      return;
    }

    SupervisionRequestResolution supervision;
    var maximumDirectPlanSteps = 5;
    try
    {
      var settings = await _settings.GetAsync(cancellationToken);
      maximumDirectPlanSteps = settings.Execution.MaxDirectPlanSteps;
      supervision = SupervisionRequestPolicy.Resolve(
        request,
        maximumDirectPlanSteps
      );
    }
    catch (SupervisionException exception)
    {
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          exception.Stage,
          exception.Message,
          exception.Code,
          request.Model,
          null,
          exception.StatusCode,
          exception.Retryable,
          exception,
          new Dictionary<string, string?>
          {
            ["code"] = exception.Code
          }
        ),
        cancellationToken
      );
      return;
    }

    try
    {
      try
      {
        _ = _imageValidator.Validate(
          request.Images
        );
        _conversationSessionId = request.ConversationSessionId;
        var persisted = await _persistentSessions.BeginTurnAsync(
          request.ConversationSessionId,
          requestId,
          request.Message,
          request.InteractionMode,
          request.Model,
          request.ApprovalPolicy,
          request.Harness,
          request.Images,
          request.HideUserMessage,
          request.ReplaceFromMessageIndex,
          cancellationToken
        );
        _conversationSessionId = persisted?.Id
          ?? _conversationSessionId;
        _trace.Link("conversationId", _conversationSessionId);
        if (persisted is not null)
        {
          await WriteEventAsync(
            SessionEvent(
              requestId,
              string.IsNullOrWhiteSpace(
                request.ConversationSessionId
              )
                ? "session-created"
                : "session-persisted",
              string.IsNullOrWhiteSpace(
                request.ConversationSessionId
              )
                ? "Local conversation session created."
                : "Conversation turn persisted locally."
            ),
            cancellationToken
          );
        }
      }
      catch (WorkspaceProfileException exception)
      {
        await WriteEventAsync(
          PersistenceEvent(
            requestId,
            exception
          ),
          cancellationToken
        );
        if (request.ReplaceFromMessageIndex is not null)
        {
          throw new ChatStageException(
            exception.Stage,
            "The edited message could not replace the original conversation turn.",
            exception.Message,
            request.Model,
            null,
            409,
            exception.Retryable,
            exception,
            new Dictionary<string, string?>
            {
              ["code"] = exception.Code
            }
          );
        }
      }

      var answer = new System.Text.StringBuilder();
      var contentBlocks = new List<ChatMessageContentBlock>();
      var normalizedRequest = request with
      {
        ConversationSessionId = _conversationSessionId,
        Message = supervision.Objective,
        ExecutionStrategy = supervision.RequestedStrategy
      };
      SupervisionTakeoverSnapshot? takeover = null;
      while (true)
      {
        var stream = supervision.Supervised
          ? StreamSupervisedAsync(
            normalizedRequest,
            supervision,
            requestId,
            takeover,
            cancellationToken
          )
          : _chatStreamService.StreamAsync(
            normalizedRequest,
            requestId,
            cancellationToken
          );
        try
        {
          await foreach (var streamEvent in stream)
          {
            CaptureContentBlock(
              contentBlocks,
              streamEvent
            );
            _executionSessionId = streamEvent.ExecutionSession?.Id
              ?? _executionSessionId;
            _trace.Link("executionSessionId", _executionSessionId);
            await PersistExecutionCheckpointAsync(
              streamEvent
            );
            if (streamEvent.Type == "response.delta")
            {
              answer.Append(
                streamEvent.Delta
              );
            }

            if (streamEvent.Type == "response.completed")
            {
              try
              {
                var review = string.IsNullOrWhiteSpace(
                  _executionSessionId
                )
                  ? null
                  : _executionSessions.GetReview(
                    _executionSessionId
                  );
                var persisted = await _persistentSessions.CompleteTurnAsync(
                  _conversationSessionId,
                  requestId,
                  answer.ToString(),
                  request.InteractionMode,
                  streamEvent.SelectedModel,
                  review,
                  cancellationToken,
                  new TraceDiagnosticReference(
                    _trace.TraceId,
                    "completed"
                  ),
                  contentBlocks,
                  _presentationTimeline
                );
                if (persisted is not null)
                {
                  await WriteEventAsync(
                    SessionEvent(
                      requestId,
                      "session-persisted",
                      "Completed conversation turn persisted locally."
                    ),
                    cancellationToken
                  );
                }
              }
              catch (WorkspaceProfileException exception)
              {
                await WriteEventAsync(
                  PersistenceEvent(
                    requestId,
                    exception
                  ),
                  cancellationToken
                );
              }
            }

            var writtenEvent = await WriteEventAsync(
              streamEvent with
              {
                ConversationSessionId = _conversationSessionId
              },
              cancellationToken
            );
            if (writtenEvent.Type == "error")
            {
              await PersistFailureMessageAsync(
                writtenEvent
              );
            }
            else if (writtenEvent.Type == "request.cancelled")
            {
              await MarkPersistentTerminalAsync(
                "cancelled"
              );
            }
            else if (writtenEvent.Type == "action.awaiting-approval")
            {
              await PersistPresentationTimelineAsync(requestId);
            }
          }
          break;
        }
        catch (SupervisionTakeoverRequiredException exception) when (
          supervision.Automatic
          && !supervision.Supervised
        )
        {
          takeover = CreateTakeoverSnapshot(
            exception,
            "accepted-plan-step-limit"
          );
          supervision = SupervisionRequestPolicy.Promote(
            supervision,
            "automatic-plan-step-limit",
            exception.Plan.Steps.Count
          );
          normalizedRequest = normalizedRequest with
          {
            ExecutionStrategy = SupervisionExecutionStrategies.Supervised
          };
          answer.Clear();
        }
        catch (Exception exception) when (
          TryCreateFailureTakeoverSnapshot(
            exception,
            supervision,
            maximumDirectPlanSteps,
            out var failureTakeover
          )
        )
        {
          takeover = failureTakeover;
          supervision = SupervisionRequestPolicy.Promote(
            supervision,
            "automatic-resource-starvation-takeover",
            takeover.DetectedPlanSteps
          );
          normalizedRequest = normalizedRequest with
          {
            ExecutionStrategy = SupervisionExecutionStrategies.Supervised
          };
          answer.Clear();
        }
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      if (_durableSupervisionRunId is not null)
      {
        _logger.LogInformation(
          "Browser detached from durable supervision run {RunId}; Host execution continues.",
          _durableSupervisionRunId
        );
        return;
      }
      _logger.LogInformation(
        "Chat request {RequestId} was cancelled.",
        requestId
      );
      await MarkPersistentTerminalAsync(
        "cancelled"
      );

      try
      {
        await WriteEventAsync(
          new ChatStreamEvent(
            requestId,
            "request.cancelled",
            DateTimeOffset.UtcNow,
            "Request cancelled.",
            null,
            request.Model,
            null,
            null,
            null,
            null,
            null,
            CurrentExecutionSummary()
          ),
          CancellationToken.None
        );
      }
      catch (Exception exception)
      {
        _logger.LogDebug(
          exception,
          "The cancelled client connection could not receive its terminal event."
        );
      }
    }
    catch (OperationCanceledException) when (
      CurrentExecutionSummary()?.State == "cancelled"
    )
    {
      await MarkPersistentTerminalAsync(
        "cancelled"
      );
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          "execution-session",
          "This Execute request was replaced by a newer request from the same browser session.",
          "The previous execution session was cancelled before another local action could run.",
          request.Model,
          null,
          409,
          true
        ),
        CancellationToken.None
      );
    }
    catch (ChatStageException exception)
    {
      await MarkPersistentTerminalAsync(
        "failed"
      );
      _logger.LogWarning(
        exception,
        "Chat request {RequestId} failed during {Stage}.",
        requestId,
        exception.Stage
      );
      await WriteErrorAsync(
        requestId,
        exception,
        cancellationToken
      );
    }
    catch (HarnessException exception)
    {
      await MarkPersistentTerminalAsync(
        "failed"
      );
      _logger.LogWarning(
        exception,
        "Chat request {RequestId} failed in harness {HarnessId} with {Code}. Diagnostic: {Diagnostic}",
        requestId,
        exception.HarnessId,
        exception.Code,
        exception.TechnicalMessage
      );
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          $"{exception.HarnessId}-harness",
          exception.Message,
          $"Harness diagnostic: {exception.Code}.",
          request.Model,
          null,
          exception.Code.EndsWith("-executable-not-found", StringComparison.Ordinal)
            || exception.Code.EndsWith("-executable-access-denied", StringComparison.Ordinal)
            || exception.Code.EndsWith("-start-failed", StringComparison.Ordinal)
            || exception.Code.EndsWith("-app-server-exited", StringComparison.Ordinal)
              ? 503
              : 400,
          exception.Recoverable,
          exception,
          new Dictionary<string, string?>
          {
            ["code"] = exception.Code,
            ["harnessId"] = exception.HarnessId
          }
        ),
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      await MarkPersistentTerminalAsync(
        "failed"
      );
      _logger.LogWarning(
        exception,
        "Chat request {RequestId} failed during Ollama {Stage}.",
        requestId,
        exception.Stage
      );
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          exception.Stage,
          exception.Message,
          exception.TechnicalMessage,
          request.Model,
          null,
          exception.HttpStatus,
          exception.Recoverable,
          exception,
          details: exception is CapabilityException capability
            ? new Dictionary<string, string?>
            {
              ["code"] = capability.Code,
              ["providerTraceId"] = capability.TraceId
            }
            : null,
          provider: exception is RoutedProviderException routed
            ? routed.Provider
            : exception is CapabilityException capabilityError
              ? capabilityError.Provider ?? "ollama-local"
            : "ollama-local"
        ),
        cancellationToken
      );
    }
    catch (OllamaRuntimeProfileException exception)
    {
      await MarkPersistentTerminalAsync("failed");
      _logger.LogWarning(exception, "Chat request {RequestId} failed runtime context validation.", requestId);
      var error = exception.Error;
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          error.Stage,
          error.Message,
          error.Diagnostic,
          error.Model,
          null,
          error.Code == "request-context-does-not-fit" ? 413 : 400,
          error.Retryable,
          exception,
          new Dictionary<string, string?>(StringComparer.Ordinal)
          {
            ["code"] = error.Code,
            ["providerTraceId"] = error.TraceId,
            ["role"] = error.Role,
            ["estimatedInputTokens"] = error.EstimatedInputTokens?.ToString(),
            ["reservedOutputTokens"] = error.ReservedOutputTokens?.ToString(),
            ["requiredContextTokens"] = error.RequiredContextTokens?.ToString(),
            ["maximumContextTokens"] = error.MaximumContextTokens?.ToString(),
            ["effectiveContextTokens"] = error.EffectiveContextTokens?.ToString()
          },
          error.Provider
        ),
        cancellationToken
      );
    }
    catch (Exception exception)
    {
      await MarkPersistentTerminalAsync(
        "failed"
      );
      _logger.LogError(
        exception,
        "Chat request {RequestId} failed unexpectedly.",
        requestId
      );
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          "application",
          "The application could not complete the request.",
          exception.Message,
          request.Model,
          null,
          500,
          false,
          exception
        ),
        cancellationToken
      );
    }
    finally
    {
      await PersistPresentationTimelineAsync(
        requestId
      );
    }
  }

  [HttpGet("supervision/{runId}/stream")]
  public async Task AttachSupervision(
    string runId,
    [FromQuery] long afterSequence = 0,
    CancellationToken cancellationToken = default
  )
  {
    Response.StatusCode = StatusCodes.Status200OK;
    Response.ContentType = "text/event-stream";
    Response.Headers.CacheControl = "no-cache";
    Response.Headers.Connection = "keep-alive";

    var requestId = Guid.NewGuid().ToString("N");
    _trace.Link("requestId", requestId);
    _trace.Link("supervisionRunId", runId);
    if (!_supervisionRuns.TryGetView(runId, out var view))
    {
      await WriteErrorAsync(
        requestId,
        new ChatStageException(
          "supervision-attach",
          "The durable supervised run is unavailable.",
          "No Host-owned run matches the requested identifier.",
          null,
          null,
          404,
          false
        ),
        cancellationToken
      );
      return;
    }

    _conversationSessionId = view.ConversationSessionId;
    _durableSupervisionRunId = runId;
    _trace.Link("conversationId", _conversationSessionId);
    var answer = new System.Text.StringBuilder();
    var contentBlocks = new List<ChatMessageContentBlock>();
    try
    {
      await WriteEventAsync(
        new ChatStreamEvent(
          requestId,
          "supervision.attached",
          DateTimeOffset.UtcNow,
          $"Attached to durable supervised run {runId} at sequence {afterSequence}.",
          null,
          view.Route.Model,
          null,
          0,
          null,
          null,
          ConversationSessionId: _conversationSessionId
        ),
        cancellationToken
      );

      await foreach (var streamEvent in StreamExistingSupervisedAsync(
        runId,
        requestId,
        view,
        Math.Max(0, afterSequence),
        cancellationToken
      ))
      {
        CaptureContentBlock(
          contentBlocks,
          streamEvent
        );
        if (streamEvent.Type == "response.delta")
        {
          answer.Append(streamEvent.Delta);
        }
        if (streamEvent.Type == "response.completed")
        {
          try
          {
            var persisted = await _persistentSessions.CompleteTurnAsync(
              _conversationSessionId,
              null,
              answer.ToString(),
              "execute",
              streamEvent.SelectedModel,
              null,
              cancellationToken,
              new TraceDiagnosticReference(
                _trace.TraceId,
                "completed"
              ),
              contentBlocks,
              _presentationTimeline
            );
            if (persisted is not null)
            {
              await WriteEventAsync(
                SessionEvent(
                  requestId,
                  "session-persisted",
                  "Reattached supervised turn persisted locally."
                ),
                cancellationToken
              );
            }
          }
          catch (WorkspaceProfileException exception)
          {
            await WriteEventAsync(
              PersistenceEvent(requestId, exception),
              cancellationToken
            );
          }
        }

        await WriteEventAsync(
          streamEvent with { ConversationSessionId = _conversationSessionId },
          cancellationToken
        );
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      _logger.LogInformation(
        "Browser detached again from durable supervision run {RunId}; Host execution continues.",
        runId
      );
    }
    finally
    {
      await PersistPresentationTimelineAsync(
        requestId
      );
    }
  }

  private async IAsyncEnumerable<ChatStreamEvent> StreamSupervisedAsync(
    ChatRequest request,
    SupervisionRequestResolution supervision,
    string requestId,
    SupervisionTakeoverSnapshot? takeover,
    [System.Runtime.CompilerServices.EnumeratorCancellation]
    CancellationToken cancellationToken
  )
  {
    SupervisionRunStartView prepared;
    DurableSupervisionRunView startedView;
    try
    {
      prepared = await _supervisionRuns.PrepareAsync(
        new PrepareSupervisionRunRequest(
          supervision.Objective,
          request.Model,
          request.Harness,
          request.BrowserSessionId ?? string.Empty,
          request.ConversationSessionId,
          request.ApprovalPolicy,
          supervision.ResumePolicy,
          ClientRunId: request.SupervisionRunId,
          AutoModelHarness: request.AutoModelHarness,
          History: request.History,
          Images: request.Images,
          Takeover: takeover,
          ExecutionStrategy: supervision.RequestedStrategy
        ),
        cancellationToken
      );
      _durableSupervisionRunId = prepared.RunId;
      _trace.Link("supervisionRunId", prepared.RunId);
      _ = await _supervisionRuns.StartAsync(
        prepared.RunId,
        cancellationToken
      );
      if (!_supervisionRuns.TryGetView(prepared.RunId, out startedView))
      {
        throw new SupervisionException(
          "supervision-run-missing",
          "supervision-start",
          "The Host-owned run disappeared immediately after it was started.",
          false,
          500
        );
      }
    }
    catch (SupervisionException exception)
    {
      throw new ChatStageException(
        exception.Stage,
        exception.Message,
        exception.Code,
        request.Model,
        null,
        exception.StatusCode,
        exception.Retryable,
        exception,
        new Dictionary<string, string?>
        {
          ["code"] = exception.Code,
          ["providerPolicy"] = "ollama-local-only"
        }
      );
    }

    var stopwatch = Stopwatch.StartNew();
    if (supervision.Autonomous)
    {
      yield return new ChatStreamEvent(
        requestId,
        "supervision.autonomous-selected",
        DateTimeOffset.UtcNow,
        "Host started fully autonomous supervision. The supervisor may approve every user-permittable action inside the trusted workspace; hard Host boundaries remain enforced.",
        null,
        startedView.Route.Model,
        null,
        stopwatch.ElapsedMilliseconds,
        null,
        null,
        SupervisionProgress: CreateSupervisionProgress(startedView)
      );
    }
    else if (supervision.Automatic)
    {
      yield return new ChatStreamEvent(
        requestId,
        "supervision.auto-selected",
        DateTimeOffset.UtcNow,
        takeover is null
          ? $"Host selected supervised execution because the objective exposes {supervision.EstimatedStepCount} structured steps, above the configured direct limit."
          : string.Equals(
            takeover.Trigger,
            "accepted-plan-step-limit",
            StringComparison.Ordinal
          )
            ? $"Host promoted the direct execution to supervised mode at a verified boundary after accepting a {takeover.DetectedPlanSteps}-step plan; the configured direct limit is {takeover.MaximumDirectPlanSteps}."
            : $"Host promoted the direct execution to supervised mode after recoverable resource failure '{takeover.Trigger}' at a verified boundary; {takeover.Files.Count} verified file effect(s) were preserved for reconciliation.",
        null,
        startedView.Route.Model,
        null,
        stopwatch.ElapsedMilliseconds,
        null,
        null
      );
    }
    yield return new ChatStreamEvent(
      requestId,
      "supervision.run-started",
      DateTimeOffset.UtcNow,
      $"Durable supervised run {prepared.RunId} started on fixed local route {startedView.Route.Model} × {startedView.Route.Harness}.",
      null,
      startedView.Route.Model,
      null,
      stopwatch.ElapsedMilliseconds,
      null,
      null,
      SupervisionProgress: CreateSupervisionProgress(startedView)
    );

    await foreach (var streamEvent in StreamExistingSupervisedAsync(
      prepared.RunId,
      requestId,
      startedView,
      0,
      cancellationToken
    ))
    {
      yield return streamEvent;
    }
  }

  private SupervisionTakeoverSnapshot CreateTakeoverSnapshot(
    SupervisionTakeoverRequiredException exception,
    string trigger
  )
  {
    var review = _executionSessions.GetReview(
      exception.ExecutionSessionId
    );
    var files = (review?.Files ?? []).Where(file => file.Verified).DistinctBy(
      file => file.RelativePath,
      StringComparer.OrdinalIgnoreCase
    ).Take(64).Select(file => new SupervisionTakeoverFileSnapshot(
      file.RelativePath,
      file.Operation,
      file.FinalHash,
      file.Verified
    )).ToArray();
    return new SupervisionTakeoverSnapshot(
      trigger,
      exception.ExecutionSessionId,
      exception.Plan.Steps.Count,
      exception.MaximumDirectPlanSteps,
      exception.AfterVerifiedMutation,
      exception.Plan,
      files,
      review?.Summary.CompletionStatus ?? "handed-off-to-supervision",
      review?.Validation?.State,
      DateTimeOffset.UtcNow
    );
  }

  private bool TryCreateFailureTakeoverSnapshot(
    Exception exception,
    SupervisionRequestResolution supervision,
    int maximumDirectPlanSteps,
    out SupervisionTakeoverSnapshot takeover
  )
  {
    takeover = null!;
    if (
      !supervision.Automatic
      || supervision.Supervised
      || !IsResourceStarvationFailure(exception)
      || string.IsNullOrWhiteSpace(_executionSessionId)
    )
    {
      return false;
    }

    var review = _executionSessions.GetReview(_executionSessionId);
    if (review is null)
    {
      return false;
    }
    var files = review.Files.Where(file => file.Verified).DistinctBy(
      file => file.RelativePath,
      StringComparer.OrdinalIgnoreCase
    ).Take(64).Select(file => new SupervisionTakeoverFileSnapshot(
      file.RelativePath,
      file.Operation,
      file.FinalHash,
      file.Verified
    )).ToArray();
    var plan = review.Summary.Plan;
    if (files.Length == 0 && plan is null)
    {
      return false;
    }

    takeover = new SupervisionTakeoverSnapshot(
      FailureTrigger(exception),
      review.Summary.Id,
      plan?.Steps.Count ?? 0,
      maximumDirectPlanSteps,
      files.Length > 0,
      plan,
      files,
      review.Summary.CompletionStatus,
      review.Validation?.State,
      DateTimeOffset.UtcNow
    );
    return true;
  }

  private static bool IsResourceStarvationFailure(Exception exception)
  {
    return exception switch
    {
      HarnessException harness => harness.Recoverable
        && (
          harness.Code.Contains("timeout", StringComparison.OrdinalIgnoreCase)
          || harness.Code.Contains("context", StringComparison.OrdinalIgnoreCase)
          || harness.Code.Contains("output-token", StringComparison.OrdinalIgnoreCase)
        ),
      OllamaProviderException provider => provider.Recoverable
        && (
          provider.IsMemoryPressure
          || provider.InnerException is TimeoutException
          || provider.Stage.Contains("timeout", StringComparison.OrdinalIgnoreCase)
          || provider.TechnicalMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        ),
      OllamaRuntimeProfileException runtime => runtime.Error.Retryable
        && runtime.Error.Code is "request-context-does-not-fit" or "context-item-too-large",
      ChatStageException stage => stage.Recoverable
        && (
          stage.Stage.Contains("timeout", StringComparison.OrdinalIgnoreCase)
          || stage.TechnicalMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase)
          || stage.TechnicalMessage.Contains("context", StringComparison.OrdinalIgnoreCase)
        ),
      _ => false
    };
  }

  private static string FailureTrigger(Exception exception)
  {
    return exception switch
    {
      HarnessException harness => harness.Code,
      OllamaProviderException provider => provider.Stage,
      OllamaRuntimeProfileException runtime => runtime.Error.Code,
      ChatStageException stage => stage.Stage,
      _ => "resource-starvation"
    };
  }

  private async IAsyncEnumerable<ChatStreamEvent> StreamExistingSupervisedAsync(
    string runId,
    string requestId,
    DurableSupervisionRunView startedView,
    long afterSequence,
    [System.Runtime.CompilerServices.EnumeratorCancellation]
    CancellationToken cancellationToken
  )
  {
    var stopwatch = Stopwatch.StartNew();
    ContextUsageView? latestContextUsage = null;
    await foreach (var progressEvent in _supervisionRuns.SubscribeAsync(
      runId,
      afterSequence,
      follow: true,
      cancellationToken
    ))
    {
      var progressView = _supervisionRuns.TryGetView(runId, out var liveView)
        ? liveView
        : startedView;
      latestContextUsage = progressEvent.ContextUsage ?? latestContextUsage;
      var isReasoning = progressEvent.Type == SupervisionEventTypeIds.TurnReasoning;
      yield return new ChatStreamEvent(
        requestId,
        isReasoning ? "reasoning.delta" : progressEvent.Type,
        progressEvent.Timestamp,
        isReasoning ? null : progressEvent.Message,
        null,
        startedView.Route.Model,
        null,
        stopwatch.ElapsedMilliseconds,
        null,
        null,
        LocalAction: CreateSupervisionLocalAction(
          progressView,
          progressEvent
        ),
        ReasoningDelta: isReasoning ? progressEvent.Message : null,
        ContentBlockId: isReasoning
          ? $"supervision:{progressEvent.ContextId ?? progressEvent.Role ?? "turn"}:reasoning"
          : null,
        SlowRequest: CreateSupervisionSlowRequest(
          progressView,
          progressEvent
        ),
        SupervisionProgress: progressEvent.Type == "context.usage"
          ? null
          : CreateSupervisionProgress(
            progressView,
            progressEvent
          ),
        ContextUsage: progressEvent.ContextUsage
      );

      if (!progressEvent.Terminal)
      {
        continue;
      }
      if (!_supervisionRuns.TryGetView(runId, out var view))
      {
        throw new ChatStageException(
          "supervision-run-missing",
          "The durable supervision result is unavailable.",
          "The Host-owned run disappeared before its terminal view was read.",
          startedView.Route.Model,
          null,
          500,
          false
        );
      }

      if (view.State == DurableSupervisionRunStates.Completed)
      {
        var finalAnswer = view.Runtime?.FinalAnswer;
        if (string.IsNullOrWhiteSpace(finalAnswer))
        {
          throw new ChatStageException(
            "supervision-final-answer-missing",
            "The supervisor completed without a final answer.",
            "The terminal Host view did not contain the accepted final answer.",
            view.Route.Model,
            null,
            500,
            false
          );
        }
        yield return new ChatStreamEvent(
          requestId,
          "response.delta",
          DateTimeOffset.UtcNow,
          null,
          finalAnswer,
          view.Route.Model,
          null,
          stopwatch.ElapsedMilliseconds,
          _markdown.Render(finalAnswer),
          null,
          ContextUsage: latestContextUsage
        );
        yield return new ChatStreamEvent(
          requestId,
          "response.completed",
          DateTimeOffset.UtcNow,
          "Durable supervised execution completed.",
          null,
          view.Route.Model,
          null,
          stopwatch.ElapsedMilliseconds,
          _markdown.Render(finalAnswer),
          null,
          ContextUsage: latestContextUsage
        );
        yield break;
      }

      if (view.State == DurableSupervisionRunStates.Cancelled)
      {
        yield return new ChatStreamEvent(
          requestId,
          "request.cancelled",
          DateTimeOffset.UtcNow,
          "Durable supervised execution was cancelled.",
          null,
          view.Route.Model,
          null,
          stopwatch.ElapsedMilliseconds,
          null,
          null,
          ContextUsage: latestContextUsage
        );
        yield break;
      }

      yield return new ChatStreamEvent(
        requestId,
        "error",
        DateTimeOffset.UtcNow,
        view.Runtime?.LastFailure ?? view.WaitReason ?? "Durable supervised execution stopped.",
        null,
        view.Route.Model,
        null,
        stopwatch.ElapsedMilliseconds,
        null,
        new ProviderError(
          "supervision",
          view.Runtime?.LastFailure ?? "Durable supervised execution stopped.",
          view.WaitReason,
          HttpContext.TraceIdentifier,
          view.Route.Provider,
          view.Route.Model,
          null,
          409,
          false,
          new Dictionary<string, string?>
          {
            ["code"] = view.WaitCode ?? "supervision-blocked",
            ["runId"] = view.RunId,
            ["workItemId"] = view.Runtime?.ActiveWorkItemId
          },
          view.WaitCode ?? "supervision-blocked"
        ),
        ContextUsage: latestContextUsage
      );
      yield break;
    }
  }

  private static SupervisionProgressView CreateSupervisionProgress(
    DurableSupervisionRunView view,
    SupervisionRunEvent? progressEvent = null
  )
  {
    return new SupervisionProgressView(
      view.RunId,
      view.Objective,
      view.ExecutionStrategy,
      view.State,
      view.Phase,
      progressEvent?.Role ?? view.Runtime?.ActiveRole,
      progressEvent?.WorkItemId ?? view.Runtime?.ActiveWorkItemId,
      progressEvent?.CompletedItems ?? view.Runtime?.CompletedItems ?? 0,
      progressEvent?.TotalItems ?? view.Runtime?.TotalItems ?? 0,
      progressEvent?.Sequence ?? view.LastSequence,
      view.Recovery?.TurnInFlight == true,
      (view.Runtime?.WorkItems ?? []).Select(item =>
        new SupervisionWorkItemProgressView(
          item.Id,
          item.Objective,
          item.Status,
          item.AttemptCount
        )
      ).ToArray(),
      view.Route.Model,
      view.Route.Harness,
      view.ApprovalPolicy,
      progressEvent?.ContextId
    );
  }

  private static LocalActionEvent? CreateSupervisionLocalAction(
    DurableSupervisionRunView view,
    SupervisionRunEvent progressEvent
  )
  {
    if (progressEvent.LocalAction is not null)
    {
      return progressEvent.LocalAction;
    }
    if (!progressEvent.Type.StartsWith("supervision.action-", StringComparison.Ordinal))
    {
      return null;
    }
    var actionId = ExtractSupervisionActionId(progressEvent.Message);
    if (actionId is null)
    {
      return null;
    }
    var action = view.Recovery?.Actions.FirstOrDefault(candidate => string.Equals(
      candidate.ActionId,
      actionId,
      StringComparison.Ordinal
    ));
    if (action is null)
    {
      return null;
    }
    var target = action.FileEffects.Count == 0
      ? action.Tool
      : string.Join(
        ", ",
        action.FileEffects.Select(effect => effect.RelativePath).Distinct(StringComparer.Ordinal)
      );
    var state = action.Phase switch
    {
      SupervisionActionPhases.Prepared => "proposed",
      SupervisionActionPhases.AwaitingApproval => "proposed",
      SupervisionActionPhases.InFlight => "executing",
      SupervisionActionPhases.Committed => "completed",
      SupervisionActionPhases.Rejected => "rejected",
      _ => "failed"
    };
    return new LocalActionEvent(
      action.ActionId,
      action.Tool,
      $"{action.Tool}: {target}",
      null,
      state,
      action.RequiresApproval,
      OriginalTool: action.Tool,
      Code: progressEvent.Type
    );
  }

  private static string? ExtractSupervisionActionId(string? message)
  {
    const string prefix = "Host action ";
    if (string.IsNullOrWhiteSpace(message) || !message.StartsWith(prefix, StringComparison.Ordinal))
    {
      return null;
    }
    var end = message.IndexOf(' ', prefix.Length);
    return end > prefix.Length
      ? message[prefix.Length..end]
      : null;
  }

  private static SlowRequestStatusView? CreateSupervisionSlowRequest(
    DurableSupervisionRunView view,
    SupervisionRunEvent progressEvent
  )
  {
    if (progressEvent.Type is not SupervisionEventTypeIds.TurnSlowWarning
      and not SupervisionEventTypeIds.TurnSlowCritical)
    {
      return null;
    }
    if (progressEvent.SlowRequest is not null)
    {
      return progressEvent.SlowRequest;
    }
    var lastActivityAt = view.Runtime?.Contexts.FirstOrDefault(context => string.Equals(
      context.Id,
      progressEvent.ContextId,
      StringComparison.Ordinal
    ))?.UpdatedAt ?? view.CreatedAt;
    return new SlowRequestStatusView(
      progressEvent.Type == SupervisionEventTypeIds.TurnSlowCritical
        ? "critical"
        : "warning",
      view.CreatedAt,
      lastActivityAt,
      Math.Max(0, (long)(progressEvent.Timestamp - view.CreatedAt).TotalMilliseconds),
      Math.Max(0, (long)(progressEvent.Timestamp - lastActivityAt).TotalMilliseconds),
      view.Route.Harness,
      view.Route.Model
    );
  }

  private async Task PersistExecutionCheckpointAsync(
    ChatStreamEvent streamEvent
  )
  {
    var summary = streamEvent.ExecutionSession;

    if (
      summary is null
      || string.IsNullOrWhiteSpace(
        _conversationSessionId
      )
      || streamEvent.Type == "response.delta"
    )
    {
      return;
    }

    var checkpoint = string.Join(
      ":",
      summary.State,
      summary.ActionCount,
      summary.ChangedFileCount,
      summary.Plan?.CompletedStepCount ?? 0,
      summary.CompletionStatus
    );

    if (string.Equals(
      checkpoint,
      _lastExecutionCheckpoint,
      StringComparison.Ordinal
    ))
    {
      return;
    }

    _lastExecutionCheckpoint = checkpoint;
    var review = _executionSessions.GetReview(
      summary.Id
    );

    try
    {
      await _persistentSessions.MarkTerminalAsync(
        _conversationSessionId,
        _turnId,
        summary.State,
        review,
        HttpContext.RequestAborted
      );
    }
    catch (WorkspaceProfileException exception)
    {
      _logger.LogWarning(
        exception,
        "An execution checkpoint could not be persisted."
      );
    }
  }

  private async Task MarkPersistentTerminalAsync(
    string state
  )
  {
    try
    {
      var review = string.IsNullOrWhiteSpace(
        _executionSessionId
      )
        ? null
        : _executionSessions.GetReview(
          _executionSessionId
        );
      await _persistentSessions.MarkTerminalAsync(
        _conversationSessionId,
        _turnId,
        state,
        review,
        CancellationToken.None
      );
    }
    catch (Exception exception)
    {
      _logger.LogWarning(
        exception,
        "The persistent conversation terminal state could not be recorded."
      );
    }
  }

  private ChatStreamEvent PersistenceEvent(
    string requestId,
    WorkspaceProfileException exception
  )
  {
    return new ChatStreamEvent(
      requestId,
      exception.Code,
      DateTimeOffset.UtcNow,
      exception.Message,
      null,
      null,
      null,
      0,
      null,
      null,
      null,
      null,
      _conversationSessionId
    );
  }

  private ChatStreamEvent SessionEvent(
    string requestId,
    string type,
    string message
  )
  {
    return new ChatStreamEvent(
      requestId,
      type,
      DateTimeOffset.UtcNow,
      message,
      null,
      null,
      null,
      0,
      null,
      null,
      null,
      null,
      _conversationSessionId
    );
  }

  private async Task WriteErrorAsync(
    string requestId,
    ChatStageException exception,
    CancellationToken cancellationToken
  )
  {
    var error = new ProviderError(
      exception.Stage,
      exception.Message,
      exception.TechnicalMessage,
      HttpContext.TraceIdentifier,
      exception.Provider,
      exception.Model,
      exception.Intention,
      exception.HttpStatus,
      exception.Recoverable,
      exception.Details,
      exception.Details?.TryGetValue("code", out var code) == true
        ? code
        : exception.Stage
    );
    var streamEvent = new ChatStreamEvent(
      requestId,
      "error",
      DateTimeOffset.UtcNow,
      exception.Message,
      null,
      exception.Model,
      exception.Intention,
      null,
      null,
      error
      ,
      null,
      CurrentExecutionSummary()
    );

    var writtenEvent = await WriteEventAsync(
      streamEvent,
      cancellationToken
    );
    await PersistFailureMessageAsync(
      writtenEvent
    );
  }

  private async Task PersistFailureMessageAsync(
    ChatStreamEvent streamEvent
  )
  {
    if (
      streamEvent.Error is null
      || string.IsNullOrWhiteSpace(_conversationSessionId)
    )
    {
      return;
    }

    var diagnostic = streamEvent.Diagnostic
      ?? new TraceDiagnosticReference(
        _trace.TraceId,
        "failed",
        streamEvent.Error.DiagnosticsPersisted
      );
    var review = string.IsNullOrWhiteSpace(_executionSessionId)
      ? null
      : _executionSessions.GetReview(_executionSessionId);
    try
    {
      await _persistentSessions.MarkTerminalAsync(
        _conversationSessionId,
        _turnId,
        "failed",
        review,
        CancellationToken.None,
        new ChatMessage(
          "assistant",
          $"{streamEvent.Error.Message}\nReference: {diagnostic.TraceId}",
          DateTimeOffset.UtcNow,
          diagnostic
        )
      );
    }
    catch (WorkspaceProfileException exception)
    {
      _logger.LogWarning(
        exception,
        "The failed conversation diagnostic reference could not be persisted."
      );
    }
  }

  private ExecutionSessionSummary? CurrentExecutionSummary()
  {
    return string.IsNullOrWhiteSpace(
      _executionSessionId
    )
      ? null
      : _executionSessions.Get(
        _executionSessionId
      )?.CreateSummary();
  }

  private static void CaptureContentBlock(
    List<ChatMessageContentBlock> blocks,
    ChatStreamEvent streamEvent
  )
  {
    var kind = streamEvent.Type switch
    {
      "reasoning.delta" => "reasoning",
      "response.delta" => "response",
      "response.completed" when !string.IsNullOrEmpty(
        streamEvent.ResponseTail
      ) => "response",
      _ => null
    };
    var content = streamEvent.Type switch
    {
      "reasoning.delta" => streamEvent.ReasoningDelta,
      "response.delta" => streamEvent.Delta,
      "response.completed" => streamEvent.ResponseTail,
      _ => null
    };

    if (kind is null || string.IsNullOrEmpty(content))
    {
      return;
    }

    var id = streamEvent.Type == "response.completed"
      ? $"terminal:{streamEvent.RequestId}"
      : streamEvent.ContentBlockId;
    var last = blocks.LastOrDefault();

    if (
      last is not null
      && string.Equals(
        last.Kind,
        kind,
        StringComparison.Ordinal
      )
      && string.Equals(
        last.Id,
        id,
        StringComparison.Ordinal
      )
    )
    {
      blocks[^1] = last with
      {
        Content = last.Content + content
      };
      return;
    }

    blocks.Add(
      new ChatMessageContentBlock(
        kind,
        content,
        id
      )
    );
  }

  private async Task PersistPresentationTimelineAsync(
    string requestId
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        _conversationSessionId
      )
      || _presentationTimeline.Count == 0
    )
    {
      return;
    }

    try
    {
      await _persistentSessions.PersistTimelineAsync(
        _conversationSessionId,
        _turnId,
        _presentationTimeline,
        CancellationToken.None
      );
    }
    catch (WorkspaceProfileException exception)
    {
      _logger.LogWarning(
        exception,
        "The complete visible conversation timeline could not be persisted."
      );
      try
      {
        await WriteEventAsync(
          PersistenceEvent(
            requestId,
            exception
          ),
          CancellationToken.None
        );
      }
      catch (Exception writeException)
      {
        _logger.LogDebug(
          writeException,
          "The client could not receive the timeline persistence failure."
        );
      }
    }
  }

  private async Task<ChatStreamEvent> WriteEventAsync(
    ChatStreamEvent streamEvent,
    CancellationToken cancellationToken
  )
  {
    streamEvent = streamEvent with
    {
      ConversationSessionId = streamEvent.ConversationSessionId ?? _conversationSessionId
    };
    var incident = IncidentEventFactory.FromChatEvent(_trace, streamEvent);
    if (incident is not null)
    {
      var write = _incidents.AppendAsync(incident, CancellationToken.None);
      _incidentWrites.Add(write);
      var terminal = streamEvent.Type is "error" or "response.completed" or "request.cancelled";
      var diagnosticCheckpoint = streamEvent.Type is
        "request.slow-warning" or "request.slow-critical"
        or "request.slow-activity-resumed";
      IncidentAppendResult result;
      if (terminal || diagnosticCheckpoint)
      {
        var results = await Task.WhenAll(_incidentWrites);
        result = results[^1];
        _incidentWrites.Clear();
      }
      else
      {
        result = new IncidentAppendResult(false);
      }
      if (streamEvent.Error is not null)
      {
        streamEvent = streamEvent with
        {
          Error = streamEvent.Error with
          {
            DiagnosticsPersisted = result.Persisted,
            ContextFit = incident.ContextFit is null
              ? null
              : new IncidentContextFitView(
                incident.ContextFit.EstimatedInputTokens,
                incident.ContextFit.ReservedOutputTokens,
                incident.ContextFit.RequiredContextTokens,
                incident.ContextFit.MaximumContextTokens,
                incident.ContextFit.EffectiveContextTokens
              )
          }
        };
      }
      if (diagnosticCheckpoint && streamEvent.Diagnostic is not null)
      {
        streamEvent = streamEvent with
        {
          Diagnostic = streamEvent.Diagnostic with
          {
            Persisted = result.Persisted
          }
        };
      }
      if (terminal)
      {
        streamEvent = streamEvent with
        {
          Diagnostic = new TraceDiagnosticReference(
            _trace.TraceId,
            streamEvent.Type switch
            {
              "response.completed" => "completed",
              "request.cancelled" => "cancelled",
              _ => "failed"
            },
            result.Persisted
          )
        };
      }
    }

    _presentationTimeline.Add(
      streamEvent
    );

    var json = JsonSerializer.Serialize(
      streamEvent,
      JsonOptions
    );

    await Response.WriteAsync(
      $"data: {json}\n\n",
      cancellationToken
    );
    await Response.Body.FlushAsync(
      cancellationToken
    );
    return streamEvent;
  }
}
