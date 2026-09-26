async function* readStreamEvents(stream, idleTimeoutMilliseconds = 0) {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let idleTimer = null;
  try {
    while (true) {
      const result = idleTimeoutMilliseconds > 0
        ? await Promise.race([
          reader.read(),
          new Promise((_, reject) => {
            idleTimer = setTimeout(
              () => reject(new Error("Live stream stopped delivering events.")),
              idleTimeoutMilliseconds
            );
          })
        ])
        : await reader.read();
      clearTimeout(idleTimer);
      idleTimer = null;

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

        yield JSON.parse(data);
      }
    }
  } catch (error) {
    await reader.cancel().catch(() => {});
    throw error;
  } finally {
    clearTimeout(idleTimer);
    reader.releaseLock();
  }
}

async function consumeEventStream(stream, assistant, options = {}) {
  let answerChunks = [];
  let completed = false;
  let diagnostic = null;
  let terminalState = null;
  let terminalSummary = null;
  let terminalWarning = false;
  const contentBlocks = [];
  const timeline = [];
  const historical = options.historical === true;

  let replaySliceStarted = performance.now();

  for await (const streamEvent of options.events ?? readStreamEvents(stream)) {
    if ((historical || options.cooperative) && performance.now() - replaySliceStarted >= 8) {
      await new Promise(resolve => setTimeout(resolve, 0));
      replaySliceStarted = performance.now();
    }
    if (historical && options.conversationVersion !== state.conversationVersion) break;
    assistant.selectedModel = streamEvent.selectedModel
      ?? assistant.selectedModel;
    if (!historical) timeline.push(streamEvent);
    const observedOutputTokens = assistant.lastObservedOutputTokens ?? 0;
    if (isMeaningfulRequestActivity(streamEvent, observedOutputTokens)) {
      assistant.lastHostActivityAt = Date.parse(streamEvent.timestamp)
        || Date.now();
      clearSlowRequestAlert(assistant);
    }
    assistant.lastObservedOutputTokens = Math.max(
      observedOutputTokens,
      streamEvent.contextUsage?.outputTokens ?? 0
    );
    if (!historical) captureConversationContentBlock(contentBlocks, streamEvent);
    const updateVisibleState = !historical && !state.readOnlyConversation;
    if (updateVisibleState) {
      state.conversationSessionId =
        streamEvent.conversationSessionId ?? state.conversationSessionId;
    }

    const selectedHarness = /^harness\.(.+)-selected$/.exec(streamEvent.type);
    if (streamEvent.type === "ollama.startup-delayed") {
      if (!assistant.ollamaStartupAlert) {
        const alert = document.createElement("div");
        alert.className = "request-slow-alert slow-warning ollama-startup-alert";
        alert.setAttribute("role", "status");
        assistant.answer.insertAdjacentElement("afterend", alert);
        assistant.ollamaStartupAlert = alert;
      }
      assistant.ollamaStartupAlert.textContent = streamEvent.message;
    } else if (["ollama.startup-recovered", "response.completed", "error", "request.cancelled"].includes(streamEvent.type)) {
      assistant.ollamaStartupAlert?.remove();
      assistant.ollamaStartupAlert = null;
    }
    if (selectedHarness && updateVisibleState) {
      state.activeHarness = selectedHarness[1];
      updateStreamingComposerActions();
    }

    if (streamEvent.contextUsage && updateVisibleState) {
      state.contextUsage = streamEvent.contextUsage;
      state.contextUsageRestored = false;
      renderContextUsage();
    }

    if (
      streamEvent.type === "target.model-resolved"
      && streamEvent.selectedModel
    ) {
      if (updateVisibleState) {
        state.activeAgentModel = streamEvent.selectedModel;
        state.activeAgentRole = "primary";
        updateActiveAgentLabel();
        void refreshSelectedModelCapabilities(
          streamEvent.selectedModel,
          "primary"
        );
      }
      renderModelSelection(
        assistant,
        streamEvent.selectedModel,
        assistant.modelSelectionOrigin
      );
    } else if (
      streamEvent.type.startsWith("cloud.local-fallback")
      && streamEvent.selectedModel
    ) {
      if (updateVisibleState) {
        state.activeAgentModel = streamEvent.selectedModel;
        state.activeAgentRole = "fallback";
        updateActiveAgentLabel();
        void refreshSelectedModelCapabilities(
          streamEvent.selectedModel,
          "fallback"
        );
      }
      renderModelSelection(
        assistant,
        streamEvent.selectedModel,
        "fallback"
      );
    }

    updateExecutionSession(
      assistant,
      streamEvent.executionSession,
      updateVisibleState
    );
    if (streamEvent.supervisionProgress) {
      renderSupervisionProgress(
        assistant,
        streamEvent
      );
    }
    renderCurrentActivity(assistant, streamEvent);

    if (
      (
        streamEvent.type === "session-created"
        || streamEvent.type === "session-persisted"
      )
      && updateVisibleState
    ) {
      setPersistenceStatus("Saved locally");
    } else if (
      streamEvent.type.startsWith("session-")
      && (
        streamEvent.type.includes("failed")
        || streamEvent.type.includes("invalid")
        || streamEvent.type.includes("too-large")
      )
      && updateVisibleState
    ) {
      setPersistenceStatus("Save failed");
    }

    if (
      streamEvent.responseSegmentHtml
      && assistant.activeResponse
    ) {
      renderAssistantResponse(
        assistant,
        assistant.activeResponse,
        streamEvent.responseSegmentHtml,
        assistant.activeResponse.chunks.join(""),
        answerChunks.join("")
      );
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
      answerChunks.push(delta);
      appendAssistantResponse(
        assistant,
        delta,
        streamEvent.responseSegmentHtml
          ?? streamEvent.renderedHtml
          ?? "",
        streamEvent.contentBlockId ?? null,
        null
      );
    } else if (streamEvent.type === "response.completed") {
      completed = true;
      terminalState = "completed";
      diagnostic = streamEvent.diagnostic ?? null;
      closeAssistantReasoning(assistant);
      const responseTail = streamEvent.responseTail ?? "";
      const answer = answerChunks.join("");
      if (responseTail) {
        closeAssistantResponse(assistant);
        const specialistCompletion = streamEvent.specialistCompletion?.trim() ?? "";
        const aggregateAnswer = specialistCompletion
          && responseTail.includes(specialistCompletion)
          ? responseTail
          : answer
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
          true
        );
        answerChunks = [aggregateAnswer];
        closeAssistantResponse(assistant);
      } else if (assistant.activeResponse && streamEvent.renderedHtml) {
        const response = assistant.activeResponse;
        renderAssistantResponse(
          assistant,
          response,
          streamEvent.renderedHtml,
          answer,
          answer
        );
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
      terminalSummary = terminalActivitySummary(
        assistant.recovered ? "Recovered" : "Completed",
        streamEvent.elapsedMilliseconds,
        diagnostic,
        assistant.selectedModel
      );
      terminalWarning = assistant.recovered;
      finishActivity(assistant, terminalSummary, terminalWarning);
      assistant.reviewButton.hidden =
        !assistant.executionSession?.reviewAvailable;
      clearSlowRequestAlert(assistant);
    } else if (streamEvent.type === "error") {
      terminalState = "failed";
      diagnostic = streamEvent.diagnostic ?? {
        traceId: streamEvent.error.traceId,
        terminalState: "failed",
        persisted: streamEvent.error.diagnosticsPersisted === true
      };
      closeAssistantContent(assistant);
      const errorText = `${streamEvent.error.message}\n`
        + `Reference: ${streamEvent.error.traceId}`;
      answerChunks = [errorText];
      const response = ensureAssistantResponse(
        assistant,
        `error:${streamEvent.error.traceId}`
      );
      response.chunks = [errorText];
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
      terminalSummary = terminalActivitySummary(
        "Failed",
        streamEvent.elapsedMilliseconds,
        diagnostic,
        assistant.selectedModel
      );
      terminalWarning = false;
      finishActivity(assistant, terminalSummary, false);
      addTraceDiagnosticActions(assistant, diagnostic);
      stopSlowRequestTimer(assistant);
    } else if (streamEvent.type.startsWith("request.slow-")) {
      renderSlowRequestAlert(
        assistant,
        streamEvent,
        historical
      );
      addActivity(
        assistant,
        streamEvent,
        streamEvent.type === "request.slow-critical"
      );
    } else if (streamEvent.type === "request.cancelled") {
      terminalState = "cancelled";
      diagnostic = streamEvent.diagnostic ?? null;
      closeAssistantContent(assistant);
      addActivity(assistant, streamEvent, false);
      assistant.answer.classList.remove("pending");
      terminalSummary = terminalActivitySummary(
        "Canceled",
        streamEvent.elapsedMilliseconds,
        diagnostic,
        assistant.selectedModel
      );
      if (
        assistant.slowDiagnostic?.persisted === true
        && diagnostic?.traceId === assistant.slowDiagnostic.traceId
      ) {
        diagnostic = {
          ...diagnostic,
          terminalState: "cancelled-after-slow-warning",
          persisted: true
        };
        terminalSummary = terminalActivitySummary(
          "Canceled",
          streamEvent.elapsedMilliseconds,
          diagnostic,
          assistant.selectedModel
        );
      }
      terminalWarning = Boolean(diagnostic?.terminalState
        === "cancelled-after-slow-warning");
      finishActivity(assistant, terminalSummary, terminalWarning);
      addTraceDiagnosticActions(assistant, diagnostic);
      stopSlowRequestTimer(assistant);
    } else if (
      streamEvent.type === "supervision.gpu-placement-warning"
      && streamEvent.userInput
    ) {
      addActivity(assistant, streamEvent, false);
      await resolveGpuPlacementWarning(streamEvent.userInput);
    } else if (
      streamEvent.type === "user-input.requested"
      && streamEvent.userInput
    ) {
      activateUserInput(streamEvent.userInput);
    } else if (
      (streamEvent.type === "user-input.submitted"
        || streamEvent.type === "user-input.cancelled")
      && streamEvent.userInput
    ) {
      if (state.activeUserInput?.id === streamEvent.userInput.id) {
        clearActiveUserInput();
      }
      if (streamEvent.type === "user-input.submitted") {
        addUserInputTranscript(assistant, streamEvent);
      } else {
        addActivity(assistant, streamEvent, false);
      }
    } else if (
      streamEvent.type === "action.awaiting-approval"
      && streamEvent.localAction
    ) {
      addApprovalActivity(
        assistant,
        streamEvent,
        historical
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
        streamEvent,
        historical
      );
    } else if (streamEvent.type === "action.recovery-resumed"
      || streamEvent.type === "action.recovery-stopped") {
      const pending = assistant.container.querySelector(
        ".recovery-decision:not([data-decision])"
      );
      if (pending) {
        pending.dataset.decision = streamEvent.type;
        pending.querySelector(".approval-status").textContent =
          streamEvent.type === "action.recovery-resumed" ? "Continuing" : "Stopped";
        pending.querySelectorAll("button").forEach(button => { button.disabled = true; });
        pending.open = false;
      }
      if (!historical && assistant.recoveryPreviousAutoFollow) {
        resumeAutoFollow();
      }
      assistant.recoveryPreviousAutoFollow = null;
      addActivity(assistant, streamEvent, false);
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
    ) {
      if (historical) {
        continue;
      }
      void refreshRuntimeStatus();
    }
  }

  if (terminalSummary) {
    finishActivity(assistant, terminalSummary, terminalWarning);
  }

  return {
    answer: answerChunks.join(""),
    completed,
    diagnostic,
    terminalState,
    contentBlocks: contentBlocks.map(({ chunks, ...block }) => ({
      ...block,
      content: chunks.join("")
    })),
    timeline
  };
}

