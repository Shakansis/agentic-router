const { t } = window.AgenticRouterI18n;
const browserSessionStorageKey = "agentic-router.browser-session-id";
const benchmarkLiveRunStorageKey = "agentic-router-benchmark-live-run";
const benchmarkBatchStorageKey = "agentic-router-benchmark-batch";

const state = {
  models: [],
  devices: [],
  settings: null,
  history: [],
  requestController: null,
  autoFollow: true,
  runtimeTimer: null,
  externalAvailabilityTimer: null,
  externalAvailabilityRefresh: null,
  activeAssistant: null,
  editingTurn: null,
  conversationVersion: 0,
  modelDiagnostics: null,
  interactionMode: "chat",
  executionStrategy: "auto",
  harness: "native",
  harnesses: [],
  approvalPolicy: "auto",
  workspace: null,
  workspaceProfiles: null,
  projectProfile: null,
  knowledgeProviders: null,
  knowledgeProviderLoadError: null,
  validationProfiles: null,
  sessions: null,
  sessionsLoadError: null,
  conversationSessionId: null,
  conversationState: "completed",
  persistenceStatus: "Unsaved",
  pendingConversationAction: null,
  conversationTransitioning: false,
  browserSessionId: restoreBrowserSessionId(),
  git: null,
  activeGitView: "current-session",
  activeGitDiff: null,
  latestExecutionSessionId: null,
  latestSavedExecutionReview: null,
  settingsDirty: false,
  settingsSection: "general",
  settingsSubsection: "portable-yaml",
  workspaceSaving: false,
  activeReview: null,
  activeDelivery: null,
  pendingDeliveryAction: null,
  activeAgentModel: null,
  activeAgentRole: null,
  activeHarness: null,
  usageOverview: null,
  pricingCatalog: null,
  cloudProviders: null,
  cloudUsageDashboard: null,
  providerHealth: null,
  modelOrganization: null,
  modelCapability: null,
  compactContextNextRequest: false,
  capabilityRequestId: 0,
  webEnabled: false,
  webControlState: "unavailable",
  webSearch: null,
  attachments: [],
  messageQueue: [],
  queueEditingId: null,
  queuedDispatchMessage: null,
  messageQueuePaused: false,
  steeringMessage: false,
  cloudImageApprovals: new Set(),
  sessionSearchController: null,
  detailsSession: null,
  summarySession: null,
  summaryEstimate: null,
  contextUsage: null,
  recovery: null,
  supervisionRuns: [],
  inspectedBackup: null,
  inspectedBackupBase64: null,
  runtimeProfiles: null,
  openCloudProviders: new Set(),
  gitConfigurationEditing: false,
  benchmark: null,
  benchmarkUi: {
    tab: "results",
    selectedCell: null,
    liveCards: new Map(),
    liveOptions: new Map(),
    resultSelection: null
  },
  benchmarkBatch: restoreBenchmarkBatch(),
  activeBenchmarkRunId: null,
  benchmarkEventSource: null,
  benchmarkElapsedTimer: null,
  projectSessions: [],
  expandedProjectIds: new Set(),
  sidebarCollapsed: false,
  runtime: null,
  setup: null,
  setupTimer: null,
  setupOnboardingDismissed: false,
  pendingDiagnosticInvestigation: null,
  readOnlyConversation: false,
  composerResizeObserver: null,
  activeUserInput: null
};

let benchmarkTooltip = null;
let benchmarkTooltipTrigger = null;
let projectMenuAnchor = null;
let initializationInProgress = false;

const elements = {};
let resizeObserver;

const settingsSectionGroups = {
  general: ["settings-general", "settings-ollama"],
  "models-routing": ["settings-models", "settings-coordinator"],
  providers: ["settings-cloud-providers"],
  harnesses: ["settings-setup", "settings-runtime"],
  execution: ["settings-execution"],
  workspaces: ["settings-workspaces", "settings-git", "settings-validation"],
  advanced: ["settings-advanced"]
};

const settingsSectionAliases = {
  ollama: "general",
  cloud: "providers",
  "cloud-providers": "providers",
  models: "models-routing",
  coordinator: "models-routing",
  actions: "models-routing",
  runtime: "harnesses",
  execution: "execution",
  context: "harnesses",
  usage: "harnesses",
  workspaces: "workspaces",
  workspace: "workspaces",
  git: "workspaces",
  validation: "workspaces",
  advanced: "advanced"
};

function normalizeSettingsSection(section) {
  if (!section) {
    return "general";
  }

  const normalized = settingsSectionAliases[section] ?? section;

  return settingsSectionGroups[normalized] ? normalized : "general";
}

function sectionElementById(sectionId) {
  return document.getElementById(sectionId);
}

function visibleSettingsSectionIds(section) {
  return settingsSectionGroups[section] ?? [section];
}

document.addEventListener("DOMContentLoaded", initialize);

async function initialize() {
  window.AgenticRouterI18n.localizeDocument();
  bindElements();
  const defaultBenchmarkPrompt = t("benchmark.custom_prompt.default");
  if (defaultBenchmarkPrompt) {
    elements.benchmarkCustomPrompt.value = defaultBenchmarkPrompt;
  }
  bindEvents();
  initializeSidebarResize();
  initializeScrollFollowing();
  initializeComposerShellMetrics();

  await loadInitialApplicationState();
}

