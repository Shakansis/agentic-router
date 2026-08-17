# PLAN v10: Qwen3.8 Codex continuity gate

## Goal

Run the exact five-turn continuity scenario three independent times with local
`qwen3.8:27b` under the same Codex/Ollama harness configuration used by the
previous Qwen3.8 smoke, then write
`docs/research/codex-qwen3.8-27b-continuity-gate.md`.

This is evidence gathering only. It does not authorize changes to Agentic
Router, DeepSeek Harness, benchmark prompts, model configuration, or prior
reports.

## Isolation and authority

- Use one fresh disposable Git clone and one fresh Codex session for each of
  Trials A, B, and C.
- Start all trials from the same committed baseline containing only the shared
  Git fixture and `assets/celebrate.js`.
- Select exact local Ollama model `qwen3.8:27b`; record its digest and reject any
  substitution.
- Reuse the prior smoke's signed Codex binary, isolated `CODEX_HOME`, explicit
  `ollama` provider, 32,768-token context, `workspace-write` sandbox, and
  `on-request` approval configuration.
- Send the five supplied prompts verbatim, in order, without tuning or retries.
- Do not manually repair evaluated files. Filesystem state, Git state, syntax
  checks, executable DOM behavior, and Codex session records are authoritative;
  model narration is not.

## Trial procedure

For each trial:

1. Clone the clean baseline into a new disposable workspace and verify clean
   Git state.
2. Start a new Codex process with the exact model and Turn 1 prompt.
3. After each completed turn, capture the Git diff, inspect every relevant
   file, run JavaScript syntax checks, and execute an independent DOM fixture.
4. Submit the next prompt only after the independent check; never change the
   prompt in response to observed behavior.
5. After Turn 5, verify all eleven final invariants and capture the final Codex
   narration and session/tool trace.
6. Stop a trial on model substitution, workspace escape, an unsafe approval, or
   a demonstrated repeated-action loop; preserve the failure evidence.

## Acceptance and decision

- `PASS`: all eleven final invariants are independently proven.
- `PARTIAL`: the app is executable but at least one required semantic invariant
  is wrong.
- `FAIL`: the app is broken, the explicit rename fails, exact 10 is unreachable,
  celebration is broadened, prior behavior is lost, or a materially broken
  result is narrated as success.

Classify the combined result as `STRONG` for 3/3 PASS, `PROMISING` for 2/3 PASS,
or `UNSTABLE` for 0/3 or 1/3 PASS. Recommend a small Agentic Router
`CodexHarness` spike only within the decision rule supplied by the goal.

## Report and validation

- Report exact environment and model identity, per-trial metrics, final
  invariant results, important failure traces, run-to-run variance, combined
  classification, limitations, and the integration-spike decision.
- Preserve raw trial evidence under the disposable evaluation root.
- Confirm evaluated sessions did not modify Agentic Router or earlier reports.
- Because the repository change is documentation-only, validate Markdown
  structure, local links, whitespace, and the complete intended diff; do not run
  the Agentic Router build or E2E suite.
- Stop after the report. Do not implement a harness.
