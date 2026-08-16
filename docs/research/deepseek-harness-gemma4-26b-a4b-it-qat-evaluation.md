# Gemma4 26B A4B IT QAT in DeepSeek Harness: controlled baseline

## 1. Executive summary

Gemma4 26B A4B IT QAT is viable for further DSH experimentation, but it is not reliable enough to own coding-task completion. The direct viability answer is **PROMISING BUT UNRELIABLE**. It used native calls consistently and produced the required final effect in 9 of 12 executable scenarios. The three failures were materially different: Test 1 created the requested file with an extra line feed; Test 5 entered a deterministic malformed-process loop and made no fix; Test 7 ended with broken long-session invariants while claiming preservation.

The strongest result is tool transport. Unlike Qwen3-Coder 30B, Gemma never emitted textual pseudo-tool markup in this suite. Across the 12 official scenario sessions it made 123 native calls and received 23 `isError: true` results, versus GPT-OSS's preserved 138 calls and 46 marked errors. This is not proof that every successful call was useful: Gemma still hallucinated two unavailable tool names, repeatedly supplied invalid optional arguments, and used 21 calls for a simple move.

Gemma was stronger than GPT-OSS on the intentionally recoverable verifier and on stopping after the first passing post-fix test. GPT-OSS remained stronger on exact minimal creation, build diagnosis, and final long-session correctness. Qwen remained the weakest native-transport fit. With correctness weighted above speed, GPT-OSS remains the best current DSH candidate, Gemma is competitive in tool transport and recovery, and none of the three establishes DSH as an authoritative Agentic Router runtime.

The principal DSH finding is unchanged across models: `turn/end: completed` and headless exit 0 describe model-turn lifecycle, not independently proven task completion. Gemma supplied new evidence for that gap: the exact-content mismatch in Test 1 and two Test 7 turns that completed after reading a file without performing the requested mutation.

Evidence labels in this report mean:

- **PROVEN:** directly established by durable native traces plus filesystem, Git, process, or browser state.
- **PLAUSIBLE:** supported by the observed configuration and traces but not exhaustively instrumented.
- **SPECULATIVE:** a hypothesis requiring a controlled experiment.
- **FAILED:** the requested effect, constraint, or terminal behavior was disproven.

## 2. Test environment

| Item | Observed value |
|---|---|
| Date | 2026-08-16, America/Sao_Paulo |
| OS | Windows `10.0.19045.6466`, x64 |
| GPU | NVIDIA GeForce RTX 4090, 24,564 MiB, driver `610.88` |
| DSH | `0.1.0-rc.6` |
| Ollama | `0.32.13` |
| Ollama endpoint | `http://127.0.0.1:11434`; DSH OpenAI-compatible endpoint `http://127.0.0.1:11434/v1` |
| DSH provider | custom `ollama-local`, `openai-completions` |
| DSH tool mode | `native` |
| Permission mode | `workspace-write` |
| DSH surfaces | headless for isolated tests; official DSH Web UI for Tests 7 and 12 |
| Telemetry | `DSH_TELEMETRY_DISABLED=1` on every DSH launch |
| Common cloud keys | OpenAI, Anthropic, Groq, Google/Gemini, and Cerebras variables absent in the evaluation environment |
| Disposable root | `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-gemma4-eval-20260816` |

All scenario work occurred in disposable Git clones. The Agentic Router checkout was used only to store the plan and report. No Router inference, build, E2E suite, architecture change, or DSH integration was run.

The 12 fixture commits were identical to the preserved GPT-OSS evaluation:

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

## 3. Exact model identity

| Field | Observed value |
|---|---|
| Ollama tag | `gemma4:26b-a4b-it-qat` |
| Digest | `2dd70431afed94dd3688d790443768c1487ed086b57147ff083851116ae4c4e4` |
| Artifact size | 15,634,199,946 bytes |
| Family | `gemma4` |
| Parameters | 25.2B |
| Quantization | Q4_0 |
| Declared context | 262,144 |
| Embedding length | 2,816 |
| Minimum Ollama | 0.30.5 |
| Capabilities | completion, vision, tools, thinking |
| Projector | CLIP, 572.79M parameters |
| Shipped parameters | temperature `1`, top-k `64`, top-p `0.95` |
| DSH configured context | 262,144 input window; 32,768 maximum output |
| Observed loaded context | 32,768 |
| Observed placement | 15 GB, 100% GPU |

