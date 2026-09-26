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
  refreshSelectedModelCapabilities();
}

function handleApprovalPolicyChange() {
  state.approvalPolicy = elements.approvalPolicy.value;
  updateComposerStatus();
}

const sendStrategyLabels = {
  auto: { label: "Auto", indicator: "A" },
  direct: { label: "Direct", indicator: "D" },
  supervised: { label: "Supervisor", indicator: "S" },
  autonomous: { label: "Autonomous", indicator: "∞" }
};

function renderSendStrategy() {
  const selected = sendStrategyLabels[state.executionStrategy]
    ?? sendStrategyLabels.auto;
  elements.sendStrategyIndicator.textContent = selected.indicator;
  elements.sendStrategyToggle.setAttribute(
    "aria-label",
    `Choose Execute send strategy. Current: ${selected.label}`
  );
  elements.sendStrategyToggle.title = `Execute strategy · ${selected.label}`;
  elements.sendButton.setAttribute(
    "aria-label",
    state.interactionMode === "execute"
      ? `Send message using ${selected.label} execution strategy`
      : "Send message"
  );
  elements.sendButton.title = state.interactionMode === "execute"
    ? `Send message · ${selected.label}`
    : "Send message";
  elements.sendStrategyMenu.querySelectorAll("[data-send-strategy]").forEach(
    item => item.setAttribute(
      "aria-checked",
      String(item.dataset.sendStrategy === state.executionStrategy)
    )
  );
}

function setSendStrategyMenu(open, focusFirst = false) {
  elements.sendStrategyMenu.hidden = !open;
  elements.sendStrategyToggle.setAttribute("aria-expanded", String(open));
  if (open && focusFirst) {
    elements.sendStrategyMenu.querySelector(
      `[data-send-strategy="${state.executionStrategy}"]`
    )?.focus();
  }
}

function toggleSendStrategyMenu() {
  setSendStrategyMenu(elements.sendStrategyMenu.hidden, true);
}

function selectSendStrategy(event) {
  const item = event.target.closest("[data-send-strategy]");
  if (!item) {
    return;
  }
  state.executionStrategy = item.dataset.sendStrategy;
  updateInteractionControls();
  setSendStrategyMenu(false);
  elements.sendButton.focus();
}

function closeSendStrategyMenuFromOutside(event) {
  if (!elements.sendStrategyControl.contains(event.target)) {
    setSendStrategyMenu(false);
  }
}

function handleSendStrategyKeyDown(event) {
  if (event.key === "Escape" && !elements.sendStrategyMenu.hidden) {
    event.preventDefault();
    setSendStrategyMenu(false);
    elements.sendStrategyToggle.focus();
    return;
  }
  if (elements.sendStrategyMenu.hidden || !["ArrowDown", "ArrowUp"].includes(event.key)) {
    return;
  }
  const items = Array.from(
    elements.sendStrategyMenu.querySelectorAll("[data-send-strategy]")
  );
  const current = items.indexOf(document.activeElement);
  const direction = event.key === "ArrowDown" ? 1 : -1;
  const next = current < 0
    ? 0
    : (current + direction + items.length) % items.length;
  event.preventDefault();
  items[next].focus();
}

function updateInteractionControls() {
  const isStreaming = Boolean(state.requestController);
  const disabled = isStreaming
    || state.conversationTransitioning
    || state.readOnlyConversation;
  const onboardingBlocked = setupOnboardingBlocksConversation();
  document.querySelectorAll(".mode-option").forEach(
    button => {
      const active = button.dataset.mode === state.interactionMode;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
      button.disabled = disabled;
    }
  );
  const autonomous = state.interactionMode === "execute"
    && state.executionStrategy === "autonomous";
  elements.approvalPolicy.value = state.approvalPolicy;
  elements.approvalPolicy.disabled =
    disabled
    || onboardingBlocked
    || state.interactionMode !== "execute"
    || autonomous;
  elements.approvalPolicy.title = autonomous
    ? "Autonomous supervision approves every action the user could permit; hard Host boundaries remain enforced."
    : "";
  elements.messageInput.disabled =
    state.conversationTransitioning
    || onboardingBlocked
    || state.readOnlyConversation;
  elements.sendButton.disabled =
    state.conversationTransitioning
    || onboardingBlocked
    || state.readOnlyConversation;
  const executeStrategyAvailable = state.interactionMode === "execute";
  elements.sendStrategyControl.classList.toggle(
    "strategy-visible",
    executeStrategyAvailable
  );
  elements.sendStrategyToggle.hidden = !executeStrategyAvailable;
  elements.sendStrategyToggle.disabled = disabled || onboardingBlocked;
  if (!executeStrategyAvailable || elements.sendStrategyToggle.disabled) {
    setSendStrategyMenu(false);
  }
  renderSendStrategy();
  updateImageAttachmentControls();
  elements.composer.classList.toggle(
    "execute-mode",
    state.interactionMode === "execute"
  );
}

function updateImageAttachmentControls() {
  const status = state.harnesses?.find(
    item => item.definition.id === state.harness
  );
  const harnessSupportsImages = state.harness === "auto-model-harness"
    || status?.definition?.capabilities?.supportsImages !== false;
  const capabilities = state.modelCapability?.capabilities;
  let disabled = true;
  let explanation;

  if (state.requestController) {
    explanation =
      "Image attachment is disabled while a response is in progress. Wait for it to finish or cancel it.";
  } else if (state.conversationTransitioning) {
    explanation = "Image attachment is disabled while the conversation is changing.";
  } else if (state.readOnlyConversation) {
    explanation = "Image attachment is disabled in a read-only conversation.";
  } else if (setupOnboardingBlocksConversation()) {
    explanation = "Complete local setup before attaching images.";
  } else if (!harnessSupportsImages) {
    explanation = "The selected harness does not support image attachments.";
  } else if (!capabilities) {
    explanation = "Image support is unavailable until model capabilities are confirmed.";
  } else if (!capabilities.vision) {
    explanation = "The selected model does not accept image input.";
  } else {
    disabled = false;
    explanation = `Attach up to ${capabilities.maximumImageCount} images `
      + `(JPEG, PNG, WebP, or GIF; ${formatBytes(capabilities.maximumImageBytes)} per image).`;
  }

  elements.attachImage.disabled = disabled;
  elements.imageInput.disabled = disabled;
  elements.attachImage.title = explanation;
  elements.attachImage.setAttribute(
    "aria-label",
    disabled ? `Attach image unavailable. ${explanation}` : explanation
  );
}

function handleHarnessChange() {
  state.harness = elements.harnessSelector.value;
  updateHarnessControls();
  updateImageAttachmentControls();
  updateComposerStatus();
  refreshSelectedModelCapabilities();
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
  ) || state.conversationTransitioning || state.readOnlyConversation;
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
  if (state.readOnlyConversation) {
    return false;
  }
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
          preservedMessageCount: state.persistedMessageCount ?? 0,
          messages: state.history.slice(state.persistedMessageCount ?? 0).map(
            message => ({
              role: message.role,
              content: message.content,
              createdAt: message.createdAt,
              diagnostic: message.diagnostic,
              hidden: message.hidden,
              contentBlocks: message.contentBlocks
            })
          ),
          interactionMode: state.interactionMode,
          selectedModel: elements.modelSelector.value,
          state: state.conversationState,
          approvalPolicy: state.approvalPolicy,
          harness: state.harness,
          executionStrategy: state.executionStrategy
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
  persistBrowserSessionId(state.browserSessionId);
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
  state.latestSavedExecutionReview = null;
  state.conversationVersion++;
  state.readOnlyConversation = false;
  state.history = [];
  state.persistedContext = null;
  state.persistedMessageCount = 0;
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
  state.setupOnboardingDismissed = false;
  state.pendingDiagnosticInvestigation = null;
  state.webEnabled = false;
  state.webControlState = "unavailable";
  state.cloudImageApprovals.clear();
  clearAttachments();
  state.autoFollow = true;
  elements.modelSelector.value = "auto";
  elements.harnessSelector.value = "native";
  elements.messageInput.value = "";
  elements.messageInput.readOnly = false;
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
  elements.projectList?.querySelectorAll(".project-new-chat-button").forEach(
    button => {
      button.disabled = isTransitioning
        || button.closest(".project-accordion")?.classList.contains("unavailable");
    }
  );
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
    const {
      setup,
      modelsResponse,
      providerHealth
    } = await loadProviderBootstrapState();
    state.setup = setup;
    state.harnesses = setup.harnesses.map(harness => ({
      definition: harness.definition,
      availability: harness.availability
    }));
    state.devices = setup.devices;
    state.models = modelsResponse.models;
    state.providerHealth = providerHealth;
    updateProviderStatus(setup.ollama);
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
  renderExternalAppWarnings();
  const emptyState = document.querySelector("#empty-state");
  const containers = document.querySelectorAll("[data-setup-surface]");
  if (containers.length === 0 || !state.setup) {
    return;
  }

  const setup = state.setup;
  const displayOnboarding = shouldDisplaySetupOnboarding();
  if (emptyState) {
    emptyState.hidden = setup.coreReady && !displayOnboarding;
    emptyState.classList.toggle("has-setup", displayOnboarding);
  }
  containers.forEach(container => {
    const onboarding = container.dataset.setupSurface === "onboarding";
    if (onboarding && !displayOnboarding) {
      container.replaceChildren();
      container.hidden = true;
      return;
    }
    renderSetupSurface(container, setup);
  });
  scheduleSetupRefresh(setup);
  updateInteractionControls();
}

