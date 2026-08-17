# PLAN v5: Ollama system-message ordering

## Goal

Prevent native Execute planning from sending multiple `system` messages to Ollama templates that require one system message at the beginning.

## Scope

- Consolidate the planner prompt and all Host-owned system context into one leading `system` message.
- Preserve the original system-content order and all non-system conversation/tool messages.
- Add deterministic browser/API evidence for the native tool-request shape.
- Do not invoke a real Ollama model.

## Ordered work

1. Normalize native planning messages at the planner request boundary.
2. Assert one leading system message in the fake-provider E2E path.
3. Run formatting, Release build, focused E2E, and diff checks.
