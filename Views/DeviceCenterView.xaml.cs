using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FanController.Models;
using FanController.Services;
using Microsoft.Win32;

namespace FanController.Views;

public partial class DeviceCenterView : UserControl
{
    private AppSettings? _settings;
    private ThermalProfileService? _profiles;
    private readonly LenovoDeviceService _devices = new();
    private readonly AutomationEngine _automation = new();
    private readonly BacklightIdleController _backlight = new();
    private readonly SemaphoreSlim _workGate = new(1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ObservableCollection<ApplicationRule> _rules = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private BatteryHistoryService? _history;
    private PowerTuningService? _power;
    private BatteryReading? _battery;
    private Action? _profileChanged;
    private bool _busy, _started, _monitorPower;
    private volatile bool _stopping;
    private DateTimeOffset _lastPowerRead = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSleepEnd;

    public sealed record ModeOption(ThermalProfile Value, string Name)
    { public override string ToString() => Name; }
    public sealed record IdleOption(int Seconds, string Name)
    { public override string ToString() => Name; }
    public DeviceCenterView() { InitializeComponent(); }

    public void Initialize(AppSettings settings, ThermalProfileService profiles, Action profileChanged)
    {
        _settings = settings; _profiles = profiles; _profileChanged = profileChanged;
        _history = new(Path.Combine(settings.ConfigDir, "battery-history"));
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "ryzenadj", "ryzenadj.exe");
        _power = new(string.IsNullOrWhiteSpace(settings.RyzenAdjPath) ? bundled : settings.RyzenAdjPath, _cancellation.Token, App.Log);
        RyzenAdjPath.Text = _power.ExecutablePath;
        var options = new[] { ThermalProfile.BatterySaving, ThermalProfile.IntelligentCooling, ThermalProfile.ExtremePerformance }
            .Select(p => new ModeOption(p, ProfileLabels.Name(p))).ToArray();
        AcProfile.ItemsSource = options; BatteryProfile.ItemsSource = options; ApplicationProfile.ItemsSource = options;
        AcProfile.SelectedValue = settings.Automation.AcProfile; BatteryProfile.SelectedValue = settings.Automation.BatteryProfile;
        ApplicationProfile.SelectedValue = ThermalProfile.ExtremePerformance;
        AutomationEnabled.IsChecked = settings.Automation.Enabled; SwitchOnPower.IsChecked = settings.Automation.SwitchOnPower;
        CooldownSeconds.Text = settings.Automation.CooldownSeconds.ToString(); ManualPauseMinutes.Text = settings.Automation.ManualPauseMinutes.ToString();
        foreach (var rule in settings.Automation.Applications) _rules.Add(new() { ProcessName = rule.ProcessName, Profile = rule.Profile });
        ApplicationRules.ItemsSource = _rules;
        var idle = new[] { 0, 30, 60, 120, 300 }.Append(settings.BacklightIdleSeconds).Distinct().Order()
            .Select(s => new IdleOption(s, s == 0 ? "关闭自动熄灭" : $"闲置 {s} 秒")).ToArray();
        BacklightTimeout.ItemsSource = idle; BacklightTimeout.SelectedValue = settings.BacklightIdleSeconds;
        RecordHistory.IsChecked = settings.RecordBatteryHistory;
        RefreshSleepHistory();
        HistoryStatus.Text = "记录目录：" + Path.Combine(settings.ConfigDir, "battery-history");
        _timer.Tick += async (_, _) => await TickAsync();
        InitializeFeedback();
    }

    public void Start()
    {
        if (_started || _settings == null || _stopping) return;
        _started = true; SystemEvents.PowerModeChanged += PowerModeChanged; _timer.Start(); _ = TickAsync();
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true; _timer.Stop(); _cancellation.Cancel();
        if (_started) SystemEvents.PowerModeChanged -= PowerModeChanged;
        // The worker releases the semaphore before returning to the dispatcher, so this cannot deadlock its UI continuation.
        if (_workGate.Wait(TimeSpan.FromSeconds(12)))
        {
            try { if (_backlight.OwnsOff) _devices.SetSwitch(DeviceSwitch.Backlight, true); }
            catch (Exception ex) { App.Log("退出时恢复背光失败：" + ex.Message); }
            finally { _workGate.Release(); }
        }
    }

