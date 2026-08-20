# PLAN v13 — Chronological content timeline

## Objective

Render every model-provided Thinking item and assistant response item as a
separate chronological segment. Preserve the exact streamed order across
reasoning, responses, tools, approvals, and Host terminal facts instead of
grouping all reasoning into one panel and all response text into one answer.

## Ordered implementation

1. Preserve Codex App Server item identity when translating reasoning and
   assistant deltas into the browser SSE contract.
2. Provide segment-specific safe HTML for response deltas while retaining the
   existing aggregate response HTML for compatibility and persistence.
3. Treat every change of content kind, item identity, or tool/action boundary
   as the end of the active content segment.
4. Append immutable `thinking` and `response` elements to the existing
   assistant work timeline in event order. Keep exactly one
   `.assistant-answer` marker on the latest response for existing browser
   contracts, copy behavior, diagnostics, and tests.
5. Render Host terminal facts as a final response segment without duplicating
   earlier model response segments.
6. Add deterministic Codex E2E coverage for
   `Thinking -> response -> Thinking -> response`, including multiple deltas
   inside each item and a tool boundary.
7. Validate JavaScript syntax, formatting, Release build, focused Playwright
   coverage, and the complete intended diff without invoking a real model.

## Boundaries

- Stream order is authoritative; the browser does not reorder by event type.
- Only provider/Codex reasoning is shown as Thinking. Host telemetry remains
  technical activity.
- Response segments retain server-rendered sanitized Markdown.
- Host validation, approval, effects, persistence, and terminal truth remain
  unchanged.
- Existing unrelated dirty-worktree changes are preserved.
- No real Ollama/GPU or cloud inference is authorized by this plan.

## Validation status

- JavaScript syntax check: passed.
- Release build: passed with zero warnings and zero errors.
- Focused chronological and Codex E2E: 6/6 passed.
- Full deterministic Playwright E2E: 202/202 passed.
- `dotnet format --verify-no-changes`: passed.
- Real Ollama/GPU and cloud validation were not run.
