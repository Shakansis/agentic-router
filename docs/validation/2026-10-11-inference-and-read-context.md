# Inference metrics and file-read context investigation

## Scope and live-instance protection

The user Host was inspected read-only (PID 138772, Release executable under the
normal source output). It was not restarted, suspended, instrumented or sent a
test inference. Its local persisted conversation was decompressed in a separate
reader with a bounded expanded size. No history was deleted or rewritten. Builds
and browser/API tests use `.artifacts/inference-finalization-20261011/`, isolated
data/workspaces and deterministic provider boundaries; no GPU inference or cloud
request was launched. Installed-Qwen validation also uses that fake provider.

## Confirmed trace evidence

### Metrics: `0HNP7LF1O5VFE:00000585`

Execution `e5bd5beae2844f25ab8e01785645e10b`, turn
`d35f858c515a4ed19cb0df10f353b4f1`:

| Persisted event | Elapsed | Output tokens | Generation | Throughput |
|---|---:|---:|---:|---:|
| Timeline 512, provider aggregate | 321,595 ms | 12,072 | 258,394.627 ms | 46.7192377 tok/s |
| Timeline 513, another inference waiting | 321,728 ms | unavailable | unavailable | unavailable |
| Timeline 521, terminal state | 329,498 ms | unavailable | unavailable | unavailable |

The known provider aggregate also contained load 10.5623 ms, corrected TTFT
2,589.5099 ms and prefill 17,694.979 ms. A further observation lacking final usage
invalidated the total. Its HTTP status/cause was not retained, so neither client
rejection nor server failure is established for this exact historical call.
Installed Qwen contains internal nonstreaming schema/next-speaker inference;
that is a possible additional call, not proof of this trace's cause.

An independently reproduced parser defect treated native `/api/generate` text
`response` as an object and lost final usage. New fixtures reproduce this and
rejected/server-failed trailing calls. Historical missing measurements are not
backfilled by assuming the last known partial aggregate is the exact final total.

### Read paradox: `0HNP7LF1O5VFK:00000369`

Execution `f8e39959e2134551a4d5e1a71826ddc9`, turn
`d6b77e11621b48938a4bd3ac17971847`:

- Qwen reported 78,397 input/active tokens in a 131,072-token window.
- Host subtracted 65,536 as an obligatory output reserve, leaving zero read
  headroom despite approximately 52,675 tokens of physical window headroom.
- The configured response reserve was 4,096. Reads at byte offsets 29,800 and
  36,500 returned `FILE_READ_CONTEXT_EXHAUSTED` without content or advancement.
- The agent tried a smaller range, then used a process. The correct Host response
  is a fitting range or context recovery, without encouraging tool bypass.

A separate Native browser/API regression read a roughly 48K-token file in a
65,536-token window, then rejected its next inference because the same fixed-half
reservation was used in runtime preflight. This was reproduced before changing
generation sizing, and passed after capping output by actual input headroom.

## Implementation

- Host read admission uses the configured response reserve and aggregate live
  occupancy; larger files can still return fitting partial content and byte offsets.
- Host compacts old active evidence when read headroom is exhausted. Full activity,
  canonical user context, verified effects, rollback and policy guards remain.
  External recovery uses the existing bounded continuity path; the deterministic
  read failure is cleared only after context changes so the same read can succeed.
- Native and common Ollama transport cap output to remaining input headroom;
  the existing half-window ceiling and residency configuration are preserved.
- Native final statistics take precedence, parser shapes are protocol-specific,
  and known pre-generation admission rejection does not poison previous metrics.
  Genuine unknown generated work remains unavailable with a short error reason.
- Each observed inference and Host read budget now has bounded diagnostic logs.
  No prompts, unrestricted payloads or secrets are added to logs.
- Native failure coverage exposed a second delivery defect: a failed inference
  was recorded, but the Chat iterator unwound before publishing the updated
  summary. The service now retains its current bounded operational summary;
  error/cancellation event translation includes it and replay uses the same data.

## Validation status

Final Release solution build and final Release test compilation passed with zero
warnings/errors. Scoped whitespace verification, JavaScript syntax and intended
diff checks passed.

| Boundary / evidence | Result |
|---|---|
| Final isolated browser/API acceptance (`final-acceptance.trx`) | 85/85 passed, 2m47s |
| Installed Qwen CLI, fake provider, configured MTP 0 and 3 (`installed-qwen-verified.trx`) | 2/2 passed, 30s; provider counts/time/prefill, tool results and native options verified |
| Same corrected read fixtures against earlier isolated API (`confirmed-read-before.trx`) | 3/3 fail as expected; full Native read and Qwen single/batch headroom |
| Native intermediate build before generation-headroom correction (`native-large-before.trx`) | full-file follow-up inference rejected; final fixture passes |
| Native failure and persisted replay (`terminal-chat-verified.trx`) | 5/5 passed including existing Chat presentation; no inference on replay |

