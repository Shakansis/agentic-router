# Model library validation — 2026-10-17

Implemented the model library redesign and explicit install/remove actions described in [model-library.md](../model-library.md), plus last successful use from retained AR usage history.

## Changed areas

- `wwwroot/index.html`, `panels.css`, `app.js`, `settings-runtime.js`: installation/search area, consistent labeled settings fields, responsive model cards, last-use dates, progress/status, native AR removal confirmation, refresh and preserved unsaved settings.
- `Setup/LocalSetupService.cs`, `LocalSetupService.Models.cs`, `Controllers/SetupController.cs`: explicit library pull endpoint, provider progress polling, exact CLI removal with configured `OLLAMA_HOST`, serialized AR inference and verified model registry effects. Existing recommended-only onboarding remains unchanged in scope.
- `Usage/UsageLedger.cs`, `Models/ModelOrganizationService.cs`, `Program.cs`: exact provider/model last successful nonempty inference timestamps derived from retained history, with an explicit unavailable state on storage errors. No additional persistent data.
- `ModelLibraryEndToEndTests.cs`, `FakeOllamaServer.cs`, `FakeOllamaCli/Program.cs`: external-boundary fixtures and browser/API/CLI coverage.

## Executed validation

1. `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers`: passed, zero warnings/errors.
2. Scoped `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore --include ...` for the changed C# files: passed. The only subsequent C# change added the correct settings-section navigation to a test.
3. `node --check` for `wwwroot/app.js` and `wwwroot/settings-runtime.js`: passed. `git diff --check`: passed.
4. Five new E2E cases passed: install/alias/cancel/remove/reinstall; pull/CLI/unverified-removal failures and invalid names; provider progress with settings closed; last-use persistence across an isolated Host restart; responsive layout, filters, affinity and preservation of unsaved General settings.
5. Four existing E2E cases passed: recommended setup downloads; model organization/profiles/workspace references; stable model GPU affinity; single-GPU affinity UI.

Results are in `.artifacts/rpg-editor/TestResults/`. `model-library-final.trx` contains eight passes and one test-navigation failure: reopening Settings correctly selects General, while the new test initially expected Models to remain selected. After navigating to Models explicitly, that case passed in `model-library-progress.trx`. The earlier four-case library suite and four-case regression suite also passed. No failing product assertion was removed or relaxed.

## Visual review

Reviewed actual Playwright screenshots at desktop 1600 and mobile 390 pixels, including the full model card, GPU field and destructive action. Layout assertions and captures also cover 1280 and 760 pixels. All controls meet the checked minimum height and the library/cards do not overflow horizontally. Captures are in `.artifacts/rpg-editor/visual-review/model-library-*.png` and `model-library-card-*.png`.

## Validation boundary and activation

Tests exercised a real browser and AR API with Ollama HTTP and CLI faked only at the external provider/process boundary. No real model was downloaded, removed or invoked, and no user Ollama process was restarted. Automatic test disposal left no testhost, fake-provider CLI or Playwright headless Edge process behind.

The user's AR process PID 14028 remains on ports 5294/7155/7156; external Ollama PID 21428 remains on 11434. Other pre-existing/user-owned Ollama processes were left untouched. The read-only check of the running AR returned 22 model entries and no `lastUsedAt` property: it still uses the previous backend build. Rebuild/restart AR normally to activate the new backend; restarting Ollama is not required. The validated build is isolated under `.artifacts/rpg-editor`.
