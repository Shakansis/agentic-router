# PLAN v2: GPT-OSS 20B in DeepSeek Harness

## Goal

Run a controlled A/B comparison of `gpt-oss:20b` and the preserved `qwen3-coder:30b` DeepSeek Harness baseline. Change only the model wherever possible, add the four requested capability/recovery scenarios, and determine whether GPT-OSS is materially more reliable inside the same DSH runtime.

This is evidence gathering only. It does not authorize DSH integration, Agentic Router architecture changes, new compatibility layers, or tuning around first-pass failures.

## Preserved baseline

- Qwen report, unchanged content after rename: `docs/research/deepseek-harness-evaluation_qwen3-code30b.md`.
- Preserved SHA-256: `1B5A34754C391FE0903C490796E99BC0041FD3F08966EF3EE05F40B06682BFB2`.
- New report: `docs/research/deepseek-harness-gpt-oss20b-evaluation.md`.
- Qwen disposable baselines remain under `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-eval-20260815\baselines`.

## Controlled variables

Keep constant:

- DSH `0.1.0-rc.6` and official Web/headless profiles;
- Windows host, Ollama `0.32.13`, GPU, DSH native-tool mode, and `workspace-write` permission mode;
- hard-disabled DSH telemetry and no cloud-provider inference;
- Qwen fixture contents, commits, prompts, Git validation, trace decoder, and independent postconditions;
- fair execution outside Codex's enclosing sandbox when DSH must apply its own Windows ACL/process sandbox.

Change only the model selection from `qwen3-coder:30b` to `gpt-oss:20b`. Record any unavoidable difference, especially model metadata, loaded context, token throughput, or provider compatibility.

## Evidence and attribution

Use only `PROVEN`, `PLAUSIBLE`, `SPECULATIVE`, and `FAILED`.

Model narration and exit code zero are not acceptance. Git state, exact file bytes, process result, independent tests, and persisted native calls are authoritative.

For each material failure assign the most supported layer:

- `MODEL`: the response chose or repeated an invalid action or lost a constraint;
- `HARNESS`: DSH mishandled a valid call/result or terminal state;
- `TOOL SURFACE`: the necessary capability was absent or the exposed catalog created the gap;
- `UNKNOWN`: the persisted evidence cannot distinguish the layer.

## Ordered work

1. Verify exact `gpt-oss:20b` tag, digest, capabilities, metadata, and availability without loading another model unnecessarily.
2. Create a separate DSH home and disposable run root for GPT-OSS. Copy the Qwen settings shape and replace only provider model identity.
3. Fresh-clone Qwen baselines for Tests 1-6 and 8. Add committed fixtures for delete gap, shell fallback, Windows paths, and stale write.
4. Run Tests 1-6 and 8 once through headless DSH, capturing elapsed time, stdout, exit, durable events, Git status/diff, and independent validation.
5. Run the eight-turn continuity sequence in one official DSH Web session and validate cross-file behavior after each completed turn where practical.
6. Run the delete-gap test without adding tools or special instructions. Empty files do not satisfy deletion.
7. Run the capability-gap and Windows-path tests without manually repairing model calls.
8. Produce stale state after the model read and before its write using an external fixture change. Preserve both the original task and the external content as acceptance conditions.
9. Collect hygiene and local-runtime facts after every run. Do not claim packet-level offline proof without packet/network enforcement.
10. Compare every shared scenario against the preserved Qwen report and distinguish model, Harness, tool-surface, and unknown causes.
11. Write the required 20-section report and end with exactly one model verdict followed by exactly one DSH verdict.

## Stop conditions

- Stop any run that escapes its disposable workspace, attempts cloud inference, risks the Router checkout, or requests destructive authority outside the explicit disposable-file list.
- Stop a repeated-action loop once the behavior is established; preserve its trace and terminal absence/cancellation as evidence.
- Do not modify DSH configuration, prompts, tools, or templates after a baseline failure. Any later mitigation must be explicitly `TUNED / NON-BASELINE` and remain separate.
- Do not run Agentic Router inference or its E2E suite; the requested A/B is between models inside DSH and the Qwen DSH evidence already exists.

## Validation

- Verify the Qwen report hash remains unchanged.
- Verify all 20 report sections are present.
- Verify exactly one allowed model verdict and one allowed DSH verdict appear as the final two verdict lines.
- Run `git diff --check` and inspect the complete intended documentation changes.
- Report unavailable metrics and unexecuted network-level proof explicitly.
