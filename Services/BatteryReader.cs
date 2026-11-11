using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FanController.Services;

public sealed record BatteryReading
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public string DeviceId { get; init; } = "";
    public float? Percent { get; init; }
    public float? PowerW { get; init; } // Positive = discharge, negative = charge.
    public float? DesignWh { get; init; }
    public float? FullChargeWh { get; init; }
    public float? RemainingWh { get; init; }
    public float? HealthPercent { get; init; }
    public uint? Cycles { get; init; }
    public bool? PowerOnline { get; init; }
    public string Message { get; init; } = "电池详细数据不可用";
}

/// <summary>Windows battery-class read-only IOCTLs; no optional kernel driver is needed.</summary>
public static class BatteryReader
{
    private static readonly Guid BatteryGuid = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    public static BatteryReading Read()
    {
        var fallback = ReadSystemStatus();
        IntPtr devices = IntPtr.Zero;
        try
        {
            var guid = BatteryGuid;
            devices = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (devices == new IntPtr(-1)) return fallback;
            for (uint index = 0; index < 16; index++)
            {
                var data = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, index, ref data)) break;
                SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
                if (size is < 8 or > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (path == null) continue;
                    using var handle = CreateFile(path, 0x80000000, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var tagData = Query(handle, 0x294040, new byte[4], 4);
                    if (tagData == null) continue;
                    uint tag = BitConverter.ToUInt32(tagData);
                    if (tag == 0) continue;
                    var infoRequest = new byte[12];
                    BitConverter.GetBytes(tag).CopyTo(infoRequest, 0);
                    var info = Query(handle, 0x294044, infoRequest, 36);
                    if (info == null || (BitConverter.ToUInt32(info) & 0x80000000) == 0) continue;
                    var statusRequest = new byte[20];
                    BitConverter.GetBytes(tag).CopyTo(statusRequest, 0);
                    var status = Query(handle, 0x29404C, statusRequest, 16);
                    if (status != null) return Decode(info, status, fallback, path + "#" + tag);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        catch (Exception ex) { return fallback with { Message = "电池详细读取失败：" + ex.Message }; }
        finally { if (devices != IntPtr.Zero && devices != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(devices); }
        return fallback;
    }

    internal static BatteryReading Decode(byte[] info, byte[] status, BatteryReading fallback, string deviceId)
    {
        if (info.Length != 36 || status.Length != 16) return fallback;
        uint capabilities = BitConverter.ToUInt32(info), design = BitConverter.ToUInt32(info, 12),
            full = BitConverter.ToUInt32(info, 16), cycles = BitConverter.ToUInt32(info, 32),
            state = BitConverter.ToUInt32(status), remaining = BitConverter.ToUInt32(status, 4);
        int rate = BitConverter.ToInt32(status, 12);
        bool absolute = (capabilities & 0x40000000) == 0;
        static bool Known(uint v) => v > 0 && v != uint.MaxValue;
        return new BatteryReading
        {
            Time = fallback.Time, DeviceId = deviceId,
            Percent = Known(full) && remaining != uint.MaxValue ? Math.Clamp(remaining * 100f / full, 0, 100) : fallback.Percent,
            PowerW = absolute && rate != int.MinValue ? -rate / 1000f : null,
            DesignWh = absolute && Known(design) ? design / 1000f : null,
            FullChargeWh = absolute && Known(full) ? full / 1000f : null,
            RemainingWh = absolute && remaining != uint.MaxValue ? remaining / 1000f : null,
            HealthPercent = Known(design) && Known(full) ? full * 100f / design : null,
            Cycles = Known(cycles) ? cycles : null, PowerOnline = (state & 1) != 0,
            Message = absolute ? "Windows 电池接口；正值为放电，负值为充电" : "电池仅报告相对容量，瓦数与 Wh 不可用",
        };
    }

    public static bool? PowerOnline() => GetSystemPowerStatus(out var s) && s.AcLineStatus != 255 ? s.AcLineStatus == 1 : null;
    private static BatteryReading ReadSystemStatus()
    {
        if (!GetSystemPowerStatus(out var s)) return new();
        return new() { Percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null,
            PowerOnline = s.AcLineStatus == 255 ? null : s.AcLineStatus == 1 };
    }
    private static byte[]? Query(SafeFileHandle handle, uint code, byte[] input, uint outputSize)
    {
        var output = new byte[outputSize];
        return DeviceIoControl(handle, code, input, (uint)input.Length, output, outputSize, out uint returned, IntPtr.Zero)
            && returned == outputSize ? output : null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInterfaceData { public int Size; public Guid Guid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus
    { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode creation, FileAttributes attributes, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
