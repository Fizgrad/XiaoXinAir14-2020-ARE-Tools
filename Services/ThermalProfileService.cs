using System.ComponentModel;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FanController.Services;

public enum ThermalProfile
{
    Unknown = 0,
    ExtremePerformance = 2,
    BatterySaving = 3,
    IntelligentCooling = 15,
}

/// <summary>Fn+Q 散热策略；显式设置后保留固件设置，不在启动或退出时自动写入。</summary>
public sealed class ThermalProfileService
{
    private readonly object _gate = new();
    private readonly IThermalProfileTransport _transport;
    private readonly Func<(bool Supported, string Description)> _readIdentity;
    private bool? _identitySupported;
    private string _identityDescription = "";
    private bool _supported;
    private ThermalProfile _current = ThermalProfile.Unknown;
    private string _lastMessage = "Fn+Q 模式尚未读取";

    // Construction performs no WMI queries, device opens, or firmware calls.
    public ThermalProfileService() : this(new NativeThermalProfileTransport(), ReadMachineIdentity) { }

    internal ThermalProfileService(IThermalProfileTransport transport,
        Func<(bool Supported, string Description)> readIdentity)
    {
        _transport = transport;
        _readIdentity = readIdentity;
    }

    public bool IsSupported { get { lock (_gate) return _supported; } }
    public ThermalProfile CurrentProfile { get { lock (_gate) return _current; } }
    public string LastMessage { get { lock (_gate) return _lastMessage; } }

    public bool Refresh()
    {
        lock (_gate)
        {
            try
            {
                if (!EnsureSupported() || !ReadCurrent(out var profile)) return false;
                _current = profile;
                _lastMessage = "当前 Fn+Q 模式：" + DisplayName(profile);
                return true;
            }
            catch (Exception ex) { return Fail("读取 Fn+Q 模式失败：" + ex.Message); }
        }
    }

    public bool SetProfile(ThermalProfile profile)
    {
        lock (_gate)
        {
            uint command = profile switch
            {
                ThermalProfile.IntelligentCooling => 0x000FB001,
                ThermalProfile.ExtremePerformance => 0x0012B001,
                ThermalProfile.BatterySaving => 0x0013B001,
                _ => 0,
            };
            if (command == 0) return Fail("不支持的 Fn+Q 模式，未发送设置请求");
            try
            {
                if (!EnsureSupported()) return false;
                if (!_transport.TryCall(command, out uint reply, out string error))
                    return Fail("Fn+Q 设置请求失败：" + error);
                if ((reply & 0xFF) != 1)
                    return Fail($"Fn+Q 设置未被固件接受（返回 0x{reply:X8}）");
                if (!ReadCurrent(out var actual)) return false;
                if (actual != profile)
                    return Fail($"Fn+Q 设置未确认：请求{DisplayName(profile)}，固件回读为{DisplayName(actual)}");
                _current = actual;
                _lastMessage = "已切换 Fn+Q 模式：" + DisplayName(actual);
                return true;
            }
            catch (Exception ex) { return Fail("设置 Fn+Q 模式失败：" + ex.Message); }
        }
    }

    private bool EnsureSupported()
    {
        if (!_identitySupported.HasValue)
        {
            var identity = _readIdentity();
            _identitySupported = identity.Supported;
            _identityDescription = identity.Description;
        }
        if (_identitySupported != true)
            return Fail("当前机型或 BIOS 未适配 Fn+Q：" + _identityDescription);
        if (_supported) return true;

        // DYTC(0) is a capability query. DYTC(2) is deliberately never used:
        // this machine's GET branch also reapplies CPU power and thermal settings.
        if (!_transport.TryCall(0, out uint capability, out string error))
            return Fail("读取 Fn+Q 接口能力失败：" + error);
        if ((capability & 0xFF) != 1 || (capability & 0x100) == 0 || (capability >> 28) != 5)
            return Fail($"Fn+Q 接口返回异常（0x{capability:X8}）");
        _supported = true;
        return true;
    }

