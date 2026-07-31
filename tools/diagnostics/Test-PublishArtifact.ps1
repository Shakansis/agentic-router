[CmdletBinding()]
param(
  [Parameter(Mandatory)]
  [string]$PublishPath
)

$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $PublishPath).Path
$forbiddenPatterns = @(
  '*.ps1',
  '*FakeOllama*',
  '*EndToEndTests*',
  '*.jsonl',
  'settings.json',
  'workspaces.json',
  'model-organization.json',
  'secrets',
  'diagnostics'
)
$violations = foreach ($item in Get-ChildItem -LiteralPath $resolved -Recurse -Force) {
  foreach ($pattern in $forbiddenPatterns) {
    if ($item.Name -like $pattern) {
      $item.FullName
      break
    }
  }
}

if ($violations) {
  $violations | ForEach-Object { Write-Error "Forbidden publish artifact: $_" }
  exit 1
}

Write-Output "Publish artifact verified: no diagnostics, tests, fake-provider assets, secrets, or local data were found."