function bindElements() {
  for (const id of [
    "app-loader",
    "app-loader-detail",
    "app-loader-retry",
    "external-app-warnings",
    "external-app-warnings-summary",
    "external-app-warnings-list",
    "messages",
    "conversation-history-loader",
    "sidebar",
    "sidebar-resizer",
    "empty-state",
    "composer",
    "composer-shell",
    "message-input",
    "model-selector",
    "harness-selector",
    "send-button",
    "send-button-label",
    "send-strategy-control",
    "send-strategy-toggle",
    "send-strategy-indicator",
    "send-strategy-menu",
    "cancel-request",
    "cancel-message-edit",
    "active-agent-label",
    "active-provider-model",
    "capability-tags",
    "fallback-indicator",
    "context-usage",
    "context-usage-summary",
    "context-usage-summary-text",
    "context-usage-estimate-warning",
    "context-usage-warning",
    "context-usage-active-value",
    "context-usage-progress",
    "context-usage-progress-fill",
    "context-usage-overview-details",
    "context-usage-message-details",
    "context-usage-limit-details",
    "context-usage-advanced",
    "context-usage-advanced-label",
    "context-usage-details",
    "compact-context",
    "web-toggle",
    "web-toggle-label",
    "attach-image",
    "image-input",
    "attachment-previews",
    "message-buffer",
    "message-buffer-count",
    "message-buffer-list",
    "message-buffer-run",
    "composer-status",
    "user-input-panel",
    "provider-badge",
    "conversation-view",
    "open-benchmarks",
    "benchmark-view",
    "benchmark-form",
    "benchmark-manual-prompt-fields",
    "benchmark-run-name",
    "benchmark-custom-prompt",
    "benchmark-setup-tests",
    "benchmark-model",
    "benchmark-model-list",
    "benchmark-suite",
    "benchmark-suite-list",
    "benchmark-timeout",
    "benchmark-repetitions",
    "benchmark-context-tokens",
    "benchmark-context-presets",
    "benchmark-default-gpu",
    "benchmark-history",
    "benchmark-history-model-filter",
    "benchmark-history-harness-filter",
    "benchmark-history-suite-filter",
    "benchmark-delete-result",
    "benchmark-delete-all-results",
    "benchmark-compare-baseline",
    "benchmark-compare-candidate",
    "compare-benchmark-runs",
    "benchmark-comparison",
    "benchmark-recommendation-version",
    "benchmark-recommendation-category",
    "benchmark-recommendation-profile",
    "generate-benchmark-recommendation",
    "research-benchmark-recommendation",
    "benchmark-recommendation-status",
    "benchmark-recommendation-results",
    "benchmark-harness-list",
    "benchmark-scoring-profile-choice",
    "benchmark-score-profile",
    "benchmark-weight-objective",
    "benchmark-weight-correctness",
    "benchmark-weight-terminality",
    "benchmark-weight-workspace",
    "benchmark-weight-efficiency",
    "benchmark-weight-total",
    "reset-benchmark-weights",
    "run-benchmark",
    "cancel-benchmark",
    "benchmark-status",
    "benchmark-run-summary",
    "benchmark-score-context",
    "benchmark-ranking-note",
    "benchmark-live-dashboard",
    "benchmark-progress-title",
    "benchmark-batch-progress",
    "benchmark-current-combination",
    "benchmark-follow-active",
    "benchmark-queue-summary",
    "benchmark-combination-picker",
    "benchmark-combination-select",
    "benchmark-combination-position",
    "benchmark-previous-combination",
    "benchmark-current-combination-button",
    "benchmark-next-combination",
    "benchmark-execution-empty",
    "benchmark-models-count",
    "benchmark-harnesses-count",
    "benchmark-tests-count",
    "benchmark-selection-summary",
    "benchmark-selection-total",
    "benchmark-recommendation-preview-title",
    "benchmark-recommendation-preview-summary",
    "benchmark-view-recommendation",
    "benchmark-matrix",
    "benchmark-ranking-scope",
    "benchmark-results-body",
    "benchmark-result-detail",
    "benchmark-raw-evidence",
    "benchmark-raw-evidence-content",
    "close-benchmarks",
    "settings-dialog",
    "settings-form",
    "settings-errors",
    "settings-dirty",
    "settings-navigation",
    "settings-section-select",
    "settings-content",
    "save-settings",
    "save-status",
    "intentions-grid",
    "ollama-url",
    "router-model",
    "router-gpu",
    "action-model",
    "action-gpu",
    "coordinator-model",
    "coordinator-gpu",
    "default-model",
    "supervisor-model",
    "default-gpu",
    "jump-latest",
    "runtime-summary",
    "runtime-details",
    "runtime-compact-meters",
    "runtime-memory-list",
    "runtime-model-summary",
    "runtime-model-list",
    "cloud-usage-card",
    "cloud-usage-badge",
    "cloud-usage-summary",
    "cloud-usage-detail",
    "cloud-usage-dialog",
    "cloud-usage-dashboard-summary",
    "cloud-usage-provider-cards",
    "cloud-usage-refresh-status",
    "refresh-cloud-usage",
    "close-cloud-usage",
    "dismiss-cloud-usage",
    "new-conversation",
    "default-context-tokens",
    "provider-context-tokens",
    "reserved-response-tokens",
    "max-direct-plan-steps",
    "file-creation-output-token-limit",
    "phase-effort-plan",
    "phase-effort-work",
    "phase-effort-verify",
    "phase-effort-complete",
    "phase-effort-recovery",
    "max-tool-output-tokens",
    "generation-timeout-seconds",
    "max-conversation-messages",
    "usage-selected-window",
    "usage-pinned-windows",
    "usage-retention-days",
    "usage-provider-short-minutes",
    "usage-provider-long-minutes",
    "usage-custom-minutes",
    "usage-alert-thresholds",
    "usage-comparison-model",
    "usage-ollama-plan",
    "settings-usage-summary",
    "settings-usage-accuracy",
    "settings-usage-details",
    "purge-usage",
    "usage-purge-status",
    "reconcile-usage",
    "runtime-role-profiles",
    "runtime-override-model",
    "runtime-override-role",
    "runtime-override-minimum",
    "runtime-override-target",
    "runtime-override-maximum",
    "runtime-override-output",
    "runtime-override-keep-alive",
    "save-runtime-override",
    "remove-runtime-override",
    "runtime-memory-gpu-percent",
    "runtime-memory-free-vram",
    "runtime-memory-free-ram",
    "runtime-memory-cpu-offload",
    "runtime-memory-prefer-full-gpu",
    "runtime-memory-device-policies",
    "analyze-runtime-profile",
    "measure-runtime-profile",
    "runtime-profile-result",
    "runtime-shared-model-warnings",
    "refresh-provider-health",
    "cloud-providers-list",
    "model-filter-search",
    "model-filter-location",
    "model-filter-context",
    "model-filter-tools",
    "model-filter-web",
    "model-filter-vision",
    "model-filter-structured",
    "model-filter-conformance",
    "model-filter-available",
    "model-filter-favorites",
    "model-filter-hidden",
    "model-organization-list",
    "model-profile-selector",
    "model-profile-name",
    "model-profile-primary",
    "model-profile-fallback",
    "model-profile-router",
    "model-profile-coordinator",
    "model-profile-web",
    "model-profile-usage-window",
    "workspace-model-profile",
    "save-model-profile",
    "apply-model-profile",
    "delete-model-profile",
    "model-profile-preview",
    "model-profile-status",
    "model-chain-preview",
    "model-diagnostics-list",
    "model-context-diagnostic",
    "model-test-selector",
    "test-model",
    "model-test-result",
    "approval-policy",
    "workspace-path",
    "git-card",
    "git-badge",
    "git-summary",
    "git-upstream-summary",
    "git-initialize-quick",
    "git-commit-quick",
    "git-push-quick",
    "git-view-folder",
    "git-quick-status",
    "session-history",
    "supervision-recovery",
    "supervision-recovery-list",
    "supervision-recovery-status",
    "project-list",
    "toggle-sidebar",
    "project-menu-popover",
    "project-menu-title",
    "project-menu-count",
    "project-menu-git-row",
    "project-menu-git",
    "project-menu-path",
    "project-menu-edit",
    "conversation-persistence",
    "conversation-persistence-sidebar",
    "enable-session-history",
    "open-session-search",
    "session-search-dialog",
    "session-search-form",
    "session-search-query",
    "session-search-model",
    "session-search-file",
    "session-search-validation",
    "session-search-from",
    "session-search-to",
    "session-search-state",
    "session-search-all-workspaces",
    "session-search-status",
    "session-search-results",
    "run-session-search",
    "close-session-search",
    "cancel-session-search",
    "session-details-dialog",
    "session-details-title",
    "session-details-conversation-title",
    "session-details-metadata",
    "session-details-state",
    "session-details-summary",
    "session-details-status",
    "session-details-pin",
    "session-details-rename",
    "session-details-duplicate",
    "session-details-archive",
    "session-details-markdown",
    "session-details-json",
    "session-details-delete",
    "edit-session-summary",
    "close-session-details",
    "dismiss-session-details",
    "session-summary-dialog",
    "session-summary-form",
    "session-summary-session-title",
    "session-summary-model",
    "session-summary-estimate",
    "session-summary-objective",
    "session-summary-decisions",
    "session-summary-files",
    "session-summary-validation",
    "session-summary-unresolved",
    "session-summary-next-step",
    "session-summary-status",
    "delete-session-summary",
    "generate-session-summary",
    "close-session-summary",
    "cancel-session-summary",
    "save-session-summary",
    "workspace-dialog",
    "workspace-dialog-eyebrow",
    "workspace-dialog-title",
    "workspace-dialog-path",
    "workspace-dialog-description",
    "workspace-form",
    "close-workspace",
    "new-workspace-section",
    "workspace-submit",
    "workspace-profile-name",
    "trusted-workspace-path",
    "workspace-validation",
    "workspace-save-status",
    "clear-workspace",
    "rename-workspace",
    "pick-workspace",
    "workspace-history-enabled",
    "local-history-section",
    "history-usage",
    "delete-archived-sessions",
    "delete-all-sessions",
    "project-profile-summary",
    "project-profile-details",
    "project-profile-section",
    "refresh-project-profile",
    "knowledge-section",
    "knowledge-provider-status",
    "knowledge-provider-diagnostic",
    "knowledge-enabled",
    "knowledge-provider",
    "knowledge-base-url",
    "knowledge-api-key",
    "save-knowledge-connection",
    "prepare-knowledge-setup",
    "refresh-knowledge-provider",
    "knowledge-embedding-guidance",
    "knowledge-library-list",
    "save-project-knowledge",
    "knowledge-save-status",
    "detected-validation-profile",
    "validation-profile-section",
    "validation-profile-name",
    "validation-empty-state",
    "add-validation-step-empty",
    "use-detected-validation-empty",
    "validation-steps",
    "add-validation-step",
    "reset-validation-profile",
    "clear-validation-profile",
    "save-validation-profile",
    "validation-command-preview",
    "validation-profile-status",
    "change-review-dialog",
    "change-review-body",
    "close-change-review",
    "dismiss-change-review",
    "undo-execution",
    "validate-changes",
    "undo-status",
    "image-review-dialog",
    "image-review-title",
    "image-review-content",
    "image-review-metadata",
    "close-image-review",
    "dismiss-image-review",
    "settings-workspace-summary",
    "settings-process-permissions",
    "settings-git-summary",
    "settings-validation-summary",
    "settings-advanced-summary",
    "settings-yaml",
    "settings-yaml-file",
    "settings-yaml-status",
    "refresh-settings-yaml",
    "open-settings-yaml-file",
    "copy-settings-yaml",
    "download-settings-yaml",
    "import-settings-yaml",
    "safe-mode-banner",
    "safe-mode-reason",
    "backup-conversations",
    "backup-summaries",
    "backup-usage",
    "backup-reviews",
    "backup-restore-file",
    "create-local-backup",
    "open-local-backup",
    "restore-local-backup",
    "local-backup-status",
    "settings-open-workspace",
    "settings-open-recent",
    "settings-open-git",
    "settings-open-validation",
    "show-onboarding-before-conversation",
    "git-dialog",
    "close-git",
    "dismiss-git",
    "git-panel-status",
    "git-overview",
    "git-initialize-panel",
    "initialize-git",
    "refresh-git",
    "git-file-list",
    "git-diff-metadata",
    "git-diff-content",
    "git-user-name",
    "git-user-name-scope",
    "git-user-email",
    "git-user-email-scope",
    "git-origin-url",
    "edit-git-configuration",
    "save-git-configuration",
    "cancel-git-configuration",
    "git-remotes",
    "git-open-review",
    "git-action-status",
    "new-conversation-dialog",
    "new-conversation-enable-history",
    "new-conversation-discard",
    "new-conversation-cancel",
    "trace-diagnostic-dialog",
    "trace-diagnostic-id",
    "trace-diagnostic-status",
    "trace-diagnostic-facts",
    "trace-diagnostic-timeline",
    "close-trace-diagnostic",
    "dismiss-trace-diagnostic",
    "copy-trace-diagnostic",
    "app-modal",
    "app-modal-form",
    "app-modal-title",
    "app-modal-message",
    "app-modal-body",
    "app-modal-close",
    "app-modal-cancel",
    "app-modal-confirm",
    "toast-region"
  ]) {
    elements[toCamelCase(id)] = document.querySelector(`#${id}`);
  }
}

