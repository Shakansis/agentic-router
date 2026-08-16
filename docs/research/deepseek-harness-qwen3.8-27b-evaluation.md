# DeepSeek Harness evaluation: Qwen3.8 27B

Date: 2026-08-16  
Model: `qwen3.8:27b`  
DeepSeek Harness: `0.1.0-rc.6`  
Evaluation type: untuned controlled local baseline

Evidence labels in this report are limited to **PROVEN**, **PLAUSIBLE**, **SPECULATIVE**, and **FAILED**. Model narration and DSH lifecycle completion are not authoritative evidence.

## 1. Executive summary

Qwen3.8 27B is the strongest local model evaluated so far in DeepSeek Harness, but the direct reliability answer is still **PROMISING BUT UNRELIABLE**. It emitted native calls consistently, produced 11 of 12 requested final effects, preserved every final semantic invariant in the eight-request browser scenario, recovered correctly from the intentional verifier transition and stale edit, and adapted to both missing delete and move capabilities.

The comparative improvement is substantial. Qwen3.8 made 68 native calls across the eight shared scenarios and 103 across all 12, with only two DSH results marked `isError: true`. It produced no pseudo-tool text, unknown tool name, malformed JSON, or malformed schema argument. The preserved baselines recorded 116 shared calls for Qwen3-Coder, 100 shared/138 total and 46 marked errors for GPT-OSS, and 75 shared/123 total and 23 marked errors for Gemma4. Qwen3.8 also avoided the earlier Qwen management loop; its only management activity was two bounded `todo_write` calls in the delete scenario.

Two acceptance failures prevent a `YES` reliability answer. Test 1's native `write` argument contained `hello world\n`, so the file was 12 bytes rather than the required 11 even though DSH ended `completed` and headless exited 0. In Test 7, all final browser behavior was correct, but the ninth turn ended `max-tokens` after the edit and verification instead of producing a terminal answer. Qwen3.8 also used avoidable full-file writes in Tests 3 and 7 and spent 170.8 seconds exploring the Test 4 PowerShell compatibility issue.

The DSH conclusion therefore does not change with the stronger model. Its traces and stale-state guard are useful, but its terminal lifecycle is not independent effect proof. It should not replace Agentic Router's Host-owned validation, approval, effect proof, changed-file review, or terminal truth.

## 2. Environment

| Item | Observed value |
|---|---|
| OS | Windows 10 build `19045.6466` (`OSVersion` reported `10.0.19045.0`) |
| GPU | NVIDIA GeForce RTX 4090, 24,564 MiB, driver `610.88` |
| DSH | `0.1.0-rc.6` |
| Ollama | `0.32.13` |
| Ollama endpoint | `http://127.0.0.1:11434`; DSH OpenAI-compatible endpoint `http://127.0.0.1:11434/v1` |
| DSH provider / API | `ollama-local` / `openai-completions` |
| DSH tool / permission mode | native / `workspace-write` |
| DSH surfaces | headless for isolated scenarios; official Web UI for Tests 7 and 12 |
| Context configuration | 262,144 input window; 32,768 maximum output |
| Telemetry | `DSH_TELEMETRY_DISABLED=1` on every launch |
| Disposable root | `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-qwen38-eval-20260816` |

Each scenario used a fresh disposable Git clone. The Agentic Router checkout was used only for the versioned plan and this report. No Router inference, build, E2E suite, source change, integration, or architecture work was performed.

Fixture commits were identical to the GPT-OSS and Gemma evaluations:

| Test | Commit |
|---|---|
| 1 | `f72cde0fdfcc29ca41cace1fab6d37dc88da6c12` |
| 2 | `3b86bb457cbc07dc9bae0e7ec3211a166de46a4a` |
| 3 | `53079799c06e41acdccfa66f2b65a30d89886fd1` |
| 4 | `7f98ae34989713ee7f0c8096c2e135a7d2af7018` |
| 5 | `e5cfc4594c13798790313909e727c92df7df4c6e` |
| 6 | `e811b9b6ef748f57c0167b190ef00572286880bd` |
| 7 | `5e97f16834cad14754b752f5799e1b41347335de` |
| 8 | `ee85381c5bf6cf3e1069fc3047214da114e7cd77` |
| 9 | `a8eaaead53eebfcc7d51165ec9283cb1aa8e213f` |
| 10 | `9e3af763c7ac78a42e9ef0b90238651a8d7db9a3` |
| 11 | `863af18326b2e8bd87d4fd5af37603d727a7526f` |
| 12 | `4d1aea3bbf71a4e26e2981bfaaac414b30a9b1e5` |

## 3. Verified model identity

The evaluated artifact was resolved from local Ollama metadata rather than inferred from its tag.

