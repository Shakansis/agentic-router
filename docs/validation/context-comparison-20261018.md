# Requested context and paired Storytelling comparison — 2026-10-18

## Changes

- Context preferences may exceed a model's declared capability. Removed the exact-model override rejection; retained valid numeric ranges, ordered role profiles, the configured provider ceiling, metadata validation and request-fit errors. The previous 1M configuration ceiling is now the existing integer contract maximum (2,147,483,647). Saved/default settings are not automatically changed.
- Runtime resolution retains the requested target and caps the effective window to model capacity. A maximum between escalation ladder entries is usable if the request fits. Requests larger than that maximum still fail before inference.
- Chat usage exposes requested/effective/model-maximum values from Host resolution. A reduction produces a visible notice. The context popover fits the available space above the composer.
- Comparison columns scroll proportionally together, including the proposal textarea. Matching edit groups use a four-color palette and retain deletion/insertion decorations. Hover or keyboard focus highlights the corresponding group only in preview. Pure additions/deletions do not invent counterparts. Draft text, explicit Apply and Undo remain unchanged.

## Files

- `Configuration/ApplicationSettings.cs`, `Runtime/OllamaRuntimeProfile{Service,Resolver,Contracts}.cs`: configuration acceptance and runtime resolution.
- `Contracts/ApplicationContracts.cs`, `Chat/ChatStreamService.cs`: Host context facts through streamed and completed usage, including external harness context snapshots.
- `wwwroot/{index.html,settings-runtime.js,app.js,conversation-render.js}`: editable limits, help and usage panel.
- `wwwroot/story-editor.{js,css}`: paired diff and synchronized scroll.
- `LargeContextEndToEndTests.cs`, `StoryEditorEndToEndTests.cs`, `ProviderAndUiEndToEndTests.cs`: E2E coverage and updated usage-panel contract.
- `docs/model-runtime-performance.md`, `docs/rpg-specialist-editor.md`: behavior documentation.

## Validation

- `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers`: final build passed, zero warnings/errors.
- Scoped `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include ...` passed for the changed C# files; the final test-only correction was verified separately.
- `node --check` passed for `story-editor.js`, `conversation-render.js`, `app.js` and `settings-runtime.js`.
- `git diff --check` passed (existing Git CRLF/LF notices only). Reviewed intended changes without reverting unrelated dirty work.
- `.tmp/Test-RpgEditor.ps1` ran the complete StoryEditor and LargeContext classes plus `ContextUsagePopoverPrioritizesSummaryAndCollapsesAdvancedDetails`, `OllamaRuntimeProfilesResolveAnalyzeMeasurePersistAndRestore`, `NativeHarnessPublishesLiveEstimatedContextDuringThinking` and `QwenCodeUsesTheHostResolved128kContextWindow`.
- Regression run: **58 passed / 1 failed / 0 skipped**. The new between-ladder test initially configured a 65,536 role maximum while retaining a 40,960 global provider ceiling, and correctly failed settings validation. Fixed the fixture's provider ceiling; no product validation was weakened.
- Final rerun of all three LargeContext cases: **3 passed / 0 failed / 0 skipped**. All **59 distinct regression cases** have a passing result with the final implementation. Four focused cases also passed before the regression run.

Evidence under `.artifacts/rpg-editor/`:

- `TestResults/context-comparison-focused.trx`
- `TestResults/context-comparison-regression.trx`
- `TestResults/context-comparison-context-final.trx`
- `visual-review/story-paired-changes-hover.png`
- `visual-review/requested-effective-context.png`
- `visual-review/rpg-comparison-390.png` and other responsive viewport captures

Inspected desktop paired colors/hover, the context warning and the mobile stacked comparison. Browser/API tests cover 256k requested with 65,536 effective at the fake Ollama wire, 2M preferences saved through Settings, JSON/YAML persistence, between-ladder resolution, oversized input rejection, scroll in both directions and edit mode, keyboard correspondence, Apply/Undo and the editor's existing workflows.

## Boundaries and cleanup

These are real browser + Host API E2E tests with Ollama faked only at its external boundary. No real model/GPU allocation or quality evaluation was performed. Effective context here is the Host-selected window sent to the provider, not independent evidence of VRAM allocation.

No user AR or Ollama service was restarted. Changes were built into isolated artifacts; activating the backend requires the user's normal AR rebuild/restart. Preserve any unsaved editor draft before doing so.

Final process inspection found no testhost, isolated AR, fake Ollama CLI, headless browser or dotnet build server remaining. Preserved user processes: AR PID 16544 (normal Release executable, Visual Studio parent 40320; ports 5294/7155/7156), Ollama PID 21428 (port 11434), additional Ollama PID 48652 and AR-managed Ollama PID 9912 (parent 16544).
