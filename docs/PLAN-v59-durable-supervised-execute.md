# PLAN v59: Durable Supervised Execute (local-only)

## Status

Architecture and Milestone 0 were approved on 2026-08-26. Milestone 1 was accepted by
the user's explicit authorization to proceed to Milestone 2. Milestone 2 was accepted by
the user's explicit authorization to proceed to Milestone 3 on 2026-08-27. Milestone 3
was accepted by the user's explicit authorization to continue through Milestone 4.
Milestone 4 implementation and authorized real-local acceptance are complete.

## Outcome

Add an automatically selected, explicitly overridable Execute strategy for large local tasks that combines two properties:

- **Durable Execute** keeps a user-started execution owned by the Host rather than by
  an HTTP/SSE connection, persists safe boundaries, and reconstructs state after a
  process restart.
- **Supervised Execute** alternates one selected local specialist serially between a
  focused supervisor context and recoverable worker contexts so the task continues to
  converge on the original objective.

The same fixed local `model × harness` pair performs every supervisor and worker turn.
Only one context may run at a time. These contexts are not subagents, do not execute
concurrently, do not delegate recursively, and have no independent authority.

The Host remains the sole authority for trusted-workspace policy, approvals, action
identity, effects, conflicts, validation, persistence, recovery budgets, and terminal
state.

Direct Execute remains the default and does not pay supervision, durable-run, or
checkpoint overhead.

## Mandatory local-only invariant

Durable Supervised Execute supports local agents only.

- The fixed provider must be `ollama-local`.
- The selected model must resolve to the configured local Ollama endpoint.
- Auto Model × Harness may consider only currently available local candidates.
- Every harness adapter must propagate the exact local model, local provider, local
  endpoint, and trusted workspace.
- A harness that cannot prove this mapping is unavailable for supervised execution.
- Cloud-qualified models are rejected before a supervised run is created.
- There is no cloud fallback, provider substitution, or mid-run route switch.
- Supervisor, worker, retry, restart recovery, and final verification all use the same
  fixed local route.

This is a product boundary, not a cost estimate or preference.

## Necessary now, useful later, and unnecessary

### Necessary now

- explicit Supervised Execute activation;
- Host-owned execution independent from browser/SSE lifetime;
- a fixed local `model × harness` route;
- one supervisor plus worker contexts created only on demand;
- a simple ordered work queue;
- focused supervisor inspection of actual current artifacts;
- evidence-backed worker acceptance or rejection;
- typed retry classification and no-progress detection;
- atomic checkpoints at meaningful boundaries;
- write-ahead action identity and post-effect reconciliation;
- explicit `manual` or `auto-safe` restart policy;
- attach/replay/cancel/recover APIs and observable terminal state;
- deterministic fake-provider browser/API validation.

### Useful later

- measured per-model × harness recommendations for the direct-plan threshold;
- user reordering of pending work;
- measured native-session/KV-cache optimizations;
- a dedicated operating-system service or application watchdog.

### Probably unnecessary

- concurrent workers;
- a different supervisor model;
- a resident coordinator;
- recursive delegation;
- a DAG or general workflow builder in version 1;
- Temporal, LangGraph, AutoGen, CrewAI, or another execution platform dependency;
- distributed execution or scheduling;
- persistence of hidden reasoning or provider KV caches;
- an LLM judge that can override Host facts.

## Activation and continuation contract

### Execute strategy

Add an additive request field:

```text
executionStrategy: auto | direct | supervised | autonomous
```

- `auto` is the Execute default; Chat remains direct.
- `direct` is an explicit override and `/direct` is its convenience alias.
- `autonomous` is an explicit full-auto Supervisor override for unattended work. It
  delegates every approval the user could grant to the Supervisor, but it cannot
  cross trusted-workspace escapes, protected paths, stale conflicts, or any other
  hard Host boundary.
- An exact `/supervisor` prefix at the beginning of an Execute message is a convenience
  alias for `executionStrategy=supervised`.
- The directive is removed from the objective sent to the specialist.
- `/supervisor` in Chat returns a typed error and never silently switches to Execute.
- Supervised activation and its fixed route are visible in activity and review.
- `execution.maxDirectPlanSteps` is validated and configurable in Settings > General;
  its default is 5, so six or more structured/accepted plan steps activate supervision.
- Auto may select supervision before inference from an explicit structured objective,
  after the Host accepts or revises an oversized specialist plan, or at a verified
  recovery boundary after context/resource starvation.
- A late takeover persists a bounded Host snapshot of the accepted plan, verified file
  effects, completion state, and validation state. The supervisor inspects current
  artifacts, preserves useful verified work, and may replace the plan with smaller work.
- Autonomous never waits for a discretionary user decision. If a supervisor response
  asks the user, the Host performs one bounded corrective decision turn; a repeated
  request is a typed blocked result rather than an unattended pause.