async function resolveGpuPlacementWarning(request) {
  const approved = await showAppConfirm(
    "The Worker is configured to use multiple GPUs through Vulkan, while the Supervisor is pinned to a single GPU.\n\nThis configuration may cause model reloads, VRAM contention, or reduced performance.\n\nContinue with this configuration?",
    {
      title: "Potential GPU placement conflict",
      confirmLabel: "Continue Anyway"
    }
  );
  const question = request.questions[0];
  await fetchJson(`/api/user-input/${encodeURIComponent(request.id)}/decision`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      browserSessionId: state.browserSessionId,
      executionSessionId: request.executionSessionId,
      answers: approved
        ? [
          {
            questionId: question.id,
            answer: "Continue Anyway"
          }
        ]
        : [],
      cancelled: !approved
    })
  });
}

function captureConversationContentBlock(blocks, streamEvent) {
  const kind = streamEvent.type === "reasoning.delta"
    ? "reasoning"
    : streamEvent.type === "response.delta"
      || (streamEvent.type === "response.completed" && streamEvent.responseTail)
      ? "response"
      : null;
  const content = streamEvent.type === "reasoning.delta"
    ? streamEvent.reasoningDelta
    : streamEvent.type === "response.delta"
      ? streamEvent.delta
      : streamEvent.responseTail;

  if (!kind || !content) {
    return;
  }

  const id = streamEvent.type === "response.completed"
    ? `terminal:${streamEvent.requestId}`
    : streamEvent.contentBlockId ?? null;
  const last = blocks.at(-1);

  if (last?.kind === kind && last.id === id) {
    last.chunks.push(content);
    return;
  }

  blocks.push({
    kind,
    content,
    chunks: [content],
    id
  });
}

