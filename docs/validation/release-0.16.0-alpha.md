# Agentic Router 0.16.0 alpha release validation

Date: 2026-10-19. This record separates deterministic browser/API coverage,
installed-CLI compatibility, package smoke tests and real inference.

## Scope and isolation

The candidate includes the accumulated source changes since 0.15: fixed
specialists and the Storytelling Editor, workspace file references, model library
operations, Native multi-tool batches and optional parallel reads, Direct model
residency, and active-role context capacity.

Validation builds use `.artifacts/release-0.16.0-alpha/`, separate API executables,
fake external providers and temporary workspaces/data. The user's running AR
and Ollama processes are preserved. No model is downloaded or removed by these
tests. Local test scripts and raw TRX/logs remain in the ignored artifact directory.

## Product corrections

An exclusive Direct turn previously acquired the model lease before replacing
the same browser's old Execute session. If the old session was waiting for Ask
approval, both requests waited indefinitely. Request preflight now finishes
before cancelling that browser's previous Execute; only then does the replacement
wait for model admission. The lease remains held until terminal session cleanup.
The browser/API regression verifies replacement, cancellation of the old approval
and observed file creation. A rejected model selection preserves the old approval
and produces no file. Existing cross-browser waiting, cancelled waiters and all
five harness model-switch paths remain covered.

Adding live inference badges made the counters cease to be the last child in the
execution header. The mobile CSS therefore left the counters in an auto-sized
column, collapsed the model column and made the header too tall to remain sticky.
`conversation-stream.js` now assigns an explicit counters class and `styles.css`
targets that direct child. The original viewport-position assertions pass for
completed/cancelled desktop turns and a failed turn at 390px; screenshots confirm
that model identity and counters remain readable. No timeout or position bound
was relaxed.

## Corrections from the first full run

- Stamp all browser assets consistently for 0.16 and verify cache revalidation.
- Filter model lifecycle requests out of inference assertions. Warm the selected
  model before checking that a generic HTTP failure does not cause eviction.
- Exercise the approved immediate Chat rewind, prompt restoration, history
  truncation, draft preservation on a rejected rewind and resubmission. The old
  cancellable inline-edit state no longer describes this workflow.
- Include `read_files` in the exact initial tool inventory and closed canonical
  registry. Capability-family suggestions remain unambiguous; `file-read` now
  contains two tools and must not select one silently.
- Give compaction fixtures actual tool calls and correlated IDs, preserving the
  most recent complete tool/result group and removing obsolete output.
- Assert the active role context and typed preflight rejection at the provider
  boundary. Do not require a later planner error after earlier context validation.
- Locate trace metadata in the terminal footer and require unavailable timing to
  be labeled unavailable rather than fabricated or silently omitted.
- Make the OpenCode fixture finish its mutation/idempotence probes before the
  deliberately exhausted read budget. Its replacement context concludes from
  prior effects instead of replaying the complete script and overwriting evidence.
- Exercise the existing no-Git rollback-coverage decision in manual supervision
  browser tests, and give installed-CLI workspaces a tracked baseline before
  requesting their initial commit.
- Select the visible inference icon explicitly; the indicator now also contains
  a separate Host-file icon. Preserve the timing/progress assertions.
- Check cloud image consent and metadata-only session JSON without expecting
  a missing-attachment marker: enabled history now retains exact upload bytes
  separately under the approved history-file contract.
- Keep the direct-delete fixture independent of preliminary text reads so binary
  deletion, edited approval and exact-byte undo remain exercised.
- Give the large-history fixture real event boundaries between reasoning groups.
  This exercises event pagination despite reasoning coalescing, exact text replay,
  bounded DOM rendering and exported boundary evidence.
- Assert the configured context window and decreasing output headroom as tool
  evidence accumulates, while retaining the single incremental-write recovery.
- Fix import ordering reported by the formatter. Portable publishing disables
  persistent build servers and shared compilation.

## Commands and result locations

Release solution build:

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/release-0.16.0-alpha/verified -p:UseSharedCompilation=false --disable-build-servers

dotnet format AgenticRouter.slnx --verify-no-changes --no-restore
```

The compiled test assembly runs with `playwright.runsettings`, overriding
`AGENTIC_ROUTER_REPOSITORY_ROOT`, `AGENTIC_ROUTER_E2E_API_PATH` and the five
`AGENTIC_ROUTER_E2E_FAKE_*_PATH` fixture executables to the isolated build.
The complete deterministic filter excludes only the four explicit real-model
classes and `InstalledHarnessUpgradeEndToEndTests`. Installed-CLI compatibility
uses that class separately with `AGENTIC_ROUTER_INSTALLED_HARNESS_E2E=1` and
explicit local CLI paths; its model provider remains simulated.

Read-only version discovery for this run: Codex `0.163.0-alpha.10.2`, Claude Code
`2.1.295`, OpenCode `1.18.35`, Qwen Code `0.25.0`. No CLI upgrade was performed.

Results are pending the final complete run and package validation. Focused
reruns are diagnostic evidence and are not substituted for the full release gate.