### Restart policy

Every supervised run has an explicitly selected policy:

```text
resumePolicy: manual | auto-safe
```

- `manual` is the default.
- `auto-safe` is an explicit unattended/overnight authorization for this bounded local
  run.
- Autonomous selects `auto-safe` whenever durable history is enabled. Without durable
  history it remains a volatile run that can survive client disconnects, but not a Host
  restart.
- Both policies allow the run to continue when the browser/SSE disconnects while the
  Host process remains alive.
- After a Host process restart, `manual` waits for the user.
- After a Host process restart, `auto-safe` resumes only when every deterministic
  safety predicate in this plan passes.
- The policy expires when the run reaches a terminal state or is discarded.

The existing approval policy remains authoritative inside the run:

- `auto` permits validated mutations without duplicate confirmation;
- `ask` pauses whenever a covered mutation requires approval;
- neither policy crosses trusted-workspace or hard security boundaries.

Autonomous adds a run-internal approval authority, not a public general-purpose
approval policy. It skips every approval wait that the user could resolve, including
process, deletion, and mutable Git approvals, only after the same Host validation and
effect-boundary checks have passed. A direct or ordinary supervised request cannot
inject this authority through `approvalPolicy`.

The run persists the approval policy selected at creation because unattended restart
needs a bounded authority contract. It cannot be changed silently on resume.

## Host-owned execution and attach/detach

The execution session, not SSE, owns the task:

```text
Browser / API client
  -> start run
  -> receive run ID
  -> attach to events after sequence N

Host-owned durable run
  -> fixed local route
  -> checkpoint
  -> supervisor
  -> worker
  -> watchdog
  -> harness
  -> exactly one terminal state
```

Closing/reloading the browser or losing the SSE connection cancels only the event
subscription. It does not cancel the run.

The Host owns a cancellation token per run. Cancellation occurs only through explicit
run cancellation, application shutdown handling, a terminal policy decision, or an
unrecoverable infrastructure failure.

The existing `BenchmarkLiveRunCoordinator` provides a proven local pattern for:

- singleton Host ownership;
- a scoped execution service created independently from the request;
- bounded events with monotonic sequence IDs;
- reconnect using a cursor and `Last-Event-ID`;
- explicit cancellation;
- exactly one terminal event;
- browser disconnection without run cancellation.

Durable Supervised Execute should reuse this pattern, not the benchmark domain types.
It additionally requires intermediate persistence, approvals, workspace reconciliation,
and restart recovery.

If the Agentic Router process stops, work cannot continue until the application starts
again. Operating-system auto-restart/service installation is outside this milestone.

## Fixed route contract

The Host resolves the route once before the first supervisor turn and records:

- provider ID (`ollama-local` only);
- exact model ID and installed digest;
- harness ID and observed version;
- configured local Ollama endpoint identity;
- workspace profile ID and canonical path identity;
- approval and restart policies;
- explicit/manual or Auto Model × Harness routing evidence.

Every context dispatch must match this route. Missing or changed route components block
manual resume and fail the `auto-safe` predicate. The Host never substitutes another
local model or harness to obtain progress.

## Runtime flow

```text
User starts Supervised Execute
  -> validate strategy, policies, local-only route, and trusted workspace
  -> create Host-owned run and initial checkpoint
  -> return run ID; browser subscribes to events
  -> supervisor decomposes the goal into an ordered queue
  -> worker executes one bounded item through the existing specialist tool loop
  -> Host validates actions and proves effects
  -> worker claims completion or reaches a failure boundary
  -> item becomes verifying
  -> supervisor reads the actual current artifacts
       -> accept with current evidence, or
       -> reject with exact discrepancy and focused corrective brief
  -> repeat until complete, blocked, cancelled, or budget-exhausted
  -> Host evaluates final completion gates
  -> persist final review and emit exactly one terminal event
```

The supervisor runs only at meaningful boundaries:

- initial decomposition;
- worker completion claim;
- focused accept/reject verification;
- repeated failure or no-progress;
- discovery that invalidates pending work;
- approval/user-decision boundary;
- context exhaustion or reset;
- claimed global completion.

It does not run after every file read, edit, tool result, or stream delta.

## Simple ordered work queue

Version 1 uses an ordered queue, not a DAG.

Each work item contains only:

- Host-assigned ID;
- objective;
- acceptance criteria;
- status;
- attempt count;
- worker context ID;
- current evidence references;
- concise failure/correction state.

Work-item states:

```text
pending -> active -> verifying -> completed
                           \----> blocked
```

- Only one item may be `active` or `verifying`.
- Pending item order is authoritative for version 1.
- Replanning may replace the remaining pending suffix while retaining an audit event.
- Completed items and their evidence are immutable.
- Worker actions are attributed automatically to the active item by the Host.
- No title/path inference or model-provided plan-step binding is required.
- Existing optional worker-local plans remain optional and do not redefine the queue.

