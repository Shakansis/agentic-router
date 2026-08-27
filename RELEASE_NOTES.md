# Agentic Router v0.9.17 alpha

This alpha adds a Linux x64 portable release alongside Windows x64 from the same
core codebase. It retains the durable local supervised Execute acceptance from
v0.9.15 while adding explicit, observable Linux Ollama acceleration profiles.

- Preserves explicit URL configuration, uses loopback port 5000 when available,
  and automatically selects another loopback port when 5000 is occupied.

## Linux x64 support

- Adds a self-contained Linux x64 release from the same core codebase; Linux
  ARM64 and macOS remain out of scope.
- Routes GPU discovery, memory telemetry, folder integration, installer launch,
  path comparison, and protected-secret storage through OS-specific services
  while preserving the existing Windows implementations.
- Adds explicit Linux Ollama setup profiles: Standard/CUDA, Vulkan using
  `OLLAMA_VULKAN=1`, and ROCm using the official supplemental package. Hardware
  detection controls which choices are offered; GPU drivers and models are not
  installed automatically.
- Reports requested profile, managed package manifest, observed backend library,
  and CPU fallback separately instead of claiming acceleration from configuration.
- Adds an approval-gated profile-change workflow that revalidates expiring plans,
  reinstalls the official base package, removes only manifest-proven ROCm-only
  files, preserves model/data directories, and restarts Ollama only after visible
  confirmation.
- Adds reproducible Linux publish/runtime smoke validation under WSL while leaving
  final physical-Linux acceptance open for the packaged tar.gz.

## Durable supervised Execute

- Adds opt-in `/supervisor` execution with one serial focused supervisor context and
  recoverable worker contexts on one fixed local `model × harness` route.
- Persists bounded integrity-checked checkpoints, logical contexts, work queue,
  evidence revisions, recovery budgets, and a sanitized write-ahead Host action
  ledger when local history is enabled.
- Verifies worker claims against current artifacts, rejects stale evidence if a file
  changes during supervisor evaluation, and retries with a focused correction.
- Adds explicit `manual` and `auto-safe` restart policies. Auto-resume is denied for
  route or workspace drift, pending approval, unproven turns, and ambiguous actions.
- Keeps Host execution alive when SSE/browser disconnects and reattaches a resumed
  conversation to retained events and the accepted final answer.
- Keeps every supervised role local-only with no cloud route, fallback, concurrent
  context execution, recursive delegation, or hidden background inference.

## Execute and UI corrections

- Adds a browser-only message buffer for every Chat/Execute route. During a
  response, the ordinary Send button queues the draft while a detached circular
  control cancels the active response. Queued prompts expose rounded Edit,
  Delete, and supported Steer icon actions; an active edit blocks automatic
  dispatch, cancellation pauses the remaining queue, and no queue state is
  persisted.
- Adds explicit same-turn steering for Codex `turn/steer` and Qwen Code
  `mid-turn-message`, bound to the exact active Agentic Router conversation and
  native turn/prompt identity. Steer is offered only on queued items. Codex now
  consumes the App Server's direct `turnId` response field. Claude Code,
  OpenCode, and Native do not receive
  fabricated steering semantics; the disabled button explains the limitation.
- Moves context accounting out of the prompt panel into a backgroundless,
  right-aligned line immediately above it. Unsupported Steer actions now expose
  a reliable wrapper-owned tooltip naming the current harness and directing the
  user to Codex or Qwen Code, including keyboard focus support.
- Converts the bounded Chat workspace-read limit into a visible finalization
  boundary: an over-budget read is rejected as a tool result, read tools close,
  and the model receives one final bounded turn to answer from collected
  evidence instead of failing the entire request.
- Makes the Host-resolved effective context window the single Codex authority:
  model-catalog metadata, thread `model_context_window`, and the 98-percent
  `model_auto_compact_token_limit` now derive from the same configuration, with
  `total` active-context scope.
- Adds exactly one visible Codex continuation attempt for
  `codex-event-idle-timeout`, `codex-provider-stream-idle-timeout`,
  `codex-provider-stream-disconnected`, and `codex-app-server-exited`.
