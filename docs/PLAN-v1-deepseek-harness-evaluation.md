# PLAN v1: DeepSeek Harness evaluation

## Goal

Determine with reproducible, same-model experiments whether the official DeepSeek Harness (`dsh`) can reliably own the local coding-agent execution loop while Agentic Router retains routing, workspace policy, user experience, and Host-authoritative boundaries.

This is a research goal. It does not authorize integrating DSH, removing Router behavior, adding ACP/MCP, or redesigning the application.

## Starting state (2026-08-15)

- Agentic Router checkout: `C:\Users\Rodrigo\source\repos\agentic-router`, branch `main`, commit `742b8d94923c96a75d86830be55d68870bbfd7be`.
- The checkout contains substantial pre-existing modified and untracked work. Evaluation changes are limited to this plan and `docs/research/deepseek-harness-evaluation.md`.
- Installed official DSH package: `@deepseek-ai/dsh` `0.1.0-rc.6` (developer preview).
- Node.js: `v22.18.0`; npm: `10.9.3`; pnpm: `11.19.0`.
- Ollama: `0.32.13`; exact installed comparison model: `qwen3-coder:30b`, digest prefix `06c1097efce0`.
- Prior Router evidence includes passing deterministic fake-provider E2E but failed/incomplete real-Qwen acceptance. That evidence is a comparison input, not a DSH result.

## Evidence rules

Every conclusion in the report is labelled `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, or `FAILED`.

- `PROVEN`: directly observed in this evaluation or in an identified preserved prior run.
- `FAILED`: the tested behavior demonstrably failed.
- `PLAUSIBLE`: supported by implementation or primary documentation but not sufficiently demonstrated.
- `SPECULATIVE`: possible but unsupported by enough evidence.

Model output, harness success text, and a zero process exit code do not prove task completion. Acceptance comes from the resulting Git diff, file contents, command results, and recorded trace.

## Isolation and reproducibility

1. Keep all scenario repositories under one disposable evaluation root, outside the Agentic Router source tree when the runtime permits it.
2. Create a committed baseline for every scenario and record its commit before execution.
3. Use a fresh clone or deterministic reset from that baseline for each independent run.
4. Record DSH command/profile, exact prompt, start/end timestamps, elapsed time, model/provider configuration, tool trace, process trace, final text, Git status, diff, and all added files.
5. Do not delete or reset the Agentic Router worktree. Do not point DSH at it.
6. Keep optional cloud providers disabled. Network access for package/source inspection is distinct from inference; fully-local claims require local Ollama request evidence.

## Ordered work

1. Inspect the installed DSH package, authoritative repository documentation, default profiles, provider adapters, session storage, tool definitions, and supported non-interactive interfaces.
2. Create the disposable baseline repository and a capture format that preserves raw traces without placing generated reports in published application output.
3. Prove the exact DSH-to-local-Ollama configuration with `qwen3-coder:30b`; record context and relevant inference settings when exposed.
4. Run the independent scenarios: exact file creation, minimal existing-file edit, existing-asset reuse, recoverable stale edit, build/test diagnosis, repeated-command classification, explicit no-recreation constraint, and workspace hygiene.
5. Run the interruption/resume scenario only through a safely cancellable DSH operation.
6. Run the eight-turn continuity scenario in one DSH session and identify the first observable loss of constraints or state, if any.
7. Compare the smallest feasible subset against current Agentic Router evidence using the same model. New Router inference must use an isolated application data root and trusted workspace and must not reuse prior dirty/manual-test roots.
8. Analyze which responsibilities DSH demonstrably covers and which Host authorities it does not cover.
9. Record ACP/MCP observations only; do not implement either protocol.
10. Write the required report and end it with exactly one allowed recommendation.

## Stop conditions

- Stop a scenario if DSH escapes the disposable workspace, requests an unconfigured paid/cloud provider, requires destructive authority beyond the scenario, or risks the Router checkout.
- Bound recovery and repeated execution. Do not keep spending GPU time after the observation is already established.
- If DSH cannot expose an auditable headless trace, use the Web UI trace plus filesystem/process evidence and mark unavailable details as unknown.
- Do not repair DSH or Agentic Router during the evaluation. A harness defect is evidence, not an invitation to build a compatibility layer.

## Required report

Create `docs/research/deepseek-harness-evaluation.md` with the requested 18 sections, scenario-level evidence, material tool-call traces, the Router comparison table, explicit unknowns, and exactly one final recommendation from:

- `ADOPT NOW`
- `ADOPT AS EXPERIMENTAL AGENT PROVIDER`
- `CONTINUE EVALUATION`
- `DO NOT ADOPT`

## Validation

- Inspect `git diff --check` and the complete diff for the two evaluation documents.
- Run Markdown/content consistency checks for required sections, evidence labels, source links, and exactly one final recommendation.
- Because this goal changes documentation only, do not spend time rebuilding or rerunning the full application suite unless another repository file changes. Report that limitation explicitly.
