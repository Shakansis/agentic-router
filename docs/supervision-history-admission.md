# Supervision history admission recovery

## Incident and scope

The incident `0HNOKV98UARHN:00000026` failed in `supervision-prepare` with
`supervision-history-too-large`, before any harness turn. Its saved conversation
contained a 301,307-character persisted continuity summary with eight V1 markers.
The admission guard rejected more than 100 messages or 262,144 content characters,
so neither normal model context fitting nor the reactive harness failsafe ran.

This correction operates in the Host. It does not modify a harness, call a model
to summarize, change the storage byte thresholds, or rewrite the visible history.

## Behavior

- Prepare and manual resume use the shared token estimator. Their history budget
  is the configured default context minus the reserved response and current
  objective. This bounds live Host input; it is not a claim to measure the complete
  native prompt. Existing per-role/model envelope checks remain authoritative.
- History already within that budget is retained unchanged. More than 100 short
  messages no longer produces an unrelated admission error.
- When history exceeds the budget, the existing persistence compactor builds a
  deterministic inference projection. User requirements and parsed requirements
  from legacy summaries remain intact (apart from whitespace normalization and
  exact deduplication), including more than 64 distinct requirements. Optional
  historical observations use bounded excerpts/entry counts and an omission note.
  The objective is unchanged. Required content that cannot fit produces an explicit
  failure, before inference, rather than silently dropping constraints.
- Prepare/resume activity records estimated before/after tokens and the history
  budget. No raw historical content is added to diagnostics.
- The conversation builder refits an oversized continuity document against the
  actual history allowance for each role/model, rather than omitting the whole
  document as an old assistant message. An irreducible requirements document fails
  explicitly. Ordinary conversations retain their existing turn-selection policy.
- Projected history retains its source conversation version, so reducing many
  messages into one summary cannot rewind native hydration cursors and cause replay.
- Persisted compaction reads prior V1 sections into the existing deduplicated
  sections, rather than embedding complete earlier summaries recursively. Existing
  per-section storage bounds remain; when those bounds are reached, newer distinct
  entries replace older entries. Inference projection does not apply that entry
  limit to requirements. Literal indented continuation lines remain entry content.
- The lossless transcript and See more storage path remain unchanged. Previously
  truncated legacy content cannot be reconstructed by this correction.

## Changed files

- `AgenticRouter.Api/Supervision/DurableSupervisionRunCoordinator.cs`
- `AgenticRouter.Api/Sessions/PersistentSessionCompactor.cs`
- `AgenticRouter.Api/Chat/ConversationContextBuilder.cs`
- `tests/AgenticRouter.EndToEndTests/DurableSupervisionEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/ExecutionStateEndToEndTests.cs`
- This document.

## Validation

New browser/API coverage exercises prepare and resume after a test-API restart,
with a nested summary above 300,000 characters and with 120 short messages. It
checks completed execution, preserved requirements/file facts/unresolved work,
one summary marker, explicit compaction activity, and no summarizing inference.
A separate case rejects irreducible mandatory input before inference. The storage
lifecycle test includes nested legacy sections and checks flattening/deduplication.
An additional same-native-session test reduces previously large observations and
checks that canonical history is not hydrated again after projection.

Regression coverage includes lossless transcript restoration, Native context
compaction, cross-harness continuity, supervised Qwen recovery, and the 17 reactive
context failsafe cases. All providers/harnesses are faked only at external boundaries.

Commands use isolated `bin/context-failsafe/` output, preserving the running user
application:

```text
dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false -p:BaseOutputPath=bin/context-failsafe/
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include <five changed C# files>
dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-build --no-restore -m:1 -p:BaseOutputPath=bin/context-failsafe/ --filter <selected cases> --logger "trx;LogFileName=supervision-history-admission-final.trx"
git diff --check
```

The test API and four fake-harness path environment variables point to the same
isolated output. Live model inference is not part of this validation.

Final result (2026-09-17): **41/41 E2E passed, zero skipped**; Release build with
zero warnings/errors; formatting verification and intended-diff/whitespace checks
passed. Results are recorded in
`tests/AgenticRouter.EndToEndTests/TestResults/supervision-history-admission-final.trx`.
The running user application was preserved; activating the correction requires
building/restarting that application separately. No test API or fake-harness
process was left running after validation.
