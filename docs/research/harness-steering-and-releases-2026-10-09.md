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

## Adoption of the six Useful now items

The user subsequently authorized implementation and CLI upgrades immediately,
after the performance task completed. The earlier no-upgrade statement describes
the first audit only. The versions below were downloaded from the official npm
packages, tested in isolation, then installed locally on 2026-10-09.

| Harness | Installed version | Adopted upstream functionality | AR integration change |
| --- | --- | --- | --- |
| Codex | 0.162.0 stable | CRLF-preserving `apply_patch` | Enables the native patch tool in the local-model catalog, translates its custom-tool wire format to Ollama JSON functions, and accepts the official `bin/codex.exe` installation layout. Existing `instant_interrupt` remains enabled. |
| OpenCode | 1.18.35 | Qwen sampling / unsupported `textVerbosity` compatibility | Uses the upstream implementation; no duplicate sampling policy in AR. |
| OpenCode | 1.18.35 | Network and unknown finish-reason recovery | Uses upstream recovery while preserving Host recovery budgets and terminal evidence. |
| OpenCode | 1.18.35 | Empty move-path permission metadata and diagnostic redaction | Uses upstream fixes; Host path/approval validation remains unchanged. |
| Qwen Code | 0.25.0 | Extension isolation and orphan cleanup | Explicitly disables `agents.crossSessionMessaging` in AR's isolated configuration, preserving the existing session boundary despite the new upstream default. |
| Claude Code | 2.1.295 | Headless MCP reconnection and backoff | Corrected authenticated MCP startup under credential scrubbing and disabled newly default-enabled built-in plugins in the isolated AR session. |

The versions include all six approved upstream fixes. No Useful later or
Probably unnecessary item was added, and no background updater was introduced.

### Compatibility findings

