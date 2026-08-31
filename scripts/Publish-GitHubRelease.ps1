[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [string]$VersionLabel,
  [string[]]$ArchivePath,
  [string[]]$ChecksumPath,
  [Parameter(Mandatory)]
  [ValidateSet('win-x64', 'linux-x64')]
  [string[]]$RuntimeIdentifier,
  [string]$Repository = 'Shakansis/agentic-router-releases',
  [switch]$CreateRepository,
  [switch]$PreflightOnly,
  [switch]$DocumentationOnly
)

$ErrorActionPreference = 'Stop'

function Assert-NativeSuccess {
  param(
    [Parameter(Mandatory)]
    [string]$Operation,
    [string[]]$Output
  )

  if ($LASTEXITCODE -ne 0) {
    $details = ($Output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine
    throw "$Operation failed with exit code $LASTEXITCODE.$([Environment]::NewLine)$details"
  }
}

function Invoke-GitHubCli {
  param(
    [Parameter(Mandatory)]
    [string]$Operation,
    [Parameter(Mandatory)]
    [string[]]$Arguments
  )

  $output = @(& $script:GitHubCliPath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
  Assert-NativeSuccess -Operation $Operation -Output $output
  return $output
}

if ($VersionLabel -notmatch '^\d+\.\d+\.\d+_[0-9A-Za-z][0-9A-Za-z.-]*$') {
  throw 'VersionLabel must use the form 0.9.19_alpha.'
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
  throw 'Repository must use the OWNER/NAME form.'
}
if (-not $PreflightOnly -and -not $DocumentationOnly) {
  if ($ArchivePath.Count -eq 0) {
    throw 'At least one release archive is required.'
  }
  if ($ArchivePath.Count -ne $ChecksumPath.Count) {
    throw 'Each release archive must have one matching checksum file.'
  }
  foreach ($path in @($ArchivePath) + @($ChecksumPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
      throw "Release asset was not found: $path"
    }
  }
}
$githubCliCommand = Get-Command gh -ErrorAction SilentlyContinue
if ($githubCliCommand) {
  $script:GitHubCliPath = $githubCliCommand.Source
}
else {
  $githubCliCandidates = @(
    'C:\Program Files\GitHub CLI\gh.exe',
    (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe')
  )
  $script:GitHubCliPath = $githubCliCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($script:GitHubCliPath)) {
  throw 'GitHub CLI (gh) is required. Install it and run: gh auth login --web'
}

Invoke-GitHubCli -Operation 'GitHub authentication check' -Arguments @(
  'auth',
  'status',
  '--active',
  '--hostname',
  'github.com'
) | Out-Null

$repositoryJson = @(& $script:GitHubCliPath repo view $Repository --json 'nameWithOwner,visibility,defaultBranchRef' 2>$null)
$repositoryExists = $LASTEXITCODE -eq 0
if (-not $repositoryExists) {
  if (-not $CreateRepository) {
    throw "GitHub repository $Repository does not exist or is not accessible. Re-run with -CreateGitHubRepository to create it as public."
  }
  if ($PreflightOnly) {
    [pscustomobject]@{
      Repository = $Repository
      RepositoryExists = $false
      Tag = "v$VersionLabel"
    }
    return
  }

  Invoke-GitHubCli -Operation 'Public release repository creation' -Arguments @(
    'repo',
    'create',
    $Repository,
    '--public',
    '--add-readme',
    '--disable-issues',
    '--disable-wiki',
    '--description',
    'Official portable releases for Agentic Router.'
  ) | Out-Null
  $repositoryJson = Invoke-GitHubCli -Operation 'Created repository lookup' -Arguments @(
    'repo',
    'view',
    $Repository,
    '--json',
    'nameWithOwner,visibility,defaultBranchRef'
  )
}

$repositoryState = ($repositoryJson -join [Environment]::NewLine) | ConvertFrom-Json
if ($repositoryState.visibility -ne 'PUBLIC') {
  throw "Refusing to publish binaries because $Repository is not public."
}
$defaultBranch = $repositoryState.defaultBranchRef.name
if ([string]::IsNullOrWhiteSpace($defaultBranch)) {
  throw "GitHub repository $Repository does not have a default branch."
}

$tag = "v$VersionLabel"
if (-not $DocumentationOnly) {
  & $script:GitHubCliPath release view $tag --repo $Repository *> $null
  if ($LASTEXITCODE -eq 0) {
    throw "GitHub release $tag already exists in $Repository. Use a new version label; published release assets are immutable by this script."
  }
}
if ($PreflightOnly) {
  [pscustomobject]@{
    Repository = $Repository
    RepositoryExists = $true
    Tag = $tag
  }
  return
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publicFiles = @{
  'LICENSE.md' = Join-Path $repositoryRoot 'LICENSE.md'
  'README.md' = Join-Path $repositoryRoot 'distribution\README.md'
}
foreach ($screenshotName in @(
  '01-first-run-setup.png',
  '02-local-resources-ready.png',
  '04-initialize-git.png',
  '05-git-ready.png',
  '06-chat-with-images.png',
  '07-chat-response.png',
  '08-execute-result.png',
  '09-review-files.png',
  '10-generated-website.png',
  '11-view-folder.png'
)) {
  $publicFiles["screenshots\$screenshotName"] = Join-Path (
    $repositoryRoot
  ) "distribution\screenshots\$screenshotName"
}
$obsoletePublicFiles = @(
  'screenshots\main-interface.png',
  'screenshots\settings-dialog.png'
)
foreach ($sourcePath in $publicFiles.Values) {
  if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Required public repository file was not found: $sourcePath"
  }
}

$temporaryRoot = Join-Path (
  [System.IO.Path]::GetTempPath()
) "agentic-router-public-release-$PID-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
  Invoke-GitHubCli -Operation 'Public release repository clone' -Arguments @(
    'repo',
    'clone',
    $Repository,
    $temporaryRoot,
    '--',
    '--depth',
    '1'
  ) | Out-Null

  $allowedRelativePaths = @($publicFiles.Keys) + $obsoletePublicFiles
  $unexpectedFiles = Get-ChildItem -LiteralPath $temporaryRoot -Recurse -File -Force |
    Where-Object {
      -not $_.FullName.StartsWith(
        (Join-Path $temporaryRoot '.git') + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase
      )
    } |
    Where-Object {
      $relativePath = [System.IO.Path]::GetRelativePath($temporaryRoot, $_.FullName)
      $relativePath -notin $allowedRelativePaths
    }
  if ($unexpectedFiles) {
    $unexpectedList = ($unexpectedFiles.FullName -join [Environment]::NewLine)
    throw "Public release repository contains files outside the allowlist:$([Environment]::NewLine)$unexpectedList"
  }

  foreach ($obsoleteRelativePath in $obsoletePublicFiles) {
    $obsoletePath = Join-Path $temporaryRoot $obsoleteRelativePath
    if (Test-Path -LiteralPath $obsoletePath -PathType Leaf) {
      Remove-Item -LiteralPath $obsoletePath -Force
    }
  }

  foreach ($entry in $publicFiles.GetEnumerator()) {
    $destinationPath = Join-Path $temporaryRoot $entry.Key
    $destinationDirectory = Split-Path -Parent $destinationPath
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $entry.Value -Destination $destinationPath -Force
  }

  $authenticatedUserOutput = @(
    Invoke-GitHubCli -Operation 'Authenticated GitHub user lookup' -Arguments @(
      'api',
      'user',
      '--jq',
      '.login'
    )
  )
  $authenticatedUser = ($authenticatedUserOutput -join [Environment]::NewLine).Trim()
  if ([string]::IsNullOrWhiteSpace($authenticatedUser)) {
    throw 'GitHub CLI did not return the authenticated user.'
  }

  & git -C $temporaryRoot config user.name $authenticatedUser
  Assert-NativeSuccess -Operation 'Public repository Git author configuration'
  & git -C $temporaryRoot config user.email "$authenticatedUser@users.noreply.github.com"
  Assert-NativeSuccess -Operation 'Public repository Git email configuration'
  & git -C $temporaryRoot add -A -- 'LICENSE.md' 'README.md' 'screenshots'
  Assert-NativeSuccess -Operation 'Public repository staging'
  & git -C $temporaryRoot diff --cached --quiet
  $hasPublicChanges = $LASTEXITCODE -eq 1
  if ($LASTEXITCODE -notin @(0, 1)) {
    throw "Public repository diff failed with exit code $LASTEXITCODE."
  }
  if ($hasPublicChanges) {
    & git -C $temporaryRoot commit -m "docs: update public release content for $VersionLabel"
    Assert-NativeSuccess -Operation 'Public repository commit'
    & git -C $temporaryRoot push origin $defaultBranch
    Assert-NativeSuccess -Operation 'Public repository push'
  }

  if ($DocumentationOnly) {
    [pscustomobject]@{
      Repository = $Repository
      Tag = $tag
      Url = "https://github.com/$Repository"
    }
    return
  }

  $releaseNotesPath = Join-Path $temporaryRoot 'release-notes.md'
  $windowsRelease = $RuntimeIdentifier -contains 'win-x64'
  $linuxRelease = $RuntimeIdentifier -contains 'linux-x64'
  $releaseNotes = if ($windowsRelease -and $linuxRelease) {
    @"
# Agentic Router $VersionLabel

Portable Windows x64 and Linux x64 release.

## Install

- Windows x64: download the `win-x64.zip` archive, verify it with the matching
  `.sha256` file, extract it, and run `AgenticRouter.exe`.
- Linux x64: download the `linux-x64.tar.gz` archive, verify it with the matching
  `.sha256` file, extract it, and run `./run-agentic-router.sh`. Apply
  `chmod +x AgenticRouter run-agentic-router.sh` if required.

Ollama and models are installed separately. Linux AMD setup offers an explicit
Vulkan or ROCm profile and never installs GPU drivers automatically. Linux ARM64,
Windows ARM64, and macOS are not included in this release. This is alpha software
and is not recommended for unattended or production use.
"@
  }
  elseif ($linuxRelease) {
    @"
# Agentic Router $VersionLabel

Portable Linux x64 release.

## Install

1. Download the `.tar.gz` archive attached to this release.
2. Verify it with the matching `.sha256` file.
3. Extract it to a writable directory.
4. Run `chmod +x AgenticRouter run-agentic-router.sh` if required.
5. Start `./run-agentic-router.sh` and open the local address displayed.

Ollama and models are installed separately. AMD setup offers an explicit Vulkan
or ROCm profile and never installs GPU drivers automatically. This is an alpha
release and is not yet recommended for unattended or production use.
"@
  }
  else {
    @"
# Agentic Router $VersionLabel

Portable Windows release for $RuntimeIdentifier.

## Install

1. Download the ZIP attached to this release.
2. Extract it to a writable directory.
3. Run AgenticRouter.exe.
4. Open the local address displayed in the console.

The SHA-256 checksum is attached beside the ZIP. This is an alpha release and
is not yet recommended for unattended or production use. Use is governed by
the Agentic Router Alpha Evaluation License included in the package and public
release repository.
"@
  }
  [System.IO.File]::WriteAllText(
    $releaseNotesPath,
    $releaseNotes,
    [System.Text.UTF8Encoding]::new($false)
  )

  $assetPaths = @($ArchivePath) + @($ChecksumPath) |
    ForEach-Object { [System.IO.Path]::GetFullPath($_) }
  if (($assetPaths | Select-Object -Unique).Count -ne $assetPaths.Count) {
    throw 'Release asset paths must be unique.'
  }

  $releaseArguments = @(
    'release',
    'create',
    $tag
  ) + $assetPaths + @(
    '--repo',
    $Repository,
    '--target',
    $defaultBranch,
    '--title',
    "Agentic Router $VersionLabel",
    '--notes-file',
    $releaseNotesPath,
    '--prerelease',
    '--latest=false'
  )
  Invoke-GitHubCli -Operation 'GitHub release creation' -Arguments $releaseArguments | Out-Null

  $releaseUrlOutput = @(
    Invoke-GitHubCli -Operation 'Published release lookup' -Arguments @(
      'release',
      'view',
      $tag,
      '--repo',
      $Repository,
      '--json',
      'url',
      '--jq',
      '.url'
    )
  )
  $releaseUrl = ($releaseUrlOutput -join [Environment]::NewLine).Trim()

  [pscustomobject]@{
    Repository = $Repository
    Tag = $tag
    Url = $releaseUrl
  }
}
finally {
  if (Test-Path -LiteralPath $temporaryRoot) {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
  }
}
