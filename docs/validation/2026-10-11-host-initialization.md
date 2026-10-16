# Host initialization and harness residency validation — 2026-10-11

Measurements use an isolated copy of the real workspace and enabled conversation history; no user-owned workspace or history was modified. Provider: local Ollama, qwen3.8:27b-q4_K_M, Vulkan all GPUs, context 131072, F16 KV, configured draft tokens 3, batch 512, keep_alive 600. The workspace has 35 files including Git metadata (~85 KiB); legacy conversation JSON expands to ~179 MB.

## Measured bottlenecks

Old history contained tens of thousands of one-character reasoning events, each repeating full state. History preparation took ~25–27 seconds; the initial checkpoint added ~13–14 seconds. Snapshot capture itself took 4–39 ms for six regular files (~46 KiB). Snapshot capture was not the cause of the observed 120-second delay. No eager workspace snapshot is now created. Each declared mutation target is captured and persisted immediately before its first mutation.

Native startup used an unspecified managed context whereas Claude used an explicit 131072, causing a managed-server replacement. The compatibility endpoint also omitted the configured MTP, causing a runner reload even when the server survived. Both execution paths now use the resolved Host context and the native Ollama bridge preserves configured runtime options.

## Real paired measurements

Same instruction: respond only OK, inspect/change no files. Times below are seconds; total measures HTTP/SSE completion at the client, not the terminal event creation timestamp. Host-to-harness includes all initialization before harness dispatch.

| Step | Total before → after | Host-to-harness before → after | Dispatch-to-first-token before → after | Load before → after | Prefill before → after |
|---|---:|---:|---:|---:|---:|
| 1. native | 159.890 → 90.569 | 40.883 → 14.569 | 64.881 → 62.347 | 52.165 → 50.124 | 12.660 → 12.167 |
| 2. claude-code | 133.471 → 36.436 | 39.092 → 10.555 | 59.286 → 14.787 | unavailable → 0.006 | unavailable → 14.272 |
| 3. native | 150.284 → 22.768 | 38.568 → 8.377 | 65.722 → 0.902 | 53.706 → 0.005 | 11.973 → 0.305 |

After correction, server PID 64308 and runner PID 126304 remained identical across all three turns. Runner arguments confirmed context 131072, draft-mtp=3, F16 K/V, batch/ubatch 512. Return to Native processed only 33 fresh prompt tokens (~305 ms), with the preceding prefix cached. Cold load still cost ~50 seconds; the change removes unnecessary subsequent loads.

This is an operational comparison, not a controlled throughput benchmark: sampled output counts differ (35→41, 34→64, 36→38). Before Claude load/prefill were not independently available through its compatibility metrics. After-native translation exposes exact provider durations. The original historical baseline includes legacy persistence; the updated run includes lossless timeline normalization.


Additional phase separation (seconds). Harness initialization here is the client-derived interval from Host harness-start to the first provider dispatch; it includes transport setup and is not model loading. Host file preparation includes persistence, attachment and snapshot-coverage work; processing bytes are cumulative, not unique disk size.

| Step | File preparation before → after | Harness-to-provider before → after | First model output received before → after |
|---|---:|---:|---:|
| 1 | 26.285 → 7.698 | 0.253 → 0.229 | 106.069 → 77.193 |
| 2 | 23.345 → 4.942 | 0.671 → 0.581 | 99.826 → 26.457 |
| 3 | 23.749 → 4.912 | 0.092 → 0.038 | 104.438 → 9.360 |

For Native the structured answer arrived with the terminal event: 147.851→87.396 seconds on step 1 and 138.812→19.646 seconds on step 3. Claude visible text arrived at 99.912→26.891 seconds on step 2. Terminal receive and stream close remain separate from provider generation.

## Thinking=None follow-up

