# PLAN v15: Qwen3.8 27B in Claude Code

## Goal

Run the established 12-scenario local-agent correctness suite through Claude Code connected to local Ollama, hold the evaluated Qwen3.8 artifact constant with the earlier DSH baseline, and produce `docs/research/claude-code-qwen3.8-27b-evaluation.md`.

This is evidence gathering only. It does not authorize Agentic Router source or architecture changes, Claude Code integration, DSH changes, prompt tuning, or model changes.

## Controlled baseline

- Reuse the exact 12 committed fixture repositories and prompts preserved under the Qwen3.8 DSH evaluation.
- Use the exact requested tag `qwen3.8:27b`, full digest `a5dfec209c3bd4a76748c0d493e596d15f8c84f5fcc255b4072f1e01a863fb9b`.
- The tag uses the same two base blobs as the prior DSH artifact but changes `draft_num_predict` from `4` to `0`. This intentionally disables MTP functionality enabled by Ollama `0.32.14` so the effective inference configuration remains aligned with the earlier Ollama `0.32.13` baseline.
- Preserve `qwen3.8:27b-mtp-backup` only as provenance for the prior digest; do not use it in measured scenarios because Ollama `0.32.14` would now enable the additional MTP path.
- Record the runtime-compensation difference explicitly. Verify the full digest before every evaluated Claude session and independently verify loaded context and GPU placement.
- Use a fresh disposable Git clone for every scenario and a fresh Claude session except where continuity or stale-state interleaving requires one persistent session.

## Harness configuration

- Install the current official Claude Code native client and record its exact version.
- Connect it only to Ollama's Anthropic-compatible loopback endpoint at `http://127.0.0.1:11434` with local placeholder authentication.
- Use headless Claude Code so prompts, session IDs, JSON results, duration, cost metadata, and terminal state can be preserved without manual intervention.
- Expose the standard Claude Code file and process capabilities without model-specific prompt or tool tuning.
- Run only inside disposable workspaces. Use the narrowest noninteractive permission mode that can execute each unchanged scenario; record any permission or sandbox difference from DSH.
- Do not enable Web search, MCP, plugins, hooks, subagents, cloud fallback, or project/user custom instructions for the evaluated sessions.

## Evidence and acceptance

Use only `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, and `FAILED` for evidence.

Claude narration and process exit zero are not effect proof. Filesystem bytes, Git tree/status/diff, process results, native tool transcripts, real browser behavior, exact session/model identity, and independently rerun validators are authoritative.

Classify significant failures as `MODEL`, `HARNESS`, `TOOL SURFACE`, `CONTEXT / SESSION`, or `UNKNOWN`. Classify final narration separately as `ACCURATE`, `PARTIALLY ACCURATE`, `MISLEADING`, or `FALSE`.

## Ordered work

1. Verify official Claude Code/Ollama compatibility requirements, install Claude Code, record exact versions, and run one excluded transport preflight against the controlled Qwen artifact.
2. Recreate the 12 clean fixture repositories from the preserved baseline commits and save their prompt/acceptance manifest before measured execution.
3. Run Tests 1-6 and 8-11 in separate headless Claude sessions without prompt tuning or manual repair.
4. Run the exact eight-request counter sequence in one persistent Claude session and independently validate intermediate and final executable behavior.
5. Run stale-write recovery in one persistent session, interleaving the external fixture mutation after Claude's read and before its requested edit. Record whether Claude Code itself detects staleness; never simulate a protection that the harness lacks.
6. Aggregate native tool discipline, self-report accuracy, workspace hygiene, convergence, context/session behavior, and local-provider evidence from the 12 scenarios.
7. Compare Claude Code with the preserved same-artifact DSH evidence and the completed Codex evidence where the scenario definitions are directly comparable. Do not fabricate unavailable metrics.
8. Write and validate the technical Markdown report in the existing `docs/research` report location.

## Stop conditions

- Stop any session that targets Agentic Router, previous reports, Claude configuration outside the isolated test configuration, a non-loopback model provider, or any path outside its disposable workspace.
- Stop a repeated-action loop once the behavior is established and preserve cancellation/terminal evidence.
- Do not tune prompts, model parameters, tools, context, system instructions, permissions, or retry policy after observing baseline behavior.
- Do not run Agentic Router inference, build, E2E tests, or integration work.

## Report and validation

- Technical audience, repository Markdown delivery, scenario-level audit tables rather than decorative charts.
- Explain the denominator: 12 executed fixtures; the former Tests 13-16 remain aggregate analyses, not additional model runs.
- Include environment, verified model, harness setup, scenario results, traces, permission/sandbox behavior, recovery, convergence, tool discipline, narration accuracy, continuity, workspace hygiene, local-runtime proof boundary, cross-harness comparison, attribution, implications, and next experiments.
- Hash preserved baseline reports before and after the run, check all fixture commits, run independent validators, inspect the complete documentation diff, run `git diff --check`, and do not claim validations that were not executed.
