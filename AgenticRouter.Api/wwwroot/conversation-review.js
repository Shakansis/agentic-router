async function openChangeReview(executionSessionId, focusRelativePath = null, savedReview = null) {
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
    if (error.status === 404 && savedReview?.summary.id === executionSessionId) {
      // Older persisted reviews may have no live execution session after restart.
      state.activeReview = savedReview;
      renderChangeReview(savedReview, focusRelativePath);
    } else {
      elements.changeReviewBody.textContent = error.message;
    }
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

async function restorePendingUserInput() {
  const pending = await fetchJson(
    `/api/user-input/pending?browserSessionId=${encodeURIComponent(state.browserSessionId)}`
  );
  const request = pending.at(-1) ?? null;
  if (request) {
    activateUserInput(request);
  }
}

function activateUserInput(request) {
  state.activeUserInput = {
    ...request,
    browserSessionId: state.browserSessionId,
    currentQuestionIndex: Math.max(
      0,
      Math.min(request.questions.length - 1, request.currentQuestionIndex ?? 0)
    ),
    answersByQuestion: Object.fromEntries(
      (request.answers ?? []).map(answer => [answer.questionId, answer.answer])
    )
  };
  renderActiveUserInput();
}

function clearActiveUserInput() {
  state.activeUserInput = null;
  elements.userInputPanel.hidden = true;
  elements.userInputPanel.replaceChildren();
  elements.messageInput.value = "";
  elements.messageInput.placeholder = "Send a message…";
  elements.messageInput.readOnly = false;
  elements.messageInput.removeAttribute("aria-invalid");
  elements.sendButtonLabel.textContent = "Send";
  resizeComposer();
  updateComposerStatus();
}

function renderActiveUserInput() {
  const request = state.activeUserInput;
  if (!request) {
    clearActiveUserInput();
    return;
  }
  const index = request.currentQuestionIndex;
  const question = request.questions[index];
  const panel = elements.userInputPanel;
  panel.replaceChildren();
  panel.hidden = false;
  panel.dataset.userInputId = request.id;
  panel.dataset.resumable = String(request.resumable !== false);

  const header = document.createElement("header");
  header.className = "user-input-header";
  const heading = document.createElement("div");
  heading.className = "user-input-heading";
  const label = document.createElement("span");
  label.className = "user-input-label";
  label.textContent = question.header;
  const text = document.createElement("strong");
  text.textContent = question.question;
  heading.append(label, text);

  const navigation = document.createElement("div");
  navigation.className = "user-input-navigation";
  const previous = document.createElement("button");
  previous.type = "button";
  previous.className = "user-input-nav";
  previous.textContent = "←";
  previous.title = "Previous question";
  previous.setAttribute("aria-label", "Previous question");
  previous.disabled = index === 0;
  const position = document.createElement("span");
  position.textContent = `${index + 1} / ${request.questions.length}`;
  const next = document.createElement("button");
  next.type = "button";
  next.className = "user-input-nav";
  next.textContent = index === request.questions.length - 1 ? "✓" : "→";
  next.title = index === request.questions.length - 1
    ? "Submit all answers"
    : "Next question";
  next.setAttribute("aria-label", next.title);
  next.disabled = request.resumable === false
    ? index === request.questions.length - 1
    : false;
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "user-input-close";
  cancel.textContent = "×";
  cancel.title = "Cancel questions";
  cancel.setAttribute("aria-label", "Cancel questions");
  navigation.append(previous, position, next, cancel);
  header.append(heading, navigation);
  panel.append(header);

  if (request.resumable === false) {
    const expired = document.createElement("p");
    expired.className = "user-input-expired";
    expired.textContent = "The questions were restored, but the underlying harness request cannot resume after the Host restart.";
    panel.append(expired);
  } else if (question.options.length > 0) {
    const options = document.createElement("div");
    options.className = "user-input-options";
    question.options.forEach((option, optionIndex) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "user-input-option";
      button.dataset.selected = String(
        request.answersByQuestion[question.id] === option.label
      );
      const key = document.createElement("span");
      key.className = "user-input-option-key";
      key.textContent = String.fromCharCode(65 + optionIndex);
      const content = document.createElement("span");
      const optionLabel = document.createElement("strong");
      optionLabel.textContent = option.label;
      content.append(optionLabel);
      if (option.description) {
        const description = document.createElement("small");
        description.textContent = option.description;
        content.append(description);
      }
      button.append(key, content);
      button.addEventListener("click", async () => {
        elements.messageInput.value = option.label;
        await answerActiveUserInput();
      });
      options.append(button);
    });
    panel.append(options);
  }

  previous.addEventListener("click", () => {
    if (request.resumable === false) {
      request.currentQuestionIndex = index - 1;
      renderActiveUserInput();
    } else {
      void navigateUserInput(index - 1, false);
    }
  });
  next.addEventListener("click", () => {
    if (request.resumable === false) {
      request.currentQuestionIndex = Math.min(request.questions.length - 1, index + 1);
      renderActiveUserInput();
    } else {
      void answerActiveUserInput();
    }
  });
  cancel.addEventListener("click", cancelActiveUserInput);
  elements.messageInput.value = request.answersByQuestion[question.id] ?? "";
  elements.messageInput.placeholder = request.resumable === false
    ? "This harness request can no longer resume"
    : "Type a custom answer…";
  elements.messageInput.readOnly = request.resumable === false;
  elements.sendButtonLabel.textContent = index === request.questions.length - 1
    ? "Submit"
    : "Next";
  resizeComposer();
  updateComposerStatus();
  if (request.resumable !== false) elements.messageInput.focus();
}