function bindEvents() {
  elements.externalAppWarnings.append(
    createProjectScrollRegion(elements.externalAppWarningsList)
  );
  elements.externalAppWarnings.addEventListener("toggle", () => {
    requestAnimationFrame(refreshProjectScrollIndicators);
  });
  elements.appLoaderRetry.addEventListener("click", loadInitialApplicationState);
  elements.composer.addEventListener("submit", handleComposerSubmit);
  elements.openBenchmarks.addEventListener("click", openBenchmarks);
  elements.benchmarkForm.addEventListener("submit", runBenchmarkSuite);
  elements.benchmarkForm.addEventListener("invalid", event => revealBenchmarkInvalidControl(event.target), true);
  elements.benchmarkForm.addEventListener("change", renderBenchmarkSelectionSummary);
  elements.benchmarkDefaultGpu.addEventListener("change", () => {
    state.benchmark.selectedDefaultGpu = elements.benchmarkDefaultGpu.value;
    elements.benchmarkDefaultGpu.title =
      elements.benchmarkDefaultGpu.selectedOptions[0]?.textContent ?? "";
  });
  elements.benchmarkRepetitions.addEventListener("input", renderBenchmarkSelectionSummary);
  elements.benchmarkContextTokens.addEventListener("input", renderBenchmarkSelectionSummary);
  for (const tab of elements.benchmarkView.querySelectorAll("[data-benchmark-tab]")) {
    tab.addEventListener("click", () => showBenchmarkTab(tab.dataset.benchmarkTab));
    tab.addEventListener("keydown", handleBenchmarkTabKeyDown);
  }
  elements.benchmarkViewRecommendation.addEventListener("click", () => showBenchmarkTab("recommendation", true));
  elements.benchmarkCombinationSelect.addEventListener("change", () => {
    state.benchmarkUi.selectedCell = elements.benchmarkCombinationSelect.value;
    renderBenchmarkLive();
  });
  elements.benchmarkPreviousCombination.addEventListener("click", () => stepBenchmarkCombination(-1));
  elements.benchmarkCurrentCombinationButton.addEventListener("click", followCurrentBenchmarkCombination);
  elements.benchmarkNextCombination.addEventListener("click", () => stepBenchmarkCombination(1));
  elements.benchmarkFollowActive.addEventListener("click", followCurrentBenchmarkCombination);
  elements.benchmarkSuite.addEventListener("change", updateBenchmarkSuiteSelection);
  elements.benchmarkView.addEventListener("pointerover", handleBenchmarkTooltipShow);
  elements.benchmarkView.addEventListener("pointerout", handleBenchmarkTooltipHide);
  elements.benchmarkView.addEventListener("focusin", handleBenchmarkTooltipShow);
  elements.benchmarkView.addEventListener("focusout", handleBenchmarkTooltipHide);
  elements.benchmarkView.addEventListener("scroll", hideBenchmarkTooltip, true);
  window.addEventListener("resize", hideBenchmarkTooltip);
  window.addEventListener("resize", closeProjectMenu);
  window.addEventListener("resize", refreshProjectScrollIndicators);
  elements.cancelBenchmark.addEventListener("click", cancelBenchmarkSuite);
  elements.closeBenchmarks.addEventListener("click", closeBenchmarks);
  elements.benchmarkHistory.addEventListener("change", openPersistedBenchmark);
  elements.benchmarkDeleteResult.addEventListener("click", deleteSelectedBenchmarkResult);
  elements.benchmarkDeleteAllResults.addEventListener("click", deleteAllBenchmarkResults);
  elements.benchmarkHistoryModelFilter.addEventListener("input", scheduleBenchmarkHistoryRefresh);
  elements.benchmarkHistoryHarnessFilter.addEventListener("change", refreshBenchmarkHistory);
  elements.benchmarkHistorySuiteFilter.addEventListener("change", refreshBenchmarkHistory);
  elements.compareBenchmarkRuns.addEventListener("click", compareBenchmarkRuns);
  elements.generateBenchmarkRecommendation.addEventListener("click", () =>
    generateBenchmarkRecommendation(false));
  elements.researchBenchmarkRecommendation.addEventListener("click", () =>
    generateBenchmarkRecommendation(true));
  elements.benchmarkRecommendationResults.addEventListener(
    "click",
    openBenchmarkRecommendationEvidence
  );
  for (const input of benchmarkWeightInputs()) {
    input.addEventListener("input", scheduleBenchmarkScoringUpdate);
  }
  elements.resetBenchmarkWeights.addEventListener("click", resetBenchmarkScoringProfile);
  elements.benchmarkResultsBody.addEventListener("click", openBenchmarkHarnessResult);
  elements.benchmarkView.querySelector("#benchmark-custom-ranking-body")
    .addEventListener("click", openBenchmarkHarnessResult);
  elements.benchmarkResultDetail.addEventListener("click", handleBenchmarkResultDetailClick);
  elements.benchmarkMatrix.addEventListener("click", openBenchmarkMatrixCell);
  elements.benchmarkRankingScope.addEventListener("change", () => {
    if ((state.benchmark?.result?.cells ?? []).length > 0) {
      renderBenchmarkRankings(state.benchmark.result, state.benchmark?.scoringProjection);
    } else {
      renderBenchmarkResult(state.benchmark?.result);
    }
  });
  elements.composer.addEventListener("click", handleComposerClick);
  elements.cancelMessageEdit.addEventListener("click", cancelMessageEdit);
  elements.cancelRequest.addEventListener("click", cancelActiveRequest);
  elements.sendStrategyToggle.addEventListener("click", toggleSendStrategyMenu);
  elements.sendStrategyMenu.addEventListener("click", selectSendStrategy);
  document.addEventListener("click", closeSendStrategyMenuFromOutside);
  document.addEventListener("click", collapseExecutionPlansFromOutside);
  document.addEventListener("keydown", handleSendStrategyKeyDown);
  elements.messageBufferRun.addEventListener("click", resumeMessageQueue);
  elements.messageInput.addEventListener("keydown", handleComposerKeyDown);
  elements.messageInput.addEventListener("input", resizeComposer);
  elements.messageInput.addEventListener("input", updateStreamingComposerActions);
  elements.messageInput.addEventListener("input", renderPendingContextUsage);
  elements.compactContext.addEventListener("click", requestManualContextCompaction);
  elements.contextUsageAdvanced.addEventListener(
    "toggle",
    updateContextUsageAdvancedLabel
  );
  elements.settingsForm.addEventListener("submit", saveSettings);
  elements.messages.addEventListener("scroll", handleConversationScroll);
  elements.messages.addEventListener("click", handleSetupAction);
  elements.messages.addEventListener("click", handleMessageImageReviewClick);
  elements.settingsContent.addEventListener("click", handleSetupAction);
  elements.jumpLatest.addEventListener("click", resumeAutoFollow);
  elements.newConversation.addEventListener("click", requestNewConversation);
  elements.toggleSidebar.addEventListener("click", toggleSidebar);
  elements.modelSelector.addEventListener("change", handleModelSelectionChange);
  elements.capabilityTags.addEventListener("click", handleCapabilityTagClick);
  elements.attachImage.addEventListener(
    "click",
    () => elements.imageInput.click()
  );
  elements.imageInput.addEventListener("change", handleImageSelection);
  elements.messageInput.addEventListener("paste", handleImagePaste);
  elements.composer.addEventListener("dragover", handleImageDragOver);
  elements.composer.addEventListener("dragleave", handleImageDragLeave);
  elements.composer.addEventListener("drop", handleImageDrop);
  elements.attachmentPreviews.addEventListener(
    "click",
    handleAttachmentPreviewClick
  );
  elements.harnessSelector.addEventListener("change", handleHarnessChange);
  elements.testModel.addEventListener("click", testSelectedModel);
  elements.approvalPolicy.addEventListener("change", handleApprovalPolicyChange);
  elements.workspaceForm.addEventListener("submit", saveWorkspace);
  elements.clearWorkspace.addEventListener("click", clearWorkspace);
  elements.renameWorkspace.addEventListener("click", () => {
    const active = activeWorkspaceProfile();
    if (active) {
      void renameWorkspace(active);
    }
  });
  elements.pickWorkspace.addEventListener("click", pickWorkspace);
  elements.workspaceHistoryEnabled.addEventListener(
    "change",
    changeWorkspaceHistory
  );
  elements.deleteArchivedSessions.addEventListener(
    "click",
    deleteArchivedSessions
  );
  elements.deleteAllSessions.addEventListener(
    "click",
    deleteAllSessions
  );
  elements.sessionSearchForm.addEventListener("submit", runSessionSearch);
  elements.openSessionSearch.addEventListener("click", openSessionSearch);
  elements.closeSessionSearch.addEventListener("click", closeSessionSearch);
  elements.cancelSessionSearch.addEventListener("click", closeSessionSearch);
  elements.sessionSearchDialog.addEventListener(
    "cancel",
    event => {
      event.preventDefault();
      closeSessionSearch();
    }
  );
  elements.closeSessionDetails.addEventListener("click", closeSessionDetails);
  elements.dismissSessionDetails.addEventListener("click", closeSessionDetails);
  elements.sessionDetailsDialog.addEventListener(
    "cancel",
    event => {
      event.preventDefault();
      closeSessionDetails();
    }
  );
  elements.sessionDetailsPin.addEventListener(
    "click",
    toggleSelectedSessionPin
  );
  elements.sessionDetailsRename.addEventListener(
    "click",
    renameSelectedSession
  );
  elements.sessionDetailsDuplicate.addEventListener(
    "click",
    duplicateSelectedSession
  );
  elements.sessionDetailsArchive.addEventListener(
    "click",
    archiveSelectedSession
  );
  elements.sessionDetailsDelete.addEventListener(
    "click",
    deleteSelectedSession
  );
  elements.editSessionSummary.addEventListener(
    "click",
    editSelectedSessionSummary
  );
  elements.sessionSummaryForm.addEventListener("submit", saveSessionSummary);
  elements.sessionSummaryModel.addEventListener(
    "change",
    refreshSessionSummaryEstimate
  );
  elements.generateSessionSummary.addEventListener(
    "click",
    generateSessionSummary
  );
  elements.deleteSessionSummary.addEventListener(
    "click",
    deleteSessionSummary
  );
  elements.closeSessionSummary.addEventListener("click", closeSessionSummary);
  elements.cancelSessionSummary.addEventListener("click", closeSessionSummary);
  elements.sessionSummaryDialog.addEventListener(
    "cancel",
    event => {
      event.preventDefault();
      closeSessionSummary();
    }
  );
  elements.purgeUsage.addEventListener(
    "click",
    purgeUsageHistory
  );
  elements.reconcileUsage.addEventListener(
    "click",
    reconcileUsage
  );
  elements.runtimeOverrideModel.addEventListener(
    "change",
    loadRuntimeOverrideEditor
  );
  elements.runtimeOverrideRole.addEventListener(
    "change",
    loadRuntimeOverrideEditor
  );
  elements.runtimeMemoryDevicePolicies.addEventListener(
    "change",
    handleRuntimeDevicePolicyChange
  );
  elements.saveRuntimeOverride.addEventListener(
    "click",
    saveRuntimeOverrideDraft
  );
  elements.removeRuntimeOverride.addEventListener(
    "click",
    removeRuntimeOverrideDraft
  );
  elements.analyzeRuntimeProfile.addEventListener(
    "click",
    analyzeRuntimeProfile
  );
  elements.measureRuntimeProfile.addEventListener(
    "click",
    measureRuntimeProfile
  );
  elements.refreshProviderHealth.addEventListener(
    "click",
    refreshProviderHealth
  );
  elements.cloudUsageCard.addEventListener("click", openCloudUsage);
  elements.closeCloudUsage.addEventListener("click", closeCloudUsage);
  elements.dismissCloudUsage.addEventListener("click", closeCloudUsage);
  elements.refreshCloudUsage.addEventListener("click", refreshCloudUsage);
  elements.cloudUsageDialog.addEventListener(
    "cancel",
    event => {
      event.preventDefault();
      closeCloudUsage();
    }
  );
  elements.cloudProvidersList.addEventListener(
    "click",
    handleCloudProviderAction
  );
  for (const filter of [
    elements.modelFilterSearch,
    elements.modelFilterLocation,
    elements.modelFilterContext,
    elements.modelFilterTools,
    elements.modelFilterWeb,
    elements.modelFilterVision,
    elements.modelFilterStructured,
    elements.modelFilterConformance,
    elements.modelFilterAvailable,
    elements.modelFilterFavorites,
    elements.modelFilterHidden
  ]) {
    filter.addEventListener("input", renderModelOrganization);
    filter.addEventListener("change", renderModelOrganization);
  }
  elements.modelOrganizationList.addEventListener(
    "click",
    handleModelOrganizationAction
  );
  elements.saveModelProfile.addEventListener("click", saveModelProfile);
  elements.applyModelProfile.addEventListener("click", applyModelProfile);
  elements.deleteModelProfile.addEventListener("click", deleteModelProfile);
  elements.modelProfileSelector.addEventListener(
    "change",
    loadSelectedModelProfile
  );
  elements.workspaceModelProfile.addEventListener(
    "change",
    saveWorkspaceModelProfile
  );
  elements.refreshProjectProfile.addEventListener("click", refreshProjectProfile);
  elements.refreshKnowledgeProvider.addEventListener(
    "click",
    refreshKnowledgeProvider
  );
  elements.saveKnowledgeConnection.addEventListener(
    "click",
    saveKnowledgeConnection
  );
  elements.knowledgeEnabled.addEventListener(
    "change",
    changeProjectKnowledgeEnabled
  );
  elements.saveProjectKnowledge.addEventListener(
    "click",
    saveProjectKnowledge
  );
  elements.prepareKnowledgeSetup.addEventListener(
    "click",
    prepareKnowledgeSetup
  );
  elements.addValidationStep.addEventListener(
    "click",
    () => addValidationStep()
  );
  elements.addValidationStepEmpty.addEventListener(
    "click",
    () => addValidationStep()
  );
  elements.useDetectedValidationEmpty.addEventListener(
    "click",
    resetValidationProfile
  );
  elements.resetValidationProfile.addEventListener(
    "click",
    resetValidationProfile
  );
  elements.clearValidationProfile.addEventListener(
    "click",
    clearValidationProfile
  );
  elements.saveValidationProfile.addEventListener(
    "click",
    saveValidationProfile
  );
  elements.closeChangeReview.addEventListener("click", closeChangeReview);
  elements.dismissChangeReview.addEventListener("click", closeChangeReview);
  elements.closeImageReview.addEventListener("click", closeImageReview);
  elements.dismissImageReview.addEventListener("click", closeImageReview);
  elements.imageReviewDialog.addEventListener(
    "cancel",
    event => {
      event.preventDefault();
      closeImageReview();
    }
  );
  elements.imageReviewDialog.addEventListener(
    "click",
    event => {
      if (event.target === elements.imageReviewDialog) {
        closeImageReview();
      }
    }
  );
  elements.undoExecution.addEventListener("click", undoExecution);
  elements.validateChanges.addEventListener("click", validateChanges);
  elements.gitCard.addEventListener("click", openGitPanel);
  elements.gitInitializeQuick.addEventListener("click", initializeGitRepositoryQuick);
  elements.gitCommitQuick.addEventListener("click", commitProjectChanges);
  elements.gitPushQuick.addEventListener("click", pushProjectBranch);
  elements.gitViewFolder.addEventListener("click", viewCurrentWorkspaceFolder);
  elements.projectMenuEdit.addEventListener("click", editSelectedProject);
  elements.closeGit.addEventListener("click", closeGitPanel);
  elements.dismissGit.addEventListener("click", closeGitPanel);
  elements.refreshGit.addEventListener("click", refreshGitPanel);
  elements.initializeGit.addEventListener("click", initializeGitRepository);
  elements.editGitConfiguration.addEventListener("click", beginGitConfigurationEdit);
  elements.saveGitConfiguration.addEventListener("click", saveGitConfiguration);
  elements.cancelGitConfiguration.addEventListener("click", cancelGitConfigurationEdit);
  elements.gitOpenReview.addEventListener("click", openLatestChangeReview);
  document.querySelectorAll("[data-git-view]").forEach(
    button => button.addEventListener("click", selectGitView)
  );
  elements.enableSessionHistory.addEventListener(
    "click",
    enableHistoryForCurrentWorkspace
  );
  elements.newConversationEnableHistory.addEventListener(
    "click",
    saveUnsavedConversationAndContinue
  );
  elements.newConversationDiscard.addEventListener(
    "click",
    discardUnsavedConversationAndContinue
  );
  elements.newConversationCancel.addEventListener(
    "click",
    cancelConversationTransition
  );
  elements.closeTraceDiagnostic.addEventListener("click", closeTraceDiagnostic);
  elements.dismissTraceDiagnostic.addEventListener("click", closeTraceDiagnostic);
  elements.copyTraceDiagnostic.addEventListener(
    "click",
    () => copyText(
      elements.traceDiagnosticDialog.dataset.traceId ?? "",
      elements.copyTraceDiagnostic,
      "Trace ID copied"
    )
  );
  elements.traceDiagnosticDialog.addEventListener(
    "cancel",
    event => {
      event.preventDefault();
      closeTraceDiagnostic();
    }
  );
  elements.settingsNavigation.querySelectorAll("[data-settings-target]").forEach(
    button => button.addEventListener("click", selectSettingsSection)
  );
  document.querySelectorAll("[data-settings-subtarget]").forEach(
    button => button.addEventListener("click", selectSettingsSubsection)
  );
  elements.settingsSectionSelect.addEventListener(
    "change",
    event => setSettingsSection(event.target.value, true)
  );
  elements.settingsForm.addEventListener("input", handleSettingsInput);
  elements.settingsDialog.addEventListener("cancel", handleSettingsCancel);
  elements.settingsOpenWorkspace.addEventListener("click", openWorkspaceFromSettings);
  elements.settingsOpenRecent.addEventListener("click", openRecentFromSettings);
  elements.settingsOpenGit.addEventListener("click", openGitFromSettings);
  elements.settingsOpenValidation.addEventListener("click", openValidationFromSettings);
  elements.refreshSettingsYaml.addEventListener("click", loadPortableYaml);
  elements.openSettingsYamlFile.addEventListener(
    "click",
    () => elements.settingsYamlFile.click()
  );
  elements.settingsYamlFile.addEventListener("change", loadPortableYamlFile);
  elements.copySettingsYaml.addEventListener("click", copyPortableYaml);
  elements.downloadSettingsYaml.addEventListener("click", downloadPortableYaml);
  elements.importSettingsYaml.addEventListener("click", importPortableYaml);
  elements.createLocalBackup.addEventListener("click", createLocalBackup);
  elements.openLocalBackup.addEventListener(
    "click",
    () => elements.backupRestoreFile.click()
  );
  elements.backupRestoreFile.addEventListener("change", inspectLocalBackup);
  elements.restoreLocalBackup.addEventListener("click", restoreLocalBackup);
  document.querySelectorAll(".mode-option").forEach(
    button => button.addEventListener("click", handleModeChange)
  );
  document.addEventListener("visibilitychange", handleVisibilityChange);
  window.addEventListener("focus", handleWindowFocus);
  document.addEventListener("click", handleCapabilityDocumentClick);
  document.addEventListener("click", handlePopoverDocumentClick);
  document.addEventListener("keydown", handleCapabilityKeyDown);
  document.querySelector("#open-settings").addEventListener(
    "click",
    () => openSettings()
  );
  document.querySelector("#close-settings").addEventListener("click", closeSettings);
  document.querySelector("#cancel-settings").addEventListener("click", closeSettings);
  document.querySelector("#open-workspace").addEventListener("click", openWorkspace);
  document.querySelector("#close-workspace").addEventListener("click", closeWorkspace);
  document.querySelector("#cancel-workspace").addEventListener("click", closeWorkspace);
}

