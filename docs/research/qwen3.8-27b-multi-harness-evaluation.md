# Qwen3.8 27B multi-harness evaluation

Date: 2026-08-18

Model: `qwen3.8:27b`

Current manifest digest: `a5dfec209c3bd4a76748c0d493e596d15f8c84f5fcc255b4072f1e01a863fb9b`

## Decision

No evaluated harness makes `qwen3.8:27b` trustworthy as autonomous Agentic Router execution authority. The model remains useful as an advisory reviewer only when the Host independently proves every important claim, edit, validation result, workspace invariant, and terminal state.

For the four harnesses measured in this run, the evidence-weighted order requested for this evaluation is:

1. **OpenCode** — the only current harness with normal terminality in all 12 scenarios; 10/12 full outcomes, but it silently broadened the long-session celebration rule, missed the requested ID rename, and staged Test 9 deletions without being asked.
2. **Qwen Code** — the strongest current correctness result at 11/12 and the only exact 11-byte Test 1 result; its last three Test 7 turns each exhausted the 300-second external limit, so its convergence is not acceptable without a Host-owned budget.
3. **Codex** — 9/12 full outcomes; it recovered many inaccessible-patch failures, but failed exact bytes, lost one continuity requirement, and spent 300 seconds reasoning around an unavailable patch path without completing the stale edit.
4. **Goose** — 8/12 full outcomes; it timed out after a correct Test 2 effect, failed to apply the Test 5 fix, and broadened the exact-five rule to `>= 5`.

The broader six-harness indicative order is **OpenCode → Qwen Code → DSH → Claude Code → Codex → Goose** under the required priority `terminality → useful outcome → correctness → recovery → convergence → hygiene → truthful report → efficiency`. DSH's position is qualified: its preserved evidence used the earlier digest/runtime and is not directly comparable for latency, memory, or exact run-time behavior. Claude Code used the same current digest, runtime, hardware, fixtures, and prompts as this run and is directly comparable on functional outcomes.

This is one controlled run per scenario, not a statistical reliability estimate.

## Headline comparison

| Harness | Normal terminality | Fully accepted | Native calls / surfaced errors | Measured duration | Most important evidence |
|---|---:|---:|---:|---:|---|
| OpenCode | **12/12** | **10/12** | 84 / 3 | 792.4 s | Never hung; failed Test 1 bytes and Test 7 semantics; staged Test 9 deletions. |
| Qwen Code | 11/12 | **11/12** | 91 / 8 | 1,923.5 s | Best correctness and exact bytes; three consecutive Test 7 timeouts. |
| DSH | 11/12 | 10/12 | 103 / 2 | about 764.5 s | Correct Test 7 effect but no terminal answer; wrong Test 1 LF. Earlier digest/runtime. |
| Claude Code | 11/12 | 10/12 | 118 / 23 | about 1,279 s | Correct long session; Test 3 correct effect followed by permission loop, extra file, cancellation. |
| Codex | 11/12 | 9/12 | 91 / 15, plus 22 metadata warnings | 1,433.7 s | Failed bytes, semantic rename, and stale edit; patch path repeatedly inaccessible. |
| Goose | 11/12 | 8/12 | 64 / 8 | 805.3 s | Lowest call count did not imply reliability: one timeout, one unfixed build, and wrong continuity semantics. |

The error counts are descriptive, not a ranking metric. Each harness reports failures differently, and several errors were useful and automatically recovered. Task failure and non-termination carry more weight than recovered internal errors.

## Scope and controls

The four new measurements used:

- the same 12 fixture commits and canonical prompts as the earlier DSH and Claude Code evaluations;
- one fresh disposable clone/session per independent scenario;
- persistent sessions only for Test 7 and Test 12, as required by the original method;
- the exact current Ollama tag and digest above, verified before each harness block;
- Ollama `0.32.14` at `http://127.0.0.1:11434`;
- the same Windows host, RTX 4090 plus RTX 2070 SUPER hardware, fixtures, validation, and 300-second per-turn external limit;
- no prompt tuning after behavior was observed;
- no failed benchmark rerun for a better outcome;
- independent Git, byte, Node, and real-browser validation rather than accepting model narration.