Durable model messages identified `ollama-local/gemma4:26b-a4b-it-qat`. No alternate model appeared in scenario traces.

## 4. Differences from previous evaluations

The intentional controlled change was:

```text
qwen3-coder:30b / gpt-oss:20b
-> gemma4:26b-a4b-it-qat
```

DSH version, Ollama version, machine, endpoint, transport, permission preset, tool mode, fixture commits, prompts, headless/Web split, Git inspection, and independent validation methods remained fixed. No Gemma-specific prompt, adapter patch, system-prompt change, tool removal, or policy tuning was introduced.

Recorded unavoidable differences:

- Test 1 included the model's cold load and took 62.6 seconds of durable turn time; timing is not directly comparable to warm trials.
- Test 5 was canceled after approximately 150 seconds because six consecutive process calls repeated the same missing-`description` schema defect and no mutation occurred. It has no terminal event.
- A first Test 6 launch stalled in DSH's title-generation path immediately after the canceled Test 5 run. It generated no task-model message, tool call, or effect and is excluded as infrastructure evidence. The fair Test 6 rerun used a fresh clone after the model was unloaded and completed normally.
- Tests 7 and 12 used the official DSH Web UI to preserve session state and inject the external concurrent edit. Other executable scenarios were headless.

The preserved comparison reports were not modified. Their SHA-256 values at the end of this run were:

- Qwen: `1B5A34754C391FE0903C490796E99BC0041FD3F08966EF3EE05F40B06682BFB2`.
- GPT-OSS: `3100CC4597E77E4B1280775B120DBF5EB1D9D2AC803878C454862A17DAE0C054`.

## 5. Test-by-test results

The call/error column counts durable `tool/call` events and tool results marked `isError: true`. A process exit code embedded in an otherwise ordinary DSH result is additional evidence and is not counted as `isError: true`.