function shouldDisplaySetupOnboarding() {
  if (!state.setup) {
    return false;
  }
  if (!state.setup.coreReady) {
    return true;
  }
  if (hasMeaningfulConversation()) {
    return false;
  }
  return state.settings?.onboarding?.showBeforeNewConversation !== false
    && !state.setupOnboardingDismissed;
}

function setupOnboardingBlocksConversation() {
  return shouldDisplaySetupOnboarding();
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
      createSetupOllamaRows(setup)
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
  if (
    container.dataset.setupSurface === "onboarding"
    && setup.coreReady
  ) {
    appendSetupOnboardingActions(container);
  }
}

function appendSetupOnboardingActions(container) {
  const actions = document.createElement("footer");
  actions.className = "setup-onboarding-actions";
  const preference = document.createElement("label");
  const checkbox = document.createElement("input");
  checkbox.type = "checkbox";
  checkbox.dataset.setupHideOnboarding = "true";
  const label = document.createElement("span");
  label.textContent = t("setup.hide_before_conversations");
  preference.append(checkbox, label);
  const button = document.createElement("button");
  button.type = "button";
  button.className = "primary-button";
  button.dataset.setupAction = "continue";
  button.textContent = t("setup.continue");
  actions.append(preference, button);
  container.append(actions);
}

