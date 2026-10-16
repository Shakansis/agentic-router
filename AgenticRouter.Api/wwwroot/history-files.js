// A fresh identity per page load: no sessionStorage/localStorage persistence.
const historyWindowId = createSessionId();
const historyPrivacyNotice = "History stays on this computer and includes messages, complete uploaded files and images, "
  + "and Execute rollback snapshots, including Git-ignored files. It may contain sensitive information and is not encrypted by AR. "
  + "Disabling history stops new saves but does not delete existing data. With history disabled, prompt data and snapshots "
  + "are temporary and cannot be recovered after closing or reloading this window.";

function createHostFilesIcon() {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.classList.add("assistant-running-host");
  svg.setAttribute("viewBox", "0 0 32 32");
  svg.setAttribute("fill", "none");
  svg.setAttribute("aria-hidden", "true");
  for (const d of [
    "M4 12V8a2 2 0 0 1 2-2h7l3 4h10a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V12Z",
    "M10 15h9l3 3v9H10V15Zm9 0v4h3M13 21h5m-5 3h5"
  ]) {
    const path = document.createElementNS(svg.namespaceURI, "path");
    path.setAttribute("d", d);
    svg.append(path);
  }
  return svg;
}

function renderHostFilesProgress(indicator, progress) {
  const maintenance = document.querySelector("#history-file-operation-status");
  maintenance.replaceChildren(createHostFilesIcon(), document.createTextNode(`Host · ${progress.action}`
    + (Number.isFinite(progress.percent) ? ` · ${Math.floor(progress.percent)}%` : "")
    + ` · ${formatElapsed(progress.elapsedMilliseconds)}`));
  indicator.classList.add("host-files-active");
  indicator.classList.remove("inference-preparing");
  if (!indicator.querySelector(".assistant-running-host")) indicator.prepend(createHostFilesIcon());
  const value = indicator.querySelector(".assistant-inference-percent");
  value.hidden = !Number.isFinite(progress.percent);
  value.textContent = value.hidden ? "" : `${Math.floor(progress.percent)}%`;
  indicator.querySelector(".assistant-current-activity").textContent = `Host · ${progress.action}`;
  indicator.dataset.hostOperationId = progress.operationId;
  indicator.dataset.hostState = progress.state;
  indicator.title = `${formatElapsed(progress.elapsedMilliseconds)} · ${progress.completedBytes} / ${progress.totalBytes ?? "unknown"} work bytes\n`
    + (progress.scopeNote ? `${progress.scopeNote}\n` : "")
    + (progress.timings ?? []).map(item => `${item.action}: ${formatElapsed(item.elapsedMilliseconds)}`).join("\n");
}

async function runHistoryFileOperation(path, payload) {
  const operationId = createSessionId();
  const article = document.createElement("article");
  article.className = "message host-file-operation";
  const indicator = createRunningIndicator();
  article.append(indicator);
  elements.messages.append(article);
  renderHostFilesProgress(indicator, { operationId, action: "Preparing file operation", state: "running", elapsedMilliseconds: 0, completedBytes: 0 });
  let stopped = false;
  let timer;
  const poll = async () => {
    try {
      const progress = await fetchJson(`/api/history-files/operations/${encodeURIComponent(operationId)}`);
      if (progress) renderHostFilesProgress(indicator, progress);
    } catch { /* The operation's own response reports its authoritative failure. */ }
    if (!stopped) timer = setTimeout(poll, 250);
  };
  timer = setTimeout(poll, 250);
  try {
    const result = await fetchJson(path.replace("{operationId}", encodeURIComponent(operationId)), {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ...payload, operationId, windowId: historyWindowId })
    });
    const progress = result.progress ?? await fetchJson(`/api/history-files/operations/${encodeURIComponent(operationId)}`);
    if (progress) renderHostFilesProgress(indicator, progress);
    return result;
  } catch (error) {
    renderHostFilesProgress(indicator, { operationId, action: error.message, state: "failed", elapsedMilliseconds: 0, completedBytes: 0 });
    article.classList.add("error");
    throw error;
  } finally { stopped = true; clearTimeout(timer); }
}

