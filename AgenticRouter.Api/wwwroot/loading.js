async function loadInitialApplicationState() {
  if (initializationInProgress) {
    return;
  }
  initializationInProgress = true;
  resetApplicationLoader();

  try {
    setApplicationLoaderStep(
      "recovery",
      "loading",
      "Restoring local recovery state…"
    );
    state.recovery = await fetchJson("/api/recovery/status");
    renderRecoveryState();
    setApplicationLoaderStep("recovery", "complete");
    setApplicationLoaderStep(
      "providers",
      "loading",
      "Starting or checking Ollama and discovering models…"
    );
    await loadApplicationState();
    await refreshRuntimeStatus();
    renderRecoveryState();
    setApplicationLoaderStep("runtime", "complete");
    setApplicationLoaderStep(
      "conversation",
      "loading",
      "Preparing the active conversation…"
    );
    if (!state.recovery?.safeMode) {
      await ensureConversationIdentity();
      await restorePendingUserInput();
    }
    setApplicationLoaderStep("conversation", "complete");
    finishApplicationLoader();
    void restoreLiveChatRun();
    scheduleRuntimeRefresh();
    scheduleExternalAvailabilityRefresh();
    elements.messageInput.focus();
  } catch (error) {
    elements.providerBadge.textContent = "Error";
    elements.providerBadge.className = "badge error";
    elements.runtimeCompactMeters.textContent = error.message;
    failApplicationLoader(error);
  } finally {
    initializationInProgress = false;
  }
}

function resetApplicationLoader() {
  document.body.classList.add("bootstrapping");
  elements.appLoader.hidden = false;
  elements.appLoader.setAttribute("aria-busy", "true");
  elements.appLoaderRetry.hidden = true;
  for (const step of elements.appLoader.querySelectorAll("[data-bootstrap-step]")) {
    step.dataset.state = "pending";
    step.removeAttribute("aria-current");
  }
  elements.appLoaderDetail.textContent = "Starting the application…";
}

function setApplicationLoaderStep(stepName, status, detail = null) {
  // Parallel startup work may finish after another request has failed.
  // Preserve the failure and Retry state until the next explicit attempt.
  if (elements.appLoader.getAttribute("aria-busy") !== "true") {
    return;
  }
  const step = elements.appLoader.querySelector(
    `[data-bootstrap-step="${stepName}"]`
  );
  if (step) {
    step.dataset.state = status;
    if (status === "loading") {
      step.setAttribute("aria-current", "step");
    } else {
      step.removeAttribute("aria-current");
    }
  }
  if (detail) {
    elements.appLoaderDetail.textContent = detail;
  }
}

function finishApplicationLoader() {
  elements.appLoader.setAttribute("aria-busy", "false");
  elements.appLoader.hidden = true;
  document.body.classList.remove("bootstrapping");
}

function failApplicationLoader(error) {
  const active = elements.appLoader.querySelector(
    '[data-bootstrap-step][data-state="loading"]'
  );
  if (active) {
    active.dataset.state = "failed";
    active.removeAttribute("aria-current");
  }
  elements.appLoaderDetail.textContent = error?.message
    ?? "Application startup failed.";
  elements.appLoader.setAttribute("aria-busy", "false");
  elements.appLoaderRetry.hidden = false;
}

