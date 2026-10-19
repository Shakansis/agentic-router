# Composer project-file references — 2026-10-19

Implemented `@` search of the active trusted workspace, keyboard/mouse selection,
inline filename badges with relative-path tooltips and removal controls. Messages
and clipboard text contain the path at the original insertion position; this
feature does not read or attach file contents. See
[behavior contract](../composer-file-references.md).

## Changed files for this feature

- `AgenticRouter.Api/WorkspaceProfiles/WorkspaceFileSearch.cs`: bounded, cancellable,
  project-scoped search through the existing trusted-path validator.
- `AgenticRouter.Api/Controllers/WorkspaceFilesController.cs`, `Program.cs`: API
  translation and service registration.
- `wwwroot/composer-files.js`, `composer-files.css`, `index.html`: inline editor,
  accessible search list, safe text serialization and responsive presentation.
- `wwwroot/app.js`, `conversation.js`: initialization and keyboard integration.
- `ComposerFilesEndToEndTests.cs`: nine browser/API tests with fake external providers.
- Existing composer assertions in BenchmarkAndHarness, DurableSupervision,
  ExecuteCore, ExecutionState, HistoryFiles and ProviderAndUi tests now read the
  editor's value property instead of requiring a native textarea. The project
  knowledge text assertion retains its regular-expression match.
- README and documentation map link the feature contract.

Unrelated dirty changes in this checkout were retained.

## Validation

- `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers`: passed, zero warnings/errors.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include ...`:
  passed for the new C# files, Program and the six adjusted test files.
- `node --check` for `composer-files.js`, `app.js`, `conversation.js`: passed.
- `git diff --check`: passed; intended changes reviewed alongside pre-existing work.
- `.tmp/Test-RpgEditor.ps1` executes the compiled Release E2E assembly with
  `playwright.runsettings`, an isolated API and fake external providers.
  `composer-files-regression.trx`: **22/22 passed**. It covers all nine new tests,
  responsive composer controls, focus, image picker/paste/drop, current immediate
  Chat rewind, Execute rollback, harness choices, all five harness user-input
  paths, and pending-input restoration after the isolated Host restarts.
- `composer-files-final.trx`: **2/2 passed**, covering the final narrow-screen capture
  and project-knowledge prompt preparation. One overlaps the previous run:
  **23 distinct passing scenarios** across these two runs.

The new tests verify same-name files, relative-path transmission with no attached
contents, Unicode/backticks, mouse and keyboard insertion, native undo/redo,
deletion, middle-of-sentence insertion, multiline plain-text paste, no HTML
injection, clipboard serialization, email/IME/empty-result behavior, stale project
IDs, traversal rejection, reparse-point exclusion, partial-result refinement,
queue transmission and badge restoration through the current rewind flow.

The first exploratory run exposed two new-editor issues (caret position after
insertion and a trailing browser placeholder newline); both were fixed and their
tests pass. It also exposed two **unchanged legacy test expectations**:
`EditingMessageKeepsSubmitControlsAlignedAndCanBeCancelledVisibly` and
`BrowserMessageBufferWaitsForInlineEditingThenDispatchesTheEditedPrompt` still
expect the old cancellable Chat-edit flow. Current `startMessageEdit` and the
approved `conversation-rewind-and-history-files.md` restore and truncate
immediately. Those legacy tests were not weakened or reported as passing.
Current rewind and a separate actual-reference queue test passed. This is not a
claim that the entire repository suite passes.

## Visual review and runtime boundary

Inspected Playwright captures at 1280×720 and 390×844:

- `.artifacts/rpg-editor/visual-review/composer-file-badge.png`
- `.artifacts/rpg-editor/visual-review/composer-file-search-mobile.png`
- `.artifacts/rpg-editor/visual-review/composer-file-badges-mobile.png`

The list distinguishes duplicate basenames by path, and inline badges and controls
fit the narrow viewport. The final mobile capture has no selection overlay.

No real-model inference was used. Test API/provider/browser processes exited.
Read-only process/port inspection found no remaining test Host, fake provider,
testhost, or build server. User services were left untouched: AR PID 36604 on
5294/7155/7156, Ollama PID 24372 on 11434, and AR-managed Ollama PID 37060 on 16535.
The open AR instance already returned the expected typed HTTP 409 for an invalid
workspace ID on the new endpoint at `http://127.0.0.1:5294/api/workspace/files`.
This verifies endpoint availability, not real-provider acceptance.
