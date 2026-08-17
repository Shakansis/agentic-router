# PLAN v7: Ollama Chat system-message ordering

## Goal

Prevent ordinary Ollama Chat requests from being rejected by templates that require the system message at the beginning.

## Scope

- Consolidate all Host-owned Chat `system` content into one leading message at the Ollama adapter boundary.
- Preserve system-content order, non-system message order, and last-user image attachment behavior.
- Cover ordinary Chat and late-added local context such as web-search results without changing cloud-provider payloads.
- Add deterministic fake-provider evidence and do not invoke a real Ollama model.

## Ordered work

1. Normalize Ollama Chat messages while creating the provider request.
2. Update the browser/API assertion for one leading consolidated system message.
3. Run formatting, Release build, focused E2E, and diff checks.
