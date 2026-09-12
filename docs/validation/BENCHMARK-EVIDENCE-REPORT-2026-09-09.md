# Benchmark Lab evidence report — 2026-09-09

## Status and scope

This is a read-only evidence snapshot captured while the next sequential benchmark run was still active. No benchmark, Agentic Router, Ollama, model runner, or harness process was stopped or restarted during collection.

The quantitative analysis covers the four latest completed runs. They use the same configuration fingerprint, Host commit, Ollama version, model set, harness set, suites, and timeout configuration. The live section records observations from the following run while it was executing `gemma4:31b`.

Capture window: 2026-09-09 19:58–20:07 America/Sao_Paulo.

## Conclusion

The evidence supports multiple independent causes. The strongest is a Host context-role mismatch: the benchmark result records a configured benchmark context of 32,768 tokens, but production Execute treats the explicit model as the `primary` role and records 131,072 as the effective context for every benchmark execution path. During the observed Native segment, this made `gemma4:31b` partially offload to CPU. When the runner was later loaded at 32,768, it fit fully in VRAM and the RTX 4090 reached 100% utilization.

`gemma4:31b × Native` is not mainly a random pass/fail problem. Across all four completed repetitions it timed out in the same eight scenarios, passed the same three, and failed `FS-READ-001`. This stable pattern points to a repeatable Native/Host execution problem amplified by the oversized context.

There are also genuine cross-run variations: 33 of 180 model × harness × test keys changed outcome across the four repetitions. Several of those changes correlate with Host/harness contract failures rather than model quality alone, especially phase-specific tool rejection, benchmark-owned approval rejection, and Qwen Code loop protection.

## Addendum — combined Vulkan and 256k context, 2026-09-10

Capture window: 2026-09-10 19:23–19:33 America/Sao_Paulo. The sequential benchmark `2f543bc71c3e416d835fc7b0c94a2bd8` remained active and was not cancelled or restarted during collection.

The runtime status and live benchmark evidence exposed a second context-control gap after the benchmark-role propagation fix:

- The active benchmark recorded 32,768 requested context tokens for `gemma4:31b × Qwen Code`.
- Ollama `/api/ps`, projected by `/api/runtime/status`, reported one `gemma4:31b` runner with 262,144 allocated context tokens.
- Ollama reported `size == size_vram == 22,053,713,345` bytes and zero estimated model RAM, so this observation does not support CPU layer offload.
- The managed backend was Vulkan with `vulkan:prefer:cuda:0`; Ollama did not expose the physical split across the RTX 4090 and RX 7900 XTX.
- Windows independently reported adapter-wide dedicated-memory use. Those physical counters include the model, other processes, and driver allocations and cannot be added to Ollama's 20.5 GiB model allocation.

The root cause is the OpenAI-compatible provider path used by external harnesses. Agentic Router wrote `generationConfig.contextWindowSize`, which constrained Qwen Code's client-side context budget, but the OpenAI API has no request field for Ollama `num_ctx`. On this machine the two 24 GiB-class adapters expose at least 48 GiB aggregate VRAM, for which current Ollama defaults to a 256k context allocation. The runner therefore used 262,144 even though the Host benchmark budget was 32,768.

The corrective implementation now:

1. starts the managed Ollama process with `OLLAMA_CONTEXT_LENGTH` equal to the Host-resolved effective context;
2. treats GPU selection plus explicit context length as the managed process configuration;
3. retains the configured port, stops only the verified Ollama process that owns it, and replaces that process when the effective context changes;
4. records the managed server context in runtime status so requested-versus-allocated comparisons use the active process configuration;
5. labels Windows values as adapter-wide VRAM, labels Ollama values as model allocation, and identifies combined Vulkan as one aggregate allocation with an unreported physical split;
6. labels 262,144 as an allocated context window rather than live token occupancy or a context share.

Deterministic validation after the change:

- Release build: zero warnings and zero errors.
- Managed Ollama E2E: the same port remained in use, `OLLAMA_CONTEXT_LENGTH=32768` was observed, the verified prior PID exited when changed to 65,536, and only one managed server remained.
- Runtime/Qwen/benchmark focused E2E: six passed; one initial infrastructure invocation used an incomplete copied fake-Qwen output and passed after using the complete isolated fake-server directory.
- JavaScript syntax, formatting, and intended-diff checks passed.

## Controlled dataset

All four runs used:

- Git commit: `c8310204a1943dc46e3536de5c6b503b42e33b91`
- Ollama: `0.33.3`
- Configuration fingerprint: `f3d66ca31498...`
- Models: `gemma4:26b-a4b-it-qat`, `gemma4:31b`, `qwen3.8:27b-gpu0`
- Harnesses: Native, Claude Code, Codex, OpenCode, Qwen Code
- Suites: Basic CRUD v1, Agent Behavior v2, Real Life Problem v1
- Matrix size: 15 cells × 12 tests = 180 test executions per run
- Requested suite timeout: 600 seconds; Agent Behavior scenarios retain their smaller metadata-specific limits of 75–150 seconds.

| Run | Local time | Duration | PASS | FAIL | ERROR | TIMEOUT | Completed cells | Failed cells | Timed-out cells |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `6189f0ceb4b9476ebc3383125cd0497e` | 08:41–10:50 | 2.15 h | 138 | 28 | 6 | 8 | 11 | 3 | 1 |
| `d9d2b854e050458abb326d68f341de8b` | 10:50–12:58 | 2.15 h | 133 | 34 | 4 | 9 | 11 | 2 | 2 |
| `2f15a3280b54424bb48578cf1f6fea21` | 12:58–15:15 | 2.27 h | 134 | 30 | 5 | 11 | 10 | 1 | 4 |
| `27e839f1e6f54373b307f97be91670e1` | 15:15–18:09 | 2.91 h | 129 | 29 | 8 | 14 | 10 | 1 | 4 |
| **Total** | | **9.48 h** | **534** | **121** | **23** | **42** | **42** | **7** | **11** |

Runtime growth tracks timeout growth: the last run took 45 minutes longer than the first while timeouts increased from 8 to 14.

### Immutable source files

| Result file | SHA-256 |
|---|---|
| `AgenticRouter.Api/data/benchmark-results/6189f0ceb4b9476ebc3383125cd0497e.json` | `2E6E78AFD6E28DC004AB57892EE64AE13AD190FC4ADC2603CCB83441C72F96D6` |
| `AgenticRouter.Api/data/benchmark-results/d9d2b854e050458abb326d68f341de8b.json` | `1EC2BFD03584FF65A7CD19C4D12CB9A58186CF87F451ACF5CC8414F4C24325EB` |
| `AgenticRouter.Api/data/benchmark-results/2f15a3280b54424bb48578cf1f6fea21.json` | `DF11DCD77E32D7E638F53891A0D0FE1357FEA08F34ACA72AE91C8030BD44A14A` |
| `AgenticRouter.Api/data/benchmark-results/27e839f1e6f54373b307f97be91670e1.json` | `725B403CBCE3988C230384A8EB758DF3D5D271DA063867ADE299A4728ACBBE8F` |

The active incident journal is append-only and was still changing, so this report does not assign it a final hash.

## Finding 1 — benchmark requests use the primary context role

Confidence: **confirmed from code, incident journal, runtime status, and Ollama**.

The saved settings distinguish the profiles:

- Primary target and maximum: 131,072 tokens (`AgenticRouter.Api/data/settings.json:83-84`).
- Benchmark target: 32,768; maximum: 40,960 (`AgenticRouter.Api/data/settings.json:102-105`).