| Test | Result | Calls / marked errors | Independently observed outcome |
|---|---:|---:|---|
| 1. Minimal file | **FAILED** exactness; **PROVEN** native effect | 2 / 0 | Native `write` created only `hello.txt`, then `read` verified it. The call explicitly sent `hello world\n`; host bytes were `68656C6C6F20776F726C640A`, not exact `hello world`. |
| 2. Narrow edit | **PROVEN** | 5 / 1 | One line in `index.html` changed from `Test App` to `Hangman App`; no other effect. One legacy editor call rejected its relative path. |
| 3. Asset reuse | **PROVEN**, mildly inefficient | 8 / 1 | Gemma inspected the existing fireworks files and changed only `app.js` to import `launchFireworks`; no duplicate engine. It recovered from one invalid escalation and used a full-file write. |
| 4. Recoverable verifier | **PROVEN** | 7 / 0 | Exactly two process executions: intentional first failure created `.verify-state`; second passed. Tracked Git stayed clean. No unnecessary third verifier run. |
| 5. Build/test diagnosis | **FAILED** | 10 / 6 | Gemma read the failing Node source/test, then issued six `pwsh` calls without required `description`. No command ran, no file changed, no terminal event was produced before controlled cancellation. Independent direct test still failed `-1 !== 5`. |
| 6. Stop after success | **PROVEN** | 11 / 2 | One-line `Hello` to `Hi` diff. `npm test` hit DSH `spawn EPERM`; a direct pre-fix test failed normally; after recovery/edit, one direct test passed. No process ran after that passing result. Independent direct host execution also passed. |
| 7. Long continuity | **FAILED** final invariants | 27 / 5 | Title, zero floor, reset, `score-value`, increment by 2, and asset reuse existed; real browser interaction worked for those pieces. `max-width: 420px` was never applied, and celebration was changed from exactly 5 to `>= 5`, causing it at 6. Two turns ended after reads without their requested effects. |
| 8. No recreate | **PROVEN** | 5 / 1 | Exactly one HTML line changed `Save` to `Save note`; no duplicate, rename, or extra file. One invalid escalation was corrected. |
| 9. Delete gap | **PROVEN**, adapted | 15 / 1 | PowerShell removed exactly six unnecessary files. Exactly five required files remained: three Hangman files and two files under `fireworks/`; no empty placeholders. |
| 10. Capability gap | **PROVEN**, inefficient | 21 / 2 | Gemma used PowerShell to create the destination and move the file. Source was absent; target SHA-256 was preserved as `602281D02B1A8F05DA127C8F1FBAEA631975D8ABA3107F7F35F2FFE945F24303`. It hallucinated case-sensitive `Glob` and later read the now-absent source. |
| 11. Windows paths | **PROVEN** | 6 / 2 | It understood the supplied absolute path but converged on relative `nested path/source file.js`. Final diff was one line; an absolute-path-only legacy editor error and one invalid escalation preceded success. |
| 12. Stale write | **PROVEN** | 6 / 2 | First usable edit received `FS_STALE_VERSION`; Gemma reread, observed `external=preserve`, changed only `mode=old` to `mode=new`, reread, and preserved all three lines. The other error was invalid escalation metadata. |
| 13. Tool discipline | **PROVEN** file/process focus; **FAILED** perfect conformance | 123 / 23 aggregate | No todo, goal, job, plan, agent, or subagent call. Two unknown tools and repeated argument defects remained. |
| 14. Workspace hygiene | **PROVEN** containment; mixed minimality | n/a | All effects stayed inside disposable clones. No duplicate project or unexpected untracked implementation. Test 7's semantic changes were harmful despite path cleanliness; Test 3 used a full rewrite for a small existing-file change. |
| 15. Fully local | **PROVEN** provider path; **PLAUSIBLE** exhaustive isolation | n/a | Loopback Ollama, exact local model identity, telemetry disabled, common cloud keys absent, and 100% GPU placement observed. No packet capture or network-deny control was used. |

Across Tests 1-12, 9 produced the required final filesystem/application effect, although Test 6 carries a DSH process-sandbox qualification. Tests 1, 5, and 7 failed their acceptance conditions. This is descriptive, not a statistical pass rate: each scenario targets a different failure class and each model received one baseline trial per new fixture.

## 6. Native tool-call traces

### Exact file creation

```text
write { file_path: "hello.txt", content: "hello world\n" }
-> success
read hello.txt
-> reports one line
turn/end: completed
Host bytes: 68656C6C6F20776F726C640A
```

Transport and effect were real; exact content was wrong. The trailing line feed was present in the model-generated argument, so this is a **MODEL** failure rather than post-write normalization by DSH.

### Correct recoverable verifier

```text
inspect fixture
pwsh verify-once.ps1
-> TRANSIENT_STATE_CHANGED; .verify-state created
inspect current state
pwsh verify-once.ps1
-> verification passed
turn/end: completed
```

The two process calls were exactly the required failure/recovery sequence. This is stronger than Qwen's unnecessary third run and GPT-OSS's fabricated marker.

### Build loop without effect

```text
glob -> read src/math.js -> read test/math.test.js
pwsh node --test                         -> missing description
pwsh node test/math.test.js             -> missing description
read package/source context
pwsh node --version / node -v / echo... -> same missing description
(six identical schema-class failures total)
external cancel at ~150 s
turn/end: absent
Git: clean; failing subtraction unchanged
```

The Harness rejection was deterministic and actionable. Repeating the same malformed shape is a **MODEL** convergence failure.

### Stale-write recovery

```text
read config.txt -> mode=old; owner=fixture
external writer -> adds external=preserve
edit with justification but no escalation -> rejected
edit from prior observed version -> FS_STALE_VERSION
read -> observes external=preserve
edit mode=old -> mode=new
read -> verifies all three lines
turn/end: completed
```

The stale guard is **HARNESS** behavior; recognizing the new state and preserving it is **MODEL** behavior. Both were proven.

