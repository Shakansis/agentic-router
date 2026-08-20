# PLAN v20: Qwen Code experimental harness

## Goal

Add installed Qwen Code `0.21.13` as the next experimental adapter behind the
existing generic harness contract. Preserve Native as the default, Codex and
OpenCode behavior, exact trusted-workspace paths, explicit local Ollama
provider/model identity, Host-observed effects, and exactly one public terminal
event.

## Approved start

The user manually accepted OpenCode on 2026-08-19 and explicitly approved the
next integration. The ordered next harness from the accepted task is Qwen
Code. Claude Code, Goose, and DeepSeek Harness remain out of scope.

## Research baseline

- Installed Qwen Code is `0.21.13`.
- `qwen serve` is now the strongest official programmatic surface: an
  experimental loopback HTTP daemon with REST, typed SSE, sessions, prompts,
  cancellation, permissions, capabilities, context usage, and owned shutdown.
- A live model-free inspection started on an OS-assigned port with bearer auth,
  reported protocol `v1`, exact Windows workspace, and 112 feature tags.
- Live `/capabilities` confirmed `session_scope_override`, `session_prompt`,
  `session_cancel`, `session_events`, `typed_event_schema`,
  `session_permission_vote`, `session_context_usage`, `session_close`, and
  `require_auth`.
- The inspection sandbox blocked ACP child process creation with `spawn EPERM`;
  the daemon itself remained healthy. Listener/process E2E must run outside the
  sandbox, matching the established repository test boundary.
- Official protocol requires treating prompt HTTP 202 as admission only and
  correlating `turn_complete` or `turn_error` by `promptId` on SSE.

## Boundaries

- Bind only `127.0.0.1`, allocate an ephemeral port, require an AR-generated
  bearer token, disable the web shell, and manage only the AR-owned process.
- Use isolated `QWEN_HOME`; do not read or overwrite the user's Qwen settings,
  credentials, sessions, extensions, skills, hooks, MCP servers, or memory.
- Configure exactly one OpenAI-compatible local provider for the selected
  Ollama endpoint and model. Do not permit silent cloud fallback.
- Use safe mode, telemetry off, automatic updates off, recording off, managed
  memory off, hooks off, and a closed core-tool allowlist. Shell, MCP,
  subagents, web/network, external directories, Git writes, and unrestricted
  process execution remain unavailable.
- Qwen native workspace reads/searches/edits may run only under its permission
  mediation and exact registered workspace. Agentic Router independently
  observes and proves file effects; model narration never owns completion.
- Speak REST/SSE/JSON directly from C#. Add no Node dependency to AR; the
  installed Qwen executable remains an optional external harness dependency.

## Implementation

1. Add `qwen-code` identity and capability definition to the generic registry.
2. Implement availability/version discovery for the configured executable,
   managed npm installation, and PATH fallback.
3. Write an isolated Qwen configuration containing only the exact selected
   Ollama model/provider and reviewed core tools.
4. Start `qwen serve` on loopback with bearer auth, safe mode, OS-assigned port,
   explicit workspace, bounded session/prompt limits, no web shell, and owned
   lifecycle cleanup.
5. Verify health, protocol v1, required capabilities, exact workspace, and
   reported Qwen version before admitting a turn.
6. Create one `sessionScope: thread` Qwen session per AR conversation, verify
   the exact active model, subscribe with `Last-Event-ID`, submit one prompt,
   and correlate all terminal events by returned `promptId`.
7. Normalize assistant/thought chunks, tool call lifecycle, usage, permission
   requests, warnings, crashes, cancellation, and terminal states while
   retaining cloned native payloads.
8. Map only explicit Qwen permission options to Host approval. Reject ambiguous
   or unmappable permissions safely.
9. Add a deterministic fake Qwen daemon and browser/API E2E for discovery,
   authentication, startup, capability preflight, exact workspace/model,
   session reuse, SSE, tools, permissions, usage, cancellation, crash,
   malformed/unknown events, cleanup, native preservation, and one terminal.
10. Document installed-version behavior, capability mapping, security
    boundaries, and exact manual validation steps.

## Validation

1. Run focused Qwen Code plus Codex/OpenCode regression E2E using fake external
   boundaries only.
2. Run `dotnet format AgenticRouter.slnx --verify-no-changes`.
3. Run a zero-warning Release build and the full Playwright E2E suite.
4. Run `git diff --check` and inspect the complete intended diff while
   preserving the existing dirty worktree.
5. Do not run a real Qwen Code/Ollama task without a separate explicit
   real-inference authorization; manual acceptance remains the real E2E gate.

## Completed evidence

- Installed-version/model-free probe: Qwen Code `0.21.13`, protocol `v1`, exact
  workspace, required daemon features present; daemon stopped cleanly.
- Focused Qwen/discovery/selector E2E: 9/9 passed.
- Combined Qwen and Settings regression after current-section test maintenance:
  13/13 passed.
