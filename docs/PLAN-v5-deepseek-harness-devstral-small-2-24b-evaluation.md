# PLAN v5: Devstral Small 2 24B in DeepSeek Harness

## Goal

Run an untuned controlled baseline of the exact locally installed `devstral-small-2:latest` artifact through DeepSeek Harness, compare it with the preserved Qwen3-Coder, GPT-OSS, Gemma4, and Qwen3.8 evidence, and produce `docs/research/deepseek-harness-devstral-small-2-24b-evaluation.md`.

This is evidence gathering only. It does not authorize DSH integration, Agentic Router architecture changes, provider adapters, tool-surface changes, model-specific tuning, or GLM testing in this run.

## Preserved baselines

- Qwen3-Coder report: `docs/research/deepseek-harness-evaluation_qwen3-code30b.md`.
- GPT-OSS report: `docs/research/deepseek-harness-gpt-oss20b-evaluation.md`.
- Gemma report: `docs/research/deepseek-harness-gemma4-26b-a4b-it-qat-evaluation.md`.
- Qwen3.8 report: `docs/research/deepseek-harness-qwen3.8-27b-evaluation.md`.
- Reuse the exact 12 committed fixtures, prompts, independent validators, DSH modes, and evidence rules preserved by the Qwen3.8 evaluation.
- New disposable root: `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-devstral24-eval-20260816`.

## Controlled variables

Keep constant:

- DSH `0.1.0-rc.6`, Ollama `0.32.13`, Windows host, RTX 4090, native-tool mode, and `workspace-write` permission mode;
- loopback Ollama OpenAI-compatible provider and disabled DSH telemetry;
- tool catalog, fixture commits, prompts, Git inspection, trace decoding, browser validation, and evidence labels;
- headless execution for isolated scenarios and official DSH Web for continuity and stale-write interleaving.

Change only the selected model to `devstral-small-2:latest`. Record the exact digest, size, architecture, quantization, capabilities, context, loaded placement, renderer/parser, and any compatibility difference from local metadata. Do not infer identity from the tag.

## Evidence and attribution

Use only `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, and `FAILED` for evidence.

Model narration and DSH exit code zero are not acceptance. Filesystem state, Git diff/status, durable native calls, process results, tests, and browser behavior are authoritative.

Classify significant failures as `MODEL`, `HARNESS`, `TOOL SURFACE`, `CONTEXT / SESSION`, or `UNKNOWN`. Classify terminal narration separately as `ACCURATE`, `PARTIALLY ACCURATE`, `MISLEADING`, or `FALSE`.

## Ordered work

1. Resolve exact local model identity and create an isolated DSH home plus fresh clones of the same 12 fixture commits.
2. Run Tests 1-6 and 8-11 headlessly with the exact preserved prompts, recording terminal output, elapsed time, Git state, exact bytes, and durable traces.
3. Run the exact eight-turn counter sequence in one official DSH Web session and validate every accumulated semantic invariant in a real browser.
4. Produce the same stale-write rejection by interleaving the same external edit in one Web session.
5. Aggregate Tests 13-16 from tool discipline, narration, hygiene, and fully-local evidence.
6. Compare all five completed baselines without fabricating unavailable measurements.
7. Write the same 22-section technical Markdown report and end with exactly three verdicts: Devstral verdict, best tested local DSH model, and DSH verdict.

## Stop conditions

- Stop any run that escapes its disposable workspace, attempts cloud inference, risks the Router checkout, or requests destructive authority outside explicit disposable targets.
- Stop a repeated-action loop once established; preserve terminal absence and cancellation evidence.
- Do not tune prompts, schemas, tools, system instructions, adapters, model settings, context configuration, or DSH policy during baseline.
- Do not run Agentic Router inference, application build, E2E tests, integration work, or GLM inference.

## Validation

- Independently validate every requested effect and all workspace changes.
- Verify the required report sections, exact verdict vocabulary, preserved prior-report hashes, whitespace, links, and complete intended documentation delta.
- Because this goal changes documentation only, do not run the application build or E2E suite; state that limitation explicitly.
