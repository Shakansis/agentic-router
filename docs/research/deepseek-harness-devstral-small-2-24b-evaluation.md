# DeepSeek Harness evaluation: Devstral Small 2 24B

Date: 2026-08-16
Model: `devstral-small-2:latest`
DeepSeek Harness: `0.1.0-rc.6`
Evaluation type: untuned controlled local baseline

Evaluation plan: [`docs/PLAN-v5-deepseek-harness-devstral-small-2-24b-evaluation.md`](../PLAN-v5-deepseek-harness-devstral-small-2-24b-evaluation.md)

Evidence labels in this report are limited to **PROVEN**, **PLAUSIBLE**, **SPECULATIVE**, and **FAILED**. Model narration, DSH `turn/end: completed`, and headless exit code zero are not authoritative evidence.

## 1. Executive summary

Devstral Small 2 24B is **NO** as a reliable DSH agent model in this baseline. Seven of twelve scenarios were fully accepted. Five failed required effects or executable behavior: the asset integration imported a nonexistent named export, the recoverable verifier emitted textual pseudo-tool syntax and performed no action, the eight-turn app became inert in the browser, the move did nothing, and the Windows-path edit removed required quotes and produced invalid JavaScript.

The transport result is mixed rather than uniformly bad. Devstral made 74 durable native calls across the eight shared scenarios and 111 across all twelve. It completed an exact 11-byte minimal write, narrow edits, a one-line build fix, stop-after-success recovery, real deletion, and stale-write reconciliation. However, one scenario regressed to pseudo-tool text, 18 tool results were marked as errors, and three additional nonzero PowerShell results were persisted with `isError: false`. DSH marked every scenario turn `completed`, including all five failed outcomes.

Devstral is materially worse than Qwen3.8 27B for this use. Qwen3.8 recorded 103 calls and two marked errors, produced eleven of twelve requested final effects, and preserved the executable browser state. Devstral recorded 111 calls and 18 marked errors, produced only seven fully accepted outcomes, failed both controlled move and Windows-path scenarios, and made the continued app nonfunctional while claiming all behavior was preserved.

The result does not condemn DSH as a whole. Its typed stale-write protection worked exactly as intended, durable traces were useful, and native filesystem/process calls executed real effects. The model produced most syntax, path, reasoning, and semantic failures. The recurring Harness-level defect remains decisive: lifecycle completion is not Host-proven objective completion, and nonzero process exits can appear as ordinary non-error tool results.

For Agentic Router, this model should not be used to autonomously review or correct the codebase. `qwen3.8:27b` remains the best tested local DSH specialist candidate, but it is still advisory-only evidence rather than a reliable execution authority. The next controlled model baseline should be GLM-4.7-Flash; no DSH or Router redesign is justified before that comparison.

## 2. Environment

| Item | Observed value |
|---|---|
| OS | Windows 10 build `19045` |
| GPU | NVIDIA GeForce RTX 4090, 24,564 MiB, driver `610.88` |
| DSH | `0.1.0-rc.6` |
| Ollama | `0.32.13` |
| Ollama endpoint | `http://127.0.0.1:11434`; DSH OpenAI-compatible endpoint `http://127.0.0.1:11434/v1` |
| DSH provider / API | `ollama-local` / `openai-completions` |
| DSH tool / permission mode | native / `workspace-write` |
| DSH surfaces | headless for isolated scenarios; official Web UI for Tests 7 and 12 |
| DSH context configuration | 262,144 input window; 32,768 maximum output |
| Telemetry | `DSH_TELEMETRY_DISABLED=1` on every launch |
| Disposable root | `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-devstral24-eval-20260816` |

Each scenario used a fresh disposable Git clone. The Agentic Router checkout was used only for the versioned plan and this report. No Router inference, application build, E2E suite, source change, integration, architecture work, or GLM inference was performed.

Fixture commits were identical to the GPT-OSS, Gemma, and Qwen3.8 evaluations:

Counting note: **8 shared tests** means the original scenario set that is also comparable with Qwen3-Coder. GPT-OSS added four controlled fixtures (delete, generic move, Windows path, and stale write), so GPT-OSS, Gemma, Qwen3.8, and Devstral each executed **12 scenario fixtures**. Tests 13-16 are aggregate analyses derived from those runs, not four additional model executions.

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

The tested model was verified from local Ollama metadata rather than inferred from its name.

