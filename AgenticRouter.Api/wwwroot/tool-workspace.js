// The chat is permanent. Auxiliary tabs share its composer and execution state.
const toolWorkspace = { active: "chat", split: 44 };

function initializeToolWorkspace() {
  const view = document.getElementById("conversation-view");
  const workspace = document.createElement("div");
  workspace.id = "tool-workspace";
  workspace.innerHTML = `
    <section id="chat-pane" aria-label="Chat">
      <div class="workspace-pane-heading" role="tablist" aria-label="Conversation">
        <button id="chat-tab" type="button" role="tab" aria-selected="true" aria-controls="chat-content">Chat <small>Fixed</small></button>
      </div>
      <div id="chat-content" role="tabpanel" aria-labelledby="chat-tab"></div>
    </section>
    <div id="tool-divider" role="separator" tabindex="0" aria-label="Resize chat and tools"
      aria-orientation="vertical" aria-valuemin="25" aria-valuemax="70" aria-valuenow="44" hidden></div>
    <section id="tool-pane" aria-label="Auxiliary tools" hidden>
      <div class="workspace-pane-heading" role="tablist" aria-label="Tools">
        <button id="story-tool-tab" type="button" role="tab" aria-selected="false" aria-controls="story-editor">Storytelling Editor</button>
      </div>
      <div id="tool-content"></div>
    </section>`;
  view.querySelector(".chat-header").after(workspace);
  document.getElementById("chat-content").append(elements.messages, document.getElementById("conversation-history-loader"), elements.jumpLatest);
  document.getElementById("tool-content").append(document.getElementById("story-editor"), document.getElementById("story-fullscreen"));
  const editor = document.getElementById("story-editor");
  editor.setAttribute("role", "tabpanel");
  editor.setAttribute("aria-labelledby", "story-tool-tab");
  const body = document.createElement("div");
  body.className = "story-editor-body";
  const sidebar = document.createElement("aside");
  sidebar.id = "story-controls";
  sidebar.className = "story-controls";
  sidebar.setAttribute("aria-label", "Editor controls");
  const reading = document.createElement("div");
  reading.className = "story-reading-area";
  for (const selector of [".story-document-toolbar", "#story-document", "#story-preview", "#story-highlights", ".story-draft-status"]) reading.append(editor.querySelector(selector));
  for (const selector of [".story-file-toolbar", ".story-workspace-source", "#story-file-save-status", "#story-import-confirmation", ".story-editor-actions", "#story-repetition-panel"]) sidebar.append(editor.querySelector(selector));
  const output = document.createElement("section");
  output.id = "story-tool-output";
  output.className = "story-tool-output";
  output.setAttribute("aria-label", "Tool response");
  output.hidden = true;
  output.innerHTML = `<div class="story-tool-output-heading"><strong>Assistant</strong><button type="button" class="icon-button" aria-label="Close tool response">×</button></div><div class="story-tool-output-body benchmark-markdown"></div>`;
  output.querySelector("button").addEventListener("click", () => { output.hidden = true; });
  reading.append(output);
  body.append(sidebar, reading);
  editor.querySelector(".story-editor-heading").after(body);
  const controls = document.createElement("button");
  controls.id = "story-controls-toggle";
  controls.className = "secondary-button";
  controls.type = "button";
  controls.textContent = "Controls";
  controls.setAttribute("aria-controls", "story-controls");
  controls.setAttribute("aria-expanded", "true");
  controls.addEventListener("click", () => {
    sidebar.hidden = !sidebar.hidden;
    body.classList.toggle("controls-collapsed", sidebar.hidden);
    controls.setAttribute("aria-expanded", String(!sidebar.hidden));
  });
  editor.querySelector(".story-heading-actions").prepend(controls);
  const target = document.createElement("div");
  target.className = "composer-target";
  target.innerHTML = `<span id="composer-target-label" role="status">Send to Chat</span><span id="composer-target-hint">One composer for chat and tools</span>`;
  elements.composerShell.prepend(target);
  document.getElementById("chat-tab").addEventListener("click", () => selectToolWorkspaceTab("chat"));
  document.getElementById("story-tool-tab").addEventListener("click", () => selectToolWorkspaceTab("story-editor"));
  const divider = document.getElementById("tool-divider");
  const setSplit = value => {
    toolWorkspace.split = Math.max(25, Math.min(70, value));
    workspace.style.setProperty("--chat-split", `${toolWorkspace.split}%`);
    divider.setAttribute("aria-valuenow", String(Math.round(toolWorkspace.split)));
  };
  divider.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    divider.setPointerCapture(event.pointerId);
    divider.classList.add("dragging");
    event.preventDefault();
  });
  divider.addEventListener("pointermove", event => {
    if (!divider.hasPointerCapture(event.pointerId)) return;
    const bounds = workspace.getBoundingClientRect();
    setSplit((event.clientX - bounds.left) / bounds.width * 100);
  });
  for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) divider.addEventListener(type, () => divider.classList.remove("dragging"));
  divider.addEventListener("keydown", event => {
    const values = { ArrowLeft: toolWorkspace.split - 2, ArrowRight: toolWorkspace.split + 2, Home: 25, End: 70 };
    if (!(event.key in values)) return;
    event.preventDefault();
    setSplit(values[event.key]);
  });
  divider.addEventListener("dblclick", () => setSplit(44));
  setSplit(44);
  renderToolWorkspace();
}

