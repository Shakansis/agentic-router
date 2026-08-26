# PLAN v45 - Chat read-only workspace tools

## Objective

Allow Chat mode to inspect the active trusted workspace without requesting user
approval, while keeping every mutation and process capability unavailable.

## Scope and decisions

- Offer only `list_files`, `read_file`, `get_file_info`, and `search_text` to the
  selected Chat model.
- Reuse the existing canonical tool schemas, name resolver, trusted-workspace
  validation, bounded read implementations, provider dispatch, usage recording,
  and tool-message protocol.
- Never create an Execute session and never enter the approval coordinator from
  the Chat read path. Independently reject any proposal that is not canonical,
  not offered, or not validated as read-only.
- Keep Chat usable without a configured valid workspace by falling back to its
  existing text-only path. Preserve image and explicit web-search behavior on
  their existing provider path.
- Bound the read loop and expose read activity as ordinary technical evidence;
  only the final model completion contributes to the visible assistant answer.

## Implementation steps

1. Replace the Chat-disabled-tools branch with a read-only workspace tool path.
2. Resolve the selected model's existing tooling profile and run a bounded
   provider-neutral tool loop using only the four canonical read schemas.
3. Validate and execute reads through `LocalActionService` with a null Execute
   session, return authoritative tool results, and render the final completion.
4. Add fake-provider E2E coverage proving that Chat under `ask` reads a workspace
   file without an approval event and still cannot execute a requested mutation.

## Validation

- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
- Release build with zero warnings.
- Focused and complete fake-provider Playwright E2E coverage.
- `git diff --check` and intended-diff review.
- No real model, cloud call, service restart, or GPU workload.

