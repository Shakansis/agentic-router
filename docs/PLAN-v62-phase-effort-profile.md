# PLAN v62 — Host-owned phase effort profile

Status: implemented and deterministically validated.

## Objective

Let the Host select a bounded `low`, `medium`, or `high` effort target for each
supervised execution phase while preserving one model + harness route, existing
approval semantics, trusted-workspace boundaries, recovery budgets, and Host
verification.

Benchmark integration is intentionally deferred to a separate change.

## Product contract

The persisted defaults are:

| Phase | Default effort |
| --- | --- |
| PLAN | `high` |
| WORK | `medium` |
| VERIFY | `medium` |
| COMPLETE | `low` |
| RECOVERY | `high` |

`low` never means disabled reasoning. Unknown values are rejected atomically.
The Host records the requested effort; adapters translate it without changing
security, approval, capability, validation, or recovery policy.

## Adapter mappings

| Route | Mapping |
| --- | --- |
| Native + Ollama | Native `/api/chat` `think` string plus concise Host guidance. |
| Codex | `turn/start.effort` and a Host-generated three-level model catalog. |
| OpenCode | Explicit model variants whose bodies send `reasoning_effort`. |
| Qwen Code | Per-session `reasoning_effort` config option before each prompt. |
| Claude Code + Ollama | Prompt-guided only because Ollama's Anthropic compatibility does not expose a reviewed effort field. |

Each external adapter emits visible activity identifying native/translated or
prompt-guided application. A provider or interlock rejection remains typed and
recoverable through the existing execution loop; no identical hidden retry is
introduced.

## Implementation slices

1. Add validated phase settings and Settings > General selectors.
2. Propagate the selected value through the specialist and harness turn contracts.
3. Apply the adapter mappings above and use RECOVERY after an observed failure or discrepancy.
4. Add deterministic browser/API tests that inspect actual provider and harness payloads.
5. Run format, Release build, affected E2E classes, and the complete deterministic suite.

## Deferred benchmark work

A later plan will decide whether effort is one selected configuration per
Model × Harness run or a third matrix dimension. This change does not version,
score, rank, or render benchmark evidence by effort.