function createSetupOllamaRows(setup) {
  const resourceRow = createSetupResourceRow(setup.ollama, "install");
  const installation = setup.ollamaInstallation;
  const rows = [];
  if (installation?.backend) {
    const backendRow = document.createElement("div");
    backendRow.className = "setup-profile-row setup-backend-row";
    const title = document.createElement("strong");
    title.textContent = t("setup.backend_evidence");
    const state = document.createElement("span");
    const observed = installation.backend.observedBackend
      || t("setup.backend_not_observed");
    state.textContent = `${installation.backend.state} · ${observed}`;
    const evidence = document.createElement("small");
    evidence.textContent = installation.backend.evidence;
    backendRow.append(title, state, evidence);
    rows.push(backendRow);
  }
  const currentProfile = installation?.backend?.manifestProfile;
  const switchProfiles = installation?.profiles?.filter(
    profile => profile.id !== currentProfile
  ) ?? [];
  if (setup.ollama.available && currentProfile && switchProfiles.length > 0) {
    const switchRow = document.createElement("div");
    switchRow.className = "setup-profile-row";
    const label = document.createElement("label");
    label.htmlFor = `setup-profile-switch-${installation.platform}`;
    label.textContent = t("setup.change_profile");
    const select = document.createElement("select");
    select.id = label.htmlFor;
    select.className = "setup-profile-select";
    select.dataset.setupProfileSwitch = "ollama";
    switchProfiles.forEach(profile => {
      const option = document.createElement("option");
      option.value = profile.id;
      option.textContent = profile.displayName;
      select.append(option);
    });
    const detail = document.createElement("small");
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button compact setup-action";
    button.dataset.setupAction = "switch-profile";
    button.disabled = setup.readOnly;
    button.textContent = t("setup.review_change");
    const renderSwitchDetail = () => {
      detail.textContent = switchProfiles.find(
        profile => profile.id === select.value
      )?.description ?? "";
    };
    select.addEventListener("change", renderSwitchDetail);
    switchRow.append(label, select, detail, button);
    renderSwitchDetail();
    rows.push(switchRow);
  }
  if (!installation?.profiles?.length || setup.ollama.available) {
    rows.push(resourceRow);
    return rows;
  }

  const profileRow = document.createElement("div");
  profileRow.className = "setup-profile-row";
  const label = document.createElement("label");
  label.htmlFor = `setup-profile-${installation.platform}`;
  label.textContent = t("setup.acceleration_profile");
  const select = document.createElement("select");
  select.id = label.htmlFor;
  select.className = "setup-profile-select";
  select.dataset.setupProfile = "ollama";
  if (installation.profiles.length > 1) {
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = t("setup.select_profile");
    select.append(placeholder);
  }
  installation.profiles.forEach(profile => {
    const option = document.createElement("option");
    option.value = profile.id;
    option.textContent = profile.displayName;
    option.selected = profile.id === installation.requestedProfile
      || installation.profiles.length === 1;
    select.append(option);
  });
  const detail = document.createElement("small");
  const renderDetail = () => {
    const selected = installation.profiles.find(
      profile => profile.id === select.value
    );
    detail.textContent = selected?.description
      || installation.diagnostic
      || t("setup.profile_required");
    const installButton = resourceRow.querySelector(
      "button[data-setup-action=\"install\"]"
    );
    if (installButton) {
      const recentlyStarted = setup.ollama.job?.state === "started"
        && Date.now() - new Date(setup.ollama.job.updatedAt).getTime() < 10 * 60 * 1000;
      installButton.disabled = setup.readOnly || recentlyStarted || !select.value;
    }
  };
  select.addEventListener("change", renderDetail);
  profileRow.append(label, select, detail);
  if (installation.diagnostic) {
    const diagnostic = document.createElement("small");
    diagnostic.className = "setup-profile-diagnostic";
    diagnostic.textContent = installation.diagnostic;
    profileRow.append(diagnostic);
  }
  renderDetail();
  rows.push(profileRow, resourceRow);
  return rows;
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
    if (action === "continue") {
      const hideBeforeConversations = Boolean(
        button.closest("[data-setup-surface]")?.querySelector(
          "[data-setup-hide-onboarding]"
        )?.checked
      );
      if (hideBeforeConversations) {
        state.settings = await fetchJson(
          "/api/settings/onboarding",
          {
            method: "PUT",
            headers: {
              "Content-Type": "application/json"
            },
            body: JSON.stringify({
              showBeforeNewConversation: false
            })
          }
        );
        renderSettings();
      }
      state.setupOnboardingDismissed = true;
      renderSetupOnboarding();
      elements.messageInput.focus();
      return;
    }
    if (action === "refresh") {
      await refreshSetupStatus();
      return;
    }
    if (action === "install") {
      const resourceId = button.dataset.resourceId;
      const profile = resourceId === "ollama"
        ? document.querySelector("[data-setup-profile=\"ollama\"]")?.value
        : null;
      if (state.setup?.ollamaInstallation?.selectionRequired && !profile) {
        throw new Error(t("setup.profile_required"));
      }
      const query = profile
        ? `?profile=${encodeURIComponent(profile)}`
        : "";
      const result = await fetchJson(
        `/api/setup/install/${encodeURIComponent(resourceId)}${query}`,
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
    } else if (action === "switch-profile") {
      const targetProfile = document.querySelector(
        "[data-setup-profile-switch=\"ollama\"]"
      )?.value;
      if (!targetProfile) {
        throw new Error(t("setup.profile_required"));
      }
      const plan = await fetchJson("/api/setup/ollama/profile-switch/plan", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ targetProfile })
      });
      const approved = await showAppConfirm(
        `${t("setup.change_summary", {
          current: plan.currentProfile,
          target: plan.targetProfile
        })}\n\n${plan.actions.map(item => `• ${item}`).join("\n")}`,
        {
          title: t("setup.change_profile"),
          confirmLabel: t("setup.apply_change"),
          danger: true
        }
      );
      if (!approved) {
        button.disabled = false;
        return;
      }
      await fetchJson("/api/setup/ollama/profile-switch/apply", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ planId: plan.planId })
      });
      showToast(
        t("setup.change_started"),
        "success",
        9000
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
    if (state.activeUserInput) {
      elements.composer.requestSubmit();
    } else if (state.requestController) {
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
    || Boolean(state.queueEditingId)
    || Boolean(state.editingTurn);

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
    || state.editingTurn
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

function referencedDiagnosticTraceId(message) {
  const known = state.history
    .map(item => item.diagnostic?.traceId)
    .filter(Boolean)
    .find(traceId => message.includes(traceId));
  if (known) {
    return known;
  }

  const explicit = /\btrace\s+id\s*[:#]?\s*([a-z0-9][a-z0-9:._-]{7,127})\b/i.exec(
    message
  );
  return explicit?.[1] ?? null;
}

function queueDiagnosticInvestigation(traceId, button) {
  if (state.pendingDiagnosticInvestigation?.diagnosticTraceId === traceId) {
    return;
  }

  button.disabled = true;
  button.textContent = "Investigation queued";
  state.pendingDiagnosticInvestigation = {
    message:
      "TRACE_DIAGNOSTIC_INVESTIGATION_V1\n"
      + `Trace ID: ${traceId}\n`
      + "Use get_trace_diagnostic for this exact trace. Explain the observed cause, distinguish Host evidence from inference, and recommend a materially different next attempt when one exists. Do not retry the failed objective and do not change models, routing, prompts, permissions, or settings. Respond in the language used by the user in this conversation.",
    hidden: true,
    diagnosticTraceId: traceId,
    interactionMode: "chat"
  };
  scheduleDiagnosticInvestigation();
}

function scheduleDiagnosticInvestigation() {
  if (
    !state.pendingDiagnosticInvestigation
    || state.requestController
    || state.conversationTransitioning
    || state.messageQueuePaused
    || state.queuedDispatchMessage
  ) {
    return;
  }

  state.queuedDispatchMessage = state.pendingDiagnosticInvestigation;
  state.pendingDiagnosticInvestigation = null;
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
  const sentAt = new Date();
  const steeredMessage = appendSteeredMessage(
    message,
    assistant,
    harnessId,
    sentAt
  );

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
    const historyMessage = {
      role: "user",
      content: message,
      createdAt: sentAt.toISOString()
    };
    if (lastHistory?.role === "assistant") {
      state.history.splice(state.history.length - 1, 0, historyMessage);
    } else {
      state.history.push(historyMessage);
    }
    settleSteeredMessage(steeredMessage, harnessId, true);
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
    settleSteeredMessage(
      steeredMessage,
      harnessId,
      false,
      error.message
    );
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

async function cancelActiveRequest() {
  const controller = state.requestController;
  if (!controller) {
    return;
  }
  state.messageQueuePaused = true;
  const assistant = state.activeAssistant;
  if (assistant?.chatRunId && !assistant.supervisionProgress) {
    assistant.cancelRequested = true;
    elements.cancelRequest.disabled = true;
    setCurrentActivity(assistant, "Canceling", false);
    if (assistant.chatAccepted) await cancelAcceptedChatRun(assistant);
    return;
  }
  const supervisionRunId = assistant?.supervisionProgress?.runId
    ?? assistant?.supervisionRunId
    ?? null;
  const durableSupervisionStarted = Boolean(assistant?.supervisionProgress);
  const durableSupervisionRequested = assistant?.requestedExecutionStrategy === "supervised"
    || assistant?.requestedExecutionStrategy === "autonomous";

  if (supervisionRunId && (durableSupervisionStarted || durableSupervisionRequested)) {
    elements.cancelRequest.disabled = true;
    try {
      const response = await fetch(
        `/api/supervision/runs/${encodeURIComponent(supervisionRunId)}/cancel`,
        { method: "POST" }
      );
      if (response.ok) {
        return;
      }
      if (response.status !== 404) {
        throw new Error(`HTTP ${response.status}`);
      }
    } catch (error) {
      elements.cancelRequest.disabled = false;
      showToast(`Could not cancel the supervised run: ${error.message}`);
      return;
    }
  }

  controller.abort();
}

async function cancelAcceptedChatRun(assistant) {
  try {
    await fetchJson(`/api/chat/runs/${encodeURIComponent(assistant.chatRunId)}/cancel`, { method: "POST" });
  } catch (error) {
    assistant.cancelRequested = false;
    elements.cancelRequest.disabled = false;
    showToast(`Could not cancel: ${error.message}`, "error");
  }
}

function appendSteeredMessage(message, assistant, harnessId, sentAt) {
  if (assistant?.container?.parentNode === elements.messages) {
    finishAssistantSegmentForSteering(assistant);
  }

  const element = document.createElement("article");
  element.className = "message user steered-message";
  element.dataset.harness = harnessId;
  const timestamp = createMessageTimestamp(sentAt);
  const content = document.createElement("div");
  content.className = "message-content";
  content.textContent = message;
  const note = document.createElement("small");
  note.className = "message-attachment-note";
  note.textContent = `Sending steering to ${benchmarkHarnessLabel(harnessId)}…`;
  content.append(note);
  element.append(timestamp, content);
  elements.messages.append(element);
  resizeObserver.observe(element);

  if (assistant) {
    appendAssistantMessage(
      {
        modelSelectionOrigin: assistant.modelSelectionOrigin,
        selectedModel: assistant.selectedModel,
        startedAt: assistant.startedAt,
        rawAnswer: assistant.rawAnswer,
        recovered: assistant.recovered,
        executionSession: assistant.executionSession
      },
      assistant
    );
  }

  return { element, note };
}

function settleSteeredMessage(
  steeredMessage,
  harnessId,
  accepted,
  error = null
) {
  steeredMessage.element.classList.toggle("failed", !accepted);
  steeredMessage.note.textContent = accepted
    ? t(
      "steer.accepted",
      { harness: benchmarkHarnessLabel(harnessId) }
    )
    : `Steering was not accepted · ${error}`;
}

function finishAssistantSegmentForSteering(assistant) {
  closeAssistantContent(assistant);
  assistant.sessionHeader.classList.remove("is-live");
  cancelAnimationFrame(assistant.clockFrame);
  assistant.progress.hidden = true;
  assistant.runningIndicator.hidden = true;
  assistant.answer.classList.remove("pending");
  assistant.answer.hidden = !assistant.answer.textContent
    && assistant.answer.childElementCount === 0;
  assistant.details.open = false;
  assistant.details.dataset.segmentComplete = "true";
  assistant.summary.textContent = assistant.technicalEventCount > 0
    ? `Technical details · ${assistant.technicalEventCount} `
      + `${assistant.technicalEventCount === 1 ? "event" : "events"} · continued`
    : "Technical details · continued";
}

async function handleComposerSubmit(event) {
  event.preventDefault();

  if (state.readOnlyConversation) {
    return;
  }

  if (setupOnboardingBlocksConversation()) {
    document.querySelector(
      "#setup-onboarding [data-setup-action=\"continue\"]"
    )?.focus();
    return;
  }

  if (state.activeUserInput) {
    await answerActiveUserInput();
    return;
  }

  if (state.requestController) {
    queueCurrentMessage();
    return;
  }

  const queuedMessage = state.queuedDispatchMessage;
  if (queuedMessage && state.editingTurn) {
    state.queuedDispatchMessage = null;
    if (queuedMessage.hidden) {
      state.pendingDiagnosticInvestigation = queuedMessage;
    } else {
      state.messageQueue.unshift(queuedMessage);
    }
    renderMessageQueue();
    return;
  }
  state.queuedDispatchMessage = null;
  const message = queuedMessage?.message ?? elements.messageInput.value.trim();
  const hiddenUserMessage = queuedMessage?.hidden === true;
  const requestInteractionMode = queuedMessage?.interactionMode
    ?? state.interactionMode;
  const diagnosticTraceId = queuedMessage?.diagnosticTraceId
    ?? referencedDiagnosticTraceId(message);

  if (!message) {
    return;
  }

  const autoModelHarness = requestInteractionMode === "execute"
    && state.harness === "auto-model-harness";
  const selectedModel = autoModelHarness
    ? "auto"
    : elements.modelSelector.value;

  if (
    !hiddenUserMessage
    && !await ensureCloudImageApproval(selectedModel)
  ) {
    if (queuedMessage) {
      state.messageQueue.unshift(queuedMessage);
      renderMessageQueue();
    }
    return;
  }

  const requestAttachments = (hiddenUserMessage ? [] : state.attachments).map(
    attachment => ({
      id: attachment.id,
      fileName: attachment.fileName,
      mimeType: attachment.mimeType,
      base64Data: attachment.base64Data,
      declaredBytes: attachment.declaredBytes
    })
  );
  const submissionSnapshot = {
    history: state.history,
    persistedMessageCount: state.persistedMessageCount,
    persistedContext: state.persistedContext,
    conversationState: state.conversationState,
    persistenceStatus: state.persistenceStatus,
    editingTurn: state.editingTurn,
    autoFollow: state.autoFollow,
    nodes: [...elements.messages.children]
  };
  state.autoFollow = true;
  updateJumpControl();
  elements.emptyState?.remove();
  const historyIndex = state.editingTurn?.historyIndex ?? state.history.length;
  const replaceFromMessageIndex = state.editingTurn?.historyIndex ?? null;
  const editSnapshot = state.editingTurn ? {
    history: state.history,
    nodes: [...elements.messages.children],
    persistedMessageCount: state.persistedMessageCount,
    persistedContext: state.persistedContext,
    editingTurn: state.editingTurn
  } : null;

  if (state.editingTurn) {
    removeConversationFrom(state.editingTurn.element);
    state.history = state.history.slice(
      0,
      historyIndex
    );
    state.editingTurn = null;
    elements.composer.classList.remove("editing");
  }

  const retainedHistory = state.history.slice(
    0,
    historyIndex
  );
  state.persistedMessageCount = Math.min(state.persistedMessageCount ?? 0, historyIndex);
  if (state.persistedContext && historyIndex < state.persistedContext.messageCount) {
    state.persistedContext = null;
  }
  const contextHistory = state.persistedContext
    && historyIndex >= state.persistedContext.messageCount
    ? [...state.persistedContext.messages, ...retainedHistory.slice(state.persistedContext.messageCount)]
    : retainedHistory;
  const requestHistory = hiddenUserMessage
    ? retainedHistory.filter(
      item => !item.hidden && item.content.length <= 8_000
    ).slice(-2)
    : contextHistory;
  const modelHistory = requestHistory.filter((item, index, messages) => {
    const failedAssistant = item.role === "assistant" && item.diagnostic?.terminalState === "failed";
    const failedUserTurn = item.role === "user"
      && messages[index + 1]?.role === "assistant"
      && messages[index + 1]?.diagnostic?.terminalState === "failed";
    return !failedAssistant && !failedUserTurn;
  }).map(
    item => ({
      role: item.role,
      content: item.content,
      createdAt: item.createdAt
    })
  );
  const sentAt = new Date();
  state.history = [
    ...retainedHistory,
    {
      role: "user",
      content: message,
      createdAt: sentAt.toISOString(),
      hidden: hiddenUserMessage
    }
  ];
  state.conversationState = "running";
  setPersistenceStatus(
    activeWorkspaceProfile()?.historyEnabled
      ? "Saving"
      : "History disabled"
  );
  if (!hiddenUserMessage) {
    appendUserMessage(
      message,
      historyIndex,
      requestAttachments,
      sentAt
    );
  }
  const conversationVersion = state.conversationVersion;
  const controller = new AbortController();
  undockExecutionPlans();
  const assistant = appendAssistantMessage({
    modelSelectionOrigin: selectedModel === "auto"
      ? "agent"
      : "user",
    selectedModel: selectedModel === "auto" ? null : selectedModel
  });
  const supervisionRunId = requestInteractionMode === "execute"
    ? globalThis.crypto?.randomUUID?.() ?? createSessionId()
    : null;
  const chatRunId = globalThis.crypto?.randomUUID?.() ?? createSessionId();
  assistant.chatRunId = chatRunId;
  rememberLiveChatRun(chatRunId);
  assistant.supervisionRunId = supervisionRunId;
  assistant.requestedExecutionStrategy = requestInteractionMode === "execute"
    ? state.executionStrategy
    : "auto";
  state.activeAssistant = assistant;
  if (!queuedMessage) {
    elements.messageInput.value = "";
  }
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
  let rejectedStatus = null;
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
          history: modelHistory,
          interactionMode: requestInteractionMode,
          harness: state.harness,
          approvalPolicy: state.approvalPolicy,
          browserSessionId: state.browserSessionId,
          conversationSessionId: state.conversationSessionId,
          webSearchEnabled: hiddenUserMessage ? false : state.webEnabled,
          images: requestAttachments,
          compactContext: hiddenUserMessage ? false : compactContext,
          autoModelHarness,
          executionStrategy: requestInteractionMode === "execute"
            ? state.executionStrategy
            : "auto",
          diagnosticTraceId,
          replaceFromMessageIndex,
          hideUserMessage: hiddenUserMessage,
          supervisionRunId,
          chatRunId
        }),
        signal: controller.signal
      }
    );

    if (!response.ok) {
      rejectedStatus = response.status;
      throw new Error(`HTTP ${response.status}`);
    }
    if (!response.body) {
      throw new Error(`HTTP ${response.status}`);
    }

    assistant.chatAccepted = true;
    clearAttachments();
    if (assistant.cancelRequested) await cancelAcceptedChatRun(assistant);

    const outcome = await consumeEventStream(response.body, assistant, {
      cooperative: true, events: reconnectChatEvents(response.body, chatRunId, controller.signal)
    });

    if (editSnapshot && outcome.terminalState === "failed"
      && !outcome.timeline.some(event => event.type === "session-persisted" || event.type === "session-created")
      && activeWorkspaceProfile()?.historyEnabled
      && state.conversationVersion === conversationVersion) {
      // Persistence rejected the edit: retain the original transcript and editable draft.
      state.history = editSnapshot.history;
      state.persistedMessageCount = editSnapshot.persistedMessageCount;
      state.persistedContext = editSnapshot.persistedContext;
      state.editingTurn = editSnapshot.editingTurn;
      elements.messages.replaceChildren(...editSnapshot.nodes);
      elements.composer.classList.add("editing");
      elements.messageInput.value = message;
      resizeComposer();
      showToast(outcome.answer, "error");
    } else if (outcome.terminalState === "failed"
      && state.conversationVersion === conversationVersion) {
      // The Host persists this terminal assistant message too. Omitting it here
      // shifts all subsequent edit indices relative to the saved conversation.
      state.history.push({
        role: "assistant", content: outcome.answer, createdAt: new Date().toISOString(),
        diagnostic: outcome.diagnostic, contentBlocks: outcome.contentBlocks, timeline: outcome.timeline
      });
    }

    if (
      outcome.completed
      && state.conversationVersion === conversationVersion
    ) {
      state.history.push(
        {
          role: "assistant",
          content: outcome.answer,
          createdAt: new Date().toISOString(),
          diagnostic: outcome.diagnostic,
          contentBlocks: outcome.contentBlocks,
          timeline: outcome.timeline
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
    } else if (
      outcome.terminalState === "failed"
      && state.conversationVersion === conversationVersion
    ) {
      state.conversationState = "failed";
      await refreshSessions();
    } else if (
      outcome.terminalState === "cancelled"
      && state.conversationVersion === conversationVersion
    ) {
      state.conversationState = "cancelled";
      await refreshSessions();
    }
  } catch (error) {
    if (rejectedStatus !== null) {
      cancelAnimationFrame(assistant.clockFrame);
      stopSlowRequestTimer(assistant);
      const originalNodes = new Set(submissionSnapshot.nodes);
      for (const node of elements.messages.children) {
        if (!originalNodes.has(node)) resizeObserver.unobserve(node);
      }
      elements.messages.replaceChildren(...submissionSnapshot.nodes);
      submissionSnapshot.nodes.forEach(node => resizeObserver.observe(node));
      state.history = submissionSnapshot.history;
      state.persistedMessageCount = submissionSnapshot.persistedMessageCount;
      state.persistedContext = submissionSnapshot.persistedContext;
      state.conversationState = submissionSnapshot.conversationState;
      setPersistenceStatus(submissionSnapshot.persistenceStatus);
      state.editingTurn = submissionSnapshot.editingTurn;
      state.autoFollow = submissionSnapshot.autoFollow;
      elements.composer.classList.toggle("editing", Boolean(state.editingTurn));
      updateJumpControl();
      try {
        const savedRun = JSON.parse(localStorage.getItem("agentic-router.live-chat-run") ?? "null");
        if (savedRun?.id === chatRunId) localStorage.removeItem("agentic-router.live-chat-run");
      } catch { /* A malformed marker must not discard the rejected message. */ }
      const reason = rejectedStatus === 409
        ? "The previous request is still finishing. Your message was kept."
        : `The request was rejected (HTTP ${rejectedStatus}). Your message was kept.`;
      if (queuedMessage?.hidden) {
        state.pendingDiagnosticInvestigation = queuedMessage;
      } else if (queuedMessage) {
        queuedMessage.error = reason;
        state.messageQueue.unshift(queuedMessage);
      } else {
        elements.messageInput.value = message;
      }
      state.messageQueuePaused = true;
      continueBufferedMessages = false;
      resizeComposer();
      renderMessageQueue();
      showToast(reason, "error");
    } else if (error.name === "AbortError") {
      continueBufferedMessages = false;
      state.messageQueuePaused = true;
      if (state.conversationVersion === conversationVersion) {
        state.conversationState = "cancelled";
      }
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
      const slowDiagnostic = assistant.slowDiagnostic?.persisted === true
        ? {
          ...assistant.slowDiagnostic,
          terminalState: "cancelled-after-slow-warning"
        }
        : null;
      finishActivity(
        assistant,
        terminalActivitySummary(
          "Canceled",
          elapsedSince(assistant),
          slowDiagnostic,
          assistant.selectedModel
        ),
        Boolean(slowDiagnostic)
      );
      addTraceDiagnosticActions(assistant, slowDiagnostic);
      stopSlowRequestTimer(assistant);
      await refreshAssistantReviewAfterCancellation(
        assistant
      );
    } else {
      if (state.conversationVersion === conversationVersion) {
        state.conversationState = "failed";
      }
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
      finishActivity(
        assistant,
        terminalActivitySummary(
          "Failed",
          elapsedSince(assistant),
          null,
          assistant.selectedModel
        ),
        true
      );
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
    scheduleDiagnosticInvestigation();
    scheduleMessageQueueDispatch();
  }
}

function appendUserMessage(
  message,
  historyIndex,
  attachments = [],
  sentAt = null
) {
  const element = document.createElement("article");
  element.className = "message user";
  const timestamp = createMessageTimestamp(sentAt);
  const content = document.createElement("div");
  content.className = "message-content";
  content.textContent = message;

  if (attachments.length > 0) {
    const gallery = createMessageImageGallery(attachments);
    const attachmentNote = document.createElement("small");
    attachmentNote.className = "message-attachment-note";
    attachmentNote.textContent =
      `${attachments.length} attached image${attachments.length === 1 ? "" : "s"}`
      + " · bytes not persisted";
    content.append(gallery, attachmentNote);
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
  element.append(timestamp, content, actions);
  elements.messages.append(element);
  resizeObserver.observe(element);
}

function createMessageImageGallery(attachments) {
  const gallery = document.createElement("div");
  gallery.className = "message-image-gallery";
  const acceptedTypes = new Set([
    "image/jpeg",
    "image/png",
    "image/webp",
    "image/gif"
  ]);

  for (const attachment of attachments) {
    if (
      !acceptedTypes.has(attachment.mimeType)
      || !attachment.base64Data
    ) {
      continue;
    }

    const fileName = attachment.fileName || "image attachment";
    const preview = document.createElement("button");
    preview.type = "button";
    preview.className = "message-image-preview";
    preview.dataset.imageName = fileName;
    preview.dataset.imageMimeType = attachment.mimeType;
    preview.dataset.imageBytes = String(attachment.declaredBytes);
    preview.setAttribute("aria-haspopup", "dialog");
    preview.setAttribute("aria-label", `Review ${fileName}`);
    preview.title = `Review ${fileName}`;
    const image = document.createElement("img");
    image.src = `data:${attachment.mimeType};base64,${attachment.base64Data}`;
    image.alt = fileName;
    preview.append(image);
    gallery.append(preview);
  }

  return gallery;
}

function handleMessageImageReviewClick(event) {
  const preview = event.target.closest(".message-image-preview");

  if (!preview) {
    return;
  }

  const image = preview.querySelector("img");
  if (!image?.src) {
    return;
  }

  const fileName = preview.dataset.imageName || image.alt || "Image attachment";
  const mimeType = preview.dataset.imageMimeType || "image";
  const declaredBytes = Number(preview.dataset.imageBytes);
  elements.imageReviewTitle.textContent = fileName;
  elements.imageReviewContent.src = image.src;
  elements.imageReviewContent.alt = `Review of ${fileName}`;
  elements.imageReviewMetadata.textContent = [
    mimeType.replace("image/", "").toUpperCase(),
    Number.isFinite(declaredBytes) ? formatBytes(declaredBytes) : null,
    "available only in this open conversation"
  ].filter(Boolean).join(" · ");
  elements.imageReviewDialog.showModal();
  elements.closeImageReview.focus();
}

function closeImageReview() {
  if (elements.imageReviewDialog.open) {
    elements.imageReviewDialog.close();
  }
  elements.imageReviewContent.removeAttribute("src");
  elements.imageReviewContent.alt = "";
  elements.imageReviewTitle.textContent = "Image review";
  elements.imageReviewMetadata.textContent = "";
}

function createMessageTimestamp(value) {
  const timestamp = document.createElement("time");
  timestamp.className = "message-timestamp";
  const parsed = value ? new Date(value) : null;

  if (!parsed || Number.isNaN(parsed.getTime())) {
    timestamp.hidden = true;
    return timestamp;
  }

  timestamp.dateTime = parsed.toISOString();
  timestamp.textContent = parsed.toLocaleString(
    window.AgenticRouterI18n.locale,
    {
      dateStyle: "short",
      timeStyle: "medium"
    }
  );
  timestamp.title = parsed.toLocaleString(
    window.AgenticRouterI18n.locale,
    {
      dateStyle: "full",
      timeStyle: "long"
    }
  );
  return timestamp;
}

function createRunningIndicator() {
  const indicator = document.createElement("span");
  indicator.className = "assistant-running-indicator";
  indicator.setAttribute("role", "status");

  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.classList.add("assistant-running-brain");
  svg.setAttribute("viewBox", "0 0 32 32");
  svg.setAttribute("fill", "none");
  svg.setAttribute("aria-hidden", "true");

  const paths = [
    "M15.8 8.4c-.5-2-2.3-3.4-4.4-3.4A4.4 4.4 0 0 0 7 9.4v.3a4.6 4.6 0 0 0-2 7.8 4.6 4.6 0 0 0 3.7 7.3 4.3 4.3 0 0 0 7.1-3.3V8.4Z",
    "M16.2 8.4c.5-2 2.3-3.4 4.4-3.4A4.4 4.4 0 0 1 25 9.4v.3a4.6 4.6 0 0 1 2 7.8 4.6 4.6 0 0 1-3.7 7.3 4.3 4.3 0 0 1-7.1-3.3V8.4Z",
    "M10.4 10.5c2.1.1 3.5 1.5 3.6 3.6m-5.3 4.3c1.7-1 3.6-.5 4.5 1m8.4-8.9c-2.1.1-3.5 1.5-3.6 3.6m5.3 4.3c-1.7-1-3.6-.5-4.5 1"
  ];

  for (const pathData of paths) {
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", pathData);
    svg.append(path);
  }

  const activity = document.createElement("span");
  activity.className = "assistant-current-activity";
  activity.textContent = "Thinking…";
  indicator.append(svg, activity);
  return indicator;
}

function appendAssistantMessage(options = {}, existingAssistant = null) {
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
  const sessionFooter = document.createElement("div");
  sessionFooter.className = "execution-session-footer";
  sessionFooter.setAttribute("aria-label", "Final execution status");
  sessionFooter.hidden = true;
  const planPanel = document.createElement("details");
  planPanel.className = "execution-plan";
  planPanel.hidden = true;
  planPanel.open = false;
  const planSummary = document.createElement("summary");
  const planBody = document.createElement("div");
  planBody.className = "execution-plan-body";
  planPanel.append(planSummary, planBody);
  details.append(summary, activityList);

  const answer = document.createElement("div");
  answer.className = "assistant-response assistant-answer pending";
  const runningIndicator = createRunningIndicator();
  const completionSummary = document.createElement("section");
  completionSummary.className = "execution-completion-summary";
  completionSummary.setAttribute("aria-label", "Host completion summary");
  completionSummary.hidden = true;
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
    sessionHeader,
    progress,
    planPanel,
    workActivity,
    answer,
    runningIndicator,
    completionSummary,
    sources,
    details,
    sessionFooter,
    actions
  );
  elements.messages.append(container);

  const assistant = existingAssistant ?? {};
  Object.assign(assistant, {
    container,
    answer,
    modelNotice,
    progress,
    supervisionProgress: null,
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
    sessionFooter,
    planPanel,
    planSummary,
    planBody,
    workActivity,
    workNarrative: null,
    actionItems: new Map(),
    modelSelectionOrigin: options.modelSelectionOrigin ?? null,
    selectedModel: options.selectedModel ?? assistant.selectedModel ?? null,
    activityGroups: new Map(),
    technicalEventCount: 0,
    startedAt: options.startedAt ?? performance.now(),
    clockFrame: null,
    lastClockUpdate: 0,
    recovered: options.recovered ?? false,
    rawAnswer: options.rawAnswer ?? "",
    sources,
    sourcesSummary,
    sourcesList,
    copyButton,
    reviewButton,
    executionSession: options.executionSession ?? null,
    runningIndicator,
    completionSummary
  });
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
  if (assistant.executionSession) {
    updateExecutionSession(assistant, assistant.executionSession, false);
  }
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
      if (
        assistant.supervisionProgress
        && !["completed", "blocked", "cancelled"].includes(
          assistant.supervisionProgress.state
        )
      ) {
        const strategy = assistant.supervisionProgress.executionStrategy === "autonomous"
          ? "Autonomous"
          : "Supervisor";
        assistant.progress.textContent =
          `${strategy} · ${supervisionPhaseLabel(assistant.supervisionProgress.phase)} · `
          + formatElapsed(elapsedSince(assistant));
      } else {
        assistant.progress.textContent =
          `${assistant.activeReasoning ? "Thinking" : "Thinking…"} · `
          + formatElapsed(elapsedSince(assistant));
      }
      assistant.lastClockUpdate = timestamp;
    }

    assistant.clockFrame = requestAnimationFrame(update);
  };
  assistant.clockFrame = requestAnimationFrame(update);
}

