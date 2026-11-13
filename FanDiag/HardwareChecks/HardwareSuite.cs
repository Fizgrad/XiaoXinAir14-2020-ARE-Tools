using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FanController;
using FanController.Models;
using FanController.Services;
using FanController.Views;

internal sealed class HardwareSuite
{
    private sealed class ConditionNotReachedException(string message) : Exception(message);
    private readonly string _repo, _output, _userSettings;
    private readonly string? _userSettingsHash;
    private readonly List<object> _results = new();
    private readonly Dictionary<string, object?> _report = new();
    private readonly LenovoDeviceService _device = new();
    private readonly ThermalProfileService _thermal = new();
    private readonly PowerTuningService _power;
    private readonly List<Process> _children = new();
    private readonly string[] _filter;
    private MainWindow? _window;
    private DeviceCenterView _center = null!;
    private LenovoDeviceState? _baselineDevice;
    private ThermalProfile _baselineProfile;
    private PowerLimits? _baselinePower;
    private int _failed;

    public HardwareSuite(string repo, string output, string? filter = null)
    {
        _repo = repo; _output = output;
        _filter = filter?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        _power = new(Path.Combine(repo, "tools", "ryzenadj", "ryzenadj.exe"));
        _userSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanController", "settings.json");
        _userSettingsHash = Hash(_userSettings);
        _report["Started"] = DateTimeOffset.Now; _report["Results"] = _results;
    }

