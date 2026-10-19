# Qwen3.8 27B MTP3: Native investigation benchmark

Date: 2026-10-15, America/Sao_Paulo. Real inference explicitly requested by the
user. This measures the existing Native implementation, without production
changes, new scheduler mechanisms, or rerunning the general E2E suite.

## Conditions

- Installed model: `qwen3.8:27b-q4_K_M`, digest
  `25b843619e944cd0ae6069f94ff4e5e26a16e109ccbc0a66a0f05979ed70098e`.
- DEV settings were read from `AgenticRouter.Api/data/settings.json` and relevant
  local runtime/execution/profile settings copied into an isolated Host. User
  settings, history and provider credentials were not changed or copied into the
  fixture. Source settings hashes are retained with the evidence.
- DEV placement: `vulkan:all`; cache F16; context 131,072; explicit MTP draft 3;
  automatic batch sizing. Runner command confirmed `-c 131072`,
  `--spec-type draft-mtp --spec-draft-n-max 3`, K/V `f16`, and batch/ubatch 512.
  Loaded DLLs included `ggml-vulkan.dll`. Existing Host runtime telemetry reported
  configured/observed Vulkan, 131,072 actual context and GPU residency. Per-GPU
  weight placement is not independently identified by that telemetry.
- Normal Host intention routing classified this task as `review-and-testing`:
  configured temperature 0.25 and thinking medium. No sampling/profile override
  was introduced to force the development classifier. The adapter reported native
  thinking control unavailable; configured medium must not be described as an
  independently verified native reasoning level.
- Explicit Native, direct strategy, automatic approval, web disabled. The isolated
  routing/coordinator assignments prevent other models being invoked. These
  benchmark selections are identical in all variants.
- A separate Host-path warmup loaded the model and exercised a read before timing.
  Nine fresh browser turns use orders A/B/C, B/C/A, C/A/B. Host, workspace, model,
  DEV runtime settings and common task are shared; conversation state is fresh.

## Task and measurement boundary

The generated OrderDesk repository contains **46 files**, including **32 C# source
files**, README, six incident/architecture/time/storage/release/runbook documents,
three JSON configuration files, a project file and three synthetic logs with
5,000 routine records each plus correlated incident records. Total size is about
2.1 MB. All fixture content is synthetic and unchanged across repetitions.

Incident INC-241 concerns orders omitted from daily history/export after a
business-date/timezone change. The prompt asks the model to start at README and
the incident document, trace the request/export flow, cross-check source/config/
contracts/logs, use bounded reads and searches, and report the evidence and one
next verification. It requires at least eight relevant read files and a search,
but does not prescribe every path or a fixed action plan. The model can choose
different investigation depth between runs. Diagnosis correctness is not scored.

Only the final tool instruction changes between A and B/C:

| Variant | Model instruction | Execution |
| --- | --- | --- |
| A | At most one tool call per response | Off, sequential |
| B | Group up to four independent inspections per response | Off, sequential |
| C | Same instruction as B | Auto, limit 4 |

All variants use individual Native tools. The existing `read_files` batch tool is
excluded consistently to avoid measuring a different batching mechanism.

Measurements reuse browser `/api/chat/stream`, Host review actions/batches,
per-inference context/provider timing and scheduler telemetry. Inference counts
deduplicate context updates by sequence. Input/output tokens are provider-reported
counts summed across inferences; input includes repeated/cached prompt content
and is not a count of unique tokens or a monetary charge. Executed tools come
from completed Host action records, not merely model proposals. Peak concurrency
comes from observed runtime counts, not admitted/queued calls.

The existing optional benchmark runner gained a task configuration and A/B/C
selection, activity/action export and a fixture generator. Its original A/B/C/D
three-source-file task remains available. Failed or incomplete investigation
turns are retained in measurements; they are not silently discarded or retried
until a favorable result appears.

Reproduction:

```powershell
dotnet build tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-restore --disable-build-servers -m:1 -v minimal
.\scripts\run-native-multitool-benchmark.ps1 -BenchmarkTask investigation -Model 'qwen3.8:27b-q4_K_M' -Repetitions 3 -OutputDirectory 'TestResults/native-multitool/qwen27b-investigation'
```

