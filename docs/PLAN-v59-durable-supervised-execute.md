# PLAN v59: Durable Supervised Execute (local-only)

## Status

Architecture approved on 2026-08-26. Milestone 0 implementation and focused
deterministic validation are complete; manual approval is pending. Later milestones
remain behind their explicit validation gates.

## Outcome

Add an opt-in Execute strategy for large local tasks that combines two properties:

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

- an automatic recommendation that a large goal use supervision;
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
executionStrategy: direct | supervised
```

- `direct` is the compatibility default.
- An exact `/supervisor` prefix at the beginning of an Execute message is a convenience
  alias for `executionStrategy=supervised`.
- The directive is removed from the objective sent to the specialist.
- `/supervisor` in Chat returns a typed error and never silently switches to Execute.
- Supervised activation and its fixed route are visible in activity and review.
- Version 1 does not silently infer that an objective is large.

### Restart policy

Every supervised run has an explicitly selected policy:

```text
resumePolicy: manual | auto-safe
```

- `manual` is the default.
- `auto-safe` is an explicit unattended/overnight authorization for this bounded local
  run.
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

The watchdog measures progress by new Host facts, acceptance coverage, validation
state, or a materially different recovery strategy. Stream tokens and fluent worker
prose do not count as progress.

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

Intermediate supervisor/worker prose is activity evidence and never visible assistant
answer content. Exactly one final answer and one terminal event are emitted.

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

- `/supervisor` or explicit strategy activation in Execute;
- explicit `manual` / `auto-safe` restart choice;
- local-only route disclosure;
- active role, item, completed/total count, context count, checkpoint durability, and
  run state in activity/review;
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

- direct Chat/Execute remain unchanged;
- no strategy means no durable supervised state;
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

- Architecture: approved on 2026-08-26. Milestone 0 manual acceptance: pending.
- Milestones 1-3: each requires prior milestone deterministic/manual approval.
- Milestone 4: requires explicit permission for the exact real local model and harness.

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
