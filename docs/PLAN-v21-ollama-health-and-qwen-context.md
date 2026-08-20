# PLAN v21: Ollama health and Qwen context authority

## Goal

Correct two manual-test regressions without invoking inference: Ollama model
discovery must count as observed local-provider health, and the Qwen Code
harness must receive the same Host-authoritative context window displayed by
Agentic Router.

## Evidence

- Local Ollama `0.32.14` answered `/api/version`, `/api/tags`, and `/api/show`.
- `qwen3.8:27b-gpu0` declares a 262,144-token model maximum but has no
  `PARAMETER num_ctx`.
- The persistent user environment contains flash attention and q8 KV cache,
  but no `OLLAMA_CONTEXT_LENGTH`; Ollama therefore selects its 32k VRAM-tier
  default for the 24 GiB GPU.
- Active Agentic Router settings still contain a 32,768 application limit, a
  40,960 provider ceiling, and a context ladder ending at 40,960.
- `/api/models` successfully contacts Ollama but does not publish that success
  to `IProviderHealthMonitor`, so provider health can remain unknown while the
  sidebar correctly reports Ollama online.
- Qwen Code `0.21.13` accepts `generationConfig.contextWindowSize`, but the
  harness configuration currently omits it.

## Implementation

1. Record local model-discovery success and failure in the existing provider
   health monitor; refresh the health snapshot after initial model discovery.
2. Add the resolved external-harness context window to `HarnessTurnRequest`.
3. Include that context in Qwen daemon identity/configuration and emit it as
   `generationConfig.contextWindowSize`.
4. Keep the Host indicator and Qwen internal compaction limit on the same
   effective value.
5. Persist the requested 128k application/provider/profile/ladder settings and
   the GPU0 model tag's `num_ctx`, preserving `main_gpu=0` and existing model
   parameters. Do not restart Ollama or run a model.
6. Add deterministic E2E coverage for observed local health and 128k Qwen
   configuration.

## Validation

1. Focused provider-health and Qwen fake-provider E2E.
2. `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
3. Zero-warning Release build and the full Playwright E2E suite.
4. `git diff --check`, settings-file inspection, and read-only `/api/show`
   verification of the recreated tag.
5. No real Ollama generation or GPU inference without separate permission.

## Completed evidence

- Focused provider-health and Qwen E2E passed 11/11. The browser asserted
  `Ollama Local` as `Saudável`; the 128k harness test asserted both the public
  effective limit and Qwen's generated `contextWindowSize` as 131,072.
- Full deterministic Playwright E2E passed 225/225 with zero skips.
- Release build completed with zero warnings and zero errors.
- `dotnet format --verify-no-changes` and `git diff --check` passed.
- Active settings now use 131,072 for the application/provider limits and the
  specialist, primary, fallback, web-synthesis, and vision role targets and
  maxima. The escalation ladder now ends at 131,072.
- `qwen3.8:27b-gpu0` was recreated from existing local layers only. Read-only
  `/api/show` verification reported `main_gpu=0` and `num_ctx=131072` on digest
  `d1f9a27632f9cab927948254285394838a1f0e0f8b7e70e53d633624ed9e9169`.
- No model generation, cloud request, model download, Ollama restart, or GPU
  inference was performed.
