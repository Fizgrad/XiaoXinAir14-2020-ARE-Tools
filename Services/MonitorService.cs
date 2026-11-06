using LibreHardwareMonitor.Hardware;
using FanController.Models;

namespace FanController.Services;

/// <summary>一轮采集的传感器快照（数值均为最新值，null 表示不可用）</summary>
public class SensorSnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;
    public float? CpuTemp { get; init; }
    public float? GpuTemp { get; init; }
    public float? PackagePower { get; init; }
    public float? SocPower { get; init; }
    public float? CpuClockMHz { get; init; }
    public float? CpuLoad { get; init; }
    public float? GpuLoad { get; init; }
    public float? GpuPowerW { get; init; }
    public float? GpuClockMHz { get; init; }
    public float? BatteryPercent { get; init; }
    public float? BatteryPowerW { get; init; }
}

/// <summary>
/// 基于 LibreHardwareMonitorLib 0.8.8 的硬件监控服务（WinRing0 驱动内嵌，开箱即用）。
/// 温度/功耗/频率来自 AMD SMU；电池数据 LHM 0.8.8 不支持，改走 WMI。
/// </summary>
public class MonitorService : IDisposable
{
    private readonly AppSettings _settings;
    private Computer? _computer;
    private Thread? _loop;
    private volatile bool _running;

    private IHardware? _cpu;
    private IHardware? _gpu;

    private ISensor? _cpuTemp, _pkgPower, _socPower;
    private ISensor? _cpuLoad, _gpuTemp, _gpuLoad, _gpuPower, _gpuClock;
    private readonly List<ISensor> _coreClocks = new();
    private readonly List<ISensor> _coreLoads = new();

    /// <summary>UI 状态信息（驱动加载、传感器缺失等）</summary>
    public event Action<string>? StatusChanged;
    public event Action<SensorSnapshot>? Updated;

    public string CpuName { get; private set; } = "—";
    public string GpuName { get; private set; } = "—";

    public MonitorService(AppSettings settings) => _settings = settings;

