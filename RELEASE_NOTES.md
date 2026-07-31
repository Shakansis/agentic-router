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
