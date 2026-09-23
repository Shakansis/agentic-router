# Startup with unavailable external resources

Missing external applications or configured devices must not prevent Agentic Router
from opening its interface and settings. Availability warnings appear beside the Git
section of the project sidebar, with links to the relevant existing settings.

The sidebar uses Host availability evidence for configured knowledge providers,
Ollama, harnesses, enabled model providers, configured web search, Git in an active
workspace, and runtime profiles. Unconfigured knowledge/cloud integrations do not
produce connection warnings. Existing settings and feature-specific recovery and
execution restrictions remain authoritative. No provider, harness, or GPU is
silently replaced.

The warnings use the project folders' hidden-scrollbar region, bottom fade, and
down indicator. Wheel, touch, and keyboard scrolling remain available. Saved
change reviews are opened from the Git panel; the large standalone history review
button is removed. A saved review remains accessible there after restart even
when its live execution session has expired, with Host-provided Undo eligibility
preserved.

Expected AnythingLLM/Ollama connection failures and unavailable GPU diagnostics
produce actionable backend warnings with stable codes. These two HTTP clients
use provider-owned logging instead of the default raw HTTP exception logger.
Original exceptions remain attached to typed failures and logged at Debug level.
Visual Studio may independently display first-chance exception notifications;
those debugger messages do not mean the exception escaped its handler.

`GET /api/runtime/profiles` returns the saved profile configuration even when Ollama
cannot be reached. Its additive `diagnostics` array reports typed, friendly messages:

- `runtime-provider-unavailable`: discovery is unavailable; retained measurements
  are marked stale.
- `model-gpu-unavailable`: one model's saved GPU affinity cannot currently be used.
- `model-runtime-unavailable`: one model's planned runtime cannot be reached.

A model with unavailable runtime evidence does not prevent other models' profiles
from being inspected. These are read-only discovery rules; inference and GPU
placement validation are unchanged. Refreshing the existing settings/status
surfaces updates the sidebar and removes resolved warnings.

The browser also refreshes availability evidence every 60 seconds while the tab is
visible and immediately when the window regains focus. The checks run concurrently
for setup/harnesses, knowledge providers, runtime profiles, provider health, web
search, and Git. A failed individual check retains its last known state instead of
removing a warning without fresh evidence. The timer is suspended while the tab is
hidden, and overlapping refreshes share the same in-flight request.

Other startup failures retain the Retry screen. Parallel startup progress cannot
overwrite a failure message, and non-JSON HTTP errors receive a friendly message
instead of exposing a server error page or a JSON parser exception.

Regression coverage in `ExternalAvailabilityEndToEndTests` exercises the real
browser and API with a refused local connection, a missing harness executable,
and a saved affinity for a disconnected GPU. External model and knowledge services
are faked only at their boundaries; no real inference is required.

## Implementation and validation

Changed files:

- `Runtime/OllamaRuntimeProfileService.cs` and `Runtime/OllamaRuntimeProfileContracts.cs`:
  nonfatal discovery diagnostics and retained configuration.
- `Program.cs`: logger registration for the profile service.
- `wwwroot/app.js`, `wwwroot/index.html`, `wwwroot/styles.css`: sidebar availability
  warnings, settings links, friendly HTTP parsing failures, and stable Retry state.
- `tests/AgenticRouter.EndToEndTests/ExternalAvailabilityEndToEndTests.cs`: four
  browser/API regressions, including recovery after a genuine non-JSON HTTP 500.

Validation on 2026-09-22:

- `dotnet format AgenticRouter.slnx --no-restore --include ... --verify-no-changes`
  passed for the changed C# files.
- `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/external-startup --no-restore --nologo --disable-build-servers -m:1`
  passed with zero warnings and errors.
- `node --check AgenticRouter.Api/wwwroot/app.js` and `git diff --check` passed.
- Nine focused E2E tests passed: the four new regressions plus existing bootstrap,
  knowledge configuration/retrieval, and runtime-profile coverage. Results:
  `.artifacts/external-startup/results/external-startup-final.trx`.
- The captured browser screenshot was visually inspected: the interface opens
  with AnythingLLM absent, and its warning appears below Git.

Build output and test processes were isolated. The user's running application was
not restarted, and no real model inference or GPU workload was used.

Follow-up validation for the sidebar and logging changes:

- `AnythingLlmKnowledgeProvider`, `OllamaClient`, `KnowledgeContextService`,
  `ModelsController`, and `OllamaRuntimeProfileService` now own the explanatory
  warnings; `Program.cs` scopes the HTTP logger change to AnythingLLM and Ollama.
- The two cases of `ReviewAndUndoRemainEligibleAfterExplicitResume` verify the
  Git panel for both restored live execution and saved historical evidence.
- Ten focused E2E cases passed, including provider warning output, no default raw
  transport stack dump, hidden-scrollbar keyboard navigation and indicators,
  saved review/Undo behavior, knowledge retrieval, and project sidebar regression.
  Results: `.artifacts/external-startup/results/sidebar-availability.trx`.
- Release build (zero warnings/errors), scoped formatting verification,
  JavaScript syntax, and intended-diff checks passed. The screenshot
  `availability-project-scrolling.png` in the test results was visually checked.

Follow-up availability watcher validation:

- The sidebar now adds and removes availability warnings without a page reload.
- A deterministic browser test changes AnythingLLM between unavailable and available,
  triggers the same foreground refresh used after returning from an external app,
  and verifies both transitions.
- All five `ExternalAvailabilityEndToEndTests` cases passed with the periodic watcher
  active.