    public async Task<int> Run()
    {
        try
        {
            _baselineDevice = _device.Refresh();
            Must(_baselineDevice.BatteryAvailable && _baselineDevice.DeviceMask.HasValue, _baselineDevice.Message);
            Must(_thermal.Refresh(), _thermal.LastMessage); _baselineProfile = _thermal.CurrentProfile;
            Must(_power.Refresh(), _power.Message); _baselinePower = _power.Current!;
            _report["Baseline"] = new { Devices = _baselineDevice, Profile = _baselineProfile, Power = _baselinePower, Battery = BatteryReader.Read() };
            Save();
            _window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual };
            _center = Control<DeviceCenterView>(_window, "ControlCenter");
            Application.Current.MainWindow = _window; _window.Show();

            await Check("监控：真实传感器与电池数据", Monitor);
            await Check("仪表盘：行顺序调整与文件持久化", Dashboard);
            await Check("Fn+Q：三档按钮及延迟硬件回读", Profiles);
            await Check("电池：正常与养护模式双向切换", Charging);
            Add("快充", _baselineDevice.RapidSupported ? "待验证" : "不支持", "仅按固件能力开放，不向不支持的接口写入");
            foreach (var feature in Enum.GetValues<DeviceSwitch>())
            {
                var captured = feature;
                await Check("设备开关：" + captured, () => Device(captured));
            }
            await Check("背光：真实闲置检测与活动恢复", IdleBacklight);
            await Check("自动场景：真实应用启动与退出恢复", ApplicationAutomation);
            await Check("自动场景：手动优先与恢复自动", ManualAutomation);
            await Check("自动场景：当前真实电源状态规则", PowerAutomation);
            await Check("功耗预设：三个界面按钮与恢复", PowerButtons);
            await Check("EC：真实读取、快照与差异对比", EcSnapshots);
            await Check("悬浮窗：开关、四角、实时数据与穿透样式", Overlay);
            await Check("托盘：关闭隐藏与恢复窗口", Tray);
            await Check("电池记录：真实采样 CSV 与导出", BatteryCsv);
            await Check("除尘：一次运行、重复触发抑制与自动结束", DustCycle);
            await Check("除尘：手动取消", DustCancel);
            await Check("电池记录：停止记录后不再追加", RecordingOff);
            Add("现场物理动作", "未验证", "用户选择先自动测试：USB 实际供电、Fn 键行为、背光物理亮灭、风扇转速变化尚未现场确认");
            Add("真实拔插电源／睡眠唤醒", "未验证", "未拔插电源或使电脑睡眠；相应逻辑另有软件回归检查");
            Add("充电阈值／长期续航效果", "未验证", "本次验证模式切换与当前电池读数，不进行完整充放电周期");
        }
        catch (Exception ex) { Add("测试准备或执行中断", "失败", Error(ex)); _failed++; }
        finally
        {
            await Restore();
            bool sameSettings = Hash(_userSettings) == _userSettingsHash;
            _report["UserSettingsUnchanged"] = sameSettings;
            if (!sameSettings) { Add("用户配置保护", "失败", "用户原配置内容发生变化，需检查是否被其他进程修改"); _failed++; }
            _report["Finished"] = DateTimeOffset.Now; _report["FailedChecks"] = _failed; Save();
        }
        return _failed == 0 ? 0 : 1;
    }

    private async Task Check(string name, Func<Task> test)
    {
        if (_filter.Length > 0 && !_filter.Any(name.Contains)) return;
        _report["CurrentCheck"] = name; Save(); Console.WriteLine("RUN " + name);
        try
        {
            var tabs = Control<TabControl>(_window!, "MainTabs");
            if (name.StartsWith("Fn+Q") || name.StartsWith("除尘")) tabs.SelectedIndex = 1;
            else if (name.StartsWith("EC")) tabs.SelectedIndex = 3;
            else if (name.StartsWith("悬浮窗")) tabs.SelectedIndex = 5;
            else if (name.StartsWith("电池") || name.StartsWith("设备") || name.StartsWith("背光") || name.StartsWith("自动") || name.StartsWith("功耗"))
            {
                tabs.SelectedIndex = 2;
                Control<TabControl>(_center, "FeatureTabs").SelectedIndex = name.StartsWith("功耗") ? 3
                    : name.StartsWith("自动") ? 1 : name.StartsWith("设备") || name.StartsWith("背光") ? 2 : 0;
            }
            else tabs.SelectedIndex = 0;
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await test(); Add(name, "通过", "真实应用／驱动调用检查完成；硬件物理表现以单独记录的验证范围为准");
        }
        catch (ConditionNotReachedException ex) { Add(name, "未验证", ex.Message); }
        catch (Exception ex) { _failed++; Add(name, "失败", Error(ex)); }
    }
    private void Add(string name, string status, string detail)
    { _results.Add(new { Time = DateTimeOffset.Now, Name = name, Status = status, Detail = detail }); Console.WriteLine(status + " " + name + ": " + detail); Save(); }

    private async Task Monitor()
    {
        await Until(() => Field<SensorSnapshot?>(_window!, "_lastSnap")?.CpuTemp.HasValue == true, 25, "CPU 温度未能读取");
        var sample = Field<SensorSnapshot>(_window!, "_lastSnap");
        Must(sample.CpuTemp is > 0 and < 110 && sample.PackagePower is >= 0 and < 100 && sample.CpuClockMHz is > 0, "CPU 核心监控字段缺失或不合理");
        Must(sample.Battery?.Percent is >= 0 and <= 100 && sample.Battery.FullChargeWh is > 0 && sample.Battery.DesignWh is > 0, "电池详细数据不可用");
        _report["SensorSnapshot"] = sample;
        var monitor = Field<MonitorService>(_window!, "_monitor");
        _report["SensorHardwareNames"] = new { monitor.CpuName, monitor.GpuName };
        _report["MissingOptionalSensors"] = new Dictionary<string, bool> { ["GpuTemperature"] = sample.GpuTemp == null,
            ["GpuPower"] = sample.GpuPowerW == null, ["GpuClock"] = sample.GpuClockMHz == null, ["GpuLoad"] = sample.GpuLoad == null,
            ["SocPower"] = sample.SocPower == null };
        await Task.Delay(1200);
        Must(Field<SensorSnapshot>(_window!, "_lastSnap").Time > sample.Time, "监控未持续刷新");
    }
    private Task Dashboard()
    {
        var settings = Field<AppSettings>(_window!, "_settings"); var before = settings.DashboardOrder.ToArray();
        var row = Control<StackPanel>(_window!, "DashboardRows").Children[0];
        var button = Descendants<Button>(row).First(b => Equals(b.Content, "↓")); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Must(settings.DashboardOrder[1] == before[0] && AppSettings.Load().DashboardOrder.SequenceEqual(settings.DashboardOrder), "行顺序未保存");
        settings.DashboardOrder = before.ToList(); Invoke(_window!, "BuildDashboard"); settings.Save(); return Task.CompletedTask;
    }
    private async Task Profiles()
    {
        foreach (var (button, profile) in new[] { ("BtnProfileSaving", ThermalProfile.BatterySaving),
            ("BtnProfileIntelligent", ThermalProfile.IntelligentCooling), ("BtnProfilePerformance", ThermalProfile.ExtremePerformance) })
        {
            await Until(() => !Field<bool>(_window!, "_profileRequestInProgress"), 12, "散热操作未结束");
            Click(_window!, button);
            await Until(() => !Field<bool>(_window!, "_profileRequestInProgress"), 12, "散热切换未结束");
            await Task.Delay(300); Must(_thermal.Refresh() && _thermal.CurrentProfile == profile, _thermal.LastMessage);
        }
        Must(_thermal.SetProfile(_baselineProfile), _thermal.LastMessage);
    }
    private async Task Charging()
    {
        Must(_baselineDevice!.Charging != ChargingMode.Unknown, "原充电模式未知");
        foreach (var (button, mode) in new[] { ("ChargeConservation", ChargingMode.Conservation), ("ChargeNormal", ChargingMode.Normal) })
        {
            await ClickCenter(button); await Task.Delay(300);
            Must(_device.Refresh().Charging == mode, _device.State.Message);
            Must(_center.LastFeedback?.Kind == FeedbackKind.Success, "充电设置未显示成功反馈");
            var feedback = _center.LastFeedback;
            await Task.Delay(3300);
            Must(ReferenceEquals(feedback, _center.LastFeedback), "自动刷新覆盖了充电设置结果");
        }
        Must(_device.SetChargingMode(_baselineDevice.Charging), _device.State.Message);
        _report["ChargingTelemetryAfterRestore"] = BatteryReader.Read();
    }
    private async Task Device(DeviceSwitch feature)
    {
        var before = _device.Refresh().Value(feature); Must(before.HasValue, "固件未报告支持");
        string name = feature == DeviceSwitch.Backlight ? "KeyboardLight" : feature.ToString();
        foreach (bool target in new[] { !before!.Value, before.Value })
        {
            await CenterReady(); var box = Control<CheckBox>(_center, name); box.IsChecked = target;
            box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await CenterReady();
            Must(_device.Refresh().Value(feature) == target, _device.State.Message);
            Must(_center.LastFeedback?.Kind == FeedbackKind.Success, "设备设置缺少成功反馈");
        }
    }
    private async Task IdleBacklight()
    {
        Must(_device.Refresh().Supported(DeviceSwitch.Backlight), "无背光支持");
        Must(_device.SetSwitch(DeviceSwitch.Backlight, true), _device.State.Message);
        var settings = Field<AppSettings>(_window!, "_settings"); settings.BacklightIdleSeconds = 30;
        var idleSamples = new List<object>(); _report["IdleSamples"] = idleSamples;
        try
        {
            var resetInput = new Input { Type = 0, Mouse = new MouseInput { Flags = 1 } };
            Must(SendInput(1, new[] { resetInput }, Marshal.SizeOf<Input>()) == 1, "无法发送零位移鼠标活动");
            var watch = Stopwatch.StartNew(); double maximumIdle = 0;
            while (_device.Refresh().Value(DeviceSwitch.Backlight) != false && watch.Elapsed.TotalSeconds < 42)
            {
                var idle = typeof(DeviceCenterView).GetMethod("ReadIdleSeconds", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                if (idle is double observedIdle) maximumIdle = Math.Max(maximumIdle, observedIdle);
                var controller = Field<BacklightIdleController>(_center, "_backlight");
                idleSamples.Add(new { Time = DateTimeOffset.Now, IdleSeconds = idle, controller.OwnsOff,
                    Suppressed = Field<bool>(controller, "_suppressed"), TimerEnabled = Field<DispatcherTimer>(_center, "_timer").IsEnabled,
                    Busy = Field<bool>(_center, "_busy"), Timeout = Field<AppSettings>(_center, "_settings").BacklightIdleSeconds,
                    DeviceMessage = Field<LenovoDeviceService>(_center, "_devices").State.Message });
                Save(); await Task.Delay(1000);
            }
            if (_device.Refresh().Value(DeviceSwitch.Backlight) != false && maximumIdle < 30)
                throw new ConditionNotReachedException($"连续空闲最长 {maximumIdle:0.0} 秒，未达到 30 秒，未验证实际闲置触发");
            Must(_device.Refresh().Value(DeviceSwitch.Backlight) == false, "闲置已达阈值但熄灭未触发，参见 IdleSamples");
            Must(Field<BacklightIdleController>(_center, "_backlight").OwnsOff, "背光关闭并非由闲置策略接管");
            var input = new Input { Type = 0, Mouse = new MouseInput { Flags = 1 } };
            Must(SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) == 1, "无法发送零位移鼠标活动");
            await Until(() => _device.Refresh().Value(DeviceSwitch.Backlight) == true, 8, "活动后未恢复背光");
        }
        finally { settings.BacklightIdleSeconds = 0; }
    }
    private async Task ApplicationAutomation()
    {
        await ConfigureAutomation(false, true);
        var trigger = StartTrigger();
        await Until(() => Profile() == ThermalProfile.BatterySaving, 22, "应用启动未触发节能规则");
        _report["ApplicationAutoSwitched"] = DateTimeOffset.Now;
        trigger.Kill(true); await trigger.WaitForExitAsync();
        await Until(() => Profile() == _baselineProfile, 22, "应用退出后未恢复原模式");
        Control<CheckBox>(_center, "AutomationEnabled").IsChecked = false; await ClickCenterByContent("保存自动场景");
    }
    private async Task ManualAutomation()
    {
        await ConfigureAutomation(false, false);
        var trigger = StartTrigger();
        await Until(() => Profile() == ThermalProfile.BatterySaving, 22, "应用自动切换失败");
        Click(_window!, "BtnProfilePerformance");
        await Until(() => Profile() == ThermalProfile.ExtremePerformance, 12, "手动切换未生效");
        await Task.Delay(6500);
        Must(Profile() == ThermalProfile.ExtremePerformance && Control<TextBlock>(_center, "AutomationStatus").Text.Contains("手动优先"), "手动模式被自动规则覆盖");
        await ClickCenterByContent("立即恢复自动切换");
        await Until(() => Profile() == ThermalProfile.BatterySaving, 16, "解除暂停后未恢复规则");
        trigger.Kill(true); await trigger.WaitForExitAsync();
        Control<CheckBox>(_center, "AutomationEnabled").IsChecked = false; await ClickCenterByContent("保存自动场景");
        await Until(() => Profile() == ThermalProfile.ExtremePerformance, 22, "停用自动场景后未恢复手动基线");
        Must(_thermal.SetProfile(_baselineProfile), _thermal.LastMessage);
    }
    private async Task PowerAutomation()
    {
        bool? online = BatteryReader.PowerOnline(); Must(online.HasValue, "电源状态未知");
        await ConfigureAutomation(true, false);
        await Until(() => Profile() == ThermalProfile.BatterySaving, 22, "当前电源规则未触发");
        Must(Control<TextBlock>(_center, "AutomationStatus").Text.Contains(online!.Value ? "接通电源" : "使用电池"), "规则原因与真实电源状态不匹配");
        Control<CheckBox>(_center, "AutomationEnabled").IsChecked = false; await ClickCenterByContent("保存自动场景");
        await Until(() => Profile() == _baselineProfile, 22, "停用电源规则后未恢复模式");
    }
    private async Task ConfigureAutomation(bool power, bool addRule)
    {
        await CenterReady();
        if (addRule)
        {
            Control<TextBox>(_center, "ProcessName").Text = "FanAutomationTrigger.exe";
            Control<ComboBox>(_center, "ApplicationProfile").SelectedValue = ThermalProfile.BatterySaving;
            await ClickCenterByContent("添加规则");
        }
        Control<CheckBox>(_center, "AutomationEnabled").IsChecked = true;
        Control<CheckBox>(_center, "SwitchOnPower").IsChecked = power;
        Control<ComboBox>(_center, "AcProfile").SelectedValue = ThermalProfile.BatterySaving;
        Control<ComboBox>(_center, "BatteryProfile").SelectedValue = ThermalProfile.BatterySaving;
        Control<TextBox>(_center, "CooldownSeconds").Text = "10"; Control<TextBox>(_center, "ManualPauseMinutes").Text = "1";
        await ClickCenterByContent("保存自动场景");
        Must(AppSettings.Load().Automation.Enabled, "规则未保存");
        // Clear a pause left by the preceding manual test; this uses the same UI action as the product.
        await ClickCenterByContent("立即恢复自动切换");
    }
    private Process StartTrigger()
    {
        string host = Environment.ProcessPath!;
        Must(host.EndsWith("HardwareChecks.exe", StringComparison.OrdinalIgnoreCase), "Run the compiled HardwareChecks.exe host");
        string path = Path.Combine(Path.GetDirectoryName(host)!, "FanAutomationTrigger.exe"); File.Copy(host, path, true);
        var process = Process.Start(new ProcessStartInfo(path, "--trigger-only") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden })!;
        _children.Add(process); Must(process.ProcessName.Equals("FanAutomationTrigger", StringComparison.OrdinalIgnoreCase), "触发进程名不匹配");
        return process;
    }
    private async Task PowerButtons()
    {
        Must(_thermal.SetProfile(_baselineProfile), _thermal.LastMessage);
        await ClickCenterByContent("读取当前参数");
        var power = Field<PowerTuningService>(_center, "_power"); Must(power.Current != null, power.Message);
        var before = power.Current;
        await CenterReady();
        await _center.ApplyStartupPresetAsync(PowerPreset.Quiet);
        Must(_center.LastFeedback?.Kind == FeedbackKind.Success && power.Target != null, "自启动功耗流程未显示成功");
        Must(_power.Refresh() && Matches(_power.Current!, power.Target!), "自启动功耗预设独立回读不匹配");
        _report["StartupPowerVerified"] = new { Preset = "Quiet", Actual = _power.Current };
        await ClickCenter("RestorePower"); Must(power.Current != null && Matches(before!, power.Current), "自启动预设恢复失败");
        var buttons = Descendants<Button>(_center).Where(b => b.Tag?.ToString() is "Quiet" or "Balanced" or "Sustained").ToArray();
        Must(buttons.Length == 3, "预设按钮缺失");
        foreach (var button in buttons)
        {
            await CenterReady(); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await CenterReady();
            Must(power.Target != null && power.Current != null, power.Message);
            var requested = button.Tag!.ToString() switch { "Quiet" => new PowerLimits(15, 12, 70), "Balanced" => new PowerLimits(25, 18, 80), _ => new PowerLimits(30, 25, 85) };
            var expected = new PowerLimits(Math.Min(requested.FastW, before!.FastW), Math.Min(requested.SlowW, before.SlowW), Math.Min(requested.TemperatureC, before.TemperatureC));
            Must(Matches(power.Target!, expected), "设置失败或回滚至其他参数：" + power.Message);
            Must(_center.LastFeedback?.Kind == FeedbackKind.Success, "功耗预设未显示成功反馈");
            await Task.Delay(500); Must(power.Refresh() && Matches(power.Current!, power.Target!), power.Message);
        }
        await ClickCenter("RestorePower"); Must(power.Current != null && Matches(before!, power.Current), power.Message);
    }
    private async Task EcSnapshots()
    {
        Must(EcIO.IsAvailable, EcIO.Status);
        Invoke(_window!, "BtnEcRefresh_Click", _window!, new RoutedEventArgs());
        await Until(() => Field<byte[]?>(_window!, "_currentDump") != null, 25, "EC 读取未完成");
        var first = Field<byte[]>(_window!, "_currentDump"); Must(first.Length == 256 && first.Any(v => v != 0 && v != 255), "EC 数据全零或全 FF");
        Invoke(_window!, "BtnEcSnapA_Click", _window!, new RoutedEventArgs());
        var baseline = Profile(); Must(_thermal.SetProfile(baseline == ThermalProfile.BatterySaving ? ThermalProfile.IntelligentCooling : ThermalProfile.BatterySaving), _thermal.LastMessage);
        Invoke(_window!, "BtnEcRefresh_Click", _window!, new RoutedEventArgs());
        await Until(() => !ReferenceEquals(first, Field<byte[]?>(_window!, "_currentDump")), 25, "第二次 EC 读取未完成");
        Invoke(_window!, "BtnEcSnapB_Click", _window!, new RoutedEventArgs()); Invoke(_window!, "BtnEcDiff_Click", _window!, new RoutedEventArgs());
        _report["EcDiff"] = Control<TextBlock>(_window!, "LblEcView").Text;
        Must(_thermal.SetProfile(baseline), _thermal.LastMessage);
    }
    private async Task Overlay()
    {
        Control<CheckBox>(_window!, "ChkOverlay").IsChecked = true;
        await Task.Delay(1200); var overlay = Field<OverlayWindow>(_window!, "_overlay");
        Must(overlay.IsVisible && overlay.Topmost && !overlay.IsActive, "悬浮窗未显示或抢占焦点");
        int style = GetWindowLong(new WindowInteropHelper(overlay).Handle, -20);
        Must((style & 0xA0) == 0xA0, "点击穿透或工具窗口样式缺失");
        Must(Control<TextBlock>(overlay, "OvCpu").Text.Contains("CPU") && !Control<TextBlock>(overlay, "OvCpu").Text.Contains("--°C"), "悬浮窗没有实时温度");
        for (int corner = 0; corner < 4; corner++)
        {
            Control<ComboBox>(_window!, "CbOverlayCorner").SelectedIndex = corner; await Task.Delay(80);
            var area = SystemParameters.WorkArea;
            Must(overlay.Left >= area.Left && overlay.Top >= area.Top && overlay.Left + overlay.ActualWidth <= area.Right + 1 && overlay.Top + overlay.ActualHeight <= area.Bottom + 1, "悬浮窗位置越界");
        }
        Control<CheckBox>(_window!, "ChkOverlay").IsChecked = false; Must(!overlay.IsVisible, "悬浮窗未隐藏");
    }
    private Task Tray()
    {
        var tray = Field<System.Windows.Forms.NotifyIcon>(_window!, "_tray"); Must(tray.Visible, "托盘图标不可见");
        _window!.Close(); Must(!_window.IsVisible, "关闭未隐藏到托盘");
        IntPtr foreground = GetForegroundWindow(); _window.ShowFromTray(); SetForegroundWindow(foreground);
        Must(_window.IsVisible, "托盘恢复未显示窗口"); return Task.CompletedTask;
    }
    private async Task BatteryCsv()
    {
        var history = Field<BatteryHistoryService>(_center, "_history");
        string export = Path.Combine(_output, "battery-export.csv");
        await Until(() => Directory.Exists(Path.Combine(new AppSettings().ConfigDir, "battery-history")), 8, "电池历史目录未创建");
        history.Export(export); var lines = File.ReadAllLines(export);
        Must(lines.Length >= 2 && lines[0].StartsWith("Time,Percent") && lines[1].Split(',').Length == 9, "CSV 导出内容无效");
        _report["BatteryCsvRows"] = lines.Length - 1;
    }
    private async Task DustCycle()
    {
        Click(_window!, "BtnDustStart"); Must(EnergyDrvFan.IsRunning, EnergyDrvFan.LastMessage);
        int? during = EnergyDrvFan.ReadState(); Must(!EnergyDrvFan.StartDustCleaning(), "运行期间重复触发被接受");
        await Until(() => !EnergyDrvFan.IsRunning, 14, "除尘没有自动结束"); Must(!EnergyDrvFan.RestorePending, EnergyDrvFan.LastMessage);
        _report["DustRawStates"] = new { During = during, After = EnergyDrvFan.ReadState(), Message = EnergyDrvFan.LastMessage };
    }
    private async Task DustCancel()
    {
        Click(_window!, "BtnDustStart"); Must(EnergyDrvFan.IsRunning, EnergyDrvFan.LastMessage);
        await Task.Delay(800); Click(_window!, "BtnDustStop");
        Must(!EnergyDrvFan.IsRunning && !EnergyDrvFan.RestorePending, EnergyDrvFan.LastMessage);
    }
    private async Task RecordingOff()
    {
        var box = Control<CheckBox>(_center, "RecordHistory"); box.IsChecked = false; box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        string directory = Path.Combine(new AppSettings().ConfigDir, "battery-history");
        long Size() => Directory.EnumerateFiles(directory, "battery-*.csv").Sum(f => new FileInfo(f).Length);
        long before = Size(); await Task.Delay(31500); Must(Size() == before, "关闭记录后仍追加 CSV");
    }

    private async Task Restore()
    {
        foreach (var child in _children) { try { if (!child.HasExited) { child.Kill(true); child.WaitForExit(3000); } child.Dispose(); } catch { } }
        try { if (_window != null) { Field<AppSettings>(_window, "_settings").Automation.Enabled = false; Invoke(_window, "Shutdown"); FieldSet(_window, "_reallyExit", true); _window.Close(); } }
        catch (Exception ex) { Add("测试窗口清理", "失败", Error(ex)); _failed++; }
        try
        {
            if (EnergyDrvFan.IsRunning || EnergyDrvFan.RestorePending) EnergyDrvFan.Stop("实机测试结束");
            if (_baselineDevice != null)
            {
                if (_baselineDevice.Charging != ChargingMode.Unknown && _device.Refresh().Charging != _baselineDevice.Charging)
                    Must(_device.SetChargingMode(_baselineDevice.Charging), _device.State.Message);
                foreach (var feature in Enum.GetValues<DeviceSwitch>())
                    if (_baselineDevice.Value(feature) is bool value && _device.Refresh().Value(feature) != value)
                        Must(_device.SetSwitch(feature, value), _device.State.Message);
            }
            if (_baselineProfile != ThermalProfile.Unknown) Must(_thermal.SetProfile(_baselineProfile), _thermal.LastMessage);
            if (_baselinePower != null)
            {
                typeof(PowerTuningService).GetMethod("WriteAndVerify", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_power, new object[] { _baselinePower });
                Must(_power.Refresh() && Matches(_power.Current!, _baselinePower), _power.Message);
            }
            var actual = _device.Refresh();
            Must(_baselineDevice == null || Enum.GetValues<DeviceSwitch>().All(f => _baselineDevice.Value(f) == actual.Value(f)) && actual.Charging == _baselineDevice.Charging,
                "部分设备设置未恢复");
            _report["Restoration"] = new { Confirmed = true, Devices = actual, Profile = Profile(), Power = _power.Current };
        }
        catch (Exception ex) { _failed++; _report["Restoration"] = new { Confirmed = false, Error = Error(ex) }; }
        await Task.CompletedTask;
    }
    private ThermalProfile Profile() { _thermal.Refresh(); return _thermal.CurrentProfile; }
    private async Task CenterReady() => await Until(() => !Field<bool>(_center, "_busy"), 20, "控制中心操作未结束");
    private async Task ClickCenter(string name) { await CenterReady(); Click(_center, name); await CenterReady(); }
    private async Task ClickCenterByContent(string text)
    {
        await CenterReady(); var button = LogicalButtons(_center).First(b => Equals(b.Content, text));
        Must(button.IsEnabled, text + " 不可用"); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await CenterReady();
    }
    private static IEnumerable<Button> LogicalButtons(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d) { if (d is Button b) yield return b; foreach (var nested in LogicalButtons(d)) yield return nested; }
    }
    private static void Click(FrameworkElement owner, string name)
    { var b = Control<Button>(owner, name); Must(b.IsEnabled, name + " 按钮不可用"); b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static T Control<T>(FrameworkElement owner, string name) => (T)owner.FindName(name)!;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void FieldSet(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static async Task Until(Func<bool> condition, double seconds, string error)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed.TotalSeconds > seconds) throw new InvalidOperationException(error); await Task.Delay(100); }
    }
    private static bool Matches(PowerLimits a, PowerLimits b) => Math.Abs(a.FastW - b.FastW) < .2 && Math.Abs(a.SlowW - b.SlowW) < .2 && Math.Abs(a.TemperatureC - b.TemperatureC) < 1;
    private static void Must(bool yes, string error) { if (!yes) throw new InvalidOperationException(error); }
    private static string Error(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner.ToString() : ex.ToString();
    private static string? Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;
    private void Save()
    {
        File.WriteAllText(Path.Combine(_output, "results.json"), JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));
        var lines = new StringBuilder("FanController 实机自动检查\n");
        foreach (var row in _results) lines.AppendLine(JsonSerializer.Serialize(row));
        File.WriteAllText(Path.Combine(_output, "results.txt"), lines.ToString());
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
}
