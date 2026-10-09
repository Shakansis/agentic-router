# Global execution progress and output-limit recovery

Host-owned execution guidance and recovery are shared by Native and external
harnesses. `ExecutionProgressPolicy` defines immediate tool dispatch, early small
writes, bounded edits, initial action commentary, effort guidance and the output-limit correction. Native consumes it in
`LocalActionPlanner`; external adapters consume the common conversation envelope.
Instructions encourage progress but cannot guarantee model compliance.

Every external transport executes through the common Host loop. Adapters must
report exact native finish reasons through `HarnessEvent.FinishReason`, preserving
identity and unrelated errors. The Host recognizes `length`, `max_tokens` and
`max_output_tokens`. The common inference transport also reads these
provider facts from Chat Completions, Responses, Anthropic and native Ollama
responses. A later successful inference supersedes an earlier cutoff: harness
native recovery is not counted as a failed turn. Missing evidence is not inferred
from prose or a token count alone.

An unrecovered output cutoff receives one materially different Host continuation
in the same native session, model, provider, workspace and approval policy. The
Host first observes effects, checks unresolved tools, approvals, validation and
user decisions, and consumes the existing per-role recovery budget. The packet
retains the original objective and verified effects, forbids repeating committed
actions, and requests incremental writes only when the role has write tools.
Supervisors receive concise canonical-decision guidance instead. A repeated cutoff
or unresolved action yields a typed actionable terminal failure with observed
effects available for review. Cancellation never initiates recovery. Recovery retains the Host output budget and does not silently substitute a route.

An idle/completed harness lifecycle is not proof of goal completion. Host
completion gates govern the terminal diagnostic, incident status and browser
label, including blocked mutation objectives and partial context results.

The common seams already govern capability projection, trusted workspace policy,
approvals, tool effects, workspace observation, validation, context recovery,
canonical conversation continuity, execution evidence and terminal presentation.
Future integrations must use these seams. Native protocol parsing, authentication,
native session control, tool mapping and transport-specific recovery mechanics
remain adapter responsibilities. Existing Codex transport-failure classifications
and each harness's context-error translation describe protocol facts, not global
output-limit exceptions.

In-flight steering is not required for this recovery path. A future steering
adapter must prove when a native control takes effect before it can be used as a
preventive intervention. Enqueuing a message is not evidence that it interrupted
the current model generation.

Validation is deterministic browser/API E2E through fake provider/harness
boundaries, including all current harnesses, repeated cutoff, native recovery,
unresolved actions, cancellation, route continuity and blocked terminal display.
Real model compliance remains separate acceptance requiring authorization.

## Implementation surfaces

- Shared policy and terminal truth: `ExecutionProgressPolicy`,
  `ExecutionTerminalState`, `HarnessContracts` and the project-wide invariant in
  `AGENTS.md`.
- Instruction consumers and recovery orchestration: `LocalActionPlanner`,
  `HarnessConversationPromptBuilder` and `ChatStreamService`.
- Protocol facts: `OpenCodeHarness`, `QwenCodeHarness`, `ClaudeCodeHarness` and
  `HarnessInferenceObserver`. Codex uses the same envelope and Host observer;
  its transport needs no output-limit policy copy.
- Diagnostics, saved history and presentation: `ChatController`,
  `IncidentEventFactory`, `JsonlIncidentJournal`, `PersistentSessionService`,
  `conversation-stream.js` and `projects-sessions.js`.
- Deterministic coverage: `ExecutionProgressEndToEndTests`, updated Native/Claude
  supervision expectations, and fake Codex, OpenCode, Qwen, Claude and Ollama
  boundary fixtures.

## Executed validation

- Release solution build with `--no-restore -m:1 -nr:false
  -p:UseSharedCompilation=false -p:OutputPath=bin/progress-check/Release/`:
  zero warnings and errors. OutputPath preserves the normal Web SDK exclusions
  and avoids replacing the running application's binaries.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed.
- `node --check` for both changed browser scripts: passed.
- `git diff --check` and intended-change inspection: passed; unrelated dirty
  work was retained.
- 118 relevant Playwright browser/API E2E cases: passed, zero skipped. Coverage
  includes global progress, context failsafes, Native/Claude budget exhaustion,
  verified effects, diagnostics, external approvals and workspace boundaries,
  conversation handoffs, and inference-observer protocols. The run used isolated
  API/fake executable paths and `playwright.runsettings`; the report is
  `tests/AgenticRouter.EndToEndTests/TestResults/execution-progress-final.trx`.
