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

The final Windows ZIP passed **22/22** browser/API tests: all 15 tool-workspace
scenarios, the Chat presentation class, the closed canonical tool registry and
all three live execution-header scenarios.
Both portable archives passed content inspection and checksum verification.
The extracted Windows executable and the Linux executable under Ubuntu/WSL
served the page and settings API successfully. All Windows static assets match
the source; Linux executable/launcher permissions are 755. No running user
instance was replaced.

Installed-harness browser/API checks passed **32/32**, with zero failures/skips,
in 7.31 minutes, against the extracted Windows executable and simulated
providers. Versions: Codex `0.167.0-alpha.4.1`, Claude Code `2.1.295`, OpenCode
`1.18.35`, Qwen Code `0.25.0`. No CLI was upgraded or reinstalled.

The complete deterministic run exercised **930 scenarios** in 30.21 minutes:
**928 passed, two failed, zero skipped**. Both failures below were corrected
and their exact cases passed in the **22/22** final-package run. The TRX result
names were matched to verify that no failed case remains unresolved. Thus all
930 deterministic scenarios have passing evidence across the broad run and
the final targeted run; this is not a claim that the earlier broad invocation
was entirely green. Including the 32 installed-harness and one real local-model
case, the distinct passing matrix is **963 scenarios**. Package reruns overlap
that matrix and are not counted twice.

The first deterministic run was
stopped after detecting an obsolete
asset-revision assertion; the interface and assertion now consistently require
`20261111.1`. Cache revalidation assertions remain intact.
The broad rerun exposed a second outdated expectation: the closed registry test
omitted the new canonical `set_response_target`. Its expected set was updated;
exact names, aliases and collision checks remain required. It passed against
the final package with the corrected test assembly. This changes no production
behavior.
The mobile execution-header scenario also reproduced a real layout regression:
the assistant message's trailing margin constrained its sticky header in the
smaller shared-composer viewport. Removing that margin only while the header
is live restored its position. All three original desktop/mobile/terminal-state
cases passed without changing assertions or tolerances; screenshots were
inspected. Both packages were rebuilt after this correction.

## Frozen packages

Both final packages identify source commit
`e90a21bf08f89e160bd8d04e514bb7c219c323cd`, following feature commit `5c0c024`
and registry-test correction `9788315`.
Validation-record commits after this point do not change the packaged code.

| Runtime | Bytes | SHA-256 |
| --- | ---: | --- |
| Windows x64 ZIP | 123032786 | `2cb4a8e5b2523bd14bfaf933ada1b08f83f7decf82b73a0eed9ab968b784f262` |
| Linux x64 tar.gz | 141889320 | `a6f74941974cccec62629d1d9fdae4213d767b34664b85f60deae224013bb6d1` |

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
Earlier real OpenAI acceptance remains historical evidence.
Unsaved editor drafts remain window-local; checkpoints do not reconstruct them
after restart. Existing services, settings, model installations and user data
are preserved; this release does not activate a replacement running instance.
