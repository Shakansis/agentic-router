# DeepSeek Harness evaluation for Agentic Router

Date: 2026-08-15
Evaluation plan: `docs/PLAN-v1-deepseek-harness-evaluation.md`
Agentic Router revision: `742b8d94923c96a75d86830be55d68870bbfd7be` (`main`, with pre-existing uncommitted development work)
DeepSeek Harness package: `@deepseek-ai/dsh` `0.1.0-rc.6`
DeepSeek Harness source inspected: `47f943859bef60e4160492346772ded9b24f765a`

Evidence labels in this report mean:

- **PROVEN**: directly observed in a recorded run, persisted trace, independent filesystem check, or independent validation.
- **PLAUSIBLE**: supported by configuration and observations, but not exhaustively instrumented.
- **SPECULATIVE**: an architectural possibility that was not exercised.
- **FAILED**: the requested behavior was not achieved, the terminal state was false, or a required invariant was broken.

## 1. Executive summary

**Result: DSH is not reliable enough to own Agentic Router's coding-agent execution loop today.** It is a serious and promising runtime, but the tested release has failures at the exact boundaries Agentic Router treats as authoritative:

- **FAILED:** four clean minimal-file trials, including one outside Codex's outer sandbox, returned exit code 0 / `completed` after printing XML-like tool markup as ordinary text. No tool call was recorded and no file existed.
- **FAILED:** the existing-asset task failed the same way with zero tool calls.
- **FAILED:** a narrow edit produced the correct one-line change, then entered a 49-call `job_list` / `todo_write` loop with no `turn/end`; the process had to be killed after 63 seconds.
- **FAILED:** an eight-follow-up Web session preserved many facts but ended with two broken invariants: the `#counter` CSS selector was stale after renaming the element to `score-value`, and celebration at `count === 5` became unreachable after increment changed from 1 to 2.
- **PROVEN:** small edit, recoverable command failure, build-fix artifact, one-test-only constraint, workspace hygiene, Web cancellation, and same-session resume all worked in at least one controlled run.
- **PROVEN:** DSH used the exact local `qwen3-coder:30b` through Ollama's loopback OpenAI-compatible endpoint and emitted durable native tool-call/result traces when the model used the expected transport.

The central problem is not that DSH lacks tools. It is that `headless` terminal truth is derived from the model turn's completion reason, not from independently proven task effects. DSH therefore reported successful process completion for tasks with no effects, while another run never reached a terminal event after the requested effect was already present.

The same Qwen model also repeated work under Agentic Router. The comparison does not establish that Router's current loop is efficient. It does establish a material authority difference: Router verified file effects, required explicit process approvals, exposed a bounded recovery decision, and reported cancellation instead of converting it into success.

DSH should remain an evaluated external runtime candidate. It should not replace the Host-owned execution, effect-proof, approval, changed-file review, or terminal-state mechanisms.

## 2. Test environment

| Item | Value |
|---|---|
| OS | Windows 10 Pro 22H2, build `19045.6466`, AMD64 |
| GPU | NVIDIA GeForce RTX 4090, 24 GB |
| Node / npm | `v22.18.0` / `10.9.3` |
| DSH | globally installed `@deepseek-ai/dsh@0.1.0-rc.6` |
| DSH source snapshot | official repository, commit `47f943859bef60e4160492346772ded9b24f765a` |
| Ollama | `0.32.13`, `http://127.0.0.1:11434` |
| Model | `qwen3-coder:30b`, digest `06c1097efce0431c2045fe7b2e5108366e43bee1b4603a7aded8f21689e90bca` |
| Model artifact | Qwen3MoE, 30.5B parameters, Q4_K_M; Ollama advertises completion and tools |
| Declared maximum context | 262,144 tokens from model metadata |
| DSH configured context | 262,144 default context, 32,768 maximum output |
| Observed loaded context | 32,768 tokens in `ollama ps`, 21 GB, 100% GPU |
| DSH provider | custom `ollama-local`, `openai-completions`, `http://127.0.0.1:11434/v1` |
| DSH modes | `headless` for isolated cases; official Web UI for multi-turn and cancellation |
| Permission mode | DSH `workspace-write`; telemetry hard-disabled for every run |
| Router comparison | current Release build, isolated data root and fresh Git workspaces, explicit `qwen3-coder:30b`, cloud disabled |

