const specialistLabels = {
  "general-chat": "General chat",
  documentation: "Documentation",
  "software-development": "Software development",
  "software-architecture": "Software architecture",
  "rpg-storytelling": "RPG storytelling",
  "review-and-testing": "Review and testing"
};

const storyEditor = {
  revision: 0,
  proposal: null,
  undo: null,
  controller: null,
  pendingImport: null,
  repetitionTimer: null,
  mode: "raw",
  previewController: null,
  viewSelection: null,
  viewSelectionEndpoints: null,
  selectionLocked: false,
  ignoredWords: new Set(),
  highlightedWord: null,
  wordMenuTrigger: null,
  fileSource: null,
  fileSaving: false,
  comparisonScrollTargets: new WeakMap(),
  elements: {}
};

function renderSpecialistControl() {
  const selector = document.getElementById("specialist-selector");
  selector.value = state.specialist ?? "";
  selector.disabled = Boolean(state.requestController || storyEditor.controller)
    || state.conversationTransitioning || state.readOnlyConversation;
  const selected = state.specialist ?? state.activeSpecialist;
  document.getElementById("active-specialist").textContent = state.specialist
    ? `${specialistLabels[selected] ?? selected} · Fixed`
    : `Automatic · ${specialistLabels[selected] ?? "Waiting for a message"}`;
  selector.options[0].textContent = state.activeSpecialist && !state.specialist
    ? `Auto · ${specialistLabels[state.activeSpecialist] ?? state.activeSpecialist}` : "Automatic";
  selector.title = document.getElementById("active-specialist").textContent;
  document.getElementById("open-story-editor").disabled = selector.disabled;
  document.getElementById("open-story-editor").hidden = state.specialist !== "rpg-storytelling";
  if (state.specialist !== "rpg-storytelling" && !document.getElementById("story-editor").hidden) closeStoryEditor();
  updateActiveAgentLabel();
}

function restoreSpecialistControl(session) {
  state.specialist = session.specialist ?? null;
  state.activeSpecialist = null;
  for (const message of [...session.messages].reverse()) {
    const selected = [...(message.timeline ?? [])].reverse().find(item =>
      item.intention && (item.type.startsWith("specialist.") || item.type === "router.classified"));
    if (selected) { state.activeSpecialist = selected.intention; break; }
  }
  renderSpecialistControl();
}