The existing maximum plan-step and plan-revision settings provide the initial item and
replan bounds. Supervisor transitions are bounded to twice the maximum work-item count.
Existing per-worker tool and recovery budgets remain authoritative.

## Logical context registry

The registry contains one supervisor plus worker contexts created only when dispatched.
It does not allocate all possible workers in advance.

Context states:

- `active`;
- `suspended`;
- `completed`;
- `abandoned`.

Each context stores:

- logical context ID and role;
- assigned work item;
- monotonic context revision;
- last synchronized run revision;
- compact role checkpoint;
- latest typed failure and recovery disposition;
- runtime native-session availability as non-authoritative evidence;
- lifecycle timestamps.

Only one context may be active. A context reset never resets Host action, approval,
recovery, or safety budgets.

Within one process, an adapter may reuse its native session. After process restart,
version 1 reconstructs a fresh runtime context from the logical checkpoint and current
Host facts. It does not claim exact restoration of hidden reasoning, KV cache,
subprocess state, or provider-native sessions.

## Focused supervisor verification

The worker's completion response is only a claim. It moves the item to `verifying`.

The supervisor receives bounded verification capabilities:

- workspace list/search/file-info/read;
- current changed-file inventory, hashes, diffs, conflicts, and action facts;
- acceptance criteria and evidence state;
- current validation profile/result;
- a Host-governed request to run the configured validation profile;
- submission of one typed supervisor decision.

It does not receive file mutation, arbitrary process, Git mutation, or unrestricted
shell capabilities.

Canonical supervisor decisions:

- `dispatch_work`;
- `accept_work`;
- `reject_work`;
- `replace_pending_work`;
- `request_validation`;
- `await_user`;
- `complete_goal`;
- `stop_blocked`.

`accept_work` must cover each required acceptance criterion with current evidence.
`reject_work` must include the evidence revision, exact discrepancy, and a bounded
corrective brief for the worker.

Example:

```text
Criterion: index.html must display "hello world today"
Worker claim: delivered HTML with "hello world"
Supervisor reads index.html at observed hash H1
Supervisor rejects: required word "today" is absent, evidence read-H1
Worker corrects; Host observes H2
Supervisor reads H2 and accepts the criterion with evidence read-H2
```

The Host proves which bytes/revision the supervisor read. The supervisor evaluates
semantic fit. Neither model statement proves completion by itself.

## Worker brief and outcome envelope

A worker receives:

- original objective and applicable instructions;
- fixed local route and workspace identity;
- active item and acceptance criteria;
- relevant completed-item summaries and Host facts;
- current changed-file inventory and conflicts;
- explicit user decisions;
- remaining budgets.

At a boundary, the Host supplies the supervisor with:

- item/context IDs;
- worker claim;
- typed action results;
- observed changed paths and hashes;
- validation actually run and results;
- denials, approvals, conflicts, and warnings;
- material-progress indicator;
- open acceptance criteria;
- remaining budgets.

Raw tool chatter, unrestricted process output, full file contents, hidden prompts, and
hidden reasoning are not copied into the durable checkpoint. During verification the
supervisor reads bounded current artifacts directly through the Host.

Declared evidence paths are bounded by the existing configurable
`execution.maxTrackedFilesPerSession` value rather than a separate fixed item limit.
Every declared path is retained; additional review-only paths fill remaining capacity,
and any omitted review-path count is explicit in the evidence envelope. File-content
inlining remains bounded to 32 KiB per file and 96,000 characters total so increasing
the path count does not create an unbounded model-context payload.

## Retry, watchdog, and no-progress rules

Failures are classified before retry:

### Automatically retryable within budget

- typed transient local-provider transport failure before a governed effect;
- typed harness startup/readiness failure whose retry changes runtime state;
- read-only operation that is idempotent and has no ambiguous result;
- deterministic rehydration after context exhaustion.

### Requires reconciliation before retry

- a mutation request was sent but its effect result is missing;
- a process may have started but terminal identity/result is unknown;
- the workspace changed after the last checkpoint;
- a stale expected hash conflicts with the current file;
- a harness terminated during a governed operation.

### Never retry unchanged

- policy/security denial;
- rejected or pending approval;
- malformed deterministic protocol output;
- invalid path/tool/arguments;
- identical strategy with no new fact;
- exhausted budget.

A malformed supervisor decision is not replayed unchanged. The Host may make exactly
one bounded canonical-output recovery turn that explicitly forbids tools, repeated
analysis, prose, and Markdown fences, and asks for the already-established decision as
one final JSON object. The recovery is emitted as
`supervision.turn-canonical-recovery`. If that materially different turn is also
malformed, the run blocks. Hidden reasoning is never promoted into an authoritative
decision, even when it happens to contain JSON.