- No real inference, GPU workload, cloud provider validation or restart of the
  user's running application. This is deterministic policy/transport validation,
  not acceptance of a real model's willingness to write early. Preventive
  in-flight steering was not added.

## Execute output budget and checkpoint regression

All Execute generations reserve 50% of the selected general context for output,
capped by the model, provider and role context ceilings. At 131,072 context tokens,
the output limit is 65,536. This Host rule takes precedence over the role/model
output limits and the older file-creation override during Execute; Chat and
non-execution calls retain their configured limits. Input-fit/compaction uses the
same reservation. The context ceiling itself is not increased.

Native Ollama resolution consumes the common Host policy. External inference
transport applies that same budget to Chat Completions (`max_tokens` or an existing
`max_completion_tokens` field), Responses
(`max_output_tokens`), Anthropic Messages (`max_tokens`) and native Ollama
(`options.num_predict`), replacing competing output-limit fields. Cloud Execute
uses the same policy through the existing provider adapters. This prevents a
harness default from silently capping Execute at a lower value. The transport
preserves the rest of the request and does not persist provider payloads. The
applied external budget is visible in `harness.output-budget.applied` activity.

Trace `0HNP052MDB2NQ:00000319` used Qwen 3.8 27B with OpenCode 1.18.18. Native
session evidence confirms delivery of early-write guidance, then an initial
32,000-output-token `length` finish without a tool call. After Host correction,
list_files completed and create_execution_plan triggered automatic supervision.
The durable takeover failed because its optional null ValidationStatus was
incorrectly rejected by the checkpoint validator. Null now remains valid, while
non-null values retain the 128-character bound. The regression exercises a
durable automatic takeover before validation and verifies the checkpoint exists.

Increasing the output allowance does not enforce model compliance with early-write
instructions. In-flight steering remains outside this change. No new real-model
inference is needed to diagnose the retained session.

## Regression validation audit

The earlier 118-case progress report was a filtered run, not the complete suite.
None of the four failing cases later reported during session-isolation validation
was included in that selection. Reproducing those failures with the old session
identity established independence from isolation, not acceptance of failures.

The four cases now exercise current contracts: common Claude output recovery in
the same Supervisor session, deterministic schema-v4 migration before automatic
inference followed by explicit resume, model/digest-specific Qwen runtime limits
for the 128k window, and shared immediate-tool/early-write guidance for Codex.
Recovery assertions include native session identity, exact continuation count,
worker separation and observed file effects. Migration retains its zero-counter
assertion at a deterministic Host wait boundary.

The first complete deterministic run found ten further failing case results
(625/635 passed). Corrections preserve inline Settings errors, invalid-field focus
and atomic Save, require Blocked after rejection, restore unique-insertion-marker
guidance globally, preserve loader failure details during reconnect, align core
script cache versions, and reduce details-modal spacing without removing controls
or raising the height assertion. Two restoration defects required code changes:

- Save the terminal event and inference metrics with the completed turn before
  publishing completion. Transfer the pending user timeline to its assistant
  message, avoiding a duplicate assistant when history is restored.
- Historical user-input events do not activate, clear or submit the live question
  batch. Conversation restoration rerenders the Host-restored draft after clearing
  the composer. Tests inspect both persisted answers and expired resume authority.

The subsequent full run also exposed an assertion for the superseded per-file
output override. Its replacement verifies that the optional setting still saves
and round-trips, Execute uses half the configured context for both tested models,
both requested files contain the observed content, and Chat retains its configured
Primary role output limit. No production setting or user-facing control was removed.

Further coverage updates the configured-context and native-recovery expectations
to the same half-context policy. The compaction fixture provides enough input room
for mandatory guidance after the output reservation while retaining oversized old
history and all compaction assertions. Scripted long-action/game fakes retain only
acknowledged results already observed at their external boundary; they do not count
proposals as effects. The long-action case now requires terminal completion, exactly
22 completed creates and 22 completed reads, and exact content in every file.
Startup displays the original failure alongside retry progress, preserving both
the non-JSON HTTP diagnostic and the five-retry/health-check/reconnect contract.

Files changed by this regression follow-up:

- Production: `BenchmarkProductionExecuteRunner.cs`, `ClaudeCodeHarness.cs`, `QwenCodeHarness.cs`, `ExecutionProgressPolicy.cs`, `ChatController.cs`,
  `PersistentSessionService.cs`, `conversation-stream.js`, `projects-sessions.js`,
  `loading.js`, `panels.css`, and the core script version in `index.html`.
