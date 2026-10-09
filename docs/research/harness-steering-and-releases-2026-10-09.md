# Harness steering and release audit — 2026-10-09

Scope: the five harnesses registered in Agentic Router. Local versions were read
from the installed executables and confirmed by the isolated Host's discovery API.
Release information was checked against upstream documentation on October 8–9.
This is a selected integration-relevant change list, not every upstream UI fix.
No CLI was upgraded, no cloud inference was invoked, and production settings were
not modified.

## Steering findings

| Harness | Installed | AR transport | Finding and disposition |
| --- | --- | --- | --- |
| Native | Built in | Host execution loop | No same-turn input transport. Queue-only is accurately advertised. Adding preemption to the Host/provider loop is separate implementation work; there is no external feature flag to configure. |
| Codex | 0.162.0-alpha.2 | App Server JSON-RPC `turn/steer` | Same-turn steering already existed, but the isolated configuration omitted the opt-in `instant_interrupt` feature. Enabled it, forwarded `clientUserMessageId`, serialized concurrent admissions, deduplicated accepted IDs, and rejected conflicting IDs and completed turns. |
| OpenCode | 1.18.18 | Legacy HTTP `POST /session/{id}/message`, `noReply: true` | Already appends context to the owned active loop without starting another loop. Existing code verifies receipt, native busy state, idempotence and immediate terminal races. This is context injection at loop boundaries, not a documented instant interruption of model generation. |
| Qwen Code | 0.21.13 | Daemon HTTP `mid-turn-message` and reconciliation | Fixed missing/mismatched receipt IDs and premature acceptance. The Host now waits for reconciled injection, rejects late promotion and attempts to release undrained admissions. A failed attempt stays queued and paused in the browser. |
| Claude Code | 2.1.234 | Headless `stream-json` stdin/stdout | Queue-only remains accurate for this adapter. Official streaming-input documentation describes sequential messages and interruption, not an exact-active-turn steering RPC with equivalent receipt guarantees. Do not infer that the interactive terminal's steering is available through this transport. |