| Field | Observed value |
|---|---|
| Exact tag | `qwen3.8:27b` |
| Full digest | `22130167c4c20e20c7b71454612966ca8e8171e9b3cc8ab6ce8aa6cbfec79643` |
| Stored size | 17,741,872,154 bytes |
| Modified | `2026-08-16T00:45:38.4414302-03:00` |
| Parent model | `qwen3.8:27b-q4_K_M` |
| Format / family | GGUF / `qwen35` |
| Base name / version | `Qwen3.8` / `0814` |
| Exact parameter count | 27,320,697,856 |
| Quantization | `Q4_K_M` |
| Capabilities | completion, vision, tools, thinking |
| Declared context | 262,144 |
| Embedding length | 5,120 |
| Minimum Ollama | `0.32.12` |
| Renderer / parser | `qwen3.8` / `qwen3.5` |
| Observed loaded state | digest prefix `22130167c4c2`, 17 GB, 32,768 context, 100% GPU |

The generated Modelfile contained two `FROM E:\LLM\models\blobs\sha256-...` entries, `TEMPLATE {{ .Prompt }}`, renderer `qwen3.8`, and parser `qwen3.5`. Together with `parent_model=qwen3.8:27b-q4_K_M`, this is **PROVEN** evidence that the tag is a locally assembled/imported derivative rather than a basis for assumptions from the name alone. The precise external creation history is **UNKNOWN** and is not inferred.

## 4. Differences from previous evaluations

The selected model was the only intended experimental variable. DSH version, Ollama version, hardware, provider endpoint, transport, native catalog, permission policy, telemetry setting, fixture commits, prompts, validation, headless/Web split, and independent Git/browser checks remained fixed.

Unavoidable artifact differences were recorded rather than normalized:

- Qwen3.8 declares 262,144 context and loaded at 32,768, matching Qwen3-Coder and Gemma's observed context but not GPT-OSS's 131,072 declared maximum.
- Its observed Ollama footprint was 17 GB, between GPT-OSS (12 GB), Gemma (15 GB), and Qwen3-Coder (21 GB).
- The local tag carries a parent-model reference plus local blob sources, renderer `qwen3.8`, and parser `qwen3.5`; no adapter or template was changed for this run.
- Tests 7 and 12 used DSH Web, as in the Gemma evaluation, to preserve session state and interleave the external stale edit.
- Test 4's literal nested `pwsh` command could not resolve because only Windows PowerShell 5.1 was available inside DSH's process environment. The model adapted to `powershell -NoProfile -File` without changing the fixture.

No tuning, reduced tool catalog, model-specific instruction, alternate execution policy, or context change was introduced.

The preserved reports were unchanged during this run:

| Baseline | SHA-256 |
|---|---|
| Qwen3-Coder 30B | `1B5A34754C391FE0903C490796E99BC0041FD3F08966EF3EE05F40B06682BFB2` |
| GPT-OSS 20B | `3100CC4597E77E4B1280775B120DBF5EB1D9D2AC803878C454862A17DAE0C054` |
| Gemma4 26B A4B IT QAT | `E5A3D07216427587ED75FCB1F87684B488AC9C73A9ADEBAC1CA71B161C974869` |

## 5. Scenario results

The calls/error column counts durable `tool/call` events and results explicitly marked `isError: true`. Non-zero process output returned as an ordinary result is discussed separately.

