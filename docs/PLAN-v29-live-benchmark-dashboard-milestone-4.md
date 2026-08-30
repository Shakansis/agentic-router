# PLAN v29: Model × Harness Benchmark Roadmap

Status: historical roadmap record through M13; not the current product roadmap.
Last reconciled: 2026-08-30.

The repository, current documentation, and accepted milestone evidence are
authoritative. The sections below preserve what was implemented, deferred, or
left at a manual gate; optional M14+ and deferred harness notes are not active
commitments.

## Completed milestones

Manual closure: on 2026-08-22, the user confirmed that the manual tests for
M0–M3 were completed successfully. Their manual gates are closed; no additional
M0–M3 acceptance work remains scheduled.

Current closure: the Milestone 13 request identifies M0–M12 as complete. M13
implementation and deterministic validation are complete; its requested manual
acceptance pass remains the next gate. Goose, DeepSeek Harness, and shared or
community benchmark work are deferred and are not in the active sequence.

### M0 — Generic Harness Boundary (completed)

- Added the generic `IAgentHarness` contract.
- Put Native behind a thin adapter without changing Native semantics.
- Centralized discovery and resolution in `IHarnessRegistry`.
- Normalized common lifecycle, event, capability, cancellation, and terminal
  contracts.

The original deterministic create-only Benchmark Lab spike informed the later
runner, but it is not a separate future milestone. Its still-valid isolation
and validation rules are part of M3 and the benchmark design below.

### M1 — Codex Generic Harness Migration (completed)

- Moved Codex fully behind `IAgentHarness`.
- Preserved the App Server integration and its protocol-specific adapter.
- Validated session reuse/resume, streaming, tool and approval events,
  cancellation, and exactly one normalized terminal result.
- Kept Host-owned workspace observation, effect proof, validation, and
  objective completion authoritative.

### M2 — OpenCode Experimental Harness (completed)

- Integrated OpenCode through the generic harness boundary.
- Added the Host capability bridge while preserving reviewed native extras.
- Completed the manual end-to-end acceptance gate.

### M3 — Automated Benchmark Runner MVP (completed)

- Added versioned Basic CRUD v1.
- Runs one exact Model × Harness selection across Native, Codex, and OpenCode.
- Creates equivalent isolated disposable fixtures for every test execution.
- Determines acceptance from deterministic Host-owned evidence rather than
  harness narration or lifecycle completion.
- Persists completed and cancelled results with raw measurements, score inputs,
  final scores, and ranking.
- Provides selection, history, result detail, cancellation, scoring, and
  ranking through the browser/API path.
- Completed the manual end-to-end validation gate for Native, Codex, and
  OpenCode.

## Milestone record through M13

### M4 — Live Benchmark Dashboard (completed)

Status: completed before the M13 implementation request.

- Show live harness and per-test progress.
- Show useful harness activity and deterministic Host validation as separate
  evidence.
- Show provisional metrics and ranking while work remains in flight.
- Make pending, running, harness-completed, validating, passed, failed,
  timed-out, cancelled, and terminal run states visible.
- Keep execution independent of browser connection and support bounded SSE
  replay/reconnect.
- Replace provisional state with the authoritative persisted result at the
  final-result boundary.
- Preserve the M3 synchronous API, persisted result shape, history rendering,
  cancellation semantics, and exactly one terminal run result.

Acceptance requires a manual Native + Codex + OpenCode Basic CRUD run through
the live dashboard, independent inspection of final persisted evidence, and
confirmation that reconnect/cancellation/terminal state behave as presented.

### M5 — Scoring & Ranking v2 (completed)

- Add user-editable weights for objective success, correctness, terminality,
  workspace accuracy, and efficiency.
- Recompute rankings from persisted raw measurements without rerunning a model
  or harness.
- Preserve raw measurements and validation evidence independently from every
  score or ranking derived from them.
- Store the weights used for each computed result so historical scores remain
  interpretable.
- Treat a ranking as a view over evidence, never as a replacement for the
  underlying test results.

### M6 — Qwen Code Experimental Harness (completed)

Status: completed before the M13 implementation request. The Qwen Code adapter
and Host capability bridge remain behind `IAgentHarness`.

- Revalidate the existing integration against the installed Qwen Code runtime
  and current generic harness contract.
- Complete the manual acceptance gate, including exact model/workspace
  identity, streaming, tools, cancellation, and truthful terminal behavior.
- Add Qwen Code to the benchmark runner only after that gate passes.
- Run Basic CRUD v1 under the same fixture, timeout, Host validation, evidence,
  scoring, and acceptance rules as the completed harnesses.
