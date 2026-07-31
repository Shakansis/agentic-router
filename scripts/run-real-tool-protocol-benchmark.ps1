[CmdletBinding()]
param(
  [string]$BaseUrl = "http://127.0.0.1:5294",
  [string[]]$Models = @(),
  [string]$OutputDirectory = "",
  [ValidateRange(30, 3600)]
  [int]$TimeoutSeconds = 600,
  [ValidateRange(0, 30)]
  [int]$SettlingSeconds = 2
)

$ErrorActionPreference = "Stop"
$normalizedBaseUrl = $BaseUrl.TrimEnd("/")

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $PSScriptRoot "..\artifacts\benchmarks"
}

$catalog = Invoke-RestMethod `
  -Method Get `
  -Uri "$normalizedBaseUrl/api/models" `
  -TimeoutSec 30

if (-not $catalog.available) {
  throw "The configured Ollama instance is unavailable: $($catalog.error.message)"
}

$installed = @($catalog.models)
$selected = New-Object System.Collections.Generic.List[object]

if ($Models.Count -eq 0) {
  foreach ($model in ($installed | Sort-Object sizeBytes, name)) {
    $selected.Add($model)
  }
} else {
  foreach ($requestedName in $Models) {
    $match = $installed |
      Where-Object {
        [string]::Equals(
          $_.name,
          $requestedName,
          [System.StringComparison]::OrdinalIgnoreCase
        )
      } |
      Select-Object -First 1

    if ($null -eq $match) {
      throw "Model '$requestedName' is not installed in the configured Ollama instance."
    }

    $selected.Add($match)
  }
}

if ($selected.Count -eq 0) {
  throw "The configured Ollama instance reported no installed models."
}

$results = New-Object System.Collections.Generic.List[object]
$transportFailure = $false
$startedAt = [DateTimeOffset]::UtcNow

for ($modelIndex = 0; $modelIndex -lt $selected.Count; $modelIndex++) {
  $model = $selected[$modelIndex]
  $isLastModel = $modelIndex -eq ($selected.Count - 1)
  Write-Host "Benchmarking $($model.name) ..."
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

  try {
    $response = Invoke-RestMethod `
      -Method Post `
      -Uri "$normalizedBaseUrl/api/models/conformance" `
      -ContentType "application/json" `
      -Body (
        @{
          model = $model.name
          restoreResidentModel = $isLastModel
        } |
          ConvertTo-Json -Compress
      ) `
      -TimeoutSec $TimeoutSeconds

    $results.Add(
      [pscustomobject][ordered]@{
        model = [string]$response.model
        digest = [string]$response.digest
        ollamaVersion = [string]$response.ollamaVersion
        passed = [bool]$response.passed
        durationMilliseconds = [long]$response.durationMilliseconds
        failure = $response.failure
        transportError = $null
      }
    )
  } catch {
    $transportFailure = $true
    $technicalMessage = $_.Exception.Message

    if (-not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
      $technicalMessage = $_.ErrorDetails.Message
    }

    $results.Add(
      [pscustomobject][ordered]@{
        model = [string]$model.name
        digest = [string]$model.digest
        ollamaVersion = "unavailable"
        passed = $false
        durationMilliseconds = [long]$stopwatch.ElapsedMilliseconds
        failure = "Benchmark request failed."
        transportError = $technicalMessage
      }
    )
  }

  if (
    $SettlingSeconds -gt 0 -and
    -not $isLastModel
  ) {
    Start-Sleep -Seconds $SettlingSeconds
  }
}

$completedAt = [DateTimeOffset]::UtcNow
$passed = @($results | Where-Object { $_.passed }).Count
$failed = $results.Count - $passed
$report = [pscustomobject][ordered]@{
  schemaVersion = 1
  benchmark = "TOOL_PROTOCOL_CONFORMANCE_V1"
  endpoint = $normalizedBaseUrl
  startedAt = $startedAt.ToString("o")
  completedAt = $completedAt.ToString("o")
  total = $results.Count
  passed = $passed
  failed = $failed
  results = $results.ToArray()
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null
$timestamp = $startedAt.ToString("yyyyMMdd-HHmmss")
$jsonPath = Join-Path $resolvedOutputDirectory "tool-protocol-$timestamp.json"
$markdownPath = Join-Path $resolvedOutputDirectory "tool-protocol-$timestamp.md"
$report |
  ConvertTo-Json -Depth 8 |
  Set-Content -LiteralPath $jsonPath -Encoding utf8

$markdown = New-Object System.Collections.Generic.List[string]
$markdown.Add("# Real Ollama tool-protocol benchmark")
$markdown.Add("")
$markdown.Add("- Benchmark: ``TOOL_PROTOCOL_CONFORMANCE_V1``")
$markdown.Add("- Endpoint: ``$normalizedBaseUrl``")
$markdown.Add("- Started: $($report.startedAt)")
$markdown.Add("- Completed: $($report.completedAt)")
$markdown.Add("- Result: $passed passed, $failed failed, $($results.Count) total")
$markdown.Add("")
$markdown.Add("| Model | Digest | Ollama | Result | Duration | Failure |")
$markdown.Add("| --- | --- | --- | --- | ---: | --- |")

foreach ($result in $results) {
  $status = if ($result.passed) { "PASS" } else { "FAIL" }
  $failure = if ($result.transportError) {
    "$($result.failure) $($result.transportError)"
  } elseif ($result.failure) {
    [string]$result.failure
  } else {
    ""
  }
  $failure = $failure.Replace("|", "\|").Replace(
    "`r",
    " "
  ).Replace(
    "`n",
    " "
  )
  $markdown.Add(
    "| ``$($result.model)`` | ``$($result.digest)`` | ``$($result.ollamaVersion)`` | $status | $($result.durationMilliseconds) ms | $failure |"
  )
}

$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

Write-Host ""
Write-Host "Benchmark complete: $passed passed, $failed failed."
Write-Host "JSON: $jsonPath"
Write-Host "Markdown: $markdownPath"

if ($transportFailure) {
  exit 2
}