Codex's feature was introduced in CLI 0.159.0. The installed executable lists it
as **under development, false by default**. The generated App Server schema
supports `clientUserMessageId`, `expectedTurnId`, `threadId` and `input`.
The AR runtime now opts in through its own `config.toml`; the user's global
Codex configuration is untouched. Older CLI versions must not be assumed to
provide instant behavior merely because they implement ordinary `turn/steer`.
[Official Codex changelog](https://learn.chatgpt.com/docs/changelog),
[App Server protocol](https://learn.chatgpt.com/docs/app-server).

Claude's distinction is a transport limitation, not a claim that the product
has no interactive steering. Mapping interrupt-and-submit to AR's same-turn
Steer button would change cancellation, turn identity and completion semantics.
[Official streaming-input contract](https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode).

Qwen transfers ownership at admission and may promote undrained messages into a
later prompt. Its reconciliation rings and injection events matter; `accepted`
alone must not be described as proof that the active model consumed the text.
The real test reproduced a promotion after AR's old one-shot check. The new
reconciliation loop does not report that as successful same-turn steering.
Upstream can still begin promotion before removal arrives: removal is not rollback
of an already-started effect. In AR the ended turn's inference/tool bridge is no
longer available, so this is not a permitted independent Host execution.
[Official daemon protocol](https://qwenlm.github.io/qwen-code-docs/en/developers/qwen-serve-protocol/).

Host inactivity recovery now monitors steering asynchronously alongside normal
stream events, rather than blocking event consumption while waiting for the
harness to reach an injection boundary. The pending operation is cancelled and
observed on stream teardown.

## Release review and AR assessment

The assessment is engineering judgment against AR's current local-first scope,
Host authority and existing integration paths. A useful upstream feature does
not automatically justify exposing it in AR.

| Harness / version | New functionality | Description | Assessment for AR |
| --- | --- | --- | --- |
| Codex 0.159.0 | Instant interrupt | Opt-in preemption during model responses and yielding during code-mode calls. | **Necessary now:** directly addresses the requested steering latency; enabled in this change. |
| Codex 0.159.0 | Item-based history pagination | App Server history can page from a specific item. | **Useful later:** can reduce resume/review transfer size if AR starts reading native history; canonical AR history remains authoritative. |
| Codex 0.162.0 | Preserve CRLF in `apply_patch` | Keeps existing Windows line endings when editing. | **Useful now as an upstream fix:** reduces unrelated diffs; no new AR abstraction needed. |
| Codex 0.162.0 | Custom-provider capabilities and `Retry-After` | Explicit web/remote-compaction capabilities and improved retry pacing. | **Useful later:** evaluate against the actual Ollama route; do not declare remote features supported merely because Codex exposes a setting. |
| Codex 0.162.0 | Managed worktrees and Code Mode helpers | New execution/orchestration capabilities. | **Probably unnecessary now:** needs reviewed bridges and workspace semantics; enabling them is outside the steering repair. |

Source for the Codex rows: [official CLI release notes](https://learn.chatgpt.com/docs/changelog).
The latest stable documented release checked was 0.162.0; the local binary is an
alpha build with the same version family, not evidence of identical contents.

| Harness / version | New functionality | Description | Assessment for AR |
| --- | --- | --- | --- |
| OpenCode 1.18.19 / 1.18.22 | Compatible sampling and request fields | Removes built-in Qwen sampling defaults and avoids unsupported `textVerbosity`. | **Useful now:** directly relevant to local/OpenAI-compatible models; candidate upgrade regression tests. |
| OpenCode 1.18.20 / 1.18.21 | Provider failure recovery | Handles more network finish reasons and avoids premature stops on unknown finish reasons. | **Useful now:** compare native recovery with AR's bounded recovery and terminal evidence. |
| OpenCode 1.18.26 / 1.18.33 | Permission metadata and diagnostic redaction | Fixes empty move paths in `apply_patch` metadata; redacts sensitive debug fields. | **Useful now:** improves boundary validation and diagnostic hygiene. |
| OpenCode 1.18.27 | Stream/header timeout handling | Longer default timeouts and cancellation fixes. | **Useful later:** relevant to model cold starts, but preserve AR's own deadlines rather than blindly increasing them. |
| OpenCode 1.18.31 / 1.18.34 | ACP resume fidelity and identity headers | Restores session controls/reasoning boundaries and improves request correlation. | **Useful later:** identity is useful for diagnostics; ACP fixes do not repair AR's current HTTP transport. |

Source: [official OpenCode changelog](https://opencode.ai/changelog).
Latest checked: 1.18.35. No new instant-steering contract was established in the
reviewed 1.18.19–1.18.35 notes.

| Harness / version | New functionality | Description | Assessment for AR |
| --- | --- | --- | --- |
| Qwen Code 0.24.0 | Summary event projection | Smaller prompt/replay streams. | **Useful later:** assess lost detail before using it; Host evidence must retain required tool and terminal facts. |
| Qwen Code 0.24.0 | Child admission budget and idle reclamation | Bounds daemon child-process pressure. | **Useful later:** potentially helpful for long sessions; validate interaction with AR process ownership. |
| Qwen Code 0.24.0 | External reasoning profiles | More explicit reasoning defaults/controls. | **Useful later:** map to AR's existing typed inference profiles, without another settings system. |
| Qwen Code 0.24.0 | Cross-session messaging enabled by default | Ambient communication between sessions. | **Probably unnecessary now:** audit isolation before upgrading; do not silently enable it for AR sessions. |

Source: [Qwen Code 0.24.0 release](https://github.com/QwenLM/qwen-code/releases/tag/v0.24.0).

| Harness / version | New functionality | Description | Assessment for AR |
| --- | --- | --- | --- |
| Qwen Code 0.25.0 | Versioned Runtime Broker contract | Manifest, preflight, approvals and file-history integration points. | **Useful later:** worth a focused adapter study, but AR must remain the authority for policy and completion. |
| Qwen Code 0.25.0 | Durable tool results and restore | Recovery of hosted work and results. | **Useful later:** compare with existing AR persistence before adopting another durable runtime. |
| Qwen Code 0.25.0 | Concurrent Code Mode Bash / freeform input | More expressive native execution. | **Probably unnecessary now:** needs a reviewed capability bridge and effect/approval validation. |
| Qwen Code 0.25.0 | Workspace agents, A2A and bundled Mem0 | Delegation, sharing and memory infrastructure. | **Probably unnecessary now:** expands scope, privacy and background behavior beyond this task. |
| Qwen Code 0.25.0 | Extension isolation and orphan cleanup | Reduces shared-state interference and lingering children. | **Useful now as upstream reliability fixes:** include in upgrade acceptance. |

Source: [Qwen Code 0.25.0 release](https://github.com/QwenLM/qwen-code/releases/tag/v0.25.0).
Latest stable checked: 0.25.0; newer previews/nightlies were not treated as stable.

| Harness / version | New functionality | Description | Assessment for AR |
| --- | --- | --- | --- |
| Claude Code 2.1.295 | Hook failure blocking | `onFailure: "block"` distinguishes hook launch/timeout failures. | **Useful later:** only if AR integrates hooks; it must not introduce a second policy authority. |
| Claude Code 2.1.295 | Headless MCP reconnection fixes | Recovers longer outages and backs off repeated drops. | **Useful now as upstream reliability fixes:** exercise with AR's isolated MCP bridge before upgrading. |
| Claude Code 2.1.295 | Headless output preservation | Preserves responses when background work causes another turn. | **Useful later:** AR currently owns turn lifecycle and does not expose ambient background work. |
| Claude Code 2.1.295 | Gateway timing and request IDs | Upstream first-byte bounds and correlation improvements. | **Probably unnecessary now:** AR's Claude harness currently targets local Ollama, not the Claude apps gateway. |
| Claude Code 2.1.295 | Terminal status protocol and mods | Terminal/native UI integration. | **Probably unnecessary:** AR supplies its own browser UI and activity state. |

Source: [official Claude Code 2.1.295 release](https://github.com/anthropics/claude-code/releases/tag/v2.1.295).
Latest release checked: 2.1.295, versus installed 2.1.234.

## Validation

### Real installed harnesses, local Ollama only

Explicitly authorized by the user. An isolated Host used a temporary trusted
workspace and `qwen3:4b-instruct`. The Host selected a test-owned Vulkan Ollama
runtime; the existing Ollama service was preserved. These timings are functional
observations, not controlled performance comparisons (warmth, prompts and native
loop behavior differ).

| Scenario | Result | Receipt / visible effect after Steer |
| --- | --- | --- |
| Codex, steer during a numeric response | Passed; changed answer within the original native `turn_id` | 48 ms / 883 ms |
| OpenCode, steer during a numeric response | Passed; produced the requested marker in the active Host turn | 101 ms / 1,512 ms |
| Qwen, steer at a read/list tool boundary | Passed; confirmed injection and produced the marker | 1,561 ms / 30,523 ms |
| Qwen, steer during a tool-free numeric response | Native same-turn delivery unavailable in this scenario; corrected Host returned `409 qwen-code-steer-promoted` | 7,419 ms to classified rejection |

The first exploratory Qwen test incorrectly received success before the daemon
promoted the input; native history showed a second prompt after the owned turn.
That evidence drove the reconciliation fix. The subsequent positive-delivery
probe correctly failed on the typed 409. It was then split into separate positive
tool-boundary acceptance and an exact negative regression asserting promotion
rejection, failed delivery indication, retained queue and paused automatic dispatch.
This does **not** establish instant interruption for Qwen.

The first OpenCode probe used 2,000 numbers; the small model refused it before
steering could be exercised. The 200-number probe passed without changing the
delivery assertions or increasing time limits.

Evidence: `harness-steering-real.trx` (exploration),
`harness-steering-real-final.trx` (Codex/OpenCode success and Qwen classified
rejection), `harness-steering-real-qwen-tools.trx` (positive Qwen boundary case),
under `tests/AgenticRouter.EndToEndTests/TestResults/`.

### Repository checks

- Release solution build with `--no-restore -m:1`,
  `-p:BaseOutputPath=bin/harness-steering/`, `-p:UseSharedCompilation=false`,
  `-nodeReuse:false`; zero warnings/errors.
- Scoped `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes`.
- `git diff --check` and comparison against copies of the initial dirty files.
- Deterministic Playwright/MSTest browser/API coverage includes malformed and
  mismatched receipts, accepted-message idempotence, conflicts, completed turns,
  delayed Qwen consumption, late promotion/removal, retired-ID reuse, queue
  preservation and all three adapters' inactivity recovery.
- The broader run exposed an already-stale Codex metadata assertion expecting
  only Low/Medium/High. The pre-existing code and `docs/performance-controls.md`
  include None. Updated the exact expected list to None/Low/Medium/High;
  no production reasoning behavior changed in this audit.

The test commands use the isolated binaries via `AGENTIC_ROUTER_E2E_API_PATH` and
the four `AGENTIC_ROUTER_E2E_FAKE_*_PATH` variables. Regression filter:

```text
(FullyQualifiedName~Steer|FullyQualifiedName~SlowWatchdog|FullyQualifiedName~SlowRequest|FullyQualifiedName~QwenCode|FullyQualifiedName~CodexHarness|FullyQualifiedName~MessageBuffer)&FullyQualifiedName!~RealHarness
```

Real tests require the explicit opt-in `AGENTIC_ROUTER_REAL_STEERING_URL` and
`AGENTIC_ROUTER_REAL_STEERING_MODEL`; the ordinary suite does not run inference.
No latest-release upgrade, cloud-provider matrix or production deployment was
performed. The isolated history-disabled Host also logged existing
`session-not-found` persistence warnings; these did not constitute successful
persistence and were outside this steering repair.

Final deterministic regression: **54/54 passed**, including all 19 focused
steering cases, in `harness-steering-regression-final.trx`. The real negative
Qwen regression also passed separately (1/1) in
`harness-steering-real-qwen-rejection.trx`: typed promotion rejection, failed
delivery presentation, retained draft, paused dispatch and no claimed marker.
The earlier diagnostic failures are retained above; they were not positive
acceptance evidence. Real positive cases were Codex, OpenCode and Qwen at a tool
boundary, as listed in the table.

Final Release build: zero warnings/errors. Final scoped format verification and
diff whitespace checks passed. Test Hosts, fake harnesses, browser test processes,
real harness children and test-owned Ollama runners were stopped. A process/port
check found no remaining test/build servers or listeners on 15379/12439. The
pre-existing Ollama service PID 9120 remained running.

### Files changed in this audit

- `AgenticRouter.Api/Execution/AgentHarness.cs`: Codex flag, input identity,
  serialized idempotence and stale-turn checks.
- `AgenticRouter.Api/Execution/QwenCodeHarness.cs`: exact receipt identity,
  bounded consumption reconciliation, promotion/timeout cleanup and retired IDs.
- `AgenticRouter.Api/Chat/ChatStreamService.cs`: asynchronous inactivity steering
  alongside normal stream processing, with cancellation/observation on teardown.
- `tests/FakeCodexAppServer/Program.cs`, `tests/FakeQwenCodeServer/Program.cs`:
  external-boundary fixtures for concurrent, invalid and delayed receipts.
- `tests/AgenticRouter.EndToEndTests/BenchmarkAndHarnessEndToEndTests.cs`:
  browser/API regressions and the existing documented reasoning-level expectation.
- `tests/AgenticRouter.EndToEndTests/RealHarnessSteeringEndToEndTests.cs`:
  explicitly opted-in real browser acceptance and promotion rejection.
- `docs/PLAN-v57-browser-message-buffer-and-harness-steering.md`,
  `docs/harness-capability-matrix.md` and this report: current contracts/evidence.

All unrelated dirty changes present at the start were preserved. Audit diffs
against the initial file copies and local diagnostic evidence are under
`.artifacts/harness-steering-audit/` (ignored; not part of the product).
