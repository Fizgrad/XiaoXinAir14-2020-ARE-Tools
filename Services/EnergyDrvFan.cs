using System.IO;
using System.Runtime.InteropServices;

namespace FanController.Services;

public enum FanControlMode { Normal, DustCleaning }

/// <summary>单次原生除尘请求；不提供手动转速或 PWM 控制。</summary>
public static class EnergyDrvFan
{
    public const int ModeNormal = 0;
    public const int ModeDustCleaning = 1;
    public static TimeSpan DustCycleDuration { get; } = TimeSpan.FromSeconds(11);
    private static readonly EnergyDrvController Controller = new(new NativeEnergyDrvTransport());
    /// <summary>running 表示当前一次除尘尚未结束。</summary>
    public static event Action<bool, string>? StateChanged
    {
        add => Controller.StateChanged += value;
        remove => Controller.StateChanged -= value;
    }
    public static bool DriverPresent => Controller.DriverPresent;
    public static bool IsRunning => Controller.IsRunning;
    public static FanControlMode CurrentMode => Controller.CurrentMode;
    public static string LastMessage => Controller.LastMessage;
    public static bool RestorePending => Controller.RestorePending;
    public static int? ReadState() => Controller.ReadState();
    public static bool StartDustCleaning() => Controller.StartDustCleaning();
    public static void Stop(string? reason = null) => Controller.Stop(reason);
}

internal interface IEnergyDrvTransport
{
    bool IsAvailable { get; }
    bool SetMode(int mode);
    int? ReadState();
}

/// <summary>所有命令串行化；会话代号使旧定时器不能再写入或复位新模式。</summary>
internal sealed class EnergyDrvController
{
    private readonly object _gate = new(), _eventGate = new();
    private readonly IEnergyDrvTransport _transport;
    private readonly TimeProvider _time;
    private readonly bool _automaticTick;
    private ITimer? _timer;
    private FanControlMode _mode;
    private string _lastMessage = "正常（自动调速）";
    private bool _restorePending;
    private long _generation, _notificationVersion, _startedAt;
    private readonly record struct Notice(long Version, bool Running, string Message);

    internal EnergyDrvController(IEnergyDrvTransport transport, TimeProvider? time = null,
        bool automaticTick = true)
    {
        _transport = transport;
        _time = time ?? TimeProvider.System;
        _automaticTick = automaticTick;
    }
    public event Action<bool, string>? StateChanged;
    public bool DriverPresent { get { lock (_gate) return Available(); } }
    public bool IsRunning { get { lock (_gate) return _mode != FanControlMode.Normal; } }
    public FanControlMode CurrentMode { get { lock (_gate) return _mode; } }
    public string LastMessage { get { lock (_gate) return _lastMessage; } }
    public bool RestorePending { get { lock (_gate) return _restorePending; } }
    public int? ReadState() { lock (_gate) return Read(); }

    public bool StartDustCleaning()
    {
        Notice? notice = null;
        bool success;
        lock (_gate)
        {
            // A second click must never queue another cycle, rearm the firmware,
            // or extend the deadline of the current request.
            if (_mode == FanControlMode.DustCleaning)
            {
                notice = Message("本次除尘尚未结束，未重复触发或排队");
                success = false;
            }
            else if (!Available())
            {
                notice = Message("无法开始除尘：EnergyDrv 驱动不可用");
                success = false;
            }
            else
            {
                CancelTimer();
                _mode = FanControlMode.Normal;
                if (!Restore())
                {
                    notice = Message("恢复正常调速失败，未开始除尘；请点击「停止除尘」重试");
                    success = false;
                }
                else
                {
                    try
                    {
                        // Only one native start request. No repeated start or fan-speed hold.
                        if (!Write(EnergyDrvFan.ModeDustCleaning))
                            throw new InvalidOperationException("除尘启动请求失败");
                        _mode = FanControlMode.DustCleaning;
                        _restorePending = false;
                        _startedAt = _time.GetTimestamp();
                        if (_automaticTick)
                        {
                            var generation = _generation;
                            var period = TimeSpan.FromMilliseconds(100);
                            _timer = _time.CreateTimer(_ => Tick(generation), null, period, period);
                        }
                        notice = Message("一次除尘请求已发送，约 11 秒后结束；执行期间不会重复触发");
                        success = true;
                    }
                    catch (Exception ex) { notice = StopLocked(ex.Message); success = false; }
                }
            }
        }
        Publish(notice);
        return success;
    }

