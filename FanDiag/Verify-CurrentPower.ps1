param([switch]$VerifyUnchanged, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$checksPath = Join-Path $PSScriptRoot 'FeatureChecks\bin\Release\net8.0-windows\FeatureChecks.dll'
$executablePath = Join-Path $repoPath 'tools\ryzenadj\ryzenadj.exe'
if (-not (Test-Path -LiteralPath $checksPath)) { throw 'Build FanDiag/FeatureChecks first.' }
$mode = if ($VerifyUnchanged) { '--power-verify-current' } else { '--power-readonly' }
# Default: read only. -VerifyUnchanged writes back only freshly queried limits.
$output = & dotnet $checksPath $mode $executablePath 2>&1
$resultCode = $LASTEXITCODE
$report = [pscustomobject]@{ Time = [DateTimeOffset]::Now.ToString('O'); Mode = $mode; ExitCode = $resultCode; Output = ($output -join [Environment]::NewLine) }
$json = $report | ConvertTo-Json
if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json) }
$json
exit $resultCode