The test-project build passed with zero warnings/errors. No general E2E suite
was invoked. Runtime artifacts, DEV snapshot, source hashes, fixture manifest,
prompts, ordered actions, answers and per-inference measurements are retained in
the output directory. The first warmup attempt lacked the API-required browser
session identifier and was rejected before inference; the launcher was corrected
before warmup and all nine measured turns. No production validation was relaxed.

The original collector successfully saved the first six measurements, then
Chromium evicted the seventh turn's SSE response before Playwright could call
`Network.getResponseBody`. That Host turn completed, but its measurements were
unavailable, so it is excluded as an instrumentation failure. The partial run,
failure and six valid rows were preserved under
`TestResults/native-multitool/qwen27b-investigation-partial`.

Only the benchmark collector was corrected: it consumes a clone of the same
browser fetch response while the UI streams, without another request or changing
production SSE behavior. After an isolated Host restart and identical Host-path
warmup, only the third block C/A/B was executed. Fixture hashes matched exactly;
DEV/runtime settings and the production sources remained unchanged. Thus the
final sample contains three valid turns per scenario, plus one disclosed invalid
collection attempt, not a selection based on timings or diagnosis quality.

Resume command used for that final block:

```powershell
.\scripts\run-native-multitool-benchmark.ps1 -BenchmarkTask investigation -Model 'qwen3.8:27b-q4_K_M' -Repetitions 3 -StartRepetition 3 -OutputDirectory 'TestResults/native-multitool/qwen27b-investigation'
```

## Results

There are **nine valid measurements, three per variant**. Every measured turn
reached terminal state, completed at least eight distinct relevant file reads
and a search, and returned an investigation report. There were zero streamed
errors, rejected tool calls or mutations. A emitted only singleton calls; B/C
actually emitted groups of three/four. No diagnosis score was assigned.

Medians, with each metric summarized independently:

| Metric | A: single sequential | B: multi sequential | C: multi Auto, limit 4 |
| --- | ---: | ---: | ---: |
| Total browser turn, seconds | 44.788 | 39.077 | 34.429 |
| Wall range, seconds | 34.476–53.370 | 31.402–52.424 | 31.911–41.136 |
| Inferences | 13 | 6 | 6 |
| Input tokens, summed | 66,830 | 34,377 | 34,704 |
| Output tokens, summed | 2,672 | 2,447 | 1,950 |
| Total input+output tokens, median of run totals | 69,502 | 37,283 | 36,913 |
| Executed tools | 12 | 17 | 17 |
| Distinct file reads | 9 | 13 | 12 |
| Search calls | 3 | 3 | 3 |
| Observed peak concurrency | **1** | **1** | **1** |
| Provider inference wall sum, ms | 43,771.406 | 38,164.896 | 33,312.488 |
| Prefill sum, ms | 6,966.686 | 5,568.923 | 6,155.137 |
| Decode sum, ms | 35,768.985 | 31,365.239 | 26,121.074 |
| Tool runtime sum, ms | 26 | 35 | 65 |
| First-inference TTFT, ms | 1,463.223 | 1,621.766 | 1,272.656 |

Per-run measurements:

| Run | Wall, s | Inferences | Input tokens | Output tokens | Tools | Peak | Tool runtime sum, ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A1 | 34.476 | 10 | 48,315 | 1,824 | 9 | 1 | 29 |
| B1 | 52.424 | 6 | 34,377 | 3,167 | 17 | 1 | 35 |
| C1 | 31.911 | 6 | 34,704 | 1,718 | 16 | 1 | 43 |
| B2 | 39.077 | 6 | 34,836 | 2,447 | 18 | 1 | 43 |
| C2 | 34.429 | 6 | 36,474 | 1,950 | 17 | 1 | 74 |
| A2 | 53.370 | 14 | 73,325 | 3,227 | 13 | 1 | 26 |
| C3 | 41.136 | 6 | 34,436 | 2,477 | 18 | 1 | 65 |
| A3 | 44.788 | 13 | 66,830 | 2,672 | 12 | 1 | 19 |
| B3 | 31.402 | 5 | 27,793 | 1,898 | 15 | 1 | 33 |

