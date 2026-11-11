# Agentic Router 0.17.0 alpha release validation

Date: 2026-11-11. This release consolidates the shared-composer tool workspace
and the previously retained OpenAI/provider, local-model cleanup and runtime UI
changes. Agent Browser and the new conversation dropdown follow this release.

## Scope

- [Tool workspace contract](../tool-workspace.md) and
  [focused browser/API evidence](tool-workspace-20261111.md).
- [OpenAI provider contract](../openai-provider.md), including background response
  recovery, hosted search, protected settings and provider capability mapping.
- [Model library](../model-library.md) and
  [runtime performance](../model-runtime-performance.md): authoritative cleanup
  of removed local models and separately identified throughput estimates.
- Explicit cloud Execute selection follows the documented Direct path for Auto
  and Supervisor. Autonomous retains its local supervised-route requirement.

## Release gates

The solution Release build passed with **zero warnings and zero errors**, using
`-p:BaseOutputPath=bin/tool-workspace/` to preserve the user's running output.
JavaScript syntax checks passed for the changed browser scripts. C# whitespace
formatting uses folder mode because sandboxed MSBuild named pipes are unavailable.

Local-model shared-composer acceptance passed **1/1** with the installed
`qwen3.8:27b-q4_K_M`, in 1m12s: the actual model proposed a revision, the draft
remained unchanged until Apply, and Undo restored the source. The screenshot
was inspected. No cloud provider was used.

The complete deterministic suite and final portable-package checks are in
progress. Publication must wait for their results. The first deterministic
run was stopped after detecting an obsolete
asset-revision assertion; the interface and assertion now consistently require
`20261111.1`. Cache revalidation assertions remain intact.

## Reproduction and evidence

Local logs, runner and TRX files are under `.artifacts/release-0.17.0-alpha/`.
The runner sets `AGENTIC_ROUTER_REPOSITORY_ROOT`, `AGENTIC_ROUTER_E2E_API_PATH`
and all five `AGENTIC_ROUTER_E2E_FAKE_*_PATH` values to the matching isolated
build. It uses `playwright.runsettings` with three class-level workers and Edge
headless. The deterministic filter excludes the explicit real-model/provider
classes and installed-harness opt-in class. Providers and harnesses are faked
only at their external boundaries.

```powershell
dotnet build AgenticRouter.slnx -c Release --no-restore -m:1 -nr:false -p:BaseOutputPath=bin/tool-workspace/
dotnet format whitespace . --folder --verify-no-changes --include <changed C# files>
node --check AgenticRouter.Api/wwwroot/tool-workspace.js
git diff --check
./scripts/Publish-PortableRelease.ps1 -VersionLabel 0.17.0_alpha -RuntimeIdentifier win-x64
./scripts/Publish-PortableRelease.ps1 -VersionLabel 0.17.0_alpha -RuntimeIdentifier linux-x64
```

## Validation boundaries

The user explicitly authorized local-model validation during this release and
asked to avoid paid models. No paid-provider inference is part of this run.
Earlier real OpenAI and installed-CLI records remain historical evidence.
Unsaved editor drafts remain window-local; checkpoints do not reconstruct them
after restart. Existing services, settings, model installations and user data
are preserved; this release does not activate a replacement running instance.
