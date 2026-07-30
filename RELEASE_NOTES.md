# Agentic Router v0.8.1

This maintenance release stabilizes the supervised Execute workflow introduced after v0.8.0.

## Highlights

- Separates the lightweight intent router from the execution coordinator.
- Adds behavioral tool-protocol conformance checks per model, Ollama version, and model digest.
- Treats malformed XML, truncated JSON, missing native tool calls, and related parser failures as typed recoverable protocol failures.
- Changes strategy after deterministic protocol failures and bounds retries, recovery, iterations, tool calls, and elapsed execution time.
- Makes execution plans host-owned, with host-generated identifiers and validated dependencies.
- Strengthens trusted-workspace confinement, file-conflict handling, validation evidence, execution review, and undo state.
- Expands browser-driven E2E coverage for planning, approvals, tools, recovery, persistence, validation, and protocol incompatibilities.

## Compatibility note

Ollama's declared `tools` capability is not treated as proof that a model can coordinate tools reliably. Direct coordination is enabled only after the exact installed model passes the host's behavioral conformance benchmark.
