[CmdletBinding()]
param(
  [string]$VersionLabel = '0.9.15_alpha',
  [ValidateSet('win-x64', 'win-arm64')]
  [string]$RuntimeIdentifier = 'win-x64',
  [string]$OutputDirectory,
  [switch]$NoRestore,
  [switch]$PublishToGitHub,
  [string]$GitHubRepository = 'Shakansis/agentic-router-releases',
  [switch]$CreateGitHubRepository
)

$ErrorActionPreference = 'Stop'

function Assert-SuccessfulExitCode {
  param(
    [Parameter(Mandatory)]
    [string]$Operation
  )

  if ($LASTEXITCODE -ne 0) {
    throw "$Operation failed with exit code $LASTEXITCODE."
  }
}

function Assert-ChildPath {
  param(
    [Parameter(Mandatory)]
    [string]$ChildPath,
    [Parameter(Mandatory)]
    [string]$ParentPath
  )

  $normalizedParent = [System.IO.Path]::GetFullPath($ParentPath).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
  ) + [System.IO.Path]::DirectorySeparatorChar
  $normalizedChild = [System.IO.Path]::GetFullPath($ChildPath)
  if (-not $normalizedChild.StartsWith(
    $normalizedParent,
    [System.StringComparison]::OrdinalIgnoreCase
  )) {
    throw "Refusing to modify a path outside the release output directory: $normalizedChild"
  }
}

if ($VersionLabel -notmatch '^\d+\.\d+\.\d+_[0-9A-Za-z][0-9A-Za-z.-]*$') {
  throw 'VersionLabel must use the form 0.9.15_alpha.'
}

$packageVersion = $VersionLabel.Replace('_', '-')
$baseVersion = $packageVersion.Split('-')[0]
$assemblyVersion = "$baseVersion.0"
$repositoryRoot = [System.IO.Path]::GetFullPath(
  (Join-Path $PSScriptRoot '..')
)
$projectPath = Join-Path $repositoryRoot 'AgenticRouter.Api\AgenticRouter.Api.csproj'
$artifactValidator = Join-Path $repositoryRoot 'tools\diagnostics\Test-PublishArtifact.ps1'
$githubPublisher = Join-Path $repositoryRoot 'scripts\Publish-GitHubRelease.ps1'

if ($CreateGitHubRepository -and -not $PublishToGitHub) {
  throw '-CreateGitHubRepository requires -PublishToGitHub.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $releaseRoot = Join-Path $repositoryRoot 'artifacts\releases'
}
elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  $releaseRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
  $releaseRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot $OutputDirectory)
  )
}

$packageName = "AgenticRouter-$VersionLabel-$RuntimeIdentifier"
$packageDirectory = Join-Path $releaseRoot $packageName
$archivePath = Join-Path $releaseRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"
$stagingDirectory = Join-Path $releaseRoot ".staging-$packageName-$PID"

Assert-ChildPath -ChildPath $packageDirectory -ParentPath $releaseRoot
Assert-ChildPath -ChildPath $archivePath -ParentPath $releaseRoot
Assert-ChildPath -ChildPath $checksumPath -ParentPath $releaseRoot
Assert-ChildPath -ChildPath $stagingDirectory -ParentPath $releaseRoot

if ($PublishToGitHub) {
  $preflightArguments = @{
    VersionLabel = $VersionLabel
    RuntimeIdentifier = $RuntimeIdentifier
    Repository = $GitHubRepository
    PreflightOnly = $true
  }
  if ($CreateGitHubRepository) {
    $preflightArguments.CreateRepository = $true
  }
  & $githubPublisher @preflightArguments | Out-Null
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $stagingDirectory) {
  Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
  if (-not $NoRestore) {
    & dotnet restore $projectPath -r $RuntimeIdentifier
    Assert-SuccessfulExitCode -Operation 'Runtime restore'
  }

  $publishArguments = @(
    'publish',
    $projectPath,
    '-c',
    'Release',
    '-r',
    $RuntimeIdentifier,
    '--self-contained',
    'true',
    '--no-restore',
    '-o',
    $stagingDirectory,
    '-p:AssemblyName=AgenticRouter',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:Version=$packageVersion",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:InformationalVersion=$VersionLabel"
  )
  & dotnet @publishArguments
  Assert-SuccessfulExitCode -Operation 'Portable publish'

  foreach ($unneededFile in @('appsettings.Development.json', 'web.config')) {
    $unneededPath = Join-Path $stagingDirectory $unneededFile
    if (Test-Path -LiteralPath $unneededPath) {
      Remove-Item -LiteralPath $unneededPath -Force
    }
  }
  Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE.md') `
    -Destination (Join-Path $stagingDirectory 'LICENSE.txt')

  $requiredPaths = @(
    (Join-Path $stagingDirectory 'AgenticRouter.exe'),
    (Join-Path $stagingDirectory 'AgenticRouter.staticwebassets.endpoints.json'),
    (Join-Path $stagingDirectory 'appsettings.json'),
    (Join-Path $stagingDirectory 'LICENSE.txt'),
    (Join-Path $stagingDirectory 'wwwroot\index.html')
  )
  foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
      throw "Required portable artifact is missing: $requiredPath"
    }
  }

  $readme = @"
Agentic Router $VersionLabel ($RuntimeIdentifier)

1. Double-click AgenticRouter.exe.
2. Wait for the console to show "Now listening on".
3. Open the displayed local address in your browser (normally http://localhost:5000).

The package includes the .NET runtime. Ollama, models, and optional harnesses can
be installed from Agentic Router's onboarding or Settings > Local resources.

Full illustrated guide:
https://github.com/Shakansis/agentic-router-releases#readme

Application data is stored in the data directory beside the executable. Keep that
directory when upgrading if you want to preserve local settings and history.

To stop Agentic Router, close its console window or press Ctrl+C in that window.
"@
  [System.IO.File]::WriteAllText(
    (Join-Path $stagingDirectory 'README.txt'),
    $readme,
    [System.Text.UTF8Encoding]::new($false)
  )
  & $artifactValidator -PublishPath $stagingDirectory
  Assert-SuccessfulExitCode -Operation 'Publish artifact verification'

  if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
  }
  Move-Item -LiteralPath $stagingDirectory -Destination $packageDirectory

  if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
  }
  $archiveItems = Get-ChildItem -LiteralPath $packageDirectory -Force |
    Select-Object -ExpandProperty FullName
  Compress-Archive -LiteralPath $archiveItems -DestinationPath $archivePath -CompressionLevel Optimal

  $checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
  [System.IO.File]::WriteAllText(
    $checksumPath,
    "$checksum  $([System.IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)",
    [System.Text.UTF8Encoding]::new($false)
  )

  $githubRelease = $null
  if ($PublishToGitHub) {
    $githubArguments = @{
      VersionLabel = $VersionLabel
      ArchivePath = $archivePath
      ChecksumPath = $checksumPath
      RuntimeIdentifier = $RuntimeIdentifier
      Repository = $GitHubRepository
    }
    if ($CreateGitHubRepository) {
      $githubArguments.CreateRepository = $true
    }
    $githubRelease = & $githubPublisher @githubArguments
  }

  [pscustomobject]@{
    Version = $VersionLabel
    Runtime = $RuntimeIdentifier
    PackageDirectory = $packageDirectory
    Zip = $archivePath
    Sha256 = $checksum
    SizeBytes = (Get-Item -LiteralPath $archivePath).Length
    GitHubRepository = $githubRelease.Repository
    GitHubRelease = $githubRelease.Url
  }
}
finally {
  if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
  }
}
