# Conversation presentation and model context

The visible transcript and the model's operational context have different retention rules.

- Preserve the complete saved transcript, including response blocks, reasoning blocks, timestamps and the existing event timeline. Model-context compaction must not replace that transcript with a generated continuity message.
- Keep the existing 10 MiB operational compaction threshold and approximately 5 MiB target. These remain compaction thresholds, not write-failure limits.
- Before committing a compact session JSON, write the original messages to a lossless gzip transcript. The JSON's `transcriptId` references a content-addressed file beside it, named `{sessionId}.{sha256}.history.gz`. Commit the transcript before atomically replacing the JSON; retain the prior transcript if the JSON write fails. Remove superseded transcripts only after commit. Conversation deletion removes its transcript files too.
- Store operations hydrate original messages for continuation, editing, duplication and export. `contextMessages` carries the compact model context separately. The browser sends only the unsaved suffix when saving a reopened conversation, retaining the Host's original prefix and event details.
- Initially replay at most the latest 20 messages. Limit each page to approximately 2,000 timeline events; always include one complete message even when it exceeds the budget, and retain adjacent user messages without events. The same rule applies to initial loading and **See more**, including histories that have not yet been archived. **See more** retrieves preceding pages and replays them through the same components. Original message indices remain stable for editing. Historical replay consumes the saved event objects directly and yields to the browser after about 8 ms of work, including within a single large message. It does not recreate an SSE payload or force a Thinking layout measurement for every delta. A conversation switch stops the previous replay. Loading a page does not invoke a model or resume actions or approvals.
- Restore the saved model, harness and interaction settings. Display the latest available context measurement as **last recorded**, not as a live measurement. A new request replaces it with fresh Host measurements.
- Legacy continuity messages remain available in collapsed details and remain part of the model context. Do not render them as normal assistant answers. Earlier originals already discarded by the former compactor cannot be reconstructed from those summaries.

Validation uses browser/API E2E tests with simulated providers, including compaction, restart, page restoration, continuation, saved routing and deletion. It does not require real inference.

## Execution status while scrolling

- During an active response, the existing execution session header uses CSS sticky
  positioning at the top of the chat's scrollable content area, respecting its
  existing padding. It remains in normal flow until scrolling reaches it. Native
  and supervised status renderers share this behavior; no extra activity state or
  scroll listener is introduced.
- On completion, error or cancellation, the header returns to normal positioning.
  One static copy of its final contents appears after Technical details, before
  the response action buttons. Repeated terminal events update that copy rather
  than appending more copies. A missing terminal session snapshot uses the actual
  response outcome for its final state label.
- Historical replay never leaves a sticky header active. The closing copy is
  reconstructed through the existing terminal rendering, without extra persisted
  markup. Thinking content and its visible area remain unchanged.
- Mid-turn steering removes stickiness from the preceding response segment and
  carries the current status into the continuing segment.
- Both copies wrap their metadata into two columns on narrow screens.

Changed for this presentation update: `AgenticRouter.Api/wwwroot/app.js`,
`AgenticRouter.Api/wwwroot/styles.css`,
`tests/AgenticRouter.EndToEndTests/ExecutionStateEndToEndTests.cs`,
`tests/FakeQwenCodeServer/Program.cs`, and this document.

Validation on 2026-09-18: **11/11 browser/API E2E passed, zero skipped**. Coverage
includes normal and narrow viewports, scroll positioning, success/failure/cancel,
one final copy, saved replay, supervision, Thinking, and mid-turn steering. Release
build passed with zero warnings/errors; formatting verification and diff checks
passed. Providers/harnesses were faked at their external boundaries; no real
inference ran and the user's running application was not restarted.

Commands: `dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false
-p:BaseOutputPath=bin/sticky-status/`; `dotnet format AgenticRouter.slnx --no-restore
--verify-no-changes --include` the two changed C# test/fixture files; and
`dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj
-c Release --no-build --no-restore -m:1 -p:BaseOutputPath=bin/sticky-status/
--filter <selected UI cases> --logger "trx;LogFileName=sticky-status-final.trx"`.
The test API/fake harness path overrides point to the isolated output. Results:
`tests/AgenticRouter.EndToEndTests/TestResults/sticky-status-final.trx`.
