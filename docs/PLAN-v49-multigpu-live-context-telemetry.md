# PLAN v49 - Multi-GPU and live context telemetry

## Objective

Keep context usage visibly current for every harness and represent every detected
GPU in the memory submenu without claiming telemetry precision the runtime does
not provide.

## Scope

- Publish throttled live context estimates from streamed reasoning and answer
  deltas for external harnesses that do not report exact usage during a turn.
- Prefer harness-reported usage whenever available and include reported output
  tokens in terminal context totals where the protocol exposes them.
- Show an accessible warning icon and tooltip while live usage is estimated,
  identifying the selected harness as the reason exact live usage is unavailable.
- Render one memory card per detected Ollama GPU, including GPUs with no model
  currently reported by Ollama.
- Show estimated context/runtime allocation derived from loaded allocation minus
  installed model size, explicitly noting that it includes KV cache and other
  runtime buffers.
- Add deterministic browser E2E coverage for exact and estimated live telemetry,
  the warning state, all detected GPUs, and the memory estimate.

## Validation

- Run focused browser E2E tests for context telemetry and runtime memory.
- Run formatting verification, Release build, full deterministic E2E, and
  `git diff --check`.
- Do not start a real model, GPU workload, cloud request, or development server.
- Preserve the user-started application process and report it explicitly.
- Shut down all test/build processes and verify no test-owned repository listener
  remains before completion.

## Completion evidence

- Completed on 2026-08-24 without real model, GPU, cloud, or development-server
  execution.
- Focused browser and contract tests passed for Native, Codex, OpenCode, Qwen
  Code, Claude Code, multi-GPU rendering, warning accessibility, and terminal
  reported usage.
- Isolated Release build passed with zero warnings and zero errors while the
  user-started Release application remained running.
- Formatting verification and `git diff --check` passed.
- Full deterministic E2E passed: 312 passed, 0 failed, 0 skipped.
- Post-validation cleanup removed the isolated v49 artifacts and stopped all
  test/build processes; the only remaining repository process is the preserved
  user-started application, PID 47648.
