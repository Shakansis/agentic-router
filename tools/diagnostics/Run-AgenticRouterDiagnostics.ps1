[CmdletBinding(SupportsShouldProcess)]
param(
  [string]$RepositoryPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
  [string]$DataDirectory,
  [string]$OutputDirectory = (Join-Path $PSScriptRoot 'output'),
  [switch]$RunBuild,
  [switch]$RunTests,
  [switch]$VerifyPublish,
  [switch]$CreateSupportPackage
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $RepositoryPath).Path
if (-not $DataDirectory) {
  $DataDirectory = Join-Path $repo 'AgenticRouter.Api\data'
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Get-RelativeOrRedactedPath([string]$Path) {
  $full = [System.IO.Path]::GetFullPath($Path)
  if ($full.StartsWith($repo, [System.StringComparison]::OrdinalIgnoreCase)) {
    $rootUri = [Uri]($repo.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar)
    return $rootUri.MakeRelativeUri([Uri]$full).ToString()
  }
  return "<external-path-redacted>"
}

function Read-JsonMetadata([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    return [ordered]@{ present = $false }
  }

  try {
    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    return [ordered]@{
      present = $true
      readable = $true
      schemaVersion = $document.schemaVersion
      bytes = (Get-Item -LiteralPath $Path).Length
    }
  }
  catch {
    return [ordered]@{
      present = $true
      readable = $false
      error = 'JSON is unreadable'
      bytes = (Get-Item -LiteralPath $Path).Length
    }
  }
}

$project = Join-Path $repo 'AgenticRouter.Api\AgenticRouter.Api.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$gitBranch = (& git -C $repo branch --show-current 2>$null)
$gitCommit = (& git -C $repo rev-parse HEAD 2>$null)
$gitStatus = @(& git -C $repo status --short 2>$null)
$dataFiles = [ordered]@{}
foreach ($name in @('settings.json', 'workspaces.json', 'model-organization.json', 'migration-state.json', 'migration-failure.json')) {
  $dataFiles[$name] = Read-JsonMetadata (Join-Path $DataDirectory $name)
}

$providerState = [ordered]@{
  settingsPresent = $dataFiles['settings.json'].present
  protectedSecretDirectoryPresent = Test-Path -LiteralPath (Join-Path $DataDirectory 'secrets')
  apiKeysIncluded = $false
}
$usageFiles = if (Test-Path -LiteralPath (Join-Path $DataDirectory 'usage')) {
  @(Get-ChildItem -LiteralPath (Join-Path $DataDirectory 'usage') -Filter '*.jsonl' -File)
} else { @() }
$sessionFiles = if (Test-Path -LiteralPath (Join-Path $DataDirectory 'workspaces')) {
  @(Get-ChildItem -LiteralPath (Join-Path $DataDirectory 'workspaces') -Filter '*.json' -File -Recurse)
} else { @() }

$commands = [ordered]@{}
if ($RunBuild) {
  & dotnet format (Join-Path $repo 'AgenticRouter.slnx') --verify-no-changes
  & dotnet build (Join-Path $repo 'AgenticRouter.slnx') -c Release
  $commands.build = [ordered]@{ requested = $true; exitCode = $LASTEXITCODE }
}
if ($RunTests) {
  & dotnet test (Join-Path $repo 'tests\AgenticRouter.EndToEndTests\AgenticRouter.EndToEndTests.csproj') -c Release --settings (Join-Path $repo 'playwright.runsettings')
  $commands.tests = [ordered]@{ requested = $true; exitCode = $LASTEXITCODE }
}
if ($VerifyPublish) {
  $publish = Join-Path $output 'publish'
  & dotnet publish $project -c Release -o $publish
  & (Join-Path $PSScriptRoot 'Test-PublishArtifact.ps1') -PublishPath $publish
  $commands.publish = [ordered]@{ requested = $true; exitCode = $LASTEXITCODE }
}

$report = [ordered]@{
  schemaVersion = 1
  createdAt = [DateTimeOffset]::UtcNow.ToString('O')
  applicationVersion = $version
  runtime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
  operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
  repository = [ordered]@{
    identity = Split-Path -Leaf $repo
    branch = $gitBranch
    commit = $gitCommit
    dirtyEntryCount = $gitStatus.Count
  }
  dataDirectory = Get-RelativeOrRedactedPath $DataDirectory
  stores = $dataFiles
  providerConfiguration = $providerState
  aggregateCounts = [ordered]@{
    usageFiles = $usageFiles.Count
    usageBytes = ($usageFiles | Measure-Object Length -Sum).Sum
    sessionFiles = $sessionFiles.Count
    sessionBytes = ($sessionFiles | Measure-Object Length -Sum).Sum
  }
  publishVerification = if ($VerifyPublish) { 'executed' } else { 'not-requested' }
  commands = $commands
  exclusions = @(
    'API keys and authorization headers',
    'prompts and assistant responses',
    'conversation history and file contents',
    'images, tool arguments, hidden prompts, and chain-of-thought',
    'full personal paths'
  )
}

$jsonPath = Join-Path $output 'diagnostics.json'
$markdownPath = Join-Path $output 'diagnostics.md'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding utf8
@"
# Agentic Router diagnostics

- Version: $version
- OS: $($report.operatingSystem)
- Runtime: $($report.runtime)
- Git: $gitBranch at $gitCommit
- Dirty entries: $($gitStatus.Count)
- Session files: $($sessionFiles.Count)
- Usage files: $($usageFiles.Count)
- Publish verification: $($report.publishVerification)

This report intentionally excludes keys, prompts, responses, conversation content, images, tool arguments, and personal paths.
"@ | Set-Content -LiteralPath $markdownPath -Encoding utf8

if ($CreateSupportPackage) {
  $package = Join-Path $output 'agentic-router-support.zip'
  Compress-Archive -LiteralPath $jsonPath, $markdownPath -DestinationPath $package -Force
}

Write-Output $jsonPath
Write-Output $markdownPath
