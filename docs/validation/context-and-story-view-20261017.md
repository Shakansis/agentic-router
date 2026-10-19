# Context ceiling and Storytelling View fixes — 2026-10-17

## Delivered behavior

- Configurable context ceiling raised from 131,072 to 1,048,576 tokens in Host validation, global context inputs, role profiles, exact-model overrides and benchmark input/help. Existing configured/default values remain unchanged. The provider ceiling, role ordering, output reservation and model-declared capability checks still apply. The separate Native file-creation output limit is unchanged.
- Storytelling View fragment links scroll only the preview. Explicit empty HTML anchor markers (`id` or legacy `name`) and Markdown heading IDs are supported; source examples inside code remain literal. The UI scopes IDs before inserting preview HTML and transforms local links into document-only controls, so URL, browser history and tab count stay unchanged.
- View selections map to the exact UTF-16 range in the original Markdown using Host-generated syntax-tree coordinates. Revision and synonyms remain enabled after focus moves to instructions. Whole formatted selections include their Markdown delimiters. Repeated phrases use their actual source positions. Applying, undoing and exporting retain the normal draft workflow. A selection that cannot map precisely asks for Raw rather than guessing a source occurrence.
- Arbitrary raw HTML stays escaped and the final HTML sanitized. Other Markdown surfaces still use the original renderer path. No draft/file rewrite is required to support navigation or selection.

## Files

- Context: `Configuration/ApplicationSettings.cs`, `SettingsValidator.cs`, `wwwroot/index.html`, `settings-runtime.js`, `benchmark.js`, `benchmark-help.html`.
- View: `Markdown/IMarkdownRenderer.cs`, `SafeMarkdownRenderer.cs`, new `StoryMarkdownDocument.cs`, `Chat/StoryEditorService.cs`, `wwwroot/story-editor.js`, new `story-preview.js`, `story-editor.css`.
- Coverage: new `LargeContextEndToEndTests.cs`, `StoryEditorEndToEndTests.cs`, external `FakeOllamaServer.cs` metadata fixture.
- Specifications: `docs/model-runtime-performance.md`, `docs/rpg-specialist-editor.md`.

## Executed checks

- `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers`: passed, zero warnings/errors.
- Scoped `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore --include ...` on all changed C# files: passed.
- `node --check` on story-editor, story-preview, settings-runtime and benchmark JavaScript: passed. `git diff --check`: passed. Intended changes reviewed while preserving pre-existing dirty work.
- Final E2E command: `.tmp/Test-RpgEditor.ps1 -Filter 'FullyQualifiedName~AgenticRouter.EndToEndTests.StoryEditorEndToEndTests|FullyQualifiedName~LargeContextEndToEndTests|FullyQualifiedName~ModelTestExplainsInsufficientSelectedContextWithoutChangingIt' -LogName 'context-and-story-view-final.trx'`.
- **29 passed, 0 failed, 0 skipped**, 1m40s. Evidence: `.artifacts/rpg-editor/TestResults/context-and-story-view-final.trx`.

Context tests save 250,000, 1,000,000 and 1,048,576 through the browser, persist an exact-model override, round-trip JSON/YAML across an isolated Host restart, and observe `num_ctx=1048576` at the fake Ollama boundary. A 65,536-token model remains capped at 65,536. Invalid global ceilings, ordering, ladder values and model-specific overrides remain rejected atomically.

View tests verify no navigation/reload/new tab; missing/malformed anchors; explicit/legacy/heading anchors; literal anchor examples in code; preserved source export; partial and multiple-paragraph selections; bold, accents and entities; exact repeated-phrase replacement; retained selection after editing instructions; comparison, apply, undo and synonyms. The complete pre-existing Storytelling editor suite also passes.

## Visual review and runtime boundary

Inspected `.artifacts/rpg-editor/visual-review/million-token-context.png`, `story-local-index.png` and `story-view-selection.png`: the expanded values are visible, the selected scene is scrolled/highlighted inside the editor, and review is enabled while instructions have focus.

All inference in this validation used an external fake provider. No real 1M-token allocation or GPU/model throughput claim is made. No user-owned Ollama process was restarted or model downloaded/removed. Test hosts, fake provider CLIs and Playwright headless processes terminated after the suite.

The existing AR PID 14028 and Ollama PIDs 21428/48652/48588 were left intact. A read-only preview probe of the currently running AR did not return the new source-map attributes, confirming it still has the earlier backend. Rebuild/restart AR normally to activate these changes; Ollama does not need restarting.