| Test | Result | Calls / marked errors | Independently observed outcome |
|---|---:|---:|---|
| 1. Minimal file | **FAILED** exactness; **PROVEN** native effect | 1 / 0 | `write` created only `hello.txt`, but the argument contained `hello world\n`. Host hex was `68656C6C6F20776F726C640A`: 12 bytes, not exact 11. DSH ended `completed`; headless exited 0. |
| 2. Narrow edit | **PROVEN** | 3 / 0 | One HTML title line changed; no other file or line changed. |
| 3. Asset reuse | **PROVEN** | 6 / 0 | Only `app.js` changed to import/call the existing engine. No duplicate engine; asset files were byte-identical. A full-file `write` was avoidable. |
| 4. Recoverable verifier | **PROVEN**, inefficient | 11 / 0 | After the unavailable nested `pwsh`, the model inspected the environment, ran the fixture under Windows PowerShell, observed intentional exit 17 and `.verify-state=observed`, retried exactly once, and obtained exit 0. Tracked Git stayed clean. |
| 5. Build/test diagnosis | **PROVEN** | 8 / 1 | One-line subtraction-to-addition diff. `node --test` hit DSH `spawn EPERM`; an unavailable escalation was rejected; direct `node test/math.test.js` passed in DSH and independently on the host. No package change. |
| 6. Stop after success | **PROVEN** | 7 / 0 | One-line `Hello` to `Hi` diff. `npm test` hit `spawn EPERM`; one direct test passed; no process ran after that pass. Independent host execution passed. |
| 7. Long continuity | **PROVEN** final behavior; **FAILED** terminal answer | 26 / 0 | All final invariants passed file and real-browser checks. The final turn made the correct edit and reread it, then ended `max-tokens` with no final narration. |
| 8. No recreate | **PROVEN** | 6 / 0 | Exactly one HTML line changed `Save` to `Save note`; no duplicate, rename, replacement, or extra file. |
| 9. Delete gap | **PROVEN**, adapted | 22 / 0 | PowerShell removed exactly six unnecessary tracked files. Exactly five required files remained; no placeholder. The route was read-heavy and used two unnecessary to-do calls. |
| 10. Generic gap | **PROVEN**, adapted | 5 / 0 | PowerShell moved the 69-byte file. Source was absent, exactly one target remained, and SHA-256 stayed `602281D02B1A8F05DA127C8F1FBAEA631975D8ABA3107F7F35F2FFE945F24303`. |
| 11. Windows paths | **PROVEN** | 3 / 0 | Relative `nested path\source file.js` was read, minimally edited, and reread; the decoy stayed unchanged. No escaping or argument error. |
| 12. Stale write | **PROVEN** | 5 / 1 | The first edit was rejected as stale. Qwen3.8 reread, preserved `external=preserve`, made only `mode=old` to `mode=new`, and verified all three lines. |
| 13. Tool discipline | **PROVEN** strong native discipline | 103 / 2 aggregate | Zero pseudo-tools, unknown tools, malformed JSON, or malformed schemas. Two bounded `todo_write` calls were unnecessary. |
| 14. Self-verification | **PROVEN** mostly accurate, not perfect | n/a | 8 scenario narrations were accurate, 3 partially accurate, 1 misleading, and 0 false. Test 7 had no final-turn narration. |
| 15. Workspace hygiene | **PROVEN** containment; mixed minimality | n/a | All 12 diffs passed `git diff --check`; no escape, duplicate application, unrelated configuration, or abandoned placeholder. Tests 3/7 used avoidable whole-file writes. |
| 16. Fully local | **PROVEN** provider path; **PLAUSIBLE** exhaustive isolation | n/a | All durable model sources used loopback `ollama-local/qwen3.8:27b`; telemetry was disabled and common cloud keys absent. No packet-level instrumentation was used. |

Ten scenarios were fully accepted, Test 7 had a proven correct effect but failed its terminal-answer condition, and Test 1 failed exact bytes. This is descriptive evidence from one controlled baseline trial per fixture, not a statistical pass rate.

## 6. Native tool-call traces

### Minimal creation mismatch

```text
write { file_path: "...\\hello.txt", content: "hello world\n" }
-> success
turn/end: completed
headless exit: 0
Host bytes: 68656C6C6F20776F726C640A
```

The transport and filesystem effect were real. The incorrect LF was present in the model argument, so the exactness failure is **MODEL** behavior; accepting lifecycle completion without effect validation is separately **HARNESS** behavior.

### Correct verifier recovery

```text
pwsh -NoProfile -File .\verify-once.ps1
-> nested executable unavailable
inspect fixture and PowerShell environment
powershell -NoProfile -File .\verify-once.ps1
-> TRANSIENT_STATE_CHANGED; exit 17
inspect .verify-state and Git
powershell -NoProfile -File .\verify-once.ps1
-> verification passed; exit 0
turn/end: completed
```

There were exactly two actual fixture executions: the designed failure and one retry. The initial literal command was a compatibility failure, not an extra fixture run.

### Build recovery

```text
inspect package/source/test
edit src/math.js: left - right -> left + right
node --test
-> child-process spawn EPERM
retry with requested wider sandbox
-> no approval channel available
node test/math.test.js
-> 1 pass, 0 fail
turn/end: completed
Independent host test: exit 0
```

### Stale-state recovery

```text
read config.txt -> mode=old; owner=fixture
external writer -> adds external=preserve
edit mode=old -> mode=new
-> rejected: file changed since read
read -> sees external=preserve
edit mode=old -> mode=new
read -> mode=new; owner=fixture; external=preserve
turn/end: completed
```

## 7. Tool-call failures

Qwen3.8 issued 103 native calls. DSH marked only two results as errors:

| Error | Count | Assessment |
|---|---:|---|
| Test 5 escalation unavailable because Web/headless had no approval channel | 1 | **HARNESS** runtime limitation; the request shape was valid |
| Test 12 intentional stale rejection | 1 | **HARNESS** protection and required benchmark event |

Additional failure-bearing process results were returned with `isError: false`:

- Test 4: unavailable nested `pwsh` exit 1, followed by the intentional fixture exit 17;
- Test 5: `node --test` reported `spawn EPERM`;
- Test 6: `npm test` reported the same `spawn EPERM`.

No textual pseudo-tool markup, unknown tool name, JSON parse error, missing required field, invalid enum, invalid path escape, or malformed native argument was observed. That is materially stronger native conformance than every preserved baseline, especially Qwen3-Coder's zero-call pseudo-tool failures and GPT-OSS/Gemma's high marked-error counts.

## 8. Error recovery

Recovery was consistently effective:

- **PROVEN:** Test 4 distinguished the unavailable `pwsh` executable from the fixture's designed exit 17, adapted to installed Windows PowerShell, inspected the new marker, retried once, verified exit 0, and stopped.
- **PROVEN:** Test 5 treated `spawn EPERM` as environment-specific, tried the sanctioned escalation path once, then selected a direct test command that passed without changing `package.json`.
- **PROVEN:** Test 6 used the same direct-test fallback and obeyed the no-run-after-pass constraint.
- **PROVEN:** Test 12 reacted to the stale rejection with reread, state reconciliation, narrow retry, and final verification. It neither overwrote nor duplicated the external line.

Test 4 was nevertheless inefficient: seven PowerShell calls, a broad bounded executable search, and 170.8 seconds for a small recovery fixture. Correctness was strong; convergence speed was not.

## 9. Capability-gap behavior

Qwen3.8 chose **ADAPT** in both controlled capability gaps.

Test 9 had no delete tool. It mapped the dependency chain, selected PowerShell, removed an explicit six-file list, and verified Git status plus the remaining tree. Test 10 had no move tool. It selected `Move-Item`, verified pre/post length and SHA-256, source absence, and one remaining copy.

There was no fake delete, empty-file approximation, false success, limitation-only response, or invented specialized tool. Test 10 converged in five calls, materially cleaner than GPT-OSS's preserved 12-call route and Gemma's 21-call route. Test 9 was correct but over-inspected the tiny fixture with 22 calls.

This is evidence about DSH's exposed PowerShell fallback, not authorization to add a generic shell to Agentic Router.

## 10. Delete behavior

The final Test 9 tree contained exactly:

```text
hangman.html
hangman.css
hangman.js
fireworks/
  firework_engine.js
  firework_config.js
```

Git showed exactly six deletions:

```text
firework_config.js
firework_engine.js
game.js
hangman-words.js
index.html
styles.css
```

The real chain remained intact: `hangman.html` references `hangman.css` and `hangman.js`; `hangman.js` imports the engine under `fireworks/`; that engine imports its colocated configuration. No empty file or directory was left. The effect is **PROVEN**.

The path was safe but inefficient. Three successive tree-listing calls preceded 11 reads, three greps, `git ls-files`, deletion, and postcondition checks. Two `todo_write` calls added no task authority or evidence.

## 11. Windows path behavior

Qwen3.8 handled the prompt's absolute Windows target and preferred the workspace-relative path in every tool call:

```text
read "nested path\source file.js"
edit "nested path\source file.js"
read "nested path\source file.js"
```

The final diff was exactly:

```diff
-export const mode = "old";
+export const mode = "new";
 export const keep = "unchanged";
```

There was no malformed escape, absolute/relative thrashing, manual repair, or modification to `relative\other.js`. This is **PROVEN** and cleaner than both GPT-OSS and Gemma on the same fixture.

## 12. Stale-write recovery

DSH independently rejected Qwen3.8's first edit after an external writer added `external=preserve`. The model did not blind-retry. It reread and explicitly identified the new line before applying the requested change.

Final bytes were:

```text
mode=new
owner=fixture
external=preserve
```

Git contained one tracked-file diff with the requested line plus the externally injected fixture line, and `git diff --check` passed. The protection is **HARNESS** behavior; correct reconciliation is **MODEL** behavior. The combined recovery is **PROVEN**.

## 13. Stopping/convergence behavior

Qwen3.8 was the most call-efficient model on the shared suite: 68 calls versus Qwen3-Coder's documented 116, GPT-OSS's 100, and Gemma's 75.

Test 6 process invocations were:

| Invocation | Classification | Result |
|---|---|---|
| `npm test` after the fix | **JUSTIFIED** | DSH child-process `spawn EPERM` |
| `node test/message.test.js` after the fix | **JUSTIFIED** | 1 pass, 0 fail |

No process ran after the pass; there was no **QUESTIONABLE** or **REDUNDANT** invocation. Test 4 also performed the designed verifier only twice after adapting from the unavailable executable.

Eleven scenario sessions ended normally. The final Test 7 turn is the exception: after a correct edit and reread it produced 5,792 output tokens and ended `turn/end: max-tokens`, with DSH showing “Output token limit reached.” This is not a repeated-tool loop, but it is a stopping/terminal-answer failure. The reason that the effective provider limit was reached below DSH's configured 32,768 maximum is **UNKNOWN** from the preserved trace.

Summed per-turn durations were about 764.5 seconds across all scenarios, including registration turns but excluding human/browser pauses between turns. This is a cold/warm mixed correctness run, not a latency benchmark.

## 14. Tool-surface discipline

Aggregate native calls:

| Tool | Calls |
|---|---:|
| `read` | 46 |
| `pwsh` | 22 |
| `edit` | 14 |
| `grep` | 7 |
| `write` | 6 |
| `glob` | 5 |
| `todo_write` | 2 |
| `str_replace_editor` | 1 |
| **Total** | **103** |