- Stop for explicit acceptance before beginning the next harness milestone.

### M7 — Claude Code Experimental Harness (completed)

- Integrate Claude Code through `IHarnessRegistry` -> `IAgentHarness` without a
  selector bypass.
- Preserve Host authority and retain safe reviewed native capabilities.
- Complete a manual acceptance gate before enabling benchmark execution.
- Run Basic CRUD v1 with the same fixture and acceptance rules.
- Stop for explicit acceptance before beginning the next harness milestone.

### M8 — Goose Experimental Harness (deferred)

Goose remains deferred. Do not integrate or benchmark it as part of the active
Model x Harness sequence.

### M9 — Benchmark Suite v2 (completed)

Add advanced, versioned scenarios only after the harness acceptance milestones
above. Keep fixtures, prompts, acceptance rules, and Host validators identical
across compared combinations.

Scenarios should cover:

- continuity across turns or resumed sessions;
- scope and constraint retention;
- recovery after a typed action or validation failure;
- convergence after a materially different recovery strategy;
- terminality and exactly one final state;
- stale or conflicting state where the capability and fixture make it
  applicable.

Suite v2 must preserve individual scenario evidence. A combined score must not
hide timeout, false completion, unintended changes, or failed recovery.

### M10 — Model × Harness Matrix (completed)

- Benchmark multiple exact models against multiple accepted harnesses.
- Use the same versioned fixtures, prompts, timeouts, Host validators, and
  acceptance rules for every comparable matrix cell.
- Record unavailable or non-comparable cells explicitly; never substitute a
  model, provider, harness, or scenario silently.
- Compare and rank combinations automatically while retaining per-cell raw
  evidence.

### M11 — Historical Benchmark Evidence (completed)

Persist enough environment identity to explain and compare results over time:

- exact model version/tag and digest;
- harness identity and version;
- runtime and provider version;
- relevant hardware and operating-system identity;
- benchmark suite, fixture, prompt, validator, and acceptance version;
- historical comparisons that identify changed and non-comparable inputs.

Historical evidence is append-only from the benchmark user's perspective.
Recalculated scores may be added as derived views, but must not rewrite raw
measurements from the original run.

### M12 — Evidence-based Recommendations (completed)

- Derive recommendations from comparable local benchmark evidence first.
- Allow optional web research or reference evidence only as a separately
  identified input.
- Clearly distinguish measured local results from external claims and
  inference.
- State uncertainty and missing matrix cells; do not manufacture a winner from
  incomplete or non-comparable evidence.
- Keep the user's machine and current runtime evidence authoritative for local
  recommendations.

### M13 — Experimental Model × Harness Router (implementation complete)

- Auto classifies the task into the existing M12 category vocabulary, requests
  the existing recommendation ranking, filters it by installed local Ollama
  models and currently available registered harnesses, and selects the first
  acceptable candidate.
- Selection happens once per active conversation/task session. The exact model
  and harness then execute through the existing `IAgentHarness` path; there is
  no mid-turn switching, session migration, cloud fallback, or cross-harness
  recovery.
- The compact route event and persisted execution review retain category,
  router/recommendation/profile versions, confidence, supporting run links,
  and availability fallback without duplicating benchmark evidence.
- Manual Model x Harness selection bypasses Auto.
- The benchmark UI now supports CRUD, Agent Behavior, or both in one run,
  keeps internal versions as reproducibility metadata, removes the redundant
  authorization checkbox, loads persisted results visibly, and moves
  historical/comparison/scoring detail behind Advanced surfaces.
- Deterministic validation passed 304/304 E2E tests, Release build with zero
  warnings/errors, format verification, JavaScript syntax validation, and
  `git diff --check`. Manual M13 acceptance remains open.

### M14+ — Optional Shared Benchmark Dataset (deferred)

- Explicit opt-in only.
- Share only anonymized, bounded benchmark evidence with a reviewed schema.
- Use community-derived evidence as priors or reference context, not as local
  truth.
- Keep local results authoritative for the user's machine, exact model,
  runtime, and harness versions.
- Do not make sharing, telemetry, an account, or a network service a
  prerequisite for local benchmarks.

## Deferred: DeepSeek Harness integration

DeepSeek Harness integration is deferred for re-evaluation later. Do not
schedule or start active integration work now.

Its integration and runtime surface is still evolving rapidly and does not
currently justify the integration, validation, and maintenance cost. Existing
DeepSeek research evidence remains historical comparison material only; it is
not evidence that an Agentic Router adapter or benchmark milestone is accepted.