| Field | Observed value |
|---|---|
| Exact tag | `devstral-small-2:latest` |
| Full digest | `24277f07f62db8f9cb68e9dfc679ea1818a7fbac47a50eff0a701d3f645b63c8` |
| Stored size | 15,177,374,099 bytes |
| Modified | `2026-07-30T21:10:43.5296743-03:00` |
| Format / family | GGUF / `mistral3` |
| Exact parameter count | 24,011,361,280 |
| Quantization | `Q4_K_M` |
| Capabilities | completion, vision, tools |
| Declared context | 393,216 |
| Embedding length | 5,120 |
| Renderer / parser metadata | not reported |
| Model temperature | `0.15` |
| Observed loaded state | digest prefix `24277f07f62d`, 19 GB, 32,768 context, 100% GPU |

The generated Modelfile referenced one local blob under `E:\LLM\models\blobs\sha256-...` and identified itself in the template as `Devstral-Small-2-24B-Instruct-2512`. The exact parameter count proves that this is the requested 24B artifact, not the 14B model. The tag does not expose enough metadata to infer its full external creation history.

## 4. Differences from previous evaluations

The intended experimental variable was the selected model. DSH version, Ollama version, machine, provider endpoint, native tool catalog, permission mode, telemetry setting, fixture commits, prompts, headless/Web split, Git inspection, trace decoding, and independent validation remained fixed.

Unavoidable artifact differences were recorded rather than normalized:

- Devstral declares 393,216 context, while DSH was configured for 262,144 and Ollama actually loaded 32,768.
- Its observed footprint was 19 GB, larger than Qwen3.8 (17 GB), Gemma (15 GB), and GPT-OSS (12 GB), but smaller than Qwen3-Coder (21 GB).
- Its local Modelfile has a Mistral-specific chat/tool template and no renderer/parser metadata fields comparable to the local Qwen3.8 artifact.
- Tests 7 and 12 used DSH Web to preserve state, as in the previous stateful evaluations.

No prompt, tool schema, system instruction, adapter, model template, execution policy, context setting, or temperature was tuned after a failure.

The preserved reports were unchanged during this run:

| Baseline | SHA-256 |
|---|---|
| Qwen3-Coder 30B | `1B5A34754C391FE0903C490796E99BC0041FD3F08966EF3EE05F40B06682BFB2` |
| GPT-OSS 20B | `3100CC4597E77E4B1280775B120DBF5EB1D9D2AC803878C454862A17DAE0C054` |
| Gemma4 26B A4B IT QAT | `E5A3D07216427587ED75FCB1F87684B488AC9C73A9ADEBAC1CA71B161C974869` |
| Qwen3.8 27B | `E14E8B5A4770095403E6BF02684252B96C00B5302C7B2D3BED779E4875A0CD0E` |

## 5. Scenario results

The calls/error column counts durable `tool/call` events and results explicitly marked `isError: true`. Three nonzero PowerShell results that DSH marked as non-errors are discussed separately.

