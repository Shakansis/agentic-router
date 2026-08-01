using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticRouter.Api.Configuration;
using AgenticRouter.Api.Contracts;
using AgenticRouter.Api.Execution;
using AgenticRouter.Api.Markdown;
using AgenticRouter.Api.Observability;
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
  private readonly IResidentCoordinationEligibilityService _residentEligibility;
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
  private readonly ITraceContext _trace;
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
    IResidentCoordinationEligibilityService residentEligibility,
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
    ITraceContext trace,
    ILogger<ChatStreamService> logger
  )
  {
    _settingsStore = settingsStore;
    _ollamaClient = ollamaClient;
    _markdownRenderer = markdownRenderer;
    _residentModel = residentModel;
    _residentEligibility = residentEligibility;
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
    _trace = trace;
    _logger = logger;
  }

  public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
    ChatRequest request,
    string requestId,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    using var requestLease = _residentModel.BeginRequest();
    _trace.Link("turnId", requestId);
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
      var configuredCoordinatorFallback = settings.CoordinatorModel;
      if (string.Equals(request.InteractionMode, "execute", StringComparison.Ordinal))
      {
        settings = settings with
        {
          CoordinatorModel = settings.ActionModel
        };
      }
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

        var tooling = await InspectToolingAsync(
          baseUri,
          selectedModel,
          cancellationToken
        );
        string coordinatorModel;
        List<ChatMessage> executionMessages;
        ExpertExecutionGuidance? executionGuidance = null;
        var toolingAdvertised =
          tooling.Capabilities?.ToolingConfirmed == true;
        var targetCoordinatesDirectly = false;
        var structuredCoordination = false;
        var residentConformanceApproved = false;
        string? effectiveConformanceIdentity = null;
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
        ToolProtocolConformanceResult? nativeConformance = null;
        ToolProtocolConformanceResult? structuredConformance = null;

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
          nativeConformance = selectedReference.IsLocal
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
          targetCoordinatesDirectly = nativeConformance?.Passed == true;
          yield return Event(
            requestId,
            nativeConformance?.Passed == true
              ? "agent.tooling-conformance-passed"
              : "agent.tooling-conformance-failed",
            nativeConformance?.Passed == true
              ? $"native-strict conformance passed for target {selectedModel}; identity {nativeConformance.Identity}."
              : $"native-strict conformance is unavailable for target {selectedModel}: {nativeConformance?.Failure ?? "explicit cloud benchmark permission is required"}.",
            stopwatch,
            selectedModel,
            intention
          );
        }

        if (
          !targetCoordinatesDirectly
          && capabilities.StructuredOutput
        )
        {
          structuredConformance = await _toolConformance.GetCachedPathAsync(
            baseUri,
            selectedModel,
            selectedIdentity.Digest,
            CoordinationConformanceProfiles.StructuredAction,
            cancellationToken
          );
          structuredCoordination = structuredConformance?.Passed == true;
          targetCoordinatesDirectly = structuredCoordination;
          yield return Event(
            requestId,
            structuredCoordination
              ? "agent.structured-conformance-passed"
              : "agent.structured-conformance-unavailable",
            structuredCoordination
              ? $"structured-action conformance passed for target {selectedModel}; identity {structuredConformance!.Identity}."
              : $"structured-action conformance is unavailable for target {selectedModel}: "
                + $"{structuredConformance?.Failure ?? "no approved evidence for this exact identity"}.",
            stopwatch,
            selectedModel,
            intention
          );
        }

        if (targetCoordinatesDirectly)
        {
          coordinatorModel = selectedModel;
          executionMessages = messages.ToList();
          effectiveConformanceIdentity = structuredCoordination
            ? structuredConformance?.Identity
            : nativeConformance?.Identity;
          yield return Event(
            requestId,
            "agent.coordination-path-resolved",
            $"Target {selectedModel} is the effective coordinator through "
              + $"{(structuredCoordination ? "direct-structured" : "direct-native")}; "
              + $"resident {settings.CoordinatorModel} is not a prerequisite.",
            stopwatch,
            selectedModel,
            intention
          );
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

          if (
            !ContainsModel(models, settings.CoordinatorModel)
            && ContainsModel(models, configuredCoordinatorFallback)
          )
          {
            yield return Event(
              requestId,
              "agent.action-model-fallback",
              $"Resident action model '{settings.CoordinatorModel}' is not installed; using configured on-demand coordinator fallback '{configuredCoordinatorFallback}'.",
              stopwatch,
              configuredCoordinatorFallback,
              intention
            );
            settings = settings with
            {
              CoordinatorModel = configuredCoordinatorFallback
            };
          }

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

          var configuredCoordinatorReference = ProviderModelReference.Parse(
            settings.CoordinatorModel
          );
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
          ToolProtocolConformanceResult? coordinatorAdaptiveConformance = null;
          var coordinatorIdentity = models.First(
            installed => string.Equals(
              installed.Name,
              settings.CoordinatorModel,
              StringComparison.OrdinalIgnoreCase
            )
          );

          if (coordinatorTooling.Capabilities?.ToolingConfirmed == true)
          {
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

            if (coordinatorConformance?.Passed != true)
            {
              yield return Event(
                requestId,
                "agent.coordinator-conformance-path-failed",
                $"Resident {settings.CoordinatorModel} failed native-strict conformance: "
                  + $"{coordinatorConformance?.Failure ?? "no approved evidence for this exact identity"}. "
                  + (coordinatorConformance?.AdaptiveRepairEligible == true
                    ? "The Host will evaluate native-adaptive independently."
                    : "The failure is not semantically repairable; another native path will not be attempted."),
                stopwatch,
                settings.CoordinatorModel,
                intention
              );

              if (coordinatorConformance?.AdaptiveRepairEligible == true)
              {
                yield return Event(
                  requestId,
                  "agent.coordinator-adaptive-conformance-started",
                  configuredCoordinatorReference.IsLocal
                    ? $"Running native-adaptive conformance for resident {settings.CoordinatorModel}."
                    : $"Loading cached native-adaptive conformance for resident {settings.CoordinatorModel}; no cloud benchmark will be started implicitly.",
                  stopwatch,
                  settings.CoordinatorModel,
                  intention
                );
                coordinatorAdaptiveConformance = configuredCoordinatorReference.IsLocal
                  ? await _toolConformance.VerifyPathAsync(
                    baseUri,
                    settings.CoordinatorModel,
                    coordinatorIdentity.Digest,
                    CoordinationConformanceProfiles.NativeAdaptive,
                    UsageContext(
                      settings.CoordinatorModel,
                      UsageModelRoles.Benchmark,
                      "tool-protocol-native-adaptive-conformance"
                    ),
                    cancellationToken
                  )
                  : await _toolConformance.GetCachedPathAsync(
                    baseUri,
                    settings.CoordinatorModel,
                    coordinatorIdentity.Digest,
                    CoordinationConformanceProfiles.NativeAdaptive,
                    cancellationToken
                  );
                yield return Event(
                  requestId,
                  coordinatorAdaptiveConformance?.Passed == true
                    ? "agent.coordinator-adaptive-conformance-passed"
                    : "agent.coordinator-adaptive-conformance-failed",
                  coordinatorAdaptiveConformance?.Passed == true
                    ? $"Resident {settings.CoordinatorModel} passed native-adaptive conformance; identity "
                      + $"{coordinatorAdaptiveConformance.Identity}."
                    : $"Resident {settings.CoordinatorModel} did not pass native-adaptive conformance: "
                      + $"{coordinatorAdaptiveConformance?.Failure ?? "no approved evidence for this exact identity"}.",
                  stopwatch,
                  settings.CoordinatorModel,
                  intention
                );
              }
            }
          }

          var approvedCoordinatorConformance = coordinatorConformance?.Passed == true
            ? coordinatorConformance
            : coordinatorAdaptiveConformance?.Passed == true
              ? coordinatorAdaptiveConformance
              : null;

          if (
            approvedCoordinatorConformance is null
            && !string.Equals(
              settings.CoordinatorModel,
              configuredCoordinatorFallback,
              StringComparison.OrdinalIgnoreCase
            )
            && ContainsModel(models, configuredCoordinatorFallback)
          )
          {
            yield return Event(
              requestId,
              "agent.action-model-conformance-fallback-started",
              $"Action model {settings.CoordinatorModel} has no approved path; evaluating on-demand coordinator fallback {configuredCoordinatorFallback}.",
              stopwatch,
              configuredCoordinatorFallback,
              intention
            );
            var fallbackTooling = await InspectToolingAsync(
              baseUri,
              configuredCoordinatorFallback,
              cancellationToken
            );
            var fallbackIdentity = models.First(
              installed => string.Equals(
                installed.Name,
                configuredCoordinatorFallback,
                StringComparison.OrdinalIgnoreCase
              )
            );
            var fallbackReference = ProviderModelReference.Parse(configuredCoordinatorFallback);
            ToolProtocolConformanceResult? fallbackStrict = null;
            ToolProtocolConformanceResult? fallbackAdaptive = null;

            if (fallbackTooling.Capabilities?.ToolingConfirmed == true)
            {
              fallbackStrict = fallbackReference.IsLocal
                ? await _toolConformance.VerifyAsync(
                  baseUri,
                  configuredCoordinatorFallback,
                  fallbackIdentity.Digest,
                  UsageContext(
                    configuredCoordinatorFallback,
                    UsageModelRoles.Benchmark,
                    "tool-protocol-conformance"
                  ),
                  cancellationToken
                )
                : await _toolConformance.GetCachedAsync(
                  baseUri,
                  configuredCoordinatorFallback,
                  fallbackIdentity.Digest,
                  cancellationToken
                );

              if (fallbackStrict?.Passed != true && fallbackStrict?.AdaptiveRepairEligible == true)
              {
                fallbackAdaptive = fallbackReference.IsLocal
                  ? await _toolConformance.VerifyPathAsync(
                    baseUri,
                    configuredCoordinatorFallback,
                    fallbackIdentity.Digest,
                    CoordinationConformanceProfiles.NativeAdaptive,
                    UsageContext(
                      configuredCoordinatorFallback,
                      UsageModelRoles.Benchmark,
                      "tool-protocol-native-adaptive-conformance"
                    ),
                    cancellationToken
                  )
                  : await _toolConformance.GetCachedPathAsync(
                    baseUri,
                    configuredCoordinatorFallback,
                    fallbackIdentity.Digest,
                    CoordinationConformanceProfiles.NativeAdaptive,
                    cancellationToken
                  );
              }
            }

            approvedCoordinatorConformance = fallbackStrict?.Passed == true
              ? fallbackStrict
              : fallbackAdaptive?.Passed == true
                ? fallbackAdaptive
                : null;

            if (approvedCoordinatorConformance is not null)
            {
              if (fallbackReference.IsLocal && _residentModel.GetStatus().Loaded)
              {
                yield return Event(
                  requestId,
                  "resident-model-eviction-started",
                  $"Evicting action model {settings.ActionModel} before loading on-demand coordinator fallback {configuredCoordinatorFallback}.",
                  stopwatch,
                  settings.ActionModel,
                  intention
                );
                recoveryActive = await _residentModel.EvictForRecoveryAsync(
                  configuredCoordinatorFallback,
                  cancellationToken
                );
                recoveryTarget = configuredCoordinatorFallback;

                if (!recoveryActive)
                {
                  throw new ChatStageException(
                    "resident-model-eviction",
                    "The resident action model could not be evicted for the on-demand coordinator fallback.",
                    $"Action model {settings.ActionModel} remained resident while fallback {configuredCoordinatorFallback} required the local runtime.",
                    settings.ActionModel,
                    intention,
                    null,
                    true
                  );
                }
              }

              settings = settings with
              {
                CoordinatorModel = configuredCoordinatorFallback
              };
              yield return Event(
                requestId,
                "agent.action-model-conformance-fallback-passed",
                $"On-demand coordinator fallback {configuredCoordinatorFallback} passed {approvedCoordinatorConformance.Profile} conformance.",
                stopwatch,
                configuredCoordinatorFallback,
                intention
              );
            }
          }

          if (approvedCoordinatorConformance is null)
          {
            var coordinatorFailure = coordinatorTooling.Capabilities?.ToolingConfirmed != true
              ? coordinatorTooling.Failure?.Message
                ?? "Native tooling support is not approved for this exact resident identity."
              : $"native-strict: {coordinatorConformance?.Failure ?? "no approved evidence"}; "
                + $"native-adaptive: {coordinatorAdaptiveConformance?.Failure ?? (coordinatorConformance?.AdaptiveRepairEligible == true ? "no approved evidence" : "not attempted because the strict failure was not semantically repairable")}";
            yield return Event(
              requestId,
              "agent.coordinator-conformance-failed",
              $"Target {selectedModel} had no approved direct path. Resident {settings.CoordinatorModel} "
                + $"failed native-strict conformance: {coordinatorFailure}. All evaluated paths are blocked.",
              stopwatch,
              settings.CoordinatorModel,
              intention
            );
            throw new ChatStageException(
              "coordination-paths-exhausted",
              "No approved Execute coordination path is available.",
              coordinatorFailure,
              settings.CoordinatorModel,
              intention,
              null,
              true,
              details: new Dictionary<string, string?>
              {
                ["targetModel"] = selectedModel,
                ["residentModel"] = settings.CoordinatorModel,
                ["nativeTargetStatus"] = nativeConformance?.Status
                  ?? CoordinationConformanceProfiles.Unknown,
                ["structuredTargetStatus"] = structuredConformance?.Status
                  ?? CoordinationConformanceProfiles.Unknown,
                ["residentStrictStatus"] = coordinatorConformance?.Status
                  ?? CoordinationConformanceProfiles.Unknown,
                ["residentAdaptiveStatus"] = coordinatorAdaptiveConformance?.Status
                  ?? CoordinationConformanceProfiles.Unknown,
                ["executionPath"] = "blocked"
              }
            );
          }

          yield return Event(
            requestId,
            "agent.coordinator-conformance-passed",
            $"Resident {settings.CoordinatorModel} passed {approvedCoordinatorConformance.Profile} conformance; identity "
              + $"{approvedCoordinatorConformance.Identity}.",
            stopwatch,
            settings.CoordinatorModel,
            intention
          );
          residentConformanceApproved = true;
          effectiveConformanceIdentity = approvedCoordinatorConformance.Identity;

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

        var residentEligibility = _residentEligibility.Evaluate(
          settings,
          selectedIdentity,
          _residentModel.GetStatus(),
          residentConformanceApproved
        );
        yield return Event(
          requestId,
          "agent.memory-eligibility-evaluated",
          $"Resident {settings.CoordinatorModel}; target {selectedModel}. Evidence: "
            + $"{residentEligibility.Evidence}. Consequence: {residentEligibility.MemoryConsequence}.",
          stopwatch,
          settings.CoordinatorModel,
          intention
        );

        if (
          !targetCoordinatesDirectly
          && !residentEligibility.ResidentEligible
        )
        {
          throw new ChatStageException(
            "resident-memory-eligibility",
            "The configured resident is not eligible for this coordination path.",
            residentEligibility.MemoryConsequence,
            settings.CoordinatorModel,
            intention,
            null,
            true
          );
        }

        if (
          targetCoordinatesDirectly
          && selectedReference.IsLocal
          && residentEligibility.RequiresResidentEviction
        )
        {
          yield return Event(
            requestId,
            "resident-model-eviction-started",
            $"Evicting resident {settings.CoordinatorModel} before local target coordination to preserve configured memory headroom.",
            stopwatch,
            settings.CoordinatorModel,
            intention
          );
          recoveryActive = await _residentModel.EvictForRecoveryAsync(
            selectedModel,
            cancellationToken
          );
          recoveryTarget = selectedModel;

          if (!recoveryActive)
          {
            throw new ChatStageException(
              "resident-model-eviction",
              "The resident could not be evicted for the selected local target.",
              residentEligibility.MemoryConsequence,
              settings.CoordinatorModel,
              intention,
              null,
              true
            );
          }

          yield return Event(
            requestId,
            "resident-model-evicted",
            $"Resident {settings.CoordinatorModel} was verified absent before {selectedModel} coordination.",
            stopwatch,
            settings.CoordinatorModel,
            intention
          );
        }

        _executionSession.ResolveCoordinator(
          coordinatorModel,
          targetCoordinatesDirectly
            ? structuredCoordination
              ? "direct-structured"
              : "direct-native"
            : "resident-bridge"
        );
        _executionSession.RecordCoordinationMetadata(
          settings.CoordinatorModel,
          effectiveConformanceIdentity,
          targetCoordinatesDirectly
            ? null
            : $"Target {selectedModel} had no approved direct coordination path."
        );

        if (!targetCoordinatesDirectly)
        {
          executionMessages = CompactCoordinatorMessages(
            executionMessages
          );
        }

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
          structuredCoordination,
          targetCoordinatesDirectly
            ? null
            : selectedModel,
          targetCoordinatesDirectly
            ? settings.Execution.DirectCoordinatorPlanningFailuresBeforeHandoff
            : settings.Execution.ResidentCoordinatorPlanningFailuresBeforeFailure,
          targetCoordinatesDirectly
            && settings.Execution.MaxCoordinatorHandoffsPerTurn > 0,
          settings,
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

          if (recoveryActive && recoveryTarget is not null)
          {
            yield return Event(
              requestId,
              "resident-model-reload-started",
              $"Restoring resident {settings.CoordinatorModel} before takeover evaluation.",
              stopwatch,
              settings.CoordinatorModel,
              intention
            );
            var restoredForTakeover = await _residentModel.RestoreAfterRecoveryAsync(
              recoveryTarget,
              cancellationToken
            );
            recoveryActive = false;

            if (!restoredForTakeover)
            {
              throw new ChatStageException(
                "resident-model-reload",
                "The resident could not be restored for coordinator takeover.",
                $"Resident {settings.CoordinatorModel} restoration failed after target path failure.",
                settings.CoordinatorModel,
                intention,
                null,
                true
              );
            }
          }

          var takeoverTooling = await InspectToolingAsync(
            baseUri,
            settings.CoordinatorModel,
            cancellationToken
          );
          var takeoverIdentity = models.First(
            installed => string.Equals(
              installed.Name,
              settings.CoordinatorModel,
              StringComparison.OrdinalIgnoreCase
            )
          );
          var takeoverReference = ProviderModelReference.Parse(
            settings.CoordinatorModel
          );
          var takeoverConformance = takeoverTooling.Capabilities?.ToolingConfirmed == true
            ? takeoverReference.IsLocal
              ? await _toolConformance.VerifyAsync(
                baseUri,
                settings.CoordinatorModel,
                takeoverIdentity.Digest,
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
                takeoverIdentity.Digest,
                cancellationToken
              )
            : null;
          var takeoverEligibility = _residentEligibility.Evaluate(
            settings,
            selectedIdentity,
            _residentModel.GetStatus(),
            takeoverConformance?.Passed == true
          );

          if (
            takeoverConformance?.Passed != true
            || !takeoverEligibility.ResidentEligible
          )
          {
            var takeoverFailure = takeoverConformance?.Failure
              ?? takeoverTooling.Failure?.Message
              ?? takeoverEligibility.MemoryConsequence;
            yield return Event(
              requestId,
              "agent.coordinator-conformance-failed",
              $"Target {selectedModel} failed its active path and resident {settings.CoordinatorModel} "
                + $"is not eligible for takeover: {takeoverFailure}",
              stopwatch,
              settings.CoordinatorModel,
              intention
            );
            throw new ChatStageException(
              "coordination-paths-exhausted",
              "All approved Execute coordination paths were exhausted.",
              takeoverFailure,
              settings.CoordinatorModel,
              intention,
              null,
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
          _executionSession.RecordCoordinationMetadata(
            settings.CoordinatorModel,
            takeoverConformance.Identity,
            $"Target {selectedModel} failed its active coordination path: {execution.PlanningFailure.Message}"
          );
          var recoveryAttemptCount = execution.RecoveryAttemptCount;
          residentMessages = CompactCoordinatorMessages(
            residentMessages
          );
          residentToolMessages = CompactCoordinatorToolMessages(
            residentToolMessages
          );
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
            false,
            selectedModel,
            settings.Execution.ResidentCoordinatorPlanningFailuresBeforeFailure,
            false,
            settings,
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

        if (execution.PartialContextExhausted)
        {
          _executionSession.MarkPartialContextExhausted();
          _executionSession.Complete("completed-with-warnings");
          var partialSummary = _executionSession.CreateSummary();
          var partialText = "Execution reached the configured coordinator context limit after one bounded deterministic compaction. "
            + "Completed actions and artifacts were preserved; open the execution review before continuing.";
          yield return new ChatStreamEvent(
            requestId,
            "response.completed",
            DateTimeOffset.UtcNow,
            "Partial reviewable result completed after context exhaustion.",
            null,
            selectedModel,
            isAuto ? intention : null,
            stopwatch.ElapsedMilliseconds,
            _markdownRenderer.Render(partialText + "\n\n" + CreateAuthoritativeStatus(partialSummary.CompletionStatus)),
            null,
            null,
            partialSummary
          );
          yield break;
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
        _executionSession.Complete(
          _executionSession.HasWarnings
            ? "completed-with-warnings"
            : "completed"
        );
        var hostReview = _executionSession.CreateReview();
        var hostResponse = CreateHostExecutionResponse(hostReview);
        yield return new ChatStreamEvent(
          requestId,
          "response.completed",
          DateTimeOffset.UtcNow,
          "Host-authoritative execution response completed.",
          null,
          hostReview.Summary.CoordinatorModel,
          isAuto ? intention : null,
          stopwatch.ElapsedMilliseconds,
          _markdownRenderer.Render(hostResponse),
          null,
          null,
          hostReview.Summary,
          ContextUsage: contextUsage
        );
        yield break;
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
        var canRecover = failure is OllamaProviderException providerFailure
          && providerFailure.IsMemoryPressure
          && !progress.ReceivedFirstChunk
          && !string.Equals(
            selectedModel,
            settings.CoordinatorModel,
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
          $"Evicting resident coordinator model {settings.CoordinatorModel} for one adaptive retry.",
          stopwatch,
          settings.CoordinatorModel,
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
          $"Resident coordinator model {settings.CoordinatorModel} was temporarily evicted.",
          stopwatch,
          settings.CoordinatorModel,
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
            $"Reloading resident coordinator model {settings.CoordinatorModel}.",
            stopwatch,
            settings.CoordinatorModel,
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
              ? $"Resident coordinator model {settings.CoordinatorModel} was restored."
              : $"Resident coordinator model {settings.CoordinatorModel} could not be restored.",
            stopwatch,
            settings.CoordinatorModel,
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
          $"Reloading resident coordinator model {settings.CoordinatorModel}.",
          stopwatch,
          settings.CoordinatorModel,
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
            ? $"Resident coordinator model {settings.CoordinatorModel} was restored."
            : $"Resident coordinator model {settings.CoordinatorModel} could not be restored.",
          stopwatch,
          settings.CoordinatorModel,
          intention
        );
      }

      if (recoveryActive && recoveryTarget is not null)
      {
        yield return Event(
          requestId,
          "resident-model-reload-started",
          $"Reloading resident coordinator model {settings.CoordinatorModel}.",
          stopwatch,
          settings.CoordinatorModel,
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
            ? $"Resident coordinator model {settings.CoordinatorModel} was restored."
            : $"Resident coordinator model {settings.CoordinatorModel} could not be restored.",
          stopwatch,
          settings.CoordinatorModel,
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
    bool structuredCoordination,
    string? recoverySpecialistModel,
    int maximumPlanningAttempts,
    bool fallbackToResident,
    ApplicationSettings applicationSettings,
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
    var semanticRepairAttempted = false;
    string? semanticFailureFingerprint = null;

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
        CoordinatorContextFit? preflightFit = null;
        StructuredContextFit? structuredPreflightFit = null;
        var budget = GetCoordinatorInputBudget(applicationSettings, model);
        if (structuredCoordination)
        {
          structuredPreflightFit = _expertGuidance.FitToBudget(
            progress.Messages,
            _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
            budget.MaximumInputTokens
          );
          if (structuredPreflightFit.Compacted && !structuredPreflightFit.TooLarge && structuredPreflightFit.Outcome == "compacted")
          {
            ReplaceChatMessages(progress.Messages, structuredPreflightFit.Messages);
            ReplaceMessages(progress.ToolMessages, progress.Messages.Select(ToToolMessage).ToArray());
            yield return Event(
              requestId,
              "request-context-compacted",
              $"Structured coordinator context compacted deterministically from {structuredPreflightFit.BeforeTokens} to {structuredPreflightFit.AfterTokens} estimated input tokens.",
              stopwatch,
              model,
              intention
            ) with
            {
              IncidentContextFit = ContextFitView(structuredPreflightFit.AfterTokens, budget)
            };
          }
        }
        else
        {
          preflightFit = _actionPlanner.FitToBudget(
            progress.ToolMessages,
            _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
            budget.MaximumInputTokens,
            _executionSession?.Plan is null,
            attempt,
            completionAllowed
          );
          if (preflightFit.Compacted && !preflightFit.TooLarge && preflightFit.Outcome == "compacted")
          {
            ReplaceMessages(progress.ToolMessages, preflightFit.Messages);
            yield return Event(
              requestId,
              "request-context-compacted",
              $"Coordinator context compacted deterministically from {preflightFit.BeforeTokens} to {preflightFit.AfterTokens} estimated input tokens.",
              stopwatch,
              model,
              intention
            ) with
            {
              IncidentContextFit = ContextFitView(preflightFit, budget)
            };
          }
        }

        PlanningAttempt planning;
        if (preflightFit?.TooLarge == true || structuredPreflightFit?.TooLarge == true)
        {
          var beforeTokens = preflightFit?.BeforeTokens ?? structuredPreflightFit!.BeforeTokens;
          var coordinationUsageRole = CoordinationUsageRole(
            applicationSettings,
            model
          );
          planning = new PlanningAttempt(
            null,
            ContextItemTooLarge(model, beforeTokens, budget, coordinationUsageRole)
          );
        }
        else
        {
          planning = await TryPlanAsync(
            () => structuredCoordination
              ? PlanStructuredActionAsync(
                baseUri,
                model,
                progress,
                projectAwareness,
                CoordinationUsageRole(applicationSettings, model),
                cancellationToken
              )
              : _actionPlanner.PlanAsync(
                baseUri,
                model,
                progress.ToolMessages,
                _executionSession?.Plan is null,
                attempt,
                completionAllowed,
                UsageContext(
                  model,
                  CoordinationUsageRole(applicationSettings, model),
                  "local-action-planning"
                ),
                cancellationToken
              )
          );
        }

        if (planning.Failure is not null)
        {
          var planningFailureCategory = _planningFailureClassifier.Classify(
            planning.Failure
          );

          if (
            planningFailureCategory == CoordinatorFailureCategory.ContextFit
            && planning.Failure is OllamaRuntimeProfileException contextFailure
          )
          {
            var error = contextFailure.Error;
            if (!progress.ContextFailureCompactionAttempted)
            {
              progress.ContextFailureCompactionAttempted = true;
              var maximum = error.MaximumContextTokens ?? applicationSettings.Context.ProviderContextTokens;
              var reserved = error.ReservedOutputTokens ?? applicationSettings.Execution.MaxToolOutputTokens;
              var recoveryBefore = 0L;
              var recoveryAfter = 0L;
              var recoveryCompacted = false;
              var recoveryTooLarge = false;
              var recoveryOutcome = string.Empty;
              if (structuredCoordination)
              {
                var recoveryFit = _expertGuidance.FitToBudget(
                  progress.Messages,
                  _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
                  Math.Max(1, maximum - reserved)
                );
                recoveryBefore = recoveryFit.BeforeTokens;
                recoveryAfter = recoveryFit.AfterTokens;
                recoveryCompacted = recoveryFit.Compacted;
                recoveryTooLarge = recoveryFit.TooLarge;
                recoveryOutcome = recoveryFit.Outcome;
                if (recoveryCompacted && !recoveryTooLarge && recoveryOutcome == "compacted")
                {
                  ReplaceChatMessages(progress.Messages, recoveryFit.Messages);
                  ReplaceMessages(progress.ToolMessages, progress.Messages.Select(ToToolMessage).ToArray());
                }
              }
              else
              {
                var recoveryFit = _actionPlanner.FitToBudget(
                  progress.ToolMessages,
                  _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
                  Math.Max(1, maximum - reserved),
                  _executionSession?.Plan is null,
                  attempt,
                  completionAllowed
                );
                recoveryBefore = recoveryFit.BeforeTokens;
                recoveryAfter = recoveryFit.AfterTokens;
                recoveryCompacted = recoveryFit.Compacted;
                recoveryTooLarge = recoveryFit.TooLarge;
                recoveryOutcome = recoveryFit.Outcome;
                if (recoveryCompacted && !recoveryTooLarge && recoveryOutcome == "compacted")
                {
                  ReplaceMessages(progress.ToolMessages, recoveryFit.Messages);
                }
              }
              if (
                recoveryCompacted
                && !recoveryTooLarge
                && recoveryAfter < recoveryBefore
                && recoveryOutcome == "compacted"
              )
              {
                yield return Event(
                  requestId,
                  "request-context-compaction-retry",
                  $"Context-fit failure triggered the single smaller retry: {recoveryBefore} -> {recoveryAfter} estimated input tokens.",
                  stopwatch,
                  model,
                  intention
                ) with
                {
                  IncidentContextFit = new IncidentContextFitView(
                    checked((int)Math.Min(int.MaxValue, recoveryAfter)),
                    reserved,
                    checked((int)Math.Min(int.MaxValue, recoveryAfter + reserved)),
                    maximum,
                    error.EffectiveContextTokens
                  )
                };
                continue;
              }
            }

            yield return Event(
              requestId,
              "request-context-exhausted",
              "The coordinator context remained too large after the single bounded compaction strategy.",
              stopwatch,
              model,
              intention
            ) with
            {
              IncidentContextFit = new IncidentContextFitView(
                error.EstimatedInputTokens,
                error.ReservedOutputTokens,
                error.RequiredContextTokens,
                error.MaximumContextTokens,
                error.EffectiveContextTokens
              )
            };

            if ((_executionSession?.CompletedActionCount ?? 0) > 0)
            {
              progress.PartialContextExhausted = true;
            }
            else if (contextFailure.Error.Code == "context-item-too-large")
            {
              progress.Failure = ToChatException(contextFailure, model, intention);
            }
            else if (fallbackToResident)
            {
              progress.PlanningFailure = contextFailure;
            }
            else
            {
              progress.Failure = ToChatException(contextFailure, model, intention);
            }
            yield break;
          }

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

        if (
          !progress.RuntimeContextReported
          && planningResult.ContextResolution is not null
        )
        {
          foreach (var contextEvent in RuntimeContextEvents(
            requestId,
            stopwatch,
            model,
            intention,
            planningResult.ContextResolution
          ))
          {
            yield return contextEvent;
          }

          progress.RuntimeContextReported = true;
        }

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

        if (
          proposal.OriginalTool is not null
          && !string.Equals(
            proposal.OriginalTool,
            proposal.Tool,
            StringComparison.Ordinal
          )
        )
        {
          yield return Event(
            requestId,
            "action.tool-name-normalized",
            $"Tool name normalized: {proposal.OriginalTool} -> {proposal.Tool} "
              + $"({FormatToolResolutionSource(proposal.ToolResolutionSource)}).",
            stopwatch,
            model,
            intention
          );
        }

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
          _executionSession?.RecordToolNameResolution(
            proposal,
            planFailure is null
              ? "accepted"
              : "rejected"
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

          _executionSession?.RecordToolNameResolution(
            proposal,
            validation.Failure is null
              ? "accepted"
              : "rejected"
          );
        }

        if (validation.Failure is null)
        {
          planningFailures = 0;
          semanticRepairAttempted = false;
          semanticFailureFingerprint = null;
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

        var semanticFingerprint = string.Concat(
          proposal.Tool,
          ":",
          proposal.Arguments.GetRawText(),
          ":",
          exception.Stage,
          ":",
          exception.Message
        );

        if (semanticRepairAttempted)
        {
          var repeated = string.Equals(
            semanticFailureFingerprint,
            semanticFingerprint,
            StringComparison.Ordinal
          );
          exhaustedFailure = exception;
          planningFailures = maximumPlanningAttempts;
          _executionSession?.RecordPlanningFailure();
          yield return Event(
            requestId,
            "agent.coordination-path-change-required",
            repeated
              ? $"Coordinator {model} repeated the identical rejected semantic proposal; the current path is disabled for this turn."
              : $"Coordinator {model} exhausted its single semantic repair attempt; a different coordination path is required.",
            stopwatch,
            model,
            intention
          );

          if (fallbackToResident)
          {
            progress.PlanningFailure = exception;
            yield break;
          }

          break;
        }

        semanticRepairAttempted = true;
        semanticFailureFingerprint = semanticFingerprint;
        planningFailures++;
        _executionSession?.RecordPlanningFailure();
        exhaustedFailure = exception;
        var correction = "STRUCTURED_ACTION_CORRECTION\n"
          + $"Tool: {proposal.Tool}\n"
          + $"Rejected contract: {exception.Stage}\n"
          + $"Reason: {exception.Message}\n"
          + "Expected: propose one registered tool with a complete JSON object containing every required non-empty field. "
          + "The rejected action was not executed. Return one materially different corrected proposal.";
        progress.Messages.Add(
          new ChatMessage(
            "user",
            correction
          )
        );
        progress.ToolMessages.Add(
          new OllamaToolMessage(
            "user",
            correction
          )
        );
        yield return Event(
          requestId,
          "action.semantic-repair-requested",
          $"Host rejected {proposal.Tool} and supplied one bounded correction for {exception.Stage}: {exception.Message}",
          stopwatch,
          model,
          intention
        );
        continue;

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

      var planStepStarted = _executionSession?.RecordPlanActionStarted(
        action.ActionId,
        action.Tool
      ) == true;
      if (planStepStarted)
      {
        yield return Event(
          requestId,
          "execution-step-started",
          $"Compatible plan step started from the proposed canonical tool: {action.Summary}.",
          stopwatch,
          model,
          intention
        );
      }
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
          action,
          _executionSession!.BrowserSessionId,
          _executionSession.Id,
          (
            pendingAction,
            editedText,
            revisionCancellationToken
          ) => ValidateApprovalRevisionAsync(
            pendingAction,
            editedText,
            _executionSession,
            revisionCancellationToken
          ),
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
        var approvalOutcome = await decisionTask;
        action = approvalOutcome.Action;

        if (approvalOutcome.Revised)
        {
          _executionSession.RecordAction(
            action,
            "revised",
            "The pending command was edited by the user and revalidated by the Host."
          );
          yield return ActionEvent(
            requestId,
            "action.revised",
            $"Edited command validated: {action.Summary}.",
            stopwatch,
            model,
            intention,
            action,
            "revised",
            true
          );
        }

        if (!approvalOutcome.Approved)
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
          var planStepBlocked = _executionSession.RecordPlanActionResult(
            action.ActionId,
            action.Tool,
            "blocked"
          );
          if (planStepBlocked)
          {
            yield return Event(
              requestId,
              "execution-step-blocked",
              $"Plan step blocked because the action was rejected: {action.Summary}.",
              stopwatch,
              model,
              intention
            );
          }
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
          requiresApproval,
          result.Output
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
        var planStepCompleted = _executionSession?.RecordPlanActionResult(
          action.ActionId,
          action.Tool,
          "completed"
        ) == true;
        yield return Event(
          requestId,
          planStepCompleted
            ? "execution-step-completed"
            : "execution-step-effect-unmatched",
          planStepCompleted
            ? $"Plan step completed from a compatible verified effect: {action.Summary}."
            : $"Verified action did not advance any plan step because no compatible expected effect was pending: {action.Summary}.",
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
          requiresApproval,
          failureOutput
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
          var conflictStepBlocked = _executionSession?.RecordPlanActionResult(
            action.ActionId,
            action.Tool,
            "blocked"
          ) == true;
          if (conflictStepBlocked)
          {
            yield return Event(
              requestId,
              "execution-step-blocked",
              $"Plan step blocked because the target changed outside this execution: {action.Summary}.",
              stopwatch,
              model,
              intention
            );
          }
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
      catch (OllamaRuntimeProfileException exception)
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

      if (
        !progress.RuntimeContextReported
        && update.ContextResolution is not null
      )
      {
        foreach (var contextEvent in RuntimeContextEvents(
          requestId,
          stopwatch,
          model,
          intention,
          update.ContextResolution
        ))
        {
          yield return contextEvent;
        }

        progress.RuntimeContextReported = true;
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

      if (!TryAcceptResponseDelta(
        progress,
        update.Delta,
        out var safeDelta,
        out var rejectedMarker
      ))
      {
        if (rejectedMarker is not null)
        {
          progress.Failure = new LocalActionException(
            "reserved-assistant-marker",
            $"The model response was rejected because it began with reserved Host marker '{rejectedMarker}'."
          );
          yield return Event(
            requestId,
            "response.reserved-marker-rejected",
            "A model response attempted to use a Host-reserved protocol marker and was not exposed as assistant content.",
            stopwatch,
            model,
            intention
          );
          yield break;
        }

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
        safeDelta
      );
      yield return new ChatStreamEvent(
        requestId,
        "response.delta",
        DateTimeOffset.UtcNow,
        null,
        safeDelta,
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

    if (progress.PrefixBuffer.Length > 0 && !progress.PrefixResolved)
    {
      var finalPrefix = progress.PrefixBuffer.ToString();
      progress.PrefixBuffer.Clear();
      progress.PrefixResolved = true;
      progress.ReceivedFirstChunk = true;
      progress.Answer.Append(finalPrefix);
      yield return new ChatStreamEvent(
        requestId,
        "response.delta",
        DateTimeOffset.UtcNow,
        null,
        finalPrefix,
        model,
        intention,
        stopwatch.ElapsedMilliseconds,
        _markdownRenderer.Render(progress.Answer.ToString()),
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
    catch (OllamaRuntimeProfileException exception)
    {
      return new GuidanceAttempt(null, exception);
    }
  }

  private async Task<LocalActionPlanningResult> PlanStructuredActionAsync(
    Uri baseUri,
    string model,
    ExecutionProgress progress,
    ProjectAwarenessSettings projectAwareness,
    string usageModelRole,
    CancellationToken cancellationToken
  )
  {
    if (progress.PendingStructuredProposal is not null)
    {
      var pending = progress.PendingStructuredProposal;
      progress.PendingStructuredProposal = null;
      return new LocalActionPlanningResult(
        pending,
        new OllamaToolMessage(
          "assistant",
          "Host-owned plan accepted; validating the pending structured action."
        ),
        false
      );
    }

    var guidance = await _expertGuidance.PrepareAsync(
      baseUri,
      model,
      progress.Messages,
      UsageContext(
        model,
        usageModelRole,
        "structured-action-coordination"
      ),
      cancellationToken
    );
    progress.Guidance = guidance;
    var assistantMessage = new OllamaToolMessage(
      "assistant",
      ExpertExecutionGuidanceService.Serialize(
        guidance
      )
    );

    if (!guidance.ActionRequired)
    {
      return new LocalActionPlanningResult(
        null,
        assistantMessage,
        true
      );
    }

    var action = guidance.Actions.Single();
    var structuredProposal = new LocalActionProposal(
      action.Tool,
      action.Arguments.Clone(),
      action.Title,
      action.OriginalTool,
      action.ToolResolutionSource
    );
    var currentPlan = _executionSession?.Plan;
    var requiresHostPlan = currentPlan is null
      || currentPlan.Steps.All(
        step => step.Status is "completed" or "failed" or "blocked"
      );

    if (!requiresHostPlan)
    {
      return new LocalActionPlanningResult(
        structuredProposal,
        assistantMessage,
        false
      );
    }

    var titles = currentPlan?.Steps.Select(
      step => step.Title
    ).ToList() ?? [];
    var normalizedTitle = action.Title.Trim();

    if (titles.Contains(
      normalizedTitle,
      StringComparer.Ordinal
    ))
    {
      const string suffix = " (next)";
      normalizedTitle = normalizedTitle.Length + suffix.Length <= 100
        ? normalizedTitle + suffix
        : normalizedTitle[..(100 - suffix.Length)] + suffix;
    }

    titles.Add(
      normalizedTitle
    );

    if (titles.Count > projectAwareness.MaxPlanSteps)
    {
      throw new LocalActionException(
        "execution-plan",
        $"Structured coordination exceeded the {projectAwareness.MaxPlanSteps}-step visible plan limit."
      );
    }

    progress.PendingStructuredProposal = structuredProposal;
    var planArguments = JsonSerializer.SerializeToElement(
      new
      {
        objective = guidance.Objective,
        steps = titles.Select(
          title => new
          {
            title
          }
        ).ToArray()
      }
    );
    return new LocalActionPlanningResult(
      new LocalActionProposal(
        currentPlan is null
          ? "create_execution_plan"
          : "revise_execution_plan",
        planArguments,
        "Host-normalized structured coordination plan"
      ),
      assistantMessage,
      false
    );
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
    catch (OllamaRuntimeProfileException exception)
    {
      return new PlanningAttempt(null, exception);
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
    OllamaRuntimeProfileException exception,
    string? model,
    string? intention
  )
  {
    return new ChatStageException(
      exception.Error.Stage,
      exception.Error.Message,
      exception.Error.Diagnostic,
      model ?? exception.Error.Model,
      intention,
      exception.Error.Code == "request-context-does-not-fit"
        ? 413
        : 400,
      exception.Error.Retryable,
      exception,
      new Dictionary<string, string?>(
        StringComparer.Ordinal
      )
      {
        ["code"] = exception.Error.Code,
        ["providerTraceId"] = exception.Error.TraceId,
        ["role"] = exception.Error.Role,
        ["requestedContext"] = exception.Error.RequestedContext?.ToString(),
        ["actualContext"] = exception.Error.ActualContext?.ToString(),
        ["estimatedInputTokens"] = exception.Error.EstimatedInputTokens?.ToString(),
        ["reservedOutputTokens"] = exception.Error.ReservedOutputTokens?.ToString(),
        ["requiredContextTokens"] = exception.Error.RequiredContextTokens?.ToString(),
        ["maximumContextTokens"] = exception.Error.MaximumContextTokens?.ToString(),
        ["effectiveContextTokens"] = exception.Error.EffectiveContextTokens?.ToString()
      },
      exception.Error.Provider
    );
  }

  private static ChatStageException ToChatException(
    Exception exception,
    string? model,
    string? intention
  )
  {
    return exception switch
    {
      OllamaProviderException provider => ToChatException(
        provider,
        model,
        intention
      ),
      OllamaRuntimeProfileException runtime => ToChatException(
        runtime,
        model,
        intention
      ),
      LocalActionException local => ToChatException(
        local,
        model,
        intention
      ),
      ChatStageException stage => stage,
      _ => new ChatStageException(
        "generation",
        "The model request could not be completed.",
        exception.Message,
        model,
        intention,
        500,
        false,
        exception
      )
    };
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

  private async Task<ApprovalRevisionValidation> ValidateApprovalRevisionAsync(
    ValidatedLocalAction currentAction,
    string editedText,
    ExecutionSession executionSession,
    CancellationToken cancellationToken
  )
  {
    if (!IsEditableApprovalAction(
      currentAction
    ))
    {
      return new ApprovalRevisionValidation(
        false,
        null,
        "This action does not expose an editable command contract."
      );
    }

    try
    {
      if (string.IsNullOrWhiteSpace(editedText) || editedText.Length > 64_000)
      {
        throw new LocalActionException(
          "approval-revision",
          "The edited action must contain between 1 and 64,000 characters."
        );
      }

      var revisedArguments = currentAction.Arguments.EnumerateObject().ToDictionary(
        property => property.Name,
        property => property.Value.Clone(),
        StringComparer.Ordinal
      );

      if (currentAction.Tool == "run_process")
      {
        var tokens = ParseApprovalTokens(
          editedText
        );

        if (tokens.Count == 0)
        {
          throw new LocalActionException(
            "approval-revision",
            "The edited command must include an executable."
          );
        }

        revisedArguments["executable"] = JsonSerializer.SerializeToElement(
          tokens[0]
        );
        revisedArguments["arguments"] = JsonSerializer.SerializeToElement(
          tokens.Skip(
            1
          ).ToArray()
        );
      }
      else if (IsPathListApprovalAction(currentAction))
      {
        var paths = editedText.Split(
          '\n',
          StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
        );

        if (paths.Length == 0)
        {
          throw new LocalActionException(
            "approval-revision",
            "The edited Git action must include at least one repository-relative path."
          );
        }

        revisedArguments["paths"] = JsonSerializer.SerializeToElement(
          paths
        );
      }
      else
      {
        using var document = JsonDocument.Parse(editedText);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
          throw new LocalActionException(
            "approval-revision",
            "The edited structured arguments must be a JSON object."
          );
        }

        revisedArguments = document.RootElement.EnumerateObject().ToDictionary(
          property => property.Name,
          property => property.Value.Clone(),
          StringComparer.Ordinal
        );
      }

      var proposal = new LocalActionProposal(
        currentAction.Tool,
        JsonSerializer.SerializeToElement(
          revisedArguments
        ),
        "Edited by the user before approval.",
        currentAction.OriginalTool,
        currentAction.ToolResolutionSource
      );
      var revised = await _actionService.ValidateAsync(
        proposal,
        executionSession,
        cancellationToken
      );
      return new ApprovalRevisionValidation(
        true,
        revised with
        {
          ActionId = currentAction.ActionId
        }
      );
    }
    catch (LocalActionException exception)
    {
      return new ApprovalRevisionValidation(
        false,
        null,
        exception.Message
      );
    }
    catch (JsonException exception)
    {
      return new ApprovalRevisionValidation(
        false,
        null,
        $"The edited structured arguments are not valid JSON: {exception.Message}"
      );
    }
  }

  private static IReadOnlyList<string> ParseApprovalTokens(
    string value
  )
  {
    if (
      string.IsNullOrWhiteSpace(
        value
      )
      || value.Length > 4_000
    )
    {
      throw new LocalActionException(
        "approval-revision",
        "The edited command must contain between 1 and 4,000 characters."
      );
    }

    var tokens = new List<string>();
    var current = new StringBuilder();
    char? quote = null;
    var tokenStarted = false;

    for (var index = 0; index < value.Length; index++)
    {
      var character = value[index];

      if (
        character == '\\'
        && index + 1 < value.Length
        && quote is not null
        && (value[index + 1] == quote || value[index + 1] == '\\')
      )
      {
        current.Append(
          value[index + 1]
        );
        tokenStarted = true;
        index++;
        continue;
      }

      if (character is '\'' or '"')
      {
        if (quote is null)
        {
          quote = character;
          tokenStarted = true;
          continue;
        }

        if (quote == character)
        {
          quote = null;
          continue;
        }
      }

      if (
        char.IsWhiteSpace(
          character
        )
        && quote is null
      )
      {
        if (tokenStarted)
        {
          tokens.Add(
            current.ToString()
          );
          current.Clear();
          tokenStarted = false;
        }
        continue;
      }

      current.Append(
        character
      );
      tokenStarted = true;
    }

    if (quote is not null)
    {
      throw new LocalActionException(
        "approval-revision",
        "The edited command contains an unterminated quoted argument."
      );
    }

    if (tokenStarted)
    {
      tokens.Add(
        current.ToString()
      );
    }

    if (tokens.Count > 128)
    {
      throw new LocalActionException(
        "approval-revision",
        "The edited command exceeds the 128-argument limit."
      );
    }

    return tokens;
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
    bool requiresApproval,
    string? resultOutput = null
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
        action.PendingFileChange?.UndoAvailable == true
          || action.PendingFileChanges?.All(change => change.UndoAvailable) == true,
        action.PendingFileChange?.UndoDiagnostic
          ?? action.PendingFileChanges?.FirstOrDefault(
            change => !change.UndoAvailable
          )?.UndoDiagnostic,
        action.OriginalTool,
        action.ToolResolutionSource,
        IsEditableApprovalAction(
          action
        ),
        IsEditableApprovalAction(
          action
        )
          ? GetEditableApprovalText(
            action
          )
          : null,
        resultOutput
      ),
      _executionSession?.CreateSummary()
    );
  }

  private static bool IsEditableApprovalAction(
    ValidatedLocalAction action
  )
  {
    return action.Tool is
      "create_file"
      or "write_file"
      or "replace_text"
      or "apply_patch"
      or "delete_files"
      or "create_directory"
      or "run_process"
      or "git_stage_files"
      or "git_unstage_files";
  }

  private static bool IsPathListApprovalAction(
    ValidatedLocalAction action
  )
  {
    return action.Tool is "delete_files" or "git_stage_files" or "git_unstage_files";
  }

  private static string GetEditableApprovalText(
    ValidatedLocalAction action
  )
  {
    if (action.Tool == "run_process")
    {
      var executable = action.Arguments.GetProperty(
        "executable"
      ).GetString() ?? string.Empty;
      var arguments = action.Arguments.TryGetProperty(
        "arguments",
        out var argumentElement
      )
        ? argumentElement.EnumerateArray().Select(
          argument => argument.GetString() ?? string.Empty
        )
        : [];
      return string.Join(
        " ",
        new[]
        {
          executable
        }.Concat(
          arguments
        ).Select(
          QuoteApprovalToken
        )
      );
    }

    if (IsPathListApprovalAction(action))
    {
      return action.Arguments.TryGetProperty(
        "paths",
        out var pathElement
      )
        ? string.Join(
          "\n",
          pathElement.EnumerateArray().Select(
            path => path.GetString() ?? string.Empty
          )
        )
        : action.Preview ?? string.Empty;
    }

    return JsonSerializer.Serialize(
      action.Arguments,
      new JsonSerializerOptions
      {
        WriteIndented = true
      }
    );
  }

  private static string QuoteApprovalToken(
    string value
  )
  {
    if (
      value.Length > 0
      && !value.Any(
        character => char.IsWhiteSpace(
          character
        ) || character is '\'' or '"'
      )
    )
    {
      return value;
    }

    return $"\"{value.Replace(
      "\\",
      "\\\\",
      StringComparison.Ordinal
    ).Replace(
      "\"",
      "\\\"",
      StringComparison.Ordinal
    )}\"";
  }

  private static string FormatToolResolutionSource(
    string source
  )
  {
    return source switch
    {
      ToolNameResolver.CuratedAliasSource => "curated alias",
      ToolNameResolver.CanonicalCaseSource => "ordinal case-insensitive canonical",
      _ => "canonical"
    };
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

  private static List<ChatMessage> CompactCoordinatorMessages(
    IReadOnlyList<ChatMessage> messages
  )
  {
    var compact = new List<ChatMessage>();
    var projectContext = messages.LastOrDefault(
      message => message.Role == "system"
        && message.Content.StartsWith(
          "APPLICATION_OWNED_PROJECT_CONTEXT",
          StringComparison.Ordinal
        )
    );
    var objective = messages.LastOrDefault(
      message => message.Role == "user"
        && !IsGuidanceMessage(
          message
        )
        && !IsCoordinatorControlMessage(
          message.Content
        )
    );
    var guidance = messages.LastOrDefault(
      IsGuidanceMessage
    );

    if (projectContext is not null)
    {
      compact.Add(
        projectContext
      );
    }

    if (objective is not null)
    {
      compact.Add(
        objective
      );
    }

    if (guidance is not null)
    {
      compact.Add(
        guidance
      );
    }

    return compact.Count > 0
      ? compact
      : messages.TakeLast(
        2
      ).ToList();
  }

  private static List<OllamaToolMessage> CompactCoordinatorToolMessages(
    IReadOnlyList<OllamaToolMessage> messages
  )
  {
    var context = messages.Where(
      message => message.Role == "system"
        && message.Content?.StartsWith(
          "APPLICATION_OWNED_PROJECT_CONTEXT",
          StringComparison.Ordinal
        ) == true
    ).TakeLast(
      1
    );
    var objective = messages.Where(
      message => message.Role == "user"
        && message.Content?.StartsWith(
          ExpertExecutionGuidanceService.GuidanceMarker,
          StringComparison.Ordinal
        ) != true
        && !IsCoordinatorControlMessage(
          message.Content
        )
    ).TakeLast(
      1
    );
    var guidance = messages.Where(
      message => message.Content?.StartsWith(
        ExpertExecutionGuidanceService.GuidanceMarker,
        StringComparison.Ordinal
      ) == true
    ).TakeLast(
      1
    );
    var actionState = messages.Where(
      message => message.Role is "assistant" or "tool"
    ).TakeLast(
      12
    );

    return context
      .Concat(
        objective
      )
      .Concat(
        guidance
      )
      .Concat(
        actionState
      )
      .ToList();
  }

  private CoordinatorInputBudget GetCoordinatorInputBudget(
    ApplicationSettings settings,
    string model
  )
  {
    _usageModelRevisions.TryGetValue(model, out var digest);
    var resolution = OllamaRuntimeProfileResolver.Resolve(
      settings,
      model,
      digest,
      CoordinationUsageRole(settings, model),
      null,
      0,
      settings.Execution.MaxToolOutputTokens
    );
    return new CoordinatorInputBudget(
      Math.Max(1, resolution.MaximumContextTokens - resolution.OutputTokenLimit),
      resolution.MaximumContextTokens,
      resolution.OutputTokenLimit,
      resolution.EffectiveContextTokens
    );
  }

  private static IncidentContextFitView ContextFitView(
    CoordinatorContextFit fit,
    CoordinatorInputBudget budget
  )
  {
    return ContextFitView(fit.AfterTokens, budget);
  }

  private static IncidentContextFitView ContextFitView(
    long inputTokens,
    CoordinatorInputBudget budget
  )
  {
    return new IncidentContextFitView(
      checked((int)Math.Min(int.MaxValue, inputTokens)),
      budget.ReservedOutputTokens,
      checked((int)Math.Min(int.MaxValue, inputTokens + budget.ReservedOutputTokens)),
      budget.MaximumContextTokens,
      budget.EffectiveContextTokens
    );
  }

  private static OllamaRuntimeProfileException ContextItemTooLarge(
    string model,
    long beforeTokens,
    CoordinatorInputBudget budget,
    string usageModelRole
  )
  {
    return new OllamaRuntimeProfileException(
      "context-item-too-large",
      "A required coordinator context item does not fit the configured context budget.",
      "request-context-fit",
      model,
      null,
      usageModelRole,
      checked((int)Math.Min(int.MaxValue, beforeTokens + budget.ReservedOutputTokens)),
      null,
      false,
      "Required coordinator state remained larger than the maximum after deterministic compaction.",
      estimatedInputTokens: checked((int)Math.Min(int.MaxValue, beforeTokens)),
      reservedOutputTokens: budget.ReservedOutputTokens,
      requiredContextTokens: checked((int)Math.Min(int.MaxValue, beforeTokens + budget.ReservedOutputTokens)),
      maximumContextTokens: budget.MaximumContextTokens,
      effectiveContextTokens: budget.MaximumContextTokens
    );
  }

  private static string CoordinationUsageRole(
    ApplicationSettings settings,
    string model
  ) => string.Equals(
    model,
    settings.ActionModel,
    StringComparison.OrdinalIgnoreCase
  )
    ? UsageModelRoles.Action
    : UsageModelRoles.Coordinator;

  private static void ReplaceMessages(
    List<OllamaToolMessage> target,
    IReadOnlyList<OllamaToolMessage> replacement
  )
  {
    target.Clear();
    target.AddRange(replacement);
  }

  private static void ReplaceChatMessages(
    List<ChatMessage> target,
    IReadOnlyList<ChatMessage> replacement
  )
  {
    target.Clear();
    target.AddRange(replacement);
  }

  private IReadOnlyList<ChatStreamEvent> RuntimeContextEvents(
    string requestId,
    Stopwatch stopwatch,
    string model,
    string? intention,
    OllamaContextResolution resolution
  )
  {
    var events = new List<ChatStreamEvent>
    {
      Event(
        requestId,
        resolution.Overridden
          ? "runtime-profile-overridden"
          : "runtime-profile-inherited",
        resolution.Overridden
          ? $"Applied the exact {resolution.Model}@{resolution.Digest} override for role {resolution.Role}."
          : $"Inherited the {resolution.Role} runtime profile for {resolution.Model}.",
        stopwatch,
        model,
        intention
      ),
      Event(
        requestId,
        "request-context-fit-evaluated",
        $"Request fit: {resolution.RequiredContextTokens} required, "
          + $"{resolution.EffectiveContextTokens} context tokens selected, "
          + $"{resolution.OutputTokenLimit} output tokens reserved.",
        stopwatch,
        model,
        intention
      )
    };

    if (resolution.Escalated)
    {
      events.Add(
        Event(
          requestId,
          "request-context-escalated",
          $"Context escalated from {resolution.TargetContextTokens} to "
            + $"{resolution.EffectiveContextTokens} tokens for this request.",
          stopwatch,
          model,
          intention
        )
      );
    }

    return events;
  }

  private static bool IsCoordinatorControlMessage(
    string? content
  )
  {
    return content is null
      || new[]
      {
        "LOCAL_ACTION_RESULT",
        "STRUCTURED_ACTION_CORRECTION",
        "EXECUTION_COMPLETION_REJECTED",
        "RECOVERY_",
        "RESIDENT_",
        "AUTHORITATIVE_EXECUTION_SESSION_FACTS"
      }.Any(
        marker => content.StartsWith(
          marker,
          StringComparison.Ordinal
        )
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

    if (!_executionSession!.CanCompletePlan())
    {
      return false;
    }

    var completedActions = _executionSession.CompletedActionCount;
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
      ? _executionSession?.RequiresMutation == true
        && _executionSession.HasVerifiedMutation == false
        ? "The objective requires a mutation, but the Host has no verified mutation effect. Revise the plan and call an edit, create, delete, directory, or Git mutation tool."
        : "No valid completed execution plan is stored. Call create_execution_plan with the required remaining work."
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
      revision,
      TraceId: _trace.TraceId,
      ProviderAttemptId: Guid.NewGuid().ToString("N"),
      IncidentEventId: Guid.NewGuid().ToString("N"),
      IncidentSequence: _trace.NextSequence()
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
      "verified-mutation-no-file-artifacts" => "A non-file workspace mutation was completed and verified.",
      "partial-context-exhausted" => "Execution stopped after bounded context compaction; completed work remains available for review.",
      "validation-passed-no-files-changed" => "Validation passed; no files were changed.",
      "blocked-validation-not-configured" => "Validation was requested, but no validation profile is configured.",
      "blocked-validation-not-run" => "Validation was requested, but it did not run.",
      "blocked-mutation-not-performed" => "The objective required a mutation, but no verified mutation occurred.",
      _ => "Inspected only; no files were changed."
    };
    return $"**Authoritative execution status:** {text}";
  }

  private static string CreateHostExecutionResponse(
    ExecutionSessionReview review
  )
  {
    var builder = new StringBuilder();
    builder.AppendLine(CreateAuthoritativeStatus(review.Summary.CompletionStatus));
    builder.AppendLine();

    if (review.Files.Count == 0)
    {
      builder.AppendLine("No files were changed.");
    }
    else
    {
      builder.AppendLine("Files verified by the Host:");
      foreach (var file in review.Files)
      {
        builder.AppendLine($"- {file.Operation}: `{file.RelativePath}`");
      }
    }

    builder.AppendLine();
    builder.AppendLine($"Validation: {review.Validation?.State ?? "not-run"}.");

    if (review.Warnings.Count > 0)
    {
      builder.AppendLine();
      builder.AppendLine("Warnings:");
      foreach (var warning in review.Warnings.Take(10))
      {
        builder.AppendLine($"- {warning}");
      }
    }

    return builder.ToString().TrimEnd();
  }

  private static bool TryAcceptResponseDelta(
    GenerationProgress progress,
    string delta,
    out string safeDelta,
    out string? rejectedMarker
  )
  {
    safeDelta = string.Empty;
    rejectedMarker = null;

    if (progress.PrefixResolved)
    {
      safeDelta = delta;
      return true;
    }

    progress.PrefixBuffer.Append(delta);
    var candidate = progress.PrefixBuffer.ToString();
    rejectedMarker = ReservedAssistantMarkers.FirstOrDefault(
      marker => candidate.StartsWith(marker, StringComparison.Ordinal)
    );

    if (rejectedMarker is not null)
    {
      progress.PrefixBuffer.Clear();
      return false;
    }

    if (ReservedAssistantMarkers.Any(
      marker => marker.StartsWith(candidate, StringComparison.Ordinal)
    ))
    {
      return false;
    }

    progress.PrefixResolved = true;
    safeDelta = candidate;
    progress.PrefixBuffer.Clear();
    return true;
  }

  private static readonly string[] ReservedAssistantMarkers =
  [
    "LOCAL_ACTION_RESULT",
    "STRUCTURED_ACTION_CORRECTION",
    "EXECUTION_COMPLETION_REJECTED",
    "RECOVERY_",
    "RESIDENT_",
    "AUTHORITATIVE_EXECUTION_SESSION_FACTS"
  ];

  private sealed class GenerationProgress
  {
    public StringBuilder Answer { get; } = new();

    public StringBuilder PrefixBuffer { get; } = new();

    public bool PrefixResolved { get; set; }

    public bool ReceivedFirstChunk { get; set; }

    public Exception? Failure { get; set; }

    public IReadOnlyList<ProviderCitation>? Citations { get; set; }

    public ProviderTokenUsage? Usage { get; set; }

    public bool RuntimeContextReported { get; set; }

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

    public LocalActionProposal? PendingStructuredProposal { get; set; }

    public int RecoveryAttemptCount { get; set; }

    public int AutomaticStrategyRevisionCount { get; set; }

    public bool RuntimeContextReported { get; set; }

    public bool ContextFailureCompactionAttempted { get; set; }

    public bool PartialContextExhausted { get; set; }
  }

  private sealed record PlanningAttempt(
    LocalActionPlanningResult? Result,
    Exception? Failure
  );

  private sealed record CoordinatorInputBudget(
    int MaximumInputTokens,
    int MaximumContextTokens,
    int ReservedOutputTokens,
    int EffectiveContextTokens
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