function supervisionPhaseLabel(phase) {
  return {
    foundation: "Starting",
    recovery: "Recovering",
    decomposing: "Planning work",
    working: "Executing work",
    verifying: "Verifying results",
    completing: "Final review"
  }[phase] ?? "Working";
}

function supervisionEventIsMeaningful(type) {
  return type?.startsWith("supervision.")
    && type !== "supervision.turn-slow-warning"
    && type !== "supervision.turn-slow-critical"
    && type !== "supervision.turn-status";
}

function renderSupervisionExecutionPlan(assistant, supervision) {
  if (!supervision.workItems?.length) {
    if (!assistant.executionSession?.plan) {
      assistant.planPanel.hidden = true;
      assistant.planPanel.classList.remove("docked");
      assistant.planBody.replaceChildren();
    }
    return;
  }
  renderExecutionPlan(
    assistant,
    {
      state: supervision.state,
      changedFileCount: 0,
      planSource: "supervisor",
      supervisionPhase: supervision.phase,
      plan: {
        objective: supervision.objective,
        steps: supervision.workItems.map(item => ({
          id: item.id,
          title: item.objective,
          status: item.status === "active" || item.status === "verifying"
            ? "in-progress"
            : item.status,
          dependencies: []
        })),
        currentStepId: supervision.workItemId,
        completedStepCount: supervision.completedItems,
        revisionCount: supervision.eventSequence
      }
    }
  );
}

