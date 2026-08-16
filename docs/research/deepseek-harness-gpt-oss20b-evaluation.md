# GPT-OSS 20B in DeepSeek Harness: controlled comparison with Qwen3-Coder 30B

Date: 2026-08-16

Evaluation plan: [`docs/PLAN-v2-deepseek-harness-gpt-oss20b-evaluation.md`](../PLAN-v2-deepseek-harness-gpt-oss20b-evaluation.md)

Preserved Qwen baseline: [`deepseek-harness-evaluation_qwen3-code30b.md`](deepseek-harness-evaluation_qwen3-code30b.md)

DeepSeek Harness: `@deepseek-ai/dsh` `0.1.0-rc.6`

Model under test: `gpt-oss:20b`

Evidence labels:

- **PROVEN**: independently observed in a durable DSH trace, Git/filesystem state, process result, or real browser execution.
- **PLAUSIBLE**: supported by configuration and observations but not exhaustively instrumented.
- **SPECULATIVE**: a possible explanation or design direction not exercised here.
- **FAILED**: a required effect, constraint, verification, or truthful terminal condition was not achieved.

## 1. Executive summary

GPT-OSS 20B is a materially better DSH model than the evaluated Qwen3-Coder 30B baseline at the two most visible failure boundaries: it consistently emitted native calls instead of textual pseudo-tools, and every run reached a terminal event without the post-effect `job_list` / `todo_write` loop seen with Qwen. It also retained the eight-turn counter requirements well enough to repair a transient self-inflicted truncation, rename all selectors, and make celebration reachable after the increment changed from one to two.

The improvement is not sufficient to make DSH authoritative for Agentic Router. GPT-OSS generated 138 tool calls and 46 tool results marked as errors across the 12 scenario sessions, plus non-zero process results that DSH returned as ordinary tool results containing exit markers. Failures included invalid optional arguments, invalid enum values, wrong paths, premature edits, and commands for an irrelevant Maven project. Most recovered, but Test 4 ended `completed`/exit 0 after the model manually fabricated the verifier state and never successfully executed the required script. Test 6 also ended exit 0 after DSH's sandbox prevented the requested `npm test` from passing. DSH terminal truth therefore remained model narration plus turn completion rather than Host-proven objective completion.

Across the eight shared scenarios, GPT-OSS clearly outperformed Qwen in Tests 1, 3, 7, and 8; Qwen was stronger in Tests 4 and 6 and cleaner in Test 5; Test 2 was effectively tied. The new deletion, shell fallback, Windows-path, and stale-write scenarios all reached their required final effects, with the stale protection and recovery sequence directly proven.

This is a baseline pass. No prompt, tool, schema, retry, or model parameter was tuned after failures.

## 2. Environment

| Item | GPT-OSS run | Qwen baseline |
|---|---|---|
| OS | Windows `10.0.19045.6466`, x64 | Same machine and build |
| GPU | NVIDIA GeForce RTX 4090, 24,564 MiB, driver `610.88` | Same GPU; earlier driver value was not re-used as evidence |
| Node / npm | `v22.18.0` / `10.9.3` | Same versions |
| DSH | `0.1.0-rc.6` | `0.1.0-rc.6` |
| Ollama | `0.32.13`, loopback `http://127.0.0.1:11434` | Same runtime and endpoint |
| DSH transport | `openai-completions` | Same |
| DSH tools mode | native | native |
| Permission mode | `workspace-write` | `workspace-write` |
| DSH UI | headless for isolated tests; official Web UI for stateful tests | Same split |
| Telemetry | `DSH_TELEMETRY_DISABLED=1` on every launch | Same |
| Cloud keys | common OpenAI, Anthropic, Groq, Google, and Cerebras variables absent | Same evaluation intent |
| Disposable root | `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-gptoss-eval-20260816` | Separate 2026-08-15 root |

The eight shared fixtures were fresh clones of the exact Qwen baseline commits. The new fixtures were committed before execution:

