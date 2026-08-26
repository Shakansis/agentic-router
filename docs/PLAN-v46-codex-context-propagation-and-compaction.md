# PLAN v46 - Codex context propagation and compaction

## Objective

Keep the Codex harness on the same Host-resolved context window as Ollama and
trigger Codex automatic context compaction when total active context reaches 98
percent of that effective window.

## Scope and decisions

- Treat `HarnessTurnRequest.ContextWindowTokens` as authoritative for Codex,
  matching the existing Qwen Code contract.
- Pass `model_context_window`, `model_auto_compact_token_limit`, and
  `model_auto_compact_token_limit_scope = "total"` through the installed Codex
  App Server `thread/start` and `thread/resume` config object.
- Calculate the compaction threshold deterministically as
  `floor(contextWindowTokens * 98 / 100)` using integer arithmetic.
- Reject Codex turns that lack a positive Host-resolved context instead of
  silently falling back to Codex model metadata.
- Preserve thread reuse when the context is unchanged. If the effective context
  changes, start a new native Codex thread so the new config is authoritative;
  canonical Agentic Router conversation hydration preserves continuity.

## Implementation steps

1. Add a small Codex thread-context config value and validation helper.
2. Include the exact config in new and resumed Codex thread requests.
3. Bind the effective context to the cached native thread identity.
4. Extend the fake Codex App Server and browser-driven E2E assertions for the
   exact context window, 98-percent threshold, total scope, and thread reuse.

## Validation

- Validate against the installed Codex `0.149.0-alpha.4.1` generated schema.
- Run focused fake-provider Codex E2E coverage.
- Run `dotnet format`, a Release build, the complete deterministic E2E suite,
  `git diff --check`, and intended-diff review.
- Do not invoke a real model, cloud provider, GPU workload, or service restart.
