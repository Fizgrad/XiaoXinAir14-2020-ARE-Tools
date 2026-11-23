using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanController.Models;
using FanController.Services;

namespace FanController.Views;

public partial class DeviceCenterView
{
    public event Action<FeedbackMessage>? FeedbackPublished;
    public FeedbackMessage? LastFeedback { get; private set; }
    private DateTimeOffset? _deviceReadAt, _powerReadAt;
    private PowerPreset? _selectedPreset;
    private bool _feedbackReady;
    private sealed record OperationResult(bool Success, string SuccessTitle, string ErrorTitle, string Detail);

    private void Publish(FeedbackKind kind, string title, string detail)
    {
        var message = new FeedbackMessage(kind, title, detail, DateTimeOffset.Now);
        LastFeedback = message;
        if (FeedbackPublished != null) FeedbackPublished(message);
        else
        {
            OperationStatus.Text = message.HistoryTitle + "\n" + message.Detail;
            OperationStatus.Foreground = (Brush)FindResource(kind == FeedbackKind.Error ? "BadBrush" : kind == FeedbackKind.Success ? "GoodBrush" : "AccentBrush");
            OperationStatus.Visibility = Visibility.Visible;
        }
    }

    private async Task RunOperationAsync(Func<OperationResult?> operation, string pendingTitle, bool background = false)
    {
        if (_stopping) return;
        if (_busy)
        {
            // 后台刷新与用户操作重叠：后台刷新直接跳过本轮；用户操作排到当前这一轮之后执行，而不是丢弃。
            if (background) return;
            if (!_backgroundBusy || _queued.HasValue)
            {
                if (LastFeedback?.Kind != FeedbackKind.Pending)
                    Publish(FeedbackKind.Info, "上一个操作仍在处理", "请稍候。");
                return;
            }
            _queued = (operation, pendingTitle);
            if (LastFeedback?.Kind != FeedbackKind.Pending) Publish(FeedbackKind.Pending, pendingTitle, "等待当前刷新完成后执行。");
            return;
        }
        if (!background) Publish(FeedbackKind.Pending, pendingTitle, "请稍候。");
        _busy = true; _backgroundBusy = background;
        // 只有用户操作才禁用控件：后台刷新每 3 秒一次，跟着禁用会让按钮和开关不停闪烁。
        if (!background) SetBusy(true);
        OperationResult? result = null;
        await _workGate.WaitAsync();
        try
        {
            result = await Task.Run(() =>
            {
                try { return _stopping ? null : operation(); }
                finally { _workGate.Release(); }
            });
        }
        catch (Exception ex)
        {
            if (!background) result = new(false, "", "操作未完成", ex.Message);
            App.Log("控制中心操作失败：" + ex);
        }
        finally
        {
            _busy = false; _backgroundBusy = false;
            if (!_stopping) { PaintState(); if (!background) SetBusy(false); }
        }
        if (!_stopping && result != null)
            Publish(result.Success ? FeedbackKind.Success : FeedbackKind.Error,
                result.Success ? result.SuccessTitle : result.ErrorTitle, result.Detail);
        var next = _queued;
        if (next.HasValue && !_stopping)
        {
            _queued = null;
            _ = RunOperationAsync(next.Value.Operation, next.Value.Title);
        }
    }

    private void InitializeFeedback()
    {
        foreach (var box in new[] { AutomationEnabled, SwitchOnPower })
        { box.Checked += (_, _) => UpdateDraftIndicators(); box.Unchecked += (_, _) => UpdateDraftIndicators(); }
        foreach (var combo in new[] { AcProfile, BatteryProfile, BacklightTimeout })
            combo.SelectionChanged += (_, _) => UpdateDraftIndicators();
        CooldownSeconds.TextChanged += (_, _) => UpdateDraftIndicators();
        ManualPauseMinutes.TextChanged += (_, _) => UpdateDraftIndicators();
        _rules.CollectionChanged += (_, _) => UpdateDraftIndicators();
        _feedbackReady = true;
        PaintState();
    }

