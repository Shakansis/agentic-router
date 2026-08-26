# PLAN v53: bounded Codex stream timeout and diagnostics

## Goal

Prevent an idle Codex/Ollama stream from leaving an Agentic Router request active beyond the configured generation timeout, and replace generic turn failures with an actionable typed cause.

## Evidence

- Trace `0HNO33FI5R55D:0000129B` emitted 442 reasoning deltas, then no further Host-visible activity.
- Codex retried the disconnected Ollama SSE stream five times and terminated after 6,294,975 ms.
- The saved `runtime.generationTimeoutSeconds` was 300, but the Codex adapter did not apply it while waiting for App Server events.

## Ordered work

1. Resolve the current generation timeout from settings for every Codex turn.
2. Interrupt and terminate the turn with a typed timeout when no App Server event arrives within that interval.
3. Classify native Codex idle/disconnect terminal errors instead of returning `codex-turn-failed`.
4. Add deterministic fake-Codex coverage for both Host timeout and native upstream failure.
5. Run formatting, isolated Release build, focused E2E, diff checks, and process cleanup.
