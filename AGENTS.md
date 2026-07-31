# AGENTS.md

## 1. Product Mission

Agentic Router is a local-first application that routes conversations and supervised software-development tasks to local Ollama models or explicitly configured cloud providers.

The product has two user-visible modes:

- **Chat** routes a turn to a configured expert and streams one continuous answer.
- **Execute** lets a coordinator inspect a trusted workspace, propose a host-owned plan, use a constrained set of local tools, request approvals when required, validate changes, and present a reviewable result.

The host, not the model, owns security boundaries, plan identity, approval decisions, retries, recovery budgets, persistence, and tool execution.

## 2. Instruction Priority

When instructions conflict, follow this order:

1. the user's current request;
2. this `AGENTS.md`;
3. an approved feature specification in the repository;
4. existing implementation patterns that do not conflict with the items above.

Do not expand scope to solve hypothetical future needs.

## 3. Current Technology and Scope

The implementation uses:

- .NET 10 and ASP.NET Core Web API;
- controllers for HTTP endpoints;
- Ollama Local plus optional Groq, Google AI Studio, and Cerebras providers;
- vanilla HTML, CSS, and JavaScript;
- Playwright for .NET with MSTest for browser-driven end-to-end tests;
- one local application instance;
- one local JSON settings store;
- one bounded local JSONL usage ledger;
- optional local conversation persistence;
- trusted workspace profiles and supervised local execution.

The application must work with one GPU, multiple GPUs, or provider-managed automatic device selection. Multi-GPU hardware is optional and must never be a runtime requirement.

## 4. Architecture and Authority

Keep responsibilities explicit:

- Controllers translate HTTP input and output only.
- Chat services coordinate turns, routing, streaming, and conversation context.
- Execution services own plans, approvals, local actions, validation, recovery, and session state.
- Each provider adapter owns its HTTP communication and provider-specific contracts.
- Settings and workspace-profile stores own validated local configuration.
- The browser renders host state and sends explicit user decisions; it is not an execution authority.

Use built-in dependency injection, options, logging, and `HttpClientFactory`. Keep business logic out of controllers and provider DTOs out of public application contracts.

The host must independently validate every proposed tool call. A model response is untrusted input even when it comes from a configured coordinator.

## 5. Routing, Coordination, and Model Roles

Intent classification, coordination, and expert inference are separate responsibilities.

- `routerModel` classifies the request.
- `coordinatorModel` maintains an Execute turn, selects tools, and reacts to tool results.
- Intent profiles resolve the expert model for Chat and specialist work.

Do not silently substitute one role for another. Model and device precedence must remain deterministic and visible in activity details.

Direct tool coordination is allowed only when the exact model identity has passed the host's behavioral tool-protocol conformance checks. Declared Ollama `tools` capability is evidence of availability, not proof of compatibility.

Tool-protocol failures must be typed and recoverable. Invalid XML, malformed or truncated JSON, missing native tool calls, Harmony/parser failures, and equivalent syntax failures must not be treated as generic provider failures. After a deterministic protocol failure, change strategy for the current turn instead of repeating an identical request.

## 6. Execute Mode

Execute mode is constrained to a user-approved trusted workspace.

Required behavior:

1. Resolve and validate the active workspace profile.
2. Inspect repository instructions and project metadata.
3. Create a host-owned plan with host-generated IDs.
4. Let the coordinator propose only registered structured tools.
5. Revalidate paths, arguments, policy, approvals, and limits in the host.
6. Record tool calls, results, file changes, validation, recovery, and terminal state.
7. Present changes for review and allow supported undo operations.

Supported file actions are structured operations such as listing, reading, searching, creating, writing, replacing, and applying patches. Process execution uses a separate structured contract and an allowlist policy. Never add a generic shell tool, free-form command arguments, or an unrestricted filesystem escape.

All workspace paths must be canonicalized and confined to the trusted root. Reject traversal, reparse-point escapes, protected paths, invalid encodings, oversized inputs, and stale file writes. Preserve external user changes and surface conflicts for review.