No goal, job, agent, subagent, planning, workflow, or unknown-tool call occurred. The two to-do calls were confined to the largest cleanup task.

Every call was reviewed using the requested categories. `REQUIRED` means directly necessary for the effect or mandated validation; `USEFUL` means it reduced relevant uncertainty; `OPTIONAL` means defensible but skippable; `UNNECESSARY` means duplicated discovery or management; `HARMFUL` means it worsened authoritative state.

| Test | Required | Useful | Optional | Unnecessary | Harmful |
|---|---:|---:|---:|---:|---:|
| 1 | 1 | 0 | 0 | 0 | 0 |
| 2 | 2 | 1 | 0 | 0 | 0 |
| 3 | 3 | 3 | 0 | 0 | 0 |
| 4 | 4 | 5 | 0 | 2 | 0 |
| 5 | 6 | 1 | 1 | 0 | 0 |
| 6 | 5 | 1 | 1 | 0 | 0 |
| 7 | 16 | 4 | 6 | 0 | 0 |
| 8 | 2 | 3 | 1 | 0 | 0 |
| 9 | 2 | 16 | 0 | 4 | 0 |
| 10 | 1 | 3 | 1 | 0 | 0 |
| 11 | 2 | 0 | 1 | 0 | 0 |
| 12 | 5 | 0 | 0 | 0 | 0 |
| **Total** | **49** | **37** | **11** | **6** | **0** |

The six clearly unnecessary calls were Test 4's pre-transition state inspection and broad executable search, Test 9's two duplicate listings, and Test 9's two `todo_write` calls. This analytical classification is scenario-specific; it is not presented as an automatically reproducible metric.

## 15. Self-verification accuracy

| Test | Narration | Authoritative comparison |
|---|---|---|
| 1 | **MISLEADING** | Said “exactly” while also admitting a trailing newline; bytes violated the exact requirement. |
| 2 | **ACCURATE** | One title line and no other effect. |
| 3 | **ACCURATE** | Correctly described existing-engine reuse and unchanged assets. |
| 4 | **ACCURATE** | Correct exit 17, marker, clean tracked state, one retry, and exit 0. |
| 5 | **PARTIALLY ACCURATE** | Correct fix and passing direct test; loosely called the blocked direct `node --test` command “npm test.” |
| 6 | **ACCURATE** | Correct one-line fix, blocked npm run, direct pass, and no post-pass run. |
| 7 | **PARTIALLY ACCURATE** | Earlier turns were mostly exact; the rename turn called prior `display` a “counter-value” ID, and the final turn had no narration due `max-tokens`. |
| 8 | **ACCURATE** | Exact one-line label change and no other effect. |
| 9 | **ACCURATE** | Exact deleted and preserved sets matched Git/tree state. |
| 10 | **PARTIALLY ACCURATE** | Move/hash/copy facts were correct; “bytes were never read” ignored its own read/hash verification calls. |
| 11 | **ACCURATE** | Correct target, diff, and untouched decoy. |
| 12 | **ACCURATE** | Correct stale sequence and final three-line state. |

Totals: 8 **ACCURATE**, 3 **PARTIALLY ACCURATE**, 1 **MISLEADING**, 0 **FALSE**. The low false-claim rate is a major improvement, but Test 1 again demonstrates why model self-report cannot establish exact effects.

## 16. Multi-turn/context findings

The official Web session contained one readiness turn followed by the exact eight work requests. It recorded nine turns, 26 steps, 26 native calls, zero marked errors, 453,359 cumulative per-request input tokens, and 18,905 output tokens. The final UI showed 13% context use and approximately 69 tokens/second. Per-turn durations summed to 302.0 seconds; human validation pauses made the wall session longer.

Final browser/file behavior was:

- **PROVEN:** title `Continuity App`;
- **PROVEN:** centered card with `max-width: 420px`;
- **PROVEN:** `score-value` in HTML and JavaScript with the `.display` class preserved;
- **PROVEN:** increment by 2, reset, and zero-floor decrement;
- **PROVEN:** no celebration at 6, followed by celebration at exactly 5 when decrementing from 6;
- **PROVEN:** existing `assets/celebrate.js` reused byte-for-byte, SHA-256 `2784263316E4C6533BEFDA130845F13D8B4DAFE0EB75F6884325751A7F9F6E41`;
- **PROVEN:** no duplicate implementation or lost file-layout decision.

Qwen3.8 noticed that its initial app already contained decrement and correctly avoided adding a duplicate in the later decrement turn. When the rename request referred to `counter-value` but the model's actual earlier ID was `display`, it inferred the requested semantic target and changed both live references to `score-value`. On the final increment change it moved the exact-five celebration check into `render()`, keeping the state reachable through `0 -> 2 -> 4 -> 6 -> 5` without broadening the condition to `>= 5`.

The important failure was not semantic continuity but terminal production: the last turn exhausted its output allowance after the correct edit/read and provided no final answer. There is no evidence of context loss; required state remained present and current context use was 13%.

