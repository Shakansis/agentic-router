# PLAN v29: Live Benchmark Dashboard Milestone 4

## Goal

Evolve the existing Benchmark CRUD modal into a compact live dashboard while
preserving Milestone 3 selection, deterministic validation, persisted result
shape, score weights, final ranking, history rendering, and harness boundary.
Execution must continue independently of the browser connection and expose
incremental progress through a reconnectable pushed stream.

## Current evidence and constraints

- Milestone 3 is manually validated and has one real persisted Basic CRUD v1
  result. Historical JSON must remain readable without migration or rewrite.
- The runner already owns per-test harness execution, deterministic Host
  validation, scoring, persistence, cancellation, and exactly one suite result.
- Native, Codex, and OpenCode all execute through `IAgentHarness`; live
  observability must remain downstream of that seam.
- The application already uses typed server-sent events for chat. Benchmark
  live state can reuse SSE without polling or adding a frontend dependency.
- Live events are transient evidence. They must not alter the final persisted
  `BenchmarkSuiteRunResult` or make correctness depend on an attached browser.

## Implementation

1. Add compact transient benchmark progress contracts using existing run,
   execution, result, score, and ranking identifiers wherever they already
   represent the state. Add only lifecycle stages absent from Milestone 3:
   pending, running, harness-completed, validating, and terminal.
2. Let the existing engine publish optional progress updates at suite, harness,
   test, useful harness-activity, Host-validation, provisional-ranking, and
   final-result boundaries. The no-observer path must preserve the Milestone 3
   API and final persisted data.
3. Execute selected harness workflows independently while keeping each
   harness's CRUD tests ordered. Continue after per-test failures/timeouts and
   preserve the existing final score/ranking calculation.
4. Add a bounded singleton live-run coordinator. It starts the scoped engine
   in a background task, retains only transient bounded events, supports SSE
   replay/reconnect, and publishes exactly one terminal event. Browser
   disconnect must not cancel the benchmark; the explicit cancellation API
   remains authoritative.
5. Add start/live-state/SSE endpoints without changing existing synchronous
   suite-run, history, detail, or cancellation contracts.
6. Extend the current modal results area with live harness cards, concise test
   states, useful activity, deterministic validation facts, elapsed time, and
   provisional ranking. Replace provisional state with the authoritative final
   persisted result on completion.
7. Add deterministic E2E coverage for lifecycle transitions, useful activity,
   validation visibility, provisional ranking, independent harness progress,
   recovered errors, timeout, cancellation, reconnect/re-render, one terminal
   event, final-result replacement, and unchanged persisted-history rendering.

## Validation

1. Run focused live benchmark API/UI E2E through fake provider/harness
   boundaries only.
2. Run all relevant Benchmark Lab and UI regressions, then the complete
   deterministic Playwright E2E suite.
3. Run `dotnet build AgenticRouter.slnx -c Release --no-restore`,
   `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`, and
   `git diff --check`.
4. Confirm disposable benchmark workspaces are empty and the existing real
   persisted Milestone 3 JSON is unchanged.
5. Do not run real Ollama, GPU, cloud, Native, Codex, or OpenCode validation;
   hand off the requested three-harness qwen3.8 manual run and stop.

## Milestone boundary

Do not add suites, harnesses, configurable score weights, charts, trends,
history analytics, routing, recommendations, installation, or unrelated UI
changes. Stop after the Milestone 4 manual-test handoff.

## Completion evidence

- Deterministic Benchmark Lab M3+M4 regressions: 9/9 passed.
- Complete deterministic Playwright suite: 276/276 passed.
- Release solution build in isolated artifacts output: zero warnings and zero
  errors. The normal Release output remained locked by the user's running API
  process and was not stopped.
- `dotnet format --verify-no-changes` and `git diff --check` passed.
- The existing manually validated M3 result remained unchanged at SHA-256
  `DE8B991C489CFFD78624C39FFBADF9AD42FF5E2D4B7C9C57BA9130E79D32E2E5`.
- No real model, GPU, cloud provider, Native, Codex, or OpenCode benchmark was
  run during implementation validation.