function terminalActivitySummary(
  label,
  elapsedMilliseconds,
  diagnostic,
  model
) {
  return [
    label,
    model ? `Model: ${model}` : null,
    elapsedMilliseconds === null || elapsedMilliseconds === undefined
      ? null
      : formatElapsed(elapsedMilliseconds),
    diagnostic?.traceId ? `Trace: ${diagnostic.traceId}` : null
  ].filter(Boolean).join(" · ");
}

function addTraceDiagnosticActions(assistant, diagnostic) {
  const eligibleTerminalState = diagnostic?.terminalState === "failed"
    || diagnostic?.terminalState === "cancelled-after-slow-warning";
  if (
    !diagnostic?.traceId
    || !eligibleTerminalState
    || diagnostic.persisted !== true
    || assistant.container.querySelector(".trace-diagnostic-actions")
  ) {
    return;
  }

  const actions = document.createElement("div");
  actions.className = "trace-diagnostic-actions";
  const investigate = createMessageActionButton(
    "Investigate error",
    "Ask Agentic Router to analyze this sanitized Host trace"
  );
  investigate.classList.add("trace-diagnostic-investigate");
  investigate.addEventListener(
    "click",
    () => queueDiagnosticInvestigation(
      diagnostic.traceId,
      investigate
    )
  );
  actions.append(investigate);
  assistant.answer.insertAdjacentElement("afterend", actions);
}

