# PLAN v48 - Loaded-model memory layout

## Objective

Match the requested loaded-model memory presentation with a compact aggregate
summary, one card per GPU, and explicit model/context details without changing
runtime telemetry or execution behavior.

## Scope

- Add aggregate used/total GPU memory and percentage beside the section title.
- Group loaded models by the configured/reported GPU identity.
- Show model VRAM, residual system/driver VRAM, and context share as compact
  metric rows, followed by model RAM and total context summaries.
- Keep model names and per-model allocations accessible through an expandable
  Details section.
- Preserve honest Automatic GPU, CPU, and GPU-not-reported states.
- Add browser E2E coverage for the grouped layout and retained model identity.

## Validation

- Run the focused runtime-memory browser test.
- Run formatting verification, Release build, full deterministic E2E, and
  `git diff --check`.
- Do not start a real model, GPU workload, cloud request, or development server.
- Shut down residual test/build processes and verify no repository listener
  remains before completion.

## Completion evidence

- Completed on 2026-08-24 as a presentation-only change.
- Focused runtime-memory browser test passed, including aggregate header,
  GPU-grouped metrics, summary rows, and expandable model identity.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` passed.
- Release build passed with zero warnings and zero errors.
- Full deterministic E2E passed: 311 passed, 0 failed, 0 skipped.
- No real model, GPU workload, cloud provider, or development server was used.
- Post-test cleanup shut down .NET build servers and one orphaned test-owned
  `AgenticRouter.Api.exe`; final process and listener counts were both zero.
