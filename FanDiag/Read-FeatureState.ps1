param([switch]$Power, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$checksPath = Join-Path $PSScriptRoot 'FeatureChecks\bin\Release\net8.0-windows\FeatureChecks.dll'
if (-not (Test-Path -LiteralPath $checksPath)) { throw 'Build FanDiag/FeatureChecks first.' }
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('Device and battery query (no setting commands):')
$deviceOutput = & dotnet $checksPath --probe-readonly 2>&1
$lines.Add(($deviceOutput -join [Environment]::NewLine))
if ($Power) {
    $lines.Add('RyzenAdj --info (no power-setting arguments):')
    $powerOutput = & dotnet $checksPath --power-readonly (Join-Path $repoPath 'tools\ryzenadj\ryzenadj.exe') 2>&1
    $lines.Add(($powerOutput -join [Environment]::NewLine))
}
if ($OutputPath) { [IO.File]::WriteAllLines([IO.Path]::GetFullPath($OutputPath), $lines) }
$lines