Approvals are host decisions. Policies may auto-approve explicitly safe operations, but ambiguous, sensitive, process, or destructive proposals must remain blocked or require the configured approval flow. Cancellation must stop active provider and process work.

### Safe Git delivery

Review changes may prepare an explicitly selected delivery for a valid Git repository inside the trusted workspace. The host may inspect bounded status, diff, log, and commit data, and may perform only structured staging, unstaging, commit, annotated-tag, current-upstream branch push, and exact-tag push operations.

The main interface exposes host-authoritative Git status and bounded Current session, Working tree, Staged, and Last commit diffs. A non-repository workspace may be initialized at the trusted root on `main`, but only in Execute mode after immutable explicit approval. Initialization must not create a commit, stage files, or add a remote. Repository identity edits are limited to explicitly approved, repository-local `user.name` and `user.email`; remotes are read-only and credential-bearing URL components must be sanitized.

Every Git write always requires immutable, action-specific user approval, including under the automatic approval policy. Pre-existing user changes remain separate and unselected by default. A commit requires an exact staged-set match and a passing validation bound to current file hashes, unless a permitted explicit override is approved. Commit, tag, branch-push, and tag-push facts remain distinct in session state.

Never expose arbitrary Git arguments, force push, amend, reset, checkout, switch, clean, stash, pull, merge, rebase, cherry-pick, revert, remote mutation, branch mutation, tag deletion, or history rewriting. Once a session delivery is committed, internal undo must not contradict or rewrite repository history.

## 7. Plans, Recovery, and Limits

Plans are host state, not opaque model documents. The model may propose objectives and normalizable titles; the host assigns stable plan and step IDs, validates dependencies, and records state transitions.

Recovery must be bounded and observable:

- classify failures by typed stage and reason;
- checkpoint enough state to continue safely;
- choose a materially different strategy after deterministic failures;
- respect retry, tool-call, iteration, elapsed-time, and recovery budgets;
- terminate with a useful blocked or failed result when safe progress is impossible.

Never hide retries, takeovers, fallbacks, or partial results from activity and audit state.

## 8. Configuration and Persistence

Keep configuration typed, validated, versionable, and backward-compatible when practical. Invalid settings must be rejected with field-level errors and must never be partially saved.

Configuration includes provider URL, router and coordinator models, intent profiles, model/device defaults, context sizes, timeouts, execution limits, approval policy, trusted workspaces, and persistence preferences.

Ollama Local context is Host-owned and role-specific. Resolve it from typed
router, resident-coordinator, specialist, primary, fallback, benchmark,
model-test, web-search-synthesis, and vision-request defaults plus an optional
exact provider/model/digest override. The global provider context remains a
ceiling, and the model-declared maximum is another ceiling. Send the effective
value only through native `/api/chat` `options.num_ctx`.

Request fit includes bounded message input, tool definitions and results,
image overhead, and reserved output. Grow context only through the configured
discrete ladder; reject an unfit request with a typed error rather than
silently truncating required execution state. Resident coordination receives
only the current objective, project context, specialist guidance, and bounded
current tool state.

The configured coordinator is the resident model. Preload it at 8,192 context
tokens by default, verify exact model, digest, and `context_length` through
`/api/ps`, and mark it ready only after verification. A mismatched resident is
unloaded, verified absent, and reloaded only while no request is active.
Settings changes, recovery eviction, and measurement must restore or explicitly
report the prior resident state.

Metadata analysis may use `/api/tags`, `/api/show`, `/api/ps`, and Ollama
version without loading a model. Real memory measurement always requires
explicit permission, bounded context candidates, active-request exclusion,
and restoration. Store measured evidence atomically under
`data/runtime-profiles/ollama-model-memory.json`, keyed by exact model digest,
Ollama version, hardware signature, role, context, and runtime signature.
Never export measured hardware records in portable YAML.

