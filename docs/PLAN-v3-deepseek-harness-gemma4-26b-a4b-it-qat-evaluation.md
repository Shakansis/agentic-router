# PLAN v3: Gemma4 26B A4B IT QAT in DeepSeek Harness

## Goal

Run a controlled baseline evaluation of `gemma4:26b-a4b-it-qat` in DeepSeek Harness and compare it with the preserved `qwen3-coder:30b` and `gpt-oss:20b` evidence. Change only model identity wherever possible, use fresh disposable clones, and produce `docs/research/deepseek-harness-gemma4-26b-a4b-it-qat-evaluation.md`.

This is evidence gathering only. It does not authorize DSH integration, Agentic Router architecture changes, provider adapters, tool-surface changes, or Gemma-specific tuning.

## Preserved baselines

- Qwen report: `docs/research/deepseek-harness-evaluation_qwen3-code30b.md`.
- GPT-OSS report: `docs/research/deepseek-harness-gpt-oss20b-evaluation.md`.
- Shared fixture commits and prompts remain those recorded in the GPT-OSS plan/report.
- New disposable root: `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-gemma4-eval-20260816`.

## Controlled variables

Keep constant:

- DSH `0.1.0-rc.6`, Ollama `0.32.13`, Windows host, GPU, native-tool mode, and `workspace-write` permission mode;
- Ollama loopback OpenAI-compatible provider and DSH telemetry disabled;
- fixtures, prompts, tool surface, Git inspection, trace decoding, browser validation, and proof labels;
- execution outside Codex's enclosing sandbox when DSH must apply its own Windows sandbox.

Change only the selected model to `gemma4:26b-a4b-it-qat`. Record model metadata, declared and loaded context, placement, throughput, or transport incompatibility as unavoidable differences.

## Evidence and attribution

Use only `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, and `FAILED`.

Model text and DSH exit code zero are not acceptance. Filesystem state, Git diff/status, native calls, actual process results, and browser behavior are authoritative.

Classify significant failures as `MODEL`, `HARNESS`, `TOOL SURFACE`, `CONTEXT / SESSION`, or `UNKNOWN`. Use context attribution only when persisted evidence shows the information was available and later ceased to be represented effectively.

## Ordered work

1. Verify exact Gemma tag, digest, capabilities, metadata, declared context, and availability.
2. Create a separate DSH home and fresh clones of all 12 committed fixtures without changing prompts, tools, or policies.
3. Run Tests 1-6 and 8-11 headlessly, capturing time, calls, error results, terminal state, Git state, and independent acceptance.
4. Run the exact eight-turn counter sequence in one official DSH Web session and validate composite behavior in a real browser.
5. Produce the stale-write rejection by interleaving a fixture mutation between read and edit in one Web session.
6. Aggregate tool-surface discipline, workspace hygiene, and fully-local runtime evidence as Tests 13-15.
7. Compare all three model reports, separating useful calls, malformed calls, repeated work, correctness, stopping, continuity, runtime, and resource cost.
8. Write the required 20-section report and end with exactly one Gemma verdict and one DSH verdict.

## Stop conditions

- Stop any run that escapes its disposable workspace, attempts cloud inference, risks the Router checkout, or requests destructive authority outside the explicit disposable targets.
- Stop a repeated-action loop once its behavior is established and preserve terminal absence/cancellation evidence.
- Do not tune prompts, schemas, model settings, tools, or DSH policy during the baseline.
- Do not run Agentic Router inference, E2E tests, or integration work.

## Validation

- Verify both prior report hashes remain unchanged.
- Verify all 20 required report sections exist.
- Verify exactly one permitted Gemma verdict and one permitted DSH verdict are the final verdict lines.
- Run whitespace/link/structure checks and inspect the complete intended documentation changes.
- State unexecuted network-level proof and any unavailable performance metrics explicitly.
