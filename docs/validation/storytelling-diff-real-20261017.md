# Storytelling comparison highlighting and real-model acceptance — 2026-10-17

This completes the previously pending real-model acceptance described in
`storytelling-view-modal-20261017.md`. The user explicitly authorized AR to load
the configured model automatically, while retaining the no-Ollama-restart constraint.

## Delivered change

The review modal marks removals in the original with red/strikethrough and additions
in the proposal with green/underline. Unchanged words remain unmarked. The legend
provides a non-color explanation. Edit proposal exposes the existing plain-text
editor; Show changes returns to the highlighted proposal. Edits and synonym choices
update the comparison without modifying the draft. Apply and export still use the
exact replacement string, never display markup.

Comparison tokenizes Unicode words, punctuation and whitespace, removes equal
edges, and uses bounded LCS. Above one million cells, the remaining changed span
is highlighted as a block with an explicit explanatory label. This avoids unbounded
quadratic work on long unrelated passages. All highlight content uses DOM text
nodes, including literary text that resembles HTML.

Files changed in this increment: `wwwroot/index.html`, `story-editor.js`,
`story-editor.css`, `Chat/StoryEditorService.cs`, `StoryEditorEndToEndTests.cs`,
`RealStoryEditorEndToEndTests.cs`, `docs/rpg-specialist-editor.md`, this report,
and the local `.tmp/Test-StoryReal.ps1` launcher. Other dirty work was preserved.

## Build and deterministic validation

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers
dotnet format AgenticRouter.slnx --verify-no-changes --no-restore --include AgenticRouter.Api/Chat/StoryEditorService.cs tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs tests/AgenticRouter.EndToEndTests/RealStoryEditorEndToEndTests.cs
node --check AgenticRouter.Api/wwwroot/story-editor.js
git diff --check
.\.tmp\Test-RpgEditor.ps1 -LogName storytelling-diff-final.trx
```

Release completed with zero warnings/errors. Formatting, JavaScript syntax and
diff checks passed. All **18/18 editor E2E cases passed** in `storytelling-diff-final.trx`.
The suite covers the existing flows plus exact
changed-word/punctuation highlighting, unchanged text, edited proposals, safe
literal HTML-like text, synonym switching and bounded large replacements.
Desktop and mobile modal screenshots were reviewed, including the real model's
revision and synonym output.

## Real acceptance

```powershell
.\.tmp\Test-StoryReal.ps1 -AllowLoad
```

The launcher identified the existing AR-managed Ollama listener through the user
AR process and its child process/listening port, verified the model was installed,
and connected an isolated Release Host to that listener. It did not change the
user Host's configuration, restart a server or download a model. The isolated Host
used the user's Storytelling prompt, inference profiles and runtime/context settings.
Only the test workspace/session was isolated; generation used the real model.

- Model: `muse-glimmer:30b`, Q4_K_M, CUDA, 131072-token context.
- Endpoint: `http://127.0.0.1:16530/`.
- Digest: `de878ce33ad81d060001db1469a02eebe4d86f0ad58cfe52dc062fdcbe4464c1`.
- First run started unloaded; AR's normal provider request loaded the model.
- Both real browser/API acceptance runs passed. The second ran after the synonym
  instruction was refined and is recorded in `storytelling-real.trx`.
- Final proposal request wall times: revision 30,220 ms; synonyms 24,621 ms.
  These are individual API/browser request timings, not a throughput benchmark.

Browser assertions verified that comparison preserves the draft, applying replaces
only the captured selection, revision undo restores the original, choosing a synonym
does not apply it, synonym apply/undo preserve surrounding text, View renders the
result, and downloaded Markdown exactly matches both applied and undone drafts.
The actual revision reduced the repeated corridor wording while preserving Clara,
the letter and the scene. The chosen synonym `vetusto` replaced only the first
`antigo`; the second occurrence and the surrounding paragraphs remained unchanged.

The first synonym result included an associated attribute, `empoeirado`, rather
than an equivalent word. The service instruction now explicitly preserves meaning
and facts, rejects unsupported attributes and prefers fewer accurate alternatives.
The final output offered `velho`, `vetusto`, and `secular`. The latter can imply a
more specific age than established by the passage; this remains a concrete example
of why the proposals require editorial review. Acceptance establishes the tested
workflow and selected replacement, not universal semantic correctness of model output.

Evidence under `.artifacts/rpg-editor/real-acceptance/`:

- `acceptance.json`: real output, trace/request IDs, residency snapshots and checks.
- `review.png`, `synonyms.png`, `view.png`: inspected browser captures.
- `applied.md`, `undone.md`: downloaded files checked against exact expected text.
- `synonyms-before-prompt-refinement.json`: the original quality finding.

User AR PID 37800 and existing Ollama PIDs 21428 and 41672 were preserved. Only
validation Hosts/browsers were disposed. The model was left under its existing
keep-alive policy. The complete repository suite was not run, and the running user
backend was not redeployed; the corrected prompt was validated in the isolated build.
