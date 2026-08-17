# PLAN v8: specialist-owned Execute toolset negotiation

## Goal

Keep explicit model selection authoritative and let the selected Execute specialist request the smallest Host tool schema set it needs without semantic tool selection by the Host.

## Scope

- Skip FunctionGemma routing, Teacher catalog creation, advisory events, and routing usage when the user selected a concrete model.
- Add the Host-owned `request_toolset` meta-tool to native specialist coordination.
- Initially send a compact catalog plus only the full `request_toolset` schema.
- Resolve requested names through the existing canonical/curated-alias registry and grant only tools inside the existing hard turn scope.
- Keep grants additive, bounded, approval-neutral, non-executing, and separate from workspace action/effect evidence.
- Preserve the existing validation, approval, execution, effect-proof, completion, cancellation, and chronological thinking paths.
- Keep the structured-output compatibility path specialist-owned; it already sends no executable native tool schemas.
- Use only deterministic fake-provider tests; do not invoke Ollama, a GPU, or cloud providers.

## Ordered work

1. Gate FunctionGemma routing on Auto selection before any Teacher catalog or provider request is created.
2. Add the canonical meta-tool contract, compact catalog, granted-tool projection, and request parsing.
3. Maintain bounded additive grants in Execute turn state and return authoritative native tool results without recording workspace actions or effects.
4. Render one compact user-facing toolset request while retaining technical event details.
5. Adapt the external fake Ollama boundary and add focused E2E coverage for explicit/Auto routing, initial discovery, grant, expansion, invalid names, guarded tools, no-tool completion, and structural schema economy.
6. Run format verification, Release build, focused E2E, full E2E, `git diff --check`, and inspect the complete intended diff.