Memory recommendations distinguish configured, inherited, overridden,
metadata-derived, measured, and stale evidence. Surface shared-model and CPU
offload consequences. Do not infer or mutate Ollama parallelism, flash
attention, or KV-cache type when those values are unavailable.

When an intent primary resolves to a cloud provider, its configured fallback
must resolve unambiguously to an installed Ollama Local model. The Host may
switch once for an eligible timeout, provider outage, rate limit, quota
exhaustion, or supported transient failure. It must not fallback for user
cancellation, invalid requests, policy or security denials, unsupported
capabilities, or malformed configuration.

Cloud API keys are protected for the current Windows user with DPAPI. Ordinary settings and portable YAML contain no keys, and browser contracts expose only masked state and opaque secret references.

Persist only the data the user has enabled. Do not expose secrets, raw provider payloads, full stack traces, or unrestricted local paths in browser-visible errors.

Provider calls record bounded metadata-only usage events under `data/usage/`.
Use provider token counts when available and one centralized conservative
estimator otherwise; exact and estimated values must remain visibly distinct.
Never write prompts, responses, images, tool arguments, file contents, hidden
guidance, or secrets to the usage ledger. Local Ollama inference has zero
provider token charge. Any paid-cloud comparison is an explicitly selected
equivalent-cost estimate using a versioned official-source price snapshot, not
an Ollama Cloud token quota or exact savings claim.

Provider health is application-owned and based on observed requests or explicit
connection tests, never merely on key presence. Diagnostics expose only
sanitized status, category, retry, quota, model identity, adapter, freshness,
latency, and accuracy facts. Cloud retries are provider-aware, jittered,
duration- and attempt-bounded, and respect valid `Retry-After` values. Never
retry authentication, invalid requests, unsupported capabilities, security
denials, user cancellation, or deterministic protocol/parser failures. Every
real provider attempt records its own usage event before the existing
cloud-to-local fallback policy is considered.

Usage events are validated before storage and aggregation. Rejected or
duplicate events must not affect derived totals, and exact provider counts are
never silently repaired. Reconciliation reads immutable JSONL events with
bounded memory, reports accepted, warned, estimated, rejected, and duplicate
counts, and atomically replaces only files under `data/usage-aggregates/`.
Automatic reconciliation is limited to a missing, invalid, or version-mismatched
aggregate; ordinary startup must not rebuild valid aggregates.

Web search is off by default and requires explicit enablement. Provider-native
search is used only where official metadata or an explicit adapter contract
authorizes it. Ollama Web Search is a separate, read-only integration with its
own DPAPI-protected key; it does not add Ollama Cloud models. Search results are
bounded untrusted data, citations must use absolute HTTPS URLs, and result
content must never trigger local tools.

Image input requires verified vision capability, MIME/signature validation,
bounded count, bytes, and decoded dimensions. Cloud image bytes require an
independent per-browser-session and per-provider confirmation that is never
persisted. Conversation history stores attachment metadata and a
`missing-attachment` marker only; the usage ledger stores counts and byte totals
but never image or search content. Images must not be stripped to make a
text-only primary or fallback succeed.

Cloud quota and cost dashboards are local projections over provider-reported
headers, explicitly configured quotas, cached provider metadata, and the usage
ledger. Accuracy must remain labelled exact, estimated, or unavailable.
Expected billing mode is a user label, never a billing guarantee. Do not make
background provider calls, send alerts externally, or switch providers
proactively.

Model presentation preferences are local metadata keyed by provider plus exact
model ID. Aliases, favorites, hidden state, and notes must never replace the
technical identity used in API calls. Hidden or unavailable saved references
remain visible in repair flows, while ordinary selectors omit hidden models.
Capability filters use provider metadata and exact cached conformance identity,
never model-name inference when authoritative data exists.

Named model profiles store references to existing primary, fallback, router,
coordinator, web, comparison, and usage-window settings. Applying a profile
requires confirmation, validates every required reference and the mandatory
cloud-to-local fallback, and atomically replaces settings without starting a
model request or changing the active conversation lock. Workspaces store only a
preferred global profile ID. Portable YAML excludes local presentation metadata
and model profiles.

