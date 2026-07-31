# Maintainer diagnostics

`Run-AgenticRouterDiagnostics.ps1` is a tracked maintainer/support tool. It is
outside the application project and is never part of the published product.

Default execution is read-only with respect to repository source, application
data, Git, providers, models, GPUs, and user workspaces. It writes sanitized
reports only under `tools/diagnostics/output/`, which is ignored by Git.

```powershell
.\tools\diagnostics\Run-AgenticRouterDiagnostics.ps1
```

Build, deterministic E2E, publish verification, and support-package creation
are opt-in switches. The script accepts no arbitrary command text and never
calls a model or cloud provider.

```powershell
.\tools\diagnostics\Run-AgenticRouterDiagnostics.ps1 `
  -RunBuild -RunTests -VerifyPublish -CreateSupportPackage
```

The support package contains only `diagnostics.json` and `diagnostics.md`.
Application data, conversation content, secrets, prompts, responses, images,
tool arguments, and full personal paths are excluded.

`Test-PublishArtifact.ps1` fails when a publish directory contains diagnostics,
PowerShell scripts, test/fake-provider assets, JSONL usage, secrets, or local
settings/workspace/model-organization data.
