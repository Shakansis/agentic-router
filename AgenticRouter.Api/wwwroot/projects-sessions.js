async function refreshWorkspaceState() {
  const [
    workspaceProfiles,
    workspace,
    projectProfile,
    knowledgeProviders,
    validationProfiles,
    settings
  ] =
    await Promise.all([
      fetchJson("/api/workspaces"),
      fetchJson("/api/workspace"),
      fetchJson("/api/workspace/project-profile"),
      fetchJson("/api/knowledge-providers"),
      fetchJson("/api/workspace/validation-profile"),
      fetchJson("/api/settings")
    ]);
  state.workspaceProfiles = workspaceProfiles;
  state.workspace = workspace;
  state.projectProfile = projectProfile;
  state.knowledgeProviders = knowledgeProviders;
  state.validationProfiles = validationProfiles;
  state.settings = settings;
  renderWorkspace();
  renderProjectProfile();
  renderKnowledgeSettings();
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

async function activateWorkspace(id, onActivated = null) {
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
        if (onActivated) {
          await onActivated();
        }
      } catch (error) {
        elements.workspaceSaveStatus.textContent = error.message;
      }
    }
  );
}

async function renameWorkspace(profile) {
  const name = (await showAppPrompt("Enter the new project name.", {
    title: "Rename project",
    inputLabel: "Project name",
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
    if (elements.workspaceDialog.open
      && elements.workspaceDialog.dataset.mode === "project") {
      elements.workspaceDialogTitle.textContent = name;
    }
    setPersistenceStatus(
      activeWorkspaceProfile()?.historyEnabled
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
    { title: "Remove project?", confirmLabel: "Remove", danger: true }
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

    if (elements.workspaceDialog.open
      && elements.workspaceDialog.dataset.mode === "project") {
      elements.workspaceDialog.close();
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
  persistBrowserSessionId(state.browserSessionId);
  state.conversationSessionId = null;
  state.latestExecutionSessionId = null;
  state.interactionMode = "chat";
  state.executionStrategy = "auto";
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
  const appendProfileRow = (label, value, className = "") => {
    const row = document.createElement("div");
    row.className = "project-profile-row";
    const heading = document.createElement("span");
    heading.className = "project-profile-label";
    heading.textContent = label;
    const detail = document.createElement("span");
    detail.className = `project-profile-value ${className}`.trim();
    detail.textContent = value;
    row.append(heading, detail);
    elements.projectProfileDetails.append(row);
  };
  appendProfileRow(
    "Profile type",
    profile.projectTypes.join(", ") || "No project markers",
    "project-profile-badge"
  );
  appendProfileRow(
    "Version control",
    profile.repository.isGitRepository
      ? `Git (${profile.repository.branch ?? "detached"}) · `
        + `${profile.repository.hasUncommittedChanges ? "Existing changes" : "Clean"}`
      : "Git not detected"
  );
  appendProfileRow(
    "Agents context",
    `${profile.instructionFiles.length} AGENTS.md file(s) found`
  );
  appendProfileRow(
    "Validation",
    `${profile.validationProfile?.name ?? "Not configured"} `
      + `(${profile.validationProfile?.source ?? "none"})`
  );

  if (profile.diagnostic) {
    const diagnostic = document.createElement("p");
    diagnostic.className = "verification-warning";
    diagnostic.textContent = profile.diagnostic;
    elements.projectProfileDetails.append(diagnostic);
  }
}

function selectedKnowledgeProvider() {
  const providerId = elements.knowledgeProvider.value || "anythingllm";
  return state.knowledgeProviders?.providers?.find(
    provider => provider.definition.id === providerId
  ) ?? null;
}

function renderKnowledgeSettings() {
  renderExternalAppWarnings();
  const active = activeWorkspaceProfile();
  const selection = active?.knowledge ?? {
    enabled: false,
    providerId: "anythingllm",
    libraryIds: []
  };
  elements.knowledgeProvider.value = selection.providerId ?? "anythingllm";
  const provider = selectedKnowledgeProvider();
  const available = Boolean(provider?.availability?.available);
  const configured = Boolean(provider?.availability?.configured);
  elements.knowledgeProviderStatus.textContent = state.knowledgeProviderLoadError
    ? "Knowledge provider status unavailable"
    : available
      ? `${provider.definition.displayName} available`
      : configured
        ? `${provider?.definition?.displayName ?? "AnythingLLM"} unavailable`
        : "AnythingLLM not configured";
  elements.knowledgeProviderDiagnostic.textContent =
    state.knowledgeProviderLoadError
      ?? provider?.availability?.diagnostic
      ?? (available ? `${provider.libraries.length} libraries available.` : "");
  elements.knowledgeProviderStatus.className = available
    ? "verification-ok"
    : "verification-warning";
  elements.knowledgeEnabled.checked = Boolean(selection.enabled);
  elements.knowledgeEnabled.disabled = !active;
  elements.knowledgeBaseUrl.value = provider?.baseUrl
    ?? "http://localhost:3001";
  elements.knowledgeApiKey.value = "";
  elements.knowledgeApiKey.placeholder = provider?.authenticationConfigured
    ? "Protected API key configured"
    : "AnythingLLM developer API key";
  elements.knowledgeEmbeddingGuidance.textContent =
    state.knowledgeProviders?.installation?.embeddingRecommendation ?? "";
  elements.knowledgeLibraryList.replaceChildren();

  const selectedIds = new Set(selection.libraryIds ?? []);
  const libraries = [...(provider?.libraries ?? [])];
  for (const id of selectedIds) {
    if (!libraries.some(library => library.id === id)) {
      libraries.push({ id, name: `${id} · currently unavailable` });
    }
  }

  if (libraries.length === 0) {
    const empty = document.createElement("p");
    empty.className = "inline-status";
    empty.textContent = available
      ? "AnythingLLM has no workspaces to select."
      : "Connect AnythingLLM to list libraries.";
    elements.knowledgeLibraryList.append(empty);
  } else {
    for (const library of libraries) {
      const label = document.createElement("label");
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = library.id;
      input.checked = selectedIds.has(library.id);
      input.disabled = !active;
      const name = document.createElement("span");
      name.textContent = library.name;
      name.title = library.id;
      label.append(input, name);
      elements.knowledgeLibraryList.append(label);
    }
  }

  elements.saveProjectKnowledge.disabled = !active;
}

async function refreshKnowledgeProvider() {
  elements.knowledgeSaveStatus.textContent = "Checking AnythingLLM…";
  try {
    state.knowledgeProviders = await fetchJson(
      `/api/knowledge-providers/${encodeURIComponent(elements.knowledgeProvider.value)}/refresh`,
      { method: "POST" }
    );
    state.knowledgeProviderLoadError = null;
    renderKnowledgeSettings();
    elements.knowledgeSaveStatus.textContent = selectedKnowledgeProvider()
      ?.availability?.available
        ? "AnythingLLM connection refreshed."
        : "AnythingLLM remains unavailable; see the provider diagnostic.";
  } catch (error) {
    elements.knowledgeSaveStatus.textContent = error.message;
  }
}

async function saveKnowledgeConnection() {
  elements.knowledgeSaveStatus.textContent = "Protecting key and checking connection…";
  try {
    state.knowledgeProviders = await fetchJson(
      "/api/knowledge-providers/anythingllm/connection",
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          baseUrl: elements.knowledgeBaseUrl.value.trim(),
          apiKey: elements.knowledgeApiKey.value
        })
      }
    );
    state.knowledgeProviderLoadError = null;
    state.settings = await fetchJson("/api/settings");
    renderKnowledgeSettings();
    elements.knowledgeSaveStatus.textContent = selectedKnowledgeProvider()
      ?.availability?.available
        ? "AnythingLLM connection saved and verified."
        : "Connection saved, but AnythingLLM is not currently available.";
  } catch (error) {
    const fieldErrors = error.payload?.errors
      ? Object.values(error.payload.errors).flat().join(" ")
      : "";
    elements.knowledgeSaveStatus.textContent =
      `${error.message} ${fieldErrors}`.trim();
  }
}

async function saveProjectKnowledge() {
  const active = activeWorkspaceProfile();
  if (!active) {
    return false;
  }

  const libraryIds = Array.from(
    elements.knowledgeLibraryList.querySelectorAll("input[type=checkbox]:checked")
  ).map(input => input.value);
  elements.knowledgeSaveStatus.textContent = "Saving project knowledge…";
  try {
    const updated = await fetchJson(
      `/api/workspaces/${encodeURIComponent(active.id)}/knowledge`,
      {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          enabled: elements.knowledgeEnabled.checked,
          providerId: elements.knowledgeProvider.value,
          libraryIds
        })
      }
    );
    state.workspaceProfiles.profiles = state.workspaceProfiles.profiles.map(
      profile => profile.id === updated.id ? updated : profile
    );
    renderKnowledgeSettings();
    elements.knowledgeSaveStatus.textContent = updated.knowledge.enabled
      ? "Project knowledge enabled. Retrieval will run before inference."
      : "Project knowledge disabled. Provider and library selection preserved.";
    return true;
  } catch (error) {
    elements.knowledgeSaveStatus.textContent = error.message;
    return false;
  }
}

