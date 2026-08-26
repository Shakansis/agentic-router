# PLAN v54: Codex context alignment and transient continuation

## Goal

Make the Host-resolved context window the single authority for Codex model
metadata, thread configuration, and 98-percent total-context compaction, then
allow one observable continuation turn after an explicitly allowlisted
transient Codex transport failure.

## Evidence

- Codex exposes separate `model_context_window` and
  `model_auto_compact_token_limit` settings; a mismatch can let the Host wait
  beyond the harness/model's effective window.
- Trace `0HNO33FI5R55D:0000129B` failed after Codex reported an upstream SSE idle
  timeout, not because its active context was full.
- App Server supports appending a later `turn/start` to the same started or
  resumed thread, preserving the exact model, provider, workspace, and thread
  history.

## Ordered work

1. Resolve one immutable Codex context configuration per turn and pass it to
   both model-catalog registration and thread start/resume.
2. Validate and prove that catalog context, thread context, and the floor of
   98 percent are identical and use total active-context scope.
3. Add exactly one continuation attempt for
   `codex-event-idle-timeout`, `codex-provider-stream-idle-timeout`,
   `codex-provider-stream-disconnected`, and `codex-app-server-exited`.
4. Build the continuation prompt from the actual code/message plus Host-owned
   objective, completed/pending plan steps, completed actions, and observed
   changed paths; do not duplicate canonical conversation hydration.
5. Keep the retry visible, bounded, and on the exact selected Codex
   model/provider/harness/workspace/approval policy; preserve the original
   terminal failure if the continuation also fails.
6. Add deterministic fake-Codex E2E coverage for context equality, successful
   state-aware continuation, bounded failure, and App Server restart recovery.
7. Update product documentation, run formatting, isolated Release build,
   focused E2E, diff checks, and stop all work-started processes.

## Validation boundary

- Do not run a real local model, GPU workload, cloud provider, or restart the
  user's currently running Agentic Router instance.
- Use only the deterministic fake App Server and isolated test artifacts.
