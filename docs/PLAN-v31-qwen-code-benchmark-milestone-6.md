# PLAN v31: Qwen Code Experimental Harness Milestone 6

## Goal

Complete the already-implemented Qwen Code experimental harness milestone by
revalidating the installed protocol and enabling `qwen-code` in the existing
Basic CRUD v1 benchmark, live dashboard, persistence, scoring, and ranking
paths. Preserve the current `IHarnessRegistry` -> `IAgentHarness` boundary,
Host capability authority, exact selected Ollama model/provider/workspace, and
all Native, Codex, OpenCode, and Milestone 5 behavior.

## Research baseline

- Installed Qwen Code is `0.21.13`; `qwen --version`, `qwen --help`, and
  `qwen serve --help` were inspected without starting inference.
- Current official Qwen documentation supports headless
  `--output-format stream-json --include-partial-messages`, but bidirectional
  `--input-format stream-json` remains under construction.
- Current official README and daemon documentation explicitly position
  experimental `qwen serve` HTTP + typed SSE as the programmatic client/IDE
  surface for shared sessions, replay/reconnect, cancellation, permissions,
  and lifecycle control.
- The existing adapter already uses the stronger daemon surface with protocol
  `v1`, loopback bearer authentication, exact workspace registration,
  `sessionScope: thread`, typed SSE, isolated `QWEN_HOME`, exact OpenAI-compatible
  Ollama `/v1` provider configuration, Host MCP capability bridge, cancellation,
  session reuse, native-payload preservation, and owned-process cleanup.
- Existing deterministic coverage already proves registry discovery,
  availability/version, provider/model/workspace identity, startup, SSE event
  normalization, thinking/text/tool events, permissions, Host tools, boundary
  recovery, cancellation, malformed/crash/unexpected events, continuity,
  payload bounds, and exactly one terminal state.

## Current delta

- `BenchmarkEngine` and the benchmark catalog currently allow only Native,
  Codex, and OpenCode even though Qwen is registered generically.
- The fake Qwen daemon does not yet execute the canonical Basic CRUD prompts.
- Benchmark UI labels do not currently reuse the experimental harness label.
- Existing benchmark tests explicitly assert that Qwen is absent.

## Implementation

1. Add `HarnessIds.QwenCode` to the existing benchmark allowlists only. Do not
   add a selector bypass, Qwen-specific runner, prompt, fixture, validator,
   timeout, score, persistence, or live-dashboard path.
2. Extend the deterministic fake Qwen daemon to recognize the unchanged Basic
   CRUD v1 prompts and perform their operations through the existing Agentic
   Router Host MCP bridge. Emit Qwen-native typed tool start/result and
   assistant events so existing benchmark evidence counts tool activity and
   captures the final report.
3. Reuse the generic experimental display label in Execute and Benchmark UI so
   Qwen appears as `Qwen Code [Experimental]`, with availability/version already
   supplied by registry discovery.
4. Extend browser/API E2E to select Qwen alongside Native/Codex/OpenCode, prove
   the shared fixture/validator/live/persistence/scoring/ranking path, exact
   model/provider/workspace/version evidence, and preserve existing harness
   regressions.

## Validation

1. Run focused Qwen adapter/Host bridge/session tests and all Benchmark Lab
   M0-M6 API/UI tests through deterministic fake boundaries.
2. Run Native, Codex, and OpenCode harness regressions relevant to selector and
   generic transport behavior.
3. Run the complete deterministic Playwright E2E suite.
4. Run `dotnet build AgenticRouter.slnx -c Release --no-restore`, using isolated
   output if the user's running Release API still owns the normal output; do not
   stop it.
5. Run `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`,
   `git diff --check`, inspect the intended diff, and verify persisted benchmark
   evidence remains unchanged.
6. Do not run a real Qwen/Ollama prompt, GPU workload, cloud call, or real CRUD
   benchmark. Hand off the exact manual Execute and three-harness benchmark
   steps and stop.

## Milestone boundary

