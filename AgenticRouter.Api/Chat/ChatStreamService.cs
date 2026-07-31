using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.ProjectAwareness;
using AgenticRouter.Api.Providers;
using AgenticRouter.Api.Providers.Ollama;
using AgenticRouter.Api.Routing;
using AgenticRouter.Api.Runtime;
using AgenticRouter.Api.Usage;
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
  private readonly IToolProtocolConformanceService _toolConformance;
  private readonly IApprovalPolicyService _approvalPolicy;
  private readonly IApprovalCoordinator _approvalCoordinator;
  private readonly IRecoveryDecisionCoordinator _recoveryDecisions;
  private readonly IExecutionSessionStore _executionSessions;
  private readonly IProjectAwarenessService _projectAwareness;
  private readonly IRepositoryInstructionService _repositoryInstructions;
  private readonly IExecutionPlanService _executionPlans;
  private readonly IWorkspaceProfileService _workspaceProfiles;
  private readonly IImageAttachmentValidator _imageValidator;
  private readonly ICloudImageApprovalStore _cloudImageApprovals;
  private readonly ILogger<ChatStreamService> _logger;
  private ExecutionSession? _executionSession;
  private string? _usageWorkspaceId;
  private string? _usageConversationId;
  private string? _usageTurnId;
  private IReadOnlyDictionary<string, string?> _usageModelRevisions =
    new Dictionary<string, string?>(
      StringComparer.OrdinalIgnoreCase
    );

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
    IToolProtocolConformanceService toolConformance,
    IApprovalPolicyService approvalPolicy,
    IApprovalCoordinator approvalCoordinator,
    IRecoveryDecisionCoordinator recoveryDecisions,
    IExecutionSessionStore executionSessions,
    IProjectAwarenessService projectAwareness,
    IRepositoryInstructionService repositoryInstructions,
    IExecutionPlanService executionPlans,
    IWorkspaceProfileService workspaceProfiles,
    IImageAttachmentValidator imageValidator,
    ICloudImageApprovalStore cloudImageApprovals,
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
    _toolConformance = toolConformance;
    _approvalPolicy = approvalPolicy;
    _approvalCoordinator = approvalCoordinator;
    _recoveryDecisions = recoveryDecisions;
    _executionSessions = executionSessions;
    _projectAwareness = projectAwareness;
    _repositoryInstructions = repositoryInstructions;
    _executionPlans = executionPlans;
    _workspaceProfiles = workspaceProfiles;
    _imageValidator = imageValidator;
    _cloudImageApprovals = cloudImageApprovals;
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
      var usageWorkspace = await _workspaceProfiles.GetActiveDataAsync(
        cancellationToken
      );
      _usageWorkspaceId = usageWorkspace?.Id;
      _usageConversationId = request.ConversationSessionId;
      _usageTurnId = requestId;
      _usageModelRevisions = models.ToDictionary(
        model => model.Name,
        model => model.Digest,
        StringComparer.OrdinalIgnoreCase
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
      var selectedModelRole = UsageModelRoles.Primary;
      var images = _imageValidator.Validate(
        request.Images
      );

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
        selectedModelRole = resolution.Attempts.LastOrDefault(
          attempt => attempt.Installed
        )?.Source == "intention fallback"
          ? UsageModelRoles.Fallback
          : UsageModelRoles.Primary;
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
      var capabilityResolution = await ResolveTurnCapabilitiesAsync(
        baseUri,
        selectedModel,
        request.WebSearchEnabled || images.Count > 0,
        cancellationToken
      );
      var capabilities = capabilityResolution.Capabilities;

      if (capabilityResolution.Warning is not null)
      {
        yield return Event(
          requestId,
          "target.capabilities-unverified",
          "Optional capabilities could not be verified; continuing with conservative text-only capability state.",
          stopwatch,
          selectedModel,
          isAuto
            ? intention
            : null
        );
      }

      ValidateRequestedCapabilities(
        request,
        selectedModel,
        capabilities,
        images
      );
      var chatOptions = new ProviderChatOptions(
        request.WebSearchEnabled,
        images
      );
      yield return Event(
        requestId,
        "target.capabilities-resolved",
        $"Capabilities from {capabilities.Source}: "
          + string.Join(
            ", ",
            CapabilityLabels(
              capabilities
            )
          )
          + ".",
        stopwatch,
        selectedModel,
        isAuto
          ? intention
          : null
      );

      if (request.WebSearchEnabled)
      {
        yield return Event(
          requestId,
          "web.search-enabled",
          capabilities.ProviderNativeWebSearch
            ? "Provider-native web search explicitly enabled for this request."
            : "Application-mediated Ollama Web Search explicitly enabled for this request.",
          stopwatch,
          selectedModel,
          isAuto
            ? intention
            : null
        );
      }

      if (images.Count > 0)
      {
        yield return Event(
          requestId,
          "image.attachments-validated",
          $"{images.Count} image attachment(s), {images.Sum(image => image.Bytes.LongLength)} bytes, validated for {selectedModel}.",
          stopwatch,
          selectedModel,
          isAuto
            ? intention
            : null
        );
      }
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
      var contextUsage = CreateContextUsage(
        context,
        capabilities,
        settings,
        null
      );
      yield return new ChatStreamEvent(
        requestId,
        "context.usage",
        DateTimeOffset.UtcNow,
        null,
        null,
        selectedModel,
        isAuto
          ? intention
          : null,
        stopwatch.ElapsedMilliseconds,
        null,
        null,
        ContextUsage: contextUsage
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

        var configuredCoordinatorReference = ProviderModelReference.Parse(
          settings.CoordinatorModel
        );

        if (
          configuredCoordinatorReference.IsLocal
          && !string.Equals(
          settings.CoordinatorModel,
          settings.RouterModel,
          StringComparison.OrdinalIgnoreCase
          )
        )
        {
          yield return Event(
            requestId,
            "resident-model-eviction-started",
            $"Evicting resident router model {settings.RouterModel} before coordinator conformance.",
            stopwatch,
            settings.RouterModel,
            intention
          );
          recoveryActive = await _residentModel.EvictForRecoveryAsync(
            settings.CoordinatorModel,
            cancellationToken
          );
          recoveryTarget = settings.CoordinatorModel;

          if (recoveryActive)
          {
            yield return Event(
              requestId,
              "resident-model-evicted",
              $"Resident router model {settings.RouterModel} was temporarily evicted for coordinator conformance.",
              stopwatch,
              settings.RouterModel,
              intention
            );
          }
        }

        var tooling = await InspectToolingAsync(
          baseUri,
          selectedModel,
          cancellationToken
        );
        if (!ContainsModel(
          models,
          settings.CoordinatorModel
        ))
        {
          throw new ChatStageException(
            "coordinator-conformance",
            "The configured tooling coordinator is unavailable.",
            $"Configured coordinator model '{settings.CoordinatorModel}' was not present in the installed model list.",
            settings.CoordinatorModel,
            intention,
            400,
            true
          );
        }

        var coordinatorTooling = string.Equals(
          settings.CoordinatorModel,
          selectedModel,
          StringComparison.OrdinalIgnoreCase
        )
          ? tooling
          : await InspectToolingAsync(
            baseUri,
            settings.CoordinatorModel,
            cancellationToken
          );
        ToolProtocolConformanceResult? coordinatorConformance = null;

        if (coordinatorTooling.Capabilities?.ToolingConfirmed == true)
        {
          var coordinatorIdentity = models.First(
            installed => string.Equals(
              installed.Name,
              settings.CoordinatorModel,
              StringComparison.OrdinalIgnoreCase
            )
          );
          coordinatorConformance = configuredCoordinatorReference.IsLocal
            ? await _toolConformance.VerifyAsync(
              baseUri,
              settings.CoordinatorModel,
              coordinatorIdentity.Digest,
              UsageContext(
                settings.CoordinatorModel,
                UsageModelRoles.Benchmark,
                "tool-protocol-conformance"
              ),
              cancellationToken
            )
            : await _toolConformance.GetCachedAsync(
              baseUri,
              settings.CoordinatorModel,
              coordinatorIdentity.Digest,
              cancellationToken
            );
        }

        if (coordinatorConformance?.Passed != true)
        {
          var coordinatorFailure = coordinatorConformance?.Failure
            ?? coordinatorTooling.Failure?.Message
            ?? "Ollama does not advertise native tool support for this model.";
          yield return Event(
            requestId,
            "agent.coordinator-conformance-failed",
            $"Tooling coordinator {settings.CoordinatorModel} did not pass protocol conformance: {coordinatorFailure}",
            stopwatch,
            settings.CoordinatorModel,
            intention
          );
          throw new ChatStageException(
            "coordinator-conformance",
            "The configured tooling coordinator did not pass protocol conformance.",
            coordinatorFailure,
            settings.CoordinatorModel,
            intention,
            null,
            true
          );
        }

        yield return Event(
          requestId,
          "agent.coordinator-conformance-passed",
          $"Tooling coordinator {settings.CoordinatorModel} passed protocol conformance with "
            + $"Ollama {coordinatorConformance.OllamaVersion} and digest {coordinatorConformance.Digest}.",
          stopwatch,
          settings.CoordinatorModel,
          intention
        );
        string coordinatorModel;
        List<ChatMessage> executionMessages;
        ExpertExecutionGuidance? executionGuidance = null;
        var toolingAdvertised =
          tooling.Capabilities?.ToolingConfirmed == true;
        var targetCoordinatesDirectly = false;

        if (toolingAdvertised)
        {
          yield return Event(
            requestId,
            "agent.tooling-advertised",
            $"Ollama advertises tooling for {selectedModel}; behavioral conformance must pass before direct coordination.",
            stopwatch,
            selectedModel,
            intention
          );
          yield return Event(
            requestId,
            "agent.tooling-conformance-started",
            $"Running native tool protocol conformance for {selectedModel}.",
            stopwatch,
            selectedModel,
            intention
          );
          var selectedIdentity = models.First(
            installed => string.Equals(
              installed.Name,
              selectedModel,
              StringComparison.OrdinalIgnoreCase
            )
          );
          var selectedReference = ProviderModelReference.Parse(
            selectedModel
          );
          var conformance = selectedReference.IsLocal
            ? await _toolConformance.VerifyAsync(
              baseUri,
              selectedModel,
              selectedIdentity.Digest,
              UsageContext(
                selectedModel,
                UsageModelRoles.Benchmark,
                "tool-protocol-conformance"
              ),
              cancellationToken
            )
            : await _toolConformance.GetCachedAsync(
              baseUri,
              selectedModel,
              selectedIdentity.Digest,
              cancellationToken
            );
          targetCoordinatesDirectly = conformance?.Passed == true;
          yield return Event(
            requestId,
            conformance?.Passed == true
              ? "agent.tooling-conformance-passed"
              : "agent.tooling-conformance-failed",
            conformance?.Passed == true
              ? $"Tool protocol conformance passed for {selectedModel} with Ollama {conformance.OllamaVersion} and digest {conformance.Digest}."
              : $"Tool protocol conformance is unavailable for {selectedModel}: {conformance?.Failure ?? "explicit cloud benchmark permission is required"} "
                + "The configured coordinator will bridge this turn.",
            stopwatch,
            selectedModel,
            intention
          );
        }

        if (targetCoordinatesDirectly)
        {
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
            settings.CoordinatorModel
          ))
          {
            yield return Event(
              requestId,
              "action.validation-error",
              $"Tooling coordinator '{settings.CoordinatorModel}' is not installed.",
              stopwatch,
              settings.CoordinatorModel,
              intention
            );
            throw new ChatStageException(
              "resident-agent-resolution",
              "The tooling coordinator is unavailable.",
              $"Configured coordinator model '{settings.CoordinatorModel}' was not present in the installed model list.",
              settings.CoordinatorModel,
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
            $"Tooling coordinator: {settings.CoordinatorModel}.",
            stopwatch,
            settings.CoordinatorModel,
            intention
          );
          coordinatorModel = settings.CoordinatorModel;
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
            settings.CoordinatorModel
          ))
          {
            yield return Event(
              requestId,
              "action.validation-error",
              $"Tooling coordinator '{settings.CoordinatorModel}' is not installed.",
              stopwatch,
              settings.CoordinatorModel,
              intention
            );
            throw new ChatStageException(
              "resident-agent-resolution",
              "The tooling coordinator is unavailable.",
              $"Configured coordinator model '{settings.CoordinatorModel}' was not present in the installed model list.",
              settings.CoordinatorModel,
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
            $"Tooling coordinator {settings.CoordinatorModel} took over with a reset planning error counter.",
            stopwatch,
            settings.CoordinatorModel,
            intention
          );
          _executionSession.RecordHandoff(
            settings.CoordinatorModel,
            "coordinator-takeover"
          );
          var recoveryAttemptCount = execution.RecoveryAttemptCount;
          execution = new ExecutionProgress(
            residentMessages,
            takeoverGuidance,
            residentToolMessages
          )
          {
            RecoveryAttemptCount = recoveryAttemptCount
          };

          await foreach (var streamEvent in ExecuteActionsAsync(
            baseUri,
            settings.CoordinatorModel,
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

      if (recoveryActive)
      {
        recoveryTarget = selectedModel;
      }

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
        selectedModelRole,
        chatOptions,
        cancellationToken
      ))
      {
        yield return streamEvent;
      }

      if (
        progress.Failure is RoutedProviderException cloudFailure
        && !progress.ReceivedFirstChunk
        && isAuto
        && CanUseLocalFallback(
          cloudFailure
        )
      )
      {
        var localFallback = ResolveLocalFallback(
          settings,
          intention,
          models
        );

        if (localFallback is not null)
        {
          var fallbackCapabilityResolution = await ResolveTurnCapabilitiesAsync(
            baseUri,
            localFallback,
            request.WebSearchEnabled || images.Count > 0,
            cancellationToken
          );
          var fallbackCapabilities = fallbackCapabilityResolution.Capabilities;
          capabilities = fallbackCapabilities;

          if (fallbackCapabilityResolution.Warning is not null)
          {
            yield return Event(
              requestId,
              "cloud.local-fallback-capabilities-unverified",
              $"Optional capabilities for local fallback {localFallback} could not be verified; using conservative text-only state.",
              stopwatch,
              localFallback,
              intention
            );
          }

          if (
            images.Count > 0
            && !fallbackCapabilities.Vision
            || request.WebSearchEnabled
            && !fallbackCapabilities.WebSearch
          )
          {
            yield return Event(
              requestId,
              "cloud.local-fallback-incompatible",
              images.Count > 0
                ? $"Local fallback {localFallback} is text-only; image attachments were not stripped."
                : $"Local fallback {localFallback} cannot perform the explicitly enabled web search.",
              stopwatch,
              localFallback,
              intention
            );
          }
          else
          {
            yield return Event(
              requestId,
              "cloud.local-fallback-started",
              $"{ModelProviderIds.DisplayName(cloudFailure.Provider)} could not complete the request "
                + $"({cloudFailure.Code}); switching once to Ollama Local Â· {localFallback}.",
              stopwatch,
              localFallback,
              intention
            );
            selectedModel = localFallback;
            selectedModelRole = UsageModelRoles.Fallback;
            progress.Failure = null;

            await foreach (var streamEvent in StreamAttemptAsync(
              baseUri,
              selectedModel,
              messages,
              requestId,
              intention,
              stopwatch,
              progress,
              selectedModelRole,
              chatOptions,
              cancellationToken
            ))
            {
              yield return streamEvent;
            }

            if (progress.Failure is null)
            {
              yield return Event(
                requestId,
                "cloud.local-fallback-completed",
                $"Ollama Local Â· {localFallback} completed the cloud fallback.",
                stopwatch,
                localFallback,
                intention
              );
            }
          }
        }
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
          selectedModelRole,
          chatOptions,
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

      if (recoveryActive && recoveryTarget is not null)
      {
        yield return Event(
          requestId,
          "resident-model-reload-started",
          $"Reloading resident router model {settings.RouterModel}.",
          stopwatch,
          settings.RouterModel,
          intention
        );
        var restored = await _residentModel.RestoreAfterRecoveryAsync(
          recoveryTarget,
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
      contextUsage = CreateContextUsage(
        context,
        capabilities,
        settings,
        progress.Usage
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
        _executionSession?.CreateSummary(),
        Citations: progress.Citations,
        ContextUsage: contextUsage
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
    var deniedFingerprints = new HashSet<string>(
      StringComparer.Ordinal
    );

    var actionBudget = 0;

    while (true)
    {
      if (actionBudget >= settings.MaxToolCallsPerTurn)
      {
        var checkpoint = CreateRecoveryCheckpoint(
          requestId,
          stopwatch,
          model,
          intention,
          $"The execution used its bounded allowance of {settings.MaxToolCallsPerTurn} local actions before all planned work was completed.",
          recoverySpecialistModel,
          cancellationToken
        );
        yield return checkpoint.Event;
        var resolution = await ResolveRecoveryDecisionAsync(
          checkpoint,
          baseUri,
          requestId,
          stopwatch,
          model,
          intention,
          progress,
          recoverySpecialistModel,
          cancellationToken
        );

        foreach (var recoveryEvent in resolution.Events)
        {
          yield return recoveryEvent;
        }

        if (!resolution.ContinueExecution)
        {
          yield break;
        }

        actionBudget = 0;
      }

      actionBudget++;
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
            UsageContext(
              model,
              UsageModelRoles.Coordinator,
              "local-action-planning"
            ),
            cancellationToken
          )
        );

        if (planning.Failure is not null)
        {
          var planningFailureCategory = _planningFailureClassifier.Classify(
            planning.Failure
          );

          if (planning.Failure is ToolProtocolException protocolFailure)
          {
            _executionSession?.RecordPlanningFailure();
            _logger.LogWarning(
              protocolFailure,
              "Native tool protocol failed for coordinator {Model} on request {RequestId}; identical automatic retries are disabled.",
              model,
              requestId
            );
            yield return Event(
              requestId,
              "action.tool-protocol-error",
              $"Model {model} returned a syntactically invalid native tool call. "
                + "The same prompt and schema will not be retried automatically.",
              stopwatch,
              model,
              intention
            );

            if (fallbackToResident)
            {
              yield return Event(
                requestId,
                "agent.tooling-fallback",
                $"Native tool coordination for {model} is disabled for this turn after its first protocol failure; "
                  + "the configured coordinator will take over with a different execution path.",
                stopwatch,
                model,
                intention
              );
              progress.PlanningFailure = protocolFailure;
              yield break;
            }

            var checkpoint = CreateRecoveryCheckpoint(
              requestId,
              stopwatch,
              model,
              intention,
              protocolFailure.TechnicalMessage,
              recoverySpecialistModel,
              cancellationToken
            );
            yield return checkpoint.Event;
            var resolution = await ResolveRecoveryDecisionAsync(
              checkpoint,
              baseUri,
              requestId,
              stopwatch,
              model,
              intention,
              progress,
              recoverySpecialistModel,
              cancellationToken
            );

            foreach (var recoveryEvent in resolution.Events)
            {
              yield return recoveryEvent;
            }

            if (!resolution.ContinueExecution)
            {
              yield break;
            }

            planningFailures = 0;
            exhaustedFailure = null;
            planningFingerprints.Clear();
            continue;
          }

          if (
            planningFailureCategory == CoordinatorFailureCategory.Provider
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
          var recoveryLimitFailure = RecordRecoveryAttempt(
            progress,
            settings,
            planning.Failure.InnerException?.Message
              ?? planning.Failure.Message,
            model,
            intention
          );

          if (recoveryLimitFailure is not null)
          {
            var checkpoint = CreateRecoveryCheckpoint(
              requestId,
              stopwatch,
              model,
              intention,
              recoveryLimitFailure.TechnicalMessage,
              recoverySpecialistModel,
              cancellationToken
            );
            yield return checkpoint.Event;
            var resolution = await ResolveRecoveryDecisionAsync(
              checkpoint,
              baseUri,
              requestId,
              stopwatch,
              model,
              intention,
              progress,
              recoverySpecialistModel,
              cancellationToken
            );

            foreach (var recoveryEvent in resolution.Events)
            {
              yield return recoveryEvent;
            }

            if (!resolution.ContinueExecution)
            {
              yield break;
            }

            planningFailures = 0;
            exhaustedFailure = null;
            planningFingerprints.Clear();
            continue;
          }

          if (
            planningFailureCategory == CoordinatorFailureCategory.CorrectablePlanning
            && !completionAllowed
            && planningFailures < maximumPlanningAttempts
          )
          {
            progress.ToolMessages.Add(
              CreateCompletionRejectedMessage()
            );
          }

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
                + $" Recovery budget: {progress.RecoveryAttemptCount}/"
                + $"{settings.MaxRecoveryAttemptsPerTurn}."
                + $" Retrying with attempt {planningFailures + 1} of "
                + $"{maximumPlanningAttempts}.",
              stopwatch,
              model,
              intention
            );
            await DelayBeforeRecoveryRetryAsync(
              progress.RecoveryAttemptCount,
              cancellationToken
            );
          }

          continue;
        }

        var planningResult = planning.Result!;
        var proposal = planningResult.Proposal;

        if (planningResult.IgnoredToolCallCount > 0)
        {
          yield return Event(
            requestId,
            "action.planning-normalized",
            $"The coordinator returned {planningResult.IgnoredToolCallCount + 1} native tool calls; "
              + "only the first call was retained for validation and the remaining calls were ignored.",
            stopwatch,
            model,
            intention
          );
        }

        if (proposal is null)
        {
          if (
            planningResult.ExplicitNoAction
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
            var recoveryLimitFailure = RecordRecoveryAttempt(
              progress,
              settings,
              completionFailure.Message,
              model,
              intention
            );

            if (recoveryLimitFailure is not null)
            {
              var checkpoint = CreateRecoveryCheckpoint(
                requestId,
                stopwatch,
                model,
                intention,
                recoveryLimitFailure.TechnicalMessage,
                recoverySpecialistModel,
                cancellationToken
              );
              yield return checkpoint.Event;
              var resolution = await ResolveRecoveryDecisionAsync(
                checkpoint,
                baseUri,
                requestId,
                stopwatch,
                model,
                intention,
                progress,
                recoverySpecialistModel,
                cancellationToken
              );

              foreach (var recoveryEvent in resolution.Events)
              {
                yield return recoveryEvent;
              }

              if (!resolution.ContinueExecution)
              {
                yield break;
              }

              planningFailures = 0;
              exhaustedFailure = null;
              planningFingerprints.Clear();
              continue;
            }

            progress.ToolMessages.Add(
              CreateCompletionRejectedMessage()
            );

            if (planningFailures < maximumPlanningAttempts)
            {
              yield return Event(
                requestId,
                "action.planning-retry",
                $"Planning attempt {planningFailures} of {maximumPlanningAttempts} stopped before pending specialist actions were executed. "
                  + $"Recovery budget: {progress.RecoveryAttemptCount}/"
                  + $"{settings.MaxRecoveryAttemptsPerTurn}. "
                  + $"Retrying with attempt {planningFailures + 1} of {maximumPlanningAttempts}.",
                stopwatch,
                model,
                intention
              );
              await DelayBeforeRecoveryRetryAsync(
                progress.RecoveryAttemptCount,
                cancellationToken
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
            progress.RecoveryAttemptCount = 0;
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

        if (
          failureCategory is CoordinatorFailureCategory.PolicyDenied
            or CoordinatorFailureCategory.ToolExecution
          || (
            failureCategory == CoordinatorFailureCategory.SecurityDenied
            && exception.Stage == "path-validation"
          )
        )
        {
          _executionSession?.AddWarning(
            $"Action denied {proposal.Tool}: {exception.Message}"
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
            var repeatedDenial =
              $"{proposal.Tool}: the coordinator repeated an identical denied proposal; execution remains blocked.";
            yield return Event(
              requestId,
              failureCategory switch
              {
                CoordinatorFailureCategory.SecurityDenied => "action.security-denied",
                CoordinatorFailureCategory.ToolExecution => "action.validation-error",
                _ => "action.policy-denied"
              },
              repeatedDenial,
              stopwatch,
              model,
              intention
            );
            var denialMessage = new ChatMessage(
              "user",
              $"LOCAL_ACTION_RESULT\nTool: {proposal.Tool}\nStatus: policy-denied\n"
                + $"Output:\n{exception.Message} The repeated action was not executed. "
                + "Choose a materially different safe alternative."
            );
            progress.Messages.Add(
              denialMessage
            );
            progress.ToolMessages.Add(
              ToToolMessage(
                denialMessage
              )
            );
            var checkpoint = CreateRecoveryCheckpoint(
              requestId,
              stopwatch,
              model,
              intention,
              repeatedDenial,
              recoverySpecialistModel,
              cancellationToken
            );
            yield return checkpoint.Event;
            var resolution = await ResolveRecoveryDecisionAsync(
              checkpoint,
              baseUri,
              requestId,
              stopwatch,
              model,
              intention,
              progress,
              recoverySpecialistModel,
              cancellationToken
            );

            foreach (var recoveryEvent in resolution.Events)
            {
              yield return recoveryEvent;
            }

            if (!resolution.ContinueExecution)
            {
              yield break;
            }

            planningFailures = 0;
            exhaustedFailure = null;
            planningFingerprints.Clear();
            continue;
          }

          var recoveryLimitFailure = RecordRecoveryAttempt(
            progress,
            settings,
            exception.Message,
            model,
            intention
          );

          if (recoveryLimitFailure is not null)
          {
            var checkpoint = CreateRecoveryCheckpoint(
              requestId,
              stopwatch,
              model,
              intention,
              recoveryLimitFailure.TechnicalMessage,
              recoverySpecialistModel,
              cancellationToken
            );
            yield return checkpoint.Event;
            var resolution = await ResolveRecoveryDecisionAsync(
              checkpoint,
              baseUri,
              requestId,
              stopwatch,
              model,
              intention,
              progress,
              recoverySpecialistModel,
              cancellationToken
            );

            foreach (var recoveryEvent in resolution.Events)
            {
              yield return recoveryEvent;
            }

            if (!resolution.ContinueExecution)
            {
              yield break;
            }

            planningFailures = 0;
            exhaustedFailure = null;
            planningFingerprints.Clear();
            continue;
          }

          yield return Event(
            requestId,
            failureCategory switch
            {
              CoordinatorFailureCategory.SecurityDenied => "action.security-denied",
              CoordinatorFailureCategory.ToolExecution => "action.validation-error",
              _ => "action.policy-denied"
            },
            $"{proposal.Tool}: {exception.Message} The action was not permitted and was not executed. "
              + $"The coordinator may propose a corrected safe action. Recovery budget: "
              + $"{progress.RecoveryAttemptCount}/{settings.MaxRecoveryAttemptsPerTurn}.",
            stopwatch,
            model,
            intention
          );
          progress.Messages.Add(
            new ChatMessage(
              "user",
              $"LOCAL_ACTION_RESULT\nTool: {proposal.Tool}\nStatus: policy-denied\n"
                + $"Output:\n{exception.Message} The action was not executed. "
                + "Do not repeat the denied proposal; choose a safe alternative inside the trusted workspace."
            )
          );
          await DelayBeforeRecoveryRetryAsync(
            progress.RecoveryAttemptCount,
            cancellationToken
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
        var planningRecoveryLimitFailure = RecordRecoveryAttempt(
          progress,
          settings,
          exception.Message,
          model,
          intention
        );

        if (planningRecoveryLimitFailure is not null)
        {
          var checkpoint = CreateRecoveryCheckpoint(
            requestId,
            stopwatch,
            model,
            intention,
            planningRecoveryLimitFailure.TechnicalMessage,
            recoverySpecialistModel,
            cancellationToken
          );
          yield return checkpoint.Event;
          var resolution = await ResolveRecoveryDecisionAsync(
            checkpoint,
            baseUri,
            requestId,
            stopwatch,
            model,
            intention,
            progress,
            recoverySpecialistModel,
            cancellationToken
          );

          foreach (var recoveryEvent in resolution.Events)
          {
            yield return recoveryEvent;
          }

          if (!resolution.ContinueExecution)
          {
            yield break;
          }

          planningFailures = 0;
          exhaustedFailure = null;
          planningFingerprints.Clear();
          continue;
        }

        if (planningFailures < maximumPlanningAttempts)
        {
          yield return Event(
            requestId,
            "action.planning-retry",
            $"Planning attempt {planningFailures} of {maximumPlanningAttempts} produced an invalid action: "
              + $"{exception.Message} Recovery budget: {progress.RecoveryAttemptCount}/"
              + $"{settings.MaxRecoveryAttemptsPerTurn}. Retrying with attempt {planningFailures + 1} of "
              + $"{maximumPlanningAttempts}.",
            stopwatch,
            model,
              intention
            );
          await DelayBeforeRecoveryRetryAsync(
            progress.RecoveryAttemptCount,
            cancellationToken
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
          "action.planning-exhausted",
          $"Local action planning exhausted its {maximumPlanningAttempts} automatic attempts: {exception.Message}",
          stopwatch,
          model,
          intention
        );
        var checkpoint = CreateRecoveryCheckpoint(
          requestId,
          stopwatch,
          model,
          intention,
          exception.Message,
          recoverySpecialistModel,
          cancellationToken
        );
        yield return checkpoint.Event;
        var resolution = await ResolveRecoveryDecisionAsync(
          checkpoint,
          baseUri,
          requestId,
          stopwatch,
          model,
          intention,
          progress,
          recoverySpecialistModel,
          cancellationToken
        );

        foreach (var recoveryEvent in resolution.Events)
        {
          yield return recoveryEvent;
        }

        if (!resolution.ContinueExecution)
        {
          yield break;
        }

        planningFailures = 0;
        continue;
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
        progress.RecoveryAttemptCount = 0;
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
          exception is LocalActionException terminalConflict
          && terminalConflict.Stage == "file-conflict"
        )
        {
          _executionSession?.RecordPlanActionResult(
            action.Tool,
            "blocked"
          );
          yield return Event(
            requestId,
            "execution-step-blocked",
            $"Plan step blocked because the target changed outside this execution: {action.Summary}.",
            stopwatch,
            model,
            intention
          );
          progress.Failure = ToChatException(
            terminalConflict,
            model,
            intention
          );
          yield break;
        }

        var recoveryLimitFailure = RecordRecoveryAttempt(
          progress,
          settings,
          failureOutput,
          model,
          intention
        );

        if (recoveryLimitFailure is not null)
        {
          var checkpoint = CreateRecoveryCheckpoint(
            requestId,
            stopwatch,
            model,
            intention,
            recoveryLimitFailure.TechnicalMessage,
            recoverySpecialistModel,
            cancellationToken
          );
          yield return checkpoint.Event;
          var resolution = await ResolveRecoveryDecisionAsync(
            checkpoint,
            baseUri,
            requestId,
            stopwatch,
            model,
            intention,
            progress,
            recoverySpecialistModel,
            cancellationToken
          );

          foreach (var recoveryEvent in resolution.Events)
          {
            yield return recoveryEvent;
          }

          if (!resolution.ContinueExecution)
          {
            yield break;
          }

          planningFailures = 0;
          continue;
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
        ) && progress.AutomaticStrategyRevisionCount < 2)
        {
          yield return Event(
            requestId,
            "agent.execution-recovery-started",
            $"The resident coordinator asked specialist model {recoverySpecialistModel} for a materially different strategy.",
            stopwatch,
            recoverySpecialistModel,
            intention
          );
          var guidance = await TryPrepareSupervisedGuidanceAsync(
            baseUri,
            recoverySpecialistModel,
            action,
            failureOutput,
            progress,
            cancellationToken
          );

          if (guidance.Guidance is not null)
          {
            progress.Messages.RemoveAll(
              IsGuidanceMessage
            );
            progress.ToolMessages.RemoveAll(
              message => message.Content?.StartsWith(
                ExpertExecutionGuidanceService.GuidanceMarker,
                StringComparison.Ordinal
              ) == true
            );
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
              guidance.RejectedUnchangedCandidate
                ? $"A materially different strategy was received from specialist model {recoverySpecialistModel} after rejecting an unchanged first response."
                : $"A materially different strategy was received from specialist model {recoverySpecialistModel}.",
              stopwatch,
              recoverySpecialistModel,
              intention
            );
          }
          else if (guidance.RepeatedPreviousStrategy)
          {
            var correction = new ChatMessage(
              "user",
              "RESIDENT_STRATEGY_SUPERVISION_RESULT\n"
                + "The specialist repeated the previous strategy twice, so that strategy was rejected. "
                + "Replan from the authoritative tool results. Preserve completed work, do not recreate "
                + "the project, do not repeat failed paths or actions, and choose a materially different "
                + "bounded next action."
            );
            progress.Messages.Add(
              correction
            );
            progress.ToolMessages.Add(
              ToToolMessage(
                correction
              )
            );
            yield return Event(
              requestId,
              "agent.execution-recovery-guidance-unchanged",
              $"Specialist model {recoverySpecialistModel} repeated the previous strategy twice. "
                + "The duplicate strategy was rejected and the active coordinator received a host-owned correction.",
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
          $"Execution failed for {action.Tool}; the result was returned to the active coordinator for replanning. "
            + $"Recovery budget: {progress.RecoveryAttemptCount}/{settings.MaxRecoveryAttemptsPerTurn}.",
          stopwatch,
          model,
          intention
        );
        await DelayBeforeRecoveryRetryAsync(
          progress.RecoveryAttemptCount,
          cancellationToken
        );
      }
    }

  }

  private async Task<SupervisedGuidanceAttempt> TryPrepareSupervisedGuidanceAsync(
    Uri baseUri,
    string specialistModel,
    ValidatedLocalAction failedAction,
    string failureOutput,
    ExecutionProgress progress,
    CancellationToken cancellationToken
  )
  {
    progress.AutomaticStrategyRevisionCount++;
    var previousGuidance = progress.Guidance is null
      ? null
      : ExpertExecutionGuidanceService.Serialize(
        progress.Guidance
      );
    var pendingSteps = _executionSession?.Plan?.Steps.Where(
      step => step.Status != "completed"
    ).Select(
      step => $"{step.Id}: {step.Title} [{step.Status}]"
    ).ToArray() ?? [];
    var revisionMessages = progress.Messages.Where(
      message => !IsGuidanceMessage(
        message
      )
    ).ToList();
    revisionMessages.Add(
      new ChatMessage(
        "user",
        "RESIDENT_STRATEGY_SUPERVISION\n"
          + "The resident coordinator detected execution without verified progress and is requesting "
          + "a different specialist approach.\n"
          + $"Failed action: {failedAction.Summary}\n"
          + $"Authoritative failure: {TruncateRecoveryContext(failureOutput)}\n"
          + $"Pending plan: {(pendingSteps.Length == 0 ? "none" : string.Join("; ", pendingSteps))}\n"
          + "The trusted workspace is already the project root. Preserve completed work and existing "
          + "files. Do not recreate the project, do not repeat the failed tool or path unchanged, and "
          + "do not use create_file when an existing file should be inspected or edited. Return a "
          + "materially different structured strategy focused on the smallest corrective action."
      )
    );
    var guidance = await TryPrepareGuidanceAsync(
      baseUri,
      specialistModel,
      revisionMessages,
      cancellationToken
    );
    var rejectedUnchangedCandidate = false;

    if (
      guidance.Guidance is not null
      && previousGuidance is not null
      && string.Equals(
        ExpertExecutionGuidanceService.Serialize(
          guidance.Guidance
        ),
        previousGuidance,
        StringComparison.Ordinal
      )
    )
    {
      rejectedUnchangedCandidate = true;
      revisionMessages.Add(
        new ChatMessage(
          "user",
          "RESIDENT_STRATEGY_SUPERVISION_REJECTED\n"
            + "The proposed strategy is identical to the strategy that already failed and was not accepted. "
            + "Change the tool, path, sequence, or arguments to address the authoritative failure. "
            + "Do not return the same JSON again."
        )
      );
      guidance = await TryPrepareGuidanceAsync(
        baseUri,
        specialistModel,
        revisionMessages,
        cancellationToken
      );
    }

    var repeatedPreviousStrategy = guidance.Guidance is not null
      && previousGuidance is not null
      && string.Equals(
        ExpertExecutionGuidanceService.Serialize(
          guidance.Guidance
        ),
        previousGuidance,
        StringComparison.Ordinal
      );

    return new SupervisedGuidanceAttempt(
      repeatedPreviousStrategy
        ? null
        : guidance.Guidance,
      guidance.Failure,
      rejectedUnchangedCandidate,
      repeatedPreviousStrategy
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
    string modelRole,
    ProviderChatOptions options,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    await using var updates = _ollamaClient.StreamChatAsync(
      baseUri,
      model,
      messages,
      UsageContext(
        model,
        modelRole,
        "target-response"
      ),
      options,
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

      if (update.Accepted && !progress.ReceivedFirstChunk)
      {
        yield return Event(
          requestId,
          "ollama.generation-accepted",
          "Generation accepted by Ollama.",
          stopwatch,
          model,
          intention
        );
      }

      if (update.Citations is not null)
      {
        progress.Citations = update.Citations;
      }

      if (update.Usage is not null)
      {
        progress.Usage = update.Usage;
      }

      if (!string.IsNullOrWhiteSpace(
        update.RetryActivity
      ))
      {
        yield return Event(
          requestId,
          "provider.retry",
          update.RetryActivity,
          stopwatch,
          model,
          intention
        );
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

  private async Task<ProviderModelCapabilities> ResolveCapabilitiesAsync(
    Uri baseUri,
    string model,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return await _ollamaClient.GetProviderModelCapabilitiesAsync(
        baseUri,
        model,
        cancellationToken
      );
    }
    catch (CapabilityException)
    {
      throw;
    }
    catch (OllamaProviderException exception)
    {
      throw new CapabilityException(
        "unknown-capability",
        "model-capabilities",
        "The selected model capabilities could not be verified.",
        exception.TechnicalMessage,
        ProviderModelReference.Parse(
          model
        ).ProviderId,
        ProviderModelReference.Parse(
          model
        ).ModelId,
        exception.HttpStatus,
        exception.Recoverable,
        exception
      );
    }
  }

  private async Task<(
    ProviderModelCapabilities Capabilities,
    CapabilityException? Warning
  )> ResolveTurnCapabilitiesAsync(
    Uri baseUri,
    string model,
    bool requiresVerifiedOptionalCapabilities,
    CancellationToken cancellationToken
  )
  {
    try
    {
      return (
        await ResolveCapabilitiesAsync(
          baseUri,
          model,
          cancellationToken
        ),
        null
      );
    }
    catch (CapabilityException exception) when (
      !requiresVerifiedOptionalCapabilities
    )
    {
      return (
        new ProviderModelCapabilities(
          Chat: true,
          Streaming: true,
          NativeTools: false,
          Vision: false,
          WebSearch: false,
          ContextTokens: null,
          Source: "unverified-conservative-text-only",
          Confirmed: false
        ),
        exception
      );
    }
  }

  private void ValidateRequestedCapabilities(
    ChatRequest request,
    string model,
    ProviderModelCapabilities capabilities,
    IReadOnlyList<ProviderImagePayload> images
  )
  {
    var reference = ProviderModelReference.Parse(
      model
    );

    if (request.WebSearchEnabled && !capabilities.WebSearch)
    {
      throw new CapabilityException(
        "unsupported-web",
        "capability-validation",
        "Web search is unavailable for the selected model.",
        $"Capability source '{capabilities.Source}' did not authorize web search.",
        reference.ProviderId,
        reference.ModelId
      );
    }

    if (
      !request.WebSearchEnabled
      && capabilities.ProviderNativeWebSearch
      && string.Equals(
        reference.ProviderId,
        ModelProviderIds.Groq,
        StringComparison.Ordinal
      )
      && (
        string.Equals(
          reference.ModelId,
          "groq/compound",
          StringComparison.Ordinal
        )
        || string.Equals(
          reference.ModelId,
          "groq/compound-mini",
          StringComparison.Ordinal
        )
      )
    )
    {
      throw new CapabilityException(
        "web-explicit-enable-required",
        "capability-validation",
        "This Groq system requires Web to be explicitly enabled.",
        "Groq Compound may invoke provider-native search automatically; the Host will not send the request while Web is off.",
        reference.ProviderId,
        reference.ModelId
      );
    }

    if (images.Count > 0 && !capabilities.Vision)
    {
      throw new CapabilityException(
        "unsupported-vision",
        "capability-validation",
        "The selected model cannot receive image attachments.",
        $"Capability source '{capabilities.Source}' did not authorize vision input. Images were not stripped.",
        reference.ProviderId,
        reference.ModelId
      );
    }

    if (
      images.Count > capabilities.MaximumImageCount
      || images.Any(
        image => image.Bytes.LongLength > capabilities.MaximumImageBytes
      )
      || images.Any(
        image => !(capabilities.SupportedImageMimeTypes ?? []).Contains(
          image.MimeType,
          StringComparer.OrdinalIgnoreCase
        )
      )
    )
    {
      throw new CapabilityException(
        "unsupported-vision",
        "capability-validation",
        "The image attachments exceed the selected model capability contract.",
        "The count, byte limit, or MIME type is unsupported by the selected model.",
        reference.ProviderId,
        reference.ModelId
      );
    }

    if (
      images.Count > 0
      && !reference.IsLocal
      && (
        string.IsNullOrWhiteSpace(
          request.BrowserSessionId
        )
        || !_cloudImageApprovals.IsApproved(
          request.BrowserSessionId,
          reference.ProviderId
        )
      )
    )
    {
      throw new CapabilityException(
        "cloud-image-approval-required",
        "cloud-image-privacy",
        $"Confirm that {ModelProviderIds.DisplayName(reference.ProviderId)} may receive {images.Sum(image => image.Bytes.LongLength)} image bytes.",
        "Cloud image upload approval is required for the current browser session and provider.",
        reference.ProviderId,
        reference.ModelId
      );
    }
  }

  private static IReadOnlyList<string> CapabilityLabels(
    ProviderModelCapabilities capabilities
  )
  {
    var labels = new List<string>();

    if (capabilities.Chat)
    {
      labels.Add(
        "chat"
      );
    }

    if (capabilities.Streaming)
    {
      labels.Add(
        "streaming"
      );
    }

    if (capabilities.NativeTools)
    {
      labels.Add(
        "tools-advertised"
      );
    }

    if (capabilities.StructuredOutput)
    {
      labels.Add(
        "structured"
      );
    }

    if (capabilities.Reasoning)
    {
      labels.Add(
        "reasoning"
      );
    }

    if (capabilities.WebSearch)
    {
      labels.Add(
        "web"
      );
    }

    if (capabilities.Vision)
    {
      labels.Add(
        "vision"
      );
    }

    return labels;
  }

  private static ContextUsageView CreateContextUsage(
    ConversationContextResult context,
    ProviderModelCapabilities capabilities,
    ApplicationSettings settings,
    ProviderTokenUsage? providerUsage
  )
  {
    var providerLimit = capabilities.ContextTokens;
    var configuredProviderLimit = settings.Context.ProviderContextTokens;
    var effectiveProviderLimit = providerLimit
      ?? configuredProviderLimit;
    var effectiveLimit = Math.Min(
      settings.Context.DefaultContextTokens,
      effectiveProviderLimit
    );
    var inputTokens = providerUsage?.InputTokens
      ?? context.EstimatedInputTokens;
    var usable = Math.Max(
      1,
      effectiveLimit - settings.Context.ReservedResponseTokens
    );
    var percentage = inputTokens * 100d / usable;
    var warning = percentage >= 95
      ? 95
      : percentage >= 85
        ? 85
        : percentage >= 70
          ? 70
          : 0;
    return new ContextUsageView(
      context.VisibleMessages,
      context.IncludedMessages,
      context.OmittedMessages,
      context.SystemInstructionTokens,
      context.CurrentUserMessageTokens,
      inputTokens,
      providerUsage is null
        ? "estimated"
        : "exact",
      providerLimit,
      configuredProviderLimit,
      settings.Context.DefaultContextTokens,
      settings.Context.ReservedResponseTokens,
      context.OmittedMessages > 0,
      warning
    );
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
      UsageContext(
        routerModel,
        UsageModelRoles.Router,
        "router-classification"
      ),
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
          UsageContext(
            model,
            UsageModelRoles.Specialist,
            "expert-execution-guidance"
          ),
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

  private static string? ResolveLocalFallback(
    ApplicationSettings settings,
    string intention,
    IReadOnlyList<InstalledModel> models
  )
  {
    if (!settings.Intentions.TryGetValue(
      intention,
      out var intentionSettings
    ))
    {
      return null;
    }

    var fallback = CloudFallbackPolicy.ResolveFallback(
      intentionSettings,
      settings.DefaultModel
    );

    return ProviderModelReference.Parse(
      fallback
    ).IsLocal && ContainsModel(
      models,
      fallback
    )
      ? fallback
      : null;
  }

  private static bool CanUseLocalFallback(
    RoutedProviderException exception
  )
  {
    if (!exception.Recoverable)
    {
      return false;
    }

    return exception.Code is
      "provider-rate-limited"
      or "provider-timeout"
      or "provider-unavailable"
      or "provider-disconnected"
      or "provider-request-failed";
  }

  private static string FormatConfidence(
    double? confidence
  )
  {
    return confidence is double value
      ? $"{Math.Round(value * 100, MidpointRounding.AwayFromZero):0}%"
      : "unavailable";
  }

  private async Task<PlanningAttempt> TryPlanAsync(
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
    var routed = exception as RoutedProviderException;
    var capability = exception as CapabilityException;

    return new ChatStageException(
      exception.Stage,
      exception.Message,
      exception.TechnicalMessage,
      model,
      intention,
      exception.HttpStatus,
      exception.Recoverable,
      exception,
      routed is null && capability is null
        ? null
        : new Dictionary<string, string?>(
          StringComparer.Ordinal
        )
        {
          ["code"] = routed?.Code
            ?? capability!.Code,
          ["providerTraceId"] = routed?.TraceId
            ?? capability!.TraceId,
          ["requestRemaining"] = routed?.RateLimit?.RequestRemaining?.ToString(),
          ["tokenRemaining"] = routed?.RateLimit?.TokenRemaining?.ToString()
        },
      routed?.Provider
        ?? capability?.Provider
        ?? ModelProviderIds.OllamaLocal
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

  private OllamaToolMessage CreateCompletionRejectedMessage()
  {
    var pendingSteps = _executionSession?.Plan?.Steps.Where(
      step => step.Status != "completed"
    ).Select(
      step => $"{step.Id}: {step.Title}"
    ).ToArray() ?? [];
    var requirement = pendingSteps.Length == 0
      ? "No valid completed execution plan is stored. Call create_execution_plan with the required remaining work."
      : $"The visible execution plan still has pending steps: {string.Join("; ", pendingSteps)}. "
        + "Call exactly one available tool for the next required step.";
    return new OllamaToolMessage(
      "user",
      $"EXECUTION_COMPLETION_REJECTED\n{requirement} Do not answer with prose."
    );
  }

  private RecoveryCheckpoint CreateRecoveryCheckpoint(
    string requestId,
    Stopwatch stopwatch,
    string model,
    string intention,
    string reason,
    string? recoverySpecialistModel,
    CancellationToken cancellationToken
  )
  {
    var executionSession = _executionSession
      ?? throw new InvalidOperationException(
        "A recovery checkpoint requires an active execution session."
      );
    var checkpointId = Guid.NewGuid().ToString(
      "N"
    );
    var options = new List<RecoveryOptionView>
    {
      new(
        "retry",
        "Tentar novamente",
        "Repassa o erro e o plano pendente ao agente ativo, com um novo limite de tentativas."
      )
    };

    var recoveryAdvisorModel = recoverySpecialistModel ?? model;
    options.Add(
      new RecoveryOptionView(
        "specialist",
        "Pedir nova estratégia",
        $"Solicita ao modelo {recoveryAdvisorModel} uma estratégia revisada antes de retomar."
      )
    );

    options.Add(
      new RecoveryOptionView(
        "stop",
        "Encerrar e manter alterações",
        "Interrompe novas ações, preserva os arquivos atuais e produz um resumo parcial."
      )
    );
    var allowedOptions = options.Select(
      option => option.Id
    ).ToHashSet(
      StringComparer.Ordinal
    );
    var decisionTask = _recoveryDecisions.WaitAsync(
      checkpointId,
      executionSession.BrowserSessionId,
      executionSession.Id,
      allowedOptions,
      cancellationToken
    );
    var streamEvent = Event(
      requestId,
      "action.recovery-decision-required",
      "Automatic recovery reached its bounded limit. Choose how the agent should continue.",
      stopwatch,
      model,
      intention
    ) with
    {
      RecoveryDecision = new RecoveryDecisionEvent(
        checkpointId,
        executionSession.Id,
        reason,
        options
      )
    };

    return new RecoveryCheckpoint(
      streamEvent,
      decisionTask,
      reason
    );
  }

  private ChatMessage CreateRecoveryDecisionMessage(
    string option,
    RecoveryCheckpoint checkpoint,
    ExecutionProgress progress
  )
  {
    var pendingSteps = _executionSession?.Plan?.Steps.Where(
      step => step.Status != "completed"
    ).Select(
      step => $"{step.Id}: {step.Title} [{step.Status}]"
    ).ToArray() ?? [];
    var pendingPlan = pendingSteps.Length == 0
      ? "No accepted visible plan is currently available."
      : string.Join(
        "; ",
        pendingSteps
      );
    var previousGuidance = progress.Guidance is null
      ? "No previous specialist strategy is available."
      : TruncateRecoveryContext(
        ExpertExecutionGuidanceService.Serialize(
          progress.Guidance
        )
      );
    var marker = option == "specialist"
      ? "RECOVERY_STRATEGY_REVISION"
      : "RECOVERY_DECISION";

    return new ChatMessage(
      "user",
      $"{marker}\n"
        + $"Option: {option}\n"
        + $"Exact failure to correct:\n{TruncateRecoveryContext(checkpoint.Reason)}\n"
        + $"Pending visible plan:\n{pendingPlan}\n"
        + $"Previous specialist strategy:\n{previousGuidance}\n"
        + "Do not repeat the failed proposal. Address the exact failure before continuing. "
        + "When creating or revising an execution plan, every step must contain non-empty string id and title fields. "
        + (
          option == "specialist"
            ? "Return a materially revised structured strategy, not the previous strategy again."
            : "Propose a materially corrected next action."
        )
    );
  }

  private static bool IsGuidanceMessage(
    ChatMessage message
  )
  {
    return message.Content.StartsWith(
      ExpertExecutionGuidanceService.GuidanceMarker,
      StringComparison.Ordinal
    );
  }

  private static string TruncateRecoveryContext(
    string value
  )
  {
    const int maximumLength = 12_000;
    return value.Length <= maximumLength
      ? value
      : $"{value[..maximumLength]}\n[recovery context truncated]";
  }

  private async Task<RecoveryResolution> ResolveRecoveryDecisionAsync(
    RecoveryCheckpoint checkpoint,
    Uri baseUri,
    string requestId,
    Stopwatch stopwatch,
    string model,
    string intention,
    ExecutionProgress progress,
    string? recoverySpecialistModel,
    CancellationToken cancellationToken
  )
  {
    var option = await checkpoint.DecisionTask;
    var events = new List<ChatStreamEvent>();

    if (option == "stop")
    {
      const string message =
        "Execution stopped by the user at the recovery checkpoint. Existing changes were preserved.";
      _executionSession?.AddWarning(
        message
      );
      progress.Messages.Add(
        new ChatMessage(
          "user",
          "RECOVERY_DECISION\nOption: stop\n"
            + "Stop proposing local actions. Preserve existing changes and report the partial result."
        )
      );
      events.Add(
        Event(
          requestId,
          "action.recovery-stopped",
          message,
          stopwatch,
          model,
          intention
        )
      );
      return new RecoveryResolution(
        false,
        events
      );
    }

    progress.RecoveryAttemptCount = 0;
    progress.PlanningFailure = null;
    var decisionMessage = CreateRecoveryDecisionMessage(
      option,
      checkpoint,
      progress
    );
    progress.Messages.Add(
      decisionMessage
    );
    progress.ToolMessages.Add(
      ToToolMessage(
        decisionMessage
      )
    );

    if (option == "specialist")
    {
      var recoveryAdvisorModel = recoverySpecialistModel ?? model;
      events.Add(
        Event(
          requestId,
          "agent.execution-recovery-started",
          $"The user requested a revised strategy from model {recoveryAdvisorModel}.",
          stopwatch,
          recoveryAdvisorModel,
          intention
        )
      );
      var previousGuidance = progress.Guidance is null
        ? null
        : ExpertExecutionGuidanceService.Serialize(
          progress.Guidance
        );
      var revisionMessages = progress.Messages.Where(
        message => !message.Content.StartsWith(
          ExpertExecutionGuidanceService.GuidanceMarker,
          StringComparison.Ordinal
        )
      ).ToList();
      var guidance = await TryPrepareGuidanceAsync(
        baseUri,
        recoveryAdvisorModel,
        revisionMessages,
        cancellationToken
      );
      var rejectedUnchangedCandidate = false;

      if (
        guidance.Guidance is not null
        && previousGuidance is not null
        && string.Equals(
          ExpertExecutionGuidanceService.Serialize(
            guidance.Guidance
          ),
          previousGuidance,
          StringComparison.Ordinal
        )
      )
      {
        rejectedUnchangedCandidate = true;
        revisionMessages.Add(
          new ChatMessage(
            "user",
            "RECOVERY_STRATEGY_REVISION_REJECTED\n"
              + "The proposed strategy was identical to the previous strategy and was not accepted. "
              + "Return a materially revised brief that directly addresses the reported failure. "
              + "Change the action titles, sequence, tool choice, or arguments as needed; do not repeat the same JSON."
          )
        );
        guidance = await TryPrepareGuidanceAsync(
          baseUri,
          recoveryAdvisorModel,
          revisionMessages,
          cancellationToken
        );
      }

      if (guidance.Guidance is not null)
      {
        var revisedGuidance = ExpertExecutionGuidanceService.Serialize(
          guidance.Guidance
        );

        if (
          previousGuidance is not null
          && string.Equals(
            revisedGuidance,
            previousGuidance,
            StringComparison.Ordinal
          )
        )
        {
          events.Add(
            Event(
              requestId,
              "agent.execution-recovery-guidance-unchanged",
              $"Model {recoveryAdvisorModel} repeated the previous strategy after two bounded revision attempts. "
                + "The duplicate strategy was not accepted; the active coordinator will retry using the explicit failure context.",
              stopwatch,
              recoveryAdvisorModel,
              intention
            )
          );
        }
        else
        {
          progress.Messages.RemoveAll(
            IsGuidanceMessage
          );
          progress.ToolMessages.RemoveAll(
            message => message.Content?.StartsWith(
              ExpertExecutionGuidanceService.GuidanceMarker,
              StringComparison.Ordinal
            ) == true
          );
          var guidanceMessage = GuidanceMessage(
            recoveryAdvisorModel,
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
          events.Add(
            Event(
              requestId,
              "agent.execution-recovery-guidance-prepared",
              rejectedUnchangedCandidate
                ? $"A materially revised strategy was received from model {recoveryAdvisorModel} after rejecting an unchanged first response."
                : $"A materially revised strategy was received from model {recoveryAdvisorModel}.",
              stopwatch,
              recoveryAdvisorModel,
              intention
            )
          );
        }
      }
      else
      {
        _logger.LogWarning(
          guidance.Failure,
          "User-requested recovery guidance was unavailable for model {Model}; the coordinator will retry directly.",
          recoveryAdvisorModel
        );
        events.Add(
          Event(
            requestId,
            "agent.execution-recovery-guidance-unavailable",
            $"The requested specialist strategy was unavailable: {guidance.Failure!.Message} "
              + "The active coordinator will retry with the recorded failure context.",
            stopwatch,
            recoveryAdvisorModel,
            intention
          )
        );
      }
    }

    events.Add(
      Event(
        requestId,
        "action.recovery-resumed",
        option == "specialist"
          ? "Execution resumed with a fresh bounded recovery budget and explicit failure context."
          : "Execution resumed with a fresh bounded recovery budget and strengthened failure context.",
        stopwatch,
        model,
        intention
      )
    );
    return new RecoveryResolution(
      true,
      events
    );
  }

  private static ChatStageException? RecordRecoveryAttempt(
    ExecutionProgress progress,
    ExecutionSettings settings,
    string detail,
    string model,
    string intention
  )
  {
    progress.RecoveryAttemptCount++;

    if (
      progress.RecoveryAttemptCount
      < settings.MaxRecoveryAttemptsPerTurn
    )
    {
      return null;
    }

    return new ChatStageException(
      "local-action-recovery-limit",
      $"The request reached the limit of {settings.MaxRecoveryAttemptsPerTurn} recovery attempts.",
      detail,
      model,
      intention,
      400,
      true
    );
  }

  private static Task DelayBeforeRecoveryRetryAsync(
    int recoveryAttemptCount,
    CancellationToken cancellationToken
  )
  {
    var exponent = Math.Clamp(
      recoveryAttemptCount - 1,
      0,
      3
    );
    var delayMilliseconds = Math.Min(
      1_200,
      150 * (1 << exponent)
    );
    return Task.Delay(
      delayMilliseconds,
      cancellationToken
    );
  }

  private static string FormatActivityOutput(
    ValidatedLocalAction action,
    string output
  )
  {
    if (action.Tool == "read_file")
    {
      return $"Read file: {Path.GetFileName(action.TargetPath)}.";
    }

    if (action.Tool == "get_file_info")
    {
      return $"Inspected: {Path.GetFileName(action.TargetPath)}.";
    }

    if (action.Tool == "search_text")
    {
      return $"Search completed in '{action.Summary["search_text: ".Length..]}'";
    }

    if (action.Tool == "list_files")
    {
      var files = output.Split(
        [
          '\r',
          '\n'
        ],
        StringSplitOptions.RemoveEmptyEntries
      );
      const int maximumVisibleFiles = 50;
      var visibleFiles = files.Take(
        maximumVisibleFiles
      );
      var suffix = files.Length > maximumVisibleFiles
        ? $"\nâ€¦ and {files.Length - maximumVisibleFiles} more."
        : string.Empty;

      return $"{action.Summary}\n{string.Join("\n", visibleFiles)}{suffix}";
    }

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

  private ProviderCallContext UsageContext(
    string model,
    string modelRole,
    string requestPurpose
  )
  {
    _usageModelRevisions.TryGetValue(
      model,
      out var revision
    );

    return new ProviderCallContext(
      _usageWorkspaceId,
      _usageConversationId,
      _usageTurnId,
      _executionSession?.Id,
      modelRole,
      requestPurpose,
      revision
    );
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
    catch (LocalActionException exception)
    {
      return exception;
    }
    catch (Exception exception) when (
      exception is IOException
      or UnauthorizedAccessException
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

    public IReadOnlyList<ProviderCitation>? Citations { get; set; }

    public ProviderTokenUsage? Usage { get; set; }
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

    public int RecoveryAttemptCount { get; set; }

    public int AutomaticStrategyRevisionCount { get; set; }
  }

  private sealed record PlanningAttempt(
    LocalActionPlanningResult? Result,
    Exception? Failure
  );

  private sealed record ValidationAttempt(
    ValidatedLocalAction? Action,
    LocalActionException? Failure
  );

  private sealed record RecoveryCheckpoint(
    ChatStreamEvent Event,
    Task<string> DecisionTask,
    string Reason
  );

  private sealed record RecoveryResolution(
    bool ContinueExecution,
    IReadOnlyList<ChatStreamEvent> Events
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

  private sealed record SupervisedGuidanceAttempt(
    ExpertExecutionGuidance? Guidance,
    Exception? Failure,
    bool RejectedUnchangedCandidate,
    bool RepeatedPreviousStrategy
  );

}
