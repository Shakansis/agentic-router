# PLAN v38: Pre-M10 real validation and Model x Harness matrix

## Goal

First prove the current Native, Codex, OpenCode, Qwen Code, and Claude Code
adapters through the real Agentic Router execution boundary with the exact
local `qwen3.8:27b-gpu0` model. Preserve every first-run result and classify
failures without prompt tuning, validator weakening, cloud fallback, or score
manipulation. Implement Milestone 10 only if that real-run gate passes.

## Pre-M10 identity and isolation

- Exact tag: `qwen3.8:27b-gpu0`.
- Record the Ollama digest, quantization, context, runtime parameters, and each
  harness version before execution.
- Run an isolated Release API instance with an isolated data directory and
  disposable workspaces. Do not stop or reuse the user's running API process.
- Run local inference sequentially so concurrent model loads cannot distort
  behavior or timing.
- Preserve raw SSE, API responses, Host-visible final state, API logs, and a
  machine-readable classification for every first attempt. Never overwrite a
  first-run artifact with a retest.

## Real battery

For every active harness, use the same canonical prompts and independently
verify:

1. read-only inspection;
2. exact file creation;
3. exact edit in a continuing multi-turn session;
4. structured deletion;
5. structured `run_process`;
6. trusted-workspace boundary rejection followed by permitted recovery;
7. cancellation and terminal cleanup;
8. complete Basic CRUD v1;
9. complete Agent Behavior v2.

Each harness gets fresh disposable state. Basic CRUD v1 and Agent Behavior v2
keep their existing canonical fixtures, prompts, validators, scoring, and raw
evidence contracts.

## Failure policy and bounded repair

Classify each failure as Agentic Router/integration, harness/runtime,
model behavior, environment, or benchmark validator/test infrastructure. Fix
only Agentic Router/integration or benchmark infrastructure defects. Preserve
the original failure, then run only the affected proof in a numbered repair
cycle. Stop repairing a distinct defect after five diagnose/fix/targeted-retest
cycles and record the unresolved limitation while continuing independent safe
tests.

## Gate

Milestone 10 may start only when all five active adapters have demonstrated
real local inference, the benchmark infrastructure is stable, and no unresolved
Agentic Router defect would invalidate Model x Harness results. Measured model
or harness behavioral failures do not block the gate when they terminate and
their evidence remains valid.

## Milestone 10, conditional on the gate

1. Extend the existing suite request and runner to accept multiple installed
   models and multiple available harnesses and generate the Cartesian cells.
2. Represent every cell as an independent first-class result with exact model
   identity, compatibility state, fresh workspace/session, raw evidence, score,
   duration, and terminal state.
3. Execute local cells sequentially; continue after isolated cell failure,
   timeout, unsupported, or unavailable outcomes.
4. Version persistence so historical Basic CRUD v1 and Agent Behavior v2 runs
   remain readable while matrix selection, identities, execution order, and
   cell results are reproducible.
5. Extend the current benchmark UI/live dashboard with multi-model selection,
   current and remaining cell progress, a readable evidence-linked matrix, and
   pair/model/harness rankings.
6. Recompute every ranking from raw evidence when scoring weights change, with
   deterministic stable tie-breaking and no rerun.
7. Add focused E2E coverage for Cartesian generation, compatibility, isolation,
   exact propagation, sequential execution, continuation, cancellation,
   timeout, progress, persistence, rescoring, rankings, ties, and historical
   compatibility.

## Validation and stop

Run focused and applicable complete deterministic E2E coverage, an isolated
Release build, formatting verification, and `git diff --check`. If the gate
passes and M10 is implemented, run the requested small real matrix sequentially
with the exact gpu0 model and any suitable second installed local model. Then
stop at:

`MILESTONE 10 MODEL × HARNESS MATRIX READY FOR MANUAL TEST`

Do not implement Goose, DSH, cloud benchmarking, automatic routing,
installation, community sharing, web recommendations, automatic weight
optimization, or Milestone 11.

## Execution record — 2026-08-23

- Pre-M10 gate: passed for Native, Codex, OpenCode, Qwen Code, and Claude Code
  with the exact `qwen3.8:27b-gpu0` digest and no cloud fallback.
- Corrected defects: OpenCode cleanup, external filesystem reads/process path
  escapes, Codex Windows permission/tool routing, Qwen filesystem tool routing,
  boundary assessment, and bridge trace normalization.
- M10 matrix: implemented with schema v2 persistence, sequential Cartesian
  execution, compatibility/final cell states, exact model identity, live cell
  progress, inspectable matrix UI, and pair/model/harness rescoring rankings.
- Automated validation: 300/300 E2E passed; isolated Release solution build,
  format verification, JavaScript syntax, and diff checks passed.
- Real validation: 2 models x 5 active harnesses, 10/10 cells persisted, later
  cells continued after failures, workspaces were unique and cleaned, rankings
  were generated and changed through rescoring without rerun.
- Evidence: `docs/validation/PRE-M10-REAL-VALIDATION-REPORT-2026-08-23.md`
  and `docs/validation/M10-MODEL-HARNESS-MATRIX-REPORT-2026-08-23.md`.
- Manual validation: approved by Rodrigo on 2026-08-23. Milestone 11 may
  proceed.