The benchmark engine records 32,768 as the benchmark configuration (`BenchmarkEngine.cs:369-371`), but `BenchmarkProductionExecuteRunner` creates a normal explicit-model `ChatRequest` with no benchmark-role marker (`BenchmarkProductionExecuteRunner.cs:80-90`). `ChatStreamService` initializes every explicit selection as `UsageModelRoles.Primary` (`ChatStreamService.cs:306`) and does not replace that role for requests carrying the benchmark execution-scope header.

Incident aggregation for `gemma4:31b` found 270 completed benchmark turns, all reporting an effective context of 131,072:

| Execution path | Completed turns at 131,072 |
|---|---:|
| Claude Code | 72 |
| Codex | 60 |
| Qwen Code | 58 |
| OpenCode | 56 |
| Native | 24 |

This is a contract inconsistency: persisted benchmark metadata says 32,768 while Host request evidence says 131,072. For external harnesses, the actual Ollama runner can still be loaded at 32,768, so the Host's `contextFit.effectiveContextTokens` is also not reliable evidence of the backend's actual context.

## Finding 2 — oversized Native context explains the CPU offload

Confidence: **confirmed for the observed interval; causality is strongly supported by the same runner fitting after context reduction**.

At 19:59, during the observed `gemma4:31b × Native` portion:

| Measurement | Value |
|---|---:|
| Effective/actual context | 131,072 |
| Ollama processor split | 24% CPU / 76% GPU |
| Total model allocation | 22,727,926,740 bytes |
| VRAM allocation | 17,367,081,942 bytes |
| Estimated system RAM allocation | 5,360,844,798 bytes |
| RTX 4090 dedicated memory | 24,188,551,168 / 25,757,220,864 bytes (93.9%) |
| RX 7900 XTX dedicated memory | 4,647,751,680 / 25,707,913,216 bytes (18.1%, no model placement observed) |
| Backend and device | managed CUDA, RTX 4090, `ollama:0` |

The active selection was CUDA on the RTX 4090. The RX 7900 XTX was not part of this model placement, so its low utilization is expected for this cell.

At 20:05 the same model was loaded at 32,768:

| Measurement | Value |
|---|---:|
| Actual context | 32,768 |
| Ollama processor split | 100% GPU |
| Total model/VRAM allocation | 19,874,751,445 bytes |
| Estimated system RAM allocation | 0 bytes |
| RTX 4090 utilization, 10 samples | min 0%, average 75.3%, max 100% |
| RTX 4090 memory utilization, 10 samples | average 61.3% |
| RTX 4090 power, 10 samples | average 281.8 W |

The GPU was not intrinsically underused. Utilization alternated between short idle boundaries and 74–100% compute while fully offloaded. CPU pressure in the Native observation is consistent with the 5.0 GiB partial offload caused by the 131,072 context allocation.

## Finding 3 — `gemma4:31b × Native` is a stable systemic failure

Confidence: **confirmed across four independent repetitions**.

Aggregate result:

- 48 test executions: 12 PASS, 4 FAIL, 32 TIMEOUT.
- Pass rate: 25.0%.
- Cell duration: 28.3–28.8 minutes; average 28.5 minutes.
- Cell status: timeout in 4/4 runs.
- Score range: 54.97–60.69.

The outcome is identical in every repetition:

| Test | Outcome in all four runs | Limit |
|---|---|---:|
| `FS-CREATE-001` | PASS | — |
| `FS-READ-001` | FAIL | — |
| `FS-UPDATE-001` | PASS | — |
| `FS-DELETE-001` | PASS | — |
| `CONTINUITY-001` | TIMEOUT | 150 s |
| `SCOPE-RETENTION-001` | TIMEOUT | 90 s |
| `RECOVERY-001` | TIMEOUT | 120 s |
| `CONVERGENCE-001` | TIMEOUT | 90 s |
| `TERMINALITY-001` | TIMEOUT | 75 s |
| `STALE-CONFLICT-001` | TIMEOUT | 120 s |
| `TRUTHFUL-REPORT-001` | TIMEOUT | 90 s |
| `MISSING-GAME-001` | TIMEOUT | 600 s |

