# PLAN v7: qwen3-coder 30B under the Codex agent harness

## Goal

Run a five-scenario smoke evaluation of the exact local Ollama artifact `qwen3-coder:30b` through the Codex agent harness and write `docs/research/codex-qwen3-coder-30b-smoke-evaluation.md`.

This is evidence gathering only. It does not authorize changes to Agentic Router, DeepSeek Harness, prior reports, benchmark definitions, or persistent Codex configuration.

## Isolation and authority

- Use a dedicated disposable root outside the Agentic Router checkout.
- Use one fresh Git workspace per isolated scenario and one persistent fresh workspace/session for the five-turn continuity scenario.
- Run every evaluated task in a separate Codex process whose selected provider is local Ollama and whose exact model is `qwen3-coder:30b`.
- Use an isolated `CODEX_HOME`; do not change the user's ordinary Codex configuration.
- Give the evaluated process write access only to its disposable scenario workspace and disable web/network-dependent task tools where supported.
- The orchestrating model may prepare fixtures, inspect traces, validate effects, and write the report, but it may not perform or repair evaluated task effects.
- Filesystem bytes, Git state, process exits, independent tests, and recorded Codex events are authoritative. Model narration is untrusted.

## Controlled scenarios

1. Exact 11-byte `hello.txt` creation in an otherwise empty Git repository.
2. Reuse the existing `fireworks/` implementation without duplication or project recreation.
3. Make the smallest fix to the Node addition fixture, run the relevant test, and stop after success.
4. Change only the existing Notes Save button label and terminate cleanly.
5. Preserve behavior across five turns in one tiny counter application session.

Use the exact prompts and validation rules from the attached goal. Stop after these five scenarios; do not expand to the 12-scenario suite.

## Environment verification

Record:

- Ollama tag, digest, version, model metadata, and observed loaded context/placement when available;
- Codex binary source, signature or provenance, version, provider selection, model selection, sandbox, approval, working directory, and isolated configuration root;
- fixture baseline commit and final Git state for each scenario.

The official Codex configuration supports a built-in `ollama` provider and `--oss` selection. Prefer the packaged Codex binary. If Windows package execution policy prevents launching it in place, copy the same signed binary into the disposable evaluation root and record that compatibility step without changing its contents.

## Evidence and metrics

Classify scenario effects as `PROVEN`, `FAILED`, `PLAUSIBLE`, or `UNKNOWN`.

For each scenario, report objective completion, independently verified effect, tool and failed-tool counts when observable, repeated calls, process executions, redundant executions, changed and unnecessary files, full-file rewrites, stopping, cancellation, and narration accuracy. Do not invent unavailable metrics.

Classify significant findings as model, Codex harness, tool/sandbox surface, context/session, or unknown. Compare like-for-like with the preserved Qwen DSH baseline.

## Stop conditions

- Stop on any attempted escape from a disposable workspace, cloud-model substitution, external publication, or modification to Agentic Router, DSH, prior reports, benchmark definitions, or ordinary Codex configuration.
- Stop a scenario if it enters a demonstrated repeated-action loop; preserve evidence and record whether cancellation was required.
- Stop after five scenarios even if results are inconclusive.

## Validation and report

- Independently validate every requested effect after each Codex process or turn.
- Preserve raw JSONL/stdout/stderr evidence inside the disposable evaluation root where possible.
- Confirm prior reports and tracked Agentic Router files are unchanged by evaluated sessions.
- Write the concise eight-part report requested by the goal.
- Because the repository delta is documentation-only, do not run Agentic Router build or E2E tests; run documentation, link, whitespace, and complete-delta checks instead.