function reportToolActivity(message) {
  elements.emptyState?.remove();
  const entry = document.createElement("p");
  entry.className = "tool-workspace-activity";
  entry.setAttribute("role", "status");
  entry.textContent = `Storytelling Editor · ${message}`;
  elements.messages.append(entry);
  if (state.autoFollow) requestAnimationFrame(scrollToBottom);
}

function selectToolWorkspaceTab(id) {
  toolWorkspace.active = id;
  renderToolWorkspace();
}

function renderToolWorkspace() {
  const workspace = document.getElementById("tool-workspace");
  if (!workspace) return;
  const open = !document.getElementById("story-editor").hidden;
  if (!open) toolWorkspace.active = "chat";
  workspace.classList.toggle("has-tool", open);
  workspace.dataset.active = toolWorkspace.active;
  document.getElementById("tool-pane").hidden = !open;
  document.getElementById("tool-divider").hidden = !open;
  document.getElementById("chat-tab").setAttribute("aria-selected", String(toolWorkspace.active === "chat"));
  document.getElementById("story-tool-tab").setAttribute("aria-selected", String(toolWorkspace.active === "story-editor"));
  document.getElementById("composer-target-label").textContent = toolWorkspace.active === "story-editor" ? "Send to Storytelling Editor" : "Send to Chat";
  document.getElementById("composer-target-hint").textContent = toolWorkspace.active === "story-editor" ? "Actions appear in Chat · Explicit requests can change the destination" : "One composer for chat and tools";
}

function captureToolWindowContext() {
  if (toolWorkspace.active !== "story-editor" || document.getElementById("story-editor").hidden) return null;
  const draft = storyEditor.elements["story-document"];
  const selection = currentStorySelection();
  return { toolId: "story-editor", document: draft.value, selectionStart: selection.start, selectionEnd: selection.end,
    revision: storyEditor.revision, instruction: storyEditor.elements["story-instruction"].value || null };
}

function toolWindowContextMatches(left, right) {
  if (!left || !right) return !left && !right;
  return left.toolId === right.toolId && left.revision === right.revision
    && left.selectionStart === right.selectionStart && left.selectionEnd === right.selectionEnd
    && (left.instruction ?? "") === (right.instruction ?? "")
    && left.document === right.document;
}

function receiveToolWindowEvent(event, historical) {
  const output = event.toolWindow;
  if (!output || historical) return;
  const panel = document.getElementById("story-tool-output");
  if (event.type === "tool-window.delta") {
    panel.hidden = false;
    const body = panel.querySelector(".story-tool-output-body");
    if (body.dataset.requestId !== event.requestId) {
      body.replaceChildren();
      body.dataset.requestId = event.requestId;
    }
    body.append(document.createTextNode(output.delta ?? ""));
  }
  if (event.type === "tool-window.response") {
    panel.hidden = false;
    const body = panel.querySelector(".story-tool-output-body");
    body.innerHTML = output.renderedHtml ?? "";
    secureRenderedLinks(body);
  }
  if (event.type === "tool-window.proposal") {
    if (output.revision !== storyEditor.revision
      || storyEditor.elements["story-document"].value.slice(output.selectionStart, output.selectionEnd) !== output.original) {
      storyStatus("Draft changed during generation. The proposal was not applied; submit a new request.");
      reportToolActivity("Draft changed during generation; proposal rejected without applying changes.");
      return;
    }
    storyEditor.proposal = { revision: output.revision, start: output.selectionStart, end: output.selectionEnd, original: output.original };
    storyEditor.elements["story-proposal-model"].textContent = `Shared composer · ${event.selectedModel ?? "Selected model"}`;
    storyEditor.elements["story-alternatives"].replaceChildren();
    storyEditor.elements["story-replacement"].value = output.replacement;
    setStoryProposalEditing(false);
    renderStoryChanges();
    updateStoryProposalControls();
    storyStatus("Proposal ready. Review it before applying; the draft has not changed.");
    // Do not interrupt the shared composer with a modal during generation.
  }
}
