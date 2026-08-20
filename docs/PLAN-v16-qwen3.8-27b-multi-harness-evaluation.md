# PLAN v16: Qwen3.8 27B multi-harness evaluation

## Goal

Run the established 12-scenario local-agent correctness suite once through Codex, Qwen Code, OpenCode, and Goose, all connected to the same local Ollama `qwen3.8:27b` artifact, then publish one directly comparable report at `docs/research/qwen3.8-27b-multi-harness-evaluation.md` using the preserved Claude Code and DeepSeek Harness evidence.

This is evidence gathering only. It does not authorize Agentic Router source or architecture changes, harness integration, prompt tuning, model changes, or benchmark-result retries.

## Controlled baseline

- Reuse the exact 12 committed fixture repositories, baseline commits, prompts, acceptance criteria, and independent validators preserved by the Qwen3.8 DSH/Claude Code evaluations.
- Use exact tag `qwen3.8:27b`, digest `a5dfec209c3bd4a76748c0d493e596d15f8c84f5fcc255b4072f1e01a863fb9b`, on the existing Ollama `0.32.14` runtime and current hardware.
- The current tag preserves the prior artifact blobs while setting `draft_num_predict 0`, intentionally compensating for MTP functionality newly enabled by Ollama `0.32.14`. Record this provenance; do not use the MTP backup tag.
- Verify model identity before each harness block. Record observed loaded context and device placement; do not silently force a common value that the previous methodology did not force.
- Create a fresh disposable Git clone for every scenario. Use one fresh harness session per scenario except the prescribed persistent sessions in Tests 7 and 12.
- Run the four harness blocks sequentially to avoid GPU contention: Codex, Qwen Code, OpenCode, then Goose.

## Frozen harness configuration

- Install or use the current official CLI release and record its exact version before measured execution.
- Connect only to Ollama's loopback API. Configure no cloud fallback, web search, MCP, plugins, hooks, subagents, project instructions, or user customizations.
- Use each harness's normal first-party local-provider adapter and standard native file/process tools. Do not emulate another harness's private tools.
- Use an isolated configuration/data root where supported and disposable workspaces for authority confinement. Record any unavoidable user-level configuration or Windows sandbox limitation.
- Use the least expansive noninteractive approval mode that permits the unchanged suite. Freeze all permissions, command allowances, context settings, system instructions, and timeout/turn limits before the first measured scenario for that harness.
- Preflight transport and configuration outside the 12 measured scenarios. Preflights may be corrected before measurement; no measured failure may be rerun.

## Evidence and acceptance

Use only `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, and `FAILED` for evidence. Harness exit zero and model narration are not effect proof.

For every scenario record:

- normal terminal return or cancellation/non-termination;
- final effect correctness and useful partial outcome;
- automatic recovery from tool or environment errors;
- convergence and stopping behavior;
- workspace hygiene;
- final user-facing report accuracy;
- native tool calls and error results;
- harness-specific permissions, paths, workarounds, and capability gaps.

Filesystem bytes, Git status/tree/diff, process results, native event transcripts, real-browser behavior for Tests 3 and 7, exact session/model identity, and independently rerun validators are authoritative. Classify important failures as `MODEL`, `HARNESS`, `TOOL SURFACE`, `ENVIRONMENT`, or `UNKNOWN`; multiple causes may apply when supported.

## Ordered work

1. Inventory the four CLIs, verify official local-Ollama configuration, install missing current releases, and freeze isolated configuration files.
2. Run one excluded transport/tool preflight per harness against a disposable preflight repository; record friction but do not include it in the 12-scenario numerator.
3. Recreate the 12 clean fixture repositories from the preserved baseline commits and save a prompt/acceptance manifest per harness.
4. Run Tests 1-6 and 8-11 once in fresh sessions, preserving raw structured output, stdout/stderr, exit status, timing, Git/filesystem state, and Ollama state.
5. Run Test 7's exact eight-turn counter sequence in one persistent session and validate final executable behavior in a real browser.
6. Run Test 12 in one persistent session, interleaving the exact external append after the read and before the first edit. Record the harness's real stale-state semantics without simulation.
7. Independently validate every final workspace and derive scenario-level outcome, recovery, convergence, hygiene, narration, and tool-call facts.
8. Combine the new evidence with the preserved Claude Code and DSH reports only where definitions are directly comparable. Keep Codex continuity-only historical evidence distinct from this new full Codex run.
9. Publish the technical Markdown report, validate its tables and arithmetic, hash preserved source reports before/after, inspect documentation changes, and run `git diff --check`.

## Stop conditions

- Stop a session that targets Agentic Router, a prior report, a non-loopback provider, or any path outside its disposable workspace/configuration root.
- Stop a repeated-action loop once non-convergence is established and preserve cancellation/terminal evidence; do not convert cancellation into a passing terminal result.
- Do not tune or repair prompts, model parameters, tools, permissions, context, or retry policy after a measured failure.
- Do not rerun any measured scenario to obtain a better result. A harness-launch failure after the model session has begun counts as that scenario's observed result.
- Do not run Agentic Router inference, build, E2E tests, integration work, or source edits.

## Report and validation

- Technical repository Markdown, scenario-level audit tables, and one direct six-harness comparison: Codex, Claude Code, DSH, Qwen Code, OpenCode, and Goose.
- Rank primarily by terminality, useful outcome, correctness, recovery, convergence, hygiene, truthful final reporting, then efficiency. Raw internal error count is diagnostic, not the primary score.
- Identify same-model material differences caused by harness behavior and explicitly mark non-comparable evidence.
- Explain that the denominator is 12 executed fixtures; former Tests 13-16 are aggregate analyses rather than extra model runs.
- Report commands, versions, evidence roots, limitations, and every validation actually executed. Do not infer superiority beyond directly comparable evidence.

## Completion status

Completed on 2026-08-18.

- Installed/configured the three requested additional harnesses and froze isolated local-only settings.
- Executed all 48 scenario blocks exactly once, preserving the original sessions after timeout or driver friction rather than rerunning for a better result.
- Independently validated bytes, Git state, final trees, Node tests, and real-browser behavior.
- Published `docs/research/qwen3.8-27b-multi-harness-evaluation.md` with the requested six-harness comparison, attribution, and ranking.
- Preserved Agentic Router source code unchanged and preserved both input report hashes.
