param([string]$Destination = (Join-Path $PSScriptRoot 'ryzenadj'))
$ErrorActionPreference = 'Stop'
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ('ryzenadj-v0.19.0-' + [guid]::NewGuid().ToString('N') + '.zip')
$expectedHash = '871734a2322ea61a0d83ea8373162d3e1ecf0411ba4eb7e6aec423f4b6b95b4f'
try {
    Invoke-WebRequest -Uri 'https://github.com/FlyGoat/RyzenAdj/releases/download/v0.19.0/ryzenadj-win64.zip' -OutFile $archivePath
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) { throw 'RyzenAdj archive SHA256 did not match the official release digest.' }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $Destination -Force
    Get-ChildItem -LiteralPath $Destination -Recurse -File | Select-Object FullName, Length
}
finally {
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
}
