# PLAN v42 - Main UI and project sidebar simplification

## Objective

Make projects and their conversations the permanent focus of the left sidebar,
move compact Ollama/RAM/VRAM status into the chat header, and reduce Git to the
small project actions requested by the user without changing the Composer or
Host-owned workspace, approval, persistence, and execution semantics.

## Scope and decisions

- Keep the existing plain HTML/CSS/JavaScript stack and preserve Composer DOM
  identities and behavior.
- Render every saved workspace as an independent project accordion. Load the
  bounded global conversation result already provided by `POST /api/sessions/search`
  and group it by `workspaceId`; keep archived sessions out of the default lists.
- Use one compact search field for the entire project section. Filtering is
  client-side over the already loaded cross-workspace result so it does not add
  one request or one control per project.
- Persist expanded project IDs and collapsed sidebar state in local storage.
  Keep the existing width/resizer behavior for the expanded state.
- Preserve workspace management in its existing dialog. Full paths appear as
  project tooltips and in that dialog, never as permanent sidebar copy.
- Remove permanent provider/model/device/usage/cloud cards. Keep token/cloud
  details in Settings and the existing cloud usage dialog; render Ollama plus
  RAM/VRAM as compact header indicators backed by existing API data.
- Keep the existing resource-detail content in the header popover, with bounded
  viewport positioning and document-level outside-click closing.
- Present workspace accordions using the proven Codex project-list pattern:
  borderless folder rows, conversations directly below the expanded row, a
  subtle active marker, clean bounded scrolling, and one contextual project
  details popover with conversation count, full path, and the existing project
  management entry point. Preserve independent expansion and resume behavior.
- Replace the permanent Git dashboard card with a compact project Git section:
  initialize when absent; branch/change signal plus Commit and Push when present.
  Git writes remain structured, confined to the active workspace, stale-bound,
  Execute-only, and explicitly confirmed. Commit stages the exact status paths
  shown by the Host and verifies the staged set before committing. An explicit
  message is used unchanged after existing validation. An empty message may call
  only the explicitly selected local Ollama model and records that provider use.
  Push uses only the already configured upstream and existing guarded preflight.
- Remove the broad Git dashboard/diff/configuration entry point from the main
  UI. Execution-session delivery/review remains unchanged.
- Remove the duplicated sidebar footer shortcuts for New conversation,
  Benchmark, and Settings; their existing top-bar controls remain authoritative.
- Add a `View folder` Git quick action. The browser sends no filesystem path;
  the Host resolves the active trusted workspace and opens only that validated
  directory through the Windows shell boundary.

## Implementation steps

1. Add the minimal workspace Git commit/push contracts and controller actions by
   composing the existing repository service; add local-model commit-message
   generation only for an empty user field.
2. Replace sidebar markup with project header/search, project accordion host,
   compact Git actions, collapse control, and essential footer actions.
3. Rework frontend state/rendering for cross-workspace conversations, independent
   accordions, active workspace activation/resume, compact runtime meters, popover
   dismissal, collapse persistence, and minimal Git actions.
4. Remove obsolete permanent sidebar styles/handlers and add bounded responsive,
   keyboard, tooltip, and focus behavior without touching Composer markup.
5. Update deterministic browser/API E2E coverage for grouping, search, collapse,
   runtime details, initialization, explicit/generated commit messages, push
   failure/success reporting, and preserved Chat/Execute/Benchmark controls.

## Validation

- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`
- `dotnet build AgenticRouter.slnx -c Release --no-restore`
- Relevant fake-provider Playwright E2E tests; no real Ollama/GPU/cloud inference.
- `git diff --check` and intended-diff inspection.
- Manual acceptance remains open for the user's 19-point browser checklist.