function slowRequestSubject(status) {
  const base = `${status.harness}/${status.model}`;
  return status.tool ? `${base} · ${status.tool}` : base;
}

function isMeaningfulRequestActivity(streamEvent, observedOutputTokens = 0) {
  if (
    streamEvent.type === "request.heartbeat"
    || streamEvent.type.startsWith("request.slow-")
  ) {
    return false;
  }
  if (streamEvent.type === "context.usage") {
    return (streamEvent.contextUsage?.outputTokens ?? 0)
      > observedOutputTokens;
  }
  if (
    streamEvent.delta
    || streamEvent.reasoningDelta
    || streamEvent.localAction
  ) {
    return true;
  }
  const type = streamEvent.type;
  return type.startsWith("action.")
    || type.startsWith("execution.")
    || type.startsWith("execution-")
    || supervisionEventIsMeaningful(type)
    || (
      type.startsWith("harness.")
      && !type.endsWith("-selected")
      && !type.endsWith("-warning")
      && !type.endsWith("-native-event-preserved")
    )
    || type.includes("tool")
    || type.includes("stdout")
    || type.includes("stderr")
    || type.includes("files-changed")
    || type.includes("edit-applied");
}

function formatRelativeActivity(milliseconds) {
  const duration = Math.max(0, Math.floor(milliseconds / 1000));
  if (duration >= 3600) {
    return `${Math.floor(duration / 3600)}h ${Math.floor((duration % 3600) / 60)}m`;
  }
  if (duration >= 60) {
    return `${Math.floor(duration / 60)}m ${duration % 60}s`;
  }
  return `${duration}s`;
}

function refreshSlowRequestAlert(assistant, now = Date.now()) {
  const alert = assistant.slowRequestAlert;
  const status = assistant.slowRequestStatus;
  if (!alert || !status) {
    return;
  }
  const lastActivityAt = assistant.lastHostActivityAt
    ?? Date.parse(status.lastActivityAt)
    ?? now;
  const startedAt = Date.parse(status.startedAt) || now;
  const idle = Math.max(0, now - lastActivityAt);
  const running = Math.max(0, now - startedAt);
  const subject = slowRequestSubject(status);
  alert.classList.toggle("slow-critical", status.level === "critical");
  alert.classList.toggle("slow-warning", status.level !== "critical");
  alert.setAttribute(
    "role",
    status.level === "critical" ? "alert" : "status"
  );
  alert.textContent = status.level === "critical"
    ? `${subject} has produced no meaningful Host activity for ${formatRelativeActivity(idle)} · total runtime ${formatRelativeActivity(running)}. Consider Stop; the trace will remain available for investigation.`
    : `${subject} has produced no meaningful Host activity for ${formatRelativeActivity(idle)} · total runtime ${formatRelativeActivity(running)}.`;
}