- E2E and external fakes: `BenchmarkAndHarnessEndToEndTests.cs`,
  `DurableSupervisionEndToEndTests.cs`, `ExecuteCoreEndToEndTests.cs`,
  `ExecutionStateEndToEndTests.cs`, `ProviderAndUiEndToEndTests.cs`,
  `InferenceProfileExecutionEndToEndTests.cs`, `EndToEndTestBase.cs`,
  `FakeOllamaServer.cs`, and `FakeClaudeCodeCli/Program.cs`.
- Specifications/evidence: this document and `PLAN-v59-durable-supervised-execute.md`.

The preceding session-isolation change touches `Chat/IChatStreamService.cs`,
`Chat/ChatStreamService.cs`, `Supervision/SupervisionExecutionEngine.cs`,
`Execution/HarnessContracts.cs` and `Execution/QwenCodeHarness.cs`. Its coverage is
`SupervisionSessionIsolationEndToEndTests.cs` and the shared external fixture
`tests/SupervisionSessionFixture.cs`, linked from the four fake harness projects
and dispatched by their `Program.cs` entrypoints. The durable-run specification
describes the common session rule and Qwen runtime lifecycle.

Full coverage also exposed an intermittent cancellation failure in the production
benchmark loopback runner. The captured cause was `ArgumentOutOfRangeException`
in `System.Net.Http.HttpConnection.ParseStatusLine`, during the review request
after a cancelled SSE read. `ReadLineAsync().WaitAsync(token)` abandoned the wait
while leaving the read pending; `ReadLineAsync(token)` now cancels and awaits the
underlying I/O before settling the turn and fetching its review. The E2E case
repeats ten cancellations, alternating pre-execution cancellation and cancellation
after an actual streamed turn activity. It retains the selection-count and
cancelled-outcome assertions and logs the Host exception on failure. No benchmark
exception is hidden or reclassified to make the assertion pass.

The live cancellation/history regression now captures the active run ID and waits
for that exact ID to be selected after cancellation. Waiting merely for a nonempty
history selection could inspect the previous result while asynchronous history
refresh was still selecting the new result. The assertions retain exact GPU
identity and also check the configured 40,960-token context on the cancelled run.
Codex continuity explicitly configures medium task Thinking before checking its
prompt-guided effort event; automatic Thinking no longer implies an unrequested
medium effort override.

The all-external-harness behavior benchmark exposed an intermittent failed cleanup
on Claude's `SCOPE-RETENTION-001` workspace. Claude requested process termination
but disposed the Process without awaiting exit, allowing workspace cleanup to race
the dying process's Windows current-directory handle. The turn now awaits exit
before disposal and completion, matching the other external adapters. The original
all-scenario cleanup assertion remains, with per-harness/scenario failure facts.

Repeating the Qwen malformed-event/daemon-crash E2E case exposed a separate
unclassified failure: the daemon could reset the prompt POST connection before
the Host observed HTTP acceptance. The captured cause was `HttpRequestException`
with inner socket error 10054 from `SubmitPromptAsync`. That path now reports
`qwen-code-prompt-transport`, retains the original exception and does not resubmit
the ambiguous prompt. The same E2E case repeats ten malformed-event/crash pairs,
requiring exactly one typed terminal event for every turn.

Failed browser/API cases now retain the last 16,000 characters of their fake Host
API log in test output, before fixture cleanup removes temporary evidence. The
Native question-cancellation case repeats ten times without extending its timeout
or dropping its distinction from approval and terminal-event assertions.

Validation uses isolated Release binaries and fake external provider/harness
boundaries. The opt-in `RealCompletionMetricsEndToEndTests` class is excluded:
no real inference, GPU workload, cloud call or restart of the user's application
is performed. Formatting is verified for the files changed by these corrections;
the global formatting write was rejected by automatic approval review because
unrelated dirty files could be rewritten.

### Completed full-suite validation, 2026-10-02

- All 648 deterministic browser/API E2E cases passed; zero failures and skips.
  The only excluded class is opt-in `RealCompletionMetricsEndToEndTests`.
  Evidence: `tests/AgenticRouter.EndToEndTests/TestResults/context-isolation-full-648-20261002.trx`.
- All 16 supervised-session identity cases passed for OpenCode, Qwen Code,
  Claude Code and Codex, including direct behavior, separate Workers, Supervisor
  reuse after Workers, independent runs and explicit resume from another browser.
- The full run includes ten benchmark cancellations, ten Qwen malformed-event /
  crash pairs and ten Native question cancellations. The Native case also passed
  ten repetitions in the focused run. An earlier generic pre-question Native
  failure had no captured original cause; no specific Native production fix is
  claimed for that unreproduced event. Failed cases now preserve bounded API logs.
