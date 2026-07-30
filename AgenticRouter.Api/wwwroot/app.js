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
  browserSessionId: createSessionId(),
  activeReview: null,
  activeAgentModel: null
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
    "new-conversation",
    "default-context-tokens",
    "provider-context-tokens",
    "reserved-response-tokens",
    "max-tool-output-tokens",
    "generation-timeout-seconds",
    "max-conversation-messages",
    "model-diagnostics-list",
    "model-context-diagnostic",
    "model-test-selector",
    "test-model",
    "model-test-result",
    "approval-policy",
    "workspace-badge",
    "workspace-path",
    "session-history",
    "recent-sessions",
    "archived-session-section",
    "archived-sessions",
    "history-new-conversation",
    "workspace-dialog",
    "workspace-form",
    "workspace-profile-list",
    "workspace-profile-name",
    "trusted-workspace-path",
    "workspace-validation",
    "workspace-save-status",
    "clear-workspace",
    "pick-workspace",
    "workspace-history-enabled",
    "history-usage",
    "delete-archived-sessions",
    "delete-all-sessions",
    "project-profile-summary",
    "project-profile-details",
    "refresh-project-profile",
    "detected-validation-profile",
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
    "undo-status"
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
  elements.newConversation.addEventListener("click", startNewConversation);
  elements.historyNewConversation.addEventListener("click", startNewConversation);
  elements.modelSelector.addEventListener("change", handleModelSelectionChange);
  elements.modelLock.addEventListener("change", handleModelLockChange);
  elements.testModel.addEventListener("click", testSelectedModel);
  elements.approvalPolicy.addEventListener("change", handleApprovalPolicyChange);
  elements.workspaceForm.addEventListener("submit", saveWorkspace);
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
  document.querySelectorAll(".mode-option").forEach(
    button => button.addEventListener("click", handleModeChange)
  );
  document.addEventListener("visibilitychange", handleVisibilityChange);
  document.querySelector("#open-settings").addEventListener("click", openSettings);
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
    workspaceProfiles
  ] = await Promise.all([
    fetchJson("/api/settings"),
    fetchJson("/api/models"),
    fetchJson("/api/devices"),
    fetchJson("/api/workspace"),
    fetchJson("/api/workspace/project-profile"),
    fetchJson("/api/workspace/validation-profile"),
    fetchJson("/api/workspaces")
  ]);

  state.settings = settings;
  state.models = modelsResponse.models;
  state.devices = devicesResponse.devices;
  state.workspace = workspace;
  state.projectProfile = projectProfile;
  state.validationProfiles = validationProfiles;
  state.workspaceProfiles = workspaceProfiles;
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
}

async function activateWorkspace(id) {
  elements.workspaceSaveStatus.textContent = "Ativando…";

  try {
    await fetchJson(
      `/api/workspaces/${encodeURIComponent(id)}/activate`,
      {
        method: "POST"
      }
    );
    resetConversationForWorkspaceChange();
    await refreshWorkspaceState();
    elements.workspaceSaveStatus.textContent =
      "Workspace ativado. Modo Chat e aprovação manual restaurados.";
  } catch (error) {
    elements.workspaceSaveStatus.textContent = error.message;
  }
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
      resetConversationForWorkspaceChange();
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
        + "pelo Agentic Router v0.8.1."
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

function resetConversationForWorkspaceChange() {
  startNewConversation();
  state.interactionMode = "chat";
  state.approvalPolicy = "ask";
  state.lockedModel = null;
  elements.modelSelector.value = "auto";
  elements.modelLock.checked = false;
  updateInteractionControls();
  updateModelLockControls();
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
  elements.workspaceProfileName.value = "";
  elements.trustedWorkspacePath.value = "";
  elements.workspaceDialog.showModal();
  elements.workspaceProfileName.focus();
}

function closeWorkspace() {
  elements.workspaceDialog.close();
}

async function saveWorkspace(event) {
  event.preventDefault();
  const path = elements.trustedWorkspacePath.value.trim();
  const name = elements.workspaceProfileName.value.trim()
    || path.split(/[\\/]/).filter(Boolean).at(-1)
    || "Workspace";
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
    resetConversationForWorkspaceChange();
    await refreshWorkspaceState();
    elements.workspaceSaveStatus.textContent = "Workspace adicionado e ativado";
    elements.workspaceProfileName.value = "";
    elements.trustedWorkspacePath.value = "";
  } catch (error) {
    elements.workspaceValidation.textContent = error.message;
    elements.workspaceValidation.className = "workspace-validation invalid";
    elements.workspaceSaveStatus.textContent = "Não foi possível salvar";
  }
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
}