async function ensureExecuteSnapshotDecision() {
  const ready = await fetchJson("/api/history-files/git-decision", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ windowId: historyWindowId, choice: "inspect" })
  });
  if (ready.ready) return true;
  const dialog = document.querySelector("#snapshot-git-dialog");
  const choice = await new Promise(resolve => {
    const finish = value => { dialog.close(); dialog.removeEventListener("cancel", cancel); dialog.removeEventListener("click", click); resolve(value); };
    const cancel = event => { event.preventDefault(); finish("cancel"); };
    const click = event => { const button = event.target.closest("[data-snapshot-choice]"); if (button) finish(button.dataset.snapshotChoice); };
    dialog.addEventListener("cancel", cancel);
    dialog.addEventListener("click", click);
    dialog.showModal();
  });
  if (choice === "cancel") return false;
  const result = await fetchJson("/api/history-files/git-decision", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ windowId: historyWindowId, choice })
  });
  if (!result.ready) throw new Error(result.diagnostic || "Git could not be prepared.");
  if (choice === "context-only") showToast("Context-only rollback saved for this project in this window. Closing or reloading resets this choice.", "success");
  await refreshGit();
  return true;
}

async function showHistoryFiles() {
  const dialog = document.querySelector("#history-files-dialog");
  const list = document.querySelector("#history-files-list");
  list.replaceChildren();
  dialog.showModal();
  try {
    const files = await fetchJson("/api/history-files");
    if (!files.length) list.textContent = "No saved files. Files from older conversations may not have been stored.";
    let shown = 0;
    const moreRow = document.createElement("li");
    const more = document.createElement("button");
    more.type = "button";
    more.className = "secondary-button";
    moreRow.append(more);
    const renderNext = () => {
      moreRow.remove();
      for (const file of files.slice(shown, shown + 200)) {
        const row = document.createElement("li");
        const open = document.createElement("button");
        open.type = "button";
        open.className = "history-file-link";
        open.textContent = file.name;
        open.addEventListener("click", async () => {
          open.disabled = true;
          try { await fetchJson("/api/history-files/open", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ fileId: file.id }) }); }
          catch (error) { showToast(error.message, "error"); }
          finally { open.disabled = false; }
        });
        const detail = document.createElement("small");
        detail.textContent = `${file.conversation} · ${file.kind} · ${new Date(file.createdAt).toLocaleString()} · ${formatBytes(file.bytes)}${file.compressed ? " · compressed" : ""}`;
        const download = document.createElement("button");
        download.type = "button";
        download.className = "secondary-button";
        download.textContent = "Download copy";
        download.addEventListener("click", async () => {
          try {
            const response = await fetch("/api/history-files/download", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ fileId: file.id }) });
            if (!response.ok) throw new Error("Could not recover this saved file.");
            const url = URL.createObjectURL(await response.blob());
            const link = document.createElement("a"); link.href = url; link.download = file.name.split(/[\\/]/).pop(); link.click();
            setTimeout(() => URL.revokeObjectURL(url), 10_000);
          } catch (error) { showToast(error.message, "error"); }
        });
        row.append(open, detail, download);
        list.append(row);
      }
      shown = Math.min(files.length, shown + 200);
      if (shown < files.length) {
        more.textContent = `Show more files · ${shown} of ${files.length}`;
        list.append(moreRow);
      }
    };
    more.addEventListener("click", renderNext);
    renderNext();
  } catch (error) { list.textContent = error.message; }
}

document.querySelector("#view-history-files").addEventListener("click", showHistoryFiles);
document.querySelector("#close-history-files").addEventListener("click", () => document.querySelector("#history-files-dialog").close());
document.querySelector("#compact-history-files").addEventListener("click", async event => {
  event.currentTarget.disabled = true;
  try {
    const result = await runHistoryFileOperation("/api/history-files/compact", {});
    showToast(`Compacted ${result.sessions} archived conversations · saved ${formatBytes(result.savedBytes)}.`, "success");
    await refreshSessions();
  } catch (error) { showToast(error.message, "error"); }
  finally { document.querySelector("#compact-history-files").disabled = false; }
});
window.addEventListener("pagehide", () => {
  navigator.sendBeacon(`/api/history-files/release-window/${encodeURIComponent(historyWindowId)}`);
});
const unsavedHistoryMessage = document.querySelector("#new-conversation-dialog p");
if (unsavedHistoryMessage) unsavedHistoryMessage.textContent += ` ${historyPrivacyNotice}`;
