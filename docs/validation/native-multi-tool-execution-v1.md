# Native multi-tool execution V1 validation

Validation date: 2026-10-15/16, Windows, baseline `765fc23`.

## Deterministic browser/API validation

Phase A was implemented and validated before introducing concurrent execution:
six E2E cases passed, with a Release build reporting zero warnings/errors.
Evidence: `TestResults/native-multitool/native-multitool-phase-a.trx`.

The final relevant regression run passed **57/57**, zero failures/skips, in
1 minute 45 seconds. It covers all Off/Auto/On modes, complete ordered call/result
correlation, invalid/malformed siblings, duplicate IDs, mixed read/write barriers,
workspace escape rejection, independent mutation approvals, cancellation, settings
and portable YAML, checkpoint restoration, one-call compatibility, read-output
budgets, action introductions, completion metrics, supervisor recovery and scope.

The overlap test performs real filesystem I/O on two 64 MiB files behind the
fake external provider and observes **peak concurrency 2 with limit 2**. It also
checks that checkpoint metadata does not contain read file payloads. This proves
Host runtime overlap; it is separate from real-model performance acceptance.

Commands executed:

```powershell
dotnet build -c Release --no-restore --disable-build-servers -m:1 -v minimal
$changedCsharp = @(
  git diff --name-only -- '*.cs'
  git ls-files --others --exclude-standard -- '*.cs'
)
dotnet format AgenticRouter.slnx --no-restore --include $changedCsharp
dotnet format AgenticRouter.slnx --no-restore --include $changedCsharp --verify-no-changes
$patterns = @(
  'NativeMultiToolEndToEndTests'
  'MultipleNativeToolCallsExecuteEveryCallInOrder'
  'NativeExecuteRejectsTheFirstActionUntilTheSpecialistProvidesAnIntroduction'
  'QwenTooling'
  'RejectedActionDoesNotWriteFile'
  'DirectApprovalRetainsItsAuthorityAfterBrowserReload'
  'CorrectAtomicWorkConvergesOnFirstWorkerAttempt'
  'ReadOnlySupervisorRolesIgnoreWorkerMutationCompletionGate'
  'AutonomousCannotBypassTrustedWorkspaceBoundary'
  'AutoTakesOverAcceptedPlanAboveConfiguredDirectLimit'
  'CompletionFooterAggregatesInferenceAndPreservesItInHistory'
  'FileReadPolicyEndToEndTests'
)
$filter = ($patterns | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|'
dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-build --settings playwright.runsettings --filter $filter --logger 'trx;LogFileName=native-multitool-final.trx' --results-directory TestResults/native-multitool --disable-build-servers
git diff --check
```

The patterns above reproduce the selected 57-case filter; individual test names
and run metadata are retained in the TRX. Full command output
is in `TestResults/native-multitool/final-tests.log`; build and formatter logs are
in the same directory. The Release build passed with **zero warnings/errors**.
The full repository suite was not run. An obsolete expected tool inventory in the
existing Qwen fixtures was corrected to include the already-offered `read_files`.

An additional **6/6** external-boundary E2E cases passed in 23 seconds: cloud
streaming/tools/usage/rate limits; Claude Code, OpenCode, Qwen Code (two protocol
variants) and Codex workspace/model/continuity. Evidence:
`TestResults/native-multitool/native-multitool-harness-regression.trx`.
Together these are **63 deterministic E2E cases**, plus the opt-in real benchmark.

## Real-model benchmark protocol

User explicitly authorized installed Qwen 4B inference in an isolated Host.
Ollama server/CLI: **0.34.2**; model: **qwen3:4b-instruct**, Q4_K_M, 4.0B;
NVIDIA RTX 4090. No model was resident on the selected Ollama server before the
benchmark. Existing Ollama servers and the running user Host were preserved.

One installed model is warmed once. Three repetitions rotate A/B/C/D order.
Each browser turn uses fresh page state, Native, direct strategy, automatic
approval, no thinking, 16,384 context tokens and 2,048 output tokens. The task
reads copies of three real source files and reports each main public type and
one source behavior, without mutations or shell commands.

| Scenario | Requested tool strategy | Scheduler |
| --- | --- | --- |
| A | One `read_file` call per response | Off |
| B | Three `read_file` calls in one response | Off |
| C | Three `read_file` calls in one response | Auto, limit 4 |
| D | One existing `read_files` call | Off |