function createSessionEntry(session) {
  const entry = document.createElement("article");
  entry.className = "session-entry";
  entry.dataset.sessionId = session.id;
  const title = document.createElement("strong");
  title.textContent = session.title;
  const metadata = document.createElement("small");
  metadata.textContent =
    `${new Date(session.updatedAt).toLocaleString()} · ${session.lastInteractionMode}`;
  const status = document.createElement("small");
  status.className = session.interrupted ? "session-interrupted" : "";
  status.textContent = [
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
  if (
    state.requestController
    || !window.confirm(
      "Retomar esta conversa? Modo Chat, aprovação manual e modelo não fixado serão restaurados."
    )
  ) {
    return;
  }

  startNewConversation();

  try {
    const session = await fetchJson(
      `/api/sessions/${encodeURIComponent(id)}/resume`,
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
    state.conversationSessionId = session.id;
    state.history = session.messages.map(
      message => ({
        role: message.role,
        content: message.content
      })
    );
    state.interactionMode = "chat";
    state.approvalPolicy = "ask";
    state.lockedModel = null;
    elements.modelSelector.value = session.selectedModel
      && state.models.some(model => model.name === session.selectedModel)
      ? session.selectedModel
      : "auto";
    renderRestoredConversation(session);
    updateInteractionControls();
    updateModelLockControls();
    updateComposerStatus();
    elements.workspaceDialog.close();
  } catch (error) {
    elements.workspaceSaveStatus.textContent = error.message;
  }
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
    startNewConversation();
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
  startNewConversation();
  await refreshSessions();
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
}

function modelOptions() {
  return state.models.map(model => ({
    value: model.name,
    label: model.name
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
      label: `${selected} (indisponível)`
    });
  }

  select.replaceChildren(
    ...normalized.map(option => {
      const element = document.createElement("option");
      element.value = option.value;
      element.textContent = option.label;
      return element;
    })
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

async function openSettings() {
  elements.settingsErrors.hidden = true;
  elements.saveStatus.textContent = "";
  elements.modelTestResult.textContent = "";
  renderSettings();
  elements.settingsDialog.showModal();

  try {
    state.modelDiagnostics = await fetchJson("/api/models/diagnostics");
    renderModelDiagnostics();
  } catch (error) {
    elements.modelContextDiagnostic.textContent = error.message;
  }
}

function closeSettings() {
  elements.settingsDialog.close();
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
    sessionHistory: state.settings.sessionHistory
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
  }
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
  document.querySelectorAll(".mode-option").forEach(
    button => {
      const active = button.dataset.mode === state.interactionMode;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", String(active));
      button.disabled = isStreaming;
    }
  );
  elements.approvalPolicy.value = state.approvalPolicy;
  elements.approvalPolicy.disabled =
    isStreaming || state.interactionMode !== "execute";
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
  const isStreaming = Boolean(state.requestController);
  const isLocked = Boolean(state.lockedModel);
  elements.modelSelector.disabled = isStreaming || isLocked;
  elements.modelLock.disabled = isStreaming
    || (!isLocked && elements.modelSelector.value === "auto");
  elements.modelLock.checked = isLocked;
  elements.composer.classList.toggle(
    "model-locked",
    isLocked
  );
}

function startNewConversation() {
  if (
    state.requestController
    && !window.confirm(
      "Cancelar a resposta atual e iniciar uma nova conversa?"
    )
  ) {
    return;
  }

  state.conversationVersion++;
  state.browserSessionId = createSessionId();
  state.requestController?.abort();
  state.history = [];
  state.conversationSessionId = null;
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
          history: state.history,
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
          role: "user",
          content: message
        },
        {
          role: "assistant",
          content: outcome.answer
        }
      );
      await refreshSessions();
    }
  } catch (error) {
    if (error.name === "AbortError") {
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