function showAppModal(options) {
  const {
    title = t("modal.confirm.title"),
    message = "",
    confirmLabel = t("action.confirm"),
    cancelLabel = t("action.cancel"),
    inputLabel = "",
    inputValue = "",
    inputType = "text",
    danger = false
  } = options ?? {};

  if (!elements.appModal.hidden) {
    elements.appModalCancel.click();
  }

  elements.appModalTitle.textContent = title;
  elements.appModalMessage.textContent = message;
  elements.appModalConfirm.textContent = confirmLabel;
  elements.appModalCancel.textContent = cancelLabel;
  elements.appModalConfirm.className = danger
    ? "primary-button danger-button"
    : "primary-button";
  elements.appModalBody.querySelector(".app-modal-field")?.remove();
  let input = null;
  let field = null;
  if (inputLabel) {
    field = document.createElement("label");
    field.id = "app-modal-field";
    field.className = "app-modal-field";
    const label = document.createElement("span");
    label.id = "app-modal-label";
    label.textContent = inputLabel;
    input = document.createElement("input");
    input.id = "app-modal-input";
    input.type = inputType;
    input.autocomplete = "off";
    input.value = inputValue;
    field.append(label, input);
    elements.appModalBody.append(field);
  }
  const previousFocus = document.activeElement;

  return new Promise(resolve => {
    let settled = false;
    const finish = value => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      elements.appModal.hidden = true;
      field?.remove();
      document.body.append(elements.appModal);
      previousFocus?.focus?.();
      resolve(value);
    };
    const submit = event => {
      event.preventDefault();
      finish(input ? input.value : true);
    };
    const cancel = () => finish(input ? null : false);
    const keydown = event => {
      if (event.key === "Escape") {
        event.preventDefault();
        cancel();
      }
    };
    const cleanup = () => {
      elements.appModalForm.removeEventListener("submit", submit);
      elements.appModalClose.removeEventListener("click", cancel);
      elements.appModalCancel.removeEventListener("click", cancel);
      document.removeEventListener("keydown", keydown, true);
    };
    elements.appModalForm.addEventListener("submit", submit);
    elements.appModalClose.addEventListener("click", cancel);
    elements.appModalCancel.addEventListener("click", cancel);
    document.addEventListener("keydown", keydown, true);
    const modalHost = Array.from(
      document.querySelectorAll("dialog[open]")
    ).at(-1) ?? document.body;
    modalHost.append(elements.appModal);
    elements.appModal.hidden = false;
    (input ?? elements.appModalConfirm).focus();
  });
}