Seven of the eight timed-out tests contain partial token/tool evidence but no successful terminal report; `CONTINUITY-001` has no collected usage or terminal reason. The deterministic boundary-aligned durations show that the Host timeout is firing as configured. They do not show whether the underlying cause is slow inference, a Native tool loop, a stream that never terminates, or a combination. Per-cell context and throughput evidence is currently missing from persisted results, which prevents a definitive split.

`gemma4:31b` performs much better through other harnesses under the same benchmark matrix:

| Harness | PASS rate | Timeouts | Average cell score | Average cell duration |
|---|---:|---:|---:|---:|
| Native | 25.0% | 32 | 57.83 | 28.5 min |
| Claude Code | 66.7% | 0 | 89.90 | 7.9 min |
| Codex | 91.7% | 0 | 93.18 | 7.1 min |
| OpenCode | 83.3% | 4 | 85.64 | 9.5 min |
| Qwen Code | 95.8% | 0 | 93.52 | 12.1 min |

That isolates the worst behavior to Native/Host execution rather than to the model alone.

## Finding 4 — 18.3% of test keys vary across repetitions

Confidence: **confirmed from immutable results**.

There are 180 unique model × harness × test keys. Thirty-three changed outcome between PASS, FAIL, ERROR, or TIMEOUT across the four runs.

| Model × harness | Unstable keys out of 12 |
|---|---:|
| `gemma4:26b-a4b-it-qat × Qwen Code` | 8 |
| `gemma4:26b-a4b-it-qat × OpenCode` | 6 |
| `gemma4:26b-a4b-it-qat × Native` | 5 |
| `gemma4:31b × OpenCode` | 5 |
| `qwen3.8:27b-gpu0 × Codex` | 4 |
| Five other pairs | 1 each |

The unstable set is concentrated rather than uniform. `gemma4:31b × Native` is absent because its failures are stable.

Across all 720 test executions:

| Outcome | Count | Rate |
|---|---:|---:|
| PASS | 534 | 74.2% |
| FAIL | 121 | 16.8% |
| ERROR | 23 | 3.2% |
| TIMEOUT | 42 | 5.8% |

The test-level distribution also shows concentration:

| Test | PASS | FAIL | ERROR | TIMEOUT |
|---|---:|---:|---:|---:|
| `MISSING-GAME-001` | 21 | 30 | 0 | 9 |
| `CONVERGENCE-001` | 30 | 19 | 6 | 5 |
| `TRUTHFUL-REPORT-001` | 35 | 20 | 0 | 5 |
| `RECOVERY-001` | 36 | 17 | 3 | 4 |
| `TERMINALITY-001` | 43 | 8 | 4 | 5 |
| `STALE-CONFLICT-001` | 45 | 10 | 0 | 5 |
| `FS-READ-001` | 46 | 14 | 0 | 0 |
| `CONTINUITY-001` | 51 | 2 | 2 | 5 |
| `SCOPE-RETENTION-001` | 52 | 1 | 3 | 4 |
| `FS-UPDATE-001` | 55 | 0 | 5 | 0 |
| `FS-CREATE-001` | 60 | 0 | 0 | 0 |
| `FS-DELETE-001` | 60 | 0 | 0 | 0 |

Basic create/delete being 60/60 shows that the underlying execution path is not universally broken. Variance and failure cluster around multi-step behavior, truthful reporting, recovery, convergence, and the long real-life scenario.

## Finding 5 — the benchmark runner changes outcomes through approval decisions

Confidence: **confirmed from code, historical tool evidence, and the active journal**.

`BenchmarkProductionExecuteRunner` sends `ApprovalPolicy: "auto"`, but its stream consumer approves only safe `delete_paths` actions and explicitly rejects every other action that still reaches `awaiting-approval` (`BenchmarkProductionExecuteRunner.cs:147-164`, `219-236`).

