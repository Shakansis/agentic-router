# Milestone 10 Model x Harness matrix report

## Implementation

Milestone 10 extends the Milestone 9 runner without replacing its fixtures,
prompts, validators, scoring, persistence, live coordinator, or harness
adapters.

- A suite request accepts one or more installed local models and one or more
  registered harnesses, plus suite, timeout, scoring profile, and score weights.
- The engine creates the Cartesian product in stable model-then-harness order
  and executes local cells sequentially.
- Every executable cell reuses the existing per-test fresh workspace and
  independent harness session path. A cell failure, timeout, cancellation,
  unsupported provider, unavailable model, or unavailable harness is recorded
  without model substitution.
- Schema v2 persists selections, exact model identities, harness versions,
  cells with raw test evidence, execution order, runtime identity, score
  profile, pair/model/harness rankings, and compatibility/final status.
- Historical schema v1 CRUD and Agent Behavior results remain readable.
- Rescoring projects every matrix cell and all three ranking scopes from raw
  evidence without rerunning inference.
- The existing benchmark page now has multi-model selection, a scoring-profile
  selector, cell-aware live progress, a compact readable matrix, inspectable
  cells, and pair/model/harness ranking views.

## Automated validation

- Focused M10 and historical compatibility set: 9 tests, 8 passed initially;
  the single presentation regression (`4` versus `4/4`) was corrected and its
  two UI tests then passed 2/2.
- Complete browser/API E2E suite: **300/300 passed**.
- Release solution build with isolated artifacts and `--no-restore`: **passed,
  zero warnings and zero errors**.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed.
- `node --check AgenticRouter.Api/wwwroot/app.js`: passed.
- `git diff --check`: passed.

The exact non-isolated Release build could not replace the running user's
`AgenticRouter.Api.exe`; that process was preserved. The equivalent isolated
solution build passed.

## Real matrix validation

Run ID: `0e41ca2b572948779daeb4ac0822c605`

Models:

- `qwen3.8:27b-gpu0`, digest
  `d1f9a27632f9cab927948254285394838a1f0e0f8b7e70e53d633624ed9e9169`,
  Q4_K_M, 27.3B.
- `qwen3:4b-instruct`, digest
  `0edcdef34593eac1aa2be9c7d06c432dcf81945adca5eca2f27662c18f168ba0`,
  Q4_K_M, 4.0B.

Harnesses: Native, Codex `0.149.0-alpha.4.1`, OpenCode `1.18.18`, Qwen
Code `0.21.13`, and Claude Code `2.1.234`.

The Basic CRUD v1 matrix produced all 10 expected cells in 8m05s. Seven cells
completed and three recorded real failures; later cells continued after every
failure.

| Rank | Model | Harness | Status | Passed | Score |
| ---: | --- | --- | --- | ---: | ---: |
| 1 | qwen3:4b-instruct | Claude Code | Completed | 4/4 | 99.75 |
| 2 | qwen3.8:27b-gpu0 | Claude Code | Completed | 4/4 | 99.13 |
| 3 | qwen3:4b-instruct | Codex | Completed | 4/4 | 99.00 |
| 4 | qwen3.8:27b-gpu0 | Codex | Completed | 4/4 | 99.00 |
| 5 | qwen3.8:27b-gpu0 | Qwen Code | Completed | 4/4 | 98.56 |
| 6 | qwen3.8:27b-gpu0 | OpenCode | Completed | 4/4 | 98.25 |
| 7 | qwen3:4b-instruct | Native | Failed | 3/4 | 81.00 |
| 8 | qwen3.8:27b-gpu0 | Native | Failed | 3/4 | 80.69 |
| 9 | qwen3:4b-instruct | OpenCode | Completed | 2/4 | 69.06 |
| 10 | qwen3:4b-instruct | Qwen Code | Failed | 0/4 | 32.38 |

Independent checks proved:

- 10/10 cells persisted with exact execution order and inspectable evidence;
- every test received a unique workspace and all workspaces were cleaned;
- every retained live lifecycle event identified model and harness;
- the persisted Host runtime recorded sequential execution;
- the bounded final live window retained sequences 108-619 and directly proved
  non-overlap for cells 3-10; starts for the first two cells had aged out of the
  512-event window;
- pair/model/harness rankings contained 10/2/5 entries;
- changing score weights changed ranking order through rescoring only, and the
  original scoring profile was restored;
- no cloud fallback occurred.

Raw evidence is under
`docs/validation/m10-real-matrix-2026-08-23-run-01`.