async function changeProjectKnowledgeEnabled(event) {
  const toggle = event.currentTarget;
  const enabled = toggle.checked;
  toggle.disabled = true;
  const saved = await saveProjectKnowledge();
  if (!saved) {
    toggle.checked = !enabled;
  }
  toggle.disabled = !activeWorkspaceProfile();
}

function prepareKnowledgeSetup() {
  const prompt = state.knowledgeProviders?.installation?.executePrompt;
  if (!prompt) {
    elements.knowledgeSaveStatus.textContent =
      "AnythingLLM setup guidance is unavailable.";
    return;
  }

  closeWorkspace();
  setInteractionMode("execute");
  elements.messageInput.value = prompt;
  resizeComposer();
  updateStreamingComposerActions();
  elements.messageInput.focus();
  showToast(
    "AnythingLLM setup prepared in Execute. Review the request before sending; downloads and installers remain Host-approved.",
    "success",
    8_000
  );
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
  elements.useDetectedValidationEmpty.hidden = !detected;
  elements.validationEmptyState.closest(".validation-profile-editor")
    ?.classList.toggle("validation-profile-editor-detected", Boolean(detected));
  elements.validationSteps.replaceChildren();

  for (const step of profile?.steps ?? []) {
    addValidationStep(step);
  }

  updateValidationEmptyState();
  updateValidationCommandPreview();
}

function updateValidationEmptyState() {
  elements.validationEmptyState.hidden =
    elements.validationSteps.children.length !== 0;
  elements.validationEmptyState.closest(".validation-profile-editor")
    ?.classList.toggle(
      "validation-profile-editor-empty",
      elements.validationSteps.children.length === 0
    );
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
      updateValidationEmptyState();
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
  updateValidationEmptyState();
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
  elements.workspaceDialog.dataset.mode = "manage";
  elements.workspaceDialogEyebrow.textContent = "Projects";
  elements.workspaceDialogTitle.textContent = "Add project";
  elements.workspaceDialogPath.textContent = "";
  elements.workspaceDialogPath.hidden = true;
  elements.workspaceDialogDescription.hidden = false;
  elements.workspaceSaveStatus.textContent = "";
  showNewWorkspaceForm();
  elements.workspaceDialog.showModal();
  elements.workspaceProfileName.focus();
}