## 17. Workspace hygiene

Every scenario passed `git diff --check`. Final effects were:

| Test | Git/filesystem state | Classification |
|---|---|---|
| 1 | untracked `hello.txt` only | **REQUIRED** path; **FAILED** exact bytes |
| 2 | one-line `index.html` modification | **REQUIRED** |
| 3 | `app.js` only, 3 insertions/5 deletions | **REQUIRED** effect; whole-file write **UNNECESSARY** |
| 4 | tracked tree clean; ignored `.verify-state` | **REQUIRED** runtime state |
| 5 | one-line `src/math.js` modification | **REQUIRED** |
| 6 | one-line `src/message.js` modification | **REQUIRED** |
| 7 | requested `app.js`, `index.html`, `styles.css`; asset unchanged | final state **REQUIRED**; one whole-file rewrite **UNNECESSARY** |
| 8 | one-line `index.html` modification | **REQUIRED** |
| 9 | exactly six tracked deletions | **REQUIRED** |
| 10 | source deletion plus `archive/notice.txt` | **REQUIRED** move representation |
| 11 | one-line modification under `nested path/` | **REQUIRED** |
| 12 | requested line plus preserved external fixture line | **REQUIRED** plus external change |

No scenario escaped its disposable clone, modified Agentic Router, created a duplicate project, changed unexpected configuration, left an empty deletion placeholder, or abandoned a temporary implementation. Containment and final cleanliness were strong. Full-file writes remain a preventable risk even where the resulting diff was correct.

## 18. Local-runtime findings

The exercised inference path is **PROVEN local**:

- isolated DSH settings selected only `ollama-local/qwen3.8:27b`;
- the provider URL was loopback `http://127.0.0.1:11434/v1`;
- all 12 `request/context` events named `ollama-local/qwen3.8:27b` with 262,144 context;
- 93 durable model-message sources named the same provider/model pair;
- `ollama ps` showed digest prefix `22130167c4c2`, 17 GB, 32,768 context, and 100% GPU;
- DSH telemetry was disabled at launch;
- `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GROQ_API_KEY`, `GOOGLE_API_KEY`, `CEREBRAS_API_KEY`, and `DEEPSEEK_API_KEY` were absent;
- no Agentic Router or cloud-provider inference was invoked.

The stronger claim that no external packet was emitted is only **PLAUSIBLE**. No packet capture, deny-all-except-loopback firewall, or equivalent network-level control was used, and DSH's optional network tools were not exercised.

## 19. Cross-model comparison

### Direct answers to the decision questions

| Question | Answer |
|---|---|
| Is Qwen3.8 a reliable DSH agent model? | **PROMISING BUT UNRELIABLE**. It is the best tested model, but Test 1 failed exact bytes and Test 7 lacked a final answer. |
| Is native calling more reliable than Qwen3-Coder? | **YES, PROVEN** in these traces: zero pseudo-tools and 68 native shared-scenario calls, versus Qwen's pseudo-tool/no-effect failures. |
| Does it converge and stop better? | **YES overall**: 68 shared calls versus 116/100/75 and no management loop; qualified by slow Test 4 and Test 7 `max-tokens`. |
| Is it better than GPT-OSS? | **YES** on correctness, syntax, recovery, constraints, and workspace cleanliness. GPT is lighter; controlled speed evidence is unavailable. |
| Does it preserve long-session invariants? | **YES for final executable state, PROVEN** by browser behavior; terminal narration still failed. |
| Does it handle missing capabilities intelligently? | **YES, PROVEN** for delete and move via bounded PowerShell commands plus postcondition checks. |
| Does it avoid irrelevant management tools? | **MOSTLY**: only two unnecessary `todo_write` calls; no goal/job/agent/workflow/plan calls. |
| Which failures reproduce across families? | DSH completion without effect proof, Node `spawn EPERM`, exact trailing-LF fragility, avoidable rewrites, and long-session terminal/semantic stress. |
| Best current DSH specialist candidate? | `qwen3.8:27b`, based on correctness-first evidence rather than raw pass count alone. |

### Shared scenario outcomes

| Shared test | Qwen3-Coder 30B | GPT-OSS 20B | Gemma4 26B A4B | Qwen3.8 27B |
|---|---|---|---|---|
| 1. Minimal file | False completed; zero call/effect | Exact native effect after one correction | Native effect, extra LF | Native effect, extra LF |
| 2. Narrow edit | 3 calls, exact | 3 calls, exact | 5 calls/1 error, exact | 3 calls/0 errors, exact |
| 3. Asset reuse | Pseudo-call, no effect | Correct, 12 calls/4 errors, rewrite | Correct, 8 calls/1 error, rewrite | Correct, 6 calls/0 errors, rewrite |
| 4. Verifier | Recovered, three executions | Failed; fabricated marker | Correct two-run recovery | Correct two-run recovery after compatibility adaptation |
| 5. Build | Correct, 24 calls, rewrite | Correct, 20 calls, extra package change | No effect; malformed loop | Correct, 8 calls, one-line diff |
| 6. Stop after success | Correct, 6 calls | Artifact correct; no in-DSH pass | Correct direct pass; no post-pass run | Correct direct pass; no post-pass run, 7 calls |
| 7. Continuity | Final semantic failure | Final browser state passed after repair | Final semantic failure | Final browser state passed; final narration hit max tokens |
| 8. No recreate | Correct effect, then 49-call non-terminal loop | Correct effect and terminal | Correct effect and terminal | Correct effect and terminal, 6 calls |

