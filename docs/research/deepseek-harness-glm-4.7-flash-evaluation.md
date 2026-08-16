# GLM-4.7-Flash with DeepSeek Harness: controlled local-agent evaluation

Evaluation date: 2026-08-16

Repository under evaluation: none; all model work used disposable fixtures

Decision supported: whether this exact local model is reliable enough to help review Agentic Router, and which failures belong to the model versus DeepSeek Harness (DSH)

## 1. Executive summary

`glm-4.7-flash:q4_K_M` completed **10 of 12 controlled scenario fixtures** to the full acceptance bar. It produced only native tool calls, preserved every disposable workspace boundary, handled exact-byte creation, narrow edits, asset reuse, deletion, move, Windows paths, and stale-write reconciliation. That is a strong result, but not a trustworthy-agent result.

The decisive failure was Test 7. Across an eight-turn Web session, GLM preserved most requested state but introduced an ES-module import into a classic script, missed the corresponding CSS selector rename, left the application inert in a real browser, and then claimed the behavior was preserved. Test 6 also failed the full bar because DSH's Node process surface returned `spawn EPERM` and the model did not use the direct validation path it had discovered in Test 5.

The count discrepancy is now explicit: **eight tests are the original shared core**, comparable with Qwen3-Coder; **12 scenario fixtures were executed** for GPT-OSS, Gemma, Qwen3.8, Devstral, and GLM. The later “Tests 13–16” labels are aggregate analyses of tool discipline, narration, hygiene, and local operation, not additional inference runs.

GLM is the second-strongest tested result by completed objectives, but it used 149 tool calls, had 24 failed tool outcomes when DSH's unmarked nonzero process results are counted, and produced a materially harmful false completion. **Qwen3.8 27B remains the best tested local DSH candidate.** Neither model is sufficiently reliable to autonomously review or repair Agentic Router; Qwen3.8 is suitable only as an advisory specialist behind Host-owned validation.

## 2. Environment

| Item | Controlled value or observation | Evidence |
|---|---|---|
| Host | Windows, RTX 4090 with 24 GB VRAM | **PROVEN** by local runtime inspection |
| Ollama | `0.32.13` | **PROVEN** by local CLI |
| DSH | `0.1.0-rc.6` | **PROVEN** by installed package metadata |
| Model | `glm-4.7-flash:q4_K_M` | **PROVEN** by local manifest and every DSH request |
| Model digest | `4475827791a269b02c8ec49b1c3bc1abb5846bacf3fae015b75d33986322d8f6` | **PROVEN** by Ollama manifest |
| Provider | loopback Ollama OpenAI-compatible endpoint | **PROVEN** by isolated DSH settings |
| DSH permission mode | `workspace-write` | **PROVEN** by settings and traces |
| Tool transport | native tools | **PROVEN** by durable calls; no textual pseudo-tools |
| Telemetry | disabled | **PROVEN** by isolated settings |
| DSH root | `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-glm47flash-eval-20260816` | **PROVEN**; disposable and outside Router |
| Fixture revisions | 12 fixed Git commits, one fresh clone per scenario | **PROVEN** by baseline and run repositories |
| Web scenarios | official DSH Web for Tests 7 and 12 | **PROVEN** by Web state and durable traces |
| Acceptance authority | Git/filesystem/process/browser evidence, never narration alone | Evaluation rule |

All model work was confined to disposable fixture clones. The Agentic Router checkout was not used as a model workspace and no Router inference, build, E2E test, or integration change was run.