function currentUserInputAnswers(request) {
  return request.questions.flatMap(question => {
    const answer = request.answersByQuestion[question.id]?.trim();
    return answer ? [{ questionId: question.id, answer }] : [];
  });
}

function captureCurrentUserInputAnswer(required) {
  const request = state.activeUserInput;
  if (!request) return false;
  const question = request.questions[request.currentQuestionIndex];
  const answer = elements.messageInput.value.trim();
  if (!answer) {
    delete request.answersByQuestion[question.id];
    if (required) {
      elements.messageInput.setAttribute("aria-invalid", "true");
      elements.composerStatus.textContent = "Answer this question before continuing";
      return false;
    }
  } else {
    request.answersByQuestion[question.id] = answer;
    elements.messageInput.removeAttribute("aria-invalid");
  }
  return true;
}

async function persistUserInputDraft(targetIndex) {
  const request = state.activeUserInput;
  if (!request) return;
  await fetchJson(`/api/user-input/${encodeURIComponent(request.id)}/draft`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      browserSessionId: request.browserSessionId,
      executionSessionId: request.executionSessionId,
      currentQuestionIndex: targetIndex,
      answers: currentUserInputAnswers(request)
    })
  });
}

async function navigateUserInput(targetIndex, requireAnswer) {
  const request = state.activeUserInput;
  if (!request || request.resumable === false) return;
  if (!captureCurrentUserInputAnswer(requireAnswer)) return;
  const bounded = Math.max(0, Math.min(request.questions.length - 1, targetIndex));
  try {
    await persistUserInputDraft(bounded);
    request.currentQuestionIndex = bounded;
    renderActiveUserInput();
  } catch (error) {
    showToast(error.message);
  }
}

async function answerActiveUserInput() {
  const request = state.activeUserInput;
  if (!request) return;
  if (request.resumable === false) {
    showToast("The underlying harness request cannot be resumed.");
    return;
  }
  if (!captureCurrentUserInputAnswer(true)) return;
  if (request.currentQuestionIndex < request.questions.length - 1) {
    await navigateUserInput(request.currentQuestionIndex + 1, true);
    return;
  }
  const answers = currentUserInputAnswers(request);
  if (answers.length !== request.questions.length) {
    const missing = request.questions.findIndex(
      question => !request.answersByQuestion[question.id]?.trim()
    );
    await navigateUserInput(missing, false);
    elements.composerStatus.textContent = "Answer every question before submitting";
    return;
  }
  elements.sendButton.disabled = true;
  try {
    await fetchJson(`/api/user-input/${encodeURIComponent(request.id)}/decision`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        browserSessionId: request.browserSessionId,
        executionSessionId: request.executionSessionId,
        answers,
        cancelled: false
      })
    });
    clearActiveUserInput();
  } catch (error) {
    showToast(error.message);
  } finally {
    elements.sendButton.disabled = false;
  }
}

async function cancelActiveUserInput() {
  const request = state.activeUserInput;
  if (!request) return;
  try {
    await fetchJson(`/api/user-input/${encodeURIComponent(request.id)}/decision`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        browserSessionId: request.browserSessionId,
        executionSessionId: request.executionSessionId,
        answers: currentUserInputAnswers(request),
        cancelled: true
      })
    });
    clearActiveUserInput();
  } catch (error) {
    showToast(error.message);
  }
}

