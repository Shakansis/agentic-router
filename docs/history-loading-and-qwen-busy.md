# History loading and Qwen busy investigation

## Evidence from the reported conversation

Read-only inspection of conversation `a33926b73d0f4d8c92b38f90bf234065` found
57 messages. Its lossless gzip transcript was 12,892,032 bytes and expanded to
361,990,598 JSON characters. The latest 20 messages alone contained 123,796
timeline events. One message contained 31,738 events.

The browser converted those objects back to one synthetic SSE string, parsed it
again and rendered every event without yielding to a browser task. Thinking also
forced a layout read after each delta. This is bounded work, but sufficiently
large to explain an unresponsive main thread; no infinite loop was identified.

The fix pages by event count as well as message count, consumes historical event
objects directly, yields during a single long message, and avoids per-delta
Thinking layout reads during restoration. Nothing is deleted or compacted in the
visible transcript. The gzip storage format and model-context policy are unchanged.
The store still decompresses the full archive to serve a page; reducing server
archive I/O would require a separate storage change and is not part of this fix.

## Qwen failure chain

Evidence: native session `9957fa4e-fd16-4288-832a-c9366577033b`, daemon run
`36e92a7879ed13497165a3dc99859d83`, and local incident records. Times below are UTC.

- 2026-09-19 00:51:49: native prompt `2f670c45-1f7b-4c0e-b0c9-f0503f6c4561`
  was accepted. Opening its stream with `Last-Event-ID: 0` requested old events;
  8,004 frames were sent before closure.
- A `session_update` of 2,174,587 bytes arrived while another live event was queued.
  The runtime evicted the subscriber with `queue_bytes_overflow` against its
  2,097,152-byte live queue budget. The installed runtime permits a single large
  event in an empty queue, so size alone does not always cause eviction.
- Host trace `0HNOL6NJGT7R9:000000E0` ended as `qwen-code-client-evicted` at
  00:52:17. The adapter treats stream eviction as terminal and removes its active
  turn tracking, but only calls native cancellation when the request token is
  cancelled. Native execution therefore continued.
- Trace `0HNOL6NJGT7R8:000000FC` tried the next prompt at 00:54:16. The daemon
  repeatedly reported `prompt_queue_full`, with limit 1 and pending count 1.
  The Host ended with `qwen-code-prompt-queue-timeout` at 00:59:40.
- The original accepted native prompt only completed at 01:11:47. This was an
  occupied native session, not evidence that the model was still loading.

The second attempt also opened SSE before waiting for prompt admission and did
not consume it during that wait. Logs show another queue eviction and a roughly
297-second drain wait. This is an additional integration weakness, not the first
failure's cause.

## Scoped correction and remaining recovery work

New-turn subscription now omits the resume cursor. The installed Qwen event bus
subscribes to future events when that header is absent; registration occurs before
prompt submission. The adapter already discarded events before the accepted
prompt baseline, so asking for the previous ring added cost without visible value.
This correction affects only Qwen transport, with no prompt/context, approval,
timeout or other harness changes. It reduces avoidable backpressure; it does not
guarantee recovery from every stream eviction.

The follow-up implementation is documented in [chat-reconnection.md](chat-reconnection.md).
The original investigation below records the remaining full-gap reconciliation boundary.

A complete recovery should keep tracking the accepted native prompt and reconnect
its event stream with a verified cursor. Replay gaps must be reconciled using the
runtime's session/transcript contract before claiming terminal success. Admission
waiting should also drain or avoid accumulating events. Do not resend an accepted
prompt, create a competing session, or treat stream disconnection as proof that
native work stopped. The follow-up implements bounded cursor reconnection and explicit Stop, tested
for acceptance, replay gaps and duplicate effects. Full transcript reconciliation
after the native replay ring loses required events remains outside that change.
Increasing the busy timeout alone would leave the underlying tracking loss intact.

No matching Codex trace was available; the user agreed to restrict investigation
to Qwen. No native runtime package was edited and no real inference was started.