| Test | Result | Calls / marked errors | Independently observed outcome |
|---|---:|---:|---|
| 1. Minimal file | **PROVEN** | 1 / 0 | One native `write` created only `hello.txt`; exact hex `68656C6C6F20776F726C64`, 11 bytes, no newline. |
| 2. Narrow edit | **PROVEN** | 5 / 0 | One exact title line changed and nothing else. A package-file glob was unnecessary. |
| 3. Asset reuse | **FAILED** | 20 / 7 | Only `app.js` changed and no duplicate engine was created, but it imported `defaultFireworkConfig` from a module that does not export it. Independent Node import failed. DSH/model narration claimed the implementation was ready. |
| 4. Recoverable verifier | **FAILED** | 0 / 0 | The final text contained `glob{"pattern": "*"}` as pseudo-tool markup. No native call ran, `.verify-state` did not exist, and the repo stayed clean. DSH ended `completed`. |
| 5. Build/test diagnosis | **PROVEN** artifact | 8 / 0 | One-line subtraction-to-addition diff; independent host test passed. DSH's `npm test` hit `spawn EPERM`, returned exit 1 with `isError: false`, and the model did not obtain an in-DSH pass. |
| 6. Stop after success | **PROVEN** | 8 / 0 | One-line `Hello` to `Hi` diff. The first PowerShell command used unsupported `&&` and did not invoke Node; one adapted command then passed. No process ran after the pass; host validation passed. |
| 7. Long continuity | **FAILED** executable behavior | 17 / 1 | The model preserved many textual requirements but added an ES-module `import` while leaving a classic `<script src="app.js">`. In a real browser, clicking Increment left the value at `0`; all JS behavior was inert. The asset stayed byte-identical and narration falsely claimed all behavior worked. |
| 8. No recreate | **PROVEN**, inefficient | 15 / 7 | Final result was the exact one-line `Save` to `Save note` edit with the original three-file tree. Seven path/editor failures preceded the correct edit. |
| 9. Delete gap | **PROVEN**, adapted | 25 / 1 | Six separate PowerShell removals deleted exactly the six unnecessary files. Exactly five required files remained; no placeholder or duplicate. The route was read-heavy. |
| 10. Generic move gap | **FAILED** | 3 / 0 | Three `glob` calls failed to identify the existing source. No PowerShell fallback ran; source remained, target was absent, and the source SHA-256 was unchanged. DSH ended `completed`. |
| 11. Windows paths | **FAILED** | 4 / 1 | The correct target was read and the decoy stayed byte-identical, but the edit produced `export const mode = new;`, removing the required quotes. Dynamic import failed with `SyntaxError: Unexpected token ';'`. |
| 12. Stale write | **PROVEN** | 5 / 1 | DSH rejected the first edit as stale. Devstral reread, preserved `external=preserve`, changed only `mode=old` to `mode=new`, and verified all three lines. |
| 13. Tool discipline | **FAILED** reliability bar | 111 / 18 aggregate | No management tools were used, but Test 4 used pseudo-tool text, Tests 3/8 thrashed paths/editors, and three artifact mutations were harmful. |
| 14. Self-verification | **FAILED** | n/a | Six final narrations were accurate, one partial, one misleading, and four false. The false cases included invalid code and a no-effect pseudo-call. |
| 15. Workspace hygiene | **PROVEN** containment; **FAILED** semantic safety | n/a | All Git diffs passed `diff --check`; no escape or duplicate project occurred. Tests 3, 7, and 11 left harmful code, and Test 10 left the required move undone. |
| 16. Fully local | **PROVEN** provider path; **PLAUSIBLE** exhaustive isolation | n/a | All 12 durable contexts named loopback `ollama-local/devstral-small-2:latest`; telemetry was disabled. No packet-level isolation was used. |

Seven scenarios were fully accepted. This is one untuned baseline trial per fixture, not a statistical success-rate estimate.

## 6. Native tool-call traces

### Exact minimal write

```text
write({ file_path: "hello.txt", content: "hello world" })
-> success
-> host bytes: 68 65 6c 6c 6f 20 77 6f 72 6c 64
```

This is clean native conformance and exact model behavior.

### Asset reuse failure

```text
glob -> read app.js/config/engine/index
-> invalid str_replace_editor path attempts
-> denied C:\app.js write attempts
-> read workspace app.js
-> write app.js with a nonexistent named export
-> read source files as text
-> completed
```

The model had already read that `firework_engine.js` exports only `launchFireworks` and internally owns the default config. The wrong import was therefore a reasoning failure, not missing context.

### Recoverable verifier transport failure

```text
assistant text:
I'll start by exploring ... glob{"pattern": "*"}
-> zero tool/call events
-> turn/end: completed
-> no .verify-state
```

This reproduces the earlier native-versus-text protocol boundary: model output was not a native call, and DSH still accepted it as a successful lifecycle end.

### Stop-after-success recovery

```text
edit greeting() Hello -> Hi
-> pwsh: cd test && node message.test.js
-> Windows PowerShell parser exit 1; Node not invoked; isError false
-> pwsh: Set-Location test; node message.test.js
-> 1 pass, exit 0
-> read changed source
-> completed
```

There was one actual fixture execution and no process after it passed.

### Stale reconciliation

```text
read config.txt
-> external fixture appends external=preserve
-> edit mode=old -> mode=new
-> FS_STALE_VERSION
-> read current three lines
-> edit requested line
-> read and verify all three lines
```

This is the strongest combined Harness/model result in the run.

## 7. Tool-call failures

Devstral issued 111 durable native calls. DSH marked 18 results as errors:

| Test | Marked errors | Main cause |
|---|---:|---|
| 3 | 7 | relative/absolute editor mismatch, root targeting, denied write, same-mode escalation, stale write |
| 7 | 1 | unnecessary read of nonexistent `package.json` |
| 8 | 7 | wrong absolute root, invalid editor path, repeated unmatched exact strings |
| 9 | 1 | read offset past the end of a five-line file |
| 11 | 1 | first edit searched for an unquoted value that was not present |
| 12 | 1 | intentional stale-version rejection |

