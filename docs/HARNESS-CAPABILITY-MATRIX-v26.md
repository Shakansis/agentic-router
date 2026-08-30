# Harness capability matrix v26

Audit date: 2026-08-27

> Historical v26 audit: this matrix predates the Claude Code adapter and is not
> the current five-harness contract. See
> [Current harness capability matrix](harness-capability-matrix.md). The detailed
> v26 implementation and manual-test record below is preserved as evidence.

## Reviewed implementations and versions

- Agentic Router Native: product `v0.9.14`, checkout `ea2addd76ed0` plus the
  uncommitted v25/v26 working tree.
- OpenAI Codex CLI/App Server: `codex-cli 0.149.0-alpha.4`. The generated
  installed-version experimental App Server schema was inspected for
  `dynamicTools`, `item/tool/call`, approvals, command execution,
  `thread/start`, `thread/resume`, `turn/start`, `turn/interrupt`, and
  `turn/completed`.
- OpenCode: `1.18.18`.
- Qwen Code: `0.21.13`; installed bundled source and the daemon v1 capability
  contract were inspected in addition to upstream documentation.

Upstream evidence reviewed:

- OpenAI model/tool guidance for shell, apply-patch, and server-side validation:
  <https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5.2>
- OpenCode tools, custom tools, permissions, and remote MCP:
  <https://opencode.ai/docs/tools/>,
  <https://opencode.ai/docs/custom-tools/>,
  <https://opencode.ai/docs/mcp-servers/>, and
  <https://opencode.ai/v2/docs/permissions>
- Qwen Code settings, tools, MCP, approval modes, hooks, and managed daemon
  guard:
  <https://qwenlm.github.io/qwen-code-docs/en/users/configuration/settings/>,
  <https://qwenlm.github.io/qwen-code-docs/en/developers/tools/introduction/>,
  <https://qwenlm.github.io/qwen-code-docs/en/users/features/mcp/>,
  <https://qwenlm.github.io/qwen-code-docs/en/users/features/approval-mode/>,
  <https://qwenlm.github.io/qwen-code-docs/en/users/features/hooks/>, and
  <https://github.com/QwenLM/qwen-code/blob/main/docs/users/qwen-serve.md>

## Classification rules

- `NATIVE`: the harness supplies the semantic capability safely.
- `HOST_BRIDGE`: AR supplies the capability through Host policy/services.
- `MISSING_ADAPTER`: a supported harness extension exists but AR does not
  connect it.
- `UNSUPPORTED`: no reasonable supported mechanism exists for the audited
  semantic capability.
- `SECURITY_CONFLICT`: the available native mechanism violates the named AR
  invariant; a safe Host equivalent may still exist.
- `NATIVE_EXTRA`: useful capability outside the AR common catalog.

When a cell says `NATIVE + HOST_BRIDGE`, the native advantage remains available
and the structured Host implementation is also guaranteed. No tool was removed
to normalize tool lists.

## AR-common capability matrix

| Semantic capability | Native | Codex | OpenCode | Qwen Code |
|---|---|---|---|---|
| List workspace entries | HOST_BRIDGE | NATIVE | NATIVE | NATIVE |
| Read workspace text | HOST_BRIDGE | NATIVE | NATIVE | NATIVE |
| Search/glob/grep | HOST_BRIDGE | NATIVE | NATIVE | NATIVE |
| File/directory metadata | HOST_BRIDGE | NATIVE | HOST_BRIDGE | HOST_BRIDGE |
| Create one text file | HOST_BRIDGE | NATIVE | NATIVE | NATIVE |
| Create bounded file batch | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Replace/write existing file | HOST_BRIDGE | NATIVE | NATIVE | NATIVE |
| Exact edit/replace/patch | HOST_BRIDGE | NATIVE | NATIVE | NATIVE |
| Create directory | HOST_BRIDGE | NATIVE | HOST_BRIDGE | HOST_BRIDGE |
| `delete_paths`, including recursive directory delete | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Structured process execution, including explicitly approved shell interpreters | HOST_BRIDGE | NATIVE + HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Saved validation profile | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Git status/diff/log/show | HOST_BRIDGE | NATIVE + HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Git stage/unstage/commit/tag | HOST_BRIDGE | NATIVE + HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Git push current branch/tag | HOST_BRIDGE | NATIVE + HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Optional create/revise execution plan | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| `Approve for me` mutation policy | HOST_BRIDGE | NATIVE + HOST_BRIDGE | NATIVE + HOST_BRIDGE | NATIVE + HOST_BRIDGE |
| `Ask for approval` mutation policy | HOST_BRIDGE | NATIVE + HOST_BRIDGE | NATIVE + HOST_BRIDGE | NATIVE + HOST_BRIDGE |
| Read-only pathless native action without approval | HOST_BRIDGE | NATIVE | NATIVE | NATIVE |
| Trusted-workspace boundary | HOST_BRIDGE | NATIVE + HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Stale/conflicting write protection | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Effect verification and truthful terminal result | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Streaming/tool activity/cancellation | NATIVE | NATIVE | NATIVE | NATIVE |
| Same-harness session resume | NATIVE | NATIVE | NATIVE | NATIVE |
| Same-turn supplemental user steering | UNSUPPORTED | NATIVE | UNSUPPORTED | NATIVE |
| Cross-harness canonical conversation hydration | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |
| Typed rejection returned for materially different recovery | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE | HOST_BRIDGE |