function compactPlanText(value, maximumLength = 72) {
  let normalized = String(value ?? "").replace(/\s+/g, " ").trim();
  if (!normalized) {
    return "Untitled work item";
  }

  const labelBoundary = normalized.indexOf(":");
  if (labelBoundary >= 12 && labelBoundary <= 56) {
    const label = normalized.slice(0, labelBoundary).trim();
    const detail = normalized.slice(labelBoundary + 1).trim();
    const beginsWithAction = /^(add|build|complete|configure|create|fix|implement|inspect|plan|prepare|review|test|update|validate|verify|adicionar|completar|configurar|corrigir|criar|implementar|inspecionar|planejar|preparar|revisar|testar|validar|verificar)\b/i.test(label);
    if (detail && !beginsWithAction) {
      const detailBoundary = detail.search(/;|[.!?](?:\s|$)|\s[—–]\s/);
      const action = (detailBoundary >= 8
        ? detail.slice(0, detailBoundary)
        : detail).replace(/[.,]+$/, "");
      normalized = `${label} · ${action}`;
    }
  }
  const clauseBoundary = normalized.search(/[:;]|\s[—–]\s/);
  const clause = clauseBoundary >= 12
    ? normalized.slice(0, clauseBoundary)
    : normalized;
  if (clause.length <= maximumLength) {
    return clause.replace(/[.,]+$/, "");
  }
  const candidate = clause.slice(0, maximumLength + 1);
  const wordBoundary = candidate.lastIndexOf(" ");
  return `${candidate.slice(0, wordBoundary >= 32 ? wordBoundary : maximumLength).trim()}…`;
}