    /// <summary>中止本次除尘或重试恢复；启动程序本身不调用此方法。</summary>
    public void Stop(string? reason = null)
    {
        Notice notice;
        lock (_gate) notice = StopLocked(reason ?? "已停止除尘");
        Publish(notice);
    }

    internal void Tick() => Tick(null); // deterministic tests use a manual clock
    private void Tick(long? generation)
    {
        Notice? notice = null;
        lock (_gate)
        {
            if ((generation.HasValue && generation.Value != _generation) || _mode == FanControlMode.Normal) return;
            try
            {
                long now = _time.GetTimestamp();
                if (_time.GetElapsedTime(_startedAt, now) >= EnergyDrvFan.DustCycleDuration)
                    notice = StopLocked("本次除尘请求已结束");
            }
            catch (Exception ex) { notice = StopLocked("风扇控制异常：" + ex.Message); }
        }
        Publish(notice);
    }
    private Notice StopLocked(string reason)
    {
        CancelTimer();
        _mode = FanControlMode.Normal;
        return Message(Restore() ? reason + "；已发送正常调速请求" : reason + "；恢复请求失败，请点击「停止除尘」重试");
    }
    private void CancelTimer()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }
    private bool Restore()
    {
        for (int i = 0; i < 3; i++)
            if (Write(EnergyDrvFan.ModeNormal)) { _restorePending = false; return true; }
        _restorePending = true;
        return false;
    }
    private bool Write(int mode) { try { return _transport.SetMode(mode); } catch { return false; } }
    private int? Read() { try { return _transport.ReadState(); } catch { return null; } }
    private bool Available() { try { return _transport.IsAvailable; } catch { return false; } }
    private Notice Message(string message)
    {
        _lastMessage = message;
        return new Notice(++_notificationVersion, _mode != FanControlMode.Normal, message);
    }
    private void Publish(Notice? notice)
    {
        if (notice is not Notice value) return;
        lock (_eventGate)
        {
            Action<bool, string>? handlers;
            lock (_gate)
            {
                if (value.Version != _notificationVersion) return;
                handlers = StateChanged;
            }
            if (handlers == null) return;
            foreach (Action<bool, string> handler in handlers.GetInvocationList())
                try { handler(value.Running, value.Message); } catch { /* UI 异常不能终止控制定时器。 */ }
        }
    }
}

internal sealed class NativeEnergyDrvTransport : IEnergyDrvTransport
{
    private const string DeviceName = "\\\\.\\EnergyDrv";
    private const uint IoctSetMode = 0x831020C0, IoctGetState = 0x831020C4;
    public bool IsAvailable
    {
        get
        {
            using var handle = CreateFile(DeviceName, FileAccess.Read, FileShare.ReadWrite,
                IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
            return !handle.IsInvalid;
        }
    }
    public bool SetMode(int mode)
    {
        if (mode is not (EnergyDrvFan.ModeNormal or EnergyDrvFan.ModeDustCleaning))
            throw new ArgumentOutOfRangeException(nameof(mode));
        using var handle = CreateFile(DeviceName, FileAccess.Write, FileShare.ReadWrite,
            IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        return DeviceIoControl(handle, IoctSetMode, new[] { 6, 1, mode }, 12, null, 0, out _, IntPtr.Zero);
    }
    public int? ReadState()
    {
        using var handle = CreateFile(DeviceName, FileAccess.Read, FileShare.ReadWrite,
            IntPtr.Zero, FileMode.Open, FileAttributes.Normal, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var output = new int[1];
        return DeviceIoControl(handle, IoctGetState, new[] { 14 }, 4, output, 4, out var returned, IntPtr.Zero)
            && returned == 4 ? output[0] : null;
    }
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