`MISSING_ADAPTER` is empty for the current AR-common catalog after v26.

## Operations outside the current AR-common catalog

| Capability | Native | Codex | OpenCode | Qwen Code | Reason |
|---|---|---|---|---|---|
| Canonical move/rename/copy action | UNSUPPORTED | NATIVE_EXTRA | SECURITY_CONFLICT | SECURITY_CONFLICT | AR has no canonical move/copy Host action. Codex retains sandboxed shell support. OpenCode/Qwen can do it only through their unrestricted native shell surfaces, which are not used as boundary bypasses. |
| Unmediated native shell | UNSUPPORTED | NATIVE_EXTRA | SECURITY_CONFLICT | SECURITY_CONFLICT | Unmediated shell authority remains outside the AR common boundary. The common Host `run_process` path accepts shell interpreters, labels their host-user authority, requires explicit approval even under `auto`, and may persist only a resolved executable + exact argument fingerprint + workspace working-directory rule that the user can revoke. |
| Web fetch/search in Execute | HOST_BRIDGE | HOST_BRIDGE + NATIVE_EXTRA | HOST_BRIDGE + NATIVE_EXTRA | HOST_BRIDGE + NATIVE_EXTRA | The canonical Host `web_search` is offered only when its configured integration is available. Codex/OpenCode/Qwen native web tools remain enabled, model-visible, and observable instead of being removed to normalize harnesses. |
| Session diff | HOST_BRIDGE | NATIVE_EXTRA | NATIVE_EXTRA | UNSUPPORTED | Host observation supplies the common diff/effect evidence. Codex/OpenCode native diff evidence is retained; the audited Qwen daemon contract does not expose session diff. |
| LSP diagnostics | UNSUPPORTED | UNSUPPORTED | NATIVE_EXTRA | SECURITY_CONFLICT | OpenCode's built-in LSP remains available. Qwen's experimental LSP loads workspace `.lsp.json` process definitions, which are ambient executable configuration outside the registered AR capability profile. |
| Todo/task-list state | UNSUPPORTED | UNSUPPORTED | NATIVE_EXTRA | NATIVE_EXTRA | Retained as non-effectful harness-local assistance where available. |
| Computer/application enumeration | UNSUPPORTED | UNSUPPORTED | UNSUPPORTED | NATIVE_EXTRA | Exact read-only `computer_use__list_apps` permission is pathless and is now authorized without a mutation prompt; it is not passed through filesystem validation. |
| Harness subagents/delegation | UNSUPPORTED | SECURITY_CONFLICT | SECURITY_CONFLICT | SECURITY_CONFLICT | The selected specialist must own the turn; recursive/autonomous delegation is outside the approved product scope. |
| Ambient/project MCP, plugins, extensions, or hooks | UNSUPPORTED | SECURITY_CONFLICT | SECURITY_CONFLICT | SECURITY_CONFLICT | Untrusted ambient extension code can bypass the registered AR capability set. The isolated AR-owned Codex dynamic tools/OpenCode MCP/Qwen top-tier MCP remain enabled. |
| Auto-memory/team-memory/save-memory | UNSUPPORTED | SECURITY_CONFLICT | UNSUPPORTED | SECURITY_CONFLICT | Would persist model-derived content outside AR's user-enabled minimum-fidelity persistence authority. |
| Worktrees/workflows/cron/background monitors | UNSUPPORTED | SECURITY_CONFLICT | UNSUPPORTED | SECURITY_CONFLICT | Can change workspace authority or continue effects beyond the bounded foreground turn and truthful completion proof. |

These `SECURITY_CONFLICT` entries are not harness limitations and are not used to
hide AR integration gaps. Each conflict names the invariant that the native
mechanism cannot currently satisfy. Safe AR-common equivalents remain exposed
where one exists.

## v26 adapter implementation

OpenCode and Qwen Code now consume one AR-owned, authenticated, loopback MCP
bridge. It:

1. exposes the existing canonical `LocalActionPlanner` JSON schemas;
2. accepts only an exact registered harness ID and a 256-bit bearer token passed
   to the owned child process through its environment;