Seventeen of the eighteen marked errors were avoidable model proposals or non-advancing exploration. The stale rejection was intentional evidence of a Harness guard.

Three additional `pwsh` results contained `[exit code: 1]` but had `isError: false`:

- Test 3: invalid PowerShell expression using `Test-Path ... -and`;
- Test 5: Node test-runner `spawn EPERM`;
- Test 6: unsupported `&&` under Windows PowerShell 5.1.

This is a Harness/tool-result semantic issue independent of model quality. A model may parse embedded text and recover, as Devstral did in Test 6, but the result contract itself did not type nonzero process exit as an error.

## 8. Error recovery

**PROVEN strengths:**

- Test 6 interpreted the parser output and selected a compatible command.
- Test 8 eventually abandoned invalid absolute-path guesses and made the narrow relative edit.
- Test 12 responded correctly to `FS_STALE_VERSION` rather than blind-retrying.

**FAILED weaknesses:**

- Test 3 recovered from seven tool rejections but converged on semantically invalid code.
- Test 4 never entered the native tool loop.
- Test 10 did not progress from unsuccessful globbing to the available PowerShell alternative.
- Test 11 recovered from an unmatched edit by creating a different invalid edit.

Recovery count alone is not a quality metric. Test 3 demonstrates that successful tool execution after several corrections can still end in a broken artifact.

## 9. Capability-gap behavior

The two controlled gaps split evenly:

- **Test 9: ADAPT, PROVEN.** Devstral used six explicit `Remove-Item` commands and verified the remaining tree.
- **Test 10: FAILED.** Devstral used only `glob`, did not discover the existing file, did not use `pwsh`, and did not move anything.

The failure is primarily **MODEL** behavior because the same exposed PowerShell capability had already been used in other scenarios. The absence of specialized delete/move tools is **TOOL SURFACE** behavior. DSH lifecycle completion after the missing move is **HARNESS** behavior.

## 10. Delete behavior

Devstral inspected the HTML/JavaScript dependency chain, identified exactly six obsolete top-level files, and issued six separate relative PowerShell removals. Independent state showed:

```text
preserved:
  hangman.html
  hangman.css
  hangman.js
  fireworks/firework_engine.js
  fireworks/firework_config.js

deleted:
  index.html
  styles.css
  game.js
  hangman-words.js
  firework_engine.js
  firework_config.js
```

No file was emptied as a fake delete. The effect was exact. The route used 25 calls, including 17 reads and two globs, and was materially less efficient than needed.

DSH executed these destructive workspace operations through generic PowerShell without a Router-style immutable approval or per-file postcondition contract. This proves capability, not suitability for Agentic Router's security boundary.

## 11. Windows path behavior

Devstral correctly preferred the relative tool path `nested path\source file.js` even though the prompt supplied an absolute Windows path. It read the intended file and never touched `relative\other.js`.

It then made two edit attempts:

```text
1. search: export const mode = old;
   -> rejected; source actually contained quotes

2. search: export const mode = "old";
   replace: export const mode = new;
   -> accepted, but invalid JavaScript
```

The path transport itself was **PROVEN**. Semantic preservation and final validity were **FAILED**. The call argument contains the missing quotes, so this is model behavior. DSH's completed terminal state without syntax/effect validation is a separate Harness weakness.

## 12. Stale-write recovery

The stale-write path passed exactly:

1. Devstral read the two-line original.
2. The external fixture appended `external=preserve`.
3. Devstral's first edit used its prior observation and DSH rejected it with `FS_STALE_VERSION`.
4. Devstral reread all three current lines.
5. It edited only `mode=old` to `mode=new`.
6. It reread and accurately reported the final contents.

Final bytes were:

```text
mode=new
owner=fixture
external=preserve
```

Attribution: freshness detection and rejection are **HARNESS** strengths. Understanding and preserving the external line are **MODEL** strengths.

## 13. Stopping/convergence behavior

Every recorded turn ended `turn/end: completed`; no session required cancellation and no management loop occurred. That lifecycle cleanliness is qualified by five incorrect final outcomes.

Trace-derived model-turn time summed to approximately 318.8 seconds across the selected scenario sessions, excluding startup overhead and human pauses. This cold/warm mix is not a controlled performance benchmark.

Test 6 process classification:

| Invocation | Classification | Outcome |
|---|---|---|
| `cd test && node message.test.js` | **JUSTIFIED** initial attempt | PowerShell parser rejected `&&`; test did not run |
| `Set-Location test; node message.test.js` | **JUSTIFIED** recovery | one pass, exit 0 |
| after the pass | n/a | no process invocation |

Devstral stopped more cleanly than Qwen3-Coder's management loop, but `completed` frequently meant only that the model stopped, not that the objective was true.

## 14. Tool-surface discipline

Aggregate native calls:

| Tool | Calls |
|---|---:|
| `read` | 52 |
| `edit` | 18 |
| `pwsh` | 15 |
| `glob` | 14 |
| `write` | 8 |
| `str_replace_editor` | 4 |
| **Total** | **111** |

No `todo`, goal, job, agent, workflow, or plan tool was used. That is a positive difference from the Qwen3-Coder management loop and Qwen3.8's two unnecessary to-do calls.

Scenario-specific analytical classification:

| Test | Required | Useful | Optional | Unnecessary | Harmful |
|---|---:|---:|---:|---:|---:|
| 1 | 1 | 0 | 0 | 0 | 0 |
| 2 | 1 | 3 | 0 | 1 | 0 |
| 3 | 0 | 7 | 4 | 8 | 1 |
| 4 | 0 | 0 | 0 | 0 | 0 |
| 5 | 1 | 6 | 1 | 0 | 0 |
| 6 | 2 | 6 | 0 | 0 | 0 |
| 7 | 9 | 5 | 1 | 1 | 1 |
| 8 | 1 | 4 | 3 | 7 | 0 |
| 9 | 6 | 12 | 6 | 1 | 0 |
| 10 | 0 | 2 | 0 | 1 | 0 |
| 11 | 0 | 2 | 0 | 1 | 1 |
| 12 | 2 | 3 | 0 | 0 | 0 |
| **Total** | **23** | **50** | **15** | **20** | **3** |

The three harmful calls are the final invalid Test 3 write, the Test 7 edit that broke browser module loading, and the Test 11 invalid JavaScript edit. Test 4's pseudo-call is excluded because no durable `tool/call` event existed. This classification is judgment-based and scenario-specific; the raw event counts remain the reproducible evidence.

## 15. Self-verification accuracy

| Test | Classification | Evidence |
|---|---|---|
| 1 | **ACCURATE** | Exact requested file existed. |
| 2 | **ACCURATE** | Exact one-line title diff. |
| 3 | **FALSE** | Claimed ready-to-use integration; import fails. |
| 4 | **FALSE** | Presented pseudo-tool text as the start of work; no call/effect occurred and DSH completed. |
| 5 | **ACCURATE** | Correctly described the one-line bug and fix; did not claim the blocked DSH test passed. |
| 6 | **PARTIALLY ACCURATE** | One actual test run passed and none followed, but it omitted the failed process proposal. |
| 7 | **FALSE** | Claimed all behavior was preserved; browser app was inert. |
| 8 | **ACCURATE** | Final one-line edit and preservation claim matched state. |
| 9 | **ACCURATE** | Exact deleted/preserved lists matched the filesystem. |
| 10 | **MISLEADING** | Reported that the existing source could not be opened after only unsuccessful glob calls. |
| 11 | **FALSE** | Claimed the requested mode change was complete; it removed string quotes and left invalid code. |
| 12 | **ACCURATE** | Final three lines and preservation claim were exact. |

Totals: six **ACCURATE**, one **PARTIALLY ACCURATE**, one **MISLEADING**, and four **FALSE**. The false claims cluster around exactly the semantic checks a code-review specialist must get right.

## 16. Multi-turn/context findings

The official Web session contained the exact eight sequential requests. It recorded eight completed turns, 17 native calls, one marked error, approximately 239,000 cumulative input tokens, 3,300 output tokens, 112 tokens/second in the final UI summary, and 5% displayed context use.

Devstral successfully retained several prior decisions:

- title `Continuity App`;
- centered card with `max-width: 420px`;
- increment, decrement, and reset source logic;
- zero floor;
- `score-value` rename in HTML and JavaScript;
- unchanged `assets/celebrate.js` hash.

The decisive failure occurred on turn 6. Devstral read the existing asset and added:

```javascript
import { celebrate } from './assets/celebrate.js';
```

but did not change:

```html
<script src="app.js"></script>
```

