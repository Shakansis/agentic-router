# Qwen Code experimental harness integration

Date: 2026-08-19  
Installed Qwen Code: `0.21.13`

## Decision

Agentic Router integrates Qwen Code through the official experimental
`qwen serve` HTTP bridge. The adapter speaks REST, SSE, and JSON directly from
C# and remains behind the generic harness registry. Native remains the default.

The adapter owns one loopback-only daemon, assigns an AR-generated bearer
token, and isolates `QWEN_HOME` under `data/qwen-code-runtime`. It does not
attach to, reconfigure, or terminate a Qwen Code process started by the user.

## Sources and installed-version checks

- [Qwen Code serve protocol](https://github.com/QwenLM/qwen-code/blob/main/docs/developers/qwen-serve-protocol.md)
- [Qwen Code daemon quickstart and operations](https://github.com/QwenLM/qwen-code/blob/main/docs/developers/daemon/20-quickstart-operations.md)
- [Qwen Code daemon documentation index](https://github.com/QwenLM/qwen-code/blob/main/docs/developers/daemon/00-index.md)
- [Qwen Code model providers](https://github.com/QwenLM/qwen-code/blob/main/docs/users/configuration/model-providers.md)
- [Qwen Code TypeScript SDK](https://github.com/QwenLM/qwen-code/blob/main/docs/developers/sdk-typescript.md)
- Live model-free `qwen serve` inspection of installed Qwen Code `0.21.13`

The live daemon reported protocol `v1`, the exact Windows workspace, and 112
feature identifiers. Required features include session creation and scope,
prompt admission, SSE events, cancellation, typed events, model selection,
permission voting, context usage, session close, and bearer authentication.
Prompt HTTP 202 is admission only; the adapter waits for a correlated
`turn_complete` or `turn_error` event with the returned `promptId`.

The sandbox prevented Qwen's ACP child preheat with `spawn EPERM`, while the
daemon itself remained healthy. Browser/server E2E therefore runs outside the
sandbox, as already required by this repository's local-listener tests.

## Provider and workspace contract

The isolated `settings.json` defines only the exact selected model through the
OpenAI-compatible provider surface, pointed at the selected Ollama endpoint
plus `/v1`. Retries, telemetry, updates, managed memory, hooks, MCP servers,
web access, shell execution, skills, subagents, workflows, and cron tools are
disabled. The closed core-tool list contains only workspace listing, reading,
glob, literal search, edit, and write operations.

The daemon starts with `--workspace` set to the exact trusted root,
`--require-auth`, `--safe-mode`, `--no-web`, bounded sessions and prompt
deadlines. AR verifies the workspace reported by capabilities and verifies the
initial configured model read-only by joining session context with workspace
provider metadata. It uses `sessionScope: thread`; one Qwen session is reused
per AR conversation while the owned daemon remains alive.

## Capability mapping

| Capability | Exposed | Boundary |
|---|---:|---|
| Streaming and thinking | Yes | Typed SSE assistant and thought chunks |
| Session reuse/resume | Yes | One Qwen thread session per AR conversation |
| Cancellation | Yes | Correlated session prompt cancellation |
| Tool events | Yes | Native lifecycle normalized; payload retained |
| Native permissions | Yes | Only explicit allow-once/reject-once options map to Host approval |
| Structured edits | Yes | Closed Qwen file tools plus Host effect observation |
| Stale protection | Yes | Qwen read/edit workflow plus Host post-effect proof |
| Session diff | No | Qwen serve v1 has no equivalent session-diff endpoint |
| Subagents | No | Agent/team tools are denied |
| Sandbox | No | Safe mode and workspace confinement are not claimed as an OS sandbox |

The first unknown event of each native type is preserved as diagnostic
activity, with at most eight such diagnostics per turn. Known daemon state
updates are ignored and adjacent text chunks are coalesced. Malformed SSE,
daemon exit, permission ambiguity, cancellation, empty successful turns, and
terminal events are typed; the public AR stream still ends exactly once.
Harness terminality cannot override Host-owned effect proof or objective
completion.

## Automated evidence

The deterministic fake Qwen executable exercises real AR-owned process
startup, bearer authentication, capability preflight, exact workspace/model,
thread reuse, SSE, thought and answer separation, tool lifecycle, exact usage,
permission voting, cancellation, malformed events, future native events, and
daemon failure. It invokes neither Ollama nor a cloud provider.

A real Qwen Code/Ollama request was intentionally not run during automated
validation. That GPU/model use remains the manual acceptance gate.

## Manual acceptance correction

The first real request exposed an ACP bootstrap requirement that is earlier
than model selection. Qwen Code `0.21.13` returned HTTP 500 because the isolated
settings declared the OpenAI-compatible model catalog but omitted
`security.auth.selectedType`. Its ACP implementation rejects session creation
when no auth type is selected, before resolving the selected model's custom
`envKey`.

The isolated settings now select `openai` explicitly. The provider still uses
`OLLAMA_API_KEY=ollama`, the exact selected Ollama `/v1` endpoint, and the exact
model ID; no user Qwen login or real OpenAI key is read or persisted. The fake
daemon reproduces the session-auth gate so this configuration cannot regress
silently.

The second manual attempt exposed another installed-version contract: the
client ID returned by `POST /session` is authoritative. Qwen Code can replace a
caller-requested ID with `client_<UUID>` for a new session; using the requested
value afterward returns `400 invalid_client_id`. AR now retains and uses only
the returned ID. The deterministic fake intentionally returns a different ID
and validates it on every session-scoped request.

The third manual attempt showed that model identity has two representations.
The model-switch endpoint returns the exact effective model, auth type, and
base URL under `_meta.qwenModelSwitch`; session context exposes an opaque ACP
route ID that points into `availableModels`. AR now validates the exact Ollama
tag and `/v1` endpoint from the authoritative switch result, and treats the
context route only as an internal catalog reference. The fake deliberately
uses different exact-model and route IDs so those contracts cannot be confused
again.

The fourth manual attempt showed that a redundant post-creation model switch
can race the daemon's asynchronous runtime reconciliation and remove the
session before SSE subscription. AR now relies on the exact model already
written before daemon startup and performs a read-only verification across
session context and `GET /workspace/providers`. No model mutation occurs after
session creation.

The fifth manual attempt showed 1,269 execution events and only the
Host-authored inspected-only fallback. The daemon had actually completed the
turn: its persisted chat contained 10 successful `read_file` results and a
2,194-character final answer. Qwen `0.21.13` sends typed session updates as
`data.update.sessionUpdate`; the adapter and deterministic fake had modeled
the obsolete flat location `data.sessionUpdate`. This converted every valid
thought, tool, answer, usage, and state update into an unknown native event.

The parser and fake now use the installed nested envelope. State-only updates
such as `available_commands_update`, mode/config/session metadata, plans, and
echoed user chunks do not enter the activity timeline. Unknown future event
types are deduplicated and capped at eight diagnostics per turn. Thought and
answer chunks are coalesced in bounded groups while the first chunk remains
immediate for progress/cancellation. A correlated `end_turn` with no assistant
text is now a typed `qwen-code-empty-response` failure rather than a successful
empty result. A deterministic 1,000-plus-frame test retains the complete answer
while producing fewer than 40 public Host events.

## Manual acceptance steps

1. Restart the Agentic Router instance so the newly registered adapter is
   loaded; do not reuse the pre-change process still running from Visual Studio.
2. Confirm `qwen --version` reports the installed Qwen Code and Ollama exposes
   the intended exact local model tag.
3. Open Agentic Router, select a trusted disposable Git workspace, switch to
   **Execute**, choose the exact local model, and select
   **Qwen Code (Experimental)**.
4. With automatic workspace approval selected, ask it to create one uniquely
   named text file containing a unique sentence, read it back, then report the
   verified result.
5. Confirm activity shows Qwen Code as the harness, the exact selected model,
   thinking separated from the visible answer, tool start/completion, exactly
   one terminal result, and the changed file in review.
6. Verify the file and Git working-tree diff independently. Then start a longer
   read-only request and press Cancel; confirm it terminates without a late
   assistant completion.
7. Report the observed UI result, file/diff result, cancellation result, and
   GPU memory placement before authorizing another harness integration.
