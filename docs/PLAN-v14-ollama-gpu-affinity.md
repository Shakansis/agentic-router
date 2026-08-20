# PLAN v14 — Ollama GPU affinity

## Objective

Make the existing GPU selections effective for Ollama Local requests. Allow the
router, resident action model, legacy coordinator, default/manual model, and
each intent specialist to select `Auto` or one exact CUDA GPU. The resident can
therefore remain on a smaller GPU while specialists use the primary GPU.

## Ordered implementation

1. Discover NVIDIA devices through bounded `nvidia-smi` metadata so the UI uses
   the same stable UUID, name, and CUDA index that Ollama sees. Preserve the
   existing Windows SetupAPI discovery as a non-selectable-order fallback.
2. Add explicit router, resident-action, and coordinator GPU settings. Retain
   the existing default and per-intent GPU settings and portable YAML support.
3. Resolve every saved selection to an Ollama `main_gpu` index. `default`
   inherits the global default and `auto` omits `main_gpu`.
4. Send `main_gpu` in every local Ollama generation/tool request and in resident
   preload. Treat a resident GPU change as a lifecycle change that unloads and
   reloads the resident before settings are committed.
5. Show the CUDA index and UUID-backed identity in Settings, while preserving
   one-GPU systems and provider-managed automatic selection.
6. Add deterministic browser/API coverage proving settings round-trip and exact
   `/api/chat` payloads for resident preload, router, and specialist requests.
7. Validate formatting, Release build, focused E2E, full E2E, and intended diff
   without invoking a real model.

## Boundaries

- This uses Ollama's request-level `options.main_gpu`; Agentic Router does not
  start, stop, or supervise the Ollama daemon.
- Exact affinity requires the Ollama process to see every selectable GPU. A
  process-wide `CUDA_VISIBLE_DEVICES` restriction still takes precedence.
- CUDA indices come from `nvidia-smi`; unsupported vendors keep `Auto` unless a
  future runtime exposes an equally authoritative index.
- GPU affinity changes placement, not context, KV-cache type, parallelism,
  quantization, or model content.
- Existing unrelated dirty-worktree changes are preserved.
- No real Ollama/GPU or cloud inference is authorized by this plan.

## Validation status

- JavaScript syntax check: passed.
- `dotnet format --verify-no-changes`: passed.
- Release build: passed with zero warnings and zero errors.
- Focused GPU-affinity E2E: passed, including resident preload, router,
  specialist, and conflicting same-model affinity rejection.
- Full deterministic Playwright E2E: 203/203 passed.
- Live Ollama 0.32.14 discovery: both NVIDIA GPUs visible after removing the
  process-wide CUDA restriction.
- Authorized resident preload: verified on the RTX 2070 SUPER by exact NVIDIA
  GPU UUID and Ollama runner logs; no user chat or specialist inference ran.