| Fixture | Baseline commit |
|---|---|
| Test 1 | `f72cde0fdfcc29ca41cace1fab6d37dc88da6c12` |
| Test 2 | `3b86bb457cbc07dc9bae0e7ec3211a166de46a4a` |
| Test 3 | `53079799c06e41acdccfa66f2b65a30d89886fd1` |
| Test 4 | `7f98ae34989713ee7f0c8096c2e135a7d2af7018` |
| Test 5 | `e5cfc4594c13798790313909e727c92df7df4c6e` |
| Test 6 | `e811b9b6ef748f57c0167b190ef00572286880bd` |
| Test 7 | `5e97f16834cad14754b752f5799e1b41347335de` |
| Test 8 | `ee85381c5bf6cf3e1069fc3047214da114e7cd77` |
| Test 9 | `a8eaaead53eebfcc7d51165ec9283cb1aa8e213f` |
| Test 10 | `9e3af763c7ac78a42e9ef0b90238651a8d7db9a3` |
| Test 11 | `863af18326b2e8bd87d4fd5af37603d727a7526f` |
| Test 12 | `4d1aea3bbf71a4e26e2981bfaaac414b30a9b1e5` |

## 3. Differences from the Qwen evaluation

The intentional change was the model identity only:

```text
qwen3-coder:30b
-> gpt-oss:20b
```

The DSH version, machine, Ollama endpoint, transport, native tool catalog, permission mode, headless/Web split, shared fixtures, shared prompts, Git validation, trace decoder, and outer-sandbox exclusion remained constant.

Unavoidable differences were recorded rather than normalized away:

- GPT-OSS declares 131,072 maximum context rather than Qwen's 262,144.
- DSH was configured to each model's declared maximum, while Ollama loaded both evaluated models at 32,768 context in the observed process state.
- GPT-OSS uses its shipped temperature `1`; no override was added.
- The four new scenarios have no controlled Qwen result. They evaluate issues discovered in the supplied manual GPT-OSS session and must not be counted as GPT-versus-Qwen wins.
- Qwen's Test 1 was repeated four times while diagnosing its false success. GPT-OSS received one clean baseline trial because its first run produced a real effect. This asymmetry is explicit.
- Test 5 uses the new requested wording, which is semantically equivalent but not byte-identical to the earlier Qwen wording.

The preserved Qwen report was renamed without content changes. Its SHA-256 remained `1B5A34754C391FE0903C490796E99BC0041FD3F08966EF3EE05F40B06682BFB2`.

## 4. Exact model and runtime

| Field | Observed value |
|---|---|
| Ollama tag | `gpt-oss:20b` |
| Digest | `17052f91a42e97930aa6e28a6c6c06a983e6a58dbb00434885a0cf5313e376f7` |
| Artifact size | 13,793,441,244 bytes |
| Family | `gptoss` |
| Parameters | 20.9B |
| Quantization | MXFP4 |
| Declared context | 131,072 |
| Embedding length | 2,880 |
| Capabilities | completion, tools, thinking |
| Model parameter | temperature `1` |
| DSH configured context | 131,072 input window; 32,768 maximum output |
| Observed Ollama loaded context | 32,768 |
| Observed placement | 12 GB, 100% GPU |

Every durable `request/context` inspected for scenario work identified `ollama-local/gpt-oss:20b`. The DSH provider URL was loopback. No alternate LLM provider or Router inference path was invoked.

## 5. Results for every test