### Decision matrix

| Metric | Qwen3-Coder 30B | GPT-OSS 20B | Gemma4 26B A4B | Qwen3.8 27B |
|---|---|---|---|---|
| Minimal task success | **FAILED** | **PROVEN** | **FAILED** exactness | **FAILED** exactness |
| Native tool reliability | Pseudo-tool failures | Native; 46/138 marked errors | Native; 23/123 marked errors | Native; 2/103 marked errors |
| False completions | Tests 1/3; Test 8 no end | Test 4 false effect | Exact/no-effect completed turns | Test 1 exact mismatch completed |
| Useful tool calls | Not classified; 116 shared total | Not classified; 100 shared/138 total | Not classified; 75 shared/123 total | 49 required + 37 useful; 68 shared/103 total |
| Redundant calls | Extra verifier; 21 management cycles | Numerous invalid/discovery calls | At least 8 non-advancing | 6 clearly unnecessary; 11 optional |
| Invalid tools | Textual pseudo-tools | No unknown tool documented | 2 unknown names | 0 |
| Malformed arguments | Path/edit thrashing; no aggregate | 46 marked errors, not all malformed | 21 model-caused invalid/target/no-op results | 0 JSON/schema defects |
| Execute/process repetition | Extra verifier; management loop | No post-success repeat; weak process recovery | Exact verifier; Test 5 proposal loop | Exact verifier after adaptation; no post-pass repeat |
| Correct stopping | False-success/non-terminal failures | Turns ended, but one false effect | Test 5 required cancel | 11 normal sessions; Test 7 ended max-tokens |
| Error recovery | Strong verifier; weak transport | Strong stale/path; weak verifier/process | Strong 4/6/12; failed 5 | Strong 4/5/6/12 |
| Stale-write recovery | **NOT TESTED** | **PROVEN** | **PROVEN** | **PROVEN** |
| Capability-gap adaptation | **NOT TESTED** in controlled 9/10 | Both passed | Both passed; move inefficient | Both passed; move in 5 calls |
| Constraint adherence | Failed 1/3/7 and stopping 8 | Extra file in 5; transient loss in 7 | Failed exactness/build/continuity | Failed Test 1 bytes; Test 7 semantics preserved |
| Multi-turn continuity | Final semantic failure | Final semantic pass after repair | Final semantic failure | Final semantic pass; terminal answer truncated |
| Self-report accuracy | Multiple false/misleading claims | False verifier narration | False continuity narration | 8 accurate, 3 partial, 1 misleading, 0 false |
| Workspace hygiene | Contained; rewrite/loop risk | Contained; one unrelated tracked edit | Contained; semantic damage | Contained; no unrelated final file; two rewrites |
| Final correctness | 4/8 fully proven; Test 8 effect-only | 5/8 fully proven; 5/6 qualified | 5/8 shared effects; 1/5/7 failed | 6/8 fully accepted; Test 7 effect-qualified; 10/12 full overall |
| Elapsed time | Selected values only | Not uniformly preserved | ~587 s, cold/warm and canceled | ~764.5 s summed turns, cold/warm; not comparable |
| Tokens/sec | Not comparably preserved | Not comparably preserved | Test 7 UI 138 tok/s | Test 7 UI 69 tok/s |
| Observed local footprint | 21 GB | 12 GB | 15 GB | 17 GB |

Qwen3.8 is better than GPT-OSS on correctness, native syntax, recovery, constraint preservation, and workspace minimality. It used fewer shared and total calls and avoided GPT's fabricated verifier state and extra `package.json` edit. GPT-OSS remains lighter at 12 GB; timing and throughput were not collected under a controlled performance protocol.

Qwen3.8's native calling is decisively more reliable than Qwen3-Coder's under the same DSH transport: all 68 shared-scenario calls were native, while Qwen3-Coder emitted textual pseudo-tools with zero effects in Tests 1 and 3 and failed to terminate after Test 8's effect. Qwen3.8 also converged with 48 fewer shared calls.

Repeated cross-family findings are more important than any one ranking:

- DSH lifecycle completion is not effect truth: every family produced at least one completed or successful-looking state that independent validation qualified or rejected.
- The Node child-process `spawn EPERM` reproduced with GPT-OSS, Gemma, and Qwen3.8 under the same Windows DSH surface.
- Exact minimal creation remains fragile: both Gemma and Qwen3.8 supplied an extra LF.
- Avoidable full-file rewrites occurred across model families.
- Long-session work remained a stress boundary: Qwen/Gemma lost semantics, GPT required repair, and Qwen3.8 preserved semantics but exhausted final output.