A uses the same new binary with a single-call instruction as the controlled
representation of previous behavior; it is **not a historical-binary benchmark**.
Actual tool choice/batch size, recoveries and quality must be considered, not
only the requested strategy. There are no simultaneous model inferences.

Quality requires the expected public types (either `ExecutionTurnToolScope` or
`ExecutionTurnToolPolicy` for Policy.cs) and Host evidence of three completed
`read_file` calls or one completed `read_files` call. Final answers are also
retained for inspection. This automated gate does not grade semantic accuracy of
every sentence. Manual review found occasional conflation of members of different
types in the same file, especially D2/D3 (for example attributing `Resolve` to
`ExecutionTurnToolScope`, whereas it belongs to `ExecutionTurnToolPolicy`).
All scenarios completed the reads, but the model's prose is not uniformly exact.
This is bounded task acceptance, not a general model quality evaluation.

The pilot exposed missing first-action introductions, premature model answers
after rejection, and an overly narrow benchmark type-name check. Pilot evidence
is retained separately under `TestResults/native-multitool/real-pilot`.
The benchmark prompt was corrected consistently for every scenario to request
the existing Native introduction contract and completion after all reads. No
product policy was relaxed to improve the benchmark.

Reproduction after explicit real-inference authorization:

```powershell
.\scripts\run-native-multitool-benchmark.ps1 -Repetitions 3
```

## Real-model results

The final benchmark passed its test-specific quality gate in **12/12 turns**,
with zero rejected calls and zero streamed errors. B and C actually emitted a
single three-call read batch; A emitted three one-call batches; D used one
`read_files` call. The table reports medians of three measurements.

| Metric | A single-call | B multi sequential | C multi Auto | D `read_files` |
| --- | ---: | ---: | ---: | ---: |
| Browser turn wall, ms | 4,285 | 3,752 | 3,735 | 4,272 |
| Wall range, ms | 3,754–4,887 | 3,725–3,753 | 3,721–3,755 | 4,263–4,302 |
| Inferences | 4 | 2 | 2 | 2 |
| Provider inference wall sum, ms | 3,194.690 | 2,872.829 | 2,888.507 | 3,392.527 |
| Prefill sum, ms | 943.706 | 810.383 | 876.428 | 1,231.702 |
| Decode sum, ms | 1,539.994 | 1,840.033 | 1,636.325 | 1,820.862 |
| Tool runtime sum, ms | 3 | 3 | 6 | 12 |
| Host batch wall sum, ms | 34.962 | 31.319 | 43.807 | 26.802 |
| Input tokens summed across inferences | 19,330 | 10,149 | 10,350 | 11,797 |
| Output tokens | 194 | 225 | 207 | 223 |
| Calls | 3 | 3 | 3 | 1 |
| Observed peak concurrency | 1 | 1 | 1 | 1 |
| Capacity wait, ms | 0 | 0 | 0 | 0 |
| First-inference TTFT after load, ms | 355.929 | 174.190 | 290.396 | 297.784 |
| Warm model load sum, ms | 24.273 | 9.428 | 12.552 | 10.496 |
| Read/type acceptance gate passed | 3/3 | 3/3 | 3/3 | 3/3 |

Provider inference time sums Ollama `total_duration`, including its load/prefill/
decode overhead; tool time sums individual observed runtimes. Batch wall includes
serial Host admission, validation and result coordination. Each row's median is
independent, so median components need not add up to the median total. TTFT is
the existing first-inference metric, not an average over every inference.

Observed B versus A: **50% fewer inferences**, **47.5% fewer input tokens**, and
**12.4% lower median browser wall time**. Output length/tokens varied with model
answers, and three samples are not a statistical performance guarantee.

There is **no demonstrated additional parallelism gain in this workload**:
the 17 ms B/C median difference is within observed variability, and C's peak was
1. These small cached source reads finished during serial Host preparation of
the next call. Auto must not delay fast reads just to manufacture overlap.
Actual concurrent runtime is independently established by the 64 MiB E2E test.

The existing batch tool is preserved and produced correct results. D was about
equal to A here (0.3% lower median wall) and 13.9% slower than B; it used more
input tokens/prefill than B. This is evidence against replacing every existing
batch tool or claiming one tool strategy is universally faster.

