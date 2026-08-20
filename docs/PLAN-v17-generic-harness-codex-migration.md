# PLAN v17: Generic harness contract and Codex migration

## Goal

Introduce the smallest internal harness contract that normalizes discovery,
lifecycle, common capabilities, event metadata, cancellation, and terminality,
then migrate the existing Codex App Server adapter onto that contract without
changing Native execution behavior or Codex permissions, prompts, provider
selection, workspace authority, or Host-owned effect proof.

This plan covers Phase 0 research and Phase 1 only. OpenCode implementation is
explicitly blocked until the user manually accepts this Codex migration.

## Research baseline

- The current integration is one singleton `IAgentHarness` implemented only by
  `CodexHarnessAdapter`; `ChatStreamService` injects it as `_codexHarness` and
  hard-codes `native`/`codex` selection and Codex-specific event handling.
- Codex App Server remains the correct transport. Current official documentation
  specifies JSON-RPC-style messages over default JSONL stdio, the
  `initialize`/`initialized` handshake, thread start/resume, turn start and
  interrupt, streamed item/reasoning/tool notifications, approvals, dynamic
  tools, and terminal `turn/completed` status.
- The installed standalone CLI is `codex-cli 0.148.0-alpha.15`. Its
  `app-server generate-json-schema` command succeeds and the generated v2
  schema contains every method and notification used by the adapter.
- The current process PATH resolves the unlaunchable Desktop MSIX Codex binary,
  while the user PATH points to a previous standalone hash directory that no
  longer exists. This refactor will expose that state as unavailable but will
  not mutate PATH or restart the running application.
- OpenCode `1.18.18` is installed and its model-free `serve --help` confirms a
  loopback default and dynamic port support. Its live server and `/doc` contract
  will not be started or inspected until Phase 2 is explicitly authorized.

## Boundaries

- Preserve Native as the default and leave its execution loop in place.
- Preserve the exact Windows workspace path and all Host confinement, approval,
  observation, validation, undo, and terminal-fact authority.
- Preserve Codex App Server JSONL, exact Ollama provider/model propagation,
  isolated `CODEX_HOME`, streaming, reasoning, Host dynamic tools, approvals,
  cancellation, and owned-process cleanup.
- Add no runtime, SDK, plugin, marketplace, Node, Python, or cloud dependency.
- Do not repair local PATH, restart services, run real Ollama inference, or add
  OpenCode in this phase.

## Implementation

1. Extract generic contracts for harness identity, capability declaration,
   availability/version, turn input, normalized event metadata, native payload,
   terminal state, approval/tool responses, cancellation, and disposal.
2. Add a small case-insensitive registry containing Native metadata plus the
   registered external adapters. Use it for request validation, discovery, and
   adapter selection so orchestration no longer owns a closed Codex branch list.
3. Migrate `CodexHarnessAdapter` to the generic request and event contract while
   retaining Codex-specific protocol code and error codes inside the adapter.
4. Add a single per-turn terminal guard so completion, cancellation, malformed
   protocol data, model reroute, timeout, and process exit produce at most one
   normalized terminal event. Retain cloned native payloads on mapped protocol
   events whenever a source payload exists.
5. Preserve the conversation-to-thread mapping and support documented
   `thread/resume` after an owned App Server restart, without persisting new
   conversation authority or changing ordinary same-process thread reuse.
6. Add a read-only harness discovery endpoint. Generalize the existing selector
   from registry results, mark experimental entries, show versions, and disable
   unavailable harnesses while keeping Native usable.
7. Extend the deterministic fake App Server and browser/API E2E coverage for
   registry discovery, version/availability, exact working-directory and
   provider/model propagation, restart/resume, native payload-aware translation,
   unexpected events, malformed JSON, process exit, cancellation, and exactly
   one terminal event.
8. Add a concise research/migration note with official documentation, installed
   schema evidence, discrepancies, architecture, capabilities, and limitations.

## Validation

1. Run focused harness discovery, Codex lifecycle, failure, cancellation, and
   Native-regression E2E tests.
2. Run `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
3. Run `dotnet build AgenticRouter.slnx -c Release` with zero warnings/errors.
4. Run the full Playwright E2E suite within its existing per-test timeout.
5. Run `git diff --check` and inspect the complete intended diff, preserving all
   pre-existing dirty work.
6. Do not run a real Codex/Ollama turn without separate explicit authorization.

## Manual gate

After automated validation, report exactly:

`CODEX GENERIC HARNESS MIGRATION READY FOR MANUAL TEST`

Then stop and wait for explicit user approval before any OpenCode implementation.

## Manual-test incident follow-up (2026-08-18)

The first Visual Studio launch exposed a Phase 1 executable-discovery gap. The
service can inherit a stale hash-versioned Codex `PATH` after a desktop update.
The separately observed Qwen memory increase remains a runtime investigation;
the user's approximately 120k context is intentional and is not changed here.

1. Discover the current standalone Windows Codex executable under the stable
   user-local `OpenAI/Codex/bin` root after an explicit configured path and
   before ambient `PATH`; never persist a hash-versioned child directory.
2. Extend fake-App-Server E2E evidence for managed-install discovery. Keep the
   missing explicit-path failure authoritative.
3. Run deterministic validation only. Do not restart the user's Visual Studio
   process or execute a real Codex/Ollama turn without explicit permission.