function openProjectEditor(projectId) {
  const project = state.workspaceProfiles?.profiles?.find(
    profile => profile.id === projectId
  );
  if (!project || !project.active) {
    return;
  }

  elements.runtimeDetails.open = false;
  hideNewWorkspaceForm();
  elements.workspaceDialog.dataset.mode = "project";
  elements.workspaceDialogEyebrow.textContent = "Project settings";
  elements.workspaceDialogTitle.textContent = project.name;
  elements.workspaceDialogPath.textContent = project.path;
  elements.workspaceDialogPath.hidden = false;
  elements.workspaceDialogDescription.hidden = true;
  elements.workspaceSaveStatus.textContent = "";
  renderWorkspace();
  renderProjectProfile();
  renderKnowledgeSettings();
  renderValidationProfile();
  elements.localHistorySection.open = true;
  elements.projectProfileSection.open = true;
  elements.knowledgeSection.open = true;
  elements.validationProfileSection.open = true;
  elements.workspaceDialog.showModal();
  elements.closeWorkspace.focus();
}

function closeWorkspace() {
  hideNewWorkspaceForm();
  elements.workspaceDialog.close();
}

function showNewWorkspaceForm() {
  elements.newWorkspaceSection.hidden = false;
  elements.workspaceProfileName.value = "";
  elements.trustedWorkspacePath.value = "";
  elements.workspaceValidation.textContent = "Select a trusted folder.";
  elements.workspaceValidation.className = "workspace-validation";
  elements.workspaceSaveStatus.textContent = "";
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
    await refreshWorkspaceState();
    closeWorkspace();
    showToast("Project added and activated.", "success");
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
    state.sessionsLoadError = null;
    renderSessionHistory();
    return;
  }

  elements.sessionHistory.setAttribute("aria-busy", "true");
  try {
    const [sessions, projects] = await Promise.all([
      fetchJson("/api/sessions"),
      fetchProjectConversations("")
    ]);
    state.sessions = sessions;
    state.projectSessions = projects.results;
    state.sessionsLoadError = null;
  } catch (error) {
    state.sessionsLoadError = error.message;
  } finally {
    elements.sessionHistory.setAttribute("aria-busy", "false");
  }

  renderSessionHistory();
}

function renderSessionHistory() {
  const usage = state.sessions?.usage;
  elements.historyUsage.replaceChildren();
  if (state.sessionsLoadError) {
    elements.historyUsage.textContent = `History unavailable: ${state.sessionsLoadError}`;
  } else if (!usage) {
    elements.historyUsage.textContent = "No stored sessions.";
  } else {
    const metrics = [
      ["Total sessions", `${usage.sessionCount} session(s)`],
      ["Storage used", formatBytes(usage.storageBytes)],
      [
        "Oldest entry",
        usage.oldestSessionAt
          ? new Date(usage.oldestSessionAt).toLocaleDateString(window.AgenticRouterI18n.locale)
          : "—"
      ],
      [
        "Newest entry",
        usage.newestSessionAt
          ? new Date(usage.newestSessionAt).toLocaleDateString(window.AgenticRouterI18n.locale)
          : "—"
      ]
    ];
    for (const [label, value] of metrics) {
      const metric = document.createElement("div");
      metric.className = "history-metric";
      const heading = document.createElement("span");
      heading.textContent = label;
      const detail = document.createElement("strong");
      detail.textContent = value;
      metric.append(heading, detail);
      elements.historyUsage.append(metric);
    }
  }
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

  if (state.sessionsLoadError) {
    const error = document.createElement("p");
    error.className = "project-load-error sidebar-expanded-only";
    error.setAttribute("role", "status");
    error.textContent = state.sessions
      ? "Conversation history unavailable; showing last loaded data."
      : "Conversation history unavailable. Reload to retry.";
    elements.projectList.append(error);
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

    const newConversation = document.createElement("button");
    newConversation.type = "button";
    newConversation.className = "project-new-chat-button sidebar-expanded-only";
    newConversation.textContent = "＋";
    newConversation.title = `New conversation in ${project.name}`;
    newConversation.setAttribute(
      "aria-label",
      `New conversation in ${project.name}`
    );
    newConversation.disabled = !project.available;
    newConversation.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      void startProjectConversation(project);
    });

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
    const conversationStack = document.createElement("div");
    conversationStack.className = "project-conversation-stack";
    conversationStack.setAttribute("aria-label", `Conversations in ${project.name}`);
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
      pinned.append(
        pinnedTitle,
        createProjectScrollRegion(pinnedList)
      );
      conversationStack.append(pinned);
      const recent = document.createElement("div");
      recent.id = "recent-sessions";
      recent.className = "project-conversation-list";
      for (const session of state.sessions?.recent ?? []) {
        recent.append(createProjectSessionEntry(session));
      }
      conversationStack.append(
        createProjectScrollRegion(recent)
      );
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
      archived.append(
        archivedSummary,
        createProjectScrollRegion(archivedList)
      );
      conversationStack.append(archived);
    } else {
      const conversations = document.createElement("div");
      conversations.className = "project-conversation-list";
      for (const session of projectSessions) {
        conversations.append(createProjectSessionEntry(session));
      }
      conversationStack.append(
        createProjectScrollRegion(conversations)
      );
    }
    const activeCount = project.active
      ? (state.sessions?.pinned?.length ?? 0) + (state.sessions?.recent?.length ?? 0)
      : projectSessions.length;
    if (activeCount === 0) {
      const empty = document.createElement("p");
      empty.className = "project-conversations-empty";
      empty.textContent = state.sessionsLoadError
        ? "Conversations unavailable. Refresh to retry."
        : project.historyEnabled
          ? "No conversations yet."
          : "History disabled.";
      conversationStack.append(empty);
    }
    menu.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      openProjectMenu(project, activeCount, menu);
    });
    body.append(actions, conversationStack);
    summary.append(newConversation, menu);
    details.append(summary, body);
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
      requestAnimationFrame(refreshProjectScrollIndicators);
    });
    elements.projectList.append(details);
  }

  requestAnimationFrame(refreshProjectScrollIndicators);
}

