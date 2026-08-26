# PLAN v44 - Model test configured timeout

## Objective

Make the Settings model connectivity test honor the validated runtime generation
timeout so cold model loads are not cancelled by an unrelated fixed 15-second
limit.

## Scope and decisions

- Keep the model-test API, prompt, provider path, runtime profile, usage evidence,
  and UI result contract unchanged.
- Remove the diagnostic service's duplicate 15-second cancellation source. The
  Ollama provider already owns and reports the configured
  `runtime.generationTimeoutSeconds` limit.
- Preserve caller cancellation as cancellation rather than reporting it as a
  provider timeout.
- Add deterministic fake-provider E2E coverage proving that the model test uses
  the configured timeout. Do not call a real model or restart the running app.

## Implementation steps

1. Pass the request cancellation token directly from `ModelDiagnosticService`
   to the provider stream and retain the provider's typed timeout message.
2. Add a one-shot model-test response delay to the fake Ollama boundary.
3. Add an E2E regression that delays the response beyond the former fixed
   15-second limit and verifies it completes within a larger configured timeout.

## Validation

- `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
- `dotnet build AgenticRouter.slnx -c Release --no-restore` with zero warnings.
- Focused and full fake-provider Playwright E2E coverage.
- `git diff --check` and intended-diff review.
