# Current harness capability matrix

Audited from the registered `HarnessDefinition` and
`HarnessCapabilityProjection` implementations on 2026-08-30, with steering
re-audited on 2026-10-09. Executable
availability and version are discovered at runtime; a registered adapter is not
proof that its CLI is installed or that a particular model behaves correctly.

All five harnesses execute through `IHarnessRegistry`. Native is built in. The
four external adapters accept only `ollama-local`; cloud providers such as Groq
use the Native/provider path and are not silently tunneled through an external
harness.

| Capability | Native | Codex | Claude Code | OpenCode | Qwen Code |
| --- | --- | --- | --- | --- | --- |
| Streaming and thinking | Yes | Yes | Yes | Yes | Yes |
| Same-harness resume | No | Yes | Yes | Yes | Yes |
| Cancellation, approvals, tool events | Yes | Yes | Yes | Yes | Yes |
| Structured edits | Yes | Yes | Yes | Yes | Yes |
| Adapter-declared stale protection | Yes | No | No | No | Yes |
| Native sandbox | No | Yes | No | No | No |
| Session diff | Yes | Yes | No | Yes | No |
| Harness-native permission protocol | No | Yes | Yes | Yes | Yes |
| Same-turn steering | No | Yes | No | Yes | Yes |
| Native web capability | No | Yes | Yes | Yes | Yes |
| Subagents exposed by Agentic Router | No | No | No | No | No |

## Host capability parity

The active turn receives one Host-owned closed capability profile. Native uses
the full Host catalog directly. External adapters keep reviewed native tools and
receive every remaining allowed canonical tool through the Host bridge:

- Codex implements `apply_patch` natively;
- Claude Code implements `read_file`, `replace_text`, and `apply_patch` through
  its reviewed native Read/Edit/Write surface;
- OpenCode implements `create_file`, `write_file`, `replace_text`, and
  `apply_patch` natively;
- Qwen Code uses the Host bridge for the common canonical catalog while keeping
  reviewed native read/search/edit and web tools available.

`MissingAdapterTools` is empty for every registered harness. Host validation,
trusted-workspace confinement, approvals, effect proof, recovery bounds, and
terminal truth remain authoritative even when a harness performs part of the
operation natively.

Codex's local-model catalog enables its native freeform patch engine. The
provider wire adapter translates that exact tool to Ollama JSON functions and
back. Its native profile is read-only with on-request approvals so every patch
reaches Host path validation; the user's AR auto/ask setting still controls
whether a browser approval is required. Source and move destination are both
validated, including paths under ambient temporary directories.

## Important route distinctions

- Web availability is derived from the effective provider/model/harness route;
  availability never triggers an eager search.
- Codex, Qwen Code, and OpenCode support same-turn steering. OpenCode appends
  context through the legacy session API's `noReply` request without starting
  another loop; native admission and terminal races are verified by the Host.
  Native and Claude Code currently use the browser follow-up queue only.
- Codex opts into `instant_interrupt` (introduced upstream in 0.159.0). Qwen and
  OpenCode support injection at native loop boundaries, which does not imply
  instant interruption of a model response. Qwen success now requires a matching
  receipt and reconciled consumption; promotion out of the active turn is rejected.
  See the [current audit](research/harness-steering-and-releases-2026-10-09.md).
- The six Useful now upstream fixes were adopted with Codex 0.162.0,
  OpenCode 1.18.35, Qwen Code 0.25.0 and Claude Code 2.1.295. The adapters preserve
  isolation across changed defaults: Qwen cross-session messaging is disabled;
  Claude's two new built-in plugins are disabled and its dedicated MCP credential
  variable works with subprocess credential scrubbing still enabled.
- Claude Code exposes Read/Glob/Grep/Edit/Write/WebSearch/WebFetch but not an
  ambient shell, plugins, skills, subagents, or unowned MCP configuration.
- Unsupported ambient capabilities are not compensated with an unrestricted
  Host shell or filesystem escape.

The older [`HARNESS-CAPABILITY-MATRIX-v26.md`](HARNESS-CAPABILITY-MATRIX-v26.md)
is retained as the pre-Claude historical v26 audit and manual test record.
