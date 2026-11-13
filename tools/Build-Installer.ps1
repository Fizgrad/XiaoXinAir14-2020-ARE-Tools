param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$artifactPath = (Resolve-Path -LiteralPath $OutputDirectory).Path
$version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'FanController.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
foreach ($required in @('FanController.exe', 'FanController.dll', 'coreclr.dll', 'PresentationFramework.dll', 'tools\ryzenadj\ryzenadj.exe', 'tools\ryzenadj\libryzenadj.dll', 'tools\ryzenadj\WinRing0x64.sys')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishPath $required) -PathType Leaf)) { throw ('Self-contained payload is missing ' + $required) }
}
if (!$CompilerPath) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compilerCommand) { $CompilerPath = $compilerCommand.Source }
    else {
        $CompilerPath = @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup 6.5 or newer is required to build the installer.' }
$languagePath = Join-Path $artifactPath 'ChineseSimplified.isl'
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_1/Files/Languages/Unofficial/ChineseSimplified.isl' -OutFile $languagePath
& $CompilerPath "/DAppVersion=$version" "/DPublishDir=$publishPath" "/DArtifactDir=$artifactPath" "/DChineseMessages=$languagePath" (Join-Path $repositoryRoot 'packaging\FanController.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$installer = Join-Path $artifactPath "FanController-v$version-win-x64-Setup.exe"
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer output is missing' }
Get-Item -LiteralPath $installer | Select-Object FullName, Length
