# PLAN v47 - Live context, memory, and vision

## Objective

Expose the active model context in real time, keep the runtime popover focused
only on memory and GPU facts, and ensure validated image attachments reach the
selected vision-capable model instead of being silently discarded in Execute.

## Scope and decisions

- Consume the installed Codex App Server `thread/tokenUsage/updated`
  notification and publish ordered `context.usage` events while reasoning is in
  progress. Display active input plus generated output against the effective
  context window.
- Preserve Host estimates before the first provider update and replace them only
  with usage reported for the active inference.
- Keep the runtime popover limited to system/GPU memory meters and loaded-model
  rows. Each loaded row identifies the configured GPU when known, current VRAM
  and RAM allocation, active context length, and total runtime allocation with
  that context.
- Move cloud-usage access to the existing Runtime and memory settings section;
  remove provider health, resident diagnostics, token usage, costs, and runtime
  implementation details from the memory popover without deleting capability.
- Forward validated images to Native Ollama/cloud structured requests and to the
  Codex App Server as `image` user-input data URLs. Reject images explicitly for
  external harnesses whose adapter does not support them; never strip them.
- Keep all new visible strings in the English i18n catalog.

## Implementation steps

1. Extend harness usage events with active-context totals and parse Codex token
   usage notifications.
2. Extend context snapshots and rendering with live active/output tokens.
3. Add GPU identity to runtime contracts and simplify the memory popover.
4. Carry validated images into Native provider calls and Codex turn input.
5. Add fake-provider E2E coverage for in-flight context updates, memory-only UI,
   exact GPU/model memory facts, and image delivery to Native and Codex.

## Validation

- Validate Codex protocol fields against installed `0.149.0-alpha.4.1` schemas.
- Run focused context, runtime-memory, Native vision, and Codex vision E2E tests.
- Run format verification, Release build, the full deterministic E2E suite, and
  intended-diff checks.
- Do not invoke real Ollama/cloud inference, restart services, or run GPU work.

## Completion evidence

- Completed on 2026-08-24.
- Installed Codex App Server schema `0.149.0-alpha.4.1` confirmed the
  `thread/tokenUsage/updated` notification and multimodal `turn/start` input.
- Focused browser/API E2E coverage passed for Codex live context plus vision,
  Native live thinking context, Native vision, the memory-only popover, and the
  relocated cloud-usage workflow.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore` passed.
- `dotnet build AgenticRouter.slnx -c Release --no-restore -m:1` passed with
  zero warnings and zero errors.
- Full deterministic E2E suite passed: 311 passed, 0 failed, 0 skipped.
- `git diff --check` passed. No real model, GPU, cloud provider, or service
  restart was used for validation.
