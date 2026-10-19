# Fixed specialists and RPG editor — 2026-10-17

Implemented the approved first increment described in
[`rpg-specialist-editor.md`](../rpg-specialist-editor.md). Automatic keyword
routing remains the default, with its existing rules and precedence unchanged.

## Delivered behavior

- Visible specialist selector and Host-resolved identity in the chat header.
  Fixed selection is independent of model/harness selection, follows common Host
  routing, and is saved with turns when conversation history is enabled.
- RPG plain-text/Markdown editor: import copies, review a selected passage,
  contextual synonym candidates, original/replacement comparison, editable
  proposals, explicit application, one-step undo, local repetition counts with
  occurrence navigation, and exact UTF-8 Markdown download.
- Revision checks preserve drafts when editing during generation or review.
  Provider errors, malformed synonyms, cancellation and discarded proposals do
  not apply text. Only selection and bounded adjacent context reach generation.
- No new model, agent, external dependency or frontend build pipeline.

## Files in this change

New files:

- `AgenticRouter.Api/Chat/StoryEditorService.cs`
- `AgenticRouter.Api/Controllers/StoryEditorController.cs`
- `AgenticRouter.Api/wwwroot/story-editor.js`
- `AgenticRouter.Api/wwwroot/story-editor.css`
- `tests/AgenticRouter.EndToEndTests/StoryEditorEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/FakeOllamaServer.StoryEditor.cs`
- `docs/rpg-specialist-editor.md` and this validation record.

Integration changes:

- `AgenticRouter.Api/Routing/IntentionRouter.cs`, `Contracts/ApplicationContracts.cs`,
  `Controllers/ChatController.cs`, `Chat/ChatStreamService.cs`, and `Program.cs`.
- `AgenticRouter.Api/Sessions/PersistentSessionService.cs`.
- `AgenticRouter.Api/Supervision/SupervisionContracts.cs`,
  `SupervisionRouteResolver.cs`, `DurableSupervisionRunCoordinator.cs`, and
  `SupervisionExecutionEngine.cs`.
- `AgenticRouter.Api/wwwroot/index.html`, `app.js`, `conversation.js`,
  `conversation-stream.js`, `conversation-render.js`, and `projects-sessions.js`.
- `tests/AgenticRouter.EndToEndTests/FakeOllamaServer.cs`,
  `ExecuteCoreEndToEndTests.cs`, and `DurableSupervisionEndToEndTests.cs`.
- `README.md` and `docs/README.md`.

Unrelated dirty work already present in the checkout was retained.

## Validation

Release build:

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers
```

Passed with **0 warnings and 0 errors**. Output was isolated because the user's
existing Release application was running.

`dotnet format AgenticRouter.slnx --verify-no-changes --no-restore --include ...`
passed for all C# files in this change. The unrestricted solution check reports
pre-existing import-order errors in five unchanged files:
`HistoryFilesController.cs`, `HarnessInferenceObserver.cs`,
`OllamaHarnessWireAdapter.cs`, `ConversationHistoryFiles.cs`, and
`HistoryContentStore.cs`. They were left untouched.

`node --check` passed for all six changed JavaScript files. `git diff --check`
passed; intended integration hunks were reviewed separately from existing dirty
work.

Playwright/MSTest ran against an isolated running Release API, Edge headless, and
fake provider/harness processes at their external boundaries. Executables were
selected with `AGENTIC_ROUTER_REPOSITORY_ROOT` and the six
`AGENTIC_ROUTER_E2E_*_PATH` overrides. The local helper is
`.tmp/Test-RpgEditor.ps1`.

**37 unique E2E cases passed:** 11 new editor/specialist cases and 26 relevant
regressions for routing, inference profiles, history, responsive composer,
cloud controls, slow-request handling, supervision and Native/Codex/Claude/
OpenCode/Qwen cancellation. The final set consists of 36 passing cases in
`.artifacts/rpg-editor/TestResults/rpg-acceptance.trx` and the corrected
cancel/rollback/resubmit case passing separately in
`.artifacts/rpg-editor/TestResults/rpg-supervision-final.trx`.

The existing routing test now asserts the exact five already-supported read-only
tools, including `read_files`. The supervision test uses the standard explicit
snapshot-consent helper, the current **Rollback** control, and waits for the
rollback to finish before resubmission. No assertions or time budgets were relaxed.

Cancellation exposed a pending-read/disposal race in the shared Host stream and
its supervision wrapper. Cleanup now settles the pending asynchronous read before
disposing the iterator, preserving cancellation semantics. The editor cancellation
case also verifies that another proposal succeeds and no `NotSupportedException`
was logged.

Screenshots at 1600, 1280, 760 and 390 pixels were captured; desktop and mobile
renders were inspected. Local previews:
[desktop](../../.artifacts/rpg-editor/preview-desktop.png),
[mobile](../../.artifacts/rpg-editor/preview-mobile.png).

Final process/port audit found no remaining validation API, fake provider,
testhost, browser, or persistent .NET build process. The original user API
remained PID 36540, parent 40320, with listeners on 5294, 7155 and 7156;
the original Ollama remained PID 21428, parent 20728, listening on 11434.
Neither was stopped, restarted, or used for inference validation.

## Limits

- No real local/cloud inference was run. Narrative and contextual synonym quality
  are not established by these deterministic tests.
- Drafts live in the current browser tab and must be exported to retain them after
  reload/close. They are not automatically added to conversation history.
- The complete repository E2E suite was not run; the selected 37 cases cover the
  changed paths and their relevant shared behavior.
- The running user instance was preserved and was not redeployed or restarted;
  using the new backend there requires rebuilding/restarting that instance.
