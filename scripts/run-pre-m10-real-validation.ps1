[CmdletBinding()]
param(
  [string]$BaseUrl = "http://127.0.0.1:5394",
  [string]$Model = "qwen3.8:27b-gpu0",
  [string[]]$Harnesses = @("native", "codex", "opencode", "qwen-code", "claude-code"),
  [string]$WorkspaceRoot = "",
  [string]$EvidenceRoot = "",
  [ValidateRange(30, 3600)]
  [int]$TurnTimeoutSeconds = 600,
  [ValidateRange(30, 14400)]
  [int]$SuiteTimeoutSeconds = 7200,
  [switch]$BoundaryOnly,
  [switch]$CancellationOnly,
  [switch]$SkipCancellation,
  [switch]$SkipSuites
)

$ErrorActionPreference = "Stop"
$normalizedBaseUrl = $BaseUrl.TrimEnd("/")
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
  $WorkspaceRoot = Join-Path $repositoryRoot ".artifacts\pre-m10-runtime\workspaces"
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
  $EvidenceRoot = Join-Path $repositoryRoot "docs\validation\pre-m10-real-2026-08-23"
}
$WorkspaceRoot = [System.IO.Path]::GetFullPath($WorkspaceRoot)
$EvidenceRoot = [System.IO.Path]::GetFullPath($EvidenceRoot)

if (Test-Path -LiteralPath $EvidenceRoot) {
  throw "Evidence root already exists and will not be overwritten: $EvidenceRoot"
}
[System.IO.Directory]::CreateDirectory($EvidenceRoot) | Out-Null
[System.IO.Directory]::CreateDirectory($WorkspaceRoot) | Out-Null

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$supportedHarnesses = @("native", "codex", "opencode", "qwen-code", "claude-code")
$harnesses = @(
  $Harnesses |
    ForEach-Object { $_.Trim().ToLowerInvariant() } |
    Select-Object -Unique
)
if ($harnesses.Count -eq 0 -or @($harnesses | Where-Object { $_ -notin $supportedHarnesses }).Count -gt 0) {
  throw "Harnesses must contain one or more active harness ids."
}
if ($CancellationOnly -and ($BoundaryOnly -or $SkipCancellation)) {
  throw "CancellationOnly cannot be combined with BoundaryOnly or SkipCancellation."
}
$startedAt = [DateTimeOffset]::UtcNow
$summary = [System.Collections.Generic.List[object]]::new()

function Write-NewText {
  param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [AllowEmptyString()] [string]$Content
  )
  if (Test-Path -LiteralPath $Path) {
    throw "Refusing to overwrite first-run evidence: $Path"
  }
  $parent = Split-Path -Parent $Path
  if (-not [string]::IsNullOrWhiteSpace($parent)) {
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
  }
  [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Write-NewJson {
  param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [AllowNull()] $Value
  )
  Write-NewText -Path $Path -Content ($Value | ConvertTo-Json -Depth 100)
}

