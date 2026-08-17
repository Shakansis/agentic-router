# Codex smoke evaluation: Qwen3.8 27B

Date: 2026-08-16

Evaluation plan: `docs/PLAN-v9-codex-qwen3.8-27b-smoke-evaluation.md`

Comparison baselines: [Qwen3-Coder under Codex](./codex-qwen3-coder-30b-smoke-evaluation.md) and [Qwen3.8 under DeepSeek Harness](./deepseek-harness-qwen3.8-27b-evaluation.md)

Evidence labels are **PROVEN**, **FAILED**, **PLAUSIBLE**, and **UNKNOWN**. Model narration and Codex lifecycle completion are not acceptance evidence.

## Technical summary

**Qwen3.8 27B is materially stronger than Qwen3-Coder 30B under the same Codex harness, but it is not yet reliable enough for autonomous Agentic Router review or correction.** Four of five scenarios were independently accepted. It created the exact 11-byte file, reused the existing fireworks implementation, made the minimal build fix, and completed the exact Notes label edit. Every model turn terminated normally and every attempted action used a native Codex function call; no textual pseudo-tool output occurred.

The five-turn continuity scenario failed. Qwen3.8 preserved the title and executable increment/decrement/reset behavior, but interpreted “rename the displayed counter identifier” as removing the word `counter` from a container class, heading, and storage key while leaving the live display identifier `id="value"` unchanged. It then changed increment from 1 to 2 without preserving the reachability of its own exact-10 celebration after an odd-state decrement. Independent execution proved both the basic behavior and this regression.

The result is **4/5 accepted**, versus **1/5** for Qwen3-Coder in the same Codex smoke. Qwen3.8 is therefore a credible advisory reviewer candidate, subject to Host-owned diff inspection and validation. The remaining semantic continuity failure is disqualifying for autonomous changes because it occurred in the scenario most representative of cross-file maintenance.

## 1. Environment

| Item | Observed value |
|---|---|
| OS | Windows 10 Pro 22H2, build `19045.6466`, AMD64 |
| GPU | NVIDIA GeForce RTX 4090, 24,564 MiB |
| Ollama | `0.32.13`, loopback `http://127.0.0.1:11434` |
| Codex | `codex-cli 0.147.0-alpha.6.6` from the installed Codex Desktop package |
| Codex executable | Same signed OpenAI binary as the Qwen3-Coder smoke; SHA-256 `592958896CBFFA154709618476FC9C9BF7FE73957E9A4FC12094C5051B6C69B3` |
| Provider/model | Built-in `ollama` provider; exact model argument `qwen3.8:27b` |
| Context | Codex configured `model_context_window=32768`; Ollama observed `context_length=32768` |
| Approval | `on-request`, reviewer `user` |
| Sandbox | Test 1 began `read-only` during first-use Windows sandbox setup and its two explicit writes were approved. Tests 2-5 recorded `workspace-write`; network was restricted. |
| Isolation | Fresh clones of the exact five qwen3-coder fixture commits; isolated `CODEX_HOME`; one session per Tests 1-4 and one persistent five-turn session for Test 5 |
| Router/DSH mutation | None. Only this plan and report were added for this run. |

