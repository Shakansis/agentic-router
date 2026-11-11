# Shared composer and auxiliary tool tabs

The conversation has one permanent Chat pane and an auxiliary pane containing
tool tabs. The auxiliary pane starts closed and opens only when the user starts
a tool. Closing the last tool restores the full-width chat. Opening the
Storytelling Editor retains its existing fixed RPG specialist requirement.

On wide layouts, both panes remain visible. Drag the separator, use its Left/Right
keys (Home/End for the bounds), or double-click to restore the initial split.
The split is window-local. On narrow layouts, Chat and the tool tab switch the
visible pane. The shared composer remains visible in both layouts and in the
editor's optional full-screen view. Jump to latest belongs to the chat viewport,
above the shared composer, and does not float over the tool.

The selected tab determines the next submission's initial destination. Opening
a tool selects it. Selecting Chat explicitly returns the next submission to Chat
while leaving the tool open. Each queued message captures its destination and
draft snapshot when queued; switching tabs does not retarget an accepted request.
Changed tool document snapshots run as the next turn. The existing Use now
steering protocol can continue with an identical snapshot, but does not replace
an active turn's tool context; its control explains that limitation.

## Host contract

`ChatRequest.toolWindow` carries the active tool identifier, draft revision,
document, selection and optional revision instructions (up to 2,000 characters).
These instructions also apply to the shared composer unless the current message
overrides them. Currently only `story-editor` is supported. The Host
validates the identifier, bounds and size before inference. This is an in-memory
document capability, not a filesystem path or a workspace mutation permission.
The selected model, interaction mode, harness and approval policy remain unchanged.

The specialist receives the selected passage (up to 16,000 characters) and up to
2,000 adjacent characters on each side, or the first 16,000 characters when no
selection exists. The prompt discloses truncation and the full draft's character
count. This context participates in normal context fitting; it is not an
unaccounted append after fitting. Source text is quoted as untrusted data.

`set_response_target` lets the same specialist declare `chat` or `story-editor`
and a bounded reason to the Host. Explicit web research, file analysis or
conversation-context requests may change the destination; this is interpretation
by the active model, not a second routing inference or a keyword classifier.
Web and filesystem access retain their existing capabilities and boundaries.
Target changes are per turn and do not silently change the selected tab.

The same tool can supply a replacement for the captured selection. Without a
selection it can propose an entire draft only within the existing 16,000-character
selection limit. This creates a reviewable proposal, never an automatic edit or
save. The browser checks the revision and original text before accepting the
proposal; Apply performs the existing conflict checks again. Existing Apply,
Undo, explicit save, export and stale-file protections remain authoritative.

Host target decisions and action summaries appear in Chat. Tool-directed response
content appears in the editor's Assistant area; Chat receives a delivery receipt.
The common Host flow exposes the capability to Native and external harnesses
through the existing bridge. Supervised workers receive the tool snapshot as
live input, and target/proposal events are transient progress events. Supervisor
protocol responses remain internal and are not replaced with delivery receipts.

The Host does not save draft source, streamed tool response payloads or proposed
replacements as conversation history or checkpoint fields. Target/action metadata and
delivery receipts can be retained under the existing history setting. A live
connection can replay transient tool events; historical replay does not reapply
editor proposals. Reload still loses the unsaved editor draft. A restarted
supervised run cannot reconstruct an unsaved auxiliary document from checkpoints.

## Editor layout

File controls, optional revision instructions, revision actions and repetition
controls live in a collapsible left sidebar. Raw/View, the document, selection
status and Undo remain in the reading area. The sidebar can be hidden to give
the document more width. Existing context menus, comparison, explicit Apply and
full-screen behavior remain available. Direct revision, Apply and Undo actions also report their
outcome in the visible Chat activity; these local UI notices do not persist the
draft or introduce an inference call.

Browser access/usage is outside this increment. The auxiliary tab container is
the intended place for that tool's future surface; no browser automation
capability or new approval rule is enabled here.
