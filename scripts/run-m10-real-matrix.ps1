[CmdletBinding()]
param(
  [string]$BaseUrl = "http://127.0.0.1:5404",
  [string[]]$Models = @("qwen3.8:27b-gpu0", "qwen3:4b-instruct"),
  [string[]]$Harnesses = @("native", "codex", "opencode", "qwen-code", "claude-code"),
  [string]$EvidenceRoot = "",
  [ValidateRange(5, 600)]
  [int]$TimeoutSeconds = 120,
  [ValidateRange(60, 21600)]
  [int]$RunTimeoutSeconds = 14400
)

$ErrorActionPreference = "Stop"
$base = $BaseUrl.TrimEnd("/")
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
  $EvidenceRoot = Join-Path $repositoryRoot "docs\validation\m10-real-matrix-2026-08-23"
}
$EvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $EvidenceRoot) {
  throw "Evidence root already exists: $EvidenceRoot"
}
[System.IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Write-NewJson([string]$Path, $Value) {
  if (Test-Path -LiteralPath $Path) {
    throw "Refusing to overwrite evidence: $Path"
  }
  [System.IO.File]::WriteAllText(
    $Path,
    ($Value | ConvertTo-Json -Depth 100),
    $utf8
  )
}

function Invoke-JsonRequest(
  [string]$Method,
  [string]$Uri,
  $Body = $null,
  [int]$RequestTimeoutSeconds = 60
) {
  $parameters = @{
    Method = $Method
    Uri = $Uri
    TimeoutSec = $RequestTimeoutSeconds
    SkipHttpErrorCheck = $true
  }
  if ($null -ne $Body) {
    $parameters.ContentType = "application/json"
    $parameters.Body = $Body | ConvertTo-Json -Depth 30 -Compress
  }
  $response = Invoke-WebRequest @parameters
  if ([int]$response.StatusCode -notin 200, 202) {
    throw "HTTP $([int]$response.StatusCode) from $Method $Uri`: $($response.Content)"
  }
  if ([string]::IsNullOrWhiteSpace([string]$response.Content)) {
    return $null
  }
  return $response.Content | ConvertFrom-Json -Depth 100
}

$startedAt = [DateTimeOffset]::UtcNow
$catalogModels = Invoke-JsonRequest "GET" "$base/api/models"
$selectedIdentities = foreach ($model in $Models) {
  $match = @($catalogModels.models | Where-Object { $_.name -ceq $model })
  if ($match.Count -ne 1) {
    throw "Exact installed model '$model' was not found once."
  }
  $match[0]
}
Write-NewJson (Join-Path $EvidenceRoot "selected-models.json") @($selectedIdentities)

$catalog = Invoke-JsonRequest "GET" "$base/api/benchmarks/catalog"
$selectedHarnesses = foreach ($harness in $Harnesses) {
  $match = @($catalog.harnesses | Where-Object { $_.definition.id -eq $harness })
  if ($match.Count -ne 1 -or -not $match[0].availability.available) {
    throw "Active harness '$harness' is unavailable."
  }
  $match[0]
}
Write-NewJson (Join-Path $EvidenceRoot "selected-harnesses.json") @($selectedHarnesses)

$originalProfile = Invoke-JsonRequest "GET" "$base/api/benchmarks/scoring-profile"
Write-NewJson (Join-Path $EvidenceRoot "scoring-profile.before.json") $originalProfile
$runId = [Guid]::NewGuid().ToString("N")
$request = [ordered]@{
  model = $Models[0]
  models = @($Models)
  harnesses = @($Harnesses)
  suiteId = "basic-crud"
  suiteVersion = 1
  timeoutSeconds = $TimeoutSeconds
  scoringProfileId = [string]$originalProfile.id
  scoreWeights = $originalProfile.weights
  modelExecutionPermissionGranted = $true
  clientRunId = $runId
}
Write-NewJson (Join-Path $EvidenceRoot "matrix.request.json") $request
$start = Invoke-JsonRequest "POST" "$base/api/benchmarks/suite-runs/live" $request
Write-NewJson (Join-Path $EvidenceRoot "matrix.start.json") $start

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($RunTimeoutSeconds)
$pollCount = 0
do {
  Start-Sleep -Seconds 1
  $pollCount++
  $view = Invoke-JsonRequest "GET" "$base/api/benchmarks/suite-runs/$runId/live"
  if ([DateTimeOffset]::UtcNow -ge $deadline -and -not $view.terminal) {
    throw "Matrix run '$runId' exceeded the unattended run timeout."
  }
} while (-not $view.terminal)
Write-NewJson (Join-Path $EvidenceRoot "matrix.final-live.json") $view
$result = Invoke-JsonRequest "GET" "$base/api/benchmarks/suite-runs/$runId"
Write-NewJson (Join-Path $EvidenceRoot "matrix.persisted.json") $result
$defaultProjection = Invoke-JsonRequest "POST" "$base/api/benchmarks/suite-runs/$runId/rescore"
Write-NewJson (Join-Path $EvidenceRoot "matrix.rescore-original.json") $defaultProjection

$originalOrder = @($defaultProjection.pairRanking | ForEach-Object { "$($_.model)|$($_.harness)" })
$challengers = @(
  [ordered]@{ objectiveSuccess = 0; correctness = 100; terminality = 0; workspaceAccuracy = 0; efficiency = 0 },
  [ordered]@{ objectiveSuccess = 0; correctness = 0; terminality = 0; workspaceAccuracy = 0; efficiency = 100 },
  [ordered]@{ objectiveSuccess = 0; correctness = 0; terminality = 100; workspaceAccuracy = 0; efficiency = 0 }
)
$rankingChanged = $false
$challengerIndex = 0
foreach ($weights in $challengers) {
  $challengerIndex++
  $profile = Invoke-JsonRequest "PUT" "$base/api/benchmarks/scoring-profile" $weights
  $projection = Invoke-JsonRequest "POST" "$base/api/benchmarks/suite-runs/$runId/rescore"
  Write-NewJson (Join-Path $EvidenceRoot ("matrix.rescore-{0:D2}.json" -f $challengerIndex)) ([ordered]@{
    profile = $profile
    projection = $projection
  })
  $order = @($projection.pairRanking | ForEach-Object { "$($_.model)|$($_.harness)" })
  if (($order -join "`n") -cne ($originalOrder -join "`n")) {
    $rankingChanged = $true
    break
  }
}
if ($originalProfile.id -eq "default") {
  $restoredProfile = Invoke-JsonRequest "POST" "$base/api/benchmarks/scoring-profile/reset"
} else {
  $restoredProfile = Invoke-JsonRequest "PUT" "$base/api/benchmarks/scoring-profile" $originalProfile.weights
}
Write-NewJson (Join-Path $EvidenceRoot "scoring-profile.restored.json") $restoredProfile

$cells = @($result.cells)
$testRuns = @($cells | Where-Object { $null -ne $_.result } | ForEach-Object { @($_.result.tests) })
$workspaceIds = @($testRuns | ForEach-Object { [string]$_.run.workspaceId })
$workspacePaths = @($testRuns | ForEach-Object { [string]$_.run.workspacePath })
$orderedExecutableCells = @($cells | Where-Object { $null -ne $_.result } | Sort-Object executionOrder)
$eventWindowTruncated = @($view.events).Count -gt 0 -and [long]$view.events[0].sequence -gt 1
$sequential = [bool]$result.environment.sequential
$previousCompletion = 0
$observedSequentialCells = 0
foreach ($cell in $orderedExecutableCells) {
  $started = @($view.events | Where-Object {
    $_.type -eq "harness.started" -and $_.model -eq $cell.model -and $_.harness -eq $cell.harness
  })
  $completed = @($view.events | Where-Object {
    $_.type -eq "harness.completed" -and $_.model -eq $cell.model -and $_.harness -eq $cell.harness
  })
  if ($started.Count -eq 0 -and $completed.Count -eq 0 -and $eventWindowTruncated) {
    continue
  }
  if ($started.Count -eq 0 -and $completed.Count -eq 1 -and $eventWindowTruncated `
    -and [long]$completed[0].sequence -lt [long]$view.events[0].sequence + 64) {
    $previousCompletion = [long]$completed[0].sequence
    continue
  }
  if ($started.Count -ne 1 -or $completed.Count -ne 1 `
    -or ($previousCompletion -gt 0 -and [long]$started[0].sequence -le $previousCompletion) `
    -or [long]$completed[0].sequence -le [long]$started[0].sequence) {
    $sequential = $false
    break
  }
  $observedSequentialCells++
  $previousCompletion = [long]$completed[0].sequence
}
$sequential = $sequential -and $observedSequentialCells -gt 0

$summary = [ordered]@{
  schemaVersion = 1
  runId = $runId
  startedAt = $startedAt.ToString("o")
  completedAt = [DateTimeOffset]::UtcNow.ToString("o")
  models = @($Models)
  harnesses = @($Harnesses)
  expectedCells = $Models.Count * $Harnesses.Count
  actualCells = $cells.Count
  executionOrder = @($result.executionOrder)
  terminalState = [string]$result.terminalState
  finalStatus = [string]$result.finalStatus
  cellStatuses = @($cells | Group-Object status | ForEach-Object {
    [ordered]@{ status = $_.Name; count = $_.Count }
  })
  uniqueWorkspacePerTest = $workspaceIds.Count -eq @($workspaceIds | Select-Object -Unique).Count
  allWorkspacesCleaned = @($testRuns | Where-Object { -not $_.workspaceCleanedUp }).Count -eq 0
  allWorkspacePathsAbsent = @($workspacePaths | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0
  sequentialCells = $sequential
  observedSequentialCells = $observedSequentialCells
  eventWindowTruncated = $eventWindowTruncated
  liveEventsIdentifyPairs = @($view.events | Where-Object {
    $_.type -in "harness.started", "harness.completed", "test.state" `
      -and ([string]::IsNullOrWhiteSpace([string]$_.model) `
        -or [string]::IsNullOrWhiteSpace([string]$_.harness))
  }).Count -eq 0
  pairRankingCount = @($result.pairRanking).Count
  modelRankingCount = @($result.modelRanking).Count
  harnessRankingCount = @($result.harnessRanking).Count
  everyCellInspectable = @($cells | Where-Object {
    $_.status -eq "completed" -and $null -eq $_.result
  }).Count -eq 0
  rankingChangedWithoutRerun = $rankingChanged
  pollCount = $pollCount
  cloudFallback = $false
}
Write-NewJson (Join-Path $EvidenceRoot "summary.json") $summary
$summary | ConvertTo-Json -Depth 30
