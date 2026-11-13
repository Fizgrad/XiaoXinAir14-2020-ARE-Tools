using System.IO;
using System.Windows;
using System.Windows.Controls;
using FanController.Models;
using FanController.Services;

namespace FanController;

public partial class MainWindow
{
    private readonly StartupRegistrationService _startupRegistration = new(Path.Combine(AppContext.BaseDirectory, "FanController.exe"));
    private StartupRegistration? _startupState;
    private bool _startupSavePending, _startupActionsStarted;

    private void InitializeStartupControls()
    {
        ChkStartWithWindows.IsChecked = _settings.StartWithWindows;
        CbStartupPowerPreset.SelectedIndex = _settings.StartupPowerPreset.HasValue ? (int)_settings.StartupPowerPreset.Value + 1 : 0;
    }

    private async Task LoadStartupStateAsync()
    {
        try
        {
            _startupState = await Task.Run(_startupRegistration.Read);
            if (_shuttingDown) return;
            _updatingUiSettings = true;
            try { ChkStartWithWindows.IsChecked = _startupState.Enabled; }
            finally { _updatingUiSettings = false; }
            RenderStartupState();
        }
        catch (Exception ex) { if (!_shuttingDown) LblStartupState.Text = "启动项读取失败：" + ex.Message; }
        finally { if (!_shuttingDown) { StartupControls.IsEnabled = true; UpdateStartupDraft(); } }
    }

    private void RenderStartupState()
    {
        LblStartupState.Text = _startupState?.Enabled == true
            ? _startupState.MatchesApplication ? "已开启 · 登录后显示主窗口" : "已开启 · 保存后更新为当前程序路径"
            : "未开启";
    }

    private void StartupOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing && !_updatingUiSettings) UpdateStartupDraft();
    }

    private void UpdateStartupDraft()
    {
        if (_initializing) return;
        bool enabled = ChkStartWithWindows.IsChecked == true;
        CbStartupPowerPreset.IsEnabled = enabled;
        bool dirty = enabled != (_startupState?.Enabled ?? _settings.StartWithWindows)
            || SelectedStartupPreset() != _settings.StartupPowerPreset
            || (enabled && _startupState?.MatchesApplication == false);
        LblStartupDraft.Text = dirty ? "有未保存的更改" : "设置已保存";
        LblStartupDraft.Foreground = (System.Windows.Media.Brush)FindResource(dirty ? "WarnBrush" : "GoodBrush");
    }

    private PowerPreset? SelectedStartupPreset() => CbStartupPowerPreset.SelectedIndex is >= 1 and <= 3
        ? (PowerPreset)(CbStartupPowerPreset.SelectedIndex - 1) : null;

    private async void SaveStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_startupSavePending) return;
        _startupSavePending = true; StartupControls.IsEnabled = false;
        bool enabled = ChkStartWithWindows.IsChecked == true; var preset = SelectedStartupPreset();
        Feedback(FeedbackKind.Pending, "正在保存启动设置…", "请稍候。");
        try
        {
            var result = await _startupRegistration.SaveAsync(_settings, enabled, preset);
            if (_shuttingDown) return;
            if (result.Success)
            {
                _startupState = await Task.Run(_startupRegistration.Read); RenderStartupState();
                Feedback(FeedbackKind.Success, "启动设置已保存", result.Message + (enabled ? " · 功耗：" + StartupPresetName(preset) : ""));
            }
            else Feedback(FeedbackKind.Error, "启动设置保存失败", result.Message);
            UpdateStartupDraft();
        }
        catch (Exception ex) { if (!_shuttingDown) Feedback(FeedbackKind.Error, "启动设置保存失败", ex.Message); }
        finally { _startupSavePending = false; if (!_shuttingDown) StartupControls.IsEnabled = true; }
    }

    private static string StartupPresetName(PowerPreset? preset) => preset switch
    { PowerPreset.Quiet => "安静办公", PowerPreset.Balanced => "均衡", PowerPreset.Sustained => "持续负载", _ => "保持当前" };

    private async Task ApplyStartupActionsAsync()
    {
        if (_startupActionsStarted) return;
        _startupActionsStarted = true;
        _ = LoadStartupStateAsync();
        if ((Application.Current as App)?.IsStartupLaunch == true && _settings.StartWithWindows && _settings.StartupPowerPreset is { } preset)
            await ControlCenter.ApplyStartupPresetAsync(preset);
    }
}
