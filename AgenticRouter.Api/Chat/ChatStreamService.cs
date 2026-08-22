using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
  private const int MaximumIdenticalStrategyAttempts = 5;
  private const int MaximumToolsetRequestsPerTurn = 4;

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
  private readonly ISpecialistToolingProfileResolver _toolingProfiles;
  private readonly ISpecialistToolingProtocol _toolingProtocol;
  private readonly IToolNameResolver _toolNames;
  private readonly IFunctionGemmaResidentProtocol _functionGemma;
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
  private readonly IHarnessRegistry _harnesses;
  private readonly ILogger<ChatStreamService> _logger;
  private readonly ITraceContext _trace;
  private ExecutionSession? _executionSession;
  private string? _usageWorkspaceId;
  private string? _usageConversationId;
  private string? _usageTurnId;
  private string? _usageGpu;
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
    ISpecialistToolingProfileResolver toolingProfiles,
    ISpecialistToolingProtocol toolingProtocol,
    IToolNameResolver toolNames,
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
    IFunctionGemmaResidentProtocol functionGemma,
    IWorkspaceProfileService workspaceProfiles,
    IImageAttachmentValidator imageValidator,
    ICloudImageApprovalStore cloudImageApprovals,
    IHarnessRegistry harnesses,
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
    _toolingProfiles = toolingProfiles;
    _toolingProtocol = toolingProtocol;
    _toolNames = toolNames;
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
    _functionGemma = functionGemma;
    _workspaceProfiles = workspaceProfiles;
    _imageValidator = imageValidator;
    _cloudImageApprovals = cloudImageApprovals;
    _harnesses = harnesses;
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
      var isAuto = string.IsNullOrWhiteSpace(
        request.Model
      ) || string.Equals(
        request.Model,
        "auto",
        StringComparison.OrdinalIgnoreCase
      );
      var functionGemmaProtocolActive = string.Equals(
        request.InteractionMode,
        "execute",
        StringComparison.Ordinal
      ) && isAuto && _functionGemma.Supports(
        settings.ActionModel
      );
      IReadOnlyList<FunctionGemmaTeacher> functionGemmaTeacherCatalog =
        Array.Empty<FunctionGemmaTeacher>();
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
      var intention = GeneralChat;
      var selectedModel = request.Model.Trim();
      var selectedModelRole = UsageModelRoles.Primary;
      var images = _imageValidator.Validate(
        request.Images
      );

      if (!isAuto)
      {
        var explicitReference = ProviderModelReference.Parse(selectedModel);
        var explicitHarness = GetHarnessDefinition(request.Harness);
        if (
          string.Equals(request.InteractionMode, "execute", StringComparison.Ordinal)
          && explicitHarness.SupportedProviders is { Count: > 0 }
          && !explicitHarness.SupportedProviders.Contains(
            explicitReference.ProviderId,
            StringComparer.OrdinalIgnoreCase
          )
        )
        {
          throw new ChatStageException(
            $"{explicitHarness.Id}-provider-unsupported",
            HarnessProviderUnsupportedMessage(explicitHarness),
            $"Selected provider '{explicitReference.ProviderId}' is outside the {explicitHarness.Id} harness scope.",
            selectedModel,
            null,
            400,
            false,
            provider: explicitReference.ProviderId
          );
        }

        yield return Event(
          requestId,
          "model.explicit-selected",
          "Manual model override.",
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
              ["selectionMode"] = "manual",
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
        else if (
          functionGemmaProtocolActive
          && _functionGemma.Supports(
            settings.RouterModel
          )
        )
        {
          yield return Event(
            requestId,
            "router.functiongemma-contract-deferred",
            $"Router {settings.RouterModel} uses the trained route_to_teacher contract in Execute; the incompatible generic intention parser was bypassed.",
            stopwatch,
            settings.RouterModel,
            intention
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

      _usageGpu = isAuto
        ? settings.Intentions[intention].Gpu
        : settings.DefaultGpu;

      if (functionGemmaProtocolActive)
      {
        if (ContainsModel(
          models,
          settings.ActionModel
        ))
        {
          functionGemmaTeacherCatalog = _functionGemma.CreateTeacherCatalog(
            models,
            selectedModel,
            intention
          );
          yield return Event(
            requestId,
            "agent.functiongemma-routing-started",
            $"Resident {settings.ActionModel} is applying the trained {FunctionGemmaResidentProtocol.RouteTool} contract.",
            stopwatch,
            settings.ActionModel,
            intention
          );

          FunctionGemmaRouteDecision? residentRoute = null;
          Exception? routeFailure = null;
          try
          {
            residentRoute = await _functionGemma.RouteAsync(
              baseUri,
              settings.ActionModel,
              request.Message,
              functionGemmaTeacherCatalog,
              UsageContext(
                settings.ActionModel,
                UsageModelRoles.Action,
                "functiongemma-routing"
              ),
              cancellationToken
            );
          }
          catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
          {
            throw;
          }
          catch (Exception exception)
          {
            routeFailure = exception;
            _logger.LogWarning(
              exception,
              "FunctionGemma routing contract failed for request {RequestId}; preserving the configured target route.",
              requestId
            );
          }

          if (
            residentRoute is null
            && routeFailure is FunctionGemmaProtocolException protocolFailure
            && functionGemmaTeacherCatalog.Count > 0
          )
          {
            yield return Event(
              requestId,
              "agent.functiongemma-routing-repair-started",
              $"The first FunctionGemma route was rejected: {protocolFailure.Message} The Host will make one materially different correction attempt with the same closed Teacher catalog.",
              stopwatch,
              settings.ActionModel,
              intention
            );
            try
            {
              residentRoute = await _functionGemma.RouteAsync(
                baseUri,
                settings.ActionModel,
                "ROUTING_CORRECTION\nThe previous route was rejected by the Host because: "
                  + protocolFailure.Message
                  + " Return exactly one complete route_to_teacher call. Select teacher_model from the closed catalog; its intent must be the intent printed for that same Teacher.\n\nORIGINAL_REQUEST:\n"
                  + request.Message,
                functionGemmaTeacherCatalog,
                UsageContext(
                  settings.ActionModel,
                  UsageModelRoles.Action,
                  "functiongemma-routing-repair"
                ),
                cancellationToken
              );
              routeFailure = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
              throw;
            }
            catch (Exception exception)
            {
              routeFailure = exception;
              _logger.LogWarning(
                exception,
                "FunctionGemma routing correction failed for request {RequestId}; preserving the configured target route.",
                requestId
              );
            }
          }

          if (residentRoute is not null)
          {
            if (residentRoute.HostNormalization is not null)
            {
              yield return Event(
                requestId,
                "agent.functiongemma-route-normalized",
                residentRoute.HostNormalization,
                stopwatch,
                settings.ActionModel,
                residentRoute.Intent
              );
            }
            yield return Event(
              requestId,
              isAuto
                ? "agent.functiongemma-route-selected"
                : "agent.functiongemma-route-advisory",
              isAuto
                ? $"FunctionGemma selected Teacher {residentRoute.TeacherModel} for {residentRoute.Intent}. Reason: {residentRoute.Reason}"
                : $"FunctionGemma recommended Teacher {residentRoute.TeacherModel} for {residentRoute.Intent}; the explicit user model {selectedModel} remains authoritative.",
              stopwatch,
              settings.ActionModel,
              residentRoute.Intent
            );

            if (isAuto)
            {
              selectedModel = residentRoute.TeacherModel;
              intention = residentRoute.Intent;
              selectedModelRole = UsageModelRoles.Specialist;
              _usageGpu = settings.Intentions[intention].Gpu;
            }
          }
          else
          {
            var routeFailureDetail = routeFailure is FunctionGemmaProtocolException rejected
              ? $" Reason: {rejected.Message}"
              : string.Empty;
            yield return Event(
              requestId,
              "agent.functiongemma-contract-warning",
              $"FunctionGemma routing was rejected at {FunctionGemmaFailureStage(routeFailure!)}.{routeFailureDetail} The configured specialist {selectedModel} remains selected and Execute will continue.",
              stopwatch,
              settings.ActionModel,
              intention
            );
          }
        }
        else
        {
          yield return Event(
            requestId,
            "agent.functiongemma-contract-warning",
            $"Configured resident {settings.ActionModel} is unavailable; the configured specialist route will continue without the trained resident contract.",
            stopwatch,
            settings.ActionModel,
            intention
          );
        }
      }

      if (
        functionGemmaProtocolActive
        && _functionGemma.Supports(
          configuredCoordinatorFallback
        )
      )
      {
        var executionFallback = functionGemmaTeacherCatalog
          .Where(
            teacher => teacher.Trained
              && !string.Equals(
                teacher.Model,
                selectedModel,
                StringComparison.OrdinalIgnoreCase
              )
          )
          .OrderBy(
            teacher => string.Equals(
              teacher.Intent,
              "review-and-testing",
              StringComparison.Ordinal
            )
              ? 0
              : 1
          )
          .ThenBy(
            teacher => teacher.Model,
            StringComparer.Ordinal
          )
          .FirstOrDefault();

        if (executionFallback is not null)
        {
          yield return Event(
            requestId,
            "agent.functiongemma-execution-fallback-corrected",
            $"Configured fallback {configuredCoordinatorFallback} is another FunctionGemma supervisor, not a generic action executor. For this turn the Host selected installed Teacher {executionFallback.Model} as the bounded on-demand execution fallback.",
            stopwatch,
            executionFallback.Model,
            executionFallback.Intent
          );
          configuredCoordinatorFallback = executionFallback.Model;
        }
        else
        {
          yield return Event(
            requestId,
            "agent.functiongemma-contract-warning",
            $"Configured fallback {configuredCoordinatorFallback} is supervisory and no distinct installed Teacher is available for execution takeover. The active Teacher path will continue, but FunctionGemma will not be used as a generic executor.",
            stopwatch,
            configuredCoordinatorFallback,
            intention
          );
        }
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
      ContextUsageView? contextUsage = null;
      if (!string.Equals(
        request.InteractionMode,
        "execute",
        StringComparison.Ordinal
      ))
      {
        contextUsage = CreateContextUsage(
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
      }

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
        var activeValidationProfile = activeWorkspace?.ValidationProfile
          ?? settings.ValidationProfile;
        _executionSession.SelectValidationProfile(
          activeValidationProfile
        );
        var executionToolScope = ExecutionTurnToolPolicy.Resolve(
          request.Message,
          activeValidationProfile is not null
        );
        var hostCapabilities = HostCapabilityProfile.Create(
          executionToolScope,
          request.ApprovalPolicy
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
        yield return Event(
          requestId,
          "execution-tool-scope-resolved",
          $"Host made {executionToolScope.AvailableTools.Count} policy-available action tools discoverable. "
            + (
              executionToolScope.ProcessExecutionAllowed
                ? "Structured process execution is available but not implied."
                : "Process execution is unavailable for this turn."
            )
            + (
              executionToolScope.ManualValidationRequested
                ? " Validation remains manual as requested."
                : string.Empty
            ),
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

        var harnessDefinition = GetHarnessDefinition(request.Harness);
        if (!_harnesses.TryGetAdapter(harnessDefinition.Id, out var harness))
        {
          throw new ChatStageException(
            "harness-adapter-missing",
            $"{HarnessLabel(harnessDefinition)} is not executable.",
            $"No adapter is registered for harness '{harnessDefinition.Id}'.",
            selectedModel,
            intention,
            503,
            true
          );
        }

        var execution = new AgentHarnessExecution<ChatStreamEvent>(
          nativeCancellationToken => ExecuteNativeHarnessSelectionAsync(
            request,
            requestId,
            selectedModel,
            intention,
            baseUri,
            messages,
            models,
            capabilities,
            settings,
            context,
            project,
            rootInstructions,
            executionToolScope,
            contextUsage,
            isAuto,
            stopwatch,
            (active, target) =>
            {
              recoveryActive = active;
              recoveryTarget = target;
            },
            nativeCancellationToken
          ),
          (transport, externalCancellationToken) =>
            ExecuteExternalHarnessSelectionAsync(
              transport,
              harnessDefinition,
              request,
              requestId,
              selectedModel,
              intention,
              baseUri,
              workspace.Path,
              settings,
              capabilities,
              context,
              models,
              hostCapabilities,
              stopwatch,
              (active, target) =>
              {
                recoveryActive = active;
                recoveryTarget = target;
              },
              externalCancellationToken
            )
        );
        await foreach (var streamEvent in harness.ExecuteAsync(
          execution,
          cancellationToken
        ))
        {
          yield return streamEvent;
        }
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
      var responseTail = _executionSession is null
        ? null
        : CreateAuthoritativeStatus(
          _executionSession.CreateSummary().CompletionStatus
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
        ContextUsage: contextUsage,
        ResponseTail: responseTail,
        ResponseTailHtml: responseTail is null
          ? null
          : _markdownRenderer.Render(responseTail)
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

  private async IAsyncEnumerable<ChatStreamEvent> ExecuteExternalHarnessSelectionAsync(
    IAgentHarnessTransport harness,
    HarnessDefinition harnessDefinition,
    ChatRequest request,
    string requestId,
    string selectedModel,
    string intention,
    Uri baseUri,
    string workspacePath,
    ApplicationSettings settings,
    ProviderModelCapabilities capabilities,
    ConversationContextResult context,
    IReadOnlyList<InstalledModel> models,
    HostCapabilityProfile hostCapabilities,
    Stopwatch stopwatch,
    Action<bool, string> setRecovery,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var harnessSelectedReference = ProviderModelReference.Parse(selectedModel);
    if (
      harnessDefinition.SupportedProviders is { Count: > 0 }
      && !harnessDefinition.SupportedProviders.Contains(
        harnessSelectedReference.ProviderId,
        StringComparer.OrdinalIgnoreCase
      )
    )
    {
      throw new ChatStageException(
        $"{harnessDefinition.Id}-provider-unsupported",
        HarnessProviderUnsupportedMessage(harnessDefinition),
        $"Selected provider '{harnessSelectedReference.ProviderId}' is outside the {harnessDefinition.Id} harness scope.",
        selectedModel,
        intention,
        400,
        false,
        provider: harnessSelectedReference.ProviderId
      );
    }

    var harnessSelectedIdentity = models.First(
      installed => string.Equals(
        installed.Name,
        selectedModel,
        StringComparison.OrdinalIgnoreCase
      )
    );
    var harnessResidentEligibility = _residentEligibility.Evaluate(
      settings,
      harnessSelectedIdentity,
      _residentModel.GetStatus(),
      false
    );
    yield return Event(
      requestId,
      "agent.memory-eligibility-evaluated",
      $"Resident {settings.CoordinatorModel}; {harnessDefinition.DisplayName} target {selectedModel}. Evidence: "
        + $"{harnessResidentEligibility.Evidence}. Consequence: {harnessResidentEligibility.MemoryConsequence}.",
      stopwatch,
      settings.CoordinatorModel,
      intention
    );

    if (harnessResidentEligibility.RequiresResidentEviction)
    {
      yield return Event(
        requestId,
        "resident-model-eviction-started",
        $"Evicting resident {settings.CoordinatorModel} before {harnessDefinition.DisplayName} starts {selectedModel}.",
        stopwatch,
        settings.CoordinatorModel,
        intention
      );
      var recoveryActive = await _residentModel.EvictForRecoveryAsync(
        selectedModel,
        cancellationToken
      );
      setRecovery(recoveryActive, selectedModel);
      if (!recoveryActive)
      {
        throw new ChatStageException(
          "resident-model-eviction",
          "The resident could not be evicted for the selected local target.",
          harnessResidentEligibility.MemoryConsequence,
          settings.CoordinatorModel,
          intention,
          null,
          true
        );
      }
    }

    var contextUsage = CreateExternalHarnessContextUsage(
      context,
      capabilities,
      settings
    );
    yield return new ChatStreamEvent(
      requestId,
      "context.usage",
      DateTimeOffset.UtcNow,
      null,
      null,
      selectedModel,
      intention,
      stopwatch.ElapsedMilliseconds,
      null,
      null,
      ContextUsage: contextUsage
    );

    await foreach (var streamEvent in ExecuteExternalHarnessAsync(
      harness,
      harnessDefinition,
      request,
      requestId,
      selectedModel,
      intention,
      baseUri,
      workspacePath,
      settings.Execution,
      settings.ProjectAwareness,
      context,
      contextUsage,
      hostCapabilities,
      stopwatch,
      cancellationToken
    ))
    {
      yield return streamEvent;
    }
  }

  private async IAsyncEnumerable<ChatStreamEvent> ExecuteNativeHarnessSelectionAsync(
    ChatRequest request,
    string requestId,
    string selectedModel,
    string intention,
    Uri baseUri,
    IReadOnlyList<ChatMessage> messages,
    IReadOnlyList<InstalledModel> models,
    ProviderModelCapabilities capabilities,
    ApplicationSettings settings,
    ConversationContextResult context,
    ProjectProfile project,
    RepositoryInstructionSet rootInstructions,
    ExecutionTurnToolScope executionToolScope,
    ContextUsageView? contextUsage,
    bool isAuto,
    Stopwatch stopwatch,
    Action<bool, string> setRecovery,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    messages = messages.Prepend(
      new ChatMessage(
        "system",
        CreateProjectContext(
          project,
          rootInstructions,
          executionToolScope
        )
      )
    ).ToArray();

    var coordinatorModel = selectedModel;
    var executionMessages = messages.ToList();
    ExpertExecutionGuidance? executionGuidance = null;
    var structuredCoordination = !capabilities.NativeTools
      && capabilities.StructuredOutput;
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
    var toolingProfile = _toolingProfiles.Resolve(
      new SpecialistToolingIdentity(
        selectedReference.ProviderId,
        selectedModel,
        selectedIdentity.Digest,
        capabilities.NativeTools,
        capabilities.StructuredOutput,
        capabilities.ToolProtocolConfirmed
      )
    );

    yield return Event(
      requestId,
      "agent.tooling-profile-resolved",
      $"Resolved specialist tooling profile {toolingProfile.Identity} through {toolingProfile.ResolutionSource}; transport {toolingProfile.Transport}.",
      stopwatch,
      selectedModel,
      intention
    );

    yield return Event(
      requestId,
      "agent.coordination-path-resolved",
      $"Target {selectedModel} owns the reasoning and tool loop through "
        + $"{(structuredCoordination ? "direct-structured" : "direct-native")}; "
        + $"router resident {settings.CoordinatorModel} is not an execution coordinator.",
      stopwatch,
      selectedModel,
      intention
    );
    var residentEligibility = _residentEligibility.Evaluate(
      settings,
      selectedIdentity,
      _residentModel.GetStatus(),
      false
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
      selectedReference.IsLocal
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
      var recoveryActive = await _residentModel.EvictForRecoveryAsync(
        selectedModel,
        cancellationToken
      );
      setRecovery(recoveryActive, selectedModel);

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

    var session = _executionSession ?? throw new InvalidOperationException(
      "Native harness execution requires an active execution session."
    );
    session.ResolveCoordinator(
      coordinatorModel,
      structuredCoordination
        ? "direct-structured"
        : "direct-native"
    );
    session.RecordCoordinationMetadata(
      settings.CoordinatorModel,
      null,
      null
    );

    var execution = new ExecutionProgress(
      executionMessages,
      toolingProfile,
      executionGuidance,
      executionToolScope,
      visibleMessages: context.VisibleMessages,
      omittedMessages: context.OmittedMessages,
      manualCompactionRequested: request.CompactContext
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
      null,
      settings.Execution.DirectCoordinatorPlanningFailuresBeforeHandoff,
      false,
      settings,
      settings.Execution,
      settings.ProjectAwareness,
      capabilities.ContextTokens,
      cancellationToken
    ))
    {
      yield return streamEvent;
    }

    if (execution.PlanningFailure is not null)
    {
      throw execution.PlanningFailure;
    }
    if (execution.Failure is not null)
    {
      throw execution.Failure;
    }

    if (execution.PartialContextExhausted)
    {
      session.MarkPartialContextExhausted();
      session.Complete("completed-with-warnings");
      var partialSummary = session.CreateSummary();
      var partialText = "Execution reached the configured coordinator context limit after one bounded deterministic compaction. "
        + "Completed actions and artifacts were preserved; open the execution review before continuing.";
      var partialResponse = partialText
        + "\n\n"
        + CreateAuthoritativeStatus(partialSummary.CompletionStatus);
      yield return new ChatStreamEvent(
        requestId,
        "response.completed",
        DateTimeOffset.UtcNow,
        "Partial reviewable result completed after context exhaustion.",
        null,
        selectedModel,
        isAuto ? intention : null,
        stopwatch.ElapsedMilliseconds,
        _markdownRenderer.Render(partialResponse),
        null,
        null,
        partialSummary,
        ContextUsage: execution.LatestContextUsage,
        ResponseTail: partialResponse,
        ResponseTailHtml: _markdownRenderer.Render(partialResponse)
      );
      yield break;
    }

    session.RefreshCompletionGate();
    yield return Event(
      requestId,
      "completion-gate-evaluated",
      $"Completion gate: {session.CreateSummary().CompletionStatus}.",
      stopwatch,
      selectedModel,
      intention
    );
    session.Complete(
      session.HasWarnings
        ? "completed-with-warnings"
        : "completed"
    );
    var hostReview = session.CreateReview();
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
      ContextUsage: execution.LatestContextUsage ?? contextUsage,
      ResponseTail: hostResponse,
      ResponseTailHtml: _markdownRenderer.Render(hostResponse)
    );
  }

  private static HarnessConversationContext CreateHarnessConversationContext(
    ConversationContextResult context
  )
  {
    var history = context.Messages
      .Where(message => message.Role is "user" or "assistant")
      .SkipLast(1)
      .ToArray();
    var sequence = context.OmittedMessages;
    return new HarnessConversationContext(
      sequence + history.Length,
      context.OmittedMessages,
      history.Select(
        message => new HarnessConversationMessage(
          sequence++,
          message.Role,
          message.Content
        )
      ).ToArray()
    );
  }

  private async IAsyncEnumerable<ChatStreamEvent> ExecuteExternalHarnessAsync(
    IAgentHarnessTransport harness,
    HarnessDefinition harnessDefinition,
    ChatRequest request,
    string requestId,
    string model,
    string intention,
    Uri ollamaUrl,
    string workspacePath,
    ExecutionSettings executionSettings,
    ProjectAwarenessSettings projectAwareness,
    ConversationContextResult context,
    ContextUsageView initialContextUsage,
    HostCapabilityProfile hostCapabilities,
    Stopwatch stopwatch,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    var session = _executionSession ?? throw new InvalidOperationException(
      "The selected harness requires an active execution session."
    );
    var conversationId = request.BrowserSessionId
      ?? request.ConversationSessionId
      ?? throw new ChatStageException(
        $"{harnessDefinition.Id}-conversation-id",
        $"{HarnessLabel(harnessDefinition)} requires an Agentic Router conversation identifier.",
        "Both conversationSessionId and browserSessionId were missing.",
        model,
        intention,
        400,
        true
      );
    var observer = await HarnessWorkspaceObserver.CaptureAsync(
      workspacePath,
      executionSettings,
      cancellationToken
    );
    var answer = new StringBuilder();
    var responseSegment = new StringBuilder();
    string? activeResponseItemId = null;
    var approvedDeletionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var approvedNativeMutation = false;
    HarnessEvent? terminalFailure = null;
    var latestContextUsage = initialContextUsage;
    session.ResolveCoordinator(
      model,
      harnessDefinition.Experimental
        ? $"{harnessDefinition.Id}-experimental"
        : harnessDefinition.Id
    );
    session.RecordCoordinationMetadata("none", null, null);

    var nativeCommon = HarnessCapabilityProjection.NativeCommonTools(
      harnessDefinition.Id
    );
    var hostBridge = HarnessCapabilityProjection.HostBridgeTools(
      harnessDefinition.Id,
      hostCapabilities
    );
    var missingAdapters = HarnessCapabilityProjection.MissingAdapterTools(
      harnessDefinition.Id,
      hostCapabilities
    );
    yield return Event(
      requestId,
      "execution-capability-profile-projected",
      $"AR_COMMON profile projected to {harnessDefinition.DisplayName}: "
        + $"native implementation [{string.Join(", ", nativeCommon)}]; "
        + $"Host bridge [{string.Join(", ", hostBridge)}]; "
        + $"missing adapter [{string.Join(", ", missingAdapters)}].",
      stopwatch,
      model,
      intention
    );

    yield return Event(
      requestId,
      $"harness.{harnessDefinition.Id}-selected",
      $"{HarnessLabel(harnessDefinition)} selected for Execute with exact model {model}.",
      stopwatch,
      model,
      intention
    );
    yield return Event(
      requestId,
      $"harness.{harnessDefinition.Id}-starting",
      $"Starting or reusing the Agentic Router-owned {harnessDefinition.DisplayName} runtime.",
      stopwatch,
      model,
      intention
    );
    await foreach (var harnessEvent in harness.StartTurnAsync(
      new HarnessTurnRequest(
        harnessDefinition.Id,
        conversationId,
        model,
        ModelProviderIds.OllamaLocal,
        workspacePath,
        request.Message,
        request.ApprovalPolicy,
        ollamaUrl,
        CreateHarnessConversationContext(context),
        ContextWindowTokens: initialContextUsage.EffectiveLimitTokens,
        HostCapabilities: hostCapabilities
      ),
      cancellationToken
    ))
    {
      if (!string.Equals(
        harnessEvent.HarnessId,
        harnessDefinition.Id,
        StringComparison.OrdinalIgnoreCase
      ))
      {
        throw new HarnessException(
          "harness-event-identity-mismatch",
          "The harness returned an event with an invalid identity.",
          $"Expected '{harnessDefinition.Id}', received '{harnessEvent.HarnessId}'.",
          false,
          harnessId: harnessDefinition.Id
        );
      }
      switch (harnessEvent.Type)
      {
        case "reasoning.delta":
          responseSegment.Clear();
          activeResponseItemId = null;
          if (!string.IsNullOrEmpty(harnessEvent.Delta))
          {
            yield return new ChatStreamEvent(
              requestId,
              "reasoning.delta",
              DateTimeOffset.UtcNow,
              null,
              null,
              model,
              intention,
              stopwatch.ElapsedMilliseconds,
              null,
              null,
              ReasoningDelta: harnessEvent.Delta,
              ContentBlockId: harnessEvent.ItemId
            );
          }
          break;
        case "assistant.delta":
          if (!string.IsNullOrEmpty(harnessEvent.Delta))
          {
            if (!string.Equals(
              activeResponseItemId,
              harnessEvent.ItemId,
              StringComparison.Ordinal
            ))
            {
              responseSegment.Clear();
              activeResponseItemId = harnessEvent.ItemId;
            }
            answer.Append(harnessEvent.Delta);
            responseSegment.Append(harnessEvent.Delta);
            yield return new ChatStreamEvent(
              requestId,
              "response.delta",
              DateTimeOffset.UtcNow,
              null,
              harnessEvent.Delta,
              model,
              intention,
              stopwatch.ElapsedMilliseconds,
              _markdownRenderer.Render(answer.ToString()),
              null,
              ExecutionSession: session.CreateSummary(),
              ContentBlockId: harnessEvent.ItemId,
              ResponseSegmentHtml: _markdownRenderer.Render(responseSegment.ToString())
            );
          }
          break;
        case "tool.started":
        case "tool.output":
        case "tool.completed":
        case "tool.failed":
          responseSegment.Clear();
          activeResponseItemId = null;
          yield return HarnessActionEvent(
            requestId,
            harnessEvent,
            harnessDefinition,
            model,
            intention,
            stopwatch,
            session.Id
          );
          break;
        case "usage.updated" when harnessEvent.ContextInputTokens is > 0:
          latestContextUsage = WithExactContextUsage(
            latestContextUsage,
            harnessEvent.ContextInputTokens.Value
          );
          yield return new ChatStreamEvent(
            requestId,
            "context.usage",
            DateTimeOffset.UtcNow,
            null,
            null,
            model,
            intention,
            stopwatch.ElapsedMilliseconds,
            null,
            null,
            ContextUsage: latestContextUsage
          );
          break;
        case "host-tool.requested":
          responseSegment.Clear();
          activeResponseItemId = null;
          await foreach (var hostToolEvent in ExecuteHarnessHostToolAsync(
            harness,
            harnessDefinition,
            request,
            requestId,
            model,
            intention,
            stopwatch,
            session,
            harnessEvent,
            approvedDeletionPaths,
            projectAwareness,
            cancellationToken
          ))
          {
            yield return hostToolEvent;
          }
          break;
        case "approval.requested":
          {
            responseSegment.Clear();
            activeResponseItemId = null;
            if (harnessEvent.ApprovalId is null)
            {
              throw new HarnessException(
                $"{harnessDefinition.Id}-approval-invalid",
                $"{harnessDefinition.DisplayName} sent an invalid approval request.",
                "The server request did not include an approval identifier.",
                false
              );
            }
            if (harnessEvent.ReadOnlyPermission)
            {
              await harness.ResolveApprovalAsync(
                harnessEvent.ApprovalId,
                true,
                cancellationToken
              );
              yield return Event(
                requestId,
                $"harness.{harnessDefinition.Id}-readonly-authorized",
                $"Agentic Router authorized read-only native capability {harnessEvent.Tool ?? "<unknown>"} without a mutation approval prompt.",
                stopwatch,
                model,
                intention
              );
              break;
            }
            if (!harnessEvent.ApprovalCanBeMapped)
            {
              await harness.ResolveApprovalAsync(
                harnessEvent.ApprovalId,
                false,
                cancellationToken
              );
              if (harnessEvent.RecoveryExhausted)
              {
                throw new HarnessException(
                  harnessEvent.ErrorCode ?? "harness-approval-recovery-exhausted",
                  $"{harnessDefinition.DisplayName} repeated an unsupported approval request after Host correction.",
                  harnessEvent.Message ?? $"Repeated unsupported {harnessDefinition.DisplayName} server request.",
                  false
                );
              }
              var correction = "Agentic Router declined a native approval that did not provide an exact Host-validatable workspace target. "
                + $"{harnessDefinition.DisplayName} may continue using its available workspace-confined capabilities.";
              session.AddWarning(correction);
              yield return Event(
                requestId,
                $"harness.{harnessDefinition.Id}-approval-corrected",
                correction,
                stopwatch,
                model,
                intention
              );
              break;
            }

            using var arguments = JsonDocument.Parse("{}");
            IReadOnlyList<string> approvedPaths = [];
            LocalActionException? pathRejection = null;
            try
            {
              approvedPaths = await ResolveHarnessApprovalPathsAsync(
                harnessEvent.Paths,
                workspacePath,
                cancellationToken
              );
            }
            catch (LocalActionException exception)
            {
              await harness.ResolveApprovalAsync(
                harnessEvent.ApprovalId,
                false,
                cancellationToken
              );
              pathRejection = exception;
            }
            if (pathRejection is not null)
            {
              var correction = $"Agentic Router denied only this native action: {pathRejection.Message} "
                + $"{harnessDefinition.DisplayName} may continue with a materially different workspace-confined action.";
              session.AddWarning(correction);
              yield return Event(
                requestId,
                $"harness.{harnessDefinition.Id}-approval-corrected",
                correction,
                stopwatch,
                model,
                intention
              );
              break;
            }
            var action = new ValidatedLocalAction(
              $"{harnessDefinition.Id}-{harnessEvent.ApprovalId}",
              harnessEvent.Tool ?? $"{harnessDefinition.Id}_file_change",
              arguments.RootElement.Clone(),
              null,
              approvedPaths[0],
              $"{harnessEvent.Message ?? $"{harnessDefinition.DisplayName} file change"} "
                + $"Targets: {string.Join(", ", approvedPaths)}.",
              harnessEvent.Output,
              false,
              true
            );

            if (
              string.Equals(request.ApprovalPolicy, "auto", StringComparison.Ordinal)
            )
            {
              await harness.ResolveApprovalAsync(
                harnessEvent.ApprovalId,
                true,
                cancellationToken
              );
              yield return HarnessApprovalEvent(
                requestId,
                "action.approved",
                $"Agentic Router approved the workspace-confined {harnessDefinition.DisplayName} file change.",
                action,
                model,
                intention,
                stopwatch,
                session.Id,
                "approved"
              );
              approvedNativeMutation = true;
              if (harnessEvent.Destructive)
              {
                foreach (var approvedPath in approvedPaths)
                {
                  approvedDeletionPaths.Add(approvedPath);
                }
              }
              break;
            }

            var decisionTask = _approvalCoordinator.WaitAsync(
              action,
              session.BrowserSessionId,
              session.Id,
              (_, _, _) => Task.FromResult(
                new ApprovalRevisionValidation(
                  false,
                  null,
                  $"{harnessDefinition.DisplayName} native approvals do not support inline argument editing."
                )
              ),
              cancellationToken
            );
            yield return HarnessApprovalEvent(
              requestId,
              "action.awaiting-approval",
              $"Waiting for approval: {action.Summary}.",
              action,
              model,
              intention,
              stopwatch,
              session.Id,
              "awaiting-approval"
            );
            var decision = await decisionTask;
            await harness.ResolveApprovalAsync(
              harnessEvent.ApprovalId,
              decision.Approved,
              cancellationToken
            );
            if (decision.Approved && harnessEvent.Destructive)
            {
              foreach (var approvedPath in approvedPaths)
              {
                approvedDeletionPaths.Add(approvedPath);
              }
            }
            if (decision.Approved)
            {
              approvedNativeMutation = true;
            }
            yield return HarnessApprovalEvent(
              requestId,
              decision.Approved ? "action.approved" : "action.rejected",
              decision.Approved
                ? $"{harnessDefinition.DisplayName} action approved."
                : $"{harnessDefinition.DisplayName} action rejected.",
              action,
              model,
              intention,
              stopwatch,
              session.Id,
              decision.Approved ? "approved" : "rejected"
            );
            break;
          }
        case "warning":
          session.AddWarning(harnessEvent.Message ?? $"{harnessDefinition.DisplayName} reported a warning.");
          yield return Event(
            requestId,
            $"harness.{harnessDefinition.Id}-warning",
            harnessEvent.Message ?? $"{harnessDefinition.DisplayName} reported a warning.",
            stopwatch,
            model,
            intention
          );
          break;
        case "files.changed":
          yield return Event(
            requestId,
            $"harness.{harnessDefinition.Id}-files-changed",
            harnessEvent.Message ?? $"{harnessDefinition.DisplayName} reported workspace changes.",
            stopwatch,
            model,
            intention
          );
          break;
        case "turn.failed":
          terminalFailure = harnessEvent;
          break;
        case "error":
          {
            var recoveredError = harnessEvent.Message
              ?? $"{harnessDefinition.DisplayName} reported a non-terminal error.";
            session.AddWarning(recoveredError);
            yield return Event(
              requestId,
              $"harness.{harnessDefinition.Id}-error-recovered",
              recoveredError,
              stopwatch,
              model,
              intention
            );
            break;
          }
        case "turn.cancelled":
          session.Complete("cancelled");
          yield return new ChatStreamEvent(
            requestId,
            "request.cancelled",
            DateTimeOffset.UtcNow,
            $"{harnessDefinition.DisplayName} turn cancelled.",
            null,
            model,
            intention,
            stopwatch.ElapsedMilliseconds,
            null,
            null,
            ExecutionSession: session.CreateSummary(),
            ContextUsage: latestContextUsage
          );
          yield break;
        case "native.event":
          if (harnessEvent.NativePayload is null)
          {
            throw new HarnessException(
              $"{harnessDefinition.Id}-native-payload-missing",
              $"{harnessDefinition.DisplayName} returned an incomplete native event.",
              harnessEvent.Message ?? "The normalized event omitted its native payload.",
              true,
              harnessId: harnessDefinition.Id
            );
          }
          yield return Event(
            requestId,
            $"harness.{harnessDefinition.Id}-native-event-preserved",
            $"{harnessDefinition.DisplayName} native event retained for diagnostics.",
            stopwatch,
            model,
            intention
          );
          break;
      }
    }

    var observed = await observer.ObserveAsync(
      approvedDeletionPaths,
      !hostCapabilities.MutationRequiresApproval || approvedNativeMutation,
      cancellationToken
    );
    HarnessWorkspaceObserver.Record(session, observed);
    yield return Event(
      requestId,
      $"harness.{harnessDefinition.Id}-effects-observed",
      observed.Count == 0
        ? $"Agentic Router observed no workspace file changes from the {harnessDefinition.DisplayName} turn."
        : $"Agentic Router independently hashed and recorded {observed.Count} changed file(s).",
      stopwatch,
      model,
      intention
    );

    if (terminalFailure is null && observed.Count > 0)
    {
      using var validationArguments = JsonDocument.Parse("{}");
      var validationAction = await _actionService.ValidateAsync(
        new LocalActionProposal(
          "run_validation_profile",
          validationArguments.RootElement.Clone(),
          $"Automatically run the Host validation profile after {harnessDefinition.DisplayName} workspace changes."
        ),
        session,
        cancellationToken
      );
      session.RecordAction(validationAction, "proposed");
      yield return Event(
        requestId,
        "validation-started",
        $"Running the Host validation profile after {harnessDefinition.DisplayName} workspace changes.",
        stopwatch,
        model,
        intention
      );
      var validationResult = await _actionService.ExecuteAsync(
        validationAction,
        session,
        cancellationToken
      );
      var validation = validationResult.Validation
        ?? throw new LocalActionException(
          "validation-profile",
          "The Host validation action did not return validation facts."
        );
      foreach (var step in validation.Steps)
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
      session.RecordAction(
        validationAction,
        validationResult.Succeeded ? "completed" : "failed",
        validationResult.Output
      );
      if (!validationResult.Succeeded)
      {
        session.AddWarning(
          validation.State == "not-configured"
            ? "Host validation was requested automatically, but no validation profile is configured."
            : $"Host validation completed with state {validation.State}."
        );
      }
      yield return Event(
        requestId,
        "validation-completed",
        $"Validation {validation.State}: {validation.ProfileName ?? "not configured"}.",
        stopwatch,
        model,
        intention
      );
    }

    if (terminalFailure is not null)
    {
      throw new HarnessException(
        terminalFailure.ErrorCode ?? $"{harnessDefinition.Id}-turn-failed",
        terminalFailure.Message ?? $"{harnessDefinition.DisplayName} turn failed.",
        terminalFailure.Message ?? $"{harnessDefinition.DisplayName} reported a failed turn.",
        true
      );
    }

    session.AddWarning(
      $"{harnessDefinition.DisplayName} lifecycle completion was recorded separately from Host-observed effects and validation facts."
    );
    session.RefreshCompletionGate();
    session.Complete("completed-with-warnings");
    var summary = session.CreateSummary();
    var responseTail = CreateAuthoritativeStatus(summary.CompletionStatus);
    var visibleAnswer = string.IsNullOrWhiteSpace(answer.ToString())
      ? responseTail
      : answer + "\n\n---\n" + responseTail;
    yield return new ChatStreamEvent(
      requestId,
      "response.completed",
      DateTimeOffset.UtcNow,
      $"{harnessDefinition.DisplayName} turn completed; Agentic Router recorded only Host-observed facts.",
      null,
      model,
      intention,
      stopwatch.ElapsedMilliseconds,
      _markdownRenderer.Render(visibleAnswer),
      null,
      ExecutionSession: summary,
      ContextUsage: latestContextUsage,
      ResponseTail: responseTail,
      ResponseTailHtml: _markdownRenderer.Render(responseTail)
    );
  }

  private async Task<IReadOnlyList<string>> ResolveHarnessApprovalPathsAsync(
    IReadOnlyList<string>? paths,
    string workspacePath,
    CancellationToken cancellationToken
  )
  {
    if (paths is null || paths.Count == 0)
    {
      throw new LocalActionException(
        "harness-approval-path",
        "A native harness file approval must name at least one explicit path."
      );
    }

    var root = Path.GetFullPath(workspacePath);
    var resolved = new List<string>(paths.Count);
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in paths)
    {
      var fullPath = await _workspace.ResolvePathAsync(path, cancellationToken);
      var relative = Path.GetRelativePath(root, fullPath);
      if (
        Path.IsPathFullyQualified(relative)
        || string.Equals(relative, "..", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
      )
      {
        throw new LocalActionException(
          "harness-approval-path",
          "The native harness approval path is outside the active trusted workspace."
        );
      }
      relative = relative.Replace('\\', '/');
      if (seen.Add(relative))
      {
        resolved.Add(relative);
      }
    }
    return resolved;
  }

  private async IAsyncEnumerable<ChatStreamEvent> ExecuteHarnessHostToolAsync(
    IAgentHarnessTransport harness,
    HarnessDefinition harnessDefinition,
    ChatRequest request,
    string requestId,
    string model,
    string intention,
    Stopwatch stopwatch,
    ExecutionSession session,
    HarnessEvent harnessEvent,
    ISet<string> approvedDeletionPaths,
    ProjectAwarenessSettings projectAwareness,
    [EnumeratorCancellation] CancellationToken cancellationToken
  )
  {
    if (
      string.IsNullOrWhiteSpace(harnessEvent.ToolCallId)
      || string.IsNullOrWhiteSpace(harnessEvent.Tool)
      || harnessEvent.Arguments is not JsonElement arguments
    )
    {
      throw new HarnessException(
        $"{harnessDefinition.Id}-tool-call-invalid",
        $"{harnessDefinition.DisplayName} sent an incomplete Host tool call.",
        harnessEvent.Message ?? "Dynamic tool request omitted its call id, tool, or arguments.",
        false
      );
    }

    var proposal = new LocalActionProposal(
      harnessEvent.Tool,
      arguments,
      $"Requested through {harnessDefinition.DisplayName} native tools."
    );
    if (proposal.Tool is "create_execution_plan" or "revise_execution_plan")
    {
      var failure = TryApplyExecutionPlan(proposal, projectAwareness, out var plan);
      if (failure is not null)
      {
        var output = FormatExecutionFailure(failure);
        await harness.ResolveToolCallAsync(
          harnessEvent.ToolCallId,
          false,
          output,
          cancellationToken
        );
        session.RecordToolFailure();
        session.AddWarning($"{proposal.Tool} rejected: {output}");
        yield return Event(
          requestId,
          "action.input-rejected",
          $"Host rejected {proposal.Tool}: {output}",
          stopwatch,
          model,
          intention
        );
        yield break;
      }
      var accepted = JsonSerializer.Serialize(plan);
      await harness.ResolveToolCallAsync(
        harnessEvent.ToolCallId,
        true,
        $"Accepted Host plan:\n{accepted}",
        cancellationToken
      );
      session.RecordToolSuccess();
      yield return Event(
        requestId,
        proposal.Tool == "create_execution_plan"
          ? "execution-plan-created"
          : "execution-plan-revised",
        proposal.Tool == "create_execution_plan"
          ? $"Execution plan created with {plan!.Steps.Count} steps."
          : "Execution plan revised; completed and failed steps were preserved.",
        stopwatch,
        model,
        intention
      );
      yield break;
    }
    var instructionFailure = await ApplyInstructionsForProposalAsync(
      proposal,
      cancellationToken
    );
    var validation = instructionFailure is null
      ? await TryValidateAsync(
        () => _actionService.ValidateAsync(
        proposal,
        session,
        cancellationToken
      )
      )
      : new ValidationAttempt(null, instructionFailure);
    if (validation.Action is null)
    {
      var failure = validation.Failure ?? new LocalActionException(
        "action-validation",
        $"The Host rejected the {harnessDefinition.DisplayName} tool arguments."
      );
      var output = FormatExecutionFailure(failure);
      await harness.ResolveToolCallAsync(
        harnessEvent.ToolCallId,
        false,
        output,
        cancellationToken
      );
      session.RecordToolFailure();
      session.AddWarning($"{harnessEvent.Tool} rejected: {output}");
      yield return Event(
        requestId,
        "action.input-rejected",
        $"Host rejected {harnessEvent.Tool}: {output}",
        stopwatch,
        model,
        intention
      );
      yield break;
    }

    var action = validation.Action;
    session.RecordAction(action, "proposed");
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
        session.BrowserSessionId,
        session.Id,
        (
          pendingAction,
          editedText,
          revisionCancellationToken
        ) => ValidateApprovalRevisionAsync(
          pendingAction,
          editedText,
          session,
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
      var decision = await decisionTask;
      action = decision.Action;
      if (decision.Revised)
      {
        session.RecordAction(action, "revised", "The pending batch was edited and revalidated by the Host.");
        yield return ActionEvent(
          requestId,
          "action.revised",
          $"Edited batch validated: {action.Summary}.",
          stopwatch,
          model,
          intention,
          action,
          "revised",
          true
        );
      }
      if (!decision.Approved)
      {
        const string rejection = "The user rejected this Host batch action. It was not executed.";
        await harness.ResolveToolCallAsync(
          harnessEvent.ToolCallId,
          false,
          rejection,
          cancellationToken
        );
        session.RecordAction(action, "rejected", rejection);
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
      () => _actionService.ExecuteAsync(action, session, cancellationToken)
    );
    if (execution.Result?.Succeeded == true)
    {
      var result = execution.Result;
      session.RecordAction(action, "completed", result.Output);
      session.RecordToolSuccess();
      if (action.Tool == "delete_paths")
      {
        foreach (var change in action.PendingFileChanges ?? [])
        {
          approvedDeletionPaths.Add(change.RelativePath.Replace('\\', '/'));
        }
      }
      await harness.ResolveToolCallAsync(
        harnessEvent.ToolCallId,
        true,
        result.Output,
        cancellationToken
      );
      yield return ActionEvent(
        requestId,
        result.EventType,
        FormatActivityOutput(action, result.Output),
        stopwatch,
        model,
        intention,
        action,
        "completed",
        requiresApproval,
        result.Output
      );
      yield break;
    }

    var exception = execution.Failure ?? new LocalActionException(
      "action-execution",
      "The Host batch action did not complete."
    );
    var failureOutput = execution.Result?.Output ?? FormatExecutionFailure(exception);
    session.RecordAction(action, "failed", failureOutput);
    session.RecordToolFailure();
    session.AddWarning($"{action.Tool} failed: {failureOutput}");
    await harness.ResolveToolCallAsync(
      harnessEvent.ToolCallId,
      false,
      failureOutput,
      cancellationToken
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
      requiresApproval,
      failureOutput
    );
  }

  private static ChatStreamEvent HarnessActionEvent(
    string requestId,
    HarnessEvent harnessEvent,
    HarnessDefinition harnessDefinition,
    string model,
    string intention,
    Stopwatch stopwatch,
    string executionSessionId
  )
  {
    var type = harnessEvent.Type switch
    {
      "tool.started" => "action.started",
      "tool.output" => "action.output",
      "tool.completed" => "action.completed",
      _ => "action.failed"
    };
    return new ChatStreamEvent(
      requestId,
      type,
      DateTimeOffset.UtcNow,
      harnessEvent.Message,
      null,
      model,
      intention,
      stopwatch.ElapsedMilliseconds,
      null,
      null,
      new LocalActionEvent(
        $"{harnessDefinition.Id}-{harnessEvent.ItemId ?? Guid.NewGuid().ToString("N")}",
        harnessEvent.Tool ?? $"{harnessDefinition.Id}_tool",
        harnessEvent.Message ?? $"{harnessDefinition.DisplayName} tool activity",
        null,
        harnessEvent.State ?? "running",
        false,
        executionSessionId,
        ResultOutput: harnessEvent.Output ?? harnessEvent.Delta
      )
    );
  }

  private static ChatStreamEvent HarnessApprovalEvent(
    string requestId,
    string type,
    string message,
    ValidatedLocalAction action,
    string model,
    string intention,
    Stopwatch stopwatch,
    string executionSessionId,
    string state
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
        true,
        executionSessionId
      )
    );
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
    int? providerMaximumTokens,
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
    var protocolRepairAttempted = false;
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
      var toolsetHandled = false;
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
        SpecialistContextMeasurement specialistMeasurement;
        var compactedForRequest = false;
        var omittedContextBlocks = 0;
        long beforeCompactionTokens;
        long afterCompactionTokens;
        var budget = GetCoordinatorInputBudget(
          applicationSettings,
          model,
          providerMaximumTokens
        );
        if (structuredCoordination)
        {
          var originalCount = progress.Messages.Count;
          var originalMeasurement = _expertGuidance.MeasureRequest(
            progress.Messages
          );
          beforeCompactionTokens = originalMeasurement.InputTokens;
          var shouldCompact = ShouldCompactContext(
            originalMeasurement,
            budget,
            progress
          );
          var previewFit = originalMeasurement.CompactionEligible
            && progress.LastUnproductiveCompactionTokens != originalMeasurement.InputTokens
            ? _expertGuidance.FitToBudget(
              progress.Messages,
              _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
              budget.MaximumInputTokens,
              true
            )
            : null;
          structuredPreflightFit = shouldCompact
            ? previewFit ?? _expertGuidance.FitToBudget(
              progress.Messages,
              _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
              budget.MaximumInputTokens,
              true
            )
            : _expertGuidance.FitToBudget(
            progress.Messages,
            _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
            budget.MaximumInputTokens
          );
          var mandatoryReduction = originalMeasurement.InputTokens > budget.MaximumInputTokens;
          if (
            shouldCompact
            && structuredPreflightFit.Compacted
            && !structuredPreflightFit.TooLarge
            && (structuredPreflightFit.Outcome == "compacted" || mandatoryReduction)
          )
          {
            ReplaceChatMessages(progress.Messages, structuredPreflightFit.Messages);
            ReplaceMessages(progress.ToolMessages, progress.Messages.Select(ToToolMessage).ToArray());
            compactedForRequest = true;
            progress.ManualCompactionPending = false;
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
          else if (shouldCompact && structuredPreflightFit.Compacted)
          {
            progress.LastUnproductiveCompactionTokens = originalMeasurement.InputTokens;
          }
          specialistMeasurement = _expertGuidance.MeasureRequest(
            progress.Messages
          ) with
          {
            CompactionEligible = originalMeasurement.CompactionEligible
          };
          afterCompactionTokens = previewFit?.AfterTokens
            ?? structuredPreflightFit.AfterTokens;
          omittedContextBlocks = Math.Max(
            0,
            originalCount - (previewFit?.Messages.Count ?? progress.Messages.Count)
          );
          progress.ManualCompactionPending = false;
        }
        else
        {
          var planRequired = _executionSession?.Plan is null;
          var originalCount = progress.ToolMessages.Count;
          var originalMeasurement = _actionPlanner.MeasureRequest(
            progress.ToolMessages,
            progress.ToolingProfile,
            progress.GrantedTools,
            planRequired,
            attempt
          );
          beforeCompactionTokens = originalMeasurement.InputTokens;
          var shouldCompact = ShouldCompactContext(
            originalMeasurement,
            budget,
            progress
          );
          var previewFit = originalMeasurement.CompactionEligible
            && progress.LastUnproductiveCompactionTokens != originalMeasurement.InputTokens
            ? _actionPlanner.FitToBudget(
              progress.ToolMessages,
              progress.ToolingProfile,
              progress.GrantedTools,
              _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
              budget.MaximumInputTokens,
              planRequired,
              attempt,
              completionAllowed,
              true
            )
            : null;
          preflightFit = shouldCompact
            ? previewFit ?? _actionPlanner.FitToBudget(
              progress.ToolMessages,
              progress.ToolingProfile,
              progress.GrantedTools,
              _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
              budget.MaximumInputTokens,
              planRequired,
              attempt,
              completionAllowed,
              true
            )
            : _actionPlanner.FitToBudget(
            progress.ToolMessages,
            progress.ToolingProfile,
            progress.GrantedTools,
            _executionSession?.CreateCoordinatorStateSummary() ?? "execution-session=unavailable",
            budget.MaximumInputTokens,
            planRequired,
            attempt,
            completionAllowed
          );
          var mandatoryReduction = originalMeasurement.InputTokens > budget.MaximumInputTokens;
          if (
            shouldCompact
            && preflightFit.Compacted
            && !preflightFit.TooLarge
            && (preflightFit.Outcome == "compacted" || mandatoryReduction)
          )
          {
            ReplaceMessages(progress.ToolMessages, preflightFit.Messages);
            compactedForRequest = true;
            progress.ManualCompactionPending = false;
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
          else if (shouldCompact && preflightFit.Compacted)
          {
            progress.LastUnproductiveCompactionTokens = originalMeasurement.InputTokens;
          }
          specialistMeasurement = _actionPlanner.MeasureRequest(
            progress.ToolMessages,
            progress.ToolingProfile,
            progress.GrantedTools,
            planRequired,
            attempt
          ) with
          {
            CompactionEligible = originalMeasurement.CompactionEligible
          };
          afterCompactionTokens = previewFit?.AfterTokens
            ?? preflightFit.AfterTokens;
          omittedContextBlocks = Math.Max(
            0,
            originalCount - (previewFit?.Messages.Count ?? progress.ToolMessages.Count)
          );
          progress.ManualCompactionPending = false;
        }

        progress.ContextInferenceSequence++;
        var contextSnapshot = CreateSpecialistContextUsage(
          specialistMeasurement,
          budget,
          applicationSettings,
          providerMaximumTokens,
          progress.VisibleMessages,
          progress.OmittedMessages,
          progress.ContextInferenceSequence,
          compactedForRequest,
          beforeCompactionTokens,
          afterCompactionTokens,
          omittedContextBlocks
        );
        progress.LatestContextUsage = contextSnapshot;
        yield return new ChatStreamEvent(
          requestId,
          "context.usage",
          DateTimeOffset.UtcNow,
          $"Specialist inference {progress.ContextInferenceSequence}: estimated input {contextSnapshot.InputTokens} tokens; required context {contextSnapshot.RequiredContextTokens} of {contextSnapshot.EffectiveLimitTokens}.",
          null,
          model,
          intention,
          stopwatch.ElapsedMilliseconds,
          null,
          null,
          ContextUsage: contextSnapshot
        );

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
          var reasoning = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
              SingleReader = true,
              SingleWriter = true
            }
          );

          async Task<PlanningAttempt> RunPlanningAsync()
          {
            try
            {
              return await TryPlanAsync(
                () => structuredCoordination
                  ? PlanStructuredActionAsync(
                    baseUri,
                    model,
                    progress,
                    CoordinationUsageRole(applicationSettings, model),
                    cancellationToken
                  )
                  : _actionPlanner.PlanAsync(
                    baseUri,
                    model,
                    progress.ToolingProfile,
                    progress.ToolMessages,
                    progress.GrantedTools,
                    _executionSession?.Plan is null,
                    attempt,
                    completionAllowed,
                    UsageContext(
                      model,
                      CoordinationUsageRole(applicationSettings, model),
                      "local-action-planning"
                    ),
                    cancellationToken,
                    (
                      delta,
                      token
                    ) => reasoning.Writer.WriteAsync(
                      delta,
                      token
                    )
                  )
              );
            }
            finally
            {
              reasoning.Writer.TryComplete();
            }
          }

          var planningTask = RunPlanningAsync();
          await foreach (
            var delta in reasoning.Reader.ReadAllAsync(
              cancellationToken
            )
          )
          {
            yield return Event(
              requestId,
              "reasoning.delta",
              null,
              stopwatch,
              model,
              intention
            ) with
            {
              ReasoningDelta = delta
            };
          }

          planning = await planningTask;
        }

        if (planning.Result?.Usage is not null)
        {
          var exactSnapshot = CreateSpecialistContextUsage(
            specialistMeasurement,
            budget,
            applicationSettings,
            providerMaximumTokens,
            progress.VisibleMessages,
            progress.OmittedMessages,
            progress.ContextInferenceSequence,
            compactedForRequest,
            beforeCompactionTokens,
            afterCompactionTokens,
            omittedContextBlocks,
            planning.Result.Usage
          );
          progress.LatestContextUsage = exactSnapshot;
          yield return new ChatStreamEvent(
            requestId,
            "context.usage",
            DateTimeOffset.UtcNow,
            $"Specialist inference {progress.ContextInferenceSequence}: provider-reported input {exactSnapshot.InputTokens} tokens; required context {exactSnapshot.RequiredContextTokens} of {exactSnapshot.EffectiveLimitTokens}.",
            null,
            model,
            intention,
            stopwatch.ElapsedMilliseconds,
            null,
            null,
            ContextUsage: exactSnapshot
          );
        }

        if (planning.Failure is not null)
        {
          if (CanCompleteAfterRejectedUnavailableTool(
            planning.Failure,
            progress
          ))
          {
            const string warning =
              "The specialist proposed an unavailable tool after the Host verified the required mutation effect with no pending mutation step. "
              + "The Host preserved the user's tool-scope boundary and completed from verified workspace facts without executing the rejected proposal.";
            _executionSession?.AddWarning(
              warning
            );
            yield return Event(
              requestId,
              "action.out-of-scope-proposal-skipped",
              warning,
              stopwatch,
              model,
              intention
            );
            noActionRequired = true;
            exhaustedFailure = null;
            break;
          }

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
                  progress.ToolingProfile,
                  progress.GrantedTools,
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

            var protocolReview = await TryReviewFunctionGemmaFailureAsync(
              baseUri,
              request,
              applicationSettings,
              settings,
              progress,
              protocolFailure,
              "invalid_native_protocol",
              "protocol_error",
              null,
              "none",
              cancellationToken
            );
            foreach (var reviewEvent in FunctionGemmaReviewEvents(
              protocolReview,
              requestId,
              stopwatch,
              applicationSettings.ActionModel,
              intention
            ))
            {
              yield return reviewEvent;
            }

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

            var protocolRecoveryLimitFailure = RecordRecoveryAttempt(
              progress,
              settings,
              protocolFailure.Message,
              model,
              intention
            );

            if (
              !protocolRepairAttempted
              && protocolRecoveryLimitFailure is null
              && planningFailures + 1 < maximumPlanningAttempts
            )
            {
              protocolRepairAttempted = true;
              planningFailures++;
              var protocolCorrection = CreatePlanningCorrection(
                "TOOL_PROTOCOL_CORRECTION",
                protocolFailure
              );
              progress.Messages.Add(
                new ChatMessage(
                  "user",
                  protocolCorrection
                )
              );
              progress.ToolMessages.Add(
                new OllamaToolMessage(
                  "user",
                  protocolCorrection
                )
              );
              yield return Event(
                requestId,
                "action.tool-protocol-repair-requested",
                $"Model {model} received one materially different Host correction after its invalid native tool call. Recovery budget: "
                  + $"{progress.RecoveryAttemptCount}/{settings.MaxRecoveryAttemptsPerTurn}.",
                stopwatch,
                model,
                intention
              );
              continue;
            }

            var checkpoint = CreateRecoveryCheckpoint(
              requestId,
              stopwatch,
              model,
              intention,
              protocolRecoveryLimitFailure?.TechnicalMessage
                ?? protocolFailure.TechnicalMessage,
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

          if (
            planningFailureCategory != CoordinatorFailureCategory.CorrectablePlanning
            || planningFailures + 1 >= maximumPlanningAttempts
          )
          {
            var planningReview = await TryReviewFunctionGemmaFailureAsync(
              baseUri,
              request,
              applicationSettings,
              settings,
              progress,
              planning.Failure,
              FunctionGemmaFailureCode(
                planning.Failure,
                planningFailureCategory
              ),
              "invalid_proposal",
              null,
              "none",
              cancellationToken
            );
            foreach (var reviewEvent in FunctionGemmaReviewEvents(
              planningReview,
              requestId,
              stopwatch,
              applicationSettings.ActionModel,
              intention
            ))
            {
              yield return reviewEvent;
            }
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
            && planningFailures < maximumPlanningAttempts
          )
          {
            var planningCorrection = CreatePlanningCorrection(
              "LOCAL_ACTION_PLANNING_CORRECTION",
              planning.Failure
            );
            progress.Messages.Add(
              new ChatMessage(
                "user",
                planningCorrection
              )
            );
            progress.ToolMessages.Add(
              new OllamaToolMessage(
                "user",
                planningCorrection
              )
            );

            if (!completionAllowed)
            {
              AddCompletionRejectedMessage(
                progress
              );
            }
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
          UsesFunctionGemmaSupervision(
            applicationSettings,
            model
          )
        )
        {
          progress.ResidentCoexistenceChecked = true;
          var coexistence = await _residentModel.EnsureResidentAlongsideTargetAsync(
            model,
            cancellationToken
          );
          yield return Event(
            requestId,
            coexistence.ResidentLoaded && coexistence.TargetLoaded
              ? "agent.functiongemma-supervision-ready"
              : "agent.functiongemma-supervision-unavailable",
            coexistence.ResidentLoaded && coexistence.TargetLoaded
              ? coexistence.Reasserted
                ? $"FunctionGemma resident {applicationSettings.ActionModel} was reasserted after Teacher {model} loaded; both exact models are verified concurrently in Ollama."
                : $"FunctionGemma resident {applicationSettings.ActionModel} and Teacher {model} are verified concurrently in Ollama."
              : $"FunctionGemma resident coexistence with Teacher {model} was not verified ({coexistence.Outcome}); the Host will continue the Teacher path without a reload loop.",
            stopwatch,
            applicationSettings.ActionModel,
            intention
          );
        }

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

            AddCompletionRejectedMessage(
              progress
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
        progress.ActiveToolCallId = planningResult.CallId;

        if (proposal.Tool == LocalActionPlanner.RequestToolsetTool)
        {
          progress.ToolsetRequestCount++;
          var grant = TryGrantToolset(
            proposal,
            progress
          );
          actionBudget--;

          if (grant.Failure is not null)
          {
            planningFailures++;
            _executionSession?.RecordPlanningFailure();
            exhaustedFailure = grant.Failure;
            progress.ToolMessages.Add(
              NativeToolResultMessage(
                progress,
                proposal.Tool,
                "rejected",
                grant.Failure.Message
              )
            );
            yield return Event(
              requestId,
              "agent.toolset-request-rejected",
              $"Solicitação de ferramentas rejeitada: {grant.Failure.Message}",
              stopwatch,
              model,
              intention
            );

            if (progress.ToolsetRequestCount >= MaximumToolsetRequestsPerTurn)
            {
              planningFailures = maximumPlanningAttempts;
              break;
            }

            toolsetHandled = true;
            break;
          }

          foreach (var resolution in grant.Resolutions)
          {
            _executionSession?.RecordToolNameResolution(
              new LocalActionProposal(
                resolution.CanonicalName,
                JsonSerializer.SerializeToElement(
                  new { }
                ),
                null,
                resolution.OriginalName,
                resolution.Source
              ),
              "toolset-granted"
            );
          }

          planningFailures = 0;
          _executionSession?.ResetPlanningFailures();
          var requestedNames = string.Join(
            ", ",
            grant.Resolutions.Select(
              resolution => resolution.Normalized
                ? $"{resolution.OriginalName} → {resolution.CanonicalName}"
                : resolution.OriginalName
            )
          );
          var result = JsonSerializer.Serialize(
            new
            {
              status = "granted",
              requested = grant.Resolutions.Select(
                resolution => new
                {
                  original = resolution.OriginalName,
                  canonical = resolution.CanonicalName,
                  source = resolution.Source
                }
              ),
              grantedTools = progress.GrantedTools.Order(
                StringComparer.Ordinal
              )
            }
          );
          progress.ToolMessages.Add(
            NativeToolResultMessage(
              progress,
              proposal.Tool,
              "completed",
              result,
              false
            )
          );
          yield return Event(
            requestId,
            "agent.toolset-requested",
            $"Agente solicitou: {requestedNames}.",
            stopwatch,
            model,
            intention
          );
          toolsetHandled = true;
          break;
        }

        if (ShouldRejectReadOnlyProposalWhileCompletionBlocked(proposal))
        {
          var completionFailure = new LocalActionException(
            "completion-facts",
            "The proposed read-only action cannot resolve the Host-verified completion issues."
          );
          _executionSession?.RecordToolNameResolution(
            proposal,
            "rejected-read-only-while-completion-blocked"
          );
          progress.ToolMessages.Add(
            NativeToolResultMessage(
              progress,
              proposal.Tool,
              "rejected",
              completionFailure.Message
            )
          );
          AddCompletionRejectedMessage(
            progress
          );
          planningFailures++;
          _executionSession?.RecordPlanningFailure();
          exhaustedFailure = completionFailure;
          yield return Event(
            requestId,
            "action.read-only-completion-correction",
            "Host rejected a redundant read-only proposal and returned the exact unresolved completion facts. The specialist must make a materially different mutation or state a safe blocker.",
            stopwatch,
            model,
            intention
          );
          continue;
        }

        if (CanCompleteAfterRedundantReadOnlyProposal(proposal, progress))
        {
          const string warning =
            "The specialist proposed another read-only action after the Host verified the requested mutation, reviewed every latest changed file, and found no unresolved completion issue. "
            + "The Host skipped the redundant proposal and completed from verified workspace facts.";
          _executionSession?.RecordToolNameResolution(
            proposal,
            "skipped-redundant-after-completion"
          );
          _executionSession?.AddWarning(warning);
          yield return Event(
            requestId,
            "action.redundant-read-skipped",
            warning,
            stopwatch,
            model,
            intention
          );
          noActionRequired = true;
          exhaustedFailure = null;
          break;
        }

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
            var acceptedPlan = JsonSerializer.Serialize(
              plan
            );
            progress.Messages.Add(
              new ChatMessage(
                "user",
                $"LOCAL_ACTION_RESULT\nTool: {proposal.Tool}\nStatus: completed\n"
                  + $"Output:\nAccepted Host plan:\n{acceptedPlan}\n"
                  + "Now propose the first required local action and bind it to one exact pending stepId."
              )
            );
            progress.ToolMessages.Add(
              NativeToolResultMessage(
                progress,
                proposal.Tool,
                "completed",
                $"Accepted Host plan:\n{acceptedPlan}"
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
          if (!progress.ToolScope.Allows(proposal.Tool))
          {
            validation = new ValidationAttempt(
              null,
              new LocalActionException(
                "action-tool-scope",
                $"Tool '{proposal.Tool}' is outside the Host-owned tool scope for this user request. "
                  + "Choose one of the tools offered for the current turn."
              )
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
          protocolRepairAttempted = false;
          semanticRepairAttempted = false;
          semanticFailureFingerprint = null;
          _executionSession?.ResetPlanningFailures();
          validatedAction = validation.Action;
          break;
        }

        var exception = validation.Failure;
        progress.ToolMessages.Add(
          NativeToolResultMessage(
            progress,
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
              NativeToolResultMessage(
                progress,
                proposal.Tool,
                "policy-denied",
                exception.Message
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
          progress.ToolMessages.Add(
            NativeToolResultMessage(
              progress,
              proposal.Tool,
              "policy-denied",
              exception.Message
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
          var validationReview = await TryReviewFunctionGemmaFailureAsync(
            baseUri,
            request,
            applicationSettings,
            settings,
            progress,
            exception,
            FunctionGemmaFailureCode(
              exception,
              failureCategory
            ),
            "tool_call",
            proposal.Tool,
            proposal.Tool,
            cancellationToken
          );
          foreach (var reviewEvent in FunctionGemmaReviewEvents(
            validationReview,
            requestId,
            stopwatch,
            applicationSettings.ActionModel,
            intention
          ))
          {
            yield return reviewEvent;
          }

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

      if (toolsetHandled || planHandled)
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

      foreach (var correction in action.Corrections ?? [])
      {
        var correctionSummary = $"Host corrected {action.Tool}.{correction.Field} from "
          + $"'{BoundedCorrectionValue(correction.OriginalValue)}' to "
          + $"'{BoundedCorrectionValue(correction.EffectiveValue)}'. {correction.Reason}";
        _executionSession?.AddWarning(
          correctionSummary
        );
        var correctionMessage = "LOCAL_ACTION_CORRECTION\n"
          + $"Tool: {action.Tool}\n"
          + $"Field: {correction.Field}\n"
          + $"Original: {BoundedCorrectionValue(correction.OriginalValue)}\n"
          + $"Effective: {BoundedCorrectionValue(correction.EffectiveValue)}\n"
          + $"Reason: {correction.Reason}\n"
          + "The Host will execute only the effective value inside the trusted workspace. Continue from that effective value.";
        progress.Messages.Add(
          new ChatMessage(
            "user",
            correctionMessage
          )
        );
        progress.ToolMessages.Add(
          new OllamaToolMessage(
            "user",
            correctionMessage
          )
        );
        yield return Event(
          requestId,
          "action.input-corrected",
          correctionSummary,
          stopwatch,
          model,
          intention
        );
      }

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
        action.Tool,
        action.TargetPath,
        action.PlanStepId
      ) == true;
      if (planStepStarted)
      {
        yield return Event(
          requestId,
          "execution-step-started",
          $"Specialist action bound to Host plan step '{action.PlanStepId}': {action.Summary}.",
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
              progress,
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
            progress,
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
        if (_executionSession?.Plan is not null)
        {
          var planStepCompleted = _executionSession.RecordPlanActionResult(
            action.ActionId,
            action.Tool,
            "completed"
          );
          yield return Event(
            requestId,
            planStepCompleted
              ? "execution-step-completed"
              : "execution-step-effect-unproven",
            planStepCompleted
              ? $"Bound plan step completed from the action's proven effect: {action.Summary}."
              : $"Bound plan step remains in progress because the action's required effect was not proven: {action.Summary}.",
            stopwatch,
            model,
            intention
          );
        }
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
            progress,
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
        ) && progress.AutomaticStrategyRevisionCount < settings.MaxRecoveryAttemptsPerTurn)
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
                + $"The specialist repeated the previous strategy {MaximumIdenticalStrategyAttempts} times, so that strategy was rejected. "
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
              $"Specialist model {recoverySpecialistModel} repeated the previous strategy {MaximumIdenticalStrategyAttempts} times. "
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
          $"Execution failed for {action.Tool}; the result was returned to the active specialist for replanning. "
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

    for (
      var attempt = 2;
      attempt <= MaximumIdenticalStrategyAttempts
        && IsIdenticalGuidance(
          guidance.Guidance,
          previousGuidance
        );
      attempt++
    )
    {
      rejectedUnchangedCandidate = true;
      revisionMessages.Add(
        new ChatMessage(
          "user",
            "RESIDENT_STRATEGY_SUPERVISION_REJECTED\n"
            + "The proposed strategy is identical to the strategy that already failed and was not accepted. "
            + "Change the tool, path, sequence, or arguments to address the authoritative failure. "
            + $"Do not return the same JSON again. Attempt {attempt} of {MaximumIdenticalStrategyAttempts}."
        )
      );
      guidance = await TryPrepareGuidanceAsync(
        baseUri,
        specialistModel,
        revisionMessages,
        cancellationToken
      );
    }

    var repeatedPreviousStrategy = IsIdenticalGuidance(
      guidance.Guidance,
      previousGuidance
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

  private static bool IsIdenticalGuidance(
    ExpertExecutionGuidance? guidance,
    string? previousGuidance
  )
  {
    return guidance is not null
      && previousGuidance is not null
      && string.Equals(
        ExpertExecutionGuidanceService.Serialize(
          guidance
        ),
        previousGuidance,
        StringComparison.Ordinal
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

      if (!string.IsNullOrEmpty(
        update.ThinkingDelta
      ))
      {
        progress.ResponseSegment.Clear();
        progress.ResponseSegmentActive = false;
        yield return Event(
          requestId,
          "reasoning.delta",
          null,
          stopwatch,
          model,
          intention
        ) with
        {
          ReasoningDelta = update.ThinkingDelta
        };
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
      if (!progress.ResponseSegmentActive)
      {
        progress.ResponseSegment.Clear();
        progress.ResponseSegmentActive = true;
      }
      progress.ResponseSegment.Append(safeDelta);
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
        _executionSession?.CreateSummary(),
        ResponseSegmentHtml: _markdownRenderer.Render(
          progress.ResponseSegment.ToString()
        )
      );
    }

    if (progress.PrefixBuffer.Length > 0 && !progress.PrefixResolved)
    {
      var finalPrefix = progress.PrefixBuffer.ToString();
      progress.PrefixBuffer.Clear();
      progress.PrefixResolved = true;
      progress.ReceivedFirstChunk = true;
      progress.Answer.Append(finalPrefix);
      if (!progress.ResponseSegmentActive)
      {
        progress.ResponseSegment.Clear();
        progress.ResponseSegmentActive = true;
      }
      progress.ResponseSegment.Append(finalPrefix);
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
        _executionSession?.CreateSummary(),
        ResponseSegmentHtml: _markdownRenderer.Render(
          progress.ResponseSegment.ToString()
        )
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

  private static ContextUsageView CreateExternalHarnessContextUsage(
    ConversationContextResult context,
    ProviderModelCapabilities capabilities,
    ApplicationSettings settings
  )
  {
    var usage = CreateContextUsage(context, capabilities, settings, null);
    var effectiveLimit = Math.Min(
      usage.ApplicationLimit,
      usage.ProviderMaximumTokens ?? usage.ConfiguredProviderLimit
    );
    return usage with
    {
      ConversationTokens = context.EstimatedInputTokens,
      RequiredContextTokens = context.EstimatedInputTokens + usage.ReservedResponseTokens,
      EffectiveLimitTokens = effectiveLimit,
      InferenceSequence = 1
    };
  }

  private static ContextUsageView WithExactContextUsage(
    ContextUsageView usage,
    long inputTokens
  )
  {
    var effectiveLimit = usage.EffectiveLimitTokens > 0
      ? usage.EffectiveLimitTokens
      : Math.Min(
        usage.ApplicationLimit,
        usage.ProviderMaximumTokens ?? usage.ConfiguredProviderLimit
      );
    var usable = Math.Max(1, effectiveLimit - usage.ReservedResponseTokens);
    var percentage = inputTokens * 100d / usable;
    var warning = percentage >= 95
      ? 95
      : percentage >= 85
        ? 85
        : percentage >= 70
          ? 70
          : 0;
    return usage with
    {
      InputTokens = inputTokens,
      Accuracy = "exact",
      RequiredContextTokens = inputTokens + usage.ReservedResponseTokens,
      EffectiveLimitTokens = effectiveLimit,
      WarningThreshold = warning
    };
  }

  private static ContextUsageView CreateSpecialistContextUsage(
    SpecialistContextMeasurement measurement,
    CoordinatorInputBudget budget,
    ApplicationSettings settings,
    int? providerMaximumTokens,
    int visibleMessages,
    int conversationOmittedMessages,
    int inferenceSequence,
    bool compacted,
    long beforeCompactionTokens,
    long afterCompactionTokens,
    int omittedBlocks,
    ProviderTokenUsage? providerUsage = null
  )
  {
    var inputTokens = providerUsage?.InputTokens ?? measurement.InputTokens;
    var required = inputTokens + budget.ReservedOutputTokens;
    var percentage = required * 100d / Math.Max(1, budget.MaximumContextTokens);
    var warning = percentage >= 95
      ? 95
      : percentage >= 85
        ? 85
        : percentage >= 70
          ? 70
          : 0;
    return new ContextUsageView(
      visibleMessages,
      measurement.IncludedMessages,
      conversationOmittedMessages,
      measurement.SystemInstructionTokens,
      measurement.CurrentUserMessageTokens,
      inputTokens,
      providerUsage is null
        ? "estimated"
        : "exact",
      providerMaximumTokens,
      settings.Context.ProviderContextTokens,
      settings.Context.DefaultContextTokens,
      budget.ReservedOutputTokens,
      conversationOmittedMessages > 0 || omittedBlocks > 0,
      warning,
      measurement.ConversationTokens,
      measurement.ProjectContextTokens,
      measurement.ToolDiscoveryTokens,
      measurement.GrantedToolSchemaTokens,
      measurement.HostStateTokens,
      measurement.StructuralOverheadTokens,
      required,
      budget.MaximumContextTokens,
      measurement.Estimator,
      inferenceSequence,
      compacted,
      measurement.CompactionEligible,
      beforeCompactionTokens,
      afterCompactionTokens,
      omittedBlocks
    );
  }

  private static bool ShouldCompactContext(
    SpecialistContextMeasurement measurement,
    CoordinatorInputBudget budget,
    ExecutionProgress progress
  )
  {
    var required = measurement.InputTokens + budget.ReservedOutputTokens;
    var percentage = required * 100d / Math.Max(
      1,
      budget.MaximumContextTokens
    );
    return measurement.InputTokens > budget.MaximumInputTokens
      || progress.ManualCompactionPending
      || percentage >= 85;
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
    string usageModelRole,
    CancellationToken cancellationToken
  )
  {
    ProviderTokenUsage? providerUsage = null;
    var guidance = await _expertGuidance.PrepareAsync(
      baseUri,
      model,
      progress.Messages,
      UsageContext(
        model,
        usageModelRole,
        "structured-action-coordination"
      ),
      cancellationToken,
      usage => providerUsage = usage
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
        true,
        Usage: providerUsage
      );
    }

    var action = guidance.Actions.Single();
    var planStepId = action.Arguments.TryGetProperty(
      "stepId",
      out var stepIdElement
    ) && stepIdElement.ValueKind == JsonValueKind.String
      ? stepIdElement.GetString()
      : null;
    var structuredProposal = new LocalActionProposal(
      action.Tool,
      RemoveJsonProperty(
        action.Arguments,
        "stepId"
      ),
      action.Title,
      action.OriginalTool,
      action.ToolResolutionSource,
      planStepId
    );
    return new LocalActionPlanningResult(
      structuredProposal,
      assistantMessage,
      false,
      Usage: providerUsage
    );
  }

  private static JsonElement RemoveJsonProperty(
    JsonElement value,
    string propertyName
  )
  {
    if (!value.TryGetProperty(propertyName, out _))
    {
      return value.Clone();
    }
    var node = JsonNode.Parse(
      value.GetRawText()
    )!.AsObject();
    node.Remove(
      propertyName
    );
    return JsonSerializer.SerializeToElement(
      node
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

  private ToolsetGrantAttempt TryGrantToolset(
    LocalActionProposal proposal,
    ExecutionProgress progress
  )
  {
    try
    {
      if (progress.ToolsetRequestCount > MaximumToolsetRequestsPerTurn)
      {
        throw new LocalActionException(
          "toolset-negotiation-budget",
          $"The specialist exceeded the bounded allowance of {MaximumToolsetRequestsPerTurn} toolset requests for this turn."
        );
      }

      if (proposal.Arguments.ValueKind != JsonValueKind.Object)
      {
        throw new LocalActionException(
          "toolset-negotiation",
          "request_toolset arguments must be a JSON object."
        );
      }

      var unexpected = proposal.Arguments.EnumerateObject().Select(
        property => property.Name
      ).FirstOrDefault(
        property => property is not "tools" and not "reason"
      );
      if (unexpected is not null)
      {
        throw new LocalActionException(
          "toolset-negotiation",
          $"request_toolset does not accept property '{unexpected}'."
        );
      }

      if (
        !proposal.Arguments.TryGetProperty(
          "tools",
          out var tools
        )
        || tools.ValueKind != JsonValueKind.Array
      )
      {
        throw new LocalActionException(
          "toolset-negotiation",
          "request_toolset requires a tools array."
        );
      }

      var requested = tools.EnumerateArray().ToArray();
      if (requested.Length is < 1 or > 8)
      {
        throw new LocalActionException(
          "toolset-negotiation",
          "request_toolset must name between 1 and 8 tools."
        );
      }

      if (
        proposal.Arguments.TryGetProperty(
          "reason",
          out var reason
        )
        && (
          reason.ValueKind != JsonValueKind.String
          || (reason.GetString()?.Length ?? 0) > 1_000
        )
      )
      {
        throw new LocalActionException(
          "toolset-negotiation",
          "request_toolset reason must be a string of at most 1000 characters."
        );
      }

      var resolutions = new List<ToolNameResolution>();
      var requestedCanonical = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase
      );

      foreach (var requestedTool in requested)
      {
        if (
          requestedTool.ValueKind != JsonValueKind.String
          || string.IsNullOrWhiteSpace(
            requestedTool.GetString()
          )
        )
        {
          throw new LocalActionException(
            "toolset-negotiation",
            "Every requested tool name must be a non-empty string."
          );
        }

        var resolution = _toolNames.Resolve(
          requestedTool.GetString()!,
          progress.ToolScope.AvailableTools
        );
        if (!requestedCanonical.Add(
          resolution.CanonicalName
        ))
        {
          throw new LocalActionException(
            "toolset-negotiation",
            $"Tool '{resolution.OriginalName}' duplicates canonical tool '{resolution.CanonicalName}' in the same request."
          );
        }
        resolutions.Add(
          resolution
        );
      }

      if (resolutions.All(
        resolution => progress.GrantedTools.Contains(
          resolution.CanonicalName
        )
      ))
      {
        throw new LocalActionException(
          "toolset-negotiation",
          "Every requested tool schema is already granted; request only a missing capability or continue with the enabled tools."
        );
      }

      foreach (var resolution in resolutions)
      {
        progress.GrantedTools.Add(
          resolution.CanonicalName
        );
      }

      return new ToolsetGrantAttempt(
        resolutions,
        null
      );
    }
    catch (LocalActionException exception)
    {
      return new ToolsetGrantAttempt(
        [],
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

  private void ValidateInteractionMode(
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
      string.IsNullOrWhiteSpace(request.Harness)
      || !_harnesses.TryGetDefinition(request.Harness, out _)
    )
    {
      throw new ChatStageException(
        "request-validation",
        "The selected harness is not registered.",
        $"Unsupported harness: {request.Harness}.",
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

  private HarnessDefinition GetHarnessDefinition(string harnessId)
  {
    if (_harnesses.TryGetDefinition(harnessId, out var definition))
    {
      return definition;
    }

    throw new ChatStageException(
      "request-validation",
      "The selected harness is not registered.",
      $"Unsupported harness: {harnessId}.",
      null,
      null,
      400,
      true
    );
  }

  private static string HarnessLabel(HarnessDefinition definition)
  {
    return definition.Experimental
      ? $"{definition.DisplayName} (Experimental)"
      : definition.DisplayName;
  }

  private static string HarnessProviderUnsupportedMessage(
    HarnessDefinition definition
  )
  {
    if (
      definition.SupportedProviders is [ModelProviderIds.OllamaLocal]
    )
    {
      return $"{HarnessLabel(definition)} supports Ollama Local models only.";
    }

    return $"{HarnessLabel(definition)} does not support the selected provider.";
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
      or "create_files"
      or "write_file"
      or "replace_text"
      or "apply_patch"
      or "delete_paths"
      or "create_directory"
      or "run_process"
      or "git_stage_files"
      or "git_unstage_files";
  }

  private static bool IsPathListApprovalAction(
    ValidatedLocalAction action
  )
  {
    return action.Tool is "delete_paths" or "git_stage_files" or "git_unstage_files";
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

  private OllamaToolMessage NativeToolResultMessage(
    ExecutionProgress progress,
    string tool,
    string status,
    string output,
    bool effectVerified = true
  )
  {
    var succeeded = string.Equals(
      status,
      "completed",
      StringComparison.Ordinal
    );
    return _toolingProtocol.CreateToolResultMessage(
      progress.ToolingProfile,
      new CanonicalToolResult(
        progress.ActiveToolCallId ?? $"host_{Guid.NewGuid():N}",
        tool,
        status,
        output,
        succeeded,
        succeeded && effectVerified
      )
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
    string model,
    int? providerMaximumTokens
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
    var effectiveLimit = new[]
    {
      resolution.MaximumContextTokens,
      settings.Context.DefaultContextTokens,
      settings.Context.ProviderContextTokens,
      providerMaximumTokens ?? int.MaxValue
    }.Min();
    return new CoordinatorInputBudget(
      Math.Max(1, effectiveLimit - resolution.OutputTokenLimit),
      effectiveLimit,
      resolution.OutputTokenLimit,
      Math.Min(
        resolution.EffectiveContextTokens,
        effectiveLimit
      )
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
  ) => UsageModelRoles.Specialist;

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
        "LOCAL_ACTION_PLANNING_CORRECTION",
        "LOCAL_ACTION_CORRECTION",
        "STRUCTURED_ACTION_CORRECTION",
        "TOOL_PROTOCOL_CORRECTION",
        "EXECUTION_COMPLETION_REJECTED",
        "HOST_COMPLETION_FACTS",
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
    var plan = _executionSession?.Plan;

    if (
      _executionSession?.RequiresMutation == true
      && _executionSession.HasVerifiedMutation == false
    )
    {
      return false;
    }

    if (
      _executionSession?.HasVerifiedChangedFiles == true
      && _executionSession.HasReviewedChangedFiles == false
    )
    {
      return false;
    }

    if (_executionSession?.UnresolvedChangedFileReferences.Count > 0)
    {
      return false;
    }

    if (_executionSession?.StaticCompletionIssues.Count > 0)
    {
      return false;
    }

    if (progress.Guidance?.ActionRequired == false)
    {
      return true;
    }

    if (plan is null)
    {
      return true;
    }

    if (
      plan.Steps.Count == 0
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

  private bool CanCompleteAfterRejectedUnavailableTool(
    Exception failure,
    ExecutionProgress progress
  )
  {
    if (
      failure is not LocalActionException localAction
      || !string.Equals(
        localAction.Stage,
        "tool-phase-validation",
        StringComparison.Ordinal
      )
      || string.IsNullOrWhiteSpace(localAction.ProposedCanonicalTool)
      || progress.ToolScope.Allows(localAction.ProposedCanonicalTool)
    )
    {
      return false;
    }

    var session = _executionSession;
    return session is not null
      && session.RequiresMutation
      && session.HasVerifiedMutation
      && session.HasReviewedChangedFiles
      && session.UnresolvedChangedFileReferences.Count == 0
      && session.StaticCompletionIssues.Count == 0
      && !session.HasPendingMutationPlanStep;
  }

  private bool CanCompleteAfterRedundantReadOnlyProposal(
    LocalActionProposal proposal,
    ExecutionProgress progress
  )
  {
    if (!IsReadOnlyInspectionTool(proposal.Tool))
    {
      return false;
    }

    var session = _executionSession;
    return session is not null
      && session.RequiresMutation
      && session.HasVerifiedMutation
      && session.HasReviewedChangedFiles
      && session.UnresolvedChangedFileReferences.Count == 0
      && session.StaticCompletionIssues.Count == 0
      && !session.HasPendingMutationPlanStep
      && CanCompletePlanning(progress);
  }

  private bool ShouldRejectReadOnlyProposalWhileCompletionBlocked(
    LocalActionProposal proposal
  )
  {
    var session = _executionSession;
    return session is not null
      && IsReadOnlyInspectionTool(proposal.Tool)
      && session.HasVerifiedChangedFiles
      && session.HasReviewedChangedFiles
      && (
        session.UnresolvedChangedFileReferences.Count > 0
          || session.StaticCompletionIssues.Count > 0
      );
  }

  private static bool IsReadOnlyInspectionTool(string tool)
  {
    return tool is "list_files"
      or "read_file"
      or "get_file_info"
      or "search_text"
      or "git_status"
      or "git_diff"
      or "git_log"
      or "git_show_commit";
  }

  private static bool FunctionGemmaFailureReviewEnabled => false;

  private bool UsesFunctionGemmaSupervision(
    ApplicationSettings settings,
    string targetModel
  )
  {
    return FunctionGemmaFailureReviewEnabled && _functionGemma.Supports(
      settings.ActionModel
    ) && !string.Equals(
      settings.ActionModel,
      targetModel,
      StringComparison.OrdinalIgnoreCase
    );
  }

  private OllamaToolMessage CreateCompletionRejectedMessage()
  {
    var pendingSteps = _executionSession?.Plan?.Steps.Where(
      step => step.Status != "completed"
    ).Select(
      step => $"{step.Id}: {step.Title}"
    ).ToArray() ?? [];
    string requirement;
    if (pendingSteps.Length > 0)
    {
      requirement = $"The visible execution plan still has pending steps: {string.Join("; ", pendingSteps)}. "
        + "Use one available tool only if it can materially advance the next required step.";
    }
    else if (
      _executionSession?.RequiresMutation == true
      && _executionSession.HasVerifiedMutation == false
    )
    {
      requirement = "The objective requires a mutation, but the Host has no verified mutation effect. Use an available mutation tool only if the requested effect can be completed safely.";
    }
    else if (
      _executionSession?.HasVerifiedChangedFiles == true
      && _executionSession.HasReviewedChangedFiles == false
    )
    {
      requirement = "The latest changed files have not all been inspected after their latest mutation: "
        + string.Join(", ", _executionSession.UnreviewedChangedFiles)
        + ". Use read_file on one listed path, verify its explicit content and cross-file references against the objective, then continue any missing work.";
    }
    else if (_executionSession?.UnresolvedChangedFileReferences.Count > 0)
    {
      requirement = "Changed HTML contains unresolved local asset references: "
        + string.Join(", ", _executionSession.UnresolvedChangedFileReferences)
        + ". Create or correct one referenced HTML, CSS, or JavaScript asset, then inspect the latest changed file before completion.";
    }
    else if (_executionSession?.StaticCompletionIssues.Count > 0)
    {
      requirement = "Host static completion review found: "
        + string.Join(" ", _executionSession.StaticCompletionIssues)
        + " Correct one reported content or cross-file behavior issue, then inspect every latest changed file before completion.";
    }
    else
    {
      requirement = "The Host still lacks a required verified effect for this objective.";
    }
    return new OllamaToolMessage(
      "user",
      $"HOST_COMPLETION_FACTS\nCompletion accepted: false\nMissing requirement: {requirement} "
        + "Change strategy. Propose one available tool only if it materially advances the objective; otherwise return a concise final response that states the safe blocker."
    );
  }

  private void AddCompletionRejectedMessage(
    ExecutionProgress progress
  )
  {
    var message = CreateCompletionRejectedMessage();
    progress.ToolMessages.Add(
      message
    );
    progress.Messages.Add(
      new ChatMessage(
        message.Role,
        message.Content ?? string.Empty
      )
    );
  }

  private async Task<FunctionGemmaReviewAttempt?> TryReviewFunctionGemmaFailureAsync(
    Uri baseUri,
    ChatRequest request,
    ApplicationSettings applicationSettings,
    ExecutionSettings executionSettings,
    ExecutionProgress progress,
    Exception failure,
    string failureCode,
    string observedKind,
    string? observedTool,
    string expectedTool,
    CancellationToken cancellationToken
  )
  {
    if (!FunctionGemmaFailureReviewEnabled || !_functionGemma.Supports(
      applicationSettings.ActionModel
    ))
    {
      return null;
    }

    var resident = _residentModel.GetStatus();
    if (
      !resident.Loaded
      || !string.Equals(
        resident.ConfiguredModel,
        applicationSettings.ActionModel,
        StringComparison.OrdinalIgnoreCase
      )
    )
    {
      return new FunctionGemmaReviewAttempt(
        null,
        null,
        "The FunctionGemma resident is not currently loaded; the Host will continue with its typed recovery policy."
      );
    }

    var failedStep = _executionSession?.Plan?.Steps.FirstOrDefault(
      step => step.Status != "completed"
    )?.Id ?? "none";
    var criteria = _executionSession?.Plan?.Steps.Select(
      step => step.Title
    ).Take(
      8
    ).ToArray() ??
    [
      "Complete the user objective using only verified Host effects."
    ];
    var availableTools = expectedTool == "none"
      ? Array.Empty<string>()
      : new[]
      {
        expectedTool
      };

    try
    {
      var review = await _functionGemma.ReviewFailureAsync(
        baseUri,
        applicationSettings.ActionModel,
        new FunctionGemmaFailureContext(
          request.Message,
          failureCode,
          failedStep,
          FunctionGemmaFailureStage(
            failure
          ),
          failure.InnerException?.Message ?? failure.Message,
          observedKind,
          observedTool,
          expectedTool,
          availableTools,
          criteria,
          Math.Max(
            0,
            executionSettings.MaxRecoveryAttemptsPerTurn
              - progress.RecoveryAttemptCount
          )
        ),
        UsageContext(
          applicationSettings.ActionModel,
          UsageModelRoles.Action,
          "functiongemma-evaluator"
        ),
        UsageContext(
          applicationSettings.ActionModel,
          UsageModelRoles.Action,
          "functiongemma-recovery"
        ),
        cancellationToken
      );
      return new FunctionGemmaReviewAttempt(
        review,
        null,
        null
      );
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      _logger.LogWarning(
        exception,
        "FunctionGemma evaluator or recovery contract failed for request {RequestId}; Host recovery remains authoritative.",
        _usageTurnId
      );
      return new FunctionGemmaReviewAttempt(
        null,
        exception,
        null
      );
    }
  }

  private IEnumerable<ChatStreamEvent> FunctionGemmaReviewEvents(
    FunctionGemmaReviewAttempt? attempt,
    string requestId,
    Stopwatch stopwatch,
    string model,
    string intention
  )
  {
    if (attempt?.Review is not null)
    {
      yield return Event(
        requestId,
        "agent.functiongemma-evaluation-explained",
        $"FunctionGemma explained the Host-owned NACK: {attempt.Review.EvaluationReason}",
        stopwatch,
        model,
        intention
      );
      yield return Event(
        requestId,
        "agent.functiongemma-recovery-selected",
        $"FunctionGemma copied Host recovery policy {attempt.Review.Recovery.Action} for {attempt.Review.Recovery.FailureCode}; next tool: {attempt.Review.Recovery.NextTool}. Reason: {attempt.Review.Recovery.Reason}",
        stopwatch,
        model,
        intention
      );
      yield break;
    }

    if (attempt?.Failure is not null)
    {
      yield return Event(
        requestId,
        "agent.functiongemma-contract-warning",
        $"FunctionGemma evaluator/recovery output was rejected at {FunctionGemmaFailureStage(attempt.Failure)}; the Host-owned recovery path will continue.",
        stopwatch,
        model,
        intention
      );
      yield break;
    }

    if (attempt?.SkippedReason is not null)
    {
      yield return Event(
        requestId,
        "agent.functiongemma-review-skipped",
        attempt.SkippedReason,
        stopwatch,
        model,
        intention
      );
    }
  }

  private static string FunctionGemmaFailureCode(
    Exception failure,
    CoordinatorFailureCategory category
  )
  {
    var stage = FunctionGemmaFailureStage(
      failure
    );
    return category switch
    {
      CoordinatorFailureCategory.SecurityDenied => "unsafe_security_boundary",
      CoordinatorFailureCategory.PolicyDenied => "approval_or_policy_denied",
      CoordinatorFailureCategory.ToolExecution => "tool_execution_failed",
      CoordinatorFailureCategory.ContextFit => "context_fit_failed",
      CoordinatorFailureCategory.Provider => "provider_failed",
      _ when stage.Contains("tool-name", StringComparison.Ordinal) => "wrong_tool",
      _ when stage.Contains("path", StringComparison.Ordinal) => "invalid_path",
      _ when stage.Contains("stale", StringComparison.Ordinal)
        || stage.Contains("conflict", StringComparison.Ordinal) => "stale_state",
      _ when failure is ToolProtocolException => "invalid_native_protocol",
      _ => "invalid_proposal"
    };
  }

  private static string FunctionGemmaFailureStage(
    Exception failure
  )
  {
    return failure switch
    {
      FunctionGemmaProtocolException protocol => protocol.Stage,
      LocalActionException localAction => localAction.Stage,
      OllamaProviderException provider => provider.Stage,
      _ => "functiongemma-contract"
    };
  }

  private static string CreatePlanningCorrection(
    string marker,
    Exception failure
  )
  {
    var stage = failure is LocalActionException localAction
      ? localAction.Stage
      : failure is OllamaProviderException provider
        ? provider.Stage
        : "local-action-planning";
    var detail = failure.InnerException?.Message ?? failure.Message;
    var unavailableToolCorrection = failure is LocalActionException
    {
      Stage: "tool-phase-validation",
      ProposedCanonicalTool: not null
    } unavailableTool
        ? $" The specific tool '{BoundedCorrectionValue(unavailableTool.ProposedCanonicalTool)}' is not offered for this turn; do not call it again. Use only a native tool definition present in the current request."
        : string.Empty;
    return $"{marker}\n"
      + $"Rejected stage: {BoundedCorrectionValue(stage)}\n"
      + $"Exact correction: {BoundedCorrectionValue(detail)}\n"
      + "The rejected output was not executed. Keep the user objective, but change the proposal. "
      + unavailableToolCorrection
      + "Return one available native tool call with a complete JSON object only when another action is necessary, "
      + "or return a concise final response without a tool call when the verified work is complete or no safe action remains. "
      + "Correct tool spelling, required fields, value types, and JSON structure; do not repeat the rejected output.";
  }

  private static string BoundedCorrectionValue(
    string value
  )
  {
    const int maximumLength = 1_000;
    var sanitized = new string(
      value.Select(
        character => char.IsControl(
          character
        )
          ? ' '
          : character
      ).ToArray()
    ).Trim();
    return sanitized.Length <= maximumLength
      ? sanitized
      : sanitized[..maximumLength] + "...";
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
      IncidentSequence: _trace.NextSequence(),
      Gpu: _usageGpu
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
      proposal.Tool == "create_files"
      && proposal.Arguments.TryGetProperty("files", out var files)
      && files.ValueKind == JsonValueKind.Array
    )
    {
      try
      {
        foreach (var file in files.EnumerateArray())
        {
          if (
            file.ValueKind != JsonValueKind.Object
            || !file.TryGetProperty("path", out var path)
            || path.ValueKind != JsonValueKind.String
          )
          {
            continue;
          }
          var relativePath = (await _workspace.ResolveCreationPathAsync(
            path.GetString(),
            cancellationToken
          )).RelativePath;
          _executionSession?.ApplyInstructions(
            await _repositoryInstructions.ResolveAsync(relativePath, cancellationToken)
          );
        }
        return null;
      }
      catch (LocalActionException exception)
      {
        return exception;
      }
      catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException
      )
      {
        return new LocalActionException(
          "repository-instructions",
          $"Repository instructions could not be resolved for the proposed batch: {exception.Message}",
          exception
        );
      }
    }

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
      var instructionPath = pathElement.GetString();

      if (proposal.Tool is "create_file" or "create_directory")
      {
        instructionPath = (await _workspace.ResolveCreationPathAsync(
          instructionPath,
          cancellationToken
        )).RelativePath;
      }

      var instructions = await _repositoryInstructions.ResolveAsync(
        instructionPath,
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
    RepositoryInstructionSet instructions,
    ExecutionTurnToolScope toolScope
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
      + $"Validation profile: {(toolScope.ValidationProfileAvailable ? "configured" : "none")}\n"
      + $"Git branch: {project.Repository.Branch ?? "unavailable"}\n"
      + $"Pre-existing dirty paths: {dirty}\n"
      + "The trusted workspace is already the existing project root. Do not create a wrapper project directory unless the user explicitly requested a subdirectory.\n"
      + (
        project.ProjectTypes.Contains("vanilla-web", StringComparer.OrdinalIgnoreCase)
          ? "This is a vanilla web project. Work with its HTML, CSS, and browser JavaScript directly; do not invent Node, npm, a build system, or a development server.\n"
          : string.Empty
      )
      + $"Host-owned turn constraints:\n{ExecutionTurnToolPolicy.Describe(toolScope)}\n"
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
    "HOST_COMPLETION_FACTS",
    "RECOVERY_",
    "RESIDENT_",
    "AUTHORITATIVE_EXECUTION_SESSION_FACTS"
  ];

  private sealed class GenerationProgress
  {
    public StringBuilder Answer { get; } = new();

    public StringBuilder ResponseSegment { get; } = new();

    public bool ResponseSegmentActive { get; set; }

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
      SpecialistToolingProfile toolingProfile,
      ExpertExecutionGuidance? guidance = null,
      ExecutionTurnToolScope? toolScope = null,
      List<OllamaToolMessage>? toolMessages = null,
      int visibleMessages = 0,
      int omittedMessages = 0,
      bool manualCompactionRequested = false
    )
    {
      Messages = messages;
      ToolingProfile = toolingProfile;
      Guidance = guidance;
      ToolScope = toolScope ?? ExecutionTurnToolPolicy.Resolve(
        messages.Select(message => (message.Role, (string?)message.Content))
      );
      ToolMessages = toolMessages ?? messages.Select(
        ToToolMessage
      ).ToList();
      VisibleMessages = visibleMessages;
      OmittedMessages = omittedMessages;
      ManualCompactionPending = manualCompactionRequested;
    }

    public List<ChatMessage> Messages { get; }

    public List<OllamaToolMessage> ToolMessages { get; }

    public SpecialistToolingProfile ToolingProfile { get; }

    public string? ActiveToolCallId { get; set; }

    public ExpertExecutionGuidance? Guidance { get; set; }

    public ExecutionTurnToolScope ToolScope { get; }

    public HashSet<string> GrantedTools { get; } = new(
      StringComparer.OrdinalIgnoreCase
    );

    public int ToolsetRequestCount { get; set; }

    public int VisibleMessages { get; }

    public int OmittedMessages { get; }

    public bool ManualCompactionPending { get; set; }

    public long? LastUnproductiveCompactionTokens { get; set; }

    public int ContextInferenceSequence { get; set; }

    public ContextUsageView? LatestContextUsage { get; set; }

    public bool ToolingValidated { get; set; }

    public ChatStageException? Failure { get; set; }

    public Exception? PlanningFailure { get; set; }

    public int RecoveryAttemptCount { get; set; }

    public int AutomaticStrategyRevisionCount { get; set; }

    public bool RuntimeContextReported { get; set; }

    public bool ContextFailureCompactionAttempted { get; set; }

    public bool PartialContextExhausted { get; set; }

    public bool ResidentCoexistenceChecked { get; set; }
  }

  private sealed record PlanningAttempt(
    LocalActionPlanningResult? Result,
    Exception? Failure
  );

  private sealed record ToolsetGrantAttempt(
    IReadOnlyList<ToolNameResolution> Resolutions,
    LocalActionException? Failure
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

  private sealed record FunctionGemmaReviewAttempt(
    FunctionGemmaFailureReview? Review,
    Exception? Failure,
    string? SkippedReason
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