to a module script. It never executed or browser-tested the result. Independent browser validation loaded the page but clicking Increment left `#score-value` at `0` and never set `data-celebrated`.

This is not evidence of context loss. Relevant files and prior decisions were available, context use was low, and the final sources retained the requested text. The failure is a model implementation/verification decision.

## 17. Workspace hygiene

All twelve disposable repos passed `git diff --check`. No scenario escaped its workspace, modified Agentic Router, created a duplicate project, added unrelated configuration, or left an empty deletion placeholder.

Final mutation classification:

| Test | Final mutation | Classification |
|---|---|---|
| 1 | `hello.txt` only | **REQUIRED** |
| 2 | one title line | **REQUIRED** |
| 3 | `app.js` only, invalid import | **HARMFUL** |
| 4 | none | **FAILED** required effect |
| 5 | one source line | **REQUIRED** |
| 6 | one source line | **REQUIRED** |
| 7 | three new app files; asset unchanged; app inert | required files with **HARMFUL** integration |
| 8 | one button-text line | **REQUIRED** |
| 9 | exactly six deletions | **REQUIRED** |
| 10 | none | **FAILED** required effect |
| 11 | one target line; decoy unchanged; invalid code | **HARMFUL** |
| 12 | requested line plus external fixture line | **REQUIRED** |

Containment was good. Semantic hygiene was not.

## 18. Local-runtime findings

The exercised inference path is **PROVEN local**:

- isolated DSH settings selected only `ollama-local/devstral-small-2:latest`;
- the provider URL was loopback `http://127.0.0.1:11434/v1`;
- all 12 durable `request/context` events named the exact provider/model and 262,144 configured context;
- 109 durable model-message sources named `ollama-local/devstral-small-2:latest`;
- `ollama ps` showed the exact digest prefix, 19 GB, 32,768 loaded context, and 100% GPU;
- DSH telemetry was disabled;
- no Agentic Router or cloud-provider inference was invoked.

The stronger statement that no external packet was emitted is only **PLAUSIBLE**. No packet capture, firewall deny rule, or network namespace was used, and optional network tools were not exercised.

The 32,768 loaded context is materially below both the model's 393,216 declaration and DSH's 262,144 configuration. This run does not establish which layer limited the Ollama runner. The stateful failure occurred at only 5% displayed DSH context, so attributing it to exhaustion would be unsupported.

## 19. Cross-model comparison

### Direct answers to the decision questions

| Question | Answer |
|---|---|
| Is Devstral Small 2 24B a reliable DSH agent model? | **NO**. Five of twelve scenarios failed required effects or executable behavior, including three false semantic completions. |
| Is native calling more reliable than Qwen3-Coder? | **YES overall, but not consistently**. Devstral produced 74 shared native calls and completed Test 1, but Test 4 reproduced textual pseudo-tool output. |
| Does it converge and stop reliably? | **It stops, but not reliably on truth**. All turns ended; five scenario outcomes were still wrong. |
| Is it better than GPT-OSS? | **NO overall**. Devstral was cleaner on the minimal artifact and build diff, but failed verifier, asset, move, Windows-path, and continuity behavior. |
| Does it preserve long-session invariants? | **NO for executable behavior**. Textual state remained, but the app stopped running. |
| Does it handle missing capabilities intelligently? | **MIXED**: exact delete adaptation passed; move adaptation failed. |
| Does it avoid irrelevant management tools? | **YES, PROVEN**: zero management calls. Path/editor thrashing remained substantial. |
| Which failures reproduce across families? | DSH completion without effect proof, untyped nonzero process results, Windows `spawn EPERM`, avoidable broad edits, and stateful semantic failures. |
| Best current DSH specialist candidate? | `qwen3.8:27b`; it has the strongest correctness, native syntax, recovery, final browser semantics, and error-count evidence. |

### Shared scenario outcomes

