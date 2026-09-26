function renderAssistantResponse(
  assistant,
  response,
  renderedHtml,
  markdown,
  aggregateMarkdown
) {
  assistant.rawAnswer = aggregateMarkdown;
  assistant.copyButton.disabled = !aggregateMarkdown;
  response.body.innerHTML = renderedHtml;
  secureRenderedLinks(response.body);
  enhanceCodeBlocks(
    response.body,
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
        `Copy code ${label.textContent}`
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
          "Code copied"
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
  state.autoFollow = elements.messages.querySelector(
    ".recovery-decision:not([data-decision]):not(.historical-approval)"
  ) ? false : isNearBottom();
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
  if (!state.autoFollow) {
    return;
  }
  if (elements.messages.querySelector("#empty-state")) {
    elements.messages.scrollTo({
      top: 0,
      behavior: "instant"
    });
    return;
  }
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

  elements.sendButtonLabel.textContent = "Send";
  elements.sendButton.querySelector(".send-icon").textContent = "\u2191";
  elements.sendButton.setAttribute(
    "aria-label",
    state.editingTurn && !isStreaming
      ? "Send edited message"
      : "Send message"
  );
  elements.sendButton.title = elements.sendButton.getAttribute("aria-label");
  elements.sendButton.classList.remove("cancel");
  elements.cancelRequest.hidden = !isStreaming;
  elements.cancelRequest.disabled = false;
  elements.composer.classList.toggle("streaming", isStreaming);
  elements.compactContext.disabled = isStreaming;
  elements.cancelMessageEdit.hidden = isStreaming || !state.editingTurn;
  elements.messages.querySelectorAll(".edit-message").forEach(
    button => {
      button.disabled = isStreaming;
    }
  );
  updateInteractionControls();
  updateHarnessControls();
  updateStreamingComposerActions();
  updateComposerStatus();
  renderWebControl();
}

function updateStreamingComposerActions() {
  elements.cancelRequest.hidden = !state.requestController;
  renderMessageQueue();
}

function renderPendingContextUsage() {
  if (!state.settings || state.requestController) {
    return;
  }
  state.contextUsage = null;
  state.contextUsageRestored = false;
  renderContextUsage();
}

function renderContextUsage() {
  const usage = state.contextUsage;

  if (!usage) {
    elements.contextUsageSummaryText.textContent =
      "Context will be calculated when sending";
    elements.contextUsage.dataset.accuracy = "pending";
    elements.contextUsage.dataset.warning = "";
    elements.contextUsageEstimateWarning.hidden = true;
    elements.contextUsageEstimateWarning.removeAttribute("title");
    elements.contextUsageEstimateWarning.removeAttribute("aria-label");
    elements.contextUsageWarning.hidden = true;
    elements.contextUsageActiveValue.textContent = "Not calculated";
    elements.contextUsageProgress.setAttribute("aria-valuemax", "1");
    elements.contextUsageProgress.setAttribute("aria-valuenow", "0");
    elements.contextUsageProgress.setAttribute(
      "aria-valuetext",
      "Context not calculated"
    );
    elements.contextUsageProgressFill.style.width = "0%";
    elements.contextUsageOverviewDetails.replaceChildren();
    elements.contextUsageMessageDetails.replaceChildren();
    elements.contextUsageLimitDetails.replaceChildren();
    elements.contextUsageDetails.replaceChildren();
    elements.contextUsageAdvanced.hidden = true;
    elements.contextUsageAdvanced.open = false;
    elements.contextUsageAdvanced.dataset.detailCount = "0";
    elements.compactContext.hidden = true;
    return;
  }

  const effectiveLimit = usage.effectiveLimitTokens
    || Math.min(
      usage.applicationLimit,
      usage.providerMaximumTokens ?? usage.configuredProviderLimit
    );
  const activeContextTokens = usage.activeContextTokens || usage.inputTokens;
  const liveContext = usage.activeContextTokens > 0 && !state.contextUsageRestored;
  const contextPercentage = activeContextTokens * 100
    / Math.max(1, effectiveLimit);
  const boundedContextPercentage = Math.max(
    0,
    Math.min(100, contextPercentage)
  );
  const contextPercentageLabel = contextPercentage > 0 && contextPercentage < 1
    ? "<1%"
    : `${Math.round(contextPercentage)}%`;
  elements.contextUsageSummaryText.textContent =
    `Context ${formatCompactTokens(activeContextTokens)} / `
    + `${formatCompactTokens(effectiveLimit)} · `
    + `${state.contextUsageRestored ? "last recorded · " : ""}`
    + `${liveContext ? `${window.AgenticRouterI18n.t("context.live")} ` : ""}`
    + `${usage.accuracy === "exact" ? "exact" : "estimated"}`;
  const estimatedLiveUsage = liveContext && usage.accuracy !== "exact";
  const estimateWarning = t(
    "context.live_estimate_warning",
    { harness: activeContextHarnessLabel() }
  );
  elements.contextUsageEstimateWarning.hidden = !estimatedLiveUsage;
  elements.contextUsageEstimateWarning.title = estimatedLiveUsage
    ? estimateWarning
    : "";
  elements.contextUsageEstimateWarning.setAttribute(
    "aria-label",
    estimatedLiveUsage ? estimateWarning : ""
  );
  elements.contextUsage.dataset.accuracy = usage.accuracy;
  elements.contextUsage.dataset.warning =
    usage.warningThreshold ? String(usage.warningThreshold) : "";
  elements.contextUsageWarning.hidden =
    !usage.warningThreshold && !usage.trimmed;
  elements.contextUsageWarning.textContent = [
    usage.warningThreshold
      ? `Warning: context is above ${usage.warningThreshold}% of usable capacity.`
      : null,
    usage.trimmed
      ? "Eligible blocks were omitted only from the submitted payload."
      : null
  ].filter(Boolean).join(" ");
  const activeContextLabel = `${formatCompactTokens(activeContextTokens)} / `
    + `${formatCompactTokens(effectiveLimit)} (${contextPercentageLabel})`;
  elements.contextUsageActiveValue.textContent = activeContextLabel;
  elements.contextUsageProgress.setAttribute(
    "aria-valuemax",
    String(effectiveLimit)
  );
  elements.contextUsageProgress.setAttribute(
    "aria-valuenow",
    String(Math.min(activeContextTokens, effectiveLimit))
  );
  elements.contextUsageProgress.setAttribute(
    "aria-valuetext",
    `${activeContextLabel} active`
  );
  elements.contextUsageProgressFill.style.width =
    `${boundedContextPercentage}%`;

  elements.contextUsageOverviewDetails.replaceChildren(
    contextDetail(
      "Visible messages",
      `${formatInteger(usage.visibleMessages)} (${formatInteger(usage.includedMessages)} included)`
    ),
    contextDetail(
      "Total input",
      `${usage.accuracy === "exact" ? "" : "~"}${formatInteger(usage.inputTokens)}`
    ),
    contextDetail(
      window.AgenticRouterI18n.t("context.generated_output"),
      formatInteger(usage.outputTokens)
    )
  );
  elements.contextUsageMessageDetails.replaceChildren(
    contextDetail(
      "Current conversation",
      `~${formatInteger(usage.conversationTokens)}`
    ),
    contextDetail(
      "System & instructions",
      `~${formatInteger(usage.systemInstructionTokens)}`
    )
  );
  elements.contextUsageLimitDetails.replaceChildren(
    contextDetail(
      "Output reserve",
      formatInteger(usage.reservedResponseTokens)
    ),
    contextDetail("Effective limit", formatInteger(effectiveLimit))
  );

  const advancedDetails = [
    ["Specialist inference", usage.inferenceSequence || 1],
    ["Omitted messages", usage.omittedMessages],
    ["Project context", `~${formatInteger(usage.projectContextTokens)}`],
    ["Toolset discovery", `~${formatInteger(usage.toolDiscoveryTokens)}`],
    ["Granted schemas", `~${formatInteger(usage.grantedToolSchemaTokens)}`],
    ["Host state/results", `~${formatInteger(usage.hostStateTokens)}`],
    ["Structural overhead", `~${formatInteger(usage.structuralOverheadTokens)}`],
    ["Required context", formatInteger(usage.requiredContextTokens)],
    [
      "Count source",
      usage.accuracy === "exact"
        ? "provider-reported usage"
        : usage.estimator
    ],
    [
      "Provider maximum",
      usage.providerMaximumTokens == null
        ? "not reported"
        : formatInteger(usage.providerMaximumTokens)
    ],
    ["Configured provider limit", formatInteger(usage.configuredProviderLimit)],
    ["Application limit", formatInteger(usage.applicationLimit)],
    ["Omitted blocks", usage.omittedBlocks || 0]
  ];
  elements.contextUsageDetails.replaceChildren(
    ...advancedDetails.map(([label, value]) => contextDetail(label, value))
  );
  elements.contextUsageAdvanced.hidden = false;
  elements.contextUsageAdvanced.dataset.detailCount =
    String(advancedDetails.length);
  updateContextUsageAdvancedLabel();
  elements.compactContext.hidden = !usage.compactionEligible;
  elements.compactContext.disabled = Boolean(state.requestController);
  elements.compactContext.textContent = state.compactContextNextRequest
    ? "Compaction prepared"
    : "Compact context";
}

function activeContextHarnessLabel() {
  const harnessId = state.activeHarness ?? state.harness;
  const status = state.harnesses.find(
    item => item.definition.id === harnessId
  );
  return status
    ? harnessDisplayLabel(status.definition)
    : harnessId === "auto-model-harness"
      ? "the selected harness"
      : harnessId || "the selected provider";
}

async function requestManualContextCompaction() {
  const usage = state.contextUsage;
  if (!usage?.compactionEligible || state.requestController) {
    return;
  }
  const before = usage.beforeCompactionTokens ?? usage.inputTokens;
  const after = usage.afterCompactionTokens ?? usage.inputTokens;
  const confirmed = await showAppConfirm(
    "Compaction will not delete saved messages or change the visible chat. "
      + "It will omit only eligible blocks from subsequent inference payloads.\n\n"
      + `Current estimate: ${formatInteger(before)} tokens\n`
      + `Compacted estimate: ${formatInteger(after)} tokens\n`
      + `Eligible/omitted blocks: ${usage.omittedBlocks || 0}`,
    {
      title: "Compact submitted context?",
      confirmLabel: "Compact next request"
    }
  );
  if (!confirmed) {
    return;
  }
  state.compactContextNextRequest = true;
  renderContextUsage();
}

function updateContextUsageAdvancedLabel() {
  const detailCount = Number(
    elements.contextUsageAdvanced.dataset.detailCount
  ) || 0;
  elements.contextUsageAdvancedLabel.textContent =
    elements.contextUsageAdvanced.open
      ? "Hide advanced details"
      : `Show advanced details (${detailCount} hidden)`;
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
    window.AgenticRouterI18n.locale,
    {
      minimumFractionDigits: 1,
      maximumFractionDigits: 1
    }
  )}k`;
}

function updateComposerStatus() {
  if (state.readOnlyConversation) {
    elements.composerStatus.textContent = state.requestController
      ? "Read-only · another conversation is still running"
      : "Read-only history · open this conversation again to continue";
  } else if (state.activeUserInput) {
    const request = state.activeUserInput;
    elements.composerStatus.textContent = request.resumable === false
      ? "Questions restored · harness request unavailable"
      : `Answering question ${request.currentQuestionIndex + 1} of ${request.questions.length}`;
  } else if (state.requestController) {
    elements.composerStatus.textContent = state.steeringMessage
      ? t("steer.sending")
      : state.queueEditingId
        ? `${t("buffer.editing")} · response in progress`
        : state.messageQueue.length > 0
          ? `Response in progress · ${t("buffer.count", { count: state.messageQueue.length })}`
          : "Response in progress";
  } else if (state.conversationTransitioning) {
    elements.composerStatus.textContent = "Switching conversation safely";
  } else if (state.editingTurn) {
    elements.composerStatus.textContent = "Editing message · Esc to cancel";
  } else if (state.interactionMode === "execute") {
    if (state.harness === "auto-model-harness") {
      elements.composerStatus.textContent =
        `Execute · Auto Model × Harness · ${state.approvalPolicy === "ask" ? "ask for approval" : "automatic approval"}`;
      updateActiveAgentLabel();
      renderCapabilityContext();
      return;
    }
    const status = state.harnesses.find(
      item => item.definition.id === state.harness
    );
    const harness = status
      ? harnessDisplayLabel(status.definition)
      : state.harness;
    elements.composerStatus.textContent =
      `Execute · ${harness} · ${state.approvalPolicy === "ask" ? "ask for approval" : "automatic approval"}`;
  } else if (state.attachments.length > 0 || state.webEnabled) {
    elements.composerStatus.textContent = [
      state.attachments.length > 0
        ? `${state.attachments.length} image${state.attachments.length === 1 ? "" : "s"}`
        : null,
      state.webEnabled ? "Web automatic" : null,
      "Press Enter to send"
    ].filter(Boolean).join(" · ");
  } else {
    elements.composerStatus.textContent = "Press Enter to send";
  }

  updateActiveAgentLabel();
  renderCapabilityContext();
}

function updateActiveAgentLabel() {
  if (!elements.activeAgentLabel) {
    return;
  }

  const selectedModel = state.activeAgentModel
    ?? elements.modelSelector.value;
  elements.activeAgentLabel.textContent =
    selectedModel && selectedModel !== "auto"
      ? selectedModel
      : "Auto (Keywords)";
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