    public static bool IsAdministrator()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public void Start()
    {
        if (_running) return;

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
            };
            _computer.Open();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("LibreHardwareMonitor 初始化失败：" + ex.Message);
            _computer = null;
        }

        Discover();
        // LHM 驱动加载完成后初始化 EC 端口访问
        EcIO.Init();
        StatusChanged?.Invoke($"传感器就绪：CPU={_cpu != null} GPU={_gpu != null}；EC：{EcIO.Status}");

        _running = true;
        _loop = new Thread(Loop) { IsBackground = true, Name = "MonitorLoop" };
        _loop.Start();
    }

    private void Discover()
    {
        if (_computer == null) return;

        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu && _cpu == null)
            {
                _cpu = hw;
                CpuName = hw.Name;
            }
            else if (hw.HardwareType.ToString().StartsWith("Gpu", StringComparison.Ordinal) && _gpu == null)
            {
                _gpu = hw;
                GpuName = hw.Name;
            }
        }

        // 先跑一轮 Update，AMD 的 SMU 传感器在第一次更新后才出现
        _cpu?.Update();
        _gpu?.Update();
        ResolveSensors();
    }

    private void ResolveSensors()
    {
        if (_cpu != null)
        {
            foreach (var s in _cpu.Sensors)
            {
                switch (s.SensorType)
                {
                    case SensorType.Temperature:
                        if (_cpuTemp == null || BetterName(s.Name, _cpuTemp.Name,
                                new[] { "Tctl", "Tdie", "Package", "Core" }))
                            _cpuTemp = s;
                        break;
                    case SensorType.Power:
                        if (s.Name.Contains("Package")) _pkgPower ??= s;
                        else if (s.Name.Contains("SoC", StringComparison.OrdinalIgnoreCase)
                              || s.Name.Contains("SOC", StringComparison.Ordinal)) _socPower ??= s;
                        break;
                    case SensorType.Clock when s.Name.Contains("Core"):
                        if (!_coreClocks.Contains(s)) _coreClocks.Add(s);
                        break;
                    case SensorType.Load:
                        if (s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)) _cpuLoad ??= s;
                        else if (s.Name.Contains("Core")) { if (!_coreLoads.Contains(s)) _coreLoads.Add(s); }
                        break;
                }
            }
        }

        if (_gpu != null)
        {
            foreach (var s in _gpu.Sensors)
            {
                if (s.SensorType == SensorType.Temperature) _gpuTemp ??= s;
                else if (s.SensorType == SensorType.Load && s.Name.Contains("Core")) _gpuLoad ??= s;
                else if (s.SensorType == SensorType.Power && s.Name.Contains("Package")) _gpuPower ??= s;
                else if (s.SensorType == SensorType.Clock && s.Name.Contains("Core")) _gpuClock ??= s;
            }
        }
    }

    private static bool BetterName(string candidate, string current, string[] keywords)
    {
        int Score(string n)
        {
            int best = -1;
            for (int i = 0; i < keywords.Length; i++)
                if (n.Contains(keywords[i], StringComparison.OrdinalIgnoreCase)) best = i;
            return best;
        }
        return Score(candidate) > Score(current);
    }

    private void Loop()
    {
        while (_running)
        {
            var snap = Collect();
            Updated?.Invoke(snap);
            Thread.Sleep(Math.Max(250, _settings.PollIntervalMs));
        }
    }

    private SensorSnapshot Collect()
    {
        var sampleTime = DateTime.Now;
        bool cpuUpdated = false;
        try
        {
            if (_cpu != null)
            {
                _cpu.Update();
                cpuUpdated = true;
            }
        }
        catch { }
        try { _gpu?.Update(); } catch { }
        // 个别 SMU 传感器出现较晚，缺失时再解析一次
        if (_pkgPower == null || _socPower == null || _cpuTemp == null) ResolveSensors();

        float? maxClock = null;
        foreach (var s in _coreClocks)
            if (s.Value is float v && (maxClock == null || v > maxClock)) maxClock = v;

        float? cpuLoad = _cpuLoad?.Value;
        if (cpuLoad == null)
        {
            foreach (var s in _coreLoads)
                if (s.Value is float v && (cpuLoad == null || v > cpuLoad)) cpuLoad = v;
        }

        var (batPct, batPower) = ReadBattery();

        return new SensorSnapshot
        {
            Time = sampleTime,
            // Do not present a cached temperature as a new reading after a failed update.
            CpuTemp = cpuUpdated ? _cpuTemp?.Value : null,
            GpuTemp = _gpuTemp?.Value,
            PackagePower = _pkgPower?.Value,
            SocPower = _socPower?.Value,
            CpuClockMHz = maxClock,
            CpuLoad = cpuLoad,
            GpuLoad = _gpuLoad?.Value,
            GpuPowerW = _gpuPower?.Value,
            GpuClockMHz = _gpuClock?.Value,
            BatteryPercent = batPct,
            BatteryPowerW = batPower,
        };
    }

    /// <summary>WMI 读电池：电量来自 root\wmi BatteryStatus/Win32_Battery，放电率为 BatteryStatus.DischargeRate</summary>
    private static (float? pct, float? watts) ReadBattery()
    {
        try
        {
            float? pct = null, watts = null;
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\wmi", "SELECT ChargeRate, DischargeRate, Charging, Discharging, PowerOnline, Voltage FROM BatteryStatus");
            foreach (var o in searcher.Get())
            {
                bool charging = Convert.ToBoolean(o["Charging"]);
                bool discharging = Convert.ToBoolean(o["Discharging"]);
                if (charging)
                {
                    if (o["ChargeRate"] is int cr && cr != 0) watts = -cr / 1000f;
                }
                else if (discharging)
                {
                    if (o["DischargeRate"] is int dr && dr != 0) watts = dr / 1000f;
                }
                else
                {
                    watts = 0;
                }
                break; // 单电池
            }

            using var pctSearcher = new System.Management.ManagementObjectSearcher(
                "root\\cimv2", "SELECT EstimatedChargeRemaining FROM Win32_Battery");
            foreach (var o in pctSearcher.Get())
            {
                pct = Convert.ToSingle(o["EstimatedChargeRemaining"]);
                break;
            }
            return (pct, watts);
        }
        catch
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _loop?.Join(1500); } catch { }
        try { _computer?.Close(); } catch { }
        _computer = null;
    }
}