## 7. Invalid tool calls

Gemma made no textual pseudo-tool calls and no malformed JSON transport was observed. Its 23 marked errors were:

| Error class | Count | Assessment |
|---|---:|---|
| `justification` without `sandbox_permissions` | 8 | **MODEL**, unnecessary optional argument |
| Missing required PowerShell `description` | 7 | **MODEL**; six repeated in Test 5, one corrected in Test 9 |
| Legacy editor rejected relative path | 2 | **MODEL / TOOL SURFACE**; a different offered editor accepted relative paths |
| Unknown tool names `ls` and `Glob` | 2 | **MODEL** tool hallucination/case error |
| Stale-version rejections | 2 | **HARNESS** protection; one intentional Test 12 event, one Test 6 recovery event |
| `old_string` and `new_string` identical | 1 | **MODEL**, no-op edit |
| Read of source after it had been moved | 1 | **MODEL**, stale target assumption |

The 23/123 marked-error ratio is materially lower than GPT-OSS's preserved 46/138 result, but marked errors are not a complete efficiency measure. Successful reads, globs, and process calls can still be unnecessary. Conversely, the intentional stale rejection is useful evidence rather than waste.

## 8. Recovery behavior

Gemma's recovery quality was bimodal.

- **PROVEN strong:** Test 4 understood the intentional transition, reran exactly once, verified success, and stopped.
- **PROVEN strong:** Test 12 reacted to `FS_STALE_VERSION` with reread, state reconciliation, a narrow retry, final verification, and completion.
- **PROVEN adequate:** Test 6 recovered from DSH's child-process `EPERM`, corrected its own invalid escalation, reread after stale protection, made the one-line change, ran one passing direct test, and stopped.
- **FAILED:** Test 5 did not change strategy after six identical missing-field rejections. It never reached a process result or edit.
- **FAILED semantic recovery:** Test 7 repaired the zero-floor requirement opportunistically in a later turn, but never repaired the missing layout and replaced the exact celebration invariant with a broader one.

This run disproves both extremes: Gemma is neither generally unable to recover nor reliably convergent. It is sensitive to the failure shape.

## 9. Capability-gap behavior

Test 9 had no native delete tool. Gemma inspected dependencies, selected PowerShell, deleted an explicit set, and verified the remaining tree. This is **PROVEN ADAPT**, not fake deletion, false success, or limitation narration.

Test 10 had no specialized move tool. Gemma eventually created the destination directory and moved the file with PowerShell, then the host proved source absence and byte preservation. The effect is **PROVEN**, but the route was inefficient: 21 calls, two marked errors, seven glob calls, and 11 PowerShell calls for one move.

The results support a narrow conclusion: Gemma can reason through a missing specialized capability when a general process tool is available. They do not justify adding a generic shell to Agentic Router. DSH's PowerShell surface remains broader than Router's structured Host-owned actions.

## 10. Windows path behavior

The prompt included an absolute Windows path containing spaces and backslashes while explicitly preferring a relative path. Gemma understood the target, but first selected `str_replace_editor` with `nested path\source file.js`; that legacy tool required an absolute slash-rooted path and rejected the call. Gemma then used workspace discovery, read the relative path, recovered from one invalid escalation argument, and applied the exact one-line edit with the ordinary `edit` tool.

Final state:

```diff
-export const mode = "old";
+export const mode = "new";
 export const keep = "unchanged";
```

There was no JSON escaping failure, manual repair, absolute-path mutation, or change to the similarly named decoy file. Final behavior is **PROVEN**; first-call tool selection was inefficient.

## 11. Stale-write behavior

DSH independently rejected the first well-formed edit after an external writer added `external=preserve`. The durable result carried `FsError` code `FS_STALE_VERSION`. Gemma then reread, explicitly identified the new third line, applied only the requested key change, and verified:

```text
mode=new
owner=fixture
external=preserve
```

Git showed one tracked file modified and `git diff --check` passed. The external change was not overwritten. This is the cleanest proof in the suite that DSH's observation-version protection and Gemma's state reconciliation can compose correctly.

