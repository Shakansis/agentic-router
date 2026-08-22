# PLAN v26 — Complete harness capability integration

Status: completed; ready for manual harness testing

## Objective

Complete the additive Agentic Router capability milestone for every registered
harness (`native`, `codex`, `opencode`, and `qwen-code`) so benchmark results
measure model + harness behavior rather than missing AR adapters. Stop before
implementing or running the benchmark.

## Authority and invariants

- The current `AGENTS.md` and the user's capability-milestone request are the
  governing authority.
- Trusted-workspace boundary checks remain Host-owned and independent of the
  approval selector.
- `auto` executes requested, validated, in-scope mutations without a duplicate
  prompt; `ask` waits for approval before every mutation.
- Capability parity is additive. Harness-native functionality is retained unless
  a concrete AR security invariant cannot be enforced.
- All Host-bridged effects use the existing `LocalActionService` path; adapters
  translate protocols and do not create separate execution stacks.
- Conversation hydration and harness switching continuity must not regress.
- No real model, GPU, or cloud execution is part of deterministic validation.

## Reviewed versions

- Native: Agentic Router source at the current checkout/version.
- Codex CLI: `codex-cli 0.149.0-alpha.4`.
- OpenCode: `1.18.18`.
- Qwen Code: `0.21.13`.

The final report will distinguish the installed versions above from upstream
documentation reviewed on 2026-08-21.

## Architecture decision

1. Keep `IHarnessRegistry -> IAgentHarness -> IAgentHarnessTransport` as the
   only harness-selection boundary.
2. Keep native common tools where their harness can enforce AR policy.
3. Reuse Codex App Server `dynamicTools` for its missing common operations.
4. Add one authenticated loopback MCP Host bridge for external harnesses that
   support MCP. It exposes only canonical AR tool schemas and forwards calls as
   normalized `host-tool.requested` events.
5. Complete each forwarded call through the existing
   `ExecuteHarnessHostToolAsync -> LocalActionService` path so workspace,
   approval, stale-write, process, Git, effect-proof, and truthful-result rules
   remain common.
6. Configure OpenCode and Qwen Code to consume that same bridge through their
   supported MCP mechanisms. Do not enable an unrestricted native shell as a
   substitute for the structured Host process capability.

## Ordered work

1. [completed] Audit registry, contracts, capability projection, complete Host
   action catalog, approval flow, continuity, installed versions, and official
   harness documentation/source.
2. [completed] Implement the shared authenticated MCP Host bridge and canonical
   tool catalog reuse.
3. [completed] Connect OpenCode and Qwen Code adapters, remove their
   `MISSING_ADAPTER` projections, and preserve native extras that do not conflict
   with an AR invariant.
4. [completed] Add browser/API E2E coverage for semantic CRUD, directories,
   process, Git, both approval modes, boundary rejection/recovery, switching
   continuity, and truthful results across all harnesses.
5. [completed] Run formatting, Release build, focused and full deterministic E2E,
   intended-diff review, and `git diff --check`.
6. [completed] Produce the exact-version capability matrix, remaining unsupported
   and security-conflict rationale, preserved-native inventory, and per-harness
   manual test plan; then stop before the benchmark.

## Initial classification boundary

- `NATIVE`: safe harness implementation of an AR-common semantic capability.
- `HOST_BRIDGE`: canonical AR capability executed by Host policy/services.
- `MISSING_ADAPTER`: harness extension point exists but is not connected to AR.
- `UNSUPPORTED`: no reasonable supported mechanism after current upstream and
  installed-source review.
- `SECURITY_CONFLICT`: the only available mechanism violates a named Host,
  boundary, approval, continuity, or truthful-reporting invariant.
- `NATIVE_EXTRA`: useful harness-specific capability outside AR common tools.

No benchmark implementation or benchmark run belongs to this plan.

## Completion evidence

- Installed versions: Agentic Router Native v0.9.14 at `ea2addd76ed0`,
  Codex `0.149.0-alpha.4`, OpenCode `1.18.18`, Qwen Code `0.21.13`.
- Installed Codex App Server experimental JSON schema generated and inspected.
- Isolated Release solution build: success, 0 warnings, 0 errors.
- Focused capability/continuity E2E: 26/26 passed.
- Final complete deterministic browser/API E2E: 265/265 passed, 0 skipped.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed
  outside the sandbox because the build-host named pipe is unavailable inside.
- `git diff --check`: passed.
- Normal `bin/Release` copy gate remains unavailable while the user's running
  `AgenticRouter.Api` PID 37104 owns the output. The process was preserved; the
  same source compiled through the isolated Release output.
- No real Ollama generation, GPU work, cloud request, model download/update, or
  benchmark implementation/run was performed.
