# PLAN v37: Benchmark Suite v2 — Milestone 9

## Goal

Extend the existing deterministic benchmark runner with one immutable advanced
agent-behavior suite. Preserve Basic CRUD v1, the generic `IAgentHarness`
boundary, sequential isolation, Host authority, persisted raw evidence, editable
scoring profiles, and the existing live benchmark workspace.

## Suite identity

- Suite: `agent-behavior`
- Suite version: `2`
- Fixture: `agent-behavior-fixture`
- Fixture version: `1`
- Every scenario has a stable id/version, canonical ordered turns, a bounded
  per-scenario timeout, an explicit turn budget, deterministic Host validation,
  and versioned acceptance criteria.

## Scenarios

1. `CONTINUITY-001`: three requests in one harness session; later edits must
   preserve the earlier title and enabled-state requirements.
2. `SCOPE-RETENTION-001`: one narrow update beside unrelated files; only the
   requested file may change and it must not be recreated.
3. `RECOVERY-001`: a deterministic Host capability rejects its first matching
   validation attempt and accepts the corrected attempt; the final workspace
   must be correct with bounded recovery evidence.
4. `CONVERGENCE-001`: one requested edit plus one deterministic validation; a
   passing validation is the stop boundary and later tool calls fail hygiene.
5. `TERMINALITY-001`: the required file change is possible while an explicitly
   optional validation capability is unavailable; final state and report must
   be truthful without looping.
6. `STALE-CONFLICT-001`: after an initial read turn, the Host mutates the same
   file externally before the edit turn; final authoritative bytes must retain
   both the external change and requested change.
7. `TRUTHFUL-REPORT-001`: required work succeeds while optional validation
   deterministically fails; the final report is classified from Host-observed
   reality as accurate, incomplete, or misleading.

## Ordered implementation

1. Generalize the test contract with ordered scenario turns and optional
   Host-owned between-turn actions while retaining one-turn CRUD defaults.
2. Execute all turns in one workspace and one stable harness session id.
   Preserve external harness resume semantics; keep Native continuity in its
   benchmark message history. Release harness workspace state only after the
   final turn.
3. Add only the scoped benchmark capabilities required by v2: deterministic
   validation, optional-unavailable validation, and fail-once validation. Do
   not expose unrestricted process or shell execution.
4. Record per-turn prompts, terminal states, final reports, tool/error counts,
   recovery and Host events. Persist independent v2 measurements for
   continuity, scope, recovery, convergence, hygiene, and narration accuracy.
5. Keep the existing five editable scoring weights and compatibility formulas.
   Map deterministic v2 evidence into the existing correctness, terminality,
   workspace-accuracy, and efficiency components rather than inventing new
   profile weights. Raw v2 dimensions remain independently inspectable.
6. Extend the existing live events and benchmark view with suite selection,
   current turn, recovery/Host event evidence, and v2 metric/report detail.
   Do not create a second benchmark UI.
7. Keep one scenario/harness failure isolated. Continue later scenarios and
   harnesses unless the user cancels the suite.

## Validation

1. Add focused fake-boundary E2E coverage for session reuse, continuity, scope,
   fail-once recovery, stop-after-success, timeout/non-convergence, external
   stale mutation, preserved external state, narration classification,
   continuation after failure, persistence/versioning, live progress, scoring
   compatibility, and Basic CRUD v1 regression.
2. Run the focused benchmark coverage and the complete deterministic E2E suite.
3. Run `dotnet build AgenticRouter.slnx -c Release --no-restore`,
   `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`, and
   `git diff --check`; inspect the complete intended diff.
4. Do not run real Ollama/GPU or external harness validation without an exact
   model and explicit run-time authorization. Hand off the exact UI steps for
   Codex, OpenCode, Qwen Code, and Claude Code using one model, then stop.

## Boundary

Do not implement Goose, DSH, another harness, Model x Harness routing,
community sharing, web recommendations, automatic tuning, harness installation,
or Milestone 10. Do not change Basic CRUD v1 prompts, fixtures, validators,
identities, persistence readability, or historical scoring behavior.

## Completion marker

When deterministic implementation and validation are complete, report exactly:

`MILESTONE 9 BENCHMARK SUITE V2 READY FOR MANUAL TEST`

Then stop.

## Deterministic validation evidence

- Five focused Agent Behavior v2 E2E tests passed, covering the complete Native
  suite, external Codex session reuse, identical scenario inputs across Codex,
  OpenCode, Qwen Code, and Claude Code, live timeout continuation, persistence,
  rescoring, and misleading-report classification.
- The final focused v2 plus Qwen/Claude cancellation-cleanup gate passed 7/7.
- Basic CRUD v1, scoring, live dashboard, and benchmark UI regression coverage
  passed 4/4 after Suite v2 was added.
- The complete deterministic E2E suite ran twice: 292/293 passed both times.
  The only failure was the pre-existing cross-test UI-state test
  `GitInitializationAndIdentityRemainExplicitAndRepositoryScoped`; it expected
  `Configuração local` while the prior `Repository initialized...` status was
  still rendered. Its isolated rerun passed 1/1 without a code change.
- Release solution build in isolated artifacts passed with zero warnings and
  zero errors. The exact default-output build was attempted on the final code
  but remained blocked by the user's running Release API process (PID 5372);
  that process was not stopped.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` and
  `git diff --check` passed.
- No real model inference, GPU workload, cloud request, harness installation,
  download, or service restart was performed. The real four-harness matrix is
  the explicit manual gate.
