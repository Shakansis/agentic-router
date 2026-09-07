# Benchmark Lab interface

## Scope

Presentation-only implementation of the reviewed HTML sketch. All interface text
is English. Benchmark requests, suites, execution order, repetitions, cancellation,
scoring, recommendation algorithms, persistence, and Host authority are unchanged.

## Organization

- Configuration uses the existing compact model, harness, and test switches in
  independent disclosure groups, with a calculated selection summary.
- Run progress and cancellation remain above the inspection tabs.
- Tests in progress uses a grouped Model × Harness selector with Previous, Next,
  and View active test controls. Only one combination's tests are mounted at a
  time; the selector supports the complete current matrix.
- Results keeps pair/model/harness rankings, the expandable matrix, score
  weights, scenario details, and original raw evidence.
- Recommendation distinguishes calculated score, confidence, and local evidence.
- History and comparison retains the existing filters and comparison controls.
- The workspace scrolls as one page. On narrow screens, results precede
  configuration without overlapping it.

## Reading stability

Live test disclosure and summary nodes are retained during SSE and timer updates.
Switching combinations retains their detached inspection nodes within the current
matrix; entries absent from a subsequent matrix are removed. Updates do not move
the user's selection or switch tabs. View active test is an explicit navigation
action. Finishing or cancelling a run keeps the live inspection available, with
the persisted result separately accessible in Results.

Result rescoring retains the inspected combination and test disclosure state.
Recommendation refresh retains evidence, alternatives, and trace disclosure state
by stable identities. Static configuration, scoring, matrix, and raw-evidence
disclosures are not reset by information updates.

## Validation

- Release solution build: zero warnings and errors.
- Solution-wide `dotnet format --verify-no-changes --no-restore`: passed.
- `node --check AgenticRouter.Api/wwwroot/app.js`: passed.
- Seven deterministic browser/API E2E tests: passed. Coverage includes a real
  browser with 30 combinations against fake external providers, retained DOM
  identity across timer updates and cancellation, recommendation refresh,
  rescoring, history, sequential repetitions, and desktop/mobile layout.
- No real local model, GPU inference, or cloud-provider validation was performed.