- Continues on the same selected model/provider/harness/workspace/approval
  contract and includes the actual failure cause plus Host-confirmed objective,
  changed paths, completed actions, and completed/pending plan steps.
- Avoids duplicating canonical conversation hydration in the continuation turn;
  repeated transient failure remains terminal after the single bounded attempt.
- Retries post-turn workspace observation exactly once after a transient local
  I/O/access race. Persistent observation failures now return the typed
  `<harness>-workspace-observation-failed` cause instead of a generic
  application error, without weakening Host effect verification.

# Agentic Router v0.9.14

This version combines the Execute effect-authority correction with a focused UI
reliability pass for conversation navigation, execution review, and the
resizable sidebar.

## Highlights

- Preserves the closed deterministic alias registry and adds a post-resolution
  effect contract: canonical tools can advance only semantically compatible
  Host-typed plan steps after the expected effect is independently observed.
- Blocks mutation objectives when no verified file, directory, or Git mutation
  occurred, even if a model or tool result claims success.
- Generates the terminal Execute response from authoritative Host review facts
  and rejects model output that begins with reserved internal protocol markers.
- Adds `delete_files` for explicit Host-validated paths, with mandatory approval,
  inline path-list editing, stale-hash checks, per-file deletion evidence,
  bounded text or binary rollback capture, partial-failure recovery, and undo.
- Makes pending process and structured file arguments editable in place. Clicking
  Approve atomically revalidates and executes the current text; no separate Save
  or Update action is required, and invalid edits remain pending and unexecuted.
- Adds `actionModel`, defaulting to `functiongemma:270m`, as the lightweight
  resident tool-action role. `coordinatorModel` remains an on-demand fallback;
  portable YAML exposes both as `models.action.primary` and `.fallback`.
- Adds deterministic effect-gate tests plus browser E2E proof that deletion does
  not occur before approval, affects only the explicit files, and restores both
  files through undo.
- Makes the recent-conversations sidebar keyboard- and pointer-resizable, stores
  the chosen width locally, aligns persistence content, and hides empty pinned
  and archived sections without allowing their grid styles to override `hidden`.
- Keeps saved-conversation details content-sized instead of stretching action
  controls to the modal height, while retaining the 95% viewport cap and
  internal scrolling for genuinely long content.
- Prevents New conversation from redundantly saving a conversation already
  marked `Saved locally`, and isolates new-session creation failures from
  non-blocking follow-up refresh failures.
- Resolves Undo against the active workspace profile rather than the deprecated
  `trustedWorkspacePath` compatibility field.

## Manual acceptance status

- File creation: acceptable in current real-use evaluation.
- File editing: poor and not accepted as reliable.
- File deletion: unacceptable in current real-use evaluation; reported as not
  working despite deterministic Host-side coverage.
- Automated fake-provider and filesystem tests do not override these manual
  findings. A real-model benchmark remains required and must only run after the
  user explicitly confirms GPU availability.

# Agentic Router v0.9.13

This version makes Execute failures traceable across the browser, Host,
provider usage ledger, and a bounded local incident journal, while preventing
coordinator action history from growing past the configured context budget.

## Highlights

- Uses the HTTP trace identifier as the canonical exact lookup key and keeps
  request, conversation, turn, execution-session, provider-attempt, and action
  identities as linked but independent authorities.
- Adds a versioned, ignored, publish-excluded JSONL incident journal with
  asynchronous writes, per-trace limits, UTC date/size rotation, retention,
  total-size limits, and tolerant malformed-record reads.
- Persists only typed operational milestones and Host-authored summaries; raw
  prompts, responses, file content, tool arguments/results, provider payloads,
  secrets, stack traces, and unrestricted paths are structurally absent.
- Links usage schema v2 events to the canonical trace and records typed runtime
  context failures with input, reserved output, required, maximum, and
  effective-context arithmetic while retaining schema v1 compatibility.
- Adds exact trace copy/details actions and a bounded, sanitized browser
  timeline, plus a read-only PowerShell maintainer lookup with Console, JSON,
  and Markdown output.
- Budgets coordinator requests with the provider's conservative estimator,
  including planner instructions and tool definitions, before every native
  planning call.