Do not add Claude Code, Goose, DSH, another harness, benchmark suite/scenario,
Qwen-specific benchmark prompt, routing, recommendations, sharing, or unrelated
refactoring. Do not replace the existing daemon integration with headless
subprocesses unless installed or official evidence invalidates the required
lifecycle surface.

## Completion evidence

- Installed, model-free `qwen serve` probe: health `ok`, protocol `v1`, Qwen
  Code `0.21.13`, `http-bridge`/REST transport, authenticated exact workspace,
  typed events, session create/load/resume/prompt/cancel/close, permission
  mediation, and workspace MCP reported. The owned daemon was stopped and its
  isolated state removed after the probe. ACP preheat reported sandbox
  `spawn EPERM`; no prompt or provider request was started.
- Focused Qwen/external-harness regression: 40/40 passed.
- Benchmark M0-M6 API/UI regression: 10/10 passed against the isolated M6 API
  and fake harness executables.
- Complete deterministic Playwright browser/API suite: 277/277 passed, zero
  skipped, in 8m25s.
- Isolated Release build passed with zero warnings and zero errors. The normal
  Release output remained unavailable because the user's running
  `AgenticRouter.Api` PID 26632 owns the executable; it was preserved.
- `dotnet format --verify-no-changes` and `git diff --check` passed.
- No real Qwen/Ollama inference, GPU workload, cloud request, model download,
  or real CRUD benchmark was run.

## Manual-gate correction: external plan-step binding

The first manual run exposed a generic external-harness contract gap. After an
OpenCode/Qwen/Codex Host bridge accepts `create_execution_plan`, its executable
tool schemas did not offer the conditional `stepId` argument and the Host event
adapter did not transfer that argument into `LocalActionProposal`. The Host
correctly rejected the resulting unbound action, but the specialist had no
schema-supported way to comply.

Correct this at the shared Host bridge boundary: expose optional `stepId` on
external executable tool schemas, require its exact returned value whenever a
plan exists through the existing Host validator, strip it before tool-specific
argument validation, and advance the bound Host plan step only from the proven
Host action result. Add deterministic OpenCode and Qwen create-plan ->
`run_process` coverage. Do not infer a step, relax the invariant, or add a
harness-specific bypass.

Correction evidence:

- Incident trace `0HNNVV3A7QOQD:00000179` identified OpenCode with selected
  model `qwen3.8:27b-gpu0`; the Host accepted `step-1`, rejected a shell-backed
  process correctly, then rejected the direct process retry because the bridge
  request could not carry `stepId`.
- Deterministic OpenCode and Qwen create-plan -> returned exact `stepId` ->
  `run_process` coverage passed 2/2, including schema exposure, action binding,
  proven effect, and `execution-step-completed`.
- Shared Host-bridge/plan regressions passed 10/10.
- Complete deterministic browser/API suite passed 277/277 with zero skipped in
  7m56s; isolated Release build remained clean with zero warnings/errors.
- No real model inference, GPU workload, cloud request, or user workspace
  mutation was used to validate the correction.

## Real CRUD diagnosis and correction

The later four-harness real CRUD run does not contradict the deterministic M6
evidence above: M6 deliberately did not run a real CRUD benchmark. Its 4/4
Qwen result came from the fake external boundary, while the earlier real Qwen
acceptance proved only a narrower Host-bridged delete path.

The real CRUD run exposed four independent defects:

- the Host configured an 8,192-token hard limit while each minimal Qwen turn
  arrived at about 23,700 prompt tokens;
- Qwen received nine native and seventeen Host MCP tools even though Basic
  CRUD exposed only six canonical Host capabilities;
- the workspace-scoped daemon remained alive during cleanup and locked the
  disposable directory on Windows; and
- cleanup failure replaced the primary Qwen/context failure in persisted
  benchmark evidence.

PLAN v34 corrects those defects with a 32,768-token benchmark floor, exact
active-profile tool scoping, benchmark-only daemon release before cleanup, and
primary-error preservation. Deterministic validation is complete; a real-model
rerun remains an explicit manual gate and must preserve the failed run as
historical evidence.