Re-evaluation requires a materially more stable supported runtime/protocol and
a fresh cost/benefit review against the then-current accepted harnesses.

## Benchmark definitions and design decisions

### Basic CRUD v1

Basic CRUD remains the common acceptance suite for M3 and new harness
milestones:

| Test | Objective | Deterministic acceptance |
| --- | --- | --- |
| `FS-CREATE-001` v1 | Create `benchmark-data/result.txt` | Exact UTF-8 bytes without BOM or trailing newline; exact path; no unrelated changes. |
| `FS-READ-001` v1 | Read two canonical fixture files | Final report contains exact `codename=ORBIT-41` and `verification-word=marigold`; workspace is unchanged. |
| `FS-UPDATE-001` v1 | Change `retries=2` to `retries=3` | Exact final bytes; only `fixture/update.txt` is modified. |
| `FS-DELETE-001` v1 | Delete `fixture/delete.txt` | Exact target is absent; exactly one deletion; no creation or modification; no shell fallback. |

Every test starts from the same versioned canonical fixture in a fresh
engine-owned workspace. Fixture identity is fingerprinted. Test execution may
continue after an individual failure or timeout; explicit run cancellation is
the only user-requested suite-wide stop.

### Evidence and metrics

The Host records and retains raw evidence independently from scoring:

- objective achieved and PASS/FAIL/ERROR;
- exactness, byte, directory, filename, and containment/workspace accuracy;
- expected and unexpected created, modified, and deleted paths;
- useful partial outcome;
- harness execution and terminal state;
- duration, token counts where reported, tool-call count, surfaced errors, and
  recovered errors where available;
- final harness report and deterministic validation facts;
- exact model, provider, harness, suite, fixture, prompt, validator, and
  acceptance identities available at that milestone.

Harness narration, a zero exit code, or lifecycle completion alone is never
objective success. The Host independently observes final state and applies the
versioned acceptance rule.

The current v1 score uses fixed weights: objective success 35, correctness 25,
terminality 15, workspace accuracy 20, and efficiency 5. M5 makes those
weights editable and rankings recomputable; it must not change or discard the
raw v1 evidence.

### Execution and authority

- All benchmark harnesses resolve through `IHarnessRegistry` and execute
  through `IAgentHarness`; no Native or benchmark-specific selector bypass is
  permitted.
- The request names one exact provider/model and explicit harness set. Real
  model execution requires explicit permission. There is no silent fallback or
  substitution.
- Suite execution is strictly sequential in normalized request order: one test
  runs at a time, all tests for one harness finish before the next harness
  starts, and no selected harnesses compete concurrently for the local model.
- Workspaces remain disposable, bounded, containment-checked, reparse-point
  safe, and outside the source repository. Cleanup must validate ownership.
- Host policy, approvals, workspace confinement, stale/conflict checks, effect
  proof, validation, recovery bounds, and terminal truth remain authoritative.
- Capability availability and failures are explicit. A missing bridge must not
  be hidden by unrestricted shell or filesystem authority.
- Persisted raw evidence is authoritative. Live events and provisional ranking
  are transient views and must not affect correctness.

### Harness milestone acceptance

Each new harness milestone is sequential:

1. integrate or revalidate the adapter behind the generic boundary;
2. pass deterministic browser/API coverage at fake external boundaries;
3. stop for manual real-runtime acceptance;
4. after acceptance, run Basic CRUD v1 with the exact selected model;
5. independently inspect final files, reports, terminal state, persistence, and
   ranking;
6. record limitations and obtain explicit acceptance before starting the next
   harness.

Deterministic fake-provider evidence, manual real-runtime evidence, and
historical research evidence must remain distinctly labeled.

## Repository reconciliation

- The old M4 document described the live dashboard as future work, while the
  current repository and its completion evidence contain that implementation.
  M4 is therefore recorded as implemented but still open at its manual gate,
  not duplicated as unimplemented work and not marked completed.
- Qwen Code is already registered behind `IAgentHarness` and connected to the
  Host capability bridge. M6 retains only revalidation, manual acceptance,
  benchmark enablement, and Basic CRUD work; a second integration is obsolete.
- Native boundary work, the create-only spike, Codex migration, OpenCode
  integration, Basic CRUD creation, persistence, fixed scoring, ranking,
  cancellation, and the first results UI were removed from future milestones
  or merged into M0–M3 and the shared benchmark definitions above.