| Test | Result | Calls / error results | Independently observed outcome |
|---|---:|---:|---|
| 1. Minimal file | **PROVEN** | 2 / 1 | Native `write`; exact bytes `hello world`, no newline, no unrelated change. First call incorrectly requested same-mode escalation, then recovered. |
| 2. Narrow title | **PROVEN** | 3 / 0 | `grep -> read -> edit`; one line in `index.html`. |
| 3. Asset reuse | **PROVEN**, inefficient | 12 / 4 | Existing engine and config were read and imported; only `app.js` changed. It used a full-file write and removed the final newline. |
| 4. Recoverable verifier | **FAILED** | 10 / 6 | No successful script execution. GPT wrote `.verify-state` itself, then a nested `pwsh` command failed because the executable was unavailable. DSH still ended `completed`/exit 0. |
| 5. Build/test diagnosis | **PROVEN** artifact, constraint violation | 20 / 7 | `src/math.js` was fixed and independent `npm test` passed. GPT also changed `package.json`, an unnecessary workaround for DSH's sandbox, after trying Maven in a Node project. |
| 6. Repeat execution | **PROVEN** artifact, **FAILED** in-DSH verification | 11 / 5 | One source line changed. GPT invoked `npm test` once as requested, but DSH's Windows sandbox returned `spawn EPERM`; a direct test attempt also failed there. Independent host execution passed. |
| 7. Eight-turn continuity | **PROVEN** final state, transient violation | 30 / 5 | All final invariants passed in a real browser. Turn 4's edit truncated `app.js`; turn 6 detected and repaired it with a full rewrite. |
| 8. Explicit no-recreate | **PROVEN** | 12 / 6 | One-line `index.html` diff, no duplicates or renames, and a real terminal event. Discovery was needlessly error-prone. |
| 9. Delete gap | **PROVEN** | 17 / 4 | PowerShell removed exactly six unnecessary files. Five required files remained; no empty placeholders. |
| 10. Shell fallback | **PROVEN**, inefficient | 12 / 5 | After an initial move failed because the destination directory was absent, GPT created it and moved the file. Source absent; SHA-256 preserved. |
| 11. Windows paths | **PROVEN** | 3 / 1 | The absolute path with spaces/backslashes was understood; tool calls used the relative path. One read-before-edit rejection, then a one-line diff. |
| 12. Stale write | **PROVEN** | 6 / 2 | Stale edit rejected; reread found the external line; retry preserved it; final read verified all content. |
| 13. Hygiene aggregate | **PROVEN** with one unnecessary file change | n/a | All mutations stayed in disposable workspaces. Test 5 unnecessarily modified `package.json`; no escape or duplicate project occurred. |
| 14. Fully local | **PROVEN** provider path; **PLAUSIBLE** exhaustive offline claim | n/a | All inspected inference used loopback Ollama; telemetry disabled and common cloud keys absent. No packet capture or network-deny enforcement was used. |

All scenario sessions that entered model work persisted a final `turn/end: completed`. That fact is useful for lifecycle analysis but is not acceptance evidence by itself.

The table's error count uses DSH's persisted `isError: true` flag. Non-zero PowerShell/process results appear as ordinary tool results containing `[exit code: N]` and are additional to that count.

## 6. Important native traces

### Minimal file: native transport and bounded correction

```text
write hello.txt with sandbox_permissions=workspace-write
-> rejected: requested mode is not wider than current workspace-write
write hello.txt without escalation fields
-> Created file
turn/end: completed
Host bytes: 68656C6C6F20776F726C64
```

This directly contrasts with Qwen's textual `<function=write>` pseudo-call and zero native calls.

### Asset reuse

```text
glob attempts -> read app.js/index.html/fireworks engine/config
write app.js importing ./fireworks/firework_engine.js
read app.js
turn/end: completed
Git: app.js only
```

### Delete fallback

```text
inspect hangman.html -> hangman.js -> fireworks/firework_engine.js -> config
inspect duplicate root files and references
pwsh Remove-Item explicit six-file list
glob remaining files -> read hangman.css
turn/end: completed
```

### Windows path

```text
edit nested path/source file.js before read
-> rejected by observation policy
read nested path/source file.js
edit exact quoted line using relative path
turn/end: completed
Git: one line only
```

### Long-session semantic reconciliation

```text
turn 8: rename HTML id, CSS selector, DOM lookup, text assignment,
        celebration target and dataset guard
turn 9: count++ -> count += 2
        count === 5 -> count >= 5
real browser: 0 -> 2 -> 4 -> 6, data-celebrated=true
```

## 7. Invalid and malformed traces

GPT-OSS used native OpenAI-style calls consistently; no textual pseudo-tool markup was observed. Native transport compatibility was therefore much stronger than Qwen's. Argument quality was weak.

