# Host context failsafe: impact assessment

Status: first reactive implementation, 2026-09-17. The original assessment below
remains the rationale; the implementation boundary is recorded here.

## Implemented boundary

- Recovery runs only on these terminal protocol failures: Codex
  `contextWindowExceeded`; OpenCode `ContextOverflowError`; Claude Code
  `error_during_execution` whose result starts with `Prompt is too long`; and
  Qwen Code's explicit post-compression context-too-large diagnostic. Unknown
  errors and empty answers alone retain their existing paths.
- The Host retains the available canonical conversation and current request
  verbatim, including their role-specific output contract and current images.
  The fresh context also receives Host-observed files, action states, processes,
  plan, validation and conflicts. Native tool outcomes are labeled as unverified
  harness observations. Missing native scratch history requires reinspection.
- Required continuity is never silently truncated to force recovery to fit. The
  existing conservative estimator counts the Host envelope, managed instructions,
  granted Host schemas and image estimates. Recovery uses 90% of the smaller of
  (configured window minus response reserve) and an explicitly reported Qwen
  admission limit, when present. The remaining 10% is headroom, not an exact
  measurement of native overhead.
  Adapter-specific notes are checked before native-context creation.
- Pending native actions, Host actions, durable journal ambiguity, approval,
  validation and user input prevent replacement. Effects are observed even when
  recovery is declined or the second attempt fails; unchanged effects are not
  recorded as additional completed actions.
- Each adapter replaces only the affected native context. Qwen retains its old
  mapping until the replacement is validated, then closes the replaced native
  session. If capacity cannot accommodate both temporarily, recovery declines
  without evicting another conversation. No daemon restart implements this reset.
- The one-attempt budget is shared with the supervised role's existing harness
  retry and Codex transient continuation. It survives a canonical-output retry.
  Exhaustion and unavailable recovery are typed non-retryable results for this
  automatic strategy; effects remain available for review and explicit user action.
- No summarizing model call, runtime patch, new provider, persistence threshold
  change, or UI/history redesign was added. Healthy turns do not perform the new
  packet construction, workspace reconciliation, inference, or context replacement.
  Existing tool events are tracked in memory to identify unfinished native work.

Changed source files: `Chat/ChatStreamService.cs`, `Chat/IChatStreamService.cs`,
`Execution/HarnessContextRecovery.cs`, `Execution/HarnessContracts.cs`,
`Execution/HarnessConversationPrompt.cs`, `Execution/AgentHarness.cs`,
`Execution/QwenCodeHarness.cs`, `Execution/OpenCodeHarness.cs`,
`Execution/ClaudeCodeHarness.cs`, and `Supervision/SupervisionExecutionEngine.cs`
under `AgenticRouter.Api`.

Test files: `tests/AgenticRouter.EndToEndTests/ContextFailsafeEndToEndTests.cs`
and `Program.cs` in `tests/FakeQwenCodeServer`, `tests/FakeOpenCodeServer`,
`tests/FakeCodexAppServer`, and `tests/FakeClaudeCodeCli`.

Documentation: this file and `docs/context-recovery-investigation.md`.

## Recommendation

Add a narrowly triggered recovery path to the Host's existing external-harness
execution loop. It should run only after an identified context-capacity or
compaction failure, and only when the Host can reconstruct sufficient continuation
state and the adapter can start a fresh native context safely. Keep native
compaction in charge of normal operation. Do not introduce a universal summarizer
or a second compaction pass on every turn.

The first implementation should use deterministic Host facts and existing token
estimation, not an additional model call to summarize. This avoids reproducing an
empty-summary dependency inside the failsafe. Model-generated summaries and
proactive resident-context management are separate, later decisions if evidence
shows the deterministic recovery is insufficient.

## Current behavior and integration points

