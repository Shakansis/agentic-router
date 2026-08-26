# PLAN v51 - Host validation and terminal incidents

## Objective

Prevent automatic Host validation from being rejected by specialist plan-step
binding, and preserve terminal incident evidence during long reasoning streams.

## Observed failures

- Trace `0HNO33FI5R55D:00000709` accepted a Host-owned plan, created verified
  workspace files, and then failed when the automatic `run_validation_profile`
  action was validated without a specialist `stepId`.
- The same trace persisted 446 `reasoning.delta` records and reached the
  500-event journal limit before the terminal application failure occurred.

## Scope

- Keep exact `stepId` binding mandatory for every specialist-proposed action
  while a plan exists.
- Mark only the automatic post-harness validation as Host-initiated so it does
  not require or invent a specialist plan-step binding.
- Keep Host validation from advancing an unrelated plan step.
- Exclude streamed reasoning deltas from the incident journal while preserving
  context milestones, tool/activity facts, failures, and terminal completion.
- Reserve the final per-trace journal slot for terminal completion, failure, or
  cancellation even when other retained milestones saturate the trace limit.
- Add deterministic Codex-harness E2E regressions for both failures.
- Do not run a real model, GPU workload, cloud request, or user-workspace test.

## Validation

- Run the focused Codex automatic-validation and incident-flood regressions.
- Run formatting verification, an isolated Release build, relevant
  deterministic E2E coverage, and `git diff --check`.
- Preserve the user-started application and stop every process started for
  implementation or testing.

## Completion evidence

- Isolated Release build passed with zero warnings and zero errors.
- Both focused regressions passed: automatic Host validation completed while a
  plan retained one pending step, and terminal completion remained persisted
  after the trace limit was saturated by retained non-terminal events.
- Existing Codex automatic validation, external Host-bridge plan binding,
  canonical trace persistence, exact trace lookup, and concurrent journal
  rotation coverage passed: 8 deterministic E2E cases total.
- Formatting verification and `git diff --check` passed.
- No real model, GPU workload, cloud request, user-workspace execution, or app
  restart was used.