The real Codex patch probe initially returned `unsupported call: apply_patch`:
the existing local-model catalog omitted `apply_patch_tool_type`, so the actual
native inventory lacked a tool AR's capability projection already advertised.
The catalog now explicitly enables `freeform`, the only variant in the
[0.162.0 native model contract](https://github.com/openai/codex/blob/rust-v0.162.0/codex-rs/protocol/src/openai_models.rs).
Ollama's direct Responses endpoint uses function calls, so
`CodexApplyPatchWireAdapter` translates only the canonical `apply_patch` tool,
its matching history/results, and its streamed response items. It preserves the
native patch grammar in the function schema and leaves execution, approval and
workspace validation in the existing native/Host path. It does not run patches
itself, add a shell, normalize other tool names, or create another model call.
The real native patch then passed the exact-byte CRLF check.

The subsequent negative native test found that Codex's default `:workspace`
profile permits ambient temporary directories, allowing a patch outside AR's
trusted root when that root was itself under the OS temporary directory. The
adapter now selects `:read-only` plus native `on-request` so every patch reaches
Host validation before execution. AR still applies the user's auto/ask choice:
automatic mode approves valid actions internally; ask mode waits in the existing
browser approval UI. This preserves the established product boundary instead of
delegating it to the broader native profile. The adapter also reads the current
structured `kind.type` / `kind.move_path` metadata so both source and destination
are validated, and deletions retain their destructive classification. Built-in
profile semantics are documented in the [official configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

The real Claude 2.1.295 executable exposed two issues that the older fake CLI
could not detect:

- `--bare` still reports `cc-plugin-agents-md@builtin` and
  `cc-plugin-plugin-authoring@builtin`. AR now explicitly sets both to false
  through `--settings`. The existing rejection of unexpected plugins remains.
- With `CLAUDE_CODE_SUBPROCESS_ENV_SCRUB=1`, MCP expansion blanked the
  `AGENTIC_ROUTER_MCP_TOKEN` credential. A local fake HTTP server reproduced this
  with both inline and file-based MCP configuration. A dedicated
  `AGENTIC_ROUTER_MCP_HEADER` variable expands correctly with scrubbing still on.
  The token stays in the process environment, never literal argv or a saved
  configuration file. The Host's bearer authentication remains required.

These are adapter compatibility corrections, not a broader plugin or authority
change. See the official [MCP credential expansion documentation](https://code.claude.com/docs/en/mcp#credential-variables-that-read-as-empty)
and [plugin configuration reference](https://code.claude.com/docs/en/plugins-reference).

### Upgrade validation and operational notes

The opt-in `InstalledHarnessUpgradeEndToEndTests` exercises the real installed
CLIs through Playwright, the running AR API and its actual isolation/MCP startup.
Only the provider HTTP boundary is scripted. Ordinary E2E runs do not invoke
installed harnesses unless `AGENTIC_ROUTER_INSTALLED_HARNESS_E2E=1` is set along
with the executable overrides used by `TestEnvironment`.

The first four-CLI run passed Codex, OpenCode and Qwen and correctly rejected
Claude's unexpected plugins. After the compatibility correction, all four
passed with authenticated MCP startup and the exact selected model. A separate
Codex fixture directs the real native `apply_patch` through the Host approval
path and compares the resulting CRLF file byte for byte.

An authorized real-model edit probe was cancelled when a new user conversation
started on the shared runtime. It is not counted as a passing inference test.
The existing real-model steering evidence earlier in this report applies to
the earlier CLI versions. These upgrade checks do not independently reproduce
every upstream fault scenario (extended MCP outage, unknown network finish,
extension concurrency, or redaction of every native diagnostic field).

Initial deterministic regression found two stale expectations from the other
completed work: OpenCode now offers the documented `none` effort, and the
running-brain SVG has three outline paths plus three progress-fill paths.
Assertions were updated to verify both actual contracts. One Qwen inactivity
test hit the existing 10-second client limit during concurrent validation;
the isolated three-harness rerun passed without changed limits or assertions.

The running user AR was preserved. The updated adapters are built separately
under `bin/harness-useful-now/Release/net10.0`; a new AR process built from this
source is required to use the adapter changes. Do not run the old adapter build
against the new Claude version: it still has the old MCP/plugin configuration.
The test data directory is isolated and must not replace the user's settings.

Codex was installed alongside the previous executable under
`%LOCALAPPDATA%/OpenAI/Codex/bin/ar-0.162.0`, retaining the official package layout.
OpenCode and Qwen were updated using pinned global npm installs. Claude was
updated using `claude install 2.1.295`; the previous 2.1.234 executable was copied
to `.artifacts/harness-useful-now/rollback/` before installation. All other local
evidence and the pinned package lock are under `.artifacts/harness-useful-now/`.
The existing Codex installation was not overwritten. To reverse npm upgrades,
the previous versions were `opencode-ai@1.18.18` and
`@qwen-code/qwen-code@0.21.13`.

### Final upgrade evidence

- Release solution build: **zero warnings and errors** using
  `dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -p:BaseOutputPath=bin/harness-useful-now/ -p:UseSharedCompilation=false -nodeReuse:false`.
- Scoped `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes`,
  `git diff --check`, and intended-diff inspection passed.
- **80/80 deterministic E2E passed** after the final native approval correction:
  `TestResults/harness-upgrade-regression-complete.trx`.
- **8/8 E2E with actual CLIs and a simulated provider passed**:
  `TestResults/harness-upgrade-native-complete.trx`. Four harness startup/stream
  cases plus Codex CRLF, direct workspace escape rejection, move destination
  escape rejection, and an actual browser ask/approve interaction. The file
  remains unchanged before approval; auto mode completes without an extra click.
- Installed CLI/entrypoint SHA-256 hashes match the staged tested files. An
  isolated AR using default executable discovery reported all four target
  versions available, without test executable overrides.
- Test Hosts, harness children, fake providers and browser test processes were
  stopped. The user's AR PID 73504 and Ollama PIDs 9120/86688 were preserved.

The final regression filter was:

```text
(FullyQualifiedName~Steer|FullyQualifiedName~QwenCode|FullyQualifiedName~CodexHarness|FullyQualifiedName~CodexManaged|FullyQualifiedName~ClaudeCode|FullyQualifiedName~OpenCode|FullyQualifiedName~CompletionMetrics|FullyQualifiedName~OutputToken)&FullyQualifiedName!~Real&FullyQualifiedName!~Installed
```

Tests used the isolated Release DLL and `playwright.runsettings`, with the
existing `AGENTIC_ROUTER_E2E_API_PATH` / four harness executable overrides.
The native matrix instead selected `FullyQualifiedName~InstalledHarnessUpgrade`
with the real staged CLIs and explicit installed-harness opt-in. Reproduction
commands are saved locally in `.artifacts/harness-useful-now/run-e2e.ps1`.

Production changes are in `AgentHarness.cs`, `CodexApplyPatchWireAdapter.cs`,
`HarnessInferenceObserver.cs`, `ClaudeCodeHarness.cs` and `QwenCodeHarness.cs`.
Test changes cover native protocol fixtures, CLI discovery, preserved isolation,
approval behavior, byte-level patch effects and negative workspace boundaries.