| Path | Existing behavior | Impact boundary |
| --- | --- | --- |
| Native | `LocalActionPlanner.FitToBudget` and `ExpertExecutionGuidanceService.FitToBudget` already compact deterministically, measure the request, and allow a bounded smaller retry. | Reuse relevant estimation/fact representation; do not wrap Native in another recovery loop. |
| Qwen Code | Reuses native sessions; the Host supplies context-window size. Native compression is internal to the external runtime. Changing model/tool-inventory configuration can restart its daemon. | Identify its explicit compression failure. A recovery must replace only the affected native context, without using a daemon restart as a reset mechanism. |
| OpenCode | Reuses a native session; the Host observes workspace changes and handles native permission events. | Existing healthy sessions remain untouched. Adapter-specific error normalization and fresh-context semantics need proof before enabling recovery. |
| Codex | Reuses/resumes threads and configures native compaction. The Host already permits one continuation for selected transient transport failures. | Context recovery must not interfere with that continuation or silently grant an additional independent retry budget. Preserve thread routing, permissions, and context configuration. |
| Claude Code | Failed turns already remove the cached native session mapping. | Do not add redundant resets. Recovery must track whether a new native context is already needed and preserve the existing routing/permission profile. |
| Supervised Execute | Has separate bounded harness, canonical-output, and watchdog recovery paths. Durable action state tracks prepared, awaiting-approval, in-flight, ambiguous, and committed work. | A lower-level context retry must report consumption/exhaustion so supervision cannot repeat the same recovery strategy with a fresh counter. |

Main source locations: `Chat/ChatStreamService.cs`, `Execution/HarnessContracts.cs`,
the four external adapters, `Execution/ExecutionSessionStore.cs`, and
`Supervision/SupervisionExecutionEngine.cs`. `IAgentHarnessTransport` currently
does not define an explicit fresh-context operation; it must not be simulated by
calling `CancelTurnAsync` or changing the Host conversation identity.

## Proposed narrow flow

1. Let the harness complete or fail using its existing behavior.
2. On a terminal failure, normalize only supported context/compaction error codes
   or narrowly recognized protocol diagnostics. An empty assistant answer, network
   error, approval rejection, or arbitrary message mentioning context is not
   sufficient evidence.
3. Reconcile Host-observed effects and existing action-journal state. Do not reset
   while an action, approval, validation, or user-input request is unresolved.
   The supervised journal provides part of this evidence; direct Execute needs
   an equivalent verified check, not an assumed empty journal.
4. Prepare a deterministic continuation packet containing the current objective,
   known requirements, relevant prior user constraints, verified changes and
   validations, completed operations, and outstanding work. Preserve the current
   role's output contract, especially supervisor verification JSON.
5. Measure the complete Host-generated packet, managed instructions, available
   tool schemas, and output reserve using the existing estimator. Native-only
   overhead is not fully visible to the Host and must remain explicitly estimated
   or unknown. Do not copy Qwen's 23,000-token reserve onto other harnesses.
6. Validate that required content fits and the packet is materially smaller before
   retiring the old mapping. Do not silently truncate a mandatory user constraint
   or required verification evidence. If insufficient information can be retained,
   preserve the reviewable failure instead of claiming a safe recovery.
7. Use an explicit adapter operation to start a fresh native context for that
   same Host conversation, role, model, endpoint, workspace, and approval mode.
   Retain original history/evidence; update the native mapping only after setup
   succeeds. Do not replay tool calls automatically.
8. Allow at most one such recovery within the applicable existing turn/role
   recovery budget. Report start, result, and exhaustion through existing technical
   activity. A later layer must not reclassify exhaustion as permission to repeat
   this same strategy.

## Risks and required controls

| Risk | Required control |
| --- | --- |
| Losing requirements or facts held only in native history | Do not equate filesystem state with complete semantic continuity. Retain canonical requirements, disclose omitted optional material, and refuse automatic reset if necessary state cannot be reconstructed. |
| Repeating a mutation or process that already completed | Reconcile journal/effects first; include committed results; require reinspection; never mechanically replay previous commands. |
| Abandoning approvals or changing their identity | No context replacement with unresolved approvals/actions. Keep Host identities and exact-command grants unchanged. |
| Retry multiplication across Host and Supervisor | Share/propagate recovery consumption and a typed exhaustion result; test nested failure paths. |
| Resetting another task or restarting a shared daemon | Replace only the affected native context. No global adapter/process reset. |
| False-positive context classification | Exact protocol evidence at the adapter boundary; unknown failures retain their current behavior. |
| Underestimating native prompt/tool overhead | Separate known packet estimates from native usage; retain native checks. No claim that the Host knows an exact full native context. |
| Losing useful native state during failed setup | Validate the packet first, create/validate the replacement before switching the mapping, retain original evidence. |
| Altering successful workloads | No new summarization, provider call, workspace scan, or reset on success; regression tests assert unchanged native session reuse and call counts. |

