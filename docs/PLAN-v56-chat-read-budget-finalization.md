# PLAN v56: Chat read-budget finalization

## Goal

Prevent a legitimate Chat workspace inspection from becoming a terminal
application failure when the bounded read loop reaches its safety limit.

## Evidence

- Trace `0HNO3EJ445JF3:00000175` completed eight trusted-workspace reads and the
  selected model requested a ninth.
- The Host rejected that ninth request by setting `chat-read-budget` as a
  terminal failure before giving the model an opportunity to answer from the
  evidence already collected.
- The fixed limit and terminal behavior were introduced with the Chat read-only
  workspace path, independently of Codex context alignment and compaction.

## Ordered work

1. Preserve the eight-read safety bound and all trusted-workspace validation.
2. Reject the over-budget call as an authoritative tool result instead of a
   terminal request failure.
3. Remove read tools for one final bounded provider turn, omitting empty tool
   declarations from local and cloud provider payloads, and require a
   user-facing completion based only on the evidence already collected.
4. Keep a typed terminal error only if the model violates the finalization
   contract or still returns no answer.
5. Add deterministic browser E2E coverage reproducing eight completed reads, a
   ninth request, and a successful final response without approval.
6. Update release notes and run formatting, Release build, focused/full E2E,
   intended-diff inspection, and process cleanup.

## Empty-response finalization (authorized 2026-10-04)

- Trace `0HNP1PMTUEVQ0:00000317` completed six workspace reads, then returned
  reasoning without a tool call or a final answer. The eight-read limit was not
  reached.
- A response with no tool call and no nonblank answer now closes the tool phase
  and requests one final synthesis using the evidence already collected. Keep
  the selected model, inference profile, and authoritative tool results.
- Emit `chat.read-only-empty-response-finalization` before that additional call.
  Do not expose reasoning as the answer. If finalization is empty, fails, or
  requests another tool, retain the existing typed terminal failure. Budget
  finalization also remains limited to one attempt; it cannot trigger a second
  recovery.
- Browser/API regression coverage includes successful synthesis, another empty
  response, and a tool request after tools have been closed.
- Validation: isolated Release build passed with zero warnings/errors; formatting
  verification and diff checks passed; focused fake-provider browser/API E2E
  passed 7/7. No real-model validation or user application restart was performed.

## Validation boundary

- Do not run a real model/GPU workload or restart the user's active application
  process without explicit authorization.

## Original v56 completion evidence

- Formatting verification and isolated Release build passed with zero warnings
  and zero errors.
- Focused Chat read, budget-finalization, and mutation-boundary E2E passed 3/3.
- The complete deterministic browser/API suite passed 323/323 with zero skips.
- No real model, GPU workload, cloud request, or application restart was used.
- All v56 build/test artifacts were removed; no test-owned process remained.