    private void RenderCurrentState()
    {
        var state = _devices.State;
        ChargeNormal.IsEnabled = ChargeConservation.IsEnabled = state.BatteryAvailable;
        ChargeRapid.IsEnabled = state.BatteryAvailable && state.RapidSupported;
        ChargeCurrentValue.Text = ChargeName(state.Charging);
        ChargeCurrentValue.Foreground = (Brush)FindResource(state.Charging == ChargingMode.Unknown ? "WarnBrush" : "FgBrush");
        ChargeCheckedAt.Text = state.Charging == ChargingMode.Unknown ? "读取未完成" : "更新于 " + ReadTime(_deviceReadAt);
        ChargeCapabilityHint.Text = !state.BatteryAvailable ? "充电模式读取失败，请刷新重试。"
            : !state.RapidSupported ? "快充不可用" : "";
        foreach (var (button, mode) in new[] { (ChargeNormal, ChargingMode.Normal), (ChargeConservation, ChargingMode.Conservation), (ChargeRapid, ChargingMode.Rapid) })
        {
            bool selected = state.Charging == mode;
            SelectButton(button, selected); button.Content = ChargeName(mode) + (selected ? "  ✓ 当前" : "");
        }

        foreach (var (box, feature, badge, label) in new[]
        {
            (FnLock, DeviceSwitch.FnLock, FnLockBadge, FnLockState), (UsbAc, DeviceSwitch.UsbAc, UsbAcBadge, UsbAcState),
            (UsbBattery, DeviceSwitch.UsbBattery, UsbBatteryBadge, UsbBatteryState), (KeyboardLight, DeviceSwitch.Backlight, KeyboardLightBadge, KeyboardLightState),
        })
        {
            bool? value = state.Value(feature); box.IsEnabled = state.Supported(feature); box.IsChecked = value;
            string text = value.HasValue ? DeviceValueName(feature, value)
                : state.DeviceMask.HasValue ? "不可用" : _deviceReadAt.HasValue ? "读取失败" : "尚未读取";
            SetBadge(badge, label, text, value);
            box.ToolTip = value.HasValue ? "更新于 " + ReadTime(_deviceReadAt) : state.Message;
        }
        BacklightTimeout.IsEnabled = SaveBacklight.IsEnabled = state.Supported(DeviceSwitch.Backlight);
        DeviceStatus.Text = state.DeviceMask.HasValue ? "更新于 " + ReadTime(_deviceReadAt) : state.Message;
        BacklightSavedState.Text = "当前策略：" + IdleName(_settings?.BacklightIdleSeconds ?? 0)
            + (_backlight.OwnsOff ? " · 已因闲置熄灭" : "");
        RenderAutomation(); RenderPower(); RenderRecording(); UpdateDraftIndicators();
    }

    private void RenderAutomation()
    {
        if (_settings == null) return;
        bool enabled = _settings.Automation.Enabled;
        string status = _automation.Status;
        bool paused = enabled && status.Contains("手动优先");
        string title = !enabled ? status.Contains("恢复") ? "已停用 · 等待恢复" : "未启用"
            : paused ? "手动优先 · 已暂停" : "已启用";
        SetBadge(AutomationStateBadge, AutomationRunState, title, !enabled ? null : !paused);
        if (!enabled) AutomationRunState.Foreground = (Brush)FindResource("FgDimBrush");
        else if (paused) AutomationRunState.Foreground = (Brush)FindResource("WarnBrush");
        AutomationLiveProfile.Text = "当前 Fn+Q：" + ProfileLabels.Name(_profiles?.CurrentProfile ?? ThermalProfile.Unknown);
        AutomationStatus.Text = enabled && status == "自动场景未启用" ? "规则已启用，等待下一次场景检测。" : status;
        var saved = _settings.Automation;
        AutomationSavedSummary.Text = "已保存：" + (saved.SwitchOnPower
            ? $"插电 → {ProfileLabels.Name(saved.AcProfile)}；电池 → {ProfileLabels.Name(saved.BatteryProfile)}"
            : "关闭电源联动，应用结束后恢复接管前模式")
            + $"\n应用规则 {saved.Applications.Count} 条 · 切换间隔 {saved.CooldownSeconds} 秒 · 手动优先 {saved.ManualPauseMinutes} 分钟";
    }