A typed recoverable harness failure during a supervisor turn receives exactly one
materially different retry in a fresh provider-native session. The retry preserves the
logical context, committed Host effects, and budgets, requires concise canonical output,
and is emitted as `supervision.turn-harness-recovery`. Repetition blocks with the
original typed harness code retained as the run wait code and terminal diagnostic code.

The watchdog measures progress by new Host facts, acceptance coverage, validation
state, or a materially different recovery strategy. Stream tokens and fluent worker
prose do not count as progress.

While a turn remains active, the Host emits bounded transient role updates through the
ordinary Execute narrative surface. The cadence is the smaller of 30 seconds or one
third of `runtime.generationTimeoutSeconds` and is driven by a Host timer independent
of provider/harness frames. These messages describe the current
Supervisor/Worker activity but do not reset the watchdog or enter the durable
checkpoint. Reasoning remains transient and continuously streamed for the full turn;
the in-memory tail is bounded without imposing a user-visible publication cutoff.

In Autonomous, the first critical inactivity event at `2T` interrupts only the active
model turn. If no governed action is unresolved, the Supervisor retries once with an
explicit materially different recovery brief. A repeated stall, or any prepared,
awaiting, in-flight, or ambiguous governed action, blocks replay instead of weakening
Host safety. Other execution strategies keep the existing warning-only behavior.

Repeated no-progress returns control to the supervisor. The supervisor may narrow the
item, issue a focused correction, reconstruct a worker context, replace pending work,
await the user, or stop blocked.

## Write-ahead action and reconciliation

Every governed mutation has a stable Host action ID and durable phase:

```text
prepared -> in-flight -> committed
                    \-> abandoned/ambiguous
```

Before sending a mutation, the Host checkpoints the prepared action, expected target
identity/hash, approval evidence, and intended effect class. After independent effect
verification, the Host checkpoints the committed result.

If a crash leaves an action `in-flight`, the Host never replays it blindly. It inspects
the workspace/process facts:

- observed intended effect with compatible hashes -> record reconciled completion;
- provably absent effect and idempotent safe retry -> permit a new attempt ID;
- conflicting or ambiguous state -> await user.

## Durable checkpoint

### Storage

When local history is enabled, write a versioned JSON checkpoint atomically under the
application data directory:

```text
workspaces/{workspaceId}/supervision/{conversationSessionId}/{runId}.json
```

The checkpoint is not stored in the trusted workspace. IDs are validated before path
composition. Writes use temporary file plus atomic replace and a monotonic revision.

When history is disabled:

- supervised execution can continue while the Host process lives;
- no durable objective/context checkpoint is written;
- `auto-safe` restart is unavailable;
- the UI clearly reports volatile durability.

### Contents

- schema and monotonic revision;
- workspace, conversation, request, execution, and run IDs;
- normalized objective and SHA-256 digest;
- fixed local route, approval policy, and restart policy;
- run state and active phase;
- ordered work queue;
- logical context checkpoints;
- Host fact ledger and changed-file metadata;
- prepared/in-flight/committed action records;
- acceptance/evidence references;
- validation/conflict/approval state;
- budgets, counters, timestamps, and integrity metadata.

It excludes:

- secrets;
- hidden/system prompts and hidden reasoning;
- raw provider/harness payloads and stream deltas;
- unrestricted tool arguments/process output;
- full file contents and rollback blobs;
- unrestricted absolute paths.

Rollback and final bounded review remain in the existing conversation persistence
contracts.

### Durable boundaries

Checkpoint after:

- run creation and fixed-route resolution;
- accepted supervisor decision;
- context activation/suspension/completion/abandonment;
- prepared mutation before dispatch;
- independently observed mutation result;
- validation/conflict/approval/user-wait transition;
- terminal state before terminal event publication.

Do not checkpoint reasoning/response deltas or unchanged progress.

Checkpoint failure emits `supervision.checkpoint-degraded`. The run may continue in
memory, but restart durability is visibly unavailable until a later successful write.

## Event journal and public lifecycle

Each Host-owned run retains bounded ordered events with monotonic sequence IDs.

Required operations:

- start a supervised run and return its ID/event URL;
- get the current sanitized run view;
- subscribe after query cursor or `Last-Event-ID`;
- explicitly cancel;
- list interrupted/recoverable runs for the active workspace;
- explicitly resume a manual run;
- discard retained recovery state.

SSE is terminal whenever the run state is terminal, including when the terminal event
is replayed. A reconnect first receives retained events after its cursor and then waits
for new events.

Intermediate Supervisor/Worker prose and bounded reasoning are transient presentation
events, not visible assistant-answer content and not durable checkpoint state. Exactly
one final answer and one terminal event are emitted.

## Startup and `auto-safe` recovery

Startup performs no inference until recovery policy is evaluated.

For each non-terminal durable checkpoint, the Host validates schema/integrity and marks
the prior process ownership interrupted.

`auto-safe` may resume only when all are true:

- checkpoint revision and integrity are valid;
- history/durability remains enabled;
- the exact workspace profile/canonical path is available;
- repository instructions reload successfully;
- exact `ollama-local` model digest, endpoint, harness, and harness version match;
- no tracked file drift or unresolved conflict exists;
- no approval/user decision is pending;
- no governed action/process is `in-flight` or ambiguous;
- no other run owns the workspace execution slot;
- remaining budgets permit progress.

Otherwise the run becomes `interrupted-recoverable` or `awaiting-user` with an exact
typed reason.

After successful reconciliation the Host reconstructs fresh runtime contexts, sends a
crash-reconciliation envelope to the supervisor, and continues from the last committed
boundary.

## Core implementation boundaries

### Durable run coordinator

```text
IDurableSupervisedRunCoordinator
  Start
  GetView
  Subscribe(afterSequence)
  Cancel
  Resume
  Discard
```

It owns run cancellation, event ordering, active execution task, checkpoint calls, and
exactly-once terminal publication. It creates service scopes independently from HTTP
requests.

### Shared context-turn runner

All roles continue through `IAgentHarness`; Native remains behind
`NativeHarnessAdapter`.

```text
IExecutionContextTurnRunner
  input: fixed local route, logical context, role, prompt projection,
         capability profile, Host session
  output: normalized activity, role result, Host fact delta, context usage
```

Roles:

- `direct`: existing behavior;
- `supervisor`: bounded read/verification plus decisions;
- `worker`: existing Execute capability policy scoped to the active item.

`ChatStreamService` keeps request/routing composition but must not own the durable state
machine. The coordinator invokes the turn runner serially.

## User-visible behavior

- one compact, visually continuous split-button next to Send with Auto / Direct /
  Supervisor / Autonomous strategies, plus `/direct` and `/supervisor` aliases;
- explicit `manual` / `auto-safe` restart choice;
- local-only route disclosure;
- active role, item, completed/total count, context count, checkpoint durability, and
  run state in activity/review;
- reuse of the ordinary Execute surfaces: the existing `Plan execution` panel for the
  Supervisor queue, the existing work narrative for periodic role updates, existing
  action cards and inactivity alert, and existing reasoning blocks;
- the existing execution-session header shows the fixed model, harness, strategy,
  active role, queue progress, phase, and approval policy for supervised runs;
- visible plan bullets use one short bounded clause while retaining the complete work
  objective as hover text and as the unchanged worker input; an open docked plan has a
  viewport-bounded scrolling body and collapses when the user clicks outside it;
- reasoning remains available in every Execute strategy; blocks created under active
  supervision start collapsed, while Direct keeps its existing open/close behavior;
- browser reload/reconnect attaches to the existing run;
- styled Resume/Discard UI for manual or unsafe restart cases;
- explicit Cancel independent from SSE;
- no browser-native `alert`, `confirm`, or `prompt`;
- no separate workflow-builder page.

## Implementation milestones

### Milestone 0 — Durable state and attach/detach foundation

No supervisor, worker, Ollama inference, GPU work, or external harness execution.

1. Add additive strategy/restart-policy and sanitized run-view contracts.
2. Add exact directive/policy validation and mandatory local-only route validation.
3. Add only the bounded run, fixed-route, lifecycle-event, and transition records
   exercised by this milestone. Work-item/context state waits for Milestone 2 and
   write-ahead action/evidence state waits for Milestone 3.
4. Add atomic `ISupervisionCheckpointStore` v1 with identity, route, policies,
   revision, lifecycle events, privacy, corruption, and cleanup handling. Evolve the
   versioned schema only when later milestones exercise their state.
5. Add Host-owned live-run state with sequence cursor, replay, explicit cancellation,
   terminal-once behavior, and browser-disconnect independence, following the existing
   benchmark pattern.
6. Add startup recovery discovery and explicit manual resume/discard state transitions;
   `auto-safe` evaluates predicates but does not invoke a model in this milestone.
7. Add API/browser surfaces sufficient to observe prepared/interrupted run state and
   checkpoint durability without exposing a partially executable supervisor feature.
8. Add deterministic Playwright browser/API coverage.
9. Run formatting, Release build, focused/full E2E, `git diff --check`, and intended
   diff inspection. Stop for manual approval.

### Milestone 1 — Shared specialist context-turn boundary

1. Extract the smallest reusable context-turn runner from the current selected
   specialist path.
2. Route unchanged direct Native and external Execute through it.
3. Preserve streaming, approvals, capability policy, effect proof, context usage,
   completion, and public behavior.
4. Validate deterministic parity for every registered harness through its existing
   fake external boundary.
5. Stop for manual approval.

### Milestone 2 — Supervisor/worker convergence loop