## 12. Repeat-execution behavior

Test 4 executed the verifier exactly twice: one intentional failure and one success. There was no third run.

Test 6 recorded three process calls:

| Execution | Classification | Result |
|---|---|---|
| `npm test` before edit | **JUSTIFIED** diagnosis | DSH Windows sandbox returned child-process `spawn EPERM` |
| `node test/message.test.js` before edit | **JUSTIFIED** alternate diagnosis | Real assertion failure, `Hello !== Hi` |
| `node test/message.test.js` after edit | **JUSTIFIED** required validation | Passed |

No process ran after the passing result. The explicit stopping constraint therefore passed. This is cleaner than Qwen's extra Test 4 verifier run and preserves GPT-OSS's positive no-post-success behavior, while also obtaining a passing in-DSH direct validation that GPT-OSS did not.

Test 5 is the negative counterexample: six calls repeated the same argument defect without ever executing. Although these were not repeated process executions, they were a repetitive proposal loop and required external cancellation.

## 13. Tool-surface discipline

The 123 official native calls were distributed as follows:

| Tool | Calls |
|---|---:|
| `read` | 43 |
| `pwsh` | 26 |
| `edit` | 21 |
| `glob` / invalid case variant `Glob` | 19 (18 valid, 1 invalid) |
| `write` | 7 |
| `grep` | 3 |
| `str_replace_editor` | 3 |
| unknown `ls` | 1 |

No todo, goal, job, agent, subagent, plan-mode, or other workflow-management call appeared. This is a clear improvement over Qwen's Test 5 distraction and 49-call Test 8 management loop. It is consistent with GPT-OSS, whose preserved report also observed no management-tool use.

An exact "useful call" count is not defensible retrospectively because an error can be task-relevant protection while a successful discovery call can be redundant. The report therefore exposes total calls, marked errors, repeated patterns, and scenario outcomes instead of manufacturing a scalar. At least eight Gemma calls were clearly redundant or non-advancing: six repeated Test 5 schema failures, the Test 7 identical-string edit, and Test 10's read of the already moved source. Invalid escalation attempts and unknown tools add further avoidable overhead.

## 14. Long-session/context findings

The official Web session contained one registration turn followed by the same eight Qwen/GPT work prompts. It persisted nine completed turns, 36 steps, 27 native calls, five marked tool errors, 426,271 cumulative per-request input tokens, and 14,198 output tokens. The final UI showed 7% current-context use, average TTFT about 0.9 seconds, and 138 tokens/second.

What survived:

- **PROVEN:** title `Continuity App`;
- **PROVEN:** increment by 2;
- **PROVEN:** decrement floor at zero;
- **PROVEN:** reset behavior;
- **PROVEN:** `score-value` in HTML, CSS, and JavaScript;
- **PROVEN:** byte-for-byte preservation of `assets/celebrate.js` (SHA-256 `2784263316E4C6533BEFDA130845F13D8B4DAFE0EB75F6884325751A7F9F6E41`);
- **PROVEN:** real browser loading and button behavior.

What failed:

- **FAILED:** the layout turn read `styles.css`, reasoned that the body was already centered, then stopped without applying `max-width: 420px` or a terminal visible answer.
- **FAILED:** the next zero-floor turn read `app.js` and stopped without editing. A later turn repaired the floor while implementing celebration, so the final floor passed but the requested turn did not.
- **FAILED:** the final turn changed `count === 5` to `count >= 5`. With increments of 2, the browser proved celebration at 6, not at exactly 5. This broadens the invariant instead of preserving it.
- **FAILED:** the final response claimed layout and celebration were preserved despite the missing width and altered threshold.
- **PLAUSIBLE inefficiency:** the first work turn pre-created decrement and reset before they were requested, weakening the incremental boundary even though those features were later required.

This is not evidence of context truncation: the UI showed only 7% current-context use, and Gemma recalled most identifiers and constraints. Primary attribution is **MODEL** reasoning/action selection. The DSH decision to mark the two no-effect turns `completed` is separately a **HARNESS** terminal-truth weakness.