Explicit None now overrides Claude SDK defaults at the Host native transport and sends think:false on every inference. Installed-Claude E2E cases passed with Auto runtime options, draft=0 and draft=3, including a tool-result continuation. A real browser run on the updated isolated Host returned no Thinking blocks. That run produced a literal </think> and an unsolicited cmd /c echo 1 despite a no-tools instruction: generation control succeeded, instruction quality did not. No files were changed. Request 9a24aa82cd9044cd89a05de44ae23f61, Host-to-harness 4.509 seconds, footer execution ~58 seconds; this is a separate cold-start run, not included in the paired latency comparison. Screenshot: ../../.artifacts/harness-snapshot-audit-20261011/claude-none-real.jpg.

## Rollback and Qwen

Real Host write and AR Rollback restored a 40-byte ignored file exactly, including BOM and CRLF. Installed Claude Edit and installed Codex apply_patch E2E separately verified durable original-byte capture before native approval and byte-identical AR Rollback. Provider-boundary scripting is deterministic; these native tests run the actual installed CLIs, API, MCP/approval path, filesystem and browser.

Qwen no longer posts reasoning_effort when its session/model does not advertise the requested option. Real Qwen Code 0.25.0 completed in the browser with None, without the previous HTTP500; trace 0HNP7A708L4QB:0000001F. Footer execution ~14 seconds, 25 tokens, 61.30 tok/s, TTFT 8.39 seconds. These footer measures do not represent total client latency.

Unknown file targets inside opaque process/native-shell actions retain the existing effect-observer/Git recovery fallback; this cannot promise durable pre-write capture of paths that were never declared. Declared native filesystem targets persist originals before permission is granted.

## Validation evidence

Release builds: zero warnings/errors (final isolated build history-validation-v11). Formatting applied with dotnet format whitespace to the changed Host/runtime/test components; git diff --check passed. Consolidated installed-final.trx: 13/13 passed across Codex, Claude, OpenCode and Qwen (MTP 0/3, None including Auto, read-tool continuations and native rollback). Qwen reasoning-option advertised/unavailable coverage: qwen-reasoning-final.trx 2/2 passed. Regression run regressions-v8.trx: 53/53 passed (history/snapshot bytes/restart normalization, file-read policy, runtime performance, Native/Claude/Native process reuse). Installed-Claude None cases: 3/3; installed Claude native Edit rollback: passed. Codex corrected Responses contract: codex-v9.trx 3/3 passed (draft 0/3 plus native patch rollback). Installed Qwen discovery/read-tool cases: qwen-v10c.trx 2/2 passed (draft 0/3).

Private evidence is under .artifacts/harness-snapshot-audit-20261011: real-comparison.json, historical-timings.json, before/after-final SSE receive logs, managed leases/process snapshots and Host stdout. This directory is ignored and must not be published with raw conversation data.

## Implementation and commands

Changed Host/runtime components: ChatStreamService, HarnessInferenceObserver, OllamaHarnessWireAdapter (new), CodexApplyPatchWireAdapter, ClaudeCodeHarness, QwenCodeHarness, LocalActionService, HarnessWorkspaceObserver, ConversationHistoryFiles, PersistentSessionService/Store, ChatController checkpoint markers, inference-metrics contracts and PricingCatalog. Existing unrelated working-tree edits were retained. Browser/API coverage is in HistoryFiles, InstalledHarnessUpgrade, OllamaStartupRecovery, RuntimePerformance and BenchmarkAndHarness tests; provider/CLI boundary fixtures were updated.

Commands executed: `dotnet format whitespace AgenticRouter.slnx --no-restore --include <changed components>`; `dotnet build tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj -c Release --no-restore --disable-build-servers -m:1 -p:OutputPath=<isolated v11 output> -v minimal`; `dotnet test <isolated E2E assembly> --filter <relevant browser/API cases> --logger trx`; `git diff --check`. Individual suite results and the exact reusable installed-CLI command script are in the ignored evidence directory.

Cleanup: temporary browser tab and validation Host were closed. Validation ports 63001, 63002 and 35535 had no listeners afterward; Host 129204, managed Ollama 129528 and runner 100164 exited. The main installation was not restarted or overwritten. Applying this build to the user's normal launcher remains a separate step.