- Replaces older action history with a deterministic Host state summary while
  preserving the objective, project context, latest guidance, plan state,
  correction state, and latest assistant/tool pair.
- Allows exactly one materially smaller compaction retry for a context-fit
  rejection, then changes coordination path or returns a typed, reviewable
  partial terminal result without increasing resident context or memory.

# Agentic Router v0.9.12

This version restores target-first adaptive Execute coordination while keeping
all action authority, validation, and lifecycle transitions in the Host.

## Highlights

- Evaluates an approved target-native or target-structured path before resident
  eligibility, so a failed resident cannot block an independently conformant
  specialist.
- Stores independent `native-strict`, `native-adaptive`, `structured-action`,
  and `guidance-only` evidence keyed by exact provider/model revision, adapter,
  runtime, and benchmark contract identity.
- Lets a resident that fails strict semantic conformance qualify through an
  independent adaptive correction probe before the Host blocks all coordination
  paths; empty required arguments remain invalid and unexecuted.
- Adds a supervised structured coordinator that proposes exactly one action,
  receives the authoritative Host result, and only then proposes the next
  action; plan and step IDs remain Host-generated.
- Allows one precise Host-authored semantic repair, fingerprints repetition,
  and changes coordination path instead of issuing a third identical attempt.
- Keeps deterministic native parser failures turn-scoped and non-retriable on
  the same protocol.
- Combines conformance with observed runtime context, memory headroom, and
  metadata-derived coexistence evidence before resident use or bounded
  eviction/restoration.
- Shows target, effective coordinator, configured resident, execution path,
  conformance identity, and handoff reason separately in activity and review.
- Adds deterministic fake-provider E2E coverage for target-first Groq
  coordination, structured actions, semantic repair, repeated failure,
  exhausted paths, and exact-identity invalidation.
- Adds one closed deterministic tool-alias registry shared by native and
  structured proposals, using ordinal case-insensitive lookup, phase checks,
  preserved audit evidence, and no fuzzy or argument normalization.
- Lets the user edit a pending structured process command or Git stage/unstage
  path list before approval; the Host preserves the tool and action identity,
  reparses structured arguments, and reruns the existing policy validation.
- Keeps the terminal-styled command and a collapsed execution response in one
  approval card, then removes decision controls after approval or rejection.
- Adds browser coverage for valid revision, policy-rejected revision,
  cross-session rejection, terminal state, and collapsed execution output.

# Agentic Router v0.9.11

This corrective version consolidates application dialogs and improves Git,
provider, composer, and validation ergonomics without adding a frontend build
toolchain.

## Highlights

- Replaces the composer Web and Image text controls with accessible inline
  Microsoft Fluent/Windows-style SVG icons.
- Removes browser-native alert, confirmation, and input prompts in favor of one
  application-styled modal with a persistent footer and bounded scrolling.
- Adds dismissible top-center error toasts with a 30-second timeout and visible
  field/card error borders for settings validation.
- Makes Git Overview collapsible and initially open, Repository configuration
  collapsible and initially closed, and identity/origin fields read-only until
  Edit is selected.
- Adds an immutable, Execute-only Host flow for changing only the exact `origin`
  address through validated HTTPS or SSH values without embedded credentials.
- Guides repository initialization from Chat to Execute by closing the Git modal
  first, then requires a separate initialization review and confirmation.
- Increases the bounded Changes viewport and color-codes file status markers.
- Consolidates provider configuration and observed health/statistics into one
  accordion per provider, normalizes compatible field styling and sizing, and
  preserves the open provider after Save, Test, or Refresh.
- Adds deterministic E2E coverage for the new modal, toasts, icon-only controls,
  protected Git configuration, origin mutation, collapsible sections, provider
  consolidation, and accordion persistence.

# Agentic Router v0.9.10

This development version adds Host-owned Ollama runtime context and memory
profiles without guessing hardware-sensitive runner settings.

## Highlights

- Adds typed context profiles for router, resident coordinator, specialist,
  primary, fallback, benchmark, model test, web-search synthesis, and vision
  requests. The resident coordinator defaults to 8,192 context tokens.