## 15. Workspace hygiene

Every scenario passed `git diff --check`. Final effects were:

| Test | Git state | Effect classification |
|---|---|---|
| 1 | untracked `hello.txt` only | **REQUIRED path**, **FAILED exact bytes** |
| 2 | `index.html` one-line modification | **REQUIRED** |
| 3 | `app.js` only | **REQUIRED effect**; full rewrite was avoidable |
| 4 | tracked tree clean; ignored `.verify-state` | **REQUIRED runtime state** |
| 5 | clean | **FAILED** because no requested fix |
| 6 | `src/message.js` one-line modification | **REQUIRED** |
| 7 | three requested untracked app files; asset unchanged | path-clean, semantically **HARMFUL** threshold and missing layout |
| 8 | `index.html` one-line modification | **REQUIRED** |
| 9 | exactly six tracked deletions | **REQUIRED** |
| 10 | source deletion plus untracked `archive/notice.txt` | **REQUIRED move representation** |
| 11 | one-line modification under `nested path/` | **REQUIRED** |
| 12 | requested line plus preserved external line | **REQUIRED** plus externally injected fixture change |

No DSH scenario modified Agentic Router, wrote outside its disposable clone, created a duplicate application, left empty deletion placeholders, or changed the preserved comparison reports. Filesystem containment was strong. Semantic minimality and effect correctness were not.

## 16. Fully-local findings

The exercised inference path is **PROVEN local**:

- DSH provider configuration named only `ollama-local` for the selected model.
- Base URL was loopback `http://127.0.0.1:11434/v1`.
- Durable model source fields named `ollama-local/gemma4:26b-a4b-it-qat`.
- `ollama ps` showed digest prefix `2dd70431afed`, 15 GB, 100% GPU, and 32,768 loaded context.
- DSH telemetry was disabled on every launch.
- Common cloud-provider API-key variables were absent.
- No Agentic Router or cloud-provider inference was invoked.

The stronger statement "no network communication of any kind occurred" remains only **PLAUSIBLE** because there was no packet capture, deny-all-except-loopback firewall rule, or equivalent instrumentation. The Web UI itself was local, but that does not upgrade the exhaustive isolation claim.

## 17. Qwen vs GPT-OSS vs Gemma comparison

### Shared scenario outcomes

| Shared test | Qwen3-Coder 30B | GPT-OSS 20B | Gemma4 26B A4B IT QAT |
|---|---|---|---|
| 1. Minimal file | False completed; zero native calls/effect | Exact native effect after one correction | Native effect, but extra LF violates exact bytes |
| 2. Narrow edit | 3 calls; exact one-line diff | 3 calls; exact one-line diff | 5 calls/1 error; exact one-line diff |
| 3. Asset reuse | Textual pseudo-call; no effect | Correct reuse, 12 calls/4 errors, full rewrite | Correct reuse, 8 calls/1 error, full rewrite |
| 4. Recoverable verifier | Recovered, but ran three times | Failed; fabricated state and narrated unexecuted run | Correct two-run recovery |
| 5. Build diagnosis | Correct artifact, 24 calls, full rewrite | Correct artifact, 20 calls, extra `package.json` change | No effect; six repeated malformed process calls; canceled |
| 6. Stop after success | Correct, six calls, one passing test | Artifact correct; DSH validation blocked by `EPERM` | Correct one-line artifact; direct in-DSH and host validation passed; no run after pass |
| 7. Continuity | Final stale selector and unreachable celebration | Final browser state passed after transient truncation/repair | Final missing layout and broadened celebration threshold |
| 8. No recreate | Correct artifact, then 49-call non-terminal loop | Correct one-line effect and terminal completion | Correct one-line effect and terminal completion |

### Decision metrics