Conversation identity is host-generated and stable for the lifetime of a conversation. When history is enabled, persist the current snapshot successfully before creating or resuming another session; a failed save must leave the visible conversation intact. When history is disabled, meaningful content requires an explicit choice to enable and save, discard, or cancel. Never auto-resume conversations, pending approvals, or processes.

Conversation search is literal, local, bounded, and cancellable. It may inspect
titles, persisted visible user and assistant messages, workspace, exact
provider/model identity, changed-file metadata, validation results, dates,
archive state, and pin state. It must not use a model, embeddings, a cloud
service, hidden instructions, raw tool results, or unrestricted process output.
Pinned sessions remain explicitly deletable but are protected from ordinary
retention cleanup.

Session summaries are separate structured records created only after the user
requests generation, reviews the bounded token estimate, and grants permission
for the selected real provider or GPU. Summary input includes only bounded
complete visible turns and authoritative bounded execution facts; exclude
hidden prompts, raw process output, incomplete responses, credentials, and
approval state. Summaries may be regenerated, edited, or deleted without
rewriting conversation messages.

Conversation duplication copies only safe visible messages, a valid preferred
global profile reference, and the optional summary into a new host-generated
identity. It must reset model lock, Execute state, approvals, processes,
rollbacks, validation runtime, pin/archive state, and changed-file authority.
Markdown export is readable and bounded, redacts likely secrets and absolute
local paths, and never exports internal runtime authority.

Full local backup is distinct from portable YAML. Archives use a versioned
manifest, per-entry SHA-256 hashes, creation time, application version, and
explicit category options. Secrets, encrypted secret blobs, approvals, active
processes, pending tools, image permissions, handles, temporary files, and user
workspace file contents are never included. Restore always inspects and
validates first, permits only selected categories, creates a current-data
backup, writes atomically, and rolls back applied files on failure.

Persisted stores declare explicit schema versions. Small sequential migrations
inspect before mutation, preserve an original backup, validate staged output,
and switch atomically. A recorded migration failure prevents automatic retry
and activates safe mode without modifying the failed original.

Safe mode may be requested with `--safe-mode`, the
`AgenticRouter__SafeMode=true` environment setting, or migration failure. It
disables Execute, provider calls, cloud mutations, automatic history loading,
resident-model startup, and settings writes. The browser shows a persistent
indicator and permits only read-only inspection and sanitized backup/export
until a normal restart.

## 9. Streaming and UI Contracts

The frontend communicates only with the local API and must never call Ollama directly.

Stream typed events in order and end every turn with exactly one terminal event. Only response-delta events contribute text to the visible assistant answer. Routing, model, tool, validation, retry, recovery, heartbeat, and timing events belong in collapsible activity details.

The composer exposes a compact context-usage indicator. Before provider
completion it is a conservative estimate; when the terminal provider response
contains usage, input tokens are exact. Details distinguish visible, included,
omitted, system, current-user, configured, provider-reported, reserved-response,
and application limits. Warnings use the 70%, 85%, and 95% thresholds and must
make trimming explicit.

The chat remains the primary surface. Execute mode must additionally expose:

- active workspace and execution state;
- plan and step progress;
- explicit approval prompts;
- changed-file and diff review;
- validation results;
- conflicts, recovery decisions, and undo availability.

The composer shows the active provider/model, capability and role tags, an
explicit Web control, and bounded image attachment controls. These controls
must remain compact, accessible, and usable without horizontal overflow at
narrow viewport widths.

Keep keyboard and accessibility behavior intact: Enter sends, Shift+Enter inserts a line break, Escape closes dialogs, focus remains visible, and collapsible controls expose `aria-expanded`.

Settings uses a near-full-viewport dialog with one dirty-state model, persistent save controls, desktop section navigation, and a compact responsive selector. Validation errors must focus the relevant section, and closing dirty settings requires explicit discard confirmation.