function createProjectScrollRegion(list) {
  const region = document.createElement("div");
  region.className = "project-scroll-region";
  const indicator = document.createElement("span");
  indicator.className = "project-scroll-indicator";
  indicator.setAttribute("aria-hidden", "true");
  indicator.hidden = true;
  list.addEventListener(
    "scroll",
    () => updateProjectScrollIndicator(list, indicator),
    { passive: true }
  );
  region.append(list, indicator);
  return region;
}

function updateProjectScrollIndicator(list, indicator) {
  const scrollable = list.scrollHeight > list.clientHeight + 1;
  const hasMoreBelow = scrollable
    && list.scrollTop + list.clientHeight < list.scrollHeight - 1;
  list.classList.toggle("has-more-below", hasMoreBelow);
  indicator.hidden = !hasMoreBelow;
}

function refreshProjectScrollIndicators() {
  elements.sidebar?.querySelectorAll(".project-scroll-region").forEach(
    region => {
      const list = region.querySelector(":scope > .project-conversation-list");
      const indicator = region.querySelector(":scope > .project-scroll-indicator");
      if (list && indicator) {
        updateProjectScrollIndicator(list, indicator);
      }
    }
  );
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
  elements.projectMenuEdit.disabled = !project.available;

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

async function editSelectedProject() {
  const projectId = elements.projectMenuPopover.dataset.workspaceId;
  const project = state.workspaceProfiles?.profiles?.find(
    profile => profile.id === projectId
  );
  closeProjectMenu();
  if (!project) {
    return;
  }

  if (project.active) {
    openProjectEditor(project.id);
    return;
  }

  await activateWorkspace(
    project.id,
    () => openProjectEditor(project.id)
  );
}

async function startProjectConversation(project) {
  if (project.active) {
    await requestNewConversation();
    return;
  }

  await activateWorkspace(project.id);
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
  const open = document.createElement("button");
  open.type = "button";
  open.className = "session-entry-content";
  open.setAttribute("aria-label", `Open ${session.title}`);
  const title = document.createElement("strong");
  title.textContent = session.title;
  const metadata = document.createElement("small");
  metadata.textContent = `${session.pinned ? "Pinned · " : ""}`
    + new Date(session.updatedAt).toLocaleDateString(window.AgenticRouterI18n.locale);
  open.append(title, metadata);
  open.addEventListener("click", () => openConversation(session.id, session.workspaceId));
  const details = document.createElement("button");
  details.type = "button";
  details.className = "session-details-button";
  details.textContent = "…";
  details.setAttribute("aria-label", `Details for ${session.title}`);
  details.addEventListener("click", () => openSessionDetails(session));
  entry.append(open, details);
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
  elements.closeSessionDetails.focus();

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
  elements.sessionDetailsTitle.textContent = "Conversation Details";
  elements.sessionDetailsConversationTitle.textContent = session.title;
  elements.sessionDetailsMetadata.textContent = [
    `Created ${new Date(session.createdAt).toLocaleString(window.AgenticRouterI18n.locale)}`,
    `Updated ${new Date(session.updatedAt).toLocaleString(window.AgenticRouterI18n.locale)}`,
    session.selectedModel ? `Model ${session.selectedModel}` : null
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
  elements.editSessionSummary.textContent = session.hasSummary
    ? "Edit Summary"
    : "Create Summary";
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
    empty.textContent = "No summary yet. This conversation is ready whenever you return.";
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
      && run.state === "running"
      && run.terminal !== true
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

async function showConversationHistoryLoader() {
  elements.conversationHistoryLoader.hidden = false;
  elements.messages.setAttribute("aria-busy", "true");
  await new Promise(resolve => requestAnimationFrame(
    () => requestAnimationFrame(resolve)
  ));
}

function hideConversationHistoryLoader() {
  elements.conversationHistoryLoader.hidden = true;
  elements.messages.removeAttribute("aria-busy");
}

async function openConversation(id, workspaceId = activeWorkspaceProfile()?.id) {
  if (state.requestController) {
    if (id === state.conversationSessionId && !state.readOnlyConversation) return;
    await openSessionReadOnly(
      id,
      workspaceId
    );
    return;
  }

  await requestConversationTransition(
    async () =>
    {
      await showConversationHistoryLoader();
      const activeSupervisionRun = findAttachableSupervisionRun(id);
      const nextBrowserSessionId = activeSupervisionRun?.state === "running"
        ? state.browserSessionId
        : createSessionId();

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
          `/api/sessions/${encodeURIComponent(id)}/open`,
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
        if (!session.activeChatRun) await resetCloudImagePrivacy(state.browserSessionId);
        clearConversationUi();
        state.browserSessionId = session.activeChatRun?.browserSessionId ?? nextBrowserSessionId;
        persistBrowserSessionId(state.browserSessionId);
        state.conversationSessionId = session.id;
        state.history = session.messages.map(
          message => ({
            role: message.role,
            content: message.content,
            createdAt: message.createdAt,
            diagnostic: message.diagnostic,
            hidden: message.hidden,
            contentBlocks: historyContentBlocks(message.contentBlocks),
            timeline: message.timeline
          })
        );
        state.persistedContext = session.contextMessages
          ? { messages: session.contextMessages, messageCount: session.messages.length }
          : null;
        state.persistedMessageCount = Number.isInteger(session.presentationOffset)
          ? session.messages.length : 0;
        state.conversationState = supervisionRun
          ? "running"
          : session.interrupted
          ? "interrupted"
          : session.state;
        state.interactionMode = supervisionRun
          ? "execute"
          : session.lastInteractionMode ?? "chat";
        state.executionStrategy = supervisionRun?.executionStrategy
          ?? session.lastExecutionStrategy
          ?? "auto";
        state.approvalPolicy = supervisionRun?.approvalPolicy
          ?? session.lastApprovalPolicy
          ?? "auto";
        state.harness = supervisionRun?.route?.harness
          ?? session.selectedHarness
          ?? "native";
        const resumedModel = supervisionRun?.route?.model ?? session.selectedModel;
        restoreSelectValue(
          elements.modelSelector,
          resumedModel ?? "auto",
          "Saved model"
        );
        restoreSelectValue(
          elements.harnessSelector,
          state.harness,
          "Saved harness"
        );
        const renderVersion = state.conversationVersion;
        state.autoFollow = false;
        try {
          await renderRestoredConversation(
            session,
            { suppressInterrupted: Boolean(supervisionRun || session.activeChatRun) }
          );
        } finally {
          state.autoFollow = true;
        }
        if (renderVersion !== state.conversationVersion) return;
        await refreshSelectedModelCapabilities();
        if (renderVersion !== state.conversationVersion) return;
        restoreConversationContextUsage(session);
        setPersistenceStatus(
          supervisionRun
            ? "Reconnecting"
            : session.interrupted
            ? "Interrupted"
            : "Saved locally"
        );
        updateInteractionControls();
        updateHarnessControls();
        updateComposerStatus();
        elements.messages.scrollTop = elements.messages.scrollHeight;
        updateJumpControl();
        hideConversationHistoryLoader();
        elements.messageInput.focus();
        elements.workspaceDialog.close();
        await Promise.allSettled([
          refreshSessions(),
          refreshGit()
        ]);
        if (session.activeChatRun) {
          void attachSupervisionConversation(session.activeChatRun);
        } else if (supervisionRun) {
          void attachSupervisionConversation(supervisionRun);
        }
      } catch (error) {
        setPersistenceStatus(
          "Save failed"
        );
        elements.workspaceSaveStatus.textContent =
          `${error.message} ${error.payload?.traceId ? `Trace ID: ${error.payload.traceId}` : ""}`.trim();
      } finally {
        hideConversationHistoryLoader();
      }
    }
  );
}

async function openSessionReadOnly(id, workspaceId) {
  await showConversationHistoryLoader();
  try {
    const session = await fetchJson(
      `/api/sessions/${encodeURIComponent(id)}`
        + `?workspaceId=${encodeURIComponent(workspaceId)}`
    );
    clearConversationUi();
    state.readOnlyConversation = true;
    state.conversationSessionId = session.id;
    state.history = session.messages.map(
      message => ({
        role: message.role,
        content: message.content,
        createdAt: message.createdAt,
        diagnostic: message.diagnostic,
        hidden: message.hidden,
        contentBlocks: historyContentBlocks(message.contentBlocks),
        timeline: message.timeline
      })
    );
    state.persistedContext = session.contextMessages
      ? { messages: session.contextMessages, messageCount: session.messages.length }
      : null;
    state.persistedMessageCount = Number.isInteger(session.presentationOffset)
      ? session.messages.length : 0;
    state.conversationState = session.state;
    state.interactionMode = session.lastInteractionMode ?? "chat";
    state.executionStrategy = session.lastExecutionStrategy ?? "auto";
    state.approvalPolicy = session.lastApprovalPolicy ?? "auto";
    state.harness = session.selectedHarness ?? "native";
    restoreSelectValue(
      elements.modelSelector,
      session.selectedModel ?? "auto",
      "Saved model"
    );
    restoreSelectValue(
      elements.harnessSelector,
      state.harness,
      "Saved harness"
    );
    const renderVersion = state.conversationVersion;
    state.autoFollow = false;
    try {
      await renderRestoredConversation(session);
    } finally {
      state.autoFollow = true;
    }
    if (renderVersion !== state.conversationVersion) return;
    setPersistenceStatus("Read-only");
    updateInteractionControls();
    updateHarnessControls();
    updateComposerStatus();
    await refreshSelectedModelCapabilities();
    restoreConversationContextUsage(session);
    elements.messages.scrollTop = elements.messages.scrollHeight;
    updateJumpControl();
    hideConversationHistoryLoader();
    elements.workspaceDialog.close();
    closeSessionDetails();
    showToast(
      "Conversation opened read-only while the active response continues.",
      "success"
    );
  } catch (error) {
    elements.composerStatus.textContent = error.message;
  } finally {
    hideConversationHistoryLoader();
  }
}

function restoreSelectValue(select, value, group) {
  if (!Array.from(select.options).some(option => option.value === value)) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = `${value} (unavailable · ${group})`;
    select.append(option);
  }
  select.value = value;
}

function historyContentBlocks(blocks) {
  return blocks?.map(
    block => ({
      kind: block.kind,
      content: block.content,
      id: block.id
    })
  );
}

function isPersistedContinuityMessage(message) {
  return message.role === "assistant"
    && message.content.startsWith("AGENTIC_ROUTER_PERSISTED_HISTORY_COMPACTION_V1");
}

function restoreConversationContextUsage(session) {
  const usage = session.lastContextUsage ?? session.messages.flatMap(message => message.timeline ?? [])
    .findLast(event => event.contextUsage)?.contextUsage;
  state.contextUsage = usage ?? null;
  state.contextUsageRestored = Boolean(usage);
  renderContextUsage();
}

async function renderRestoredConversation(session, options = {}) {
  elements.emptyState?.remove();
  const version = state.conversationVersion;
  const startIndex = options.startIndex ?? session.presentationOffset ?? 0;
  const endIndex = options.endIndex ?? session.messages.length;
  let renderSliceStarted = performance.now();

  if (!options.historyPage && startIndex > 0) {
    const more = document.createElement("button");
    more.type = "button";
    more.className = "secondary-button see-more-history";
    more.textContent = "See more";
    more.setAttribute("aria-label", "See more conversation history");
    let before = startIndex;
    const version = state.conversationVersion;
    more.addEventListener("click", async () => {
      more.disabled = true;
      try {
        const page = await fetchJson(
          `/api/sessions/${encodeURIComponent(session.id)}/history`
          + `?workspaceId=${encodeURIComponent(session.workspaceId)}&before=${before}`
        );
        if (version !== state.conversationVersion) return;
        const insertionPoint = more.nextSibling;
        const existing = new Set(elements.messages.children);
        const scrollHeight = elements.messages.scrollHeight;
        const scrollTop = elements.messages.scrollTop;
        state.autoFollow = false;
        page.messages.forEach((message, index) => {
          session.messages[page.startIndex + index] = message;
        });
        await renderRestoredConversation(session, {
          historyPage: true, startIndex: page.startIndex, endIndex: before
        });
        if (version !== state.conversationVersion) return;
        for (const node of [...elements.messages.children]) {
          if (!existing.has(node)) elements.messages.insertBefore(node, insertionPoint);
        }
        before = page.startIndex;
        if (before === 0) more.remove();
        elements.messages.scrollTop = scrollTop + elements.messages.scrollHeight - scrollHeight;
        updateJumpControl();
      } catch (error) {
        showToast(error.message, "error");
      } finally {
        more.disabled = false;
      }
    });
    elements.messages.append(more);
  }

  for (let index = startIndex; index < endIndex; index++) {
      const message = session.messages[index];
      if (performance.now() - renderSliceStarted >= 8) {
        await new Promise(resolve => setTimeout(resolve, 0));
        renderSliceStarted = performance.now();
      }
      if (version !== state.conversationVersion) return;
      if (message.hidden) {
        continue;
      }
      if (isPersistedContinuityMessage(message)) {
        const details = document.createElement("details");
        details.className = "activity persisted-continuity";
        const summary = document.createElement("summary");
        summary.textContent = "Earlier history · compacted continuity context";
        const content = document.createElement("pre");
        content.className = "work-action-preview";
        content.textContent = message.content;
        details.append(summary, content);
        elements.messages.append(details);
        continue;
      }
      if (message.role === "user") {
        appendUserMessage(
          message.content,
          index,
          [],
          message.createdAt
        );
        if ((message.timeline?.length ?? 0) > 0) {
          const assistant = appendAssistantMessage({
            modelSelectionOrigin: timelineModelSelectionOrigin(
              message.timeline
            ),
            selectedModel: session.selectedModel
          });
          const outcome = await replayConversationTimeline(
            message.timeline,
            assistant
          );
          assistant.rawAnswer = outcome.answer;
          assistant.copyButton.disabled = !outcome.answer;
        }
      } else if (message.role === "assistant") {
        const timeline = message.timeline ?? [];
        const assistant = appendAssistantMessage({
          modelSelectionOrigin: timelineModelSelectionOrigin(timeline),
          selectedModel: session.selectedModel
        });
        cancelAnimationFrame(assistant.clockFrame);
        assistant.progress.hidden = true;
        assistant.runningIndicator.hidden = true;
        assistant.details.open = false;
        assistant.summary.textContent = message.diagnostic?.terminalState === "failed"
          ? "Failed"
          : "Completed";
        const blocks = message.contentBlocks ?? [];
        if (timeline.length > 0) {
          await replayConversationTimeline(
            timeline,
            assistant
          );
        } else if (blocks.length > 0) {
          for (const block of blocks) {
            if (block.kind === "reasoning") {
              appendAssistantReasoning(
                assistant,
                block.content,
                block.id ?? null
              );
              closeAssistantReasoning(assistant);
            } else if (block.kind === "response") {
              appendAssistantResponse(
                assistant,
                block.content,
                block.renderedHtml ?? "",
                block.id ?? null,
                message.content
              );
              closeAssistantResponse(assistant);
            }
          }
        } else {
          const response = ensureAssistantResponse(
            assistant,
            `history:${index}`
          );
          renderAssistantResponse(
            assistant,
            response,
            message.renderedHtml ?? "",
            message.content,
            message.content
          );
          closeAssistantResponse(assistant);
        }
        assistant.answer.classList.remove("pending");
        assistant.rawAnswer = message.content;
        assistant.copyButton.disabled = false;
        if (message.diagnostic && timeline.length === 0) {
          const failed = message.diagnostic.terminalState === "failed";
          finishActivity(
            assistant,
            terminalActivitySummary(
              failed ? "Failed" : "Completed",
              null,
              message.diagnostic,
              assistant.selectedModel
            ),
            failed
          );
          if (failed) {
            assistant.answer.classList.add("error");
            addTraceDiagnosticActions(
              assistant,
              message.diagnostic
            );
          }
        }
      }
  }

  if (options.historyPage || version !== state.conversationVersion) return;

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
    notice.textContent = session.messages.some(isPersistedContinuityMessage)
      ? "Older turns were compacted into continuity context. Their original presentation is no longer available in this saved history."
      : session.transcriptId
        ? "Model context was compacted. The original conversation history is preserved."
      : "Older messages remain visible but will be omitted from the model's next context.";
    elements.messages.append(notice);
  }

  if (session.executionReviews.length > 0) {
    const review = session.executionReviews.at(-1);
    state.latestExecutionSessionId = review.summary.id;
    state.latestSavedExecutionReview = review;
  }
}

