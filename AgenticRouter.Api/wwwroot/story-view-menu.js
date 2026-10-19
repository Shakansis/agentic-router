const storyViewMenu = { section: null, word: null, revision: null };

function closeStoryViewMenu() {
  document.getElementById("story-view-menu").hidden = true;
}

function initializeStoryViewMenu() {
  const preview = storyEditor.elements["story-preview"];
  const menu = document.getElementById("story-view-menu");
  const show = event => {
    if (storyEditor.previewController || storyEditor.mode !== "view") return;
    const heading = event.target.closest("h1,h2,h3,h4,h5,h6");
    const block = heading ?? event.target.closest("blockquote") ?? event.target.closest("p,li,pre,td,th");
    if (!block || !preview.contains(block) || !block.hasAttribute("data-story-start")) return;
    event.preventDefault();
    const range = document.createRange();
    range.selectNodeContents(block);
    let end = Number(block.dataset.storyEnd);
    if (heading) {
      const headings = [...preview.querySelectorAll("h1,h2,h3,h4,h5,h6")];
      const next = headings.slice(headings.indexOf(heading) + 1).find(item => Number(item.tagName[1]) <= Number(heading.tagName[1]));
      let boundary = next;
      // Legacy document anchors belong to the section that follows them.
      if (next?.previousElementSibling?.hasAttribute("data-story-anchor")) boundary = next.previousElementSibling;
      range.setStartBefore(heading);
      if (boundary) range.setEndBefore(boundary);
      else range.setEnd(preview, preview.childNodes.length);
      end = boundary?.dataset.storyStart != null ? Number(boundary.dataset.storyStart) : storyEditor.elements["story-document"].value.length;
      end = storyEditor.elements["story-document"].value.slice(0, end).trimEnd().length;
    }
    storyViewMenu.section = { range, start: Number(block.dataset.storyStart), end };
    storyViewMenu.word = storyWordAtPoint(event, block);
    storyViewMenu.revision = storyEditor.revision;
    document.getElementById("story-select-section").textContent = `Select section · ${heading ? "scene" : block.tagName === "BLOCKQUOTE" ? "speech" : "paragraph"}`;
    const synonym = document.getElementById("story-view-synonym");
    synonym.textContent = storyViewMenu.word ? `Find synonym for “${storyViewMenu.word.text}”` : "Find synonym";
    synonym.disabled = !storyViewMenu.word || Boolean(storyEditor.controller || state.requestController);
    closeStoryWordMenu();
    menu.hidden = false;
    const rect = block.getBoundingClientRect();
    menu.style.left = `${Math.max(8, Math.min(event.clientX || rect.left, innerWidth - menu.offsetWidth - 8))}px`;
    menu.style.top = `${Math.max(8, Math.min(event.clientY || rect.top, innerHeight - menu.offsetHeight - 8))}px`;
    menu.querySelector("button").focus({ preventScroll: true });
  };
  preview.addEventListener("contextmenu", show);
  preview.addEventListener("keydown", event => {
    if (event.key === "ContextMenu" || event.key === "F10" && event.shiftKey) {
      const selected = window.getSelection();
      const target = selected?.anchorNode?.parentElement;
      if (target && preview.contains(target)) show({ target, preventDefault: () => event.preventDefault() });
    }
  });
  document.addEventListener("pointerdown", event => { if (!menu.contains(event.target)) closeStoryViewMenu(); });
  document.addEventListener("keydown", event => {
    if (menu.hidden) return;
    if (event.key === "Escape" || event.key === "Tab") {
      event.preventDefault();
      event.stopPropagation();
      closeStoryViewMenu();
      preview.focus({ preventScroll: true });
    } else if (["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) {
      event.preventDefault();
      const buttons = [...menu.querySelectorAll("button:not(:disabled)")];
      buttons[event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1
        : (buttons.indexOf(document.activeElement) + 1) % buttons.length].focus();
    }
  }, true);
  document.getElementById("story-select-section").addEventListener("click", () => {
    if (!selectStoryMenuRange(storyViewMenu.section)) return;
    storyStatus("Section selected. Add instructions or choose a review action.");
  });
  document.getElementById("story-view-synonym").addEventListener("click", () => {
    if (selectStoryMenuRange(storyViewMenu.word)) void requestStoryProposal("synonyms");
  });
}

function selectStoryMenuRange(selection) {
  closeStoryViewMenu();
  if (!selection || storyViewMenu.revision !== storyEditor.revision) return false;
  storyEditor.elements["story-preview"].focus({ preventScroll: true });
  window.getSelection().removeAllRanges();
  window.getSelection().addRange(selection.range);
  captureStoryViewSelection();
  if (selection.start != null) {
    storyEditor.viewSelection = { start: selection.start, end: selection.end, revision: storyEditor.revision };
    storyEditor.elements["story-document"].setSelectionRange(selection.start, selection.end);
    storyEditor.viewSelectionEndpoints = [selection.range.startContainer, selection.range.startOffset, selection.range.endContainer, selection.range.endOffset];
  }
  updateStorySelection();
  return Boolean(storyEditor.viewSelection);
}

function storyWordAtPoint(event, block) {
  let caret;
  if (event.clientX != null) {
    const position = document.caretPositionFromPoint?.(event.clientX, event.clientY);
    if (position) caret = { node: position.offsetNode, offset: position.offset };
    else {
      const range = document.caretRangeFromPoint?.(event.clientX, event.clientY);
      if (range) caret = { node: range.startContainer, offset: range.startOffset };
    }
  } else {
    const selection = window.getSelection();
    if (selection?.anchorNode) caret = { node: selection.anchorNode, offset: selection.anchorOffset };
  }
  if (!caret || !block.contains(caret.node) || caret.node.nodeType !== Node.TEXT_NODE) return null;
  const nodes = [], walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
  let text = "", offset = -1;
  while (walker.nextNode()) {
    const node = walker.currentNode;
    nodes.push({ node, start: text.length });
    if (node === caret.node) offset = text.length + caret.offset;
    text += node.textContent;
  }
  const word = storyWords(text).find(item => item.index <= offset && item.index + item[0].length >= offset);
  if (!word || !/\p{L}/u.test(word[0])) return null;
  const start = word.index, end = start + word[0].length;
  const first = nodes.find(item => item.start <= start && item.start + item.node.length > start);
  const last = nodes.find(item => item.start < end && item.start + item.node.length >= end);
  const range = document.createRange();
  range.setStart(first.node, start - first.start);
  range.setEnd(last.node, end - last.start);
  if (event.clientX != null && ![...range.getClientRects()].some(rect =>
    event.clientX >= rect.left - 1 && event.clientX <= rect.right + 1
      && event.clientY >= rect.top - 1 && event.clientY <= rect.bottom + 1)) return null;
  return { text: word[0], range };
}
