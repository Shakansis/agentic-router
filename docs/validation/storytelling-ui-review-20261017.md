# Storytelling Editor UI and behavior review — 2026-10-17

This follow-up addresses the supplied screenshots and subsequent requests about
the editor name, conditional entry and centered Runtime Summary. It supersedes
the initial layout assessment in `rpg-specialist-editor-20261017.md`.

## Corrections

- Compact specialist selector directly beside Local session. Automatic displays
  the Host-resolved specialist after a turn; fixed choices remain explicit.
- Runtime Summary centered relative to the chat, independently of the widths of
  the left and right controls. Container queries account for sidebar resizing.
  Smaller areas put the summary on its own centered row; narrow meters wrap.
- Storytelling Editor button and panel title replace RPG editor. The entry is
  hidden unless RPG storytelling is explicitly fixed, including when Automatic
  happens to route to RPG. Changing specialist closes the panel but keeps its draft.
- File toolbar inputs/buttons align; editor typography, spacing, dividers,
  disabled controls and proposal status use the existing application palette.
- Full-width editor overlays reset grid placement and start directly below the
  header, including when a wide sidebar leaves little conversation space.
- Undo stays beside the draft after another proposal is generated/discarded or
  the panel is closed/reopened. Original selection and stale-response protections
  remain intact.
- ChatStageException from generation is translated into the editor's typed error
  response, preserving status, recoverability and logged cause. A provider 503
  previously escaped as an unhandled HTTP error despite preserving the draft.

## Files changed in this review

- `AgenticRouter.Api/wwwroot/index.html`, `story-editor.css`, `story-editor.js`
- `AgenticRouter.Api/Chat/StoryEditorService.cs`
- `tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/ExecuteCoreEndToEndTests.cs`
- `README.md`, `docs/README.md`, `docs/rpg-specialist-editor.md`, this report

Other pre-existing changes in the checkout were preserved. The existing manual
model test now excludes only empty, non-streaming `keep_alive=0` unload requests
from its inference count. Captured evidence showed exactly one such unload of
`docs:latest` and one streamed `beta:code` inference with system/user messages.
It still requires exactly one inference and asserts manual routing/model identity.

## Executed validation

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers
dotnet format AgenticRouter.slnx --verify-no-changes --no-restore --include AgenticRouter.Api/Chat/StoryEditorService.cs tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs tests/AgenticRouter.EndToEndTests/ExecuteCoreEndToEndTests.cs
node --check AgenticRouter.Api/wwwroot/story-editor.js
git diff --check
```

Release build: zero warnings/errors. Scoped formatting, JavaScript syntax and
diff whitespace checks passed. Intended frontend, service and test changes were
reviewed separately from unrelated dirty work.

E2E execution used `.tmp/Test-RpgEditor.ps1`, the isolated Release API and Edge
headless. Only external model/provider boundaries were simulated.

- **30/30 passed** in
  `.artifacts/rpg-editor/TestResults/storytelling-ui-final.trx`: 14 specialist/editor
  cases plus 16 regressions covering Chat presentation, composer/layout, automatic
  routing, explicit models, inference profiles, history and cancellation.
- Visual inspection then found narrow-runtime clipping and an overlay grid-area
  defect. After CSS fixes and stronger assertions, **3/3 relevant layout cases
  passed again** in `storytelling-ui-geometry-reviewed.trx`. These are repeat cases,
  not three additional unique tests. Final C# formatting verification also passed.
- Geometry coverage: 1600, 1280, 1100, 760, 390 and 320 px; Automatic/RPG/long
  specialist names; keyboard selection; collapsed and maximum-width sidebar;
  picker-label adjacency, centered runtime, non-overlapping controls, visible
  meter bounds, compact desktop height, file-control alignment and full-width
  overlay bounds. Browser error collection stayed empty.
- Functional coverage includes conditional entry, automatic routing unaffected,
  fixed history restore, manual model selection, review/synonym comparison,
  explicit selected-range application, undo after discard, Unicode repetitions,
  file import protection, exact Markdown contents, stale generation, malformed
  output, typed provider failure, cancellation and retry.

Final screenshots were inspected at desktop/mobile widths and with the expanded
sidebar. Stable local previews:

- [Desktop editor](../../.artifacts/rpg-editor/storytelling-reviewed-desktop.png)
- [Mobile comparison](../../.artifacts/rpg-editor/storytelling-reviewed-mobile.png)
- [Automatic header](../../.artifacts/rpg-editor/storytelling-reviewed-header.png)

The final process audit found no test host, fake provider, headless test browser,
or persistent build server. Existing user processes remained: AR PID 43596,
parent 40320, ports 5294/7155/7156; Ollama PID 21428, parent 20728, port 11434.
Neither was stopped, restarted or used for model validation.

## Limits

No real model inference or narrative-quality evaluation was performed. The full
repository suite was not run. Validation used an isolated build; the existing
user backend was not redeployed. Draft persistence remains tab-local plus explicit
Markdown export, as documented.
