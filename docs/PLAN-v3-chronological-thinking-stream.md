# PLAN v3 — Chronological Thinking stream

## Objective

Render model-provided Thinking as live chronological blocks interleaved with visible Execute actions instead of one turn-wide panel populated after planning completes.

## Ordered implementation

1. Stream native Ollama tool-call responses and forward each `message.thinking` delta before the tool-call result is complete.
2. Bridge planning deltas into the ordered chat SSE stream without weakening Host validation or action authority.
3. Replace the single reasoning panel with immutable timeline blocks. Close the active block when a visible action begins; create a new block when later reasoning resumes.
4. Keep completed Thinking blocks in their original DOM position, collapsed after their associated phase, while the active block remains open and updates incrementally.
5. Add deterministic E2E coverage proving a Thinking fragment is visible before its action and proving `Thinking → action → Thinking → action` DOM order.
6. Validate formatting, Release build, focused E2E, complete E2E where practical, and desktop/narrow browser behavior without real model inference.

## Boundaries

- Only actual provider reasoning is shown as Thinking; Host telemetry remains technical activity.
- Non-streaming providers do not receive a fabricated writeout animation.
- Action validation, approvals, execution, persistence, and terminal truth remain Host-owned.
- No real Ollama/GPU or cloud request is authorized by this plan.

## Validation status

- Release build: passed with zero warnings and zero errors.
- Focused deterministic E2E: 5/5 passed, including a live assertion before the tool-call terminal chunk and final `Thinking → action → Thinking → action` order.
- Browser inspection: desktop and 420 px passed with no horizontal overflow or console warning/error; the temporary preview was removed.
- Changed C# files format clean except for the four pre-existing whitespace findings in `ChatStreamService.cs` around the existing terminal-answer block.
- Full E2E was attempted and stopped after broader non-terminal Execute scenarios accumulated. No full-suite passing claim is made.
- Real Ollama/GPU and cloud validation were not run.
