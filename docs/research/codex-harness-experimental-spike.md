# Codex experimental harness spike

Date: 2026-08-17

Implementation plan: [`docs/PLAN-v12-codex-experimental-harness.md`](../PLAN-v12-codex-experimental-harness.md)

## Result

Agentic Router now offers an optional `Codex (Experimental)` harness in Execute
mode while retaining Native as the default. The former conversation model-lock
control and its request/state behavior were removed. Selecting Codex starts one
lazy Agentic-Router-owned `codex app-server`, maps the current Agentic Router
conversation to a Codex thread, and reuses that thread for later turns in the
same browser conversation.

The integration is ready for continued real side-by-side testing against
Native. Deterministic fake-App-Server coverage and one explicitly authorized
real `qwen3.8:27b` turn both passed. No cloud call was run.

## Integration boundary

- Agentic Router still owns routing, the exact selected model, trusted-workspace
  validation, approvals, cancellation, observed file effects, execution-session
  state, and the terminal answer.
- The adapter owns only the supported App Server JSONL protocol and the process
  it launches. It initializes over stdio, starts or resumes a thread, starts and
  interrupts turns, answers supported approval requests, and translates events.
- `create_files` and `delete_files` are Host-owned structured batch contracts
  shared by Native and Codex. Codex receives them through experimental
  `dynamicTools`; both harnesses execute the same path validation, approval,
  rollback, and effect-proof implementation.
- App Server stdin, stdout, and stderr are explicitly UTF-8. Protocol reasoning
  and assistant text retain Portuguese accents without console-codepage
  reinterpretation.
- Codex receives an isolated Agentic-Router-owned `CODEX_HOME`, the built-in
  `ollama` provider, the configured local Ollama endpoint, the exact selected
  model, and the active trusted workspace. It does not read or mutate the
  user's ordinary Codex configuration.
- The isolated runtime disables plugin/catalog and remote-control features,
  web search, dependency installation, analytics, feedback, memories, and
  multi-agent features. App Server is launched with explicit stdio transport
  and strict configuration validation.
- Codex is rejected for cloud targets and never silently falls back to Native,
  another provider, or another model.
- `AgenticRouter__Codex__ExecutablePath` is the stable startup override when a
  `codex.exe` discovered through `PATH` is not launchable. Discovery failures,
  Windows access denial, and other startup failures have distinct typed errors;
  no versioned or hash-named installation path is embedded in the product.

This follows the documented experimental [Codex App Server protocol](https://developers.openai.com/codex/app-server)
and the built-in Ollama provider described in the [Codex configuration reference](https://developers.openai.com/codex/config-reference).

## Event translation and evidence

Codex reasoning deltas feed the existing Thinking surface; assistant deltas are
the only Codex content added to the visible answer. Command and file-change
notifications become ordinary activity events, command output remains activity
detail, and warnings/errors remain typed non-answer events. Unknown protocol
notifications are ignored and logged instead of being interpreted.

Agentic Router snapshots the bounded workspace before and after each turn,
rejects reparse-point traversal, and hashes protected Git control state. It then
records only observed creates, modifications, and deletions. An App Server
`completed` notification is recorded as lifecycle evidence only and cannot
promote the objective beyond Host-observed effects and validation facts.

File-change approvals are mapped only when their paths can be confined to the
trusted workspace. Deletion authority is bound to the exact approved paths;
unapproved observed deletions are restored from the bounded pre-turn snapshot
and fail the turn. An unmappable command approval is declined and returned to
Codex once as a visible correction so it can use `create_files` or
`delete_files`; a repeated unsupported approval fails with a typed terminal
result. Shell text is never parsed into a file action.

After a successful Codex turn with observed workspace changes, the Host runs
the configured validation profile automatically. When none exists, it records
and displays `not-configured`; validation is no longer silently left as
`not-run`.

Cancellation sends `turn/interrupt`. If the turn does not acknowledge the
interrupt within the bounded grace period, Agentic Router terminates only the
App Server process tree it owns. Unexpected process exit produces a typed,
reviewable harness failure.

## Deterministic coverage

A small fake App Server executable exercises the real browser/API path without
invoking Codex or Ollama. Focused coverage proves:

- Native remains selected by default and does not start Codex;
- the new Execute-only selector is present and the model lock is absent;
- reasoning, assistant text, command/file activity, and observed create/edit
  effects stream through the existing UI;
- a second turn reuses the same Codex thread;
- cancellation interrupts an active turn;
- deletion waits for explicit approval and is bound to the requested file;
- Native and Codex both create UTF-8 files through one `create_files` action;
- Codex deletion uses one editable `delete_files` approval for multiple paths;
- a declined shell deletion recovers once through the structured Host tool;
- accented reasoning and assistant deltas cross JSONL without mojibake;
- changed Codex work records automatic validation as passed, failed, or
  explicitly not configured;
- a missing Codex executable fails clearly while Native remains usable; and
- cloud selection is rejected without fallback.

The broader cloud-provider E2E was also made deterministic after model-lock
removal by waiting explicitly for the selected model's capability refresh and
the turn's terminal event rather than relying on checkbox interaction latency.

A model-free compatibility probe against installed `codex-cli
0.147.0-alpha.6.6` reproduced trace `0HNNS2O6L5V7U:00000065`: `initialize`
succeeded, while `thread/start` rejected the camelCase policy values. The
version-generated schema required `approvalPolicy = "on-request"` and the
legacy thread sandbox value `"workspace-write"`; the corrected handshake then
returned exact `model = "qwen3.8:27b"` and `modelProvider = "ollama"`. No
`turn/start`, model request, or GPU inference was executed. Explicitly disabling
`plugins` and `remote_control` also removed the startup plugin synchronization
observed in the initial probe. A second model-free probe established that the
isolated Windows runtime requires `windows.sandbox = "unelevated"` to return an
effective `workspaceWrite` sandbox; the adapter now verifies that returned fact
before any turn can start.

Executed evidence:

- focused batch/approval/UTF-8 E2E: 5/5 passed;
- complete Playwright browser/API suite: 201/201 passed in 5m19s;
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed;
- `dotnet build AgenticRouter.slnx -c Release`: passed with zero
  warnings and zero errors; and
- real Codex/Ollama validation: authorized `qwen3.8:27b` Execute turn completed
  in 1m11s. Codex inspected `hangman.html`, requested one Host `delete_files`
  batch for `random1` through `random12`, waited for approval, removed exactly
  those 12 files, and left the five game files present. The rendered assistant
  response contained correct accented Portuguese with no observed `ÔÇ` or `Ã`
  mojibake. Host validation ran and reported `not-configured`, matching the
  active workspace configuration.

## Known limitations

- Thread continuity is in-memory and lasts only for the owning App Server and
  Agentic Router process lifetime; Codex thread IDs are not persisted in
  conversation storage.
- The adapter deliberately serializes Codex turns. Parallel App Server turns
  were outside this bounded experiment.
- Workspace observation is capped at 5,000 files. Exceeding the bound fails the
  turn rather than returning incomplete evidence.
- Only batch creation and deletion are exposed as Host dynamic tools in this
  spike. Other Codex command approvals remain declined and receive at most one
  structured-tool recovery opportunity.
- App Server and `dynamicTools` remain experimental even though the installed
  `codex-cli 0.147.0-alpha.6.6` completed the authorized exact-model turn.

## Readiness decision

Continue controlled side-by-side testing. The installed App Server preserved
the tested protocol shapes and exact Ollama model while Agentic Router owned the
batch approval, independently verified the filesystem effects, and recorded
validation truth. Native remains the production baseline; this evidence does
not justify replacing it or weakening Host authority.