function showAppConfirm(message, options = {}) {
  return showAppModal({
    ...options,
    message
  });
}

function showAppPrompt(message, options = {}) {
  return showAppModal({
    ...options,
    message,
    inputLabel: options.inputLabel ?? t("prompt.value")
  });
}

function showToast(message, tone = "error", timeout = 30000) {
  const toast = document.createElement("article");
  toast.className = "app-toast";
  toast.dataset.tone = tone;
  toast.setAttribute("role", tone === "error" ? "alert" : "status");
  const text = document.createElement("p");
  text.textContent = message;
  const close = document.createElement("button");
  close.type = "button";
  close.setAttribute("aria-label", t("toast.close"));
  close.textContent = "×";
  let timer;
  const dismiss = () => {
    clearTimeout(timer);
    toast.remove();
  };
  close.addEventListener("click", dismiss);
  toast.append(text, close);
  elements.toastRegion.append(toast);
  timer = window.setTimeout(dismiss, timeout);
  return toast;
}

function initializeSidebarResize() {
  const minimum = 220;
  const maximum = 460;
  const storageKey = "agentic-router.sidebar-width";
  const collapsedStorageKey = "agentic-router.sidebar-collapsed";
  const clampWidth = value => Math.min(
    Math.max(minimum, value),
    Math.min(maximum, Math.max(minimum, window.innerWidth - 360))
  );
  const applyWidth = (value, persist = false) => {
    const width = clampWidth(Math.round(value));
    document.documentElement.style.setProperty(
      "--sidebar-width",
      `${width}px`
    );
    elements.sidebarResizer.setAttribute("aria-valuenow", String(width));
    if (persist) {
      try {
        localStorage.setItem(storageKey, String(width));
      } catch {
        // A blocked localStorage must not make the sidebar unusable.
      }
    }
    return width;
  };

  try {
    const stored = Number(localStorage.getItem(storageKey));
    if (Number.isFinite(stored) && stored > 0) {
      applyWidth(stored);
    }
  } catch {
    // Keep the CSS default when browser storage is unavailable.
  }

  try {
    state.sidebarCollapsed = localStorage.getItem(collapsedStorageKey) === "true";
    const expanded = JSON.parse(
      localStorage.getItem("agentic-router.expanded-projects") ?? "[]"
    );
    state.expandedProjectIds = new Set(
      Array.isArray(expanded) ? expanded.filter(id => typeof id === "string") : []
    );
  } catch {
    state.sidebarCollapsed = false;
    state.expandedProjectIds = new Set();
  }
  applySidebarCollapsedState();

  elements.sidebarResizer.addEventListener(
    "pointerdown",
    event => {
      if (event.button !== 0 || window.innerWidth <= 760) {
        return;
      }
      event.preventDefault();
      elements.sidebarResizer.setPointerCapture(event.pointerId);
      document.body.classList.add("resizing-sidebar");
    }
  );
  elements.sidebarResizer.addEventListener(
    "pointermove",
    event => {
      if (!elements.sidebarResizer.hasPointerCapture(event.pointerId)) {
        return;
      }
      applyWidth(event.clientX);
    }
  );
  const finishResize = event => {
    if (!elements.sidebarResizer.hasPointerCapture(event.pointerId)) {
      return;
    }
    elements.sidebarResizer.releasePointerCapture(event.pointerId);
    document.body.classList.remove("resizing-sidebar");
    applyWidth(elements.sidebar.getBoundingClientRect().width, true);
  };
  elements.sidebarResizer.addEventListener("pointerup", finishResize);
  elements.sidebarResizer.addEventListener("pointercancel", finishResize);
  elements.sidebarResizer.addEventListener(
    "dblclick",
    () => applyWidth(248, true)
  );
  elements.sidebarResizer.addEventListener(
    "keydown",
    event => {
      const current = elements.sidebar.getBoundingClientRect().width;
      const increment = event.shiftKey ? 24 : 10;
      let next = null;
      if (event.key === "ArrowLeft") next = current - increment;
      if (event.key === "ArrowRight") next = current + increment;
      if (event.key === "Home") next = minimum;
      if (event.key === "End") next = maximum;
      if (next === null) {
        return;
      }
      event.preventDefault();
      applyWidth(next, true);
    }
  );
}

