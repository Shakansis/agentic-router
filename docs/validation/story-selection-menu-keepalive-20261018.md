# Story editor: persistent selection, View context menu and model retention

## Changes

- View selection survives focusing/typing in instructions. A CSS Custom Highlight
  keeps it visible without modifying rendered HTML or source Markdown. Source
  positions remain tied to the draft revision; a new preview or draft edit invalidates
  them. Raw selection remains usable after instructions and full-screen transitions.
- View right-click offers Select section for a paragraph, enclosing speech blockquote,
  or heading section. A heading includes subordinate headings and stops before the
  next equal/higher heading and its preceding legacy anchor. Anchors now expose the
  same Host-generated render-only source coordinates used elsewhere in the preview.
- Find synonym for the clicked word maps its actual pointer location to the exact
  source occurrence. Whitespace/numeric-only hits do not generate a word request.
  Context-menu labels and highlights use safe text/native DOM APIs. Menu/Shift+F10,
  focus navigation and Escape are supported; proposals still require explicit Apply.
- StoryEditorService supplies an internal, JSON-ignored minimum keep-alive hint of
  1,800 seconds through common Chat/provider context. Only target responses receive
  it. Ollama request generation applies the floor for streamed, tool-enabled and
  ordinary generation paths, retaining larger effective settings and unlimited (-1).
  Existing settings and ordinary Chat payload behavior remain unchanged.

## Changed files

- `AgenticRouter.Api/wwwroot/story-editor.js`, `story-preview.js`, `story-view-menu.js`
- `AgenticRouter.Api/wwwroot/story-editor.css`, `index.html`
- `AgenticRouter.Api/Markdown/StoryMarkdownDocument.cs`
- `AgenticRouter.Api/Chat/StoryEditorService.cs`, `ChatStreamService.cs`
- `AgenticRouter.Api/Contracts/ApplicationContracts.cs`
- `AgenticRouter.Api/Usage/UsageContracts.cs`
- `AgenticRouter.Api/Providers/Ollama/OllamaClient.cs`
- `tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs`
- `docs/rpg-specialist-editor.md`

## Validation

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include AgenticRouter.Api/Contracts/ApplicationContracts.cs AgenticRouter.Api/Usage/UsageContracts.cs AgenticRouter.Api/Chat/ChatStreamService.cs AgenticRouter.Api/Chat/StoryEditorService.cs AgenticRouter.Api/Providers/Ollama/OllamaClient.cs AgenticRouter.Api/Markdown/StoryMarkdownDocument.cs tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs
node --check AgenticRouter.Api/wwwroot/story-editor.js
node --check AgenticRouter.Api/wwwroot/story-preview.js
node --check AgenticRouter.Api/wwwroot/story-view-menu.js
.\.tmp\Test-RpgEditor.ps1 -LogName 'story-selection-menu-keepalive-verified.trx'
git diff --check
```

- Final Release build: zero warnings/errors. Scoped format and JavaScript syntax
  checks passed. Intended changes reviewed while preserving unrelated dirty work.
- Final real-browser/Host E2E: **51 passed, zero failed/skipped**, 1m55s.
  Evidence: `.artifacts/rpg-editor/TestResults/story-selection-menu-keepalive-verified.trx`.
- New cases verify selection after typing instructions in Raw and View, persistent
  visible highlight, exact second-occurrence replacement, paragraph/speech/scene
  boundaries, exclusion of the following scene's anchor, clicked-word synonyms,
  and outbound keep_alive values 1800/3600/-1 with settings and ordinary Chat unchanged.
- Early validation caught an omitted keep_alive argument in the tool-enabled
  response path. That path was corrected. Retention fixtures were also corrected
  to configure Primary, the actual runtime role used for Auto Chat's preferred model.
- Screenshots reviewed under `.artifacts/rpg-editor/visual-review/`:
  `story-preserved-view-selection.png`, `story-view-section-menu.png`,
  `story-view-synonym-menu.png`. Existing responsive editor/comparison checks remain green.

## Limits

The provider was simulated at its external boundary. Tests verified the actual
outbound retention parameter, not a 30-minute real-GPU residency experiment. Explicit
model switching/unloading or runtime resource constraints can still evict a model.
There are no background keep-alive calls, persisted settings changes or Ollama restarts.
Existing selection-size limits continue to apply to a whole selected section.
An older running AR needs its normal rebuild/restart to activate the Host change.