- Full deterministic Playwright E2E: 221/221 passed, zero skipped.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed.
- Isolated full-solution Release build: zero warnings and zero errors. The
  ordinary Release output remained intentionally untouched because the user's
  Visual Studio Agentic Router process held it open.
- No real Qwen Code prompt, Ollama generation, cloud call, or GPU inference was
  run. Manual acceptance is still required.
- After the manual HTTP 500 correction, the auth-gate E2E passed, all Qwen Code
  E2E passed 7/7, and the complete deterministic suite passed again 221/221.
- After the manual `invalid_client_id` correction, the fake assigned a distinct
  server-owned ID, all Qwen Code E2E passed 7/7, and the complete deterministic
  suite passed again 221/221.
- After the manual exact-model correction, the fake returned the installed
  `_meta.qwenModelSwitch` envelope and a distinct ACP route ID; all Qwen Code
  E2E passed 7/7 and the complete deterministic suite passed again 221/221.
- After the manual `session_not_found` correction, the fake forbade redundant
  model mutation and required read-only context/provider verification; all
  Qwen Code E2E passed 7/7 and the complete deterministic suite passed again
  221/221.
- After the manual 1,269-event empty-result report, daemon and persisted-chat
  evidence proved that Qwen completed 10 successful workspace reads and wrote
  a 2,194-character final answer. The adapter had discarded it because Qwen
  `0.21.13` nests each typed update under `data.update`, while the adapter and
  fake both incorrectly read `data.sessionUpdate`. The fake now reproduces the
  installed envelope. A 1,000-plus-frame regression proves that known daemon
  state updates are ignored, text is coalesced, the complete answer survives,
  unknown diagnostics are bounded, and an empty `end_turn` fails reviewably.
  Focused Qwen E2E passed 9/9; the final complete deterministic suite passed
  223/223 with zero skips. Release build remained at zero warnings/errors,
  format verification passed, and `git diff --check` passed.

## Manual gate

After automated validation, report exactly:

`QWEN CODE EXPERIMENTAL HARNESS READY FOR MANUAL TEST`

Then stop. Do not implement Claude Code, Goose, DeepSeek Harness, or another
harness until the user manually accepts Qwen Code.

## Manual acceptance correction

The first real manual request reached the owned Qwen daemon but session
creation returned HTTP 500 with `Authentication required`. Installed Qwen Code
`0.21.13` requires `security.auth.selectedType` before its ACP session can
resolve the selected `modelProviders` entry. The isolated configuration now
sets the exact auth type `openai`; the model entry continues to use only the
placeholder `OLLAMA_API_KEY=ollama` and the selected local Ollama `/v1`
endpoint. The deterministic fake rejects session creation unless both the auth
type and referenced environment credential are present.

The next manual attempt exposed the daemon's authoritative client identity
contract. On a fresh session Qwen Code may ignore the requested
`X-Qwen-Client-Id`, generate `client_<UUID>`, and return it as `clientId` from
`POST /session`. The adapter now uses that returned ID for SSE, model, context,
prompt, permission, cancellation, and close requests. The fake daemon assigns
a different ID and rejects the requested ID on every subsequent session route.

The third manual attempt exposed the installed model-switch response shape.
`POST /session/:id/model` returns the effective model under
`_meta.qwenModelSwitch`, while `/session/:id/context` reports an opaque ACP
route ID as `currentModelId`. The adapter now validates the exact effective
model, `openai` transport, and selected Ollama `/v1` endpoint from the switch
envelope, then independently verifies that the context's current route exists
in its advertised model catalog. The fake uses the real envelope and a route ID
that intentionally differs from the model tag.

The fourth manual attempt reached SSE and failed with `session_not_found`.
Incident evidence identified `qwen-code-event-stream` as the failing stage:
the explicit model mutation had triggered asynchronous runtime reconciliation
after session creation. Because the isolated daemon already starts with one
exact configured model, AR no longer mutates the model after creating a
session. It verifies the initial selection by joining session context with
`GET /workspace/providers`, including exact base model, current ACP route,
`openai` transport, workspace, and selected Ollama `/v1` endpoint.

The fifth manual attempt completed but surfaced 1,269 execution events and no
useful answer. Qwen daemon evidence showed 1,260 SSE frames, including a large
`available_commands_update`; its persisted chat contained 10 successful
`read_file` results and the full final answer. The installed typed event shape
is `data: { sessionId, update: { sessionUpdate, ... } }`. The adapter and fake
had both used the flat shape `data: { sessionUpdate, ... }`, so every valid
update was retained as an unknown native diagnostic. The adapter now parses
the nested update, drops known state-only updates, deduplicates and caps unknown
diagnostics at eight per turn, coalesces adjacent text chunks, and rejects a
terminal `end_turn` with no assistant output.
