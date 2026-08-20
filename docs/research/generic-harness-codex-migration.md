# Generic harness contract and Codex migration

Date: 2026-08-18

Plan: [`docs/PLAN-v17-generic-harness-codex-migration.md`](../PLAN-v17-generic-harness-codex-migration.md)

## Scope and result

Phase 1 replaces the single injected Codex-shaped harness seam with a small
internal registry and generic contract. Native remains the default and retains
its existing execution path. Codex remains an experimental App Server adapter
with the same isolated runtime, prompt, exact Ollama selection, Host dynamic
tools, approvals, workspace observation, validation, and terminal-fact rules.

OpenCode was not implemented. Its installed version was recorded during
research, but its live server and `/doc` schema remain behind the required
post-Codex manual acceptance gate.

## Current official and installed evidence

The current official [Codex App Server documentation](https://developers.openai.com/codex/app-server)
still defines the interface as JSON-RPC-style messages over default JSONL stdio.
It requires one `initialize` request followed by `initialized`, supports
`thread/start`, `thread/resume`, `turn/start`, `turn/interrupt`, streamed item
and reasoning notifications, server-initiated approvals and dynamic tools, and
ends turns through `turn/completed` with an explicit status. The documentation
also states that generated TypeScript and JSON Schema output is specific to the
CLI version that produced it.

The current official [Codex configuration reference](https://developers.openai.com/codex/config-reference)
continues to reserve the built-in `ollama` provider id and documents
`model_provider`, `oss_provider = "ollama"`, `sandbox_mode = "workspace-write"`,
and native Windows `windows.sandbox = "unelevated" | "elevated"`. No Codex
prompt, permission, provider, or sandbox configuration was changed by this
refactor.

Installed model-free probes established:

- standalone Codex: `codex-cli 0.148.0-alpha.15`;
- `codex app-server generate-json-schema --out <temp>`: succeeded;
- generated v2 schema: contains `thread/start`, `thread/resume`, `turn/start`,
  `turn/interrupt`, `turn/completed`, `item/agentMessage/delta`,
  `item/reasoning/summaryTextDelta`, and `item/tool/call`;
- OpenCode: `1.18.18`; and
- `opencode serve --help`: defaults to `127.0.0.1`, supports port `0`, and does
  not enable mDNS by default.

No model turn, Ollama inference, cloud call, OpenCode server, or OpenCode
`/doc` request was used during this research or automated validation.

## Material discrepancy

The currently focused process resolves `codex` to the Desktop MSIX executable,
which Windows denies this service process permission to launch. The user PATH
also still names a previous standalone hash directory that no longer exists
after the latest app update. The current accessible standalone executable lives
under a newer hash directory and was used directly for the model-free version
and schema probes.

This repository change does not repair PATH, persist a versioned executable
path, or restart Agentic Router. Instead, `GET /api/harnesses` reports Codex as
unavailable when its configured/PATH executable cannot be launched, and the UI
keeps Native selected while disabling the unavailable Codex option. The
existing `AgenticRouter:Codex:ExecutablePath` override remains the stable
operator-controlled escape hatch.

## Contract and registry

The generic layer normalizes only the control surface:

- stable identity, display name, experimental state, and supported providers;
- explicit capabilities rather than simulated equivalence;
- availability, version, and sanitized unavailability reason;
- session id, exact working directory, provider, model, prompt, approval policy,
  provider endpoint, protected paths, cancellation, and native options;
- streamed normalized events with harness/session/turn ids and timestamps;
- optional cloned native payloads retained inside the Host boundary;
- approval and Host-tool responses;
- cancellation and asynchronous disposal; and
- explicit terminal states with one atomic terminal emission per adapter turn.

`HarnessRegistry` contains built-in Native metadata plus registered external
adapters. `ChatStreamService` validates selections and providers through the
registry, then selects an external adapter without injecting a Codex-specific
singleton. Native is deliberately not forced through an external-process
abstraction.

## Codex capabilities and lifecycle

The migrated Codex adapter declares the capabilities actually exposed by this
integration: streaming, thinking summaries, resume, cancellation, approvals,
tool events, Host structured edits, App Server sandboxing, session diff events,
and native permissions. Subagents remain disabled in the isolated runtime and
are reported unsupported. Codex-native stale-edit protection is not claimed as
a generic adapter capability; Agentic Router still performs its own stale and
effect validation.

One Agentic Router session maps to one Codex thread. Same-process turns reuse
the attached thread. If the Agentic-Router-owned App Server exits after a
completed turn, the mapping survives, a new owned process starts lazily, and
the adapter uses documented `thread/resume` with the exact model, provider,
working directory, approval policy, and sandbox override. A failed resume is
typed and visible; it is not silently replaced with a new thread.

Known Codex notifications are normalized while retaining a clone of the full
source payload. Unknown valid notifications become an internal `native.event`
instead of being discarded; the ordinary browser receives only a sanitized
fact that native diagnostic data was retained, never the raw payload. Malformed
JSON, stream failure, process exit, model reroute, interrupt, and normal
completion all converge on the same per-turn atomic terminal guard.

## Deterministic evidence

The focused browser/API harness slice passed 15/15. It covers:

- registry order, Native/Codex discovery, experimental metadata, capabilities,
  and fake version detection;
- UI population and unavailable-option behavior;
- exact Windows working directory, Ollama provider, model, and endpoint;
- reasoning, assistant deltas, tools, approvals, UTF-8, and Host effects;
- same-process thread reuse and `thread/resume` after an owned-process restart;
- cancellation;
- controlled child-process exit;
- malformed JSON and an unexpected future notification;
- retained native payload presence without browser exposure;
- exactly one AR terminal event for success and failure paths; and
- unchanged Native default and Native batch-tool behavior.

The first in-sandbox run could not start the deterministic `HttpListener` fake
and failed with `HttpListenerException: The handle is invalid`. The identical
focused suite passed outside the sandbox. This is an established test-host
restriction, not product or model evidence.

Final repository gates also passed:

- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`;
- `dotnet build AgenticRouter.slnx -c Release --no-restore`: zero warnings and
  zero errors;
- complete deterministic Playwright/API suite: 207/207 in 5m22s;
- `node --check AgenticRouter.Api/wwwroot/app.js`; and
- `git diff --check`.

## Known limitations

- Availability is cached for one minute per application process to avoid
  spawning `<harness> --version` on every page load. Execution still performs
  ordinary launch validation, so a stale positive status cannot bypass failure.
- Agentic Router still keeps the Codex conversation-to-thread mapping in memory.
  `thread/resume` covers an owned App Server restart, not an Agentic Router
  process restart.
- The native payload escape hatch is internal and deliberately not persisted in
  incidents or returned raw to the browser.
- Codex App Server and `dynamicTools` remain experimental.
- Real Codex/Ollama validation was not run for this refactor because no separate
  permission was requested or granted.