    private void RenderPower()
    {
        if (_power == null) return;
        var current = _power.Current;
        PowerFastValue.Text = Unit(current?.FastW, " W"); PowerSlowValue.Text = Unit(current?.SlowW, " W");
        bool changed = PowerOverridden;
        string applied = _selectedPreset is { } appliedPreset ? "预设「" + PresetName(appliedPreset) + "」" : "上次设置的参数";
        PowerCurrent.Text = current == null ? "当前限制：尚未读取或不可用" : changed
            ? applied + "已被固件覆盖 · 当前 " + current + " · 更新于 " + ReadTime(_powerReadAt)
            : "当前生效值 " + current + " · 更新于 " + ReadTime(_powerReadAt);
        PowerCurrent.Foreground = (Brush)FindResource(changed ? "WarnBrush" : "FgBrush");
        PowerCurrentCard.Background = changed ? PowerCardOverriddenBrush : PowerCardBrush;
        PowerStatus.Text = changed
            ? "当前值已偏离上次设置的预设。固件在 Fn+Q 散热模式切换（热键、联想工具或自动场景）后会重新下发自己的限值；"
              + "这里只提示、不自动重写，需要时点“重新应用上次预设”或重新选择预设。"
            : current != null ? (_power.BaselineNote.Length > 0 ? _power.BaselineNote : "每 15 秒刷新；Fn+Q 切换后若预设被覆盖，会在这里提示")
            : _power.Message;
        PowerStatus.Foreground = (Brush)FindResource(changed || current == null ? "WarnBrush" : "FgDimBrush");
        PowerOriginal.Text = _power.Original is { } original ? "可恢复的原参数：" + original : "恢复基准：首次应用预设时记录";
        ReapplyPower.IsEnabled = changed && !(_busy && !_backgroundBusy);
        PowerPreset? selected = _selectedPreset is { } last && current != null && current.Matches(_power.Preview(last)) ? last : null;
        if (!selected.HasValue && current != null)
            selected = Enum.GetValues<PowerPreset>().Cast<PowerPreset?>().FirstOrDefault(p => current.Matches(PowerTuningService.RequestedLimits(p!.Value)));
        foreach (var (preset, button, title, values) in new[]
        {
            (PowerPreset.Quiet, PresetQuiet, QuietTitle, QuietValues), (PowerPreset.Balanced, PresetBalanced, BalancedTitle, BalancedValues),
            (PowerPreset.Sustained, PresetSustained, SustainedTitle, SustainedValues),
        })
        {
            var preview = _power.Preview(preset); bool active = selected == preset;
            SelectButton(button, active); title.Text = PresetName(preset) + (active ? "  ✓ 当前" : "");
            values.Text = $"{preview.FastW:0.#} W / {preview.SlowW:0.#} W";
            button.ToolTip = "将应用：" + preview;
        }
    }

    private void RenderRecording()
    {
        bool enabled = _settings?.RecordBatteryHistory == true;
        RecordingState.Text = enabled ? "● 正在记录 · 每 30 秒保存" : "● 已暂停记录 · 保留已有历史";
        RecordingState.Foreground = (Brush)FindResource(enabled ? "GoodBrush" : "FgDimBrush");
    }

    private void UpdateDraftIndicators()
    {
        if (!_feedbackReady || _settings == null) return;
        var saved = _settings.Automation;
        string Signature(bool enabled, bool power, object? ac, object? battery, string cooldown, string pause, IEnumerable<ApplicationRule> rules) =>
            JsonSerializer.Serialize(new { enabled, power, ac = ac?.ToString(), battery = battery?.ToString(), cooldown = cooldown.Trim(), pause = pause.Trim(),
                rules = rules.Select(r => new { r.ProcessName, r.Profile }) });
        bool dirty = Signature(AutomationEnabled.IsChecked == true, SwitchOnPower.IsChecked == true, AcProfile.SelectedValue, BatteryProfile.SelectedValue,
            CooldownSeconds.Text, ManualPauseMinutes.Text, _rules)
            != Signature(saved.Enabled, saved.SwitchOnPower, saved.AcProfile, saved.BatteryProfile, saved.CooldownSeconds.ToString(), saved.ManualPauseMinutes.ToString(), saved.Applications);
        AutomationDraftState.Text = dirty ? "有未保存的更改 · 点击“保存自动场景”后生效" : "所有修改已保存 · 当前运行状态见页面顶部";
        AutomationDraftState.Foreground = (Brush)FindResource(dirty ? "WarnBrush" : "GoodBrush");
        BacklightDraftState.Text = BacklightTimeout.SelectedValue is int seconds && seconds != _settings.BacklightIdleSeconds
            ? "未保存：将改为“" + IdleName(seconds) + "”，点击保存后生效。" : "";
    }

    private void SaveBacklightSettings()
    {
        if (_settings == null || BacklightTimeout.SelectedValue is not int seconds) return;
        int before = _settings.BacklightIdleSeconds; _settings.BacklightIdleSeconds = seconds;
        if (!_settings.TrySave(out string error))
        { _settings.BacklightIdleSeconds = before; Publish(FeedbackKind.Error, "背光策略未保存", error + "；当前仍为：" + IdleName(before)); }
        else Publish(FeedbackKind.Success, "背光策略已保存", IdleName(seconds));
        PaintState(); _ = TickAsync();
    }