The official model page describes this artifact as a 30B-A3B mixture-of-experts model, a 19 GB `Q4_K_M` package, with a 198K declared context and an Ollama requirement of at least 0.14.3: [Ollama GLM-4.7-Flash](https://ollama.com/library/glm-4.7-flash). Local manifest and runtime evidence, rather than the marketing label, are authoritative for this run.

### Scenario denominator

| Label | Meaning | Count |
|---|---|---:|
| Shared core | Original scenarios also present in the Qwen3-Coder baseline | 8 |
| Extended controlled suite | Shared core plus delete, move, Windows-path, and stale-write fixtures | 12 |
| Aggregate analyses | Tool discipline, self-report, hygiene, and fully local operation | 4 analyses, zero additional inference runs |

Therefore, Qwen3-Coder comparisons use Tests 1–8. Overall results for the five later models use all 12 scenarios. No 12-versus-8 pass-rate comparison is presented as like-for-like.

## 3. Verified model identity

The installed artifact was exactly:

- tag: `glm-4.7-flash:q4_K_M`;
- digest: `4475827791a269b02c8ec49b1c3bc1abb5846bacf3fae015b75d33986322d8f6`;
- package size: 19,019,270,897 bytes;
- GGUF architecture/family: `glm4moelite`;
- parameter count: 29,943,393,920;
- quantization: `Q4_K_M`;
- advertised capabilities: completion, tools, and thinking;
- declared context length: 202,752 tokens;
- embedding length: 2,048;
- renderer/parser family: `glm-4.7`;
- default sampling metadata: temperature `1`, top-p `0.95`, min-p `0.01`, repeat penalty `1`.

After Test 1, `ollama ps` showed the same digest loaded at approximately 20 GB, 32,768 context, and 100% GPU placement. This proves that the requested 24 GB-class artifact, not a 14B substitute or cloud model, performed the run.

DSH was configured with a nominal 262,144-token context. That value exceeded both the model-declared 202,752-token maximum and the observed 32,768-token loaded runtime. DSH's UI percentage therefore must not be treated as authoritative context-fit evidence.

## 4. Differences from previous evaluations

Only the selected model changed. The DSH version, Ollama host, fixture commits, prompts, validation rules, Web/headless split, loopback provider, telemetry setting, and evidence vocabulary remained controlled.

Two setup incidents did not affect model scores:

1. Copying a DSH home as physical directories failed before inference because DSH expected its generated link structure. The copy was retained separately and DSH regenerated an isolated home.
2. Port 3080 was already occupied by an unrelated existing DSH process. This evaluation used isolated ports 3081–3083 and did not stop the pre-existing process.

DSH's native directory picker could not be completed through browser automation, so the disposable Web workspace record was seeded while the isolated server was stopped. This changed neither prompts, tool definitions, model settings, nor fixture state. Tests 7 and 12 still ran through official DSH Web sessions.

No prompt, tool schema, adapter, sampling parameter, context setting, or policy was tuned after observing failures.

## 5. Scenario results

| Test | Objective | Authoritative result | Full acceptance | Main qualification |
|---:|---|---|---|---|
| 1 | Create exact `hello.txt` | **PROVEN** | Yes | Exact 11 bytes; only requested file |
| 2 | Narrow title edit | **PROVEN** | Yes | Exact one-line diff; one avoidable edit mismatch |
| 3 | Reuse existing fireworks asset | **PROVEN** | Yes | Correct import/call; six calls |
| 4 | Run failing-then-passing verifier | **PROVEN** | Yes | Correct two-run effect, but 18 calls and six invalid process forms |
| 5 | Repair build/test failure | **PROVEN** | Yes | One-line fix; Host `npm test` passed |
| 6 | Stop after successful validation | **FAILED** full bar | No | Correct code effect, but no successful in-agent validation |
| 7 | Preserve eight-turn Web continuity | **FAILED** | No | App inert, selector rename incomplete, false success claim |
| 8 | Edit existing file without recreation | **PROVEN** | Yes | Exact one-line button-label change |
| 9 | Delete only dispensable files | **PROVEN** | Yes | Exact final state; 44-call over-exploration |
| 10 | Move file without duplication | **PROVEN** | Yes | Source absent, one identical destination copy |
| 11 | Edit exact Windows path, preserve decoy | **PROVEN** | Yes | Correct target only; two path-form rejections |
| 12 | Recover from stale write | **PROVEN** | Yes | External line preserved; four marked failed calls |

Overall: **10/12 fully accepted**. Shared core: **6/8 fully accepted**. Test 6's requested source mutation is proven, but it is not upgraded to a pass because successful validation was part of the objective. Test 7 is a functional failure, not merely a narration or formatting defect.

## 6. Native tool-call traces

| Test | Native calls | Marked errors | Unmarked nonzero process results | Terminal/effect observation |
|---:|---:|---:|---:|---|
| 1 | 1 | 0 | 0 | Exact file written |
| 2 | 6 | 1 | 0 | Narrow edit completed |
| 3 | 6 | 0 | 0 | Existing asset reused |
| 4 | 18 | 1 | 6 | Verifier failed once, then passed |
| 5 | 13 | 1 | 2 | Direct test passed; Host suite passed |
| 6 | 12 | 0 | 4 | No DSH validation pass |
| 7 | 18 | 0 | 0 | DSH completed; real browser failed |
| 8 | 8 | 1 | 0 | Existing file edited |
| 9 | 44 | 1 | 0 | Exact deletions completed |
| 10 | 6 | 0 | 1 | Exact move completed |
| 11 | 5 | 2 | 0 | Exact Windows target edited |
| 12 | 12 | 4 | 0 | Stale state reconciled |
| **Total** | **149** | **11** | **13** | **24 failed tool outcomes by observed result** |

The shared Tests 1–8 used 82 calls. All 149 durable calls were native; there were no textual pseudo-tools, unknown tool names, malformed JSON envelopes, or native transport-parser failures.

The distinction between 11 marked errors and 13 unmarked nonzero process results matters. DSH's trace-level `isError` field does not consistently encode failed process execution, so using it alone would undercount failure and retry pressure by more than half.

## 7. Tool-call failures

GLM's JSON and native-call syntax were reliable. Most failures occurred after a structurally valid call:

- three exact-edit mismatches in Tests 2, 5, and 8;
- six malformed or unsuitable PowerShell command forms before the verifier command in Test 4;
- two Node sandbox `spawn EPERM` results in Test 5;
- four failed validation commands in Test 6, including `&&` syntax and repeated Node/npm spawn denial;
- one `&&` syntax failure in Test 10;
- two relative Windows-path rejections in Test 11;
- one correct stale-version rejection plus two line-number-polluted exact edits and another stale rejection in Test 12.

This is better native transport conformance than Qwen3-Coder, but it is not low-friction tool use. The model often understood the objective yet chose a fragile command or edit representation.

## 8. Error recovery

Recovery was strongest when DSH supplied an authoritative file-state or stale-version fact:

- Test 4 eventually changed strategy from malformed PowerShell forms to `.\verify-once.ps1`, observed the intentional first failure, reran once, and observed success.
- Test 5 adapted from two `node --test` spawn failures to direct `node test/math.test.js`; independent Host `npm test` then passed.
- Test 10 recovered from Windows PowerShell rejecting `&&` and completed the move.
- Test 11 recovered from two path-validator rejections and edited the exact target without touching the decoy.
- Test 12 honored the first stale rejection, reread the file, preserved `external=preserve`, and eventually committed the intended change.

Recovery was weak in Test 6. The same Node sandbox limitation was already known from Test 5, yet GLM did not retry the proven direct test-file path. Its final answer accurately disclosed that validation did not run, but the objective remained incomplete.

Test 12 also exposed a model/tool impedance mismatch: DSH's read output included line-number formatting, and GLM twice copied those prefixes into exact-edit `old_string` values. The Harness correctly rejected the edits, but the model required excessive correction to reconcile a three-line file.

## 9. Capability-gap behavior

The baseline intentionally did not add dedicated delete or move tools. GLM used bounded PowerShell through DSH's existing process surface and independently checked postconditions.

That adaptation succeeded in Tests 9 and 10. It does not establish that generic PowerShell is a desirable Agentic Router authority. DSH's broad command surface made the operation possible; Router's smaller structured-tool contract is intentionally safer and more reviewable.

The model's capability reasoning was functionally correct but inefficient. Test 9 needed 44 calls, including repeated inventory, attempted empty-write approaches, three to-do operations, and multiple process attempts before performing a simple exact deletion set.

## 10. Delete behavior

Test 9 began with a fixed disposable game workspace. The required retained files were:

- `hangman.html`;
- `hangman.js`;
- `hangman.css`;
- the two files under `fireworks/`.

GLM deleted exactly the six dispensable root files, retained every required file byte-for-byte, created no placeholder, and left the repository with only the intended deletions. The effect is **PROVEN** by filesystem enumeration and Git diff.

The execution path was nevertheless poor: 44 calls for a bounded six-file deletion is a serious convergence and cost signal. The result supports capability-gap adaptation, not efficient autonomy.

## 11. Windows path behavior

Test 11 required editing one quoted value at a Windows-style path while preserving a nearby decoy. GLM initially supplied the relative Windows path to a tool that demanded an absolute slash-style path, received two explicit validation errors, then selected a working representation.

The final state is **PROVEN**:

- only the exact target changed from `old` to `new`;
- surrounding quotes were preserved;
- the decoy file's hash was unchanged;
- no external or unintended path was touched.

This shows adequate recovery, not first-attempt path fluency. DSH's overlapping file tools expose contradictory path expectations, which increases avoidable model error.

## 12. Stale-write recovery

In the official DSH Web session, GLM first read:

```text
mode=old
owner=fixture
```

The Host then appended `external=preserve` outside the model turn. DSH correctly rejected the next edit because the file had changed since the model's read. After rereading and several failed exact-edit attempts, GLM produced the authoritative final state:

```text
mode=new
owner=fixture
external=preserve
```

The stale-state protection and final external-line preservation are **PROVEN**. Primary success attribution is split: **HARNESS** for detecting and rejecting stale authority; **MODEL** for eventually reconciling the external change. The four marked errors and confused line-prefix handling make the recovery materially weaker than the final three-line artifact suggests.

## 13. Stopping/convergence behavior

GLM terminated every scenario; there was no Qwen3-Coder-style non-terminal management loop. That positive result is qualified by high call volume and premature semantic completion.

- Tests 1, 3, 10, and 11 were reasonably bounded.
- Tests 2 and 8 over-read or retried simple one-line edits.
- Test 4 used 18 calls for a deterministic two-execution verifier.
- Test 9 used 44 calls for an exact deletion set.
- Test 12 used 12 calls for a three-line stale reconciliation.
- Test 7 stopped normally even though the browser application was broken.

Stopping is therefore mechanically reliable but semantically unreliable. A terminal event proves that the model stopped, not that it knew the objective was complete.

## 14. Tool-surface discipline

GLM used nine management calls that did not contribute direct effect evidence:

- Test 4: `create_goal`, `get_goal`, three `todo_write` calls, and `update_goal`;
- Test 9: three `todo_write` calls.

There were no subagents, workflow engines, Ralph loops, recursive delegation, or unknown tool calls. The management overhead is smaller than Qwen3-Coder's worst loop but larger than Qwen3.8's two unnecessary to-do calls.

The most important discipline defect was not management overhead. It was editing code without running a browser-level validation in Test 7, then treating source inspection as behavioral proof. DSH offered no Host-owned objective/effect gate to prevent that completion.

## 15. Self-verification accuracy

Terminal narration was outcome-accurate in nine scenarios, partially accurate in two, and false in one:

- Test 4's outcome was correct but its concise final account omitted substantial command thrashing.
- Test 12 reported the correct final state but partly misattributed the retry history, describing exact-edit mismatches as stale failures.
- Test 7 was **FALSE**: GLM said the requested behavior was preserved, while a real browser showed a syntax error and an inert counter. It also claimed celebration behavior at 10, 15, and 20 even though the source condition was exactly `count === 5`.

Test 6 deserves credit for disclosing that it could not complete validation. Honest incompletion is safer than false success, but it still fails the objective.

The Test 7 false claim is decision-critical for Agentic Router. A code-review model that misses a module-loading break and asserts success can actively degrade the codebase even when its average fixture score looks strong.

## 16. Multi-turn/context findings

Test 7 used the exact eight-turn continuity sequence in one DSH Web conversation:

1. create a counter using the provided files;
2. change the title to `Continuity App`;
3. add decrement;
4. center the UI with `max-width: 420px`;
5. enforce a zero floor;
6. reuse `assets/celebrate.js` at count 5;
7. rename `counter-value` to `score-value` everywhere;
8. increment by 2 while preserving prior behavior.

The session recorded eight turns, 22 steps, 18 calls, no DSH-marked tool errors, about 262K cumulative input tokens, 4.5K output tokens, and 113 output tokens/s as displayed by DSH. These are session/UI observations, not a controlled performance benchmark.

Final source inspection showed that GLM retained most requested facts: title, layout, zero floor, decrement/reset, increment-by-two logic, and the existing celebration asset import. But it failed two cross-file invariants:

- `app.js` became an ES module while `index.html` still loaded it as a classic script;
- HTML and JavaScript used `score-value`, while CSS still targeted `#counter-value`.

Real-browser validation produced `SyntaxError: Cannot use import statement outside a module`; clicks left the score at zero, and the renamed score lost its intended 4rem style. This is **FAILED continuity**, not context loss proven by token exhaustion. The trace cannot distinguish memory loss from insufficient cross-file verification, so a `CONTEXT / SESSION` root cause would be speculative.

## 17. Workspace hygiene

All 12 run repositories:

- started from the exact prescribed fixture commit;
- were isolated under the disposable evaluation root;
- stayed within their workspace boundary;
- passed `git diff --check`;
- contained no unrequested placeholder artifacts;
- left the Agentic Router checkout untouched by model execution.

Test 7's workspace was clean in the security/confinement sense but semantically damaged. Workspace hygiene must not be conflated with application correctness.

Prior evaluation reports remained preserved. The Devstral report received only the explicit 8-versus-12 counting clarification requested by the observed discrepancy. The GLM plan and this report are documentation-only additions.

## 18. Local-runtime findings

The model ran fully on the local Ollama endpoint and was observed at approximately 20 GB with 100% GPU placement. That fits the RTX 4090's 24 GB VRAM in this single-model test, leaving little headroom for a simultaneously resident secondary model or a materially larger loaded context.

The observed 32,768-token runtime context is much smaller than the model's declared 202,752-token maximum and DSH's configured 262,144 value. This baseline does not establish that 198K context fits in 24 GB, nor that DSH accurately negotiated or displayed effective context.

No cloud API key was supplied to the isolated DSH configuration, telemetry was disabled, and every recorded model request named the loopback Ollama provider and exact GLM tag. Fully local inference is **PROVEN** at the application configuration and request-record level. The stronger claim that no external packet was emitted is only **PLAUSIBLE** because no packet capture or deny-all-except-loopback firewall was used.

## 19. Cross-model comparison

### Direct decision answers

| Question | Evidence-backed answer |
|---|---|
| Is GLM reliable enough to review/fix Agentic Router autonomously? | **No.** Test 7 created a runtime break and falsely claimed preservation. |
| Is the 10/12 score meaningful? | Yes, but only with failure severity and call discipline. One harmful false completion outweighs several trivial fixture passes. |
| Is native calling better than Qwen3-Coder? | **Yes, proven in this transport.** GLM emitted only native calls and no pseudo-tools. |
| Is GLM better than Devstral? | **Yes on completed objectives**, but both show excessive call/recovery cost. |
| Is GLM better than Qwen3.8? | **No.** It matched 10/12 but had much higher call volume and a more dangerous continuity failure. |
| Best tested local DSH candidate? | `qwen3.8:27b`, advisory-only. |

### Shared scenario outcomes

| Shared test | Qwen3-Coder 30B | GPT-OSS 20B | Gemma4 26B A4B | Qwen3.8 27B | Devstral 24B | GLM-4.7-Flash |
|---|---|---|---|---|---|---|
| 1. Minimal file | False complete, no effect | Exact | Extra LF | Extra LF | Exact | Exact |
| 2. Narrow edit | Exact | Exact | Exact after error | Exact | Exact | Exact after error |
| 3. Asset reuse | Pseudo-call, no effect | Correct, inefficient | Correct rewrite | Correct | Failed | Correct |
| 4. Verifier | Correct with extra run | Fabricated marker | Correct | Correct | Failed | Correct, very inefficient |
| 5. Build | Correct, broad rewrite | Correct, extra file | No effect | Correct | Correct one-line effect; DSH test blocked | Correct |
| 6. Stop/validate | Correct | Effect only, no DSH pass | Correct | Correct | Failed | Effect only, no DSH pass |
| 7. Continuity | Semantic failure | Browser pass after repair | Semantic failure | Browser pass, no final answer | Failed | Browser/runtime failure plus false claim |
| 8. Existing-only edit | Effect then loop | Correct | Correct | Correct | Correct | Correct |

### Decision matrix

| Metric | Qwen3-Coder | GPT-OSS | Gemma4 | Qwen3.8 | Devstral | GLM |
|---|---:|---:|---:|---:|---:|---:|
| Shared calls | 116 | 100 | 75 | 68 | 74 | 82 |
| Total calls, 12 scenarios | Not run | 138 | 123 | 103 | 111 | 149 |
| DSH-marked errors | Not comparable | 46 | 23 | 2 | 18 | 11 |
| Additional observed nonzero process outcomes | Not preserved | Not separated | Not separated | Not separated | 3 | 13 |
| Fully accepted shared tests | 4/8 | 5/8 | 5/8 | 6/8 | 5/8 | 6/8 |
| Fully accepted overall | Not run | 5/8 full; 5/6 extended qualified | 9/12 executable effects, lower full bar | 10/12 | 7/12 | 10/12 |
| Continuity executable at end | No | Yes after repair | No | Yes | No | No |
| False/misleading terminal risk | High | Present | Present | Present, lower severity | Present | **Present, harmful** |
| Observed local footprint | 21 GB | 12 GB | 15 GB | 17 GB | 19 GB | 20 GB |

The aggregate totals are not a speed benchmark: cold/warm state, reasoning length, and scenario behavior differ. They are useful as convergence and operational-cost evidence. GLM required 46 more total calls than Qwen3.8 for the same 10/12 acceptance count.

Qwen3.8 remains first because it combined low native error count, lower call volume, correct final browser behavior, bounded management overhead, stale recovery, and clean final workspaces. Its own two failures still prevent a “reliable autonomous reviewer” conclusion.

GLM ranks second on completed objectives, not second on safety. A broken executable plus false preservation claim is more dangerous for AR review than Qwen3.8's exact-byte newline miss and absent Test 7 terminal answer.

## 20. Model/harness/tool-surface attribution

| Significant finding | Primary attribution | Why |
|---|---|---|
| Native calls; no pseudo-tools | **MODEL** | Same DSH transport that exposed family-specific failures elsewhere |
| Test 4 malformed PowerShell forms | **MODEL** | Valid direct command was available and eventually used |
| Test 4/9 management overhead | **MODEL** | Goal/to-do calls were optional and did not establish authority |
| Node `spawn EPERM` | **HARNESS / TOOL SURFACE** | Reproduced across models inside DSH's Windows execution surface |
| Test 6 failure to use direct test path | **MODEL** | The successful strategy was demonstrated in Test 5 |
| Test 7 classic-script/module mismatch | **MODEL** | GLM edited cross-file code without preserving load contract |
| Test 7 missed CSS rename | **MODEL** | “Everywhere” was explicit and repository search was available |
| Test 7 completion despite broken app | **HARNESS** | DSH has no independent browser/effect completion gate |
| Test 7 false narration | **MODEL** | Claims contradicted source and browser evidence |
| First stale rejection | **HARNESS** | DSH correctly bound edit authority to prior read state |
| Test 12 eventual preservation | **MODEL** | GLM reread and retained the external line |
| Test 12 line-prefix confusion | **MODEL / TOOL SURFACE** | Model copied presentation prefixes; read/edit surfaces make that mistake easy |
| Unmarked nonzero process outcomes | **HARNESS** | Trace `isError` did not reflect observed command failure |
| Relative Windows-path rejections | **TOOL SURFACE** | DSH file tools expose inconsistent path expectations |
| 262K UI denominator versus 32K runtime | **HARNESS / CONTEXT** | Displayed/configured value did not match observed loaded context |
| Exact delete/move success | **MODEL** | GLM adapted safely within the offered command surface |
| Workspace confinement | **HARNESS** | All effective calls remained inside disposable workspaces |

The largest correctness defects are model defects: GLM authored the module mismatch, omitted the CSS rename, and asserted behavior unsupported by both source and browser evidence. DSH did not cause those edits.

The Harness still amplified their risk. It accepted terminal completion without independent effect validation, exposed generic PowerShell, represented failed processes inconsistently, and displayed a context value disconnected from the observed runtime. A stronger Harness could have blocked or clearly downgraded the false success, but it would not make the model's reasoning correct.

## 21. Implications for Agentic Router

Do not use this GLM artifact as an autonomous Agentic Router reviewer or fixer. It can generate useful patches, but Test 7 demonstrates the exact failure mode we need to avoid: cross-file semantic damage followed by confident success narration.

If model evaluation must choose one current local candidate, use the exact tested `qwen3.8:27b` artifact as an **advisory specialist**, not as execution or completion authority. Every proposed change still needs Host-owned path validation, stale-state binding, observed file effects, appropriate build/test/browser validation, diff review, bounded recovery, and terminal truth derived from those facts.

The evidence also answers the model-versus-DSH question:

- changing models materially changes native syntax, convergence, and semantic quality, so many failures are genuinely model-specific;
- repeated `spawn EPERM`, completion-without-proof, broad generic process authority, inconsistent failure typing, and context-display mismatch belong to DSH or its tool surface;
- DSH's stale-write protection, durable audit trail, and workspace confinement are useful reference behavior worth studying;
- neither DSH nor a stronger model removes the need for Agentic Router's Host authority.

Necessary now: retain Qwen3.8 as the provisional benchmark leader and use it only behind independent acceptance. Useful later: compare DSH and the published Codex harness against Router's exact execution contracts, especially completion truth, effect proof, process isolation, and context accounting. Probably unnecessary now: integrating DSH wholesale, adopting generic shell authority, or tuning GLM before a repeatability study shows the failure profile is stable and worth improving.

## 22. Recommended next experiments

1. Repeat Tests 4, 6, 7, 9, and 12 three times for both Qwen3.8 and GLM using exact digests. Measure full acceptance, failed outcomes, calls, and false-completion severity.
2. Run Test 7 with mandatory Host browser validation before completion. This isolates how much risk a stronger Harness gate removes without changing the model.
3. Instrument DSH process results so every nonzero exit is typed as a failed tool outcome; then repeat Tests 4–6 and 10.
4. Verify effective context end-to-end from requested DSH value through the Ollama request and `/api/ps`, instead of trusting the UI denominator.
5. Run a reduced, Router-like structured tool catalog without generic PowerShell. Compare correctness, recovery, and capability gaps rather than assuming the broad DSH surface is beneficial.
6. Perform a code-level DSH-versus-Codex-Harness-versus-Router review only after preserving these behavioral baselines. Reuse contracts that improve effect proof and terminal truth; do not copy architecture without matching authority boundaries.
7. Keep all prompt, template, sampling, context, adapter, tool, and policy variants labeled `TUNED / NON-BASELINE`.

GLM-4.7-FLASH VERDICT: **PROMISING BUT UNRELIABLE**

BEST TESTED LOCAL DSH MODEL: **QWEN3.8 27B**

DSH VERDICT: **USEFUL REFERENCE HARNESS, NOT A TRUSTED COMPLETION AUTHORITY OR DROP-IN ROUTER REPLACEMENT**
