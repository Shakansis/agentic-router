# Agentic Router v0.9.0

This development version adds a narrow, host-owned Git delivery workflow to Execute review.

## Highlights

- Shows fresh branch, HEAD, upstream, ahead/behind, operation, conflict, staged, unstaged, and untracked state.
- Separates current execution-session files from pre-existing user changes and stages only explicit selections.
- Binds successful validation to selected file hashes and blocks stale or missing validation unless an allowed override is explicitly approved.
- Creates one exact commit, optional annotated tag, current-upstream branch push, and exact-tag push through structured operations.
- Requires immutable explicit approval for every Git write, including with the automatic execution policy.
- Records commit, tag, branch-push, and tag-push facts separately and disables history-contradicting internal undo after commit.

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