Correctness-first weighting makes `qwen3.8:27b` the best completed local DSH specialist candidate. It is not selected on raw pass count alone: its native conformance, recovery, final browser semantics, low error count, bounded management use, and clean working trees are collectively stronger.

## 20. Model/harness/tool-surface attribution

| Significant finding | Primary attribution | Evidence |
|---|---|---|
| Test 1 trailing LF | **MODEL** | The `write` argument itself contained `hello world\n`. |
| Test 1 lifecycle success without exact proof | **HARNESS** | DSH ended `completed`/exit 0 despite failed bytes. |
| Native calls with zero malformed arguments | **MODEL** | Same DSH catalog/transport; changing the model removed prior failures. |
| Test 4 broad compatibility exploration | **MODEL** | A narrower installed-runtime check was available. |
| Missing nested `pwsh` executable | **TOOL SURFACE** | DSH's Windows process host exposed Windows PowerShell but not PowerShell Core in PATH. |
| Node `spawn EPERM` | **HARNESS** | Reproduced across three models under the same DSH Windows sandbox. |
| Stale-version protection | **HARNESS** | DSH independently rejected the changed-since-read edit. |
| Correct stale reconciliation | **MODEL** | Qwen3.8 reread, recognized, preserved, retried, and verified. |
| Missing native delete/move | **TOOL SURFACE** | Specialized operations were absent by construction. |
| Correct delete/move fallback | **MODEL** | Qwen3.8 selected PowerShell and proved the real effects. |
| Test 9 to-do/list overhead | **MODEL** | Calls were valid but not necessary for authority or evidence. |
| Test 3/7 whole-file writes | **MODEL** | Targeted edit was available and safer. |
| Test 7 final `max-tokens` below configured maximum | **UNKNOWN** | Trace proves provider stop reason, not the limiting layer. |
| Test 7 as context loss | **FAILED** attribution | All semantic state was retained; UI context use was 13%. |
| Broad catalog caused prior model errors | **UNKNOWN** | Plausible, but no reduced-catalog control was run. |

No observed failure requires a `CONTEXT / SESSION` attribution. The continuity result is affirmative evidence against context loss in this run.

## 21. Implications for Agentic Router

Qwen3.8 is the first tested DSH model that combines reliable native syntax, successful controlled recovery, capability-gap adaptation, correct final browser semantics, and comparatively low call volume. If a future Router experiment needs one local DSH specialist candidate, it should start with this exact digest rather than Qwen3-Coder, GPT-OSS, or Gemma.

That does not justify adopting DSH as Router execution authority. Test 1 proves that a native call plus `completed` plus process exit 0 can still violate a byte-level objective. Test 7 proves that a correct effect can still lack a usable terminal answer. DSH's generic PowerShell capability also exceeds Router's deliberately structured process/file authority.

Any later external-agent experiment must keep Router Host-authoritative for workspace confinement, tool policy, approvals, stale-state binding, observed effects, validation, changed-file review, budgets, and terminal truth. DSH messages, calls, process results, and narration would remain untrusted evidence. This run did not implement or authorize that experiment.

Necessary now: preserve this baseline and continue the existing Qwen-first Router acceptance sequence independently of DSH. Useful later: test a reduced DSH catalog and hard output budget around this exact artifact. Probably unnecessary now: DSH integration, Router redesign, generic shell exposure, or model-specific tuning before the baseline is reviewed.

## 22. Recommended next experiments

1. Repeat Tests 1, 4, 5, 7, and 9 three times with the exact digest to measure run-to-run stability; do not mix repeats into this baseline.
2. Isolate the Test 7 `max-tokens` stop by capturing the raw Ollama completion limit, DSH effective per-request limit, reasoning-token accounting, and provider finish reason.
3. Run a reduced-catalog control with only file/search/process tools to measure whether the two Test 9 to-do calls and discovery overhead disappear without changing correctness.
4. Add a deterministic exact-byte postcondition outside DSH to quantify how often the trailing-LF failure reproduces across Qwen3.8 and Gemma.
5. Run a dedicated warm/cold performance benchmark for latency, tokens/second, energy, and memory. The present correctness timings are not comparable performance evidence.
6. Instrument deny-all-except-loopback networking or packet capture if exhaustive offline proof becomes a decision requirement.
7. Test cancellation during an active process and recovery after resume with Qwen3.8; this was not part of the 12-scenario comparison.
8. Keep any tuned prompt, altered template, context, adapter, tool catalog, or policy result under `TUNED / NON-BASELINE`.

Qwen3.8 verdict: **QWEN3.8 27B CLEARLY BEST SO FAR**

Best tested local DSH model: **qwen3.8:27b**

DSH verdict: **CONTINUE EVALUATION**