function renderSupervisionSessionHeader(assistant, supervision) {
  assistant.sessionHeader.hidden = false;
  assistant.sessionHeader.replaceChildren();
  const stateLabel = document.createElement("strong");
  stateLabel.textContent = supervision.state;
  const route = document.createElement("span");
  const strategy = supervision.executionStrategy === "autonomous"
    ? "Autonomous"
    : "Supervisor";
  route.textContent = supervision.workerModel || supervision.supervisorModel
    ? `Worker: ${supervision.workerModel || "unavailable"} / ${supervision.workerGpu || "Auto"} / ${supervision.workerRuntime || "runtime"} · `
      + `Supervisor: ${supervision.supervisorModel || "unavailable"} / ${supervision.supervisorGpu || "Auto"} / ${supervision.supervisorRuntime || "runtime"} · `
      + `Harness: ${supervision.harness || "unavailable"} · ${strategy}`
    : `Target: ${supervision.model || "unavailable"} · `
      + `Harness: ${supervision.harness || "unavailable"} · ${strategy}`;
  route.title = [
    `Run: ${supervision.runId}`,
    supervision.approvalPolicy
      ? `Approval: ${supervision.approvalPolicy}`
      : null
  ].filter(Boolean).join("\n");
  const role = supervision.role === "worker"
    ? "Worker"
    : supervision.role === "supervisor"
      ? "Supervisor"
      : "Host";
  const counts = document.createElement("span");
  counts.textContent = [
    role,
    supervision.totalItems > 0
      ? `${supervision.completedItems}/${supervision.totalItems} items`
      : "building queue",
    supervisionPhaseLabel(supervision.phase),
    supervision.approvalPolicy
      ? `approval ${supervision.approvalPolicy}`
      : null
  ].filter(Boolean).join(" · ");
  assistant.sessionHeader.append(stateLabel, route, counts);
  updateExecutionStatusPlacement(assistant);
}

function supervisionProgressMessage(streamEvent, supervision) {
  const action = /^Host action [^(]+ \(([^)]+)\) entered durable phase ([^.]+)\.$/.exec(
    streamEvent.message ?? ""
  );
  if (action) {
    const [, tool, phase] = action;
    return phase === "in-flight"
      ? `Host is executing ${tool}.`
      : phase === "committed"
        ? `Host completed and recorded ${tool}.`
        : phase === "rejected"
          ? `Host rejected ${tool}.`
          : `${tool}: ${phase}.`;
  }
  const workItem = supervision.workItems?.find(
    item => item.id === supervision.workItemId
  );
  const itemTitle = compactPlanText(workItem?.objective ?? supervision.objective);
  if (streamEvent.type === "supervision.turn-status") {
    if (supervision.role === "worker") {
      return `Working on: ${itemTitle}. No new Host-observed result yet.`;
    }
    if (supervision.phase === "verifying") {
      return `Verifying: ${itemTitle}. No new Host-observed result yet.`;
    }
    if (supervision.phase === "completing") {
      return "Reviewing accepted work and current Host evidence.";
    }
    return `Planning the work queue for: ${itemTitle}.`;
  }
  if (streamEvent.type === "supervision.worker-started") {
    return `Executing: ${itemTitle}.`;
  }
  if (streamEvent.type === "supervision.verification-started") {
    return `Verifying: ${itemTitle}.`;
  }
  if (streamEvent.type === "supervision.work-queued") {
    return `Plan ready with ${supervision.totalItems} work item(s).`;
  }
  return streamEvent.message ?? "Supervision state updated.";
}

function supervisionProgressSource(streamEvent) {
  return streamEvent.supervisionProgress.role === "worker"
    ? "Worker"
    : streamEvent.type.startsWith("supervision.action-")
      ? "Host"
      : "Supervisor";
}

function renderSupervisionProgress(assistant, streamEvent) {
  const supervision = streamEvent.supervisionProgress;
  if (!supervision) {
    return;
  }

  assistant.supervisionProgress = supervision;
  renderCompletionSummary(assistant, supervision.completionSummary);
  assistant.selectedModel = supervision.model ?? assistant.selectedModel;
  renderSupervisionSessionHeader(
    assistant,
    supervision
  );
  if (assistant.modelNotice.hidden && supervision.model) {
    renderModelSelection(
      assistant,
      supervision.model,
      assistant.modelSelectionOrigin
    );
  }
  renderSupervisionExecutionPlan(
    assistant,
    supervision
  );
  if (streamEvent.message) {
    assistant.workActivity.hidden = false;
    ensureWorkNarrative(
      assistant,
      `${supervisionProgressSource(streamEvent)}: ${supervisionProgressMessage(streamEvent, supervision)}`,
      true
    );
  }
  if (streamEvent.slowRequest) {
    renderSlowRequestAlert(
      assistant,
      streamEvent,
      false
    );
  }
  const terminal = ["completed", "blocked", "cancelled"].includes(
    supervision.state
  );
  assistant.progress.hidden = terminal;
  if (!terminal) {
    const strategy = supervision.executionStrategy === "autonomous"
      ? "Autonomous"
      : "Supervisor";
    assistant.progress.textContent =
      `${strategy} · ${supervisionPhaseLabel(supervision.phase)} · `
      + formatElapsed(elapsedSince(assistant));
  }
}

function renderModelSelection(assistant, model, origin) {
  const message = origin === "user"
    ? `Model ${model} selected by the user.`
    : origin === "fallback"
      ? `Model ${model} selected as fallback by the Host.`
      : `Model ${model} routed by the agent.`;
  assistant.selectedModel = model;
  assistant.modelNotice.textContent = message;
  assistant.modelNotice.hidden = false;
}

