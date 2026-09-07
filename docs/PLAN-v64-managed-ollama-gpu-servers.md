# PLAN v64 - Managed Ollama GPU servers

## Problem

Ollama `main_gpu` indices are local to the backend selected by the Ollama
server. On a mixed NVIDIA/AMD Windows host, `main_gpu=0` therefore cannot
distinguish CUDA 0 from ROCm 0. A saved RTX selection can still run on ROCm,
and AMD adapters cannot be offered honestly through the existing request-only
affinity mechanism.

## Decision

1. Preserve `auto` as the existing provider-managed endpoint.
2. Treat `ollama:N` as the compatible CUDA-N selection and add `rocm:N`.
3. Add an explicit `vulkan:all` selection for a heterogeneous Vulkan server
   with scheduling spread enabled. It is opt-in because Vulkan multi-GPU is
   experimental and can be slower than a single native-backend GPU.
4. Add `vulkan:prefer:<source-backend>:<source-index>` selections for every
   selectable physical GPU. The persisted identity is the discovered physical
   device, not a hardcoded vendor/model or a transient Vulkan index. On every
   startup, probe Vulkan discovery, map that physical device by its normalized
   adapter identity, then restart the isolated server with the preferred
   Vulkan ID first in `GGML_VK_VISIBLE_DEVICES` and `main_gpu=0`. Fail when the
   mapping is missing or ambiguous instead of silently choosing another GPU.
5. For the standard local Ollama endpoint on Windows, start one Agentic
   Router-owned headless server per selected target. Force the matching Ollama
   library and device visibility in that process, and route the request to its
   isolated loopback endpoint. Custom or remote Ollama endpoints remain
   user-managed and retain request-level `main_gpu` behavior.
6. Never fall back from an explicit GPU/backend target to another backend.
   Startup, library, port, or health failures are surfaced as typed provider
   failures.

`vulkan:all` requests scheduler spreading without a preferred device.
`vulkan:prefer:*` keeps all discovered Vulkan GPUs visible but disables forced
spreading and controls their enumeration order. This is a priority hint to the
current Ollama/Vulkan scheduler, not a promise that the preferred GPU will be
filled completely before any allocation reaches another GPU.

## Lifecycle and orphan safety

- Assign every managed Ollama server to a Windows Job Object configured with
  `KILL_ON_JOB_CLOSE`.
- Persist a bounded lease containing the PID, process start time, executable,
  endpoint, backend, and device selection.
- On startup, collect a previous lease only when PID, start time, executable,
  and listening-port ownership all still match. A partial identity match is
  never sufficient to terminate a process.
- On normal shutdown, stop only owned processes, wait for exit, then remove
  their leases. Never stop or reconfigure the user's tray/service Ollama.

## Validation boundary

Deterministic E2E coverage may use a fake Ollama endpoint and must not start a
managed server. Real CUDA, ROCm, or combined Vulkan inference remains an
explicitly authorized manual validation step.