function initializeStoryEditor() {
  for (const id of ["story-editor", "story-document", "story-status", "story-selection-status",
    "story-instruction", "story-revise", "story-rewrite", "story-synonyms", "story-agreement", "story-cancel", "story-comparison",
    "story-original", "story-replacement", "story-alternatives", "story-proposal-model",
    "story-apply", "story-undo", "story-file-name", "story-repetition-panel",
    "story-repetition-results", "story-ignore-common", "story-import-confirmation",
    "story-mode-raw", "story-mode-view", "story-preview", "story-token-estimate",
    "story-review-proposal", "story-comparison-status", "story-replacement-diff", "story-edit-proposal", "story-diff-note"]) {
    storyEditor.elements[id] = document.getElementById(id);
  }
  document.getElementById("specialist-selector").addEventListener("change", event => {
    state.specialist = event.target.value || null;
    state.activeSpecialist = null;
    renderSpecialistControl();
    renderPendingContextUsage();
  });
  document.getElementById("open-story-editor").addEventListener("click", () => {
    if (state.specialist !== "rpg-storytelling") return;
    storyEditor.elements["story-editor"].hidden = false;
    document.getElementById("conversation-view").classList.add("story-editor-open");
    document.getElementById("open-story-editor").setAttribute("aria-expanded", "true");
    storyEditor.elements[storyEditor.mode === "raw" ? "story-document" : "story-preview"].focus();
  });
  document.getElementById("close-story-editor").addEventListener("click", () => {
    closeStoryEditor();
    document.getElementById("open-story-editor").focus();
  });
  const fullscreen = document.getElementById("story-fullscreen");
  document.getElementById("story-fullscreen-toggle").addEventListener("click", () => setStoryFullscreen(!fullscreen.open));
  fullscreen.addEventListener("cancel", event => {
    event.preventDefault();
    setStoryFullscreen(false);
  });
  const draft = storyEditor.elements["story-document"];
  draft.addEventListener("input", storyDraftChanged);
  draft.addEventListener("select", updateStorySelection);
  draft.addEventListener("keyup", updateStorySelection);
  draft.addEventListener("pointerup", updateStorySelection);
  storyEditor.elements["story-mode-raw"].addEventListener("click", () => setStoryDisplayMode("raw"));
  storyEditor.elements["story-mode-view"].addEventListener("click", () => setStoryDisplayMode("view"));
  document.addEventListener("selectionchange", captureStoryViewSelection);
  storyEditor.elements["story-editor"].addEventListener("pointerdown", event => {
    const inPreview = storyEditor.elements["story-preview"].contains(event.target);
    if (!inPreview) captureStoryViewSelection();
    storyEditor.selectionLocked = !inPreview;
  }, true);
  storyEditor.elements["story-editor"].addEventListener("focusin", event => {
    storyEditor.selectionLocked = !storyEditor.elements["story-preview"].contains(event.target);
  });
  initializeStoryViewMenu();
  storyEditor.elements["story-preview"].addEventListener("pointerup", captureStoryViewSelection);
  storyEditor.elements["story-preview"].addEventListener("keyup", captureStoryViewSelection);
  storyEditor.elements["story-preview"].addEventListener("click", navigateStoryPreview);
  document.getElementById("story-revise").addEventListener("click", () => requestStoryProposal("revise"));
  document.getElementById("story-rewrite").addEventListener("click", () => requestStoryProposal("rewrite"));
  document.getElementById("story-synonyms").addEventListener("click", () => requestStoryProposal("synonyms"));
  document.getElementById("story-agreement").addEventListener("click", () => requestStoryProposal("agreement"));
  document.getElementById("story-cancel").addEventListener("click", cancelStoryProposal);
  document.getElementById("story-apply").addEventListener("click", applyStoryProposal);
  document.getElementById("story-close-comparison").addEventListener("click", () => storyEditor.elements["story-comparison"].close());
  storyEditor.elements["story-review-proposal"].addEventListener("click", () => {
    if (storyEditor.proposal) storyEditor.elements["story-comparison"].showModal();
  });
  storyEditor.elements["story-comparison"].addEventListener("close", () => {
    updateStoryProposalControls();
    if (!storyEditor.elements["story-editor"].hidden) {
      storyEditor.elements[storyEditor.proposal ? "story-review-proposal" : storyEditor.mode === "raw" ? "story-document" : "story-preview"].focus();
    }
  });
  document.getElementById("story-discard").addEventListener("click", () => {
    storyEditor.proposal = null;
    storyEditor.elements["story-comparison"].close();
    updateStoryProposalControls();
    storyStatus("Proposal discarded. Draft preserved.");
  });
  storyEditor.elements["story-replacement"].addEventListener("input", () => {
    updateStoryProposalControls();
    renderStoryChanges();
  });
  storyEditor.elements["story-edit-proposal"].addEventListener("click", () => {
    const editing = storyEditor.elements["story-replacement"].hidden;
    setStoryProposalEditing(editing);
    storyEditor.elements[editing ? "story-replacement" : "story-replacement-diff"].focus();
  });
  document.getElementById("story-undo").addEventListener("click", undoStoryApplication);
  for (const id of ["story-original", "story-replacement-diff", "story-replacement"]) {
    storyEditor.elements[id].addEventListener("scroll", event => syncStoryComparisonScroll(event.target, true), { passive: true });
  }
  document.getElementById("story-repetitions").addEventListener("click", () => {
    const panel = storyEditor.elements["story-repetition-panel"];
    panel.hidden = !panel.hidden;
    if (!panel.hidden) renderStoryRepetitions();
  });
  storyEditor.elements["story-ignore-common"].addEventListener("change", renderStoryRepetitions);
  document.getElementById("story-repetition-distance").addEventListener("input", renderStoryRepetitions);
  initializeStoryWordMenu();
  initializeStoryFiles();
  document.getElementById("story-confirm-import").addEventListener("click", () => {
    if (storyEditor.pendingImport) replaceStoryDraft(storyEditor.pendingImport);
  });
  document.getElementById("story-cancel-import").addEventListener("click", () => {
    storyEditor.pendingImport = null;
    storyEditor.elements["story-import-confirmation"].hidden = true;
  });
  document.getElementById("story-export").addEventListener("click", exportStoryMarkdown);
  renderSpecialistControl();
  updateStorySelection();
}

function closeStoryEditor() {
  closeStoryViewMenu();
  document.getElementById("story-files-dialog").close();
  closeStoryWordMenu();
  if (storyEditor.controller) cancelStoryProposal();
  storyEditor.elements["story-comparison"].close();
  setStoryFullscreen(false);
  document.getElementById("story-editor").hidden = true;
  document.getElementById("conversation-view").classList.remove("story-editor-open");
  document.getElementById("open-story-editor").setAttribute("aria-expanded", "false");
}

