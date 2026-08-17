# Qwen3.8 27B Codex continuity gate

Date: 2026-08-16

Evaluation plan: [`docs/PLAN-v10-codex-qwen3.8-continuity-gate.md`](../PLAN-v10-codex-qwen3.8-continuity-gate.md)

Previous smoke: [`codex-qwen3.8-27b-smoke-evaluation.md`](./codex-qwen3.8-27b-smoke-evaluation.md)

## Technical summary

**Result: STRONG, 3/3 PASS.** In three independent fresh Codex sessions and
fresh Git workspaces, `qwen3.8:27b` completed all five exact turns and preserved
all eleven final invariants. The explicit `value` to `score-value` rename was
correct in HTML, CSS, and JavaScript every time. Increment changed to `+2`, exact
10 remained reachable, celebration triggered at 10, and an odd-state path could
return to the milestone through `9 -> 8 -> 10`. Independent execution also
proved that reaching 11 without passing through 10 did not celebrate.

This clears the narrow continuity gate that failed in the earlier smoke. Per the
predefined decision rule, **a small experimental Agentic Router `CodexHarness`
spike is justified**. It does not justify broad integration or autonomous
acceptance: Codex exposed only a shell action surface in these sessions, caused
four failed patch attempts across the gate, and induced 13 full-file rewrites of
an existing `index.html` across 12 mutation turns.

## Exact environment and model

| Item | Observed value |
|---|---|
| OS | Windows 10 Pro 22H2, build `19045.6466`, x64 |
| GPU | NVIDIA GeForce RTX 4090, 24,564 MiB |
| Ollama | `0.32.13`, loopback `http://127.0.0.1:11434` |
| Model argument | Exact `qwen3.8:27b`; no substitution |
| Ollama digest | `22130167c4c20e20c7b71454612966ca8e8171e9b3cc8ab6ce8aa6cbfec79643` |
| Model artifact | 17,741,872,154 bytes; `qwen35`, 27.3B, `Q4_K_M` |
| Loaded placement | 17 GB, 100% GPU, context 32,768 |
| Codex | `codex-cli 0.147.0-alpha.6.6`, signed by OpenAI OpCo, LLC |
| Codex SHA-256 | `592958896CBFFA154709618476FC9C9BF7FE73957E9A4FC12094C5051B6C69B3` |
| Launch selection | `--oss --local-provider ollama -m qwen3.8:27b` |
| Context / sandbox / approval | 32,768 / `workspace-write` / `on-request`, user reviewer |
| Baseline commit | `5e97f16834cad14754b752f5799e1b41347335de` |
| Sessions | A `01a00d2c-ff0f-7361-b8e9-fe8a2d0e8c2e`; B `01a00d33-a860-7bd1-9ea7-bb81c0b8b3e9`; C `01a00d38-6ddf-75e2-9a1f-bf8bd76992c4` |

The official Codex configuration reference identifies `ollama` as a built-in
provider, documents `model_context_window`, and defines the selected
`workspace-write` and `on-request` controls
([OpenAI Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)).

## Scope and validation method

Each trial cloned the same clean commit containing only the fixture
`assets/celebrate.js`, then ran the five supplied prompts verbatim in one fresh
Codex session. There was no prompt tuning, retry of a turn, model-specific hint,
or manual repair. Trust confirmation occurred before the evaluated task; `Ctrl+C`
was used only after the fifth `task_complete` to exit the idle TUI and is not a
model-task cancellation.

After every turn, validation inspected Git state and relevant files, compiled the
effective JavaScript, and executed it against a fresh deterministic DOM fixture.
For module turns, the fixture executed the real body of the committed
`assets/celebrate.js`; the final probe instrumented that function only in memory
to count calls. Final paths proved:

- basic behavior: `0 -> 2 -> 1 -> 0` through increment, decrement, and reset;
- milestone: `0 -> 2 -> 4 -> 6 -> 8 -> 10`, with one celebration call;
- recovery from an odd state: `10 -> 9 -> 8 -> 10`, with a second call;
- no broadening: `0 -> -1 -> 1 -> 3 -> 5 -> 7 -> 9 -> 11`, with zero calls.

Model narration and Codex lifecycle state were not used as behavioral evidence.

## Three trial results

| Trial | Turns 1-5 | Final result | Calls / failed | Existing-file full rewrites | Session tokens | Final narration |
|---|---|---:|---:|---:|---:|---|
| A | PASS / PASS / PASS / PASS / PASS | **PASS** | 18 / 3 | 5 | 268,699 | Accurate |
| B | PASS / PASS / PASS / PASS / PASS | **PASS** | 13 / 1 | 4 | 222,521 | Accurate |
| C | PASS / PASS / PASS / PASS / PASS | **PASS** | 17 / 0 | 4 | 246,792 | Accurate |