The existing transcript gzip/See more behavior and storage byte thresholds do not
need a change for this failsafe. Replacing byte thresholds with token thresholds
would affect persistence and still would not measure the runtime's resident state.

## Cost

- Healthy path: target only failure classification after a failure; no additional
  inference, storage reads, workspace observation, or native-session creation on
  successful turns. This is a design target to verify, not a performance result.
- Recovery path: local packet construction/token estimation, effect reconciliation
  where required, one native-context creation, and at most one additional execution
  attempt. A fresh context may lose cache reuse and cause file rereads. It is not
  free, but it is confined to an already failed turn.
- Diagnostics: record failure category, recovery attempt, estimated packet tokens,
  reserve, configured limit, and known observed effects. Report finish reason or
  pre/post-extraction text lengths only when the native protocol supplies them.
  The integration cannot manufacture unavailable summarizer diagnostics.

## Scope classification

- Necessary now: precise failure normalization; recovery feasibility checks;
  bounded deterministic continuation; explicit per-adapter fresh-context semantics;
  shared retry accounting; observed activity and E2E regression coverage.
- Useful later: proactive budgeting based on reliable native resident-context
  measurements; optional model-generated summaries if deterministic recovery proves
  inadequate. Absence of a native compactor must be established explicitly, not
  inferred from missing metadata or one error.
- Unnecessary for this correction: a Qwen fork, runtime bundle patching, a proxy that
  rewrites native model requests, new storage policy, a global context reduction,
  another background summarization service, or a redesign of Thinking/history UI.

## Validation required before implementation can be called complete

Use browser/API E2E with fake external boundaries. Cover successful existing flows
and unchanged native-session/provider-call counts for all harnesses; Native's
existing compaction; exact-context failure and successful recovery; second failure
without another reset; fresh-session setup failure; missing native overhead;
unresolved approval/action; cancellation; committed file/process effects; saved
transcript restoration; role/model isolation; and Supervisor/Codex retry interaction.

Then run formatting, zero-warning Release build, relevant existing E2E, intended
diff review, and process cleanup checks. Real inference is not needed for these
contract tests; it remains a separate acceptance step if requested.

The initial assessment was based on source inspection. The implementation's
browser/API results and commands are recorded below; fake-provider results do not
constitute a real-model reproduction or a performance benchmark.

## Validation results — 2026-09-17

- Release build passed with zero warnings and errors:
  `dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false
  -p:BaseOutputPath=bin/context-failsafe/`.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include`
  the ten implementation and five test/fake files listed above: passed.
- Browser/API E2E: **51/51 passed, zero skipped**. This includes 17 new failsafe
  cases and 34 existing regression cases. Coverage exercises recovery and repeated
  failure in all four external adapters; canonical constraints and committed file
  effects; failure during replacement setup and during recovery transport;
  unresolved native action; oversized required continuity; cancellation; independent
  conversation/daemon isolation; shared Supervisor retry accounting; native-session
  hydration/deltas, queue recovery, eviction, existing Codex transient recovery,
  Claude approval, Native coordinator compaction, and lossless history restoration.
- Test command: `dotnet test
  tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release
  --no-build --no-restore -m:1 -p:BaseOutputPath=bin/context-failsafe/ --filter ...
  --logger "trx;LogFileName=context-failsafe-final.trx"`, with the test API and four
  fake-harness path overrides pointing to the isolated output above. The selected
  cases and results are recorded in
  `tests/AgenticRouter.EndToEndTests/TestResults/context-failsafe-final.trx`.
- Intended diff and `git diff --check`: passed. Existing unrelated work was retained.
  The user's running application was not rebuilt in place or restarted. No real
  model/provider inference ran; these are deterministic external-protocol tests,
  not a claim that native compaction or the original empty summary was repaired.
  After validation, no test API, testhost, or fake-harness processes remained;
  the original application process (PID 1708) remained running.
- Native overhead remains estimated. Missing mandatory continuity, an envelope
  that cannot fit, unresolved work, or insufficient native-session capacity can
  still prevent automatic recovery; the implementation preserves reviewable
  evidence rather than silently dropping required content.