- Release solution rebuild passed with zero warnings/errors. It ran from the
  frozen checkout under `bin/validation-snapshot-context-isolation`, using
  `dotnet build AgenticRouter.slnx -t:Rebuild -c Release --no-restore
  -p:OutputPath=bin/frozen-check/Release/ -p:UseSharedCompilation=false -m:1 -nr:false`.
  Rebuild avoids reusing stale binaries when copied sources retain older timestamps.
- Full-suite command: `dotnet test tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj
  -c Release --no-build -p:OutputPath=bin/frozen-check/Release/
  --settings playwright.runsettings --filter 'FullyQualifiedName!~RealCompletionMetricsEndToEndTests'
  --logger 'trx;LogFileName=deterministic-all-verified.trx'`.
  The fixture environment selects the frozen API/fake executable paths.
- `dotnet format --verify-no-changes --no-restore --include ...` passed for the
  15 regression C# files and nine additional isolation/fixture C# files.
  JavaScript syntax checks passed for `loading.js`, `projects-sessions.js` and
  `conversation-stream.js`; `git diff --check` passed.
- Final source-hash comparison matched the current checkout for all frozen files
  except this evolving report. The user's application and unrelated processes
  were preserved; real inference/GPU/cloud validation was not run.

## Executed output-budget validation

- Isolated Release solution build, `--no-restore -m:1 -nr:false
  -p:UseSharedCompilation=false -p:OutputPath=bin/checkpoint-done/Release/`:
  zero warnings and errors. Subsequent fixture-only builds also passed.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` and
  `git diff --check`: passed; unrelated dirty work was preserved.
- 27 deterministic browser/API E2E results passed, zero failures/skips:
  `tests/AgenticRouter.EndToEndTests/TestResults/checkpoint-output-complete.trx`.
  Coverage includes actual outgoing output limits for Native, all four external
  harnesses, Groq/Cerebras/Gemini adapters, unchanged Chat limits, retained
  optional-setting round-trip, context fit, bounded output recovery/cancellation,
  durable takeover before validation and takeover after verified mutations.
- Gemini's fake provider now supplies the required initial commentary and follows
  the Host completion/read gate. Earlier failures and waits from incomplete fake
  responses were resolved; their runs are not counted as passing evidence.
- The incident diagnosis uses retained OpenCode session/incident facts. No new
  real inference, GPU workload, real cloud request or user application restart
  was performed. The running user instance still requires the updated build to
  be started before this change takes effect.


## Inference profile ownership

Direct Execute and supervised Workers use the configured task-intention profile,
including Temperature, supported sampling controls, Seed, and Thinking. Manual
model selection does not replace these values with the internal deterministic
profile. Workers classify the active work-item objective rather than the Host
wrapper, requirements, verification instructions, or correction prose. Requests
to create a game are classified as software development.

Only Supervisor turns use the Supervisor sampling profile and the Plan, Verify,
Complete, and Recovery phase effort controls. The old Work control was removed
from Settings; its serialized field is preserved for compatibility and no longer
controls Worker inference. Auto/Disabled/Enabled task Thinking does not introduce
a hidden medium-effort override. External harnesses omit an unsolicited effort
override and continue reporting profile controls they cannot forward as unavailable.
This change does not add a sampling transport to those harnesses.

Validation of profile ownership: Release solution build passed with zero warnings
and errors; whole-solution `dotnet format --verify-no-changes --no-restore`,
`node --check` for the three changed JavaScript files, and intended-diff review
passed. All 42 focused fake-provider browser/API E2E cases passed. The report is
`tests/AgenticRouter.EndToEndTests/TestResults/inference-profile-final.trx`.
Coverage includes footer Save staging, manual/Auto direct Execute sampling,
game/story classification, distinct Supervisor/Worker profiles, Worker Thinking
Auto and recovery, unavailable Thinking modes, all four external harness effort
contracts, Chat profile regressions, and actual Groq/Cerebras/Gemini request
parameters while preserving the half-context Execute output budget. No real
model/GPU/provider inference was invoked and the user's running Host was not restarted.

Implementation files for this correction: `Providers/ModelProviders.cs`,
`Routing/IntentionRouter.cs`, `Chat/ChatStreamService.cs`, `Chat/IChatStreamService.cs`,
`Supervision/SupervisionExecutionEngine.cs`, the common harness request/prompt
contracts and four existing harness adapters. Settings descriptions, phase controls,
local Help, and the configuration inventory were updated together. E2E coverage
was added in `InferenceProfileExecutionEndToEndTests.cs` and updated in the existing
DurableSupervision, ExecutionProgress, and BenchmarkAndHarness suites.
