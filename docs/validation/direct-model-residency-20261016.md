# Direct model switch residency — 2026-10-16

## Observed incident

The latest persisted session selected `direct` and Native. Local timestamps:

| Turn | Model | Trace | Timing |
| --- | --- | --- | --- |
| Previous | `gemma4:26b-a4b-it-qat` | `0HNPB6VOJLSI7:000000B1` | 00:46:37–00:47:43 |
| Next | `gemma4-rpg:26b-mtp` | `0HNPB6VOJLSI7:000001EB` | 00:48:28–00:50:11 |

The user's Host PID 81968 owned two independent Ollama servers: PID 25276,
port 16535, Vulkan/all GPUs; and PID 10056, port 16531, ROCm/7900 XTX. Initial
read-only process discovery found both model runners, PIDs 53300 and 39756.
The first runner subsequently exited naturally during investigation. No unload
event was present between the two turns.

The second runner had a 131072-token context, F16 KV cache and draft-MTP=3.
Its final persisted provider metrics were 463 output tokens, 53.285 seconds of
generation (**8.689 tokens/s**), 29.824 seconds of prefill and 17.937 seconds of
model loading. These establish the slowdown and overlapping model residency;
the retained evidence does not quantify historical CPU/RAM offload. The
ROCm `/api/ps` size fields were inconsistent with the model size and were not
used to estimate RAM placement.

Sources: the local incident journal `incidents-20261016-001.jsonl`, persisted
session `630c0f0b512f47d2ba83f17be2dfee2a`, managed-server leases, process
parentage, runner arguments and read-only `/api/ps` observations.

## Cause and correction

Managed Ollama servers are keyed by backend/device and KV-cache configuration.
Changing the model's affinity selected another endpoint without unloading the
previous endpoint's resident model. A per-server model limit cannot enforce a
single-model rule across those processes.

`DirectModelResidency` now applies the requested Direct policy in the common
Host path, before Native or an external harness starts. It unloads previous
AR-owned models at their observed endpoints, verifies absence through `/api/ps`
and then permits the new load. The exact-endpoint provider operation does not
re-resolve the old model's current affinity. Unload failures or unverified
effects prevent the new load and remain typed, recoverable errors.

`ModelRequestTracker` now supports cancellable exclusive admission for local
Direct turns. A switch waits for active turns instead of unloading their model;
completion, failure and cancellation release admission. Supervised turns retain
shared admission with each other. Keeping the selected model warm is preserved.
Managed server processes remain reusable. On external endpoints, only model
names previously selected by Chat/Execute in this Host process are eligible for
cleanup; unrelated external models are preserved.

Production files changed for this correction:

- `Chat/ChatStreamService.cs`: common admission, residency preparation and activity.
- `Runtime/DirectModelResidency.cs`: cross-endpoint cleanup and effect verification.
- `Runtime/ModelRequestTracker.cs`: shared/exclusive request admission.
- `Providers/Ollama/{IOllamaClient,OllamaClient}.cs` and
  `Providers/ProviderDispatchClient.cs`: exact-endpoint unload operation.
- `Program.cs`: register shared residency state.

`docs/model-gpu-residency.md` documents the Direct exception to supervised
coexistence. Existing unrelated multi-tool changes remain in the working tree.

## Validation

Validation uses an isolated Release output under `.artifacts/direct-residency`,
isolated Hosts and fake providers at the external boundary. Vulkan and ROCm
endpoints have separate fake provider residency, allowing the test to detect
whether target inference starts before the previous model disappears.

- Release build: passed, **0 warnings, 0 errors**.
- Scoped `dotnet format ... whitespace --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed. Existing frontend CRLF notices do not indicate
  whitespace errors and those files were not edited for this correction.
- Focused Playwright browser/API E2E: **20/20 passed** in 2.94 minutes.

| Coverage | Cases |
| --- | ---: |
| Separate Vulkan/ROCm residency; unload before target inference; Native, Codex, Claude Code, Qwen Code and OpenCode | 5 |
| Unload HTTP failure; successful response without actual unloading; recovery | 2 |
| Active-turn protection; cancelling a queued switch and subsequently switching | 2 |
| External endpoint preserves unrelated models and keeps the selected model warm | 1 |
| Existing per-model KV-cache endpoint selection/reuse across five harnesses | 5 |
| Native → Claude Code → Native managed process/context reuse | 1 |
| Existing explicit/default supervisor and worker GPU affinity | 2 |
| Existing parallel Native reads and concurrency limit | 1 |
| Existing Native batch cancellation prevents pending mutations | 1 |

Build command:

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/direct-residency -p:UseSharedCompilation=false -nodeReuse:false -m:1 --no-restore
```

The initial isolated restore was performed with the same command without
`--no-restore`. Build/format/E2E ran outside the Windows sandbox after its NuGet
network and listener/build-host restrictions were identified. Validation did not
disable NuGet auditing or suppress warnings. Test executable overrides pointed
every fixture and the API to this isolated output.

E2E command used `dotnet test` on the isolated `AgenticRouter.EndToEndTests.dll`,
`--settings playwright.runsettings`, the focused test-name filter above, and
`--logger 'trx;LogFileName=direct-residency-final.trx'`. The retained result is
`.artifacts/direct-residency/test-results/direct-residency-final.trx`.

Final process/port verification found no remaining test Hosts, fake providers,
Playwright browser processes or build hosts. The original processes remained:

| Process | PID | Parent | Ports |
| --- | ---: | ---: | --- |
| User AR, started 00:45:32 | 81968 | 77860 | 5294, 7155 |
| User Ollama app | 34188 | 52336 | — |
| User Ollama service | 19340 | 34188 | 11434 |
| Existing AR-owned Vulkan server | 25276 | 81968 | 16535 |
| Existing AR-owned ROCm server | 10056 | 81968 | 16531 |

The user's AR reported no active chat runs. Its settings file retained its
pre-investigation modification timestamp (2026-10-15 02:52:20).

No real Gemma inference was started for validation. The user's running AR and
Ollama services were not restarted or replaced; activating the correction
requires a subsequent build and restart of that AR instance.

At the user's request, after cleanup, a hidden one-shot Windows PowerShell helper (PID 82036, no listening port) requested S3 suspension after a 15-second delivery delay. It preserves running applications and records its outcome in .artifacts/direct-residency/suspend-state.json. Actual resume/outcome can only be inspected afterward.
