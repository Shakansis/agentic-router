[CmdletBinding()]
param(
  [string]$BaseUrl = "http://127.0.0.1:5396",
  [string]$Model = "qwen3.8:27b-gpu0",
  [string]$EvidenceRoot = "",
  [ValidateRange(30, 14400)]
  [int]$TimeoutSeconds = 7200
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd("/")
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
  $EvidenceRoot = Join-Path $repositoryRoot "docs\validation\pre-m10-real-2026-08-23-run-05-opencode-cleanup-fix"
}
$EvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) {
  throw "Evidence root already exists: $EvidenceRoot"
}
[System.IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Write-NewText([string]$Path, [string]$Content) {
  if (Test-Path -LiteralPath $Path) {
    throw "Refusing to overwrite evidence: $Path"
  }
  [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Write-NewJson([string]$Path, $Value) {
  Write-NewText $Path ($Value | ConvertTo-Json -Depth 100)
}

function Invoke-Suite([string]$SuiteId, [int]$SuiteVersion, [string]$Prefix) {
  $request = [ordered]@{
    model = $Model
    harnesses = @("opencode")
    suiteId = $SuiteId
    suiteVersion = $SuiteVersion
    timeoutSeconds = 600
    modelExecutionPermissionGranted = $true
    clientRunId = [Guid]::NewGuid().ToString("N")
  }
  Write-NewJson (Join-Path $EvidenceRoot "$Prefix.request.json") $request
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  $response = Invoke-WebRequest `
    -Method Post `
    -Uri "$base/api/benchmarks/suite-runs" `
    -ContentType "application/json" `
    -Body ($request | ConvertTo-Json -Depth 20 -Compress) `
    -TimeoutSec $TimeoutSeconds `
    -SkipHttpErrorCheck
  Write-NewText (Join-Path $EvidenceRoot "$Prefix.response.json") ([string]$response.Content)
  Write-NewJson (Join-Path $EvidenceRoot "$Prefix.meta.json") ([ordered]@{
    statusCode = [int]$response.StatusCode
    elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
    completedAt = [DateTimeOffset]::UtcNow.ToString("o")
  })
  if ($response.StatusCode -ne 200) {
    return [pscustomobject]@{ statusCode = [int]$response.StatusCode; result = $null }
  }
  return [pscustomobject]@{
    statusCode = [int]$response.StatusCode
    result = $response.Content | ConvertFrom-Json -Depth 100
  }
}

$startedAt = [DateTimeOffset]::UtcNow
$models = Invoke-RestMethod "$base/api/models" -TimeoutSec 30
$exact = @($models.models | Where-Object { $_.name -ceq $Model })
if ($exact.Count -ne 1) {
  throw "Exact model '$Model' is not installed once."
}
Write-NewJson (Join-Path $EvidenceRoot "exact-model.json") $exact[0]
$harnesses = Invoke-RestMethod "$base/api/harnesses" -TimeoutSec 60
$openCode = @($harnesses | Where-Object { $_.definition.id -eq "opencode" })
if ($openCode.Count -ne 1 -or -not $openCode[0].availability.available) {
  throw "OpenCode is unavailable."
}
Write-NewJson (Join-Path $EvidenceRoot "opencode-identity.json") $openCode[0]

Write-Host "[opencode cleanup fix] Basic CRUD v1"
$basic = Invoke-Suite "basic-crud" 1 "basic-crud-v1"
Write-Host "[opencode cleanup fix] Agent Behavior v2"
$behavior = Invoke-Suite "agent-behavior" 2 "agent-behavior-v2"

$assessments = foreach ($item in @($basic, $behavior)) {
  $result = $item.result
  $harness = @($result.harnessResults | Where-Object { $_.harness -eq "opencode" })
  $tests = if ($harness.Count -eq 1) { @($harness[0].tests) } else { @() }
  [pscustomobject][ordered]@{
    suite = if ($null -ne $result) { [string]$result.suiteId } else { "missing" }
    statusCode = $item.statusCode
    runId = if ($null -ne $result) { [string]$result.runId } else { $null }
    terminalState = if ($null -ne $result) { [string]$result.terminalState } else { "failed" }
    finalStatus = if ($null -ne $result) { [string]$result.finalStatus } else { "transport-error" }
    passed = if ($harness.Count -eq 1) { [int]$harness[0].passed } else { 0 }
    total = if ($harness.Count -eq 1) { [int]$harness[0].total } else { 0 }
    score = if ($harness.Count -eq 1) { $harness[0].score } else { $null }
    allWorkspaceCleanupFlags = $tests.Count -gt 0 -and @($tests | Where-Object { -not $_.workspaceCleanedUp }).Count -eq 0
    allWorkspacePathsAbsent = $tests.Count -gt 0 -and @($tests | Where-Object { Test-Path -LiteralPath $_.run.workspacePath }).Count -eq 0
    cleanupFailures = @($tests | Where-Object { $_.rawResult.error.code -eq "benchmark-cleanup-failed" }).Count
  }
}

$runtime = Join-Path $repositoryRoot ".artifacts\pre-m10-runtime-fix-01\data\opencode-runtime"
$pidFile = Join-Path $runtime "fake-opencode-process-id.txt"
$processAlive = $null
if (Test-Path -LiteralPath $pidFile) {
  $openCodePid = [int](Get-Content -LiteralPath $pidFile -Raw)
  $processAlive = $null -ne (Get-Process -Id $openCodePid -ErrorAction SilentlyContinue)
}

$summary = [pscustomobject][ordered]@{
  schemaVersion = 1
  repairCycle = 1
  defect = "opencode-benchmark-workspace-release"
  model = $Model
  startedAt = $startedAt.ToString("o")
  completedAt = [DateTimeOffset]::UtcNow.ToString("o")
  openCodeVersion = $openCode[0].availability.version
  openCodeProcessAliveAfterFinalSuite = $processAlive
  suites = @($assessments)
}
Write-NewJson (Join-Path $EvidenceRoot "summary.json") $summary
$summary | ConvertTo-Json -Depth 20
