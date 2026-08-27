using System.Diagnostics;
using System.Text.Json;
using AgenticRouter.Api.Chat;
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
  private readonly IMarkdownRenderer _markdown;
  private string? _executionSessionId;
  private string? _conversationSessionId;
  private string? _durableSupervisionRunId;
  private string? _lastExecutionCheckpoint;
  private readonly List<Task<IncidentAppendResult>> _incidentWrites = [];

  public ChatController(
    IChatStreamService chatStreamService,
    IExecutionSessionStore executionSessions,
    IPersistentSessionService persistentSessions,
    IImageAttachmentValidator imageValidator,
    IDurableSupervisionRunCoordinator supervisionRuns,
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
    try
    {
      supervision = SupervisionRequestPolicy.Resolve(
        request
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
          request.Message,
          request.InteractionMode,
          request.Model,
          request.Images,
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
      }

      var answer = new System.Text.StringBuilder();
      var normalizedRequest = request with
      {
        ConversationSessionId = _conversationSessionId
      };
      var stream = supervision.Supervised
        ? StreamSupervisedAsync(
          normalizedRequest,
          supervision,
          requestId,
          cancellationToken
        )
        : _chatStreamService.StreamAsync(
          normalizedRequest,
          requestId,
          cancellationToken
        );
      await foreach (var streamEvent in stream)
      {
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
              answer.ToString(),
              request.InteractionMode,
              streamEvent.SelectedModel,
              review,
              cancellationToken
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

        await WriteEventAsync(
          streamEvent with
          {
            ConversationSessionId = _conversationSessionId
          },
          cancellationToken
        );
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
              answer.ToString(),
              "execute",
              streamEvent.SelectedModel,
              null,
              cancellationToken
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
  }

  private async IAsyncEnumerable<ChatStreamEvent> StreamSupervisedAsync(
    ChatRequest request,
    SupervisionRequestResolution supervision,
    string requestId,
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
          AutoModelHarness: request.AutoModelHarness,
          History: request.History,
          Images: request.Images
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
      null
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
    await foreach (var progressEvent in _supervisionRuns.SubscribeAsync(
      runId,
      afterSequence,
      follow: true,
      cancellationToken
    ))
    {
      yield return new ChatStreamEvent(
        requestId,
        progressEvent.Type,
        progressEvent.Timestamp,
        progressEvent.Message,
        null,
        startedView.Route.Model,
        null,
        stopwatch.ElapsedMilliseconds,
        null,
        null
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
          null
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
          null
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
          null
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
            ["code"] = "supervision-blocked",
            ["runId"] = view.RunId,
            ["workItemId"] = view.Runtime?.ActiveWorkItemId
          },
          "supervision-blocked"
        )
      );
      yield break;
    }
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

    await WriteEventAsync(
      streamEvent,
      cancellationToken
    );
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

  private async Task WriteEventAsync(
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
      IncidentAppendResult result;
      if (terminal)
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
    }

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
  }
}
