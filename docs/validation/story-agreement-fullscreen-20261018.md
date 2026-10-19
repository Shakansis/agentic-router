# Storytelling Editor: contextual review, repetitions and full screen

## Delivered behavior

- Check agreement uses the existing Host proposal path, selected passage plus
  bounded adjacent context, and the configured storytelling specialist. The prompt
  requests nominal/verbal agreement corrections only, or an unchanged passage.
  The comparison, explicit Apply, discard, stale protection and undo are shared.
- Rewrite selection uses new wording while asking the model to preserve meaning,
  facts, voice and coherence with adjacent context. It uses the same comparison
  and explicit application contract.
- Synonym comparison includes the complete source sentence on both sides. Context
  stays unchanged while choosing/editing alternatives changes only the selection.
- An operation-specific animated loader appears while waiting, with Cancel visible;
  success, cancellation and errors clear it. Full-screen transitions keep it in view.
- Repeated-word groups retain occurrence cycling and add an explicit Synonyms
  action with an occurrence counter. Only that occurrence can be replaced. Counting
  and cycling stay local and make no model requests.
- Repetition counts now include only nearby occurrences: default maximum word-position
  distance 50, adjustable from 1 to 500. Distant isolated occurrences are excluded.
  Right-click/Shift+F10 opens Highlight all and Ignore from repetition. Highlights
  appear only in View, include all visible whole-word matches (also across inline
  formatting), and never change export. Ignored words can be restored and reset on
  importing a new draft.
- Full screen moves the existing editor into a viewport-sized native dialog.
  Return to split view or Escape restores the panel. It preserves Raw/View mode,
  source selection, draft, instructions, file name, pending proposal and undo.
  Comparison opens above the editor; Escape closes only the top comparison.
- Mobile header actions occupy a separate aligned row. Existing automatic routing
  and specialist-only editor availability remain unchanged.

## Changed files for this increment

- `AgenticRouter.Api/Chat/StoryEditorService.cs`
- `AgenticRouter.Api/wwwroot/index.html`
- `AgenticRouter.Api/wwwroot/story-editor.js`
- `AgenticRouter.Api/wwwroot/story-editor.css`
- `tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/FakeOllamaServer.StoryEditor.cs`
- `docs/rpg-specialist-editor.md`

## Executed validation

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include AgenticRouter.Api/Chat/StoryEditorService.cs tests/AgenticRouter.EndToEndTests/FakeOllamaServer.StoryEditor.cs tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs
node --check AgenticRouter.Api/wwwroot/story-editor.js
.\.tmp\Test-RpgEditor.ps1 -LogName 'story-agreement-fullscreen-final.trx'
.\.tmp\Test-RpgEditor.ps1 -LogName 'story-editor-context-rewrite-repetitions.trx'
.\.tmp\Test-RpgEditor.ps1 -Filter 'FullyQualifiedName~AgenticRouter.EndToEndTests.StoryEditorEndToEndTests.ProposalShowsLoaderUntilModelCompletesAndSurvivesFullscreenChange' -LogName 'story-loader-viewport-final.trx'
git diff --check
```

- Final Release build: zero warnings/errors. Scoped format, JavaScript syntax and
  intended-diff review passed. Git reported only existing CRLF normalization notices.
- First increment browser/API E2E: **29 passed, zero failed, zero skipped**, 1m32s.
  Result: `.artifacts/rpg-editor/TestResults/story-agreement-fullscreen-final.trx`.
- Expanded suite after the follow-up requests: **40 passed, zero failed, zero
  skipped**, 1m42s. Result:
  `.artifacts/rpg-editor/TestResults/story-editor-context-rewrite-repetitions.trx`.
- Visual inspection found the loader partially below the viewport after detaching
  during a pending request. After fixing scroll restoration, the four operation
  cases were rerun with an explicit full-viewport visibility assertion: **4 passed,
  zero failed/skipped**, 9s. Result: `story-loader-viewport-final.trx` in the same folder.
  The final `story-model-loading.png` was inspected with the complete loader visible.
- Added cases cover Raw/View agreement, unchanged output, exact second-occurrence
  synonyms, full-screen proposal/apply/undo/export, Escape/focus and responsive sizing.
  Further cases cover sentence/paragraph boundaries and inline Markdown, all four
  loading operations, rewrite prompt/context/application, distance filtering,
  keyboard/mouse context-menu use, reversible ignore and exact export after highlighting.
  Existing cases cover routing, stale proposals, failures/cancellation, imports,
  local anchors, exact View mapping, selected synonyms and comparison highlighting.
- Screenshots inspected under `.artifacts/rpg-editor/visual-review/`:
  `story-fullscreen-1600.png`, `story-fullscreen-390.png`,
  `story-fullscreen-320.png`, `story-fullscreen-comparison.png`,
  `story-repetition-synonyms.png`, `story-synonym-sentence.png`,
  `story-repetition-context-menu.png`, `story-repetition-highlights.png`.
  Responsive assertions also cover 760px, and
  the existing split-view coverage runs down to 320px.

## Validation boundary and activation

Tests use the real browser and Host API with a fake only at the external Ollama
boundary. They prove interaction, request scope and state preservation, not the
linguistic quality of a real model's agreement corrections or synonyms. No real
inference, model downloads or Ollama restarts were performed for this increment.
Build artifacts and test hosts were isolated from the user's AR. No test-owned
API, fake-provider, headless-browser or .NET build-server process remained after
validation. The user's AR process changed independently during the work; no restart
was performed by this task. An older running build needs an AR rebuild/restart for
the new Host operations. Export any unsaved editor draft before reloading its tab.
