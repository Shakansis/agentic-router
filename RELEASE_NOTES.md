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
