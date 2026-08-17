# PLAN v1: Chat UI refinement using DSH as a visual reference

## Goal

Modernize only the Agentic Router Chat working surface. Preserve the current frontend state, APIs, routing, Execute semantics, approval behavior, model selection, context accounting, Web/image controls, and Host-owned activity facts while making the composer and conversation feel like one cohesive, compact workspace surface.

## Scope

- Refine the existing Chat HTML and CSS; make only narrowly required JavaScript changes for presentation or accessibility.
- Keep every current composer control and DOM identity used by the frontend and E2E suite.
- Reduce card-within-card visual noise in assistant turns and render existing activity groups as compact inline technical details.
- Improve wrapping, truncation, focus treatment, and narrow-viewport behavior.
- Do not add backend contracts, reasoning reconstruction, context injection data, dependencies, protocols, or application-wide styling changes.

## Ordered implementation

1. Capture the current Chat/component structure and existing browser assertions.
2. Restructure the composer markup only where needed to form an integrated status, editor, action, and hint hierarchy without duplicating state.
3. Refine conversation, message-action, activity, and composer CSS using the existing tokens and controls.
4. Add focused E2E assertions for integrated layout, preserved controls, compact activity, truncation, and responsive containment.
5. Run formatter verification, Release build, the focused Chat UI tests, the full fake-provider E2E suite if the focused proof is stable, and `git diff --check`.
6. Inspect the real browser surface at desktop and compact widths. Do not invoke a real Ollama model or cloud provider.

## Acceptance

- Chat and Execute remain selectable and visually unambiguous.
- Web, image attachment, approval, Auto/manual model, conversation pin, context usage, status, and Send remain available and accessible.
- Long model names and technical output cannot widen the application shell.
- Assistant prose remains primary; existing technical activity remains available in compact collapsible rows with observable states.
- Optional context/reasoning sections are omitted unless equivalent provider-visible data already exists.
- No runtime, provider, routing, permission, or execution behavior changes.

## Validation boundary

All automated validation uses the deterministic fake-provider browser/API path. Real Ollama/GPU inference and real cloud calls remain outside this plan unless separately authorized.

## Implementation status (2026-08-16)

- Integrated Chat composer, conversation density, compact activity styling, responsive containment, accessible Send states, and inspectable truncated model identity: implemented.
- Existing DOM identities, Chat/Execute behavior, approval state, Auto/manual model selection, conversation pinning, Web/image controls, context usage, status hints, message actions, and Host activity data: preserved.
- Provider-visible reasoning and dedicated context-injection content are not exposed by the current frontend contracts, so no empty or reconstructed sections were added.
- Release build: passed with zero warnings and errors.
- Focused deterministic browser/API validation: 11/11 passed across integrated/responsive composer, Web/images, Chat/Execute, approval, Auto/manual/pinned models, normal Chat, Markdown, long content, message editing/focus, and tool activity.
- Browser visual inspection: passed at 1440 px, 540 px, and 420 px; no page or composer overflow at the narrowest width.
- `dotnet format` passed for the changed C# test file. The solution-wide verification remains blocked by four pre-existing whitespace findings in `AgenticRouter.Api/Chat/ChatStreamService.cs` lines 6195-6198, which is outside this UI-only change.
- Real Ollama/GPU inference and real cloud calls: not run.