    private bool ReadCurrent(out ThermalProfile profile)
    {
        profile = ThermalProfile.Unknown;
        // Local DTCN27WW DSDT proves DYTC(7) only reads mode flags.
        if (!_transport.TryCall(7, out uint reply, out string error))
            return Fail("Fn+Q 模式回读失败：" + error);
        if ((reply & 0xFFFF) != 1)
            return Fail($"Fn+Q 模式回读无效（返回 0x{reply:X8}）");
        var candidate = (ThermalProfile)(reply >> 16);
        if (candidate is not (ThermalProfile.IntelligentCooling or ThermalProfile.ExtremePerformance or ThermalProfile.BatterySaving))
            return Fail($"固件返回尚未识别的 Fn+Q 模式（0x{reply:X8}）");
        profile = candidate;
        return true;
    }

    private bool Fail(string message)
    {
        _current = ThermalProfile.Unknown;
        _lastMessage = message;
        return false;
    }

    private static string DisplayName(ThermalProfile profile) => profile switch
    {
        ThermalProfile.IntelligentCooling => "智能散热",
        ThermalProfile.ExtremePerformance => "野兽／性能",
        ThermalProfile.BatterySaving => "节能／静音",
        _ => "未知",
    };

    internal static (bool Supported, string Description) ReadMachineIdentity()
    {
        string manufacturer = "", model = "", bios = "";
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) };
        using (var searcher = new ManagementObjectSearcher(new ManagementScope("root\\cimv2"),
            new ObjectQuery("SELECT Manufacturer,Model FROM Win32_ComputerSystem"), options))
        using (var results = searcher.Get())
        {
            foreach (ManagementBaseObject item in results)
            using (item)
            {
                manufacturer = Convert.ToString(item["Manufacturer"])?.Trim() ?? "";
                model = Convert.ToString(item["Model"])?.Trim() ?? "";
                break;
            }
        }
        using (var searcher = new ManagementObjectSearcher(new ManagementScope("root\\cimv2"),
            new ObjectQuery("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"), options))
        using (var results = searcher.Get())
        {
            foreach (ManagementBaseObject item in results)
            using (item)
            {
                bios = Convert.ToString(item["SMBIOSBIOSVersion"])?.Trim() ?? "";
                break;
            }
        }
        bool supported = manufacturer.Equals("LENOVO", StringComparison.OrdinalIgnoreCase)
            && model == "81YN" && bios == "DTCN27WW(V1.13)";
        return (supported, $"{manufacturer} {model} / {bios}");
    }
}

internal interface IThermalProfileTransport
{
    bool TryCall(uint command, out uint reply, out string error);
}

internal sealed class NativeThermalProfileTransport : IThermalProfileTransport
{
    internal const uint IoctlDytc = 0x8310213C;

    public bool TryCall(uint command, out uint reply, out string error)
    {
        reply = 0;
        using var handle = CreateFile("\\\\.\\EnergyDrv", 0xC0000000, FileShare.ReadWrite,
            IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            error = $"无法打开 EnergyDrv（Win32 {code}：{new Win32Exception(code).Message}）";
            return false;
        }
        var input = new[] { command };
        var output = new uint[1];
        // SET calls also require a four-byte output buffer in this driver.
        bool success = DeviceIoControl(handle, IoctlDytc, input, 4, output, 4, out uint returned, IntPtr.Zero);
        int nativeError = success ? 0 : Marshal.GetLastWin32Error();
        return ValidateResponse(success, returned, output[0], nativeError, out reply, out error);
    }

    internal static bool ValidateResponse(bool success, uint returned, uint raw, int nativeError,
        out uint reply, out string error)
    {
        reply = 0;
        if (!success)
        {
            error = $"EnergyDrv 调用失败（Win32 {nativeError}：{new Win32Exception(nativeError).Message}）";
            return false;
        }
        if (returned != 4)
        {
            error = $"EnergyDrv 返回长度异常：{returned} 字节（应为 4）";
            return false;
        }
        reply = raw;
        error = "";
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
        IntPtr security, FileMode creation, FileAttributes attributes, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        uint[] input, uint inputSize, uint[] output, uint outputSize,
        out uint returned, IntPtr overlapped);
}
