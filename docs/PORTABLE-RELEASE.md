# Portable Windows and Linux x64 releases

Agentic Router can be distributed as a self-contained, single-file executable
plus the ASP.NET static content and configuration that must remain external.
The supported release targets are Windows x64, Windows ARM64, and Linux x64.
Linux ARM64 and macOS are not release targets in this version.

## Build the package

From the repository root:

```powershell
.\scripts\Publish-PortableRelease.ps1
```

The default command creates `0.9.15_alpha` for `win-x64` under
`artifacts\releases`:

- an unpacked directory for inspection;
- `AgenticRouter-0.9.15_alpha-win-x64.zip`;
- a SHA-256 checksum file beside the ZIP.

To create the Linux x64 package locally:

```powershell
.\scripts\Publish-PortableRelease.ps1 `
  -VersionLabel 0.9.15_alpha `
  -RuntimeIdentifier linux-x64 `
  -OutputDirectory artifacts\releases
```

This creates an unpacked inspection directory,
`AgenticRouter-0.9.15_alpha-linux-x64.tar.gz`, and its matching `.sha256` file.

The first publish for a runtime identifier may download the corresponding .NET
runtime packs from NuGet. Later offline publishes can use `-NoRestore` after the
runtime-specific restore has succeeded at least once.

Optional parameters:

```powershell
.\scripts\Publish-PortableRelease.ps1 `
  -VersionLabel 0.9.15_alpha `
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
  -VersionLabel 0.9.15_alpha `
  -RuntimeIdentifier win-x64 `
  -DocumentationOnly
```

The release receives only the generated ZIP or tar.gz and its SHA-256 file. Publishing
aborts if the repository contains any other tracked file, is not public, or
already has the requested version tag. Existing release assets are never
silently replaced.

## Windows package contract

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

## Linux x64 package contract

The tar.gz contains:

- `AgenticRouter`, including the application and the .NET runtime;
- `run-agentic-router.sh`, the terminal launcher;
- `wwwroot` and the ASP.NET static-asset manifest;
- `appsettings.json`, `README.txt`, and `LICENSE.txt`;
- the reviewed guided Ollama install and profile-change scripts under `scripts/`.

After extraction, run:

```bash
chmod +x AgenticRouter run-agentic-router.sh
./run-agentic-router.sh
```

Linux setup does not install GPU drivers or models automatically. For AMD
hardware, the first-run surface requires an explicit choice:

- **Vulkan** installs the official base package and configures
  `OLLAMA_VULKAN=1`. Ollama currently documents this backend as experimental.
- **ROCm** installs the official base package plus the official ROCm
  supplemental package and requires compatible ROCm v7 driver support.

Requested profile, saved package manifest, observed runtime backend, and CPU
fallback are reported separately. Changing profiles requires a reviewed,
expiring Host plan plus confirmation in the visible terminal. The switch
reinstalls the official base package, removes only manifest-proven ROCm-only
files, and preserves model storage and application data.

Cloud API-key storage on Linux requires `secret-tool` from `libsecret-tools`
and an active user keyring. The native folder picker uses `zenity` or `kdialog`
when available; manual path entry remains available. `xdg-utils` supplies the
folder-opening integration.
