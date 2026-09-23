# Managed Ollama startup recovery

The Host waits up to 120 seconds for both a successful `/api/version` response
and discovery evidence for the explicitly selected backend. HTTP availability
alone never permits a CPU or different-GPU fallback.

If readiness times out, the Host cleans up that owned
process and retries once. Port-binding startup recovery uses the same bounded
retry, not an additional loop. Invalid configuration and unrelated port
ownership remain distinct failures. A process that exits with a deterministic
startup failure is still reported with its captured evidence; it is not blindly
restarted or described as merely slow.

For Chat and Execute (including supervisor and worker turns), a second readiness
failure displays a nonblocking startup warning. It asks the user to check Ollama
and the selected GPU/runtime, but does not open a question, require a response,
or change the session to awaiting-user-input. The warning distinguishes an
HTTP-responsive server without backend evidence from a non-ready server.

The second process remains alive for up to 600 additional seconds of readiness
monitoring. A ready server continues the original turn automatically and clears
the warning. Otherwise the Host cleans up its starting process and reports
`managed-ollama-start-timeout`. Stop cancels the matching turn and its startup
wait. Browser close or refresh does not cancel an accepted turn. Healthy managed
servers remain usable while another server is starting or recovering.

Callers without a progress stream receive the two automatic attempts. Custom,
remote, and Auto endpoints remain user-managed. This policy governs managed
server startup, not model-loading or generation timeouts.

Host configuration under `AgenticRouter:ManagedOllama` supports
`AttemptTimeoutSeconds` (default 120, range 1-3600) and
`RecoveryTimeoutSeconds` (default 600, range 1-86400). Tests use short real
durations and fake provider processes, not mocked Host state. `ExecutablePath`
and `PortOffset` let isolated tests use a fake Ollama executable and separate
managed ports without touching the user's Ollama installation or instances.

## Incident 0HNOOS0EDESHM:000002FF (2026-09-22)

The installed Ollama 0.34.2 ROCm package had invalid DLLs, including zeroed PE
headers in `ggml-hip.dll`, `rocblas.dll`, and `libhipblas.dll`. HTTP startup
succeeded but GPU discovery returned no devices. Removing device masks did not
fix discovery. Replacing the damaged ROCm directory with the same-version
official package restored `ROCm0: AMD Radeon RX 7900 XTX`; a separate `ollama
serve` probe using the Host device environment became HTTP-ready in 1.2 seconds
and logged `library=ROCm compute=gfx1100`. No inference was run for this check.

The official archive SHA-256 was
`a019f59490a28ab04f65e291716ee2147d92df373aa3978aa9073e1f6cb1ce82`.
All 908 installed ROCm files were checked against the archive. The original
directory was retained for rollback. This repair fixes the observed loader
failure; longer readiness waits alone would not have fixed damaged DLLs.

## Validation for this change

- Release solution build succeeded with zero warnings and errors, using
  `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rocm-startup-recovery --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false`.
- `dotnet format --verify-no-changes` passed for the changed C# files.
- `node --check AgenticRouter.Api/wwwroot/app.js` and `git diff --check` passed.
- All 11 focused tests passed: eight browser/API recovery cases and three
  existing managed-server lifecycle/port regressions. The new cases cover slow
  startup, one automatic retry, warning without user input, automatic recovery,
  recovery-window expiry, Chat/Native/Qwen Stop, and browser refresh.
- The rendered nonblocking warning was captured and visually inspected.
- Real hardware validation covered only DLL loading and server/GPU discovery;
  model loading and inference were not executed. The running Router and CUDA
  process were not restarted. The package repair applies to the next ROCm
  startup; the new Host timeout/alert code requires a rebuilt/restarted Router.
