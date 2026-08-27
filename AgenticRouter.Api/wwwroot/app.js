const { t } = window.AgenticRouterI18n;

const state = {
  models: [],
  devices: [],
  settings: null,
  history: [],
  requestController: null,
  autoFollow: true,
  runtimeTimer: null,
  activeAssistant: null,
  editingTurn: null,
  conversationVersion: 0,
  modelDiagnostics: null,
  interactionMode: "chat",
  harness: "native",
  harnesses: [],
  approvalPolicy: "auto",
  workspace: null,
  workspaceProfiles: null,
  projectProfile: null,
  validationProfiles: null,
  sessions: null,
  conversationSessionId: null,
  conversationState: "completed",
  persistenceStatus: "Unsaved",
  pendingConversationAction: null,
  conversationTransitioning: false,
  browserSessionId: createSessionId(),
  git: null,
  activeGitView: "current-session",
  activeGitDiff: null,
  latestExecutionSessionId: null,
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
  activeBenchmarkRunId: null,
  benchmarkEventSource: null,
  benchmarkElapsedTimer: null,
  projectSessions: [],
  expandedProjectIds: new Set(),
  sidebarCollapsed: false,
  runtime: null,
  setup: null,
  setupTimer: null
};

let benchmarkTooltip = null;
let benchmarkTooltipTrigger = null;
let projectMenuAnchor = null;

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
  bindEvents();
  initializeSidebarResize();
  initializeScrollFollowing();

  try {
    state.recovery = await fetchJson("/api/recovery/status");
    renderRecoveryState();
    await loadApplicationState();
  } catch (error) {
    elements.providerBadge.textContent = "Error";
    elements.providerBadge.className = "badge error";
    elements.runtimeCompactMeters.textContent = error.message;
  }

  await refreshRuntimeStatus();
  if (!state.recovery?.safeMode) {
    await ensureConversationIdentity();
  }
  scheduleRuntimeRefresh();
  elements.messageInput.focus();
}

function bindElements() {
  for (const id of [
    "messages",
    "sidebar",
    "sidebar-resizer",
    "empty-state",
    "composer",
    "message-input",
    "model-selector",
    "harness-selector",
    "send-button",
    "send-button-label",
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
    "provider-badge",
    "conversation-view",
    "open-benchmarks",
    "benchmark-view",
    "benchmark-form",
    "benchmark-model",
    "benchmark-model-list",
    "benchmark-suite",
    "benchmark-suite-list",
    "benchmark-timeout",
    "benchmark-history",
    "benchmark-history-model-filter",
    "benchmark-history-harness-filter",
    "benchmark-history-suite-filter",
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
    "resume-session-details",
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
    "workspace-form",
    "workspace-profile-list",
    "saved-workspaces-section",
    "add-workspace",
    "new-workspace-section",
    "new-workspace-accordion",
    "cancel-new-workspace",
    "workspace-submit",
    "workspace-profile-name",
    "trusted-workspace-path",
    "workspace-validation",
    "workspace-save-status",
    "clear-workspace",
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
    "detected-validation-profile",
    "validation-profile-section",
    "validation-profile-name",
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
    "settings-workspace-summary",
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
  elements.composer.addEventListener("submit", handleComposerSubmit);
  elements.openBenchmarks.addEventListener("click", openBenchmarks);
  elements.benchmarkForm.addEventListener("submit", runBenchmarkSuite);
  elements.benchmarkSuite.addEventListener("change", updateBenchmarkSuiteSelection);
  elements.benchmarkView.addEventListener("pointerover", handleBenchmarkTooltipShow);
  elements.benchmarkView.addEventListener("pointerout", handleBenchmarkTooltipHide);
  elements.benchmarkView.addEventListener("focusin", handleBenchmarkTooltipShow);
  elements.benchmarkView.addEventListener("focusout", handleBenchmarkTooltipHide);
  elements.benchmarkView.addEventListener("scroll", hideBenchmarkTooltip, true);
  window.addEventListener("resize", hideBenchmarkTooltip);
  window.addEventListener("resize", closeProjectMenu);
  elements.cancelBenchmark.addEventListener("click", cancelBenchmarkSuite);
  elements.closeBenchmarks.addEventListener("click", closeBenchmarks);
  elements.benchmarkHistory.addEventListener("change", openPersistedBenchmark);
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
  elements.messageBufferRun.addEventListener("click", resumeMessageQueue);
  elements.messageInput.addEventListener("keydown", handleComposerKeyDown);
  elements.messageInput.addEventListener("input", resizeComposer);
  elements.messageInput.addEventListener("input", updateStreamingComposerActions);
  elements.messageInput.addEventListener("input", renderPendingContextUsage);
  elements.compactContext.addEventListener("click", requestManualContextCompaction);
  elements.settingsForm.addEventListener("submit", saveSettings);
  elements.messages.addEventListener("scroll", handleConversationScroll);
  elements.messages.addEventListener("click", handleSetupAction);
  elements.settingsContent.addEventListener("click", handleSetupAction);
  elements.jumpLatest.addEventListener("click", resumeAutoFollow);
  elements.newConversation.addEventListener("click", requestNewConversation);
  elements.toggleSidebar.addEventListener("click", toggleSidebar);
  elements.modelSelector.addEventListener("change", handleModelSelectionChange);
  elements.capabilityTags.addEventListener("click", handleCapabilityTagClick);
  elements.webToggle.addEventListener("click", toggleWebSearch);
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
  elements.addWorkspace.addEventListener("click", showNewWorkspaceForm);
  elements.cancelNewWorkspace.addEventListener("click", hideNewWorkspaceForm);
  elements.clearWorkspace.addEventListener("click", clearWorkspace);
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
  elements.resumeSessionDetails.addEventListener(
    "click",
    resumeSelectedSession
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
  elements.addValidationStep.addEventListener(
    "click",
    () => addValidationStep()
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
  const [
    settings,
    setup,
    modelsResponse,
    workspace,
    projectProfile,
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
    fetchJson("/api/setup/status"),
    fetchJson("/api/models"),
    fetchJson("/api/workspace"),
    fetchJson("/api/workspace/project-profile"),
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
  const providerHealth = await fetchJson("/api/provider-health");
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
  updateProviderStatus(modelsResponse);
  updateDeviceStatus(devicesResponse);
  renderHarnesses();
  renderComposerModels();
  renderSettings();
  renderCloudUsage();
  renderProviderHealth();
  renderWorkspace();
  renderWorkspaceProfiles();
  renderProjectProfile();
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

async function openBenchmarks() {
  elements.runtimeDetails.open = false;
  elements.conversationView.hidden = true;
  elements.benchmarkView.hidden = false;
  elements.closeBenchmarks.focus();
  elements.benchmarkStatus.textContent = "Loading catalog and history…";

  try {
    const [
      catalog,
      history,
      modelsResponse,
      scoringProfile,
      recommendationCatalog
    ] = await Promise.all([
      fetchJson("/api/benchmarks/catalog"),
      fetchJson("/api/benchmarks/history?limit=100"),
      fetchJson("/api/models"),
      fetchJson("/api/benchmarks/scoring-profile"),
      fetchJson("/api/benchmarks/recommendation-catalog")
    ]);
    state.models = modelsResponse.models;
    const retainedLive = state.benchmark?.live?.terminal
      ? null
      : state.benchmark?.live ?? null;
    const initialResult = retainedLive
      ? state.benchmark?.result ?? null
      : history[0]
        ? await fetchJson(`/api/benchmarks/suite-runs/${encodeURIComponent(history[0].runId)}`)
        : null;
    state.benchmark = {
      catalog,
      history,
      result: initialResult,
      scoringProfile,
      scoringProjection: null,
      scoringUpdateTimer: null,
      historyUpdateTimer: null,
      comparison: null,
      recommendationCatalog,
      recommendation: null,
      live: retainedLive
    };
    renderBenchmarkControls();
    renderBenchmarkHistory();
    renderBenchmarkRecommendationControls();
    const storedRunId = sessionStorage.getItem("agentic-router-benchmark-live-run");
    if (retainedLive && state.activeBenchmarkRunId) {
      renderBenchmarkLive();
      setBenchmarkRunning(true);
      connectBenchmarkEvents(state.activeBenchmarkRunId, retainedLive.lastSequence);
    } else if (storedRunId) {
      await resumeLiveBenchmark(storedRunId);
    } else {
      await rescoreBenchmarkResult();
      await generateGeneralBenchmarkRecommendation();
      if (!state.benchmark?.live?.terminal) {
        elements.benchmarkStatus.textContent = "Ready.";
      }
    }
  } catch (error) {
    elements.benchmarkStatus.textContent = error.message;
  }
}

function closeBenchmarks() {
  hideBenchmarkTooltip();
  elements.benchmarkView.hidden = true;
  elements.conversationView.hidden = false;
  elements.openBenchmarks.focus();
}

function handleBenchmarkTooltipShow(event) {
  const trigger = event.target.closest(".information-button[data-tooltip]");
  if (!trigger || !elements.benchmarkView.contains(trigger)) {
    return;
  }
  if (event.relatedTarget && trigger.contains(event.relatedTarget)) {
    return;
  }
  showBenchmarkTooltip(trigger);
}

function handleBenchmarkTooltipHide(event) {
  const trigger = event.target.closest(".information-button[data-tooltip]");
  if (!trigger || trigger !== benchmarkTooltipTrigger) {
    return;
  }
  if (event.relatedTarget && trigger.contains(event.relatedTarget)) {
    return;
  }
  hideBenchmarkTooltip();
}

function showBenchmarkTooltip(trigger) {
  hideBenchmarkTooltip();
  const tooltip = document.createElement("div");
  tooltip.id = "benchmark-floating-tooltip";
  tooltip.className = "benchmark-floating-tooltip";
  tooltip.role = "tooltip";
  tooltip.textContent = trigger.dataset.tooltip;
  tooltip.style.visibility = "hidden";
  tooltip.style.maxWidth = `${Math.max(180, Math.min(320, window.innerWidth - 24))}px`;
  document.body.append(tooltip);

  const triggerRect = trigger.getBoundingClientRect();
  const tooltipRect = tooltip.getBoundingClientRect();
  const margin = 12;
  const left = Math.min(
    Math.max(margin, triggerRect.left),
    Math.max(margin, window.innerWidth - tooltipRect.width - margin)
  );
  let top = triggerRect.bottom + 8;
  if (top + tooltipRect.height > window.innerHeight - margin) {
    top = triggerRect.top - tooltipRect.height - 8;
  }
  top = Math.max(margin, top);
  tooltip.style.left = `${left}px`;
  tooltip.style.top = `${top}px`;
  tooltip.style.visibility = "visible";
  trigger.setAttribute("aria-describedby", tooltip.id);
  benchmarkTooltip = tooltip;
  benchmarkTooltipTrigger = trigger;
}

function hideBenchmarkTooltip() {
  benchmarkTooltipTrigger?.removeAttribute("aria-describedby");
  benchmarkTooltip?.remove();
  benchmarkTooltip = null;
  benchmarkTooltipTrigger = null;
}

function renderBenchmarkControls() {
  const localModels = modelOptions().filter(option =>
    option.provider === "ollama-local" && !option.disabled
  );
  const selectedModel = localModels.some(option => option.value === state.settings?.defaultModel)
    ? state.settings.defaultModel
    : localModels[0]?.value;
  elements.benchmarkModel.replaceChildren();
  elements.benchmarkModelList.replaceChildren();
  for (const model of localModels) {
    const option = document.createElement("option");
    option.value = model.value;
    option.textContent = model.label;
    option.selected = model.value === selectedModel;
    elements.benchmarkModel.append(option);

    const label = document.createElement("label");
    label.className = "benchmark-switch benchmark-model-option";
    const identity = document.createElement("span");
    identity.className = "benchmark-switch-identity";
    const name = document.createElement("strong");
    name.textContent = model.label;
    const detail = document.createElement("small");
    detail.textContent = model.label === model.value
      ? "Ollama Local"
      : model.value;
    identity.append(name, detail);
    const input = document.createElement("input");
    input.type = "checkbox";
    input.name = "benchmark-model-toggle";
    input.value = model.value;
    input.checked = model.value === selectedModel;
    input.setAttribute("role", "switch");
    input.setAttribute("aria-label", `Use model ${model.label}`);
    input.addEventListener("change", syncBenchmarkModelSelection);
    label.append(identity, input);
    elements.benchmarkModelList.append(label);
  }

  const catalog = state.benchmark?.catalog;
  const suites = catalog?.suites ?? (catalog?.suite ? [catalog.suite] : []);
  const persistedSelections = state.benchmark?.result?.selectedSuites
    ?? (state.benchmark?.result && state.benchmark.result.suiteId !== "combined"
      ? [{ id: state.benchmark.result.suiteId, version: state.benchmark.result.suiteVersion }]
      : []);
  elements.benchmarkSuiteList.replaceChildren();
  for (const suite of suites) {
    const label = document.createElement("label");
    label.className = "benchmark-switch benchmark-test-group-option";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.name = "benchmark-suite";
    input.value = suite.id;
    input.dataset.version = String(suite.version);
    input.setAttribute("role", "switch");
    input.checked = persistedSelections.length > 0
      ? persistedSelections.some(item => item.id === suite.id && item.version === suite.version)
      : true;
    const text = document.createElement("span");
    text.className = "benchmark-switch-identity";
    const name = document.createElement("strong");
    name.textContent = suite.id === "basic-crud" ? "CRUD" : "Agent Behavior";
    input.setAttribute("aria-label", `Run tests ${name.textContent}`);
    const detail = document.createElement("small");
    detail.textContent = `${suite.tests.length} tests`;
    text.title = `${suite.name}; internal version ${suite.version}.`;
    text.append(name, detail);
    label.append(text, input);
    elements.benchmarkSuiteList.append(label);
  }
  elements.benchmarkTimeout.value = String(catalog?.defaultTimeoutSeconds ?? 120);
  elements.benchmarkTimeout.min = String(catalog?.minimumTimeoutSeconds ?? 5);
  elements.benchmarkTimeout.max = String(catalog?.maximumTimeoutSeconds ?? 600);
  elements.benchmarkHarnessList.replaceChildren();
  for (const status of catalog?.harnesses ?? []) {
    const label = document.createElement("label");
    label.className = "benchmark-switch benchmark-harness-option";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.name = "benchmark-harness";
    input.value = status.definition.id;
    input.disabled = !status.availability.available;
    input.dataset.available = String(status.availability.available);
    input.checked = status.availability.available;
    input.setAttribute("role", "switch");
    const text = document.createElement("span");
    text.className = "benchmark-switch-identity benchmark-harness-identity";
    const harnessLabel = harnessDisplayLabel(status.definition);
    const name = document.createElement("strong");
    name.textContent = harnessLabel;
    input.setAttribute("aria-label", `Use harness ${harnessLabel}`);
    const version = document.createElement("small");
    version.textContent = status.availability.available
      ? status.availability.version ?? "Version not reported"
      : "Unavailable";
    text.append(name, version);
    if (!status.availability.available && status.availability.message) {
      label.title = status.availability.message;
    }
    label.append(text, input);
    elements.benchmarkHarnessList.append(label);
  }
  elements.benchmarkScoringProfileChoice.value = state.benchmark?.scoringProfile?.id === "custom"
    ? "custom"
    : "default";
  replaceOptions(
    elements.benchmarkHistoryHarnessFilter,
    [
      { value: "", label: "All harnesses" },
      ...(catalog?.harnesses ?? []).map(status => ({
        value: status.definition.id,
        label: harnessDisplayLabel(status.definition)
      }))
    ],
    elements.benchmarkHistoryHarnessFilter.value
  );
  replaceOptions(
    elements.benchmarkHistorySuiteFilter,
    [
      { value: "", label: "All suites" },
      ...suites.map(suite => ({
        value: suite.id,
        label: suite.id === "basic-crud" ? "CRUD" : "Agent Behavior"
      }))
    ],
    elements.benchmarkHistorySuiteFilter.value
  );
  renderBenchmarkScoringProfile();
  updateBenchmarkSuiteSelection();
}

function selectedBenchmarkModels() {
  return [...elements.benchmarkModelList.querySelectorAll(
    'input[name="benchmark-model-toggle"]:checked:not(:disabled)'
  )].map(input => input.value);
}

function syncBenchmarkModelSelection() {
  const selected = new Set(selectedBenchmarkModels());
  for (const option of elements.benchmarkModel.options) {
    option.selected = selected.has(option.value);
  }
}

function selectedBenchmarkSuites() {
  const selected = [...elements.benchmarkSuiteList.querySelectorAll(
    'input[name="benchmark-suite"]:checked'
  )].map(input => ({ id: input.value, version: Number(input.dataset.version) }));
  const suites = state.benchmark?.catalog?.suites ?? [];
  return selected.map(selection => suites.find(suite =>
    suite.id === selection.id && suite.version === selection.version
  )).filter(Boolean);
}

function updateBenchmarkSuiteSelection() {
  const suites = selectedBenchmarkSuites();
  if (suites.length === 0) {
    elements.runBenchmark.textContent = "Select tests";
    return;
  }
  const scenarioTimeout = Math.max(
    ...suites.flatMap(suite => suite.tests)
      .map(test => Number(test.timeoutSeconds || 120))
  );
  elements.benchmarkTimeout.value = String(Math.min(
    Number(elements.benchmarkTimeout.max || 600),
    scenarioTimeout
  ));
  elements.runBenchmark.textContent = "Run benchmark";
}

function benchmarkWeightInputs() {
  return [
    elements.benchmarkWeightObjective,
    elements.benchmarkWeightCorrectness,
    elements.benchmarkWeightTerminality,
    elements.benchmarkWeightWorkspace,
    elements.benchmarkWeightEfficiency
  ];
}

function benchmarkWeightsFromInputs() {
  return {
    objectiveSuccess: Number(elements.benchmarkWeightObjective.value),
    correctness: Number(elements.benchmarkWeightCorrectness.value),
    terminality: Number(elements.benchmarkWeightTerminality.value),
    workspaceAccuracy: Number(elements.benchmarkWeightWorkspace.value),
    efficiency: Number(elements.benchmarkWeightEfficiency.value)
  };
}

function renderBenchmarkScoringProfile() {
  const profile = state.benchmark?.scoringProfile;
  if (!profile) {
    return;
  }
  const weights = profile.weights;
  elements.benchmarkScoreProfile.textContent = `${profile.displayName} v${profile.version}`;
  elements.benchmarkWeightObjective.value = String(weights.objectiveSuccess);
  elements.benchmarkWeightCorrectness.value = String(weights.correctness);
  elements.benchmarkWeightTerminality.value = String(weights.terminality);
  elements.benchmarkWeightWorkspace.value = String(weights.workspaceAccuracy);
  elements.benchmarkWeightEfficiency.value = String(weights.efficiency);
  renderBenchmarkWeightTotal(weights);
}

function renderBenchmarkWeightTotal(weights) {
  const total = Number(weights.objectiveSuccess)
    + Number(weights.correctness)
    + Number(weights.terminality)
    + Number(weights.workspaceAccuracy)
    + Number(weights.efficiency);
  elements.benchmarkWeightTotal.textContent = total <= 0
    ? "Total 0 · invalid configuration"
    : total === 100
      ? "Total 100 · no normalization"
      : `Total ${total} · normalized to 100%`;
  elements.benchmarkWeightTotal.classList.toggle("error", total <= 0);
}

function scheduleBenchmarkScoringUpdate() {
  const weights = benchmarkWeightsFromInputs();
  renderBenchmarkWeightTotal(weights);
  clearTimeout(state.benchmark?.scoringUpdateTimer);
  if (!state.benchmark) {
    return;
  }
  state.benchmark.scoringUpdateTimer = setTimeout(saveBenchmarkScoringProfile, 150);
}

async function saveBenchmarkScoringProfile() {
  const weights = benchmarkWeightsFromInputs();
  try {
    const profile = await fetchJson("/api/benchmarks/scoring-profile", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(weights)
    });
    state.benchmark.scoringProfile = profile;
    if (state.benchmark.recommendationCatalog) {
      state.benchmark.recommendationCatalog.activeScoringProfile = profile;
    }
    state.benchmark.scoringUpdateTimer = null;
    renderBenchmarkScoringProfile();
    renderBenchmarkRecommendationControls();
    await rescoreBenchmarkResult();
    await refreshBenchmarkHistory();
    if (state.benchmark.comparison) {
      await compareBenchmarkRuns();
    }
    elements.benchmarkStatus.textContent = "Custom profile saved; ranking recalculated without running the benchmark.";
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function resetBenchmarkScoringProfile() {
  clearTimeout(state.benchmark?.scoringUpdateTimer);
  try {
    const profile = await fetchJson("/api/benchmarks/scoring-profile/reset", {
      method: "POST"
    });
    state.benchmark.scoringProfile = profile;
    if (state.benchmark.recommendationCatalog) {
      state.benchmark.recommendationCatalog.activeScoringProfile = profile;
    }
    renderBenchmarkScoringProfile();
    renderBenchmarkRecommendationControls();
    await rescoreBenchmarkResult();
    await refreshBenchmarkHistory();
    if (state.benchmark.comparison) {
      await compareBenchmarkRuns();
    }
    elements.benchmarkStatus.textContent = "Default profile restored; original ranking recalculated.";
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function rescoreBenchmarkResult() {
  const result = state.benchmark?.result;
  if (!result) {
    if (state.benchmark) {
      state.benchmark.scoringProjection = null;
    }
    renderBenchmarkResult(null);
    return;
  }
  state.benchmark.scoringProjection = await fetchJson(
    `/api/benchmarks/suite-runs/${encodeURIComponent(result.runId)}/rescore`,
    { method: "POST" }
  );
  renderBenchmarkResult(result);
}

function renderBenchmarkHistory() {
  const options = [
    { value: "", label: "Select a persisted result" },
    ...(state.benchmark?.history ?? []).map(result => ({
      value: result.runId,
      label: benchmarkHistoryRunLabel(result)
    }))
  ];
  replaceOptions(
    elements.benchmarkHistory,
    options,
    state.benchmark?.result?.runId ?? ""
  );
  const compareOptions = [
    { value: "", label: "Select a run" },
    ...(state.benchmark?.history ?? []).map(result => ({
      value: result.runId,
      label: benchmarkHistoryRunLabel(result)
    }))
  ];
  const previousBaseline = elements.benchmarkCompareBaseline.value;
  const previousCandidate = elements.benchmarkCompareCandidate.value;
  const history = state.benchmark?.history ?? [];
  replaceOptions(
    elements.benchmarkCompareBaseline,
    compareOptions,
    previousBaseline || history[1]?.runId || ""
  );
  replaceOptions(
    elements.benchmarkCompareCandidate,
    compareOptions,
    previousCandidate || history[0]?.runId || ""
  );
  elements.compareBenchmarkRuns.disabled = history.length < 2;
}

function benchmarkHistoryRunLabel(result) {
  const timestamp = new Date(result.startedAt).toLocaleString(window.AgenticRouterI18n.locale, {
    dateStyle: "short",
    timeStyle: "short"
  });
  return `${timestamp} · ${benchmarkSuiteLabel(result.suiteId)} · `
    + `${result.models.length}M × ${result.harnesses.length}H · ${result.finalStatus}`;
}

function benchmarkSuiteLabel(suiteId) {
  if (suiteId === "basic-crud") {
    return "CRUD";
  }
  if (suiteId === "agent-behavior") {
    return "Agent Behavior";
  }
  if (suiteId === "combined") {
    return "CRUD + Agent Behavior";
  }
  return suiteId || "Tests";
}

function scheduleBenchmarkHistoryRefresh() {
  if (!state.benchmark) {
    return;
  }
  clearTimeout(state.benchmark.historyUpdateTimer);
  state.benchmark.historyUpdateTimer = setTimeout(refreshBenchmarkHistory, 180);
}

async function refreshBenchmarkHistory(options = {}) {
  if (!state.benchmark) {
    return;
  }
  clearTimeout(state.benchmark.historyUpdateTimer);
  state.benchmark.historyUpdateTimer = null;
  const query = new URLSearchParams({ limit: "100" });
  const model = elements.benchmarkHistoryModelFilter.value.trim();
  const harness = elements.benchmarkHistoryHarnessFilter.value;
  const suite = elements.benchmarkHistorySuiteFilter.value;
  if (model) {
    query.set("model", model);
  }
  if (harness) {
    query.set("harness", harness);
  }
  if (suite) {
    query.set("suite", suite);
  }
  try {
    state.benchmark.history = await fetchJson(`/api/benchmarks/history?${query}`);
    renderBenchmarkHistory();
    if (options.selectRunId) {
      elements.benchmarkHistory.value = options.selectRunId;
    }
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function compareBenchmarkRuns() {
  const baselineRunId = elements.benchmarkCompareBaseline.value;
  const candidateRunId = elements.benchmarkCompareCandidate.value;
  if (!baselineRunId || !candidateRunId) {
    elements.benchmarkStatus.textContent = "Select two historical runs.";
    return;
  }
  if (baselineRunId === candidateRunId) {
    elements.benchmarkStatus.textContent = "Select different runs to compare.";
    return;
  }
  try {
    const query = new URLSearchParams({ baselineRunId, candidateRunId });
    const comparison = await fetchJson(`/api/benchmarks/comparisons?${query}`);
    state.benchmark.comparison = comparison;
    renderBenchmarkComparison(comparison);
    elements.benchmarkStatus.textContent = "Historical comparison calculated without changing the original evidence.";
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function renderBenchmarkComparison(comparison) {
  elements.benchmarkComparison.hidden = !comparison;
  elements.benchmarkComparison.replaceChildren();
  if (!comparison) {
    return;
  }
  const heading = document.createElement("div");
  heading.className = "benchmark-comparison-heading";
  const title = document.createElement("strong");
  title.textContent = benchmarkComparabilityLabel(comparison.comparability);
  const score = document.createElement("span");
  score.textContent = `Original ${Number(comparison.baseline.originalScore).toFixed(2)} → ${Number(comparison.candidate.originalScore).toFixed(2)} · `
    + `Current-profile ${Number(comparison.baseline.currentProfileScore).toFixed(2)} → ${Number(comparison.candidate.currentProfileScore).toFixed(2)}`;
  heading.append(title, score);
  elements.benchmarkComparison.append(heading);

  if (comparison.reasons.length > 0) {
    const reasons = document.createElement("ul");
    reasons.className = "benchmark-comparison-reasons";
    for (const reason of comparison.reasons) {
      const item = document.createElement("li");
      item.textContent = reason;
      reasons.append(item);
    }
    elements.benchmarkComparison.append(reasons);
  }

  const deltas = document.createElement("dl");
  deltas.className = "benchmark-comparison-deltas";
  for (const delta of comparison.deltas) {
    const term = document.createElement("dt");
    term.textContent = delta.metric;
    const value = document.createElement("dd");
    const numericDelta = Number(delta.delta);
    value.textContent = `${Number(delta.baseline).toFixed(2)} → ${Number(delta.candidate).toFixed(2)} `
      + `(${numericDelta >= 0 ? "+" : ""}${numericDelta.toFixed(2)} ${delta.unit ?? ""})`;
    deltas.append(term, value);
  }
  elements.benchmarkComparison.append(deltas);

  if (comparison.comparability !== "comparable") {
    const warning = document.createElement("p");
    warning.className = "benchmark-comparison-warning";
    warning.textContent = "Numeric deltas are evidence only; no regression or improvement was classified because the conditions are not directly comparable.";
    elements.benchmarkComparison.append(warning);
  } else if (comparison.signals.length > 0) {
    const signals = document.createElement("ul");
    signals.className = "benchmark-comparison-signals";
    for (const signal of comparison.signals) {
      const item = document.createElement("li");
      item.dataset.direction = signal.direction;
      const scope = [signal.model, signal.harness, signal.testId].filter(Boolean).join(" × ");
      item.textContent = `${signal.direction}: ${signal.message}${scope ? ` · ${scope}` : ""}`;
      signals.append(item);
    }
    elements.benchmarkComparison.append(signals);
  }

  if (comparison.changedMetadata.length > 0) {
    const metadata = document.createElement("details");
    const summary = document.createElement("summary");
    summary.textContent = `Changed metadata (${comparison.changedMetadata.length})`;
    const list = document.createElement("dl");
    list.className = "benchmark-comparison-metadata";
    for (const change of comparison.changedMetadata) {
      const term = document.createElement("dt");
      term.textContent = change.field;
      const value = document.createElement("dd");
      value.textContent = `${change.baseline} → ${change.candidate}`;
      list.append(term, value);
    }
    metadata.append(summary, list);
    elements.benchmarkComparison.append(metadata);
  }
}

function benchmarkComparabilityLabel(value) {
  return {
    comparable: "Comparable",
    "partially-comparable": "Partially comparable",
    "not-directly-comparable": "Not directly comparable"
  }[value] ?? value;
}

function benchmarkAggregateOriginalScore(result) {
  const scores = (result.cells?.length ? result.cells : result.harnessResults ?? [])
    .map(item => Number(item.score));
  return scores.length
    ? scores.reduce((total, value) => total + value, 0) / scores.length
    : 0;
}

function benchmarkAggregateCurrentScore(projection, result) {
  const scores = projection?.matrixCellScores?.length
    ? projection.matrixCellScores.map(item => Number(item.score))
    : projection?.harnessScores?.length
      ? projection.harnessScores.map(item => Number(item.score))
      : [];
  return scores.length
    ? scores.reduce((total, value) => total + value, 0) / scores.length
    : benchmarkAggregateOriginalScore(result);
}

function renderBenchmarkRecommendationControls() {
  const catalog = state.benchmark?.recommendationCatalog;
  if (!catalog) {
    return;
  }
  elements.benchmarkRecommendationVersion.textContent = catalog.algorithmVersion;
  replaceOptions(
    elements.benchmarkRecommendationCategory,
    catalog.categories.map(category => ({
      value: category.id,
      label: category.name
    })),
    elements.benchmarkRecommendationCategory.value || catalog.categories[0]?.id
  );
  const active = catalog.activeScoringProfile;
  replaceOptions(
    elements.benchmarkRecommendationProfile,
    [
      {
        value: "active",
        label: `Active profile · ${active.displayName} v${active.version}`
      },
      { value: "default", label: "Default v1" }
    ],
    elements.benchmarkRecommendationProfile.value || "active"
  );
  elements.researchBenchmarkRecommendation.disabled = !catalog.externalResearchAvailable;
  elements.researchBenchmarkRecommendation.title = catalog.externalResearchAvailable
    ? "Request explicit external research separate from local evidence."
    : "Configure Ollama Web Search to enable optional external research.";
}

async function generateBenchmarkRecommendation(includeExternalEvidence) {
  if (!state.benchmark || state.activeBenchmarkRunId) {
    return;
  }
  elements.generateBenchmarkRecommendation.disabled = true;
  elements.researchBenchmarkRecommendation.disabled = true;
  renderBenchmarkRecommendation(null);
  elements.benchmarkRecommendationStatus.textContent = includeExternalEvidence
    ? "Explicitly researching external sources; local data will not be sent."
    : "Calculating recommendation using persisted local evidence only.";
  try {
    const recommendation = await fetchJson("/api/benchmarks/recommendations", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        category: elements.benchmarkRecommendationCategory.value,
        scoringProfile: elements.benchmarkRecommendationProfile.value,
        includeExternalEvidence
      })
    });
    state.benchmark.recommendation = recommendation;
    renderBenchmarkRecommendation(recommendation);
    elements.benchmarkRecommendationStatus.textContent = recommendation.summary;
  } catch (error) {
    elements.benchmarkRecommendationStatus.textContent = benchmarkErrorMessage(error);
  } finally {
    elements.generateBenchmarkRecommendation.disabled = false;
    elements.researchBenchmarkRecommendation.disabled =
      !state.benchmark?.recommendationCatalog?.externalResearchAvailable;
  }
}

async function generateGeneralBenchmarkRecommendation() {
  if (!state.benchmark?.recommendationCatalog) {
    return;
  }
  elements.benchmarkRecommendationCategory.value = "general-coding";
  elements.benchmarkRecommendationProfile.value = "active";
  await generateBenchmarkRecommendation(false);
}

function renderBenchmarkRecommendation(recommendation) {
  const container = elements.benchmarkRecommendationResults;
  container.hidden = !recommendation;
  container.replaceChildren();
  if (!recommendation) {
    return;
  }
  const trace = document.createElement("details");
  trace.className = "benchmark-recommendation-trace";
  const traceSummary = document.createElement("summary");
  traceSummary.textContent = "Recommendation details";
  const traceBody = document.createElement("p");
  traceBody.textContent = `${recommendation.algorithmVersion} · ${recommendation.category} · `
    + `${recommendation.scoringProfile.displayName} v${recommendation.scoringProfile.version} · `
    + `ID ${recommendation.recommendationId.slice(0, 12)}`;
  trace.append(traceSummary, traceBody);
  container.append(trace);

  let alternatives = null;
  let alternativesBody = null;
  for (const candidate of recommendation.candidates) {
    const card = document.createElement("article");
    card.className = "benchmark-recommendation-card";
    const heading = document.createElement("div");
    heading.className = "benchmark-recommendation-card-heading";
    const title = document.createElement("h4");
    title.textContent = `#${candidate.rank} ${candidate.model} × ${benchmarkHarnessLabel(candidate.harness)}`;
    const label = document.createElement("span");
    label.className = "badge";
    label.textContent = candidate.recommendation;
    heading.append(title, label);
    const summary = document.createElement("p");
    summary.className = "benchmark-recommendation-card-summary";
    summary.textContent = `Score ${Number(candidate.score).toFixed(2)} · ${candidate.confidence} · `
      + `${candidate.evidenceStrength} · ${candidate.comparableHistoricalRunCount} comparable · `
      + `${candidate.partialHistoricalRunCount} partial · ${candidate.incompatibleHistoricalRunCount} incompatible`;
    card.append(heading, summary);
    card.append(
      recommendationList("Strengths", candidate.strengths, "strengths"),
      recommendationList("Limitations", candidate.weaknesses, "weaknesses")
    );
    const evidence = document.createElement("details");
    const evidenceSummary = document.createElement("summary");
    evidenceSummary.textContent = `Local evidence (${candidate.evidence.length})`;
    const links = document.createElement("div");
    links.className = "benchmark-recommendation-evidence-links";
    for (const item of candidate.evidence) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "benchmark-result-link";
      button.dataset.recommendationRunId = item.runId;
      button.textContent = `${new Date(item.startedAt).toLocaleDateString(window.AgenticRouterI18n.locale)} · ${benchmarkSuiteLabel(item.suiteId)} · `
        + `${item.source} · ${item.comparability} · ${Number(item.categoryScore).toFixed(2)}`;
      links.append(button);
    }
    evidence.append(evidenceSummary, links);
    card.append(evidence);
    if (candidate.rank === 1) {
      container.append(card);
    } else {
      if (!alternatives) {
        alternatives = document.createElement("details");
        alternatives.className = "benchmark-recommendation-alternatives";
        const alternativesSummary = document.createElement("summary");
        alternativesSummary.textContent =
          `Ranked alternatives (${recommendation.candidates.length - 1})`;
        alternativesBody = document.createElement("div");
        alternativesBody.className = "benchmark-recommendation-alternatives-body";
        alternatives.append(alternativesSummary, alternativesBody);
        container.append(alternatives);
      }
      alternativesBody.append(card);
    }
  }

  if (recommendation.candidates.length === 0) {
    const insufficient = document.createElement("p");
    insufficient.className = "benchmark-recommendation-warning";
    insufficient.textContent = "Not enough local evidence. Benchmark these combinations?";
    container.append(insufficient);
  }
  if (recommendation.missingEvidence.length > 0) {
    const missing = document.createElement("details");
    missing.className = "benchmark-recommendation-missing";
    const summary = document.createElement("summary");
    summary.textContent = `Evidence that would most increase confidence (${recommendation.missingEvidence.length})`;
    const list = document.createElement("ul");
    for (const item of recommendation.missingEvidence) {
      const row = document.createElement("li");
      row.textContent = `${item.model} × ${benchmarkHarnessLabel(item.harness)} · ${item.reason} · suggested suite: ${item.suggestedSuite}`;
      list.append(row);
    }
    missing.append(summary, list);
    container.append(missing);
  }
  if (
    recommendation.externalResearchStatus === "not-requested"
    && recommendation.externalEvidence.length === 0
  ) {
    return;
  }
  const external = document.createElement("section");
  external.className = "benchmark-recommendation-external";
  const externalTitle = document.createElement("h4");
  externalTitle.textContent = "External evidence (separate)";
  const externalStatus = document.createElement("p");
  externalStatus.textContent = `Status: ${recommendation.externalResearchStatus}`;
  external.append(externalTitle, externalStatus);
  if (recommendation.externalEvidence.length > 0) {
    const sources = document.createElement("ul");
    for (const source of recommendation.externalEvidence) {
      const item = document.createElement("li");
      const link = document.createElement("a");
      link.href = source.url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      link.textContent = source.title;
      const status = document.createElement("span");
      status.textContent = ` · ${source.status}`;
      item.append(link, status);
      sources.append(item);
    }
    external.append(sources);
  }
  container.append(external);
}

function recommendationList(title, items, kind) {
  const section = document.createElement("section");
  section.className = `benchmark-recommendation-${kind}`;
  const heading = document.createElement("h5");
  heading.textContent = title;
  const list = document.createElement("ul");
  for (const value of items) {
    const item = document.createElement("li");
    item.textContent = value;
    list.append(item);
  }
  section.append(heading, list);
  return section;
}

async function openBenchmarkRecommendationEvidence(event) {
  const button = event.target.closest("[data-recommendation-run-id]");
  if (!button) {
    return;
  }
  try {
    const runId = button.dataset.recommendationRunId;
    const selected = await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(runId)}`
    );
    state.benchmark.result = selected;
    state.benchmark.scoringProjection = null;
    elements.benchmarkHistory.value = runId;
    await rescoreBenchmarkResult();
    elements.benchmarkResultDetail.scrollIntoView({ behavior: "smooth", block: "start" });
    elements.benchmarkStatus.textContent = "Supporting local evidence opened.";
  } catch (error) {
    elements.benchmarkRecommendationStatus.textContent = benchmarkErrorMessage(error);
  }
}

async function runBenchmarkSuite(event) {
  event.preventDefault();
  if (state.activeBenchmarkRunId) {
    return;
  }
  const harnesses = [...elements.benchmarkHarnessList.querySelectorAll(
    'input[name="benchmark-harness"]:checked:not(:disabled)'
  )].map(input => input.value);
  if (harnesses.length === 0) {
    elements.benchmarkStatus.textContent = "Select at least one available harness.";
    return;
  }
  const models = selectedBenchmarkModels();
  if (models.length === 0) {
    elements.benchmarkStatus.textContent = "Select at least one installed local model.";
    return;
  }
  const clientRunId = globalThis.crypto?.randomUUID?.() ?? createSessionId();
  const suites = selectedBenchmarkSuites();
  if (suites.length === 0) {
    elements.benchmarkStatus.textContent = "Select CRUD, Agent Behavior, or both.";
    return;
  }
  state.activeBenchmarkRunId = clientRunId;
  sessionStorage.setItem("agentic-router-benchmark-live-run", clientRunId);
  setBenchmarkRunning(true);
  initializeBenchmarkLive(clientRunId, models, harnesses, suites);
  elements.benchmarkStatus.textContent = "Starting live dashboard…";
  elements.benchmarkResultsBody.replaceChildren();
  renderBenchmarkLive();

  try {
    const started = await fetchJson("/api/benchmarks/suite-runs/live", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        model: models[0],
        models,
        harnesses,
        suiteId: suites[0].id,
        suiteVersion: suites[0].version,
        suites: suites.map(suite => ({ id: suite.id, version: suite.version })),
        timeoutSeconds: Number(elements.benchmarkTimeout.value),
        scoringProfileId: elements.benchmarkScoringProfileChoice.value,
        scoreWeights: elements.benchmarkScoringProfileChoice.value === "custom"
          ? benchmarkWeightsFromInputs()
          : state.benchmark.catalog.scoreWeights,
        modelExecutionPermissionGranted: true,
        clientRunId
      })
    });
    state.activeBenchmarkRunId = started.runId;
    sessionStorage.setItem("agentic-router-benchmark-live-run", started.runId);
    state.benchmark.live.runId = started.runId;
    elements.benchmarkStatus.textContent = "Benchmark running; events connected.";
    connectBenchmarkEvents(started.runId, 0, started.eventsUrl);
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
    clearBenchmarkLiveConnection();
    state.activeBenchmarkRunId = null;
    state.benchmark.live = null;
    sessionStorage.removeItem("agentic-router-benchmark-live-run");
    setBenchmarkRunning(false);
  }
}

function initializeBenchmarkLive(
  runId,
  modelIds = selectedBenchmarkModels(),
  harnessIds = [],
  suites = selectedBenchmarkSuites()
) {
  const tests = suites.flatMap(suite => suite.tests ?? []);
  const cells = {};
  for (const model of modelIds) {
    for (const harness of harnessIds) {
      const key = benchmarkCellKey(model, harness);
      cells[key] = {
        id: key,
        model,
        harness,
        state: "pending",
        completed: 0,
        total: tests.length,
        passed: 0,
        score: null,
        terminality: 0,
        elapsedMilliseconds: 0,
        startedAt: null,
        currentTest: null,
        tests: Object.fromEntries(tests.map(test => [test.id, {
          id: test.id,
          state: "pending",
          currentTurn: 0,
          totalTurns: test.turnBudget ?? 1,
          activities: [],
          checks: {},
          result: null
        }]))
      };
    }
  }
  state.benchmark.live = {
    runId,
    startedAt: new Date().toISOString(),
    lastSequence: 0,
    terminal: false,
    ranking: [],
    models: modelIds,
    harnessIds,
    cells
  };
}

async function resumeLiveBenchmark(runId) {
  try {
    const view = await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(runId)}/live`
    );
    initializeBenchmarkLive(runId);
    state.activeBenchmarkRunId = runId;
    setBenchmarkRunning(!view.terminal);
    for (const progressEvent of view.events ?? []) {
      applyBenchmarkProgress(progressEvent, false);
    }
    if (!view.terminal) {
      elements.benchmarkStatus.textContent = view.cancellationRequested
        ? "Cancellation in progress…"
        : "Dashboard reconnected to the running benchmark.";
      connectBenchmarkEvents(runId, view.lastSequence);
      renderBenchmarkLive();
    }
  } catch (error) {
    sessionStorage.removeItem("agentic-router-benchmark-live-run");
    state.activeBenchmarkRunId = null;
    state.benchmark.live = null;
    setBenchmarkRunning(false);
    renderBenchmarkResult(state.benchmark.result);
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function connectBenchmarkEvents(runId, afterSequence = 0, eventsUrl = null) {
  if (state.benchmarkEventSource) {
    state.benchmarkEventSource.close();
  }
  const separator = (eventsUrl ?? "").includes("?") ? "&" : "?";
  const url = `${eventsUrl ?? `/api/benchmarks/suite-runs/${encodeURIComponent(runId)}/events`}`
    + `${separator}after=${Math.max(0, afterSequence)}`;
  const source = new EventSource(url);
  state.benchmarkEventSource = source;
  source.addEventListener("benchmark", event => {
    try {
      applyBenchmarkProgress(JSON.parse(event.data));
    } catch {
      elements.benchmarkStatus.textContent = "Invalid live event; waiting to reconnect.";
    }
  });
  source.onopen = () => {
    if (state.activeBenchmarkRunId) {
      elements.benchmarkStatus.textContent = "Benchmark running; events connected.";
    }
  };
  source.onerror = () => {
    if (state.activeBenchmarkRunId) {
      elements.benchmarkStatus.textContent = "Connection interrupted; reconnecting without canceling the run…";
    }
  };
  if (!state.benchmarkElapsedTimer) {
    state.benchmarkElapsedTimer = setInterval(() => {
      if (state.benchmark?.live && !state.benchmark.live.terminal) {
        renderBenchmarkLive();
      }
    }, 1000);
  }
}

function benchmarkCellKey(model, harness) {
  return `${model}\u001f${harness}`;
}

function ensureLiveCell(model, harnessId, total = 0) {
  const live = state.benchmark.live;
  const key = benchmarkCellKey(model, harnessId);
  if (!live.cells[key]) {
    live.cells[key] = {
      id: key,
      model,
      harness: harnessId,
      state: "pending",
      completed: 0,
      total,
      passed: 0,
      score: null,
      terminality: 0,
      elapsedMilliseconds: 0,
      startedAt: null,
      currentTest: null,
      tests: {}
    };
  }
  return live.cells[key];
}

function ensureLiveTest(harness, testId) {
  if (!harness.tests[testId]) {
    harness.tests[testId] = {
      id: testId,
      state: "pending",
      currentTurn: 0,
      totalTurns: 1,
      activities: [],
      checks: {},
      result: null
    };
  }
  return harness.tests[testId];
}

function applyBenchmarkProgress(progressEvent, render = true) {
  if (!state.benchmark?.live || progressEvent.runId !== state.benchmark.live.runId) {
    initializeBenchmarkLive(
      progressEvent.runId,
      progressEvent.selectedModels ?? (progressEvent.model ? [progressEvent.model] : []),
      progressEvent.selectedHarnesses ?? []
    );
  }
  const live = state.benchmark.live;
  if (progressEvent.sequence <= live.lastSequence) {
    return;
  }
  live.lastSequence = progressEvent.sequence;
  if (progressEvent.type === "run.started") {
    live.startedAt = progressEvent.startedAt ?? progressEvent.timestamp;
    live.models = progressEvent.selectedModels ?? live.models;
    live.harnessIds = progressEvent.selectedHarnesses ?? live.harnessIds;
    for (const model of live.models ?? []) {
      for (const harnessId of live.harnessIds ?? []) {
        const cell = ensureLiveCell(model, harnessId, progressEvent.totalTests);
        for (const test of progressEvent.tests ?? []) {
          ensureLiveTest(cell, test.id);
        }
      }
    }
  }
  const model = progressEvent.model ?? live.models?.[0] ?? state.benchmark?.result?.model ?? "model";
  const harness = progressEvent.harness
    ? ensureLiveCell(model, progressEvent.harness, progressEvent.totalTests)
    : null;
  const test = harness && progressEvent.testId
    ? ensureLiveTest(harness, progressEvent.testId)
    : null;
  if (progressEvent.type === "harness.started" && harness) {
    harness.state = "running";
    harness.startedAt = progressEvent.startedAt ?? progressEvent.timestamp;
  } else if (progressEvent.type === "harness.progress" && harness) {
    Object.assign(harness, {
      state: progressEvent.state,
      completed: progressEvent.completedTests,
      total: progressEvent.totalTests,
      passed: progressEvent.passedTests,
      score: progressEvent.provisionalScore,
      terminality: progressEvent.terminality,
      elapsedMilliseconds: progressEvent.elapsedMilliseconds
    });
  } else if (progressEvent.type === "harness.completed" && harness) {
    Object.assign(harness, {
      state: progressEvent.state,
      completed: progressEvent.completedTests,
      total: progressEvent.totalTests,
      passed: progressEvent.passedTests,
      score: progressEvent.provisionalScore,
      terminality: progressEvent.terminality,
      elapsedMilliseconds: progressEvent.elapsedMilliseconds,
      currentTest: null
    });
  } else if (progressEvent.type === "test.state" && test) {
    test.state = progressEvent.state;
    test.result = progressEvent.testResult ?? test.result;
    if (["running", "harness-completed", "validating"].includes(progressEvent.state)) {
      harness.currentTest = progressEvent.testId;
    }
  } else if (progressEvent.type === "activity" && test) {
    if (progressEvent.turnNumber) {
      test.currentTurn = progressEvent.turnNumber;
      test.totalTurns = progressEvent.totalTurns || test.totalTurns;
    }
    test.activities.push({
      kind: progressEvent.activityKind ?? "activity",
      message: progressEvent.message ?? "Activity recorded.",
      timestamp: progressEvent.timestamp,
      turnNumber: progressEvent.turnNumber || 0,
      totalTurns: progressEvent.totalTurns || 0
    });
    test.activities = test.activities.slice(-8);
  } else if (progressEvent.type === "validation" && test) {
    test.checks = progressEvent.validationChecks ?? {};
  } else if (progressEvent.type === "ranking.provisional") {
    live.ranking = progressEvent.ranking ?? [];
  } else if (progressEvent.type === "run.cancelling") {
    for (const item of Object.values(live.cells)) {
      if (!["completed", "cancelled"].includes(item.state)) {
        item.state = "cancelling";
      }
    }
    elements.benchmarkStatus.textContent = "Cancellation in progress…";
  } else if (progressEvent.type === "run.completed" && progressEvent.finalResult) {
    finishBenchmarkLive(progressEvent.finalResult);
    return;
  } else if (progressEvent.type === "run.failed") {
    failBenchmarkLive(progressEvent.error?.message ?? progressEvent.message);
    return;
  }
  if (render) {
    renderBenchmarkLive();
  }
}

function renderBenchmarkLive() {
  const live = state.benchmark?.live;
  if (!live) {
    return;
  }
  elements.benchmarkLiveDashboard.hidden = false;
  elements.benchmarkRankingNote.hidden = false;
  const cells = Object.values(live.cells);
  const terminalStates = new Set(["completed", "cancelled", "failed", "timed-out", "unsupported", "unavailable"]);
  const completedCells = cells.filter(cell => terminalStates.has(cell.state)).length;
  const currentCell = cells.find(cell => ["running", "validating", "harness-completed"].includes(cell.state));
  elements.benchmarkRunSummary.textContent =
    `Live run · ${completedCells}/${cells.length} cell(s) · `
    + `${currentCell ? `current ${currentCell.model} × ${benchmarkHarnessLabel(currentCell.harness)}` : "no active cell"} · `
    + `${Math.max(0, cells.length - completedCells)} remaining · sequential local run`;
  elements.benchmarkLiveDashboard.replaceChildren();
  for (const harness of cells) {
    const card = document.createElement("article");
    card.className = "benchmark-live-harness";
    card.dataset.state = harness.state;
    const elapsed = harness.startedAt && !["completed", "cancelled"].includes(harness.state)
      ? Date.now() - new Date(harness.startedAt).getTime()
      : harness.elapsedMilliseconds;
    const heading = document.createElement("h4");
    heading.textContent = `${harness.model} · ${benchmarkHarnessLabel(harness.harness)}`;
    const summary = document.createElement("p");
    summary.className = "benchmark-live-summary";
    summary.textContent = `${harness.state} · ${harness.completed}/${harness.total} · `
      + `${harness.passed} passed · score* ${harness.score === null ? "—" : Number(harness.score).toFixed(2)} · `
      + `terminality ${harness.terminality}% · ${formatBenchmarkDuration(Math.max(0, elapsed || 0))}`;
    const current = document.createElement("p");
    current.className = "benchmark-live-current";
    current.textContent = harness.currentTest
      ? `Current: ${harness.currentTest} · ${harness.tests[harness.currentTest]?.state ?? harness.state}`
        + (harness.tests[harness.currentTest]?.currentTurn
          ? ` · turn ${harness.tests[harness.currentTest].currentTurn}/${harness.tests[harness.currentTest].totalTurns}`
          : "")
      : "No active test.";
    card.append(heading, summary, current);
    for (const test of Object.values(harness.tests)) {
      const details = document.createElement("details");
      details.className = "benchmark-live-test";
      const testSummary = document.createElement("summary");
      testSummary.textContent = `${test.id} · ${test.state}`;
      details.append(testSummary);
      if (test.activities.length) {
        const list = document.createElement("ul");
        for (const activity of test.activities) {
          const item = document.createElement("li");
          item.textContent = activity.turnNumber
            ? `Turn ${activity.turnNumber}/${activity.totalTurns} · ${activity.kind}: ${activity.message}`
            : `${activity.kind}: ${activity.message}`;
          list.append(item);
        }
        details.append(list);
      }
      if (Object.keys(test.checks).length) {
        const checks = document.createElement("dl");
        checks.className = "benchmark-live-checks";
        for (const [name, value] of Object.entries(test.checks)) {
          const term = document.createElement("dt");
          term.textContent = name;
          const definition = document.createElement("dd");
          definition.textContent = String(value);
          checks.append(term, definition);
        }
        details.append(checks);
      }
      card.append(details);
    }
    elements.benchmarkLiveDashboard.append(card);
  }
  renderProvisionalBenchmarkRanking(live.ranking);
  elements.benchmarkResultDetail.textContent =
    "Expand a test in the card to view useful activity and validation facts. The final result will replace this state.";
}

function renderProvisionalBenchmarkRanking(ranking) {
  elements.benchmarkResultsBody.replaceChildren();
  for (const entry of ranking) {
    const row = document.createElement("tr");
    for (const value of [
      entry.rank
        ? `#${entry.rank} ${entry.model ?? ""} × ${benchmarkHarnessLabel(entry.harness)}`
        : `— ${entry.model ?? ""} × ${benchmarkHarnessLabel(entry.harness)}`,
      entry.state,
      `${entry.passed}/${entry.total}`,
      entry.score === null ? "—" : `${Number(entry.score).toFixed(2)}*`,
      formatBenchmarkDuration(entry.durationMilliseconds),
      `${entry.terminality}%`
    ]) {
      const cell = document.createElement("td");
      cell.textContent = value;
      row.append(cell);
    }
    elements.benchmarkResultsBody.append(row);
  }
}

function finishBenchmarkLive(result) {
  if (state.benchmark?.live?.terminal) {
    return;
  }
  state.benchmark.live.terminal = true;
  clearBenchmarkLiveConnection();
  state.activeBenchmarkRunId = null;
  sessionStorage.removeItem("agentic-router-benchmark-live-run");
  setBenchmarkRunning(false);
  state.benchmark.result = result;
  renderBenchmarkResult(result);
  refreshBenchmarkHistory({ selectRunId: result.runId });
  rescoreBenchmarkResult().catch(error => {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  });
  generateGeneralBenchmarkRecommendation().catch(error => {
    elements.benchmarkRecommendationStatus.textContent = benchmarkErrorMessage(error);
  });
  elements.benchmarkStatus.textContent = result.terminalState === "cancelled"
    ? "Run canceled with a persisted final result."
    : "Benchmark completed and persisted.";
}

function failBenchmarkLive(message) {
  if (state.benchmark?.live) {
    state.benchmark.live.terminal = true;
  }
  clearBenchmarkLiveConnection();
  state.activeBenchmarkRunId = null;
  sessionStorage.removeItem("agentic-router-benchmark-live-run");
  setBenchmarkRunning(false);
  elements.benchmarkStatus.textContent = message ?? "The live benchmark failed before the final result.";
}

function clearBenchmarkLiveConnection() {
  if (state.benchmarkEventSource) {
    state.benchmarkEventSource.close();
    state.benchmarkEventSource = null;
  }
  if (state.benchmarkElapsedTimer) {
    clearInterval(state.benchmarkElapsedTimer);
    state.benchmarkElapsedTimer = null;
  }
}

async function cancelBenchmarkSuite() {
  if (!state.activeBenchmarkRunId) {
    return;
  }
  elements.benchmarkStatus.textContent = "Requesting clean cancellation…";
  for (const harness of Object.values(state.benchmark?.live?.cells ?? {})) {
    if (!["completed", "cancelled"].includes(harness.state)) {
      harness.state = "cancelling";
    }
  }
  renderBenchmarkLive();
  try {
    await fetchJson(
      `/api/benchmarks/suite-runs/${encodeURIComponent(state.activeBenchmarkRunId)}/cancel`,
      { method: "POST" }
    );
  } catch (error) {
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function setBenchmarkRunning(running) {
  elements.runBenchmark.disabled = running;
  elements.cancelBenchmark.disabled = !running;
  elements.benchmarkModel.disabled = running;
  for (const input of elements.benchmarkModelList.querySelectorAll("input")) {
    input.disabled = running;
  }
  elements.benchmarkScoringProfileChoice.disabled = running;
  elements.benchmarkTimeout.disabled = running;
  for (const input of elements.benchmarkSuiteList.querySelectorAll("input")) {
    input.disabled = running;
  }
  elements.benchmarkHistory.disabled = running;
  elements.benchmarkHistoryModelFilter.disabled = running;
  elements.benchmarkHistoryHarnessFilter.disabled = running;
  elements.benchmarkHistorySuiteFilter.disabled = running;
  elements.benchmarkCompareBaseline.disabled = running;
  elements.benchmarkCompareCandidate.disabled = running;
  elements.compareBenchmarkRuns.disabled = running
    || (state.benchmark?.history?.length ?? 0) < 2;
  elements.generateBenchmarkRecommendation.disabled = running;
  elements.researchBenchmarkRecommendation.disabled = running
    || !state.benchmark?.recommendationCatalog?.externalResearchAvailable;
  for (const input of elements.benchmarkHarnessList.querySelectorAll("input")) {
    input.disabled = running || input.dataset.available === "false";
  }
}

async function openPersistedBenchmark() {
  try {
    const runId = elements.benchmarkHistory.value;
    elements.benchmarkStatus.textContent = runId
      ? "Loading persisted result…"
      : "Clearing loaded result…";
    const selected = runId
      ? await fetchJson(`/api/benchmarks/suite-runs/${encodeURIComponent(runId)}`)
      : null;
    state.benchmark.result = selected;
    state.benchmark.scoringProjection = null;
    if (selected) {
      const selections = selected.selectedSuites
        ?? [{ id: selected.suiteId, version: selected.suiteVersion }];
      for (const input of elements.benchmarkSuiteList.querySelectorAll(
        'input[name="benchmark-suite"]'
      )) {
        input.checked = selections.some(item =>
          item.id === input.value && item.version === Number(input.dataset.version)
        );
      }
      updateBenchmarkSuiteSelection();
    }
    await rescoreBenchmarkResult();
    await generateGeneralBenchmarkRecommendation();
    elements.benchmarkStatus.textContent = selected
      ? `Persisted result loaded: ${benchmarkSuiteLabel(selected.suiteId)} · ${selected.runId.slice(0, 8)}.`
      : "No persisted result loaded.";
  } catch (error) {
    renderBenchmarkResult(state.benchmark?.result ?? null);
    elements.benchmarkStatus.textContent = benchmarkErrorMessage(error);
  }
}

function renderBenchmarkResult(result) {
  elements.benchmarkLiveDashboard.hidden = true;
  elements.benchmarkRankingNote.hidden = true;
  elements.benchmarkMatrix.hidden = true;
  elements.benchmarkResultsBody.replaceChildren();
  elements.benchmarkResultDetail.replaceChildren();
  elements.benchmarkRawEvidence.hidden = !result;
  elements.benchmarkRawEvidence.open = false;
  elements.benchmarkRawEvidenceContent.textContent = result
    ? JSON.stringify(result, null, 2)
    : "";
  if (!result) {
    elements.benchmarkRunSummary.textContent = "Run or open a persisted result.";
    elements.benchmarkResultDetail.textContent = "Select a harness in the table to inspect scenarios.";
    return;
  }

  const projection = state.benchmark?.scoringProjection?.runId === result.runId
    ? state.benchmark.scoringProjection
    : null;
  const profile = projection?.activeProfile ?? state.benchmark?.scoringProfile;
  const modelSummary = result.selectedModels?.length > 1
    ? `${result.selectedModels.length} models × ${result.selectedHarnesses?.length ?? 0} harnesses`
    : result.selectedModels?.[0] ?? result.model;
  elements.benchmarkRunSummary.textContent =
    `${modelSummary} · ${benchmarkSuiteLabel(result.suiteId)} · ${result.finalStatus} · ${formatBenchmarkDuration(result.durationMilliseconds)}`;
  const originalScore = benchmarkAggregateOriginalScore(result);
  const currentScore = benchmarkAggregateCurrentScore(projection, result);
  elements.benchmarkScoreContext.textContent = profile
    ? `Measured evidence unchanged · Original score ${originalScore.toFixed(2)} · Current-profile score ${currentScore.toFixed(2)} with ${profile.displayName} v${profile.version}`
    : "Measured evidence and Calculated score are presented separately.";
  if ((result.cells ?? []).length > 0) {
    renderBenchmarkMatrix(result, projection);
    renderBenchmarkRankings(result, projection);
    const first = (projection?.pairRanking ?? result.pairRanking ?? [])[0];
    if (first) {
      renderBenchmarkMatrixCellDetail(first.model, first.harness);
    }
    return;
  }
  const byHarness = new Map(result.harnessResults.map(item => [item.harness, item]));
  const scoreByHarness = new Map(
    (projection?.harnessScores ?? []).map(item => [item.harness, item])
  );
  const ranking = projection?.ranking ?? result.ranking;
  for (const ranked of ranking) {
    const harness = byHarness.get(ranked.harness);
    if (!harness) {
      continue;
    }
    const row = document.createElement("tr");
    const harnessCell = document.createElement("td");
    const open = document.createElement("button");
    open.type = "button";
    open.className = "benchmark-result-link";
    open.dataset.harness = harness.harness;
    open.textContent = `#${ranked.rank} ${benchmarkHarnessLabel(harness.harness)}`;
    harnessCell.append(open);
    for (const value of [
      harness.terminalState,
      `${harness.passed}/${harness.total}`,
      Number(scoreByHarness.get(harness.harness)?.score ?? harness.score).toFixed(2),
      formatBenchmarkDuration(harness.durationMilliseconds),
      `${harness.terminality}%`
    ]) {
      const cell = document.createElement("td");
      cell.textContent = value;
      row.append(cell);
    }
    row.prepend(harnessCell);
    elements.benchmarkResultsBody.append(row);
  }
  const firstHarness = ranking[0]?.harness;
  if (firstHarness) {
    renderBenchmarkHarnessDetail(byHarness.get(firstHarness), scoreByHarness.get(firstHarness));
  }
}

function renderBenchmarkMatrix(result, projection) {
  elements.benchmarkMatrix.hidden = false;
  elements.benchmarkMatrix.replaceChildren();
  const models = result.selectedModels ?? [...new Set(result.cells.map(cell => cell.model))];
  const harnesses = result.selectedHarnesses ?? [...new Set(result.cells.map(cell => cell.harness))];
  const scoreByCell = new Map(
    (projection?.matrixCellScores ?? []).map(cell => [benchmarkCellKey(cell.model, cell.harness), cell.score])
  );
  const byCell = new Map(result.cells.map(cell => [benchmarkCellKey(cell.model, cell.harness), cell]));
  const table = document.createElement("table");
  const head = document.createElement("thead");
  const headRow = document.createElement("tr");
  const modelHeading = document.createElement("th");
  modelHeading.textContent = "Model";
  headRow.append(modelHeading);
  for (const harness of harnesses) {
    const heading = document.createElement("th");
    heading.textContent = benchmarkHarnessLabel(harness);
    headRow.append(heading);
  }
  head.append(headRow);
  const body = document.createElement("tbody");
  for (const model of models) {
    const row = document.createElement("tr");
    const label = document.createElement("th");
    label.scope = "row";
    label.textContent = model;
    row.append(label);
    for (const harness of harnesses) {
      const cellElement = document.createElement("td");
      const cell = byCell.get(benchmarkCellKey(model, harness));
      const button = document.createElement("button");
      button.type = "button";
      button.className = "benchmark-matrix-cell";
      button.dataset.model = model;
      button.dataset.harness = harness;
      button.dataset.status = cell?.status ?? "unavailable";
      const calculated = scoreByCell.get(benchmarkCellKey(model, harness));
      button.textContent = cell?.status === "completed"
        ? Number(calculated ?? cell.score).toFixed(2)
        : cell?.status ?? "unavailable";
      button.title = cell?.message ?? `${model} × ${benchmarkHarnessLabel(harness)}`;
      cellElement.append(button);
      row.append(cellElement);
    }
    body.append(row);
  }
  table.append(head, body);
  elements.benchmarkMatrix.append(table);
}

function renderBenchmarkRankings(result, projection) {
  elements.benchmarkResultsBody.replaceChildren();
  if (!result) {
    return;
  }
  const scope = elements.benchmarkRankingScope.value;
  if (scope === "model" || scope === "harness") {
    const ranking = scope === "model"
      ? projection?.modelRanking ?? result.modelRanking ?? []
      : projection?.harnessRanking ?? result.harnessRanking ?? [];
    for (const entry of ranking) {
      const row = document.createElement("tr");
      const label = scope === "model" ? entry.id : benchmarkHarnessLabel(entry.id);
      for (const value of [
        `#${entry.rank} ${label}`,
        `${entry.completedCells}/${entry.totalCells} completed`,
        String(entry.passed),
        Number(entry.score).toFixed(2),
        formatBenchmarkDuration(entry.durationMilliseconds),
        `${entry.terminality}%`
      ]) {
        const cell = document.createElement("td");
        cell.textContent = value;
        row.append(cell);
      }
      elements.benchmarkResultsBody.append(row);
    }
    return;
  }
  const ranking = projection?.pairRanking ?? result.pairRanking ?? [];
  for (const entry of ranking) {
    const resultCell = result.cells?.find(cell =>
      cell.model === entry.model && cell.harness === entry.harness
    );
    const row = document.createElement("tr");
    const identity = document.createElement("td");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "benchmark-result-link";
    button.dataset.model = entry.model;
    button.dataset.harness = entry.harness;
    button.textContent = `#${entry.rank} ${entry.model} × ${benchmarkHarnessLabel(entry.harness)}`;
    identity.append(button);
    row.append(identity);
    for (const value of [
      entry.status,
      `${entry.passed}/${resultCell?.total ?? 0}`,
      Number(entry.score).toFixed(2),
      formatBenchmarkDuration(entry.durationMilliseconds),
      `${entry.terminality}%`
    ]) {
      const cell = document.createElement("td");
      cell.textContent = value;
      row.append(cell);
    }
    elements.benchmarkResultsBody.append(row);
  }
}

function openBenchmarkMatrixCell(event) {
  const button = event.target.closest("[data-model][data-harness]");
  if (button) {
    renderBenchmarkMatrixCellDetail(button.dataset.model, button.dataset.harness);
  }
}

function renderBenchmarkMatrixCellDetail(model, harnessId) {
  const cell = state.benchmark?.result?.cells?.find(item =>
    item.model === model && item.harness === harnessId
  );
  const calculated = state.benchmark?.scoringProjection?.matrixCellScores?.find(item =>
    item.model === model && item.harness === harnessId
  );
  if (!cell?.result) {
    elements.benchmarkResultDetail.replaceChildren();
    const heading = document.createElement("h4");
    heading.textContent = `${model} × ${benchmarkHarnessLabel(harnessId)} · ${cell?.status ?? "unavailable"}`;
    const message = document.createElement("p");
    message.textContent = cell?.message ?? "This combination did not produce an executable result.";
    elements.benchmarkResultDetail.append(heading, message);
    return;
  }
  renderBenchmarkHarnessDetail(cell.result, calculated, model);
}

function openBenchmarkHarnessResult(event) {
  const button = event.target.closest("[data-harness]");
  if (!button) {
    return;
  }
  if (button.dataset.model) {
    renderBenchmarkMatrixCellDetail(button.dataset.model, button.dataset.harness);
    return;
  }
  const harness = state.benchmark?.result?.harnessResults?.find(
    item => item.harness === button.dataset.harness
  );
  const score = state.benchmark?.scoringProjection?.harnessScores?.find(
    item => item.harness === button.dataset.harness
  );
  renderBenchmarkHarnessDetail(harness, score);
}

function renderBenchmarkHarnessDetail(harness, calculated, model = null) {
  elements.benchmarkResultDetail.replaceChildren();
  if (!harness) {
    elements.benchmarkResultDetail.textContent = "Harness result unavailable.";
    return;
  }
  const heading = document.createElement("h4");
  heading.textContent = `${model ? `${model} × ` : ""}${benchmarkHarnessLabel(harness.harness)} · ${harness.passed}/${harness.total} passed`;
  elements.benchmarkResultDetail.append(heading);
  if (calculated) {
    const scoreHeading = document.createElement("strong");
    scoreHeading.textContent = `Calculated score · ${Number(calculated.score).toFixed(2)}`;
    const breakdown = document.createElement("dl");
    breakdown.className = "benchmark-score-breakdown";
    for (const [label, value] of [
      ["Objective success", calculated.breakdown.objectiveSuccess],
      ["Correctness / exactness", calculated.breakdown.correctness],
      ["Terminality", calculated.breakdown.terminality],
      ["Workspace accuracy", calculated.breakdown.workspaceAccuracy],
      ["Efficiency", calculated.breakdown.efficiency]
    ]) {
      const term = document.createElement("dt");
      term.textContent = label;
      const definition = document.createElement("dd");
      definition.textContent = Number(value).toFixed(2);
      breakdown.append(term, definition);
    }
    elements.benchmarkResultDetail.append(scoreHeading, breakdown);
  }
  if (harness.tests.length === 0) {
    const empty = document.createElement("p");
    empty.textContent = "No test started before cancellation.";
    elements.benchmarkResultDetail.append(empty);
    return;
  }
  for (const test of harness.tests) {
    const details = document.createElement("details");
    details.className = "benchmark-test-detail";
    const summary = document.createElement("summary");
    const calculatedTest = calculated?.tests?.find(item => item.runId === test.run.runId);
    summary.textContent = `${test.run.testId} · ${test.rawResult.status} · calculated score ${Number(calculatedTest?.score?.total ?? test.score?.total ?? 0).toFixed(2)}`;
    details.append(summary);
    const facts = document.createElement("dl");
    facts.className = "benchmark-evidence-grid";
    const evidenceHeading = document.createElement("strong");
    evidenceHeading.textContent = "Measured evidence";
    const evidence = [
      ["Terminal", test.rawResult.executionStatus],
      ["Exactness", `${test.rawResult.exactness}%`],
      ["Workspace", `${test.rawResult.containmentAccuracy}%`],
      ["Host validation", test.rawResult.hostValidationResult],
      ["Duration", formatBenchmarkDuration(test.durationMilliseconds)],
      ["Workspace id", test.run.workspaceId],
      ["Fixture fingerprint", test.run.fixtureFingerprint],
      ["Workspace cleaned", test.workspaceCleanedUp],
      ["Tool calls", test.rawResult.toolCallCount ?? "n/d"],
      ["Errors / recovered", `${test.rawResult.surfacedErrorCount ?? "n/d"} / ${test.rawResult.recoveredErrorCount ?? "n/d"}`],
      ["Changed files", (test.rawResult.changedFiles ?? []).join(", ") || "none"],
      ["Unexpected", (test.rawResult.unexpectedFiles ?? []).join(", ") || "none"],
      ["Turns", `${test.rawResult.behaviorMetrics?.successfulTerminalTurns ?? 0}/${test.rawResult.behaviorMetrics?.totalTurns ?? 0}`],
      ["Continuity", benchmarkMetric(test.rawResult.behaviorMetrics?.continuityPreservation)],
      ["Scope accuracy", benchmarkMetric(test.rawResult.behaviorMetrics?.scopeAccuracy)],
      ["Recovery", benchmarkMetric(test.rawResult.behaviorMetrics?.recovery)],
      ["Convergence", benchmarkMetric(test.rawResult.behaviorMetrics?.convergence)],
      ["Hygiene", benchmarkMetric(test.rawResult.behaviorMetrics?.hygiene)],
      ["Truthful report", benchmarkMetric(test.rawResult.behaviorMetrics?.truthfulFinalReport)],
      ["Narration", test.rawResult.behaviorMetrics?.narrationClassification ?? "n/d"],
      ...Object.entries(test.rawResult.validationFacts ?? {}).map(
        ([key, value]) => [`Validation · ${key}`, value]
      )
    ];
    for (const [label, value] of evidence) {
      const term = document.createElement("dt");
      term.textContent = label;
      const definition = document.createElement("dd");
      definition.textContent = String(value);
      facts.append(term, definition);
    }
    details.append(evidenceHeading, facts);
    if (test.rawResult.error) {
      const error = document.createElement("p");
      error.className = "benchmark-validation-error";
      error.textContent = `${test.rawResult.error.code}: ${test.rawResult.error.message}`;
      details.append(error);
    }
    const promptLabel = document.createElement("strong");
    promptLabel.textContent = "Canonical prompt";
    const prompt = document.createElement("pre");
    prompt.textContent = test.run.prompt;
    const reportLabel = document.createElement("strong");
    reportLabel.textContent = "Final harness report";
    const report = document.createElement("pre");
    report.textContent = test.rawResult.finalHarnessReport || "(no report)";
    details.append(promptLabel, prompt, reportLabel, report);
    if ((test.rawResult.turns ?? []).length > 0) {
      const turnsLabel = document.createElement("strong");
      turnsLabel.textContent = "Persisted turns";
      const turns = document.createElement("ol");
      for (const turn of test.rawResult.turns) {
        const item = document.createElement("li");
        item.textContent = `${turn.order}. ${turn.name} · ${turn.executionStatus} · ${turn.durationMilliseconds} ms · ${turn.finalReport || "(no report)"}`;
        turns.append(item);
      }
      details.append(turnsLabel, turns);
    }
    if ((test.rawResult.hostEvents ?? []).length > 0) {
      const hostLabel = document.createElement("strong");
      hostLabel.textContent = "Host events";
      const hostEvents = document.createElement("ul");
      for (const hostEvent of test.rawResult.hostEvents) {
        const item = document.createElement("li");
        item.textContent = `After turn ${hostEvent.afterTurn} · ${hostEvent.type}: ${hostEvent.message}`;
        hostEvents.append(item);
      }
      details.append(hostLabel, hostEvents);
    }
    elements.benchmarkResultDetail.append(details);
  }
}

function benchmarkMetric(value) {
  return value === null || value === undefined ? "n/d" : `${value}%`;
}

function benchmarkHarnessLabel(harnessId) {
  return state.benchmark?.catalog?.harnesses?.find(
    item => item.definition.id === harnessId
  )?.definition.displayName
    ?? state.harnesses?.find(item => item.definition.id === harnessId)
      ?.definition.displayName
    ?? harnessId;
}

function formatBenchmarkDuration(milliseconds) {
  if (milliseconds < 1000) {
    return `${milliseconds} ms`;
  }
  return `${(milliseconds / 1000).toFixed(2)} s`;
}

function benchmarkErrorMessage(error) {
  const errors = error.payload?.errors;
  const first = errors
    ? Object.values(errors).flat()[0]
    : null;
  return first ?? error.message;
}

function renderRecoveryState() {
  const recovery = state.recovery;
  elements.safeModeBanner.hidden = !recovery?.safeMode;
  document.body.dataset.historyAutoload =
    recovery?.historyAutoLoadDisabled ? "disabled" : "enabled";
  elements.safeModeReason.textContent = recovery?.reason
    ?? "Execute, cloud, and configuration changes are disabled.";

  if (!recovery?.safeMode) {
    return;
  }

  document.querySelector("[data-mode=\"execute\"]").disabled = true;
  elements.saveSettings.disabled = true;
  elements.importSettingsYaml.disabled = true;
  elements.messageInput.disabled = true;
  elements.sendButton.disabled = true;
}

function updateProviderStatus(response) {
  elements.providerBadge.textContent = response.available ? "Online" : "Unavailable";
  elements.providerBadge.className = `badge ${response.available ? "success" : "error"}`;
  elements.runtimeDetails.dataset.provider = response.available ? "online" : "offline";
}

function updateDeviceStatus() {}

function renderWorkspace() {
  const active = activeWorkspaceProfile();
  const workspace = state.workspace;
  const valid = Boolean(workspace?.valid);
  elements.workspacePath.textContent = active
    ? `${active.name} · ${active.path}`
    : "No folder selected";
  renderProjectSidebar();
  elements.workspaceValidation.textContent = workspace?.diagnostic
    ?? workspace?.status
    ?? "Not configured";
  elements.workspaceValidation.className =
    `workspace-validation ${valid ? "valid" : workspace?.configured ? "invalid" : ""}`;
  elements.trustedWorkspacePath.value = workspace?.path ?? "";
  elements.workspaceProfileName.value = "";
  elements.clearWorkspace.disabled = !workspace?.configured;
  elements.workspaceHistoryEnabled.checked = Boolean(active?.historyEnabled);
}

function activeWorkspaceProfile() {
  return state.workspaceProfiles?.profiles?.find(profile => profile.active) ?? null;
}

function shortenPath(path) {
  if (!path || path.length <= 46) {
    return path ?? "";
  }

  return `…${path.slice(-45)}`;
}

async function refreshGit() {
  if (!activeWorkspaceProfile()) {
    state.git = {
      state: "unavailable",
      diagnostic: "No active trusted workspace.",
      repository: null,
      currentSessionPaths: [],
      remotes: []
    };
    renderGitCard();
    renderSettingsSummaries();
    return;
  }

  try {
    state.git = await fetchJson("/api/git");
  } catch (error) {
    state.git = {
      state: "unavailable",
      diagnostic: error.message,
      repository: null,
      currentSessionPaths: [],
      remotes: []
    };
  }

  renderGitCard();
  renderSettingsSummaries();

  if (elements.gitDialog.open) {
    renderGitPanel();
  }
}

function renderGitCard() {
  const git = state.git;
  const repository = git?.repository;
  elements.gitViewFolder.hidden = !activeWorkspaceProfile()?.available;
  elements.gitInitializeQuick.hidden = git?.state !== "not-initialized";
  elements.gitCommitQuick.hidden = git?.state !== "available";
  elements.gitPushQuick.hidden = git?.state !== "available";
  elements.gitCard.classList.toggle(
    "has-conflicts",
    (repository?.conflictedPaths?.length ?? 0) > 0
  );
  elements.gitCard.classList.toggle(
    "detached",
    Boolean(repository?.detachedHead)
  );

  if (git?.state === "available" && repository) {
    const changes = new Set([
      ...repository.stagedPaths,
      ...repository.unstagedPaths,
      ...repository.untrackedPaths
    ]).size;
    const branch = repository.detachedHead
      ? `detached ${shortHash(repository.head)}`
      : repository.branch ?? "unborn";
    elements.gitBadge.textContent = repository.conflictedPaths.length > 0
      ? "Conflicts"
      : repository.clean
        ? "Clean"
        : `${changes} changes`;
    elements.gitBadge.className =
      `badge ${repository.conflictedPaths.length > 0 ? "error" : repository.clean ? "success" : "muted"}`;
    elements.gitSummary.textContent =
      `${branch} · ${repository.clean ? "clean" : `${changes} changes`}`;
    elements.gitUpstreamSummary.textContent = repository.upstream
      ? `${repository.upstream} · ahead ${repository.ahead} · behind ${repository.behind}`
      : "No upstream";
    elements.gitCard.setAttribute(
      "aria-label",
      `Git repository. ${branch}. ${changes} changes. `
        + `${repository.conflictedPaths.length} conflicts. `
        + `${repository.upstream ?? "No upstream"}. `
        + `Ahead ${repository.ahead}, behind ${repository.behind}.`
    );
    elements.gitCommitQuick.disabled = repository.clean
      || repository.truncated
      || repository.conflictedPaths.length > 0
      || Boolean(repository.operationInProgress);
    elements.gitPushQuick.disabled = Boolean(repository.detachedHead)
      || Boolean(repository.operationInProgress);
    return;
  }

  const notInitialized = git?.state === "not-initialized";
  elements.gitBadge.textContent = notInitialized ? "Not initialized" : "Unavailable";
  elements.gitBadge.className = `badge ${notInitialized ? "muted" : "error"}`;
  elements.gitSummary.textContent = notInitialized
    ? "Not initialized"
    : "Unavailable";
  elements.gitUpstreamSummary.textContent =
    git?.diagnostic ?? "Open the Git panel for details.";
  elements.gitCard.setAttribute(
    "aria-label",
    `Git: ${elements.gitSummary.textContent}. ${elements.gitUpstreamSummary.textContent}`
  );
  elements.gitInitializeQuick.disabled = !notInitialized;
}

async function requireExecuteForGitAction(actionName) {
  if (state.interactionMode === "execute") {
    return true;
  }

  if (!await showAppConfirm(
    `${actionName} changes the workspace and requires Execute mode. `
      + "Switch to Execute now? The action will not run until you select it again.",
    {
      title: "Switch to Execute?",
      confirmLabel: "Switch to Execute"
    }
  )) {
    return false;
  }

  setInteractionMode("execute");
  elements.gitQuickStatus.textContent =
    "Execute mode enabled. Select the Git action again to confirm.";
  return false;
}

async function initializeGitRepositoryQuick() {
  if (!await requireExecuteForGitAction("Initialize the repository")) {
    return;
  }
  if (!await showAppConfirm(
    "Initialize Git at the active project root with the main branch? No commit or remote will be created.",
    {
      title: "Initialize Git repository?",
      confirmLabel: "Initialize"
    }
  )) {
    return;
  }

  elements.gitQuickStatus.textContent = "Initializing…";
  try {
    state.git = await fetchJson(
      "/api/git/initialize",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          interactionMode: state.interactionMode,
          actionId: state.git.initializeActionId,
          confirmed: true
        })
      }
    );
    renderGitCard();
    renderSettingsSummaries();
    elements.gitQuickStatus.textContent = "Repository initialized on main.";
  } catch (error) {
    elements.gitQuickStatus.textContent = gitActionError(error);
  }
}

async function commitProjectChanges() {
  if (!await requireExecuteForGitAction("Create a commit")) {
    return;
  }
  const requiresValidationOverride = Boolean(
    state.settings?.gitDelivery?.requireValidationBeforeCommit
  );
  const message = await showAppPrompt(
    "Enter a message or leave it blank to generate a concise message with the selected local model."
      + `${requiresValidationOverride
        ? " This compact flow confirms the configured option for an explicit commit without session validation."
        : ""}`,
    {
      title: "Commit current changes",
      inputLabel: "Optional message",
      confirmLabel: "Commit"
    }
  );
  if (message === null) {
    return;
  }

  let model = null;
  if (message.trim().length === 0) {
    const selected = elements.modelSelector.value;
    const selectedOption = modelOptions().find(option => option.value === selected);
    if (selected === "auto" || selectedOption?.provider !== "ollama-local") {
      elements.gitQuickStatus.textContent =
        "Select a specific local model or enter a commit message.";
      return;
    }
    model = selected;
  }

  elements.gitQuickStatus.textContent = message.trim().length === 0
    ? "Generating a local message and creating the commit…"
    : "Creating commit…";
  try {
    const result = await fetchJson(
      "/api/git/commit",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          interactionMode: state.interactionMode,
          actionId: state.git.commitActionId,
          confirmed: true,
          message,
          model,
          commitWithoutValidation: requiresValidationOverride
        })
      }
    );
    state.git = result.overview;
    renderGitCard();
    renderSettingsSummaries();
    elements.gitQuickStatus.textContent =
      `Commit ${shortHash(result.commitHash)} created: ${result.commitSubject}`;
  } catch (error) {
    elements.gitQuickStatus.textContent = gitActionError(error);
  }
}

async function pushProjectBranch() {
  if (!await requireExecuteForGitAction("Push the current branch")) {
    return;
  }
  const upstream = state.git?.repository?.upstream;
  if (!await showAppConfirm(
    upstream
      ? `Push the current branch to ${upstream} using the existing configuration?`
      : "The current branch has no upstream. Try the push to get the Host diagnostic?",
    {
      title: "Push the current branch?",
      confirmLabel: "Push"
    }
  )) {
    return;
  }

  elements.gitQuickStatus.textContent = "Running preflight and push…";
  try {
    const result = await fetchJson(
      "/api/git/push",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          interactionMode: state.interactionMode,
          actionId: state.git.pushActionId,
          confirmed: true
        })
      }
    );
    state.git = result.overview;
    renderGitCard();
    renderSettingsSummaries();
    elements.gitQuickStatus.textContent = "Push confirmed by the upstream.";
  } catch (error) {
    elements.gitQuickStatus.textContent = gitActionError(error);
  }
}

async function viewCurrentWorkspaceFolder() {
  elements.gitViewFolder.disabled = true;
  elements.gitQuickStatus.textContent = "Opening folder in Explorer…";

  try {
    await fetchJson(
      "/api/workspaces/active/open-folder",
      {
        method: "POST"
      }
    );
    elements.gitQuickStatus.textContent = "Folder opened in Explorer.";
  } catch (error) {
    elements.gitQuickStatus.textContent = error.message;
  } finally {
    elements.gitViewFolder.disabled = false;
  }
}

function gitActionError(error) {
  return `${error.message}${error.payload?.diagnostic
    ? ` · ${error.payload.diagnostic}`
    : ""}${error.payload?.traceId
    ? ` · Trace ID: ${error.payload.traceId}`
    : ""}`;
}

async function openGitPanel() {
  elements.runtimeDetails.open = false;
  elements.gitActionStatus.textContent = "";
  await refreshGit();
  renderGitPanel();
  elements.gitDialog.showModal();
  elements.closeGit.focus();

  if (state.git?.state === "available") {
    await loadGitDiff(state.activeGitView);
  }
}

function closeGitPanel() {
  elements.gitDialog.close();
  elements.gitCard.focus();
}

async function refreshGitPanel() {
  elements.gitActionStatus.textContent = "Refreshing…";
  await refreshGit();
  renderGitPanel();
  if (state.git?.state === "available") {
    await loadGitDiff(state.activeGitView);
  }
  elements.gitActionStatus.textContent = "Git status refreshed.";
}

function renderGitPanel() {
  const git = state.git;
  const repository = git?.repository;
  elements.gitPanelStatus.textContent = git?.diagnostic
    ?? (git?.state === "available"
      ? "Repository state refreshed by the Host."
      : "Git state unavailable.");
  elements.gitOverview.replaceChildren();

  const facts = git?.state === "available" && repository
    ? [
      ["Repository root", repository.repositoryRoot ?? "."],
      ["Branch", repository.detachedHead ? "detached HEAD" : repository.branch ?? "unborn"],
      ["HEAD", shortHash(repository.head)],
      ["Latest commit", git.latestCommit
        ? `${shortHash(git.latestCommit.hash)} · ${git.latestCommit.subject}`
        : "No commits"],
      ["Latest timestamp", git.latestCommit?.authoredAt
        ? new Date(git.latestCommit.authoredAt).toLocaleString(window.AgenticRouterI18n.locale)
        : "Unavailable"],
      ["Working state", repository.clean ? "clean" : "dirty"],
      ["Upstream", repository.upstream ?? "Not configured"],
      ["Ahead / behind", `${repository.ahead} / ${repository.behind}`],
      ["Operation", repository.operationInProgress ?? "none"],
      ["Git executable", git.executablePath ?? "Unavailable"],
      ["Git version", git.version ?? "Unavailable"],
      ["Default branch", git.defaultBranch ?? "Not configured"]
    ]
    : [
      ["State", git?.state ?? "unavailable"],
      ["Git executable", git?.executablePath ?? "Unavailable"],
      ["Git version", git?.version ?? "Unavailable"],
      ["Default branch", git?.defaultBranch ?? "Not configured"]
    ];
  for (const [label, value] of facts) {
    const item = document.createElement("div");
    const term = document.createElement("dt");
    term.textContent = label;
    const description = document.createElement("dd");
    description.textContent = value;
    description.title = value;
    item.append(term, description);
    elements.gitOverview.append(item);
  }

  elements.gitInitializePanel.hidden = git?.state !== "not-initialized";
  const available = git?.state === "available";
  elements.gitUserName.disabled = !available;
  elements.gitUserEmail.disabled = !available;
  elements.gitOriginUrl.disabled = !available;
  elements.gitUserName.value = git?.userName?.value ?? "";
  elements.gitUserEmail.value = git?.userEmail?.value ?? "";
  elements.gitOriginUrl.value = git?.remotes?.find(
    remote => remote.name === "origin"
  )?.fetchUrl ?? "";
  elements.gitUserNameScope.textContent =
    `Effective scope: ${git?.userName?.scope ?? "unset"}`;
  elements.gitUserEmailScope.textContent =
    `Effective scope: ${git?.userEmail?.scope ?? "unset"}`;
  elements.gitRemotes.replaceChildren();
  for (const remote of git?.remotes ?? []) {
    const row = document.createElement("div");
    row.className = "git-remote-row";
    const name = document.createElement("strong");
    name.textContent = remote.name;
    const url = document.createElement("code");
    url.textContent = remote.fetchUrl;
    row.append(name, url);
    elements.gitRemotes.append(row);
  }
  if ((git?.remotes?.length ?? 0) === 0) {
    elements.gitRemotes.textContent = "No remotes configured.";
  }
  state.gitConfigurationEditing = false;
  renderGitConfigurationEditState();
  elements.gitOpenReview.disabled = !state.latestExecutionSessionId;
}

async function initializeGitRepository() {
  if (state.interactionMode !== "execute") {
    const switchMode = await showAppConfirm(
      "Repository creation requires Execute mode. The Git panel will close and no change will be made until you reopen it and confirm initialization.",
      {
        title: "Switch to Execute mode?",
        confirmLabel: "Close and switch to Execute"
      }
    );
    if (switchMode) {
      closeGitPanel();
      setInteractionMode("execute");
      showToast(
        "Execute mode enabled. Reopen the Git panel to review and confirm repository creation.",
        "success"
      );
    }
    return;
  }
  const facts = "Initialize Git repository at the trusted-workspace root.\n"
    + "Initial branch: main\nNo commit, staging, remote, or project file will be created.";
  if (!await showAppConfirm(facts, {
    title: "Initialize Git repository?",
    confirmLabel: "Initialize"
  })) {
    return;
  }

  try {
    state.git = await fetchJson(
      "/api/git/initialize",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          interactionMode: state.interactionMode,
          actionId: state.git.initializeActionId,
          confirmed: true
        })
      }
    );
    state.projectProfile = await fetchJson(
      "/api/workspace/project-profile/refresh",
      {
        method: "POST"
      }
    );
    renderProjectProfile();
    renderGitCard();
    renderGitPanel();
    elements.gitActionStatus.textContent =
      "Repository initialized on main. No commit or remote was created.";
    await loadGitDiff("working-tree");
  } catch (error) {
    elements.gitActionStatus.textContent =
      `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
  }
}

function renderGitConfigurationEditState() {
  const editing = state.gitConfigurationEditing;
  const available = state.git?.state === "available";
  for (const input of [
    elements.gitUserName,
    elements.gitUserEmail,
    elements.gitOriginUrl
  ]) {
    input.readOnly = !editing;
  }
  elements.editGitConfiguration.hidden = editing;
  elements.editGitConfiguration.disabled = !available;
  elements.saveGitConfiguration.hidden = !editing;
  elements.cancelGitConfiguration.hidden = !editing;
}

function beginGitConfigurationEdit() {
  state.gitConfigurationEditing = true;
  renderGitConfigurationEditState();
  elements.gitUserName.focus();
}

function cancelGitConfigurationEdit() {
  state.gitConfigurationEditing = false;
  renderGitPanel();
}

async function saveGitConfiguration() {
  if (state.interactionMode !== "execute") {
    showToast("Switch to Execute mode before changing repository configuration.");
    return;
  }

  try {
    const changes = [];
    const identityValues = [
      ["user.name", elements.gitUserName.value.trim(), state.git?.userName?.value ?? ""],
      ["user.email", elements.gitUserEmail.value.trim(), state.git?.userEmail?.value ?? ""]
    ];
    for (const [field, value, current] of identityValues) {
      if (value !== current) {
        const preview = await fetchJson("/api/git/identity/preview", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ field, value })
        });
        changes.push({ kind: "identity", field, value: preview.value, preview });
      }
    }
    const origin = elements.gitOriginUrl.value.trim();
    const currentOrigin = state.git?.remotes?.find(
      remote => remote.name === "origin"
    )?.fetchUrl ?? "";
    if (origin !== currentOrigin) {
      const preview = await fetchJson("/api/git/remote/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ remoteName: "origin", url: origin })
      });
      changes.push({ kind: "remote", value: preview.url, preview });
    }
    if (changes.length === 0) {
      state.gitConfigurationEditing = false;
      renderGitConfigurationEditState();
      elements.gitActionStatus.textContent = "No changes to save.";
      return;
    }
    const summary = changes.map(change => change.kind === "identity"
      ? `${change.field} = "${change.value}"`
      : `origin = "${change.value}"`
    ).join("\n");
    if (!await showAppConfirm(
      `Apply to the local repository:\n${summary}\n\nGlobal Git configuration will not be changed.`,
      { title: "Save repository configuration?", confirmLabel: "Save" }
    )) {
      return;
    }
    for (const change of changes) {
      const currentPreview = change.kind === "identity"
        ? await fetchJson("/api/git/identity/preview", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ field: change.field, value: change.value })
        })
        : await fetchJson("/api/git/remote/preview", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ remoteName: "origin", url: change.value })
        });
      const path = change.kind === "identity"
        ? "/api/git/identity"
        : "/api/git/remote";
      const body = change.kind === "identity"
        ? {
          browserSessionId: state.browserSessionId,
          interactionMode: state.interactionMode,
          actionId: currentPreview.actionId,
          confirmed: true,
          field: change.field,
          value: change.value
        }
        : {
          browserSessionId: state.browserSessionId,
          interactionMode: state.interactionMode,
          actionId: currentPreview.actionId,
          confirmed: true,
          remoteName: "origin",
          url: change.value
        };
      state.git = await fetchJson(path, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    }
    state.gitConfigurationEditing = false;
    renderGitCard();
    renderGitPanel();
    renderSettingsSummaries();
    elements.gitActionStatus.textContent = "Local repository configuration saved.";
    showToast("Repository configuration saved.", "success");
  } catch (error) {
    const message = `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
    elements.gitActionStatus.textContent = message;
    showToast(message);
  }
}

function selectGitView(event) {
  state.activeGitView = event.currentTarget.dataset.gitView;
  document.querySelectorAll("[data-git-view]").forEach(
    button => button.setAttribute(
      "aria-selected",
      String(button === event.currentTarget)
    )
  );
  void loadGitDiff(state.activeGitView);
}

async function loadGitDiff(view) {
  state.activeGitView = view;
  elements.gitDiffContent.textContent = "Loading bounded diff…";
  elements.gitDiffMetadata.textContent = "";

  try {
    state.activeGitDiff = await fetchJson(
      "/api/git/diff",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          view,
          paths: []
        })
      }
    );
    renderGitDiff();
  } catch (error) {
    state.activeGitDiff = null;
    elements.gitFileList.replaceChildren();
    elements.gitDiffContent.textContent = error.message;
    elements.gitDiffMetadata.textContent =
      error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : "";
  }
}

function renderGitDiff() {
  const files = state.activeGitDiff?.files ?? [];
  elements.gitFileList.replaceChildren();
  elements.gitDiffMetadata.textContent = files.length === 0
    ? state.activeGitView === "last-commit" && !state.git?.latestCommit
      ? "No commit exists yet; an initial-tree diff is unavailable."
      : "No files in this view."
    : `${files.length} file(s)${state.activeGitDiff.truncated ? " · truncated" : ""}`;
  elements.gitDiffContent.textContent = files.length === 0
    ? "No diff available."
    : "Select a file to expand its diff.";

  for (const file of files) {
    const button = document.createElement("button");
    button.type = "button";
    button.setAttribute("aria-expanded", "false");
    const type = document.createElement("span");
    type.className = "git-file-status";
    type.dataset.changeType = file.changeType.toLowerCase();
    type.textContent = file.binary ? "BIN" : file.changeType.slice(0, 1).toUpperCase();
    const path = document.createElement("span");
    path.className = "git-file-path";
    path.textContent = file.path;
    path.title = file.path;
    const flags = document.createElement("span");
    flags.textContent = file.truncated ? "truncated" : "";
    button.append(type, path, flags);
    button.addEventListener(
      "click",
      () => {
        const expanded = button.getAttribute("aria-expanded") === "true";
        elements.gitFileList.querySelectorAll("button").forEach(
          item => {
            item.setAttribute("aria-expanded", "false");
            item.removeAttribute("aria-current");
          }
        );
        if (expanded) {
          elements.gitDiffContent.textContent = "Diff collapsed.";
          return;
        }
        button.setAttribute("aria-expanded", "true");
        button.setAttribute("aria-current", "true");
        elements.gitDiffMetadata.textContent =
          `${file.path} · ${file.changeType}`
          + `${file.binary ? " · binary" : ""}`
          + `${file.truncated ? " · truncated" : ""}`;
        elements.gitDiffContent.textContent = file.content || "[empty diff]";
      }
    );
    elements.gitFileList.append(button);
  }
}

function openLatestChangeReview() {
  if (state.latestExecutionSessionId) {
    closeGitPanel();
    void openChangeReview(state.latestExecutionSessionId);
  }
}

function renderWorkspaceProfiles() {
  elements.workspaceProfileList.replaceChildren();

  for (const profile of state.workspaceProfiles?.profiles ?? []) {
    const entry = document.createElement("article");
    entry.className = `workspace-profile-entry${profile.active ? " active" : ""}`
      + `${profile.available ? "" : " unavailable"}`;
    entry.dataset.workspaceId = profile.id;
    const name = document.createElement("strong");
    name.textContent = `${profile.name}${profile.active ? " · active" : ""}`;
    const path = document.createElement("small");
    path.textContent = profile.path;
    const metadata = document.createElement("small");
    metadata.textContent = [
      profile.projectProfile?.projectTypes?.join(", ") || "profile not detected",
      profile.historyEnabled ? "history enabled" : "history disabled",
      profile.available ? null : profile.diagnostic || "unavailable"
    ].filter(Boolean).join(" · ");
    const actions = document.createElement("div");
    actions.className = "workspace-profile-actions";
    const activate = document.createElement("button");
    activate.type = "button";
    activate.className = "secondary-button";
    activate.textContent = profile.active ? "Active" : "Enable";
    activate.disabled = profile.active || !profile.available;
    activate.addEventListener("click", () => activateWorkspace(profile.id));
    const rename = document.createElement("button");
    rename.type = "button";
    rename.className = "secondary-button";
    rename.textContent = "Rename";
    rename.addEventListener("click", () => renameWorkspace(profile));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "secondary-button danger-button";
    remove.textContent = "Remove";
    remove.addEventListener("click", () => removeWorkspace(profile));
    actions.append(activate, rename, remove);
    entry.append(name, path, metadata, actions);
    elements.workspaceProfileList.append(entry);
  }
}

async function refreshWorkspaceState() {
  const [
    workspaceProfiles,
    workspace,
    projectProfile,
    validationProfiles,
    settings
  ] =
    await Promise.all([
      fetchJson("/api/workspaces"),
      fetchJson("/api/workspace"),
      fetchJson("/api/workspace/project-profile"),
      fetchJson("/api/workspace/validation-profile"),
      fetchJson("/api/settings")
    ]);
  state.workspaceProfiles = workspaceProfiles;
  state.workspace = workspace;
  state.projectProfile = projectProfile;
  state.validationProfiles = validationProfiles;
  state.settings = settings;
  renderWorkspace();
  renderWorkspaceProfiles();
  renderProjectProfile();
  renderValidationProfile();
  await refreshSupervisionRuns();
  await refreshSessions();
  await refreshGit();
}

async function refreshSupervisionRuns() {
  if (!activeWorkspaceProfile()) {
    state.supervisionRuns = [];
    renderSupervisionRecovery();
    return;
  }

  try {
    const response = await fetchJson("/api/supervision/runs");
    state.supervisionRuns = response.runs ?? [];
    elements.supervisionRecoveryStatus.textContent = "";
  } catch (error) {
    state.supervisionRuns = [];
    elements.supervisionRecoveryStatus.textContent = error.message;
  }

  renderSupervisionRecovery();
}

function renderSupervisionRecovery() {
  const recoverable = state.supervisionRuns.filter(run =>
    run.state === "interrupted-recoverable" || run.state === "awaiting-user");
  elements.supervisionRecovery.hidden = recoverable.length === 0;
  elements.supervisionRecoveryList.replaceChildren();

  for (const run of recoverable) {
    const card = document.createElement("article");
    card.className = "supervision-recovery-card";
    card.dataset.runId = run.runId;

    const objective = document.createElement("strong");
    objective.textContent = run.objective;
    objective.title = run.objective;

    const route = document.createElement("small");
    route.textContent = `${run.route.model} × ${benchmarkHarnessLabel(run.route.harness)}`;

    const progress = document.createElement("small");
    progress.textContent = `${run.runtime?.completedItems ?? 0}/${run.runtime?.totalItems ?? 0} items · ${run.resumePolicy}`;

    const reason = document.createElement("p");
    reason.textContent = run.waitReason ?? "The prior Host process stopped before completion.";
    if (run.waitCode) {
      reason.title = run.waitCode;
    }

    const actions = document.createElement("div");
    actions.className = "supervision-recovery-actions";
    const resume = document.createElement("button");
    resume.type = "button";
    resume.className = "secondary-button";
    resume.textContent = "Resume";
    resume.addEventListener("click", () => resumeSupervisionRun(run));
    const discard = document.createElement("button");
    discard.type = "button";
    discard.className = "secondary-button";
    discard.textContent = "Discard";
    discard.addEventListener("click", () => discardSupervisionRun(run));
    actions.append(resume, discard);
    card.append(objective, route, progress, reason, actions);
    elements.supervisionRecoveryList.append(card);
  }
}

async function resumeSupervisionRun(run) {
  elements.supervisionRecoveryStatus.textContent = "Reconciling durable state…";
  try {
    await fetchJson(
      `/api/supervision/runs/${encodeURIComponent(run.runId)}/resume`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          history: state.history,
          images: state.attachments
        })
      }
    );
    showToast("Supervised execution resumed from reconciled Host state.", "success");
  } catch (error) {
    showToast(error.message);
  }
  await refreshSupervisionRuns();
}

async function discardSupervisionRun(run) {
  const confirmed = await showAppConfirm(
    "Discard this durable supervision recovery state? Workspace files will be preserved.",
    {
      title: "Discard supervised recovery?",
      confirmLabel: "Discard",
      danger: true
    }
  );
  if (!confirmed) {
    return;
  }

  elements.supervisionRecoveryStatus.textContent = "Discarding recovery state…";
  try {
    await fetchJson(
      `/api/supervision/runs/${encodeURIComponent(run.runId)}?confirmed=true`,
      { method: "DELETE" }
    );
    showToast("Durable supervision recovery state discarded.", "success");
  } catch (error) {
    showToast(error.message);
  }
  await refreshSupervisionRuns();
}

async function activateWorkspace(id) {
  await requestConversationTransition(
    async () =>
    {
      elements.workspaceSaveStatus.textContent = "Activating…";

      try {
        await fetchJson(
          `/api/workspaces/${encodeURIComponent(id)}/activate`,
          {
            method: "POST"
          }
        );
        await resetConversationForWorkspaceChange();
        await refreshWorkspaceState();
        elements.workspaceSaveStatus.textContent =
          "Workspace activated. Chat mode and manual approval restored.";
      } catch (error) {
        elements.workspaceSaveStatus.textContent = error.message;
      }
    }
  );
}

async function renameWorkspace(profile) {
  const name = (await showAppPrompt("Enter the new workspace name.", {
    title: "Rename workspace",
    inputLabel: "Workspace name",
    inputValue: profile.name,
    confirmLabel: "Rename"
  }))?.trim();

  if (!name) {
    return;
  }

  try {
    await fetchJson(
      `/api/workspaces/${encodeURIComponent(profile.id)}/name`,
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({ name })
      }
    );
    await refreshWorkspaceState();
    setPersistenceStatus(
      enabled
        ? hasMeaningfulConversation()
          ? "Unsaved"
          : "Saved locally"
        : "History disabled"
    );
  } catch (error) {
    elements.workspaceSaveStatus.textContent = error.message;
  }
}

async function removeWorkspace(profile) {
  if (!await showAppConfirm(
    `Remove "${profile.name}" and its local Agentic Router history? `
      + "The actual folder and project files will not be deleted.",
    { title: "Remove workspace?", confirmLabel: "Remove", danger: true }
  )) {
    return;
  }

  try {
    await fetchJson(
      `/api/workspaces/${encodeURIComponent(profile.id)}?confirmed=true`,
      {
        method: "DELETE"
      }
    );

    if (profile.active) {
      await resetConversationForWorkspaceChange();
    }

    await refreshWorkspaceState();
  } catch (error) {
    elements.workspaceSaveStatus.textContent = error.message;
  }
}

async function changeWorkspaceHistory(event) {
  const active = activeWorkspaceProfile();

  if (!active) {
    event.currentTarget.checked = false;
    return;
  }

  const enabled = event.currentTarget.checked;

  if (
    enabled
    && !await showAppConfirm(
      "Enable local history for this workspace? The content will not be encrypted "
        + "by Agentic Router v0.9.12.",
      { title: "Enable local history?", confirmLabel: "Enable" }
    )
  ) {
    event.currentTarget.checked = false;
    return;
  }

  try {
    await fetchJson(
      `/api/workspaces/${encodeURIComponent(active.id)}/history`,
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({ enabled })
      }
    );
    await refreshWorkspaceState();
  } catch (error) {
    event.currentTarget.checked = !enabled;
    elements.workspaceSaveStatus.textContent = error.message;
  }
}

async function resetConversationForWorkspaceChange() {
  await resetCloudImagePrivacy(state.browserSessionId);
  clearConversationUi();
  state.browserSessionId = createSessionId();
  state.conversationSessionId = null;
  state.latestExecutionSessionId = null;
  state.interactionMode = "chat";
  state.approvalPolicy = "auto";
  state.harness = "native";
  elements.modelSelector.value = "auto";
  elements.harnessSelector.value = "native";
  updateInteractionControls();
  updateHarnessControls();
  await ensureConversationIdentity();
  await refreshSelectedModelCapabilities();
}

function renderProjectProfile() {
  const profile = state.projectProfile;

  if (!profile || profile.status === "unavailable") {
    elements.projectProfileSummary.textContent =
      profile?.diagnostic ?? "Profile unavailable";
    elements.projectProfileDetails.replaceChildren();
    return;
  }

  elements.projectProfileSummary.textContent =
    `${profile.displayName} · ${profile.projectTypes.join(", ") || "no project markers"}`;
  elements.projectProfileDetails.replaceChildren();
  const repository = document.createElement("p");
  repository.textContent = profile.repository.isGitRepository
    ? `Git · ${profile.repository.branch ?? "detached"} · `
      + `${profile.repository.hasUncommittedChanges ? "existing changes" : "clean"}`
    : "Git not detected";
  const instructions = document.createElement("p");
  instructions.textContent =
    `${profile.instructionFiles.length} AGENTS.md file(s)`;
  const validation = document.createElement("p");
  validation.textContent =
    `Validation: ${profile.validationProfile?.name ?? "not configured"} `
    + `(${profile.validationProfile?.source ?? "none"})`;
  elements.projectProfileDetails.append(
    repository,
    instructions,
    validation
  );

  if (profile.diagnostic) {
    const diagnostic = document.createElement("p");
    diagnostic.className = "verification-warning";
    diagnostic.textContent = profile.diagnostic;
    elements.projectProfileDetails.append(diagnostic);
  }
}

async function refreshProjectProfile() {
  elements.refreshProjectProfile.disabled = true;
  elements.projectProfileSummary.textContent = "Refreshing…";

  try {
    state.projectProfile = await fetchJson(
      "/api/workspace/project-profile/refresh",
      {
        method: "POST"
      }
    );
    state.validationProfiles = await fetchJson(
      "/api/workspace/validation-profile"
    );
    renderProjectProfile();
    renderValidationProfile();
    await refreshGit();
  } catch (error) {
    elements.projectProfileSummary.textContent = error.message;
  } finally {
    elements.refreshProjectProfile.disabled = false;
  }
}

function renderValidationProfile(profile = state.validationProfiles?.active) {
  const detected = state.validationProfiles?.detected;
  elements.detectedValidationProfile.textContent = detected
    ? `Detected suggestion: ${detected.name} · ${detected.steps.length} step(s)`
    : "No validation suggestion was detected.";
  elements.validationProfileName.value = profile?.name ?? "";
  elements.validationSteps.replaceChildren();

  for (const step of profile?.steps ?? []) {
    addValidationStep(step);
  }

  updateValidationCommandPreview();
}

function addValidationStep(step = {}) {
  if (elements.validationSteps.children.length >= 8) {
    elements.validationProfileStatus.textContent = "The limit is 8 steps.";
    return;
  }

  const row = document.createElement("section");
  row.className = "validation-step-editor";
  row.innerHTML = `
    <div class="validation-step-grid">
      <label><span>ID</span><input data-field="id" maxlength="40"></label>
      <label><span>Label</span><input data-field="label" maxlength="100"></label>
      <label><span>Executable</span><input data-field="executable" maxlength="260"></label>
      <label class="validation-arguments">
        <span>Argumentos (array JSON)</span>
        <input data-field="arguments" spellcheck="false">
      </label>
      <label><span>Relative directory</span><input data-field="workingDirectory"></label>
      <label><span>Timeout (s)</span><input data-field="timeoutSeconds" type="number" min="1" max="120"></label>
      <label class="validation-required">
        <input data-field="required" type="checkbox">
        <span>Required</span>
      </label>
    </div>
    <div class="validation-step-buttons">
      <button class="secondary-button" data-action="up" type="button">↑</button>
      <button class="secondary-button" data-action="down" type="button">↓</button>
      <button class="secondary-button danger-button" data-action="remove" type="button">Remove</button>
    </div>
  `;
  row.querySelector('[data-field="id"]').value =
    step.id ?? `step-${elements.validationSteps.children.length + 1}`;
  row.querySelector('[data-field="label"]').value = step.label ?? "";
  row.querySelector('[data-field="executable"]').value = step.executable ?? "dotnet";
  row.querySelector('[data-field="arguments"]').value =
    JSON.stringify(step.arguments ?? []);
  row.querySelector('[data-field="workingDirectory"]').value =
    step.workingDirectory ?? ".";
  row.querySelector('[data-field="timeoutSeconds"]').value =
    step.timeoutSeconds ?? 60;
  row.querySelector('[data-field="required"]').checked =
    step.required ?? true;
  row.addEventListener("input", updateValidationCommandPreview);
  row.querySelector('[data-action="remove"]').addEventListener(
    "click",
    () => {
      row.remove();
      updateValidationCommandPreview();
    }
  );
  row.querySelector('[data-action="up"]').addEventListener(
    "click",
    () => {
      const previous = row.previousElementSibling;
      if (previous) {
        elements.validationSteps.insertBefore(row, previous);
        updateValidationCommandPreview();
      }
    }
  );
  row.querySelector('[data-action="down"]').addEventListener(
    "click",
    () => {
      const next = row.nextElementSibling;
      if (next) {
        elements.validationSteps.insertBefore(next, row);
        updateValidationCommandPreview();
      }
    }
  );
  elements.validationSteps.append(row);
  updateValidationCommandPreview();
}

function readValidationProfileEditor() {
  const steps = [...elements.validationSteps.children].map(row => {
    let args;
    try {
      args = JSON.parse(row.querySelector('[data-field="arguments"]').value);
    } catch {
      throw new Error("Each step's arguments must be a valid JSON array.");
    }

    if (!Array.isArray(args) || args.some(item => typeof item !== "string")) {
      throw new Error("Each step's arguments must be a JSON string array.");
    }

    return {
      id: row.querySelector('[data-field="id"]').value.trim(),
      label: row.querySelector('[data-field="label"]').value.trim(),
      executable: row.querySelector('[data-field="executable"]').value.trim(),
      arguments: args,
      workingDirectory:
        row.querySelector('[data-field="workingDirectory"]').value.trim(),
      timeoutSeconds: Number(
        row.querySelector('[data-field="timeoutSeconds"]').value
      ),
      required: row.querySelector('[data-field="required"]').checked
    };
  });
  return {
    name: elements.validationProfileName.value.trim(),
    source: "user",
    steps
  };
}

function updateValidationCommandPreview() {
  try {
    const profile = readValidationProfileEditor();
    elements.validationCommandPreview.textContent = profile.steps.length
      ? profile.steps.map(step =>
        `${step.executable} ${step.arguments.map(JSON.stringify).join(" ")}`
        + `\n  cwd: ${step.workingDirectory} · ${step.timeoutSeconds}s · `
        + `${step.required ? "required" : "optional"}`
      ).join("\n")
      : "No steps configured.";
  } catch (error) {
    elements.validationCommandPreview.textContent = error.message;
  }
}

function resetValidationProfile() {
  const detected = state.validationProfiles?.detected;
  if (!detected) {
    elements.validationProfileStatus.textContent =
      "No detected suggestion is available.";
    return;
  }

  renderValidationProfile(detected);
  elements.validationProfileStatus.textContent =
    "Suggestion loaded. Save to activate it.";
}

async function saveValidationProfile() {
  elements.validationProfileStatus.textContent = "Validating and saving…";

  try {
    const profile = readValidationProfileEditor();
    state.validationProfiles.active = await fetchJson(
      "/api/workspace/validation-profile",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify(profile)
      }
    );
    elements.validationProfileStatus.textContent = "Profile saved.";
    await refreshProjectProfile();
  } catch (error) {
    const fieldErrors = error.payload?.errors
      ? Object.values(error.payload.errors).flat().join(" ")
      : "";
    elements.validationProfileStatus.textContent =
      `${error.message} ${fieldErrors}`.trim();
  }
}

async function clearValidationProfile() {
  elements.validationProfileStatus.textContent = "Clearing…";

  try {
    state.validationProfiles = await fetchJson(
      "/api/workspace/validation-profile",
      {
        method: "DELETE"
      }
    );
    renderValidationProfile();
    elements.validationProfileStatus.textContent =
      "Active profile removed. Validation is not configured.";
    await refreshProjectProfile();
  } catch (error) {
    elements.validationProfileStatus.textContent = error.message;
  }
}

function openWorkspace() {
  elements.runtimeDetails.open = false;
  elements.workspaceSaveStatus.textContent = "";
  renderWorkspace();
  elements.savedWorkspacesSection.open = true;
  elements.localHistorySection.open = true;
  elements.projectProfileSection.open = false;
  elements.validationProfileSection.open = false;
  hideNewWorkspaceForm();
  elements.workspaceDialog.showModal();
  elements.addWorkspace.focus();
}

function closeWorkspace() {
  hideNewWorkspaceForm();
  elements.workspaceDialog.close();
}

function showNewWorkspaceForm() {
  elements.newWorkspaceSection.hidden = false;
  elements.newWorkspaceAccordion.open = true;
  elements.workspaceProfileName.value = "";
  elements.trustedWorkspacePath.value = "";
  elements.workspaceValidation.textContent = "Select a trusted folder.";
  elements.workspaceValidation.className = "workspace-validation";
  elements.workspaceSaveStatus.textContent = "";
  elements.newWorkspaceSection.scrollIntoView({
    block: "nearest"
  });
  elements.workspaceProfileName.focus();
}

function hideNewWorkspaceForm() {
  elements.newWorkspaceSection.hidden = true;
  elements.workspaceProfileName.value = "";
  elements.trustedWorkspacePath.value = "";
  renderWorkspace();
}

async function saveWorkspace(event) {
  event.preventDefault();
  if (
    state.workspaceSaving
    || elements.newWorkspaceSection.hidden
  ) {
    return;
  }
  const path = elements.trustedWorkspacePath.value.trim();
  const name = elements.workspaceProfileName.value.trim()
    || path.split(/[\\/]/).filter(Boolean).at(-1)
    || "Workspace";
  setWorkspaceSaving(
    true
  );
  elements.workspaceSaveStatus.textContent = "Validating…";

  try {
    const created = await fetchJson(
      "/api/workspaces",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          name,
          path
        })
      }
    );
    if (!created.active) {
      await fetchJson(
        `/api/workspaces/${encodeURIComponent(created.id)}/activate`,
        {
          method: "POST"
        }
      );
    }
    await resetConversationForWorkspaceChange();
    hideNewWorkspaceForm();
    await refreshWorkspaceState();
    elements.workspaceSaveStatus.textContent = "Workspace added and activated";
  } catch (error) {
    elements.workspaceValidation.textContent = error.message;
    elements.workspaceValidation.className = "workspace-validation invalid";
    elements.workspaceSaveStatus.textContent = "Could not save";
  } finally {
    setWorkspaceSaving(
      false
    );
  }
}

function setWorkspaceSaving(isSaving) {
  state.workspaceSaving = isSaving;
  elements.workspaceProfileName.disabled = isSaving;
  elements.trustedWorkspacePath.disabled = isSaving;
  elements.pickWorkspace.disabled = isSaving;
  elements.workspaceSubmit.disabled = isSaving;
  elements.addWorkspace.disabled = isSaving;
}

async function pickWorkspace() {
  elements.pickWorkspace.disabled = true;
  elements.workspaceSaveStatus.textContent = "Abrindo seletor…";

  try {
    const result = await fetchJson(
      "/api/workspace/pick",
      {
        method: "POST"
      }
    );

    if (result.selected && result.path) {
      elements.trustedWorkspacePath.value = result.path;
      elements.workspaceValidation.textContent =
        "Folder selected. Click Save to trust it.";
      elements.workspaceValidation.className = "workspace-validation valid";
      elements.workspaceSaveStatus.textContent = "";
    } else if (result.cancelled) {
      elements.workspaceSaveStatus.textContent = "Selection canceled";
    } else {
      elements.workspaceValidation.textContent =
        result.error ?? "Could not open the folder picker.";
      elements.workspaceValidation.className = "workspace-validation invalid";
      elements.workspaceSaveStatus.textContent = "";
    }
  } catch (error) {
    elements.workspaceValidation.textContent = error.message;
    elements.workspaceValidation.className = "workspace-validation invalid";
    elements.workspaceSaveStatus.textContent = "";
  } finally {
    elements.pickWorkspace.disabled = false;
  }
}

async function clearWorkspace() {
  const active = activeWorkspaceProfile();

  if (active) {
    await removeWorkspace(active);
  }
}

async function refreshSessions() {
  if (!activeWorkspaceProfile()) {
    state.sessions = null;
    state.projectSessions = [];
    renderSessionHistory();
    return;
  }

  try {
    const [sessions, projects] = await Promise.all([
      fetchJson("/api/sessions"),
      fetchProjectConversations("")
    ]);
    state.sessions = sessions;
    state.projectSessions = projects.results;
  } catch {
    state.sessions = null;
    state.projectSessions = [];
  }

  renderSessionHistory();
}

function renderSessionHistory() {
  const usage = state.sessions?.usage;
  elements.historyUsage.textContent = usage
    ? `${usage.sessionCount} session(s) · ${formatBytes(usage.storageBytes)} · `
      + `${usage.enabled ? "history enabled" : "history disabled"}`
      + `${usage.oldestSessionAt
        ? ` · oldest ${new Date(usage.oldestSessionAt).toLocaleDateString(window.AgenticRouterI18n.locale)}`
        : ""}`
      + `${usage.newestSessionAt
        ? ` · newest ${new Date(usage.newestSessionAt).toLocaleDateString(window.AgenticRouterI18n.locale)}`
        : ""}`
    : "No stored sessions.";
  elements.enableSessionHistory.hidden = Boolean(usage?.enabled);
  renderPersistenceStatus();
  renderProjectSidebar();
  renderSettingsSummaries();
}

async function fetchProjectConversations(query) {
  return await fetchJson(
    "/api/sessions/search",
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        query: query || null,
        allWorkspaces: true,
        archived: false,
        limit: 100
      })
    }
  );
}

function renderProjectSidebar() {
  const projects = state.workspaceProfiles?.profiles ?? [];
  const searching = false;
  const sessionsByProject = new Map();
  for (const session of state.projectSessions) {
    const grouped = sessionsByProject.get(session.workspaceId) ?? [];
    grouped.push(session);
    sessionsByProject.set(session.workspaceId, grouped);
  }
  closeProjectMenu();
  elements.projectList.replaceChildren();

  if (projects.length === 0) {
    const empty = document.createElement("p");
    empty.className = "project-empty sidebar-expanded-only";
    empty.textContent = "No projects configured.";
    elements.projectList.append(empty);
    return;
  }

  for (const project of projects) {
    const projectSessions = sessionsByProject.get(project.id) ?? [];
    if (searching && projectSessions.length === 0) {
      continue;
    }

    const details = document.createElement("details");
    details.className = `project-accordion${project.active ? " active" : ""}`
      + `${project.available ? "" : " unavailable"}`;
    details.dataset.workspaceId = project.id;
    details.open = searching
      || state.expandedProjectIds.has(project.id)
      || (state.expandedProjectIds.size === 0 && project.active);
    const summary = document.createElement("summary");
    summary.title = project.path;
    const icon = document.createElement("span");
    icon.className = "project-icon";
    icon.setAttribute("aria-hidden", "true");
    const name = document.createElement("strong");
    name.className = "sidebar-expanded-only";
    name.textContent = project.name;
    const active = document.createElement("span");
    active.className = "project-active-marker sidebar-expanded-only";
    active.title = "Active project";
    active.setAttribute("aria-label", "Active project");
    active.hidden = !project.active;
    summary.append(icon, name, active);

    const menu = document.createElement("button");
    menu.type = "button";
    menu.className = "project-menu-button sidebar-expanded-only";
    menu.textContent = "…";
    menu.setAttribute("aria-label", `Details for ${project.name}`);
    menu.setAttribute("aria-haspopup", "dialog");
    menu.setAttribute("aria-expanded", "false");

    const body = document.createElement("div");
    body.className = "project-body sidebar-expanded-only";
    const actions = document.createElement("div");
    actions.className = "project-actions";
    if (!project.active) {
      const activate = document.createElement("button");
      activate.type = "button";
      activate.className = "project-activate";
      activate.textContent = "Use project";
      activate.disabled = !project.available;
      activate.addEventListener("click", () => activateWorkspace(project.id));
      actions.append(activate);
    }
    const list = document.createElement("div");
    list.className = "project-conversation-list";
    list.setAttribute("aria-label", `Conversations in ${project.name}`);
    if (project.active) {
      const pinned = document.createElement("section");
      pinned.id = "pinned-session-section";
      pinned.className = "pinned-session-section";
      pinned.hidden = (state.sessions?.pinned?.length ?? 0) === 0;
      const pinnedTitle = document.createElement("h2");
      pinnedTitle.textContent = "Pinned";
      const pinnedList = document.createElement("div");
      pinnedList.id = "pinned-sessions";
      pinnedList.className = "project-conversation-list";
      for (const session of state.sessions?.pinned ?? []) {
        pinnedList.append(createProjectSessionEntry(session));
      }
      pinned.append(pinnedTitle, pinnedList);
      list.append(pinned);
      const recent = document.createElement("div");
      recent.id = "recent-sessions";
      recent.className = "project-conversation-list";
      for (const session of state.sessions?.recent ?? []) {
        recent.append(createProjectSessionEntry(session));
      }
      list.append(recent);
      const archived = document.createElement("details");
      archived.id = "archived-session-section";
      archived.className = "archived-session-section";
      archived.hidden = (state.sessions?.archived?.length ?? 0) === 0;
      const archivedSummary = document.createElement("summary");
      archivedSummary.textContent = "Archived";
      const archivedList = document.createElement("div");
      archivedList.id = "archived-sessions";
      archivedList.className = "project-conversation-list";
      for (const session of state.sessions?.archived ?? []) {
        archivedList.append(createProjectSessionEntry(session));
      }
      archived.append(archivedSummary, archivedList);
      list.append(archived);
    } else {
      for (const session of projectSessions) {
        list.append(createProjectSessionEntry(session));
      }
    }
    const activeCount = project.active
      ? (state.sessions?.pinned?.length ?? 0) + (state.sessions?.recent?.length ?? 0)
      : projectSessions.length;
    if (activeCount === 0) {
      const empty = document.createElement("p");
      empty.className = "project-conversations-empty";
      empty.textContent = project.historyEnabled
        ? "No saved conversations."
        : "History disabled.";
      list.append(empty);
    }
    menu.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      openProjectMenu(project, activeCount, menu);
    });
    body.append(actions, list);
    details.append(summary, menu, body);
    details.addEventListener("toggle", () => {
      if (searching) {
        return;
      }
      if (details.open) {
        state.expandedProjectIds.add(project.id);
      } else {
        state.expandedProjectIds.delete(project.id);
      }
      persistExpandedProjects();
    });
    elements.projectList.append(details);
  }
}

function openProjectMenu(project, conversationCount, anchor) {
  if (
    projectMenuAnchor === anchor
    && !elements.projectMenuPopover.hidden
  ) {
    closeProjectMenu();
    return;
  }

  closeProjectMenu();
  projectMenuAnchor = anchor;
  anchor.setAttribute("aria-expanded", "true");
  elements.projectMenuPopover.dataset.workspaceId = project.id;
  elements.projectMenuTitle.textContent = project.name;
  elements.projectMenuCount.textContent = `${conversationCount} ${conversationCount === 1 ? "conversation" : "conversations"}`;
  elements.projectMenuPath.textContent = project.path;

  const repository = project.active && state.git?.state === "available"
    ? state.git.repository
    : null;
  elements.projectMenuGitRow.hidden = !repository;
  elements.projectMenuGit.textContent = repository
    ? `Git · ${repository.detachedHead ? "detached" : repository.branch ?? "unborn"}`
    : "";
  elements.projectMenuPopover.hidden = false;
  positionProjectMenu(anchor);
}

function positionProjectMenu(anchor) {
  const margin = 12;
  const gap = 10;
  const anchorRect = anchor.getBoundingClientRect();
  const sidebarRect = elements.sidebar.getBoundingClientRect();
  const popoverRect = elements.projectMenuPopover.getBoundingClientRect();
  let left = Math.max(
    anchorRect.right + gap,
    sidebarRect.right + gap
  );

  if (left + popoverRect.width > window.innerWidth - margin) {
    left = anchorRect.left - popoverRect.width - gap;
  }

  const top = Math.min(
    Math.max(margin, anchorRect.top - 8),
    Math.max(margin, window.innerHeight - popoverRect.height - margin)
  );
  elements.projectMenuPopover.style.left = `${Math.max(margin, left)}px`;
  elements.projectMenuPopover.style.top = `${top}px`;
}

function closeProjectMenu() {
  projectMenuAnchor?.setAttribute("aria-expanded", "false");
  projectMenuAnchor = null;
  if (!elements.projectMenuPopover) {
    return;
  }
  elements.projectMenuPopover.hidden = true;
  delete elements.projectMenuPopover.dataset.workspaceId;
}

function editSelectedProject() {
  closeProjectMenu();
  openWorkspace();
}

function createProjectSessionEntry(session) {
  const entry = document.createElement("article");
  const current = state.conversationSessionId === session.id;
  entry.className = `session-entry${current ? " current" : ""}`;
  entry.dataset.sessionId = session.id;
  entry.setAttribute(
    "aria-current",
    current ? "true" : "false"
  );
  const resume = document.createElement("button");
  resume.type = "button";
  resume.className = "session-entry-content";
  resume.setAttribute("aria-label", `Resume ${session.title}`);
  const title = document.createElement("strong");
  title.textContent = session.title;
  const metadata = document.createElement("small");
  metadata.textContent = `${session.pinned ? "Pinned · " : ""}`
    + new Date(session.updatedAt).toLocaleDateString(window.AgenticRouterI18n.locale);
  resume.append(title, metadata);
  resume.addEventListener("click", () => resumeSession(session.id, session.workspaceId));
  const details = document.createElement("button");
  details.type = "button";
  details.className = "session-details-button";
  details.textContent = "…";
  details.setAttribute("aria-label", `Details for ${session.title}`);
  details.addEventListener("click", () => openSessionDetails(session));
  entry.append(resume, details);
  return entry;
}

function persistExpandedProjects() {
  try {
    localStorage.setItem(
      "agentic-router.expanded-projects",
      JSON.stringify([...state.expandedProjectIds])
    );
  } catch {
    // Project expansion still works when browser storage is unavailable.
  }
}

async function openSessionDetails(session) {
  state.detailsSession = session;
  renderSessionDetails(session);
  renderSessionDetailsSummary(null, true);
  elements.sessionDetailsDialog.showModal();
  elements.resumeSessionDetails.focus();

  try {
    const summary = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary`
    );
    if (state.detailsSession?.id !== session.id) {
      return;
    }

    renderSessionDetailsSummary(summary?.content ?? null, false);
  } catch (error) {
    elements.sessionDetailsSummary.replaceChildren();
    const message = document.createElement("p");
    message.className = "runtime-note";
    message.textContent = error.message;
    elements.sessionDetailsSummary.append(message);
  }
}

function renderSessionDetails(session) {
  elements.sessionDetailsTitle.textContent = session.title;
  elements.sessionDetailsMetadata.textContent = [
    new Date(session.updatedAt).toLocaleString(window.AgenticRouterI18n.locale),
    session.lastInteractionMode === "execute" ? "Execute" : "Chat",
    session.selectedModel
  ].filter(Boolean).join(" · ");
  elements.sessionDetailsState.textContent = [
    state.conversationSessionId === session.id ? "Current conversation" : null,
    session.pinned ? "Pinned" : null,
    session.hasSummary ? "Has summary" : "No summary",
    session.interrupted ? "Interrupted" : null,
    session.archived ? "Archived" : null
  ].filter(Boolean).join(" · ");
  elements.sessionDetailsPin.textContent = session.pinned
    ? "Unpin"
    : "Pin";
  elements.sessionDetailsArchive.hidden = session.archived;
  elements.sessionDetailsMarkdown.href =
    `/api/sessions/${encodeURIComponent(session.id)}/export/markdown`
    + "?includeSummary=true&includeModelMetadata=true";
  elements.sessionDetailsJson.href =
    `/api/sessions/${encodeURIComponent(session.id)}/export`;
  elements.sessionDetailsStatus.textContent = "";
}

function renderSessionDetailsSummary(content, loading) {
  elements.sessionDetailsSummary.replaceChildren();

  if (loading) {
    const message = document.createElement("p");
    message.className = "runtime-note";
    message.textContent = "Loading summary…";
    elements.sessionDetailsSummary.append(message);
    return;
  }

  if (!content) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent =
      "No summary was created. The conversation can be resumed normally without it.";
    elements.sessionDetailsSummary.append(empty);
    return;
  }

  const fields = [
    ["Objective", content.objective],
    ["Decisions", content.decisions],
    ["Changed files", content.filesChanged],
    ["Commands and validation", content.commandsAndValidation],
    ["Unresolved issues", content.unresolvedIssues],
    ["Next step", content.nextSuggestedStep]
  ];

  for (const [label, value] of fields) {
    const values = Array.isArray(value)
      ? value.filter(Boolean)
      : value
        ? [value]
        : [];
    if (values.length === 0) {
      continue;
    }

    const item = document.createElement("section");
    item.className = "session-summary-fact";
    const heading = document.createElement("h4");
    heading.textContent = label;
    item.append(heading);
    if (Array.isArray(value)) {
      const list = document.createElement("ul");
      for (const text of values) {
        const entry = document.createElement("li");
        entry.textContent = text;
        list.append(entry);
      }
      item.append(list);
    } else {
      const text = document.createElement("p");
      text.textContent = values[0];
      item.append(text);
    }
    elements.sessionDetailsSummary.append(item);
  }
}

function closeSessionDetails() {
  state.detailsSession = null;
  elements.sessionDetailsDialog.close();
}

function findSession(id) {
  return [
    ...(state.sessions?.pinned ?? []),
    ...(state.sessions?.recent ?? []),
    ...(state.sessions?.archived ?? [])
  ].find(session => session.id === id) ?? null;
}

function findAttachableSupervisionRun(conversationSessionId) {
  return state.supervisionRuns
    .filter(run =>
      run.conversationSessionId === conversationSessionId
      && (run.state === "running" || run.state === "completed")
    )
    .sort((left, right) =>
      new Date(left.createdAt).getTime() - new Date(right.createdAt).getTime()
    )
    .at(-1) ?? null;
}

function refreshSelectedSessionDetails(id) {
  const session = findSession(id);
  if (!session) {
    closeSessionDetails();
    return;
  }

  state.detailsSession = session;
  renderSessionDetails(session);
}

async function resumeSelectedSession() {
  const session = state.detailsSession;
  if (!session) {
    return;
  }

  closeSessionDetails();
  await resumeSession(session.id, session.workspaceId);
}

async function toggleSelectedSessionPin() {
  const session = state.detailsSession;
  if (!session) {
    return;
  }

  await setSessionPinned(session);
  refreshSelectedSessionDetails(session.id);
}

async function renameSelectedSession() {
  const session = state.detailsSession;
  if (!session) {
    return;
  }

  await renameSession(session);
  refreshSelectedSessionDetails(session.id);
}

async function duplicateSelectedSession() {
  const session = state.detailsSession;
  if (!session) {
    return;
  }

  const duplicate = await duplicateSession(session);
  elements.sessionDetailsStatus.textContent = duplicate
    ? `Copy created: ${duplicate.session.title}`
    : "The conversation was not duplicated.";
}

async function archiveSelectedSession() {
  const session = state.detailsSession;
  if (!session) {
    return;
  }

  await archiveSession(session.id);
  closeSessionDetails();
}

async function deleteSelectedSession() {
  const session = state.detailsSession;
  if (!session) {
    return;
  }

  if (await deleteSession(session)) {
    closeSessionDetails();
  }
}

async function editSelectedSessionSummary() {
  const session = state.detailsSession;
  if (!session) {
    return;
  }

  closeSessionDetails();
  await openSessionSummary(session);
}

async function resumeSession(id, workspaceId = activeWorkspaceProfile()?.id) {
  await requestConversationTransition(
    async () =>
    {
      if (!await showAppConfirm(
        "Resume this conversation? Chat mode, manual approval, and an unlocked model will be restored.",
        { title: "Resume conversation?", confirmLabel: "Resume" }
      )) {
        return;
      }
      const nextBrowserSessionId = createSessionId();

      try {
        if (workspaceId && workspaceId !== activeWorkspaceProfile()?.id) {
          await fetchJson(
            `/api/workspaces/${encodeURIComponent(workspaceId)}/activate`,
            {
              method: "POST"
            }
          );
          await resetConversationForWorkspaceChange();
          await refreshWorkspaceState();
        }
        const session = await fetchJson(
          `/api/sessions/${encodeURIComponent(id)}/resume`,
          {
            method: "POST",
            headers: {
              "Content-Type": "application/json"
            },
            body: JSON.stringify({
              browserSessionId: nextBrowserSessionId
            })
          }
        );
        await refreshSupervisionRuns();
        const supervisionRun = session.interrupted
          ? findAttachableSupervisionRun(session.id)
          : null;
        await resetCloudImagePrivacy(state.browserSessionId);
        clearConversationUi();
        state.browserSessionId = nextBrowserSessionId;
        state.conversationSessionId = session.id;
        state.history = session.messages.map(
          message => ({
            role: message.role,
            content: message.content
          })
        );
        state.conversationState = supervisionRun
          ? "running"
          : session.interrupted
          ? "interrupted"
          : session.state;
        state.interactionMode = supervisionRun ? "execute" : "chat";
        state.approvalPolicy = supervisionRun?.approvalPolicy ?? "auto";
        state.harness = supervisionRun?.route?.harness ?? "native";
        const resumedModel = supervisionRun?.route?.model ?? session.selectedModel;
        elements.modelSelector.value = resumedModel
          && state.models.some(model => model.name === resumedModel)
          ? resumedModel
          : "auto";
        renderRestoredConversation(
          session,
          { suppressInterrupted: Boolean(supervisionRun) }
        );
        await refreshSelectedModelCapabilities();
        setPersistenceStatus(
          supervisionRun
            ? "Reconnecting"
            : session.interrupted
            ? "Interrupted"
            : "Saved locally"
        );
        updateInteractionControls();
        elements.harnessSelector.value = state.harness;
        updateHarnessControls();
        updateComposerStatus();
        elements.workspaceDialog.close();
        await refreshSessions();
        await refreshGit();
        if (supervisionRun) {
          void attachSupervisionConversation(supervisionRun);
        }
      } catch (error) {
        setPersistenceStatus(
          "Save failed"
        );
        elements.workspaceSaveStatus.textContent =
          `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
      }
    }
  );
}

function renderRestoredConversation(session, options = {}) {
  elements.emptyState?.remove();

  session.messages.forEach(
    (message, index) => {
      if (message.role === "user") {
        appendUserMessage(
          message.content,
          index
        );
      } else if (message.role === "assistant") {
        const assistant = appendAssistantMessage();
        cancelAnimationFrame(assistant.clockFrame);
        assistant.progress.hidden = true;
        assistant.details.open = false;
        assistant.summary.textContent = "History restored";
        assistant.answer.classList.remove("pending");
        assistant.answer.textContent = message.content;
        assistant.rawAnswer = message.content;
        assistant.copyButton.disabled = false;
      }
    }
  );

  if (session.interrupted && !options.suppressInterrupted) {
    const warning = document.createElement("article");
    warning.className = "message assistant";
    warning.textContent =
      "The previous execution was interrupted. Completed actions were preserved. "
      + "No pending process or approval was resumed. Continue with a new turn.";
    elements.messages.append(warning);
  }

  if (session.contextTruncated) {
    const notice = document.createElement("p");
    notice.className = "workspace-note";
    notice.textContent =
      "Older messages remain visible but will be omitted from the model's next context.";
    elements.messages.append(notice);
  }

  if (session.executionReviews.length > 0) {
    const review = session.executionReviews.at(-1);
    state.latestExecutionSessionId = review.summary.id;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button";
    button.textContent = "Review completed changes";
    button.addEventListener(
      "click",
      () => {
        state.activeReview = review;
        renderChangeReview(review);
        elements.changeReviewDialog.showModal();
      }
    );
    elements.messages.append(button);
  }
}

async function attachSupervisionConversation(run) {
  const conversationVersion = state.conversationVersion;
  const controller = new AbortController();
  const assistant = appendAssistantMessage({ modelSelectionOrigin: "user" });
  state.requestController = controller;
  state.activeAssistant = assistant;
  state.activeHarness = run.route?.harness ?? state.harness;
  setStreamingState(true);
  updateComposerStatus();

  try {
    const response = await fetch(
      `/api/chat/supervision/${encodeURIComponent(run.runId)}/stream?afterSequence=0`,
      { signal: controller.signal }
    );
    if (!response.ok || !response.body) {
      throw new Error(`HTTP ${response.status}`);
    }

    const outcome = await consumeEventStream(response.body, assistant);
    if (outcome.completed && state.conversationVersion === conversationVersion) {
      state.history.push({ role: "assistant", content: outcome.answer });
      state.conversationState = "completed";
      setPersistenceStatus("Saved locally");
      await refreshSessions();
      await refreshGit();
    }
  } catch (error) {
    if (error.name !== "AbortError") {
      state.conversationState = "failed";
      addActivity(
        assistant,
        {
          type: "client.error",
          message: error.message,
          elapsedMilliseconds: elapsedSince(assistant)
        },
        true
      );
      assistant.answer.textContent ||= "Could not reattach to the supervised run.";
      assistant.answer.classList.add("error");
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Failed", true);
    }
  } finally {
    if (state.requestController === controller) {
      state.requestController = null;
      state.activeAssistant = null;
      setStreamingState(false);
      await refreshRuntimeStatus();
      scheduleRuntimeRefresh();
    }
    renderMessageQueue();
    updateComposerStatus();
  }
}

async function renameSession(session) {
  const title = (await showAppPrompt("Enter the new conversation title.", {
    title: "Rename conversation",
    inputLabel: "Title",
    inputValue: session.title,
    confirmLabel: "Rename"
  }))?.trim();

  if (!title) {
    return;
  }

  await fetchJson(
    `/api/sessions/${encodeURIComponent(session.id)}/name`,
    {
      method: "PUT",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ title })
    }
  );
  await refreshSessions();
}

async function archiveSession(id) {
  await fetchJson(
    `/api/sessions/${encodeURIComponent(id)}/archive`,
    {
      method: "POST"
    }
  );
  await refreshSessions();
}

async function setSessionPinned(session) {
  await fetchJson(
    `/api/sessions/${encodeURIComponent(session.id)}/pin`,
    {
      method: "PUT",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        pinned: !session.pinned
      })
    }
  );
  await refreshSessions();
}

async function duplicateSession(session) {
  try {
    const duplicate = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/duplicate`,
      {
        method: "POST"
      }
    );
    elements.sessionSearchStatus.textContent =
      `Copy created: ${duplicate.session.title}`;
    await refreshSessions();
    return duplicate;
  } catch (error) {
    elements.sessionSearchStatus.textContent = error.message;
    return null;
  }
}

function openSessionSearch() {
  elements.sessionSearchStatus.textContent =
    "Search uses only local session files.";
  elements.sessionSearchResults.replaceChildren();
  elements.sessionSearchAllWorkspaces.checked = true;
  elements.sessionSearchDialog.showModal();
  elements.sessionSearchQuery.focus();
}

function closeSessionSearch() {
  state.sessionSearchController?.abort();
  state.sessionSearchController = null;
  elements.sessionSearchDialog.close();
  elements.openSessionSearch.focus();
}

async function runSessionSearch(event) {
  event.preventDefault();
  state.sessionSearchController?.abort();
  const controller = new AbortController();
  state.sessionSearchController = controller;
  elements.runSessionSearch.disabled = true;
  elements.sessionSearchStatus.textContent = "Searching local records…";
  const stateFilter = elements.sessionSearchState.value;

  try {
    const result = await fetchJson(
      "/api/sessions/search",
      {
        method: "POST",
        signal: controller.signal,
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          query: elements.sessionSearchQuery.value.trim() || null,
          allWorkspaces: elements.sessionSearchAllWorkspaces.checked,
          model: elements.sessionSearchModel.value.trim() || null,
          fileChanged: elements.sessionSearchFile.value.trim() || null,
          validationResult:
            elements.sessionSearchValidation.value.trim() || null,
          from: searchDateValue(elements.sessionSearchFrom.value, false),
          to: searchDateValue(elements.sessionSearchTo.value, true),
          archived: stateFilter === "active"
            ? false
            : stateFilter === "archived"
              ? true
              : null,
          pinned: stateFilter === "pinned" ? true : null,
          limit: 50
        })
      }
    );
    renderSessionSearchResults(result);
    elements.sessionSearchStatus.textContent =
      `${result.results.length} result(s) · ${result.scannedSessions} session(s) examined`
      + `${result.truncated ? " · bounded result" : ""}`
      + ` · ${result.workspaceScope === "active-workspace"
        ? "active workspace"
        : "all workspaces"}`;
  } catch (error) {
    elements.sessionSearchStatus.textContent = error.name === "AbortError"
      ? "Search canceled."
      : error.message;
  } finally {
    if (state.sessionSearchController === controller) {
      state.sessionSearchController = null;
    }

    elements.runSessionSearch.disabled = false;
  }
}

function searchDateValue(value, endOfDay) {
  if (!value) {
    return null;
  }

  return new Date(
    `${value}T${endOfDay ? "23:59:59.999" : "00:00:00.000"}`
  ).toISOString();
}

function renderSessionSearchResults(response) {
  elements.sessionSearchResults.replaceChildren();

  for (const result of response.results) {
    const entry = document.createElement("article");
    entry.className = "session-search-result";
    const title = document.createElement("strong");
    title.textContent = result.title;
    const metadata = document.createElement("small");
    metadata.textContent = [
      result.workspaceName,
      new Date(result.updatedAt).toLocaleString(window.AgenticRouterI18n.locale),
      result.model,
      result.pinned ? "pinned" : null,
      result.archived ? "archived" : null
    ].filter(Boolean).join(" · ");
    const field = document.createElement("small");
    field.textContent = `Match: ${result.matchField}`;
    const snippet = document.createElement("p");
    appendHighlightedSnippet(
      snippet,
      result.snippet,
      result.highlights
    );
    const open = document.createElement("button");
    open.type = "button";
    open.className = "secondary-button";
    open.textContent = "Resume safely";
    open.addEventListener(
      "click",
      async () => {
        closeSessionSearch();
        await resumeSession(result.id, result.workspaceId);
      }
    );
    entry.append(title, metadata, field, snippet, open);
    elements.sessionSearchResults.append(entry);
  }

  if (response.results.length === 0) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent = "No conversation matches the filters.";
    elements.sessionSearchResults.append(empty);
  }
}

function appendHighlightedSnippet(container, value, ranges) {
  let offset = 0;

  for (const range of ranges ?? []) {
    const start = Math.max(
      offset,
      range.start
    );
    const end = Math.min(
      value.length,
      start + range.length
    );

    if (start > offset) {
      container.append(
        document.createTextNode(
          value.slice(offset, start)
        )
      );
    }

    const mark = document.createElement("mark");
    mark.textContent = value.slice(start, end);
    container.append(mark);
    offset = end;
  }

  if (offset < value.length) {
    container.append(
      document.createTextNode(
        value.slice(offset)
      )
    );
  }
}

async function openSessionSummary(session) {
  state.summarySession = session;
  state.summaryEstimate = null;
  elements.sessionSummarySessionTitle.textContent = session.title;
  replaceOptions(
    elements.sessionSummaryModel,
    modelOptions(),
    session.selectedModel
      && state.models.some(model => model.name === session.selectedModel)
      ? session.selectedModel
      : state.settings.defaultModel
  );
  elements.sessionSummaryStatus.textContent =
    "The summary is separate from the original messages.";
  elements.sessionSummaryDialog.showModal();

  try {
    const summary = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary`
    );
    fillSessionSummary(summary?.content ?? null);
    elements.deleteSessionSummary.disabled = !summary;
  } catch (error) {
    fillSessionSummary(null);
    elements.sessionSummaryStatus.textContent = error.message;
  }

  await refreshSessionSummaryEstimate();
  elements.sessionSummaryObjective.focus();
}

function closeSessionSummary() {
  state.summarySession = null;
  state.summaryEstimate = null;
  elements.sessionSummaryDialog.close();
}

async function refreshSessionSummaryEstimate() {
  const session = state.summarySession;
  const model = elements.sessionSummaryModel.value;

  if (!session || !model) {
    elements.sessionSummaryEstimate.textContent = "Select a model.";
    return;
  }

  elements.sessionSummaryEstimate.textContent = "Calculating bounded facts…";

  try {
    state.summaryEstimate = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary/estimate`
        + `?model=${encodeURIComponent(model)}`
    );
    const estimate = state.summaryEstimate;
    elements.sessionSummaryEstimate.textContent =
      `${providerLabel(estimate.provider)} · ${estimate.model} · `
      + `up to ${formatInteger(estimate.estimatedInputTokens)} estimated tokens · `
      + `${estimate.includedMessages} messages included`
      + `${estimate.omittedMessages
        ? ` · ${estimate.omittedMessages} omitted`
        : ""}`;
  } catch (error) {
    state.summaryEstimate = null;
    elements.sessionSummaryEstimate.textContent = error.message;
  }
}

async function generateSessionSummary() {
  const session = state.summarySession;
  const estimate = state.summaryEstimate;

  if (!session || !estimate) {
    return;
  }

  if (!await showAppConfirm(
    `Generate a summary with ${providerLabel(estimate.provider)} · ${estimate.model}? `
      + `The call may use GPU or real quota and is estimated at up to `
      + `${formatInteger(estimate.estimatedInputTokens)} input tokens.`,
    { title: "Generate summary with a model?", confirmLabel: "Generate summary" }
  )) {
    return;
  }

  elements.generateSessionSummary.disabled = true;
  elements.sessionSummaryStatus.textContent = "Generating explicit summary…";

  try {
    const summary = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          model: estimate.model,
          confirmed: true,
          providerPermissionGranted: true
        })
      }
    );
    fillSessionSummary(summary.content);
    elements.deleteSessionSummary.disabled = false;
    elements.sessionSummaryStatus.textContent =
      "Summary generated and persisted separately.";
    await refreshSessions();
  } catch (error) {
    elements.sessionSummaryStatus.textContent = error.message;
  } finally {
    elements.generateSessionSummary.disabled = false;
  }
}

async function saveSessionSummary(event) {
  event.preventDefault();
  const session = state.summarySession;

  if (!session) {
    return;
  }

  try {
    const summary = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary`,
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          content: collectSessionSummary()
        })
      }
    );
    fillSessionSummary(summary.content);
    elements.deleteSessionSummary.disabled = false;
    elements.sessionSummaryStatus.textContent =
      "Summary edit saved without calling a model.";
    await refreshSessions();
  } catch (error) {
    elements.sessionSummaryStatus.textContent = error.message;
  }
}

async function deleteSessionSummary() {
  const session = state.summarySession;

  if (!session || !await showAppConfirm(
    "Delete only this conversation summary?",
    { title: "Delete summary?", confirmLabel: "Delete", danger: true }
  )) {
    return;
  }

  try {
    await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary`,
      {
        method: "DELETE"
      }
    );
    fillSessionSummary(null);
    elements.deleteSessionSummary.disabled = true;
    elements.sessionSummaryStatus.textContent = "Summary deleted.";
    await refreshSessions();
  } catch (error) {
    elements.sessionSummaryStatus.textContent = error.message;
  }
}

function collectSessionSummary() {
  return {
    objective: elements.sessionSummaryObjective.value.trim(),
    decisions: summaryLines(elements.sessionSummaryDecisions.value),
    filesChanged: summaryLines(elements.sessionSummaryFiles.value),
    commandsAndValidation:
      summaryLines(elements.sessionSummaryValidation.value),
    unresolvedIssues:
      summaryLines(elements.sessionSummaryUnresolved.value),
    nextSuggestedStep: elements.sessionSummaryNextStep.value.trim()
  };
}

function fillSessionSummary(content) {
  elements.sessionSummaryObjective.value = content?.objective ?? "";
  elements.sessionSummaryDecisions.value =
    (content?.decisions ?? []).join("\n");
  elements.sessionSummaryFiles.value =
    (content?.filesChanged ?? []).join("\n");
  elements.sessionSummaryValidation.value =
    (content?.commandsAndValidation ?? []).join("\n");
  elements.sessionSummaryUnresolved.value =
    (content?.unresolvedIssues ?? []).join("\n");
  elements.sessionSummaryNextStep.value = content?.nextSuggestedStep ?? "";
}

function summaryLines(value) {
  return value.split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean);
}

async function deleteSession(session) {
  if (!await showAppConfirm(
    `Delete only the local record "${session.title}"? Project files will be preserved.`,
    { title: "Delete conversation?", confirmLabel: "Delete", danger: true }
  )) {
    return false;
  }

  await fetchJson(
    `/api/sessions/${encodeURIComponent(session.id)}?confirmed=true`,
    {
      method: "DELETE"
    }
  );

  if (state.conversationSessionId === session.id) {
    await beginEmptyConversation();
  }

  await refreshSessions();
  return true;
}

async function deleteArchivedSessions() {
  if (!await showAppConfirm(
    "Delete all archived conversations from this workspace?",
    { title: "Delete archived conversations?", confirmLabel: "Delete", danger: true }
  )) {
    return;
  }

  await fetchJson(
    "/api/sessions/archived?confirmed=true",
    {
      method: "DELETE"
    }
  );
  await refreshSessions();
}

async function deleteAllSessions() {
  if (!await showAppConfirm(
    "Delete all local history from this workspace? Project files will be preserved.",
    { title: "Delete all history?", confirmLabel: "Delete all", danger: true }
  )) {
    return;
  }

  await fetchJson(
    "/api/sessions?confirmed=true",
    {
      method: "DELETE"
    }
  );
  await beginEmptyConversation();
  await refreshSessions();
}

async function purgeUsageHistory() {
  if (!await showAppConfirm(
    "Delete all local token-usage history? This action does not change conversations or project files.",
    { title: "Delete usage history?", confirmLabel: "Delete", danger: true }
  )) {
    return;
  }

  elements.usagePurgeStatus.textContent = "Deleting usage history…";

  try {
    const result = await fetchJson(
      "/api/usage?confirmed=true",
      {
        method: "DELETE"
      }
    );
    elements.usagePurgeStatus.textContent =
      `${result.deletedEvents} usage event(s) deleted.`;
    await refreshUsage();
  } catch (error) {
    elements.usagePurgeStatus.textContent = error.message;
  }
}

async function reconcileUsage() {
  elements.reconcileUsage.disabled = true;
  elements.usagePurgeStatus.textContent = "Validating events and rebuilding aggregates…";

  try {
    const result = await fetchJson(
      "/api/usage/reconcile",
      {
        method: "POST"
      }
    );
    elements.usagePurgeStatus.textContent =
      `${formatInteger(result.accepted)} aceitos · `
      + `${formatInteger(result.warned)} with warnings · `
      + `${formatInteger(result.estimated)} estimated · `
      + `${formatInteger(result.rejected)} rejected · `
      + `${formatInteger(result.duplicates)} duplicates`;
    await refreshUsage();
  } catch (error) {
    elements.usagePurgeStatus.textContent = error.message;
  } finally {
    elements.reconcileUsage.disabled = false;
  }
}

function renderProviderHealth() {
  const degraded = (state.providerHealth?.providers ?? []).filter(
    provider => provider.enabled
      && ["degraded", "unavailable"].includes(provider.connectionState)
  );
  if (degraded.length > 0) {
    elements.cloudUsageCard.dataset.healthWarning = "";
    elements.cloudUsageDetail.textContent =
      `${degraded.length} active provider(s) are degraded or unavailable.`;
  } else {
    delete elements.cloudUsageCard.dataset.healthWarning;
  }
  renderCloudProviders();
}

async function refreshProviderHealth() {
  elements.refreshProviderHealth.disabled = true;

  try {
    state.providerHealth = await fetchJson("/api/provider-health");
    renderProviderHealth();
  } finally {
    elements.refreshProviderHealth.disabled = false;
  }
}

async function handleProviderHealthAction(event) {
  const test = event.target.closest("[data-provider-health-test]");

  if (test) {
    test.disabled = true;

    try {
      state.providerHealth = await fetchJson(
        `/api/provider-health/${encodeURIComponent(test.dataset.providerHealthTest)}/test`,
        {
          method: "POST"
        }
      );
      renderProviderHealth();
    } catch (error) {
      test.textContent = error.message;
    } finally {
      test.disabled = false;
    }

    return;
  }

  if (event.target.closest("[data-provider-health-refresh]")) {
    await refreshProviderHealth();
  }
}

function providerHealthStateLabel(value) {
  return {
    healthy: "Healthy",
    degraded: "Degraded",
    unavailable: "Unavailable",
    "not-configured": "Not configured",
    unknown: "Unknown"
  }[value] ?? value;
}

function formatProviderHealthDate(value) {
  return value
    ? new Date(value).toLocaleString(window.AgenticRouterI18n.locale)
    : "not observed yet";
}

async function refreshRuntimeStatus() {
  if (document.hidden) {
    return;
  }

  try {
    renderRuntimeStatus(
      await fetchJson("/api/runtime/status")
    );
  } catch (error) {
    elements.runtimeCompactMeters.textContent = "Resources unavailable";
    elements.runtimeModelList.replaceChildren(
      diagnosticRow("Memory telemetry", error.message)
    );
  }

  await refreshUsage();
}

async function refreshUsage() {
  try {
    const active = activeWorkspaceProfile();
    const query = active?.id
      ? `?workspaceId=${encodeURIComponent(active.id)}`
      : "";
    [
      state.usageOverview,
      state.cloudUsageDashboard
    ] = await Promise.all([
      fetchJson(`/api/usage/overview${query}`),
      fetchJson("/api/usage/cloud-dashboard")
    ]);
    renderUsageSummary();
    renderCloudUsage();
  } catch (error) {
    elements.settingsUsageAccuracy.textContent = "unavailable";
    elements.settingsUsageDetails.textContent =
      `Usage unavailable · ${error.message}`;
    elements.cloudUsageBadge.textContent = "unavailable";
    elements.cloudUsageSummary.textContent = "Cloud usage unavailable";
    elements.cloudUsageDetail.textContent = error.message;
  }
}

function renderUsageSummary() {
  const overview = state.usageOverview;

  if (!overview) {
    elements.settingsUsageAccuracy.textContent = "no data";
    elements.settingsUsageDetails.textContent = "Usage is not available yet.";
    return;
  }

  const usage = overview.selected;
  const accuracy = usage.accuracy === "exact"
    ? "exact"
    : usage.accuracy === "mixed"
      ? "mixed"
      : usage.accuracy === "estimated"
        ? "estimated"
        : "no data";
  const lastUpdate = usage.lastUpdatedAt
    ? new Date(usage.lastUpdatedAt).toLocaleString(window.AgenticRouterI18n.locale)
    : "no recorded calls";
  const topModels = usage.topModels.length
    ? usage.topModels.map(
      item => `${item.key}: ${formatInteger(item.totalTokens)}`
    ).join("\n")
    : "No model in this period.";
  const topRoles = usage.topRoles.length
    ? usage.topRoles.map(
      item => `${item.key}: ${formatInteger(item.totalTokens)}`
    ).join("\n")
    : "No role in this period.";
  const pinnedWindows = overview.pinned.length
    ? overview.pinned.map(
      item => `${item.window.id}: ${formatInteger(item.totalTokens)} tokens`
    ).join("\n")
    : "No pinned window.";
  const local = usage.providerBreakdown
    .filter(item => item.key === "ollama-local")
    .reduce((total, item) => total + item.totalTokens, 0);
  const cloud = usage.providerBreakdown
    .filter(item => item.key !== "ollama-local")
    .reduce((total, item) => total + item.totalTokens, 0);
  const comparison =
    `${overview.comparisonProvider} · ${overview.comparisonModel}`;
  const comparisonPrice = state.pricingCatalog?.comparisons.find(
    item => item.providerId === overview.comparisonProvider
      && item.modelId === overview.comparisonModel
  );
  const plan = state.pricingCatalog?.ollamaPlans.find(
    item => item.plan === state.settings?.usage.ollamaPlanReference
  );
  const comparisonDetails = comparisonPrice
    ? `${formatCurrency(comparisonPrice.inputPricePerMillion)}/M input · `
      + `${formatCurrency(comparisonPrice.outputPricePerMillion)}/M output · `
      + `catalog ${comparisonPrice.catalogVersion} · `
      + `updated ${new Date(comparisonPrice.updatedAt).toLocaleDateString(window.AgenticRouterI18n.locale)} · `
      + `${comparisonPrice.stale ? "stale" : "current"}\n`
      + `Comparison source: ${comparisonPrice.officialSourceUrl}`
    : "comparison price unavailable";
  const planDetails = plan
    ? `${formatCurrency(plan.monthlyPrice)}/month · ${plan.usageDescription}\n`
      + `${plan.tokenEquivalent}\n`
      + `${plan.availability ? `${plan.availability}\n` : ""}`
      + `Effective date: ${plan.effectiveDate} · `
      + `${plan.stale ? "stale reference" : "current reference"}\n`
      + `Official source: ${plan.officialSourceUrl}`
    : "reference unavailable";
  elements.settingsUsageAccuracy.textContent = accuracy;
  elements.settingsUsageSummary.dataset.accuracy = usage.accuracy;
  elements.settingsUsageDetails.textContent =
    `Window: ${usage.window.id}\n`
    + `Input / output / total: ${formatInteger(usage.inputTokens)} / `
    + `${formatInteger(usage.outputTokens)} / ${formatInteger(usage.totalTokens)}\n`
    + `Calls: ${usage.requests} · Success: ${usage.successes} · `
    + `Failures: ${usage.failures} · Cancellations: ${usage.cancellations}\n`
    + `Local / cloud: ${formatInteger(local)} / ${formatInteger(cloud)} tokens\n`
    + `Estimated provider cost: ${formatCurrency(usage.estimatedActualCost)}\n`
    + `Equivalent cloud estimate: ${formatCurrency(usage.equivalentCloudCost)} `
    + `against ${comparison}\n`
    + `Comparison rates: ${comparisonDetails}\n`
    + `This is an equivalent comparison, not an exact Ollama Cloud saving.\n`
    + `Top models:\n${topModels}\n`
    + `Top roles:\n${topRoles}\n`
    + `Pinned windows:\n${pinnedWindows}\n`
    + `Ollama plan reference: ${plan?.plan ?? "unavailable"}\n`
    + `${planDetails}\n`
    + `Last update: ${lastUpdate}`;
}

function renderCloudUsage() {
  const dashboard = state.cloudUsageDashboard;
  const active = parseModelReference(
    state.activeAgentModel
      ?? elements.modelSelector.value
  );
  const activeProvider = active.provider === "ollama-local"
    ? null
    : dashboard?.providers.find(
      provider => provider.providerId === active.provider
    );

  delete elements.cloudUsageCard.dataset.alert;

  if (!dashboard || dashboard.providers.length === 0) {
    elements.cloudUsageBadge.textContent = "not configured";
    elements.cloudUsageSummary.textContent = "Cloud usage";
    elements.cloudUsageDetail.textContent = "Not configured";
  } else if (activeProvider) {
    const accuracy = usageAccuracyLabel(activeProvider.accuracy);
    elements.cloudUsageBadge.textContent = activeProvider.percentage === null
      ? accuracy
      : `${formatPercentage(activeProvider.percentage)} · ${accuracy}`;
    elements.cloudUsageSummary.textContent =
      `${activeProvider.displayName} · ${active.model}`;
    elements.cloudUsageDetail.textContent =
      `${state.activeAgentRole ?? "primary"} · ${activeProvider.window}`;

    if (activeProvider.alertThreshold !== null) {
      elements.cloudUsageCard.dataset.alert =
        String(activeProvider.alertThreshold);
    }
  } else if (dashboard.connectedProviderCount > 0) {
    elements.cloudUsageBadge.textContent = "inactive";
    elements.cloudUsageSummary.textContent =
      `${dashboard.connectedProviderCount} connected provider(s)`;
    elements.cloudUsageDetail.textContent = "No active cloud model";
  } else {
    elements.cloudUsageBadge.textContent = "disconnected";
    elements.cloudUsageSummary.textContent =
      `${dashboard.providers.length} configured provider(s)`;
    elements.cloudUsageDetail.textContent = "No active cloud model";
  }

  elements.cloudUsageDashboardSummary.textContent = dashboard
    ? `Selected window: ${dashboard.selectedWindow}\n`
      + `Connected providers: ${dashboard.connectedProviderCount}\n`
      + `Local alerts: ${dashboard.alertThresholds.join("%, ")}%\n`
      + `Updated: ${new Date(dashboard.generatedAt).toLocaleString(window.AgenticRouterI18n.locale)}`
    : "Dashboard is not available yet.";
  elements.cloudUsageProviderCards.replaceChildren();

  for (const provider of dashboard?.providers ?? []) {
    elements.cloudUsageProviderCards.append(
      createCloudUsageProviderCard(provider)
    );
  }
}

function createCloudUsageProviderCard(provider) {
  const card = document.createElement("article");
  card.className = "cloud-usage-provider-card";

  if (provider.alertThreshold !== null) {
    card.dataset.alert = String(provider.alertThreshold);
  }

  const heading = document.createElement("div");
  heading.className = "cloud-usage-provider-heading";
  const headingText = document.createElement("div");
  const title = document.createElement("h3");
  title.textContent = provider.displayName;
  const connection = document.createElement("small");
  connection.textContent =
    `${cloudConnectionLabel(provider.connectionState)} · `
    + `${billingModeLabel(provider.expectedBillingMode)}`;
  headingText.append(title, connection);
  const quota = document.createElement("span");
  quota.className = `usage-accuracy-badge${provider.alertThreshold !== null
    ? " warning"
    : ""}`;
  quota.textContent = provider.percentage === null
    ? usageAccuracyLabel(provider.accuracy)
    : `${formatPercentage(provider.percentage)} · `
      + usageAccuracyLabel(provider.accuracy);
  heading.append(headingText, quota);

  const metrics = document.createElement("div");
  metrics.className = "cloud-usage-metrics";
  metrics.append(
    cloudUsageMetric("Tokens", formatInteger(provider.totalTokens)),
    cloudUsageMetric("Requests", formatInteger(provider.requests)),
    cloudUsageMetric(
      "Estimated cost",
      formatCurrency(provider.estimatedActualCost)
    ),
    cloudUsageMetric(
      "Last call",
      provider.latestRequestAt
        ? new Date(provider.latestRequestAt).toLocaleString(window.AgenticRouterI18n.locale)
        : "none"
    )
  );

  const quotaDetail = document.createElement("small");
  quotaDetail.textContent =
    `Quota: ${provider.quotaSource} · ${provider.window}`
    + `${provider.resetAt
      ? ` · reset ${new Date(provider.resetAt).toLocaleString(window.AgenticRouterI18n.locale)}`
      : ""}`;
  const billingDetail = document.createElement("small");
  billingDetail.textContent =
    `${billingModeLabel(provider.expectedBillingMode)} is only a local expectation; `
    + "it does not guarantee billing or free usage.";
  const warning = document.createElement("small");
  warning.hidden = !provider.hasRateLimitWarning;
  warning.className = "cloud-provider-diagnostic";
  warning.textContent = "Warning: a 429 response was observed in this window.";

  const models = document.createElement("div");
  models.className = "cloud-usage-models";

  for (const model of provider.models) {
    const item = document.createElement("section");
    item.className = "cloud-usage-model";
    const modelTitle = document.createElement("h4");
    modelTitle.textContent = `${provider.displayName} · ${model.modelId}`;
    const details = document.createElement("small");
    details.textContent =
      `${formatInteger(model.inputTokens)} input · `
      + `${formatInteger(model.outputTokens)} output · `
      + `${formatInteger(model.requests)} call(s) · `
      + `${formatCurrency(model.estimatedActualCost)} · `
      + `${model.roles.join(", ") || "no observed role"}`;
    const capabilities = document.createElement("div");
    capabilities.className = "cloud-capability-list";

    for (const capability of model.capabilities) {
      const badge = document.createElement("span");
      badge.className = "badge muted";
      badge.textContent = capability;
      capabilities.append(badge);
    }

    item.append(modelTitle, details, capabilities);
    models.append(item);
  }

  if (provider.models.length === 0) {
    const empty = document.createElement("small");
    empty.textContent = "No cached model or observed usage.";
    models.append(empty);
  }

  card.append(
    heading,
    metrics,
    quotaDetail,
    billingDetail,
    warning,
    models
  );
  return card;
}

function cloudUsageMetric(label, value) {
  const metric = document.createElement("div");
  metric.className = "cloud-usage-metric";
  const name = document.createElement("span");
  name.textContent = label;
  const content = document.createElement("strong");
  content.textContent = value;
  metric.append(name, content);
  return metric;
}

async function openCloudUsage() {
  elements.runtimeDetails.open = false;
  await refreshCloudUsage();
  elements.cloudUsageDialog.showModal();
  elements.dismissCloudUsage.focus();
}

function closeCloudUsage() {
  if (!elements.cloudUsageDialog.open) {
    return;
  }

  elements.cloudUsageDialog.close();
  elements.cloudUsageCard.focus();
}

async function refreshCloudUsage() {
  elements.refreshCloudUsage.disabled = true;
  elements.cloudUsageRefreshStatus.textContent = "Refreshing local data…";

  try {
    state.cloudUsageDashboard = await fetchJson("/api/usage/cloud-dashboard");
    renderCloudUsage();
    elements.cloudUsageRefreshStatus.textContent = "Dashboard refreshed.";
  } catch (error) {
    elements.cloudUsageRefreshStatus.textContent = error.message;
  } finally {
    elements.refreshCloudUsage.disabled = false;
  }
}

function parseModelReference(value) {
  const normalized = value && value !== "auto"
    ? value
    : "";
  const separator = normalized.indexOf("::");
  return separator > 0
    ? {
      provider: normalized.slice(0, separator),
      model: normalized.slice(separator + 2)
    }
    : {
      provider: "ollama-local",
      model: normalized
    };
}

function usageAccuracyLabel(accuracy) {
  return {
    exact: "exact",
    estimated: "estimated",
    mixed: "mixed",
    unavailable: "unavailable"
  }[accuracy] ?? "unavailable";
}

function billingModeLabel(mode) {
  return {
    "free-tier": "Expected free tier",
    paid: "Expected paid",
    unknown: "Unknown billing"
  }[mode] ?? "Unknown billing";
}

function formatPercentage(value) {
  return `${Number(value).toLocaleString(
    window.AgenticRouterI18n.locale,
    {
      maximumFractionDigits: 2
    }
  )}%`;
}

function renderRuntimeStatus(runtime) {
  state.runtime = runtime;
  const memoryRows = [];
  const compactMeters = [];
  const ram = runtime.systemMemory;

  if (ram.status === "available") {
    compactMeters.push(
      compactRuntimeMeter("RAM", ram.usedPercent, "system")
    );
    memoryRows.push(
      memoryRow(
        "System RAM",
        ram.usedBytes,
        ram.totalBytes,
        ram.usedPercent,
        ram.diagnostic,
        "system"
      )
    );
  } else {
    compactMeters.push(compactRuntimeMeter("RAM", null, "system"));
    memoryRows.push(
      diagnosticRow(
        "System RAM",
        ram.diagnostic
      )
    );
  }

  for (const device of runtime.devices) {
    compactMeters.push(
      compactRuntimeMeter(
        compactDeviceName(device.name),
        device.usedDedicatedMemoryBytes == null ? null : device.usedPercent,
        "gpu"
      )
    );
    memoryRows.push(
      device.usedDedicatedMemoryBytes == null
        ? diagnosticRow(
          device.name,
          device.diagnostic ?? `Dedicated total: ${formatGiB(device.totalDedicatedMemoryBytes)}`,
          "partial"
        )
        : memoryRow(
          device.name,
          device.usedDedicatedMemoryBytes,
          device.totalDedicatedMemoryBytes,
          device.usedPercent,
          device.diagnostic,
          "gpu"
        )
    );
  }

  if (runtime.devicesStatus === "unavailable") {
    memoryRows.push(
      diagnosticRow(
        "Graphics devices",
        runtime.devicesDiagnostic
      )
    );
  }

  elements.runtimeCompactMeters.replaceChildren(...compactMeters);
  elements.runtimeSummary.title = runtime.warnings.join("\n");
  elements.runtimeMemoryList.replaceChildren(...memoryRows);
  renderLoadedModels(runtime);
}

function compactRuntimeMeter(label, percent, kind) {
  const indicator = document.createElement("span");
  indicator.className = `runtime-compact-indicator ${kind}`;
  const name = document.createElement("span");
  name.className = "runtime-compact-label";
  name.textContent = label;
  const meter = document.createElement("span");
  meter.className = "runtime-compact-meter";
  const fill = document.createElement("span");
  const normalized = percent == null
    ? 0
    : Math.max(0, Math.min(100, percent));
  fill.style.width = `${normalized}%`;
  fill.className = normalized >= 90
    ? "critical"
    : normalized >= 75
      ? "warning"
      : "";
  meter.append(fill);
  const value = document.createElement("span");
  value.className = "runtime-compact-value";
  value.textContent = formatPercent(percent);
  indicator.setAttribute(
    "aria-label",
    `${label}: ${formatPercent(percent)}`
  );
  indicator.append(name, meter, value);
  return indicator;
}

function compactDeviceName(name) {
  const match = name.match(/(?:RTX|GTX)\s*(\d{3,4})/i);
  return match?.[1] ?? name.replace(/^NVIDIA\s+/i, "");
}

function memoryRow(name, used, total, percent, diagnostic, kind) {
  const row = document.createElement("div");
  row.className = `runtime-row memory ${kind}`;
  const header = document.createElement("div");
  header.className = "runtime-row-header";
  const label = document.createElement("strong");
  label.textContent = name;
  const value = document.createElement("span");
  value.textContent =
    `${formatGiB(used)} / ${formatGiB(total)} · ${formatPercent(percent)}`;
  const meter = document.createElement("div");
  meter.className = "runtime-meter";
  meter.setAttribute("role", "progressbar");
  meter.setAttribute("aria-label", `${name}: ${formatPercent(percent)}`);
  meter.setAttribute("aria-valuemin", "0");
  meter.setAttribute("aria-valuemax", "100");
  meter.setAttribute("aria-valuenow", String(Math.round(percent)));
  const fill = document.createElement("span");
  const normalized = Math.max(0, Math.min(100, percent));
  fill.className = normalized >= 90
    ? "critical"
    : normalized >= 75
      ? "warning"
      : "";
  fill.style.width = `${normalized}%`;
  meter.append(fill);
  row.title = diagnostic ?? "";
  header.append(label, value);
  row.append(header, meter);
  return row;
}

function diagnosticRow(name, diagnostic, status = "unavailable") {
  const row = document.createElement("div");
  row.className = `runtime-row diagnostic ${status}`;
  const label = document.createElement("strong");
  label.textContent = name;
  const value = document.createElement("span");
  value.textContent = diagnostic ?? "Unavailable";
  row.append(label, value);
  return row;
}

function renderLoadedModels(runtime) {
  const availableDevices = runtime.devices.filter(
    device => device.usedDedicatedMemoryBytes != null
      && device.totalDedicatedMemoryBytes > 0
  );
  const usedGpuMemory = availableDevices.reduce(
    (total, device) => total + device.usedDedicatedMemoryBytes,
    0
  );
  const totalGpuMemory = availableDevices.reduce(
    (total, device) => total + device.totalDedicatedMemoryBytes,
    0
  );
  elements.runtimeModelSummary.textContent = totalGpuMemory > 0
    ? `${formatGiB(usedGpuMemory)} / ${formatGiB(totalGpuMemory)} · `
      + `${formatPercent(usedGpuMemory * 100 / totalGpuMemory)}`
    : t("memory.gpu_memory_unavailable");

  const groups = new Map();
  for (const model of runtime.loadedModels) {
    const identity = loadedModelGpuIdentity(model);
    if (!groups.has(identity.key)) {
      groups.set(identity.key, {
        ...identity,
        models: []
      });
    }
    groups.get(identity.key).models.push(model);
  }
  for (const device of runtime.devices) {
    if (device.ollamaIndex == null) {
      continue;
    }
    const key = `gpu-${device.ollamaIndex}`;
    const label = `GPU ${device.ollamaIndex} · ${device.name}`;
    if (!groups.has(key)) {
      groups.set(key, {
        key,
        gpuIndex: Number(device.ollamaIndex),
        label,
        order: Number(device.ollamaIndex),
        models: []
      });
    } else {
      groups.get(key).label = label;
    }
  }

  if (groups.size === 0) {
    elements.runtimeModelList.replaceChildren(
      diagnosticRow(
        runtime.loadedModelsStatus === "unavailable"
          ? "Ollama telemetry"
          : "No reported model",
        runtime.loadedModelsDiagnostic
          ?? "Ollama did not report loaded models in /api/ps."
      )
    );
    return;
  }

  const orderedGroups = [...groups.values()].sort(
    (left, right) => left.order - right.order
  );
  const grid = document.createElement("div");
  grid.className = "loaded-model-gpu-grid";
  grid.append(
    ...orderedGroups.map(
      group => loadedModelGpuCard(
        group,
        runtime.devices,
        runtime.loadedModelsStatus
      )
    )
  );
  const summary = document.createElement("div");
  summary.className = "loaded-model-summary";
  summary.append(
    loadedModelSummaryItem(
      t("memory.system_ram_model"),
      formatGiB(runtime.loadedModels.reduce(
        (total, model) => total + Number(model.estimatedRamSizeBytes ?? 0),
        0
      ))
    ),
    loadedModelSummaryItem(
      t("memory.total_context_window"),
      `${formatInteger(runtime.loadedModels.reduce(
        (total, model) => total + Number(model.actualContextTokens ?? 0),
        0
      ))} tokens`
    )
  );
  elements.runtimeModelList.replaceChildren(grid, summary);
}

function loadedModelGpuIdentity(model) {
  if (model.gpuIndex != null) {
    return {
      key: `gpu-${model.gpuIndex}`,
      gpuIndex: Number(model.gpuIndex),
      label: `GPU ${model.gpuIndex}${model.gpuName ? ` · ${model.gpuName}` : ""}`,
      order: Number(model.gpuIndex)
    };
  }
  if (model.processor === "cpu") {
    return {
      key: "cpu",
      gpuIndex: null,
      label: t("memory.cpu"),
      order: Number.MAX_SAFE_INTEGER - 2
    };
  }
  if (model.processor === "gpu" || model.processor === "hybrid") {
    return {
      key: "auto",
      gpuIndex: null,
      label: t("memory.gpu_auto"),
      order: Number.MAX_SAFE_INTEGER - 1
    };
  }
  return {
    key: "unknown",
    gpuIndex: null,
    label: t("memory.gpu_unknown"),
    order: Number.MAX_SAFE_INTEGER
  };
}

function loadedModelGpuCard(group, devices, loadedModelsStatus) {
  const card = document.createElement("article");
  card.className = "loaded-model-gpu-card";
  const header = document.createElement("header");
  const title = document.createElement("strong");
  title.textContent = group.label;
  const details = document.createElement("details");
  details.className = "loaded-model-details";
  const detailsSummary = document.createElement("summary");
  detailsSummary.textContent = t("memory.details");
  const detailsContent = document.createElement("div");
  detailsContent.className = "loaded-model-details-content";
  detailsContent.append(
    ...(group.models.length > 0
      ? group.models.map(model => loadedModelDetailRow(model))
      : [loadedModelEmptyDetail()])
  );
  details.append(detailsSummary, detailsContent);
  header.append(title, details);

  const modelVramValues = group.models
    .map(model => model.vramSizeBytes)
    .filter(value => value != null);
  const modelVram = modelVramValues.reduce(
    (total, value) => total + Number(value),
    0
  );
  const device = group.gpuIndex == null
    ? null
    : devices.find(item => item.ollamaIndex === group.gpuIndex);
  const modelTelemetryAvailable = loadedModelsStatus === "available";
  const modelVramKnown = modelTelemetryAvailable
    && modelVramValues.length === group.models.length;
  const systemDriverVram = device?.usedDedicatedMemoryBytes == null
    || !modelVramKnown
    ? null
    : Math.max(0, device.usedDedicatedMemoryBytes - modelVram);
  const contextRuntimeValues = group.models
    .map(model => estimatedContextRuntimeBytes(model))
    .filter(value => value != null);
  const contextRuntimeBytes = group.models.length === 0 && modelTelemetryAvailable
    ? 0
    : contextRuntimeValues.length === group.models.length
      ? contextRuntimeValues.reduce((total, value) => total + value, 0)
      : null;
  const contextTokens = group.models.reduce(
    (total, model) => total + Number(model.actualContextTokens ?? 0),
    0
  );
  const metrics = document.createElement("div");
  metrics.className = "loaded-model-metrics";
  metrics.append(
    loadedModelMetric(
      "model",
      t("memory.model_vram_used"),
      modelVramKnown ? formatGiB(modelVram) : "n/d"
    ),
    loadedModelMetric(
      "system",
      t("memory.system_driver_vram"),
      systemDriverVram == null ? "n/d" : formatGiB(systemDriverVram)
    ),
    loadedModelMetric(
      "context",
      t("memory.context_share"),
      `${formatInteger(contextTokens)} tokens`
    ),
    loadedModelMetric(
      "memory",
      t("memory.context_runtime"),
      contextRuntimeBytes == null ? "n/d" : `~${formatGiB(contextRuntimeBytes)}`,
      t("memory.context_runtime_note")
    )
  );
  card.append(header, metrics);
  return card;
}

function loadedModelMetric(kind, labelText, valueText, diagnostic = null) {
  const row = document.createElement("div");
  row.className = "loaded-model-metric";
  if (diagnostic) {
    row.title = diagnostic;
  }
  const label = document.createElement("span");
  const icon = document.createElement("span");
  icon.className = `loaded-model-metric-icon ${kind}`;
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = {
    model: "▦",
    system: "⚙",
    context: "●",
    memory: "◫"
  }[kind];
  label.append(icon, document.createTextNode(labelText));
  const value = document.createElement("strong");
  value.textContent = valueText;
  row.append(label, value);
  return row;
}

function loadedModelDetailRow(model) {
  const row = document.createElement("div");
  row.className = "loaded-model-detail-row";
  const name = document.createElement("strong");
  name.textContent = model.name;
  const allocation = document.createElement("span");
  const contextRuntimeBytes = estimatedContextRuntimeBytes(model);
  allocation.textContent = `${formatGiB(model.vramSizeBytes)} VRAM · `
    + `${formatGiB(model.estimatedRamSizeBytes)} RAM · `
    + `${formatInteger(model.actualContextTokens)} tokens · `
    + `${contextRuntimeBytes == null ? "n/d" : `~${formatGiB(contextRuntimeBytes)}`} context/runtime`;
  row.append(name, allocation);
  return row;
}

function loadedModelEmptyDetail() {
  const row = document.createElement("div");
  row.className = "loaded-model-detail-row";
  const message = document.createElement("span");
  message.textContent = t("memory.no_loaded_model");
  row.append(message);
  return row;
}

function estimatedContextRuntimeBytes(model) {
  if (model.totalSizeBytes == null) {
    return null;
  }
  const installed = state.models.find(
    candidate => candidate.provider === "ollama-local"
      && candidate.name === model.name
  );
  return installed?.sizeBytes == null
    ? null
    : Math.max(0, Number(model.totalSizeBytes) - Number(installed.sizeBytes));
}

function loadedModelSummaryItem(labelText, valueText) {
  const item = document.createElement("div");
  const label = document.createElement("span");
  label.textContent = labelText;
  const value = document.createElement("strong");
  value.textContent = valueText;
  item.append(label, value);
  return item;
}

function formatInteger(value) {
  return new Intl.NumberFormat(window.AgenticRouterI18n.locale).format(Number(value ?? 0));
}

function formatCurrency(value) {
  const number = Number(value ?? 0);
  const digits = Math.abs(number) > 0 && Math.abs(number) < 0.01
    ? 6
    : 2;
  return new Intl.NumberFormat(
    window.AgenticRouterI18n.locale,
    {
      style: "currency",
      currency: "USD",
      minimumFractionDigits: digits,
      maximumFractionDigits: digits
    }
  ).format(number);
}

function scheduleRuntimeRefresh() {
  clearTimeout(state.runtimeTimer);

  if (document.hidden || !state.settings) {
    return;
  }

  const seconds = state.requestController
    ? state.settings.runtime.runtimeStatusActiveRefreshSeconds
    : state.settings.runtime.runtimeStatusIdleRefreshSeconds;
  state.runtimeTimer = setTimeout(
    async () => {
      await refreshRuntimeStatus();
      scheduleRuntimeRefresh();
    },
    seconds * 1000
  );
}

function handleVisibilityChange() {
  if (document.hidden) {
    clearTimeout(state.runtimeTimer);
  } else {
    refreshRuntimeStatus();
    void refreshGit();
    scheduleRuntimeRefresh();
  }
}

const runtimeRoleLabels = {
  router: "Router",
  residentCoordinator: "Resident coordinator",
  specialist: "Specialist",
  primary: "Primary",
  fallback: "Fallback",
  benchmark: "Benchmark",
  modelTest: "Model test",
  webSearchSynthesis: "Web search synthesis",
  visionRequest: "Vision request"
};

function renderRuntimeProfilesEditor() {
  const runtime = state.settings?.ollamaRuntime;

  if (!runtime || !elements.runtimeRoleProfiles) {
    return;
  }

  const profiles = [];

  for (const [role, profile] of Object.entries(runtime.roleDefaults)) {
    const card = document.createElement("article");
    card.className = "runtime-role-profile";
    card.dataset.role = role;
    const title = document.createElement("strong");
    title.textContent = runtimeRoleLabels[role] ?? role;
    const fields = document.createElement("div");
    fields.className = "runtime-profile-fields";

    for (const [label, field] of [
      ["Min.", "minimumContextTokens"],
      ["Target", "targetContextTokens"],
      ["Max.", "maximumContextTokens"],
      ["Output", "outputTokenLimit"],
      ["Keep-alive", "keepAlive"]
    ]) {
      const fieldLabel = document.createElement("label");
      const caption = document.createElement("span");
      caption.textContent = label;
      const input = document.createElement("input");
      input.type = "number";
      input.min = field === "keepAlive" ? "-1" : "128";
      input.max = field === "keepAlive" ? "86400" : "131072";
      input.value = profile[field];
      input.dataset.runtimeRole = role;
      input.dataset.runtimeField = field;
      fieldLabel.append(caption, input);
      fields.append(fieldLabel);
    }

    card.append(title, fields);
    profiles.push(card);
  }

  elements.runtimeRoleProfiles.replaceChildren(...profiles);
  elements.runtimeMemoryGpuPercent.value =
    runtime.memory.targetMaximumGpuUsagePercent;
  elements.runtimeMemoryFreeVram.value =
    bytesToGiB(runtime.memory.minimumFreeVramBytes);
  elements.runtimeMemoryFreeRam.value =
    bytesToGiB(runtime.memory.minimumFreeSystemRamBytes);
  elements.runtimeMemoryCpuOffload.checked = runtime.memory.allowCpuOffload;
  elements.runtimeMemoryPreferFullGpu.checked =
    runtime.memory.preferFullGpuForActivePrimary;
  renderRuntimeDevicePolicies(
    runtime.memory
  );

  const localModels = state.models
    .filter(model => model.provider === "ollama-local")
    .map(model => ({
      value: model.name,
      label: `${model.displayName ?? model.name} · ${shortDigest(model.digest)}`,
      title: model.digest ?? "digest unavailable"
    }));
  replaceOptions(
    elements.runtimeOverrideModel,
    localModels,
    elements.runtimeOverrideModel.value
      || localModels[0]?.value
      || ""
  );
  replaceOptions(
    elements.runtimeOverrideRole,
    Object.keys(runtimeRoleLabels).map(role => ({
      value: role,
      label: runtimeRoleLabels[role]
    })),
    elements.runtimeOverrideRole.value || "residentCoordinator"
  );
  loadRuntimeOverrideEditor();
  renderRuntimeProfileEvidence();
}

function loadRuntimeOverrideEditor() {
  const runtime = state.settings?.ollamaRuntime;
  const model = state.models.find(
    candidate => candidate.name === elements.runtimeOverrideModel.value
      && candidate.provider === "ollama-local"
  );
  const role = elements.runtimeOverrideRole.value;
  const saved = runtime?.modelOverrides.find(
    candidate => candidate.provider === "ollama-local"
      && candidate.model === model?.name
      && candidate.digest === model?.digest
  )?.overrides?.[role];
  const profile = saved ?? runtime?.roleDefaults?.[role];

  if (!profile) {
    return;
  }

  elements.runtimeOverrideMinimum.value = profile.minimumContextTokens;
  elements.runtimeOverrideTarget.value = profile.targetContextTokens;
  elements.runtimeOverrideMaximum.value = profile.maximumContextTokens;
  elements.runtimeOverrideOutput.value = profile.outputTokenLimit;
  elements.runtimeOverrideKeepAlive.value = profile.keepAlive;
  elements.removeRuntimeOverride.disabled = !saved;
}

function renderRuntimeDevicePolicies(memory) {
  const cards = state.devices
    .filter(device => !device.isAuto)
    .map(device => {
      const card = document.createElement("article");
      card.className = "runtime-device-policy";
      const enabledLabel = document.createElement("label");
      enabledLabel.className = "checkbox-label";
      const enabled = document.createElement("input");
      enabled.type = "checkbox";
      enabled.dataset.runtimeDeviceEnabled = device.id;
      enabled.checked = Object.hasOwn(
        memory.devices,
        device.id
      );
      const name = document.createElement("span");
      name.textContent = device.name;
      enabledLabel.append(enabled, name);

      const fields = document.createElement("div");
      fields.className = "runtime-profile-fields runtime-device-policy-fields";
      const policy = memory.devices[device.id] ?? {
        targetMaximumUsagePercent: memory.targetMaximumGpuUsagePercent,
        minimumFreeVramBytes: memory.minimumFreeVramBytes
      };
      const percentLabel = document.createElement("label");
      const percentCaption = document.createElement("span");
      percentCaption.textContent = "Maximum usage (%)";
      const percent = document.createElement("input");
      percent.type = "number";
      percent.min = "50";
      percent.max = "100";
      percent.value = policy.targetMaximumUsagePercent;
      percent.dataset.runtimeDevicePercent = device.id;
      percent.disabled = !enabled.checked;
      percentLabel.append(percentCaption, percent);
      const freeLabel = document.createElement("label");
      const freeCaption = document.createElement("span");
      freeCaption.textContent = "Free VRAM (GiB)";
      const free = document.createElement("input");
      free.type = "number";
      free.min = "0";
      free.step = "0.25";
      free.value = bytesToGiB(policy.minimumFreeVramBytes);
      free.dataset.runtimeDeviceFreeVram = device.id;
      free.disabled = !enabled.checked;
      freeLabel.append(freeCaption, free);
      fields.append(percentLabel, freeLabel);
      card.append(enabledLabel, fields);
      return card;
    });

  if (cards.length === 0) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent = "No specific GPU was detected.";
    cards.push(empty);
  }

  elements.runtimeMemoryDevicePolicies.replaceChildren(...cards);
}

function handleRuntimeDevicePolicyChange(event) {
  const deviceId = event.target.dataset.runtimeDeviceEnabled;

  if (!deviceId) {
    return;
  }

  const enabled = event.target.checked;
  elements.runtimeMemoryDevicePolicies.querySelector(
    `[data-runtime-device-percent="${CSS.escape(deviceId)}"]`
  ).disabled = !enabled;
  elements.runtimeMemoryDevicePolicies.querySelector(
    `[data-runtime-device-free-vram="${CSS.escape(deviceId)}"]`
  ).disabled = !enabled;
}

function saveRuntimeOverrideDraft() {
  const runtime = state.settings.ollamaRuntime;
  const model = state.models.find(
    candidate => candidate.name === elements.runtimeOverrideModel.value
      && candidate.provider === "ollama-local"
  );
  const role = elements.runtimeOverrideRole.value;

  if (!model?.digest) {
    elements.runtimeProfileResult.textContent =
      "The local model must have an exact digest to receive an override.";
    return;
  }

  const profile = {
    minimumContextTokens: Number(elements.runtimeOverrideMinimum.value),
    targetContextTokens: Number(elements.runtimeOverrideTarget.value),
    maximumContextTokens: Number(elements.runtimeOverrideMaximum.value),
    outputTokenLimit: Number(elements.runtimeOverrideOutput.value),
    keepAlive: Number(elements.runtimeOverrideKeepAlive.value)
  };
  const overrides = runtime.modelOverrides.map(
    item => ({
      ...item,
      overrides: {
        ...item.overrides
      }
    })
  );
  let exact = overrides.find(
    item => item.provider === "ollama-local"
      && item.model === model.name
      && item.digest === model.digest
  );

  if (!exact) {
    exact = {
      provider: "ollama-local",
      model: model.name,
      digest: model.digest,
      overrides: {}
    };
    overrides.push(exact);
  }

  exact.overrides[role] = profile;
  state.settings.ollamaRuntime = {
    ...runtime,
    modelOverrides: overrides
  };
  state.settingsDirty = true;
  updateSettingsDirtyState();
  elements.runtimeProfileResult.textContent =
    `Override prepared for ${model.name}@${shortDigest(model.digest)} · ${runtimeRoleLabels[role]}. Save settings to apply it.`;
  loadRuntimeOverrideEditor();
}

function removeRuntimeOverrideDraft() {
  const runtime = state.settings.ollamaRuntime;
  const model = state.models.find(
    candidate => candidate.name === elements.runtimeOverrideModel.value
      && candidate.provider === "ollama-local"
  );
  const role = elements.runtimeOverrideRole.value;
  const overrides = runtime.modelOverrides.map(
    item => ({
      ...item,
      overrides: {
        ...item.overrides
      }
    })
  );
  const exact = overrides.find(
    item => item.provider === "ollama-local"
      && item.model === model?.name
      && item.digest === model?.digest
  );

  if (exact) {
    delete exact.overrides[role];
  }

  state.settings.ollamaRuntime = {
    ...runtime,
    modelOverrides: overrides.filter(
      item => Object.keys(item.overrides).length > 0
    )
  };
  state.settingsDirty = true;
  updateSettingsDirtyState();
  elements.runtimeProfileResult.textContent =
    "Override removed from the draft. Save settings to apply it.";
  loadRuntimeOverrideEditor();
}

function collectOllamaRuntimeSettings() {
  const runtime = state.settings.ollamaRuntime;
  const roles = {};

  for (const [role, fallback] of Object.entries(runtime.roleDefaults)) {
    const values = {};

    for (const field of Object.keys(fallback)) {
      const input = elements.runtimeRoleProfiles.querySelector(
        `[data-runtime-role="${role}"][data-runtime-field="${field}"]`
      );
      values[field] = input ? Number(input.value) : fallback[field];
    }

    roles[role] = values;
  }

  const devices = {};

  for (const enabled of elements.runtimeMemoryDevicePolicies.querySelectorAll(
    "[data-runtime-device-enabled]"
  )) {
    if (!enabled.checked) {
      continue;
    }

    const deviceId = enabled.dataset.runtimeDeviceEnabled;
    devices[deviceId] = {
      targetMaximumUsagePercent: Number(
        elements.runtimeMemoryDevicePolicies.querySelector(
          `[data-runtime-device-percent="${CSS.escape(deviceId)}"]`
        ).value
      ),
      minimumFreeVramBytes: giBToBytes(
        elements.runtimeMemoryDevicePolicies.querySelector(
          `[data-runtime-device-free-vram="${CSS.escape(deviceId)}"]`
        ).value
      )
    };
  }

  return {
    ...runtime,
    roleDefaults: roles,
    memory: {
      ...runtime.memory,
      targetMaximumGpuUsagePercent: Number(
        elements.runtimeMemoryGpuPercent.value
      ),
      minimumFreeVramBytes: giBToBytes(
        elements.runtimeMemoryFreeVram.value
      ),
      minimumFreeSystemRamBytes: giBToBytes(
        elements.runtimeMemoryFreeRam.value
      ),
      allowCpuOffload: elements.runtimeMemoryCpuOffload.checked,
      preferFullGpuForActivePrimary:
        elements.runtimeMemoryPreferFullGpu.checked,
      devices
    }
  };
}

async function analyzeRuntimeProfile() {
  const model = elements.runtimeOverrideModel.value;
  const role = elements.runtimeOverrideRole.value;
  elements.analyzeRuntimeProfile.disabled = true;
  elements.runtimeProfileResult.textContent =
    `Analyzing ${model} metadata; the model will not be loaded…`;

  try {
    const result = await fetchJson(
      "/api/runtime/profiles/analyze",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          model,
          role
        })
      }
    );
    const recommendation = result.recommendation;
    elements.runtimeProfileResult.textContent =
      `${model} · ${runtimeRoleLabels[role] ?? role}\n`
      + `Declared: ${formatInteger(recommendation.declaredMaximumContext)} · `
      + `configured: ${formatInteger(recommendation.configuredContext)}\n`
      + `Suggestion ${formatInteger(recommendation.suggestedMinimum)} / `
      + `${formatInteger(recommendation.suggestedTarget)} / `
      + `${formatInteger(recommendation.suggestedMaximum)} · `
      + `confidence ${recommendation.confidence}\n`
      + `Source: ${recommendation.source} · ${recommendation.reason}\n`
      + `Load changed: ${result.loadedModelChanged ? "yes (unexpected)" : "no"}`;
  } catch (error) {
    elements.runtimeProfileResult.textContent =
      runtimeProfileErrorMessage(error);
  } finally {
    elements.analyzeRuntimeProfile.disabled = false;
  }
}

async function measureRuntimeProfile() {
  const model = elements.runtimeOverrideModel.value;
  const role = elements.runtimeOverrideRole.value;
  const context = Number(elements.runtimeOverrideTarget.value);
  const consent =
    `Measure ${model} with ${formatInteger(context)} context tokens?\n\n`
    + "This action will load a real model in Ollama and may use GPU, VRAM, and RAM. "
    + "The Host will try to restore the previous resident state.";

  if (!await showAppConfirm(consent, {
    title: "Run real measurement?",
    confirmLabel: "Run measurement"
  })) {
    return;
  }

  elements.measureRuntimeProfile.disabled = true;
  elements.runtimeProfileResult.textContent =
    `Measuring ${model} at ${formatInteger(context)} tokens…`;

  try {
    const result = await fetchJson(
      "/api/runtime/profiles/measure",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          model,
          role,
          contextCandidates: [context],
          permissionGranted: true,
          runMinimalRequest: false
        })
      }
    );
    const measurement = result.measurement;
    elements.runtimeProfileResult.textContent =
      `${measurement.model}@${shortDigest(measurement.digest)} · `
      + `${formatInteger(measurement.actualContext)} tokens\n`
      + `VRAM ${formatGiB(measurement.vramSizeBytes)} · `
      + `estimated RAM ${formatGiB(measurement.estimatedRamSizeBytes)} · `
      + `${measurement.processor}\n`
      + `Load ${formatInteger(measurement.loadDurationMilliseconds)} ms · `
      + `resident restored: ${result.priorResidentRestored ? "yes" : "not required"}`;
    state.runtimeProfiles = await fetchJson("/api/runtime/profiles");
    renderRuntimeProfileEvidence();
    await refreshRuntimeStatus();
  } catch (error) {
    elements.runtimeProfileResult.textContent =
      runtimeProfileErrorMessage(error);
  } finally {
    elements.measureRuntimeProfile.disabled = false;
  }
}

function renderRuntimeProfileEvidence() {
  const profiles = state.runtimeProfiles;

  if (!profiles) {
    return;
  }

  const warnings = profiles.sharedModelWarnings.map(warning => {
    const row = document.createElement("div");
    row.className = "runtime-shared-warning";
    const icon = document.createElement("span");
    icon.className = "information-button";
    icon.textContent = "i";
    icon.tabIndex = 0;
    icon.setAttribute("role", "img");
    icon.setAttribute("aria-label", warning.message);
    icon.dataset.tooltip = warning.message;
    const text = document.createElement("span");
    text.textContent =
      `${warning.model} · ${warning.roles.map(
        role => runtimeRoleLabels[role] ?? role
      ).join(", ")}`;
    row.append(icon, text);
    return row;
  });
  elements.runtimeSharedModelWarnings.replaceChildren(...warnings);
}

function runtimeProfileErrorMessage(error) {
  const payload = error.payload;
  return payload?.code
    ? `${payload.message}\nCode: ${payload.code} · stage: ${payload.stage} · trace: ${payload.traceId}`
    : error.message;
}

function shortDigest(value) {
  return value
    ? value.slice(0, 12)
    : "no digest";
}

function bytesToGiB(value) {
  return Number((Number(value ?? 0) / (1024 ** 3)).toFixed(2));
}

function giBToBytes(value) {
  return Math.round(Number(value || 0) * (1024 ** 3));
}

function renderComposerModels() {
  const selected = elements.modelSelector.value || "auto";
  replaceOptions(
    elements.modelSelector,
    [
      {
        value: "auto",
        label: "Auto"
      },
      ...modelOptions()
    ],
    selected
  );
  updateComposerModelTitle();
  updateHarnessControls();
}

function renderSettings() {
  if (!state.settings) {
    return;
  }

  elements.ollamaUrl.value = state.settings.ollamaUrl;
  replaceOptions(elements.routerModel, modelOptions(), state.settings.routerModel);
  replaceOptions(
    elements.routerGpu,
    gpuOptions(true, state.settings.routerGpu),
    state.settings.routerGpu
  );
  replaceOptions(
    elements.actionModel,
    modelOptions(),
    state.settings.actionModel
  );
  replaceOptions(
    elements.actionGpu,
    gpuOptions(true, state.settings.actionGpu),
    state.settings.actionGpu
  );
  replaceOptions(
    elements.coordinatorModel,
    modelOptions(),
    state.settings.coordinatorModel
  );
  replaceOptions(
    elements.coordinatorGpu,
    gpuOptions(true, state.settings.coordinatorGpu),
    state.settings.coordinatorGpu
  );
  replaceOptions(elements.defaultModel, modelOptions(), state.settings.defaultModel);
  replaceOptions(
    elements.defaultGpu,
    gpuOptions(false, state.settings.defaultGpu),
    state.settings.defaultGpu
  );
  elements.defaultContextTokens.value = state.settings.context.defaultContextTokens;
  elements.providerContextTokens.value = state.settings.context.providerContextTokens;
  elements.reservedResponseTokens.value = state.settings.context.reservedResponseTokens;
  elements.maxToolOutputTokens.value = state.settings.execution.maxToolOutputTokens;
  elements.generationTimeoutSeconds.value = state.settings.runtime.generationTimeoutSeconds;
  elements.maxConversationMessages.value = state.settings.context.maxConversationMessages;
  renderRuntimeProfilesEditor();
  elements.usageSelectedWindow.value = state.settings.usage.selectedWindow;
  elements.usageRetentionDays.value = state.settings.usage.retentionDays;
  elements.usageProviderShortMinutes.value =
    state.settings.usage.providerShortWindowMinutes;
  elements.usageProviderLongMinutes.value =
    state.settings.usage.providerLongWindowMinutes;
  elements.usageCustomMinutes.value =
    state.settings.usage.customRollingWindowMinutes;
  elements.usageAlertThresholds.value =
    state.settings.usage.alertThresholds.join(", ");
  for (const option of elements.usagePinnedWindows.options) {
    option.selected = state.settings.usage.pinnedWindows.includes(option.value);
  }
  replaceOptions(
    elements.usageComparisonModel,
    (state.pricingCatalog?.comparisons ?? []).map(
      entry => ({
        value: `${entry.providerId}|${entry.modelId}`,
        label: `${entry.providerId} · ${entry.modelId} · `
          + `${formatCurrency(entry.inputPricePerMillion)}/M input · `
          + `${formatCurrency(entry.outputPricePerMillion)}/M output`
      })
    ),
    `${state.settings.usage.comparisonProvider}|${state.settings.usage.comparisonModel}`
  );
  replaceOptions(
    elements.usageOllamaPlan,
    (state.pricingCatalog?.ollamaPlans ?? []).map(
      plan => ({
        value: plan.plan,
        label: `${plan.plan} · ${formatCurrency(plan.monthlyPrice)}/month`
      })
    ),
    state.settings.usage.ollamaPlanReference
  );
  replaceOptions(
    elements.modelTestSelector,
    modelOptions(),
    elements.modelTestSelector.value || state.settings.defaultModel
  );
  elements.intentionsGrid.replaceChildren();

  for (const [name, intention] of Object.entries(state.settings.intentions)) {
    elements.intentionsGrid.append(createIntentionCard(name, intention));
  }

  renderModelDiagnostics();
  renderCloudProviders();
  renderModelOrganization();
  renderModelProfiles();
  renderModelChainPreview();
  renderSettingsSummaries();
  renderUsageSummary();
}

function renderSettingsSummaries() {
  if (!state.settings) {
    return;
  }

  const active = activeWorkspaceProfile();
  const usage = state.sessions?.usage;
  elements.settingsWorkspaceSummary.textContent = active
    ? `Active workspace: ${active.name}\n`
      + `History: ${active.historyEnabled ? "enabled" : "disabled"}\n`
      + `Stored sessions: ${usage?.sessionCount ?? 0}\n`
      + `Storage: ${formatBytes(usage?.storageBytes ?? 0)}\n`
      + `Retention: ${state.settings.sessionHistory.maxSessionsPerWorkspace} sessions, `
      + `${formatBytes(state.settings.sessionHistory.maxSessionBytes)} each`
    : "No active workspace.";
  elements.settingsGitSummary.textContent = state.git?.state === "available"
    ? `Repository: ${state.git.repository?.repositoryRoot ?? "."}\n`
      + `Branch: ${state.git.repository?.detachedHead ? "detached HEAD" : state.git.repository?.branch ?? "unborn"}\n`
      + `Upstream: ${state.git.repository?.upstream ?? "not configured"}\n`
      + `Git: ${state.git.version ?? "unavailable"}\n`
      + `Identity: ${state.git.userName?.scope ?? "unset"} / ${state.git.userEmail?.scope ?? "unset"}`
    : state.git?.state === "not-initialized"
      ? "Git repository is not initialized for this workspace."
      : `Git unavailable: ${state.git?.diagnostic ?? "unknown"}`;
  elements.settingsValidationSummary.textContent =
    state.validationProfiles?.active
      ? `Active profile: ${state.validationProfiles.active.name}\n`
        + `${state.validationProfiles.active.steps.length} structured step(s)`
      : state.validationProfiles?.detected
        ? `Detected suggestion: ${state.validationProfiles.detected.name}\nNo user profile is active.`
        : "No validation profile is configured or detected.";
  elements.settingsAdvancedSummary.textContent =
    `Git diff limit: ${formatBytes(state.settings.gitDelivery.maxDiffBytesPerFile)} per file\n`
    + `Git log limit: ${state.settings.gitDelivery.maxLogEntries} entries\n`
    + `Process history output: ${formatBytes(state.settings.sessionHistory.maxStoredProcessOutputBytesPerTurn)} per turn\n`
    + `Execution tools: ${state.settings.execution.maxToolCallsPerTurn} per turn`;
}

function modelOptions() {
  const groups = {
    "ollama-local": "Local models",
    groq: "Groq",
    "google-ai-studio": "Google AI Studio",
    cerebras: "Cerebras"
  };

  return state.models.map(model => {
    const organized = organizedModel(model.name);
    const capabilities = model.capabilities;
    const badges = [
      capabilities?.nativeTools ? "tools" : null,
      capabilities?.vision ? "vision" : null,
      capabilities?.streaming ? "stream" : null
    ].filter(Boolean);

    return {
      value: model.name,
      label: `${organized?.alias ?? model.displayName ?? model.name}`
        + `${organized?.alias ? ` · ${model.name}` : ""}`
        + `${organized?.favorite ? " ★" : ""}`
        + `${badges.length ? ` · ${badges.join(" · ")}` : ""}`,
      group: groups[model.provider] ?? model.provider ?? "Local models",
      disabled: model.selectable === false,
      hidden: organized?.hidden === true,
      favorite: organized?.favorite === true,
      provider: model.provider ?? "ollama-local",
      exactId: organized?.modelId ?? model.name,
      title: capabilities?.source
        ? `Capacidades: ${capabilities.source}`
        : null
    };
  }).filter(
    option => !option.hidden
  ).sort(
    (left, right) =>
      left.provider.localeCompare(right.provider)
      || Number(right.favorite) - Number(left.favorite)
      || left.label.localeCompare(right.label, undefined, { sensitivity: "base" })
      || left.exactId.localeCompare(right.exactId)
  );
}

function organizedModel(qualifiedId) {
  return state.modelOrganization?.models?.find(
    model => model.qualifiedId === qualifiedId
  ) ?? null;
}

function renderModelOrganization() {
  if (!elements.modelOrganizationList) {
    return;
  }

  elements.modelOrganizationList.replaceChildren();
  const search = elements.modelFilterSearch.value.trim().toLocaleLowerCase();
  const location = elements.modelFilterLocation.value;
  const minimumContext = Number(elements.modelFilterContext.value) || 0;
  const models = (state.modelOrganization?.models ?? []).filter(
    model => {
      const capability = model.capabilities;
      return (elements.modelFilterHidden.checked || !model.hidden)
        && (!search
          || `${model.alias ?? ""} ${model.modelId} ${model.qualifiedId}`
            .toLocaleLowerCase()
            .includes(search))
        && (location === "all"
          || location === "local" && model.providerId === "ollama-local"
          || location === "cloud" && model.providerId !== "ollama-local")
        && (!elements.modelFilterTools.checked || capability?.nativeTools)
        && (!elements.modelFilterWeb.checked || capability?.webSearch)
        && (!elements.modelFilterVision.checked || capability?.vision)
        && (!elements.modelFilterStructured.checked || capability?.structuredOutput)
        && (!elements.modelFilterConformance.checked || model.conformanceApproved)
        && (!elements.modelFilterAvailable.checked || model.available)
        && (!elements.modelFilterFavorites.checked || model.favorite)
        && (minimumContext <= 0 || (capability?.contextTokens ?? 0) >= minimumContext);
    }
  );

  for (const model of models) {
    const card = document.createElement("article");
    card.className = "model-organization-card";
    card.dataset.modelIdentity = model.qualifiedId;
    const heading = document.createElement("div");
    heading.className = "model-organization-card-heading";
    const title = document.createElement("strong");
    title.textContent = model.alias ?? model.modelId;
    const exact = document.createElement("small");
    exact.textContent = `${providerLabel(model.providerId)} · ${model.modelId}`;
    const badges = document.createElement("span");
    badges.className = "model-organization-badges";

    for (const label of [
      model.favorite ? "★ favorito" : null,
      model.hidden ? "oculto" : null,
      model.available ? "available" : "unavailable",
      model.conformanceApproved ? "approved conformance" : null,
      model.capabilities?.nativeTools ? "tools" : null,
      model.capabilities?.webSearch ? "web" : null,
      model.capabilities?.vision ? "vision" : null,
      model.capabilities?.structuredOutput ? "structured" : null
    ].filter(Boolean)) {
      const badge = document.createElement("span");
      badge.className = "badge muted";
      badge.textContent = label;
      badges.append(badge);
    }

    heading.append(title, exact, badges);
    const fields = document.createElement("div");
    fields.className = "model-preference-fields";
    const alias = document.createElement("input");
    alias.type = "text";
    alias.maxLength = 80;
    alias.placeholder = "Local alias";
    alias.value = model.alias ?? "";
    alias.dataset.modelAlias = "";
    const note = document.createElement("input");
    note.type = "text";
    note.maxLength = 500;
    note.placeholder = "Optional note";
    note.value = model.note ?? "";
    note.dataset.modelNote = "";
    fields.append(alias, note);
    const actions = document.createElement("div");
    actions.className = "settings-action-row";

    for (const action of [
      {
        value: "favorite",
        label: model.favorite ? "Desfavoritar" : "Favoritar"
      },
      {
        value: "hidden",
        label: model.hidden ? "Reexibir" : "Ocultar"
      },
      {
        value: "save",
        label: "Save alias and note"
      }
    ]) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "secondary-button";
      button.dataset.modelOrganizationAction = action.value;
      button.dataset.providerId = model.providerId;
      button.dataset.modelId = model.modelId;
      button.textContent = action.label;
      actions.append(button);
    }

    card.append(heading, fields, actions);
    elements.modelOrganizationList.append(card);
  }

  if (models.length === 0) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent = "No model matches the filters.";
    elements.modelOrganizationList.append(empty);
  }
}

async function handleModelOrganizationAction(event) {
  const button = event.target.closest("[data-model-organization-action]");

  if (!button) {
    return;
  }

  const current = (state.modelOrganization?.models ?? []).find(
    model => model.providerId === button.dataset.providerId
      && model.modelId === button.dataset.modelId
  );

  if (!current) {
    return;
  }

  const card = button.closest(".model-organization-card");
  button.disabled = true;

  try {
    state.modelOrganization = await fetchJson(
      "/api/model-organization/preference",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          providerId: current.providerId,
          modelId: current.modelId,
          alias: card.querySelector("[data-model-alias]").value.trim() || null,
          note: card.querySelector("[data-model-note]").value.trim() || null,
          favorite: button.dataset.modelOrganizationAction === "favorite"
            ? !current.favorite
            : current.favorite,
          hidden: button.dataset.modelOrganizationAction === "hidden"
            ? !current.hidden
            : current.hidden
        })
      }
    );
    renderComposerModels();
    renderSettings();
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  } finally {
    button.disabled = false;
  }
}

function allProfileModelOptions(includeNone = false) {
  return [
    ...(includeNone
      ? [
        {
          value: "none",
          label: "None"
        }
      ]
      : []),
    ...(state.modelOrganization?.models ?? []).map(
      model => ({
        value: model.qualifiedId,
        label: `${model.alias ?? model.modelId}`
          + `${model.alias ? ` · ${model.qualifiedId}` : ""}`
          + `${model.available ? "" : " (unavailable)"}`,
        group: providerLabel(model.providerId),
        disabled: !model.available
      })
    )
  ];
}

function renderModelProfiles() {
  const profiles = state.modelOrganization?.profiles ?? [];
  const selected = elements.modelProfileSelector.value;
  replaceOptions(
    elements.modelProfileSelector,
    [
      {
        value: "",
        label: "New profile"
      },
      ...profiles.map(
        profile => ({
          value: profile.id,
          label: profile.name
        })
      )
    ],
    profiles.some(profile => profile.id === selected)
      ? selected
      : ""
  );
  replaceOptions(
    elements.modelProfilePrimary,
    allProfileModelOptions(),
    elements.modelProfilePrimary.value || state.settings?.defaultModel
  );
  replaceOptions(
    elements.modelProfileFallback,
    allProfileModelOptions(true),
    elements.modelProfileFallback.value || "none"
  );
  replaceOptions(
    elements.modelProfileRouter,
    allProfileModelOptions(),
    elements.modelProfileRouter.value || state.settings?.routerModel
  );
  replaceOptions(
    elements.modelProfileCoordinator,
    allProfileModelOptions(),
    elements.modelProfileCoordinator.value || state.settings?.coordinatorModel
  );
  replaceOptions(
    elements.workspaceModelProfile,
    [
      {
        value: "",
        label: "No preferred profile"
      },
      ...profiles.map(
        profile => ({
          value: profile.id,
          label: profile.name
        })
      )
    ],
    activeWorkspaceProfile()?.preferredModelProfileId ?? ""
  );
  elements.workspaceModelProfile.disabled = !activeWorkspaceProfile();
  elements.applyModelProfile.disabled = !elements.modelProfileSelector.value;
  elements.deleteModelProfile.disabled = !elements.modelProfileSelector.value;
}

function loadSelectedModelProfile() {
  const profile = (state.modelOrganization?.profiles ?? []).find(
    item => item.id === elements.modelProfileSelector.value
  );

  if (!profile) {
    elements.modelProfileName.value = "";
    elements.modelProfilePreview.textContent =
      "Fill in the fields and save to generate the authoritative view.";
    renderModelProfiles();
    return;
  }

  elements.modelProfileName.value = profile.name;
  replaceOptions(
    elements.modelProfilePrimary,
    allProfileModelOptions(),
    profile.primaryModel
  );
  replaceOptions(
    elements.modelProfileFallback,
    allProfileModelOptions(true),
    profile.fallbackModel
  );
  replaceOptions(
    elements.modelProfileRouter,
    allProfileModelOptions(),
    profile.routerModel
  );
  replaceOptions(
    elements.modelProfileCoordinator,
    allProfileModelOptions(),
    profile.coordinatorModel
  );
  elements.modelProfileWeb.value = profile.webPreference;
  elements.modelProfileUsageWindow.value = profile.usageWindow ?? "";
  renderModelProfiles();
  void previewModelProfile(profile.id);
}

async function saveModelProfile() {
  elements.saveModelProfile.disabled = true;
  elements.modelProfileStatus.textContent = "Validating and saving profile…";

  try {
    const preview = await fetchJson(
      "/api/model-organization/profiles",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          id: elements.modelProfileSelector.value || null,
          name: elements.modelProfileName.value.trim(),
          primaryModel: elements.modelProfilePrimary.value,
          fallbackModel: elements.modelProfileFallback.value,
          routerModel: elements.modelProfileRouter.value,
          coordinatorModel: elements.modelProfileCoordinator.value,
          webPreference: elements.modelProfileWeb.value,
          comparisonModel: null,
          usageWindow: elements.modelProfileUsageWindow.value || null
        })
      }
    );
    state.modelOrganization = await fetchJson("/api/model-organization");
    renderModelProfiles();
    elements.modelProfileSelector.value = preview.profileId;
    loadSelectedModelProfile();
    renderProfilePreview(preview);
    elements.modelProfileStatus.textContent = "Profile saved without starting a model.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  } finally {
    elements.saveModelProfile.disabled = false;
  }
}

async function previewModelProfile(profileId) {
  try {
    renderProfilePreview(
      await fetchJson(
        `/api/model-organization/profiles/${encodeURIComponent(profileId)}/preview`
      )
    );
  } catch (error) {
    elements.modelProfilePreview.textContent = error.message;
  }
}

function renderProfilePreview(preview) {
  elements.modelProfilePreview.textContent = [
    ...preview.chain.map(
      item =>
        `${item.role.toUpperCase()}\n`
        + `${providerLabel(item.providerId)} · ${item.exactModelId}\n`
        + `${item.alias ? `Alias: ${item.alias}\n` : ""}`
        + `Available: ${item.available ? "yes" : "no"} · `
        + `Conformance: ${item.conformanceApproved ? "approved" : "not approved"} · `
        + `Tools: ${item.toolPath} · Web: ${item.web ? "yes" : "no"} · `
        + `Vision: ${item.vision ? "yes" : "no"}`
    ),
    `Local fallback: ${preview.localFallbackValid ? "valid" : "invalid"}`,
    `Affected workspaces: ${preview.affectedWorkspaces.join(", ") || "none"}`,
    ...preview.errors.map(error => `ERROR: ${error}`)
  ].join("\n\n");
}

async function applyModelProfile() {
  const profileId = elements.modelProfileSelector.value;

  if (!profileId || !await showAppConfirm(
    "Apply this profile atomically to new requests? The current conversation will not restart.",
    { title: "Apply profile?", confirmLabel: "Apply" }
  )) {
    return;
  }

  elements.applyModelProfile.disabled = true;

  try {
    const preview = await fetchJson(
      `/api/model-organization/profiles/${encodeURIComponent(profileId)}/apply`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          confirmed: true
        })
      }
    );
    state.settings = await fetchJson("/api/settings");
    renderSettings();
    renderProfilePreview(preview);
    elements.modelProfileStatus.textContent =
      "Profile applied. The current conversation selection was preserved.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  } finally {
    elements.applyModelProfile.disabled = false;
  }
}

async function deleteModelProfile() {
  const profileId = elements.modelProfileSelector.value;

  if (!profileId || !await showAppConfirm(
    "Delete this saved profile?",
    { title: "Delete profile?", confirmLabel: "Delete", danger: true }
  )) {
    return;
  }

  try {
    state.modelOrganization = await fetchJson(
      `/api/model-organization/profiles/${encodeURIComponent(profileId)}`,
      {
        method: "DELETE"
      }
    );
    elements.modelProfileSelector.value = "";
    loadSelectedModelProfile();
    elements.modelProfileStatus.textContent = "Profile deleted.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  }
}

async function saveWorkspaceModelProfile() {
  const workspace = activeWorkspaceProfile();

  if (!workspace) {
    return;
  }

  try {
    state.workspaceProfiles = await fetchJson(
      `/api/model-organization/workspaces/${encodeURIComponent(workspace.id)}/preferred-profile`,
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          profileId: elements.workspaceModelProfile.value || null
        })
      }
    );
    elements.modelProfileStatus.textContent =
      "Workspace preference saved by reference.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  }
}

function renderModelChainPreview() {
  if (!state.settings || !elements.modelChainPreview) {
    return;
  }

  const generalFallback =
    document.querySelector('[data-intention="general-chat"] .intention-fallback-model')
      ?.value
    ?? state.settings.intentions["general-chat"]?.fallbackModel
    ?? "none";
  const roles = [
    {
      role: "PRIMARY",
      model: elements.defaultModel.value || state.settings.defaultModel
    },
    {
      role: "FALLBACK",
      model: generalFallback
    },
    {
      role: "ROUTER",
      model: elements.routerModel.value || state.settings.routerModel
    },
    {
      role: "COORDINATOR",
      model: elements.coordinatorModel.value || state.settings.coordinatorModel
    }
  ];
  elements.modelChainPreview.textContent = roles.map(
    item => {
      if (item.model === "none") {
        return `${item.role}\nNenhum`;
      }

      const model = organizedModel(item.model);
      const reference = parseModelReference(item.model);
      return `${item.role}\n`
        + `${providerLabel(reference.provider)} · ${reference.model}\n`
        + `${model?.alias ? `Alias: ${model.alias} · ` : ""}`
        + `${model?.available ? "available" : "unavailable"} · `
        + `conformance ${model?.conformanceApproved ? "approved" : "not approved"} · `
        + `tools ${model?.capabilities?.nativeTools ? "yes" : "no"} · `
        + `web ${model?.capabilities?.webSearch ? "yes" : "no"} · `
        + `vision ${model?.capabilities?.vision ? "yes" : "no"}`;
    }
  ).join("\n\n");
}

function renderCloudProviders() {
  elements.cloudProvidersList.replaceChildren();

  const localHealth = (state.providerHealth?.providers ?? []).find(
    provider => provider.providerId.startsWith("ollama")
  );
  if (localHealth) {
    elements.cloudProvidersList.append(
      createProviderHealthCard(localHealth)
    );
  }

  for (const provider of state.cloudProviders?.providers ?? []) {
    const health = (state.providerHealth?.providers ?? []).find(
      item => item.providerId === provider.provider
    );
    const card = document.createElement("details");
    card.className = "cloud-provider-card";
    card.dataset.provider = provider.provider;
    card.open = state.openCloudProviders.has(provider.provider);
    card.addEventListener("toggle", () => {
      if (card.open) {
        state.openCloudProviders.add(provider.provider);
      } else {
        state.openCloudProviders.delete(provider.provider);
      }
    });
    const summary = document.createElement("summary");
    const title = document.createElement("span");
    title.className = "cloud-provider-title";
    title.textContent = provider.displayName;
    const status = document.createElement("span");
    status.className = `badge ${provider.connectionState === "connected"
      ? "success"
      : provider.connectionState === "error"
        ? "error"
        : "muted"}`;
    status.textContent = cloudConnectionLabel(provider.connectionState);
    summary.append(title, status);

    const body = document.createElement("div");
    body.className = "cloud-provider-body";
    const metadata = document.createElement("dl");
    metadata.className = "cloud-provider-metadata";
    appendDefinition(metadata, "Active", provider.enabled ? "Yes" : "No");
    appendDefinition(metadata, "Key", provider.maskedKeyState);
    appendDefinition(metadata, "Models", String(provider.modelCount));
    appendDefinition(
      metadata,
      "Last update",
      provider.lastRefreshAt
        ? new Date(provider.lastRefreshAt).toLocaleString(window.AgenticRouterI18n.locale)
        : "Not updated yet"
    );
    appendDefinition(metadata, "Quota", provider.quotaSource);
    if (health) {
      appendDefinition(
        metadata,
        "Health",
        providerHealthStateLabel(health.connectionState)
      );
      appendDefinition(
        metadata,
        "Last success",
        formatProviderHealthDate(health.lastSuccessfulRequest)
      );
      appendDefinition(
        metadata,
        "Latency",
        health.totalLatencyMilliseconds == null
          ? "unavailable"
          : `${formatInteger(health.totalLatencyMilliseconds)} ms`
      );
      appendDefinition(metadata, "Usage", usageAccuracyLabel(health.tokenUsageAccuracy));
    }

    const billingField = document.createElement("label");
    billingField.className = "cloud-billing-field";
    const billingLabel = document.createElement("span");
    billingLabel.textContent = "Expected billing mode";
    const billingSelect = document.createElement("select");
    billingSelect.dataset.cloudBilling = provider.provider;
    replaceOptions(
      billingSelect,
      [
        {
          value: "unknown",
          label: "Unknown"
        },
        {
          value: "free-tier",
          label: "Free tier"
        },
        {
          value: "paid",
          label: "Paid"
        }
      ],
      provider.expectedBillingMode ?? "unknown"
    );
    billingField.append(billingLabel, billingSelect);

    const keyField = document.createElement("label");
    const keyLabel = document.createElement("span");
    keyLabel.textContent = provider.hasKey ? "Replace key" : "API key";
    const keyInput = document.createElement("input");
    keyInput.type = "password";
    keyInput.autocomplete = "new-password";
    keyInput.dataset.cloudKey = provider.provider;
    keyInput.dataset.ignoreSettingsDirty = "";
    keyInput.placeholder = provider.hasKey
      ? "Enter a new key"
      : "Enter the key";
    keyField.append(keyLabel, keyInput);

    const actions = document.createElement("div");
    actions.className = "settings-action-row";
    actions.append(
      cloudActionButton(
        provider.provider,
        "save-key",
        provider.hasKey ? "Replace" : "Save key"
      ),
      cloudActionButton(provider.provider, "test", "Test connection"),
      cloudActionButton(provider.provider, "refresh", "Refresh models"),
      cloudActionButton(
        provider.provider,
        "remove-key",
        "Remove key",
        "danger-button"
      )
    );
    actions.querySelector('[data-cloud-action="test"]').disabled = !provider.hasKey;
    actions.querySelector('[data-cloud-action="refresh"]').disabled = !provider.hasKey;
    actions.querySelector('[data-cloud-action="remove-key"]').disabled = !provider.hasKey;

    const diagnostic = document.createElement("p");
    diagnostic.className = "runtime-note cloud-provider-diagnostic";
    diagnostic.dataset.cloudDiagnostic = provider.provider;
    diagnostic.textContent = provider.diagnostic ?? "";
    const fields = document.createElement("div");
    fields.className = "cloud-provider-fields";
    fields.append(keyField, billingField);
    body.append(metadata, fields, actions, diagnostic);
    card.append(summary, body);
    elements.cloudProvidersList.append(card);
  }

  renderOllamaWebSearchSettings();
}

function createProviderHealthCard(provider) {
  const card = document.createElement("details");
  card.className = "cloud-provider-card provider-health-card";
  card.dataset.provider = provider.providerId;
  card.dataset.state = provider.connectionState;
  card.open = state.openCloudProviders.has(provider.providerId);
  card.addEventListener("toggle", () => {
    if (card.open) {
      state.openCloudProviders.add(provider.providerId);
    } else {
      state.openCloudProviders.delete(provider.providerId);
    }
  });
  const summary = document.createElement("summary");
  const title = document.createElement("span");
  title.className = "cloud-provider-title";
  title.textContent = provider.displayName;
  const badge = document.createElement("span");
  badge.className = `badge ${provider.connectionState === "healthy"
    ? "success"
    : ["degraded", "unavailable"].includes(provider.connectionState)
      ? "error"
      : "muted"}`;
  badge.textContent = providerHealthStateLabel(provider.connectionState);
  summary.append(title, badge);
  const body = document.createElement("div");
  body.className = "cloud-provider-body";
  const metadata = document.createElement("dl");
  metadata.className = "cloud-provider-metadata";
  appendDefinition(metadata, "Last success", formatProviderHealthDate(provider.lastSuccessfulRequest));
  appendDefinition(
    metadata,
    "Latency",
    provider.totalLatencyMilliseconds == null
      ? "unavailable"
      : `${formatInteger(provider.totalLatencyMilliseconds)} ms`
  );
  appendDefinition(metadata, "Quota", provider.quotaState);
  appendDefinition(metadata, "Usage", usageAccuracyLabel(provider.tokenUsageAccuracy));
  const diagnostic = document.createElement("pre");
  diagnostic.className = "provider-health-diagnostic";
  diagnostic.textContent = [
    `status: ${provider.diagnostic.lastStatusCode ?? "unavailable"}`,
    `retry: ${provider.diagnostic.retryDecision}`,
    `source: ${provider.healthSource}`,
    `stale: ${provider.stale ? "yes" : "no"}`
  ].join("\n");
  body.append(metadata, diagnostic);
  card.append(summary, body);
  return card;
}

function renderOllamaWebSearchSettings() {
  const integration = state.webSearch;

  if (!integration) {
    return;
  }

  const card = document.createElement("details");
  card.className = "cloud-provider-card";
  card.dataset.provider = integration.provider;
  card.open = state.openCloudProviders.has(integration.provider);
  card.addEventListener("toggle", () => {
    if (card.open) {
      state.openCloudProviders.add(integration.provider);
    } else {
      state.openCloudProviders.delete(integration.provider);
    }
  });
  const summary = document.createElement("summary");
  const title = document.createElement("span");
  title.className = "cloud-provider-title";
  title.textContent = integration.displayName;
  const status = document.createElement("span");
  status.className = `badge ${integration.state === "available"
    ? "success"
    : "muted"}`;
  status.textContent = integration.state === "available"
    ? "Available"
    : "Not configured";
  summary.append(title, status);

  const body = document.createElement("div");
  body.className = "cloud-provider-body";
  const note = document.createElement("p");
  note.className = "runtime-note";
  note.textContent =
    "Read-only search for local models. It is separate from Ollama Cloud models "
      + "and is used only when Web is explicitly enabled in the Composer.";
  const keyField = document.createElement("label");
  const keyLabel = document.createElement("span");
  keyLabel.textContent = integration.hasKey
    ? "Replace key"
    : "Separate API key";
  const keyInput = document.createElement("input");
  keyInput.type = "password";
  keyInput.autocomplete = "new-password";
  keyInput.dataset.webSearchKey = "";
  keyInput.dataset.ignoreSettingsDirty = "";
  keyInput.placeholder = integration.hasKey
    ? "Enter a new key"
    : "OLLAMA_API_KEY";
  keyField.append(keyLabel, keyInput);
  const actions = document.createElement("div");
  actions.className = "settings-action-row";
  const save = document.createElement("button");
  save.type = "button";
  save.className = "secondary-button";
  save.dataset.webSearchAction = "save-key";
  save.textContent = integration.hasKey ? "Replace" : "Save key";
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "secondary-button danger-button";
  remove.dataset.webSearchAction = "remove-key";
  remove.textContent = "Remove key";
  remove.disabled = !integration.hasKey;
  actions.append(save, remove);
  const diagnostic = document.createElement("p");
  diagnostic.className = "runtime-note cloud-provider-diagnostic";
  diagnostic.dataset.webSearchDiagnostic = "";
  diagnostic.textContent = integration.diagnostic ?? "";
  body.append(note, keyField, actions, diagnostic);
  card.append(summary, body);
  elements.cloudProvidersList.append(card);
}

function cloudActionButton(provider, action, label, extraClass = "") {
  const button = document.createElement("button");
  button.type = "button";
  button.className = `secondary-button ${extraClass}`.trim();
  button.dataset.cloudProvider = provider;
  button.dataset.cloudAction = action;
  button.textContent = label;
  return button;
}

function collectCloudProviderSettings() {
  const cloud = structuredClone(state.settings.cloudProviders);
  const keys = {
    groq: "groq",
    "google-ai-studio": "googleAiStudio",
    cerebras: "cerebras"
  };

  for (const select of elements.cloudProvidersList.querySelectorAll(
    "[data-cloud-billing]"
  )) {
    const key = keys[select.dataset.cloudBilling];

    if (key && cloud[key]) {
      cloud[key].expectedBillingMode = select.value;
    }
  }

  return cloud;
}

function appendDefinition(list, term, description) {
  const dt = document.createElement("dt");
  dt.textContent = term;
  const dd = document.createElement("dd");
  dd.textContent = description;
  list.append(dt, dd);
}

function cloudConnectionLabel(stateName) {
  return {
    connected: "Connected",
    error: "Error",
    disabled: "Disabled",
    "key-required": "Key required",
    "not-tested": "Not tested"
  }[stateName] ?? stateName;
}

async function handleCloudProviderAction(event) {
  const webButton = event.target.closest("[data-web-search-action]");

  if (webButton) {
    await handleOllamaWebSearchAction(webButton);
    return;
  }

  const button = event.target.closest("[data-cloud-action]");

  if (!button) {
    return;
  }

  const provider = button.dataset.cloudProvider;
  const action = button.dataset.cloudAction;
  const diagnostic = elements.cloudProvidersList.querySelector(
    `[data-cloud-diagnostic="${CSS.escape(provider)}"]`
  );
  let path;
  let options;

  if (action === "save-key") {
    const input = elements.cloudProvidersList.querySelector(
      `[data-cloud-key="${CSS.escape(provider)}"]`
    );
    const apiKey = input.value.trim();

    if (!apiKey) {
      diagnostic.textContent = "Enter an API key before saving.";
      input.classList.add("field-invalid");
      input.setAttribute("aria-invalid", "true");
      showToast(diagnostic.textContent);
      input.focus();
      return;
    }

    path = `/api/cloud-providers/${encodeURIComponent(provider)}/key`;
    options = {
      method: "PUT",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ apiKey })
    };
  } else if (action === "remove-key") {
    if (!await showAppConfirm(
      "Permanently remove this provider's protected key?",
      { title: "Remove key?", confirmLabel: "Remove", danger: true }
    )) {
      return;
    }

    path = `/api/cloud-providers/${encodeURIComponent(provider)}/key?confirmed=true`;
    options = { method: "DELETE" };
  } else {
    const operation = action === "test" ? "test the connection" : "refresh models";

    if (!await showAppConfirm(
      `Allow a real provider call for ${operation}? This action may consume quota.`,
      { title: "Authorize provider call?", confirmLabel: "Authorize" }
    )) {
      return;
    }

    path = action === "test"
      ? `/api/cloud-providers/${encodeURIComponent(provider)}/test`
      : `/api/cloud-providers/${encodeURIComponent(provider)}/models/refresh`;
    options = { method: "POST" };
  }

  button.disabled = true;
  diagnostic.textContent = "Processing…";

  try {
    await fetchJson(path, options);
    await refreshCloudProviderState();
    showToast("Provider action completed.", "success");
  } catch (error) {
    diagnostic.textContent = error.message;
    showToast(error.message);
  } finally {
    button.disabled = false;
  }
}

async function handleOllamaWebSearchAction(button) {
  const action = button.dataset.webSearchAction;
  const diagnostic = elements.cloudProvidersList.querySelector(
    "[data-web-search-diagnostic]"
  );
  let path;
  let options;

  if (action === "save-key") {
    const input = elements.cloudProvidersList.querySelector(
      "[data-web-search-key]"
    );
    const apiKey = input.value.trim();

    if (!apiKey) {
      diagnostic.textContent = "Enter the separate key before saving.";
      input.classList.add("field-invalid");
      input.setAttribute("aria-invalid", "true");
      showToast(diagnostic.textContent);
      input.focus();
      return;
    }

    path = "/api/web-search/key";
    options = {
      method: "PUT",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ apiKey })
    };
  } else {
    if (!await showAppConfirm(
      "Permanently remove the protected Ollama Web Search key?",
      { title: "Remove key?", confirmLabel: "Remove", danger: true }
    )) {
      return;
    }

    path = "/api/web-search/key?confirmed=true";
    options = { method: "DELETE" };
  }

  button.disabled = true;
  diagnostic.textContent = "Processing…";

  try {
    state.webSearch = await fetchJson(path, options);
    renderCloudProviders();
    await refreshSelectedModelCapabilities();
  } catch (error) {
    diagnostic.textContent = error.message;
    showToast(error.message);
  } finally {
    button.disabled = false;
  }
}

async function refreshCloudProviderState() {
  const [
    cloudProviders,
    modelsResponse,
    settings,
    cloudUsageDashboard,
    webSearch
  ] = await Promise.all([
    fetchJson("/api/cloud-providers"),
    fetchJson("/api/models"),
    fetchJson("/api/settings"),
    fetchJson("/api/usage/cloud-dashboard"),
    fetchJson("/api/web-search")
  ]);
  state.cloudProviders = cloudProviders;
  state.models = modelsResponse.models;
  state.settings = settings;
  state.cloudUsageDashboard = cloudUsageDashboard;
  state.webSearch = webSearch;
  renderCloudProviders();
  renderComposerModels();
  renderSettings();
  renderCloudUsage();
  await refreshSelectedModelCapabilities();
  await refreshProviderHealth();
}

function gpuOptions(includeDefault, selected = null) {
  const options = includeDefault
    ? [
      {
        value: "default",
        label: "Default"
      }
    ]
    : [];

  for (const device of state.devices) {
    if (!device.isAuto && device.ollamaIndex == null) {
      continue;
    }

    options.push({
      value: device.isAuto ? "auto" : `ollama:${device.ollamaIndex}`,
      label: device.isAuto
        ? "Auto"
        : `CUDA ${device.ollamaIndex} · ${device.name}`
    });
  }

  if (
    selected
    && /^ollama:\d+$/.test(selected)
    && !options.some(option => option.value === selected)
  ) {
    options.push({
      value: selected,
      label: `CUDA ${selected.slice("ollama:".length)} · configured, unavailable`
    });
  }

  return options;
}

function createIntentionCard(name, intention) {
  const card = document.createElement("article");
  card.className = "intention-card";
  card.dataset.intention = name;
  const heading = document.createElement("h4");
  heading.textContent = name;
  const selects = document.createElement("div");
  selects.className = "intention-selects";
  selects.append(
    createSelectField(
      "Model",
      "intention-model",
      [
        {
          value: "default",
          label: "Default"
        },
        ...modelOptions()
      ],
      intention.model
    ),
    createSelectField(
      "Fallback",
      "intention-fallback-model",
      [
        {
          value: "none",
          label: "None"
        },
        {
          value: "default",
          label: "Default"
        },
        ...modelOptions()
      ],
      intention.fallbackModel ?? "none"
    ),
    createSelectField(
      "GPU",
      "intention-gpu",
      gpuOptions(true, intention.gpu),
      intention.gpu
    )
  );
  const promptField = document.createElement("label");
  const promptLabel = document.createElement("span");
  promptLabel.textContent = "System prompt";
  const prompt = document.createElement("textarea");
  prompt.className = "intention-prompt";
  prompt.required = true;
  prompt.value = intention.systemPrompt;
  promptField.append(promptLabel, prompt);
  card.append(heading, selects, promptField);
  return card;
}

function createSelectField(labelText, className, options, selected) {
  const label = document.createElement("label");
  const text = document.createElement("span");
  text.textContent = labelText;
  const select = document.createElement("select");
  select.className = className;
  replaceOptions(select, options, selected);
  label.append(text, select);
  return label;
}

function replaceOptions(select, options, selected) {
  const normalized = [...options];

  if (selected && !normalized.some(option => option.value === selected)) {
    normalized.push({
      value: selected,
      label: `${selected} (unavailable)`,
      group: "Current configuration"
    });
  }

  const nodes = [];
  const groups = new Map();

  for (const option of normalized) {
    let parent = null;

    if (option.group) {
      parent = groups.get(option.group);

      if (!parent) {
        parent = document.createElement("optgroup");
        parent.label = option.group;
        groups.set(
          option.group,
          parent
        );
        nodes.push(
          parent
        );
      }
    }

    const element = document.createElement("option");
    element.value = option.value;
    element.textContent = option.label;
    element.disabled = Boolean(option.disabled);

    if (option.title) {
      element.title = option.title;
    }

    if (parent) {
      parent.append(
        element
      );
    } else {
      nodes.push(
        element
      );
    }
  }

  select.replaceChildren(
    ...nodes
  );
  select.value = selected;
}

function renderModelDiagnostics() {
  if (!state.modelDiagnostics) {
    elements.modelDiagnosticsList.replaceChildren();
    elements.modelContextDiagnostic.textContent = "Loading diagnostic…";
    return;
  }

  elements.modelContextDiagnostic.textContent =
    state.modelDiagnostics.contextDiagnostic;
  elements.modelDiagnosticsList.replaceChildren(
    ...state.modelDiagnostics.models.map(
      diagnostic => {
        const row = document.createElement("div");
        row.className = "model-diagnostic-row";
        const configuration = document.createElement("span");
        configuration.textContent = diagnostic.configuration;
        const model = document.createElement("strong");
        model.textContent = diagnostic.resolvedModel
          ?? diagnostic.configuredValue
          ?? "—";
        const status = document.createElement("span");
        status.className = `model-status ${diagnostic.status.toLowerCase()}`;
        status.textContent = diagnostic.status;
        row.append(configuration, model, status);
        return row;
      }
    )
  );
}

async function testSelectedModel() {
  const model = elements.modelTestSelector.value;
  elements.testModel.disabled = true;
  elements.modelTestResult.textContent = `Testing ${model}…`;

  try {
    const result = await fetchJson(
      "/api/models/test",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          model
        })
      }
    );
    elements.modelTestResult.textContent = result.connected
      ? `${result.model} · Completed · Time to first chunk: `
        + `${result.timeToFirstChunkMilliseconds ?? "unavailable"} ms · `
        + `Total duration: ${result.totalDurationMilliseconds} ms`
      : `${result.model} · Failed · ${result.error} · Trace ID: ${result.traceId}`;
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderModelDiagnostics();
  } catch (error) {
    elements.modelTestResult.textContent = error.message;
  } finally {
    elements.testModel.disabled = false;
  }
}

async function openSettings(section = "general") {
  elements.runtimeDetails.open = false;
  elements.settingsErrors.hidden = true;
  elements.saveStatus.textContent = "";
  elements.modelTestResult.textContent = "";
  renderSettings();
  elements.settingsDialog.showModal();
  state.settingsDirty = false;
  updateSettingsDirtyState();
  setSettingsSection(section, false);
  document.querySelector(
    `[data-settings-target="${normalizeSettingsSection(section)}"]`
  )?.focus();
  await refreshSetupStatus({ quiet: true });

  try {
    [
      state.modelDiagnostics,
      state.runtimeProfiles
    ] = await Promise.all([
      fetchJson("/api/models/diagnostics"),
      fetchJson("/api/runtime/profiles")
    ]);
    renderModelDiagnostics();
    renderRuntimeProfilesEditor();
  } catch (error) {
    elements.modelContextDiagnostic.textContent = error.message;
    elements.runtimeProfileResult.textContent = error.message;
  }

  await loadPortableYaml();
}

async function closeSettings() {
  if (
    state.settingsDirty
    && !await showAppConfirm(
      "Discard the unsaved configuration changes?",
      { title: "Close without saving?", confirmLabel: "Discard", danger: true }
    )
  ) {
    return;
  }
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  document.querySelector("#open-settings").focus();
}

async function saveSettings(event) {
  event.preventDefault();
  elements.settingsErrors.hidden = true;
  clearSettingsValidationMarkers();
  elements.saveStatus.textContent = "Saving…";
  const intentions = {};

  for (const card of elements.intentionsGrid.querySelectorAll(".intention-card")) {
    intentions[card.dataset.intention] = {
      model: card.querySelector(".intention-model").value,
      fallbackModel: card.querySelector(".intention-fallback-model").value,
      gpu: card.querySelector(".intention-gpu").value,
      systemPrompt: card.querySelector(".intention-prompt").value
    };
  }

  const nextSettings = {
    schemaVersion: state.settings.schemaVersion,
    ollamaUrl: elements.ollamaUrl.value.trim(),
    routerModel: elements.routerModel.value,
    routerGpu: elements.routerGpu.value,
    actionModel: elements.actionModel.value,
    actionGpu: elements.actionGpu.value,
    coordinatorModel: elements.coordinatorModel.value,
    coordinatorGpu: elements.coordinatorGpu.value,
    defaultModel: elements.defaultModel.value,
    defaultGpu: elements.defaultGpu.value,
    trustedWorkspacePath: state.settings.trustedWorkspacePath ?? null,
    intentions,
    context: {
      defaultContextTokens: Number(elements.defaultContextTokens.value),
      providerContextTokens: Number(elements.providerContextTokens.value),
      reservedResponseTokens: Number(elements.reservedResponseTokens.value),
      maxConversationMessages: Number(elements.maxConversationMessages.value)
    },
    runtime: {
      ...state.settings.runtime,
      generationTimeoutSeconds: Number(elements.generationTimeoutSeconds.value)
    },
    ollamaRuntime: collectOllamaRuntimeSettings(),
    execution: {
      ...state.settings.execution,
      maxToolOutputTokens: Number(elements.maxToolOutputTokens.value)
    },
    projectAwareness: state.settings.projectAwareness,
    validationProfile: state.validationProfiles?.active ?? null,
    sessionHistory: state.settings.sessionHistory,
    gitDelivery: state.settings.gitDelivery,
    usage: {
      ...state.settings.usage,
      retentionDays: Number(elements.usageRetentionDays.value),
      selectedWindow: elements.usageSelectedWindow.value,
      pinnedWindows: Array.from(
        elements.usagePinnedWindows.selectedOptions,
        option => option.value
      ),
      providerShortWindowMinutes: Number(elements.usageProviderShortMinutes.value),
      providerLongWindowMinutes: Number(elements.usageProviderLongMinutes.value),
      customRollingWindowMinutes: Number(elements.usageCustomMinutes.value),
      alertThresholds: elements.usageAlertThresholds.value
        .split(",")
        .map(value => Number(value.trim()))
        .filter(value => Number.isInteger(value)),
      comparisonProvider: elements.usageComparisonModel.value.split("|")[0],
      comparisonModel: elements.usageComparisonModel.value.split("|")[1],
      ollamaPlanReference: elements.usageOllamaPlan.value
    },
    cloudProviders: collectCloudProviderSettings(),
    webSearch: state.settings.webSearch,
    modelOrganization: state.settings.modelOrganization
  };

  try {
    state.settings = await fetchJson(
      "/api/settings",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify(nextSettings)
      }
    );
    elements.saveStatus.textContent = "Saved";
    state.settingsDirty = false;
    updateSettingsDirtyState();
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderSettings();
    await refreshRuntimeStatus();
    scheduleRuntimeRefresh();
  } catch (error) {
    const errors = error.payload?.errors;
    const message = errors
      ? Object.entries(errors)
        .flatMap(([field, messages]) => messages.map(message => `${field}: ${message}`))
        .join("\n")
      : error.message;
    elements.settingsErrors.textContent = message;
    markSettingsValidationErrors(errors ?? {});
    showToast(message, "error", 30000);
    elements.saveStatus.textContent = "";
    navigateToSettingsError(
      Object.keys(errors ?? {})[0]
    );
  }
}

function handleSettingsInput(event) {
  event.target.classList.remove("field-invalid");
  event.target.removeAttribute("aria-invalid");
  event.target.closest(".intention-card")?.classList.remove("field-invalid-card");
  if (
    event.target.closest("[data-ignore-settings-dirty]")
    ||
    event.target.id === "model-test-selector"
    || event.target.closest(".model-test-panel") && event.target.tagName === "BUTTON"
  ) {
    return;
  }
  state.settingsDirty = true;
  updateSettingsDirtyState();
  renderModelChainPreview();
}

function clearSettingsValidationMarkers() {
  elements.settingsForm.querySelectorAll(".field-invalid").forEach(
    field => {
      field.classList.remove("field-invalid");
      field.removeAttribute("aria-invalid");
    }
  );
  elements.settingsForm.querySelectorAll(".field-invalid-card").forEach(
    card => card.classList.remove("field-invalid-card")
  );
}

function markSettingsValidationErrors(errors) {
  for (const field of Object.keys(errors)) {
    let control = null;
    const intention = field.match(/^intentions[.:]([^.:]+)/i)?.[1];
    if (intention) {
      const card = elements.intentionsGrid.querySelector(
        `[data-intention="${CSS.escape(intention)}"]`
      );
      control = field.toLowerCase().endsWith("gpu")
        ? card?.querySelector(".intention-gpu")
        : field.toLowerCase().includes("fallback")
          ? card?.querySelector(".intention-fallback-model")
          : card?.querySelector(".intention-model");
      card?.classList.add("field-invalid-card");
    } else if (field === "routerGpu") {
      control = elements.routerGpu;
    } else if (field.startsWith("router")) {
      control = elements.routerModel;
    } else if (field === "actionGpu") {
      control = elements.actionGpu;
    } else if (field.startsWith("action")) {
      control = elements.actionModel;
    } else if (field === "coordinatorGpu") {
      control = elements.coordinatorGpu;
    } else if (field.startsWith("coordinator")) {
      control = elements.coordinatorModel;
    } else if (field.startsWith("defaultModel")) {
      control = elements.defaultModel;
    } else {
      const fieldControls = {
        "context.defaultContextTokens": elements.defaultContextTokens,
        "context.providerContextTokens": elements.providerContextTokens,
        "context.reservedResponseTokens": elements.reservedResponseTokens,
        "context.maxConversationMessages": elements.maxConversationMessages,
        "execution.maxToolOutputTokens": elements.maxToolOutputTokens,
        "runtime.generationTimeoutSeconds": elements.generationTimeoutSeconds,
        defaultGpu: elements.defaultGpu,
        ollamaUrl: elements.ollamaUrl
      };
      control = fieldControls[field] ?? null;
    }
    if (control) {
      control.classList.add("field-invalid");
      control.setAttribute("aria-invalid", "true");
    }
  }
}

function updateSettingsDirtyState() {
  elements.settingsDirty.textContent = state.settingsDirty
    ? "Unsaved changes"
    : "No changes";
  elements.settingsDirty.className =
    `badge ${state.settingsDirty ? "error" : "muted"}`;
  elements.saveSettings.disabled =
    Boolean(state.recovery?.settingsReadOnly) || !state.settingsDirty;
}

async function loadPortableYaml() {
  elements.settingsYamlStatus.textContent = "Loading configuration…";
  elements.settingsYamlStatus.className = "portable-yaml-status";

  try {
    elements.settingsYaml.value = await fetchText("/api/settings/yaml");
    elements.settingsYamlStatus.textContent = "Current configuration loaded.";
  } catch (error) {
    elements.settingsYamlStatus.textContent = error.message;
    elements.settingsYamlStatus.className = "portable-yaml-status error";
  }
}

async function loadPortableYamlFile(event) {
  const [file] = event.target.files;
  event.target.value = "";

  if (!file) {
    return;
  }

  try {
    elements.settingsYaml.value = await file.text();
    elements.settingsYamlStatus.textContent = `${file.name} loaded. Review it and click Import and apply.`;
    elements.settingsYamlStatus.className = "portable-yaml-status";
  } catch (error) {
    elements.settingsYamlStatus.textContent = error.message;
    elements.settingsYamlStatus.className = "portable-yaml-status error";
  }
}

async function copyPortableYaml() {
  await copyText(
    elements.settingsYaml.value,
    elements.copySettingsYaml,
    "YAML copied"
  );
  elements.settingsYamlStatus.textContent = "YAML copied.";
  elements.settingsYamlStatus.className = "portable-yaml-status success";
}

function downloadPortableYaml() {
  const yaml = elements.settingsYaml.value;

  if (!yaml.trim()) {
    elements.settingsYamlStatus.textContent = "There is no YAML to download.";
    elements.settingsYamlStatus.className = "portable-yaml-status error";
    return;
  }

  const url = URL.createObjectURL(
    new Blob(
      [yaml],
      {
        type: "application/yaml;charset=utf-8"
      }
    )
  );
  const link = document.createElement("a");
  link.href = url;
  link.download = "agentic-router.yaml";
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
  elements.settingsYamlStatus.textContent = "agentic-router.yaml prepared.";
  elements.settingsYamlStatus.className = "portable-yaml-status success";
}

async function createLocalBackup() {
  elements.localBackupStatus.textContent = "Creating file with manifest and hashes…";

  try {
    const response = await fetch("/api/recovery/backup", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        includeConversations: elements.backupConversations.checked,
        includeSessionSummaries: elements.backupSummaries.checked,
        includeUsageHistory: elements.backupUsage.checked,
        includeReviewData: elements.backupReviews.checked
      })
    });

    if (!response.ok) {
      throw new Error(await response.text());
    }

    const blob = await response.blob();
    const href = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = href;
    anchor.download = `agentic-router-backup-${new Date().toISOString().slice(0, 19).replaceAll(":", "-")}.zip`;
    anchor.click();
    URL.revokeObjectURL(href);
    elements.localBackupStatus.textContent =
      "Backup created without keys, approvals, or process state.";
  } catch (error) {
    elements.localBackupStatus.textContent = error.message;
  }
}

async function inspectLocalBackup() {
  const file = elements.backupRestoreFile.files?.[0];

  if (!file) {
    return;
  }

  elements.localBackupStatus.textContent = "Validating manifest and hashes…";

  try {
    const base64 = await fileToBase64(file);
    const inspection = await fetchJson("/api/recovery/backup/inspect", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        archiveBase64: base64
      })
    });
    state.inspectedBackup = inspection;
    state.inspectedBackupBase64 = base64;
    elements.restoreLocalBackup.disabled = false;
    elements.localBackupStatus.textContent =
      `${inspection.manifest.categories.join(", ")} · `
      + `${inspection.manifest.entries.length} files · valid hashes · `
      + `${inspection.conflicts.length} conflitos atuais`;
  } catch (error) {
    state.inspectedBackup = null;
    state.inspectedBackupBase64 = null;
    elements.restoreLocalBackup.disabled = true;
    elements.localBackupStatus.textContent = error.message;
  } finally {
    elements.backupRestoreFile.value = "";
  }
}

async function restoreLocalBackup() {
  const inspection = state.inspectedBackup;

  if (!inspection || !state.inspectedBackupBase64) {
    return;
  }

  const categories = inspection.manifest.categories;

  if (!await showAppConfirm(
    `Restore the categories ${categories.join(", ")}? `
      + "The current state will be saved before atomic application.",
    { title: "Restore backup?", confirmLabel: "Restore" }
  )) {
    return;
  }

  try {
    const result = await fetchJson("/api/recovery/backup/restore", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        archiveBase64: state.inspectedBackupBase64,
        categories,
        confirmed: true
      })
    });
    elements.localBackupStatus.textContent =
      `Restored: ${result.restoredCategories.join(", ")}. `
      + `Previous backup: ${result.currentDataBackup}. Restart to reload.`;
  } catch (error) {
    elements.localBackupStatus.textContent = error.message;
  }
}

function fileToBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => {
      const value = String(reader.result);
      resolve(value.slice(value.indexOf(",") + 1));
    });
    reader.addEventListener("error", () => reject(reader.error));
    reader.readAsDataURL(file);
  });
}

async function importPortableYaml() {
  const yaml = elements.settingsYaml.value;

  if (!yaml.trim()) {
    elements.settingsYamlStatus.textContent = "Enter a YAML configuration.";
    elements.settingsYamlStatus.className = "portable-yaml-status error";
    return;
  }

  if (
    state.settingsDirty
    && !await showAppConfirm(
      "Importing will replace this form's unsaved changes. Continue?",
      { title: "Import configuration?", confirmLabel: "Import" }
    )
  ) {
    return;
  }

  elements.importSettingsYaml.disabled = true;
  elements.settingsYamlStatus.textContent = "Validating and applying…";
  elements.settingsYamlStatus.className = "portable-yaml-status";

  try {
    state.settings = await fetchJson(
      "/api/settings/yaml",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          yaml
        })
      }
    );
    state.settingsDirty = false;
    updateSettingsDirtyState();
    renderSettings();
    await loadPortableYaml();
    elements.settingsYamlStatus.textContent = "YAML configuration imported and applied.";
    elements.settingsYamlStatus.className = "portable-yaml-status success";
    await refreshRuntimeStatus();
    scheduleRuntimeRefresh();
  } catch (error) {
    const errors = error.payload?.errors;
    elements.settingsYamlStatus.textContent = errors
      ? Object.entries(errors)
        .flatMap(([field, messages]) => messages.map(message => `${field}: ${message}`))
        .join("\n")
      : error.message;
    elements.settingsYamlStatus.className = "portable-yaml-status error";
  } finally {
    elements.importSettingsYaml.disabled = false;
  }
}

function selectSettingsSection(event) {
  setSettingsSection(
    event.currentTarget.dataset.settingsTarget,
    true
  );
}

function setSettingsSubsection(subsection, moveFocus) {
  const advancedSection = sectionElementById("settings-advanced");
  if (!advancedSection) {
    return;
  }

  const normalizedSubsection = subsection ?? "portable-yaml";
  const subsectionPanels = advancedSection.querySelectorAll(
    "[data-settings-subsection]"
  );
  let foundPanel = null;

  subsectionPanels.forEach(
    panel => {
      const isActive = panel.dataset.settingsSubsection === normalizedSubsection;
      panel.classList.toggle("active", isActive);
      if (isActive) {
        foundPanel = panel;
      }
    }
  );

  advancedSection.querySelectorAll(
    "[data-settings-subtarget]"
  ).forEach(
    button => button.setAttribute(
      "aria-current",
      button.dataset.settingsSubtarget === normalizedSubsection
        ? "page"
        : "false"
    )
  );

  if (!foundPanel) {
    return;
  }

  state.settingsSubsection = normalizedSubsection;
  if (moveFocus) {
    foundPanel.focus({
      preventScroll: true
    });
  }
}

function selectSettingsSubsection(event) {
  setSettingsSubsection(
    event.currentTarget.dataset.settingsSubtarget,
    true
  );
}

function setSettingsSection(section, moveFocus) {
  const normalizedSection = normalizeSettingsSection(section);
  const sectionIds = visibleSettingsSectionIds(normalizedSection);
  const allSections = document.querySelectorAll(".settings-section");
  const activeSections = sectionIds
    .map(
      sectionId => sectionElementById(sectionId)
    )
    .filter(Boolean);

  if (!activeSections.length) {
    return;
  }

  allSections.forEach(
    sectionElement => sectionElement.classList.remove("active")
  );
  activeSections.forEach(
    sectionElement => sectionElement.classList.add("active")
  );

  state.settingsSection = normalizedSection;
  elements.settingsSectionSelect.value = normalizedSection;
  elements.settingsDialog.dataset.section = normalizedSection;
  elements.settingsNavigation.querySelectorAll("[data-settings-target]").forEach(
    button => button.setAttribute(
      "aria-current",
      button.dataset.settingsTarget === normalizedSection
        ? "page"
        : "false"
    )
  );

  if (normalizedSection === "advanced") {
    setSettingsSubsection(
      state.settingsSubsection
    );
  }

  if (moveFocus) {
    activeSections[0].focus({
      preventScroll: true
    });
  }
}

function navigateToSettingsError(field) {
  const section = !field
    ? "general"
    : field.startsWith("ollama")
      ? "general"
      : field.startsWith("router") || field.startsWith("intentions")
        ? "models-routing"
        : field.startsWith("coordinator") || field.startsWith("action")
          ? "models-routing"
          : field.startsWith("execution")
            ? "execution"
            : field.startsWith("runtime") || field.startsWith("context")
              ? "harnesses"
              : field.startsWith("usage")
                ? "harnesses"
                : field.startsWith("validation")
                  ? "workspaces"
                  : field.startsWith("git")
                    ? "workspaces"
                    : field.startsWith("session")
                      ? "workspaces"
                      : "general";
  setSettingsSection(
    section,
    true
  );
  elements.settingsForm.querySelector(".field-invalid")?.focus();
}

function handleSettingsCancel(event) {
  event.preventDefault();
  closeSettings();
}

function openWorkspaceFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  openWorkspace();
}

function openRecentFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  elements.sessionHistory.scrollIntoView({
    block: "nearest"
  });
  elements.openSessionSearch.focus();
}

function openGitFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  void openGitPanel();
}

function openValidationFromSettings() {
  state.settingsDirty = false;
  updateSettingsDirtyState();
  elements.settingsDialog.close();
  openWorkspace();
  elements.validationProfileSection.open = true;
  elements.validationProfileName.focus();
}

async function handleModelSelectionChange() {
  updateHarnessControls();
  updateInteractionControls();
  updateComposerModelTitle();
  updateComposerStatus();
  state.activeAgentModel = null;
  state.activeAgentRole = null;
  state.activeHarness = null;
  state.webEnabled = false;
  state.webControlState = "unavailable";
  await refreshSelectedModelCapabilities();
}

async function refreshSelectedModelCapabilities(
  model = null,
  role = null
) {
  const selected = model
    ?? state.activeAgentModel
    ?? elements.modelSelector.value;
  const capabilityModel = selected && selected !== "auto"
    ? selected
    : state.settings?.defaultModel;
  const requestId = ++state.capabilityRequestId;

  if (!capabilityModel) {
    state.modelCapability = null;
    renderCapabilityContext();
    return;
  }

  elements.activeProviderModel.textContent = "Checking capabilities…";

  try {
    const view = await fetchJson(
      `/api/capabilities/model?model=${encodeURIComponent(capabilityModel)}`
    );

    if (requestId !== state.capabilityRequestId) {
      return;
    }

    state.modelCapability = {
      ...view,
      role: role ?? state.activeAgentRole ?? view.role
    };

    if (!view.webAvailable) {
      state.webEnabled = false;
      state.webControlState = "unavailable";
    } else if (state.webControlState === "unavailable") {
      state.webControlState = "available";
    }
  } catch (error) {
    if (requestId !== state.capabilityRequestId) {
      return;
    }

    state.modelCapability = {
      model: capabilityModel,
      provider: providerFromModel(capabilityModel),
      providerDisplayName: providerLabel(providerFromModel(capabilityModel)),
      role: role ?? "primary",
      capabilities: null,
      webAvailable: false,
      webUnavailableReason: error.message
    };
    state.webEnabled = false;
    state.webControlState = "unavailable";
  }

  renderCapabilityContext();
  renderPendingContextUsage();
}

function renderCapabilityContext() {
  const view = state.modelCapability;
  elements.capabilityTags.replaceChildren();

  if (!view?.capabilities) {
    elements.activeProviderModel.textContent =
      view?.webUnavailableReason ?? "Capabilities unavailable";
    renderWebControl();
    elements.fallbackIndicator.hidden = true;
    return;
  }

  const capabilities = view.capabilities;
  const routerConfigurationDocumentation =
    "https://github.com/Shakansis/agentic-router#configuration";
  const routerCapabilityDocumentation =
    "https://github.com/Shakansis/agentic-router#web-search-citations-and-image-input";
  const isLocal = view.provider === "ollama-local";
  elements.activeProviderModel.textContent =
    `${view.providerDisplayName} · ${view.model}`;
  const tags = [
    {
      label: isLocal ? "Local" : "Cloud",
      kind: isLocal ? "local" : "cloud",
      status: "Active in this conversation",
      enabled: true,
      description: isLocal
        ? `Model ${view.model} is running through Ollama Local.`
        : `Model ${view.model} is running through provider ${view.providerDisplayName}.`,
      documentationUrl: isLocal
        ? "https://docs.ollama.com/api/introduction"
        : routerConfigurationDocumentation
    },
    capabilities.nativeTools
      ? {
        label: "Tools",
        kind: "tools",
        status: capabilities.toolProtocolConfirmed
          ? "Enabled and confirmed"
          : "Enabled; behavioral confirmation pending",
        enabled: true,
        description: capabilities.toolProtocolConfirmed
          ? "The model can call structured tools and that protocol has been confirmed by the Router."
          : `The model advertises tool calls in ${capabilities.source}; behavioral conformance is verified separately.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/tool-calling"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.webSearch
      ? {
        label: "Web",
        kind: "web",
        status: state.webEnabled
          ? "Enabled in this conversation"
          : "Available, but disabled in this conversation",
        enabled: state.webEnabled,
        description: capabilities.providerNativeWebSearch
          ? "Native provider web search. The user must explicitly enable it for the conversation."
          : "Separate read-only Ollama search. The user must explicitly enable it for the conversation.",
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/web-search"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.vision
      ? {
        label: "Vision",
        kind: "vision",
        status: "Enabled for this model",
        enabled: true,
        description: `Accepts up to ${capabilities.maximumImageCount} images, with ${formatBytes(capabilities.maximumImageBytes)} per image.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/vision"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.structuredOutput
      ? {
        label: "Structured",
        kind: "structured",
        status: "Enabled for this model",
        enabled: true,
        description: `The model can respond using a structured schema; evidence from ${capabilities.source}.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/structured-outputs"
          : routerCapabilityDocumentation
      }
      : null,
    {
      label: view.role === "fallback" ? "Fallback" : "Primary",
      kind: view.role === "fallback" ? "fallback" : "primary",
      status: "Active role in this conversation",
      enabled: true,
      description: view.role === "fallback"
        ? "This model is serving as the configured fallback after eligible primary-model unavailability."
        : "This model is the primary target selected for this conversation.",
      documentationUrl: routerConfigurationDocumentation
    }
  ].filter(Boolean);

  for (const [index, tag] of tags.entries()) {
    const container = document.createElement("span");
    const trigger = document.createElement("button");
    const popover = document.createElement("span");
    const heading = document.createElement("strong");
    const status = document.createElement("span");
    const description = document.createElement("span");
    const documentation = document.createElement("a");
    const popoverId = `capability-help-${tag.kind}-${index}`;

    container.className = "capability-info";
    trigger.type = "button";
    trigger.className = "capability-tag";
    trigger.dataset.kind = tag.kind;
    trigger.textContent = tag.label;
    trigger.setAttribute("aria-expanded", "false");
    trigger.setAttribute("aria-controls", popoverId);
    trigger.setAttribute("aria-haspopup", "dialog");
    trigger.setAttribute(
      "aria-label",
      `${tag.label}: ${tag.status}. Open details.`
    );

    popover.id = popoverId;
    popover.className = "capability-popover";
    popover.setAttribute("role", "dialog");
    popover.setAttribute("aria-label", `Details for ${tag.label}`);
    heading.textContent = tag.label;
    status.className = "capability-popover-status";
    status.dataset.enabled = String(tag.enabled);
    status.textContent = tag.status;
    description.className = "capability-popover-description";
    description.textContent = tag.description;
    documentation.className = "capability-popover-link";
    documentation.href = tag.documentationUrl;
    documentation.target = "_blank";
    documentation.rel = "noopener noreferrer";
    documentation.textContent = "Learn more in the official documentation ↗";
    popover.append(heading, status, description, documentation);
    container.append(trigger, popover);
    elements.capabilityTags.append(container);
  }

  elements.fallbackIndicator.hidden =
    view.provider === "ollama-local"
    || !hasConfiguredLocalFallback(view.model);
  renderWebControl();
}

function handleCapabilityTagClick(event) {
  const trigger = event.target.closest(".capability-tag");
  if (!trigger) {
    return;
  }

  event.stopPropagation();
  const container = trigger.closest(".capability-info");
  const shouldOpen = container.dataset.open !== "true";
  closeCapabilityPopovers();
  if (shouldOpen) {
    container.dataset.open = "true";
    trigger.setAttribute("aria-expanded", "true");
  }
}

function handleCapabilityDocumentClick(event) {
  if (!event.target.closest(".capability-info")) {
    closeCapabilityPopovers();
  }
}

function handleCapabilityKeyDown(event) {
  if (event.key !== "Escape") {
    return;
  }

  if (!elements.projectMenuPopover.hidden) {
    event.preventDefault();
    const anchor = projectMenuAnchor;
    closeProjectMenu();
    anchor?.focus();
    return;
  }

  const openTrigger = elements.capabilityTags.querySelector(
    ".capability-info[data-open=\"true\"] .capability-tag"
  );
  if (!openTrigger) {
    return;
  }

  event.preventDefault();
  closeCapabilityPopovers();
  openTrigger.focus();
}

function closeCapabilityPopovers() {
  for (const container of elements.capabilityTags.querySelectorAll(
    ".capability-info[data-open=\"true\"]"
  )) {
    delete container.dataset.open;
    container.querySelector(".capability-tag")?.setAttribute(
      "aria-expanded",
      "false"
    );
  }
}

function renderWebControl() {
  const available = Boolean(state.modelCapability?.webAvailable);

  if (!available) {
    state.webEnabled = false;
    state.webControlState = "unavailable";
  } else if (state.webEnabled) {
    state.webControlState = "enabled";
  } else if (!["available", "off"].includes(state.webControlState)) {
    state.webControlState = "available";
  }

  const labels = {
    unavailable: "Web unavailable",
    available: "Web available",
    enabled: "Web enabled",
    off: "Web off"
  };
  elements.webToggle.dataset.state = state.webControlState;
  elements.webToggleLabel.textContent = labels[state.webControlState];
  elements.webToggle.setAttribute(
    "aria-label",
    labels[state.webControlState]
  );
  elements.webToggle.disabled = !available || Boolean(state.requestController);
  elements.webToggle.setAttribute(
    "aria-pressed",
    String(state.webEnabled)
  );
  elements.webToggle.title = available
    ? state.modelCapability.capabilities.providerNativeWebSearch
      ? "Official provider search. Click to explicitly enable it in this conversation."
      : "Separate read-only Ollama search. Click to explicitly enable it in this conversation."
    : state.modelCapability?.webUnavailableReason
      ?? "No authorized search integration is available.";
}

function toggleWebSearch() {
  if (!state.modelCapability?.webAvailable || state.requestController) {
    return;
  }

  state.webEnabled = !state.webEnabled;
  state.webControlState = state.webEnabled ? "enabled" : "off";
  renderCapabilityContext();
  updateComposerStatus();
}

function providerFromModel(model) {
  const separator = model.indexOf("::");
  return separator > 0 ? model.slice(0, separator) : "ollama-local";
}

function providerLabel(provider) {
  return {
    "ollama-local": "Ollama Local",
    groq: "Groq",
    "google-ai-studio": "Google AI Studio",
    cerebras: "Cerebras"
  }[provider] ?? provider;
}

function hasConfiguredLocalFallback(model) {
  return Object.values(state.settings?.intentions ?? {}).some(
    intention => {
      const primary = intention.model === "default"
        ? state.settings.defaultModel
        : intention.model;
      const fallback = intention.fallbackModel === "default"
        ? state.settings.defaultModel
        : intention.fallbackModel;
      return primary === model
        && fallback
        && fallback !== "none"
        && providerFromModel(fallback) === "ollama-local";
    }
  );
}

async function handleImageSelection(event) {
  await addImageFiles(event.currentTarget.files);
  event.currentTarget.value = "";
}

function handleImagePaste(event) {
  const files = Array.from(event.clipboardData?.files ?? []).filter(
    file => file.type.startsWith("image/")
  );

  if (files.length > 0) {
    event.preventDefault();
    void addImageFiles(files);
  }
}

function handleImageDragOver(event) {
  if (Array.from(event.dataTransfer?.items ?? []).some(
    item => item.kind === "file"
  )) {
    event.preventDefault();
    elements.composer.classList.add("drag-active");
  }
}

function handleImageDragLeave(event) {
  if (!elements.composer.contains(event.relatedTarget)) {
    elements.composer.classList.remove("drag-active");
  }
}

function handleImageDrop(event) {
  event.preventDefault();
  elements.composer.classList.remove("drag-active");
  void addImageFiles(event.dataTransfer?.files);
}

async function addImageFiles(fileList) {
  const files = Array.from(fileList ?? []);
  const acceptedTypes = new Set([
    "image/jpeg",
    "image/png",
    "image/webp",
    "image/gif"
  ]);

  if (state.attachments.length + files.length > 4) {
    elements.composerStatus.textContent = "Up to 4 images per request.";
    return;
  }

  for (const file of files) {
    if (!acceptedTypes.has(file.type)) {
      elements.composerStatus.textContent =
        "Only JPEG, PNG, WebP, and GIF are accepted; SVG is not allowed.";
      continue;
    }

    if (file.size <= 0 || file.size > 10 * 1024 * 1024) {
      elements.composerStatus.textContent =
        "Each image must be no larger than 10 MiB.";
      continue;
    }

    const total = state.attachments.reduce(
      (sum, attachment) => sum + attachment.declaredBytes,
      file.size
    );

    if (total > 20 * 1024 * 1024) {
      elements.composerStatus.textContent =
        "Combined images must be no larger than 20 MiB.";
      break;
    }

    const dataUrl = await readFileDataUrl(file);
    state.attachments.push({
      id: createSessionId(),
      fileName: file.name || "clipboard-image",
      mimeType: file.type,
      declaredBytes: file.size,
      base64Data: dataUrl.slice(dataUrl.indexOf(",") + 1),
      previewUrl: URL.createObjectURL(file)
    });
  }

  renderAttachmentPreviews();
  updateComposerStatus();
}

function readFileDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.addEventListener("load", () => resolve(String(reader.result)));
    reader.addEventListener("error", () => reject(reader.error));
    reader.readAsDataURL(file);
  });
}

function renderAttachmentPreviews() {
  elements.attachmentPreviews.replaceChildren();
  elements.attachmentPreviews.hidden = state.attachments.length === 0;

  for (const attachment of state.attachments) {
    const preview = document.createElement("figure");
    preview.className = "attachment-preview";
    const image = document.createElement("img");
    image.src = attachment.previewUrl;
    image.alt = attachment.fileName;
    const caption = document.createElement("span");
    caption.textContent = attachment.fileName;
    caption.title = `${attachment.fileName} · ${formatBytes(attachment.declaredBytes)}`;
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "attachment-remove";
    remove.dataset.attachmentRemove = attachment.id;
    remove.setAttribute("aria-label", `Remove ${attachment.fileName}`);
    remove.title = `Remove ${attachment.fileName}`;
    remove.textContent = "×";
    preview.append(image, caption, remove);
    elements.attachmentPreviews.append(preview);
  }
}

function handleAttachmentPreviewClick(event) {
  const button = event.target.closest("[data-attachment-remove]");

  if (!button) {
    return;
  }

  const index = state.attachments.findIndex(
    attachment => attachment.id === button.dataset.attachmentRemove
  );

  if (index >= 0) {
    URL.revokeObjectURL(state.attachments[index].previewUrl);
    state.attachments.splice(index, 1);
    renderAttachmentPreviews();
    updateComposerStatus();
  }
}

function clearAttachments() {
  for (const attachment of state.attachments) {
    URL.revokeObjectURL(attachment.previewUrl);
  }

  state.attachments = [];
  renderAttachmentPreviews();
}

async function ensureCloudImageApproval(model) {
  if (state.attachments.length === 0) {
    return true;
  }

  if (!model || model === "auto") {
    elements.composerStatus.textContent =
      "Explicitly select a Vision-capable model before sending images.";
    return false;
  }

  const provider = providerFromModel(model);

  if (provider === "ollama-local") {
    return true;
  }

  const key = `${state.browserSessionId}\n${provider}`;

  if (state.cloudImageApprovals.has(key)) {
    return true;
  }

  const bytes = state.attachments.reduce(
    (sum, attachment) => sum + attachment.declaredBytes,
    0
  );
  const approved = await showAppConfirm(
    `${providerLabel(provider)} will receive ${formatBytes(bytes)} of images. `
      + "These bytes will leave this computer. Authorize this provider for this session?",
    { title: "Authorize image submission?", confirmLabel: "Authorize" }
  );

  if (!approved) {
    elements.composerStatus.textContent = "Cloud image submission was not authorized.";
    return false;
  }

  await fetchJson(
    "/api/privacy/cloud-images/approve",
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        browserSessionId: state.browserSessionId,
        provider
      })
    }
  );
  state.cloudImageApprovals.add(key);
  return true;
}

async function resetCloudImagePrivacy(browserSessionId) {
  state.cloudImageApprovals.clear();
  state.webEnabled = false;
  state.webControlState = state.modelCapability?.webAvailable
    ? "available"
    : "unavailable";
  clearAttachments();

  if (!browserSessionId) {
    return;
  }

  try {
    await fetchJson(
      "/api/privacy/cloud-images/reset",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({ browserSessionId })
      }
    );
  } catch {
    // The old browser-session identifier is no longer reused.
  }
}

function handleModeChange(event) {
  if (state.requestController) {
    return;
  }
  setInteractionMode(event.currentTarget.dataset.mode);
}

function setInteractionMode(mode) {
  state.interactionMode = mode;
  if (mode !== "execute" && state.harness === "auto-model-harness") {
    state.harness = "native";
    elements.harnessSelector.value = "native";
  }
  updateInteractionControls();
  updateHarnessControls();
  updateComposerStatus();
}

function handleApprovalPolicyChange() {
  state.approvalPolicy = elements.approvalPolicy.value;
  updateComposerStatus();
}

function updateInteractionControls() {
  const isStreaming = Boolean(state.requestController);
  const disabled = isStreaming || state.conversationTransitioning;
  document.querySelectorAll(".mode-option").forEach(
    button => {
      const active = button.dataset.mode === state.interactionMode;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
      button.disabled = disabled;
    }
  );
  elements.approvalPolicy.value = state.approvalPolicy;
  elements.approvalPolicy.disabled =
    disabled || state.interactionMode !== "execute";
  elements.composer.classList.toggle(
    "execute-mode",
    state.interactionMode === "execute"
  );
}

function handleHarnessChange() {
  state.harness = elements.harnessSelector.value;
  updateHarnessControls();
  updateComposerStatus();
}

function harnessDisplayLabel(definition) {
  if (!definition.experimental) {
    return definition.displayName;
  }
  return definition.id === "opencode"
    || definition.id === "qwen-code"
    || definition.id === "claude-code"
    ? `${definition.displayName} [Experimental]`
    : `${definition.displayName} (Experimental)`;
}

function renderHarnesses() {
  const statuses = Array.isArray(state.harnesses)
    ? state.harnesses
    : [];
  const options = statuses.map(status => {
    const definition = status.definition;
    const availability = status.availability;
    const label = harnessDisplayLabel(definition);
    return {
      value: definition.id,
      label: availability.available
        ? label
        : `${label} — Unavailable`,
      disabled: !availability.available,
      title: availability.available
        ? `${definition.description} Version: ${availability.version ?? "unknown"}.`
        : availability.message ?? `${label} is unavailable.`
    };
  });
  options.unshift({
    value: "auto-model-harness",
    label: "Auto Model × Harness",
    title: "Selects once before Execute using local recommendation and current availability."
  });
  if (!options.some(option => option.value === "native")) {
    options.unshift({ value: "native", label: "Native" });
  }
  const selected = options.some(
    option => option.value === state.harness && !option.disabled
  )
    ? state.harness
    : "native";
  state.harness = selected;
  replaceOptions(elements.harnessSelector, options, selected);
}

function updateHarnessControls() {
  const disabled = Boolean(
    state.requestController
  ) || state.conversationTransitioning;
  const automaticRoute = state.interactionMode === "execute"
    && state.harness === "auto-model-harness";
  elements.modelSelector.disabled = disabled || automaticRoute;
  elements.harnessSelector.disabled = disabled;
  const automaticOption = elements.harnessSelector.querySelector(
    'option[value="auto-model-harness"]'
  );
  if (automaticOption) {
    automaticOption.disabled = state.interactionMode !== "execute";
  }
  elements.harnessSelector.value = state.harness;
}

async function ensureConversationIdentity() {
  if (state.conversationSessionId) {
    renderPersistenceStatus();
    return true;
  }

  try {
    const identity = await fetchJson(
      "/api/sessions/new",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId
        })
      }
    );
    state.conversationSessionId = identity.sessionId;
    setPersistenceStatus(identity.status);
    return true;
  } catch (error) {
    setPersistenceStatus("Save failed");
    elements.composerStatus.textContent =
      `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
    return false;
  }
}

async function requestNewConversation() {
  await requestConversationTransition(
    beginEmptyConversation
  );
}

async function requestConversationTransition(action) {
  if (state.requestController) {
    elements.composerStatus.textContent =
      "Finish or cancel the active turn before switching conversations.";
    return;
  }
  if (state.conversationTransitioning) {
    return;
  }

  setConversationTransitioning(
    true
  );
  const historyEnabled = Boolean(
    activeWorkspaceProfile()?.historyEnabled
  );
  if (historyEnabled) {
    try {
      if (
        hasMeaningfulConversation()
        && state.persistenceStatus !== "Saved locally"
        && !await saveCurrentConversation()
      ) {
        return;
      }
      await action();
    } finally {
      setConversationTransitioning(
        false
      );
    }
    return;
  }

  if (hasMeaningfulConversation()) {
    state.pendingConversationAction = action;
    elements.newConversationDialog.showModal();
    elements.newConversationEnableHistory.focus();
    return;
  }

  try {
    await action();
  } finally {
    setConversationTransitioning(
      false
    );
  }
}

function hasMeaningfulConversation() {
  return state.history.length > 0
    || Boolean(state.latestExecutionSessionId);
}

async function saveCurrentConversation() {
  if (!state.conversationSessionId && !await ensureConversationIdentity()) {
    return false;
  }
  if (!activeWorkspaceProfile()?.historyEnabled) {
    setPersistenceStatus("History disabled");
    return false;
  }

  setPersistenceStatus("Saving");

  try {
    const result = await fetchJson(
      "/api/sessions/current",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          sessionId: state.conversationSessionId,
          messages: state.history,
          interactionMode: state.interactionMode,
          selectedModel: elements.modelSelector.value,
          state: state.conversationState
        })
      }
    );
    setPersistenceStatus(result.status);
    await refreshSessions();
    return true;
  } catch (error) {
    setPersistenceStatus("Save failed");
    elements.composerStatus.textContent =
      `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
    return false;
  }
}

async function beginEmptyConversation() {
  const nextBrowserSessionId = createSessionId();
  let identity;

  try {
    identity = await fetchJson(
      "/api/sessions/new",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          browserSessionId: nextBrowserSessionId
        })
      }
    );
  } catch (error) {
    setPersistenceStatus("Save failed");
    elements.composerStatus.textContent =
      `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
    return;
  }

  const previousBrowserSessionId = state.browserSessionId;
  clearConversationUi();
  state.browserSessionId = nextBrowserSessionId;
  state.conversationSessionId = identity.sessionId;
  state.conversationState = "completed";
  state.latestExecutionSessionId = null;
  setPersistenceStatus(identity.status);

  await Promise.allSettled([
    resetCloudImagePrivacy(previousBrowserSessionId),
    refreshSessions(),
    refreshGit(),
    refreshSelectedModelCapabilities()
  ]);
}

function clearConversationUi() {
  state.conversationVersion++;
  state.history = [];
  state.editingTurn = null;
  state.harness = "native";
  state.interactionMode = "chat";
  state.approvalPolicy = "auto";
  state.activeAgentModel = null;
  state.activeAgentRole = null;
  state.modelCapability = null;
  state.contextUsage = null;
  state.messageQueue = [];
  state.queueEditingId = null;
  state.queuedDispatchMessage = null;
  state.messageQueuePaused = false;
  state.steeringMessage = false;
  state.webEnabled = false;
  state.webControlState = "unavailable";
  state.cloudImageApprovals.clear();
  clearAttachments();
  state.autoFollow = true;
  elements.modelSelector.value = "auto";
  elements.harnessSelector.value = "native";
  elements.messageInput.value = "";
  resizeComposer();
  elements.composer.classList.remove("editing");
  elements.cancelMessageEdit.hidden = true;
  renderMessageQueue();

  for (const message of elements.messages.children) {
    resizeObserver.unobserve(message);
  }

  const emptyState = createEmptyState();
  elements.emptyState = emptyState;
  elements.messages.replaceChildren(
    emptyState
  );
  updateHarnessControls();
  updateInteractionControls();
  updateComposerStatus();
  renderPendingContextUsage();
  updateJumpControl();
  elements.messageInput.focus();
}

async function enableHistoryForCurrentWorkspace() {
  const active = activeWorkspaceProfile();
  if (!active) {
    return false;
  }

  try {
    await fetchJson(
      `/api/workspaces/${encodeURIComponent(active.id)}/history`,
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          enabled: true
        })
      }
    );
    await refreshWorkspaceState();
    setPersistenceStatus(
      hasMeaningfulConversation()
        ? "Unsaved"
        : "Saved locally"
    );
    return true;
  } catch (error) {
    setPersistenceStatus("Save failed");
    elements.composerStatus.textContent = error.message;
    return false;
  }
}

async function saveUnsavedConversationAndContinue() {
  const action = state.pendingConversationAction;
  elements.newConversationDialog.close();
  state.pendingConversationAction = null;
  try {
    if (
      !action
      || !await enableHistoryForCurrentWorkspace()
      || !await saveCurrentConversation()
    ) {
      return;
    }
    await action();
  } finally {
    setConversationTransitioning(
      false
    );
  }
}

async function discardUnsavedConversationAndContinue() {
  const action = state.pendingConversationAction;
  elements.newConversationDialog.close();
  state.pendingConversationAction = null;
  try {
    if (action) {
      await action();
    }
  } finally {
    setConversationTransitioning(
      false
    );
  }
}

function cancelConversationTransition() {
  state.pendingConversationAction = null;
  elements.newConversationDialog.close();
  setConversationTransitioning(
    false
  );
  elements.newConversation.focus();
}

function setConversationTransitioning(isTransitioning) {
  state.conversationTransitioning = isTransitioning;
  elements.newConversation.disabled = isTransitioning;
  elements.messageInput.disabled = isTransitioning;
  elements.sendButton.disabled = isTransitioning;
  updateInteractionControls();
  updateHarnessControls();
  if (
    isTransitioning
    || state.persistenceStatus !== "Save failed"
  ) {
    updateComposerStatus();
  }
}

function setPersistenceStatus(status) {
  state.persistenceStatus = status;
  renderPersistenceStatus();
}

function renderPersistenceStatus() {
  const historyEnabled = Boolean(
    activeWorkspaceProfile()?.historyEnabled
  );
  const status = historyEnabled
    ? state.persistenceStatus
    : "History disabled";
  const className = status === "Saved locally"
    ? "saved"
    : status === "Saving"
      ? "saving"
      : status === "Save failed"
        ? "failed"
        : status === "Interrupted"
          ? "interrupted"
          : "";
  for (const element of [
    elements.conversationPersistence,
    elements.conversationPersistenceSidebar
  ]) {
    element.textContent = status;
    element.className = `persistence-status ${className}`.trim();
  }
  elements.conversationPersistenceSidebar.classList.add("sidebar-expanded-only");
}

async function refreshSetupStatus({ quiet = false } = {}) {
  try {
    const [setup, modelsResponse] = await Promise.all([
      fetchJson("/api/setup/status"),
      fetchJson("/api/models")
    ]);
    state.setup = setup;
    state.harnesses = setup.harnesses.map(harness => ({
      definition: harness.definition,
      availability: harness.availability
    }));
    state.devices = setup.devices;
    state.models = modelsResponse.models;
    updateProviderStatus(modelsResponse);
    updateDeviceStatus({
      devices: setup.devices,
      diagnostic: setup.deviceDiagnostic
    });
    renderHarnesses();
    renderComposerModels();
    updateHarnessControls();
    updateInteractionControls();
    renderSetupOnboarding();
  } catch (error) {
    if (!quiet) {
      showToast(error.message || t("setup.refresh_failed"));
    }
  }
}

function renderSetupOnboarding() {
  const emptyState = document.querySelector("#empty-state");
  const containers = document.querySelectorAll("[data-setup-surface]");
  if (containers.length === 0 || !state.setup) {
    return;
  }

  const setup = state.setup;
  if (emptyState) {
    emptyState.hidden = setup.coreReady;
    emptyState.classList.toggle("has-setup", !setup.coreReady);
  }
  containers.forEach(container => {
    const onboarding = container.dataset.setupSurface === "onboarding";
    if (onboarding && setup.coreReady) {
      container.replaceChildren();
      container.hidden = true;
      return;
    }
    renderSetupSurface(container, setup);
  });
  scheduleSetupRefresh(setup);
}

function renderSetupSurface(container, setup) {
  container.replaceChildren();
  container.hidden = false;

  const header = document.createElement("header");
  header.className = "setup-header";
  const heading = document.createElement("div");
  const title = document.createElement("strong");
  title.textContent = t("setup.title");
  const summary = document.createElement("span");
  summary.textContent = setup.coreReady
    ? t("setup.ready")
    : t("setup.missing");
  heading.append(title, summary);
  const refresh = document.createElement("button");
  refresh.type = "button";
  refresh.className = "icon-button setup-refresh";
  refresh.dataset.setupAction = "refresh";
  refresh.title = t("setup.refresh");
  refresh.setAttribute("aria-label", t("setup.refresh"));
  refresh.textContent = "↻";
  header.append(heading, refresh);
  container.append(header);

  const description = document.createElement("p");
  description.className = "setup-description";
  description.textContent = t("setup.description");
  container.append(description);

  const grid = document.createElement("div");
  grid.className = "setup-grid";
  grid.append(
    createSetupGroup(
      t("setup.ollama"),
      [createSetupResourceRow(setup.ollama, "install")]
    ),
    createSetupGroup(
      t("setup.models"),
      createSetupModelRows(setup)
    ),
    createSetupGroup(
      t("setup.harnesses"),
      setup.harnesses.map(harness => createSetupResourceRow(harness, "install"))
    )
  );
  container.append(grid);

  const gpu = document.createElement("small");
  gpu.className = "setup-hardware-note";
  gpu.textContent = setup.largestGpuMemoryBytes
    ? t("setup.gpu", { memory: formatSetupBytes(setup.largestGpuMemoryBytes) })
    : t("setup.gpu_unknown");
  container.append(gpu);
  if (setup.readOnly) {
    const readOnly = document.createElement("small");
    readOnly.className = "setup-read-only";
    readOnly.textContent = t("setup.read_only");
    container.append(readOnly);
  }
}

function createSetupGroup(label, rows) {
  const group = document.createElement("section");
  group.className = "setup-group";
  const heading = document.createElement("h3");
  heading.textContent = label;
  const list = document.createElement("div");
  list.className = "setup-list";
  list.append(...rows);
  group.append(heading, list);
  return group;
}

function createSetupResourceRow(resource, action) {
  const row = document.createElement("div");
  row.className = "setup-row";
  const status = document.createElement("span");
  status.className = `setup-state ${resource.available ? "ready" : "missing"}`;
  status.textContent = resource.available ? "✓" : "!";
  status.setAttribute("aria-hidden", "true");
  const content = document.createElement("div");
  content.className = "setup-row-content";
  const name = document.createElement("strong");
  name.textContent = resource.displayName;
  if (resource.recommended) {
    const tag = document.createElement("span");
    tag.className = "setup-tag";
    tag.textContent = t("setup.harness_recommended");
    name.append(" ", tag);
  }
  const detail = document.createElement("small");
  detail.textContent = setupResourceDetail(resource);
  content.append(name, detail);
  row.append(status, content);

  if (!resource.available && resource.installSupported) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button compact setup-action";
    button.dataset.setupAction = action;
    button.dataset.resourceId = resource.id;
    const recentlyStarted = resource.job?.state === "started"
      && Date.now() - new Date(resource.job.updatedAt).getTime() < 10 * 60 * 1000;
    button.disabled = state.setup?.readOnly || recentlyStarted;
    button.textContent = resource.job?.state === "failed" || (
      !recentlyStarted && resource.job
    )
      ? t("setup.retry")
      : t("setup.install");
    row.append(button);
  }
  return row;
}

function createSetupModelRows(setup) {
  return setup.recommendedModels.map(model => {
    const row = document.createElement("div");
    row.className = "setup-row setup-model-row";
    row.dataset.model = model.model;
    const status = document.createElement("span");
    const downloading = model.job?.state === "downloading";
    status.className = `setup-state ${model.installed ? "ready" : "missing"}`;
    status.textContent = model.installed ? "✓" : downloading ? "↓" : "!";
    status.setAttribute("aria-hidden", "true");
    const content = document.createElement("div");
    content.className = "setup-row-content";
    const name = document.createElement("strong");
    name.textContent = model.model;
    if (model.recommended) {
      const tag = document.createElement("span");
      tag.className = "setup-tag";
      tag.textContent = t("setup.recommended");
      name.append(" ", tag);
    }
    const detail = document.createElement("small");
    detail.textContent = model.installed
      ? t("setup.installed")
      : `${formatSetupBytes(model.downloadBytes)} · ${model.reason}`;
    content.append(name, detail);
    if (downloading && model.job?.totalBytes > 0) {
      const progress = document.createElement("progress");
      progress.max = model.job.totalBytes;
      progress.value = model.job.completedBytes ?? 0;
      progress.setAttribute("aria-label", `${model.model} ${t("setup.downloading")}`);
      content.append(progress);
    }
    row.append(status, content);
    if (!model.installed) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "secondary-button compact setup-action";
      button.dataset.setupAction = "pull";
      button.dataset.model = model.model;
      button.disabled = setup.readOnly || !setup.ollama.available || downloading;
      button.textContent = downloading ? t("setup.downloading") : t("setup.pull");
      row.append(button);
    }
    return row;
  });
}

function setupResourceDetail(resource) {
  if (resource.available) {
    if (resource.required && resource.id === "native") {
      return t("setup.native");
    }
    const status = resource.definition
      ? t("setup.installed")
      : t("setup.available");
    return resource.version
      ? `${status} · ${resource.version}`
      : status;
  }
  if (
    resource.job?.state === "started"
    && Date.now() - new Date(resource.job.updatedAt).getTime() < 10 * 60 * 1000
  ) {
    return t("setup.started");
  }
  return resource.diagnostic || (resource.required
    ? t("setup.missing_status")
    : t("setup.optional"));
}

function scheduleSetupRefresh(setup) {
  clearTimeout(state.setupTimer);
  const installerPending = [setup.ollama, ...setup.harnesses].some(
    resource => !resource.available
      && resource.job?.state === "started"
      && Date.now() - new Date(resource.job.updatedAt).getTime() < 10 * 60 * 1000
  );
  const modelPending = setup.recommendedModels.some(
    model => model.job?.state === "downloading"
  );
  if (installerPending || modelPending) {
    state.setupTimer = window.setTimeout(
      () => refreshSetupStatus({ quiet: true }),
      5000
    );
  }
}

async function handleSetupAction(event) {
  const button = event.target.closest("[data-setup-action]");
  if (!button) {
    return;
  }
  const action = button.dataset.setupAction;
  button.disabled = true;
  try {
    if (action === "refresh") {
      await refreshSetupStatus();
      return;
    }
    if (action === "install") {
      const resourceId = button.dataset.resourceId;
      const result = await fetchJson(
        `/api/setup/install/${encodeURIComponent(resourceId)}`,
        { method: "POST" }
      );
      showToast(
        t("setup.action_started", { resource: result.resourceId }),
        "success",
        7000
      );
    } else if (action === "pull") {
      const model = button.dataset.model;
      await fetchJson("/api/setup/models/pull", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ model })
      });
      showToast(
        t("setup.model_started", { resource: model }),
        "success",
        7000
      );
    }
    await refreshSetupStatus({ quiet: true });
  } catch (error) {
    showToast(error.message);
    button.disabled = false;
  }
}

function formatSetupBytes(bytes) {
  const gib = bytes / (1024 ** 3);
  return gib >= 1
    ? `${gib.toFixed(gib >= 10 ? 0 : 1)} GB`
    : `${(bytes / (1024 ** 2)).toFixed(0)} MB`;
}

function createEmptyState() {
  const container = document.createElement("div");
  container.id = "empty-state";
  container.className = "empty-state";
  const icon = document.createElement("div");
  icon.className = "empty-icon";
  icon.setAttribute(
    "aria-hidden",
    "true"
  );
  icon.textContent = "✦";
  const heading = document.createElement("h2");
  heading.textContent = t("empty.ready_title");
  const description = document.createElement("p");
  description.textContent = t("empty.ready_description");
  const setup = document.createElement("section");
  setup.id = "setup-onboarding";
  setup.className = "setup-onboarding";
  setup.dataset.setupSurface = "onboarding";
  setup.setAttribute("aria-live", "polite");
  setup.hidden = true;
  container.append(icon, heading, description, setup);
  queueMicrotask(renderSetupOnboarding);
  return container;
}

function handleComposerKeyDown(event) {
  if (event.key === "Escape" && state.editingTurn && !state.requestController) {
    event.preventDefault();
    cancelMessageEdit();
    return;
  }

  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    if (state.requestController) {
      queueCurrentMessage();
    } else {
      elements.composer.requestSubmit();
    }
  }
}

function handleComposerClick(event) {
  if (event.target.closest("button, select, option, label, .message-buffer")) {
    return;
  }

  elements.messageInput.focus();
}

function resizeComposer() {
  elements.messageInput.style.height = "auto";
  elements.messageInput.style.height = `${elements.messageInput.scrollHeight}px`;
}

function queueCurrentMessage() {
  if (!state.requestController) {
    return;
  }
  const message = elements.messageInput.value.trim();
  if (!message) {
    return;
  }
  state.messageQueue.push({
    id: `queued-${createSessionId()}`,
    message
  });
  state.messageQueuePaused = false;
  elements.messageInput.value = "";
  resizeComposer();
  renderMessageQueue();
  updateStreamingComposerActions();
  updateComposerStatus();
  elements.messageInput.focus();
}

function renderMessageQueue() {
  elements.messageBufferList.replaceChildren();
  elements.messageBuffer.hidden = state.messageQueue.length === 0;
  elements.messageBufferCount.textContent = t(
    "buffer.count",
    { count: state.messageQueue.length }
  );
  elements.messageBufferRun.hidden = Boolean(state.requestController)
    || state.messageQueue.length === 0
    || Boolean(state.queueEditingId);

  for (const item of state.messageQueue) {
    const row = document.createElement("article");
    row.className = `message-buffer-item${item.steering ? " steering" : ""}`;
    row.dataset.queueId = item.id;
    const actions = document.createElement("div");
    actions.className = "message-buffer-item-actions";

    if (state.queueEditingId === item.id) {
      const editor = document.createElement("textarea");
      editor.className = "message-buffer-editor";
      editor.value = item.draft ?? item.message;
      editor.setAttribute("aria-label", t("buffer.edit_label"));
      const save = createMessageBufferButton(t("action.save"), "save");
      const cancel = createMessageBufferButton(t("action.cancel"), "cancel");
      const remove = createMessageBufferButton(t("action.remove"), "delete");
      save.addEventListener("click", () => saveBufferedMessage(item.id, editor.value));
      cancel.addEventListener("click", cancelBufferedMessageEdit);
      remove.addEventListener("click", () => removeBufferedMessage(item.id));
      editor.addEventListener("input", () => {
        item.draft = editor.value;
      });
      editor.addEventListener("keydown", event => {
        if (event.key === "Escape") {
          event.preventDefault();
          cancelBufferedMessageEdit();
        }
      });
      actions.append(save, cancel, remove);
      row.append(editor, actions);
      queueMicrotask(() => {
        editor.focus();
        editor.setSelectionRange(editor.value.length, editor.value.length);
      });
    } else {
      const body = document.createElement("div");
      body.className = "message-buffer-item-body";
      const content = document.createElement("p");
      content.textContent = item.message;
      body.append(content);
      if (item.error) {
        const error = document.createElement("small");
        error.className = "message-buffer-item-error";
        error.textContent = item.error;
        body.append(error);
      }
      const edit = createMessageBufferButton(t("action.edit"), "edit");
      const remove = createMessageBufferButton(t("action.remove"), "delete");
      const steer = createMessageBufferButton(t("steer.action"), "steer");
      const steeringSupported = activeHarnessSupportsSteering();
      const activeHarnessId = state.activeHarness ?? state.harness;
      const steerExplanation = !state.requestController
        ? t("steer.no_active")
        : !steeringSupported
          ? t(
            "steer.unavailable_harness",
            { harness: benchmarkHarnessLabel(activeHarnessId) }
          )
          : t("steer.available");
      steer.disabled = !state.requestController
        || !steeringSupported
        || state.steeringMessage;
      steer.title = steerExplanation;
      steer.setAttribute("aria-label", steerExplanation);
      const steerTooltip = document.createElement("span");
      steerTooltip.className = "message-buffer-action-tooltip";
      steerTooltip.dataset.tooltip = steerExplanation;
      steerTooltip.append(steer);
      if (steer.disabled) {
        steerTooltip.tabIndex = 0;
        steerTooltip.setAttribute("aria-label", steerExplanation);
      }
      edit.disabled = state.steeringMessage;
      remove.disabled = state.steeringMessage;
      edit.addEventListener("click", () => editBufferedMessage(item.id));
      remove.addEventListener("click", () => removeBufferedMessage(item.id));
      steer.addEventListener("click", () => steerBufferedMessage(item.id));
      actions.append(edit, remove, steerTooltip);
      row.append(body, actions);
    }

    elements.messageBufferList.append(row);
  }
}

function createMessageBufferButton(label, icon) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "message-buffer-action";
  button.dataset.action = icon;
  button.setAttribute("aria-label", label);
  button.title = label;
  button.append(createMessageBufferIcon(icon));
  return button;
}

function createMessageBufferIcon(icon) {
  const namespace = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(namespace, "svg");
  svg.setAttribute("viewBox", "0 0 20 20");
  svg.setAttribute("aria-hidden", "true");
  const path = document.createElementNS(namespace, "path");
  const paths = {
    edit: "M13.9 2.9a1.5 1.5 0 0 1 2.2 0l1 1a1.5 1.5 0 0 1 0 2.2L7.2 16H3v-4.2l10.9-8.9ZM5 12.6V14h1.4l7.7-7.7-1.4-1.4L5 12.6Z",
    delete: "M7 3h6l1 2h3v2H3V5h3l1-2Zm-2 5h10l-1 9H6L5 8Zm3 2v5h1v-5H8Zm3 0v5h1v-5h-1Z",
    steer: "M3 5h7a5 5 0 0 1 5 5v1.2l2-2V14h-4.8l2-2V10a3 3 0 0 0-3-3H3V5Z",
    save: "M3.8 10.2 8 14.4 16.4 6l-1.5-1.5L8 11.4 5.3 8.7l-1.5 1.5Z",
    cancel: "m5.7 4.3 4.3 4.3 4.3-4.3 1.4 1.4-4.3 4.3 4.3 4.3-1.4 1.4-4.3-4.3-4.3 4.3-1.4-1.4 4.3-4.3-4.3-4.3 1.4-1.4Z"
  };
  path.setAttribute("d", paths[icon] ?? paths.edit);
  svg.append(path);
  return svg;
}

function editBufferedMessage(id) {
  state.queueEditingId = id;
  renderMessageQueue();
  updateComposerStatus();
}

function saveBufferedMessage(id, value) {
  const message = value.trim();
  if (!message) {
    showToast(t("buffer.empty_error"));
    return;
  }
  const item = state.messageQueue.find(candidate => candidate.id === id);
  if (!item) {
    return;
  }
  item.message = message;
  delete item.draft;
  state.queueEditingId = null;
  state.messageQueuePaused = false;
  renderMessageQueue();
  updateComposerStatus();
  scheduleMessageQueueDispatch();
}

function cancelBufferedMessageEdit() {
  const item = state.messageQueue.find(
    candidate => candidate.id === state.queueEditingId
  );
  if (item) {
    delete item.draft;
  }
  state.queueEditingId = null;
  renderMessageQueue();
  updateComposerStatus();
  scheduleMessageQueueDispatch();
}

function removeBufferedMessage(id) {
  state.messageQueue = state.messageQueue.filter(item => item.id !== id);
  if (state.queueEditingId === id) {
    state.queueEditingId = null;
  }
  renderMessageQueue();
  updateComposerStatus();
  scheduleMessageQueueDispatch();
}

function resumeMessageQueue() {
  state.messageQueuePaused = false;
  scheduleMessageQueueDispatch();
}

function scheduleMessageQueueDispatch() {
  if (
    state.requestController
    || state.conversationTransitioning
    || state.queueEditingId
    || state.queuedDispatchMessage
    || state.messageQueuePaused
    || state.steeringMessage
    || state.messageQueue.length === 0
  ) {
    renderMessageQueue();
    return;
  }

  state.queuedDispatchMessage = state.messageQueue.shift();
  renderMessageQueue();
  queueMicrotask(() => elements.composer.requestSubmit(elements.sendButton));
}

function activeHarnessSupportsSteering() {
  if (state.interactionMode !== "execute") {
    return false;
  }
  const harnessId = state.activeHarness ?? state.harness;
  return harnessId === "codex" || harnessId === "qwen-code";
}

async function steerBufferedMessage(id) {
  const item = state.messageQueue.find(candidate => candidate.id === id);
  if (
    !item
    || !state.requestController
    || !activeHarnessSupportsSteering()
    || !item.message
    || item.message.length > 16_384
    || state.steeringMessage
  ) {
    return;
  }

  const message = item.message;
  const harnessId = state.activeHarness ?? state.harness;
  const assistant = state.activeAssistant;
  const messageId = `steer-${createSessionId()}`;
  state.steeringMessage = true;
  item.steering = true;
  item.error = null;
  renderMessageQueue();
  updateStreamingComposerActions();
  updateComposerStatus();

  let accepted = false;
  try {
    const result = await fetchJson(
      `/api/harnesses/${encodeURIComponent(harnessId)}/steer`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          sessionId: state.browserSessionId,
          message,
          messageId
        })
      }
    );
    if (!result?.accepted) {
      throw new Error("The harness did not accept the steering message.");
    }
    accepted = true;

    const lastHistory = state.history.at(-1);
    const historyMessage = { role: "user", content: message };
    if (lastHistory?.role === "assistant") {
      state.history.splice(state.history.length - 1, 0, historyMessage);
    } else {
      state.history.push(historyMessage);
    }
    appendSteeredMessage(message, assistant, harnessId);
    state.messageQueue = state.messageQueue.filter(candidate => candidate.id !== id);
    if (assistant) {
      addActivity(
        assistant,
        {
          type: "harness.steer.accepted",
          message: t(
            "steer.accepted",
            { harness: benchmarkHarnessLabel(harnessId) }
          ),
          elapsedMilliseconds: elapsedSince(assistant)
        },
        false
      );
    }
  } catch (error) {
    item.error = error.message;
    state.messageQueuePaused = true;
    showToast(error.message);
  } finally {
    item.steering = false;
    state.steeringMessage = false;
    renderMessageQueue();
    updateStreamingComposerActions();
    updateComposerStatus();
    if (accepted) {
      scheduleMessageQueueDispatch();
    }
  }
}

function cancelActiveRequest() {
  if (!state.requestController) {
    return;
  }
  state.messageQueuePaused = true;
  state.requestController.abort();
}

function appendSteeredMessage(message, assistant, harnessId) {
  const element = document.createElement("article");
  element.className = "message user steered-message";
  element.dataset.harness = harnessId;
  const content = document.createElement("div");
  content.className = "message-content";
  content.textContent = message;
  const note = document.createElement("small");
  note.className = "message-attachment-note";
  note.textContent = t(
    "steer.accepted",
    { harness: benchmarkHarnessLabel(harnessId) }
  );
  content.append(document.createElement("br"), note);
  element.append(content);
  if (assistant?.container?.parentNode === elements.messages) {
    elements.messages.insertBefore(element, assistant.container);
  } else {
    elements.messages.append(element);
  }
  resizeObserver.observe(element);
}

async function handleComposerSubmit(event) {
  event.preventDefault();

  if (state.requestController) {
    queueCurrentMessage();
    return;
  }

  const queuedMessage = state.queuedDispatchMessage;
  state.queuedDispatchMessage = null;
  const message = queuedMessage?.message ?? elements.messageInput.value.trim();

  if (!message) {
    return;
  }

  const autoModelHarness = state.interactionMode === "execute"
    && state.harness === "auto-model-harness";
  const selectedModel = autoModelHarness
    ? "auto"
    : elements.modelSelector.value;

  if (!await ensureCloudImageApproval(selectedModel)) {
    if (queuedMessage) {
      state.messageQueue.unshift(queuedMessage);
      renderMessageQueue();
    }
    return;
  }

  const requestAttachments = state.attachments.map(
    attachment => ({
      id: attachment.id,
      fileName: attachment.fileName,
      mimeType: attachment.mimeType,
      base64Data: attachment.base64Data,
      declaredBytes: attachment.declaredBytes
    })
  );
  state.autoFollow = true;
  updateJumpControl();
  elements.emptyState?.remove();
  const historyIndex = state.editingTurn?.historyIndex ?? state.history.length;

  if (state.editingTurn) {
    removeConversationFrom(state.editingTurn.element);
    state.history = state.history.slice(
      0,
      historyIndex
    );
    state.editingTurn = null;
    elements.composer.classList.remove("editing");
  }

  const requestHistory = state.history.slice(
    0,
    historyIndex
  );
  state.history = [
    ...requestHistory,
    {
      role: "user",
      content: message
    }
  ];
  state.conversationState = "running";
  setPersistenceStatus(
    activeWorkspaceProfile()?.historyEnabled
      ? "Saving"
      : "History disabled"
  );
  appendUserMessage(
    message,
    historyIndex,
    requestAttachments
  );
  const conversationVersion = state.conversationVersion;
  const controller = new AbortController();
  const assistant = appendAssistantMessage({
    modelSelectionOrigin: selectedModel === "auto"
      ? "agent"
      : "user"
  });
  state.activeAssistant = assistant;
  if (!queuedMessage) {
    elements.messageInput.value = "";
  }
  clearAttachments();
  resizeComposer();
  state.requestController = controller;
  state.activeHarness = state.harness;
  setStreamingState(true);
  requestAnimationFrame(scrollToBottom);
  await refreshRuntimeStatus();
  scheduleRuntimeRefresh();
  const compactContext = state.compactContextNextRequest;
  state.compactContextNextRequest = false;

  let continueBufferedMessages = true;
  try {
    const response = await fetch(
      "/api/chat/stream",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          message,
          model: selectedModel,
          history: requestHistory,
          interactionMode: state.interactionMode,
          harness: state.harness,
          approvalPolicy: state.approvalPolicy,
          browserSessionId: state.browserSessionId,
          conversationSessionId: state.conversationSessionId,
          webSearchEnabled: state.webEnabled,
          images: requestAttachments,
          compactContext,
          autoModelHarness
        }),
        signal: controller.signal
      }
    );

    if (!response.ok || !response.body) {
      throw new Error(`HTTP ${response.status}`);
    }

    const outcome = await consumeEventStream(response.body, assistant);

    if (
      outcome.completed
      && state.conversationVersion === conversationVersion
    ) {
      state.history.push(
        {
          role: "assistant",
          content: outcome.answer
        }
      );
      state.conversationState = "completed";
      if (state.requestController === controller) {
        state.requestController = null;
        state.activeAssistant = null;
        setStreamingState(false);
        void refreshRuntimeStatus();
        scheduleRuntimeRefresh();
      }
      await refreshSessions();
      await refreshGit();
    }
  } catch (error) {
    if (error.name === "AbortError") {
      continueBufferedMessages = false;
      state.messageQueuePaused = true;
      state.conversationState = "cancelled";
      addActivity(
        assistant,
        {
          type: "request.cancelled",
          message: "Request canceled by the user.",
          elapsedMilliseconds: elapsedSince(assistant)
        },
        false
      );
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Canceled", false);
      await refreshAssistantReviewAfterCancellation(
        assistant
      );
    } else {
      state.conversationState = "failed";
      addActivity(
        assistant,
        {
          type: "client.error",
          message: error.message,
          elapsedMilliseconds: elapsedSince(assistant)
        },
        true
      );
      assistant.answer.textContent ||= "Could not complete the response.";
      assistant.answer.classList.add("error");
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Failed", true);
    }
  } finally {
    if (state.requestController === controller) {
      state.requestController = null;
      state.activeAssistant = null;
      setStreamingState(false);
      await refreshRuntimeStatus();
      scheduleRuntimeRefresh();
      await refreshSessions();
      await refreshGit();
    }

    if (
      state.autoFollow
      && state.conversationVersion === conversationVersion
    ) {
      requestAnimationFrame(scrollToBottom);
    }

    elements.messageInput.focus();
    if (continueBufferedMessages) {
      state.messageQueuePaused = false;
    }
    renderMessageQueue();
    scheduleMessageQueueDispatch();
  }
}

function appendUserMessage(message, historyIndex, attachments = []) {
  const element = document.createElement("article");
  element.className = "message user";
  const content = document.createElement("div");
  content.className = "message-content";
  content.textContent = message;

  if (attachments.length > 0) {
    const attachmentNote = document.createElement("small");
    attachmentNote.className = "message-attachment-note";
    attachmentNote.textContent =
      `${attachments.length} attached image${attachments.length === 1 ? "" : "s"}`
      + `${attachments.length === 1 ? "" : "s"} · bytes not persisted`;
    content.append(document.createElement("br"), attachmentNote);
  }
  const actions = document.createElement("div");
  actions.className = "message-actions";
  const editButton = createMessageActionButton(
    "Edit",
    "Edit message"
  );
  editButton.classList.add("edit-message");
  editButton.addEventListener(
    "click",
    () => startMessageEdit(
      element,
      message,
      historyIndex
    )
  );
  actions.append(editButton);
  element.append(content, actions);
  elements.messages.append(element);
  resizeObserver.observe(element);
}

function appendAssistantMessage(options = {}) {
  const container = document.createElement("article");
  container.className = "message assistant";

  const modelNotice = document.createElement("p");
  modelNotice.className = "model-selection-note";
  modelNotice.hidden = true;
  const progress = document.createElement("p");
  progress.className = "assistant-progress";
  progress.setAttribute("role", "status");
  progress.textContent = "Thinking… · 0 ms";

  const details = document.createElement("details");
  details.className = "activity";
  details.open = false;
  const summary = document.createElement("summary");
  summary.textContent = "Technical details";
  summary.setAttribute("aria-label", "Request technical details");
  const activityList = document.createElement("div");
  activityList.className = "activity-list";
  const sessionHeader = document.createElement("div");
  sessionHeader.className = "execution-session-header";
  sessionHeader.hidden = true;
  const planPanel = document.createElement("details");
  planPanel.className = "execution-plan";
  planPanel.hidden = true;
  planPanel.open = true;
  const planSummary = document.createElement("summary");
  const planBody = document.createElement("div");
  planBody.className = "execution-plan-body";
  planPanel.append(planSummary, planBody);
  details.append(summary, sessionHeader, activityList);

  const answer = document.createElement("div");
  answer.className = "assistant-response assistant-answer pending";
  const workActivity = document.createElement("section");
  workActivity.className = "assistant-work";
  workActivity.hidden = true;
  const sources = document.createElement("details");
  sources.className = "assistant-sources";
  sources.hidden = true;
  const sourcesSummary = document.createElement("summary");
  sourcesSummary.textContent = "Sources";
  const sourcesList = document.createElement("ol");
  sourcesList.className = "assistant-source-list";
  sources.append(sourcesSummary, sourcesList);
  const actions = document.createElement("div");
  actions.className = "message-actions assistant-actions";
  const copyButton = createMessageActionButton(
    "Copy",
    "Copy response"
  );
  copyButton.classList.add("copy-message");
  copyButton.disabled = true;
  const reviewButton = createMessageActionButton(
    "Review changes",
    "Review changes from this execution"
  );
  reviewButton.classList.add("review-changes");
  reviewButton.hidden = true;
  actions.append(reviewButton, copyButton);
  container.append(
    modelNotice,
    progress,
    planPanel,
    workActivity,
    answer,
    sources,
    details,
    actions
  );
  elements.messages.append(container);

  const assistant = {
    container,
    answer,
    modelNotice,
    progress,
    activeReasoning: null,
    activeResponse: null,
    hasReasoning: false,
    hasResponse: false,
    reasoningBlockCount: 0,
    responseBlockCount: 0,
    details,
    summary,
    activityList,
    sessionHeader,
    planPanel,
    planSummary,
    planBody,
    workActivity,
    workNarrative: null,
    actionItems: new Map(),
    modelSelectionOrigin: options.modelSelectionOrigin ?? null,
    activityGroups: new Map(),
    technicalEventCount: 0,
    startedAt: performance.now(),
    clockFrame: null,
    lastClockUpdate: 0,
    recovered: false,
    rawAnswer: "",
    sources,
    sourcesSummary,
    sourcesList,
    copyButton,
    reviewButton,
    executionSession: null
  };
  copyButton.addEventListener(
    "click",
    () => copyText(
      assistant.rawAnswer,
      copyButton,
      "Response copied"
    )
  );
  reviewButton.addEventListener(
    "click",
    () => openChangeReview(assistant.executionSession?.id)
  );
  details.addEventListener(
    "toggle",
    () => {
      if (state.autoFollow) {
        requestAnimationFrame(scrollToBottom);
      }
    }
  );
  resizeObserver.observe(container);
  startElapsedClock(assistant);
  return assistant;
}

function renderAssistantSources(assistant, citations) {
  assistant.sourcesList.replaceChildren();
  const safeCitations = (citations ?? []).filter(
    citation => {
      try {
        return new URL(citation.url).protocol === "https:";
      } catch {
        return false;
      }
    }
  );
  assistant.sources.hidden = safeCitations.length === 0;
  assistant.sourcesSummary.textContent =
    `Sources (${safeCitations.length})`;

  for (const citation of safeCitations) {
    const item = document.createElement("li");
    const link = document.createElement("a");
    link.href = citation.url;
    link.target = "_blank";
    link.rel = "noopener noreferrer";
    link.textContent = citation.title || new URL(citation.url).hostname;
    item.append(link);
    assistant.sourcesList.append(item);
  }
}

function startElapsedClock(assistant) {
  const update = timestamp => {
    if (timestamp - assistant.lastClockUpdate >= 250) {
      assistant.progress.textContent =
        `${assistant.activeReasoning ? "Thinking" : "Thinking…"} · `
        + formatElapsed(elapsedSince(assistant));
      assistant.lastClockUpdate = timestamp;
    }

    assistant.clockFrame = requestAnimationFrame(update);
  };
  assistant.clockFrame = requestAnimationFrame(update);
}

function renderModelSelection(assistant, model, origin) {
  const message = origin === "user"
    ? `Model ${model} selected by the user.`
    : origin === "fallback"
      ? `Model ${model} selected as fallback by the Host.`
      : `Model ${model} routed by the agent.`;
  assistant.modelNotice.textContent = message;
  assistant.modelNotice.hidden = false;
}

function appendAssistantReasoning(assistant, delta, contentBlockId = null) {
  if (!delta) {
    return;
  }

  closeAssistantResponse(assistant);
  if (
    assistant.activeReasoning
    && contentBlockId
    && assistant.activeReasoning.contentBlockId !== contentBlockId
  ) {
    closeAssistantReasoning(assistant);
  }

  if (!assistant.activeReasoning) {
    const details = document.createElement("details");
    details.className = "assistant-reasoning";
    details.dataset.timelineKind = "thinking";
    details.dataset.block = String(++assistant.reasoningBlockCount);
    details.dataset.deltaCount = "0";
    if (contentBlockId) {
      details.dataset.contentBlockId = contentBlockId;
    }
    details.open = true;
    const summary = document.createElement("summary");
    summary.textContent = "Thinking";
    summary.setAttribute("aria-label", "Reasoning provided by the model");
    const body = document.createElement("div");
    body.className = "assistant-reasoning-body";
    details.append(summary, body);
    assistant.workActivity.hidden = false;
    assistant.workActivity.append(details);
    assistant.activeReasoning = {
      details,
      body,
      raw: "",
      contentBlockId
    };
  }

  assistant.activeReasoning.raw += delta;
  assistant.activeReasoning.body.textContent = assistant.activeReasoning.raw;
  assistant.activeReasoning.body.scrollTop =
    assistant.activeReasoning.body.scrollHeight;
  assistant.activeReasoning.details.dataset.deltaCount = String(
    Number(assistant.activeReasoning.details.dataset.deltaCount) + 1
  );
  assistant.hasReasoning = true;
  assistant.progress.hidden = false;
  assistant.progress.textContent =
    `Thinking · ${formatElapsed(elapsedSince(assistant))}`;
}

function closeAssistantReasoning(assistant) {
  if (!assistant.activeReasoning) {
    return;
  }

  assistant.activeReasoning.details.open = false;
  assistant.activeReasoning = null;
}

function ensureAssistantResponse(
  assistant,
  contentBlockId = null,
  promoteLatest = true
) {
  if (
    assistant.activeResponse
    && (
      !contentBlockId
      || assistant.activeResponse.contentBlockId === contentBlockId
    )
  ) {
    return assistant.activeResponse;
  }

  closeAssistantResponse(assistant);
  let body = assistant.answer;
  if (assistant.hasResponse) {
    body = document.createElement("div");
    body.className = "assistant-response pending";
    if (promoteLatest) {
      assistant.answer.classList.remove("assistant-answer");
      body.classList.add("assistant-answer");
      assistant.answer = body;
    }
  }

  body.dataset.timelineKind = "response";
  body.dataset.block = String(++assistant.responseBlockCount);
  body.dataset.deltaCount = "0";
  if (contentBlockId) {
    body.dataset.contentBlockId = contentBlockId;
  } else {
    delete body.dataset.contentBlockId;
  }
  assistant.workActivity.hidden = false;
  assistant.workActivity.append(body);
  assistant.hasResponse = true;
  assistant.activeResponse = {
    body,
    raw: "",
    contentBlockId
  };
  return assistant.activeResponse;
}

function appendAssistantResponse(
  assistant,
  delta,
  renderedHtml,
  contentBlockId,
  aggregateMarkdown,
  promoteLatest = true
) {
  if (!delta) {
    return;
  }

  closeAssistantReasoning(assistant);
  const response = ensureAssistantResponse(
    assistant,
    contentBlockId,
    promoteLatest
  );
  response.raw += delta;
  response.body.dataset.deltaCount = String(
    Number(response.body.dataset.deltaCount) + 1
  );
  renderAssistantResponse(
    assistant,
    response,
    renderedHtml,
    response.raw,
    aggregateMarkdown
  );
  assistant.progress.hidden = true;
}

function closeAssistantResponse(assistant) {
  if (!assistant.activeResponse) {
    return;
  }

  assistant.activeResponse.body.classList.remove("pending");
  assistant.activeResponse = null;
}

function closeAssistantContent(assistant) {
  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
}

function isAssistantContentBoundary(streamEvent) {
  return Boolean(streamEvent.localAction)
    || streamEvent.type === "agent.toolset-requested"
    || streamEvent.type === "action.recovery-decision-required";
}

function ensureWorkNarrative(assistant, text, replace = false) {
  if (!assistant.workNarrative) {
    assistant.workNarrative = document.createElement("p");
    assistant.workNarrative.className = "assistant-work-narrative";
    assistant.workActivity.append(assistant.workNarrative);
  }

  if (replace || !assistant.workNarrative.textContent) {
    assistant.workNarrative.textContent = text;
  }
}

function isVisibleWorkAction(action) {
  return new Set([
    "create_file",
    "create_files",
    "write_file",
    "replace_text",
    "apply_patch",
    "delete_paths",
    "create_directory"
  ]).has(action?.tool);
}

function actionDisplayLabel(tool) {
  return {
    create_file: "Create",
    create_files: "Create files",
    write_file: "Escrever",
    replace_text: "Edit",
    apply_patch: "Apply patch",
    delete_paths: "Delete",
    create_directory: "Create folder"
  }[tool] ?? tool;
}

function actionTarget(action) {
  const prefix = `${action.tool}:`;
  return action.summary?.startsWith(prefix)
    ? action.summary.slice(prefix.length).trim()
    : action.summary;
}

function actionStateLabel(stateValue) {
  return {
    proposed: "Preparing",
    approved: "Approved",
    executing: "Executing…",
    completed: "Completed",
    failed: "Failed",
    rejected: "Rejected",
    revised: "Revised"
  }[stateValue] ?? stateValue;
}

function summarizeActionPreview(value) {
  const lines = (value ?? "").replaceAll("\r\n", "\n").split("\n");

  if (lines.length <= 14) {
    return lines.join("\n");
  }

  const omitted = lines.length - 12;
  return [
    ...lines.slice(0, 6),
    `… ${omitted} omitted lines …`,
    ...lines.slice(-6)
  ].join("\n");
}

function joinWorkspacePath(workspacePath, relativePath) {
  if (!workspacePath || !relativePath) {
    return relativePath || workspacePath || "";
  }

  if (/^[a-z]:[\\/]/i.test(relativePath) || relativePath.startsWith("\\\\")) {
    return relativePath;
  }

  return `${workspacePath.replace(/[\\/]+$/, "")}\\${relativePath.replaceAll("/", "\\")}`;
}

async function hydrateWorkActionPath(assistant, item, relativePath) {
  if (item.path.dataset.hydrated === "true") {
    return;
  }

  const executionSessionId = assistant.executionSession?.id;

  if (!executionSessionId) {
    return;
  }

  try {
    const review = await fetchJson(
      `/api/execution-sessions/${encodeURIComponent(executionSessionId)}/review`
    );
    item.path.textContent = joinWorkspacePath(
      review.workspacePath,
      relativePath
    );
    item.path.dataset.hydrated = "true";
  } catch {
    item.path.textContent = relativePath;
  }
}

function upsertWorkAction(assistant, streamEvent) {
  const action = streamEvent.localAction;

  if (!isVisibleWorkAction(action)) {
    return;
  }

  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
  assistant.workActivity.hidden = false;
  ensureWorkNarrative(
    assistant,
    "I will execute the requested changes and show only the affected files."
  );
  assistant.progress.hidden = false;
  assistant.progress.textContent =
    `Executing… · ${formatElapsed(elapsedSince(assistant))}`;
  const relativePath = actionTarget(action);
  let item = assistant.actionItems.get(action.actionId);

  if (!item) {
    const details = document.createElement("details");
    details.className = "work-action";
    details.dataset.timelineKind = "action";
    details.dataset.actionId = action.actionId;
    details.dataset.eventType = streamEvent.type;
    const summary = document.createElement("summary");
    const icon = document.createElement("span");
    icon.className = "work-action-icon";
    icon.textContent = "⌁";
    icon.setAttribute("aria-hidden", "true");
    const label = document.createElement("span");
    label.className = "work-action-label";
    label.textContent = actionDisplayLabel(action.tool);
    const link = document.createElement("a");
    link.className = "work-action-file";
    link.href = "#";
    link.textContent = relativePath;
    link.setAttribute("aria-label", `Open review for ${relativePath}`);
    const status = document.createElement("span");
    status.className = "work-action-status";
    const body = document.createElement("div");
    body.className = "work-action-body";
    const path = document.createElement("code");
    path.className = "work-action-path";
    path.textContent = relativePath;
    const preview = document.createElement("pre");
    preview.className = "work-action-preview";
    body.append(path, preview);
    summary.append(icon, label, link, status);
    details.append(summary, body);
    assistant.workActivity.append(details);
    item = {
      details,
      icon,
      label,
      link,
      status,
      path,
      preview
    };
    assistant.actionItems.set(action.actionId, item);
    details.addEventListener(
      "toggle",
      () => {
        if (details.open) {
          void hydrateWorkActionPath(
            assistant,
            item,
            relativePath
          );
        }
      }
    );
    link.addEventListener(
      "click",
      event => {
        event.preventDefault();
        event.stopPropagation();
        void openChangeReview(
          assistant.executionSession?.id,
          relativePath
        );
      }
    );
  }

  item.details.dataset.eventType = streamEvent.type;
  item.details.dataset.state = action.state;
  item.status.textContent = actionStateLabel(action.state);
  item.icon.textContent = action.state === "completed"
    ? "✓"
    : action.state === "failed" || action.state === "rejected"
      ? "!"
      : "⌁";
  item.preview.textContent = summarizeActionPreview(
    action.preview || action.resultOutput || "No textual content to display."
  );
  item.details.open = action.state === "failed";
}

function addToolsetRequest(assistant, streamEvent) {
  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
  assistant.workActivity.hidden = false;
  const item = document.createElement("p");
  item.className = "assistant-toolset-request";
  item.dataset.timelineKind = "toolset";
  item.textContent = streamEvent.message;
  assistant.workActivity.append(item);
}

async function consumeEventStream(stream, assistant) {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let answer = "";
  let completed = false;

  while (true) {
    const result = await reader.read();

    if (result.done) {
      break;
    }

    buffer += decoder.decode(result.value, { stream: true });
    const blocks = buffer.split("\n\n");
    buffer = blocks.pop() ?? "";

    for (const block of blocks) {
      const data = block
        .split("\n")
        .filter(line => line.startsWith("data:"))
        .map(line => line.slice(5).trimStart())
        .join("\n");

      if (!data) {
        continue;
      }

      const streamEvent = JSON.parse(data);
      state.conversationSessionId =
        streamEvent.conversationSessionId ?? state.conversationSessionId;

      const selectedHarness = /^harness\.(.+)-selected$/.exec(streamEvent.type);
      if (selectedHarness) {
        state.activeHarness = selectedHarness[1];
        updateStreamingComposerActions();
      }

      if (streamEvent.contextUsage) {
        state.contextUsage = streamEvent.contextUsage;
        renderContextUsage();
      }

      if (
        streamEvent.type === "target.model-resolved"
        && streamEvent.selectedModel
      ) {
        state.activeAgentModel = streamEvent.selectedModel;
        state.activeAgentRole = "primary";
        updateActiveAgentLabel();
        void refreshSelectedModelCapabilities(
          streamEvent.selectedModel,
          "primary"
        );
        renderModelSelection(
          assistant,
          streamEvent.selectedModel,
          assistant.modelSelectionOrigin
        );
      } else if (
        streamEvent.type.startsWith("cloud.local-fallback")
        && streamEvent.selectedModel
      ) {
        state.activeAgentModel = streamEvent.selectedModel;
        state.activeAgentRole = "fallback";
        updateActiveAgentLabel();
        void refreshSelectedModelCapabilities(
          streamEvent.selectedModel,
          "fallback"
        );
        renderModelSelection(
          assistant,
          streamEvent.selectedModel,
          "fallback"
        );
      }

      updateExecutionSession(
        assistant,
        streamEvent.executionSession
      );

      if (
        streamEvent.type === "session-created"
        || streamEvent.type === "session-persisted"
      ) {
        setPersistenceStatus("Saved locally");
      } else if (
        streamEvent.type.startsWith("session-")
        && (
          streamEvent.type.includes("failed")
          || streamEvent.type.includes("invalid")
          || streamEvent.type.includes("too-large")
        )
      ) {
        setPersistenceStatus("Save failed");
      }

      if (isAssistantContentBoundary(streamEvent)) {
        closeAssistantContent(assistant);
      }

      if (streamEvent.type === "reasoning.delta") {
        appendAssistantReasoning(
          assistant,
          streamEvent.reasoningDelta ?? "",
          streamEvent.contentBlockId ?? null
        );
      } else if (streamEvent.type === "response.delta") {
        const delta = streamEvent.delta ?? "";
        answer += delta;
        appendAssistantResponse(
          assistant,
          delta,
          streamEvent.responseSegmentHtml
            ?? streamEvent.renderedHtml
            ?? "",
          streamEvent.contentBlockId ?? null,
          answer
        );
      } else if (streamEvent.type === "response.completed") {
        completed = true;
        closeAssistantContent(assistant);
        const responseTail = streamEvent.responseTail ?? "";
        if (responseTail) {
          const aggregateAnswer = answer
            ? `${answer}\n\n---\n${responseTail}`
            : responseTail;
          appendAssistantResponse(
            assistant,
            responseTail,
            streamEvent.responseTailHtml
              ?? streamEvent.renderedHtml
              ?? "",
            `terminal:${streamEvent.requestId}`,
            aggregateAnswer,
            !assistant.hasResponse
          );
          answer = aggregateAnswer;
          closeAssistantResponse(assistant);
        } else if (!assistant.hasResponse && streamEvent.renderedHtml) {
          appendAssistantResponse(
            assistant,
            answer || " ",
            streamEvent.renderedHtml,
            `terminal:${streamEvent.requestId}`,
            answer
          );
          closeAssistantResponse(assistant);
        } else {
          assistant.rawAnswer = answer;
          assistant.copyButton.disabled = !answer;
        }
        renderAssistantSources(
          assistant,
          streamEvent.citations
        );
        assistant.answer.classList.remove("pending");
        addActivity(assistant, streamEvent, false);
        finishActivity(
          assistant,
          `${assistant.recovered ? "Recovered" : "Completed"} · `
            + formatElapsed(streamEvent.elapsedMilliseconds),
          assistant.recovered
        );
        assistant.reviewButton.hidden =
          !assistant.executionSession?.reviewAvailable;
      } else if (streamEvent.type === "error") {
        closeAssistantContent(assistant);
        const errorText = `${streamEvent.error.message}\n`
          + `Reference: ${streamEvent.error.traceId}`;
        const response = ensureAssistantResponse(
          assistant,
          `error:${streamEvent.error.traceId}`
        );
        response.raw = errorText;
        response.body.classList.remove("pending");
        response.body.classList.add("error");
        response.body.textContent = errorText;
        assistant.rawAnswer ||= errorText;
        assistant.copyButton.disabled = !assistant.rawAnswer;
        closeAssistantResponse(assistant);
        addActivity(
          assistant,
          {
            ...streamEvent,
            message:
              `${streamEvent.error.stage}: `
              + `${streamEvent.error.technicalMessage ?? streamEvent.error.message} `
              + `Trace: ${streamEvent.error.traceId}`
          },
          true
        );
        finishActivity(
          assistant,
          `Failed · ${streamEvent.error.traceId}`,
          true
        );
        addTraceDiagnosticActions(assistant, streamEvent.error);
      } else if (streamEvent.type === "request.cancelled") {
        closeAssistantContent(assistant);
        addActivity(assistant, streamEvent, false);
        assistant.answer.classList.remove("pending");
        finishActivity(assistant, "Canceled", false);
      } else if (
        streamEvent.type === "action.awaiting-approval"
        && streamEvent.localAction
      ) {
        addApprovalActivity(
          assistant,
          streamEvent
        );
      } else if (
        streamEvent.localAction
        && updateApprovalActivity(
          assistant,
          streamEvent
        )
      ) {
      } else if (
        streamEvent.type === "action.recovery-decision-required"
        && streamEvent.recoveryDecision
      ) {
        addRecoveryDecisionActivity(
          assistant,
          streamEvent
        );
      } else if (streamEvent.type === "agent.toolset-requested") {
        addToolsetRequest(
          assistant,
          streamEvent
        );
        addActivity(
          assistant,
          streamEvent,
          false
        );
      } else if (streamEvent.localAction) {
        upsertWorkAction(
          assistant,
          streamEvent
        );
        if (streamEvent.message) {
          addActivity(
            assistant,
            streamEvent,
            streamEvent.type.includes("failed")
              || streamEvent.type.includes("warning")
          );
        }
      } else if (streamEvent.message) {
        if (streamEvent.type === "target-request-recovered") {
          assistant.recovered = true;
        }

        addActivity(
          assistant,
          streamEvent,
          streamEvent.type.includes("failed")
            || streamEvent.type.includes("warning")
            || streamEvent.type === "memory-pressure-detected"
        );
      }

      if (
        streamEvent.type === "memory-pressure-detected"
        || streamEvent.type === "target-request-recovered"
        || streamEvent.type.startsWith("resident-model-")
      ) {
        void refreshRuntimeStatus();
      }
    }
  }

  return {
    answer,
    completed
  };
}

function addTraceDiagnosticActions(assistant, error) {
  if (!error?.traceId || assistant.container.querySelector(".trace-diagnostic-actions")) {
    return;
  }

  const actions = document.createElement("div");
  actions.className = "trace-diagnostic-actions";
  const copy = createMessageActionButton("Copy trace", "Copy trace identifier");
  const view = createMessageActionButton("View diagnostic", "Open sanitized local diagnostic");
  view.disabled = error.diagnosticsPersisted !== true;
  if (view.disabled) {
    view.title = "The local journal did not confirm persistence of this diagnostic.";
  }
  copy.addEventListener("click", () => copyText(error.traceId, copy, "Trace ID copied"));
  view.addEventListener("click", () => openTraceDiagnostic(error.traceId));
  actions.append(copy, view);
  assistant.answer.insertAdjacentElement("afterend", actions);
}

async function openTraceDiagnostic(traceId) {
  elements.traceDiagnosticDialog.dataset.traceId = traceId;
  elements.traceDiagnosticId.textContent = traceId;
  elements.traceDiagnosticStatus.textContent = "Loading local diagnostic...";
  elements.traceDiagnosticFacts.replaceChildren();
  elements.traceDiagnosticTimeline.replaceChildren();
  elements.traceDiagnosticDialog.showModal();

  try {
    const report = await fetchJson(`/api/diagnostics/traces/${encodeURIComponent(traceId)}`);
    renderTraceDiagnostic(report);
  } catch (error) {
    elements.traceDiagnosticStatus.textContent = error.message;
  }
}

function closeTraceDiagnostic() {
  if (elements.traceDiagnosticDialog.open) {
    elements.traceDiagnosticDialog.close();
  }
}

function renderTraceDiagnostic(report) {
  elements.traceDiagnosticStatus.textContent = report.truncated
    ? `Diagnostic bounded to ${report.totalEvents} safe events.`
    : `${report.totalEvents} correlated safe events.`;
  const facts = [
    ["Status", report.status],
    ["Code", report.failureCode ?? "none"],
    ["Stage", report.failureStage ?? "none"],
    ["Provider / model", [report.provider, report.model].filter(Boolean).join(" / ") || "unavailable"],
    ["Coordinator", report.coordinator ?? "unavailable"],
    ["Path", report.executionPath ?? "unavailable"],
    ["Reviewable result", report.reviewAvailable ? "yes" : "no"],
    ["Recommendation", report.recommendation]
  ];
  if (report.contextFit) {
    facts.push([
      "Contexto",
      `input ${report.contextFit.estimatedInputTokens ?? "?"} + reserve ${report.contextFit.reservedOutputTokens ?? "?"} = required ${report.contextFit.requiredContextTokens ?? "?"}; maximum ${report.contextFit.maximumContextTokens ?? "?"}`
    ]);
  }

  for (const [label, value] of facts) {
    const term = document.createElement("dt");
    term.textContent = label;
    const detail = document.createElement("dd");
    detail.textContent = value;
    elements.traceDiagnosticFacts.append(term, detail);
  }

  for (const event of report.events ?? []) {
    const item = document.createElement("li");
    const heading = document.createElement("strong");
    heading.textContent = `${event.sequence}. ${event.code}`;
    const meta = document.createElement("span");
    meta.textContent = `${event.stage} · ${event.status}`;
    const summary = document.createElement("p");
    summary.textContent = event.summary;
    item.append(heading, meta, summary);
    elements.traceDiagnosticTimeline.append(item);
  }
}

function addActivity(assistant, streamEvent, isWarningOrError) {
  if (!streamEvent.message) {
    return;
  }

  const group = ensureActivityGroup(
    assistant,
    streamEvent,
    isWarningOrError
  );
  const row = document.createElement("div");
  row.className = `activity-row${isWarningOrError ? " warning" : ""}`;
  row.dataset.eventType = streamEvent.type;
  const time = document.createElement("span");
  time.className = "activity-time";
  time.textContent = formatElapsed(
    streamEvent.elapsedMilliseconds
  );
  const icon = document.createElement("span");
  icon.className = "activity-icon";
  icon.textContent = activityIconFor(
    streamEvent.type
  );
  icon.setAttribute(
    "aria-hidden",
    "true"
  );
  const message = document.createElement("span");
  message.className = "activity-message";
  message.textContent = streamEvent.message;
  row.append(time, icon, message);
  group.body.append(row);
  group.count++;
  assistant.technicalEventCount++;
  group.countLabel.textContent =
    `${group.count} ${group.count === 1 ? "event" : "events"}`;
  assistant.summary.textContent =
    `Technical details · ${assistant.technicalEventCount} `
    + `${assistant.technicalEventCount === 1 ? "event" : "events"}`;
}

function ensureActivityGroup(assistant, streamEvent, isWarningOrError) {
  const definition = activityGroupFor(
    streamEvent
  );

  const existing = assistant.activityGroups.get(definition.key);

  if (existing) {
    if (isWarningOrError) {
      existing.details.classList.add("warning");
    }

    return existing;
  }

  const details = document.createElement("details");
  details.className = `activity-group${isWarningOrError ? " warning" : ""}`;
  const summary = document.createElement("summary");
  const time = document.createElement("span");
  time.className = "activity-time";
  time.textContent = formatElapsed(
    streamEvent.elapsedMilliseconds
  );
  const icon = document.createElement("span");
  icon.className = "activity-icon";
  icon.textContent = activityIconFor(
    streamEvent.type
  );
  icon.setAttribute(
    "aria-hidden",
    "true"
  );
  const title = document.createElement("strong");
  title.className = "activity-group-title";
  title.textContent = definition.title;
  const countLabel = document.createElement("span");
  countLabel.className = "activity-group-count";
  const body = document.createElement("div");
  body.className = "activity-group-body";
  summary.append(time, icon, title, countLabel);
  details.append(summary, body);
  assistant.activityList.append(details);
  const group = {
    details,
    body,
    countLabel,
    count: 0
  };
  assistant.activityGroups.set(definition.key, group);
  return group;
}

function activityGroupFor(streamEvent) {
  const type = streamEvent.type ?? "";
  const action = streamEvent.localAction;

  if (action?.actionId) {
    return {
      key: `action:${action.actionId}`,
      title: action.summary
    };
  }

  if (
    type.startsWith("action.planning")
    || type.startsWith("execution-plan")
    || type === "execution-step-completed"
  ) {
    return {
      key: "planning",
      title: "Planning"
    };
  }

  if (type.includes("recovery")) {
    return {
      key: "recovery",
      title: "Recovery"
    };
  }

  if (
    type.startsWith("agent.")
    || type.startsWith("target.")
    || type.startsWith("router.")
    || type.startsWith("model.")
    || type.startsWith("ollama.")
  ) {
    return {
      key: "agents",
      title: "Agents and routing"
    };
  }

  if (
    type.startsWith("workspace")
    || type.startsWith("project-")
    || type.startsWith("baseline-")
    || type.startsWith("repository-")
    || type.startsWith("preexisting-")
  ) {
    return {
      key: "workspace",
      title: "Workspace and project"
    };
  }

  if (type.startsWith("validation-")) {
    return {
      key: "validation",
      title: "Validation"
    };
  }

  if (
    type.startsWith("response.")
    || type.startsWith("request.")
    || type.startsWith("turn.")
  ) {
    return {
      key: "response",
      title: "Response"
    };
  }

  return {
    key: "execution",
    title: "Execution"
  };
}

function activityIconFor(type) {
  if (type.includes("error") || type.includes("failed")) {
    return "!";
  }

  if (type.includes("warning") || type.includes("denied")) {
    return "◇";
  }

  if (type.startsWith("action.")) {
    return type.includes("planning")
      ? "⌁"
      : type.includes("completed") || type.includes("applied")
        ? "✓"
        : "›";
  }

  if (type.startsWith("execution-plan")) {
    return "☷";
  }

  if (type.startsWith("agent.") || type.startsWith("target.")) {
    return "⚡";
  }

  if (type.startsWith("validation-")) {
    return "✓";
  }

  return "·";
}

function updateExecutionSession(assistant, session) {
  if (!session) {
    return;
  }

  assistant.executionSession = session;
  state.latestExecutionSessionId = session.id;
  if (session.plan?.objective) {
    assistant.workActivity.hidden = false;
    ensureWorkNarrative(
      assistant,
      session.state === "running"
        ? `Working on: ${session.plan.objective}`
        : `Objective: ${session.plan.objective}`,
      true
    );
  }
  assistant.sessionHeader.hidden = false;
  assistant.sessionHeader.replaceChildren();
  const stateLabel = document.createElement("strong");
  stateLabel.textContent = session.state;
  const coordinator = document.createElement("span");
  coordinator.textContent =
    `Target: ${session.selectedModel || "unavailable"} · `
    + `Specialist: ${session.coordinatorModel} · `
    + `Resident router: ${session.residentModel || "unavailable"} · `
    + session.executionPath;
  coordinator.title = [
    session.routingEvidence
      ? `Auto Model × Harness: ${session.routingEvidence.selectedModel} × ${benchmarkHarnessLabel(session.routingEvidence.selectedHarness)} · ${session.routingEvidence.confidence} · recommendation ${session.routingEvidence.recommendationId.slice(0, 12)}`
      : null,
    session.conformanceIdentity
      ? `Conformance: ${session.conformanceIdentity}`
      : null,
    session.handoffReason
      ? `Handoff: ${session.handoffReason}`
      : null
  ].filter(Boolean).join("\n");
  const counts = document.createElement("span");
  counts.textContent =
    `${session.actionCount} actions · ${session.changedFileCount} files · `
    + `planning ${session.planningFailureCount} · `
    + `tool failures ${session.consecutiveToolFailureCount} · `
    + formatElapsed(session.elapsedMilliseconds);
  assistant.sessionHeader.append(
    stateLabel,
    coordinator,
    counts
  );
  renderExecutionPlan(
    assistant,
    session
  );

  if (session.reviewAvailable && session.state !== "running") {
    assistant.reviewButton.hidden = false;
  }
}

function renderExecutionPlan(assistant, session) {
  const plan = session?.plan;
  if (!plan) {
    assistant.planPanel.hidden = true;
    assistant.planBody.replaceChildren();
    return;
  }

  assistant.planPanel.hidden = false;
  assistant.planSummary.textContent =
    `Plan · ${plan.objective}`;
  assistant.planBody.replaceChildren();
  const list = document.createElement("ol");

  for (const step of plan.steps) {
    const item = document.createElement("li");
    item.className = `plan-step ${step.status}`;
    item.dataset.stepId = step.id;
    const marker = document.createElement("span");
    marker.className = "plan-step-marker";
    marker.textContent = {
      completed: "✓",
      failed: "×",
      blocked: "!",
      skipped: "–",
      "in-progress": "●"
    }[step.status] ?? "○";
    const title = document.createElement("span");
    title.textContent = step.title;
    const status = document.createElement("small");
    status.textContent = step.status;
    if (step.dependencies?.length) {
      status.title = `Depends on: ${step.dependencies.join(", ")}`;
    }
    item.append(marker, title, status);
    list.append(item);
  }

  const activeIndex = plan.steps.findIndex(
    step => step.id === plan.currentStepId
  );
  const displayedStep = activeIndex >= 0
    ? activeIndex + 1
    : Math.min(plan.completedStepCount + 1, plan.steps.length);
  const footer = document.createElement("p");
  footer.className = "execution-plan-progress";
  footer.textContent = plan.completedStepCount === plan.steps.length
    ? `Steps ${plan.steps.length}/${plan.steps.length} · ${session.changedFileCount} changed files`
    : `Step ${displayedStep}/${plan.steps.length} · ${session.changedFileCount} changed files`;
  assistant.planBody.append(list, footer);
}

async function openChangeReview(executionSessionId, focusRelativePath = null) {
  if (!executionSessionId) {
    return;
  }

  elements.changeReviewBody.textContent = "Loading review…";
  elements.undoStatus.textContent = "";
  elements.undoExecution.disabled = true;
  if (!elements.changeReviewDialog.open) {
    elements.changeReviewDialog.showModal();
  }

  try {
    const review = await fetchJson(
      `/api/execution-sessions/${encodeURIComponent(executionSessionId)}/review`
    );
    state.activeReview = review;
    renderChangeReview(review, focusRelativePath);
    await loadGitDelivery(review);
  } catch (error) {
    elements.changeReviewBody.textContent = error.message;
  }
}

async function refreshAssistantReviewAfterCancellation(assistant) {
  const executionSessionId = assistant.executionSession?.id;

  if (!executionSessionId) {
    return;
  }

  for (let attempt = 0; attempt < 4; attempt++) {
    try {
      const review = await fetchJson(
        `/api/execution-sessions/${encodeURIComponent(executionSessionId)}/review`
      );

      if (review.summary.state === "running") {
        await new Promise(
          resolve => setTimeout(resolve, 75)
        );
        continue;
      }

      updateExecutionSession(
        assistant,
        review.summary
      );
      assistant.reviewButton.hidden = !review.summary.reviewAvailable;
      return;
    } catch {
      await new Promise(
        resolve => setTimeout(resolve, 75)
      );
    }
  }
}

function closeChangeReview() {
  state.activeReview = null;
  state.activeDelivery = null;
  state.pendingDeliveryAction = null;
  elements.changeReviewDialog.close();
}

function renderChangeReview(review, focusRelativePath = null) {
  elements.changeReviewBody.replaceChildren();
  const summary = document.createElement("section");
  summary.className = "change-review-summary";
  const heading = document.createElement("h3");
  heading.textContent =
    `${review.summary.state} · Target: ${review.summary.selectedModel || "unavailable"}`;
  const metadata = document.createElement("p");
  metadata.textContent =
    `Specialist: ${review.summary.coordinatorModel} · `
    + `Resident router: ${review.summary.residentModel || "unavailable"} · `
    + `${review.summary.executionPath} · ${review.summary.actionCount} actions · `
    + `${review.summary.changedFileCount} files · `
    + `${formatElapsed(review.summary.elapsedMilliseconds)} · `
    + `${review.summary.completionStatus}`;
  const objective = document.createElement("p");
  objective.textContent = review.objective;
  summary.append(heading, metadata, objective);
  elements.changeReviewBody.append(summary);

  if (review.summary.routingEvidence) {
    const route = review.summary.routingEvidence;
    const routing = document.createElement("section");
    routing.className = "change-review-context routing-evidence";
    const title = document.createElement("h3");
    title.textContent = "Auto Model × Harness";
    const selection = document.createElement("p");
    selection.textContent =
      `${route.selectedModel} × ${benchmarkHarnessLabel(route.selectedHarness)} · `
      + `${route.confidence} · ${route.taskCategory}`
      + (route.fallback ? " · availability fallback" : "");
    const reason = document.createElement("p");
    reason.textContent = route.reason;
    const trace = document.createElement("p");
    trace.className = "runtime-note";
    trace.textContent =
      `${route.routerVersion} · ${route.recommendationVersion} · `
      + `${route.scoringProfileId} v${route.scoringProfileVersion} · `
      + `recommendation ${route.recommendationId.slice(0, 12)}`;
    routing.append(title, selection, reason, trace);
    if (route.supportingRunIds.length > 0) {
      const evidence = document.createElement("details");
      const evidenceSummary = document.createElement("summary");
      evidenceSummary.textContent = `Open supporting evidence (${route.supportingRunIds.length})`;
      const links = document.createElement("div");
      links.className = "benchmark-recommendation-evidence-links";
      for (const runId of route.supportingRunIds) {
        const link = document.createElement("button");
        link.type = "button";
        link.className = "benchmark-result-link";
        link.textContent = `Benchmark ${runId.slice(0, 12)}`;
        link.addEventListener("click", () => openRoutingEvidence(runId));
        links.append(link);
      }
      evidence.append(evidenceSummary, links);
      routing.append(evidence);
    }
    elements.changeReviewBody.append(routing);
  }

  if (review.project) {
    const project = document.createElement("section");
    project.className = "change-review-context";
    const title = document.createElement("h3");
    title.textContent = "Project and baseline";
    const profile = document.createElement("p");
    profile.textContent =
      `${review.project.displayName} · `
      + `${review.project.projectTypes.join(", ") || "no detected type"} · `
      + `${review.baseline?.gitAvailable
        ? `Git ${review.baseline.branch ?? "detached"}`
        : "no Git"}`;
    const dirty = document.createElement("p");
    dirty.textContent = review.baseline?.preExistingDirtyPaths.length
      ? `Pre-existing changes: ${review.baseline.preExistingDirtyPaths.join(", ")}`
      : "No pre-existing changes detected.";
    const instructions = document.createElement("p");
    instructions.textContent = review.appliedInstructionFiles?.length
      ? `Applied instructions: ${review.appliedInstructionFiles.join(", ")}`
      : "No AGENTS.md applied.";
    project.append(title, profile, dirty, instructions);
    elements.changeReviewBody.append(project);
  }

  if (review.summary.plan) {
    const plan = document.createElement("section");
    plan.className = "change-review-context";
    const title = document.createElement("h3");
    title.textContent =
      `Plan · ${review.summary.plan.completedStepCount}/${review.summary.plan.steps.length}`;
    const list = document.createElement("ol");
    for (const step of review.summary.plan.steps) {
      const item = document.createElement("li");
      item.textContent = `${step.status} · ${step.title}`;
      list.append(item);
    }
    plan.append(title, list);
    elements.changeReviewBody.append(plan);
  }

  let focusedFile = null;

  for (const file of review.files) {
    const section = document.createElement("details");
    section.className = "change-file-review";
    section.dataset.relativePath = file.relativePath;
    section.open = focusRelativePath
      ? file.relativePath === focusRelativePath
      : true;
    if (section.open && focusRelativePath) {
      focusedFile = section;
    }
    const title = document.createElement("summary");
    title.textContent =
      `${file.operation === "created" ? "Created" : "Modified"} · ${file.relativePath}`;
    const status = document.createElement("p");
    status.className = file.verified
      ? "verification-ok"
      : "verification-warning";
    status.textContent = file.verified
      ? `Verified · ${file.finalSizeBytes} bytes`
      : "Read verification failed";
    section.append(title, status);

    if (file.preExistingChange) {
      const existing = document.createElement("p");
      existing.className = "preexisting-change";
      existing.textContent =
        "This file already had changes before the session and was also changed by it.";
      section.append(existing);
    }

    if (file.unifiedDiff) {
      const diff = document.createElement("pre");
      diff.className = "change-diff";
      diff.textContent = file.unifiedDiff;
      section.append(diff);
    }

    if (!file.undoAvailable && file.undoDiagnostic) {
      const warning = document.createElement("p");
      warning.className = "verification-warning";
      warning.textContent = file.undoDiagnostic;
      section.append(warning);
    }

    elements.changeReviewBody.append(section);
  }

  if (focusedFile) {
    requestAnimationFrame(
      () => focusedFile.scrollIntoView({
        block: "start"
      })
    );
  }

  if (review.processes.length > 0) {
    const processes = document.createElement("section");
    processes.className = "process-review";
    const heading = document.createElement("h3");
    heading.textContent = "Processes";
    processes.append(heading);

    for (const process of review.processes) {
      const entry = document.createElement("pre");
      const flags = [
        process.timedOut ? "timeout" : null,
        process.cancelled ? "cancelled" : null,
        process.standardOutputTruncated ? "stdout truncated" : null,
        process.standardErrorTruncated ? "stderr truncated" : null
      ].filter(Boolean);
      entry.textContent =
        `${process.executable} ${process.arguments.join(" ")}\n`
        + `cwd: ${process.workingDirectory}\n`
        + `exit: ${process.exitCode} · ${process.durationMilliseconds} ms`
        + `${flags.length ? ` · ${flags.join(", ")}` : ""}\n`
        + `${process.standardOutput}${process.standardError}`;
      processes.append(entry);
    }

    elements.changeReviewBody.append(processes);
  }

  if (review.validation) {
    const validation = document.createElement("section");
    validation.className = "change-review-context validation-results";
    const heading = document.createElement("h3");
    heading.textContent =
      `Validation · ${review.validation.state} · `
      + `${review.validation.profileName ?? "not configured"}`;
    validation.append(heading);

    for (const step of review.validation.steps) {
      const result = document.createElement("p");
      result.className = step.status === "passed"
        ? "verification-ok"
        : "verification-warning";
      result.textContent =
        `${step.label}: ${step.status} · exit ${step.exitCode ?? "n/a"} · `
        + `${step.durationMilliseconds} ms`;
      validation.append(result);
    }

    elements.changeReviewBody.append(validation);
  }

  for (const conflict of review.conflicts ?? []) {
    const warning = document.createElement("p");
    warning.className = "verification-warning";
    warning.textContent =
      `Conflict in ${conflict.relativePath}: expected ${conflict.expectedHash}, `
      + `current ${conflict.currentHash}.`;
    elements.changeReviewBody.append(warning);
  }

  for (const warningText of review.warnings) {
    const warning = document.createElement("p");
    warning.className = "verification-warning";
    warning.textContent = warningText;
    elements.changeReviewBody.append(warning);
  }

  elements.undoExecution.disabled = !review.summary.undoAvailable;
  elements.undoExecution.title = review.summary.undoDiagnostic ?? "";
  elements.validateChanges.disabled = review.files.length === 0;
}

async function openRoutingEvidence(runId) {
  closeChangeReview();
  await openBenchmarks();
  const advanced = document.querySelector(".benchmark-history-advanced");
  if (advanced) {
    advanced.open = true;
  }
  const option = [...elements.benchmarkHistory.options].some(item => item.value === runId);
  if (!option) {
    elements.benchmarkStatus.textContent =
      "Supporting evidence is unavailable in the current filtered history.";
    return;
  }
  elements.benchmarkHistory.value = runId;
  await openPersistedBenchmark();
}

async function loadGitDelivery(review) {
  const existing = elements.changeReviewBody.querySelector(
    ".git-delivery-panel"
  );
  existing?.remove();

  if (!review.project?.repository?.isGitRepository) {
    renderGitDeliveryUnavailable(
      "Prepare delivery is unavailable because this workspace is not a Git repository."
    );
    return;
  }

  const loading = document.createElement("section");
  loading.className = "change-review-context git-delivery-panel";
  loading.textContent = "Loading Git delivery status...";
  elements.changeReviewBody.append(loading);

  try {
    state.activeDelivery = await fetchJson(
      `/api/execution-sessions/${encodeURIComponent(review.summary.id)}/delivery`
    );
    renderGitDelivery(state.activeDelivery);
  } catch (error) {
    state.activeDelivery = null;
    renderGitDeliveryUnavailable(error.message);
  }
}

function renderGitDeliveryUnavailable(message) {
  elements.changeReviewBody.querySelector(".git-delivery-panel")?.remove();
  const panel = document.createElement("section");
  panel.className = "change-review-context git-delivery-panel";
  const heading = document.createElement("h3");
  heading.textContent = "Prepare delivery";
  const diagnostic = document.createElement("p");
  diagnostic.className = "verification-warning";
  diagnostic.textContent = message;
  panel.append(heading, diagnostic);
  elements.changeReviewBody.append(panel);
}

function renderGitDelivery(delivery) {
  elements.changeReviewBody.querySelector(".git-delivery-panel")?.remove();
  const panel = document.createElement("section");
  panel.className = "change-review-context git-delivery-panel";
  panel.dataset.deliveryState = delivery.state;

  const heading = document.createElement("h3");
  heading.textContent = `Prepare delivery · ${delivery.state}`;
  const repository = document.createElement("p");
  repository.className = "git-delivery-repository";
  repository.textContent =
    `${delivery.repository.repositoryRoot ?? "."} · `
    + `${delivery.repository.branch ?? "detached HEAD"} · `
    + `${shortHash(delivery.repository.head)} · `
    + `${delivery.repository.upstream ?? "no upstream"} · `
    + `ahead ${delivery.repository.ahead} / behind ${delivery.repository.behind}`;
  panel.append(heading, repository);

  if (delivery.repository.operationInProgress) {
    const operation = document.createElement("p");
    operation.className = "verification-warning";
    operation.textContent =
      `Git ${delivery.repository.operationInProgress} operation in progress. Delivery writes are blocked.`;
    panel.append(operation);
  }

  const sessionGroup = createDeliveryFileGroup(
    "Session changes",
    delivery.sessionChangedFiles,
    delivery,
    false
  );
  const preExistingGroup = createDeliveryFileGroup(
    "Pre-existing user changes",
    delivery.preExistingFiles,
    delivery,
    true
  );
  panel.append(sessionGroup, preExistingGroup);

  const editor = document.createElement("div");
  editor.className = "git-delivery-editor";
  editor.innerHTML = `
    <label>
      <span>Commit message</span>
      <textarea class="delivery-commit-message" maxlength="10000"></textarea>
    </label>
    <div class="git-delivery-tag-grid">
      <label>
        <span>Annotated tag (optional)</span>
        <input class="delivery-tag-name" type="text" maxlength="200">
      </label>
      <label>
        <span>Tag annotation</span>
        <input class="delivery-tag-annotation" type="text" maxlength="10000">
      </label>
    </div>
    <label class="delivery-validation-override">
      <input class="delivery-commit-override" type="checkbox">
      <span>Commit without current validation (explicit override)</span>
    </label>
  `;
  editor.querySelector(".delivery-commit-message").value =
    delivery.commitMessage ?? "";
  editor.querySelector(".delivery-tag-name").value = delivery.tag ?? "";
  editor.querySelector(".delivery-tag-annotation").value =
    delivery.tagAnnotation ?? "";
  editor.querySelector(".delivery-commit-override").checked =
    delivery.commitWithoutValidation;
  panel.append(editor);

  const validation = document.createElement("p");
  validation.className = delivery.validationBinding?.passed
    && !delivery.validationBinding?.stale
    ? "verification-ok delivery-validation"
    : "verification-warning delivery-validation";
  validation.textContent = delivery.validationBinding
    ? delivery.validationBinding.stale
      ? `Validation stale · ${delivery.validationBinding.diagnostic}`
      : delivery.validationBinding.passed
        ? `Validation bound to ${delivery.validationBinding.fileHashes
          ? Object.keys(delivery.validationBinding.fileHashes).length
          : 0} selected files.`
        : `Validation unavailable · ${delivery.validationBinding.diagnostic}`
    : "No passing validation is bound to this selection.";
  panel.append(validation);

  if (delivery.commitHash) {
    const facts = document.createElement("p");
    facts.className = "delivery-facts";
    facts.textContent =
      `Commit ${shortHash(delivery.commitHash)} · ${delivery.commitSubject} · `
      + `branch pushed: ${delivery.branchPushed ? "yes" : "no"} · `
      + `tag: ${delivery.tag ?? "none"} · tag pushed: ${delivery.tagPushed ? "yes" : "no"}`;
    panel.append(facts);
  }

  if (delivery.events?.length) {
    const activity = document.createElement("details");
    activity.className = "delivery-activity";
    const summary = document.createElement("summary");
    summary.textContent = `Delivery activity · ${delivery.events.length}`;
    activity.append(summary);
    for (const entry of delivery.events.slice(-12)) {
      const row = document.createElement("p");
      row.dataset.eventType = entry.type;
      row.textContent = `${entry.type} · ${entry.message}`;
      activity.append(row);
    }
    panel.append(activity);
  }

  const actions = document.createElement("div");
  actions.className = "git-delivery-actions";
  actions.append(
    createDeliveryButton("Save selection", "save-selection"),
    createDeliveryButton("Review unstaged diff", "diff"),
    createDeliveryButton("Stage selected", "stage"),
    createDeliveryButton("Unstage selected", "unstage"),
    createDeliveryButton("Create commit", "commit"),
    createDeliveryButton("Create annotated tag", "tag"),
    createDeliveryButton("Push current branch", "push-branch"),
    createDeliveryButton("Push exact tag", "push-tag")
  );
  panel.append(actions);

  const approvalHost = document.createElement("div");
  approvalHost.className = "git-delivery-approval-host";
  panel.append(approvalHost);
  panel.addEventListener("click", handleDeliveryPanelClick);
  elements.changeReviewBody.append(panel);
}

function createDeliveryFileGroup(title, paths, delivery, preExisting) {
  const group = document.createElement("fieldset");
  group.className = preExisting
    ? "git-delivery-files preexisting"
    : "git-delivery-files";
  const legend = document.createElement("legend");
  legend.textContent = title;
  group.append(legend);

  if (paths.length === 0) {
    const empty = document.createElement("p");
    empty.textContent = "None.";
    group.append(empty);
    return group;
  }

  for (const path of paths) {
    const label = document.createElement("label");
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.className = "delivery-file-selection";
    checkbox.value = path;
    checkbox.dataset.preExisting = preExisting ? "true" : "false";
    checkbox.checked = delivery.selectedFiles.includes(path);
    checkbox.disabled = delivery.repository.conflictedPaths.includes(path);
    const text = document.createElement("span");
    text.textContent = path;
    const status = document.createElement("small");
    status.textContent = [
      delivery.repository.stagedPaths.includes(path) ? "staged" : null,
      delivery.repository.unstagedPaths.includes(path) ? "unstaged" : null,
      delivery.repository.untrackedPaths.includes(path) ? "untracked" : null,
      delivery.repository.conflictedPaths.includes(path) ? "conflicted" : null
    ].filter(Boolean).join(", ");
    label.append(checkbox, text, status);
    group.append(label);
  }

  if (preExisting) {
    const warning = document.createElement("p");
    warning.className = "verification-warning";
    warning.textContent =
      "Pre-existing changes are not owned by this execution and require explicit inclusion.";
    group.append(warning);
  }
  return group;
}

function createDeliveryButton(label, operation) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "secondary-button";
  button.dataset.deliveryOperation = operation;
  button.textContent = label;
  return button;
}

async function handleDeliveryPanelClick(event) {
  const button = event.target.closest("[data-delivery-operation]");

  if (!button || !state.activeReview || !state.activeDelivery) {
    return;
  }

  const operation = button.dataset.deliveryOperation;
  if (operation === "save-selection") {
    await saveDeliverySelection();
    return;
  }
  if (operation === "diff") {
    await showDeliveryDiff();
    return;
  }
  if (state.approvalPolicy === "ask") {
    showDeliveryApproval(operation);
    return;
  }
  state.pendingDeliveryAction = prepareDeliveryAction(operation);
  if (state.pendingDeliveryAction) {
    await executePendingDeliveryAction(false);
  }
}

async function saveDeliverySelection() {
  const panel = elements.changeReviewBody.querySelector(".git-delivery-panel");
  const selected = [...panel.querySelectorAll(".delivery-file-selection:checked")]
    .map(input => input.value);
  const includePreExisting = [...panel.querySelectorAll(
    ".delivery-file-selection:checked"
  )].some(input => input.dataset.preExisting === "true");
  const commitMessage = panel.querySelector(".delivery-commit-message").value;
  const tag = panel.querySelector(".delivery-tag-name").value;
  const tagAnnotation = panel.querySelector(
    ".delivery-tag-annotation"
  ).value;
  const commitWithoutValidation = panel.querySelector(
    ".delivery-commit-override"
  ).checked;

  try {
    state.activeDelivery = await fetchJson(
      deliveryUrl("selection"),
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          selectedFiles: selected,
          includePreExistingChanges: includePreExisting,
          commitMessage,
          tag,
          tagAnnotation,
          commitWithoutValidation
        })
      }
    );
    state.pendingDeliveryAction = null;
    renderGitDelivery(state.activeDelivery);
    elements.undoStatus.textContent = "Delivery selection saved. No Git write occurred.";
  } catch (error) {
    elements.undoStatus.textContent = error.message;
  }
}

async function showDeliveryDiff() {
  const selected = state.activeDelivery.selectedFiles;

  if (selected.length === 0) {
    elements.undoStatus.textContent = "Select files before requesting a diff.";
    return;
  }

  try {
    const diff = await fetchJson(
      deliveryUrl("diff"),
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          paths: selected,
          staged: false
        })
      }
    );
    const panel = elements.changeReviewBody.querySelector(".git-delivery-panel");
    panel.querySelector(".delivery-diff-results")?.remove();
    const results = document.createElement("div");
    results.className = "delivery-diff-results";
    for (const file of diff.files) {
      const details = document.createElement("details");
      details.open = true;
      const summary = document.createElement("summary");
      summary.textContent =
        `${file.path}${file.binary ? " · binary" : ""}${file.truncated ? " · truncated" : ""}`;
      const content = document.createElement("pre");
      content.className = "change-diff";
      content.textContent = file.content || "[no unstaged diff]";
      details.append(summary, content);
      results.append(details);
    }
    panel.append(results);
  } catch (error) {
    elements.undoStatus.textContent = error.message;
  }
}

function prepareDeliveryAction(operation) {
  const delivery = state.activeDelivery;
  const panel = elements.changeReviewBody.querySelector(".git-delivery-panel");
  const actionId = {
    stage: delivery.stageActionId,
    unstage: delivery.unstageActionId,
    commit: delivery.commitActionId,
    tag: delivery.tagActionId,
    "push-branch": delivery.pushBranchActionId,
    "push-tag": delivery.pushTagActionId
  }[operation];

  if (!actionId) {
    return null;
  }

  return {
    operation,
    actionId,
    commitWithoutValidation: panel.querySelector(
      ".delivery-commit-override"
    ).checked,
    tag: panel.querySelector(".delivery-tag-name").value.trim(),
    annotation: panel.querySelector(
      ".delivery-tag-annotation"
    ).value.trim()
  };
}

function showDeliveryApproval(operation) {
  const panel = elements.changeReviewBody.querySelector(".git-delivery-panel");
  state.pendingDeliveryAction = prepareDeliveryAction(operation);
  if (!state.pendingDeliveryAction) {
    return;
  }
  const actionId = state.pendingDeliveryAction.actionId;
  const delivery = state.activeDelivery;
  const host = panel.querySelector(".git-delivery-approval-host");
  host.replaceChildren();
  const card = document.createElement("section");
  card.className = "delivery-approval";
  const heading = document.createElement("h4");
  heading.textContent = `Explicit approval required · ${operation}`;
  const facts = document.createElement("pre");
  facts.textContent = [
    `action: ${actionId}`,
    `repository: ${delivery.repository.repositoryRoot ?? "."}`,
    `branch: ${delivery.repository.branch ?? "detached"}`,
    `upstream: ${delivery.repository.upstream ?? "none"}`,
    `files: ${delivery.selectedFiles.join(", ") || "none"}`,
    `message: ${delivery.commitMessage || "none"}`,
    `tag: ${state.pendingDeliveryAction.tag || "none"}`,
    `validation: ${delivery.validationBinding?.stale
      ? "stale"
      : delivery.validationBinding?.passed
        ? "passed and bound"
        : "missing"}`,
    `override: ${state.pendingDeliveryAction.commitWithoutValidation
      ? "commit without validation"
      : "none"}`
  ].join("\n");
  const controls = document.createElement("div");
  controls.className = "git-delivery-actions";
  const approve = document.createElement("button");
  approve.type = "button";
  approve.className = "primary-button";
  approve.textContent = "Approve exact action";
  approve.addEventListener("click", approveDeliveryAction);
  const reject = document.createElement("button");
  reject.type = "button";
  reject.className = "secondary-button";
  reject.textContent = "Cancel";
  reject.addEventListener(
    "click",
    () => {
      state.pendingDeliveryAction = null;
      host.replaceChildren();
    }
  );
  controls.append(reject, approve);
  card.append(heading, facts, controls);
  host.append(card);
}

async function approveDeliveryAction() {
  await executePendingDeliveryAction(true);
}

async function executePendingDeliveryAction(confirmed) {
  const pending = state.pendingDeliveryAction;
  if (!pending) {
    return;
  }
  const endpoint = {
    stage: "stage",
    unstage: "unstage",
    commit: "commit",
    tag: "tag",
    "push-branch": "push-branch",
    "push-tag": "push-tag"
  }[pending.operation];
  let payload = {
    browserSessionId: state.browserSessionId,
    actionId: pending.actionId,
    confirmed
  };
  if (pending.operation === "commit") {
    payload.commitWithoutValidation = pending.commitWithoutValidation;
  }
  if (pending.operation === "tag") {
    payload.tag = pending.tag;
    payload.annotation = pending.annotation;
  }

  try {
    state.activeDelivery = await fetchJson(
      deliveryUrl(endpoint),
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify(payload)
      }
    );
    state.pendingDeliveryAction = null;
    renderGitDelivery(state.activeDelivery);
    const review = await fetchJson(
      `/api/execution-sessions/${encodeURIComponent(
        state.activeReview.summary.id
      )}/review`
    );
    state.activeReview = review;
    elements.undoExecution.disabled = !review.summary.undoAvailable;
    elements.undoExecution.title = review.summary.undoDiagnostic ?? "";
    elements.undoStatus.textContent =
      `Git ${pending.operation} completed and repository status refreshed.`;
    await refreshGit();
  } catch (error) {
    elements.undoStatus.textContent = error.message;
  }
}

function deliveryUrl(action = "") {
  const id = encodeURIComponent(
    state.activeReview.summary.id
  );
  return `/api/execution-sessions/${id}/delivery${action ? `/${action}` : ""}`;
}

function shortHash(hash) {
  return hash
    ? hash.slice(0, 8)
    : "unborn";
}

async function undoExecution() {
  const review = state.activeReview;

  if (!review?.summary.undoAvailable) {
    return;
  }

  if (!await showAppConfirm(
    "Fully undo this session's changes? The current state will be validated before any change.",
    { title: "Undo changes?", confirmLabel: "Undo", danger: true }
  )) {
    return;
  }

  elements.undoExecution.disabled = true;
  elements.undoStatus.textContent = "Validating and undoing…";

  try {
    const response = await fetchJson(
      `/api/execution-sessions/${encodeURIComponent(review.summary.id)}/undo`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          confirmed: true,
          browserSessionId: state.browserSessionId
        })
      }
    );
    await openChangeReview(
      review.summary.id
    );
    elements.undoStatus.textContent = response.message;
  } catch (error) {
    elements.undoStatus.textContent = error.message;

    if (error.payload) {
      const warning = document.createElement("p");
      warning.className = "verification-warning";
      warning.textContent = [
        error.payload.message,
        ...(error.payload.warnings ?? [])
      ].join(" ");
      elements.changeReviewBody.prepend(warning);
    }

    elements.undoExecution.disabled = false;
  }
}

async function validateChanges() {
  const review = state.activeReview;

  if (!review || review.files.length === 0) {
    return;
  }

  const confirmed = state.approvalPolicy !== "ask"
    || await showAppConfirm(
      "Run every structured step in the saved validation profile now?",
      { title: "Run validation?", confirmLabel: "Run" }
    );

  if (!confirmed) {
    return;
  }

  elements.validateChanges.disabled = true;
  elements.undoStatus.textContent = "Running validation…";

  try {
    const result = await fetchJson(
      `/api/execution-sessions/${encodeURIComponent(review.summary.id)}/validate`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          confirmed
        })
      }
    );
    await openChangeReview(
      review.summary.id
    );
    elements.undoStatus.textContent =
      `Validation ${result.state}.`;
  } catch (error) {
    elements.undoStatus.textContent = error.message;
    elements.validateChanges.disabled = false;
  }
}

function addApprovalActivity(assistant, streamEvent) {
  const action = streamEvent.localAction;
  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
  const row = document.createElement("details");
  row.className = "activity-row action-approval";
  row.open = true;
  row.dataset.eventType = streamEvent.type;
  row.dataset.actionId = action.actionId;
  row.dataset.executionSessionId = action.executionSessionId ?? "";
  const summary = document.createElement("summary");
  summary.className = "action-approval-summary";
  const time = document.createElement("span");
  time.className = "activity-time";
  time.textContent = formatElapsed(
    streamEvent.elapsedMilliseconds
  );
  const toggle = document.createElement("span");
  toggle.className = "action-approval-toggle";
  toggle.textContent = "›";
  toggle.setAttribute(
    "aria-hidden",
    "true"
  );
  const summaryContent = document.createElement("span");
  summaryContent.className = "action-approval-summary-content";
  const title = document.createElement("strong");
  title.textContent = action.summary;
  const status = document.createElement("span");
  status.className = "approval-status";
  status.textContent = "Waiting for decision";
  summaryContent.append(title, status);
  summary.append(time, toggle, summaryContent);
  const content = document.createElement("div");
  content.className = "action-approval-content";
  const message = document.createElement("span");
  message.className = "activity-message";
  message.textContent = streamEvent.message;
  content.append(message);
  const command = createTerminalCommand(action, title);

  if (command.host) {
    content.append(command.host);
  }

  const controls = document.createElement("div");
  controls.className = "approval-controls";
  const reject = document.createElement("button");
  reject.className = "secondary-button";
  reject.type = "button";
  reject.textContent = "Reject";
  const approve = document.createElement("button");
  approve.className = "primary-button";
  approve.type = "button";
  approve.textContent = "Approve";
  controls.append(reject, approve);
  content.append(controls);
  row.append(summary, content);
  assistant.workActivity.hidden = false;
  ensureWorkNarrative(
    assistant,
    "I need your decision to continue this change."
  );
  assistant.workActivity.append(row);

  if (command.input) {
    row.dataset.editableText = command.input.value;
    command.input.addEventListener(
      "input",
      () => {
        command.input.removeAttribute("aria-invalid");
        status.textContent = command.input.value
          === (row.dataset.editableText ?? "")
          ? "Waiting for decision"
          : "Change will be validated upon approval";
      }
    );
  }

  approve.addEventListener(
    "click",
    () => decideAction(
      action.actionId,
      action.executionSessionId,
      true,
      approve,
      reject,
      status,
      row,
      command.input
    )
  );
  reject.addEventListener(
    "click",
    () => decideAction(
      action.actionId,
      action.executionSessionId,
      false,
      approve,
      reject,
      status,
      row,
      command.input
    )
  );
}

function createTerminalCommand(action, title) {
  if (!action.preview && !action.editableText) {
    return { host: null, input: null };
  }

  const host = document.createElement("div");
  host.className = "terminal-command";
  const prompt = document.createElement("span");
  prompt.className = "terminal-prompt";
  prompt.textContent = "$";
  title.classList.add("terminal-tool-title");

  if (action.editable) {
    const input = document.createElement(
      action.tool === "run_process" ? "input" : "textarea"
    );
    input.className = "terminal-command-input";

    if (input instanceof HTMLInputElement) {
      input.type = "text";
    } else {
      input.rows = Math.min(
        4,
        Math.max(1, (action.editableText ?? action.preview).split("\n").length)
      );
    }

    input.value = action.editableText ?? action.preview;
    input.autocomplete = "off";
    input.spellcheck = false;
    input.setAttribute(
      "aria-label",
      `Edit ${action.tool} command`
    );
    host.append(prompt);

    if (action.tool !== "run_process") {
      const tool = document.createElement("code");
      tool.className = "terminal-structured-tool";
      tool.textContent = action.tool;
      host.append(tool);
    }

    host.append(input);
    return { host, input };
  }

  const value = document.createElement("code");
  value.className = "terminal-command-value";
  const match = action.preview.match(/^(\S+)([\s\S]*)$/);
  const executablePart = document.createElement("span");
  executablePart.className = "terminal-executable";
  executablePart.textContent = match?.[1] ?? action.preview;
  const argumentsPart = document.createElement("span");
  argumentsPart.className = "terminal-arguments";
  argumentsPart.textContent = match?.[2] ?? "";
  value.append(executablePart, argumentsPart);
  host.append(prompt, value);
  return { host, input: null };
}

function updateApprovalActivity(assistant, streamEvent) {
  const action = streamEvent.localAction;
  const approval = assistant.container.querySelector(
    `.action-approval[data-action-id="${CSS.escape(action.actionId)}"]`
  );

  if (!approval) {
    return false;
  }

  approval.dataset.eventType = streamEvent.type;

  const status = approval.querySelector(".approval-status");
  const title = approval.querySelector(".action-approval-summary-content strong");
  const input = approval.querySelector(".terminal-command-input");
  const controls = approval.querySelector(".approval-controls");

  if (title && action.summary) {
    title.textContent = action.summary;
  }

  if (input && action.editableText) {
    input.value = action.editableText;
    input.readOnly = action.state !== "awaiting-approval"
      && action.state !== "revised";
    approval.dataset.editableText = action.editableText;
  }

  if (action.state === "revised") {
    status.textContent = "Change validated";
  } else if (action.state === "approved") {
    status.textContent = "Approved";
  } else if (action.state === "executing") {
    status.textContent = "Executing…";
  } else if (action.state === "completed") {
    status.textContent = "Completed";
    approval.dataset.decision = "completed";
    renderApprovalResponse(approval, action, false);
  } else if (action.state === "failed") {
    status.textContent = "Failed";
    approval.dataset.decision = "failed";
    renderApprovalResponse(approval, action, true);
  } else if (action.state === "rejected") {
    status.textContent = "Rejected";
    approval.dataset.decision = "rejected";
  }

  if (
    action.state === "approved"
    || action.state === "executing"
    || action.state === "completed"
    || action.state === "failed"
    || action.state === "rejected"
  ) {
    controls?.remove();

    if (input) {
      input.readOnly = true;
    }
  }

  if (action.state === "rejected") {
    approval.open = false;
  } else if (action.state === "completed" || action.state === "failed") {
    approval.open = true;
  }

  return true;
}

function renderApprovalResponse(approval, action, failed) {
  let response = approval.querySelector(".action-response");

  if (!response) {
    response = document.createElement("details");
    response.className = "action-response";
    const summary = document.createElement("summary");
    const output = document.createElement("pre");
    output.className = "action-response-output";
    response.append(summary, output);
    approval.querySelector(".action-approval-content")?.append(response);
  }

  response.dataset.state = failed ? "failed" : "completed";
  response.open = false;
  response.querySelector("summary").textContent = failed
    ? "Execution · failed"
    : "Execution · completed";
  response.querySelector(".action-response-output").textContent =
    action.resultOutput || "Completed without textual output.";
}

async function decideAction(
  actionId,
  executionSessionId,
  approved,
  approveButton,
  rejectButton,
  status,
  approval,
  input
) {
  approveButton.disabled = true;
  rejectButton.disabled = true;
  if (input) {
    input.disabled = true;
  }
  status.textContent = approved ? "Approving…" : "Rejecting…";

  try {
    await fetchJson(
      `/api/actions/${encodeURIComponent(actionId)}/decision`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          approved,
          browserSessionId: state.browserSessionId,
          executionSessionId,
          editedText: approved
            && input
            && input.value !== (approval.dataset.editableText ?? "")
            ? input.value
            : null
        })
      }
    );
    if (
      approval.dataset.decision === "completed"
      || approval.dataset.decision === "failed"
      || approval.dataset.decision === "rejected"
    ) {
      return;
    }

    status.textContent = approved ? "Approved" : "Rejected";
    approval.dataset.decision = approved
      ? "approved"
      : "rejected";
    approval.querySelector(".approval-controls")?.remove();
    if (input) {
      approval.dataset.editableText = input.value;
      input.readOnly = true;
      input.disabled = false;
    }

    approval.open = approved;
  } catch (error) {
    status.textContent = approved && input
      ? "Invalid change"
      : error.message;
    if (input) {
      input.disabled = false;
      input.setAttribute("aria-invalid", "true");
    }
    showToast(error.message);
    approveButton.disabled = false;
    rejectButton.disabled = false;
  }
}

function addRecoveryDecisionActivity(assistant, streamEvent) {
  const recovery = streamEvent.recoveryDecision;
  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
  const row = document.createElement("details");
  row.className = "activity-row action-approval recovery-decision";
  row.open = true;
  row.dataset.eventType = streamEvent.type;
  row.dataset.checkpointId = recovery.checkpointId;
  row.dataset.executionSessionId = recovery.executionSessionId;
  const summary = document.createElement("summary");
  summary.className = "action-approval-summary";
  const time = document.createElement("span");
  time.className = "activity-time";
  time.textContent = formatElapsed(
    streamEvent.elapsedMilliseconds
  );
  const toggle = document.createElement("span");
  toggle.className = "action-approval-toggle";
  toggle.textContent = "›";
  toggle.setAttribute(
    "aria-hidden",
    "true"
  );
  const summaryContent = document.createElement("span");
  summaryContent.className = "action-approval-summary-content";
  const title = document.createElement("strong");
  title.textContent = "Automatic recovery exhausted";
  const status = document.createElement("span");
  status.className = "approval-status";
  status.textContent = "Choose an alternative";
  summaryContent.append(title, status);
  summary.append(time, toggle, summaryContent);
  const content = document.createElement("div");
  content.className = "action-approval-content";
  const message = document.createElement("span");
  message.className = "activity-message";
  message.textContent = streamEvent.message;
  const reason = document.createElement("pre");
  reason.className = "action-preview recovery-reason";
  reason.textContent = recovery.reason;
  const controls = document.createElement("div");
  controls.className = "approval-controls recovery-controls";
  const optionRows = [];
  const buttons = recovery.options.map(
    (option, index) => {
      const optionRow = document.createElement("div");
      optionRow.className = "recovery-option";
      const button = document.createElement("button");
      button.type = "button";
      button.className = option.id === "retry"
        ? "primary-button"
        : "secondary-button";
      button.dataset.recoveryOption = option.id;
      button.title = option.description;
      button.textContent =
        `${String.fromCharCode(65 + index)} · ${option.label}`;
      const description = document.createElement("small");
      description.textContent = option.description;
      optionRow.append(button, description);
      optionRows.push(
        optionRow
      );
      button.addEventListener(
        "click",
        () => decideRecovery(
          recovery,
          option,
          buttons,
          status,
          row
        )
      );
      return button;
    }
  );
  controls.append(...optionRows);
  content.append(message, reason, controls);
  row.append(summary, content);
  assistant.workActivity.hidden = false;
  ensureWorkNarrative(
    assistant,
    "Automatic recovery has ended; choose how the task should continue."
  );
  assistant.workActivity.append(row);
}

async function decideRecovery(
  recovery,
  option,
  buttons,
  status,
  checkpoint
) {
  buttons.forEach(
    button => {
      button.disabled = true;
    }
  );
  status.textContent = "Applying decision…";

  try {
    await fetchJson(
      `/api/recovery/${encodeURIComponent(recovery.checkpointId)}/decision`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          option: option.id,
          browserSessionId: state.browserSessionId,
          executionSessionId: recovery.executionSessionId
        })
      }
    );
    status.textContent = option.label;
    checkpoint.dataset.decision = option.id;
    checkpoint.open = false;
  } catch (error) {
    status.textContent = error.message;
    buttons.forEach(
      button => {
        button.disabled = false;
      }
    );
  }
}

function finishActivity(assistant, summary, keepOpen) {
  cancelAnimationFrame(assistant.clockFrame);
  assistant.progress.hidden = true;
  assistant.summary.textContent = summary;
  assistant.details.dataset.terminal = "true";
  assistant.details.open = keepOpen;
}

function startMessageEdit(element, message, historyIndex) {
  if (state.requestController) {
    return;
  }

  state.editingTurn = {
    element,
    historyIndex
  };
  elements.composer.classList.add("editing");
  elements.messageInput.value = message;
  resizeComposer();
  setStreamingState(false);
  elements.messageInput.focus();
  elements.messageInput.setSelectionRange(
    message.length,
    message.length
  );
}

function cancelMessageEdit() {
  state.editingTurn = null;
  elements.composer.classList.remove("editing");
  elements.messageInput.value = "";
  resizeComposer();
  setStreamingState(false);
  elements.messageInput.focus();
}

function removeConversationFrom(element) {
  let current = element;

  while (current) {
    const next = current.nextElementSibling;
    resizeObserver.unobserve(current);
    current.remove();
    current = next;
  }
}

function createMessageActionButton(text, accessibleName) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "message-action-button";
  button.textContent = text;
  button.setAttribute(
    "aria-label",
    accessibleName
  );
  button.dataset.originalLabel = accessibleName;
  return button;
}

function renderAssistantResponse(
  assistant,
  response,
  renderedHtml,
  markdown,
  aggregateMarkdown
) {
  assistant.rawAnswer = aggregateMarkdown;
  assistant.copyButton.disabled = !aggregateMarkdown;
  response.body.innerHTML = renderedHtml;
  secureRenderedLinks(response.body);
  enhanceCodeBlocks(
    response.body,
    markdown
  );
}

function enhanceCodeBlocks(container, markdown) {
  const fencedLanguages = extractFencedLanguages(markdown);
  const blocks = Array.from(
    container.querySelectorAll("pre")
  );

  blocks.forEach(
    (pre, index) => {
      const parent = pre.parentElement;
      const parentLanguage = parent?.tagName === "DIV"
        ? Array.from(parent.classList).find(name => name !== "code-block")
        : null;
      const codeLanguage = pre.querySelector("code")?.className
        .split(/\s+/)
        .find(name => name.startsWith("language-"))
        ?.slice("language-".length);
      const language = parentLanguage
        ?? codeLanguage
        ?? fencedLanguages[index]
        ?? "code";
      const block = parentLanguage
        ? parent
        : document.createElement("div");

      if (!parentLanguage) {
        pre.replaceWith(block);
        block.append(pre);
      }

      block.classList.add("code-block");
      const header = document.createElement("div");
      header.className = "code-block-header";
      const label = document.createElement("span");
      label.className = "code-language";
      label.textContent = formatLanguageName(language);
      const copyButton = document.createElement("button");
      copyButton.type = "button";
      copyButton.className = "code-copy-button";
      copyButton.setAttribute(
        "aria-label",
        `Copy code ${label.textContent}`
      );
      copyButton.dataset.originalLabel = copyButton.getAttribute("aria-label");
      const icon = document.createElement("span");
      icon.className = "copy-icon";
      icon.setAttribute(
        "aria-hidden",
        "true"
      );
      copyButton.append(icon);
      copyButton.addEventListener(
        "click",
        () => copyText(
          pre.textContent.replace(/\n$/, ""),
          copyButton,
          "Code copied"
        )
      );
      header.append(label, copyButton);
      block.prepend(header);
    }
  );
}

function extractFencedLanguages(markdown) {
  return Array.from(
    markdown.matchAll(
      /^```([^\s`]*)/gm
    ),
    match => match[1] || "code"
  );
}

function formatLanguageName(language) {
  const normalized = language.toLowerCase();
  const names = {
    csharp: "C#",
    cs: "C#",
    css: "CSS",
    html: "HTML",
    javascript: "JavaScript",
    js: "JavaScript",
    json: "JSON",
    markdown: "Markdown",
    md: "Markdown",
    powershell: "PowerShell",
    ps1: "PowerShell",
    typescript: "TypeScript",
    ts: "TypeScript",
    xml: "XML"
  };
  return names[normalized]
    ?? language.toUpperCase();
}

async function copyText(text, button, successLabel) {
  if (!text) {
    return;
  }

  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
    } else {
      fallbackCopyText(text);
    }

    showCopySuccess(
      button,
      successLabel
    );
  } catch {
    fallbackCopyText(text);
    showCopySuccess(
      button,
      successLabel
    );
  }
}

function fallbackCopyText(text) {
  const temporary = document.createElement("textarea");
  temporary.value = text;
  temporary.style.position = "fixed";
  temporary.style.opacity = "0";
  document.body.append(temporary);
  temporary.select();
  document.execCommand("copy");
  temporary.remove();
}

function showCopySuccess(button, label) {
  const originalLabel = button.dataset.originalLabel;
  button.classList.add("copied");
  button.setAttribute(
    "aria-label",
    label
  );
  clearTimeout(
    Number(button.dataset.resetTimer)
  );
  button.dataset.resetTimer = String(
    setTimeout(
      () => {
        button.classList.remove("copied");
        button.setAttribute(
          "aria-label",
          originalLabel
        );
      },
      1600
    )
  );
}

function secureRenderedLinks(container) {
  for (const link of container.querySelectorAll("a")) {
    link.rel = "noopener noreferrer";
    link.target = "_blank";
  }
}

function handleConversationScroll() {
  state.autoFollow = isNearBottom();
  updateJumpControl();
}

function resumeAutoFollow() {
  state.autoFollow = true;
  updateJumpControl();
  scrollToBottom();
}

function updateJumpControl() {
  elements.jumpLatest.hidden = state.autoFollow;
}

function scrollToBottom() {
  if (elements.messages.querySelector("#empty-state")) {
    elements.messages.scrollTo({
      top: 0,
      behavior: "instant"
    });
    return;
  }
  elements.messages.scrollTo(
    {
      top: elements.messages.scrollHeight,
      behavior: "instant"
    }
  );
}

function setStreamingState(isStreaming) {
  if (!isStreaming) {
    state.activeAgentModel = null;
    state.activeAgentRole = null;
  }

  elements.sendButtonLabel.textContent = "Send";
  elements.sendButton.querySelector(".send-icon").textContent = "\u2191";
  elements.sendButton.setAttribute(
    "aria-label",
    state.editingTurn && !isStreaming
      ? "Send edited message"
      : "Send message"
  );
  elements.sendButton.title = elements.sendButton.getAttribute("aria-label");
  elements.sendButton.classList.remove("cancel");
  elements.cancelRequest.hidden = !isStreaming;
  elements.composer.classList.toggle("streaming", isStreaming);
  elements.attachImage.disabled = isStreaming;
  elements.imageInput.disabled = isStreaming;
  elements.compactContext.disabled = isStreaming;
  elements.cancelMessageEdit.hidden = isStreaming || !state.editingTurn;
  elements.messages.querySelectorAll(".edit-message").forEach(
    button => {
      button.disabled = isStreaming;
    }
  );
  updateHarnessControls();
  updateStreamingComposerActions();
  updateComposerStatus();
  renderWebControl();
}

function updateStreamingComposerActions() {
  elements.cancelRequest.hidden = !state.requestController;
  renderMessageQueue();
}

function renderPendingContextUsage() {
  if (!state.settings || state.requestController) {
    return;
  }
  state.contextUsage = null;
  renderContextUsage();
}

function renderContextUsage() {
  const usage = state.contextUsage;

  if (!usage) {
    elements.contextUsageSummaryText.textContent =
      "Context will be calculated when sending";
    elements.contextUsage.dataset.accuracy = "pending";
    elements.contextUsage.dataset.warning = "";
    elements.contextUsageEstimateWarning.hidden = true;
    elements.contextUsageEstimateWarning.removeAttribute("title");
    elements.contextUsageEstimateWarning.removeAttribute("aria-label");
    elements.contextUsageWarning.hidden = true;
    elements.contextUsageDetails.replaceChildren();
    elements.compactContext.hidden = true;
    return;
  }

  const effectiveLimit = usage.effectiveLimitTokens
    || Math.min(
      usage.applicationLimit,
      usage.providerMaximumTokens ?? usage.configuredProviderLimit
    );
  const activeContextTokens = usage.activeContextTokens || usage.inputTokens;
  const liveContext = usage.activeContextTokens > 0;
  elements.contextUsageSummaryText.textContent =
    `Context ${formatCompactTokens(activeContextTokens)} / `
    + `${formatCompactTokens(effectiveLimit)} · `
    + `${liveContext ? `${window.AgenticRouterI18n.t("context.live")} ` : ""}`
    + `${usage.accuracy === "exact" ? "exact" : "estimated"}`;
  const estimatedLiveUsage = liveContext && usage.accuracy !== "exact";
  const estimateWarning = t(
    "context.live_estimate_warning",
    { harness: activeContextHarnessLabel() }
  );
  elements.contextUsageEstimateWarning.hidden = !estimatedLiveUsage;
  elements.contextUsageEstimateWarning.title = estimatedLiveUsage
    ? estimateWarning
    : "";
  elements.contextUsageEstimateWarning.setAttribute(
    "aria-label",
    estimatedLiveUsage ? estimateWarning : ""
  );
  elements.contextUsage.dataset.accuracy = usage.accuracy;
  elements.contextUsage.dataset.warning =
    usage.warningThreshold ? String(usage.warningThreshold) : "";
  elements.contextUsageWarning.hidden =
    !usage.warningThreshold && !usage.trimmed;
  elements.contextUsageWarning.textContent = [
    usage.warningThreshold
      ? `Warning: context is above ${usage.warningThreshold}% of usable capacity.`
      : null,
    usage.trimmed
      ? "Eligible blocks were omitted only from the submitted payload."
      : null
  ].filter(Boolean).join(" ");
  elements.contextUsageDetails.replaceChildren(
    contextDetail("Specialist inference", usage.inferenceSequence || 1),
    contextDetail("Visible messages", usage.visibleMessages),
    contextDetail("Included messages", usage.includedMessages),
    contextDetail("Omitted messages", usage.omittedMessages),
    contextDetail(
      "Current conversation and message",
      `${formatInteger(usage.conversationTokens)} estimated tokens`
    ),
    contextDetail(
      "System and instructions",
      `${formatInteger(usage.systemInstructionTokens)} estimated tokens`
    ),
    contextDetail(
      "Project context",
      `${formatInteger(usage.projectContextTokens)} estimated tokens`
    ),
    contextDetail(
      "Toolset discovery",
      `${formatInteger(usage.toolDiscoveryTokens)} estimated tokens`
    ),
    contextDetail(
      "Granted schemas",
      `${formatInteger(usage.grantedToolSchemaTokens)} estimated tokens`
    ),
    contextDetail(
      "Host state/results",
      `${formatInteger(usage.hostStateTokens)} estimated tokens`
    ),
    contextDetail(
      "Structural overhead",
      `${formatInteger(usage.structuralOverheadTokens)} estimated tokens`
    ),
    contextDetail(
      "Total input",
      `${formatInteger(usage.inputTokens)} tokens · ${usage.accuracy === "exact" ? "reported" : "estimated"}`
    ),
    ...(liveContext
      ? [
        contextDetail(
          window.AgenticRouterI18n.t("context.generated_output"),
          `${formatInteger(usage.outputTokens)} tokens`
        ),
        contextDetail(
          window.AgenticRouterI18n.t("context.active"),
          `${formatInteger(activeContextTokens)} tokens`
        )
      ]
      : []),
    contextDetail(
      "Output reserve",
      `${formatInteger(usage.reservedResponseTokens)} tokens`
    ),
    contextDetail(
      "Required context",
      `${formatInteger(usage.requiredContextTokens)} tokens`
    ),
    contextDetail(
      "Effective limit",
      `${formatInteger(effectiveLimit)} tokens`
    ),
    contextDetail(
      "Count source",
      usage.accuracy === "exact"
        ? "provider-reported usage"
        : usage.estimator
    ),
    contextDetail(
      "Provider maximum",
      usage.providerMaximumTokens == null
        ? "not reported"
        : `${formatInteger(usage.providerMaximumTokens)} tokens`
    ),
    contextDetail(
      "Configured provider limit",
      `${formatInteger(usage.configuredProviderLimit)} tokens`
    ),
    contextDetail(
      "Application limit",
      `${formatInteger(usage.applicationLimit)} tokens`
    ),
    contextDetail("Omitted blocks", usage.omittedBlocks || 0)
  );
  elements.compactContext.hidden = !usage.compactionEligible;
  elements.compactContext.disabled = Boolean(state.requestController);
  elements.compactContext.textContent = state.compactContextNextRequest
    ? "Compaction prepared"
    : "Compact context";
}

function activeContextHarnessLabel() {
  const harnessId = state.activeHarness ?? state.harness;
  const status = state.harnesses.find(
    item => item.definition.id === harnessId
  );
  return status
    ? harnessDisplayLabel(status.definition)
    : harnessId === "auto-model-harness"
      ? "the selected harness"
      : harnessId || "the selected provider";
}

async function requestManualContextCompaction() {
  const usage = state.contextUsage;
  if (!usage?.compactionEligible || state.requestController) {
    return;
  }
  const before = usage.beforeCompactionTokens ?? usage.inputTokens;
  const after = usage.afterCompactionTokens ?? usage.inputTokens;
  const confirmed = await showAppConfirm(
    "Compaction will not delete saved messages or change the visible chat. "
      + "It will omit only eligible blocks from subsequent inference payloads.\n\n"
      + `Current estimate: ${formatInteger(before)} tokens\n`
      + `Compacted estimate: ${formatInteger(after)} tokens\n`
      + `Eligible/omitted blocks: ${usage.omittedBlocks || 0}`,
    {
      title: "Compact submitted context?",
      confirmLabel: "Compact next request"
    }
  );
  if (!confirmed) {
    return;
  }
  state.compactContextNextRequest = true;
  renderContextUsage();
}

function contextDetail(label, value) {
  const fragment = document.createDocumentFragment();
  const term = document.createElement("dt");
  const detail = document.createElement("dd");
  term.textContent = label;
  detail.textContent = value;
  fragment.append(term, detail);
  return fragment;
}

function formatCompactTokens(value) {
  const numeric = Number(value) || 0;

  if (numeric < 1000) {
    return formatInteger(numeric);
  }

  return `${(numeric / 1000).toLocaleString(
    window.AgenticRouterI18n.locale,
    {
      minimumFractionDigits: 1,
      maximumFractionDigits: 1
    }
  )}k`;
}

function updateComposerStatus() {
  if (state.requestController) {
    elements.composerStatus.textContent = state.steeringMessage
      ? t("steer.sending")
      : state.queueEditingId
        ? `${t("buffer.editing")} · response in progress`
        : state.messageQueue.length > 0
          ? `Response in progress · ${t("buffer.count", { count: state.messageQueue.length })}`
          : "Response in progress";
  } else if (state.conversationTransitioning) {
    elements.composerStatus.textContent = "Switching conversation safely";
  } else if (state.editingTurn) {
    elements.composerStatus.textContent = "Editing message · Esc to cancel";
  } else if (state.interactionMode === "execute") {
    if (state.harness === "auto-model-harness") {
      elements.composerStatus.textContent =
        `Execute · Auto Model × Harness · ${state.approvalPolicy === "ask" ? "ask for approval" : "automatic approval"}`;
      updateActiveAgentLabel();
      renderCapabilityContext();
      return;
    }
    const status = state.harnesses.find(
      item => item.definition.id === state.harness
    );
    const harness = status
      ? harnessDisplayLabel(status.definition)
      : state.harness;
    elements.composerStatus.textContent =
      `Execute · ${harness} · ${state.approvalPolicy === "ask" ? "ask for approval" : "automatic approval"}`;
  } else if (state.attachments.length > 0 || state.webEnabled) {
    elements.composerStatus.textContent = [
      state.attachments.length > 0
        ? `${state.attachments.length} image${state.attachments.length === 1 ? "" : "s"}`
        : null,
      state.webEnabled ? "Web enabled" : null,
      "Press Enter to send"
    ].filter(Boolean).join(" · ");
  } else {
    elements.composerStatus.textContent = "Press Enter to send";
  }

  updateActiveAgentLabel();
  renderCapabilityContext();
}

function updateActiveAgentLabel() {
  if (!elements.activeAgentLabel) {
    return;
  }

  const selectedModel = state.activeAgentModel
    ?? elements.modelSelector.value;
  elements.activeAgentLabel.textContent =
    selectedModel && selectedModel !== "auto"
      ? selectedModel
      : "Auto (Router)";
  elements.activeAgentLabel.title = elements.activeAgentLabel.textContent;
  renderCloudUsage();
}

function updateComposerModelTitle() {
  elements.modelSelector.title =
    elements.modelSelector.selectedOptions[0]?.textContent?.trim()
    ?? elements.modelSelector.value;
}

function elapsedSince(assistant) {
  return Math.round(performance.now() - assistant.startedAt);
}

function formatElapsed(milliseconds) {
  const elapsed = Math.max(
    0,
    Math.round(milliseconds ?? 0)
  );

  if (elapsed < 1_000) {
    return `${elapsed} ms`;
  }

  if (elapsed < 60_000) {
    const seconds = elapsed / 1_000;
    return `${seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)} s`;
  }

  const totalSeconds = Math.round(
    elapsed / 1_000
  );
  const minutes = Math.floor(
    totalSeconds / 60
  );
  const seconds = totalSeconds % 60;
  return `${minutes} min ${seconds} s`;
}

function isNearBottom() {
  return elements.messages.scrollHeight
    - elements.messages.scrollTop
    - elements.messages.clientHeight <= 120;
}

function formatGiB(bytes) {
  return bytes == null
    ? "n/d"
    : `${(bytes / 1073741824).toFixed(1)} GB`;
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KiB`;
  }

  return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
}

function formatPercent(value) {
  return value == null
    ? "n/d"
    : `${Math.round(value)}%`;
}

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  const payload = response.status === 204
    ? null
    : await response.json();

  if (!response.ok) {
    const error = new Error(
      payload?.message
      ?? payload?.detail
      ?? `HTTP ${response.status}`
    );
    error.payload = payload;
    throw error;
  }

  return payload;
}

async function fetchText(url, options) {
  const response = await fetch(url, options);
  const payload = await response.text();

  if (!response.ok) {
    let message = `HTTP ${response.status}`;

    try {
      message = JSON.parse(payload)?.message ?? message;
    } catch {
      if (payload.trim()) {
        message = payload.trim();
      }
    }

    throw new Error(message);
  }

  return payload;
}

function createSessionId() {
  return globalThis.crypto?.randomUUID?.()
    ?? `browser-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function toCamelCase(value) {
  return value.replace(
    /-([a-z])/g,
    (_, letter) => letter.toUpperCase()
  );
}
