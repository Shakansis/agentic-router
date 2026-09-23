# Missing Qwen session and failed-turn edit continuity

The trace `0HNOL1KC121FB:00000016` failed while opening Qwen's event stream,
before prompt submission. The same daemon had completed the previous turn about
31 minutes earlier. The installed Qwen runtime defaults to reaping disconnected
sessions after 30 minutes; the Host still held the old session ID. The exact reap
event was not present in the retained daemon log, so expiry is consistent with the
evidence rather than directly logged.

The subsequent `0HNOL1KC121FB:0000002C` failed with `session-edit-conflict`. The Host
persists terminal assistant errors, while the browser previously appended only
successful assistant answers to its history. This shifts later user-message indices
and can cause the observed edit conflict. The original trace did not record the
browser's submitted index.

## Correction

- Qwen recovery recognizes only HTTP 404 with JSON `code=session_not_found` and
  the exact expected session ID when opening the event stream, before submission.
  It removes the stale mapping, creates and validates one new native session,
  and hydrates canonical Host history. The normal path has no extra HTTP request.
- No daemon restart, model reload, timeout increase, or accepted-prompt replay is
  used. A second missing-session failure terminates this strategy. An existing
  context/transient recovery does not receive another missing-session retry.
  Other 404 responses keep their existing failure behavior. Recovery is observable
  through the existing warning activity.
- The browser now includes typed terminal error answers in local history, keeping
  subsequent edit indices aligned with persisted history. Rejected persisted edits
  restore the original visible/local conversation and keep the edited draft. Host
  conflict validation remains unchanged; no approximate replacement is introduced.

Changed files: `AgenticRouter.Api/Execution/QwenCodeHarness.cs`,
`AgenticRouter.Api/wwwroot/app.js`, `tests/FakeQwenCodeServer/Program.cs`,
`tests/AgenticRouter.EndToEndTests/BenchmarkAndHarnessEndToEndTests.cs`,
`tests/AgenticRouter.EndToEndTests/ExecuteCoreEndToEndTests.cs`, and this document.

Validation uses browser/API E2E and fake external providers. New coverage includes
missing-session recovery, repeated disappearance, unrelated/mismatched 404,
canonical hydration without daemon restart, editing after a provider failure,
reopening the edited saved history, and rejection after an external history change.
Release output is isolated under `bin/session-recovery/`. The user's application
and installed Qwen runtime are not restarted or modified; no real inference runs.

Final validation (2026-09-18): **36/36 E2E passed, zero skipped**. Release build
passed with zero warnings/errors; formatting verification and intended-diff checks
passed. Test processes exited; the existing user API (PID 38976) remained running.

Commands:

```text
dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false -p:BaseOutputPath=bin/session-recovery/
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include <four changed C# files>
dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-build --no-restore -m:1 -p:BaseOutputPath=bin/session-recovery/ --filter <selected recovery/edit/regression cases> --logger "trx;LogFileName=session-expiry-edit-final.trx"
git diff --check
```

API and fake-harness executable path overrides point to the isolated output.
The selected cases and individual results are recorded in
`tests/AgenticRouter.EndToEndTests/TestResults/session-expiry-edit-final.trx`.
Activating the changes in the user's application requires its normal rebuild and
restart; this validation did not replace the running binaries.
