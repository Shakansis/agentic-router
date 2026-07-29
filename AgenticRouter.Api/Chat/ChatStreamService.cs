using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Routing;
using AgenticRouter.Api.Runtime;

namespace AgenticRouter.Api.Chat;

public sealed class ChatStreamService : IChatStreamService
{
  private const string GeneralChat = "general-chat";

  private readonly ISettingsStore _settingsStore;
  private readonly IOllamaClient _ollamaClient;
  private readonly IMarkdownRenderer _markdownRenderer;
  private readonly IResidentModelManager _residentModel;
  private readonly IIntentionRouter _intentionRouter;
  private readonly IModelResolver _modelResolver;
  private readonly IConversationContextBuilder _contextBuilder;
  private readonly ITrustedWorkspaceService _workspace;
  private readonly ILocalActionPlanner _actionPlanner;
  private readonly IExpertExecutionGuidanceService _expertGuidance;
  private readonly ILocalActionService _actionService;
  private readonly IApprovalPolicyService _approvalPolicy;
  private readonly IApprovalCoordinator _approvalCoordinator;
  private readonly ILogger<ChatStreamService> _logger;

  public ChatStreamService(
    ISettingsStore settingsStore,
    IOllamaClient ollamaClient,
    IMarkdownRenderer markdownRenderer,
    IResidentModelManager residentModel,
    IIntentionRouter intentionRouter,
    IModelResolver modelResolver,
    IConversationContextBuilder contextBuilder,
    ITrustedWorkspaceService workspace,
    ILocalActionPlanner actionPlanner,
    IExpertExecutionGuidanceService expertGuidance,
    ILocalActionService actionService,
    IApprovalPolicyService approvalPolicy,
    IApprovalCoordinator approvalCoordinator,
    ILogger<ChatStreamService> logger
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _markdownRenderer = markdownRenderer;
    _residentModel = residentModel;
    _intentionRouter = intentionRouter;
    _modelResolver = modelResolver;
    _contextBuilder = contextBuilder;
    _workspace = workspace;
    _actionPlanner = actionPlanner;
    _expertGuidance = expertGuidance;
    _actionService = actionService;
    _approvalPolicy = approvalPolicy;
    _approvalCoordinator = approvalCoordinator;
    _logger = logger;
  }

  public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
    ChatRequest request,
    string requestId,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    using var requestLease = _residentModel.BeginRequest();
    var stopwatch = Stopwatch.StartNew();
    var recoveryActive = false;
    string? recoveryTarget = null;

