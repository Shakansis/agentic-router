# PLAN v27: OpenCode protocol revalidation

## Goal

Revalidate the completed OpenCode experimental harness against the installed
OpenCode `1.18.18` API and make only the corrections required by Milestone 2.
Preserve the existing generic harness boundary, Native and Codex behavior, and
all unrelated dirty-worktree changes.

## Current evidence

- The installed executable reports `1.18.18` and its owned headless server
  publishes OpenAPI 3.1 JSON at `/doc`.
- The live API confirms the health, session, asynchronous prompt, SSE event,
  permission reply, abort, and diff endpoints used by the adapter.
- `permission.v2.asked` carries `action` plus `resources`; legacy
  `permission.asked` carries `permission` plus `patterns`. The deterministic
  fake currently combines the legacy event name with the v2 payload.
- Assistant `message.updated` information reports `providerID` and `modelID`.
  The adapter sends the selected identity but does not yet reject a reported
  substitution.
- SSE reads currently have no inactivity bound after response headers arrive.

## Corrections

1. Map both installed permission event shapes exactly and preserve their native
   payloads.
2. Verify the provider/model reported for assistant messages; abort and emit
   one typed terminal failure on substitution.
3. Bound SSE inactivity using the existing OpenCode request timeout and type
   EOF caused by owned-process exit distinctly.
4. Align the fake OpenCode server with the installed schemas and add E2E cases
   for legacy permission compatibility, reported identity substitution, and an
   unavailable executable.

## Validation

Run focused OpenCode plus Native/Codex regression E2E, the Release solution
build, `dotnet format --verify-no-changes`, and `git diff --check`. Do not run a
real OpenCode prompt or model inference.

## Stop

Report the Milestone 2 manual-test handoff and stop. Do not begin another
harness milestone.