The current manifest changes `draft_num_predict` from the earlier artifact's `4` to `0`, following the operator's requirement to disable behavior newly enabled by Ollama while retaining the original effective model behavior. The current four harnesses and Claude Code used digest `a5dfec...`. DSH used preserved digest `22130167...` under Ollama `0.32.13`; its correctness evidence remains useful, but performance and byte-identical runtime claims are excluded.

The current runs all used local Ollama inference. No cloud model, fallback model, MCP server, or subagent was observed. Codex's repeated “fallback metadata” message refers to missing local model metadata, not a model fallback; Ollama `/api/ps` still showed the exact requested digest.

Authoritative current evidence is under:

`C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-qwen38-multiharness-20260818-r2`

That root contains the measured manifest, prompt hashes, per-turn transcripts, timings, final diffs, final snapshots, and all 48 disposable workspaces.

The preserved comparison reports were read-only inputs. Their before/after SHA-256 values remained:

- DSH: `E14E8B5A4770095403E6BF02684252B96C00B5302C7B2D3BED779E4875A0CD0E`;
- Claude Code: `84E96998129DB17DF59E32A2A54877B6F187BD5E2B9BF2E24865FC7E19E38D7E`.

## Harness versions and local configuration

| Harness | Version | Local configuration |
|---|---|---|
| Codex | `codex-cli 0.148.0-alpha.9` | Isolated `CODEX_HOME`; Ollama provider; `workspace-write`; approval `never`; web, apps, plugins, remote plugins, skills, and multi-agent disabled. |
| Qwen Code | `0.21.13` | Isolated `QWEN_HOME`; OpenAI-compatible Ollama loopback; YOLO permission mode; telemetry off; intended core file/search/shell tools only. |
| OpenCode | `1.18.18` | Isolated XDG roots; custom Ollama OpenAI-compatible provider; pure/automatic mode; plugins and instructions empty; sharing and updates off. |
| Goose | `1.46.0` | Isolated `GOOSE_PATH_ROOT`; local Ollama provider; automatic mode; telemetry off; built-in developer extension only. |

