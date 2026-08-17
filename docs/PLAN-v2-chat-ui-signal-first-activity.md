# PLAN v2: Signal-first Chat and Execute activity

## Goal

Replace the user-facing telemetry timeline with a compact task narrative, one authoritative model-selection sentence, and a deduplicated list of meaningful file mutations. Keep the complete Host activity available behind one collapsed technical-details control.

## Scope

- Preserve the existing streaming, routing, execution, approval, review, undo, and persistence contracts.
- Render exactly one visible model-selection sentence per assistant turn and update it when an explicit fallback changes the active model.
- Consolidate repeated action lifecycle events by Host action ID.
- Show file mutations in a DSH-like action list; hide read/list/planning noise from the primary surface.
- Expand a file action inline to show its workspace path and a bounded first/last-lines preview.
- Make the filename open the existing Host review focused on that file; do not add a filesystem-serving or OS-launch endpoint.
- Keep approvals, recovery decisions, warnings, and failures visible because they require or explain user action.
- Preserve real `message.thinking` returned by Ollama, stream it separately from answer content, and render it in a transient collapsible section. Never reconstruct or label telemetry as model reasoning.

## Ordered implementation

1. Preserve Ollama thinking deltas through the provider, application stream, and UI without mixing them into assistant content.
2. Recompose assistant-turn DOM into model selection, live thinking/status, answer, useful work, technical details, and actions.
3. Add action-ID-based mutation rendering and lazy review-path resolution.
4. Keep raw events in the existing groups under one collapsed technical-details disclosure.
5. Preserve the beginning and end of bounded backend action previews.
6. Update focused Playwright assertions for thinking, routed/manual model text, collapsed telemetry, action deduplication, inline preview, and focused file review.
7. Run formatter verification, Release build, deterministic fake-provider E2E, `git diff --check`, and browser inspection without real model calls.

## Acceptance

- Ordinary routing produces one visible sentence: model X was routed by the agent or selected by the user.
- Repeated proposed/approved/executing/completed events produce one visible action row.
- Read-only discovery and planning remain available only in collapsed technical details.
- Expanding a file action shows a bounded preview with beginning and end plus the absolute workspace path when review data is available.
- The filename is keyboard-accessible and opens the existing review at the corresponding file.
- Real Ollama thinking is visibly separate from the final answer and is not persisted as conversation content.

## Validation boundary

All automated validation uses the deterministic fake-provider browser/API path. Real Ollama/GPU inference and real cloud calls remain out of scope unless separately authorized.

## Implementation status (2026-08-16)

- Ollama `message.thinking` is preserved for ordinary streaming and native tool-call turns, emitted through a dedicated `reasoningDelta`, and rendered separately from assistant content: implemented.
- One routed/manual/fallback model sentence, task narrative, action-ID deduplication, mutation-only primary list, inline first/last preview, lazy absolute path, and focused review link: implemented.
- Raw routing, planning, workspace, provider, validation, and lifecycle telemetry: preserved under one collapsed technical-details disclosure.
- Focused deterministic browser/API validation: 7/7 passed, including normal streamed thinking, tool-call thinking, explicit/automatic model selection, response hierarchy, action expansion, and collapsed technical activity.
- Release build: passed with zero warnings and errors.
- Responsive browser inspection: passed at desktop and 420 px with no action/page overflow and no console warnings or errors.
- Solution formatter: all changed C# files except `ChatStreamService.cs` pass. The solution-wide check continues to report the same four pre-existing whitespace findings in that file, shifted to lines 6229-6232 by this change.
- Full E2E validation did not complete cleanly: `ConformantGroqTargetCoordinatesBeforeFailedResidentIsEvaluated` and `GroqTargetCoordinatesDirectlyWithoutResidentConformanceBridge` remained non-terminal for 20 seconds, and an earlier bounded run also surfaced `StructuredSpecialistRepairsOneSemanticFailureBeforeExecution`. The focused signal-first suite remains green; no passing full-suite claim is made.
- Real Ollama/GPU inference and real cloud calls: not run.
