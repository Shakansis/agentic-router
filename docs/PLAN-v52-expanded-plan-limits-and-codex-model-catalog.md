# PLAN v52: expanded plan limits and Codex local-model metadata

## Goal

Apply the agreed balanced execution-plan limits and prevent Codex from falling back to generic metadata for exact local Ollama model slugs.

## Scope

- Raise the maximum plan size to 20 steps, objective length to 500 characters, step-title length to 160 characters, and revision count to 6.
- Keep defaults, saved-settings migration, validation, Host parsing, and model-facing schemas synchronized.
- Generate an isolated Codex model catalog from the Host-resolved exact model, context window, and image capability before App Server startup.
- Restart only the harness-owned idle App Server when its isolated catalog changes; preserve native session resume behavior.
- Add deterministic fake-Codex evidence without invoking a real model, GPU workload, or cloud provider.

## Ordered work

1. Centralize and migrate the balanced plan limits.
2. Generate and activate exact Codex local-model metadata at the harness boundary.
3. Extend fake-Codex evidence for the model catalog and plan schema.
4. Run formatting, Release build, focused E2E, diff inspection, and process cleanup.