function addApprovalActivity(assistant, streamEvent, historical = false) {
  const action = streamEvent.localAction;
  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
  const row = document.createElement("details");
  row.className = "activity-row action-approval";
  row.classList.toggle("historical-approval", historical);
  const terminalExecution = action.tool === "run_process";
  row.classList.toggle("terminal-execution-approval", terminalExecution);
  row.open = true;
  row.dataset.eventType = streamEvent.type;
  row.dataset.tool = action.tool;
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
  toggle.textContent = terminalExecution ? "🛡" : "›";
  toggle.setAttribute(
    "aria-hidden",
    "true"
  );
  const summaryContent = document.createElement("span");
  summaryContent.className = "action-approval-summary-content";
  const title = document.createElement("strong");
  title.textContent = terminalExecution
    ? terminalExecutionApprovalTitle(action)
    : action.summary;
  const status = document.createElement("span");
  status.className = "approval-status";
  status.textContent = historical
    ? "Expired · no longer actionable"
    : "Waiting for decision";
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
  const downloadChoices = createDownloadConflictControls(action);
  if (downloadChoices.host) {
    content.append(downloadChoices.host);
    downloadChoices.host.querySelectorAll("select").forEach(select => {
      select.disabled = historical;
    });
  }
  const decisionInput = downloadChoices.input ?? command.input;

  if (action.canRememberApproval) {
    const warning = document.createElement("p");
    warning.className = "approval-boundary-warning";
    warning.textContent = "This process runs with the Host user's authority and may access files, processes, the registry, or the network outside the trusted workspace.";
    content.append(warning);
  }

  if (historical) {
    const notice = document.createElement("p");
    notice.className = "historical-approval-notice";
    notice.textContent = "This approval belongs to an earlier turn and cannot be executed now.";
    content.append(notice);
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
  approve.textContent = downloadChoices.input
    ? "Continue with choices"
    : "Approve";
  const remember = action.canRememberApproval
    ? document.createElement("button")
    : null;
  if (remember) {
    remember.className = "primary-button";
    remember.type = "button";
    remember.textContent = "Always allow exact command";
  }
  approve.disabled = historical;
  reject.disabled = historical;
  if (remember) {
    remember.disabled = historical;
  }
  if (terminalExecution) {
    controls.append(approve, reject);
  } else {
    controls.append(reject, approve);
  }
  if (remember) {
    controls.append(remember);
  }
  content.append(controls);
  row.append(summary, content);
  row.addEventListener(
    "toggle",
    () => {
      const label = row.querySelector(".terminal-output-toggle");
      if (label) {
        label.textContent = row.open ? "Hide output" : "View output";
      }
    }
  );
  assistant.workActivity.hidden = false;
  ensureWorkNarrative(
    assistant,
    historical
      ? "This saved turn ended before the decision was completed."
      : "I need your decision to continue this change."
  );
  assistant.workActivity.append(row);

  if (decisionInput) {
    row.dataset.editableText = decisionInput.value;
    decisionInput.readOnly = historical;
    decisionInput.disabled = historical;
    decisionInput.addEventListener(
      "input",
      () => {
        decisionInput.removeAttribute("aria-invalid");
        status.textContent = decisionInput.value
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
      decisionInput,
      false,
      remember
    )
  );
  remember?.addEventListener(
    "click",
    () => decideAction(
      action.actionId,
      action.executionSessionId,
      true,
      approve,
      reject,
      status,
      row,
      decisionInput,
      true,
      remember
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
      decisionInput,
      false,
      remember
    )
  );
}

function createDownloadConflictControls(action) {
  const conflicts = action.downloadConflicts;
  if (!Array.isArray(conflicts) || conflicts.length === 0) {
    return { host: null, input: null };
  }
  const host = document.createElement("div");
  host.className = "download-conflict-decisions";
  const heading = document.createElement("strong");
  heading.textContent = "Existing files: choose what to do with each one";
  const list = document.createElement("div");
  list.className = "download-conflict-list";
  const input = document.createElement("textarea");
  input.hidden = true;
  const choices = new Map();
  const refresh = () => {
    input.value = JSON.stringify({
      decisions: [...choices].map(([path, choice]) => ({ path, choice }))
    });
    input.dispatchEvent(new Event("input"));
  };
  for (const conflict of conflicts) {
    const row = document.createElement("label");
    row.className = "download-conflict-row";
    const description = document.createElement("span");
    description.textContent = `${conflict.relativePath} (${conflict.bytes} bytes)`;
    const select = document.createElement("select");
    select.setAttribute("aria-label", `Existing ${conflict.relativePath}`);
    for (const [value, label] of [
      ["keep", "Keep existing"],
      ["replace", "Replace with download"]
    ]) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      select.append(option);
    }
    choices.set(conflict.relativePath, "keep");
    select.addEventListener("change", () => {
      choices.set(conflict.relativePath, select.value);
      refresh();
    });
    row.append(description, select);
    list.append(row);
  }
  refresh();
  host.append(heading, list, input);
  return { host, input };
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

function terminalCommandText(action, approval = null) {
  return (
    action.editableText
    ?? approval?.dataset.editableText
    ?? action.preview
    ?? action.summary
    ?? "command"
  ).trim();
}

function terminalExecutionApprovalTitle(action) {
  const command = terminalCommandText(action);
  const match = /^(?:"([^"]+)"|(\S+))/.exec(command);
  const executable = (match?.[1] ?? match?.[2] ?? "command")
    .split(/[\\/]/)
    .at(-1)
    ?.replace(/\.exe$/i, "")
    ?? "command";
  const label = {
    node: "Node.js",
    powershell: "PowerShell",
    pwsh: "PowerShell",
    cmd: "Command Prompt",
    dotnet: ".NET"
  }[executable.toLowerCase()] ?? executable;
  return `Action required: allow ${label} execution`;
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

  const terminalExecution = approval.classList.contains(
    "terminal-execution-approval"
  );
  if (title && action.summary) {
    title.textContent = terminalExecution
      ? terminalExecutionApprovalTitle(action)
      : action.summary;
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
    if (terminalExecution) {
      renderCompletedTerminalApproval(approval, action);
    }
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

  if (terminalExecution && action.state === "completed") {
    approval.open = false;
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
  response.open = !failed && approval.classList.contains(
    "terminal-execution-approval"
  );
  response.querySelector("summary").textContent = failed
    ? "Execution · failed"
    : "Execution · completed";
  response.querySelector(".action-response-output").textContent =
    action.resultOutput || "Completed without textual output.";
}

function renderCompletedTerminalApproval(approval, action) {
  approval.classList.add("terminal-execution-completed");
  const summary = approval.querySelector(":scope > summary");
  const time = summary?.querySelector(".activity-time");
  const icon = summary?.querySelector(".action-approval-toggle");
  const content = summary?.querySelector(".action-approval-summary-content");
  const title = content?.querySelector("strong");
  const status = content?.querySelector(".approval-status");
  if (!summary || !time || !icon || !content || !title || !status) {
    return;
  }

  time.textContent = "Completed";
  icon.textContent = "✓";
  title.textContent = "Executed command:";
  status.textContent = terminalCommandText(action, approval);
  status.classList.add("terminal-command-summary");
  const outputToggle = document.createElement("span");
  outputToggle.className = "terminal-output-toggle";
  outputToggle.textContent = "View output";
  content.replaceChildren(title, status, outputToggle);
  summary.setAttribute(
    "aria-label",
    `Executed command: ${status.textContent}. View output.`
  );
}

async function decideAction(
  actionId,
  executionSessionId,
  approved,
  approveButton,
  rejectButton,
  status,
  approval,
  input,
  rememberForWorkspace = false,
  rememberButton = null
) {
  approveButton.disabled = true;
  rejectButton.disabled = true;
  if (rememberButton) {
    rememberButton.disabled = true;
  }
  if (input) {
    input.disabled = true;
  }
  approval.querySelectorAll(".download-conflict-row select").forEach(select => {
    select.disabled = true;
  });
  status.textContent = approved
    ? rememberForWorkspace
      ? "Approving and remembering…"
      : "Approving…"
    : "Rejecting…";

  try {
    const decision = await fetchJson(
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
          rememberForWorkspace,
          editedText: approved
            && input
            && input.value !== (approval.dataset.editableText ?? "")
            ? input.value
            : null
        })
      }
    );
    if (decision.rememberedForWorkspace) {
      state.workspaceProfiles = await fetchJson("/api/workspaces");
      renderSettingsSummaries();
    }
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
    if (error.status === 404) {
      status.textContent = "Expired · no longer actionable";
      approval.classList.add("historical-approval");
      approval.querySelector(".approval-controls")?.remove();
      if (input) {
        input.readOnly = true;
        input.disabled = false;
      }
      if (!approval.querySelector(".historical-approval-notice")) {
        const notice = document.createElement("p");
        notice.className = "historical-approval-notice";
        notice.textContent = error.message;
        approval.querySelector(".action-approval-content")?.append(notice);
      }
      showToast(error.message);
      return;
    }
    status.textContent = approved && input
      ? "Invalid change"
      : error.message;
    if (input) {
      input.disabled = false;
      input.setAttribute("aria-invalid", "true");
    }
    approval.querySelectorAll(".download-conflict-row select").forEach(select => {
      select.disabled = false;
    });
    showToast(error.message);
    approveButton.disabled = false;
    rejectButton.disabled = false;
    if (rememberButton) {
      rememberButton.disabled = false;
    }
  }
}

