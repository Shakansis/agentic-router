const composerFiles = { controller: null, timer: null, match: null, files: [], index: 0, known: new Map() };

function composerText(node) {
  if (node.nodeType === Node.TEXT_NODE) return node.data.replace(/\u00a0/g, " ");
  if (node.dataset?.fileToken) return node.dataset.fileToken;
  // Chromium keeps a trailing BR as a caret placeholder, including after inline widgets.
  if (node.nodeName === "BR") return node.nextSibling ? "\n" : "";
  let value = "";
  for (const child of node.childNodes) {
    const block = child.nodeName === "DIV" || child.nodeName === "P";
    if (block && value && !value.endsWith("\n")) value += "\n";
    value += composerText(child);
    if (block && child.nextSibling && !value.endsWith("\n")) value += "\n";
  }
  return value;
}

function initializeComposerFiles() {
  const input = elements.messageInput;
  let disabled = false, readOnly = false;
  const editable = () => {
    input.contentEditable = String(!disabled && !readOnly);
    input.setAttribute("aria-disabled", String(disabled));
    input.setAttribute("aria-readonly", String(readOnly));
    input.tabIndex = disabled ? -1 : 0;
    if (disabled || readOnly) closeComposerFileMenu();
  };
  // Keep the existing composer/queue/steering value contract while rendering inline references.
  Object.defineProperties(input, {
    value: { get: () => composerText(input), set: value => {
      closeComposerFileMenu();
      input.replaceChildren();
      const text = String(value ?? "");
      const workspaceId = activeWorkspaceProfile()?.id;
      let offset = 0;
      while (offset < text.length) {
        let next = null;
        for (const [token, file] of composerFiles.known) {
          if (file.workspaceId !== workspaceId) continue;
          const index = text.indexOf(token, offset);
          if (index >= 0 && (!next || index < next.index)) next = { token, file, index };
        }
        if (!next) { input.append(document.createTextNode(text.slice(offset))); break; }
        input.append(document.createTextNode(text.slice(offset, next.index)), composerFileBadge(next.file, next.token));
        offset = next.index + next.token.length;
      }
      updateComposerEmptyState();
    } },
    disabled: { get: () => disabled, set: value => { disabled = Boolean(value); editable(); } },
    readOnly: { get: () => readOnly, set: value => { readOnly = Boolean(value); editable(); } },
    placeholder: { get: () => input.dataset.placeholder, set: value => { input.dataset.placeholder = value; } }
  });
  input.addEventListener("input", () => { updateComposerEmptyState(); searchComposerFiles(); });
  input.addEventListener("keyup", event => { if (!["ArrowUp", "ArrowDown", "Escape", "Enter"].includes(event.key)) searchComposerFiles(); });
  input.addEventListener("click", event => {
    const remove = event.target.closest(".composer-file-remove");
    if (remove) {
      event.preventDefault(); event.stopPropagation();
      if (disabled || readOnly) return;
      input.focus();
      const range = document.createRange(); range.selectNode(remove.parentElement);
      getSelection().removeAllRanges(); getSelection().addRange(range);
      document.execCommand("delete");
    } else searchComposerFiles();
  });
  input.addEventListener("paste", event => {
    if ([...(event.clipboardData?.items ?? [])].some(item => item.type.startsWith("image/"))) return;
    event.preventDefault();
    document.execCommand("insertText", false, event.clipboardData?.getData("text/plain") ?? "");
  });
  for (const type of ["copy", "cut"]) input.addEventListener(type, event => {
    const selection = getSelection();
    if (!selection?.rangeCount || !input.contains(selection.anchorNode) || !input.contains(selection.focusNode)) return;
    event.preventDefault();
    event.clipboardData.setData("text/plain", composerText(selection.getRangeAt(0).cloneContents()));
    if (type === "cut" && !disabled && !readOnly) document.execCommand("delete");
  });
  input.addEventListener("beforeinput", event => { if (event.inputType.startsWith("format")) event.preventDefault(); });
  input.addEventListener("drop", event => {
    if ([...(event.dataTransfer?.files ?? [])].some(file => file.type.startsWith("image/"))) return;
    event.preventDefault(); // Do not import arbitrary HTML or file contents via native editing.
  });
  input.addEventListener("blur", () => closeComposerFileMenu());
  document.getElementById("composer-file-list").addEventListener("pointerdown", event => event.preventDefault());
  document.getElementById("composer-file-list").addEventListener("click", event => {
    const option = event.target.closest("[data-file-index]");
    if (option) insertComposerFile(Number(option.dataset.fileIndex));
  });
  editable(); updateComposerEmptyState();
}

function updateComposerEmptyState() { elements.messageInput.dataset.empty = String(!elements.messageInput.value.trim()); }

function composerFileBadge(file, token) {
  const badge = document.createElement("span");
  badge.className = "composer-file-badge";
  badge.contentEditable = "false";
  badge.dataset.fileToken = token;
  badge.dataset.filePath = file.path;
  badge.title = file.path;
  const name = document.createElement("span"); name.textContent = file.name;
  const remove = document.createElement("button");
  remove.type = "button"; remove.tabIndex = -1; remove.className = "composer-file-remove";
  remove.textContent = "×"; remove.setAttribute("aria-label", `Remove reference to ${file.path}`);
  badge.append(name, remove);
  return badge;
}

