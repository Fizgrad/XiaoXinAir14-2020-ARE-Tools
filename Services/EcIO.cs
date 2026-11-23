using System.Reflection;

namespace FanController.Services;

/// <summary>
/// 通过 ACPI EC 端口（状态/命令 0x66，数据 0x62）读写 Embedded Controller RAM。
/// 端口 I/O 用 LibreHardwareMonitorLib 0.8.8 内嵌的 WinRing0 驱动；
/// Ring0 类是 internal，这里反射绑定（签名已实测确认：
/// ReadIoPort(uint):byte / WriteIoPort(uint,byte) / WaitIsaBusMutex(int) / ReleaseIsaBusMutex()）。
/// 需要管理员权限。访问前后持有 ISA 总线互斥量，避免与 LHM 传感器轮询冲突。
/// </summary>
public static class EcIO
{
    private const uint StatusPort = 0x66;
    private const uint DataPort = 0x62;

    private const byte CmdRead = 0x80;   // 读 EC RAM，后跟单字节地址

    private const byte FlagObf = 0x01;   // 输出缓冲区满（EC → CPU）
    private const byte FlagIbf = 0x02;   // 输入缓冲区满（CPU → EC）

    private static readonly object Gate = new();

    private static MethodInfo? _readIo, _writeIo, _waitMutex, _releaseMutex;
    private static MethodInfo? _readMemByte, _readMemBytes;
    private static PropertyInfo? _isOpen;
    private static MethodInfo? _open;

    /// <summary>ECMM 共享内存基址（本机 DSDT：SystemMemory 0xFE0B0800，区域长度 0x1000）</summary>
    public const ulong EcmmMailboxBase = 0xFE0B0800;
    /// <summary>ERPM 字段在邮箱内的字节偏移（DSDT Field 解析：bit 48 = byte 6, 8 位宽）</summary>
    public const int EcmmRpmOffset = 6;

    public static bool MmioAvailable { get; private set; }

    public static bool IsAvailable { get; private set; }
    public static string Status { get; private set; } = "未初始化";