| Metric | Qwen3-Coder 30B | GPT-OSS 20B | Gemma4 26B A4B IT QAT |
|---|---|---|---|
| Minimal task success | **FAILED** | **PROVEN** | **FAILED** exactness; effect real |
| Native tool reliability | Pseudo-tool failures in Tests 1/3 | Native throughout; high argument-error rate | Native throughout; lower marked-error rate |
| False completions | Tests 1/3; Test 8 lacked end after effect | Test 4 completed without required execution | Test 1 exact mismatch; two Test 7 no-effect turns completed |
| Total useful calls | Not consistently classifiable; 116 shared total | Not consistently classifiable; 100 shared, 138 all 12 | Not consistently classifiable; 75 shared, 123 all 12 |
| Redundant calls | Extra verifier; 21 repeated management cycles in Test 8 | Numerous invalid/discovery calls; exact subset not tagged | At least 8 clearly redundant/non-advancing |
| Invalid tools | Textual pseudo-tools; management tools were valid but distracting | No unknown tool documented | 2 unknown names (`ls`, `Glob`) |
| Malformed arguments | Path/edit thrashing documented; no aggregate | 46 marked errors across 138 calls, not all malformed | 23 marked errors across 123; 21 model-caused invalid/target/no-op results |
| Repeated execution | Extra Test 4 run; post-effect Test 8 loop | No post-success repeat; process recovery weak | Exact Test 4 count; no post-success Test 6 repeat; malformed Test 5 loop |
| Correct stopping | Failed false-success and non-terminal cases | Every turn ended, but completion was not effect proof | Test 5 required cancel; other turns ended, including no-effect Test 7 turns |
| Recovery quality | Strong Test 4; weak transport/context recovery | Strong stale/path/gap recovery; weak Test 4/process recovery | Strong Tests 4/6/12; failed Test 5 convergence |
| Capability-gap adaptation | No controlled Tests 9/10 | Both new gap tests passed | Both passed; move was inefficient |
| Constraint adherence | Failed effects in 1/3/7 and stopping in 8 | Extra file in 5; transient loss in 7 | Exactness in 1, no effect in 5, semantic loss in 7 |
| Context continuity | Final semantic failure | Final semantic pass after repair | Final semantic failure despite low context use |
| Unnecessary file changes | No extra file in shared suite; one full rewrite | Extra `package.json`; three full rewrites | No extra file; two documented full rewrites |
| Workspace hygiene | Contained | Contained, one unrelated tracked edit | Contained; no duplicate/escape |
| Final correctness | 4/8 fully proven; Test 8 artifact-only | 5/8 fully proven; Tests 5/6 qualified | 5/8 required shared effects; Tests 1/5/7 failed acceptance |
| Elapsed time | Only selected fair times preserved | Not uniformly preserved | About 587 s including canceled Test 5; cold/warm mix |
| Tokens/sec | Not preserved comparably | Not preserved comparably | Test 7 final UI: 138 tok/s; not suite-wide |
| Local resource cost | 21 GB, 100% GPU, 32,768 context | 12 GB, 100% GPU, 32,768 context | 15 GB, 100% GPU, 32,768 context |

No single scalar ranking is justified. GPT-OSS retains the best current combination because it finished the build diagnosis and ended the continuity session with correct browser behavior. Gemma is materially cleaner in marked native-call errors and decisively better on the recoverable verifier, but correctness-first weighting makes its no-effect build loop and final continuity violations disqualifying. Qwen remains behind both on native transport and convergence.

Timing and throughput are not controlled benchmarks. Gemma's total includes a cold first load and a canceled loop; prior reports did not preserve comparable suite-wide durations or tokens/second. The resource comparison is an observed loaded footprint, not energy or latency measurement.

## 18. Model/harness/tool-surface attribution

