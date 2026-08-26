# PLAN v55: Codex post-turn workspace observation recovery

## Goal

Prevent a transient workspace-read race after a successful Codex turn from
being surfaced as a generic application failure, while preserving Host effect
authority and refusing to claim success when observation remains unavailable.

## Evidence

- Trace `0HNO33FI5R55D:0000C919` reached Codex `task_complete`; all eight plan
  steps and the final answer were completed.
- The execution session was marked failed roughly 450 ms later, before
  `harness.codex-effects-observed` or automatic validation started.
- The incident journal in the running old binary saturated at 500 reasoning
  events, but the current source already excludes reasoning deltas and has
  deterministic coverage for terminal-event retention.

## Ordered work

1. Route every external-harness workspace observation through one helper.
2. Retry exactly once after a short asynchronous delay only for transient
   `IOException` or `UnauthorizedAccessException` failures.
3. Preserve boundary/security `HarnessException` failures unchanged.
4. Convert any final observation failure into a typed, recoverable harness
   diagnostic with the original exception retained as the logged cause.
5. Add a fake-Codex E2E that completes while a changed file is temporarily
   locked, proving the bounded Host retry completes and observes the effect.
6. Update release documentation and run isolated Release build, focused and
   full deterministic E2E, formatting, diff inspection, and process cleanup.

## Validation boundary

- Do not run a real model/GPU workload or restart the user's active Visual
  Studio process without explicit authorization.
