function mountStoryPreview(preview, html) {
  const template = document.createElement("template");
  template.innerHTML = html;
  let anchorNumber = 0;
  for (const target of template.content.querySelectorAll("[id]")) {
    target.dataset.storyAnchor = target.id;
    target.id = `story-preview-anchor-${++anchorNumber}`;
  }
  secureRenderedLinks(template.content);
  for (const link of template.content.querySelectorAll('a[href^="#"]')) {
    // Local document navigation must never change the application URL or open an AR tab.
    const button = document.createElement("button");
    button.type = "button";
    button.className = "story-preview-link";
    button.setAttribute("role", "link");
    button.dataset.storyHref = link.getAttribute("href");
    for (const attribute of ["data-story-start", "data-story-end", "data-story-anchor", "id", "title"]) {
      if (link.hasAttribute(attribute)) button.setAttribute(attribute, link.getAttribute(attribute));
    }
    button.append(...link.childNodes);
    link.replaceWith(button);
  }
  preview.replaceChildren(template.content);
}

function navigateStoryPreview(event) {
  const link = event.target.closest("[data-story-href]");
  if (!link) return;
  event.preventDefault();
  const preview = storyEditor.elements["story-preview"];
  let anchor;
  try { anchor = decodeURIComponent(link.dataset.storyHref.slice(1)); }
  catch { storyStatus("This document link has an invalid anchor. The draft is unchanged."); return; }
  const target = [...preview.querySelectorAll("[data-story-anchor]")]
    .find(element => element.dataset.storyAnchor === anchor);
  if (anchor && !target) {
    storyStatus(`Section not found in this document: ${anchor}. The draft is unchanged.`);
    return;
  }
  preview.querySelector(".story-anchor-target")?.classList.remove("story-anchor-target");
  const highlight = target?.textContent.trim() ? target
    : target?.closest("p")?.nextElementSibling ?? target?.nextElementSibling ?? target;
  if (highlight) highlight.classList.add("story-anchor-target");
  preview.scrollTop = target
    ? preview.scrollTop + target.getBoundingClientRect().top - preview.getBoundingClientRect().top - 12 : 0;
  preview.focus({ preventScroll: true });
  storyStatus(anchor ? `Showing section: ${anchor}.` : "Showing the start of the document.");
}

function currentStorySelection() {
  if (storyEditor.mode === "view") return storyEditor.viewSelection?.revision === storyEditor.revision
    ? storyEditor.viewSelection : { start: 0, end: 0 };
  const draft = storyEditor.elements["story-document"];
  return { start: draft.selectionStart, end: draft.selectionEnd };
}

function captureStoryViewSelection() {
  const preview = storyEditor.elements["story-preview"];
  if (!preview || storyEditor.mode !== "view" || storyEditor.previewController || preview.hidden) return;
  const selection = window.getSelection();
  if (!selection?.rangeCount) return;
  const range = selection.getRangeAt(0);
  // Keep the selected passage when the author focuses instructions or action buttons.
  if (!preview.contains(range.startContainer) || !preview.contains(range.endContainer)) return;
  if (range.collapsed && (storyEditor.selectionLocked || !preview.contains(document.activeElement))) return;
  storyEditor.viewSelection = null;
  if (range.collapsed) { updateStorySelection(); return; }
  const walker = document.createTreeWalker(preview, NodeFilter.SHOW_TEXT);
  const selected = [];
  while (walker.nextNode()) {
    const node = walker.currentNode;
    if (!node.textContent.length || !range.intersectsNode(node)) continue;
    // Renderer line breaks between blocks have no source node of their own.
    if (!node.textContent.trim() && !node.parentElement.closest("[data-story-start]")) continue;
    const part = document.createRange();
    part.selectNodeContents(node);
    if (range.comparePoint(node, 0) < 0) part.setStart(range.startContainer, range.startOffset);
    if (range.comparePoint(node, node.textContent.length) > 0) part.setEnd(range.endContainer, range.endOffset);
    if (part.toString().length) selected.push({ node, part });
  }
  if (!selected.length) { updateStorySelection(); return; }
  const source = storyEditor.elements["story-document"].value;
  const start = storySourceOffset(selected[0], false, source, range, preview);
  const end = storySourceOffset(selected.at(-1), true, source, range, preview);
  if (start == null || end == null || start < 0 || end <= start || end > source.length) {
    storyStatus("This selection could not be mapped precisely. Select the passage in Raw to revise it.");
  } else {
    storyEditor.viewSelection = { start, end, revision: storyEditor.revision };
    storyEditor.viewSelectionEndpoints = [range.startContainer, range.startOffset, range.endContainer, range.endOffset];
    storyEditor.elements["story-document"].setSelectionRange(start, end);
  }
  updateStorySelection();
}

function renderStorySelectionHighlight() {
  if (!globalThis.Highlight || !CSS.highlights) return;
  CSS.highlights.delete("story-selection");
  const endpoints = storyEditor.viewSelectionEndpoints;
  if (storyEditor.mode !== "view" || storyEditor.viewSelection?.revision !== storyEditor.revision
    || !endpoints?.[0].isConnected || !endpoints[2].isConnected) return;
  const range = document.createRange();
  try {
    range.setStart(endpoints[0], endpoints[1]);
    range.setEnd(endpoints[2], endpoints[3]);
    CSS.highlights.set("story-selection", new Highlight(range));
  } catch { /* A preview refresh invalidates the old DOM range, never the draft. */ }
}

function storySourceOffset(entry, end, source, range, preview) {
  const element = entry.node.parentElement.closest("[data-story-start][data-story-end]");
  if (!element || !preview.contains(element)) return null;
  const prefix = document.createRange();
  prefix.selectNodeContents(element);
  prefix.setEnd(end ? entry.part.endContainer : entry.part.startContainer, end ? entry.part.endOffset : entry.part.startOffset);
  const offset = prefix.toString().length;
  const start = Number(element.dataset.storyStart), finish = Number(element.dataset.storyEnd);
  const rendered = element.textContent, raw = source.slice(start, finish);
  let result;
  if (offset === 0) result = start;
  else if (offset === rendered.length) result = finish;
  else if (raw === rendered) result = start + offset;
  else {
    const contentAt = raw.indexOf(rendered);
    if (contentAt < 0 || raw.indexOf(rendered, contentAt + 1) >= 0) return null;
    result = start + contentAt + offset;
  }
  // Include delimiters only when the selection covers the whole formatted phrase/block.
  for (let parent = element.parentElement; parent && parent !== preview; parent = parent.parentElement) {
    if (!parent.hasAttribute("data-story-start")) continue;
    const full = document.createRange();
    full.selectNodeContents(parent);
    const before = full.cloneRange(), after = full.cloneRange();
    if (parent.contains(range.startContainer)) before.setEnd(range.startContainer, range.startOffset);
    else before.collapse(true);
    if (parent.contains(range.endContainer)) after.setStart(range.endContainer, range.endOffset);
    else after.collapse(false);
    if (before.toString().length === 0 && after.toString().length === 0)
      result = Number(end ? parent.dataset.storyEnd : parent.dataset.storyStart);
  }
  return result;
}
