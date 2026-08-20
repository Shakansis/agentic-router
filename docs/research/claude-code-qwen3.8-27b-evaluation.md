# Claude Code evaluation: Qwen3.8 27B

Date: 2026-08-18  
Plan: [`docs/PLAN-v15-claude-code-qwen3.8-27b-evaluation.md`](../PLAN-v15-claude-code-qwen3.8-27b-evaluation.md)  
Model: `qwen3.8:27b`  
Claude Code: `2.1.234`  
Evaluation type: untuned controlled local baseline

Evidence labels are limited to **PROVEN**, **PLAUSIBLE**, **SPECULATIVE**, and **FAILED**. Claude Code lifecycle completion and model narration are not effect proof.

## Technical summary

**Qwen3.8 27B under Claude Code is PROMISING BUT UNRELIABLE.** Ten of the twelve scenario fixtures met the complete acceptance bar. The model produced the required primary code or filesystem effect in every scenario except the exact-byte requirement in Test 1, preserved every final invariant in the eight-request counter session, adapted to missing delete and move tools, made minimal build fixes, handled Windows paths, and preserved the external stale-write line.

The two full failures are material:

- Test 1 reproduced the same model-authored trailing-LF failure seen under DSH: the reasoning explicitly chose `hello world\n`, Claude Code executed it, and the harness completed even though the file was 12 bytes instead of 11.
- Test 3 integrated the existing fireworks implementation correctly, but then entered a permission-driven validation loop, left `app_check.mjs` behind, produced no terminal result, and required cancellation after about 118 seconds.

Claude Code did not outperform DSH overall. It used 118 native calls versus DSH's 103, returned 23 results marked as errors versus DSH's 2, and required one cancellation. The error counts are not semantically identical: 18 Claude errors were permission/safety denials, four were path or environment failures, and one was the designed verifier exit 17. No pseudo-tool text, unknown tool, malformed JSON, malformed schema, cloud tool, MCP call, or subagent call occurred.

The long-session result is the strongest positive signal. The same exact eight-request sequence that ended without a final DSH answer completed normally in Claude Code and passed real-browser verification: title, `score-value`, `max-width: 420px`, zero floor, reset, increment by 2, exact-5 reachability, real celebration at 5, and asset reuse all remained valid.

The harness conclusion is still negative for Agentic Router authority. Claude Code's Windows tool surface introduced synthetic Unix paths, Bash/PowerShell cwd mismatches, valid-command false positives, inconsistent approval behavior, and no independent objective-level effect proof. Its stale edit support safely merged this exact replacement and returned `staleRecovered: true`, but it did not reject the write before mutation as DSH did. Claude Code is useful comparison evidence, not a replacement for Router-owned validation, approval, effect proof, changed-file review, or terminal truth.

Counting note: this evaluation executed **12 scenario fixtures**. The earlier benchmark's Tests 13-16 are aggregate analyses derived from those runs, not four additional model executions.

## Environment and runtime

| Item | Observed value |
|---|---|
| OS | Windows 10 Pro, `10.0.19045` |
| GPUs | NVIDIA GeForce RTX 4090, 24,564 MiB; NVIDIA GeForce RTX 2070 SUPER, 8,192 MiB |
| NVIDIA driver | `610.74` on both GPUs |
| Ollama | `0.32.14`, loopback `http://127.0.0.1:11434` |
| Claude Code | Native Windows client `2.1.234`, installed from Anthropic's official installer |
| Transport | Anthropic-compatible `/v1/messages` through local Ollama |
| Claude mode | Headless `--print`, `--bare`, `--safe-mode`, stream JSON evidence |
| Permission mode | `acceptEdits`; narrow explicit Bash allowances only for Tests 4-6 |
| Tools actually exposed | `Bash`, `Edit`, `Read` |
| Disabled surfaces | WebSearch, WebFetch, Chrome, MCP/customizations, slash commands |
| Telemetry settings | `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1`, telemetry/error reporting/updater disabled |
| Disposable root | `C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-claude-qwen38-eval-20260818` |