Compact shareable evidence includes every prompt, answer, ordered inspection
sequence, metric and fixture hash:
[native-multi-tool-qwen27b-investigation.json](native-multi-tool-qwen27b-investigation.json).
Detailed per-inference and batch telemetry is in
`TestResults/native-multitool/qwen27b-investigation/measurements.json`.

## Interpretation

**Multi-tool calling works in an actual investigation.** A's 10–14 inferences
became B's 5–6 and C's 6, while B/C executed more inspections: 15–18 tools versus
A's 9–13. Median inference count fell **53.8%** and median input tokens roughly
halved. B's median total time was **12.8% lower** than A. The model used the
results to move from incident/contracts to implementation/configuration and
trace-correlated logs; it did not simply issue a fixed read batch and stop.

The investigation depth varied despite identical task and runtime conditions.
For example, A1 read the range factory/resolver/query and searched the logs as a
directory, whereas B1 additionally inspected tenant configuration, API service,
export job and each log individually. This is intended observational evidence,
not a fixed-trace replay. Whole-turn timing therefore combines batching,
investigation choices, prefill/cache behavior and variable generated output.
Three repetitions do not establish a general performance ranking.

**There is no measured parallel tool gain.** C's median was 11.9% below B, but
peak concurrency was exactly one in every C run. Eligible calls were admitted
through the parallel path (`eligible-read`), but finished before another call
became active. These cached source reads and bounded searches were too short
relative to serial Host preparation to overlap. Multiple queued/admitted calls
must not be counted as concurrent execution.

The entire tool runtime sums were only **19–74 ms per turn**. At the C median,
65 ms is about **0.19%** of the total wall time. It cannot explain the 4.648-second
B/C difference. C generated fewer output tokens (median 1,950 versus 2,447), and
its provider decode time was correspondingly lower (26.121 versus 31.365 seconds).
Inference occupied roughly 96–98% of each turn. The B/C wall difference must not
be attributed to concurrent I/O when no such overlap was observed.

Input-token reduction is directly visible from fewer tool/result round trips;
decoding cost still depends on how much the model emits. B1 illustrates the
limit: it performed more inspection with fewer inferences than A1 but emitted
3,167 rather than 1,824 output tokens and took longer. Fewer inferences do not
guarantee a shorter complete turn.

## Default recommendation

**Keep Off as the Native default for now.** Off already accepts and processes
multiple calls per inference, so this retains the demonstrated multi-tool benefit.
Auto remains available for workloads whose read/query operations actually overlap.
This investigation provides no measured parallelism benefit to justify changing
the default, and its C timing advantage is not scheduler evidence. No default,
production policy or production code was changed as part of this benchmark.

## Validation and cleanup

- Test-project Release build: zero warnings/errors. Only the opt-in real benchmark
  was run; no general E2E suite was rerun. Final test invocation completed the
  three resumed turns and retained all nine rows; its duration was 2m05s, not the
  duration of all nine turns. The partial invocation and collection failure remain
  disclosed above.
- Formatter verification for the benchmark collector passed; `git diff --check`
  passed. The fixture generator and launcher parsed and executed successfully.
- Original DEV settings hash matched after the benchmark. All 46 fixture hashes
  matched; production source hashes matched the pre-benchmark snapshot.
- Both isolated Hosts and their managed Ollama/runner trees were stopped. Final
  Host port **63484** and managed runtime port **16535** had no listeners; known
  owned runner/server PIDs were absent. Original Ollama processes **34188, 19340,
  65184** retained their original identities, and services on 11434/11436 retained
  their original empty model residency. No user service was restarted.
- `cleanup.json`, `runner.json`, `backend-evidence.json`, `runtime-effective.json`,
  `dev-profile.json`, `fixture-manifest.json`, TRX and Host logs are under the
  benchmark/partial output directories. Volatile-history missing-session warnings
  still appear on the existing persistence path; no persistence fix was introduced
  or represented as successful durable-history acceptance.

Changes for this measurement are limited to the existing benchmark launcher and
test collector, the new `scripts/New-NativeInvestigationFixture.ps1`, this report
and its JSON evidence. The prior multi-tool production implementation is intact.
