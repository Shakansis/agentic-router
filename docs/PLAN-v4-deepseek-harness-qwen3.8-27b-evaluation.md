# PLAN v4: Qwen3.8 27B in DeepSeek Harness

## Goal

Run an untuned controlled baseline of the actual locally installed `qwen3.8:27b` artifact through DeepSeek Harness, compare it with the preserved Qwen3-Coder, GPT-OSS, and Gemma4 evidence, and produce `docs/research/deepseek-harness-qwen3.8-27b-evaluation.md`.

This is evidence gathering only. It does not authorize DSH integration, Agentic Router architecture changes, provider adapters, tool-surface changes, or Qwen3.8-specific tuning.

## Preserved baselines

- Qwen report: `docs/research/deepseek-harness-evaluation_qwen3-code30b.md`.
- GPT-OSS report: `docs/research/deepseek-harness-gpt-oss20b-evaluation.md`.
- Gemma report: `docs/research/deepseek-harness-gemma4-26b-a4b-it-qat-evaluation.md`.
- Reuse the exact 12 committed fixtures, prompts, and independent validators preserved by the Gemma evaluation.
- New disposable root: `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-qwen38-eval-20260816`.

## Controlled variables

Keep constant:

- DSH `0.1.0-rc.6`, Ollama `0.32.13`, Windows host, RTX 4090, native-tool mode, and `workspace-write` permission mode;
- loopback Ollama OpenAI-compatible provider and disabled DSH telemetry;
- tool catalog, fixture commits, prompts, Git inspection, trace decoding, real-browser validation, and evidence labels;
- headless execution for isolated scenarios and official DSH Web for continuity and stale-write interleaving.

Change only the selected model to `qwen3.8:27b`. Record the actual local artifact identity and any renderer/parser, context, placement, or compatibility difference. Do not infer architecture from the tag.

## Evidence and attribution

Use only `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, and `FAILED` for evidence.

Model narration and DSH exit code zero are not acceptance. Filesystem state, Git diff/status, durable native calls, process results, tests, and browser behavior are authoritative.

Classify significant failures as `MODEL`, `HARNESS`, `TOOL SURFACE`, `CONTEXT / SESSION`, or `UNKNOWN`. Classify final narration separately as `ACCURATE`, `PARTIALLY ACCURATE`, `MISLEADING`, or `FALSE`.

## Ordered work

1. Resolve the exact tag, full digest, blob provenance, size, architecture, quantization, capabilities, declared context, and renderer/parser from local Ollama metadata.
2. Create a separate DSH home and fresh clones of the same 12 fixture commits without changing prompts, tools, context policy, or permissions.
3. Run Tests 1-6 and 8-11 headlessly, preserving call/error/terminal/timing evidence and independently validating every effect.
4. Run the exact eight-turn counter sequence in one official DSH Web session and validate relevant intermediate and final behavior in a real browser.
5. Produce the stale-write rejection by interleaving an external fixture mutation between the model's read and first usable edit in one Web session.
6. Aggregate tool discipline, self-verification, workspace hygiene, and fully-local evidence as Tests 13-16.
7. Compare all four completed baselines without fabricating unavailable useful-call, elapsed-time, or throughput metrics.
8. Write the required 22-section technical Markdown report and end with exactly the three requested verdicts.

## Stop conditions

- Stop any run that escapes its disposable workspace, attempts cloud inference, risks the Router checkout, or requests destructive authority outside explicit disposable targets.
- Stop a repeated-action loop once the behavior is established; preserve terminal absence and cancellation evidence.
- Do not tune prompts, schemas, tools, system instructions, adapters, model settings, context configuration, or DSH policy during baseline.
- Do not run Agentic Router inference, build, E2E tests, or integration work.

## Report shape and validation

- Technical audience, Markdown delivery only, because the user specified the repository path.
- Use exact lookup tables rather than charts where a chart would obscure scenario-level evidence.
- Map the technical-report structure across the requested 22 visible sections: answer first, environment/model/method definitions, scenario evidence, limitations/attribution, implications, and next experiments.
- Verify all 22 sections, exact final verdict vocabulary, prior report hashes, whitespace, links, and the complete intended documentation delta.
- State network-level isolation and non-comparable timing/throughput limitations explicitly.
