# Reattaching browser and Qwen event streams

## Authorized behavior

Refreshing or closing the browser detaches its subscriber. Once accepted by the
Host, the same request continues while the Host process remains alive. Reopening
the conversation attaches to that request; it never submits its prompt again.
Stop remains an explicit cancellation of both the request lifetime and its exact Host execution session, including a pending approval. Existing approvals retain their original
execution and browser identity, and are neither auto-approved nor regenerated.

This is separate from restarting Agentic Router. Direct/native process recovery
after Host restart is not added; existing durable-supervision restart policy stays
in force. With history disabled, attachment state exists only in memory; no prompt
or tool content is added to localStorage. The browser stores only run, conversation
and workspace identifiers for its latest attachment.

## Implementation and impact

- UI chat submissions opt into Host-owned lifetime using `chatRunId`. Legacy API
  callers that omit it retain HTTP-cancellation semantics. Routing and all harness
  invocations stay in the existing pipeline, without new model calls.
- `LiveChatRuns` owns cancellation and a sequence-indexed event journal. The original
  ASP.NET request scope stays alive until the producer finishes; its subscriber
  uses the HTTP token independently. GET attachments cannot trigger execution.
  Duplicate IDs and competing active requests for the same conversation return
  409 before prompt admission. Completed journals are evicted on later admission,
  retaining at most 16 completed runs for short-term recovery; saved transcripts
  remain under the existing history policy.
- Session opening recognizes a surviving run instead of marking it interrupted.
  A partial timeline is restored from the live journal exactly once. The original
  browser identity is reused for approvals and steering. Selecting the already
  attached conversation preserves the live view.
- Browser startup reopens the latest known run. A dropped stream or one that
  delivers no incoming data for 75 seconds retries its GET attachment up to three times
  using the last processed sequence. The retry count resets after a delivered
  event. Consecutive text
  deltas carry the last merged sequence through the existing presentation writer.
  Replay yields to the browser and completed/error/cancelled events remain singular.
- A new turn waits up to 30 seconds for cleanup of an earlier run that has already
  published its terminal event. If admission still returns HTTP 409, the browser
  restores a manually submitted draft or returns a buffered prompt to the front
  of the queue and pauses it. A rejected optimistic turn is removed from the
  editable conversation so it cannot be mistaken for saved history.
- A new Qwen turn still subscribes without replaying old history. Within an accepted
  turn, EOF, transport failure, client eviction or stream error can reconnect up
  to three times, with the original client/session/prompt and last delivered event
  cursor. The configured request timeout bounds connection recovery. Duplicate
  event IDs are ignored; the accepted POST is never replayed.
- If Qwen reports a replay gap, dies, or exhausts recovery, the Host does not claim
  success from incomplete evidence. An unsettled accepted native turn receives a
  bounded cancellation attempt before its tracking is released, avoiding the
  previously untracked background prompt. Cancellation failure is logged explicitly.
  Full transcript reconciliation after native replay-ring loss is not implemented.

The browser lifetime change is shared by Native, Qwen, Codex, Claude, OpenCode and
supervised routing. The native transport recovery is confined to Qwen, whose
protocol and incident were verified. Other harness transport policies are unchanged.
The E2E fixture explicitly cancels and settles its own surviving Host runs before
resetting settings/workspaces; page teardown is no longer an implicit Stop.

## Validation

Deterministic browser/API coverage exercises refresh, closing/reopening a page,
original approval authority, explicit Stop, duplicate admission, native Qwen
reconnection with a duplicate cursor event, and a typed failure on a replay gap.
Native fake-provider fixtures record prompt admission to detect repeat execution.
No real inference, runtime-package patch or user-process restart is required.

## Files changed

- `AgenticRouter.Api/Chat/LiveChatRuns.cs`
- `AgenticRouter.Api/Contracts/ApplicationContracts.cs`
- `AgenticRouter.Api/Controllers/ChatController.cs`
- `AgenticRouter.Api/Controllers/SessionsController.cs`
- `AgenticRouter.Api/Controllers/SsePresentationWriter.cs`
- `AgenticRouter.Api/Execution/QwenCodeHarness.cs`
- `AgenticRouter.Api/Program.cs`
- `AgenticRouter.Api/Sessions/PersistentSessionService.cs`
- `AgenticRouter.Api/wwwroot/app.js`
- `tests/AgenticRouter.EndToEndTests/BenchmarkAndHarnessEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/ExecutionStateEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/TestEnvironment.cs`
- `tests/FakeQwenCodeServer/Program.cs`
- `docs/history-loading-and-qwen-busy.md`
- This document.

## Validation result

2026-09-18: **41/41 browser/API E2E passed, zero skipped** in the final run,
including the existing supervised reload and pending-approval tests, direct reload,
page closure, history disabled, Stop while waiting for approval, editing, Thinking,
status/footer, large history, and the Qwen adapter regression cases. The initial
runs exposed selecting an already attached conversation and cancellation of a
pending Host approval; both were corrected without weakening assertions/timeouts.

Commands used:

```powershell
dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false -p:BaseOutputPath=bin/chat-reconnect/
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include <changed C# files listed above>
dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-build --no-restore -m:1 -p:BaseOutputPath=bin/chat-reconnect/ --filter 'FullyQualifiedName~QwenCode|FullyQualifiedName~ActiveChatSurvivesBrowserLoss|FullyQualifiedName~DirectApprovalRetainsItsAuthority|FullyQualifiedName~BrowserReloadReattachesDurableSupervision|FullyQualifiedName~BrowserReloadPreservesPendingApproval|FullyQualifiedName~ExplicitCancelTerminatesAutonomousCheckpoint|FullyQualifiedName~OllamaThinkingStreamsSeparatelyFromAssistantAnswer|FullyQualifiedName~ExecuteThinkingStreamsInChronologicalBlocksBetweenActions|FullyQualifiedName~CodexHarnessPreservesChronologicalThinkingAndResponseItems|FullyQualifiedName~CodexHarnessCancellationInterruptsTheActiveTurn|FullyQualifiedName~ActiveTurnBlocksNewConversationUntilCancelled|FullyQualifiedName~RestoredPendingCommandApprovalIsVisibleButExpired|FullyQualifiedName~EditingUserMessageReplacesTurnAndTruncatesLaterContext|FullyQualifiedName~NewExecuteHistoryRestoresTheCompleteVisibleTimeline|FullyQualifiedName~LargeHistoryPagesByEventCountAndReplaysWithoutBlockingTheBrowser|FullyQualifiedName~ExecutionStatusSticksWhileRunningAndClosesWithOneStaticCopy' --logger 'trx;LogFileName=chat-reconnect-final.trx'
node --check AgenticRouter.Api/wwwroot/app.js
git diff --check
```

API and fake harness path environment overrides used `bin/chat-reconnect/Release/net10.0/`.
Release build: zero warnings/errors. Formatting, JavaScript syntax and diff checks
passed. Providers were simulated at external boundaries; no real inference ran.
The user's running AR process was preserved; the final test processes exited.
