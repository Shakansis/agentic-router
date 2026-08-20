# PLAN v22: Benchmark Lab Milestone 0

## Goal

Implement the smallest complete headless Benchmark Lab path for one selected
Ollama Local model, one registered harness, and deterministic test
`FS-CREATE-001`. First remove the current harness-selection bifurcation by
putting Native behind the same reusable harness boundary as the experimental
adapters. The Host must execute only inside an engine-owned disposable
workspace, validate final filesystem state independently, return raw JSON
evidence, and clean up on success or failure.

## Evidence

- Native is currently a built-in definition but not an `IAgentHarness`; its
  execution loop remains selected through a special branch in
  `ChatStreamService`. Leaving that branch would create two execution stacks
  exactly where benchmark selection needs one reusable boundary.
- Codex, OpenCode, and Qwen Code already implement `IAgentHarness`. Native can
  join that boundary through a thin adapter over the smallest reusable seam in
  its current services; this does not require changing Native semantics.
- Existing provider discovery exposes the selected local model and digest.
- The deterministic test suite already replaces only external Ollama and
  harness-process boundaries, so the benchmark can be exercised through the
  running API without real inference.
- The checkout contains substantial pre-existing modifications. This work
  must add a narrow vertical slice and preserve all unrelated changes.

## Implementation

1. Add a thin `NativeHarnessAdapter` over the current Native execution seam,
   register it as `IAgentHarness`, and make `ChatStreamService` resolve every
   selected harness through `IHarnessRegistry`. Remove the Native special-case
   selector without rewriting its internal behavior.
2. Add compact benchmark contracts for test definition, run identity,
   harness execution, deterministic validation, raw metrics, and errors.
3. Add an engine-owned disposable workspace factory under a configurable
   benchmark-runs root outside the source repository. Capture safe bounded
   initial and final snapshots without following reparse points, then clean up
   after evidence is materialized.
4. Implement `FS-CREATE-001` with canonical UTF-8 bytes and no trailing
   newline. Keep expected bytes and validator state inside the test definition;
   provide only the task text and workspace context to the harness.
5. Run one adapter resolved from the unified `IHarnessRegistry` and require one
   terminal harness result. Use whichever registered path yields the smallest
   complete spike after Native is behind the boundary; do not introduce a
   benchmark-specific harness pipeline.
6. Resolve the exact selected Ollama Local model and digest through current
   settings/provider discovery. Require an explicit real-model permission bit
   on the headless API request and return one structured JSON result.
7. Add deterministic E2E scenarios through the running API for
   exact output, wrong content/name/directory, missing output, extra file,
   unrelated modification, and unrelated deletion. Assert fixture creation,
   repository/workspace isolation, structured identity, and cleanup.

## Validation

1. Focused E2E coverage proving Native and experimental harness selection use
   the registry plus Benchmark Lab coverage through the running API.
2. `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
3. Zero-warning Release build and the full Playwright E2E suite.
4. `git diff --check` and review of the complete intended Benchmark Lab diff.
5. Do not run a real Ollama model, cloud provider, GPU benchmark, or smoke
   without separate explicit permission.

## Milestone boundary

Do not add UI, persistence/history, scoring weights, rankings, additional
tests, additional harness integrations, telemetry infrastructure, recovery
injection, or Milestone 1 abstractions.

## Completed evidence

- Native is now a registered `IAgentHarness`. `ChatStreamService` resolves
  Native, Codex, OpenCode, and Qwen Code from `IHarnessRegistry` and calls the
  same `ExecuteAsync` boundary; there is no Native selector bypass.
- `FS-CREATE-001` runs through the existing Codex adapter in an engine-owned
  disposable workspace, captures bounded initial/final snapshots, validates
  exact bytes/path/containment, returns structured raw JSON, and cleans the run
  directory on PASS and FAIL.
- Deterministic E2E covers exact output, wrong bytes, wrong filename, wrong
  directory, missing file, unexpected creation, unrelated modification,
  unrelated deletion, fixture preservation, isolation, cleanup, permission,
  and structured identity.
- Focused Benchmark/registry tests passed 3/3. Focused Native/UI regressions
  passed 3/3. The final full deterministic Playwright E2E suite passed 227/227
  with zero skips.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` passed.
  The isolated Release build passed with zero warnings and zero errors. The
  ordinary Release output remained locked by a pre-existing running
  `AgenticRouter.Api` process, which was preserved rather than stopped.
- `git diff --check` passed and the benchmark-runs root was empty after tests.
- No real Ollama generation, GPU inference, cloud request, model download, or
  real benchmark was executed.