- Resolves every local request from role defaults plus an optional exact
  provider/model/digest override, caps it by provider and model limits, and
  sends the selected context as native Ollama `num_ctx`.
- Calculates request fit from bounded input, tool schema/results, image
  overhead, and reserved output. Context grows only through a configured
  discrete ladder and fails with a typed error when the request cannot fit.
- Verifies resident preload and context through `/api/ps`, unloads and reloads
  mismatched runners only while idle, and surfaces requested versus actual
  context, digest, VRAM, estimated RAM, offload, and shared-model warnings.
- Adds metadata-only analysis that must not load a model plus explicit,
  permission-gated real measurement with bounded context, prior-state
  restoration, and atomic measured records under
  `data/runtime-profiles/ollama-model-memory.json`.
- Invalidates measured evidence when exact model digest, Ollama version,
  hardware signature, or relevant runtime signature changes. Parallelism,
  flash attention, and KV-cache type remain visibly unknown instead of being
  guessed or changed.
- Adds Runtime settings for per-role profiles, exact overrides, memory
  headroom, metadata analysis, and confirmed measurement. Portable YAML
  includes policy and overrides but excludes measured hardware evidence.
- Compacts resident coordination to the current objective, project context,
  specialist guidance, and bounded current tool state instead of unrelated
  conversation history.
- Adds deterministic fake-Ollama E2E coverage for defaults, native payloads,
  `/api/ps` verification, shared warnings, analysis without load, explicit
  permission, measurement persistence/restoration, exact overrides, and YAML
  exclusion.

# Agentic Router v0.9.9

This development version adds pre-1.0 recovery, safe backup and restore,
schema-migration hardening, safe mode, and maintainer-only diagnostics.

## Highlights

- Adds a tracked read-only maintainer diagnostics tool with sanitized JSON and
  Markdown reports, optional deterministic build/test/publish checks, and a
  minimal support package outside the published product.
- Adds an explicit publish verifier and project exclusions for diagnostics,
  PowerShell scripts, tests, fake providers, secrets, local settings, sessions,
  usage ledgers, and other local data.
- Adds full local ZIP backup with a versioned manifest, application version,
  creation time, per-entry SHA-256 hashes, and optional conversations,
  summaries, usage history, and bounded review data.
- Always excludes API keys, encrypted secret blobs, approvals, processes,
  pending tools, rollbacks, temporary permissions, handles, and workspace file
  contents from backup.
- Adds inspect-before-apply, hash validation, conflict reporting, selective
  category restore, current-data backup, atomic replacement, and rollback.
- Adds small sequential schema migration with backup, staged validation,
  atomic replacement, persistent result/failure state, and no automatic retry
  loop after failure.
- Adds explicit and failure-triggered safe mode with a visible banner,
  read-only settings, disabled Execute/cloud/provider activity, no automatic
  history loading, no resident-model startup, and backup/export availability.
- Adds deterministic coverage for optional backup categories, secret
  exclusion, manifest hashes, corruption rejection, selective restore,
  current-data backup, migration backup, original preservation, safe mode, and
  publish exclusion.

# Agentic Router v0.9.8

This development version adds local conversation productivity tools and a
transparent context-usage indicator without introducing background inference.

## Highlights

- Adds literal, bounded, cancellable local search over conversation titles,
  visible persisted messages, workspace, exact provider/model, changed files,
  validation, dates, archive state, and pin state.
- Adds pin/unpin with a stable pinned section and protects pinned conversations
  from automatic retention cleanup while preserving explicit deletion.
- Adds explicit structured session summaries with a pre-generation token
  estimate and per-request provider/GPU permission. Summary facts exclude
  hidden prompts, raw process output, incomplete responses, credentials, and
  approval state.
- Stores summaries separately so they can be regenerated, edited, or deleted
  without rewriting visible conversation history.
- Adds safe conversation duplication with new Host identity and reset runtime,
  execution, model-lock, approval, rollback, archive, and pin state.
- Adds readable bounded Markdown export with optional summary and model
  metadata, secret and absolute-path redaction, and no internal runtime
  authority.