function toggleSidebar() {
  state.sidebarCollapsed = !state.sidebarCollapsed;
  applySidebarCollapsedState();
  try {
    localStorage.setItem(
      "agentic-router.sidebar-collapsed",
      String(state.sidebarCollapsed)
    );
  } catch {
    // A blocked localStorage must not make the sidebar unusable.
  }
}

function applySidebarCollapsedState() {
  document.body.classList.toggle("sidebar-collapsed", state.sidebarCollapsed);
  elements.toggleSidebar.textContent = state.sidebarCollapsed ? "›" : "‹";
  elements.toggleSidebar.setAttribute(
    "aria-label",
    state.sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"
  );
  elements.toggleSidebar.setAttribute(
    "title",
    state.sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"
  );
  elements.toggleSidebar.setAttribute(
    "aria-expanded",
    String(!state.sidebarCollapsed)
  );
  elements.sidebarResizer.hidden = state.sidebarCollapsed;
}

function handlePopoverDocumentClick(event) {
  if (
    elements.runtimeDetails.open
    && !elements.runtimeDetails.contains(event.target)
  ) {
    elements.runtimeDetails.open = false;
  }

  if (
    !elements.projectMenuPopover.hidden
    && !elements.projectMenuPopover.contains(event.target)
    && event.target !== projectMenuAnchor
  ) {
    closeProjectMenu();
  }
}