## Changed files and validation

- `AgenticRouter.Api/Controllers/SessionsController.cs`: event-aware presentation pages.
- `AgenticRouter.Api/wwwroot/app.js`: direct, cooperative historical replay and switch guards.
- `AgenticRouter.Api/Execution/QwenCodeHarness.cs`: live-only new-turn subscription.
- `tests/AgenticRouter.EndToEndTests/ExecutionStateEndToEndTests.cs`: 30,000-event
  browser replay, pagination, exact Thinking preservation and export coverage.
- `tests/AgenticRouter.EndToEndTests/BenchmarkAndHarnessEndToEndTests.cs`: reused
  Qwen native session subscription coverage.
- `tests/FakeQwenCodeServer/Program.cs`: record the external SSE resume header.
- `docs/session-history-presentation.md` and this investigation.

Validation on 2026-09-18: **27/27 E2E passed, zero skipped**. This includes Qwen
adapter cases, initial history and See more, continuation/editing, Thinking order,
status stickiness and terminal copies. An earlier focused UI run also passed 6/6.
All providers were simulated at their external boundaries. These results validate
the scoped changes; they do not claim automatic recovery from real native eviction.

Commands:

```powershell
dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false -p:BaseOutputPath=bin/history-responsive/
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include AgenticRouter.Api/Controllers/SessionsController.cs AgenticRouter.Api/Execution/QwenCodeHarness.cs tests/AgenticRouter.EndToEndTests/ExecutionStateEndToEndTests.cs tests/AgenticRouter.EndToEndTests/BenchmarkAndHarnessEndToEndTests.cs tests/FakeQwenCodeServer/Program.cs
dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-build --no-restore -m:1 -p:BaseOutputPath=bin/history-responsive/ --filter 'FullyQualifiedName~QwenCode|FullyQualifiedName~LargeHistoryPagesByEventCountAndReplaysWithoutBlockingTheBrowser|FullyQualifiedName~LosslessTranscriptRestoresOriginalTimelineThroughSeeMoreAndSurvivesContinuation|FullyQualifiedName~NewExecuteHistoryRestoresTheCompleteVisibleTimeline|FullyQualifiedName~ExecutionStatusSticksWhileRunningAndClosesWithOneStaticCopy|FullyQualifiedName~OllamaThinkingStreamsSeparatelyFromAssistantAnswer|FullyQualifiedName~ExecuteThinkingStreamsInChronologicalBlocksBetweenActions|FullyQualifiedName~CodexHarnessPreservesChronologicalThinkingAndResponseItems' --logger 'trx;LogFileName=history-responsive-final.trx'
node --check AgenticRouter.Api/wwwroot/app.js
git diff --check
```

The test API and fake harness environment overrides pointed to the isolated
`bin/history-responsive/Release/net10.0/` outputs. Release build completed with
zero warnings/errors; formatting, JavaScript syntax and diff checks passed.
The user's running AR process was preserved. Test processes exited after validation.

## Follow-up: visible conversation loading and lighter DOM restoration

Opening a saved conversation now paints a loader over only the chat message region
before requesting or rendering its history. The Composer remains visible and disabled
by the existing conversation-transition state. The loader also covers read-only history
opened while another response continues.

Historical messages are rendered in bounded browser-task slices in addition to the
existing per-timeline yielding. Automatic scroll following is paused while the DOM is
rebuilt and one final scroll is applied after completion, avoiding repeated layout and
scroll work for every restored message. Session and Git sidebar refreshes then run in
parallel after the visible conversation is ready. No persisted content, timeline event,
reasoning block, action, or operational state is omitted.

The focused loader test delays the session response at the HTTP boundary, verifies that
the loader is painted inside the exact message-region bounds, checks `aria-busy` and the
disabled Composer, and confirms the restored content before the loader closes. That test
and the existing large 30,000-event, complete-presentation, and cross-workspace read-only
regressions passed 4/4.