function renderSlowRequestAlert(assistant, streamEvent, historical) {
  if (!streamEvent.slowRequest) {
    return;
  }
  assistant.slowRequestStatus = streamEvent.slowRequest;
  assistant.slowDiagnostic = streamEvent.diagnostic
    ?? assistant.slowDiagnostic;
  assistant.lastHostActivityAt = Date.parse(
    streamEvent.slowRequest.lastActivityAt
  ) || assistant.lastHostActivityAt || Date.now();
  if (!assistant.slowRequestAlert) {
    const alert = document.createElement("div");
    alert.className = "request-slow-alert slow-warning";
    alert.dataset.timelineKind = "slow-request";
    assistant.slowRequestAlert = alert;
    assistant.answer.insertAdjacentElement("afterend", alert);
  }
  refreshSlowRequestAlert(assistant);
  if (!historical && !assistant.slowRequestTimer) {
    assistant.slowRequestTimer = window.setInterval(
      () => refreshSlowRequestAlert(assistant),
      1_000
    );
  }
}

function clearSlowRequestAlert(assistant) {
  stopSlowRequestTimer(assistant);
  assistant.slowRequestAlert?.remove();
  assistant.slowRequestAlert = null;
  assistant.slowRequestStatus = null;
  assistant.slowDiagnostic = null;
}