1. Add the coordinator state machine over the shared turn runner.
2. Add focused supervisor read/verification and typed decisions.
3. Dispatch workers serially using the fixed local route.
4. Add mandatory claim -> verifying -> inspect -> accept/reject behavior.
5. Add retry classification, watchdog, no-progress, context reconstruction, queue
   replacement, and Host-authoritative completion.
6. Keep intermediate prose out of the visible assistant answer.
7. Validate deterministically and stop for manual approval.

### Milestone 3 — Durable restart and `auto-safe` continuation

1. Persist all durable boundaries including write-ahead governed actions.
2. Reconcile workspace, route, process/action state, instructions, and budgets on
   restart.
3. Resume `auto-safe` only from a fully proven committed boundary.
4. Expose exact wait reasons and manual Resume/Discard when a predicate fails.
5. Restart the deterministic test Host mid-goal and complete the recovered run.
6. Validate full browser/API reconnect/replay/cancel/terminal behavior and stop for
   manual approval.

### Milestone 4 — Authorized local real acceptance

Run only after explicit permission for the exact local model/harness matrix:

1. a large Native Ollama goal;
2. a supported external harness using the exact local Ollama route;
3. a focused supervisor rejection/correction cycle;
4. a recoverable transient failure;
5. browser disconnect/reconnect while the Host continues;
6. Host restart with `manual` and `auto-safe` policies;
7. route/workspace/action ambiguity that correctly waits for the user;
8. final process/server cleanup and independent artifact validation.

No cloud request is permitted in any milestone.

## Deterministic acceptance matrix

### Compatibility and local-only

- direct Chat and explicitly forced Direct Execute remain unchanged;
- no Execute strategy means Auto; up to the configured step limit remains direct;
- Autonomous always creates a supervised run and never pauses for an approvable action
  or discretionary supervisor question;
- Autonomous destructive/process/Git actions still pass through the ordinary Host
  validators, and a hard workspace or policy rejection remains terminal or recoverable
  only through a materially different permitted action;
- structured or accepted plans above the limit create one visible supervised run;
- late takeover preserves and exposes prior verified Host effects;
- cloud-qualified supervised routes are rejected before run creation;
- Auto Model × Harness uses only available local candidates;
- every role turn preserves exact local model/digest/endpoint/harness/workspace;
- no fallback or route switch occurs.

### Host ownership

- start returns a run ID before event attachment;
- disconnecting SSE leaves the Host run unchanged;
- reconnect replays only events after the effective cursor;
- explicit cancel reaches the run-owned cancellation token;
- exactly one terminal event is retained and replayed;
- terminal subscriptions end even when terminal state was already retained.

### Queue, contexts, and verification

- one active/verifying item and one active context at most;
- workers are created only on dispatch;
- worker claim moves to `verifying`, never directly to `completed`;
- supervisor reads the current artifact revision;
- stale evidence cannot accept work;
- rejection cites current evidence and exact correction;
- corrected revision is read again before acceptance;
- false completion cannot override Host facts.

### Retry and reconciliation

- transient read/provider failures retry within budget;
- deterministic failures do not repeat unchanged;
- ambiguous mutation/process state never replays blindly;
- no-progress excludes stream tokens/prose;
- context reset preserves all Host budgets and facts;
- write-ahead in-flight action blocks `auto-safe` until reconciled.

### Persistence and recovery

- history disabled writes no checkpoint and disables restart auto-resume;
- checkpoint writes are atomic, bounded, monotonic, and schema-validated;
- forbidden sensitive fields are absent;
- startup recovery performs no model call in Milestone 0;
- `manual` waits after restart;
- `auto-safe` requires every predicate;
- drift, route change, pending approval, or ambiguous action waits for the user;
- final review persists before checkpoint cleanup;
- conversation deletion/retention removes related checkpoints.

### Validation discipline

- formatting verification passes;
- Release build has zero warnings/errors;
- focused and complete Playwright browser/API E2E pass;
- `git diff --check` passes;
- intended diff contains no unrelated changes;
- all work-started test hosts, fake providers, browser processes, watchers, and servers
  are stopped and verified.

## Non-goals

- cloud agents or provider calls;
- parallel/recursive agents;
- a resident coordinator;
- automatic route/model/harness changes;
- unrestricted operating-system/filesystem access;
- provider-native session state as the only recovery source;
- persistence of hidden reasoning or KV cache;
- operating-system service installation or self-restart;
- a general scheduler/workflow builder;
- external workflow-framework dependencies.

## Approval gates

- Architecture and Milestone 0: approved on 2026-08-26.
- Milestone 1 deterministic validation and manual acceptance: approved by the user's
  explicit authorization to implement Milestone 2 on 2026-08-26.
- Milestone 2 deterministic validation and manual acceptance: approved by the user's
  explicit authorization to implement Milestone 3 on 2026-08-27.
- Milestone 3 deterministic validation and manual acceptance: approved by the user's
  explicit authorization to continue through Milestone 4 on 2026-08-27.
