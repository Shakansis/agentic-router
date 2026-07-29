using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.ProjectAwareness;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Routing;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.WorkspaceProfiles;

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
  private readonly IPlanningFailureClassifier _planningFailureClassifier;
  private readonly IApprovalPolicyService _approvalPolicy;
  private readonly IApprovalCoordinator _approvalCoordinator;
  private readonly IExecutionSessionStore _executionSessions;
  private readonly IProjectAwarenessService _projectAwareness;
  private readonly IRepositoryInstructionService _repositoryInstructions;
  private readonly IExecutionPlanService _executionPlans;
  private readonly IWorkspaceProfileService _workspaceProfiles;
  private readonly ILogger<ChatStreamService> _logger;
  private ExecutionSession? _executionSession;

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
    IPlanningFailureClassifier planningFailureClassifier,
    IApprovalPolicyService approvalPolicy,
    IApprovalCoordinator approvalCoordinator,
    IExecutionSessionStore executionSessions,
    IProjectAwarenessService projectAwareness,
    IRepositoryInstructionService repositoryInstructions,
    IExecutionPlanService executionPlans,
    IWorkspaceProfileService workspaceProfiles,
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
    _planningFailureClassifier = planningFailureClassifier;
    _approvalPolicy = approvalPolicy;
    _approvalCoordinator = approvalCoordinator;
    _executionSessions = executionSessions;
    _projectAwareness = projectAwareness;
    _repositoryInstructions = repositoryInstructions;
    _executionPlans = executionPlans;
    _workspaceProfiles = workspaceProfiles;
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
        yield return Event(
          requestId,
          "project-profile-loading",
          "Loading the bounded project profile.",
          stopwatch,
          selectedModel,
          intention
        );
        var project = await _projectAwareness.GetAsync(
          false,
          cancellationToken
        );
        var rootInstructions = await _repositoryInstructions.ResolveAsync(
          null,
          cancellationToken
        );
        _executionSession = _executionSessions.Begin(
          request.BrowserSessionId
            ?? throw new ChatStageException(
              "execution-session",
              "Execute mode requires a browser session identifier.",
              "The browserSessionId request field was missing.",
              selectedModel,
              intention,
              400,
              true
            ),
          requestId,
          request.Message,
          request.ApprovalPolicy,
          workspace.Path,
          selectedModel,
          selectedModel,
          "resolving",
          settings.Execution
        );
        _executionSession.AttachProject(
          project
        );
        _executionSession.ApplyInstructions(
          rootInstructions
        );
        var activeWorkspace = await _workspaceProfiles.GetActiveDataAsync(
          cancellationToken
        );
        _executionSession.SelectValidationProfile(
          activeWorkspace?.ValidationProfile
            ?? settings.ValidationProfile
        );
        yield return Event(
          requestId,
          "execution.session-started",
          $"Execution session {_executionSession.Id} started.",
          stopwatch,
          selectedModel,
          intention
        );
        yield return Event(
          requestId,
          project.Status == "partial"
            ? "project-profile-partial"
            : "project-profile-loaded",
          $"Project profile: {project.DisplayName ?? "workspace"} · "
            + $"{string.Join(", ", project.ProjectTypes.DefaultIfEmpty("no project type"))}.",
          stopwatch,
          selectedModel,
          intention
        );
        yield return Event(
          requestId,
          "baseline-captured",
          project.Repository.IsGitRepository
            ? $"Git baseline captured on {project.Repository.Branch ?? "detached branch"} with "
              + $"{project.Repository.DirtyPaths.Count} pre-existing dirty paths."
            : "Workspace baseline captured; Git repository was not detected.",
          stopwatch,
          selectedModel,
          intention
        );

        if (project.Repository.DirtyPaths.Count > 0)
        {
          yield return Event(
            requestId,
            "preexisting-change-detected",
            $"Pre-existing changed paths: {string.Join(", ", project.Repository.DirtyPaths)}.",
            stopwatch,
            selectedModel,
            intention
          );
        }

        if (rootInstructions.AppliedFiles.Count > 0)
        {
          yield return Event(
            requestId,
            "repository-instructions-loaded",
            $"Repository instructions loaded: {string.Join(", ", rootInstructions.AppliedFiles)}.",
            stopwatch,
            selectedModel,
            intention
          );
        }

        messages = messages.Prepend(
          new ChatMessage(
            "system",
            CreateProjectContext(
              project,
              rootInstructions
            )
          )
        ).ToArray();

        var tooling = await InspectToolingAsync(
          baseUri,
          selectedModel,
          cancellationToken
        );
        string coordinatorModel;
        List<ChatMessage> executionMessages;
        ExpertExecutionGuidance? executionGuidance = null;
        var targetCoordinatesDirectly =
          tooling.Capabilities?.ToolingConfirmed == true;

        if (targetCoordinatesDirectly)
        {
          yield return Event(
            requestId,
            "agent.tooling-advertised",
            $"Ollama advertises tooling for {selectedModel}; the first valid native tool call will confirm it behaviorally.",
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
            executionGuidance = guidance.Guidance;
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

        _executionSession.ResolveCoordinator(
          coordinatorModel,
          targetCoordinatesDirectly
            ? "direct"
            : "resident-bridge"
        );

        var execution = new ExecutionProgress(
          executionMessages,
          executionGuidance
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
            ? settings.Execution.DirectCoordinatorPlanningFailuresBeforeHandoff
            : settings.Execution.ResidentCoordinatorPlanningFailuresBeforeFailure,
          targetCoordinatesDirectly
            && settings.Execution.MaxCoordinatorHandoffsPerTurn > 0,
          settings.Execution,
          settings.ProjectAwareness,
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
          var residentToolMessages = execution.ToolMessages.ToList();
          ExpertExecutionGuidance? takeoverGuidance = null;

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
            residentToolMessages.Add(
              ToToolMessage(
                GuidanceMessage(
                  selectedModel,
                  guidance.Guidance
                )
              )
            );
            takeoverGuidance = guidance.Guidance;
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
          _executionSession.RecordHandoff(
            settings.RouterModel,
            "resident-takeover"
          );
          execution = new ExecutionProgress(
            residentMessages,
            takeoverGuidance,
            residentToolMessages
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
            settings.Execution.ResidentCoordinatorPlanningFailuresBeforeFailure,
            false,
            settings.Execution,
            settings.ProjectAwareness,
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

        _executionSession.RefreshCompletionGate();
        yield return Event(
          requestId,
          "completion-gate-evaluated",
          $"Completion gate: {_executionSession.CreateSummary().CompletionStatus}.",
          stopwatch,
          selectedModel,
          intention
        );
        execution.Messages.Add(
          new ChatMessage(
            "system",
            ExpertExecutionGuidanceService.FinalResponseInstruction
              + "\n"
              + CreateExecutionFacts(
                _executionSession
              )
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

      _executionSession?.Complete(
        _executionSession.HasWarnings
          ? "completed-with-warnings"
          : "completed"
      );
      var visibleAnswer = _executionSession is null
        ? progress.Answer.ToString()
        : string.Concat(
          progress.Answer,
          "\n\n---\n",
          CreateAuthoritativeStatus(
            _executionSession.CreateSummary().CompletionStatus
          )
        );
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
          visibleAnswer
        ),
        null,
        null,
        _executionSession?.CreateSummary()
      );
    }
    finally
    {
      if (_executionSession?.IsActive == true)
      {
        _executionSession.Complete(
          cancellationToken.IsCancellationRequested
            ? "cancelled"
            : "failed"
        );
      }

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
    ExecutionSettings settings,
    ProjectAwarenessSettings projectAwareness,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    using var sessionCancellation = _executionSession is null
      ? null
      : CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken,
        _executionSession.CancellationToken
      );

    if (sessionCancellation is not null)
    {
      cancellationToken = sessionCancellation.Token;
    }

    var planningFailures = 0;

    for (var index = 0; index < settings.MaxToolCallsPerTurn; index++)
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
      var planHandled = false;
      var proposalCycles = 0;
      var deniedFingerprints = new HashSet<string>(
        StringComparer.Ordinal
      );
      var planningFingerprints = new Dictionary<string, int>(
        StringComparer.Ordinal
      );

      while (
        planningFailures < maximumPlanningAttempts
        && proposalCycles < settings.MaxToolCallsPerTurn
      )
      {
        proposalCycles++;
        var attempt = planningFailures + 1;
        var completionAllowed = CanCompletePlanning(
          progress
        );
        var planning = await TryPlanAsync(
          () => _actionPlanner.PlanAsync(
            baseUri,
            model,
            progress.ToolMessages,
            _executionSession?.Plan is null,
            attempt,
            completionAllowed,
            cancellationToken
          )
        );

        if (planning.Failure is not null)
        {
          if (
            _planningFailureClassifier.Classify(
              planning.Failure
            ) == CoordinatorFailureCategory.Provider
            && planning.Failure is OllamaProviderException providerFailure
          )
          {
            yield return Event(
              requestId,
              "action.provider-error",
              $"The coordinator provider failed while planning: {providerFailure.Message}",
              stopwatch,
              model,
              intention
            );
            progress.Failure = ToChatException(
              providerFailure,
              model,
              intention
            );
            yield break;
          }

          planningFailures++;
          _executionSession?.RecordPlanningFailure();
          exhaustedFailure = planning.Failure;
          var failureFingerprint = string.Concat(
            planning.Failure.GetType().Name,
            ":",
            planning.Failure.Message,
            ":",
            planning.Failure.InnerException?.Message
          );
          planningFingerprints.TryGetValue(
            failureFingerprint,
            out var repeatedCount
          );
          repeatedCount++;
          planningFingerprints[failureFingerprint] = repeatedCount;
          _logger.LogWarning(
            planning.Failure,
            "Local action planning attempt {Attempt} of {MaximumAttempts} failed for request {RequestId}.",
            attempt,
            maximumPlanningAttempts,
            requestId
          );

          if (planningFailures < maximumPlanningAttempts)
          {
            yield return Event(
              requestId,
              "action.planning-retry",
              $"Planning attempt {planningFailures} of {maximumPlanningAttempts} failed: "
                + $"{planning.Failure.Message}"
                + (
                  repeatedCount > 1
                    ? $" The same invalid response was repeated {repeatedCount} times."
                    : string.Empty
                )
                + $" Retrying with attempt {planningFailures + 1} of "
                + $"{maximumPlanningAttempts}.",
              stopwatch,
              model,
              intention
            );
          }

          continue;
        }

        var planningResult = planning.Result!;
        var proposal = planningResult.Proposal;

        if (proposal is null)
        {
          if (
            planningResult.ExplicitNoAction
            && progress.Guidance?.ActionRequired == true
            && !completionAllowed
          )
          {
            var completionFailure = new LocalActionException(
              "local-action-planning",
              "The coordinator declared that no action was required while the structured specialist brief still has pending actions."
            );
            planningFailures++;
            _executionSession?.RecordPlanningFailure();
            exhaustedFailure = completionFailure;
            progress.ToolMessages.Add(
              new OllamaToolMessage(
                "user",
                "EXECUTION_COMPLETION_REJECTED\n"
                  + "The structured specialist brief still requires local actions. "
                  + "Call the next pending tool and do not answer with prose."
              )
            );

            if (planningFailures < maximumPlanningAttempts)
            {
              yield return Event(
                requestId,
                "action.planning-retry",
                $"Planning attempt {planningFailures} of {maximumPlanningAttempts} stopped before pending specialist actions were executed. "
                  + $"Retrying with attempt {planningFailures + 1} of {maximumPlanningAttempts}.",
                stopwatch,
                model,
                intention
              );
            }

            continue;
          }

          _executionSession?.ResetPlanningFailures();
          noActionRequired = true;
          break;
        }

        progress.ToolMessages.Add(
          planningResult.AssistantMessage
        );

        if (!progress.ToolingValidated)
        {
          progress.ToolingValidated = true;
          yield return Event(
            requestId,
            "agent.tooling-validated",
            $"Model {model} returned a valid native tool call; tooling is confirmed for this execution path.",
            stopwatch,
            model,
            intention
          );
        }

        ValidationAttempt validation;

        if (proposal.Tool is "create_execution_plan" or "revise_execution_plan")
        {
          var planFailure = TryApplyExecutionPlan(
            proposal,
            projectAwareness,
            out var plan
          );

          if (planFailure is null)
          {
            planningFailures = 0;
            _executionSession?.ResetPlanningFailures();
            yield return Event(
              requestId,
              proposal.Tool == "create_execution_plan"
                ? "execution-plan-created"
                : "execution-plan-revised",
              proposal.Tool == "create_execution_plan"
                ? $"Execution plan created with {plan!.Steps.Count} steps."
                : $"Execution plan revised; completed and failed steps were preserved.",
              stopwatch,
              model,
              intention
            );
            progress.Messages.Add(
              new ChatMessage(
                "user",
                $"LOCAL_ACTION_RESULT\nTool: {proposal.Tool}\nStatus: completed\n"
                  + $"Output:\nVisible plan accepted with {plan!.Steps.Count} steps. "
                  + "Now propose the first required local action."
              )
            );
            progress.ToolMessages.Add(
              NativeToolResultMessage(
                proposal.Tool,
                "completed",
                $"Visible plan accepted with {plan!.Steps.Count} steps."
              )
            );
            planHandled = true;
            break;
          }

          validation = new ValidationAttempt(
            null,
            planFailure
          );
        }
        else
        {
          var instructionFailure = await ApplyInstructionsForProposalAsync(
            proposal,
            cancellationToken
          );
          validation = instructionFailure is null
            ? await TryValidateAsync(
              () => _actionService.ValidateAsync(
                proposal,
                _executionSession,
                cancellationToken
              )
            )
            : new ValidationAttempt(
              null,
              instructionFailure
            );

          if (
            validation.Failure is null
            && _executionSession?.Plan is null
          )
          {
            validation = new ValidationAttempt(
              null,
              new LocalActionException(
                "execution-plan",
                "Create a valid visible execution plan before proposing a local action."
              )
            );
          }
        }

        if (validation.Failure is null)
        {
          planningFailures = 0;
          _executionSession?.ResetPlanningFailures();
          validatedAction = validation.Action;
          break;
        }

        var exception = validation.Failure;
        progress.ToolMessages.Add(
          NativeToolResultMessage(
            proposal.Tool,
            "rejected",
            exception.Message
          )
        );
        _logger.LogWarning(
          exception,
          "Local action proposal {Tool} failed validation for request {RequestId}.",
          proposal.Tool,
          requestId
        );

        var failureCategory = _planningFailureClassifier.Classify(
          exception
        );

        if (failureCategory == CoordinatorFailureCategory.PolicyDenied)
        {
          _executionSession?.AddWarning(
            $"Policy denied {proposal.Tool}: {exception.Message}"
          );
          var fingerprint = string.Concat(
            proposal.Tool,
            ":",
            proposal.Arguments.GetRawText()
          );

          if (!deniedFingerprints.Add(
            fingerprint
          ))
          {
            yield return Event(
              requestId,
              "action.policy-denied",
              $"{proposal.Tool}: the coordinator repeated an identical denied proposal; execution was blocked.",
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

          yield return Event(
            requestId,
            "action.policy-denied",
            $"{proposal.Tool}: {exception.Message} The coordinator may propose a corrected safe action.",
            stopwatch,
            model,
            intention
          );
          progress.Messages.Add(
            new ChatMessage(
              "user",
              $"LOCAL_ACTION_RESULT\nTool: {proposal.Tool}\nStatus: policy-denied\n"
                + $"Output:\n{exception.Message} Do not repeat the denied proposal; choose a safe alternative."
            )
          );
          continue;
        }

        if (failureCategory != CoordinatorFailureCategory.CorrectablePlanning)
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

        planningFailures++;
        _executionSession?.RecordPlanningFailure();
        exhaustedFailure = exception;

        if (planningFailures < maximumPlanningAttempts)
        {
          yield return Event(
            requestId,
            "action.planning-retry",
            $"Planning attempt {planningFailures} of {maximumPlanningAttempts} produced an invalid action: "
              + $"{exception.Message} Retrying with attempt {planningFailures + 1} of "
              + $"{maximumPlanningAttempts}.",
            stopwatch,
            model,
            intention
          );
        }
      }

      if (planHandled)
      {
        continue;
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
      var appliedInstructions = _executionSession?.CreateReview()
        .AppliedInstructionFiles;

      if (appliedInstructions?.Count > 0)
      {
        yield return Event(
          requestId,
          "repository-instructions-loaded",
          $"Instructions applied to this action: {string.Join(", ", appliedInstructions)}.",
          stopwatch,
          model,
          intention
        );
      }

      _executionSession?.RecordPlanActionStarted(
        action.Tool
      );
      yield return Event(
        requestId,
        "execution-step-started",
        $"Plan step advanced from execution fact: {action.Summary}.",
        stopwatch,
        model,
        intention
      );
      _executionSession?.RecordAction(
        action,
        "proposed"
      );
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
          _executionSession!.BrowserSessionId,
          _executionSession.Id,
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
          progress.ToolMessages.Add(
            NativeToolResultMessage(
              action.Tool,
              "rejected",
              "The user rejected this action. It was not executed."
            )
          );
          _executionSession.RecordAction(
            action,
            "rejected",
            "Rejected by the user."
          );
          _executionSession.RecordPlanActionResult(
            action.Tool,
            "blocked"
          );
          yield return Event(
            requestId,
            "execution-step-blocked",
            $"Plan step blocked because the action was rejected: {action.Summary}.",
            stopwatch,
            model,
            intention
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

      if (action.Tool == "run_validation_profile")
      {
        yield return Event(
          requestId,
          "validation-started",
          "Running the saved validation profile in configured order.",
          stopwatch,
          model,
          intention
        );
      }

      var execution = await TryExecuteAsync(
        () => _actionService.ExecuteAsync(
          action,
          _executionSession,
          cancellationToken
        )
      );

      if (execution.Result?.Succeeded == true)
      {
        var result = execution.Result;

        if (result.Validation is not null)
        {
          foreach (var step in result.Validation.Steps)
          {
            yield return Event(
              requestId,
              "validation-step-started",
              $"Validation step started: {step.Label}.",
              stopwatch,
              model,
              intention
            );
            yield return Event(
              requestId,
              step.Status == "passed"
                ? "validation-step-passed"
                : "validation-step-failed",
              $"{step.Label}: {step.Status} · exit {step.ExitCode?.ToString() ?? "n/a"} · "
                + $"{step.DurationMilliseconds} ms.",
              stopwatch,
              model,
              intention
            );
          }

          yield return Event(
            requestId,
            "validation-completed",
            $"Validation {result.Validation.State}: {result.Validation.ProfileName}.",
            stopwatch,
            model,
            intention
          );
        }
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
        progress.ToolMessages.Add(
          NativeToolResultMessage(
            action.Tool,
            "completed",
            result.Output
          )
        );
        _executionSession?.RecordAction(
          action,
          "completed",
          result.Output
        );
        _executionSession?.RecordToolSuccess();
        _executionSession?.RecordPlanActionResult(
          action.Tool,
          "completed"
        );
        yield return Event(
          requestId,
          "execution-step-completed",
          $"Plan step completed from a verified action result: {action.Summary}.",
          stopwatch,
          model,
          intention
        );
      }
      else
      {
        var exception = execution.Failure ?? new LocalActionException(
          "process-execution",
          "The process returned an unsuccessful result."
        );
        var failureOutput = execution.Result?.Output
          ?? FormatExecutionFailure(
            exception
          );

        if (execution.Result?.Validation is not null)
        {
          foreach (var step in execution.Result.Validation.Steps)
          {
            yield return Event(
              requestId,
              "validation-step-started",
              $"Validation step started: {step.Label}.",
              stopwatch,
              model,
              intention
            );
            yield return Event(
              requestId,
              step.Status == "passed"
                ? "validation-step-passed"
                : "validation-step-failed",
              $"{step.Label}: {step.Status} · exit {step.ExitCode?.ToString() ?? "n/a"} · "
                + $"{step.DurationMilliseconds} ms.",
              stopwatch,
              model,
              intention
            );
          }

          yield return Event(
            requestId,
            "validation-completed",
            $"Validation {execution.Result.Validation.State}: "
              + $"{execution.Result.Validation.ProfileName ?? "not configured"}.",
            stopwatch,
            model,
            intention
          );
        }
        _logger.LogWarning(
          exception,
          "Local action {ActionId} failed for request {RequestId}.",
          action.ActionId,
          requestId
        );

        if (
          exception is LocalActionException localFailure
          && localFailure.Stage == "file-conflict"
        )
        {
          yield return Event(
            requestId,
            "file-conflict-detected",
            failureOutput,
            stopwatch,
            model,
            intention
          );
        }

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
        progress.ToolMessages.Add(
          NativeToolResultMessage(
            action.Tool,
            "failed",
            failureOutput
          )
        );
        _executionSession?.RecordAction(
          action,
          "failed",
          failureOutput
        );
        _executionSession?.RecordToolFailure();
        _executionSession?.AddWarning(
          $"{action.Tool} failed: {failureOutput}"
        );
        var failedProcess = execution.Result?.Process is null
          ? CreateFailedProcessReview(
            action,
            exception,
            execution.DurationMilliseconds,
            _executionSession
          )
          : null;

        if (failedProcess is not null)
        {
          _executionSession?.RecordProcess(
            failedProcess
          );
        }

        if (
          _executionSession is not null
          && _executionSession.CreateSummary().ConsecutiveToolFailureCount
            >= settings.MaxConsecutiveToolFailures
        )
        {
          progress.Failure = new ChatStageException(
            "local-action-failure-limit",
            $"The request reached the limit of {settings.MaxConsecutiveToolFailures} consecutive tool failures.",
            failureOutput,
            model,
            intention,
            400,
            true
          );
          yield break;
        }

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
            var guidanceMessage = GuidanceMessage(
              recoverySpecialistModel,
              guidance.Guidance
            );
            progress.Messages.Add(
              guidanceMessage
            );
            progress.ToolMessages.Add(
              ToToolMessage(
                guidanceMessage
              )
            );
            progress.Guidance = guidance.Guidance;
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
      $"The request reached the limit of {settings.MaxToolCallsPerTurn} local actions.",
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
        null,
        null,
        _executionSession?.CreateSummary()
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
    Func<Task<LocalActionPlanningResult>> action
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

  private static async Task<ExecutionAttempt> TryExecuteAsync(
    Func<Task<LocalActionResult>> action
  )
  {
    var stopwatch = Stopwatch.StartNew();

    try
    {
      return new ExecutionAttempt(
        await action(),
        null,
        stopwatch.ElapsedMilliseconds
      );
    }
    catch (LocalActionException exception)
    {
      return new ExecutionAttempt(
        null,
        exception,
        stopwatch.ElapsedMilliseconds
      );
    }
  }

  private static ExecutionProcessReview? CreateFailedProcessReview(
    ValidatedLocalAction action,
    LocalActionException exception,
    long durationMilliseconds,
    ExecutionSession? executionSession
  )
  {
    if (action.Tool != "run_process")
    {
      return null;
    }

    var arguments = action.Arguments.TryGetProperty(
      "arguments",
      out var argumentElement
    ) && argumentElement.ValueKind == System.Text.Json.JsonValueKind.Array
      ? argumentElement.EnumerateArray()
        .Select(
          item => item.GetString() ?? string.Empty
        ).ToArray()
      : [];
    return new ExecutionProcessReview(
      action.TargetPath ?? string.Empty,
      arguments,
      executionSession is not null
        && action.WorkingDirectory is not null
        ? Path.GetRelativePath(
          executionSession.WorkspacePath,
          action.WorkingDirectory
        )
        : action.WorkingDirectory ?? string.Empty,
      null,
      durationMilliseconds,
      false,
      false,
      false,
      false,
      string.Empty,
      exception.Message
    );
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

  private ChatStreamEvent ActionEvent(
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
        requiresApproval,
        _executionSession?.Id,
        action.PendingFileChange?.UndoAvailable == true,
        action.PendingFileChange?.UndoDiagnostic
      ),
      _executionSession?.CreateSummary()
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

  private static OllamaToolMessage NativeToolResultMessage(
    string tool,
    string status,
    string output
  )
  {
    const int limit = 16_000;
    var safeOutput = output.Length <= limit
      ? output
      : $"{output[..limit]}\n[tool result truncated]";

    return new OllamaToolMessage(
      "tool",
      $"Status: {status}\nOutput:\n{safeOutput}",
      ToolName: tool
    );
  }

  private static ChatMessage GuidanceMessage(
    string specialistModel,
    ExpertExecutionGuidance guidance
  )
  {
    return new ChatMessage(
      "user",
      $"{ExpertExecutionGuidanceService.GuidanceMarker}\n"
        + $"Specialist model: {specialistModel}\n"
        + $"Structured guidance:\n{ExpertExecutionGuidanceService.Serialize(guidance)}"
    );
  }

  private static OllamaToolMessage ToToolMessage(
    ChatMessage message
  )
  {
    return new OllamaToolMessage(
      message.Role,
      message.Content
    );
  }

  private bool CanCompletePlanning(
    ExecutionProgress progress
  )
  {
    if (progress.Guidance?.ActionRequired == false)
    {
      return true;
    }

    var plan = _executionSession?.Plan;

    if (
      plan is null
      || plan.Steps.Count == 0
      || plan.Steps.Any(
        step => step.Status != "completed"
      )
    )
    {
      return false;
    }

    var completedActions = _executionSession!.CompletedActionCount;
    return progress.Guidance is null
      ? completedActions > 0
      : completedActions >= progress.Guidance.Actions.Count;
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

  private ChatStreamEvent Event(
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
      null,
      null,
      _executionSession?.CreateSummary()
    );
  }

  private static string CreateExecutionFacts(
    ExecutionSession session
  )
  {
    var review = session.CreateReview();
    var changedFiles = review.Files.Count == 0
      ? "none"
      : string.Join(
        ", ",
        review.Files.Select(
          file => $"{file.Operation}:{file.RelativePath}"
        )
      );
    var failedProcesses = review.Processes.Count(
      process => process.ExitCode != 0 || process.TimedOut || process.Cancelled
    );
    var commands = review.Processes.Count == 0
      ? "none"
      : string.Join(
        "; ",
        review.Processes.Select(
          process => $"{process.Executable} {string.Join(" ", process.Arguments)}"
        )
      );
    var instructions = review.AppliedInstructionFiles?.Count > 0
      ? string.Join(
        ", ",
        review.AppliedInstructionFiles
      )
      : "none";
    var preExisting = review.Files.Where(
      file => file.PreExistingChange
    ).Select(
      file => file.RelativePath
    ).ToArray();

    return "AUTHORITATIVE_EXECUTION_SESSION_FACTS\n"
      + $"Session: {review.Summary.Id}\n"
      + $"Coordinator: {review.Summary.CoordinatorModel}\n"
      + $"Execution path: {review.Summary.ExecutionPath}\n"
      + $"Actions: {review.Summary.ActionCount}\n"
      + $"Changed files: {changedFiles}\n"
      + $"Pre-existing changed paths touched by this session: {string.Join(", ", preExisting.DefaultIfEmpty("none"))}\n"
      + $"Commands actually run: {commands}\n"
      + $"Applied instruction files: {instructions}\n"
      + $"Validation: {review.Validation?.State ?? "not-run"}\n"
      + $"Completion gate: {review.Summary.CompletionStatus}\n"
      + $"Process failures: {failedProcesses}\n"
      + "Base the final summary only on these session facts and tool results. "
      + "Do not claim implemented and validated unless completion gate says implemented-and-validated.";
  }

  private async Task<LocalActionException?> ApplyInstructionsForProposalAsync(
    LocalActionProposal proposal,
    CancellationToken cancellationToken
  )
  {
    if (
      !proposal.Arguments.TryGetProperty(
        "path",
        out var pathElement
      )
      || pathElement.ValueKind != System.Text.Json.JsonValueKind.String
    )
    {
      return null;
    }

    try
    {
      var instructions = await _repositoryInstructions.ResolveAsync(
        pathElement.GetString(),
        cancellationToken
      );
      _executionSession?.ApplyInstructions(
        instructions
      );
      return null;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
      or LocalActionException
    )
    {
      return new LocalActionException(
        "repository-instructions",
        $"Repository instructions could not be resolved for the proposed path: {exception.Message}",
        exception
      );
    }
  }

  private LocalActionException? TryApplyExecutionPlan(
    LocalActionProposal proposal,
    ProjectAwarenessSettings projectAwareness,
    out ExecutionPlanView? plan
  )
  {
    try
    {
      var current = _executionSession?.Plan;

      if (proposal.Tool == "create_execution_plan")
      {
        if (current is not null)
        {
          throw new LocalActionException(
            "execution-plan",
            "Use revise_execution_plan because this session already has a plan."
          );
        }

        plan = _executionPlans.ValidateCreate(
          proposal.Arguments,
          projectAwareness.MaxPlanSteps
        );
        _executionSession?.CreatePlan(
          plan
        );
        return null;
      }

      if (current is null)
      {
        throw new LocalActionException(
          "execution-plan",
          "Create an execution plan before revising it."
        );
      }

      if (current.RevisionCount >= projectAwareness.MaxPlanRevisions)
      {
        throw new LocalActionException(
          "execution-plan",
          $"The execution plan revision limit of {projectAwareness.MaxPlanRevisions} was reached."
        );
      }

      plan = _executionPlans.ValidateRevision(
        proposal.Arguments,
        current,
        projectAwareness.MaxPlanSteps
      );
      _executionSession?.RevisePlan(
        plan
      );
      return null;
    }
    catch (LocalActionException failure)
    {
      plan = null;
      return failure;
    }
  }

  private static string CreateProjectContext(
    ProjectProfile project,
    RepositoryInstructionSet instructions
  )
  {
    var markers = project.DetectedFiles.Count == 0
      ? "none"
      : string.Join(
        ", ",
        project.DetectedFiles
      );
    var dirty = project.Repository.DirtyPaths.Count == 0
      ? "none"
      : string.Join(
        ", ",
        project.Repository.DirtyPaths
      );
    return "APPLICATION_OWNED_PROJECT_CONTEXT\n"
      + $"Workspace display name: {project.DisplayName ?? "workspace"}\n"
      + $"Project types: {string.Join(", ", project.ProjectTypes.DefaultIfEmpty("none"))}\n"
      + $"Detected markers: {markers}\n"
      + $"Git branch: {project.Repository.Branch ?? "unavailable"}\n"
      + $"Pre-existing dirty paths: {dirty}\n"
      + "Pre-existing dirty files must not be claimed as changes made solely by this turn.\n"
      + "Existing files must be inspected before modification and may conflict if their hash changes.\n"
      + (
        string.IsNullOrWhiteSpace(
          instructions.Content
        )
          ? "No repository AGENTS.md instructions were loaded."
          : instructions.Content
      );
  }

  private static string CreateAuthoritativeStatus(
    string completionStatus
  )
  {
    var text = completionStatus switch
    {
      "implemented-and-validated" => "Implemented and validated.",
      "implemented-and-validated-with-warnings" => "Implemented and validated with warnings.",
      "implemented-validation-failed" => "Implemented; validation failed.",
      "implemented-validation-cancelled" => "Implemented; validation was cancelled.",
      "implemented-validation-not-configured" => "Implemented; no validation profile is configured.",
      "implemented-validation-not-run" => "Implemented; validation was not run.",
      "validation-passed-no-files-changed" => "Validation passed; no files were changed.",
      "blocked-validation-not-configured" => "Validation was requested, but no validation profile is configured.",
      "blocked-validation-not-run" => "Validation was requested, but it did not run.",
      _ => "Inspected only; no files were changed."
    };
    return $"**Authoritative execution status:** {text}";
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
      List<ChatMessage> messages,
      ExpertExecutionGuidance? guidance = null,
      List<OllamaToolMessage>? toolMessages = null
    )
    {
      Messages = messages;
      Guidance = guidance;
      ToolMessages = toolMessages ?? messages.Select(
        ToToolMessage
      ).ToList();
    }

    public List<ChatMessage> Messages { get; }

    public List<OllamaToolMessage> ToolMessages { get; }

    public ExpertExecutionGuidance? Guidance { get; set; }

    public bool ToolingValidated { get; set; }

    public ChatStageException? Failure { get; set; }

    public Exception? PlanningFailure { get; set; }
  }

  private sealed record PlanningAttempt(
    LocalActionPlanningResult? Result,
    Exception? Failure
  );

  private sealed record ValidationAttempt(
    ValidatedLocalAction? Action,
    LocalActionException? Failure
  );

  private sealed record ExecutionAttempt(
    LocalActionResult? Result,
    LocalActionException? Failure,
    long DurationMilliseconds
  );

  private sealed record ToolingInspection(
    OllamaModelCapabilities? Capabilities,
    OllamaProviderException? Failure
  );

  private sealed record GuidanceAttempt(
    ExpertExecutionGuidance? Guidance,
    Exception? Failure
  );

}