All 48 actions were native Codex `shell_command` function calls. No textual
pseudo-tool action, unsafe approval, workspace escape, repeated-action loop, or
manual cancellation occurred. The three runs consumed 738,012 session tokens;
these totals include Codex context traffic and are not a throughput benchmark.

### Required per-trial metrics

| Metric | Trial A | Trial B | Trial C |
|---|---|---|---|
| All five turns completed | Yes | Yes | Yes |
| Native actions only | Yes | Yes | Yes |
| Manual cancellation required | No | No | No |
| Final app executable | Yes | Yes | Yes |
| Rename correct | Yes | Yes | Yes |
| Increment-by-2 correct | Yes | Yes | Yes |
| Exact-10 celebration preserved | Yes | Yes | Yes |
| Exact 10 reachable | Yes | Yes | Yes |
| Previous behaviors preserved | Yes | Yes | Yes |
| Unnecessary files created | 0 | 0 | 0 |
| Existing files unnecessarily rewritten | `index.html` 5x | `index.html` 4x | `index.html` 4x |
| Tool calls | 18 | 13 | 17 |
| Failed calls | 3 | 1 | 0 |
| Final narration accurate | Yes | Yes | Yes |

## Final invariant matrix

`PROVEN` means independently established from file/Git state and executable
behavior after Turn 5.

| Final invariant | Trial A | Trial B | Trial C |
|---|---|---|---|
| Title is `Continuity App` | PROVEN | PROVEN | PROVEN |
| Displayed id is `score-value` | PROVEN | PROVEN | PROVEN |
| Increment adds 2 | PROVEN | PROVEN | PROVEN |
| Decrement subtracts 1 | PROVEN | PROVEN | PROVEN |
| Reset returns to 0 | PROVEN | PROVEN | PROVEN |
| Existing `assets/celebrate.js` is reused | PROVEN | PROVEN | PROVEN |
| Celebration predicate remains exactly `count === 10` | PROVEN | PROVEN | PROVEN |
| Exact 10 is reachable | PROVEN | PROVEN | PROVEN |
| Celebration triggers at 10 | PROVEN | PROVEN | PROVEN |
| No duplicate celebration implementation exists | PROVEN | PROVEN | PROVEN |
| No prior required behavior was lost | PROVEN | PROVEN | PROVEN |

The old live identifier was absent in every final app. The identifier-like local
variables (`valueElement` or `valueEl`) in Trials B and C were not DOM identifiers
and did not violate the requested `id` rename.

## Failure traces and run-to-run variance

There were no semantic failures. The only failed calls were tool-surface failures:

```text
Trial A, Turn 2: apply_patch as shell -> Access is denied
Trial A, Turn 2: JSON-shaped apply_patch command -> PowerShell parser error
Trial A, Turn 2: array-shaped apply_patch command -> PowerShell parser error
Trial B, Turn 1: apply_patch passed as shell text -> exit 1
Trial C: no failed calls
```

Qwen3.8 recovered each time with PowerShell full-file writes. This is a Codex
tool-surface mismatch plus model recovery behavior, not evidence against the
continuity result. It does matter for a future harness spike: a structured patch
primitive or a clear supported edit contract would reduce failed calls and broad
rewrites.

Final implementations varied in markup and styling (`h1`, `p`, or `div`; 64-98
lines), but converged on the same semantic structure: one `index.html`, one import
of the committed fixture, one exact equality guard, and one `+2` increment. Tool
calls ranged from 13 to 18, failed calls from 0 to 3, full rewrites from 4 to 5,
and session tokens from 222,521 to 268,699. Semantic variance was zero in this
three-run sample; operational variance was material.

## Limitations and decision

This is a targeted three-trial gate, not a general coding benchmark, long-session
test, Agentic Router code review, or proof of production reliability. The DOM
fixture establishes event and module behavior but is not a browser compatibility
matrix. All runs used one exact quantized artifact, one host, and one Codex build.

**Classification: STRONG (3/3 PASS).** The explicit prompt removed the ambiguity
from the earlier smoke and Qwen3.8 consistently preserved the cross-turn and
cross-reference invariants. A small `CodexHarness` spike is therefore justified,
bounded to an experimental provider and Host-verified effects. The useful next
question is whether the same model can review a small real Agentic Router diff
without writing files or overstating findings. Broad replacement of Agentic
Router Native is not supported by this gate.