The completed runs contain 18 `run_process` actions rejected with `APPROVAL_REJECTED`, all in `qwen3.8:27b-gpu0 × MISSING-GAME-001`:

- Qwen Code: 8 raw evidence events.
- Claude Code: 6.
- Codex: 3.
- OpenCode: 1.

The active run reproduced it at 20:05:26 for `gemma4:31b × Claude Code`: the incident journal records `run_process` entering `action.awaiting-approval` and being rejected one millisecond later (`incidents-20260909-009.jsonl:1888-1889`). The request later completed, so a rejected validation command does not always fail the objective, but it changes the evidence and can affect pass/fail behavior depending on how the harness recovers.

Changing this behavior is an approval-policy decision and should be implemented only after defining which benchmark processes are permitted automatically. The report does not change it.

## Finding 6 — tool-phase and adapter failures contaminate quality scores

Confidence: **confirmed symptoms; precise root causes require focused replay**.

The four runs include these non-model-quality failures:

- 19 `not-evaluated` errors, all in `gemma4:26b-a4b-it-qat × Native`. The model proposed canonical mutation tools such as `replace_text`, `write_file`, `create_file`, `create_files`, or `apply_patch`, but the Host said the tool was not offered in the current execution phase. Two cases were malformed local action proposals.
- 9 `qwen-code-turn-error` results caused by Qwen Code's tool-call loop protection. They span both Gemma models and multiple Agent Behavior/Real Life tests.
- 18 approval-rejected process evidence events described above.
- Raw tool-event counts contain both native-wrapper and canonical Host projections. They must be deduplicated by action identity before being treated as failure counts.

These are useful benchmark outcomes for system reliability, but they should be reported separately from semantic task quality. A single combined score currently makes it difficult to distinguish model inability, adapter protocol failure, Host capability projection, approval behavior, and backend performance.

## Evidence gaps

The current persisted schema does not retain enough per-turn runtime evidence to prove every causal step:

- actual Ollama context per cell and per inference;
- model load duration versus prompt evaluation versus token generation;
- prompt and generation tokens per second;
- CPU/GPU utilization samples tied to run, cell, test, and inference sequence;
- layer offload count and backend runner identity per inference;
- requested model role beside actual backend context;
- exact capability/tool set offered in each execution phase;
- unique action identity across wrapper and Host tool projections;
- the active suite run ID in a read-only live-run listing endpoint.

The browser-control transport was unavailable during collection, so the active run ID stored in browser `sessionStorage` could not be read. The active workspaces, incident traces, managed Ollama process, and model/harness progression were still observable without modifying the run.

## Recommended work

### Necessary now

1. Propagate an internal benchmark execution marker/model role from `BenchmarkProductionExecuteRunner` through the loopback request and make every Host-owned Ollama call resolve `UsageModelRoles.Benchmark`. Assert that the effective context stays within the configured 40,960 maximum.
2. Add a real-path E2E that observes the Native benchmark request at the Ollama boundary and proves `num_ctx=32768`, rather than checking only the persisted configuration field.
3. Decide the benchmark process-approval contract. Under the selected rule, make process validation deterministic across harnesses and record whether the command was permitted, rejected by policy, or unavailable.
4. Record per-cell context, backend, offloaded bytes/layers, inference timing, and device samples so GPU/CPU diagnoses survive after the run.

### Useful next

1. Split reported outcomes into semantic validation, Host policy/capability, harness protocol, provider/runtime, and timeout categories before scoring.
2. Add focused replay fixtures for the 19 Native phase/tool errors and the 9 Qwen Code loop-protection errors.
3. Record generation controls, including temperature and seed availability, then compare deterministic and stochastic repetitions explicitly.
4. Add a read-only endpoint listing retained live benchmark runs with run ID, current model/harness/test, last event time, and terminal state.
5. Track timeout phase and last meaningful progress so a slow model can be distinguished from a dead tool loop or stalled stream.

