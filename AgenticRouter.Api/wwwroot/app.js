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
  approvalPolicy: "ask",
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
  usageOverview: null,
  pricingCatalog: null
};

const elements = {};
let resizeObserver;

document.addEventListener("DOMContentLoaded", initialize);

async function initialize() {
  bindElements();
  bindEvents();
  initializeScrollFollowing();

  try {
    await loadApplicationState();
  } catch (error) {
    elements.providerBadge.textContent = "Erro";
    elements.providerBadge.className = "badge error";
    elements.providerDetail.textContent = error.message;
  }

  await refreshRuntimeStatus();
  await ensureConversationIdentity();
  scheduleRuntimeRefresh();
  elements.messageInput.focus();
}

function bindElements() {
  for (const id of [
    "messages",
    "empty-state",
    "composer",
    "message-input",
    "model-selector",
    "model-lock",
    "send-button",
    "send-button-label",
    "cancel-message-edit",
    "active-agent-label",
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
    "usage-comparison-model",
    "usage-ollama-plan",
    "settings-usage-summary",
    "settings-usage-accuracy",
    "settings-usage-details",
    "purge-usage",
    "usage-purge-status",
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
    "recent-sessions",
    "archived-session-section",
    "archived-sessions",
    "history-new-conversation",
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
    "save-git-user-name",
    "save-git-user-email",
    "git-remotes",
    "git-open-review",
    "git-action-status",
    "new-conversation-dialog",
    "new-conversation-enable-history",
    "new-conversation-discard",
    "new-conversation-cancel"
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
  elements.settingsForm.addEventListener("submit", saveSettings);
  elements.messages.addEventListener("scroll", handleConversationScroll);
  elements.jumpLatest.addEventListener("click", resumeAutoFollow);
  elements.newConversation.addEventListener("click", requestNewConversation);
  elements.historyNewConversation.addEventListener("click", requestNewConversation);
  elements.modelSelector.addEventListener("change", handleModelSelectionChange);
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
  elements.purgeUsage.addEventListener(
    "click",
    purgeUsageHistory
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
  elements.saveGitUserName.addEventListener(
    "click",
    () => saveGitIdentity("user.name")
  );
  elements.saveGitUserEmail.addEventListener(
    "click",
    () => saveGitIdentity("user.email")
  );
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
  document.querySelectorAll(".mode-option").forEach(
    button => button.addEventListener("click", handleModeChange)
  );
  document.addEventListener("visibilitychange", handleVisibilityChange);
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
    pricingCatalog
  ] = await Promise.all([
    fetchJson("/api/settings"),
    fetchJson("/api/models"),
    fetchJson("/api/devices"),
    fetchJson("/api/workspace"),
    fetchJson("/api/workspace/project-profile"),
    fetchJson("/api/workspace/validation-profile"),
    fetchJson("/api/workspaces"),
    fetchJson("/api/usage/overview"),
    fetchJson("/api/usage/pricing")
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
  updateProviderStatus(modelsResponse);
  updateDeviceStatus(devicesResponse);
  renderComposerModels();
  renderSettings();
  renderWorkspace();
  renderWorkspaceProfiles();
  renderProjectProfile();
  renderValidationProfile();
  updateInteractionControls();
  await refreshSessions();
  await refreshGit();
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
  elements.saveGitUserName.disabled = !available;
  elements.saveGitUserEmail.disabled = !available;
  elements.gitUserName.value = git?.userName?.value ?? "";
  elements.gitUserEmail.value = git?.userEmail?.value ?? "";
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
  elements.gitOpenReview.disabled = !state.latestExecutionSessionId;
}

async function initializeGitRepository() {
  if (state.interactionMode !== "execute") {
    elements.gitActionStatus.textContent =
      "Switch to Execute mode before initializing Git.";
    return;
  }
  const facts = "Initialize Git repository at the trusted-workspace root.\n"
    + "Initial branch: main\nNo commit, staging, remote, or project file will be created.";
  if (!window.confirm(facts)) {
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

async function saveGitIdentity(field) {
  if (state.interactionMode !== "execute") {
    elements.gitActionStatus.textContent =
      "Switch to Execute mode before changing repository identity.";
    return;
  }
  const input = field === "user.name"
    ? elements.gitUserName
    : elements.gitUserEmail;
  const value = input.value.trim();

  try {
    const preview = await fetchJson(
      "/api/git/identity/preview",
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          field,
          value
        })
      }
    );
    if (!window.confirm(
      `Write repository-local ${field} = "${preview.value}"?\n`
      + "Global Git configuration will not be changed."
    )) {
      return;
    }
    state.git = await fetchJson(
      "/api/git/identity",
      {
        method: "PUT",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          browserSessionId: state.browserSessionId,
          interactionMode: state.interactionMode,
          actionId: preview.actionId,
          confirmed: true,
          field,
          value: preview.value
        })
      }
    );
    renderGitCard();
    renderGitPanel();
    renderSettingsSummaries();
    elements.gitActionStatus.textContent =
      `${field} saved in repository-local configuration.`;
  } catch (error) {
    elements.gitActionStatus.textContent =
      `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
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
  const name = window.prompt("Novo nome do workspace:", profile.name)?.trim();

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
  if (!window.confirm(
    `Remover "${profile.name}" e seu histórico local do Agentic Router? `
      + "A pasta real e os arquivos do projeto não serão excluídos."
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
    && !window.confirm(
      "Ativar histórico local para este workspace? O conteúdo não será criptografado "
        + "pelo Agentic Router v0.9.2."
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
  clearConversationUi();
  state.browserSessionId = createSessionId();
  state.conversationSessionId = null;
  state.latestExecutionSessionId = null;
  state.interactionMode = "chat";
  state.approvalPolicy = "ask";
  state.lockedModel = null;
  elements.modelSelector.value = "auto";
  elements.modelLock.checked = false;
  updateInteractionControls();
  updateModelLockControls();
  await ensureConversationIdentity();
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
    await refreshWorkspaceState();
    elements.workspaceSaveStatus.textContent = "Workspace adicionado e ativado";
    hideNewWorkspaceForm();
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
  renderSettingsSummaries();
}

function createSessionEntry(session) {
  const entry = document.createElement("article");
  const current = state.conversationSessionId === session.id;
  entry.className = `session-entry${current ? " current" : ""}`;
  entry.dataset.sessionId = session.id;
  entry.setAttribute(
    "aria-current",
    current ? "true" : "false"
  );
  const title = document.createElement("strong");
  title.textContent = session.title;
  const metadata = document.createElement("small");
  metadata.textContent =
    `${new Date(session.updatedAt).toLocaleString()} · ${session.lastInteractionMode}`;
  const status = document.createElement("small");
  status.className = session.interrupted ? "session-interrupted" : "";
  status.textContent = [
    current ? "atual" : null,
    session.interrupted ? "interrompida" : null,
    session.archived ? "arquivada" : null
  ].filter(Boolean).join(" · ");
  status.hidden = !status.textContent;
  const actions = document.createElement("div");
  actions.className = "session-entry-actions";
  const resume = document.createElement("button");
  resume.type = "button";
  resume.className = "secondary-button";
  resume.textContent = "Retomar";
  resume.addEventListener("click", () => resumeSession(session.id));
  const rename = document.createElement("button");
  rename.type = "button";
  rename.className = "secondary-button";
  rename.textContent = "Renomear";
  rename.addEventListener("click", () => renameSession(session));
  const archive = document.createElement("button");
  archive.type = "button";
  archive.className = "secondary-button";
  archive.textContent = "Arquivar";
  archive.hidden = session.archived;
  archive.addEventListener("click", () => archiveSession(session.id));
  const exportButton = document.createElement("a");
  exportButton.className = "secondary-button";
  exportButton.textContent = "Exportar";
  exportButton.href = `/api/sessions/${encodeURIComponent(session.id)}/export`;
  exportButton.download = "";
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "secondary-button danger-button";
  remove.textContent = "Excluir";
  remove.addEventListener("click", () => deleteSession(session));
  actions.append(resume, rename, archive, exportButton, remove);
  entry.append(title, metadata, status, actions);
  return entry;
}

async function resumeSession(id) {
  await requestConversationTransition(
    async () =>
    {
      if (!window.confirm(
        "Retomar esta conversa? Modo Chat, aprovação manual e modelo não fixado serão restaurados."
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
        state.approvalPolicy = "ask";
        state.lockedModel = null;
        elements.modelSelector.value = session.selectedModel
          && state.models.some(model => model.name === session.selectedModel)
          ? session.selectedModel
          : "auto";
        renderRestoredConversation(session);
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
  const title = window.prompt("Novo título:", session.title)?.trim();

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

async function deleteSession(session) {
  if (!window.confirm(
    `Excluir somente o registro local "${session.title}"? Os arquivos do projeto serão preservados.`
  )) {
    return;
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
}

async function deleteArchivedSessions() {
  if (!window.confirm(
    "Excluir todas as conversas arquivadas deste workspace?"
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
  if (!window.confirm(
    "Excluir todo o histórico local deste workspace? Os arquivos do projeto serão preservados."
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
  if (!window.confirm(
    "Excluir todo o histórico local de uso de tokens? Esta ação não altera conversas nem arquivos do projeto."
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
    state.usageOverview = await fetchJson(`/api/usage/overview${query}`);
    renderUsageSummary();
  } catch (error) {
    elements.runtimeUsageAccuracy.textContent = "indisponível";
    elements.settingsUsageAccuracy.textContent = "indisponível";
    elements.runtimeUsageDetails.textContent =
      `Uso indisponível · ${error.message}`;
    elements.settingsUsageDetails.textContent =
      `Uso indisponível · ${error.message}`;
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
  elements.runtimeSummary.textContent = compact.join(" · ");
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
    `Total ${formatGiB(model.totalSizeBytes)} · VRAM ${formatGiB(model.vramSizeBytes)} · `
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
  updateModelLockControls();
}

function renderSettings() {
  if (!state.settings) {
    return;
  }

  elements.ollamaUrl.value = state.settings.ollamaUrl;
  replaceOptions(elements.routerModel, modelOptions(), state.settings.routerModel);
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
  elements.usageSelectedWindow.value = state.settings.usage.selectedWindow;
  elements.usageRetentionDays.value = state.settings.usage.retentionDays;
  elements.usageProviderShortMinutes.value =
    state.settings.usage.providerShortWindowMinutes;
  elements.usageProviderLongMinutes.value =
    state.settings.usage.providerLongWindowMinutes;
  elements.usageCustomMinutes.value =
    state.settings.usage.customRollingWindowMinutes;
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
  return state.models.map(model => ({
    value: model.name,
    label: model.name,
    group: "Instalados"
  }));
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
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderModelDiagnostics();
  } catch (error) {
    elements.modelContextDiagnostic.textContent = error.message;
  }

  await loadPortableYaml();
}

function closeSettings() {
  if (
    state.settingsDirty
    && !window.confirm(
      "Discard unsaved settings changes?"
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
      comparisonProvider: elements.usageComparisonModel.value.split("|")[0],
      comparisonModel: elements.usageComparisonModel.value.split("|")[1],
      ollamaPlanReference: elements.usageOllamaPlan.value
    }
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
    elements.settingsErrors.textContent = errors
      ? Object.entries(errors)
        .flatMap(([field, messages]) => messages.map(message => `${field}: ${message}`))
        .join("\n")
      : error.message;
    elements.settingsErrors.hidden = false;
    elements.saveStatus.textContent = "";
    navigateToSettingsError(
      Object.keys(errors ?? {})[0]
    );
  }
}

function handleSettingsInput(event) {
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
}

function updateSettingsDirtyState() {
  elements.settingsDirty.textContent = state.settingsDirty
    ? "Unsaved changes"
    : "Sem alterações";
  elements.settingsDirty.className =
    `badge ${state.settingsDirty ? "error" : "muted"}`;
  elements.saveSettings.disabled = !state.settingsDirty;
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

async function importPortableYaml() {
  const yaml = elements.settingsYaml.value;

  if (!yaml.trim()) {
    elements.settingsYamlStatus.textContent = "Informe uma configuração YAML.";
    elements.settingsYamlStatus.className = "portable-yaml-status error";
    return;
  }

  if (
    state.settingsDirty
    && !window.confirm(
      "A importação substituirá as alterações ainda não salvas deste formulário. Continuar?"
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
  elements.settingsErrors.focus();
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

function handleModelSelectionChange() {
  if (elements.modelSelector.value === "auto") {
    state.lockedModel = null;
    elements.modelLock.checked = false;
  }

  updateModelLockControls();
  updateInteractionControls();
  updateComposerStatus();
}

function handleModeChange(event) {
  if (state.requestController) {
    return;
  }

  state.interactionMode = event.currentTarget.dataset.mode;
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
      if (!await saveCurrentConversation()) {
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

  try {
    const identity = await fetchJson(
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
    clearConversationUi();
    state.browserSessionId = nextBrowserSessionId;
    state.conversationSessionId = identity.sessionId;
    state.conversationState = "completed";
    state.latestExecutionSessionId = null;
    setPersistenceStatus(identity.status);
    await refreshSessions();
    await refreshGit();
  } catch (error) {
    setPersistenceStatus("Save failed");
    elements.composerStatus.textContent =
      `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
  }
}

function clearConversationUi() {
  state.conversationVersion++;
  state.history = [];
  state.editingTurn = null;
  state.lockedModel = null;
  state.interactionMode = "chat";
  state.approvalPolicy = "ask";
  state.activeAgentModel = null;
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
    historyIndex
  );
  const conversationVersion = state.conversationVersion;
  const selectedModel = state.lockedModel ?? elements.modelSelector.value;
  const controller = new AbortController();
  const assistant = appendAssistantMessage();
  state.activeAssistant = assistant;
  elements.messageInput.value = "";
  resizeComposer();
  state.requestController = controller;
  setStreamingState(true);
  requestAnimationFrame(scrollToBottom);
  await refreshRuntimeStatus();
  scheduleRuntimeRefresh();

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
          conversationSessionId: state.conversationSessionId
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

function appendUserMessage(message, historyIndex) {
  const element = document.createElement("article");
  element.className = "message user";
  const content = document.createElement("div");
  content.className = "message-content";
  content.textContent = message;
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

function appendAssistantMessage() {
  const container = document.createElement("article");
  container.className = "message assistant";

  const details = document.createElement("details");
  details.className = "activity";
  details.open = true;
  const summary = document.createElement("summary");
  summary.textContent = "Em andamento · 0 ms";
  summary.setAttribute("aria-label", "Atividade da solicitação");
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
  container.append(details, planPanel, answer, actions);
  elements.messages.append(container);

  const assistant = {
    container,
    answer,
    details,
    summary,
    activityList,
    sessionHeader,
    planPanel,
    planSummary,
    planBody,
    startedAt: performance.now(),
    clockFrame: null,
    lastClockUpdate: 0,
    recovered: false,
    rawAnswer: "",
    copyButton,
    reviewButton,
    executionSession: null,
    lastActivityGroup: null,
    lastActivityGroupKey: null
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

function startElapsedClock(assistant) {
  const update = timestamp => {
    if (timestamp - assistant.lastClockUpdate >= 250) {
      assistant.summary.textContent =
        `Em andamento · ${formatElapsed(elapsedSince(assistant))}`;
      assistant.lastClockUpdate = timestamp;
    }

    assistant.clockFrame = requestAnimationFrame(update);
  };
  assistant.clockFrame = requestAnimationFrame(update);
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

      if (
        streamEvent.type === "target.model-resolved"
        && streamEvent.selectedModel
      ) {
        state.activeAgentModel = streamEvent.selectedModel;
        updateActiveAgentLabel();
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

      if (streamEvent.type === "response.delta") {
        answer += streamEvent.delta ?? "";
        renderAssistantAnswer(
          assistant,
          streamEvent.renderedHtml ?? "",
          answer
        );
      } else if (streamEvent.type === "response.completed") {
        completed = true;
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
      } else if (streamEvent.type === "request.cancelled") {
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
        streamEvent.type === "action.recovery-decision-required"
        && streamEvent.recoveryDecision
      ) {
        addRecoveryDecisionActivity(
          assistant,
          streamEvent
        );
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
  group.countLabel.textContent =
    `${group.count} ${group.count === 1 ? "evento" : "eventos"}`;
}

function ensureActivityGroup(assistant, streamEvent, isWarningOrError) {
  const definition = activityGroupFor(
    streamEvent
  );

  if (
    assistant.lastActivityGroup
    && assistant.lastActivityGroupKey === definition.key
  ) {
    if (isWarningOrError) {
      assistant.lastActivityGroup.details.classList.add("warning");
    }

    return assistant.lastActivityGroup;
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
  assistant.lastActivityGroup = group;
  assistant.lastActivityGroupKey = definition.key;
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
  assistant.sessionHeader.hidden = false;
  assistant.sessionHeader.replaceChildren();
  const stateLabel = document.createElement("strong");
  stateLabel.textContent = session.state;
  const coordinator = document.createElement("span");
  coordinator.textContent =
    `${session.coordinatorModel} · ${session.executionPath}`;
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
    session.plan
  );

  if (session.reviewAvailable && session.state !== "running") {
    assistant.reviewButton.hidden = false;
  }
}

function renderExecutionPlan(assistant, plan) {
  if (!plan) {
    assistant.planPanel.hidden = true;
    return;
  }

  assistant.planPanel.hidden = false;
  assistant.planSummary.textContent =
    `${plan.completedStepCount}/${plan.steps.length} · ${plan.objective}`;
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
    item.append(marker, title, status);
    list.append(item);
  }

  assistant.planBody.append(list);
}

async function openChangeReview(executionSessionId) {
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
    renderChangeReview(review);
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

function renderChangeReview(review) {
  elements.changeReviewBody.replaceChildren();
  const summary = document.createElement("section");
  summary.className = "change-review-summary";
  const heading = document.createElement("h3");
  heading.textContent = `${review.summary.state} · ${review.summary.coordinatorModel}`;
  const metadata = document.createElement("p");
  metadata.textContent =
    `${review.summary.executionPath} · ${review.summary.actionCount} ações · `
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

  for (const file of review.files) {
    const section = document.createElement("details");
    section.className = "change-file-review";
    section.open = true;
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

  if (!window.confirm(
    "Desfazer integralmente as alterações desta sessão? O estado atual será validado antes de qualquer mudança."
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
    || window.confirm(
      "Executar agora todas as etapas estruturadas do perfil de validação salvo?"
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

  if (action.preview) {
    const preview = document.createElement("pre");
    preview.className = "action-preview";
    preview.textContent = action.preview;
    content.append(preview);
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
  assistant.activityList.append(row);
  assistant.details.open = true;
  approve.addEventListener(
    "click",
    () => decideAction(
      action.actionId,
      action.executionSessionId,
      true,
      approve,
      reject,
      status,
      row
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
      row
    )
  );
}

async function decideAction(
  actionId,
  executionSessionId,
  approved,
  approveButton,
  rejectButton,
  status,
  approval
) {
  approveButton.disabled = true;
  rejectButton.disabled = true;
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
          executionSessionId
        })
      }
    );
    status.textContent = approved ? "Aprovada" : "Rejeitada";
    approval.dataset.decision = approved
      ? "approved"
      : "rejected";
    approval.open = false;
  } catch (error) {
    status.textContent = error.message;
    approveButton.disabled = false;
    rejectButton.disabled = false;
  }
}

function addRecoveryDecisionActivity(assistant, streamEvent) {
  const recovery = streamEvent.recoveryDecision;
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
  assistant.activityList.append(row);
  assistant.details.open = true;
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
  assistant.summary.textContent = summary;
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
  }

  elements.sendButtonLabel.textContent = isStreaming
    ? "Cancelar"
    : state.editingTurn
      ? "Enviar edição"
      : "Enviar";
  elements.sendButton.setAttribute(
    "aria-label",
    isStreaming
      ? "Cancelar solicitação"
      : state.editingTurn
        ? "Enviar mensagem editada"
        : "Enviar mensagem"
  );
  elements.sendButton.classList.toggle("cancel", isStreaming);
  elements.cancelMessageEdit.hidden = isStreaming || !state.editingTurn;
  elements.messages.querySelectorAll(".edit-message").forEach(
    button => {
      button.disabled = isStreaming;
    }
  );
  updateModelLockControls();
  updateComposerStatus();
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
  } else {
    elements.composerStatus.textContent = "Enter para enviar";
  }

  updateActiveAgentLabel();
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