function timelineModelSelectionOrigin(timeline) {
  return timeline.some(
    streamEvent => streamEvent.type === "model.explicit-selected"
  )
    ? "user"
    : "agent";
}

async function replayConversationTimeline(timeline, assistant) {
  assistant.replayingHistory = true;
  let outcome;
  try {
    outcome = await consumeEventStream(null, assistant, {
      historical: true,
      events: timeline,
      conversationVersion: state.conversationVersion
    });
  } finally {
    assistant.replayingHistory = false;
  }
  assistant.sessionHeader.classList.remove("is-live");
  assistant.container.querySelectorAll(
    ".action-approval button, .recovery-decision button"
  ).forEach(
    button => {
      button.disabled = true;
    }
  );
  return outcome;
}

async function restoreLiveChatRun() {
  let saved;
  try { saved = JSON.parse(localStorage.getItem("agentic-router.live-chat-run") ?? "null"); } catch { return; }
  if (!saved?.id || state.requestController) return;
  try {
    const run = await fetchJson(`/api/chat/runs/${encodeURIComponent(saved.id)}`);
    if (run.conversationSessionId && run.historyAvailable) {
      await openConversation(run.conversationSessionId, saved.workspaceId);
    } else {
      state.browserSessionId = run.browserSessionId;
      state.conversationSessionId = run.conversationSessionId;
      persistBrowserSessionId(state.browserSessionId);
      state.interactionMode = run.interactionMode;
      state.harness = run.harness;
      state.approvalPolicy = run.approvalPolicy;
      state.executionStrategy = run.executionStrategy;
      restoreSelectValue(elements.modelSelector, run.model, "Saved model");
      restoreSelectValue(elements.harnessSelector, run.harness, "Saved harness");
      appendUserMessage(run.message, 0);
      state.history = [{ role: "user", content: run.message }];
      void attachSupervisionConversation(run);
    }
    if (run.completed) localStorage.removeItem("agentic-router.live-chat-run");
  } catch (error) {
    // A Host restart has no surviving in-memory execution. Saved history remains authoritative.
    if (error.status === 404 || error.message?.includes("404")) {
      localStorage.removeItem("agentic-router.live-chat-run");
      if (saved.conversationSessionId) await openConversation(saved.conversationSessionId, saved.workspaceId);
    } else showToast(`Could not reconnect: ${error.message}`, "error");
  }
}

