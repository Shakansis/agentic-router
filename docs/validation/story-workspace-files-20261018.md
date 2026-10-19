# Storytelling Editor: workspace file selection and explicit save

## Delivered

- Workspace files replaces the OS upload input. The modal browses the active
  project's folders and filters file names. Files are limited to .md/.txt, with
  read-only, oversized and reparse-point entries excluded. Opening validates UTF-8
  and the existing 200,000-character limit before replacing any draft.
- The selected file retains its workspace ID, relative path and source version.
  The separate filename field controls only the exported Markdown copy.
- Save to workspace writes only on an explicit click. Model proposals, Apply,
  Undo and ordinary typing change the draft only. Save state and results appear
  beside the save control. New unbound drafts retain export; this increment does
  not create or rename workspace files.
- A changed source version returns HTTP 409 and preserves the draft. The common
  Host trusted-workspace validator rejects traversal, absolute paths, .git and
  reparse-point escapes. Operations pin their validated active workspace profile;
  stale requests cannot silently target another project.
- Saves retain UTF-8 BOM and source newline convention, compare source bytes under
  an exclusive handle, and verify written bytes before returning success.

## Files changed

- `AgenticRouter.Api/Chat/StoryWorkspaceFiles.cs`
- `AgenticRouter.Api/Controllers/StoryEditorController.cs`
- `AgenticRouter.Api/Program.cs`
- `AgenticRouter.Api/wwwroot/story-files.js`
- `AgenticRouter.Api/wwwroot/story-editor.js`
- `AgenticRouter.Api/wwwroot/story-editor.css`
- `AgenticRouter.Api/wwwroot/index.html`
- `AgenticRouter.Api/wwwroot/app.js`
- `AgenticRouter.Api/wwwroot/projects-sessions.js`
- `tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs`
- `docs/rpg-specialist-editor.md`

## Commands and evidence

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include AgenticRouter.Api/Chat/StoryWorkspaceFiles.cs AgenticRouter.Api/Controllers/StoryEditorController.cs tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs
node --check AgenticRouter.Api/wwwroot/story-files.js
node --check AgenticRouter.Api/wwwroot/story-editor.js
node --check AgenticRouter.Api/wwwroot/app.js
node --check AgenticRouter.Api/wwwroot/projects-sessions.js
.\.tmp\Test-RpgEditor.ps1 -LogName 'story-workspace-files-final.trx'
git diff --check
```

- Release: zero warnings/errors. Scoped format, JavaScript syntax and intended-diff
  inspection passed. Unrelated dirty work was preserved.
- Initial run: 43 passed, one failed because the workspace-open button was not
  refreshed during initial bootstrap. Fixed bootstrap and project-refresh updates.
- Full corrected suite: **44 passed, zero failed/skipped**, 2m05s.
  Result: `.artifacts/rpg-editor/TestResults/story-workspace-files-final.trx`.
- Visual review moved save success/conflict feedback beside Save to workspace.
  The final save/conflict and responsive-layout cases passed again: **2 passed**,
  zero failed/skipped, 6s; `story-workspace-feedback-final.trx` in the same folder.
  The save-conflict test also asserts that the complete feedback is inside the viewport.
- Real browser, running Host API and real fixture filesystem were exercised.
  Only the external model provider was simulated; no model inference was needed.
- Added coverage: nested-folder selection, filtering, explicit Apply versus Save,
  BOM/CRLF preservation, external-edit conflict, concurrent stale saves, active-project
  mismatch, traversal/absolute/ADS/protected paths, directory junction escape, invalid
  UTF-8, read-only filtering and responsive picker widths down to 320px.
- Reviewed screenshots under `.artifacts/rpg-editor/visual-review/`:
  `story-workspace-picker.png`, `story-workspace-picker-1600.png`,
  `story-workspace-picker-390.png`, `story-workspace-picker-320.png`,
  `story-workspace-conflict.png`.

## Boundary

The save is exclusive and verified, but in-place rather than crash-atomic. Disk or
power failure during writing is not covered by optimistic conflict protection.
Mixed line endings are normalized to the existing file's CRLF/LF/CR convention
when edited. Files must be UTF-8; unsupported encodings return a typed error.
Neither the user's AR nor Ollama was restarted. Isolated builds do not activate
new API endpoints in an already-running older AR build.
