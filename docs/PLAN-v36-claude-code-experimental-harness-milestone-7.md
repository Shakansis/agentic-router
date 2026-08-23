# PLAN v36: Claude Code Experimental Harness — Milestone 7

## Goal

Integrate installed Claude Code `2.1.234` as `ClaudeCodeHarnessAdapter :
IAgentHarness`, preserve the exact Agentic Router Ollama model, trusted Windows
workspace, Host capability/approval authority, canonical conversation, and
exactly one terminal result, then enable the existing Basic CRUD v1 path without
Claude-specific benchmark behavior.

## Fixed boundaries

- Milestones 0–6 and the current `IHarnessRegistry` -> `IAgentHarness` boundary
  remain authoritative. Native, Codex, OpenCode, and Qwen Code keep their
  existing semantics.
- Use the installed native Claude Code executable as a subprocess. Do not add a
  Python or Node service and do not reimplement the Claude agent loop.
- Support Ollama Local only. Set the Anthropic-compatible endpoint and exact
  selected model explicitly, do not configure a fallback model, and fail on
  reported model, provider, or workspace substitution.
- Preserve the exact canonical Windows workspace path with `ProcessStartInfo`
  argument entries and `WorkingDirectory`; never construct a shell command.
- Do not claim or emulate native Windows Claude sandboxing. Official Claude
  sandbox enforcement supports macOS, Linux, and WSL2, not native Windows.
- Keep browser, WebSearch/WebFetch, Bash, subagents, skills, plugins, hooks,
  user/project settings, arbitrary MCP, and cloud-only tools out of the
  controlled surface. The only MCP server is Agentic Router's existing
  loopback Host capability bridge, configured explicitly and exclusively.
- Keep Host observation, trusted-workspace validation, approvals, effect proof,
  recovery, benchmark validation, persistence, scoring, and terminal truth
  authoritative.
- Do not run real Ollama/GPU/cloud inference as part of automated validation.
  The requested `qwen3.8:27b-gpu0` matrix remains a manual acceptance gate.

## Research baseline

- Installed version: `2.1.234 (Claude Code)` at
  `C:\Users\Rodrigo\.local\bin\claude.exe`.
- Official C# guidance is to invoke the CLI subprocess because the Agent SDK
  libraries are Python and TypeScript only.
- The installed and documented programmatic surface is `-p` with
  `--input-format stream-json`, `--output-format stream-json`, `--verbose`, and
  `--include-partial-messages`. The final line is a structured `result` frame.
- `--permission-prompt-tool stdio` uses the Agent SDK control protocol:
  `control_request/request.subtype=can_use_tool` and a correlated
  `control_response`. This maps native Claude permission prompts to the existing
  `IAgentHarnessTransport.ResolveApprovalAsync` flow without a second approval
  system.
- `--resume=<session-id>` provides cross-process continuity. Preserve the first
  canonical native session id because reported ids on resumed invocations have
  had known drift in prior releases.
- `--bare`, `--disable-slash-commands`, `--no-chrome`, `--strict-mcp-config`,
  `--tools`, and explicit MCP/permission configuration provide a controlled
  tool and customization surface. Bare mode requires explicit local placeholder
  auth, which matches Ollama's documented `ANTHROPIC_AUTH_TOKEN=ollama`, empty
  `ANTHROPIC_API_KEY`, and `ANTHROPIC_BASE_URL` configuration.

## Ordered implementation

1. Add `HarnessIds.ClaudeCode`, Claude capability projection, options,
   dependency registration, discovery, and the experimental selector entry.
2. Implement one owned Claude subprocess per turn with structured NDJSON
   parsing, exact environment/model/cwd validation, bounded stderr diagnostics,
   cancellation by process-tree termination, timeout propagation, native
   payload preservation, session continuity, and an exactly-once terminal
   guard.
3. Normalize `system/init`, partial `stream_event`, assistant text/thinking,
   `tool_use`, `tool_result`, retry/status, malformed/unknown frames,
   permission requests, and final `result` frames into existing `HarnessEvent`
   semantics.
4. Configure only intentional Claude native tools plus the existing Host MCP
   bridge. Map ask/auto decisions through existing Host approval handling and
   route Host tool calls/results through `HarnessMcpHostBridge`.
5. Generalize only legacy Codex-named workspace-observer messages/codes needed
   for truthful Claude reporting; do not change the observer's policy.
6. Let registry-driven Execute and Basic CRUD discover Claude automatically;
   update only hard-coded regression expectations/counts and no benchmark
   prompt, fixture, timeout, validator, scoring, or persistence behavior.
7. Add a deterministic fake Claude CLI boundary and focused E2E coverage for
   discovery/version, exact Ollama/model/cwd propagation, spaces in Windows
   paths, structured streaming/thinking/tool events, Host capabilities,
   approval, cancellation/timeout, malformed/unknown frames, continuity,
   exactly-one terminal behavior, benchmark participation/persistence/scoring,
   and Native/Codex/OpenCode/Qwen regression.
8. Run focused deterministic E2E tests, then the applicable full deterministic
   suite, Release build, format verification, `git diff --check`, and intended
   diff review. Report unavailable gates exactly.

## Manual acceptance gate