    try
    {
      yield return Event(
        requestId,
        "request.received",
        $"Request {requestId} received.",
        stopwatch
      );

      ValidateInteractionMode(
        request
      );
      var settings = await _settingsStore.GetAsync(
        cancellationToken
      );
      var baseUri = new Uri(
        settings.OllamaUrl,
        UriKind.Absolute
      );

      yield return Event(
        requestId,
        "settings.loaded",
        "Settings loaded.",
        stopwatch
      );
      yield return Event(
        requestId,
        "ollama.models-query-started",
        "Checking installed Ollama models.",
        stopwatch
      );

      var models = await GetModelsAsync(
        baseUri,
        cancellationToken
      );
      var isAuto = string.IsNullOrWhiteSpace(
        request.Model
      ) || string.Equals(
        request.Model,
        "auto",
        StringComparison.OrdinalIgnoreCase
      );
      var intention = GeneralChat;
      var selectedModel = request.Model.Trim();

      if (!isAuto)
      {
        yield return Event(
          requestId,
          request.ModelLocked
            ? "model.lock-active"
            : "model.explicit-selected",
          request.ModelLocked
            ? "Conversation model lock active."
            : "Manual model override.",
          stopwatch,
          selectedModel
        );
        yield return Event(
          requestId,
          "router.bypassed",
          "Router bypassed.",
          stopwatch,
          selectedModel
        );

        if (!ContainsModel(
          models,
          selectedModel
        ))
        {
          throw new ChatStageException(
            "target-model-resolution",
            $"The target model '{selectedModel}' is not installed in Ollama.",
            "The explicit target model was not present in the /api/tags response.",
            selectedModel,
            null,
            400,
            true,
            details: new Dictionary<string, string?>
            {
              ["selectionMode"] = request.ModelLocked
                ? "conversation-lock"
                : "manual",
              ["installedModelAvailability"] = "unavailable"
            }
          );
        }
      }
      else
      {
        yield return Event(
          requestId,
          "router.auto-enabled",
          "Auto routing enabled.",
          stopwatch
        );
        yield return Event(
          requestId,
          "router.model-resolved",
          $"Router model: {settings.RouterModel}.",
          stopwatch,
          settings.RouterModel
        );

        if (!ContainsModel(
          models,
          settings.RouterModel
        ))
        {
          yield return Event(
            requestId,
            "router.warning",
            $"Router model '{settings.RouterModel}' is unavailable; using general-chat fallback.",
            stopwatch,
            settings.RouterModel,
            GeneralChat
          );
        }
        else
        {
          yield return Event(
            requestId,
            "router.classification-started",
            "Classifying request intention.",
            stopwatch,
            settings.RouterModel
          );
          yield return Event(
            requestId,
            "ollama.connection-started",
            $"Connecting to Ollama for router model {settings.RouterModel}.",
            stopwatch,
            settings.RouterModel
          );

          var routing = await ClassifyAsync(
            baseUri,
            settings.RouterModel,
            request,
            cancellationToken
          );

          if (routing.Decision is not null)
          {
            intention = routing.Decision.Intention;
            yield return Event(
              requestId,
              "router.classified",
              $"Intent: {intention}.",
              stopwatch,
              settings.RouterModel,
              intention
            );
            yield return Event(
              requestId,
              "router.confidence",
              $"Confidence: {FormatConfidence(routing.Decision.Confidence)}.",
              stopwatch,
              settings.RouterModel,
              intention
            );

            if (!string.IsNullOrWhiteSpace(
              routing.Decision.Reason
            ))
            {
              yield return Event(
                requestId,
                "router.reason",
                $"Reason: {routing.Decision.Reason}",
                stopwatch,
                settings.RouterModel,
                intention
              );
            }
          }
          else
          {
            yield return Event(
              requestId,
              "router.warning",
              $"{routing.Warning} Stage: router-response-parse. "
                + $"Router model: {settings.RouterModel}. "
                + $"Parse failure: {routing.FailureType ?? "none"}. "
                + $"Raw output captured internally: {routing.RawOutputCaptured}. "
                + $"Trace ID: {requestId}. Recoverable: true.",
              stopwatch,
              settings.RouterModel,
              GeneralChat
            );
          }
        }

        yield return Event(
          requestId,
          "target.configuration",
          $"Target configuration: {intention}.",
          stopwatch,
          null,
          intention
        );
        var resolution = _modelResolver.Resolve(
          settings,
          intention,
          models
        );

        foreach (var attempt in resolution.Attempts.Where(
          attempt => !attempt.Installed
        ))
        {
          yield return Event(
            requestId,
            "target.model-fallback",
            $"{attempt.Source} model '{attempt.ResolvedModel}' is unavailable; trying next configured fallback.",
            stopwatch,
            attempt.ResolvedModel,
            intention
          );
        }

        if (resolution.Model is null)
        {
          var intentionSettings = settings.Intentions[intention];
          throw new ChatStageException(
            "target-model-resolution",
            $"No installed model could be resolved for '{intention}'.",
            "The configured primary, fallback, and global default models were unavailable.",
            null,
            intention,
            400,
            true,
            details: new Dictionary<string, string?>
            {
              ["requestedIntention"] = intention,
              ["configuredPrimaryModel"] = intentionSettings.Model,
              ["configuredFallbackModel"] = intentionSettings.FallbackModel,
              ["globalDefaultModel"] = settings.DefaultModel,
              ["installedModelAvailability"] = string.Join(
                ", ",
                models.Select(
                  model => model.Name
                )
              )
            }
          );
        }

        selectedModel = resolution.Model;
      }

      yield return Event(
        requestId,
        "target.model-resolved",
        $"Target model: {selectedModel}.",
        stopwatch,
        selectedModel,
        isAuto
          ? intention
          : null
      );
      yield return Event(
        requestId,
        "ollama.connection-started",
        $"Connecting to Ollama for target model {selectedModel}.",
        stopwatch,
        selectedModel,
        isAuto
          ? intention
          : null
      );

      var context = _contextBuilder.Build(
        request,
        settings,
        intention
      );

      if (context.OmittedMessages > 0)
      {
        yield return Event(
          requestId,
          "context.trimmed",
          $"Conversation context trimmed: {context.OmittedMessages} older messages omitted.",
          stopwatch,
          selectedModel,
          isAuto
            ? intention
            : null
        );
      }

      IReadOnlyList<ChatMessage> messages = context.Messages;

      if (string.Equals(
        request.InteractionMode,
        "execute",
        StringComparison.Ordinal
      ))
      {
        yield return Event(
          requestId,
          "interaction.execute",
          $"Execute mode enabled with {request.ApprovalPolicy} approval policy.",
          stopwatch,
          selectedModel,
          intention
        );
        var workspace = await _workspace.GetStatusAsync(
          cancellationToken
        );

        if (!workspace.Valid || workspace.Path is null)
        {
          yield return Event(
            requestId,
            "action.validation-error",
            workspace.Diagnostic
              ?? "Configure a valid trusted workspace before using Execute mode.",
            stopwatch,
            selectedModel,
            intention
          );
          throw new ChatStageException(
            "trusted-workspace",
            "Configure a valid trusted workspace before using Execute mode.",
            workspace.Diagnostic ?? "No valid trusted workspace is configured.",
            selectedModel,
            intention,
            400,
            true
          );
        }

        yield return Event(
          requestId,
          "workspace.validated",
          $"Trusted workspace: {workspace.Path}.",
          stopwatch,
          selectedModel,
          intention
        );

        var tooling = await InspectToolingAsync(
          baseUri,
          selectedModel,
          cancellationToken
        );
        string coordinatorModel;
        List<ChatMessage> executionMessages;
        var targetCoordinatesDirectly =
          tooling.Capabilities?.ToolingConfirmed == true;

        if (targetCoordinatesDirectly)
        {
          yield return Event(
            requestId,
            "agent.tooling-confirmed",
            $"Ollama confirmed tooling for {selectedModel}; the target model will coordinate local actions directly.",
            stopwatch,
            selectedModel,
            intention
          );
          coordinatorModel = selectedModel;
          executionMessages = messages.ToList();
        }
        else
        {
          var capabilityMessage = tooling.Failure is null
            ? $"Ollama did not confirm tooling for {selectedModel}; the resident agent will bridge the specialist guidance."
            : $"Tooling capability could not be confirmed for {selectedModel}; the resident agent will bridge the specialist guidance.";
          yield return Event(
            requestId,
            "agent.tooling-unconfirmed",
            capabilityMessage,
            stopwatch,
            selectedModel,
            intention
          );

          if (!ContainsModel(
            models,
            settings.RouterModel
          ))
          {
            yield return Event(
              requestId,
              "action.validation-error",
              $"Resident tooling agent '{settings.RouterModel}' is not installed.",
              stopwatch,
              settings.RouterModel,
              intention
            );
            throw new ChatStageException(
              "resident-agent-resolution",
              "The resident tooling agent is unavailable.",
              $"Configured resident model '{settings.RouterModel}' was not present in the installed model list.",
              settings.RouterModel,
              intention,
              400,
              true
            );
          }

          yield return Event(
            requestId,
            "agent.expert-guidance-started",
            $"Specialist model {selectedModel} is preparing execution guidance.",
            stopwatch,
            selectedModel,
            intention
          );
          var guidance = await TryPrepareGuidanceAsync(
            baseUri,
            selectedModel,
            messages,
            cancellationToken
          );

          if (guidance.Guidance is not null)
          {
            yield return Event(
              requestId,
              "agent.expert-guidance-prepared",
              $"Execution guidance received from specialist model {selectedModel}.",
              stopwatch,
              selectedModel,
              intention
            );
            executionMessages = messages.Concat(
              [
                GuidanceMessage(
                  selectedModel,
                  guidance.Guidance
                )
              ]
            ).ToList();
          }
          else
          {
            _logger.LogWarning(
              guidance.Failure,
              "Specialist guidance was unavailable for model {Model}; the resident agent will plan directly.",
              selectedModel
            );
            yield return Event(
              requestId,
              "agent.expert-guidance-unavailable",
              $"Specialist guidance was unavailable: {guidance.Failure!.Message} "
                + "The resident agent will continue directly from the conversation context.",
              stopwatch,
              selectedModel,
              intention
            );
            executionMessages = messages.ToList();
          }

          yield return Event(
            requestId,
            "agent.resident-bridge-resolved",
            $"Resident tooling agent: {settings.RouterModel}.",
            stopwatch,
            settings.RouterModel,
            intention
          );
          coordinatorModel = settings.RouterModel;
        }

        var execution = new ExecutionProgress(
          executionMessages
        );

        await foreach (var streamEvent in ExecuteActionsAsync(
          baseUri,
          coordinatorModel,
          request,
          requestId,
          intention,
          stopwatch,
          execution,
          targetCoordinatesDirectly
            ? null
            : selectedModel,
          targetCoordinatesDirectly
            ? 2
            : 3,
          targetCoordinatesDirectly,
          cancellationToken
        ))
        {
          yield return streamEvent;
        }

        if (
          targetCoordinatesDirectly
          && execution.PlanningFailure is not null
        )
        {
          if (!ContainsModel(
            models,
            settings.RouterModel
          ))
          {
            yield return Event(
              requestId,
              "action.validation-error",
              $"Resident tooling agent '{settings.RouterModel}' is not installed.",
              stopwatch,
              settings.RouterModel,
              intention
            );
            throw new ChatStageException(
              "resident-agent-resolution",
              "The resident tooling agent is unavailable.",
              $"Configured resident model '{settings.RouterModel}' was not present in the installed model list.",
              settings.RouterModel,
              intention,
              400,
              true
            );
          }

          yield return Event(
            requestId,
            "agent.expert-guidance-started",
            $"Specialist model {selectedModel} is preparing execution guidance for the resident takeover.",
            stopwatch,
            selectedModel,
            intention
          );
          var guidance = await TryPrepareGuidanceAsync(
            baseUri,
            selectedModel,
            execution.Messages,
            cancellationToken
          );
          var residentMessages = execution.Messages.ToList();

          if (guidance.Guidance is not null)
          {
            yield return Event(
              requestId,
              "agent.expert-guidance-prepared",
              $"Execution guidance received from specialist model {selectedModel}.",
              stopwatch,
              selectedModel,
              intention
            );
            residentMessages.Add(
              GuidanceMessage(
                selectedModel,
                guidance.Guidance
              )
            );
          }
          else
          {
            _logger.LogWarning(
              guidance.Failure,
              "Takeover guidance was unavailable for model {Model}; the resident agent will plan directly.",
              selectedModel
            );
            yield return Event(
              requestId,
              "agent.expert-guidance-unavailable",
              $"Specialist guidance was unavailable: {guidance.Failure!.Message} "
                + "The resident agent will take over directly from the conversation and completed action results.",
              stopwatch,
              selectedModel,
              intention
            );
          }

          yield return Event(
            requestId,
            "agent.resident-bridge-resolved",
            $"Resident tooling agent {settings.RouterModel} took over with a reset planning error counter.",
            stopwatch,
            settings.RouterModel,
            intention
          );
          execution = new ExecutionProgress(
            residentMessages
          );

          await foreach (var streamEvent in ExecuteActionsAsync(
            baseUri,
            settings.RouterModel,
            request,
            requestId,
            intention,
            stopwatch,
            execution,
            selectedModel,
            3,
            false,
            cancellationToken
          ))
          {
            yield return streamEvent;
          }
        }

        if (execution.Failure is not null)
        {
          throw execution.Failure;
        }

        execution.Messages.Add(
          new ChatMessage(
            "system",
            ExpertExecutionGuidanceService.FinalResponseInstruction
          )
        );
        messages = execution.Messages;
      }
      else
      {
        yield return Event(
          requestId,
          "interaction.chat",
          "Chat mode active; local tools are disabled.",
          stopwatch,
          selectedModel,
          intention
        );
      }

      var progress = new GenerationProgress();

      await foreach (var streamEvent in StreamAttemptAsync(
        baseUri,
        selectedModel,
        messages,
        requestId,
        isAuto
          ? intention
          : null,
        stopwatch,
        progress,
        cancellationToken
      ))
      {
        yield return streamEvent;
      }

      if (progress.Failure is not null)
      {
        var failure = progress.Failure;
        var canRecover = failure.IsMemoryPressure
          && !progress.ReceivedFirstChunk
          && !string.Equals(
            selectedModel,
            settings.RouterModel,
            StringComparison.OrdinalIgnoreCase
          );

        if (!canRecover)
        {
          throw ToChatException(
            failure,
            selectedModel,
            isAuto
              ? intention
              : null
          );
        }

        yield return Event(
          requestId,
          "memory-pressure-detected",
          $"Ollama reported memory pressure while loading {selectedModel}.",
          stopwatch,
          selectedModel,
          intention
        );
        yield return Event(
          requestId,
          "resident-model-eviction-started",
          $"Evicting resident router model {settings.RouterModel} for one adaptive retry.",
          stopwatch,
          settings.RouterModel,
          intention
        );

        recoveryActive = await _residentModel.EvictForRecoveryAsync(
          selectedModel,
          cancellationToken
        );
        recoveryTarget = selectedModel;

        if (!recoveryActive)
        {
          throw ToChatException(
            failure,
            selectedModel,
            intention
          );
        }

        yield return Event(
          requestId,
          "resident-model-evicted",
          $"Resident router model {settings.RouterModel} was temporarily evicted.",
          stopwatch,
          settings.RouterModel,
          intention
        );
        yield return Event(
          requestId,
          "target-request-retry-started",
          $"Retrying target model {selectedModel} once.",
          stopwatch,
          selectedModel,
          intention
        );

        progress.Failure = null;

        await foreach (var streamEvent in StreamAttemptAsync(
          baseUri,
          selectedModel,
          messages,
          requestId,
          isAuto
            ? intention
            : null,
          stopwatch,
          progress,
          cancellationToken
        ))
        {
          yield return streamEvent;
        }

        if (progress.Failure is not null)
        {
          var retryFailure = progress.Failure;
          yield return Event(
            requestId,
            "resident-model-reload-started",
            $"Reloading resident router model {settings.RouterModel}.",
            stopwatch,
            settings.RouterModel,
            intention
          );
          var restored = await _residentModel.RestoreAfterRecoveryAsync(
            selectedModel,
            cancellationToken
          );
          recoveryActive = false;
          yield return Event(
            requestId,
            restored
              ? "resident-model-reloaded"
              : "resident-model-reload-failed",
            restored
              ? $"Resident router model {settings.RouterModel} was restored."
              : $"Resident router model {settings.RouterModel} could not be restored.",
            stopwatch,
            settings.RouterModel,
            intention
          );

          throw ToChatException(
            retryFailure,
            selectedModel,
            intention
          );
        }

        yield return Event(
          requestId,
          "target-request-recovered",
          $"Target model {selectedModel} recovered after adaptive eviction.",
          stopwatch,
          selectedModel,
          intention
        );
        yield return Event(
          requestId,
          "resident-model-reload-started",
          $"Reloading resident router model {settings.RouterModel}.",
          stopwatch,
          settings.RouterModel,
          intention
        );
        var reloaded = await _residentModel.RestoreAfterRecoveryAsync(
          selectedModel,
          cancellationToken
        );
        recoveryActive = false;
        yield return Event(
          requestId,
          reloaded
            ? "resident-model-reloaded"
            : "resident-model-reload-failed",
          reloaded
            ? $"Resident router model {settings.RouterModel} was restored."
            : $"Resident router model {settings.RouterModel} could not be restored.",
          stopwatch,
          settings.RouterModel,
          intention
        );
      }

      yield return new ChatStreamEvent(
        requestId,
        "response.completed",
        DateTimeOffset.UtcNow,
        $"Response completed in {stopwatch.ElapsedMilliseconds} ms.",
        null,
        selectedModel,
        isAuto
          ? intention
          : null,
        stopwatch.ElapsedMilliseconds,
        _markdownRenderer.Render(
          progress.Answer.ToString()
        ),
        null
      );
    }
    finally
    {
      if (recoveryActive && recoveryTarget is not null)
      {
        await _residentModel.RestoreAfterRecoveryAsync(
          recoveryTarget,
          CancellationToken.None
        );
      }
    }
  }

  private async IAsyncEnumerable<ChatStreamEvent> ExecuteActionsAsync(
    Uri baseUri,
    string model,
    ChatRequest request,
    string requestId,
    string intention,
    Stopwatch stopwatch,
    ExecutionProgress progress,
    string? recoverySpecialistModel,
    int maximumPlanningAttempts,
    bool fallbackToResident,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    const int maximumActions = 8;

    for (var index = 0; index < maximumActions; index++)
    {
      yield return Event(
        requestId,
        "action.planning-started",
        "Checking whether a local action is needed.",
        stopwatch,
        model,
        intention
      );
      ValidatedLocalAction? validatedAction = null;
      Exception? exhaustedFailure = null;
      var noActionRequired = false;

      for (
        var attempt = 1;
        attempt <= maximumPlanningAttempts;
        attempt++
      )
      {
        var planning = await TryPlanAsync(
          () => _actionPlanner.PlanAsync(
            baseUri,
            model,
            progress.Messages,
            attempt,
            cancellationToken
          )
        );

        if (planning.Failure is not null)
        {
          exhaustedFailure = planning.Failure;
          _logger.LogWarning(
            planning.Failure,
            "Local action planning attempt {Attempt} of {MaximumAttempts} failed for request {RequestId}.",
            attempt,
            maximumPlanningAttempts,
            requestId
          );

          if (attempt < maximumPlanningAttempts)
          {
            yield return Event(
              requestId,
              "action.planning-retry",
              $"Planning attempt {attempt} of {maximumPlanningAttempts} failed: "
                + $"{planning.Failure.Message} Retrying with attempt {attempt + 1} of "
                + $"{maximumPlanningAttempts}.",
              stopwatch,
              model,
              intention
            );
          }

          continue;
        }

        var proposal = planning.Proposal;

        if (proposal is null)
        {
          noActionRequired = true;
          break;
        }

        var validation = await TryValidateAsync(
          () => _actionService.ValidateAsync(
            proposal,
            cancellationToken
          )
        );

        if (validation.Failure is null)
        {
          validatedAction = validation.Action;
          break;
        }

        var exception = validation.Failure;
        _logger.LogWarning(
          exception,
          "Local action proposal {Tool} failed validation for request {RequestId}.",
          proposal.Tool,
          requestId
        );

        if (!IsReplannableValidation(
          exception
        ))
        {
          yield return Event(
            requestId,
            "action.validation-error",
            $"{proposal.Tool}: {exception.Message}",
            stopwatch,
            model,
            intention
          );
          progress.Failure = ToChatException(
            exception,
            model,
            intention
          );
          yield break;
        }

        exhaustedFailure = exception;

        if (attempt < maximumPlanningAttempts)
        {
          yield return Event(
            requestId,
            "action.planning-retry",
            $"Planning attempt {attempt} of {maximumPlanningAttempts} produced an invalid action: "
              + $"{exception.Message} Retrying with attempt {attempt + 1} of "
              + $"{maximumPlanningAttempts}.",
            stopwatch,
            model,
            intention
          );
        }
      }

      if (noActionRequired)
      {
        yield return Event(
          requestId,
          "action.planning-completed",
          "No further local action is required.",
          stopwatch,
          model,
          intention
        );
        yield break;
      }

      if (validatedAction is null)
      {
        var exception = exhaustedFailure ?? throw new InvalidOperationException(
          "Action planning ended without a result or failure."
        );

        if (fallbackToResident)
        {
          yield return Event(
            requestId,
            "agent.tooling-fallback",
            $"Planning attempt {maximumPlanningAttempts} of {maximumPlanningAttempts} failed: "
              + $"{exception.Message} Tooling for {model} will be ignored for this turn; "
              + "the resident agent will take over with a reset error counter.",
            stopwatch,
            model,
            intention
          );
          progress.PlanningFailure = exception;
          yield break;
        }

        yield return Event(
          requestId,
          "action.validation-error",
          $"Local action planning failed after {maximumPlanningAttempts} attempts: {exception.Message}",
          stopwatch,
          model,
          intention
        );
        progress.Failure = ToPlanningChatException(
          exception,
          model,
          intention
        );
        yield break;
      }

      var action = validatedAction;
      _logger.LogInformation(
        "Local action {ActionId} proposed for request {RequestId}: {Tool} {Summary}.",
        action.ActionId,
        requestId,
        action.Tool,
        action.Summary
      );
      yield return ActionEvent(
        requestId,
        "action.proposed",
        $"Proposed action: {action.Summary}.",
        stopwatch,
        model,
        intention,
        action,
        "proposed",
        false
      );
      var requiresApproval = _approvalPolicy.RequiresApproval(
        action,
        request.ApprovalPolicy
      );

      if (requiresApproval)
      {
        var decisionTask = _approvalCoordinator.WaitAsync(
          action.ActionId,
          cancellationToken
        );
        yield return ActionEvent(
          requestId,
          "action.awaiting-approval",
          $"Waiting for approval: {action.Summary}.",
          stopwatch,
          model,
          intention,
          action,
          "awaiting-approval",
          true
        );
        var approved = await decisionTask;

        if (!approved)
        {
          _logger.LogInformation(
            "Local action {ActionId} was rejected for request {RequestId}.",
            action.ActionId,
            requestId
          );
          yield return ActionEvent(
            requestId,
            "action.rejected",
            $"Action rejected: {action.Summary}.",
            stopwatch,
            model,
            intention,
            action,
            "rejected",
            true
          );
          progress.Messages.Add(
            ToolResultMessage(
              action,
              "rejected",
              "The user rejected this action. It was not executed."
            )
          );
          yield break;
        }

        yield return ActionEvent(
          requestId,
          "action.approved",
          $"Action approved: {action.Summary}.",
          stopwatch,
          model,
          intention,
          action,
          "approved",
          true
        );
      }
      else
      {
        yield return ActionEvent(
          requestId,
          "action.approved",
          $"Action approved by policy: {action.Summary}.",
          stopwatch,
          model,
          intention,
          action,
          "approved",
          false
        );
      }

      yield return ActionEvent(
        requestId,
        "action.execution-started",
        $"Executing: {action.Summary}.",
        stopwatch,
        model,
        intention,
        action,
        "executing",
        requiresApproval
      );

      var execution = await TryExecuteAsync(
        () => _actionService.ExecuteAsync(
          action,
          cancellationToken
        )
      );

      if (execution.Result is not null)
      {
        var result = execution.Result;
        _logger.LogInformation(
          "Local action {ActionId} completed for request {RequestId}: {Tool}.",
          action.ActionId,
          requestId,
          action.Tool
        );
        yield return ActionEvent(
          requestId,
          result.EventType,
          FormatActivityOutput(
            action,
            result.Output
          ),
          stopwatch,
          model,
          intention,
          action,
          "completed",
          requiresApproval
        );
        progress.Messages.Add(
          ToolResultMessage(
            action,
            "completed",
            result.Output
          )
        );
      }
      else
      {
        var exception = execution.Failure!;
        var failureOutput = FormatExecutionFailure(
          exception
        );
        _logger.LogWarning(
          exception,
          "Local action {ActionId} failed for request {RequestId}.",
          action.ActionId,
          requestId
        );
        yield return ActionEvent(
          requestId,
          "action.execution-error",
          $"{action.Summary}: {failureOutput}",
          stopwatch,
          model,
          intention,
          action,
          "failed",
          requiresApproval
        );
        progress.Messages.Add(
          ToolResultMessage(
            action,
            "failed",
            failureOutput
          )
        );

        if (!string.IsNullOrWhiteSpace(
          recoverySpecialistModel
        ))
        {
          yield return Event(
            requestId,
            "agent.execution-recovery-started",
            $"Execution failure sent to specialist model {recoverySpecialistModel} for revised guidance.",
            stopwatch,
            recoverySpecialistModel,
            intention
          );
          var guidance = await TryPrepareGuidanceAsync(
            baseUri,
            recoverySpecialistModel,
            progress.Messages,
            cancellationToken
          );

          if (guidance.Guidance is not null)
          {
            progress.Messages.Add(
              GuidanceMessage(
                recoverySpecialistModel,
                guidance.Guidance
              )
            );
            yield return Event(
              requestId,
              "agent.execution-recovery-guidance-prepared",
              $"Revised execution guidance received from specialist model {recoverySpecialistModel}.",
              stopwatch,
              recoverySpecialistModel,
              intention
            );
          }
          else
          {
            _logger.LogWarning(
              guidance.Failure,
              "Execution recovery guidance was unavailable for model {Model}; the coordinator will replan directly.",
              recoverySpecialistModel
            );
            yield return Event(
              requestId,
              "agent.execution-recovery-guidance-unavailable",
              $"Revised specialist guidance was unavailable: {guidance.Failure!.Message} "
                + "The active coordinator will replan directly from the failed tool result.",
              stopwatch,
              recoverySpecialistModel,
              intention
            );
          }
        }

        yield return Event(
          requestId,
          "action.recovery-planning",
          $"Execution failed for {action.Tool}; the result was returned to the active coordinator for replanning.",
          stopwatch,
          model,
          intention
        );
      }
    }

    progress.Failure = new ChatStageException(
      "local-action-limit",
      "The request reached the limit of 8 local actions.",
      "The bounded Execute loop stopped before another action could be planned.",
      model,
      intention,
      400,
      true
    );
    yield return Event(
      requestId,
      "action.validation-error",
      progress.Failure.Message,
      stopwatch,
      model,
      intention
    );
  }

  private async IAsyncEnumerable<ChatStreamEvent> StreamAttemptAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    string requestId,
    string? intention,
    Stopwatch stopwatch,
    GenerationProgress progress,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    await using var updates = _ollamaClient.StreamChatAsync(
      baseUri,
      model,
      messages,
      cancellationToken
    ).GetAsyncEnumerator(
      cancellationToken
    );

    while (true)
    {
      OllamaChatUpdate update;

      try
      {
        if (!await updates.MoveNextAsync())
        {
          break;
        }

        update = updates.Current;
      }
      catch (OllamaProviderException exception)
      {
        progress.Failure = exception;
        yield break;
      }

      if (update.Accepted)
      {
        yield return Event(
          requestId,
          "ollama.generation-accepted",
          "Generation accepted by Ollama.",
          stopwatch,
          model,
          intention
        );
        continue;
      }

      if (string.IsNullOrEmpty(
        update.Delta
      ))
      {
        continue;
      }

      if (!progress.ReceivedFirstChunk)
      {
        progress.ReceivedFirstChunk = true;
        yield return Event(
          requestId,
          "response.first-chunk",
          "First response chunk received.",
          stopwatch,
          model,
          intention
        );
      }

      progress.Answer.Append(
        update.Delta
      );
      yield return new ChatStreamEvent(
        requestId,
        "response.delta",
        DateTimeOffset.UtcNow,
        null,
        update.Delta,
        model,
        intention,
        stopwatch.ElapsedMilliseconds,
        _markdownRenderer.Render(
          progress.Answer.ToString()
        ),
        null
      );
    }
  }

  private async Task<IReadOnlyList<InstalledModel>> GetModelsAsync(
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await _ollamaClient.GetModelsAsync(
        baseUri,
        cancellationToken
      );
    }
    catch (OllamaProviderException exception)
    {
      throw ToChatException(
        exception,
        null,
        null
      );
    }
  }

  private async Task<IntentionRoutingResult> ClassifyAsync(
    Uri baseUri,
    string routerModel,
    ChatRequest request,
    CancellationToken cancellationToken
  )
  {
    return await _intentionRouter.RouteAsync(
      baseUri,
      routerModel,
      request,
      cancellationToken
    );
  }

  private async Task<GuidanceAttempt> TryPrepareGuidanceAsync(
    Uri baseUri,
    string model,
    IReadOnlyList<ChatMessage> messages,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return new GuidanceAttempt(
        await _expertGuidance.PrepareAsync(
          baseUri,
          model,
          messages,
          cancellationToken
        ),
        null
      );
    }
    catch (LocalActionException exception)
    {
      return new GuidanceAttempt(
        null,
        exception
      );
    }
    catch (OllamaProviderException exception)
    {
      return new GuidanceAttempt(
        null,
        exception
      );
    }
  }

  private async Task<ToolingInspection> InspectToolingAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return new ToolingInspection(
        await _ollamaClient.GetModelCapabilitiesAsync(
          baseUri,
          model,
          cancellationToken
        ),
        null
      );
    }
    catch (OllamaProviderException exception)
    {
      _logger.LogWarning(
        exception,
        "Could not inspect tooling capability for model {Model}; the resident bridge will be used.",
        model
      );
      return new ToolingInspection(
        null,
        exception
      );
    }
  }

  private static bool ContainsModel(
    IReadOnlyList<InstalledModel> models,
    string model
  )
  {
    return models.Any(
      installed => string.Equals(
        installed.Name,
        model,
        StringComparison.OrdinalIgnoreCase
      )
    );
  }

  private static string FormatConfidence(
    double? confidence
  )
  {
    return confidence is double value
      ? $"{Math.Round(value * 100, MidpointRounding.AwayFromZero):0}%"
      : "unavailable";
  }

  private static async Task<PlanningAttempt> TryPlanAsync(
    Func<Task<LocalActionProposal?>> action
  )
  {
    try
    {
      return new PlanningAttempt(
        await action(),
        null
      );
    }
    catch (LocalActionException exception)
    {
      return new PlanningAttempt(
        null,
        exception
      );
    }
    catch (OllamaProviderException exception)
    {
      return new PlanningAttempt(
        null,
        exception
      );
    }
  }

  private static ChatStageException ToPlanningChatException(
    Exception exception,
    string? model,
    string? intention
  )
  {
    return exception switch
    {
      LocalActionException localAction => ToChatException(
        localAction,
        model,
        intention
      ),
      OllamaProviderException provider => ToChatException(
        provider,
        model,
        intention
      ),
      _ => throw new InvalidOperationException(
        "Unexpected local action planning exception.",
        exception
      )
    };
  }

  private static async Task<ValidationAttempt> TryValidateAsync(
    Func<Task<ValidatedLocalAction>> action
  )
  {
    try
    {
      return new ValidationAttempt(
        await action(),
        null
      );
    }
    catch (LocalActionException exception)
    {
      return new ValidationAttempt(
        null,
        exception
      );
    }
  }

  private static bool IsReplannableValidation(
    LocalActionException exception
  )
  {
    return string.Equals(
      exception.Stage,
      "action-validation",
      StringComparison.Ordinal
    );
  }

  private static async Task<ExecutionAttempt> TryExecuteAsync(
    Func<Task<LocalActionResult>> action
  )
  {
    try
    {
      return new ExecutionAttempt(
        await action(),
        null
      );
    }
    catch (LocalActionException exception)
    {
      return new ExecutionAttempt(
        null,
        exception
      );
    }
  }

  private static ChatStageException ToChatException(
    OllamaProviderException exception,
    string? model,
    string? intention
  )
  {
    return new ChatStageException(
      exception.Stage,
      exception.Message,
      exception.TechnicalMessage,
      model,
      intention,
      exception.HttpStatus,
      exception.Recoverable,
      exception
    );
  }

  private static ChatStageException ToChatException(
    LocalActionException exception,
    string? model,
    string? intention
  )
  {
    return new ChatStageException(
      exception.Stage,
      exception.Message,
      exception.InnerException?.Message ?? exception.Message,
      model,
      intention,
      400,
      true,
      exception
    );
  }

  private static void ValidateInteractionMode(
    ChatRequest request
  )
  {
    if (
      !string.Equals(
        request.InteractionMode,
        "chat",
        StringComparison.Ordinal
      )
      && !string.Equals(
        request.InteractionMode,
        "execute",
        StringComparison.Ordinal
      )
    )
    {
      throw new ChatStageException(
        "request-validation",
        "Interaction mode must be chat or execute.",
        $"Unsupported interaction mode: {request.InteractionMode}.",
        request.Model,
        null,
        400,
        true
      );
    }

    if (
      !string.Equals(
        request.ApprovalPolicy,
        "ask",
        StringComparison.Ordinal
      )
      && !string.Equals(
        request.ApprovalPolicy,
        "auto",
        StringComparison.Ordinal
      )
    )
    {
      throw new ChatStageException(
        "request-validation",
        "Approval policy must be ask or auto.",
        $"Unsupported approval policy: {request.ApprovalPolicy}.",
        request.Model,
        null,
        400,
        true
      );
    }
  }

  private static ChatStreamEvent ActionEvent(
    string requestId,
    string type,
    string message,
    Stopwatch stopwatch,
    string model,
    string intention,
    ValidatedLocalAction action,
    string state,
    bool requiresApproval
  )
  {
    return new ChatStreamEvent(
      requestId,
      type,
      DateTimeOffset.UtcNow,
      message,
      null,
      model,
      intention,
      stopwatch.ElapsedMilliseconds,
      null,
      null,
      new LocalActionEvent(
        action.ActionId,
        action.Tool,
        action.Summary,
        action.Preview,
        state,
        requiresApproval
      )
    );
  }

  private static ChatMessage ToolResultMessage(
    ValidatedLocalAction action,
    string status,
    string output
  )
  {
    const int limit = 16_000;
    var safeOutput = output.Length <= limit
      ? output
      : $"{output[..limit]}\n[tool result truncated]";

    return new ChatMessage(
      "user",
      $"LOCAL_ACTION_RESULT\nTool: {action.Tool}\nStatus: {status}\nOutput:\n{safeOutput}"
    );
  }

  private static ChatMessage GuidanceMessage(
    string specialistModel,
    string guidance
  )
  {
    return new ChatMessage(
      "user",
      $"{ExpertExecutionGuidanceService.GuidanceMarker}\n"
        + $"Specialist model: {specialistModel}\n"
        + $"Guidance:\n{guidance}"
    );
  }

  private static string FormatActivityOutput(
    ValidatedLocalAction action,
    string output
  )
  {
    const int limit = 8_000;
    var safeOutput = output.Length <= limit
      ? output
      : $"{output[..limit]}\n[activity output truncated]";

    return $"{action.Summary}\n{safeOutput}";
  }

  private static string FormatExecutionFailure(
    LocalActionException exception
  )
  {
    var technical = exception.InnerException?.Message;

    return string.IsNullOrWhiteSpace(
      technical
    ) || string.Equals(
      technical,
      exception.Message,
      StringComparison.Ordinal
    )
      ? exception.Message
      : $"{exception.Message} Details: {technical}";
  }

  private static ChatStreamEvent Event(
    string requestId,
    string type,
    string? message,
    Stopwatch stopwatch,
    string? model = null,
    string? intention = null
  )
  {
    return new ChatStreamEvent(
      requestId,
      type,
      DateTimeOffset.UtcNow,
      message,
      null,
      model,
      intention,
      stopwatch.ElapsedMilliseconds,
      null,
      null
    );
  }

  private sealed class GenerationProgress
  {
    public StringBuilder Answer { get; } = new();

    public bool ReceivedFirstChunk { get; set; }

    public OllamaProviderException? Failure { get; set; }
  }

  private sealed class ExecutionProgress
  {
    public ExecutionProgress(
      List<ChatMessage> messages
    )
    {
      Messages = messages;
    }

    public List<ChatMessage> Messages { get; }

    public ChatStageException? Failure { get; set; }

    public Exception? PlanningFailure { get; set; }
  }

  private sealed record PlanningAttempt(
    LocalActionProposal? Proposal,
    Exception? Failure
  );

  private sealed record ValidationAttempt(
    ValidatedLocalAction? Action,
    LocalActionException? Failure
  );

  private sealed record ExecutionAttempt(
    LocalActionResult? Result,
    LocalActionException? Failure
  );

  private sealed record ToolingInspection(
    OllamaModelCapabilities? Capabilities,
    OllamaProviderException? Failure
  );

  private sealed record GuidanceAttempt(
    string? Guidance,
    Exception? Failure
  );

}