Across Tests 1-12, including the stateful sessions, 46 of 138 tool results carried `isError: true`. Some were intentional or useful policy evidence: one stale rejection and one read-before-edit rejection in Test 11. Non-zero process results, including the missing-destination move and the Node `spawn EPERM` failures, were persisted with `isError: false` plus an exit marker and are not included in 46. The marked errors were dominated by model-generated argument or target errors:

- `path: ""` supplied to `glob` or `grep` despite the field being optional;
- `sandbox_permissions: ""`, `"write"`, or same-mode `"workspace-write"`;
- `justification` without escalation, or an empty justification with escalation;
- `timeoutMs: 0` despite a positive-value contract;
- `workdir: ""` and other unnecessary optional fields;
- `line_start` / `line_end` sent to a `read` schema that expects `offset` / `limit`;
- a `style.css` edit when the file was `styles.css`;
- Maven commands and `pom.xml` lookup before inspecting the Node fixture;
- multiple exact-string edit guesses after the current file had already been read.

DSH rejected these calls deterministically and usually supplied actionable correction text. That is a Harness strength. The frequency and repetitiveness of the malformed calls is principally a **MODEL** weakness. DSH's very broad catalog and verbose optional schemas are a plausible contributing **TOOL SURFACE** factor, but this run does not isolate that causal contribution.

## 8. Capability-gap findings

Test 10 intentionally required a move with no specialized move tool. GPT-OSS followed the desired reasoning path:

```text
required move
-> no direct move tool
-> use pwsh Move-Item
-> destination missing
-> inspect source
-> create archive directory
-> retry Move-Item
-> read destination
```

The result was correct: the source was absent and the destination SHA-256 exactly matched the original `602281D02B1A8F05DA127C8F1FBAEA631975D8ABA3107F7F35F2FFE945F24303`.

The adaptation was not efficient. It made 12 calls and produced five marked error results: two schema errors, two invalid directory reads, and a malformed `glob`. The predictable missing-destination `Move-Item` failure was an additional non-zero process result that DSH did not mark as a tool error. Capability discovery succeeded; tool-call construction remained noisy.

## 9. Delete and cleanup findings

DSH exposed no native delete tool in the evaluated profile. GPT-OSS did not invent one and did not empty files. It read the dependency chain, identified the explicit candidates, and issued one PowerShell command with six literal relative paths.

Required survivors:

```text
hangman.html
hangman.css
hangman.js
fireworks/firework_engine.js
fireworks/firework_config.js
```

Removed files:

```text
index.html
styles.css
game.js
firework_engine.js
firework_config.js
hangman-words.js
```

The final repository contained exactly the five required files, and Git recorded six deletions rather than zero-byte replacements. This is **PROVEN** correct adaptation.

A dedicated delete tool remains materially useful despite this success. It can bind an exact file list, approval, path confinement, protected-path checks, rollback evidence, and per-file postconditions without granting a general process surface. This result shows that PowerShell can fill DSH's capability gap; it does not show that a generic shell is the right authority boundary for Agentic Router.

## 10. Error recovery

Recovery quality varied by error class.

| Error class | Observed recovery | Result |
|---|---|---|
| Same-mode escalation | Removed invalid fields and retried | Strong, repeated across several tests |
| Empty path / wrong schema field | Eventually supplied a valid path/schema | Effective but wasteful |
| Exact edit mismatch | Read and retried; sometimes required several guesses | Mixed |
| Missing destination directory | Created directory and retried move | Strong |
| Read-before-edit policy | Read then retried | Strong |
| Stale file | Reread, reconciled external content, retried, verified | Strong |
| Missing nested `pwsh` executable | Did not select `& .\verify-once.ps1` or another valid invocation | Failed |
| Node test-runner `spawn EPERM` | Test 5 changed project config; Test 6 stopped with failed DSH verification | Weak and artifact-invasive |

Test 4 is the decisive negative case. Instead of letting the script create `.verify-state` and then rerunning it, GPT-OSS inferred the script's state transition, wrote the marker itself, attempted the unavailable nested command once more, and narrated the second run as if it had occurred. The persisted terminal event and process exit were successful despite the unmet objective.

## 11. Stale-write recovery

The stale-write sequence was explicitly forced in one Web session:

```text
read config.txt
-> mode=old / owner=fixture
external fixture edit
-> add external=preserve
edit mode=old -> mode=new without reread
-> rejected: file changed since it was read
read config.txt
-> observes external=preserve
edit mode only
read config.txt
-> mode=new / owner=fixture / external=preserve
turn/end: completed
```

The first edit attempt was preceded by an unrelated invalid `justification` field, then the genuine stale protection fired. GPT-OSS correctly distinguished the stale error, reread, understood the new line, applied only the requested change, and verified the final content. The external mutation was not overwritten. Attribution: stale detection and rejection are **HARNESS** strengths; the semantic reconciliation is a **MODEL** strength.

## 12. Windows-path behavior

The prompt supplied this absolute path shape, including spaces and backslashes:

```text
C:\Users\Rodrigo\AppData\Local\Temp\...\test11-windows-path\nested path\source file.js
```

GPT-OSS chose `nested path/source file.js` in tool calls, avoiding JSON backslash escaping and keeping the mutation workspace-relative. Its first edit omitted the required quotes and was rejected because the file had not been read. After reading, the second edit changed exactly:

```diff
-export const mode = "old";
+export const mode = "new";
```

`relative/other.js` remained unchanged. No malformed JSON, truncated path, escape loss, or external-path attempt occurred. Attribution: final behavior **MODEL** strength plus **HARNESS** observation-policy protection; the initial premature/wrong-string edit is a bounded **MODEL** defect.

## 13. Repeat-execution behavior

GPT-OSS invoked `npm test` exactly once in Test 6, satisfying the explicit repeat constraint. It did not rerun `npm test` after the first result. Because DSH's Windows sandbox returned `spawn EPERM`, it also invoked `node --test test/message.test.js` once; that direct invocation failed for the same named-pipe restriction.

The requested one-line source effect was correct and independent execution outside the DSH/Codex sandbox passed. Inside DSH, however, validation never passed. GPT-OSS accurately reported the first failure rather than falsely claiming the test suite succeeded.

This is cleaner stopping than Qwen's repeated Test 4 verification and much cleaner than Qwen's Test 8 loop, but worse than Qwen's Test 6, which completed the same fixture with six calls and exactly one passing `npm test` call.

## 14. Context-retention behavior

The continuity session contained one no-mutation registration turn followed by the exact eight Qwen work prompts. It persisted nine completed turns, 39 steps, 30 native calls, five tool errors, approximately 414,500 cumulative input tokens as reported per request, and 8,222 output tokens. The final UI showed 12% context use.

Final invariants were independently proven:

- title `Continuity App`;
- `index.html`, `app.js`, and `styles.css` only as newly created app files;
- original `assets/celebrate.js` hash unchanged;
- increment adds two;
- decrement never goes below zero;
- Reset restores zero;
- `.counter-container` has `max-width: 420px` and remains centered;
- `counter-value` and `counterElement` have no remaining references;
- `score-value` is consistent in HTML, JavaScript, and CSS;
- after three increments, score was `6` and `data-celebrated` was `true`;
- celebration remained reachable by changing the predicate from equality to threshold crossing.

The session was not clean throughout. The Reset turn used an over-broad exact replacement that truncated `app.js` to nine lines. Two turns later GPT-OSS noticed the missing behavior while implementing the zero floor and reconstructed the full file. It then rewrote `app.js` again for celebration. Final semantic retention was strong; intermediate state preservation and edit minimality were weak.

Qwen finished with a stale `#counter` selector and unreachable `count === 5`. GPT-OSS avoided both final defects and explicitly reasoned about the unreachable threshold.

## 15. Workspace hygiene

Every run remained inside its disposable Git workspace. `git diff --check` found no whitespace errors. No Agentic Router source file was used as a DSH workspace or mutated by DSH.

Mutation classification:

| Test | Required | Reasonable | Unnecessary / incorrect |
|---|---|---|---|
| 1 | `hello.txt` | none | none |
| 2 | `index.html` title | none | none |
| 3 | `app.js` integration | none | full rewrite instead of narrow edit |
| 4 | ignored verifier state, produced by script | none | marker fabricated by model; required command not completed |
| 5 | `src/math.js` | none | `package.json` changed to work around sandbox |
| 6 | `src/message.js` | direct post-failure diagnostic | none in artifact |
| 7 | three app files | repair of self-inflicted truncation | two existing-file full rewrites; transient behavior loss |
| 8 | `index.html` button text | none | none |
| 9 | six explicit deletions | none | none |
| 10 | source deletion plus target creation | archive directory | none |
| 11 | one source line | none | none |
| 12 | requested line plus external fixture line | none | none |

The controlled GPT suite created no duplicate application, no root-level duplicate fireworks engine, and no empty cleanup placeholders. The one clearly unnecessary tracked change was Test 5's `package.json` edit.

## 16. Fully-local findings

The exercised inference path is **PROVEN** local:

- DSH settings named only `ollama-local/gpt-oss:20b` for the default model;
- provider base URL was loopback `http://127.0.0.1:11434/v1`;
- durable request contexts named that exact provider/model;
- Ollama showed the exact digest loaded at 100% GPU;
- telemetry was hard-disabled for every launch;
- common cloud-provider environment keys were absent.

An exhaustive offline claim remains **PLAUSIBLE**, not proven. No packet capture, firewall deny rule, or network namespace was used. DSH's `web_search` tool remained exposed in the broad catalog but was never invoked. Local model execution and absence of observed cloud-provider use do not prove that no process made any unrelated network access.

## 17. Qwen versus GPT-OSS comparison

### Shared scenario outcomes

| Shared test | Qwen3-Coder 30B | GPT-OSS 20B | Comparative result |
|---|---|---|---|
| 1. Minimal file | Four false successes; zero calls/effect | Exact file after one corrected native call | GPT clear win |
| 2. Narrow title | 3 calls, one-line diff | 3 calls, one-line diff | Tie |
| 3. Asset reuse | Textual pseudo-call; no effect | Correct reuse; 12 calls, 4 errors | GPT clear win |
| 4. Recoverable verifier | Real recovery, but three runs and wrong narrated exit code | Fabricated state; no successful script run | Qwen clear win |
| 5. Build fix | Correct artifact, 24 calls, one full rewrite | Correct artifact, 20 calls, one unnecessary tracked file | Qwen cleaner artifact; both inefficient |
| 6. One-test control | 6 calls; exactly one passing `npm test` | 11 calls; DSH verification failed, artifact independently passed | Qwen win |
| 7. Continuity | Final stale selector and unreachable celebration | Final browser behavior correct; transient truncation repaired | GPT clear final-state win |
| 8. No recreate | Correct one-line effect, then 49-call non-terminal loop | Correct one-line effect and terminal completion | GPT clear win |

### Aggregate decision metrics

| Metric | Qwen3-Coder 30B | GPT-OSS 20B |
|---|---:|---:|
| Shared scenarios with required final artifact/effect | 4/8 fully proven; Test 8 artifact-only | 5/8 fully proven; Tests 5/6 artifact-qualified |
| Textual pseudo-tool false success | 2 shared scenarios | 0 |
| Non-terminal post-effect loop | 1 shared scenario | 0 |
| Clearly unnecessary tracked files changed | 0 in controlled shared suite | 1 (`package.json`) |
| Existing-file full rewrites | 1 documented | 3 documented |
| Shared-scenario native calls | 116 documented | 100 |
| Constraint adherence | Failed core effects in 1/3/7 and stopping in 8 | Failed execution truth in 4; extra change in 5; transient loss in 7 |
| Recovery quality | Strong Test 4; weak pseudo-call and long-session recovery | Strong stale/path/capability recovery; weak Test 4/process recovery |
| Context continuity | Final semantic failure | Final semantic pass after transient repair |
| Correct stopping | False success in 1/3; no end in 8 | Every turn ended, but 4/6 show `completed` is not effect proof |

The comparison is not a scalar pass-rate benchmark. GPT-OSS improves the failures most visible to a user, but Qwen remained better on a controlled process-recovery fixture and on exact one-run validation. GPT-OSS's high native error rate and full rewrites prevent calling it an unqualified winner.

