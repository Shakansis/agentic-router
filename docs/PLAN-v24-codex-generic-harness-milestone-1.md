# PLAN v24: Codex Generic Harness Milestone 1

## Goal

Reinspect the completed generic harness boundary and migrate or correct the
existing Codex App Server integration so every normal Codex Execute turn flows
through `IHarnessRegistry` and `IAgentHarness`, while preserving the current
Codex protocol, Host authority, Native behavior, selected provider/model,
exact Windows workspace, streaming, session continuity, cancellation, and
terminal facts.

## Evidence at start

- The checkout is intentionally dirty with completed Milestone 0 and later
  in-progress work. Preserve every unrelated change and avoid broad rewrites.
- Native, Codex, OpenCode, and Qwen Code are registered adapters in the current
  working tree. Milestone 0 changed `ChatStreamService` to select all of them
  through the registry and `IAgentHarness.ExecuteAsync`.
- Codex protocol code remains isolated in `CodexHarnessAdapter` and uses App
  Server JSONL rather than CLI-output parsing.
- Official current App Server documentation still requires the
  `initialize`/`initialized` handshake, thread start/resume, turn start,
  streamed notifications, `turn/completed`, and `turn/interrupt` for
  cancellation. It supports exact `cwd` and model overrides.
- Ambient `PATH` currently resolves an unlaunchable Desktop MSIX executable.
  A standalone managed Codex installation exists under the stable local
  `OpenAI/Codex/bin` root; discovery must report the actual launchable state.

## Investigation and implementation

1. Validate the installed standalone Codex version and generate its App Server
   JSON schema into a temporary directory. Compare the methods and events used
   by the adapter with the installed schema and official documentation.
2. Trace harness selection from browser/API request through registry,
   `IAgentHarness`, Codex transport, Host event mapping, session state, and
   cancellation. Identify any Codex-specific orchestration bypass.
3. Inspect discovery, version/capability reporting, exact workspace/model/
   provider propagation, thread creation/resume, native-payload preservation,
   malformed input handling, process exit, and terminal-event guarding.
4. Make only the smallest coherent corrections required for Codex to satisfy
   the generic boundary. Keep protocol-specific behavior inside the adapter or
   its native extension surface; do not add OpenCode behavior or change Native.
5. Reuse and tighten existing deterministic fake-App-Server E2E coverage for
   the required lifecycle, propagation, discovery, error, cancellation,
   continuity, native-payload, and exactly-one-terminal cases.

## Validation

1. Run focused registry, Codex adapter/lifecycle, Host, and UI/API E2E tests.
2. Run `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
3. Run a zero-warning Release build and the full deterministic Playwright E2E
   suite within the existing timeout.
4. Run `git diff --check` and inspect the complete intended diff, separating
   this milestone from pre-existing work.
5. Do not run real Ollama inference, a cloud request, model download/update, or
   manual Codex turn without separate explicit permission.

## Hard stop

After automated validation, report exactly
`CODEX GENERIC HARNESS MIGRATION READY FOR MANUAL TEST`, provide the requested
manual procedure, and stop. Do not begin Milestone 2 or OpenCode work.

## Completed evidence

- Normal Execute selection now resolves Native and every external harness from
  `IHarnessRegistry` and calls `IAgentHarness.ExecuteAsync`; Codex has no
  selector-side execution branch. Its App Server transport remains isolated in
  `CodexHarnessAdapter`.
- The remaining Codex-specific approval-recovery check was replaced by the
  normalized `HarnessEvent.RecoveryExhausted` fact. External orchestration no
  longer interprets a Codex error-code literal.
- Non-terminal native error diagnostics remain activity/warning facts while
  `turn/completed` supplies the one terminal outcome. Failed, cancelled,
  malformed, unavailable, timeout, and process-exit paths retain the adapter's
  atomic terminal guard.
- A focused E2E proves an absolute nested Windows workspace containing spaces
  reaches both Codex thread and turn behavior unchanged, together with the
  exact `alpha:latest` model and `ollama` provider.
- Official App Server documentation was rechecked on 2026-08-20. The installed
  launchable standalone CLI is `codex-cli 0.148.0-alpha.15`; its model-free
  `app-server generate-json-schema` output contains every method and event used
  by the adapter. Ambient `PATH` still resolves an unlaunchable MSIX binary,
  while managed-install discovery selects the current standalone executable.
- Focused registry/Codex/Native/UI tests passed 22/22 from both isolated and
  normal Release outputs. The complete deterministic Playwright/API suite
  passed 240/240 with zero skips in 6m22s.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` passed.
  The normal Release build passed with zero warnings and zero errors after the
  previously running application was stopped. `git diff --check` passed.
- No real Ollama generation, GPU inference, cloud request, model download,
  Codex update, OpenCode implementation, or OpenCode protocol work was run.
