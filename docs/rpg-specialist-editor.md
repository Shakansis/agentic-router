# Fixed specialists and the RPG editor

The conversation header exposes a compact specialist selector beside Local session,
independently of model and harness selection. Its Automatic option shows the last
Host-resolved specialist after a turn; fixed options explicitly say Fixed.
Automatic is the default and retains the existing keyword rules and
their precedence. A fixed selection uses one exact configured intention identifier
for the conversation until the user changes it. It controls the specialist prompt,
inference profile, and (when the model is Auto) its configured model resolution.
Explicit model and harness selections remain authoritative. No new agent or router
model is introduced.

The common Host router validates fixed identifiers before model calls. Selection is
visible in `specialist.auto-selected` / `specialist.fixed-selected` events, including
manual model selection. Direct Chat, Execute, and durable supervised workers carry
the same selection. Existing checkpoints and saved conversations default to Auto.
The choice is saved with conversation turns only when history is enabled, and is
restored when the conversation is reopened. Starting a new conversation resets it.

**Storytelling Editor** is available only while the user explicitly fixes
**RPG storytelling**. Automatic routing to that specialist does not reveal the
button. The editor supports storytelling beyond RPG; the configured specialist
identifier and prompt remain unchanged. Opening it shows a plain-text/Markdown
draft beside Chat on wide screens. Narrow screens show
the editor across the conversation area; closing it restores Chat. Drafts remain
in the current tab when closed or when conversations change. They are independent
of conversation history and are not automatically saved; the editor displays this
and provides explicit workspace saving and Markdown export. Reloading or closing
the tab loses unsaved changes.
Switching to another specialist or Automatic closes the panel and hides its entry,
preserving the draft so selecting RPG storytelling again can reopen it.

## Draft and review workflow

- **Full screen** moves the same editor into a viewport-sized modal. **Return to
  split view** or Escape restores its original position. Draft, file name, Raw/View
  mode, selection, instructions, pending proposal and undo remain available. A
  comparison opens above this modal; Escape closes only that top comparison.
- **Raw** edits the original text; **View** uses the document path of the Host
  `SafeMarkdownRenderer` via `/api/story-editor/preview`. Arbitrary raw HTML remains
  escaped and the output sanitized. Empty `<a id="…"></a>` / `<a name="…"></a>`
  markers are recognized as document anchors, alongside heading IDs. Preview never
  invokes a model, persists the source, or changes the raw draft.
- Fragment links become document-local controls that scroll only the preview.
  They do not update the application URL/history or open another AR tab. IDs are
  scoped to the preview before insertion into the page. Missing/malformed anchors
  report a status instead of navigating. External links retain their separate-tab
  behavior. Anchor-like code examples remain literal text.
- Review, synonyms and agreement checking work from Raw and View. The Host supplies render-only UTF-16
  source coordinates from the Markdown syntax tree; the browser maps selected text
  to exact source boundaries, including fully selected Markdown formatting. The
  selection survives focus moving to instructions/actions, and is invalidated when
  the draft changes. Repeated text is resolved by source position. An unmappable
  selection asks the author to use Raw rather than replacing an approximate match.
  Returning to Raw preserves the mapped selection. Applying/undoing refreshes View.
- Focusing instructions preserves the mapped selection and its visible CSS highlight
  in View. A new selection inside the preview replaces it; changing the draft or
  rendering a new preview invalidates it. Highlighting never changes the Markdown.
- Right-click in View exposes **Select section**: a paragraph for ordinary prose,
  the enclosing blockquote for a speech, or the complete section for a heading,
  stopping before the next heading of equal/higher level (including its legacy
  anchor). The existing selection-size limit still applies. **Find synonym for…**
  uses the whole word under the pointer and exact source coordinates, including
  repeated occurrences and inline formatting. Keyboard Menu/Shift+F10 is supported.
- The document toolbar displays approximate tokens for the whole draft using the
  same conservative heuristic as the Host (ceil of UTF-16 length divided by three).
  It updates on editing, import, application and undo, without inference. This is
  neither exact model tokenization, current model context nor RAM/VRAM usage.
- Type/paste or use **Workspace files** to open a UTF-8 `.txt` / `.md` file from
  the active project's trusted workspace, up to 200,000 UTF-16 characters.
  The selector navigates folders and filters names; external uploads are unavailable.
  Replacing a nonempty/changed draft requires the inline **Replace draft** action.
  The source path is distinct from the editable export file name.
- **Save to workspace** explicitly updates the opened source file. Apply and Undo
  affect only the draft. Save is disabled for unbound drafts and when another
  project is active. Switching projects retains the draft and its original binding.
  The Host checks the active workspace ID and pins its profile for the operation,
  then resolves paths through the common trusted-workspace validator. Absolute or
  traversing paths, .git metadata and reparse-point traversal are rejected. Lists
  exclude links, read-only files, unsupported extensions and oversized files.
- Each open returns a SHA-256 version of the source bytes. Save uses an exclusive
  file handle to compare that version before writing and verifies the resulting
  bytes before reporting success. External modifications or stale/concurrent saves
  produce a conflict without applying the stale draft. Deleted files are not recreated.
  The source UTF-8 BOM and newline convention are retained; mixed line endings are
  normalized to the file's CRLF/LF/CR convention on an actual edit. This is an
  in-place exclusive save, not a crash-atomic replacement. Once writing starts,
  browser cancellation does not interrupt it. No automatic retries or force-overwrite.
  The user can reopen the current file for review or export the preserved draft.
- Select 1–16,000 characters and request revision, synonyms or agreement checking. Optional author
  instructions are limited to 2,000 characters.
