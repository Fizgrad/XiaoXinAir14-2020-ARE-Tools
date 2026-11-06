param([ValidateRange(1,10)][int]$Samples = 3)

# Only reuse an already-running InpOut driver. Never install/start a driver.
# The only hardware memory address used here is the ECMM region in this BIOS's DSDT.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$model = (Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 10).Model
$bios = (Get-CimInstance Win32_BIOS -OperationTimeoutSec 10).SMBIOSBIOSVersion
if ($model -ne '81YN' -or $bios -ne 'DTCN27WW(V1.13)') {
    throw "This probe was reviewed only for 81YN / DTCN27WW(V1.13); found $model / $bios"
}
$driver = Get-CimInstance Win32_SystemDriver -Filter "Name='inpoutx64'" -OperationTimeoutSec 10
if ($driver.State -ne 'Running') { throw 'Existing inpoutx64 driver is not running; probe will not install or start it.' }

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ReadOnlyFanProbe {
    [StructLayout(LayoutKind.Sequential, Pack=1)]
    private struct Mapping {
        public IntPtr SectionHandle;
        public UIntPtr Size;
        public IntPtr Physical;
        public IntPtr Linear;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    public static byte[] ReadEcmm() {
        if (IntPtr.Size != 8 || Marshal.SizeOf(typeof(Mapping)) != 32)
            throw new InvalidOperationException("Requires x64 and the reviewed 32-byte mapping structure.");
        using (var device = CreateFile(@"\\.\inpoutx64", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open existing inpoutx64");
            var mapping = new Mapping { Size = new UIntPtr(256), Physical = new IntPtr(0xFE0B0800L) };
            var buffer = Marshal.AllocHGlobal(32);
            bool mapped = false;
            try {
                Marshal.StructureToPtr(mapping, buffer, false);
                uint returned;
                // InpOut IOCTL_WINIO_MAPPHYSTOLIN and IOCTL_WINIO_UNMAPPHYSADDR only.
                if (!DeviceIoControl(device, unchecked((uint)-1673519076), buffer, 32, buffer, 32, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Map DSDT ECMM");
                mapping = (Mapping)Marshal.PtrToStructure(buffer, typeof(Mapping));
                mapped = mapping.Linear != IntPtr.Zero;
                if (returned != 32 || !mapped) throw new InvalidOperationException("Incomplete mapping response.");
                var data = new byte[256];
                // Marshal.Copy source is the mapping: this only reads hardware memory.
                Marshal.Copy(mapping.Linear, data, 0, data.Length);
                return data;
            } finally {
                try {
                    if (mapped) {
                        uint returned;
                        if (!DeviceIoControl(device, unchecked((uint)-1673519072), buffer, 32, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unmap DSDT ECMM");
                    }
                } finally { Marshal.FreeHGlobal(buffer); }
            }
        }
    }
    public static int ReadDustState() {
        using (var device = CreateFile(@"\\.\EnergyDrv", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
            if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open existing EnergyDrv");
            var input = Marshal.AllocHGlobal(4);
            var output = Marshal.AllocHGlobal(4);
            try {
                Marshal.WriteInt32(input, 14);
                uint returned;
                if (!DeviceIoControl(device, 0x831020C4, input, 4, output, 4, out returned, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Read dust state");
                if (returned != 4) throw new InvalidOperationException("Incomplete dust state response.");
                return Marshal.ReadInt32(output);
            } finally { Marshal.FreeHGlobal(input); Marshal.FreeHGlobal(output); }
        }
    }
}
'@
$results = @()
for ($i = 0; $i -lt $Samples; $i++) {
    $item = [ordered]@{ Time = (Get-Date).ToString('o'); Model = $model; Bios = $bios }
    try {
        $data = [ReadOnlyFanProbe]::ReadEcmm()
        $item.EcmmReadSucceeded = $true
        $item.AllZero = @($data | Where-Object { $_ -ne 0 }).Count -eq 0
        $item.AllFF = @($data | Where-Object { $_ -ne 255 }).Count -eq 0
        $item.RawHex = [BitConverter]::ToString($data)
        # Field names and locations are proven by DSDT; ERPM's RPM scaling is unverified.
        $item.Fields = [ordered]@{
            ERPM_Raw = [int]$data[6]
            FNCM_Raw = [int]$data[15]
            QTMD = [int](($data[16] -shr 2) -band 1)
            AUPM = [int](($data[16] -shr 3) -band 1)
            AQMD = [int](($data[16] -shr 4) -band 1)
            STMD = [int](($data[16] -shr 5) -band 1)
            TCNL_Raw = [int]$data[0xAA]
            FAN1 = [int](($data[0xAB] -shr 4) -band 1)
            FAN2 = [int](($data[0xAB] -shr 5) -band 1)
            FANT = [int](($data[0xAB] -shr 6) -band 1)
            FSSN_Raw = [int]($data[0xAD] -band 15)
            FANU_Raw = [int](($data[0xAD] -shr 4) -band 15)
            FFEN = [int](($data[0xBA] -shr 4) -band 1)
            FFST = [int](($data[0xBA] -shr 5) -band 1)
            CTMP_Raw = [int]$data[0xB0]
            CTML_Raw = [int]$data[0xB1]
        }
        $item.ProfileInference = 'Unknown'
        if (-not $item.AllZero -and -not $item.AllFF -and $item.Fields.AUPM -eq 0 -and $item.Fields.AQMD -eq 0) {
            if ($item.Fields.STMD -eq 1 -and $item.Fields.QTMD -eq 0) { $item.ProfileInference = 'IntelligentCooling' }
            elseif ($item.Fields.STMD -eq 0 -and $item.Fields.QTMD -eq 1) { $item.ProfileInference = 'BatterySaving' }
            elseif ($item.Fields.STMD -eq 0 -and $item.Fields.QTMD -eq 0) { $item.ProfileInference = 'ExtremePerformance' }
        }
    } catch { $item.EcmmReadSucceeded = $false; $item.EcmmError = $_.Exception.Message }
    try { $item.DustStateRaw = [ReadOnlyFanProbe]::ReadDustState() }
    catch { $item.DustStateError = $_.Exception.Message }
    $results += $item
    if ($i + 1 -lt $Samples) { Start-Sleep -Milliseconds 1000 }
}
$directory = Join-Path $PSScriptRoot 'exploration'
$null = New-Item -ItemType Directory -Path $directory -Force
$json = $results | ConvertTo-Json -Depth 6
$json | Set-Content -LiteralPath (Join-Path $directory 'fan-settings.json') -Encoding UTF8
$json
