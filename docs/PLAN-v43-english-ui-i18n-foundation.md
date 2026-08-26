# PLAN v43 - English UI and i18n foundation

## Objective

Make English the only user-visible language for the current release, repair the
shared decision-modal presentation, and establish a small localization boundary
that supports adding translated catalogs later without introducing a frontend
framework or changing product behavior.

## Scope and decisions

- Keep API contracts, settings schema, Composer behavior, workspace authority,
  approvals, and execution semantics unchanged.
- Separate confirmation/decision dialogs from value prompts. A decision dialog
  never owns an input element; a prompt creates its field only for the lifetime
  of the prompt that explicitly requests a value.
- Give destructive primary actions an explicit danger surface with readable
  foreground contrast; secondary danger actions keep their current lighter
  treatment.
- Use English as the source and fallback locale. Add a dependency-free i18n
  module with semantic keys, interpolation, document-attribute localization,
  and an explicit locale boundary. Add no second locale in this milestone.
- Convert all permanent markup, labels, tooltips, ARIA text, modal copy, dynamic
  browser statuses, and browser-generated UI to English. Preserve raw backend,
  provider, model, harness, Git, and user-authored text as supplied.
- Keep future localization deterministic: missing keys fall back to English and
  never expose a key token to the user.

## Implementation steps

1. Repair the shared modal primitive and destructive primary-button styling.
2. Add the English catalog and localization helper before `app.js`; localize
   static semantic surfaces and shared dynamic primitives through that boundary.
3. Translate the remaining user-visible HTML and JavaScript copy to English and
   update affected accessibility attributes and deterministic E2E assertions.
4. Add browser coverage for fieldless confirmations, intentional prompts,
   destructive-action contrast, English-only primary surfaces, and locale state.

## Validation

- `node --check` for each browser script.
- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
- `dotnet build AgenticRouter.slnx -c Release --no-restore` with zero warnings.
- Focused fake-provider Playwright E2E; no real model or cloud inference.
- Browser visual checks for confirmation, prompt, Settings, workspace, Git,
  conversation, benchmark, and responsive states.
- Source-language audit and `git diff --check`.