function initializeScrollFollowing() {
  resizeObserver = new ResizeObserver(
    () => {
      if (state.autoFollow) {
        requestAnimationFrame(scrollToBottom);
      }
    }
  );
  resizeObserver.observe(elements.messages);
}

async function loadApplicationState() {
  const providerBootstrapPromise = loadProviderBootstrapState({
    reportProgress: true
  }).then(providerBootstrap => {
    const localModelCount = providerBootstrap.modelsResponse.models.filter(
      model => model.provider === "ollama-local"
    ).length;
    const ollamaVersion = providerBootstrap.setup.ollama.version;
    setApplicationLoaderStep(
      "providers",
      "complete",
      providerBootstrap.setup.ollama.available
        ? `Ollama${ollamaVersion ? ` ${ollamaVersion}` : ""} ready · ${localModelCount} local model${localModelCount === 1 ? "" : "s"}`
        : "Ollama check complete · runtime unavailable"
    );
    setApplicationLoaderStep(
      "runtime",
      "loading",
      "Loading workspace and runtime status…"
    );
    return providerBootstrap;
  });
  const [
    settings,
    providerBootstrap,
    workspace,
    projectProfile,
    knowledgeProviders,
    validationProfiles,
    workspaceProfiles,
    usageOverview,
    pricingCatalog,
    cloudProviders,
    cloudUsageDashboard,
    webSearch,
    modelOrganization,
    runtimeProfiles
  ] = await Promise.all([
    fetchJson("/api/settings"),
    providerBootstrapPromise,
    fetchJson("/api/workspace"),
    fetchJson("/api/workspace/project-profile"),
    fetchJson("/api/knowledge-providers").catch(error => {
      state.knowledgeProviderLoadError = error.message;
      return null;
    }),
    fetchJson("/api/workspace/validation-profile"),
    fetchJson("/api/workspaces"),
    fetchJson("/api/usage/overview"),
    fetchJson("/api/usage/pricing"),
    fetchJson("/api/cloud-providers"),
    fetchJson("/api/usage/cloud-dashboard"),
    fetchJson("/api/web-search"),
    fetchJson("/api/model-organization"),
    fetchJson("/api/runtime/profiles")
  ]);
  const {
    setup,
    modelsResponse,
    providerHealth
  } = providerBootstrap;
  const devicesResponse = {
    devices: setup.devices,
    diagnostic: setup.deviceDiagnostic
  };

  state.settings = settings;
  state.harnesses = setup.harnesses.map(harness => ({
    definition: harness.definition,
    availability: harness.availability
  }));
  state.models = modelsResponse.models;
  state.devices = devicesResponse.devices;
  state.workspace = workspace;
  state.projectProfile = projectProfile;
  state.knowledgeProviders = knowledgeProviders;
  state.validationProfiles = validationProfiles;
  state.workspaceProfiles = workspaceProfiles;
  state.usageOverview = usageOverview;
  state.pricingCatalog = pricingCatalog;
  state.cloudProviders = cloudProviders;
  state.cloudUsageDashboard = cloudUsageDashboard;
  state.webSearch = webSearch;
  state.providerHealth = providerHealth;
  state.modelOrganization = modelOrganization;
  state.runtimeProfiles = runtimeProfiles;
  state.setup = setup;
  updateProviderStatus(setup.ollama);
  updateDeviceStatus(devicesResponse);
  renderHarnesses();
  renderComposerModels();
  renderSettings();
  renderCloudUsage();
  renderProviderHealth();
  renderWorkspace();
  renderProjectProfile();
  renderKnowledgeSettings();
  renderValidationProfile();
  updateInteractionControls();
  await refreshSelectedModelCapabilities();
  renderPendingContextUsage();
  renderSetupOnboarding();
  await refreshSupervisionRuns();
  if (!state.recovery?.historyAutoLoadDisabled) {
    await refreshSessions();
  }
  await refreshGit();
}