function setStoryFullscreen(expanded) {
  closeStoryViewMenu();
  closeStoryWordMenu();
  const dialog = document.getElementById("story-fullscreen");
  if (dialog.open === expanded) return;
  const panel = storyEditor.elements["story-editor"];
  const draft = storyEditor.elements["story-document"];
  const preview = storyEditor.elements["story-preview"];
  const selection = [draft.selectionStart, draft.selectionEnd, draft.selectionDirection];
  const scroll = [panel.scrollTop, draft.scrollTop, preview.scrollTop];
  const range = window.getSelection()?.rangeCount ? window.getSelection().getRangeAt(0) : null;
  // Store node references, not a live Range: moving its ancestor collapses that range.
  const endpoints = range && preview.contains(range.commonAncestorContainer)
    ? [range.startContainer, range.startOffset, range.endContainer, range.endOffset] : null;
  if (expanded) {
    panel.before(dialog);
    dialog.append(panel);
    document.getElementById("conversation-view").classList.remove("story-editor-open");
    dialog.showModal();
  } else {
    dialog.close();
    dialog.before(panel);
    document.getElementById("conversation-view").classList.add("story-editor-open");
  }
  const toggle = document.getElementById("story-fullscreen-toggle");
  toggle.textContent = expanded ? "Return to split view" : "Full screen";
  toggle.setAttribute("aria-expanded", String(expanded));
  toggle.focus({ preventScroll: true });
  draft.setSelectionRange(...selection);
  if (endpoints) {
    const restored = document.createRange();
    restored.setStart(endpoints[0], endpoints[1]);
    restored.setEnd(endpoints[2], endpoints[3]);
    window.getSelection().removeAllRanges();
    window.getSelection().addRange(restored);
  }
  [panel.scrollTop, draft.scrollTop, preview.scrollTop] = scroll;
  if (storyEditor.controller) document.getElementById("story-loading").scrollIntoView({ block: "nearest" });
  updateStorySelection();
}

function storyStatus(message) {
  storyEditor.elements["story-status"].textContent = message;
  storyEditor.elements["story-comparison-status"].textContent = message;
}

