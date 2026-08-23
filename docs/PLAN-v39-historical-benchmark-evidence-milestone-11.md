# PLAN v39: Historical Benchmark Evidence — Milestone 11

## Goal

Extend the existing benchmark persistence and UI with immutable historical
evidence, environment snapshots, explicit comparability, historical deltas,
and non-mutating current-profile rescoring. Reuse the Milestone 10 execution,
validation, scoring, and matrix architecture.

## Invariants

- A completed persisted result is immutable. Loading, comparing, and rescoring
  must not rewrite its file or alter its original scores or evidence.
- Capture one environment snapshot per suite or matrix run and reference it
  from all cells. Optional metadata collection failures do not block execution.
- Evidence fields distinguish measured, detected, and unavailable values.
- Missing fields in M3-M10 records remain unavailable; never infer historical
  facts that were not captured.
- Comparability is explicit: comparable, partially comparable, or not directly
  comparable, with stable reasons derived from evidence and configuration.
- Regression signals are emitted only for directly comparable evidence and
  preserve the underlying measurements.
- Original scores remain distinct from projections made with the current
  scoring profile.
- No real model, GPU, cloud provider, or new benchmark run is required for
  automated validation.

## Ordered implementation

1. Extend persisted contracts with versioned environment evidence, stable run
   summaries, comparison classification, deltas, and regression signals while
   retaining schema v1/v2 readability.
2. Capture model, harness, provider/runtime, Host, OS, CPU, GPU/VRAM, RAM,
   suite/fixture, configuration, and scoring identities once per run where
   available. Record provenance and unavailable optional values explicitly.
3. Extend the result store and controller with filtered history, raw evidence,
   pairwise comparison, and current-profile rescoring endpoints. Keep all
   reads and projections non-mutating.
4. Add deterministic comparability and delta calculation for individual suite
   runs and matrix runs, including version/configuration incompatibilities.
5. Extend the existing benchmark page with compact history filters, two-run
   selection, comparison metadata/deltas, warnings, original raw evidence, and
   clearly separated original/current-profile scores.
6. Add focused browser/API E2E coverage for capture, persistence, backward
   compatibility, history, comparison classes, deltas, non-mutating rescoring,
   matrix history, stable evidence, and absent optional metadata.
7. Run applicable deterministic E2E tests, Release build, format verification,
   JavaScript syntax validation, `git diff --check`, and intended-diff review.

## Scope exclusions

Do not redesign benchmark execution or scoring. Do not add automatic routing,
web recommendations, community sharing, cloud synchronization, automatic
submission or remediation, Goose, or DSH.

## Stop condition

After implementation and deterministic validation, stop at:

`MILESTONE 11 HISTORICAL BENCHMARK EVIDENCE READY FOR MANUAL TEST`

Do not start Milestone 12 without explicit approval.

## Execution record — 2026-08-23

- Persisted benchmark schema v3 adds one run-level environment snapshot,
  model/harness/runtime/Host identities, hardware evidence, observed context
  where available, configuration fingerprint, scoring-profile version, and
  explicit measured/detected/unavailable provenance. Existing schema v1/v2
  records remain readable with absent evidence left unavailable.
- Result files are immutable after creation. History, raw inspection,
  comparison, and current-profile rescoring are read-only projections.
- Comparability is classified as comparable, partially comparable, or not
  directly comparable from suite/fixture versions, shared model/harness
  identity, model digests, harness/runtime versions, hardware, and relevant
  configuration. Regression signals are emitted only for comparable runs.
- The compact benchmark history UI supports model/harness/suite filters,
  baseline/candidate selection, deltas, changed metadata, guarded regression
  signals, original versus current-profile score, and original raw evidence.
- Focused history, matrix, and rescoring E2E tests passed 3/3. The complete
  deterministic suite passed 300/301. The remaining independent Git settings
  test reproducibly expects `Configuração local` while the UI retains the
  successful `Repository initialized on main` status; no Git/settings file is
  in the Milestone 11 diff.
- Release build passed with zero warnings/errors. `dotnet format
  AgenticRouter.slnx --verify-no-changes --no-restore`, JavaScript syntax, and
  `git diff --check` passed. No real model, GPU, or cloud execution was run.
- Manual validation: approved by Rodrigo on 2026-08-23 after the compact
  history layout correction. Milestone 12 may proceed.