Use plain HTML, CSS, and JavaScript. Do not add Node.js, npm, a bundler, or a frontend framework.

## 10. Error Handling

Use stable typed errors with fields equivalent to code, message, stage, provider, model, intent, retryability, trace ID, and sanitized details.

Distinguish at least:

- invalid configuration or request;
- unavailable model or provider;
- timeout and cancellation;
- invalid router output;
- tool-protocol incompatibility;
- policy or approval rejection;
- workspace/path security rejection;
- stale or conflicting file changes;
- process and validation failure;
- exhausted execution or recovery budget.

Preserve the original exception as the logged cause. Any fallback must be explicit. Never retry indefinitely or replace every failure with a generic message.

## 11. Testing and Validation

The automated suite contains end-to-end tests only.

Use Playwright for .NET with MSTest to exercise the browser and running API together. The default suite may replace Ollama only at its external HTTP boundary with a deterministic fake. Do not mock internal controllers, routing, execution, persistence, or browser code.

Before running any benchmark, smoke test, or other validation that invokes a real Ollama model, obtain explicit permission from the user for that run. Before using a real cloud provider for conformance or validation, obtain explicit permission because the call may consume quota. Fake-provider E2E tests and read-only local model discovery do not require this permission.

Every E2E test has a maximum timeout of 60 seconds. Do not solve slow tests by raising that limit. Use Playwright assertions and event-based waiting, avoid arbitrary sleeps, and keep tests independent.

Before completing a change:

1. run `dotnet format AgenticRouter.slnx --verify-no-changes`;
2. run `dotnet build AgenticRouter.slnx -c Release`;
3. run the full Playwright E2E suite;
4. run a relevant real-Ollama smoke when the required local runtime/model is available;
5. inspect `git diff --check` and the complete intended diff.

Maintainer diagnostics live only under `tools/diagnostics/`. Default execution
is read-only and sanitized and must not invoke models, GPUs, cloud providers,
arbitrary command text, workspace changes, or Git-history changes. Generated
reports remain ignored. Published application artifacts must exclude
diagnostic scripts and reports, tests, fake-provider assets, benchmarks,
secrets, settings, workspace/session data, and usage ledgers.

Never claim validation that was not executed. Report unavailable or incompatible real models as limitations, not as passing evidence.

## 12. Code Quality and Change Discipline

- Keep nullable reference types enabled and the build at zero warnings.
- Use asynchronous APIs and pass `CancellationToken` through I/O paths.
- Never block async work with `.Result`, `.Wait()`, or thread sleeps.
- Prefer cohesive classes, immutable records, and explicit result types.
- Keep I/O at the edges and avoid static mutable application state.
- Do not add dependencies when the platform provides a simple solution.
- Avoid speculative abstractions, dead scaffolding, section-divider comments, and `#region`.
- Preserve public contracts unless the requested change explicitly versions them.
- Preserve unrelated work and external file changes.
- Do not report success for an operation the host did not perform.

Before editing, inspect the relevant implementation, repository instructions, and tests. Make the smallest coherent vertical change. After editing, report the files changed, commands run, results, and real limitations.

## 13. Current Non-Goals

Do not implement these without an explicit later request:

- model providers other than Ollama Local, Groq, Google AI Studio, and Cerebras;
- MCP, plugin, remote-agent, or recursive delegation systems;
- unrestricted shell or operating-system control;
- destructive filesystem operations or history rewriting;
- background queues, schedulers, distributed execution, or multi-node inference;
- RAG, embeddings, vector databases, fine-tuning, or training pipelines;
- authentication, accounts, billing, telemetry platforms, installers, or auto-update;
- automatic model downloads;
- frontend frameworks or a JavaScript build pipeline.

## 14. Definition of Done

A change is complete only when all applicable behavior works through the real browser/API path, host authority and workspace confinement remain intact, failures are reviewable, the Release build has zero errors and warnings, the full E2E suite passes within its timeout, and any required real-provider limitation is explicitly reported.