function storyWords(text) {
  return [...text.matchAll(/[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*/gu)];
}

function setStoryDisplayMode(mode) {
  storyEditor.mode = mode;
  const raw = mode === "raw";
  storyEditor.elements["story-document"].hidden = !raw;
  storyEditor.elements["story-preview"].hidden = raw;
  storyEditor.elements["story-mode-raw"].setAttribute("aria-pressed", String(raw));
  storyEditor.elements["story-mode-view"].setAttribute("aria-pressed", String(!raw));
  storyEditor.previewController?.abort();
  if (!raw) void renderStoryPreview();
  storyEditor.elements[raw ? "story-document" : "story-preview"].focus();
  updateStorySelection();
}

async function renderStoryPreview() {
  storyEditor.viewSelection = null;
  updateStorySelection();
  storyEditor.previewController?.abort();
  const controller = new AbortController();
  storyEditor.previewController = controller;
  const revision = storyEditor.revision;
  const preview = storyEditor.elements["story-preview"];
  preview.setAttribute("aria-busy", "true");
  preview.textContent = "Rendering Markdown…";
  try {
    const result = await fetchJson("/api/story-editor/preview", {
      method: "POST", headers: { "Content-Type": "application/json" }, signal: controller.signal,
      body: JSON.stringify({ document: storyEditor.elements["story-document"].value })
    });
    if (controller.signal.aborted || revision !== storyEditor.revision || storyEditor.mode !== "view") return;
    mountStoryPreview(preview, result.renderedHtml);
    renderStoryHighlights();
    if (!preview.textContent.trim()) preview.textContent = "The draft is empty. Switch to Raw to write or open a file.";
  } catch (error) {
    if (!controller.signal.aborted && storyEditor.previewController === controller) {
      preview.textContent = `${error.message} Switch to Raw to keep editing; your draft is preserved.`;
    }
  } finally {
    if (storyEditor.previewController === controller) {
      storyEditor.previewController = null;
      preview.setAttribute("aria-busy", "false");
    }
  }
}

function updateStorySelection() {
  const draft = storyEditor.elements["story-document"];
  const selection = currentStorySelection();
  const length = selection.end - selection.start;
  // Same conservative text heuristic as Host ConservativeTokenEstimator; no model call.
  storyEditor.elements["story-token-estimate"].textContent = `~${Math.ceil(draft.value.length / 3).toLocaleString()} tokens · Whole draft`;
  storyEditor.elements["story-selection-status"].textContent =
    `${storyWords(draft.value).length} words · ${draft.value.length.toLocaleString()} characters · `
    + (length ? `${length.toLocaleString()} selected${storyEditor.mode === "view" ? " in View" : ""}${length > 16000 ? " · Select at most 16,000 characters" : ""}` : "Select a passage in Raw or View");
  const disabled = Boolean(storyEditor.controller || state.requestController)
    || length < 1 || length > 16000;
  storyEditor.elements["story-revise"].disabled = disabled;
  storyEditor.elements["story-rewrite"].disabled = disabled;
  storyEditor.elements["story-synonyms"].disabled = disabled;
  storyEditor.elements["story-agreement"].disabled = disabled;
  const reason = storyEditor.controller || state.requestController ? "Wait for the active request to finish."
    : length > 16000 ? "Select at most 16,000 characters." : length < 1 ? "Select a passage in Raw or View." : "";
  storyEditor.elements["story-revise"].title = reason;
  storyEditor.elements["story-rewrite"].title = reason || "Use new wording while preserving meaning and coherence with the surrounding text.";
  storyEditor.elements["story-synonyms"].title = reason;
  storyEditor.elements["story-agreement"].title = reason || "Check verb and noun agreement in the selected passage.";
  document.querySelectorAll(".story-repetition-synonyms").forEach(button => {
    button.disabled = Boolean(storyEditor.controller || state.requestController);
  });
  renderStorySelectionHighlight();
}

function storyDraftChanged() {
  closeStoryViewMenu();
  storyEditor.revision++;
  storyEditor.viewSelection = null;
  storyEditor.undo = null;
  updateStorySelection();
  updateStoryProposalControls();
  updateStoryFileState();
  if (storyEditor.mode === "view") void renderStoryPreview();
  if (storyEditor.proposal) storyStatus("Draft changed. Generate a new proposal before applying.");
  clearTimeout(storyEditor.repetitionTimer);
  if (!storyEditor.elements["story-repetition-panel"].hidden) {
    storyEditor.repetitionTimer = setTimeout(renderStoryRepetitions, 120);
  }
}

function updateStoryProposalControls() {
  const proposal = storyEditor.proposal;
  storyEditor.elements["story-review-proposal"].hidden = !proposal;
  storyEditor.elements["story-apply"].disabled = Boolean(storyEditor.controller)
    || !proposal || proposal.revision !== storyEditor.revision
    || !storyEditor.elements["story-replacement"].value.trim();
  storyEditor.elements["story-undo"].disabled = !storyEditor.undo
    || storyEditor.undo.revision !== storyEditor.revision;
}

function setStoryProposalEditing(editing) {
  storyEditor.elements["story-replacement"].hidden = !editing;
  storyEditor.elements["story-replacement-diff"].hidden = editing;
  highlightStoryChange(null);
  syncStoryComparisonScroll(storyEditor.elements["story-original"]);
  storyEditor.elements["story-edit-proposal"].setAttribute("aria-pressed", String(editing));
  storyEditor.elements["story-edit-proposal"].textContent = editing ? "Show changes" : "Edit proposal";
  const contextual = Boolean(storyEditor.proposal?.context);
  document.getElementById("story-original-label").textContent = contextual ? "Original sentence" : "Original selection";
  document.getElementById("story-replacement-label").textContent = contextual
    ? editing ? "Replacement for selected text only" : "Sentence with replacement" : "Proposed replacement";
  document.getElementById("story-synonym-context-note").hidden = !contextual;
}

function storySentenceContext(document, start, end) {
  const paragraphStart = document.lastIndexOf("\n\n", Math.max(0, start - 1));
  const paragraphEnd = document.indexOf("\n\n", end);
  const base = paragraphStart < 0 ? 0 : Math.min(start, paragraphStart + 2);
  const text = document.slice(base, paragraphEnd < 0 ? document.length : paragraphEnd);
  let first = base, last = base + text.length;
  for (const segment of new Intl.Segmenter(undefined, { granularity: "sentence" }).segment(text)) {
    const begin = base + segment.index, finish = begin + segment.segment.length;
    if (begin <= start && finish > start) first = begin;
    if (begin < end && finish >= end) { last = finish; break; }
  }
  return { before: document.slice(first, start).trimStart(), after: document.slice(end, last).trimEnd() };
}

function renderStoryChanges() {
  if (!storyEditor.proposal) return;
  const tokenize = text => text.match(/[\p{L}\p{M}\p{N}_]+|\s+|[^\s]/gu) ?? [];
  const left = tokenize(storyEditor.proposal.original);
  const right = tokenize(storyEditor.elements["story-replacement"].value);
  let start = 0, leftEnd = left.length, rightEnd = right.length;
  while (start < leftEnd && start < rightEnd && left[start] === right[start]) start++;
  while (leftEnd > start && rightEnd > start && left[leftEnd - 1] === right[rightEnd - 1]) { leftEnd--; rightEnd--; }
  const removed = new Map(), added = new Map();
  const rows = leftEnd - start, columns = rightEnd - start;
  // Bound comparison memory/work for large, unrelated passages. Common edges remain exact.
  const coarse = (rows + 1) * (columns + 1) > 1_000_000;
  if (coarse) {
    for (let i = start; i < leftEnd; i++) removed.set(i, 0);
    for (let j = start; j < rightEnd; j++) added.set(j, 0);
  } else {
    const stride = columns + 1;
    const lengths = new Uint16Array((rows + 1) * stride);
    for (let i = rows - 1; i >= 0; i--) {
      for (let j = columns - 1; j >= 0; j--) {
        lengths[i * stride + j] = left[start + i] === right[start + j]
          ? lengths[(i + 1) * stride + j + 1] + 1
          : Math.max(lengths[(i + 1) * stride + j], lengths[i * stride + j + 1]);
      }
    }
    let i = 0, j = 0, group = 0, changing = false;
    while (i < rows || j < columns) {
      if (i < rows && j < columns && left[start + i] === right[start + j]) {
        i++; j++;
        if (changing) group++;
        changing = false;
      } else {
        changing = true;
        if (i < rows && (j === columns || lengths[(i + 1) * stride + j] >= lengths[i * stride + j + 1])) removed.set(start + i++, group);
        else added.set(start + j++, group);
      }
    }
  }
  const render = (target, tokens, changes, tag) => {
    const fragment = document.createDocumentFragment();
    const context = storyEditor.proposal.context;
    if (context) fragment.append(document.createTextNode(context.before));
    for (let i = 0; i < tokens.length;) {
      const group = changes.get(i), changed = group !== undefined;
      let text = tokens[i++];
      while (i < tokens.length && changes.get(i) === group) text += tokens[i++];
      const node = changed ? document.createElement(tag) : document.createTextNode(text);
      if (changed) {
        node.textContent = text;
        node.dataset.changeGroup = String(group);
        node.className = `story-change story-change-${group % 4}`;
        node.tabIndex = 0;
        const paired = [...(tag === "ins" ? removed : added).values()].includes(group);
        node.title = `Change ${group + 1} · ${paired ? "matching color identifies the corresponding passage" : tag === "ins" ? "addition, no original passage" : "deletion, no replacement passage"}`;
        for (const type of ["pointerenter", "focus"]) node.addEventListener(type, () => highlightStoryChange(group));
        for (const type of ["pointerleave", "blur"]) node.addEventListener(type, () => highlightStoryChange(null));
      }
      fragment.append(node);
    }
    if (context) fragment.append(document.createTextNode(context.after));
    target.replaceChildren(fragment);
  };
  render(storyEditor.elements["story-original"], left, removed, "del");
  render(storyEditor.elements["story-replacement-diff"], right, added, "ins");
  storyEditor.elements["story-diff-note"].textContent = coarse
    ? "Large replacement: changed span highlighted as a block."
    : removed.size || added.size ? "Word-level comparison" : "No changes";
}

function highlightStoryChange(group) {
  // Correspondence belongs to the comparison preview, never the editable text.
  const preview = storyEditor.elements["story-replacement"].hidden;
  for (const node of storyEditor.elements["story-comparison"].querySelectorAll("[data-change-group]")) {
    node.classList.toggle("story-change-active", preview && group !== null && node.dataset.changeGroup === String(group));
  }
}

function syncStoryComparisonScroll(source, fromScrollEvent = false) {
  if (source.hidden) return;
  const expected = storyEditor.comparisonScrollTargets.get(source);
  storyEditor.comparisonScrollTargets.delete(source);
  // Ignore only the echo of a scroll we assigned, not a newer user movement.
  if (fromScrollEvent && expected !== undefined && Math.abs(source.scrollTop - expected) < 1) return;
  const original = storyEditor.elements["story-original"];
  const replacement = storyEditor.elements[storyEditor.elements["story-replacement"].hidden ? "story-replacement-diff" : "story-replacement"];
  const target = source === original ? replacement : original;
  const range = source.scrollHeight - source.clientHeight;
  if (range <= 0) return;
  const position = source.scrollTop / range * Math.max(0, target.scrollHeight - target.clientHeight);
  // Tolerance absorbs browser rounding and prevents reciprocal scroll events looping.
  if (Math.abs(target.scrollTop - position) > 1 || (position === 0 && target.scrollTop !== 0)) {
    target.scrollTop = position;
    storyEditor.comparisonScrollTargets.set(target, target.scrollTop);
  }
}

async function requestStoryProposal(operation) {
  if (storyEditor.controller || state.requestController) return;
  const draft = storyEditor.elements["story-document"];
  const { start, end } = currentStorySelection();
  const original = draft.value.slice(start, end);
  if (!original.trim() || end - start > 16000) {
    storyStatus("Select 1–16,000 characters to continue.");
    return;
  }
  const snapshot = { revision: storyEditor.revision, start, end, original,
    context: operation === "synonyms" ? storySentenceContext(draft.value, start, end) : null };
  const controller = new AbortController();
  storyEditor.controller = controller;
  storyEditor.elements["story-cancel"].hidden = false;
  document.getElementById("story-loading-label").textContent = operation === "synonyms" ? "Finding contextual synonyms…"
    : operation === "agreement" ? "Checking agreement…" : operation === "rewrite" ? "Rewriting selected passage…" : "Revising selected passage…";
  document.getElementById("story-loading").hidden = false;
  document.querySelector(".story-editor-actions > .story-editor-toolbar").setAttribute("aria-busy", "true");
  storyEditor.proposal = null;
  storyEditor.elements["story-comparison"].close();
  updateStorySelection();
  updateStoryProposalControls();
  updateInteractionControls();
  updateHarnessControls();
  storyStatus("Generating a proposal with the RPG specialist… You can keep editing; changes will invalidate this proposal.");
  document.getElementById("story-loading").scrollIntoView({ block: "nearest" });
  try {
    const result = await fetchJson("/api/story-editor/proposals", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      signal: controller.signal,
      body: JSON.stringify({
        operation, document: draft.value, selectionStart: start, selectionEnd: end,
        instruction: storyEditor.elements["story-instruction"].value || null,
        model: elements.modelSelector.value,
        thinking: elements.thinkingSelector.value || null
      })
    });
    if (controller.signal.aborted || storyEditor.controller !== controller) return;
    if (result.original !== original || !result.alternatives?.length) throw new Error("The proposal did not match the selected passage.");
    storyEditor.proposal = snapshot;
    storyEditor.elements["story-proposal-model"].textContent = `RPG storytelling · ${result.model ?? "Selected model"}`;
    const alternatives = storyEditor.elements["story-alternatives"];
    alternatives.replaceChildren();
    storyEditor.elements["story-replacement"].value = result.alternatives[0].text;
    setStoryProposalEditing(false);
    renderStoryChanges();
    for (const [index, alternative] of result.alternatives.entries()) {
      if (result.alternatives.length === 1) break;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "secondary-button";
      button.textContent = alternative.text;
      button.setAttribute("aria-pressed", String(index === 0));
      const item = document.createElement("div");
      item.append(button);
      if (alternative.explanation) {
        const reason = document.createElement("small");
        reason.textContent = alternative.explanation;
        item.append(reason);
      }
      button.addEventListener("click", () => {
        storyEditor.elements["story-replacement"].value = alternative.text;
        renderStoryChanges();
        alternatives.querySelectorAll("button").forEach(option => option.setAttribute("aria-pressed", String(option === button)));
        updateStoryProposalControls();
      });
      alternatives.append(item);
    }
    storyEditor.elements["story-comparison"].showModal();
    storyStatus(snapshot.revision === storyEditor.revision
      ? "Compare the original and replacement. The draft has not changed."
      : "Draft changed during generation. Generate a new proposal before applying.");
  } catch (error) {
    if (storyEditor.controller === controller) {
      storyStatus(error.name === "AbortError" ? "Proposal cancelled. Draft preserved." : `${error.message} Draft preserved.`);
    }
  } finally {
    if (storyEditor.controller === controller) {
      storyEditor.controller = null;
      storyEditor.elements["story-cancel"].hidden = true;
      document.getElementById("story-loading").hidden = true;
      document.querySelector(".story-editor-actions > .story-editor-toolbar").setAttribute("aria-busy", "false");
      updateStorySelection();
      updateStoryProposalControls();
      updateInteractionControls();
      updateHarnessControls();
    }
  }
}