The earlier valid schema-request round had all 12 quality checks pass but added
one rejected schema request to each B/C turn, giving 3 rather than 2 inferences.
Its wall medians were A 3,763 ms, B 5,265 ms, C 4,819 ms and D 4,244 ms. It is
retained under `real-schema-request`: unnecessary discovery can erase the gain.
The final common prompt explicitly states that both read schemas are already
offered; exact final prompts and answers are included with the measurements.

Compact shareable evidence, with source hashes, each run's metrics, call states,
prompts and answers: [native-multi-tool-benchmark.json](native-multi-tool-benchmark.json).
Raw per-inference context/provider timing and batch metadata remain in
`TestResults/native-multitool/real/measurements.json`; the real test TRX is in
the same directory. The final Release test took 1 minute 4 seconds including
browser setup and the 12 measured turns.

Benchmark history was disabled and rollback was context-only. The existing
persistent-session path logged missing-session warnings for volatile conversation
IDs, including timeline/terminal saves; these were not streamed inference
failures. Durable checkpoint acceptance comes from the separate enabled-history
E2E coverage. This warning path is not treated as evidence of successful history
persistence or expanded into an unrelated persistence refactor here.

Cleanup verified: benchmark listener **56339 closed**, no matching benchmark
Host/test processes remained, and Qwen 4B was absent from Ollama's resident model
list. The original Ollama services (PIDs 19340 and 65184) remained alive.
`TestResults/native-multitool/real/cleanup.json` records the checks.

## Changed files and contract review

| Area | Files |
| --- | --- |
| Normalization and planning | `Execution/SpecialistToolingProfiles.cs`, `Execution/LocalActionPlanner.cs`, new `Execution/ToolExecutionBatch.cs` |
| Host orchestration | `Chat/ChatStreamService.cs`, new `Execution/NativeToolScheduler.cs`, `Execution/LocalActionService.cs`, `Execution/PlanningFailureClassifier.cs` |
| Checkpoints and journal | `Execution/ExecutionSessionStore.cs`, `Supervision/DurableSupervisionRunCoordinator.cs`, `Supervision/SupervisionContracts.cs`, `Controllers/ChatController.cs` |
| Public additive metadata | `Contracts/ApplicationContracts.cs`: optional Native batch review and typed stream telemetry |
| Provider parsing | `Providers/Ollama/OllamaClient.cs`, `Providers/Cloud/OpenAiCompatibleCloudProvider.cs` |
| Settings and UI | `Configuration/ApplicationSettings.cs`, `Configuration/SettingsValidator.cs`, `Configuration/PortableYamlSettingsService.cs`; `wwwroot/app.js`, `index.html`, `settings-runtime.js`, `conversation-stream.js` |
| E2E | new `NativeMultiToolEndToEndTests.cs`, new `RealNativeMultiToolBenchmarkEndToEndTests.cs`; `FakeOllamaServer.cs`, `TestSettings.cs`, `ExecuteCoreEndToEndTests.cs`, `EndToEndTestBase.cs` |
| Reproduction and documentation | new `scripts/run-native-multitool-benchmark.ps1`, `docs/native-multi-tool-execution.md`, this validation record and benchmark evidence |

Source paths above are relative to `AgenticRouter.Api`, except tests under
`tests/AgenticRouter.EndToEndTests` and the explicitly prefixed docs/script paths.

The internal profile flag is now `AcceptsMultipleCalls`; execution concurrency is
an independent setting. Existing single-proposal planner consumers remain valid;
Native gains an optional batch with ordered calls and individual parse failures.
Public stream/review and durable journal fields are additive and optional.

Eligibility is the exact allowlist `read_file`, `get_file_info`, `list_files`,
`search_text`, after individual validation and approval. Every mutation, unknown
tool, process, control capability, or existing batch tool is a sequential barrier.
Off is the default; default concurrency is 4, accepted range 1–16.

Persistence uses the existing checkpoint/journal. Completed effects are not
replayed as a batch. Unfinished restored calls become interrupted; metadata hashes
do not claim to be reusable result payloads. No automatic batch replay/retry was
added. The retained identity/order/state contract allows future explicit
dependencies; V1 introduces no DAG, subagents or additional checkpoint store.

LM Studio has no first-class Native provider in this checkout. Its documented
wire format was reviewed, but no LM Studio Native execution acceptance is claimed.
