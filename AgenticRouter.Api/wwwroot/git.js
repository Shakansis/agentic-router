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
  renderExternalAppWarnings();
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
  elements.gitInitializeQuick.disabled = true;
  elements.gitCommitQuick.disabled = true;
  elements.gitPushQuick.disabled = true;
  renderGitPanel();
  if (!elements.gitDialog.open) {
    elements.gitDialog.showModal();
  }
  elements.closeGit.focus();
  elements.gitDialog.setAttribute("aria-busy", "true");
  elements.gitPanelStatus.textContent = "Refreshing Git state…";
  try {
    await refreshGit();
    if (state.git?.state === "available") {
      await loadGitDiff(state.activeGitView);
    }
  } finally {
    elements.gitInitializeQuick.disabled = false;
    elements.gitDialog.setAttribute("aria-busy", "false");
  }
}

function closeGitPanel() {
  elements.gitDialog.close();
  elements.gitCard.focus();
}

async function refreshGitPanel() {
  elements.gitActionStatus.textContent = "Refreshing…";
  elements.gitDialog.setAttribute("aria-busy", "true");
  try {
    await refreshGit();
    renderGitPanel();
    if (state.git?.state === "available") {
      await loadGitDiff(state.activeGitView);
    }
    elements.gitActionStatus.textContent = "Git status refreshed.";
  } finally {
    elements.gitDialog.setAttribute("aria-busy", "false");
  }
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
    elements.gitActionStatus.textContent = "Saving local repository configuration…";
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
    void openChangeReview(state.latestExecutionSessionId, null, state.latestSavedExecutionReview);
  }
}