The official project identifies the product as a developer preview and explicitly warns of compatibility-breaking changes ([official DSH README at the evaluated revision](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/README.md#developer-preview)). The package was used as installed; DSH source was cloned only for read-only contract inspection.

### Isolation and fairness

All primary scenarios ran in fresh Git clones under:

`C:\Users\Rodrigo\AppData\Local\Temp\agentic-router-dsh-eval-20260815`

The Agentic Router checkout was never used as a DSH workspace. Every result was checked with Git status/diff and, where applicable, an independent test command.

An initial DSH edit run and an initial Router process run were affected by Codex's enclosing sandbox. Those observations were excluded from efficacy results. Fair repeats ran outside the enclosing sandbox while retaining DSH's `workspace-write` policy or Router's own Host policies. The minimal DSH failure was also repeated outside the enclosing sandbox and remained identical.

## 3. Exact scenarios executed

| Test | Fresh fixture and exact task |
|---|---|
| 1 | Empty Git repo. `Create a file named hello.txt containing exactly:` followed by `hello world`, then `Do not create or modify any other files.` Four trials; three corrected headless trials plus one fair external-sandbox trial. |
| 2 | Existing `index.html`. `Change only the page title from 'Test App' to 'Hangman App'. Do not modify anything else.` |
| 3 | Existing `app.js`, `fireworks/firework_engine.js`, and `fireworks/firework_config.js`. `The current fireworks implementation is poor. Replace it by using the existing fireworks implementation inside the fireworks folder. Do not create another fireworks engine.` |
| 4 | `verify-once.ps1` returns 17 and writes ignored state on its first invocation, then succeeds. Task: run it, inspect/adapt, and run it again without changing tracked files. |
| 5 | Node test expects `add(2, 3) === 5`; implementation subtracts. Task: diagnose, make the smallest fix, run tests, and avoid unrelated changes. |
| 6 | Node test expects `greeting() === "Hi"`. Task explicitly requires changing the value, running `npm test` once, and not rerunning after success. |
| 7 | One DSH Web session: create counter; set title; add decrement; center/max-width; enforce zero floor; reuse `assets/celebrate.js` at 5; rename `counter-value` to `score-value`; change increment from 1 to 2 while preserving all prior decisions. |
| 8 | Existing three-file Notes app. Explicit no-recreate/no-duplicate/no-rename constraints; change only the Save button text to `Save note`. |
| 9 | Inspect Git status/diff and added files after every scenario, including ignored state files. |
| 10 | Use only the loopback Ollama provider; keep telemetry disabled; inspect configuration, model source, environment key presence, session provider/model, and loaded runtime. |
| 11 | In DSH Web, start a 30-second PowerShell verification followed by a one-line edit; press Stop while the process is active; inspect state; resume in the same session. |
| 12 | Run four identical tasks through current Agentic Router: Tests 1, 2, 3, and 5, with the same exact Qwen model and fresh clones. |

The disposable baseline commits are recorded in the evaluation plan and remain in the temporary lab for reproduction.

## 4. Results for each scenario

| Test | Outcome | Evidence and observations |
|---|---|---|
| 1. Minimal file | **FAILED** | Four runs printed a `<function=write>` block as text, recorded zero `tool/call` events, ended as `completed`/exit 0, and left Git clean with no `hello.txt`. Fair run elapsed 4.6 s. |
| 2. Small edit | **PROVEN** | Fair run: `glob -> read -> edit`; one file and one line changed, no added files, exit 0. Elapsed 5.2 s. |
| 3. Asset reuse | **FAILED** | Printed `<function=glob>` as text, zero recorded calls, exit 0/`completed`, clean Git, no integration. Elapsed 4.5 s. |
| 4. Error recovery | **PROVEN**, inefficient | First command failed as designed; DSH inspected state and recovered. Tracked Git state remained clean. It ran the verification three times, not twice, used 11 calls, and incorrectly summarized the first exit code as 1 instead of 17. |
| 5. Build/test diagnosis | **PROVEN**, inefficient | Corrected subtraction to addition; independent `npm test` passed. One tracked file changed. DSH used 24 calls, tried several path forms/edit tools, finally rewrote the full three-line file, removed its final newline, and invoked irrelevant goal/job/agent tools. |
| 6. Repeat-loop control | **PROVEN** | One minimal line edit and exactly one `npm test` call; independent diff was one insertion/one deletion. Six calls total. |
| 7. Long session | **FAILED** | First work turn was a text pseudo-call with no effect. The second turn recovered by creating the missing files. Nine turns and 23 native calls persisted correctly, but the final state had stale `#counter` CSS and unreachable `count === 5` celebration after `count += 2`. DSH claimed all behavior was preserved. |
| 8. Do-not-recreate | **FAILED** operationally | Requested one-line diff was correct and no files were duplicated. Afterward DSH looped: 49 calls total, including 22 `job_list` and 21 `todo_write`; no `turn/end`. The disposable process was killed after 63.1 s. |
| 9. Hygiene | **PROVEN** for controlled suite | Git showed only expected paths: Tests 2/8 one HTML modification, Tests 5/6 one source modification, Test 7 three expected new files, Test 11 one result modification. Tests 1/3 stayed clean and Test 4 changed ignored state only. No escape or unexpected untracked project was observed. |
| 10. Fully local | **PROVEN** for provider path; **PLAUSIBLE** for exhaustive offline claim | All exercised LLM calls used `ollama-local/qwen3-coder:30b` at loopback; telemetry was hard-disabled; common cloud keys were absent; Ollama showed the model loaded locally. No packet capture was taken and DSH's cloud-backed `web_search` was not exercised. |
| 11. Interruption/resume | **PROVEN** | Stop produced an `aborted` turn and `tool call aborted`; no marker or tracked edit existed. Same-session resume ran verification once to completion, wrote the marker, and changed only `result.txt`. Durable turn reasons were `completed, aborted, completed`. |
| 12. Router comparison | **PROVEN** | Four same-model comparisons were executed. See section 11. |

This is not a pass-rate benchmark: some tests target different failure classes, and Test 9/10 aggregate evidence from other runs. The decisive failures are false terminal success, non-termination after a valid effect, and silent semantic loss in a continued session.

## 5. Important tool-call traces

### False-success trace

```text
assistant/message: <function=write> ... </function> </tool_call>
tool/call count: 0
turn/end: completed
headless exit: 0
Host-observed effect: hello.txt absent
```

This matches the documented headless contract: it waits for quiescence and exits 0 when the persisted final `turn/end` reason is `completed` ([official CLI reference](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/apps/cli/reference/README.md#L28-L30)). The contract does not independently prove the requested effect.

### Strong small-edit trace

```text
glob **
read index.html
edit index.html: <title>Test App</title> -> <title>Hangman App</title>
turn/end: completed
Git: one file, one line changed
```

### Recovery trace

```text
glob -> read script -> read README
pwsh verify (intentional exit 17)
pwsh inspect state
pwsh verify (success)
pwsh inspect state/exit
pwsh verify again (unnecessary)
todo_write -> update_goal
```

### Build-fix trace

```text
glob -> read test -> read source -> pwsh failing test
several str_replace attempts using relative, root-relative, and absolute paths
full-file write of src/math.js
pwsh passing test -> read -> direct function check
todo/goal/job/agent/plan actions unrelated to the requested artifact
turn/end: completed
```

### Non-terminating narrow edit

```text
glob -> read index/app/styles -> edit index.html (correct)
update_goal complete
(job_list -> todo_write completed) repeated 21 cycles
last persisted event: partial text chunks
turn/end: absent
```

### Manual pre-evaluation DSH session supplied with the goal

The supplied session `78f41f1f-94e6-4fa2-8525-86fa0bcbf544` contained 3 turns, 27 steps, and 24 calls: 9 writes, 6 edits, 5 reads, 2 PowerShell calls, and 2 to-do writes. It first created `hangman.*`, later created a second `index.html` / `styles.css` / `game.js` application, and recreated `firework_config.js` and `firework_engine.js` at the workspace root after reading the existing `fireworks` folder. This independently corroborates the original observation of project recreation and duplicate implementation. It is supporting evidence, not one of the clean-suite results.

## 6. Failures observed

1. **Transport mismatch accepted as completion.** Qwen sometimes emitted XML-like tool syntax instead of native OpenAI tool calls. DSH preserved it as assistant text and still completed successfully.
2. **No task-effect authority.** Exit 0 and `completed` did not mean the requested file or mutation existed.
3. **Loop after completion.** A correct one-line effect did not stop Test 8; DSH issued 49 calls without a terminal event.
4. **Tool-catalog distraction.** Simple tasks could invoke `todo_write`, `update_goal`, `job_list`, `list_agents`, and `exit_plan_mode`, increasing state and failure surface without advancing the artifact.
5. **Path/edit thrashing.** Test 5 cycled through relative, root-relative, and absolute paths plus multiple editing tools before falling back to a full write.
6. **Semantic continuity loss.** Test 7 retained textual facts while breaking the behavior those facts represented.
7. **Incorrect final narration.** Test 4 reported exit code 1 instead of 17; Test 7 said all behavior was preserved when it was not; Test 5 described a single-line minimal change despite newline churn and 24 calls.
8. **Windows sandbox is partial by design.** Official DSH documentation says new sessions default to `workspace-write`, restricts mutations/processes to the workspace and temp roots, but does not confine reads, network, or process visibility ([official CLI reference](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/apps/cli/reference/README.md#L70-L76)). This is materially broader than Agentic Router's trusted-workspace execution authority.

No tested run escaped its disposable workspace or modified the Agentic Router checkout.

## 7. Recovery behavior observed

DSH recovery is real but inconsistent.

- **PROVEN:** it can consume a failed tool result, inspect state, choose a different next action, and continue.
- **PROVEN:** Web Stop aborts an active PowerShell tool and the same durable session can resume from filesystem state without duplicating the interrupted edit.
- **PROVEN:** the persisted session log is a useful audit substrate. Tool calls/results, steps, usage, turn reasons, and aborted state were recoverable from compressed JSONL.
- **FAILED:** deterministic native-tool mismatch in Tests 1 and 3 received no corrective retry. The model text was accepted as the final response.
- **FAILED:** recovery can become exploration rather than convergence. Test 5 used 24 calls for a one-operator bug; Test 8 did not terminate.

The runtime has mechanisms for repeated-tool reminders, goals, jobs, and subagents, but the observed behavior does not show a reliable general convergence policy. More mechanisms did not automatically produce better stopping behavior.

## 8. Context-retention findings

**PROVEN:** DSH Web preserved one session across nine turns, kept the selected local model, and retained enough state to perform later incremental edits. Its durable session log is the source for projected model history; the official architecture explicitly describes fork/resume/transcript/persistence as projections of that event stream ([official architecture](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/docs/architecture.md#L90-L94)).

**FAILED:** retention of text was not retention of invariants. At the end:

- HTML used `id="score-value"`, but CSS still selected `#counter`.
- Increment used `count += 2`, but celebration remained guarded by `count === 5`; starting at zero, normal increment could never trigger it.
- The assistant explicitly claimed both rename styling and celebration were preserved.

The first work turn also completed after a pseudo-tool call, so the next turn had to create the entire missing application while nominally being asked to change only the title. The session recovered operationally, but its change-boundary semantics were already compromised at turn 2.

## 9. Workspace-hygiene findings

The clean suite's filesystem hygiene was better than the supplied manual session:

- **PROVEN:** no scenario wrote outside its disposable workspace.
- **PROVEN:** Tests 2, 5, 6, 8, and 11 touched only the expected tracked file; Test 7 added only the three requested application files and preserved `assets/celebrate.js` byte-for-byte.
- **PROVEN:** Test 4 left tracked files clean and used only its ignored state file.
- **PROVEN:** Tests 1 and 3 left clean repos, although that cleanliness came from failure to act.
- **FAILED:** Test 5 converted a one-character correction into a whole-file write and removed the final newline.
- **FAILED:** the supplied manual session duplicated both the application and fireworks implementation.

DSH's sandbox helps contain effects, but containment alone does not enforce minimality, protect pre-existing user changes, bind a changed-file set to task scope, or prove every effect before completion.

## 10. Fully-local/offline findings

The tested coding path was local:

- DSH selected only provider `ollama-local` and model `qwen3-coder:30b`.
- Provider base URL was `http://127.0.0.1:11434/v1`.
- The required OpenAI-compatible API key value was a local placeholder ignored by Ollama.
- `DSH_TELEMETRY_DISABLED=1` was applied to every DSH process.
- `DEEPSEEK_API_KEY`, `ANTHROPIC_API_KEY`, and `OPENAI_API_KEY` were absent in the inspection shell.
- Ollama reported the exact model loaded locally at 32,768 context tokens.

Ollama documents tool support on its OpenAI-compatible chat-completions endpoint and notes that an API key may be required by clients but is ignored locally ([Ollama OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)).

**Proof boundary:** no packet capture or deny-all network firewall was used, so “no external packet was emitted” is not proven. DSH's built-in `web_search` is not a fully local capability and was not exercised. The provider configuration and recorded response source prove the LLM/file/terminal path used local Ollama; they do not prove every optional DSH capability is offline-safe.

## 11. Comparison with current Agentic Router

Both systems used the exact same explicitly selected `qwen3-coder:30b`. Router ran from the current Release build with isolated data and workspace profiles. Cloud providers and Web search were disabled.

| Identical task | DSH | Current Agentic Router |
|---|---|---|
| Minimal `hello.txt` | **FAILED:** four false completions, zero calls/effect. | **PROVEN:** completed in 3.5 s; Host reported `created: hello.txt`; independent check found exactly 11 bytes and no other file. |
| One-line title edit | **PROVEN:** 3 calls, 5.2 s, exact one-line diff. | **PROVEN:** 5.8 s, Host reported only `index.html`; exact same one-line diff. |
| Existing fireworks reuse | **FAILED:** pseudo-`glob`, zero calls/effect. | **PROVEN with caveat:** 5.3 s, only `app.js` changed, existing engine imported. It unnecessarily overrode the existing default configuration and removed the file's final newline. Host skipped a redundant post-effect read proposal. |
| Build/test bug | **PROVEN artifact, poor loop:** 24 calls, full-file rewrite, independent test passed, DSH reported completed. | **PROVEN artifact, canceled session:** 17 Host actions in 1 min 20 s before cancellation; one file changed and independent test passed. Several approved validations had already passed, but Qwen kept proposing another test. Router preserved the change and reported `Cancelado`. |

The build case exposes a model-level weakness shared by both harnesses: Qwen repeats verification. DSH's larger general tool surface added unrelated calls; Router's process approval surface added friction. Neither path is satisfactory.

The authority distinction is nevertheless clear:

- Router's terminal answer came from Host facts, not model prose.
- Router independently registered created/modified files.
- Every process remained behind immutable explicit approval despite automatic approval mode.
- Router skipped a redundant post-effect action in the asset test.
- Router exposed a bounded recovery checkpoint in the first sandbox-affected run and a real cancellation state in the fair run.
- DSH returned success for missing effects and had no terminal state in its longest simple loop.

Historical Router evidence remains mixed: deterministic fake-provider E2E passed previously, while the earlier real Qwen fork-game acceptance remained semantically incomplete. This comparison therefore does not establish Router's current loop as generally accepted; it establishes that replacing its Host authority with the evaluated DSH runtime would be a regression.

## 12. Candidate Agentic Router components DSH could replace

These are candidates, not approved changes:

| Candidate | Evidence level | Condition |
|---|---|---|
| Specialist reasoning/tool-use loop | **PLAUSIBLE** | Only after native-call conformance, bounded stopping, and effect-aware terminal truth pass a larger matrix. |
| Prompt and tool-schema assembly | **PLAUSIBLE** | Router must still control offered capabilities and remove unrelated DSH tools. |
| Durable assistant/tool transcript | **PROVEN capability** | DSH's compressed JSONL trace and Web resume worked; retention/redaction and schema stability still need contract review. |
| Cancellation and cold/same-session resume mechanics | **PROVEN capability** | Process-tree cancellation and restart recovery need broader Windows coverage. |
| Generic file/process tool adapters | **PLAUSIBLE** | Only behind Router-owned path, process, approval, and effect validation; not as direct DSH authority. |
| Optional external-editor runtime via ACP | **SPECULATIVE** | Requires a separate transport/conformance experiment. |

DSH should not replace Router routing, model/provider settings, specialist selection, or product UI. Those are different product responsibilities.

## 13. Components that should remain inside Agentic Router

The following are product authority, not generic harness plumbing:

- trusted-workspace profile resolution and canonical path confinement;
- exact tool alias/canonical-name registry and phase availability;
- structured action validation, process allowlist, and approval binding;
- independent effect proof and changed-file review before completion;
- stale-write/conflict protection and preservation of pre-existing user changes;
- Host-owned plan/action/session identities and bounded recovery decisions;
- terminal status generated from Host facts;
- safe Git staging/commit/tag/push approvals and exact staged-set validation;
- provider routing, fallback rules, local/cloud secret handling, usage accounting, and incident facts;
- browser-facing review, approval, cancellation, undo, and diagnostics contracts.

Agentic Router may eventually stop owning a generic model iteration engine. It cannot stop owning the effect boundary. A DSH process that directly edits the trusted workspace and then supplies an opaque “completed” result would bypass the product's central security and correctness contract.

## 14. DSH integration risks

1. **Unstable API surface:** the evaluated release is explicitly a developer preview with breaking changes expected.
2. **False terminal success:** the highest-risk observed defect; it breaks any opaque provider integration.
3. **Dual authority:** letting both DSH and Router execute tools would create conflicting approvals, paths, retries, and terminal states.
4. **Broad default tool surface:** goals, jobs, agents, workflows, and search add irrelevant states for constrained Execute tasks.
5. **Sandbox mismatch:** DSH confines writes/processes but not reads, network, or process visibility; Router requires a narrower trusted-workspace contract.
6. **Trace schema coupling:** durable JSONL is valuable but internal event schemas are not yet a versioned Router integration contract.
7. **Windows behavior:** DSH documents partial Windows enforcement, and the evaluation required careful separation from an enclosing ACL sandbox.
8. **Context configuration ambiguity:** DSH declared 262k while the observed Ollama runner loaded 32k. Ollama's OpenAI compatibility endpoint does not expose all native runtime controls used by Router.
9. **Loop and cost control:** Test 8 had no terminal event; Test 5 used many irrelevant calls. A sidecar needs hard outer budgets and cancellation owned by Router.
10. **Semantic drift:** session memory retained statements but did not revalidate cross-file invariants.

## 15. ACP/MCP observations

DSH's architecture already includes ACP and MCP-related packages. ACP is the more relevant possible boundary because it is designed for external agent/editor interaction and session lifecycle; MCP is primarily a capability/tool connection mechanism.

- **PROVEN from source inspection:** DSH composes model adapters, tools, persistence, sandbox, approval policy, settings, credentials, and telemetry in its base bundle; Web and one-shot headless are additional profiles ([official architecture](https://github.com/deepseek-ai/deepseek-harness/blob/47f943859bef60e4160492346772ded9b24f765a/docs/architecture.md#L19-L25)).
- **PROVEN from source inspection:** MCP configuration supports stdio and streamable HTTP clients; it is not required for the tested native file/process loop.
- **NOT TESTED:** ACP conformance, MCP server/client interoperability, disconnect recovery, authentication, or Router tool delegation.
- **ARCHITECTURAL JUDGMENT:** adding MCP would not solve the authority problem. It could transport calls, but Router would still need to validate and prove every effect.
- **ARCHITECTURAL JUDGMENT:** ACP may be worth a narrow future experiment as a DSH session/control transport. It should not be adopted until its real published entry path, cancellation, versioning, and tool delegation are tested on Windows.

No ACP or MCP integration was implemented in this goal.

## 16. Recommended next architecture

Keep the current Router architecture unchanged for production. If evaluation continues, treat DSH as an out-of-process **experimental specialist runtime**, not Core and not an execution authority.

The smallest safe boundary is:

```text
Agentic Router
  owns routing, workspace, policy, approvals, budgets, effects, review, terminal truth
        |
        | bounded session request / typed tool intent / authoritative tool result
        v
DSH experimental runtime
  owns specialist reasoning, context projection, iteration, optional resume
```

For the experiment, DSH's direct mutation/process tools should be disabled or replaced by a narrow bridge that returns typed proposals to Router. Router executes approved actions and returns authoritative results. DSH's final prose remains advisory; Router determines completion from proven effects.

Do not build a compatibility abstraction before confirming that the evaluated ACP or another stable DSH entry point can support this topology without patching DSH internals. If DSH can only operate as an opaque workspace-writing process, it is not a safe replacement for Execute Core.

Classification of work:

- **Necessary now:** preserve Router Host authority; record these failures; do not integrate the evaluated release.
- **Useful later:** a small transport/conformance spike on a disposable branch and workspace.
- **Probably unnecessary:** MCP, recursive subagents, DSH workflow/goal systems, or a second generic approval system inside Router.

## 17. Required follow-up experiments

1. Pin a newer DSH release and rerun Tests 1, 3, 7, and 8 first. Stop if false completion or non-termination reproduces.
2. Test a minimal DSH profile exposing only `read`, `glob`, `edit/write`, and one constrained process tool. Compare against the 49-call loop and 24-call build trace.
3. Determine whether DSH can reject assistant text that resembles a tool call when native calls are required, and surface a nonzero typed protocol failure.
4. Add an external effect oracle for headless runs: requested file/hash/diff/test postconditions must control exit status independently of model turn reason.
5. Run an ACP published-entry conformance spike: create, tool round-trip, cancel active process tree, resume, crash/restart, and schema/version negotiation.
6. Prove Router-owned tool delegation. DSH must not directly mutate the workspace in this experiment.
7. Repeat the eight-turn continuity test with executable browser assertions after every turn, including cross-file selector checks and reachability of celebration after step-size changes.
8. Measure context and token growth across 20+ turns. The nine-turn session consumed roughly 386k cumulative input tokens while showing only 6% current-context usage; distinguish per-call cumulative cost from active context.
9. Run deny-all-except-loopback network instrumentation to upgrade the fully-local claim from plausible to proven, including Web UI and optional tool discovery.
10. Repeat cancellation against nested child processes and process trees on Windows, then restart DSH and cold-resume the same session.
11. Compare at least one non-Qwen native-tool model only after Qwen passes the real acceptance gate; do not broaden model-family work to hide a Qwen transport failure.

## 18. Final recommendation

DSH demonstrates enough real capability to justify more focused experiments: local Ollama worked, native tools sometimes worked well, durable traces were useful, and Web interruption/resume was strong. It does not yet satisfy the acceptance bar for owning Agentic Router's execution loop. False success without effects, a non-terminating simple edit, duplicated manual-project output, and silent multi-turn invariant loss are disqualifying for production adoption. The official developer-preview status and broader sandbox authority amplify those observed risks.

The next gate should be narrow and falsifiable: a newer pinned DSH version must pass native-call conformance, effect-aware exit status, bounded stopping, and Router-owned tool delegation on Windows before architecture work resumes.

CONTINUE EVALUATION