- Milestone 4 authorized local real acceptance: complete on 2026-08-27.

## Milestone 0 evidence — 2026-08-26

- No Ollama generation, real harness execution, GPU work, download, or cloud request
  was performed.
- Mandatory local-only route preparation uses the concrete local Ollama client rather
  than the provider aggregator and rejects cloud-qualified models before discovery.
- Host-owned prepared runs support sanitized view, cursor/`Last-Event-ID` replay,
  browser observation, explicit cancellation, terminal-once behavior, manual resume,
  `auto-safe` predicate evaluation, discard, and conversation-linked cleanup.
- Checkpoint v1 is atomic, bounded, monotonic, integrity-checked, reparse-safe, and
  contains only identity, exact local route, policies, lifecycle, and bounded events.
  Work/context/action/evidence schemas were intentionally not scaffolded early.
- Focused Playwright browser/API E2E: 6 passed, 0 failed.
- Isolated Release solution build: 0 warnings, 0 errors. The normal Release apphost was
  locked by a pre-existing AgenticRouter.Api process, so validation used an isolated
  `%TEMP%` output without stopping that process.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes`: passed.
- Complete retained E2E: 335 passed, 2 failed, 0 skipped. One persistent unrelated Git
  UI expectation observed `Repository initialized on main...` instead of the later
  configuration-save status in the already-dirty UI work; one Cancel-button timing
  test passed when rerun alone. No Durable Supervised Execute test failed.
- Process cleanup verification found no retained test API, fake provider, harness, or
  browser process. The pre-existing AgenticRouter.Api process was left running.

## Milestone 1 evidence — 2026-08-26

- Added the typed `IExecutionContextTurnRunner` boundary over the existing
  `IAgentHarness` seam. Its request carries the logical context identity, role,
  prepared provider/model/harness/endpoint/workspace route, prompt projection, Host
  capability profile, and authoritative execution session.
- The runner rejects route/session divergence for provider, local endpoint, model,
  harness, workspace, direct-context identity, and approval policy before dispatch.
- Unchanged direct Native, Codex, OpenCode, Qwen Code, and Claude Code Execute paths now
  enter through the same runner. Their existing specialist loops still own streaming,
  approvals, capability projection, effect proof, context usage, completion, and
  harness-specific translation.
- No new public stream event, API field, browser behavior, retry, fallback, or product
  policy was introduced.
- Focused deterministic browser/API parity plus the Milestone 0 regression matrix:
  11 passed, 0 failed. The parity selection covered every registered harness through
  its existing fake external boundary.
- Complete retained E2E: 336 passed, 1 failed, 0 skipped. The sole failure is the same
  pre-existing Git UI status mismatch recorded in Milestone 0 and reproduced alone;
  no context-turn or supervision test failed.
- Isolated Release solution build: 0 warnings, 0 errors. The normal Release apphost
  remains locked by the pre-existing AgenticRouter.Api process and was not stopped.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes`: passed.
- `git diff --check`: passed.
- No Ollama generation, real harness execution, GPU work, download, or cloud request
  was performed.
- Process cleanup verification found no retained test API, fake provider, harness, or
  browser process. The pre-existing AgenticRouter.Api process was left running.

## Milestone 2 evidence — 2026-08-26

- Added a Host-owned serial state machine over `IExecutionContextTurnRunner` with one
  focused supervisor context, worker contexts created only on dispatch, an ordered
  bounded queue, typed canonical decisions, bounded retries/transitions, no-progress
  evidence, queue replacement, and exactly one terminal state.
- Every supervisor and worker turn revalidates the prepared local model digest,
  Ollama endpoint, harness/version, workspace identity, and fixed route. Cloud-qualified
  input is rejected even when an Auto Model × Harness flag is also present; no fallback
  or route switch is permitted.
- The supervisor receives a minimal read/inspection/validation capability projection.
  It cannot mutate files, run arbitrary processes, or perform Git mutations. Worker
  actions retain the existing Host approval, trusted-workspace, effect-proof, and
  recovery loop.
- Worker prose is treated only as a claim. The Host captures bounded current file bytes,
  hashes, changed-file/review facts, conflicts, and validation state; the supervisor can
  accept or reject only the current evidence revision and must cover every criterion.
- The `/supervisor` Execute path starts the Host-owned run, exposes the exact resolved
  local route in activity, streams progress without intermediate answer prose, and emits
  only the accepted final answer plus one `response.completed` event.
- Focused M2 E2E: 4 passed, 0 failed. The full M0-M2 supervision class: 10 passed,
  0 failed. Coverage includes the requested `hello world` -> rejection -> focused
  correction to `hello world today` -> reread -> acceptance cycle, malformed canonical
  decision without identical retry, material no-progress detection and budget stop,
  local-only rejection, lifecycle replay, terminality, and restart-foundation
  compatibility.