The local-provider selection follows the official Codex configuration contract, which lists `ollama` as a built-in provider and supports explicit model/provider selection ([Codex configuration reference](https://developers.openai.com/codex/config-reference)). No hosted model performed an evaluated task.

## 2. Exact model identity

| Field | Value |
|---|---|
| Ollama tag | `qwen3.8:27b` |
| Digest | `22130167c4c20e20c7b71454612966ca8e8171e9b3cc8ab6ce8aa6cbfec79643` |
| Artifact size | `17,741,872,154` bytes; Ollama displayed 17 GB |
| Architecture | `qwen35` |
| Parameters | `27,320,697,856` (`27.3B`) |
| Quantization | `Q4_K_M` |
| Declared maximum context | `262,144` |
| Embedding length | `5,120` |
| Observed loaded footprint | 17 GB, 100% GPU, 32,768 context |
| Ollama capabilities | completion, vision, tools, thinking |
| Renderer/parser | `qwen3.8` / `qwen3.5` |

All five Codex session records identify provider `ollama` and model `qwen3.8:27b`. No model substitution was observed.

## 3. Five scenario results

| Scenario | Acceptance | Calls / failed | Shell executions / redundant | Files changed / unnecessary files | Existing-file full rewrites | Stop / cancellation | Narration |
|---|---:|---:|---:|---:|---:|---|---|
| 1. Exact minimal file | **PROVEN** | 2 / 1 | 2 / 0 | 1 / 0 | 0 | Normal / no | **ACCURATE** |
| 2. Existing asset reuse | **PROVEN** | 6 / 1 | 6 / 0 | 1 / 0 | 1 | Normal / no | **ACCURATE** |
| 3. Build/test diagnosis | **PROVEN** | 8 / 3 | 8 / 0 | 1 / 0 | 1 | Normal / no | **ACCURATE** |
| 4. Existing Notes edit | **PROVEN** | 4 / 1 | 4 / 0 | 1 / 0 | 1 | Normal / no | **ACCURATE** |
| 5. Five-turn continuity | **FAILED** | 17 / 2 | 14 / 0 | 2 / 0 | 4 | Five normal turns / no | **PARTIALLY ACCURATE** |

“Failed call” includes a nonzero shell result or an output that proved the intended validation failed even when the composite PowerShell command returned zero. Test 3's initial failing test is counted because the metric is process-result based, although that failure was expected and informative. Alternative commands after a failure are not counted as redundant.

The Codex/Ollama path exposed `shell_command` plus plan state as the effective action surface. File changes therefore appear as shell executions, not specialized file-tool calls. Creation writes are not counted as full rewrites; subsequent `Set-Content` replacements of an existing file are.

### Scenario findings

1. **Exact minimal file — PROVEN after recovery.** Qwen3.8 first selected `Set-Content -NoNewline -Encoding utf8NoBOM`; Windows PowerShell 5 rejected that encoding name. It then selected ASCII, read the result, and verified length 11. Independent bytes were exactly `68-65-6C-6C-6F-20-77-6F-72-6C-64`, with no BOM or newline. Only `hello.txt` was added.

2. **Existing asset reuse — PROVEN.** Qwen3.8 inspected `app.js`, `index.html`, and both fireworks modules, then changed only `app.js` to import and call `launchFireworks`. The first patch-as-shell attempt failed; the fallback rewrote the small file and introduced a UTF-8 BOM. Both existing assets remained byte-identical. Independent module execution proved `effect="fireworks"` and `particleCount="80"`; all modules passed `node --check`.

3. **Build/test diagnosis — PROVEN.** Qwen3.8 found subtraction instead of addition and changed only `src/math.js`. `npm test` was blocked by the local PowerShell execution policy; direct `node --test` proved the pre-fix failure. A patch-as-shell attempt failed, the model used a literal replacement, and the first post-fix `node --test` passed. Independent `npm test` outside the enclosing sandbox also passed. It stopped immediately after success.

4. **Existing Notes edit — PROVEN.** Qwen3.8 inspected the three existing files, attempted the unavailable patch path once, then used an exact literal replacement that preserved `id="save"`. Independent Git diff showed one deletion and one insertion in `index.html`; `app.js` still referenced `#save`; no other file changed. The turn ended normally after four calls.

5. **Five-turn continuity — FAILED, with working basic behavior.** Turn 1 created root `index.html` and `assets/app.js`, reusing the committed `assets/celebrate.js`; it proactively included decrement, reset, persistence, and exact-10 celebration. Turn 2 changed the browser title to `Continuity App`. Turn 3 correctly recognized that decrement already existed. Turn 4 changed `.counter` to `.app`, the heading to `Continuity App`, and storage key `counter.value` to `continuity-app.value`, but left the actual display ID and JavaScript lookup as `value`. Turn 5 changed `change(1)` to `change(2)`.

Independent execution proved increment `0 -> 2`, decrement `2 -> 1`, reset `1 -> 0`, and celebration at 10 on the even path. It also proved that after reset/decrement, repeated +2 increments follow `-1 -> 1 -> 3 -> 5 -> 7 -> 9 -> 11` and never celebrate. The model noticed and disclosed this caveat in its final answer, but did not preserve the prior behavior.

## 4. Important traces

### Exactness recovery

```text
Set-Content ... -Encoding utf8NoBOM -> exit 1 (unsupported by Windows PowerShell 5)
Set-Content ... -Encoding ascii -NoNewline -> exit 0
model-observed length: 11
independent bytes: 11, no BOM, no newline
```

This is strong model recovery over the same Codex/PowerShell surface where Qwen3-Coder wrote 28 UTF-16LE bytes and falsely claimed exactness.

### Repeated patch/tool-surface mismatch

```text
Qwen3.8 emits shell_command("apply_patch ...")
PowerShell returns access denied or parser errors
Qwen3.8 selects a literal PowerShell replacement
requested effect succeeds
```

The mismatch occurred in Tests 2, 3, 4, and Turn 2 of Test 5. Codex's base instructions recommend `apply_patch`, but this local model path did not expose a working patch function. Qwen3.8 recovered every time, at the cost of full-file rewrites.

### Masked validation failure

```text
node --input-type=module --check (Get-Content -Raw assets/app.js)
Node output: MODULE_NOT_FOUND
composite PowerShell exit: 0
Qwen3.8 reads the output, copies to a temporary .mjs, reruns node --check
second validation: syntax OK
```

The model recovered correctly, but the first tool result illustrates why exit zero from a compound shell command is not effect or validation truth.

### Continuity mismatch

```text
requested: rename the displayed counter identifier consistently across all files
changed: .counter -> .app; Counter heading -> Continuity App; counter.value -> continuity-app.value
unchanged live pair: id="value" and getElementById("value")
final step: change(1) -> change(2)
regression: exact-10 celebration becomes unreachable from odd states
```

## 5. Stopping and repetition

Stopping was strong. All nine evaluated turns produced `task_complete`; none required manual cancellation; no exact-call loop occurred; and Tests 3-4 stopped immediately after the requested verified effect. The persistent session also completed all five turns, unlike the longer Qwen3.8 DSH session whose final answer ended `max-tokens` after a correct effect.

The cost profile was not efficient. The run recorded 37 total function calls, 34 shell executions, and 491,702 total tokens across the five sessions. Four patch attempts failed because of the harness/tool-surface mismatch. Test 5 used four existing-file full rewrites and 260,340 tokens for five short turns. These token totals include Codex context and are not a controlled throughput benchmark.

## 6. Comparison with preserved baselines

| Dimension | Qwen3-Coder / Codex | Qwen3.8 / Codex | Qwen3.8 / DSH | Interpretation |
|---|---|---|---|---|
| Accepted smoke scenarios | 1/5 | **4/5** | 3/5 fully accepted plus continuity effect-qualified | Qwen3.8 is decisively stronger; harness changes which edge case fails |
| Minimal exact file | 28-byte UTF-16LE failure | **Exact 11 bytes after recovery** | 12 bytes with trailing LF | Codex plus Qwen3.8 produced the strongest exactness result |
| Asset reuse | Zero-call pseudo-tool, no effect | **Correct reuse, 6 calls** | Correct reuse, 6 calls | Model change removes the protocol failure under Codex |
| Build diagnosis | Correct, 12 calls | **Correct, 8 calls** | Correct, 8 calls | Qwen3.8 converges faster; Codex adds PowerShell/patch friction |
| Notes edit | Broken ID, 8 calls, aborted | **Exact one-line effect, 4 calls** | Exact effect, 6 calls | Qwen3.8 is both safer and more efficient under Codex |
| Continuity | Missed identifier rename; basic behavior worked | Acted on related identifiers but missed display ID; celebration regression | Final behavior correct; final narration hit max-tokens in longer run | Semantic continuity remains the Qwen3.8/Codex weakness |
| Native-call conformance | One zero-call pseudo-tool scenario | **No pseudo-tools; every action native** | No pseudo-tools in shared suite | Strong evidence that the earlier protocol failure was model-specific |
| Terminal behavior | One aborted turn | **All nine turns complete** | One long final turn ended max-tokens | Codex is strong on this shorter stopping control |

The DSH continuity scenario had nine turns and additional invariants, so its 26 calls and terminal result are not directly comparable to this five-turn subset. The exact fixture comparisons are strongest for minimal creation, asset reuse, build diagnosis, and Notes edit.

## 7. Model-versus-harness attribution

| Finding | Attribution | Basis |
|---|---|---|
| Zero pseudo-tools and correct native actions | **MODEL** | Same Codex harness as Qwen3-Coder; changing the model removed the protocol failure. |
| Exact 11-byte recovery | **MODEL + TOOL SURFACE** | Qwen3.8 recognized the PowerShell encoding rejection and selected a safe alternative. |
| Four failed patch attempts | **HARNESS / TOOL SURFACE** | Codex guidance recommended `apply_patch`, but the working action surface accepted it only as a failing shell command. |
| Full-file rewrites | **MODEL + TOOL SURFACE** | Qwen3.8 selected `Set-Content` fallbacks; a working structured patch primitive was unavailable. |
| `npm.ps1` policy failure | **ENVIRONMENT** | The local execution policy blocked the script shim; direct Node worked. |
| Masked Node validation exit | **MODEL + SHELL CONTRACT** | The compound command converted Node failure into shell exit zero; Qwen3.8 still read the output and recovered. |
| Missed display-ID rename | **MODEL**, qualified by prompt ambiguity | Reads and writes worked. The model changed related identifiers but not the live cross-file display reference used by the common acceptance rule. |
| Celebration reachability regression | **MODEL** | The exact `change(2)` call preserved the equality check without preserving all reachable prior behavior. |
| All turns completed | **MODEL + HARNESS** | Qwen3.8 emitted bounded actions and Codex terminated every turn cleanly. |

The harness materially contributes execution friction and encourages broad rewrites, but the remaining failed objective is semantic model behavior. Unlike the Qwen3-Coder result, the evidence does not support saying that the model is generally unfit for agentic tool execution.

## 8. Limitations and conclusion

The smoke is one trial per fixture, not a stability estimate. Test 5's rename instruction did not specify the target name, although the same acceptance interpretation was applied to the prior Qwen-Coder run and the preserved DSH benchmark. The continuity fixture also allowed Qwen3.8 to introduce celebration and persistence during Turn 1; those became prior behaviors that the final turn was required to preserve. No full browser was needed for the measured DOM/event behavior, but the independent Node stub is not a browser compatibility test.

**Decision: Qwen3.8 27B is promising and materially useful, but still not autonomous-review reliable.** It is the first tested local candidate with strong evidence across exact effects, native calls, bounded recovery, narrow edits, and clean stopping in both DSH and Codex. For Agentic Router, it is reasonable to evaluate this exact digest as an advisory specialist whose proposed changes, cross-file references, tests, and completion claims remain Host-verified.

The next justified experiment is not the full 12-scenario Codex suite. First repeat the five-turn continuity case three times with an explicit rename target such as `value -> score-value`, and require an executable invariant that exact-milestone behavior remains reachable after increment changes. Passing that targeted gate would justify a real, read-only Agentic Router review trial. Failing it would keep Qwen3.8 advisory-only without spending more benchmark volume.
