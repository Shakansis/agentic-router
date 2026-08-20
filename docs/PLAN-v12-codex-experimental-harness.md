# PLAN v12: Optional Codex experimental Execute harness

## Goal

Add a user-selected `Codex (Experimental)` Execute harness beside the existing
Native path without changing Native coordination behavior. Remove the composer
model-lock feature and reuse its location for a Native/Codex selector.

## Boundaries

- Keep routing, selected-model resolution, trusted-workspace validation,
  execution-session facts, browser streaming contracts, and terminal truth in
  Agentic Router.
- Keep the Native Execute implementation inline and unchanged except for the
  harness-selection branch.
- Put all Codex JSONL protocol and owned-process lifecycle behavior behind one
  `IAgentHarness` implementation.
- Support only an exact selected Ollama Local model. Never fall back to Native,
  another provider, or another model.
- Use an Agentic-Router-owned `CODEX_HOME`; never read or modify the user's
  ordinary Codex configuration.
- Do not run a real Codex/Ollama turn without separate explicit permission.

## Implementation

1. Remove the model-lock control, browser state/handlers, request field, backend
   lock-only activity, styling, and lock-specific E2E assertions.
2. Add the compact Execute-only harness selector with `Native` as the default
   and send the selection as a host request field.
3. Add a minimal harness contract and a singleton `CodexHarnessAdapter` that:
   safely resolves an explicit executable or `codex` from `PATH`, lazily starts
   one owned `codex app-server`, initializes stdio JSONL, maps one AR
   conversation to one Codex thread, streams events, interrupts cancellation,
   detects exit, and kills only its owned process tree as a timeout fallback.
4. Generate isolated local Codex configuration for the built-in Ollama provider
   and pass the exact selected model plus trusted workspace on each thread/turn.
5. Translate Codex reasoning, assistant, command/file activity, terminal status,
   and typed errors into existing AR events. Fail closed on approval requests
   that cannot be mapped safely.
6. Compare bounded before/after workspace and protected `.git` state so AR
   records only observed effects and never promotes Codex lifecycle completion
   into stronger factual completion.
7. Add a deterministic fake App Server executable and focused browser/API E2E
   coverage for UI selection, Native isolation, streaming, thread reuse,
   cancellation, unsupported providers, missing Codex, and observed file facts.
8. Write the short research report with executed evidence and limitations.

## Validation

- Run focused Codex-harness and model-lock-removal E2E tests.
- Run `dotnet format AgenticRouter.slnx --verify-no-changes`.
- Run `dotnet build AgenticRouter.slnx -c Release`.
- Run the full Playwright E2E suite.
- Run `git diff --check` and inspect the complete intended diff.
- Report real Codex/Ollama validation as not run unless explicitly authorized.

## Incident follow-up: `0HNNS3JQEKAKN:00000018`

1. Preserve the native Ollama authority in `OLLAMA_HOST`.
2. Pass the OpenAI-compatible `/v1` API base through
   `CODEX_OSS_BASE_URL` so Codex reaches `/v1/responses`.
3. Record both inherited values in the fake App Server and assert them in the
   focused browser/API path.
4. Pass Host-authored preservation guidance plus the bounded pre-existing Git
   dirty-path list on every Codex turn.
5. Validate the fix deterministically, then repeat the authorized real prompt.

## Batch Host tools and encoding follow-up

1. Add `create_files` to the Host-owned closed tool catalog for both Native and
   Codex harnesses, while reusing the existing `delete_files` authority.
2. Validate every path and the complete bounded batch before the first create;
   reject existing targets and preserve per-file effect proof.
3. Advertise only `create_files` and `delete_files` to Codex through App Server
   experimental `dynamicTools`, then execute them through the same
   `LocalActionService` used by Native.
4. Keep deletion approval Host-owned and atomic for the exact validated list;
   never infer a structured deletion from PowerShell or shell text.
5. Configure App Server stdin, stdout, and stderr explicitly as UTF-8 and cover
   non-ASCII reasoning and assistant deltas deterministically.
6. Validate both harness paths with the fake App Server, then run the explicitly
   authorized real prompt against `qwen3.8:27b` and independently inspect the
   remaining workspace files.
