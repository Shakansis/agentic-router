# PLAN v19: OpenCode manual-acceptance fixes

## Goal

Correct the two defects found in the first real OpenCode manual test without
changing Codex behavior: classify OpenCode reasoning parts before routing their
`field: text` deltas, and restore estimated then provider-reported context usage
for external harness turns.

## Evidence

- OpenCode `1.18.18` emits both text and reasoning through
  `message.part.delta` with `field: "text"`; the part type is carried by the
  preceding `message.part.updated.properties.part.type`.
- The current adapter treats every `field: "text"` delta as assistant output
  and retains `message.part.updated` only as a diagnostic event. This leaks
  reasoning into the visible answer.
- `ChatStreamService` intentionally skips its ordinary initial context event
  for Execute. Native later publishes specialist measurements, but the external
  harness path publishes none.
- OpenCode `message.updated.properties.info.tokens.input` contains final
  provider usage for assistant messages.

## Implementation

1. Track OpenCode part identity/type and accumulated text per turn.
2. Route `message.part.delta` by its registered part type; backfill only unseen
   text from full part updates and never default an unknown text delta to the
   visible assistant answer.
3. Normalize current OpenCode tool-part state transitions and assistant token
   usage while retaining every native payload.
4. Publish an estimated context snapshot before an external harness starts and
   replace it with exact provider-reported OpenCode input usage when available.
5. Update the fake OpenCode server to emit the installed `1.18.18` event shapes
   and add browser/API regressions proving reasoning placement, visible answer,
   exact context count, tools, terminality, and no Codex regression.

## Validation

1. Focused OpenCode and Codex harness E2E.
2. `dotnet format AgenticRouter.slnx --verify-no-changes`.
3. Release build with zero warnings.
4. Full Playwright E2E suite.
5. `git diff --check` and intended-diff inspection.

No real model call is authorized or required for this correction.

## Completion evidence

- Focused OpenCode E2E: 6/6 passed.
- Focused Codex E2E: 11/11 passed.
- Format verification passed.
- Isolated Release solution build passed with 0 warnings and 0 errors while the
  user's Visual Studio process continued running.
- Full Playwright E2E: 214/214 passed in 5m48s.
- No real Ollama/OpenCode inference was run.
