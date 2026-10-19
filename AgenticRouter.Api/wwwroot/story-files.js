const storyFiles = { directory: ".", entries: [], controller: null, workspaceId: null };

function initializeStoryFiles() {
  const dialog = document.getElementById("story-files-dialog");
  document.getElementById("story-open-file").addEventListener("click", () => {
    if (storyEditor.fileSaving) return;
    dialog.showModal();
    void loadStoryFiles(".");
  });
  document.getElementById("story-files-close").addEventListener("click", () => dialog.close());
  dialog.addEventListener("close", () => {
    storyFiles.controller?.abort();
    if (!storyEditor.elements["story-editor"].hidden) document.getElementById("story-open-file").focus();
  });
  document.getElementById("story-files-up").addEventListener("click", () => {
    const parts = storyFiles.directory.split("/");
    parts.pop();
    void loadStoryFiles(parts.join("/") || ".");
  });
  document.getElementById("story-files-refresh").addEventListener("click", () => void loadStoryFiles(storyFiles.directory));
  document.getElementById("story-files-filter").addEventListener("input", renderStoryFiles);
  document.getElementById("story-save-file").addEventListener("click", saveStoryWorkspaceFile);
  updateStoryFileState();
}

function updateStoryFileState() {
  const draft = storyEditor.elements["story-document"];
  if (!draft) return;
  const source = storyEditor.fileSource;
  const active = activeWorkspaceProfile()?.id;
  const changed = source && source.document !== draft.value;
  const wrongProject = source && source.workspaceId !== active;
  document.getElementById("story-file-source").textContent = source
    ? `${source.path} · ${wrongProject ? "Different project — return to its workspace to save" : changed ? "Unsaved changes" : "Saved in workspace"}`
    : "Unsaved draft · Open a workspace file to save changes";
  document.getElementById("story-save-file").disabled = !source || !changed || wrongProject || storyEditor.fileSaving;
  document.getElementById("story-save-file").textContent = storyEditor.fileSaving ? "Saving…" : "Save to workspace";
  document.getElementById("story-open-file").disabled = !active || storyEditor.fileSaving;
  document.getElementById("story-confirm-import").disabled = storyEditor.fileSaving;
  if (storyFiles.workspaceId && storyFiles.workspaceId !== active) {
    storyFiles.directory = ".";
    storyFiles.controller?.abort();
    storyFiles.entries = [];
    document.getElementById("story-files-list").replaceChildren();
    document.getElementById("story-files-status").textContent = "Project changed. Refresh to browse its workspace.";
  }
}

async function loadStoryFiles(directory) {
  storyFiles.controller?.abort();
  const controller = new AbortController();
  storyFiles.controller = controller;
  const workspaceId = activeWorkspaceProfile()?.id;
  const status = document.getElementById("story-files-status");
  storyFiles.workspaceId = workspaceId;
  storyFiles.entries = [];
  document.getElementById("story-files-list").replaceChildren();
  status.textContent = "Loading workspace files…";
  document.getElementById("story-files-project").textContent = activeWorkspaceProfile()?.name ?? "No active project";
  try {
    if (!workspaceId) throw new Error("Select a project with a trusted workspace first.");
    const result = await fetchJson(`/api/story-editor/files?${new URLSearchParams({ workspaceId, directory })}`, { signal: controller.signal });
    if (controller.signal.aborted || workspaceId !== activeWorkspaceProfile()?.id) return;
    storyFiles.directory = result.directory;
    storyFiles.entries = result.entries;
    document.getElementById("story-files-filter").value = "";
    document.getElementById("story-files-directory").textContent = result.directory === "." ? "Workspace root" : result.directory;
    document.getElementById("story-files-up").disabled = result.directory === ".";
    renderStoryFiles();
  } catch (error) {
    if (!controller.signal.aborted) status.textContent = error.message;
  }
}

function renderStoryFiles() {
  const list = document.getElementById("story-files-list");
  const query = document.getElementById("story-files-filter").value.toLocaleLowerCase();
  const entries = storyFiles.entries.filter(entry => entry.name.toLocaleLowerCase().includes(query));
  list.replaceChildren();
  document.getElementById("story-files-status").textContent = entries.length ? `${entries.length} ${entries.length === 1 ? "entry" : "entries"}` : "No editable files or folders found.";
  for (const entry of entries) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button";
    button.dataset.path = entry.path;
    button.textContent = `${entry.directory ? "Folder: " : ""}${entry.name}`;
    button.title = entry.path;
    button.addEventListener("click", () => entry.directory ? loadStoryFiles(entry.path) : openStoryWorkspaceFile(entry.path));
    list.append(button);
  }
}

async function openStoryWorkspaceFile(path) {
  storyFiles.controller?.abort();
  const controller = new AbortController();
  storyFiles.controller = controller;
  const workspaceId = storyFiles.workspaceId;
  const revision = storyEditor.revision;
  document.getElementById("story-files-status").textContent = "Opening file…";
  try {
    const result = await fetchJson(`/api/story-editor/file?${new URLSearchParams({ workspaceId, path })}`, { signal: controller.signal });
    if (controller.signal.aborted || workspaceId !== activeWorkspaceProfile()?.id) return;
    const imported = { name: result.path.split("/").at(-1), text: result.document, source: result };
    document.getElementById("story-files-dialog").close();
    if (storyEditor.elements["story-document"].value || storyEditor.revision !== revision
      || storyEditor.fileSource && storyEditor.fileSource.document !== storyEditor.elements["story-document"].value) {
      storyEditor.pendingImport = imported;
      storyEditor.elements["story-import-confirmation"].hidden = false;
      storyEditor.elements["story-import-confirmation"].scrollIntoView({ block: "nearest" });
    } else replaceStoryDraft(imported);
  } catch (error) {
    if (!controller.signal.aborted) document.getElementById("story-files-status").textContent = error.message;
  }
}

async function saveStoryWorkspaceFile() {
  const source = storyEditor.fileSource;
  if (!source || storyEditor.fileSaving || source.workspaceId !== activeWorkspaceProfile()?.id) return;
  const text = storyEditor.elements["story-document"].value;
  const status = document.getElementById("story-file-save-status");
  status.textContent = "Saving workspace file…";
  storyEditor.fileSaving = true;
  updateStoryFileState();
  try {
    const result = await fetchJson("/api/story-editor/file", {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ workspaceId: source.workspaceId, path: source.path, document: text, expectedVersion: source.version })
    });
    // Editing can continue during I/O. Only the submitted snapshot becomes the saved baseline.
    if (storyEditor.fileSource === source) storyEditor.fileSource = result;
    status.textContent = storyEditor.elements["story-document"].value === text
      ? `Saved ${source.path} in its workspace.` : `Saved ${source.path}. Newer draft edits still need saving.`;
  } catch (error) { status.textContent = error.message; }
  finally { storyEditor.fileSaving = false; updateStoryFileState(); }
}