After deterministic validation, hand off the exact UI steps for
`qwen3.8:27b-gpu0`: read, create, edit, structured delete, `run_process`, ask
approval, workspace rejection with recovery, cancellation, continuity, clean
terminal completion, then Basic CRUD v1 for Codex/OpenCode/Qwen Code/Claude
Code. Do not execute the real model without separate explicit authorization.

## Deterministic validation evidence

- Isolated and default-output Release builds: passed with zero warnings and zero
  errors. The final default-output command was
  `dotnet build AgenticRouter.slnx -c Release --no-restore`.
- Harness and benchmark browser/API regressions: 73 passed, 0 failed, including
  Native, Codex, OpenCode, Qwen Code, Claude Code, Basic CRUD v1 persistence and
  scoring, approval, trusted-workspace rejection/recovery, cancellation,
  timeout, continuity, malformed input, and exactly-one terminal behavior.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed.
- The manual acceptance matrix remains open. Real inference was subsequently
  authorized and executed as recorded below.

## Real validation evidence after initial manual failure

- The first real Execute probe reproduced `claude-code-tool-surface`: Claude
  Code 2.1.234 exposed the safe subset `Read`, `Edit`, and the configured Host
  MCP tools while omitting optional native `Glob`, `Grep`, and `Write`.
- The adapter now rejects unexpected tools but accepts an omitted optional
  native capability, emits a visible warning, and leaves the declared Host
  bridge available. This preserves the controlled surface without claiming a
  capability the installed runtime did not expose.
- A corrected real, read-only Execute request with `qwen3.8:27b-gpu0` streamed
  thinking and response events and reached exactly one completed terminal state
  without modifying files.
- Real Basic CRUD v1 run `623bd0f854aa4d3f932b1a8173bf567c`, Claude Code
  only: 4/4 passed, score 99.31, terminality 100%, duration 55.525 seconds.
  Create, read, update, and structured delete all passed deterministic Host
  validation; the delete recovered from one surfaced error.
- Real validation used an isolated API/data runtime on loopback port 5394 and
  was shut down afterward. The user's running API process was not interrupted.
- Follow-up trace `0HNO0OA7D882G:000004CF` showed the installed CLI emitting a
  `system/thinking_tokens` frame for almost every thinking delta. Publishing
  both frames flooded the activity timeline and exhausted the 500-event
  diagnostic retention before the five-minute terminal timeout was recorded.
- The timed-out turn had already modified `hangman.html`, `hangman.js`, and
  `words.js`; the Host persisted a failed review with three changed files and
  `implemented-validation-not-run`. `turn.timed-out` now becomes a typed
  harness failure instead of falling through to a generic application error.
- Claude deltas are now losslessly coalesced into bounded chunks; known
  transport-only frames are not surfaced as UI activity, while genuinely
  unknown top-level payloads remain preserved. A 2,000-frame deterministic
  storm stays below 60 public events.
- The installed runtime omitted native `Glob`, `Grep`, and `Write`. Claude's
  truthful native capability projection is now `read_file`, `replace_text`, and
  `apply_patch`; `list_files`, `search_text`, `create_file`, and `write_file`
  remain available through the existing Host bridge instead of being silently
  absent.
- A second real read-only request completed through Host `list_files` with 44
  total public events, six reasoning chunks, two response chunks, three bounded
  native diagnostics, no tool errors, no file changes, and exactly one terminal
  result. The equivalent pre-fix UI run showed 4,933 events.
- Trace `0HNO0P1BEUQ4G:00000015` isolated the next failure to
  `git_unstage_files` in an unborn repository. `git restore --staged` cannot
  resolve `HEAD` before the first commit, and the uncaught
  `GitDeliveryException` ended the MCP turn before the Host result returned.
- Unstage now removes only the exact approved index entries with
  `git rm --cached --force` when `HEAD` does not exist and keeps
  `git restore --staged` for established repositories. Git delivery failures
  are translated to typed `LocalActionException` results with sanitized Git
  code/diagnostic evidence, allowing the active harness to recover.
- External-harness observation now verifies protected Git state immediately
  before a Host-owned Git mutation and rebases the protected snapshot after the
  Host attempt. Native harness Git mutation remains rejected, while approved
  Host Git operations no longer produce a false `codex-git-boundary-rejected`;
  error identity uses the selected harness id.
- Real API request `e7491e235aa84aa6ae7c9cd3c93aabcf` used Claude Code
  `2.1.234`, exact Ollama model `qwen3.8:27b-gpu0`, and the active `YpIaiYo`
  workspace. Claude called Host `git_unstage_files` for exactly `index.html`
  and `script.js`, then Host `git_status`. It completed in 35.186 seconds with
  63 public events, zero tool failures, zero file-content changes, no commit,
  empty `StagedPaths`, and exactly one `response.completed` terminal event.
  Independent `git status`, cached diff, and working-tree diff checks confirmed
  that only the six pre-existing game files remain untracked.
- Final post-fix validation: 288/288 E2E passed; Release build passed with zero
  warnings/errors; format verification and `git diff --check` passed. The
  Release API used for the real validation remains available on loopback port
  5294 for user testing.

When ready, report exactly:

`MILESTONE 7 CLAUDE CODE EXPERIMENTAL HARNESS READY FOR MANUAL TEST`

Then stop. Do not begin Milestone 8 or integrate Goose, DSH, another benchmark
suite, routing, community data, or unrelated refactoring without explicit
approval.
