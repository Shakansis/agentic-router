# Portable Windows release

Agentic Router can be distributed as a self-contained, single-file Windows
executable plus the ASP.NET static content and configuration that must remain
external.

## Build the package

From the repository root:

```powershell
.\scripts\Publish-PortableRelease.ps1
```

The default command creates `0.9.14_alpha` for `win-x64` under
`artifacts\releases`:

- an unpacked directory for inspection;
- `AgenticRouter-0.9.14_alpha-win-x64.zip`;
- a SHA-256 checksum file beside the ZIP.

The first publish for a runtime identifier may download the corresponding .NET
runtime packs from NuGet. Later offline publishes can use `-NoRestore` after the
runtime-specific restore has succeeded at least once.

Optional parameters:

```powershell
.\scripts\Publish-PortableRelease.ps1 `
  -VersionLabel 0.9.14_alpha `
  -RuntimeIdentifier win-x64 `
  -OutputDirectory artifacts\releases
```

## Publish to GitHub

GitHub publishing is explicit and never occurs during an ordinary local build.
It requires the GitHub CLI authenticated with an account allowed to create and
write the target repository:

```powershell
gh auth login --web
```

The first publication can create the dedicated public repository:

```powershell
.\scripts\Publish-PortableRelease.ps1 `
  -PublishToGitHub `
  -CreateGitHubRepository
```

Later versions use the existing repository:

```powershell
.\scripts\Publish-PortableRelease.ps1 -PublishToGitHub
```

The default target is `Shakansis/agentic-router-releases`. It can be changed
with `-GitHubRepository OWNER/NAME`.

The public repository is restricted by an allowlist to:

- `README.md`;
- `LICENSE.md`;
- the ten reviewed manual images under `screenshots/` (numbered `01`, `02`,
  and `04` through `11`).

The public `README.md` is sourced from `distribution/README.md`; the source
repository's root README remains the developer-facing project document. To
update only the public manual and screenshots without replacing an existing
release or its immutable assets:

```powershell
.\scripts\Publish-GitHubRelease.ps1 `
  -VersionLabel 0.9.14_alpha `
  -RuntimeIdentifier win-x64 `
  -DocumentationOnly
```

The release receives only the generated ZIP and its SHA-256 file. Publishing
aborts if the repository contains any other tracked file, is not public, or
already has the requested version tag. Existing release assets are never
silently replaced.

## Package contract

The archive contains:

- `AgenticRouter.exe`, including the application and the .NET runtime;
- `wwwroot`, containing browser assets required by ASP.NET Core;
- `AgenticRouter.staticwebassets.endpoints.json`, the static-asset manifest;
- `appsettings.json`, the editable runtime configuration;
- `README.txt`, with first-run instructions and a link to the illustrated
  online manual;
- `LICENSE.txt`, containing the alpha evaluation terms.

It intentionally excludes development configuration, debug symbols, tests,
fake providers, diagnostics, secrets, and existing local application data.

The published executable resolves its content and `data` directories relative
to its own location. It therefore behaves consistently when started by double
click or from a different working directory.