function addRecoveryDecisionActivity(assistant, streamEvent, historical = false) {
  const recovery = streamEvent.recoveryDecision;
  closeAssistantReasoning(assistant);
  closeAssistantResponse(assistant);
  const row = document.createElement("details");
  row.className = "activity-row action-approval recovery-decision";
  row.classList.toggle("historical-approval", historical);
  row.open = true;
  row.dataset.eventType = streamEvent.type;
  row.dataset.checkpointId = recovery.checkpointId;
  row.dataset.executionSessionId = recovery.executionSessionId;
  row.setAttribute("aria-label", "Recovery decision required");
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
  status.textContent = historical
    ? "Expired · no longer actionable"
    : "Choose an alternative";
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
      button.disabled = historical;
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
  if (historical) {
    const notice = document.createElement("p");
    notice.className = "historical-approval-notice";
    notice.textContent = "This recovery decision belongs to an earlier turn and cannot be applied now.";
    content.append(notice);
  }
  content.append(message, reason, controls);
  row.append(summary, content);
  assistant.workActivity.hidden = false;
  ensureWorkNarrative(
    assistant,
    historical
      ? "This saved turn ended without a recovery decision."
      : "Automatic recovery has ended; choose how the task should continue."
  );
  assistant.answer.insertAdjacentElement("afterend", row);
  if (!historical && !state.readOnlyConversation) {
    assistant.recoveryPreviousAutoFollow = state.autoFollow;
    state.autoFollow = false;
    updateJumpControl();
    requestAnimationFrame(() => {
      if (row.isConnected && !row.dataset.decision) {
        row.scrollIntoView({ block: "center" });
      }
    });
  }
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
  assistant.runningIndicator.hidden = true;
  renderTerminalActivitySummary(assistant.summary, summary);
  assistant.details.dataset.terminal = "true";
  assistant.details.open = keepOpen;
  const status = assistant.sessionHeader.querySelector("strong");
  if (status && !["completed", "failed", "blocked", "cancelled"].includes(status.textContent.toLowerCase())) {
    // A transport failure/cancellation may arrive without a final session snapshot.
    status.textContent = summary.startsWith("Failed") ? "failed"
      : summary.startsWith("Canceled") ? "cancelled" : "completed";
  }
  updateExecutionStatusPlacement(assistant);
}

function renderTerminalActivitySummary(element, summary) {
  const traceMarker = " · Trace: ";
  const traceIndex = summary.lastIndexOf(traceMarker);
  if (traceIndex < 0) {
    element.textContent = summary;
    return;
  }

  const traceId = summary.slice(traceIndex + traceMarker.length);
  const prefix = summary.slice(0, traceIndex);
  const copyTrace = document.createElement("button");
  copyTrace.type = "button";
  copyTrace.className = "activity-trace-copy";
  copyTrace.textContent = `Trace: ${traceId}`;
  copyTrace.title = "Copy trace ID";
  copyTrace.setAttribute("aria-label", "Copy trace ID");
  copyTrace.dataset.originalLabel = "Copy trace ID";
  copyTrace.addEventListener("click", event => {
    event.preventDefault();
    event.stopPropagation();
    void copyText(traceId, copyTrace, "Trace ID copied");
  });
  element.replaceChildren(
    document.createTextNode(`${prefix} · `),
    copyTrace
  );
}

function startMessageEdit(element, message, historyIndex) {
  if (state.requestController || state.readOnlyConversation) {
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
  scheduleMessageQueueDispatch();
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