function flushAssistantReasoning(reasoning, scrollToEnd) {
  if (reasoning.pendingDeltas.length === 0) return;
  const text = reasoning.pendingDeltas.join("");
  reasoning.pendingDeltas.length = 0;
  const last = reasoning.body.lastChild;
  if (last?.nodeType === Node.TEXT_NODE && last.length + text.length <= 8192) {
    last.appendData(text);
  } else {
    reasoning.body.append(document.createTextNode(text));
  }
  if (scrollToEnd) {
    reasoning.body.scrollTop = reasoning.body.scrollHeight;
  }
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
    details.open = !assistant.supervisionProgress && !assistant.replayingHistory;
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
      chunks: [],
      contentBlockId,
      pendingDeltas: [],
      flushFrame: null
    };
  }

  const reasoning = assistant.activeReasoning;
  reasoning.chunks.push(delta);
  reasoning.pendingDeltas.push(delta);
  if (assistant.replayingHistory) {
    flushAssistantReasoning(reasoning, false);
  } else if (reasoning.flushFrame === null) {
    reasoning.flushFrame = requestAnimationFrame(() => {
      reasoning.flushFrame = null;
      flushAssistantReasoning(reasoning, true);
    });
  }
  reasoning.details.dataset.deltaCount = String(
    Number(reasoning.details.dataset.deltaCount) + 1
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

  const reasoning = assistant.activeReasoning;
  if (reasoning.flushFrame !== null) cancelAnimationFrame(reasoning.flushFrame);
  flushAssistantReasoning(reasoning, false);
  reasoning.details.open = false;
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
    chunks: [],
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
  response.chunks.push(delta);
  response.body.dataset.deltaCount = String(
    Number(response.body.dataset.deltaCount) + 1
  );
  if (renderedHtml) {
    renderAssistantResponse(
      assistant,
      response,
      renderedHtml,
      response.chunks.join(""),
      aggregateMarkdown
    );
  } else {
    response.body.append(document.createTextNode(delta));
    assistant.copyButton.disabled = false;
  }
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
  const tool = action?.tool?.trim().toLowerCase();
  return Boolean(tool)
    && !tool.startsWith("mcp__agentic_router__");
}

function canonicalActionTool(tool) {
  const normalized = tool?.trim().toLowerCase() ?? "";
  const separator = normalized.lastIndexOf("__");
  return separator >= 0 ? normalized.slice(separator + 2) : normalized;
}

function isToolDiscoveryAction(action) {
  return ["tool_search", "toolsearch"].includes(canonicalActionTool(action?.tool));
}

function isReviewableWorkAction(action) {
  return new Set([
    "create_file",
    "create_files",
    "write_file",
    "write",
    "replace_text",
    "edit",
    "apply_patch",
    "patch",
    "delete_paths",
    "create_directory"
  ]).has(canonicalActionTool(action?.tool));
}

function actionDisplayLabel(tool) {
  const normalized = canonicalActionTool(tool);
  return {
    create_file: "Create",
    create_files: "Create files",
    write_file: "Write",
    write: "Write",
    replace_text: "Edit",
    edit: "Edit",
    apply_patch: "Apply patch",
    patch: "Apply patch",
    delete_paths: "Delete",
    create_directory: "Create folder",
    read: "Read",
    read_file: "Read",
    list_files: "List files",
    search_text: "Search",
    get_file_info: "Inspect",
    run_process: "Run",
    run_validation_profile: "Validate",
    git_status: "Git status",
    web_search: "Search web",
    tool_search: "Tool discovery",
    toolsearch: "Tool discovery"
  }[normalized] ?? (normalized.replaceAll("_", " ") || tool);
}

function actionTarget(action) {
  const prefix = `${action.tool}:`;
  return action.summary?.startsWith(prefix)
    ? action.summary.slice(prefix.length).trim()
    : action.summary;
}

function currentActivityToolKind(tool) {
  const normalized = canonicalActionTool(tool);
  if (["read", "read_file", "readfile"].includes(normalized)) {
    return "read";
  }
  if (["list", "list_files", "glob", "ls"].includes(normalized)) {
    return "list";
  }
  if (["search", "search_text", "grep", "rg"].includes(normalized)) {
    return "search";
  }
  if (["get_file_info", "inspect", "stat"].includes(normalized)) {
    return "inspect";
  }
  if (["create", "create_file", "create_files"].includes(normalized)) {
    return "create";
  }
  if (["create_directory", "mkdir"].includes(normalized)) {
    return "create-directory";
  }
  if ([
    "write",
    "write_file",
    "edit",
    "replace_text",
    "apply_patch",
    "patch",
    "file_change",
    "filechange"
  ].includes(normalized)) {
    return "update";
  }
  if (["delete", "delete_paths", "remove"].includes(normalized)) {
    return "delete";
  }
  if ([
    "run_process",
    "bash",
    "shell",
    "command",
    "command_execution",
    "exec_command",
    "execute"
  ].includes(normalized)) {
    return "process";
  }
  if (["run_validation_profile", "validation", "test"].includes(normalized)) {
    return "validation";
  }
  if (normalized.startsWith("git_") || normalized === "git") {
    return "git";
  }
  if (["web_search", "websearch"].includes(normalized)) {
    return "web";
  }
  if (["tool_search", "toolsearch"].includes(normalized)) {
    return "tools";
  }
  return "action";
}

function currentActivityTarget(action) {
  const paths = Array.isArray(action?.relativePaths)
    ? action.relativePaths
      .filter(path => typeof path === "string")
      .map(path => path.trim().replaceAll("\\", "/"))
      .filter(path => path
        && !path.startsWith("/")
        && !/^[a-z]:\//i.test(path)
        && !path.split("/").includes(".."))
    : [];
  if (paths.length === 1) {
    const path = paths[0];
    return path.length <= 56 ? path : `…${path.slice(-55)}`;
  }
  return paths.length > 1 ? `${paths.length} files` : null;
}

function currentActivityWorkItem(assistant) {
  const supervision = assistant.supervisionProgress;
  if (supervision && !["completed", "blocked", "cancelled"].includes(supervision.state)) {
    const item = supervision.workItems?.find(
      candidate => candidate.id === supervision.workItemId
    );
    const objective = item?.objective ?? supervision.objective;
    return objective ? compactPlanText(objective, 56) : null;
  }

  const plan = assistant.executionSession?.plan;
  const step = plan?.steps?.find(
    candidate => candidate.id === plan.currentStepId
  ) ?? plan?.steps?.find(candidate => candidate.status === "in-progress");
  return step?.title ? compactPlanText(step.title, 56) : null;
}

function setCurrentActivity(assistant, text, includeWorkItem = true) {
  const label = assistant.runningIndicator.querySelector(".assistant-current-activity");
  const workItem = includeWorkItem ? currentActivityWorkItem(assistant) : null;
  const normalized = text.replace(/…$/, "");
  const value = workItem && !normalized.toLowerCase().includes(workItem.toLowerCase())
    ? `${normalized} · ${workItem}…`
    : `${normalized}…`;
  if (label.textContent !== value) {
    label.textContent = value;
  }
}

function activeActionActivity(kind, target, preparing) {
  const suffix = target ? ` ${target}` : "";
  const active = {
    read: `Reading${suffix}`,
    list: `Listing files${suffix}`,
    search: `Searching files${suffix}`,
    inspect: `Inspecting files${suffix}`,
    create: `Creating${target ? suffix : " file"}`,
    "create-directory": `Creating folder${suffix}`,
    update: `Updating${target ? suffix : " files"}`,
    delete: `Deleting${target ? suffix : " files"}`,
    process: "Running command",
    validation: "Running validation",
    git: "Checking Git status",
    web: "Searching the web",
    tools: "Loading tools",
    action: "Executing action"
  }[kind];
  if (!preparing) {
    return active;
  }
  return {
    read: `Preparing to read${suffix || " files"}`,
    list: "Preparing to list files",
    search: "Preparing to search files",
    inspect: "Preparing to inspect files",
    create: `Preparing to create${target ? suffix : " a file"}`,
    "create-directory": `Preparing to create folder${suffix}`,
    update: `Preparing to update${target ? suffix : " files"}`,
    delete: `Preparing to delete${target ? suffix : " files"}`,
    process: "Preparing to run command",
    validation: "Preparing validation",
    git: "Preparing to check Git status",
    web: "Preparing web search",
    tools: "Preparing tool discovery",
    action: "Preparing action"
  }[kind];
}

function completedActionActivity(kind) {
  if (["read", "list", "search", "inspect", "web"].includes(kind)) {
    return "Analyzing findings";
  }
  if (["create", "create-directory", "update", "delete"].includes(kind)) {
    return "Reviewing changes";
  }
  return {
    process: "Analyzing command result",
    validation: "Reviewing validation result",
    git: "Analyzing Git status",
    tools: "Reviewing available tools",
    action: "Reviewing action result"
  }[kind];
}