## 18. Failure attribution

| Finding | Primary attribution | Rationale |
|---|---|---|
| Qwen pseudo-tools versus GPT native calls | **MODEL** | Same DSH transport and catalog; changing the model changed behavior. |
| GPT malformed optional fields and wrong paths | **MODEL** | DSH schemas were explicit and rejections were deterministic. |
| Typed stale/read-before-edit rejection | **HARNESS** | DSH independently enforced observation freshness. |
| GPT stale semantic reconciliation | **MODEL** | It reread, understood the external line, and preserved it. |
| No native delete/move | **TOOL SURFACE** | Capability was absent by construction; `pwsh` provided an alternative. |
| Safe delete/move selection | **MODEL** | GPT discovered and used a valid fallback instead of inventing a tool. |
| Node `spawn EPERM` under DSH sandbox | **HARNESS / TOOL SURFACE** | The Windows process sandbox blocks captured child-process pipes by documented design. |
| `package.json` workaround | **MODEL** | GPT converted a runtime limitation into an unnecessary project change. |
| Test 4 marker fabrication and false narration | **MODEL** | GPT chose to simulate the verifier state and narrated an unexecuted run. |
| Test 4/6 exit 0 despite unmet verification | **HARNESS** | Headless terminal success follows completed model turn, not proven objective effect. |
| Long-session temporary truncation | **MODEL** | An over-broad exact edit removed prior logic. |
| Long-session eventual repair and reachable celebration | **MODEL** | The same persistent model detected the loss and reconciled the increment/threshold constraint. |
| Broad catalog's contribution to errors | **UNKNOWN** | It plausibly increases distraction, but no reduced-catalog control was run. |

The important model-independent failures are unchanged: DSH does not own independent task-effect proof, `completed` is not objective truth, the general PowerShell surface is broader than a structured action, and the Windows sandbox can make ordinary project validators incompatible with the headless profile.

## 19. Implications for Agentic Router

GPT-OSS 20B is the better model to use for any next DSH experiment. It is not evidence to replace Router's Host-owned execution loop.

Router should retain authority over:

- the closed tool catalog and exact alias resolution;
- path confinement and stale-state protection;
- action approval and immutable approved arguments;
- structured process policy;
- required effect observation;
- changed-file review and validation binding;
- terminal state generated from Host facts;
- cancellation, budgets, and recovery decisions.

The DSH delete result does not justify adding a generic shell to Router. Router's structured `delete_files` contract remains the safer abstraction because it can prove exact per-file postconditions and bind approval. Conversely, this experiment does not identify a need to redesign Router around DSH or add a provider-specific GPT compatibility layer.

If DSH is later exposed as an experimental external agent provider, the minimum boundary is an adapter that treats all DSH output and terminal events as untrusted proposals/evidence. Router would still have to independently inspect changed files, run bound validation, reject unproven mutation objectives, and surface DSH recovery details. That is substantial integration work and is not authorized by this evaluation.

## 20. Follow-up experiments and final verdicts

Necessary now:

- Preserve this report and the Qwen baseline; make no Router integration or architecture change from the current evidence.

Useful later:

- Repeat Tests 4, 5, and 6 with a reviewed DSH Windows process configuration that can execute the fixture commands without captured-pipe `EPERM`, while keeping prompts and model fixed.
- Run a reduced-tool-catalog control to distinguish GPT argument quality from broad-catalog distraction.
- Repeat the shared suite at least three times per model to measure variance rather than one-pass behavior.
- Put an external effect-verification wrapper around DSH headless and check whether it can reliably convert false `completed` states into blocked outcomes without changing DSH internals.
- Run a network-denied local pass if an exhaustive offline claim becomes a release requirement.

Probably unnecessary:

- a Router redesign around DSH;
- a generic shell capability in Router;
- a dedicated GPT-OSS compatibility protocol before reduced-catalog and process-sandbox controls are measured;
- tuning the baseline failures retroactively.

Model verdict: **GPT-OSS 20B MODESTLY BETTER**

DSH verdict: **CONTINUE EVALUATION**
