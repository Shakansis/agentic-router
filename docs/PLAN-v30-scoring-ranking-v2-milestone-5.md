# PLAN v30: Scoring and Ranking v2 Milestone 5

## Goal

Let the user apply validated scoring weights to current or historical Basic CRUD
benchmark evidence and immediately obtain deterministic per-test scores,
per-harness aggregates, and ranking without executing inference or rewriting the
persisted run. Preserve the Milestone 3/4 weights and ranking behavior as the
versioned Default profile, and persist one local Custom profile.

## Current evidence and constraints

- Milestone 3 persists raw Host measurements beside scores calculated with
  objective success 35, correctness 25, terminality 15, workspace accuracy 20,
  and efficiency 5. Those weights and scoring semantics are the Default v1
  compatibility contract.
- Milestone 4 consumes the same final result shape in its live dashboard and
  persists one authoritative result. Rescoring must remain a read-only operation
  over that result and must not affect live execution or transient progress.
- Existing persisted JSON must remain readable byte-for-byte without migration
  or rewrite. The original score and weights remain in that JSON; a rescored
  response is an explicit calculated projection.
- Only Default and Custom profiles are defined. Additional presets would require
  product-selected values and are outside this milestone.
- Automated validation uses deterministic fake provider/harness boundaries. Do
  not run Ollama generation, GPU work, cloud providers, or real harness prompts.

## Implementation

1. Version the Default scoring profile and add contracts that distinguish the
   active profile, original persisted scoring, and a calculated rescored result.
2. Extend the scorer to accept explicit validated weights while retaining the
   exact existing component formulas and default output. Validate finite integer
   weights in a bounded non-negative range and require a positive total;
   normalize by the actual total so non-100 profiles are deterministic.
3. Build suite rescoring exclusively from persisted raw results. Recalculate
   every available test, aggregate over the suite's declared test count so fewer
   completed tests are not rewarded, and apply stable ordinal harness tie-breaks
   after score, passes, and measured duration.
4. Persist one active local profile atomically outside benchmark-result files.
   Default resets to versioned built-in values; Custom survives application
   restart. Invalid profile data falls back to Default without partially applying
   it.
5. Add profile read/update/reset and historical rescore API endpoints. Rescoring
   accepts the active persisted profile, performs no model execution, and never
   saves the returned projection into benchmark evidence.
6. Extend the existing benchmark modal with a compact Default/Custom weight
   editor, total/normalization state, reset, active-profile label, and explicit
   Measured evidence versus Calculated score presentation. Apply valid edits to
   the selected persisted/final result immediately and show harness/test score
   component breakdowns.
7. Add deterministic browser/API E2E coverage for default compatibility,
   changed weights/rankings, zero/negative rejection, reset and restart
   persistence, raw-file immutability, no inference during rescore, historical
   and legacy JSON, incomplete/missing metrics, bounded scores, and stable ties.

## Validation

1. Run focused Benchmark Lab M3-M5 API/UI E2E tests.
2. Run the complete deterministic Playwright E2E suite.
3. Run `dotnet build AgenticRouter.slnx -c Release --no-restore`; if the user's
   running API locks output, use an isolated output without stopping it and
   report the normal gate limitation.
4. Run `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` and
   `git diff --check`, inspect the intended diff, confirm benchmark workspaces
   are clean, and verify pre-existing persisted result files are unchanged.
5. Do not run the manual Native/Codex/OpenCode benchmark. Hand off the exact
   requested steps and stop.

## Milestone boundary

Do not add scoring presets with invented values, named-profile management,
cloud sync, sharing, automatic optimization, suites, scenarios, harnesses,
matrix execution, routing, recommendations, or Milestone 6 work.

## Completion evidence

- Deterministic Benchmark Lab M0-M5 regressions: 10/10 passed.
- Complete deterministic Playwright suite: 277/277 passed. The two focused M5
  API/UI tests also passed again on the final isolated build.
- Release solution build in isolated artifacts output: zero warnings and zero
  errors. The exact normal-output build was attempted but remained blocked by
  the user's running Release API process; that process was not stopped.
- `dotnet format --verify-no-changes` and `git diff --check` passed.
- The two repository benchmark result files remained unchanged at SHA-256
  `DE8B991C489CFFD78624C39FFBADF9AD42FF5E2D4B7C9C57BA9130E79D32E2E5` and
  `B4AE58D4393EC68C289B7BC70157A954B28C56F797DED8E274F30AA05B9D576F`.
- No real model, GPU, cloud provider, Native, Codex, or OpenCode benchmark was
  run during implementation validation.
