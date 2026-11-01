using System.IO;
using System.Runtime.InteropServices;

namespace FanController.Services;

/// <summary>
/// 通过联想官方 EnergyDrv 内核驱动（Lenovo ACPI-Compliant Virtual Power Controller，
/// 随联想电脑管家 / Energy Manager 安装）复用“除尘模式”实现风扇全速散热。
/// 协议参考开源项目 Lenovo-Fan-Control (MIT)：IOCTL 0x831020C0，输入 [6,1,mode]。
/// 除尘模式本身是“全速约 9 秒 → 停 2 秒”的循环，这里每 8.5 秒重新触发一次保持持续全速。
/// </summary>
public static class EnergyDrvFan
{
    private const string DeviceName = "\\\\.\\EnergyDrv";
    private const uint IoctSetMode = 0x831020C0;
    private const uint IoctGetState = 0x831020C4;

    public const int ModeNormal = 0;
    public const int ModeDustFullSpeed = 1;

    private static readonly object Gate = new();
    private static Thread? _loop;
    private static volatile bool _running;
    private static int _normalState = int.MinValue;
    private static DateTime _startedAt;

    /// <summary>全速循环状态变化（参数：是否正在全速，说明文字）</summary>
    public static event Action<bool, string>? StateChanged;

    public static bool DriverPresent
    {
        get
        {
            try
            {
                var h = CreateFile(DeviceName, FileAccess.Read, FileShare.ReadWrite,
                    IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
                if (h.IsInvalid) return false;
                h.Close();
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>写风扇模式（0=正常 1=除尘全速）</summary>
    public static bool SetMode(int mode)
    {
        try
        {
            using var h = CreateFile(DeviceName, FileAccess.Write, FileShare.ReadWrite,
                IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
            if (h.IsInvalid) return false;

            var inBuffer = new int[] { 6, 1, mode };
            return DeviceIoControl(h, IoctSetMode, inBuffer, (uint)(inBuffer.Length * sizeof(int)),
                null, 0, out _, IntPtr.Zero);
        }
        catch { return false; }
    }

    /// <summary>查询当前状态值（数值含义随机型 BIOS 而异，启动时记录正常值作基线）</summary>
    public static int? ReadState()
    {
        try
        {
            using var h = CreateFile(DeviceName, FileAccess.Read, FileShare.ReadWrite,
                IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
            if (h.IsInvalid) return null;

            var inBuffer = new int[] { 14 };
            var outBuffer = new int[1];
            if (!DeviceIoControl(h, IoctGetState, inBuffer, sizeof(int),
                    outBuffer, sizeof(int), out _, IntPtr.Zero)) return null;
            return outBuffer[0];
        }
        catch { return null; }
    }

    /// <summary>开启持续全速散热（除尘循环保持）</summary>
    public static bool StartFullSpeed(int maxMinutes)
    {
        lock (Gate)
        {
            if (_running) return true;
            if (!DriverPresent) return false;

            // 记录“正常模式”基线，用于判断除尘是否生效
            SetMode(ModeNormal);
            _normalState = ReadState() ?? int.MinValue;
            _running = true;
            _startedAt = DateTime.Now;

            _loop = new Thread(() => DustLoop(maxMinutes))
            {
                IsBackground = true,
                Name = "EnergyDrvDustLoop",
            };
            _loop.Start();
            StateChanged?.Invoke(true, "全速散热已开启（除尘模式保持）");
            return true;
        }
    }

    private static void DustLoop(int maxMinutes)
    {
        try
        {
            while (_running)
            {
                // 持续触发除尘模式直到状态离开基线
                for (int i = 0; i < 200 && _running; i++)
                {
                    SetMode(ModeDustFullSpeed);
                    var st = ReadState();
                    if (st != null && _normalState != int.MinValue && st != _normalState) break;
                    Thread.Sleep(10);
                }
                if (!_running) break;

                if (maxMinutes > 0 && (DateTime.Now - _startedAt).TotalMinutes > maxMinutes)
                {
                    StopInternal($"已达最长 {maxMinutes} 分钟，自动停止");
                    return;
                }
                Thread.Sleep(7000); // 除尘全速段约 9 秒，提前重触发避免进入停止相位造成掉速
            }
        }
        catch
        {
            StopInternal("除尘循环异常退出，已恢复正常模式");
        }
    }

    /// <summary>停止全速并恢复正常模式</summary>
    public static void Stop(string? reason = null)
    {
        lock (Gate)
        {
            if (!_running) return;
            _running = false;
            StopInternal(reason ?? "已恢复正常模式");
        }
    }

    private static void StopInternal(string reason)
    {
        _running = false;
        try
        {
            for (int i = 0; i < 50; i++)
            {
                SetMode(ModeNormal);
                var st = ReadState();
                if (st != null && st == _normalState) break;
                Thread.Sleep(10);
            }
        }
        catch { }
        StateChanged?.Invoke(false, reason);
    }

    public static bool IsRunning => _running;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName, FileAccess dwDesiredAccess, FileShare dwShareMode,
        IntPtr lpSecurityAttributes, FileMode dwCreationDisposition,
        FileAttributes dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
        int[]? lpInBuffer, uint nInBufferSize, int[]? lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
