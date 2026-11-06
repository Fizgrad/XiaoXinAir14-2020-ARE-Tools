param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'exploration'))

# Read-only discovery: no driver installation, EC writes, or WMI method calls.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$report = [ordered]@{ CapturedAt = (Get-Date).ToString('o') }
function Read-Query([string]$Namespace, [string]$Class, [string[]]$Properties) {
    try {
        $items = @(Get-CimInstance -Namespace $Namespace -ClassName $Class -OperationTimeoutSec 15)
        if ($Properties) { return @($items | Select-Object -Property $Properties) }
        return @($items | Select-Object -Property * -ExcludeProperty CimClass,CimInstanceProperties,CimSystemProperties,PSComputerName)
    } catch { return @{ Error = $_.Exception.Message } }
}
$report.Computer = Read-Query 'root/cimv2' 'Win32_ComputerSystem' @('Manufacturer','Model','SystemFamily')
$report.Bios = Read-Query 'root/cimv2' 'Win32_BIOS' @('Manufacturer','SMBIOSBIOSVersion','ReleaseDate','EmbeddedControllerMajorVersion','EmbeddedControllerMinorVersion')
$report.Cpu = Read-Query 'root/cimv2' 'Win32_Processor' @('Name')
$report.OS = Read-Query 'root/cimv2' 'Win32_OperatingSystem' @('Caption','Version','BuildNumber')
$report.Fan = Read-Query 'root/cimv2' 'Win32_Fan' @('Name','Status','DesiredSpeed','VariableSpeed','Availability')
$report.ThermalZones = Read-Query 'root/wmi' 'MSAcpi_ThermalZoneTemperature' @('InstanceName','CurrentTemperature','ActiveTripPoint','ActiveTripPointCount')
$report.WmiClasses = @()
foreach ($namespace in @('root/wmi','root/Lenovo')) {
    try {
        $classes = @(Get-CimClass -Namespace $namespace -OperationTimeoutSec 20 | Where-Object {
            $_.CimClassName -match 'Lenovo|IdeaPad|GameZone|Fan|Thermal|Energy|DYTC'
        })
        foreach ($class in $classes) {
            $report.WmiClasses += [ordered]@{
                Namespace = $namespace
                Class = $class.CimClassName
                Properties = @($class.CimClassProperties | ForEach-Object { "$($_.Name):$($_.CimType)" })
                Methods = @($class.CimClassMethods | ForEach-Object {
                    @{ Name = $_.Name; Parameters = @($_.Parameters | ForEach-Object { "$($_.Name):$($_.CimType)" }) }
                })
            }
        }
    } catch { $report["NamespaceError:$namespace"] = $_.Exception.Message }
}
try {
    $report.Drivers = @(Get-CimInstance Win32_SystemDriver -OperationTimeoutSec 15 | Where-Object {
        $_.Name -match 'Energy|Lenovo|ACPI|WinRing|Pawn|InpOut' -or $_.DisplayName -match 'Lenovo|Energy'
    } | Select-Object Name,DisplayName,State,PathName)
} catch { $report.DriverError = $_.Exception.Message }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FanFirmwareTables {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern uint EnumSystemFirmwareTables(uint provider, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[] data, uint size);
}
'@
$report.FirmwareTables = @()
$report.RegistryAcpiTables = @()
foreach ($signature in @('DSDT','SSDT')) {
    try {
        $index = 0
        foreach ($key in @(Get-ChildItem "HKLM:\HARDWARE\ACPI\$signature" -Recurse)) {
            foreach ($valueName in $key.GetValueNames()) {
                $value = $key.GetValue($valueName)
                if ($value -isnot [byte[]] -or $value.Length -lt 36) { continue }
                if ([Text.Encoding]::ASCII.GetString($value, 0, 4) -ne $signature) { continue }
                $path = Join-Path $OutputDirectory "$signature-registry-$index.aml"
                [IO.File]::WriteAllBytes($path, $value)
                $report.RegistryAcpiTables += @{ Signature = $signature; Length = $value.Length; File = $path }
                $index++
            }
        }
    } catch { $report["RegistryError:$signature"] = $_.Exception.Message }
}
$provider = [uint32]0x41435049 # ACPI
$seenIds = @{}
$size = [FanFirmwareTables]::EnumSystemFirmwareTables($provider, $null, 0)
if ($size -gt 0) {
    $ids = [byte[]]::new($size)
    $actual = [FanFirmwareTables]::EnumSystemFirmwareTables($provider, $ids, $size)
    for ($offset = 0; $offset + 4 -le $actual; $offset += 4) {
        $id = [BitConverter]::ToUInt32($ids, $offset)
        $length = [FanFirmwareTables]::GetSystemFirmwareTable($provider, $id, $null, 0)
        if ($length -eq 0 -or $length -gt 4194304) { continue }
        $bytes = [byte[]]::new($length)
        $received = [FanFirmwareTables]::GetSystemFirmwareTable($provider, $id, $bytes, $length)
        if ($received -ne $length) { continue }
        $signature = [Text.Encoding]::ASCII.GetString($bytes, 0, 4)
        $entry = [ordered]@{ Signature = $signature; Length = $length; TableId = $id }
        if ($signature -match '^(DSDT|SSDT|FACP|ECDT|WSMT)$' -and -not $seenIds.ContainsKey($id)) {
            $path = Join-Path $OutputDirectory "$signature-$offset.aml"
            [IO.File]::WriteAllBytes($path, $bytes)
            $entry.File = $path
        }
        $seenIds[$id] = $true
        $report.FirmwareTables += $entry
    }
} else { $report.FirmwareError = [Runtime.InteropServices.Marshal]::GetLastWin32Error() }
$json = $report | ConvertTo-Json -Depth 12
$json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'interfaces.json') -Encoding UTF8
$json