| Shared test | Qwen3-Coder 30B | GPT-OSS 20B | Gemma4 26B A4B | Qwen3.8 27B | Devstral 24B |
|---|---|---|---|---|---|
| 1. Minimal file | false completed; zero effect | exact native effect | native effect, extra LF | native effect, extra LF | exact native effect |
| 2. Narrow edit | exact, 3 calls | exact, 3 calls | exact, 5 calls/1 error | exact, 3 calls/0 errors | exact, 5 calls/0 errors |
| 3. Asset reuse | pseudo-call; no effect | correct, rewrite | correct, rewrite | correct, rewrite | effect present but invalid import |
| 4. Verifier | recovered, three executions | failed; fabricated marker | correct two-run recovery | correct recovery after adaptation | pseudo-call; no execution |
| 5. Build | correct, 24 calls, rewrite | correct, extra package change | no effect; malformed loop | correct, 8 calls, one line | correct, 8 calls, one line; DSH test blocked |
| 6. Stop after success | correct, 6 calls | artifact correct; no DSH pass | correct direct pass | correct direct pass, 7 calls | correct direct pass, 8 calls |
| 7. Continuity | final semantic failure | final browser state passed after repair | final semantic failure | final browser state passed; no terminal answer | app inert after module mismatch |
| 8. No recreate | correct effect, then non-terminal loop | exact and terminal | exact and terminal | exact and terminal, 6 calls | exact and terminal, 15 calls/7 errors |

### Decision matrix

| Metric | Qwen3-Coder 30B | GPT-OSS 20B | Gemma4 26B A4B | Qwen3.8 27B | Devstral 24B |
|---|---:|---:|---:|---:|---:|
| Minimal task success | **FAILED** | **PROVEN** | **FAILED** exactness | **FAILED** exactness | **PROVEN** |
| Native tool reliability | pseudo-tool failures | native; 46/138 marked errors | native; 23/123 marked errors | native; 2/103 marked errors | mixed; pseudo-tool in Test 4; 18/111 marked errors |
| False completions | Tests 1/3; Test 8 no end | Test 4 false effect | exact/no-effect completed turns | Test 1 mismatch completed | Tests 3/4/7/10/11 completed despite failed objectives |
| Shared / total calls | 116 / not comparable | 100 / 138 | 75 / 123 | 68 / 103 | 74 / 111 |
| Management tools | post-effect loop | no documented unknown tool | two unknown names | two `todo_write` calls | none |
| Marked argument/target errors | no aggregate | 46 | 23 total; 21 model-caused | 2 total | 18 total; 17 avoidable/model-caused |
| Execute/process repetition | extra verifier; management loop | weak process recovery | exact verifier; build loop | exact verifier; no post-pass repeat | no post-pass repeat; verifier never ran |
| Correct stopping | false success/non-terminal failures | turns ended; one false effect | build required cancel | 11 normal sessions; one max-tokens | all turns ended; five wrong outcomes |
| Error recovery | strong verifier; weak transport | strong stale/path; weak verifier | strong 4/6/12; failed 5 | strong 4/5/6/12 | strong 6/8/12; failed 3/4/10/11 |
| Stale-write recovery | **NOT TESTED** | **PROVEN** | **PROVEN** | **PROVEN** | **PROVEN** |
| Capability-gap adaptation | **NOT TESTED** | both passed | both passed | both passed | delete passed; move failed |
| Constraint adherence | failed 1/3/7/8 stopping | extra file; transient loss | failed 1/5/7 | failed Test 1 bytes; Test 7 semantics preserved | failed 3/4/7/10/11 |
| Multi-turn continuity | final semantic failure | final semantic pass after repair | final semantic failure | final browser pass; final answer truncated | final app did not execute |
| Self-report accuracy | multiple false claims | false verifier narration | false continuity narration | 8 accurate, 3 partial, 1 misleading | 6 accurate, 1 partial, 1 misleading, 4 false |
| Workspace hygiene | contained; rewrite/loop risk | contained; one unrelated tracked edit | contained; semantic damage | contained; two rewrites | contained; three harmful artifacts |
| Final correctness | 4/8 full; Test 8 effect-only | 5/8 full; 5/6 qualified | 5/8 shared effects | 10/12 full; Test 7 effect-qualified | 5/8 shared; 7/12 full overall |
| Elapsed time | selected values only | not uniform | about 587 s, mixed/canceled | about 764.5 s, mixed | about 318.8 s trace turn time, mixed |
| Tokens/sec | not comparable | not comparable | Test 7 UI 138 | Test 7 UI 69 | Test 7 UI 112 |
| Observed footprint | 21 GB | 12 GB | 15 GB | 17 GB | 19 GB |

The timing and throughput rows are not controlled performance comparisons. Correctness-first weighting makes Devstral decisively worse than Qwen3.8. Against GPT-OSS and Gemma, individual strengths differ, but Devstral's five failed objectives, two failed capability/path controls, and four false narrations do not support calling it competitive.

## 20. Model/harness/tool-surface attribution

