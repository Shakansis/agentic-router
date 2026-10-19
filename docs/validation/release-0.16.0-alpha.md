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

| Gate | Result | Local evidence under `.artifacts/release-0.16.0-alpha/` |
| --- | --- | --- |
| Complete deterministic suite | **884/884**, zero failures/skips; 26.2835 minutes | `TestResults/release016-full-final.trx`, `full-final.log` |
| Installed CLI integration | **32/32**, zero failures/skips; 6.7876 minutes | `TestResults/release016-installed-final-source.trx` |
| Extracted Windows ZIP browser/API | **8/8**, zero failures/skips | `TestResults/release016-packaged-browser-fixed.trx` |
| Test-only restart guard, API build | **3/3**, zero failures/skips | `TestResults/release016-restart-guard.trx` |
| Release builds | Zero warnings/errors | `build-audit-fixes.log`, `build-fixture-guard.log` |
| Formatting | Whole solution passed, then changed test helper passed | `format-verified.log`, `format-fixture-verified.log` |
| Syntax and diff | 18 JavaScript files, 8 PowerShell files, `git diff --check` passed | Node syntax check and PowerShell Parser |

The first complete run exposed 37 failures (847/884 passed). The corrections
above were first verified in focused reruns, then the entire 884-case suite
passed against the frozen application source. The later test-only process guard
was exercised against both the API executable and the extracted portable binary.
The extra package/guard checks overlap suite cases; they are not added to the
916 distinct deterministic/installed-CLI cases. Focused reruns do not substitute
for the complete suite.

## Portable packages

Both targets were built from source commit
`9ba9241cdb517b32d77f5a35059907bfc629a8d2`; generated assembly metadata includes
that revision. Subsequent edits affect documentation and the test-only restart
identity guard; application files remain byte-for-byte unchanged.

```powershell
./scripts/Publish-PortableRelease.ps1 -VersionLabel 0.16.0_alpha -RuntimeIdentifier win-x64 -OutputDirectory .artifacts/release-0.16.0-alpha/packages
./scripts/Publish-PortableRelease.ps1 -VersionLabel 0.16.0_alpha -RuntimeIdentifier linux-x64 -OutputDirectory .artifacts/release-0.16.0-alpha/packages
```

| Archive | Bytes | SHA-256 |
| --- | ---: | --- |
| `AgenticRouter-0.16.0_alpha-win-x64.zip` | 122979839 | `03bf79aa75b5f766237ac19a82d4a1c90c7002a126b1e53e939add11a8f74601` |
| `AgenticRouter-0.16.0_alpha-linux-x64.tar.gz` | 141836173 | `92a19512ae7028a85d35aba249ee2dbdca341ea89d7a8601bd094cc836f6639f` |

- `Test-PublishArtifact.ps1` passed for both targets: no diagnostics, test/fake
  binaries, credentials or application data. Each checksum was verified again
  before extraction.
- Windows: extracted into a fresh directory and started from another working
  directory, with an isolated data path and loopback port. File version is
  `0.16.0.0`; product version is `0.16.0_alpha+9ba9241cdb517b32d77f5a35059907bfc629a8d2`.
  Root HTML, settings API and conversation script returned HTTP 200.
- Linux: extracted and executed in Ubuntu/WSL from another working directory,
  with temporary data and port. Root HTML and settings API returned HTTP 200;
  settings parsed as JSON. Executable and launcher modes are 755 and the bundled
  Linux Playwright node is executable.
- All 36 source `wwwroot` files match the packaged copies in both targets.
  Both smoke processes were stopped. No inference was requested by package checks.
- Eight additional browser/API cases passed against the executable extracted
  from the Windows ZIP: explicit Storytelling Apply, conflict/encoding-preserving
  workspace saves, model install/organize/remove through the fake CLI, Chat rewind,
  Execute replacement and three desktop/mobile terminal status cases.
  The first package probe exposed the test helper's hardcoded API process name;
  its restart guard now verifies the exact executable path it started. The same
  package then passed all eight cases; no application or package rebuild was
  needed. Provider inference and model installation remain simulated in this probe.
  Three additional API-build cases passed with the new guard: model install/remove,
  actionable CLI failures and successful-use persistence across a Host restart.

## Validation boundary

The four real-model classes contain 13 opt-in cases, including one replay case
that depends on their recorded histories. They were discovered but not executed
in this release run: fresh local inference authorization and an isolated real-test
API/history are required. Earlier feature-specific real-model reports remain
historical evidence and are not counted as acceptance of this release binary.

Linux startup was verified in WSL, not a separate Linux desktop/GPU environment.
Package smoke checks exercise startup, HTTP and static assets; full browser/API
coverage uses the isolated Windows Release build. No coverage percentage is
claimed from these test counts.