function composerAtMatch() {
  const selection = getSelection();
  if (!selection?.rangeCount || !selection.isCollapsed || elements.messageInput.disabled || elements.messageInput.readOnly) return null;
  const range = selection.getRangeAt(0), node = range.startContainer;
  if (node.nodeType !== Node.TEXT_NODE || !elements.messageInput.contains(node) || node.parentElement.closest(".composer-file-badge")) return null;
  const match = node.data.slice(0, range.startOffset).match(/(?:^|[\s([{])@([^\n@]*)$/u);
  if (!match) return null;
  return { node, start: range.startOffset - match[1].length - 1, end: range.startOffset, query: match[1], workspaceId: activeWorkspaceProfile()?.id };
}

function closeComposerFileMenu() {
  clearTimeout(composerFiles.timer);
  composerFiles.controller?.abort(); composerFiles.controller = null;
  composerFiles.match = null; composerFiles.files = [];
  document.getElementById("composer-file-picker").hidden = true;
  elements.messageInput.setAttribute("aria-expanded", "false");
  elements.messageInput.removeAttribute("aria-activedescendant");
}

function searchComposerFiles() {
  const match = composerAtMatch();
  closeComposerFileMenu();
  if (!match) return;
  composerFiles.match = match;
  document.getElementById("composer-file-picker").hidden = false;
  elements.messageInput.setAttribute("aria-expanded", "true");
  document.getElementById("composer-file-list").replaceChildren();
  const status = document.getElementById("composer-file-status");
  status.textContent = match.workspaceId ? "Searching project files…" : "Select a project with a workspace first.";
  if (!match.workspaceId) return;
  const controller = new AbortController(); composerFiles.controller = controller;
  composerFiles.timer = setTimeout(async () => {
    try {
      const result = await fetchJson(`/api/workspace/files?${new URLSearchParams({ workspaceId: match.workspaceId, query: match.query })}`, { signal: controller.signal });
      if (controller.signal.aborted || composerFiles.match !== match || activeWorkspaceProfile()?.id !== match.workspaceId) return;
      composerFiles.files = result.files; composerFiles.index = 0;
      status.textContent = result.files.length
        ? `${result.partial ? "First " : ""}${result.files.length} files · ↑↓ choose · Enter insert · Esc close${result.partial ? " · Refine by folder/name" : ""}`
        : result.partial ? "No matches in the scanned portion. Refine with a folder path." : "No matching project files.";
      renderComposerFileOptions();
    } catch (error) { if (!controller.signal.aborted) status.textContent = error.message; }
  }, 150);
}

function renderComposerFileOptions() {
  const list = document.getElementById("composer-file-list"); list.replaceChildren();
  composerFiles.files.forEach((file, index) => {
    const option = document.createElement("div");
    option.id = `composer-file-option-${index}`; option.dataset.fileIndex = index;
    option.setAttribute("role", "option"); option.setAttribute("aria-selected", String(index === composerFiles.index));
    const name = document.createElement("strong"), path = document.createElement("small");
    name.textContent = file.name; path.textContent = file.path;
    option.append(name, path); list.append(option);
  });
  const selected = list.children[composerFiles.index];
  if (selected) {
    elements.messageInput.setAttribute("aria-activedescendant", selected.id);
    selected.scrollIntoView({ block: "nearest" });
  }
}

function insertComposerFile(index) {
  const match = composerFiles.match, file = composerFiles.files[index], current = composerAtMatch();
  if (!match || !file || !current || match.workspaceId !== activeWorkspaceProfile()?.id
    || current.node !== match.node || current.start !== match.start || current.end !== match.end || current.query !== match.query) { closeComposerFileMenu(); return; }
  const ticks = "`".repeat(Math.max(0, ...(file.path.match(/`+/g) ?? []).map(run => run.length)) + 1);
  const token = `${ticks}${file.path}${ticks}`;
  composerFiles.known.set(token, { ...file, workspaceId: match.workspaceId });
  const range = document.createRange(); range.setStart(match.node, match.start); range.setEnd(match.node, match.end);
  getSelection().removeAllRanges(); getSelection().addRange(range);
  const badge = composerFileBadge(file, token);
  badge.id = `composer-insert-${crypto.randomUUID()}`;
  // Browser editing preserves native undo/redo; HTML is built only from escaped DOM text.
  document.execCommand("insertHTML", false, badge.outerHTML + "&nbsp;");
  const inserted = document.getElementById(badge.id);
  if (inserted) {
    inserted.removeAttribute("id");
    const caret = document.createRange(), following = inserted.nextSibling;
    if (following?.nodeType === Node.TEXT_NODE) caret.setStart(following, 1);
    else caret.setStartAfter(inserted);
    caret.collapse(true);
    getSelection().removeAllRanges(); getSelection().addRange(caret);
  }
  updateComposerEmptyState();
  closeComposerFileMenu();
}

function handleComposerMentionKey(event) {
  if (event.isComposing) return false;
  const open = !document.getElementById("composer-file-picker").hidden;
  if (open && ["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape"].includes(event.key)) {
    if (event.key === "Tab" && !composerFiles.files.length) { closeComposerFileMenu(); return false; }
    event.preventDefault();
    if (event.key === "Escape") closeComposerFileMenu();
    else if (["Enter", "Tab"].includes(event.key)) insertComposerFile(composerFiles.index);
    else if (composerFiles.files.length) {
      composerFiles.index = (composerFiles.index + (event.key === "ArrowDown" ? 1 : -1) + composerFiles.files.length) % composerFiles.files.length;
      renderComposerFileOptions();
    }
    return true;
  }
  if (event.key === "Enter" && event.shiftKey) {
    event.preventDefault(); document.execCommand("insertLineBreak"); return true;
  }
  return false;
}