function cancelStoryProposal() {
  storyEditor.controller?.abort();
}

function applyStoryProposal() {
  const draft = storyEditor.elements["story-document"];
  const proposal = storyEditor.proposal;
  const replacement = storyEditor.elements["story-replacement"].value;
  if (!proposal || proposal.revision !== storyEditor.revision
    || draft.value.slice(proposal.start, proposal.end) !== proposal.original || !replacement.trim()) {
    storyStatus("The draft or selection changed. Generate a new proposal before applying.");
    return;
  }
  const before = draft.value;
  if (before.length - proposal.original.length + replacement.length > 200000) {
    storyStatus("The replacement would exceed the 200,000-character draft limit.");
    return;
  }
  draft.setRangeText(replacement, proposal.start, proposal.end, "select");
  storyDraftChanged();
  storyEditor.undo = { document: before, revision: storyEditor.revision };
  storyEditor.proposal = null;
  storyEditor.elements["story-comparison"].close();
  updateStoryProposalControls();
  storyStatus("Replacement applied only to the selected passage. Undo is available.");
  storyEditor.elements[storyEditor.mode === "raw" ? "story-document" : "story-preview"].focus();
}

function undoStoryApplication() {
  const undo = storyEditor.undo;
  if (!undo || undo.revision !== storyEditor.revision) return;
  storyEditor.elements["story-document"].value = undo.document;
  storyDraftChanged();
  storyStatus("Last application undone.");
}

