# Execute and Benchmark resilience controls

## Scope

Preserve the existing Host policy, selected model and harness, approval mode,
trusted workspace, scoring, sequential benchmark order, and browser detach
semantics. Do not add generic provider retries or restart an accepted prompt.

## Workspace admission

Serialize the existing workspace-ownership check with the transition to Running.
Concurrent starts keep the existing behavior: one run starts and another waits
with `supervision-recovery-workspace-busy`. This closes a check/transition race;
it does not change which workspaces or operations are permitted.

## Benchmark replay

The activity journal retains 512 events. Current semantic state (run selection,
cell progress, test state/result, validation and ranking) is retained separately,
with one latest event per semantic key. A live view includes this state plus the
bounded journal in sequence order. It is not a complete historical event log.

When an SSE cursor predates the retained journal, the Host emits `run.snapshot`
with `snapshotEvents` and the current sequence. The browser rebuilds the existing
dashboard from that snapshot, then continues with newer events. Duplicate or
stale snapshots are ignored. Completion keeps its existing single-final-result
behavior, including sequential repetitions.

## Recovery controls and user conditions

1. Cancellation remains in progress until execution has settled. After 30 seconds
   without settlement, expose the failure and keep workspace ownership; do not
   start the next benchmark test while the previous execution remains active.
2. Recoverable checkpoint I/O receives three bounded storage-only retries
   (250/500/1000 ms). The model and its effects are never replayed by these
   retries. Persistent failure interrupts the current turn and pauses the
   objective in `awaiting-user`, preserving the last valid checkpoint. Explicit
   resume reconciles current effects before continuing; the objective is not
   marked cancelled merely because storage was temporarily unavailable.
3. Infrastructure failure during cell preparation ends the matrix while retaining
   completed results. Ordinary test failure continues to allow following tests.
   Final-result persistence has a separate status so a storage failure does not
   discard the evidence or prevent export.

The user approved item 3 and conditioned items 1/2 on protection against stale
occupancy and cancellation of recoverable objectives. Workspace occupancy now
tracks the actual Host execution task rather than lifecycle labels or process
names. Completed/faulted tasks do not hold a workspace slot. Terminal state
eviction and run removal do not override a still-active task's ownership.

The benchmark gives each production Execute turn a Host-owned chat-run identity,
explicitly cancels on timeout, and waits for Host execution settlement before
validation/cleanup. Unconfirmed shutdown stops the matrix and retains the
workspace without claiming validated effects. Review collection has a 5-second
deadline; snapshot/validation and cleanup each receive a separate 30-second
budget. Filesystem cleanup checks cancellation between entries.

Infrastructure and persistence errors are separate additive result fields.
Unsaved results remain available through the existing result/raw endpoints in
the current Host process (up to 20 retained results), and the browser does not
start further batch repetitions after either error. Incremental benchmark
restart/resume and additional recovery layers are outside this change.

## Validation

Use the real browser/API with fake external providers for deterministic coverage:

- replay beyond the 512-event journal and browser reload;
- benchmark timeout after automatic promotion to Supervisor;
- cancellation during delayed harness teardown;
- simultaneous supervision admission to one workspace;
- checkpoint write failure, cancellation, and explicit reconciled resume;
- cell preparation failure after completed tests;
- final-result storage failure and evidence export;
- bounded finalization without starting overlapping work.

Real local validation is authorized by the user for worker `qwen3.8:27b-gpu0`,
discovered through the installed-model endpoint, and supervisor `gpt-oss:20b`
(explicit user correction to match daily use), preserving their configured GPU
affinities, with Native, Claude Code, Codex, OpenCode, and Qwen Code. Run
sequentially using an isolated Host/data/workspace and verify
both supervised execution and benchmark evidence. Record actual results and
limitations rather than inferring real-provider success from fake-provider tests.

## Validation, 2026-09-22

- Isolated Release build: zero warnings and zero errors. Scoped
  `dotnet format --verify-no-changes --no-restore`, `node --check`, and
  `git diff --check` passed.
- Browser/API end-to-end regression selection: 15/15 passed with fake external
  providers. This includes the seven focused resilience cases and existing
  replay, matrix, reconnection, and cancellation coverage. The result is in
  `.artifacts/resilience/test-results/resilience.trx`.
- Real Execute Supervisor through the browser: Native, Claude Code, Codex,
  OpenCode, and Qwen Code each completed with worker `qwen3.8:27b-gpu0` and
  supervisor `gpt-oss:20b`. The Host reached a terminal state, released the
  workspace, and the requested file contained the exact marker for each harness.
  The recorded routes used `ollama:0` for the worker and `rocm:0` for the
  supervisor. Per-harness proofs are in `.artifacts/resilience/real-20260922-174212/`.
- Real Benchmark Lab custom-prompt matrix, run
  `6847839a59374ad09b8ad0516ac54244`: one sequential cell per harness,
  5/5 completed and persisted, with no infrastructure or persistence error.
  Each retained workspace contained the exact requested file content. The UI
  displayed five finished cells. Its manual result awaits user review; the
  technical pass is not a user quality score. Full result and compact proof are
  `benchmark.json` and `benchmark-proof.json` in the same evidence directory.

The real run validates the success path and cross-harness settlement. Fault
injection for checkpoint, cancellation, replay gap, infrastructure failure, and
result storage was covered by the deterministic browser/API tests; those fault
paths were not induced against the real GPU providers. The user's regular API
and concurrent ROCm startup work were preserved during validation.

## Complete-suite gate

The complete Release E2E suite passed outside the sandbox: **564 passed, 0
failed, 0 skipped** in 10m 58s. The final TRX is
`.artifacts/resilience/test-results/resilience-full-green-candidate.trx`.
Earlier complete runs exposed Chat, Execute UI, approval, restored-input,
Groq-routing, and Benchmark-reporting failures; the corrected cases were
rerun individually before this complete green run. The sandbox cannot start
the fake `HttpListener`, so its failed attempt is not test evidence.

The final Release solution build has zero warnings and zero errors. Scoped
`dotnet format --verify-no-changes --no-restore`, `node --check`, and
`git diff --check` passed.

The production benchmark was repeated after the final-report fix in a fresh
isolated Host. Run `f70c1536bf394c9e8ca211cdab1fa2e7` completed and
persisted all five harness cells using `qwen3.8:27b-gpu0`, without
infrastructure or persistence error. Every retained workspace contained the
exact requested marker, and every final harness report included it. Native
and Codex now retain the completion instead of only an action introduction.
The raw result, compact proof, and per-harness report proof are in
`.artifacts/resilience/real-20260922-195549/`.

Execute Supervisor was also repeated on the final build in
`.artifacts/resilience/real-20260922-200002/`. Native, Claude Code, Codex,
OpenCode, and Qwen Code each completed with `qwen3.8:27b-gpu0` as worker and
`gpt-oss:20b` as supervisor, a durable checkpoint, no active execution at
terminal state, and the exact expected file content. Qwen Code needed one
visible correction after supervisor verification before completion. The first
Native probe used a volatile workspace profile; it was repeated after enabling
history, and the retained `proof-native.json` is the durable passing run.