function renderCurrentActivity(assistant, streamEvent) {
  const label = assistant.runningIndicator.querySelector(".assistant-current-activity");
  const action = streamEvent.localAction;
  if (action && ["proposed", "approved", "executing", "awaiting-approval"].includes(action.state)) {
    label.dataset.actionId = action.actionId;
    const kind = currentActivityToolKind(action.tool);
    label.dataset.lastActionKind = kind;
    if (streamEvent.type === "action.awaiting-approval" || action.state === "awaiting-approval") {
      setCurrentActivity(assistant, "Awaiting approval", false);
    } else {
      setCurrentActivity(
        assistant,
        activeActionActivity(
          kind,
          currentActivityTarget(action),
          action.state !== "executing"
        )
      );
    }
    return;
  }
  if (action?.actionId === label.dataset.actionId) {
    delete label.dataset.actionId;
  }
  if (action) {
    const kind = currentActivityToolKind(action.tool);
    label.dataset.lastActionKind = kind;
    if (["failed", "rejected", "cancelled"].includes(action.state)) {
      setCurrentActivity(assistant, "Revising approach");
    } else if (action.state === "completed") {
      setCurrentActivity(assistant, completedActionActivity(kind));
    }
    return;
  }
  if (streamEvent.type === "validation-started") {
    label.dataset.actionId = "validation";
    label.dataset.lastActionKind = "validation";
    setCurrentActivity(assistant, "Running validation");
    return;
  } else if (streamEvent.type === "validation-completed" && label.dataset.actionId === "validation") {
    delete label.dataset.actionId;
    setCurrentActivity(assistant, "Reviewing validation result");
    return;
  }
  if (label.dataset.actionId) {
    return;
  }

  const type = streamEvent.type ?? "";
  if (type === "request.received") {
    setCurrentActivity(assistant, "Understanding request", false);
  } else if (
    type.startsWith("target.")
    || type.startsWith("router.")
    || (type.startsWith("harness.") && type.includes("start"))
  ) {
    setCurrentActivity(assistant, "Preparing specialist", false);
  } else if (
    type.startsWith("workspace")
    || type.startsWith("project-")
    || type.startsWith("repository-")
    || type.startsWith("baseline-")
    || type.startsWith("preexisting-")
  ) {
    setCurrentActivity(assistant, "Inspecting workspace", false);
  } else if (type === "agent.toolset-requested" || type.includes("toolset")) {
    setCurrentActivity(assistant, "Checking available capabilities");
  } else if (type.startsWith("action.planning")) {
    setCurrentActivity(assistant, "Planning next action");
  } else if (type.startsWith("execution-plan") || type === "execution-step-completed") {
    setCurrentActivity(assistant, "Organizing work");
  } else if (type === "reasoning.delta") {
    const kind = label.dataset.lastActionKind;
    setCurrentActivity(
      assistant,
      kind ? completedActionActivity(kind) : "Analyzing request"
    );
  } else if (type === "response.first-chunk" || type === "response.delta") {
    setCurrentActivity(assistant, "Writing response");
  } else if (type === "action.recovery-decision-required") {
    setCurrentActivity(assistant, "Awaiting your recovery decision", false);
  } else if (type.includes("recovery")) {
    setCurrentActivity(assistant, "Revising approach");
  } else if (type === "user-input.requested" || type === "approval.requested") {
    setCurrentActivity(assistant, "Awaiting your decision", false);
  } else if (type.startsWith("supervision.")) {
    const supervision = assistant.supervisionProgress;
    if (supervision && !["completed", "blocked", "cancelled"].includes(supervision.state)) {
      setCurrentActivity(assistant, supervisionPhaseLabel(supervision.phase));
    }
  }
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

function formatActionOutput(action, value) {
  const tool = canonicalActionTool(action.tool);
  try {
    const result = JSON.parse(value);
    if ((tool === "read_file" || tool === "read")
      && typeof result?.content === "string"
      && [result.offsetBytes, result.lengthBytes, result.sizeBytes].every(Number.isFinite)
      && Object.keys(result).every(key => ["content", "offsetBytes", "lengthBytes", "sizeBytes"].includes(key))) {
      return `Offset: ${result.offsetBytes} bytes · Read: ${result.lengthBytes} bytes · Size: ${result.sizeBytes} bytes\n\n${result.content}`;
    }
    if (tool === "search_text" && Array.isArray(result?.results)
      && result.results.length === 0 && Number.isFinite(result.searchedFiles)
      && typeof result.truncated === "boolean"
      && Object.keys(result).every(key => ["results", "searchedFiles", "truncated"].includes(key))) {
      return `No matches found.\nFiles searched: ${result.searchedFiles}\nResults truncated: ${result.truncated ? "yes" : "no"}`;
    }
    if (tool !== "read_file" && tool !== "read" && result !== null && typeof result === "object") {
      return JSON.stringify(result, null, 2);
    }
  } catch {
    // Plain text and bounded/truncated payloads remain literal.
  }
  return value;
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
  const discovery = isToolDiscoveryAction(action);
  if (!discovery) {
    assistant.workActivity.hidden = false;
  }
  assistant.progress.hidden = false;
  assistant.progress.textContent =
    `Executing… · ${formatElapsed(elapsedSince(assistant))}`;
  const relativePath = actionTarget(action);
  const reviewable = isReviewableWorkAction(action);
  let item = assistant.actionItems.get(action.actionId);

  if (!item) {
    const details = document.createElement("details");
    details.className = "work-action";
    details.dataset.timelineKind = "action";
    details.dataset.actionId = action.actionId;
    if (reviewable) {
      details.dataset.eventType = streamEvent.type;
    }
    const summary = document.createElement("summary");
    const icon = document.createElement("span");
    icon.className = "work-action-icon";
    icon.textContent = "⌁";
    icon.setAttribute("aria-hidden", "true");
    const label = document.createElement("span");
    label.className = "work-action-label";
    label.textContent = actionDisplayLabel(action.tool);
    const link = document.createElement(reviewable ? "a" : "span");
    link.className = reviewable ? "work-action-file" : "work-action-target";
    link.textContent = relativePath;
    if (reviewable) {
      link.href = "#";
      link.setAttribute("aria-label", `Open review for ${relativePath}`);
    }
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
    (discovery ? assistant.activityList : assistant.workActivity).append(details);
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
    if (reviewable) {
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
  }

  if (reviewable) {
    item.details.dataset.eventType = streamEvent.type;
  }
  item.details.dataset.state = action.state;
  item.status.textContent = actionStateLabel(action.state);
  item.icon.textContent = action.state === "completed"
    ? "✓"
    : action.state === "failed" || action.state === "rejected"
      ? "!"
      : "⌁";
  if (action.preview) item.input = action.preview;
  if (streamEvent.type === "action.started" && action.resultOutput) {
    item.input = action.resultOutput;
  } else if (action.resultOutput != null) {
    item.output = action.resultOutput;
  }
  const sections = [];
  if (item.input) sections.push(`Input\n${summarizeActionPreview(item.input)}`);
  if (item.output != null) {
    sections.push(`Result\n${summarizeActionPreview(formatActionOutput(action, item.output))}`);
  }
  item.preview.textContent = sections.join("\n\n") || "No textual content to display.";
  if (["completed", "failed", "rejected"].includes(action.state)) {
    item.details.open = false;
  }
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

function addUserInputTranscript(assistant, streamEvent) {
  const request = streamEvent.userInput;
  if (!request) {
    return;
  }
  const existing = [...assistant.workActivity.querySelectorAll(
    ".user-input-transcript"
  )].find(item => item.dataset.userInputId === request.id);
  if (existing) {
    return;
  }

  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
  assistant.workActivity.hidden = false;
  const card = document.createElement("section");
  card.className = "user-input-transcript";
  card.dataset.timelineKind = "user-input";
  card.dataset.eventType = streamEvent.type;
  card.dataset.userInputId = request.id;
  const title = document.createElement("strong");
  title.className = "user-input-transcript-title";
  title.textContent = request.questions.length === 1
    ? "Question answered"
    : `${request.questions.length} questions answered`;
  card.append(title);

  const answers = new Map(
    (request.answers ?? []).map(answer => [answer.questionId, answer.answer])
  );
  request.questions.forEach(question => {
    const item = document.createElement("div");
    item.className = "user-input-transcript-item";
    const header = document.createElement("span");
    header.className = "user-input-transcript-header";
    header.textContent = question.header;
    const prompt = document.createElement("p");
    prompt.textContent = question.question;
    const answer = document.createElement("div");
    answer.className = "user-input-transcript-answer";
    const answerLabel = document.createElement("span");
    answerLabel.textContent = "Answer";
    const answerText = document.createElement("strong");
    answerText.textContent = answers.get(question.id) ?? "No answer submitted";
    answer.append(answerLabel, answerText);
    item.append(header, prompt, answer);
    card.append(item);
  });
  assistant.workActivity.append(card);
}

