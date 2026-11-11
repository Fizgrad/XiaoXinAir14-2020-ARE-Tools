param([switch]$Run)
$ErrorActionPreference = 'Stop'
if (-not $Run) { throw 'Use -Run to temporarily apply Quiet, run a bounded CPU load, and restore the original limits.' }
$repoPath = Split-Path -Parent $PSScriptRoot
$checksPath = Join-Path $PSScriptRoot 'FeatureChecks\bin\Release\net8.0-windows\FeatureChecks.dll'
$executablePath = Join-Path $repoPath 'tools\ryzenadj\ryzenadj.exe'
$evidencePath = Join-Path $PSScriptRoot 'exploration\power-quiet-live-1.2.1.json'
if (-not (Test-Path -LiteralPath $checksPath)) { throw 'Build FanDiag/FeatureChecks first.' }
& dotnet $checksPath --power-test-quiet $executablePath $evidencePath
exit $LASTEXITCODE
