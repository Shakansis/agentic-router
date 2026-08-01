[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^[A-Za-z0-9:._-]{1,128}$')]
  [string]$TraceId,

  [string]$DataDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'AgenticRouter.Api\data'),

  [ValidateSet('Console', 'Json', 'Markdown')]
  [string]$Format = 'Console',

  [ValidateRange(1, 500)]
  [int]$MaximumEvents = 200,

  [ValidateRange(16384, 4194304)]
  [long]$MaximumOutputBytes = 262144
)

$ErrorActionPreference = 'Stop'
$incidentDirectory = Join-Path ([System.IO.Path]::GetFullPath($DataDirectory)) 'incidents'
if (-not (Test-Path -LiteralPath $incidentDirectory -PathType Container)) {
  throw 'diagnostic-journal-unavailable: the incident directory does not exist.'
}

$events = [System.Collections.Generic.List[object]]::new()
$matchedBytes = 0L
$truncated = $false
$malformed = 0
$files = @(Get-ChildItem -LiteralPath $incidentDirectory -Filter 'incidents-*.jsonl' -File | Sort-Object LastWriteTimeUtc -Descending)
foreach ($file in $files) {
  $reader = [System.IO.StreamReader]::new($file.FullName, [System.Text.Encoding]::UTF8, $true, 4096)
  try {
    while (-not $reader.EndOfStream) {
      $line = $reader.ReadLine()
      if ($line.IndexOf($TraceId, [System.StringComparison]::Ordinal) -lt 0) {
        continue
      }

      try {
        $item = $line | ConvertFrom-Json
      }
      catch {
        $malformed++
        continue
      }

      if ([string]$item.traceId -cne $TraceId) {
        continue
      }

      $matchedBytes += [System.Text.Encoding]::UTF8.GetByteCount($line)
      if ($events.Count -ge $MaximumEvents -or $matchedBytes -gt $MaximumOutputBytes) {
        $truncated = $true
        continue
      }
      $events.Add($item)
    }
  }
  finally {
    $reader.Dispose()
  }
}

if ($events.Count -eq 0) {
  throw 'diagnostic-trace-not-found: no retained event matched the exact trace identifier.'
}

$ordered = @($events | Sort-Object sequence)
$failure = @($ordered | Where-Object status -eq 'failed') | Select-Object -Last 1
$last = $ordered[-1]
$contextEvent = @($ordered | Where-Object { $null -ne $_.contextFit }) | Select-Object -Last 1
$completed = @($ordered | Where-Object completed -eq $true).Count -gt 0
$reviewAvailable = @($ordered | Where-Object reviewAvailable -eq $true).Count -gt 0
$provider = if ($failure -and $failure.provider) { [string]$failure.provider } else { [string]$last.provider }
$model = if ($failure -and $failure.model) { [string]$failure.model } else { [string]$last.model }
$report = [ordered]@{
  traceId = $TraceId
  status = if ($failure) { 'failed' } elseif ($completed) { 'completed' } else { [string]$last.status }
  failureCode = if ($failure) { [string]$failure.code } else { $null }
  failureStage = if ($failure) { [string]$failure.stage } else { $null }
  provider = $provider
  model = $model
  coordinator = [string](@($ordered | Where-Object coordinator | Select-Object -Last 1).coordinator)
  executionPath = [string](@($ordered | Where-Object executionPath | Select-Object -Last 1).executionPath)
  contextFit = if ($contextEvent) { $contextEvent.contextFit } else { $null }
  completed = $completed
  reviewAvailable = $reviewAvailable
  eventCount = $ordered.Count
  malformedRecordCount = $malformed
  truncated = $truncated
  recommendation = if ($completed) {
    'Review the terminal execution summary and retained artifacts.'
  } elseif ($reviewAvailable) {
    'Open the execution review before deciding whether to retry.'
  } else {
    'Use the typed failure and context arithmetic to select another execution path.'
  }
  events = $ordered
}

if ($Format -eq 'Json') {
  $report | ConvertTo-Json -Depth 8
  return
}

if ($Format -eq 'Markdown') {
  "# Agentic Router trace $TraceId"
  ""
  "- Status: $($report.status)"
  "- Failure: $($report.failureCode) / $($report.failureStage)"
  "- Provider/model: $($report.provider) / $($report.model)"
  "- Coordinator/path: $($report.coordinator) / $($report.executionPath)"
  "- Completed/reviewable: $completed / $reviewAvailable"
  if ($report.contextFit) {
    "- Context: input $($report.contextFit.estimatedInputTokens) + reserved $($report.contextFit.reservedOutputTokens) = required $($report.contextFit.requiredContextTokens); maximum $($report.contextFit.maximumContextTokens)"
  }
  "- Recommendation: $($report.recommendation)"
  ""
  "## Timeline"
  foreach ($event in $ordered) {
    "- $($event.sequence). [$($event.status)] $($event.code) - $($event.summary)"
  }
  return
}

Write-Output "Trace: $TraceId"
Write-Output "Status: $($report.status)"
Write-Output "Failure: $($report.failureCode) / $($report.failureStage)"
Write-Output "Provider/model: $($report.provider) / $($report.model)"
Write-Output "Coordinator/path: $($report.coordinator) / $($report.executionPath)"
if ($report.contextFit) {
  Write-Output "Context: input $($report.contextFit.estimatedInputTokens) + reserved $($report.contextFit.reservedOutputTokens) = required $($report.contextFit.requiredContextTokens); maximum $($report.contextFit.maximumContextTokens)"
}
Write-Output "Completed/reviewable: $completed / $reviewAvailable"
Write-Output "Recommendation: $($report.recommendation)"
Write-Output 'Timeline:'
foreach ($event in $ordered) {
  Write-Output "  $($event.sequence). [$($event.status)] $($event.code) - $($event.summary)"
}
