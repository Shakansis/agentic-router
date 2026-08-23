# Pre-M10 real harness validation report

## Gate decision

**PASS.** All five active harness adapters executed the exact local model through
the real Agentic Router path. The benchmark infrastructure is stable and no
unresolved Agentic Router defect remains that would invalidate Model x Harness
results. Milestone 10 may proceed.

## Fixed identity

- Model tag: `qwen3.8:27b-gpu0`
- Digest: `d1f9a27632f9cab927948254285394838a1f0e0f8b7e70e53d633624ed9e9169`
- Size: `17741872184` bytes
- Quantization: `Q4_K_M`
- Model metadata context: `262144`; configured `num_ctx`: `131072`
- Provider/runtime: local Ollama; `main_gpu 0`
- Cloud fallback: disabled
- Execution: sequential, disposable workspaces and independent sessions

Harness versions were Native built-in, Codex `0.149.0-alpha.4.1`, OpenCode
`1.18.18`, Qwen Code `0.21.13`, and Claude Code `2.1.234`.

## Latest authoritative suite evidence

| Harness | Basic CRUD v1 | Agent Behavior v2 | Boundary recovery | Cancellation |
| --- | ---: | ---: | --- | --- |
| Native | 3/4, 80.69 | 6/7, 87.20 | passed | passed in Run 16 |
| Codex | 4/4, 99.00 | 7/7, 99.07 | passed in Run 9 | passed in Run 16 |
| OpenCode | 4/4, 98.81 | 7/7, 98.59 | passed in Run 10 | passed in Run 4 |
| Qwen Code | 4/4, 98.31 | 5/7, 71.09 | passed in Run 10 | passed in Run 4 |
| Claude Code | 4/4, 99.31 | 4/7, 95.20 | passed | passed in Run 4 |

All latest affected-suite runs reported complete workspace cleanup and absent
disposable workspace paths. First-run failures and every numbered retest remain
preserved under `docs/validation/pre-m10-real-2026-08-23*`.

## Agentic Router and benchmark defects corrected

1. OpenCode benchmark-owned server/workspace handles survived suite completion.
   Cleanup now honors release flags and stops the owned server.
2. Codex, OpenCode, and Qwen Code native reads or structured process arguments
   could reach a path outside the trusted root. Filesystem reads now require
   Host-resolved paths, structured process path arguments are validated, and
   affected native filesystem surfaces route through the Host bridge.
3. Codex on native Windows could not enforce split read permissions through a
   custom sandbox profile. It now uses the supported workspace permission
   profile with native shell execution disabled; Host tools retain the bounded
   filesystem and process authority.
4. Qwen Code native edits depended on a prior native read after reads moved to
   the Host bridge. Its benchmark filesystem mutations now use Host tools too.
5. The boundary assessment initially accepted an unchanged workspace without
   requiring an observed rejection. It now requires the rejection event,
   recovery result, absence of forbidden content, and unchanged protected state.
6. Agent Behavior convergence counted the MCP envelope and Host action as two
   semantic calls, and pathless OpenCode edit events hid the mutation boundary.
   The validator now normalizes bridge envelopes and recognizes the pathless
   mutation event without weakening the one-read/no-follow-up criterion. Run 15
   proves 7/7 with `postMutationValidationReads=1` and
   `toolCallsAfterSuccess=0`.

## Non-blocking model/harness behavior

- Native missed one Basic CRUD objective and one v2 objective.
- Qwen Code timed out in `RECOVERY-001` and `CONVERGENCE-001` in Run 12. Runtime
  evidence shows valid exact-model responses and Host tool execution before the
  bounded scenario deadlines; cleanup remained complete.
- Claude Code's remaining v2 failures and the earlier Native failures are
  measured behavioral results, not infrastructure failures.
- Run 13 is a preserved environment failure: the isolated API was accidentally
  started inside the sandbox, so its local bridge `HttpListener` could not open.
  Run 14/15 outside the sandbox proved the product path.

## Deterministic validation at this gate

- Isolated Release E2E build: succeeded, zero warnings and zero errors.
- Focused boundary/harness/live benchmark set: 6/6 passed.
- Cross-harness Agent Behavior v2 proof after trace normalization: 1/1 passed.
- Real OpenCode targeted retest: 7/7 passed, score 98.59.
- Real Native/Codex cancellation retest: both passed with HTTP 202, terminal
  `cancelled`, and persisted HTTP 200 results.

