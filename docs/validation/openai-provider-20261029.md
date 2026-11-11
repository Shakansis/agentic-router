# OpenAI provider validation - 2026-10-29

This records the initial integration. Subsequent real-provider acceptance,
hosted web search, reasoning corrections and the stale-status warning fix are
recorded in [the follow-up investigation](openai-web-search-20261029.md).

The implementation adds an optional OpenAI provider through the existing cloud
registry and protected-key workflow. It uses Responses for text, image input,
structured output, and Host function calls. The menu exposes organization,
project, service tier, verbosity, and output limit. Inference profiles retain
reasoning and sampling controls. See [provider contract](../openai-provider.md).

## Results

- Release solution build: passed, zero warnings and errors.
- Focused browser/API regression: **34 passed, 0 failed, 0 skipped**, 1m 6s.
- Formatting and intended diff checked; test output is under
  `.artifacts/openai-provider/results/openai-regression.trx`.
- New OpenAI coverage exercises key entry/removal, protected storage, menu
  settings/restart, grouped discovery, unsupported-model exclusion, reasoning,
  non-reasoning sampling, maximum effort, image input, provider token counts,
  interrupted/incomplete streams, invalid settings, sanitized authentication
  errors, tool conformance, and a real Host read in the temporary workspace.
- The external fake rejects incompatible endpoint fields and missing encrypted
  reasoning continuation. History-enabled acceptance verifies that the opaque
  continuation does not appear in JSON persistence or API logs.
- Existing cloud key, streaming/tool, inference, health, fallback, image privacy,
  settings UI, and Native multi-tool E2Es passed in the same regression.

No real OpenAI credential, inference, billing, or model access was tested. The
complete repository suite was not run. Structured-output support is implemented
against the official contract but was not independently exercised by this
focused regression. No native external harness was configured to use OpenAI.

## Commands

```powershell
dotnet build AgenticRouter.slnx -c Release --artifacts-path .artifacts/openai-provider --no-restore -m:1 -nr:false
dotnet format AgenticRouter.slnx --no-restore --verify-no-changes --include <changed C# files>
git diff --check
```

The isolated build initially required restoring its assets; the first build
without `--no-restore` restored the existing project dependencies. No dependency
was added. E2E ran with `playwright.runsettings`, Release, `--no-build`,
`--no-restore`, and `--artifacts-path .artifacts/openai-provider`. Environment
variables `AGENTIC_ROUTER_E2E_API_PATH` and all five `AGENTIC_ROUTER_E2E_FAKE_*_PATH`
variables pointed to matching isolated apphosts. The exact local invocation and
filter are retained in `.artifacts/openai-provider/validate.ps1`.

## Changed files

- `AgenticRouter.Api/Configuration/ApplicationSettings.cs`
- `AgenticRouter.Api/Configuration/SettingsValidator.cs`
- `AgenticRouter.Api/Execution/SpecialistToolingProfiles.cs`
- `AgenticRouter.Api/Program.cs`
- `AgenticRouter.Api/Providers/Cloud/CloudProviderRegistry.cs`
- `AgenticRouter.Api/Providers/Cloud/OpenAiCloudProvider.cs`
- `AgenticRouter.Api/Providers/Cloud/OpenAiCompatibleCloudProvider.cs`
- `AgenticRouter.Api/Providers/ModelProviders.cs`
- `AgenticRouter.Api/Providers/Ollama/IOllamaClient.cs`
- `AgenticRouter.Api/Usage/ProviderUsageMapper.cs`
- `AgenticRouter.Api/wwwroot/settings-runtime.js`
- `tests/AgenticRouter.EndToEndTests/OpenAiEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/FakeCloudProviderServer.cs`
- `tests/AgenticRouter.EndToEndTests/TestEnvironment.cs`
- `tests/AgenticRouter.EndToEndTests/ProviderAndUiEndToEndTests.cs`
- `tests/AgenticRouter.EndToEndTests/ExecuteCoreEndToEndTests.cs`
- `README.md`, `docs/openai-provider.md`, and this validation record.

All validation Hosts exited. The four API processes present before this work
(PIDs 37964, 6836, 38392, 13732) were preserved. The currently running application
still needs to be rebuilt/restarted to load this source change; this work does
not claim that an already-running binary has the new menu.
