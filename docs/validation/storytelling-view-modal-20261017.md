# Storytelling Editor: document tokens, View/Raw and review modal

## Implemented

- Whole-draft token estimate, using the Host's conservative three-characters-per-
  token heuristic. It is explicitly approximate, updates after import/edit/apply/
  undo, and does not claim to measure current model context or GPU memory.
- Raw editing and View rendering. Preview uses the existing Host Markdown renderer
  and sanitization, with no provider call, source persistence or raw-text mutation.
  Raw selection survives toggling; exports always contain the raw document.
- A wide review modal replaces the cramped inline comparison. Original and editable
  replacement have equal-height, independently scrollable surfaces; synonym choices
  occupy a separate bounded area. Apply/Discard remain in the footer. Narrow screens
  stack the columns. Native modal focus confinement and Escape are retained.
- Closing the modal preserves the proposal; Review proposal reopens it. Stale
  revision protection remains effective after returning to/editing the draft.

Touched files: `wwwroot/index.html`, `story-editor.js`, `story-editor.css`,
`Chat/StoryEditorService.cs`, `Controllers/StoryEditorController.cs`,
`StoryEditorEndToEndTests.cs`, new `RealStoryEditorEndToEndTests.cs`, and the editor
specification. Unrelated working-tree changes were preserved.

## Executed validation

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers
dotnet format AgenticRouter.slnx --verify-no-changes --no-restore --include AgenticRouter.Api/Chat/StoryEditorService.cs AgenticRouter.Api/Controllers/StoryEditorController.cs tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs tests/AgenticRouter.EndToEndTests/RealStoryEditorEndToEndTests.cs
node --check AgenticRouter.Api/wwwroot/story-editor.js
git diff --check
```

Release: zero warnings/errors. Formatting, JavaScript syntax and diff checks passed.

**20/20 browser/API E2E cases passed** in
`.artifacts/rpg-editor/TestResults/storytelling-view-modal-final.trx`: 16 editor
cases plus three Chat presentation regressions and compact-header coverage. The
external model boundary was simulated. Coverage includes Markdown headings/lists/
tables/code, raw HTML and unsafe-link handling, no inference during preview,
selection and exact-export preservation, estimate updates, modal close/reopen,
stale proposals, explicit apply/undo, keyboard focus and responsive geometry.

The modal was captured at 1600/1280/1100/760/390/320 px. Markdown View was captured
at 1600/760/320 px. Desktop and mobile screenshots were visually inspected.
Stable captures are in `.artifacts/rpg-editor/visual-review/`, including
`rpg-comparison-1600.png`, `rpg-comparison-390.png` and `story-view-1600.png`.

## Real-model acceptance: pending resident model

The user authorized real acceptance using the already-loaded model, with no
Ollama restart. Initial discovery reported no resident model. The user chose to
load the desired model manually and notify us. Subsequent discovery observed
`muse-glimmer:30b` at the AR-managed endpoint `http://127.0.0.1:16530/`, CUDA,
131072 context tokens, digest
`de878ce33ad81d060001db1469a02eebe4d86f0ad58cfe52dc062fdcbe4464c1`.
Its reported expiry was 2026-10-17 16:48:28 America/Sao_Paulo. Before the prepared
test could start, `/api/ps` returned no resident model again. No inference was
started, and neither keep-alive policy nor Ollama processes were changed.

`RealStoryEditorEndToEndTests` is compiled and opt-in via `AR_STORY_REAL_*` variables.
It refuses to proceed unless the specified model is resident, uses a separately
configured Host with the user's specialist prompt/profile/context, talks to that
existing Ollama endpoint, and checks revision, synonyms, comparison, exact apply,
undo, View and Markdown downloads through the browser. It records model output,
screenshots and exported files. The local launcher `.tmp/Test-StoryReal.ps1`
discovers the active managed endpoint again before running. Real narrative quality
and that acceptance run remain unverified until the user reloads the model.

The full repository suite was not run. The user-facing AR instance was not
restarted/redeployed; View requires the updated backend build. Final process audit
found no validation host, fake provider or headless test browser. User AR PID 37800,
its managed Ollama PID 41672, and default Ollama PID 21428 remained intact.
Visual Studio-owned MSBuild processes were not terminated.