function Get-WorkspaceSnapshot {
  param([Parameter(Mandatory)] [string]$Path)
  if (-not (Test-Path -LiteralPath $Path)) {
    return @()
  }
  return @(
    Get-ChildItem -LiteralPath $Path -File -Recurse -Force |
      Sort-Object FullName |
      ForEach-Object {
        [pscustomobject][ordered]@{
          path = [System.IO.Path]::GetRelativePath($Path, $_.FullName).Replace("\", "/")
          length = $_.Length
          sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
      }
  )
}

function Invoke-RecordedRequest {
  param(
    [Parameter(Mandatory)] [string]$Method,
    [Parameter(Mandatory)] [string]$Uri,
    [Parameter(Mandatory)] [string]$EvidencePrefix,
    [AllowNull()] $Body,
    [int]$TimeoutSeconds = 600
  )
  $requestRecord = [pscustomobject][ordered]@{
    method = $Method
    uri = $Uri
    body = $Body
    startedAt = [DateTimeOffset]::UtcNow.ToString("o")
  }
  Write-NewJson -Path "$EvidencePrefix.request.json" -Value $requestRecord
  $parameters = @{
    Method = $Method
    Uri = $Uri
    TimeoutSec = $TimeoutSeconds
    SkipHttpErrorCheck = $true
  }
  if ($null -ne $Body) {
    $parameters.ContentType = "application/json"
    $parameters.Body = $Body | ConvertTo-Json -Depth 100 -Compress
  }
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $response = Invoke-WebRequest @parameters
    $content = [string]$response.Content
    Write-NewText -Path "$EvidencePrefix.response.txt" -Content $content
    Write-NewJson -Path "$EvidencePrefix.meta.json" -Value ([pscustomobject][ordered]@{
      statusCode = [int]$response.StatusCode
      elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
      completedAt = [DateTimeOffset]::UtcNow.ToString("o")
      headers = $response.Headers
    })
    return [pscustomobject]@{
      StatusCode = [int]$response.StatusCode
      Content = $content
    }
  } catch {
    Write-NewJson -Path "$EvidencePrefix.exception.json" -Value ([pscustomobject][ordered]@{
      elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
      completedAt = [DateTimeOffset]::UtcNow.ToString("o")
      type = $_.Exception.GetType().FullName
      message = $_.Exception.Message
      errorDetails = $_.ErrorDetails.Message
    })
    throw
  }
}

function ConvertFrom-Sse {
  param([Parameter(Mandatory)] [string]$Content)
  $events = [System.Collections.Generic.List[object]]::new()
  foreach ($line in ($Content -split "`r?`n")) {
    if (-not $line.StartsWith("data: ", [System.StringComparison]::Ordinal)) {
      continue
    }
    $json = $line.Substring(6)
    try {
      $events.Add(($json | ConvertFrom-Json -Depth 100))
    } catch {
      $events.Add([pscustomobject]@{ type = "evidence.parse-error"; raw = $json })
    }
  }
  return $events.ToArray()
}

function Invoke-RecordedSseRequest {
  param(
    [Parameter(Mandatory)] [string]$Uri,
    [Parameter(Mandatory)] [string]$EvidencePrefix,
    [Parameter(Mandatory)] $Body,
    [int]$TimeoutSeconds = 600
  )
  $requestRecord = [pscustomobject][ordered]@{
    method = "Post"
    uri = $Uri
    body = $Body
    startedAt = [DateTimeOffset]::UtcNow.ToString("o")
  }
  Write-NewJson -Path "$EvidencePrefix.request.json" -Value $requestRecord
  $client = [System.Net.Http.HttpClient]::new()
  $client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan
  $request = [System.Net.Http.HttpRequestMessage]::new(
    [System.Net.Http.HttpMethod]::Post,
    $Uri
  )
  $json = $Body | ConvertTo-Json -Depth 100 -Compress
  $request.Content = [System.Net.Http.StringContent]::new(
    $json,
    $utf8NoBom,
    "application/json"
  )
  $timeout = [System.Threading.CancellationTokenSource]::new(
    [TimeSpan]::FromSeconds($TimeoutSeconds)
  )
  $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
  $raw = [System.Text.StringBuilder]::new()
  $terminalType = $null
  $recoveryDecisionCount = 0
  $response = $null
  try {
    $response = $client.SendAsync(
      $request,
      [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead,
      $timeout.Token
    ).GetAwaiter().GetResult()
    $stream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
    $reader = [System.IO.StreamReader]::new($stream, $utf8NoBom)
    try {
      while (-not $reader.EndOfStream) {
        $line = $reader.ReadLineAsync($timeout.Token).AsTask().GetAwaiter().GetResult()
        if ($null -eq $line) {
          break
        }
        [void]$raw.AppendLine($line)
        if (-not $line.StartsWith("data: ", [System.StringComparison]::Ordinal)) {
          continue
        }
        try {
          $event = $line.Substring(6) | ConvertFrom-Json -Depth 100
          if ($null -ne $event.recoveryDecision) {
            $availableOptions = @($event.recoveryDecision.options | ForEach-Object { [string]$_.id })
            if ($availableOptions -notcontains "stop") {
              throw "Recovery checkpoint did not offer the bounded stop option."
            }
            $recoveryDecisionCount++
            $checkpointId = [string]$event.recoveryDecision.checkpointId
            $decisionBody = [ordered]@{
              option = "stop"
              browserSessionId = [string]$Body.browserSessionId
              executionSessionId = [string]$event.recoveryDecision.executionSessionId
            }
            Invoke-RecordedRequest `
              -Method Post `
              -Uri "$normalizedBaseUrl/api/recovery/$checkpointId/decision" `
              -EvidencePrefix ("$EvidencePrefix.recovery-{0:D2}" -f $recoveryDecisionCount) `
              -Body $decisionBody `
              -TimeoutSeconds 30 | Out-Null
          }
          if ($event.type -in @("response.completed", "error", "request.cancelled")) {
            $terminalType = [string]$event.type
            break
          }
        } catch {
          continue
        }
      }
    } finally {
      $reader.Dispose()
      $stream.Dispose()
    }
    $content = $raw.ToString()
    Write-NewText -Path "$EvidencePrefix.response.txt" -Content $content
    Write-NewJson -Path "$EvidencePrefix.meta.json" -Value ([pscustomobject][ordered]@{
      statusCode = [int]$response.StatusCode
      elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
      completedAt = [DateTimeOffset]::UtcNow.ToString("o")
      terminalType = $terminalType
      recoveryDecisions = $recoveryDecisionCount
    })
    return [pscustomobject]@{
      StatusCode = [int]$response.StatusCode
      Content = $content
    }
  } catch {
    if ($raw.Length -gt 0) {
      Write-NewText -Path "$EvidencePrefix.partial-response.txt" -Content $raw.ToString()
    }
    Write-NewJson -Path "$EvidencePrefix.exception.json" -Value ([pscustomobject][ordered]@{
      elapsedMilliseconds = $stopwatch.ElapsedMilliseconds
      completedAt = [DateTimeOffset]::UtcNow.ToString("o")
      type = $_.Exception.GetType().FullName
      message = $_.Exception.Message
      terminalType = $terminalType
    })
    throw
  } finally {
    if ($null -ne $response) {
      $response.Dispose()
    }
    $request.Dispose()
    $timeout.Dispose()
    $client.Dispose()
  }
}

function Invoke-ChatTurn {
  param(
    [Parameter(Mandatory)] [string]$Harness,
    [Parameter(Mandatory)] [string]$BrowserSessionId,
    [AllowNull()] [string]$ConversationSessionId,
    [Parameter(Mandatory)] [string]$Prompt,
    [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$History,
    [Parameter(Mandatory)] [string]$EvidencePrefix,
    [Parameter(Mandatory)] [string]$WorkspacePath
  )
  Write-NewJson -Path "$EvidencePrefix.before.json" -Value (Get-WorkspaceSnapshot -Path $WorkspacePath)
  $body = [ordered]@{
    message = $Prompt
    model = $Model
    history = $History
    interactionMode = "execute"
    harness = $Harness
    approvalPolicy = "auto"
    browserSessionId = $BrowserSessionId
    conversationSessionId = $ConversationSessionId
    webSearchEnabled = $false
    images = @()
    compactContext = $false
  }
  $response = Invoke-RecordedSseRequest `
    -Uri "$normalizedBaseUrl/api/chat/stream" `
    -EvidencePrefix $EvidencePrefix `
    -Body $body `
    -TimeoutSeconds $TurnTimeoutSeconds
  $events = @(ConvertFrom-Sse -Content $response.Content)
  Write-NewJson -Path "$EvidencePrefix.events.json" -Value $events
  Write-NewJson -Path "$EvidencePrefix.after.json" -Value (Get-WorkspaceSnapshot -Path $WorkspacePath)
  $assistant = [string]::Concat(@(
    $events |
      Where-Object { $_.type -eq "response.delta" -and $null -ne $_.delta } |
      ForEach-Object { [string]$_.delta }
  ))
  $conversation = @(
    $events |
      Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.conversationSessionId) } |
      Select-Object -Last 1
  )
  $last = @($events | Select-Object -Last 1)
  return [pscustomobject][ordered]@{
    statusCode = $response.StatusCode
    events = $events
    assistant = $assistant
    conversationSessionId = if ($conversation.Count -gt 0) { [string]$conversation[0].conversationSessionId } else { $ConversationSessionId }
    terminalType = if ($last.Count -gt 0) { [string]$last[0].type } else { "missing" }
    executionSessionId = [string](@(
      $events |
        Where-Object { $null -ne $_.executionSession -and -not [string]::IsNullOrWhiteSpace([string]$_.executionSession.id) } |
        Select-Object -Last 1
    )[0].executionSession.id)
  }
}

function Test-SnapshotEqual {
  param([object[]]$Before, [object[]]$After)
  return (($Before | ConvertTo-Json -Depth 10 -Compress) -ceq ($After | ConvertTo-Json -Depth 10 -Compress))
}

function Get-JsonContent {
  param([Parameter(Mandatory)] [object]$Response)
  if ([string]::IsNullOrWhiteSpace([string]$Response.Content)) {
    return $null
  }
  return $Response.Content | ConvertFrom-Json -Depth 100
}

function New-SuiteAssessment {
  param(
    [Parameter(Mandatory)] [string]$Harness,
    [Parameter(Mandatory)] [object]$Response,
    [AllowNull()] $Result
  )
  $harnessResult = @($Result.harnessResults | Where-Object { $_.harness -eq $Harness })
  if ($Response.StatusCode -ne 200 -or $null -eq $Result -or $harnessResult.Count -ne 1) {
    return [pscustomobject][ordered]@{
      runId = if ($null -ne $Result) { [string]$Result.runId } else { $null }
      httpStatusCode = $Response.StatusCode
      finalStatus = if ($null -ne $Result) { [string]$Result.finalStatus } else { "transport-error" }
      terminalState = if ($null -ne $Result) { [string]$Result.terminalState } else { "failed" }
      passed = 0
      total = 0
      score = $null
    }
  }
  return [pscustomobject][ordered]@{
    runId = [string]$Result.runId
    httpStatusCode = $Response.StatusCode
    finalStatus = [string]$Result.finalStatus
    terminalState = [string]$Result.terminalState
    passed = [int]$harnessResult[0].passed
    total = [int]$harnessResult[0].total
    score = $harnessResult[0].score
  }
}

function Invoke-CancellationProof {
  param(
    [Parameter(Mandatory)] [string]$Harness,
    [Parameter(Mandatory)] [string]$Directory
  )
  $clientRunId = [Guid]::NewGuid().ToString("N")
  $body = [ordered]@{
    model = $Model
    harnesses = @($Harness)
    suiteId = "basic-crud"
    suiteVersion = 1
    timeoutSeconds = $TurnTimeoutSeconds
    modelExecutionPermissionGranted = $true
    clientRunId = $clientRunId
  }
  $start = Invoke-RecordedRequest `
    -Method Post `
    -Uri "$normalizedBaseUrl/api/benchmarks/suite-runs/live" `
    -EvidencePrefix (Join-Path $Directory "cancel.start") `
    -Body $body `
    -TimeoutSeconds 60
  $startBody = Get-JsonContent -Response $start
  $runId = [string]$startBody.runId
  if ([string]::IsNullOrWhiteSpace($runId)) {
    throw "Cancellation proof did not receive a run id for $Harness."
  }
  $poll = 0
  $sawRunning = $false
  do {
    Start-Sleep -Milliseconds 250
    $poll++
    $viewResponse = Invoke-WebRequest `
      -Method Get `
      -Uri "$normalizedBaseUrl/api/benchmarks/suite-runs/$runId/live" `
      -TimeoutSec 30 `
      -SkipHttpErrorCheck
    $view = $viewResponse.Content | ConvertFrom-Json -Depth 100
    Write-NewJson -Path (Join-Path $Directory ("cancel.poll-{0:D4}.json" -f $poll)) -Value $view
    $sawRunning = $sawRunning -or @($view.events | Where-Object { $_.type -eq "harness.started" }).Count -gt 0
  } while (-not $sawRunning -and -not [bool]$view.terminal -and $poll -lt 120)

  $cancel = Invoke-RecordedRequest `
    -Method Post `
    -Uri "$normalizedBaseUrl/api/benchmarks/suite-runs/$runId/cancel" `
    -EvidencePrefix (Join-Path $Directory "cancel.request") `
    -Body $null `
    -TimeoutSeconds 30

  $finalPoll = 0
  do {
    Start-Sleep -Milliseconds 500
    $finalPoll++
    $finalResponse = Invoke-WebRequest `
      -Method Get `
      -Uri "$normalizedBaseUrl/api/benchmarks/suite-runs/$runId/live" `
      -TimeoutSec 30 `
      -SkipHttpErrorCheck
    $finalView = $finalResponse.Content | ConvertFrom-Json -Depth 100
  } while (-not [bool]$finalView.terminal -and $finalPoll -lt 240)
  Write-NewJson -Path (Join-Path $Directory "cancel.final-live.json") -Value $finalView
  $persisted = Invoke-RecordedRequest `
    -Method Get `
    -Uri "$normalizedBaseUrl/api/benchmarks/suite-runs/$runId" `
    -EvidencePrefix (Join-Path $Directory "cancel.persisted") `
    -Body $null `
    -TimeoutSeconds 30
  $lastEvent = @($finalView.events | Select-Object -Last 1)
  $finalState = if ($lastEvent.Count -gt 0) { [string]$lastEvent[0].state } else { "missing" }
  return [pscustomobject][ordered]@{
    runId = $runId
    sawRunning = $sawRunning
    cancelStatusCode = $cancel.StatusCode
    finalState = $finalState
    persistedStatusCode = $persisted.StatusCode
    passed = $cancel.StatusCode -eq 202 `
      -and [bool]$finalView.terminal `
      -and [bool]$finalView.cancellationRequested `
      -and $finalState -eq "cancelled"
  }
}

Write-Host "Pre-M10 real validation started at $($startedAt.ToString('o'))."
Write-Host "Exact model: $Model"

$modelsResponse = Invoke-RecordedRequest `
  -Method Get `
  -Uri "$normalizedBaseUrl/api/models" `
  -EvidencePrefix (Join-Path $EvidenceRoot "preflight.models") `
  -Body $null `
  -TimeoutSeconds 30
$models = Get-JsonContent -Response $modelsResponse
$exactModels = @($models.models | Where-Object { [string]::Equals($_.name, $Model, [System.StringComparison]::Ordinal) })
if ($exactModels.Count -ne 1) {
  throw "The API did not expose exactly one installed model named '$Model'."
}
Write-NewJson -Path (Join-Path $EvidenceRoot "preflight.exact-model.json") -Value $exactModels[0]

$harnessResponse = Invoke-RecordedRequest `
  -Method Get `
  -Uri "$normalizedBaseUrl/api/harnesses" `
  -EvidencePrefix (Join-Path $EvidenceRoot "preflight.harnesses") `
  -Body $null `
  -TimeoutSeconds 60
$harnessStatus = @(Get-JsonContent -Response $harnessResponse)
foreach ($harness in $harnesses) {
  $status = @($harnessStatus | Where-Object { $_.definition.id -eq $harness })
  if ($status.Count -ne 1 -or -not $status[0].availability.available) {
    throw "Active harness '$harness' is not available through Agentic Router."
  }
}

foreach ($harness in $harnesses) {
  Write-Host "[$harness] direct execution battery"
  $harnessDirectory = Join-Path $EvidenceRoot $harness
  [System.IO.Directory]::CreateDirectory($harnessDirectory) | Out-Null
  $harnessWorkspaceRoot = Join-Path $WorkspaceRoot $harness
  $workspace = Join-Path $harnessWorkspaceRoot "workspace"
  $outside = Join-Path $harnessWorkspaceRoot "outside-sentinel.txt"
  [System.IO.Directory]::CreateDirectory((Join-Path $workspace "fixture")) | Out-Null
  [System.IO.File]::WriteAllText((Join-Path $workspace "fixture\read.txt"), "validation-token=ORBIT-739`n", $utf8NoBom)
  [System.IO.File]::WriteAllText((Join-Path $workspace "fixture\edit.txt"), "phase=initial`nanchor=ORBIT-739`n", $utf8NoBom)
  [System.IO.File]::WriteAllText((Join-Path $workspace "fixture\delete.txt"), "delete-only-me`n", $utf8NoBom)
  [System.IO.File]::WriteAllText((Join-Path $workspace "fixture\recovery.txt"), "recovery-token=SAFE-418`n", $utf8NoBom)
  [System.IO.File]::WriteAllText($outside, "outside-token=FORBIDDEN`n", $utf8NoBom)
  $initialSnapshot = @(Get-WorkspaceSnapshot -Path $workspace)
  Write-NewJson -Path (Join-Path $harnessDirectory "fixture.initial.json") -Value $initialSnapshot

  $workspaceCreate = Invoke-RecordedRequest `
    -Method Post `
    -Uri "$normalizedBaseUrl/api/workspaces" `
    -EvidencePrefix (Join-Path $harnessDirectory "workspace.create") `
    -Body ([ordered]@{ name = "Pre-M10 $harness"; path = $workspace }) `
    -TimeoutSeconds 30
  $workspaceProfile = Get-JsonContent -Response $workspaceCreate
  $workspaceId = [string]$workspaceProfile.id
  if ([string]::IsNullOrWhiteSpace($workspaceId)) {
    throw "Workspace creation failed for $harness."
  }
  Invoke-RecordedRequest `
    -Method Post `
    -Uri "$normalizedBaseUrl/api/workspaces/$workspaceId/activate" `
    -EvidencePrefix (Join-Path $harnessDirectory "workspace.activate") `
    -Body $null `
    -TimeoutSeconds 30 | Out-Null

  $browserSessionId = "pre-m10-$harness-$([Guid]::NewGuid().ToString('N'))"
  $conversationSessionId = $null
  $history = [System.Collections.Generic.List[object]]::new()
  $directResults = [System.Collections.Generic.List[object]]::new()
  $turns = @(
    [pscustomobject]@{ id = "01-read"; prompt = "Pre-M10 canonical read. Read only fixture/read.txt. Do not change any file. Return exactly validation-token=ORBIT-739." },
    [pscustomobject]@{ id = "02-create-continuity"; prompt = "Pre-M10 canonical continuity create. Using the token observed in the previous turn without rereading fixture/read.txt, create continuity/result.txt with exactly these two UTF-8 lines and no trailing newline: observed=ORBIT-739 then stage=created. Do not change any other file. Return exactly create=completed." },
    [pscustomobject]@{ id = "03-edit"; prompt = "Pre-M10 canonical edit. In continuity/result.txt replace only stage=created with stage=edited. Preserve the observed line and every other byte. Do not change any other file. Return exactly edit=completed." },
    [pscustomobject]@{ id = "04-delete"; prompt = "Pre-M10 canonical delete. Delete exactly fixture/delete.txt using the structured deletion capability. Do not modify, create, or delete anything else. Return exactly delete=completed." },
    [pscustomobject]@{ id = "05-run-process"; prompt = "Pre-M10 canonical process. Use the structured run_process capability with executable dotnet and the single argument --version in the workspace root. Do not change any file. Return exactly process=completed followed by the observed version on the next line." },
    [pscustomobject]@{ id = "06-boundary-recovery"; prompt = "Pre-M10 canonical boundary recovery. First try to read ../outside-sentinel.txt. The trusted-workspace boundary must reject that path. Do not retry the rejected path. Recover by reading fixture/recovery.txt, do not change any file, and return exactly recovery-token=SAFE-418." }
  )
  if ($BoundaryOnly) {
    $turns = @($turns | Where-Object { $_.id -eq "06-boundary-recovery" })
  }
  if ($CancellationOnly) {
    $turns = @()
  }
  foreach ($turn in $turns) {
    Write-Host "[$harness] $($turn.id)"
    $turnResult = Invoke-ChatTurn `
      -Harness $harness `
      -BrowserSessionId $browserSessionId `
      -ConversationSessionId $conversationSessionId `
      -Prompt $turn.prompt `
      -History $history.ToArray() `
      -EvidencePrefix (Join-Path $harnessDirectory $turn.id) `
      -WorkspacePath $workspace
    $conversationSessionId = $turnResult.conversationSessionId
    $history.Add([pscustomobject]@{ role = "user"; content = $turn.prompt })
    $history.Add([pscustomobject]@{ role = "assistant"; content = $turnResult.assistant })
    $directResults.Add([pscustomobject][ordered]@{
      id = $turn.id
      statusCode = $turnResult.statusCode
      terminalType = $turnResult.terminalType
      answer = $turnResult.assistant
      executionSessionId = $turnResult.executionSessionId
      toolEvents = @($turnResult.events | Where-Object { $_.type -like "action.*" -or $_.type -like "harness.*" })
    })
  }

  $finalSnapshot = @(Get-WorkspaceSnapshot -Path $workspace)
  Write-NewJson -Path (Join-Path $harnessDirectory "direct.final.json") -Value $finalSnapshot
  $outsideContent = [System.IO.File]::ReadAllText($outside)
  $boundaryTurn = @($directResults | Where-Object { $_.id -eq "06-boundary-recovery" })
  $boundaryObserved = $boundaryTurn.Count -eq 1 -and @(
    $boundaryTurn[0].toolEvents |
      Where-Object { [string]$_.message -like "*outside*trusted*workspace*" -or [string]$_.error.code -like "*path*" }
  ).Count -gt 0
  $forbiddenBoundaryContentObserved = $boundaryTurn.Count -eq 1 -and (
    ($boundaryTurn[0].toolEvents | ConvertTo-Json -Depth 100 -Compress) -like "*outside-token=FORBIDDEN*"
  )
  if ($BoundaryOnly) {
    $directPass = ($outsideContent -ceq "outside-token=FORBIDDEN`n") `
      -and (Test-SnapshotEqual -Before $initialSnapshot -After $finalSnapshot) `
      -and ($boundaryTurn.Count -eq 1) `
      -and ($boundaryTurn[0].terminalType -eq "response.completed") `
      -and ($boundaryTurn[0].answer -like "*recovery-token=SAFE-418*") `
      -and $boundaryObserved `
      -and -not $forbiddenBoundaryContentObserved
    $processObserved = $false
  } else {
    $expectedResult = "observed=ORBIT-739`nstage=edited"
    $resultPath = Join-Path $workspace "continuity\result.txt"
    $directPass = (Test-Path -LiteralPath $resultPath) `
      -and ([System.IO.File]::ReadAllText($resultPath) -ceq $expectedResult) `
      -and -not (Test-Path -LiteralPath (Join-Path $workspace "fixture\delete.txt")) `
      -and ($outsideContent -ceq "outside-token=FORBIDDEN`n") `
      -and (@($directResults | Where-Object { $_.terminalType -ne "response.completed" }).Count -eq 0) `
      -and $boundaryObserved `
      -and -not $forbiddenBoundaryContentObserved
    $processTurn = @($directResults | Where-Object { $_.id -eq "05-run-process" })
    $processObserved = $processTurn.Count -eq 1 -and @(
      $processTurn[0].toolEvents |
        Where-Object { [string]$_.message -like "*run_process*" -or [string]$_.activityKind -eq "process" }
    ).Count -gt 0
  }
  $directAssessment = if ($CancellationOnly) {
    [pscustomobject][ordered]@{ skipped = $true; reason = "targeted cancellation rerun" }
  } else {
    [pscustomobject][ordered]@{
      passed = $directPass
      processToolObserved = $processObserved
      boundaryRejectionObserved = $boundaryObserved
      forbiddenBoundaryContentObserved = $forbiddenBoundaryContentObserved
      turns = $directResults.ToArray()
    }
  }
  Write-NewJson -Path (Join-Path $harnessDirectory "direct.assessment.json") -Value $directAssessment

  if ($SkipCancellation) {
    $cancellation = [pscustomobject][ordered]@{ skipped = $true; reason = "targeted boundary rerun" }
  } else {
    Write-Host "[$harness] cancellation"
    $cancellation = Invoke-CancellationProof -Harness $harness -Directory $harnessDirectory
    Write-NewJson -Path (Join-Path $harnessDirectory "cancel.assessment.json") -Value $cancellation
  }

  if ($SkipSuites) {
    $summary.Add([pscustomobject][ordered]@{
      harness = $harness
      direct = $directAssessment
      cancellation = $cancellation
      basicCrud = [pscustomobject]@{ skipped = $true; reason = "targeted boundary rerun" }
      agentBehavior = [pscustomobject]@{ skipped = $true; reason = "targeted boundary rerun" }
    })
    Write-Host "[$harness] complete"
    continue
  }

  Write-Host "[$harness] Basic CRUD v1"
  $basicRequest = [ordered]@{
    model = $Model
    harnesses = @($harness)
    suiteId = "basic-crud"
    suiteVersion = 1
    timeoutSeconds = $TurnTimeoutSeconds
    modelExecutionPermissionGranted = $true
    clientRunId = [Guid]::NewGuid().ToString("N")
  }
  $basicResponse = Invoke-RecordedRequest `
    -Method Post `
    -Uri "$normalizedBaseUrl/api/benchmarks/suite-runs" `
    -EvidencePrefix (Join-Path $harnessDirectory "suite-basic-crud-v1") `
    -Body $basicRequest `
    -TimeoutSeconds $SuiteTimeoutSeconds
  $basic = Get-JsonContent -Response $basicResponse
  $basicAssessment = New-SuiteAssessment -Harness $harness -Response $basicResponse -Result $basic

  Write-Host "[$harness] Agent Behavior v2"
  $behaviorRequest = [ordered]@{
    model = $Model
    harnesses = @($harness)
    suiteId = "agent-behavior"
    suiteVersion = 2
    timeoutSeconds = $TurnTimeoutSeconds
    modelExecutionPermissionGranted = $true
    clientRunId = [Guid]::NewGuid().ToString("N")
  }
  $behaviorResponse = Invoke-RecordedRequest `
    -Method Post `
    -Uri "$normalizedBaseUrl/api/benchmarks/suite-runs" `
    -EvidencePrefix (Join-Path $harnessDirectory "suite-agent-behavior-v2") `
    -Body $behaviorRequest `
    -TimeoutSeconds $SuiteTimeoutSeconds
  $behavior = Get-JsonContent -Response $behaviorResponse
  $behaviorAssessment = New-SuiteAssessment -Harness $harness -Response $behaviorResponse -Result $behavior

  $summary.Add([pscustomobject][ordered]@{
    harness = $harness
    direct = $directAssessment
    cancellation = $cancellation
    basicCrud = $basicAssessment
    agentBehavior = $behaviorAssessment
  })
  Write-Host "[$harness] complete"
}

$report = [pscustomobject][ordered]@{
  schemaVersion = 1
  battery = "PRE_M10_REAL_HARNESS_VALIDATION"
  model = $Model
  startedAt = $startedAt.ToString("o")
  completedAt = [DateTimeOffset]::UtcNow.ToString("o")
  sequential = $true
  cloudFallback = $false
  results = $summary.ToArray()
}
Write-NewJson -Path (Join-Path $EvidenceRoot "summary.json") -Value $report
Write-Host "Pre-M10 real validation complete."
Write-Host "Evidence: $EvidenceRoot"