3. advertises the full AR bridge catalog but enforces the active per-turn
   `HostCapabilityProfile` before emitting a call;
4. normalizes the call to `host-tool.requested`;
5. routes execution through `ExecuteHarnessHostToolAsync`, approval policy,
   `LocalActionService`, workspace/Git/process services, and effect proof;
6. returns success/failure to the same active specialist so recovery can
   continue;
7. closes outstanding calls when the turn ends.

OpenCode uses its supported remote MCP configuration. Qwen Code uses the
supported top-tier `--mcp-config` path so the AR-owned server remains available
under `--safe-mode`. Generated config contains only environment placeholders;
the bearer token is not persisted.

## Preserved native functionality

- Codex: workspace-write sandbox, shell/command execution, native filesystem
  edits, native web search, streaming reasoning/tool events, approvals,
  cancellation, and thread resume.
- OpenCode: read/list/glob/grep/edit/write/patch, web fetch/search, LSP/session
  diff, streaming events, permissions, cancellation, and session reuse.
- Qwen Code: list/read/glob/grep/edit/write, web fetch/search, todo state,
  exact read-only application enumeration, typed SSE, permissions,
  cancellation, and session context.
- Native: the existing provider-neutral Host tool loop remains unchanged behind
  the shared registry boundary.

No capability was disabled merely to make the harness lists identical.

## Browser message sequencing and steering update

The browser now owns a non-persistent editable follow-up queue for every
model/harness route, including Native and Claude Code. This is UI sequencing,
not a harness capability: after a turn ends, the next ready item is submitted
through the ordinary `/api/chat/stream` path. Editing any queued item blocks
automatic submission until the user saves or cancels the edit; cancelling the
active response pauses the queue.

Same-turn steering remains a separate, capability-gated operation:

- Codex maps to App Server `turn/steer` with the exact expected active turn ID.
- Qwen Code maps to the owned daemon session's `mid-turn-message` endpoint with
  an idempotent browser-generated message ID.
- OpenCode and Claude Code remain queue-only because their reviewed public
  contracts do not expose equivalent same-turn injection semantics.
- Native remains outside the steering contract.

The Host never converts steering into cancellation plus a new turn and never
silently falls back from `Steer` to `Queue`.

## Concise manual test plan

Use a disposable trusted Git workspace containing one pre-existing inspected
text file. Repeat each mutation once with `Approve for me` and once with
`Ask for approval`; in ask mode verify the file is unchanged before clicking
approve.

### Native

1. List/search/read the existing file, then create, edit, and delete a nested
   file/directory.
2. Run `dotnet --version` through structured process execution and inspect Git
   status/diff; optionally stage/unstage/commit in the disposable repository.
3. Attempt `../outside.txt`; verify a typed denial appears, the turn continues,
   and a subsequent internal path succeeds.
4. Switch Native -> each external harness and back; ask the target to cite an
   exact earlier conversation marker.

### Codex 0.149.0-alpha.4

1. Exercise native read/edit/sandboxed shell and Host `create_files`,
   `delete_paths`, Git, validation, and optional plan tools in the same turn.
2. Verify `Approve for me` has no duplicate prompt and `Ask for approval` waits
   for every Host/native mutation.
3. Attempt an external path and confirm only that action is denied; Codex must
   recover without the AR request terminating.
4. Switch away and back; verify thread resume or canonical hydration preserves
   the earlier task marker and Host-observed file state.
5. Verify the model catalog and thread report the same Host-resolved context,
   compact at 98% of that total window, and perform at most one cause-aware
   continuation after an allowlisted transient stream/App Server failure.

### OpenCode 1.18.18

1. Confirm native read/glob/grep/edit plus AR MCP `create_files`, directory,
   delete, process, Git, validation, and optional plan tools.
2. Confirm web fetch/search remains present as a native extra and native `bash`
   remains unavailable; use Host `run_process` for commands.
3. Repeat mutations in both approval modes and attempt `../outside.txt`, then
   recover with an internal path.
4. Switch OpenCode in every direction with Native/Codex/Qwen and verify the
   canonical marker and session reuse.

### Qwen Code 0.21.13

1. Confirm native list/read/glob/grep/edit/write plus top-tier AR MCP directory,
   delete, process, Git, validation, and optional plan tools.
2. Ask to list installed applications; verify `computer_use__list_apps` runs in
   both approval modes without a filesystem-path error or mutation prompt.
3. Confirm web fetch/search and todo remain native extras; native shell/monitor
   is not used and structured Host `run_process` works instead.
4. Repeat mutations in both approval modes, exercise external-path denial plus
   recovery, and switch Qwen in every direction while checking the earlier
   canonical marker.

For every harness, accept completion only if the review surface agrees with the
actual final filesystem and Git state. A narrated success without the observed
effect is a failure.
