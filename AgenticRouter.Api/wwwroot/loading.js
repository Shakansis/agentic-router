let startupReconnectController = null;

function startApplicationReconnect() {
  if (startupReconnectController && !startupReconnectController.signal.aborted) return;
  const controller = new AbortController();
  startupReconnectController = controller;
  void retryServerConnection(
    attempt => loadInitialApplicationState(false, attempt),
    controller.signal
  ).catch(error => {
    if (error.name !== "AbortError") failApplicationLoader(error);
  }).finally(() => {
    if (startupReconnectController === controller) startupReconnectController = null;
  });
}

function setServerConnectionStatus(message) {
  elements.serverConnectionStatus.textContent = message ?? "";
  elements.serverConnectionStatus.hidden = !message;
  if (message && !elements.appLoader.hidden) {
    elements.appLoaderDetail.textContent = [
      elements.appLoaderDetail.dataset.failureMessage, message
    ].filter(Boolean).join(" ");
  }
}

function waitForConnectionDelay(milliseconds, signal) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException("Connection wait cancelled.", "AbortError"));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, milliseconds);
    function onAbort() {
      clearTimeout(timer);
      reject(new DOMException("Connection wait cancelled.", "AbortError"));
    }
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

async function retryServerConnection(connect, signal) {
  while (!signal?.aborted) {
    for (let attempt = 1; attempt <= 5; attempt++) {
      setServerConnectionStatus(`Reconnecting to the server ${attempt}/5…`);
      try {
        const result = await connect(attempt);
        setServerConnectionStatus(null);
        return result;
      } catch (error) {
        if (signal?.aborted || error.name === "AbortError") {
          setServerConnectionStatus(null);
          throw error;
        }
        if (error.retryConnection === false || error.status && error.status < 500) {
          setServerConnectionStatus(null);
          throw error;
        }
        setServerConnectionStatus(`Reconnecting to the server ${attempt}/5…`);
        await waitForConnectionDelay(5_000, signal);
      }
    }

    setServerConnectionStatus("Server unavailable. Checking its health every 30 seconds in the background…");
    while (!signal?.aborted) {
      await waitForConnectionDelay(30_000, signal);
      try {
        await fetchJson("/api/recovery/status", { cache: "no-store", signal });
        break;
      } catch (error) {
        if (signal?.aborted || error.name === "AbortError") throw error;
      }
    }
  }
  throw new DOMException("Connection wait cancelled.", "AbortError");
}

async function loadInitialApplicationState(autoReconnect = true, reconnectAttempt = null) {
  if (initializationInProgress) {
    return;
  }
  initializationInProgress = true;
  resetApplicationLoader();
  if (reconnectAttempt != null) {
    setServerConnectionStatus(`Reconnecting to the server ${reconnectAttempt}/5…`);
  }

  let failure = null;
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
    renderSupervisionRecovery();
    setApplicationLoaderStep("conversation", "complete");
    finishApplicationLoader();
    setServerConnectionStatus(null);
    void restoreLiveChatRun();
    scheduleRuntimeRefresh();
    scheduleExternalAvailabilityRefresh();
    elements.messageInput.focus();
  } catch (error) {
    failure = error;
    elements.providerBadge.textContent = "Error";
    elements.providerBadge.className = "badge error";
    elements.runtimeCompactMeters.textContent = error.message;
    failApplicationLoader(error);
  } finally {
    initializationInProgress = false;
  }
  if (!failure) return;
  if (!autoReconnect) throw failure;
  if (failure.status && failure.status < 500) return;
  startApplicationReconnect();
}

function resetApplicationLoader() {
  delete elements.appLoaderDetail.dataset.failureMessage;
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
  const message = error?.message ?? "Application startup failed.";
  elements.appLoaderDetail.dataset.failureMessage = message;
  elements.appLoaderDetail.textContent = message;
  elements.appLoader.setAttribute("aria-busy", "false");
  elements.appLoaderRetry.hidden = false;
}

