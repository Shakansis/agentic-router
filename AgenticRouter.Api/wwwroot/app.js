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
  lockedModel: null,
  conversationVersion: 0,
  modelDiagnostics: null,
  interactionMode: "chat",
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
  workspaceSaving: false,
  activeReview: null,
  activeDelivery: null,
  pendingDeliveryAction: null,
  activeAgentModel: null,
  activeAgentRole: null,
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
  cloudImageApprovals: new Set(),
  sessionSearchController: null,
  detailsSession: null,
  summarySession: null,
  summaryEstimate: null,
  contextUsage: null,
  recovery: null,
  inspectedBackup: null,
  inspectedBackupBase64: null,
  runtimeProfiles: null,
  openCloudProviders: new Set(),
  gitConfigurationEditing: false
};

const elements = {};
let resizeObserver;

document.addEventListener("DOMContentLoaded", initialize);

async function initialize() {
  bindElements();
  bindEvents();
  initializeSidebarResize();
  initializeScrollFollowing();

  try {
    state.recovery = await fetchJson("/api/recovery/status");
    renderRecoveryState();
    await loadApplicationState();
  } catch (error) {
    elements.providerBadge.textContent = "Erro";
    elements.providerBadge.className = "badge error";
    elements.providerDetail.textContent = error.message;
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
    "model-lock",
    "send-button",
    "send-button-label",
    "cancel-message-edit",
    "active-agent-label",
    "active-provider-model",
    "capability-tags",
    "fallback-indicator",
    "context-usage",
    "context-usage-summary",
    "context-usage-warning",
    "context-usage-details",
    "compact-context",
    "web-toggle",
    "web-toggle-label",
    "attach-image",
    "image-input",
    "attachment-previews",
    "composer-status",
    "provider-badge",
    "provider-detail",
    "model-count",
    "device-count",
    "device-diagnostic",
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
    "action-model",
    "coordinator-model",
    "default-model",
    "default-gpu",
    "jump-latest",
    "runtime-summary",
    "runtime-memory-list",
    "runtime-model-list",
    "resident-model-status",
    "runtime-usage-summary",
    "runtime-usage-accuracy",
    "runtime-usage-details",
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
    "workspace-badge",
    "workspace-path",
    "git-card",
    "git-badge",
    "git-summary",
    "git-upstream-summary",
    "session-history",
    "conversation-persistence",
    "conversation-persistence-sidebar",
    "enable-session-history",
    "open-session-search",
    "pinned-session-section",
    "pinned-sessions",
    "recent-sessions",
    "archived-session-section",
    "archived-sessions",
    "history-new-conversation",
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
    "app-modal-field",
    "app-modal-label",
    "app-modal-input",
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
  elements.composer.addEventListener("click", handleComposerClick);
  elements.cancelMessageEdit.addEventListener("click", cancelMessageEdit);
  elements.messageInput.addEventListener("keydown", handleComposerKeyDown);
  elements.messageInput.addEventListener("input", resizeComposer);
  elements.messageInput.addEventListener("input", renderPendingContextUsage);
  elements.compactContext.addEventListener("click", requestManualContextCompaction);
  elements.settingsForm.addEventListener("submit", saveSettings);
  elements.messages.addEventListener("scroll", handleConversationScroll);
  elements.jumpLatest.addEventListener("click", resumeAutoFollow);
  elements.newConversation.addEventListener("click", requestNewConversation);
  elements.historyNewConversation.addEventListener("click", requestNewConversation);
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
  elements.modelLock.addEventListener("change", handleModelLockChange);
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
  elements.openSessionSearch.addEventListener("click", openSessionSearch);
  elements.sessionSearchForm.addEventListener("submit", runSessionSearch);
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
      "Trace ID copiado"
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
    title = "Confirmar ação",
    message = "",
    confirmLabel = "Confirmar",
    cancelLabel = "Cancelar",
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
  elements.appModalField.hidden = !inputLabel;
  elements.appModalLabel.textContent = inputLabel;
  elements.appModalInput.type = inputType;
  elements.appModalInput.value = inputValue;
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
      document.body.append(elements.appModal);
      previousFocus?.focus?.();
      resolve(value);
    };
    const submit = event => {
      event.preventDefault();
      finish(inputLabel ? elements.appModalInput.value : true);
    };
    const cancel = () => finish(inputLabel ? null : false);
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
    (inputLabel ? elements.appModalInput : elements.appModalConfirm).focus();
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
    inputLabel: options.inputLabel ?? "Valor"
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
  close.setAttribute("aria-label", "Fechar notificação");
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
    modelsResponse,
    devicesResponse,
    workspace,
    projectProfile,
    validationProfiles,
    workspaceProfiles,
    usageOverview,
    pricingCatalog,
    cloudProviders,
    cloudUsageDashboard,
    webSearch,
    providerHealth,
    modelOrganization,
    runtimeProfiles
  ] = await Promise.all([
    fetchJson("/api/settings"),
    fetchJson("/api/models"),
    fetchJson("/api/devices"),
    fetchJson("/api/workspace"),
    fetchJson("/api/workspace/project-profile"),
    fetchJson("/api/workspace/validation-profile"),
    fetchJson("/api/workspaces"),
    fetchJson("/api/usage/overview"),
    fetchJson("/api/usage/pricing"),
    fetchJson("/api/cloud-providers"),
    fetchJson("/api/usage/cloud-dashboard"),
    fetchJson("/api/web-search"),
    fetchJson("/api/provider-health"),
    fetchJson("/api/model-organization"),
    fetchJson("/api/runtime/profiles")
  ]);

  state.settings = settings;
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
  updateProviderStatus(modelsResponse);
  updateDeviceStatus(devicesResponse);
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
  if (!state.recovery?.historyAutoLoadDisabled) {
    await refreshSessions();
  }
  await refreshGit();
}

function renderRecoveryState() {
  const recovery = state.recovery;
  elements.safeModeBanner.hidden = !recovery?.safeMode;
  document.body.dataset.historyAutoload =
    recovery?.historyAutoLoadDisabled ? "disabled" : "enabled";
  elements.safeModeReason.textContent = recovery?.reason
    ?? "Execute, cloud e alteraÃ§Ãµes de configuraÃ§Ã£o estÃ£o desativados.";

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
  elements.providerBadge.textContent = response.available ? "Online" : "Indisponível";
  elements.providerBadge.className = `badge ${response.available ? "success" : "error"}`;
  elements.providerDetail.textContent = response.available
    ? "HTTP conectado"
    : response.error?.message ?? "Falha de conexão";
  elements.modelCount.textContent =
    `${response.models.length} instalado${response.models.length === 1 ? "" : "s"}`;
}

function updateDeviceStatus(response) {
  const physicalDevices = response.devices.filter(device => !device.isAuto);
  elements.deviceCount.textContent =
    `${physicalDevices.length} detectado${physicalDevices.length === 1 ? "" : "s"}`;
  elements.deviceDiagnostic.textContent = response.diagnostic ?? "";
}

function renderWorkspace() {
  const active = activeWorkspaceProfile();
  const workspace = state.workspace;
  const valid = Boolean(workspace?.valid);
  elements.workspaceBadge.textContent = active?.available
    ? "Ativo"
    : active
      ? "Indisponível"
      : "Não configurado";
  elements.workspaceBadge.className =
    `badge ${active?.available ? "success" : active ? "error" : "muted"}`;
  elements.workspacePath.textContent = active
    ? `${active.name} · ${shortenPath(active.path)}`
    : "Nenhuma pasta selecionada";
  elements.workspaceValidation.textContent = workspace?.diagnostic
    ?? workspace?.status
    ?? "Não configurado";
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
}