function stopSlowRequestTimer(assistant) {
  if (assistant.slowRequestTimer) {
    window.clearInterval(assistant.slowRequestTimer);
    assistant.slowRequestTimer = null;
  }
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
  if (assistant.details.dataset.terminal !== "true") {
    assistant.summary.textContent =
      `Technical details · ${assistant.technicalEventCount} `
      + `${assistant.technicalEventCount === 1 ? "event" : "events"}`;
  }
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

function renderCompletionSummary(assistant, lines) {
  const completionLines = lines ?? [];
  assistant.completionSummary.hidden = completionLines.length === 0;
  assistant.completionSummary.replaceChildren();
  if (completionLines.length) {
    const heading = document.createElement("strong");
    heading.textContent = "Host summary";
    const list = document.createElement("ul");
    for (const line of completionLines) {
      const item = document.createElement("li");
      item.textContent = line;
      list.append(item);
    }
    assistant.completionSummary.append(heading, list);
  }
}

function updateExecutionSession(assistant, session, updateVisibleState = true) {
  if (!session) {
    return;
  }

  assistant.executionSession = session;
  renderCompletionSummary(assistant, session.completionSummary);
  if (updateVisibleState) {
    state.latestExecutionSessionId = session.id;
  }
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
  updateExecutionStatusPlacement(assistant);
  renderExecutionPlan(assistant, session);

  if (session.reviewAvailable && session.state !== "running") {
    assistant.reviewButton.hidden = false;
  }
}

function updateExecutionStatusPlacement(assistant) {
  const terminal = assistant.details.dataset.terminal === "true";
  assistant.sessionHeader.classList.toggle("is-live", !terminal && !assistant.sessionHeader.hidden);
  assistant.sessionFooter.hidden = !terminal || assistant.sessionHeader.hidden;
  if (!assistant.sessionFooter.hidden) {
    assistant.sessionFooter.replaceChildren(
      ...[...assistant.sessionHeader.childNodes].map(node => node.cloneNode(true))
    );
  }
}

function renderExecutionPlan(assistant, session) {
  const plan = session?.plan;
  if (!plan) {
    assistant.planPanel.hidden = true;
    assistant.planPanel.classList.remove("docked");
    assistant.planBody.replaceChildren();
    return;
  }

  document.querySelectorAll(".execution-plan.docked").forEach(
    panel => {
      if (panel !== assistant.planPanel) {
        panel.classList.remove("docked");
      }
    }
  );
  assistant.planPanel.classList.add("docked");
  assistant.planPanel.hidden = false;
  assistant.planPanel.dataset.state = session.state;
  const totalSteps = plan.steps.length;
  const completedSteps = Math.min(plan.completedStepCount, totalSteps);
  const complete = totalSteps > 0 && completedSteps === totalSteps;
  const failed = plan.steps.some(
    step => step.status === "failed" || step.status === "blocked"
  );
  const titleArea = document.createElement("span");
  titleArea.className = "execution-plan-title-area";
  const icon = document.createElement("span");
  icon.className = "execution-plan-icon";
  icon.setAttribute("aria-hidden", "true");
  icon.textContent = complete ? "✓" : failed ? "!" : "●";
  const title = document.createElement("span");
  title.className = "execution-plan-title";
  title.textContent = `Plan · ${compactPlanText(plan.objective, 84)}`;
  title.title = plan.objective;
  titleArea.append(icon, title);

  const meta = document.createElement("span");
  meta.className = "execution-plan-meta";
  const stepCount = document.createElement("span");
  stepCount.className = "execution-plan-step-count";
  stepCount.textContent = `${completedSteps}/${totalSteps} steps`;
  const chevron = document.createElement("span");
  chevron.className = "execution-plan-chevron";
  chevron.setAttribute("aria-hidden", "true");
  chevron.textContent = "⌄";
  meta.append(stepCount, chevron);

  const progressTrack = document.createElement("span");
  progressTrack.className = "execution-plan-progress-track";
  progressTrack.setAttribute("role", "progressbar");
  progressTrack.setAttribute("aria-label", "Plan progress");
  progressTrack.setAttribute("aria-valuemin", "0");
  progressTrack.setAttribute("aria-valuemax", String(totalSteps));
  progressTrack.setAttribute("aria-valuenow", String(completedSteps));
  const progressFill = document.createElement("span");
  progressFill.className = "execution-plan-progress-fill";
  progressFill.style.width = totalSteps > 0
    ? `${completedSteps * 100 / totalSteps}%`
    : "0%";
  progressTrack.append(progressFill);
  assistant.planSummary.replaceChildren(titleArea, meta, progressTrack);
  assistant.planSummary.setAttribute(
    "aria-label",
    `Plan: ${plan.objective}. ${completedSteps} of ${totalSteps} steps complete.`
  );
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
    title.textContent = compactPlanText(step.title);
    if (title.textContent !== step.title) {
      title.title = step.title;
    }
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
  footer.textContent = session.planSource === "supervisor"
    ? `Supervisor-managed queue · ${supervisionPhaseLabel(session.supervisionPhase)}`
    : `${session.changedFileCount} changed files`;
  footer.title = plan.completedStepCount === plan.steps.length
    ? `Steps ${plan.steps.length}/${plan.steps.length}`
    : `Step ${displayedStep}/${plan.steps.length}`;
  assistant.planBody.append(list, footer);
}

function collapseExecutionPlansFromOutside(event) {
  document.querySelectorAll(".execution-plan.docked[open]").forEach(
    panel => {
      if (!panel.contains(event.target)) {
        panel.open = false;
      }
    }
  );
}

function undockExecutionPlans() {
  elements.messages.querySelectorAll(".execution-plan.docked").forEach(
    panel => panel.classList.remove("docked")
  );
}

function initializeComposerShellMetrics() {
  const chatHeader = elements.conversationView.querySelector(".chat-header");
  const updateHeight = () => {
    const height = Math.ceil(
      elements.composerShell.getBoundingClientRect().height
    );
    const workspaceRect = elements.conversationView.getBoundingClientRect();
    elements.conversationView.style.setProperty(
      "--composer-shell-height",
      `${height}px`
    );
    elements.conversationView.style.setProperty(
      "--workspace-center-x",
      `${workspaceRect.left + workspaceRect.width / 2}px`
    );
    elements.conversationView.style.setProperty(
      "--chat-header-height",
      `${Math.ceil(chatHeader.getBoundingClientRect().height)}px`
    );
  };
  state.composerResizeObserver?.disconnect();
  state.composerResizeObserver = new ResizeObserver(updateHeight);
  state.composerResizeObserver.observe(elements.composerShell);
  state.composerResizeObserver.observe(elements.conversationView);
  state.composerResizeObserver.observe(chatHeader);
  updateHeight();
}

