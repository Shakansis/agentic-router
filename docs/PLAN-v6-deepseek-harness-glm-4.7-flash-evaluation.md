# PLAN v6: GLM-4.7-Flash in DeepSeek Harness

## Goal

Install and run an untuned controlled baseline of the official Ollama `glm-4.7-flash:q4_K_M` artifact through DeepSeek Harness, compare it with the preserved Qwen3-Coder, GPT-OSS, Gemma4, Qwen3.8, and Devstral evidence, and produce `docs/research/deepseek-harness-glm-4.7-flash-evaluation.md`.

This is evidence gathering only. It does not authorize DSH integration, Agentic Router architecture changes, provider adapters, tool-surface changes, or model-specific tuning.

## Scenario-count authority

- Execute the 12 committed scenario fixtures used by GPT-OSS, Gemma, Qwen3.8, and Devstral.
- Report the first eight as the shared core also comparable with Qwen3-Coder.
- Treat Tests 13-16 (tool discipline, narration, hygiene, and fully local operation) as aggregate analyses, not additional model executions.
- Do not compare a 12-scenario total directly with Qwen3-Coder's eight-scenario baseline without labeling the subset.

## Preserved baselines

- Qwen3-Coder report: `docs/research/deepseek-harness-evaluation_qwen3-code30b.md`.
- GPT-OSS report: `docs/research/deepseek-harness-gpt-oss20b-evaluation.md`.
- Gemma report: `docs/research/deepseek-harness-gemma4-26b-a4b-it-qat-evaluation.md`.
- Qwen3.8 report: `docs/research/deepseek-harness-qwen3.8-27b-evaluation.md`.
- Devstral report: `docs/research/deepseek-harness-devstral-small-2-24b-evaluation.md`.
- Reuse the exact 12 committed fixtures, prompts, validators, DSH modes, and evidence rules preserved by the latest evaluations.
- New disposable root: `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-glm47flash-eval-20260816`.

## Controlled variables

Keep constant:

- DSH `0.1.0-rc.6`, Ollama `0.32.13`, Windows host, RTX 4090, native-tool mode, and `workspace-write` permission mode;
- loopback Ollama OpenAI-compatible provider and disabled DSH telemetry;
- tool catalog, fixture commits, prompts, Git inspection, trace decoding, browser validation, and evidence labels;
- headless execution for isolated scenarios and official DSH Web for continuity and stale-write interleaving.

Change only the selected model to the official `glm-4.7-flash:q4_K_M` tag. Record exact digest, size, architecture, active/total parameter count when authoritative metadata permits, quantization, capabilities, declared context, loaded context, placement, renderer/parser, and unavoidable compatibility differences. Do not infer identity from the marketing name.

## Evidence and attribution

Use only `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, and `FAILED` for evidence.

Model narration and DSH exit code zero are not acceptance. Filesystem state, Git diff/status, durable native calls, process results, tests, and browser behavior are authoritative.

Classify significant failures as `MODEL`, `HARNESS`, `TOOL SURFACE`, `CONTEXT / SESSION`, or `UNKNOWN`. Classify terminal narration separately as `ACCURATE`, `PARTIALLY ACCURATE`, `MISLEADING`, or `FALSE`.

## Ordered work

1. Pull the official 19 GB Q4_K_M tag and verify its exact local manifest and runtime compatibility.
2. Create an isolated DSH home plus fresh clones of the same 12 fixture commits.
3. Run Tests 1-6 and 8-11 headlessly with exact preserved prompts and independent validation.
4. Run the exact eight-turn counter sequence in one official DSH Web session and validate accumulated behavior in a real browser.
5. Produce the same stale-write rejection by interleaving the same external edit in one Web session.
6. Aggregate Tests 13-16 from tool discipline, narration, hygiene, and fully-local evidence.
7. Compare all six completed baselines without fabricating unavailable measurements.
8. Write the same 22-section technical report and end with exactly three verdicts: GLM verdict, best tested local DSH model, and DSH verdict.

## Stop conditions

- Stop any run that escapes its disposable workspace, attempts cloud inference, risks the Router checkout, or requests destructive authority outside explicit disposable targets.
- Stop a repeated-action loop once established; preserve terminal absence and cancellation evidence.
- Do not tune prompts, schemas, tools, system instructions, adapters, model settings, context configuration, or DSH policy during baseline.
- Do not run Agentic Router inference, application build, E2E tests, or integration work.

## Validation

- Independently validate every requested effect and all workspace changes.
- Verify 12 executed scenario fixtures, the labeled eight-test shared subset, aggregate Tests 13-16, exact report sections, verdict vocabulary, preserved prior-report hashes, whitespace, links, and complete intended documentation delta.
- Because this goal changes documentation only, do not run the application build or E2E suite; state that limitation explicitly.
