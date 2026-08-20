# PLAN v18: OpenCode experimental harness and pinned Qwen GPU tag

## Goal

Add OpenCode `1.18.18` as the first new experimental adapter behind the
approved generic harness contract, while preserving Native as the default,
Codex behavior, exact trusted-workspace paths, Host-owned effect proof, and one
normalized terminal event. Separately create and adopt the explicit local
`qwen3.8:27b-gpu0` tag so calls that cannot carry request-level `main_gpu`
still bind the Qwen runner to the RTX 4090.

## Approved start

The user manually accepted the Codex generic-harness migration on 2026-08-18
and explicitly authorized Phase 2 OpenCode implementation, the new Ollama tag,
all active Agentic Router configuration updates, deterministic test updates,
runner replacement, and one real Qwen placement smoke.

## Research baseline

- Installed OpenCode is `1.18.18`. `serve --help` supports loopback binding,
  dynamic port selection, pure mode, and no mDNS by default.
- The live installed `/doc` returns OpenAPI 3.1 JSON directly. It confirms
  `/global/health`, `/event`, session create/status/diff, `prompt_async`, abort,
  permission/question replies, and native session/message/tool/reasoning events.
- Installed behavior differs from the current web documentation: explicit
  `--port 0` selected port `4096`, so the adapter must allocate and bind a free
  loopback port itself instead of parsing or trusting port-zero behavior.
- OpenCode attempted to use the default state root even with config/data/cache
  roots isolated. The owned runtime must also set `XDG_STATE_HOME` and must use
  an AR-owned configuration rather than the user's global OpenCode state.
- The current Qwen runner correctly uses q8 KV cache and 22.1 GB VRAM, but an
  OpenAI-compatible Codex request has no `main_gpu`; Ollama therefore fits 56
  layers on the 4090 and 9 on the 2070. A model-level `main_gpu = 0` is the
  smallest durable request-independent correction.

## Boundaries

- Bind the owned OpenCode server to `127.0.0.1` only with an AR-generated Basic
  Auth password, pure mode, isolated XDG roots, sharing and updates disabled,
  and no inherited user plugins, instructions, credentials, or cloud fallback.
- Speak HTTP, SSE, and JSON directly from C#; add no Node/TypeScript dependency
  to Agentic Router. The installed OpenCode executable remains an optional
  external harness dependency.
- Pass the exact AR-selected Ollama provider, model, and Windows workspace path.
- Do not expose OpenCode shell, file, Git, MCP, worktree, sharing, or other
  native mutation authority directly to the browser. Host observation and
  postcondition proof remain authoritative.
- Preserve native payloads for diagnostics. Do not claim stale protection or
  subagents unless the implemented integration actually exposes them safely.
- Do not alter historical evaluation reports that name the previously measured
  `qwen3.8:27b` artifact.

## Implementation

1. Create `qwen3.8:27b-gpu0` from the current Qwen tag with model-level
   `main_gpu = 0`; verify the manifest, update active AR settings/configuration
   and deterministic fixtures, unload the split runner, and run one authorized
   real placement smoke.
2. Add an OpenCode adapter with availability/version discovery, owned-process
   identity, free-port allocation, loopback/authenticated startup, isolated
   configuration, health verification, bounded lifecycle, and owned cleanup.
3. Create or reuse one OpenCode session per AR conversation, preserving the
   exact working directory and explicit local provider/model identity.
4. Consume SSE and normalize assistant text, reasoning, tool lifecycle,
   permissions, warnings/errors, session diff, cancellation, crash, and idle
   terminality while retaining cloned native payloads.
5. Extend generic approval routing only where OpenCode exposes a real pending
   permission. Reject unsupported native permission breadth rather than
   silently granting it, and keep Host effect observation mandatory.
6. Register OpenCode and let the existing discovery endpoint and generic UI
   selector render its experimental availability without a new UI branch.
7. Add a deterministic fake OpenCode server and browser/API E2E coverage for
   discovery/version, startup/auth/loopback, exact workspace and model, session
   reuse, SSE mapping, tools, permission reply, diff, abort, crash, malformed
   events, cleanup, native payload preservation, and exactly one terminal.
8. Document installed-version discrepancies, capability mapping, configuration,
   security boundaries, and manual validation steps.

## Validation

1. Run focused OpenCode and generic-harness browser/API E2E tests using only the
   fake Ollama and fake OpenCode external boundaries.
2. Run `dotnet format AgenticRouter.slnx --verify-no-changes`.
3. Run a zero-warning Release build and the full Playwright E2E suite.
4. Run `git diff --check` and inspect the complete intended diff while
   preserving the pre-existing dirty worktree.
5. Run only the separately authorized real Qwen GPU placement smoke. Do not run
   a real OpenCode task without another explicit real-harness validation grant.

## Manual gate

After automated validation, report exactly:

`OPENCODE EXPERIMENTAL HARNESS READY FOR MANUAL TEST`

Then stop. Do not implement Qwen Code, Claude Code, Goose, DeepSeek Harness, or
another harness until the user manually accepts OpenCode.

## Completion evidence

- `qwen3.8:27b-gpu0` exists with `main_gpu 0`; the authorized 131,072-context
  smoke returned `OK` with the runner present only on the RTX 4090.
- User-scope Ollama runtime settings are `OLLAMA_FLASH_ATTENTION=1` and
  `OLLAMA_KV_CACHE_TYPE=q8_0`.
- Focused OpenCode E2E matrix: 6/6 passed.
- `dotnet format AgenticRouter.slnx --verify-no-changes`: passed.
- Release build: passed with 0 warnings and 0 errors.
- Full Playwright E2E suite: 214/214 passed in 5m32s.
- `git diff --check`: passed; only the pre-existing CRLF/LF warning for
  `.gitignore` was reported.
- No real OpenCode/model task was run; that remains the manual acceptance gate.
