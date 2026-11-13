param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedVersion = (([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'FanController.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1) + '.0'
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('FanController-InstallerCheck-' + $testId)
$installationPath = Join-Path $testRoot 'app'
$groupName = 'FanController-InstallerCheck-' + $testId
$taskName = 'FanController.Startup.InstallerCheck-' + $testId
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AD2273CC-8077-4C44-A805-446B62D28DBE}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An existing FanController installation must not be modified by this smoke test.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$setup = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$installationPath`"", "/GROUP=`"$groupName`"", '/TASKS=', "/LOG=`"$(Join-Path $testRoot 'install.log')`"") -WindowStyle Hidden -Wait -PassThru
if ($setup.ExitCode -ne 0) { throw ('Installer failed with exit code ' + $setup.ExitCode) }
$appPath = Join-Path $installationPath 'FanController.exe'
$shortcut = $null
try {
    foreach ($required in @('FanController.exe', 'FanController.dll', 'coreclr.dll', 'PresentationFramework.dll', 'unins000.exe', 'tools\ryzenadj\ryzenadj.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $installationPath $required) -PathType Leaf)) { throw ('Installed file missing: ' + $required) }
    }
    $wsh = New-Object -ComObject WScript.Shell
    foreach ($programsRoot in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('Programs'))) {
        if (!$programsRoot -or !(Test-Path -LiteralPath $programsRoot)) { continue }
        foreach ($candidate in @(Get-ChildItem -LiteralPath $programsRoot -Filter 'FanController.lnk' -Recurse -File)) {
            if ($wsh.CreateShortcut($candidate.FullName).TargetPath -eq $appPath) { $shortcut = $candidate.FullName; break }
        }
        if ($shortcut) { break }
    }
    if (!$shortcut) { throw 'Start menu shortcut targeting the installed application was not found' }
    Write-Output ('Verified start menu shortcut: ' + $shortcut)
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installationPath 'FanController.dll')).FileVersion
    if ($version -ne $expectedVersion) { throw ('Unexpected installed version: ' + $version) }
    if (!(Test-Path -LiteralPath $uninstallKey)) { throw 'Windows uninstall entry is missing' }
    $shortcutInfo = $wsh.CreateShortcut($shortcut)
    if ($shortcutInfo.TargetPath -ne $appPath) { throw 'Start menu shortcut targets the wrong executable' }

    $scheduler = New-Object -ComObject Schedule.Service; $scheduler.Connect(); $folder = $scheduler.GetFolder('\')
    $definition = $scheduler.NewTask(0)
    $definition.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $definition.Principal.LogonType = 3; $definition.Principal.RunLevel = 1
    $definition.Settings.DisallowStartIfOnBatteries = $false; $definition.Settings.StopIfGoingOnBatteries = $false
    $action = $definition.Actions.Create(0); $action.Path = $appPath; $action.Arguments = '--startup'
    $folder.RegisterTaskDefinition($taskName, $definition, 6, $null, $null, 3, $null) | Out-Null
    $uninstaller = Start-Process -FilePath (Join-Path $installationPath 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $testRoot 'uninstall.log')`"") -WindowStyle Hidden -Wait -PassThru
    if ($uninstaller.ExitCode -ne 0) { throw ('Uninstaller failed with exit code ' + $uninstaller.ExitCode) }
    if (Test-Path -LiteralPath $appPath) { throw 'Uninstall left the application executable' }
    if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left the start menu shortcut' }
    if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall entry was left behind' }
    $remaining = @($folder.GetTasks(1) | Where-Object { $_.Name -eq $taskName })
    if ($remaining.Count -gt 0) { throw 'Uninstall left the application logon task' }
    Write-Output 'PASS installer: self-contained runtime, application files, shortcuts, uninstall entry and scheduled-task cleanup'
}
catch {
    foreach ($logName in @('install.log','uninstall.log')) {
        $logPath = Join-Path $testRoot $logName
        if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 70 | Write-Output }
    }
    throw
}
finally {
    if ($folder) { try { $folder.DeleteTask($taskName, 0) } catch { } }
    if (Test-Path -LiteralPath (Join-Path $installationPath 'unins000.exe')) {
        Start-Process -FilePath (Join-Path $installationPath 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait | Out-Null
    }
}