- Adds compact estimated/exact context usage with visible, included, omitted,
  system, current-request, provider, configured, application, and reserved
  response details plus 70%, 85%, 95%, and trimming warnings.
- Adds deterministic browser/API coverage for search, highlights, pin-aware
  retention, summary consent and persistence, safe duplication, sanitized
  Markdown, context trimming, and exact provider usage.

# Agentic Router v0.9.7

This development version adds local model organization, authoritative
capability filters, execution-chain previews, and reusable model configuration
profiles.

## Highlights

- Adds provider-qualified aliases, favorites, hidden state, and optional notes
  while preserving the exact provider/model identity used by every request.
- Keeps hidden and unavailable saved references repairable, groups models by
  provider, orders favorites deterministically, and exposes hidden models only
  through an explicit toggle.
- Adds Local, Cloud, Tools, Web, Vision, Structured output, exact conformance,
  context, availability, favorites, and alias/exact-ID search filters using
  provider metadata rather than model-name heuristics.
- Shows the effective primary, fallback, router, and coordinator chain with
  provider, exact ID, alias, availability, conformance, tool path, Web, Vision,
  affected workspaces, and mandatory local-fallback validation.
- Adds bounded global profiles for model roles, Web preference, cost
  comparison, and usage window. Save and preview never infer; applying requires
  confirmation and atomically updates settings without changing the active
  browser lock.
- Stores only a preferred global profile reference per workspace and reports
  affected workspaces before applying profile changes.
- Persists organization metadata atomically in a schema-versioned local store
  while keeping aliases, notes, hidden state, profiles, secrets, workspace
  paths, and conversation state outside portable YAML.
- Adds deterministic fake-provider E2E coverage for identities, ordering,
  hidden repair, authoritative filters, exact conformance, profile validation
  and application, workspace references, secret exclusion, and unchanged
  routing.

# Agentic Router v0.9.6

This development version adds application-owned provider health, bounded
provider-aware retries, strict usage validation, and immutable-ledger
reconciliation.

## Highlights

- Adds compact health views for Ollama Local, Groq, Google AI Studio, and
  Cerebras based on observed calls and explicit tests rather than key presence.
- Adds sanitized latency, freshness, rate-limit, quota, usage-accuracy,
  adapter, model, status, and retry diagnostics without request content or
  credentials.
- Adds three-attempt/eight-second retry limits with jitter, `Retry-After`,
  cancellation, and no retry for authentication, invalid requests, unsupported
  capability, security, parser, or tool-protocol failures.
- Records every real provider attempt separately and preserves the existing
  mandatory local-fallback rules.
- Validates usage records and excludes rejected or duplicate events from
  derived totals without altering exact provider counts.
- Adds explicit and schema-triggered reconciliation that streams immutable
  JSONL events, reports validation counts, and atomically rebuilds only derived
  aggregate files.
- Adds deterministic E2E coverage for health transitions, staleness,
  authentication, bounded timeout and rate-limit retry, cancellation, request
  accounting, corruption rejection, duplicate detection, immutable source
  preservation, sanitized diagnostics, and browser presentation.

# Agentic Router v0.9.5

This development version adds capability-aware chat with explicitly enabled
web search, safe citations, and bounded image understanding across verified
provider paths.

## Highlights

- Adds one unified capability contract for chat, streaming, advertised tools,
  structured output, reasoning, vision, provider-native and
  application-mediated search, citations, image count/byte limits, and MIME
  types while keeping behavioral tool conformance separate.
- Shows compact Local, Cloud, Tools, Web, Vision, Structured, Primary, and
  Fallback tags beside the active provider/model and keeps the composer
  responsive on narrow viewports.
- Adds explicit Off, Available, Enabled, and Unavailable Web states. Supported
  Gemini requests use Google Search grounding, supported Groq Compound systems
  use provider citations, and Cerebras never receives invented search support.
- Adds a separate DPAPI-protected Ollama Web Search integration for local
  models. Results are bounded, treated as untrusted data, limited to HTTPS
  citations, and cannot trigger local tools or restore Ollama Cloud models.
