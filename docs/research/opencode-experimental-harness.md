# OpenCode experimental harness integration

Date: 2026-08-18  
Installed OpenCode: `1.18.18`

## Decision

Agentic Router integrates OpenCode through its owned headless HTTP server. The
adapter speaks HTTP, SSE, and JSON directly from C# and remains behind the same
generic harness registry used by Native and Codex. Native remains the default.

The adapter starts only its own process, binds it to `127.0.0.1`, assigns an
AR-generated Basic Auth password, and isolates config, data, cache, and state
under `data/opencode-runtime`. It does not attach to or terminate another
OpenCode process.

## Sources and installed-version checks

- [OpenCode server documentation](https://opencode.ai/docs/server/)
- [OpenCode providers](https://opencode.ai/docs/providers)
- [OpenCode configuration](https://opencode.ai/docs/config/)
- [OpenCode permissions](https://opencode.ai/docs/permissions/)
- [Ollama OpenCode integration](https://docs.ollama.com/integrations/opencode)
- Live `http://127.0.0.1:<port>/doc` from installed OpenCode `1.18.18`

The installed `/doc` response is OpenAPI 3.1 JSON. It confirmed health, event,
session creation, asynchronous prompt, abort, diff, status, and permission
reply contracts used by the adapter.

## Material discrepancies

1. Installed `opencode serve --port 0` listened on port `4096` rather than an
   OS-assigned ephemeral port. AR therefore reserves a free loopback port and
   passes that explicit value.
2. The installed API exposes the SSE stream at `/event`; the current overview
   documentation also describes `/global/event`. The adapter follows the live
   installed OpenAPI contract.
3. Isolating only `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, and `XDG_CACHE_HOME` was
   insufficient on Windows: the process still tried the default user state
   directory. AR also sets `XDG_STATE_HOME`.

## Provider and workspace contract

The owned `opencode.json` contains one custom OpenAI-compatible provider named
`agentic-router-ollama`, whose base URL is the selected Ollama endpoint plus
`/v1`. It exposes only the exact AR-selected model and makes that provider/model
the configured default. Updates, sharing, plugins, inherited instructions,
shell, subagents, network tools, and external directories are disabled.

Session creation and every prompt carry the exact trusted Windows workspace
path. AR rejects a different directory returned by OpenCode. One OpenCode
session is reused per AR conversation while the owned server remains alive.

## Capability mapping

| Capability | Exposed | Boundary |
|---|---:|---|
| Streaming and thinking | Yes | SSE text and reasoning deltas |
| Session reuse/resume | Yes | Same owned server and AR conversation |
| Cancellation | Yes | Session abort plus request cancellation |
| Tool events | Yes | Native lifecycle normalized; payload retained |
| Native permissions | Yes | Mapped to Host approval; destructive requests remain explicit |
| Structured edits | Yes | OpenCode native edits, followed by Host observation |
| Session diff | Yes | Queried at terminal idle |
| Stale protection | No | Host observation does not claim OpenCode-native stale binding |
| Subagents | No | `task` is disabled |
| Sandbox | No | Workspace and permission controls are not presented as a sandbox |

Harness terminality and Host completion remain separate. OpenCode idle/error,
malformed SSE, unexpected exit, and cancellation are normalized, while AR
independently hashes workspace effects and produces exactly one public terminal
stream event.

## Automated evidence

The deterministic fake OpenCode executable exercises real AR process startup,
loopback HTTP, Basic Auth, SSE, session reuse, exact provider/model/workspace,
reasoning, text, tools, permissions, diff, abort, malformed data, unexpected
native data, and process crash. It uses no model and no cloud service.

A real OpenCode/Qwen task was intentionally not run during implementation: the
existing authorization covered the real Qwen GPU-placement smoke, not a second
real-model harness task. That final behavior remains the manual acceptance
gate.

## Manual acceptance correction

The first real manual turn exposed a version-specific event-mapping defect.
OpenCode `1.18.18` sends both assistant text and reasoning through
`message.part.delta` with `field: "text"`. The discriminator is the preceding
`message.part.updated.properties.part.type`, which is `text` or `reasoning`.
Treating the delta field alone as the content kind leaked reasoning into the
visible answer.

The adapter now tracks each part before accepting deltas, backfills only unseen
full-part text, and normalizes current tool-part state transitions. Unknown
parts remain diagnostic native events instead of defaulting to assistant text.
The installed-version source also confirms assistant usage under
`message.updated.properties.info.tokens`; AR publishes an initial conservative
context estimate and replaces it with input plus cache read/write when OpenCode
reports final usage.

Sources:

- [OpenCode 1.18.18 session schema](https://github.com/anomalyco/opencode/blob/v1.18.18/packages/opencode/src/session/message-v2.ts)
- [OpenCode 1.18.18 stream processor](https://github.com/anomalyco/opencode/blob/v1.18.18/packages/opencode/src/session/processor.ts)
