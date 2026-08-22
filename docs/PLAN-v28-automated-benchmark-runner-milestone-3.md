# PLAN v28: Automated Benchmark Runner Milestone 3

## Goal

Implement the first usable automated Model x Harness benchmark runner for one
explicit Ollama Local model and any available subset of Native, Codex, and
OpenCode. Run the versioned basic CRUD suite in fresh equivalent disposable
workspaces, determine correctness from Host-owned evidence, persist raw results
separately from explicit scores, and expose a small inspectable results UI.

## Current evidence and constraints

- Milestone 0 already owns disposable benchmark workspaces, deterministic
  snapshots, exact create validation, explicit model-execution permission, and
  the `IHarnessRegistry` -> `IAgentHarness` execution seam.
- Native, Codex, and OpenCode already enter that seam. The runner must supply a
  benchmark-specific Native delegate and the existing external transport
  delegate; it must not add a selector bypass or another harness registry.
- Milestones 1 and 2 have substantial uncommitted Native/Codex/OpenCode
  integration work. Preserve it and make only narrow overlapping edits.
- Automated validation must use deterministic fake provider/harness boundaries.
  Do not run Ollama generation, a GPU benchmark, a cloud provider, or a real
  harness prompt during implementation validation.

## Implementation

1. Version the suite, four tests, fixture, prompts, and acceptance rules. Build
   every test workspace from one canonical fixture and persist its fingerprint.
2. Extend the existing benchmark engine to prevalidate one exact model and the
   selected available Native/Codex/OpenCode adapters, then execute every test
   independently through `IAgentHarness`. Continue after test failure or
   timeout; stop cleanly only on run cancellation.
3. Add a small benchmark-only Native structured-tool loop using the existing
   provider client and canonical filesystem tool names. Expose no shell and
   confine every effect to the disposable workspace.
4. Implement deterministic Host validators for Create, Read, Update, and
   Delete. Capture exactness, useful partial outcome, tool/error evidence when
   available, final harness report, changed/unexpected files, and terminality.
5. Add explicit, replaceable scoring weights for objective success,
   correctness, terminality, workspace accuracy, and efficiency. Keep raw
   measurements and validation evidence independent from calculated scores.
6. Persist completed/cancelled suite results atomically under the existing
   local application data root. Provide list/detail API reads and an explicit
   in-flight cancellation endpoint.
7. Add a compact Benchmark dialog that selects one model, one or more available
   supported harnesses, the fixed CRUD suite, and timeout; show ranking and
   expandable per-test validation evidence.
8. Extend deterministic fake Native/Codex/OpenCode behavior and Playwright E2E
   coverage for selection, isolation/equality, all validators, timeout/failure
   continuation, cancellation, one final state, persistence, scoring, ranking,
   and the shared harness boundary.

## Validation

1. Run focused benchmark E2E coverage through the real browser/API and fake
   external boundaries.
2. Run the complete deterministic Playwright E2E suite.
3. Run `dotnet build AgenticRouter.slnx -c Release --no-restore` with zero
   warnings; use an isolated output only if the user's running API locks normal
   build output, without stopping that process.
4. Run `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` and
   `git diff --check`, then inspect the intended diff and benchmark-run cleanup.
5. Do not execute the requested qwen3.8 real-model manual run. Hand off exact
   Codex + OpenCode steps and stop for the user's explicit approval.

## Milestone boundary

Do not add the remaining eight scenarios, continuity/stale-write/recovery
suites, new harnesses, automatic routing/installation, cloud sharing,
model-specific tuning, or an advanced dashboard.