- `/api/story-editor/proposals` uses the normal read-only Chat generation service,
  provider/model resolution, context fitting, usage accounting, and the configured
  `rpg-storytelling` prompt. There is no alternate execution authority or provider
  call from the browser. The model picker is authoritative; Execute strategy and
  external execution harnesses are not used for these Chat proposals.
- The prompt contains the selected passage and at most 2,000 characters before and
  after it, as quoted JSON literary source. No conversation history or whole-story
  continuity check is implied. Draft source is never persisted as a conversation.
  Calls occur only on explicit revision/synonym/agreement actions and can be cancelled.
- Local Ollama editor analyses request a minimum keep-alive of 1,800 seconds per
  target response. Larger configured durations and unlimited retention (-1) are
  preserved. This is a Host-only request hint, ignored in public Chat JSON; it does
  not persist settings or change ordinary Chat/cloud behavior. No background calls
  or warming timers are introduced. Explicit model switching/unloading and runtime
  resource constraints can still evict the model before that interval.
- **Check agreement** requests only nominal and verbal agreement corrections in
  the selected passage, preserving voice, facts and Markdown. It uses operation
  `agreement` on the same proposal endpoint and compares the replacement before
  explicit Apply. An unchanged answer shows **No changes**. It is model advice,
  not a deterministic grammar diagnosis or a whole-document check.
- **Rewrite selection** requests different wording and sentence construction while
  preserving meaning, facts, names, voice and coherence with adjacent context.
  Operation `rewrite` uses the same read-only proposal endpoint and comparison;
  only the selected source range is replaced after explicit Apply.
- Revision returns replacement text. Synonyms return bounded structured candidates
  with contextual explanations; malformed output is an actionable error, without
  an automatic retry or applying partial results.
- While a request is active, an animated indicator names the operation, explains
  that the model is processing and keeps Cancel available. It clears on success,
  failure and cancellation, including when switching the editor to full screen.
- Synonym comparison shows the entire containing sentence (or sentences for a
  selection spanning multiple sentences), segmented locally with `Intl.Segmenter`
  inside its paragraph boundaries. Surrounding source text is unchanged context;
  only the selected text participates in the diff and is editable/applied. Choosing
  or editing an alternative updates its sentence immediately. The snapshot remains
  tied to the original source positions, including repeated sentences.
- Compare original and editable replacement in a wide modal with equal-height,
  proportionally synchronized scrollable columns, separated synonym candidates and a persistent
  action footer. Narrow screens stack the columns. Escape or Return to draft closes
  the modal without discarding; Review proposal reopens it. Focus stays in the modal
  while open and returns to the draft/proposal control on close.
  Select a synonym candidate if needed.
- The comparison highlights removed words/punctuation with strikethrough and
  additions with underline. Each contiguous edit group uses the same color on both
  sides, cycling a four-color palette. Hover or keyboard focus outlines only the
  matching group in both columns; pure additions/deletions have no invented counterpart.
  This correspondence is active only in preview, not Edit proposal. Unchanged text
  remains unmarked. A legend explains the decorations and synchronized scroll.
  Edit proposal switches the proposed side to plain-text editing;
  Show changes restores highlighting. Editing or choosing another synonym updates
  the comparison without modifying the draft. Highlights use text nodes, never
  rendered HTML from the source. Common prefixes/suffixes are preserved; comparisons
  exceeding one million dynamic-programming cells mark the remaining changed span
  as a block and explicitly label this coarser view, bounding browser work.
  Only **Apply to selection** modifies the draft. A revision counter and original
  selection check reject stale proposals, including changes during generation or
  while reviewing. Selection movement alone does not change the proposal target.
- Undo last application stays beside the draft, independently of the comparison,
  and is available until the draft is manually changed. Rejecting
  a proposal, cancellation and provider errors preserve the draft.
- Export downloads the current draft as UTF-8 `.md`. It never exports an unapplied
  proposal or overwrites a workspace file.

## Repetitions

Counting runs entirely in the browser, with no inference. Unicode words are grouped
case-insensitively with NFC normalization, preserving accents, internal apostrophes,
and hyphens. A repeat qualifies only if another occurrence is nearby: by default,
the difference between their word positions is at most 50, adjustable from 1 to
500. Positions include common words even when those are filtered from the results.
Isolated distant occurrences do not contribute to the displayed count or cycling.
An explicit toggle filters a small common-word list for Portuguese and English.
The top 40 qualifying repeated words are
ordered by count, then alphabetically. Clicking a word selects and cycles through
its exact occurrences. Edited drafts invalidate previous occurrence positions.
Each repeated-word group shows its chosen occurrence and a **Synonyms** action.
That action explicitly requests alternatives for the chosen occurrence (the first
by default), with its adjacent context. Counting and cycling never invoke a model.
Apply replaces only that occurrence; there is no automatic or global replacement.
Right-clicking a word (or Shift+F10/Menu key) opens **Highlight all** and **Ignore
from repetition**. Highlight all switches to View and marks every visible whole-word
match, including distant occurrences, case variants and words split by inline
formatting. The count explicitly says Whole draft. Highlights can be cleared and
are never stored in Markdown or exports. Ignore removes a word from this draft's
analysis; **Ignored words** provides Restore controls. Ignore choices survive closing
the panel and reset on importing a new draft. These actions never invoke a model.

## Validation boundary

Browser/API E2E coverage uses a fake only at the Ollama/provider boundary. It checks
automatic and fixed routing, prompts and profiles, persistence, proposal comparison,
scoped replacement, stale responses, failures/cancellation, Unicode repetition,
file import protection, exact Markdown download, and responsive layout. Narrative
quality and contextual synonym quality still require authorized real-model review.
