using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FanController.Services;

public enum ChargingMode { Unknown, Normal, Conservation, Rapid }
public enum DeviceSwitch { FnLock, UsbAc, UsbBattery, Backlight }

public sealed record LenovoDeviceState(uint? BatteryMask, uint? DeviceMask, string Message)
{
    public bool BatteryAvailable => BatteryMask.HasValue;
    public bool RapidSupported => (BatteryMask.GetValueOrDefault() & 0x20000) != 0;
    public bool ConflictingChargeModes => (BatteryMask.GetValueOrDefault() & 0x24) == 0x24;
    public ChargingMode Charging => !BatteryAvailable || ConflictingChargeModes ? ChargingMode.Unknown
        : (BatteryMask!.Value & 0x20) != 0 ? ChargingMode.Conservation
        : (BatteryMask.Value & 4) != 0 ? ChargingMode.Rapid : ChargingMode.Normal;
    public bool Supported(DeviceSwitch feature) => DeviceMask.HasValue && (DeviceMask.Value & SupportBit(feature)) != 0;
    public bool? Value(DeviceSwitch feature) => Supported(feature) ? (DeviceMask!.Value & StateBit(feature)) != 0 : null;
    internal static uint SupportBit(DeviceSwitch feature) => feature switch
    { DeviceSwitch.FnLock => 0x200, DeviceSwitch.UsbAc => 0x40, DeviceSwitch.UsbBattery => 0x4000, DeviceSwitch.Backlight => 0x10, _ => 0 };
    internal static uint StateBit(DeviceSwitch feature) => feature switch
    { DeviceSwitch.FnLock => 0x400, DeviceSwitch.UsbAc => 0x80, DeviceSwitch.UsbBattery => 0x8000, DeviceSwitch.Backlight => 0x20, _ => 0 };
}

/// <summary>81YN / DTCN27WW 的 GBMD/SBMC、HALS/SALS 接口；不直接写 EC。</summary>
public sealed class LenovoDeviceService
{
    internal const uint BatteryIoctl = 0x831020F8, DeviceIoctl = 0x831020E8;
    private readonly IEnergyTransport _transport;
    private readonly Lazy<(bool Supported, string Description)> _identity;
    private readonly object _gate = new();
    public LenovoDeviceState State { get; private set; } = new(null, null, "尚未读取设备设置");
    public LenovoDeviceService() : this(new NativeEnergyTransport(), ThermalProfileService.ReadMachineIdentity) { }
    internal LenovoDeviceService(IEnergyTransport transport, Func<(bool Supported, string Description)> identity)
    { _transport = transport; _identity = new(identity); }

    public LenovoDeviceState Refresh()
    {
        lock (_gate)
        {
            try
            {
                if (!_identity.Value.Supported) return State = new(null, null, "机型或 BIOS 未适配：" + _identity.Value.Description);
                var errors = new List<string>();
                uint? battery = Query(BatteryIoctl, 0xFF, errors);
                uint? device = Query(DeviceIoctl, 2, errors);
                return State = new(battery, device, errors.Count == 0 ? "设备状态已读取" : string.Join("；", errors));
            }
            catch (Exception ex) { return State = new(null, null, "读取失败：" + ex.Message); }
        }
    }

    private uint? Query(uint ioctl, byte command, List<string> errors)
    {
        if (_transport.TryCall(ioctl, command, true, out uint value, out string error) && value != uint.MaxValue) return value;
        errors.Add((ioctl == BatteryIoctl ? "电池接口：" : "设备接口：") + (error.Length > 0 ? error : "固件返回无效状态"));
        return null;
    }

    public bool SetChargingMode(ChargingMode mode)
    {
        lock (_gate)
        {
            if (mode is not (ChargingMode.Normal or ChargingMode.Conservation or ChargingMode.Rapid)) return Fail("无效充电模式");
            var before = Refresh();
            if (!before.BatteryAvailable || (mode == ChargingMode.Rapid && !before.RapidSupported)) return Fail("当前充电模式不受支持：" + before.Message);
            // This model's DSDT implements SBMC(3/5/7/8). Disable the other mode first.
            byte[] commands = mode switch
            {
                ChargingMode.Conservation => before.RapidSupported ? new byte[] { 8, 3 } : new byte[] { 3 },
                ChargingMode.Rapid => new byte[] { 5, 7 },
                _ => before.RapidSupported ? new byte[] { 8, 5 } : new byte[] { 5 },
            };
            foreach (byte command in commands)
                if (!_transport.TryCall(BatteryIoctl, command, false, out _, out string error))
                { Refresh(); return Fail("充电模式设置中断，已重新读取实际状态：" + error); }
            if (Refresh().Charging != mode) return Fail("充电模式回读未匹配，请查看实际状态");
            State = State with { Message = "充电模式已设置并回读确认" };
            return true;
        }
    }

    public bool SetSwitch(DeviceSwitch feature, bool enabled)
    {
        lock (_gate)
        {
            var before = Refresh();
            if (!before.Supported(feature)) return Fail("本机未报告支持此设备开关");
            byte command = feature switch
            {
                DeviceSwitch.FnLock => (byte)(enabled ? 14 : 15),
                DeviceSwitch.UsbAc => (byte)(enabled ? 10 : 11),
                DeviceSwitch.UsbBattery => (byte)(enabled ? 19 : 18),
                DeviceSwitch.Backlight => (byte)(enabled ? 8 : 9),
                _ => throw new ArgumentOutOfRangeException(nameof(feature)),
            };
            if (!_transport.TryCall(DeviceIoctl, command, false, out _, out string error))
            { Refresh(); return Fail("设备设置失败：" + error); }
            if (Refresh().Value(feature) != enabled) return Fail("设备设置回读未匹配，请查看实际状态");
            State = State with { Message = "设备开关已设置并回读确认" };
            return true;
        }
    }

    private bool Fail(string message) { State = State with { Message = message }; return false; }
}

internal interface IEnergyTransport
{
    bool TryCall(uint ioctl, byte command, bool query, out uint reply, out string error);
}

internal sealed class NativeEnergyTransport : IEnergyTransport
{
    public bool TryCall(uint ioctl, byte command, bool query, out uint reply, out string error)
    {
        reply = 0;
        using var handle = CreateFile("\\\\.\\EnergyDrv", 0xC0000000, FileShare.ReadWrite,
            IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
        if (handle.IsInvalid) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
        var output = new byte[4];
        bool ok = DeviceIoControl(handle, ioctl, new[] { command }, 1, output, 4, out uint returned, IntPtr.Zero);
        int nativeError = ok ? 0 : Marshal.GetLastWin32Error();
        // Local driver requires a 4-byte output buffer, but setters return zero bytes.
        if (!ok || (query ? returned != 4 : returned is not (0 or 4)))
        { error = !ok ? new Win32Exception(nativeError).Message : $"驱动返回长度异常：{returned}"; return false; }
        reply = BitConverter.ToUInt32(output); error = ""; return true;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode creation, FileAttributes attributes, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