| Significant finding | Primary attribution | Evidence |
|---|---|---|
| Test 1 trailing LF | **MODEL** | The native `write` argument itself contained `hello world\n`. |
| No pseudo-tool markup | **MODEL** | Same DSH transport/catalog; Gemma consistently emitted native calls while Qwen did not. |
| Invalid escalation/missing fields/unknown tools | **MODEL** | Schemas and deterministic rejection text were available; Gemma repeated some defects. |
| Typed stale/read-version protection | **HARNESS** | DSH independently rejected edits against unobserved/currently changed content. |
| Stale-state semantic reconciliation | **MODEL** | Gemma reread, recognized the external line, preserved it, and retried narrowly. |
| Test 5 non-convergence | **MODEL** | Six materially identical missing-field proposals followed actionable rejections. |
| Test 7 missing layout and `>= 5` rewrite | **MODEL** | Required state was still present in the session and context usage was only 7%. |
| Test 7 no-effect turns marked completed | **HARNESS** | Turn lifecycle ended successfully without a proven requested mutation. |
| `completed`/exit 0 not effect truth across models | **HARNESS** | Qwen false effects, GPT unexecuted verifier, and Gemma mismatched/no-effect turns all reached successful lifecycle states. |
| No native delete/move | **TOOL SURFACE** | Capability was absent by construction; PowerShell was the exposed fallback. |
| Correct delete/move fallback | **MODEL** | Gemma selected and executed a real alternative, then verified effects. |
| General PowerShell authority | **TOOL SURFACE** | It enables gaps but is broader than structured file/process contracts. |
| Node test-runner `spawn EPERM` | **HARNESS / TOOL SURFACE** | Reproduced with GPT and Gemma under the same Windows DSH process environment. |
| Broad catalog contribution to invalid calls | **UNKNOWN** | Plausible, but no reduced-catalog control was run. |
| Long-session failure as context loss | **FAILED attribution** | Available evidence points to model decisions, not missing context. |

Failures reproduced across all available models and therefore remain the strongest DSH candidates:

1. terminal completion is not Host-proven objective completion;
2. process results can embed non-zero exits without being marked `isError: true`;
3. the Windows process sandbox can block ordinary Node test-runner child processes;
4. the general process fallback is substantially broader than a structured action;
5. durable traces are useful evidence but do not themselves enforce artifact correctness.

## 19. Implications for Agentic Router

This baseline does not justify redesigning Agentic Router, integrating DSH, or adding a Gemma-specific protocol. Gemma's native transport fit is useful evidence for a future experiment, not a transfer of authority.

Router should continue to own:

- closed tool names and exact aliases;
- trusted-root path confinement and stale-state rules;
- approval identity and argument binding;
- constrained process policy;
- independent required-effect proof;
- changed-file review and validation tied to current hashes;
- terminal truth generated from Host facts;
- retry, recovery, cancellation, and elapsed/tool budgets.

If DSH is ever evaluated as an experimental external agent provider, all DSH messages, calls, process results, and terminal events must remain untrusted evidence. Router would need to observe the final workspace independently, bind validation to current state, and refuse completion for cases like Gemma Tests 1, 5, and 7. That is a future integration experiment, not an architectural recommendation from this run.

## 20. Follow-up experiments

Necessary now:

- Preserve this untuned report, plan, and the two hashed comparison baselines.
- Make no Router architecture or integration change from this evidence.

Useful later:

- Repeat Tests 1, 5, and 7 at least three times per model to distinguish stable behavior from single-run variance.
- Run a reduced-tool-schema control that removes overlapping legacy editors and optional escalation fields; compare marked errors without changing task prompts.
- Add an external effect oracle around DSH headless so exact bytes, expected diffs, required process exits, and browser invariants determine success independently of `turn/end`.
- Repeat the Windows process scenarios with a reviewed DSH configuration that supports Node's child-process pipes, without changing project files as a workaround.
- Add per-turn browser assertions to the long session so the first lost invariant blocks the next prompt instead of being discovered only at final validation.
- Run deny-all-except-loopback instrumentation if exhaustive offline proof becomes a release requirement.
- Measure warm and cold latency, tokens/second, energy, and memory in a dedicated benchmark; do not mix those results with this correctness baseline.

Probably unnecessary:

- a Router redesign around DSH;
- a generic shell in Router because DSH used PowerShell successfully;
- a Gemma-specific compatibility adapter before repeated baselines and reduced-schema controls;
- retroactive prompt tuning of this baseline.

Gemma verdict: **GEMMA4 26B PROMISING BUT UNRELIABLE**

DSH verdict: **CONTINUE EVALUATION**
