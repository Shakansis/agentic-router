# PLAN v63 - Windows mixed-vendor GPU telemetry

> Superseded for affinity by PLAN v64. The telemetry facts remain valid, but
> explicit CUDA, ROCm, and combined Vulkan selections now use isolated
> Agentic Router-owned Ollama servers instead of relying on cross-backend
> `main_gpu` semantics.

## Objective

Represent NVIDIA and AMD adapters together on Windows and report loaded-model
placement from observed Ollama runner evidence instead of treating configured
affinity as proof of placement.

## Scope

- Merge authoritative NVIDIA SMI identity and VRAM telemetry with non-NVIDIA
  DXGI adapter inventory.
- Read adapter-wide dedicated-memory usage from Windows GPU performance
  counters and correlate it by DXGI LUID.
- Use one stable device identity in discovery, runtime telemetry, and per-device
  memory policy.
- Identify a local default-port Ollama runner backend from loaded CUDA, ROCm, or
  Vulkan modules.
- Keep configured CUDA affinity separate from observed backend/device placement
  in the API and UI.
- Render every detected adapter, including a mixed NVIDIA + AMD fixture, without
  inventing a cross-backend `main_gpu` index.

## Boundaries

- Exact request-level affinity remains limited to authoritative CUDA indices.
  An AMD adapter is monitored and may be observed as ROCm, but remains on
  Ollama-managed `Auto` placement because `main_gpu=0` cannot distinguish
  `CUDA0` from `ROCm0`.
- Runner-module inspection is used only for the local default Ollama endpoint.
  Remote or custom-port endpoints report placement as unavailable rather than
  borrowing unrelated local process evidence.
- No model inference, provider call, driver change, Ollama restart, or GPU
  configuration change is part of validation.

## Validation

- Build the API and E2E projects in isolated Release output with zero warnings.
- Run focused deterministic browser E2E for runtime memory and mixed-vendor
  rendering.
- Run a read-only isolated API smoke against the existing local Ollama `/api/ps`
  endpoint and compare adapter telemetry with Windows counters.
- Run JavaScript syntax, formatting, intended-diff, and applicable full E2E
  validation.
- Preserve the user-started Agentic Router process and stop all task-owned test,
  build, and server processes.

## Completion evidence

- Isolated Release API/E2E builds and the final canonical Release solution build
  passed with zero warnings and zero errors.
- JavaScript syntax, formatting verification, and the focused runtime/mixed-GPU
  browser tests passed; focused E2E result: 2/2.
- Read-only live smoke enumerated the RTX 4090 as `CUDA0` and the RX 7900 XTX
  as `ROCm0`; Windows counters reported about 0.8 GiB and 23.6 GiB dedicated
  usage respectively. The active model was attributed to the AMD adapter from
  the observed `ggml-hip` runner module.
- With isolated `CUDA0` configuration, runtime status kept configuration and
  observation separate and warned that the runner was actually on ROCm.
- Full deterministic E2E pass 1 completed 407/410; all three unrelated failures
  passed individually. Pass 2 completed 409/410; the remaining pre-existing
  durable-supervision timing test again advanced past its expected intermediate
  state under suite load and passed independently 1/1.
- No real inference or generation request, driver change, Ollama restart, or
  GPU configuration change was performed; live validation used read-only
  `/api/ps` telemetry.
- Task-owned isolated outputs and processes were removed after validation.