Ollama officially documents Claude Code through its Anthropic-compatible API and the manual loopback environment used here ([Ollama Claude Code integration](https://docs.ollama.com/integrations/claude-code), [Anthropic compatibility](https://docs.ollama.com/api/anthropic-compatibility)). Claude Code's CLI documents the selected headless, bare, stream-JSON, model, tool, and permission controls ([Claude Code CLI reference](https://code.claude.com/docs/en/cli-usage)).

Native Claude Code sandboxing is not supported on Windows; Anthropic documents macOS, Linux, and WSL2 support and says native Windows support is planned ([Claude Code sandboxing](https://code.claude.com/docs/en/sandboxing)). Therefore this baseline did not use `bypassPermissions`. All evaluated workspaces were disposable Git clones, and commands outside the ordinary edit set remained behind `acceptEdits` policy.

No Agentic Router inference, build, E2E test, runtime integration, source modification, or architecture work was performed.

## Verified model identity

| Field | Observed value |
|---|---|
| Exact tag | `qwen3.8:27b` |
| Full digest | `a5dfec209c3bd4a76748c0d493e596d15f8c84f5fcc255b4072f1e01a863fb9b` |
| Stored size | 17,741,872,154 bytes |
| Parent tag | `qwen3.8:27b-mtp-backup` |
| Architecture / family | `qwen35` |
| Parameter count | 27,320,697,856 (`27.3B`) |
| Quantization | `Q4_K_M` |
| Capabilities | completion, tools, thinking, vision |
| Declared context | 262,144 |
| Embedding length | 5,120 |
| Renderer / parser | `qwen3.8` / `qwen3.5` |
| Current model parameter difference | `draft_num_predict 0` |

The current tag uses the same two base blob sources as the earlier DSH/Codex artifact, whose preserved digest is `22130167...`. The manifest changed `draft_num_predict` from `4` to `0`. The operator explained that this disables MTP functionality newly enabled by Ollama `0.32.14` so the effective behavior remains aligned with the earlier runtime. The manifest and shared blob provenance are **PROVEN**; the operational rationale is recorded from the operator rather than inferred from the tag.

Every Claude assistant event named `qwen3.8:27b`, and Ollama `/api/ps` returned the exact current digest. No fallback model was configured or observed.

## Context and hardware were not constant

The excluded no-tool preflight loaded Qwen at 32,768 context, 19 GB runtime size, and `5%/95% CPU/GPU`. After the long and stale sessions, `/api/ps` showed 131,072 context, 26 GB runtime size, and `4%/96% CPU/GPU`. The same Ollama server PID was visible on both GPUs.

No context setting was changed during the benchmark. The growth was produced by the Claude/Ollama path and is **PROVEN** as observed runtime state; the exact decision mechanism is **UNKNOWN**. It is an unavoidable harness/runtime difference from the preserved DSH and Codex baselines, both of which remained at 32,768.

This invalidates direct latency, memory, or tokens-per-second ranking. Claude Code also reported nonzero `costUSD` values because it treated the unrecognized local name as a first-party model. Those values are client-side estimates, not Ollama charges, and are excluded.

## Controlled method

All twelve workspaces were cloned from the exact preserved commits:

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

Tests 1-6 and 8-11 used fresh independent sessions. Test 7 used one registration turn plus the exact eight sequential requests in one persistent session. Test 12 used one persistent session with an external fixture mutation between the read turn and the edit turn. Prompts were not changed after observing behavior, and failed turns were not rerun for a better result.

## Scenario results

The calls/error column counts native tool-use events and tool results explicitly carrying `is_error: true`. Permission denials are a subset recorded in the terminal result. Durations are Claude-reported API/session duration; Test 3 uses observed transcript time because it had no terminal result.

| Test | Result | Calls / errors | Duration | Independently observed outcome |
|---|---:|---:|---:|---|
| 1. Minimal file | **FAILED** exactness; native effect **PROVEN** | 1 / 0 | 11.7 s | Bash wrote `hello world\n`; host hex `68656C6C6F20776F726C640A`, 12 bytes. Claude completed and narration omitted the mismatch. |
| 2. Narrow edit | **PROVEN** | 6 / 2 | 24.4 s | Exact one-line title diff. Two denied attempts targeted synthetic `/Users/dev/workspace-...` paths before cwd recovery. |
| 3. Asset reuse | Primary effect **PROVEN**; completion/hygiene **FAILED** | 13 / 4 | about 118.4 s | Real browser click set `effect=fireworks`, `particleCount=80`; assets byte-identical. Validation loop left `app_check.mjs`; no terminal result; manual cancellation required. |
| 4. Recoverable verifier | **PROVEN**, inefficient | 12 / 4 | 94.0 s | Exactly two real fixture executions: designed exit 17, then pass. Relative `pwsh` cwd mismatch added failed calls. Tracked Git stayed clean. |
| 5. Build/test diagnosis | **PROVEN** | 12 / 4 | 51.5 s | One-character subtraction-to-addition diff; independent Node test passed. Synthetic Unix paths and denied `sed` caused recovery calls. |
| 6. Stop after success | **PROVEN** | 7 / 0 | 30.3 s | One-line `Hello` to `Hi`; exactly one post-fix `npm test`, then stop. Independent Node test passed. |
| 7. Long continuity | **PROVEN** final behavior and terminal answer | 26 / 4 | 571.9 s | All final invariants passed file, syntax, and real-browser checks. Nine turns completed normally. |
| 8. No recreate | **PROVEN** | 6 / 0 | 50.5 s | Exact `Save` to `Save note` line; no duplicate, replacement, rename, or extra file. |
| 9. Delete gap | **PROVEN**, adapted | 22 / 3 | 167.0 s | `git rm` was denied; `rm` removed exactly six files. Exact five-file required tree remained; no placeholder. |
| 10. Generic move gap | **PROVEN**, adapted | 6 / 2 | 77.4 s | `mkdir` plus `mv`; source absent, one target, 69 bytes, SHA-256 `602281D...`. |
| 11. Windows paths | **PROVEN** | 3 / 0 | 25.1 s | Read absolute target, edited relative `nested path\source file.js`, preserved quotes/decoy, syntax passed. |
| 12. Stale write | Final effect and detection **PROVEN**; semantics differ | 4 / 0 | 57.0 s | Edit returned `staleRecovered: true`, merged against current file, preserved `external=preserve`, then model reread. No rejection occurred. |

Ten scenarios were fully accepted. Test 1 failed exact bytes. Test 3 had a correct executable primary effect but failed terminal and hygiene requirements. This is one baseline trial per fixture, not a statistical pass rate.

## Native tool-call traces

### Test 1: the model chose the wrong bytes

```text
model reasoning: adding a newline is safer
Bash: printf 'hello world\n' > hello.txt && cat hello.txt
-> success
Claude terminal: completed
Host length/hex: 12 / 68656C6C6F20776F726C640A
```

The wrong LF was explicit in model reasoning and tool input. Attribution: **MODEL** for the exactness failure; **HARNESS** for accepting terminal completion without independently checking the requested bytes.

### Test 3: correct integration followed by a validation loop

```text
read app and existing fireworks modules
edit app.js to import/call launchFireworks
attempt copy to /tmp for module validation -> denied
attempt recursive copy -> denied
attempt three temporary .mjs files + node --check -> denied
create app_check.mjs
node --check -> denied
try alternate Node validation
controlled cancellation; no terminal result
```

Independent browser execution proved the integration itself. The loop and abandoned file fail full acceptance.

### Test 4: Bash/PowerShell cwd mismatch

```text
pwsh -File .\verify-once.ps1 -> script path not found
inspect Bash cwd and missing state
pwsh -File C:\...\verify-once.ps1 -> exit 17, marker observed
inspect marker and Git
pwsh -File C:\...\verify-once.ps1 -> verification passed
Git clean; terminal completed
```

There were exactly two actual fixture executions after path adaptation.

### Test 12: automatic targeted stale recovery

```text
Read -> mode=old; owner=fixture
external writer -> add external=preserve
Edit old_string=mode=old, new_string=mode=new
-> success note: file changed since prior read
-> tool metadata: staleRecovered=true
Read -> mode=new; owner=fixture; external=preserve
terminal completed
```

Claude Code detected staleness but applied the narrow replacement directly to the current file. DSH rejected first and required a retry. Both preserved the external line in this fixture; only DSH proved pre-mutation rejection.

## Tool failures and Windows friction

| Category | Count | Assessment |
|---|---:|---|
| Permission/safety denials | 18 | Mostly **HARNESS / TOOL SURFACE**; includes valid CSS heredoc, `git rm`, Node checks, synthetic-path reads, and command composition |
| Path/environment failures | 4 | **HARNESS / ENVIRONMENT + MODEL recovery**; Bash path mapping, relative `pwsh`, missing pre-created destination, and Unix/Windows path confusion |
| Designed verifier exit 17 | 1 | Required benchmark transition, not a defect |
| **Total `is_error: true`** | **23** | Error semantics differ from DSH's marked-error field |

Claude Code initialized every evaluated session with only `Bash`, `Edit`, and `Read`, despite the CLI's broader built-in tool family. File creation therefore used Bash heredocs. A valid CSS heredoc was rejected as `Contains brace with quote character (expansion obfuscation)`, and the model rewrote the font-family line to avoid quotes. This was a harness false positive with a harmless model workaround.

The most persistent path problem was conflicting location context:

- Test 2 tried `/Users/dev/workspace-8077e3d3` twice before using the real cwd.
- Tests 4-5 received `/tmp/agentic-router-...` paths from Bash context that did not work with Windows `pwsh` or `Edit`.
- The model recovered by using the actual absolute Windows path, but those calls would be unnecessary under a consistent workspace contract.

No textual pseudo-tool output, unknown tool, malformed JSON, malformed argument schema, agent/subagent call, goal/todo call, Web call, or MCP call was observed.

## Recovery and capability gaps

Recovery was usually correct:

- **PROVEN:** Test 4 distinguished path failure from the intentional exit 17, inspected the marker, retried once, and stopped after exit 0.
- **PROVEN:** Test 5 recovered from synthetic Unix paths, denied `sed`, and an invalid Edit path, then made the exact Windows-path edit and obtained a passing test.
- **PROVEN:** Test 9 switched from denied `git rm` to workspace-local `rm`, then verified the tree and dependency chain.
- **PROVEN:** Test 10 used `mkdir` and `mv`, then verified absence, hash, size, and uniqueness.
- **PROVEN:** Test 12 understood the stale warning, reread, and accurately reported the preserved external line.
- **FAILED:** Test 3 did not stop changing validation strategy after the correct effect; permission denials amplified its repetition until cancellation.

The capability-gap results support **ADAPT** for delete and move. They do not justify adding an unrestricted shell to Agentic Router.

## Delete and move evidence

Test 9 ended with exactly:

```text
hangman.html
hangman.css
hangman.js
fireworks/
  firework_engine.js
  firework_config.js
```

Exactly these six tracked files were deleted:

```text
firework_config.js
firework_engine.js
game.js
hangman-words.js
index.html
styles.css
```

Test 10 moved the only 69-byte `notice.txt` from `incoming/` to `archive/`. The target SHA-256 remained `602281D02B1A8F05DA127C8F1FBAEA631975D8ABA3107F7F35F2FFE945F24303`; the source was absent and a full-tree search found one copy.

## Windows-path evidence

Test 11 used the absolute Windows path for the initial `Read`, then the requested relative path for `Edit`:

```text
Read C:\...\nested path\source file.js
Edit nested path\source file.js
Bash git status --porcelain && git diff
```

The final diff was exactly:

```diff
-export const mode = "old";
+export const mode = "new";
 export const keep = "unchanged";
```

`relative\other.js` remained byte-identical, and independent `node --check` passed.

## Stopping and convergence

Eleven of twelve scenario sessions ended normally. Test 3 required cancellation after the correct integration because validation attempts kept changing form without gaining permission.

Aggregate Claude-reported terminal duration was 1,160.7 seconds. Adding Test 3's observed 118.4 seconds yields about 1,279 seconds, or 21.3 minutes, excluding human validation pauses. DSH reported about 764.5 seconds. The difference is descriptive only because Claude's context grew to 131,072 and the current host used a different Ollama/driver/GPU configuration.

Test 6 process discipline was exact:

| Invocation | Classification | Result |
|---|---|---|
| `npm test` after the fix | **JUSTIFIED** | 1 pass, 0 fail |

No process ran after the pass.

## Tool-surface discipline

Aggregate native calls:

| Tool | Calls |
|---|---:|
| `Bash` | 65 |
| `Read` | 37 |
| `Edit` | 16 |
| **Total** | **118** |

The following scenario-specific judgment uses the same categories as the DSH report. `HARMFUL` means the call worsened authoritative workspace state; Test 3's abandoned `app_check.mjs` is the only such call.

| Test | Required | Useful | Optional | Unnecessary | Harmful |
|---|---:|---:|---:|---:|---:|
| 1 | 1 | 0 | 0 | 0 | 0 |
| 2 | 2 | 2 | 0 | 2 | 0 |
| 3 | 4 | 3 | 0 | 5 | 1 |
| 4 | 3 | 5 | 0 | 4 | 0 |
| 5 | 5 | 3 | 0 | 4 | 0 |
| 6 | 5 | 2 | 0 | 0 | 0 |
| 7 | 14 | 5 | 3 | 4 | 0 |
| 8 | 2 | 4 | 0 | 0 | 0 |
| 9 | 2 | 16 | 1 | 3 | 0 |
| 10 | 2 | 3 | 0 | 1 | 0 |
| 11 | 2 | 1 | 0 | 0 | 0 |
| 12 | 3 | 1 | 0 | 0 | 0 |
| **Total** | **45** | **45** | **4** | **23** | **1** |

The classification is analytical rather than mechanically reproducible. Most unnecessary calls came from synthetic paths, command-policy negotiation, and validation after the primary effect.

## Self-verification accuracy

| Classification | Scenarios | Notes |
|---|---:|---|
| **ACCURATE** | 10 | Tests 2, 4-12 matched authoritative state |
| **MISLEADING** | 1 | Test 1 said the requested file was created without disclosing the extra LF |
| No terminal narration | 1 | Test 3 was canceled during validation |

Test 12's narration accurately distinguished “edit applied” from the subsequent stale warning and reread. It did not falsely claim that the harness rejected the write.

## Multi-turn continuity

All nine turns in the Test 7 session completed normally: one `READY` registration plus eight exact requests. The final browser observations were:

| Check | Observed result |
|---|---|
| Page title | `Continuity App` |
| Display id | `score-value` |
| Card width | computed `420px` |
| Decrement at zero | remained `0` |
| Increment | `0 -> 2 -> 4` |
| Reach exact 5 | `4 -> decrement -> 3 -> increment -> 5` |
| Celebration at 5 | `data-celebrated="true"` |
| Reset | returned `5 -> 0` |
| Existing asset | SHA-256 unchanged: `27842633...` |
| Duplicate implementation | none |

The seventh request referred to the historical `counter-value` id, while this run's first turn had chosen `count`. Qwen correctly applied the semantic rename to the live display id and JavaScript lookup, producing `score-value` consistently rather than declaring a false no-op.

The final request changed increment from 1 to 2 without broadening `count === 5`. Qwen explicitly reasoned that exact 5 remained reachable through `4 -> 3 -> 5`; real-browser interaction confirmed both reachability and the real imported celebration effect.

## Workspace hygiene

All twelve repositories stayed at the expected baseline commit, and every `git diff --check` passed.

- **PROVEN:** no observed action escaped a disposable workspace.
- **PROVEN:** Tests 2, 5, 6, 8, 11, and 12 touched only the requested tracked file.
- **PROVEN:** Test 4 left tracked Git clean and only its ignored state marker.
- **PROVEN:** Test 7 added only the requested app files and preserved the asset byte-for-byte.
- **PROVEN:** Tests 9-10 produced exactly the requested delete/move effects.
- **FAILED:** Test 3 left untracked `app_check.mjs`, a duplicate validation artifact unrelated to the final application.
- **FAILED:** Test 1's only file had the wrong final byte.

Claude Code's built-in file-write boundary and permissions helped contain work, but containment did not enforce objective exactness, minimal final file sets, or terminal truth.

## Fully local operation

The evaluated inference path is **PROVEN** local within the following boundary:

- `ANTHROPIC_BASE_URL` was the loopback Ollama endpoint.
- Every assistant/result event named exact `qwen3.8:27b`.
- Ollama `/api/ps` returned the current full digest and local runtime state.
- `WebSearch` and `WebFetch` were explicitly disallowed; MCP and Chrome were disabled.
- Claude stream init reported `analytics_disabled=true` and `product_feedback_disabled=true`.
- Server usage reported zero Web search/fetch requests.

No packet capture or deny-all firewall was used. Therefore exhaustive absence of every external packet is **PLAUSIBLE**, not proven. The Claude client printed `unrecognized_model` and synthetic cost metadata, but the actual model endpoint and loaded artifact were local.

## Same-model comparison with DSH

| Scenario | Qwen3.8 / DSH | Qwen3.8 / Claude Code | Better evidence |
|---|---|---|---|
| 1. Exact minimal | 1 call; extra LF; completed | 1 call; extra LF; completed | Tie; strongly model-level |
| 2. Narrow edit | 3 / 0; exact | 6 / 2; exact | DSH cleaner |
| 3. Asset reuse | 6 / 0; correct, terminal | 13 / 4; correct effect, extra file, canceled | DSH clear win |
| 4. Verifier recovery | 11 / 0; correct | 12 / 4; correct | DSH cleaner |
| 5. Build diagnosis | 8 / 1; correct | 12 / 4; correct | DSH cleaner |
| 6. Stop after success | 7 / 0; correct | 7 / 0; correct | Tie |
| 7. Long continuity | Correct behavior; final answer hit max-tokens | Correct behavior and normal final answer | Claude win |
| 8. No recreate | 6 / 0; exact | 6 / 0; exact | Tie |
| 9. Delete gap | 22 / 0; correct | 22 / 3; correct | DSH cleaner |
| 10. Move gap | 5 / 0; correct | 6 / 2; correct | DSH cleaner |
| 11. Windows path | 3 / 0; exact | 3 / 0; exact | Tie |
| 12. Stale write | Reject, reread, retry; correct | Automatic narrow merge + warning, reread; correct | Different authority semantics |
| Fully accepted | **10/12** | **10/12** | Tie on count, different failures |
| Aggregate calls | **103** | **118** | DSH |
| Marked errors | **2** | **23** | Not directly equivalent; Claude materially noisier |

The trailing LF reproducing under both harnesses with explicit model reasoning is the strongest cross-harness model attribution. Test 3 and the Windows path/policy errors demonstrate that harness choice materially changes convergence and hygiene.

## Directly comparable Codex subset

The earlier Codex smoke did not run all twelve scenarios, so a full three-harness ranking is unavailable.

| Shared scenario | Codex | DSH | Claude Code |
|---|---|---|---|
| Minimal exact file | Exact 11 bytes after recovery | Extra LF | Extra LF |
| Asset reuse | Correct, 6 calls, terminal | Correct, 6 calls, terminal | Correct, 13 calls, extra file, canceled |
| Build diagnosis | Correct, 8 calls | Correct, 8 calls | Correct, 12 calls |
| Notes edit | Correct, 4 calls | Correct, 6 calls | Correct, 6 calls |
| Explicit continuity control | Separate five-turn gate: 3/3 PASS | Exact eight-request run: correct effect, no final answer | Exact eight-request run: correct effect and final answer |

Codex currently has the strongest combined exact-file and repeated continuity evidence, but it has not passed the full twelve-scenario suite. Its earlier shell-only surface also caused failed patch attempts and broad rewrites. A definitive harness ranking requires the same twelve fixtures under the current Codex runtime and model configuration.

## Model, harness, and tool-surface attribution

| Finding | Most likely layer | Basis |
|---|---|---|
| Test 1 trailing LF | **MODEL** | The model explicitly reasoned for and emitted `\n`; same failure under DSH |
| Completion despite wrong bytes | **HARNESS** | Claude terminal truth did not depend on independent requested-byte proof |
| Synthetic `/Users/...` and `/tmp/...` paths | **HARNESS / CONTEXT** | Paths arrived through Claude's system/Bash workspace representation, not the real Windows root |
| Correct recovery from path errors | **MODEL** | It changed to the observed Windows path and completed |
| Valid CSS heredoc rejection | **HARNESS / TOOL SURFACE** | Command filter labeled ordinary CSS braces/quotes as obfuscation |
| Test 3 validation loop | **MODEL + HARNESS / TOOL SURFACE** | Qwen kept changing validation strategy; repeated policy denials supplied the loop condition |
| Test 3 extra `app_check.mjs` | **MODEL** | The model created and failed to remove the file |
| Exact Test 6 stopping | **MODEL + HARNESS** | One edit, one post-fix test, terminal completion |
| Correct eight-request continuity | **MODEL + SESSION HARNESS** | Persistent state, exact edits, and executable invariants all held |
| `git rm` denied but `rm` allowed | **HARNESS / PERMISSION POLICY** | Same requested deletion, different command approval outcome |
| Stale warning and automatic merge | **HARNESS** | Tool returned `staleRecovered: true` and current structured patch |
| Correct stale reconciliation/narration | **MODEL** | It reread, understood, preserved, and reported the external line |
| Context growth 32k to 131k | **HARNESS / OLLAMA RUNTIME**, exact cause unknown | No benchmark setting changed; `/api/ps` proved the transition |

## Implications for Agentic Router

Claude Code does not solve the authority problem that motivated Agentic Router's Host-owned execution contracts.

Useful evidence:

- Qwen3.8 can perform long sequential coding work correctly when the session and instructions are explicit.
- Claude's `Edit` tool provides useful stale awareness and a structured patch result.
- The model adapted safely to delete/move capability gaps without hallucinating tools.
- Native tool syntax was completely reliable.

Disqualifying evidence for autonomous Router correction:

- exact requested effects can still be wrong while the harness reports success;
- a correct code effect can be followed by a non-terminating validation loop and abandoned file;
- Windows paths and command policy add substantial operational noise;
- automatic stale merge is not the same authority contract as Host rejection, reconciliation, and revalidation;
- context and memory footprint changed implicitly;
- final objective truth still depends on external validation.

Qwen3.8 remains appropriate only as an advisory specialist whose findings and proposed edits are independently checked by the Host. Claude Code may be useful as a reference implementation for session/tool ergonomics, but its native Windows runtime should not replace Router validation, approval, effect proof, or terminal state.

## Limitations and robustness

- One trial per fixture cannot estimate run-to-run variance.
- The current model manifest and Ollama version differ from the earlier digest/runtime, although the current manifest intentionally disables the newly enabled MTP path and uses the same base blobs.
- The host now has a second active GPU and a different driver, and Claude's context grew to 131,072; performance is not directly comparable.
- Claude, DSH, and Codex use different error semantics and tool surfaces; raw error counts are descriptive.
- Native Windows Claude Code has no OS-level sandbox, so this evaluation used disposable workspaces and `acceptEdits`, not bypass mode.
- Real-browser validation covered Tests 3 and 7, not a browser compatibility matrix.
- Local-provider configuration and zero Web calls do not prove packet-level offline isolation.
- The current Codex path has not run the complete twelve-scenario suite.

## Recommended next experiments

1. Run the same twelve fixtures under the current Codex harness, current `qwen3.8:27b` manifest, Ollama `0.32.14`, and the same hardware. This is necessary for a fair harness decision.
2. Repeat Claude Tests 1 and 3 three times without prompt changes. Test 1 measures model variance; Test 3 measures whether permission-driven nontermination is stable.
3. Run a narrow Claude context control at explicitly observed 32,768 and 131,072 only if the adapter exposes a supported setting. Do not infer or force an undocumented parameter.
4. Inspect Claude Code's stale-edit contract against adversarial surrounding-content changes before treating `staleRecovered` as safe enough for any Router reference design.
5. Keep any real Agentic Router review trial read-only and require Host-verified citations to files/lines; do not authorize model-authored corrections from this result.

## Final verdicts

### Qwen3.8 under Claude Code

**PROMISING BUT UNRELIABLE.** Ten of twelve full scenarios passed, including the long browser session, but the repeated exact-byte failure and one non-terminal hygiene failure remain disqualifying for autonomous changes.

### Harness comparison

**DSH is cleaner on this exact twelve-scenario baseline; Claude Code is stronger on the long terminal session; Codex has the strongest available targeted continuity evidence but lacks a full twelve-scenario control.** There is no fully proven harness winner yet.

### Agentic Router decision

**CONTINUE EVALUATION; DO NOT ADOPT CLAUDE CODE AS EXECUTION AUTHORITY.** Retain Router Host ownership of validation, approvals, effect proof, stale/conflict policy, changed-file review, and terminal truth.