- Adds file-picker, drag/drop, clipboard paste, preview, and removal for
  verified JPEG, PNG, WebP, and GIF attachments with signature, count, decoded
  byte, MIME, and practical dimension checks. SVG is rejected.
- Requires a non-persisted, browser-session/provider-specific confirmation
  before cloud image upload. Local vision needs no confirmation; text-only
  primaries and fallbacks never receive silently stripped images.
- Maps multimodal messages to Ollama, compatible Groq models, and Gemini.
  Cerebras image input remains disabled unless authoritative model metadata
  confirms it.
- Extends metadata-only usage events with media tokens when reported, image
  count/bytes, search queries, grounded requests, citation count, provider
  search cost when available, and exactness without storing image or search
  content.
- Adds deterministic E2E coverage for capability accuracy, search/citations,
  unsafe URLs, cancellation, image validation and persistence, privacy
  approval, multimodal mapping, vision-aware fallback, and responsive composer
  behavior.

# Agentic Router v0.9.4

This development version adds a Host-owned cloud usage dashboard and requires a
verified Ollama Local fallback whenever an intent primary resolves to cloud.

## Highlights

- Rejects missing, cloud, unavailable, or ambiguous fallback identities for a
  cloud primary and verifies the exact local model through Ollama discovery
  before settings are saved.
- Performs at most one explicit cloud-to-local fallback for eligible provider
  unavailability, timeout, rate-limit, quota, or transient request failures.
  Cancellation, malformed requests, policy failures, and non-retryable errors
  do not trigger fallback.
- Records the local attempt with the `fallback` model role and exposes the
  strategy change in activity instead of retrying the cloud request
  indefinitely.
- Adds a compact clickable cloud-usage card to the left sidebar above Recent
  conversations and a 95%-viewport dashboard with fixed controls and central
  scrolling.
- Shows configured-provider connection state, exact/estimated/unavailable quota
  accuracy, provider windows and resets, costs, latest request, observed 429
  warnings, per-model totals, roles, and capability metadata.
- Adds user-labelled expected billing modes and local visual quota thresholds.
  A Free tier label is explicitly an expectation and never a billing guarantee.
- Builds the dashboard exclusively from local provider cache and the bounded
  usage ledger; it does not make background cloud calls or switch providers
  proactively.

# Agentic Router v0.9.3

This development version adds protected, provider-qualified cloud inference
through Groq, Google AI Studio, and Cerebras while preserving Ollama Local
behavior.

## Highlights

- Adds a provider registry and collision-safe `provider::model` identities with
  grouped model selectors, capability metadata, and repairable unavailable
  saved references.
- Adds official HTTP adapters for Groq's OpenAI-compatible API, the Gemini
  Developer API, and Cerebras Inference, including streaming, native tool or
  function calls, provider token usage, rate-limit snapshots, and typed errors.
- Protects API keys with Windows DPAPI for the current user. Settings retain
  opaque references only; keys never return to the browser, portable YAML,
  usage ledger, logs, or conversation history.
- Adds focused cloud-provider settings cards for masked key state, connection
  tests, model refresh, last refresh, model count, quota source, diagnostics,
  key replacement, and confirmed removal.
- Caches non-secret provider model metadata locally so qualified selections
  survive application restarts without automatically spending cloud quota.
- Requires explicit permission before a cloud behavioral conformance
  benchmark and keys results by qualified model revision and adapter protocol
  version rather than trusting advertised tool capability alone.
- Records each completed cloud provider call exactly once in the v0.9.2 usage
  ledger, including exact/estimated token source, price snapshot, failure or
  429 state, and observed rate-limit metadata.
- Adds deterministic fake-provider E2E coverage for encryption at rest,
  secret exclusion, model discovery, restart persistence, grouped selection,
  streaming, tools/functions, usage, rate limits, and cloud conformance.

# Agentic Router v0.9.2

This development version adds local token-usage analytics and transparent
equivalent paid-cloud cost estimates.

## Highlights

- Records metadata-only usage events at the Ollama provider boundary, using
  authoritative terminal `prompt_eval_count` and `eval_count` values when
  available and a visibly marked centralized estimate otherwise.