    public void NotifyManualProfileChange()
    {
        if (_settings?.Automation.Enabled != true) return;
        _automation.Pause(DateTimeOffset.Now, _settings.Automation.ManualPauseMinutes);
        AutomationStatus.Text = _automation.Status;
        RenderAutomation();
    }

    public void OnSnapshot(SensorSnapshot snapshot)
    {
        if (_stopping || _settings == null || snapshot.Battery == null || _history == null) return;
        _battery = snapshot.Battery;
        _history.Add(_battery, _settings.RecordBatteryHistory);
        BatteryPercent.Text = Unit(_battery.Percent, " %", "0");
        BatteryHealth.Text = Unit(_battery.HealthPercent, " %", "0.0");
        BatteryCapacity.Text = Unit(_battery.FullChargeWh, " Wh", "0.0");
        BatteryCycles.Text = _battery.Cycles?.ToString() ?? "--";
        string wear = _battery.HealthPercent is float h ? $" · 损耗约 {Math.Max(0, 100 - h):0.0}%" : "";
        BatteryDetail.Text = $"设计容量 {Unit(_battery.DesignWh, " Wh")} · 剩余 {Unit(_battery.RemainingWh, " Wh")} · 当前 {Unit(_battery.PowerW, " W")}{wear}\n{_battery.Message}";
        float? average = _history.AverageDischargeW;
        string estimate = average is > 0 && _battery.RemainingWh is float remaining && _battery.PowerOnline == false
            ? $" · 按此负载估算剩余 {remaining / average.Value:0.0} 小时" : "";
        BatteryAverage.Text = average is float a ? $"最近 5 分钟平均放电功率：{a:0.0} W{estimate}"
            : "最近 5 分钟平均放电功率：等待至少 20 秒连续放电采样";
        if (_history.LastError.Length > 0) HistoryStatus.Text = _history.LastError;
        RefreshSleepHistory();
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_stopping) return;
        var latest = _battery; var eventTime = DateTimeOffset.Now;
        Dispatcher.BeginInvoke(() =>
        {
            if (_stopping || _history == null || _settings == null) return;
            if (e.Mode == PowerModes.Suspend) _history.Suspend(latest, _settings.RecordBatteryHistory, eventTime);
            if (e.Mode == PowerModes.Resume) { _history.Resume(); _ = TickAsync(); }
        });
    }

    private async Task TickAsync()
    {
        if (!_started || _stopping || _settings == null || _profiles == null || _power == null) return;
        var settings = _settings.Automation; int timeout = _settings.BacklightIdleSeconds;
        bool readPower = _monitorPower && DateTimeOffset.Now - _lastPowerRead >= TimeSpan.FromSeconds(15);
        bool changed = false;
        await ExecuteAsync(() =>
        {
            _devices.Refresh();
            _deviceReadAt = DateTimeOffset.Now;
            if (_stopping) return;
            _profiles.Refresh();
            var decision = _automation.Evaluate(settings, _profiles.CurrentProfile, BatteryReader.PowerOnline(),
                settings.Applications.Count > 0 && settings.Enabled ? ReadProcesses() : new HashSet<string>(), DateTimeOffset.Now);
            if (decision != null && !_stopping)
            {
                bool ok = _profiles.SetProfile(decision.Profile); changed = true;
                _automation.Complete(decision, ok, _profiles.LastMessage);
            }
            if (_stopping) return;
            double? idle = ReadIdleSeconds();
            if (idle is double seconds)
            {
                bool? target = _backlight.Evaluate(timeout, seconds, _devices.State.Value(DeviceSwitch.Backlight), DateTimeOffset.Now);
                if (target.HasValue && !_stopping) _backlight.Complete(target.Value, _devices.SetSwitch(DeviceSwitch.Backlight, target.Value));
            }
            if (readPower && !_stopping) { _lastPowerRead = DateTimeOffset.Now; _power.Refresh(); _powerReadAt = DateTimeOffset.Now; }
        }, background: true);
        if (changed && !_stopping) _profileChanged?.Invoke();
    }

    private Task ExecuteAsync(Action work, bool background = false) => RunOperationAsync(() => { work(); return null; }, "正在更新状态", background);

    private void SetBusy(bool busy)
    {
        ChargeControls.IsEnabled = DeviceControls.IsEnabled = !busy;
        PowerPathControls.IsEnabled = ReadPower.IsEnabled = !busy;
        PresetControls.IsEnabled = !busy && _power?.Ready == true;
        RestorePower.IsEnabled = !busy && _power?.Original != null;
    }
    private void PaintState() => RenderCurrentState();

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(() =>
    {
        var state = _devices.Refresh(); _deviceReadAt = DateTimeOffset.Now;
        return new(state.BatteryAvailable && state.DeviceMask.HasValue, "设备状态已更新", "设备状态读取未完成", state.Message);
    }, "正在读取设备状态…");
    private async void Charging_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && Enum.TryParse<ChargingMode>(button.Tag?.ToString(), out var mode))
            await RunOperationAsync(() =>
            {
                bool ok = _devices.SetChargingMode(mode); _deviceReadAt = DateTimeOffset.Now;
                return new(ok, "充电模式已切换为" + ChargeName(mode), "充电模式未确认",
                    ok ? "当前模式：" + ChargeName(_devices.State.Charging) : _devices.State.Message + "；当前模式：" + ChargeName(_devices.State.Charging));
            }, "正在切换充电模式…");
    }
    private async void DeviceSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || !Enum.TryParse<DeviceSwitch>(box.Tag?.ToString(), out var feature)) return;
        bool desired = !(_devices.State.Value(feature) ?? false);
        box.IsChecked = _devices.State.Value(feature);
        if (feature == DeviceSwitch.Backlight) _backlight.ManualChange();
        await RunOperationAsync(() =>
        {
            bool ok = _devices.SetSwitch(feature, desired); _deviceReadAt = DateTimeOffset.Now;
            return new(ok, DeviceName(feature) + (desired ? "已开启" : "已关闭"), DeviceName(feature) + "设置未确认",
                ok ? "当前状态：" + DeviceValueName(feature, _devices.State.Value(feature)) : _devices.State.Message + "；当前状态：" + DeviceValueName(feature, _devices.State.Value(feature)));
        }, "正在设置" + DeviceName(feature) + "…");
    }
    private void SaveBacklight_Click(object sender, RoutedEventArgs e)
    {
        SaveBacklightSettings();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        string name = AutomationSettings.NormalizeProcess(ProcessName.Text);
        if (name.Length == 0 || ApplicationProfile.SelectedValue is not ThermalProfile mode)
        { Publish(FeedbackKind.Error, "规则尚未添加", "请输入有效的进程名，例如 game.exe，并选择散热模式。"); return; }
        if (_rules.Count >= 50 || _rules.Any(r => r.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { Publish(FeedbackKind.Error, "规则尚未添加", "该进程已存在，或规则已达到 50 条。"); return; }
        _rules.Add(new() { ProcessName = name, Profile = mode }); ProcessName.Clear();
        Publish(FeedbackKind.Info, "规则已加入草稿，尚未生效", name + ".exe → " + ProfileLabels.Name(mode) + "；点击“保存自动场景”后生效。");
    }
    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (ApplicationRules.SelectedItem is ApplicationRule rule)
        { _rules.Remove(rule); Publish(FeedbackKind.Info, "规则已从草稿删除", "点击“保存自动场景”后才会更新正在使用的规则。"); }
        else Publish(FeedbackKind.Info, "请先选择规则", "在列表中选中要删除的应用规则。");
    }
    private void SaveAutomation_Click(object sender, RoutedEventArgs e)
    {
        SaveAutomationSettings();
    }
    private void ResumeAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (_settings?.Automation.Enabled != true)
        { Publish(FeedbackKind.Info, "自动场景尚未启用", "勾选“保存后启用自动场景”并保存，再恢复自动切换。"); return; }
        _automation.Resume();
        Publish(FeedbackKind.Success, "已解除手动暂停", "自动场景已恢复检测，满足稳定时间后按已保存规则切换。");
        PaintState(); _ = TickAsync();
    }
    private void RecordHistory_Click(object sender, RoutedEventArgs e)
    {
        SaveRecordingSettings();
    }
    private async void ExportBattery_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv", FileName = $"battery-history-{DateTime.Today:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog() != true || _history == null) return;
        Publish(FeedbackKind.Pending, "正在导出电池历史…", dialog.FileName);
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            _history.Export(dialog.FileName);
            Publish(FeedbackKind.Success, "电池历史已导出", dialog.FileName);
        }
        catch (Exception ex) { Publish(FeedbackKind.Error, "导出失败", ex.Message); }
    }
    private void RefreshSleepHistory()
    {
        if (_history == null) return;
        var end = _history.SleepRecords.LastOrDefault()?.End;
        if (end != _lastSleepEnd || SleepHistory.ItemsSource == null)
        { _lastSleepEnd = end; SleepHistory.ItemsSource = _history.SleepRecords.AsEnumerable().Reverse().ToArray(); }
    }
    private void ChooseRyzenAdj_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _power == null || _settings == null) return;
        if (_power.Original != null) { Publish(FeedbackKind.Info, "请先恢复本次调整前参数", "恢复成功后再更换 RyzenAdj 文件。"); return; }
        var dialog = new OpenFileDialog { Filter = "RyzenAdj (ryzenadj.exe)|ryzenadj.exe", Title = "选择完整解压目录中的 ryzenadj.exe" };
        if (dialog.ShowDialog() != true) return;
        string before = _settings.RyzenAdjPath;
        try
        {
            _settings.RyzenAdjPath = dialog.FileName;
            if (!_settings.TrySave(out string error)) { _settings.RyzenAdjPath = before; Publish(FeedbackKind.Error, "文件路径保存失败", error); return; }
            _power.SetExecutable(dialog.FileName); RyzenAdjPath.Text = dialog.FileName; _monitorPower = false; _selectedPreset = null;
            PaintState(); SetBusy(false); Publish(FeedbackKind.Success, "RyzenAdj 路径已保存", "点击“读取当前参数”检查支持情况。\n" + dialog.FileName);
        }
        catch (Exception ex) { _settings.RyzenAdjPath = before; _settings.TrySave(out _); Publish(FeedbackKind.Error, "文件选择未完成", ex.Message); }
    }
    private async void ReadPower_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(() =>
        {
            bool ok = _power?.Refresh() == true; _monitorPower = ok; _powerReadAt = _lastPowerRead = DateTimeOffset.Now;
            return new(ok, "功耗参数已读取", "功耗参数读取失败", _power?.Current?.ToString() ?? _power?.Message ?? "尚未初始化");
        }, "正在读取功耗参数…");
    }
    private async void PowerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !Enum.TryParse<PowerPreset>(button.Tag?.ToString(), out var preset)) return;
        if (_busy || _stopping) return;
        NotifyManualProfileChange();
        await RunOperationAsync(() =>
        {
            bool ok = _power?.Apply(preset) == true; if (ok) _selectedPreset = preset;
            _powerReadAt = _lastPowerRead = DateTimeOffset.Now;
            return new(ok, PresetName(preset) + "已应用", "功耗预设未确认", ok ? _power!.Current!.ToString() : _power?.Message ?? "尚未初始化");
        }, "正在应用" + PresetName(preset) + "…");
    }
    private async void RestorePower_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _stopping) return;
        NotifyManualProfileChange();
        await RunOperationAsync(() =>
        {
            bool ok = _power?.Restore() == true; if (ok) _selectedPreset = null;
            _powerReadAt = _lastPowerRead = DateTimeOffset.Now;
            return new(ok, "原功耗参数已恢复", "恢复参数未确认", ok ? _power!.Current!.ToString() : (_power?.Message ?? "尚未初始化") + "；当前参数：" + _power?.Current);
        }, "正在恢复原功耗参数…");
    }

    private static string Unit(float? value, string suffix, string format = "0.0") => value?.ToString(format) + (value.HasValue ? suffix : "--");
    private static HashSet<string>? ReadProcesses()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcesses())
                using (process) { try { names.Add(process.ProcessName); } catch { } }
            return names;
        }
        catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInput input);
    private static double? ReadIdleSeconds()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) ? unchecked((uint)Environment.TickCount - input.Tick) / 1000d : null;
    }
}
