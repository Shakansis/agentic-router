param(
  [string]$Model = 'qwen3:4b-instruct',
  [string]$OllamaUrl = 'http://127.0.0.1:11434',
  [int]$Repetitions = 3,
  [string]$OutputDirectory = 'TestResults/native-multitool/real',
  [ValidateSet('source-read','investigation')][string]$BenchmarkTask = 'source-read',
  [string]$DevSettingsPath = 'AgenticRouter.Api/data/settings.json',
  [ValidateRange(1,100)][int]$StartRepetition = 1
)

# Opt-in real inference. Uses installed models only, an isolated Host/workspace,
# and never restarts the configured Ollama server or an existing AR instance.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
[void](New-Item -ItemType Directory -Path $outputRoot -Force)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('ar-native-benchmark-' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $temporaryRoot 'data'
$workspaceRoot = Join-Path $temporaryRoot 'workspace'
[void](New-Item -ItemType Directory -Path $dataRoot -Force)
if ($BenchmarkTask -eq 'investigation') {
  & (Join-Path $PSScriptRoot 'New-NativeInvestigationFixture.ps1') -Destination $workspaceRoot |
    ConvertTo-Json | Set-Content (Join-Path $outputRoot 'fixture.json') -Encoding utf8
} else {
  [void](New-Item -ItemType Directory -Path $workspaceRoot)
  Copy-Item -LiteralPath (Join-Path $repoRoot 'AgenticRouter.Api/Execution/NativeToolScheduler.cs') -Destination (Join-Path $workspaceRoot 'Scheduler.cs')
  Copy-Item -LiteralPath (Join-Path $repoRoot 'AgenticRouter.Api/Execution/ToolExecutionBatch.cs') -Destination (Join-Path $workspaceRoot 'Batch.cs')
  Copy-Item -LiteralPath (Join-Path $repoRoot 'AgenticRouter.Api/Execution/ExecutionTurnToolPolicy.cs') -Destination (Join-Path $workspaceRoot 'Policy.cs')
}
$residentBefore = (Invoke-RestMethod "$OllamaUrl/api/ps").models
if ($residentBefore.Count -gt 0 -and $Model -notin $residentBefore.name) {
  throw 'A different model is resident. Reuse it explicitly or wait; this script will not compete for VRAM.'
}
if ($Model -notin (Invoke-RestMethod "$OllamaUrl/api/tags").models.name) { throw 'Model must already be installed.' }
$profile = @{ minimumContextTokens = 4096; targetContextTokens = 16384; maximumContextTokens = 16384; outputTokenLimit = 2048; keepAlive = 300 }
$settings = @{
  schemaVersion = 1; ollamaUrl = $OllamaUrl; routerModel = 'none'; actionModel = 'none'
  coordinatorModel = $Model; defaultModel = $Model; trustedWorkspacePath = $workspaceRoot
  context = @{ defaultContextTokens = 16384; providerContextTokens = 32768; reservedResponseTokens = 2048; maxConversationMessages = 40 }
  runtime = @{ generationTimeoutSeconds = 180 }
  ollamaRuntime = @{ roleDefaults = @{ router = $profile; residentCoordinator = $profile; specialist = $profile; primary = $profile; fallback = $profile; benchmark = $profile; modelTest = $profile; webSearchSynthesis = $profile; visionRequest = $profile } }
  execution = @{ parallelToolExecution = 'off'; maxConcurrentToolCalls = 4 }
  onboarding = @{ showBeforeNewConversation = $false }
}
$settings.intentions = @{}
foreach ($intention in @('general-chat', 'documentation', 'software-development', 'software-architecture', 'rpg-storytelling', 'review-and-testing')) {
  $settings.intentions[$intention] = @{ model = 'default'; fallbackModel = 'none'; gpu = 'default'; systemPrompt = 'Analyze the requested code and report only verified source facts.' }
}
if ($BenchmarkTask -eq 'investigation') {
  $devPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $DevSettingsPath))
  $dev = Get-Content -LiteralPath $devPath -Raw | ConvertFrom-Json -AsHashtable
  $devHash = (Get-FileHash -LiteralPath $devPath).Hash
  # Copy only local execution configuration; never copy cloud secrets or user history.
  foreach ($key in 'defaultGpu','modelGpuAffinities','context','runtime','ollamaRuntime','execution','inferenceProfiles','intentions','projectAwareness') {
    $settings[$key] = $dev[$key]
  }
  $settings.execution.parallelToolExecution = 'off'
  $settings.execution.maxConcurrentToolCalls = 4
  $settings.onboarding.showBeforeNewConversation = $false
  $task = @{
    thinking = $dev.inferenceProfiles.'software-development'.thinking
    objective = @'
Software development investigation: analyze incident INC-241 in this isolated OrderDesk repository. Daily history and exports omit late-evening orders after release 2.18; stored rows still exist.
Start with README.md and docs/incidents/INC-241.md. Follow the documented request/export flow and compare source, tenant/feature configuration, time contracts and operational logs. Search for the incident trace IDs and relevant range-construction/filtering symbols. Inspect at least eight relevant files across source, documentation, configuration and logs before concluding. Log files are long: use search_text to find the relevant region and bounded read_file ranges rather than dumping whole logs.
Explain the investigation flow, cite inspected paths and observations, distinguish evidence from hypotheses, and suggest one focused next verification. Keep the final report under 220 words. Do not change files, execute commands, run tests, use web search or ask the user for input. This is an inspection-only task.
Use list_files, search_text, read_file and get_file_info as needed. Do not use read_files: all three benchmark variants compare individual native tool calls. Their schemas are already offered; do not request them again. Include a short progress introduction in the same response as the first call. Continue after introductions until the investigation is complete.
'@
  }
  $taskPath = Join-Path $outputRoot 'task.json'
  $task | ConvertTo-Json -Depth 5 | Set-Content $taskPath -Encoding utf8
  $env:AR_NATIVE_BENCHMARK_TASK_FILE = $taskPath
  [ordered]@{ sourceSettingsSha256=$devHash; model=$Model; defaultGpu=$settings.defaultGpu; context=$settings.context;
    inferenceProfiles=$dev.inferenceProfiles; profileSelection='Normal Host intention routing; sampling is not overridden'; runtime=$settings.ollamaRuntime;
    comparisonChanges=@('isolated workspace/data','explicit Native model/direct strategy','router disabled','Off versus Auto, limit 4') } |
    ConvertTo-Json -Depth 15 | Set-Content (Join-Path $outputRoot 'dev-profile.json') -Encoding utf8
  Get-ChildItem $workspaceRoot -Recurse -File | ForEach-Object {
    [pscustomobject]@{path=[IO.Path]::GetRelativePath($workspaceRoot,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash}
  } | ConvertTo-Json | Set-Content (Join-Path $outputRoot 'fixture-manifest.json') -Encoding utf8
  Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'AgenticRouter|ollama|llama-server' } |
    Select-Object ProcessId,ParentProcessId,Name,CreationDate | ConvertTo-Json |
    Set-Content (Join-Path $outputRoot 'processes-before.json') -Encoding utf8
}
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $dataRoot 'settings.json') -Encoding utf8
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$endpoint = "http://127.0.0.1:$port/"
$priorData = $env:AgenticRouter__DataDirectory
$apiProcess = $null
$modelLoadedByBenchmark = $false
try {
  $env:AgenticRouter__DataDirectory = $dataRoot
  $apiProcess = Start-Process -FilePath (Join-Path $repoRoot 'AgenticRouter.Api/bin/Release/net10.0/AgenticRouter.Api.exe') `
    -ArgumentList @('--urls', $endpoint) -WorkingDirectory (Join-Path $repoRoot 'AgenticRouter.Api') `
    -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outputRoot 'host.log') -RedirectStandardError (Join-Path $outputRoot 'host-error.log')
  $env:AgenticRouter__DataDirectory = $priorData
  $ready = $false
  for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $apiProcess.Refresh()
    if ($apiProcess.HasExited) { break }
    try { $null = Invoke-RestMethod ($endpoint + 'api/settings'); $ready = $true; break } catch { Start-Sleep -Milliseconds 500 }
  }
  if (!$ready) { throw 'Isolated Host did not become ready. Inspect host.log.' }
  if ($BenchmarkTask -eq 'investigation') {
    # Warm through the same Host/runtime resolver: preserve DEV GPU, F16, context and MTP settings.
    $warmup = @{ model=$Model; message='Software development: inspect README.md and report ready. Use one read_file call with a short introduction. Do not modify files or run commands.';
      interactionMode='execute'; harness='native'; approvalPolicy='auto'; executionStrategy='direct'; thinking=$task.thinking; browserSessionId=[guid]::NewGuid().ToString('N') }
    $warmupResult = (Invoke-WebRequest -Method Post -Uri ($endpoint+'api/chat/stream') -ContentType application/json -Body ($warmup|ConvertTo-Json) -TimeoutSec 900).Content
    $warmupResult | Set-Content (Join-Path $outputRoot 'warmup.sse') -Encoding utf8
    if ($warmupResult -match '"type"\s*:\s*"error"') { throw 'Host warmup failed; inspect warmup.sse before measuring.' }
    Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'llama-server.exe' } |
      Select-Object ProcessId,ParentProcessId,CommandLine | ConvertTo-Json -Depth 4 |
      Set-Content (Join-Path $outputRoot 'runner.json') -Encoding utf8
  } else {
    # Exclude cold loading from A/B/C/D; each turn still records load/TTFT telemetry.
    $warmup = @{ model = $Model; prompt = 'Reply OK.'; stream = $false; think = $false; keep_alive = 300; options = @{ num_ctx = 16384; num_predict = 8 } }
    $modelLoadedByBenchmark = $Model -notin $residentBefore.name
    Invoke-RestMethod -Method Post -Uri "$OllamaUrl/api/generate" -ContentType application/json -Body ($warmup | ConvertTo-Json -Depth 5) -TimeoutSec 180 |
      ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'warmup.json') -Encoding utf8
  }
  $env:AR_NATIVE_BENCHMARK_URL = $endpoint
  $env:AR_NATIVE_BENCHMARK_MODEL = $Model
  $env:AR_NATIVE_BENCHMARK_OUTPUT = Join-Path $outputRoot 'measurements.json'
  $env:AR_NATIVE_BENCHMARK_REPETITIONS = "$Repetitions"
  $env:AR_NATIVE_BENCHMARK_START_REPETITION = "$StartRepetition"
  dotnet test (Join-Path $repoRoot 'tests/AgenticRouter.EndToEndTests/AgenticRouter.EndToEndTests.csproj') -c Release --no-build `
    --settings (Join-Path $repoRoot 'playwright.runsettings') --filter 'FullyQualifiedName~RealNativeMultiToolBenchmarkEndToEndTests' `
    --logger 'trx;LogFileName=real-native-benchmark.trx' --results-directory $outputRoot --disable-build-servers
  if ($LASTEXITCODE -ne 0) { throw 'Real benchmark did not pass; inspect measurements and TRX.' }
}
finally {
  $env:AgenticRouter__DataDirectory = $priorData
  Remove-Item Env:AR_NATIVE_BENCHMARK_URL, Env:AR_NATIVE_BENCHMARK_MODEL, Env:AR_NATIVE_BENCHMARK_OUTPUT, Env:AR_NATIVE_BENCHMARK_REPETITIONS, Env:AR_NATIVE_BENCHMARK_TASK_FILE, Env:AR_NATIVE_BENCHMARK_START_REPETITION -ErrorAction SilentlyContinue
  if ($apiProcess -and !$apiProcess.HasExited) { $apiProcess.Kill($true); $apiProcess.WaitForExit() }
  if ($modelLoadedByBenchmark) {
    $unload = @{ model = $Model; keep_alive = 0 }
    Invoke-RestMethod -Method Post -Uri "$OllamaUrl/api/generate" -ContentType application/json -Body ($unload | ConvertTo-Json) | Out-Null
  }
  if ($BenchmarkTask -eq 'investigation' -and (Get-FileHash -LiteralPath $devPath).Hash -ne $devHash) {
    Write-Warning 'DEV settings changed externally during the benchmark; they were not restored or overwritten.'
  }
  Write-Output "Evidence: $outputRoot"
  Write-Output "Isolated data/workspace retained for inspection: $temporaryRoot"
}