- Stores bounded daily JSONL files under `data/usage/` with concurrency-safe
  append, partial-tail recovery, configurable retention, streaming aggregation,
  filters, windows, and confirmed purge support.
- Separates input and output tokens, model roles, provider/model breakdowns,
  success, failure, cancellation, exactness, and event-time price snapshots.
- Adds Runtime views for selected and pinned windows, top models and roles,
  local/cloud totals, last update, and equivalent paid-cloud cost.
- Adds a versioned official-source pricing catalog and decimal cost calculation
  against an explicitly selected Google comparison model.
- Treats local Ollama inference as zero provider token cost and presents
  Free/Pro/Max only as subscription references without inventing token quotas
  or claiming exact Ollama Cloud savings.
- Extends portable YAML with usage preferences while deliberately excluding
  usage history.

# Agentic Router v0.9.1

This corrective development version makes the existing Git and conversation-persistence capabilities discoverable and safe in the main browser interface.

## Highlights

- Adds a sidebar Git card and dedicated panel with authoritative overview plus bounded current-session, working-tree, staged, and last-commit diffs.
- Supports explicit repository initialization on `main` and explicitly approved repository-local `user.name` and `user.email` without creating commits, staging files, or mutating remotes.
- Gives every conversation a stable Host-generated identity, saves before switching when history is enabled, and preserves the visible conversation when persistence fails.
- Adds explicit save, discard, and cancel choices for meaningful unsaved conversations and visible persistence status in the main interface.
- Makes Recent conversations identify the current session and safely persist the current conversation before resume.
- Reorganizes Settings into a responsive near-full-viewport interface with section navigation, dirty-state protection, focused validation errors, and persistent save controls.
- Simplifies workspace management with consistent accordions, section titles outside their content panels, information tooltips for static warnings, and an explicit `+` flow that reveals the new-workspace form only when requested.
- Keeps modal headers and action footers visible, lets dialogs grow to at most 95% of the viewport, and confines overflow scrolling to the central content area.
- Adds portable YAML import/export for global settings, with only `primary` and `fallback` model roles, strict field-and-line validation, atomic application, and deliberate exclusion of workspace paths, conversations, validation commands, and approvals.
- Adds an explicit Host-owned real-Ollama conformance endpoint and sequential PowerShell runner that reuse the production simple-call, nested-plan, and read-result-edit probes.
- Records the v0.9.1 release benchmark for all 17 locally installed model digests on Ollama 0.32.5: 10 passed and 7 failed protocol conformance.

# Agentic Router v0.9.0

This development version adds a narrow, host-owned Git delivery workflow to Execute review.

## Highlights

- Shows fresh branch, HEAD, upstream, ahead/behind, operation, conflict, staged, unstaged, and untracked state.
- Separates current execution-session files from pre-existing user changes and stages only explicit selections.
- Binds successful validation to selected file hashes and blocks stale or missing validation unless an allowed override is explicitly approved.
- Creates one exact commit, optional annotated tag, current-upstream branch push, and exact-tag push through structured operations.
- Requires immutable explicit approval for every Git write, including with the automatic execution policy.
- Records commit, tag, branch-push, and tag-push facts separately and disables history-contradicting internal undo after commit.

# Agentic Router v0.8.1

This maintenance release stabilizes the supervised Execute workflow introduced after v0.8.0.

## Highlights

- Separates the lightweight intent router from the execution coordinator.
- Adds behavioral tool-protocol conformance checks per model, Ollama version, and model digest.
- Treats malformed XML, truncated JSON, missing native tool calls, and related parser failures as typed recoverable protocol failures.
- Changes strategy after deterministic protocol failures and bounds retries, recovery, iterations, tool calls, and elapsed execution time.
- Makes execution plans host-owned, with host-generated identifiers and validated dependencies.
- Strengthens trusted-workspace confinement, file-conflict handling, validation evidence, execution review, and undo state.
- Expands browser-driven E2E coverage for planning, approvals, tools, recovery, persistence, validation, and protocol incompatibilities.

## Compatibility note

Ollama's declared `tools` capability is not treated as proof that a model can coordinate tools reliably. Direct coordination is enabled only after the exact installed model passes the host's behavioral conformance benchmark.