function rememberLiveChatRun(id, conversationSessionId = state.conversationSessionId) {
  localStorage.setItem("agentic-router.live-chat-run", JSON.stringify({
    id, conversationSessionId, workspaceId: activeWorkspaceProfile()?.id
  }));
}

async function* reconnectChatEvents(stream, id, signal) {
  let sequence = 0;
  let attempts = 0;
  while (true) {
    try {
      for await (const event of readStreamEvents(stream, 75_000)) {
        if (event.chatRunSequence && event.chatRunSequence <= sequence) continue;
        sequence = event.chatRunSequence ?? sequence;
        attempts = 0;
        if (event.conversationSessionId) rememberLiveChatRun(id, event.conversationSessionId);
        yield event;
        if (["response.completed", "error", "request.cancelled"].includes(event.type)) {
          const saved = JSON.parse(localStorage.getItem("agentic-router.live-chat-run") ?? "null");
          if (saved?.id === id) localStorage.removeItem("agentic-router.live-chat-run");
          return;
        }
      }
    } catch (error) {
      if (signal.aborted) throw error;
    }
    if (signal.aborted) throw new DOMException("Detached", "AbortError");
    if (++attempts > 3) throw new Error("Connection lost. The Host keeps this request running; reopen the conversation to reconnect.");
    await new Promise(resolve => setTimeout(resolve, attempts * 250));
    try {
      const response = await fetch(`/api/chat/runs/${encodeURIComponent(id)}/stream?afterSequence=${sequence}`, { signal });
      if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);
      stream = response.body;
    } catch (error) {
      if (signal.aborted) throw error;
      stream = new Blob([]).stream();
    }
  }
}