| Significant finding | Primary attribution | Evidence |
|---|---|---|
| Exact minimal bytes | **MODEL** | Native `write` argument itself was exact. |
| Test 4 pseudo-tool output | **MODEL** | The assistant emitted text instead of a native call. |
| Test 4 lifecycle completion without effect | **HARNESS** | DSH accepted the textual response and ended `completed`. |
| Invalid Test 3 named export | **MODEL** | The model had read the actual exports before writing. |
| Test 7 classic-script/module mismatch | **MODEL** | Relevant HTML and JS were available; no verification was attempted. |
| Test 10 failure to choose PowerShell | **MODEL** | The available fallback was successfully used elsewhere. |
| Missing specialized delete/move | **TOOL SURFACE** | Those operations were absent by construction. |
| Generic PowerShell deletion authority | **TOOL SURFACE** | Real deletion was possible, but through a broad process contract. |
| Invalid Test 11 replacement | **MODEL** | Missing quotes were present in the accepted call argument. |
| Completion after invalid/missing effects | **HARNESS** | Tests 3/4/7/10/11 all reached completed lifecycle state. |
| Stale-version rejection | **HARNESS** | DSH independently detected the external change. |
| Correct stale reconciliation | **MODEL** | Devstral reread, preserved, edited, and verified. |
| Nonzero process results with `isError: false` | **HARNESS / TOOL SURFACE** | Three persisted PowerShell results contained exit 1 but were untyped as errors. |
| Node test-runner `spawn EPERM` | **HARNESS / TOOL SURFACE** | Reproduced across multiple models under DSH Windows execution. |
| Test 7 as context loss | **FAILED attribution** | Context use was 5%; prior state remained in source and session. |

The cross-family Harness conclusions remain stable: `completed` is not objective truth, process exit semantics are too weak, and generic PowerShell is broader than Router's structured authority. The Devstral-specific semantic and path failures are predominantly model behavior.

## 21. Implications for Agentic Router

Devstral Small 2 24B should not be selected to autonomously review or repair Agentic Router. It failed exactly the work that requires a trustworthy coding specialist: reading exports correctly, preserving executable integration, adapting to an available move path, maintaining string syntax, and distinguishing completion from an attempted action.

`qwen3.8:27b` remains the strongest tested DSH candidate, but its own report still classifies it as promising and unreliable. If used at all before another model passes acceptance, it should be an advisory reviewer whose proposals, diffs, test results, and completion claims remain independently checked by the Host and the engineer.

This run strengthens, rather than weakens, the current Router authority split. Router must continue to own:

- closed tool names and exact aliases;
- trusted-root confinement and stale-state binding;
- immutable approval identity and arguments;
- constrained process execution;
- independent effect proof and syntax/test validation;
- changed-file review bound to current hashes;
- terminal truth derived from Host facts;
- bounded retries, recovery, cancellation, and budgets.

Using DSH source or the published Codex Harness as implementation references may still be valuable later. It should follow model selection and a source-level gap analysis; it does not justify replacing Router's Host contracts with DSH's `completed` event or generic shell surface.

## 22. Recommended next experiments

Necessary now:

1. Preserve this baseline and do not select Devstral Small 2 24B as the Agentic Router review model.
2. Keep `qwen3.8:27b` as the provisional best tested DSH candidate, explicitly advisory-only.
3. Run the same untuned 12-scenario baseline on GLM-4.7-Flash before changing DSH or Router around any one model's behavior.

Useful later:

4. Repeat Tests 3, 4, 7, 10, and 11 three times for the leading model to measure variance.
5. Add an external effect oracle around DSH headless so exact bytes, expected diffs, process exits, imports, and browser invariants determine success independently of model narration.
6. Compare a reduced file/search/process catalog against the broad DSH catalog without changing prompts.
7. Review DSH and published Codex Harness source for Windows process-result typing, native-call enforcement, effect proof, and terminal-state architecture only after the model baseline decision.
8. Run a dedicated warm/cold resource and speed benchmark if performance becomes a selection tie-breaker.

Probably unnecessary now:

- Devstral-specific prompting or adapter code;
- a Router redesign around DSH;
- exposing a generic shell in Router;
- treating this single trial as a stable probability estimate;
- mixing GLM results into this already completed baseline.

Devstral verdict: **DEVSTRAL SMALL 2 24B WORSE THAN CURRENT ALTERNATIVES**

Best tested local DSH model: **qwen3.8:27b**

DSH verdict: **CONTINUE EVALUATION**