### Probably unnecessary before those fixes

- Increasing timeouts globally. The same eight `gemma4:31b × Native` scenarios already consume their exact limits in all four runs; a larger limit would mainly extend a repeatable failure and hide the context/termination issue.
- Treating all pass/fail variation as model randomness. A material portion has explicit Host/harness error evidence.
- Using Task Manager utilization alone as placement proof. Ollama allocation, managed-runner identity, actual context, and backend/device evidence are stronger.

## Reproduction checkpoints for the next investigation

After the active batch finishes:

1. Run only `gemma4:31b × Native` with the same three suites.
2. Capture the first Ollama request options and verify model role, `num_ctx`, backend, and offload before evaluating quality.
3. Stop after the first Agent Behavior timeout and inspect the last stream event, active tool set, pending action, and provider generation state.
4. Repeat once with the context-role fix while keeping model digest, prompt, suites, harness, GPU, and timeouts unchanged.
5. Compare per-test terminal state and duration against the four-run baseline in this report; do not average away individual repetitions.

## Remediation implemented after evidence collection

The source tree now contains the first remediation pass described above. It has not been loaded into the long-running Router process while the fifth benchmark batch remains active.

- Benchmark context is an explicit run input in the Benchmark Lab. The catalog exposes the benchmark default, lower bound, provider upper bound, and configured context presets. The server rejects values outside those bounds or above any selected model's declared limit.
- The server freezes `DefaultGpu` and the selected context at run start, carries both through the internal benchmark scope, uses the benchmark model role for every Host-owned Ollama request, exposes the exact context to external harnesses, and includes context, GPU, and model role in the immutable configuration fingerprint.
- Schema version 5 persists requested and observed context, configured GPU/backend, model/VRAM/estimated RAM bytes, processor/offload percentage, a system-RAM and per-device VRAM sample, Ollama load/prompt/generation timing, prompt/output tokens per second, failure category, timeout phase, and last meaningful progress for each test inside a matrix cell.
- Benchmark auto approval now approves every action that has already passed Host validation and reached `awaiting-approval` inside the secret loopback benchmark scope. Trusted-workspace and hard security boundaries remain Host-enforced.
- Outcomes are categorized as semantic validation, Host policy/capability, harness protocol, provider/runtime, timeout, cancellation, preparation, or unknown. The category is displayed beside the measured evidence.
- A read-only live-run listing endpoint exposes retained run IDs plus current model, harness, test, last update, and terminal state. The browser uses it to recover an active run when `sessionStorage` is missing.

Isolated validation on 2026-09-09:

- Release build: zero warnings and zero errors.
- JavaScript syntax validation: passed.
- Provider-boundary E2E: passed; `40960` was observed in every Native fake-Ollama request and in `/api/ps` runtime evidence, with persisted GPU selection, offload bytes, and calculated throughput.
- Benchmark browser E2E: passed; context and Default GPU were visible, context was editable before execution, and the control was disabled during execution.
- Timeout evidence E2E: passed; category, phase, and last meaningful progress survived into the persisted result.

CPU/GPU utilization sampling over the full inference interval is still absent. The implemented evidence records allocation/offload and Ollama inference timing without inserting a polling workload that could perturb benchmark results. A later sampler should run at a defined cadence and record its own measurement overhead before utilization is used for comparison or scoring.

At 21:33 local time the pre-change fifth batch was still active. The latest persisted result remained the fourth run (`27e839f1e6f54373b307f97be91670e1`, ended 18:09:53, schema 4), the incident journal was still growing, and `/api/ps` showed `qwen3.8:27b-gpu0` loaded with context `131072`. The running Router and Ollama processes were deliberately left untouched so this historical run can finish without mixing old and new binaries.