    private void SaveAutomationSettings()
    {
        if (_settings == null) return;
        if (!int.TryParse(CooldownSeconds.Text, out int cooldown) || cooldown is < 10 or > 600
            || !int.TryParse(ManualPauseMinutes.Text, out int pause) || pause is < 1 or > 120
            || AcProfile.SelectedValue is not ThermalProfile ac || BatteryProfile.SelectedValue is not ThermalProfile battery)
        { Publish(FeedbackKind.Error, "自动场景尚未保存", "切换间隔应为 10–600 秒，手动优先应为 1–120 分钟。正在使用的规则保持原值。"); return; }
        var before = _settings.Automation;
        _settings.Automation = new() { Enabled = AutomationEnabled.IsChecked == true, SwitchOnPower = SwitchOnPower.IsChecked == true,
            AcProfile = ac, BatteryProfile = battery, CooldownSeconds = cooldown, ManualPauseMinutes = pause,
            Applications = _rules.Select(r => new ApplicationRule { ProcessName = r.ProcessName, Profile = r.Profile }).ToList() };
        if (!_settings.TrySave(out string error))
        { _settings.Automation = before; Publish(FeedbackKind.Error, "自动场景未保存、未生效", error + "；编辑内容已保留，可修正后重试。"); }
        else
        {
            CooldownSeconds.Text = cooldown.ToString(); ManualPauseMinutes.Text = pause.ToString();
            Publish(FeedbackKind.Success, _settings.Automation.Enabled ? "自动场景已保存并启用" : "自动场景已保存并停用",
                $"已保存 {_settings.Automation.Applications.Count} 条应用规则。" + (_settings.Automation.Enabled ? "满足稳定时间后按规则切换。" : "如需恢复原模式，将按切换间隔完成。"));
        }
        PaintState(); _ = TickAsync();
    }

    private void SaveRecordingSettings()
    {
        if (_settings == null) return;
        bool before = _settings.RecordBatteryHistory; _settings.RecordBatteryHistory = RecordHistory.IsChecked == true;
        if (!_settings.TrySave(out string error))
        { _settings.RecordBatteryHistory = before; RecordHistory.IsChecked = before; Publish(FeedbackKind.Error, "记录开关未更改", error); }
        else
        {
            if (!_settings.RecordBatteryHistory) _history?.StopRecording();
            Publish(FeedbackKind.Success, _settings.RecordBatteryHistory ? "电池历史记录已开启" : "电池历史记录已暂停",
                _settings.RecordBatteryHistory ? "每 30 秒保存一次，保留最近 30 天。" : "已停止追加数据，已有记录仍可查看和导出。");
        }
        RenderRecording();
    }

    private static string ReadTime(DateTimeOffset? time) => time?.ToString("HH:mm:ss") ?? "等待首次读取";
    private static string ChargeName(ChargingMode mode) => mode switch
    { ChargingMode.Normal => "正常充电", ChargingMode.Conservation => "电池养护", ChargingMode.Rapid => "快速充电", _ => "状态未知" };
    private static string PresetName(PowerPreset preset) => preset switch
    { PowerPreset.Quiet => "安静办公", PowerPreset.Balanced => "均衡", _ => "持续负载" };
    private static string IdleName(int seconds) => seconds == 0 ? "关闭自动熄灭" : $"闲置 {seconds} 秒后熄灭";
    private static string DeviceName(DeviceSwitch feature) => feature switch
    { DeviceSwitch.FnLock => "Fn 锁定", DeviceSwitch.UsbAc => "插电 USB 持续供电", DeviceSwitch.UsbBattery => "电池 USB 持续供电", _ => "键盘背光" };
    private static string DeviceValueName(DeviceSwitch feature, bool? value) => value == null ? "状态未知"
        : feature == DeviceSwitch.FnLock ? value.Value ? "F1–F12 优先" : "亮度／音量热键优先" : value.Value ? "已开启" : "已关闭";
    // 画刷只创建一次并 Freeze：渲染每 3 秒跑一轮，原先每轮都 new 未冻结的画刷，纯属 GC 压力。
    private static readonly Brush PowerCardBrush = Frozen(Color.FromRgb(27, 37, 44));
    private static readonly Brush PowerCardOverriddenBrush = Frozen(Color.FromRgb(51, 44, 32));
    private static readonly Brush SelectedPresetBrush = Frozen(Color.FromRgb(38, 67, 80));
    private static readonly Brush PositiveBadgeBrush = Frozen(Color.FromRgb(34, 57, 42));
    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    private void SelectButton(Button button, bool selected)
    {
        button.Background = selected ? SelectedPresetBrush : (Brush)FindResource("ChipBrush");
        button.BorderBrush = (Brush)FindResource(selected ? "AccentBrush" : "CardBorderBrush");
        button.BorderThickness = new Thickness(selected ? 2 : 1);
    }
    private void SetBadge(Border badge, TextBlock label, string text, bool? positive)
    {
        label.Text = text;
        label.Foreground = (Brush)FindResource(positive == true ? "GoodBrush" : positive == false ? "FgBrush" : "WarnBrush");
        badge.Background = positive == true ? PositiveBadgeBrush : (Brush)FindResource("ChipBrush");
    }
}