- Final complete retained E2E: 340 passed, 1 failed, 0 skipped. The sole failure is the
  persistent unrelated Git UI status mismatch already recorded in Milestones 0 and 1.
  No M2 supervision test failed.
- Isolated Release solution build: 0 warnings, 0 errors. The normal Release apphost
  remained locked by the pre-existing AgenticRouter.Api process and was not stopped.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes`: passed.
  Changed-file whitespace verification and `git diff --check` also passed. A separate
  exploratory run at analyzer severity `info` reported repository-wide pre-existing
  suggestions and was not treated as the formatting gate.
- No Ollama generation, real harness execution, GPU work, download, or cloud request
  was performed.

## Milestone 3 evidence — 2026-08-27

- Checkpoint schema v2 now persists the ordered queue, logical contexts, fixed recovery
  budgets, repository-instruction identity, tracked file facts, turn ownership, and a
  bounded sanitized Host action ledger. Legacy v1 checkpoints are integrity-verified
  against their original shape and migrated in memory without inventing executable
  recovery state.
- Native and external-harness Host actions cross a write-ahead journal before effects:
  `prepared`, optional `awaiting-approval`, `in-flight`, and independently observed
  `committed`, `failed`, or `rejected`. Persisted action evidence contains canonical
  IDs, hashes, effect classes, and relative paths rather than raw arguments, outputs,
  file contents, hidden prompts, or provider payloads.
- Startup validates the exact local route, workspace, history, repository instructions,
  tracked artifacts, approvals, action effects, and remaining budgets before any
  inference. A failure while loading one recovery record is isolated to that run and
  becomes an exact awaiting-user reason rather than preventing API startup.
- `manual` restart reconstructs fresh logical contexts only after explicit Resume.
  `auto-safe` resumes only from a proven committed boundary. Pending approval,
  instruction drift, workspace drift, an unproven worker turn, or an ambiguous action
  remains user-visible and is never replayed blindly. Manual acknowledgement does not
  erase an unresolved ambiguous action from future `auto-safe` evaluation.
- The sidebar exposes a bounded recovery card with objective, fixed local
  `model × harness`, completed/total progress, policy, and exact wait reason. Resume
  calls Host reconciliation; Discard uses the styled application modal and preserves
  workspace files.
- Deterministic M0-M3 supervision E2E: 15 passed, 0 failed. Coverage includes process
  restart during an active goal, committed-boundary continuation, manual reconstruction,
  pending approval without new inference, workspace and instruction drift, schema/privacy,
  invalid-checkpoint startup isolation, browser Resume/Discard presentation, replay,
  cancellation, and exactly-once terminal state.
- Final complete retained E2E: 345 passed, 1 failed, 0 skipped. The sole failure is the
  same pre-existing Git UI status mismatch recorded in prior milestones; no supervision,
  checkpoint, recovery, or action-journal test failed.
- Isolated Release solution build: 0 warnings, 0 errors. The normal Release apphost
  remained locked by the pre-existing AgenticRouter.Api process and was not stopped.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes` and
  `git diff --check` passed.
- No real Ollama generation, real harness execution, GPU work, download, or cloud
  request was performed.

## Milestone 4 evidence — 2026-08-27

- The authorized real-local matrix used Ollama 0.33.0 and
  `qwen3.8:27b-q4_K_M` at digest
  `25b843619e944cd0ae6069f94ff4e5e26a16e109ccbc0a66a0f05979ed70098e`.
  Cloud providers remained disabled and no cloud request or fallback was permitted.
- A large Native goal completed four independently verified work items and produced a
  three-file responsive application whose button behavior passed a real browser check.
- The exact local Codex route completed normally and again after its owned app-server
  was terminated during a worker turn; continuation preserved model, provider,
  harness, workspace, and approval policy.
- A controlled workspace mutation during supervisor inference was rejected by a new
  Host-side fresh-evidence check. The worker reinspected, restored the exact bytes, and
  completed on attempt 2.
- Browser close/reload exposed and drove correction of a missing reattachment path.
  The saved conversation now attaches to retained run events, renders the accepted
  answer, and clears its interrupted flag after persistence.
- Real Host restarts verified explicit idle `manual` recovery and eligible
  `auto-safe` continuation. Route drift, workspace drift, and an action awaiting
  approval each remained `awaiting-user` with an exact typed reason and no blind replay.
- Exact run IDs, artifacts, hashes, failure injections, and accepted outcomes are
  recorded in `docs/v0.9.15-real-acceptance.md`.
- Final deterministic supervision E2E: 17 passed, 0 failed. Final complete retained
  E2E: 347 passed, 1 failed, 0 skipped. The sole failure is the unchanged pre-existing
  Git UI status race documented since Milestone 0; no supervision, reconnect,
  persistence, recovery, or harness test failed.
- Final Release build completed with 0 warnings and 0 errors. Formatting and intended
  diff checks completed before packaging.
