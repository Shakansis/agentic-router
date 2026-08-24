# PLAN v41: Auto Model x Harness Router and UX Consolidation — Milestone 13

## Goal

Add a bounded Auto Model x Harness selection mode to Execute by reusing the
Milestone 12 recommendation engine, then simplify the existing benchmark and
recommendation experience without adding benchmark capabilities outside the
requested multi-suite selection.

## Invariants

- Auto selection happens once before an Execute session starts. It never
  switches or migrates a harness during the active turn and never performs a
  cross-harness retry.
- Recommendation ranking remains owned by
  `BenchmarkRecommendationService`; the router only classifies the task,
  requests the existing ranking, applies current availability, and selects the
  first acceptable local candidate.
- External evidence is disabled for automatic routing. Only an installed local
  Ollama model and an available registered harness may be selected; no cloud,
  model, or harness substitution is silent.
- Manual Model x Harness selection bypasses Auto and preserves current Execute
  behavior.
- The selected route, category, recommendation/profile versions, evidence
  links, confidence, and any availability fallback are visible and persisted
  with the execution review without duplicating benchmark evidence.
- CRUD and Agent Behavior may be selected independently or together in one
  sequential benchmark run. Internal suite, fixture, prompt, validator, and
  acceptance versions remain persisted for reproducibility but are secondary
  UI metadata.
- Clicking Run Benchmark is the explicit authorization for that requested
  benchmark. Existing Host approval/security rules remain authoritative.
- Historical schema compatibility and raw evidence inspectability remain
  intact.

## Ordered implementation

1. Add versioned Auto routing contracts and a deterministic task-category
   classifier over the existing Milestone 12 category vocabulary.
2. Add an availability-aware routing service that calls the existing
   recommendation service with local evidence only and returns a selected,
   fallback, or insufficient-evidence result.
3. Resolve Auto once at the beginning of Execute, propagate the exact selected
   model and harness through the current `IAgentHarness` path, emit compact
   route evidence, and persist it in the execution session/review.
4. Add the Auto Model x Harness composer option while keeping manual selection
   explicit and unchanged.
5. Extend benchmark requests/results with backward-compatible selected-suite
   metadata and execute the union of current CRUD/Behavior tests sequentially.
6. Consolidate the benchmark page around models, harnesses, tests, Run,
   current progress, result, and general recommendation. Move history,
   comparison, detailed scoring/profile controls, external research, and
   metadata into Advanced.
7. Fix persisted-result selection so it fetches, renders, rescoring-projects,
   updates recommendation context, and reports failure visibly.
8. Align benchmark controls with the composer visual language, normalize
   harness name/version presentation, add compact terminology help, and repair
   responsive density.
9. Add deterministic browser/API E2E coverage for routing determinism,
   category/profile effects, availability fallback, insufficiency, exact
   propagation, no cloud/mid-turn switching, manual bypass, persistence,
   multi-suite runs, and consolidated UI behavior.
10. Run applicable E2E tests, Release build, format verification, JavaScript
    syntax validation, `git diff --check`, and intended-diff review. Update the
    roadmap only after validation evidence is known.

## Scope exclusions

Do not add continuous routing, cross-harness recovery, session migration,
community/shared benchmarks, uploads, Goose, DSH, automatic benchmarking,
automatic installs/downloads, or unrelated application redesign/refactoring.

## Stop condition

After implementation and deterministic validation, stop at:

`MILESTONE 13 ROUTING AND UX CONSOLIDATION READY FOR MANUAL TEST`

Do not begin shared/community benchmark functionality.

## Execution record — 2026-08-23

- Added deterministic `auto-model-harness-router-v1` task classification over
  the existing M12 category vocabulary. The router calls
  `BenchmarkRecommendationService` with the active profile and local evidence
  only; no second ranking engine or external research path was added.
- Auto filters ranked candidates by exact installed local Ollama model,
  registered harness provider support, and current harness availability. It
  discloses availability fallback and returns an insufficient-evidence choice
  request when no acceptable executable candidate exists.
- The route is retained for the active conversation/task session and executes
  through the existing `IHarnessRegistry` -> `IAgentHarness` boundary. Manual
  selections preserve the previous path and contain no Auto routing evidence.
- Execution reviews persist the task category, router/recommendation/profile
  versions, exact selected Model x Harness, confidence, reason, fallback, and
  supporting benchmark run IDs without copying raw benchmark evidence.
- Benchmark schema v4 adds optional selected-suite identities while retaining
  the legacy single-suite request/result fields. CRUD, Agent Behavior, or their
  deterministic union run sequentially in one existing benchmark pipeline.
- Consolidated the browser experience around models, harnesses, test groups,
  Run, live state, ranking, and the general recommendation. Historical runs,
  comparison, profile weights, category-specific controls, external research,
  and raw evidence remain available behind Advanced or drill-down surfaces.
- Removed the separate model-execution authorization checkbox. Clicking Run
  Benchmark now supplies the existing explicit execution permission.
- Persisted-result selection now fetches, renders, rescores, synchronizes the
  selected test groups, refreshes the general recommendation, and emits a
  visible status instead of silently doing nothing.
- Focused M13/M11/M12/UI coverage passed 7/7. The complete deterministic E2E
  suite passed 304/304. Release build passed with zero warnings/errors; format
  verification, JavaScript syntax validation, and `git diff --check` passed.
  No real model, GPU, cloud provider, benchmark, or harness execution was run.

## Manual UX follow-up — 2026-08-23

- Keep benchmark selection behavior and payloads unchanged while replacing the
  visible multi-select and oversized native checkboxes with compact scrollable
  Model, Tests, and Harness rows using small on/off switches.
- Replace benchmark pseudo-element tooltips with a viewport-clamped floating
  tooltip so help never renders outside the window or on top of its trigger.
- Limit this follow-up to benchmark HTML, CSS, JavaScript, and deterministic UI
  coverage. Do not change routing, benchmark execution, scoring, persistence,
  or Host policy.
- Manual review found the Harness fieldset outside the selection-grid scope.
  Apply the switch-row styling through the full benchmark-controls boundary and
  verify compact switch geometry plus stacked harness identity/version text in
  browser-driven coverage.
