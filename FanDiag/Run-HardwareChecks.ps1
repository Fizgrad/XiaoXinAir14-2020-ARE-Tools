param([switch]$Run, [string]$OutputDirectory, [string]$Filter)
$ErrorActionPreference = 'Stop'
if (-not $Run) { throw 'Use -Run to exercise real settings and UI actions. The suite restores captured hardware settings and uses an isolated configuration directory.' }
$repoPath = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $PSScriptRoot 'HardwareChecks\bin\Release\net8.0-windows\HardwareChecks.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build HardwareChecks first.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('exploration\hardware-checks-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$checkArguments = @('--run', $repoPath, $OutputDirectory)
if ($Filter) { $checkArguments += $Filter }
& $executable @checkArguments
exit $LASTEXITCODE
