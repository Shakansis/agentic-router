# Role preferences above the provider ceiling — 2026-10-19

The previous correction allowed profiles above the model maximum but retained a second save-time gate against `Context.ProviderContextTokens`. This produced the reported SAVE FAILED errors for specialist, primary, webSearchSynthesis and visionRequest.

`SettingsValidator.cs` now validates role and exact-model profile numeric ordering independently of the provider ceiling. Minimum >= 1024, minimum <= target <= maximum, output reservation and keep-alive validation remain. Ordering errors identify the received values. Runtime resolution still caps the effective window to the configured provider ceiling and model capability; no user setting is silently increased.

`wwwroot/conversation-render.js` identifies a reduction caused by the configured provider ceiling. `wwwroot/index.html`, `wwwroot/settings-runtime.js` and `docs/model-runtime-performance.md` now explain that the ceilings apply at runtime without blocking profile saving. `LargeContextEndToEndTests.cs` adds two browser/API cases for the four reported roles and an exact-model override, including JSON/YAML persistence, unchanged global settings, actual fake-provider request context, visible notice, and atomic rejection of unordered values.

Validation:

- `dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/rpg-editor -p:UseSharedCompilation=false --disable-build-servers`: passed, zero warnings/errors.
- `dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include AgenticRouter.Api/Configuration/SettingsValidator.cs tests/AgenticRouter.EndToEndTests/LargeContextEndToEndTests.cs`: passed.
- `node --check` for `settings-runtime.js` and `conversation-render.js`: passed.
- `git diff --check`: passed; existing Git newline notices only. Intended changes reviewed without reverting unrelated work.
- `.tmp/Test-RpgEditor.ps1 -Filter 'FullyQualifiedName~AgenticRouter.EndToEndTests.LargeContextEndToEndTests|FullyQualifiedName~ContextUsagePopoverPrioritizesSummaryAndCollapsesAdvancedDetails|FullyQualifiedName~OllamaRuntimeProfilesResolveAnalyzeMeasurePersistAndRestore|FullyQualifiedName~SavesRoutingSettingsWithoutLoadingLegacyResidentModels|FullyQualifiedName~PortableYamlCanBeDownloadedAndAppliedFromAdvancedSettings' -LogName 'provider-ceiling-save-20261019.trx'`: **9 passed, 0 failed, 0 skipped**, 35 seconds.

Evidence: `.artifacts/rpg-editor/TestResults/provider-ceiling-save-20261019.trx`; `.artifacts/rpg-editor/visual-review/provider-context-ceiling-False.png` (visually inspected) and `provider-context-ceiling-True.png`. Tests saved requested contexts of 262,144 and 524,288 with the original 40,960 provider ceiling unchanged, then observed 40,960 in the fake Ollama request and the matching Chat warning.

These tests use a real browser and Host API with Ollama simulated at the external boundary. No real-model inference or allocation was performed. No user services were restarted. Activation requires the user's normal AR rebuild/restart. Final process inspection found no testhost, isolated AR, fake CLI, headless browser or dotnet server remaining. Preserved AR PID 16756 (normal Release path, parent 16340; ports 5294/7155/7156), Ollama PID 24372 (11434) and AR-managed Ollama PID 27244 (parent 16756).
