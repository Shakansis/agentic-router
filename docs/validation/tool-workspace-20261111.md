# Shared composer and tool workspace validation — 2026-11-11

## Result

Implemented the permanent Chat pane, initially closed auxiliary tool pane,
resizable split and shared composer. The Storytelling Editor runs in the tool
tab with a collapsible controls sidebar. Narrow layouts switch the visible
pane; full screen retains the same composer. Jump to latest is anchored to the
chat viewport.

The Host receives the active tool, bounded revision preferences, draft revision
and selection. The selected specialist can declare a different destination with
`set_response_target`, or propose a replacement for explicit review and Apply.
Activity remains visible in Chat. Queued submissions retain their initial
destination and snapshot. Host conversation history excludes draft/proposal
payloads. See [the feature contract](../tool-workspace.md).

## Validation executed

- Release solution build: **passed, zero warnings and zero errors**, using the
  isolated `bin/tool-workspace/` output path.
- C# whitespace formatting and `--verify-no-changes`: **passed**, scoped to the
  changed C# files.
- `node --check`: **passed** for `tool-workspace.js`, `story-editor.js`,
  `conversation.js`, `conversation-stream.js` and `app.js`.
- `git diff --check` and intended-diff inspection: **passed**. Pre-existing
  provider/model/settings work was retained.
- Main browser/API regression: **72/72 passed** in
  `tests/AgenticRouter.EndToEndTests/TestResults/tool-workspace-final.trx`.
- After adding shared-composer revision preferences and the tool-directed
  supervised scenario, the complete tool workspace class was rechecked:
  **15/15 passed** in `tool-workspace-context-final.trx`. There are **73 distinct
  passing scenarios** across these two runs; 14 cases overlap.

The tests used Edge headless, isolated running ASP.NET APIs, real temporary
workspaces, and fakes only at provider/harness boundaries. No real model
inference, GPU workload or cloud-provider validation was performed. E2E local
IPC required execution outside the sandbox after its socket restriction
prevented the test runner from connecting to the testhost.

Coverage includes closed-by-default tools; pointer and keyboard resizing;
desktop/mobile layouts; fullscreen and selection continuity; revision preferences;
proposal review, Apply and Undo; concurrent draft changes; explicit Chat output;
queued-message destinations; Native tools and structured responses, including
compaction; all four external harness routes; and tool-directed supervised
completion with internal protocol decisions kept separate from the answer.
Existing editor, workspace-boundary, file-version and read-only Chat regressions
also passed. Screenshots under `.artifacts/tool-workspace/` were inspected.

## Principal commands

```powershell
dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false -p:BaseOutputPath=bin/tool-workspace/
```

Browser tests set `AGENTIC_ROUTER_E2E_API_PATH` to the API executable in that
isolated output directory and ran:

```powershell
dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-build --no-restore -m:1 -p:BaseOutputPath=bin/tool-workspace/ --filter FullyQualifiedName~AgenticRouter.EndToEndTests.ToolWorkspaceEndToEndTests --logger 'trx;LogFileName=tool-workspace-context-final.trx' -- Playwright.BrowserName=chromium Playwright.LaunchOptions.Channel=msedge
```

The 72-case filter additionally included `StoryEditorEndToEndTests`,
`ExternalHarnessHostBridgeExecutesCanonicalBatchThroughHost`,
`ChatWithholdsStructuredToolPreambleAndExecutesReadOnlyCall`,
`CorrectAtomicWorkConvergesOnFirstWorkerAttempt`,
`NativeExecuteIntroducesWorkBeforeTheFirstActionAndPreservesTheFinalReport`, and
`ComposerExposesIntegratedCompactStrategySplitButtonIncludingAutonomous`.
Formatting used `dotnet format whitespace . --folder --verify-no-changes
--include` with the changed C# file list.

## Files changed for this feature

- UI: `wwwroot/tool-workspace.js`, `wwwroot/tool-workspace.css`, `wwwroot/app.js`,
  `wwwroot/index.html`, `wwwroot/story-editor.js`, `wwwroot/conversation.js`,
  `wwwroot/conversation-stream.js`.
- Host: `Chat/ToolWindowTurn.cs`, `Chat/ChatStreamService.ToolWindow.cs`,
  `Chat/ChatStreamService.cs`, `Contracts/ApplicationContracts.cs`,
  `Controllers/ChatController.cs`.
- Tool projection and context: `Execution/LocalActionPlanner.cs`,
  `Execution/ToolNameResolver.cs`, `Execution/ExpertExecutionGuidanceService.cs`.
- Supervision: `Supervision/SupervisionContracts.cs`,
  `Supervision/SupervisionExecutionEngine.cs`,
  `Supervision/DurableSupervisionRunCoordinator.cs`.
- E2E: `ToolWorkspaceEndToEndTests.cs`, `StoryEditorEndToEndTests.cs`,
  `FakeOllamaServer.cs`, `FakeOllamaServer.StoryEditor.cs`.
- Documentation: `docs/tool-workspace.md`, `docs/rpg-specialist-editor.md`, and
  this validation record.

UI/Host paths above are relative to `AgenticRouter.Api/`; E2E paths are relative
to `tests/AgenticRouter.EndToEndTests/`.

## Limits and activation

Real-model interpretation and real installed-harness acceptance were not tested.
Unsaved drafts remain window-local; checkpoints do not reconstruct them after a
Host restart. Browser access/usage is not enabled by this increment.

The user's running application was not restarted or replaced. Activation uses
the normal rebuild/restart workflow, preserving any unsaved draft first.