    /// <summary>绑定 Ring0（幂等，可在 LHM Computer.Open 之前或之后调用）</summary>
    public static void Init()
    {
        lock (Gate)
        {
            if (IsAvailable) return;
            try
            {
                // 0.8.8 里有两个 Ring0：Interop.Ring0（仅 DllImport 声明）和
                // Hardware.Ring0（真正的驱动封装，含 ReadIoPort/WriteIoPort），按方法存在性选取
                var ring0 = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetTypes()
                    .Where(t => t.Name == "Ring0")
                    .FirstOrDefault(t => t.GetMethod("ReadIoPort",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) != null);
                if (ring0 == null)
                {
                    Status = "未找到可用的 Ring0 类型";
                    return;
                }

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _readIo = ring0.GetMethod("ReadIoPort", F);
                _writeIo = ring0.GetMethod("WriteIoPort", F);
                _waitMutex = ring0.GetMethod("WaitIsaBusMutex", F);
                _releaseMutex = ring0.GetMethod("ReleaseIsaBusMutex", F);
                _isOpen = ring0.GetProperty("IsOpen", F);
                _open = ring0.GetMethod("Open", F);

                if (_readIo == null || _writeIo == null)
                {
                    Status = "Ring0 端口 API 缺失";
                    return;
                }

                // 物理内存读（WinRing0），用于 ECMM 共享内存邮箱
                foreach (var m in ring0.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "ReadMemory" || !m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 2 || ps[0].ParameterType != typeof(ulong)) continue;
                    if (ps[1].ParameterType.IsByRef && ps[1].ParameterType.GetElementType() is { } et)
                    {
                        if (et.IsArray) _readMemBytes ??= m;
                        else _readMemByte ??= m;
                    }
                }
                MmioAvailable = _readMemBytes != null || _readMemByte != null;

                // Computer.Open 会加载驱动；单独运行时确保已打开
                if (_isOpen != null && !(bool)_isOpen.GetValue(null)!) _open?.Invoke(null, null);
                if (_isOpen != null && !(bool)_isOpen.GetValue(null)!)
                {
                    Status = "Ring0 驱动打开失败";
                    return;
                }

                // 先放行再探测（Read 内部有 IsAvailable 门禁）
                IsAvailable = true;
                var probe = Read(0x00);
                if (probe == null)
                {
                    IsAvailable = false;
                    Status = "EC 端口无响应（" + Status + "）";
                }
                else
                {
                    Status = "就绪";
                }
            }
            catch (Exception ex)
            {
                Status = "初始化失败：" + ex.Message;
            }
        }
    }

    private static byte ReadPort(uint port) => (byte)(_readIo!.Invoke(null, new object[] { port }) ?? (byte)0);
    private static void WritePort(uint port, byte value) => _writeIo!.Invoke(null, new object[] { port, value });

    private static bool WaitBit(byte flag, bool set, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool bit = (ReadPort(StatusPort) & flag) != 0;
            if (bit == set) return true;
            Thread.Sleep(0);
        }
        return false;
    }

    /// <summary>解除 Ring0 绑定。MonitorService 释放 LHM Computer 后必须调用，否则 IsAvailable 仍为 true，
    /// 之后的端口访问会在驱动已关闭的情况下失败。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            IsAvailable = false;
            MmioAvailable = false;
            _readIo = _writeIo = _waitMutex = _releaseMutex = null;
            _readMemByte = _readMemBytes = null;
            _isOpen = null; _open = null;
            Status = "未初始化";
        }
    }

    /// <summary>读 EC RAM 一个字节（ACPI EC 协议：0x80 命令 + 单字节地址）。失败（超时）返回 null。</summary>
    public static byte? Read(ushort address)
    {
        if (!IsAvailable) return null;
        lock (Gate)
        {
            try
            {
                Ring0Acquire();
                try
                {
                    if (!WaitBit(FlagIbf, false, 20)) return Fail<byte?>("等 IBF 超时");
                    // 上一次读取超时后，EC 迟到的数据会留在输出缓冲；此时直接发新命令会先读到旧字节，
                    // 之后整段数据悄悄错位一字节。发命令前先确认 OBF 已清，仍有残留就读数据口排空。
                    if (!WaitBit(FlagObf, false, 5))
                    {
                        ReadPort(DataPort);
                        if (!WaitBit(FlagObf, false, 20)) return Fail<byte?>("等 OBF 清空超时");
                    }
                    WritePort(StatusPort, CmdRead);
                    if (!WaitBit(FlagIbf, false, 20)) return Fail<byte?>("等 IBF 超时");
                    WritePort(DataPort, (byte)address);
                    // EC 处理风扇/温度事件的短暂忙碌可持续几十毫秒；20 ms 实测偶发超时（约 1/19 次 dump）。
                    if (!WaitBit(FlagObf, true, 50)) return Fail<byte?>("等 OBF 超时");
                    byte value = ReadPort(DataPort);
                    if (Status != "就绪") Status = "就绪"; // 一次瞬时失败不应让状态行永远停在失败原因
                    return value;
                }
                finally
                {
                    Ring0Release();
                }
            }
            catch (Exception ex)
            {
                return Fail<byte?>("读异常：" + ex.Message);
            }
        }
    }

    /// <summary>读取 EC RAM 前 size 字节（用于 dump 视图）。每个字节最多重试 4 次，重试间隔 15 ms 以跨过 EC 的短暂忙碌；
    /// 任何字节重试后仍失败即返回 null，绝不把失败伪装成 0x00 数据。</summary>
    public static byte[]? Dump(int size)
    {
        if (!IsAvailable) return null;
        var data = new byte[Math.Min(size, 256)];
        for (ushort i = 0; i < data.Length; i++)
        {
            byte? b = null;
            for (int attempt = 0; attempt < 4 && b == null; attempt++)
            {
                b = Read(i);
                if (b == null) Thread.Sleep(15);
            }
            if (b == null)
            {
                Status = $"Dump 在地址 0x{i:X2} 处重试后仍读取失败（{_lastError}）";
                return null;
            }
            data[i] = b.Value;
            Thread.Sleep(1); // 字节间隔，避免 EC 忙不过来
        }
        return data;
    }

    /// <summary>读物理内存（MMIO）一段字节，走 WinRing0 驱动。失败返回 null。</summary>
    public static byte[]? ReadMmio(ulong address, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (!MmioAvailable) return null;
        lock (Gate)
        {
            try
            {
                if (_readMemBytes != null)
                {
                    var gm = _readMemBytes.MakeGenericMethod(typeof(byte));
                    var buf = new byte[count];
                    var args = new object[] { address, buf };
                    if (gm.Invoke(null, args) is not true)
                        return Fail<byte[]?>("MMIO 读取失败：驱动未返回有效数据");
                    return (byte[])args[1];
                }
                var g1 = _readMemByte!.MakeGenericMethod(typeof(byte));
                var data = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    var a = new object[] { address + (ulong)i, (byte)0 };
                    if (g1.Invoke(null, a) is not true)
                        return Fail<byte[]?>("MMIO 读取失败：驱动未返回有效数据");
                    data[i] = (byte)a[1];
                }
                return data;
            }
            catch (Exception ex)
            {
                Status = "MMIO 读取失败：" + (ex.InnerException ?? ex).Message;
                return null;
            }
        }
    }

    private static void Ring0Acquire()
    {
        try { _waitMutex?.Invoke(null, new object[] { 100 }); } catch { }
    }

    private static void Ring0Release()
    {
        try { _releaseMutex?.Invoke(null, null); } catch { }
    }

    private static T Fail<T>(string reason)
    {
        Status = reason; _lastError = reason;
        return default!;
    }
    private static string _lastError = "";
}