async function openGitPanel() {
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
        ? new Date(git.latestCommit.authoredAt).toLocaleString()
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
      "A criação do repositório exige o modo Execute. O painel Git será fechado e nenhuma alteração será feita até você reabri-lo e confirmar a inicialização.",
      {
        title: "Mudar para o modo Execute?",
        confirmLabel: "Fechar e mudar para Execute"
      }
    );
    if (switchMode) {
      closeGitPanel();
      setInteractionMode("execute");
      showToast(
        "Modo Execute ativado. Reabra o painel Git para revisar e confirmar a criação do repositório.",
        "success"
      );
    }
    return;
  }
  const facts = "Initialize Git repository at the trusted-workspace root.\n"
    + "Initial branch: main\nNo commit, staging, remote, or project file will be created.";
  if (!await showAppConfirm(facts, {
    title: "Inicializar repositório Git?",
    confirmLabel: "Inicializar"
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
    showToast("Mude para o modo Execute antes de alterar a configuração do repositório.");
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
      elements.gitActionStatus.textContent = "Nenhuma alteração para salvar.";
      return;
    }
    const summary = changes.map(change => change.kind === "identity"
      ? `${change.field} = "${change.value}"`
      : `origin = "${change.value}"`
    ).join("\n");
    if (!await showAppConfirm(
      `Aplicar no repositório local:\n${summary}\n\nA configuração global do Git não será alterada.`,
      { title: "Salvar configuração do repositório?", confirmLabel: "Salvar" }
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
    elements.gitActionStatus.textContent = "Configuração local do repositório salva.";
    showToast("Configuração do repositório salva.", "success");
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
    name.textContent = `${profile.name}${profile.active ? " · ativo" : ""}`;
    const path = document.createElement("small");
    path.textContent = profile.path;
    const metadata = document.createElement("small");
    metadata.textContent = [
      profile.projectProfile?.projectTypes?.join(", ") || "perfil não detectado",
      profile.historyEnabled ? "histórico ativo" : "histórico desativado",
      profile.available ? null : profile.diagnostic || "indisponível"
    ].filter(Boolean).join(" · ");
    const actions = document.createElement("div");
    actions.className = "workspace-profile-actions";
    const activate = document.createElement("button");
    activate.type = "button";
    activate.className = "secondary-button";
    activate.textContent = profile.active ? "Ativo" : "Ativar";
    activate.disabled = profile.active || !profile.available;
    activate.addEventListener("click", () => activateWorkspace(profile.id));
    const rename = document.createElement("button");
    rename.type = "button";
    rename.className = "secondary-button";
    rename.textContent = "Renomear";
    rename.addEventListener("click", () => renameWorkspace(profile));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "secondary-button danger-button";
    remove.textContent = "Remover";
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
  await refreshSessions();
  await refreshGit();
}

async function activateWorkspace(id) {
  await requestConversationTransition(
    async () =>
    {
      elements.workspaceSaveStatus.textContent = "Ativando…";

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
          "Workspace ativado. Modo Chat e aprovação manual restaurados.";
      } catch (error) {
        elements.workspaceSaveStatus.textContent = error.message;
      }
    }
  );
}

async function renameWorkspace(profile) {
  const name = (await showAppPrompt("Informe o novo nome do workspace.", {
    title: "Renomear workspace",
    inputLabel: "Nome do workspace",
    inputValue: profile.name,
    confirmLabel: "Renomear"
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
    `Remover "${profile.name}" e seu histórico local do Agentic Router? `
      + "A pasta real e os arquivos do projeto não serão excluídos.",
    { title: "Remover workspace?", confirmLabel: "Remover", danger: true }
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
      "Ativar histórico local para este workspace? O conteúdo não será criptografado "
        + "pelo Agentic Router v0.9.12.",
      { title: "Ativar histórico local?", confirmLabel: "Ativar" }
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
  state.lockedModel = null;
  elements.modelSelector.value = "auto";
  elements.modelLock.checked = false;
  updateInteractionControls();
  updateModelLockControls();
  await ensureConversationIdentity();
  await refreshSelectedModelCapabilities();
}

function renderProjectProfile() {
  const profile = state.projectProfile;

  if (!profile || profile.status === "unavailable") {
    elements.projectProfileSummary.textContent =
      profile?.diagnostic ?? "Perfil indisponível";
    elements.projectProfileDetails.replaceChildren();
    return;
  }

  elements.projectProfileSummary.textContent =
    `${profile.displayName} · ${profile.projectTypes.join(", ") || "sem marcadores de projeto"}`;
  elements.projectProfileDetails.replaceChildren();
  const repository = document.createElement("p");
  repository.textContent = profile.repository.isGitRepository
    ? `Git · ${profile.repository.branch ?? "detached"} · `
      + `${profile.repository.hasUncommittedChanges ? "alterações existentes" : "limpo"}`
    : "Git não detectado";
  const instructions = document.createElement("p");
  instructions.textContent =
    `${profile.instructionFiles.length} arquivo(s) AGENTS.md`;
  const validation = document.createElement("p");
  validation.textContent =
    `Validação: ${profile.validationProfile?.name ?? "não configurada"} `
    + `(${profile.validationProfile?.source ?? "nenhuma"})`;
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
  elements.projectProfileSummary.textContent = "Atualizando…";

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
    ? `Sugestão detectada: ${detected.name} · ${detected.steps.length} etapa(s)`
    : "Nenhuma sugestão de validação foi detectada.";
  elements.validationProfileName.value = profile?.name ?? "";
  elements.validationSteps.replaceChildren();

  for (const step of profile?.steps ?? []) {
    addValidationStep(step);
  }

  updateValidationCommandPreview();
}

function addValidationStep(step = {}) {
  if (elements.validationSteps.children.length >= 8) {
    elements.validationProfileStatus.textContent = "O limite é de 8 etapas.";
    return;
  }

  const row = document.createElement("section");
  row.className = "validation-step-editor";
  row.innerHTML = `
    <div class="validation-step-grid">
      <label><span>ID</span><input data-field="id" maxlength="40"></label>
      <label><span>Rótulo</span><input data-field="label" maxlength="100"></label>
      <label><span>Executável</span><input data-field="executable" maxlength="260"></label>
      <label class="validation-arguments">
        <span>Argumentos (array JSON)</span>
        <input data-field="arguments" spellcheck="false">
      </label>
      <label><span>Diretório relativo</span><input data-field="workingDirectory"></label>
      <label><span>Timeout (s)</span><input data-field="timeoutSeconds" type="number" min="1" max="120"></label>
      <label class="validation-required">
        <input data-field="required" type="checkbox">
        <span>Obrigatória</span>
      </label>
    </div>
    <div class="validation-step-buttons">
      <button class="secondary-button" data-action="up" type="button">↑</button>
      <button class="secondary-button" data-action="down" type="button">↓</button>
      <button class="secondary-button danger-button" data-action="remove" type="button">Remover</button>
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
      throw new Error("Os argumentos de cada etapa devem ser um array JSON válido.");
    }

    if (!Array.isArray(args) || args.some(item => typeof item !== "string")) {
      throw new Error("Os argumentos de cada etapa devem ser um array JSON de strings.");
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
        + `${step.required ? "obrigatória" : "opcional"}`
      ).join("\n")
      : "Nenhuma etapa configurada.";
  } catch (error) {
    elements.validationCommandPreview.textContent = error.message;
  }
}

function resetValidationProfile() {
  const detected = state.validationProfiles?.detected;
  if (!detected) {
    elements.validationProfileStatus.textContent =
      "Nenhuma sugestão detectada está disponível.";
    return;
  }

  renderValidationProfile(detected);
  elements.validationProfileStatus.textContent =
    "Sugestão carregada. Salve para ativá-la.";
}

async function saveValidationProfile() {
  elements.validationProfileStatus.textContent = "Validando e salvando…";

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
    elements.validationProfileStatus.textContent = "Perfil salvo.";
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
  elements.validationProfileStatus.textContent = "Limpando…";

  try {
    state.validationProfiles = await fetchJson(
      "/api/workspace/validation-profile",
      {
        method: "DELETE"
      }
    );
    renderValidationProfile();
    elements.validationProfileStatus.textContent =
      "Perfil ativo removido. Validação não configurada.";
    await refreshProjectProfile();
  } catch (error) {
    elements.validationProfileStatus.textContent = error.message;
  }
}

function openWorkspace() {
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
  elements.workspaceValidation.textContent = "Selecione uma pasta confiável.";
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
  elements.workspaceSaveStatus.textContent = "Validando…";

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
    elements.workspaceSaveStatus.textContent = "Workspace adicionado e ativado";
  } catch (error) {
    elements.workspaceValidation.textContent = error.message;
    elements.workspaceValidation.className = "workspace-validation invalid";
    elements.workspaceSaveStatus.textContent = "Não foi possível salvar";
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
        "Pasta selecionada. Clique em Salvar para torná-la confiável.";
      elements.workspaceValidation.className = "workspace-validation valid";
      elements.workspaceSaveStatus.textContent = "";
    } else if (result.cancelled) {
      elements.workspaceSaveStatus.textContent = "Seleção cancelada";
    } else {
      elements.workspaceValidation.textContent =
        result.error ?? "Não foi possível abrir o seletor de pastas.";
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
    renderSessionHistory();
    return;
  }

  try {
    state.sessions = await fetchJson("/api/sessions");
  } catch {
    state.sessions = null;
  }

  renderSessionHistory();
}

function renderSessionHistory() {
  elements.pinnedSessions.replaceChildren();
  elements.recentSessions.replaceChildren();
  elements.archivedSessions.replaceChildren();
  const usage = state.sessions?.usage;
  elements.historyUsage.textContent = usage
    ? `${usage.sessionCount} sessão(ões) · ${formatBytes(usage.storageBytes)} · `
      + `${usage.enabled ? "histórico ativo" : "histórico desativado"}`
      + `${usage.oldestSessionAt
        ? ` · mais antiga ${new Date(usage.oldestSessionAt).toLocaleDateString()}`
        : ""}`
      + `${usage.newestSessionAt
        ? ` · mais recente ${new Date(usage.newestSessionAt).toLocaleDateString()}`
        : ""}`
    : "Nenhuma sessão armazenada.";
  elements.enableSessionHistory.hidden = Boolean(usage?.enabled);
  renderPersistenceStatus();

  for (const session of state.sessions?.pinned ?? []) {
    elements.pinnedSessions.append(
      createSessionEntry(session)
    );
  }

  for (const session of state.sessions?.recent ?? []) {
    elements.recentSessions.append(
      createSessionEntry(session)
    );
  }

  for (const session of state.sessions?.archived ?? []) {
    elements.archivedSessions.append(
      createSessionEntry(session)
    );
  }

  elements.archivedSessionSection.hidden =
    (state.sessions?.archived?.length ?? 0) === 0;
  elements.pinnedSessionSection.hidden =
    (state.sessions?.pinned?.length ?? 0) === 0;
  renderSettingsSummaries();
}

function createSessionEntry(session) {
  const entry = document.createElement("article");
  const current = state.conversationSessionId === session.id;
  entry.className = `session-entry${current ? " current" : ""}`;
  entry.dataset.sessionId = session.id;
  entry.tabIndex = 0;
  entry.setAttribute("role", "button");
  entry.setAttribute("aria-label", `Abrir detalhes de ${session.title}`);
  entry.setAttribute(
    "aria-current",
    current ? "true" : "false"
  );
  const content = document.createElement("div");
  content.className = "session-entry-content";
  const title = document.createElement("strong");
  title.textContent = session.title;
  const metadata = document.createElement("small");
  metadata.textContent = new Date(session.updatedAt).toLocaleDateString();
  const resume = document.createElement("button");
  resume.type = "button";
  resume.className = "secondary-button";
  resume.textContent = "Retomar";
  resume.addEventListener(
    "click",
    event => {
      event.stopPropagation();
      resumeSession(session.id);
    }
  );
  content.append(title, metadata);
  entry.append(content, resume);
  entry.addEventListener("click", () => openSessionDetails(session));
  entry.addEventListener(
    "keydown",
    event => {
      if (event.target !== entry || !["Enter", " "].includes(event.key)) {
        return;
      }

      event.preventDefault();
      openSessionDetails(session);
    }
  );
  return entry;
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
    new Date(session.updatedAt).toLocaleString(),
    session.lastInteractionMode === "execute" ? "Execute" : "Chat",
    session.selectedModel
  ].filter(Boolean).join(" · ");
  elements.sessionDetailsState.textContent = [
    state.conversationSessionId === session.id ? "Conversa atual" : null,
    session.pinned ? "Fixada" : null,
    session.hasSummary ? "Com resumo" : "Sem resumo",
    session.interrupted ? "Interrompida" : null,
    session.archived ? "Arquivada" : null
  ].filter(Boolean).join(" · ");
  elements.sessionDetailsPin.textContent = session.pinned
    ? "Desafixar"
    : "Fixar";
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
    message.textContent = "Carregando resumo…";
    elements.sessionDetailsSummary.append(message);
    return;
  }

  if (!content) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent =
      "Nenhum resumo foi criado. A conversa pode ser retomada normalmente sem ele.";
    elements.sessionDetailsSummary.append(empty);
    return;
  }

  const fields = [
    ["Objetivo", content.objective],
    ["Decisões", content.decisions],
    ["Arquivos alterados", content.filesChanged],
    ["Comandos e validação", content.commandsAndValidation],
    ["Questões não resolvidas", content.unresolvedIssues],
    ["Próximo passo", content.nextSuggestedStep]
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
  await resumeSession(session.id);
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
    ? `Cópia criada: ${duplicate.session.title}`
    : "A conversa não foi duplicada.";
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

async function resumeSession(id) {
  await requestConversationTransition(
    async () =>
    {
      if (!await showAppConfirm(
        "Retomar esta conversa? Modo Chat, aprovação manual e modelo não fixado serão restaurados.",
        { title: "Retomar conversa?", confirmLabel: "Retomar" }
      )) {
        return;
      }
      const nextBrowserSessionId = createSessionId();

      try {
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
        state.conversationState = session.interrupted
          ? "interrupted"
          : session.state;
        state.interactionMode = "chat";
        state.approvalPolicy = "auto";
        state.lockedModel = null;
        elements.modelSelector.value = session.selectedModel
          && state.models.some(model => model.name === session.selectedModel)
          ? session.selectedModel
          : "auto";
        renderRestoredConversation(session);
        await refreshSelectedModelCapabilities();
        setPersistenceStatus(
          session.interrupted
            ? "Interrupted"
            : "Saved locally"
        );
        updateInteractionControls();
        updateModelLockControls();
        updateComposerStatus();
        elements.workspaceDialog.close();
        await refreshSessions();
        await refreshGit();
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

function renderRestoredConversation(session) {
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
        assistant.summary.textContent = "Histórico restaurado";
        assistant.answer.classList.remove("pending");
        assistant.answer.textContent = message.content;
        assistant.rawAnswer = message.content;
        assistant.copyButton.disabled = false;
      }
    }
  );

  if (session.interrupted) {
    const warning = document.createElement("article");
    warning.className = "message assistant";
    warning.textContent =
      "A execução anterior foi interrompida. Ações concluídas foram preservadas. "
      + "Nenhum processo ou aprovação pendente foi retomado. Continue com um novo turno.";
    elements.messages.append(warning);
  }

  if (session.contextTruncated) {
    const notice = document.createElement("p");
    notice.className = "workspace-note";
    notice.textContent =
      "Mensagens antigas continuam visíveis, mas serão omitidas do próximo contexto do modelo.";
    elements.messages.append(notice);
  }

  if (session.executionReviews.length > 0) {
    const review = session.executionReviews.at(-1);
    state.latestExecutionSessionId = review.summary.id;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button";
    button.textContent = "Revisar alterações concluídas";
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

async function renameSession(session) {
  const title = (await showAppPrompt("Informe o novo título da conversa.", {
    title: "Renomear conversa",
    inputLabel: "Título",
    inputValue: session.title,
    confirmLabel: "Renomear"
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
      `Cópia criada: ${duplicate.session.title}`;
    await refreshSessions();
    return duplicate;
  } catch (error) {
    elements.sessionSearchStatus.textContent = error.message;
    return null;
  }
}

function openSessionSearch() {
  elements.sessionSearchStatus.textContent =
    "A busca usa somente os arquivos locais de sessão.";
  elements.sessionSearchResults.replaceChildren();
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
  elements.sessionSearchStatus.textContent = "Buscando registros locais…";
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
      `${result.results.length} resultado(s) · ${result.scannedSessions} sessão(ões) examinadas`
      + `${result.truncated ? " · resultado limitado" : ""}`
      + ` · ${result.workspaceScope === "active-workspace"
        ? "workspace ativo"
        : "todos os workspaces"}`;
  } catch (error) {
    elements.sessionSearchStatus.textContent = error.name === "AbortError"
      ? "Busca cancelada."
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
      new Date(result.updatedAt).toLocaleString(),
      result.model,
      result.pinned ? "fixada" : null,
      result.archived ? "arquivada" : null
    ].filter(Boolean).join(" · ");
    const field = document.createElement("small");
    field.textContent = `Correspondência: ${result.matchField}`;
    const snippet = document.createElement("p");
    appendHighlightedSnippet(
      snippet,
      result.snippet,
      result.highlights
    );
    const open = document.createElement("button");
    open.type = "button";
    open.className = "secondary-button";
    open.textContent = "Retomar com segurança";
    open.addEventListener(
      "click",
      async () => {
        closeSessionSearch();
        await resumeSession(result.id);
      }
    );
    entry.append(title, metadata, field, snippet, open);
    elements.sessionSearchResults.append(entry);
  }

  if (response.results.length === 0) {
    const empty = document.createElement("p");
    empty.className = "runtime-note";
    empty.textContent = "Nenhuma conversa corresponde aos filtros.";
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
    "O resumo é separado das mensagens originais.";
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
    elements.sessionSummaryEstimate.textContent = "Selecione um modelo.";
    return;
  }

  elements.sessionSummaryEstimate.textContent = "Calculando fatos limitados…";

  try {
    state.summaryEstimate = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary/estimate`
        + `?model=${encodeURIComponent(model)}`
    );
    const estimate = state.summaryEstimate;
    elements.sessionSummaryEstimate.textContent =
      `${providerLabel(estimate.provider)} · ${estimate.model} · `
      + `até ${formatInteger(estimate.estimatedInputTokens)} tokens estimados · `
      + `${estimate.includedMessages} mensagens incluídas`
      + `${estimate.omittedMessages
        ? ` · ${estimate.omittedMessages} omitidas`
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
    `Gerar resumo com ${providerLabel(estimate.provider)} · ${estimate.model}? `
      + `A chamada pode usar GPU ou quota real e estima até `
      + `${formatInteger(estimate.estimatedInputTokens)} tokens de entrada.`,
    { title: "Gerar resumo com modelo?", confirmLabel: "Gerar resumo" }
  )) {
    return;
  }

  elements.generateSessionSummary.disabled = true;
  elements.sessionSummaryStatus.textContent = "Gerando resumo explícito…";

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
      "Resumo gerado e persistido separadamente.";
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
      "Edição do resumo salva sem chamar modelo.";
    await refreshSessions();
  } catch (error) {
    elements.sessionSummaryStatus.textContent = error.message;
  }
}

async function deleteSessionSummary() {
  const session = state.summarySession;

  if (!session || !await showAppConfirm(
    "Excluir somente o resumo desta conversa?",
    { title: "Excluir resumo?", confirmLabel: "Excluir", danger: true }
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
    elements.sessionSummaryStatus.textContent = "Resumo excluído.";
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
    `Excluir somente o registro local "${session.title}"? Os arquivos do projeto serão preservados.`,
    { title: "Excluir conversa?", confirmLabel: "Excluir", danger: true }
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
    "Excluir todas as conversas arquivadas deste workspace?",
    { title: "Excluir conversas arquivadas?", confirmLabel: "Excluir", danger: true }
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
    "Excluir todo o histórico local deste workspace? Os arquivos do projeto serão preservados.",
    { title: "Excluir todo o histórico?", confirmLabel: "Excluir tudo", danger: true }
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
    "Excluir todo o histórico local de uso de tokens? Esta ação não altera conversas nem arquivos do projeto.",
    { title: "Excluir histórico de uso?", confirmLabel: "Excluir", danger: true }
  )) {
    return;
  }

  elements.usagePurgeStatus.textContent = "Excluindo histórico de uso…";

  try {
    const result = await fetchJson(
      "/api/usage?confirmed=true",
      {
        method: "DELETE"
      }
    );
    elements.usagePurgeStatus.textContent =
      `${result.deletedEvents} evento(s) de uso excluído(s).`;
    await refreshUsage();
  } catch (error) {
    elements.usagePurgeStatus.textContent = error.message;
  }
}

async function reconcileUsage() {
  elements.reconcileUsage.disabled = true;
  elements.usagePurgeStatus.textContent = "Validando eventos e reconstruindo agregados…";

  try {
    const result = await fetchJson(
      "/api/usage/reconcile",
      {
        method: "POST"
      }
    );
    elements.usagePurgeStatus.textContent =
      `${formatInteger(result.accepted)} aceitos · `
      + `${formatInteger(result.warned)} com aviso · `
      + `${formatInteger(result.estimated)} estimados · `
      + `${formatInteger(result.rejected)} rejeitados · `
      + `${formatInteger(result.duplicates)} duplicados`;
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
      `${degraded.length} provedor(es) ativo(s) degradado(s) ou indisponível(is).`;
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
    healthy: "Saudável",
    degraded: "Degradado",
    unavailable: "Indisponível",
    "not-configured": "Não configurado",
    unknown: "Desconhecido"
  }[value] ?? value;
}

function formatProviderHealthDate(value) {
  return value
    ? new Date(value).toLocaleString()
    : "ainda não observado";
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
    elements.runtimeSummary.textContent = "Memória indisponível";
    elements.residentModelStatus.textContent = error.message;
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
    elements.runtimeUsageAccuracy.textContent = "indisponível";
    elements.settingsUsageAccuracy.textContent = "indisponível";
    elements.runtimeUsageDetails.textContent =
      `Uso indisponível · ${error.message}`;
    elements.settingsUsageDetails.textContent =
      `Uso indisponível · ${error.message}`;
    elements.cloudUsageBadge.textContent = "indisponível";
    elements.cloudUsageSummary.textContent = "Uso cloud indisponível";
    elements.cloudUsageDetail.textContent = error.message;
  }
}

function renderUsageSummary() {
  const overview = state.usageOverview;

  if (!overview) {
    elements.runtimeUsageAccuracy.textContent = "sem dados";
    elements.settingsUsageAccuracy.textContent = "sem dados";
    elements.runtimeUsageDetails.textContent = "Uso ainda não disponível.";
    elements.settingsUsageDetails.textContent = "Uso ainda não disponível.";
    return;
  }

  const usage = overview.selected;
  const accuracy = usage.accuracy === "exact"
    ? "exato"
    : usage.accuracy === "mixed"
      ? "misto"
      : usage.accuracy === "estimated"
        ? "estimado"
        : "sem dados";
  const lastUpdate = usage.lastUpdatedAt
    ? new Date(usage.lastUpdatedAt).toLocaleString()
    : "nenhuma chamada registrada";
  const topModels = usage.topModels.length
    ? usage.topModels.map(
      item => `${item.key}: ${formatInteger(item.totalTokens)}`
    ).join("\n")
    : "Nenhum modelo no período.";
  const topRoles = usage.topRoles.length
    ? usage.topRoles.map(
      item => `${item.key}: ${formatInteger(item.totalTokens)}`
    ).join("\n")
    : "Nenhum papel no período.";
  const pinnedWindows = overview.pinned.length
    ? overview.pinned.map(
      item => `${item.window.id}: ${formatInteger(item.totalTokens)} tokens`
    ).join("\n")
    : "Nenhuma janela fixada.";
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
    ? `${formatCurrency(comparisonPrice.inputPricePerMillion)}/M entrada · `
      + `${formatCurrency(comparisonPrice.outputPricePerMillion)}/M saída · `
      + `catálogo ${comparisonPrice.catalogVersion} · `
      + `atualizado ${new Date(comparisonPrice.updatedAt).toLocaleDateString()} · `
      + `${comparisonPrice.stale ? "desatualizado" : "atual"}\n`
      + `Fonte da comparação: ${comparisonPrice.officialSourceUrl}`
    : "preço de comparação indisponível";
  const planDetails = plan
    ? `${formatCurrency(plan.monthlyPrice)}/mês · ${plan.usageDescription}\n`
      + `${plan.tokenEquivalent}\n`
      + `${plan.availability ? `${plan.availability}\n` : ""}`
      + `Vigência: ${plan.effectiveDate} · `
      + `${plan.stale ? "referência desatualizada" : "referência atual"}\n`
      + `Fonte oficial: ${plan.officialSourceUrl}`
    : "referência indisponível";
  elements.runtimeUsageAccuracy.textContent = accuracy;
  elements.settingsUsageAccuracy.textContent = accuracy;
  elements.runtimeUsageDetails.textContent =
    `${usage.window.id} · ${formatInteger(usage.totalTokens)} tokens\n`
    + `Input ${formatInteger(usage.inputTokens)} · Output ${formatInteger(usage.outputTokens)}\n`
    + `Equivalente ${formatCurrency(usage.equivalentCloudCost)} · ${comparison}\n`
    + `Atualizado: ${lastUpdate}`;
  elements.runtimeUsageSummary.dataset.accuracy = usage.accuracy;
  elements.settingsUsageSummary.dataset.accuracy = usage.accuracy;
  elements.settingsUsageDetails.textContent =
    `Janela: ${usage.window.id}\n`
    + `Entrada / saída / total: ${formatInteger(usage.inputTokens)} / `
    + `${formatInteger(usage.outputTokens)} / ${formatInteger(usage.totalTokens)}\n`
    + `Chamadas: ${usage.requests} · Sucesso: ${usage.successes} · `
    + `Falha: ${usage.failures} · Cancelamento: ${usage.cancellations}\n`
    + `Local / cloud: ${formatInteger(local)} / ${formatInteger(cloud)} tokens\n`
    + `Custo estimado do provedor: ${formatCurrency(usage.estimatedActualCost)}\n`
    + `Estimativa cloud equivalente: ${formatCurrency(usage.equivalentCloudCost)} `
    + `contra ${comparison}\n`
    + `Tarifas de comparação: ${comparisonDetails}\n`
    + `Esta é uma comparação equivalente, não uma economia exata no Ollama Cloud.\n`
    + `Principais modelos:\n${topModels}\n`
    + `Principais papéis:\n${topRoles}\n`
    + `Janelas fixadas:\n${pinnedWindows}\n`
    + `Referência de plano Ollama: ${plan?.plan ?? "indisponível"}\n`
    + `${planDetails}\n`
    + `Última atualização: ${lastUpdate}`;
}

function renderCloudUsage() {
  const dashboard = state.cloudUsageDashboard;
  const active = parseModelReference(
    state.activeAgentModel
      ?? state.lockedModel
      ?? elements.modelSelector.value
  );
  const activeProvider = active.provider === "ollama-local"
    ? null
    : dashboard?.providers.find(
      provider => provider.providerId === active.provider
    );

  delete elements.cloudUsageCard.dataset.alert;

  if (!dashboard || dashboard.providers.length === 0) {
    elements.cloudUsageBadge.textContent = "não configurado";
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
    elements.cloudUsageBadge.textContent = "inativo";
    elements.cloudUsageSummary.textContent =
      `${dashboard.connectedProviderCount} provedor(es) conectado(s)`;
    elements.cloudUsageDetail.textContent = "Nenhum modelo cloud ativo";
  } else {
    elements.cloudUsageBadge.textContent = "desconectado";
    elements.cloudUsageSummary.textContent =
      `${dashboard.providers.length} provedor(es) configurado(s)`;
    elements.cloudUsageDetail.textContent = "Nenhum modelo cloud ativo";
  }

  elements.cloudUsageDashboardSummary.textContent = dashboard
    ? `Janela selecionada: ${dashboard.selectedWindow}\n`
      + `Provedores conectados: ${dashboard.connectedProviderCount}\n`
      + `Alertas locais: ${dashboard.alertThresholds.join("%, ")}%\n`
      + `Atualizado: ${new Date(dashboard.generatedAt).toLocaleString()}`
    : "Dashboard ainda não disponível.";
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
    cloudUsageMetric("Requisições", formatInteger(provider.requests)),
    cloudUsageMetric(
      "Custo estimado",
      formatCurrency(provider.estimatedActualCost)
    ),
    cloudUsageMetric(
      "Última chamada",
      provider.latestRequestAt
        ? new Date(provider.latestRequestAt).toLocaleString()
        : "nenhuma"
    )
  );

  const quotaDetail = document.createElement("small");
  quotaDetail.textContent =
    `Quota: ${provider.quotaSource} · ${provider.window}`
    + `${provider.resetAt
      ? ` · reset ${new Date(provider.resetAt).toLocaleString()}`
      : ""}`;
  const billingDetail = document.createElement("small");
  billingDetail.textContent =
    `${billingModeLabel(provider.expectedBillingMode)} é apenas uma expectativa local; `
    + "não garante faturamento ou gratuidade.";
  const warning = document.createElement("small");
  warning.hidden = !provider.hasRateLimitWarning;
  warning.className = "cloud-provider-diagnostic";
  warning.textContent = "Aviso: uma resposta 429 foi observada nesta janela.";

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
      + `${formatInteger(model.requests)} chamada(s) · `
      + `${formatCurrency(model.estimatedActualCost)} · `
      + `${model.roles.join(", ") || "sem papel observado"}`;
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
    empty.textContent = "Nenhum modelo em cache ou uso observado.";
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
  elements.cloudUsageRefreshStatus.textContent = "Atualizando dados locais…";

  try {
    state.cloudUsageDashboard = await fetchJson("/api/usage/cloud-dashboard");
    renderCloudUsage();
    elements.cloudUsageRefreshStatus.textContent = "Dashboard atualizado.";
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
    exact: "exato",
    estimated: "estimado",
    mixed: "misto",
    unavailable: "indisponível"
  }[accuracy] ?? "indisponível";
}

function billingModeLabel(mode) {
  return {
    "free-tier": "Free tier esperado",
    paid: "Pago esperado",
    unknown: "Faturamento desconhecido"
  }[mode] ?? "Faturamento desconhecido";
}

function formatPercentage(value) {
  return `${Number(value).toLocaleString(
    undefined,
    {
      maximumFractionDigits: 2
    }
  )}%`;
}

function renderRuntimeStatus(runtime) {
  const compact = [];
  const memoryRows = [];
  const ram = runtime.systemMemory;

  if (ram.status === "available") {
    compact.push(
      `RAM ${formatGiB(ram.usedBytes)} / ${formatGiB(ram.totalBytes)} ${formatPercent(ram.usedPercent)}`
    );
    memoryRows.push(
      memoryRow(
        "RAM do sistema",
        ram.usedBytes,
        ram.totalBytes,
        ram.usedPercent,
        ram.diagnostic,
        "system"
      )
    );
  } else {
    compact.push("RAM n/d");
    memoryRows.push(
      diagnosticRow(
        "RAM do sistema",
        ram.diagnostic
      )
    );
  }

  for (const device of runtime.devices) {
    compact.push(
      device.usedDedicatedMemoryBytes == null
        ? `${device.name} n/d`
        : `${device.name} ${formatGiB(device.usedDedicatedMemoryBytes)} / `
          + `${formatGiB(device.totalDedicatedMemoryBytes)} ${formatPercent(device.usedPercent)}`
    );
    memoryRows.push(
      device.usedDedicatedMemoryBytes == null
        ? diagnosticRow(
          device.name,
          device.diagnostic ?? `Total dedicado: ${formatGiB(device.totalDedicatedMemoryBytes)}`,
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
        "Dispositivos gráficos",
        runtime.devicesDiagnostic
      )
    );
  }

  compact.push(
    `Modelos ${runtime.loadedModels.length} carregado${runtime.loadedModels.length === 1 ? "" : "s"}`
  );
  if (runtime.warning) {
    compact.push(
      `⚠ ${runtime.warnings.length} aviso${runtime.warnings.length === 1 ? "" : "s"}`
    );
  }
  elements.runtimeSummary.textContent = compact.join(" · ");
  elements.runtimeSummary.title = runtime.warnings.join("\n");
  elements.runtimeMemoryList.replaceChildren(...memoryRows);
  elements.runtimeModelList.replaceChildren(
    ...(runtime.loadedModels.length === 0
      ? [
        diagnosticRow(
          runtime.loadedModelsStatus === "unavailable"
            ? "Telemetria do Ollama"
            : "Nenhum modelo reportado",
          runtime.loadedModelsDiagnostic
            ?? "O Ollama não informou modelos carregados em /api/ps."
        )
      ]
      : runtime.loadedModels.map(model => loadedModelRow(model)))
  );
  elements.residentModelStatus.textContent =
    `${runtime.residentModel.configuredModel || "não configurado"} · `
    + `${runtime.residentModel.state}`
    + `${runtime.residentModel.loaded ? " · carregado" : ""}`
    + `${runtime.residentModel.requestedContextTokens
      ? ` · contexto ${formatInteger(runtime.residentModel.actualContextTokens)} / ${formatInteger(runtime.residentModel.requestedContextTokens)}`
      : ""}`
    + `${runtime.residentModel.diagnostic ? ` · ${runtime.residentModel.diagnostic}` : ""}`;
  elements.residentModelStatus.dataset.state = runtime.residentModel.state;
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
  value.textContent = diagnostic ?? "Indisponível";
  row.append(label, value);
  return row;
}

function loadedModelRow(model) {
  const row = document.createElement("div");
  row.className = "loaded-model-row";
  const name = document.createElement("strong");
  name.textContent = `${model.name}${model.isResidentModel ? " · residente" : ""}`;
  const details = document.createElement("span");
  details.textContent =
    `${model.role ?? "papel não configurado"} · ${shortDigest(model.digest)} · `
    + `contexto ${formatInteger(model.actualContextTokens)} / `
    + `${formatInteger(model.requestedContextTokens)} · ${model.profileStatus}`
    + `${model.sharedAcrossRoles ? " · compartilhado" : ""}\n`
    + `Total ${formatGiB(model.totalSizeBytes)} · VRAM ${formatGiB(model.vramSizeBytes)} · `
    + `RAM estimada ${formatGiB(model.estimatedRamSizeBytes)} · ${model.processor}`
    + `${model.expiresAt ? ` · expira ${new Date(model.expiresAt).toLocaleTimeString()}` : ""}`;
  row.append(name, details);
  return row;
}

function formatInteger(value) {
  return new Intl.NumberFormat().format(Number(value ?? 0));
}

function formatCurrency(value) {
  const number = Number(value ?? 0);
  const digits = Math.abs(number) > 0 && Math.abs(number) < 0.01
    ? 6
    : 2;
  return new Intl.NumberFormat(
    undefined,
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
  residentCoordinator: "Coordenador residente",
  specialist: "Especialista",
  primary: "Primário",
  fallback: "Fallback",
  benchmark: "Benchmark",
  modelTest: "Teste de modelo",
  webSearchSynthesis: "Síntese de busca web",
  visionRequest: "Requisição com visão"
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
      ["Mín.", "minimumContextTokens"],
      ["Alvo", "targetContextTokens"],
      ["Máx.", "maximumContextTokens"],
      ["Saída", "outputTokenLimit"],
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
      title: model.digest ?? "digest indisponível"
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
      percentCaption.textContent = "Uso máximo (%)";
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
      freeCaption.textContent = "VRAM livre (GiB)";
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
    empty.textContent = "Nenhuma GPU específica foi detectada.";
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
      "O modelo local precisa ter um digest exato para receber um override.";
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
    `Override preparado para ${model.name}@${shortDigest(model.digest)} · ${runtimeRoleLabels[role]}. Salve as configurações para aplicar.`;
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
    "Override removido do rascunho. Salve as configurações para aplicar.";
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
    `Analisando metadados de ${model}; o modelo não será carregado…`;

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
      + `Declarado: ${formatInteger(recommendation.declaredMaximumContext)} · `
      + `configurado: ${formatInteger(recommendation.configuredContext)}\n`
      + `Sugestão ${formatInteger(recommendation.suggestedMinimum)} / `
      + `${formatInteger(recommendation.suggestedTarget)} / `
      + `${formatInteger(recommendation.suggestedMaximum)} · `
      + `confiança ${recommendation.confidence}\n`
      + `Origem: ${recommendation.source} · ${recommendation.reason}\n`
      + `Carga alterada: ${result.loadedModelChanged ? "sim (inesperado)" : "não"}`;
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
    `Medir ${model} com ${formatInteger(context)} tokens de contexto?\n\n`
    + "Esta ação carregará um modelo real no Ollama e poderá usar GPU, VRAM e RAM. "
    + "O Host tentará restaurar o estado residente anterior.";

  if (!await showAppConfirm(consent, {
    title: "Executar medição real?",
    confirmLabel: "Executar medição"
  })) {
    return;
  }

  elements.measureRuntimeProfile.disabled = true;
  elements.runtimeProfileResult.textContent =
    `Medindo ${model} em ${formatInteger(context)} tokens…`;

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
      + `RAM estimada ${formatGiB(measurement.estimatedRamSizeBytes)} · `
      + `${measurement.processor}\n`
      + `Carga ${formatInteger(measurement.loadDurationMilliseconds)} ms · `
      + `residente restaurado: ${result.priorResidentRestored ? "sim" : "não necessário"}`;
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
    ? `${payload.message}\nCódigo: ${payload.code} · etapa: ${payload.stage} · trace: ${payload.traceId}`
    : error.message;
}

function shortDigest(value) {
  return value
    ? value.slice(0, 12)
    : "sem digest";
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
  updateModelLockControls();
}

function renderSettings() {
  if (!state.settings) {
    return;
  }

  elements.ollamaUrl.value = state.settings.ollamaUrl;
  replaceOptions(elements.routerModel, modelOptions(), state.settings.routerModel);
  replaceOptions(
    elements.actionModel,
    modelOptions(),
    state.settings.actionModel
  );
  replaceOptions(
    elements.coordinatorModel,
    modelOptions(),
    state.settings.coordinatorModel
  );
  replaceOptions(elements.defaultModel, modelOptions(), state.settings.defaultModel);
  replaceOptions(elements.defaultGpu, gpuOptions(false), state.settings.defaultGpu);
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
    "ollama-local": "Modelos locais",
    groq: "Groq",
    "google-ai-studio": "Google AI Studio",
    cerebras: "Cerebras"
  };

  return state.models.map(model => {
    const organized = organizedModel(model.name);
    const capabilities = model.capabilities;
    const badges = [
      capabilities?.nativeTools ? "tools" : null,
      capabilities?.vision ? "visão" : null,
      capabilities?.streaming ? "stream" : null
    ].filter(Boolean);

    return {
      value: model.name,
      label: `${organized?.alias ?? model.displayName ?? model.name}`
        + `${organized?.alias ? ` · ${model.name}` : ""}`
        + `${organized?.favorite ? " ★" : ""}`
        + `${badges.length ? ` · ${badges.join(" · ")}` : ""}`,
      group: groups[model.provider] ?? model.provider ?? "Modelos locais",
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
      model.available ? "disponível" : "indisponível",
      model.conformanceApproved ? "conformidade aprovada" : null,
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
    alias.placeholder = "Alias local";
    alias.value = model.alias ?? "";
    alias.dataset.modelAlias = "";
    const note = document.createElement("input");
    note.type = "text";
    note.maxLength = 500;
    note.placeholder = "Nota opcional";
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
        label: "Salvar alias e nota"
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
    empty.textContent = "Nenhum modelo corresponde aos filtros.";
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
          label: "Nenhum"
        }
      ]
      : []),
    ...(state.modelOrganization?.models ?? []).map(
      model => ({
        value: model.qualifiedId,
        label: `${model.alias ?? model.modelId}`
          + `${model.alias ? ` · ${model.qualifiedId}` : ""}`
          + `${model.available ? "" : " (indisponível)"}`,
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
        label: "Novo perfil"
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
        label: "Nenhum perfil preferido"
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
      "Preencha os campos e salve para gerar a visualização autoritativa.";
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
  elements.modelProfileStatus.textContent = "Validando e salvando perfil…";

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
    elements.modelProfileStatus.textContent = "Perfil salvo sem iniciar modelo.";
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
        + `Disponível: ${item.available ? "sim" : "não"} · `
        + `Conformidade: ${item.conformanceApproved ? "aprovada" : "não aprovada"} · `
        + `Tools: ${item.toolPath} · Web: ${item.web ? "sim" : "não"} · `
        + `Vision: ${item.vision ? "sim" : "não"}`
    ),
    `Fallback local: ${preview.localFallbackValid ? "válido" : "inválido"}`,
    `Workspaces afetados: ${preview.affectedWorkspaces.join(", ") || "nenhum"}`,
    ...preview.errors.map(error => `ERRO: ${error}`)
  ].join("\n\n");
}

async function applyModelProfile() {
  const profileId = elements.modelProfileSelector.value;

  if (!profileId || !await showAppConfirm(
    "Aplicar este perfil atomicamente às novas solicitações? A conversa atual não será reiniciada.",
    { title: "Aplicar perfil?", confirmLabel: "Aplicar" }
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
      "Perfil aplicado. O lock da conversa atual foi preservado.";
  } catch (error) {
    elements.modelProfileStatus.textContent = error.message;
  } finally {
    elements.applyModelProfile.disabled = false;
  }
}

async function deleteModelProfile() {
  const profileId = elements.modelProfileSelector.value;

  if (!profileId || !await showAppConfirm(
    "Excluir este perfil salvo?",
    { title: "Excluir perfil?", confirmLabel: "Excluir", danger: true }
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
    elements.modelProfileStatus.textContent = "Perfil excluído.";
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
      "Preferência do workspace salva por referência.";
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
        + `${model?.available ? "disponível" : "indisponível"} · `
        + `conformidade ${model?.conformanceApproved ? "aprovada" : "não aprovada"} · `
        + `tools ${model?.capabilities?.nativeTools ? "sim" : "não"} · `
        + `web ${model?.capabilities?.webSearch ? "sim" : "não"} · `
        + `vision ${model?.capabilities?.vision ? "sim" : "não"}`;
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
    appendDefinition(metadata, "Ativo", provider.enabled ? "Sim" : "Não");
    appendDefinition(metadata, "Chave", provider.maskedKeyState);
    appendDefinition(metadata, "Modelos", String(provider.modelCount));
    appendDefinition(
      metadata,
      "Última atualização",
      provider.lastRefreshAt
        ? new Date(provider.lastRefreshAt).toLocaleString()
        : "Ainda não atualizada"
    );
    appendDefinition(metadata, "Quota", provider.quotaSource);
    if (health) {
      appendDefinition(
        metadata,
        "Saúde",
        providerHealthStateLabel(health.connectionState)
      );
      appendDefinition(
        metadata,
        "Último sucesso",
        formatProviderHealthDate(health.lastSuccessfulRequest)
      );
      appendDefinition(
        metadata,
        "Latência",
        health.totalLatencyMilliseconds == null
          ? "indisponível"
          : `${formatInteger(health.totalLatencyMilliseconds)} ms`
      );
      appendDefinition(metadata, "Uso", usageAccuracyLabel(health.tokenUsageAccuracy));
    }

    const billingField = document.createElement("label");
    billingField.className = "cloud-billing-field";
    const billingLabel = document.createElement("span");
    billingLabel.textContent = "Modo de faturamento esperado";
    const billingSelect = document.createElement("select");
    billingSelect.dataset.cloudBilling = provider.provider;
    replaceOptions(
      billingSelect,
      [
        {
          value: "unknown",
          label: "Desconhecido"
        },
        {
          value: "free-tier",
          label: "Free tier"
        },
        {
          value: "paid",
          label: "Pago"
        }
      ],
      provider.expectedBillingMode ?? "unknown"
    );
    billingField.append(billingLabel, billingSelect);

    const keyField = document.createElement("label");
    const keyLabel = document.createElement("span");
    keyLabel.textContent = provider.hasKey ? "Substituir chave" : "API key";
    const keyInput = document.createElement("input");
    keyInput.type = "password";
    keyInput.autocomplete = "new-password";
    keyInput.dataset.cloudKey = provider.provider;
    keyInput.dataset.ignoreSettingsDirty = "";
    keyInput.placeholder = provider.hasKey
      ? "Digite uma nova chave"
      : "Digite a chave";
    keyField.append(keyLabel, keyInput);

    const actions = document.createElement("div");
    actions.className = "settings-action-row";
    actions.append(
      cloudActionButton(
        provider.provider,
        "save-key",
        provider.hasKey ? "Substituir" : "Salvar chave"
      ),
      cloudActionButton(provider.provider, "test", "Testar conexão"),
      cloudActionButton(provider.provider, "refresh", "Atualizar modelos"),
      cloudActionButton(
        provider.provider,
        "remove-key",
        "Remover chave",
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
  appendDefinition(metadata, "Último sucesso", formatProviderHealthDate(provider.lastSuccessfulRequest));
  appendDefinition(
    metadata,
    "Latência",
    provider.totalLatencyMilliseconds == null
      ? "indisponível"
      : `${formatInteger(provider.totalLatencyMilliseconds)} ms`
  );
  appendDefinition(metadata, "Quota", provider.quotaState);
  appendDefinition(metadata, "Uso", usageAccuracyLabel(provider.tokenUsageAccuracy));
  const diagnostic = document.createElement("pre");
  diagnostic.className = "provider-health-diagnostic";
  diagnostic.textContent = [
    `status: ${provider.diagnostic.lastStatusCode ?? "indisponível"}`,
    `retry: ${provider.diagnostic.retryDecision}`,
    `fonte: ${provider.healthSource}`,
    `stale: ${provider.stale ? "sim" : "não"}`
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
    ? "Disponível"
    : "Não configurado";
  summary.append(title, status);

  const body = document.createElement("div");
  body.className = "cloud-provider-body";
  const note = document.createElement("p");
  note.className = "runtime-note";
  note.textContent =
    "Pesquisa somente leitura para modelos locais. É separada dos modelos Ollama Cloud "
      + "e só é usada quando Web for habilitado explicitamente no composer.";
  const keyField = document.createElement("label");
  const keyLabel = document.createElement("span");
  keyLabel.textContent = integration.hasKey
    ? "Substituir chave"
    : "API key separada";
  const keyInput = document.createElement("input");
  keyInput.type = "password";
  keyInput.autocomplete = "new-password";
  keyInput.dataset.webSearchKey = "";
  keyInput.dataset.ignoreSettingsDirty = "";
  keyInput.placeholder = integration.hasKey
    ? "Digite uma nova chave"
    : "OLLAMA_API_KEY";
  keyField.append(keyLabel, keyInput);
  const actions = document.createElement("div");
  actions.className = "settings-action-row";
  const save = document.createElement("button");
  save.type = "button";
  save.className = "secondary-button";
  save.dataset.webSearchAction = "save-key";
  save.textContent = integration.hasKey ? "Substituir" : "Salvar chave";
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "secondary-button danger-button";
  remove.dataset.webSearchAction = "remove-key";
  remove.textContent = "Remover chave";
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
    connected: "Conectado",
    error: "Erro",
    disabled: "Desativado",
    "key-required": "Chave necessária",
    "not-tested": "Não testado"
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
      diagnostic.textContent = "Digite uma API key antes de salvar.";
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
      "Remover permanentemente a chave protegida deste provedor?",
      { title: "Remover chave?", confirmLabel: "Remover", danger: true }
    )) {
      return;
    }

    path = `/api/cloud-providers/${encodeURIComponent(provider)}/key?confirmed=true`;
    options = { method: "DELETE" };
  } else {
    const operation = action === "test" ? "testar a conexão" : "atualizar os modelos";

    if (!await showAppConfirm(
      `Permitir uma chamada real ao provedor para ${operation}? Essa ação pode consumir quota.`,
      { title: "Autorizar chamada ao provedor?", confirmLabel: "Autorizar" }
    )) {
      return;
    }

    path = action === "test"
      ? `/api/cloud-providers/${encodeURIComponent(provider)}/test`
      : `/api/cloud-providers/${encodeURIComponent(provider)}/models/refresh`;
    options = { method: "POST" };
  }

  button.disabled = true;
  diagnostic.textContent = "Processando…";

  try {
    await fetchJson(path, options);
    await refreshCloudProviderState();
    showToast("Ação do provedor concluída.", "success");
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
      diagnostic.textContent = "Digite a chave separada antes de salvar.";
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
      "Remover permanentemente a chave protegida do Ollama Web Search?",
      { title: "Remover chave?", confirmLabel: "Remover", danger: true }
    )) {
      return;
    }

    path = "/api/web-search/key?confirmed=true";
    options = { method: "DELETE" };
  }

  button.disabled = true;
  diagnostic.textContent = "Processando…";

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
}

function gpuOptions(includeDefault) {
  const options = includeDefault
    ? [
      {
        value: "default",
        label: "Default"
      }
    ]
    : [];

  for (const device of state.devices) {
    options.push({
      value: device.id,
      label: device.isAuto ? "Auto" : device.name
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
      "Modelo",
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
          label: "Nenhum"
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
      gpuOptions(true),
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
      label: `${selected} (indisponível)`,
      group: "Configuração atual"
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
    elements.modelContextDiagnostic.textContent = "Carregando diagnóstico…";
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
  elements.modelTestResult.textContent = `Testando ${model}…`;

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
      ? `${result.model} · Concluído · Time to first chunk: `
        + `${result.timeToFirstChunkMilliseconds ?? "unavailable"} ms · `
        + `Total duration: ${result.totalDurationMilliseconds} ms`
      : `${result.model} · Falhou · ${result.error} · Trace ID: ${result.traceId}`;
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderModelDiagnostics();
  } catch (error) {
    elements.modelTestResult.textContent = error.message;
  } finally {
    elements.testModel.disabled = false;
  }
}

async function openSettings(section = "general") {
  elements.settingsErrors.hidden = true;
  elements.saveStatus.textContent = "";
  elements.modelTestResult.textContent = "";
  renderSettings();
  elements.settingsDialog.showModal();
  state.settingsDirty = false;
  updateSettingsDirtyState();
  setSettingsSection(section, false);
  document.querySelector(`[data-settings-target="${section}"]`)?.focus();

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
      "Descartar as alterações de configuração ainda não salvas?",
      { title: "Fechar sem salvar?", confirmLabel: "Descartar", danger: true }
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
  elements.saveStatus.textContent = "Salvando…";
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
    actionModel: elements.actionModel.value,
    coordinatorModel: elements.coordinatorModel.value,
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
    elements.saveStatus.textContent = "Salvo";
    state.settingsDirty = false;
    updateSettingsDirtyState();
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderSettings();
    elements.settingsDialog.close();
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
      control = field.toLowerCase().includes("fallback")
        ? card?.querySelector(".intention-fallback-model")
        : card?.querySelector(".intention-model");
      card?.classList.add("field-invalid-card");
    } else if (field.startsWith("router")) {
      control = elements.routerModel;
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
    : "Sem alterações";
  elements.settingsDirty.className =
    `badge ${state.settingsDirty ? "error" : "muted"}`;
  elements.saveSettings.disabled =
    Boolean(state.recovery?.settingsReadOnly) || !state.settingsDirty;
}

async function loadPortableYaml() {
  elements.settingsYamlStatus.textContent = "Carregando configuração…";
  elements.settingsYamlStatus.className = "portable-yaml-status";

  try {
    elements.settingsYaml.value = await fetchText("/api/settings/yaml");
    elements.settingsYamlStatus.textContent = "Configuração atual carregada.";
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
    elements.settingsYamlStatus.textContent = `${file.name} carregado. Revise e clique em Importar e aplicar.`;
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
    "YAML copiado"
  );
  elements.settingsYamlStatus.textContent = "YAML copiado.";
  elements.settingsYamlStatus.className = "portable-yaml-status success";
}

function downloadPortableYaml() {
  const yaml = elements.settingsYaml.value;

  if (!yaml.trim()) {
    elements.settingsYamlStatus.textContent = "Não há YAML para baixar.";
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
  elements.settingsYamlStatus.textContent = "Arquivo agentic-router.yaml preparado.";
  elements.settingsYamlStatus.className = "portable-yaml-status success";
}

async function createLocalBackup() {
  elements.localBackupStatus.textContent = "Criando arquivo com manifesto e hashesâ€¦";

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
      "Backup criado sem chaves, aprovaÃ§Ãµes ou estado de processo.";
  } catch (error) {
    elements.localBackupStatus.textContent = error.message;
  }
}

async function inspectLocalBackup() {
  const file = elements.backupRestoreFile.files?.[0];

  if (!file) {
    return;
  }

  elements.localBackupStatus.textContent = "Validando manifesto e hashesâ€¦";

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
      `${inspection.manifest.categories.join(", ")} Â· `
      + `${inspection.manifest.entries.length} arquivos Â· hashes vÃ¡lidos Â· `
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
    `Restaurar as categorias ${categories.join(", ")}? `
      + "O estado atual será salvo antes da aplicação atômica.",
    { title: "Restaurar backup?", confirmLabel: "Restaurar" }
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
      `Restaurado: ${result.restoredCategories.join(", ")}. `
      + `Backup anterior: ${result.currentDataBackup}. Reinicie para recarregar.`;
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
    elements.settingsYamlStatus.textContent = "Informe uma configuração YAML.";
    elements.settingsYamlStatus.className = "portable-yaml-status error";
    return;
  }

  if (
    state.settingsDirty
    && !await showAppConfirm(
      "A importação substituirá as alterações ainda não salvas deste formulário. Continuar?",
      { title: "Importar configuração?", confirmLabel: "Importar" }
    )
  ) {
    return;
  }

  elements.importSettingsYaml.disabled = true;
  elements.settingsYamlStatus.textContent = "Validando e aplicando…";
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
    elements.settingsYamlStatus.textContent = "Configuração YAML importada e aplicada.";
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

function setSettingsSection(section, moveFocus) {
  const target = document.querySelector(
    `[data-settings-section="${section}"]`
  );
  if (!target) {
    return;
  }
  state.settingsSection = section;
  elements.settingsSectionSelect.value = section;
  elements.settingsDialog.dataset.section = section;
  elements.settingsNavigation.querySelectorAll("[data-settings-target]").forEach(
    button => button.setAttribute(
      "aria-current",
      button.dataset.settingsTarget === section ? "page" : "false"
    )
  );
  target.scrollIntoView({
    block: "start"
  });
  if (moveFocus) {
    target.focus({
      preventScroll: true
    });
  }
}

function navigateToSettingsError(field) {
  const section = !field
    ? "general"
    : field.startsWith("ollama")
      ? "ollama"
      : field.startsWith("router") || field.startsWith("intentions")
        ? "models"
        : field.startsWith("coordinator")
          ? "coordinator"
          : field.startsWith("execution")
            ? "execution"
            : field.startsWith("runtime") || field.startsWith("context")
              ? "runtime"
              : field.startsWith("usage")
                ? "runtime"
              : field.startsWith("git")
                ? "git"
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
  elements.sessionHistory.open = true;
  elements.sessionHistory.scrollIntoView({
    block: "nearest"
  });
  elements.historyNewConversation.focus();
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
  if (elements.modelSelector.value === "auto") {
    state.lockedModel = null;
    elements.modelLock.checked = false;
  }

  updateModelLockControls();
  updateInteractionControls();
  updateComposerModelTitle();
  updateComposerStatus();
  state.activeAgentModel = null;
  state.activeAgentRole = null;
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
    ?? state.lockedModel
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

  elements.activeProviderModel.textContent = "Verificando capacidades…";

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
      view?.webUnavailableReason ?? "Capacidades indisponíveis";
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
      status: "Ativo nesta conversa",
      enabled: true,
      description: isLocal
        ? `O modelo ${view.model} está sendo executado pelo provedor Ollama Local.`
        : `O modelo ${view.model} está sendo executado pelo provedor ${view.providerDisplayName}.`,
      documentationUrl: isLocal
        ? "https://docs.ollama.com/api/introduction"
        : routerConfigurationDocumentation
    },
    capabilities.nativeTools
      ? {
        label: "Tools",
        kind: "tools",
        status: capabilities.toolProtocolConfirmed
          ? "Habilitado e confirmado"
          : "Habilitado; confirmação comportamental pendente",
        enabled: true,
        description: capabilities.toolProtocolConfirmed
          ? "O modelo pode chamar ferramentas estruturadas e esse protocolo já foi confirmado pelo Router."
          : `O modelo anuncia chamadas de ferramentas em ${capabilities.source}; a conformidade comportamental é verificada separadamente.`,
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
          ? "Habilitado nesta conversa"
          : "Disponível, mas desabilitado nesta conversa",
        enabled: state.webEnabled,
        description: capabilities.providerNativeWebSearch
          ? "Pesquisa web nativa do provedor. O usuário precisa habilitá-la explicitamente para a conversa."
          : "Pesquisa Ollama separada e somente leitura. O usuário precisa habilitá-la explicitamente para a conversa.",
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/web-search"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.vision
      ? {
        label: "Vision",
        kind: "vision",
        status: "Habilitado para este modelo",
        enabled: true,
        description: `Aceita até ${capabilities.maximumImageCount} imagens, com ${formatBytes(capabilities.maximumImageBytes)} por imagem.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/vision"
          : routerCapabilityDocumentation
      }
      : null,
    capabilities.structuredOutput
      ? {
        label: "Structured",
        kind: "structured",
        status: "Habilitado para este modelo",
        enabled: true,
        description: `O modelo pode responder conforme um schema estruturado; evidência obtida de ${capabilities.source}.`,
        documentationUrl: isLocal
          ? "https://docs.ollama.com/capabilities/structured-outputs"
          : routerCapabilityDocumentation
      }
      : null,
    {
      label: view.role === "fallback" ? "Fallback" : "Primary",
      kind: view.role === "fallback" ? "fallback" : "primary",
      status: "Papel ativo nesta conversa",
      enabled: true,
      description: view.role === "fallback"
        ? "Este modelo está atendendo como fallback configurado após a indisponibilidade elegível do modelo principal."
        : "Este modelo é o destino principal selecionado para esta conversa.",
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
      `${tag.label}: ${tag.status}. Abrir detalhes.`
    );

    popover.id = popoverId;
    popover.className = "capability-popover";
    popover.setAttribute("role", "dialog");
    popover.setAttribute("aria-label", `Detalhes de ${tag.label}`);
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
    documentation.textContent = "Saiba mais na documentação oficial ↗";
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
    unavailable: "Web indisponível",
    available: "Web disponível",
    enabled: "Web habilitada",
    off: "Web desligada"
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
      ? "Pesquisa oficial do provedor. Clique para habilitar explicitamente nesta conversa."
      : "Pesquisa Ollama separada e somente leitura. Clique para habilitar explicitamente nesta conversa."
    : state.modelCapability?.webUnavailableReason
      ?? "Nenhuma integração de pesquisa autorizada está disponível.";
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
    elements.composerStatus.textContent = "No máximo 4 imagens por solicitação.";
    return;
  }

  for (const file of files) {
    if (!acceptedTypes.has(file.type)) {
      elements.composerStatus.textContent =
        "Somente JPEG, PNG, WebP e GIF são aceitos; SVG não é permitido.";
      continue;
    }

    if (file.size <= 0 || file.size > 10 * 1024 * 1024) {
      elements.composerStatus.textContent =
        "Cada imagem deve ter no máximo 10 MiB.";
      continue;
    }

    const total = state.attachments.reduce(
      (sum, attachment) => sum + attachment.declaredBytes,
      file.size
    );

    if (total > 20 * 1024 * 1024) {
      elements.composerStatus.textContent =
        "As imagens combinadas devem ter no máximo 20 MiB.";
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
    remove.setAttribute("aria-label", `Remover ${attachment.fileName}`);
    remove.title = `Remover ${attachment.fileName}`;
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
      "Selecione explicitamente um modelo com Vision antes de enviar imagens.";
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
    `${providerLabel(provider)} receberá ${formatBytes(bytes)} de imagens. `
      + "Esses bytes sairão deste computador. Autorizar para este provedor nesta sessão?",
    { title: "Autorizar envio de imagens?", confirmLabel: "Autorizar" }
  );

  if (!approved) {
    elements.composerStatus.textContent = "Envio cloud de imagens não autorizado.";
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
  updateInteractionControls();
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

function handleModelLockChange() {
  if (
    elements.modelLock.checked
    && elements.modelSelector.value !== "auto"
  ) {
    state.lockedModel = elements.modelSelector.value;
  } else {
    state.lockedModel = null;
    elements.modelLock.checked = false;
  }

  updateModelLockControls();
  updateComposerStatus();
}

function updateModelLockControls() {
  const disabled = Boolean(
    state.requestController
  ) || state.conversationTransitioning;
  const isLocked = Boolean(state.lockedModel);
  elements.modelSelector.disabled = disabled || isLocked;
  elements.modelLock.disabled = disabled
    || (!isLocked && elements.modelSelector.value === "auto");
  elements.modelLock.checked = isLocked;
  elements.composer.classList.toggle(
    "model-locked",
    isLocked
  );
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
          selectedModel: state.lockedModel ?? elements.modelSelector.value,
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
  state.lockedModel = null;
  state.interactionMode = "chat";
  state.approvalPolicy = "auto";
  state.activeAgentModel = null;
  state.activeAgentRole = null;
  state.modelCapability = null;
  state.contextUsage = null;
  state.webEnabled = false;
  state.webControlState = "unavailable";
  state.cloudImageApprovals.clear();
  clearAttachments();
  state.autoFollow = true;
  elements.modelSelector.value = "auto";
  elements.modelLock.checked = false;
  elements.messageInput.value = "";
  resizeComposer();
  elements.composer.classList.remove("editing");
  elements.cancelMessageEdit.hidden = true;

  for (const message of elements.messages.children) {
    resizeObserver.unobserve(message);
  }

  const emptyState = createEmptyState();
  elements.emptyState = emptyState;
  elements.messages.replaceChildren(
    emptyState
  );
  updateModelLockControls();
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
  elements.historyNewConversation.disabled = isTransitioning;
  elements.messageInput.disabled = isTransitioning;
  elements.sendButton.disabled = isTransitioning;
  updateInteractionControls();
  updateModelLockControls();
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
  heading.textContent = "Pronto para conversar";
  const description = document.createElement("p");
  description.textContent =
    "Use Auto para classificar a intenção e escolher o modelo configurado.";
  container.append(icon, heading, description);
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
    elements.composer.requestSubmit();
  }
}

function handleComposerClick(event) {
  if (event.target.closest("button, select, option, label")) {
    return;
  }

  elements.messageInput.focus();
}

function resizeComposer() {
  elements.messageInput.style.height = "auto";
  elements.messageInput.style.height = `${elements.messageInput.scrollHeight}px`;
}

async function handleComposerSubmit(event) {
  event.preventDefault();

  if (state.requestController) {
    state.requestController.abort();
    return;
  }

  const message = elements.messageInput.value.trim();

  if (!message) {
    return;
  }

  const selectedModel = state.lockedModel ?? elements.modelSelector.value;

  if (!await ensureCloudImageApproval(selectedModel)) {
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
  elements.messageInput.value = "";
  clearAttachments();
  resizeComposer();
  state.requestController = controller;
  setStreamingState(true);
  requestAnimationFrame(scrollToBottom);
  await refreshRuntimeStatus();
  scheduleRuntimeRefresh();
  const compactContext = state.compactContextNextRequest;
  state.compactContextNextRequest = false;

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
          modelLocked: Boolean(state.lockedModel),
          interactionMode: state.interactionMode,
          approvalPolicy: state.approvalPolicy,
          browserSessionId: state.browserSessionId,
          conversationSessionId: state.conversationSessionId,
          webSearchEnabled: state.webEnabled,
          images: requestAttachments,
          compactContext
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
      state.conversationState = "cancelled";
      addActivity(
        assistant,
        {
          type: "request.cancelled",
          message: "Solicitação cancelada pelo usuário.",
          elapsedMilliseconds: elapsedSince(assistant)
        },
        false
      );
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Cancelado", false);
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
      assistant.answer.textContent ||= "Não foi possível concluir a resposta.";
      assistant.answer.classList.add("error");
      assistant.answer.classList.remove("pending");
      finishActivity(assistant, "Falhou", true);
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
      `${attachments.length} imagem${attachments.length === 1 ? "" : "ns"} anexada`
      + `${attachments.length === 1 ? "" : "s"} · bytes não persistidos`;
    content.append(document.createElement("br"), attachmentNote);
  }
  const actions = document.createElement("div");
  actions.className = "message-actions";
  const editButton = createMessageActionButton(
    "Editar",
    "Editar mensagem"
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
  progress.textContent = "Pensando… · 0 ms";

  const details = document.createElement("details");
  details.className = "activity";
  details.open = false;
  const summary = document.createElement("summary");
  summary.textContent = "Detalhes técnicos";
  summary.setAttribute("aria-label", "Detalhes técnicos da solicitação");
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
  answer.className = "assistant-answer pending";
  const workActivity = document.createElement("section");
  workActivity.className = "assistant-work";
  workActivity.hidden = true;
  const sources = document.createElement("details");
  sources.className = "assistant-sources";
  sources.hidden = true;
  const sourcesSummary = document.createElement("summary");
  sourcesSummary.textContent = "Fontes";
  const sourcesList = document.createElement("ol");
  sourcesList.className = "assistant-source-list";
  sources.append(sourcesSummary, sourcesList);
  const actions = document.createElement("div");
  actions.className = "message-actions assistant-actions";
  const copyButton = createMessageActionButton(
    "Copiar",
    "Copiar resposta"
  );
  copyButton.classList.add("copy-message");
  copyButton.disabled = true;
  const reviewButton = createMessageActionButton(
    "Revisar alterações",
    "Revisar alterações desta execução"
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
    hasReasoning: false,
    reasoningBlockCount: 0,
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
      "Resposta copiada"
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
    `Fontes (${safeCitations.length})`;

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
        `${assistant.activeReasoning ? "Thinking" : "Pensando…"} · `
        + formatElapsed(elapsedSince(assistant));
      assistant.lastClockUpdate = timestamp;
    }

    assistant.clockFrame = requestAnimationFrame(update);
  };
  assistant.clockFrame = requestAnimationFrame(update);
}

function renderModelSelection(assistant, model, origin) {
  const message = origin === "user"
    ? `Modelo ${model} selecionado pelo usuário.`
    : origin === "fallback"
      ? `Modelo ${model} selecionado como fallback pelo Host.`
      : `Modelo ${model} roteado pelo agente.`;
  assistant.modelNotice.textContent = message;
  assistant.modelNotice.hidden = false;
}

function appendAssistantReasoning(assistant, delta) {
  if (!delta) {
    return;
  }

  if (!assistant.activeReasoning) {
    const details = document.createElement("details");
    details.className = "assistant-reasoning";
    details.dataset.timelineKind = "thinking";
    details.dataset.block = String(++assistant.reasoningBlockCount);
    details.dataset.deltaCount = "0";
    details.open = true;
    const summary = document.createElement("summary");
    summary.textContent = "Thinking";
    summary.setAttribute("aria-label", "Raciocínio fornecido pelo modelo");
    const body = document.createElement("div");
    body.className = "assistant-reasoning-body";
    details.append(summary, body);
    assistant.workActivity.hidden = false;
    assistant.workActivity.append(details);
    assistant.activeReasoning = {
      details,
      body,
      raw: ""
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
    "write_file",
    "replace_text",
    "apply_patch",
    "delete_files",
    "create_directory"
  ]).has(action?.tool);
}

function actionDisplayLabel(tool) {
  return {
    create_file: "Criar",
    write_file: "Escrever",
    replace_text: "Editar",
    apply_patch: "Aplicar patch",
    delete_files: "Excluir",
    create_directory: "Criar pasta"
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
    proposed: "Preparando",
    approved: "Aprovada",
    executing: "Executando…",
    completed: "Concluída",
    failed: "Falhou",
    rejected: "Rejeitada",
    revised: "Revisada"
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
    `… ${omitted} linhas omitidas …`,
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
  assistant.workActivity.hidden = false;
  ensureWorkNarrative(
    assistant,
    "Vou executar as alterações solicitadas e mostrar apenas os arquivos afetados."
  );
  assistant.progress.hidden = false;
  assistant.progress.textContent =
    `Executando… · ${formatElapsed(elapsedSince(assistant))}`;
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
    link.setAttribute("aria-label", `Abrir revisão de ${relativePath}`);
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
    action.preview || action.resultOutput || "Sem conteúdo textual para exibir."
  );
  item.details.open = action.state === "failed";
}

function addToolsetRequest(assistant, streamEvent) {
  closeAssistantReasoning(assistant);
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

      if (streamEvent.type === "reasoning.delta") {
        appendAssistantReasoning(
          assistant,
          streamEvent.reasoningDelta ?? ""
        );
      } else if (streamEvent.type === "response.delta") {
        answer += streamEvent.delta ?? "";
        assistant.progress.hidden = true;
        renderAssistantAnswer(
          assistant,
          streamEvent.renderedHtml ?? "",
          answer
        );
      } else if (streamEvent.type === "response.completed") {
        completed = true;
        closeAssistantReasoning(assistant);
        renderAssistantSources(
          assistant,
          streamEvent.citations
        );
        assistant.answer.classList.remove("pending");
        renderAssistantAnswer(
          assistant,
          streamEvent.renderedHtml ?? "",
          answer
        );
        addActivity(assistant, streamEvent, false);
        finishActivity(
          assistant,
          `${assistant.recovered ? "Recuperado" : "Concluído"} · `
            + formatElapsed(streamEvent.elapsedMilliseconds),
          assistant.recovered
        );
        assistant.reviewButton.hidden =
          !assistant.executionSession?.reviewAvailable;
      } else if (streamEvent.type === "error") {
        closeAssistantReasoning(assistant);
        assistant.answer.classList.remove("pending");
        assistant.answer.classList.add("error");
        assistant.answer.textContent ||= `${streamEvent.error.message}\n`
          + `Referência: ${streamEvent.error.traceId}`;
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
          `Falhou · ${streamEvent.error.traceId}`,
          true
        );
        addTraceDiagnosticActions(assistant, streamEvent.error);
      } else if (streamEvent.type === "request.cancelled") {
        closeAssistantReasoning(assistant);
        addActivity(assistant, streamEvent, false);
        assistant.answer.classList.remove("pending");
        finishActivity(assistant, "Cancelado", false);
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
  const copy = createMessageActionButton("Copiar trace", "Copiar identificador de trace");
  const view = createMessageActionButton("Ver diagnostico", "Abrir diagnostico local sanitizado");
  view.disabled = error.diagnosticsPersisted !== true;
  if (view.disabled) {
    view.title = "O journal local nao confirmou a persistencia deste diagnostico.";
  }
  copy.addEventListener("click", () => copyText(error.traceId, copy, "Trace ID copiado"));
  view.addEventListener("click", () => openTraceDiagnostic(error.traceId));
  actions.append(copy, view);
  assistant.answer.insertAdjacentElement("afterend", actions);
}

async function openTraceDiagnostic(traceId) {
  elements.traceDiagnosticDialog.dataset.traceId = traceId;
  elements.traceDiagnosticId.textContent = traceId;
  elements.traceDiagnosticStatus.textContent = "Carregando diagnostico local...";
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
    ? `Diagnostico limitado a ${report.totalEvents} eventos seguros.`
    : `${report.totalEvents} eventos seguros correlacionados.`;
  const facts = [
    ["Estado", report.status],
    ["Codigo", report.failureCode ?? "nenhum"],
    ["Etapa", report.failureStage ?? "nenhuma"],
    ["Provedor / modelo", [report.provider, report.model].filter(Boolean).join(" / ") || "indisponivel"],
    ["Coordenador", report.coordinator ?? "indisponivel"],
    ["Caminho", report.executionPath ?? "indisponivel"],
    ["Resultado revisavel", report.reviewAvailable ? "sim" : "nao"],
    ["Recomendacao", report.recommendation]
  ];
  if (report.contextFit) {
    facts.push([
      "Contexto",
      `entrada ${report.contextFit.estimatedInputTokens ?? "?"} + reserva ${report.contextFit.reservedOutputTokens ?? "?"} = requerido ${report.contextFit.requiredContextTokens ?? "?"}; maximo ${report.contextFit.maximumContextTokens ?? "?"}`
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
    `${group.count} ${group.count === 1 ? "evento" : "eventos"}`;
  assistant.summary.textContent =
    `Detalhes técnicos · ${assistant.technicalEventCount} `
    + `${assistant.technicalEventCount === 1 ? "evento" : "eventos"}`;
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
      title: "Planejamento"
    };
  }

  if (type.includes("recovery")) {
    return {
      key: "recovery",
      title: "Recuperação"
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
      title: "Agentes e roteamento"
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
      title: "Workspace e projeto"
    };
  }

  if (type.startsWith("validation-")) {
    return {
      key: "validation",
      title: "Validação"
    };
  }

  if (
    type.startsWith("response.")
    || type.startsWith("request.")
    || type.startsWith("turn.")
  ) {
    return {
      key: "response",
      title: "Resposta"
    };
  }

  return {
    key: "execution",
    title: "Execução"
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
        ? `Estou trabalhando em: ${session.plan.objective}`
        : `Objetivo: ${session.plan.objective}`,
      true
    );
  }
  assistant.sessionHeader.hidden = false;
  assistant.sessionHeader.replaceChildren();
  const stateLabel = document.createElement("strong");
  stateLabel.textContent = session.state;
  const coordinator = document.createElement("span");
  coordinator.textContent =
    `Alvo: ${session.selectedModel || "indisponível"} · `
    + `Especialista: ${session.coordinatorModel} · `
    + `Roteador residente: ${session.residentModel || "indisponível"} · `
    + session.executionPath;
  coordinator.title = [
    session.conformanceIdentity
      ? `Conformidade: ${session.conformanceIdentity}`
      : null,
    session.handoffReason
      ? `Handoff: ${session.handoffReason}`
      : null
  ].filter(Boolean).join("\n");
  const counts = document.createElement("span");
  counts.textContent =
    `${session.actionCount} ações · ${session.changedFileCount} arquivos · `
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
    `Plano · ${plan.objective}`;
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
      status.title = `Depende de: ${step.dependencies.join(", ")}`;
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
    ? `Etapas ${plan.steps.length}/${plan.steps.length} · ${session.changedFileCount} arquivos alterados`
    : `Etapa ${displayedStep}/${plan.steps.length} · ${session.changedFileCount} arquivos alterados`;
  assistant.planBody.append(list, footer);
}

async function openChangeReview(executionSessionId, focusRelativePath = null) {
  if (!executionSessionId) {
    return;
  }

  elements.changeReviewBody.textContent = "Carregando revisão…";
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
    `${review.summary.state} · Alvo: ${review.summary.selectedModel || "indisponível"}`;
  const metadata = document.createElement("p");
  metadata.textContent =
    `Especialista: ${review.summary.coordinatorModel} · `
    + `Roteador residente: ${review.summary.residentModel || "indisponível"} · `
    + `${review.summary.executionPath} · ${review.summary.actionCount} ações · `
    + `${review.summary.changedFileCount} arquivos · `
    + `${formatElapsed(review.summary.elapsedMilliseconds)} · `
    + `${review.summary.completionStatus}`;
  const objective = document.createElement("p");
  objective.textContent = review.objective;
  summary.append(heading, metadata, objective);
  elements.changeReviewBody.append(summary);

  if (review.project) {
    const project = document.createElement("section");
    project.className = "change-review-context";
    const title = document.createElement("h3");
    title.textContent = "Projeto e baseline";
    const profile = document.createElement("p");
    profile.textContent =
      `${review.project.displayName} · `
      + `${review.project.projectTypes.join(", ") || "sem tipo detectado"} · `
      + `${review.baseline?.gitAvailable
        ? `Git ${review.baseline.branch ?? "detached"}`
        : "sem Git"}`;
    const dirty = document.createElement("p");
    dirty.textContent = review.baseline?.preExistingDirtyPaths.length
      ? `Alterações pré-existentes: ${review.baseline.preExistingDirtyPaths.join(", ")}`
      : "Nenhuma alteração pré-existente detectada.";
    const instructions = document.createElement("p");
    instructions.textContent = review.appliedInstructionFiles?.length
      ? `Instruções aplicadas: ${review.appliedInstructionFiles.join(", ")}`
      : "Nenhum AGENTS.md aplicado.";
    project.append(title, profile, dirty, instructions);
    elements.changeReviewBody.append(project);
  }

  if (review.summary.plan) {
    const plan = document.createElement("section");
    plan.className = "change-review-context";
    const title = document.createElement("h3");
    title.textContent =
      `Plano · ${review.summary.plan.completedStepCount}/${review.summary.plan.steps.length}`;
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
      `${file.operation === "created" ? "Criado" : "Modificado"} · ${file.relativePath}`;
    const status = document.createElement("p");
    status.className = file.verified
      ? "verification-ok"
      : "verification-warning";
    status.textContent = file.verified
      ? `Verificado · ${file.finalSizeBytes} bytes`
      : "A verificação de leitura falhou";
    section.append(title, status);

    if (file.preExistingChange) {
      const existing = document.createElement("p");
      existing.className = "preexisting-change";
      existing.textContent =
        "Este arquivo já possuía alterações antes da sessão e também foi alterado por ela.";
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
    heading.textContent = "Processos";
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
      `Validação · ${review.validation.state} · `
      + `${review.validation.profileName ?? "não configurada"}`;
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
      `Conflito em ${conflict.relativePath}: esperado ${conflict.expectedHash}, `
      + `atual ${conflict.currentHash}.`;
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
  showDeliveryApproval(operation);
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

function showDeliveryApproval(operation) {
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
    return;
  }

  state.pendingDeliveryAction = {
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
    confirmed: true
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
    "Desfazer integralmente as alterações desta sessão? O estado atual será validado antes de qualquer mudança.",
    { title: "Desfazer alterações?", confirmLabel: "Desfazer", danger: true }
  )) {
    return;
  }

  elements.undoExecution.disabled = true;
  elements.undoStatus.textContent = "Validando e desfazendo…";

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
      "Executar agora todas as etapas estruturadas do perfil de validação salvo?",
      { title: "Executar validação?", confirmLabel: "Executar" }
    );

  if (!confirmed) {
    return;
  }

  elements.validateChanges.disabled = true;
  elements.undoStatus.textContent = "Executando validação…";

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
      `Validação ${result.state}.`;
  } catch (error) {
    elements.undoStatus.textContent = error.message;
    elements.validateChanges.disabled = false;
  }
}

function addApprovalActivity(assistant, streamEvent) {
  const action = streamEvent.localAction;
  closeAssistantReasoning(assistant);
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
  status.textContent = "Aguardando decisão";
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
  reject.textContent = "Rejeitar";
  const approve = document.createElement("button");
  approve.className = "primary-button";
  approve.type = "button";
  approve.textContent = "Aprovar";
  controls.append(reject, approve);
  content.append(controls);
  row.append(summary, content);
  assistant.workActivity.hidden = false;
  ensureWorkNarrative(
    assistant,
    "Preciso da sua decisão para continuar esta alteração."
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
          ? "Aguardando decisão"
          : "Alteração será validada ao aprovar";
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
      `Editar comando de ${action.tool}`
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
    status.textContent = "Alteração validada";
  } else if (action.state === "approved") {
    status.textContent = "Aprovada";
  } else if (action.state === "executing") {
    status.textContent = "Executando…";
  } else if (action.state === "completed") {
    status.textContent = "Concluída";
    approval.dataset.decision = "completed";
    renderApprovalResponse(approval, action, false);
  } else if (action.state === "failed") {
    status.textContent = "Falhou";
    approval.dataset.decision = "failed";
    renderApprovalResponse(approval, action, true);
  } else if (action.state === "rejected") {
    status.textContent = "Rejeitada";
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
    ? "Execução · falhou"
    : "Execução · concluída";
  response.querySelector(".action-response-output").textContent =
    action.resultOutput || "Concluída sem saída textual.";
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
  status.textContent = approved ? "Aprovando…" : "Rejeitando…";

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

    status.textContent = approved ? "Aprovada" : "Rejeitada";
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
      ? "Alteração inválida"
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
  title.textContent = "Recuperação automática esgotada";
  const status = document.createElement("span");
  status.className = "approval-status";
  status.textContent = "Escolha uma alternativa";
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
    "A recuperação automática terminou; escolha como a tarefa deve continuar."
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
  status.textContent = "Aplicando decisão…";

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

function renderAssistantAnswer(assistant, renderedHtml, markdown) {
  assistant.rawAnswer = markdown;
  assistant.copyButton.disabled = !markdown;
  assistant.answer.innerHTML = renderedHtml;
  secureRenderedLinks(assistant.answer);
  enhanceCodeBlocks(
    assistant.answer,
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
        `Copiar código ${label.textContent}`
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
          "Código copiado"
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

  elements.sendButtonLabel.textContent = isStreaming
    ? "Cancelar"
    : state.editingTurn
      ? "Enviar edição"
      : "Enviar";
  elements.sendButton.querySelector(".send-icon").textContent = isStreaming
    ? "\u25a0"
    : "\u2191";
  elements.sendButton.setAttribute(
    "aria-label",
    isStreaming
      ? "Cancelar solicitação"
      : state.editingTurn
        ? "Enviar mensagem editada"
        : "Enviar mensagem"
  );
  elements.sendButton.title = elements.sendButton.getAttribute("aria-label");
  elements.sendButton.classList.toggle("cancel", isStreaming);
  elements.attachImage.disabled = isStreaming;
  elements.imageInput.disabled = isStreaming;
  elements.compactContext.disabled = isStreaming;
  elements.cancelMessageEdit.hidden = isStreaming || !state.editingTurn;
  elements.messages.querySelectorAll(".edit-message").forEach(
    button => {
      button.disabled = isStreaming;
    }
  );
  updateModelLockControls();
  updateComposerStatus();
  renderWebControl();
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
    elements.contextUsageSummary.textContent = "Contexto será calculado ao enviar";
    elements.contextUsage.dataset.accuracy = "pending";
    elements.contextUsage.dataset.warning = "";
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
  elements.contextUsageSummary.textContent =
    `Contexto ${formatCompactTokens(usage.inputTokens)} / `
    + `${formatCompactTokens(effectiveLimit)} · `
    + `${usage.accuracy === "exact" ? "exato" : "estimado"}`;
  elements.contextUsage.dataset.accuracy = usage.accuracy;
  elements.contextUsage.dataset.warning =
    usage.warningThreshold ? String(usage.warningThreshold) : "";
  elements.contextUsageWarning.hidden =
    !usage.warningThreshold && !usage.trimmed;
  elements.contextUsageWarning.textContent = [
    usage.warningThreshold
      ? `Atenção: contexto acima de ${usage.warningThreshold}% da capacidade útil.`
      : null,
    usage.trimmed
      ? "Blocos elegíveis foram omitidos somente do payload enviado."
      : null
  ].filter(Boolean).join(" ");
  elements.contextUsageDetails.replaceChildren(
    contextDetail("Inferência do especialista", usage.inferenceSequence || 1),
    contextDetail("Mensagens visíveis", usage.visibleMessages),
    contextDetail("Mensagens incluídas", usage.includedMessages),
    contextDetail("Mensagens omitidas", usage.omittedMessages),
    contextDetail(
      "Conversa e mensagem atual",
      `${formatInteger(usage.conversationTokens)} tokens estimados`
    ),
    contextDetail(
      "Sistema e instruções",
      `${formatInteger(usage.systemInstructionTokens)} tokens estimados`
    ),
    contextDetail(
      "Contexto do projeto",
      `${formatInteger(usage.projectContextTokens)} tokens estimados`
    ),
    contextDetail(
      "Toolset discovery",
      `${formatInteger(usage.toolDiscoveryTokens)} tokens estimados`
    ),
    contextDetail(
      "Schemas concedidos",
      `${formatInteger(usage.grantedToolSchemaTokens)} tokens estimados`
    ),
    contextDetail(
      "Estado/resultados do Host",
      `${formatInteger(usage.hostStateTokens)} tokens estimados`
    ),
    contextDetail(
      "Overhead estrutural",
      `${formatInteger(usage.structuralOverheadTokens)} tokens estimados`
    ),
    contextDetail(
      "Entrada total",
      `${formatInteger(usage.inputTokens)} tokens · ${usage.accuracy === "exact" ? "reportado" : "estimado"}`
    ),
    contextDetail(
      "Reserva de saída",
      `${formatInteger(usage.reservedResponseTokens)} tokens`
    ),
    contextDetail(
      "Contexto requerido",
      `${formatInteger(usage.requiredContextTokens)} tokens`
    ),
    contextDetail(
      "Limite efetivo",
      `${formatInteger(effectiveLimit)} tokens`
    ),
    contextDetail(
      "Origem da contagem",
      usage.accuracy === "exact"
        ? "usage reportado pelo provedor"
        : usage.estimator
    ),
    contextDetail(
      "Máximo do provedor",
      usage.providerMaximumTokens == null
        ? "não reportado"
        : `${formatInteger(usage.providerMaximumTokens)} tokens`
    ),
    contextDetail(
      "Limite de provedor configurado",
      `${formatInteger(usage.configuredProviderLimit)} tokens`
    ),
    contextDetail(
      "Limite da aplicação",
      `${formatInteger(usage.applicationLimit)} tokens`
    ),
    contextDetail("Blocos omitidos", usage.omittedBlocks || 0)
  );
  elements.compactContext.hidden = !usage.compactionEligible;
  elements.compactContext.disabled = Boolean(state.requestController);
  elements.compactContext.textContent = state.compactContextNextRequest
    ? "Compactação preparada"
    : "Compactar contexto";
}

async function requestManualContextCompaction() {
  const usage = state.contextUsage;
  if (!usage?.compactionEligible || state.requestController) {
    return;
  }
  const before = usage.beforeCompactionTokens ?? usage.inputTokens;
  const after = usage.afterCompactionTokens ?? usage.inputTokens;
  const confirmed = await showAppConfirm(
    "A compactação não apagará mensagens salvas nem alterará o chat visível. "
      + "Ela omitirá somente blocos elegíveis dos payloads das próximas inferências.\n\n"
      + `Estimativa atual: ${formatInteger(before)} tokens\n`
      + `Estimativa compactada: ${formatInteger(after)} tokens\n`
      + `Blocos elegíveis/omitidos: ${usage.omittedBlocks || 0}`,
    {
      title: "Compactar contexto enviado?",
      confirmLabel: "Compactar próxima solicitação"
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
    "pt-BR",
    {
      minimumFractionDigits: 1,
      maximumFractionDigits: 1
    }
  )}k`;
}

function updateComposerStatus() {
  if (state.requestController) {
    elements.composerStatus.textContent = "Resposta em andamento";
  } else if (state.conversationTransitioning) {
    elements.composerStatus.textContent = "Switching conversation safely";
  } else if (state.editingTurn) {
    elements.composerStatus.textContent = "Editando mensagem · Esc para cancelar";
  } else if (state.lockedModel) {
    elements.composerStatus.textContent = `Modelo fixado: ${state.lockedModel}`;
  } else if (state.interactionMode === "execute") {
    elements.composerStatus.textContent =
      `Execute · ${state.approvalPolicy === "ask" ? "pedir aprovação" : "aprovação automática"}`;
  } else if (state.attachments.length > 0 || state.webEnabled) {
    elements.composerStatus.textContent = [
      state.attachments.length > 0
        ? `${state.attachments.length} imagem${state.attachments.length === 1 ? "" : "ns"}`
        : null,
      state.webEnabled ? "Web habilitada" : null,
      "Enter para enviar"
    ].filter(Boolean).join(" · ");
  } else {
    elements.composerStatus.textContent = "Enter para enviar";
  }

  updateActiveAgentLabel();
  renderCapabilityContext();
}

function updateActiveAgentLabel() {
  if (!elements.activeAgentLabel) {
    return;
  }

  const selectedModel = state.activeAgentModel
    ?? state.lockedModel
    ?? elements.modelSelector.value;
  elements.activeAgentLabel.textContent =
    selectedModel && selectedModel !== "auto"
      ? selectedModel
      : "Auto (Roteador)";
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
    const error = new Error(payload?.message ?? `HTTP ${response.status}`);
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