The configurations follow the supported local integration surfaces documented by [Ollama for Codex](https://docs.ollama.com/integrations/codex), [Qwen Code model providers](https://github.com/QwenLM/qwen-code/blob/main/docs/users/configuration/model-providers.md), [OpenCode providers](https://opencode.ai/docs/providers), and [Ollama for Goose](https://docs.ollama.com/integrations/goose). Codex approval, sandbox, and provider keys were selected from the [official Codex configuration reference](https://developers.openai.com/codex/config-reference).

Preflight-only friction, excluded from measured results:

- Goose's documented stable PowerShell installer URL returned 404; the inspected official repository installer was used instead.
- Codex rejected incompatible combinations of `--approve-for-me`, `--sandbox`, and `--ask-for-approval` during setup.
- an early Codex resume attempted OpenAI after provider state was not pinned; it was discarded, then a fresh local-only resume preflight proved the corrected configuration before measurement;
- Qwen Code's initialization event advertised a much broader registry than the requested core-tool list, although the measured runs invoked only the intended local tools;
- OpenCode required no material workaround after isolated provider setup.

## Independent validation

All 48 final workspaces passed `git diff --check`. Tests 5 and 6 were rerun independently with Node after the harnesses stopped. Tests 3 and 7 were exercised through the real in-app browser against a loopback HTTP server.

Browser Test 3 passed for all four current harnesses: clicking **Launch** set `data-effect="fireworks"` and `data-particle-count="80"`, while both existing asset hashes remained unchanged.

Strict browser/static Test 7 findings:

| Harness | `+2` | `score-value` ID | no celebration at 6 | celebration at exact 5 | zero floor/reset/layout/assets | Full Test 7 |
|---|---:|---:|---:|---:|---:|---:|
| Codex | yes | **no** (`count-display`) | yes | yes | yes | **no** |
| Qwen Code | **no** (`+1`) | **no** (`display`) | yes | yes through five `+1` clicks | yes | **no** |
| OpenCode | yes | **no** (`count`) | **no**; celebrates on crossing to 6 | **no** on 6 → 5 decrement | yes | **no** |
| Goose | yes | yes | **no**; `count >= 5` | yes, but over-broad | yes | **no** |

The exact-five rule matters. The final `+2` request deliberately keeps 5 reachable through `0 → 2 → 4 → 6 → 5`; DSH and Claude Code preserved `count === 5`. OpenCode and Goose changed the contract rather than preserving it.

## Codex: complete scenario results

| Test | Result | Terminality; effect; recovery; hygiene; final report | Calls / errors | Time |
|---:|---|---|---:|---:|
| 1 | **FAIL** | Normal return. Created `hello.txt` with CRLF: 13 bytes, not exact 11. No extra file. Final report called it exact while also naming CRLF, so it was misleading. | 3 / 0 | 125.5 s |
| 2 | **PASS** | Exact one-line title edit. Recovered after patch/approval-path errors using a bounded PowerShell rewrite. No unrelated change; final report accurate. | 14 / 5 | 158.1 s |
| 3 | **PASS** | Correct existing-engine import/call; real browser produced `fireworks/80`; assets and tree clean. Recovered from inaccessible patch attempts. Final report accurate. | 11 / 4 | 91.4 s |
| 4 | **PASS** | Exactly two verifier invocations: first exit 17/state `observed`, second pass. Git clean. Recovery and report accurate. | 5 / 0 | 35.0 s |
| 5 | **PASS** | One-line subtraction-to-addition fix; independent test passed. Recovered from tool/path failures; no unrelated change; report accurate. | 8 / 2 | 46.2 s |
| 6 | **PASS** | One-line `Hello` → `Hi`; exactly one post-fix `npm test`, then stopped. Independent test passed; report accurate. | 9 / 1 | 48.8 s |
| 7 | **PARTIAL / FAIL** | All nine turns returned normally and most behavior passed. It never applied the requested live ID rename (`count-display` remained), yet the final report claimed the `score-value` “key” was preserved. No asset or extra-file damage. | 19 / 0 | 470.1 s |
| 8 | **PASS** | Exact one-line button text edit; tree clean; report accurate. | 5 / 1 | 37.0 s |
| 9 | **PASS** | Removed exactly six obsolete files; five required files remained; no placeholders. Report accurate. | 4 / 0 | 49.4 s |
| 10 | **PASS** | Source absent, one 69-byte destination with expected SHA-256. Report accurate. | 2 / 0 | 22.4 s |
| 11 | **PASS** | Quotes-preserving one-line edit in the spaced path; decoy untouched; syntax valid. Report accurate. | 5 / 0 | 41.1 s |
| 12 | **FAIL** | Final turn timed out at 300 seconds. External line remained safe, but `mode=old` was never changed. The model repeatedly reasoned about invoking an inaccessible shell `apply_patch`; its last user-facing text was the older pre-mutation file state. | 6 / 2 | 308.7 s |

Codex emitted the missing-model-metadata warning once per process/turn, 22 times total. The warning did not change the requested model identity, but the advertised patch path was operationally unusable. The model recovered from this in several short tasks and failed to converge around it in Test 12.

One measurement-driver incident occurred after Codex Test 7 turn 0: progress output polluted the runner's session-path return value. The driver was corrected and the existing session resumed at turn 1. Turn 0 was not rerun, no model work was repeated, and the incident is classified **ENVIRONMENT / DRIVER**, not a harness outcome.

## Qwen Code: complete scenario results

| Test | Result | Terminality; effect; recovery; hygiene; final report | Calls / errors | Time |
|---:|---|---|---:|---:|
| 1 | **PASS** | Normal return; the only exact 11-byte `hello world` result among the four current harnesses. Tree clean; report accurate. | 2 / 0 | 26.1 s |
| 2 | **PASS** | Exact one-line title edit; clean and accurately reported. | 2 / 0 | 24.3 s |
| 3 | **PASS** | Correct real-engine reuse; browser and hashes passed. One tool error recovered; final report accurate. | 9 / 1 | 88.7 s |
| 4 | **PASS** | Exactly two verifier runs with successful state-aware recovery; Git clean; report accurate. | 8 / 1 | 83.4 s |
| 5 | **PASS** | Minimal math fix; harness and independent tests passed; report accurate. | 5 / 0 | 39.1 s |
| 6 | **PASS** | Minimal greeting fix and exactly one post-fix test; stopped and reported accurately. | 5 / 0 | 42.9 s |
| 7 | **PARTIAL / FAIL** | Turns 0-5 returned normally; turns 6, 7, and 8 each timed out at 300 seconds. The workspace gained celebration and zero-floor work, but retained `+1` and `display`, missing the final `score-value` and `+2` requirements. No extra files or asset damage. No terminal reports for the last three requests. | 22 / 4 | 1,085.0 s |
| 8 | **PASS** | Exact button text change; no duplicate/recreation; accurate report. | 5 / 0 | 43.6 s |
| 9 | **PASS** | Exact required five-file tree. High exploration cost but normal stop and accurate report. | 16 / 0 | 246.4 s |
| 10 | **PASS** | Exact one-copy move with expected hash; accurate report. | 9 / 0 | 116.1 s |
| 11 | **PASS** | Correct spaced-path edit and untouched decoy. One error recovered; report accurate. | 3 / 1 | 37.2 s |
| 12 | **PASS** | First edit was rejected because the current session had not read the file; it reread, preserved `external=preserve`, edited, verified, and reported the recovery truthfully. | 5 / 1 | 90.7 s |

Qwen Code demonstrated the best exact file behavior and the clearest stale-read guard. Its Test 7 behavior is nevertheless disqualifying for unbounded autonomous use: three consecutive external timeouts occurred in the same session after useful earlier work.

## OpenCode: complete scenario results

| Test | Result | Terminality; effect; recovery; hygiene; final report | Calls / errors | Time |
|---:|---|---|---:|---:|
| 1 | **FAIL** | Normal return. `hello.txt` contained one trailing LF: 12 bytes. No extra file. Final report omitted the newline and implied exact completion. | 1 / 0 | 20.5 s |
| 2 | **PASS** | Exact one-line title edit; normal and accurate. | 3 / 0 | 25.4 s |
| 3 | **PASS** | Correct engine reuse; browser and asset/tree checks passed. One error recovered; report accurate. | 10 / 1 | 40.9 s |
| 4 | **PASS** | Exactly two verifier runs; inspected recoverable state and ended cleanly. Report accurate. | 5 / 1 | 46.4 s |
| 5 | **PASS** | Minimal math fix; tests passed independently; report accurate. | 8 / 0 | 35.6 s |
| 6 | **PASS** | Minimal greeting fix and one post-fix test; stopped correctly. | 6 / 0 | 30.7 s |
| 7 | **PARTIAL / FAIL** | All turns returned normally, but ID remained `count` and celebration was broadened to crossing `>= 5`: it fired at 6 and not on the 6 → 5 decrement. Final report falsely claimed the `score-value` key and exact-five behavior were preserved. | 31 / 0 | 398.8 s |
| 8 | **PASS** | Exact text edit; no recreation or hygiene issue; report accurate. | 4 / 0 | 26.3 s |
| 9 | **PASS, hygiene-qualified** | Correct final five-file tree, but the six deletions were staged with Git although staging was not requested. The final report disclosed the staging. | 9 / 1 | 58.1 s |
| 10 | **PASS** | Exact one-copy move and hash; report accurate. | 2 / 0 | 49.7 s |
| 11 | **PASS** | Exact spaced-path edit; decoy untouched; report accurate. | 2 / 0 | 24.4 s |
| 12 | **PASS** | Direct current-state substring edit preserved the external line without a stale rejection, followed by verification. Final report accurately disclosed that no rejection occurred. | 3 / 0 | 35.6 s |

OpenCode's principal strength was bounded terminality. Its principal weakness was semantic confidence without proof: Test 7 stopped normally and narrated success despite two observable contract violations. It therefore needs Host-owned behavioral validation even when it looks operationally smooth.

## Goose: complete scenario results

| Test | Result | Terminality; effect; recovery; hygiene; final report | Calls / errors | Time |
|---:|---|---|---:|---:|
| 1 | **FAIL** | Normal return. File had one trailing LF: 12 bytes. No extra file. The final report truthfully exposed the newline and hex bytes. | 2 / 0 | 16.8 s |
| 2 | **EFFECT-ONLY / FAIL** | The title edit was correct and clean, but the process timed out at 300 seconds. Its last text hallucinated a plan to build an entire Hangman application rather than report the one-line result. | 4 / 1 | 300.1 s |
| 3 | **PASS** | Correct existing-engine reuse; browser and hashes passed; clean and accurately reported. | 3 / 0 | 31.8 s |
| 4 | **PASS** | Exactly two verifier runs and successful recovery. An inappropriate `read_image` call on a script failed, but the shell path recovered; report accurate. | 6 / 1 | 36.2 s |
| 5 | **FAIL** | It identified subtraction as the defect and ran the failing test, but never edited `src/math.js`. It returned normally with “Fixing it now,” leaving the build broken. | 4 / 2 | 22.0 s |
| 6 | **PASS** | Two structured edits failed, then a full-file write recovered; exactly one post-fix test passed. No unrelated change; report accurate. | 6 / 2 | 33.3 s |
| 7 | **PARTIAL / FAIL** | Normal turns and correct title, `score-value`, `+2`, floor, reset, layout, and asset reuse. It changed exact `count === 5` to `count >= 5`, causing celebration at 6. Final report openly described the broadening but mischaracterized it as satisfying “reaches 5.” | 18 / 0 | 212.4 s |
| 8 | **PASS** | Exact button text edit; clean and accurately reported. | 3 / 0 | 17.3 s |
| 9 | **PASS** | Exact six deletions/five retained files; normal stop and accurate report. | 6 / 0 | 43.8 s |
| 10 | **PASS** | Exact one-copy move; 69-byte result; accurate report. | 4 / 0 | 34.1 s |
| 11 | **PASS** | Correct spaced-path edit after inappropriate image/edit failures; decoy untouched; final report accurate. | 5 / 2 | 28.8 s |
| 12 | **PASS** | Direct substring edit preserved the external line and verified final bytes; no stale rejection. Report accurate. | 3 / 0 | 28.7 s |

Goose's low call count mainly reflects short, direct paths; it did not compensate for stopping before Test 5's requested fix or for Test 2's non-termination. Efficiency without a proven effect is not a reliability advantage.

## Same model, materially different harness behavior

| Scenario | Material harness difference |
|---|---|
| Test 1 exact bytes | Qwen Code wrote 11 exact bytes. Codex produced CRLF (13 bytes); OpenCode, Goose, DSH, and Claude produced LF (12 bytes). The same model did not deterministically fail under every tool surface. |
| Test 2 terminality | Codex, Qwen Code, and OpenCode edited and stopped. Goose made the same correct edit but then timed out and emitted unrelated Hangman planning. |
| Test 4 recovery | All four current harnesses reached the required two-run recovery. Qwen/OpenCode exposed the first run as an error; Codex/Goose commands captured or wrapped exit state differently. Raw error counts therefore do not describe recovery quality. |
| Test 5 completion | Codex, Qwen Code, OpenCode, DSH, and Claude fixed the defect. Goose diagnosed it, ran the failing test, promised a fix, and stopped without changing the file. |
| Test 6 tool recovery | All current harnesses obeyed the one-test stopping contract. Goose recovered from two failed structured edits via a write; the recovered errors did not reduce the final result. |
| Test 7 continuity | DSH and Claude preserved every semantic invariant; DSH then lacked a terminal answer. Codex missed the ID rename; Qwen Code timed out on the final three requests and retained old ID/step; OpenCode missed the ID and broadened exact-five semantics; Goose preserved the ID/step but broadened equality to `>=`. |
| Test 9 Git hygiene | OpenCode staged the deletions. The other harnesses left equivalent working-tree deletions unstaged. |
| Test 12 stale edit | Qwen Code enforced a session read-before-edit guard, reread, and recovered. OpenCode and Goose applied current-state replacements that safely preserved the new line without rejecting staleness. Codex hit an inaccessible patch surface and a 300-second reasoning loop, leaving the requested edit undone. Claude previously merged safely with `staleRecovered: true`; DSH rejected the stale write before recovery. |

These differences are direct evidence that “model quality” and “harness quality” cannot be reduced to one number. Tool semantics, context/session state, permission boundaries, error shape, and terminal control materially change the same model's behavior.

## Failure attribution

| Finding | Attribution | Evidence boundary |
|---|---|---|
| Exact-byte failures | **MODEL + TOOL SURFACE** | The model/tool arguments chose newline-producing paths under five harnesses, while Qwen Code's write produced exact bytes. Harnesses that declared completion without byte proof add a **HARNESS** terminal-truth failure. |
| Codex patch access failures | **HARNESS / TOOL SURFACE + ENVIRONMENT** | The active Codex surface instructed shell invocation of `apply_patch`, which repeatedly returned access denied. Recovery proved ordinary workspace writes were possible. |
| Codex Test 12 timeout | **MODEL + HARNESS / TOOL SURFACE** | The inaccessible patch path supplied the failure condition; the model spent most of the 300-second turn repeatedly reconsidering invocation details rather than changing strategy promptly. |
| Qwen Code Test 7 triple timeout | **MODEL + HARNESS / SESSION** | The same persistent session stopped converging on three consecutive later requests. The exact limiting internal stage is not exposed, so a more specific attribution is unsupported. |
| OpenCode Test 7 broadening | **MODEL** | The final narration explicitly reinterpreted “at 5” as crossing to 6; the harness did not independently validate the semantic contract. |
| Goose Test 2 post-effect drift | **MODEL + HARNESS** | The correct edit existed before timeout, but generation continued into an unrelated application plan and the harness supplied no objective-level stop. |
| Goose Test 5 no edit | **MODEL + TOOL SURFACE + HARNESS** | The model misused `read_image`, diagnosed the code through shell, ran the failing test, then stopped after promising an edit. The harness accepted a normal terminal without proving mutation or test success. |
| Goose Test 7 `>= 5` | **MODEL** | The model deliberately broadened the condition and described that decision. The harness again lacked executable acceptance proof. |
| OpenCode Test 9 staging | **MODEL + PERMISSIVE TOOL SURFACE** | The model used a Git-writing path unnecessary for the requested deletion objective. |
| Current context growth to 131,072 | **HARNESS / RUNTIME, exact mechanism UNKNOWN** | Ollama `/api/ps` observed 131,072 after current harness use despite 32,768 metadata in isolated configs. No supported evidence identifies the component that raised it. |
| Codex measurement-driver resume | **ENVIRONMENT / DRIVER** | The driver, not Codex, polluted the session-path return after turn 0. The existing session resumed without repeating the turn. |

## What this means for Agentic Router

### Necessary now

- Keep the Router Host authoritative for effect proof, terminal truth, workspace/Git hygiene, stale/conflict handling, and bounded recovery.
- If selecting one harness for the next advisory-review experiment, start with **OpenCode** for terminality or **Qwen Code** for correctness, but place a strict Host-owned per-turn and per-objective budget around either.
- Use Qwen Code's read-before-edit behavior as a reference for stale-state protection.
- Use OpenCode's bounded stopping as a reference, while explicitly rejecting its current lack of objective-level behavioral proof.
- Preserve DSH as a structural reference because it remains closer to Agentic Router's local specialist/tool-loop shape, but rerun the current digest before treating its six-way position as confirmed-current.

### Useful later

- Inspect the open-source Codex harness for provider/session/tool-loop patterns, but do not copy its current local-model metadata or patch-surface behavior blindly.
- Run a focused three-repeat stability control on Tests 1, 5, 7, and 12 for OpenCode and Qwen Code. Keep repeats separate from this one-shot baseline.
- Add Host validators that make these exact failures impossible to call complete: byte length/hash, expected mutation, no unintended staging, browser invariant checks, and one terminal event.

### Probably unnecessary

- Replacing Agentic Router wholesale with any measured harness.
- Ranking by raw tool-error count or token speed.
- Prompt tuning around individual failures before the untuned baseline is preserved and repeatability is measured.

## Final recommendation

For reviewing Agentic Router, use `qwen3.8:27b` only as a constrained advisory specialist. The current evidence supports **OpenCode as the best terminally bounded harness candidate** and **Qwen Code as the best correctness candidate**, not an autonomous winner.

If one next experiment must be chosen, use Qwen Code behind Agentic Router-style Host budgets and independent validators, because it achieved 11/12, exact bytes, and explicit stale recovery. If non-termination risk dominates the decision, use OpenCode instead, accepting that its fluent terminal reports can be semantically wrong and therefore must be treated as untrusted narration.

The most valuable design conclusion is not which CLI wins. It is that Agentic Router's Host-owned contracts are justified by direct same-model evidence: every harness produced at least one case where a normal-looking agent result, a correct partial effect, or a low error count failed to prove the actual objective.
