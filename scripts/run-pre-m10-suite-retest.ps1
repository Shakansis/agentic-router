[CmdletBinding()]
param(
  [string]$BaseUrl = "http://127.0.0.1:5400",
  [string]$Model = "qwen3.8:27b-gpu0",
  [string[]]$Harnesses = @("codex", "opencode", "qwen-code"),
  [string]$EvidenceRoot = "",
  [ValidateRange(30, 14400)]
  [int]$TimeoutSeconds = 7200,
  [switch]$SkipBasicCrud,
  [switch]$SkipAgentBehavior
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd("/")
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
  $EvidenceRoot = Join-Path $repositoryRoot "docs\validation\pre-m10-real-2026-08-23-suite-retest"
}
$EvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) {
  throw "Evidence root already exists: $EvidenceRoot"
}
[System.IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
$supportedHarnesses = @("native", "codex", "opencode", "qwen-code", "claude-code")
$harnesses = @($Harnesses | ForEach-Object { $_.Trim().ToLowerInvariant() } | Select-Object -Unique)
if ($harnesses.Count -eq 0 -or @($harnesses | Where-Object { $_ -notin $supportedHarnesses }).Count -gt 0) {
  throw "Harnesses contains an unsupported active harness id."
}
if ($SkipBasicCrud -and $SkipAgentBehavior) {
  throw "At least one benchmark suite must be selected."
}

function Write-NewText([string]$Path, [string]$Content) {
  if (Test-Path -LiteralPath $Path) {
    throw "Refusing to overwrite evidence: $Path"
  }
  $parent = Split-Path -Parent $Path
  [System.IO.Directory]::CreateDirectory($parent) | Out-Null
  [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Write-NewJson([string]$Path, $Value) {
  Write-NewText $Path ($Value | ConvertTo-Json -Depth 100)
}

function Invoke-Suite([string]$Harness, [string]$SuiteId, [int]$SuiteVersion, [string]$Prefix) {
  $directory = Join-Path $EvidenceRoot $Harness
  [System.IO.Directory]::CreateDirectory($directory) | Out-Null
  $request = [ordered]@{
    model = $Model
    harnesses = @($Harness)
    suiteId = $SuiteId
    suiteVersion = $SuiteVersion
    timeoutSeconds = 600
    modelExecutionPermissionGranted = $true
    clientRunId = [Guid]::NewGuid().ToString("N")
  }
  Write-NewJson (Join-Path $directory "$Prefix.request.json") $request
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  $response = Invoke-WebRequest `
    -Method Post `
    -Uri "$base/api/benchmarks/suite-runs" `
    -ContentType "application/json" `
    -Body ($request | ConvertTo-Json -Depth 20 -Compress) `
    -TimeoutSec $TimeoutSeconds `
    -SkipHttpErrorCheck
  Write-NewText (Join-Path $directory "$Prefix.response.json") ([string]$response.Content)
  Write-NewJson (Join-Path $directory "$Prefix.meta.json") ([ordered]@{
    statusCode = [int]$response.StatusCode
    elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
    completedAt = [DateTimeOffset]::UtcNow.ToString("o")
  })
  return [pscustomobject]@{
    StatusCode = [int]$response.StatusCode
    Result = if ($response.StatusCode -eq 200) {
      $response.Content | ConvertFrom-Json -Depth 100
    } else {
      $null
    }
  }
}

function New-Assessment([string]$Harness, $Response) {
  $result = $Response.Result
  $harnessResult = @($result.harnessResults | Where-Object { $_.harness -eq $Harness })
  $tests = if ($harnessResult.Count -eq 1) { @($harnessResult[0].tests) } else { @() }
  return [pscustomobject][ordered]@{
    suite = if ($null -ne $result) { [string]$result.suiteId } else { "missing" }
    statusCode = $Response.StatusCode
    runId = if ($null -ne $result) { [string]$result.runId } else { $null }
    terminalState = if ($null -ne $result) { [string]$result.terminalState } else { "failed" }
    finalStatus = if ($null -ne $result) { [string]$result.finalStatus } else { "transport-error" }
    passed = if ($harnessResult.Count -eq 1) { [int]$harnessResult[0].passed } else { 0 }
    total = if ($harnessResult.Count -eq 1) { [int]$harnessResult[0].total } else { 0 }
    score = if ($harnessResult.Count -eq 1) { $harnessResult[0].score } else { $null }
    allWorkspaceCleanupFlags = $tests.Count -gt 0 -and @(
      $tests | Where-Object { -not $_.workspaceCleanedUp }
    ).Count -eq 0
    allWorkspacePathsAbsent = $tests.Count -gt 0 -and @(
      $tests | Where-Object { Test-Path -LiteralPath $_.run.workspacePath }
    ).Count -eq 0
    cleanupFailures = @(
      $tests | Where-Object { $_.rawResult.error.code -eq "benchmark-cleanup-failed" }
    ).Count
  }
}

$startedAt = [DateTimeOffset]::UtcNow
$models = Invoke-RestMethod "$base/api/models" -TimeoutSec 30
$exact = @($models.models | Where-Object { $_.name -ceq $Model })
if ($exact.Count -ne 1) {
  throw "Exact model '$Model' is not installed once."
}
Write-NewJson (Join-Path $EvidenceRoot "exact-model.json") $exact[0]
$availableHarnesses = Invoke-RestMethod "$base/api/harnesses" -TimeoutSec 60
$identities = foreach ($harness in $harnesses) {
  $identity = @($availableHarnesses | Where-Object { $_.definition.id -eq $harness })
  if ($identity.Count -ne 1 -or -not $identity[0].availability.available) {
    throw "Harness '$harness' is unavailable."
  }
  $identity[0]
}
Write-NewJson (Join-Path $EvidenceRoot "harness-identities.json") @($identities)

$results = foreach ($harness in $harnesses) {
  $suites = @()
  if (-not $SkipBasicCrud) {
    Write-Host "[$harness] Basic CRUD v1"
    $basic = Invoke-Suite $harness "basic-crud" 1 "basic-crud-v1"
    $suites += New-Assessment $harness $basic
  }
  if (-not $SkipAgentBehavior) {
    Write-Host "[$harness] Agent Behavior v2"
    $behavior = Invoke-Suite $harness "agent-behavior" 2 "agent-behavior-v2"
    $suites += New-Assessment $harness $behavior
  }
  [pscustomobject][ordered]@{
    harness = $harness
    version = @($identities | Where-Object { $_.definition.id -eq $harness })[0].availability.version
    suites = @($suites)
  }
}

$summary = [pscustomobject][ordered]@{
  schemaVersion = 1
  battery = "PRE_M10_AFFECTED_SUITE_RETEST"
  model = $Model
  startedAt = $startedAt.ToString("o")
  completedAt = [DateTimeOffset]::UtcNow.ToString("o")
  sequential = $true
  cloudFallback = $false
  results = @($results)
}
Write-NewJson (Join-Path $EvidenceRoot "summary.json") $summary
$summary | ConvertTo-Json -Depth 30