const storyCommonWords = new Set(("a o as os um uma uns umas de da do das dos e é em no na nos nas "
  + "por para com sem que se não ao aos à às seu sua seus suas ele ela eles elas eu tu você nós vocês "
  + "foi era ser estar como mais mas ou já lhe isso isto esse essa este esta the a an and or of to in on "
  + "at for from with without is was were be been it its he she they we you his her their my that this "
  + "these those not but as by had has have").split(/\s+/));

function renderStoryRepetitions() {
  closeStoryWordMenu();
  const draft = storyEditor.elements["story-document"];
  const revision = storyEditor.revision;
  const distance = document.getElementById("story-repetition-distance");
  if (!distance.checkValidity() || !distance.value) {
    storyEditor.elements["story-repetition-results"].textContent = "Choose a distance from 1 to 500 words.";
    return;
  }
  const maximumDistance = Number(distance.value);
  const groups = new Map();
  for (const [index, match] of storyWords(draft.value).entries()) {
    const word = match[0].normalize("NFC").toLocaleLowerCase("pt-BR");
    if (storyEditor.ignoredWords.has(word)) continue;
    if (storyEditor.elements["story-ignore-common"].checked && storyCommonWords.has(word)) continue;
    if (!groups.has(word)) groups.set(word, []);
    groups.get(word).push({ start: match.index, end: match.index + match[0].length, index });
  }
  const repeated = [...groups].map(([word, occurrences]) => [word, occurrences.filter((item, index) =>
    index > 0 && item.index - occurrences[index - 1].index <= maximumDistance
      || index + 1 < occurrences.length && occurrences[index + 1].index - item.index <= maximumDistance)])
    .filter(([, occurrences]) => occurrences.length > 1)
    .sort((left, right) => right[1].length - left[1].length || left[0].localeCompare(right[0], "pt-BR"));
  const results = storyEditor.elements["story-repetition-results"];
  results.replaceChildren();
  if (!repeated.length) results.textContent = "No repeated words found with these filters.";
  for (const [word, occurrences] of repeated.slice(0, 40)) {
    let next = 0, selected = 0;
    const item = document.createElement("div");
    item.className = "story-repetition-item";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button";
    button.dataset.storyWord = word;
    button.setAttribute("aria-haspopup", "menu");
    button.title = "Click to cycle nearby occurrences. Right-click for highlight and ignore options.";
    button.textContent = `${word} · ${occurrences.length}`;
    const choose = index => {
      if (revision !== storyEditor.revision) { renderStoryRepetitions(); return false; }
      selected = index;
      const occurrence = occurrences[selected];
      if (storyEditor.mode !== "raw") setStoryDisplayMode("raw");
      draft.focus();
      draft.setSelectionRange(occurrence.start, occurrence.end);
      storyStatus(`${word}: occurrence ${selected + 1} of ${occurrences.length}.`);
      position.textContent = `${selected + 1} / ${occurrences.length}`;
      updateStorySelection();
      return true;
    };
    button.addEventListener("click", () => {
      if (choose(next)) next = (next + 1) % occurrences.length;
    });
    const position = document.createElement("span");
    position.className = "runtime-note";
    position.textContent = `1 / ${occurrences.length}`;
    position.setAttribute("aria-label", `Selected occurrence of ${word}`);
    const synonyms = document.createElement("button");
    synonyms.type = "button";
    synonyms.className = "secondary-button story-repetition-synonyms";
    synonyms.textContent = "Synonyms";
    synonyms.setAttribute("aria-label", `Find synonyms for ${word}`);
    synonyms.addEventListener("click", () => {
      if (storyEditor.controller || state.requestController) return;
      if (choose(selected)) void requestStoryProposal("synonyms");
    });
    item.append(button, position, synonyms);
    results.append(item);
  }
  if (repeated.length > 40) {
    const note = document.createElement("p");
    note.textContent = `Showing the 40 most repeated words of ${repeated.length}.`;
    results.append(note);
  }
  updateStorySelection();
}