The final suite includes 14 HTTP telemetry cases (native/compatible protocols,
malformed optional metadata, native final usage, buffered/missing usage, rejection,
server failure and large input), all five harness presentations/replay, complete
and ranged reads, actual zero-headroom recovery, encoding/memory bounds and
existing context/output/progress recovery. Simulated telemetry values verify
preservation/calculation, not real generation speed or measured latency savings.

The first expanded run passed 75/78 cases, including the Native complete-file
regression, 12 HTTP metrics cases, read-context recovery and existing context
failsafes. It found two memory-deferral classification regressions, corrected
without changing offsets/bytes, and one old UI fixture missing the current Git
decision, updated to use the existing send helper without weakening assertions.

Implementation touchpoints:

- `Execution/HostFileReadPolicy.cs`, `Execution/HarnessContextRecovery.cs`,
  `Execution/ExecutionSessionStore.cs`, `Chat/ChatStreamService.cs`: read budget,
  partial content, active compaction and bounded continuation.
- `Execution/ExecutionProgressPolicy.cs`, `Runtime/OllamaRuntimeProfileResolver.cs`,
  `Execution/HarnessInferenceObserver.cs`: generation ceiling versus remaining
  context, protocol telemetry and rejection/transport diagnostics.
- `Contracts/ApplicationContracts.cs`, `Usage/PricingCatalog.cs`,
  `Chat/IChatStreamService.cs`, `Controllers/ChatController.cs`,
  `wwwroot/conversation-stream.js`: optional failure evidence and delivery of the
  current summary on terminal errors/cancellation through the common renderer.
- E2E coverage and provider/CLI boundary fixtures: `FileReadPolicyEndToEndTests`,
  `BenchmarkAndHarnessEndToEndTests`, `InstalledHarnessUpgradeEndToEndTests`,
  `ProviderAndUiEndToEndTests`, `ExecutionProgressEndToEndTests`,
  `FakeOllamaServer`, `FakeCodexAppServer`, `FakeQwenCodeServer`.

Commands and output locations:

```powershell
dotnet build AgenticRouter.slnx -c Release --no-restore `
  --artifacts-path .artifacts/inference-finalization-20261011/build `
  -p:OutputPath="$PWD/.artifacts/inference-finalization-20261011/final/bin/" `
  -p:AppendTargetFrameworkToOutputPath=false -p:UseSharedCompilation=false `
  -m:1 --disable-build-servers

# The helper sets API/CLI executable paths to isolated outputs; all providers are fake.
.artifacts/inference-finalization-20261011/run-tests.ps1 `
  -Name final-acceptance `
  -Filter 'FullyQualifiedName~FileReadPolicyEndToEndTests|FullyQualifiedName~HarnessHttpMetricsAggregateCallsWithoutNativeUsageDuplication|FullyQualifiedName~EveryHarnessKeepsTokenBadgesVisibleIncludingUnavailableMetricsAndReplay|FullyQualifiedName~ContextFailsafe|FullyQualifiedName~GlobalExecuteOutputBudget|FullyQualifiedName~ExecutionProgress|FullyQualifiedName~FailedProviderCallIsRecordedAsEstimatedFailure|FullyQualifiedName~NativeFailedInferenceKeepsBadges|FullyQualifiedName~ChatPresentationEndToEndTests'
.artifacts/inference-finalization-20261011/run-tests.ps1 -Installed `
  -Name installed-qwen-verified `
  -Filter 'FullyQualifiedName~InstalledHarnessPreservesConfiguredNativeOptionsAndToolResults&Name~qwen-code'
.artifacts/inference-finalization-20261011/run-tests.ps1 -Baseline `
  -Name confirmed-read-before `
  -Filter 'Name~HarnessReadsHeadroom|FullyQualifiedName~NativeCanUseACompleteFileBeyondTheHalfContextGenerationCeiling'

dotnet format whitespace AgenticRouter.slnx --no-restore `
  --include <the implementation and fixture files above> --verify-no-changes
node --check AgenticRouter.Api/wwwroot/conversation-stream.js
git diff --check
```

The targeted filter covers all file-read cases, HTTP metrics aggregation, all
harness badge/replay cases, existing context failsafes, global Execute output
budget and progress recovery, Native failure metrics and Chat presentation.
TRX evidence is under `.artifacts/inference-finalization-20261011/results/`.
The source changes remain uncommitted alongside pre-existing work. This task did
not change `AGENTS.md`, rewrite history or remove unrelated modifications.

The normal user executable was not built or replaced by these validation commands.
The user instance changed independently during inspection (later PID 145600 under
the same Visual Studio parent); no user process was controlled by this work.
Post-run inventory found no remaining isolated API/test/Playwright or temporary
harness helper from these runs; the user Host PID 145600 remained running.
Acceptance in that instance has not been tested. Real-model generation,
resident PID preservation and fresh performance comparison were not performed
in this investigation. Deterministic fake-provider results are not GPU acceptance.
