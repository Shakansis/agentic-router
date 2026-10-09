# Model runtime performance

The runtime settings editor retains all existing context, role override, memory,
analysis and measurement controls. Presentation follows the existing AR theme.
Help buttons use a manually controlled native popover above the Settings dialog,
escaping sidebar layering and scroll-container clipping. Hover and keyboard focus
open help; Escape, scroll, dialog close and resize dismiss it.

## Configuration

Only draft tokens and input batch size are added. The typed performance object is
attached to the existing exact provider/model/digest identity, alongside role
overrides, and applies across roles. Sampling profiles, GPU placement, memory
policy, context limits, approval and recovery semantics are unchanged.

- Missing performance settings preserve the old request behavior.
- Draft tokens: null means Auto (omit); 0 means Off; positive integers are explicit.
- Batch size: null means Auto (omit); positive integers are explicit.
- Field edits remain pending per exact model, including when navigating models or
  sections. Only the modal footer's Save changes persists them, together with the
  other settings. There is no separate performance apply button.
- Save validates pending Custom values across all edited models before sending
  settings. Invalid input does not partially persist the modal. Cancel discards
  pending performance edits.
- Removing a context role override preserves model performance configuration.
- JSON persistence and portable YAML carry the optional performance object.
  YAML uses performance.draft_tokens and performance.batch_size with auto or an
  integer. Measured hardware records remain local.

## Provider capability and application evidence

The neutral capability contract declares conceptual identifiers, transport
mechanism, scope, support, provenance and explanation. The Ollama adapter maps
draftTokens to options.draft_num_predict and batchSize to options.num_batch
for native streaming, non-streaming, tools and model preload requests. Both are
load options in the [Ollama API contract](https://github.com/ollama/ollama/blob/main/api/types.go).

Positive nextn_predict_layers metadata confirms that the model declares draft
layers, but not runtime activation. Missing metadata is unknown, never proof that
the model lacks support. Non-generative model metadata disables this LLM editor.
Ollama accepts num_batch as a runner load option; /api/ps does not expose its
effective value. Unknown support permits configuration and clearly warns that
the runtime may reject or ignore it. Option-specific provider errors retain their
cause and surface runtime-performance-rejected without rewriting saved settings.

Native activity reports explicit values sent and Auto omitted. It does not claim
that accepted options became effective runner settings. Compatibility endpoints
used by external harnesses do not have a reviewed mapping for these options;
configured values produce an explicit runtime.performance-unavailable activity
warning on that route. No Modelfile mutation or compatibility-request rewriting
is introduced to compensate.

## Measurement identity

Measurements record configured performance beside the observed memory/context
facts. Their runtime signature includes explicit performance values, and changing
those values makes prior measurements stale. All-Auto preserves the legacy
signature. Measurement preload sends explicit options even when context already
matches. Actual activation remains unverified when the provider supplies no
independent evidence.

The benchmark configuration fingerprint includes exact model identity and its
configured performance values. History's existing partial-comparability assessment
therefore identifies different configurations without hiding prior results.

## Validation boundary

Browser/API E2E tests fake Ollama only at the provider boundary. Real model
generation, GPU performance acceptance, and cloud-provider calls require separate
explicit authorization under the existing testing policy.

## Changed components

- Configuration: ApplicationSettings.cs, SettingsValidator.cs and
  PortableYamlSettingsService.cs.
- Provider contract and mapping: ModelProviders.cs, IOllamaClient.cs and
  OllamaClient.cs.
- Runtime resolution and evidence: ModelRuntimePerformance.cs,
  OllamaRuntimeProfileContracts.cs, OllamaRuntimeProfileResolver.cs and
  OllamaRuntimeProfileService.cs.
- Activity and benchmark identity: ChatStreamService.cs and BenchmarkEngine.cs.
- Settings presentation: wwwroot/index.html, app.js, settings-runtime.js,
  model-runtime.css and benchmark.css (runtime rules moved to their own stylesheet).
- E2E coverage: RuntimePerformanceEndToEndTests.cs, FakeOllamaServer.cs,
  TestSettings.cs and ExecuteCoreEndToEndTests.cs (tooltip assertion now checks
  the actual native popover while preserving the above-input/menu-clearance check).

## Verification on this checkout

- Release build: `dotnet build AgenticRouter.slnx -c Release --no-restore
  -p:OutDir=<isolated output under tests/bin>`; zero warnings and errors.
  Isolated output avoids the executable held by the running user-owned app.
- Formatting: `dotnet format AgenticRouter.slnx --verify-no-changes --no-restore`.
- JavaScript: `node --check` for app.js and settings-runtime.js.
- Browser/API: `dotnet test` with Release, --no-build, --no-restore, the same OutDir
  and explicit fake-provider executable paths. Six new tests and nine relevant
  existing tests passed across the final runs. Coverage includes JSON/YAML/restart,
  Off and Auto omission, validation atomicity, exact digest, capabilities/rejection,
  native structured and tool requests, external-harness unavailability, measurement
  staleness, benchmark comparison, desktop/mobile geometry, keyboard and menu layering.
- Existing unrelated failure: SettingsUseViewportNavigationDirtyProtectionAndResponsiveFocus
  expects an error toast on invalid save. It fails at ExecuteCoreEndToEndTests.cs:1492
  both with the changed UI and with a separate copy of the pre-change UI served via
  ASPNETCORE_WEBROOT. Field errors are present; the error toast is absent.
  No assertion was weakened and the existing save behavior was preserved.
- No real model generation, GPU workload or cloud-provider validation was performed.

## Footer save follow-up

Removed the separate performance apply button. The changed files are
wwwroot/index.html, app.js, settings-runtime.js,
RuntimePerformanceEndToEndTests.cs and this document.

Performance edits now join the modal's existing Save changes submission.
Per-model pending values survive model/section navigation. Invalid Custom
values in another model block the whole save and reveal the offending field;
Cancel discards pending edits without writing settings.

Verification: isolated Release build with zero warnings/errors, format
--verify-no-changes, node --check for both changed scripts and git diff --check
passed. Thirteen fake-provider browser/API E2E tests passed (eight runtime
performance tests and five existing settings/runtime/inference/YAML tests).
No real-model, GPU or cloud-provider workload was used.