function closeStoryWordMenu(restoreFocus = false) {
  document.getElementById("story-word-menu").hidden = true;
  if (restoreFocus && storyEditor.wordMenuTrigger?.isConnected) storyEditor.wordMenuTrigger.focus({ preventScroll: true });
}

function initializeStoryWordMenu() {
  const menu = document.getElementById("story-word-menu");
  const results = storyEditor.elements["story-repetition-results"];
  const show = event => {
    const trigger = event.target.closest("button[data-story-word]");
    if (!trigger) return;
    event.preventDefault();
    storyEditor.wordMenuTrigger = trigger;
    menu.hidden = false;
    const rect = trigger.getBoundingClientRect();
    const x = event.clientX || rect.left, y = event.clientY || rect.bottom;
    menu.style.left = `${Math.max(8, Math.min(x, innerWidth - menu.offsetWidth - 8))}px`;
    menu.style.top = `${Math.max(8, Math.min(y, innerHeight - menu.offsetHeight - 8))}px`;
    menu.querySelector("button").focus({ preventScroll: true });
  };
  results.addEventListener("contextmenu", show);
  results.addEventListener("keydown", event => {
    if (event.key === "ContextMenu" || event.key === "F10" && event.shiftKey) show(event);
  });
  document.addEventListener("pointerdown", event => {
    if (!menu.contains(event.target)) closeStoryWordMenu();
  });
  document.addEventListener("keydown", event => {
    if (menu.hidden) return;
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      closeStoryWordMenu(true);
    } else if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
      event.preventDefault();
      const buttons = [...menu.querySelectorAll("button")];
      const index = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1
        : (buttons.indexOf(document.activeElement) + (event.key === "ArrowDown" ? 1 : buttons.length - 1)) % buttons.length;
      buttons[index].focus();
    } else if (event.key === "Tab") closeStoryWordMenu(true);
  }, true);
  document.getElementById("story-highlight-word").addEventListener("click", () => {
    storyEditor.highlightedWord = storyEditor.wordMenuTrigger.dataset.storyWord;
    closeStoryWordMenu();
    if (storyEditor.mode !== "view") setStoryDisplayMode("view");
    else renderStoryHighlights();
  });
  document.getElementById("story-ignore-word").addEventListener("click", () => {
    const word = storyEditor.wordMenuTrigger.dataset.storyWord;
    storyEditor.ignoredWords.add(word);
    if (storyEditor.highlightedWord === word) {
      storyEditor.highlightedWord = null;
      renderStoryHighlights();
    }
    closeStoryWordMenu();
    renderStoryRepetitions();
    renderStoryIgnoredWords();
    document.getElementById("story-ignored").open = true;
    document.getElementById("story-ignored-words").lastElementChild?.focus();
    storyStatus(`${word} ignored in this draft. Restore it under Ignored words.`);
  });
  document.getElementById("story-clear-highlights").addEventListener("click", () => {
    storyEditor.highlightedWord = null;
    renderStoryHighlights();
    storyEditor.elements["story-preview"].focus();
  });
}