async function attachSupervisionConversation(run) {
  const chatRunId = run.id ?? null;
  if (chatRunId) rememberLiveChatRun(chatRunId, run.conversationSessionId);
  const conversationVersion = state.conversationVersion;
  const controller = new AbortController();
  undockExecutionPlans();
  const assistant = appendAssistantMessage({
    modelSelectionOrigin: "user",
    selectedModel: run.route?.model ?? run.model
  });
  assistant.chatRunId = chatRunId;
  state.requestController = controller;
  state.activeAssistant = assistant;
  state.activeHarness = run.route?.harness ?? run.harness ?? state.harness;
  setStreamingState(true);
  updateComposerStatus();

  try {
    const response = await fetch(
      chatRunId ? `/api/chat/runs/${encodeURIComponent(chatRunId)}/stream?afterSequence=0`
        : `/api/chat/supervision/${encodeURIComponent(run.runId)}/stream?afterSequence=0`,
      { signal: controller.signal }
    );
    if (!response.ok || !response.body) {
      throw new Error(`HTTP ${response.status}`);
    }

    assistant.chatAccepted = Boolean(chatRunId);
    const outcome = await consumeEventStream(response.body, assistant, chatRunId
      ? { cooperative: true, events: reconnectChatEvents(response.body, chatRunId, controller.signal) } : {});
    if (outcome.terminalState && state.conversationVersion === conversationVersion) {
      state.history.push({
        role: "assistant",
        content: outcome.answer,
        createdAt: new Date().toISOString(),
        contentBlocks: outcome.contentBlocks,
        timeline: outcome.timeline
      });
      state.conversationState = outcome.terminalState;
      setPersistenceStatus(chatRunId && !run.historyAvailable ? "History disabled" : "Saved locally");
      if (chatRunId && run.conversationSessionId && run.historyAvailable) {
        const saved = await fetchJson(`/api/sessions/${encodeURIComponent(run.conversationSessionId)}?workspaceId=${encodeURIComponent(activeWorkspaceProfile().id)}`);
        state.history = saved.messages;
        state.persistedMessageCount = saved.messages.length;
      }
      await refreshSessions();
      await refreshGit();
    }
  } catch (error) {
    if (error.name !== "AbortError") {
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
      assistant.answer.textContent ||= "Could not reattach. The Host may still be running this request.";
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
    open.textContent = "Open conversation";
    open.addEventListener(
      "click",
      async () => {
        closeSessionSearch();
        await openConversation(result.id, result.workspaceId);
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
    "Loading summary…";
  fillSessionSummary(null);
  elements.deleteSessionSummary.disabled = true;
  elements.sessionSummaryDialog.setAttribute("aria-busy", "true");
  elements.sessionSummaryDialog.showModal();

  try {
    const summary = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary`
    );
    if (state.summarySession?.id !== session.id) {
      return;
    }
    fillSessionSummary(summary?.content ?? null);
    elements.deleteSessionSummary.disabled = !summary;
    elements.sessionSummaryStatus.textContent =
      "The summary is separate from the original messages.";
  } catch (error) {
    if (state.summarySession?.id !== session.id) {
      return;
    }
    fillSessionSummary(null);
    elements.sessionSummaryStatus.textContent = error.message;
  }

  await refreshSessionSummaryEstimate();
  elements.sessionSummaryDialog.setAttribute("aria-busy", "false");
  elements.sessionSummaryObjective.focus();
}

function closeSessionSummary() {
  state.summarySession = null;
  state.summaryEstimate = null;
  elements.sessionSummaryDialog.setAttribute("aria-busy", "false");
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
    const estimate = await fetchJson(
      `/api/sessions/${encodeURIComponent(session.id)}/summary/estimate`
        + `?model=${encodeURIComponent(model)}`
    );
    if (state.summarySession?.id !== session.id
      || elements.sessionSummaryModel.value !== model) {
      return;
    }
    state.summaryEstimate = estimate;
    elements.sessionSummaryEstimate.textContent =
      `${providerLabel(estimate.provider)} · ${estimate.model} · `
      + `up to ${formatInteger(estimate.estimatedInputTokens)} estimated tokens · `
      + `${estimate.includedMessages} messages included`
      + `${estimate.omittedMessages
        ? ` · ${estimate.omittedMessages} omitted`
        : ""}`;
  } catch (error) {
    if (state.summarySession?.id !== session.id
      || elements.sessionSummaryModel.value !== model) {
      return;
    }
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

function renderExternalAppWarnings() {
  const warnings = [];
  const add = (id, name, diagnostic, target) => warnings.push({
    id, name, diagnostic: diagnostic || "Unavailable", target
  });
  if (state.setup?.ollama.available === false) {
    add("ollama-local", "Ollama", state.setup.ollama.diagnostic, "general");
  }
  for (const provider of state.knowledgeProviders?.providers ?? []) {
    if (provider.availability.configured && !provider.availability.available) {
      add(provider.definition.id, provider.definition.displayName,
        provider.availability.diagnostic, "knowledge");
    }
  }
  if (state.knowledgeProviderLoadError) {
    add("knowledge-status", "Knowledge provider",
      state.knowledgeProviderLoadError, "knowledge");
  }
  for (const diagnostic of state.runtimeProfiles?.diagnostics ?? []) {
    if (diagnostic.code === "runtime-provider-unavailable"
      && warnings.some(warning => warning.id === "ollama-local")) {
      continue;
    }
    add(`runtime:${diagnostic.code}:${diagnostic.model ?? ""}`,
      diagnostic.model ? `Runtime · ${diagnostic.model}` : "Ollama runtime",
      diagnostic.message,
      diagnostic.code === "model-gpu-unavailable" ? "models-routing" : "general");
  }
  for (const harness of state.harnesses) {
    if (!harness.availability.available) {
      add(harness.definition.id, harness.definition.displayName,
        harness.availability.message, "harnesses");
    }
  }
  for (const provider of state.providerHealth?.providers ?? []) {
    if (provider.enabled
      && ["degraded", "unavailable"].includes(provider.connectionState)
      && !warnings.some(warning => warning.id === provider.providerId)) {
      add(provider.providerId, provider.displayName,
        provider.currentDiagnostic, "providers");
    }
  }
  if (state.webSearch?.hasKey && state.webSearch.state !== "available") {
    add(state.webSearch.provider, state.webSearch.displayName,
      state.webSearch.diagnostic, "providers");
  }
  if (activeWorkspaceProfile() && state.git?.state === "unavailable") {
    add("git", "Git", state.git.diagnostic, "git");
  }

  const signature = JSON.stringify(warnings);
  if (elements.externalAppWarnings.dataset.signature === signature) {
    return;
  }
  elements.externalAppWarnings.dataset.signature = signature;
  elements.externalAppWarnings.hidden = warnings.length === 0;
  elements.externalAppWarningsSummary.textContent =
    `Apps and devices · ${warnings.length} warning${warnings.length === 1 ? "" : "s"}`;
  elements.externalAppWarningsList.replaceChildren();
  for (const warning of warnings) {
    const item = document.createElement("li");
    item.dataset.externalApp = warning.id;
    const title = document.createElement("strong");
    title.textContent = warning.name;
    const diagnostic = document.createElement("p");
    diagnostic.textContent = warning.diagnostic;
    const configure = document.createElement("button");
    configure.type = "button";
    configure.className = "secondary-button";
    configure.textContent = "Open settings";
    configure.setAttribute("aria-label", `Open ${warning.name} settings`);
    configure.addEventListener("click", () => {
      if (warning.target === "knowledge") {
        const active = activeWorkspaceProfile();
        if (active) {
          openProjectEditor(active.id);
          elements.knowledgeSection.open = true;
          elements.knowledgeBaseUrl.focus();
        } else {
          openWorkspace();
        }
      } else if (warning.target === "git") {
        void openGitPanel();
      } else {
        void openSettings(warning.target);
      }
    });
    item.append(title, diagnostic, configure);
    elements.externalAppWarningsList.append(item);
  }
  requestAnimationFrame(refreshProjectScrollIndicators);
}

function renderProviderHealth() {
  renderExternalAppWarnings();
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

