# PLAN v35: Dedicated Benchmark Workspace

## Goal

Replace the Benchmark CRUD modal with a dedicated main application view based
on the original benchmark modal visual spike (removed after the production UI
superseded it). Keep the existing benchmark behavior and
provide an explicit route back to the conversation view.

## Smallest complete change

1. Keep the application sidebar and add a second `<main>` beside the existing
   conversation `<main>`; exactly one main view is visible at a time.
2. Reuse every existing benchmark control, identifier, API call, live event,
   scoring, history, result, and evidence contract.
3. Use the reference layout: fixed configuration panel on the left, results
   panel on the right, ranking weights above the result surface, and bounded
   internal scrolling.
4. Replace modal open/close behavior with view navigation and an explicit
   `Voltar à conversa` button. Leaving the page must not cancel an active run.
5. Preserve keyboard focus, responsive stacking, and the running benchmark's
   reconnectable state.

## Validation

1. Extend browser E2E to prove main-view navigation, benchmark execution,
   leaving/reopening during a live run, cancellation, history, and evidence.
2. Run focused benchmark UI coverage and the complete deterministic E2E suite.
3. Run Release build, formatter verification, `git diff --check`, and intended
   diff inspection.

## Boundary

Presentation and navigation only. Do not change benchmark scheduling, model or
harness selection, execution permission, scoring, persistence, SSE behavior,
Host authority, or backend contracts. Do not run a real model or GPU workload.

## Completion evidence

- Benchmark CRUD now renders in its own `<main>` while the conversation main is
  hidden. `Voltar à conversa` restores the conversation without cancelling or
  disconnecting an active benchmark.
- The reference layout was applied as a bounded setup panel on the left and a
  results panel on the right. Persisted history and ranking weights now occupy
  the results header, while live cards, ranking, table, and evidence share one
  internally scrollable result surface.
- All existing benchmark element identifiers and browser/API contracts were
  retained; no backend contract or execution behavior changed.
- Focused completed-run UI coverage passed 1/1. Focused leave/reopen/live-run
  cancellation coverage passed 1/1.
- The complete deterministic fake-provider browser/API suite passed 278/278,
  zero skipped, in 2m43s.
- Release build passed with zero warnings and zero errors. `dotnet format
  AgenticRouter.slnx --verify-no-changes --no-restore` passed.
- No real model inference, GPU workload, cloud request, download, or service
  restart was performed.