function renderStoryIgnoredWords() {
  const list = document.getElementById("story-ignored-words");
  list.replaceChildren();
  document.getElementById("story-ignored").hidden = storyEditor.ignoredWords.size === 0;
  for (const word of storyEditor.ignoredWords) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button";
    button.textContent = `Restore ${word}`;
    button.addEventListener("click", () => {
      storyEditor.ignoredWords.delete(word);
      renderStoryIgnoredWords();
      renderStoryRepetitions();
      storyEditor.elements["story-ignore-common"].focus();
    });
    list.append(button);
  }
}

function renderStoryHighlights() {
  const preview = storyEditor.elements["story-preview"];
  preview.querySelectorAll("mark.story-word-highlight").forEach(mark => mark.replaceWith(...mark.childNodes));
  preview.normalize();
  const word = storyEditor.highlightedWord;
  document.getElementById("story-highlights").hidden = !word;
  if (!word) return;
  const nodes = [], walker = document.createTreeWalker(preview, NodeFilter.SHOW_TEXT);
  let text = "";
  while (walker.nextNode()) {
    nodes.push({ node: walker.currentNode, start: text.length });
    text += walker.currentNode.textContent;
  }
  const matches = storyWords(text).filter(match => match[0].normalize("NFC").toLocaleLowerCase("pt-BR") === word);
  // Split only text nodes, backwards, retaining all renderer coordinates and HTML boundaries.
  let matchIndex = matches.length - 1;
  for (const entry of nodes.reverse()) {
    const nodeEnd = entry.start + entry.node.length;
    while (matchIndex >= 0 && matches[matchIndex].index >= nodeEnd) matchIndex--;
    for (let index = matchIndex; index >= 0 && matches[index].index + matches[index][0].length > entry.start; index--) {
      const match = matches[index];
      const start = Math.max(entry.start, match.index), end = Math.min(nodeEnd, match.index + match[0].length);
      if (end <= start) continue;
      const range = document.createRange();
      range.setStart(entry.node, start - entry.start);
      range.setEnd(entry.node, end - entry.start);
      const mark = document.createElement("mark");
      mark.className = "story-word-highlight";
      range.surroundContents(mark);
    }
  }
  document.getElementById("story-highlight-status").textContent = `${word}: ${matches.length} highlighted in View · Whole draft`;
  preview.querySelector("mark.story-word-highlight")?.scrollIntoView({ block: "nearest" });
}

function replaceStoryDraft(imported) {
  if (imported.source.workspaceId !== activeWorkspaceProfile()?.id) {
    storyStatus("The active project changed. Reopen the workspace file selector. Draft preserved.");
    return;
  }
  cancelStoryProposal();
  storyEditor.ignoredWords.clear();
  storyEditor.highlightedWord = null;
  renderStoryIgnoredWords();
  renderStoryHighlights();
  storyEditor.elements["story-document"].value = imported.text;
  storyEditor.fileSource = imported.source;
  document.getElementById("story-file-save-status").textContent = "";
  storyEditor.elements["story-file-name"].value = imported.name.replace(/\.[^.]+$/, ".md");
  storyEditor.pendingImport = null;
  storyEditor.proposal = null;
  storyEditor.elements["story-import-confirmation"].hidden = true;
  storyEditor.elements["story-comparison"].close();
  storyDraftChanged();
  storyStatus("Workspace file opened. Apply edits to the draft, then Save to workspace to update the original.");
}

function exportStoryMarkdown() {
  const name = storyEditor.elements["story-file-name"].value.trim()
    .replace(/[/\\<>:"|?*\u0000-\u001f]/g, "_").replace(/\.(md|txt)$/i, "") || "story";
  const url = URL.createObjectURL(new Blob([storyEditor.elements["story-document"].value], { type: "text/markdown;charset=utf-8" }));
  const link = document.createElement("a");
  link.href = url;
  link.download = `${name}.md`;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
  storyStatus("Markdown copy exported.");
}
